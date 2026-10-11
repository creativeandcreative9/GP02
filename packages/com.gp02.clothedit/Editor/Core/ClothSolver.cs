using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 「つまんで引っ張る」編集のための粒子計算。
    ///
    /// 1回の Solve で次の2段階を行う。
    /// 1. つまんだ範囲の目標の変化を、範囲からメッシュに沿った距離に応じて周りへ配る（影響の広がりを決める）
    /// 2. XPBD（Extended Position Based Dynamics）の制約を繰り返し解き、布の伸びと曲げを直す
    ///
    /// 時間は進めない（重力も速度も持たない）ので、時間刻みは 1 とみなし、コンプライアンスは値をそのまま使う。
    ///
    /// つまんだ範囲は、粒子ごとの目標（<see cref="BrushMotion"/> の動きと、範囲の中の重み）へ引き寄せる。
    /// テザーと周りへの配分は、範囲の中心ではなく「一番近い、範囲の粒子」から測った距離で行うので、
    /// 範囲がいくつに分かれていても（対称編集の左右、直線距離でつまんだ別の層）同じように扱える。
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

        // つまんだ範囲の粒子ごとの目標。BrushCompliance > 0 なら柔らかい制約で引き寄せ、0 なら毎 Solve でそこに置く
        readonly Vector3[] _brushStart;       // 範囲を選んだときの位置（内部の座標）
        readonly Vector3[] _brushGoal;        // 目標（内部の座標）
        readonly Vector3[] _brushApplied;     // 周りへ配り終えた目標。SpreadGrabMotion が前回からの増分だけを配るため
        readonly int[] _goalAnchor;           // つまんだ範囲の粒子ごとの、目標に一番近い元の粒子（目標を元の形の外に保つため）
        readonly int[] _clampedBrush;         // 目標を元の形の外へ押し出した、つまんだ範囲の粒子
        int _clampedCount;
        readonly Vector3[] _brushLambda;      // XPBD の λ。同じつまみが続く間は Solve をまたいで引き継ぐ
        float _brushCompliance = DefaultBrushCompliance;
        readonly bool[] _pinned;
        readonly float[] _stretchLambda;
        readonly float[] _bendLambda;

        // テザー：つまんだ範囲・ピンから、メッシュに沿った距離より遠くへは離れない。
        // 少ない反復回数でも布が伸びないようにするための制約（Long Range Attachments）。
        readonly float[] _grabDistance;   // つまんだ範囲（いずれかの粒子）からの、メッシュに沿った距離
        readonly int[] _grabSource;       // 一番近い、つまんだ範囲の粒子
        readonly float[] _pinDistance;
        readonly int[] _nearestPin;
        bool _pinDistancesDirty = true;

        // つまんでいる範囲。中心の粒子（と鏡像の粒子）から、ブラシ半径以内で重みが正の粒子
        readonly bool[] _inBrush;
        readonly float[] _brushWeight;    // 範囲外は 0
        readonly bool[] _brushMirrored;   // 鏡像の粒子に属し、鏡に映した動きで動く
        readonly int[] _brushList;        // 範囲の粒子（先頭から _brushCount 個）
        int _brushCount;
        int _grabbed = -1;
        int _mirrorSeed = -1;             // 鏡像の中心の粒子。使わなければ -1
        bool _midline;                    // 中央線の上をつまんだ（動きを鏡の面に沿う成分だけにする）
        float _brushRadius;
        FalloffCurve _brushCurve = FalloffCurve.Constant;
        BrushDistanceMode _brushMode;
        float _brushConnect;              // 範囲を選んだときのつながりの許容距離 ε
        bool _hasMirror;
        MirrorPlane _mirrorPlane;
        float _mirrorTolerance;
        Vector3 _grabOrigin;              // 範囲を選んだときの中心の位置（内部の座標）
        AffineMotion _motion;             // 内部の座標での動き
        AffineMotion _mirrorMotion;       // 鏡に映した動き
        bool _motionValid;
        bool _spreadPending;              // 目標が変わり、周りへまだ配っていない
        float _brushPull;                 // 範囲の粒子の、目標までの移動距離の最大
        readonly int[] _seedWork = new int[2];
        int[] _components;                // 粒子ごとの島の番号（FindMirrorParticle などで初めて使うときに作る）
        int _componentCount;

        // 重ね着の層どうしの接触（SetLayerContacts）。外側の粒子 _contactOuter[k] が、内側の粒子 _contactInner[k] の面より内に入らない
        int[] _contactOuter = new int[0];
        int[] _contactInner = new int[0];  // Solve ごとに、内側の層の隣へたどって近いものに探し直す
        float[] _contactGap = new float[0];
        int _contactCount;
        float _contactReach;              // 内側の粒子の面に沿ってこれより離れた外側の粒子は、組を解かない
        bool _keepLayersApart = true;
        bool[] _componentActive;          // 計算範囲を決めるときの作業用（島ごと）

        // 集合でつまむ（Grab(int[], ...)）ときの作業用
        int[] _selectionSet = new int[0]; // 前回つまんだ集合（同じ集合なら範囲を選び直さない）
        int _selectionCount = -1;         // 集合でつまんでいなければ -1
        bool[] _selectionMark;
        int[] _selectionSources;
        PointGrid _mirrorGrid;            // 鏡像を探すための、元の位置の格子
        float _mirrorGridCell;
        List<int> _queryWork;
        float[] _workDistance;            // RelaxTowardRest 用
        MeshGraph _rangeGraph;            // 範囲を決めるときの、ε 以内の別の島もつないだグラフ（RangeGraph）
        readonly int[] _nearIndex = new int[4];   // ReflectedDisplacement の作業用
        readonly float[] _nearSq = new float[4];
        float _rangeGraphConnect = -1f;

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

        /// <summary>
        /// 重ね着の層どうしの接触（<see cref="SetLayerContacts"/>）を解くか（既定は true）。
        /// 外側の層が内側の層の面より内に入らないよう、両方を押し分ける。組が無ければ何もしない。
        /// </summary>
        public bool KeepLayersApart
        {
            get => _keepLayersApart;
            set
            {
                if (value == _keepLayersApart) return;
                _keepLayersApart = value;
                _activeDirty = true;
            }
        }

        /// <summary>層どうしの接触の組の数。</summary>
        public int LayerContactCount => _contactCount;

        /// <summary>接触の組 k の、外側の粒子。</summary>
        public int LayerContactOuter(int k) => _contactOuter[k];

        /// <summary>接触の組 k の、今の内側の粒子（Solve ごとに探し直す）。</summary>
        public int LayerContactInner(int k) => _contactInner[k];

        /// <summary>
        /// 重ね着の層どうしの接触の組を設定する（<see cref="LayerContacts.Find(ParticleCloth, float, float)"/> で探したもの）。null なら無し。
        /// 組でつながった島は、片方をつまむともう片方も計算範囲に入る（押された内側の層が動けるように）。
        /// </summary>
        public void SetLayerContacts(LayerContacts contacts)
        {
            int count = contacts != null ? contacts.Count : 0;
            int n = _positions.Length;
            for (int k = 0; k < count; k++)
            {
                if ((uint)contacts.Outer[k] >= (uint)n || (uint)contacts.Inner[k] >= (uint)n) throw new ArgumentException("粒子の番号が範囲外です。", nameof(contacts));
            }
            _contactOuter = count > 0 ? (int[])contacts.Outer.Clone() : new int[0];
            _contactInner = count > 0 ? (int[])contacts.Inner.Clone() : new int[0];
            _contactGap = count > 0 ? (float[])contacts.Gaps.Clone() : new float[0];
            _contactCount = count;
            _contactReach = count > 0 ? contacts.Reach : 0f;
            if (count > 0)
            {
                EnsureComponents();
                if (_componentActive == null) _componentActive = new bool[_componentCount];
            }
            _activeDirty = true;
        }

        void EnsureComponents()
        {
            if (_components != null) return;
            _components = new int[_positions.Length];
            _componentCount = _graph.Components(_components);
        }

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
        /// 有限にすると、つまんだ範囲の端からのメッシュに沿った距離が、影響範囲＋余白＋範囲の粒子の最大の移動距離 より外を固定して速くする。
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
            _brushGoal = new Vector3[n];
            _brushApplied = new Vector3[n];
            _goalAnchor = new int[n];
            _clampedBrush = new int[n];
            _brushLambda = new Vector3[n];
            _pinned = new bool[n];
            _stretchLambda = new float[model.StretchRestLengths.Length];
            _bendLambda = new float[model.BendRestLengths.Length];
            _grabDistance = new float[n];
            _grabSource = new int[n];
            _pinDistance = new float[n];
            _nearestPin = new int[n];
            _inBrush = new bool[n];
            _brushWeight = new float[n];
            _brushMirrored = new bool[n];
            _brushList = new int[n];
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

        /// <summary>対称編集で、範囲のもう1つの中心にしている鏡像の粒子。使っていなければ -1。</summary>
        public int MirrorParticle => _grabbed >= 0 ? _mirrorSeed : -1;

        /// <summary>つまんでいる範囲の粒子の数。</summary>
        public int BrushParticleCount => _grabbed >= 0 ? _brushCount : 0;

        /// <summary>つまんでいる範囲に含まれるか（重みが正か）。</summary>
        public bool IsGrabbed(int particle) => _grabbed >= 0 && _inBrush[particle];

        /// <summary>つまんでいる範囲の中の重み（0〜1）。範囲外やつまんでいないときは 0。中心の粒子は 1。</summary>
        /// <summary>目標を元の形の外へ押し出した、つまんだ範囲の粒子の数（<see cref="KeepOutsideRestSurface"/>）。</summary>
        public int ClampedBrushCount => _clampedCount;

        public float BrushWeight(int particle) => _grabbed >= 0 ? _brushWeight[particle] : 0f;

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
        /// 範囲全体が同じだけ平行移動する（<see cref="Grab(int, BrushMotion, BrushOptions)"/> の、
        /// 平行移動だけ・重み一定・メッシュに沿った距離・対称なし の場合と同じ）。
        /// </summary>
        /// <param name="particle">つまむ範囲の中心</param>
        /// <param name="target">中心の粒子の移動先。範囲内の粒子は、中心と同じだけ平行移動する</param>
        /// <param name="brushRadius">つまむ範囲（メッシュに沿った距離、メートル）。0 なら中心の1点だけ</param>
        public void Grab(int particle, Vector3 target, float brushRadius = 0f)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            if (!(brushRadius >= 0f)) throw new ArgumentOutOfRangeException(nameof(brushRadius));
            if (NeedsSelection(particle, brushRadius, FalloffCurve.Constant, BrushDistanceMode.Surface, 0f, false, default, 0f))
            {
                SelectBrush(particle, brushRadius, FalloffCurve.Constant, BrushDistanceMode.Surface, 0f, false, default, 0f);
            }
            SetMotion(AffineMotion.FromTranslation(target - _origin - _grabOrigin));
        }

        /// <summary>
        /// 粒子をつまみ、範囲を motion で動かす。実際に動くのは次の Solve。
        /// 範囲の粒子 i の目標は、範囲を選んだときの位置 start_i から、重み w_i の分だけ動きに近づけた位置
        /// start_i + w_i * (motion.Apply(start_i) - start_i)。motion はいつも、範囲を選んだときの位置に対する動き。
        ///
        /// 中心の粒子・半径・距離の測り方・カーブ・鏡の面のどれかが前回と違うときだけ、範囲を選び直す（このときだけ重い）。
        /// 同じなら目標を計算し直すだけ（範囲の粒子の数に比例）。
        ///
        /// 鏡の面を渡すと、中心の粒子の鏡像（<see cref="FindMirrorParticle"/>）も範囲の中心にし、各粒子は近いほうの中心に属する。
        /// 鏡像に属する粒子は、鏡に映した動き（鏡映 ∘ motion ∘ 鏡映）で動く。
        /// 鏡像が中心から（距離の測り方で）ブラシ半径の2倍以内にあるとき（中央線の上をつまんだとき）は鏡像を使わず、
        /// 左右対称が崩れないよう、動きを鏡の面に沿う成分だけにする（平行移動と中心は面に沿う成分、回転は面の法線まわりの成分）。
        /// 鏡像が見つからないときは、対称にせず中心の側だけを動かす（<see cref="MirrorParticle"/> が -1）。
        /// </summary>
        /// <param name="options">範囲の決め方。null なら半径 0（中心の1点だけ）</param>
        public void Grab(int particle, BrushMotion motion, BrushOptions options)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            float radius = options != null ? options.Radius : 0f;
            if (!(radius >= 0f) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(options), "半径が不正です。");
            FalloffCurve curve = options?.Falloff ?? FalloffCurve.Constant;
            BrushDistanceMode mode = options != null ? options.DistanceMode : BrushDistanceMode.Surface;
            MirrorPlane? mirror = options?.Mirror;
            bool hasMirror = mirror.HasValue;
            MirrorPlane plane = hasMirror ? mirror.Value : default;
            if (hasMirror && !plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(options));
            float tolerance = options != null ? options.MirrorTolerance : BrushOptions.DefaultMirrorTolerance;
            if (hasMirror && !(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(options), "鏡像を探す許容距離が不正です。");
            float connect = options != null ? options.ConnectDistance : 0f;
            if (!(connect >= 0f) || float.IsInfinity(connect)) throw new ArgumentOutOfRangeException(nameof(options), "つながりの許容距離が不正です。");

            if (NeedsSelection(particle, radius, curve, mode, connect, hasMirror, plane, tolerance))
            {
                SelectBrush(particle, radius, curve, mode, connect, hasMirror, plane, tolerance);
            }
            if (_midline) motion = SymmetrizeMotion(motion, _mirrorPlane);
            SetMotion(AffineMotion.FromBrushMotion(motion, _origin));
        }

        /// <summary>
        /// 渡した粒子の集合をつまみ、motion で動かす（「頂点を選んで動かす」用）。実際に動くのは次の Solve。
        /// 集合の粒子はすべて重み 1。options の Radius が正なら、集合の端から Radius 以内の粒子も、
        /// 集合からの距離（DistanceMode の測り方）に応じた Falloff の重みでつまむ（ソフト選択）。Radius が 0 なら集合だけ。
        /// 鏡の面を渡すと、集合の各粒子の鏡像（<see cref="FindMirrorParticle"/>）も鏡に映した動きで動かす。
        /// 集合の半分以上の鏡像が集合の中にある（中央線をまたいで両側を選んだ）ときは、鏡像を使わず、
        /// 中心の粒子 1 つでつまむときと同じく、動きを鏡の面に沿う成分だけにする。
        /// 集合と options が前回と同じなら、範囲を選び直さずに目標だけを計算し直す。
        /// </summary>
        /// <param name="particles">つまむ粒子（重複してよい）。空は不可</param>
        /// <param name="options">null なら半径 0・対称なし</param>
        public void Grab(int[] particles, BrushMotion motion, BrushOptions options)
        {
            if (particles == null) throw new ArgumentNullException(nameof(particles));
            if (particles.Length == 0) throw new ArgumentException("粒子がありません。", nameof(particles));
            int n = _positions.Length;
            foreach (int particle in particles)
            {
                if ((uint)particle >= (uint)n) throw new ArgumentOutOfRangeException(nameof(particles));
            }
            float radius = options != null ? options.Radius : 0f;
            if (!(radius >= 0f) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(options), "半径が不正です。");
            FalloffCurve curve = options?.Falloff ?? FalloffCurve.Constant;
            BrushDistanceMode mode = options != null ? options.DistanceMode : BrushDistanceMode.Surface;
            MirrorPlane? mirror = options?.Mirror;
            bool hasMirror = mirror.HasValue;
            MirrorPlane plane = hasMirror ? mirror.Value : default;
            if (hasMirror && !plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(options));
            float tolerance = options != null ? options.MirrorTolerance : BrushOptions.DefaultMirrorTolerance;
            if (hasMirror && !(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(options), "鏡像を探す許容距離が不正です。");
            float connect = options != null ? options.ConnectDistance : 0f;
            if (!(connect >= 0f) || float.IsInfinity(connect)) throw new ArgumentOutOfRangeException(nameof(options), "つながりの許容距離が不正です。");

            if (NeedsSetSelection(particles, radius, curve, mode, connect, hasMirror, plane, tolerance))
            {
                SelectSet(particles, radius, curve, mode, connect, hasMirror, plane, tolerance);
            }
            if (_midline) motion = SymmetrizeMotion(motion, _mirrorPlane);
            SetMotion(AffineMotion.FromBrushMotion(motion, _origin));
        }

        bool NeedsSetSelection(int[] particles, float radius, FalloffCurve curve, BrushDistanceMode mode, float connect, bool hasMirror, MirrorPlane plane, float tolerance)
        {
            if (_grabbed < 0 || _selectionCount != particles.Length) return true;
            if (radius != _brushRadius || mode != _brushMode || connect != _brushConnect || hasMirror != _hasMirror || !curve.SameAs(_brushCurve)) return true;
            if (hasMirror && (plane.Point != _mirrorPlane.Point || plane.Normal != _mirrorPlane.Normal || tolerance != _mirrorTolerance)) return true;
            for (int k = 0; k < particles.Length; k++)
            {
                if (particles[k] != _selectionSet[k]) return true;
            }
            return false;
        }

        // 集合でつまむ範囲を選ぶ。集合（と鏡像）を起点に距離を測り、範囲の粒子・重み・属する側を決める
        void SelectSet(int[] particles, float radius, FalloffCurve curve, BrushDistanceMode mode, float connect, bool hasMirror, MirrorPlane plane, float tolerance)
        {
            for (int k = 0; k < _brushCount; k++)
            {
                int i = _brushList[k];
                _inBrush[i] = false;
                _brushWeight[i] = 0f;
                _brushMirrored[i] = false;
            }
            _brushCount = 0;

            int n = _positions.Length;
            if (_selectionMark == null)
            {
                _selectionMark = new bool[n];
                _selectionSources = new int[n];
            }
            if (_selectionSet.Length != particles.Length) _selectionSet = new int[particles.Length];
            Array.Copy(particles, _selectionSet, particles.Length);
            _selectionCount = particles.Length;
            _brushRadius = radius;
            _brushCurve = curve;
            _brushMode = mode;
            _brushConnect = connect;
            _hasMirror = hasMirror;
            _mirrorPlane = plane;
            _mirrorTolerance = tolerance;

            // 集合（重複を除く）
            bool[] mark = _selectionMark;
            int[] sources = _selectionSources;
            int setCount = 0;
            foreach (int particle in particles)
            {
                if (mark[particle]) continue;
                mark[particle] = true;
                sources[setCount++] = particle;
            }

            // 鏡像。集合の外にある鏡像だけを、鏡に映した動きで動かす起点にする（加えた鏡像の印は、範囲を選ぶまでの間 _brushMirrored に付ける）
            int sourceCount = setCount;
            int mirrorSeed = -1;
            bool midline = false;
            if (hasMirror)
            {
                sourceCount = AddSetMirrors(sources, setCount, mark, _brushMirrored, plane, tolerance, out midline);
                if (!midline && sourceCount > setCount) mirrorSeed = sources[setCount];
            }

            // 起点からの距離。範囲の距離はテザーの距離の配列を作業場所として使う（あとで範囲全体からの距離で上書きする）
            float[] distance = _grabDistance;
            int[] nearest = _grabSource;
            Vector3[] p = _positions;
            ComputeSetDistances(sources, sourceCount, radius, mode, connect, distance, nearest);

            // 印を、集合から鏡像の起点に付け替える（範囲の粒子が、どちらの起点に属するかを見るため）
            for (int k = 0; k < setCount; k++) mark[sources[k]] = false;
            for (int k = setCount; k < sourceCount; k++) mark[sources[k]] = true;
            for (int i = 0; i < n; i++)
            {
                float d = distance[i];
                if (!(d <= radius)) continue;
                float w = d <= 0f ? 1f : curve.Evaluate(d / radius);
                if (!(w > 0f)) continue;
                _brushList[_brushCount++] = i;
                _inBrush[i] = true;
                _brushWeight[i] = w;
                _brushMirrored[i] = mirrorSeed >= 0 && nearest[i] >= 0 && mark[nearest[i]];
                _brushStart[i] = p[i];
                _brushGoal[i] = p[i];
                _brushApplied[i] = p[i];
                _brushLambda[i] = Vector3.Zero;
                _goalAnchor[i] = _restAnchor[i];
            }
            for (int k = setCount; k < sourceCount; k++) mark[sources[k]] = false;

            _grabbed = particles[0];
            _clampedCount = 0;
            _mirrorSeed = mirrorSeed;
            _midline = midline;
            _grabOrigin = p[_grabbed];
            _graph.ShortestDistances(_brushList, _brushCount, _grabDistance, _grabSource, float.PositiveInfinity);
            _activePull = 0f;
            _brushPull = 0f;
            _motionValid = false;
            _spreadPending = false;
            _activeDirty = true;
        }

        // 集合の粒子 sources[0..setCount) の鏡像を、sources の後ろに足す。足した数まで含めた起点の数を返す。
        // mark は集合の印（呼ぶ前に true にしておく）、mirrorFlag は足した鏡像を重複なく数えるための作業用（呼ぶ前後で全部 false）。
        // 集合の半分以上の鏡像が集合の中にあるとき（中央線をまたいだ）は midline が true になり、鏡像は足さない
        int AddSetMirrors(int[] sources, int setCount, bool[] mark, bool[] mirrorFlag, MirrorPlane plane, float tolerance, out bool midline)
        {
            int sourceCount = setCount;
            int inside = 0;
            for (int k = 0; k < setCount; k++)
            {
                int m = FindMirrorNear(sources[k], plane, tolerance);
                if (m < 0) continue;
                if (mark[m])
                {
                    inside++;
                    continue;
                }
                if (mirrorFlag[m]) continue;
                mirrorFlag[m] = true;
                sources[sourceCount++] = m;
            }
            midline = 2 * inside >= setCount;
            for (int k = setCount; k < sourceCount; k++) mirrorFlag[sources[k]] = false;
            return midline ? setCount : sourceCount;
        }

        // 起点 sources からの距離と、一番近い起点を求める（範囲 radius の外は届かなくてよい）。radius が 0 なら起点だけが距離 0。
        // nearest は null でもよい
        void ComputeSetDistances(int[] sources, int sourceCount, float radius, BrushDistanceMode mode, float connect, float[] distance, int[] nearest)
        {
            int n = _positions.Length;
            Vector3[] p = _positions;
            if (radius > 0f && mode == BrushDistanceMode.Surface)
            {
                RangeGraph(connect).ShortestDistances(sources, sourceCount, distance, nearest, radius);
            }
            else if (radius > 0f)
            {
                if (_queryWork == null) _queryWork = new List<int>();
                var grid = new PointGrid(radius);
                for (int k = 0; k < sourceCount; k++) grid.Add(sources[k], p[sources[k]]);
                for (int i = 0; i < n; i++)
                {
                    distance[i] = float.PositiveInfinity;
                    if (nearest != null) nearest[i] = -1;
                    grid.Query(p[i], radius, _queryWork);
                    for (int c = 0; c < _queryWork.Count; c++)
                    {
                        int source = _queryWork[c];
                        float d = MathF.Sqrt(DistanceSquared(p[i].X, p[i].Y, p[i].Z, p[source]));
                        if (d < distance[i])
                        {
                            distance[i] = d;
                            if (nearest != null) nearest[i] = source;
                        }
                    }
                }
                for (int k = 0; k < sourceCount; k++)
                {
                    distance[sources[k]] = 0f;
                    if (nearest != null) nearest[sources[k]] = sources[k];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    distance[i] = float.PositiveInfinity;
                    if (nearest != null) nearest[i] = -1;
                }
                for (int k = 0; k < sourceCount; k++)
                {
                    distance[sources[k]] = 0f;
                    if (nearest != null) nearest[sources[k]] = sources[k];
                }
            }
        }

        /// <summary>
        /// 粒子の集合をつまんだとき（<see cref="Grab(int[], BrushMotion, BrushOptions)"/>）に範囲に入る粒子と重みを調べる。
        /// つまんだ状態は変えない。範囲と重みは Grab と同じ決め方で、集合の粒子は重み 1、
        /// options.Radius 以内の粒子は集合からの距離（DistanceMode の測り方）に応じた Falloff の重み、それより外は 0（含めない）。
        /// options.Mirror があれば、集合の鏡像の側の範囲も含める（中央線をまたぐ集合では鏡像を使わない、も Grab と同じ）。
        /// 範囲の粒子が maxCount より多いときは、粒子の番号順に一定の間隔で間引いて maxCount 以下にする（重みでは選ばない）。
        /// 選択が変わったときだけ呼ぶ想定で、呼ぶたびに配列を確保する。
        /// </summary>
        /// <param name="particles">集合（重複してよい）。空なら 0 を返す</param>
        /// <param name="options">null なら半径 0・対称なし</param>
        /// <param name="indices">範囲の粒子の番号（出力、番号の昇順）</param>
        /// <param name="weights">範囲の中の重み（出力、indices と同じ順）</param>
        /// <param name="maxCount">書き込む数の上限</param>
        /// <returns>書き込んだ数</returns>
        public int CollectSetRange(int[] particles, BrushOptions options, int[] indices, float[] weights, int maxCount)
        {
            if (particles == null) throw new ArgumentNullException(nameof(particles));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            int n = _positions.Length;
            foreach (int particle in particles)
            {
                if ((uint)particle >= (uint)n) throw new ArgumentOutOfRangeException(nameof(particles));
            }
            float radius = options != null ? options.Radius : 0f;
            if (!(radius >= 0f) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(options), "半径が不正です。");
            FalloffCurve curve = options?.Falloff ?? FalloffCurve.Constant;
            BrushDistanceMode mode = options != null ? options.DistanceMode : BrushDistanceMode.Surface;
            MirrorPlane? mirror = options?.Mirror;
            bool hasMirror = mirror.HasValue;
            MirrorPlane plane = hasMirror ? mirror.Value : default;
            if (hasMirror && !plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(options));
            float tolerance = options != null ? options.MirrorTolerance : BrushOptions.DefaultMirrorTolerance;
            if (hasMirror && !(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(options), "鏡像を探す許容距離が不正です。");
            float connect = options != null ? options.ConnectDistance : 0f;
            if (!(connect >= 0f) || float.IsInfinity(connect)) throw new ArgumentOutOfRangeException(nameof(options), "つながりの許容距離が不正です。");
            maxCount = Math.Min(maxCount, Math.Min(indices.Length, weights.Length));
            if (maxCount <= 0 || particles.Length == 0) return 0;

            // 作業用の配列は、つまんでいる状態を壊さないよう、この呼び出しの中だけで確保する
            var mark = new bool[n];
            var sources = new int[n];
            int setCount = 0;
            foreach (int particle in particles)
            {
                if (mark[particle]) continue;
                mark[particle] = true;
                sources[setCount++] = particle;
            }
            int sourceCount = setCount;
            if (hasMirror) sourceCount = AddSetMirrors(sources, setCount, mark, new bool[n], plane, tolerance, out _);

            if (_workDistance == null) _workDistance = new float[n];
            float[] distance = _workDistance;
            ComputeSetDistances(sources, sourceCount, radius, mode, connect, distance, null);

            int total = 0;
            for (int i = 0; i < n; i++)
            {
                float d = distance[i];
                if (!(d <= radius)) continue;
                if (d <= 0f || curve.Evaluate(d / radius) > 0f) total++;
            }
            int stride = total <= maxCount ? 1 : (total + maxCount - 1) / maxCount;
            int written = 0, seen = 0;
            for (int i = 0; i < n && written < maxCount; i++)
            {
                float d = distance[i];
                if (!(d <= radius)) continue;
                float w = d <= 0f ? 1f : curve.Evaluate(d / radius);
                if (!(w > 0f)) continue;
                if (seen++ % stride != 0) continue;
                indices[written] = i;
                weights[written] = w;
                written++;
            }
            return written;
        }

        // 範囲を決めるときに使うグラフ。connect（ε）が正なら、元の位置で ε 以内にある別の島の粒子どうしも、その距離の辺でつなぐ。
        // 同じ島の中は、メッシュに沿った距離のまま（折り返したひだを近道しないため）。ε が変わったときだけ作り直す
        MeshGraph RangeGraph(float connect)
        {
            if (!(connect > 0f)) return _graph;
            if (_rangeGraph != null && _rangeGraphConnect == connect) return _rangeGraph;
            EnsureComponents();
            Vector3[] rest = _model.RestPositions;
            int n = rest.Length;
            var pairs = new List<int>(_model.StretchPairs);
            var lengths = new List<float>(_model.StretchRestLengths);
            var grid = new PointGrid(connect);
            for (int i = 0; i < n; i++) grid.Add(i, rest[i]);
            var near = new List<int>();
            float limit = connect * connect;
            for (int i = 0; i < n; i++)
            {
                grid.Query(rest[i], connect, near);
                for (int c = 0; c < near.Count; c++)
                {
                    int j = near[c];
                    if (j <= i || _components[j] == _components[i]) continue;
                    float sq = DistanceSquared(rest[i].X, rest[i].Y, rest[i].Z, rest[j]);
                    if (sq > limit) continue;
                    pairs.Add(i);
                    pairs.Add(j);
                    lengths.Add(MathF.Sqrt(sq));
                }
            }
            _rangeGraph = new MeshGraph(n, pairs.ToArray(), lengths.ToArray());
            _rangeGraphConnect = connect;
            return _rangeGraph;
        }

        // FindMirrorParticle と同じ選び方を、元の位置の格子で近くの粒子だけ調べて行う（集合の粒子ごとに全粒子を調べると遅いため）
        int FindMirrorNear(int particle, MirrorPlane plane, float tolerance)
        {
            EnsureComponents();
            Vector3[] rest = _model.RestPositions;
            EnsureMirrorGrid(tolerance);

            Vector3 target = plane.Reflect(rest[particle]);
            Vector3 normal = _restNormals != null ? plane.ReflectDirection(_restNormals[particle]) : Vector3.Zero;
            bool useNormal = normal != Vector3.Zero;
            int island = _components[particle];
            float limit = tolerance * tolerance;
            int bestSame = -1, bestOther = -1;
            float bestSameSq = float.MaxValue, bestOtherSq = float.MaxValue;
            _mirrorGrid.Query(target, tolerance, _queryWork);
            for (int c = 0; c < _queryWork.Count; c++)
            {
                int j = _queryWork[c];
                float sq = DistanceSquared(rest[j].X, rest[j].Y, rest[j].Z, target);
                if (sq > limit) continue;
                if (_components[j] == island)
                {
                    if (sq < bestSameSq || (sq == bestSameSq && j < bestSame))
                    {
                        bestSameSq = sq;
                        bestSame = j;
                    }
                }
                else if (useNormal && Vector3.Dot(_restNormals[j], normal) > 0.5f && (sq < bestOtherSq || (sq == bestOtherSq && j < bestOther)))
                {
                    bestOtherSq = sq;
                    bestOther = j;
                }
            }
            return bestSame >= 0 ? bestSame : bestOther;
        }

        // 元の位置の格子（鏡像を探す用）。セルの大きさが変わったときだけ作り直す
        void EnsureMirrorGrid(float tolerance)
        {
            float cell = MathF.Max(tolerance, 1e-4f);
            if (_mirrorGrid == null || _mirrorGridCell != cell)
            {
                Vector3[] rest = _model.RestPositions;
                _mirrorGrid = new PointGrid(cell);
                for (int i = 0; i < rest.Length; i++) _mirrorGrid.Add(i, rest[i]);
                _mirrorGridCell = cell;
            }
            if (_queryWork == null) _queryWork = new List<int>();
        }

        // 鏡像の側の変形を、鏡に映した元の位置 q のまわりで補って求め、鏡に映して返す。
        // 一番近い1個だけを写すと、粒子の並びが左右でそろっていないため、変形が急に変わる所（つまんだ範囲の縁）で数 mm〜数 cm の段差が出る。
        // そこで、鏡像の粒子 m と同じ島の、q から tolerance 以内の近い粒子（最大 4 個）の変形を、距離の逆二乗の重みで平均する
        Vector3 ReflectedDisplacement(int i, int m, MirrorPlane plane, float tolerance)
        {
            Vector3[] rest = _model.RestPositions;
            Vector3 q = plane.Reflect(rest[i]);
            int island = _components[m];
            EnsureMirrorGrid(tolerance);
            _mirrorGrid.Query(q, MathF.Max(tolerance, 1e-4f), _queryWork);
            const int K = 4;
            int count = 0;
            for (int c = 0; c < _queryWork.Count; c++)
            {
                int j = _queryWork[c];
                if (_components[j] != island) continue;
                float sq = DistanceSquared(rest[j].X, rest[j].Y, rest[j].Z, q);
                if (count < K)
                {
                    _nearIndex[count] = j;
                    _nearSq[count] = sq;
                    count++;
                }
                else
                {
                    int worst = 0;
                    for (int k = 1; k < K; k++)
                    {
                        if (_nearSq[k] > _nearSq[worst]) worst = k;
                    }
                    if (sq >= _nearSq[worst]) continue;
                    _nearIndex[worst] = j;
                    _nearSq[worst] = sq;
                }
            }
            float dx, dy, dz;
            if (count == 0)
            {
                dx = _worldPositions[m].X - rest[m].X;
                dy = _worldPositions[m].Y - rest[m].Y;
                dz = _worldPositions[m].Z - rest[m].Z;
            }
            else
            {
                float wx = 0f, wy = 0f, wz = 0f, total = 0f;
                for (int k = 0; k < count; k++)
                {
                    int j = _nearIndex[k];
                    float w = 1f / (_nearSq[k] + 1e-10f);
                    wx += w * (_worldPositions[j].X - rest[j].X);
                    wy += w * (_worldPositions[j].Y - rest[j].Y);
                    wz += w * (_worldPositions[j].Z - rest[j].Z);
                    total += w;
                }
                dx = wx / total;
                dy = wy / total;
                dz = wz / total;
            }
            Vector3 n = plane.Normal;
            float along = 2f * (dx * n.X + dy * n.Y + dz * n.Z);
            return new Vector3(dx - along * n.X, dy - along * n.Y, dz - along * n.Z);
        }

        bool NeedsSelection(int particle, float radius, FalloffCurve curve, BrushDistanceMode mode, float connect, bool hasMirror, MirrorPlane plane, float tolerance)
        {
            if (_selectionCount >= 0 || particle != _grabbed || radius != _brushRadius || mode != _brushMode || connect != _brushConnect || hasMirror != _hasMirror) return true;
            if (!curve.SameAs(_brushCurve)) return true;
            return hasMirror && (plane.Point != _mirrorPlane.Point || plane.Normal != _mirrorPlane.Normal || tolerance != _mirrorTolerance);
        }

        // 範囲を選び直す。範囲の粒子・重み・属する中心を決め、範囲全体からのメッシュに沿った距離（テザーと計算範囲に使う）を求める
        void SelectBrush(int seed, float radius, FalloffCurve curve, BrushDistanceMode mode, float connect, bool hasMirror, MirrorPlane plane, float tolerance)
        {
            for (int k = 0; k < _brushCount; k++)
            {
                int i = _brushList[k];
                _inBrush[i] = false;
                _brushWeight[i] = 0f;
                _brushMirrored[i] = false;
            }
            _brushCount = 0;

            _selectionCount = -1;
            _grabbed = seed;
            _brushRadius = radius;
            _brushCurve = curve;
            _brushMode = mode;
            _brushConnect = connect;
            _hasMirror = hasMirror;
            _mirrorPlane = plane;
            _mirrorTolerance = tolerance;

            int mirror = hasMirror ? FindMirrorParticle(seed, plane, tolerance) : -1;
            bool midline = hasMirror && mirror == seed;
            if (midline) mirror = -1;

            // 範囲を選ぶ距離は、テザーの距離の配列を作業場所として使う（あとで範囲全体からの距離で上書きする）
            float[] distance = _grabDistance;
            int[] nearest = _grabSource;
            int n = _positions.Length;
            Vector3[] p = _positions;
            if (mode == BrushDistanceMode.Surface)
            {
                // メッシュに沿った距離で選ぶので、近くにあってもつながっていない布（重ね着など）はつままない（ε 以内の別の島は除く）
                MeshGraph graph = RangeGraph(connect);
                _seedWork[0] = seed;
                bool done = false;
                if (mirror >= 0)
                {
                    graph.ShortestDistances(_seedWork, 1, distance, null, 2f * radius);
                    if (distance[mirror] <= 2f * radius)
                    {
                        midline = true;
                        mirror = -1;
                        done = true;   // 半径の2倍まで求めてあるので、半径以内の距離はこのまま使える
                    }
                }
                if (mirror >= 0)
                {
                    _seedWork[1] = mirror;
                    graph.ShortestDistances(_seedWork, 2, distance, nearest, radius);
                }
                else if (!done)
                {
                    graph.ShortestDistances(_seedWork, 1, distance, null, radius);
                }
            }
            else
            {
                Vector3 a = p[seed];
                if (mirror >= 0 && Vector3.Distance(a, p[mirror]) <= 2f * radius)
                {
                    midline = true;
                    mirror = -1;
                }
                Vector3 b = mirror >= 0 ? p[mirror] : a;
                for (int i = 0; i < n; i++)
                {
                    float da = MathF.Sqrt(DistanceSquared(p[i].X, p[i].Y, p[i].Z, a));
                    float db = mirror >= 0 ? MathF.Sqrt(DistanceSquared(p[i].X, p[i].Y, p[i].Z, b)) : float.PositiveInfinity;
                    distance[i] = MathF.Min(da, db);
                    nearest[i] = db < da ? mirror : seed;
                }
                // 中心は自分自身からの距離 0（鏡像と同じ位置でも、それぞれの中心に属する）
                distance[seed] = 0f;
                nearest[seed] = seed;
                if (mirror >= 0)
                {
                    distance[mirror] = 0f;
                    nearest[mirror] = mirror;
                }
            }

            for (int i = 0; i < n; i++)
            {
                float d = distance[i];
                if (!(d <= radius)) continue;
                // 中心（距離 0）は、カーブによらず重み 1
                float w = d <= 0f ? 1f : curve.Evaluate(d / radius);
                if (!(w > 0f)) continue;
                _brushList[_brushCount++] = i;
                _inBrush[i] = true;
                _brushWeight[i] = w;
                _brushMirrored[i] = mirror >= 0 && nearest[i] == mirror;
                _brushStart[i] = p[i];
                _brushGoal[i] = p[i];
                _brushApplied[i] = p[i];
                _brushLambda[i] = Vector3.Zero;
                _goalAnchor[i] = _restAnchor[i];
            }

            _clampedCount = 0;
            _mirrorSeed = mirror;
            _midline = midline;
            _grabOrigin = p[seed];
            _graph.ShortestDistances(_brushList, _brushCount, _grabDistance, _grabSource, float.PositiveInfinity);
            _activePull = 0f;
            _brushPull = 0f;
            _motionValid = false;
            _spreadPending = false;
            _activeDirty = true;
        }

        // 目標を、近くの元の面より（入ってよい深さを超えて）内側に入らないよう押し出す
        bool KeepGoalOutside(int i)
        {
            Vector3 g = _brushGoal[i];
            int anchor = WalkToNearestRest(_goalAnchor[i], g.X, g.Y, g.Z);
            _goalAnchor[i] = anchor;
            Vector3 n = _restNormals[anchor];
            Vector3 r = _restInternal[anchor];
            float d = (g.X - r.X) * n.X + (g.Y - r.Y) * n.Y + (g.Z - r.Z) * n.Z;
            if (_restAllowance != null) d += _restAllowance[anchor];
            if (d >= 0f) return false;
            _brushGoal[i].X = g.X - d * n.X;
            _brushGoal[i].Y = g.Y - d * n.Y;
            _brushGoal[i].Z = g.Z - d * n.Z;
            return true;
        }

        // 目標を押し出した粒子は、粒子そのものも元の面の外へ出す。柔らかいつまみでは、周りの布に引かれて目標から内側へずれるため。
        // 範囲の全部ではなくこの粒子だけにするのは、外へ引いたとき（目標が面の外）の柔らかいつまみの譲り方を変えないため。
        // カーネル（Burst）を変えずに済むよう、Solve の最後に1回だけ行う
        void PushClampedBrushOutside()
        {
            if (_grabbed < 0) return;
            Vector3[] p = _positions;
            for (int k = 0; k < _clampedCount; k++)
            {
                int i = _clampedBrush[k];
                if (_pinned[i]) continue;
                int anchor = _goalAnchor[i];
                Vector3 n = _restNormals[anchor];
                Vector3 r = _restInternal[anchor];
                float d = (p[i].X - r.X) * n.X + (p[i].Y - r.Y) * n.Y + (p[i].Z - r.Z) * n.Z;
                if (_restAllowance != null) d += _restAllowance[anchor];
                if (d >= 0f) continue;
                p[i].X -= d * n.X;
                p[i].Y -= d * n.Y;
                p[i].Z -= d * n.Z;
                _worldPositions[i].X = p[i].X + _origin.X;
                _worldPositions[i].Y = p[i].Y + _origin.Y;
                _worldPositions[i].Z = p[i].Z + _origin.Z;
            }
        }

        // anchor から、伸びの制約でつながった隣へ、点に近くなる限りたどる（UpdateRestAnchors と同じ探し方）
        int WalkToNearestRest(int anchor, float px, float py, float pz)
        {
            int[] offsets = _graph.Offsets;
            int[] neighbors = _graph.Neighbors;
            Vector3[] rest = _restInternal;
            float best = DistanceSquared(px, py, pz, rest[anchor]);
            // 目標は1回で大きく動くことがあるので、Solve ごとの探し直し（16歩）より多めにたどる
            for (int step = 0; step < 64; step++)
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
            return anchor;
        }

        // 範囲の粒子ごとの目標を計算し直す。動きが前回と同じなら何もしない（λ を引き継ぐ）
        void SetMotion(in AffineMotion motion)
        {
            AffineMotion mirrored = _mirrorSeed >= 0 ? motion.Mirrored(_mirrorPlane.Point - _origin, _mirrorPlane.Normal) : default;
            if (_motionValid && motion.Equals(_motion) && mirrored.Equals(_mirrorMotion)) return;
            _motion = motion;
            _mirrorMotion = mirrored;
            _motionValid = true;

            // 回す・縮めると、範囲の一部の目標が元の形より内側（体や内側の布の中）に入る。
            // 範囲の粒子そのものに押し戻しをかけると、届かない所へ引いたときに柔らかいつまみが譲れなくなって伸びが増えるので、目標の方を外に出す
            bool keepOutside = KeepOutsideRestSurface && _restNormals != null;
            _clampedCount = 0;
            float pullSquared = 0f;
            for (int k = 0; k < _brushCount; k++)
            {
                int i = _brushList[k];
                AffineMotion m = _brushMirrored[i] ? mirrored : motion;
                Vector3 s = _brushStart[i];
                float w = _brushWeight[i];
                float dx = (m.L00 * s.X + m.L01 * s.Y + m.L02 * s.Z + m.Bx) * w;
                float dy = (m.L10 * s.X + m.L11 * s.Y + m.L12 * s.Z + m.By) * w;
                float dz = (m.L20 * s.X + m.L21 * s.Y + m.L22 * s.Z + m.Bz) * w;
                _brushGoal[i].X = s.X + dx;
                _brushGoal[i].Y = s.Y + dy;
                _brushGoal[i].Z = s.Z + dz;
                if (keepOutside && KeepGoalOutside(i)) _clampedBrush[_clampedCount++] = i;
                pullSquared = MathF.Max(pullSquared, dx * dx + dy * dy + dz * dz);
                // ドラッグ中（目標が動いている間）は λ をためない。ためると、一時的にかかった力の分だけ、
                // 届く目標でも遅れが残る。手を止めてから、届かない分だけ少しずつ譲る
                _brushLambda[i] = Vector3.Zero;
            }
            _brushPull = MathF.Sqrt(pullSquared);
            _spreadPending = true;
        }

        // 動きを、鏡の面に沿う成分だけにする（鏡映と入れ替えても変わらない動きにする）
        static BrushMotion SymmetrizeMotion(BrushMotion motion, MirrorPlane plane)
        {
            Vector3 n = plane.Normal;
            Vector3 translation = motion.Translation;
            translation -= Vector3.Dot(translation, n) * n;
            Vector3 pivot = motion.Pivot;
            pivot -= plane.SignedDistance(pivot) * n;
            // 回転は、法線まわりのひねりの成分だけを残す（swing-twist 分解）
            Quaternion q = motion.NormalizedRotation();
            float along = q.X * n.X + q.Y * n.Y + q.Z * n.Z;
            var twist = new Quaternion(n * along, q.W);
            float length = twist.Length();
            twist = length > 1e-6f ? Quaternion.Multiply(twist, 1f / length) : Quaternion.Identity;
            var result = new BrushMotion(translation, twist, motion.Scale, pivot);
            if (motion.HasAxisScale) ApplySymmetricAxisScale(ref result, motion, n);
            return result;
        }

        // 軸ごとの拡大は、拡大の軸が鏡の面の法線とそろっている（鏡映と入れ替えても変わらない）ときだけ残す。
        // そろっていないと、鏡像の側と食い違って中央線で崩れるので、軸の倍率の平均で一様に拡大する
        static void ApplySymmetricAxisScale(ref BrushMotion result, in BrushMotion motion, Vector3 n)
        {
            motion.GetScaleMatrix(out float m00, out float m01, out float m02, out float m10, out float m11, out float m12,
                out float m20, out float m21, out float m22);
            // M が H = I - 2nnᵀ と入れ替わる ⇔ M n が n に平行
            var mn = new Vector3(m00 * n.X + m01 * n.Y + m02 * n.Z, m10 * n.X + m11 * n.Y + m12 * n.Z, m20 * n.X + m21 * n.Y + m22 * n.Z);
            float along = Vector3.Dot(mn, n);
            Vector3 across = mn - along * n;
            if (across.LengthSquared() <= 1e-8f * MathF.Max(1f, mn.LengthSquared()))
            {
                result.ScaleAxes = motion.ScaleAxes;
                result.ScaleFrame = motion.ScaleFrame;
                return;
            }
            Vector3 axes = motion.ScaleAxes;
            result.Scale = motion.Scale * (axes.X + axes.Y + axes.Z) / 3f;
        }

        /// <summary>
        /// 粒子の鏡像の粒子を探す。元の位置を鏡に映した点から tolerance 以内の粒子のうち、
        /// 同じ島（伸びの制約でつながった部品）の最も近い粒子を選ぶ。無ければ、元の形の面の外向きを鏡に映した向きと
        /// 内積が 0.5 より大きい、最も近い粒子（左右の袖のように、別の島に分かれた部品のため）。どちらも無ければ -1。
        /// 同じ島を優先するので、すぐ内側の別の布（3mm 内側のペチコートなど）には移らない。中央線の上の粒子は自分自身を返すことがある。
        /// </summary>
        public int FindMirrorParticle(int particle, MirrorPlane plane, float tolerance)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            if (!plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(plane));
            if (!(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            EnsureComponents();

            Vector3[] rest = _model.RestPositions;
            Vector3 target = plane.Reflect(rest[particle]);
            Vector3 normal = _restNormals != null ? plane.ReflectDirection(_restNormals[particle]) : Vector3.Zero;
            bool useNormal = normal != Vector3.Zero;
            int island = _components[particle];
            float limit = tolerance * tolerance;
            int bestSame = -1, bestOther = -1;
            float bestSameSq = float.MaxValue, bestOtherSq = float.MaxValue;
            for (int j = 0; j < rest.Length; j++)
            {
                float sq = DistanceSquared(rest[j].X, rest[j].Y, rest[j].Z, target);
                if (sq > limit) continue;
                if (_components[j] == island)
                {
                    if (sq < bestSameSq)
                    {
                        bestSameSq = sq;
                        bestSame = j;
                    }
                }
                else if (useNormal && sq < bestOtherSq && Vector3.Dot(_restNormals[j], normal) > 0.5f)
                {
                    bestOtherSq = sq;
                    bestOther = j;
                }
            }
            return bestSame >= 0 ? bestSame : bestOther;
        }

        /// <summary>
        /// すべての粒子の鏡像の粒子（<see cref="FindMirrorParticle"/> と同じ選び方）を result に入れる。見つからない粒子は -1。
        /// 元の位置の格子で近くだけを調べるので、粒子数に比例する時間で済む。相手のいない点の表示・左右差・「左右対称にする」に使う。
        /// </summary>
        /// <param name="result">粒子数以上の長さの配列</param>
        /// <returns>鏡像が見つからなかった粒子の数</returns>
        public int FindMirrorParticles(MirrorPlane plane, float tolerance, int[] result)
        {
            if (!plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(plane));
            if (!(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (result == null) throw new ArgumentNullException(nameof(result));
            int n = _positions.Length;
            if (result.Length < n) throw new ArgumentException("配列が粒子数より短いです。", nameof(result));
            int unmatched = 0;
            for (int i = 0; i < n; i++)
            {
                result[i] = FindMirrorNear(i, plane, tolerance);
                if (result[i] < 0) unmatched++;
            }
            return unmatched;
        }

        /// <summary>
        /// 粒子ごとの左右差（メートル）を result に入れる。粒子 i の変形（元の位置からの移動）と、鏡像の側の変形を鏡に映したものとの差の大きさ。
        /// 鏡像の側の変形は、i の元の位置を鏡に映した点のまわりの粒子（鏡像の粒子 m と同じ島、tolerance 以内の最大 4 個）から補う。
        /// 左右対称に変形していれば 0。鏡像が無い粒子（mirrorMap が負）は -1。
        /// </summary>
        /// <param name="mirrorMap"><see cref="FindMirrorParticles"/> の結果</param>
        public void MeasureMirrorDifference(int[] mirrorMap, MirrorPlane plane, float tolerance, float[] result)
        {
            if (mirrorMap == null) throw new ArgumentNullException(nameof(mirrorMap));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (!plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(plane));
            if (!(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            int n = _positions.Length;
            if (mirrorMap.Length < n || result.Length < n) throw new ArgumentException("配列が粒子数より短いです。");
            EnsureComponents();
            Vector3[] rest = _model.RestPositions;
            for (int i = 0; i < n; i++)
            {
                int m = mirrorMap[i];
                if ((uint)m >= (uint)n)
                {
                    result[i] = -1f;
                    continue;
                }
                Vector3 reflected = ReflectedDisplacement(i, m, plane, tolerance);
                float dx = _worldPositions[i].X - rest[i].X - reflected.X;
                float dy = _worldPositions[i].Y - rest[i].Y - reflected.Y;
                float dz = _worldPositions[i].Z - rest[i].Z - reflected.Z;
                result[i] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        /// <summary>
        /// 今の形を左右対称にする。基準の側（keepPositiveSide なら、元の位置が鏡の面の法線の向きにある側）の変形（元の位置からの移動）を、
        /// 鏡に映して反対側へ写す（写す変形は <see cref="MeasureMirrorDifference"/> と同じく、鏡に映した位置のまわりの粒子から補う）。
        /// 中央線の近く（元の位置が面から tolerance の半分以内）の粒子は、
        /// 自分の変形と鏡像の変形を鏡に映したものの平均にする（中央で左右がつながるように）。
        /// 鏡像が無い粒子とピン留めの粒子は動かさない。つまんでいる状態は解除する。布の計算はしないので、写したあとは仕上げ（Solve）で伸びを直す。
        /// </summary>
        /// <param name="mirrorMap"><see cref="FindMirrorParticles"/> の結果。null ならここで求める</param>
        /// <returns>位置を変えた粒子の数</returns>
        public int SymmetrizeShape(MirrorPlane plane, float tolerance, bool keepPositiveSide, int[] mirrorMap = null)
        {
            if (!plane.IsValid) throw new ArgumentException("鏡の面の法線が 0 ベクトルです。", nameof(plane));
            if (!(tolerance >= 0f)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            int n = _positions.Length;
            if (mirrorMap == null)
            {
                mirrorMap = new int[n];
                FindMirrorParticles(plane, tolerance, mirrorMap);
            }
            else if (mirrorMap.Length < n)
            {
                throw new ArgumentException("配列が粒子数より短いです。", nameof(mirrorMap));
            }

            EnsureComponents();
            Vector3[] rest = _model.RestPositions;
            float sign = keepPositiveSide ? 1f : -1f;
            float band = 0.5f * tolerance;
            var result = (Vector3[])_worldPositions.Clone();
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                int m = mirrorMap[i];
                if ((uint)m >= (uint)n || _pinned[i]) continue;
                float side = sign * plane.SignedDistance(rest[i]);
                if (side > band) continue;
                Vector3 reflected = ReflectedDisplacement(i, m, plane, tolerance);
                Vector3 target;
                if (MathF.Abs(side) <= band)
                {
                    target = rest[i] + 0.5f * (_worldPositions[i] - rest[i] + reflected);
                }
                else if (side < 0f && sign * plane.SignedDistance(rest[m]) >= -band)
                {
                    target = rest[i] + reflected;
                }
                else
                {
                    continue;
                }
                if (Vector3.DistanceSquared(target, _worldPositions[i]) <= 1e-14f) continue;
                result[i] = target;
                changed++;
            }
            SetInternalPositions(result);
            _grabbed = -1;
            _activeDirty = true;
            return changed;
        }

        /// <summary>
        /// 範囲内の粒子を、元の位置へ strength × 重み だけ近づける（部分的なリセット）。ピン留めした粒子は動かさない。
        /// つまんでいる間に呼ぶと、つまんだ範囲の粒子は次の Solve で目標へ戻ろうとする。
        /// </summary>
        /// <param name="particle">範囲の中心</param>
        /// <param name="radius">範囲の半径（メートル）。0 なら中心の1点だけ</param>
        /// <param name="strength">近づける割合（0〜1。1 で、中心は元の位置に戻る）</param>
        /// <param name="curve">範囲の中の重み。null なら <see cref="FalloffCurve.Constant"/></param>
        /// <param name="mode">範囲を決める距離の測り方（Linear は今の位置での直線距離）</param>
        /// <param name="connectDistance">つながりの許容距離 ε（<see cref="BrushOptions.ConnectDistance"/>）。Surface のときだけ使う</param>
        /// <returns>動かした粒子の数</returns>
        public int RelaxTowardRest(int particle, float radius, float strength, FalloffCurve curve, BrushDistanceMode mode, float connectDistance = 0f)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            if (!(radius >= 0f)) throw new ArgumentOutOfRangeException(nameof(radius));
            if (!(strength >= 0f)) throw new ArgumentOutOfRangeException(nameof(strength));
            if (strength > 1f) strength = 1f;
            if (curve == null) curve = FalloffCurve.Constant;
            int n = _positions.Length;
            if (mode == BrushDistanceMode.Surface)
            {
                if (_workDistance == null) _workDistance = new float[n];
                _seedWork[0] = particle;
                RangeGraph(connectDistance).ShortestDistances(_seedWork, 1, _workDistance, null, radius);
            }

            Vector3[] rest = _model.RestPositions;
            Vector3 center = _worldPositions[particle];
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (_pinned[i]) continue;
                Vector3 world = _worldPositions[i];
                float d = mode == BrushDistanceMode.Surface ? _workDistance[i] : MathF.Sqrt(DistanceSquared(world.X, world.Y, world.Z, center));
                if (i == particle) d = 0f;
                if (!(d <= radius)) continue;
                float w = d <= 0f ? 1f : curve.Evaluate(d / radius);
                float f = strength * w;
                if (!(f > 0f)) continue;
                if (f >= 1f)
                {
                    // 元の位置にぴったり戻す（内部の座標の丸め誤差を持ち込まない）
                    _worldPositions[i] = rest[i];
                    _positions[i] = _restInternal[i];
                }
                else
                {
                    world.X += (rest[i].X - world.X) * f;
                    world.Y += (rest[i].Y - world.Y) * f;
                    world.Z += (rest[i].Z - world.Z) * f;
                    _worldPositions[i] = world;
                    _positions[i] = world - _origin;
                }
                count++;
            }
            return count;
        }

        /// <summary>
        /// 中心の粒子にブラシを当てたとき、範囲に入る粒子と重みを調べる。つまんだ状態は変えない。
        /// 範囲と重みは <see cref="Grab(int, BrushMotion, BrushOptions)"/> と同じ決め方（対称の鏡像側は含めない）。
        /// ブラシを当てる前の表示用。距離の計算に使う作業場所は使い回し、出力は呼び出し側の配列に入れる。
        /// 範囲が maxCount より多いときは、一定の間隔で間引いて maxCount 以下にする（中心は必ず含める）。
        /// </summary>
        /// <param name="indices">範囲の粒子の番号（出力）</param>
        /// <param name="weights">範囲の中の重み（出力、indices と同じ順）</param>
        /// <param name="maxCount">書き込む数の上限</param>
        /// <param name="offset">配列のどこから書き込むか（鏡像側の範囲を続けて入れるため）</param>
        /// <param name="connectDistance">つながりの許容距離 ε（<see cref="BrushOptions.ConnectDistance"/>）。Surface のときだけ使う</param>
        /// <returns>書き込んだ数</returns>
        public int CollectBrushRange(int particle, float radius, FalloffCurve curve, BrushDistanceMode mode, int[] indices, float[] weights, int maxCount, int offset = 0,
            float connectDistance = 0f)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            if (!(radius >= 0f) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (curve == null) curve = FalloffCurve.Constant;
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            maxCount = Math.Min(maxCount, Math.Min(indices.Length, weights.Length) - offset);
            if (maxCount <= 0) return 0;

            int n = _positions.Length;
            if (_workDistance == null) _workDistance = new float[n];
            float[] distance = _workDistance;
            if (mode == BrushDistanceMode.Surface)
            {
                _seedWork[0] = particle;
                RangeGraph(connectDistance).ShortestDistances(_seedWork, 1, distance, null, radius);
            }
            else
            {
                Vector3 a = _worldPositions[particle];
                for (int i = 0; i < n; i++) distance[i] = MathF.Sqrt(DistanceSquared(_worldPositions[i].X, _worldPositions[i].Y, _worldPositions[i].Z, a));
            }
            distance[particle] = 0f;

            int total = 0;
            for (int i = 0; i < n; i++)
            {
                float d = distance[i];
                if (!(d <= radius)) continue;
                if (d <= 0f || curve.Evaluate(d / radius) > 0f) total++;
            }

            int stride = total <= maxCount ? 1 : (total + maxCount - 1) / maxCount;
            int written = 0, seen = 0;
            for (int i = 0; i < n && written < maxCount; i++)
            {
                float d = distance[i];
                if (!(d <= radius)) continue;
                float w = d <= 0f ? 1f : curve.Evaluate(d / radius);
                if (!(w > 0f)) continue;
                if (i != particle && seen++ % stride != 0) continue;
                indices[offset + written] = i;
                weights[offset + written] = w;
                written++;
            }
            return written;
        }

        /// <summary>
        /// 粒子ごとの伸び（0.1 で 10%）を result に入れる。その粒子につながる伸びの制約のうち、最も伸びているもの。縮んでいる分は 0。
        /// 伸びの見える化（ヒートマップ）用。制約の数に比例するので、形が変わったときだけ呼ぶこと。
        /// </summary>
        /// <param name="result">粒子数以上の長さの配列</param>
        public void MeasureParticleStretch(float[] result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            int n = _worldPositions.Length;
            if (result.Length < n) throw new ArgumentException("配列が粒子数より短いです。", nameof(result));
            Array.Clear(result, 0, n);

            int[] pairs = _model.StretchPairs;
            float[] rest = _model.StretchRestLengths;
            for (int k = 0; k < rest.Length; k++)
            {
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float dx = _worldPositions[i].X - _worldPositions[j].X;
                float dy = _worldPositions[i].Y - _worldPositions[j].Y;
                float dz = _worldPositions[i].Z - _worldPositions[j].Z;
                float stretch = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / rest[k] - 1f;
                if (stretch > result[i]) result[i] = stretch;
                if (stretch > result[j]) result[j] = stretch;
            }
        }

        public void Release()
        {
            _grabbed = -1;
            _activeDirty = true;
        }

        /// <summary>
        /// つまんだ範囲の粒子（なめらかに減る範囲と鏡像の側を含む）を、布の計算をせずに目標の位置へ置く（「形だけ動かす」用）。
        /// ピン留めの粒子は動かさない。範囲の外の粒子もそのまま。置いた数を返す。つまんでいなければ 0。
        /// 目標は <see cref="Grab(int[], BrushMotion, BrushOptions)"/> などで決まったもの（<see cref="KeepOutsideRestSurface"/> なら元の形の外に押し出した位置）。
        /// </summary>
        public int PlaceBrushAtGoals()
        {
            if (_grabbed < 0) return 0;
            int placed = 0;
            for (int k = 0; k < _brushCount; k++)
            {
                int i = _brushList[k];
                if (_pinned[i]) continue;
                Vector3 g = _brushGoal[i];
                _positions[i] = g;
                _worldPositions[i].X = g.X + _origin.X;
                _worldPositions[i].Y = g.Y + _origin.Y;
                _worldPositions[i].Z = g.Z + _origin.Z;
                placed++;
            }
            return placed;
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
            if (_grabbed >= 0 && _brushPull > _activePull + 0.5f * _activeMargin)
            {
                _activePull = _brushPull;
                _activeDirty = true;
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
            int contactCount = SolvesLayerContacts ? _contactCount : 0;

            IClothSolverKernel kernel = Kernel;
            if (kernel != null && iterations > 0)
            {
                // λ のリセット、元の形の近い粒子の探し直し、ワールド座標への書き戻しも、カーネルが行う
                kernel.Iterate(new ClothSolverIterationData(_positions, _worldPositions, _origin, _graph.Offsets, _graph.Neighbors, _inverseMass,
                    bendPairs, bendRest, _bendLambda, _activeBend, _activeBendCount, BendCompliance,
                    stretchPairs, stretchRest, _stretchLambda, _activeStretch, _activeStretchCount, StretchCompliance,
                    _activeParticles, _activeParticleCount, _grabbed, _inBrush, _grabDistance, _grabSource, _nearestPin, _pinDistance,
                    keepOutside, _restInternal, _restNormals, _restAnchor, _restAllowance,
                    softBrush ? _brushCompliance : 0f, _brushGoal, _brushLambda,
                    contactCount, _contactOuter, _contactInner, _contactGap, _contactReach), iterations);
                if (keepOutside && iterations > 0) PushClampedBrushOutside();
                return;
            }

            for (int a = 0; a < _activeStretchCount; a++) _stretchLambda[_activeStretch[a]] = 0f;
            for (int a = 0; a < _activeBendCount; a++) _bendLambda[_activeBend[a]] = 0f;
            if (keepOutside && iterations > 0) UpdateRestAnchors();
            if (contactCount > 0 && iterations > 0) UpdateContactPartners();

            for (int it = 0; it < iterations; it++)
            {
                // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
                if (softBrush) SolveBrushTargets();
                SolveDistances(_activeBend, _activeBendCount, bendPairs, bendRest, _bendLambda, BendCompliance);
                SolveDistances(_activeStretch, _activeStretchCount, stretchPairs, stretchRest, _stretchLambda, StretchCompliance);

                SolveTethers();
                if (keepOutside) SolveRestSurface();
                if (contactCount > 0) SolveLayerContacts();
            }
            if (keepOutside && iterations > 0) PushClampedBrushOutside();

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
            // 距離はつまんだ範囲の端から測るので、ブラシ半径は足さない（中心から測っていたときと同じ範囲になる）
            float limit = _influenceRadius + _activeMargin + _activePull;
            int n = _positions.Length;
            bool linked = _grabbed >= 0 && SolvesLayerContacts && MarkContactLinkedComponents();
            _activeParticleCount = 0;
            for (int i = 0; i < n; i++)
            {
                // つながっていない部品（距離が無限大）は計算しない。ただし接触の組でつながった層は、押されて動けるよう計算する
                bool active = _grabbed < 0 || (!float.IsInfinity(_grabDistance[i]) && _grabDistance[i] <= limit)
                    || (linked && float.IsInfinity(_grabDistance[i]) && _componentActive[_components[i]]);
                // つまんだ範囲は、柔らかい制約で引き寄せるときは動ける（BrushCompliance を参照）
                bool fixedParticle = _pinned[i] || (IsGrabbed(i) && _brushCompliance <= 0f) || !active;
                _inverseMass[i] = fixedParticle ? 0f : 1f;
                // つまんだ範囲もテザーの対象にするので、ピン留めでなければ一覧に入れる
                if (active && !_pinned[i]) _activeParticles[_activeParticleCount++] = i;
            }

            _activeStretchCount = CollectActive(_model.StretchPairs, _activeStretch);
            _activeBendCount = CollectActive(_model.BendPairs, _activeBend);
        }

        bool SolvesLayerContacts => _keepLayersApart && _contactCount > 0 && _restNormals != null;

        // つまんだ範囲のある島から、接触の組でたどれる島に印を付ける。印の付いた島があれば true。
        // 計算範囲を決め直すときだけ呼ばれる（毎回の Solve ではない）
        bool MarkContactLinkedComponents()
        {
            bool[] marked = _componentActive;
            Array.Clear(marked, 0, marked.Length);
            for (int k = 0; k < _brushCount; k++) marked[_components[_brushList[k]]] = true;
            bool changed = true;
            bool any = false;
            while (changed)
            {
                changed = false;
                for (int k = 0; k < _contactCount; k++)
                {
                    int a = _components[_contactOuter[k]], b = _components[_contactInner[k]];
                    if (marked[a] == marked[b]) continue;
                    marked[a] = true;
                    marked[b] = true;
                    changed = true;
                    any = true;
                }
            }
            return any;
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

        // 目標が変わったら、つまんだ範囲の外側へ、一番近い範囲の粒子の目標の増分を、範囲からの距離に応じて配る（初期の推定）。
        // 現在位置ではなく、前回配った目標からの増分で考える。中心がピン留めされていても、同じ移動を何度も足さないようにするため。
        // 範囲の粒子は、柔らかい制約なら同じく増分だけ動かし、固定（BrushCompliance = 0）なら目標の位置に置く。
        // 目標が変わらない間は置き直さない（仕上げの途中で固定に切り替えたとき、今の位置のまま押さえるため）
        void SpreadGrabMotion()
        {
            Vector3[] p = _positions;
            bool soft = _brushCompliance > 0f;
            if (_spreadPending)
            {
                _spreadPending = false;
                Vector3[] goal = _brushGoal;
                Vector3[] applied = _brushApplied;
                for (int a = 0; a < _activeParticleCount; a++)
                {
                    int i = _activeParticles[a];
                    if (_inBrush[i]) continue;
                    float f = Falloff(_grabDistance[i]);
                    if (f == 0f) continue;
                    int s = _grabSource[i];
                    p[i].X += (goal[s].X - applied[s].X) * f;
                    p[i].Y += (goal[s].Y - applied[s].Y) * f;
                    p[i].Z += (goal[s].Z - applied[s].Z) * f;
                }
                for (int k = 0; k < _brushCount; k++)
                {
                    int i = _brushList[k];
                    if (!_pinned[i])
                    {
                        if (soft)
                        {
                            p[i].X += goal[i].X - applied[i].X;
                            p[i].Y += goal[i].Y - applied[i].Y;
                            p[i].Z += goal[i].Z - applied[i].Z;
                        }
                        else
                        {
                            p[i] = goal[i];
                        }
                    }
                    applied[i] = goal[i];
                }
            }
        }

        // 1（つまんでいる範囲）〜 0（影響範囲の外）。distance は範囲の端からの距離
        float Falloff(float distance)
        {
            if (distance <= 0f) return 1f;
            if (_influenceRadius <= 0f) return 0f;
            float t = distance / _influenceRadius;
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
                // つまんだ範囲の外側は、一番近い範囲の粒子から、メッシュに沿った距離より遠くへは離れない
                if (_grabbed >= 0 && !_inBrush[i]) SolveTether(i, _grabSource[i], _grabDistance[i]);

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

        // 接触の組の内側の粒子を、内側の層の隣へ、外側の粒子の今の位置に近くなる限りたどって探し直す（最大16歩）。
        // 層が互いにずれても、外側の粒子のすぐ下の粒子を相手にするため
        void UpdateContactPartners()
        {
            int[] offsets = _graph.Offsets;
            int[] neighbors = _graph.Neighbors;
            Vector3[] p = _positions;
            for (int k = 0; k < _contactCount; k++)
            {
                int i = _contactOuter[k];
                int partner = _contactInner[k];
                float px = p[i].X, py = p[i].Y, pz = p[i].Z;
                float best = DistanceSquared(px, py, pz, p[partner]);
                for (int step = 0; step < 16; step++)
                {
                    int next = -1;
                    int end = offsets[partner + 1];
                    for (int e = offsets[partner]; e < end; e++)
                    {
                        int candidate = neighbors[e];
                        float d = DistanceSquared(px, py, pz, p[candidate]);
                        if (d < best)
                        {
                            best = d;
                            next = candidate;
                        }
                    }
                    if (next < 0) break;
                    partner = next;
                }
                _contactInner[k] = partner;
            }
        }

        // 外側の粒子 i が、内側の粒子 j の元の形の面（外向き n_j）から、保つ隙間より内に入っていれば、両方を逆質量で押し分ける。
        // つまんでいる粒子は押す側として動かさない（相手が動けないときだけ動かす）。外の布を内へ押すと、内の布が押されて動くように。
        // i が j の面に沿って _contactReach より離れていれば（内側の層の縁の外）、解かない
        void SolveLayerContacts()
        {
            Vector3[] p = _positions;
            Vector3[] normals = _restNormals;
            float[] inverseMass = _inverseMass;
            bool grabbing = _grabbed >= 0;
            float reachSquared = _contactReach * _contactReach;
            for (int k = 0; k < _contactCount; k++)
            {
                int i = _contactOuter[k], j = _contactInner[k];
                float nx = normals[j].X, ny = normals[j].Y, nz = normals[j].Z;
                float dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, dz = p[i].Z - p[j].Z;
                float along = dx * nx + dy * ny + dz * nz;
                float c = along - _contactGap[k];
                if (c >= 0f) continue;
                if (dx * dx + dy * dy + dz * dz - along * along > reachSquared) continue;
                float wi = inverseMass[i], wj = inverseMass[j];
                if (grabbing)
                {
                    if (_inBrush[i] && wj > 0f) wi = 0f;
                    else if (_inBrush[j] && wi > 0f) wj = 0f;
                }
                float w = wi + wj;
                if (w == 0f) continue;
                float s = -c / w;
                p[i].X += wi * s * nx; p[i].Y += wi * s * ny; p[i].Z += wi * s * nz;
                p[j].X -= wj * s * nx; p[j].Y -= wj * s * ny; p[j].Z -= wj * s * nz;
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

        // つまんだ範囲の各粒子を、粒子ごとの目標へ引き寄せる（XPBD の柔らかい制約）
        void SolveBrushTargets()
        {
            Vector3[] p = _positions;
            Vector3[] goal = _brushGoal;
            float alpha = _brushCompliance;
            float scale = 1f / (1f + alpha);
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                if (!_inBrush[i]) continue;
                float dx = (-(p[i].X - goal[i].X) - alpha * _brushLambda[i].X) * scale;
                float dy = (-(p[i].Y - goal[i].Y) - alpha * _brushLambda[i].Y) * scale;
                float dz = (-(p[i].Z - goal[i].Z) - alpha * _brushLambda[i].Z) * scale;
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
            if (origin < 0 || float.IsInfinity(maxLength)) return;
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
