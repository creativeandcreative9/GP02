using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>ブラシを当てる前の範囲の調べ方（CollectBrushRange）と、粒子ごとの伸び（MeasureParticleStretch）のテスト。</summary>
public class BrushPreviewTests
{
    const float Tolerance = 1e-5f;

    // 頂点がそのまま粒子になる平らな布（XY 平面、1cm 間隔）
    static ClothSolver FlatCloth(int cells = 20, float size = 0.2f)
    {
        ProceduralMeshes.Grid(cells, cells, size, size, out var vertices, out var triangles);
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
    }

    static ClothSolver TwoLayers(float gap, out int frontCount)
    {
        ProceduralMeshes.Grid(20, 20, 0.2f, 0.2f, out var front, out var frontTriangles);
        var back = front.Select(v => v + new Vector3(0f, 0f, -gap)).ToArray();
        var vertices = front.Concat(back).ToArray();
        var triangles = frontTriangles.Concat(frontTriangles.Select(i => i + front.Length)).ToArray();
        frontCount = front.Length;
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
    }

    [Theory]
    [InlineData(BrushDistanceMode.Surface)]
    [InlineData(BrushDistanceMode.Linear)]
    public void CollectBrushRange_MatchesTheRangeAndWeightsOfGrab(BrushDistanceMode mode)
    {
        ClothSolver solver = FlatCloth();
        int seed = solver.FindNearestParticle(new Vector3(0f, 0f, 0f));
        var options = new BrushOptions { Radius = 0.05f, Falloff = FalloffCurve.Smooth, DistanceMode = mode };

        var indices = new int[solver.Model.ParticleCount];
        var weights = new float[indices.Length];
        int count = solver.CollectBrushRange(seed, options.Radius, options.Falloff, mode, indices, weights, indices.Length);

        solver.Grab(seed, default, options);
        Assert.Equal(solver.BrushParticleCount, count);
        var seen = new HashSet<int>();
        for (int k = 0; k < count; k++)
        {
            Assert.True(solver.IsGrabbed(indices[k]));
            Assert.Equal(solver.BrushWeight(indices[k]), weights[k], Tolerance);
            Assert.True(seen.Add(indices[k]), "同じ粒子が2回入っている");
        }
        Assert.Contains(seed, seen);
        Assert.Equal(1f, weights[Array.IndexOf(indices, seed, 0, count)], Tolerance);
    }

    [Fact]
    public void CollectBrushRange_DoesNotChangeTheGrabState()
    {
        ClothSolver solver = FlatCloth();
        int seed = solver.FindNearestParticle(new Vector3(0.05f, 0f, 0f));
        Assert.Equal(-1, solver.GrabbedParticle);

        var indices = new int[500];
        var weights = new float[500];
        solver.CollectBrushRange(seed, 0.04f, FalloffCurve.Linear, BrushDistanceMode.Surface, indices, weights, 500);
        Assert.Equal(-1, solver.GrabbedParticle);

        // つまんでいる間に調べても、つまんだ範囲は変わらない
        solver.Grab(seed, default, new BrushOptions { Radius = 0.03f });
        int before = solver.BrushParticleCount;
        solver.CollectBrushRange(seed, 0.08f, FalloffCurve.Linear, BrushDistanceMode.Surface, indices, weights, 500);
        Assert.Equal(before, solver.BrushParticleCount);
        Assert.Equal(seed, solver.GrabbedParticle);
    }

    [Fact]
    public void CollectBrushRange_SurfaceSkipsDisconnectedLayers_LinearIncludesThem()
    {
        ClothSolver solver = TwoLayers(0.003f, out int frontCount);
        int seed = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        Assert.True(seed < frontCount);
        var indices = new int[solver.Model.ParticleCount];
        var weights = new float[indices.Length];

        int surface = solver.CollectBrushRange(seed, 0.03f, FalloffCurve.Constant, BrushDistanceMode.Surface, indices, weights, indices.Length);
        for (int k = 0; k < surface; k++) Assert.True(indices[k] < frontCount);

        int linear = solver.CollectBrushRange(seed, 0.03f, FalloffCurve.Constant, BrushDistanceMode.Linear, indices, weights, indices.Length);
        Assert.True(linear > surface);
        bool hasBack = false;
        for (int k = 0; k < linear; k++) hasBack |= indices[k] >= frontCount;
        Assert.True(hasBack);
    }

    [Fact]
    public void CollectBrushRange_ThinsOutToTheLimit_KeepsTheCenter()
    {
        ClothSolver solver = FlatCloth();
        int seed = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        var indices = new int[1000];
        var weights = new float[1000];
        int all = solver.CollectBrushRange(seed, 0.08f, FalloffCurve.Constant, BrushDistanceMode.Surface, indices, weights, 1000);
        Assert.True(all > 60);

        int limited = solver.CollectBrushRange(seed, 0.08f, FalloffCurve.Constant, BrushDistanceMode.Surface, indices, weights, 25);
        Assert.InRange(limited, 10, 25);
        Assert.Contains(seed, indices.Take(limited));

        Assert.Equal(0, solver.CollectBrushRange(seed, 0.08f, FalloffCurve.Constant, BrushDistanceMode.Surface, indices, weights, 0));
    }

    [Fact]
    public void CollectBrushRange_ZeroWeightRegionsAreLeftOut()
    {
        ClothSolver solver = FlatCloth();
        int seed = solver.FindNearestParticle(new Vector3(0f, 0f, 0f));
        var indices = new int[500];
        var weights = new float[500];
        // 端が 0 のカーブ（Linear）では、半径ちょうどの粒子は重み 0 なので入らない
        int count = solver.CollectBrushRange(seed, 0.03f, FalloffCurve.Linear, BrushDistanceMode.Surface, indices, weights, 500);
        for (int k = 0; k < count; k++) Assert.True(weights[k] > 0f);
    }

    [Fact]
    public void MeasureParticleStretch_IsZeroAtRest_AndFindsTheStretchedParticles()
    {
        ClothSolver solver = FlatCloth(10, 0.1f);
        var stretch = new float[solver.Model.ParticleCount];
        solver.MeasureParticleStretch(stretch);
        Assert.All(stretch, s => Assert.True(s < 1e-4f));

        // 1つの粒子を 1 辺の半分だけ外へ動かす → その粒子とつながった粒子が伸びる
        int p = solver.FindNearestParticle(new Vector3(0f, 0f, 0f));
        var moved = solver.Positions.ToArray();
        moved[p] += new Vector3(0f, 0f, 0.005f);
        solver.SetPositions(moved);
        solver.MeasureParticleStretch(stretch);
        Assert.True(stretch[p] > 0.05f);
        Assert.Equal(solver.MeasureMaxStretch() > 0f, stretch.Max() > 0f);
        Assert.Equal(solver.MeasureMaxStretch(), stretch.Max(), 1e-4f);
        int far = solver.FindNearestParticle(new Vector3(0.05f, 0.05f, 0f));
        Assert.True(stretch[far] < 1e-4f);
    }

    [Fact]
    public void MeasureParticleStretch_IgnoresCompression()
    {
        ClothSolver solver = FlatCloth(10, 0.1f);
        int p = solver.FindNearestParticle(new Vector3(0f, 0f, 0f));
        var moved = solver.Positions.ToArray();
        moved[p] = moved[solver.FindNearestParticle(new Vector3(0.01f, 0f, 0f))];   // 隣に重ねる（縮む）
        solver.SetPositions(moved);
        var stretch = new float[solver.Model.ParticleCount];
        solver.MeasureParticleStretch(stretch);
        Assert.All(stretch, s => Assert.True(s >= 0f));
    }

    [Fact]
    public void CollectBrushRange_CanAppendAfterAnOffset()
    {
        ClothSolver solver = FlatCloth();
        int a = solver.FindNearestParticle(new Vector3(0.05f, 0.1f, 0f));
        int b = solver.FindNearestParticle(new Vector3(0.15f, 0.1f, 0f));
        var indices = new int[400];
        var weights = new float[400];
        int first = solver.CollectBrushRange(a, 0.02f, FalloffCurve.Constant, BrushDistanceMode.Surface, indices, weights, 200);
        int second = solver.CollectBrushRange(b, 0.02f, FalloffCurve.Constant, BrushDistanceMode.Surface, indices, weights, 200, first);
        Assert.True(first > 0 && second > 0);
        Assert.Contains(a, indices.Take(first));
        Assert.Contains(b, indices.Skip(first).Take(second));
        Assert.DoesNotContain(b, indices.Take(first));
    }
}
