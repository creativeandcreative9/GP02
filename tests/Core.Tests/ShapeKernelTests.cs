using System.Diagnostics;
using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

// ClothSolver.DefaultKernel を使う他のテストと同時に動かさない（時間を測るテストを含むため）
[Collection("DefaultKernel")]
public class ShapeKernelTests
{
    // 渡されるデータだけを使い、ShapeKernels（C#）とは別の書き方（Vector3 の演算子）で計算する
    sealed class ReferenceShapeKernel : IShapeKernel
    {
        public int DeformCalls, ExpandCalls, RefitCalls;

        public void DeformWelded(in WeldedDeformData d)
        {
            DeformCalls++;
            for (int v = 0; v < d.Welded.Length; v++)
            {
                Vector3 sum = Vector3.Zero;
                for (int k = d.BindOffsets[v]; k < d.BindOffsets[v + 1]; k++)
                {
                    int p = d.BindParticles[k];
                    sum += (d.ParticlePositions[p] - d.ParticleRestPositions[p]) * d.BindWeights[k];
                }
                d.Welded[v] = d.BasePositions[v] + sum;
            }
        }

        public void ExpandVertices(in VertexExpandData d)
        {
            ExpandCalls++;
            for (int v = 0; v < d.Vertices.Length; v++)
            {
                int w = d.VertexToWelded[v];
                d.Vertices[v] = d.BaseVertices[v] + (d.Welded[w] - d.WeldedRest[w]);
            }
        }

        public void RefitBvh(in BvhRefitData d)
        {
            RefitCalls++;
            for (int node = d.NodeCount - 1; node >= 0; node--)
            {
                Vector3 min, max;
                if (d.Right[node] < 0)
                {
                    min = new Vector3(float.MaxValue);
                    max = new Vector3(float.MinValue);
                    for (int i = 3 * d.Start[node]; i < 3 * (d.Start[node] + d.Count[node]); i++)
                    {
                        Vector3 p = d.Positions[d.LeafTriangleVertices[i]];
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                    min -= new Vector3(d.Padding);
                    max += new Vector3(d.Padding);
                }
                else
                {
                    min = Vector3.Min(d.BoxMin[node + 1], d.BoxMin[d.Right[node]]);
                    max = Vector3.Max(d.BoxMax[node + 1], d.BoxMax[d.Right[node]]);
                }
                d.BoxMin[node] = min;
                d.BoxMax[node] = max;
            }
        }
    }

    static ParticleCloth BuildSeamCloth()
    {
        ProceduralMeshes.GridWithSeam(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        return ParticleCloth.Build(vertices, triangles);
    }

    // ピン留め → つまんで引っ張った状態の粒子の位置
    static Vector3[] PulledParticles(ParticleCloth cloth)
    {
        var solver = cloth.CreateSolver();
        solver.InfluenceRadius = 0.1f;
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 0.3f - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.15f, 0.1f, 0f));
        solver.Grab(center, solver.Positions[center] + new Vector3(0f, 0.01f, 0.08f), 0.02f);
        solver.Solve(20);
        return solver.Positions.ToArray();
    }

    [Fact]
    public void DeformWelded_WithKernel_MatchesCSharp()
    {
        var cloth = BuildSeamCloth();
        Vector3[] particles = PulledParticles(cloth);
        var kernel = new ReferenceShapeKernel();
        var expected = new Vector3[cloth.Topology.Positions.Length];
        var actual = new Vector3[expected.Length];

        cloth.DeformWelded(particles, expected);
        cloth.DeformWelded(particles, actual, kernel);

        Assert.Equal(1, kernel.DeformCalls);
        float moved = 0f;
        for (int v = 0; v < expected.Length; v++)
        {
            moved = MathF.Max(moved, Vector3.Distance(expected[v], cloth.Topology.Positions[v]));
            Assert.True(Vector3.Distance(expected[v], actual[v]) < 1e-5f, $"頂点 {v}");
        }
        Assert.True(moved > 0.01f, "動いていません");
    }

    [Fact]
    public void ExpandVertices_WithKernel_MatchesCSharpAndFollowsWeldedMotion()
    {
        var cloth = BuildSeamCloth();
        Vector3[] particles = PulledParticles(cloth);
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(particles, welded);

        // 元の頂点（継ぎ目で同じ位置に2つある）。基準はまとめる前の位置
        ProceduralMeshes.GridWithSeam(20, 20, 0.3f, 0.3f, out var baseVertices, out _);
        int[] map = cloth.Topology.VertexToWelded;
        Assert.True(map.Length > welded.Length, "継ぎ目で頂点がまとまっていません");
        var kernel = new ReferenceShapeKernel();
        var expected = new Vector3[baseVertices.Length];
        var actual = new Vector3[baseVertices.Length];

        ShapeKernels.ExpandVertices(null, expected, baseVertices, welded, cloth.Topology.Positions, map);
        ShapeKernels.ExpandVertices(kernel, actual, baseVertices, welded, cloth.Topology.Positions, map);

        Assert.Equal(1, kernel.ExpandCalls);
        for (int v = 0; v < expected.Length; v++)
        {
            Vector3 motion = welded[map[v]] - cloth.Topology.Positions[map[v]];
            Assert.True(Vector3.Distance(expected[v], baseVertices[v] + motion) < 1e-5f, $"頂点 {v}");
            Assert.True(Vector3.Distance(expected[v], actual[v]) < 1e-5f, $"頂点 {v}");
        }
    }

    [Fact]
    public void ExpandVertices_RejectsMismatchedLengths()
    {
        var vertices = new Vector3[3];
        Assert.Throws<ArgumentException>(() =>
            ShapeKernels.ExpandVertices(null, vertices, new Vector3[2], new Vector3[1], new Vector3[1], new int[3]));
    }

    [Fact]
    public void Refit_WithKernel_GivesSameBoxesAndRaycastResults()
    {
        var random = new Random(7);
        var vertices = new Vector3[3000 * 3];
        var triangles = new int[vertices.Length];
        for (int t = 0; t < 3000; t++)
        {
            var center = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            for (int k = 0; k < 3; k++)
            {
                vertices[3 * t + k] = center + new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f) * 0.1f;
                triangles[3 * t + k] = 3 * t + k;
            }
        }
        var viaCSharp = new TriangleBvh(vertices, triangles);
        var viaKernel = new TriangleBvh(vertices, triangles);
        var kernel = new ReferenceShapeKernel();

        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] += new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.3f;
        }
        viaCSharp.Refit(vertices);
        viaKernel.Refit(vertices, kernel);

        Assert.Equal(1, kernel.RefitCalls);
        Assert.Equal(viaCSharp.NodeCount, viaKernel.NodeCount);
        for (int node = 0; node < viaCSharp.NodeCount; node++)
        {
            viaCSharp.GetNodeBox(node, out Vector3 expectedMin, out Vector3 expectedMax);
            viaKernel.GetNodeBox(node, out Vector3 actualMin, out Vector3 actualMax);
            Assert.Equal(expectedMin, actualMin);
            Assert.Equal(expectedMax, actualMax);
        }

        int hits = 0;
        for (int r = 0; r < 500; r++)
        {
            var origin = new Vector3((float)random.NextDouble() * 3f - 1f, (float)random.NextDouble() * 3f - 1f, (float)random.NextDouble() * 3f - 1f);
            Vector3 direction = Vector3.Normalize(new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) - origin);
            bool expected = viaCSharp.Raycast(origin, direction, vertices, out float expectedDistance, out int expectedTriangle);
            bool actual = viaKernel.Raycast(origin, direction, vertices, out float distance, out int triangle);
            Assert.Equal(expected, actual);
            if (!expected) continue;
            hits++;
            Assert.Equal(expectedDistance, distance);
            Assert.Equal(expectedTriangle, triangle);
        }
        Assert.True(hits > 50, $"当たったレイが少なすぎます（{hits}）");
    }

    [Fact]
    public void GetNodeBox_RejectsOutOfRangeNode()
    {
        ProceduralMeshes.Grid(4, 4, 1f, 1f, out var vertices, out var triangles);
        var bvh = new TriangleBvh(vertices, triangles);
        Assert.Throws<ArgumentOutOfRangeException>(() => bvh.GetNodeBox(bvh.NodeCount, out _, out _));
    }

    // 時間がかかるふりをするカーネル：前後の処理に overhead ミリ秒、反復1回に perIteration ミリ秒（待つだけで計算はしない）
    sealed class SlowKernel : IClothSolverKernel
    {
        readonly double _overhead, _perIteration;
        public int IterateCalls, IterationsTotal;
        public SlowKernel(double overhead, double perIteration)
        {
            _overhead = overhead;
            _perIteration = perIteration;
        }

        public string Name => "Slow";
        public bool IsAccelerated => false;

        public void Iterate(in ClothSolverIterationData data, int iterations)
        {
            IterateCalls++;
            IterationsTotal += iterations;
            var timer = Stopwatch.StartNew();
            double wait = _overhead + _perIteration * iterations;
            while (timer.Elapsed.TotalMilliseconds < wait) { }
        }

        public void SolveCoarseLevel(in ClothSolverCoarseLevelData data) { }
    }

    static ClothSolver CreateSlowSolver(SlowKernel kernel)
    {
        ProceduralMeshes.Grid(10, 10, 0.2f, 0.2f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        return new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = kernel };
    }

    [Fact]
    public void SolveFor_FillsBudgetWithFewSolves()
    {
        // 前後の処理が 5ms と重く、反復は 0.5ms。5回ずつ解くと 1回 7.5ms で、60ms なら 8回の Solve（40回の反復）になる
        var kernel = new SlowKernel(5.0, 0.5);
        ClothSolver solver = CreateSlowSolver(kernel);

        var timer = Stopwatch.StartNew();
        int done = solver.SolveFor(60.0);
        timer.Stop();

        Assert.Equal(kernel.IterationsTotal, done);
        Assert.True(kernel.IterateCalls <= 4, $"Solve の回数が多すぎます（{kernel.IterateCalls}）");
        Assert.True(done >= 60, $"反復が少なすぎます（{done}）");
        Assert.True(timer.Elapsed.TotalMilliseconds < 60.0 + 25.0, $"持ち時間を大きく超えました（{timer.Elapsed.TotalMilliseconds:F1} ms）");
    }

    [Fact]
    public void SolveFor_RemembersTimingAcrossCalls()
    {
        var kernel = new SlowKernel(3.0, 0.4);
        ClothSolver solver = CreateSlowSolver(kernel);
        solver.SolveFor(18.0);   // 時間の測定

        int callsBefore = kernel.IterateCalls;
        int iterations = solver.SolveFor(18.0);

        // 時間が分かっていれば、1コマ（18ms）は1回の Solve で使い切る：(18 - 3) / 0.4 = 37 回
        Assert.InRange(kernel.IterateCalls - callsBefore, 1, 2);
        Assert.InRange(iterations, 25, ClothSolver.MaxIterationsPerSolve);
    }

    [Fact]
    public void SolveFor_NeverExceedsMaxIterationsPerSolve()
    {
        // ほぼ時間がかからない：持ち時間を使い切るまでに、何度も Solve するが、1回の反復は上限以下
        var kernel = new SlowKernel(0.0, 0.0);
        ClothSolver solver = CreateSlowSolver(kernel);
        var tracking = new TrackingKernel(kernel);
        solver.Kernel = tracking;

        solver.SolveFor(5.0);

        Assert.InRange(tracking.MaxStep, 1, ClothSolver.MaxIterationsPerSolve);
    }

    sealed class TrackingKernel : IClothSolverKernel
    {
        readonly IClothSolverKernel _inner;
        public int MaxStep;
        public TrackingKernel(IClothSolverKernel inner) => _inner = inner;
        public string Name => "Tracking";
        public bool IsAccelerated => false;

        public void Iterate(in ClothSolverIterationData data, int iterations)
        {
            MaxStep = Math.Max(MaxStep, iterations);
            _inner.Iterate(data, iterations);
        }

        public void SolveCoarseLevel(in ClothSolverCoarseLevelData data) => _inner.SolveCoarseLevel(data);
    }

    [Fact]
    public void SolveFor_ReturnsAtLeastOneIteration_WhenBudgetIsSmallerThanOneSolve()
    {
        var kernel = new SlowKernel(10.0, 1.0);
        ClothSolver solver = CreateSlowSolver(kernel);
        solver.SolveFor(40.0);   // 時間の測定

        int iterations = solver.SolveFor(0.0);

        Assert.Equal(1, iterations);
    }
}
