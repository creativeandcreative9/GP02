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
    internal sealed class ReferenceKernel : IClothSolverKernel
    {
        public int IterateCalls, CoarseCalls, IterationsTotal;

        public string Name => "Reference";
        public bool IsAccelerated => false;

        public void Iterate(in ClothSolverIterationData d, int iterations)
        {
            IterateCalls++;
            IterationsTotal += iterations;
            // 前処理：λ を 0 に戻し、元の形の近い粒子を探し直す
            for (int a = 0; a < d.ActiveStretchCount; a++) d.StretchLambda[d.ActiveStretch[a]] = 0f;
            for (int a = 0; a < d.ActiveBendCount; a++) d.BendLambda[d.ActiveBend[a]] = 0f;
            if (d.KeepOutsideRestSurface) UpdateRestAnchors(d);
            if (d.ContactCount > 0) UpdateContactPartners(d);
            for (int it = 0; it < iterations; it++)
            {
                if (d.BrushCompliance > 0f)
                {
                    for (int a = 0; a < d.ActiveParticleCount; a++)
                    {
                        int i = d.ActiveParticles[a];
                        if (!d.InBrush[i]) continue;
                        Vector3 delta = (-(d.Positions[i] - d.BrushGoal[i]) - d.BrushCompliance * d.BrushLambda[i]) / (1f + d.BrushCompliance);
                        d.BrushLambda[i] += delta;
                        d.Positions[i] += delta;
                    }
                }
                Distances(d.Positions, d.InverseMass, d.ActiveBend, d.ActiveBendCount, d.BendPairs, d.BendRestLengths, d.BendLambda, d.BendCompliance);
                Distances(d.Positions, d.InverseMass, d.ActiveStretch, d.ActiveStretchCount, d.StretchPairs, d.StretchRestLengths, d.StretchLambda, d.StretchCompliance);
                for (int a = 0; a < d.ActiveParticleCount; a++)
                {
                    int i = d.ActiveParticles[a];
                    if (d.Grabbed >= 0 && !d.InBrush[i]) Tether(d.Positions, i, d.GrabSource[i], d.GrabDistance[i]);
                    int pin = d.NearestPin[i];
                    if (pin >= 0) Tether(d.Positions, i, pin, d.PinDistance[i]);
                }
                if (d.KeepOutsideRestSurface)
                {
                    for (int a = 0; a < d.ActiveParticleCount; a++)
                    {
                        int i = d.ActiveParticles[a];
                        if (d.Grabbed >= 0 && d.InBrush[i]) continue;
                        int r = d.RestAnchor[i];
                        Vector3 n = d.RestNormals[r];
                        float depth = Vector3.Dot(d.Positions[i] - d.RestPositions[r], n) + (d.RestAllowance != null ? d.RestAllowance[r] : 0f);
                        if (depth < 0f) d.Positions[i] -= depth * n;
                    }
                }
                for (int k = 0; k < d.ContactCount; k++)
                {
                    int i = d.ContactOuter[k], j = d.ContactInner[k];
                    Vector3 n = d.RestNormals[j];
                    Vector3 diff = d.Positions[i] - d.Positions[j];
                    float along = Vector3.Dot(diff, n);
                    float c = along - d.ContactGap[k];
                    if (c >= 0f) continue;
                    if (diff.LengthSquared() - along * along > d.ContactReach * d.ContactReach) continue;
                    float wi = d.InverseMass[i], wj = d.InverseMass[j];
                    if (d.Grabbed >= 0)
                    {
                        if (d.InBrush[i] && wj > 0f) wi = 0f;
                        else if (d.InBrush[j] && wi > 0f) wj = 0f;
                    }
                    if (wi + wj == 0f) continue;
                    float s = -c / (wi + wj);
                    d.Positions[i] += wi * s * n;
                    d.Positions[j] -= wj * s * n;
                }
                for (int k = 0; k < d.BodyPlaneCount; k++)
                {
                    int i = d.BodyPlaneParticles[k];
                    if (d.InverseMass[i] == 0f) continue;
                    Vector3 n = d.BodyPlaneNormals[k];
                    float depth = Vector3.Dot(d.Positions[i] - d.BodyPlanePoints[k], n) - d.BodyPlaneMinDistances[k];
                    if (depth < 0f) d.Positions[i] -= depth * n;
                }
            }
            // 後処理：ワールド座標へ書き戻す
            for (int a = 0; a < d.ActiveParticleCount; a++)
            {
                int i = d.ActiveParticles[a];
                d.WorldPositions[i] = d.Positions[i] + d.Origin;
            }
        }

        static void UpdateRestAnchors(in ClothSolverIterationData d)
        {
            for (int a = 0; a < d.ActiveParticleCount; a++)
            {
                int i = d.ActiveParticles[a];
                int anchor = d.RestAnchor[i];
                float best = Vector3.DistanceSquared(d.Positions[i], d.RestPositions[anchor]);
                for (int step = 0; step < 16; step++)
                {
                    int next = -1;
                    for (int k = d.GraphOffsets[anchor]; k < d.GraphOffsets[anchor + 1]; k++)
                    {
                        int candidate = d.GraphNeighbors[k];
                        float distance = Vector3.DistanceSquared(d.Positions[i], d.RestPositions[candidate]);
                        if (distance < best)
                        {
                            best = distance;
                            next = candidate;
                        }
                    }
                    if (next < 0) break;
                    anchor = next;
                }
                d.RestAnchor[i] = anchor;
            }
        }

        static void UpdateContactPartners(in ClothSolverIterationData d)
        {
            for (int k = 0; k < d.ContactCount; k++)
            {
                Vector3 p = d.Positions[d.ContactOuter[k]];
                int partner = d.ContactInner[k];
                float best = Vector3.DistanceSquared(p, d.Positions[partner]);
                for (int step = 0; step < 16; step++)
                {
                    int next = -1;
                    for (int e = d.GraphOffsets[partner]; e < d.GraphOffsets[partner + 1]; e++)
                    {
                        int candidate = d.GraphNeighbors[e];
                        float distance = Vector3.DistanceSquared(p, d.Positions[candidate]);
                        if (distance < best)
                        {
                            best = distance;
                            next = candidate;
                        }
                    }
                    if (next < 0) break;
                    partner = next;
                }
                d.ContactInner[k] = partner;
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
            if (origin < 0 || float.IsInfinity(maxLength)) return;
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

    // 一般化したブラシ（回転・拡大縮小、なめらかな重み、直線距離、対称）でつまんで数コマ動かす → 仕上げ → 離す
    static Vector3[] OperateGeneralBrush(ParticleCloth cloth, IClothSolverKernel? kernel, float brushCompliance)
    {
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.1f, Kernel = kernel, BrushCompliance = brushCompliance };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 0.3f - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.08f, 0.1f, 0f));
        Vector3 pivot = solver.Positions[center];
        var options = new BrushOptions
        {
            Radius = 0.03f,
            Falloff = FalloffCurve.Smooth,
            DistanceMode = BrushDistanceMode.Linear,
            Mirror = new MirrorPlane(new Vector3(0.15f, 0f, 0f), Vector3.UnitX),
        };
        for (int f = 1; f <= 5; f++)
        {
            var motion = new BrushMotion(new Vector3(0.01f, 0.01f, 0.06f) * f / 5f,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f * f / 5f), 1f + 0.1f * f / 5f, pivot);
            solver.Grab(center, motion, options);
            solver.Solve(5);
        }
        Assert.True(solver.MirrorParticle >= 0);
        solver.Settle(0.02f, 40, out _);
        solver.Release();
        solver.Solve(10);
        return solver.Positions.ToArray();
    }

    [Theory]
    [InlineData(0, ClothSolver.DefaultBrushCompliance)]
    [InlineData(2, ClothSolver.DefaultBrushCompliance)]
    [InlineData(2, 0f)]
    public void Kernel_ReceivingAllData_MatchesBuiltInPath_WithGeneralBrush(int coarseLevels, float brushCompliance)
    {
        var cloth = BuildCloth(coarseLevels);
        var reference = new ReferenceKernel();

        Vector3[] expected = OperateGeneralBrush(cloth, null, brushCompliance);
        Vector3[] actual = OperateGeneralBrush(cloth, reference, brushCompliance);

        Assert.True(reference.IterateCalls > 0);
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
