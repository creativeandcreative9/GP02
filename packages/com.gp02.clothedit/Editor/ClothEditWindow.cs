using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 布の編集の操作画面。つまむ・ピン留めをシーンビューで行い、結果をシェイプキーとして保存する。
    /// 編集の中身は <see cref="ClothEditSession"/> が持ち、ここは入力と表示だけを受け持つ。
    /// </summary>
    public sealed class ClothEditWindow : EditorWindow
    {
        enum Mode { Grab, Pin, Unpin }

        static readonly string[] ModeLabels = { "つまむ", "ピン留め", "ピン解除" };

        const float SettleTolerance = 0.01f;     // 仕上げの終わりとみなす最大の伸び（1%）
        const double SettleTimeout = 2.0;        // 仕上げの時間切れ（秒）
        const double SolveMilliseconds = 12.0;   // 1回の更新で計算に使う時間
        const int MaxPinDots = 3000;             // 描画するピンの点の上限（多いと重くなる）

        // ウィンドウの内容は再読み込み後も残るが、セッションは作り直しになる
        [SerializeField] Renderer _target;
        [SerializeField] Mode _mode;
        [SerializeField] float _brushCentimeters = 3f;
        [SerializeField] float _influenceCentimeters = 30f;
        [SerializeField] string _shapeName = "";

        ClothEditSession _session;
        ClothEditState _state;          // Unity の Undo の記録先（セッションごとに作る）
        bool _opOpen;                   // 変更の記録を取り始めて、まだ書き込んでいない
        int _opGroup;
        bool _ignoreUndoCallback;
        bool _dragging;
        bool _settling;
        double _settleStart;
        float _lastStretch;
        Plane _dragPlane;

        bool _hasHover;
        Vector3 _hoverPoint;
        Vector3 _hoverNormal;
        int _hoverTriangle = -1;   // カーソルの下の三角形（重ね着で、どの布をつまむか・ピン留めするかを決める）
        readonly List<Vector3> _pinDots = new List<Vector3>();

        [MenuItem("Tools/GP02/布の編集")]
        static void Open()
        {
            var window = GetWindow<ClothEditWindow>("GP02 布の編集");
            window.minSize = new Vector2(280f, 360f);
        }

        void OnEnable()
        {
            // コンパイル後はセッションが消えているので、編集していない状態から始める
            _session = null;
            _dragging = false;
            _settling = false;
            SceneView.beforeSceneGui += OnBeforeSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        void OnDisable()
        {
            SceneView.beforeSceneGui -= OnBeforeSceneGUI;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            EndEdit();
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode) EndEdit();
        }

        static Renderer FindRenderer(GameObject target)
        {
            if (target == null) return null;
            var skinned = target.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null) return skinned;
            var meshRenderer = target.GetComponent<MeshRenderer>();
            return meshRenderer != null && meshRenderer.GetComponent<MeshFilter>() != null ? meshRenderer : null;
        }

        static bool IsEditable(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh != null;
            if (renderer is MeshRenderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                return filter != null && filter.sharedMesh != null;
            }
            return false;
        }

        /// <param name="carriedPins">引き継ぐピンのワールド位置（保存後や Undo による作り直しのとき）</param>
        /// <param name="carriedPinIslands">引き継ぐピンごとの部品（島）の番号。重ね着で元の層だけを留めるため</param>
        /// <param name="reuseCloth">粒子を置き直さずに使う布（保存後の <see cref="ParticleCloth.Rebase"/> の結果）。メッシュに合わなければ使わない</param>
        /// <param name="carriedPinParticles">reuseCloth を使うときに引き継ぐピンの粒子の番号（番号が同じなので位置で探さない）</param>
        void StartEdit(IReadOnlyList<Vector3> carriedPins = null, IReadOnlyList<int> carriedPinIslands = null,
            ParticleCloth reuseCloth = null, IReadOnlyList<int> carriedPinParticles = null)
        {
            EndEdit();
            if (reuseCloth != null && !ClothEditSession.IsClothCompatible(_target, reuseCloth)) reuseCloth = null;
            try
            {
                EditorUtility.DisplayProgressBar("GP02", "メッシュを粒子にしています…", 0.5f);
                _session = new ClothEditSession(_target, reuseCloth);
            }
            catch (Exception ex)
            {
                _session = null;
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("GP02", "編集を始められませんでした。\n" + ex.Message, "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            _session.Solver.InfluenceRadius = _influenceCentimeters * 0.01f;
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
            // 作り直した直後の形が、Undo の戻り先の基準になる
            _state = ClothEditState.Create();
            _session.WriteState(_state);
            _shapeName = _session.SuggestShapeName();
            _lastStretch = 0f;
            _hasHover = false;
            Debug.Log($"[GP02] {_target.name}: 頂点 {_session.VertexCount} / 粒子 {_session.ParticleCount} / 準備 {_session.PrepareMilliseconds:F0}ms");
            SceneView.RepaintAll();
            Repaint();
        }

        void EndEdit()
        {
            _dragging = false;
            _settling = false;
            _hasHover = false;
            _opOpen = false;
            if (_state != null)
            {
                Undo.ClearUndo(_state);
                DestroyImmediate(_state);
                _state = null;
            }
            if (_session == null) return;
            _session.Dispose();
            _session = null;
            SceneView.RepaintAll();
            Repaint();
        }

        // 仕上げを待たずに、つまみを外す（操作ボタンを押したときなど）
        void FinishStroke()
        {
            _dragging = false;
            _settling = false;
            if (_session != null && _session.IsGrabbing) _session.Release();
            CommitOperation();
        }

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
                _ignoreUndoCallback = true;
                try { Undo.RevertAllDownToGroup(_opGroup); }
                finally { _ignoreUndoCallback = false; }
                return;
            }
            _session.WriteState(_state);
            Undo.IncrementCurrentGroup();
        }

        // Ctrl+Z / Ctrl+Y、メニューの Edit > Undo、ボタンのどれでもここに来る
        void OnUndoRedoPerformed()
        {
            if (_session == null || _ignoreUndoCallback) return;

            // 仕上げ中・ドラッグ中なら止める。書き込まない記録は、Undo がすでに戻している
            _dragging = false;
            _settling = false;
            _opOpen = false;
            if (_session.IsGrabbing) _session.Release();

            if (!_session.IsTargetUnchanged())
            {
                // 保存そのもの（メッシュの差し替えやシェイプキーの重み）が戻された。今のレンダラーで作り直す
                RebuildSession();
                return;
            }

            if (_session.ReadState(_state)) AfterShapeChanged();
        }

        void RebuildSession()
        {
            if (_target == null || !IsEditable(_target))
            {
                EndEdit();
                return;
            }
            var pins = new List<Vector3>();
            var pinIslands = new List<int>();
            _session.GetPinnedPositions(pins, pinIslands);
            StartEdit(pins, pinIslands);
        }

        void OnGUI()
        {
            bool editing = _session != null;

            using (new EditorGUI.DisabledScope(editing))
            {
                _target = (Renderer)EditorGUILayout.ObjectField("対象", _target, typeof(Renderer), true);
                if (GUILayout.Button("選択中のオブジェクトから設定"))
                {
                    Renderer found = FindRenderer(Selection.activeGameObject);
                    if (found != null) _target = found;
                    else EditorUtility.DisplayDialog("GP02", "SkinnedMeshRenderer か、MeshFilter と MeshRenderer を持つオブジェクトを選んでください。", "OK");
                }
            }

            if (_target != null && !IsEditable(_target))
            {
                EditorGUILayout.HelpBox("メッシュを持つ SkinnedMeshRenderer か、MeshFilter 付きの MeshRenderer を指定してください。", MessageType.Warning);
            }
            else if (_target is MeshRenderer)
            {
                EditorGUILayout.HelpBox("MeshFilter のメッシュにはシェイプキーを表示する仕組みがありません。保存はできますが、見た目は変わりません。", MessageType.Info);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(editing || _target == null || !IsEditable(_target)))
                {
                    if (GUILayout.Button("編集を開始")) StartEdit();
                }
                using (new EditorGUI.DisabledScope(!editing))
                {
                    if (GUILayout.Button("編集を終了")) EndEdit();
                }
            }

            if (_session == null) return;

            EditorGUILayout.Space();
            _mode = (Mode)GUILayout.Toolbar((int)_mode, ModeLabels);
            _brushCentimeters = EditorGUILayout.Slider("ブラシ半径（cm）", _brushCentimeters, 1f, 20f);
            EditorGUI.BeginChangeCheck();
            _influenceCentimeters = EditorGUILayout.Slider("影響範囲（cm）", _influenceCentimeters, 5f, 100f);
            if (EditorGUI.EndChangeCheck()) _session.Solver.InfluenceRadius = _influenceCentimeters * 0.01f;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("粒子の数", _session.ParticleCount.ToString("N0"));
            EditorGUILayout.LabelField("ピンの数", _session.PinnedCount.ToString("N0"));
            EditorGUILayout.LabelField("最大の伸び", $"{_lastStretch * 100f:F1} %");
            EditorGUILayout.LabelField("高速化", ClothEditSession.DescribeAcceleration(_session.Solver.Kernel));
            EditorGUILayout.LabelField("状態", _dragging ? "つまんでいる" : _settling ? "仕上げ中" : "待機中");

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("1つ戻す"))
                {
                    FinishStroke();
                    Undo.PerformUndo();
                    GUIUtility.ExitGUI();   // Undo の結果でセッションが作り直されることがあるので、この描画は打ち切る
                }
                if (GUILayout.Button("やり直す"))
                {
                    FinishStroke();
                    Undo.PerformRedo();
                    GUIUtility.ExitGUI();
                }
            }
            if (GUILayout.Button("すべて元に戻す"))
            {
                FinishStroke();
                BeginOperation("GP02 すべて元に戻す");
                _session.ResetAll();
                CommitOperation();
                AfterShapeChanged();
            }
            EditorGUILayout.LabelField("Ctrl+Z / Ctrl+Y で戻す・やり直す", EditorStyles.miniLabel);

            EditorGUILayout.Space();
            _shapeName = EditorGUILayout.TextField("シェイプキーの名前", _shapeName);
            if (GUILayout.Button("シェイプキーとして保存")) Save();
        }

        void AfterShapeChanged()
        {
            _lastStretch = _session.Solver.MeasureMaxStretch();
            SceneView.RepaintAll();
            Repaint();
        }

        void Save()
        {
            FinishStroke();
            string name = _shapeName == null ? "" : _shapeName.Trim();
            if (name.Length == 0)
            {
                EditorUtility.DisplayDialog("GP02", "シェイプキーの名前を入れてください。", "OK");
                return;
            }
            if (_session.SourceMesh.GetBlendShapeIndex(name) >= 0)
            {
                EditorUtility.DisplayDialog("GP02", $"同じ名前のシェイプキー「{name}」が既にあります。別の名前にしてください。", "OK");
                return;
            }

            Mesh saved;
            try
            {
                saved = _session.ApplyToScene(name);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("GP02", "保存できませんでした。\n" + ex.Message, "OK");
                return;
            }

            Debug.Log($"[GP02] シェイプキー「{name}」を {AssetDatabase.GetAssetPath(saved)} に保存しました。");
            EditorGUIUtility.PingObject(saved);
            // 保存の Undo（メッシュの差し替えなど）が、次の編集と同じグループにならないようにする
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
                StartEdit(pins, pinIslands);
                return;
            }
            double rebaseMs = timer.Elapsed.TotalMilliseconds;
            StartEdit(null, null, rebased, pinParticles);
            Debug.Log($"[GP02] 保存後の作り直し {timer.Elapsed.TotalMilliseconds:F0}ms（うち静止状態の更新 {rebaseMs:F0}ms）");
        }

        void OnUpdate()
        {
            if (_session == null) return;
            if (!_session.IsTargetAlive)
            {
                EndEdit();
                return;
            }
            if (!_dragging && !_settling) return;

            _session.Solver.SolveFor(SolveMilliseconds);
            _session.UpdateCurrentShape();
            _lastStretch = _session.Solver.MeasureMaxStretch();

            // つまみを外すのは仕上げが終わってから。先に外すと、つまんでいた範囲が戻ってしまう
            if (_settling && (_lastStretch <= SettleTolerance || EditorApplication.timeSinceStartup - _settleStart > SettleTimeout))
            {
                _session.Release();
                _settling = false;
                CommitOperation();
            }

            SceneView.RepaintAll();
            Repaint();
        }

        // Graphics.DrawMesh は、シーンのカメラが描く前に呼ばないと次のコマまで表示されない
        void OnBeforeSceneGUI(SceneView sceneView)
        {
            if (_session == null || Event.current.type != EventType.Repaint) return;
            _session.DrawPreview(sceneView.camera);
        }

        void OnSceneGUI(SceneView sceneView)
        {
            if (_session == null) return;
            Event e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            sceneView.wantsMouseMove = true;

            if (e.type == EventType.Layout)
            {
                // クリックでオブジェクトの選択が変わらないようにする
                HandleUtility.AddDefaultControl(controlId);
                return;
            }

            if (e.type == EventType.Repaint)
            {
                DrawOverlay();
                return;
            }

            // 視点操作（Alt）と右・中ボタンは何もしない
            if (e.alt) return;

            switch (e.type)
            {
                case EventType.MouseMove:
                    UpdateHover(e);
                    sceneView.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button != 0 || HandleUtility.nearestControl != controlId) break;
                    if (!UpdateHover(e)) break;
                    GUIUtility.hotControl = controlId;
                    OnPress(sceneView);
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (e.button != 0 || GUIUtility.hotControl != controlId) break;
                    // つまんで動かしている間は、カーソルの下の点を使わない（形が変わるたびに境界箱を更新する手間を省く）
                    if (!(_mode == Mode.Grab && _dragging)) UpdateHover(e);
                    OnDrag(e);
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (e.button != 0 || GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    OnRelease();
                    e.Use();
                    break;
            }
        }

        bool UpdateHover(Event e)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            _hasHover = _session.Raycast(ray, out _hoverPoint, out _hoverNormal, out _hoverTriangle);
            return _hasHover;
        }

        void OnPress(SceneView sceneView)
        {
            float radius = _brushCentimeters * 0.01f;
            switch (_mode)
            {
                case Mode.Grab:
                    // 前のストロークの仕上げ中なら、そこで確定する
                    FinishStroke();
                    if (_session.BeginGrab(_hoverPoint, radius, _hoverTriangle))
                    {
                        BeginOperation("GP02 つまむ");
                        // ドラッグ平面は、ヒット点を通りカメラの向きに垂直な面
                        _dragPlane = new Plane(-sceneView.camera.transform.forward, _hoverPoint);
                        _dragging = true;
                    }
                    break;
                case Mode.Pin:
                case Mode.Unpin:
                    // 押してから離すまでのドラッグを、1回の操作にまとめる
                    FinishStroke();
                    BeginOperation(_mode == Mode.Pin ? "GP02 ピン留め" : "GP02 ピン解除");
                    PaintPins(radius);
                    break;
            }
        }

        void OnDrag(Event e)
        {
            if (_mode == Mode.Grab)
            {
                if (!_dragging) return;
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                if (_dragPlane.Raycast(ray, out float distance)) _session.DragTo(ray.GetPoint(distance));
            }
            else if (_hasHover && _opOpen)
            {
                PaintPins(_brushCentimeters * 0.01f);
            }
        }

        void OnRelease()
        {
            if (_mode != Mode.Grab)
            {
                CommitOperation();
                return;
            }
            if (!_dragging) return;
            _dragging = false;
            _settling = true;
            _settleStart = EditorApplication.timeSinceStartup;
        }

        void PaintPins(float radius)
        {
            if (!_hasHover) return;
            if (_session.PaintPins(_hoverPoint, radius, _mode == Mode.Pin, _hoverTriangle) > 0) Repaint();
            SceneView.RepaintAll();
        }

        void DrawOverlay()
        {
            if (_session.PinnedCount > 0)
            {
                _session.GetPinnedPositions(_pinDots, MaxPinDots);
                Handles.color = new Color(1f, 0.3f, 0.2f, 1f);
                for (int i = 0; i < _pinDots.Count; i++)
                {
                    Vector3 p = _pinDots[i];
                    Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * 0.015f, EventType.Repaint);
                }
            }

            if (_hasHover)
            {
                Handles.color = _mode == Mode.Grab ? new Color(0.2f, 0.8f, 1f, 1f)
                    : _mode == Mode.Pin ? new Color(1f, 0.3f, 0.2f, 1f) : new Color(0.9f, 0.9f, 0.2f, 1f);
                Handles.DrawWireDisc(_hoverPoint, _hoverNormal, _brushCentimeters * 0.01f);
            }
        }
    }
}
