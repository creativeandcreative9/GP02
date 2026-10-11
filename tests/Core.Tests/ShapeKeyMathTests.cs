using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ShapeKeyMathTests
{
    const int Columns = 20, Rows = 10;
    const float Width = 0.4f, Height = 0.2f;
    const float Center = Width / 2f;

    static void Grid(out Vector3[] vertices, out int[] triangles) =>
        ProceduralMeshes.Grid(Columns, Rows, Width, Height, out vertices, out triangles);

    // 頂点ごとに違う、なめらかでない差分（左右どちらにも値がある）
    static Vector3[] SampleDeltas(Vector3[] vertices)
    {
        var deltas = new Vector3[vertices.Length];
        for (int i = 0; i < deltas.Length; i++)
        {
            Vector3 p = vertices[i];
            deltas[i] = new Vector3(0.01f * MathF.Sin(31f * p.Y) + 0.002f * i % 3, 0.02f * p.X - 0.003f * (i % 5), 0.005f * MathF.Cos(17f * p.X) + 0.001f * p.Y);
        }
        return deltas;
    }

    static void AssertClose(Vector3[] expected, Vector3[] actual, float tolerance, string what = "")
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Vector3.Distance(expected[i], actual[i]) <= tolerance, $"{what} 頂点 {i}: {expected[i]} と {actual[i]}");
        }
    }

    // ---- 左右の分割 ----

    [Theory]
    [InlineData(0f, true)]
    [InlineData(0.05f, true)]
    [InlineData(0.05f, false)]
    [InlineData(1f, true)]
    public void SplitLeftRight_SumEqualsOriginal(float blendWidth, bool leftIsPositive)
    {
        Grid(out var vertices, out _);
        var deltas = SampleDeltas(vertices);

        ShapeKeyMath.SplitLeftRight(deltas, vertices, 0, Center, blendWidth, out var left, out var right, leftIsPositive);

        AssertClose(deltas, ShapeKeyMath.Add(left, right), 1e-6f);
    }

    [Fact]
    public void SplitLeftRight_FarVerticesBelongToOneSideAndCenterIsHalf()
    {
        Grid(out var vertices, out _);
        var deltas = SampleDeltas(vertices);

        ShapeKeyMath.SplitLeftRight(deltas, vertices, 0, Center, 0.1f, out var left, out var right);

        for (int i = 0; i < vertices.Length; i++)
        {
            float s = vertices[i].X - Center;
            if (s >= 0.05f + 1e-5f) // ぼかしの幅（片側 0.05）より外の +X 側は、すべて left
            {
                Assert.True(Vector3.Distance(left[i], deltas[i]) < 1e-6f, $"左 頂点 {i}");
                Assert.True(right[i].Length() < 1e-6f, $"右 頂点 {i}");
            }
            else if (s <= -0.05f - 1e-5f)
            {
                Assert.True(left[i].Length() < 1e-6f, $"左 頂点 {i}");
                Assert.True(Vector3.Distance(right[i], deltas[i]) < 1e-6f, $"右 頂点 {i}");
            }
            else if (MathF.Abs(s) < 1e-6f)
            {
                Assert.True(Vector3.Distance(left[i], deltas[i] * 0.5f) < 1e-6f, $"中央 頂点 {i}");
            }
        }
    }

    [Fact]
    public void SplitLeftRight_BlendIsMonotoneAcrossCenter()
    {
        // 一様な差分を分けると、左の割合が中央に向かって 0→1 に単調に増える
        Grid(out var vertices, out _);
        var deltas = new Vector3[vertices.Length];
        for (int i = 0; i < deltas.Length; i++) deltas[i] = new Vector3(1f, 0f, 0f);

        ShapeKeyMath.SplitLeftRight(deltas, vertices, 0, Center, 0.12f, out var left, out _);

        for (int r = 0; r < 1; r++)
        {
            float previous = -1f;
            for (int c = 0; c <= Columns; c++)
            {
                float share = left[r * (Columns + 1) + c].X;
                Assert.InRange(share, 0f, 1f);
                Assert.True(share >= previous - 1e-6f, $"列 {c}");
                previous = share;
            }
        }
    }

    [Fact]
    public void SplitLeftRight_LeftIsPositiveOptionSwapsSides()
    {
        Grid(out var vertices, out _);
        var deltas = SampleDeltas(vertices);

        ShapeKeyMath.SplitLeftRight(deltas, vertices, 0, Center, 0.05f, out var leftA, out var rightA, true);
        ShapeKeyMath.SplitLeftRight(deltas, vertices, 0, Center, 0.05f, out var leftB, out var rightB, false);

        AssertClose(leftA, rightB, 1e-6f);
        AssertClose(rightA, leftB, 1e-6f);
    }

    // ---- 鏡像 ----

    [Fact]
    public void MirrorMap_PairsSymmetricGridVertices()
    {
        Grid(out var vertices, out _);

        var map = VertexMirrorMap.Build(vertices, 0, Center, 1e-4f);

        int stride = Columns + 1;
        for (int r = 0; r <= Rows; r++)
        for (int c = 0; c <= Columns; c++)
        {
            Assert.Equal(r * stride + (Columns - c), map[r * stride + c]);
        }
    }

    [Fact]
    public void MirrorMap_NotFoundIsMinusOneAndAxisIsRespected()
    {
        // X方向に非対称な点を1つ足す
        Grid(out var vertices, out _);
        var extended = new Vector3[vertices.Length + 1];
        System.Array.Copy(vertices, extended, vertices.Length);
        extended[^1] = new Vector3(0.03f, 0.5f, 0f);

        var mapX = VertexMirrorMap.Build(extended, 0, Center, 1e-4f);
        Assert.Equal(-1, mapX[^1]);

        // Y軸で映すと、グリッドは Height の中央で対称
        var mapY = VertexMirrorMap.Build(vertices, 1, Height / 2f, 1e-4f);
        int stride = Columns + 1;
        Assert.Equal((Rows - 2) * stride + 5, mapY[2 * stride + 5]);
    }

    [Fact]
    public void MirrorMap_PrefersSelfOnCenterPlaneWithDuplicates()
    {
        // 中央の列に、同じ位置の頂点が2つ重なっている（UVの継ぎ目）
        ProceduralMeshes.GridWithSeam(Columns, Rows, Width, Height, out var vertices, out _);

        var map = VertexMirrorMap.Build(vertices, 0, Center, 1e-4f);

        for (int v = 0; v < vertices.Length; v++)
        {
            Assert.True(map[v] >= 0, $"頂点 {v}");
            if (MathF.Abs(vertices[v].X - Center) < 1e-6f) Assert.Equal(v, map[v]);
        }
    }

    // ---- 反転・対称化 ----

    [Fact]
    public void Flip_TwiceReturnsOriginal()
    {
        Grid(out var vertices, out _);
        var deltas = SampleDeltas(vertices);
        var map = VertexMirrorMap.Build(vertices, 0, Center, 1e-4f);

        var flipped = ShapeKeyMath.Flip(deltas, map, 0);
        var back = ShapeKeyMath.Flip(flipped, map, 0);

        AssertClose(deltas, back, 1e-7f);
        // 1回では変わっている
        Assert.True(Vector3.Distance(deltas[0], flipped[0]) > 1e-4f);
    }

    [Fact]
    public void Flip_MirrorsDirectionOfMotion()
    {
        Grid(out var vertices, out _);
        var map = VertexMirrorMap.Build(vertices, 0, Center, 1e-4f);
        var deltas = new Vector3[vertices.Length];
        deltas[0] = new Vector3(0.01f, 0.02f, 0.03f);     // 左下の隅

        var flipped = ShapeKeyMath.Flip(deltas, map, 0);

        // 右下の隅へ、X だけ逆向きになって移る
        Assert.Equal(new Vector3(-0.01f, 0.02f, 0.03f), flipped[Columns]);
        Assert.Equal(Vector3.Zero, flipped[0]);
    }

    [Fact]
    public void Flip_UnmatchedVertexGetsZero()
    {
        var vertices = new[] { new Vector3(0.1f, 0f, 0f), new Vector3(0.3f, 0f, 0f), new Vector3(0.2f, 1f, 0f) };
        var map = VertexMirrorMap.Build(vertices, 0, 0.2f, 1e-4f);
        Assert.Equal(new[] { 1, 0, 2 }, map);
        map[2] = -1;

        var flipped = ShapeKeyMath.Flip(new[] { Vector3.One, Vector3.One, Vector3.One }, map, 0);

        Assert.Equal(Vector3.Zero, flipped[2]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Symmetrize_CopiesKeptSideToOtherSide(bool keepPositive)
    {
        Grid(out var vertices, out _);
        var deltas = SampleDeltas(vertices);
        var map = VertexMirrorMap.Build(vertices, 0, Center, 1e-4f);

        var result = ShapeKeyMath.Symmetrize(deltas, vertices, map, 0, Center, keepPositive);

        for (int v = 0; v < vertices.Length; v++)
        {
            float s = vertices[v].X - Center;
            if (MathF.Abs(s) < 1e-6f)
            {
                // 中央の頂点は、自分と鏡像の平均（軸方向の動きは 0）
                Assert.True(MathF.Abs(result[v].X) < 1e-7f, $"中央 頂点 {v}");
                Assert.True(MathF.Abs(result[v].Y - deltas[v].Y) < 1e-7f, $"中央 頂点 {v}");
                continue;
            }
            bool kept = keepPositive ? s > 0 : s < 0;
            if (kept)
            {
                Assert.Equal(deltas[v], result[v]);
            }
            else
            {
                var m = deltas[map[v]];
                Assert.True(Vector3.Distance(new Vector3(-m.X, m.Y, m.Z), result[v]) < 1e-7f, $"頂点 {v}");
            }
        }
        // 結果は左右対称（反転しても同じ）
        AssertClose(result, ShapeKeyMath.Flip(result, map, 0), 1e-6f);
    }

    // ---- 足し算など ----

    [Fact]
    public void AddSubtractScale_Basics()
    {
        var a = new[] { new Vector3(1, 2, 3), new Vector3(0, 0, 1) };
        var b = new[] { new Vector3(1, 1, 1), new Vector3(2, 0, 0) };

        Assert.Equal(new[] { new Vector3(1.5f, 2.5f, 3.5f), new Vector3(1, 0, 1) }, ShapeKeyMath.Add(a, b, 0.5f));
        Assert.Equal(new[] { new Vector3(0, 1, 2), new Vector3(-2, 0, 1) }, ShapeKeyMath.Subtract(a, b));
        Assert.Equal(new[] { new Vector3(2, 4, 6), new Vector3(0, 0, 2) }, ShapeKeyMath.Scale(a, 2f));
        Assert.Equal(new Vector3(1, 2, 3), a[0]);   // 入力は変わらない
        Assert.Throws<ArgumentException>(() => ShapeKeyMath.Add(a, new Vector3[3]));
    }

    // ---- in-between ----

    [Fact]
    public void InBetween_ProgressiveIsProportionalPlusOwnDelta()
    {
        var final = new[] { new Vector3(0, 0, 1), new Vector3(0, 2, 0) };
        var own30 = new[] { new Vector3(0.1f, 0, 0), Vector3.Zero };
        var own60 = new[] { Vector3.Zero, new Vector3(0, -0.5f, 0) };

        var frames = ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive, final, new[] { 30f, 60f }, new[] { own30, own60 });

        Assert.Equal(3, frames.Length);
        Assert.Equal(new[] { 30f, 60f, 100f }, frames.Select(f => f.Weight).ToArray());
        AssertClose(new[] { new Vector3(0.1f, 0, 0.3f), new Vector3(0, 0.6f, 0) }, frames[0].Deltas, 1e-6f);
        AssertClose(new[] { new Vector3(0, 0, 0.6f), new Vector3(0, 0.7f, 0) }, frames[1].Deltas, 1e-6f);
        AssertClose(final, frames[2].Deltas, 0f);
        // 最終形の配列は共有しない
        Assert.NotSame(final, frames[2].Deltas);
    }

    [Fact]
    public void InBetween_ProgressiveWithZeroOwnDeltaEqualsLinearShape()
    {
        var final = new[] { new Vector3(0, 0, 1), new Vector3(0, 2, 0) };
        var zero = new Vector3[2];

        var frames = ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive, final, new[] { 25f, 50f }, new[] { zero, zero });

        var at40 = ShapeKeyMath.EvaluateFrames(frames, 40f, 2);
        AssertClose(ShapeKeyMath.Scale(final, 0.4f), at40, 1e-6f);
    }

    [Fact]
    public void InBetween_CrossfadeKeepsFramesAsGivenAndBlendsLinearly()
    {
        var final = new[] { new Vector3(0, 0, 1) };
        var a = new[] { new Vector3(1, 0, 0) };
        var b = new[] { new Vector3(0, 1, 0) };

        var frames = ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Crossfade, final, new[] { 20f, 60f }, new[] { a, b });

        AssertClose(a, frames[0].Deltas, 0f);
        AssertClose(b, frames[1].Deltas, 0f);
        AssertClose(final, frames[2].Deltas, 0f);
        // 20 と 60 の真ん中（40）は a と b の平均
        AssertClose(new[] { new Vector3(0.5f, 0.5f, 0) }, ShapeKeyMath.EvaluateFrames(frames, 40f, 1), 1e-6f);
        // 60 と 100 の間
        AssertClose(new[] { new Vector3(0, 0.5f, 0.5f) }, ShapeKeyMath.EvaluateFrames(frames, 80f, 1), 1e-6f);
        // 最初のフレームの手前は 0 からの線形
        AssertClose(new[] { new Vector3(0.5f, 0, 0) }, ShapeKeyMath.EvaluateFrames(frames, 10f, 1), 1e-6f);
    }

    [Fact]
    public void InBetween_RejectsBadWeights()
    {
        var final = new Vector3[1];
        var one = new[] { new Vector3[1] };
        Assert.Throws<ArgumentException>(() => ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive, final, new[] { 0f }, one));
        Assert.Throws<ArgumentException>(() => ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive, final, new[] { 100f }, one));
        Assert.Throws<ArgumentException>(() => ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive, final, new[] { 50f, 40f }, new[] { final, final }));
        Assert.Throws<ArgumentException>(() => ShapeKeyMath.MakeInBetweenFrames(InBetweenMode.Progressive, final, new[] { 50f }, new[] { new Vector3[2] }));
    }

    [Fact]
    public void Sequential_ProgressiveAccumulatesAndCrossfadeKeepsUnits()
    {
        var u1 = new[] { new Vector3(1, 0, 0) };
        var u2 = new[] { new Vector3(0, 1, 0) };
        var u3 = new[] { new Vector3(0, 0, 1) };
        var weights = new[] { 30f, 70f, 100f };

        var progressive = ShapeKeyMath.MakeSequentialFrames(InBetweenMode.Progressive, weights, new[] { u1, u2, u3 });
        var crossfade = ShapeKeyMath.MakeSequentialFrames(InBetweenMode.Crossfade, weights, new[] { u1, u2, u3 });

        AssertClose(u1, progressive[0].Deltas, 0f);
        AssertClose(new[] { new Vector3(1, 1, 0) }, progressive[1].Deltas, 0f);
        AssertClose(new[] { new Vector3(1, 1, 1) }, progressive[2].Deltas, 0f);
        AssertClose(u1, crossfade[0].Deltas, 0f);
        AssertClose(u2, crossfade[1].Deltas, 0f);
        AssertClose(u3, crossfade[2].Deltas, 0f);
        // Progressive は、部品1 の途中（15）で部品1 が半分
        AssertClose(new[] { new Vector3(0.5f, 0, 0) }, ShapeKeyMath.EvaluateFrames(progressive, 15f, 1), 1e-6f);
        Assert.Throws<ArgumentException>(() => ShapeKeyMath.MakeSequentialFrames(InBetweenMode.Progressive, new[] { 30f, 90f }, new[] { u1, u2 }));
    }

    // ---- 法線 ----

    [Fact]
    public void RecomputeNormalDeltas_ZeroForUnmovedMesh()
    {
        Grid(out var vertices, out var triangles);
        var restNormals = new Vector3[vertices.Length];
        MeshNormals.Compute(vertices, triangles, restNormals);

        ShapeKeyMath.RecomputeNormalDeltas(vertices, triangles, new Vector3[vertices.Length], restNormals, out var normalDeltas);

        foreach (var d in normalDeltas) Assert.True(d.Length() < 1e-6f);
    }

    [Fact]
    public void RecomputeNormalDeltas_BentPlaneTurnsNormalsTowardTheBend()
    {
        Grid(out var vertices, out var triangles);
        var restNormals = new Vector3[vertices.Length];
        MeshNormals.Compute(vertices, triangles, restNormals);
        Assert.True(restNormals[0].Z < -0.99f);   // 平面の法線は -Z

        // 中央より右を、中央の縦線のまわりに 30 度回して折り曲げる
        const float angle = MathF.PI / 6f;
        var deltas = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            float x = vertices[i].X - Center;
            if (x <= 0f) continue;
            // Y軸まわりの回転（x' = x cos + z sin, z' = -x sin + z cos）で、z = 0 の点は (x cos, -x sin)
            deltas[i] = new Vector3(x * MathF.Cos(angle) - x, 0f, -x * MathF.Sin(angle));
        }

        ShapeKeyMath.RecomputeNormalDeltas(vertices, triangles, deltas, restNormals, out var normalDeltas);

        // 折り目から離れた右端の頂点は、法線が (0,0,-1) → (-sin, 0, -cos) に回る
        int stride = Columns + 1;
        int rightEdge = 5 * stride + Columns;
        var expected = new Vector3(-MathF.Sin(angle), 0f, 1f - MathF.Cos(angle));
        Assert.True(Vector3.Distance(expected, normalDeltas[rightEdge]) < 0.05f, $"{normalDeltas[rightEdge]}");
        // 左端はそのまま
        Assert.True(normalDeltas[5 * stride].Length() < 1e-5f);
        // 足し直した法線は、向きが折り曲げた側（-X 寄り）を向く
        Vector3 bent = Vector3.Normalize(restNormals[rightEdge] + normalDeltas[rightEdge]);
        Assert.True(bent.X < -0.4f && bent.Z < -0.8f);
    }

    [Fact]
    public void RecomputeNormalDeltas_DoesNotMergeSplitVertices()
    {
        // 継ぎ目で頂点が分かれている平面を曲げる。継ぎ目の2つの頂点の法線の差分は、それぞれの側の面だけで決まる
        ProceduralMeshes.GridWithSeam(Columns, Rows, Width, Height, out var vertices, out var triangles);
        var restNormals = new Vector3[vertices.Length];
        MeshNormals.Compute(vertices, triangles, restNormals);

        var deltas = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            float x = vertices[i].X - Center;
            if (x > 0f) deltas[i] = new Vector3(0f, 0f, -x * 0.5f);
        }
        ShapeKeyMath.RecomputeNormalDeltas(vertices, triangles, deltas, restNormals, out var normalDeltas);

        int stride = Columns + 1;
        int seamOriginal = 5 * stride + Columns / 2;
        int seamDuplicate = vertices.Length - (Rows + 1) + 5;
        Assert.True(Vector3.Distance(vertices[seamOriginal], vertices[seamDuplicate]) < 1e-7f);
        // 片側（動かない側）の頂点の差分は 0、もう片側は傾いている
        Assert.True(normalDeltas[seamOriginal].Length() < 1e-5f);
        Assert.True(normalDeltas[seamDuplicate].Length() > 0.05f);
    }

    [Fact]
    public void RecomputeNormalDeltas_VertexWithoutFaceGetsZero()
    {
        Grid(out var vertices, out var triangles);
        var extended = new Vector3[vertices.Length + 1];
        System.Array.Copy(vertices, extended, vertices.Length);
        extended[^1] = new Vector3(5f, 5f, 5f);
        var deltas = new Vector3[extended.Length];
        deltas[^1] = Vector3.One;
        var restNormals = new Vector3[extended.Length];
        MeshNormals.Compute(extended, triangles, restNormals);

        ShapeKeyMath.RecomputeNormalDeltas(extended, triangles, deltas, restNormals, out var normalDeltas);

        Assert.Equal(Vector3.Zero, normalDeltas[^1]);
    }
}
