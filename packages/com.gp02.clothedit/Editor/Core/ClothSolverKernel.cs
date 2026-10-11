using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// <see cref="ClothSolver"/> の重い計算を肩代わりする部品。
    /// 差し替えなければソルバー自身の C# が計算する。Burst など、環境によって速くできるものを後から足すための口。
    /// 実装は、ソルバーの C# と同じ式・同じ順序で計算し、結果がほぼ一致するようにすること。
    /// </summary>
    public interface IClothSolverKernel
    {
        /// <summary>表示用の名前（例: "Burst"）。</summary>
        string Name { get; }

        /// <summary>高速化が実際に効いているか。名前だけ登録されていて、実際には遅い経路で動いている場合は false。</summary>
        bool IsAccelerated { get; }

        /// <summary>細かい粒子の反復（曲げの制約 → 伸びの制約 → テザー）を iterations 回行う。</summary>
        void Iterate(in ClothSolverIterationData data, int iterations);

        /// <summary>粗い段1段分（<see cref="ClothSolverCoarseLevelData.Iterations"/> 回の伸びだけの制約 → この段に含まれない粒子への配分）を行う。</summary>
        void SolveCoarseLevel(in ClothSolverCoarseLevelData data);
    }

    /// <summary>
    /// <see cref="IClothSolverKernel.Iterate"/> に渡す、ソルバー内部の配列への参照と数。コピーはしない。
    /// 配列はソルバーの持ち物で、<see cref="Positions"/> と λ の配列は書き換えてよい。それ以外は読むだけにすること。
    /// </summary>
    public readonly struct ClothSolverIterationData
    {
        /// <summary>内部の座標（ワールド座標 - 原点のずらし）。粒子ごとに x, y, z の順に float 3つ分。</summary>
        public readonly Vector3[] Positions;

        /// <summary>粒子ごとの逆質量。動かない粒子（ピン留め、つまんだ範囲、計算範囲の外）は 0、それ以外は 1。</summary>
        public readonly float[] InverseMass;

        /// <summary>曲げの制約の粒子ペア（制約ごとに2つ）と、元の長さ。全制約が対象で、解くのは ActiveBend にある制約だけ。</summary>
        public readonly int[] BendPairs;
        public readonly float[] BendRestLengths;
        public readonly float[] BendLambda;

        /// <summary>解く曲げの制約の番号（先頭から <see cref="ActiveBendCount"/> 個が有効）。</summary>
        public readonly int[] ActiveBend;
        public readonly int ActiveBendCount;
        public readonly float BendCompliance;

        public readonly int[] StretchPairs;
        public readonly float[] StretchRestLengths;
        public readonly float[] StretchLambda;
        public readonly int[] ActiveStretch;
        public readonly int ActiveStretchCount;
        public readonly float StretchCompliance;

        /// <summary>計算範囲にある、ピン留めでない粒子（先頭から <see cref="ActiveParticleCount"/> 個が有効）。テザーの対象。</summary>
        public readonly int[] ActiveParticles;
        public readonly int ActiveParticleCount;

        /// <summary>つまんでいる範囲の中心の粒子。つまんでいなければ -1。</summary>
        public readonly int Grabbed;

        /// <summary>つまんでいる範囲に含まれるか（粒子ごと、1バイトの bool）。</summary>
        public readonly bool[] InBrush;

        /// <summary>つまんだ中心から、メッシュに沿った距離（つながっていなければ無限大）。</summary>
        public readonly float[] GrabDistance;

        /// <summary>一番近いピンの粒子（なければ -1）と、そこまでのメッシュに沿った距離。</summary>
        public readonly int[] NearestPin;
        public readonly float[] PinDistance;

        internal ClothSolverIterationData(Vector3[] positions, float[] inverseMass,
            int[] bendPairs, float[] bendRestLengths, float[] bendLambda, int[] activeBend, int activeBendCount, float bendCompliance,
            int[] stretchPairs, float[] stretchRestLengths, float[] stretchLambda, int[] activeStretch, int activeStretchCount, float stretchCompliance,
            int[] activeParticles, int activeParticleCount, int grabbed, bool[] inBrush, float[] grabDistance, int[] nearestPin, float[] pinDistance)
        {
            Positions = positions;
            InverseMass = inverseMass;
            BendPairs = bendPairs;
            BendRestLengths = bendRestLengths;
            BendLambda = bendLambda;
            ActiveBend = activeBend;
            ActiveBendCount = activeBendCount;
            BendCompliance = bendCompliance;
            StretchPairs = stretchPairs;
            StretchRestLengths = stretchRestLengths;
            StretchLambda = stretchLambda;
            ActiveStretch = activeStretch;
            ActiveStretchCount = activeStretchCount;
            StretchCompliance = stretchCompliance;
            ActiveParticles = activeParticles;
            ActiveParticleCount = activeParticleCount;
            Grabbed = grabbed;
            InBrush = inBrush;
            GrabDistance = grabDistance;
            NearestPin = nearestPin;
            PinDistance = pinDistance;
        }
    }

    /// <summary>
    /// <see cref="IClothSolverKernel.SolveCoarseLevel"/> に渡す、粗い段1段分の情報。配列はコピーしない。
    /// <see cref="Positions"/> と <see cref="LevelStart"/> は書き換えてよい。それ以外は読むだけにすること。
    /// </summary>
    public readonly struct ClothSolverCoarseLevelData
    {
        /// <summary>内部の座標。</summary>
        public readonly Vector3[] Positions;

        /// <summary>この段を解き始めたときの位置を控える作業用の配列（粒子ごと。この段の粒子の分だけ使う）。</summary>
        public readonly Vector3[] LevelStart;

        public readonly float[] InverseMass;

        /// <summary>この段の粒子（細かい粒子の番号）。</summary>
        public readonly int[] Members;

        /// <summary>この段の伸びの制約。粒子ペア（制約ごとに2つ、細かい粒子の番号）と元の長さ。</summary>
        public readonly int[] Pairs;
        public readonly float[] RestLengths;

        /// <summary>この段に含まれない粒子が動きを受け取る親。CSR 形式（<c>BindOffsets[i]</c> 〜 <c>BindOffsets[i+1]</c>）。</summary>
        public readonly int[] BindOffsets;
        public readonly int[] BindParents;
        public readonly float[] BindWeights;

        /// <summary>動きの配分先になる、計算範囲のピン留めでない粒子（先頭から <see cref="ActiveParticleCount"/> 個）。</summary>
        public readonly int[] ActiveParticles;
        public readonly int ActiveParticleCount;

        /// <summary>伸びだけの制約を繰り返す回数（<see cref="ClothSolver.CoarseIterations"/>）。</summary>
        public readonly int Iterations;

        internal ClothSolverCoarseLevelData(Vector3[] positions, Vector3[] levelStart, float[] inverseMass, int[] members, int[] pairs, float[] restLengths,
            int[] bindOffsets, int[] bindParents, float[] bindWeights, int[] activeParticles, int activeParticleCount, int iterations)
        {
            Positions = positions;
            LevelStart = levelStart;
            InverseMass = inverseMass;
            Members = members;
            Pairs = pairs;
            RestLengths = restLengths;
            BindOffsets = bindOffsets;
            BindParents = bindParents;
            BindWeights = bindWeights;
            ActiveParticles = activeParticles;
            ActiveParticleCount = activeParticleCount;
            Iterations = iterations;
        }
    }
}
