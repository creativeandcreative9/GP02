using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

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
        Renderer _target;
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
        public Renderer Target => _target;
        public ClothEditSession Session => _session;
        public bool IsEditing => _session != null;
        public bool IsDragging => _dragging;
        public bool IsSettling => _settling;
        public bool IsBusy => _dragging || _settling;

        /// <summary>直前の編集開始・作り直しが失敗したときの理由。成功すると空になる。</summary>
        public string LastError { get; private set; } = "";

        /// <summary>最後に保存したメッシュ。</summary>
        public Mesh LastSavedMesh { get; private set; }

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
            if (_session != null) _settings.ApplyTo(_session.Solver, _session.RestAllowance);
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
            _target = target;
            // 見せ方は、編集を始めるたびに編集中の形（100%）から始める
            _settings.ShowOriginal = false;
            _settings.CompareBlend = 1f;
            _holdOriginal = false;
            return StartSession(null, null, null, null, out error);
        }

        /// <param name="carriedPins">引き継ぐピンのワールド位置（Undo による作り直しのとき）</param>
        /// <param name="carriedPinIslands">引き継ぐピンごとの部品（島）の番号。重ね着で元の層だけを留めるため</param>
        /// <param name="reuseCloth">粒子を置き直さずに使う布（保存後の <see cref="ParticleCloth.Rebase"/> の結果）。メッシュに合わなければ使わない</param>
        /// <param name="carriedPinParticles">reuseCloth を使うときに引き継ぐピンの粒子の番号（番号が同じなので位置で探さない）</param>
        bool StartSession(IReadOnlyList<Vector3> carriedPins, IReadOnlyList<int> carriedPinIslands,
            ParticleCloth reuseCloth, IReadOnlyList<int> carriedPinParticles, out string error)
        {
            End();
            error = GetStartProblem(_target);
            if (error != null)
            {
                Fail(error);
                return false;
            }

            if (reuseCloth != null && !ClothEditSession.IsClothCompatible(_target, reuseCloth)) reuseCloth = null;
            try
            {
                if (ShowProgress) EditorUtility.DisplayProgressBar("GP02", L10n.Tr("メッシュを粒子にしています…", "Converting the mesh to particles..."), 0.5f);
                _session = new ClothEditSession(_target, reuseCloth);
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

            _settings.ApplyTo(_session.Solver, _session.RestAllowance);
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
            Debug.Log($"[GP02] {_target.name}: 頂点 {_session.VertexCount} / 粒子 {_session.ParticleCount} / 準備 {_session.PrepareMilliseconds:F0}ms / 初回の計算 {warmUp:F0}ms");
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

            if (_strokeMode == ClothEditMode.Pin || _strokeMode == ClothEditMode.Unpin)
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
            if (!(_pointerDown || _dragging || _settling || _opOpen)) return false;

            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
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

        void RelaxHere()
        {
            if (!HasHover) return;
            MirrorPlane? mirror = TryGetMirrorPlane(out MirrorPlane plane) ? plane : (MirrorPlane?)null;
            _session.RelaxAt(HoverPoint, _hoverTriangle, _brushRadius, _settings.RelaxStrength, _settings.GetFalloffCurve(), _settings.DistanceMode, mirror);
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
            if (_target == null || GetStartProblem(_target) != null)
            {
                End();
                return;
            }
            var pins = new List<Vector3>();
            var pinIslands = new List<int>();
            _session.GetPinnedPositions(pins, pinIslands);
            StartSession(pins, pinIslands, null, null, out _);
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
            if (_session.SourceMesh.GetBlendShapeIndex(name) >= 0)
            {
                message = L10n.Tr($"同じ名前のシェイプキー「{name}」が既にあります。別の名前にして保存してください。",
                    $"A shape key named \"{name}\" already exists. Save it under a different name.");
                return false;
            }

            // 保存の Undo（メッシュの差し替えなど）が、直前の編集と同じグループにならないようにする
            Undo.IncrementCurrentGroup();
            Mesh saved;
            try
            {
                saved = _session.ApplyToScene(name, SaveFolder);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = L10n.Tr($"保存できませんでした。保存先「{SaveFolder}」に書き込めるか確認してください（フォルダが読み取り専用になっていないか、同名のファイルが開かれていないか）。\n{ex.Message}",
                    $"Could not save. Check that the save folder \"{SaveFolder}\" is writable (not read-only, and no file of the same name is open).\n{ex.Message}");
                return false;
            }

            LastSavedMesh = saved;
            message = L10n.Tr($"シェイプキー「{name}」を {AssetDatabase.GetAssetPath(saved)} に保存しました。",
                $"Saved the shape key \"{name}\" to {AssetDatabase.GetAssetPath(saved)}.");
            Debug.Log("[GP02] " + message);
            // 保存の Undo が、次の編集と同じグループにならないようにする
            Undo.IncrementCurrentGroup();

            // シェイプキー込みの形（編集中の形そのもの）が新しい基準になる。
            // 粒子は置き直さず、今の位置を静止状態にした布で作り直す。ピンは粒子の番号が同じなので番号で引き継ぐ
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ParticleCloth rebased;
            var pinParticles = new List<int>();
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
                if (!StartSession(pins, pinIslands, null, null, out string retryError)) message += L10n.Tr("\n編集を続けられませんでした。「編集を開始」からやり直してください。\n", "\nCould not continue editing. Press \"Start Editing\" to start over.\n") + retryError;
                return true;
            }
            double rebaseMs = timer.Elapsed.TotalMilliseconds;
            if (!StartSession(null, null, rebased, pinParticles, out string error)) message += L10n.Tr("\n編集を続けられませんでした。「編集を開始」からやり直してください。\n", "\nCould not continue editing. Press \"Start Editing\" to start over.\n") + error;
            Debug.Log($"[GP02] 保存後の作り直し {timer.Elapsed.TotalMilliseconds:F0}ms（うち静止状態の更新 {rebaseMs:F0}ms）");
            return true;
        }

        void Notify()
        {
            Changed?.Invoke();
        }
    }
}
