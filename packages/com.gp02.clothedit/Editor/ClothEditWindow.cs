using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 布の編集の操作画面。道具（つまむ・回す・拡大縮小・ピン留め・元に戻すブラシ・選んで動かす）をシーンビューで使い、結果をシェイプキーとして保存する。
    /// 操作の流れは <see cref="ClothEditController"/> が持ち、ここはイベントを読んで渡すことと、表示だけを受け持つ。
    /// ウィンドウの内容はこのファイル、シーンビューの入力と重ね描きは ClothEditWindow.SceneView.cs、
    /// 「選んで動かす」の区切りとハンドルは ClothEditWindow.Selection.cs。
    /// </summary>
    public sealed partial class ClothEditWindow : EditorWindow
    {
        // スライダーの範囲（センチメートル）。ホイールやキーで変えるときも、この範囲に収める
        const float BrushMinCm = 1f;
        const float BrushMaxCm = 20f;
        const float InfluenceMinCm = 5f;
        const float InfluenceMaxCm = 100f;

        // ツールバーに並べる順（ClothEditMode の数値とは別）。「選んで動かす」は、道具の下に1つだけ広いボタンで置く
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
        [SerializeField] bool _foldGuide;

        ClothEditController _controller;
        ClothEditSettings _settings;
        bool _settingsDirty;               // OnGUI の終わりに、設定を保存してソルバーへ反映する
        readonly List<TargetWarning> _warnings = new List<TargetWarning>();
        Renderer _warningsTarget;
        bool _warningsDirty = true;

        // 手順の案内（編集を始めるたびに最初からやり直す）
        bool _guideWasEditing;
        bool _guideEdited;
        bool _guideSaved;
        int _guideEditedVersion = -1;
        Mesh _guideSavedBaseline;
        readonly GuideStepState[] _guideStates = new GuideStepState[ClothEditGuide.StepCount];

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
            // シェイプキーの一覧の「布で編集する」から、このウィンドウの編集を使う
            ShapeKeyClothEdit.ControllerProvider = () => _controller;
            ClothEditShortcutBindings.Handler = OnShortcut;
            SyncController();
            SceneView.beforeSceneGui += OnBeforeSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _warningsDirty = true;
            _overlayOwner = this;
        }

        void OnDisable()
        {
            if (_overlayOwner == this) _overlayOwner = null;
            SceneView.beforeSceneGui -= OnBeforeSceneGUI;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            if (ShapeKeyClothEdit.ControllerProvider != null && ShapeKeyClothEdit.ControllerProvider() == _controller) ShapeKeyClothEdit.ControllerProvider = null;
            if (ClothEditShortcutBindings.Handler == (System.Action<ClothEditShortcutAction, bool>)OnShortcut) ClothEditShortcutBindings.Handler = null;
            ClothEditShortcutBindings.SetEditing(false);
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
            RememberToolBeforeSelect();
            _controller?.Tick(EditorApplication.timeSinceStartup);
            SyncShortcutContext();
            if (_radiusDrag.Active && (_controller == null || !_controller.IsEditing)) _radiusDrag.Cancel();
        }

        void OnGUI()
        {
            if (_controller == null) return;
            string title = L10n.Tr("GP02 布の編集", "GP02 Cloth Edit");
            if (titleContent.text != title) titleContent = new GUIContent(title);

            if (Event.current.type == EventType.Layout) RefreshTargetWarnings();
            DrawTopBar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawGuide();
            if (!_controller.IsEditing && ClothSolver.DefaultKernel == null) BurstInstaller.DrawNotice();
            DrawTargetSection();
            DrawDiagnosticsSection();
            DrawToolSection();
            DrawSelectionSection();
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
                var languageLabels = new[]
                {
                    new GUIContent("日本語", L10n.Tr("画面の言葉を日本語にします", "Show the interface in Japanese")),
                    new GUIContent("English", L10n.Tr("画面の言葉を英語にします", "Show the interface in English")),
                };
                int language = GUILayout.Toolbar((int)L10n.Language, languageLabels, EditorStyles.toolbarButton, GUILayout.Width(150f));
                if (language != (int)L10n.Language)
                {
                    L10n.Language = (UiLanguage)language;
                    _warningsDirty = true;
                    SceneView.RepaintAll();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(L10n.Tr("説明書", "Manual"), L10n.Tr("使い方の説明書を開きます（Documentation~ フォルダの文書）", "Opens the manual (a document in the Documentation~ folder)")),
                    EditorStyles.toolbarButton, GUILayout.Width(56f)))
                {
                    OpenManual();
                }
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

        void OpenManual()
        {
            if (!ClothEditDocs.TryOpen(out string message)) EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        // ---- 手順の案内 ----

        // 編集を始め直したら、「引いた」「保存した」の記録を最初からにする
        void UpdateGuideProgress(bool editing)
        {
            ClothEditSession session = _controller.Session;
            if (editing && !_guideWasEditing)
            {
                _guideEdited = false;
                _guideSaved = false;
                _guideEditedVersion = -1;
                _guideSavedBaseline = _controller.LastSavedMesh;
            }
            _guideWasEditing = editing;
            if (!editing || session == null) return;

            if (!_guideSaved && _controller.LastSavedMesh != null && _controller.LastSavedMesh != _guideSavedBaseline) _guideSaved = true;
            // 形が変わったときだけ調べ直す（毎回の描画で全粒子を見ない）
            if (!_guideEdited && _guideEditedVersion != session.ShapeVersion)
            {
                _guideEditedVersion = session.ShapeVersion;
                _guideEdited = session.IsShapeEdited();
            }
        }

        void DrawGuide()
        {
            bool editing = _controller.IsEditing;
            UpdateGuideProgress(editing);
            ClothEditSession session = _controller.Session;
            ClothEditGuide.Evaluate(_target != null, editing, session != null && session.PinnedCount > 0, editing && _guideEdited, editing && _guideSaved, _guideStates);

            if (editing)
            {
                _foldGuide = Section(_foldGuide, L10n.Tr("手順（今どこまで済んだか）", "Steps (where you are)"));
                if (!_foldGuide) return;
            }
            else
            {
                EditorGUILayout.LabelField(L10n.Tr("はじめての方へ: 5つの手順", "New here? Five steps"), EditorStyles.boldLabel);
            }

            for (int i = 0; i < ClothEditGuide.StepCount; i++)
            {
                GuideStepState state = _guideStates[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(GuideMarker(state), GUILayout.Width(20f), GUILayout.Height(18f));
                    GUIStyle style = state == GuideStepState.Current ? EditorStyles.boldLabel : EditorStyles.label;
                    Color previous = GUI.contentColor;
                    if (state == GuideStepState.Done || state == GuideStepState.Skipped) GUI.contentColor = new Color(previous.r, previous.g, previous.b, 0.55f);
                    GUILayout.Label(ClothEditGuide.Title(i), style);
                    GUI.contentColor = previous;
                }
                if (state == GuideStepState.Current) EditorGUILayout.HelpBox(ClothEditGuide.Detail(i), MessageType.Info);
            }
            EditorGUILayout.Space();
        }

        static GUIContent GuideMarker(GuideStepState state)
        {
            // 組み込みのアイコンが無い環境でも、文字で分かるようにする
            string iconName = state == GuideStepState.Done ? "TestPassed" : state == GuideStepState.Current ? "tab_next" : "TestNormal";
            GUIContent icon = EditorGUIUtility.IconContent(iconName);
            if (icon != null && icon.image != null) return new GUIContent(icon.image);
            return new GUIContent(state == GuideStepState.Done ? "x" : state == GuideStepState.Current ? ">" : "-");
        }

        // ---- 対象 ----

        // 重ね着をまとめて編集するときの、2つ目以降の対象（1つ目は _target）
        [SerializeField] List<Renderer> _extraTargets = new List<Renderer>();
        readonly List<Renderer> _warningOwners = new List<Renderer>();   // _warnings と同じ順の、警告の対象
        readonly List<TargetWarning> _warningWork = new List<TargetWarning>();
        int _warningsTargetCount;

        // 今の対象（1つ目と、2つ目以降）。null は除く
        List<Renderer> CollectTargets()
        {
            var targets = new List<Renderer>();
            if (_target != null) targets.Add(_target);
            foreach (Renderer extra in _extraTargets)
            {
                if (extra != null && !targets.Contains(extra)) targets.Add(extra);
            }
            return targets;
        }

        void DrawTargetSection()
        {
            _foldTarget = Section(_foldTarget, L10n.Tr("対象", "Target"));
            if (!_foldTarget) return;

            bool editing = _controller.IsEditing;
            using (new EditorGUI.DisabledScope(editing))
            {
                EditorGUI.BeginChangeCheck();
                using (new EditorGUILayout.HorizontalScope())
                {
                    _target = (Renderer)EditorGUILayout.ObjectField(L10n.Tr("対象", "Target"), _target, typeof(Renderer), true);
                    if (_extraTargets.Count > 0 && GUILayout.Button(new GUIContent(L10n.Tr("外す", "Remove"), L10n.Tr("この対象を一覧から外す", "Remove this target from the list")), GUILayout.Width(48f)))
                    {
                        _target = _extraTargets[0];
                        _extraTargets.RemoveAt(0);
                    }
                }
                for (int i = 0; i < _extraTargets.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _extraTargets[i] = (Renderer)EditorGUILayout.ObjectField(L10n.Tr($"対象 {i + 2}", $"Target {i + 2}"), _extraTargets[i], typeof(Renderer), true);
                        if (GUILayout.Button(new GUIContent(L10n.Tr("外す", "Remove"), L10n.Tr("この対象を一覧から外す", "Remove this target from the list")), GUILayout.Width(48f)))
                        {
                            _extraTargets.RemoveAt(i);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (EditorGUI.EndChangeCheck()) _warningsDirty = true;

                if (GUILayout.Button(new GUIContent(L10n.Tr("選択中のオブジェクトから設定", "Set from Selection"),
                    L10n.Tr("ヒエラルキーで選んでいる衣装（SkinnedMeshRenderer か MeshRenderer）を対象にします", "Use the clothing selected in the Hierarchy (SkinnedMeshRenderer or MeshRenderer) as the target"))))
                {
                    PickFromSelection();
                    _warningsDirty = true;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("選択中のオブジェクトを追加", "Add Selection"),
                        L10n.Tr("選んだオブジェクトの布を対象に加えます。重ね着をまとめて1つの布として編集し、保存すると全部に同じ名前のシェイプキーが入ります",
                            "Adds the selected objects' cloth to the targets. Layers are edited together as one cloth, and saving adds a shape key with the same name to all of them"))))
                    {
                        AddSelectionToTargets();
                        _warningsDirty = true;
                    }
                    using (new EditorGUI.DisabledScope(_target == null))
                    {
                        if (GUILayout.Button(new GUIContent(L10n.Tr("同じアバターの重ね着をまとめて追加", "Add Layers of the Same Avatar"),
                            L10n.Tr($"同じアバターの SkinnedMeshRenderer のうち、今の対象と重なっているもの（頂点の半分以上が {ClothEditController.DefaultLayerDistance * 100f:F0}cm 以内）を加えます",
                                $"Adds the SkinnedMeshRenderers of the same avatar that overlap the current targets (at least half of the vertices within {ClothEditController.DefaultLayerDistance * 100f:F0} cm)"))))
                        {
                            AddLayeredTargets();
                            _warningsDirty = true;
                        }
                    }
                }
            }

            // 問題の一覧と「直す」ボタンは「診断」の区切りに出す。ここでは始められないものだけを見る
            bool blocked = false;
            foreach (TargetWarning warning in _warnings)
            {
                if (warning.Kind == TargetWarningKind.CannotStart) blocked = true;
            }
            if (!editing && blocked) EditorGUILayout.HelpBox(L10n.Tr("この対象では編集を始められません。理由は下の「診断」を見てください。", "Editing cannot start with this target. See Diagnostics below for the reason."), MessageType.Error);
            if (!editing && !string.IsNullOrEmpty(_controller.LastError)) EditorGUILayout.HelpBox(_controller.LastError, MessageType.Error);

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
                if (session.RendererCount > 1)
                {
                    EditorGUILayout.LabelField(L10n.Tr("まとめて編集", "Editing Together"), L10n.Tr($"{session.RendererCount} 個（層どうしの接触 {session.LayerContacts.Count:N0} 組）", $"{session.RendererCount} renderers ({session.LayerContacts.Count:N0} layer contacts)"));
                }
                EditorGUILayout.LabelField(L10n.Tr("粒子の数", "Particles"), session.ParticleCount.ToString("N0"));
                EditorGUILayout.LabelField(L10n.Tr("ピンの数", "Pins"), session.PinnedCount.ToString("N0"));
                EditorGUILayout.LabelField(L10n.Tr("最大の伸び", "Max Stretch"), $"{_controller.LastStretch * 100f:F1} %");
                EditorGUILayout.LabelField(L10n.Tr("高速化", "Acceleration"), ClothEditSession.DescribeAcceleration(session.Solver.Kernel));
                EditorGUILayout.LabelField(L10n.Tr("状態", "State"), _controller.StateLabel);
                if (ClothSolver.DefaultKernel == null) BurstInstaller.DrawNotice();
            }
            EditorGUILayout.Space();
        }

        // 対象ごとの点検（ClothEditWarnings.Collect）を、対象や内容が変わったときだけ作り直す（OnGUI の Layout で呼ぶ）
        void RefreshTargetWarnings()
        {
            List<Renderer> targets = CollectTargets();
            if (!(_warningsDirty || _warningsTarget != _target || _warningsTargetCount != targets.Count)) return;
            _warnings.Clear();
            _warningOwners.Clear();
            foreach (Renderer renderer in targets)
            {
                ClothEditWarnings.Collect(renderer, _warningWork);
                foreach (TargetWarning warning in _warningWork)
                {
                    _warnings.Add(warning);
                    _warningOwners.Add(renderer);
                }
            }
            _warningsTarget = _target;
            _warningsTargetCount = targets.Count;
            _warningsDirty = false;
            _warningsVersion++;
        }

        void PickFromSelection()
        {
            GameObject selected = Selection.activeGameObject;
            Renderer found = ClothEditController.FindRenderer(selected);
            if (found != null)
            {
                _target = found;
                _extraTargets.Clear();
                return;
            }
            ShowNoRendererMessage(selected);
        }

        static void ShowNoRendererMessage(GameObject selected)
        {
            string message = selected == null
                ? L10n.Tr("オブジェクトが選ばれていません。Hierarchy で布のオブジェクトを選んでから押してください。",
                    "No object is selected. Select the cloth object in the Hierarchy first.")
                : L10n.Tr($"「{selected.name}」には SkinnedMeshRenderer も、MeshFilter 付きの MeshRenderer もありません。布の部分（子オブジェクトのことが多いです）を選んでください。",
                    $"\"{selected.name}\" has neither a SkinnedMeshRenderer nor a MeshRenderer with a MeshFilter. Select the cloth part (often a child object).");
            EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        // 選んだオブジェクト（複数可）の布を、対象の一覧の最後に加える
        void AddSelectionToTargets()
        {
            GameObject[] selected = Selection.gameObjects;
            int added = 0;
            foreach (GameObject go in selected)
            {
                Renderer found = ClothEditController.FindRenderer(go);
                if (found == null) continue;
                if (AddTarget(found)) added++;
            }
            if (added == 0 && (selected.Length == 0 || ClothEditController.FindRenderer(selected[0]) == null)) ShowNoRendererMessage(selected.Length > 0 ? selected[0] : null);
        }

        bool AddTarget(Renderer renderer)
        {
            if (renderer == null || CollectTargets().Contains(renderer)) return false;
            if (_target == null) _target = renderer;
            else _extraTargets.Add(renderer);
            return true;
        }

        void AddLayeredTargets()
        {
            List<Renderer> found = ClothEditController.FindLayeredRenderers(CollectTargets());
            int added = 0;
            foreach (Renderer renderer in found)
            {
                if (AddTarget(renderer)) added++;
            }
            if (added == 0)
            {
                EditorUtility.DisplayDialog("GP02", L10n.Tr("今の対象と重なっている、同じアバターの布は見つかりませんでした。", "No cloth of the same avatar overlapping the current targets was found."), "OK");
            }
        }

        void FixReadWrite(Renderer renderer)
        {
            ClothEditWarnings.TryEnableReadWrite(renderer, out string message);
            _warningsDirty = true;
            EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        void StartEdit()
        {
            if (!_controller.TryStartLayered(CollectTargets(), out string error))
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
                labels[i] = new GUIContent(ClothEditHelp.ToolName(mode) + " (" + ClothEditHelp.ToolKey(mode) + ")", ClothEditHelp.ToolTip(mode));
            }
            // 「選んで動かす」を使っているときは、上の6つはどれも選ばれていない（-1）
            int current = System.Array.IndexOf(ToolOrder, _settings.Tool);
            int selected = GUILayout.SelectionGrid(current, labels, 3);
            if (selected != current && selected >= 0)
            {
                RememberToolBeforeSelect();
                _settings.Tool = ToolOrder[selected];
                _settingsDirty = true;
                SceneView.RepaintAll();
            }
            bool selectOn = GUILayout.Toggle(_settings.Tool == ClothEditMode.Select,
                new GUIContent(ClothEditHelp.ToolName(ClothEditMode.Select) + " (" + ClothEditHelp.ToolKey(ClothEditMode.Select) + ")", ClothEditHelp.ToolTip(ClothEditMode.Select)), "Button");
            if (selectOn != (_settings.Tool == ClothEditMode.Select))
            {
                RememberToolBeforeSelect();
                // もう一度押すと、直前の道具に戻る（Q と同じ）
                _settings.Tool = selectOn ? ClothEditMode.Select : ClothEditShortcuts.LeaveSelectTool(_toolBeforeSelect);
                _settingsDirty = true;
                SceneView.RepaintAll();
            }

            EditorGUI.BeginChangeCheck();
            switch (_settings.Tool)
            {
                case ClothEditMode.Grab:
                    _settings.MoveDirection = (MoveDirection)EditorGUILayout.Popup(
                        new GUIContent(L10n.Tr("動かす向き", "Move Direction"),
                            L10n.Tr("自由: 視点の平面の上を動かす。面の法線: つまんだ所の面に垂直な向きだけ。X/Y/Z: その軸の向きだけ。視線: 画面の奥・手前の向きだけ（上下のドラッグで）。",
                                "Free: move on the view plane. Surface normal: only perpendicular to the surface. X/Y/Z: only along that axis. View: only toward or away from the screen (drag up / down).")),
                        (int)_settings.MoveDirection,
                        new[]
                        {
                            L10n.Tr("自由（視点の平面）", "Free (view plane)"), L10n.Tr("面の法線に沿って", "Along surface normal"),
                            L10n.Tr("X 軸に固定", "Lock to X axis"), L10n.Tr("Y 軸に固定", "Lock to Y axis"), L10n.Tr("Z 軸に固定", "Lock to Z axis"),
                            L10n.Tr("視線に沿って（奥へ・手前へ）", "Along the view (push / pull)"),
                        });
                    if (_settings.MoveDirection == MoveDirection.View)
                    {
                        EditorGUILayout.LabelField(L10n.Tr("上へドラッグで奥へ、下へドラッグで手前へ動きます", "Drag up to push away, drag down to pull toward you"), EditorStyles.wordWrappedMiniLabel);
                    }
                    if (_settings.MoveDirection == MoveDirection.AxisX || _settings.MoveDirection == MoveDirection.AxisY || _settings.MoveDirection == MoveDirection.AxisZ)
                    {
                        _settings.DirectionSpace = (AxisSpace)EditorGUILayout.Popup(new GUIContent(L10n.Tr("軸の基準", "Axis Space"),
                            L10n.Tr("X/Y/Z 軸を、シーンのワールド座標で見るか、アバターの向きに合わせたローカル座標で見るか。", "Whether the X/Y/Z axes are in world space or in the avatar's local space.")),
                            (int)_settings.DirectionSpace,
                            new[] { L10n.Tr("ワールド", "World"), L10n.Tr("アバターのローカル", "Avatar local") });
                    }
                    break;
                case ClothEditMode.Rotate:
                    _settings.RotateAxis = (RotateAxisMode)EditorGUILayout.Popup(new GUIContent(L10n.Tr("回転軸", "Rotation Axis"),
                        L10n.Tr("視線の向き: 画面に向かって回る。面の法線: つまんだ所の面に垂直な軸のまわりに回る。", "View direction: rotates facing the screen. Surface normal: rotates around the axis perpendicular to the grabbed surface.")),
                        (int)_settings.RotateAxis,
                        new[] { L10n.Tr("視線の向き", "View direction"), L10n.Tr("面の法線", "Surface normal") });
                    break;
                case ClothEditMode.Pin:
                case ClothEditMode.Unpin:
                    _settings.PinRectFrontOnly = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("矩形は表側だけ", "Rectangle: front only"),
                        L10n.Tr("Shift+ドラッグの矩形で、カメラ側を向いた粒子だけを対象にします。オフにすると、裏側（裏の面）の粒子も入ります。", "In a Shift+Drag rectangle, only particles facing the camera are used. Turn off to include the back side as well.")),
                        _settings.PinRectFrontOnly);
                    EditorGUILayout.LabelField(L10n.Tr("Shift+ドラッグ: 矩形で一度に / Ctrl+クリック: 部品（島）ごと", "Shift+Drag: rectangle / Ctrl+Click: whole part (island)"), EditorStyles.miniLabel);
                    break;
                case ClothEditMode.Relax:
                    _settings.RelaxStrength = EditorGUILayout.Slider(new GUIContent(L10n.Tr("強さ", "Strength"),
                        L10n.Tr("1回塗るごとに、元の形へ近づける割合です。1 で元の形に戻ります。", "How far each stroke brings the cloth back to its original shape. 1 restores it fully.")),
                        _settings.RelaxStrength, 0f, 1f);
                    break;
                case ClothEditMode.Select:
                    EditorGUILayout.LabelField(L10n.Tr("ドラッグ: 塗って選ぶ（Ctrl: 外す。半径はブラシ半径）", "Drag: paint to select (Ctrl: deselect; radius is the brush radius)"), EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.LabelField(L10n.Tr("Shift+ドラッグ: 矩形 / ダブルクリック: 部品ごと / Esc: 解除", "Shift+Drag: rectangle / Double-click: whole part / Esc: clear"), EditorStyles.wordWrappedMiniLabel);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) _settingsDirty = true;

            if (!_controller.IsEditing) return;
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("1つ戻す", "Undo"), L10n.Tr("直前の操作を1つ戻します（Ctrl+Z と同じ）", "Undoes the last operation (same as Ctrl+Z)"))))
                {
                    _controller.UndoLast();
                    GUIUtility.ExitGUI();   // Undo の結果でセッションが作り直されることがあるので、この描画は打ち切る
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("やり直す", "Redo"), L10n.Tr("戻した操作をやり直します（Ctrl+Y と同じ）", "Redoes the undone operation (same as Ctrl+Y)"))))
                {
                    _controller.RedoLast();
                    GUIUtility.ExitGUI();
                }
            }
            if (GUILayout.Button(new GUIContent(L10n.Tr("すべて元に戻す", "Reset All"), L10n.Tr("引いた形をすべて元の形に戻します（ピンは残ります）。Ctrl+Z で取り消せます", "Returns all pulled shapes to the original (pins stay). Ctrl+Z undoes this"))))
            {
                _controller.ResetAll();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("ピンをすべて外す", "Clear All Pins"), L10n.Tr("ピン留めをすべて外します。Ctrl+Z で取り消せます", "Removes every pin. Ctrl+Z undoes this"))))
                {
                    _controller.ClearPins();
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("ピンを反転", "Invert Pins"), L10n.Tr("ピン留めした所を外し、留めていなかった所を留めます（「この辺りだけ動かす」をしたいときに便利）。Ctrl+Z で取り消せます",
                    "Unpins what was pinned and pins what was not (handy to move only one area). Ctrl+Z undoes this"))))
                {
                    _controller.InvertPins();
                }
            }
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
            string[] presetNames = FalloffPresetNames();
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
            DrawCurveLibrary();

            _settings.DistanceMode = (BrushDistanceMode)EditorGUILayout.Popup(new GUIContent(L10n.Tr("距離の測り方", "Distance"),
                L10n.Tr("メッシュに沿う: つながった布だけを範囲にする（重ね着の別の層は動かさない）。直線: 近くにあるものを層に関係なく範囲にする。",
                    "Along the surface: only connected cloth is in range (other layers are left alone). Straight: anything nearby is in range, whatever layer it is on.")),
                (int)_settings.DistanceMode, new[] { L10n.Tr("メッシュに沿う", "Along the surface"), L10n.Tr("直線", "Straight line") });
            using (new EditorGUI.DisabledScope(_settings.DistanceMode != BrushDistanceMode.Surface))
            {
                float connectCm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("つながりの許容距離 ε（cm）", "Connect Distance ε (cm)"),
                    L10n.Tr("「メッシュに沿う」で範囲を測るとき、この距離以内にある別の部品（島）にも範囲を広げます。袖とフリルのように分かれたパーツを一緒に動かせます。" +
                            "布の計算のつながりは変わりません（範囲の決め方だけ）。0 で広げません。重ね着の層どうしも、この距離より近ければつながります",
                        "When measuring along the surface, the range also spreads to other parts (islands) within this distance, so separate pieces such as a sleeve and its frill move together. " +
                        "The cloth simulation's connections are unchanged (only the range). 0 disables it. Layers closer than this distance are connected too")),
                    _settings.ConnectDistance * 100f, 0f, ClothEditSettings.MaxConnectDistance * 100f);
                _settings.ConnectDistance = connectCm * 0.01f;
            }

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
            _settings.SymmetryAxis = (SymmetryAxis)EditorGUILayout.Popup(new GUIContent(L10n.Tr("軸", "Axis"),
                L10n.Tr("アバターのローカル座標の軸。この軸に垂直な面が鏡になります（左右対称なら X）。", "An axis in the avatar's local space. The plane perpendicular to it is the mirror (X for left / right).")),
                (int)_settings.SymmetryAxis, new[] { "X", "Y", "Z" });
            float offsetCm = EditorGUILayout.FloatField(new GUIContent(L10n.Tr("中心のずれ（cm）", "Center Offset (cm)"),
                L10n.Tr("アバターの中心から、鏡の面までのずれ（アバターのローカル座標）。「鏡の面を自動で合わせる」で衣装に合わせられます。", "Offset of the mirror plane from the avatar's center (in the avatar's local space). Auto-Fit Mirror Plane matches it to the outfit.")),
                _settings.SymmetryOffset * 100f);
            _settings.SymmetryOffset = offsetCm * 0.01f;
            float toleranceCm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("鏡像を探す許容距離（cm）", "Mirror Tolerance (cm)"),
                L10n.Tr("鏡に映した位置から、この距離以内の点を反対側の相手にします。左右で形が少し違う衣装では広げます。広げすぎると、別の部分を相手にすることがあります。既定 2cm",
                    "A point within this distance of the mirrored position becomes the partner on the opposite side. Widen it for outfits whose sides differ slightly; too wide may pick an unrelated part. Default 2 cm")),
                _settings.MirrorTolerance * 100f, ClothEditSettings.MinMirrorTolerance * 100f, ClothEditSettings.MaxMirrorTolerance * 100f);
            _settings.MirrorTolerance = toleranceCm * 0.01f;
            bool symmetryChanged = EditorGUI.EndChangeCheck();
            if (symmetryChanged) _settingsDirty = true;

            bool editing = _controller.IsEditing;
            using (new EditorGUI.DisabledScope(!editing || _controller.IsBusy))
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("鏡の面を自動で合わせる", "Auto-Fit Mirror Plane"),
                    L10n.Tr("元の形を鏡に映したとき一番よく重なる位置を探して、「中心のずれ」に入れます。衣装がアバターの中心からずれていても、衣装の左右対称の面で編集できます（編集中に使えます）",
                        "Finds where the original shape best overlaps its mirror image and puts it in Center Offset, so you can edit symmetrically around the outfit's own plane even if it is off the avatar's center (while editing)"))))
                {
                    FitMirrorPlane();
                }
            }

            if (editing && _settings.SymmetryEnabled)
            {
                int unmatched = _controller.UnmatchedMirrorCount;
                float ratio = _controller.UnmatchedMirrorRatio;
                EditorGUILayout.LabelField(L10n.Tr("相手のいない点", "Points Without a Partner"), L10n.Tr($"{unmatched:N0} 個（{ratio * 100f:F1}%）", $"{unmatched:N0} ({ratio * 100f:F1}%)"));
                if (ratio > ClothEditSettings.UnmatchedMirrorWarnRatio)
                {
                    EditorGUILayout.HelpBox(L10n.Tr("相手のいない点が多いです。その点をつまんでも、反対側は動きません。「鏡の面を自動で合わせる」か、許容距離を広げてください。",
                        "Many points have no partner. Grabbing them does not move the opposite side. Use Auto-Fit Mirror Plane or widen the tolerance."), MessageType.Warning);
                }
            }

            EditorGUI.BeginChangeCheck();
            _settings.ShowUnmatchedMirror = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("相手のいない点を表示", "Show Points Without a Partner"),
                L10n.Tr("対称がオンのとき、鏡像の相手が見つからない点を赤紫の点で見せます。そこをつまんでも反対側は動きません", "While symmetry is on, shows points with no mirror partner as magenta dots. Grabbing them does not move the opposite side")),
                _settings.ShowUnmatchedMirror);
            _settings.ShowMirrorDifference = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("左右差を色分け", "Show Left / Right Difference"),
                L10n.Tr("点ごとに、反対側の相手との変形の差（鏡に映した動きとの差）を緑→黄→赤で見せます（0 → 0.5cm → 2cm）", "Shows, per point, how much its deformation differs from its mirrored partner's, from green to yellow to red (0 / 0.5 cm / 2 cm)")),
                _settings.ShowMirrorDifference);
            if (EditorGUI.EndChangeCheck())
            {
                _settingsDirty = true;
                SceneView.RepaintAll();
            }
            if (editing && _settings.ShowMirrorDifference && _controller.TryGetMirrorPlane(out MirrorPlane differencePlane))
            {
                float max = _controller.Session.MaxMirrorDifference(differencePlane, _settings.MirrorTolerance);
                EditorGUILayout.LabelField(L10n.Tr("左右差の最大", "Max Difference"), $"{max * 100f:F2} cm");
            }

            // 今の形を左右対称にする
            EditorGUILayout.Space(2f);
            string axisName = _settings.SymmetryAxis.ToString();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _settings.SymmetrizeSource = (SymmetrySourceSide)EditorGUILayout.Popup(new GUIContent(L10n.Tr("基準にする側", "Keep Side"),
                    L10n.Tr("「今の形を左右対称にする」で、変形を残す側です。反対側は、この側の変形を鏡に映した形になります（X 軸では、+X がアバターの右側です）",
                        "The side whose deformation is kept by Symmetrize Shape. The other side becomes its mirror image (on the X axis, +X is the avatar's right side)")),
                    (int)_settings.SymmetrizeSource, new[] { L10n.Tr($"+{axisName} 側", $"+{axisName} side"), L10n.Tr($"−{axisName} 側", $"-{axisName} side") });
                if (EditorGUI.EndChangeCheck()) _settingsDirty = true;
            }
            using (new EditorGUI.DisabledScope(!editing || _controller.IsBusy))
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("今の形を左右対称にする", "Symmetrize Shape"),
                    L10n.Tr("基準にする側の変形（元の形からの移動）を、鏡に映して反対側へ写し、仕上げで伸びを直します。中央は両側の平均にします。ピンの点と相手のいない点は動きません。Ctrl+Z で戻せます（1回で1段）",
                        "Mirrors the deformation of the kept side onto the other side, then settles the stretch. The middle becomes the average of both sides. Pinned points and points without a partner do not move. Ctrl+Z undoes it (one step)"))))
                {
                    SymmetrizeShape();
                }
            }

            Transform root = editing ? _controller.AvatarRoot : (_target != null ? ClothEditSymmetry.FindAvatarRoot(_target.transform) : null);
            if (root != null)
            {
                EditorGUILayout.LabelField(L10n.Tr("基準", "Reference"), root.name, EditorStyles.miniLabel);
                if (_settings.SymmetryEnabled && root.GetComponentInParent<Animator>() == null)
                {
                    EditorGUILayout.HelpBox(L10n.Tr("Animator のある親が見つからなかったので、一番上の親「" + root.name + "」を基準にしています。",
                        "No parent with an Animator was found, so the topmost parent \"" + root.name + "\" is used as the reference."), MessageType.Info);
                }
            }
            EditorGUILayout.Space();
        }

        void FitMirrorPlane()
        {
            bool ok = _controller.AutoFitSymmetryPlane(out string message);
            _settings.Save();
            if (ok) ShowNotification(new GUIContent(message));
            else EditorUtility.DisplayDialog("GP02", message, "OK");
            SceneView.RepaintAll();
        }

        void SymmetrizeShape()
        {
            int changed = _controller.SymmetrizeShape();
            ShowNotification(new GUIContent(changed > 0
                ? L10n.Tr($"{changed:N0} 個の点を左右対称にしました（仕上げ中）", $"Symmetrized {changed:N0} points (settling)")
                : L10n.Tr("変わる点がありませんでした（すでに左右対称か、相手のいない点だけです）", "Nothing changed (already symmetric, or only points without a partner)")));
            SceneView.RepaintAll();
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
            _settings.ShowMoveHeatmap = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("動いた量のヒートマップ", "Movement Heatmap"),
                L10n.Tr("元の形からどれだけ動いたかを、青→黄→赤で表示します（0 → 2.5cm → 5cm 以上）。どこまで動いたか・動かしたくない所が動いていないかを確かめられます。",
                    "Shows how far each point moved from the original shape, from blue to yellow to red (0 / 2.5 cm / 5 cm or more). Check how far edits reach and that areas that should stay did not move.")),
                _settings.ShowMoveHeatmap);
            _settings.ShowOriginalGhost = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("元の形を重ねて表示", "Overlay Original Shape"),
                L10n.Tr("編集前の形を半透明の水色で重ねます。「元の形を表示」と違い、編集中の形と同時に見えます。", "Overlays the shape before editing in translucent light blue. Unlike Show Original Shape, you see it together with the edited shape.")),
                _settings.ShowOriginalGhost);
            _settings.ShowFalloffRings = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("減り方を同心円で表示", "Show Falloff Rings"),
                L10n.Tr("ブラシの円の中に、動きの強さが 75% / 50% / 25% になる位置を同心円で見せます（減衰カーブの形が分かります）。", "Draws rings inside the brush circle where the motion strength is 75% / 50% / 25% (shows the falloff curve's shape).")),
                _settings.ShowFalloffRings);
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

            GP02ShapeKeys entryComponent = ShapeKeyClothEdit.ActiveComponent;
            if (entryComponent != null && entryComponent.gameObject == _controller.Target.gameObject)
            {
                // 一覧から「布で編集する」で始めたときは、そのシェイプキーへ保存する
                EditorGUILayout.HelpBox(L10n.Tr($"シェイプキー「{ShapeKeyClothEdit.ActiveEntryName}」を布で編集しています。保存はデータに入ります。メッシュへの書き出しは、シェイプキーの一覧から行います。",
                    $"Editing the shape key \"{ShapeKeyClothEdit.ActiveEntryName}\" with cloth. Saving stores it in the data. Export to the mesh from the shape key list."), MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("同じ名前で上書き保存", "Save (overwrite)"), L10n.Tr("編集したシェイプキーを、同じ名前で上書きします。Ctrl+Z で取り消せます", "Overwrites the shape key being edited, under the same name. Ctrl+Z undoes this")))) SaveEntry(true);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("別名で追加", "Save as new"), L10n.Tr("今の形を、新しいシェイプキーとして一覧に加えます", "Adds the current shape to the list as a new shape key")))) SaveEntry(false);
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("シェイプキーの一覧を開く", "Open the shape key list"), L10n.Tr("保存したシェイプキーの再編集・分割・反転・書き出しをする一覧を開きます", "Opens the list for re-editing, splitting, flipping and exporting saved shape keys")))) EditorApplication.ExecuteMenuItem("Tools/GP02/シェイプキーの一覧");
                EditorGUILayout.Space();
                return;
            }

            _shapeName = EditorGUILayout.TextField(new GUIContent(L10n.Tr("シェイプキーの名前", "Shape Key Name"),
                L10n.Tr("保存するシェイプキー（BlendShape）の名前。メッシュに同じ名前があると保存できません", "Name of the shape key (BlendShape) to save. Saving fails if the mesh already has one with this name")), _shapeName);
            if (GUILayout.Button(new GUIContent(L10n.Tr("シェイプキーとして保存", "Save as Shape Key"),
                L10n.Tr("メッシュの複製にシェイプキーとして入れ、編集の内容をデータにも記録します（あとでシェイプキーの一覧から再編集・分割・反転できます）",
                    "Adds it to a copy of the mesh as a shape key and records the edit in the data (you can re-edit, split, or flip it later from the shape key list)")))) Save();
            EditorGUILayout.Space();
        }

        void SaveEntry(bool overwrite)
        {
            ShapeKeyEntry saved = ShapeKeyClothEdit.SaveActive(_controller, overwrite, overwrite ? null : _controller.SuggestShapeName());
            if (saved == null)
            {
                EditorUtility.DisplayDialog("GP02", string.IsNullOrEmpty(_controller.LastError) ? L10n.Tr("保存できませんでした。", "Could not save.") : _controller.LastError, "OK");
                return;
            }
            _guideSaved = true;
            ShowNotification(new GUIContent(L10n.Tr($"「{saved.Name}」を保存しました", $"Saved \"{saved.Name}\"")));
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
            _settings.KeepLayersApart = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("重ね着の層を押し分ける", "Keep Layers Apart"),
                L10n.Tr("オンにすると、重ね着の外の布を内へ押したときに内の布も一緒に押され、外の布が内の布を突き抜けません（重ね着をまとめて編集するときや、1つのメッシュに層があるとき）。" +
                        "外へ引くと、周りの外の布が締まって内の布を少し押すことがあります。気になるときはオフにします。既定はオン",
                    "When on, pushing the outer layer of layered clothing also pushes the inner layer, and the outer layer never passes through it (when editing layers together, or when one mesh has several layers). " +
                    "Pulling outward may tighten the surrounding outer cloth and push the inner layer slightly; turn this off if that bothers you. Default on")),
                _settings.KeepLayersApart);

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
