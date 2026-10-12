using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

// ParticleCloth.FitParticlesToWelded（頂点の位置から粒子の位置を作り、頂点に戻したときの差を小さくする）と、その判定に使う伸びの測り方
public class ParticleClothFitTests
{
    // 30cm四方の布（頂点 1.5cm 間隔）。上辺をピンで留めて、真ん中を手前に引っ張る。settle なら、つまんだまま仕上げてから離す
    static ParticleCloth PulledGrid(bool settle, out Vector3[] particles)
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.2f };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 0.3f - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.15f, 0.15f, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(center, start + new Vector3(0.02f, -0.01f, 0.05f) * f / 10, 0.04f);
            solver.Solve(20);
        }
        if (settle) solver.Settle(0.01f, 4000, out _);
        solver.Release();
        particles = solver.Positions.ToArray();
        return cloth;
    }

    static Vector3[] Welded(ParticleCloth cloth, Vector3[] particles)
    {
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(particles, welded);
        return welded;
    }

    static float MaxDistance(Vector3[] a, Vector3[] b)
    {
        float max = 0f;
        for (int i = 0; i < a.Length; i++) max = MathF.Max(max, Vector3.Distance(a[i], b[i]));
        return max;
    }

    [Fact]
    public void ZeroIterations_IsTheSameAsBarycentricInterpolation()
    {
        var cloth = PulledGrid(true, out var original);
        Vector3[] target = Welded(cloth, original);
        var plain = new Vector3[original.Length];
        cloth.ParticlePositionsFromWelded(target, plain);

        var fitted = new Vector3[original.Length];
        float residual = cloth.FitParticlesToWelded(target, fitted, 0, out int done);

        Assert.Equal(0, done);
        Assert.True(MaxDistance(plain, fitted) == 0f);
        Assert.Equal(MaxDistance(target, Welded(cloth, plain)), residual, 6);
    }

    [Fact]
    public void AtRest_ParticlesStayAtRest()
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var particles = new Vector3[cloth.Model.ParticleCount];

        float residual = cloth.FitParticlesToWelded(cloth.Topology.Positions, particles, 8, out _);

        Assert.True(residual < 1e-5f, $"頂点の差 {residual}");
        Assert.True(MaxDistance(particles, cloth.Model.RestPositions) < 1e-5f);
    }

    // 仕上げた（伸びの小さい）編集の形を頂点にして読み込むと、補間だけより頂点の差が大きく減り、粒子の伸びは補間だけ（か、許す伸びの 2%）より増えない
    [Fact]
    public void SettledEdit_FitsTheVerticesMuchCloser_WithoutMoreStretch()
    {
        var cloth = PulledGrid(true, out var original);
        Vector3[] target = Welded(cloth, original);
        var plain = new Vector3[original.Length];
        cloth.ParticlePositionsFromWelded(target, plain);
        float plainResidual = MaxDistance(target, Welded(cloth, plain));
        float plainStretch = cloth.MeasureMaxStretch(plain);

        var fitted = new Vector3[original.Length];
        float residual = cloth.FitParticlesToWelded(target, fitted, 8, out int done);

        Assert.True(plainResidual > 1e-3f, $"補間だけの差が小さすぎて、テストにならない（{plainResidual}）");
        Assert.True(done > 0);
        Assert.Equal(MaxDistance(target, Welded(cloth, fitted)), residual, 6);
        Assert.True(residual < 0.1f * plainResidual, $"頂点の差 {residual * 1000f:F3} mm（補間だけ {plainResidual * 1000f:F3} mm）");
        Assert.True(cloth.MeasureMaxStretch(fitted) <= MathF.Max(plainStretch, ParticleCloth.FitStretchAllowance) + 1e-4f, $"伸び {cloth.MeasureMaxStretch(fitted)}（補間だけ {plainStretch}）");
        // 元の粒子にも近づく（頂点に見えない動きを足していない）
        Assert.True(MaxDistance(original, fitted) < MaxDistance(original, plain));
    }

    // 大きく伸びた形（仕上げていない）でも、粒子の伸びは「補間だけ」「頂点の形（メッシュの辺）」「許す伸び（2%）」の一番大きいものを超えず、頂点の差は増えない
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeverStretchesBeyondTheLimit_AndNeverFitsWorse(bool settle)
    {
        var cloth = PulledGrid(settle, out var original);
        Vector3[] target = Welded(cloth, original);
        var plain = new Vector3[original.Length];
        cloth.ParticlePositionsFromWelded(target, plain);
        float limit = MathF.Max(ParticleCloth.FitStretchAllowance, MathF.Max(cloth.MeasureMaxStretch(plain), cloth.MeasureMaxEdgeStretch(target)));

        var fitted = new Vector3[original.Length];
        float residual = cloth.FitParticlesToWelded(target, fitted, 8, out _);

        Assert.True(cloth.MeasureMaxStretch(fitted) <= limit + 1e-6f);
        Assert.True(residual <= MaxDistance(target, Welded(cloth, plain)) + 1e-7f);
    }

    [Fact]
    public void IsDeterministic()
    {
        var cloth = PulledGrid(true, out var original);
        Vector3[] target = Welded(cloth, original);
        var a = new Vector3[original.Length];
        var b = new Vector3[original.Length];
        cloth.FitParticlesToWelded(target, a, 8, out int doneA);
        cloth.FitParticlesToWelded(target, b, 8, out int doneB);
        Assert.Equal(doneA, doneB);
        Assert.True(MaxDistance(a, b) == 0f);
    }

    [Fact]
    public void RejectsWrongArguments()
    {
        var cloth = PulledGrid(false, out var original);
        Assert.Throws<ArgumentOutOfRangeException>(() => cloth.FitParticlesToWelded(cloth.Topology.Positions, new Vector3[original.Length], -1, out _));
        Assert.Throws<ArgumentException>(() => cloth.FitParticlesToWelded(new Vector3[3], new Vector3[original.Length], 4, out _));
        Assert.Throws<ArgumentException>(() => cloth.MeasureMaxStretch(new Vector3[3]));
        Assert.Throws<ArgumentException>(() => cloth.MeasureMaxEdgeStretch(new Vector3[3]));
    }

    // ---- 伸びの測り方 ----

    [Fact]
    public void Stretch_IsZeroAtRest_AndTenPercentWhenScaledBy1_1()
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        Assert.InRange(cloth.MeasureMaxStretch(cloth.Model.RestPositions), 0f, 1e-5f);
        Assert.InRange(cloth.MeasureMaxEdgeStretch(cloth.Topology.Positions), 0f, 1e-5f);

        var scaledParticles = cloth.Model.RestPositions.Select(p => p * 1.1f).ToArray();
        var scaledVertices = cloth.Topology.Positions.Select(p => p * 1.1f).ToArray();
        Assert.Equal(0.1f, cloth.MeasureMaxStretch(scaledParticles), 4);
        Assert.Equal(0.1f, cloth.MeasureMaxEdgeStretch(scaledVertices), 4);

        // 縮みは伸びに数えない
        var shrunk = cloth.Model.RestPositions.Select(p => p * 0.9f).ToArray();
        Assert.Equal(0f, cloth.MeasureMaxStretch(shrunk));
    }
}
