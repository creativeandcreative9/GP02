using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>重ね着の層どうしの接触（LayerContacts と ClothSolver.SetLayerContacts）のテスト。</summary>
[Collection("DefaultKernel")]
public class LayerContactTests
{
    const float Top = 0.13f, Bottom = 0.30f, Length = 0.35f, TopY = 0.8f, Gap = 0.003f;
    const float Slope = (Bottom - Top) / Length;

    // スカート状の円錐台を2枚（外側と、面の法線方向に 3mm 内側の、丈が 90% のもの）重ねた布。頂点の組は外側 0・内側 1
    static ParticleCloth BuildLayeredCones(bool useGroups = true)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        var groups = new List<int>();
        float radialGap = Gap * MathF.Sqrt(1f + Slope * Slope);
        AddCone(v, t, groups, 0, 120, 35, 1f, 0f);
        AddCone(v, t, groups, 1, 120, 31, 0.9f, radialGap);
        return ParticleCloth.Build(v.ToArray(), t.ToArray(), vertexGroups: useGroups ? groups.ToArray() : null);
    }

    static void AddCone(List<Vector3> v, List<int> t, List<int> groups, int group, int around, int rings, float lengthRatio, float radiusOffset)
    {
        int start = v.Count;
        for (int r = 0; r <= rings; r++)
        {
            float f = lengthRatio * r / rings;
            float radius = Top + (Bottom - Top) * f - radiusOffset;
            for (int a = 0; a < around; a++)
            {
                float angle = 2f * MathF.PI * a / around;
                v.Add(new Vector3(MathF.Cos(angle) * radius, TopY - Length * f, MathF.Sin(angle) * radius));
                groups.Add(group);
            }
        }
        for (int r = 0; r < rings; r++)
        for (int a = 0; a < around; a++)
        {
            int i = start + r * around + a, j = start + r * around + (a + 1) % around;
            int k = i + around, l = j + around;
            t.AddRange(new[] { i, j, k, j, l, k });
        }
    }

    // 上端（ウエスト）を両方の層ともピン留めし、体までの隙間のつもりで、内側へ 5cm まで入ってよいことにする
    static ClothSolver CreateSolver(ParticleCloth cloth, LayerContacts? contacts, float outerAllowance = 0.05f)
    {
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.2f, Kernel = null };
        var allowance = new float[cloth.Model.ParticleCount];
        Array.Fill(allowance, 0.05f);
        int outerIsland = contacts != null && contacts.Count > 0 ? cloth.ParticleIsland[contacts.Outer[0]] : -1;
        for (int i = 0; i < allowance.Length; i++)
        {
            if (cloth.ParticleIsland[i] == outerIsland) allowance[i] = outerAllowance;
        }
        solver.SetRestSurfaceAllowance(allowance);
        solver.SetLayerContacts(contacts);
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.Model.RestPositions[i].Y > TopY - cloth.Spacing) solver.SetPinned(i, true);
        }
        return solver;
    }

    // 前（-Z）の、上から 20cm の高さの外側の布をつまみ、Z 方向に distance（正で軸の方＝内へ）だけ動かして仕上げる
    static int PushFront(ClothSolver solver, float distance, bool settle = true)
    {
        float y = TopY - 0.2f;
        float radius = Top + Slope * 0.2f;
        int center = solver.FindNearestParticle(new Vector3(0f, y, -radius - 0.01f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(center, start + new Vector3(0f, 0f, distance * f / 10f), 0.04f);
            solver.Solve(10);
        }
        if (settle) solver.Settle(0.01f, 400, out _);
        return center;
    }

    // 外側の層が内側の層の内に入った最大の深さ（メートル、正なら突き抜けている）。
    // 外側の層の各頂点について、同じ高さの軸から頂点へ向かう水平なレイが、今の形の内側の層に最初に当たる距離と、頂点の軸からの距離を比べる
    static float MaxPenetration(ParticleCloth cloth, ReadOnlySpan<Vector3> positions, int outerIsland)
    {
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(positions, welded);
        var innerTriangles = new List<int>();
        int[] triangles = cloth.Topology.Triangles;
        for (int t = 0; t < cloth.Topology.TriangleCount; t++)
        {
            if (cloth.Topology.TriangleIsland[t] == outerIsland) continue;
            innerTriangles.Add(triangles[3 * t]);
            innerTriangles.Add(triangles[3 * t + 1]);
            innerTriangles.Add(triangles[3 * t + 2]);
        }
        var bvh = new TriangleBvh(welded, innerTriangles.ToArray());
        // 見た目どうしで比べるため、外側の層も粒子ではなく、粒子についていく頂点（まとめた頂点）の位置で比べる
        var outerVertex = new bool[welded.Length];
        for (int t = 0; t < cloth.Topology.TriangleCount; t++)
        {
            if (cloth.Topology.TriangleIsland[t] != outerIsland) continue;
            for (int c = 0; c < 3; c++) outerVertex[triangles[3 * t + c]] = true;
        }
        Vector3[] rest = cloth.Topology.Positions;
        float worst = float.MinValue;
        for (int v = 0; v < welded.Length; v++)
        {
            if (!outerVertex[v]) continue;
            // 内側の層の裾より下（内側の層が無い所）は除く
            if (rest[v].Y < TopY - 0.85f * Length) continue;
            Vector3 p = welded[v];
            var radial = new Vector3(p.X, 0f, p.Z);
            float radius = radial.Length();
            if (!bvh.Raycast(new Vector3(0f, p.Y, 0f), radial / radius, welded, out float innerRadius, out _)) continue;
            worst = MathF.Max(worst, innerRadius - radius);
        }
        return worst;
    }

    // 内側の層の粒子が、元の形の面より内（mm 単位ではなくメートル）へ動いた最大と、外へ動いた最大
    static void InnerMovement(ParticleCloth cloth, ReadOnlySpan<Vector3> positions, int outerIsland, out float maxInward, out float maxOutward)
    {
        maxInward = 0f;
        maxOutward = 0f;
        Vector3[] rest = cloth.Model.RestPositions;
        Vector3[] normals = cloth.Model.RestNormals;
        for (int j = 0; j < rest.Length; j++)
        {
            if (cloth.ParticleIsland[j] == outerIsland) continue;
            float d = Vector3.Dot(positions[j] - rest[j], normals[j]);
            maxInward = MathF.Max(maxInward, -d);
            maxOutward = MathF.Max(maxOutward, d);
        }
    }

    [Fact]
    public void VertexGroups_KeepCoincidentVerticesOfDifferentGroupsApart()
    {
        ProceduralMeshes.Grid(4, 4, 0.1f, 0.1f, out var vertices, out var triangles);
        Vector3[] both = vertices.Concat(vertices).ToArray();
        int[] bothTriangles = triangles.Concat(triangles.Select(i => i + vertices.Length)).ToArray();

        var welded = MeshTopology.Build(both, bothTriangles);
        Assert.Equal(vertices.Length, welded.Positions.Length);

        int[] groups = Enumerable.Range(0, both.Length).Select(i => i < vertices.Length ? 0 : 1).ToArray();
        var separate = MeshTopology.Build(both, bothTriangles, MeshTopology.DefaultWeldTolerance, groups);
        Assert.Equal(both.Length, separate.Positions.Length);
        Assert.Equal(2, separate.IslandCount);
    }

    [Fact]
    public void Find_PairsOuterLayerWithInnerLayer()
    {
        ParticleCloth cloth = BuildLayeredCones();
        Assert.Equal(2, cloth.Topology.IslandCount);
        LayerContacts contacts = LayerContacts.Find(cloth);

        int outerIsland = cloth.ParticleIsland[contacts.Outer[0]];
        int outerCount = cloth.ParticleIsland.Count(island => island == outerIsland);
        // 外側の層の、内側の層がある高さ（丈の 90%）の粒子のほとんどに相手がいる
        Assert.True(contacts.Count > 0.8f * 0.9f * outerCount, $"組 {contacts.Count} / 外側の粒子 {outerCount}");
        int edgePairs = 0;
        float innerHemY = TopY - 0.9f * Length;
        for (int k = 0; k < contacts.Count; k++)
        {
            int i = contacts.Outer[k], j = contacts.Inner[k];
            Assert.Equal(outerIsland, cloth.ParticleIsland[i]);
            Assert.NotEqual(outerIsland, cloth.ParticleIsland[j]);
            Assert.Equal(MathF.Min(LayerContacts.DefaultMinGap, contacts.RestGaps[k]), contacts.Gaps[k]);
            if (contacts.HasInnerLayer[i])
            {
                Assert.InRange(contacts.RestGaps[k], 0.002f, 0.0045f);
                continue;
            }
            // 内側の層の裾より下にある外側の粒子は、裾の縁の粒子と組になる（内向きのレイは内側の層に当たらない）
            edgePairs++;
            Assert.True(cloth.Model.RestPositions[i].Y < innerHemY + 0.01f);
            Assert.True(cloth.Model.RestPositions[j].Y < innerHemY + 0.015f);
            Assert.True(Vector3.Distance(cloth.Model.RestPositions[i], cloth.Model.RestPositions[j]) <= LayerContacts.DefaultSearchDistance);
        }
        Assert.True(edgePairs > 100, $"裾の縁の組 {edgePairs}");
        Assert.Equal(1.5f * cloth.Spacing, contacts.Reach, 6);
        // 内側の層から内向きには何も無い
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.ParticleIsland[i] != outerIsland) Assert.False(contacts.HasInnerLayer[i]);
        }
    }

    [Fact]
    public void Find_ReturnsNothing_ForSingleLayer()
    {
        ProceduralMeshes.Grid(10, 10, 0.1f, 0.1f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        Assert.Equal(0, LayerContacts.Find(cloth).Count);
    }

    [Fact]
    public void PushingOuterLayerInward_PushesInnerLayer_WithoutPassingThrough()
    {
        ParticleCloth cloth = BuildLayeredCones();
        ClothSolver solver = CreateSolver(cloth, LayerContacts.Find(cloth));
        int center = PushFront(solver, 0.03f);
        int outerIsland = cloth.ParticleIsland[center];

        float penetration = MaxPenetration(cloth, solver.Positions, outerIsland);
        InnerMovement(cloth, solver.Positions, outerIsland, out float innerInward, out _);
        Assert.True(innerInward > 0.015f, $"内側の層が押されていません（最大 {innerInward * 1000f:F1} mm）");
        // 粒子は 1cm 間隔で、間の面は補間なので、1mm 未満のずれは見た目に出ない
        Assert.True(penetration < 0.001f, $"外側の層が内側の層を突き抜けています（{penetration * 1000f:F2} mm）");
        Assert.True(solver.MeasureMaxStretch() < 0.15f);
    }

    [Fact]
    public void WithoutContacts_OuterLayerPassesThroughInnerLayer()
    {
        ParticleCloth cloth = BuildLayeredCones();
        ClothSolver solver = CreateSolver(cloth, LayerContacts.Find(cloth));
        solver.KeepLayersApart = false;
        int center = PushFront(solver, 0.03f);
        int outerIsland = cloth.ParticleIsland[center];

        float penetration = MaxPenetration(cloth, solver.Positions, outerIsland);
        InnerMovement(cloth, solver.Positions, outerIsland, out float innerInward, out _);
        Assert.True(penetration > 0.005f, $"接触の制約なしでも突き抜けていません（{penetration * 1000f:F2} mm）。テストが効いていません");
        Assert.Equal(0f, innerInward);
    }

    [Fact]
    public void PullingOuterLayerOutward_LeavesInnerLayer()
    {
        ParticleCloth cloth = BuildLayeredCones();
        // 外側の層は元の形より内へ入らないようにする（引いた所に寄せられて周りが締まり、内側の層を押すことが無いように）
        ClothSolver solver = CreateSolver(cloth, LayerContacts.Find(cloth), outerAllowance: 0f);
        int center = PushFront(solver, -0.03f);
        Assert.Equal(cloth.ParticleIsland[LayerContacts.Find(cloth).Outer[0]], cloth.ParticleIsland[center]);
        int outerIsland = cloth.ParticleIsland[center];

        float outerMoved = 0f;
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.ParticleIsland[i] == outerIsland) outerMoved = MathF.Max(outerMoved, Vector3.Distance(solver.Positions[i], cloth.Model.RestPositions[i]));
        }
        InnerMovement(cloth, solver.Positions, outerIsland, out float innerInward, out float innerOutward);
        Assert.True(outerMoved > 0.02f);
        Assert.True(innerOutward < 1e-4f && innerInward < 1e-4f, $"内側の層が動いています（外へ {innerOutward * 1000f:F2} mm、内へ {innerInward * 1000f:F2} mm）");
        Assert.True(MaxPenetration(cloth, solver.Positions, outerIsland) < 0.0005f);
    }

    [Fact]
    public void GrabbingOuterLayer_AlsoSolvesLinkedInnerLayer()
    {
        ParticleCloth cloth = BuildLayeredCones();
        int n = cloth.Model.ParticleCount;
        ClothSolver solver = CreateSolver(cloth, LayerContacts.Find(cloth));
        Assert.True(solver.LayerContactCount > 0);
        int center = solver.FindNearestParticle(new Vector3(0f, TopY - 0.2f, -0.3f));
        solver.Grab(center, solver.Positions[center], 0.03f);
        solver.Solve(1);
        int outerCount = cloth.ParticleIsland.Count(island => island == cloth.ParticleIsland[center]);
        int pinned = Enumerable.Range(0, n).Count(solver.IsPinned);
        Assert.Equal(n - pinned, solver.ActiveParticleCount);

        solver.KeepLayersApart = false;
        solver.Solve(1);
        Assert.True(solver.ActiveParticleCount < outerCount);
    }

    [Fact]
    public void ContactPartners_FollowTheOuterParticle()
    {
        ParticleCloth cloth = BuildLayeredCones();
        ClothSolver solver = CreateSolver(cloth, LayerContacts.Find(cloth));
        PushFront(solver, 0.03f);
        // 探し直した相手は、外側の粒子の今の位置の近くにある（最後の Solve の始めに探したので、その後の動きの分は許す）
        LayerContacts original = LayerContacts.Find(cloth);
        int changed = 0;
        for (int k = 0; k < solver.LayerContactCount; k++)
        {
            Vector3 p = solver.Positions[solver.LayerContactOuter(k)];
            float now = Vector3.Distance(p, solver.Positions[solver.LayerContactInner(k)]);
            float before = Vector3.Distance(p, solver.Positions[original.Inner[k]]);
            Assert.True(now <= before + 0.002f, $"組 {k}: 今の相手 {now * 1000f:F2} mm / 元の相手 {before * 1000f:F2} mm");
            if (solver.LayerContactInner(k) != original.Inner[k]) changed++;
        }
        Assert.True(changed > 0, "押して層がずれたのに、相手が変わっていません");
    }

    [Theory]
    [InlineData(0, ClothSolver.DefaultBrushCompliance)]
    [InlineData(2, ClothSolver.DefaultBrushCompliance)]
    [InlineData(2, 0f)]
    public void Kernel_ReceivingContactData_MatchesBuiltInPath(int coarseLevels, float brushCompliance)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        var groups = new List<int>();
        float radialGap = Gap * MathF.Sqrt(1f + Slope * Slope);
        AddCone(v, t, groups, 0, 120, 35, 1f, 0f);
        AddCone(v, t, groups, 1, 120, 31, 0.9f, radialGap);
        var cloth = ParticleCloth.Build(v.ToArray(), t.ToArray(), coarseLevels: coarseLevels, vertexGroups: groups.ToArray());
        LayerContacts contacts = LayerContacts.Find(cloth);

        Vector3[] Run(IClothSolverKernel? kernel)
        {
            ClothSolver solver = CreateSolver(cloth, contacts);
            solver.Kernel = kernel;
            solver.BrushCompliance = brushCompliance;
            // 仕上げまで回すと、Vector3 の演算と成分ごとの計算の丸めの差が、接触の有無の切り替わりで少しずつ広がるので、短めにする
            PushFront(solver, 0.03f, settle: false);
            solver.Release();
            solver.Solve(10);
            return solver.Positions.ToArray();
        }

        var reference = new ClothSolverKernelTests.ReferenceKernel();
        Vector3[] expected = Run(null);
        Vector3[] actual = Run(reference);
        Assert.True(reference.IterateCalls > 0);
        float moved = 0f;
        for (int i = 0; i < expected.Length; i++) moved = MathF.Max(moved, Vector3.Distance(expected[i], cloth.Model.RestPositions[i]));
        Assert.True(moved > 0.01f);
        for (int i = 0; i < expected.Length; i++) Assert.True(Vector3.Distance(expected[i], actual[i]) < 1e-5f, $"粒子 {i}");
    }
}
