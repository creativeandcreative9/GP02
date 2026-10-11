using System.Collections.Generic;
using System.IO;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// シェイプキーのデータ管理（<see cref="ShapeKeyLibrary"/>）と、編集セッションとの往復を確かめる。
    /// 形はプログラムで作った格子の板。作ったアセットは TearDown で消す。
    /// </summary>
    public class ShapeKeyLibraryTests
    {
        const string TempRoot = "Assets/GP02_ShapeKeyTestTemp";
        const string DataFolder = TempRoot + "/Data";
        const int Cells = 14;
        const float Size = 0.28f;      // 2cm 間隔

        readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
            AssetDatabase.DeleteAsset(TempRoot);
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // ---- 形の用意 ----

        // XY 平面の格子の板。(Cells + 1)^2 個の頂点で、法線は -Z
        Mesh CreateGridMesh(string name = "Grid")
        {
            int stride = Cells + 1;
            var vertices = new Vector3[stride * stride];
            var uv = new Vector2[vertices.Length];
            for (int r = 0; r <= Cells; r++)
            for (int c = 0; c <= Cells; c++)
            {
                vertices[r * stride + c] = new Vector3(Size * c / Cells, Size * r / Cells, 0f);
                uv[r * stride + c] = new Vector2((float)c / Cells, (float)r / Cells);
            }
            var triangles = new List<int>();
            for (int r = 0; r < Cells; r++)
            for (int c = 0; c < Cells; c++)
            {
                int i = r * stride + c;
                triangles.AddRange(new[] { i, i + stride, i + 1, i + 1, i + stride, i + stride + 1 });
            }
            var mesh = Track(new Mesh { name = name });
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            return mesh;
        }

        static Vector3[] Ramp(int count, float scale)
        {
            var deltas = new Vector3[count];
            for (int v = 0; v < count; v++) deltas[v] = new Vector3(0f, 0.001f * (v % 7), scale * (1 + v % 5));
            return deltas;
        }

        static ShapeKeyEntry Entry(string name, Vector3[] deltas, float weight = 100f)
        {
            var entry = new ShapeKeyEntry { Name = name };
            entry.Frames.Add(new ShapeKeyFrame { Weight = weight, DeltaVertices = deltas });
            return entry;
        }

        GP02ShapeKeyData NewData(Mesh mesh, params ShapeKeyEntry[] entries)
        {
            var data = Track(ScriptableObject.CreateInstance<GP02ShapeKeyData>());
            data.SourceMesh = mesh;
            data.VertexCount = mesh.vertexCount;
            data.Entries.AddRange(entries);
            return data;
        }

        static void AssertSame(Vector3[] expected, Vector3[] actual, float tolerance, string what = "")
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(Vector3.Distance(expected[i], actual[i]), Is.LessThanOrEqualTo(tolerance), $"{what} 頂点 {i}");
            }
        }

        static Vector3[] FrameVertices(Mesh mesh, int shape, int frame)
        {
            var vertices = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shape, frame, vertices, null, null);
            return vertices;
        }

        // ---- BuildMesh ----

        [Test]
        public void BuildMesh_AddsEnabledEntriesAndKeepsExistingShapes()
        {
            Mesh source = CreateGridMesh();
            Vector3[] existing = Ramp(source.vertexCount, 0.002f);
            source.AddBlendShapeFrame("Existing", 100f, existing, null, null);
            var disabled = Entry("Off", Ramp(source.vertexCount, 0.01f));
            disabled.Enabled = false;
            GP02ShapeKeyData data = NewData(source, Entry("New", Ramp(source.vertexCount, 0.003f)), disabled);

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, false));

            Assert.That(built, Is.Not.SameAs(source));
            Assert.That(source.blendShapeCount, Is.EqualTo(1), "元のメッシュは変えない");
            Assert.That(built.blendShapeCount, Is.EqualTo(2));
            Assert.That(built.GetBlendShapeName(0), Is.EqualTo("Existing"));
            Assert.That(built.GetBlendShapeName(1), Is.EqualTo("New"));
            AssertSame(existing, FrameVertices(built, 0, 0), 1e-6f, "元の BlendShape");
            AssertSame(Ramp(source.vertexCount, 0.003f), FrameVertices(built, 1, 0), 1e-6f);
            Assert.That(built.vertexCount, Is.EqualTo(source.vertexCount));
        }

        [Test]
        public void BuildMesh_OverwriteReplacesSameNameInPlace()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            source.AddBlendShapeFrame("A", 100f, Ramp(n, 0.001f), null, null);
            source.AddBlendShapeFrame("B", 100f, Ramp(n, 0.002f), null, null);
            source.AddBlendShapeFrame("C", 100f, Ramp(n, 0.003f), null, null);
            GP02ShapeKeyData data = NewData(source, Entry("B", Ramp(n, 0.009f)));

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, true));

            Assert.That(built.blendShapeCount, Is.EqualTo(3));
            Assert.That(built.GetBlendShapeName(0), Is.EqualTo("A"));
            Assert.That(built.GetBlendShapeName(1), Is.EqualTo("B"));
            Assert.That(built.GetBlendShapeName(2), Is.EqualTo("C"));
            AssertSame(Ramp(n, 0.009f), FrameVertices(built, 1, 0), 1e-6f, "B");
            AssertSame(Ramp(n, 0.001f), FrameVertices(built, 0, 0), 1e-6f, "A");
            AssertSame(Ramp(n, 0.003f), FrameVertices(built, 2, 0), 1e-6f, "C");
            AssertSame(Ramp(n, 0.002f), FrameVertices(source, 1, 0), 1e-6f, "元の B は変わらない");
        }

        [Test]
        public void BuildMesh_WithoutOverwriteUsesNumberedName()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            source.AddBlendShapeFrame("B", 100f, Ramp(n, 0.002f), null, null);
            GP02ShapeKeyData data = NewData(source, Entry("B", Ramp(n, 0.009f)), Entry("B", Ramp(n, 0.004f)));

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, false));

            Assert.That(built.blendShapeCount, Is.EqualTo(3));
            Assert.That(built.GetBlendShapeName(0), Is.EqualTo("B"));
            Assert.That(built.GetBlendShapeName(1), Is.EqualTo("B (1)"));
            Assert.That(built.GetBlendShapeName(2), Is.EqualTo("B (2)"));
            AssertSame(Ramp(n, 0.002f), FrameVertices(built, 0, 0), 1e-6f);
            AssertSame(Ramp(n, 0.009f), FrameVertices(built, 1, 0), 1e-6f);
        }

        [Test]
        public void BuildMesh_OverwriteTwiceWithSameNameInDataKeepsLast()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            GP02ShapeKeyData data = NewData(source, Entry("X", Ramp(n, 0.001f)), Entry("X", Ramp(n, 0.005f)));

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, true));

            Assert.That(built.blendShapeCount, Is.EqualTo(1));
            AssertSame(Ramp(n, 0.005f), FrameVertices(built, 0, 0), 1e-6f);
        }

        [Test]
        public void BuildMesh_WritesInBetweenFrames()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            Vector3[] final = Ramp(n, 0.01f);
            var own = new[] { Ramp(n, 0.001f), Ramp(n, -0.001f) };
            ShapeFrame[] frames = ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive,
                MeshBridge.ToNumerics(final, Vector3.one), new[] { 30f, 60f },
                new[] { MeshBridge.ToNumerics(own[0], Vector3.one), MeshBridge.ToNumerics(own[1], Vector3.one) });
            var entry = new ShapeKeyEntry { Name = "InBetween", InBetweenMode = ShapeKeyInBetweenMode.Progressive };
            foreach (ShapeFrame frame in frames)
            {
                entry.Frames.Add(new ShapeKeyFrame { Weight = frame.Weight, DeltaVertices = MeshBridge.ToUnity(frame.Deltas, Vector3.one) });
            }
            GP02ShapeKeyData data = NewData(source, entry);

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, false));

            Assert.That(built.GetBlendShapeFrameCount(0), Is.EqualTo(3));
            Assert.That(built.GetBlendShapeFrameWeight(0, 0), Is.EqualTo(30f));
            Assert.That(built.GetBlendShapeFrameWeight(0, 1), Is.EqualTo(60f));
            Assert.That(built.GetBlendShapeFrameWeight(0, 2), Is.EqualTo(100f));
            AssertSame(final, FrameVertices(built, 0, 2), 1e-6f);
            Vector3[] expectedAt30 = new Vector3[n];
            for (int v = 0; v < n; v++) expectedAt30[v] = final[v] * 0.3f + own[0][v];
            AssertSame(expectedAt30, FrameVertices(built, 0, 0), 1e-6f);
        }

        [Test]
        public void BuildMesh_SortsFramesByWeightAndRejectsWrongVertexCount()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            var entry = Entry("Late", Ramp(n, 0.01f), 100f);
            entry.Frames.Insert(0, new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.02f) });
            entry.Frames[1].Weight = 40f;    // 順番が逆
            GP02ShapeKeyData data = NewData(source, entry);

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, false));
            Assert.That(built.GetBlendShapeFrameWeight(0, 0), Is.EqualTo(40f));
            Assert.That(built.GetBlendShapeFrameWeight(0, 1), Is.EqualTo(100f));

            data.VertexCount = n + 1;
            Assert.Throws<System.ArgumentException>(() => ShapeKeyLibrary.BuildMesh(source, data, false));
            data.VertexCount = n;
            data.Entries[0].Frames[0].DeltaVertices = new Vector3[3];
            Assert.Throws<System.ArgumentException>(() => ShapeKeyLibrary.BuildMesh(source, data, false));
        }

        [Test]
        public void BuildMesh_CarriesNormalDeltas()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            var frame = new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.01f), DeltaNormals = new Vector3[n] };
            for (int v = 0; v < n; v++) frame.DeltaNormals[v] = new Vector3(0.1f, 0f, 0f);
            var entry = new ShapeKeyEntry { Name = "WithNormals" };
            entry.Frames.Add(frame);

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, NewData(source, entry), false));

            var normals = new Vector3[n];
            built.GetBlendShapeFrameVertices(0, 0, new Vector3[n], normals, null);
            Assert.That(normals[3].x, Is.EqualTo(0.1f).Within(1e-6f));
        }

        // ---- BakeIntoBase ----

        [Test]
        public void BakeIntoBase_MovesVerticesByDeltaTimesWeight()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            source.AddBlendShapeFrame("Existing", 100f, Ramp(n, 0.002f), null, null);
            Vector3[] delta = Ramp(n, 0.01f);
            var entry = Entry("Bake", delta);
            entry.Frames[0].DeltaNormals = new Vector3[n];
            for (int v = 0; v < n; v++) entry.Frames[0].DeltaNormals[v] = new Vector3(0.5f, 0f, 0f);
            Vector3[] before = source.vertices;
            Vector3[] normalsBefore = source.normals;

            Mesh baked = Track(ShapeKeyLibrary.BakeIntoBase(source, new[] { entry }, new[] { 50f }));

            Vector3[] after = baked.vertices;
            for (int v = 0; v < n; v++)
            {
                Assert.That(Vector3.Distance(after[v], before[v] + delta[v] * 0.5f), Is.LessThan(1e-6f), $"頂点 {v}");
            }
            // 法線は差分 × 重みを足して正規化
            Vector3 expectedNormal = (normalsBefore[3] + new Vector3(0.25f, 0f, 0f)).normalized;
            Assert.That(Vector3.Distance(baked.normals[3], expectedNormal), Is.LessThan(1e-5f));
            Assert.That(baked.normals[3].magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(baked.blendShapeCount, Is.EqualTo(1), "既存の BlendShape は残る");
            Assert.That(source.vertices[5], Is.EqualTo(before[5]), "元のメッシュは変えない");
        }

        [Test]
        public void BakeIntoBase_InterpolatesBetweenFramesAndDefaultsToPreviewWeight()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            var entry = new ShapeKeyEntry { Name = "Two", PreviewWeight = 75f };
            entry.Frames.Add(new ShapeKeyFrame { Weight = 50f, DeltaVertices = Ramp(n, 0.02f) });
            entry.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.04f) });
            Vector3[] before = source.vertices;

            Mesh baked = Track(ShapeKeyLibrary.BakeIntoBase(source, new[] { entry }, null));

            // 重み 75 は、50 と 100 の真ん中
            Vector3[] middle = Ramp(n, 0.03f);
            for (int v = 0; v < n; v++)
            {
                Assert.That(Vector3.Distance(baked.vertices[v], before[v] + middle[v]), Is.LessThan(1e-6f), $"頂点 {v}");
            }
        }

        // ---- 取り込み ----

        [Test]
        public void ImportFromBlendShape_ReadsAllFrames()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("Other", 100f, Ramp(n, 0.001f), null, null);
            mesh.AddBlendShapeFrame("Multi", 40f, Ramp(n, 0.004f), null, null);
            var normals = new Vector3[n];
            for (int v = 0; v < n; v++) normals[v] = new Vector3(0f, 0.2f, 0f);
            mesh.AddBlendShapeFrame("Multi", 100f, Ramp(n, 0.01f), normals, null);

            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromBlendShape(mesh, 1);

            Assert.That(entry.Name, Is.EqualTo("Multi"));
            Assert.That(entry.Frames.Count, Is.EqualTo(2));
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(40f));
            Assert.That(entry.Frames[1].Weight, Is.EqualTo(100f));
            AssertSame(Ramp(n, 0.004f), entry.Frames[0].DeltaVertices, 1e-6f);
            AssertSame(Ramp(n, 0.01f), entry.Frames[1].DeltaVertices, 1e-6f);
            Assert.That(entry.Frames[0].HasNormals, Is.False);
            Assert.That(entry.Frames[1].DeltaNormals[2].y, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ShapeKeyLibrary.ImportFromBlendShape(mesh, 2));
        }

        [Test]
        public void ImportFromBlendShape_BuildMeshRoundTripKeepsFrames()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("Multi", 40f, Ramp(n, 0.004f), null, null);
            mesh.AddBlendShapeFrame("Multi", 100f, Ramp(n, 0.01f), null, null);
            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromBlendShape(mesh, 0);
            entry.Name = "Copy";

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(mesh, NewData(mesh, entry), false));

            Assert.That(built.GetBlendShapeFrameCount(1), Is.EqualTo(2));
            AssertSame(FrameVertices(mesh, 0, 0), FrameVertices(built, 1, 0), 1e-6f);
            AssertSame(FrameVertices(mesh, 0, 1), FrameVertices(built, 1, 1), 1e-6f);
        }

        [Test]
        public void ImportFromMesh_TakesVertexDifference()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            Mesh edited = Track(Object.Instantiate(source));
            edited.name = "Edited";
            Vector3[] delta = Ramp(n, 0.005f);
            Vector3[] moved = source.vertices;
            for (int v = 0; v < n; v++) moved[v] += delta[v];
            edited.vertices = moved;

            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromMesh(source, edited);

            Assert.That(entry.Name, Is.EqualTo("Edited"));
            Assert.That(entry.Frames.Count, Is.EqualTo(1));
            Assert.That(entry.Frames[0].Weight, Is.EqualTo(100f));
            AssertSame(delta, entry.Frames[0].DeltaVertices, 1e-6f);
        }

        [Test]
        public void ImportFromMesh_DifferentVertexCountExplainsTheProblem()
        {
            Mesh source = CreateGridMesh();
            Mesh other = Track(new Mesh { name = "Other" });
            other.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            other.triangles = new[] { 0, 1, 2 };

            var ex = Assert.Throws<System.ArgumentException>(() => ShapeKeyLibrary.ImportFromMesh(source, other));

            StringAssert.Contains("頂点の数が違う", ex.Message);
            StringAssert.Contains(source.vertexCount.ToString(), ex.Message);
        }

        [Test]
        public void ImportFromAnimationClip_ComposesBlendShapesByCurveValues()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            mesh.AddBlendShapeFrame("A", 100f, Ramp(n, 0.01f), null, null);
            mesh.AddBlendShapeFrame("B", 100f, Ramp(n, 0.02f), null, null);
            mesh.AddBlendShapeFrame("Unused", 100f, Ramp(n, 0.5f), null, null);
            var clip = Track(new AnimationClip { name = "Smile" });
            clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.A", AnimationCurve.Linear(0f, 0f, 1f, 100f));
            clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.B", AnimationCurve.Constant(0f, 1f, 50f));
            clip.SetCurve("Other", typeof(SkinnedMeshRenderer), "blendShape.Unused", AnimationCurve.Constant(0f, 1f, 100f));

            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromAnimationClip(mesh, clip, "Body", 0.5f);

            Assert.That(entry.Name, Is.EqualTo("Smile"));
            Assert.That(entry.Frames.Count, Is.EqualTo(1));
            Vector3[] expected = Combine(Ramp(n, 0.01f), 0.5f, Ramp(n, 0.02f), 0.5f);
            AssertSame(expected, entry.Frames[0].DeltaVertices, 1e-6f);

            Assert.Throws<System.InvalidOperationException>(() => ShapeKeyLibrary.ImportFromAnimationClip(mesh, clip, "Missing", 0.5f));
        }

        static Vector3[] Combine(Vector3[] a, float wa, Vector3[] b, float wb)
        {
            var result = new Vector3[a.Length];
            for (int i = 0; i < a.Length; i++) result[i] = a[i] * wa + b[i] * wb;
            return result;
        }

        // ---- ボーンウェイトの付け替え ----

        [Test]
        public void RemapBoneWeights_MovedVerticesTakeWeightsFromNearestRestVertex()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            int stride = Cells + 1;
            var weights = new BoneWeight[n];
            for (int v = 0; v < n; v++)
            {
                bool upper = v / stride > Cells / 2;
                weights[v] = new BoneWeight { boneIndex0 = upper ? 1 : 0, weight0 = 1f };
            }
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };

            // 一番上の行を、下の行の位置まで下げる（下の行はボーン 0）
            var deltas = new Vector3[n];
            for (int c = 0; c <= Cells; c++) deltas[Cells * stride + c] = new Vector3(0f, -Size * 0.95f, 0f);

            Mesh remapped = Track(ShapeKeyLibrary.RemapBoneWeights(mesh, deltas, 0.05f));

            BoneWeight[] after = remapped.boneWeights;
            for (int v = 0; v < n; v++)
            {
                bool moved = v / stride == Cells;
                int expectedBone = moved ? 0 : (v / stride > Cells / 2 ? 1 : 0);
                Assert.That(after[v].boneIndex0, Is.EqualTo(expectedBone), $"頂点 {v}");
                Assert.That(after[v].weight0, Is.EqualTo(1f).Within(1e-6f));
            }
            // 元のメッシュは変わらない
            Assert.That(mesh.boneWeights[Cells * stride].boneIndex0, Is.EqualTo(1));
            Assert.Throws<System.InvalidOperationException>(() => ShapeKeyLibrary.RemapBoneWeights(CreateGridMesh("NoBones"), deltas, 0.05f));
        }

        // ---- 反転・分割・保存 ----

        [Test]
        public void SplitAndFlipEntries_SurviveSavingTheDataAsset()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            ShapeKeyEntry entry = ShapeKeyLibrary.CreateEntryFromDeltas(source, "Bump", Ramp(n, 0.004f));
            entry.Group = "Body";
            Assert.That(entry.Frames[0].HasNormals, Is.True, "法線の差分も入る");

            ShapeKeyLibrary.SplitEntry(entry, source, 0, Size / 2f, 0.04f, out ShapeKeyEntry left, out ShapeKeyEntry right);
            ShapeKeyEntry flipped = ShapeKeyLibrary.FlipEntry(entry, source, 0, Size / 2f, 1e-4f, "Bump_flip");
            ShapeKeyEntry flippedBack = ShapeKeyLibrary.FlipEntry(flipped, source, 0, Size / 2f, 1e-4f, "Bump_back");

            Assert.That(left.Name, Is.EqualTo("Bump_L"));
            Assert.That(right.Name, Is.EqualTo("Bump_R"));
            Assert.That(left.Group, Is.EqualTo("Body"));
            for (int v = 0; v < n; v++)
            {
                Vector3 sum = left.Frames[0].DeltaVertices[v] + right.Frames[0].DeltaVertices[v];
                Assert.That(Vector3.Distance(sum, entry.Frames[0].DeltaVertices[v]), Is.LessThan(1e-6f), $"分割の和 頂点 {v}");
                Assert.That(Vector3.Distance(flippedBack.Frames[0].DeltaVertices[v], entry.Frames[0].DeltaVertices[v]), Is.LessThan(1e-6f), $"2回反転 頂点 {v}");
                Vector3 sumNormals = left.Frames[0].DeltaNormals[v] + right.Frames[0].DeltaNormals[v];
                Assert.That(Vector3.Distance(sumNormals, entry.Frames[0].DeltaNormals[v]), Is.LessThan(1e-6f), $"法線の分割の和 頂点 {v}");
            }
            // 反転では X だけ逆になって、鏡像の頂点に移る（左下の隅 → 右下の隅）
            Vector3 corner = entry.Frames[0].DeltaVertices[0];
            Vector3 mirroredCorner = flipped.Frames[0].DeltaVertices[Cells];
            Assert.That(Vector3.Distance(mirroredCorner, new Vector3(-corner.x, corner.y, corner.z)), Is.LessThan(1e-6f));

            // アセットに保存して、中身が書き出されること
            var data = ShapeKeyLibrary.CreateData(source, DataFolder);
            Assert.That(AssetDatabase.GetAssetPath(data), Does.StartWith(DataFolder));
            Assert.That(data.VertexCount, Is.EqualTo(n));
            ShapeKeyLibrary.AddEntry(data, left);
            ShapeKeyLibrary.AddEntry(data, right);
            ShapeKeyLibrary.AddEntry(data, flipped);
            ShapeKeyLibrary.SaveData(data);

            string path = AssetDatabase.GetAssetPath(data);
            string text = File.ReadAllText(path);
            StringAssert.Contains("Bump_L", text);
            StringAssert.Contains("Bump_flip", text);
            StringAssert.Contains("DeltaVertices", text);

            // Unity のシリアライザが配列を読み書きできること（Instantiate はシリアライズ経由で複製する）
            var copy = Track(Object.Instantiate(data));
            Assert.That(copy.Entries.Count, Is.EqualTo(3));
            Assert.That(copy.Groups, Is.EquivalentTo(new[] { "Body" }));
            AssertSame(left.Frames[0].DeltaVertices, copy.Entries[0].Frames[0].DeltaVertices, 1e-7f);
            AssertSame(flipped.Frames[0].DeltaNormals, copy.Entries[2].Frames[0].DeltaNormals, 1e-7f);
            Assert.That(copy.SourceMesh, Is.SameAs(source));

            // 保存したデータからメッシュを作ると、3つの BlendShape になる
            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, copy, false));
            Assert.That(built.blendShapeCount, Is.EqualTo(3));
            Assert.That(built.GetBlendShapeIndex("Bump_R"), Is.EqualTo(1));

            Assert.That(ShapeKeyLibrary.RemoveEntry(data, right), Is.True);
            Assert.That(data.Entries.Count, Is.EqualTo(2));
        }

        [Test]
        public void Attach_AddsOneComponentAndSetsReferences()
        {
            Mesh mesh = CreateGridMesh();
            GameObject go = Track(new GameObject("Target"));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            GP02ShapeKeyData data = NewData(mesh);

            GP02ShapeKeys first = ShapeKeyLibrary.Attach(renderer, data);
            GP02ShapeKeys second = ShapeKeyLibrary.Attach(renderer, data);

            Assert.That(second, Is.SameAs(first));
            Assert.That(go.GetComponents<GP02ShapeKeys>().Length, Is.EqualTo(1));
            Assert.That(first.Data, Is.SameAs(data));
            Assert.That(first.SourceMesh, Is.SameAs(mesh));
        }

        // ---- 編集セッションとの往復 ----

        Renderer CreateTarget(Mesh mesh, bool skinned)
        {
            GameObject go = Track(new GameObject(skinned ? "SkinnedTarget" : "MeshFilterTarget"));
            go.transform.SetPositionAndRotation(new Vector3(0.3f, 1f, -0.2f), Quaternion.Euler(20f, 35f, 10f));
            go.transform.localScale = Vector3.one * 1.5f;
            if (!skinned)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                return go.AddComponent<MeshRenderer>();
            }

            GameObject boneA = Track(new GameObject("BoneA"));
            GameObject boneB = Track(new GameObject("BoneB"));
            boneA.transform.SetParent(go.transform, false);
            boneB.transform.SetParent(go.transform, false);
            var weights = new BoneWeight[mesh.vertexCount];
            Vector3[] vertices = mesh.vertices;
            for (int v = 0; v < weights.Length; v++)
            {
                float wb = Mathf.Clamp01(vertices[v].x / Size);
                weights[v] = new BoneWeight { boneIndex0 = 0, weight0 = 1f - wb, boneIndex1 = 1, weight1 = wb };
            }
            mesh.boneWeights = weights;
            mesh.bindposes = new[]
            {
                boneA.transform.worldToLocalMatrix * go.transform.localToWorldMatrix,
                boneB.transform.worldToLocalMatrix * go.transform.localToWorldMatrix,
            };
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { boneA.transform, boneB.transform };
            renderer.rootBone = boneA.transform;
            // ポーズを付けて、頂点ごとの行列が単位行列ではないようにする
            boneB.transform.localRotation = Quaternion.Euler(0f, 0f, 20f);
            boneB.transform.localPosition = new Vector3(0.05f, 0.02f, 0f);
            return renderer;
        }

        // 中央を法線方向へ引っ張る（ClothEditSessionTests.PullCenter と同じ）
        static void PullCenter(ClothEditSession session, float distance = 0.05f)
        {
            session.Solver.InfluenceRadius = 2f;
            Vector3 center = Vector3.zero;
            foreach (Vector3 v in session.CurrentWorldVertices) center += v;
            center /= session.CurrentWorldVertices.Length;

            Vector3 normal = Vector3.Cross(session.CurrentWorldVertices[1] - session.CurrentWorldVertices[0],
                session.CurrentWorldVertices[Cells + 1] - session.CurrentWorldVertices[0]).normalized;
            Assert.That(session.Raycast(new Ray(center + normal, -normal), out Vector3 hit, out Vector3 hitNormal), Is.True);

            Assert.That(session.BeginGrab(hit, 0.1f), Is.True);
            session.DragTo(hit + hitNormal * distance);
            for (int i = 0; i < 6; i++) session.Solver.Solve(10);
            session.Release();
            session.UpdateCurrentShape();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Session_GetThenLoadMeshSpaceDeltasReproducesTheShape(bool skinned)
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateTarget(mesh, skinned);

            Vector3[] deltas;
            Vector3[] editedShape;
            using (var first = new ClothEditSession(renderer))
            {
                PullCenter(first);
                deltas = first.GetMeshSpaceDeltas();
                editedShape = (Vector3[])first.CurrentWorldVertices.Clone();
            }
            Assert.That(deltas.Length, Is.EqualTo(mesh.vertexCount));
            float largest = 0f;
            foreach (Vector3 d in deltas) largest = Mathf.Max(largest, d.magnitude);
            Assert.That(largest, Is.GreaterThan(0.005f), "動いていないテストにならないように");

            using (var second = new ClothEditSession(renderer))
            {
                // 読み込む前は元の形
                Assert.That(Vector3.Distance(second.CurrentWorldVertices[(Cells + 1) * (Cells + 1) / 2], editedShape[(Cells + 1) * (Cells + 1) / 2]), Is.GreaterThan(1e-3f));

                Assert.That(second.LoadMeshSpaceDeltas(deltas), Is.True);

                for (int v = 0; v < editedShape.Length; v++)
                {
                    Assert.That(Vector3.Distance(second.CurrentWorldVertices[v], editedShape[v]), Is.LessThan(0.002f), $"頂点 {v}");
                }
                // もう一度差分にしても、ほぼ同じ
                Vector3[] again = second.GetMeshSpaceDeltas();
                for (int v = 0; v < deltas.Length; v++)
                {
                    Assert.That(Vector3.Distance(again[v], deltas[v]), Is.LessThan(0.004f), $"差分 頂点 {v}");
                }
                // 続きの編集ができる（ソルバーの伸びが小さい）
                Assert.That(second.Solver.MeasureMaxStretch(), Is.LessThan(0.2f));

                Assert.That(second.LoadMeshSpaceDeltas(new Vector3[3]), Is.False, "頂点数が違えば何もしない");
            }
        }

        [Test]
        public void Session_LoadsAnImportedBlendShapeAsTheStartingShape()
        {
            Mesh mesh = CreateGridMesh();
            int n = mesh.vertexCount;
            // なめらかな盛り上がりのシェイプキー
            var bump = new Vector3[n];
            Vector3[] vertices = mesh.vertices;
            for (int v = 0; v < n; v++)
            {
                Vector2 p = new Vector2(vertices[v].x, vertices[v].y) - new Vector2(Size / 2f, Size / 2f);
                bump[v] = new Vector3(0f, 0f, -0.04f * Mathf.Exp(-p.sqrMagnitude / 0.01f));
            }
            mesh.AddBlendShapeFrame("Bump", 100f, bump, null, null);
            Renderer renderer = CreateTarget(mesh, false);
            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromBlendShape(mesh, 0);

            // MeshFilter はシェイプキーを描かないので、セッションの基準は元の頂点
            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.LoadMeshSpaceDeltas(entry.Frames[0].DeltaVertices), Is.True);

                Matrix4x4 m = renderer.transform.localToWorldMatrix;
                for (int v = 0; v < n; v++)
                {
                    Vector3 expected = m.MultiplyPoint3x4(vertices[v] + bump[v]);
                    Assert.That(Vector3.Distance(session.CurrentWorldVertices[v], expected), Is.LessThan(0.002f), $"頂点 {v}");
                }
                // 取り込んだ形からさらに引っ張れる
                PullCenter(session, 0.03f);
                Vector3[] continued = session.GetMeshSpaceDeltas();
                Assert.That(Vector3.Distance(continued[n / 2], bump[n / 2]), Is.GreaterThan(0.005f));
            }
        }
    }
}
