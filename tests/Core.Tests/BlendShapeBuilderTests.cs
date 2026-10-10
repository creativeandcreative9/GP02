using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class BlendShapeBuilderTests
{
    const float Tolerance = 1e-5f;

    [Fact]
    public void Build_WithoutEdit_IsZero()
    {
        ProceduralMeshes.Grid(10, 10, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];

        BlendShapeBuilder.Build(cloth, cloth.Model.RestPositions, deltaVertices, deltaNormals);

        Assert.All(deltaVertices, d => Assert.True(d.Length() < Tolerance));
        Assert.All(deltaNormals, d => Assert.True(d.Length() < Tolerance));
    }

    [Fact]
    public void Build_MatchesDeformedVertices()
    {
        ProceduralMeshes.Grid(10, 10, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = new ClothSolver(cloth.Model);
        int center = solver.FindNearestParticle(new Vector3(0.15f, 0.15f, 0f));
        solver.Grab(center, solver.Positions[center] + new Vector3(0f, 0f, 0.05f), 0.02f);
        solver.Solve(40);
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(solver.Positions, welded);
        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];

        BlendShapeBuilder.Build(cloth, solver.Positions, deltaVertices, deltaNormals);

        for (int v = 0; v < vertices.Length; v++)
        {
            Vector3 expected = welded[cloth.Topology.VertexToWelded[v]] - vertices[v];
            Assert.True(Vector3.Distance(expected, deltaVertices[v]) < Tolerance, $"頂点 {v}");
        }
        // 中央付近の頂点は手前に動き、法線も変わる
        int middle = 5 * 11 + 5;
        Assert.True(deltaVertices[middle].Z > 0.02f);
        Assert.Contains(deltaNormals, d => d.Length() > 0.01f);
    }

    [Fact]
    public void Build_SeamVerticesGetSameDelta()
    {
        ProceduralMeshes.GridWithSeam(10, 10, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = new ClothSolver(cloth.Model);
        int center = solver.FindNearestParticle(new Vector3(0.15f, 0.05f, 0f));
        solver.Grab(center, solver.Positions[center] + new Vector3(0f, 0f, 0.05f), 0.02f);
        solver.Solve(40);
        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];

        BlendShapeBuilder.Build(cloth, solver.Positions, deltaVertices, deltaNormals);

        // 継ぎ目の頂点（元の頂点と複製）は、同じ差分になる
        for (int r = 0; r <= 10; r++)
        {
            int original = r * 11 + 5;
            int duplicate = 11 * 11 + r;
            Assert.Equal(deltaVertices[original], deltaVertices[duplicate]);
            Assert.Equal(deltaNormals[original], deltaNormals[duplicate]);
        }
        Assert.True(deltaVertices[1 * 11 + 5].Length() > 0.01f);
    }

    [Fact]
    public void Build_RejectsWrongLengths()
    {
        ProceduralMeshes.Grid(2, 2, 0.1f, 0.1f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);

        Assert.Throws<ArgumentException>(() =>
            BlendShapeBuilder.Build(cloth, cloth.Model.RestPositions, new Vector3[1], new Vector3[vertices.Length]));
    }
}
