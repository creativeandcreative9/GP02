using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 重みのプレビュー（<see cref="ShapeKeyPreview"/>）を確かめる。
    /// 最優先は「元のメッシュ・アセットを書き換えない」と「一時メッシュを必ず元に戻す」こと。
    /// </summary>
    public class ShapeKeyPreviewTests : ShapeKeyUiTestBase
    {
        sealed class Setup
        {
            public Mesh Source;
            public GP02ShapeKeyData Data;
            public Renderer Renderer;
            public ShapeKeyEntry Entry;
            public Vector3[] Deltas;
        }

        Setup MakeSetup(bool skinned = true)
        {
            var s = new Setup { Source = CreateGridMesh("Preview") };
            int n = s.Source.vertexCount;
            s.Deltas = Ramp(n, 0.004f);
            s.Entry = Entry("Lift", s.Deltas);
            s.Data = NewData(s.Source, s.Entry);
            s.Renderer = CreateRenderer(s.Source, skinned);
            return s;
        }

        static Mesh MeshOf(Renderer renderer) => ClothEditController.GetMesh(renderer);

        [TestCase(true)]
        [TestCase(false)]
        public void Apply_ShowsTheWeightedShapeOnATemporaryMesh_WithoutTouchingTheSourceOrTheBlendShapeWeights(bool skinned)
        {
            Setup s = MakeSetup(skinned);
            Vector3[] sourceVertices = s.Source.vertices;
            s.Entry.PreviewWeight = 50f;

            Assert.That(ShapeKeyPreview.Apply(s.Renderer, s.Data, out string error), Is.True, error);

            Mesh temp = MeshOf(s.Renderer);
            Assert.That(temp, Is.Not.SameAs(s.Source));
            Assert.That(temp.hideFlags & HideFlags.DontSave, Is.EqualTo(HideFlags.DontSave), "保存されない一時メッシュ");
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True);
            Assert.That(ShapeKeyPreview.GetTempMesh(s.Renderer), Is.SameAs(temp));
            Assert.That(ShapeKeyPreview.GetOriginalMesh(s.Renderer), Is.SameAs(s.Source));
            Vector3[] shown = temp.vertices;
            for (int v = 0; v < shown.Length; v++)
            {
                Assert.That(Vector3.Distance(shown[v], sourceVertices[v] + s.Deltas[v] * 0.5f), Is.LessThan(1e-6f), $"頂点 {v}");
            }
            AssertSame(sourceVertices, s.Source.vertices, 0f, "元のメッシュは書き換えない");
            Assert.That(AssetDatabase.GetAssetPath(temp), Is.Empty, "アセットにならない");
        }

        [Test]
        public void Apply_UpdatesTheSameTemporaryMeshWhenWeightsChange_AndEndsWhenAllWeightsAreZero()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 20f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);
            Vector3 base0 = s.Source.vertices[3];

            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(temp), "一時メッシュは作り直さない");
            Assert.That(Vector3.Distance(temp.vertices[3], base0 + s.Deltas[3]), Is.LessThan(1e-6f));

            s.Entry.PreviewWeight = 0f;
            Assert.That(ShapeKeyPreview.Apply(s.Renderer, s.Data, out string error), Is.True, error);
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source), "重みがすべて 0 なら元に戻る");
            Assert.That(temp == null, Is.True, "一時メッシュは捨てる");
        }

        [Test]
        public void Apply_IgnoresDisabledEntries_AndInterpolatesInBetweenFrames()
        {
            Setup s = MakeSetup();
            int n = s.Source.vertexCount;
            Vector3[] half = Ramp(n, 0.001f);
            var inBetween = new ShapeKeyEntry { Name = "IB" };
            inBetween.Frames.Add(new ShapeKeyFrame { Weight = 50f, DeltaVertices = half });
            inBetween.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = s.Deltas });
            inBetween.PreviewWeight = 75f;
            s.Data.Entries.Add(inBetween);
            s.Entry.PreviewWeight = 100f;
            s.Entry.Enabled = false;

            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);

            Vector3[] shown = MeshOf(s.Renderer).vertices;
            Vector3[] sourceVertices = s.Source.vertices;
            for (int v = 0; v < n; v += 17)
            {
                Vector3 expected = sourceVertices[v] + Vector3.Lerp(half[v], s.Deltas[v], 0.5f);
                Assert.That(Vector3.Distance(shown[v], expected), Is.LessThan(1e-6f), $"頂点 {v}");
            }
        }

        [Test]
        public void Apply_MovesNormalsWithTheNormalDeltas()
        {
            Setup s = MakeSetup();
            int n = s.Source.vertexCount;
            var normalDeltas = new Vector3[n];
            for (int v = 0; v < n; v++) normalDeltas[v] = new Vector3(0.5f, 0f, 0f);
            s.Entry.Frames[0].DeltaNormals = normalDeltas;
            s.Entry.PreviewWeight = 100f;
            Vector3 before = s.Source.normals[0];

            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);

            Vector3 after = MeshOf(s.Renderer).normals[0];
            Assert.That(Vector3.Distance(after, (before + new Vector3(0.5f, 0f, 0f)).normalized), Is.LessThan(1e-5f));
            Assert.That(s.Source.normals[0], Is.EqualTo(before), "元の法線は変わらない");
        }

        [Test]
        public void Begin_RefusesAMeshWithAnotherVertexCountWithoutTouchingTheRenderer()
        {
            Setup s = MakeSetup();
            s.Data.VertexCount = 10;
            s.Entry.PreviewWeight = 50f;

            Assert.That(ShapeKeyPreview.Begin(s.Renderer, s.Data, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);

            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = null;
            s.Data.VertexCount = s.Source.vertexCount;
            Assert.That(ShapeKeyPreview.Begin(s.Renderer, s.Data, out error), Is.False, "メッシュが None のときは始めない");
            Assert.That(ShapeKeyPreview.Begin(null, s.Data, out error), Is.False);
            Assert.That(ShapeKeyPreview.Begin(s.Renderer, null, out error), Is.False);
        }

        [Test]
        public void End_RestoresTheOriginalMeshAndDestroysTheTemporaryOne()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);

            ShapeKeyPreview.End(s.Renderer);

            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Assert.That(temp == null, Is.True);
            Assert.That(ShapeKeyPreview.ActiveCount, Is.EqualTo(0));
            ShapeKeyPreview.End(s.Renderer);   // 2回呼んでも何も起きない
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
        }

        [Test]
        public void EndAll_RestoresEveryRenderer()
        {
            Setup a = MakeSetup();
            Setup b = MakeSetup(false);
            a.Entry.PreviewWeight = 100f;
            b.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(a.Renderer, a.Data, out _);
            ShapeKeyPreview.Apply(b.Renderer, b.Data, out _);
            Assert.That(ShapeKeyPreview.ActiveCount, Is.EqualTo(2));

            ShapeKeyPreview.EndAll();

            Assert.That(MeshOf(a.Renderer), Is.SameAs(a.Source));
            Assert.That(MeshOf(b.Renderer), Is.SameAs(b.Source));
            Assert.That(ShapeKeyPreview.ActiveCount, Is.EqualTo(0));
        }

        [Test]
        public void EndUnselected_RestoresRenderersThatAreNoLongerSelected()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            GameObject other = Track(new GameObject("Other"));
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);

            Selection.activeGameObject = s.Renderer.gameObject;
            ShapeKeyPreview.EndUnselected();
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True, "選択中なら続く");

            Selection.activeGameObject = other;
            ShapeKeyPreview.EndUnselected();
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Selection.activeGameObject = null;
        }

        [Test]
        public void Validate_DoesNotOverwriteAMeshThatAnotherOperationAssigned_AndDropsTheTemporaryMesh()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);

            Mesh assigned = CreateGridMesh("Assigned");
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = assigned;
            ShapeKeyPreview.Validate();

            Assert.That(MeshOf(s.Renderer), Is.SameAs(assigned), "他の操作で割り当てたメッシュは上書きしない");
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);
            Assert.That(temp == null, Is.True);
        }

        [Test]
        public void Validate_DropsPreviewsOfDestroyedRenderers()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);

            Object.DestroyImmediate(s.Renderer.gameObject);
            ShapeKeyPreview.Validate();

            Assert.That(ShapeKeyPreview.ActiveCount, Is.EqualTo(0));
            Assert.That(temp == null, Is.True);
        }

        [Test]
        public void UndoDuringPreviewNeverLeavesTheTemporaryMeshOnTheRenderer()
        {
            Setup s = MakeSetup();
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(s.Renderer, s.Data);
            ShapeKeyExporter.ExportToMesh(component, true, MeshFolder);

            // 書き出し（Undo に記録される）のあとにプレビューを始め、Undo する
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True);
            Undo.PerformUndo();
            ShapeKeyPreview.Validate();

            Mesh now = MeshOf(s.Renderer);
            Assert.That(now == null || (now.hideFlags & HideFlags.DontSave) == 0, Is.True, "一時メッシュが残らない");
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);
            Assert.That(now, Is.SameAs(s.Source), "書き出しの Undo で元のメッシュに戻っている");
        }

        [Test]
        public void Export_EndsThePreviewFirst_SoTheTemporaryMeshIsNeverExported()
        {
            Setup s = MakeSetup();
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(s.Renderer, s.Data);
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);

            Mesh exported = ShapeKeyExporter.ExportToMesh(component, true, MeshFolder);

            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(exported));
            Assert.That(temp == null, Is.True);
            Assert.That(exported.hideFlags & HideFlags.DontSave, Is.EqualTo(HideFlags.None));
            AssertSame(s.Source.vertices, exported.vertices, 0f, "書き出したメッシュの頂点は、プレビューの形ではなく元の形");
        }

        [Test]
        public void RecoverAfterReload_PutsTheOriginalMeshBackWhenTheStateWasLost()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);
            Assert.That(temp, Is.Not.SameAs(s.Source));

            // 再読み込みで C# の状態だけが消え、一時メッシュがレンダラーに残った場合
            ShapeKeyPreview.ForgetWithoutRestoring();
            Assert.That(MeshOf(s.Renderer), Is.SameAs(temp));
            ShapeKeyPreview.RecoverAfterReload();

            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Assert.That(temp == null, Is.True);
        }

        [Test]
        public void RecoverAfterReload_FixesAMeshThatBecameNone_AndLeavesOtherMeshesAlone()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp = MeshOf(s.Renderer);
            ShapeKeyPreview.ForgetWithoutRestoring();
            Object.DestroyImmediate(temp);      // 一時メッシュが消えて、レンダラーのメッシュが None になった
            Assert.That(MeshOf(s.Renderer) == null, Is.True);

            ShapeKeyPreview.RecoverAfterReload();
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source), "None を元のメッシュに直す");

            // 他の操作で差し替え済みなら触らない
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Mesh temp2 = MeshOf(s.Renderer);
            ShapeKeyPreview.ForgetWithoutRestoring();
            Mesh assigned = CreateGridMesh("Assigned");
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = assigned;
            ShapeKeyPreview.RecoverAfterReload();
            Assert.That(MeshOf(s.Renderer), Is.SameAs(assigned));
            Object.DestroyImmediate(temp2);
        }

        [Test]
        public void SavingAScene_RestoresTheMeshDuringTheSave_AndResumesThePreviewAfterwards()
        {
            Setup s = MakeSetup();
            s.Entry.PreviewWeight = 100f;
            ShapeKeyPreview.Apply(s.Renderer, s.Data, out _);
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True);

            Mesh meshDuringSave = null;
            bool previewActiveDuringSave = true;
            UnityEngine.SceneManagement.Scene scene = s.Renderer.gameObject.scene;
            // 登録順で ShapeKeyPreview の処理の後に呼ばれる
            EditorSceneManager.SceneSavingCallback onSaving = (saving, path) =>
            {
                meshDuringSave = MeshOf(s.Renderer);
                previewActiveDuringSave = ShapeKeyPreview.IsActive(s.Renderer);
            };
            EditorSceneManager.sceneSaving += onSaving;
            try
            {
                AssetDatabase.CreateFolder("Assets", "GP02_ShapeKeyUiTestTemp");
                string path = TempRoot + "/Saved.unity";
                Assert.That(EditorSceneManager.SaveScene(scene, path, true), Is.True);
            }
            finally
            {
                EditorSceneManager.sceneSaving -= onSaving;
            }

            Assert.That(meshDuringSave, Is.SameAs(s.Source), "保存の間は元のメッシュ（一時メッシュを保存しない）");
            Assert.That(previewActiveDuringSave, Is.False);
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True, "保存が終わったらプレビューを再開する");
            Assert.That(MeshOf(s.Renderer), Is.Not.SameAs(s.Source));
        }
    }
}
