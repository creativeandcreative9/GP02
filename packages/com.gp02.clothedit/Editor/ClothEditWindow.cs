using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 布の編集の操作画面。つまむ・ピン留めをシーンビューで行い、結果をシェイプキーとして保存する。
    /// 操作の流れは <see cref="ClothEditController"/> が持ち、ここはイベントを読んで渡すことと、表示だけを受け持つ。
    /// </summary>
    public sealed class ClothEditWindow : EditorWindow
    {
        static readonly string[] ModeLabels = { "つまむ", "ピン留め", "ピン解除" };

        const int MaxPinDots = 3000;             // 描画するピンの点の上限（多いと重くなる）

        // ウィンドウの内容は再読み込み後も残るが、セッションは作り直しになる
        [SerializeField] Renderer _target;
        [SerializeField] ClothEditMode _mode;
        [SerializeField] float _brushCentimeters = 3f;
        [SerializeField] float _influenceCentimeters = 30f;
        [SerializeField] string _shapeName = "";
        [SerializeField] bool _showAdvanced;

        ClothEditController _controller;
        ClothEditSettings _settings;
        readonly List<Vector3> _pinDots = new List<Vector3>();
        GUIStyle _hintStyle;

        [MenuItem("Tools/GP02/布の編集")]
        static void Open()
        {
            var window = GetWindow<ClothEditWindow>("GP02 布の編集");
            window.minSize = new Vector2(280f, 360f);
        }

        void OnEnable()
        {
            // コンパイル後はセッションが消えているので、編集していない状態から始める
            _settings = ClothEditSettings.Load();
            _controller = new ClothEditController(_settings) { ShowProgress = true };
            _controller.Changed += OnControllerChanged;
            SyncController();
            SceneView.beforeSceneGui += OnBeforeSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        void OnDisable()
        {
            SceneView.beforeSceneGui -= OnBeforeSceneGUI;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            if (_controller != null)
            {
                _controller.Changed -= OnControllerChanged;
                _controller.Dispose();
                _controller = null;
            }
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode) _controller.End();
        }

        void OnControllerChanged()
        {
            SceneView.RepaintAll();
            Repaint();
        }

        // ウィンドウに保存してある値を、操作の部品へ渡す
        void SyncController()
        {
            _controller.Mode = _mode;
            _controller.BrushRadius = _brushCentimeters * 0.01f;
            _controller.InfluenceRadius = _influenceCentimeters * 0.01f;
        }

        void OnUpdate()
        {
            _controller.Tick(EditorApplication.timeSinceStartup);
        }

        void OnGUI()
        {
            bool editing = _controller.IsEditing;

            using (new EditorGUI.DisabledScope(editing))
            {
                _target = (Renderer)EditorGUILayout.ObjectField("対象", _target, typeof(Renderer), true);
                if (GUILayout.Button("選択中のオブジェクトから設定")) PickFromSelection();
            }

            string problem = _target != null ? ClothEditController.GetStartProblem(_target) : null;
            if (problem != null)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Warning);
            }
            else if (_target is MeshRenderer)
            {
                EditorGUILayout.HelpBox("MeshFilter のメッシュにはシェイプキーを表示する仕組みがありません。保存はできますが、見た目は変わりません。", MessageType.Info);
            }
            else if (_target != null && !editing && ClothEditController.IsMeshUnreadable(_target))
            {
                EditorGUILayout.HelpBox("このメッシュは Read/Write が無効です。エディタ上では編集できます。保存したメッシュも Read/Write 無効のままですが、シェイプキーの表示には影響しません。", MessageType.Info);
            }
            if (!editing && !string.IsNullOrEmpty(_controller.LastError))
            {
                EditorGUILayout.HelpBox(_controller.LastError, MessageType.Error);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(editing || _target == null || problem != null))
                {
                    if (GUILayout.Button("編集を開始")) StartEdit();
                }
                using (new EditorGUI.DisabledScope(!editing))
                {
                    if (GUILayout.Button("編集を終了")) _controller.End();
                }
            }

            if (editing) DrawEditingControls();
            DrawAdvancedSettings();
        }

        void PickFromSelection()
        {
            GameObject selected = Selection.activeGameObject;
            Renderer found = ClothEditController.FindRenderer(selected);
            if (found != null)
            {
                _target = found;
                return;
            }
            string message = selected == null
                ? "オブジェクトが選ばれていません。Hierarchy で布のオブジェクトを選んでから押してください。"
                : $"「{selected.name}」には SkinnedMeshRenderer も、MeshFilter 付きの MeshRenderer もありません。布の部分（子オブジェクトのことが多いです）を選んでください。";
            EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        void StartEdit()
        {
            if (!_controller.TryStart(_target, out string error))
            {
                EditorUtility.DisplayDialog("GP02", error, "OK");
                return;
            }
            _shapeName = _controller.SuggestShapeName();
        }

        void DrawEditingControls()
        {
            ClothEditSession session = _controller.Session;
            if (session == null) return;

            EditorGUILayout.Space();
            _mode = (ClothEditMode)GUILayout.Toolbar((int)_mode, ModeLabels);
            _brushCentimeters = EditorGUILayout.Slider("ブラシ半径（cm）", _brushCentimeters, 1f, 20f);
            _influenceCentimeters = EditorGUILayout.Slider("影響範囲（cm）", _influenceCentimeters, 5f, 100f);
            SyncController();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("粒子の数", session.ParticleCount.ToString("N0"));
            EditorGUILayout.LabelField("ピンの数", session.PinnedCount.ToString("N0"));
            EditorGUILayout.LabelField("最大の伸び", $"{_controller.LastStretch * 100f:F1} %");
            EditorGUILayout.LabelField("高速化", ClothEditSession.DescribeAcceleration(session.Solver.Kernel));
            EditorGUILayout.LabelField("状態", _controller.StateLabel);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("1つ戻す"))
                {
                    _controller.UndoLast();
                    GUIUtility.ExitGUI();   // Undo の結果でセッションが作り直されることがあるので、この描画は打ち切る
                }
                if (GUILayout.Button("やり直す"))
                {
                    _controller.RedoLast();
                    GUIUtility.ExitGUI();
                }
            }
            if (GUILayout.Button("すべて元に戻す")) _controller.ResetAll();
            EditorGUILayout.LabelField("Ctrl+Z / Ctrl+Y で戻す・やり直す。ドラッグ中は Esc で取り消し", EditorStyles.miniLabel);

            EditorGUILayout.Space();
            _shapeName = EditorGUILayout.TextField("シェイプキーの名前", _shapeName);
            if (GUILayout.Button("シェイプキーとして保存")) Save();
        }

        void Save()
        {
            if (!_controller.Save(_shapeName, out string message))
            {
                EditorUtility.DisplayDialog("GP02", message, "OK");
                return;
            }
            if (_controller.LastSavedMesh != null) EditorGUIUtility.PingObject(_controller.LastSavedMesh);
            if (_controller.IsEditing) _shapeName = _controller.SuggestShapeName();
            else EditorUtility.DisplayDialog("GP02", message, "OK");   // 保存はできたが、編集を続けられなかった
        }

        void DrawAdvancedSettings()
        {
            EditorGUILayout.Space();
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "詳細設定", true);
            if (!_showAdvanced) return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _settings.BendCompliance = EditorGUILayout.Slider(new GUIContent("曲がりやすさ",
                "大きいほど布が柔らかく折れ曲がります。小さいほど、つまんだときに周りが平らに保たれます。既定 0.1"),
                _settings.BendCompliance, 0f, 1f);
            _settings.StretchCompliance = EditorGUILayout.Slider(new GUIContent("伸びやすさ",
                "大きいほど布が伸びます。0 で伸びません（強く引っ張ると、布が浮き上がって手前で止まります）。既定 0"),
                _settings.StretchCompliance, 0f, 1f);
            _settings.SettleTolerance = EditorGUILayout.Slider(new GUIContent("仕上げの許容する伸び（%）",
                "手を離したあと、これ以下の伸びになったら仕上げを終えます。小さいほど丁寧ですが、時間がかかります。既定 1%"),
                _settings.SettleTolerance * 100f, 0.1f, 20f) * 0.01f;
            _settings.SettleTimeoutSeconds = EditorGUILayout.Slider(new GUIContent("仕上げの時間切れ（秒）",
                "伸びが収まらなくても、この時間で仕上げを打ち切ります。既定 2 秒"),
                _settings.SettleTimeoutSeconds, 0.1f, 10f);
            _settings.SolveMilliseconds = EditorGUILayout.Slider(new GUIContent("1回の更新の計算時間（ms）",
                "1回の画面更新で計算に使う時間です。大きいと布が速く収束しますが、操作が重くなります。既定 12ms"),
                _settings.SolveMilliseconds, 1f, 50f);
            _settings.CoarseIterations = EditorGUILayout.IntSlider(new GUIContent("粗い段の反復回数",
                "粗い粒子で先に形を決める計算の、1段あたりの反復回数です。多いと遠くまで動きが伝わりやすくなりますが、重くなります。既定 10"),
                _settings.CoarseIterations, 0, 40);

            _settings.KeepOutsideRestSurface = EditorGUILayout.Toggle(new GUIContent("元の形より内側に入らない",
                "オンにすると、布が編集前の形より内側（体や内側の布の方）へ入らなくなり、すり抜けを防ぎます。" +
                "つまんでいる範囲は対象外です。平らな部品や、裏面用の面が重なった部分には効きません。内側へ押し込む編集をしたいときはオフにします。既定はオン"),
                _settings.KeepOutsideRestSurface);

            IClothSolverKernel kernel = ClothSolver.DefaultKernel;
            if (kernel != null)
            {
                _settings.UseAcceleration = EditorGUILayout.Toggle(new GUIContent($"高速版（{kernel.Name}）を使う",
                    "オフにすると、高速版を使わず C# で計算します（速さの比較用）。結果はほぼ同じです。既定はオン"),
                    _settings.UseAcceleration);
            }

            bool changed = EditorGUI.EndChangeCheck();
            if (GUILayout.Button(new GUIContent("既定に戻す", "詳細設定の値をすべて既定値に戻します。")))
            {
                _settings.ResetToDefaults();
                changed = true;
            }
            EditorGUI.indentLevel--;

            if (changed)
            {
                _settings.Sanitize();
                _settings.Save();
                _controller.ApplySettings();
            }
        }

        // Graphics.DrawMesh は、シーンのカメラが描く前に呼ばないと次のコマまで表示されない
        void OnBeforeSceneGUI(SceneView sceneView)
        {
            ClothEditSession session = _controller.Session;
            if (session == null || Event.current.type != EventType.Repaint) return;
            session.DrawPreview(sceneView.camera);
        }

        void OnSceneGUI(SceneView sceneView)
        {
            if (!_controller.IsEditing) return;
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
                DrawHint();
                return;
            }

            // Esc：ドラッグ中・仕上げ中の操作を取り消して、押す前の形に戻す
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (_controller.Cancel())
                {
                    GUIUtility.hotControl = 0;
                    e.Use();
                }
                return;
            }

            // 視点操作（Alt）と右・中ボタンは何もしない
            if (e.alt) return;

            switch (e.type)
            {
                case EventType.MouseMove:
                    _controller.Hover(HandleUtility.GUIPointToWorldRay(e.mousePosition));
                    sceneView.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button != 0 || HandleUtility.nearestControl != controlId) break;
                    if (!_controller.PointerDown(HandleUtility.GUIPointToWorldRay(e.mousePosition), sceneView.camera.transform.forward)) break;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (e.button != 0 || GUIUtility.hotControl != controlId) break;
                    _controller.PointerDrag(HandleUtility.GUIPointToWorldRay(e.mousePosition));
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (e.button != 0 || GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    _controller.PointerUp();
                    e.Use();
                    break;
            }
        }

        void DrawOverlay()
        {
            ClothEditSession session = _controller.Session;
            if (session == null) return;

            if (session.PinnedCount > 0)
            {
                session.GetPinnedPositions(_pinDots, MaxPinDots);
                Handles.color = new Color(1f, 0.3f, 0.2f, 1f);
                for (int i = 0; i < _pinDots.Count; i++)
                {
                    Vector3 p = _pinDots[i];
                    Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * 0.015f, EventType.Repaint);
                }
            }

            if (_controller.HasHover)
            {
                ClothEditMode mode = _controller.Mode;
                Handles.color = mode == ClothEditMode.Grab ? new Color(0.2f, 0.8f, 1f, 1f)
                    : mode == ClothEditMode.Pin ? new Color(1f, 0.3f, 0.2f, 1f) : new Color(0.9f, 0.9f, 0.2f, 1f);
                Handles.DrawWireDisc(_controller.HoverPoint, _controller.HoverNormal, _controller.BrushRadius);
            }
        }

        // シーンビューの左上に出す案内
        void DrawHint()
        {
            if (_hintStyle == null)
            {
                _hintStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(8, 8, 4, 4),
                };
            }

            string text;
            if (_controller.IsDragging) text = "GP02 つまんでいます: 離すと仕上げます / Esc で取り消し";
            else if (_controller.IsSettling) text = "GP02 仕上げ中: Esc で取り消し";
            else if (_controller.Mode == ClothEditMode.Grab) text = "GP02 編集中: 左ドラッグでつまむ / Esc で取り消し / Alt+ドラッグで視点移動";
            else text = $"GP02 編集中（{ModeLabels[(int)_controller.Mode]}）: 左ドラッグで塗る / Esc で取り消し / Alt+ドラッグで視点移動";

            var content = new GUIContent(text);
            Vector2 size = _hintStyle.CalcSize(content);
            Handles.BeginGUI();
            GUI.Label(new Rect(10f, 10f, size.x, size.y + 4f), content, _hintStyle);
            Handles.EndGUI();
        }
    }
}
