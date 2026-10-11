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
    public sealed class ClothSolver
    {
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

        // 元の形より内側に入らないようにするための情報（KeepOutsideRestSurface）
        readonly Vector3[] _restInternal;     // 元の位置（内部の座標）
        readonly Vector3[] _restNormals;      // 元の形の面の外向き。決められない粒子は 0 ベクトル。null なら無し
        readonly int[] _restAnchor;           // 粒子ごとの、今の位置に一番近い元の粒子
        float[] _restAllowance;               // 粒子ごとの、内側へ入ってよい深さ（体や内側の布までの隙間）。null なら 0

        // つまんだ範囲を目標へ引き寄せる柔らかい制約（BrushCompliance > 0 のとき）
        readonly Vector3[] _brushStart;       // つまみ始めの位置（内部の座標）
        readonly Vector3[] _brushLambda;      // XPBD の λ。同じつまみが続く間は Solve をまたいで引き継ぐ
        float _brushCompliance = DefaultBrushCompliance;
        readonly bool[] _pinned;
        readonly float[] _stretchLambda;
        readonly float[] _bendLambda;

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
        readonly int[] _activeStretch;
        readonly int[] _activeBend;
        int _activeParticleCount;
        int _activeStretchCount;
        int _activeBendCount;
        bool _activeDirty = true;
        float _activePull;   // 計算範囲を決めたときの、つまみ始めからの移動距離
        IClothSolverKernel _kernel = DefaultKernel;
        float _influenceRadius = 0.3f;
        float _activeMargin = float.PositiveInfinity;

        /// <summary>
        /// 新しく作るソルバーの <see cref="Kernel"/> の初期値。null なら C# で計算する。
        /// Burst など、環境によって使える高速版が、読み込み時に自分を登録するために使う。
        /// </summary>
        public static IClothSolverKernel DefaultKernel { get; set; }

        /// <summary>
        /// 重い計算（反復と粗い段）を任せる部品。null ならこのクラスの C# で計算する。
        /// 作ったときは <see cref="DefaultKernel"/> が入る。
        /// </summary>
        public IClothSolverKernel Kernel
        {
            get => _kernel;
            set
            {
                if (ReferenceEquals(value, _kernel)) return;
                _kernel = value;
                // 時間の見積もりは、計算する部品によって変わる
                ResetSolveTiming();
            }
        }

        /// <summary>伸びやすさ。0 で伸びない。</summary>
        public float StretchCompliance { get; set; } = 0f;

        /// <summary>
        /// 元の形の表面より内側に入らないようにするか（既定は true）。つまんでいる範囲は対象外。
        /// 衣装はもともと体や内側の布の外側にあるので、これだけで体や内側の布へのすり抜けをほぼ防げる。
        /// 内側へ押し込む編集をしたいときは false にする。<see cref="ClothModel.RestNormals"/> が無い布では効かない。
        /// </summary>
        public bool KeepOutsideRestSurface { get; set; } = true;

        /// <summary><see cref="BrushCompliance"/> の既定値。</summary>
        public const float DefaultBrushCompliance = 0.02f;

        /// <summary>
        /// つまんだ範囲の柔らかさ（XPBD のコンプライアンス）。0 なら、つまんだ範囲を目標の位置に固定する。
        ///
        /// 固定すると、布の長さで届かない位置（ウエストをピン留めしたスカートを外へ引くなど）まで連れて行くので、
        /// どこかに大きな伸びが残って消えない（スカートを外へ10cmで約54%）。柔らかくすると、届く目標には届き、
        /// 届かない目標では布の長さが勝って手前で止まる（同じ操作で約4%）。
        /// λ は、目標が動かない間は Solve をまたいで引き継ぐので、手を止めてから届かない分だけ少しずつ譲る。
        /// 目標が動くとリセットするので、ドラッグ中は手にしっかり付いてくる。大きいほど早く譲るが、届く目標にも遅れて着く。
        /// </summary>
        public float BrushCompliance
        {
            get => _brushCompliance;
            set
            {
                if (!(value >= 0f)) throw new ArgumentOutOfRangeException(nameof(value));
                if ((value > 0f) != (_brushCompliance > 0f)) _activeDirty = true;
                _brushCompliance = value;
            }
        }

        /// <summary>
        /// 粒子ごとに、元の形より内側へ入ってよい深さ（メートル）を設定する。null なら全て 0（元の形より内側へは入らない）。
        /// 体や内側の布までの隙間を入れると、ふんわりした服は隙間の分だけ内側へ動け、布を融通できる。
        /// </summary>
        public void SetRestSurfaceAllowance(float[] allowance)
        {
            if (allowance != null && allowance.Length != _positions.Length) throw new ArgumentException("粒子数が一致しません。", nameof(allowance));
            _restAllowance = allowance;
        }

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
            _restInternal = (Vector3[])_positions.Clone();
            _restNormals = model.RestNormals;
            _restAnchor = new int[n];
            for (int i = 0; i < n; i++) _restAnchor[i] = i;
            _brushStart = new Vector3[n];
            _brushLambda = new Vector3[n];
            _pinned = new bool[n];
            _stretchLambda = new float[model.StretchRestLengths.Length];
            _bendLambda = new float[model.BendRestLengths.Length];
            _grabDistance = new float[n];
            _pinDistance = new float[n];
            _nearestPin = new int[n];
            _inBrush = new bool[n];
            _inverseMass = new float[n];
            _activeParticles = new int[n];
            _activeStretch = new int[model.StretchRestLengths.Length];
            _activeBend = new int[model.BendRestLengths.Length];
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
                for (int i = 0; i < _inBrush.Length; i++)
                {
                    _inBrush[i] = _grabDistance[i] <= brushRadius;
                    _brushStart[i] = _positions[i];
                    _brushLambda[i] = Vector3.Zero;
                }
                _grabOrigin = _positions[particle];
                _appliedOffset = Vector3.Zero;
                _activePull = 0f;
                _activeDirty = true;
            }
            _grabbed = particle;
            _brushRadius = brushRadius;
            Vector3 newTarget = target - _origin;
            if (newTarget != _grabTarget)
            {
                // ドラッグ中（目標が動いている間）は λ をためない。ためると、一時的にかかった力の分だけ、
                // 届く目標でも遅れが残る。手を止めてから、届かない分だけ少しずつ譲る
                for (int i = 0; i < _inBrush.Length; i++)
                {
                    if (_inBrush[i]) _brushLambda[i] = Vector3.Zero;
                }
            }
            _grabTarget = newTarget;
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
            for (int a = 0; a < _activeStretchCount; a++)
            {
                int k = _activeStretch[a];
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

        /// <summary>1回の Solve にまとめる反復回数の上限（<see cref="SolveFor"/>）。時間の見積もりが外れたときに、持ち時間を大きく超えないため。</summary>
        public const int MaxIterationsPerSolve = 40;

        // SolveFor の時間の見積もり。1回の Solve の時間 = 前後の処理 + 反復1回 × 回数 に、測定を古いものほど軽く重み付けして当てはめる
        const double TimingDecay = 0.75;
        const double DefaultOverheadShare = 0.3;
        double _timingWeight, _timingSumN, _timingSumT, _timingSumNN, _timingSumNT;
        double _overheadShare = DefaultOverheadShare;   // 測定のばらつきが足りないときに使う、前後の処理が占める割合
        double _overheadMilliseconds, _iterationMilliseconds;

        void ResetSolveTiming()
        {
            _timingWeight = _timingSumN = _timingSumT = _timingSumNN = _timingSumNT = 0.0;
            _overheadShare = DefaultOverheadShare;
            _overheadMilliseconds = _iterationMilliseconds = 0.0;
        }

        void RecordSolveTiming(int iterations, double milliseconds)
        {
            // Burst のコンパイルや GC などの突発的な遅れで、見積もりが何コマも引きずられないようにする
            if (_timingWeight > 0.0)
            {
                double predicted = _overheadMilliseconds + _iterationMilliseconds * iterations;
                if (predicted > 0.0) milliseconds = Math.Min(milliseconds, 4.0 * predicted);
            }

            _timingWeight = _timingWeight * TimingDecay + 1.0;
            _timingSumN = _timingSumN * TimingDecay + iterations;
            _timingSumT = _timingSumT * TimingDecay + milliseconds;
            _timingSumNN = _timingSumNN * TimingDecay + (double)iterations * iterations;
            _timingSumNT = _timingSumNT * TimingDecay + iterations * milliseconds;

            double meanN = _timingSumN / _timingWeight;
            double meanT = _timingSumT / _timingWeight;
            double variance = _timingSumNN / _timingWeight - meanN * meanN;
            bool fitted = false;
            // 回数が十分ばらついているときだけ、直線を当てはめて前後の処理と反復1回の時間を分ける。
            // 毎回ほぼ同じ回数だと分けられないので、前に分けられたときの割合を使う
            if (variance > 0.04 * meanN * meanN)
            {
                double slope = (_timingSumNT / _timingWeight - meanN * meanT) / variance;
                double intercept = meanT - slope * meanN;
                if (slope > 0.0 && intercept >= 0.0)
                {
                    _iterationMilliseconds = slope;
                    _overheadMilliseconds = intercept;
                    _overheadShare = Math.Min(0.9, intercept / meanT);
                    fitted = true;
                }
                else
                {
                    _overheadShare = slope > 0.0 ? 0.0 : 0.9;
                }
            }
            if (!fitted)
            {
                _overheadMilliseconds = _overheadShare * meanT;
                _iterationMilliseconds = (meanT - _overheadMilliseconds) / meanN;
            }
        }

        /// <summary>
        /// 指定した時間（ミリ秒）だけ解く。Unity側で、1コマの時間予算に収めるために使う。
        /// 最低でも1回は解く。
        ///
        /// 1回の Solve には、反復の回数によらない前後の処理（粗い段、元の形の近い粒子の探し直しなど）がかかる。
        /// そこで、反復1回の時間と前後の処理の時間を覚えておき、持ち時間をなるべく少ない回数の Solve で使い切る
        /// （1回の Solve は最大 <see cref="MaxIterationsPerSolve"/> 回の反復）。
        /// 時間が分かるまで（このソルバーで最初の呼び出し）は、<paramref name="iterationsPerStep"/> 回の反復で測る。
        /// </summary>
        /// <param name="iterationsPerStep">時間がまだ分からないときの、最初の Solve の反復回数</param>
        /// <returns>行った反復回数</returns>
        public int SolveFor(double milliseconds, int iterationsPerStep = 5)
        {
            if (iterationsPerStep <= 0) throw new ArgumentOutOfRangeException(nameof(iterationsPerStep));
            var timer = Stopwatch.StartNew();
            int done = 0;
            while (true)
            {
                int step = iterationsPerStep;
                if (_timingWeight > 0.0)
                {
                    double remaining = milliseconds - timer.Elapsed.TotalMilliseconds;
                    double affordable = _iterationMilliseconds > 0.0
                        ? (remaining - _overheadMilliseconds) / _iterationMilliseconds
                        : MaxIterationsPerSolve;
                    step = affordable >= MaxIterationsPerSolve ? MaxIterationsPerSolve : (int)Math.Max(affordable, 0.0);
                    if (step < 1)
                    {
                        // 次の Solve は持ち時間に入らない。1回も解いていなければ、最低1回は解く
                        if (done > 0) break;
                        step = 1;
                    }
                }

                long begin = Stopwatch.GetTimestamp();
                Solve(step);
                RecordSolveTiming(step, (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency);
                done += step;
                if (timer.Elapsed.TotalMilliseconds >= milliseconds) break;
            }
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

            int[] stretchPairs = _model.StretchPairs;
            float[] stretchRest = _model.StretchRestLengths;
            int[] bendPairs = _model.BendPairs;
            float[] bendRest = _model.BendRestLengths;

            bool keepOutside = KeepOutsideRestSurface && _restNormals != null;
            bool softBrush = _grabbed >= 0 && _brushCompliance > 0f;

            IClothSolverKernel kernel = Kernel;
            if (kernel != null && iterations > 0)
            {
                // λ のリセット、元の形の近い粒子の探し直し、ワールド座標への書き戻しも、カーネルが行う
                kernel.Iterate(new ClothSolverIterationData(_positions, _worldPositions, _origin, _graph.Offsets, _graph.Neighbors, _inverseMass,
                    bendPairs, bendRest, _bendLambda, _activeBend, _activeBendCount, BendCompliance,
                    stretchPairs, stretchRest, _stretchLambda, _activeStretch, _activeStretchCount, StretchCompliance,
                    _activeParticles, _activeParticleCount, _grabbed, _inBrush, _grabDistance, _nearestPin, _pinDistance,
                    keepOutside, _restInternal, _restNormals, _restAnchor, _restAllowance,
                    softBrush ? _brushCompliance : 0f, _brushStart, _brushLambda, _grabTarget - _grabOrigin), iterations);
                return;
            }

            for (int a = 0; a < _activeStretchCount; a++) _stretchLambda[_activeStretch[a]] = 0f;
            for (int a = 0; a < _activeBendCount; a++) _bendLambda[_activeBend[a]] = 0f;
            if (keepOutside && iterations > 0) UpdateRestAnchors();

            for (int it = 0; it < iterations; it++)
            {
                // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
                if (softBrush) SolveBrushTargets();
                SolveDistances(_activeBend, _activeBendCount, bendPairs, bendRest, _bendLambda, BendCompliance);
                SolveDistances(_activeStretch, _activeStretchCount, stretchPairs, stretchRest, _stretchLambda, StretchCompliance);

                SolveTethers();
                if (keepOutside) SolveRestSurface();
            }

            // 動くのは計算範囲の粒子だけ（ピン留めと範囲外は動かない）
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                _worldPositions[i].X = _positions[i].X + _origin.X;
                _worldPositions[i].Y = _positions[i].Y + _origin.Y;
                _worldPositions[i].Z = _positions[i].Z + _origin.Z;
            }
        }

        void SolveCoarseLevels()
        {
            IClothSolverKernel kernel = Kernel;
            foreach (var level in _hierarchy.Levels)
            {
                if (kernel != null)
                {
                    kernel.SolveCoarseLevel(new ClothSolverCoarseLevelData(_positions, _levelStart, _inverseMass, level.Members,
                        level.StretchPairs, level.RestLengths, level.BindOffsets, level.BindParents, level.BindWeights,
                        _activeParticles, _activeParticleCount, CoarseIterations));
                    continue;
                }

                foreach (int m in level.Members) _levelStart[m] = _positions[m];

                for (int it = 0; it < CoarseIterations; it++) SolveStretchOnly(level.StretchPairs, level.RestLengths);

                // この段の粒子の動きを、この段に含まれない粒子へ配る
                Vector3[] p = _positions;
                Vector3[] start = _levelStart;
                int[] offsets = level.BindOffsets;
                int[] parents = level.BindParents;
                float[] weights = level.BindWeights;
                for (int a = 0; a < _activeParticleCount; a++)
                {
                    int i = _activeParticles[a];
                    if (_inverseMass[i] == 0f) continue;
                    int end = offsets[i + 1];
                    float dx = 0f, dy = 0f, dz = 0f;
                    for (int k = offsets[i]; k < end; k++)
                    {
                        int parent = parents[k];
                        float w = weights[k];
                        dx += (p[parent].X - start[parent].X) * w;
                        dy += (p[parent].Y - start[parent].Y) * w;
                        dz += (p[parent].Z - start[parent].Z) * w;
                    }
                    p[i].X += dx;
                    p[i].Y += dy;
                    p[i].Z += dz;
                }
            }
        }

        // 伸びているときだけ縮める（縮んでいるのは許す）
        void SolveStretchOnly(int[] pairs, float[] rest)
        {
            Vector3[] p = _positions;
            float[] inverseMass = _inverseMass;
            for (int k = 0; k < rest.Length; k++)
            {
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;

                float dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, dz = p[i].Z - p[j].Z;
                float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length <= rest[k]) continue;

                float s = -(length - rest[k]) / (w * length);
                dx *= s;
                dy *= s;
                dz *= s;
                p[i].X += wi * dx; p[i].Y += wi * dy; p[i].Z += wi * dz;
                p[j].X -= wj * dx; p[j].Y -= wj * dy; p[j].Z -= wj * dz;
            }
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
                // つまんだ範囲は、柔らかい制約で引き寄せるときは動ける（BrushCompliance を参照）
                bool fixedParticle = _pinned[i] || (IsGrabbed(i) && _brushCompliance <= 0f) || !active;
                _inverseMass[i] = fixedParticle ? 0f : 1f;
                // つまんだ範囲もテザーの対象にするので、ピン留めでなければ一覧に入れる
                if (active && !_pinned[i]) _activeParticles[_activeParticleCount++] = i;
            }

            _activeStretchCount = CollectActive(_model.StretchPairs, _activeStretch);
            _activeBendCount = CollectActive(_model.BendPairs, _activeBend);
        }

        // 少なくとも一方の粒子が動ける制約だけを集める
        int CollectActive(int[] pairs, int[] result)
        {
            int count = 0;
            for (int k = 0; k < result.Length; k++)
            {
                if (_inverseMass[pairs[2 * k]] > 0f || _inverseMass[pairs[2 * k + 1]] > 0f) result[count++] = k;
            }
            return count;
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
                float f = Falloff(_grabDistance[i]);
                if (f == 0f) continue;
                _positions[i].X += delta.X * f;
                _positions[i].Y += delta.Y * f;
                _positions[i].Z += delta.Z * f;
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

        void SolveTethers()
        {
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                if (_grabbed >= 0 && !_inBrush[i]) SolveTether(i, _grabbed, _grabDistance[i]);

                // つまんだ範囲も、ピンから布の長さ以上は離れない。
                // 布の長さが足りない方向へ引っ張ると、本物の布のように目標の手前で止まる（持ち上がる）。
                int pin = _nearestPin[i];
                if (pin >= 0) SolveTether(i, pin, _pinDistance[i]);
            }
        }

        // 1回の Solve の間に粒子が動く距離は粒子の間隔より十分小さいので、近い元の粒子は Solve の最初に1度だけ探す。
        // 今の近い粒子から、伸びの制約でつながった隣へ、近くなる限りたどる（表面に沿って探すので、重ね着の別の層には移らない）
        void UpdateRestAnchors()
        {
            int[] offsets = _graph.Offsets;
            int[] neighbors = _graph.Neighbors;
            Vector3[] p = _positions;
            Vector3[] rest = _restInternal;
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                int anchor = _restAnchor[i];
                float px = p[i].X, py = p[i].Y, pz = p[i].Z;
                float best = DistanceSquared(px, py, pz, rest[anchor]);
                for (int step = 0; step < 16; step++)
                {
                    int next = -1;
                    int end = offsets[anchor + 1];
                    for (int k = offsets[anchor]; k < end; k++)
                    {
                        int candidate = neighbors[k];
                        float d = DistanceSquared(px, py, pz, rest[candidate]);
                        if (d < best)
                        {
                            best = d;
                            next = candidate;
                        }
                    }
                    if (next < 0) break;
                    anchor = next;
                }
                _restAnchor[i] = anchor;
            }
        }

        static float DistanceSquared(float x, float y, float z, Vector3 b)
        {
            float dx = x - b.X, dy = y - b.Y, dz = z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        // 元の形の、近い粒子の面より、入ってよい深さ以上内側にあれば押し戻す。粒子は1cm間隔なので、曲面でも誤差は1mm未満
        void SolveRestSurface()
        {
            Vector3[] p = _positions;
            Vector3[] rest = _restInternal;
            Vector3[] normals = _restNormals;
            float[] allowance = _restAllowance;
            bool grabbing = _grabbed >= 0;
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                if (grabbing && _inBrush[i]) continue;
                int r = _restAnchor[i];
                float nx = normals[r].X, ny = normals[r].Y, nz = normals[r].Z;
                float d = (p[i].X - rest[r].X) * nx + (p[i].Y - rest[r].Y) * ny + (p[i].Z - rest[r].Z) * nz;
                if (allowance != null) d += allowance[r];
                if (d >= 0f) continue;
                p[i].X -= d * nx;
                p[i].Y -= d * ny;
                p[i].Z -= d * nz;
            }
        }

        // つまんだ範囲の各粒子を、つまみ始めの位置＋つまんだ点の移動量へ引き寄せる（XPBD の柔らかい制約）
        void SolveBrushTargets()
        {
            Vector3[] p = _positions;
            float alpha = _brushCompliance;
            float mx = _grabTarget.X - _grabOrigin.X, my = _grabTarget.Y - _grabOrigin.Y, mz = _grabTarget.Z - _grabOrigin.Z;
            float scale = 1f / (1f + alpha);
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                if (!_inBrush[i]) continue;
                float dx = (-(p[i].X - _brushStart[i].X - mx) - alpha * _brushLambda[i].X) * scale;
                float dy = (-(p[i].Y - _brushStart[i].Y - my) - alpha * _brushLambda[i].Y) * scale;
                float dz = (-(p[i].Z - _brushStart[i].Z - mz) - alpha * _brushLambda[i].Z) * scale;
                _brushLambda[i].X += dx;
                _brushLambda[i].Y += dy;
                _brushLambda[i].Z += dz;
                p[i].X += dx;
                p[i].Y += dy;
                p[i].Z += dz;
            }
        }

        void SolveTether(int i, int origin, float maxLength)
        {
            if (float.IsInfinity(maxLength)) return;
            Vector3[] p = _positions;
            float ox = p[origin].X, oy = p[origin].Y, oz = p[origin].Z;
            float dx = p[i].X - ox, dy = p[i].Y - oy, dz = p[i].Z - oz;
            float lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared <= maxLength * maxLength) return;
            float s = maxLength / MathF.Sqrt(lengthSquared);
            p[i].X = ox + dx * s;
            p[i].Y = oy + dy * s;
            p[i].Z = oz + dz * s;
        }

        // XPBD の距離の制約を、active の先頭 count 個について順に解く。
        // Unity の Mono では System.Numerics.Vector3 の演算子が1つずつ関数の呼び出しになり、成分ごとに書くより約3倍遅い。
        // 1制約ごとのメソッドの呼び出しも避けるため、ループの中に直接書いている
        void SolveDistances(int[] active, int count, int[] pairs, float[] rest, float[] lambda, float alpha)
        {
            Vector3[] p = _positions;
            float[] inverseMass = _inverseMass;
            for (int a = 0; a < count; a++)
            {
                int k = active[a];
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;

                float dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, dz = p[i].Z - p[j].Z;
                float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length < 1e-12f) continue;

                float deltaLambda = (-(length - rest[k]) - alpha * lambda[k]) / (w + alpha);
                lambda[k] += deltaLambda;

                float s = deltaLambda / length;
                dx *= s;
                dy *= s;
                dz *= s;
                p[i].X += wi * dx; p[i].Y += wi * dy; p[i].Z += wi * dz;
                p[j].X -= wj * dx; p[j].Y -= wj * dy; p[j].Z -= wj * dz;
            }
        }
    }
}
