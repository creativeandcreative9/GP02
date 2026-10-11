using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 「ウェイトを体に寄せる」の計算（WeightTransfer・BodyAnchors）と、体どうしの重なりの判定（PoseCheckEvaluator の分け方、BodySurface のレイ）のテスト。
/// 体は円柱（BodySurfaceTests.Lathe）、布はそれを囲む筒。ポーズの代わりに、体の頂点をプログラムで動かす。
/// </summary>
[Collection("DefaultKernel")]
public class WeightTransferTests
{
    const float TubeTop = 0.7f, TubeBottom = 0.3f;

    static (Vector3[] Vertices, int[] Triangles) Cylinder(float cx = 0f, float cz = 0f, float radius = 0.1f, float top = 1f, float bottom = 0f, int rows = 100, int segments = 64)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        BodySurfaceTests.Lathe(v, t, cx, cz, top, bottom, rows, segments, (y, angle) => radius);
        return (v.ToArray(), t.ToArray());
    }

    static (Vector3[] Vertices, int[] Triangles) Join(params (Vector3[] Vertices, int[] Triangles)[] parts)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        foreach (var (vertices, triangles) in parts)
        {
            int start = v.Count;
            v.AddRange(vertices);
            foreach (int k in triangles) t.Add(start + k);
        }
        return (v.ToArray(), t.ToArray());
    }

    static BodySurface Body((Vector3[] Vertices, int[] Triangles) mesh)
    {
        var body = new BodySurface(mesh.Vertices, mesh.Triangles);
        body.EnableSolidIslands();
        return body;
    }

    // 体を囲む筒の布（ふたなし、外向きが表）
    static ParticleCloth Tube(float radius = 0.115f, float centerX = 0f, int segments = 96, int rows = 40, float top = TubeTop, float bottom = TubeBottom)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        for (int k = 0; k <= rows; k++)
        {
            float y = top + (bottom - top) * k / rows;
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

    // 頂点ごとに同じウェイトの集まり
    static BoneWeightSets Uniform(int count, params (int Bone, float Weight)[] weights)
    {
        var offsets = new int[count + 1];
        var bones = new int[count * weights.Length];
        var values = new float[count * weights.Length];
        for (int v = 0; v < count; v++)
        {
            offsets[v + 1] = offsets[v] + weights.Length;
            for (int k = 0; k < weights.Length; k++)
            {
                bones[v * weights.Length + k] = weights[k].Bone;
                values[v * weights.Length + k] = weights[k].Weight;
            }
        }
        return new BoneWeightSets(offsets, bones, values);
    }

    static BoneWeightSets FromFunction(Vector3[] positions, Func<Vector3, (int Bone, float Weight)[]> weight)
    {
        var offsets = new int[positions.Length + 1];
        var bones = new List<int>();
        var values = new List<float>();
        for (int v = 0; v < positions.Length; v++)
        {
            foreach (var (bone, w) in weight(positions[v]))
            {
                bones.Add(bone);
                values.Add(w);
            }
            offsets[v + 1] = bones.Count;
        }
        return new BoneWeightSets(offsets, bones.ToArray(), values.ToArray());
    }

    static float WeightOf(BoneWeightSets sets, int vertex, int bone)
    {
        for (int k = sets.Offsets[vertex]; k < sets.Offsets[vertex + 1]; k++)
        {
            if (sets.Bones[k] == bone) return sets.Weights[k];
        }
        return 0f;
    }

    static float Sum(BoneWeightSets sets, int vertex)
    {
        float total = 0f;
        for (int k = sets.Offsets[vertex]; k < sets.Offsets[vertex + 1]; k++) total += sets.Weights[k];
        return total;
    }

    static int[] Identity(int count)
    {
        var map = new int[count];
        for (int i = 0; i < count; i++) map[i] = i;
        return map;
    }

    sealed class FloatComparer : IEqualityComparer<float>
    {
        readonly float _tolerance;
        public FloatComparer(float tolerance) => _tolerance = tolerance;
        public bool Equals(float x, float y) => MathF.Abs(x - y) <= _tolerance;
        public int GetHashCode(float value) => 0;
    }

    // ---- ボーンの対応 ----

    [Fact]
    public void ResolveBoneMap_UsesTheDirectMatch_OrTheNearestMatchedParent()
    {
        // 体のボーン: 0 Hips、1 Spine（親 0）、2 Chest（親 1）、3 Breast（親 2）、4 LeftUpperLeg（親 0）、5 どこにも付かない根
        int[] parents = { -1, 0, 1, 2, 0, -1 };
        // 服: Hips → 0、Chest → 2、LeftUpperLeg → 1。Spine・Breast・根は服に無い
        int[] direct = { 0, -1, 2, -1, 1, -1 };
        int[] map = WeightTransfer.ResolveBoneMap(direct, parents);
        Assert.Equal(new[] { 0, 0, 2, 2, 1, -1 }, map);

        // 親が子より後ろに並んでいても、たどれる。輪になっていても止まる
        Assert.Equal(new[] { 5, 5 }, WeightTransfer.ResolveBoneMap(new[] { -1, 5 }, new[] { 1, -1 }));
        Assert.Equal(new[] { -1, -1 }, WeightTransfer.ResolveBoneMap(new[] { -1, -1 }, new[] { 1, 0 }));
    }

    // ---- 体の点と、ウェイトの補間 ----

    [Fact]
    public void Anchors_FindTheClosestBodyPoint_AndFollowTheMovedBody()
    {
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        ParticleCloth cloth = Tube();
        Vector3[] clothVertices = cloth.Topology.Positions;
        BodyAnchors anchors = BodyAnchors.Measure(clothVertices, body);
        Assert.Equal(clothVertices.Length, anchors.VertexCount);
        Assert.Equal(clothVertices.Length, anchors.Count);
        for (int v = 0; v < clothVertices.Length; v += 53)
        {
            Assert.True(anchors.Has(v));
            Assert.InRange(anchors.GetDistance(v), 0.0145f, 0.0155f);
            anchors.GetBarycentric(v, out float u, out float b, out float c);
            Assert.InRange(u + b + c, 0.9999f, 1.0001f);
            // 体が動かなければ、付いていった位置は布の頂点そのもの
            Assert.True(anchors.TryFollow(v, body, 0.001f, out Vector3 same, out Vector3 normal));
            Assert.True(Vector3.Distance(same, clothVertices[v]) < 1.5e-3f, $"頂点 {v}: {same} / {clothVertices[v]}");
            Assert.True(Vector3.Dot(normal, Vector3.Normalize(new Vector3(clothVertices[v].X, 0f, clothVertices[v].Z))) > 0.99f);
        }

        // 体を +X へ 3cm 動かすと、付いていった位置も 3cm 動く
        body.SetVertexPositions(Moved(mesh.Vertices, new Vector3(0.03f, 0f, 0f)));
        for (int v = 0; v < clothVertices.Length; v += 53)
        {
            Assert.True(anchors.TryFollow(v, body, 0.001f, out Vector3 moved, out _));
            Assert.True(Vector3.Distance(moved, clothVertices[v] + new Vector3(0.03f, 0f, 0f)) < 1.5e-3f, $"頂点 {v}");
        }

        // 布の外側にある体の点（外向きと同じ側）は使わない
        var outward = new Vector3[clothVertices.Length];
        for (int v = 0; v < outward.Length; v++) outward[v] = -Vector3.Normalize(new Vector3(clothVertices[v].X, 0f, clothVertices[v].Z));
        body.SetVertexPositions(mesh.Vertices);
        Assert.Equal(0, BodyAnchors.Measure(clothVertices, body, BodyAnchors.DefaultMaxDistance, outward).Count);
        Assert.Equal(0, BodyAnchors.Measure(clothVertices, body, 0.01f).Count);
    }

    [Fact]
    public void Transfer_InterpolatesTheBodyWeights_AtTheClosestPoint_AndMapsTheBones()
    {
        // 体: 高さに比例して、下は Hips（体のボーン 0）、上は Spine（体のボーン 1、親 0）
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        var bodySource = FromFunction(mesh.Vertices, p => new[] { (0, 1f - p.Y), (1, p.Y) });
        var bodyToWelded = new int[mesh.Vertices.Length];
        for (int v = 0; v < bodyToWelded.Length; v++) bodyToWelded[v] = body.GetWeldedVertex(v);
        BoneWeightSets bodyWeights = WeightTransfer.AverageToWelded(bodySource, bodyToWelded, body.VertexCount, 2);
        Assert.Equal(body.VertexCount, bodyWeights.VertexCount);

        ParticleCloth cloth = Tube();
        Vector3[] clothVertices = cloth.Topology.Positions;
        int n = clothVertices.Length;
        BodyAnchors anchors = BodyAnchors.Measure(clothVertices, body);
        // 服のボーン: 0 スカートの根、1 Hips、2 Spine
        BoneWeightSets clothWeights = Uniform(n, (0, 1f));
        var influence = new float[n];
        for (int v = 0; v < n; v++) influence[v] = 1f;
        var options = new WeightTransferOptions { Strength = 1f, Radius = 0f, SmoothIterations = 0 };

        WeightTransferResult result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence,
            anchors, body, bodyWeights, new[] { 1, 2 }, 3, options);
        Assert.Equal(n, result.ChangedCount);
        for (int v = 0; v < n; v += 37)
        {
            float y = clothVertices[v].Y;
            Assert.InRange(WeightOf(result.Weights, v, 2), y - 1e-3f, y + 1e-3f);
            Assert.InRange(WeightOf(result.Weights, v, 1), 1f - y - 1e-3f, 1f - y + 1e-3f);
            Assert.Equal(0f, WeightOf(result.Weights, v, 0));
            Assert.InRange(Sum(result.Weights, v), 0.9999f, 1.0001f);
        }

        // 服に Spine が無ければ、対応のある一番近い親（Hips）へ寄せる
        int[] map = WeightTransfer.ResolveBoneMap(new[] { 1, -1 }, new[] { -1, 0 });
        result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, map, 3, options);
        for (int v = 0; v < n; v += 37) Assert.InRange(WeightOf(result.Weights, v, 1), 0.9999f, 1.0001f);

        // どのボーンにも対応しなければ、変えない
        result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { -1, -1 }, 3, options);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(n, result.Unmapped);
        for (int v = 0; v < n; v += 37) Assert.Equal(1f, WeightOf(result.Weights, v, 0));
    }

    [Fact]
    public void Transfer_BlendsWithTheStrength_NormalizesAndKeepsTheBoneLimit()
    {
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        // 体の頂点は 3 本（体のボーン 0, 1, 2）
        var bodySource = Uniform(mesh.Vertices.Length, (0, 0.5f), (1, 0.3f), (2, 0.2f));
        var bodyToWelded = new int[mesh.Vertices.Length];
        for (int v = 0; v < bodyToWelded.Length; v++) bodyToWelded[v] = body.GetWeldedVertex(v);
        BoneWeightSets bodyWeights = WeightTransfer.AverageToWelded(bodySource, bodyToWelded, body.VertexCount, 3);

        ParticleCloth cloth = Tube();
        int n = cloth.Topology.Positions.Length;
        BodyAnchors anchors = BodyAnchors.Measure(cloth.Topology.Positions, body);
        // 服の頂点も 3 本（服のボーン 0, 1, 2。合計が 1 でないものも混ぜる）。体のボーンは服のボーン 3, 4, 5 に対応する
        BoneWeightSets clothWeights = Uniform(n, (0, 0.6f), (1, 0.3f), (2, 0.3f));
        var influence = new float[n];
        for (int v = 0; v < n; v++) influence[v] = 1f;

        var options = new WeightTransferOptions { Strength = 0.5f, Radius = 0f, SmoothIterations = 0, MaxBonesPerVertex = 4 };
        WeightTransferResult result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence,
            anchors, body, bodyWeights, new[] { 3, 4, 5 }, 6, options);
        // 混ぜると 6 本: 0 は 0.25、1・2 は 0.125、3 は 0.25、4 は 0.15、5 は 0.1 → 大きいほうから 4 本（0, 3, 4 と、同じ重みなら番号の小さい 1）を残して合計 1
        for (int v = 0; v < n; v += 41)
        {
            int count = result.Weights.Offsets[v + 1] - result.Weights.Offsets[v];
            Assert.Equal(4, count);
            Assert.InRange(Sum(result.Weights, v), 0.9999f, 1.0001f);
            float kept = 0.25f + 0.25f + 0.15f + 0.125f;
            Assert.InRange(WeightOf(result.Weights, v, 0), 0.25f / kept - 1e-4f, 0.25f / kept + 1e-4f);
            Assert.InRange(WeightOf(result.Weights, v, 3), 0.25f / kept - 1e-4f, 0.25f / kept + 1e-4f);
            Assert.InRange(WeightOf(result.Weights, v, 4), 0.15f / kept - 1e-4f, 0.15f / kept + 1e-4f);
            Assert.InRange(WeightOf(result.Weights, v, 1), 0.125f / kept - 1e-4f, 0.125f / kept + 1e-4f);
            Assert.Equal(0f, WeightOf(result.Weights, v, 2));
            Assert.Equal(0f, WeightOf(result.Weights, v, 5));
            // 大きい順に並んでいる
            for (int k = result.Weights.Offsets[v] + 1; k < result.Weights.Offsets[v + 1]; k++) Assert.True(result.Weights.Weights[k - 1] >= result.Weights.Weights[k]);
        }

        // 上限 2 本
        options.MaxBonesPerVertex = 2;
        result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 3, 4, 5 }, 6, options);
        for (int v = 0; v < n; v += 41)
        {
            Assert.Equal(2, result.Weights.Offsets[v + 1] - result.Weights.Offsets[v]);
            Assert.InRange(WeightOf(result.Weights, v, 0), 0.4999f, 0.5001f);
            Assert.InRange(WeightOf(result.Weights, v, 3), 0.4999f, 0.5001f);
        }

        // 強さ 0 なら何も変えない
        options.Strength = 0f;
        result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 3, 4, 5 }, 6, options);
        Assert.Equal(0, result.ChangedCount);
    }

    [Fact]
    public void Spread_FallsOffSmoothlyAlongTheCloth_AndTheTransferFollowsIt()
    {
        ParticleCloth cloth = Tube(rows: 80, top: 0.9f, bottom: 0.1f);
        Vector3[] positions = cloth.Topology.Positions;
        int n = positions.Length;
        // 対象: 高さ 0.5 の輪
        var seeds = new float[n];
        for (int v = 0; v < n; v++) seeds[v] = MathF.Abs(positions[v].Y - 0.5f) < 1e-4f ? 1f : 0f;
        const float radius = 0.1f;
        float[] influence = WeightTransfer.Spread(n, cloth.Topology.Triangles, positions, seeds, radius);
        for (int v = 0; v < n; v++)
        {
            float d = MathF.Abs(positions[v].Y - 0.5f);
            if (d < 1e-4f) Assert.Equal(1f, influence[v]);
            else if (d >= radius) Assert.Equal(0f, influence[v]);
            else
            {
                // 縦に辺をたどった距離なので、smoothstep の値になる
                float x = d / radius;
                Assert.InRange(influence[v], 1f - x * x * (3f - 2f * x) - 0.03f, 1f - x * x * (3f - 2f * x) + 0.03f);
            }
        }
        // 縦に隣り合う頂点の差は小さい（急に切り替わらない）
        int segments = 96;
        for (int v = 0; v + segments < n; v++) Assert.True(MathF.Abs(influence[v] - influence[v + segments]) < 0.2f, $"頂点 {v}");

        // 半径 0 なら対象だけ
        float[] only = WeightTransfer.Spread(n, cloth.Topology.Triangles, positions, seeds, 0f);
        for (int v = 0; v < n; v++) Assert.Equal(seeds[v], only[v]);

        // 体のウェイトを写すと、写した割合も同じように減る（半径の外は元のまま）
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        var bodyToWelded = new int[mesh.Vertices.Length];
        for (int v = 0; v < bodyToWelded.Length; v++) bodyToWelded[v] = body.GetWeldedVertex(v);
        BoneWeightSets bodyWeights = WeightTransfer.AverageToWelded(Uniform(mesh.Vertices.Length, (0, 1f)), bodyToWelded, body.VertexCount, 1);
        BodyAnchors anchors = BodyAnchors.Measure(positions, body);
        BoneWeightSets clothWeights = Uniform(n, (0, 1f));
        var options = new WeightTransferOptions { Strength = 1f, Radius = radius, SmoothIterations = 2 };
        WeightTransferResult result = WeightTransfer.Transfer(clothWeights, Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 1 }, 2, options);
        for (int v = 0; v < n; v++)
        {
            float d = MathF.Abs(positions[v].Y - 0.5f);
            if (d >= radius)
            {
                Assert.False(result.Changed[v]);
                Assert.Equal(1f, WeightOf(result.Weights, v, 0));
                continue;
            }
            Assert.InRange(WeightOf(result.Weights, v, 1), influence[v] - 1e-3f, influence[v] + 1e-3f);
            Assert.InRange(Sum(result.Weights, v), 0.9999f, 1.0001f);
        }
    }

    [Fact]
    public void Transfer_WorksPerMesh_InTheCombinedVertexOrder()
    {
        // 2つのメッシュをまとめて編集しているとき: 2つ目のメッシュの頂点だけを、まとめた頂点の並びの途中から写す
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        var bodyToWelded = new int[mesh.Vertices.Length];
        for (int v = 0; v < bodyToWelded.Length; v++) bodyToWelded[v] = body.GetWeldedVertex(v);
        BoneWeightSets bodyWeights = WeightTransfer.AverageToWelded(Uniform(mesh.Vertices.Length, (0, 1f)), bodyToWelded, body.VertexCount, 1);
        ParticleCloth cloth = Tube();
        int n = cloth.Topology.Positions.Length;
        BodyAnchors anchors = BodyAnchors.Measure(cloth.Topology.Positions, body);
        var influence = new float[n];
        for (int v = 0; v < n; v++) influence[v] = v % 2 == 0 ? 1f : 0f;
        // まとめた頂点の並び: 前に 5 個（ほかのメッシュ）、その後ろにこのメッシュの頂点
        var vertexToWelded = new int[5 + n];
        for (int k = 0; k < n; k++) vertexToWelded[5 + k] = k;
        var options = new WeightTransferOptions { Strength = 1f, Radius = 0f, SmoothIterations = 0 };
        WeightTransferResult result = WeightTransfer.Transfer(Uniform(n, (0, 1f)), vertexToWelded, 5, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 1 }, 2, options);
        for (int k = 0; k < n; k++)
        {
            Assert.Equal(k % 2 == 0, result.Changed[k]);
            Assert.Equal(k % 2 == 0 ? 1f : 0f, WeightOf(result.Weights, k, 1));
        }
    }

    [Fact]
    public void Proximity_KeepsTheStrengthNearTheBody_AndFadesOutFarAway()
    {
        var options = new WeightTransferOptions { FullDistance = 0.01f, ZeroDistance = 0.05f };
        Assert.Equal(1f, WeightTransfer.Proximity(-0.01f, options));
        Assert.Equal(1f, WeightTransfer.Proximity(0.01f, options));
        Assert.Equal(0f, WeightTransfer.Proximity(0.05f, options));
        Assert.Equal(0f, WeightTransfer.Proximity(0.2f, options));
        Assert.InRange(WeightTransfer.Proximity(0.03f, options), 0.499f, 0.501f);
        float previous = 1f;
        for (float d = 0.01f; d <= 0.05f; d += 0.002f)
        {
            float p = WeightTransfer.Proximity(d, options);
            Assert.True(p <= previous + 1e-6f);
            previous = p;
        }

        // 体から 1.5cm の筒はそのまま、6cm の筒は少しだけ、10cm の筒は変えない
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        var bodyToWelded = new int[mesh.Vertices.Length];
        for (int v = 0; v < bodyToWelded.Length; v++) bodyToWelded[v] = body.GetWeldedVertex(v);
        BoneWeightSets bodyWeights = WeightTransfer.AverageToWelded(Uniform(mesh.Vertices.Length, (0, 1f)), bodyToWelded, body.VertexCount, 1);
        var transferOptions = new WeightTransferOptions { Strength = 1f, Radius = 0f, SmoothIterations = 0 };
        float WeightAt(float radius)
        {
            ParticleCloth cloth = Tube(radius: radius);
            int n = cloth.Topology.Positions.Length;
            BodyAnchors anchors = BodyAnchors.Measure(cloth.Topology.Positions, body, 0.2f);
            var influence = new float[n];
            for (int v = 0; v < n; v++) influence[v] = 1f;
            WeightTransferResult result = WeightTransfer.Transfer(Uniform(n, (0, 1f)), Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 1 }, 2, transferOptions);
            return WeightOf(result.Weights, 0, 1);
        }
        Assert.InRange(WeightAt(0.115f), 0.999f, 1.0001f);
        float middle = WeightAt(0.16f);
        Assert.InRange(middle, 0.05f, 0.95f);
        Assert.Equal(0f, WeightAt(0.2f));
    }

    [Fact]
    public void Smoothing_OnlyWhereTheClosestBodyPointJumps_KeepingSharpJointBlends()
    {
        // 体: 1本の円柱で、高さ 0.5 の前後 5mm で体のボーン 0 から 1 へ切り替わる（体のひざのような急な切り替わり）
        var mesh = Cylinder();
        BodySurface body = Body(mesh);
        var bodySource = FromFunction(mesh.Vertices, p =>
        {
            float t = MathF.Max(0f, MathF.Min(1f, (p.Y - 0.495f) / 0.01f));
            return new[] { (0, 1f - t), (1, t) };
        });
        var bodyToWelded = new int[mesh.Vertices.Length];
        for (int v = 0; v < bodyToWelded.Length; v++) bodyToWelded[v] = body.GetWeldedVertex(v);
        BoneWeightSets bodyWeights = WeightTransfer.AverageToWelded(bodySource, bodyToWelded, body.VertexCount, 2);
        ParticleCloth cloth = Tube(rows: 80, top: 0.9f, bottom: 0.1f);
        Vector3[] positions = cloth.Topology.Positions;
        int n = positions.Length;
        BodyAnchors anchors = BodyAnchors.Measure(positions, body);
        var influence = new float[n];
        for (int v = 0; v < n; v++) influence[v] = 1f;
        var options = new WeightTransferOptions { Strength = 1f, Radius = 0f, SmoothIterations = 3 };
        int Mixed(WeightTransferResult r)
        {
            int count = 0;
            for (int v = 0; v < n; v++)
            {
                float w = WeightOf(r.Weights, v, 1);
                if (w > 0.05f && w < 0.95f) count++;
            }
            return count;
        }
        // 体の点が飛ばない（体に沿った筒）ので、ならさない。切り替わりは 1cm 間隔の筒の1段だけ
        WeightTransferResult kept = WeightTransfer.Transfer(Uniform(n, (0, 1f)), Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 0, 1 }, 2, options, positions);
        Assert.True(Mixed(kept) <= 96, $"混ざった頂点 {Mixed(kept)}");
        // 位置を渡さなければ、全部をならす（切り替わりが広がる）
        WeightTransferResult blurred = WeightTransfer.Transfer(Uniform(n, (0, 1f)), Identity(n), 0, cloth.Topology.Triangles, influence, anchors, body, bodyWeights, new[] { 0, 1 }, 2, options);
        Assert.True(Mixed(blurred) > 2 * 96, $"混ざった頂点 {Mixed(blurred)}");

        // 左右2本の脚（体のボーン 0 と 1）の前の、平らな布: 真ん中で一番近い体の点が隣の脚へ飛ぶので、そこだけなめらかにする
        var legs = Join(Cylinder(cx: -0.1f, radius: 0.05f), Cylinder(cx: 0.1f, radius: 0.05f));
        BodySurface legBody = Body(legs);
        var legSource = FromFunction(legs.Vertices, p => new[] { (p.X < 0f ? 0 : 1, 1f) });
        var legToWelded = new int[legs.Vertices.Length];
        for (int v = 0; v < legToWelded.Length; v++) legToWelded[v] = legBody.GetWeldedVertex(v);
        BoneWeightSets legWeights = WeightTransfer.AverageToWelded(legSource, legToWelded, legBody.VertexCount, 2);
        ProceduralMeshes.Grid(30, 10, 0.3f, 0.2f, out Vector3[] grid, out int[] gridTriangles);
        var sheet = new Vector3[grid.Length];
        for (int v = 0; v < grid.Length; v++) sheet[v] = new Vector3(grid[v].X - 0.15f, 0.4f + grid[v].Y, 0.07f);
        BodyAnchors sheetAnchors = BodyAnchors.Measure(sheet, legBody);
        var sheetInfluence = new float[sheet.Length];
        for (int v = 0; v < sheet.Length; v++) sheetInfluence[v] = 1f;
        // 体からの距離では減らさない（ならすことだけを見る）
        var far = new WeightTransferOptions { Strength = 1f, Radius = 0f, SmoothIterations = 3, FullDistance = 1f, ZeroDistance = 2f };
        WeightTransferResult sheetResult = WeightTransfer.Transfer(Uniform(sheet.Length, (0, 1f)), Identity(sheet.Length), 0, gridTriangles, sheetInfluence,
            sheetAnchors, legBody, legWeights, new[] { 0, 1 }, 2, far, sheet);
        WeightTransferResult sheetSharp = WeightTransfer.Transfer(Uniform(sheet.Length, (0, 1f)), Identity(sheet.Length), 0, gridTriangles, sheetInfluence,
            sheetAnchors, legBody, legWeights, new[] { 0, 1 }, 2, new WeightTransferOptions { Strength = 1f, Radius = 0f, SmoothIterations = 0, FullDistance = 1f, ZeroDistance = 2f }, sheet);
        int mixedSmooth = 0, mixedSharp = 0;
        for (int v = 0; v < sheet.Length; v++)
        {
            float a = WeightOf(sheetResult.Weights, v, 1), b = WeightOf(sheetSharp.Weights, v, 1);
            if (a > 0.05f && a < 0.95f) mixedSmooth++;
            if (b > 0.05f && b < 0.95f) mixedSharp++;
            // 脚の正面（真ん中から離れた所）は、ならしても片方の脚のまま
            if (MathF.Abs(sheet[v].X) > 0.08f) Assert.True(a < 0.05f || a > 0.95f, $"頂点 {v} x={sheet[v].X}: {a}");
        }
        Assert.Equal(0, mixedSharp);
        Assert.True(mixedSmooth >= 2 * 11, $"真ん中でなめらかにならない（混ざった頂点 {mixedSmooth}）");
    }

    // ---- 体どうしの重なり ----

    [Fact]
    public void BodySurface_RayTellsInsideFromOutside_EvenWithinOneConnectedPart()
    {
        BodySurface body = Body(Cylinder());
        Assert.True(body.IsInsideAlongRay(new Vector3(0.05f, 0.5f, 0f), Vector3.UnitX, 0.3f), "円柱の中の点");
        Assert.False(body.IsInsideAlongRay(new Vector3(0.15f, 0.5f, 0f), Vector3.UnitX, 0.3f), "外へ向けて飛ばした、外の点");
        Assert.False(body.IsInsideAlongRay(new Vector3(0.15f, 0.5f, 0f), -Vector3.UnitX, 0.3f), "外から体へ向けて飛ばした（入る面に当たる）");
        Assert.False(body.IsInsideAlongRay(new Vector3(0.05f, 0.5f, 0f), Vector3.UnitX, 0.01f), "届かなければ分からない");
        Assert.True(body.Raycast(new Vector3(0.3f, 0.5f, 0f), -Vector3.UnitX, 1f, out float distance, out int triangle));
        Assert.InRange(distance, 0.199f, 0.2005f);
        Assert.True(triangle >= 0);

        // 動かした体でも、レイの木は付いてくる
        var mesh = Cylinder();
        body.SetVertexPositions(Moved(mesh.Vertices, new Vector3(0.2f, 0f, 0f)));
        Assert.False(body.IsInsideAlongRay(new Vector3(0.05f, 0.5f, 0f), Vector3.UnitX, 0.04f));
        Assert.True(body.IsInsideAlongRay(new Vector3(0.2f, 0.5f, 0f), Vector3.UnitX, 0.3f));
    }

    [Fact]
    public void ArmGoingIntoTheTorso_IsABodyOverlap_ButTheTorsoMovingIsFixableByWeights()
    {
        // 胴体（x=0、半径 0.1）と、離れた腕（x=0.3、半径 0.04）。布は胴体を囲む筒（隙間 1.5cm）
        var torso = Cylinder(radius: 0.1f);
        var arm = Cylinder(cx: 0.3f, radius: 0.04f, top: 0.8f, bottom: 0.2f, rows: 60, segments: 32);
        var joined = Join(torso, arm);
        BodySurface body = Body(joined);
        ParticleCloth cloth = Tube();
        Vector3[] clothVertices = cloth.Topology.Positions;
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        var options = new PoseCheckOptions();
        var baseline = new float[evaluator.VertexCount];
        evaluator.MeasureDepth(clothVertices, body, options, baseline);
        var normals = new Vector3[evaluator.VertexCount];
        evaluator.ComputeOutwardNormals(clothVertices, normals);
        BodyAnchors anchors = BodyAnchors.Measure(clothVertices, body, BodyAnchors.DefaultMaxDistance, normals);
        var restOverlap = new bool[evaluator.VertexCount];
        evaluator.MeasureFollowOverlap(body, anchors, options, restOverlap);
        Assert.DoesNotContain(true, restOverlap);

        // 腕を下ろしたように、腕が胴体に入る（x=0.12。布の +X の側は腕の中）。布がどう付いていっても腕の中なので、体どうしの重なり
        body.SetVertexPositions(Join(torso, Cylinder(cx: 0.12f, radius: 0.04f, top: 0.8f, bottom: 0.2f, rows: 60, segments: 32)).Vertices);
        var plain = new PosePenetration();
        evaluator.Evaluate(clothVertices, body, options, baseline, plain);
        Assert.True(plain.IsPenetrating, "アンカーを渡さなければ、今までどおり貫通として数える");
        var result = new PosePenetration();
        evaluator.Evaluate(clothVertices, body, options, baseline, result, anchors, restOverlap);
        Assert.False(result.IsPenetrating, "体どうしの重なりを貫通として数えた: " + result);
        Assert.True(result.HasOverlap, result.ToString());
        Assert.Equal(1, result.OverlapRegionCount);
        Assert.Equal(plain.PenetratingCount, result.OverlapVertices.Count);
        Assert.InRange(result.OverlapMaxDepth, plain.MaxDepth - 1e-6f, plain.MaxDepth + 1e-6f);
        Assert.NotEmpty(result.OverlapPoints);
        Assert.Empty(result.Points);

        // 胴体のほうが +X へ 3cm 動く（布が付いていけば突き抜けない。ウェイトで直せる）
        body.SetVertexPositions(Join((Moved(torso.Vertices, new Vector3(0.03f, 0f, 0f)), torso.Triangles), arm).Vertices);
        evaluator.Evaluate(clothVertices, body, options, baseline, result, anchors, restOverlap);
        Assert.True(result.IsPenetrating, result.ToString());
        Assert.False(result.HasOverlap, "胴体に付いていけば直る所を、体どうしの重なりとした: " + result);
    }

    [Fact]
    public void BodyPartThatWasNotCovered_IsNotCountedWhenItComesNextToTheCloth()
    {
        // 胴体（半径 0.1）と、布の外にある細い腕（x=0.3、半径 0.02）。布は胴体を囲む筒（半径 0.115）
        var torso = Cylinder(radius: 0.1f);
        (Vector3[] Vertices, int[] Triangles) Arm(float cx) => Cylinder(cx: cx, radius: 0.02f, top: 0.65f, bottom: 0.35f, rows: 30, segments: 24);
        BodySurface body = Body(Join(torso, Arm(0.3f)));
        ParticleCloth cloth = Tube();
        Vector3[] clothVertices = cloth.Topology.Positions;
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        var options = new PoseCheckOptions();
        var baseline = new float[evaluator.VertexCount];
        evaluator.MeasureDepth(clothVertices, body, options, baseline);

        // 編集の姿勢で布に覆われているのは、筒の中の胴体だけ（筒より上・下の胴体は、一番近い布の点が縁。腕は布の外）
        var covered = new bool[body.VertexCount];
        evaluator.MeasureCoveredBodyVertices(clothVertices, body, options, covered);
        int coveredTorso = 0, coveredArm = 0, outsideTube = 0;
        for (int v = 0; v < body.VertexCount; v++)
        {
            Vector3 p = body.GetVertex(v);
            bool arm = p.X > 0.2f;
            if (arm) coveredArm += covered[v] ? 1 : 0;
            else if (p.Y > TubeTop + 0.02f || p.Y < TubeBottom - 0.02f) outsideTube += covered[v] ? 1 : 0;
            else coveredTorso += covered[v] ? 1 : 0;
        }
        Assert.True(coveredTorso > 1000, $"筒の中の胴体 {coveredTorso}");
        Assert.Equal(0, coveredArm);
        Assert.Equal(0, outsideTube);

        // 腕が布のすぐ外に来る（布にも胴体にも入らない）。腕の外側の面は布の外にあり、向きも布と同じなので、覆われていたかを見ないと「布の外へ出た体」と数えてしまう
        body.SetVertexPositions(Join(torso, Arm(0.115f + 0.02f + 0.001f)).Vertices);
        var plain = new PosePenetration();
        evaluator.Evaluate(clothVertices, body, options, baseline, plain);
        Assert.True(plain.IsPenetrating, "このテストの前提: 覆われていたかを見なければ貫通と数える " + plain);
        BodyAnchors anchors = BodyAnchors.Measure(clothVertices, body);
        var result = new PosePenetration();
        evaluator.Evaluate(clothVertices, body, options, baseline, result, anchors, null, covered);
        Assert.False(result.IsPenetrating, "布の外に並んだだけの腕を、貫通と数えた: " + result);
        Assert.False(result.HasOverlap, "布の外に並んだだけの腕を、体どうしの重なりとした: " + result);

        // 覆われていた胴体が布の外へ出るのは、今までどおり貫通
        body.SetVertexPositions(Join((Moved(torso.Vertices, new Vector3(0.03f, 0f, 0f)), torso.Triangles), Arm(0.3f)).Vertices);
        evaluator.Evaluate(clothVertices, body, options, baseline, result, anchors, null, covered);
        Assert.True(result.IsPenetrating, result.ToString());
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate(clothVertices, body, options, baseline, result, anchors, null, new bool[3]));
    }

    [Fact]
    public void OverlapWithinOneConnectedBody_IsFoundByTheRay()
    {
        // 胴体と腕を細い三角形でつないで、1つの島にする（実際のアバターの体は、たいてい1つのつながったメッシュ）
        var torso = Cylinder(radius: 0.1f);
        var arm = Cylinder(cx: 0.3f, radius: 0.04f, top: 0.8f, bottom: 0.2f, rows: 60, segments: 32);
        (Vector3[] Vertices, int[] Triangles) Connected((Vector3[] Vertices, int[] Triangles) a, (Vector3[] Vertices, int[] Triangles) b)
        {
            var j = Join(a, b);
            // 胴体の上の極（0）と次の頂点（1）、腕の上の極
            var t = new List<int>(j.Triangles) { 0, 1, a.Vertices.Length };
            return (j.Vertices, t.ToArray());
        }
        BodySurface body = Body(Connected(torso, arm));
        Assert.Equal(1, body.IslandCount);
        ParticleCloth cloth = Tube();
        Vector3[] clothVertices = cloth.Topology.Positions;
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        var options = new PoseCheckOptions();
        var baseline = new float[evaluator.VertexCount];
        evaluator.MeasureDepth(clothVertices, body, options, baseline);
        BodyAnchors anchors = BodyAnchors.Measure(clothVertices, body);
        var restOverlap = new bool[evaluator.VertexCount];
        evaluator.MeasureFollowOverlap(body, anchors, options, restOverlap);

        body.SetVertexPositions(Connected(torso, Cylinder(cx: 0.12f, radius: 0.04f, top: 0.8f, bottom: 0.2f, rows: 60, segments: 32)).Vertices);
        var result = new PosePenetration();
        evaluator.Evaluate(clothVertices, body, options, baseline, result, anchors, restOverlap);
        Assert.True(result.HasOverlap, result.ToString());
        Assert.True(result.OverlapVertices.Count > result.Vertices.Count, "1つの島の中の重なりを見落とした: " + result);
    }

    // ---- 対象の選び方と、伸び ----

    [Fact]
    public void PlanWeightFix_TargetsTheLowGainAndTooDeepRegions_AndCountsTheOverlaps()
    {
        var summary = new PoseCheckSummary(10);
        var entry = new PosePenetration { Group = 0, HasBody = true, RegionCount = 3, MaxDepth = 0.05f, OverlapRegionCount = 2 };
        void Add(PosePenetration e, int vertex, int region, float depth, float gain)
        {
            e.Vertices.Add(vertex);
            e.Depths.Add(depth);
            e.VertexRegions.Add(region);
            e.Gains.Add(gain);
            while (e.RegionDepths.Count <= region) e.RegionDepths.Add(0f);
            e.RegionDepths[region] = MathF.Max(e.RegionDepths[region], depth);
        }
        // か所 0: 浅い（外へ逃がせる）、か所 1: 動く割合の小さい頂点がある、か所 2: 深い
        Add(entry, 0, 0, 0.004f, 0.9f);
        Add(entry, 1, 0, 0.002f, 1f);
        Add(entry, 2, 1, 0.003f, 0.9f);
        Add(entry, 3, 1, 0.006f, 0.4f);
        Add(entry, 4, 2, 0.05f, 1f);
        Add(entry, 5, 2, 0.001f, 1f);
        entry.OverlapVertices.AddRange(new[] { 7, 8 });
        entry.OverlapDepths.AddRange(new[] { 0.01f, 0.02f });
        summary.Add(entry);

        WeightFixPlan plan = summary.PlanWeightFix(0.002f, 0.03f, 0.7f);
        Assert.Equal(new[] { false, true, true }, plan.TargetRegions[0]);
        Assert.Equal(1, plan.LowGainRegions);
        Assert.Equal(1, plan.TooFarRegions);
        Assert.Equal(2, plan.Regions);
        Assert.Equal(2, plan.OverlapRegions);
        // 外へ逃がせるか所の頂点 0（割合 0.9）は、(1 - 0.9) / (1 - 0.7) だけ対象にする。割合 1 の頂点 1 は対象でない
        Assert.Equal(new float[] { (1f - 0.9f) / (1f - 0.7f), 0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f }, plan.WeldedSeed, new FloatComparer(1e-5f));
        Assert.Equal(5, plan.SeedVertexCount);
        Assert.Equal(2, summary.TotalOverlapRegionCount);
        Assert.Equal(1, summary.OverlapGroupCount);

        // 直したあと: か所 1 の頂点 3 は浅くなり、頂点 2 は直った。か所 2 は残る
        var after = new PoseCheckSummary(10);
        var fixedEntry = new PosePenetration { Group = 0, HasBody = true, RegionCount = 2 };
        Add(fixedEntry, 0, 0, 0.004f, 0.9f);
        Add(fixedEntry, 3, 1, 0.002f, 0.9f);
        Add(fixedEntry, 4, 1, 0.01f, 1f);
        after.Add(fixedEntry);
        PoseFixEffect effect = plan.MeasureEffect(summary, after, 0.02f);
        Assert.Equal(4, effect.TargetPairs);
        Assert.Equal(2, effect.ClearedPairs);
        Assert.Equal((0.003f + 0.006f + 0.05f + 0.001f) / 4f, effect.TargetDepthBefore, 5);
        Assert.Equal((0f + 0.002f + 0.01f + 0f) / 4f, effect.TargetDepthAfter, 5);
        Assert.True(effect.Improved(0.6f));
        Assert.Equal(0.5f, effect.ClearedRatio);
    }

    [Fact]
    public void MeasureStretch_ReportsTheLongestEdgeGrowth_AndAHighPercentile()
    {
        ParticleCloth cloth = Tube();
        var evaluator = PoseCheckEvaluator.FromCloth(cloth);
        Vector3[] rest = cloth.Topology.Positions;
        Assert.Equal(0f, evaluator.MeasureStretch(rest, rest, out float none));
        Assert.Equal(0f, none);

        // 全体を縦に 10% 伸ばす（横の辺は伸びない）。縮めても伸びとは数えない
        var stretched = new Vector3[rest.Length];
        for (int v = 0; v < rest.Length; v++) stretched[v] = new Vector3(rest[v].X, 0.5f + (rest[v].Y - 0.5f) * 1.1f, rest[v].Z);
        float max = evaluator.MeasureStretch(rest, stretched, out float high);
        Assert.InRange(max, 0.099f, 0.101f);
        Assert.InRange(high, 0.05f, 0.101f);
        var squashed = new Vector3[rest.Length];
        for (int v = 0; v < rest.Length; v++) squashed[v] = new Vector3(rest[v].X * 0.9f, rest[v].Y, rest[v].Z * 0.9f);
        Assert.Equal(0f, evaluator.MeasureStretch(rest, squashed, out _), 4);

        // 1つの頂点だけ大きく動いたとき、最大は大きいが、上位 1% の所はほとんど伸びない
        var spike = (Vector3[])rest.Clone();
        spike[100] += new Vector3(0.05f, 0f, 0f);
        float spikeMax = evaluator.MeasureStretch(rest, spike, out float spikeHigh);
        Assert.True(spikeMax > 1f);
        Assert.True(spikeHigh < 0.01f, $"上位 1% {spikeHigh}");

        // 測る頂点を絞ると（体から離れた所だけ）、その外の辺は見ない
        var include = new bool[rest.Length];
        for (int v = 0; v < rest.Length; v++) include[v] = v != 100;
        Assert.Equal(0f, evaluator.MeasureStretch(rest, spike, include, out float masked), 4);
        Assert.Equal(0f, masked, 4);
        Assert.InRange(evaluator.MeasureStretch(rest, stretched, include, out _), 0.099f, 0.101f);
        Assert.Throws<ArgumentException>(() => evaluator.MeasureStretch(rest, rest, new bool[3], out _));
    }

    [Fact]
    public void Plans_WithAnchors_LeaveTheShallowPlacesOfTightClothesToTheWeightFix()
    {
        // 頂点 0〜3 は体（円柱、半径 0.1）から 1cm（ぴったり）、4〜5 は 5cm（ゆったり）
        BodySurface body = Body(Cylinder());
        var points = new Vector3[6];
        for (int v = 0; v < points.Length; v++)
        {
            float radius = v < 4 ? 0.11f : 0.15f;
            float angle = 0.3f * v;
            points[v] = new Vector3(radius * MathF.Cos(angle), 0.5f, radius * MathF.Sin(angle));
        }
        BodyAnchors anchors = BodyAnchors.Measure(points, body);
        Assert.Equal(6, anchors.Count);

        var summary = new PoseCheckSummary(6);
        var entry = new PosePenetration { Group = 0, HasBody = true, RegionCount = 2, MaxDepth = 0.004f };
        void Add(int vertex, int region, float depth)
        {
            entry.Vertices.Add(vertex);
            entry.Depths.Add(depth);
            entry.VertexRegions.Add(region);
            entry.Gains.Add(1f);
            while (entry.RegionDepths.Count <= region) entry.RegionDepths.Add(0f);
            entry.RegionDepths[region] = MathF.Max(entry.RegionDepths[region], depth);
        }
        // どちらも浅く、ポーズで動く割合も 1（外へ逃がせる所）。か所 0 はぴったりの所、か所 1 はゆったりした所
        Add(0, 0, 0.004f);
        Add(1, 0, 0.003f);
        Add(4, 1, 0.004f);
        Add(5, 1, 0.002f);
        summary.Add(entry);

        WeightFixPlan without = summary.PlanWeightFix(0.002f, 0.03f, 0.7f);
        Assert.Equal(0, without.Regions);
        Assert.Equal(0, without.SeedVertexCount);

        WeightFixPlan plan = summary.PlanWeightFix(0.002f, 0.03f, 0.7f, anchors, WeightTransferOptions.DefaultFullDistance);
        Assert.Equal(new[] { true, false }, plan.TargetRegions[0]);
        Assert.Equal(1, plan.TightRegions);
        Assert.Equal(1, plan.Regions);
        Assert.Equal(new float[] { 1f, 1f, 0f, 0f, 0f, 0f }, plan.WeldedSeed, new FloatComparer(1e-6f));
        Assert.Throws<ArgumentException>(() => summary.PlanWeightFix(0.002f, 0.03f, 0.7f, BodyAnchors.Measure(new Vector3[3], body), 0.015f));

        // 外へ逃がすのは、ゆったりした所だけ（ぴったりの所は布に余裕が無いので逃がさず、ウェイトで直す）
        PoseEscapePlan escapeAll = summary.PlanEscape(0.002f, 0.03f, 0.7f);
        Assert.Equal(2, escapeAll.PushedRegions);
        PoseEscapePlan escape = summary.PlanEscape(0.002f, 0.03f, 0.7f, anchors, WeightTransferOptions.DefaultFullDistance);
        Assert.Equal(new[] { false, true }, escape.AllowedRegions[0]);
        Assert.Equal(1, escape.PushedRegions);
        Assert.Equal(1, escape.TightRegions);
        Assert.Equal(1, escape.SkippedRegions);
        Assert.Equal(0f, escape.WeldedPush[0]);
        Assert.Equal(0f, escape.WeldedPush[1]);
        Assert.InRange(escape.WeldedPush[4], 0.0059f, 0.0061f);
        Assert.InRange(escape.WeldedPush[5], 0.0039f, 0.0041f);
    }
}
