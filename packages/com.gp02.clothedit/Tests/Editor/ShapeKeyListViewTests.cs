using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 一覧の画面（<see cref="ShapeKeyListView"/>）のうち、描画に依存しない部分
    /// （対象の切り替え、プレビューの後始末、選択、行の警告、操作のつなぎ）を確かめる。
    /// </summary>
    public class ShapeKeyListViewTests : ShapeKeyUiTestBase
    {
        readonly List<ShapeKeyListView> _views = new List<ShapeKeyListView>();

        [TearDown]
        public void TearDown()
        {
            foreach (ShapeKeyListView v in _views) v.Dispose();
            _views.Clear();
        }

        sealed class Setup
        {
            public Mesh Source;
            public GP02ShapeKeyData Data;
            public Renderer Renderer;
            public GP02ShapeKeys Component;
            public ShapeKeyEntry A, B, C;
            public ShapeKeyListView View;
        }

        Setup MakeSetup(string name = "Target")
        {
            var s = new Setup { Source = CreateGridMesh(name) };
            int n = s.Source.vertexCount;
            s.A = Entry("A", Ramp(n, 0.002f));
            s.B = Entry("B", Ramp(n, 0.003f));
            s.C = Entry("C", Ramp(n, 0.004f));
            s.Data = NewData(s.Source, s.A, s.B, s.C);
            s.Renderer = CreateRenderer(s.Source, true, name);
            s.Component = ShapeKeyLibrary.Attach(s.Renderer, s.Data);
            s.View = new ShapeKeyListView(null, new ShapeKeyViewSettings());
            _views.Add(s.View);
            s.View.SetTarget(s.Component, s.Renderer);
            s.View.FlushTree();
            return s;
        }

        static Mesh MeshOf(Renderer renderer) => ClothEditController.GetMesh(renderer);

        [Test]
        public void SliderChange_StartsThePreview_AndDisposeRestoresTheMeshAndResetsTheWeights()
        {
            Setup s = MakeSetup();

            s.View.SetPreviewWeight(s.A, 40f);
            Assert.That(s.A.PreviewWeight, Is.EqualTo(40f));
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True);
            Assert.That(MeshOf(s.Renderer), Is.Not.SameAs(s.Source));

            s.View.Dispose();

            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False);
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
            Assert.That(s.A.PreviewWeight, Is.EqualTo(0f), "次に開いたとき、スライダーだけ動いている食い違いを作らない");
        }

        [Test]
        public void ChangingTheTarget_EndsThePreviewOfThePreviousTarget()
        {
            Setup first = MakeSetup("First");
            Setup second = MakeSetup("Second");
            first.View.SetPreviewWeight(first.A, 100f);
            Assert.That(ShapeKeyPreview.IsActive(first.Renderer), Is.True);

            first.View.SetTarget(second.Component, second.Renderer);

            Assert.That(ShapeKeyPreview.IsActive(first.Renderer), Is.False);
            Assert.That(MeshOf(first.Renderer), Is.SameAs(first.Source));
            Assert.That(first.A.PreviewWeight, Is.EqualTo(0f));
            Assert.That(first.View.Data, Is.SameAs(second.Data));
        }

        [Test]
        public void GroupSlider_MovesEveryEntryOfTheGroup()
        {
            Setup s = MakeSetup();
            ShapeKeyListOperations.SetGroup(s.Data, new[] { s.A, s.B }, "Face");

            s.View.SetPreviewWeights(s.View.GetEntriesInGroup("Face"), 70f);

            Assert.That(s.A.PreviewWeight, Is.EqualTo(70f));
            Assert.That(s.B.PreviewWeight, Is.EqualTo(70f));
            Assert.That(s.C.PreviewWeight, Is.EqualTo(0f));
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True);
        }

        [Test]
        public void DisablingTheOnlyPreviewedEntry_EndsThePreview()
        {
            Setup s = MakeSetup();
            s.View.SetPreviewWeight(s.A, 100f);
            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.True);

            s.View.SetEnabled(new[] { s.A }, false);

            Assert.That(ShapeKeyPreview.IsActive(s.Renderer), Is.False, "有効なものの重みが 0 になったので戻る");
            Assert.That(MeshOf(s.Renderer), Is.SameAs(s.Source));
        }

        [Test]
        public void GetProblem_FlagsMismatchedVertexCountsAndEmptyNames()
        {
            Setup s = MakeSetup();
            Assert.That(s.View.GetProblem(s.A, out _), Is.Null);

            var wrong = Entry("Wrong", new Vector3[4]);
            s.Data.Entries.Add(wrong);
            string problem = s.View.GetProblem(wrong, out bool isError);
            Assert.That(problem, Is.Not.Null);
            Assert.That(isError, Is.True, "頂点数が合わないものは赤");

            s.A.Name = "";
            Assert.That(s.View.GetProblem(s.A, out isError), Is.Not.Null);
            Assert.That(isError, Is.True);

            // レンダラーのメッシュが別の頂点数なら、すべての行が赤くなる
            ((SkinnedMeshRenderer)s.Renderer).sharedMesh = Track(new Mesh { name = "Other", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
            Assert.That(s.View.GetProblem(s.B, out isError), Is.Not.Null);
            Assert.That(isError, Is.True);
        }

        [Test]
        public void Tree_ShowsGroupsWithTheirEntriesAndUngroupedEntries()
        {
            Setup s = MakeSetup();
            ShapeKeyListOperations.SetGroup(s.Data, new[] { s.A, s.C }, "Face");
            s.View.MoveEntriesTo(new[] { s.B }, 0, "");      // 構造の変更のあとに一覧を作り直す
            s.View.FlushTree();
            s.View.Tree.ExpandAll();

            Assert.That(s.View.Tree.GetRows().Count, Is.EqualTo(1 + 2 + 1), "グループ1 + その中の2つ + グループなし1つ");
            Assert.That(s.View.SelectedEntries, Is.EqualTo(new[] { s.B }), "動かしたものが選ばれる");
        }

        [Test]
        public void MoveEntriesTo_ReordersChangesTheGroupAndSelectsTheMovedEntries()
        {
            Setup s = MakeSetup();

            s.View.MoveEntriesTo(new[] { s.A }, 3, "G");
            s.View.FlushTree();

            Assert.That(s.Data.Entries, Is.EqualTo(new[] { s.B, s.C, s.A }));
            Assert.That(s.A.Group, Is.EqualTo("G"));
            Assert.That(s.View.SelectedEntries, Is.EqualTo(new[] { s.A }));
        }

        [Test]
        public void DuplicateCopyPasteDelete_WorkOnTheSelection()
        {
            Setup s = MakeSetup();
            s.View.MoveEntriesTo(new[] { s.A, s.B }, 0, "");
            s.View.FlushTree();
            Assert.That(s.View.SelectedEntries.Count, Is.EqualTo(2));

            s.View.DuplicateSelected();
            s.View.FlushTree();
            Assert.That(s.Data.Entries.Count, Is.EqualTo(5));
            Assert.That(s.View.SelectedEntries.ConvertAll(e => e.Name), Is.EqualTo(new[] { "A_copy", "B_copy" }), "複製が選ばれる");

            s.View.CopySelected();
            s.View.Paste();
            s.View.FlushTree();
            Assert.That(s.Data.Entries.Count, Is.EqualTo(7));
            Assert.That(s.View.SelectedEntries.ConvertAll(e => e.Name), Is.EqualTo(new[] { "A_copy (1)", "B_copy (1)" }));

            s.View.DeleteSelected();
            s.View.FlushTree();
            Assert.That(s.Data.Entries.Count, Is.EqualTo(5));
            Assert.That(s.View.SelectedEntries, Is.Empty);
        }

        [Test]
        public void RenameAndRenameGroup_UpdateTheData()
        {
            Setup s = MakeSetup();
            ShapeKeyListOperations.SetGroup(s.Data, new[] { s.A }, "Face");

            s.View.Rename(s.A, "Smile");
            s.View.RenameGroup("Face", "Head");

            Assert.That(s.A.Name, Is.EqualTo("Smile"));
            Assert.That(s.A.Group, Is.EqualTo("Head"));
            Assert.That(s.Data.Groups, Is.EqualTo(new[] { "Head" }));
        }

        [Test]
        public void ViewWithoutAComponent_CanBeCreatedAndDisposedSafely()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            var view = new ShapeKeyListView(null, new ShapeKeyViewSettings());
            _views.Add(view);

            view.SetTarget(null, renderer);
            view.FlushTree();

            Assert.That(view.Data, Is.Null);
            Assert.That(view.SelectedEntries, Is.Empty);
            view.Dispose();
            view.Dispose();
        }

        [Test]
        public void ViewSettings_RoundTripThroughJson()
        {
            var settings = new ShapeKeyViewSettings { Axis = 2, Center = 0.5f, BlendWidthCm = 8f, ExportFolder = "Assets/X" };
            var copy = new ShapeKeyViewSettings();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(settings), copy);

            Assert.That(copy.Axis, Is.EqualTo(2));
            Assert.That(copy.BlendWidth, Is.EqualTo(0.08f).Within(1e-6f));
            Assert.That(copy.ExportFolder, Is.EqualTo("Assets/X"));
            Assert.That(new ShapeKeyViewSettings().Tolerance, Is.GreaterThan(0f));
        }
    }
}
