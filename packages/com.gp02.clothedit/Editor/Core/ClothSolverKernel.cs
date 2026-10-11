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

        /// <summary>
        /// 1回の Solve の細かい粒子の計算（iterations が 1 以上のときだけ呼ばれる）を、次の順に行う。
        /// 1. 計算範囲の伸び・曲げの制約の λ を 0 に戻す（<see cref="ClothSolverIterationData.ActiveStretch"/>、<see cref="ClothSolverIterationData.ActiveBend"/>）
        /// 2. <see cref="ClothSolverIterationData.KeepOutsideRestSurface"/> が true なら、元の形の近い粒子（<see cref="ClothSolverIterationData.RestAnchor"/>）を探し直す。
        ///    続けて <see cref="ClothSolverIterationData.ContactCount"/> が正なら、接触の組の内側の粒子（<see cref="ClothSolverIterationData.ContactInner"/>）を探し直す
        /// 3. 反復を iterations 回行う（つまんだ範囲の引き寄せ → 曲げの制約 → 伸びの制約 → テザー → 元の形より内側に入らない制約 → 層どうしの接触 → 体との平面）。
        ///    テザーは、粒子ごとに「範囲の外側なら GrabSource から GrabDistance 以内」→「NearestPin から PinDistance 以内」の順。
        ///    最初の引き寄せは <see cref="ClothSolverIterationData.BrushCompliance"/> が正のときだけ、
        ///    元の形より内側に入らない制約は KeepOutsideRestSurface が true のときだけ、層どうしの接触は ContactCount が正のときだけ、
        ///    体との平面は <see cref="ClothSolverIterationData.BodyPlaneCount"/> が正のときだけ
        /// 4. 計算範囲の粒子の位置を、ワールド座標（内部の座標 + <see cref="ClothSolverIterationData.Origin"/>）として
        ///    <see cref="ClothSolverIterationData.WorldPositions"/> へ書き戻す
        /// 前後の処理（1・2・4）を C# で別に行うと、粒子が多いときに反復1回分より重くなるため、まとめて任せている。
        /// </summary>
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

        /// <summary>外に見せるワールド座標。計算のあとに、計算範囲の粒子の分を書き戻す（<c>Positions[i] + Origin</c>）。</summary>
        public readonly Vector3[] WorldPositions;

        /// <summary>内部の座標に足すとワールド座標になる、原点のずらし。</summary>
        public readonly Vector3 Origin;

        /// <summary>
        /// 粒子をつなぐ辺のグラフ（伸びの制約でつながった隣）。粒子 i の隣は <c>GraphNeighbors[GraphOffsets[i] .. GraphOffsets[i + 1])</c>。
        /// 元の形の近い粒子を探し直すのに使う。
        /// </summary>
        public readonly int[] GraphOffsets;
        public readonly int[] GraphNeighbors;

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

        /// <summary>つまんでいる範囲の中心の粒子。つまんでいなければ -1（つまんでいるかどうかの判断にだけ使う）。</summary>
        public readonly int Grabbed;

        /// <summary>つまんでいる範囲に含まれるか（粒子ごと、1バイトの bool）。</summary>
        public readonly bool[] InBrush;

        /// <summary>つまんだ範囲（いずれかの粒子）から、メッシュに沿った距離（つながっていなければ無限大）。</summary>
        public readonly float[] GrabDistance;

        /// <summary>
        /// 一番近い、つまんだ範囲の粒子（つながっていなければ -1）。テザーで、範囲の外側の粒子 i は
        /// GrabSource[i] から GrabDistance[i] より遠くへは離れない。
        /// </summary>
        public readonly int[] GrabSource;

        /// <summary>一番近いピンの粒子（なければ -1）と、そこまでのメッシュに沿った距離。</summary>
        public readonly int[] NearestPin;
        public readonly float[] PinDistance;

        /// <summary>元の形より内側に入らないようにするか。false なら、下の3つは null のことがある。</summary>
        public readonly bool KeepOutsideRestSurface;

        /// <summary>元の位置（内部の座標）と、元の形の面の外向き（決められない粒子は 0 ベクトル）。</summary>
        public readonly Vector3[] RestPositions;
        public readonly Vector3[] RestNormals;

        /// <summary>
        /// 粒子ごとの、今の位置に一番近い元の粒子。テザーの後に、つまんでいる範囲以外の粒子を、
        /// この粒子の面（RestPositions と RestNormals で決まる平面）より外側へ押し戻す。
        /// 反復の前に、計算範囲の粒子について、今の粒子から辺でつながった隣へ、近くなる限り（最大16歩）たどって更新する。
        /// 1歩ごとに隣を順に調べ、最も近くなる隣（同じ距離なら先に調べたほう）へ進む。
        /// </summary>
        public readonly int[] RestAnchor;

        /// <summary>粒子ごとの、元の形より内側へ入ってよい深さ（RestAnchor の粒子の値を使う）。null なら 0。</summary>
        public readonly float[] RestAllowance;

        /// <summary>
        /// つまんだ範囲の引き寄せのコンプライアンス。0 以下なら引き寄せない（つまんだ範囲は逆質量 0 で、目標の位置に置いてある）。
        /// 正なら、つまんだ範囲（InBrush）の粒子 i を、BrushGoal[i]（内部の座標）へ引き寄せる XPBD の制約を、x, y, z 別々に解く:
        /// Δ = (-(p - 目標) - α λ) / (1 + α)、λ += Δ、p += Δ。BrushLambda は書き換えてよい（Solve をまたいで引き継がれる）。
        /// </summary>
        public readonly float BrushCompliance;
        public readonly Vector3[] BrushGoal;
        public readonly Vector3[] BrushLambda;

        /// <summary>
        /// 重ね着の層どうしの接触の組の数（0 なら接触の段は何もしない）。組 k は、外側の粒子 i = ContactOuter[k] と内側の粒子 j = ContactInner[k]。
        /// 反復の前に、j から辺でつながった隣へ、今の位置が p_i に近くなる限り（最大16歩）たどって ContactInner を更新する（RestAnchor と同じたどり方）。
        /// 反復の最後に、組を順に、d = p_i - p_j、a = dot(d, RestNormals[j])、c = a - ContactGap[k] が負で、
        /// かつ面に沿った離れ具合 dot(d, d) - a * a が ContactReach の2乗以下なら、
        /// 逆質量 w_i, w_j（つまんでいるとき、InBrush の粒子は相手が動ける（逆質量が正）なら 0 にする。i を先に調べる）で
        /// s = -c / (w_i + w_j)、p_i += w_i s n、p_j -= w_j s n とする（w_i + w_j が 0 なら何もしない）。
        /// </summary>
        public readonly int ContactCount;
        public readonly int[] ContactOuter;
        public readonly int[] ContactInner;
        public readonly float[] ContactGap;
        public readonly float ContactReach;

        /// <summary>
        /// 体との平面の数（0 なら体との段は何もしない）。平面 k は、粒子 i = BodyPlaneParticles[k] が
        /// dot(p_i - BodyPlanePoints[k], BodyPlaneNormals[k]) &gt;= BodyPlaneMinDistances[k] を満たすようにする片側だけの制約（点は内部の座標）。
        /// 反復の最後に、平面を順に、逆質量が 0 の粒子は飛ばして、d = dot(p_i - 点, 向き) - 下限 が負なら p_i -= d * 向き とする。
        /// 同じ粒子が2つの平面に出てくることがある（体の尖った所のための平面）。
        /// </summary>
        public readonly int BodyPlaneCount;
        public readonly int[] BodyPlaneParticles;
        public readonly Vector3[] BodyPlanePoints;
        public readonly Vector3[] BodyPlaneNormals;
        public readonly float[] BodyPlaneMinDistances;

        internal ClothSolverIterationData(Vector3[] positions, Vector3[] worldPositions, Vector3 origin, int[] graphOffsets, int[] graphNeighbors, float[] inverseMass,
            int[] bendPairs, float[] bendRestLengths, float[] bendLambda, int[] activeBend, int activeBendCount, float bendCompliance,
            int[] stretchPairs, float[] stretchRestLengths, float[] stretchLambda, int[] activeStretch, int activeStretchCount, float stretchCompliance,
            int[] activeParticles, int activeParticleCount, int grabbed, bool[] inBrush, float[] grabDistance, int[] grabSource, int[] nearestPin, float[] pinDistance,
            bool keepOutsideRestSurface, Vector3[] restPositions, Vector3[] restNormals, int[] restAnchor, float[] restAllowance,
            float brushCompliance, Vector3[] brushGoal, Vector3[] brushLambda,
            int contactCount, int[] contactOuter, int[] contactInner, float[] contactGap, float contactReach,
            int bodyPlaneCount, int[] bodyPlaneParticles, Vector3[] bodyPlanePoints, Vector3[] bodyPlaneNormals, float[] bodyPlaneMinDistances)
        {
            BodyPlaneCount = bodyPlaneCount;
            BodyPlaneParticles = bodyPlaneParticles;
            BodyPlanePoints = bodyPlanePoints;
            BodyPlaneNormals = bodyPlaneNormals;
            BodyPlaneMinDistances = bodyPlaneMinDistances;
            ContactReach = contactReach;
            ContactCount = contactCount;
            ContactOuter = contactOuter;
            ContactInner = contactInner;
            ContactGap = contactGap;
            WorldPositions = worldPositions;
            Origin = origin;
            GraphOffsets = graphOffsets;
            GraphNeighbors = graphNeighbors;
            RestAllowance = restAllowance;
            BrushCompliance = brushCompliance;
            BrushGoal = brushGoal;
            BrushLambda = brushLambda;
            KeepOutsideRestSurface = keepOutsideRestSurface;
            RestPositions = restPositions;
            RestNormals = restNormals;
            RestAnchor = restAnchor;
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
            GrabSource = grabSource;
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
