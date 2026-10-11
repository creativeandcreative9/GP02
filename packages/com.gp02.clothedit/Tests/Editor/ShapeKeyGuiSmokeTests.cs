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
        ClothEditViewMode _savedViewMode;

        [SetUp]
        public void RequireAScreen()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("画面が無い環境（-nographics）では描画できません。GP02_GUI=1 で仮想画面の上で動かしてください。");
            }
            // 既定は英語で描く。GP02_GUI_LANG=ja なら日本語（run-tests.sh が日本語のフォントをコンテナに入れる）
            _savedLanguage = L10n.Language;
            _languageChanged = true;
            L10n.Language = System.Environment.GetEnvironmentVariable("GP02_GUI_LANG") == "ja" ? UiLanguage.Japanese : UiLanguage.English;
            // 表示のしかたは、既定（かんたん）から始める。くわしく表示を見るテストは、自分で切り替える
            _savedViewMode = ClothEditView.Mode;
            ClothEditView.Mode = ClothEditViewMode.Simple;
        }

        [TearDown]
        public void RestoreLanguage()
        {
            if (_languageChanged)
            {
                L10n.Language = _savedLanguage;
                ClothEditView.Mode = _savedViewMode;
            }
            _languageChanged = false;
        }

        GP02ShapeKeys MakeTarget(out Renderer renderer, out GP02ShapeKeyData data)
        {
            Mesh mesh = CreateGridMesh("GuiTarget");
            int n = mesh.vertexCount;
            // 元のメッシュに同じ名前の BlendShape があると、書き出しの一覧に「上書き」が出る
            mesh.AddBlendShapeFrame("Smile", 100f, Ramp(n, 0.001f), null, null);
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
            // 内容は、ウィンドウの上のタブの分だけ下にずれる。その分も写す（画面の外には出ない）
            int height = Mathf.Min((int)rect.height + 24, Screen.currentResolution.height - (int)rect.y);
            Color[] pixels = InternalEditorUtility.ReadScreenPixel(rect.position, (int)rect.width, height);
            var texture = new Texture2D((int)rect.width, height, TextureFormat.RGB24, false);
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
                ClothEditView.Mode = ClothEditViewMode.Detailed;
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

                // 詳細: 素体に焼き込む印・in-between のフレーム・前の版
                data.Entries[1].Prebake = true;
                data.Entries[1].PrebakeWeight = 60f;
                ShapeKeyHistory.Push(data.Entries[4], "布で編集して上書き");
                ShapeKeyHistory.Push(data.Entries[4], "取り込みで上書き");
                window.View.Tree.SelectEntries(new[] { data.Entries[4] });
                yield return Pump();
                Shot(window, "window_details_frames");
                window.View.Tree.SelectEntries(new[] { data.Entries[1] });
                yield return Pump();
                Shot(window, "window_details_prebake");

                // 検索・並べ替え・ソロ
                window.View.Settings.FilterText = "mouth";
                window.View.Settings.Sort = ShapeKeyListSort.Name;
                window.View.SetSolo(data.Entries[2]);
                window.View.Tree.Reload();
                window.View.Tree.ExpandAll();
                yield return Pump();
                Shot(window, "window_filter_solo");
                window.View.SetSolo(null);
                window.View.Settings.FilterText = "";
                window.View.Settings.Sort = ShapeKeyListSort.DataOrder;
                window.View.Tree.Reload();
                window.View.StopPreview();

                // 書き出しの確認の一覧・名前の前置き・後置き・トグル用 AnimationClip・バックアップ
                bool showOperations = window.View.Settings.ShowOperations, showDetails = window.View.Settings.ShowDetails;
                window.View.Settings.ShowOperations = false;
                window.View.Settings.ShowDetails = false;
                component.NamePrefix = "GP02_";
                component.SkipIncompatible = true;
                window.View.Settings.CreateClips = true;
                window.View.Settings.ShowBackups = true;
                yield return Pump();
                Shot(window, "window_export");
                component.SkipIncompatible = false;
                yield return Pump();
                Shot(window, "window_export_error");
                component.NamePrefix = "";
                window.View.Settings.CreateClips = false;
                window.View.Settings.ShowBackups = false;

                // 選んだ点の範囲・別のメッシュへ写す
                window.View.Settings.ShowOperations = true;
                window.View.Settings.ShowRangeTools = true;
                window.View.Settings.ShowTransfer = true;
                window.View.Settings.ShowOperationSettings = true;
                window.View.Settings.SplitInAvatarSpace = true;
                window.View.Settings.ShowExport = false;
                yield return Pump();
                Shot(window, "window_range_transfer");
                window.View.Settings.ShowRangeTools = false;
                window.View.Settings.ShowTransfer = false;
                window.View.Settings.ShowOperationSettings = false;
                window.View.Settings.SplitInAvatarSpace = false;
                window.View.Settings.ShowExport = true;
                window.View.Settings.ShowOperations = showOperations;
                window.View.Settings.ShowDetails = showDetails;
                data.Entries[1].Prebake = false;

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
        public IEnumerator Window_SimpleView_ShowsOnlyTheMainOperations()
        {
            GP02ShapeKeys component = MakeTarget(out Renderer renderer, out GP02ShapeKeyData data);
            var window = EditorWindow.GetWindow<ShapeKeyListWindow>();
            window.position = new Rect(10, 10, 440, 720);
            try
            {
                ClothEditView.Mode = ClothEditViewMode.Simple;
                Selection.activeGameObject = renderer.gameObject;
                window.OnSelectionChanged();
                window.View.SetPreviewWeight(data.Entries[0], 60f);
                yield return Pump();
                Assert.That(window.GuiCalls, Is.GreaterThan(0), "描画が走っていない");
                Shot(window, "window_simple");

                // 選ぶと、複製・削除・左右に分割・左右反転が使える
                window.View.Tree.ExpandAll();
                window.View.Tree.SelectEntries(new[] { data.Entries[1], data.Entries[2] });
                yield return Pump();
                Shot(window, "window_simple_selected");

                // 同じ状態をくわしく表示にすると、検索・並べ替え・コピー・グループ・詳細などが増える
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                yield return Pump();
                Shot(window, "window_simple_vs_detailed");
            }
            finally
            {
                window.Close();
                Selection.activeGameObject = null;
            }
            Assert.That(component, Is.Not.Null);
        }

        // かんたん表示の「メッシュへの反映」: データとメッシュが同じなら「✓ メッシュに反映済み」の1行、違うときだけ「メッシュに反映（変更 N 件）」
        [UnityTest]
        public IEnumerator Window_SimpleView_ShowsWhetherTheMeshIsUpToDate()
        {
            Mesh mesh = CreateGridMesh("GuiSync");
            int n = mesh.vertexCount;
            GP02ShapeKeyData data = NewData(mesh, Entry("GP02_1", Ramp(n, 0.002f)), Entry("GP02_2", Ramp(n, 0.003f)));
            Renderer renderer = CreateRenderer(mesh, true, "GuiSync");
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            var window = EditorWindow.GetWindow<ShapeKeyListWindow>();
            window.position = new Rect(10, 10, 440, 620);
            try
            {
                ClothEditView.Mode = ClothEditViewMode.Simple;
                Selection.activeGameObject = renderer.gameObject;
                window.OnSelectionChanged();
                yield return Pump();
                Assert.That(window.View.GetMeshStatus().ChangeCount, Is.EqualTo(2), "元のメッシュのままなので、2 件が加わる");
                Shot(window, "window_simple_apply_needed");

                ShapeKeyExporter.ExportToMesh(component, ShapeKeyBuildOptions.FromComponent(component), MeshFolder);
                yield return Pump();
                Assert.That(window.View.GetMeshStatus().InSync, Is.True);
                Shot(window, "window_simple_applied");

                // データの形を変えると、違う 1 件
                ShapeKeyListOperations.Scale(data, new[] { data.Entries[0] }, 0.5f);
                yield return Pump();
                Assert.That(window.View.GetMeshStatus().ChangeCount, Is.EqualTo(1));
                Shot(window, "window_simple_changed");

                // くわしく表示でも、違いの1行と「メッシュに反映（変更 1 件）」が出る（ほかの区切りは畳んで、反映の欄を見せる）
                bool showOperations = window.View.Settings.ShowOperations, showDetails = window.View.Settings.ShowDetails;
                window.View.Settings.ShowOperations = false;
                window.View.Settings.ShowDetails = false;
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                window.position = new Rect(10, 10, 440, 960);
                yield return Pump();
                Shot(window, "window_detailed_changed");
                window.View.Settings.ShowOperations = showOperations;
                window.View.Settings.ShowDetails = showDetails;
            }
            finally
            {
                window.Close();
                Selection.activeGameObject = null;
            }
        }

        [UnityTest]
        public IEnumerator DataInspector_ListsTheBackupsOfASavedData()
        {
            string savedFolder = ShapeKeyBackup.Folder;
            ShapeKeyBackup.Folder = TempRoot + "/Backups";
            if (!AssetDatabase.IsValidFolder(TempRoot)) AssetDatabase.CreateFolder("Assets", "GP02_ShapeKeyUiTestTemp");
            if (!AssetDatabase.IsValidFolder(MeshFolder)) AssetDatabase.CreateFolder(TempRoot, "Meshes");
            Mesh mesh = Object.Instantiate(CreateGridMesh("InspectorGrid"));
            AssetDatabase.CreateAsset(mesh, MeshFolder + "/InspectorGrid.asset");
            GP02ShapeKeyData data = ShapeKeyLibrary.CreateData(mesh, DataFolder);
            int n = mesh.vertexCount;
            data.Entries.Add(Entry("Smile", Ramp(n, 0.002f)));
            data.Entries.Add(Entry("Open", Ramp(n, 0.003f)));
            data.Entries.Add(Entry("Brow", Ramp(n, 0.001f)));
            ShapeKeyListOperations.Scale(data, new[] { data.Entries[0] }, 0.5f);
            ShapeKeyListOperations.Delete(data, new[] { data.Entries[2] });
            ShapeKeyListOperations.Merge(data, new[] { data.Entries[0], data.Entries[1] }, null, "Both", true);

            Editor editor = Editor.CreateEditor(data);
            var host = ScriptableObject.CreateInstance<HostWindow>();
            host.position = new Rect(10, 10, 440, 420);
            host.Draw = () => editor.OnInspectorGUI();
            host.ShowUtility();
            try
            {
                yield return Pump();
                Assert.That(host.Calls, Is.GreaterThan(0), "描画が走っていない");
                Assert.That(ShapeKeyBackup.List(data).Count, Is.EqualTo(3));
                Shot(host, "data_inspector");
            }
            finally
            {
                host.Close();
                Object.DestroyImmediate(editor);
                ShapeKeyBackup.Folder = savedFolder;
            }
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
