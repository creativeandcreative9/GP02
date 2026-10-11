using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ShapeTransferTests
{
    // 板の上のなめらかな差分（位置の関数）。写した結果をこれと比べる
    static Vector3 Field(Vector3 p) => new Vector3(0.01f * MathF.Sin(4f * p.Y), 0.005f * p.X, 0.03f * MathF.Sin(3f * p.X) * MathF.Cos(2f * p.Y));

    static Vector3[] Sample(Vector3[] positions)
    {
        var d = new Vector3[positions.Length];
        for (int i = 0; i < d.Length; i++) d[i] = Field(positions[i]);
        return d;
    }

    static float MaxError(Vector3[] actual, Vector3[] expected)
    {
        float max = 0f;
        for (int i = 0; i < actual.Length; i++) max = MathF.Max(max, Vector3.Distance(actual[i], expected[i]));
        return max;
    }

    static float MaxMagnitude(Vector3[] values)
    {
        float max = 0f;
        foreach (Vector3 v in values) max = MathF.Max(max, v.Length());
        return max;
    }

    [Fact]
    public void SameShape_TransfersTheDeltasExactly()
    {
        ProceduralMeshes.Grid(12, 12, 0.3f, 0.3f, out var vertices, out var triangles);
        Vector3[] deltas = Sample(vertices);

        var map = ShapeTransfer.Map(vertices, triangles, vertices, null, triangles, 0.01f);
        Vector3[] result = ShapeTransfer.Apply(map, deltas);

        Assert.Equal(0, map.MissCount);
        Assert.True(MaxError(result, deltas) < 1e-6f, $"誤差 {MaxError(result, deltas)}");
    }

    [Fact]
    public void FinerMesh_GetsNearlyTheSameDeltasAsTheFieldItself()
    {
        ProceduralMeshes.Grid(12, 12, 0.3f, 0.3f, out var coarse, out var coarseTriangles);
        ProceduralMeshes.Grid(37, 29, 0.3f, 0.3f, out var fine, out var fineTriangles);
        Vector3[] coarseDeltas = Sample(coarse);

        var map = ShapeTransfer.Map(coarse, coarseTriangles, fine, null, fineTriangles, 0.01f);
        Vector3[] result = ShapeTransfer.Apply(map, coarseDeltas);

        Assert.Equal(0, map.MissCount);
        float error = MaxError(result, Sample(fine));
        // 粗い側の線形補間の誤差だけ（差分の最大 約3cm に対して 1 割以内）
        Assert.True(error < 0.1f * MaxMagnitude(coarseDeltas), $"誤差 {error}");
    }

    [Fact]
    public void CoarserMesh_GetsNearlyTheSameDeltasAsTheFieldItself()
    {
        ProceduralMeshes.Grid(48, 48, 0.3f, 0.3f, out var fine, out var fineTriangles);
        ProceduralMeshes.Grid(9, 9, 0.3f, 0.3f, out var coarse, out var coarseTriangles);
        Vector3[] fineDeltas = Sample(fine);

        var map = ShapeTransfer.Map(fine, fineTriangles, coarse, null, coarseTriangles, 0.01f);
        Vector3[] result = ShapeTransfer.Apply(map, fineDeltas);

        Assert.Equal(0, map.MissCount);
        float error = MaxError(result, Sample(coarse));
        Assert.True(error < 0.02f * MaxMagnitude(fineDeltas), $"誤差 {error}");
    }

    [Fact]
    public void VerticesFartherThanTheLimitGetZero()
    {
        ProceduralMeshes.Grid(10, 10, 0.2f, 0.2f, out var source, out var sourceTriangles);
        Vector3[] deltas = Sample(source);
        // 写し先: 板の 5mm 上、5cm 上、板の上
        var target = new[] { new Vector3(0.05f, 0.05f, 0.005f), new Vector3(0.1f, 0.1f, 0.05f), new Vector3(0.15f, 0.12f, 0f) };

        var map = ShapeTransfer.Map(source, sourceTriangles, target, new[] { -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ }, null, 0.01f);
        Vector3[] result = ShapeTransfer.Apply(map, deltas);

        Assert.Equal(1, map.MissCount);
        Assert.Equal(-1, map.Triangles[1]);
        Assert.Equal(Vector3.Zero, result[1]);
        Assert.True(Vector3.Distance(result[0], Field(new Vector3(0.05f, 0.05f, 0f))) < 0.1f * MaxMagnitude(deltas));
        Assert.True(result[2] != Vector3.Zero);
    }

    [Fact]
    public void FacesPointingTheOtherWayAreAvoided()
    {
        // 表向きの板 (z = 0) と、少し離れた逆向きの板 (z = 0.004) の 2 枚。差分は表の板だけ +X に 1cm
        ProceduralMeshes.Grid(4, 4, 0.2f, 0.2f, out var front, out var frontTriangles);
        var vertices = new List<Vector3>(front);
        var triangles = new List<int>(frontTriangles);
        int offset = vertices.Count;
        foreach (Vector3 p in front) vertices.Add(new Vector3(p.X, p.Y, 0.004f));
        for (int t = 0; t < frontTriangles.Length; t += 3)
        {
            // 向きを逆にする
            triangles.Add(frontTriangles[t] + offset);
            triangles.Add(frontTriangles[t + 2] + offset);
            triangles.Add(frontTriangles[t + 1] + offset);
        }
        var deltas = new Vector3[vertices.Count];
        for (int i = 0; i < front.Length; i++) deltas[i] = new Vector3(0.01f, 0f, 0f);

        // 写し先の頂点は、裏向きの板に近い (z = 0.003)。法線は表の板と同じ向き（Grid の面は -Z 向き）
        var target = new[] { new Vector3(0.1f, 0.1f, 0.003f) };
        var withNormal = ShapeTransfer.Map(vertices.ToArray(), triangles.ToArray(), target, new[] { -Vector3.UnitZ }, null, 0.01f);
        Assert.Equal(0.01f, ShapeTransfer.Apply(withNormal, deltas)[0].X, 5);

        var ignoringNormal = ShapeTransfer.Map(vertices.ToArray(), triangles.ToArray(), target, new[] { -Vector3.UnitZ }, null, 0.01f, -1f);
        Assert.Equal(0f, ShapeTransfer.Apply(ignoringNormal, deltas)[0].X, 5);
    }

    [Fact]
    public void NormalsAreComputedFromTheTargetTrianglesWhenNotGiven()
    {
        ProceduralMeshes.Grid(6, 6, 0.2f, 0.2f, out var source, out var sourceTriangles);
        ProceduralMeshes.Grid(9, 9, 0.2f, 0.2f, out var target, out var targetTriangles);
        Vector3[] deltas = Sample(source);

        var map = ShapeTransfer.Map(source, sourceTriangles, target, null, targetTriangles, 0.01f);

        Assert.Equal(0, map.MissCount);
        Assert.Throws<ArgumentException>(() => ShapeTransfer.Map(source, sourceTriangles, target, null, null, 0.01f));
        Assert.Equal(target.Length, ShapeTransfer.Apply(map, deltas).Length);
    }
}
