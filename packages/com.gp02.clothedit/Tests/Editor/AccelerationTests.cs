using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 計算の高速版（Burst など）の有無にかかわらず動くテスト。
    /// Burst が入っているかどうかで、ソルバーの既定のカーネルが変わる。
    /// </summary>
    public class AccelerationTests
    {
        [Test]
        public void DescribeAcceleration_ExplainsEachState()
        {
            Assert.That(ClothEditSession.DescribeAcceleration(null), Does.Contain("なし"));
            Assert.That(ClothEditSession.DescribeAcceleration(new FakeKernel("Fast", true)), Is.EqualTo("Fast（有効）"));
            Assert.That(ClothEditSession.DescribeAcceleration(new FakeKernel("Fast", false)), Does.StartWith("Fast（無効"));
        }

        [Test]
        public void NewSolver_UsesDefaultKernel_AndLogsWhichOne()
        {
            ProceduralMeshes.Grid(4, 4, 0.1f, 0.1f, out var vertices, out var triangles);
            var cloth = ParticleCloth.Build(vertices, triangles);
            ClothSolver solver = cloth.CreateSolver();

            Assert.That(solver.Kernel, Is.SameAs(ClothSolver.DefaultKernel));
            Debug.Log($"[GP02] 高速化: {ClothEditSession.DescribeAcceleration(solver.Kernel)}");

            // どちらの場合でも解ける
            solver.SetPinned(0, true);
            solver.Grab(solver.FindNearestParticle(new System.Numerics.Vector3(0.05f, 0.05f, 0f)), new System.Numerics.Vector3(0.05f, 0.05f, 0.03f), 0.01f);
            Assert.DoesNotThrow(() => solver.Solve(5));
        }

        sealed class FakeKernel : IClothSolverKernel
        {
            public FakeKernel(string name, bool accelerated)
            {
                Name = name;
                IsAccelerated = accelerated;
            }

            public string Name { get; }
            public bool IsAccelerated { get; }
            public void Iterate(in ClothSolverIterationData data, int iterations) { }
            public void SolveCoarseLevel(in ClothSolverCoarseLevelData data) { }
        }
    }
}
