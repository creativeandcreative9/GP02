using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ClothModelTests
{
    [Fact]
    public void Weld_MergesSeamVertices()
    {
        ProceduralMeshes.GridWithSeam(4, 3, 1f, 1f, out var vertices, out _);

        int[] map = VertexWelder.Weld(vertices, MeshTopology.DefaultWeldTolerance, out var welded);

        Assert.Equal(5 * 4 + 4, vertices.Length);
        Assert.Equal(5 * 4, welded.Length);
        for (int v = 0; v < vertices.Length; v++)
        {
            Assert.Equal(vertices[v], welded[map[v]]);
        }
    }

    [Fact]
    public void Topology_SeamIsOneIsland()
    {
        ProceduralMeshes.GridWithSeam(4, 3, 1f, 1f, out var vertices, out var triangles);

        var topology = MeshTopology.Build(vertices, triangles);

        Assert.Equal(1, topology.IslandCount);
        Assert.Equal(4 * 3 * 2, topology.TriangleCount);
    }

    [Fact]
    public void Topology_SeparatesDisconnectedLayers()
    {
        var (vertices, triangles) = TwoLayers(10, 0.01f);

        var topology = MeshTopology.Build(vertices, triangles);

        Assert.Equal(2, topology.IslandCount);
    }

    [Fact]
    public void Topology_SkipsDegenerateTriangles()
    {
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY };
        var triangles = new[] { 0, 1, 2, 0, 1, 3 };

        var topology = MeshTopology.Build(vertices, triangles);

        Assert.Equal(1, topology.TriangleCount);
    }

    [Fact]
    public void FromTriangleMesh_Grid_CreatesEdgeAndBendConstraints()
    {
        const int columns = 3, rows = 2;
        ProceduralMeshes.Grid(columns, rows, 1f, 1f, out var vertices, out var triangles);

        var model = ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles));

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
    public void FromTriangleMesh_GridWithSeam_IsSameAsGridWithoutSeam()
    {
        ProceduralMeshes.Grid(4, 3, 1f, 1f, out var gridVertices, out var gridTriangles);
        ProceduralMeshes.GridWithSeam(4, 3, 1f, 1f, out var seamVertices, out var seamTriangles);

        var grid = ClothModel.FromTriangleMesh(MeshTopology.Build(gridVertices, gridTriangles));
        var seam = ClothModel.FromTriangleMesh(MeshTopology.Build(seamVertices, seamTriangles));

        Assert.Equal(grid.ParticleCount, seam.ParticleCount);
        Assert.Equal(grid.StretchRestLengths.Length, seam.StretchRestLengths.Length);
        Assert.Equal(grid.BendRestLengths.Length, seam.BendRestLengths.Length);
    }

    // 指定の距離だけ離して重ねた、2枚の1m四方の布（重ね着を想定）
    internal static (Vector3[] Vertices, int[] Triangles) TwoLayers(int cells, float gap)
    {
        ProceduralMeshes.Grid(cells, cells, 1f, 1f, out var front, out var frontTriangles);
        var back = front.Select(v => v + new Vector3(0f, 0f, -gap)).ToArray();
        var vertices = front.Concat(back).ToArray();
        var triangles = frontTriangles.Concat(frontTriangles.Select(i => i + front.Length)).ToArray();
        return (vertices, triangles);
    }
}
