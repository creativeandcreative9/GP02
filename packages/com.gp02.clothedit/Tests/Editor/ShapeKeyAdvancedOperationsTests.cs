using System;
using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// フレームの編集、差し引き、選んだ点の範囲（マスク）での分割・取り出し・削除、平面での分割、別のメッシュへ写す、
    /// 履歴、取り込みの上書き、AnimationClip のキーフレームの取り込み、並べ替えと検索、ソロ、ボーンウェイトの付け替え（左右反転）を、画面なしで確かめる。
    /// </summary>
    public class ShapeKeyAdvancedOperationsTests : ShapeKeyUiTestBase
    {
        const float Center = Size / 2f;

        static string[] Names(GP02ShapeKeyData data)
        {
            var names = new string[data.Entries.Count];
            for (int i = 0; i < names.Length; i++) names[i] = data.Entries[i].Name;
            return names;
        }

        // 位置の関数としてのなめらかな差分
        static Vector3 Field(Vector3 p) => new Vector3(0.004f * Mathf.Sin(9f * p.y), 0.002f * p.x, 0.01f * Mathf.Sin(7f * p.x) * Mathf.Cos(5f * p.y));

        static Vector3[] Sample(Vector3[] positions)
        {
            var d = new Vector3[positions.Length];
            for (int i = 0; i < d.Length; i++) d[i] = Field(positions[i]);
            return d;
        }

        // XY 平面の格子（一辺 Size、cells 分割、z のずれ）
        Mesh Grid(int cells, string name, float z = 0f)
        {
            int stride = cells + 1;
            var vertices = new Vector3[stride * stride];
            var triangles = new List<int>();
            for (int r = 0; r <= cells; r++)
            for (int c = 0; c <= cells; c++) vertices[r * stride + c] = new Vector3(Size * c / cells, Size * r / cells, z);
            for (int r = 0; r < cells; r++)
            for (int c = 0; c < cells; c++)
            {
                int i = r * stride + c;
                triangles.AddRange(new[] { i, i + stride, i + 1, i + 1, i + stride, i + stride + 1 });
            }
            var mesh = Track(new Mesh { name = name });
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        // ---- in-between のフレーム ----

        static ShapeKeyEntry TwoFrames(int n)
        {
            ShapeKeyEntry entry = Entry("Blink", Ramp(n, 0.002f), 40f);
            entry.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.004f) });
            return entry;
        }

        [Test]
        public void DeleteFrame_RemovesAFrameKeepsTheOldShapeInTheHistory_AndUndoBringsItBack()
        {
            Mesh mesh = CreateGridMesh();
            ShapeKeyEntry entry = TwoFrames(mesh.vertexCount);
            GP02ShapeKeyData data = NewData(mesh, entry);

            NextUndoGroup();
            Assert.That(ShapeKeyListOperations.DeleteFrame(data, entry, 0), Is.True);
            NextUndoGroup();

            Assert.That(entry.Frames.Count, Is.EqualTo(1));
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(100f));
            Assert.That(entry.History.Count, Is.EqualTo(1));
            Assert.That(entry.History[0].Frames.Count, Is.EqualTo(2), "消す前の形が残る");
            Assert.That(ShapeKeyListOperations.DeleteFrame(data, entry, 0), Is.False, "最後の 1 つは消せない");
            Assert.That(ShapeKeyListOperations.DeleteFrame(data, entry, 5), Is.False);

            Undo.PerformUndo();
            Assert.That(data.Entries[0].Frames.Count, Is.EqualTo(2));
            Assert.That(data.Entries[0].History.Count, Is.EqualTo(0));
        }

        [Test]
        public void SetFrameWeight_ReordersByWeight_ClampsAndRejectsDuplicates()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = TwoFrames(n);
            GP02ShapeKeyData data = NewData(mesh, entry);

            Assert.That(ShapeKeyListOperations.SetFrameWeight(data, entry, 0, 120f), Is.False, "100 に収まって、もう 1 つのフレームと重なる");
            NextUndoGroup();
            Assert.That(ShapeKeyListOperations.SetFrameWeight(data, entry, 0, 60f), Is.True);
            NextUndoGroup();
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(60f));
            AssertSame(Ramp(n, 0.002f), entry.Frames[0].DeltaVertices, 0f, "差分はそのまま");

            Assert.That(ShapeKeyListOperations.SetFrameWeight(data, entry, 1, 30f), Is.True);
            NextUndoGroup();
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(30f), "重みの順に並び直す");
            AssertSame(Ramp(n, 0.004f), entry.Frames[0].DeltaVertices, 0f);
            Assert.That(ShapeKeyListOperations.SetFrameWeight(data, entry, 0, -5f), Is.True);
            NextUndoGroup();
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(ShapeKeyListOperations.MinFrameWeight), "0 以下は下限に収める");

            Undo.PerformUndo();
            Assert.That(data.Entries[0].Frames[0].Weight, Is.EqualTo(30f));
        }

        // ---- 差し引き ----

        [Test]
        public void Subtract_MakesAMinusB_PlacedAfterBoth_AndUndoRemovesIt()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.004f)), b = Entry("B", Ramp(n, 0.001f)), c = Entry("C", Ramp(n, 0.002f));
            GP02ShapeKeyData data = NewData(mesh, a, b, c);

            NextUndoGroup();
            ShapeKeyEntry result = ShapeKeyListOperations.Subtract(data, a, b, null, true);
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "A_minus_B", "C" }));
            var expected = new Vector3[n];
            for (int v = 0; v < n; v++) expected[v] = a.Frames[0].DeltaVertices[v] - b.Frames[0].DeltaVertices[v];
            AssertSame(expected, result.Frames[0].DeltaVertices, 1e-7f);
            Assert.That(result.Frames[0].Weight, Is.EqualTo(100f));
            Assert.That(a.Enabled || b.Enabled, Is.False, "元は無効になる");
            Assert.That(result.CreatedTicks, Is.GreaterThan(0));

            // 逆向きと、in-between を含むもの（重み 100 での形で引く）
            ShapeKeyEntry reverse = ShapeKeyListOperations.Subtract(data, b, a, "B−A");
            AssertSame(Combine(a.Frames[0].DeltaVertices, -1f, b.Frames[0].DeltaVertices, 1f), reverse.Frames[0].DeltaVertices, 1e-7f);

            Assert.Throws<ArgumentException>(() => ShapeKeyListOperations.Subtract(data, a, null));
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "A", "B", "C" }));
            Assert.That(data.FindEntry("A").Enabled, Is.True);
        }

        // ---- 選んだ点の範囲（マスク）----

        float[] RightHalfMask(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            var mask = new float[v.Length];
            for (int i = 0; i < mask.Length; i++) mask[i] = v[i].x > Center ? 1f : 0f;
            return mask;
        }

        [Test]
        public void SplitByMask_InsideAndOutsideAddBackToTheOriginal_AndTheBorderCanBeSmoothed()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, entry, Entry("Other", Ramp(n, 0.001f)));
            float[] mask = RightHalfMask(mesh);

            NextUndoGroup();
            ShapeKeyListOperations.SplitByMask(data, entry, mask, 0, "_Sel", "_Rest", true, out ShapeKeyEntry inside, out ShapeKeyEntry outside);
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "Lift", "Lift_Sel", "Lift_Rest", "Other" }));
            Assert.That(entry.Enabled, Is.False);
            Vector3[] original = entry.Frames[0].DeltaVertices;
            Vector3[] insideDeltas = inside.Frames[0].DeltaVertices, outsideDeltas = outside.Frames[0].DeltaVertices;
            for (int v = 0; v < n; v++)
            {
                Assert.That(Vector3.Distance(insideDeltas[v] + outsideDeltas[v], original[v]), Is.LessThan(1e-8f), $"頂点 {v}");
                Assert.That(Vector3.Distance(insideDeltas[v], mask[v] > 0f ? original[v] : Vector3.zero), Is.LessThan(1e-8f));
            }

            // 境目をなめらかにすると、途中の値の頂点ができる
            ShapeKeyEntry other = data.Entries[3];
            ShapeKeyListOperations.SplitByMask(data, other, mask, 3, "_Sel", "_Rest", false, out ShapeKeyEntry smoothInside, out _);
            bool anyPartial = false;
            for (int v = 0; v < n; v++)
            {
                Vector3 d = smoothInside.Frames[0].DeltaVertices[v];
                Vector3 full = other.Frames[0].DeltaVertices[v];
                float ratio = full.z != 0f ? d.z / full.z : 0f;
                if (ratio > 0.05f && ratio < 0.95f) anyPartial = true;
            }
            Assert.That(anyPartial, Is.True);
            Assert.That(other.Enabled, Is.True, "元を無効にしない設定");

            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "Lift", "Other" }));
            Assert.That(data.Entries[0].Enabled, Is.True);
        }

        [Test]
        public void SplitByMask_RejectsMissingOrWrongSizedMasks()
        {
            Mesh mesh = CreateGridMesh();
            ShapeKeyEntry entry = Entry("Lift", Ramp(mesh.vertexCount, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, entry);

            Assert.Throws<ArgumentException>(() => ShapeKeyListOperations.SplitByMask(data, entry, null, 0, "_a", "_b", false, out _, out _));
            Assert.Throws<ArgumentException>(() => ShapeKeyListOperations.SplitByMask(data, entry, new float[3], 0, "_a", "_b", false, out _, out _));
            Assert.That(data.Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void ApplyMask_ExtractAndRemove_WorkAsCopiesAndInPlace_WithHistory()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, entry);
            float[] mask = RightHalfMask(mesh);
            Vector3[] original = Ramp(n, 0.004f);

            ShapeKeyEntry extracted = ShapeKeyListOperations.ApplyMask(data, entry, mask, 0, ShapeKeyMaskOperation.Extract, true);
            ShapeKeyEntry removed = ShapeKeyListOperations.ApplyMask(data, entry, mask, 0, ShapeKeyMaskOperation.Remove, true);
            Assert.That(Names(data), Is.EqualTo(new[] { "Lift", "Lift_Rest", "Lift_Sel" }));
            for (int v = 0; v < n; v++)
            {
                Assert.That(Vector3.Distance(extracted.Frames[0].DeltaVertices[v], mask[v] > 0f ? original[v] : Vector3.zero), Is.LessThan(1e-8f));
                Assert.That(Vector3.Distance(removed.Frames[0].DeltaVertices[v], mask[v] > 0f ? Vector3.zero : original[v]), Is.LessThan(1e-8f));
            }
            AssertSame(original, entry.Frames[0].DeltaVertices, 0f, "コピーのときは元を変えない");

            ShapeKeyEntry same = ShapeKeyListOperations.ApplyMask(data, entry, mask, 0, ShapeKeyMaskOperation.Remove, false);
            Assert.That(same, Is.SameAs(entry));
            Assert.That(entry.History.Count, Is.EqualTo(1), "書き換えたときは前の形が履歴に残る");
            AssertSame(original, entry.History[0].Frames[0].DeltaVertices, 0f);
            for (int v = 0; v < n; v++) Assert.That(entry.Frames[0].DeltaVertices[v], Is.EqualTo(mask[v] > 0f ? Vector3.zero : original[v]));
        }

        [Test]
        public void SplitByPlane_OnTheMeshAxisGivesTheSameResultAsSplitLeftRight()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry a = Entry("A", Ramp(n, 0.004f)), b = Entry("B", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, a, b);

            ShapeKeyListOperations.SplitLeftRight(data, a, 0, Center, 0.04f, true, false, out ShapeKeyEntry left, out ShapeKeyEntry right);
            ShapeKeyListOperations.SplitByPlane(data, b, Vector3.right, Center, 0.04f, true, false, out ShapeKeyEntry planeLeft, out ShapeKeyEntry planeRight);

            AssertSame(left.Frames[0].DeltaVertices, planeLeft.Frames[0].DeltaVertices, 1e-7f, "左");
            AssertSame(right.Frames[0].DeltaVertices, planeRight.Frames[0].DeltaVertices, 1e-7f, "右");
            Assert.That(planeLeft.Name, Is.EqualTo("B_L"));

            ShapeKeyListOperations.SplitByPlane(data, b, Vector3.right, Center, 0.04f, false, false, out ShapeKeyEntry flippedLeft, out _);
            AssertSame(right.Frames[0].DeltaVertices, flippedLeft.Frames[0].DeltaVertices, 1e-7f, "+ 側を L にしない");
        }

        [Test]
        public void TryGetMeshPlane_ConvertsAvatarLocalAxesEvenWhenRotatedAndScaled()
        {
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.transform.SetPositionAndRotation(new Vector3(1f, 0.5f, -2f), Quaternion.Euler(10f, 90f, 5f));
            avatar.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            GameObject mesh = Track(new GameObject("Mesh"));
            mesh.transform.SetParent(avatar.transform, false);
            mesh.transform.localPosition = new Vector3(0.2f, -0.1f, 0.3f);
            mesh.transform.localRotation = Quaternion.Euler(-90f, 0f, 20f);
            mesh.transform.localScale = new Vector3(0.01f, 0.02f, 0.03f);

            for (int axis = 0; axis < 3; axis++)
            {
                Assert.That(ShapeKeyListOperations.TryGetMeshPlane(mesh.transform, avatar.transform, axis, 0.15f, out Vector3 normal, out float offset), Is.True);
                var random = new System.Random(5 + axis);
                for (int i = 0; i < 40; i++)
                {
                    var p = new Vector3((float)random.NextDouble() * 20f - 10f, (float)random.NextDouble() * 20f - 10f, (float)random.NextDouble() * 20f - 10f);
                    Vector3 local = avatar.transform.InverseTransformPoint(mesh.transform.TransformPoint(p));
                    float expected = local[axis] - 0.15f;
                    float actual = Vector3.Dot(normal, p) - offset;
                    if (Mathf.Abs(expected) < 1e-3f) continue;
                    Assert.That(Mathf.Sign(actual), Is.EqualTo(Mathf.Sign(expected)), $"軸 {axis} の点 {i}");
                }
            }
            avatar.transform.localScale = Vector3.zero;
            Assert.That(ShapeKeyListOperations.TryGetMeshPlane(mesh.transform, avatar.transform, 0, 0f, out _, out _), Is.False);
        }

        // ---- 別のメッシュへ写す ----

        [Test]
        public void TransferEntries_ToAFinerMesh_FollowsTheSameFieldAndIsUndoable()
        {
            Mesh coarse = Grid(14, "Coarse");
            Mesh fine = Grid(41, "Fine");
            Assert.That(fine.vertexCount, Is.Not.EqualTo(coarse.vertexCount));
            ShapeKeyEntry entry = Entry("Wave", Sample(coarse.vertices));
            entry.Group = "Body";
            GP02ShapeKeyData source = NewData(coarse, entry);
            GP02ShapeKeyData target = NewData(fine, Entry("Wave", new Vector3[fine.vertexCount]));

            NextUndoGroup();
            ShapeKeyTransferResult result = ShapeKeyListOperations.TransferEntries(source, new[] { entry }, target, 0.01f);
            NextUndoGroup();

            Assert.That(result.Entries.Count, Is.EqualTo(1));
            Assert.That(result.MissCount, Is.EqualTo(0));
            Assert.That(result.VertexCount, Is.EqualTo(fine.vertexCount));
            Assert.That(Names(target), Is.EqualTo(new[] { "Wave", "Wave (1)" }), "写し先に同じ名前があれば番号を付ける");
            Assert.That(target.Groups, Does.Contain("Body"));
            Vector3[] expected = Sample(fine.vertices);
            Vector3[] actual = result.Entries[0].Frames[0].DeltaVertices;
            float worst = 0f, magnitude = 0f;
            for (int v = 0; v < expected.Length; v++)
            {
                worst = Mathf.Max(worst, Vector3.Distance(expected[v], actual[v]));
                magnitude = Mathf.Max(magnitude, expected[v].magnitude);
            }
            Assert.That(worst, Is.LessThan(0.1f * magnitude), "同じ場の値にほぼ一致する");
            Assert.That(source.Entries.Count, Is.EqualTo(1), "元のデータは変わらない");

            Undo.PerformUndo();
            Assert.That(Names(target), Is.EqualTo(new[] { "Wave" }));
        }

        [Test]
        public void TransferEntries_ToACoarserMesh_AndBeyondTheLimitGivesZero()
        {
            Mesh fine = Grid(42, "Fine");
            Mesh coarse = Grid(7, "Coarse");
            Mesh shifted = Grid(7, "Shifted", 0.05f);
            ShapeKeyEntry entry = Entry("Wave", Sample(fine.vertices));
            GP02ShapeKeyData source = NewData(fine, entry);
            GP02ShapeKeyData target = NewData(coarse);
            GP02ShapeKeyData far = NewData(shifted);

            ShapeKeyTransferResult result = ShapeKeyListOperations.TransferEntries(source, new[] { entry }, target, 0.01f);
            Vector3[] expected = Sample(coarse.vertices);
            for (int v = 0; v < expected.Length; v++) Assert.That(Vector3.Distance(expected[v], result.Entries[0].Frames[0].DeltaVertices[v]), Is.LessThan(0.002f), $"頂点 {v}");

            ShapeKeyTransferResult missed = ShapeKeyListOperations.TransferEntries(source, new[] { entry }, far, 0.01f);
            Assert.That(missed.MissCount, Is.EqualTo(shifted.vertexCount));
            foreach (Vector3 d in missed.Entries[0].Frames[0].DeltaVertices) Assert.That(d, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void TransferEntries_CarriesInBetweenFramesAndRejectsBadInput()
        {
            Mesh coarse = Grid(10, "Coarse");
            Mesh fine = Grid(25, "Fine");
            ShapeKeyEntry entry = Entry("Blink", Sample(coarse.vertices), 50f);
            Vector3[] doubled = Sample(coarse.vertices);
            for (int i = 0; i < doubled.Length; i++) doubled[i] *= 2f;
            entry.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = doubled });
            GP02ShapeKeyData source = NewData(coarse, entry);
            GP02ShapeKeyData target = NewData(fine);

            ShapeKeyTransferResult result = ShapeKeyListOperations.TransferEntries(source, new[] { entry }, target, 0.01f);

            Assert.That(result.Entries[0].Frames.Count, Is.EqualTo(2));
            Assert.That(result.Entries[0].Frames[0].Weight, Is.EqualTo(50f));
            Assert.That(result.Entries[0].Frames[1].DeltaVertices[100].magnitude, Is.EqualTo(2f * result.Entries[0].Frames[0].DeltaVertices[100].magnitude).Within(1e-6f));

            Assert.Throws<ArgumentException>(() => ShapeKeyListOperations.TransferEntries(source, new ShapeKeyEntry[0], target, 0.01f));
            var noMesh = NewData(fine);
            noMesh.SourceMesh = null;
            Assert.Throws<InvalidOperationException>(() => ShapeKeyListOperations.TransferEntries(source, new[] { entry }, noMesh, 0.01f));
        }

        // ---- 履歴 ----

        [Test]
        public void History_KeepsTheLatestTenVersionsWithTimestamps_NotDuplicatesOfTheSameShape()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.001f));
            for (int i = 1; i <= 13; i++)
            {
                var frames = new List<ShapeKeyFrame> { new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.001f * (1 + i)) } };
                Assert.That(ShapeKeyHistory.Overwrite(entry, frames, new int[0], "テスト " + i), Is.True);
            }

            Assert.That(entry.History.Count, Is.EqualTo(ShapeKeyEntry.MaxHistory));
            Assert.That(entry.History[ShapeKeyEntry.MaxHistory - 1].Reason, Is.EqualTo("テスト 13"));
            Assert.That(entry.History[0].Reason, Is.EqualTo("テスト 4"), "古いものから捨てる");
            Assert.That(entry.History[0].SavedAt, Does.Match(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$"));
            AssertSame(Ramp(n, 0.001f * 14), entry.Frames[0].DeltaVertices, 0f);
            AssertSame(Ramp(n, 0.001f * 13), entry.History[ShapeKeyEntry.MaxHistory - 1].Frames[0].DeltaVertices, 0f, "上書きする前の形");

            List<ShapeKeyFrame> same = ShapeKeyHistory.CloneFrames(entry.Frames);
            Assert.That(ShapeKeyHistory.Overwrite(entry, same, entry.PinnedVertices, "同じ"), Is.False, "同じ形なら履歴を増やさない");
            Assert.That(entry.History.Count, Is.EqualTo(ShapeKeyEntry.MaxHistory));
        }

        [Test]
        public void RestoreVersion_GoesBackKeepsTheCurrentShapeInTheHistory_AndUndoWorks()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.002f));
            GP02ShapeKeyData data = NewData(mesh, entry);
            ShapeKeyHistory.Overwrite(entry, new List<ShapeKeyFrame> { new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.005f) } }, new[] { 3 }, "上書き");
            Assert.That(entry.History.Count, Is.EqualTo(1));

            NextUndoGroup();
            ShapeKeyListOperations.RestoreVersion(data, entry, 0);
            NextUndoGroup();

            AssertSame(Ramp(n, 0.002f), entry.Frames[0].DeltaVertices, 0f);
            Assert.That(entry.History.Count, Is.EqualTo(2), "戻す前の形も残る");
            AssertSame(Ramp(n, 0.005f), entry.History[1].Frames[0].DeltaVertices, 0f);
            Assert.That(entry.PinnedVertices, Is.Empty);

            Undo.PerformUndo();
            entry = data.Entries[0];
            AssertSame(Ramp(n, 0.005f), entry.Frames[0].DeltaVertices, 0f);
            Assert.That(entry.PinnedVertices, Is.EqualTo(new[] { 3 }));
            Assert.That(entry.History.Count, Is.EqualTo(1));

            Assert.Throws<ArgumentOutOfRangeException>(() => ShapeKeyListOperations.RestoreVersion(data, entry, 4));
            entry.History[0].Frames[0].DeltaVertices = new Vector3[3];
            Assert.Throws<InvalidOperationException>(() => ShapeKeyListOperations.RestoreVersion(data, entry, 0));
        }

        [Test]
        public void InPlaceEdits_ScaleAndRecomputeNormals_PushHistory()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.004f));
            GP02ShapeKeyData data = NewData(mesh, entry);

            ShapeKeyListOperations.Scale(data, new[] { entry }, 0.5f);
            ShapeKeyListOperations.Scale(data, new[] { entry }, 0.5f);

            Assert.That(entry.History.Count, Is.EqualTo(2));
            AssertSame(Ramp(n, 0.004f), entry.History[0].Frames[0].DeltaVertices, 1e-7f);
            Assert.That(entry.Frames[0].DeltaVertices[1].z, Is.EqualTo(Ramp(n, 0.004f)[1].z * 0.25f).Within(1e-7f));
        }

        [Test]
        public void ImportBlendShapesWithOverwrite_ReplacesTheSameNameAndKeepsTheOldShapeInTheHistory()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("Smile", 100f, Ramp(n, 0.009f), null, null);
            mesh.AddBlendShapeFrame("Open", 100f, Ramp(n, 0.007f), null, null);
            ShapeKeyEntry smile = Entry("Smile", Ramp(n, 0.002f));
            GP02ShapeKeyData data = NewData(mesh, smile);

            NextUndoGroup();
            List<ShapeKeyEntry> added = ShapeKeyListOperations.ImportAllBlendShapes(data, mesh, true);
            NextUndoGroup();

            Assert.That(Names(data), Is.EqualTo(new[] { "Smile", "Open" }), "同じ名前は新しく作らない");
            Assert.That(added[0], Is.SameAs(smile));
            AssertSame(Ramp(n, 0.009f), smile.Frames[0].DeltaVertices, 1e-7f);
            Assert.That(smile.History.Count, Is.EqualTo(1));
            AssertSame(Ramp(n, 0.002f), smile.History[0].Frames[0].DeltaVertices, 0f);
            Assert.That(smile.History[0].Reason, Is.Not.Empty);

            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "Smile" }));
            AssertSame(Ramp(n, 0.002f), data.Entries[0].Frames[0].DeltaVertices, 0f);

            // 上書きしない取り込みは、従来どおり別名で加える
            ShapeKeyListOperations.ImportBlendShapes(data, mesh, new[] { 0 });
            Assert.That(Names(data), Is.EqualTo(new[] { "Smile", "Smile (1)" }));
        }

        [Test]
        public void ImportFromMeshAndClipWithOverwrite_ReplaceTheEntryInPlace()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("A", 100f, Ramp(n, 0.01f), null, null);
            ShapeKeyEntry existing = Entry("Smile", Ramp(n, 0.001f));
            GP02ShapeKeyData data = NewData(mesh, existing);
            var clip = Track(new AnimationClip { name = "Smile" });
            clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.A", AnimationCurve.Constant(0f, 1f, 50f));

            ShapeKeyEntry result = ShapeKeyListOperations.ImportFromClip(data, clip, "Body", 0.5f, true);

            Assert.That(result, Is.SameAs(existing));
            Assert.That(data.Entries.Count, Is.EqualTo(1));
            Assert.That(existing.History.Count, Is.EqualTo(1));
            AssertSame(Combine(Ramp(n, 0.01f), 0.5f, Ramp(n, 0.01f), 0f), existing.Frames[0].DeltaVertices, 1e-7f);
        }

        // ---- AnimationClip のすべてのキーフレーム ----

        static AnimationClip KeyframeClip(string name, params (string shape, float[] times, float[] values)[] curves)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (shape, times, values) in curves)
            {
                var keys = new Keyframe[times.Length];
                for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(times[i], values[i]);
                var curve = new AnimationCurve(keys);
                for (int i = 0; i < keys.Length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                }
                clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape, curve);
            }
            return clip;
        }

        [Test]
        public void ImportFromClipKeyframes_MakesOneInBetweenFramePerKeyframeAndSkipsTheNeutralTime()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("A", 100f, Ramp(n, 0.01f), null, null);
            mesh.AddBlendShapeFrame("B", 100f, Ramp(n, 0.02f), null, null);
            AnimationClip clip = Track(KeyframeClip("Face",
                ("A", new[] { 0f, 0.5f, 1f }, new[] { 0f, 100f, 40f }),
                ("B", new[] { 0f, 1f }, new[] { 0f, 100f })));
            GP02ShapeKeyData data = NewData(mesh);

            NextUndoGroup();
            ShapeKeyEntry entry = ShapeKeyListOperations.ImportFromClipKeyframes(data, clip, "Body");
            NextUndoGroup();

            Assert.That(entry.Name, Is.EqualTo("Face"));
            Assert.That(entry.Frames.Count, Is.EqualTo(2), "時刻 0 は元の形なので、フレームにしない");
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(50f));
            Assert.That(entry.Frames[1].Weight, Is.EqualTo(100f));
            Assert.That(entry.InBetweenMode, Is.EqualTo(ShapeKeyInBetweenMode.Crossfade));
            AssertSame(Combine(Ramp(n, 0.01f), 1f, Ramp(n, 0.02f), 0.5f), entry.Frames[0].DeltaVertices, 1e-6f);
            AssertSame(Combine(Ramp(n, 0.01f), 0.4f, Ramp(n, 0.02f), 1f), entry.Frames[1].DeltaVertices, 1e-6f);
            Assert.That(entry.CreatedTicks, Is.GreaterThan(0));

            // 重み 50 で評価すると、時刻 0.5 の形になる
            ShapeKeyFrame at50 = ShapeKeyLibrary.EvaluateEntry(entry, 50f, n);
            AssertSame(entry.Frames[0].DeltaVertices, at50.DeltaVertices, 1e-6f);

            Undo.PerformUndo();
            Assert.That(data.Entries, Is.Empty);
        }

        [Test]
        public void ImportFromClipKeyframes_SingleKeyAndErrors()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("A", 100f, Ramp(n, 0.01f), null, null);
            GP02ShapeKeyData data = NewData(mesh);

            AnimationClip single = Track(KeyframeClip("One", ("A", new[] { 0f }, new[] { 60f })));
            ShapeKeyEntry one = ShapeKeyListOperations.ImportFromClipKeyframes(data, single, "Body");
            Assert.That(one.Frames.Count, Is.EqualTo(1));
            Assert.That(one.Frames[0].Weight, Is.EqualTo(100f));
            AssertSame(Combine(Ramp(n, 0.01f), 0.6f, Ramp(n, 0.01f), 0f), one.Frames[0].DeltaVertices, 1e-6f);

            var times = new float[ShapeKeyLibrary.MaxClipFrames + 1];
            var values = new float[times.Length];
            for (int i = 0; i < times.Length; i++)
            {
                times[i] = i * 0.1f;
                values[i] = 10f + i;
            }
            AnimationClip many = Track(KeyframeClip("Many", ("A", times, values)));
            Assert.Throws<InvalidOperationException>(() => ShapeKeyListOperations.ImportFromClipKeyframes(data, many, "Body"));
            Assert.Throws<InvalidOperationException>(() => ShapeKeyListOperations.ImportFromClipKeyframes(data, single, "Missing"));
            AnimationClip zero = Track(KeyframeClip("Zero", ("A", new[] { 0f, 1f }, new[] { 0f, 0f })));
            Assert.Throws<InvalidOperationException>(() => ShapeKeyListOperations.ImportFromClipKeyframes(data, zero, "Body"));
        }

        // ---- 並べ替え・検索 ----

        [Test]
        public void OrderForDisplay_SortsByNameOrCreationWithoutChangingTheList_AndSortEntriesAppliesIt()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            GP02ShapeKeyData data = NewData(mesh);
            var added = new List<ShapeKeyEntry>();
            foreach (string name in new[] { "mouth", "Brow", "arm", "Cheek" }) added.Add(ShapeKeyListOperations.AddEntry(data, Entry(name, Ramp(n, 0.001f))));
            ShapeKeyEntry old = Entry("legacy", Ramp(n, 0.001f));   // 日時の無い古いデータ
            data.Entries.Add(old);

            List<ShapeKeyEntry> byName = ShapeKeyListOperations.OrderForDisplay(data.Entries, ShapeKeyListSort.Name);
            Assert.That(byName.ConvertAll(e => e.Name), Is.EqualTo(new[] { "arm", "Brow", "Cheek", "legacy", "mouth" }), "大文字小文字を区別しない");
            List<ShapeKeyEntry> byCreated = ShapeKeyListOperations.OrderForDisplay(data.Entries, ShapeKeyListSort.Created);
            Assert.That(byCreated.ConvertAll(e => e.Name), Is.EqualTo(new[] { "legacy", "mouth", "Brow", "arm", "Cheek" }), "日時の無いものが先頭、あとは作った順");
            Assert.That(Names(data), Is.EqualTo(new[] { "mouth", "Brow", "arm", "Cheek", "legacy" }), "元の並びは変わらない");
            Assert.That(ShapeKeyListOperations.OrderForDisplay(data.Entries, ShapeKeyListSort.DataOrder), Is.EqualTo(data.Entries));

            NextUndoGroup();
            ShapeKeyListOperations.SortEntries(data, ShapeKeyListSort.Name);
            NextUndoGroup();
            Assert.That(Names(data), Is.EqualTo(new[] { "arm", "Brow", "Cheek", "legacy", "mouth" }));
            Undo.PerformUndo();
            Assert.That(Names(data), Is.EqualTo(new[] { "mouth", "Brow", "arm", "Cheek", "legacy" }));
        }

        [Test]
        public void MatchesFilter_RequiresEveryWordCaseInsensitively()
        {
            ShapeKeyEntry e = Entry("Mouth_Open_Wide", new Vector3[1]);
            Assert.That(ShapeKeyListOperations.MatchesFilter(e, ""), Is.True);
            Assert.That(ShapeKeyListOperations.MatchesFilter(e, "   "), Is.True);
            Assert.That(ShapeKeyListOperations.MatchesFilter(e, "open"), Is.True);
            Assert.That(ShapeKeyListOperations.MatchesFilter(e, "MOUTH wide"), Is.True);
            Assert.That(ShapeKeyListOperations.MatchesFilter(e, "mouth brow"), Is.False);
            Assert.That(ShapeKeyListOperations.MatchesFilter(e, "x"), Is.False);
            Assert.That(ShapeKeyListOperations.MatchesFilter(null, "x"), Is.False);
        }

        // ---- ボーンウェイトの付け替え（左右反転）----

        Mesh MeshWithArmBones(out string[] boneNames)
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            var weights = new BoneWeight[n];
            Vector3[] v = mesh.vertices;
            for (int i = 0; i < n; i++)
            {
                // 左側（x が小さい）は Arm.L(0) と Spine(2)、右側は Arm.R(1) と Spine(2)
                bool left = v[i].x < Center;
                weights[i] = new BoneWeight { boneIndex0 = left ? 0 : 1, weight0 = 0.7f, boneIndex1 = 2, weight1 = 0.3f };
            }
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
            boneNames = new[] { "Arm.L", "Arm.R", "Spine" };
            return mesh;
        }

        [Test]
        public void RemapBoneWeights_Mirror_SwapsTheBonesOfVerticesThatCrossedToTheOtherSide()
        {
            Mesh mesh = MeshWithArmBones(out string[] names);
            int n = mesh.vertexCount;
            Vector3[] v = mesh.vertices;
            var deltas = new Vector3[n];
            // 左端の列は反対側へ大きく動かす。左の中ほどの列は同じ側のまま少し動く
            for (int i = 0; i < n; i++)
            {
                if (v[i].x < 0.001f) deltas[i] = new Vector3(Size * 0.8f, 0f, 0f);
                else if (Mathf.Abs(v[i].x - 0.04f) < 0.001f) deltas[i] = new Vector3(0.06f, 0f, 0f);
            }

            Mesh remapped = Track(ShapeKeyLibrary.RemapBoneWeights(mesh, deltas, new BoneWeightRemapOptions
            {
                Mode = BoneWeightRemapMode.Mirror, Threshold = 0.05f, Axis = 0, Center = Center, BoneNames = names,
            }));

            BoneWeight[] after = remapped.boneWeights;
            for (int i = 0; i < n; i++)
            {
                bool crossed = v[i].x < 0.001f;
                int expectedBone = crossed ? 1 : (v[i].x < Center ? 0 : 1);
                Assert.That(after[i].boneIndex0, Is.EqualTo(expectedBone), $"頂点 {i}");
                Assert.That(after[i].weight0, Is.EqualTo(0.7f).Within(1e-5f));
                Assert.That(after[i].boneIndex1, Is.EqualTo(2), "背骨は相手がないのでそのまま");
            }
            Assert.That(mesh.boneWeights[0].boneIndex0, Is.EqualTo(0), "元のメッシュは変わらない");
            Assert.Throws<ArgumentException>(() => ShapeKeyLibrary.RemapBoneWeights(mesh, deltas, new BoneWeightRemapOptions { Mode = BoneWeightRemapMode.Mirror }));
        }

        [Test]
        public void RemapBoneWeights_SmoothingBlendsTheRemappedVerticesWithTheirNeighbors()
        {
            Mesh mesh = MeshWithArmBones(out string[] names);
            int n = mesh.vertexCount;
            Vector3[] v = mesh.vertices;
            var deltas = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (v[i].x < 0.001f) deltas[i] = new Vector3(Size * 0.8f, 0f, 0f);
            }
            var options = new BoneWeightRemapOptions
            {
                Mode = BoneWeightRemapMode.Mirror, Threshold = 0.05f, Axis = 0, Center = Center, BoneNames = names, SmoothIterations = 2,
            };

            Mesh smoothed = Track(ShapeKeyLibrary.RemapBoneWeights(mesh, deltas, options));

            BoneWeight[] after = smoothed.boneWeights;
            bool anyMixed = false;
            for (int i = 0; i < n; i++)
            {
                float sum = after[i].weight0 + after[i].weight1 + after[i].weight2 + after[i].weight3;
                Assert.That(sum, Is.EqualTo(1f).Within(1e-4f), $"頂点 {i}");
                bool hasLeft = after[i].boneIndex0 == 0 || after[i].boneIndex1 == 0 || after[i].boneIndex2 == 0;
                if (v[i].x < 0.001f && hasLeft) anyMixed = true;
            }
            Assert.That(anyMixed, Is.True, "付け替えた頂点に、隣（元の左のボーン）のウェイトが混ざる");
        }

        [Test]
        public void RemapBoneWeights_NearestWithSmoothing_StillTakesTheNearestVertexFirst()
        {
            Mesh mesh = MeshWithArmBones(out _);
            int n = mesh.vertexCount;
            Vector3[] v = mesh.vertices;
            var deltas = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (v[i].x < 0.001f) deltas[i] = new Vector3(Size * 0.9f, 0f, 0f);
            }

            Mesh plain = Track(ShapeKeyLibrary.RemapBoneWeights(mesh, deltas, 0.05f));
            Mesh smoothed = Track(ShapeKeyLibrary.RemapBoneWeights(mesh, deltas, new BoneWeightRemapOptions { Threshold = 0.05f, SmoothIterations = 1 }));

            Assert.That(plain.boneWeights[0].boneIndex0, Is.EqualTo(1), "動いた先（右端）の頂点のウェイトをもらう");
            Assert.That(smoothed.boneWeights[0].boneIndex0, Is.EqualTo(1));
        }
    }
}
