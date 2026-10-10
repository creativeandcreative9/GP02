using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class BlendShapeBuilderTests
{
    const float Tolerance = 1e-5f;

    [Fact]
    public void Build_WithoutEdit_IsZero()
    {
        ProceduralMeshes.Grid(4, 4, 1f, 1f, out var vertices, out var triangles);
        var model = ClothModel.Build(vertices, triangles);
        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];

        BlendShapeBuilder.Build(model, model.RestPositions, deltaVertices, deltaNormals);

        Assert.All(deltaVertices, d => Assert.True(d.Length() < Tolerance));
        Assert.All(deltaNormals, d => Assert.True(d.Length() < Tolerance));
    }

    [Fact]
    public void Build_ReturnsEditedMinusRest()
    {
        ProceduralMeshes.Grid(4, 4, 1f, 1f, out var vertices, out var triangles);
        var model = ClothModel.Build(vertices, triangles);
        var edited = (Vector3[])model.RestPositions.Clone();
        edited[7] += new Vector3(0f, 0f, 0.1f);
        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];

        BlendShapeBuilder.Build(model, edited, deltaVertices, deltaNormals);

        for (int v = 0; v < vertices.Length; v++)
        {
            Vector3 expected = v == 7 ? new Vector3(0f, 0f, 0.1f) : Vector3.Zero;
            Assert.True(Vector3.Distance(expected, deltaVertices[v]) < Tolerance, $"頂点 {v}");
        }
        // 持ち上げた点のまわりは傾くので、法線が変わる
        Assert.True(deltaNormals[6].Length() > 0.01f);
    }

    [Fact]
    public void Build_SeamVerticesGetSameDelta()
    {
        ProceduralMeshes.GridWithSeam(4, 4, 1f, 1f, out var vertices, out var triangles);
        var model = ClothModel.Build(vertices, triangles);
        var solver = new ClothSolver(model);
        for (int c = 0; c <= 4; c++) solver.SetPinned(model.VertexToParticle[4 * 5 + c], true);
        int seamBottom = model.VertexToParticle[2];
        solver.Grab(seamBottom, model.RestPositions[seamBottom] + new Vector3(0f, 0f, 0.2f));
        solver.Solve(100);
        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];

        BlendShapeBuilder.Build(model, solver.Positions, deltaVertices, deltaNormals);

        // 継ぎ目の頂点（元の頂点と複製）は、同じ差分になる
        for (int r = 0; r <= 4; r++)
        {
            int original = r * 5 + 2;
            int duplicate = 5 * 5 + r;
            Assert.Equal(deltaVertices[original], deltaVertices[duplicate]);
            Assert.Equal(deltaNormals[original], deltaNormals[duplicate]);
        }
        Assert.True(deltaVertices[2].Length() > 0.1f);
    }

    [Fact]
    public void Build_RejectsWrongLengths()
    {
        ProceduralMeshes.Grid(2, 2, 1f, 1f, out var vertices, out var triangles);
        var model = ClothModel.Build(vertices, triangles);

        Assert.Throws<ArgumentException>(() =>
            BlendShapeBuilder.Build(model, model.RestPositions, new Vector3[1], new Vector3[vertices.Length]));
    }
}
