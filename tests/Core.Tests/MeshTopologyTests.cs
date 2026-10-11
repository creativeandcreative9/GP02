using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class MeshTopologyTests
{
    [Fact]
    public void SourceTriangleMaps_SkipCollapsedTriangles()
    {
        // 四角形（三角形2枚）の間に、同じ位置の頂点でできた潰れた三角形と、頂点が重複した三角形を挟む
        var vertices = new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f),
            new Vector3(5f, 5f, 5f), new Vector3(5f, 5f, 5f), new Vector3(5f, 5f, 5f),
        };
        var triangles = new[]
        {
            0, 1, 2,      // 元の0番 → 0
            4, 5, 6,      // 元の1番 → 潰れる
            1, 3, 2,      // 元の2番 → 1
            0, 0, 1,      // 元の3番 → 潰れる
        };

        var topology = MeshTopology.Build(vertices, triangles);

        Assert.Equal(2, topology.TriangleCount);
        Assert.Equal(new[] { 0, -1, 1, -1 }, topology.SourceToTriangle);
        Assert.Equal(new[] { 0, 2 }, topology.TriangleToSource);
        Assert.Equal(topology.TriangleIsland[0], topology.TriangleIsland[1]);
    }

    [Fact]
    public void BackFaceOverlay_IsOneIsland()
    {
        // 裏面用に、同じ位置・逆向きの三角形を別の頂点で重ねる
        ProceduralMeshes.Grid(4, 4, 1f, 1f, out var front, out var frontTriangles);
        var vertices = new Vector3[front.Length * 2];
        var triangles = new int[frontTriangles.Length * 2];
        for (int i = 0; i < front.Length; i++)
        {
            vertices[i] = front[i];
            vertices[front.Length + i] = front[i];
        }
        for (int t = 0; t < frontTriangles.Length; t += 3)
        {
            for (int k = 0; k < 3; k++) triangles[t + k] = frontTriangles[t + k];
            triangles[frontTriangles.Length + t] = frontTriangles[t] + front.Length;
            triangles[frontTriangles.Length + t + 1] = frontTriangles[t + 2] + front.Length;
            triangles[frontTriangles.Length + t + 2] = frontTriangles[t + 1] + front.Length;
        }

        var topology = MeshTopology.Build(vertices, triangles);

        Assert.Equal(1, topology.IslandCount);
        Assert.Equal(triangles.Length / 3, topology.TriangleCount);
    }
}
