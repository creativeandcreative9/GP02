using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class TriangleBvhTests
{
    // 決まった乱数で作る、ばらばらの三角形（互いに重なり、向きもばらばら）
    static void RandomSoup(int triangleCount, int seed, out Vector3[] vertices, out int[] triangles)
    {
        var random = new Random(seed);
        vertices = new Vector3[triangleCount * 3];
        triangles = new int[triangleCount * 3];
        for (int t = 0; t < triangleCount; t++)
        {
            var center = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            for (int k = 0; k < 3; k++)
            {
                vertices[3 * t + k] = center + new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f) * 0.1f;
                triangles[3 * t + k] = 3 * t + k;
            }
        }
    }

    static bool BruteForce(Vector3 origin, Vector3 direction, Vector3[] vertices, int[] triangles, out float best, out int bestTriangle)
    {
        best = float.MaxValue;
        bestTriangle = -1;
        for (int t = 0; t < triangles.Length / 3; t++)
        {
            if (TriangleBvh.IntersectTriangle(origin, direction, vertices[triangles[3 * t]], vertices[triangles[3 * t + 1]],
                    vertices[triangles[3 * t + 2]], out float d) && d < best)
            {
                best = d;
                bestTriangle = t;
            }
        }
        return bestTriangle >= 0;
    }

    static void ExpectSameAsBruteForce(TriangleBvh bvh, Vector3[] vertices, int[] triangles, int rayCount, int seed)
    {
        var random = new Random(seed);
        int hits = 0;
        for (int r = 0; r < rayCount; r++)
        {
            var origin = new Vector3((float)random.NextDouble() * 3f - 1f, (float)random.NextDouble() * 3f - 1f, (float)random.NextDouble() * 3f - 1f);
            var target = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            Vector3 direction = Vector3.Normalize(target - origin);

            bool expected = BruteForce(origin, direction, vertices, triangles, out float expectedDistance, out int expectedTriangle);
            bool actual = bvh.Raycast(origin, direction, vertices, out float distance, out int triangle);
            Assert.Equal(expected, actual);
            if (!expected) continue;
            hits++;
            // 同じ距離に複数の三角形があるときは、どちらが返ってもよい
            Assert.Equal(expectedDistance, distance, 1e-5f);
            if (triangle != expectedTriangle)
            {
                Assert.True(TriangleBvh.IntersectTriangle(origin, direction, vertices[triangles[3 * triangle]], vertices[triangles[3 * triangle + 1]],
                    vertices[triangles[3 * triangle + 2]], out float other));
                Assert.Equal(expectedDistance, other, 1e-5f);
            }
        }
        Assert.True(hits > rayCount / 4, $"当たったレイが少なすぎます（{hits}）");
    }

    [Fact]
    public void Raycast_MatchesBruteForce()
    {
        RandomSoup(3000, 1, out var vertices, out var triangles);
        var bvh = new TriangleBvh(vertices, triangles);

        ExpectSameAsBruteForce(bvh, vertices, triangles, 2000, 2);
    }

    [Fact]
    public void Raycast_MatchesBruteForceAfterRefit()
    {
        RandomSoup(3000, 3, out var vertices, out var triangles);
        var bvh = new TriangleBvh(vertices, triangles);

        // 木を作った後で、頂点を大きく動かす（形が変わっても、箱を更新すれば結果は総当たりと同じ）
        var random = new Random(4);
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] += new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.3f;
        }
        bvh.Refit(vertices);

        ExpectSameAsBruteForce(bvh, vertices, triangles, 2000, 5);
    }

    [Fact]
    public void Raycast_HandlesAxisAlignedRaysThroughGrid()
    {
        ProceduralMeshes.Grid(20, 20, 1f, 1f, out var vertices, out var triangles);
        var bvh = new TriangleBvh(vertices, triangles);

        // 軸に平行で、箱の面や三角形の辺の上を通るレイ（0 で割る・辺すれすれの取りこぼしが起きやすい）
        Assert.True(bvh.Raycast(new Vector3(0.5f, 0.5f, 2f), new Vector3(0f, 0f, -1f), vertices, out float distance, out _));
        Assert.Equal(2f, distance, 1e-5f);
        Assert.True(bvh.Raycast(new Vector3(0.25f, 0.25f, -1f), new Vector3(0f, 0f, 1f), vertices, out distance, out _));
        Assert.Equal(1f, distance, 1e-5f);
        // 布の面に沿うレイは当たらない
        Assert.False(bvh.Raycast(new Vector3(-1f, 0.5f, 0f), new Vector3(1f, 0f, 0f), vertices, out _, out _));
        // 後ろ向きは当たらない
        Assert.False(bvh.Raycast(new Vector3(0.5f, 0.5f, 2f), new Vector3(0f, 0f, 1f), vertices, out _, out _));
    }

    [Fact]
    public void Raycast_ReturnsNearestOfOverlappingLayers()
    {
        // 3mm 離した2枚の布。手前の層に当たること
        ProceduralMeshes.Grid(4, 4, 1f, 1f, out var a, out var triA);
        var vertices = new Vector3[a.Length * 2];
        var triangles = new int[triA.Length * 2];
        for (int i = 0; i < a.Length; i++)
        {
            vertices[i] = a[i];
            vertices[a.Length + i] = a[i] + new Vector3(0f, 0f, 0.003f);
        }
        for (int i = 0; i < triA.Length; i++)
        {
            triangles[i] = triA[i];
            triangles[triA.Length + i] = triA[i] + a.Length;
        }
        var bvh = new TriangleBvh(vertices, triangles);

        Assert.True(bvh.Raycast(new Vector3(0.3f, 0.4f, 1f), new Vector3(0f, 0f, -1f), vertices, out float distance, out int triangle));
        Assert.Equal(1f - 0.003f, distance, 1e-5f);
        Assert.True(triangle >= triA.Length / 3);
    }

    [Fact]
    public void Raycast_EmptyAndSingle()
    {
        var empty = new TriangleBvh(new Vector3[0], new int[0]);
        Assert.False(empty.Raycast(Vector3.Zero, Vector3.UnitZ, new Vector3[0], out _, out _));

        var vertices = new[] { new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(0f, 1f, 1f) };
        var single = new TriangleBvh(vertices, new[] { 0, 1, 2 });
        Assert.True(single.Raycast(new Vector3(0.2f, 0.2f, 0f), Vector3.UnitZ, vertices, out float distance, out int triangle));
        Assert.Equal(1f, distance, 1e-5f);
        Assert.Equal(0, triangle);
    }

    [Fact]
    public void Raycast_IgnoresZeroAreaTriangles()
    {
        // 一直線上の三角形と、3頂点が同じ位置の三角形
        var vertices = new[]
        {
            new Vector3(0f, 0f, 1f), new Vector3(0.5f, 0f, 1f), new Vector3(1f, 0f, 1f),
            new Vector3(0.3f, 0.3f, 1f), new Vector3(0.3f, 0.3f, 1f), new Vector3(0.3f, 0.3f, 1f),
        };
        var bvh = new TriangleBvh(vertices, new[] { 0, 1, 2, 3, 4, 5 });

        Assert.False(bvh.Raycast(new Vector3(0.3f, 0.3f, 0f), Vector3.UnitZ, vertices, out _, out _));
        Assert.False(bvh.Raycast(new Vector3(0.5f, 0f, 0f), Vector3.UnitZ, vertices, out _, out _));
    }
}
