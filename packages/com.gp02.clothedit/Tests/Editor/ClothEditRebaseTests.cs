using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 保存したあとに、粒子を置き直さず <see cref="GP02.Core.ParticleCloth.Rebase"/> した布でセッションを作り直せるかを確かめる。
    /// </summary>
    public class ClothEditRebaseTests
    {
        const string TempRoot = "Assets/GP02_TestTemp";
        const string Folder = TempRoot + "/Meshes";
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
            AssetDatabase.DeleteAsset(TempRoot);
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        Renderer CreateTarget(bool skinned)
        {
            Mesh mesh = Track(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Quad.fbx")));
            GameObject go = Track(new GameObject(skinned ? "RebaseSkinned" : "RebaseMeshFilter"));
            go.transform.SetPositionAndRotation(new Vector3(0.3f, 1f, -0.2f), Quaternion.Euler(20f, 35f, 0f));
            if (skinned)
            {
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                return renderer;
            }
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        // 中央を法線方向へ引っ張る（ClothEditSessionTests.PullCenter と同じ）
        static void PullCenter(ClothEditSession session, float distance = 0.05f)
        {
            session.Solver.InfluenceRadius = 2f;
            Vector3 center = Vector3.zero;
            foreach (Vector3 v in session.CurrentWorldVertices) center += v;
            center /= session.CurrentWorldVertices.Length;

            Vector3 normal = Vector3.Cross(session.CurrentWorldVertices[1] - session.CurrentWorldVertices[0],
                session.CurrentWorldVertices[2] - session.CurrentWorldVertices[0]).normalized;
            Assert.That(session.Raycast(new Ray(center + normal, -normal), out Vector3 hit, out Vector3 hitNormal), Is.True);

            Assert.That(session.BeginGrab(hit, 0.1f), Is.True);
            session.DragTo(hit + hitNormal * distance);
            for (int i = 0; i < 6; i++) session.Solver.Solve(10);
            session.Release();
            session.UpdateCurrentShape();
        }

        // 引っ張る → 保存 → Rebase した布でセッションを作り直す。古いセッションは破棄する
        static ClothEditSession SaveAndRebuild(ClothEditSession session, string shapeName, out Vector3[] before)
        {
            before = (Vector3[])session.CurrentWorldVertices.Clone();
            session.ApplyToScene(shapeName, Folder);
            var rebased = session.Cloth.Rebase(session.Solver.Positions);
            var pins = new List<int>();
            session.GetPinnedParticles(pins);
            Renderer renderer = session.Renderer;
            session.Dispose();

            var next = new ClothEditSession(renderer, rebased);
            next.PinParticles(pins);
            return next;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Rebuild_KeepsCurrentShapeAndStartsWithoutStretch(bool skinned)
        {
            Renderer renderer = CreateTarget(skinned);
            var session = new ClothEditSession(renderer);
            ClothEditSession next = null;
            try
            {
                // 隅をピン留めしてから引っ張る
                Assert.That(session.PaintPins(session.CurrentWorldVertices[0], 0.05f, true), Is.GreaterThan(0));
                PullCenter(session);
                int pinnedBefore = session.PinnedCount;

                next = SaveAndRebuild(session, "Pulled", out Vector3[] before);
                session = null;

                Assert.That(next.CurrentWorldVertices.Length, Is.EqualTo(before.Length));
                for (int v = 0; v < before.Length; v++)
                {
                    Assert.That(Vector3.Distance(next.CurrentWorldVertices[v], before[v]), Is.LessThan(Tolerance), $"頂点 {v}");
                }
                Assert.That(next.Solver.MeasureMaxStretch(), Is.InRange(-1e-4f, 1e-4f));
                Assert.That(next.PinnedCount, Is.EqualTo(pinnedBefore));

                // 何も動かしていない状態でも、形が保存前のまま
                next.UpdateCurrentShape();
                for (int v = 0; v < before.Length; v++)
                {
                    Assert.That(Vector3.Distance(next.CurrentWorldVertices[v], before[v]), Is.LessThan(Tolerance), $"更新後の頂点 {v}");
                }
            }
            finally
            {
                session?.Dispose();
                next?.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Rebuild_CarriesPinIndices(bool skinned)
        {
            Renderer renderer = CreateTarget(skinned);
            var session = new ClothEditSession(renderer);
            ClothEditSession next = null;
            try
            {
                session.PaintPins(session.CurrentWorldVertices[0], 0.05f, true);
                session.PaintPins(session.CurrentWorldVertices[3], 0.08f, true);
                var pinsBefore = new List<int>();
                session.GetPinnedParticles(pinsBefore);
                Assert.That(pinsBefore.Count, Is.GreaterThan(0));
                int particleCount = session.ParticleCount;
                PullCenter(session);

                next = SaveAndRebuild(session, "Pulled", out _);
                session = null;

                var pinsAfter = new List<int>();
                next.GetPinnedParticles(pinsAfter);
                Assert.That(next.ParticleCount, Is.EqualTo(particleCount));
                Assert.That(next.PinnedCount, Is.EqualTo(pinsBefore.Count));
                Assert.That(pinsAfter, Is.EqualTo(pinsBefore));
                // 範囲外の番号は無視する
                Assert.That(next.PinParticles(new[] { -1, particleCount }), Is.EqualTo(0));
            }
            finally
            {
                session?.Dispose();
                next?.Dispose();
            }
        }

        [Test]
        public void Rebuild_RejectsClothThatDoesNotFitMesh()
        {
            Renderer renderer = CreateTarget(false);
            using (var session = new ClothEditSession(renderer))
            {
                Mesh other = Track(new Mesh());
                other.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
                other.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                GameObject go = Track(new GameObject("OtherTarget"));
                go.AddComponent<MeshFilter>().sharedMesh = other;
                var otherRenderer = go.AddComponent<MeshRenderer>();

                // 頂点数は同じ（4）でも三角形の数が違えば合わない
                Assert.That(ClothEditSession.IsClothCompatible(renderer, session.Cloth), Is.True);
                other.triangles = new[] { 0, 1, 2 };
                Assert.That(ClothEditSession.IsClothCompatible(otherRenderer, session.Cloth), Is.False);
                Assert.Throws<System.ArgumentException>(() => new ClothEditSession(otherRenderer, session.Cloth));
                Assert.That(otherRenderer.forceRenderingOff, Is.False);
            }
        }

        [Test]
        public void Rebuild_SecondShapeAddsOnTopOfFirst()
        {
            Renderer renderer = CreateTarget(true);
            var skinned = (SkinnedMeshRenderer)renderer;
            var session = new ClothEditSession(renderer);
            ClothEditSession next = null;
            try
            {
                PullCenter(session, 0.05f);
                next = SaveAndRebuild(session, "First", out _);
                session = null;

                // 2回目は別の向きの形にする
                PullCenter(next, -0.03f);
                Vector3[] expected = (Vector3[])next.CurrentWorldVertices.Clone();
                Mesh saved = next.ApplyToScene("Second", Folder);

                Assert.That(saved.blendShapeCount, Is.EqualTo(2));
                Assert.That(skinned.sharedMesh, Is.SameAs(saved));
                Assert.That(skinned.GetBlendShapeWeight(saved.GetBlendShapeIndex("First")), Is.EqualTo(100f));
                Assert.That(skinned.GetBlendShapeWeight(saved.GetBlendShapeIndex("Second")), Is.EqualTo(100f));

                // Unity のスキニングで、2つとも 100 にした形が編集結果と一致する
                Mesh baked = Track(new Mesh());
                skinned.BakeMesh(baked, true);
                Vector3[] bakedVertices = baked.vertices;
                Matrix4x4 toWorld = skinned.transform.localToWorldMatrix;
                for (int v = 0; v < bakedVertices.Length; v++)
                {
                    Vector3 world = toWorld.MultiplyPoint3x4(bakedVertices[v]);
                    Assert.That(Vector3.Distance(world, expected[v]), Is.LessThan(Tolerance), $"頂点 {v}");
                }
            }
            finally
            {
                session?.Dispose();
                next?.Dispose();
            }
        }
    }
}
