using System;

namespace GP02.Core
{
    /// <summary>
    /// 2粒子の制約を、複数のスレッドで同時に解ける組に分ける。
    ///
    /// 粒子の番号の範囲で布を区画に分け（粒子は空間の順に並べてあるので、区画は空間的にまとまる）、
    /// - 両方の粒子が同じ区画にある制約は、区画ごとに番号順で解く（区画どうしは粒子を共有しないので同時に解ける）
    /// - 区画をまたぐ制約は、粒子を共有しない組（色）に分け、色ごとに同時に解く
    ///
    /// 区画が1つなら、すべてを番号順に解く（1つのスレッドで順に解くのと同じ）。
    /// 番号順に解くと、直した結果が1回の反復の中で布の端から端まで伝わる。区画に分けるとこの伝わりが区画の境目で止まるので、
    /// 1回の反復は速くなるが、手を止めた後の仕上げ（伸び5%以下）には約3倍の反復が要った（1㎡・10cm引っ張り）。
    /// すべてを色分けした場合や、区画をまたぐ制約を番号順に解いた場合も、同様に遅かった。
    /// </summary>
    internal sealed class ConstraintGroups
    {
        /// <summary>並列で解くときの区画の数。スレッド数によらず固定なので、スレッド数を変えても結果は同じになる。</summary>
        public const int ParallelRegionCount = 16;

        public readonly int RegionCount;

        public readonly int[] Pairs;
        public readonly float[] RestLengths;
        public readonly float[] Lambda;

        /// <summary>区画をまたぐ制約の色の数。</summary>
        public readonly int BoundaryColorCount;

        /// <summary>最後の色が、1つのスレッドで順に解く寄せ集めか（64色で足りなかった制約）。</summary>
        public readonly bool HasSerialColor;

        // 制約ごとの組。0〜RegionCount-1 は区画の中、RegionCount + c は区画をまたぐ色 c
        readonly int[] _group;

        /// <summary>計算範囲の制約。組ごとにまとめ、組の中は番号順。</summary>
        public readonly int[] Active;

        /// <summary>組 g の制約は Active[GroupStart[g] .. GroupStart[g + 1])。</summary>
        public readonly int[] GroupStart;

        public int ActiveCount { get; private set; }

        /// <param name="regionCount">区画の数。1 なら、すべてを番号順に解く</param>
        public ConstraintGroups(int particleCount, int[] pairs, float[] restLengths, int regionCount)
        {
            if (regionCount < 1 || regionCount > PhaseRunner.MaxChunks) throw new ArgumentOutOfRangeException(nameof(regionCount));
            RegionCount = regionCount;
            Pairs = pairs;
            RestLengths = restLengths;
            int count = restLengths.Length;
            Lambda = new float[count];
            Active = new int[count];
            _group = new int[count];

            int boundaryCount = 0;
            for (int k = 0; k < count; k++)
            {
                int a = RegionOf(pairs[2 * k], particleCount, regionCount), b = RegionOf(pairs[2 * k + 1], particleCount, regionCount);
                _group[k] = a == b ? a : -1;
                if (a != b) boundaryCount++;
            }

            var boundaryPairs = new int[2 * boundaryCount];
            int index = 0;
            for (int k = 0; k < count; k++)
            {
                if (_group[k] >= 0) continue;
                boundaryPairs[2 * index] = pairs[2 * k];
                boundaryPairs[2 * index + 1] = pairs[2 * k + 1];
                index++;
            }
            int[] colors = ConstraintColoring.Color(particleCount, boundaryPairs, out BoundaryColorCount, out HasSerialColor);
            index = 0;
            for (int k = 0; k < count; k++)
            {
                if (_group[k] < 0) _group[k] = RegionCount + colors[index++];
            }

            GroupStart = new int[RegionCount + BoundaryColorCount + 1];
        }

        static int RegionOf(int particle, int particleCount, int regionCount) => (int)((long)particle * regionCount / particleCount);

        /// <summary>少なくとも一方の粒子が動ける制約だけを、組ごとにまとめて集める。</summary>
        public void CollectActive(float[] inverseMass)
        {
            int[] start = GroupStart;
            Array.Clear(start, 0, start.Length);
            for (int k = 0; k < _group.Length; k++)
            {
                if (IsMovable(k, inverseMass)) start[_group[k] + 1]++;
            }
            for (int g = 0; g + 1 < start.Length; g++) start[g + 1] += start[g];

            // start[g] を書き込み位置として使い、最後に元に戻す
            for (int k = 0; k < _group.Length; k++)
            {
                if (IsMovable(k, inverseMass)) Active[start[_group[k]]++] = k;
            }
            for (int g = start.Length - 1; g > 0; g--) start[g] = start[g - 1];
            start[0] = 0;
            ActiveCount = start[start.Length - 1];
        }

        bool IsMovable(int k, float[] inverseMass) => inverseMass[Pairs[2 * k]] > 0f || inverseMass[Pairs[2 * k + 1]] > 0f;

        public void ResetLambda()
        {
            for (int a = 0; a < ActiveCount; a++) Lambda[Active[a]] = 0f;
        }

        /// <summary>この制約を解く段階の数（区画の中の1段階＋色の数）。</summary>
        public int StepCount => 1 + BoundaryColorCount;

        /// <summary>段階 step（0 = 区画の中、1以上 = 色 step-1）の、組の範囲。</summary>
        public void StepRange(int step, out int begin, out int end)
        {
            int group = step == 0 ? 0 : RegionCount + step - 1;
            int lastGroup = step == 0 ? RegionCount : group + 1;
            begin = GroupStart[group];
            end = GroupStart[lastGroup];
        }
    }
}
