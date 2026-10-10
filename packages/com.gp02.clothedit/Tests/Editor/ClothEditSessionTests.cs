using System.Collections.Generic;
using UnityEditor;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.Tests
{
    /// <summary>
    /// ClothEditSession が、ワールド座標で編集した結果をメッシュ座標のシェイプキー差分に正しく直せるかを確かめる。
    /// 判定の基準は「各頂点で M_v * (v + d) が編集後のワールド位置と一致すること」。
    /// </summary>
    public class ClothEditSessionTests
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

        Mesh CopyOfBuiltinQuad()
        {
            var quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            return Track(Object.Instantiate(quad));
        }

        GameObject NewObject(string name)
        {
            return Track(new GameObject(name));
        }

        // 中央を法線方向へ 5cm つまんで解く。影響範囲を広げ、四隅の頂点も動くようにしてある
        static void PullCenter(ClothEditSession session)
        {
            session.Solver.InfluenceRadius = 2f;
            Vector3 center = Vector3.zero;
            foreach (Vector3 v in session.CurrentWorldVertices) center += v;
            center /= session.CurrentWorldVertices.Length;

            // 四角形の中心あたりへ、法線方向のレイを当てる（ボーンで歪むと厳密には面の上にないので、当たった点を使う）
            Vector3 normal = Vector3.Cross(session.CurrentWorldVertices[1] - session.CurrentWorldVertices[0],
                session.CurrentWorldVertices[2] - session.CurrentWorldVertices[0]).normalized;
            Assert.That(session.Raycast(new Ray(center + normal, -normal), out Vector3 hit, out Vector3 hitNormal), Is.True);

            Assert.That(session.BeginGrab(hit, 0.1f), Is.True);
            session.DragTo(hit + hitNormal * 0.05f);
            // 収束まで解く必要はない（形がどうあれ、差分がワールド形状を再現することを見る）
            for (int i = 0; i < 6; i++) session.Solver.Solve(10);
            session.Release();
            session.UpdateCurrentShape();
        }

        static void AssertDeltasReproduceWorldShape(ClothEditSession session, System.Func<int, Matrix4x4> matrixOf, Vector3[] baseVertices)
        {
            var deltaVertices = new Vector3[session.VertexCount];
            var deltaNormals = new Vector3[session.VertexCount];
            session.BuildMeshSpaceDeltas(deltaVertices, deltaNormals);

            float maxMove = 0f;
            for (int v = 0; v < deltaVertices.Length; v++)
            {
                Vector3 world = matrixOf(v).MultiplyPoint3x4(baseVertices[v] + deltaVertices[v]);
                Assert.That(Vector3.Distance(world, session.CurrentWorldVertices[v]), Is.LessThan(Tolerance), $"頂点 {v}");
                maxMove = Mathf.Max(maxMove, deltaVertices[v].magnitude);
            }
            // 何も動いていないテストにならないように
            Assert.That(maxMove, Is.GreaterThan(0.005f));
        }

        [Test]
        public void MeshFilter_DeltasReproduceEditedWorldShape()
        {
            GameObject go = NewObject("MeshFilterTarget");
            go.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(30f, 50f, 20f));
            go.transform.localScale = Vector3.one * 2f;
            Mesh mesh = CopyOfBuiltinQuad();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();

            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(renderer.forceRenderingOff, Is.True);
                Assert.That(session.ParticleCount, Is.GreaterThan(0));
                PullCenter(session);

                Matrix4x4 m = go.transform.localToWorldMatrix;
                AssertDeltasReproduceWorldShape(session, _ => m, mesh.vertices);
            }
            Assert.That(renderer.forceRenderingOff, Is.False);
        }

        [Test]
        public void SkinnedMesh_DeltasReproduceEditedWorldShape()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            GameObject root = NewObject("SkinnedTarget");
            root.transform.SetPositionAndRotation(new Vector3(0.5f, 0f, -1f), Quaternion.Euler(0f, 25f, 0f));
            GameObject boneA = NewObject("BoneA");
            GameObject boneB = NewObject("BoneB");
            boneA.transform.SetParent(root.transform, false);
            boneB.transform.SetParent(root.transform, false);

            // 頂点ごとに異なるウェイト（2本のボーンの合計は 1）
            float[] weightB = { 0f, 0.3f, 0.7f, 1f };
            var boneWeights = new BoneWeight[mesh.vertexCount];
            for (int v = 0; v < boneWeights.Length; v++)
            {
                float wb = weightB[v % weightB.Length];
                boneWeights[v] = new BoneWeight { boneIndex0 = 0, weight0 = 1f - wb, boneIndex1 = 1, weight1 = wb };
            }
            mesh.boneWeights = boneWeights;
            mesh.bindposes = new[]
            {
                boneA.transform.worldToLocalMatrix * root.transform.localToWorldMatrix,
                boneB.transform.worldToLocalMatrix * root.transform.localToWorldMatrix,
            };

            var renderer = root.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { boneA.transform, boneB.transform };
            renderer.rootBone = boneA.transform;

            // 片方のボーンだけ回転・移動してポーズを付ける
            boneB.transform.localRotation = Quaternion.Euler(0f, 0f, 35f);
            boneB.transform.localPosition = new Vector3(0.1f, 0.05f, 0f);

            Matrix4x4 skinA = boneA.transform.localToWorldMatrix * mesh.bindposes[0];
            Matrix4x4 skinB = boneB.transform.localToWorldMatrix * mesh.bindposes[1];
            System.Func<int, Matrix4x4> expected = v =>
            {
                float wb = boneWeights[v].weight1, wa = boneWeights[v].weight0;
                var m = new Matrix4x4();
                for (int e = 0; e < 16; e++) m[e] = skinA[e] * wa + skinB[e] * wb;
                return m;
            };

            using (var session = new ClothEditSession(renderer))
            {
                for (int v = 0; v < mesh.vertexCount; v++)
                {
                    for (int e = 0; e < 16; e++) Assert.That(session.GetVertexMatrix(v)[e], Is.EqualTo(expected(v)[e]).Within(1e-5f));
                }
                PullCenter(session);
                AssertDeltasReproduceWorldShape(session, expected, mesh.vertices);
            }
        }

        // Unity 自身のスキニングで、保存したシェイプキーが編集した形どおりに見えるかを確かめる（自前の式との比較だけでは足りないため）
        [Test]
        public void SkinnedMesh_SavedShapeMatchesUnitySkinning()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            var existing = new Vector3[mesh.vertexCount];
            for (int v = 0; v < existing.Length; v++) existing[v] = new Vector3(0f, 0f, 0.02f * (v + 1));
            mesh.AddBlendShapeFrame("Existing", 100f, existing, null, null);

            GameObject root = NewObject("SkinnedTarget");
            root.transform.SetPositionAndRotation(new Vector3(-0.3f, 1f, 0.2f), Quaternion.Euler(10f, -40f, 0f));
            GameObject boneA = NewObject("BoneA");
            GameObject boneB = NewObject("BoneB");
            boneA.transform.SetParent(root.transform, false);
            boneB.transform.SetParent(root.transform, false);

            float[] weightB = { 0f, 0.4f, 0.6f, 1f };
            var boneWeights = new BoneWeight[mesh.vertexCount];
            for (int v = 0; v < boneWeights.Length; v++)
            {
                float wb = weightB[v % weightB.Length];
                boneWeights[v] = new BoneWeight { boneIndex0 = 0, weight0 = 1f - wb, boneIndex1 = 1, weight1 = wb };
            }
            mesh.boneWeights = boneWeights;
            mesh.bindposes = new[]
            {
                boneA.transform.worldToLocalMatrix * root.transform.localToWorldMatrix,
                boneB.transform.worldToLocalMatrix * root.transform.localToWorldMatrix,
            };

            var renderer = root.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { boneA.transform, boneB.transform };
            renderer.SetBlendShapeWeight(0, 40f);
            boneB.transform.localRotation = Quaternion.Euler(20f, 0f, 30f);
            boneB.transform.localPosition = new Vector3(0.1f, -0.05f, 0.03f);

            using (var session = new ClothEditSession(renderer))
            {
                PullCenter(session);
                Mesh saved = Track(session.CreateMeshWithBlendShape("GP02_Test"));
                renderer.sharedMesh = saved;
                renderer.SetBlendShapeWeight(saved.GetBlendShapeIndex("GP02_Test"), 100f);

                // 拡大率 1 なので、焼いた頂点はレンダラーの行列でワールド座標になる
                Mesh baked = Track(new Mesh());
                renderer.BakeMesh(baked, true);
                Vector3[] bakedVertices = baked.vertices;
                Matrix4x4 toWorld = root.transform.localToWorldMatrix;
                for (int v = 0; v < bakedVertices.Length; v++)
                {
                    Vector3 world = toWorld.MultiplyPoint3x4(bakedVertices[v]);
                    Assert.That(Vector3.Distance(world, session.CurrentWorldVertices[v]), Is.LessThan(Tolerance), $"頂点 {v}");
                }
            }
        }

        [Test]
        public void SkinnedMesh_ReflectsCurrentBlendShapeWeight()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            Vector3[] vertices = mesh.vertices;
            var delta = new Vector3[vertices.Length];
            for (int v = 0; v < delta.Length; v++) delta[v] = new Vector3(0.1f * v, 0.2f, -0.05f * v);
            var zeros = new Vector3[vertices.Length];
            mesh.AddBlendShapeFrame("Shape", 100f, delta, zeros, zeros);

            GameObject go = NewObject("BlendShapeTarget");
            go.transform.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.Euler(0f, 90f, 0f));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.SetBlendShapeWeight(0, 50f);

            using (var session = new ClothEditSession(renderer))
            {
                Matrix4x4 m = go.transform.localToWorldMatrix;
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 expected = m.MultiplyPoint3x4(vertices[v] + 0.5f * delta[v]);
                    Assert.That(Vector3.Distance(session.CurrentWorldVertices[v], expected), Is.LessThan(Tolerance), $"頂点 {v}");
                }
            }
        }

        [Test]
        public void SkinnedMesh_InterpolatesBetweenMultipleBlendShapeFrames()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            Vector3[] vertices = mesh.vertices;
            var first = new Vector3[vertices.Length];
            var second = new Vector3[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                first[v] = new Vector3(0.1f, 0f, 0f);
                second[v] = new Vector3(0.1f, 0.4f, 0f);
            }
            var zeros = new Vector3[vertices.Length];
            mesh.AddBlendShapeFrame("Shape", 50f, first, zeros, zeros);
            mesh.AddBlendShapeFrame("Shape", 100f, second, zeros, zeros);

            GameObject go = NewObject("MultiFrameTarget");
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;

            // 最初のフレームの手前は 0 との間、2フレームの間は線形補間
            (float weight, Vector3 expected)[] cases =
            {
                (25f, new Vector3(0.05f, 0f, 0f)),
                (75f, new Vector3(0.1f, 0.2f, 0f)),
                (100f, new Vector3(0.1f, 0.4f, 0f)),
            };
            foreach (var (weight, expected) in cases)
            {
                renderer.SetBlendShapeWeight(0, weight);
                using (var session = new ClothEditSession(renderer))
                {
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        Assert.That(Vector3.Distance(session.CurrentWorldVertices[v], vertices[v] + expected), Is.LessThan(Tolerance), $"重み {weight} 頂点 {v}");
                    }
                }
            }
        }

        [Test]
        public void Undo_RestoresParticlePositions()
        {
            GameObject go = NewObject("UndoTarget");
            go.AddComponent<MeshFilter>().sharedMesh = CopyOfBuiltinQuad();
            var renderer = go.AddComponent<MeshRenderer>();

            using (var session = new ClothEditSession(renderer))
            {
                NVector3[] before = session.Solver.Positions.ToArray();
                Vector3[] shapeBefore = (Vector3[])session.CurrentWorldVertices.Clone();

                Assert.That(session.BeginGrab(Vector3.zero, 0.05f), Is.True);
                session.DragTo(new Vector3(0f, 0f, -0.1f));
                for (int i = 0; i < 10; i++) session.Solver.Solve(20);
                session.Release();
                session.UpdateCurrentShape();
                Assert.That(session.Solver.Positions.ToArray(), Is.Not.EqualTo(before));

                Assert.That(session.Undo(), Is.True);

                NVector3[] after = session.Solver.Positions.ToArray();
                Assert.That(after, Is.EqualTo(before));
                for (int v = 0; v < shapeBefore.Length; v++)
                {
                    Assert.That(Vector3.Distance(session.CurrentWorldVertices[v], shapeBefore[v]), Is.LessThan(Tolerance));
                }
                Assert.That(session.Undo(), Is.False);
            }
        }

        [Test]
        public void PaintPins_PinsParticlesWithinRadius()
        {
            GameObject go = NewObject("PinTarget");
            go.AddComponent<MeshFilter>().sharedMesh = CopyOfBuiltinQuad();
            var renderer = go.AddComponent<MeshRenderer>();

            using (var session = new ClothEditSession(renderer))
            {
                int pinned = session.PaintPins(Vector3.zero, 0.05f, true);

                Assert.That(pinned, Is.GreaterThan(0));
                Assert.That(session.PinnedCount, Is.EqualTo(pinned));
                var dots = new List<Vector3>();
                session.GetPinnedPositions(dots);
                Assert.That(dots.Count, Is.EqualTo(pinned));
                foreach (Vector3 p in dots) Assert.That(p.magnitude, Is.LessThanOrEqualTo(0.05f + Tolerance));

                Assert.That(session.PaintPins(Vector3.zero, 0.05f, false), Is.EqualTo(pinned));
                Assert.That(session.PinnedCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void CreateMeshWithBlendShape_AddsNamedShapeWithDeltas()
        {
            Mesh source = CopyOfBuiltinQuad();
            GameObject go = NewObject("MeshCreateTarget");
            go.AddComponent<MeshFilter>().sharedMesh = source;
            var renderer = go.AddComponent<MeshRenderer>();

            using (var session = new ClothEditSession(renderer))
            {
                PullCenter(session);
                var expected = new Vector3[session.VertexCount];
                session.BuildMeshSpaceDeltas(expected, new Vector3[session.VertexCount]);

                Mesh created = Track(session.CreateMeshWithBlendShape("Pulled"));

                Assert.That(created, Is.Not.SameAs(source));
                Assert.That(created.name, Is.EqualTo(source.name + "_GP02"));
                Assert.That(source.blendShapeCount, Is.EqualTo(0));
                Assert.That(created.GetBlendShapeName(0), Is.EqualTo("Pulled"));
                var written = new Vector3[created.vertexCount];
                created.GetBlendShapeFrameVertices(0, 0, written, null, null);
                for (int v = 0; v < written.Length; v++)
                {
                    Assert.That(Vector3.Distance(written[v], expected[v]), Is.LessThan(1e-5f), $"頂点 {v}");
                }
            }
        }

        [Test]
        public void SuggestShapeName_AvoidsExistingNames()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            var zeros = new Vector3[mesh.vertexCount];
            GameObject go = NewObject("NameTarget");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();

            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.SuggestShapeName(), Is.EqualTo("GP02_1"));
                mesh.AddBlendShapeFrame("GP02_1", 100f, zeros, zeros, zeros);
                mesh.AddBlendShapeFrame("GP02_2", 100f, zeros, zeros, zeros);
                Assert.That(session.SuggestShapeName(), Is.EqualTo("GP02_3"));
            }
        }

        [Test]
        public void ApplyToScene_SavesMeshAndSetsSkinnedWeightToFull()
        {
            const string folder = "Assets/GP02_TestTemp/Meshes";
            try
            {
                Mesh source = CopyOfBuiltinQuad();
                GameObject go = NewObject("ApplyTarget");
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = source;

                Mesh saved;
                using (var session = new ClothEditSession(renderer))
                {
                    PullCenter(session);
                    saved = session.ApplyToScene("Pulled", folder);
                }

                Assert.That(AssetDatabase.GetAssetPath(saved), Does.StartWith(folder));
                Assert.That(renderer.sharedMesh, Is.SameAs(saved));
                Assert.That(saved.GetBlendShapeIndex("Pulled"), Is.EqualTo(0));
                Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100f));

                // 保存したメッシュを元に作り直すと、編集後の形が新しい基準になり、続けて同じメッシュに足せる
                using (var session = new ClothEditSession(renderer))
                {
                    Assert.That(session.SuggestShapeName(), Is.EqualTo("GP02_1"));
                    Mesh again = session.ApplyToScene("Second", folder);
                    Assert.That(again, Is.SameAs(saved));
                    Assert.That(saved.blendShapeCount, Is.EqualTo(2));
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset("Assets/GP02_TestTemp");
            }
        }
    }
}
