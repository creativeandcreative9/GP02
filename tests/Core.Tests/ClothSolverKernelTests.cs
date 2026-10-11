using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

// DefaultKernel は static なので、他のテストと同時に動かさない
[CollectionDefinition("DefaultKernel", DisableParallelization = true)]
public class DefaultKernelCollection { }

[Collection("DefaultKernel")]
public class ClothSolverKernelTests
{
    // 渡されるデータだけを使って、ソルバーの C# と同じ式・同じ順序で計算する。
    // データに漏れがないこと（カーネルがソルバーの内部を見なくても計算できること）の確認を兼ねる
    sealed class ReferenceKernel : IClothSolverKernel
    {
        public int IterateCalls, CoarseCalls, IterationsTotal;

        public string Name => "Reference";
        public bool IsAccelerated => false;

        public void Iterate(in ClothSolverIterationData d, int iterations)
        {
            IterateCalls++;
            IterationsTotal += iterations;
            for (int it = 0; it < iterations; it++)
            {
                Distances(d.Positions, d.InverseMass, d.ActiveBend, d.ActiveBendCount, d.BendPairs, d.BendRestLengths, d.BendLambda, d.BendCompliance);
                Distances(d.Positions, d.InverseMass, d.ActiveStretch, d.ActiveStretchCount, d.StretchPairs, d.StretchRestLengths, d.StretchLambda, d.StretchCompliance);
                for (int a = 0; a < d.ActiveParticleCount; a++)
                {
                    int i = d.ActiveParticles[a];
                    if (d.Grabbed >= 0 && !d.InBrush[i]) Tether(d.Positions, i, d.Grabbed, d.GrabDistance[i]);
                    int pin = d.NearestPin[i];
                    if (pin >= 0) Tether(d.Positions, i, pin, d.PinDistance[i]);
                }
            }
        }

        public void SolveCoarseLevel(in ClothSolverCoarseLevelData d)
        {
            CoarseCalls++;
            Vector3[] p = d.Positions;
            foreach (int m in d.Members) d.LevelStart[m] = p[m];
            for (int it = 0; it < d.Iterations; it++)
            {
                for (int k = 0; k < d.RestLengths.Length; k++)
                {
                    int i = d.Pairs[2 * k], j = d.Pairs[2 * k + 1];
                    float wi = d.InverseMass[i], wj = d.InverseMass[j];
                    float w = wi + wj;
                    if (w == 0f) continue;
                    Vector3 diff = p[i] - p[j];
                    float length = diff.Length();
                    if (length <= d.RestLengths[k]) continue;
                    Vector3 delta = diff * (-(length - d.RestLengths[k]) / (w * length));
                    p[i] += wi * delta;
                    p[j] -= wj * delta;
                }
            }
            for (int a = 0; a < d.ActiveParticleCount; a++)
            {
                int i = d.ActiveParticles[a];
                if (d.InverseMass[i] == 0f) continue;
                Vector3 sum = Vector3.Zero;
                for (int k = d.BindOffsets[i]; k < d.BindOffsets[i + 1]; k++)
                {
                    int parent = d.BindParents[k];
                    sum += (p[parent] - d.LevelStart[parent]) * d.BindWeights[k];
                }
                p[i] += sum;
            }
        }

        static void Distances(Vector3[] p, float[] inverseMass, int[] active, int count, int[] pairs, float[] rest, float[] lambda, float alpha)
        {
            for (int a = 0; a < count; a++)
            {
                int k = active[a];
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;
                Vector3 diff = p[i] - p[j];
                float length = diff.Length();
                if (length < 1e-12f) continue;
                float deltaLambda = (-(length - rest[k]) - alpha * lambda[k]) / (w + alpha);
                lambda[k] += deltaLambda;
                Vector3 delta = diff * (deltaLambda / length);
                p[i] += wi * delta;
                p[j] -= wj * delta;
            }
        }

        static void Tether(Vector3[] p, int i, int origin, float maxLength)
        {
            if (float.IsInfinity(maxLength)) return;
            Vector3 diff = p[i] - p[origin];
            float lengthSquared = diff.LengthSquared();
            if (lengthSquared <= maxLength * maxLength) return;
            p[i] = p[origin] + diff * (maxLength / MathF.Sqrt(lengthSquared));
        }
    }

    // ダミー。呼ばれたことだけを記録し、何も計算しない
    sealed class DummyKernel : IClothSolverKernel
    {
        public int IterateCalls, CoarseCalls;
        public string Name => "Dummy";
        public bool IsAccelerated => false;
        public void Iterate(in ClothSolverIterationData data, int iterations) => IterateCalls++;
        public void SolveCoarseLevel(in ClothSolverCoarseLevelData data) => CoarseCalls++;
    }

    static ParticleCloth BuildCloth(int coarseLevels)
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        return ParticleCloth.Build(vertices, triangles, coarseLevels: coarseLevels);
    }

    // ピン留め → つまんで数コマ引っ張る → 離して仕上げ
    static Vector3[] Operate(ParticleCloth cloth, IClothSolverKernel? kernel)
    {
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.1f, Kernel = kernel };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 0.3f - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.15f, 0.1f, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 5; f++)
        {
            solver.Grab(center, start + new Vector3(0f, 0.01f, 0.08f) * f / 5f, 0.02f);
            solver.Solve(5);
        }
        solver.Settle(0.02f, 40, out _);
        solver.Release();
        solver.Solve(10);
        return solver.Positions.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Kernel_ReceivingAllData_MatchesBuiltInPath(int coarseLevels)
    {
        var cloth = BuildCloth(coarseLevels);
        var reference = new ReferenceKernel();

        Vector3[] expected = Operate(cloth, null);
        Vector3[] actual = Operate(cloth, reference);

        Assert.True(reference.IterateCalls > 0);
        Assert.Equal(coarseLevels > 0, reference.CoarseCalls > 0);
        // 動いていること（比較が空振りでない）
        float moved = 0f;
        for (int i = 0; i < expected.Length; i++) moved = MathF.Max(moved, Vector3.Distance(expected[i], cloth.Model.RestPositions[i]));
        Assert.True(moved > 0.01f);
        for (int i = 0; i < expected.Length; i++) Assert.True(Vector3.Distance(expected[i], actual[i]) < 1e-5f, $"粒子 {i}");
    }

    [Fact]
    public void Kernel_IsCalledOncePerSolveAndOncePerLevel()
    {
        var cloth = BuildCloth(2);
        var dummy = new DummyKernel();
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = dummy };

        solver.Solve(7);
        Assert.Equal(1, dummy.IterateCalls);
        Assert.Equal(cloth.Hierarchy.LevelCount, dummy.CoarseCalls);

        // 反復 0 回なら、反復も粗い段も呼ばない（従来と同じ）
        solver.Solve(0);
        Assert.Equal(1, dummy.IterateCalls);
        Assert.Equal(cloth.Hierarchy.LevelCount, dummy.CoarseCalls);
    }

    [Fact]
    public void Kernel_Null_UsesBuiltInPath()
    {
        var cloth = BuildCloth(2);
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy);
        Assert.Null(solver.Kernel);

        Vector3[] first = Operate(cloth, null);
        Vector3[] second = Operate(cloth, null);
        for (int i = 0; i < first.Length; i++) Assert.Equal(first[i], second[i]);
    }

    [Fact]
    public void DefaultKernel_IsUsedByNewSolversOnly()
    {
        var cloth = BuildCloth(0);
        var dummy = new DummyKernel();
        Assert.Null(ClothSolver.DefaultKernel);
        var before = new ClothSolver(cloth.Model);
        try
        {
            ClothSolver.DefaultKernel = dummy;
            var after = cloth.CreateSolver();
            Assert.Same(dummy, after.Kernel);
            Assert.Null(before.Kernel);

            after.Solve(3);
            Assert.Equal(1, dummy.IterateCalls);

            // 個別に差し替えられる
            after.Kernel = null;
            after.Solve(3);
            Assert.Equal(1, dummy.IterateCalls);
        }
        finally
        {
            ClothSolver.DefaultKernel = null;
        }
        Assert.Null(new ClothSolver(cloth.Model).Kernel);
    }
}
