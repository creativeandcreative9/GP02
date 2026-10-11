using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using PivotKind = GP02.EditorTools.SelectionPivot;

namespace GP02.EditorTools
{
    /// <summary>
    /// 道具（ブラシの使い方）。数値は EditorPrefs に保存するので、並べ替えず、足すときは末尾に足すこと。
    /// </summary>
    public enum ClothEditMode
    {
        /// <summary>つまんで動かす。</summary>
        Grab,
        Pin,
        Unpin,

        /// <summary>クリックした点を中心に、横ドラッグで回す。</summary>
        Rotate,

        /// <summary>クリックした点を中心に、横ドラッグで拡大縮小する。</summary>
        Scale,

        /// <summary>塗った所を、元の形へ近づける。</summary>
        Relax,

        /// <summary>
        /// 点（粒子）を選んで動かす。塗って選ぶ（ポインタの操作）と、選んだ点をハンドルで動かす・回す・拡大縮小する
        /// （<see cref="ClothEditController.BeginSelectionTransform"/> など）を合わせた道具。
        /// </summary>
        Select,
    }

    /// <summary>
    /// 布の編集の操作の流れ（押す・ドラッグ・離す、仕上げ、Unity の Undo との連動、保存、作り直し）。
    /// 画面（EditorWindow、シーンビューのイベント）には依存しない。入力は「レイ」「カメラの向き」「経過時間」で受け取るので、
    /// EditMode テストから動かせる。ウィンドウは、イベントを読んでここに渡し、描画するだけ。
    /// 編集の中身（粒子・保存）は <see cref="ClothEditSession"/> が持つ。
    /// </summary>
    public sealed class ClothEditController : IDisposable
    {
        public const string DefaultSaveFolder = "Assets/GP02/Meshes";

        readonly ClothEditSettings _settings;
        Renderer _target;                // 対象（複数のときは最初のもの）
        readonly List<Renderer> _targets = new List<Renderer>();
        ClothEditSession _session;
        ClothEditState _state;          // Unity の Undo の記録先（セッションごとに作る）
        float _influenceRadius = 0.3f;
        float _brushRadius = 0.03f;
        bool _disposed;

        // 操作の状態
        bool _opOpen;                   // 変更の記録を取り始めて、まだ書き込んでいない
        int _opGroup;
        bool _ignoreUndoCallback;
        bool _pointerDown;              // 押してから離す（または取り消す）まで
        ClothEditMode _strokeMode;      // 押したときのモード
        bool _dragging;                 // つまんで動かしている
        bool _moved;                    // つまんだ点が実際に動いた
        bool _settling;
        bool _settleClockPending;       // 離したあとの最初の Tick で、仕上げの開始時刻を決める
        double _settleStart;
        Plane _dragPlane;
        Vector3 _dragStartPoint;
        bool _dragOnLine;               // 動きを直線（法線・軸）に固定している
        Vector3 _dragLineDirection;
        Vector3 _dragRawLast;           // 前回のカーソルの位置（平面または直線の上）
        Vector3 _dragTarget;            // 精密操作を反映した、つまんだ点の動かし先
        float _startScreenX;
        float _lastScreenX;
        float _effectivePixels;         // 回す・拡大縮小の横ドラッグ量（精密操作を反映）
        Vector3 _rotateAxis;
        float _grabAngle;
        float _grabScale = 1f;
        Transform _avatarRoot;
        bool _holdOriginal;
        readonly List<int> _relaxHeld = new List<int>();   // 元に戻すブラシの仕上げの間だけ、一時的にピン留めしている粒子

        // ブラシを当てる前の表示（範囲に入る粒子と重み）
        public const int MaxPreviewParticles = 1500;
        readonly int[] _previewIndices = new int[MaxPreviewParticles];
        readonly float[] _previewWeights = new float[MaxPreviewParticles];
        int _previewCount;
        bool _previewKeyValid;
        int _previewSeed = -1;
        float _previewRadius;
        BrushDistanceMode _previewMode;
        FalloffCurve _previewCurve;
        bool _previewMirror;
        MirrorPlane _previewPlane;
        bool _previewShown;
        int _hoverMirrorSeed = -1;      // _hoverMirrorParticle を探した中心の粒子
        int _hoverMirrorParticle = -1;
        MirrorPlane _hoverMirrorPlane;
        bool _hoverMirrorValid;

        int _hoverTriangle = -1;        // カーソルの下の三角形（重ね着で、どの布をつまむか・ピン留めするかを決める）

        // 選んだ点（粒子）。Unity の Undo には入れない
        bool[] _selected;               // 粒子ごと。編集中でなければ null
        int[] _selectedList = new int[0];   // 選んだ粒子の番号（昇順）。選択が変わるたびに作り直す（つまむときに同じ配列を渡す）
        int _selectionCount;
        int _selectionVersion;
        readonly List<int> _selectWork = new List<int>();
        readonly List<int> _selectWork2 = new List<int>();
        bool _selectPainting;           // 塗って選んでいる途中
        bool _selectDeselect;
        bool _hasLastClick;
        int _lastClickParticle = -1;    // 最後にクリックした所の一番近い粒子と、そこからのずれ（形が変わっても付いていくため）
        Vector3 _lastClickOffset;
        Vector3 _lastClickWorld;

        // 選んだ点の変形
        bool _xformActive;              // BeginSelectionTransform から EndSelectionTransform まで
        bool _xformShapeOnly;           // 形だけ動かす（ClothEditSettings.SelectionMovesAsCloth がオフ）
        bool _setGrab;                  // 集合でつまんでいる（変形の開始から確定まで）
        Vector3 _xformPivot;            // 押したときの中心と、拡大の軸の向き
        Quaternion _xformFrame = Quaternion.identity;
        Vector3 _xformRawTranslation;   // 呼び出し側から最後に受け取った値（押したときからの合計）
        Quaternion _xformRawRotation = Quaternion.identity;
        Vector3 _xformRawLogScale;
        Vector3 _xformTranslation;      // 精密操作を反映した、動きの合計
        Quaternion _xformRotation = Quaternion.identity;
        Vector3 _xformLogScale;
        bool _xformPreciseUsed;

        // ハンドルの基準の計算結果（選択か形が変わるまで使い回す）
        int _pivotVersion = -1, _pivotShape = -1;
        Vector3 _centerCache;
        int _frameVersion = -1, _frameShape = -1;
        HandleOrientation _frameSpace;
        Quaternion _frameCache = Quaternion.identity;

        // 画面で鏡像の側を薄く見せるための粒子
        readonly List<int> _mirrored = new List<int>();
        int _mirroredVersion = -1;
        bool _mirroredHasPlane;
        MirrorPlane _mirroredPlane;

        // なめらかに減る範囲の重み
        public const int MaxSelectionWeightParticles = 3000;
        readonly int[] _weightParticles = new int[MaxSelectionWeightParticles];
        readonly float[] _weightValues = new float[MaxSelectionWeightParticles];
        int[] _weightWorkIndices;
        float[] _weightWorkValues;
        int _weightCount;
        int _weightVersion = -1;
        float _weightRadius;
        FalloffCurve _weightCurve;
        BrushDistanceMode _weightMode;
        bool _weightHasMirror;
        MirrorPlane _weightPlane;
        int _weightShape;
        bool _weightShown;

        public ClothEditController(ClothEditSettings settings = null)
        {
            _settings = settings ?? new ClothEditSettings();
            // Ctrl+Z / Ctrl+Y、メニューの Edit > Undo、ボタンのどれでもここに来る
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        /// <summary>画面の更新が必要になったときに呼ばれる。</summary>
        public event Action Changed;

        // ---- 設定・状態 ----

        public ClothEditSettings Settings => _settings;
        /// <summary>対象のレンダラー（重ね着をまとめて編集しているときは最初のもの）。</summary>
        public Renderer Target => _target;

        /// <summary>まとめて編集している（最後に編集を始めた）レンダラー。</summary>
        public IReadOnlyList<Renderer> Targets => _targets;
        public ClothEditSession Session => _session;
        public bool IsEditing => _session != null;
        public bool IsDragging => _dragging;
        public bool IsSettling => _settling;
        public bool IsBusy => _dragging || _settling;

        /// <summary>直前の編集開始・作り直しが失敗したときの理由。成功すると空になる。</summary>
        public string LastError { get; private set; } = "";

        /// <summary>最後に保存したメッシュ（重ね着をまとめて編集しているときは最初のレンダラーのもの）。</summary>
        public Mesh LastSavedMesh { get; private set; }

        /// <summary>最後に保存したメッシュ（<see cref="Targets"/> の順）。</summary>
        public IReadOnlyList<Mesh> LastSavedMeshes { get; private set; } = new Mesh[0];

        /// <summary>計算の最後に測った、最大の伸び（0.1 で 10%）。</summary>
        public float LastStretch { get; private set; }

        /// <summary>今の道具。<see cref="ClothEditSettings.Tool"/> に持つので、保存される。</summary>
        public ClothEditMode Mode
        {
            get => _settings.Tool;
            set => _settings.Tool = value;
        }

        /// <summary>
        /// アバターの基準の Transform（対称編集の鏡と、軸に固定するときの「アバターのローカル」の基準）。
        /// 編集を始めたときに <see cref="ClothEditSymmetry.FindAvatarRoot"/> で決める。編集中でなければ null。
        /// </summary>
        public Transform AvatarRoot => _avatarRoot;

        /// <summary>つまんでいる点の、今の動かし先（精密操作を反映したワールド座標）。つまんでいなければ意味を持たない。</summary>
        public Vector3 GrabTarget => _dragTarget;

        /// <summary>回している角度（度）。「回す」で押している間の値。</summary>
        public float GrabAngle => _grabAngle;

        /// <summary>拡大率。「拡大縮小」で押している間の値。</summary>
        public float GrabScale => _grabScale;

        // ---- 操作の定数 ----

        /// <summary>横に1ピクセル動かしたときに回る角度（度）。</summary>
        public const float DegreesPerPixel = 0.5f;

        /// <summary>横に1ピクセル動かしたときの拡大率の対数の増分（exp(px * この値)）。</summary>
        public const float ScaleLogPerPixel = 0.006f;

        public const float MinScale = 0.2f;
        public const float MaxScale = 3f;

        /// <summary>Ctrl を押している間の回転の刻み（度）。</summary>
        public const float SnapDegrees = 15f;

        /// <summary>Shift を押している間の、動きの倍率。</summary>
        public const float PrecisionFactor = 0.2f;

        /// <summary>横ドラッグの量（ピクセル）から回転角（度）。snap なら <see cref="SnapDegrees"/> の倍数にする。</summary>
        public static float AngleFromPixels(float pixels, bool snap)
        {
            float angle = pixels * DegreesPerPixel;
            return snap ? Mathf.Round(angle / SnapDegrees) * SnapDegrees : angle;
        }

        /// <summary>横ドラッグの量（ピクセル）から拡大率（<see cref="MinScale"/>〜<see cref="MaxScale"/>）。</summary>
        public static float ScaleFromPixels(float pixels)
        {
            return Mathf.Clamp(Mathf.Exp(pixels * ScaleLogPerPixel), MinScale, MaxScale);
        }

        /// <summary>
        /// ホイールやキーで半径を変える。1ステップごとに 10% 大きく（小さく）なり、min〜max に収める。
        /// 大きさに比例して変わるので、小さい半径も細かく調整できる。
        /// </summary>
        /// <param name="steps">正で大きく、負で小さく</param>
        public static float AdjustRadius(float current, float steps, float min, float max)
        {
            return Mathf.Clamp(current * Mathf.Pow(1.1f, steps), min, max);
        }

        /// <summary>
        /// レイと直線（origin を通り direction の向き）の最も近い点。レイが直線とほぼ平行なとき（動かす向きが視線とほぼ同じ）は
        /// 位置が決まらないので false。
        /// </summary>
        public static bool TryClosestPointOnLine(Ray ray, Vector3 origin, Vector3 direction, out Vector3 point)
        {
            Vector3 d1 = ray.direction.normalized;
            Vector3 d2 = direction.normalized;
            Vector3 w = ray.origin - origin;
            float b = Vector3.Dot(d1, d2);
            float d = Vector3.Dot(d1, w);
            float e = Vector3.Dot(d2, w);
            float denominator = 1f - b * b;
            if (denominator < 1e-3f)
            {
                point = origin;
                return false;
            }
            float t = (e - b * d) / denominator;
            // 視線に近い向きでは、少しのカーソルの動きで大きく飛ぶので、遠くへは行かせない
            t = Mathf.Clamp(t, -3f, 3f);
            point = origin + d2 * t;
            return true;
        }

        // ---- 比較（元の形との間の見せ方） ----

        /// <summary>元の形を表示するか（トグル）。</summary>
        public bool ShowOriginal
        {
            get => _settings.ShowOriginal;
            set
            {
                _settings.ShowOriginal = value;
                ApplyPreviewBlend();
            }
        }

        /// <summary>C キーを押している間だけ元の形を見せる。</summary>
        public bool HoldOriginal
        {
            get => _holdOriginal;
            set
            {
                if (_holdOriginal == value) return;
                _holdOriginal = value;
                ApplyPreviewBlend();
            }
        }

        /// <summary>編集中の形と元の形の間の見せ方（0 で元の形、1 で編集中の形）。</summary>
        public float CompareBlend
        {
            get => _settings.CompareBlend;
            set
            {
                _settings.CompareBlend = Mathf.Clamp01(value);
                ApplyPreviewBlend();
            }
        }

        /// <summary>今、元の形（または元との中間）を見せているか。</summary>
        public bool IsComparing => _settings.ShowOriginal || _holdOriginal || _settings.CompareBlend < 1f;

        void ApplyPreviewBlend()
        {
            if (_session != null) _session.PreviewBlend = _settings.ShowOriginal || _holdOriginal ? 0f : _settings.CompareBlend;
            Notify();
        }

        // ---- ブラシ（範囲の決め方・対称） ----

        /// <summary>設定に従った鏡の面。対称がオフ、または編集中でなければ false。</summary>
        public bool TryGetMirrorPlane(out MirrorPlane plane)
        {
            return ClothEditSymmetry.TryBuildMirrorPlane(_settings, _avatarRoot, out plane);
        }

        /// <summary>設定に従った、つまむ範囲の決め方（カーブ・距離の測り方・対称）。半径は呼び出し側（BeginGrab など）が決める。</summary>
        public BrushOptions BuildBrushOptions()
        {
            return new BrushOptions
            {
                Radius = _brushRadius,
                Falloff = _settings.GetFalloffCurve(),
                DistanceMode = _settings.DistanceMode,
                Mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null,
            };
        }

        /// <summary>保存先のフォルダ。</summary>
        public string SaveFolder { get; set; } = DefaultSaveFolder;

        /// <summary>メッシュの変換中に進捗バーを出すか（ウィンドウだけ true にする）。</summary>
        public bool ShowProgress { get; set; }

        /// <summary>ブラシ半径（メートル）。</summary>
        public float BrushRadius
        {
            get => _brushRadius;
            set => _brushRadius = Mathf.Max(0f, value);
        }

        /// <summary>つまんだ動きが広がる範囲（メートル）。編集中でもすぐ反映される。</summary>
        public float InfluenceRadius
        {
            get => _influenceRadius;
            set
            {
                value = Mathf.Max(0f, value);
                if (value == _influenceRadius) return;   // 毎回の描画から呼ばれるので、変わらないときはソルバーを触らない
                _influenceRadius = value;
                if (_session != null) _session.Solver.InfluenceRadius = _influenceRadius;
            }
        }

        public bool HasHover { get; private set; }
        public Vector3 HoverPoint { get; private set; }
        public Vector3 HoverNormal { get; private set; } = Vector3.up;

        public string StateLabel
        {
            get
            {
                if (_dragging)
                {
                    if (_setGrab) return L10n.Tr("選んだ点を動かしている", "Moving the selection");
                    return _strokeMode == ClothEditMode.Rotate ? L10n.Tr("回している", "Rotating")
                        : _strokeMode == ClothEditMode.Scale ? L10n.Tr("拡大縮小している", "Scaling")
                        : L10n.Tr("つまんでいる", "Grabbing");
                }
                return _settling ? L10n.Tr("仕上げ中", "Settling") : L10n.Tr("待機中", "Idle");
            }
        }

        /// <summary>設定を変えたあとに呼ぶ。編集中のソルバーに反映する。</summary>
        public void ApplySettings()
        {
            _settings.Sanitize();
            if (_session != null) _settings.ApplyTo(_session.Solver, _session.GetRestAllowance(_settings.KeepLayersApart));
            _previewKeyValid = false;
            ApplyPreviewBlend();
        }

        // ---- 対象の確認 ----

        public static Renderer FindRenderer(GameObject target)
        {
            if (target == null) return null;
            var skinned = target.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null) return skinned;
            var meshRenderer = target.GetComponent<MeshRenderer>();
            return meshRenderer != null && meshRenderer.GetComponent<MeshFilter>() != null ? meshRenderer : null;
        }

        public static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (renderer is MeshRenderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
            return null;
        }

        /// <summary>編集を始められない理由。始められるなら null。利用者に見せる文章にしてある。</summary>
        public static string GetStartProblem(Renderer renderer)
        {
            if (renderer == null)
            {
                return L10n.Tr("対象が指定されていません。「対象」に、布の SkinnedMeshRenderer（または MeshFilter 付きの MeshRenderer）を指定してください。",
                    "No target is set. Set the cloth's SkinnedMeshRenderer (or a MeshRenderer with a MeshFilter) as the target.");
            }
            Mesh mesh = GetMesh(renderer);
            if (mesh == null)
            {
                return L10n.Tr($"「{renderer.name}」にメッシュが設定されていません。メッシュを持つ SkinnedMeshRenderer か、MeshFilter 付きの MeshRenderer を指定してください。",
                    $"\"{renderer.name}\" has no mesh. Choose a SkinnedMeshRenderer that has a mesh, or a MeshRenderer with a MeshFilter.");
            }
            if (ClothEditSession.CountTriangles(mesh) == 0)
            {
                return L10n.Tr($"「{renderer.name}」のメッシュには三角形（面）がありません。面のあるメッシュを指定してください。",
                    $"The mesh of \"{renderer.name}\" has no triangles (faces). Choose a mesh that has faces.");
            }
            return null;
        }

        /// <summary>重ね着とみなす距離（メートル）の既定値（<see cref="FindLayeredRenderers"/>）。</summary>
        public const float DefaultLayerDistance = 0.05f;

        // 頂点のこの割合以上が対象の近くにあるレンダラーを、重ね着とみなす（体は一部だけが服の近くにあるので入らない）
        const float LayerOverlapRatio = 0.5f;

        /// <summary>
        /// 同じアバター（<see cref="ClothEditSymmetry.FindAvatarRoot"/>）の下の、有効な SkinnedMeshRenderer のうち、
        /// 今の対象と面が近い（頂点の半分以上が、対象のどれかの頂点から distance 以内にある）ものを探す。対象自身は含めない。
        /// 「同じアバターの重ね着をまとめて追加」用。形は今のポーズ（BakeMesh）で比べる。
        /// </summary>
        public static List<Renderer> FindLayeredRenderers(IReadOnlyList<Renderer> targets, float distance = DefaultLayerDistance)
        {
            var result = new List<Renderer>();
            if (targets == null || !(distance > 0f)) return result;
            var roots = new List<Transform>();
            var targetPoints = new List<Vector3>();
            foreach (Renderer target in targets)
            {
                if (target == null) continue;
                Transform root = ClothEditSymmetry.FindAvatarRoot(target.transform);
                if (!roots.Contains(root)) roots.Add(root);
                targetPoints.AddRange(BakeWorldVertices(target));
            }
            if (targetPoints.Count == 0) return result;
            var grid = new PointGrid(distance);
            for (int k = 0; k < targetPoints.Count; k++) grid.Add(k, new System.Numerics.Vector3(targetPoints[k].x, targetPoints[k].y, targetPoints[k].z));

            var candidates = new List<int>();
            float distanceSq = distance * distance;
            foreach (Transform root in roots)
            {
                foreach (SkinnedMeshRenderer candidate in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (!candidate.enabled || candidate.sharedMesh == null || result.Contains(candidate)) continue;
                    bool isTarget = false;
                    foreach (Renderer target in targets) isTarget |= target == candidate;
                    if (isTarget || GetStartProblem(candidate) != null) continue;

                    Vector3[] vertices = BakeWorldVertices(candidate);
                    if (vertices.Length == 0) continue;
                    int near = 0;
                    foreach (Vector3 v in vertices)
                    {
                        grid.Query(new System.Numerics.Vector3(v.x, v.y, v.z), distance, candidates);
                        foreach (int c in candidates)
                        {
                            if ((targetPoints[c] - v).sqrMagnitude <= distanceSq)
                            {
                                near++;
                                break;
                            }
                        }
                    }
                    if (near >= LayerOverlapRatio * vertices.Length) result.Add(candidate);
                }
            }
            return result;
        }

        // 今のポーズのワールド頂点（SkinnedMeshRenderer は BakeMesh。読めないメッシュは空）
        static Vector3[] BakeWorldVertices(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                if (skinned.sharedMesh == null) return new Vector3[0];
                var baked = new Mesh();
                try
                {
                    // スケールを含めて焼くと、レンダラーの位置と回転だけでワールド座標になる
                    skinned.BakeMesh(baked, true);
                    Vector3[] vertices = baked.vertices;
                    Matrix4x4 toWorld = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                    for (int v = 0; v < vertices.Length; v++) vertices[v] = toWorld.MultiplyPoint3x4(vertices[v]);
                    return vertices;
                }
                finally
                {
                    Object.DestroyImmediate(baked);
                }
            }
            Mesh mesh = GetMesh(renderer);
            if (mesh == null) return new Vector3[0];
            Vector3[] local = mesh.vertices;
            Matrix4x4 matrix = renderer.transform.localToWorldMatrix;
            for (int v = 0; v < local.Length; v++) local[v] = matrix.MultiplyPoint3x4(local[v]);
            return local;
        }

        /// <summary>Read/Write が無効なメッシュか。エディタ上では編集できるが、利用者に知らせるために使う。</summary>
        public static bool IsMeshUnreadable(Renderer renderer)
        {
            Mesh mesh = GetMesh(renderer);
            return mesh != null && !mesh.isReadable;
        }

        // ---- 編集の開始と終了 ----

        /// <summary>
        /// 編集を始める。始められないときは false を返し、利用者向けの理由を error に入れる（<see cref="LastError"/> にも残る）。
        /// </summary>
        public bool TryStart(Renderer target, out string error)
        {
            return TryStartLayered(new[] { target }, out error);
        }

        /// <summary>
        /// 複数のレンダラー（重ね着など）をまとめて1つの布として編集を始める。同じレンダラーが重なっていれば1つにする。
        /// 保存すると、すべてのレンダラーのメッシュに同じ名前のシェイプキーが入る。
        /// </summary>
        public bool TryStartLayered(IReadOnlyList<Renderer> targets, out string error)
        {
            _targets.Clear();
            if (targets != null)
            {
                foreach (Renderer renderer in targets)
                {
                    // null は残して、GetStartProblem で「対象が指定されていません」を出す
                    if (renderer == null || !_targets.Contains(renderer)) _targets.Add(renderer);
                }
            }
            _target = _targets.Count > 0 ? _targets[0] : null;
            // 見せ方は、編集を始めるたびに編集中の形（100%）から始める
            _settings.ShowOriginal = false;
            _settings.CompareBlend = 1f;
            _holdOriginal = false;
            return StartSession(null, null, null, null, null, out error);
        }

        /// <param name="carriedPins">引き継ぐピンのワールド位置（Undo による作り直しのとき）</param>
        /// <param name="carriedPinIslands">引き継ぐピンごとの部品（島）の番号。重ね着で元の層だけを留めるため</param>
        /// <param name="reuseCloth">粒子を置き直さずに使う布（保存後の <see cref="ParticleCloth.Rebase"/> の結果）。メッシュに合わなければ使わない</param>
        /// <param name="carriedPinParticles">reuseCloth を使うときに引き継ぐピンの粒子の番号（番号が同じなので位置で探さない）</param>
        /// <param name="carriedSelection">引き継ぐ選択（<see cref="CaptureSelection"/>）。null なら選択は空から始める</param>
        bool StartSession(IReadOnlyList<Vector3> carriedPins, IReadOnlyList<int> carriedPinIslands,
            ParticleCloth reuseCloth, IReadOnlyList<int> carriedPinParticles, SelectionCarry carriedSelection, out string error)
        {
            End();
            error = _targets.Count == 0 ? GetStartProblem(null) : null;
            foreach (Renderer renderer in _targets)
            {
                if (error != null) break;
                error = GetStartProblem(renderer);
            }
            if (error != null)
            {
                Fail(error);
                return false;
            }

            if (reuseCloth != null && !ClothEditSession.IsClothCompatible(_targets, reuseCloth)) reuseCloth = null;
            try
            {
                if (ShowProgress) EditorUtility.DisplayProgressBar("GP02", L10n.Tr("メッシュを粒子にしています…", "Converting the mesh to particles..."), 0.5f);
                _session = new ClothEditSession(_targets, reuseCloth);
            }
            catch (ArgumentException ex)
            {
                // 想定している失敗（Read/Write が読めない、三角形がない、など）。理由は ex.Message に書いてある
                _session = null;
                error = ex.Message;
                Fail(error);
                return false;
            }
            catch (Exception ex)
            {
                _session = null;
                Debug.LogException(ex);
                error = L10n.Tr("編集を始められませんでした。メッシュの形が特殊な可能性があります。Console のエラーを確認してください。\n",
                    "Could not start editing. The mesh may have an unusual shape. Check the errors in the Console.\n") + ex.Message;
                Fail(error);
                return false;
            }
            finally
            {
                if (ShowProgress) EditorUtility.ClearProgressBar();
            }

            _settings.ApplyTo(_session.Solver, _session.GetRestAllowance(_settings.KeepLayersApart));
            _session.Solver.InfluenceRadius = _influenceRadius;
            if (reuseCloth != null)
            {
                if (carriedPinParticles != null && carriedPinParticles.Count > 0)
                {
                    int pinned = _session.PinParticles(carriedPinParticles);
                    Debug.Log($"[GP02] ピン {pinned} 粒子を引き継ぎました。");
                }
            }
            else if (carriedPins != null && carriedPins.Count > 0)
            {
                int pinned = _session.PinNear(carriedPins, _session.Cloth.Spacing * 0.75f, carriedPinIslands);
                Debug.Log($"[GP02] ピン {carriedPins.Count} 点から {pinned} 粒子を引き継ぎました。");
            }

            RestoreSelection(carriedSelection, reuseCloth != null);

            // Burst のコンパイルをここで済ませる（最初のドラッグで止まらないように）
            double warmUp = _session.WarmUp();

            // 作り直した直後の形が、Undo の戻り先の基準になる
            _state = ClothEditState.Create();
            _session.WriteState(_state);
            _avatarRoot = ClothEditSymmetry.FindAvatarRoot(_target.transform);
            _previewKeyValid = false;
            _previewCount = 0;
            _hoverMirrorValid = false;
            ApplyPreviewBlend();
            LastError = "";
            LastStretch = 0f;
            HasHover = false;
            Debug.Log($"[GP02] {_session.DescribeTargets()}: 頂点 {_session.VertexCount} / 粒子 {_session.ParticleCount} / 層どうしの接触 {_session.LayerContacts.Count} 組 / 準備 {_session.PrepareMilliseconds:F0}ms / 初回の計算 {warmUp:F0}ms");
            Notify();
            return true;
        }

        void Fail(string message)
        {
            LastError = message;
            Debug.LogWarning("[GP02] " + message);
            Notify();
        }

        /// <summary>編集を終える。元のレンダラーの表示を戻す。</summary>
        public void End()
        {
            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            _opOpen = false;
            _relaxHeld.Clear();
            HasHover = false;
            _previewCount = 0;
            _previewKeyValid = false;
            HasMirrorHover = false;
            _selectPainting = false;
            _xformActive = false;
            _setGrab = false;
            ResetSelectionState();
            if (_state != null)
            {
                Undo.ClearUndo(_state);
                Object.DestroyImmediate(_state);
                _state = null;
            }
            if (_session == null) return;
            _session.Dispose();
            _session = null;
            Notify();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            End();
        }

        // 編集中のレンダラーが消されていたら、編集を終わらせる。生きていれば true
        bool EnsureAlive()
        {
            if (_session == null) return false;
            if (_session.IsTargetAlive) return true;
            End();
            return false;
        }

        // ---- ポインタ（マウス）の入力 ----

        /// <summary>カーソルの下の布を調べる（ブラシの円の表示用）。当たれば true。</summary>
        public bool Hover(Ray ray)
        {
            if (!EnsureAlive()) return false;
            bool hit = UpdateHover(ray);
            if (hit && !_pointerDown) RefreshHoverExtras();
            return hit;
        }

        bool UpdateHover(Ray ray)
        {
            bool hit = _session.Raycast(ray, out Vector3 point, out Vector3 normal, out _hoverTriangle);
            HasHover = hit;
            if (hit)
            {
                HoverPoint = point;
                HoverNormal = normal;
            }
            else
            {
                HasMirrorHover = false;
            }
            return hit;
        }

        // ---- ブラシを当てる前の表示 ----

        /// <summary>範囲に入る粒子の数（<see cref="PreviewParticle"/> / <see cref="PreviewWeight"/> の数）。表示しないときは 0。</summary>
        public int PreviewCount => _previewShown ? _previewCount : 0;

        /// <summary>範囲に入る粒子の番号。位置は <c>Session.Solver.Positions</c> から読む（形が変わっても最新になる）。</summary>
        public int PreviewParticle(int k) => _previewIndices[k];

        /// <summary>範囲の中の重み（0〜1）。</summary>
        public float PreviewWeight(int k) => _previewWeights[k];

        /// <summary>対称編集の鏡像側のブラシの中心があるか。</summary>
        public bool HasMirrorHover { get; private set; }

        /// <summary>鏡像側のブラシの中心（ワールド）と、円を描く面の法線。</summary>
        public Vector3 MirrorHoverPoint { get; private set; }
        public Vector3 MirrorHoverNormal { get; private set; } = Vector3.up;

        /// <summary>ブラシの円を描く中心。つまんで動かしている間は、つまんだ点の今の動かし先に付いていく。</summary>
        public Vector3 BrushRingCenter => _dragging && _strokeMode == ClothEditMode.Grab ? _dragTarget : HoverPoint;

        // つまんだ瞬間に、実際に範囲に入った粒子と重み（ClothSolver.BrushWeight）を表示用に控える。
        // 対称のときは鏡像の側も含まれる。ドラッグ中は範囲が変わらないので、一度だけ集めて使い回す（毎コマの確保をしない）
        void CaptureGrabRange(bool setGrab = false)
        {
            _previewCount = 0;
            _previewShown = false;
            HasMirrorHover = false;
            if (!(setGrab ? _settings.ShowSelectionWeights : _settings.ShowBrushPreview)) return;

            ClothSolver solver = _session.Solver;
            int total = solver.BrushParticleCount;
            if (total <= 0) return;
            int stride = (total + MaxPreviewParticles - 1) / MaxPreviewParticles;
            int particleCount = solver.Positions.Length;
            int seen = 0;
            for (int i = 0; i < particleCount && _previewCount < MaxPreviewParticles; i++)
            {
                if (!solver.IsGrabbed(i)) continue;
                float weight = solver.BrushWeight(i);
                if (!(weight > 0f)) continue;
                // 中心（鏡像の中心も）は必ず含め、残りは間引く
                bool center = i == solver.GrabbedParticle || i == solver.MirrorParticle;
                if (!center && seen++ % stride != 0) continue;
                _previewIndices[_previewCount] = i;
                _previewWeights[_previewCount] = weight;
                _previewCount++;
            }
            _previewShown = true;
            _previewKeyValid = false;
        }

        // 重みをつけて範囲を決める道具か（ピン留めは重みなしで、円の内側すべて）
        static bool UsesWeights(ClothEditMode mode)
        {
            return mode == ClothEditMode.Grab || mode == ClothEditMode.Rotate || mode == ClothEditMode.Scale || mode == ClothEditMode.Relax;
        }

        /// <summary>範囲の表示を作り直すよう伝える（形や設定が変わったあとに呼ぶ）。</summary>
        public void InvalidatePreview()
        {
            _previewKeyValid = false;
        }

        // マウスを乗せている間の表示（範囲の粒子と重み、鏡像側の円）を更新する。
        // マウスの動きごとに呼ばれるので、一番近い粒子や設定が変わらない間は何もしない
        void RefreshHoverExtras()
        {
            if (Mode == ClothEditMode.Select)
            {
                // 選ぶ道具は鏡像の側を塗らず、重みもつけない（鏡像の側は MirroredSelection で見せる）
                _previewShown = false;
                HasMirrorHover = false;
                return;
            }
            bool hasMirror = TryGetMirrorPlane(out MirrorPlane plane);
            bool wantPreview = _settings.ShowBrushPreview && UsesWeights(Mode);
            _previewShown = wantPreview;
            if (!wantPreview && !hasMirror)
            {
                HasMirrorHover = false;
                return;
            }

            int seed = _session.FindParticle(HoverPoint, _hoverTriangle);
            if (seed < 0)
            {
                _previewCount = 0;
                HasMirrorHover = false;
                return;
            }

            // 鏡像の円
            HasMirrorHover = false;
            if (hasMirror)
            {
                if (!_hoverMirrorValid || _hoverMirrorSeed != seed || !SamePlane(_hoverMirrorPlane, plane))
                {
                    _hoverMirrorSeed = seed;
                    _hoverMirrorPlane = plane;
                    _hoverMirrorValid = true;
                    int mirror = _session.Solver.FindMirrorParticle(seed, plane, BrushOptions.DefaultMirrorTolerance);
                    _hoverMirrorParticle = mirror == seed ? -1 : mirror;
                }
                // 鏡像が範囲の2倍以内にあるとき（中央線の近く）は、同じ範囲なので別の円は出さない
                if (_hoverMirrorParticle >= 0
                    && System.Numerics.Vector3.Distance(_session.Solver.Positions[seed], _session.Solver.Positions[_hoverMirrorParticle]) > 2f * _brushRadius)
                {
                    System.Numerics.Vector3 offset = ClothEditSymmetry.ToNumerics(HoverPoint) - _session.Solver.Positions[seed];
                    MirrorHoverPoint = ClothEditSymmetry.ToUnity(_session.Solver.Positions[_hoverMirrorParticle] + plane.ReflectDirection(offset));
                    MirrorHoverNormal = ClothEditSymmetry.ToUnity(plane.ReflectDirection(ClothEditSymmetry.ToNumerics(HoverNormal)));
                    HasMirrorHover = true;
                }
            }

            if (!wantPreview) return;
            FalloffCurve curve = _settings.GetFalloffCurve();
            if (_previewKeyValid && _previewSeed == seed && _previewRadius == _brushRadius && _previewMode == _settings.DistanceMode
                && ReferenceEquals(_previewCurve, curve) && _previewMirror == hasMirror && (!hasMirror || SamePlane(_previewPlane, plane)))
            {
                return;
            }
            _previewSeed = seed;
            _previewRadius = _brushRadius;
            _previewMode = _settings.DistanceMode;
            _previewCurve = curve;
            _previewMirror = hasMirror;
            _previewPlane = plane;
            _previewKeyValid = true;

            BrushOptions options = BuildBrushOptions();
            _previewCount = _session.CollectBrushRange(seed, options, _previewIndices, _previewWeights, MaxPreviewParticles);
        }

        static bool SamePlane(MirrorPlane a, MirrorPlane b) => a.Point == b.Point && a.Normal == b.Normal;

        /// <summary>
        /// 押した。布に当たったら、道具に応じてつまむ・回す・拡大縮小・ピン留め・元に戻すブラシを始め、true を返す（イベントを使ってよい）。
        /// 当たらなければ false。
        /// </summary>
        /// <param name="cameraForward">カメラの向き。つまんだ点を動かす平面（ヒット点を通り、カメラの向きに垂直）と、回す軸（視線）を決める</param>
        /// <param name="screenX">マウスの横位置（ピクセル）。「回す」「拡大縮小」の横ドラッグの量を測る基準</param>
        public bool PointerDown(Ray ray, Vector3 cameraForward, float screenX = 0f)
        {
            if (!EnsureAlive()) return false;
            if (!UpdateHover(ray)) return false;

            // 前のストロークの仕上げ中なら、そこで確定する
            FinishStroke();
            _pointerDown = true;
            _strokeMode = Mode;
            _moved = false;
            _dragOnLine = false;
            _startScreenX = screenX;
            _lastScreenX = screenX;
            _effectivePixels = 0f;
            _grabAngle = 0f;
            _grabScale = 1f;
            _previewShown = false;
            switch (_strokeMode)
            {
                case ClothEditMode.Grab:
                case ClothEditMode.Rotate:
                case ClothEditMode.Scale:
                    if (_session.BeginGrab(HoverPoint, _brushRadius, _hoverTriangle, BuildBrushOptions()))
                    {
                        BeginOperation(_strokeMode == ClothEditMode.Grab ? L10n.Tr("GP02 つまむ", "GP02 Grab")
                            : _strokeMode == ClothEditMode.Rotate ? L10n.Tr("GP02 回す", "GP02 Rotate") : L10n.Tr("GP02 拡大縮小", "GP02 Scale"));
                        _dragPlane = new Plane(-cameraForward, HoverPoint);
                        _dragStartPoint = HoverPoint;
                        _dragRawLast = HoverPoint;
                        _dragTarget = HoverPoint;
                        _dragging = true;
                        CaptureGrabRange();
                        if (_strokeMode == ClothEditMode.Grab) ChooseDragLine();
                        else _rotateAxis = _settings.RotateAxis == RotateAxisMode.SurfaceNormal ? HoverNormal : -cameraForward;
                    }
                    break;
                case ClothEditMode.Pin:
                case ClothEditMode.Unpin:
                    // 押してから離すまでのドラッグを、1回の操作にまとめる
                    BeginOperation(_strokeMode == ClothEditMode.Pin ? L10n.Tr("GP02 ピン留め", "GP02 Pin") : L10n.Tr("GP02 ピン解除", "GP02 Unpin"));
                    PaintPins();
                    break;
                case ClothEditMode.Relax:
                    BeginOperation(L10n.Tr("GP02 元に戻すブラシ", "GP02 Restore Brush"));
                    RelaxHere();
                    break;
                case ClothEditMode.Select:
                    // 選ぶ道具。押している間、ブラシ半径で塗って選ぶ（選択は Undo に入れない）
                    _selectPainting = true;
                    _selectDeselect = false;
                    PaintSelectionHere(true);
                    break;
            }
            Notify();
            return true;
        }

        // 「動かす向き」の設定に従って、つまんだ点を直線の上だけで動かすかを決める
        void ChooseDragLine()
        {
            switch (_settings.MoveDirection)
            {
                case MoveDirection.SurfaceNormal:
                    _dragOnLine = true;
                    _dragLineDirection = HoverNormal;
                    break;
                case MoveDirection.AxisX:
                case MoveDirection.AxisY:
                case MoveDirection.AxisZ:
                    _dragOnLine = ClothEditSymmetry.TryGetAxisDirection(_settings.MoveDirection, _settings.DirectionSpace, _avatarRoot, out _dragLineDirection);
                    break;
            }
        }

        /// <summary>押したまま動かした。つまんでいれば動かし、ピン留めなら塗る。</summary>
        public void PointerDrag(Ray ray)
        {
            PointerDrag(ray, _lastScreenX, false, false);
        }

        /// <summary>
        /// 押したまま動かした。つまんでいれば動かし（回す・拡大縮小は横の動きで）、ピン留めや元に戻すブラシなら塗る。
        /// </summary>
        /// <param name="screenX">マウスの横位置（ピクセル）</param>
        /// <param name="precise">Shift：動きを <see cref="PrecisionFactor"/> 倍にする。途中で切り替えても、動かし先は飛ばない</param>
        /// <param name="snap">Ctrl：回転を <see cref="SnapDegrees"/> の倍数にする</param>
        public void PointerDrag(Ray ray, float screenX, bool precise, bool snap)
        {
            if (!EnsureAlive() || !_pointerDown) return;
            float factor = precise ? PrecisionFactor : 1f;

            switch (_strokeMode)
            {
                case ClothEditMode.Grab:
                    if (!_dragging) return;
                    // つまんで動かしている間は、カーソルの下の点を調べない（形が変わるたびに境界箱を更新する手間を省く）
                    if (TryGetDragPoint(ray, out Vector3 raw))
                    {
                        // 増分に倍率をかけて足す。倍率を途中で変えても、今の動かし先から続きが始まる
                        _dragTarget += (raw - _dragRawLast) * factor;
                        _dragRawLast = raw;
                        _session.DragTo(_dragTarget);
                        if ((_dragTarget - _dragStartPoint).sqrMagnitude > 1e-12f) _moved = true;
                    }
                    break;

                case ClothEditMode.Rotate:
                case ClothEditMode.Scale:
                    if (!_dragging) return;
                    _effectivePixels += (screenX - _lastScreenX) * factor;
                    _lastScreenX = screenX;
                    if (_strokeMode == ClothEditMode.Rotate)
                    {
                        _grabAngle = AngleFromPixels(_effectivePixels, snap);
                        _session.UpdateGrab(_dragStartPoint, Quaternion.AngleAxis(_grabAngle, _rotateAxis), 1f);
                        if (Mathf.Abs(_grabAngle) > 1e-4f) _moved = true;
                    }
                    else
                    {
                        _grabScale = ScaleFromPixels(_effectivePixels);
                        _session.UpdateGrab(_dragStartPoint, Quaternion.identity, _grabScale);
                        if (Mathf.Abs(_grabScale - 1f) > 1e-4f) _moved = true;
                    }
                    break;

                case ClothEditMode.Relax:
                    _lastScreenX = screenX;
                    UpdateHover(ray);
                    if (_opOpen) RelaxHere();
                    break;

                case ClothEditMode.Select:
                    _lastScreenX = screenX;
                    SelectPaintDrag(ray);
                    break;

                default:
                    _lastScreenX = screenX;
                    UpdateHover(ray);
                    if (_opOpen) PaintPins();
                    break;
            }
            Notify();
        }

        bool TryGetDragPoint(Ray ray, out Vector3 point)
        {
            if (_dragOnLine) return TryClosestPointOnLine(ray, _dragStartPoint, _dragLineDirection, out point);
            if (_dragPlane.Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        /// <summary>
        /// 離した。ピン留めなら確定する。つまんでいた（回す・拡大縮小・元に戻すブラシも）なら仕上げに入る（以降の <see cref="Tick"/> で進む）。
        /// 動かさずに離したときは、何も記録せずに終わる。
        /// </summary>
        public void PointerUp()
        {
            if (!EnsureAlive() || !_pointerDown) return;
            _pointerDown = false;
            InvalidatePreview();

            if (_strokeMode == ClothEditMode.Select)
            {
                SelectPaintEnd();
            }
            else if (_strokeMode == ClothEditMode.Pin || _strokeMode == ClothEditMode.Unpin)
            {
                CommitOperation();
            }
            else if (_strokeMode == ClothEditMode.Relax)
            {
                // 近づけた分の布の伸びを、仕上げで直す。何も変わっていなければ、そのまま終わる
                if (_opOpen && !_session.MatchesState(_state))
                {
                    // 戻した所は留めておく。周りの布の側が、戻した形に合わせる
                    _session.HoldChangedParticles(_state, _relaxHeld);
                    _settling = true;
                    _settleClockPending = true;
                }
                else
                {
                    CommitOperation();
                }
            }
            else if (_dragging)
            {
                if (_moved)
                {
                    _dragging = false;
                    _settling = true;
                    _settleClockPending = true;
                }
                else
                {
                    FinishStroke();   // クリックしただけ。計算せず、Undo の記録も残さない
                }
            }
            Notify();
        }

        /// <summary>
        /// 操作を取り消す（Esc）。ドラッグ中・仕上げ中・ピンを塗っている途中なら、押す前の形に戻し、Undo の記録も残さない。
        /// 取り消すものがあれば true。
        /// </summary>
        public bool Cancel()
        {
            if (!EnsureAlive()) return false;
            bool busy = _dragging || _settling || _opOpen;
            if (!busy && (_selectPainting || (_pointerDown && _strokeMode == ClothEditMode.Select)))
            {
                // 塗って選んでいる途中。選んだ分はそのまま残す（選択は Undo に入れないので、戻すものがない）
                _selectPainting = false;
                _pointerDown = false;
                return true;
            }
            if (!(_pointerDown || busy)) return false;

            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            _selectPainting = false;
            EndTransformState();
            _relaxHeld.Clear();   // ピンの状態は、下で操作の前の形（_state）に戻すときに戻る
            if (_session.IsGrabbing) _session.Release();
            if (_opOpen)
            {
                _opOpen = false;
                RevertRecords(_opGroup);
            }
            // _state には、操作を始める前の形が入っている（書き込むのは確定のとき）
            _session.ReadState(_state);
            AfterShapeChanged();
            return true;
        }

        // 対称がオンなら、鏡像の側にも同じように塗る
        void PaintPins()
        {
            if (!HasHover) return;
            MirrorPlane? mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null;
            _session.PaintPins(HoverPoint, _brushRadius, _strokeMode == ClothEditMode.Pin, _hoverTriangle, mirror);
        }

        // ---- 範囲を指定したピン留め（Shift＋ドラッグの矩形、Ctrl＋クリックで部品全体、すべて外す、反転） ----
        // どれも Unity の Undo の1段になる。変えた粒子の数を返す

        /// <summary>
        /// 画面上の矩形に入る粒子をピン留め（または解除）する。カメラ側を向いた粒子だけにするかは、設定の <see cref="ClothEditSettings.PinRectFrontOnly"/>。
        /// 対称がオンなら、鏡像の粒子も同じようにする。
        /// </summary>
        /// <param name="viewportRect">矩形（ビューポート座標。0〜1、左下が原点）</param>
        public int PinInRect(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 cameraForward, bool orthographic, Rect viewportRect, bool pinned)
        {
            if (!EnsureAlive()) return 0;
            MirrorPlane? mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null;
            return ChangePins(pinned ? L10n.Tr("GP02 範囲でピン留め", "GP02 Pin Area") : L10n.Tr("GP02 範囲でピン解除", "GP02 Unpin Area"),
                () => _session.PinInViewport(viewProjection, cameraPosition, cameraForward, orthographic, viewportRect, pinned, _settings.PinRectFrontOnly, mirror));
        }

        /// <summary>レイが当たった布の部品（島）全体をピン留め（または解除）する。当たらなければ 0。</summary>
        public int PinIslandAt(Ray ray, bool pinned)
        {
            if (!EnsureAlive() || !UpdateHover(ray)) return 0;
            int triangle = _hoverTriangle;
            MirrorPlane? mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null;
            return ChangePins(pinned ? L10n.Tr("GP02 部品全体をピン留め", "GP02 Pin Part") : L10n.Tr("GP02 部品全体をピン解除", "GP02 Unpin Part"),
                () => _session.PinIsland(triangle, pinned, mirror));
        }

        /// <summary>ピンをすべて外す。</summary>
        public int ClearPins()
        {
            if (!EnsureAlive()) return 0;
            return ChangePins(L10n.Tr("GP02 ピンをすべて外す", "GP02 Clear Pins"), () => _session.ClearPins());
        }

        /// <summary>ピン留めの粒子とそうでない粒子を入れ替える。</summary>
        public int InvertPins()
        {
            if (!EnsureAlive()) return 0;
            return ChangePins(L10n.Tr("GP02 ピンを反転", "GP02 Invert Pins"), () => _session.InvertPins());
        }

        // ピンを変える操作を、Undo の1段にまとめる。何も変わらなければ記録を残さない
        int ChangePins(string undoName, Func<int> change)
        {
            FinishStroke();
            BeginOperation(undoName);
            int changed = change();
            CommitOperation();
            InvalidatePreview();
            Notify();
            return changed;
        }

        void RelaxHere()
        {
            if (!HasHover) return;
            MirrorPlane? mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null;
            _session.RelaxAt(HoverPoint, _hoverTriangle, _brushRadius, _settings.RelaxStrength, _settings.GetFalloffCurve(), _settings.DistanceMode, mirror);
        }

        // ---- 選んで動かす（点の選択と、選んだ点の変形） ----
        // 選ぶ操作は、選んだ粒子の集合を変えるだけで、布の形は変えない。Unity の Undo には入れない
        // （Ctrl+Z は形とピンを戻し、選択は残る）。変えたら Changed で知らせる。
        // 対称がオンのときも、選ぶ操作は鏡像の側を足さない。動かすときに ClothSolver が鏡像を鏡に映した動きで動かすため
        // （両側を選ぶと、ソルバーは中央線をまたいだとみなして、動きを鏡の面に沿う成分だけにしてしまう）。

        /// <summary>選んでいる粒子の数。</summary>
        public int SelectionCount => _selectionCount;

        /// <summary>選んでいる粒子の番号（昇順）。選択が変わるたびに新しい配列になる。つまむときに同じ配列を毎コマ渡している。</summary>
        public IReadOnlyList<int> SelectedParticles => _selectedList;

        /// <summary>粒子が選ばれているか。</summary>
        public bool IsSelected(int particle) => _selected != null && (uint)particle < (uint)_selected.Length && _selected[particle];

        /// <summary>選択が変わるたびに増える番号。画面の表示のキャッシュを作り直すかの判断に使う。</summary>
        public int SelectionVersion => _selectionVersion;

        /// <summary>
        /// 最後にクリックして選んだ、面の上の点（ワールド）。クリックした所の粒子に付いていくので、形が変わってもその場所を指す。
        /// まだクリックして選んでいなければ原点。
        /// </summary>
        public Vector3 LastClickPoint
        {
            get
            {
                if (_lastClickParticle >= 0 && _session != null && _lastClickParticle < _session.ParticleCount)
                {
                    NVector3 p = _session.Solver.Positions[_lastClickParticle];
                    return new Vector3(p.X, p.Y, p.Z) + _lastClickOffset;
                }
                return _lastClickWorld;
            }
        }

        /// <summary>
        /// <see cref="LastClickPoint"/> が使えるか。クリックして選んだ所の粒子が、まだ選ばれているときだけ true
        /// （外されたあとは、中心の計算には使われない）。
        /// </summary>
        public bool HasLastClickPoint => _hasLastClick && IsSelected(_lastClickParticle);

        // 選択を空にして、編集中でなければ配列も持たない。バージョンは増やす（画面のキャッシュを捨てさせる）
        void ResetSelectionState()
        {
            _selected = null;
            _selectedList = new int[0];
            _selectionCount = 0;
            _selectionVersion++;
            _hasLastClick = false;
            _lastClickParticle = -1;
            _mirrored.Clear();
            _mirroredVersion = -1;
            _weightCount = 0;
            _weightVersion = -1;
            _pivotVersion = -1;
            _frameVersion = -1;
        }

        // _selected の中身から、数と番号の配列を作り直し、バージョンを増やして知らせる
        void CommitSelection()
        {
            int count = 0;
            for (int i = 0; i < _selected.Length; i++)
            {
                if (_selected[i]) count++;
            }
            var list = new int[count];
            int k = 0;
            for (int i = 0; i < _selected.Length; i++)
            {
                if (_selected[i]) list[k++] = i;
            }
            _selectedList = list;
            _selectionCount = count;
            _selectionVersion++;
            Notify();
        }

        // particles を選ぶ（deselect なら外す）。変わった数を返す
        int ApplySelection(List<int> particles, bool deselect)
        {
            int changed = 0;
            bool value = !deselect;
            for (int k = 0; k < particles.Count; k++)
            {
                int i = particles[k];
                if ((uint)i >= (uint)_selected.Length || _selected[i] == value) continue;
                _selected[i] = value;
                changed++;
            }
            if (changed > 0) CommitSelection();
            return changed;
        }

        // 選んだ点の変形が仕上げ中なら確定してから、選択を変える
        void FinishSelectionTransform()
        {
            if (_setGrab) FinishStroke();
        }

        void RegisterClick(Vector3 point, int triangle)
        {
            int particle = _session.FindParticle(point, triangle);
            if (particle < 0) return;
            NVector3 p = _session.Solver.Positions[particle];
            _lastClickParticle = particle;
            _lastClickOffset = point - new Vector3(p.X, p.Y, p.Z);
            _lastClickWorld = point;
            _hasLastClick = true;
        }

        // カーソルの下をブラシ半径で選ぶ（外す）。半径が小さくて粒子が入らないときは、一番近い粒子を1つ
        void PaintSelectionHere(bool click)
        {
            if (!HasHover) return;
            _session.CollectInSphere(HoverPoint, _brushRadius, _hoverTriangle, _selectWork);
            if (_selectWork.Count == 0)
            {
                int nearest = _session.FindParticle(HoverPoint, _hoverTriangle);
                if (nearest >= 0) _selectWork.Add(nearest);
            }
            ApplySelection(_selectWork, _selectDeselect);
            if (click && !_selectDeselect) RegisterClick(HoverPoint, _hoverTriangle);
        }

        /// <summary>
        /// 塗って選ぶ操作を始める。カーソルの下の布の、ブラシ半径（<see cref="BrushRadius"/>）以内の粒子を、当たった部品（島）の中で選ぶ。
        /// deselect なら、選びではなく外す。当たらなければ false。<see cref="Mode"/> が <see cref="ClothEditMode.Select"/> のときの
        /// <see cref="PointerDown"/> は、これ（選ぶ側）に回る。続けて <see cref="SelectPaintDrag"/>、最後に <see cref="SelectPaintEnd"/>。
        /// 最初にクリックした所は、<see cref="LastClickPoint"/>（中心を「最後にクリックした点」にしたとき）になる。
        /// </summary>
        public bool SelectPaintBegin(Ray ray, bool deselect)
        {
            if (!EnsureAlive() || !UpdateHover(ray)) return false;
            FinishSelectionTransform();
            _selectPainting = true;
            _selectDeselect = deselect;
            PaintSelectionHere(true);
            Notify();
            return true;
        }

        /// <summary>塗って選んでいる途中で、カーソルを動かした。通った所を選ぶ（外す）。</summary>
        public void SelectPaintDrag(Ray ray)
        {
            if (!_selectPainting || !EnsureAlive()) return;
            if (UpdateHover(ray)) PaintSelectionHere(false);
            Notify();
        }

        /// <summary>塗って選ぶ操作を終える。</summary>
        public void SelectPaintEnd()
        {
            _selectPainting = false;
        }

        /// <summary>
        /// 画面上の矩形に入る粒子を選ぶ（deselect なら外す）。変わった数を返す。
        /// <see cref="ClothEditSettings.SelectThroughOcclusion"/> が false なら、カメラ側を向いた粒子で、他の布に隠れていないものだけ。
        /// true なら、裏側や隠れた粒子も選ぶ。
        /// </summary>
        /// <param name="viewportRect">矩形（ビューポート座標。0〜1、左下が原点）</param>
        public int SelectInRect(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 cameraForward, bool orthographic, Rect viewportRect, bool deselect)
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            bool visibleOnly = !_settings.SelectThroughOcclusion;
            _session.CollectInViewport(viewProjection, cameraPosition, cameraForward, orthographic, viewportRect, visibleOnly, visibleOnly, _selectWork);
            return ApplySelection(_selectWork, deselect);
        }

        /// <summary>レイが当たった布の部品（島）全体を選ぶ（deselect なら外す）。変わった数を返す。当たらなければ 0。</summary>
        public int SelectIslandAt(Ray ray, bool deselect)
        {
            if (!EnsureAlive() || !UpdateHover(ray)) return 0;
            FinishSelectionTransform();
            int triangle = _hoverTriangle;
            _session.CollectIsland(triangle, _selectWork);
            int changed = ApplySelection(_selectWork, deselect);
            if (!deselect && _selectWork.Count > 0) RegisterClick(HoverPoint, triangle);
            return changed;
        }

        /// <summary>すべての粒子を選ぶ。新しく選んだ数を返す。</summary>
        public int SelectAll()
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            int added = _selected.Length - _selectionCount;
            if (added > 0)
            {
                for (int i = 0; i < _selected.Length; i++) _selected[i] = true;
                CommitSelection();
            }
            return added;
        }

        /// <summary>選択をすべて外す。外した数を返す。</summary>
        public int ClearSelection()
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            int removed = _selectionCount;
            if (removed > 0)
            {
                Array.Clear(_selected, 0, _selected.Length);
                CommitSelection();
            }
            return removed;
        }

        /// <summary>選んだ粒子と、選んでいない粒子を入れ替える。変えた数（全粒子）を返す。</summary>
        public int InvertSelection()
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            for (int i = 0; i < _selected.Length; i++) _selected[i] = !_selected[i];
            CommitSelection();
            return _selected.Length;
        }

        /// <summary>選択を1周ぶん広げる（伸びの制約でつながった隣を足す。同じ島の中だけ）。足した数を返す。</summary>
        public int GrowSelection()
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            int before = _selectionCount;
            _session.GrowSelection(_selected);
            CommitSelection();
            return _selectionCount - before;
        }

        /// <summary>選択を1周ぶん狭める（選んでいない隣がある粒子を外す。同じ島の中だけ）。外した数を返す。</summary>
        public int ShrinkSelection()
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            int before = _selectionCount;
            _session.ShrinkSelection(_selected);
            CommitSelection();
            return before - _selectionCount;
        }

        /// <summary>
        /// 選んだ粒子の鏡像の粒子を足す。鏡の面は対称編集の設定（軸とずれ）から作り、対称がオフでも設定の軸で映す。
        /// アバターの基準の Transform が無ければ何もしない。足した数を返す。
        /// </summary>
        public int MirrorSelection()
        {
            if (!EnsureAlive() || _selectionCount == 0) return 0;
            if (!ClothEditSymmetry.TryBuildMirrorPlane(_settings, _avatarRoot, true, out MirrorPlane plane)) return 0;
            FinishSelectionTransform();
            _session.CollectMirror(_selectedList, plane, _selectWork);
            return ApplySelection(_selectWork, false);
        }

        /// <summary>ピン留めされている粒子を選択に足す（すでに選んでいる粒子は残す）。足した数を返す。</summary>
        public int SelectPinned()
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            _session.GetPinnedParticles(_selectWork);
            return ApplySelection(_selectWork, false);
        }

        /// <summary>選んだ粒子をピン留めする（pinned が false なら解除する）。Unity の Undo の1段。変えた粒子の数を返す。</summary>
        public int PinSelected(bool pinned)
        {
            if (!EnsureAlive() || _selectionCount == 0) return 0;
            int[] particles = _selectedList;
            return ChangePins(pinned ? L10n.Tr("GP02 選んだ点をピン留め", "GP02 Pin Selection") : L10n.Tr("GP02 選んだ点のピン解除", "GP02 Unpin Selection"),
                () => _session.SetPinned(particles, pinned));
        }

        /// <summary>
        /// 対称がオンのとき、選んだ粒子の鏡像の粒子（選んでいないもの）。動かすと、これらが鏡に映した動きで動く。画面で薄く見せる用。
        /// 選択か対称の設定が変わったときだけ計算し直す。対称がオフなら空。
        /// </summary>
        public IReadOnlyList<int> MirroredSelection
        {
            get
            {
                RefreshMirroredSelection();
                return _mirrored;
            }
        }

        void RefreshMirroredSelection()
        {
            bool has = _session != null && TryGetMirrorPlane(out _mirroredPlaneNow);
            if (_mirroredVersion == _selectionVersion && has == _mirroredHasPlane && (!has || SamePlane(_mirroredPlane, _mirroredPlaneNow))) return;
            _mirroredVersion = _selectionVersion;
            _mirroredHasPlane = has;
            _mirroredPlane = _mirroredPlaneNow;
            _mirrored.Clear();
            if (!has || _selectionCount == 0) return;
            _session.CollectMirror(_selectedList, _mirroredPlaneNow, _selectWork2);
            foreach (int j in _selectWork2)
            {
                if (!_selected[j]) _mirrored.Add(j);
            }
        }

        MirrorPlane _mirroredPlaneNow;

        // ---- ハンドルの基準 ----

        /// <summary>
        /// ハンドルを置く中心（ワールド）。<see cref="ClothEditSettings.PivotMode"/> に従う。
        /// Center は選んだ点の今の位置の平均、LastClicked は最後にクリックして選んだ面の上の点（無ければ Center）。
        /// 選んだ点がなければ原点。<see cref="IsTransformingSelection"/> の間は、押したときの値のまま動かない（ハンドルが逃げない）。
        /// </summary>
        public Vector3 SelectionPivot
        {
            get
            {
                if (_xformActive) return _xformPivot;
                if (_session == null || _selectionCount == 0) return Vector3.zero;
                if (_settings.PivotMode == PivotKind.LastClicked && HasLastClickPoint) return LastClickPoint;
                if (_pivotVersion != _selectionVersion || _pivotShape != _session.ShapeVersion)
                {
                    NVector3 sum = NVector3.Zero;
                    var positions = _session.Solver.Positions;
                    for (int k = 0; k < _selectedList.Length; k++) sum += positions[_selectedList[k]];
                    sum /= _selectedList.Length;
                    _centerCache = new Vector3(sum.X, sum.Y, sum.Z);
                    _pivotVersion = _selectionVersion;
                    _pivotShape = _session.ShapeVersion;
                }
                return _centerCache;
            }
        }

        /// <summary>
        /// ハンドルの向き。<see cref="ClothEditSettings.HandleSpace"/> に従う。
        /// Global は単位、Avatar はアバターの基準の Transform（対称編集と同じもの）の向き、
        /// Normal は選んだ点の面の法線の平均を Z にし、Y はワールドの上をなるべく保った向き（法線が求まらなければ単位）。
        /// <see cref="IsTransformingSelection"/> の間は、押したときの値のまま動かない。
        /// </summary>
        public Quaternion HandleRotation
        {
            get
            {
                if (_xformActive) return _xformFrame;
                switch (_settings.HandleSpace)
                {
                    case HandleOrientation.Avatar:
                        return _avatarRoot != null ? _avatarRoot.rotation : Quaternion.identity;
                    case HandleOrientation.Normal:
                        if (_session == null || _selectionCount == 0) return Quaternion.identity;
                        if (_frameVersion != _selectionVersion || _frameShape != _session.ShapeVersion || _frameSpace != HandleOrientation.Normal)
                        {
                            _frameCache = _session.TryGetAverageNormal(_selectedList, out Vector3 normal) ? FrameFromNormal(normal) : Quaternion.identity;
                            _frameVersion = _selectionVersion;
                            _frameShape = _session.ShapeVersion;
                            _frameSpace = HandleOrientation.Normal;
                        }
                        return _frameCache;
                    default:
                        return Quaternion.identity;
                }
            }
        }

        // Z が法線で、Y がワールドの上にできるだけ近い向き。法線が真上か真下なら、上の代わりにワールドの前を使う
        static Quaternion FrameFromNormal(Vector3 normal)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9999f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(normal, up);
        }

        // ---- 選んだ点の変形 ----

        /// <summary>選んだ点を動かしている途中か（<see cref="BeginSelectionTransform"/> から <see cref="EndSelectionTransform"/> まで）。仕上げ中は含まない（<see cref="IsSettling"/>）。</summary>
        public bool IsTransformingSelection => _xformActive;

        // 選んだ点を動かす範囲の決め方（なめらかに減る半径・カーブ・距離の測り方・対称）
        BrushOptions BuildSelectionOptions()
        {
            float radius = _settings.SelectionFalloffRadius;
            return new BrushOptions
            {
                Radius = radius >= 0f ? Mathf.Min(radius, ClothEditSettings.MaxSelectionFalloffRadius) : 0f,
                Falloff = _settings.GetSelectionFalloffCurve(),
                DistanceMode = _settings.DistanceMode,
                Mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null,
            };
        }

        /// <summary>
        /// 選んだ点を動かし始める（ハンドルを押した）。選択が空なら false。押したときの中心（<see cref="SelectionPivot"/>）と向き
        /// （<see cref="HandleRotation"/>）を覚え、選んだ点を集合でつまむ。続けて <see cref="UpdateSelectionTransform"/> を毎コマ呼び、
        /// 離したら <see cref="EndSelectionTransform"/>。Unity の Undo の1段になる。Esc の <see cref="Cancel"/> で押す前に戻る。
        /// 選んだ点は、設定の半径（<see cref="ClothEditSettings.SelectionFalloffRadius"/>）の中でなめらかに減る重みで、周りの布も連れていく。
        /// ピン留めした粒子は、選んでいても動かない。
        /// </summary>
        public bool BeginSelectionTransform()
        {
            if (!EnsureAlive() || _selectionCount == 0) return false;
            FinishStroke();
            Vector3 pivot = SelectionPivot;
            Quaternion frame = HandleRotation;
            BeginOperation(L10n.Tr("GP02 選んだ点を動かす", "GP02 Move Selection"));
            if (!_session.BeginGrabSet(_selectedList, BuildSelectionOptions()))
            {
                CommitOperation();
                return false;
            }
            _xformPivot = pivot;
            _xformFrame = frame;
            _xformRawTranslation = _xformTranslation = Vector3.zero;
            _xformRawRotation = _xformRotation = Quaternion.identity;
            _xformRawLogScale = _xformLogScale = Vector3.zero;
            _xformPreciseUsed = false;
            _xformShapeOnly = !_settings.SelectionMovesAsCloth;
            _xformActive = true;
            _setGrab = true;
            _strokeMode = ClothEditMode.Select;
            _moved = false;
            _dragging = true;
            _settling = false;
            CaptureGrabRange(true);
            Notify();
            return true;
        }

        // 拡大率の対数を取る前に、0 や負の値を避ける
        const float MinAxisScale = 1e-3f;

        static Vector3 LogScale(Vector3 scale)
        {
            return new Vector3(Mathf.Log(Mathf.Max(scale.x, MinAxisScale)), Mathf.Log(Mathf.Max(scale.y, MinAxisScale)), Mathf.Log(Mathf.Max(scale.z, MinAxisScale)));
        }

        /// <summary>
        /// 選んだ点の動きを更新する。値は押したときからの合計（ハンドルが返す値そのまま）。
        /// translation はワールドの平行移動、rotation はワールドでの回転の差、scale は <see cref="HandleRotation"/>（押したときの向き）の軸に沿った倍率。
        /// 回転と拡大縮小の中心は、押したときの <see cref="SelectionPivot"/>。回転のあとに平行移動を足す。
        /// precise（Shift）なら、前回からの増分を <see cref="PrecisionFactor"/> 倍して足す
        /// （平行移動は差、回転は Slerp で増分を縮め、拡大は対数で増分を縮める）。途中で切り替えても動かし先は飛ばない。
        /// </summary>
        public void UpdateSelectionTransform(Vector3 translation, Quaternion rotation, Vector3 scale, bool precise)
        {
            if (!_xformActive || !EnsureAlive()) return;
            float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            rotation = length > 1e-6f ? new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length) : Quaternion.identity;
            Vector3 logScale = LogScale(scale);

            if (precise) _xformPreciseUsed = true;
            if (!_xformPreciseUsed)
            {
                // 精密操作を使っていなければ、受け取った値そのまま（丸め誤差を足さない）
                _xformTranslation = translation;
                _xformRotation = rotation;
                _xformLogScale = logScale;
            }
            else
            {
                float factor = precise ? PrecisionFactor : 1f;
                _xformTranslation += (translation - _xformRawTranslation) * factor;
                Quaternion delta = rotation * Quaternion.Inverse(_xformRawRotation);
                if (factor != 1f) delta = Quaternion.Slerp(Quaternion.identity, delta, factor);
                _xformRotation = Quaternion.Normalize(delta * _xformRotation);
                _xformLogScale += (logScale - _xformRawLogScale) * factor;
            }
            _xformRawTranslation = translation;
            _xformRawRotation = rotation;
            _xformRawLogScale = logScale;

            Vector3 effectiveScale = _xformPreciseUsed
                ? new Vector3(Mathf.Exp(_xformLogScale.x), Mathf.Exp(_xformLogScale.y), Mathf.Exp(_xformLogScale.z))
                : new Vector3(Mathf.Max(scale.x, MinAxisScale), Mathf.Max(scale.y, MinAxisScale), Mathf.Max(scale.z, MinAxisScale));
            var motion = new BrushMotion(
                ClothEditSymmetry.ToNumerics(_xformTranslation),
                new NQuaternion(_xformRotation.x, _xformRotation.y, _xformRotation.z, _xformRotation.w),
                1f,
                ClothEditSymmetry.ToNumerics(_xformPivot))
            {
                ScaleAxes = ClothEditSymmetry.ToNumerics(effectiveScale),
                ScaleFrame = new NQuaternion(_xformFrame.x, _xformFrame.y, _xformFrame.z, _xformFrame.w),
            };
            _session.UpdateGrabSet(motion);
            if (_xformShapeOnly)
            {
                // 形だけ動かす: 布の計算をせず、範囲を目標の位置へ置く（Tick では計算しない）
                _session.Solver.PlaceBrushAtGoals();
                _session.UpdateCurrentShape();
            }

            if (_xformTranslation.sqrMagnitude > 1e-12f || Quaternion.Angle(_xformRotation, Quaternion.identity) > 1e-3f
                || (effectiveScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                _moved = true;
            }
            Notify();
        }

        /// <summary>
        /// 選んだ点を動かし終えた（ハンドルを離した）。動いていれば仕上げに入る（以降の <see cref="Tick"/> で進み、終わると Undo の1段として確定）。
        /// 動いていなければ、記録を残さずに終わる。
        /// </summary>
        public void EndSelectionTransform()
        {
            if (!_xformActive) return;
            _xformActive = false;
            if (!EnsureAlive()) return;
            if (_moved && _xformShapeOnly)
            {
                // 形だけ動かしたときは仕上げをしない（布の計算で形が変わらないように）。その場で確定する
                FinishStroke();
                AfterShapeChanged();
                return;
            }
            if (_moved)
            {
                _dragging = false;
                _settling = true;
                _settleClockPending = true;
            }
            else
            {
                FinishStroke();
            }
            Notify();
        }

        /// <summary>
        /// 選んだ点を、数値で指定した動きで動かす。<see cref="BeginSelectionTransform"/>、<see cref="UpdateSelectionTransform"/>（精密操作なし）、
        /// <see cref="EndSelectionTransform"/> を1回で行うので、ハンドルで同じ値まで動かしたときと同じ結果になる。仕上げは以降の <see cref="Tick"/>。
        /// 選択が空なら false。動きが無い（単位の値）ときも何もせず false。
        /// </summary>
        /// <param name="translation">ワールドの平行移動</param>
        /// <param name="rotation">ワールドでの回転</param>
        /// <param name="scale"><see cref="HandleRotation"/> の軸に沿った倍率</param>
        public bool ApplySelectionTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            if (!BeginSelectionTransform()) return false;
            UpdateSelectionTransform(translation, rotation, scale, false);
            bool moved = _moved;
            EndSelectionTransform();
            return moved;
        }

        // 選んだ点の変形を終えた状態にする（確定・取り消し・Undo のとき）
        void EndTransformState()
        {
            if (!_setGrab && !_xformActive) return;
            _setGrab = false;
            _xformActive = false;
            _previewCount = 0;
            _previewShown = false;
        }

        // ---- なめらかに減る範囲の表示 ----

        /// <summary>
        /// 選んだ点の周りの、動く粒子（選んでいないもの）の数。選んだ点を動かしたときに、設定の半径・カーブ・距離の測り方で重みがつく粒子で、
        /// 対称がオンなら鏡像の側も含む。選択・半径・カーブ・距離の測り方・対称が変わったときだけ計算し直す。上限は <see cref="MaxSelectionWeightParticles"/>（超えるときは間引く）。
        /// <see cref="ClothEditSettings.ShowSelectionWeights"/> がオフ、半径が 0、選択が空のときは 0。選んだ点そのもの（重み 1）は含まないので、<see cref="SelectedParticles"/> を使う。
        /// </summary>
        public int SelectionWeightCount
        {
            get
            {
                RefreshSelectionWeights();
                return _weightCount;
            }
        }

        /// <summary>k 番目の粒子の番号（0 〜 <see cref="SelectionWeightCount"/> - 1）。位置は <c>Session.Solver.Positions</c> から読む。</summary>
        public int SelectionWeightParticle(int k) => _weightParticles[k];

        /// <summary>k 番目の粒子の重み（0 より大きく 1 以下）。</summary>
        public float SelectionWeightValue(int k) => _weightValues[k];

        void RefreshSelectionWeights()
        {
            bool show = _session != null && _settings.ShowSelectionWeights && _selectionCount > 0 && _settings.SelectionFalloffRadius > 0f;
            if (!show)
            {
                _weightCount = 0;
                _weightShown = false;
                _weightVersion = -1;
                return;
            }
            BrushOptions options = BuildSelectionOptions();
            bool hasMirror = options.Mirror.HasValue;
            MirrorPlane plane = hasMirror ? options.Mirror.Value : default;
            // 直線距離で測るときは、形が変わると範囲も変わる。つまんでいる間は毎コマ計算しない
            int shape = options.DistanceMode == BrushDistanceMode.Linear && !IsBusy ? _session.ShapeVersion : 0;
            if (_weightShown && _weightVersion == _selectionVersion && _weightRadius == options.Radius && ReferenceEquals(_weightCurve, options.Falloff)
                && _weightMode == options.DistanceMode && _weightHasMirror == hasMirror && (!hasMirror || SamePlane(_weightPlane, plane)) && _weightShape == shape)
            {
                return;
            }
            _weightShown = true;
            _weightVersion = _selectionVersion;
            _weightRadius = options.Radius;
            _weightCurve = options.Falloff;
            _weightMode = options.DistanceMode;
            _weightHasMirror = hasMirror;
            _weightPlane = plane;
            _weightShape = shape;

            int n = _session.ParticleCount;
            if (_weightWorkIndices == null || _weightWorkIndices.Length != n)
            {
                _weightWorkIndices = new int[n];
                _weightWorkValues = new float[n];
            }
            int total = _session.CollectSetRange(_selectedList, options, _weightWorkIndices, _weightWorkValues, n);
            int outside = 0;
            for (int k = 0; k < total; k++)
            {
                if (!_selected[_weightWorkIndices[k]]) outside++;
            }
            int stride = outside <= MaxSelectionWeightParticles ? 1 : (outside + MaxSelectionWeightParticles - 1) / MaxSelectionWeightParticles;
            _weightCount = 0;
            int seen = 0;
            for (int k = 0; k < total && _weightCount < MaxSelectionWeightParticles; k++)
            {
                int i = _weightWorkIndices[k];
                if (_selected[i] || seen++ % stride != 0) continue;
                _weightParticles[_weightCount] = i;
                _weightValues[_weightCount] = _weightWorkValues[k];
                _weightCount++;
            }
        }

        // ---- 編集をやり直すときの選択の引き継ぎ ----

        // 作り直す前の選択。粒子の番号が同じ作り直し（保存後）は番号で、粒子を置き直す作り直しは位置と島で引き継ぐ
        sealed class SelectionCarry
        {
            public int[] Particles;
            public List<Vector3> Positions;
            public List<int> Islands;
            public bool HasLastClick;
            public Vector3 LastClick;
        }

        SelectionCarry CaptureSelection()
        {
            if (_session == null || _selectionCount == 0) return null;
            var carry = new SelectionCarry
            {
                Particles = _selectedList,
                Positions = new List<Vector3>(_selectedList.Length),
                Islands = new List<int>(_selectedList.Length),
                HasLastClick = HasLastClickPoint,
                LastClick = LastClickPoint,
            };
            var positions = _session.Solver.Positions;
            int[] particleIsland = _session.Cloth.ParticleIsland;
            foreach (int i in _selectedList)
            {
                carry.Positions.Add(new Vector3(positions[i].X, positions[i].Y, positions[i].Z));
                carry.Islands.Add(particleIsland[i]);
            }
            return carry;
        }

        void RestoreSelection(SelectionCarry carry, bool sameParticles)
        {
            int n = _session.ParticleCount;
            _selected = new bool[n];
            if (carry != null)
            {
                if (sameParticles)
                {
                    foreach (int i in carry.Particles)
                    {
                        if ((uint)i < (uint)n) _selected[i] = true;
                    }
                }
                else
                {
                    _session.CollectNear(carry.Positions, _session.Cloth.Spacing * 0.75f, carry.Islands, _selectWork);
                    foreach (int i in _selectWork) _selected[i] = true;
                }
            }
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (_selected[i]) count++;
            }
            var list = new int[count];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (_selected[i]) list[k++] = i;
            }
            _selectedList = list;
            _selectionCount = count;
            _selectionVersion++;
            if (carry != null && carry.HasLastClick && count > 0) RegisterClick(carry.LastClick, -1);
        }

        // ---- 時間の経過 ----

        /// <summary>
        /// 一定時間ごと（EditorApplication.update）に呼ぶ。つまんでいる間と仕上げの間、計算を進める。
        /// 対象が消されていたら編集を終える。
        /// </summary>
        /// <param name="now">現在の時刻（秒。仕上げの時間切れの判定に使う）</param>
        public void Tick(double now)
        {
            if (!EnsureAlive()) return;
            if (!_dragging && !_settling) return;
            // 動かしていないうちは計算しない（クリックしただけで形が変わらないように）
            if (_dragging && !_moved) return;
            // 形だけ動かしている間も計算しない（UpdateSelectionTransform が置いた形のまま）
            if (_xformActive && _xformShapeOnly) return;

            if (_settleClockPending)
            {
                _settleStart = now;
                _settleClockPending = false;
            }

            _session.Solver.SolveFor(_settings.SolveMilliseconds);
            _session.UpdateCurrentShape();
            LastStretch = _session.Solver.MeasureMaxStretch();

            // つまみを外すのは仕上げが終わってから。先に外すと、つまんでいた範囲が戻ってしまう
            if (_settling && (LastStretch <= _settings.SettleTolerance || now - _settleStart > _settings.SettleTimeoutSeconds))
            {
                _session.ReleaseHeld(_relaxHeld);
                _session.Release();
                _settling = false;
                EndTransformState();
                CommitOperation();
                InvalidatePreview();
            }
            Notify();
        }

        // 仕上げを待たずに、つまみを外して確定する（次の操作や保存の前など）
        void FinishStroke()
        {
            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            EndTransformState();
            if (_session != null) _session.ReleaseHeld(_relaxHeld);
            if (_session != null && _session.IsGrabbing) _session.Release();
            CommitOperation();
        }

        // ---- Undo ----

        // 変更の直前に呼ぶ。RecordObject は配列の要素ごとに差分を作り、粒子が数万あると遅いので Complete を使う
        void BeginOperation(string name)
        {
            // 前の操作と同じ Undo のグループにならないように区切る
            Undo.IncrementCurrentGroup();
            _opGroup = Undo.GetCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_state, name);
            _opOpen = true;
        }

        // 変更が終わったら呼ぶ。何も変わっていなければ、積んだ記録を取り消す（空の Undo を残さない）
        void CommitOperation()
        {
            if (!_opOpen) return;
            _opOpen = false;
            if (_session == null || _state == null) return;

            if (_session.MatchesState(_state))
            {
                RevertRecords(_opGroup);
                return;
            }
            _session.WriteState(_state);
            Undo.IncrementCurrentGroup();
        }

        void RevertRecords(int group)
        {
            _ignoreUndoCallback = true;
            try { Undo.RevertAllDownToGroup(group); }
            finally { _ignoreUndoCallback = false; }
        }

        /// <summary>Unity の Undo を1つ戻す（途中の操作は確定してから）。この後、編集が作り直されることがある。</summary>
        public void UndoLast()
        {
            if (_session != null) FinishStroke();
            Undo.PerformUndo();
        }

        /// <summary>Unity の Undo をやり直す（途中の操作は確定してから）。</summary>
        public void RedoLast()
        {
            if (_session != null) FinishStroke();
            Undo.PerformRedo();
        }

        /// <summary>
        /// Undo / Redo が行われた通知（<c>Undo.undoRedoPerformed</c>）。コンストラクターで登録済みなので、通常は呼ばなくてよい。
        /// </summary>
        public void OnUndoRedoPerformed()
        {
            if (_session == null || _ignoreUndoCallback) return;

            // 仕上げ中・ドラッグ中なら止める。書き込まない記録は、Undo がすでに戻している
            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            _opOpen = false;
            _selectPainting = false;
            EndTransformState();
            _relaxHeld.Clear();
            if (_session.IsGrabbing) _session.Release();

            if (!_session.IsTargetUnchanged())
            {
                // 保存そのもの（メッシュの差し替えやシェイプキーの重み）が戻された、または対象が消えた。今のレンダラーで作り直す
                RebuildSession();
                return;
            }

            if (_session.ReadState(_state)) AfterShapeChanged();
        }

        void RebuildSession()
        {
            // 消えた・編集できなくなったレンダラーは外して、残りで作り直す
            _targets.RemoveAll(renderer => renderer == null || GetStartProblem(renderer) != null);
            _target = _targets.Count > 0 ? _targets[0] : null;
            if (_target == null)
            {
                End();
                return;
            }
            var pins = new List<Vector3>();
            var pinIslands = new List<int>();
            _session.GetPinnedPositions(pins, pinIslands);
            // 粒子を置き直すと番号が変わるので、選んだ点も位置と島から探し直す
            SelectionCarry carry = CaptureSelection();
            StartSession(pins, pinIslands, null, null, carry, out _);
        }

        void AfterShapeChanged()
        {
            LastStretch = _session.Solver.MeasureMaxStretch();
            InvalidatePreview();
            Notify();
        }

        // ---- ボタン操作 ----

        /// <summary>すべて元に戻す（ピンは残す）。Undo で戻せる。</summary>
        public void ResetAll()
        {
            if (!EnsureAlive()) return;
            FinishStroke();
            BeginOperation(L10n.Tr("GP02 すべて元に戻す", "GP02 Reset All"));
            _session.ResetAll();
            CommitOperation();
            AfterShapeChanged();
        }

        /// <summary>"GP02_1", "GP02_2" … のうち、今のメッシュのシェイプキーと重ならない名前。編集中でなければ空。</summary>
        public string SuggestShapeName() => _session != null ? _session.SuggestShapeName() : "";

        /// <summary>
        /// 今の形をシェイプキーとして保存し、そのメッシュで編集を続ける（粒子とピンは引き継ぐ）。
        /// 失敗したら false を返し、利用者向けの理由を message に入れる。成功したときの message は保存先の案内。
        /// </summary>
        public bool Save(string shapeName, out string message)
        {
            message = null;
            if (!EnsureAlive())
            {
                message = L10n.Tr("編集中ではありません。「編集を開始」を押してから保存してください。", "You are not editing. Press \"Start Editing\" before saving.");
                return false;
            }

            FinishStroke();
            string name = shapeName == null ? "" : shapeName.Trim();
            if (name.Length == 0)
            {
                message = L10n.Tr("シェイプキーの名前が空です。名前を入れてから保存してください。", "The shape key name is empty. Enter a name before saving.");
                return false;
            }
            if (_session.HasBlendShape(name))
            {
                message = L10n.Tr($"同じ名前のシェイプキー「{name}」が既にあります。別の名前にして保存してください。",
                    $"A shape key named \"{name}\" already exists. Save it under a different name.");
                return false;
            }

            // 保存の Undo（メッシュの差し替えなど）が、直前の編集と同じグループにならないようにする
            Undo.IncrementCurrentGroup();
            Mesh[] savedMeshes;
            try
            {
                // あとで一覧から再編集・分割・書き出しができるよう、データにも記録する（レンダラーごと）。
                // メッシュを差し替える前に行うのは、データの元のメッシュを、差し替える前のメッシュにするため
                if (RecordToData)
                {
                    for (int r = 0; r < _session.RendererCount; r++)
                    {
                        Renderer renderer = _session.Renderers[r];
                        GP02ShapeKeyData data = ShapeKeyLibrary.FindOrCreateData(renderer, DataFolder);
                        _session.GetVertexRange(r, out _, out int vertexCount);
                        if (data != null && data.VertexCount == vertexCount) RecordEntry(data, name, true, r);
                    }
                }
                savedMeshes = _session.ApplyToSceneAll(name, SaveFolder);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = L10n.Tr($"保存できませんでした。保存先「{SaveFolder}」に書き込めるか確認してください（フォルダが読み取り専用になっていないか、同名のファイルが開かれていないか）。\n{ex.Message}",
                    $"Could not save. Check that the save folder \"{SaveFolder}\" is writable (not read-only, and no file of the same name is open).\n{ex.Message}");
                return false;
            }

            Mesh saved = savedMeshes[0];
            LastSavedMesh = saved;
            LastSavedMeshes = savedMeshes;
            var paths = new string[savedMeshes.Length];
            for (int r = 0; r < savedMeshes.Length; r++) paths[r] = AssetDatabase.GetAssetPath(savedMeshes[r]);
            string where = string.Join(", ", paths);
            message = L10n.Tr($"シェイプキー「{name}」を {where} に保存しました。",
                $"Saved the shape key \"{name}\" to {where}.");
            Debug.Log("[GP02] " + message);
            // 保存の Undo が、次の編集と同じグループにならないようにする
            Undo.IncrementCurrentGroup();

            // シェイプキー込みの形（編集中の形そのもの）が新しい基準になる。
            // 粒子は置き直さず、今の位置を静止状態にした布で作り直す。ピンは粒子の番号が同じなので番号で引き継ぐ
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ParticleCloth rebased;
            var pinParticles = new List<int>();
            SelectionCarry carry = CaptureSelection();   // 粒子の番号は同じなので、選択もそのまま引き継ぐ
            try
            {
                rebased = _session.Cloth.Rebase(_session.Solver.Positions);
                _session.GetPinnedParticles(pinParticles);
            }
            catch (Exception ex)
            {
                // 想定外の失敗は、普通の作り直し（粒子の置き直し）に戻す
                Debug.LogException(ex);
                var pins = new List<Vector3>();
                var pinIslands = new List<int>();
                _session.GetPinnedPositions(pins, pinIslands);
                if (!StartSession(pins, pinIslands, null, null, carry, out string retryError)) message += L10n.Tr("\n編集を続けられませんでした。「編集を開始」からやり直してください。\n", "\nCould not continue editing. Press \"Start Editing\" to start over.\n") + retryError;
                return true;
            }
            double rebaseMs = timer.Elapsed.TotalMilliseconds;
            if (!StartSession(null, null, rebased, pinParticles, carry, out string error)) message += L10n.Tr("\n編集を続けられませんでした。「編集を開始」からやり直してください。\n", "\nCould not continue editing. Press \"Start Editing\" to start over.\n") + error;
            Debug.Log($"[GP02] 保存後の作り直し {timer.Elapsed.TotalMilliseconds:F0}ms（うち静止状態の更新 {rebaseMs:F0}ms）");
            return true;
        }

        void Notify()
        {
            Changed?.Invoke();
        }

        // ---- シェイプキーのデータとの連携（別の変更と衝突しないよう、クラスの末尾にまとめている） ----

        /// <summary>
        /// メッシュ空間の差分（シェイプキーのデータの1フレーム分）を、編集中の形として読み込む。Unity の Undo の1段になる。
        /// 頂点数が合わない・編集中でないときは何もせず false。
        /// </summary>
        public bool LoadDeltasAsOperation(Vector3[] deltas)
        {
            return LoadDeltasAsOperation(deltas, null);
        }

        /// <summary>
        /// 差分を読み込み、pinnedVertices（元メッシュの頂点番号）が空でなければ、今のピンをその頂点の近くの粒子に置き換える。
        /// ピンの読み込みも同じ Undo の1段に含まれる。
        /// </summary>
        public bool LoadDeltasAsOperation(Vector3[] deltas, int[] pinnedVertices)
        {
            if (!EnsureAlive() || deltas == null) return false;
            // 重ね着をまとめて編集しているときは、最初のレンダラーのメッシュの差分も受け付ける
            _session.GetVertexRange(0, out _, out int firstCount);
            bool combined = deltas.Length == _session.VertexCount;
            if (!combined && deltas.Length != firstCount) return false;
            FinishStroke();
            BeginOperation("GP02 シェイプキーを読み込む");
            bool loaded = combined ? _session.LoadMeshSpaceDeltas(deltas) : _session.LoadMeshSpaceDeltas(0, deltas);
            if (loaded && pinnedVertices != null && pinnedVertices.Length > 0) ReplacePinsWithVertices(pinnedVertices);
            // 何も変わらなかったとき（差分が 0 など）は、Undo の記録も残さない
            CommitOperation();
            AfterShapeChanged();
            return loaded;
        }

        /// <summary>
        /// 今の編集（粒子の形とピン）を、シェイプキーのデータのエントリとして保存する。保存したエントリを返す。Unity の Undo で戻せる。
        /// 編集は続けられる（メッシュは差し替えない）。ピン留めした粒子の近くの頂点番号もエントリに入れる。
        /// </summary>
        /// <param name="overwrite">true なら同じ名前のエントリを上書きする（無ければ追加）。false なら、同じ名前があれば「名前 (1)」のように別名で追加する</param>
        /// <returns>保存できなければ null（理由は <see cref="LastError"/>）</returns>
        public ShapeKeyEntry SaveToData(GP02ShapeKeyData data, string entryName, bool overwrite)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!EnsureAlive())
            {
                LastError = L10n.Tr("編集中ではありません。", "You are not editing.");
                return null;
            }
            string name = entryName == null ? "" : entryName.Trim();
            if (name.Length == 0)
            {
                LastError = L10n.Tr("シェイプキーの名前が空です。名前を入れてから保存してください。", "The shape key name is empty. Enter a name before saving.");
                return null;
            }
            _session.GetVertexRange(0, out _, out int firstVertexCount);
            if (data.VertexCount != firstVertexCount)
            {
                LastError = L10n.Tr($"データの頂点数（{data.VertexCount}）が、編集中のメッシュの頂点数（{firstVertexCount}）と違うので保存できません。",
                    $"Cannot save: the data has {data.VertexCount} vertices but the mesh being edited has {firstVertexCount}.");
                return null;
            }

            FinishStroke();
            Undo.IncrementCurrentGroup();
            ShapeKeyEntry result = RecordEntry(data, name, overwrite);
            Undo.IncrementCurrentGroup();
            LastError = "";
            return result;
        }

        /// <summary><see cref="Save"/> を押したときに、データにも記録するか。テストで、データを作らずに保存を確かめるときは false にする。</summary>
        public bool RecordToData { get; set; } = true;

        /// <summary><see cref="Save"/> でデータを新しく作るときの保存先。</summary>
        public string DataFolder { get; set; } = ShapeKeyLibrary.DataFolder;

        // 今の編集を、rendererIndex 番目のレンダラーのエントリとしてデータに入れる（Undo のグループは呼び出し側で区切る）
        ShapeKeyEntry RecordEntry(GP02ShapeKeyData data, string name, bool overwrite, int rendererIndex = 0)
        {
            Vector3[] deltas = _session.GetMeshSpaceDeltas(rendererIndex);
            // 法線の差分は、元の形（データの元のメッシュ）に対して計算する
            Mesh rest = data.SourceMesh != null && data.SourceMesh.vertexCount == deltas.Length ? data.SourceMesh : _session.GetSourceMesh(rendererIndex);
            ShapeKeyEntry fresh = ShapeKeyLibrary.CreateEntryFromDeltas(rest, name, deltas);
            fresh.PinnedVertices = CollectPinnedVertices(rendererIndex);

            ShapeKeyEntry result;
            ShapeKeyEntry existing = overwrite ? data.FindEntry(name) : null;
            if (existing != null)
            {
                Undo.RecordObject(data, "GP02 シェイプキーを上書き保存");
                existing.Frames = fresh.Frames;
                existing.PinnedVertices = fresh.PinnedVertices;
                EditorUtility.SetDirty(data);
                result = existing;
            }
            else
            {
                fresh.Name = ShapeKeyListOperations.UniqueName(data, name);
                ShapeKeyLibrary.AddEntry(data, fresh);
                result = fresh;
            }
            ShapeKeyLibrary.SaveData(data);
            return result;
        }

        /// <summary>
        /// レンダラーのシェイプキーのデータを返す。コンポーネントとデータが無ければ作って付ける（Undo できる）。
        /// レンダラーにメッシュが無ければ null。
        /// </summary>
        public GP02ShapeKeyData FindOrCreateData(Renderer renderer)
        {
            return ShapeKeyLibrary.FindOrCreateData(renderer);
        }

        // ピン留めした粒子の近くにある、rendererIndex 番目のレンダラーの頂点の番号（そのメッシュでの番号）。復元（ReplacePinsWithVertices）と同じ距離で見る
        int[] CollectPinnedVertices(int rendererIndex)
        {
            _session.GetVertexRange(rendererIndex, out int start, out int count);
            var pins = new List<Vector3>();
            _session.GetPinnedPositions(pins);
            if (pins.Count == 0) return new int[0];

            float radius = _session.Cloth.Spacing * 0.75f;
            var grid = new PointGrid(radius);
            for (int i = 0; i < pins.Count; i++) grid.Add(i, new System.Numerics.Vector3(pins[i].x, pins[i].y, pins[i].z));

            var result = new List<int>();
            var candidates = new List<int>();
            Vector3[] vertices = _session.CurrentWorldVertices;
            float radiusSq = radius * radius;
            for (int v = start; v < start + count; v++)
            {
                grid.Query(new System.Numerics.Vector3(vertices[v].x, vertices[v].y, vertices[v].z), radius, candidates);
                foreach (int c in candidates)
                {
                    if ((pins[c] - vertices[v]).sqrMagnitude <= radiusSq)
                    {
                        result.Add(v - start);
                        break;
                    }
                }
            }
            return result.ToArray();
        }

        // 今のピンをすべて外し、頂点の（今の形での）位置の近くの粒子をピン留めする
        void ReplacePinsWithVertices(int[] vertexIndices)
        {
            _session.PaintPins(Vector3.zero, 1e5f, false);
            Vector3[] vertices = _session.CurrentWorldVertices;
            var positions = new List<Vector3>(vertexIndices.Length);
            foreach (int v in vertexIndices)
            {
                if ((uint)v < (uint)vertices.Length) positions.Add(vertices[v]);
            }
            _session.PinNear(positions, _session.Cloth.Spacing * 0.75f);
        }
    }
}
