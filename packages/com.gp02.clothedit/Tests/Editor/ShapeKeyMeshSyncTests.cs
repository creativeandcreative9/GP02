using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// シェイプキーの一覧の「メッシュに反映済み」（<see cref="ShapeKeyMeshSync"/>）を確かめる。
    /// 布の編集の「シェイプキーとして保存」はデータとメッシュの両方に入れるので、保存した直後は反映済みになること。
    /// データを変える（形・有効・名前の前置き・削除・素体に焼き込む印）と違いが数えられ、「メッシュに反映」で反映済みに戻ること。
    /// </summary>
    public class ShapeKeyMeshSyncTests : ShapeKeyUiTestBase
    {
        static ShapeKeyMeshStatus Status(GP02ShapeKeys component, Renderer renderer) =>
            ShapeKeyMeshSync.Compare(component, renderer, ShapeKeyBuildOptions.FromComponent(component));

        static int Count(ShapeKeyMeshStatus status, ShapeKeyChangeKind kind)
        {
            int count = 0;
            foreach (ShapeKeyChange change in status.Changes) count += change.Kind == kind ? 1 : 0;
            return count;
        }

        [Test]
        public void ClothSave_PutsTheShapeKeyIntoTheDataAndTheMesh_SoItIsAlreadyApplied()
        {
            Mesh mesh = CreateGridMesh("SyncCloth");
            Renderer renderer = CreateRenderer(mesh, true, "SyncCloth");
            var c = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 4f }) { SaveFolder = MeshFolder, DataFolder = DataFolder };
            try
            {
                Assert.That(c.TryStart(renderer, out string error), Is.True, error);
                // 中央を盛り上げた形にして保存する
                Vector3[] vertices = mesh.vertices;
                var bump = new Vector3[vertices.Length];
                for (int v = 0; v < bump.Length; v++)
                {
                    Vector2 p = new Vector2(vertices[v].x, vertices[v].y) - new Vector2(Size / 2f, Size / 2f);
                    bump[v] = new Vector3(0f, 0f, -0.04f * Mathf.Exp(-p.sqrMagnitude / 0.01f));
                }
                Assert.That(c.LoadDeltasAsOperation(bump), Is.True);
                NextUndoGroup();
                Assert.That(c.Save("GP02_1", out string message), Is.True, message);
                NextUndoGroup();
            }
            finally
            {
                c.Dispose();
            }

            var component = renderer.GetComponent<GP02ShapeKeys>();
            Assert.That(component != null && component.Data != null, Is.True, "保存でデータを作っていない");
            Mesh saved = ClothEditController.GetMesh(renderer);
            Assert.That(saved, Is.Not.SameAs(mesh), "保存で、シェイプキーの入ったメッシュに差し替える");
            Assert.That(saved.GetBlendShapeIndex("GP02_1"), Is.GreaterThanOrEqualTo(0));

            // 書き出しの計画は「追加 1」（元のメッシュから作り直すので）だが、今のメッシュには同じものが入っている
            List<ExportPlanItem> plan = ShapeKeyExporter.GetPlan(component, ShapeKeyBuildOptions.FromComponent(component));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan[0].Action, Is.EqualTo(ExportAction.Add));
            ShapeKeyMeshStatus status = Status(component, renderer);
            Assert.That(status.Problem, Is.Null);
            Assert.That(status.ChangeCount, Is.EqualTo(0), "保存した直後なのに、メッシュと違うと出た: " + status.Describe());
            Assert.That(status.InSync, Is.True);
            Assert.That(status.Describe(), Is.Empty);

            // データの形を変える（量を半分にする）と、置き換わる 1 件
            ShapeKeyListOperations.Scale(component.Data, new[] { component.Data.Entries[0] }, 0.5f);
            status = Status(component, renderer);
            Assert.That(status.ChangeCount, Is.EqualTo(1));
            Assert.That(Count(status, ShapeKeyChangeKind.Update), Is.EqualTo(1));
            StringAssert.Contains("GP02_1", status.Describe());

            // メッシュに反映すると、反映済みに戻る
            ShapeKeyExporter.ExportToMesh(component, ShapeKeyBuildOptions.FromComponent(component), MeshFolder);
            Assert.That(Status(component, renderer).InSync, Is.True);
        }

        [Test]
        public void Differences_AreCountedByKind_AndApplyingClearsThem()
        {
            Mesh mesh = CreateGridMesh("SyncTarget");
            int n = mesh.vertexCount;
            // 元のメッシュの BlendShape は、書き出しでもそのまま残る（データに無くても「無くなる」ではない）
            mesh.AddBlendShapeFrame("Original", 100f, Ramp(n, 0.001f), null, null);
            GP02ShapeKeyData data = NewData(mesh, Entry("A", Ramp(n, 0.002f)), Entry("B", Ramp(n, 0.003f)));
            Renderer renderer = CreateRenderer(mesh, true, "SyncTarget");
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);

            // 元のメッシュのまま: A と B が加わる
            ShapeKeyMeshStatus status = Status(component, renderer);
            Assert.That(status.Problem, Is.Null);
            Assert.That(Count(status, ShapeKeyChangeKind.Add), Is.EqualTo(2));
            Assert.That(status.ChangeCount, Is.EqualTo(2));

            ShapeKeyExporter.ExportToMesh(component, ShapeKeyBuildOptions.FromComponent(component), MeshFolder);
            Assert.That(Status(component, renderer).InSync, Is.True);

            // 無効にすると、メッシュから無くなる
            data.Entries[1].Enabled = false;
            status = Status(component, renderer);
            Assert.That(status.ChangeCount, Is.EqualTo(1));
            Assert.That(Count(status, ShapeKeyChangeKind.Remove), Is.EqualTo(1));
            data.Entries[1].Enabled = true;

            // 名前の前置きを付けると、名前が変わる（加わる 2・無くなる 2）
            component.NamePrefix = "GP02_";
            status = Status(component, renderer);
            Assert.That(Count(status, ShapeKeyChangeKind.Add), Is.EqualTo(2));
            Assert.That(Count(status, ShapeKeyChangeKind.Remove), Is.EqualTo(2));
            component.NamePrefix = "";
            Assert.That(Status(component, renderer).InSync, Is.True);

            // 素体に焼き込む印: B は BlendShape でなくなり、素体の形が変わる
            data.Entries[1].Prebake = true;
            data.Entries[1].PrebakeWeight = 100f;
            status = Status(component, renderer);
            Assert.That(status.BaseDiffers, Is.True);
            Assert.That(Count(status, ShapeKeyChangeKind.Remove), Is.EqualTo(1));
            Assert.That(status.ChangeCount, Is.EqualTo(2));
            ShapeKeyExporter.ExportToMesh(component, ShapeKeyBuildOptions.FromComponent(component), MeshFolder);
            status = Status(component, renderer);
            Assert.That(status.InSync, Is.True, status.Describe());

            // 元のメッシュに戻すと、焼き込みと A が無くなったと分かる
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(component), Is.True);
            status = Status(component, renderer);
            Assert.That(status.BaseDiffers, Is.True);
            Assert.That(Count(status, ShapeKeyChangeKind.Add), Is.EqualTo(1));

            // 頂点数の合わないデータは比べない
            data.VertexCount = n + 1;
            Assert.That(Status(component, renderer).Problem, Is.Not.Null);
        }
    }
}
