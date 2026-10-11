using System;
using System.Threading;

namespace GP02.Core
{
    /// <summary>
    /// 一連の段階を、複数のスレッドで順番に処理する。各段階は互いに独立な小片に分かれ、どのスレッドがどの小片を処理してもよい。
    ///
    /// 1コマに数百回段階が切り替わるので、Parallel.For（呼ぶたびにスレッドを起こして待つ）では待ち合わせの手間が計算より大きくなる。
    /// ここでは、Run の間だけスレッドプールのスレッドを借り、段階の切り替えは共有の数え上げで行う。
    /// 呼び出したスレッドも処理に加わるので、借りたスレッドが来なくても必ず終わる。
    /// Run を抜けた後はスレッドを持ち続けない（Unity でスクリプトを再読み込みするとき、残ったスレッドが邪魔をしないように）。
    ///
    /// スレッド s は、小片の列の s / スレッド数 の位置から取り始める。どの段階でも同じ範囲のデータを同じスレッドが扱うので、
    /// データがコアのキャッシュに残りやすい（取り方をばらばらにすると、4スレッドでも1.2倍しか速くならなかった）。
    /// </summary>
    internal sealed class PhaseRunner
    {
        /// <summary>1つの段階の小片の数の上限。</summary>
        public const int MaxChunks = 64;

        public interface IWork
        {
            /// <summary>
            /// 段階の小片の数（1〜<see cref="MaxChunks"/>）。Run の最初に、呼び出したスレッドがすべての段階について呼ぶ。
            /// 番号が近い小片は、近いデータを扱うように分けること。
            /// </summary>
            int ChunkCount(int phase);

            void RunChunk(int phase, int chunk);

            /// <summary>段階が始まる直前に、1つのスレッドだけが呼ぶ（前の段階はすべて終わっている）。</summary>
            void EnterPhase(int phase);
        }

        readonly int _helperCount;
        readonly WaitCallback _helperEntry;
        IWork _work;
        int _phaseCount;
        int[] _chunks = new int[0];
        int[] _claimed = new int[0];   // 段階 p の小片 c を誰かが取ったか（[p * MaxChunks + c]）
        int[] _done = new int[0];
        int _phase;
        int _running;
        int _activeHelpers;
        int _nextSlot;

        /// <param name="threadCount">呼び出したスレッドを含めたスレッド数（1なら呼び出したスレッドだけで処理する）</param>
        public PhaseRunner(int threadCount)
        {
            if (threadCount < 1) throw new ArgumentOutOfRangeException(nameof(threadCount));
            _helperCount = threadCount - 1;
            _helperEntry = HelperEntry;
        }

        public int ThreadCount => _helperCount + 1;

        public void Run(IWork work, int phaseCount)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (phaseCount <= 0) return;
            if (_chunks.Length < phaseCount)
            {
                // 反復回数が増えたときだけ確保する（毎コマは確保しない）
                int size = Math.Max(phaseCount, 2 * _chunks.Length);
                _chunks = new int[size];
                _claimed = new int[size * MaxChunks];
                _done = new int[size];
            }

            bool worthHelping = false;
            for (int p = 0; p < phaseCount; p++)
            {
                int chunks = Math.Max(1, Math.Min(MaxChunks, work.ChunkCount(p)));
                _chunks[p] = chunks;
                _done[p] = 0;
                Array.Clear(_claimed, p * MaxChunks, chunks);
                if (chunks > 1) worthHelping = true;
            }
            _work = work;
            _phaseCount = phaseCount;
            _nextSlot = 0;
            work.EnterPhase(0);
            _phase = 0;
            Volatile.Write(ref _running, 1);

            if (worthHelping)
            {
                for (int h = 0; h < _helperCount; h++) ThreadPool.UnsafeQueueUserWorkItem(_helperEntry, null);
            }

            Participate(0);

            // 借りたスレッドが全員抜けるまで待つ。次の Run が数え上げを初期化するとき、誰も触っていないようにするため
            Volatile.Write(ref _running, 0);
            int spins = 0;
            while (Volatile.Read(ref _activeHelpers) != 0) Wait(ref spins);
            _work = null;
        }

        void HelperEntry(object state)
        {
            Interlocked.Increment(ref _activeHelpers);
            // 数えてから確かめる。Run が終わった後に来たスレッドは、何も触らずに帰る
            if (Volatile.Read(ref _running) == 1) Participate(Interlocked.Increment(ref _nextSlot) % ThreadCount);
            Interlocked.Decrement(ref _activeHelpers);
        }

        void Participate(int slot)
        {
            int spins = 0;
            while (true)
            {
                int p = Volatile.Read(ref _phase);
                if (p >= _phaseCount) return;

                int chunks = _chunks[p];
                int first = slot * chunks / ThreadCount;
                int claimed = -1;
                for (int k = 0; k < chunks; k++)
                {
                    int c = first + k;
                    if (c >= chunks) c -= chunks;
                    if (Interlocked.Exchange(ref _claimed[p * MaxChunks + c], 1) == 0)
                    {
                        claimed = c;
                        break;
                    }
                }

                if (claimed >= 0)
                {
                    _work.RunChunk(p, claimed);
                    if (Interlocked.Increment(ref _done[p]) == chunks)
                    {
                        // この段階の最後の小片を終えたスレッドが、次の段階を始める
                        int nextPhase = p + 1;
                        if (nextPhase < _phaseCount) _work.EnterPhase(nextPhase);
                        Volatile.Write(ref _phase, nextPhase);
                    }
                    spins = 0;
                }
                else
                {
                    // 小片はすべて誰かが処理中。この段階が終わるのを待つ
                    while (Volatile.Read(ref _phase) == p) Wait(ref spins);
                }
            }
        }

        // 段階の切り替えは数マイクロ秒で起きるので、まずは空回りで待つ。
        // 長く待つときは他のスレッドに譲る（Thread.Sleep(1) は1ミリ秒以上眠るので使わない）
        static void Wait(ref int spins)
        {
            if (spins < 200) Thread.SpinWait(20);
            else Thread.Yield();
            spins++;
        }
    }
}
