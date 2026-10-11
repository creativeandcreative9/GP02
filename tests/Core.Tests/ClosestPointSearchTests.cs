using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ClosestPointSearchTests
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
                    (float)random.NextDouble() - 0.5f) * 0.2f;
                triangles[3 * t + k] = 3 * t + k;
            }
        }
    }

    [Fact]
    public void ClosestOnTriangle_CoversInsideEdgesAndVertices()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(1, 0, 0);
        var c = new Vector3(0, 1, 0);

        // 面の真上: 垂線の足
        Vector3 inside = ClosestPointSearch.ClosestOnTriangle(new Vector3(0.25f, 0.25f, 0.5f), a, b, c, out Vector3 bary);
        Assert.True(Vector3.Distance(inside, new Vector3(0.25f, 0.25f, 0f)) < 1e-6f);
        Assert.True(Math.Abs(bary.X + bary.Y + bary.Z - 1f) < 1e-6f);
        Assert.True(Vector3.Distance(bary.X * a + bary.Y * b + bary.Z * c, inside) < 1e-6f);

        // 頂点の外: その頂点
        Vector3 corner = ClosestPointSearch.ClosestOnTriangle(new Vector3(2f, -1f, 0f), a, b, c, out bary);
        Assert.True(Vector3.Distance(corner, b) < 1e-6f, "頂点 b が一番近い");
        Assert.True(Vector3.Distance(bary, new Vector3(0, 1, 0)) < 1e-6f);

        // 辺の外: 辺の上の点
        Vector3 onAb = ClosestPointSearch.ClosestOnTriangle(new Vector3(0.5f, -2f, 0f), a, b, c, out bary);
        Assert.True(Vector3.Distance(onAb, new Vector3(0.5f, 0f, 0f)) < 1e-6f);
        Assert.True(Math.Abs(bary.Z) < 1e-6f);

        // 面積 0 の三角形でも、三角形の上の点を返す
        Vector3 flat = ClosestPointSearch.ClosestOnTriangle(new Vector3(0.4f, 1f, 0f), a, b, new Vector3(2f, 0f, 0f), out bary);
        Assert.True(Math.Abs(flat.Y) < 1e-6f && Math.Abs(flat.Z) < 1e-6f);
        Assert.True(Math.Abs(bary.X + bary.Y + bary.Z - 1f) < 1e-5f);
    }

    [Fact]
    public void FindClosest_MatchesBruteForceOnRandomTriangles()
    {
        RandomSoup(600, 7, out var vertices, out var triangles);
        var search = new ClosestPointSearch(vertices, triangles);
        var random = new Random(11);
        for (int i = 0; i < 300; i++)
        {
            var p = new Vector3((float)random.NextDouble() * 1.4f - 0.2f, (float)random.NextDouble() * 1.4f - 0.2f, (float)random.NextDouble() * 1.4f - 0.2f);

            float best = float.MaxValue;
            for (int t = 0; t < triangles.Length / 3; t++)
            {
                Vector3 q = ClosestPointSearch.ClosestOnTriangle(p, vertices[triangles[3 * t]], vertices[triangles[3 * t + 1]], vertices[triangles[3 * t + 2]], out _);
                best = MathF.Min(best, Vector3.Distance(p, q));
            }

            Assert.True(search.TryFindClosest(p, 10f, out ClosestPointSearch.Hit hit));
            Assert.True(Math.Abs(hit.Distance - best) < 1e-5f, $"{i}: 木 {hit.Distance} / 総当たり {best}");
            Assert.True(Math.Abs(Vector3.Distance(hit.Point, p) - hit.Distance) < 1e-5f);
        }
    }

    [Fact]
    public void FindClosest_RespectsMaxDistance()
    {
        ProceduralMeshes.Grid(4, 4, 1f, 1f, out var vertices, out var triangles);
        var search = new ClosestPointSearch(vertices, triangles);

        Assert.True(search.TryFindClosest(new Vector3(0.5f, 0.5f, 0.1f), 0.2f, out ClosestPointSearch.Hit near));
        Assert.True(Math.Abs(near.Distance - 0.1f) < 1e-5f);
        Assert.False(search.TryFindClosest(new Vector3(0.5f, 0.5f, 0.3f), 0.2f, out _), "上限より遠いと見つからない");
        Assert.False(new ClosestPointSearch(new Vector3[0], new int[0]).TryFindClosest(Vector3.Zero, 1f, out _), "三角形が無い");
    }

    [Fact]
    public void FindClosest_WithNormal_AvoidsFacesPointingTheOtherWay()
    {
        // 同じ位置に、表向き（+Z）の面と裏向き（-Z）の面が重なっている（後の面は少し手前）
        var vertices = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0, 0, 0.05f), new Vector3(0, 1, 0.05f), new Vector3(1, 0, 0.05f),
        };
        var triangles = new[] { 0, 1, 2, 3, 4, 5 };   // 前の三角形は +Z 向き、後の三角形は -Z 向き（頂点の並びが逆）
        var search = new ClosestPointSearch(vertices, triangles);

        var p = new Vector3(0.2f, 0.2f, 0.04f);
        Assert.True(search.TryFindClosest(p, 1f, out ClosestPointSearch.Hit any));
        Assert.Equal(1, any.Triangle);   // 向きを問わなければ、近い裏向きの面

        Assert.True(search.TryFindClosest(p, 1f, new Vector3(0, 0, 1), 0f, out ClosestPointSearch.Hit front));
        Assert.Equal(0, front.Triangle);   // +Z 向きの法線なら、遠くても表向きの面

        Assert.True(search.TryFindClosest(p, 1f, new Vector3(0, 0, -1), 0f, out ClosestPointSearch.Hit back));
        Assert.Equal(1, back.Triangle);

        // 表向きの面だけが範囲にあるとき、裏向きの法線の頂点は見つからない
        Assert.False(new ClosestPointSearch(vertices, new[] { 0, 1, 2 }).TryFindClosest(p, 1f, new Vector3(0, 0, -1), 0f, out _));
    }
}
