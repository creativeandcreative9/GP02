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
    }
}
