using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ShapeMaskTests
{
    static Vector3[] Ramp(int count)
    {
        var d = new Vector3[count];
        for (int i = 0; i < count; i++) d[i] = new Vector3(0.001f * (i % 5), 0.002f * (i % 3), 0.003f * (1 + i % 4));
        return d;
    }

    [Fact]
    public void MultiplyAndComplement_AddBackToTheOriginal()
    {
        Vector3[] deltas = Ramp(10);
        var mask = new float[] { 0, 1, 0.5f, 0.25f, 0.75f, 1, 0, 0.1f, 0.9f, 0.3f };

        Vector3[] inside = ShapeMask.Multiply(deltas, mask);
        Vector3[] outside = ShapeMask.MultiplyComplement(deltas, mask);

        for (int i = 0; i < deltas.Length; i++)
        {
            Assert.True(Vector3.Distance(inside[i] + outside[i], deltas[i]) < 1e-9f, $"頂点 {i}");
            Assert.True(Vector3.Distance(inside[i], deltas[i] * mask[i]) < 1e-9f);
        }
        Assert.Throws<ArgumentException>(() => ShapeMask.Multiply(deltas, new float[3]));
    }

    [Fact]
    public void PlaneMask_MatchesTheAxisSplitOfShapeKeyMath()
    {
        ProceduralMeshes.Grid(10, 6, 0.4f, 0.2f, out var vertices, out _);
        float center = 0.17f, width = 0.06f;

        float[] mask = ShapeMask.PlaneMask(vertices, Vector3.UnitX, center, width);
        ShapeKeyMath.SplitLeftRight(Ramp(vertices.Length), vertices, 0, center, width, out var left, out _);
        Vector3[] viaMask = ShapeMask.Multiply(Ramp(vertices.Length), mask);

        for (int i = 0; i < vertices.Length; i++) Assert.True(Vector3.Distance(viaMask[i], left[i]) < 1e-7f, $"頂点 {i}");
    }

    [Fact]
    public void PlaneMask_WorksForATiltedPlaneAndAHardEdge()
    {
        var positions = new[] { new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0.5f, 0.5f, 0) };
        // 法線 (1,1,0)、原点を通る
        float[] mask = ShapeMask.PlaneMask(positions, new Vector3(1, 1, 0), 0f, 0f);

        Assert.Equal(1f, mask[0]);
        Assert.Equal(0f, mask[1]);
        Assert.Equal(1f, mask[2]);
        Assert.Equal(1f, mask[3]);
        Assert.Throws<ArgumentException>(() => ShapeMask.PlaneMask(positions, Vector3.Zero, 0f, 0.1f));
    }

    [Fact]
    public void Smooth_SpreadsASharpEdgeButKeepsTheRangeAndSeams()
    {
        ProceduralMeshes.GridWithSeam(10, 6, 0.4f, 0.24f, out var vertices, out var triangles);
        var mask = new float[vertices.Length];
        for (int i = 0; i < vertices.Length; i++) mask[i] = vertices[i].X > 0.2f ? 1f : 0f;

        float[] smoothed = ShapeMask.Smooth(mask, vertices, triangles, 3);

        bool anyMiddle = false;
        for (int i = 0; i < smoothed.Length; i++)
        {
            Assert.InRange(smoothed[i], 0f, 1f);
            if (smoothed[i] > 0.05f && smoothed[i] < 0.95f) anyMiddle = true;
        }
        Assert.True(anyMiddle, "境目に途中の値ができる");
        // 同じ位置の頂点（継ぎ目）は同じ値
        for (int a = 0; a < vertices.Length; a++)
        for (int b = a + 1; b < vertices.Length; b++)
        {
            if (Vector3.Distance(vertices[a], vertices[b]) < 1e-7f) Assert.Equal(smoothed[a], smoothed[b], 6);
        }
        // 遠くの値は変わらない（3 回の広がりは 3 マス = 12cm 以内）
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i].X > 0.37f) Assert.Equal(1f, smoothed[i], 5);
            if (vertices[i].X < 0.03f) Assert.Equal(0f, smoothed[i], 5);
        }
        // 0 回なら何も変わらない
        Assert.Equal(mask, ShapeMask.Smooth(mask, vertices, triangles, 0));
    }

    [Fact]
    public void ParticleCloth_AverageOverBinding_GivesOneForAFullySelectedClothAndPartialNearTheEdge()
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        int n = cloth.Model.ParticleCount;
        var values = new float[n];
        var welded = new float[cloth.Topology.Positions.Length];

        for (int i = 0; i < n; i++) values[i] = 1f;
        cloth.AverageOverBinding(values, welded);
        foreach (float w in welded) Assert.Equal(1f, w, 5);

        // X < 0.15 の粒子だけ選ぶ
        for (int i = 0; i < n; i++) values[i] = cloth.Model.RestPositions[i].X < 0.15f ? 1f : 0f;
        cloth.AverageOverBinding(values, welded);
        bool anyPartial = false;
        for (int w = 0; w < welded.Length; w++)
        {
            float x = cloth.Topology.Positions[w].X;
            Assert.InRange(welded[w], 0f, 1f);
            if (x < 0.1f) Assert.Equal(1f, welded[w], 5);
            if (x > 0.2f) Assert.Equal(0f, welded[w], 5);
            if (welded[w] > 0.01f && welded[w] < 0.99f) anyPartial = true;
        }
        Assert.True(anyPartial, "境目の頂点は途中の値になる");
        Assert.Throws<ArgumentException>(() => cloth.AverageOverBinding(new float[3], welded));
    }
}
