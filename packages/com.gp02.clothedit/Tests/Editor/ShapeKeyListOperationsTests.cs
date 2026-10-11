using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// シェイプキーの一覧の操作（<see cref="ShapeKeyListOperations"/>）を、画面なしで確かめる。
    /// データを書き換える操作は、Unity の Undo で元に戻ることも確かめる。
    /// </summary>
    public class ShapeKeyListOperationsTests : ShapeKeyUiTestBase
    {
        const float Center = Size / 2f;

        static string[] Names(GP02ShapeKeyData data)
        {
            var names = new string[data.Entries.Count];
            for (int i = 0; i < names.Length; i++) names[i] = data.Entries[i].Name;
            return names;
        }

        // ---- 複製・コピー・貼り付け・削除 ----

        [Test]
        public void Duplicate_InsertsCopiesRightAfterTheOriginalsWithUniqueNames_AndUndoRemovesThem()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.002f)), b = Entry("B", Ramp(n, 0.003f));
            GP02ShapeKeyData data = NewData(mesh, a, b);

            NextUndoGroup();
            List<ShapeKeyEntry> copies = ShapeKeyListOperations.Duplicate(data, new[] { b, a });
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "A", "A_copy", "B", "B_copy" }));
            Assert.That(copies[0].Name, Is.EqualTo("A_copy"), "複製はデータの並びの順で返る");
            AssertSame(a.Frames[0].DeltaVertices, copies[0].Frames[0].DeltaVertices, 0f);
            Assert.That(copies[0].Frames[0].DeltaVertices, Is.Not.SameAs(a.Frames[0].DeltaVertices), "差分の配列は別物");

            // もう一度複製すると、名前が重ならない
            ShapeKeyListOperations.Duplicate(data, new[] { a });
            Assert.That(data.Entries.FindAll(e => e.Name == "A_copy").Count, Is.EqualTo(1));
            Assert.That(data.FindEntry("A_copy (1)"), Is.Not.Null);

            NextUndoGroup();
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B" }));
        }

        [Test]
        public void CopyPaste_WorksWithinAndAcrossDataWithTheSameVertexCount_ButRejectsOtherVertexCounts()
        {
            Mesh mesh = CreateGridMesh("MeshA");
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.002f));
            a.Group = "G";
            GP02ShapeKeyData first = NewData(mesh, a);
            GP02ShapeKeyData second = NewData(CreateGridMesh("MeshB"));
            GP02ShapeKeyData other = NewData(Track(new Mesh { name = "Small", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } }));

            Assert.That(ShapeKeyListOperations.GetPasteProblem(first), Is.Not.Null, "何もコピーしていない");
            Assert.That(ShapeKeyListOperations.Copy(first, new[] { a }), Is.EqualTo(1));

            NextUndoGroup();
            Assert.That(ShapeKeyListOperations.TryPaste(second, out List<ShapeKeyEntry> pasted, out string error), Is.True, error);
            NextUndoGroup();
            Assert.That(pasted.Count, Is.EqualTo(1));
            Assert.That(second.Entries[0].Name, Is.EqualTo("A"));
            Assert.That(second.Groups, Does.Contain("G"), "グループも一覧に入る");
            AssertSame(a.Frames[0].DeltaVertices, second.Entries[0].Frames[0].DeltaVertices, 0f);

            // コピー元を変えても、クリップボードと貼り付けた先は変わらない
            a.Frames[0].DeltaVertices[0] = Vector3.one;
            Assert.That(second.Entries[0].Frames[0].DeltaVertices[0], Is.Not.EqualTo(Vector3.one));

            // 同じデータにも貼れて、名前は重ならない
            Assert.That(ShapeKeyListOperations.TryPaste(first, out _, out _), Is.True);
            NextUndoGroup();
            Assert.That(Names(first), Is.EqualTo(new[] { "A", "A (1)" }));

            // 頂点数が違うデータには貼れない
            Assert.That(ShapeKeyListOperations.GetPasteProblem(other), Is.Not.Null);
            Assert.That(ShapeKeyListOperations.TryPaste(other, out List<ShapeKeyEntry> none, out string rejected), Is.False);
            Assert.That(none, Is.Empty);
            Assert.That(rejected, Is.Not.Empty);
            Assert.That(other.Entries, Is.Empty);

            Undo.PerformUndo();
            Assert.That(Names(first), Is.EqualTo(new[] { "A" }), "貼り付けは Undo で戻る");
            Undo.PerformUndo();
            Assert.That(second.Entries, Is.Empty, "別のデータへの貼り付けも戻る");
        }

        [Test]
        public void Delete_RemovesSelectedEntriesAndUndoRestoresThem()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.002f)), b = Entry("B", Ramp(n, 0.003f)), c = Entry("C", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, a, b, c);

            NextUndoGroup();
            Assert.That(ShapeKeyListOperations.Delete(data, new[] { a, c }), Is.EqualTo(2));
            NextUndoGroup();
            Assert.That(Names(data), Is.EqualTo(new[] { "B" }));

            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "C" }));
            Assert.That(ShapeKeyListOperations.Delete(data, new ShapeKeyEntry[0]), Is.EqualTo(0));
        }

        // ---- 合成 ----

        [Test]
        public void Merge_AddsWeightedDeltasIntoOneEntry_AndDisablesTheSources()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] da = Ramp(n, 0.002f), db = Ramp(n, 0.003f);
            ShapeKeyEntry a = Entry("A", da), b = Entry("B", db);
            GP02ShapeKeyData data = NewData(mesh, a, b);

            NextUndoGroup();
            ShapeKeyEntry merged = ShapeKeyListOperations.Merge(data, new[] { a, b }, new[] { 50f, 100f }, "AB");
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "AB" }));
            Assert.That(merged.Frames.Count, Is.EqualTo(1));
            Assert.That(merged.Frames[0].Weight, Is.EqualTo(100f));
            AssertSame(Combine(da, 0.5f, db, 1f), merged.Frames[0].DeltaVertices, 1e-6f);
            Assert.That(a.Enabled, Is.False);
            Assert.That(b.Enabled, Is.False);
            Assert.That(merged.Enabled, Is.True);

            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(data.Entries[0].Enabled, Is.True, "元の有効も戻る");
        }

        [Test]
        public void Merge_DefaultsToPreviewWeights_AndUsesFullWeightWhenAllAreZero()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] da = Ramp(n, 0.002f), db = Ramp(n, 0.003f);
            ShapeKeyEntry a = Entry("A", da), b = Entry("B", db);
            GP02ShapeKeyData data = NewData(mesh, a, b);

            ShapeKeyEntry allZero = ShapeKeyListOperations.Merge(data, new[] { a, b }, null, "Full", false);
            AssertSame(Combine(da, 1f, db, 1f), allZero.Frames[0].DeltaVertices, 1e-6f);
            Assert.That(a.Enabled, Is.True, "disableSources が false なら有効のまま");

            a.PreviewWeight = 20f;
            b.PreviewWeight = 60f;
            ShapeKeyEntry preview = ShapeKeyListOperations.Merge(data, new[] { a, b }, null, "Preview", false);
            AssertSame(Combine(da, 0.2f, db, 0.6f), preview.Frames[0].DeltaVertices, 1e-6f);
        }

        [Test]
        public void Merge_EvaluatesInBetweenEntriesAtTheirWeight()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] half = Ramp(n, 0.001f), full = Ramp(n, 0.004f);
            var inBetween = new ShapeKeyEntry { Name = "IB" };
            inBetween.Frames.Add(new ShapeKeyFrame { Weight = 50f, DeltaVertices = half });
            inBetween.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = full });
            GP02ShapeKeyData data = NewData(mesh, inBetween);

            ShapeKeyEntry at75 = ShapeKeyListOperations.Merge(data, new[] { inBetween }, new[] { 75f }, "At75", false);
            AssertSame(Combine(half, 0.5f, full, 0.5f), at75.Frames[0].DeltaVertices, 1e-6f);
        }

        // ---- 左右分割・反転・対称 ----

        [Test]
        public void SplitLeftRight_ProducesLandRThatSumToTheOriginal_AndUndoRemovesThem()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] deltas = Ramp(n, 0.003f);
            ShapeKeyEntry entry = Entry("Smile", deltas);
            GP02ShapeKeyData data = NewData(mesh, entry);

            NextUndoGroup();
            ShapeKeyListOperations.SplitLeftRight(data, entry, 0, Center, 0.04f, true, true, out ShapeKeyEntry left, out ShapeKeyEntry right);
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "Smile", "Smile_L", "Smile_R" }));
            Assert.That(entry.Enabled, Is.False, "元は無効になる");
            Vector3[] l = left.Frames[0].DeltaVertices, r = right.Frames[0].DeltaVertices;
            AssertSame(deltas, Combine(l, 1f, r, 1f), 1e-6f, "L + R");

            // 中央から十分離れた頂点は、片側だけが動く（+X 側が L）
            Vector3[] positions = mesh.vertices;
            for (int v = 0; v < n; v++)
            {
                if (positions[v].x > Center + 0.03f)
                {
                    Assert.That(r[v].magnitude, Is.LessThan(1e-6f), $"頂点 {v}");
                    Assert.That(Vector3.Distance(l[v], deltas[v]), Is.LessThan(1e-6f));
                }
                else if (positions[v].x < Center - 0.03f)
                {
                    Assert.That(l[v].magnitude, Is.LessThan(1e-6f), $"頂点 {v}");
                }
            }

            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "Smile" }));
            Assert.That(data.Entries[0].Enabled, Is.True);
        }

        [Test]
        public void SplitLeftRight_LeftIsPositiveFalseSwapsSides()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = Entry("E", Ramp(n, 0.003f));
            GP02ShapeKeyData data = NewData(mesh, entry);

            ShapeKeyListOperations.SplitLeftRight(data, entry, 0, Center, 0f, false, false, out ShapeKeyEntry left, out _);
            Vector3[] positions = mesh.vertices;
            for (int v = 0; v < n; v++)
            {
                if (positions[v].x > Center + 0.01f) Assert.That(left.Frames[0].DeltaVertices[v].magnitude, Is.LessThan(1e-6f));
            }
            Assert.That(entry.Enabled, Is.True);
        }

        [Test]
        public void Flip_MirrorsTheDeltasInPlace_TwiceGivesTheOriginal_AndUndoRestores()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] deltas = Ramp(n, 0.003f);
            ShapeKeyEntry entry = Entry("E", deltas);
            GP02ShapeKeyData data = NewData(mesh, entry);

            NextUndoGroup();
            ShapeKeyEntry flipped = ShapeKeyListOperations.Flip(data, entry, 0, Center, 0.001f, false);
            NextUndoGroup();

            Assert.That(flipped, Is.SameAs(entry), "元のエントリを書き換える");
            Vector3[] now = entry.Frames[0].DeltaVertices;
            for (int r = 0; r <= Cells; r++)
            for (int c = 0; c <= Cells; c++)
            {
                Vector3 m = deltas[r * Stride + (Cells - c)];
                Assert.That(Vector3.Distance(now[r * Stride + c], new Vector3(-m.x, m.y, m.z)), Is.LessThan(1e-6f), $"行 {r} 列 {c}");
            }

            ShapeKeyListOperations.Flip(data, entry, 0, Center, 0.001f, false);
            AssertSame(deltas, entry.Frames[0].DeltaVertices, 1e-6f, "2回反転");

            Undo.PerformUndo();
            Undo.PerformUndo();
            AssertSame(deltas, data.Entries[0].Frames[0].DeltaVertices, 0f, "Undo");
        }

        [Test]
        public void Flip_AsCopyAddsANewEntryAndLeavesTheOriginal()
        {
            Mesh mesh = CreateGridMesh();
            Vector3[] deltas = Ramp(mesh.vertexCount, 0.003f);
            ShapeKeyEntry entry = Entry("E", deltas);
            GP02ShapeKeyData data = NewData(mesh, entry);

            ShapeKeyEntry copy = ShapeKeyListOperations.Flip(data, entry, 0, Center, 0.001f, true);

            Assert.That(copy, Is.Not.SameAs(entry));
            Assert.That(Names(data), Is.EqualTo(new[] { "E", "E_flip" }));
            AssertSame(deltas, entry.Frames[0].DeltaVertices, 0f);
            Assert.That(Vector3.Distance(copy.Frames[0].DeltaVertices[0], deltas[0]), Is.GreaterThan(1e-6f));
        }

        [Test]
        public void Flip_MirrorsThePinnedVertices()
        {
            Mesh mesh = CreateGridMesh();
            ShapeKeyEntry entry = Entry("E", Ramp(mesh.vertexCount, 0.003f));
            entry.PinnedVertices = new[] { 0, Stride + 1 };     // 左端の列
            GP02ShapeKeyData data = NewData(mesh, entry);

            ShapeKeyListOperations.Flip(data, entry, 0, Center, 0.001f, false);

            Assert.That(entry.PinnedVertices, Is.EquivalentTo(new[] { Cells, Stride + Cells - 1 }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Symmetrize_CopiesTheKeptSideToTheOtherSide(bool keepPositiveSide)
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] deltas = Ramp(n, 0.003f);
            ShapeKeyEntry entry = Entry("E", deltas);
            GP02ShapeKeyData data = NewData(mesh, entry);

            NextUndoGroup();
            ShapeKeyListOperations.Symmetrize(data, entry, 0, Center, 0.001f, keepPositiveSide, false);
            NextUndoGroup();

            Vector3[] now = entry.Frames[0].DeltaVertices;
            for (int r = 0; r <= Cells; r++)
            for (int c = 0; c <= Cells; c++)
            {
                int v = r * Stride + c, mirror = r * Stride + (Cells - c);
                Vector3 mirrored = new Vector3(-deltas[mirror].x, deltas[mirror].y, deltas[mirror].z);
                bool onKeptSide = keepPositiveSide ? c > Cells / 2 : c < Cells / 2;
                if (c == Cells / 2)
                {
                    Vector3 average = (deltas[v] + mirrored) * 0.5f;
                    Assert.That(Vector3.Distance(now[v], average), Is.LessThan(1e-6f), $"中央 行 {r}");
                }
                else if (onKeptSide)
                {
                    Assert.That(Vector3.Distance(now[v], deltas[v]), Is.LessThan(1e-6f), $"正の側 行 {r} 列 {c}");
                }
                else
                {
                    Assert.That(Vector3.Distance(now[v], mirrored), Is.LessThan(1e-6f), $"反対側 行 {r} 列 {c}");
                }
            }

            Undo.PerformUndo();
            AssertSame(deltas, data.Entries[0].Frames[0].DeltaVertices, 0f, "Undo");
        }

        [Test]
        public void SplitFlipSymmetrize_RejectEntriesAndMeshesThatDoNotMatch()
        {
            Mesh mesh = CreateGridMesh();
            ShapeKeyEntry good = Entry("Good", Ramp(mesh.vertexCount, 0.002f));
            ShapeKeyEntry bad = Entry("Bad", new Vector3[5]);
            GP02ShapeKeyData data = NewData(mesh, good, bad);

            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.Flip(data, bad, 0, Center, 0.001f, false));
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.Symmetrize(data, bad, 0, Center, 0.001f, true, false));
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.SplitLeftRight(data, bad, 0, Center, 0.01f, true, true, out _, out _));
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.Flip(data, Entry("Outside", Ramp(mesh.vertexCount, 0.002f)), 0, Center, 0.001f, false));

            data.SourceMesh = null;
            Assert.Throws<System.InvalidOperationException>(() => ShapeKeyListOperations.Flip(data, good, 0, Center, 0.001f, false));
        }

        // ---- 量の拡大縮小 ----

        [Test]
        public void Scale_MultipliesEveryFrameAndChannel_AndUndoRestores()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] half = Ramp(n, 0.001f), full = Ramp(n, 0.004f), normals = Ramp(n, 0.1f);
            var entry = new ShapeKeyEntry { Name = "IB" };
            entry.Frames.Add(new ShapeKeyFrame { Weight = 50f, DeltaVertices = half });
            entry.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = full, DeltaNormals = normals });
            ShapeKeyEntry other = Entry("Other", Ramp(n, 0.002f));
            GP02ShapeKeyData data = NewData(mesh, entry, other);

            NextUndoGroup();
            ShapeKeyListOperations.Scale(data, new[] { entry }, 2f);
            NextUndoGroup();

            AssertSame(Combine(half, 2f, half, 0f), entry.Frames[0].DeltaVertices, 1e-6f);
            AssertSame(Combine(full, 2f, full, 0f), entry.Frames[1].DeltaVertices, 1e-6f);
            AssertSame(Combine(normals, 2f, normals, 0f), entry.Frames[1].DeltaNormals, 1e-6f);
            Assert.That(entry.Frames[0].HasNormals, Is.False);
            AssertSame(Ramp(n, 0.002f), other.Frames[0].DeltaVertices, 0f, "選んでいないものは変わらない");

            Undo.PerformUndo();
            AssertSame(full, data.Entries[0].Frames[1].DeltaVertices, 0f, "Undo");
        }

        // ---- in-between ----

        [Test]
        public void MakeInBetween_Crossfade_ChainsEntriesIntoFramesAtEvenWeights()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] a = Ramp(n, 0.001f), b = Ramp(n, 0.002f), c = Ramp(n, 0.003f);
            ShapeKeyEntry ea = Entry("A", a), eb = Entry("B", b), ec = Entry("C", c);
            GP02ShapeKeyData data = NewData(mesh, ea, eb, ec);

            NextUndoGroup();
            ShapeKeyEntry result = ShapeKeyListOperations.MakeInBetween(data, new[] { ea, eb, ec }, "Chain", ShapeKeyInBetweenMode.Crossfade);
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "C", "Chain" }));
            Assert.That(result.InBetweenMode, Is.EqualTo(ShapeKeyInBetweenMode.Crossfade));
            Assert.That(result.Frames.Count, Is.EqualTo(3));
            Assert.That(result.Frames[0].Weight, Is.EqualTo(100f / 3f).Within(1e-3f));
            Assert.That(result.Frames[1].Weight, Is.EqualTo(200f / 3f).Within(1e-3f));
            Assert.That(result.Frames[2].Weight, Is.EqualTo(100f));
            AssertSame(a, result.Frames[0].DeltaVertices, 1e-6f);
            AssertSame(b, result.Frames[1].DeltaVertices, 1e-6f);
            AssertSame(c, result.Frames[2].DeltaVertices, 1e-6f);
            Assert.That(ea.Enabled || eb.Enabled || ec.Enabled, Is.False, "元は無効になる");

            // メッシュに書き出すと、3フレームの BlendShape になる
            Mesh built = Track(ShapeKeyLibrary.BuildMesh(mesh, data, true));
            Assert.That(built.blendShapeCount, Is.EqualTo(1), "有効なのは Chain だけ");
            Assert.That(built.GetBlendShapeFrameCount(0), Is.EqualTo(3));
            Assert.That(built.GetBlendShapeFrameWeight(0, 2), Is.EqualTo(100f));

            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "C" }));
        }

        [Test]
        public void MakeInBetween_Progressive_FramesAreRunningSums()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            Vector3[] a = Ramp(n, 0.001f), b = Ramp(n, 0.002f);
            ShapeKeyEntry ea = Entry("A", a), eb = Entry("B", b);
            GP02ShapeKeyData data = NewData(mesh, ea, eb);

            ShapeKeyEntry result = ShapeKeyListOperations.MakeInBetween(data, new[] { ea, eb }, null, ShapeKeyInBetweenMode.Progressive,
                new[] { 30f, 100f }, false);

            Assert.That(result.Name, Is.EqualTo("A_inbetween"));
            Assert.That(result.InBetweenMode, Is.EqualTo(ShapeKeyInBetweenMode.Progressive));
            Assert.That(result.Frames[0].Weight, Is.EqualTo(30f));
            AssertSame(a, result.Frames[0].DeltaVertices, 1e-6f);
            AssertSame(Combine(a, 1f, b, 1f), result.Frames[1].DeltaVertices, 1e-6f);
            Assert.That(ea.Enabled, Is.True);
        }

        [Test]
        public void MakeInBetween_SingleEntryMakesAnOrdinaryShapeKey_AndBadWeightsAreRejected()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry ea = Entry("A", Ramp(n, 0.001f)), eb = Entry("B", Ramp(n, 0.002f));
            GP02ShapeKeyData data = NewData(mesh, ea, eb);

            ShapeKeyEntry single = ShapeKeyListOperations.MakeInBetween(data, new[] { ea }, "One", ShapeKeyInBetweenMode.Crossfade);
            Assert.That(single.Frames.Count, Is.EqualTo(1));
            Assert.That(single.Frames[0].Weight, Is.EqualTo(100f));

            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.MakeInBetween(data, new[] { ea, eb }, "Bad", ShapeKeyInBetweenMode.Crossfade, new[] { 80f, 60f }));
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.MakeInBetween(data, new[] { ea, eb }, "Bad", ShapeKeyInBetweenMode.Crossfade, new[] { 50f, 90f }), "最後は 100");
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.MakeInBetween(data, new ShapeKeyEntry[0], "Bad", ShapeKeyInBetweenMode.Crossfade));
        }

        // ---- グループ・並べ替え・名前 ----

        [Test]
        public void Groups_SetRenameRemove_AndUndo()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.001f)), b = Entry("B", Ramp(n, 0.002f));
            GP02ShapeKeyData data = NewData(mesh, a, b);

            NextUndoGroup();
            ShapeKeyListOperations.SetGroup(data, new[] { a, b }, "Face");
            NextUndoGroup();
            Assert.That(data.Groups, Is.EqualTo(new[] { "Face" }));
            Assert.That(a.Group, Is.EqualTo("Face"));

            Assert.That(ShapeKeyListOperations.RenameGroup(data, "Face", "Head"), Is.True);
            Assert.That(data.Groups, Is.EqualTo(new[] { "Head" }));
            Assert.That(b.Group, Is.EqualTo("Head"));
            Assert.That(ShapeKeyListOperations.RenameGroup(data, "Head", ""), Is.False);
            ShapeKeyListOperations.AddGroup(data, "Body");
            Assert.That(ShapeKeyListOperations.RenameGroup(data, "Body", "Head"), Is.False, "同じ名前のグループがある");

            ShapeKeyListOperations.RemoveGroup(data, "Head");
            Assert.That(data.Groups, Is.EqualTo(new[] { "Body" }));
            Assert.That(a.Group, Is.EqualTo(""), "中のシェイプキーは残る");
            Assert.That(data.Entries.Count, Is.EqualTo(2));

            Assert.That(ShapeKeyListOperations.SuggestGroupName(data), Is.Not.EqualTo("Body"));

            ShapeKeyListOperations.SetGroup(data, new[] { a }, "");
            Assert.That(a.Group, Is.EqualTo(""));
        }

        [Test]
        public void Move_MovesWithinTheSameGroupOnly_AndKeepsSelectionOrder()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.001f)), b = Entry("B", Ramp(n, 0.002f)), c = Entry("C", Ramp(n, 0.003f)), d = Entry("D", Ramp(n, 0.004f));
            a.Group = "G1";
            c.Group = "G1";
            GP02ShapeKeyData data = NewData(mesh, a, b, c, d);

            NextUndoGroup();
            Assert.That(ShapeKeyListOperations.Move(data, new[] { c }, -1), Is.True);
            NextUndoGroup();
            Assert.That(Names(data), Is.EqualTo(new[] { "C", "A", "B", "D" }), "グループの外の B を飛び越えて A の前に入る");

            Assert.That(ShapeKeyListOperations.Move(data, new[] { c }, -1), Is.False, "先頭は動かない");
            // B と D は同じグループ（なし）で隣り合っている（間の A は別グループ）。D は末尾で動けず、B は D とぶつかるので動かない
            Assert.That(ShapeKeyListOperations.Move(data, new[] { b, d }, 1), Is.False);
            Assert.That(Names(data), Is.EqualTo(new[] { "C", "A", "B", "D" }));
        }

        [Test]
        public void Move_MovesSeveralSelectedEntriesTogether()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.001f)), b = Entry("B", Ramp(n, 0.002f)), c = Entry("C", Ramp(n, 0.003f)), d = Entry("D", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, a, b, c, d);

            Assert.That(ShapeKeyListOperations.Move(data, new[] { b, c }, 1), Is.True);
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "D", "B", "C" }));
            Assert.That(ShapeKeyListOperations.Move(data, new[] { b, c }, -1), Is.True);
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "C", "D" }));
        }

        [Test]
        public void MoveTo_ReordersAndChangesTheGroup_AndUndoRestores()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.001f)), b = Entry("B", Ramp(n, 0.002f)), c = Entry("C", Ramp(n, 0.003f)), d = Entry("D", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, a, b, c, d);

            NextUndoGroup();
            ShapeKeyListOperations.MoveTo(data, new[] { a }, 3, "G");
            NextUndoGroup();
            Assert.That(Names(data), Is.EqualTo(new[] { "B", "C", "A", "D" }));
            Assert.That(a.Group, Is.EqualTo("G"));
            Assert.That(data.Groups, Does.Contain("G"));

            ShapeKeyListOperations.MoveTo(data, new[] { d, c }, 0, "");
            Assert.That(Names(data), Is.EqualTo(new[] { "C", "D", "B", "A" }), "選んだもの同士の順は、データの並びのまま");

            ShapeKeyListOperations.MoveTo(data, new[] { b }, 99, "");
            Assert.That(Names(data), Is.EqualTo(new[] { "C", "D", "A", "B" }), "範囲外は最後");

            Undo.PerformUndo();
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "C", "D" }));
            Assert.That(data.Entries[0].Group, Is.EqualTo(""));
        }

        [Test]
        public void RenameAndSetEnabled_AreUndoable_AndEmptyNamesAreRefused()
        {
            Mesh mesh = CreateGridMesh();
            ShapeKeyEntry a = Entry("A", Ramp(mesh.vertexCount, 0.001f));
            GP02ShapeKeyData data = NewData(mesh, a);

            NextUndoGroup();
            Assert.That(ShapeKeyListOperations.Rename(data, a, "  Smile "), Is.True);
            NextUndoGroup();
            Assert.That(a.Name, Is.EqualTo("Smile"));
            Assert.That(ShapeKeyListOperations.Rename(data, a, "   "), Is.False);
            Assert.That(a.Name, Is.EqualTo("Smile"));

            ShapeKeyListOperations.SetEnabled(data, new[] { a }, false);
            Assert.That(a.Enabled, Is.False);
            NextUndoGroup();

            Undo.PerformUndo();
            Assert.That(data.Entries[0].Enabled, Is.True);
            Undo.PerformUndo();
            Assert.That(data.Entries[0].Name, Is.EqualTo("A"));
        }

        [Test]
        public void PreviewWeights_AreClampedAndCanBeReset()
        {
            Mesh mesh = CreateGridMesh();
            ShapeKeyEntry a = Entry("A", Ramp(mesh.vertexCount, 0.001f));
            GP02ShapeKeyData data = NewData(mesh, a);

            ShapeKeyListOperations.SetPreviewWeight(data, a, 250f);
            Assert.That(a.PreviewWeight, Is.EqualTo(100f));
            ShapeKeyListOperations.SetPreviewWeight(data, a, -5f);
            Assert.That(a.PreviewWeight, Is.EqualTo(0f));

            Assert.That(ShapeKeyListOperations.HasActivePreviewWeight(data), Is.False);
            a.PreviewWeight = 30f;
            Assert.That(ShapeKeyListOperations.HasActivePreviewWeight(data), Is.True);
            a.Enabled = false;
            Assert.That(ShapeKeyListOperations.HasActivePreviewWeight(data), Is.False, "無効なものは数えない");
            a.Enabled = true;
            ShapeKeyListOperations.ResetPreviewWeights(data);
            Assert.That(a.PreviewWeight, Is.EqualTo(0f));
        }

        // ---- 取り込み ----

        Mesh CreateMeshWithBlendShapes()
        {
            Mesh mesh = CreateGridMesh("WithShapes");
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("Smile", 100f, Ramp(n, 0.004f), null, null);
            mesh.AddBlendShapeFrame("Blink", 50f, Ramp(n, 0.001f), null, null);
            mesh.AddBlendShapeFrame("Blink", 100f, Ramp(n, 0.003f), null, null);
            return mesh;
        }

        [Test]
        public void ImportBlendShapes_ByIndexAndAll_KeepAllFramesAndMakeNamesUnique()
        {
            Mesh mesh = CreateMeshWithBlendShapes();
            GP02ShapeKeyData data = NewData(mesh);

            NextUndoGroup();
            List<ShapeKeyEntry> one = ShapeKeyListOperations.ImportBlendShapes(data, mesh, new[] { 1 });
            NextUndoGroup();
            Assert.That(one.Count, Is.EqualTo(1));
            Assert.That(one[0].Name, Is.EqualTo("Blink"));
            Assert.That(one[0].Frames.Count, Is.EqualTo(2));
            Assert.That(one[0].Frames[0].Weight, Is.EqualTo(50f));

            List<ShapeKeyEntry> all = ShapeKeyListOperations.ImportAllBlendShapes(data, mesh);
            Assert.That(all.Count, Is.EqualTo(2));
            Assert.That(Names(data), Is.EqualTo(new[] { "Blink", "Smile", "Blink (1)" }));

            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "Blink" }));
            Undo.PerformUndo();
            Assert.That(data.Entries, Is.Empty);
        }

        [Test]
        public void ImportBlendShapes_RejectsMeshesWithAnotherVertexCount()
        {
            Mesh mesh = CreateMeshWithBlendShapes();
            GP02ShapeKeyData data = NewData(mesh);
            data.VertexCount = 10;

            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.ImportAllBlendShapes(data, mesh));
            Assert.That(data.Entries, Is.Empty);
        }

        [Test]
        public void ImportFromMeshAsset_TakesTheDifferenceFromTheSourceMesh()
        {
            Mesh source = CreateGridMesh("Source");
            GP02ShapeKeyData data = NewData(source);
            Mesh edited = Track(Object.Instantiate(source));
            edited.name = "Edited";
            Vector3[] vertices = edited.vertices;
            for (int v = 0; v < vertices.Length; v++) vertices[v].z += 0.01f;
            edited.vertices = vertices;

            NextUndoGroup();
            ShapeKeyEntry entry = ShapeKeyListOperations.ImportFromMeshAsset(data, edited);
            NextUndoGroup();

            Assert.That(entry.Name, Is.EqualTo("Edited"));
            Assert.That(data.Entries, Does.Contain(entry));
            foreach (Vector3 d in entry.Frames[0].DeltaVertices) Assert.That(d.z, Is.EqualTo(0.01f).Within(1e-6f));

            Mesh wrong = Track(new Mesh { name = "Wrong", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.ImportFromMeshAsset(data, wrong));

            Undo.PerformUndo();
            Assert.That(data.Entries, Is.Empty);
        }

        [Test]
        public void ImportFromClip_UsesTheBlendShapeValuesAtTheGivenTime()
        {
            Mesh mesh = CreateMeshWithBlendShapes();
            GP02ShapeKeyData data = NewData(mesh);
            var clip = Track(new AnimationClip { name = "Joy" });
            clip.SetCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Smile", AnimationCurve.Linear(0f, 0f, 1f, 100f));

            NextUndoGroup();
            ShapeKeyEntry entry = ShapeKeyListOperations.ImportFromClip(data, clip, "Face", 0.5f);
            NextUndoGroup();

            Assert.That(entry.Name, Is.EqualTo("Joy"));
            AssertSame(Combine(Ramp(mesh.vertexCount, 0.004f), 0.5f, Ramp(mesh.vertexCount, 0.004f), 0f), entry.Frames[0].DeltaVertices, 1e-6f);
            Assert.Throws<System.InvalidOperationException>(() => ShapeKeyListOperations.ImportFromClip(data, clip, "Other", 0.5f));

            Undo.PerformUndo();
            Assert.That(data.Entries, Is.Empty);
        }

        [Test]
        public void GuessRendererPath_IsRelativeToTheNearestAnimator()
        {
            GameObject root = Track(new GameObject("Avatar"));
            root.AddComponent<Animator>();
            GameObject body = Track(new GameObject("Body"));
            body.transform.SetParent(root.transform, false);
            GameObject face = Track(new GameObject("Face"));
            face.transform.SetParent(body.transform, false);
            var renderer = face.AddComponent<SkinnedMeshRenderer>();

            Assert.That(ShapeKeyListOperations.GuessRendererPath(renderer), Is.EqualTo("Body/Face"));
            Assert.That(ShapeKeyListOperations.GuessRendererPath(null), Is.EqualTo(""));
        }
    }
}
