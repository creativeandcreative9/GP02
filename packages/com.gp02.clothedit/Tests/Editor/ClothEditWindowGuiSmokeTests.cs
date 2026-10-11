using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using NVector3 = System.Numerics.Vector3;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 編集ウィンドウ（編集前の手引き、編集中）とシーンビューの道具バーを実際に描画して、例外が出ないことを確かめ、画像にする。
    /// 描画には画面が要るので、通常のテスト（-nographics）では飛ばす。
    /// <c>GP02_GUI=1 GP02_UNITY_ARGS="-testFilter GP02.Tests.ClothEditWindowGuiSmokeTests" tools/unity/run-tests.sh</c>
    /// 画像は tools/unity/out/gui/ に出る。
    /// </summary>
    public partial class ClothEditWindowGuiSmokeTests
    {
        const string ShotFolder = "/repo/tools/unity/out/gui";
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        readonly List<Object> _created = new List<Object>();
        UiLanguage _savedLanguage;
        ClothEditViewMode _savedViewMode;
        bool _viewModeSaved;

        [SetUp]
        public void RequireAScreen()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("画面が無い環境（-nographics）では描画できません。GP02_GUI=1 で仮想画面の上で動かしてください。");
            }
            // Docker の Unity には日本語のフォントが無く、日本語の文字が空白になる。画像で読めるように英語で描く
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.English;
            // 表示のしかたは、既定（かんたん）から始める。くわしく表示を見るテストは、自分で切り替える
            _savedViewMode = ClothEditView.Mode;
            _viewModeSaved = true;
            ClothEditView.Mode = ClothEditViewMode.Simple;
        }

        [TearDown]
        public void Cleanup()
        {
            L10n.Language = _savedLanguage;
            if (_viewModeSaved) ClothEditView.Mode = _savedViewMode;
            _viewModeSaved = false;
            foreach (Object o in _created)
            {
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        static IEnumerator Pump(int frames = 6)
        {
            for (int i = 0; i < frames; i++)
            {
                InternalEditorUtility.RepaintAllViews();
                yield return null;
            }
        }

        static void Shot(EditorWindow window, string name)
        {
            if (!Directory.Exists("/repo/tools/unity/out")) return;
            Directory.CreateDirectory(ShotFolder);
            Rect rect = window.position;
            // 内容は、ウィンドウの上のタブの分だけ下にずれる。その分も写す（画面の外には出ない）
            int height = Mathf.Min((int)rect.height + 24, Screen.currentResolution.height - (int)rect.y);
            Color[] pixels = InternalEditorUtility.ReadScreenPixel(rect.position, (int)rect.width, height);
            var texture = new Texture2D((int)rect.width, height, TextureFormat.RGB24, false);
            texture.SetPixels(pixels);
            texture.Apply();
            File.WriteAllBytes(Path.Combine(ShotFolder, name + ".png"), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        // 5cm 間隔の頂点を持つ 1m 四方の布（XY 平面、x=0 で左右対称、-Z 向きが表）
        Renderer CreateCloth()
        {
            ProceduralMeshes.Grid(20, 20, 1f, 1f, out NVector3[] grid, out int[] gridTriangles);
            var vertices = new Vector3[grid.Length];
            for (int i = 0; i < grid.Length; i++) vertices[i] = new Vector3(grid[i].X - 0.5f, grid[i].Y + 0.5f, 0f);
            Mesh mesh = Track(new Mesh { name = "GuiCloth" });
            mesh.vertices = vertices;
            mesh.triangles = gridTriangles;
            mesh.RecalculateNormals();
            GameObject go = Track(new GameObject("GuiCloth"));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.8f, 0.5f, 0.6f) };
            return renderer;
        }

        static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(target);
        }

        [UnityTest]
        public IEnumerator Window_DrawsTheGuideBeforeEditing_AndWhileEditing_WithoutErrors()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 900);
            Renderer cloth = CreateCloth();
            try
            {
                // 1. 何も選んでいない: 手順 1 が「今やること」
                yield return Pump();
                Shot(window, "edit_window_guide_step1");

                // 2. 衣装を選んだ: 手順 2
                SetField(window, "_target", cloth);
                yield return Pump();
                Shot(window, "edit_window_guide_step2");

                // 3. 編集を始めた: 手順 3（ピン留め）
                MethodInfo start = typeof(ClothEditWindow).GetMethod("StartEdit", Private);
                start.Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                SetField(window, "_foldGuide", true);
                yield return Pump();
                Shot(window, "edit_window_editing_step3");

                // 4. ピンを留めた: 手順 4
                controller.Session.PaintPins(new Vector3(0f, 1f, 0f), 0.1f, true);
                yield return Pump();
                Shot(window, "edit_window_editing_step4");

                // 5. 引いた: 手順 5。範囲指定のピンの道具も見る
                var ray = new Ray(new Vector3(0.2f, 0.5f, -3f), Vector3.forward);
                Assert.That(controller.PointerDown(ray, Vector3.forward, 0f), Is.True);
                controller.PointerDrag(new Ray(new Vector3(0.2f, 0.7f, -3f), Vector3.forward), 0f, false, false);
                controller.Tick(EditorApplication.timeSinceStartup);
                controller.PointerUp();
                for (int i = 0; i < 400 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 10 + i * 0.016);
                controller.Settings.Tool = ClothEditMode.Pin;
                yield return Pump();
                Shot(window, "edit_window_editing_step5");

                controller.End();
                yield return Pump();
                Shot(window, "edit_window_after_end");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SceneView_ShowsTheToolBarOverlay_WhileEditing()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 700);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            Renderer cloth = CreateCloth();
            try
            {
                yield return Pump(10);
                Shot(sceneView, "scene_overlay_not_editing");

                SetField(window, "_target", cloth);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                Assert.That(ClothEditWindow.IsEditingNow, Is.True);

                // Overlay が登録されていて、編集中は「出す」状態になる
                Assert.That(sceneView.TryGetOverlay(ClothEditOverlay.OverlayId, out UnityEditor.Overlays.Overlay overlay), Is.True, "道具バーの Overlay が見つからない");
                Assert.That(((UnityEditor.Overlays.ITransientOverlay)overlay).visible, Is.True);

                sceneView.LookAt(new Vector3(0f, 0.5f, 0f), Quaternion.identity, 1.6f);
                yield return Pump(20);
                Shot(sceneView, "scene_overlay_editing");

                // 道具バーの操作はウィンドウの設定を変える
                window.OverlaySetTool(ClothEditMode.Pin);
                window.OverlaySetSymmetry(true);
                Assert.That(controller.Settings.Tool, Is.EqualTo(ClothEditMode.Pin));
                Assert.That(controller.Settings.SymmetryEnabled, Is.True);
                yield return Pump(20);
                Shot(sceneView, "scene_overlay_pin_tool");
            }
            finally
            {
                window.Close();
                sceneView.Close();
            }
        }

        // 衣装の正面から、布に当たるレイ（GuiCloth は 1m 四方で、x が -0.5〜0.5、y が 0.5〜1.5）
        static Ray ClothRay(float x, float y) => new Ray(new Vector3(x, y, -3f), Vector3.forward);

        // ワールドの点を、シーンビューの GUI 座標（イベントの mousePosition）にする
        static Vector2 GuiPoint(SceneView sceneView, Vector3 world)
        {
            Camera camera = sceneView.camera;
            Vector3 screen = camera.WorldToScreenPoint(world);
            float ppp = EditorGUIUtility.pixelsPerPoint;
            return new Vector2(screen.x / ppp, (camera.pixelHeight - screen.y) / ppp);
        }

        // 画像のために設定を変えるので、あとで元に戻す（EditorPrefs に保存される設定を壊さない）
        sealed class SettingsBackup
        {
            readonly ClothEditSettings _settings;
            readonly ClothEditMode _tool;
            readonly SelectionHandle _handle;
            readonly HandleOrientation _space;
            readonly bool _symmetry, _asCloth, _pinErase;
            readonly float _radius;
            readonly MoveDirection _moveDirection;

            public SettingsBackup(ClothEditSettings settings)
            {
                _settings = settings;
                _tool = settings.Tool;
                _handle = settings.HandleType;
                _space = settings.HandleSpace;
                _symmetry = settings.SymmetryEnabled;
                _asCloth = settings.SelectionMovesAsCloth;
                _radius = settings.SelectionFalloffRadius;
                _pinErase = settings.PinEraseMode;
                _moveDirection = settings.MoveDirection;
            }

            public void Restore()
            {
                _settings.Tool = _tool;
                _settings.HandleType = _handle;
                _settings.HandleSpace = _space;
                _settings.SymmetryEnabled = _symmetry;
                _settings.SelectionMovesAsCloth = _asCloth;
                _settings.SelectionFalloffRadius = _radius;
                _settings.PinEraseMode = _pinErase;
                _settings.MoveDirection = _moveDirection;
                _settings.Save();
            }
        }

        [UnityTest]
        public IEnumerator Window_SelectMove_DrawsTheSelectionSection_WithTheNumericFields()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 900);
            Renderer cloth = CreateCloth();
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            try
            {
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                SetField(window, "_target", cloth);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                // 画面に収まるよう、道具より上と下の区切りを閉じる
                SetField(window, "_foldTarget", false);
                SetField(window, "_foldBrush", false);
                SetField(window, "_foldDisplay", false);
                SetField(window, "_foldSave", false);

                // 1. 「選んで動かす」だが、まだ選んでいない
                controller.Settings.SymmetryEnabled = false;
                controller.Settings.Tool = ClothEditMode.Select;
                yield return Pump();
                Shot(window, "select_window_empty");

                // 2. 選んだ（ピン留めした点を含む）。対称もオン。数値で動かすを開く
                controller.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.1f, true);
                SetField(window, "_brushCentimeters", 12f);
                controller.BrushRadius = 0.12f;
                Assert.That(controller.SelectPaintBegin(ClothRay(0.05f, 1.45f), false), Is.True);
                controller.SelectPaintEnd();
                controller.Settings.SymmetryEnabled = true;
                controller.Settings.HandleSpace = HandleOrientation.Normal;
                SetField(window, "_foldNumeric", true);
                SetField(window, "_numericMove", new Vector3(0f, -5f, 0f));
                SetField(window, "_numericRotate", new Vector3(0f, 0f, 15f));
                SetField(window, "_numericScale", new Vector3(100f, 120f, 100f));
                yield return Pump();
                Assert.That(controller.SelectionCount, Is.GreaterThan(0));
                Shot(window, "select_window_selected");

                // 3. 「選んで動かす」以外の道具でも、選択があれば区切りが残る。形だけ動かす
                controller.Settings.SelectionMovesAsCloth = false;
                controller.Settings.Tool = ClothEditMode.Grab;
                yield return Pump();
                Shot(window, "select_window_other_tool");
            }
            finally
            {
                backup.Restore();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SceneView_SelectMove_ShowsThePointsAndTheHandle_AndTakesMouseEvents()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 700);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            Renderer cloth = CreateCloth();
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            try
            {
                SetField(window, "_target", cloth);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                SetField(window, "_showShortcuts", false);
                controller.Settings.SymmetryEnabled = false;
                controller.Settings.SelectionMovesAsCloth = true;
                controller.Settings.HandleType = SelectionHandle.Move;
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.identity, 0.9f);
                controller.Settings.Tool = ClothEditMode.Select;
                controller.Settings.SelectionFalloffRadius = 0.08f;
                SetField(window, "_brushCentimeters", 10f);   // ウィンドウのスライダーの値が、描画のたびに Controller へ渡る
                controller.BrushRadius = 0.1f;
                yield return Pump(20);

                // 1. マウスを乗せる（薄いピンクの点とブラシの円）
                Vector2 hoverPoint = GuiPoint(sceneView, new Vector3(0.15f, 1.1f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = hoverPoint });
                yield return Pump(10);
                Assert.That(controller.HasHover, Is.True, "マウスの位置から布に当たらない（座標の変換が合っていない）");
                Assert.That(controller.HoverSelectCount, Is.GreaterThan(0));
                Shot(sceneView, "select_scene_hover");

                // 2. ドラッグで塗って選ぶ（イベントを送って、窓の入口から確かめる）
                var hotBefore = GUIUtility.hotControl;
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = hoverPoint });
                sceneView.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = GuiPoint(sceneView, new Vector3(0.15f, 0.9f, 0f)) });
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = GuiPoint(sceneView, new Vector3(0.15f, 0.9f, 0f)) });
                yield return Pump(10);
                int painted = controller.SelectionCount;
                Assert.That(painted, Is.GreaterThan(50), "ドラッグで点が選ばれる");
                Assert.That(GUIUtility.hotControl, Is.EqualTo(hotBefore), "離したらホットなコントロールは戻る");
                Shot(sceneView, "select_scene_painted_move_handle");

                // 3. ハンドルの種類と、対称（鏡像の側）、なめらかな範囲の色分け
                controller.Settings.HandleType = SelectionHandle.Rotate;
                controller.Settings.SymmetryEnabled = true;
                yield return Pump(10);
                Shot(sceneView, "select_scene_rotate_handle_symmetry");
                controller.Settings.HandleType = SelectionHandle.Scale;
                controller.Settings.SymmetryEnabled = false;
                yield return Pump(10);
                Shot(sceneView, "select_scene_scale_handle");

                // 4. Shift+ドラッグの矩形で選び（Ctrl も押して外し）、空の所のクリックで解除する
                int beforeRect = controller.SelectionCount;
                Vector2 a = GuiPoint(sceneView, new Vector3(-0.4f, 1.3f, 0f));
                Vector2 b = GuiPoint(sceneView, new Vector3(-0.2f, 1.1f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = a });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, shift = true, mousePosition = a });
                sceneView.SendEvent(new Event { type = EventType.MouseDrag, button = 0, shift = true, mousePosition = b });
                yield return Pump(4);
                Shot(sceneView, "select_scene_rect_dragging");
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, shift = true, mousePosition = b });
                yield return Pump(10);
                Assert.That(controller.SelectionCount, Is.GreaterThan(beforeRect), "矩形で点が増える");

                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = GuiPoint(sceneView, new Vector3(0.3f, 0.6f, 0f)) });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 2, mousePosition = GuiPoint(sceneView, new Vector3(0.3f, 0.6f, 0f)) });
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = GuiPoint(sceneView, new Vector3(0.3f, 0.6f, 0f)) });
                yield return Pump(4);
                Assert.That(controller.SelectionCount, Is.GreaterThan(controller.Session.ParticleCount * 9 / 10), "ダブルクリックで部品全体（この布は1つの島）");

                Vector2 empty = GuiPoint(sceneView, new Vector3(0.8f, 1f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = empty });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = empty });
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = empty });
                yield return Pump(4);
                Assert.That(controller.SelectionCount, Is.EqualTo(0), "何もない所のクリックで解除");

                // 5. Esc: 選んだあとの Esc で解除される
                Assert.That(controller.SelectPaintBegin(ClothRay(0.1f, 1.0f), false), Is.True);
                controller.SelectPaintEnd();
                Assert.That(controller.SelectionCount, Is.GreaterThan(0));
                sceneView.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                yield return Pump(4);
                Assert.That(controller.SelectionCount, Is.EqualTo(0), "何もしていないときの Esc で解除");

                // 6. 数値で動かしてから、動かしたあとの形を見る
                Assert.That(controller.SelectPaintBegin(ClothRay(0.1f, 1.0f), false), Is.True);
                controller.SelectPaintEnd();
                Assert.That(controller.ApplySelectionTransform(new Vector3(0f, 0f, -0.1f), Quaternion.identity, Vector3.one), Is.True);
                for (int i = 0; i < 400 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 10 + i * 0.016);
                yield return Pump(10);
                Shot(sceneView, "select_scene_after_move");
            }
            finally
            {
                backup.Restore();
                window.Close();
                sceneView.Close();
            }
        }

        // 診断・対称の補助・比較の表示。衣装をアバターの中心から 8cm ずらして、相手のいない点・自動の面合わせ・左右差・左右対称にする・
        // 元の形の重ね描き・動いた量・減り方の同心円・3D カーソル・F キーで半径を変える操作を描く
        [UnityTest]
        public IEnumerator Window_And_SceneView_ShowDiagnosticsSymmetryAndCompare()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 1000);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            Renderer cloth = CreateCloth();
            GameObject avatar = Track(new GameObject("GuiAvatar"));
            avatar.AddComponent<Animator>();
            avatar.transform.position = new Vector3(-0.08f, 0f, 0f);
            cloth.transform.SetParent(avatar.transform, true);
            try
            {
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                SetField(window, "_target", cloth);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                ClothEditSettings settings = controller.Settings;
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldTarget", "_foldTool", "_foldBrush", "_foldDisplay", "_foldSave", "_foldGuide" }) SetField(window, fold, false);
                SetField(window, "_foldSymmetry", true);
                SetField(window, "_foldDiagnostics", true);
                settings.Tool = ClothEditMode.Grab;
                settings.SymmetryEnabled = true;
                settings.SymmetryOffset = 0f;
                settings.ShowUnmatchedMirror = true;
                settings.ShowMirrorDifference = false;
                controller.ApplySettings();
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.identity, 1.4f);
                yield return Pump(20);

                // 1. ずれたまま: 相手のいない点（赤紫）と、診断の注意
                Assert.That(controller.UnmatchedMirrorRatio, Is.GreaterThan(0.1f));
                Shot(window, "diag_symmetry_window_offcenter");
                Shot(sceneView, "symmetry_scene_unmatched");

                // 2. 鏡の面を自動で合わせ、右側だけ手前へ引いて、左右差を色分け
                Assert.That(controller.AutoFitSymmetryPlane(out string message), Is.True, message);
                controller.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.05f, true);
                controller.Session.PaintPins(new Vector3(-0.35f, 1.5f, 0f), 0.05f, true);
                controller.Session.PaintPins(new Vector3(0.35f, 1.5f, 0f), 0.05f, true);
                settings.SymmetryEnabled = false;
                settings.MoveDirection = MoveDirection.View;
                controller.ApplySettings();
                Assert.That(controller.PointerDown(ClothRay(0.25f, 0.9f), Vector3.forward, Vector3.up, 0f), Is.True);
                controller.PointerDrag(ClothRay(0.25f, 0.82f));
                controller.PointerUp();
                for (int i = 0; i < 600 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 10 + i * 0.016);
                settings.MoveDirection = MoveDirection.Free;
                settings.SymmetryEnabled = true;
                settings.ShowMirrorDifference = true;
                controller.ApplySettings();
                yield return Pump(20);
                Shot(window, "diag_symmetry_window_fitted");
                Shot(sceneView, "symmetry_scene_difference");

                // 3. 今の形を左右対称にする
                Assert.That(controller.SymmetrizeShape(), Is.GreaterThan(0));
                for (int i = 0; i < 600 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 20 + i * 0.016);
                yield return Pump(20);
                Shot(sceneView, "symmetry_scene_symmetrized");

                // 4. 比較: 元の形の重ね描きと動いた量。3D カーソル（Shift+右クリック）と減り方の同心円も
                settings.SymmetryEnabled = false;
                settings.ShowMirrorDifference = false;
                settings.ShowOriginalGhost = true;
                settings.ShowMoveHeatmap = true;
                settings.ShowFalloffRings = true;
                settings.Falloff = FalloffPreset.Smooth;
                controller.ApplySettings();
                SetField(window, "_foldSymmetry", false);
                SetField(window, "_foldDisplay", true);
                SetField(window, "_foldBrush", true);
                SetField(window, "_brushCentimeters", 12f);
                Vector2 cursorPoint = GuiPoint(sceneView, new Vector3(-0.25f, 0.8f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 1, shift = true, mousePosition = cursorPoint });
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 1, shift = true, mousePosition = cursorPoint });
                Assert.That(controller.HasCursor, Is.True, "Shift+右クリックで 3D カーソルが置かれる");
                Vector2 hover = GuiPoint(sceneView, new Vector3(0.1f, 1.1f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = hover });
                yield return Pump(20);
                Shot(window, "compare_window_brush_display");
                Shot(sceneView, "compare_scene_ghost_heatmap_rings_cursor");
                // 斜め横から見ると、元の形（平らな板）の水色の重ね描きと、手前へ出た編集中の形が両方見える
                settings.ShowMoveHeatmap = false;
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.Euler(15f, 60f, 0f), 1.4f);
                yield return Pump(20);
                Shot(sceneView, "compare_scene_ghost_side");
                settings.ShowMoveHeatmap = true;
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.identity, 1.4f);
                yield return Pump(10);

                // 5. F キーで半径を変える（始めてから右へ動かし、クリックで確定）
                float before = controller.BrushRadius;
                typeof(ClothEditWindow).GetMethod("ToggleRadiusDrag", Private).Invoke(window, new object[] { false });
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = hover + new Vector2(60f, 0f) });
                yield return Pump(10);
                Shot(sceneView, "radius_drag_scene");
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = hover + new Vector2(60f, 0f) });
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = hover + new Vector2(60f, 0f) });
                yield return Pump(4);
                Assert.That(controller.BrushRadius, Is.GreaterThan(before), "右へ動かすと大きくなる");
                Assert.That(controller.IsDragging || controller.IsSettling, Is.False, "確定のクリックで布はつままない");
            }
            finally
            {
                savedSettings.Save();
                window.Close();
                sceneView.Close();
            }
        }

        // ---- 簡単さ・使いやすさ（かんたん表示／くわしく表示、帯、保存したシェイプキーの一覧、届かない表示） ----

        // 編集を始める。始められなければテストを止める
        ClothEditController BeginEditing(ClothEditWindow window, Renderer cloth)
        {
            SetField(window, "_target", cloth);
            typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
            var controller = GetField<ClothEditController>(window, "_controller");
            Assert.That(controller.IsEditing, Is.True, controller.LastError);
            return controller;
        }

        // 下を少し引く（手を離して、仕上げまで進める）
        static void PullDown(ClothEditController controller, float x, float fromY, float distance)
        {
            Assert.That(controller.PointerDown(ClothRay(x, fromY), Vector3.forward, Vector3.up, 0f), Is.True);
            for (int k = 1; k <= 5; k++)
            {
                controller.PointerDrag(ClothRay(x, fromY - distance * k / 5f));
                controller.Tick(EditorApplication.timeSinceStartup + k * 0.016);
            }
            controller.PointerUp();
            for (int i = 0; i < 400 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 10 + i * 0.016);
        }

        [UnityTest]
        public IEnumerator Window_BeforeEditing_ShowsOnlyTheGuideTargetAndStart_AndDetailedShowsTheDiagnosticsToo()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 760);
            Renderer cloth = CreateCloth();
            try
            {
                ClothEditView.Mode = ClothEditViewMode.Simple;
                SetField(window, "_target", null);
                yield return Pump();
                Shot(window, "simplicity_before_editing_no_target");

                SetField(window, "_target", cloth);
                yield return Pump();
                Shot(window, "simplicity_before_editing_simple");

                ClothEditView.Mode = ClothEditViewMode.Detailed;
                yield return Pump();
                Shot(window, "simplicity_before_editing_detailed");

                // いちばん狭い幅（最小幅）でも、上の切り替えが収まる
                ClothEditView.Mode = ClothEditViewMode.Simple;
                window.position = new Rect(10, 10, 300, 560);
                yield return Pump();
                Shot(window, "simplicity_before_editing_narrow");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator Window_Editing_ShowsTheMainItemsInSimple_AndEverythingInDetailed()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 900);
            Renderer cloth = CreateCloth();
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            try
            {
                ClothEditController controller = BeginEditing(window, cloth);
                SetField(window, "_showShortcuts", false);
                SetField(window, "_foldGuide", false);
                controller.Settings.SymmetryEnabled = true;
                controller.Settings.Tool = ClothEditMode.Grab;

                // 0. ピンが無いと、診断の「情報」が1行に畳まれて出る（押すと開く）
                ClothEditView.Mode = ClothEditViewMode.Simple;
                yield return Pump();
                Shot(window, "simplicity_editing_simple_no_pins");

                controller.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.1f, true);
                PullDown(controller, 0.2f, 0.8f, 0.15f);

                // 1. かんたん表示: つまむ
                yield return Pump();
                Shot(window, "simplicity_editing_simple_grab");

                // 2. ピン留め（「塗ると外す」が出る）
                controller.Settings.Tool = ClothEditMode.Pin;
                yield return Pump();
                Shot(window, "simplicity_editing_simple_pin");

                // 3. 選んで動かす（選んだあと。主な項目だけ）
                SetField(window, "_brushCentimeters", 12f);
                controller.BrushRadius = 0.12f;
                Assert.That(controller.SelectPaintBegin(ClothRay(0.05f, 1.2f), false), Is.True);
                controller.SelectPaintEnd();
                controller.Settings.Tool = ClothEditMode.Select;
                yield return Pump();
                Shot(window, "simplicity_editing_simple_select");

                // 4. くわしく表示（同じ状態）。上から見た所と、下までスクロールした所
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                SetField(window, "_scroll", Vector2.zero);
                yield return Pump();
                Shot(window, "simplicity_editing_detailed_top");
                SetField(window, "_scroll", new Vector2(0f, 900f));
                yield return Pump();
                Shot(window, "simplicity_editing_detailed_bottom");

                // 5. くわしく表示: つまむ（動かす向きなどが出る）
                controller.Settings.Tool = ClothEditMode.Grab;
                SetField(window, "_scroll", Vector2.zero);
                yield return Pump();
                Shot(window, "simplicity_editing_detailed_grab");

                // 6. いちばん狭い幅（最小幅）の、かんたん表示
                ClothEditView.Mode = ClothEditViewMode.Simple;
                controller.Settings.Tool = ClothEditMode.Select;
                window.position = new Rect(10, 10, 300, 900);
                yield return Pump();
                Shot(window, "simplicity_editing_simple_narrow");
            }
            finally
            {
                backup.Restore();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator Window_ShowsThePreviousEditBand_AndTheBurstBand()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 640);
            Renderer cloth = CreateCloth();
            IClothSolverKernel savedKernel = ClothSolver.DefaultKernel;
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            try
            {
                // 1. 前回の編集が残っている（編集して取っておき、編集を終えた状態）
                ClothEditController controller = BeginEditing(window, cloth);
                controller.Settings.Tool = ClothEditMode.Grab;
                controller.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.1f, true);
                PullDown(controller, 0.2f, 0.8f, 0.15f);
                window.StashNow();
                controller.End();
                window.RefreshStashInfo(true);
                Assert.That(window.HasStashBand, Is.True);
                yield return Pump();
                Shot(window, "simplicity_restore_band");

                // 2. Burst が無いときの案内（初めて開いたときに1回だけ）。取っておきの帯と並ぶ所も見る
                ClothSolver.DefaultKernel = null;
                SetField(window, "_burstBandDecided", true);
                SetField(window, "_burstBandVisible", true);
                yield return Pump();
                Shot(window, "simplicity_both_bands");

                window.DiscardStash();
                Assert.That(window.HasStashBand, Is.False);
                yield return Pump();
                Shot(window, "simplicity_burst_band");

                // 3. 戻すを押すと、編集が始まって形・ピンが戻る
                ClothSolver.DefaultKernel = savedKernel;
                SetField(window, "_burstBandVisible", false);
                controller = BeginEditing(window, cloth);
                controller.Settings.Tool = ClothEditMode.Grab;
                PullDown(controller, 0.2f, 0.8f, 0.15f);
                window.StashNow();
                controller.End();
                window.RefreshStashInfo(true);
                Assert.That(window.RestoreFromStash(), Is.True, window.StashMessage);
                Assert.That(controller.HasUnsavedEdits, Is.True);
                yield return Pump();
                Shot(window, "simplicity_after_restore");
            }
            finally
            {
                ClothSolver.DefaultKernel = savedKernel;
                backup.Restore();
                window.DiscardStash();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator Window_SavedKeyList_ShowsTheListAndTheKeyGhostInTheScene()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 760);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            Renderer cloth = CreateCloth();
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            const string TempFolder = "Assets/GP02_GuiTemp";
            try
            {
                ClothEditController controller = BeginEditing(window, cloth);
                controller.SaveFolder = TempFolder + "/Meshes";
                controller.DataFolder = TempFolder + "/Data";
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldTarget", "_foldTool", "_foldBrush", "_foldSymmetry", "_foldGuide" }) SetField(window, fold, false);
                controller.Settings.Tool = ClothEditMode.Grab;
                controller.Settings.SymmetryEnabled = false;
                controller.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.4f, true);

                // 手前へふくらませて保存し、元に戻して、もう1つ保存する
                SetField(window, "_brushCentimeters", 15f);
                controller.BrushRadius = 0.15f;
                Assert.That(controller.SelectPaintBegin(ClothRay(0f, 0.8f), false), Is.True);
                controller.SelectPaintEnd();
                Assert.That(controller.ApplySelectionTransform(new Vector3(0f, 0f, -0.25f), Quaternion.identity, Vector3.one), Is.True);
                for (int i = 0; i < 600 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 10 + i * 0.016);
                Assert.That(controller.Save("Gui_Bulge", out string message), Is.True, message);
                controller.ResetAll();
                controller.ClearSelection();
                PullDown(controller, 0.3f, 0.7f, 0.1f);
                Assert.That(controller.Save("Gui_Pulled", out message), Is.True, message);
                controller.ResetAll();

                GP02ShapeKeyData data = window.FindSavedKeyData(out GP02ShapeKeys component);
                Assert.That(data, Is.Not.Null);
                Assert.That(data.Entries.Count, Is.EqualTo(2));
                yield return Pump();
                Shot(window, "simplicity_saved_keys");

                // 重みを動かすと、編集中の形に保存した形が橙の半透明で重なる（斜め横から見る）
                window.SetKeyWeight(data, data.Entries[0], 100f);
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.Euler(10f, 80f, 0f), 1.4f);
                yield return Pump(20);
                Shot(window, "simplicity_saved_keys_weight");
                Shot(sceneView, "simplicity_key_ghost_scene");
            }
            finally
            {
                backup.Restore();
                window.Close();
                sceneView.Close();
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        // ピン留めは1つの道具: ドラッグ＝留める、Ctrl＝外す、Shift＝矩形、ダブルクリック＝部品ごと。「塗ると外す」で逆になる。
        // シーンビューへイベントを送って、ウィンドウの入口から確かめる
        [UnityTest]
        public IEnumerator SceneView_PinTool_FollowsTheSameKeysAsSelectMove()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 700);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            Renderer cloth = CreateCloth();
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            try
            {
                ClothEditController controller = BeginEditing(window, cloth);
                SetField(window, "_showShortcuts", false);
                SetField(window, "_brushCentimeters", 10f);
                controller.BrushRadius = 0.1f;
                controller.Settings.Tool = ClothEditMode.Pin;
                controller.Settings.PinEraseMode = false;
                controller.Settings.SymmetryEnabled = false;
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.identity, 0.9f);
                yield return Pump(20);

                // マウスを動かしてからドラッグする（修飾キーと、クリックの回数を指定できる）
                IEnumerator Drag(Vector3 from, Vector3 to, bool shift = false, bool control = false, int clicks = 1)
                {
                    Vector2 a = GuiPoint(sceneView, from), b = GuiPoint(sceneView, to);
                    sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = a, shift = shift, control = control });
                    sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = clicks, mousePosition = a, shift = shift, control = control });
                    sceneView.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = b, shift = shift, control = control });
                    yield return Pump(3);
                    sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = b, shift = shift, control = control });
                    yield return Pump(4);
                }

                // 1. ドラッグで留める
                yield return Drag(new Vector3(-0.3f, 1.3f, 0f), new Vector3(0.3f, 1.3f, 0f));
                int painted = controller.Session.PinnedCount;
                Assert.That(painted, Is.GreaterThan(100), "ドラッグで塗った所が留まる");

                // 2. Ctrl+ドラッグで外す（同じ所を通る）
                yield return Drag(new Vector3(-0.3f, 1.3f, 0f), new Vector3(0.3f, 1.3f, 0f), control: true);
                Assert.That(controller.Session.PinnedCount, Is.EqualTo(0), "Ctrl+ドラッグで外れる");

                // 3. Shift+ドラッグで矩形に留める。ドラッグ中の矩形を画像にする
                Vector2 rectA = GuiPoint(sceneView, new Vector3(-0.35f, 1.4f, 0f)), rectB = GuiPoint(sceneView, new Vector3(0.35f, 0.9f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = rectA, shift = true });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = rectA, shift = true });
                sceneView.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = rectB, shift = true });
                yield return Pump(4);
                Shot(sceneView, "simplicity_pin_rect_pinning");
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = rectB, shift = true });
                yield return Pump(4);
                int rectPinned = controller.Session.PinnedCount;
                Assert.That(rectPinned, Is.GreaterThan(1000), "矩形の中が一度に留まる");

                // 4. Ctrl+Shift+ドラッグで、矩形の中を外す（黄色の矩形）
                Vector2 eraseA = GuiPoint(sceneView, new Vector3(-0.2f, 1.25f, 0f)), eraseB = GuiPoint(sceneView, new Vector3(0.2f, 1.05f, 0f));
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = eraseA, shift = true, control = true });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = eraseA, shift = true, control = true });
                sceneView.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = eraseB, shift = true, control = true });
                yield return Pump(4);
                Shot(sceneView, "simplicity_pin_rect_erasing");
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = eraseB, shift = true, control = true });
                yield return Pump(4);
                Assert.That(controller.Session.PinnedCount, Is.LessThan(rectPinned), "Ctrl+Shift+ドラッグで矩形の中が外れる");

                // 5. ダブルクリックで部品ごと留め、Ctrl+ダブルクリックで部品ごと外す
                yield return Drag(new Vector3(0.4f, 0.6f, 0f), new Vector3(0.4f, 0.6f, 0f), clicks: 2);
                Assert.That(controller.Session.PinnedCount, Is.GreaterThan(controller.Session.ParticleCount * 9 / 10), "ダブルクリックで部品全体（この布は1つの島）");
                yield return Drag(new Vector3(0.4f, 0.6f, 0f), new Vector3(0.4f, 0.6f, 0f), control: true, clicks: 2);
                Assert.That(controller.Session.PinnedCount, Is.EqualTo(0), "Ctrl+ダブルクリックで部品全体が外れる");

                // 6. 「塗ると外す」をオン: ドラッグで外れ、Ctrl+ドラッグで留まる
                controller.Settings.PinEraseMode = true;
                yield return Pump(4);
                yield return Drag(new Vector3(-0.3f, 1.3f, 0f), new Vector3(0.3f, 1.3f, 0f), control: true);
                Assert.That(controller.Session.PinnedCount, Is.GreaterThan(100), "塗ると外すがオンなら、Ctrl+ドラッグで留まる");
                Shot(sceneView, "simplicity_pin_erase_mode");
                yield return Drag(new Vector3(-0.3f, 1.3f, 0f), new Vector3(0.3f, 1.3f, 0f));
                Assert.That(controller.Session.PinnedCount, Is.EqualTo(0), "塗ると外すがオンなら、ドラッグで外れる");

                // 7. Undo は1操作ずつ（直前の「外す」を戻すと、留めた状態に戻る）
                controller.UndoLast();
                Assert.That(controller.Session.PinnedCount, Is.GreaterThan(100));
            }
            finally
            {
                backup.Restore();
                window.Close();
                sceneView.Close();
            }
        }

        [UnityTest]
        public IEnumerator SceneView_ReachLine_ShowsWhenThePullIsBeyondTheClothLength()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 700);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            Renderer cloth = CreateCloth();
            SettingsBackup backup = new SettingsBackup(GetField<ClothEditController>(window, "_controller").Settings);
            try
            {
                ClothEditController controller = BeginEditing(window, cloth);
                SetField(window, "_showShortcuts", false);
                controller.Settings.Tool = ClothEditMode.Grab;
                controller.Settings.SymmetryEnabled = false;
                controller.Session.PaintPins(new Vector3(0f, 1.5f, 0f), 0.12f, true);
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.identity, 1.8f);
                yield return Pump(10);

                // 布の長さ（約 0.8m）で届かない所まで、下へ 0.8m 引く。実際の更新で計算が進むよう、フレームを回しながら動かす
                Assert.That(controller.PointerDown(ClothRay(0f, 0.6f), Vector3.forward, Vector3.up, 0f), Is.True);
                for (int k = 1; k <= 10; k++)
                {
                    controller.PointerDrag(ClothRay(0f, 0.6f - 0.8f * k / 10f));
                    yield return null;
                }
                yield return Pump(30);
                Assert.That(controller.IsReachTracking, Is.True);
                Assert.That(controller.IsOutOfReach, Is.True, "届かない: " + controller.ReachDistance);
                Shot(sceneView, "simplicity_reach_scene");

                controller.PointerUp();
                for (int i = 0; i < 600 && controller.IsBusy; i++) controller.Tick(EditorApplication.timeSinceStartup + 10 + i * 0.016);
                yield return Pump(10);
                Assert.That(controller.IsReachTracking, Is.False);
            }
            finally
            {
                backup.Restore();
                window.Close();
                sceneView.Close();
            }
        }
    }
}
