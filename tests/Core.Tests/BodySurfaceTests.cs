using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>体の表面（BodySurface）の符号付き距離・一番近い点・法線と、島の扱いのテスト。</summary>
public class BodySurfaceTests
{
    // ---- テスト用の形 ----

    /// <summary>
    /// 閉じた回転体。rings は上から下へ並べた (高さ, 半径) で、上下の端に極（半径 0 の点）を付ける。
    /// radius で、角度と高さごとに半径を変えられる（こぶなど）。外向きが表になる巻き順（テスト用の衣装と同じ）。
    /// seam なら、輪の最初と最後に同じ位置の頂点を置き、極も区切りごとに別の頂点にする（Unity の UV の継ぎ目のように）。
    /// </summary>
    internal static void Lathe(List<Vector3> v, List<int> t, float centerX, float centerZ, float topY, float bottomY, int rows, int segments,
        Func<float, float, float> radius, bool seam = false)
    {
        int ring = seam ? segments + 1 : segments;
        int first = v.Count;
        // 上の極
        int topPole = v.Count;
        if (seam) for (int i = 0; i < segments; i++) v.Add(new Vector3(centerX, topY, centerZ));
        else v.Add(new Vector3(centerX, topY, centerZ));
        int ringStart = v.Count;
        for (int k = 0; k <= rows; k++)
        {
            float y = topY + (bottomY - topY) * k / rows;
            for (int i = 0; i < ring; i++)
            {
                float angle = 2f * MathF.PI * (i % segments) / segments;
                float r = radius(y, angle);
                v.Add(new Vector3(centerX + r * MathF.Cos(angle), y, centerZ + r * MathF.Sin(angle)));
            }
        }
        int bottomPole = v.Count;
        if (seam) for (int i = 0; i < segments; i++) v.Add(new Vector3(centerX, bottomY, centerZ));
        else v.Add(new Vector3(centerX, bottomY, centerZ));

        int Ring(int k, int i) => ringStart + k * ring + (seam ? i : i % segments);
        for (int i = 0; i < segments; i++)
        {
            int pole = seam ? topPole + i : topPole;
            t.AddRange(new[] { pole, Ring(0, i + 1), Ring(0, i) });
        }
        for (int k = 0; k < rows; k++)
        {
            for (int i = 0; i < segments; i++)
            {
                int a = Ring(k, i), b = Ring(k, i + 1), c = Ring(k + 1, i), d = Ring(k + 1, i + 1);
                t.AddRange(new[] { a, b, c, b, d, c });
            }
        }
        for (int i = 0; i < segments; i++)
        {
            int pole = seam ? bottomPole + i : bottomPole;
            t.AddRange(new[] { Ring(rows, i), Ring(rows, i + 1), pole });
        }
        _ = first;
    }

    internal static BodySurface Cylinder(float radius, float topY, float bottomY, int rows = 30, int segments = 48, float centerX = 0f, float centerZ = 0f)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        Lathe(v, t, centerX, centerZ, topY, bottomY, rows, segments, (_, _) => radius);
        return new BodySurface(v.ToArray(), t.ToArray());
    }

    // 箱 [-h, h]^3 （外向きの巻き順）
    static void Box(List<Vector3> v, List<int> t, Vector3 center, float h)
    {
        int s = v.Count;
        for (int i = 0; i < 8; i++) v.Add(center + new Vector3((i & 1) != 0 ? h : -h, (i & 2) != 0 ? h : -h, (i & 4) != 0 ? h : -h));
        int[] faces =
        {
            0, 2, 3, 1,   // -Z
            4, 5, 7, 6,   // +Z
            0, 1, 5, 4,   // -Y
            2, 6, 7, 3,   // +Y
            0, 4, 6, 2,   // -X
            1, 3, 7, 5,   // +X
        };
        for (int f = 0; f < 6; f++)
        {
            int a = s + faces[4 * f], b = s + faces[4 * f + 1], c = s + faces[4 * f + 2], d = s + faces[4 * f + 3];
            t.AddRange(new[] { a, b, c, a, c, d });
        }
    }

    static BodySurface BoxSurface(float h)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        Box(v, t, Vector3.Zero, h);
        return new BodySurface(v.ToArray(), t.ToArray());
    }

    static float Signed(BodySurface body, Vector3 p, float maxDistance = 1f)
    {
        Assert.True(body.TryQuery(p, maxDistance, out BodyHit hit), $"{p} で体が見つからない");
        return hit.SignedDistance;
    }

    // ---- 箱（面・辺・頂点の近くの内外） ----

    [Fact]
    public void Box_SignedDistance_IsExactOnFacesEdgesAndCorners()
    {
        BodySurface box = BoxSurface(0.1f);
        Assert.Equal(1, box.IslandCount);
        Assert.True(box.IsIslandSolid(0));

        // 面の外・中
        Assert.Equal(0.02f, Signed(box, new Vector3(0.12f, 0.03f, -0.02f)), 4);
        Assert.Equal(-0.02f, Signed(box, new Vector3(0.08f, 0.03f, -0.02f)), 4);
        // 辺の外（斜め）と、辺のすぐ内側
        Assert.Equal(MathF.Sqrt(2f) * 0.003f, Signed(box, new Vector3(0.103f, 0.103f, 0.01f)), 4);
        Assert.Equal(-0.0005f, Signed(box, new Vector3(0.099f, 0.0995f, 0.01f)), 4);
        // 頂点の外（斜め）と、頂点のすぐ内側
        Assert.Equal(MathF.Sqrt(3f) * 0.004f, Signed(box, new Vector3(0.104f, 0.104f, 0.104f)), 4);
        Assert.True(Signed(box, new Vector3(0.0995f, 0.0995f, 0.0995f)) < 0f);
        // 頂点・辺の外側でも、少しずつずらした点の符号が反転しない
        for (int k = 0; k < 50; k++)
        {
            float a = 0.1f + 0.0001f * (k + 1);
            Assert.True(Signed(box, new Vector3(a, a * 0.999f, a * 1.001f)) > 0f, $"外側 {a}");
            float b = 0.1f - 0.0001f * (k + 1);
            Assert.True(Signed(box, new Vector3(b, b * 0.999f, 0.1f - 0.00005f)) < 0f, $"内側 {b}");
        }
    }

    [Fact]
    public void Box_ClosestPointAndNormal()
    {
        BodySurface box = BoxSurface(0.1f);
        Assert.True(box.TryQuery(new Vector3(0.15f, 0.02f, 0.03f), 1f, out BodyHit hit));
        Assert.True(Vector3.Distance(hit.Point, new Vector3(0.1f, 0.02f, 0.03f)) < 1e-5f);
        // 面の中の点では、なめらかな法線は面の向きに近い（箱は角で向きが混ざるので、+X 寄り）
        Assert.True(hit.Normal.X > 0.5f);
        Assert.True(hit.Triangle >= 0);
        Assert.Equal(0, hit.Island);
    }

    [Fact]
    public void MaxDistance_LimitsTheSearch()
    {
        BodySurface box = BoxSurface(0.1f);
        Assert.False(box.TryQuery(new Vector3(0.2f, 0f, 0f), 0.05f, out _));
        Assert.True(box.TryQuery(new Vector3(0.14f, 0f, 0f), 0.05f, out BodyHit hit));
        Assert.Equal(0.04f, hit.SignedDistance, 4);
        // 中の深い点（表面から 10cm）は、検索の距離より遠いので見つからない
        Assert.False(box.TryQuery(new Vector3(0f, 0f, 0f), 0.05f, out _));
    }

    // ---- 球（UV の継ぎ目のある多面体） ----

    [Fact]
    public void Sphere_WithUvSeams_SignedDistanceAndNormal()
    {
        const float r = 0.1f;
        var v = new List<Vector3>();
        var t = new List<int>();
        // 球: 高さ y の輪の半径 sqrt(r^2 - y^2)。継ぎ目の頂点と極は重ねて置く（まとめてから計算するので、裂け目にならない）
        Lathe(v, t, 0f, 0f, r, -r, 32, 48, (y, _) => MathF.Sqrt(MathF.Max(0f, r * r - y * y)), seam: true);
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.Equal(1, body.IslandCount);
        Assert.True(body.GetIslandCompactness(0) > 0.9f, $"球の体積と面積の比 {body.GetIslandCompactness(0)}");

        var random = new Random(7);
        int checkedPoints = 0;
        for (int k = 0; k < 400; k++)
        {
            var direction = Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
            // 輪は高さで等間隔なので、極の近くは面が粗い（球から最大 1.6mm 内側）。距離の値は、面の細かい所で比べる
            if (MathF.Abs(direction.Y) > 0.8f) direction = Vector3.Normalize(new Vector3(direction.X, direction.Y * 0.5f, direction.Z));
            float distance = r + ((float)random.NextDouble() - 0.5f) * 0.04f;   // 表面の ±2cm
            Vector3 p = direction * distance;
            Assert.True(body.TryQuery(p, 0.05f, out BodyHit hit));
            // 多面体の面は球より最大 約 0.5mm 内側
            Assert.True(MathF.Abs(hit.SignedDistance - (distance - r)) < 0.0012f, $"{p}: {hit.SignedDistance} / {distance - r}");
            Assert.True(Vector3.Dot(hit.Normal, direction) > 0.98f, "法線は外向き（球の中心から外）");
            checkedPoints++;
        }
        Assert.Equal(400, checkedPoints);
        // 継ぎ目（角度 0）の真上と、極の近く
        Assert.True(Signed(body, new Vector3(r + 0.003f, 0f, 0f)) > 0f);
        Assert.True(Signed(body, new Vector3(r - 0.003f, 0f, 0f)) < 0f);
        Assert.True(Signed(body, new Vector3(0.0001f, r + 0.002f, 0.0001f)) > 0f);
        Assert.True(Signed(body, new Vector3(0.0001f, r - 0.004f, 0.0001f)) < 0f);
    }

    // ---- 円柱（ふたの縁の辺、閉じていない筒） ----

    [Fact]
    public void Cylinder_SignedDistance_NearTheRimAndCaps()
    {
        BodySurface body = Cylinder(0.1f, 1f, 0f, 20, 64);
        // 横の外・中
        Assert.InRange(Signed(body, new Vector3(0.12f, 0.5f, 0f)), 0.019f, 0.021f);
        Assert.InRange(Signed(body, new Vector3(0.09f, 0.5f, 0f)), -0.011f, -0.009f);
        // ふたの上・下
        Assert.Equal(0.01f, Signed(body, new Vector3(0.03f, 1.01f, 0.02f)), 4);
        Assert.Equal(-0.01f, Signed(body, new Vector3(0.03f, 0.99f, 0.02f)), 4);
        // ふたの縁の外（斜め上）と、縁のすぐ内側
        Assert.True(Signed(body, new Vector3(0.101f, 1.001f, 0f)) > 0f);
        Assert.True(Signed(body, new Vector3(0.0995f, 0.9995f, 0f)) < 0f);
    }

    [Fact]
    public void OpenTube_IsUsable_AndFarPointsAreIgnored()
    {
        // ふたの無い筒（首の穴のような、閉じていない体）
        var v = new List<Vector3>();
        var t = new List<int>();
        const int segments = 48, rows = 20;
        for (int k = 0; k <= rows; k++)
        {
            float y = 1f - k / (float)rows;
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                v.Add(new Vector3(0.1f * MathF.Cos(angle), y, 0.1f * MathF.Sin(angle)));
            }
        }
        for (int k = 0; k < rows; k++)
        for (int i = 0; i < segments; i++)
        {
            int a = k * segments + i, b = k * segments + (i + 1) % segments, c = a + segments, d = b + segments;
            t.AddRange(new[] { a, b, c, b, d, c });
        }
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.True(body.IsIslandSolid(0), "閉じていない筒でも、中は決まる");
        Assert.True(Signed(body, new Vector3(0.105f, 0.5f, 0f), 0.05f) > 0f);
        Assert.True(Signed(body, new Vector3(0.095f, 0.5f, 0f), 0.05f) < 0f);
        // 筒の口の上の、離れた所は見ない
        Assert.False(body.TryQuery(new Vector3(0f, 1.2f, 0f), BodySurface.DefaultMaxDistance, out _));
    }

    [Fact]
    public void OpenFlaredSkirt_PointsBeyondTheHemAreOutside()
    {
        // 裾が広がった、ふたの無い円錐（内側の重ね着を体として使うときのペチコートのような形）。
        // 裾の下の点は、裾の縁が一番近く、縁の面の向きだけで決めると中と取り違える（裾の外側の布を押し出してしまう）
        var v = new List<Vector3>();
        var t = new List<int>();
        const int segments = 120, rows = 28;
        for (int k = 0; k <= rows; k++)
        {
            float y = 0.80f - 0.315f * k / rows;
            float r = 0.1267f + (0.2797f - 0.1267f) * k / rows;
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                v.Add(new Vector3(r * MathF.Cos(angle), y, r * MathF.Sin(angle)));
            }
        }
        for (int k = 0; k < rows; k++)
        for (int i = 0; i < segments; i++)
        {
            int a = k * segments + i, b = k * segments + (i + 1) % segments, c = a + segments, d = b + segments;
            t.AddRange(new[] { a, b, c, b, d, c });
        }
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.True(body.IsIslandSolid(0));
        // 外のスカートの裾（ペチコートより長い）の点: 裾の縁の斜め下
        Assert.True(Signed(body, new Vector3(0.295f, 0.46f, 0f), 0.05f) > 0f);
        Assert.True(Signed(body, new Vector3(0.285f, 0.45f, 0f), 0.05f) > 0f);
        // 上の縁の上
        Assert.True(Signed(body, new Vector3(0.12f, 0.82f, 0f), 0.05f) > 0f);
        // 面の中ほどでは、内外がふつうに決まる
        Assert.True(Signed(body, new Vector3(0.205f, 0.65f, 0f), 0.05f) > 0f);
        Assert.True(Signed(body, new Vector3(0.19f, 0.65f, 0f), 0.05f) < 0f);
    }

    // ---- 島（つながった部品）----

    [Fact]
    public void OverlappingIslands_UseTheDeepestInside()
    {
        // 2つの箱が重なっている（胴体と脚の筒のように）。片方の箱の面が、もう片方の箱の中に入り込んでいる
        var v = new List<Vector3>();
        var t = new List<int>();
        Box(v, t, Vector3.Zero, 0.1f);
        Box(v, t, new Vector3(0.15f, 0f, 0f), 0.1f);
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.Equal(2, body.IslandCount);
        // 1つ目の箱の中で、2つ目の箱の -X の面（x = 0.05）のすぐ外にある点。1つの面だけで決めると「外」になる
        var p = new Vector3(0.045f, 0f, 0f);
        Assert.True(body.TryQuery(p, 0.2f, out BodyHit hit));
        Assert.True(hit.SignedDistance < -0.04f, $"{hit.SignedDistance}");
        Assert.Equal(0, hit.Island);
        // 両方の外
        Assert.True(Signed(body, new Vector3(0.3f, 0f, 0f)) > 0f);
    }

    [Fact]
    public void InvertedIsland_IsFlipped_AndFlatIslandIsNotUsed()
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        Box(v, t, Vector3.Zero, 0.1f);
        // 巻き順を逆にした箱（裏返しのメッシュ）
        for (int k = 0; k < t.Count; k += 3) (t[k + 1], t[k + 2]) = (t[k + 2], t[k + 1]);
        // 平らな板（中が決まらない）
        int s = v.Count;
        v.AddRange(new[] { new Vector3(0.3f, -0.1f, -0.1f), new Vector3(0.3f, 0.1f, -0.1f), new Vector3(0.3f, 0.1f, 0.1f), new Vector3(0.3f, -0.1f, 0.1f) });
        t.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.Equal(2, body.IslandCount);
        Assert.True(body.IsIslandSolid(0));
        Assert.False(body.IsIslandSolid(1));
        Assert.False(body.IsIslandEnabled(1));
        Assert.True(Signed(body, new Vector3(0.11f, 0f, 0f)) > 0f, "裏返しの箱も、外は外");
        Assert.True(Signed(body, new Vector3(0.09f, 0f, 0f)) < 0f);
        // 板の後ろの点は、板を見ないので、箱からの距離になる
        Assert.True(body.TryQuery(new Vector3(0.29f, 0f, 0f), 0.5f, out BodyHit hit));
        Assert.Equal(0, hit.Island);
    }

    [Fact]
    public void ClassifyIslands_UsesTheInnerBody_ButNotAnOuterLayer()
    {
        // 体（半径 10cm の円柱）と、布（半径 12cm の筒の点）と、布の外の上着（半径 14cm の閉じた円柱）
        var v = new List<Vector3>();
        var t = new List<int>();
        Lathe(v, t, 0f, 0f, 1f, 0f, 20, 48, (_, _) => 0.10f);
        Lathe(v, t, 0f, 0f, 0.9f, 0.1f, 20, 48, (_, _) => 0.14f);
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.Equal(2, body.IslandCount);

        var cloth = new List<Vector3>();
        for (int k = 0; k <= 30; k++)
        for (int i = 0; i < 60; i++)
        {
            float angle = 2f * MathF.PI * i / 60;
            cloth.Add(new Vector3(0.12f * MathF.Cos(angle), 0.2f + 0.6f * k / 30, 0.12f * MathF.Sin(angle)));
        }
        int enabled = body.ClassifyIslands(cloth.ToArray());
        Assert.Equal(1, enabled);
        Assert.True(body.IsIslandEnabled(0), "布の内側の体は使う");
        Assert.False(body.IsIslandEnabled(1), "布の外側の上着は使わない（布がその中にあるので）");
        body.GetIslandVotes(0, out int outside, out int inside);
        Assert.True(outside > 0 && inside == 0);
        body.GetIslandVotes(1, out outside, out inside);
        Assert.True(inside > 0 && outside == 0);

        // 使わない島は検索に出てこない。上着の面のすぐ内側でも、体からの距離になる
        Assert.True(body.TryQuery(new Vector3(0.135f, 0.5f, 0f), 0.05f, out BodyHit hit));
        Assert.Equal(0, hit.Island);
        Assert.InRange(hit.SignedDistance, 0.034f, 0.036f);
    }

    [Fact]
    public void ClosestOnTriangle_MatchesClosestPointSearch()
    {
        var random = new Random(3);
        for (int k = 0; k < 2000; k++)
        {
            Vector3 R() => new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            Vector3 a = R(), b = R(), c = R(), p = R() * 2f - Vector3.One * 0.5f;
            Vector3 expected = ClosestPointSearch.ClosestOnTriangle(p, a, b, c, out Vector3 bary);
            ClosestTriangleTree.ClosestOnTriangle(p.X, p.Y, p.Z, a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z,
                out float qx, out float qy, out float qz, out float u, out float v, out float w);
            Assert.True(Vector3.Distance(expected, new Vector3(qx, qy, qz)) < 1e-5f);
            Assert.True(Vector3.Distance(bary, new Vector3(u, v, w)) < 1e-4f);
        }
    }
}
