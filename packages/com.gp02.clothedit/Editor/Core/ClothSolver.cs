using System;
using System.Diagnostics;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 「つまんで引っ張る」編集のための粒子計算。
    ///
    /// 1回の Solve で次の2段階を行う。
    /// 1. つまんだ点の移動量を、メッシュに沿った距離に応じて周りへ配る（影響の広がりを決める）
    /// 2. XPBD（Extended Position Based Dynamics）の制約を繰り返し解き、布の伸びと曲げを直す
    ///
    /// 時間は進めない（重力も速度も持たない）ので、時間刻みは 1 とみなし、コンプライアンスは値をそのまま使う。
    ///
    /// 粗い段（<see cref="ClothHierarchy"/>）を渡すと、細かい粒子の計算の前に、粗い段から順に形を決める。
    /// 長さの補正が少ない反復で全体に行き渡るので、伸びが残りにくくなる。
    ///
    /// つまんでいる間は、つまんだ点とつながった部品（島）だけを計算する。
    /// <see cref="ActiveMargin"/> を有限にすると、さらに周りだけに絞れるが、
    /// 外側を固定するため、大きく引っ張ると境目が伸びる（布を周りから引き寄せられないため）。
    /// </summary>
    public sealed class ClothSolver : PhaseRunner.IWork
    {
        // 1つの小片で解く、区画をまたぐ制約・テザーの数の目安。小さすぎると数え上げの手間の方が大きくなる
        const int ConstraintChunk = 256;
        const int ParticleChunk = 512;

        readonly ClothModel _model;
        readonly ClothHierarchy _hierarchy;
        readonly Vector3[] _levelStart;   // 粗い段を解き始めたときの位置（その段の粒子だけ使う）
        readonly MeshGraph _graph;
        // 内部では、すべての座標が 0.5m 以上になるよう原点をずらして計算する。
        // 座標 0 付近では、つまんだ動きが伝わるうちに値が指数的に小さくなって非正規化数になり、計算が2倍以上遅くなるため。
        // 0.5 以上の数どうしの差は 0 か一定以上の大きさになるので、非正規化数は出ない。
        readonly Vector3 _origin;
        readonly Vector3[] _positions;        // 内部の座標（ワールド座標 - _origin）
        readonly Vector3[] _worldPositions;   // 外に見せるワールド座標
        readonly bool[] _pinned;
        // 制約の組。1スレッドのときは番号順（区画1つ）、複数スレッドのときは区画に分けた組を使う
        readonly ConstraintGroups _stretchSerial;
        readonly ConstraintGroups _bendSerial;
        readonly ConstraintGroups _stretchParallel;
        readonly ConstraintGroups _bendParallel;
        ConstraintGroups _stretch;
        ConstraintGroups _bend;

        // テザー：つまんだ点・ピンから、メッシュに沿った距離より遠くへは離れない。
        // 少ない反復回数でも布が伸びないようにするための制約（Long Range Attachments）。
        readonly float[] _grabDistance;
        readonly float[] _pinDistance;
        readonly int[] _nearestPin;
        bool _pinDistancesDirty = true;

        // つまんでいる範囲。中心の粒子から、メッシュに沿ってブラシ半径以内の粒子
        readonly bool[] _inBrush;
        int _grabbed = -1;
        float _brushRadius;
        Vector3 _grabTarget;
        Vector3 _grabOrigin;      // つまみ始めたときの中心の位置
        Vector3 _appliedOffset;   // これまでに範囲内へ加えた移動量

        // 計算する範囲。固定の粒子（ピン、つまんだ範囲、計算範囲の外）は逆質量 0
        readonly float[] _inverseMass;
        readonly int[] _activeParticles;   // 計算範囲にある、ピン留めでない粒子
        PhaseRunner _runner;
        readonly PhaseRunner _serialRunner;
        PhaseRunner _parallelRunner;
        Vector3 _grabbedSnapshot;   // テザーを解く間の、つまんだ点の位置
        int _activeParticleCount;
        bool _activeDirty = true;
        float _activePull;   // 計算範囲を決めたときの、つまみ始めからの移動距離
        float _influenceRadius = 0.3f;
        float _activeMargin = float.PositiveInfinity;

        /// <summary>伸びやすさ。0 で伸びない。</summary>
        public float StretchCompliance { get; set; } = 0f;

        /// <summary>粗い段1段あたりの反復回数。</summary>
        public int CoarseIterations { get; set; } = 10;

        /// <summary>曲がりやすさ。大きいほど柔らかく折れ曲がる。</summary>
        public float BendCompliance { get; set; } = 0.1f;

        /// <summary>
        /// つまんでいる範囲の外側へ、動きがメッシュに沿ってどこまで広がるか（メートル）。
        /// この外側は、布の長さを保つために必要な分しか動かない。
        /// </summary>
        public float InfluenceRadius
        {
            get => _influenceRadius;
            set
            {
                if (value < 0f) throw new ArgumentOutOfRangeException(nameof(value));
                _influenceRadius = value;
                _activeDirty = true;
            }
        }

        /// <summary>
        /// 影響範囲のさらに外側で、計算に含める余白（メートル）。既定は無限大（つまんだ部品全体を計算する）。
        /// 有限にすると、ブラシ半径＋影響範囲＋余白＋つまみ始めからの移動距離 より外を固定して速くする。
        /// ただし外側を固定すると布を周りから引き寄せられないので、大きく引っ張ると境目が伸びる。
        /// </summary>
        public float ActiveMargin
        {
            get => _activeMargin;
            set
            {
                if (value < 0f) throw new ArgumentOutOfRangeException(nameof(value));
                _activeMargin = value;
                _activeDirty = true;
            }
        }

        /// <summary>直前の Solve で計算した粒子の数（ピン留めを除く）。</summary>
        public int ActiveParticleCount => _activeParticleCount;

        /// <param name="hierarchy">粗い段。null なら細かい粒子だけで解く</param>
        public ClothSolver(ClothModel model, ClothHierarchy hierarchy = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _hierarchy = hierarchy;
            int n = model.ParticleCount;
            if (hierarchy != null) _levelStart = new Vector3[n];
            _graph = new MeshGraph(n, model.StretchPairs, model.StretchRestLengths);
            Vector3[] rest = model.RestPositions;
            Vector3 min = n > 0 ? rest[0] : Vector3.Zero;
            for (int i = 1; i < n; i++) min = Vector3.Min(min, rest[i]);
            _origin = min - new Vector3(0.5f);
            _positions = new Vector3[n];
            for (int i = 0; i < n; i++) _positions[i] = rest[i] - _origin;
            _worldPositions = (Vector3[])rest.Clone();
            _pinned = new bool[n];
            _stretchSerial = new ConstraintGroups(n, model.StretchPairs, model.StretchRestLengths, 1);
            _bendSerial = new ConstraintGroups(n, model.BendPairs, model.BendRestLengths, 1);
            _stretchParallel = new ConstraintGroups(n, model.StretchPairs, model.StretchRestLengths, ConstraintGroups.ParallelRegionCount);
            _bendParallel = new ConstraintGroups(n, model.BendPairs, model.BendRestLengths, ConstraintGroups.ParallelRegionCount);
            _grabDistance = new float[n];
            _pinDistance = new float[n];
            _nearestPin = new int[n];
            _inBrush = new bool[n];
            _inverseMass = new float[n];
            _activeParticles = new int[n];
            _serialRunner = new PhaseRunner(1);
            ThreadCount = 1;
        }

        /// <summary>
        /// 計算に使うスレッド数の既定値（論理コア数の半分。ただし8まで）。
        /// 段階の切り替えでは全スレッドがそろうのを待つので、1つでも他の処理に割り込まれると全員が待たされる。
        /// Unity 自身も描画などでスレッドを使うので、すべてのコアは使わない。
        /// </summary>
        public static int DefaultThreadCount => Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));

        /// <summary>
        /// 計算に使うスレッド数（呼び出したスレッドを含む）。既定は 1。
        /// 1 なら、すべての制約を番号順に解く。2 以上なら、布を区画に分けて並列に解く。1回の反復は速いが、
        /// 仕上げには反復が多く要る（詳しくは ConstraintGroups）。そのため、ドラッグ中は複数スレッド、手を止めた後の仕上げは 1 が向いている。
        /// 2 以上なら、スレッド数によらず結果はまったく同じになる。
        /// </summary>
        public int ThreadCount
        {
            get => _runner.ThreadCount;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                if (_runner != null && value == _runner.ThreadCount) return;
                if (value == 1)
                {
                    _runner = _serialRunner;
                }
                else
                {
                    // ドラッグのたびに切り替えるので、複数スレッド用は使い回す
                    if (_parallelRunner == null || _parallelRunner.ThreadCount != value) _parallelRunner = new PhaseRunner(value);
                    _runner = _parallelRunner;
                }
                ConstraintGroups stretch = value == 1 ? _stretchSerial : _stretchParallel;
                if (stretch != _stretch)
                {
                    _stretch = stretch;
                    _bend = value == 1 ? _bendSerial : _bendParallel;
                    _activeDirty = true;
                }
            }
        }

        public ClothModel Model => _model;

        /// <summary>現在の粒子の位置。</summary>
        public ReadOnlySpan<Vector3> Positions => _worldPositions;

        /// <summary>つまんでいる範囲の中心の粒子。つまんでいなければ -1。</summary>
        public int GrabbedParticle => _grabbed;

        /// <summary>つまんでいる範囲に含まれるか。</summary>
        public bool IsGrabbed(int particle) => _grabbed >= 0 && _inBrush[particle];

        public bool IsPinned(int particle) => _pinned[particle];

        /// <summary>ピン留めした粒子は動かない。</summary>
        public void SetPinned(int particle, bool pinned)
        {
            if (_pinned[particle] == pinned) return;
            _pinned[particle] = pinned;
            _pinDistancesDirty = true;
            _activeDirty = true;
        }

        /// <summary>
        /// 粒子をつまみ、指定位置へ動かす。実際に動くのは次の Solve。
        /// </summary>
        /// <param name="particle">つまむ範囲の中心</param>
        /// <param name="target">中心の粒子の移動先。範囲内の粒子は、中心と同じだけ平行移動する</param>
        /// <param name="brushRadius">つまむ範囲（メッシュに沿った距離、メートル）。0 なら中心の1点だけ</param>
        public void Grab(int particle, Vector3 target, float brushRadius = 0f)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            if (brushRadius < 0f) throw new ArgumentOutOfRangeException(nameof(brushRadius));
            if (particle != _grabbed || brushRadius != _brushRadius)
            {
                // メッシュに沿った距離で選ぶので、近くにあってもつながっていない布（重ね着など）はつままない
                _graph.ShortestDistances(new[] { particle }, _grabDistance, null);
                for (int i = 0; i < _inBrush.Length; i++) _inBrush[i] = _grabDistance[i] <= brushRadius;
                _grabOrigin = _positions[particle];
                _appliedOffset = Vector3.Zero;
                _activePull = 0f;
                _activeDirty = true;
            }
            _grabbed = particle;
            _brushRadius = brushRadius;
            _grabTarget = target - _origin;
        }

        public void Release()
        {
            _grabbed = -1;
            _activeDirty = true;
        }

        /// <summary>元の形に戻す。ピン留めは保持する。</summary>
        public void ResetToRest()
        {
            SetInternalPositions(_model.RestPositions);
            _grabbed = -1;
            _activeDirty = true;
        }

        /// <summary>
        /// 粒子の位置を丸ごと置き換える（「1つ戻す」用）。つまんでいる状態は解除する。
        /// XPBD の lambda は Solve のたびに 0 に戻すので、ここでは何もしなくてよい。
        /// </summary>
        public void SetPositions(ReadOnlySpan<Vector3> positions)
        {
            if (positions.Length != _positions.Length) throw new ArgumentException("粒子数が一致しません。", nameof(positions));
            SetInternalPositions(positions);
            _grabbed = -1;
            _activeDirty = true;
        }

        public void CopyPositionsTo(Vector3[] destination)
        {
            Array.Copy(_worldPositions, destination, _worldPositions.Length);
        }

        void SetInternalPositions(ReadOnlySpan<Vector3> world)
        {
            world.CopyTo(_worldPositions);
            for (int i = 0; i < _positions.Length; i++) _positions[i] = world[i] - _origin;
        }

        /// <summary>指定位置に一番近い粒子を返す。</summary>
        public int FindNearestParticle(Vector3 point)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _positions.Length; i++)
            {
                float sq = Vector3.DistanceSquared(_worldPositions[i], point);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// 計算範囲にある伸びの制約のうち、最も大きい伸び（0.1 なら 10%）。
        /// </summary>
        public float MeasureMaxStretch()
        {
            if (_activeDirty)
            {
                UpdateActiveRegion();
                _activeDirty = false;
            }

            int[] pairs = _model.StretchPairs;
            float[] rest = _model.StretchRestLengths;
            float max = 0f;
            for (int a = 0; a < _stretch.ActiveCount; a++)
            {
                int k = _stretch.Active[a];
                // 内部の座標は原点をずらした分の丸め誤差を含むので、外に見せるワールド座標で測る
                float length = Vector3.Distance(_worldPositions[pairs[2 * k]], _worldPositions[pairs[2 * k + 1]]);
                max = MathF.Max(max, length / rest[k] - 1f);
            }
            return max;
        }

        /// <summary>
        /// 手を止めたときの仕上げ。今の状態のまま（つまんでいれば、つまんだまま）、
        /// 伸びが tolerance 以下になるか、反復回数が maxIterations に達するまで解き続ける。
        /// つまんだまま解くのは、押さえを外すとつまんでいた範囲が戻り、編集した形が変わってしまうため。
        /// </summary>
        /// <param name="iterationsDone">実際に行った反復回数（出力）</param>
        /// <returns>仕上げた後の最大の伸び</returns>
        public float Settle(float tolerance, int maxIterations, out int iterationsDone, int iterationsPerStep = 40)
        {
            if (iterationsPerStep <= 0) throw new ArgumentOutOfRangeException(nameof(iterationsPerStep));
            iterationsDone = 0;
            float stretch = MeasureMaxStretch();
            while (stretch > tolerance && iterationsDone < maxIterations)
            {
                int step = Math.Min(iterationsPerStep, maxIterations - iterationsDone);
                Solve(step);
                iterationsDone += step;
                stretch = MeasureMaxStretch();
            }
            return stretch;
        }

        /// <summary>
        /// 指定した時間（ミリ秒）だけ解く。Unity側で、1コマの時間予算に収めるために使う。
        /// 最低でも1回は解く。
        /// </summary>
        /// <returns>行った反復回数</returns>
        public int SolveFor(double milliseconds, int iterationsPerStep = 5)
        {
            if (iterationsPerStep <= 0) throw new ArgumentOutOfRangeException(nameof(iterationsPerStep));
            var timer = Stopwatch.StartNew();
            int done = 0;
            do
            {
                Solve(iterationsPerStep);
                done += iterationsPerStep;
            }
            while (timer.Elapsed.TotalMilliseconds < milliseconds);
            return done;
        }

        /// <summary>
        /// つまんだ点を目標へ動かし、制約を解く。
        /// 粗い段があれば、先に各段を <see cref="CoarseIterations"/> 回ずつ解いてから、細かい粒子を iterations 回解く。
        /// </summary>
        public void Solve(int iterations)
        {
            if (_pinDistancesDirty)
            {
                UpdatePinDistances();
                _pinDistancesDirty = false;
            }

            // 大きく引っ張るほど、遠くの布まで引き寄せられる。引っ張った距離に応じて計算範囲を広げる
            if (_grabbed >= 0)
            {
                float pull = Vector3.Distance(_grabTarget, _grabOrigin);
                if (pull > _activePull + 0.5f * _activeMargin)
                {
                    _activePull = pull;
                    _activeDirty = true;
                }
            }

            if (_activeDirty)
            {
                UpdateActiveRegion();
                _activeDirty = false;
            }

            if (_grabbed >= 0) SpreadGrabMotion();
            if (_hierarchy != null && iterations > 0) SolveCoarseLevels();

            _stretch.ResetLambda();
            _bend.ResetLambda();

            // 1回の反復は「曲げ（区画の中 → 区画をまたぐ各色）→ 伸び（同じ）→ テザー」の段階に分かれる。
            // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
            if (iterations > 0) _runner.Run(this, iterations * StepsPerIteration);

            // 動くのは計算範囲の粒子だけ（ピン留めと範囲外は動かない）
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                _worldPositions[i] = _positions[i] + _origin;
            }
        }

        int StepsPerIteration => _bend.StepCount + _stretch.StepCount + 1;

        // 段階を、曲げ・伸びのどちらの何番目の段階か、またはテザー（groups = null）かに分ける
        void Decode(int phase, out ConstraintGroups groups, out int step)
        {
            step = phase % StepsPerIteration;
            if (step < _bend.StepCount)
            {
                groups = _bend;
                return;
            }
            step -= _bend.StepCount;
            if (step < _stretch.StepCount)
            {
                groups = _stretch;
                return;
            }
            groups = null;
        }

        int PhaseRunner.IWork.ChunkCount(int phase)
        {
            Decode(phase, out ConstraintGroups groups, out int step);
            if (groups == null) return _activeParticleCount / ParticleChunk;
            if (step == 0) return groups.RegionCount;
            if (groups.HasSerialColor && step == groups.StepCount - 1) return 1;
            groups.StepRange(step, out int begin, out int end);
            return (end - begin) / ConstraintChunk;
        }

        void PhaseRunner.IWork.EnterPhase(int phase)
        {
            // テザーは粒子ごとに独立に解くが、つまんだ点自身もピンのテザーで動くことがある。
            // 同時に読み書きしないよう、テザーの段階の間は、始まる直前の位置を使う
            if (phase % StepsPerIteration == StepsPerIteration - 1 && _grabbed >= 0) _grabbedSnapshot = _positions[_grabbed];
        }

        void PhaseRunner.IWork.RunChunk(int phase, int chunk)
        {
            Decode(phase, out ConstraintGroups groups, out int step);
            int chunkCount = ((PhaseRunner.IWork)this).ChunkCount(phase);
            chunkCount = Math.Max(1, Math.Min(PhaseRunner.MaxChunks, chunkCount));
            if (groups == null)
            {
                SolveTethers(Split(0, _activeParticleCount, chunk, chunkCount), Split(0, _activeParticleCount, chunk + 1, chunkCount));
                return;
            }

            float compliance = groups == _bend ? BendCompliance : StretchCompliance;
            if (step == 0)
            {
                // 区画の中の制約を、番号順に解く
                SolveRange(groups, compliance, groups.GroupStart[chunk], groups.GroupStart[chunk + 1]);
                return;
            }
            groups.StepRange(step, out int begin, out int end);
            SolveRange(groups, compliance, Split(begin, end, chunk, chunkCount), Split(begin, end, chunk + 1, chunkCount));
        }

        // [begin, end) を chunkCount 等分したときの、chunk 番目の始まり
        static int Split(int begin, int end, int chunk, int chunkCount) => begin + (int)((long)(end - begin) * chunk / chunkCount);

        void SolveRange(ConstraintGroups groups, float compliance, int begin, int end)
        {
            int[] active = groups.Active;
            int[] pairs = groups.Pairs;
            float[] rest = groups.RestLengths;
            float[] lambda = groups.Lambda;
            for (int a = begin; a < end; a++)
            {
                int k = active[a];
                SolveDistance(pairs[2 * k], pairs[2 * k + 1], rest[k], compliance, ref lambda[k]);
            }
        }

        void SolveCoarseLevels()
        {
            foreach (var level in _hierarchy.Levels)
            {
                foreach (int m in level.Members) _levelStart[m] = _positions[m];

                int[] pairs = level.StretchPairs;
                float[] rest = level.RestLengths;
                for (int it = 0; it < CoarseIterations; it++)
                {
                    for (int k = 0; k < rest.Length; k++) SolveStretchOnly(pairs[2 * k], pairs[2 * k + 1], rest[k]);
                }

                // この段の粒子の動きを、この段に含まれない粒子へ配る
                for (int a = 0; a < _activeParticleCount; a++)
                {
                    int i = _activeParticles[a];
                    if (_inverseMass[i] == 0f) continue;
                    int end = level.BindOffsets[i + 1];
                    int begin = level.BindOffsets[i];
                    if (begin == end) continue;
                    Vector3 delta = Vector3.Zero;
                    for (int k = begin; k < end; k++)
                    {
                        int parent = level.BindParents[k];
                        delta += (_positions[parent] - _levelStart[parent]) * level.BindWeights[k];
                    }
                    _positions[i] += delta;
                }
            }
        }

        // 伸びているときだけ縮める（縮んでいるのは許す）
        void SolveStretchOnly(int i, int j, float restLength)
        {
            float wi = _inverseMass[i];
            float wj = _inverseMass[j];
            float w = wi + wj;
            if (w == 0f) return;

            Vector3 d = _positions[i] - _positions[j];
            float length = d.Length();
            if (length <= restLength) return;

            Vector3 correction = d / length * (-(length - restLength) / w);
            _positions[i] += wi * correction;
            _positions[j] -= wj * correction;
        }

        void UpdateActiveRegion()
        {
            float limit = _brushRadius + _influenceRadius + _activeMargin + _activePull;
            int n = _positions.Length;
            _activeParticleCount = 0;
            for (int i = 0; i < n; i++)
            {
                // つながっていない部品（距離が無限大）は計算しない
                bool active = _grabbed < 0 || (!float.IsInfinity(_grabDistance[i]) && _grabDistance[i] <= limit);
                bool fixedParticle = _pinned[i] || IsGrabbed(i) || !active;
                _inverseMass[i] = fixedParticle ? 0f : 1f;
                // つまんだ範囲もテザーの対象にするので、ピン留めでなければ一覧に入れる
                if (active && !_pinned[i]) _activeParticles[_activeParticleCount++] = i;
            }

            _stretch.CollectActive(_inverseMass);
            _bend.CollectActive(_inverseMass);
        }


        void SpreadGrabMotion()
        {
            // 現在位置ではなく、つまみ始めからの移動量で考える。
            // 中心がピン留めされていても、同じ移動を何度も足さないようにするため。
            Vector3 delta = _grabTarget - _grabOrigin - _appliedOffset;
            if (delta.LengthSquared() == 0f) return;
            _appliedOffset += delta;

            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                _positions[i] += delta * Falloff(_grabDistance[i]);
            }
        }

        // 1（つまんでいる範囲）〜 0（影響範囲の外）
        float Falloff(float distance)
        {
            if (distance <= _brushRadius) return 1f;
            if (_influenceRadius <= 0f) return 0f;
            float t = (distance - _brushRadius) / _influenceRadius;
            if (t >= 1f) return 0f;
            float s = 1f - t;
            return s * s * (3f - 2f * s);
        }

        void UpdatePinDistances()
        {
            int count = 0;
            for (int i = 0; i < _pinned.Length; i++) if (_pinned[i]) count++;
            var sources = new int[count];
            count = 0;
            for (int i = 0; i < _pinned.Length; i++) if (_pinned[i]) sources[count++] = i;
            _graph.ShortestDistances(sources, _pinDistance, _nearestPin);
        }

        void SolveTethers(int begin, int end)
        {
            for (int a = begin; a < end; a++)
            {
                int i = _activeParticles[a];
                if (_grabbed >= 0 && !_inBrush[i]) SolveTether(i, _grabbedSnapshot, _grabDistance[i]);

                // つまんだ範囲も、ピンから布の長さ以上は離れない。
                // 布の長さが足りない方向へ引っ張ると、本物の布のように目標の手前で止まる（持ち上がる）。
                int pin = _nearestPin[i];
                if (pin >= 0) SolveTether(i, _positions[pin], _pinDistance[i]);
            }
        }

        void SolveTether(int i, Vector3 origin, float maxLength)
        {
            if (float.IsInfinity(maxLength)) return;
            Vector3 d = _positions[i] - origin;
            float length = d.Length();
            if (length <= maxLength) return;
            _positions[i] = origin + d / length * maxLength;
        }

        void SolveDistance(int i, int j, float restLength, float alpha, ref float lambda)
        {
            float wi = InverseMass(i);
            float wj = InverseMass(j);
            float w = wi + wj;
            if (w == 0f) return;

            Vector3 d = _positions[i] - _positions[j];
            float length = d.Length();
            if (length < 1e-12f) return;

            float c = length - restLength;
            float deltaLambda = (-c - alpha * lambda) / (w + alpha);
            lambda += deltaLambda;

            Vector3 correction = d / length * deltaLambda;
            _positions[i] += wi * correction;
            _positions[j] -= wj * correction;
        }

        float InverseMass(int particle) => _inverseMass[particle];
    }
}
