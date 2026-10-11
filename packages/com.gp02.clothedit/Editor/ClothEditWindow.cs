using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 布の編集の操作画面。道具（つまむ・回す・拡大縮小・ピン留め・元に戻すブラシ）をシーンビューで使い、結果をシェイプキーとして保存する。
    /// 操作の流れは <see cref="ClothEditController"/> が持ち、ここはイベントを読んで渡すことと、表示だけを受け持つ。
    /// ウィンドウの内容はこのファイル、シーンビューの入力と重ね描きは ClothEditWindow.SceneView.cs。
    /// </summary>
    public sealed partial class ClothEditWindow : EditorWindow
    {
        // スライダーの範囲（センチメートル）。ホイールやキーで変えるときも、この範囲に収める
        const float BrushMinCm = 1f;
        const float BrushMaxCm = 20f;
        const float InfluenceMinCm = 5f;
        const float InfluenceMaxCm = 100f;

        // ツールバーに並べる順（ClothEditMode の数値とは別）
        static readonly ClothEditMode[] ToolOrder =
        {
            ClothEditMode.Grab, ClothEditMode.Rotate, ClothEditMode.Scale,
            ClothEditMode.Pin, ClothEditMode.Unpin, ClothEditMode.Relax,
        };

        // ウィンドウの内容は再読み込み後も残るが、セッションは作り直しになる
        [SerializeField] Renderer _target;
        [SerializeField] float _brushCentimeters = 3f;
        [SerializeField] float _influenceCentimeters = 30f;
        [SerializeField] string _shapeName = "";
        [SerializeField] Vector2 _scroll;
        [SerializeField] bool _showShortcuts;
        [SerializeField] bool _foldTarget = true;
        [SerializeField] bool _foldTool = true;
        [SerializeField] bool _foldBrush = true;
        [SerializeField] bool _foldSymmetry;
        [SerializeField] bool _foldDisplay = true;
        [SerializeField] bool _foldSave = true;
        [SerializeField] bool _foldAdvanced;

        ClothEditController _controller;
        ClothEditSettings _settings;
        bool _settingsDirty;               // OnGUI の終わりに、設定を保存してソルバーへ反映する
        readonly List<TargetWarning> _warnings = new List<TargetWarning>();
        Renderer _warningsTarget;
        bool _warningsDirty = true;

        [MenuItem("Tools/GP02/布の編集")]
        static void Open()
        {
            var window = GetWindow<ClothEditWindow>(L10n.Tr("GP02 布の編集", "GP02 Cloth Edit"));
            window.minSize = new Vector2(300f, 360f);
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
            _warningsDirty = true;
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

        void OnProjectChange()
        {
            _warningsDirty = true;
        }

        void OnFocus()
        {
            _warningsDirty = true;
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
            _controller.BrushRadius = _brushCentimeters * 0.01f;
            _controller.InfluenceRadius = _influenceCentimeters * 0.01f;
        }

        void OnUpdate()
        {
            _controller.Tick(EditorApplication.timeSinceStartup);
        }

        void OnGUI()
        {
            if (_controller == null) return;
            string title = L10n.Tr("GP02 布の編集", "GP02 Cloth Edit");
            if (titleContent.text != title) titleContent = new GUIContent(title);

            DrawTopBar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawTargetSection();
            DrawToolSection();
            DrawBrushSection();
            DrawSymmetrySection();
            DrawDisplaySection();
            if (_controller.IsEditing) DrawSaveSection();
            DrawAdvancedSection();
            EditorGUILayout.EndScrollView();

            if (_settingsDirty)
            {
                _settingsDirty = false;
                _settings.Sanitize();
                _settings.Save();
                _controller.ApplySettings();
            }
        }

        // ---- 区切りの見出し ----

        static bool Section(bool open, string label)
        {
            return EditorGUILayout.Foldout(open, label, true, EditorStyles.foldoutHeader);
        }

        // ---- 上部（言語、ショートカット一覧） ----

        void DrawTopBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(L10n.Tr("言語", "Language"), EditorStyles.miniLabel, GUILayout.Width(50f));
                int language = GUILayout.Toolbar((int)L10n.Language, new[] { "日本語", "English" }, EditorStyles.toolbarButton, GUILayout.Width(150f));
                if (language != (int)L10n.Language)
                {
                    L10n.Language = (UiLanguage)language;
                    _warningsDirty = true;
                    SceneView.RepaintAll();
                }
                GUILayout.FlexibleSpace();
                bool show = GUILayout.Toggle(_showShortcuts,
                    new GUIContent("?", L10n.Tr("ショートカット一覧を出す・隠す（F1）", "Show / hide the shortcut list (F1)")),
                    EditorStyles.toolbarButton, GUILayout.Width(26f));
                if (show != _showShortcuts)
                {
                    _showShortcuts = show;
                    SceneView.RepaintAll();
                }
            }
        }

        // ---- 対象 ----

        void DrawTargetSection()
        {
            _foldTarget = Section(_foldTarget, L10n.Tr("対象", "Target"));
            if (!_foldTarget) return;

            bool editing = _controller.IsEditing;
            using (new EditorGUI.DisabledScope(editing))
            {
                EditorGUI.BeginChangeCheck();
                _target = (Renderer)EditorGUILayout.ObjectField(L10n.Tr("対象", "Target"), _target, typeof(Renderer), true);
                if (EditorGUI.EndChangeCheck()) _warningsDirty = true;
                if (GUILayout.Button(L10n.Tr("選択中のオブジェクトから設定", "Set from Selection")))
                {
                    PickFromSelection();
                    _warningsDirty = true;
                }
            }

            if (Event.current.type == EventType.Layout && (_warningsDirty || _warningsTarget != _target))
            {
                ClothEditWarnings.Collect(_target, _warnings);
                _warningsTarget = _target;
                _warningsDirty = false;
            }
            bool blocked = false;
            if (!editing)
            {
                for (int i = 0; i < _warnings.Count; i++)
                {
                    TargetWarning warning = _warnings[i];
                    if (warning.Kind == TargetWarningKind.CannotStart) blocked = true;
                    EditorGUILayout.HelpBox(warning.Message, warning.Severity);
                    if (warning.CanAutoFix && GUILayout.Button(L10n.Tr("Read/Write を有効にする", "Enable Read/Write")))
                    {
                        FixReadWrite();
                        GUIUtility.ExitGUI();
                    }
                }
                if (!string.IsNullOrEmpty(_controller.LastError)) EditorGUILayout.HelpBox(_controller.LastError, MessageType.Error);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(editing || _target == null || blocked))
                {
                    if (GUILayout.Button(L10n.Tr("編集を開始", "Start Editing"))) StartEdit();
                }
                using (new EditorGUI.DisabledScope(!editing))
                {
                    if (GUILayout.Button(L10n.Tr("編集を終了", "End Editing"))) _controller.End();
                }
            }

            ClothEditSession session = _controller.Session;
            if (editing && session != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(L10n.Tr("粒子の数", "Particles"), session.ParticleCount.ToString("N0"));
                EditorGUILayout.LabelField(L10n.Tr("ピンの数", "Pins"), session.PinnedCount.ToString("N0"));
                EditorGUILayout.LabelField(L10n.Tr("最大の伸び", "Max Stretch"), $"{_controller.LastStretch * 100f:F1} %");
                EditorGUILayout.LabelField(L10n.Tr("高速化", "Acceleration"), ClothEditSession.DescribeAcceleration(session.Solver.Kernel));
                EditorGUILayout.LabelField(L10n.Tr("状態", "State"), _controller.StateLabel);
            }
            EditorGUILayout.Space();
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
                ? L10n.Tr("オブジェクトが選ばれていません。Hierarchy で布のオブジェクトを選んでから押してください。",
                    "No object is selected. Select the cloth object in the Hierarchy first.")
                : L10n.Tr($"「{selected.name}」には SkinnedMeshRenderer も、MeshFilter 付きの MeshRenderer もありません。布の部分（子オブジェクトのことが多いです）を選んでください。",
                    $"\"{selected.name}\" has neither a SkinnedMeshRenderer nor a MeshRenderer with a MeshFilter. Select the cloth part (often a child object).");
            EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        void FixReadWrite()
        {
            ClothEditWarnings.TryEnableReadWrite(_target, out string message);
            _warningsDirty = true;
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

            // 初めて編集を始めたときは、ショートカット一覧を自動で出す
            if (!EditorPrefs.GetBool(ClothEditHelp.ShownOncePrefKey, false))
            {
                _showShortcuts = true;
                EditorPrefs.SetBool(ClothEditHelp.ShownOncePrefKey, true);
            }
        }

        // ---- 道具 ----

        void DrawToolSection()
        {
            _foldTool = Section(_foldTool, L10n.Tr("道具", "Tools"));
            if (!_foldTool) return;

            var labels = new GUIContent[ToolOrder.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                ClothEditMode mode = ToolOrder[i];
                labels[i] = new GUIContent(ClothEditHelp.ToolName(mode) + " (" + ClothEditHelp.ToolKey(mode) + ")");
            }
            int current = System.Array.IndexOf(ToolOrder, _settings.Tool);
            int selected = GUILayout.SelectionGrid(Mathf.Max(0, current), labels, 3);
            if (selected != current)
            {
                _settings.Tool = ToolOrder[selected];
                _settingsDirty = true;
                SceneView.RepaintAll();
            }

            EditorGUI.BeginChangeCheck();
            switch (_settings.Tool)
            {
                case ClothEditMode.Grab:
                    _settings.MoveDirection = (MoveDirection)EditorGUILayout.Popup(
                        new GUIContent(L10n.Tr("動かす向き", "Move Direction"),
                            L10n.Tr("自由: 視点の平面の上を動かす。面の法線: つまんだ所の面に垂直な向きだけ。X/Y/Z: その軸の向きだけ。", "Free: move on the view plane. Surface normal: only perpendicular to the surface. X/Y/Z: only along that axis.")),
                        (int)_settings.MoveDirection,
                        new[]
                        {
                            L10n.Tr("自由（視点の平面）", "Free (view plane)"), L10n.Tr("面の法線に沿って", "Along surface normal"),
                            L10n.Tr("X 軸に固定", "Lock to X axis"), L10n.Tr("Y 軸に固定", "Lock to Y axis"), L10n.Tr("Z 軸に固定", "Lock to Z axis"),
                        });
                    if (_settings.MoveDirection >= MoveDirection.AxisX)
                    {
                        _settings.DirectionSpace = (AxisSpace)EditorGUILayout.Popup(L10n.Tr("軸の基準", "Axis Space"), (int)_settings.DirectionSpace,
                            new[] { L10n.Tr("ワールド", "World"), L10n.Tr("アバターのローカル", "Avatar local") });
                    }
                    break;
                case ClothEditMode.Rotate:
                    _settings.RotateAxis = (RotateAxisMode)EditorGUILayout.Popup(L10n.Tr("回転軸", "Rotation Axis"), (int)_settings.RotateAxis,
                        new[] { L10n.Tr("視線の向き", "View direction"), L10n.Tr("面の法線", "Surface normal") });
                    break;
                case ClothEditMode.Relax:
                    _settings.RelaxStrength = EditorGUILayout.Slider(new GUIContent(L10n.Tr("強さ", "Strength"),
                        L10n.Tr("1回塗るごとに、元の形へ近づける割合です。1 で元の形に戻ります。", "How far each stroke brings the cloth back to its original shape. 1 restores it fully.")),
                        _settings.RelaxStrength, 0f, 1f);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) _settingsDirty = true;

            if (!_controller.IsEditing) return;
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L10n.Tr("1つ戻す", "Undo")))
                {
                    _controller.UndoLast();
                    GUIUtility.ExitGUI();   // Undo の結果でセッションが作り直されることがあるので、この描画は打ち切る
                }
                if (GUILayout.Button(L10n.Tr("やり直す", "Redo")))
                {
                    _controller.RedoLast();
                    GUIUtility.ExitGUI();
                }
            }
            if (GUILayout.Button(L10n.Tr("すべて元に戻す", "Reset All"))) _controller.ResetAll();
            EditorGUILayout.LabelField(L10n.Tr("Ctrl+Z / Ctrl+Y で戻す・やり直す。ドラッグ中は Esc で取り消し", "Ctrl+Z / Ctrl+Y to undo / redo. Esc cancels while dragging"), EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        // ---- ブラシ ----

        void DrawBrushSection()
        {
            _foldBrush = Section(_foldBrush, L10n.Tr("ブラシ", "Brush"));
            if (!_foldBrush) return;

            _brushCentimeters = EditorGUILayout.Slider(new GUIContent(L10n.Tr("ブラシ半径（cm）", "Brush Radius (cm)"),
                L10n.Tr("つまむ範囲の半径。Ctrl+ホイール、または [ ] キーでも変えられます。", "Radius of the grabbed area. Also Ctrl+Wheel or the [ ] keys.")),
                _brushCentimeters, BrushMinCm, BrushMaxCm);
            _influenceCentimeters = EditorGUILayout.Slider(new GUIContent(L10n.Tr("影響範囲（cm）", "Influence (cm)"),
                L10n.Tr("つまんだ動きが周りへ広がる範囲。Ctrl+Shift+ホイール、または Shift+[ ] でも変えられます。", "How far the grabbed motion spreads to the surroundings. Also Ctrl+Shift+Wheel or Shift+[ ].")),
                _influenceCentimeters, InfluenceMinCm, InfluenceMaxCm);
            SyncController();

            EditorGUI.BeginChangeCheck();

            // プリセットを選ぶと、そのカーブを下のカーブに写す（描き足して使える）。カーブを直接いじると「自由に描く」になる
            string[] presetNames =
            {
                L10n.Tr("一定", "Constant"), L10n.Tr("なめらか", "Smooth"), L10n.Tr("直線", "Linear"),
                L10n.Tr("鋭い", "Sharp"), L10n.Tr("球", "Sphere"), L10n.Tr("自由に描く", "Custom"),
            };
            var preset = (FalloffPreset)EditorGUILayout.Popup(new GUIContent(L10n.Tr("減衰カーブ", "Falloff"),
                L10n.Tr("ブラシの範囲の中で、中心から端へ動きがどう弱まるか。横軸が中心(0)→端(1)、縦軸が動きの強さです。", "How the motion weakens from the center to the edge of the brush. Horizontal: center (0) to edge (1); vertical: strength.")),
                (int)_settings.Falloff, presetNames);
            if (preset != _settings.Falloff)
            {
                if (preset != FalloffPreset.Custom) _settings.CustomFalloff = ClothEditSettings.CurveFromPreset(preset);
                _settings.Falloff = preset;
            }

            EditorGUI.BeginChangeCheck();
            AnimationCurve edited = EditorGUILayout.CurveField(GUIContent.none, _settings.CustomFalloff ?? ClothEditSettings.DefaultCustomFalloff(),
                new Color(1f, 0.85f, 0.2f), new Rect(0f, 0f, 1f, 1f), GUILayout.Height(48f));
            if (EditorGUI.EndChangeCheck())
            {
                _settings.CustomFalloff = edited;
                _settings.Falloff = FalloffPreset.Custom;
            }

            _settings.DistanceMode = (BrushDistanceMode)EditorGUILayout.Popup(new GUIContent(L10n.Tr("距離の測り方", "Distance"),
                L10n.Tr("メッシュに沿う: つながった布だけを範囲にする（重ね着の別の層は動かさない）。直線: 近くにあるものを層に関係なく範囲にする。",
                    "Along the surface: only connected cloth is in range (other layers are left alone). Straight: anything nearby is in range, whatever layer it is on.")),
                (int)_settings.DistanceMode, new[] { L10n.Tr("メッシュに沿う", "Along the surface"), L10n.Tr("直線", "Straight line") });

            _settings.RelaxStrength = EditorGUILayout.Slider(new GUIContent(L10n.Tr("元に戻すブラシの強さ", "Restore Brush Strength"),
                L10n.Tr("1回塗るごとに、元の形へ近づける割合です。1 で元の形に戻ります。", "How far each stroke brings the cloth back to its original shape. 1 restores it fully.")),
                _settings.RelaxStrength, 0f, 1f);

            if (EditorGUI.EndChangeCheck()) _settingsDirty = true;
            EditorGUILayout.Space();
        }

        // ---- 対称 ----

        void DrawSymmetrySection()
        {
            _foldSymmetry = Section(_foldSymmetry, L10n.Tr("対称", "Symmetry"));
            if (!_foldSymmetry) return;

            EditorGUI.BeginChangeCheck();
            _settings.SymmetryEnabled = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("対称編集", "Symmetric Editing"),
                L10n.Tr("オンにすると、つまむ・回す・拡大縮小・ピン・元に戻すブラシが、反対側にも鏡に映したように働きます。", "When on, grab / rotate / scale / pin / restore also act on the opposite side, mirrored.")),
                _settings.SymmetryEnabled);
            using (new EditorGUI.DisabledScope(!_settings.SymmetryEnabled))
            {
                _settings.SymmetryAxis = (SymmetryAxis)EditorGUILayout.Popup(new GUIContent(L10n.Tr("軸", "Axis"),
                    L10n.Tr("アバターのローカル座標の軸。この軸に垂直な面が鏡になります（左右対称なら X）。", "An axis in the avatar's local space. The plane perpendicular to it is the mirror (X for left / right).")),
                    (int)_settings.SymmetryAxis, new[] { "X", "Y", "Z" });
                float offsetCm = EditorGUILayout.FloatField(new GUIContent(L10n.Tr("中心のずれ（cm）", "Center Offset (cm)"),
                    L10n.Tr("アバターの中心から、鏡の面までのずれ（アバターのローカル座標）。", "Offset of the mirror plane from the avatar's center (in the avatar's local space).")),
                    _settings.SymmetryOffset * 100f);
                _settings.SymmetryOffset = offsetCm * 0.01f;
            }
            if (EditorGUI.EndChangeCheck()) _settingsDirty = true;

            if (_settings.SymmetryEnabled)
            {
                Transform root = _controller.IsEditing ? _controller.AvatarRoot : (_target != null ? ClothEditSymmetry.FindAvatarRoot(_target.transform) : null);
                if (root != null)
                {
                    EditorGUILayout.LabelField(L10n.Tr("基準", "Reference"), root.name, EditorStyles.miniLabel);
                    if (root.GetComponentInParent<Animator>() == null)
                    {
                        EditorGUILayout.HelpBox(L10n.Tr("Animator のある親が見つからなかったので、一番上の親「" + root.name + "」を基準にしています。",
                            "No parent with an Animator was found, so the topmost parent \"" + root.name + "\" is used as the reference."), MessageType.Info);
                    }
                }
            }
            EditorGUILayout.Space();
        }

        // ---- 表示 ----

        void DrawDisplaySection()
        {
            _foldDisplay = Section(_foldDisplay, L10n.Tr("表示", "Display"));
            if (!_foldDisplay) return;

            EditorGUI.BeginChangeCheck();
            _settings.ShowBrushPreview = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("範囲を色で表示", "Show Brush Range"),
                L10n.Tr("マウスを乗せている間、範囲に入る粒子を重みで色分けした点で表示します。", "While hovering, shows the particles in range as dots colored by weight.")),
                _settings.ShowBrushPreview);
            _settings.ShowStretchHeatmap = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("伸びのヒートマップ", "Stretch Heatmap"),
                L10n.Tr("粒子ごとの伸びを緑→黄→赤で表示します（0% → 5% → 15%）。", "Shows each particle's stretch from green to yellow to red (0% to 5% to 15%).")),
                _settings.ShowStretchHeatmap);
            if (EditorGUI.EndChangeCheck())
            {
                _settingsDirty = true;
                SceneView.RepaintAll();
            }

            if (_controller.IsEditing)
            {
                EditorGUI.BeginChangeCheck();
                bool showOriginal = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("元の形を表示", "Show Original Shape"),
                    L10n.Tr("編集前の形を見せます。C キーを押している間だけ見せることもできます。", "Shows the shape before editing. You can also hold the C key.")),
                    _controller.ShowOriginal);
                float blend = EditorGUILayout.Slider(new GUIContent(L10n.Tr("見せる形（%）", "Blend (%)"),
                    L10n.Tr("0 で元の形、100 で編集中の形。間は元の形と編集中の形の中間です。", "0 shows the original shape, 100 the edited shape. In between is a mix of the two.")),
                    _controller.CompareBlend * 100f, 0f, 100f) * 0.01f;
                if (EditorGUI.EndChangeCheck())
                {
                    _controller.ShowOriginal = showOriginal;
                    _controller.CompareBlend = blend;
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.Space();
        }

        // ---- 保存 ----
        // 保存の区切りは、ボタンをこのメソッドの中の1か所にまとめてある（シェイプキーの一覧と差し替えやすいように）

        void DrawSaveSection()
        {
            _foldSave = Section(_foldSave, L10n.Tr("保存", "Save"));
            if (!_foldSave) return;

            _shapeName = EditorGUILayout.TextField(L10n.Tr("シェイプキーの名前", "Shape Key Name"), _shapeName);
            if (GUILayout.Button(L10n.Tr("シェイプキーとして保存", "Save as Shape Key"))) Save();
            EditorGUILayout.Space();
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

        // ---- 詳細設定 ----

        void DrawAdvancedSection()
        {
            _foldAdvanced = Section(_foldAdvanced, L10n.Tr("詳細設定", "Advanced"));
            if (!_foldAdvanced) return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _settings.BendCompliance = EditorGUILayout.Slider(new GUIContent(L10n.Tr("曲がりやすさ", "Bendiness"),
                L10n.Tr("大きいほど布が柔らかく折れ曲がります。小さいほど、つまんだときに周りが平らに保たれます。既定 0.1",
                    "Larger values let the cloth fold more softly. Smaller values keep the surroundings flat when grabbing. Default 0.1")),
                _settings.BendCompliance, 0f, 1f);
            _settings.StretchCompliance = EditorGUILayout.Slider(new GUIContent(L10n.Tr("伸びやすさ", "Stretchiness"),
                L10n.Tr("大きいほど布が伸びます。0 で伸びません（強く引っ張ると、布が浮き上がって手前で止まります）。既定 0",
                    "Larger values let the cloth stretch. 0 means it does not stretch (pulling hard lifts the cloth and it stops short). Default 0")),
                _settings.StretchCompliance, 0f, 1f);
            _settings.SettleTolerance = EditorGUILayout.Slider(new GUIContent(L10n.Tr("仕上げの許容する伸び（%）", "Settle Tolerance (%)"),
                L10n.Tr("手を離したあと、これ以下の伸びになったら仕上げを終えます。小さいほど丁寧ですが、時間がかかります。既定 1%",
                    "After releasing, settling ends once the stretch falls below this. Smaller is more careful but slower. Default 1%")),
                _settings.SettleTolerance * 100f, 0.1f, 20f) * 0.01f;
            _settings.SettleTimeoutSeconds = EditorGUILayout.Slider(new GUIContent(L10n.Tr("仕上げの時間切れ（秒）", "Settle Timeout (s)"),
                L10n.Tr("伸びが収まらなくても、この時間で仕上げを打ち切ります。既定 2 秒",
                    "Settling stops after this time even if the stretch has not settled. Default 2 s")),
                _settings.SettleTimeoutSeconds, 0.1f, 10f);
            _settings.SolveMilliseconds = EditorGUILayout.Slider(new GUIContent(L10n.Tr("1回の更新の計算時間（ms）", "Solve Time per Update (ms)"),
                L10n.Tr("1回の画面更新で計算に使う時間です。大きいと布が速く収束しますが、操作が重くなります。既定 12ms",
                    "Time spent computing per editor update. Larger converges faster but makes editing heavier. Default 12 ms")),
                _settings.SolveMilliseconds, 1f, 50f);
            _settings.CoarseIterations = EditorGUILayout.IntSlider(new GUIContent(L10n.Tr("粗い段の反復回数", "Coarse Iterations"),
                L10n.Tr("粗い粒子で先に形を決める計算の、1段あたりの反復回数です。多いと遠くまで動きが伝わりやすくなりますが、重くなります。既定 10",
                    "Iterations per level of the coarse-particle pass that shapes the cloth first. More carries motion farther but is heavier. Default 10")),
                _settings.CoarseIterations, 0, 40);

            _settings.BrushCompliance = EditorGUILayout.Slider(new GUIContent(L10n.Tr("つまむ柔らかさ", "Grab Softness"),
                L10n.Tr("大きいほど、布の長さで届かない所へ引いたときに早く手前で止まります（伸びが残りにくい）。" +
                        "小さいほど手にしっかり付いてきます。0 でつまんだ所を固定します（届かない所まで連れて行くので伸びが残ります）。既定 0.02",
                    "Larger values stop the cloth short sooner when pulled beyond its length (less leftover stretch). " +
                    "Smaller values follow the hand more tightly. 0 pins the grabbed area to the target (it is dragged past its reach, leaving stretch). Default 0.02")),
                _settings.BrushCompliance, 0f, ClothEditSettings.MaxBrushCompliance);

            _settings.KeepOutsideRestSurface = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("元の形より内側に入らない", "Stay Outside Original Surface"),
                L10n.Tr("オンにすると、布が編集前の形より内側（体や内側の布の方）へ入らなくなり、すり抜けを防ぎます。" +
                        "つまんでいる範囲は対象外です。平らな部品や、裏面用の面が重なった部分には効きません。内側へ押し込む編集をしたいときはオフにします。既定はオン",
                    "When on, the cloth cannot move inside its original surface (toward the body or inner cloth), preventing clipping. " +
                    "The grabbed area is exempt. It does not work on flat parts or overlapping back faces. Turn it off to push cloth inward. Default on")),
                _settings.KeepOutsideRestSurface);
            _settings.UseRestSurfaceAllowance = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("体や内側の布までの隙間を使う", "Use Gap to Body / Inner Cloth"),
                L10n.Tr("オンにすると、編集を始めるときに測った「体や内側の布までの隙間」の分だけ、布が元の形より内側へ動けます。" +
                        "ふんわりした服は、隙間を使えると布を融通できて伸びが残りにくくなります（体や内側の布には入り込みません）。" +
                        "オフにすると隙間を 0 にして、元の形より内側へは入りません。「元の形より内側に入らない」がオフのときは関係ありません。既定はオン",
                    "When on, the cloth may move inside its original surface by the gap to the body or inner cloth measured when editing started. " +
                    "Loose clothing can then give more slack and keeps less stretch (it still does not enter the body or inner cloth). " +
                    "When off, the gap is 0 and the cloth never goes inside its original surface. Irrelevant if \"Stay Outside Original Surface\" is off. Default on")),
                _settings.UseRestSurfaceAllowance);

            IClothSolverKernel kernel = ClothSolver.DefaultKernel;
            if (kernel != null)
            {
                _settings.UseAcceleration = EditorGUILayout.Toggle(new GUIContent(L10n.Tr($"高速版（{kernel.Name}）を使う", $"Use Accelerated Version ({kernel.Name})"),
                    L10n.Tr("オフにすると、高速版を使わず C# で計算します（速さの比較用）。結果はほぼ同じです。既定はオン",
                        "When off, computes in plain C# without the accelerated version (for speed comparison). Results are nearly identical. Default on")),
                    _settings.UseAcceleration);
            }

            bool changed = EditorGUI.EndChangeCheck();
            if (GUILayout.Button(new GUIContent(L10n.Tr("既定に戻す", "Reset to Defaults"),
                L10n.Tr("道具・ブラシ・対称・表示を含め、すべての設定値を既定値に戻します。", "Resets every setting, including tool, brush, symmetry and display, to its default."))))
            {
                _settings.ResetToDefaults();
                _brushCentimeters = 3f;
                _influenceCentimeters = 30f;
                changed = true;
            }
            EditorGUI.indentLevel--;

            if (changed) _settingsDirty = true;
        }
    }
}
