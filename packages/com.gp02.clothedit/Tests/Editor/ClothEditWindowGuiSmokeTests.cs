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
    public class ClothEditWindowGuiSmokeTests
    {
        const string ShotFolder = "/repo/tools/unity/out/gui";
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        readonly List<Object> _created = new List<Object>();
        UiLanguage _savedLanguage;

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
        }

        [TearDown]
        public void Cleanup()
        {
            L10n.Language = _savedLanguage;
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
            Color[] pixels = InternalEditorUtility.ReadScreenPixel(rect.position, (int)rect.width, (int)rect.height);
            var texture = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGB24, false);
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
            readonly bool _symmetry, _asCloth;
            readonly float _radius;

            public SettingsBackup(ClothEditSettings settings)
            {
                _settings = settings;
                _tool = settings.Tool;
                _handle = settings.HandleType;
                _space = settings.HandleSpace;
                _symmetry = settings.SymmetryEnabled;
                _asCloth = settings.SelectionMovesAsCloth;
                _radius = settings.SelectionFalloffRadius;
            }

            public void Restore()
            {
                _settings.Tool = _tool;
                _settings.HandleType = _handle;
                _settings.HandleSpace = _space;
                _settings.SymmetryEnabled = _symmetry;
                _settings.SelectionMovesAsCloth = _asCloth;
                _settings.SelectionFalloffRadius = _radius;
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
    }
}
