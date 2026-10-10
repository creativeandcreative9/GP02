using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ClothModelTests
{
    [Fact]
    public void Weld_MergesSeamVertices()
    {
        ProceduralMeshes.GridWithSeam(4, 3, 1f, 1f, out var vertices, out _);

        int[] map = VertexWelder.Weld(vertices, ClothModel.DefaultWeldTolerance, out var particles);

        Assert.Equal(5 * 4 + 4, vertices.Length);
        Assert.Equal(5 * 4, particles.Length);
        for (int v = 0; v < vertices.Length; v++)
        {
            Assert.Equal(vertices[v], particles[map[v]]);
        }
    }

    [Fact]
    public void Build_Grid_CreatesEdgeAndBendConstraints()
    {
        const int columns = 3, rows = 2;
        ProceduralMeshes.Grid(columns, rows, 1f, 1f, out var vertices, out var triangles);

        var model = ClothModel.Build(vertices, triangles);

        int horizontal = columns * (rows + 1);
        int vertical = (columns + 1) * rows;
        int diagonal = columns * rows;
        int edges = horizontal + vertical + diagonal;
        int boundaryEdges = 2 * columns + 2 * rows;
        Assert.Equal((columns + 1) * (rows + 1), model.ParticleCount);
        Assert.Equal(edges, model.StretchRestLengths.Length);
        // 内側の辺1本につき、曲げの制約が1つ
        Assert.Equal(edges - boundaryEdges, model.BendRestLengths.Length);
    }

    [Fact]
    public void Build_GridWithSeam_IsSameAsGridWithoutSeam()
    {
        ProceduralMeshes.Grid(4, 3, 1f, 1f, out var gridVertices, out var gridTriangles);
        ProceduralMeshes.GridWithSeam(4, 3, 1f, 1f, out var seamVertices, out var seamTriangles);

        var grid = ClothModel.Build(gridVertices, gridTriangles);
        var seam = ClothModel.Build(seamVertices, seamTriangles);

        Assert.Equal(grid.ParticleCount, seam.ParticleCount);
        Assert.Equal(grid.StretchRestLengths.Length, seam.StretchRestLengths.Length);
        Assert.Equal(grid.BendRestLengths.Length, seam.BendRestLengths.Length);
    }

    [Fact]
    public void Build_SkipsDegenerateTriangles()
    {
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY };
        var triangles = new[] { 0, 1, 2, 0, 1, 3 };

        var model = ClothModel.Build(vertices, triangles);

        Assert.Equal(3, model.Triangles.Length);
    }
}
