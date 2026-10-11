using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// メッシュへの書き出し・素体への焼き込み・元に戻す（<see cref="ShapeKeyExporter"/>）と、データの保護
    /// （<see cref="ShapeKeyProtection"/>）を確かめる。作ったアセットは TearDown で消す。
    /// </summary>
    public class ShapeKeyExporterTests : ShapeKeyUiTestBase
    {
        sealed class Setup
        {
            public Mesh Source;
            public GP02ShapeKeyData Data;
            public Renderer Renderer;
            public GP02ShapeKeys Component;
        }

        Setup MakeSetup(bool skinned = true, params ShapeKeyEntry[] entries)
        {
            var s = new Setup { Source = CreateGridMesh("Skirt") };
            int n = s.Source.vertexCount;
            s.Source.AddBlendShapeFrame("Existing", 100f, Ramp(n, 0.001f), null, null);
            if (entries.Length == 0) entries = new[] { Entry("Lift", Ramp(n, 0.003f)) };
            s.Data = NewData(s.Source, entries);
            s.Renderer = CreateRenderer(s.Source, skinned);
            s.Component = ShapeKeyLibrary.Attach(s.Renderer, s.Data);
            return s;
        }

        static Mesh MeshOf(Renderer renderer) => ClothEditController.GetMesh(renderer);

        // ---- メッシュに書き出す ----

        [TestCase(true)]
        [TestCase(false)]
        public void ExportToMesh_SavesAnAssetAssignsItAndKeepsTheData(bool skinned)
        {
            Setup s = MakeSetup(skinned);
            ShapeKeyEntry off = new ShapeKeyEntry { Name = "Off", Enabled = false };
            off.Frames.Add(new ShapeKeyFrame { DeltaVertices = Ramp(s.Source.vertexCount, 0.01f) });
            s.Data.Entries.Add(off);

            NextUndoGroup();
            Mesh result = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);

            string path = AssetDatabase.GetAssetPath(result);
            Assert.That(path, Does.StartWith(MeshFolder + "/"));
            Assert.That(path, Does.EndWith(".asset"));
            Assert.That(result.name, Is.EqualTo("Skirt_GP02"));
            Assert.That(MeshOf(s.Renderer), Is.SameAs(result));
            Assert.That(result.blendShapeCount, Is.EqualTo(2), "元の BlendShape と、有効なシェイプキー");
            Assert.That(result.GetBlendShapeName(0), Is.EqualTo("Existing"));
            Assert.That(result.GetBlendShapeName(1), Is.EqualTo("Lift"));
            AssertSame(Ramp(result.vertexCount, 0.003f), FrameVertices(result, 1, 0), 1e-6f);

            Assert.That(s.Source.blendShapeCount, Is.EqualTo(1), "元のメッシュは変わらない");
            Assert.That(s.Component.ExportedMesh, Is.SameAs(result));
            Assert.That(s.Component.SourceMesh, Is.SameAs(s.Source));
            Assert.That(s.Data.Entries.Count, Is.EqualTo(2), "データは残る");
        }

        [Test]
        public void ExportToMesh_ExportingAgainOverwritesTheSameAssetInsteadOfAddingMore()
        {
            Setup s = MakeSetup();
            Mesh first = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            string path = AssetDatabase.GetAssetPath(first);
            string guid = AssetDatabase.AssetPathToGUID(path);

            // データを直して書き出し直す
            s.Data.Entries[0].Frames[0].DeltaVertices = Ramp(s.Source.vertexCount, 0.006f);
            s.Data.Entries.Add(Entry("Extra", Ramp(s.Source.vertexCount, 0.002f)));
            Mesh second = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);

            Assert.That(second, Is.SameAs(first), "同じアセット");
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(second)), Is.EqualTo(guid));
            Assert.That(MeshOf(s.Renderer), Is.SameAs(second));
            Assert.That(second.blendShapeCount, Is.EqualTo(3));
            AssertSame(Ramp(second.vertexCount, 0.006f), FrameVertices(second, 1, 0), 1e-6f, "直した内容");
            Assert.That(AssetDatabase.FindAssets("t:Mesh", new[] { MeshFolder }).Length, Is.EqualTo(1), "アセットは増えない");
        }

        [Test]
        public void ExportToMesh_OverwriteSameNameReplacesTheShapeInPlace_OtherwiseAddsANumberedName()
        {
            Setup s = MakeSetup();
            int n = s.Source.vertexCount;
            // 元のメッシュに同じ名前 "Lift" がある場合
            s.Source.AddBlendShapeFrame("Lift", 100f, Ramp(n, 0.009f), null, null);

            Mesh replaced = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(replaced.blendShapeCount, Is.EqualTo(2));
            AssertSame(Ramp(n, 0.003f), FrameVertices(replaced, replaced.GetBlendShapeIndex("Lift"), 0), 1e-6f, "上書き");

            Mesh numbered = ShapeKeyExporter.ExportToMesh(s.Component, false, MeshFolder);
            Assert.That(numbered.blendShapeCount, Is.EqualTo(3));
            Assert.That(numbered.GetBlendShapeIndex("Lift (1)"), Is.GreaterThanOrEqualTo(0));
            AssertSame(Ramp(n, 0.009f), FrameVertices(numbered, numbered.GetBlendShapeIndex("Lift"), 0), 1e-6f, "元の Lift はそのまま");
        }

        [Test]
        public void ExportToMesh_UndoGoesBackToTheSourceMesh()
        {
            Setup s = MakeSetup();
            NextUndoGroup();
            Mesh result = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(result));

            Undo.PerformUndo();

            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Assert.That(s.Component.ExportedMesh, Is.Null);
            Assert.That(s.Data.Entries.Count, Is.EqualTo(1), "データは残る");
        }

        [Test]
        public void ExportToMesh_UndoOfAnOverwriteRestoresThePreviousContents()
        {
            Setup s = MakeSetup();
            Mesh first = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(first.blendShapeCount, Is.EqualTo(2));

            s.Data.Entries.Add(Entry("Extra", Ramp(s.Source.vertexCount, 0.002f)));
            NextUndoGroup();
            ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(first.blendShapeCount, Is.EqualTo(3));

            Undo.PerformUndo();

            Assert.That(first.blendShapeCount, Is.EqualTo(2), "上書きの前の内容に戻る");
            Assert.That(MeshOf(s.Renderer), Is.SameAs(first));
        }

        [Test]
        public void ExportToMesh_WorksWhenTheRendererMeshIsNone()
        {
            Setup s = MakeSetup();
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = null;

            Mesh result = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);

            Assert.That(MeshOf(s.Renderer), Is.SameAs(result), "None のメッシュも直る");
        }

        [Test]
        public void ExportToMesh_KeepsTheExportedMeshWhenTheDataAssetIsDeleted()
        {
            Mesh source = CreateGridMesh("Dress");
            GP02ShapeKeyData data = ShapeKeyLibrary.CreateData(source, DataFolder);
            data.Entries.Add(Entry("Lift", Ramp(source.vertexCount, 0.003f)));
            EditorUtility.SetDirty(data);
            Renderer renderer = CreateRenderer(source, true);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);

            Mesh result = ShapeKeyExporter.ExportToMesh(component, true, MeshFolder);
            string meshPath = AssetDatabase.GetAssetPath(result);
            Assert.That(AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(data)), Is.True);

            Mesh loaded = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            Assert.That(loaded, Is.Not.Null, "書き出したメッシュはアセットとして残る");
            Assert.That(loaded.blendShapeCount, Is.EqualTo(1));
            Assert.That(loaded.GetBlendShapeName(0), Is.EqualTo("Lift"));
            Assert.That(component.Data == null, Is.True);
        }

        [Test]
        public void ExportToMesh_RefusesWhenItCannotExport()
        {
            Setup s = MakeSetup();
            Assert.That(ShapeKeyExporter.GetProblem(s.Component), Is.Null);

            s.Data.VertexCount = 7;
            Assert.That(ShapeKeyExporter.GetProblem(s.Component), Is.Not.Null);
            Assert.Throws<System.InvalidOperationException>(() => ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder));

            s.Data.VertexCount = s.Source.vertexCount;
            s.Component.Data = null;
            Assert.That(ShapeKeyExporter.GetProblem(s.Component), Is.Not.Null);
            Assert.That(ShapeKeyExporter.GetProblem(null), Is.Not.Null);
            Assert.That(AssetDatabase.IsValidFolder(MeshFolder), Is.False, "失敗したときはアセットを作らない");
        }

        // ---- 素体への焼き込み ----

        [Test]
        public void BakeIntoBase_BakesSelectedEntriesWithWeights_KeepsSourceAndData_AndUndoRestores()
        {
            Mesh source = CreateGridMesh("Body");
            int n = source.vertexCount;
            Vector3[] d1 = Ramp(n, 0.003f), d2 = Ramp(n, 0.002f);
            ShapeKeyEntry e1 = Entry("One", d1), e2 = Entry("Two", d2), skipped = Entry("Skipped", Ramp(n, 0.05f));
            GP02ShapeKeyData data = NewData(source, e1, e2, skipped);
            Renderer renderer = CreateRenderer(source, true);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            Vector3[] sourceVertices = source.vertices;

            NextUndoGroup();
            Mesh baked = ShapeKeyExporter.BakeIntoBase(component, new[] { e1, e2 }, new[] { 100f, 50f }, false, 0f, MeshFolder);

            Assert.That(AssetDatabase.GetAssetPath(baked), Does.StartWith(MeshFolder + "/"));
            Assert.That(baked.name, Is.EqualTo("Body_Baked"));
            Assert.That(MeshOf(renderer), Is.SameAs(baked));
            Vector3[] bakedVertices = baked.vertices;
            for (int v = 0; v < n; v++)
            {
                Assert.That(Vector3.Distance(bakedVertices[v], sourceVertices[v] + d1[v] + d2[v] * 0.5f), Is.LessThan(1e-5f), $"頂点 {v}");
            }
            AssertSame(sourceVertices, source.vertices, 0f, "元のメッシュは変わらない");
            Assert.That(data.Entries.Count, Is.EqualTo(3), "データは残る");
            Assert.That(component.ExportedMesh, Is.Null, "焼き込みは書き出しの記録を変えない");

            Undo.PerformUndo();
            Assert.That(MeshOf(renderer), Is.SameAs(source));
        }

        [Test]
        public void BakeIntoBase_CanRemapBoneWeightsOfFarMovedVertices()
        {
            Mesh source = CreateGridMesh("Cloth");
            int n = source.vertexCount;
            var weights = new BoneWeight[n];
            for (int v = 0; v < n; v++) weights[v] = new BoneWeight { boneIndex0 = v / Stride > Cells / 2 ? 1 : 0, weight0 = 1f };
            source.boneWeights = weights;
            source.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            // 一番上の行を、下の行（ボーン 0）の位置まで下げる
            var deltas = new Vector3[n];
            for (int c = 0; c <= Cells; c++) deltas[Cells * Stride + c] = new Vector3(0f, -Size * 0.95f, 0f);
            ShapeKeyEntry shorten = Entry("Shorten", deltas);
            GP02ShapeKeyData data = NewData(source, shorten);
            Renderer renderer = CreateRenderer(source, true);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);

            Mesh kept = ShapeKeyExporter.BakeIntoBase(component, new[] { shorten }, new[] { 100f }, false, 0f, MeshFolder);
            Assert.That(kept.boneWeights[Cells * Stride].boneIndex0, Is.EqualTo(1), "付け替えなしなら元のウェイト");

            Mesh remapped = ShapeKeyExporter.BakeIntoBase(component, new[] { shorten }, new[] { 100f }, true, 0.05f, MeshFolder);
            Assert.That(remapped.boneWeights[Cells * Stride].boneIndex0, Is.EqualTo(0), "動いた先の頂点のウェイトになる");
            Assert.That(remapped.boneWeights[0].boneIndex0, Is.EqualTo(0));
            Assert.That(source.boneWeights[Cells * Stride].boneIndex0, Is.EqualTo(1), "元のメッシュのウェイトは変わらない");
            Assert.That(remapped.vertices[Cells * Stride].y, Is.EqualTo(Size - Size * 0.95f).Within(1e-5f));
            Assert.That(AssetDatabase.FindAssets("t:Mesh", new[] { MeshFolder }).Length, Is.EqualTo(2), "途中のメッシュはアセットに残らない");
        }

        [Test]
        public void BakeIntoBase_RejectsAnEmptySelection()
        {
            Setup s = MakeSetup();
            Assert.Throws<System.ArgumentException>(() => ShapeKeyExporter.BakeIntoBase(s.Component, new ShapeKeyEntry[0], null, false, 0f, MeshFolder));
        }

        // ---- 元のメッシュに戻す ----

        [Test]
        public void RestoreSourceMesh_PutsTheSourceMeshBack_AndUndoReturnsToTheExportedOne()
        {
            Setup s = MakeSetup();
            Mesh exported = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(ShapeKeyExporter.GetMeshState(s.Component, s.Renderer), Is.EqualTo(MeshState.Exported));

            NextUndoGroup();
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(s.Component), Is.True);
            NextUndoGroup();
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Assert.That(ShapeKeyExporter.GetMeshState(s.Component, s.Renderer), Is.EqualTo(MeshState.Source));
            Assert.That(s.Data.Entries.Count, Is.EqualTo(1), "データは残る");

            Undo.PerformUndo();
            Assert.That(MeshOf(s.Renderer), Is.SameAs(exported));

            // 何度でも書き出し直せる
            ShapeKeyExporter.RestoreSourceMesh(s.Component);
            Mesh again = ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(again, Is.SameAs(exported));
        }

        [Test]
        public void RestoreSourceMesh_FixesANoneMesh_AndRefusesWhenTheSourceIsUnknown()
        {
            Setup s = MakeSetup();
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = null;
            Assert.That(ShapeKeyExporter.GetMeshState(s.Component, s.Renderer), Is.EqualTo(MeshState.None));

            Assert.That(ShapeKeyExporter.RestoreSourceMesh(s.Component), Is.True);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));

            s.Component.SourceMesh = null;
            s.Data.SourceMesh = null;
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(s.Component), Is.False);
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(null), Is.False);
        }

        [Test]
        public void GetMeshState_DistinguishesSourceExportedAndOther()
        {
            Setup s = MakeSetup();
            Assert.That(ShapeKeyExporter.GetMeshState(s.Component, s.Renderer), Is.EqualTo(MeshState.Source));
            ShapeKeyExporter.ExportToMesh(s.Component, true, MeshFolder);
            Assert.That(ShapeKeyExporter.GetMeshState(s.Component, s.Renderer), Is.EqualTo(MeshState.Exported));
            ShapeKeyExporter.BakeIntoBase(s.Component, s.Data.Entries, new[] { 100f }, false, 0f, MeshFolder);
            Assert.That(ShapeKeyExporter.GetMeshState(s.Component, s.Renderer), Is.EqualTo(MeshState.Other));
        }

        // ---- データの保護 ----

        [Test]
        public void Diagnose_FindsMissingDataMeshesAndMismatches()
        {
            Setup s = MakeSetup();
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, s.Renderer), Is.EqualTo(ShapeKeyProtection.Issue.None));

            // データが消された
            s.Component.Data = null;
            ShapeKeyProtection.Issue noData = ShapeKeyProtection.Diagnose(s.Component, s.Renderer);
            Assert.That(noData & ShapeKeyProtection.Issue.DataMissing, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));
            Assert.That(ShapeKeyProtection.Diagnose(null, s.Renderer) & ShapeKeyProtection.Issue.DataMissing, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));
            s.Component.Data = s.Data;

            // レンダラーのメッシュが None
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = null;
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, s.Renderer) & ShapeKeyProtection.Issue.RendererMeshMissing, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));

            // 頂点数が違うメッシュ
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = Track(new Mesh { name = "Other", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, s.Renderer) & ShapeKeyProtection.Issue.VertexCountMismatch, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = s.Source;

            // 元のメッシュが消えた／食い違う
            Mesh differs = CreateGridMesh("Other2");
            s.Component.SourceMesh = differs;
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, s.Renderer) & ShapeKeyProtection.Issue.SourceMeshDiffers, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));
            ShapeKeyProtection.SyncComponentSource(s.Component);
            Assert.That(s.Component.SourceMesh, Is.SameAs(s.Source));

            s.Data.SourceMesh = null;
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, s.Renderer) & ShapeKeyProtection.Issue.SourceMeshMissing, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, null) & ShapeKeyProtection.Issue.NoRenderer, Is.Not.EqualTo(ShapeKeyProtection.Issue.None));
        }

        [Test]
        public void AdoptRendererMeshAsSource_RepairsAMissingSourceMesh_OnlyForTheSameVertexCount()
        {
            Setup s = MakeSetup();
            s.Data.SourceMesh = null;
            s.Component.SourceMesh = null;

            NextUndoGroup();
            Assert.That(ShapeKeyProtection.AdoptRendererMeshAsSource(s.Component, s.Renderer), Is.True);
            NextUndoGroup();
            Assert.That(s.Data.SourceMesh, Is.SameAs(s.Source));
            Assert.That(s.Component.SourceMesh, Is.SameAs(s.Source));
            Assert.That(ShapeKeyProtection.Diagnose(s.Component, s.Renderer), Is.EqualTo(ShapeKeyProtection.Issue.None));

            s.Data.SourceMesh = null;
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = Track(new Mesh { name = "Other", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
            Assert.That(ShapeKeyProtection.AdoptRendererMeshAsSource(s.Component, s.Renderer), Is.False);
            Assert.That(s.Data.SourceMesh, Is.Null);
        }

        [Test]
        public void SetData_ReattachesDataAndFillsAnEmptySourceMesh()
        {
            Setup s = MakeSetup();
            s.Component.Data = null;
            s.Component.SourceMesh = null;

            NextUndoGroup();
            ShapeKeyProtection.SetData(s.Component, s.Data);
            NextUndoGroup();

            Assert.That(s.Component.Data, Is.SameAs(s.Data));
            Assert.That(s.Component.SourceMesh, Is.SameAs(s.Source));
            Assert.That(ShapeKeyProtection.CanRestoreMesh(s.Component), Is.True);

            Undo.PerformUndo();
            Assert.That(s.Component.Data == null, Is.True);
        }
    }
}
