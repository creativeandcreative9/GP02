using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 動作チェック（PoseCheckEvaluator・PoseCheckSummary）と「外へ逃がす」（PoseEscapeFixer）、ポーズを変えた体（BodySurface.SetVertexPositions）のテスト。
/// ポーズの代わりに、体や布の頂点をプログラムで動かす（体は円柱、布はそれを囲む筒）。
/// </summary>
[Collection("DefaultKernel")]
public class PoseCheckTests
{
    const float LegRadius = 0.10f;
    const float TubeRadius = 0.115f;
    const float TubeTop = 0.7f, TubeBottom = 0.3f;

    // 円柱の体（中心 (cx, cz)）。bump で角度・高さごとに半径を足せる
    static (Vector3[] Vertices, int[] Triangles) Cylinder(float cx = 0f, float cz = 0f, float radius = LegRadius, Func<float, float, float>? bump = null, int segments = 64)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        BodySurfaceTests.Lathe(v, t, cx, cz, 1f, 0f, 100, segments, (y, angle) => radius + (bump != null ? bump(y, angle) : 0f));
        return (v.ToArray(), t.ToArray());
    }

    static BodySurface Body(params (Vector3[] Vertices, int[] Triangles)[] parts)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        foreach (var (vertices, triangles) in parts)
        {
            int start = v.Count;
            v.AddRange(vertices);
            foreach (int k in triangles) t.Add(start + k);
        }
        var body = new BodySurface(v.ToArray(), t.ToArray());
        body.EnableSolidIslands();
        return body;
    }

    // 体を囲む筒の布（ふたなし、外向きが表）
    static ParticleCloth Tube(float radius = TubeRadius, float centerX = 0f, int segments = 96, int rows = 40)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        for (int k = 0; k <= rows; k++)
        {
            float y = TubeTop + (TubeBottom - TubeTop) * k / rows;
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                v.Add(new Vector3(centerX + radius * MathF.Cos(angle), y, radius * MathF.Sin(angle)));
            }
        }
        for (int k = 0; k < rows; k++)
        for (int i = 0; i < segments; i++)
        {
            int a = k * segments + i, b = k * segments + (i + 1) % segments, c = a + segments, d = b + segments;
            t.AddRange(new[] { a, b, c, b, d, c });
        }
        return ParticleCloth.Build(v.ToArray(), t.ToArray());
    }

    static Vector3[] Moved(Vector3[] vertices, Vector3 offset)
    {
        var result = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++) result[i] = vertices[i] + offset;
        return result;
    }

    static float Angle(Vector3 p, float cx = 0f) => MathF.Atan2(p.Z, p.X - cx);

    [Fact]
    public void LegPushedIntoTheTube_IsOneRegion_AndTheBasePoseHasNone()
    {
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        Vector3[] clothVertices = cloth.Topology.Positions;
        var (legVertices, legTriangles) = Cylinder();
        BodySurface body = Body((legVertices, legTriangles));
        var options = new PoseCheckOptions();

        // 基準の姿勢: 隙間 1.5cm で、貫通なし
        var baseline = new float[evaluator.VertexCount];
        Assert.True(evaluator.MeasureDepth(clothVertices, body, options, baseline));
        Assert.All(baseline, d => Assert.Equal(0f, d));
        var result = new PosePenetration { Group = 3, Stage = 0.5f };
        evaluator.Evaluate(clothVertices, body, options, baseline, result);
        Assert.True(result.HasBody);
        Assert.False(result.IsPenetrating);
        Assert.Equal(0, result.RegionCount);
        Assert.Equal(3, result.Group);
        Assert.Equal(0.5f, result.Stage);

        // ポーズ: 脚が +X へ 3cm 動いて、+X の側で布を 1.5cm 突き抜ける
        body.SetVertexPositions(Moved(legVertices, new Vector3(0.03f, 0f, 0f)));
        evaluator.Evaluate(clothVertices, body, options, baseline, result);
        Assert.True(result.IsPenetrating, result.ToString());
        Assert.Equal(1, result.RegionCount);
        Assert.InRange(result.MaxDepth, 0.0135f, 0.0155f);
        Assert.Equal(result.Vertices.Count, result.Depths.Count);
        Assert.NotEmpty(result.Points);
        foreach (int v in result.Vertices)
        {
            Vector3 p = clothVertices[v];
            Assert.True(MathF.Abs(Angle(p)) < 1.2f, $"+X の側から離れた頂点 {v} {p}");
        }
        foreach (float depth in result.Depths) Assert.True(depth > options.Tolerance);
        Assert.Equal(3, result.Group);

        // 許容の深さを大きくすると数えない
        evaluator.Evaluate(clothVertices, body, new PoseCheckOptions { Tolerance = 0.02f }, baseline, result);
        Assert.False(result.IsPenetrating);

        // 体が無ければ調べない
        evaluator.Evaluate(clothVertices, null!, options, baseline, result);
        Assert.False(result.HasBody);
        Assert.False(result.IsPenetrating);
    }

    [Fact]
    public void TwoBumps_AreTwoRegions()
    {
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        // 角度 0 と π の側に、高さ 0.5m を中心にこぶ（布を 1cm 突き抜ける）
        float Bump(float y, float angle)
        {
            float a0 = angle > MathF.PI ? angle - 2f * MathF.PI : angle;
            float a1 = angle - MathF.PI;
            float dy = y - 0.5f;
            float s0 = LegRadius * a0, s1 = LegRadius * a1;
            const float sigma = 0.03f, height = 0.025f;
            return height * (MathF.Exp(-(dy * dy + s0 * s0) / (2f * sigma * sigma)) + MathF.Exp(-(dy * dy + s1 * s1) / (2f * sigma * sigma)));
        }
        BodySurface body = Body(Cylinder(bump: Bump));
        var result = new PosePenetration();
        evaluator.Evaluate(cloth.Topology.Positions, body, new PoseCheckOptions(), null, result);
        Assert.Equal(2, result.RegionCount);
        Assert.InRange(result.MaxDepth, 0.007f, 0.0105f);
        // か所の番号: 角度 0 の側と π の側で分かれ、か所ごとの深さはその中の一番深い頂点
        Assert.Equal(result.Vertices.Count, result.VertexRegions.Count);
        Assert.Equal(2, result.RegionDepths.Count);
        for (int k = 0; k < result.Vertices.Count; k++)
        {
            int r = result.VertexRegions[k];
            Assert.InRange(r, 0, 1);
            Assert.True(result.Depths[k] <= result.RegionDepths[r]);
            bool positiveX = cloth.Topology.Positions[result.Vertices[k]].X > 0f;
            int first = result.VertexRegions[0];
            bool firstPositiveX = cloth.Topology.Positions[result.Vertices[0]].X > 0f;
            Assert.Equal(positiveX == firstPositiveX, r == first);
        }
        Assert.Equal(result.MaxDepth, MathF.Max(result.RegionDepths[0], result.RegionDepths[1]));
    }

    [Fact]
    public void Gains_ShrinkWhereThePoseScalesTheOutwardMove()
    {
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        Vector3[] vertices = cloth.Topology.Positions;
        var normals = new Vector3[evaluator.VertexCount];
        evaluator.ComputeOutwardNormals(vertices, normals);
        foreach (Vector3 n in normals) Assert.InRange(n.Length(), 0.999f, 1.001f);
        // 外向きは筒の半径の向き
        for (int v = 0; v < vertices.Length; v += 97)
        {
            var radial = Vector3.Normalize(new Vector3(vertices[v].X, 0f, vertices[v].Z));
            Assert.True(Vector3.Dot(radial, normals[v]) > 0.98f, $"頂点 {v}");
        }

        // ポーズで、外向きの動きが +X の側だけ半分に縮む（ひざを曲げたときの、ウェイトの混ざった頂点のように）
        var result = new PosePenetration();
        for (int v = 0; v < vertices.Length; v++)
        {
            result.Vertices.Add(v);
            result.Depths.Add(0.005f);
        }
        var probe = new Vector3[vertices.Length];
        for (int v = 0; v < vertices.Length; v++) probe[v] = vertices[v] + normals[v] * (vertices[v].X > 0f ? 0.005f : 0.01f);
        evaluator.MeasureGains(vertices, probe, 0.01f, result);
        for (int v = 0; v < vertices.Length; v++) Assert.InRange(result.Gains[v], vertices[v].X > 0f ? 0.499f : 0.999f, vertices[v].X > 0f ? 0.501f : 1.001f);
    }

    [Fact]
    public void PlanEscape_PushesEnoughForThePose_AndSkipsDeepOrLowGainRegions()
    {
        var summary = new PoseCheckSummary(10);
        // 1つ目のポーズ: か所 0（浅い・割合 0.8）、か所 1（割合 0.4 の頂点がある）、か所 2（深い）
        var entry = new PosePenetration { Group = 0, HasBody = true, RegionCount = 3, MaxDepth = 0.05f };
        void Add(PosePenetration e, int vertex, int region, float depth, float gain)
        {
            e.Vertices.Add(vertex);
            e.Depths.Add(depth);
            e.VertexRegions.Add(region);
            e.Gains.Add(gain);
            while (e.RegionDepths.Count <= region) e.RegionDepths.Add(0f);
            e.RegionDepths[region] = MathF.Max(e.RegionDepths[region], depth);
        }
        Add(entry, 0, 0, 0.004f, 0.8f);
        Add(entry, 1, 0, 0.002f, 1f);
        Add(entry, 2, 1, 0.003f, 0.9f);
        Add(entry, 3, 1, 0.006f, 0.4f);
        Add(entry, 4, 2, 0.05f, 1f);
        Add(entry, 5, 2, 0.001f, 1f);
        summary.Add(entry);
        // 2つ目のポーズ: 頂点 0 がもっと深い（量の大きいほうを使う）。割合を測っていない（1 とみなす）
        var other = new PosePenetration { Group = 1, HasBody = true, RegionCount = 1, MaxDepth = 0.008f };
        other.Vertices.Add(0);
        other.Depths.Add(0.008f);
        other.VertexRegions.Add(0);
        other.RegionDepths.Add(0.008f);
        summary.Add(other);

        PoseEscapePlan plan = summary.PlanEscape(0.002f, 0.03f, 0.7f);
        Assert.Equal(new[] { true, false, false }, plan.AllowedRegions[0]);
        Assert.Equal(new[] { true }, plan.AllowedRegions[1]);
        Assert.Equal(2, plan.PushedRegions);
        Assert.Equal(1, plan.LowGainRegions);
        Assert.Equal(1, plan.TooFarRegions);
        Assert.Equal(2, plan.SkippedRegions);
        Assert.Equal(MathF.Max((0.004f + 0.002f) / 0.8f, 0.008f + 0.002f), plan.WeldedPush[0], 5);
        Assert.Equal(0.004f, plan.WeldedPush[1], 5);
        Assert.Equal(0f, plan.WeldedPush[2]);
        Assert.Equal(0f, plan.WeldedPush[3]);
        Assert.Equal(0f, plan.WeldedPush[4]);
        Assert.Equal(0f, plan.WeldedPush[5]);
    }

    [Fact]
    public void PenetrationInTheBasePose_IsCountedOnlyWhenThePoseMakesItDeeper()
    {
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        Vector3[] clothVertices = cloth.Topology.Positions;
        // 基準の姿勢から、角度 0 の側で 5mm 突き抜けている
        float Bump(float y, float angle, float height)
        {
            float a = angle > MathF.PI ? angle - 2f * MathF.PI : angle;
            float dy = y - 0.5f, s = LegRadius * a;
            return height * MathF.Exp(-(dy * dy + s * s) / (2f * 0.03f * 0.03f));
        }
        var (vertices, triangles) = Cylinder(bump: (y, a) => Bump(y, a, 0.02f));
        BodySurface body = Body((vertices, triangles));
        var options = new PoseCheckOptions();
        var baseline = new float[evaluator.VertexCount];
        evaluator.MeasureDepth(clothVertices, body, options, baseline);
        Assert.InRange(baseline.Max(), 0.004f, 0.0055f);

        var result = new PosePenetration();
        // 基準と同じ形のポーズ: 基準の深さを引くので数えない。引かなければ数える
        evaluator.Evaluate(clothVertices, body, options, baseline, result);
        Assert.False(result.IsPenetrating, result.ToString());
        evaluator.Evaluate(clothVertices, body, options, null, result);
        Assert.True(result.IsPenetrating);

        // ポーズでこぶが 2cm 高くなる: 深くなった所だけを数える（深さは基準からではなく、その姿勢での深さ）
        var (deeper, _) = Cylinder(bump: (y, a) => Bump(y, a, 0.04f));
        body.SetVertexPositions(deeper);
        evaluator.Evaluate(clothVertices, body, options, baseline, result);
        Assert.True(result.IsPenetrating);
        Assert.Equal(1, result.RegionCount);
        Assert.InRange(result.MaxDepth, 0.023f, 0.0255f);
    }

    [Fact]
    public void NarrowSpikeBetweenClothVertices_IsFoundByTheBodyVertices()
    {
        // 粗い筒（頂点の間隔 約 3cm）と、その間を抜ける細いとげ（広がり 4mm、布を 1cm 突き抜ける）
        ParticleCloth cloth = Tube(segments: 24, rows: 12);
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        // とげは、布の頂点（角度 0°・15°…、高さ 0.7m から 3.3cm ごと）の間で、体の頂点の上（角度 8°、高さ 0.52m）に置く
        float spikeAngle = 8f * MathF.PI / 180f, spikeY = 0.52f;
        float Spike(float y, float angle)
        {
            float a = angle - spikeAngle;
            float dy = y - spikeY, s = LegRadius * a;
            return 0.025f * MathF.Exp(-(dy * dy + s * s) / (2f * 0.004f * 0.004f));
        }
        BodySurface body = Body(Cylinder(bump: Spike, segments: 360));
        var result = new PosePenetration();
        evaluator.Evaluate(cloth.Topology.Positions, body, new PoseCheckOptions { CheckBodyVertices = false }, null, result);
        Assert.False(result.IsPenetrating, "布の頂点だけでは、間を抜けるとげは見つからない");
        evaluator.Evaluate(cloth.Topology.Positions, body, new PoseCheckOptions(), null, result);
        Assert.True(result.IsPenetrating, result.ToString());
        Assert.Equal(1, result.RegionCount);
        Assert.True(result.BodyVertexCount > 0);
        Assert.InRange(result.MaxDepth, 0.006f, 0.0115f);
    }

    // 外向きが表の直方体
    static (Vector3[] Vertices, int[] Triangles) Box(Vector3 min, Vector3 max)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        int[] t =
        {
            0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,   // -Z, +Z
            0, 1, 4, 1, 5, 4,   2, 6, 3, 3, 6, 7,   // -Y, +Y
            0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,   // -X, +X
        };
        return (v, t);
    }

    [Fact]
    public void BodyBeyondTheOpenHem_IsNotAPoke()
    {
        // 筒の裾（高さ 0.3m）のすぐ下から、外へ突き出した足（直方体）。一番近い布の点は裾の縁の上なので、布を突き抜けたとはみなさない
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        BodySurface body = Body(Cylinder(), Box(new Vector3(0.05f, 0.26f, -0.03f), new Vector3(0.2f, 0.29f, 0.03f)));
        var result = new PosePenetration();
        evaluator.Evaluate(cloth.Topology.Positions, body, new PoseCheckOptions(), null, result);
        Assert.False(result.IsPenetrating, result.ToString());

        // 同じ足が裾より上にあれば、布を突き抜けている
        body.SetVertexPositions(Concat(Cylinder(), Box(new Vector3(0.05f, 0.40f, -0.03f), new Vector3(0.2f, 0.43f, 0.03f))));
        evaluator.Evaluate(cloth.Topology.Positions, body, new PoseCheckOptions(), null, result);
        Assert.True(result.IsPenetrating);
        Assert.Equal(1, result.RegionCount);
    }

    [Fact]
    public void SleeveNextToTheTorso_IsNotPenetrating_UntilTheTorsoGoesInside()
    {
        // 腕（中心 x=0.25）と袖、その横（中心 x=0）に胴体。袖の内側の面と胴体は向かい合っていて、隙間は 2cm
        const float armX = 0.25f, armRadius = 0.05f, sleeveRadius = 0.06f;
        ParticleCloth sleeve = Tube(radius: sleeveRadius, centerX: armX, segments: 64, rows: 40);
        var evaluator = PoseCheckEvaluator.FromCloth(sleeve);
        var torso = Cylinder(radius: 0.17f);
        var arm = Cylinder(cx: armX, radius: armRadius);
        BodySurface body = Body(torso, arm);
        var options = new PoseCheckOptions();
        var result = new PosePenetration();
        evaluator.Evaluate(sleeve.Topology.Positions, body, options, null, result);
        Assert.False(result.IsPenetrating, "向かい合った胴体の面を、袖を突き抜けた体とみなしている: " + result);

        // 腕を下ろしたように、胴体が袖の内側に 1cm 入る。布の頂点が胴体の中にあれば、向きに関係なく数える
        body.SetVertexPositions(Concat(Cylinder(cx: 0.03f, radius: 0.17f), arm));
        evaluator.Evaluate(sleeve.Topology.Positions, body, options, null, result);
        Assert.True(result.IsPenetrating);
        Assert.InRange(result.MaxDepth, 0.008f, 0.0105f);
        foreach (int v in result.Vertices) Assert.True(sleeve.Topology.Positions[v].X < armX, "胴体の側の頂点だけ");
    }

    static Vector3[] Concat(params (Vector3[] Vertices, int[] Triangles)[] parts)
    {
        var v = new List<Vector3>();
        foreach (var part in parts) v.AddRange(part.Vertices);
        return v.ToArray();
    }

    [Fact]
    public void Summary_KeepsTheWorstDepthPerVertex_CountsPoses_AndRanksThem()
    {
        var summary = new PoseCheckSummary(10);
        var a = new PosePenetration { Group = 0, Stage = 0.5f, HasBody = true, RegionCount = 1, MaxDepth = 0.004f };
        a.Vertices.AddRange(new[] { 1, 2, 3 });
        a.Depths.AddRange(new[] { 0.002f, 0.004f, 0.003f });
        var b = new PosePenetration { Group = 0, Stage = 1f, HasBody = true, RegionCount = 2, MaxDepth = 0.009f };
        b.Vertices.AddRange(new[] { 2, 3, 7 });
        b.Depths.AddRange(new[] { 0.009f, 0.001f, 0.005f });
        var c = new PosePenetration { Group = 1, Stage = 1f, HasBody = true };
        var d = new PosePenetration { Group = 2, Stage = 1f, HasBody = true, RegionCount = 3, MaxDepth = 0.004f };
        d.Vertices.Add(9);
        d.Depths.Add(0.004f);
        Assert.Equal(0, summary.Add(a));
        Assert.Equal(1, summary.Add(b));
        Assert.Equal(2, summary.Add(c));
        Assert.Equal(3, summary.Add(d));

        Assert.Equal(0.002f, summary.WorstDepth[1]);
        Assert.Equal(0, summary.WorstEntry[1]);
        Assert.Equal(0.009f, summary.WorstDepth[2]);
        Assert.Equal(1, summary.WorstEntry[2]);
        Assert.Equal(0.003f, summary.WorstDepth[3]);
        Assert.Equal(0, summary.WorstEntry[3]);
        Assert.Equal(0.005f, summary.WorstDepth[7]);
        Assert.Equal(0f, summary.WorstDepth[0]);
        Assert.Equal(-1, summary.WorstEntry[0]);
        Assert.Equal(5, summary.PenetratingVertexCount);
        Assert.Equal(3, summary.PenetratingEntryCount);
        Assert.Equal(2, summary.PenetratingGroupCount);
        Assert.Equal(6, summary.TotalRegionCount);
        Assert.Equal(0.009f, summary.MaxDepth);
        // 悪い順: 深さ 9mm → 4mm で所が多い方（3か所）→ 4mm で1か所
        Assert.Equal(new[] { 1, 3, 0 }, summary.RankEntries());
    }

    [Fact]
    public void ToParticleDepth_UsesTheDeepestVertexBoundToEachParticle()
    {
        ParticleCloth cloth = Tube(segments: 48, rows: 20);
        int vertexCount = cloth.Topology.Positions.Length;
        var summary = new PoseCheckSummary(vertexCount);
        var entry = new PosePenetration { HasBody = true, RegionCount = 1 };
        // 頂点 0〜9 に深さ（頂点 5 が一番深い）
        for (int v = 0; v < 10; v++)
        {
            entry.Vertices.Add(v);
            entry.Depths.Add(v == 5 ? 0.02f : 0.005f);
        }
        entry.MaxDepth = 0.02f;
        summary.Add(entry);
        var particleDepth = new float[cloth.Model.ParticleCount];
        summary.ToParticleDepth(cloth, particleDepth);

        var expected = new float[particleDepth.Length];
        for (int v = 0; v < 10; v++)
        {
            for (int k = 0; k < cloth.BoundParticleCount(v); k++)
            {
                int i = cloth.GetBoundParticle(v, k, out float weight);
                if (weight > 0f) expected[i] = MathF.Max(expected[i], v == 5 ? 0.02f : 0.005f);
            }
        }
        Assert.Equal(expected, particleDepth);
        Assert.Contains(particleDepth, d => d == 0.02f);
        Assert.Contains(particleDepth, d => d == 0f);
        Assert.Throws<ArgumentException>(() => summary.ToParticleDepth(cloth, new float[3]));
    }

    [Fact]
    public void BodySurface_SetVertexPositions_MatchesASurfaceBuiltFromTheMovedVertices()
    {
        var (vertices, triangles) = Cylinder(bump: (y, a) => 0.01f * MathF.Sin(3f * a) * MathF.Sin(5f * y));
        var surface = new BodySurface(vertices, triangles);
        surface.EnableSolidIslands();
        Assert.Equal(vertices.Length, surface.SourceVertexCount);

        // 曲げたように、高さに応じて横へずらし、全体を回す
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)), 0.7f);
        var moved = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 p = vertices[i];
            p.X += 0.08f * p.Y * p.Y;
            moved[i] = Vector3.Transform(p, rotation) + new Vector3(0.1f, -0.2f, 0.05f);
        }
        surface.SetVertexPositions(moved);
        var rebuilt = new BodySurface(moved, triangles);
        rebuilt.EnableSolidIslands();

        var random = new Random(7);
        int compared = 0;
        for (int k = 0; k < 400; k++)
        {
            Vector3 q = Vector3.Transform(new Vector3((float)random.NextDouble() * 0.4f - 0.2f, (float)random.NextDouble(), (float)random.NextDouble() * 0.4f - 0.2f), rotation)
                + new Vector3(0.1f, -0.2f, 0.05f);
            bool a = surface.TryQuery(q, 0.05f, out BodyHit ha);
            bool b = rebuilt.TryQuery(q, 0.05f, out BodyHit hb);
            Assert.Equal(b, a);
            if (!a) continue;
            compared++;
            Assert.InRange(ha.SignedDistance - hb.SignedDistance, -1e-5f, 1e-5f);
            Assert.True(Vector3.Distance(ha.Point, hb.Point) < 1e-4f);
            Assert.True(Vector3.Dot(ha.Normal, hb.Normal) > 0.999f);
        }
        Assert.True(compared > 50, $"比べた点 {compared}");
        Assert.Throws<ArgumentException>(() => surface.SetVertexPositions(new Vector3[3]));
    }

    [Fact]
    public void MeasureEffect_ComparesThePushedPlacesPoseByPose()
    {
        // 逃がす前: 結果 0 のか所 0（頂点 0, 1）を逃がす。か所 1（頂点 2）は逃がさない。結果 1 は逃がさない
        PosePenetration Entry(int group, params (int Vertex, int Region, float Depth)[] items)
        {
            var e = new PosePenetration { Group = group, HasBody = true };
            foreach (var (vertex, region, depth) in items)
            {
                e.Vertices.Add(vertex);
                e.Depths.Add(depth);
                e.VertexRegions.Add(region);
                while (e.RegionDepths.Count <= region) e.RegionDepths.Add(0f);
                e.RegionDepths[region] = MathF.Max(e.RegionDepths[region], depth);
            }
            e.RegionCount = e.RegionDepths.Count;
            return e;
        }
        var before = new PoseCheckSummary(5);
        before.Add(Entry(0, (0, 0, 0.004f), (1, 0, 0.002f), (2, 1, 0.05f)));
        before.Add(Entry(1, (0, 0, 0.06f)));
        PoseEscapePlan plan = before.PlanEscape(0.002f, 0.02f, 0.7f);
        Assert.Equal(1, plan.PushedRegions);
        Assert.Equal(2, plan.TooFarRegions);

        // 逃がしたあと: 頂点 0 は浅くなり、頂点 1 は無くなった。逃がさなかった所はそのまま、頂点 4 に新しく少し
        var after = new PoseCheckSummary(5);
        after.Add(Entry(0, (0, 0, 0.001f), (2, 1, 0.05f), (4, 2, 0.002f)));
        after.Add(Entry(1, (0, 0, 0.06f)));
        PoseEscapeEffect effect = plan.MeasureEffect(before, after, 1f);
        Assert.Equal(2, effect.PushedPairs);
        Assert.Equal(1, effect.ClearedPairs);
        Assert.Equal(0.003f, effect.PushedDepthBefore, 5);
        Assert.Equal(0.0005f, effect.PushedDepthAfter, 5);
        Assert.Equal(0.116, effect.TotalDepthBefore, 5);
        Assert.Equal(0.113, effect.TotalDepthAfter, 5);
        Assert.True(effect.Improved(0.5f));
        Assert.False(effect.Improved(0.1f));

        // 深さを 2cm で頭打ちにすると、逃がさない深い所が少し深くなっても、合計はほとんど動かない
        var deeper = new PoseCheckSummary(5);
        deeper.Add(Entry(0, (0, 0, 0.001f), (2, 1, 0.055f)));
        deeper.Add(Entry(1, (0, 0, 0.065f)));
        Assert.False(plan.MeasureEffect(before, deeper, 1f).Improved(0.5f));
        PoseEscapeEffect capped = plan.MeasureEffect(before, deeper, 0.02f);
        Assert.Equal(0.046, capped.TotalDepthBefore, 5);
        Assert.Equal(0.041, capped.TotalDepthAfter, 5);
        Assert.True(capped.Improved(0.5f));

        // 全体が増えたら、逃がした所が浅くなっても効いたとはみなさない
        var worse = new PoseCheckSummary(5);
        worse.Add(Entry(0, (2, 1, 0.05f), (3, 2, 0.02f)));
        worse.Add(Entry(1, (0, 0, 0.06f)));
        PoseEscapeEffect effectWorse = plan.MeasureEffect(before, worse, 0.02f);
        Assert.Equal(2, effectWorse.ClearedPairs);
        Assert.False(effectWorse.Improved(0.5f));
    }

    [Fact]
    public void Escape_PushesTheParticlesOut_WithoutABody_AndLeavesPinsAlone()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = cloth.CreateSolver();
        // 周りが内へ寄れるようにしても、逃がす計算では周りを今の位置より内へ入れない
        solver.KeepOutsideRestSurface = false;
        Assert.Null(solver.Body);
        int n = cloth.Model.ParticleCount;
        // 上端をピン留め。+X の側の高さ 0.5m あたりを 3mm 逃がす（上端の近くの粒子も入れて、ピンは動かないことを見る）
        const float amount = 0.003f;
        var push = new float[n];
        int pinnedWithPush = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = cloth.Model.RestPositions[i];
            if (p.Y > TubeTop - cloth.Spacing) solver.SetPinned(i, true);
            bool patch = MathF.Abs(Angle(p)) < 0.4f && MathF.Abs(p.Y - 0.5f) < 0.05f;
            bool top = MathF.Abs(Angle(p)) < 0.2f && p.Y > TubeTop - cloth.Spacing;
            if (patch || top) push[i] = amount;
            if (top && solver.IsPinned(i)) pinnedWithPush++;
        }
        Vector3[] before = solver.Positions.ToArray();
        var options = new PoseEscapeOptions();
        PoseEscapeResult result = PoseEscapeFixer.Fix(cloth, solver, push, options);
        Assert.True(result.Particles > 10, result.ToString());
        Assert.Equal(pinnedWithPush, result.Pinned);
        Assert.Equal(0, result.NoDirection);
        Assert.Equal(0, result.Capped);
        Assert.Equal(0, result.Limited);
        Assert.Equal(1, result.Clusters);
        Assert.InRange(result.MaxPush, amount - 1e-4f, amount + 1e-4f);
        Assert.True(result.RemainingViolation < 2e-4f, result.ToString());
        Assert.True(result.MaxStretch <= options.MaxStretch + 0.005f, result.ToString());

        ReadOnlySpan<Vector3> after = solver.Positions;
        for (int i = 0; i < n; i++)
        {
            if (solver.IsPinned(i))
            {
                Assert.Equal(before[i], after[i]);
                continue;
            }
            float radialBefore = new Vector2(before[i].X, before[i].Z).Length();
            float radialAfter = new Vector2(after[i].X, after[i].Z).Length();
            // 逃がす所は外へ出て、ほかの所は内へ入らない
            if (push[i] > 0f) Assert.True(radialAfter - radialBefore > amount - 0.0005f, $"粒子 {i}: {radialBefore:F4} → {radialAfter:F4}");
            else Assert.True(radialAfter - radialBefore > -2e-4f, $"粒子 {i} が内へ入った: {radialBefore:F4} → {radialAfter:F4}");
        }
        // 足した面は、粒子の位置を置き換えると消える（元に戻すと、外へ逃がした形は残らない）
        Assert.True(solver.BodyExtraPlaneCount > 0);
        solver.SetPositions(before);
        Assert.Equal(0, solver.BodyExtraPlaneCount);
        solver.Solve(20);
        for (int i = 0; i < n; i++) Assert.True(Vector3.Distance(before[i], solver.Positions[i]) < 1e-5f, $"粒子 {i}");
    }

    [Fact]
    public void Escape_CapsTheAmount_AndIgnoresTinyPushes()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = cloth.CreateSolver();
        int n = cloth.Model.ParticleCount;
        var push = new float[n];
        int target = -1;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = cloth.Model.RestPositions[i];
            if (MathF.Abs(Angle(p)) < 0.05f && MathF.Abs(p.Y - 0.5f) < 0.006f) target = i;
            else push[i] = 5e-5f;
        }
        Assert.True(target >= 0);
        push[target] = 0.2f;
        // 伸びの上限と周りへ広げるのは外して、量の上限だけを見る（1粒子を 3cm 出すと、周りは大きく伸びる）
        PoseEscapeResult result = PoseEscapeFixer.Fix(cloth, solver, push, new PoseEscapeOptions { MaxPush = 0.03f, MaxStretch = 1f, MaxFold = 1f, FeatherRadius = 0f });
        Assert.Equal(1, result.Particles);
        Assert.Equal(1, result.Capped);
        Assert.Equal(0, result.Limited);
        Assert.Equal(0.03f, result.MaxPush, 6);
    }

    [Fact]
    public void Escape_StopsBeforeStretchingATightCloth()
    {
        // 筒を一周、高さ 10cm の帯で 1cm 外へ。周りが内へ寄れない（元の面より内へ入らない）ので、出した分だけ周の長さが伸びる
        ParticleCloth cloth = Tube();
        int n = cloth.Model.ParticleCount;
        var push = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (MathF.Abs(cloth.Model.RestPositions[i].Y - 0.5f) < 0.05f) push[i] = 0.01f;
        }

        ClothSolver free = cloth.CreateSolver();
        free.KeepOutsideRestSurface = true;
        PoseEscapeResult unlimited = PoseEscapeFixer.Fix(cloth, free, push, new PoseEscapeOptions { MaxStretch = 1f, MaxFold = 1f });
        Assert.InRange(unlimited.MaxPush, 0.0099f, 0.0101f);
        Assert.True(unlimited.MaxStretch > 0.08f, unlimited.ToString());

        // 伸びの上限を外しても、周りの布が寄ってしわになる所で止まる
        ClothSolver foldOnly = cloth.CreateSolver();
        foldOnly.KeepOutsideRestSurface = true;
        PoseEscapeResult byFold = PoseEscapeFixer.Fix(cloth, foldOnly, push, new PoseEscapeOptions { MaxStretch = 1f });
        Assert.True(byFold.Limited > 0, byFold.ToString());
        Assert.True(byFold.MaxPush < 0.0095f, byFold.ToString());

        ClothSolver solver = cloth.CreateSolver();
        solver.KeepOutsideRestSurface = true;
        var options = new PoseEscapeOptions();
        PoseEscapeResult result = PoseEscapeFixer.Fix(cloth, solver, push, options);
        Assert.Equal(1, result.Clusters);
        Assert.Equal(1, result.LimitedClusters);
        Assert.True(result.Limited > 0, result.ToString());
        Assert.InRange(result.MaxPush, 0.0005f, 0.005f);
        Assert.True(result.MaxStretch <= options.MaxStretch + 0.005f, result.ToString());
        // 出した分は元の位置より外（内へは入らない）
        ReadOnlySpan<Vector3> after = solver.Positions;
        for (int i = 0; i < n; i++)
        {
            if (!(push[i] > 0f)) continue;
            float radial = new Vector2(after[i].X, after[i].Z).Length();
            Assert.True(radial > TubeRadius + result.MaxPush - 0.0015f, $"粒子 {i}: {radial:F4}");
        }
    }

    [Fact]
    public void EndToEnd_FindInThePose_EscapeInTheBasePose_ThenThePoseIsClear()
    {
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        var (legVertices, legTriangles) = Cylinder();
        BodySurface body = Body((legVertices, legTriangles));
        var options = new PoseCheckOptions();
        var baseline = new float[evaluator.VertexCount];
        evaluator.MeasureDepth(cloth.Topology.Positions, body, options, baseline);

        // ポーズで脚が +X へ 2cm（布を 5mm 突き抜ける）。布はポーズで動かない（スキニングが単位行列）とみなす
        Vector3[] posedLeg = Moved(legVertices, new Vector3(0.02f, 0f, 0f));
        body.SetVertexPositions(posedLeg);
        var summary = new PoseCheckSummary(evaluator.VertexCount);
        var entry = new PosePenetration { Group = 0, Stage = 1f };
        evaluator.Evaluate(cloth.Topology.Positions, body, options, baseline, entry);
        summary.Add(entry);
        Assert.True(entry.IsPenetrating);

        // ポーズで外向きに動く割合: 布はポーズで動かないので、編集の姿勢で外へ動かした分がそのまま残る（割合 1）
        var normals = new Vector3[evaluator.VertexCount];
        evaluator.ComputeOutwardNormals(cloth.Topology.Positions, normals);
        var probe = new Vector3[evaluator.VertexCount];
        for (int v = 0; v < probe.Length; v++) probe[v] = cloth.Topology.Positions[v] + normals[v] * 0.01f;
        evaluator.MeasureGains(cloth.Topology.Positions, probe, 0.01f, entry);
        Assert.Equal(entry.Vertices.Count, entry.Gains.Count);
        Assert.All(entry.Gains, g => Assert.InRange(g, 0.999f, 1.001f));

        // 元のポーズで、足りるだけ外へ逃がす
        var escapeOptions = new PoseEscapeOptions();
        PoseEscapePlan plan = summary.PlanEscape(escapeOptions.Extra, escapeOptions.MaxPush, escapeOptions.MinGain);
        Assert.Equal(1, plan.PushedRegions);
        Assert.Equal(0, plan.SkippedRegions);
        ClothSolver solver = cloth.CreateSolver();
        var particlePush = new float[cloth.Model.ParticleCount];
        PoseCheckSummary.ToParticles(cloth, plan.WeldedPush, particlePush);
        PoseEscapeResult result = PoseEscapeFixer.Fix(cloth, solver, particlePush, escapeOptions);
        Assert.True(result.Particles > 0, result.ToString());

        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(solver.Positions, welded);
        var after = new PosePenetration { Group = 0, Stage = 1f };
        evaluator.Evaluate(welded, body, options, baseline, after);
        Assert.False(after.IsPenetrating, after.ToString());
    }
}
