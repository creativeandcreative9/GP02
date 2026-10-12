using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 布の編集の操作画面。道具（つまむ・回す・拡大縮小・ピン留め・元に戻すブラシ・選んで動かす）をシーンビューで使い、結果をシェイプキーとして保存する。
    /// 操作の流れは <see cref="ClothEditController"/> が持ち、ここはイベントを読んで渡すことと、表示だけを受け持つ。
    /// 画面は状態に合わせて出し分ける: 編集前は「手順」「対象」と大きな「編集を開始」だけ、道具・ブラシ・対称・選んだ点・保存は編集中だけ。
    /// 上部の「かんたん／くわしく」（<see cref="ClothEditView"/>）で、出す項目の量を切り替える。
    /// ウィンドウの内容はこのファイル、シーンビューの入力と重ね描きは ClothEditWindow.SceneView.cs、
    /// 「選んで動かす」の区切りとハンドルは ClothEditWindow.Selection.cs、前回の編集の復元・始め方・確認は ClothEditWindow.Recovery.cs。
    /// </summary>
    public sealed partial class ClothEditWindow : EditorWindow
    {
        // スライダーの範囲（センチメートル）。ホイールやキーで変えるときも、この範囲に収める
        const float BrushMinCm = 1f;
        const float BrushMaxCm = 20f;
        const float InfluenceMinCm = 5f;
        const float InfluenceMaxCm = 100f;

        // ツールバーに並べる順（ClothEditMode の数値とは別）。シーンビューの道具バーと同じ並びにする。
        // ピン解除は「ピン留め＋外す」としてピン留めに含める。かんたん表示では、回す・拡大縮小のボタンを出さない（キーは今のまま使える）
        static readonly ClothEditMode[] ToolOrder =
        {
            ClothEditMode.Grab, ClothEditMode.Select, ClothEditMode.Pin,
            ClothEditMode.Relax, ClothEditMode.Rotate, ClothEditMode.Scale,
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
        [SerializeField] bool _foldSymmetry = true;
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
        GUIStyle _startButtonStyle;

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
            ShapeKeyClothEdit.ConfirmLeave = ConfirmLeaveForShapeKeyEdit;
            ClothEditShortcutBindings.Handler = OnShortcut;
            SyncController();
            SceneView.beforeSceneGui += OnBeforeSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Selection.selectionChanged += OnHierarchySelectionChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            ClothEditView.Changed += OnViewModeChanged;
            _warningsDirty = true;
            _candidatesDirty = true;
            _overlayOwner = this;
            _startCardDismissed = ClothEditHelp.IsStartCardDismissed();
            ClothEditStash.DeleteOlderThan(ClothEditStash.MaxAge);
            InitTargetOnEnable();
            RefreshStashInfo(true);
        }

        void OnDisable()
        {
            if (_overlayOwner == this) _overlayOwner = null;
            // ウィンドウを閉じる・スクリプトの再読み込みの前に、保存していない編集を取っておく
            if (_controller != null) StashNow();
            ClothEditToolHandles.Restore(this);
            SceneView.beforeSceneGui -= OnBeforeSceneGUI;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Selection.selectionChanged -= OnHierarchySelectionChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            ClothEditView.Changed -= OnViewModeChanged;
            if (ShapeKeyClothEdit.ControllerProvider != null && ShapeKeyClothEdit.ControllerProvider() == _controller) ShapeKeyClothEdit.ControllerProvider = null;
            if (ShapeKeyClothEdit.ConfirmLeave == (System.Func<ClothEditLeaveReason, bool>)ConfirmLeaveForShapeKeyEdit) ShapeKeyClothEdit.ConfirmLeave = null;
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
            _candidatesDirty = true;
        }

        void OnViewModeChanged()
        {
            Repaint();
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (_controller == null) return;
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // プレイモードに入る前に、保存していない編集を取っておく
                    StashNow();
                    _controller.End();
                    ClothEditToolHandles.Restore(this);
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    _controller.End();
                    ClothEditToolHandles.Restore(this);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // プレイモードから戻ったら、前回の編集が残っていないか探し直す
                    _warningsDirty = true;
                    RefreshStashInfo(true);
                    Repaint();
                    break;
            }
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
            SyncToolHandles();
            if (_radiusDrag.Active && (_controller == null || !_controller.IsEditing)) _radiusDrag.Cancel();
        }

        static bool Shows(ClothEditUiItem item) => ClothEditView.Shows(item);

        // 編集中だけ、Unity の道具のハンドル（移動の矢印など）を隠す。編集の開始・終了のほか、Undo やウィンドウの外から編集が変わったときも、ここで追いつく
        void SyncToolHandles()
        {
            ClothEditToolHandles.Sync(this, _controller != null && _controller.IsEditing);
        }

        void OnGUI()
        {
            if (_controller == null) return;
            string title = L10n.Tr("GP02 布の編集", "GP02 Cloth Edit");
            if (titleContent.text != title) titleContent = new GUIContent(title);

            if (Event.current.type == EventType.Layout)
            {
                RefreshTargetWarnings();
                RefreshStashInfo(false);
            }
            DrawTopBar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawBands();
            DrawGuide();
            DrawTargetSection();
            DrawDiagnosticsSection();
            DrawToolSection();
            DrawSelectionSection();
            DrawBrushSection();
            DrawSymmetrySection();
            DrawDisplaySection();
            DrawPoseCheckSection();
            DrawSaveSection();
            DrawAdvancedSection();
            EditorGUILayout.EndScrollView();
            DrawStatusLine();

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

        // ---- 上部（かんたん／くわしく、言語、ショートカット一覧） ----

        void DrawTopBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                ClothEditView.DrawToggle(EditorStyles.toolbarButton, 112f);
                GUILayout.Space(2f);
                var languageLabels = new[]
                {
                    new GUIContent("日本語", L10n.Tr("画面の言葉を日本語にします", "Show the interface in Japanese")),
                    new GUIContent("English", L10n.Tr("画面の言葉を英語にします", "Show the interface in English")),
                };
                int language = GUILayout.Toolbar((int)L10n.Language, languageLabels, EditorStyles.toolbarButton, GUILayout.Width(104f));
                if (language != (int)L10n.Language)
                {
                    L10n.Language = (UiLanguage)language;
                    _warningsDirty = true;
                    SceneView.RepaintAll();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(L10n.Tr("説明書", "Manual"), L10n.Tr("使い方の説明書を開きます（Documentation~ フォルダの文書）", "Opens the manual (a document in the Documentation~ folder)")),
                    EditorStyles.toolbarButton, GUILayout.Width(52f)))
                {
                    OpenManual();
                }
                bool show = GUILayout.Toggle(_showShortcuts,
                    new GUIContent("?", L10n.Tr("ショートカット一覧を出す・隠す（F1）", "Show / hide the shortcut list (F1)")),
                    EditorStyles.toolbarButton, GUILayout.Width(22f));
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
                ClearResult();
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
            if (!Shows(ClothEditUiItem.Guide)) return;

            if (editing)
            {
                _foldGuide = Section(_foldGuide, L10n.Tr("手順（今どこまで済んだか）", "Steps (where you are)"));
                if (!_foldGuide)
                {
                    // 畳んでいても、今の手順は1行で出す。押すと開く
                    DrawGuideNextLine();
                    return;
                }
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
                if (state == GuideStepState.Current)
                {
                    EditorGUILayout.HelpBox(ClothEditGuide.Detail(i), MessageType.Info);
                    if (editing && i == ClothEditGuide.PinStep) DrawAutoPinButton();
                }
            }
            EditorGUILayout.Space();
        }

        GUIStyle _nextLineStyle;

        // 手順の区切りを畳んでいるときの、今の手順の1行（「次: 3. 動かさない所をピン留め」）。押すと手順を開く。
        // 今の手順がピン留めなら、「服の付け根を自動でピン留め」も、ここに出す（手順を開かなくても押せるように）
        void DrawGuideNextLine()
        {
            if (_nextLineStyle == null)
            {
                _nextLineStyle = new GUIStyle(EditorStyles.label) { padding = new RectOffset(18, 2, 0, 0) };
            }
            Color link = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.78f, 1f, 1f) : new Color(0.05f, 0.3f, 0.75f, 1f);
            _nextLineStyle.normal.textColor = link;
            _nextLineStyle.hover.textColor = link;
            _nextLineStyle.active.textColor = link;

            int step = ClothEditGuide.CurrentStep(_guideStates);
            var content = new GUIContent(ClothEditGuide.NextLine(_guideStates), L10n.Tr("押すと、5つの手順を開きます", "Press to open the five steps"));
            Rect rect = GUILayoutUtility.GetRect(content, _nextLineStyle);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (GUI.Button(rect, content, _nextLineStyle))
            {
                _foldGuide = true;
                GUIUtility.ExitGUI();
            }
            if (step == ClothEditGuide.PinStep) DrawAutoPinButton();
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
            if (_controller.IsEditing)
            {
                DrawEditingHeader();
                return;
            }

            _foldTarget = Section(_foldTarget, L10n.Tr("対象", "Target"));
            if (_foldTarget)
            {
                if (Shows(ClothEditUiItem.TargetField)) DrawTargetFields();
                // アバター全体のように、衣装そのものではないものを選んだときは、その中の衣装の一覧
                DrawCandidates();
            }

            // 問題の一覧と「直す」ボタンは「診断」に出す。ここでは始められないものだけを見る
            bool blocked = false;
            foreach (TargetWarning warning in _warnings)
            {
                if (warning.Kind == TargetWarningKind.CannotStart) blocked = true;
            }
            if (blocked) EditorGUILayout.HelpBox(L10n.Tr("この対象では編集を始められません。理由は下の診断を見てください。", "Editing cannot start with this target. See the diagnostics below for the reason."), MessageType.Error);
            if (!string.IsNullOrEmpty(_controller.LastError)) EditorGUILayout.HelpBox(_controller.LastError, MessageType.Error);
            DrawLikelyBodyNotice();
            DrawResultBox();

            EditorGUILayout.Space();
            if (_startButtonStyle == null)
            {
                _startButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold };
            }
            using (new EditorGUI.DisabledScope(_target == null || blocked))
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("編集を開始", "Start Editing"),
                    L10n.Tr("衣装を、つまんで引っ張れる状態にします（数秒かかります）", "Gets the outfit ready so you can grab and pull it (takes a few seconds)")),
                    _startButtonStyle, GUILayout.Height(42f)))
                {
                    StartEdit();
                    GUIUtility.ExitGUI();
                }
            }
            if (_target == null)
            {
                EditorGUILayout.LabelField(L10n.Tr("Hierarchy で衣装を選ぶと、ここに入ります", "Select the outfit in the Hierarchy and it appears here"), EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.Space();
        }

        // 編集中の見出し: 衣装の名前、「保存」、「編集を終了」。保存は下の「保存」の区切りまで行かなくても押せる。
        // その下に、直前の操作の詳しい結果（通知は短いので、詳しい文はここに残す）
        void DrawEditingHeader()
        {
            ClothEditSession session = _controller.Session;
            string names = session != null ? session.DescribeTargets() : "";
            GP02ShapeKeys entryComponent = ShapeKeyClothEdit.ActiveComponent;
            bool overwrite = entryComponent != null && _controller.Target != null && entryComponent.gameObject == _controller.Target.gameObject;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(L10n.Tr("編集中: ", "Editing: ") + names,
                    session != null && session.RendererCount > 1
                        ? L10n.Tr($"{session.RendererCount} 個をまとめて編集しています（重ね着どうしの接触 {session.LayerContacts.Count:N0} 組）", $"Editing {session.RendererCount} outfits together ({session.LayerContacts.Count:N0} layer contacts)")
                        : ""), HeaderLabelStyle(), GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));
                string saveTip = overwrite
                    ? L10n.Tr($"シェイプキー「{ShapeKeyClothEdit.ActiveEntryName}」に、同じ名前で上書き保存します。Ctrl+Z で取り消せます", $"Saves over the shape key \"{ShapeKeyClothEdit.ActiveEntryName}\" under the same name. Ctrl+Z undoes this")
                    : L10n.Tr($"今の形を、シェイプキー「{_shapeName}」として保存します。名前は、下の「保存」の区切りで変えられます",
                        $"Saves the current shape as the shape key \"{_shapeName}\". You can change the name in the Save section below");
                if (GUILayout.Button(new GUIContent(L10n.Tr("保存", "Save"), saveTip), GUILayout.Width(56f)))
                {
                    SaveFromHeader();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("編集を終了", "End Editing"), L10n.Tr("編集を終えて、元の表示に戻します。保存していない編集があれば、先に確認します", "Ends editing and restores the normal display. Asks first if there are unsaved edits")),
                    GUILayout.Width(96f)))
                {
                    RequestEnd();
                    GUIUtility.ExitGUI();
                }
            }
            DrawResultBox();
            EditorGUILayout.Space();
        }

        /// <summary>
        /// 編集中の見出しの「保存」。一覧から「布で編集」で始めたときは同じ名前で上書き保存し、そうでなければ名前の欄（<c>_shapeName</c>）の名前で保存する。
        /// 保存できたら true（できなければ、理由をダイアログで出す）。
        /// </summary>
        internal bool SaveFromHeader()
        {
            return SaveBeforeLeaving();
        }

        GUIStyle _headerLabelStyle;

        GUIStyle HeaderLabelStyle()
        {
            if (_headerLabelStyle == null) _headerLabelStyle = new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip };
            return _headerLabelStyle;
        }

        GUIStyle _placeholderStyle;

        // 衣装の欄。空のときの「None (Renderer)」は、そのまま見せず、「（衣装を選んでください）」を重ねて出す。
        // 欄の文字だけを見えなくして（GUI.contentColor）、欄そのもの（ドラッグ＆ドロップ・選ぶ丸いボタン）は、そのまま使う
        Renderer DrawOutfitField(string label, Renderer value)
        {
            Color previous = GUI.contentColor;
            if (value == null) GUI.contentColor = Color.clear;
            var result = (Renderer)EditorGUILayout.ObjectField(label, value, typeof(Renderer), true);
            GUI.contentColor = previous;
            if (value == null && Event.current.type == EventType.Repaint)
            {
                if (_placeholderStyle == null) _placeholderStyle = new GUIStyle(EditorStyles.label) { padding = new RectOffset(4, 0, 0, 0) };
                Color grey = EditorGUIUtility.isProSkin ? new Color(0.62f, 0.62f, 0.62f, 1f) : new Color(0.4f, 0.4f, 0.4f, 1f);
                _placeholderStyle.normal.textColor = grey;
                Rect field = GUILayoutUtility.GetLastRect();
                var text = new Rect(field.x + EditorGUIUtility.labelWidth + 2f, field.y, Mathf.Max(0f, field.width - EditorGUIUtility.labelWidth - 22f), field.height);
                GUI.Label(text, L10n.Tr("（衣装を選んでください）", "(Select an outfit)"), _placeholderStyle);
            }
            return result;
        }

        void DrawTargetFields()
        {
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                _target = DrawOutfitField(L10n.Tr("対象", "Target"), _target);
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
                    _extraTargets[i] = DrawOutfitField(L10n.Tr($"対象 {i + 2}", $"Target {i + 2}"), _extraTargets[i]);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("外す", "Remove"), L10n.Tr("この対象を一覧から外す", "Remove this target from the list")), GUILayout.Width(48f)))
                    {
                        _extraTargets.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            if (EditorGUI.EndChangeCheck()) _warningsDirty = true;

            if (GUILayout.Button(new GUIContent(L10n.Tr("選択中のオブジェクトから設定", "Set from Selection"),
                L10n.Tr("ヒエラルキーで選んでいる衣装を対象にします。編集していないときは、選ぶだけで自動で対象になります。アバター全体を選んだときは、下に出る「この中の衣装」から選べます",
                    "Use the outfit selected in the Hierarchy as the target. While you are not editing, selecting it is enough. If you select the whole avatar, pick from \"Outfits in this object\" below"))))
            {
                PickFromSelection();
                _warningsDirty = true;
            }
            if (!Shows(ClothEditUiItem.TargetAddLayers)) return;

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
                _pickHint = null;
                return;
            }
            // 衣装そのものではない（アバター全体など）: ダイアログにせず、窓の中の「この中の衣装」の一覧と案内を出す
            ShowPickHint(selected);
        }

        // 衣装が見つからなかった理由と、次にすることを、窓の中に出す（ダイアログにしない）
        void ShowPickHint(GameObject selected)
        {
            _candidatesDirty = true;
            RefreshCandidates();
            if (selected == null)
            {
                _pickHint = L10n.Tr("オブジェクトが選ばれていません。Hierarchy で衣装を選んでから押してください。", "No object is selected. Select the outfit in the Hierarchy first.");
            }
            else if (_candidates.Count > 0)
            {
                _pickHint = L10n.Tr($"「{selected.name}」は衣装そのものではありません。下の「この中の衣装」から、編集する衣装を選んでください。",
                    $"\"{selected.name}\" is not an outfit itself. Pick the outfit to edit from \"Outfits in this object\" below.");
            }
            else
            {
                _pickHint = L10n.Tr($"「{selected.name}」の中に、編集できる衣装がありません。衣装のオブジェクト（子オブジェクトのことが多いです）を選んでください。",
                    $"There is no outfit to edit inside \"{selected.name}\". Select the outfit object (often a child object).");
            }
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
            if (added == 0 && (selected.Length == 0 || ClothEditController.FindRenderer(selected[0]) == null)) ShowPickHint(selected.Length > 0 ? selected[0] : null);
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
                _pickHint = L10n.Tr("今の対象と重なっている、同じアバターの布は見つかりませんでした。", "No cloth of the same avatar overlapping the current targets was found.");
            }
        }

        void FixReadWrite(Renderer renderer)
        {
            ClothEditWarnings.TryEnableReadWrite(renderer, out string message);
            _warningsDirty = true;
            EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        // 編集を始める。始められたら true
        bool StartEdit()
        {
            List<Renderer> targets = CollectTargets();
            // 重みのプレビュー（一時メッシュ）が出たままだと、その形が編集の基準になってしまう
            foreach (Renderer target in targets) ShapeKeyPreview.End(target);
            if (!_controller.TryStartLayered(targets, out string error))
            {
                EditorUtility.DisplayDialog("GP02", error, "OK");
                return false;
            }
            _shapeName = _controller.SuggestShapeName();
            ShapeKeyClothEdit.Clear();   // 一覧から「布で編集する」で始めたのではないので、前の再編集の記録は使わない
            _pickHint = null;
            _candidatesDirty = true;

            // シーンビューには、まだ閉じていなければ、小さな札（最初に覚える3つの操作と「F1: すべての操作」）が出る。
            // 約30行のショートカット一覧は、自動では出さない（「?」・F1・道具バーで出す）
            _startCardDismissed = ClothEditHelp.IsStartCardDismissed();
            SyncToolHandles();   // 編集中は、Unity の道具のハンドルを隠す
            return true;
        }

        // ---- 道具 ----

        void DrawToolSection()
        {
            if (!_controller.IsEditing) return;
            _foldTool = Section(_foldTool, L10n.Tr("道具", "Tools"));
            if (!_foldTool) return;

            DrawToolButtons();

            EditorGUI.BeginChangeCheck();
            switch (_settings.Tool)
            {
                case ClothEditMode.Grab:
                    if (!Shows(ClothEditUiItem.ToolOptions)) break;
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
                    if (!Shows(ClothEditUiItem.ToolOptions)) break;
                    _settings.RotateAxis = (RotateAxisMode)EditorGUILayout.Popup(new GUIContent(L10n.Tr("回転軸", "Rotation Axis"),
                        L10n.Tr("視線の向き: 画面に向かって回る。面の法線: つまんだ所の面に垂直な軸のまわりに回る。", "View direction: rotates facing the screen. Surface normal: rotates around the axis perpendicular to the grabbed surface.")),
                        (int)_settings.RotateAxis,
                        new[] { L10n.Tr("視線の向き", "View direction"), L10n.Tr("面の法線", "Surface normal") });
                    break;
                case ClothEditMode.Pin:
                case ClothEditMode.Unpin:
                    DrawPinOptions();
                    break;
                case ClothEditMode.Relax:
                    if (!Shows(ClothEditUiItem.ToolOptions)) break;
                    _settings.RelaxStrength = EditorGUILayout.Slider(new GUIContent(L10n.Tr("強さ", "Strength"),
                        L10n.Tr("1回塗るごとに、元の形へ近づける割合です。1 で元の形に戻ります。", "How far each stroke brings the cloth back to its original shape. 1 restores it fully.")),
                        _settings.RelaxStrength, 0f, 1f);
                    break;
                case ClothEditMode.Select:
                    EditorGUILayout.LabelField(L10n.Tr("ドラッグ: 塗って選ぶ（Ctrl: 外す。半径はブラシ半径）", "Drag: paint to select (Ctrl: deselect; radius is the brush radius)"), EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.LabelField(L10n.Tr("Shift+ドラッグ: 四角で囲む / ダブルクリック: 部品ごと / Esc: 解除", "Shift+Drag: box / Double-click: whole part / Esc: clear"), EditorStyles.wordWrappedMiniLabel);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) _settingsDirty = true;

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
            if (Shows(ClothEditUiItem.HistoryExtras))
            {
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
            }
            EditorGUILayout.LabelField(L10n.Tr("Ctrl+Z / Ctrl+Y で戻す・やり直す。ドラッグ中は Esc で取り消し", "Ctrl+Z / Ctrl+Y to undo / redo. Esc cancels while dragging"), EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        // 出す道具のボタン。今の道具が、かんたん表示で隠れているもの（回す・拡大縮小）でも、ボタンを出して選ばれている印を見せる
        List<ClothEditMode> ToolsToShow()
        {
            var tools = new List<ClothEditMode>();
            foreach (ClothEditMode mode in ToolOrder)
            {
                if (ShowsTool(mode) || IsCurrentTool(mode)) tools.Add(mode);
            }
            return tools;
        }

        static bool ShowsTool(ClothEditMode mode)
        {
            switch (mode)
            {
                case ClothEditMode.Rotate: return Shows(ClothEditUiItem.ToolRotate);
                case ClothEditMode.Scale: return Shows(ClothEditUiItem.ToolScale);
                case ClothEditMode.Pin: return Shows(ClothEditUiItem.ToolPin);
                case ClothEditMode.Relax: return Shows(ClothEditUiItem.ToolRelax);
                case ClothEditMode.Select: return Shows(ClothEditUiItem.ToolSelect);
                default: return Shows(ClothEditUiItem.ToolGrab);
            }
        }

        bool IsCurrentTool(ClothEditMode mode)
        {
            return _settings.Tool == mode || (mode == ClothEditMode.Pin && _settings.Tool == ClothEditMode.Unpin);
        }

        void DrawToolButtons()
        {
            List<ClothEditMode> tools = ToolsToShow();
            int columns = ClothEditView.IsDetailed ? 3 : 2;
            for (int start = 0; start < tools.Count; start += columns)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int k = 0; k < columns; k++)
                    {
                        if (start + k < tools.Count) DrawToolButton(tools[start + k]);
                        else GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        // 道具のボタン。「選んで動かす」だけは、もう一度押すと直前の道具に戻る（Q と同じ）
        void DrawToolButton(ClothEditMode mode)
        {
            bool isOn = IsCurrentTool(mode);
            var content = new GUIContent(ClothEditHelp.ToolName(mode) + " (" + ClothEditHelp.ToolKey(mode) + ")", ClothEditHelp.ToolTip(mode));
            bool now = GUILayout.Toggle(isOn, content, "Button", GUILayout.MinHeight(24f));
            if (now == isOn) return;
            RememberToolBeforeSelect();
            if (mode == ClothEditMode.Select) _settings.Tool = now ? ClothEditMode.Select : ClothEditShortcuts.LeaveSelectTool(_toolBeforeSelect);
            else if (now) _settings.Tool = mode;
            else return;
            _settingsDirty = true;
            SceneView.RepaintAll();
        }

        // ピン留めの道具: 「塗ると外す」と、操作のしかた
        void DrawPinOptions()
        {
            bool erase = _settings.PinEraseMode || _settings.Tool == ClothEditMode.Unpin;
            if (Shows(ClothEditUiItem.PinEraseToggle))
            {
                bool newErase = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("塗ると外す", "Paint to erase"),
                    L10n.Tr("オンにすると、ドラッグでピンを外し、Ctrl+ドラッグで留めます（Ctrl を押さずに外したいとき、マウスだけで操作したいときに）。Shift+P でも入ります",
                        "When on, dragging removes pins and Ctrl+Drag adds them (for removing without Ctrl, or when using only the mouse). Shift+P also turns it on")), erase);
                if (newErase != erase)
                {
                    _settings.PinEraseMode = newErase;
                    if (_settings.Tool == ClothEditMode.Unpin) _settings.Tool = ClothEditMode.Pin;
                }
            }
            if (Shows(ClothEditUiItem.ToolOptions))
            {
                _settings.PinRectFrontOnly = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("四角は表側だけ", "Box: front only"),
                    L10n.Tr("Shift+ドラッグの四角で、カメラ側を向いた点だけを対象にします。オフにすると、裏側（裏の面）の点も入ります。", "In a Shift+Drag box, only points facing the camera are used. Turn off to include the back side as well.")),
                    _settings.PinRectFrontOnly);
            }
            EditorGUILayout.LabelField(erase
                ? L10n.Tr("ドラッグ: 外す / Ctrl+ドラッグ: 留める / Shift+ドラッグ: 四角で囲む / ダブルクリック: 部品ごと", "Drag: remove / Ctrl+Drag: add / Shift+Drag: box / Double-click: whole part")
                : L10n.Tr("ドラッグ: 留める / Ctrl+ドラッグ: 外す / Shift+ドラッグ: 四角で囲む / ダブルクリック: 部品ごと（Ctrl で外す）", "Drag: pin / Ctrl+Drag: remove / Shift+Drag: box / Double-click: whole part (Ctrl removes)"),
                EditorStyles.wordWrappedMiniLabel);
            // ウエストや首まわりを一周ぶん、まとめて留める（見えている側だけでなく、後ろ側も）
            DrawAutoPinButton();
        }

        // ---- ブラシ ----

        void DrawBrushSection()
        {
            if (!_controller.IsEditing) return;
            _foldBrush = Section(_foldBrush, L10n.Tr("ブラシ", "Brush"));
            if (!_foldBrush) return;

            _brushCentimeters = EditorGUILayout.Slider(new GUIContent(L10n.Tr("ブラシ半径（cm）", "Brush Radius (cm)"),
                L10n.Tr("つまむ範囲の半径。Ctrl+ホイール、または [ ] キーでも変えられます。", "Radius of the grabbed area. Also Ctrl+Wheel or the [ ] keys.")),
                _brushCentimeters, BrushMinCm, BrushMaxCm);
            if (!Shows(ClothEditUiItem.BrushDetails))
            {
                SyncController();
                EditorGUILayout.Space();
                return;
            }
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
                    L10n.Tr("「メッシュに沿う」で範囲を測るとき、この距離以内にある別の部品にも範囲を広げます。袖とフリルのように分かれたパーツを一緒に動かせます。" +
                            "布の計算のつながりは変わりません（範囲の決め方だけ）。0 で広げません。重ね着どうしも、この距離より近ければつながります",
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
            if (!_controller.IsEditing) return;
            _foldSymmetry = Section(_foldSymmetry, L10n.Tr("対称", "Symmetry"));
            if (!_foldSymmetry) return;

            bool detailed = Shows(ClothEditUiItem.SymmetryDetails);
            EditorGUI.BeginChangeCheck();
            _settings.SymmetryEnabled = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("対称編集", "Symmetric Editing"),
                L10n.Tr("オンにすると、つまむ・回す・拡大縮小・ピン・元に戻すブラシが、左右の反対側にも同じように働きます。", "When on, grab / rotate / scale / pin / restore also act on the opposite side, mirrored.")),
                _settings.SymmetryEnabled);
            if (detailed)
            {
                _settings.SymmetryAxis = (SymmetryAxis)EditorGUILayout.Popup(new GUIContent(L10n.Tr("軸", "Axis"),
                    L10n.Tr("アバターのローカル座標の軸。この軸に垂直な面が左右の中心（鏡の面）になります（左右対称なら X）。", "An axis in the avatar's local space. The plane perpendicular to it is the mirror (X for left / right).")),
                    (int)_settings.SymmetryAxis, new[] { "X", "Y", "Z" });
                float offsetCm = EditorGUILayout.FloatField(new GUIContent(L10n.Tr("中心のずれ（cm）", "Center Offset (cm)"),
                    L10n.Tr("アバターの中心から、左右の中心（鏡の面）までのずれ（アバターのローカル座標）。「左右の中心を自動で合わせる」で衣装に合わせられます。", "Offset of the mirror plane from the avatar's center (in the avatar's local space). Auto-Fit Mirror Plane matches it to the outfit.")),
                    _settings.SymmetryOffset * 100f);
                _settings.SymmetryOffset = offsetCm * 0.01f;
                float toleranceCm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("鏡像を探す許容距離（cm）", "Mirror Tolerance (cm)"),
                    L10n.Tr("鏡に映した位置から、この距離以内の点を反対側の相手にします。左右で形が少し違う衣装では広げます。広げすぎると、別の部分を相手にすることがあります。既定 2cm",
                        "A point within this distance of the mirrored position becomes the partner on the opposite side. Widen it for outfits whose sides differ slightly; too wide may pick an unrelated part. Default 2 cm")),
                    _settings.MirrorTolerance * 100f, ClothEditSettings.MinMirrorTolerance * 100f, ClothEditSettings.MaxMirrorTolerance * 100f);
                _settings.MirrorTolerance = toleranceCm * 0.01f;
            }
            bool symmetryChanged = EditorGUI.EndChangeCheck();
            if (symmetryChanged) _settingsDirty = true;

            bool editing = _controller.IsEditing;
            if (Shows(ClothEditUiItem.SymmetryAutoFit))
            {
                using (new EditorGUI.DisabledScope(!editing || _controller.IsBusy))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("左右の中心を自動で合わせる", "Auto-Fit Mirror Plane"),
                        L10n.Tr("元の形を左右に映したとき一番よく重なる位置を探して、左右の中心にします。衣装がアバターの中心からずれていても、衣装そのものの左右の中心で、対称に編集できます（編集中に使えます）",
                            "Finds where the original shape best overlaps its mirror image and puts it in Center Offset, so you can edit symmetrically around the outfit's own plane even if it is off the avatar's center (while editing)"))))
                    {
                        FitMirrorPlane();
                    }
                }
            }

            if (editing && _settings.SymmetryEnabled)
            {
                int unmatched = _controller.UnmatchedMirrorCount;
                float ratio = _controller.UnmatchedMirrorRatio;
                if (detailed) EditorGUILayout.LabelField(L10n.Tr("相手のいない点", "Unmatched Points"), L10n.Tr($"{unmatched:N0} 個（{ratio * 100f:F1}%）", $"{unmatched:N0} ({ratio * 100f:F1}%)"));
                if (ratio > ClothEditSettings.UnmatchedMirrorWarnRatio)
                {
                    EditorGUILayout.HelpBox(L10n.Tr("左右の相手がいない点が多いです。その点をつまんでも、反対側は動きません。「左右の中心を自動で合わせる」を押してください。",
                        "Many points have no partner. Grabbing them does not move the opposite side. Use Auto-Fit Mirror Plane or widen the tolerance."), MessageType.Warning);
                }
            }

            if (!detailed)
            {
                EditorGUILayout.Space();
                return;
            }

            EditorGUI.BeginChangeCheck();
            _settings.ShowUnmatchedMirror = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("相手のいない点を表示", "Show Unmatched"),
                L10n.Tr("対称がオンのとき、鏡像の相手が見つからない点を赤紫の点で見せます。そこをつまんでも反対側は動きません", "While symmetry is on, shows points with no mirror partner as magenta dots. Grabbing them does not move the opposite side")),
                _settings.ShowUnmatchedMirror);
            _settings.ShowMirrorDifference = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("左右差を色分け", "Show L/R Difference"),
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

        // 結果の通知は短く、詳しい文（中心のずれ・重なった割合など）は、窓の結果の欄に残す
        void FitMirrorPlane()
        {
            bool ok = _controller.AutoFitSymmetryPlane(out string message);
            _settings.Save();
            if (ok) Report(L10n.Tr("左右の中心を合わせました", "Mirror plane fitted"), message);
            else Report(L10n.Tr("合わせられませんでした", "Could not fit it"), message, MessageType.Warning);
            SceneView.RepaintAll();
        }

        void SymmetrizeShape()
        {
            int changed = _controller.SymmetrizeShape();
            if (changed > 0)
            {
                Report(L10n.Tr("左右対称にしました", "Symmetrized"), L10n.Tr($"{changed:N0} 点を左右対称にしました（仕上げ中）", $"Symmetrized {changed:N0} points (settling)"));
            }
            else
            {
                Report(L10n.Tr("変わる点がありません", "Nothing to change"),
                    L10n.Tr("変わる点がありませんでした（すでに左右対称か、相手のいない点だけです）", "Nothing changed (already symmetric, or only points without a partner)"));
            }
            SceneView.RepaintAll();
        }

        // ---- 表示 ----

        void DrawDisplaySection()
        {
            if (!_controller.IsEditing || !Shows(ClothEditUiItem.DisplaySection)) return;
            _foldDisplay = Section(_foldDisplay, L10n.Tr("表示", "Display"));
            if (!_foldDisplay) return;

            EditorGUI.BeginChangeCheck();
            _settings.ShowBrushPreview = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("範囲を色で表示", "Show Brush Range"),
                L10n.Tr("マウスを乗せている間、範囲に入る点を重みで色分けして表示します。", "While hovering, shows the points in range as dots colored by weight.")),
                _settings.ShowBrushPreview);
            _settings.ShowStretchHeatmap = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("伸びのヒートマップ", "Stretch Heatmap"),
                L10n.Tr("点ごとの伸びを緑→黄→赤で表示します（0% → 5% → 15%）。", "Shows each point's stretch from green to yellow to red (0% to 5% to 15%).")),
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
            EditorGUILayout.Space();
        }

        // ---- 保存 ----
        // 保存の区切りは、ボタンをこのメソッドの中の1か所にまとめてある（シェイプキーの一覧と差し替えやすいように）

        void DrawSaveSection()
        {
            if (!_controller.IsEditing) return;
            _foldSave = Section(_foldSave, L10n.Tr("保存", "Save"));
            if (!_foldSave) return;

            GP02ShapeKeys entryComponent = ShapeKeyClothEdit.ActiveComponent;
            if (entryComponent != null && entryComponent.gameObject == _controller.Target.gameObject)
            {
                // 一覧から「布で編集する」で始めたときは、そのシェイプキーへ保存する
                EditorGUILayout.HelpBox(L10n.Tr($"シェイプキー「{ShapeKeyClothEdit.ActiveEntryName}」を布で編集しています。保存はデータに入ります。メッシュへの反映は、シェイプキーの一覧から行います。",
                    $"Editing the shape key \"{ShapeKeyClothEdit.ActiveEntryName}\" with cloth. Saving stores it in the data. Apply it to the mesh from the shape key list."), MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("同じ名前で上書き保存", "Save (overwrite)"), L10n.Tr("編集したシェイプキーを、同じ名前で上書きします。Ctrl+Z で取り消せます", "Overwrites the shape key being edited, under the same name. Ctrl+Z undoes this")))) SaveEntry(true);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("別名で追加", "Save as new"), L10n.Tr("今の形を、新しいシェイプキーとして一覧に加えます", "Adds the current shape to the list as a new shape key")))) SaveEntry(false);
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("シェイプキーの一覧を開く", "Open the shape key list"), L10n.Tr("保存したシェイプキーの再編集・分割・反転・メッシュへの反映をする一覧を開きます", "Opens the list for re-editing, splitting, flipping and applying saved shape keys to the mesh")))) OpenShapeKeyList();
                DrawSavedKeys();
                EditorGUILayout.Space();
                return;
            }

            _shapeName = EditorGUILayout.TextField(new GUIContent(L10n.Tr("シェイプキーの名前", "Shape Key Name"),
                L10n.Tr("保存するシェイプキー（BlendShape）の名前。メッシュに同じ名前があると保存できません", "Name of the shape key (BlendShape) to save. Saving fails if the mesh already has one with this name")), _shapeName);
            if (GUILayout.Button(new GUIContent(L10n.Tr("シェイプキーとして保存", "Save as Shape Key"),
                L10n.Tr("メッシュの複製にシェイプキーとして入れ、編集の内容をデータにも記録します（あとでシェイプキーの一覧から再編集・分割・反転できます）",
                    "Adds it to a copy of the mesh as a shape key and records the edit in the data (you can re-edit, split, or flip it later from the shape key list)")))) Save();
            DrawSavedKeys();
            EditorGUILayout.Space();
        }

        // 保存できたら true
        bool SaveEntry(bool overwrite)
        {
            ShapeKeyEntry saved = ShapeKeyClothEdit.SaveActive(_controller, overwrite, overwrite ? null : _controller.SuggestShapeName());
            if (saved == null)
            {
                EditorUtility.DisplayDialog("GP02", string.IsNullOrEmpty(_controller.LastError) ? L10n.Tr("保存できませんでした。", "Could not save.") : _controller.LastError, "OK");
                return false;
            }
            _guideSaved = true;
            AfterSaved();
            Report(L10n.Tr("保存しました", "Saved"),
                L10n.Tr($"「{saved.Name}」を保存しました。データに入りました（メッシュへの反映は、シェイプキーの一覧から行います）。", $"Saved \"{saved.Name}\" to the data (apply it to the mesh from the shape key list)."));
            return true;
        }

        // 保存できたら true
        bool Save()
        {
            if (!_controller.Save(_shapeName, out string message))
            {
                EditorUtility.DisplayDialog("GP02", message, "OK");
                return false;
            }
            AfterSaved();
            if (_controller.LastSavedMesh != null) EditorGUIUtility.PingObject(_controller.LastSavedMesh);
            if (_controller.IsEditing)
            {
                _shapeName = _controller.SuggestShapeName();
                Report(L10n.Tr("保存しました", "Saved"), message);
            }
            else
            {
                EditorUtility.DisplayDialog("GP02", message, "OK");   // 保存はできたが、編集を続けられなかった
            }
            return true;
        }

        // ---- 詳細設定 ----

        void DrawAdvancedSection()
        {
            if (!Shows(ClothEditUiItem.AdvancedSettings)) return;
            _foldAdvanced = Section(_foldAdvanced, L10n.Tr("詳細設定", "Advanced"));
            if (!_foldAdvanced) return;

            // Burst が無いときは、いつでもここから入れられる
            if (ClothSolver.DefaultKernel == null) BurstInstaller.DrawNotice();

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

            bool log = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("記録をコンソールに出す", "Log to Console"),
                L10n.Tr("オンにすると、編集の開始・保存・貫通や動作チェックの結果などの記録を、コンソールに出します（不具合を報告するときに使います）。既定はオフ",
                    "When on, writes a log of starting, saving, penetration and motion check results, etc. to the Console (useful when reporting a problem). Default off")),
                ClothEditLog.Enabled);
            if (log != ClothEditLog.Enabled) ClothEditLog.Enabled = log;

            bool changed = EditorGUI.EndChangeCheck();
            if (GUILayout.Button(new GUIContent(L10n.Tr("既定に戻す", "Reset to Defaults"),
                L10n.Tr("道具・ブラシ・対称・表示を含め、すべての設定値を既定値に戻します。", "Resets every setting, including tool, brush, symmetry and display, to its default."))))
            {
                _settings.ResetToDefaults();
                ClothEditLog.Enabled = false;
                _brushCentimeters = 3f;
                _influenceCentimeters = 30f;
                changed = true;
            }
            EditorGUI.indentLevel--;

            if (changed) _settingsDirty = true;
        }

        // ---- ウィンドウの一番下: 状態の数値を1行に ----
        // かんたん表示は、伸びを数字ではなく言葉と色（ほぼ無し=緑・少し=黄・大きい=赤。境目は伸びのヒートマップと同じ）で出す。
        // 数字は、行にマウスを乗せたときのツールチップに残す。くわしく表示は、数字のまま

        GUIStyle _statusStyle;

        void DrawStatusLine()
        {
            if (!_controller.IsEditing || !Shows(ClothEditUiItem.StatusLine)) return;
            ClothEditSession session = _controller.Session;
            if (session == null) return;

            string acceleration = ClothEditSession.DescribeAccelerationShort(session.Solver.Kernel);
            float stretch = _controller.LastStretch;
            if (ClothEditView.IsDetailed)
            {
                string text = L10n.Tr(
                    $"粒子 {session.ParticleCount:N0} ・ ピン {session.PinnedCount:N0} ・ 最大の伸び {stretch * 100f:F1}% ・ 高速化 {acceleration} ・ {_controller.StateLabel}",
                    $"Particles {session.ParticleCount:N0} · Pins {session.PinnedCount:N0} · Max stretch {stretch * 100f:F1}% · Acceleration {acceleration} · {_controller.StateLabel}");
                string tooltip = L10n.Tr(
                    $"粒子の数 {session.ParticleCount:N0} / ピンの数 {session.PinnedCount:N0} / 最大の伸び {stretch * 100f:F1}% / 高速化 {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)} / 状態 {_controller.StateLabel}",
                    $"Particles {session.ParticleCount:N0} / Pins {session.PinnedCount:N0} / Max stretch {stretch * 100f:F1}% / Acceleration {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)} / State {_controller.StateLabel}");
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label(new GUIContent(text, tooltip), EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                }
                return;
            }

            ClothEditHelp.StretchLevel level = ClothEditHelp.GetStretchLevel(stretch);
            string levelName = ClothEditHelp.StretchLevelName(level);
            string simpleTooltip = L10n.Tr(
                $"点の数 {session.ParticleCount:N0} / ピンの数 {session.PinnedCount:N0} / 最大の伸び {stretch * 100f:F1}%（{ClothEditSettings.HeatmapYellow * 100f:F0}% 未満: ほぼ無し、{ClothEditSettings.HeatmapRed * 100f:F0}% 未満: 少し、それ以上: 大きい）/ 高速化 {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)} / 状態 {_controller.StateLabel}",
                $"Points {session.ParticleCount:N0} / Pins {session.PinnedCount:N0} / Max stretch {stretch * 100f:F1}% (under {ClothEditSettings.HeatmapYellow * 100f:F0}%: none, under {ClothEditSettings.HeatmapRed * 100f:F0}%: slight, above: large) / Acceleration {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)} / State {_controller.StateLabel}");
            if (_statusStyle == null) _statusStyle = new GUIStyle(EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(new GUIContent(L10n.Tr($"点 {session.ParticleCount:N0} ・ ピン {session.PinnedCount:N0} ・ ", $"Points {session.ParticleCount:N0} · Pins {session.PinnedCount:N0} · "), simpleTooltip), EditorStyles.miniLabel);
                _statusStyle.normal.textColor = ClothEditHelp.StretchLevelColor(level, EditorGUIUtility.isProSkin);
                _statusStyle.fontStyle = FontStyle.Bold;
                GUILayout.Label(new GUIContent(L10n.Tr($"伸び: {levelName}", $"Stretch: {levelName}"), simpleTooltip), _statusStyle);
                GUILayout.Label(new GUIContent(L10n.Tr($" ・ 高速化 {acceleration} ・ {_controller.StateLabel}", $" · Acceleration {acceleration} · {_controller.StateLabel}"), simpleTooltip), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
            }
        }
    }
}
