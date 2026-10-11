using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ParticlePositionsFromWeldedTests
{
    static ParticleCloth BuildCloth()
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        return ParticleCloth.Build(vertices, triangles);
    }

    // なめらかな変形（手前への盛り上がりと、横方向のゆるいずれ）
    static Vector3 Smooth(Vector3 p) =>
        new Vector3(p.X + 0.01f * MathF.Sin(9f * p.Y), p.Y + 0.01f * p.X, 0.03f * MathF.Sin(7f * p.X) * MathF.Cos(5f * p.Y));

    [Fact]
    public void AtRestShapeParticlesStayAtRest()
    {
        var cloth = BuildCloth();
        var particles = new Vector3[cloth.Model.ParticleCount];

        cloth.ParticlePositionsFromWelded(cloth.Topology.Positions, particles);

        for (int i = 0; i < particles.Length; i++)
        {
            Assert.True(Vector3.Distance(particles[i], cloth.Model.RestPositions[i]) < 1e-5f, $"粒子 {i}");
        }
    }

    [Fact]
    public void SmoothDeformation_RoundTripsThroughDeformWeldedWithinOneMillimeter()
    {
        var cloth = BuildCloth();
        int n = cloth.Model.ParticleCount;
        var deformedParticles = new Vector3[n];
        for (int i = 0; i < n; i++) deformedParticles[i] = Smooth(cloth.Model.RestPositions[i]);
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(deformedParticles, welded);

        var restored = new Vector3[n];
        cloth.ParticlePositionsFromWelded(welded, restored);
        var weldedAgain = new Vector3[welded.Length];
        cloth.DeformWelded(restored, weldedAgain);

        float maxParticleError = 0f, maxVertexError = 0f;
        for (int i = 0; i < n; i++) maxParticleError = MathF.Max(maxParticleError, Vector3.Distance(deformedParticles[i], restored[i]));
        for (int i = 0; i < welded.Length; i++) maxVertexError = MathF.Max(maxVertexError, Vector3.Distance(welded[i], weldedAgain[i]));
        Assert.True(maxParticleError < 1e-3f, $"粒子の誤差 {maxParticleError}");
        Assert.True(maxVertexError < 1e-3f, $"頂点の誤差 {maxVertexError}");
        // 何も動かない変形のテストになっていない
        Assert.True(Vector3.Distance(deformedParticles[n / 2], cloth.Model.RestPositions[n / 2]) > 1e-3f);
    }

    [Fact]
    public void RebaseCarriesBarycentricCoordinates()
    {
        var cloth = BuildCloth();
        int n = cloth.Model.ParticleCount;
        var deformedParticles = new Vector3[n];
        for (int i = 0; i < n; i++) deformedParticles[i] = Smooth(cloth.Model.RestPositions[i]);

        var rebased = cloth.Rebase(deformedParticles);
        var particles = new Vector3[n];
        rebased.ParticlePositionsFromWelded(rebased.Topology.Positions, particles);

        for (int i = 0; i < n; i++)
        {
            Assert.True(Vector3.Distance(particles[i], deformedParticles[i]) < 1e-3f, $"粒子 {i}");
        }
    }

    [Fact]
    public void WorksWithSeamedMesh()
    {
        ProceduralMeshes.GridWithSeam(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var welded = new Vector3[cloth.Topology.Positions.Length];
        for (int i = 0; i < welded.Length; i++) welded[i] = Smooth(cloth.Topology.Positions[i]);

        var particles = new Vector3[cloth.Model.ParticleCount];
        cloth.ParticlePositionsFromWelded(welded, particles);

        for (int i = 0; i < particles.Length; i++)
        {
            Assert.True(Vector3.Distance(particles[i], Smooth(cloth.Model.RestPositions[i])) < 1e-3f, $"粒子 {i}");
        }
    }

    [Fact]
    public void RejectsWrongSizes()
    {
        var cloth = BuildCloth();
        var particles = new Vector3[cloth.Model.ParticleCount];
        Assert.Throws<ArgumentException>(() => cloth.ParticlePositionsFromWelded(new Vector3[3], particles));
        Assert.Throws<ArgumentException>(() => cloth.ParticlePositionsFromWelded(cloth.Topology.Positions, new Vector3[3]));
    }
}
