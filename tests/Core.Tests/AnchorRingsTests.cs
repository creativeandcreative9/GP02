using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 服の付け根（ウエスト・首まわり・肩の側の袖口）を自動で見つける計算のテスト。
/// 形はプログラムで作る: 開いた円筒のスカート、両端が同じ高さの筒（T ポーズの袖）、脚2本＋ウエストのタイツ状、閉じた球、複数の島。
/// </summary>
public class AnchorRingsTests
{
    const float Spacing = 0.01f;

    // ---- 形を作る ----

    // 軸が Y の円錐台（開いた円筒）。上の縁（高さ yTop・半径 rTop）と下の縁（高さ yBottom・半径 rBottom）が開いている。
    // 頂点は上から下へ rings+1 段、1 段に around 個。offset の分だけ動かす
    static void OpenCylinder(float yTop, float rTop, float yBottom, float rBottom, int around, int rings, Vector3 offset,
        List<Vector3> vertices, List<int> triangles)
    {
        int start = vertices.Count;
        for (int r = 0; r <= rings; r++)
        {
            float f = r / (float)rings;
            float radius = rTop + (rBottom - rTop) * f;
            float y = yTop + (yBottom - yTop) * f;
            for (int a = 0; a < around; a++)
            {
                double angle = 2.0 * Math.PI * a / around;
                vertices.Add(offset + new Vector3((float)(Math.Cos(angle) * radius), y, (float)(Math.Sin(angle) * radius)));
            }
        }
        for (int r = 0; r < rings; r++)
        for (int a = 0; a < around; a++)
        {
            int i = start + r * around + a, j = start + r * around + (a + 1) % around;
            triangles.AddRange(new[] { i, j, i + around, j, j + around, i + around });
        }
    }

    // 軸が X の筒（袖）。x が xStart から xEnd まで、高さ y、半径 radius
    static void SleeveTube(float xStart, float xEnd, float y, float radius, int around, int segments, List<Vector3> vertices, List<int> triangles)
    {
        int start = vertices.Count;
        for (int s = 0; s <= segments; s++)
        {
            float x = xStart + (xEnd - xStart) * s / segments;
            for (int a = 0; a < around; a++)
            {
                double angle = 2.0 * Math.PI * a / around;
                vertices.Add(new Vector3(x, y + (float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius)));
            }
        }
        for (int s = 0; s < segments; s++)
        for (int a = 0; a < around; a++)
        {
            int i = start + s * around + a, j = start + s * around + (a + 1) % around;
            triangles.AddRange(new[] { i, j, i + around, j, j + around, i + around });
        }
    }

    // 閉じた球（極から極まで）
    static void ClosedSphere(float radius, int around, int rings, Vector3 center, List<Vector3> vertices, List<int> triangles)
    {
        int start = vertices.Count;
        vertices.Add(center + new Vector3(0f, radius, 0f));
        for (int r = 1; r < rings; r++)
        {
            double polar = Math.PI * r / rings;
            for (int a = 0; a < around; a++)
            {
                double angle = 2.0 * Math.PI * a / around;
                vertices.Add(center + new Vector3((float)(Math.Sin(polar) * Math.Cos(angle) * radius), (float)(Math.Cos(polar) * radius), (float)(Math.Sin(polar) * Math.Sin(angle) * radius)));
            }
        }
        vertices.Add(center + new Vector3(0f, -radius, 0f));
        int bottom = vertices.Count - 1;
        for (int a = 0; a < around; a++) triangles.AddRange(new[] { start, start + 1 + a, start + 1 + (a + 1) % around });
        for (int r = 0; r < rings - 2; r++)
        for (int a = 0; a < around; a++)
        {
            int i = start + 1 + r * around + a, j = start + 1 + r * around + (a + 1) % around;
            triangles.AddRange(new[] { i, i + around, j, j, i + around, j + around });
        }
        for (int a = 0; a < around; a++)
        {
            int i = bottom - around + a, j = bottom - around + (a + 1) % around;
            triangles.AddRange(new[] { i, bottom, j });
        }
    }

    // 同じ数の頂点を持つ2つの輪（番号の並び）の間を、三角形でつなぐ
    static void Band(List<int> triangles, int[] ringA, int[] ringB)
    {
        int n = ringA.Length;
        for (int k = 0; k < n; k++)
        {
            int a0 = ringA[k], a1 = ringA[(k + 1) % n], b0 = ringB[k], b1 = ringB[(k + 1) % n];
            triangles.AddRange(new[] { a0, a1, b0, a1, b1, b0 });
        }
    }

    static int[] AddRing(List<Vector3> vertices, Func<int, Vector3> position, int count)
    {
        var ring = new int[count];
        for (int k = 0; k < count; k++)
        {
            ring[k] = vertices.Count;
            vertices.Add(position(k));
        }
        return ring;
    }

    // 脚2本＋ウエストのタイツ状（ズボン）。ウエスト（高さ yWaist）が開き、足首（高さ yAnkle）が左右1つずつ開く。開いた縁は3つ
    static void Pants(float yWaist, float yCrotch, float yAnkle, List<Vector3> vertices, List<int> triangles)
    {
        const int N = 24, M = N / 2;
        const float R = 0.15f, LegX = 0.08f, AnkleRadius = 0.05f;
        int[] waist = AddRing(vertices, k => HipPoint(k, yWaist, R), N);
        int[] crotch = AddRing(vertices, k => HipPoint(k, yCrotch, R), N);
        Band(triangles, waist, crotch);

        // ズボンの内側の縫い目（x=0 の面の上）の頂点。2本の脚で共有する
        int inseamCount = N - (M + 1);
        int[] inseam = AddRing(vertices, k => new Vector3(0f, yCrotch, -R + 2f * R * (k + 1) / (inseamCount + 1)), inseamCount);

        // 左脚（x が負）: 腰の輪の左半分（正面 F から背中 B へ。M+1 個）と、縫い目（B から F へ）が上の輪になる。右脚は逆側
        for (int side = 0; side < 2; side++)
        {
            var top = new int[N];
            for (int k = 0; k <= M; k++) top[k] = crotch[side == 0 ? N / 4 + k : (3 * N / 4 + k) % N];
            for (int k = 0; k < inseamCount; k++) top[M + 1 + k] = side == 0 ? inseam[k] : inseam[inseamCount - 1 - k];

            float centerX = side == 0 ? -LegX : LegX;
            float phase = side == 0 ? 90f : 270f;
            int rows = 8;
            int[] previous = top;
            for (int row = 1; row <= rows; row++)
            {
                float f = row / (float)rows;
                int[] current = AddRing(vertices, k =>
                {
                    Vector3 from = vertices[top[k]];
                    double angle = (phase + 360.0 * k / N) * Math.PI / 180.0;
                    var ankle = new Vector3(centerX + (float)(Math.Cos(angle) * AnkleRadius), yAnkle, (float)(Math.Sin(angle) * AnkleRadius));
                    return Vector3.Lerp(from, ankle, f);
                }, N);
                Band(triangles, previous, current);
                previous = current;
            }
        }
    }

    static Vector3 HipPoint(int k, float y, float radius)
    {
        double angle = 2.0 * Math.PI * k / 24;
        return new Vector3((float)(Math.Cos(angle) * radius), y, (float)(Math.Sin(angle) * radius));
    }

    static ParticleCloth Build(List<Vector3> vertices, List<int> triangles) => ParticleCloth.Build(vertices.ToArray(), triangles.ToArray(), Spacing);

    static AnchorSearch Search(ParticleCloth cloth, Vector3 up, Vector3 axisPoint, float band = AnchorRings.DefaultBandWidth)
    {
        return AnchorRings.Find(cloth.Topology, cloth.Topology.Positions, cloth.ParticleIsland, cloth.Model.RestPositions, up, axisPoint, band);
    }

    // 輪の線（辺の線分）までの距離（メートル）。ライブラリの関数を使わずに、別に計算して確かめる
    static float DistanceToRing(OpenRing ring, Vector3[] welded, Vector3 p)
    {
        float best = float.MaxValue;
        for (int e = 0; e < ring.EdgeCount; e++)
        {
            Vector3 a = welded[ring.Edges[2 * e]], b = welded[ring.Edges[2 * e + 1]];
            Vector3 ab = b - a;
            float t = Math.Clamp(Vector3.Dot(p - a, ab) / Math.Max(Vector3.Dot(ab, ab), 1e-12f), 0f, 1f);
            best = Math.Min(best, Vector3.Distance(p, a + ab * t));
        }
        return best;
    }

    // ---- スカート（開いた円筒） ----

    [Fact]
    public void Skirt_PinsOnlyTheWaist()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 48, 20, Vector3.Zero, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        Assert.Equal(1, search.IslandCount);
        Assert.Equal(1, search.OpenIslandCount);
        OpenRing ring = Assert.Single(search.Rings);
        Assert.InRange(ring.Centroid.Y, 0.899f, 0.901f);
        Assert.InRange(ring.Centroid.X, -1e-3f, 1e-3f);
        Assert.NotEmpty(search.Particles);

        // 帯の中の粒子は、残らず留まり、帯の外の粒子は1つも留まらない
        var pinned = new HashSet<int>(search.Particles);
        Vector3[] rest = cloth.Model.RestPositions;
        for (int i = 0; i < rest.Length; i++)
        {
            float distance = DistanceToRing(ring, cloth.Topology.Positions, rest[i]);
            if (distance < AnchorRings.DefaultBandWidth - 1e-4f) Assert.True(pinned.Contains(i), $"粒子 {i}（輪から {distance * 100f:F2}cm）が留まっていない");
            if (distance > AnchorRings.DefaultBandWidth + 1e-4f) Assert.False(pinned.Contains(i), $"粒子 {i}（輪から {distance * 100f:F2}cm）が留まっている");
        }
        Assert.All(search.Particles, i => Assert.True(rest[i].Y > 0.86f, $"裾の粒子 {i} が留まっている: {rest[i]}"));
    }

    [Fact]
    public void Skirt_WaistRingIsPickedEvenWhenTheHemIsAlsoOpen()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 36, 12, Vector3.Zero, vertices, triangles);
        MeshTopology topology = MeshTopology.Build(vertices.ToArray(), triangles.ToArray());

        List<OpenRing> rings = AnchorRings.FindOpenRings(topology, topology.Positions);

        Assert.Equal(2, rings.Count);
        Assert.All(rings, ring => Assert.Equal(36, ring.EdgeCount));
        var chosen = new List<OpenRing>();
        AnchorRings.SelectAnchorRings(rings, Vector3.UnitY, Vector3.Zero, chosen);
        Assert.Single(chosen);
        Assert.InRange(chosen[0].Centroid.Y, 0.899f, 0.901f);
        Assert.True(chosen[0].Score > rings.Where(r => r != chosen[0]).Max(r => r.Score));
    }

    [Fact]
    public void BandWidth_ChangesHowManyParticlesAreCaught()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 48, 20, Vector3.Zero, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        int narrow = Search(cloth, Vector3.UnitY, Vector3.Zero, 0.01f).Particles.Count;
        int normal = Search(cloth, Vector3.UnitY, Vector3.Zero).Particles.Count;
        int wide = Search(cloth, Vector3.UnitY, Vector3.Zero, 0.04f).Particles.Count;
        int fallback = Search(cloth, Vector3.UnitY, Vector3.Zero, 0f).Particles.Count;

        Assert.True(narrow > 0 && narrow < normal && normal < wide, $"{narrow} < {normal} < {wide}");
        Assert.Equal(normal, fallback);
    }

    // ---- 袖（両端が同じ高さの筒） ----

    [Fact]
    public void SleeveWithEndsAtTheSameHeight_PinsTheEndNearestTheCenter()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        SleeveTube(0.10f, 0.60f, 1.40f, 0.04f, 24, 25, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        OpenRing ring = Assert.Single(search.Rings);
        Assert.InRange(ring.Centroid.X, 0.099f, 0.101f);
        Assert.NotEmpty(search.Particles);
        Assert.All(search.Particles, i => Assert.True(cloth.Model.RestPositions[i].X < 0.135f, $"袖口の粒子 {i} が留まっている: {cloth.Model.RestPositions[i]}"));
    }

    [Fact]
    public void LeftAndRightSleeves_AreSeparateIslands_AndEachPinsItsShoulderSide()
    {
        // 右の袖（+X）と左の袖（-X）。それぞれ別の島
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        SleeveTube(0.10f, 0.60f, 1.40f, 0.04f, 24, 25, vertices, triangles);
        SleeveTube(-0.10f, -0.60f, 1.40f, 0.04f, 24, 25, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        Assert.Equal(2, search.IslandCount);
        Assert.Equal(2, search.OpenIslandCount);
        Assert.Equal(2, search.Rings.Count);
        Assert.NotEqual(search.Rings[0].Island, search.Rings[1].Island);
        Vector3[] rest = cloth.Model.RestPositions;
        Assert.All(search.Particles, i => Assert.True(MathF.Abs(rest[i].X) < 0.135f, $"粒子 {i}: {rest[i]}"));
        Assert.Contains(search.Particles, i => rest[i].X > 0f);
        Assert.Contains(search.Particles, i => rest[i].X < 0f);
        // 左右で同じくらいの数
        int right = search.Particles.Count(i => rest[i].X > 0f), left = search.Particles.Count - right;
        Assert.InRange((float)right / left, 0.7f, 1.4f);
    }

    [Fact]
    public void ASlopedSleeve_PinsTheShoulderSide_NotTheCuff()
    {
        // A ポーズの袖: 肩（高さ 1.40, x=0.15）から手首（高さ 1.0, x=0.50）へ斜めに下がる
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        const int around = 24, segments = 25;
        Vector3 across = Vector3.Normalize(new Vector3(0.40f, 0.35f, 0f));   // 筒の軸（0.35, -0.40, 0）に垂直な、断面の1つの向き
        for (int s = 0; s <= segments; s++)
        {
            float f = s / (float)segments;
            Vector3 center = new Vector3(0.15f + 0.35f * f, 1.40f - 0.40f * f, 0f);
            for (int a = 0; a < around; a++)
            {
                double angle = 2.0 * Math.PI * a / around;
                vertices.Add(center + across * ((float)Math.Cos(angle) * 0.04f) + new Vector3(0f, 0f, (float)Math.Sin(angle) * 0.04f));
            }
        }
        for (int s = 0; s < segments; s++)
        for (int a = 0; a < around; a++)
        {
            int i = s * around + a, j = s * around + (a + 1) % around;
            triangles.AddRange(new[] { i, j, i + around, j, j + around, i + around });
        }
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        OpenRing ring = Assert.Single(search.Rings);
        Assert.True(ring.Centroid.X < 0.2f && ring.Centroid.Y > 1.3f, ring.Centroid.ToString());
        Assert.All(search.Particles, i => Assert.True(cloth.Model.RestPositions[i].Y > 1.3f, $"粒子 {i}: {cloth.Model.RestPositions[i]}"));
    }

    // ---- タイツ（脚2本＋ウエスト） ----

    [Fact]
    public void Tights_HaveThreeOpenRings_AndOnlyTheWaistIsPinned()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        Pants(0.95f, 0.78f, 0.10f, vertices, triangles);
        MeshTopology topology = MeshTopology.Build(vertices.ToArray(), triangles.ToArray());
        Assert.Equal(1, topology.IslandCount);

        List<OpenRing> rings = AnchorRings.FindOpenRings(topology, topology.Positions);
        Assert.Equal(3, rings.Count);
        Assert.Contains(rings, ring => MathF.Abs(ring.Centroid.Y - 0.95f) < 1e-3f);
        Assert.Equal(2, rings.Count(ring => MathF.Abs(ring.Centroid.Y - 0.10f) < 1e-3f));

        ParticleCloth cloth = Build(vertices, triangles);
        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);
        OpenRing waist = Assert.Single(search.Rings);
        Assert.InRange(waist.Centroid.Y, 0.949f, 0.951f);
        Assert.NotEmpty(search.Particles);
        Assert.All(search.Particles, i => Assert.True(cloth.Model.RestPositions[i].Y > 0.92f, $"足首や脚の粒子 {i} が留まっている: {cloth.Model.RestPositions[i]}"));
    }

    // ---- 閉じた形・複数の島 ----

    [Fact]
    public void ClosedSphere_PinsNothing()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        ClosedSphere(0.15f, 24, 12, new Vector3(0f, 1.0f, 0f), vertices, triangles);
        MeshTopology topology = MeshTopology.Build(vertices.ToArray(), triangles.ToArray());
        Assert.Empty(AnchorRings.FindOpenRings(topology, topology.Positions));

        ParticleCloth cloth = Build(vertices, triangles);
        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        Assert.Empty(search.Particles);
        Assert.Empty(search.Rings);
        Assert.Equal(1, search.IslandCount);
        Assert.Equal(0, search.OpenIslandCount);
    }

    [Fact]
    public void SeveralIslands_EachOpenOneGetsItsOwnAnchor_AndTheClosedOneNone()
    {
        // スカート（開いた部品）・袖（開いた部品）・玉（閉じた部品）
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 36, 14, Vector3.Zero, vertices, triangles);
        SleeveTube(0.50f, 1.00f, 1.40f, 0.04f, 20, 20, vertices, triangles);
        ClosedSphere(0.05f, 16, 8, new Vector3(0f, 1.7f, 0f), vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        Assert.Equal(3, search.IslandCount);
        Assert.Equal(2, search.OpenIslandCount);
        Assert.Equal(2, search.Rings.Count);
        Vector3[] rest = cloth.Model.RestPositions;
        Assert.All(search.Particles, i =>
        {
            Assert.True(rest[i].Y < 1.6f, $"閉じた玉の粒子 {i} が留まっている: {rest[i]}");
            bool waist = rest[i].Y > 0.86f && rest[i].Y < 0.91f;
            bool sleeve = rest[i].X < 0.54f && rest[i].X > 0.49f && rest[i].Y > 1.3f;
            Assert.True(waist || sleeve, $"粒子 {i}: {rest[i]}");
        });
        Assert.Contains(search.Particles, i => rest[i].Y > 0.86f && rest[i].Y < 0.91f);
        Assert.Contains(search.Particles, i => rest[i].Y > 1.3f && rest[i].X > 0.49f);
    }

    [Fact]
    public void LayeredSkirts_EachPinsOnlyItsOwnParticles()
    {
        // 5mm 離した2枚のスカート（別の島）。帯の幅（2cm）より近いが、島が違う粒子は留めない
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 36, 14, Vector3.Zero, vertices, triangles);
        int firstCount = vertices.Count;
        OpenCylinder(0.9f, 0.125f, 0.5f, 0.305f, 36, 14, Vector3.Zero, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch search = Search(cloth, Vector3.UnitY, Vector3.Zero);

        Assert.Equal(2, cloth.Topology.IslandCount);
        Assert.Equal(2, search.Rings.Count);
        var ringOfIsland = search.Rings.ToDictionary(ring => ring.Island);
        Vector3[] welded = cloth.Topology.Positions;
        foreach (int i in search.Particles)
        {
            Assert.True(ringOfIsland.ContainsKey(cloth.ParticleIsland[i]));
            Assert.True(DistanceToRing(ringOfIsland[cloth.ParticleIsland[i]], welded, cloth.Model.RestPositions[i]) <= AnchorRings.DefaultBandWidth + 1e-4f, $"粒子 {i}");
        }
        // どちらの島の粒子も、留まっている
        foreach (OpenRing ring in search.Rings) Assert.Contains(search.Particles, i => cloth.ParticleIsland[i] == ring.Island);
        Assert.True(firstCount > 0);
    }

    // ---- 向きと軸 ----

    [Fact]
    public void UpAndAxis_DecideWhichRingIsTheRoot()
    {
        // 横に倒したスカート（軸が +X。ウエストが x=0.9 側）。アバターの上向きが +X なら、ウエストが付け根
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 36, 12, Vector3.Zero, vertices, triangles);
        for (int i = 0; i < vertices.Count; i++) vertices[i] = new Vector3(vertices[i].Y, vertices[i].X, vertices[i].Z);
        MeshTopology topology = MeshTopology.Build(vertices.ToArray(), triangles.ToArray());
        List<OpenRing> rings = AnchorRings.FindOpenRings(topology, topology.Positions);
        var chosen = new List<OpenRing>();

        AnchorRings.SelectAnchorRings(rings, Vector3.UnitX, Vector3.Zero, chosen);
        Assert.InRange(chosen[0].Centroid.X, 0.899f, 0.901f);

        // 上向きが反対（-X）なら、裾のほうが高い（上）ので、裾が付け根になる
        AnchorRings.SelectAnchorRings(rings, -Vector3.UnitX, Vector3.Zero, chosen);
        Assert.InRange(chosen[0].Centroid.X, 0.499f, 0.501f);
    }

    [Fact]
    public void Score_IsTheHeightMinusHalfTheDistanceFromTheVerticalAxis()
    {
        Vector3 axisPoint = new Vector3(1f, 0.2f, -1f);
        // 軸の上（水平距離 0）の高さ 1.5
        Assert.Equal(1.5f, AnchorRings.Score(new Vector3(1f, 1.7f, -1f), Vector3.UnitY, axisPoint), 1e-5f);
        // 軸から水平に 0.4 離れた、高さ 1.0
        Assert.Equal(1.0f - 0.2f, AnchorRings.Score(new Vector3(1.4f, 1.2f, -1f), Vector3.UnitY, axisPoint), 1e-5f);
        // 上向きが長さ 1 でなくても同じ
        Assert.Equal(AnchorRings.Score(new Vector3(1.4f, 1.2f, -1f), Vector3.UnitY, axisPoint), AnchorRings.Score(new Vector3(1.4f, 1.2f, -1f), new Vector3(0f, 5f, 0f), axisPoint), 1e-5f);
        // 上向きが 0 なら、世界の上向き
        Assert.Equal(AnchorRings.Score(new Vector3(1.4f, 1.2f, -1f), Vector3.UnitY, axisPoint), AnchorRings.Score(new Vector3(1.4f, 1.2f, -1f), Vector3.Zero, axisPoint), 1e-5f);
        // 傾いた上向き（X 方向）でも、その向きの高さと、その軸からの距離で測る
        Assert.Equal(0.9f - 0.5f * 0.3f, AnchorRings.Score(new Vector3(0.9f, 0.3f, 0f), Vector3.UnitX, Vector3.Zero), 1e-5f);
    }

    // ---- 輪の重心・両面の布 ----

    [Fact]
    public void RingCentroid_IsWeightedByEdgeLength_NotByVertexCount()
    {
        // 円の輪。頂点が片側（x>0）に偏って細かく並んでいても、重心は円の中心
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int[] top = AddRing(vertices, k =>
        {
            // 前半 40 個は x>0 の半円に、後半 8 個は x<0 の半円に
            double angle = k < 40 ? -Math.PI / 2 + Math.PI * k / 40.0 : Math.PI / 2 + Math.PI * (k - 40) / 8.0;
            return new Vector3((float)(Math.Cos(angle) * 0.2f), 1f, (float)(Math.Sin(angle) * 0.2f));
        }, 48);
        int[] bottom = AddRing(vertices, k => vertices[top[k]] + new Vector3(0f, -0.3f, 0f), 48);
        Band(triangles, top, bottom);
        MeshTopology topology = MeshTopology.Build(vertices.ToArray(), triangles.ToArray());

        List<OpenRing> rings = AnchorRings.FindOpenRings(topology, topology.Positions);

        OpenRing upper = rings.Single(ring => ring.Centroid.Y > 0.9f);
        Vector3 vertexMean = top.Aggregate(Vector3.Zero, (sum, i) => sum + vertices[i]) / top.Length;
        Assert.True(MathF.Abs(vertexMean.X) > 0.05f, "頂点の平均は偏る: " + vertexMean);
        Assert.InRange(upper.Centroid.X, -0.02f, 0.02f);
        Assert.InRange(upper.Centroid.Z, -0.02f, 0.02f);
        Assert.InRange(upper.Length, 2f * MathF.PI * 0.2f * 0.97f, 2f * MathF.PI * 0.2f * 1.001f);
    }

    [Fact]
    public void DoubleSidedSheet_StillHasItsOpenEdge()
    {
        // 前と裏（同じ3頂点で向きを逆にした三角形）で重ねた平らな布。縁の辺は2枚ずつ付いているが、開いた縁として見つかる
        ProceduralMeshes.Grid(8, 8, 0.4f, 0.4f, out Vector3[] vertices, out int[] triangles);
        var both = new List<int>(triangles);
        for (int t = 0; t < triangles.Length; t += 3) both.AddRange(new[] { triangles[t], triangles[t + 2], triangles[t + 1] });
        MeshTopology topology = MeshTopology.Build(vertices, both.ToArray());

        List<OpenRing> rings = AnchorRings.FindOpenRings(topology, topology.Positions);

        OpenRing ring = Assert.Single(rings);
        Assert.Equal(32, ring.EdgeCount);
        Assert.Equal(1.6f, ring.Length, 1e-4f);
        Assert.InRange(ring.Centroid.X, 0.199f, 0.201f);
        Assert.InRange(ring.Centroid.Y, 0.199f, 0.201f);
    }

    [Fact]
    public void UvSeamsAndSplitVertices_DoNotCountAsOpenEdges()
    {
        // 継ぎ目で頂点を分けた布（同じ位置に2つ）。まとめたあとはつながるので、縁は外側の1周だけ
        ProceduralMeshes.GridWithSeam(8, 6, 0.4f, 0.3f, out Vector3[] vertices, out int[] triangles);
        MeshTopology topology = MeshTopology.Build(vertices, triangles);

        List<OpenRing> rings = AnchorRings.FindOpenRings(topology, topology.Positions);

        OpenRing ring = Assert.Single(rings);
        Assert.Equal(2 * (8 + 6), ring.EdgeCount);
    }

    [Fact]
    public void Find_IsDeterministic_AndParticlesAreSortedWithoutDuplicates()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 36, 12, Vector3.Zero, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        AnchorSearch a = Search(cloth, Vector3.UnitY, Vector3.Zero);
        AnchorSearch b = Search(cloth, Vector3.UnitY, Vector3.Zero);

        Assert.Equal(a.Particles, b.Particles);
        Assert.Equal(a.Particles.OrderBy(i => i).ToList(), a.Particles);
        Assert.Equal(a.Particles.Count, a.Particles.Distinct().Count());
    }

    [Fact]
    public void Find_ChecksItsArguments()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 12, 4, Vector3.Zero, vertices, triangles);
        MeshTopology topology = MeshTopology.Build(vertices.ToArray(), triangles.ToArray());

        Assert.Throws<ArgumentNullException>(() => AnchorRings.FindOpenRings(null, topology.Positions));
        Assert.Throws<ArgumentException>(() => AnchorRings.FindOpenRings(topology, new Vector3[3]));
        Assert.Throws<ArgumentException>(() => AnchorRings.Find(topology, topology.Positions, new int[2], new Vector3[3], Vector3.UnitY, Vector3.Zero));
    }

    // 今の形（粒子が動いたあと）の位置で付け根を見つける: 縁は今の頂点の位置を使う
    [Fact]
    public void Find_UsesTheCurrentShape_NotTheRestShape()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        OpenCylinder(0.9f, 0.12f, 0.5f, 0.30f, 36, 12, Vector3.Zero, vertices, triangles);
        ParticleCloth cloth = Build(vertices, triangles);

        // 全体を +X に 1m、上に 0.3m 動かした形
        Vector3 shift = new Vector3(1f, 0.3f, 0f);
        Vector3[] welded = cloth.Topology.Positions.Select(p => p + shift).ToArray();
        Vector3[] particles = cloth.Model.RestPositions.Select(p => p + shift).ToArray();
        AnchorSearch search = AnchorRings.Find(cloth.Topology, welded, cloth.ParticleIsland, particles, Vector3.UnitY, Vector3.Zero);

        Assert.NotEmpty(search.Particles);
        OpenRing ring = Assert.Single(search.Rings);
        Assert.InRange(ring.Centroid.Y, 1.199f, 1.201f);
        Assert.All(search.Particles, i => Assert.True(particles[i].Y > 1.16f, $"粒子 {i}: {particles[i]}"));
    }
}
