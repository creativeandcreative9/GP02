using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.Tests
{
    /// <summary>
    /// Unity 上で、メッシュの読み込み → 粒子化 → 引っ張る → シェイプキーとして書き込む、が通ることを確かめる。
    /// 計算の中身はクラウドの xUnit テストで確かめているので、ここでは Unity とのつなぎを見る。
    /// </summary>
    public class UnityIntegrationTests
    {
        static Mesh CopyOfBuiltinQuad()
        {
            // Unity 組み込みの Quad（1m四方、頂点4つ）
            var quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            return Object.Instantiate(quad);
        }

        [Test]
        public void ParticleCloth_BuildsFromUnityMesh()
        {
            Mesh mesh = CopyOfBuiltinQuad();

            var cloth = ParticleCloth.Build(MeshBridge.ToNumerics(mesh.vertices, Vector3.one), mesh.triangles);

            // 1㎡ に 1cm 間隔なので、おおよそ 6,000 個前後
            Assert.That(cloth.Model.ParticleCount, Is.InRange(5000, 7500));
            Assert.That(cloth.Topology.IslandCount, Is.EqualTo(1));
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void PulledShape_IsWrittenAsBlendShape()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            var cloth = ParticleCloth.Build(MeshBridge.ToNumerics(mesh.vertices, Vector3.one), mesh.triangles);
            ClothSolver solver = cloth.CreateSolver();
            int center = solver.FindNearestParticle(NVector3.Zero);
            NVector3 start = solver.Positions[center];
            for (int f = 1; f <= 10; f++)
            {
                solver.Grab(center, start + new NVector3(0f, 0f, 0.05f) * f / 10, 0.05f);
                solver.Solve(20);
            }

            var deltaVertices = new NVector3[mesh.vertexCount];
            var deltaNormals = new NVector3[mesh.vertexCount];
            BlendShapeBuilder.Build(cloth, solver.Positions, deltaVertices, deltaNormals);
            MeshBridge.AddBlendShape(mesh, "GP02_Test", MeshBridge.ToUnity(deltaVertices, Vector3.one), MeshBridge.ToUnity(deltaNormals, Vector3.one));

            Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(mesh.GetBlendShapeName(0), Is.EqualTo("GP02_Test"));
            var written = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(0, 0, written, null, null);
            for (int v = 0; v < written.Length; v++)
            {
                Assert.That(written[v].z, Is.EqualTo(deltaVertices[v].Z).Within(1e-5f), $"頂点 {v}");
            }
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void AddBlendShape_RejectsDuplicateName()
        {
            Mesh mesh = CopyOfBuiltinQuad();
            var zeros = new Vector3[mesh.vertexCount];
            MeshBridge.AddBlendShape(mesh, "Same", zeros, zeros);

            Assert.Throws<System.ArgumentException>(() => MeshBridge.AddBlendShape(mesh, "Same", zeros, zeros));
            Object.DestroyImmediate(mesh);
        }
    }
}
