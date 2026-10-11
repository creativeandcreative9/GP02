using System;
using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using L10n = GP02.EditorTools.L10n;

namespace GP02.Tests
{
    /// <summary>
    /// 布の編集で選んだ点から作るマスク（<see cref="ClothEditController.GetSelectionVertexMask"/>）と、それを使う一覧の操作
    /// （選んだ点で分割・範囲だけ取り出す・範囲を消す）、布で上書き保存したときの履歴、一覧の画面側のつなぎ
    /// （ソロ・焼き込みの印・検索と並べ替え）を確かめる。
    /// </summary>
    public class ShapeKeySelectionRangeTests : ShapeKeyUiTestBase
    {
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        readonly List<ShapeKeyListView> _views = new List<ShapeKeyListView>();
        Func<ClothEditController> _savedProvider;

        [SetUp]
        public void SetUp()
        {
            _savedProvider = ShapeKeyClothEdit.ControllerProvider;
            ShapeKeyClothEdit.ControllerProvider = null;
            ShapeKeyClothEdit.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ShapeKeyListView v in _views) v.Dispose();
            _views.Clear();
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            ShapeKeyClothEdit.ControllerProvider = _savedProvider;
            ShapeKeyClothEdit.Clear();
        }

        ClothEditController Start(Renderer renderer)
        {
            var controller = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 4f });
            _controllers.Add(controller);
            Assert.That(controller.TryStart(renderer, out string error), Is.True, error);
            return controller;
        }

        // 平らな板は XY 平面で、面の表は -Z 側。カメラ（レイの出発点）は -Z 側から +Z に向ける
        static Ray FrontRay(float x, float y) => new Ray(new Vector3(x, y, -1.5f), Vector3.forward);

        static void SelectPatch(ClothEditController c, float x, float y, float radius)
        {
            c.BrushRadius = radius;
            Assert.That(c.SelectPaintBegin(FrontRay(x, y), false), Is.True, "板に当たりません");
            c.SelectPaintEnd();
            Assert.That(c.SelectionCount, Is.GreaterThan(0));
        }

        // ---- マスク ----

        [Test]
        public void GetSelectionVertexMask_IsOneInsideThePatchZeroFarAwayAndPartialAtTheBorder()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            ClothEditController c = Start(renderer);

            float[] none = c.GetSelectionVertexMask(renderer);
            Assert.That(none, Is.Not.Null);
            Assert.That(none.Length, Is.EqualTo(mesh.vertexCount));
            foreach (float m in none) Assert.That(m, Is.EqualTo(0f), "何も選んでいなければ 0");

            SelectPatch(c, 0.07f, 0.14f, 0.04f);
            float[] mask = c.GetSelectionVertexMask(renderer);

            Vector3[] vertices = mesh.vertices;
            bool anyPartial = false;
            for (int v = 0; v < mask.Length; v++)
            {
                Assert.That(mask[v], Is.InRange(0f, 1f));
                float distance = new Vector2(vertices[v].x - 0.07f, vertices[v].y - 0.14f).magnitude;
                if (distance < 0.02f) Assert.That(mask[v], Is.GreaterThan(0.9f), $"選んだ範囲の内側 頂点 {v}");
                if (distance > 0.09f) Assert.That(mask[v], Is.EqualTo(0f), $"遠く 頂点 {v}");
                if (mask[v] > 0.05f && mask[v] < 0.95f) anyPartial = true;
            }
            Assert.That(anyPartial, Is.True, "境目の頂点は途中の値");

            c.SelectAll();
            foreach (float m in c.GetSelectionVertexMask(renderer)) Assert.That(m, Is.GreaterThan(0.999f));
            c.ClearSelection();
            foreach (float m in c.GetSelectionVertexMask(renderer)) Assert.That(m, Is.EqualTo(0f));
        }

        [Test]
        public void GetSelectionVertexMask_IsNullWhenNotEditingOrForARendererThatIsNotBeingEdited()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            Renderer other = CreateRenderer(CreateGridMesh("Other"), false, "Other");
            var idle = new ClothEditController(new ClothEditSettings());
            _controllers.Add(idle);
            Assert.That(idle.GetSelectionVertexMask(renderer), Is.Null, "編集中でない");

            ClothEditController c = Start(renderer);
            Assert.That(c.GetSelectionVertexMask(other), Is.Null, "編集の対象でない");
            Assert.That(c.GetSelectionVertexMask(null), Is.Null);
            c.End();
            Assert.That(c.GetSelectionVertexMask(renderer), Is.Null, "編集を終えた");
        }

        [Test]
        public void TheMaskFromTheSelection_SplitsAShapeKeyIntoTheRangeAndTheRest()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Renderer renderer = CreateRenderer(mesh, false);
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, entry);
            ClothEditController c = Start(renderer);
            SelectPatch(c, 0.07f, 0.14f, 0.04f);
            float[] mask = c.GetSelectionVertexMask(renderer);

            ShapeKeyListOperations.SplitByMask(data, entry, mask, 2, "_Sel", "_Rest", false, out ShapeKeyEntry inside, out ShapeKeyEntry outside);

            Vector3[] vertices = mesh.vertices;
            for (int v = 0; v < n; v++)
            {
                Vector3 sum = inside.Frames[0].DeltaVertices[v] + outside.Frames[0].DeltaVertices[v];
                Assert.That(Vector3.Distance(sum, entry.Frames[0].DeltaVertices[v]), Is.LessThan(1e-7f));
                float distance = new Vector2(vertices[v].x - 0.07f, vertices[v].y - 0.14f).magnitude;
                if (distance > 0.12f) Assert.That(inside.Frames[0].DeltaVertices[v], Is.EqualTo(Vector3.zero), "遠くの頂点は範囲の外");
            }
            Assert.That(inside.Frames[0].DeltaVertices[7 * Stride + 3].magnitude, Is.GreaterThan(0f));
        }

        [Test]
        public void ListView_SplitExtractAndRemoveBySelection_UseTheClothEditSelection_AndExplainWhenThereIsNone()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Renderer renderer = CreateRenderer(mesh, false);
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, entry);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            ShapeKeyListView view = MakeView(component, renderer);
            view.Tree.SelectEntries(new[] { entry });

            // 編集中でなければ何も変わらない
            view.SplitBySelection();
            view.ApplyRange(ShapeKeyMaskOperation.Extract);
            Assert.That(data.Entries.Count, Is.EqualTo(1));

            ClothEditController c = Start(renderer);
            ShapeKeyClothEdit.ControllerProvider = () => c;
            view.SplitBySelection();
            Assert.That(data.Entries.Count, Is.EqualTo(1), "点を選んでいなければ何も変わらない");

            SelectPatch(c, 0.07f, 0.14f, 0.04f);
            view.Settings.MaskSmoothIterations = 1;
            view.SplitBySelection();
            view.FlushTree();
            Assert.That(data.Entries.ConvertAll(e => e.Name), Is.EqualTo(new[] { "Lift", "Lift_Sel", "Lift_Rest" }));
            Assert.That(data.Entries[0].Enabled, Is.False, "元は無効になる（設定の既定）");

            view.Tree.SelectEntries(new[] { data.Entries[0] });
            view.Settings.MaskAsCopy = true;
            view.ApplyRange(ShapeKeyMaskOperation.Remove);
            view.FlushTree();
            Assert.That(data.Entries.Count, Is.EqualTo(4));
            Assert.That(data.Entries.Exists(e => e.Name == "Lift_Rest (1)" || e.Name == "Lift_Rest"), Is.True);
        }

        // ---- 布で上書き保存したときの履歴 ----

        [Test]
        public void OverwritingFromTheClothEdit_KeepsThePreviousShapeInTheHistory()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Renderer renderer = CreateRenderer(mesh, false);
            ShapeKeyEntry entry = Entry("Bump", Ramp(n, 0.001f));
            GP02ShapeKeyData data = NewData(mesh, entry);
            ClothEditController c = Start(renderer);
            c.LoadDeltasAsOperation(new Vector3[n]);
            Vector3[] big = new Vector3[n];
            for (int v = 0; v < n; v++) big[v] = new Vector3(0f, 0f, -0.03f);
            c.LoadDeltasAsOperation(big);

            ShapeKeyEntry saved = c.SaveToData(data, "Bump", true);

            Assert.That(saved, Is.SameAs(entry));
            Assert.That(entry.History.Count, Is.EqualTo(1));
            AssertSame(Ramp(n, 0.001f), entry.History[0].Frames[0].DeltaVertices, 0f);
            Assert.That(entry.History[0].Reason, Is.EqualTo(L10n.Tr("布で編集して上書き", "Overwritten by cloth edit")));
            Assert.That(entry.History[0].SavedAt, Is.Not.Empty);

            // 同じ形でもう一度保存しても、履歴は増えない
            c.SaveToData(data, "Bump", true);
            Assert.That(entry.History.Count, Is.EqualTo(1));
        }

        // ---- 一覧の画面側のつなぎ ----

        ShapeKeyListView MakeView(GP02ShapeKeys component, Renderer renderer)
        {
            var view = new ShapeKeyListView(null, new ShapeKeyViewSettings());
            _views.Add(view);
            view.SetTarget(component, renderer);
            view.FlushTree();
            return view;
        }

        Mesh PreviewVertices(Renderer renderer, out Vector3[] vertices)
        {
            Mesh temp = ShapeKeyPreview.GetTempMesh(renderer);
            Assert.That(temp, Is.Not.Null, "プレビュー中のはず");
            vertices = temp.vertices;
            return temp;
        }

        [Test]
        public void Solo_ShowsOnlyThatShapeKey_EvenWhenAllWeightsAreZero_AndLeavesTheWeightsAlone()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] rest = mesh.vertices;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.002f)), b = Entry("B", Ramp(n, 0.003f));
            GP02ShapeKeyData data = NewData(mesh, a, b);
            Renderer renderer = CreateRenderer(mesh, true);
            ShapeKeyListView view = MakeView(ShapeKeyLibrary.Attach(renderer, data), renderer);

            view.SetPreviewWeight(b, 40f);
            PreviewVertices(renderer, out Vector3[] shown);
            Assert.That(Vector3.Distance(shown[3], rest[3] + b.Frames[0].DeltaVertices[3] * 0.4f), Is.LessThan(1e-6f), "ふつうのプレビュー");

            view.SetSolo(a);
            Assert.That(view.IsSolo(a), Is.True);
            Assert.That(view.IsSolo(b), Is.False);
            PreviewVertices(renderer, out shown);
            Assert.That(Vector3.Distance(shown[3], rest[3] + a.Frames[0].DeltaVertices[3]), Is.LessThan(1e-6f), "A だけを、スライダーが 0 なので 100 で見せる");
            Assert.That(b.PreviewWeight, Is.EqualTo(40f), "他の重みは変えない");
            Assert.That(a.PreviewWeight, Is.EqualTo(0f));

            // ソロ中に重みを動かしても、ソロは続く（見せるのは A だけ）
            view.SetPreviewWeight(a, 50f);
            PreviewVertices(renderer, out shown);
            Assert.That(Vector3.Distance(shown[3], rest[3] + a.Frames[0].DeltaVertices[3] * 0.5f), Is.LessThan(1e-6f));

            view.SetSolo(b);
            PreviewVertices(renderer, out shown);
            Assert.That(Vector3.Distance(shown[3], rest[3] + b.Frames[0].DeltaVertices[3] * 0.4f), Is.LessThan(1e-6f));

            view.SetSolo(null);
            Assert.That(view.IsSolo(b), Is.False);
            PreviewVertices(renderer, out shown);
            Vector3 both = rest[3] + a.Frames[0].DeltaVertices[3] * 0.5f + b.Frames[0].DeltaVertices[3] * 0.4f;
            Assert.That(Vector3.Distance(shown[3], both), Is.LessThan(1e-6f), "ソロをやめると、重みどおり");

            view.SetPreviewWeight(a, 0f);
            view.SetPreviewWeight(b, 0f);
            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.False);
            view.SetSolo(a);
            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.True, "重みが全部 0 でも、ソロなら見せる");
            view.SetSolo(null);
            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.False, "ソロをやめて重みが全部 0 なら、プレビューも終わる");
            Assert.That(ClothEditController.GetMesh(renderer), Is.SameAs(mesh));
        }

        [Test]
        public void SoloEndsWithThePreview_AndTheSoloEntryIsShownEvenIfDisabled()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.002f));
            a.Enabled = false;
            GP02ShapeKeyData data = NewData(mesh, a);
            Renderer renderer = CreateRenderer(mesh, true);
            ShapeKeyListView view = MakeView(ShapeKeyLibrary.Attach(renderer, data), renderer);

            view.SetSolo(a);
            Assert.That(ShapeKeyPreview.GetSolo(renderer), Is.SameAs(a));
            PreviewVertices(renderer, out Vector3[] shown);
            Assert.That(Vector3.Distance(shown[3], mesh.vertices[3] + a.Frames[0].DeltaVertices[3]), Is.LessThan(1e-6f), "無効でも、ソロなら確かめられる");

            view.StopPreview();
            Assert.That(ShapeKeyPreview.GetSolo(renderer), Is.Null);
            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.False);
        }

        [Test]
        public void SetPrebakeFromTheView_MarksTheSelectionAndIsUndoable()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.002f)), b = Entry("B", Ramp(n, 0.003f));
            GP02ShapeKeyData data = NewData(mesh, a, b);
            Renderer renderer = CreateRenderer(mesh, true);
            ShapeKeyListView view = MakeView(ShapeKeyLibrary.Attach(renderer, data), renderer);

            NextUndoGroup();
            view.SetPrebake(new[] { a, b }, true);
            NextUndoGroup();
            Assert.That(a.Prebake && b.Prebake, Is.True);

            Undo.PerformUndo();
            Assert.That(data.Entries.TrueForAll(e => !e.Prebake), Is.True);
        }

        [Test]
        public void FilterAndSort_ChangeTheDisplayOnlyAndTurnOffDragReordering()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            GP02ShapeKeyData data = NewData(mesh, Entry("Mouth_Open", Ramp(n, 0.002f)), Entry("Brow_Up", Ramp(n, 0.002f)), Entry("Mouth_Wide", Ramp(n, 0.002f)));
            ShapeKeyLibrary.StampCreated(data.Entries[2]);
            Renderer renderer = CreateRenderer(mesh, true);
            ShapeKeyListView view = MakeView(ShapeKeyLibrary.Attach(renderer, data), renderer);
            Assert.That(view.IsReorderAllowed, Is.True);

            List<string> Rows()
            {
                view.Tree.Reload();
                var names = new List<string>();
                foreach (var row in view.Tree.GetRows()) names.Add(row.displayName);
                return names;
            }

            Assert.That(Rows(), Is.EqualTo(new[] { "Mouth_Open", "Brow_Up", "Mouth_Wide" }));

            view.Settings.FilterText = "mouth";
            Assert.That(Rows(), Is.EqualTo(new[] { "Mouth_Open", "Mouth_Wide" }));
            Assert.That(view.IsReorderAllowed, Is.False);
            view.Settings.FilterText = "mouth wide";
            Assert.That(Rows(), Is.EqualTo(new[] { "Mouth_Wide" }));

            view.Settings.FilterText = "";
            view.Settings.Sort = ShapeKeyListSort.Name;
            Assert.That(Rows(), Is.EqualTo(new[] { "Brow_Up", "Mouth_Open", "Mouth_Wide" }));
            Assert.That(view.IsReorderAllowed, Is.False);
            Assert.That(data.Entries[0].Name, Is.EqualTo("Mouth_Open"), "データの並びは変わらない");

            // 表示の順でも、選んだ行は正しいシェイプキーを指す
            view.Tree.SelectEntries(new[] { data.Entries[1] });
            Assert.That(view.SelectedEntries[0].Name, Is.EqualTo("Brow_Up"));

            view.Settings.Sort = ShapeKeyListSort.DataOrder;
            Assert.That(view.IsReorderAllowed, Is.True);
        }
    }
}
