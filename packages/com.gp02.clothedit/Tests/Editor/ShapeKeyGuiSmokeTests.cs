using System;
using System.Collections;
using System.IO;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 画面（ウィンドウとインスペクター）を実際に描画して、例外やレイアウトの食い違いが出ないことを確かめる。
    /// 描画には画面が要るので、通常のテスト（-nographics）では飛ばす。
    /// <c>GP02_GUI=1 GP02_UNITY_ARGS="-testFilter GP02.Tests.ShapeKeyGuiSmokeTests" tools/unity/run-tests.sh</c>
    /// で、仮想画面（Xvfb）の上で動かす。画像は tools/unity/out/gui/ に出る。
    /// </summary>
    public class ShapeKeyGuiSmokeTests : ShapeKeyUiTestBase
    {
        const string ShotFolder = "/repo/tools/unity/out/gui";

        sealed class HostWindow : EditorWindow
        {
            public Action Draw;
            public int Calls;

            void OnGUI()
            {
                Calls++;
                Draw?.Invoke();
            }
        }

        UiLanguage _savedLanguage;
        bool _languageChanged;

        [SetUp]
        public void RequireAScreen()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("画面が無い環境（-nographics）では描画できません。GP02_GUI=1 で仮想画面の上で動かしてください。");
            }
            // Docker の Unity には日本語のフォントが無く、日本語の文字が空白になる。画像で読めるように英語で描く
            _savedLanguage = L10n.Language;
            _languageChanged = true;
            L10n.Language = UiLanguage.English;
        }

        [TearDown]
        public void RestoreLanguage()
        {
            if (_languageChanged) L10n.Language = _savedLanguage;
            _languageChanged = false;
        }

        GP02ShapeKeys MakeTarget(out Renderer renderer, out GP02ShapeKeyData data)
        {
            Mesh mesh = CreateGridMesh("GuiTarget");
            int n = mesh.vertexCount;
            var a = Entry("Smile", Ramp(n, 0.002f));
            var b = Entry("Mouth_Open", Ramp(n, 0.003f));
            b.Group = "Face";
            var c = Entry("Mouth_Wide", Ramp(n, 0.001f));
            c.Group = "Face";
            var wrong = Entry("From_Another_Mesh", new Vector3[4]);
            var inBetween = Entry("Blink", Ramp(n, 0.002f), 50f);
            inBetween.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.004f) });
            data = NewData(mesh, a, b, c, wrong, inBetween);
            data.Groups.Add("Face");
            renderer = CreateRenderer(mesh, true, "GuiTarget");
            return ShapeKeyLibrary.Attach(renderer, data);
        }

        static IEnumerator Pump(int frames = 6)
        {
            for (int i = 0; i < frames; i++)
            {
                InternalEditorUtility.RepaintAllViews();
                yield return null;
            }
        }

        // 画面に出ている内容を画像にする（確認用）
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

        [UnityTest]
        public IEnumerator Window_DrawsForSelectedObjectsWithAndWithoutData_WithoutErrors()
        {
            GP02ShapeKeys component = MakeTarget(out Renderer renderer, out GP02ShapeKeyData data);
            GameObject plain = Track(new GameObject("Plain"));
            var window = EditorWindow.GetWindow<ShapeKeyListWindow>();
            window.position = new Rect(10, 10, 440, 900);
            try
            {
                Selection.activeGameObject = plain;
                window.OnSelectionChanged();
                yield return Pump();
                Assert.That(window.GuiCalls, Is.GreaterThan(0), "描画が走っていない");

                Selection.activeGameObject = renderer.gameObject;
                window.OnSelectionChanged();
                window.View.SetPreviewWeight(data.Entries[0], 60f);
                yield return Pump();
                Shot(window, "window_basic");

                // 展開・選択した状態でも描ける
                window.View.Tree.ExpandAll();
                window.View.Tree.SelectEntries(new[] { data.Entries[1], data.Entries[2] });
                yield return Pump();
                Shot(window, "window_selected");

                // 折りたたみを開いた状態（操作の設定・取り込み）
                window.View.Settings.ShowOperationSettings = true;
                window.View.Settings.ShowImport = true;
                yield return Pump();
                Shot(window, "window_settings");
                window.View.Settings.ShowOperationSettings = false;
                window.View.Settings.ShowImport = false;

                // 布で編集中の保存欄
                var controller = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 4f });
                Func<ClothEditController> savedProvider = ShapeKeyClothEdit.ControllerProvider;
                ShapeKeyClothEdit.ControllerProvider = () => controller;
                try
                {
                    Assert.That(ShapeKeyClothEdit.Begin(controller, component, data.Entries[0], out string message), Is.True, message);
                    yield return Pump();
                    Shot(window, "window_cloth_edit");
                }
                finally
                {
                    controller.Dispose();
                    ShapeKeyClothEdit.ControllerProvider = savedProvider;
                    ShapeKeyClothEdit.Clear();
                }

                component.Data = null;
                yield return Pump();
                Shot(window, "window_no_data");
                component.Data = data;
                yield return Pump();
            }
            finally
            {
                window.Close();
                Selection.activeGameObject = null;
            }
            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.False, "ウィンドウを閉じるとプレビューは戻る");
            Assert.That(ClothEditController.GetMesh(renderer), Is.SameAs(data.SourceMesh));
        }

        [UnityTest]
        public IEnumerator Inspector_DrawsWithValidAndBrokenData_WithoutErrors()
        {
            GP02ShapeKeys component = MakeTarget(out Renderer renderer, out GP02ShapeKeyData data);
            Editor editor = Editor.CreateEditor(component);
            var host = ScriptableObject.CreateInstance<HostWindow>();
            host.position = new Rect(10, 10, 420, 900);
            host.Draw = () => editor.OnInspectorGUI();
            host.ShowUtility();
            try
            {
                yield return Pump();
                Assert.That(host.Calls, Is.GreaterThan(0), "描画が走っていない");
                Shot(host, "inspector_basic");

                // 壊れた状態（データが無い・レンダラーのメッシュが None・頂点数が違う）でも描ける
                component.Data = null;
                yield return Pump();
                Shot(host, "inspector_no_data");
                component.Data = data;
                ((SkinnedMeshRenderer)renderer).sharedMesh = null;
                yield return Pump();
                Shot(host, "inspector_mesh_none");
                ((SkinnedMeshRenderer)renderer).sharedMesh = Track(new Mesh { name = "Other", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
                yield return Pump();
                Shot(host, "inspector_mismatch");
            }
            finally
            {
                host.Close();
                Object.DestroyImmediate(editor);
            }
        }
    }
}
