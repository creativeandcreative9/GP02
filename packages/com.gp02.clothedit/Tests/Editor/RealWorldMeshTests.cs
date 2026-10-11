using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 実際の衣装で起きそうな問題を、プログラムで作った形で再現して確かめる
    /// （重ね着、5本以上のボーンウェイト、きれいでないメッシュ、小さな部品）。購入したデータは使わない。
    /// </summary>
    public class RealWorldMeshTests
    {
        const float Tolerance = 1e-4f;

        readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // ---- 形を作る道具 ----

        // XY 平面の正方形の布（中心が原点）。頂点は (divisions+1)^2 個
        static void AddGrid(List<Vector3> vertices, List<int> triangles, float size, int divisions, float z, bool flip = false)
        {
            int baseIndex = vertices.Count;
            int stride = divisions + 1;
            for (int r = 0; r <= divisions; r++)
            for (int c = 0; c <= divisions; c++)
            {
                vertices.Add(new Vector3((c / (float)divisions - 0.5f) * size, (r / (float)divisions - 0.5f) * size, z));
            }
            for (int r = 0; r < divisions; r++)
            for (int c = 0; c < divisions; c++)
            {
                int i = baseIndex + r * stride + c;
                if (!flip)
                {
                    triangles.AddRange(new[] { i, i + stride, i + 1, i + 1, i + stride, i + stride + 1 });
                }
                else
                {
                    triangles.AddRange(new[] { i, i + 1, i + stride, i + 1, i + stride + 1, i + stride });
                }
            }
        }

        Mesh NewMesh(List<Vector3> vertices, List<int> triangles)
        {
            var mesh = Track(new Mesh { name = "TestMesh" });
            mesh.indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        MeshRenderer NewMeshRenderer(Mesh mesh, string name = "Target")
        {
            GameObject go = Track(new GameObject(name));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        static Ray FromFront(float x, float y) => new Ray(new Vector3(x, y, 1f), Vector3.back);

        static void AssertFinite(Vector3[] values, string what)
        {
            for (int i = 0; i < values.Length; i++)
            {
                Vector3 v = values[i];
                bool ok = !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
                Assert.That(ok, Is.True, $"{what}[{i}] = {v}");
            }
        }

        // 当たった点でつまみ、法線方向へ引っ張って解く。保存用の差分まで作り、差分を返す
        static Vector3[] PullAndBuildDeltas(ClothEditSession session, Ray ray, out int hitTriangle)
        {
            session.Solver.InfluenceRadius = 0.15f;
            Assert.That(session.Raycast(ray, out Vector3 hit, out Vector3 normal, out hitTriangle), Is.True);
            Assert.That(session.BeginGrab(hit, 0.05f, hitTriangle), Is.True);
            session.DragTo(hit + normal * 0.05f);
            for (int i = 0; i < 6; i++) session.Solver.Solve(10);
            session.Release();
            session.UpdateCurrentShape();
            AssertFinite(session.CurrentWorldVertices, "CurrentWorldVertices");

            var deltaVertices = new Vector3[session.VertexCount];
            var deltaNormals = new Vector3[session.VertexCount];
            session.BuildMeshSpaceDeltas(deltaVertices, deltaNormals);
            AssertFinite(deltaVertices, "deltaVertices");
            AssertFinite(deltaNormals, "deltaNormals");
            return deltaVertices;
        }

        static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float tolerance)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Mathf.Abs(Vector3.Dot(p - a, n.normalized)) > tolerance) return false;
            float areaTotal = n.magnitude;
            float s1 = Vector3.Cross(b - p, c - p).magnitude, s2 = Vector3.Cross(c - p, a - p).magnitude, s3 = Vector3.Cross(a - p, b - p).magnitude;
            return s1 + s2 + s3 <= areaTotal * (1f + 1e-3f) + 1e-9f;
        }

        // ---- 1・2. 重ね着 ----

        // 外側（z=0）の布と、その 3mm 内側（z=-3mm）の布。粒子は 1cm 間隔なので、内側の粒子のほうが近いことが起きる
        ClothEditSession NewLayeredSession(out int outerIsland, out int innerIsland)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddGrid(vertices, triangles, 0.4f, 8, 0f);
            int outerTriangleCount = triangles.Count / 3;
            AddGrid(vertices, triangles, 0.4f, 8, -0.003f);

            var session = new ClothEditSession(NewMeshRenderer(NewMesh(vertices, triangles), "Layered"));
            MeshTopologyAccess(session, 0, out outerIsland);
            MeshTopologyAccess(session, outerTriangleCount, out innerIsland);
            Assert.That(outerIsland, Is.Not.EqualTo(innerIsland));
            return session;
        }

        static void MeshTopologyAccess(ClothEditSession session, int meshTriangle, out int island)
        {
            var topology = session.Cloth.Topology;
            island = topology.TriangleIsland[topology.SourceToTriangle[meshTriangle]];
        }

        static IEnumerable<Vector2> ScanPoints()
        {
            for (int i = 0; i < 20; i++)
            for (int j = 0; j < 20; j++) yield return new Vector2(-0.15f + 0.3f * i / 19f, -0.15f + 0.3f * j / 19f);
        }

        [Test]
        public void BeginGrab_WithHitTriangle_PicksParticleOfTheClothThatWasClicked()
        {
            using (ClothEditSession session = NewLayeredSession(out int outer, out int inner))
            {
                int[] particleIsland = session.Cloth.ParticleIsland;
                int innerPicksWithoutTriangle = 0, total = 0;
                foreach (Vector2 xy in ScanPoints())
                {
                    Assert.That(session.Raycast(FromFront(xy.x, xy.y), out Vector3 hit, out _, out int triangle), Is.True);
                    // 手前（外側）の布に当たる
                    Assert.That(session.Cloth.Topology.TriangleIsland[session.Cloth.Topology.SourceToTriangle[triangle]], Is.EqualTo(outer));

                    // 三角形を渡さない従来の呼び方では、内側の粒子のほうが近いと内側をつまんでしまう
                    Assert.That(session.BeginGrab(hit, 0.03f), Is.True);
                    if (particleIsland[session.Solver.GrabbedParticle] == inner) innerPicksWithoutTriangle++;
                    session.Release();
                    total++;

                    Assert.That(session.BeginGrab(hit, 0.03f, triangle), Is.True);
                    Assert.That(particleIsland[session.Solver.GrabbedParticle], Is.EqualTo(outer), $"({xy.x}, {xy.y})");
                    // つまむ範囲も、外側の布の中だけ
                    for (int i = 0; i < particleIsland.Length; i++)
                    {
                        if (session.Solver.IsGrabbed(i)) Assert.That(particleIsland[i], Is.EqualTo(outer));
                    }
                    session.Release();
                }
                // 再現できていること（できていなければ、このテストは何も確かめていない）
                Assert.That(innerPicksWithoutTriangle, Is.GreaterThan(total / 10), "内側をつまむ場面が再現できていません");
                Debug.Log($"[GP02] 重ね着: 三角形なしだと {innerPicksWithoutTriangle}/{total} 回、内側の布をつまんだ");
            }
        }

        [Test]
        public void PaintPins_WithHitTriangle_PinsOnlyTheClothThatWasClicked()
        {
            using (ClothEditSession session = NewLayeredSession(out int outer, out int inner))
            {
                int[] particleIsland = session.Cloth.ParticleIsland;
                Assert.That(session.Raycast(FromFront(0.02f, -0.03f), out Vector3 hit, out _, out int triangle), Is.True);

                // 従来の呼び方は、内側の粒子も留める
                Assert.That(session.PaintPins(hit, 0.025f, true), Is.GreaterThan(0));
                Assert.That(CountPinned(session, inner), Is.GreaterThan(0));
                Assert.That(session.PaintPins(hit, 0.025f, false), Is.GreaterThan(0));
                Assert.That(session.PinnedCount, Is.EqualTo(0));

                int changed = session.PaintPins(hit, 0.025f, true, triangle);
                Assert.That(changed, Is.GreaterThan(0));
                Assert.That(CountPinned(session, outer), Is.EqualTo(changed));
                Assert.That(CountPinned(session, inner), Is.EqualTo(0));
                Assert.That(session.PinnedCount, Is.EqualTo(changed));

                // 解除も同じ層だけ。内側にもピンがあれば残る
                session.PaintPins(hit, 0.025f, true);
                int innerPinned = CountPinned(session, inner);
                Assert.That(innerPinned, Is.GreaterThan(0));
                session.PaintPins(hit, 0.025f, false, triangle);
                Assert.That(CountPinned(session, outer), Is.EqualTo(0));
                Assert.That(CountPinned(session, inner), Is.EqualTo(innerPinned));
                Assert.That(particleIsland.Length, Is.GreaterThan(0));
            }
        }

        static int CountPinned(ClothEditSession session, int island)
        {
            int count = 0;
            int[] particleIsland = session.Cloth.ParticleIsland;
            for (int i = 0; i < particleIsland.Length; i++)
            {
                if (particleIsland[i] == island && session.Solver.IsPinned(i)) count++;
            }
            return count;
        }

        [Test]
        public void PinNear_WithIslands_CarriesPinsOnlyToTheSameLayer()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddGrid(vertices, triangles, 0.4f, 8, 0f);
            AddGrid(vertices, triangles, 0.4f, 8, -0.003f);
            MeshRenderer renderer = NewMeshRenderer(NewMesh(vertices, triangles), "LayeredPins");

            var positions = new List<Vector3>();
            var islands = new List<int>();
            int pinned;
            using (var first = new ClothEditSession(renderer))
            {
                Assert.That(first.Raycast(FromFront(0.05f, 0.05f), out Vector3 hit, out _, out int triangle), Is.True);
                pinned = first.PaintPins(hit, 0.03f, true, triangle);
                Assert.That(pinned, Is.GreaterThan(0));
                first.GetPinnedPositions(positions, islands);
                Assert.That(positions.Count, Is.EqualTo(pinned));
                Assert.That(islands.Count, Is.EqualTo(pinned));
            }

            using (var withIslands = new ClothEditSession(renderer))
            using (var withoutIslands = new ClothEditSession(renderer))
            {
                Assert.That(withIslands.PinNear(positions, withIslands.Cloth.Spacing * 0.75f, islands), Is.EqualTo(pinned));
                // 位置だけだと、3mm 内側の布の粒子まで留めてしまう
                Assert.That(withoutIslands.PinNear(positions, withoutIslands.Cloth.Spacing * 0.75f), Is.GreaterThan(pinned));
            }
        }

        // ---- 3. 5本以上のボーンウェイト ----

        // 頂点ごとに 6 本のウェイトを持つ SkinnedMeshRenderer（ボーン 6 本、それぞれ違うポーズ）
        SkinnedMeshRenderer NewSixBoneSkinnedMesh(out float[][] weights, out int[][] boneIndices, out Matrix4x4[] skin)
        {
            var quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            Mesh mesh = Track(Object.Instantiate(quad));
            int vertexCount = mesh.vertexCount;
            const int boneCount = 6;

            GameObject root = Track(new GameObject("SixBoneRoot"));
            root.transform.SetPositionAndRotation(new Vector3(0.2f, 1f, -0.4f), Quaternion.Euler(5f, 30f, -10f));
            var bones = new Transform[boneCount];
            var bindposes = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                GameObject bone = Track(new GameObject("Bone" + b));
                bone.transform.SetParent(root.transform, false);
                bones[b] = bone.transform;
                bindposes[b] = bone.transform.worldToLocalMatrix * root.transform.localToWorldMatrix;
            }
            mesh.bindposes = bindposes;

            // 大きい順に並べる（GetAllBoneWeights の順）。頂点ごとにボーンの割り当てを回す
            float[] sorted = { 0.34f, 0.24f, 0.17f, 0.12f, 0.08f, 0.05f };
            weights = new float[vertexCount][];
            boneIndices = new int[vertexCount][];
            var bonesPerVertex = new NativeArray<byte>(vertexCount, Allocator.Temp);
            var allWeights = new NativeArray<BoneWeight1>(vertexCount * boneCount, Allocator.Temp);
            for (int v = 0; v < vertexCount; v++)
            {
                bonesPerVertex[v] = boneCount;
                weights[v] = sorted;
                boneIndices[v] = new int[boneCount];
                for (int k = 0; k < boneCount; k++)
                {
                    int bone = (v + k) % boneCount;
                    boneIndices[v][k] = bone;
                    allWeights[v * boneCount + k] = new BoneWeight1 { boneIndex = bone, weight = sorted[k] };
                }
            }
            mesh.SetBoneWeights(bonesPerVertex, allWeights);
            bonesPerVertex.Dispose();
            allWeights.Dispose();

            var renderer = root.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[0];

            // ボーンごとに違うポーズ
            for (int b = 0; b < boneCount; b++)
            {
                bones[b].localPosition = new Vector3(0.03f * b, -0.02f * b * (b % 2 == 0 ? 1 : -1), 0.01f * b);
                bones[b].localRotation = Quaternion.Euler(12f * b, -9f * b, 17f * (b % 3));
            }
            skin = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++) skin[b] = bones[b].localToWorldMatrix * bindposes[b];
            return renderer;
        }

        // 上位 limit 本だけを使い、合計 1 に正規化したときの、頂点の行列
        static Matrix4x4 ExpectedMatrix(float[] weights, int[] boneIndices, Matrix4x4[] skin, int limit)
        {
            int n = Mathf.Min(limit, weights.Length);
            float total = 0f;
            for (int k = 0; k < n; k++) total += weights[k];
            var sum = new Matrix4x4();
            for (int k = 0; k < n; k++)
            {
                for (int e = 0; e < 16; e++) sum[e] += skin[boneIndices[k]][e] * (weights[k] / total);
            }
            return sum;
        }

        [Test]
        public void GetBoneWeightLimit_FollowsQualityAndProjectSetting()
        {
            GameObject go = Track(new GameObject("LimitTarget"));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            SkinWeights original = QualitySettings.skinWeights;
            try
            {
                renderer.quality = SkinQuality.Bone1;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(1));
                renderer.quality = SkinQuality.Bone2;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(2));
                renderer.quality = SkinQuality.Bone4;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(4));

                renderer.quality = SkinQuality.Auto;
                QualitySettings.skinWeights = SkinWeights.OneBone;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(1));
                QualitySettings.skinWeights = SkinWeights.TwoBones;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(2));
                QualitySettings.skinWeights = SkinWeights.FourBones;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(4));
                QualitySettings.skinWeights = SkinWeights.Unlimited;
                Assert.That(ClothEditSession.GetBoneWeightLimit(renderer), Is.EqualTo(int.MaxValue));
            }
            finally
            {
                QualitySettings.skinWeights = original;
            }
        }

        [Test]
        public void SixBoneWeights_VertexMatricesUseOnlyTheTopWeightsUnityUses()
        {
            SkinnedMeshRenderer renderer = NewSixBoneSkinnedMesh(out float[][] weights, out int[][] boneIndices, out Matrix4x4[] skin);
            foreach (SkinQuality quality in new[] { SkinQuality.Bone1, SkinQuality.Bone2, SkinQuality.Bone4 })
            {
                renderer.quality = quality;
                int limit = ClothEditSession.GetBoneWeightLimit(renderer);
                using (var session = new ClothEditSession(renderer))
                {
                    for (int v = 0; v < session.VertexCount; v++)
                    {
                        Matrix4x4 expected = ExpectedMatrix(weights[v], boneIndices[v], skin, limit);
                        for (int e = 0; e < 16; e++)
                        {
                            Assert.That(session.GetVertexMatrix(v)[e], Is.EqualTo(expected[e]).Within(1e-5f), $"{quality} 頂点 {v} 要素 {e}");
                        }
                    }
                }
            }
        }

        [Test]
        public void SixBoneWeights_SavedShapeMatchesUnitySkinning()
        {
            SkinnedMeshRenderer renderer = NewSixBoneSkinnedMesh(out float[][] weights, out int[][] boneIndices, out Matrix4x4[] skin);

            // 6本全部を使った場合と4本に絞った場合で、行列が見て分かるほど違う（違わなければ、このテストは何も確かめていない）
            float difference = 0f;
            for (int v = 0; v < weights.Length; v++)
            {
                Matrix4x4 all = ExpectedMatrix(weights[v], boneIndices[v], skin, int.MaxValue);
                Matrix4x4 four = ExpectedMatrix(weights[v], boneIndices[v], skin, 4);
                for (int e = 0; e < 16; e++) difference = Mathf.Max(difference, Mathf.Abs(all[e] - four[e]));
            }
            Assert.That(difference, Is.GreaterThan(0.005f));

            // Auto は、プロジェクトの設定（QualitySettings.skinWeights）に従う
            var cases = new (SkinQuality quality, SkinWeights projectSetting)[]
            {
                (SkinQuality.Bone2, SkinWeights.FourBones),
                (SkinQuality.Bone4, SkinWeights.Unlimited),
                (SkinQuality.Auto, SkinWeights.FourBones),
                (SkinQuality.Auto, SkinWeights.TwoBones),
                (SkinQuality.Auto, SkinWeights.Unlimited),
            };
            SkinWeights originalSetting = QualitySettings.skinWeights;
            Mesh original = renderer.sharedMesh;
            try
            {
                foreach (var (quality, projectSetting) in cases)
                {
                    QualitySettings.skinWeights = projectSetting;
                    renderer.quality = quality;
                    using (var session = new ClothEditSession(renderer))
                    {
                        session.Solver.InfluenceRadius = 2f;
                        Assert.That(session.Raycast(RayAtCenter(session), out Vector3 hit, out Vector3 normal, out int triangle), Is.True);
                        Assert.That(session.BeginGrab(hit, 0.1f, triangle), Is.True);
                        session.DragTo(hit + normal * 0.05f);
                        for (int i = 0; i < 6; i++) session.Solver.Solve(10);
                        session.Release();
                        session.UpdateCurrentShape();

                        Mesh saved = Track(session.CreateMeshWithBlendShape("GP02_Test"));
                        renderer.sharedMesh = saved;
                        renderer.SetBlendShapeWeight(saved.GetBlendShapeIndex("GP02_Test"), 100f);

                        Mesh baked = Track(new Mesh());
                        renderer.BakeMesh(baked, true);
                        Vector3[] bakedVertices = baked.vertices;
                        Matrix4x4 toWorld = renderer.transform.localToWorldMatrix;
                        float maxError = 0f;
                        for (int v = 0; v < bakedVertices.Length; v++)
                        {
                            Vector3 world = toWorld.MultiplyPoint3x4(bakedVertices[v]);
                            maxError = Mathf.Max(maxError, Vector3.Distance(world, session.CurrentWorldVertices[v]));
                        }
                        Debug.Log($"[GP02] 6本ウェイト quality={quality} skinWeights={projectSetting}（上限 {ClothEditSession.GetBoneWeightLimit(renderer)} 本）: 保存した形と BakeMesh の最大のずれ {maxError * 1000f:F4} mm");
                        Assert.That(maxError, Is.LessThan(Tolerance), $"{quality} / {projectSetting}");
                    }
                    renderer.sharedMesh = original;
                }
            }
            finally
            {
                QualitySettings.skinWeights = originalSetting;
            }
        }

        static Ray RayAtCenter(ClothEditSession session)
        {
            Vector3 center = Vector3.zero;
            foreach (Vector3 v in session.CurrentWorldVertices) center += v;
            center /= session.CurrentWorldVertices.Length;
            Vector3 normal = Vector3.Cross(session.CurrentWorldVertices[1] - session.CurrentWorldVertices[0],
                session.CurrentWorldVertices[2] - session.CurrentWorldVertices[0]).normalized;
            return new Ray(center + normal, -normal);
        }

        // ---- 4. きれいでないメッシュ ----

        [Test]
        public void BackFaceOverlay_PullsFrontAndBackTogether()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddGrid(vertices, triangles, 0.4f, 12, 0f);
            AddGrid(vertices, triangles, 0.4f, 12, 0f, flip: true);   // 同じ位置・逆向き・別の頂点
            MeshRenderer renderer = NewMeshRenderer(NewMesh(vertices, triangles), "BackFace");

            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(1));
                Vector3[] delta = PullAndBuildDeltas(session, FromFront(0.02f, 0.03f), out _);

                int half = vertices.Count / 2;
                float moved = 0f;
                for (int i = 0; i < half; i++)
                {
                    Assert.That(Vector3.Distance(delta[i], delta[half + i]), Is.LessThan(1e-5f), $"頂点 {i}");
                    moved = Mathf.Max(moved, delta[i].magnitude);
                }
                Assert.That(moved, Is.GreaterThan(0.01f));
            }
        }

        [Test]
        public void ZeroAreaTriangles_AreSkippedByRaycastAndDoNotBreakAnything()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            // 面積 0 の三角形を先頭に置く（計算から外れる三角形があると、三角形の番号がずれる）
            vertices.Add(new Vector3(0.30f, 0.30f, 0f));
            vertices.Add(new Vector3(0.30f, 0.30f, 0f));
            vertices.Add(new Vector3(0.30f, 0.30f, 0f));       // 3頂点が同じ位置
            vertices.Add(new Vector3(-0.2f, -0.2f, 0f));
            vertices.Add(new Vector3(-0.1f, -0.2f, 0f));
            vertices.Add(new Vector3(0.0f, -0.2f, 0f));        // 一直線上
            triangles.AddRange(new[] { 0, 1, 2, 3, 4, 5 });
            int collapsedHead = triangles.Count / 3;
            AddGrid(vertices, triangles, 0.4f, 12, 0f);
            vertices.Add(new Vector3(0.1f, 0.1f, 0f));
            vertices.Add(new Vector3(0.1f, 0.1f, 0f));
            vertices.Add(new Vector3(0.15f, 0.1f, 0f));       // 2頂点だけ同じ位置
            triangles.AddRange(new[] { vertices.Count - 3, vertices.Count - 2, vertices.Count - 1 });

            Mesh mesh = NewMesh(vertices, triangles);
            using (var session = new ClothEditSession(NewMeshRenderer(mesh, "ZeroArea")))
            {
                Assert.That(session.Cloth.Topology.TriangleCount, Is.LessThan(triangles.Count / 3));

                Assert.That(session.Raycast(FromFront(-0.05f, 0.07f), out Vector3 hit, out _, out int triangle), Is.True);
                Assert.That(triangle, Is.GreaterThanOrEqualTo(collapsedHead));
                // 返ってくる番号は mesh.triangles の番号（計算から外れた三角形を数えた番号）
                int[] meshTriangles = mesh.triangles;
                Assert.That(PointInTriangle(hit, session.CurrentWorldVertices[meshTriangles[3 * triangle]],
                    session.CurrentWorldVertices[meshTriangles[3 * triangle + 1]], session.CurrentWorldVertices[meshTriangles[3 * triangle + 2]], 1e-4f), Is.True);

                Vector3[] delta = PullAndBuildDeltas(session, FromFront(-0.05f, 0.07f), out _);
                Assert.That(delta.Length, Is.EqualTo(vertices.Count));
                // 3頂点が同じ位置の三角形は、同じ位置にある頂点が同じだけ動く（NaN にならない）
                Assert.That(delta[0], Is.EqualTo(delta[1]));
            }
        }

        [Test]
        public void UnusedVertices_HaveZeroDelta()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddGrid(vertices, triangles, 0.4f, 12, 0f);
            int unusedA = vertices.Count;
            vertices.Add(new Vector3(0.02f, 0.03f, 0.3f));    // 布の近くにあるが、どの三角形にも使われていない
            vertices.Add(new Vector3(3f, 3f, 3f));
            MeshRenderer renderer = NewMeshRenderer(NewMesh(vertices, triangles), "Unused");

            using (var session = new ClothEditSession(renderer))
            {
                Vector3[] delta = PullAndBuildDeltas(session, FromFront(0.02f, 0.03f), out _);

                Assert.That(delta[unusedA], Is.EqualTo(Vector3.zero));
                Assert.That(delta[unusedA + 1], Is.EqualTo(Vector3.zero));
                float moved = 0f;
                for (int i = 0; i < unusedA; i++) moved = Mathf.Max(moved, delta[i].magnitude);
                Assert.That(moved, Is.GreaterThan(0.01f));
            }
        }

        [Test]
        public void MultipleSubMeshes_TrianglesAreConcatenatedAndTriangleNumbersFollowThem()
        {
            // 2つのサブメッシュ（マテリアル）。左半分と右半分
            var vertices = new List<Vector3>();
            var left = new List<int>();
            var right = new List<int>();
            AddGrid(vertices, left, 0.4f, 8, 0f);
            var rightVertices = new List<Vector3>();
            AddGrid(rightVertices, right, 0.4f, 8, 0f);
            int offset = vertices.Count;
            for (int i = 0; i < rightVertices.Count; i++) vertices.Add(rightVertices[i] + new Vector3(0.5f, 0f, 0f));
            for (int i = 0; i < right.Count; i++) right[i] += offset;

            var mesh = Track(new Mesh { name = "TwoSubMeshes" });
            mesh.SetVertices(vertices);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(left, 0);
            mesh.SetTriangles(right, 1);
            mesh.RecalculateNormals();

            // mesh.triangles は全サブメッシュを順に並べたもの
            int[] all = mesh.triangles;
            Assert.That(all.Length, Is.EqualTo(left.Count + right.Count));
            Assert.That(all[left.Count], Is.EqualTo(right[0]));

            MeshRenderer renderer = NewMeshRenderer(mesh, "SubMeshes");
            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(2));

                // 右のサブメッシュの上の点。返る番号は左のサブメッシュの三角形数より後
                Assert.That(session.Raycast(FromFront(0.55f, 0.05f), out Vector3 hit, out _, out int triangle), Is.True);
                Assert.That(triangle, Is.GreaterThanOrEqualTo(left.Count / 3));
                Assert.That(PointInTriangle(hit, session.CurrentWorldVertices[all[3 * triangle]], session.CurrentWorldVertices[all[3 * triangle + 1]],
                    session.CurrentWorldVertices[all[3 * triangle + 2]], 1e-4f), Is.True);

                Vector3[] delta = PullAndBuildDeltas(session, FromFront(0.55f, 0.05f), out _);
                // つまんだのは右半分だけ
                float leftMoved = 0f, rightMoved = 0f;
                for (int i = 0; i < offset; i++) leftMoved = Mathf.Max(leftMoved, delta[i].magnitude);
                for (int i = offset; i < delta.Length; i++) rightMoved = Mathf.Max(rightMoved, delta[i].magnitude);
                Assert.That(leftMoved, Is.EqualTo(0f));
                Assert.That(rightMoved, Is.GreaterThan(0.01f));

                Mesh saved = Track(session.CreateMeshWithBlendShape("GP02_Sub"));
                Assert.That(saved.subMeshCount, Is.EqualTo(2));
                Assert.That(saved.triangles.Length, Is.EqualTo(all.Length));
            }
        }

        // ---- 5. 小さな部品 ----

        // 大きな布の 2mm 手前に、5mm 四方の四角形（ボタンのつもり）を並べる
        ClothEditSession NewClothWithButtons(out int clothVertexCount, out int buttonFirstTriangle)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddGrid(vertices, triangles, 0.4f, 12, 0f);
            clothVertexCount = vertices.Count;
            buttonFirstTriangle = triangles.Count / 3;
            for (int b = 0; b < 4; b++)
            {
                float x = -0.15f + 0.08f * b, y = 0.05f;
                int i = vertices.Count;
                vertices.Add(new Vector3(x, y, 0.002f));
                vertices.Add(new Vector3(x + 0.005f, y, 0.002f));
                vertices.Add(new Vector3(x, y + 0.005f, 0.002f));
                vertices.Add(new Vector3(x + 0.005f, y + 0.005f, 0.002f));
                triangles.AddRange(new[] { i, i + 2, i + 1, i + 1, i + 2, i + 3 });
            }
            return new ClothEditSession(NewMeshRenderer(NewMesh(vertices, triangles), "Buttons"));
        }

        [Test]
        public void SmallParts_GetParticlesAndStayStillWhenTheClothMoves()
        {
            using (ClothEditSession session = NewClothWithButtons(out int clothVertexCount, out _))
            {
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(5));
                var hasParticle = new bool[5];
                foreach (int island in session.Cloth.ParticleIsland) hasParticle[island] = true;
                Assert.That(hasParticle, Is.All.True);

                // ボタンの隣の布をつまむ
                Vector3[] delta = PullAndBuildDeltas(session, FromFront(0.0f, -0.1f), out _);
                float clothMoved = 0f;
                for (int i = 0; i < clothVertexCount; i++) clothMoved = Mathf.Max(clothMoved, delta[i].magnitude);
                Assert.That(clothMoved, Is.GreaterThan(0.01f));
                for (int i = clothVertexCount; i < delta.Length; i++) Assert.That(delta[i], Is.EqualTo(Vector3.zero), $"頂点 {i}");
            }
        }

        [Test]
        public void SmallParts_AreGrabbedAndMovedAsOnePiece()
        {
            using (ClothEditSession session = NewClothWithButtons(out int clothVertexCount, out int buttonFirstTriangle))
            {
                // 最初のボタン（x = -0.15）の真上から。布より 2mm 手前なので、ボタンに当たる
                Vector3[] delta = PullAndBuildDeltas(session, FromFront(-0.1475f, 0.0525f), out int triangle);

                Assert.That(triangle, Is.InRange(buttonFirstTriangle, buttonFirstTriangle + 1));
                Vector3 first = delta[clothVertexCount];
                Assert.That(first.magnitude, Is.GreaterThan(0.01f));
                for (int k = 1; k < 4; k++) Assert.That(Vector3.Distance(delta[clothVertexCount + k], first), Is.LessThan(1e-4f), $"ボタンの頂点 {k}");
                for (int i = 0; i < clothVertexCount; i++) Assert.That(delta[i], Is.EqualTo(Vector3.zero), $"布の頂点 {i}");
                for (int i = clothVertexCount + 4; i < delta.Length; i++) Assert.That(delta[i], Is.EqualTo(Vector3.zero), $"ほかのボタンの頂点 {i}");
            }
        }
    }
}
