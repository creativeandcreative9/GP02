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

        ClothEditSession NewQuadSession(string name, out MeshRenderer renderer)
        {
            GameObject go = NewObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = CopyOfBuiltinQuad();
            renderer = go.AddComponent<MeshRenderer>();
            return new ClothEditSession(renderer);
        }

        // 中心あたりを少しつまんで動かし、離す
        static void DragSomewhere(ClothEditSession session)
        {
            Assert.That(session.BeginGrab(Vector3.zero, 0.05f), Is.True);
            session.DragTo(new Vector3(0f, 0f, -0.1f));
            for (int i = 0; i < 10; i++) session.Solver.Solve(20);
            session.Release();
            session.UpdateCurrentShape();
        }

        ClothEditState NewState()
        {
            return Track(ClothEditState.Create());
        }

        [Test]
        public void ReadState_RestoresPositionsPinsAndPinnedCount()
        {
            using (var session = NewQuadSession("StateTarget", out _))
            {
                ClothEditState state = NewState();
                Assert.That(state.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(session.PaintPins(new Vector3(0.3f, 0f, 0f), 0.03f, true), Is.GreaterThan(0));
                int pinnedBefore = session.PinnedCount;
                session.WriteState(state);
                NVector3[] positionsBefore = session.Solver.Positions.ToArray();
                Vector3[] shapeBefore = (Vector3[])session.CurrentWorldVertices.Clone();
                Assert.That(session.MatchesState(state), Is.True);

                DragSomewhere(session);
                session.PaintPins(Vector3.zero, 0.05f, true);
                session.PaintPins(new Vector3(0.3f, 0f, 0f), 0.03f, false);
                Assert.That(session.Solver.Positions.ToArray(), Is.Not.EqualTo(positionsBefore));
                Assert.That(session.PinnedCount, Is.Not.EqualTo(pinnedBefore));
                Assert.That(session.MatchesState(state), Is.False);

                Assert.That(session.ReadState(state), Is.True);

                Assert.That(session.Solver.Positions.ToArray(), Is.EqualTo(positionsBefore));
                Assert.That(session.PinnedCount, Is.EqualTo(pinnedBefore));
                int counted = 0;
                for (int i = 0; i < session.ParticleCount; i++)
                {
                    if (session.Solver.IsPinned(i)) counted++;
                }
                Assert.That(counted, Is.EqualTo(pinnedBefore));
                Assert.That(session.MatchesState(state), Is.True);
                for (int v = 0; v < shapeBefore.Length; v++)
                {
                    Assert.That(Vector3.Distance(session.CurrentWorldVertices[v], shapeBefore[v]), Is.LessThan(Tolerance));
                }
            }
        }

        [Test]
        public void ReadState_ReturnsFalseAndChangesNothing_WhenParticleCountDiffers()
        {
            // 2倍の大きさのメッシュは、粒子の数が違う
            GameObject bigObject = NewObject("BigTarget");
            bigObject.transform.localScale = Vector3.one * 2f;
            bigObject.AddComponent<MeshFilter>().sharedMesh = CopyOfBuiltinQuad();
            var bigRenderer = bigObject.AddComponent<MeshRenderer>();

            ClothEditState state = NewState();
            using (var big = new ClothEditSession(bigRenderer))
            {
                big.WriteState(state);
            }

            using (var session = NewQuadSession("SmallTarget", out _))
            {
                Assert.That(session.ParticleCount * 3, Is.Not.EqualTo(state.Positions.Length));
                session.PaintPins(Vector3.zero, 0.05f, true);
                DragSomewhere(session);
                NVector3[] positions = session.Solver.Positions.ToArray();
                int pinned = session.PinnedCount;

                Assert.That(session.ReadState(state), Is.False);
                Assert.That(session.ReadState(NewState()), Is.False);   // 何も書いていない state

                Assert.That(session.Solver.Positions.ToArray(), Is.EqualTo(positions));
                Assert.That(session.PinnedCount, Is.EqualTo(pinned));
            }
        }

        [Test]
        public void UnityUndo_RevertsAndRedoesState()
        {
            ClothEditState state = NewState();
            using (var session = NewQuadSession("UndoTarget", out _))
            {
                try
                {
                    session.WriteState(state);
                    NVector3[] before = session.Solver.Positions.ToArray();
                    Vector3[] shapeBefore = (Vector3[])session.CurrentWorldVertices.Clone();

                    // ウィンドウと同じ手順：記録 → 変更 → 書き込み → 区切り
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(state, "GP02 つまむ");
                    DragSomewhere(session);
                    Assert.That(session.MatchesState(state), Is.False);
                    session.WriteState(state);
                    Undo.IncrementCurrentGroup();
                    NVector3[] after = session.Solver.Positions.ToArray();
                    Assert.That(after, Is.Not.EqualTo(before));

                    Undo.PerformUndo();
                    Assert.That(session.ReadState(state), Is.True);
                    Assert.That(session.Solver.Positions.ToArray(), Is.EqualTo(before));
                    for (int v = 0; v < shapeBefore.Length; v++)
                    {
                        Assert.That(Vector3.Distance(session.CurrentWorldVertices[v], shapeBefore[v]), Is.LessThan(Tolerance));
                    }

                    Undo.PerformRedo();
                    Assert.That(session.ReadState(state), Is.True);
                    Assert.That(session.Solver.Positions.ToArray(), Is.EqualTo(after));
                }
                finally
                {
                    Undo.ClearUndo(state);
                }
            }
        }

        [Test]
        public void UnityUndo_PinPaintingIsOneUndoStep()
        {
            ClothEditState state = NewState();
            using (var session = NewQuadSession("PinUndoTarget", out _))
            {
                try
                {
                    session.WriteState(state);
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(state, "GP02 ピン留め");
                    // ドラッグ中の塗りは、記録1回にまとまる
                    session.PaintPins(new Vector3(-0.2f, 0f, 0f), 0.03f, true);
                    session.PaintPins(new Vector3(0f, 0f, 0f), 0.03f, true);
                    session.PaintPins(new Vector3(0.2f, 0f, 0f), 0.03f, true);
                    int pinned = session.PinnedCount;
                    session.WriteState(state);
                    Undo.IncrementCurrentGroup();

                    Undo.PerformUndo();
                    Assert.That(session.ReadState(state), Is.True);
                    Assert.That(session.PinnedCount, Is.EqualTo(0));

                    Undo.PerformRedo();
                    Assert.That(session.ReadState(state), Is.True);
                    Assert.That(session.PinnedCount, Is.EqualTo(pinned));
                }
                finally
                {
                    Undo.ClearUndo(state);
                }
            }
        }

        [Test]
        public void UnityUndo_OperationWithoutChangeLeavesNoRecord()
        {
            ClothEditState state = NewState();
            using (var session = NewQuadSession("NoChangeTarget", out _))
            {
                try
                {
                    session.WriteState(state);

                    // 1つ目の操作：ピン留め
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(state, "GP02 ピン留め");
                    session.PaintPins(Vector3.zero, 0.03f, true);
                    session.WriteState(state);
                    Undo.IncrementCurrentGroup();
                    Assert.That(session.PinnedCount, Is.GreaterThan(0));

                    // 2つ目の操作：記録を取ったが、何も変わらなかった（動かさずにクリックしただけ）
                    Undo.IncrementCurrentGroup();
                    int group = Undo.GetCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(state, "GP02 つまむ");
                    Assert.That(session.MatchesState(state), Is.True);
                    Undo.RevertAllDownToGroup(group);

                    // 空の記録が残っていれば、ここで空の記録が戻るだけで、1つ目の操作は戻らない
                    Undo.PerformUndo();
                    Assert.That(session.ReadState(state), Is.True);
                    Assert.That(session.PinnedCount, Is.EqualTo(0));
                }
                finally
                {
                    Undo.ClearUndo(state);
                }
            }
        }

        [Test]
        public void ResetAll_KeepsPinsAndCanBeRestoredFromState()
        {
            ClothEditState state = NewState();
            using (var session = NewQuadSession("ResetTarget", out _))
            {
                session.PaintPins(new Vector3(0.4f, 0f, 0f), 0.03f, true);
                DragSomewhere(session);
                session.WriteState(state);
                NVector3[] dragged = session.Solver.Positions.ToArray();
                int pinned = session.PinnedCount;

                session.ResetAll();
                Assert.That(session.Solver.Positions.ToArray(), Is.Not.EqualTo(dragged));
                Assert.That(session.PinnedCount, Is.EqualTo(pinned));

                Assert.That(session.ReadState(state), Is.True);
                Assert.That(session.Solver.Positions.ToArray(), Is.EqualTo(dragged));
            }
        }

        [Test]
        public void PinNear_RestoresPinsInNewSessionOfSameMesh()
        {
            GameObject go = NewObject("PinCarryTarget");
            go.AddComponent<MeshFilter>().sharedMesh = CopyOfBuiltinQuad();
            var renderer = go.AddComponent<MeshRenderer>();

            var pinPositions = new List<Vector3>();
            int pinned;
            using (var first = new ClothEditSession(renderer))
            {
                first.PaintPins(new Vector3(0.2f, 0.1f, 0f), 0.06f, true);
                first.PaintPins(new Vector3(-0.3f, -0.2f, 0f), 0.04f, true);
                pinned = first.PinnedCount;
                Assert.That(pinned, Is.GreaterThan(0));
                first.GetPinnedPositions(pinPositions);
                Assert.That(pinPositions.Count, Is.EqualTo(pinned));
            }

            using (var second = new ClothEditSession(renderer))
            {
                int added = second.PinNear(pinPositions, second.Cloth.Spacing * 0.75f);
                Assert.That(added, Is.EqualTo(pinned));
                Assert.That(second.PinnedCount, Is.EqualTo(pinned));
                // 位置も同じ
                var carried = new List<Vector3>();
                second.GetPinnedPositions(carried);
                Assert.That(carried.Count, Is.EqualTo(pinPositions.Count));
                for (int i = 0; i < carried.Count; i++) Assert.That(Vector3.Distance(carried[i], pinPositions[i]), Is.LessThan(Tolerance));

                // 空の入力は何もしない
                Assert.That(second.PinNear(new List<Vector3>(), 0.01f), Is.EqualTo(0));
                Assert.That(second.PinNear(pinPositions, 0.0075f), Is.EqualTo(0));   // すでにピン留めされたものは数えない
            }
        }

        [Test]
        public void IsTargetUnchanged_DetectsMeshSwapAndBlendShapeWeightChange()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            var zeros = new Vector3[mesh.vertexCount];
            mesh.AddBlendShapeFrame("Shape", 100f, zeros, zeros, zeros);
            GameObject go = NewObject("UnchangedTarget");
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.SetBlendShapeWeight(0, 30f);

            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.IsTargetUnchanged(), Is.True);

                renderer.SetBlendShapeWeight(0, 100f);
                Assert.That(session.IsTargetUnchanged(), Is.False);
                renderer.SetBlendShapeWeight(0, 30f);
                Assert.That(session.IsTargetUnchanged(), Is.True);

                renderer.sharedMesh = CopyOfBuiltinQuad();
                Assert.That(session.IsTargetUnchanged(), Is.False);
                renderer.sharedMesh = mesh;
                Assert.That(session.IsTargetUnchanged(), Is.True);
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
