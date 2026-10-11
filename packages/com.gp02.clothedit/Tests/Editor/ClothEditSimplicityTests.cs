using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using NVector3 = System.Numerics.Vector3;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// テスト全体で、編集の取っておき（Library/GP02/EditStash）を一時フォルダに向ける。
    /// ウィンドウを閉じるときに取っておきを書くので、向けないと、テストの残骸が開発用プロジェクトの Library に溜まる。
    /// </summary>
    [SetUpFixture]
    public class EditStashFolderRedirect
    {
        string _folder;

        [OneTimeSetUp]
        public void Redirect()
        {
            _folder = Path.Combine(Path.GetTempPath(), "gp02_stash_suite_" + Guid.NewGuid().ToString("N"));
            ClothEditStash.Folder = _folder;
        }

        [OneTimeTearDown]
        public void Restore()
        {
            ClothEditStash.Folder = null;
            try
            {
                if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
                // 消せなくても、テストの結果には関係ない
            }
        }
    }

    /// <summary>
    /// 簡単さ・使いやすさの整理（品質の吟味 A・B）: かんたん表示で出す項目、ピン留めの修飾キーと Unpin の互換、
    /// 保存していない編集の判定と確認、編集の取っておき・復元、右クリックから始める処理、届かないときの距離、保存したシェイプキーの一覧、Burst の初回案内。
    /// 画面に依存しない部分を EditMode で確かめる（画面の見た目は GUI のスモークテストの画像で見る）。
    /// </summary>
    public class ClothEditSimplicityTests
    {
        const string TempRoot = "Assets/GP02_SimplicityTemp";
        const string MeshFolder = TempRoot + "/Meshes";
        const string DataFolder = TempRoot + "/Data";
        const string SettingsPrefix = "GP02.Test.Simplicity.";

        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditWindow> _windows = new List<ClothEditWindow>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        string _stashFolder;
        string _savedStashFolder;
        UiLanguage _savedLanguage;
        ClothEditViewMode _savedMode;
        Func<ClothEditLeaveReason, ClothEditUnsavedChoice> _savedAsk;
        Func<string, string, string, string, bool> _savedConfirm;
        Object[] _savedSelection;
        double _time = 100.0;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.Japanese;
            _savedMode = ClothEditView.Mode;
            ClothEditView.Mode = ClothEditViewMode.Simple;
            _savedAsk = ClothEditPrompts.AskUnsaved;
            _savedConfirm = ClothEditPrompts.Confirm;
            // ダイアログで自動テストが止まらないよう、既定は「やめる」「いいえ」にしておく
            ClothEditPrompts.AskUnsaved = reason => ClothEditUnsavedChoice.Cancel;
            ClothEditPrompts.Confirm = (title, text, ok, cancel) => false;
            _savedSelection = Selection.objects;
            _savedStashFolder = ClothEditStash.Folder;
            _stashFolder = Path.Combine(Path.GetTempPath(), "gp02_stash_test_" + Guid.NewGuid().ToString("N"));
            ClothEditStash.Folder = _stashFolder;
        }

        [TearDown]
        public void TearDown()
        {
            // ウィンドウを先に消す（閉じるときに編集を取っておくので、衣装がまだ生きているうちに）
            foreach (ClothEditWindow window in _windows)
            {
                if (window != null) Object.DestroyImmediate(window);
            }
            _windows.Clear();
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            foreach (Object o in _created)
            {
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            ShapeKeyClothEdit.Clear();
            Selection.objects = _savedSelection;
            AssetDatabase.DeleteAsset(TempRoot);
            ClothEditSettings.DeleteSaved(SettingsPrefix);
            ClothEditStash.Folder = _savedStashFolder;
            try
            {
                if (Directory.Exists(_stashFolder)) Directory.Delete(_stashFolder, true);
            }
            catch (IOException)
            {
                // 消せなくても、テストの結果には関係ない
            }
            ClothEditPrompts.AskUnsaved = _savedAsk;
            ClothEditPrompts.Confirm = _savedConfirm;
            ClothEditView.Mode = _savedMode;
            L10n.Language = _savedLanguage;
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // 0.5m 四方の布（XY 平面、x は -0.25〜0.25、y は 1.0〜1.5。-Z 側が表）
        Renderer CreateCloth(string name = "SimplicityCloth", int columns = 10, Vector3? position = null)
        {
            ProceduralMeshes.Grid(columns, columns, 0.5f, 0.5f, out NVector3[] grid, out int[] triangles);
            var vertices = new Vector3[grid.Length];
            for (int i = 0; i < grid.Length; i++) vertices[i] = new Vector3(grid[i].X - 0.25f, grid[i].Y + 1f, 0f);
            Mesh mesh = Track(new Mesh { name = name });
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            GameObject go = Track(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            if (position.HasValue) go.transform.position = position.Value;
            return renderer;
        }

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            // 計算時間を短くして、テストを速くする
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 4f };
            var controller = new ClothEditController(settings) { SaveFolder = MeshFolder, DataFolder = DataFolder, BrushRadius = 0.04f, InfluenceRadius = 0.3f };
            _controllers.Add(controller);
            Assert.That(controller.TryStart(renderer, out string error), Is.True, error);
            return controller;
        }

        ClothEditWindow CreateWindow()
        {
            var window = ScriptableObject.CreateInstance<ClothEditWindow>();
            _windows.Add(window);
            window.Controller.SaveFolder = MeshFolder;
            window.Controller.DataFolder = DataFolder;
            window.Controller.BrushRadius = 0.04f;
            return window;
        }

        // 正面（-Z 側）から布を見るレイ
        static Ray FrontRay(float x, float y) => new Ray(new Vector3(x, y, -3f), Vector3.forward);

        void Tick(ClothEditController c)
        {
            _time += 0.016;
            c.Tick(_time);
        }

        void TickMany(ClothEditController c, int count)
        {
            for (int i = 0; i < count; i++) Tick(c);
        }

        void SettleUntilDone(ClothEditController c)
        {
            for (int i = 0; i < 1000 && c.IsBusy; i++) Tick(c);
            Assert.That(c.IsBusy, Is.False, "仕上げが終わらない");
        }

        // 押して、まっすぐドラッグして、離す。離したあとの仕上げまで進める
        void Pull(ClothEditController c, Vector3 from, Vector3 to, int steps = 6)
        {
            Assert.That(c.PointerDown(FrontRay(from.x, from.y), Vector3.forward, Vector3.up, 0f), Is.True);
            for (int k = 1; k <= steps; k++)
            {
                Vector3 p = Vector3.Lerp(from, to, k / (float)steps);
                c.PointerDrag(FrontRay(p.x, p.y));
                Tick(c);
            }
            c.PointerUp();
            SettleUntilDone(c);
        }

        static readonly Vector3 PullFrom = new Vector3(0f, 1.1f, 0f);
        static readonly Vector3 PullTo = new Vector3(0.05f, 1.1f, 0f);

        // 上の端の一番上の列をピン留めする
        static void PinTop(ClothEditController c) => c.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.05f, true);

        // 上の端を左右いっぱいにピン留めする（吊るされた布にする）
        static void PinTopEdge(ClothEditController c)
        {
            for (int k = -5; k <= 5; k++) c.Session.PaintPins(new Vector3(0.05f * k, 1.5f, 0f), 0.05f, true);
        }

        static NVector3[] Positions(ClothEditController c) => c.Session.Solver.Positions.ToArray();

        // ---- かんたん表示 / くわしく表示 ----

        [Test]
        public void ViewMode_Simple_ShowsOnlyTheMainItems()
        {
            var simple = new[]
            {
                // 布の編集ウィンドウ: 手順・対象・開始・診断の重大/注意、道具（つまむ・選んで動かす・ピン留め・元に戻す）、Undo/Redo、ブラシの大きさ、対称、選んで動かすの主な項目、保存、状態の1行
                ClothEditUiItem.Guide, ClothEditUiItem.TargetField, ClothEditUiItem.StartEditing, ClothEditUiItem.DiagnosticsSevere, ClothEditUiItem.DiagnosticsInfoLine,
                ClothEditUiItem.ToolGrab, ClothEditUiItem.ToolSelect, ClothEditUiItem.ToolPin, ClothEditUiItem.ToolRelax, ClothEditUiItem.PinEraseToggle,
                ClothEditUiItem.Undo, ClothEditUiItem.Redo, ClothEditUiItem.BrushRadius,
                ClothEditUiItem.SymmetryToggle, ClothEditUiItem.SymmetryAutoFit,
                ClothEditUiItem.SelectionHandleType, ClothEditUiItem.SelectionMoveStyle, ClothEditUiItem.SelectionSmoothness,
                ClothEditUiItem.SaveButton, ClothEditUiItem.SavedKeyList, ClothEditUiItem.StatusLine, ClothEditUiItem.ReachWarning, ClothEditUiItem.PenetrationFix,
                // 動作チェック（始める・結果の要約・順に見る・外へ逃がす）
                ClothEditUiItem.PoseCheck,
                // シェイプキーの一覧: 有効・名前・重み・複製・削除・左右分割・反転・メッシュに書き出す
                ClothEditUiItem.ShapeKeyEnabled, ClothEditUiItem.ShapeKeyName, ClothEditUiItem.ShapeKeyWeight, ClothEditUiItem.ShapeKeyDuplicate,
                ClothEditUiItem.ShapeKeyDelete, ClothEditUiItem.ShapeKeySplit, ClothEditUiItem.ShapeKeyFlip, ClothEditUiItem.ShapeKeyExport,
            };
            var detailedOnly = new[]
            {
                ClothEditUiItem.TargetAddLayers, ClothEditUiItem.DiagnosticsPanel, ClothEditUiItem.ToolRotate, ClothEditUiItem.ToolScale, ClothEditUiItem.ToolOptions,
                ClothEditUiItem.HistoryExtras, ClothEditUiItem.BrushDetails, ClothEditUiItem.SymmetryDetails, ClothEditUiItem.DisplaySection,
                ClothEditUiItem.SelectionDetails, ClothEditUiItem.AdvancedSettings, ClothEditUiItem.OverlayDisplayToggles, ClothEditUiItem.PenetrationMargin,
                ClothEditUiItem.PoseCheckDetails,
                ClothEditUiItem.ShapeKeyDataField, ClothEditUiItem.ShapeKeySearchSort, ClothEditUiItem.ShapeKeyCopyPaste, ClothEditUiItem.ShapeKeyReorder,
                ClothEditUiItem.ShapeKeyBulkEnable, ClothEditUiItem.ShapeKeyGroups, ClothEditUiItem.ShapeKeySolo, ClothEditUiItem.ShapeKeyDetails,
                ClothEditUiItem.ShapeKeyMoreOperations, ClothEditUiItem.ShapeKeyOperationSettings, ClothEditUiItem.ShapeKeyImport,
                ClothEditUiItem.ShapeKeyApplyMode, ClothEditUiItem.ShapeKeyExportDetails, ClothEditUiItem.ShapeKeyBackups,
            };
            foreach (ClothEditUiItem item in simple) Assert.That(ClothEditView.Shows(item, ClothEditViewMode.Simple), Is.True, item + " はかんたん表示でも出す");
            foreach (ClothEditUiItem item in detailedOnly) Assert.That(ClothEditView.Shows(item, ClothEditViewMode.Simple), Is.False, item + " はくわしく表示だけ");

            // すべての部品がどちらかに分類されている（足し忘れが無い）。くわしく表示は、機能を減らさず、すべて出す
            Assert.That(simple.Length + detailedOnly.Length, Is.EqualTo(Enum.GetValues(typeof(ClothEditUiItem)).Length));
            Assert.That(ClothEditView.SimpleItems.Count, Is.EqualTo(simple.Length));
            foreach (ClothEditUiItem item in Enum.GetValues(typeof(ClothEditUiItem))) Assert.That(ClothEditView.Shows(item, ClothEditViewMode.Detailed), Is.True, item.ToString());
        }

        [Test]
        public void ViewMode_DefaultsToSimple_IsSaved_AndNotifiesChanges()
        {
            EditorPrefs.DeleteKey(ClothEditView.PrefKey);
            ClothEditView.Reload();
            Assert.That(ClothEditView.Mode, Is.EqualTo(ClothEditViewMode.Simple), "既定はかんたん表示");
            Assert.That(ClothEditView.IsDetailed, Is.False);
            Assert.That(ClothEditView.Shows(ClothEditUiItem.ToolRotate), Is.False);

            int changed = 0;
            Action handler = () => changed++;
            ClothEditView.Changed += handler;
            try
            {
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                ClothEditView.Mode = ClothEditViewMode.Detailed;
            }
            finally
            {
                ClothEditView.Changed -= handler;
            }
            Assert.That(changed, Is.EqualTo(1), "変えたときだけ知らせる");
            ClothEditView.Reload();
            Assert.That(ClothEditView.Mode, Is.EqualTo(ClothEditViewMode.Detailed), "EditorPrefs に保存される");
            Assert.That(ClothEditView.Shows(ClothEditUiItem.ToolRotate), Is.True);
        }

        [Test]
        public void ViewMode_Overlay_HidesRotateScaleAndDisplayTogglesInSimple_ButKeepsTheCurrentTool()
        {
            ClothEditWindow window = CreateWindow();
            ClothEditSettings settings = window.OverlaySettings;
            ClothEditMode savedTool = settings.Tool;
            try
            {
                settings.Tool = ClothEditMode.Grab;
                var overlay = new ClothEditOverlay();
                UnityEngine.UIElements.VisualElement root = overlay.CreatePanelContent();
                var toggles = root.Query<UnityEditor.UIElements.ToolbarToggle>().ToList();
                // 並び: 道具 0〜5（回す・拡大縮小は 4・5）、ハンドル 6〜8、塗ると外す 9、対称 10・伸びの表示 11・元の形 12、ショートカット 13
                ClothEditView.Mode = ClothEditViewMode.Simple;
                overlay.Refresh();
                Assert.That(toggles[4].parent.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None), "かんたん表示では、回す・拡大縮小を出さない");
                Assert.That(toggles[11].style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None), "伸びの表示も出さない");
                Assert.That(toggles[12].style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None), "元の形も出さない");
                Assert.That(toggles[10].style.display.value, Is.Not.EqualTo(UnityEngine.UIElements.DisplayStyle.None), "対称は出す");

                // 今の道具が回す・拡大縮小なら、選ばれている印が見えるよう出す
                settings.Tool = ClothEditMode.Rotate;
                overlay.Refresh();
                Assert.That(toggles[4].parent.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));

                ClothEditView.Mode = ClothEditViewMode.Detailed;
                settings.Tool = ClothEditMode.Grab;
                overlay.Refresh();
                Assert.That(toggles[4].parent.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));
                Assert.That(toggles[11].style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));
                Assert.That(toggles[12].style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));
            }
            finally
            {
                settings.Tool = savedTool;
            }
        }

        // ---- ピン留めの修飾キー ----

        [TestCase(false, false, 1, false, PinGesture.Paint, false)]
        [TestCase(false, true, 1, false, PinGesture.Paint, true)]
        [TestCase(true, false, 1, false, PinGesture.Rectangle, false)]
        [TestCase(true, true, 1, false, PinGesture.Rectangle, true)]
        [TestCase(false, false, 2, false, PinGesture.Island, false)]
        [TestCase(false, true, 2, false, PinGesture.Island, true)]
        [TestCase(true, false, 2, false, PinGesture.Rectangle, false)]
        // 「塗ると外す」がオン: 留める・外すが逆になる（Ctrl は今のモードの反対）
        [TestCase(false, false, 1, true, PinGesture.Paint, true)]
        [TestCase(false, true, 1, true, PinGesture.Paint, false)]
        [TestCase(true, false, 1, true, PinGesture.Rectangle, true)]
        [TestCase(true, true, 1, true, PinGesture.Rectangle, false)]
        [TestCase(false, false, 2, true, PinGesture.Island, true)]
        [TestCase(false, true, 2, true, PinGesture.Island, false)]
        public void PinInput_FollowsTheSameKeysAsSelectMove(bool shift, bool control, int clicks, bool eraseMode, PinGesture gesture, bool erase)
        {
            PinInput input = ClothEditPinInput.Resolve(shift, control, clicks, eraseMode);
            Assert.That(input.Gesture, Is.EqualTo(gesture));
            Assert.That(input.Erase, Is.EqualTo(erase));
        }

        [Test]
        public void PinInput_UnpinToolCountsAsPinWithErase()
        {
            Assert.That(ClothEditPinInput.IsPinTool(ClothEditMode.Pin), Is.True);
            Assert.That(ClothEditPinInput.IsPinTool(ClothEditMode.Unpin), Is.True);
            Assert.That(ClothEditPinInput.IsPinTool(ClothEditMode.Grab), Is.False);
            Assert.That(ClothEditPinInput.IsErase(ClothEditMode.Pin, false, false), Is.False);
            Assert.That(ClothEditPinInput.IsErase(ClothEditMode.Pin, false, true), Is.True);
            Assert.That(ClothEditPinInput.IsErase(ClothEditMode.Pin, true, false), Is.True);
            Assert.That(ClothEditPinInput.IsErase(ClothEditMode.Pin, true, true), Is.False);
            Assert.That(ClothEditPinInput.IsErase(ClothEditMode.Unpin, false, false), Is.True, "昔のピン解除は、ドラッグで外す");
            Assert.That(ClothEditPinInput.IsErase(ClothEditMode.Unpin, false, true), Is.False, "Ctrl で留める");
        }

        [Test]
        public void Settings_UnpinIsLoadedAsPinWithErase_AndPinEraseIsSavedAndReset()
        {
            var legacy = new ClothEditSettings { Tool = ClothEditMode.Unpin };
            legacy.Sanitize();
            Assert.That(legacy.Tool, Is.EqualTo(ClothEditMode.Pin), "Unpin は、ピン留めとして扱う");
            Assert.That(legacy.PinEraseMode, Is.True);

            var saved = new ClothEditSettings { Tool = ClothEditMode.Unpin };
            saved.Save(SettingsPrefix);
            ClothEditSettings loaded = ClothEditSettings.Load(SettingsPrefix);
            Assert.That(loaded.Tool, Is.EqualTo(ClothEditMode.Pin), "保存値の Unpin は、ピン留めとして読む");
            Assert.That(loaded.PinEraseMode, Is.True);

            loaded.PinEraseMode = false;
            loaded.Save(SettingsPrefix);
            Assert.That(ClothEditSettings.Load(SettingsPrefix).PinEraseMode, Is.False);
            loaded.PinEraseMode = true;
            loaded.Save(SettingsPrefix);
            Assert.That(ClothEditSettings.Load(SettingsPrefix).PinEraseMode, Is.True);
            loaded.ResetToDefaults();
            Assert.That(loaded.PinEraseMode, Is.False);
        }

        [Test]
        public void Shortcuts_PAndShiftP_ChooseThePinToolWithoutAndWithErase()
        {
            ClothEditShortcuts.ResolvePinMode(ClothEditMode.Pin, true, out ClothEditMode tool, out bool erase);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Pin));
            Assert.That(erase, Is.False, "P は、塗ると外すをオフにしたピン留め");
            ClothEditShortcuts.ResolvePinMode(ClothEditMode.Unpin, false, out tool, out erase);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Pin));
            Assert.That(erase, Is.True, "Shift+P は、塗ると外すをオンにしたピン留め");
            ClothEditShortcuts.ResolvePinMode(ClothEditMode.Grab, true, out tool, out erase);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(erase, Is.True, "ほかの道具では、塗ると外すは今のまま");

            // キーの解釈（P と Shift+P）は今までどおり
            Assert.That(ClothEditShortcuts.ResolveToolKey(KeyCode.P, false, ClothEditMode.Grab, ClothEditMode.Grab, SelectionHandle.Move).Tool, Is.EqualTo(ClothEditMode.Pin));
            Assert.That(ClothEditShortcuts.ResolveToolKey(KeyCode.P, true, ClothEditMode.Grab, ClothEditMode.Grab, SelectionHandle.Move).Tool, Is.EqualTo(ClothEditMode.Unpin));
            Assert.That(ClothEditShortcutBindings.Definitions.Count(d => d.Id == ClothEditShortcutBindings.IdPinErase), Is.EqualTo(1));
            Assert.That(ClothEditHelp.ToolKey(ClothEditMode.Unpin), Does.Contain("P"));
        }

        [Test]
        public void Controller_PinStroke_CtrlRemoves_AndEraseModeInverts()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            c.Mode = ClothEditMode.Pin;

            // ドラッグで留める
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f), Is.True);
            c.PointerUp();
            Assert.That(c.Session.PinnedCount, Is.GreaterThan(0));

            // Ctrl を押したドラッグで外す
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f, true), Is.True);
            c.PointerUp();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "Ctrl+ドラッグでピンが外れる");

            // 「塗ると外す」をオン: ドラッグで外し、Ctrl+ドラッグで留める
            c.Settings.PinEraseMode = true;
            Assert.That(c.PinEraseMode, Is.True);
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f, true), Is.True);
            c.PointerUp();
            Assert.That(c.Session.PinnedCount, Is.GreaterThan(0), "塗ると外すがオンなら、Ctrl+ドラッグで留まる");
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f), Is.True);
            c.PointerUp();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "塗ると外すがオンなら、ドラッグで外れる");

            // 昔のピン解除の道具は、ピン留め＋外すとして働く
            c.Settings.PinEraseMode = false;
            c.Mode = ClothEditMode.Pin;
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f), Is.True);
            c.PointerUp();
            Assert.That(c.Session.PinnedCount, Is.GreaterThan(0));
            c.Mode = ClothEditMode.Unpin;
            Assert.That(c.PinEraseMode, Is.True);
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f), Is.True);
            c.PointerUp();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
        }

        [Test]
        public void Overlay_PinEraseToggle_AndTheLegacyUnpinTool()
        {
            ClothEditWindow window = CreateWindow();
            ClothEditSettings settings = window.OverlaySettings;
            ClothEditMode savedTool = settings.Tool;
            bool savedErase = settings.PinEraseMode;
            try
            {
                window.OverlaySetTool(ClothEditMode.Pin);
                window.OverlaySetPinErase(false);
                Assert.That(window.OverlayPinErase, Is.False);

                window.OverlaySetPinErase(true);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Pin));
                Assert.That(settings.PinEraseMode, Is.True);
                Assert.That(window.OverlayPinErase, Is.True);

                // 昔の「ピン解除」を選ぶと、「塗ると外す」をオンにしたピン留めになる
                window.OverlaySetPinErase(false);
                window.OverlaySetTool(ClothEditMode.Unpin);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Pin));
                Assert.That(settings.PinEraseMode, Is.True);

                // 保存値が Unpin のままのとき、オフにするとピン留めに直る
                settings.Tool = ClothEditMode.Unpin;
                settings.PinEraseMode = false;
                Assert.That(window.OverlayPinErase, Is.True);
                window.OverlaySetPinErase(false);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Pin));
                Assert.That(settings.PinEraseMode, Is.False);
            }
            finally
            {
                settings.Tool = savedTool;
                settings.PinEraseMode = savedErase;
                settings.Save();
            }
        }

        [Test]
        public void Help_PinTool_ExplainsTheNewKeys_InBothLanguages()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                string normal = ClothEditHelp.GetHint(ClothEditMode.Pin, false, false, 0, false);
                string erase = ClothEditHelp.GetHint(ClothEditMode.Pin, false, false, 0, true);
                Assert.That(normal, Does.Contain("Ctrl+").And.Contain("Shift+"));
                Assert.That(erase, Is.Not.EqualTo(normal), "塗ると外すがオンなら、案内が変わる");
                string tip = ClothEditHelp.ToolTip(ClothEditMode.Pin);
                Assert.That(tip, Does.Contain("Ctrl+").And.Contain("Shift+"));
                Assert.That(ClothEditHelp.ToolName(ClothEditMode.Unpin), Is.Not.EqualTo(ClothEditHelp.ToolName(ClothEditMode.Pin)));
                var lines = new List<KeyValuePair<string, string>>();
                ClothEditHelp.GetShortcutLines(lines);
                string pin = language == UiLanguage.Japanese ? "ピン" : "Pin";
                Assert.That(lines.Exists(l => l.Key.Contains("Ctrl") && l.Key.Contains(pin)), Is.True, "Ctrl+ドラッグ（ピン）");
                string erased = language == UiLanguage.Japanese ? "外す" : "Erase";
                Assert.That(lines.Exists(l => l.Key == "Shift+P" && l.Value.Contains(erased)), Is.True, "Shift+P は、ピン留め＋外す");
            }
        }

        // ---- 保存していない編集 ----

        [Test]
        public void UnsavedEdits_AreDetectedFromTheShape_AndClearedByUndoResetAndSave()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            Assert.That(c.HasUnsavedEdits, Is.False, "始めたばかりは、保存していない編集は無い");
            Assert.That(c.NeedsLeaveConfirmation, Is.False);

            PinTop(c);
            Assert.That(c.HasUnsavedEdits, Is.False, "ピンだけでは、確認は要らない");

            Pull(c, PullFrom, PullTo);
            Assert.That(c.HasUnsavedEdits, Is.True, "形を引いたら、保存していない編集がある");
            Assert.That(c.NeedsLeaveConfirmation, Is.True);

            c.UndoLast();
            Assert.That(c.HasUnsavedEdits, Is.False, "Undo で最初の形に戻れば、確認は要らない");

            c.RedoLast();
            Assert.That(c.HasUnsavedEdits, Is.True);
            c.ResetAll();
            Assert.That(c.HasUnsavedEdits, Is.False, "すべて元に戻したら、確認は要らない");

            Pull(c, PullFrom, PullTo);
            Assert.That(c.HasUnsavedEdits, Is.True);
            Assert.That(c.Save("SimplicityKey", out string message), Is.True, message);
            Assert.That(c.HasUnsavedEdits, Is.False, "保存したら、今の形が保存済みの形");
            Pull(c, new Vector3(0f, 1.2f, 0f), new Vector3(-0.05f, 1.2f, 0f));
            Assert.That(c.HasUnsavedEdits, Is.True, "保存のあとにまた引いたら、保存していない編集がある");
        }

        [Test]
        public void UnsavedEdits_NotEditing_IsFalse()
        {
            var c = new ClothEditController(new ClothEditSettings());
            _controllers.Add(c);
            Assert.That(c.HasUnsavedEdits, Is.False);
            Assert.That(c.NeedsLeaveConfirmation, Is.False);
        }

        [Test]
        public void Window_EndEditing_AsksOnlyWhenThereAreUnsavedEdits()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            int asked = 0;
            ClothEditUnsavedChoice answer = ClothEditUnsavedChoice.Cancel;
            ClothEditPrompts.AskUnsaved = reason =>
            {
                asked++;
                Assert.That(reason, Is.EqualTo(ClothEditLeaveReason.End));
                return answer;
            };

            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            ClothEditController c = window.Controller;
            Assert.That(c.IsEditing, Is.True);

            // 何も変えていなければ、聞かずに終わる
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(asked, Is.EqualTo(0));
            Assert.That(c.IsEditing, Is.False);

            // 変えたあと: やめる → 編集は残る
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            Pull(c, PullFrom, PullTo);
            answer = ClothEditUnsavedChoice.Cancel;
            Assert.That(window.RequestEnd(), Is.False);
            Assert.That(asked, Is.EqualTo(1));
            Assert.That(c.IsEditing, Is.True, "やめるを選んだら、編集を続ける");
            Assert.That(c.HasUnsavedEdits, Is.True);

            // 保存しないで終わる
            answer = ClothEditUnsavedChoice.Discard;
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(asked, Is.EqualTo(2));
            Assert.That(c.IsEditing, Is.False);
            Assert.That(cloth.GetComponent<GP02ShapeKeys>() == null, Is.True, "保存しなかった");
        }

        [Test]
        public void Window_EndEditing_SaveBeforeEnding_AddsTheShapeKey()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            ClothEditPrompts.AskUnsaved = reason => ClothEditUnsavedChoice.Save;
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            ClothEditController c = window.Controller;
            Pull(c, PullFrom, PullTo);

            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(c.IsEditing, Is.False);
            Mesh saved = ClothEditController.GetMesh(cloth);
            Assert.That(saved.blendShapeCount, Is.EqualTo(1), "保存してから終わった");
        }

        [Test]
        public void Window_SwitchingToAnotherOutfit_AsksAboutUnsavedEdits()
        {
            Renderer first = CreateCloth("FirstCloth");
            Renderer second = CreateCloth("SecondCloth");
            ClothEditWindow window = CreateWindow();
            var reasons = new List<ClothEditLeaveReason>();
            ClothEditUnsavedChoice answer = ClothEditUnsavedChoice.Cancel;
            ClothEditPrompts.AskUnsaved = reason =>
            {
                reasons.Add(reason);
                return answer;
            };

            Assert.That(window.BeginFromRenderers(new List<Renderer> { first }), Is.True);
            ClothEditController c = window.Controller;
            Assert.That(window.BeginFromRenderers(new List<Renderer> { first }), Is.True, "同じ衣装なら、そのまま");
            Assert.That(reasons, Is.Empty);

            // 何も変えていなければ、聞かずに切り替わる
            Assert.That(window.BeginFromRenderers(new List<Renderer> { second }), Is.True);
            Assert.That(reasons, Is.Empty);
            Assert.That(c.Target, Is.SameAs(second));

            Pull(c, PullFrom, PullTo);
            answer = ClothEditUnsavedChoice.Cancel;
            Assert.That(window.BeginFromRenderers(new List<Renderer> { first }), Is.False);
            Assert.That(reasons, Is.EqualTo(new[] { ClothEditLeaveReason.SwitchTarget }));
            Assert.That(c.Target, Is.SameAs(second), "やめるを選んだら、元の衣装の編集が残る");
            Assert.That(c.HasUnsavedEdits, Is.True);

            answer = ClothEditUnsavedChoice.Discard;
            Assert.That(window.BeginFromRenderers(new List<Renderer> { first }), Is.True);
            Assert.That(c.Target, Is.SameAs(first));
        }

        [Test]
        public void ShapeKeyClothEdit_SwitchingToAnotherOutfit_AsksThroughTheHook()
        {
            Renderer first = CreateCloth("HookCloth1");
            Renderer second = CreateCloth("HookCloth2");
            ClothEditController c = StartController(first);
            Pull(c, PullFrom, PullTo);
            Assert.That(c.HasUnsavedEdits, Is.True);

            Mesh mesh = ClothEditController.GetMesh(second);
            GP02ShapeKeyData data = Track(ScriptableObject.CreateInstance<GP02ShapeKeyData>());
            data.SourceMesh = mesh;
            data.VertexCount = mesh.vertexCount;
            var entry = new ShapeKeyEntry { Name = "Hooked" };
            var deltas = new Vector3[mesh.vertexCount];
            for (int v = 0; v < deltas.Length; v++) deltas[v] = new Vector3(0f, 0f, -0.02f);
            entry.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = deltas });
            data.Entries.Add(entry);
            GP02ShapeKeys component = second.gameObject.AddComponent<GP02ShapeKeys>();
            component.Data = data;
            component.SourceMesh = mesh;

            Func<ClothEditLeaveReason, bool> saved = ShapeKeyClothEdit.ConfirmLeave;
            try
            {
                var reasons = new List<ClothEditLeaveReason>();
                bool allow = false;
                ShapeKeyClothEdit.ConfirmLeave = reason =>
                {
                    reasons.Add(reason);
                    return allow;
                };
                Assert.That(ShapeKeyClothEdit.Begin(c, component, entry, out string message), Is.False);
                Assert.That(reasons, Is.EqualTo(new[] { ClothEditLeaveReason.SwitchTarget }));
                Assert.That(c.Target, Is.SameAs(first), "やめたら、元の編集が残る: " + message);
                Assert.That(c.HasUnsavedEdits, Is.True);

                allow = true;
                Assert.That(ShapeKeyClothEdit.Begin(c, component, entry, out message), Is.True, message);
                Assert.That(reasons.Count, Is.EqualTo(2));
                Assert.That(c.Target, Is.SameAs(second), "続けると決めたら、切り替わる");
            }
            finally
            {
                ShapeKeyClothEdit.ConfirmLeave = saved;
            }
        }

        // ---- 編集の取っておき ----

        [Test]
        public void Stash_Record_RoundTripsThroughAFile_AndRejectsBrokenFiles()
        {
            var record = new ClothEditStash.Record
            {
                Key = "0123456789abcdef",
                SavedTicksUtc = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc).Ticks,
                DisplayName = "Skirt、Petticoat",
                RendererIds = new[] { "GlobalObjectId_V1-2-aaaa-1-0", "" },
                RendererPaths = new[] { "|Avatar#0/Skirt#2", "|Avatar#0/Petticoat#3" },
                MeshNames = new[] { "SkirtMesh", "PetticoatMesh" },
                VertexCounts = new[] { 121, 64 },
                ParticleCount = 4,
                RestStride = 2,
                RestSamples = new[] { 0f, 1f, 2f, 3f, 4f, 5f },
                HasBody = true,
                Positions = Enumerable.Range(0, 12).Select(i => i * 0.5f).ToArray(),
                PinnedParticles = new[] { 0, 3 },
                SelectedParticles = new[] { 1, 2 },
            };
            Assert.That(ClothEditStash.Write(record), Is.True);
            string path = ClothEditStash.PathForKey(record.Key);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.Exists(path + ".tmp"), Is.False, "書き終えたら、一時ファイルは残らない");

            Assert.That(ClothEditStash.TryRead(path, false, out ClothEditStash.Record read), Is.True);
            Assert.That(read.Key, Is.EqualTo(record.Key));
            Assert.That(read.SavedTicksUtc, Is.EqualTo(record.SavedTicksUtc));
            Assert.That(read.DisplayName, Is.EqualTo(record.DisplayName));
            Assert.That(read.RendererIds, Is.EqualTo(record.RendererIds));
            Assert.That(read.RendererPaths, Is.EqualTo(record.RendererPaths));
            Assert.That(read.MeshNames, Is.EqualTo(record.MeshNames));
            Assert.That(read.VertexCounts, Is.EqualTo(record.VertexCounts));
            Assert.That(read.ParticleCount, Is.EqualTo(4));
            Assert.That(read.RestStride, Is.EqualTo(2));
            Assert.That(read.RestSamples, Is.EqualTo(record.RestSamples));
            Assert.That(read.HasBody, Is.True);
            Assert.That(read.Positions, Is.EqualTo(record.Positions));
            Assert.That(read.PinnedParticles, Is.EqualTo(record.PinnedParticles));
            Assert.That(read.SelectedParticles, Is.EqualTo(record.SelectedParticles));

            // ヘッダーだけ読むと、位置などは読まない
            Assert.That(ClothEditStash.TryRead(path, true, out ClothEditStash.Record header), Is.True);
            Assert.That(header.HasBody, Is.False);
            Assert.That(header.Positions, Is.Empty);
            Assert.That(header.DisplayName, Is.EqualTo(record.DisplayName));

            // 途中で切れたファイル・壊れたファイルは、無いものとして扱う
            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.Take(bytes.Length - 3).ToArray());
            Assert.That(ClothEditStash.TryRead(path, false, out _), Is.False, "本体が途中で切れている");
            Assert.That(ClothEditStash.TryRead(path, true, out _), Is.True, "ヘッダーは読める");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            Assert.That(ClothEditStash.TryRead(path, true, out _), Is.False);
            Assert.That(ClothEditStash.TryRead(Path.Combine(_stashFolder, "none" + ClothEditStash.FileExtension), true, out _), Is.False);
        }

        [Test]
        public void Stash_Capture_IsNullUntilThereIsSomethingToKeep()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            Assert.That(ClothEditStash.Capture(c), Is.Null, "何も変えていなければ、取っておかない");
            Assert.That(ClothEditStash.Capture(null), Is.Null);

            PinTop(c);
            ClothEditStash.Record pinsOnly = ClothEditStash.Capture(c);
            Assert.That(pinsOnly, Is.Not.Null, "ピンだけでも取っておく");
            Assert.That(pinsOnly.PinnedParticles.Length, Is.EqualTo(c.Session.PinnedCount));
            Assert.That(pinsOnly.Positions.Length, Is.EqualTo(3 * c.Session.ParticleCount));
            Assert.That(pinsOnly.ParticleCount, Is.EqualTo(c.Session.ParticleCount));
            Assert.That(pinsOnly.VertexCounts, Is.EqualTo(new[] { cloth.GetComponent<SkinnedMeshRenderer>().sharedMesh.vertexCount }));
            Assert.That(pinsOnly.DisplayName, Is.EqualTo(cloth.name));
            Assert.That(pinsOnly.RestSamples.Length, Is.GreaterThan(0).And.LessThanOrEqualTo(3 * ClothEditStash.MaxRestSamples));
        }

        [Test]
        public void Stash_Matches_OnlyTheSameOutfitWithTheSameVertexCount()
        {
            Renderer cloth = CreateCloth("MatchCloth");
            Renderer other = CreateCloth("OtherCloth");
            ClothEditController c = StartController(cloth);
            PinTop(c);
            ClothEditStash.Record record = ClothEditStash.Capture(c);
            Assert.That(record, Is.Not.Null);
            c.End();

            Assert.That(ClothEditStash.Matches(record, new[] { cloth }, out string reason), Is.True, reason);
            Assert.That(ClothEditStash.Matches(record, new[] { other }, out reason), Is.False, "別の衣装では戻さない");
            Assert.That(reason, Is.Not.Empty);
            Assert.That(ClothEditStash.Matches(record, new[] { cloth, other }, out reason), Is.False, "衣装の数が違う");
            Assert.That(ClothEditStash.Matches(record, new Renderer[0], out _), Is.False);

            // メッシュの頂点数が変わった（別のメッシュに差し替えた）
            Renderer finer = CreateCloth("MatchCloth2", 14);
            ClothEditController finerController = StartController(finer);
            PinTop(finerController);
            ClothEditStash.Record finerRecord = ClothEditStash.Capture(finerController);
            finerController.End();
            ((SkinnedMeshRenderer)finer).sharedMesh = ((SkinnedMeshRenderer)cloth).sharedMesh;
            Assert.That(ClothEditStash.Matches(finerRecord, new[] { finer }, out reason), Is.False, "頂点数が違うと戻さない");
            Assert.That(reason, Does.Contain("頂点"));
        }

        [Test]
        public void Stash_FindsOnlyTheMatchingFile_AndDeletesIt()
        {
            Renderer cloth = CreateCloth("FindCloth");
            Renderer other = CreateCloth("FindOther");
            ClothEditController c = StartController(cloth);
            PinTop(c);
            ClothEditStash.Record record = ClothEditStash.Capture(c);
            Assert.That(ClothEditStash.Write(record), Is.True);
            c.End();

            Assert.That(ClothEditStash.TryFind(new[] { cloth }, out ClothEditStash.Record found), Is.True);
            Assert.That(found.Key, Is.EqualTo(ClothEditStash.KeyFor(new[] { cloth })));
            Assert.That(found.HasBody, Is.False, "探すときはヘッダーだけ");
            Assert.That(ClothEditStash.TryFind(new[] { other }, out _), Is.False);
            Assert.That(ClothEditStash.KeyFor(new[] { cloth }), Is.Not.EqualTo(ClothEditStash.KeyFor(new[] { other })));
            Assert.That(ClothEditStash.KeyFor(new[] { cloth, other }), Is.EqualTo(ClothEditStash.KeyFor(new[] { other, cloth })), "並びが違っても同じ組");
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1));

            ClothEditStash.DeleteFor(new[] { cloth });
            Assert.That(ClothEditStash.TryFind(new[] { cloth }, out _), Is.False);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(0));
        }

        [Test]
        public void Stash_DeleteOlderThan_RemovesOnlyOldFiles()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            PinTop(c);
            ClothEditStash.Record record = ClothEditStash.Capture(c);
            Assert.That(ClothEditStash.Write(record), Is.True);
            string path = ClothEditStash.PathForKey(record.Key);
            Assert.That(ClothEditStash.DeleteOlderThan(TimeSpan.FromDays(1)), Is.EqualTo(0));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(10));
            Assert.That(ClothEditStash.DeleteOlderThan(ClothEditStash.MaxAge), Is.EqualTo(1));
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void Stash_ParticlePlacement_IsTheSameForTheSameInput_AndDiffersWhenTheOutfitMoves()
        {
            Renderer cloth = CreateCloth();
            ClothEditController first = StartController(cloth);
            NVector3[] rest = (NVector3[])first.Session.Cloth.Model.RestPositions.Clone();
            first.End();
            ClothEditController second = StartController(cloth);
            NVector3[] again = second.Session.Cloth.Model.RestPositions;
            Assert.That(again.Length, Is.EqualTo(rest.Length), "粒子の数が同じ");
            for (int i = 0; i < rest.Length; i++) Assert.That(again[i], Is.EqualTo(rest[i]), "粒子 " + i);

            // 衣装を 5cm 動かすと、元の位置が合わなくなる
            PinTop(second);
            ClothEditStash.Record record = ClothEditStash.Capture(second);
            Assert.That(ClothEditStash.MatchesSession(record, second.Session, out string sameReason), Is.True, sameReason);
            second.End();
            cloth.transform.position += new Vector3(0.05f, 0f, 0f);
            ClothEditController moved = StartController(cloth);
            Assert.That(ClothEditStash.MatchesSession(record, moved.Session, out string reason), Is.False, "5cm 動いたら戻さない");
            Assert.That(reason, Is.Not.Empty);
        }

        [Test]
        public void Stash_Restore_PutsBackPositionsPinsAndSelection_InOneUndoStep()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c1 = StartController(cloth);
            PinTop(c1);
            Pull(c1, PullFrom, new Vector3(0.08f, 1.1f, 0f));
            Assert.That(c1.SelectPaintBegin(FrontRay(0f, 1.3f), false), Is.True);
            c1.SelectPaintEnd();
            Assert.That(c1.SelectionCount, Is.GreaterThan(0));

            NVector3[] before = Positions(c1);
            var pinnedBefore = new List<int>();
            c1.Session.GetPinnedParticles(pinnedBefore);
            int[] selectedBefore = c1.SelectedParticles.ToArray();
            ClothEditStash.Record record = ClothEditStash.Capture(c1);
            Assert.That(record, Is.Not.Null);
            Assert.That(ClothEditStash.Write(record), Is.True);
            c1.End();

            // 編集を始め直して戻す（コンパイルやプレイモードのあと、ウィンドウを開き直したときと同じ）
            Assert.That(ClothEditStash.TryRead(ClothEditStash.PathForKey(record.Key), false, out ClothEditStash.Record read), Is.True);
            ClothEditController c2 = StartController(cloth);
            NVector3[] rest = Positions(c2);
            Assert.That(c2.HasUnsavedEdits, Is.False);
            Assert.That(ClothEditStash.MatchesSession(read, c2.Session, out string reason), Is.True, reason);
            Assert.That(c2.RestoreEditState(read.Positions, read.PinnedParticles, read.SelectedParticles), Is.True);

            NVector3[] after = Positions(c2);
            Assert.That(after.Length, Is.EqualTo(before.Length));
            for (int i = 0; i < before.Length; i++) Assert.That(NVector3.Distance(after[i], before[i]), Is.LessThan(1e-6f), "粒子 " + i);
            var pinnedAfter = new List<int>();
            c2.Session.GetPinnedParticles(pinnedAfter);
            Assert.That(pinnedAfter, Is.EqualTo(pinnedBefore), "ピンが戻る");
            Assert.That(c2.SelectedParticles.ToArray(), Is.EqualTo(selectedBefore), "選んだ点が戻る");
            Assert.That(c2.HasUnsavedEdits, Is.True, "戻した編集は、まだ保存していない");

            // Ctrl+Z で、戻す前の形に戻る
            c2.UndoLast();
            NVector3[] undone = Positions(c2);
            for (int i = 0; i < rest.Length; i++) Assert.That(NVector3.Distance(undone[i], rest[i]), Is.LessThan(1e-6f), "Undo 後の粒子 " + i);
            Assert.That(c2.HasUnsavedEdits, Is.False);
        }

        [Test]
        public void Stash_Restore_ReproducesTheStretchOfTheStashedShape()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c1 = StartController(cloth);
            PinTop(c1);
            Pull(c1, new Vector3(0f, 1.1f, 0f), new Vector3(0.12f, 1.1f, 0f));
            float stretch = c1.Session.Solver.MeasureMaxStretch();
            ClothEditStash.Record record = ClothEditStash.Capture(c1);
            c1.End();

            ClothEditController c2 = StartController(cloth);
            Assert.That(ClothEditStash.MatchesSession(record, c2.Session, out string reason), Is.True, reason);
            Assert.That(c2.RestoreEditState(record.Positions, record.PinnedParticles, record.SelectedParticles), Is.True);
            Assert.That(c2.Session.Solver.MeasureMaxStretch(), Is.EqualTo(stretch).Within(1e-5f), "位置のほかに、取っておく状態は無い");
            Assert.That(c2.LastStretch, Is.EqualTo(stretch).Within(1e-5f), "画面に出す伸びも同じ");
        }

        [Test]
        public void Stash_Restore_RefusesTheWrongShapeOrCount()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            PinTop(c);
            int pins = c.Session.PinnedCount;
            Assert.That(c.RestoreEditState(new float[3], new int[0], new int[0]), Is.False, "粒子の数が合わなければ戻さない");
            Assert.That(c.RestoreEditState(null, null, null), Is.False);
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pins), "戻せなかったときは、今の状態のまま");
        }

        [Test]
        public void Stash_DescribeAge_ReadsAsMinutesHoursDays()
        {
            Assert.That(ClothEditStash.DescribeAge(TimeSpan.FromSeconds(10)), Is.EqualTo("たった今"));
            Assert.That(ClothEditStash.DescribeAge(TimeSpan.FromMinutes(3.4)), Is.EqualTo("3分前"));
            Assert.That(ClothEditStash.DescribeAge(TimeSpan.FromMinutes(59)), Is.EqualTo("59分前"));
            Assert.That(ClothEditStash.DescribeAge(TimeSpan.FromHours(2.5)), Is.EqualTo("2時間前"));
            Assert.That(ClothEditStash.DescribeAge(TimeSpan.FromDays(3)), Is.EqualTo("3日前"));
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditStash.DescribeAge(TimeSpan.FromMinutes(3)), Is.EqualTo("3 min ago"));

            var record = new ClothEditStash.Record { DisplayName = "Skirt", SavedTicksUtc = DateTime.UtcNow.AddMinutes(-3).Ticks };
            Assert.That(ClothEditStash.Describe(record, DateTime.UtcNow), Is.EqualTo("Skirt (3 min ago)"));
        }

        // ---- ウィンドウの取っておきと帯 ----

        [Test]
        public void Window_StashesOnDemand_ShowsTheBand_RestoresAndDiscards()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            ClothEditController c = window.Controller;
            Assert.That(window.HasStashBand, Is.False, "編集中は帯を出さない");
            PinTop(c);
            Pull(c, PullFrom, new Vector3(0.06f, 1.1f, 0f));
            NVector3[] before = Positions(c);

            window.StashNow();
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1));
            // 同じ状態のまま、もう一度は書かない（書き直すと更新時刻が変わる）
            string path = Directory.GetFiles(_stashFolder, "*" + ClothEditStash.FileExtension)[0];
            File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            window.StashNow();
            Assert.That(File.GetLastWriteTimeUtc(path).Year, Is.EqualTo(2020), "状態が変わっていなければ書き直さない");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            // 編集を終えて（コンパイルやプレイモードで消えたのと同じ）、帯が出る
            c.End();
            window.RefreshStashInfo(true);
            Assert.That(window.HasStashBand, Is.True);
            Assert.That(window.StashInfo.DisplayName, Is.EqualTo(cloth.name));

            Assert.That(window.RestoreFromStash(), Is.True, window.StashMessage);
            Assert.That(c.IsEditing, Is.True);
            Assert.That(window.HasStashBand, Is.False, "戻したら帯は消える");
            NVector3[] after = Positions(c);
            Assert.That(after.Length, Is.EqualTo(before.Length));
            for (int i = 0; i < before.Length; i++) Assert.That(NVector3.Distance(after[i], before[i]), Is.LessThan(1e-6f));
            Assert.That(c.Session.PinnedCount, Is.GreaterThan(0));

            // 終えて、捨てる
            c.End();
            window.RefreshStashInfo(true);
            Assert.That(window.HasStashBand, Is.True, "戻したあとも、保存するか捨てるまでは取っておきを残す");
            window.DiscardStash();
            Assert.That(window.HasStashBand, Is.False);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(0));
        }

        [Test]
        public void Window_Band_IsNotShownForAnotherOutfit_AndRestoreRefusesAMovedOutfit()
        {
            Renderer cloth = CreateCloth("BandCloth");
            Renderer other = CreateCloth("BandOther");
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            PinTop(window.Controller);
            window.StashNow();
            window.Controller.End();

            // 別の衣装を対象にすると、前の取っておきは出ない
            Assert.That(window.BeginFromRenderers(new List<Renderer> { other }), Is.True);
            window.Controller.End();
            window.RefreshStashInfo(true);
            Assert.That(window.HasStashBand, Is.False);

            // 衣装を動かしてから戻そうとすると、戻さずに理由を知らせる（取っておきは残る）
            cloth.transform.position += new Vector3(0.08f, 0f, 0f);
            window.SetTargets(new List<Renderer> { cloth });
            Assert.That(window.HasStashBand, Is.True);
            Assert.That(window.RestoreFromStash(), Is.False);
            Assert.That(window.Controller.IsEditing, Is.False, "戻せなかったときは、始めた編集も終える");
            Assert.That(window.HasStashBand, Is.True, "取っておきは残る");
            Assert.That(window.StashMessage, Is.Not.Empty);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1));
        }

        [Test]
        public void Window_Save_DeletesTheStash()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            Pull(window.Controller, PullFrom, PullTo);
            window.StashNow();
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1));
            ClothEditPrompts.AskUnsaved = reason => ClothEditUnsavedChoice.Save;
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(0), "保存したら、取っておきは消す");
        }

        [Test]
        public void Window_Discarding_DeletesTheStash()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            Pull(window.Controller, PullFrom, PullTo);
            window.StashNow();
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1));
            ClothEditPrompts.AskUnsaved = reason => ClothEditUnsavedChoice.Discard;
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(0), "保存しないで終わると決めたら、取っておきも要らない");
        }

        [Test]
        public void Window_StashesBeforeScriptReload_AndKeepsEditing()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            ClothEditController c = window.Controller;
            Pull(c, PullFrom, PullTo);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(0));

            typeof(ClothEditWindow).GetMethod("OnBeforeAssemblyReload", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1), "スクリプトの再読み込みの前に取っておく");
            Assert.That(c.IsEditing, Is.True, "取っておいても、編集は続く");
        }

        [Test]
        public void Window_StashesBeforePlayMode_AndShowsTheBandWhenBack()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            ClothEditController c = window.Controller;
            Pull(c, PullFrom, PullTo);

            MethodInfo changed = typeof(ClothEditWindow).GetMethod("OnPlayModeStateChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            changed.Invoke(window, new object[] { PlayModeStateChange.ExitingEditMode });
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1), "プレイモードに入る前に取っておく");
            Assert.That(c.IsEditing, Is.False, "プレイモードに入る前に、編集を終える");

            changed.Invoke(window, new object[] { PlayModeStateChange.EnteredEditMode });
            Assert.That(window.HasStashBand, Is.True, "プレイモードから戻ったら、前回の編集の帯が出る");
            Assert.That(window.RestoreFromStash(), Is.True, window.StashMessage);
            Assert.That(c.HasUnsavedEdits, Is.True);
        }

        [Test]
        public void Window_StashesWhenTheWindowCloses()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            Pull(window.Controller, PullFrom, PullTo);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(0));

            _windows.Remove(window);
            Object.DestroyImmediate(window);
            Assert.That(ClothEditStash.Count(), Is.EqualTo(1), "ウィンドウを閉じるときにも取っておく");
            Assert.That(ClothEditStash.TryFind(new[] { cloth }, out _), Is.True);
        }

        [Test]
        public void Window_RightClickStart_WithAStash_ShowsTheBandInsteadOfOverwriting()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            PinTop(window.Controller);
            window.StashNow();
            window.Controller.End();

            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.False, "前回の編集が残っているときは、黙って始めない");
            Assert.That(window.Controller.IsEditing, Is.False);
            Assert.That(window.HasStashBand, Is.True);
        }

        // ---- 右クリックから始める ----

        [Test]
        public void StartMenu_FindsOnlyEditableClothInTheScene()
        {
            Renderer cloth = CreateCloth("MenuCloth");
            Renderer second = CreateCloth("MenuCloth2");
            GameObject plain = Track(new GameObject("MenuPlain"));
            GameObject empty = Track(new GameObject("MenuEmptyRenderer"));
            empty.AddComponent<MeshRenderer>();   // MeshFilter が無い MeshRenderer は編集できない

            List<Renderer> found = ClothEditStartMenu.FindCloth(new[] { cloth.gameObject, plain, null, second.gameObject, cloth.gameObject, empty });
            Assert.That(found, Is.EqualTo(new[] { cloth, second }), "選んだ順で、同じものは1つ");
            Assert.That(ClothEditStartMenu.CanStart(new[] { cloth.gameObject }), Is.True);
            Assert.That(ClothEditStartMenu.CanStart(new[] { plain, empty }), Is.False, "布が無ければ、メニューは使えない");
            Assert.That(ClothEditStartMenu.CanStart(new GameObject[0]), Is.False);
            Assert.That(ClothEditStartMenu.CanStart(null), Is.False);

            // MeshFilter 付きの MeshRenderer も布として使える
            GameObject filterObject = Track(new GameObject("MenuFilter"));
            filterObject.AddComponent<MeshFilter>().sharedMesh = ((SkinnedMeshRenderer)cloth).sharedMesh;
            filterObject.AddComponent<MeshRenderer>();
            Assert.That(ClothEditStartMenu.CanStart(new[] { filterObject }), Is.True);
        }

        [Test]
        public void StartMenu_PrefabAssetsAreNotEditable()
        {
            Renderer cloth = CreateCloth("PrefabCloth");
            if (!AssetDatabase.IsValidFolder(TempRoot)) AssetDatabase.CreateFolder("Assets", "GP02_SimplicityTemp");
            // メッシュを持たないプレハブ（アセット）。シーンのオブジェクトではないので、布として数えない
            GameObject source = Track(new GameObject("PrefabOnly"));
            source.AddComponent<SkinnedMeshRenderer>();
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, TempRoot + "/PrefabOnly.prefab");
            Assert.That(prefab, Is.Not.Null);
            Assert.That(ClothEditController.FindRenderer(prefab), Is.Not.Null, "プレハブにもレンダラーはある");
            Assert.That(ClothEditStartMenu.CanStart(new[] { prefab }), Is.False, "プロジェクトのプレハブそのものは対象にしない");
            Assert.That(ClothEditStartMenu.CanStart(new[] { cloth.gameObject }), Is.True, "シーンに置いたものは対象");
        }

        [Test]
        public void StartMenu_ValidateFollowsTheSelection()
        {
            Renderer cloth = CreateCloth("ValidateCloth");
            GameObject plain = Track(new GameObject("ValidatePlain"));
            MethodInfo validate = typeof(ClothEditStartMenu).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);

            Selection.objects = new Object[] { cloth.gameObject };
            Assert.That((bool)validate.Invoke(null, null), Is.True, "布を選んでいれば使える");
            Selection.objects = new Object[] { plain };
            Assert.That((bool)validate.Invoke(null, null), Is.False, "布の無いものだけなら使えない");
            Selection.objects = new Object[0];
            Assert.That((bool)validate.Invoke(null, null), Is.False, "何も選んでいなければ使えない");
            Assert.That(ClothEditStartMenu.MenuPath, Does.StartWith("GameObject/"), "ヒエラルキーの右クリックに出る");
        }

        [Test]
        public void Window_StartFromTheMenu_StartsAtOnce_AndStacksSeveralTogether()
        {
            Renderer a = CreateCloth("LayerA");
            Renderer b = CreateCloth("LayerB", 10, new Vector3(0f, 0f, 0.02f));
            ClothEditWindow window = CreateWindow();

            Assert.That(window.BeginFromRenderers(ClothEditStartMenu.FindCloth(new[] { a.gameObject, b.gameObject })), Is.True);
            ClothEditController c = window.Controller;
            Assert.That(c.IsEditing, Is.True, "1回で編集が始まる");
            Assert.That(c.Session.RendererCount, Is.EqualTo(2), "複数選んでいれば、重ね着としてまとめて");
            Assert.That(c.Targets, Is.EquivalentTo(new[] { a, b }));
            window.Controller.End();

            Assert.That(window.BeginFromRenderers(ClothEditStartMenu.FindCloth(new[] { b.gameObject })), Is.True);
            Assert.That(c.Session.RendererCount, Is.EqualTo(1));
            Assert.That(c.Target, Is.SameAs(b));
            Assert.That(window.BeginFromRenderers(new List<Renderer>()), Is.False);
        }

        [Test]
        public void Window_FollowsTheHierarchySelection_OnlyWhileNotEditing()
        {
            Renderer a = CreateCloth("FollowA");
            Renderer b = CreateCloth("FollowB");
            GameObject plain = Track(new GameObject("FollowPlain"));
            ClothEditWindow window = CreateWindow();

            Selection.objects = new Object[] { a.gameObject };
            window.TrySetTargetsFromSelection();
            Assert.That(window.Targets, Is.EqualTo(new[] { a }), "選んだ衣装が対象に入る");
            Assert.That(window.TrySetTargetsFromSelection(), Is.False, "同じ選択では変わらない");

            Selection.objects = new Object[] { plain };
            Assert.That(window.TrySetTargetsFromSelection(), Is.False, "布の無いものを選んでも、対象は変えない");
            Assert.That(window.Targets, Is.EqualTo(new[] { a }));

            Selection.objects = new Object[] { a.gameObject, b.gameObject };
            window.TrySetTargetsFromSelection();
            Assert.That(window.Targets, Is.EquivalentTo(new[] { a, b }), "複数なら重ね着としてまとめる");

            // 編集中は、選択が変わっても対象を変えない
            Selection.objects = new Object[] { a.gameObject };
            window.TrySetTargetsFromSelection();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { a }), Is.True);
            Selection.objects = new Object[] { b.gameObject };
            Assert.That(window.TrySetTargetsFromSelection(), Is.False, "編集中は変えない");
            Assert.That(window.Controller.Target, Is.SameAs(a));
            Assert.That(window.Targets, Is.EqualTo(new[] { a }));
        }

        // ---- 届かないときの見える化 ----

        [Test]
        public void Reach_Thresholds_NeedBothDistanceAndRatio()
        {
            Assert.That(ClothEditReach.IsOutOfReach(0.005f, 0.01f), Is.False, "0.5cm は届いている");
            Assert.That(ClothEditReach.IsOutOfReach(0.01f, 0.30f), Is.False, "1cm でも、引いた量（30cm）の2割に満たない");
            Assert.That(ClothEditReach.IsOutOfReach(0.02f, 0.30f), Is.False);
            Assert.That(ClothEditReach.IsOutOfReach(0.07f, 0.30f), Is.True, "7cm は引いた量（30cm）の2割以上");
            Assert.That(ClothEditReach.IsOutOfReach(0.01f, 0.04f), Is.True, "1cm かつ引いた量の2割以上");
            Assert.That(ClothEditReach.IsOutOfReach(0.009f, 0.01f), Is.False, "1cm に満たなければ、割合が大きくても出さない");
            Assert.That(ClothEditReach.IsOutOfReach(0.5f, 0.5f), Is.True);
            Assert.That(ClothEditReach.Message(), Does.Contain("布の長さが足りません").And.Contain("形だけ動かす").And.Contain("伸びやすさ"));
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditReach.Message(), Does.Contain("not long enough"));
        }

        [Test]
        public void Reach_Grab_FollowsTheHandWhenReachable_AndFallsBehindWhenNot()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            Assert.That(c.IsReachTracking, Is.False);
            Assert.That(c.ReachDistance, Is.EqualTo(0f));
            Assert.That(c.IsOutOfReach, Is.False);

            // 上の端を留めて、真ん中をつまむ。視線に沿って動かす（上の端を軸に回るだけで、布の長さは変わらない）
            c.Settings.MoveDirection = MoveDirection.View;
            PinTopEdge(c);
            Assert.That(c.PointerDown(FrontRay(0f, 1.1f), Vector3.forward, Vector3.up, 0f), Is.True);
            Assert.That(c.IsReachTracking, Is.True);

            // 3cm なら、布の長さの内で、手に付いてくる
            for (int k = 1; k <= 6; k++)
            {
                c.PointerDrag(FrontRay(0f, 1.1f + 0.03f * k / 6f));
                Tick(c);
            }
            // 手に付いてくるまでの時間は、計算の速さ（Burst の有無）で変わるので、付いてくるまで進める
            for (int i = 0; i < 400 && c.ReachDistance >= ClothEditReach.WarnDistance; i++) Tick(c);
            Assert.That(c.ReachPulled, Is.EqualTo(0.03f).Within(0.002f), "引いた量");
            Assert.That(c.ReachDistance, Is.LessThan(ClothEditReach.WarnDistance), "届くところなら、手に付いてくる: " + c.ReachDistance);
            Assert.That(c.IsOutOfReach, Is.False);

            // 布の長さ（0.35m）で届かない所まで、75cm 引く
            for (int k = 1; k <= 15; k++)
            {
                c.PointerDrag(FrontRay(0f, 1.1f + 0.75f * k / 15f));
                Tick(c);
            }
            TickMany(c, 60);
            Assert.That(c.ReachPulled, Is.EqualTo(0.75f).Within(0.01f));
            Assert.That(c.ReachDistance, Is.GreaterThan(0.1f), "布が止まって、引いている先との距離が開く: " + c.ReachDistance);
            Assert.That(c.IsOutOfReach, Is.True);
            Assert.That(Vector3.Distance(c.ReachTarget, c.ReachCurrent), Is.EqualTo(c.ReachDistance).Within(1e-5f));
            Assert.That(c.ReachTarget.z, Is.GreaterThan(c.ReachCurrent.z + 0.1f), "引いている先のほうが、手前で止まった点より遠い");

            // 離して仕上げが終わったら、測るのをやめる
            c.PointerUp();
            SettleUntilDone(c);
            Assert.That(c.IsReachTracking, Is.False);
            Assert.That(c.ReachDistance, Is.EqualTo(0f));
            Assert.That(c.IsOutOfReach, Is.False);
        }

        [Test]
        public void Reach_RotateAndScaleTools_AreNotMeasured()
        {
            Renderer cloth = CreateCloth();
            ClothEditController c = StartController(cloth);
            c.Mode = ClothEditMode.Rotate;
            Assert.That(c.PointerDown(FrontRay(0f, 1.25f), Vector3.forward, Vector3.up, 0f), Is.True);
            c.PointerDrag(FrontRay(0f, 1.25f), 40f, false, false);
            Tick(c);
            Assert.That(c.IsReachTracking, Is.False, "回す・拡大縮小は対象にしない");
            c.PointerUp();
            SettleUntilDone(c);
        }

        [Test]
        public void Reach_SelectMove_MeasuresTheSelectionCenter_AndShapeOnlyAlwaysReaches()
        {
            Renderer cloth = CreateCloth();
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, SelectionFalloffRadius = 0f };
            ClothEditController c = StartController(cloth, settings);
            PinTopEdge(c);
            c.BrushRadius = 0.06f;
            Assert.That(c.SelectPaintBegin(FrontRay(0f, 1.05f), false), Is.True);
            c.SelectPaintEnd();
            Assert.That(c.SelectionCount, Is.GreaterThan(0));

            // 布として動かす: 届かない所へ 75cm
            Assert.That(c.BeginSelectionTransform(), Is.True);
            Assert.That(c.IsReachTracking, Is.True);
            for (int k = 1; k <= 12; k++)
            {
                c.UpdateSelectionTransform(new Vector3(0f, -0.75f * k / 12f, 0f), Quaternion.identity, Vector3.one, false);
                Tick(c);
            }
            TickMany(c, 60);
            Assert.That(c.ReachPulled, Is.EqualTo(0.75f).Within(0.01f));
            Assert.That(c.IsOutOfReach, Is.True, "選んだ点の中心が、引いている先に届かない: " + c.ReachDistance);
            c.EndSelectionTransform();
            SettleUntilDone(c);
            Assert.That(c.IsReachTracking, Is.False);

            // 形だけ動かす: 指定どおりの位置へ置くので、届く
            c.ResetAll();
            c.Settings.SelectionMovesAsCloth = false;
            Assert.That(c.BeginSelectionTransform(), Is.True);
            c.UpdateSelectionTransform(new Vector3(0f, -0.75f, 0f), Quaternion.identity, Vector3.one, false);
            Tick(c);
            Assert.That(c.ReachDistance, Is.LessThan(ClothEditReach.WarnDistance), "形だけ動かすなら、届いている: " + c.ReachDistance);
            Assert.That(c.IsOutOfReach, Is.False);
            c.EndSelectionTransform();
            SettleUntilDone(c);
            Assert.That(c.IsReachTracking, Is.False);
        }

        // ---- 保存したシェイプキーの簡単な一覧 ----

        [Test]
        public void SavedKeys_ListTheSavedEntries_ShowAKeyGhost_AndDeleteWithConfirmation()
        {
            Renderer cloth = CreateCloth();
            ClothEditWindow window = CreateWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { cloth }), Is.True);
            ClothEditController c = window.Controller;
            Assert.That(window.FindSavedKeyData(out _), Is.Null, "保存するまでデータは無い");

            Pull(c, PullFrom, new Vector3(0.06f, 1.1f, 0f));
            Assert.That(c.Save("ListedKey", out string message), Is.True, message);
            GP02ShapeKeyData data = window.FindSavedKeyData(out GP02ShapeKeys component);
            Assert.That(data, Is.Not.Null);
            Assert.That(component, Is.Not.Null);
            Assert.That(data.Entries.Select(e => e.Name), Is.EqualTo(new[] { "ListedKey" }));

            // 重みを動かすと、編集中の形に保存した形を重ねる（データの重みは触らない）
            ShapeKeyEntry entry = data.Entries[0];
            Assert.That(c.Session.HasKeyGhost, Is.False);
            window.SetKeyWeight(data, entry, 50f);
            Assert.That(c.Session.HasKeyGhost, Is.True);
            Assert.That(entry.PreviewWeight, Is.EqualTo(0f), "データの「プレビューの重み」は変えない");
            Vector3[] ghost = c.Session.KeyGhostVertices;
            Vector3[] source = data.SourceMesh.vertices;
            ShapeKeyFrame frame = ShapeKeyLibrary.EvaluateEntry(entry, 50f, source.Length);
            Assert.That(ghost.Length, Is.EqualTo(source.Length));
            int moved = 0;
            for (int v = 0; v < source.Length; v++)
            {
                Vector3 expected = c.Session.GetVertexMatrix(v).MultiplyPoint3x4(source[v] + frame.DeltaVertices[v]);
                Assert.That(Vector3.Distance(ghost[v], expected), Is.LessThan(1e-5f), "頂点 " + v);
                if (frame.DeltaVertices[v].sqrMagnitude > 1e-8f) moved++;
            }
            Assert.That(moved, Is.GreaterThan(0), "保存した形に動いた頂点がある");
            window.SetKeyWeight(data, entry, 0f);
            Assert.That(c.Session.HasKeyGhost, Is.False, "重みが 0 に戻れば、重ね描きは消える");

            // 削除: 確認で「やめる」なら残り、「削除する」なら消える
            window.SetKeyWeight(data, entry, 30f);
            ClothEditPrompts.Confirm = (title, text, ok, cancel) => false;
            Assert.That(window.DeleteSavedKey(data, entry), Is.False);
            Assert.That(data.Entries.Count, Is.EqualTo(1));
            Assert.That(c.Session.HasKeyGhost, Is.True);
            ClothEditPrompts.Confirm = (title, text, ok, cancel) => true;
            Assert.That(window.DeleteSavedKey(data, entry), Is.True);
            Assert.That(data.Entries.Count, Is.EqualTo(0));
            Assert.That(c.Session.HasKeyGhost, Is.False, "消したシェイプキーの重ね描きも消える");
        }

        // ---- Burst の初回案内 ----

        [Test]
        public void BurstGuide_IsShownOnlyOnce_AndOnlyWithoutBurst()
        {
            Assert.That(ClothEditWindow.ShouldShowBurstGuide(false, false), Is.True, "Burst が無く、初めてなら出す");
            Assert.That(ClothEditWindow.ShouldShowBurstGuide(false, true), Is.False, "出したことがあれば、もう出さない");
            Assert.That(ClothEditWindow.ShouldShowBurstGuide(true, false), Is.False, "Burst が入っていれば出さない");
            Assert.That(ClothEditWindow.ShouldShowBurstGuide(true, true), Is.False);
            Assert.That(BurstInstaller.GuideShownPrefKey, Is.Not.Empty);
        }
    }
}
