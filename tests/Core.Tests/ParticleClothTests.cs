using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ParticleClothTests
{
    const float Spacing = 0.01f;

    // 50cm四方の布。メッシュの細かさは 5cm（粒子の間隔より粗い）
    static ParticleCloth CoarseSquare(out Vector3[] vertices, out int[] triangles)
    {
        ProceduralMeshes.Grid(10, 10, 0.5f, 0.5f, out vertices, out triangles);
        return ParticleCloth.Build(vertices, triangles, Spacing);
    }

    [Fact]
    public void Sample_KeepsMinimumSpacing()
    {
        var cloth = CoarseSquare(out _, out _);
        var particles = cloth.Model.RestPositions;

        for (int i = 0; i < particles.Length; i++)
        for (int j = i + 1; j < particles.Length; j++)
        {
            Assert.True(Vector3.Distance(particles[i], particles[j]) >= Spacing * 0.999f, $"粒子 {i} と {j}");
        }
    }

    [Fact]
    public void Sample_CountMatchesSpacing()
    {
        var cloth = CoarseSquare(out _, out _);

        // 最小間隔 1cm のポアソンディスクは、0.25㎡ に おおよそ 1,300〜2,000 個
        Assert.InRange(cloth.Model.ParticleCount, 1300, 2000);
    }

    [Fact]
    public void Sample_DoesNotDependOnMeshResolution()
    {
        ProceduralMeshes.Grid(10, 10, 0.5f, 0.5f, out var coarseVertices, out var coarseTriangles);
        ProceduralMeshes.Grid(100, 100, 0.5f, 0.5f, out var fineVertices, out var fineTriangles);

        var coarse = ParticleCloth.Build(coarseVertices, coarseTriangles, Spacing);
        var fine = ParticleCloth.Build(fineVertices, fineTriangles, Spacing);

        // メッシュの頂点数は100倍違っても、粒子の数はほぼ同じ
        float ratio = (float)fine.Model.ParticleCount / coarse.Model.ParticleCount;
        Assert.InRange(ratio, 0.9f, 1.1f);
    }

    [Fact]
    public void Sample_IsDeterministic()
    {
        var a = CoarseSquare(out _, out _);
        var b = CoarseSquare(out _, out _);

        Assert.Equal(a.Model.RestPositions, b.Model.RestPositions);
        Assert.Equal(a.Model.StretchPairs, b.Model.StretchPairs);
    }

    [Fact]
    public void Build_EveryParticleHasNeighbors()
    {
        var cloth = CoarseSquare(out _, out _);
        var degree = new int[cloth.Model.ParticleCount];
        foreach (int p in cloth.Model.StretchPairs) degree[p]++;

        Assert.All(degree, d => Assert.True(d >= 2, $"つながりの数: {d}"));
    }

    [Fact]
    public void Build_DoesNotConnectDisconnectedLayers()
    {
        // 5mm 離した2枚。粒子の間隔（1cm）より近いが、表面でつながっていない
        var (vertices, triangles) = ClothModelTests.TwoLayers(10, 0.005f);

        var cloth = ParticleCloth.Build(vertices, triangles, Spacing);

        int[] island = cloth.Topology.TriangleIsland;
        int[] pTri = cloth.ParticleTriangle;
        var pairs = cloth.Model.StretchPairs.Concat(cloth.Model.BendPairs).ToArray();
        for (int k = 0; k < pairs.Length; k += 2)
        {
            Assert.Equal(island[pTri[pairs[k]]], island[pTri[pairs[k + 1]]]);
        }
        // どちらの層にも粒子がある
        Assert.Contains(pTri, t => island[t] == 0);
        Assert.Contains(pTri, t => island[t] == 1);
    }

    [Fact]
    public void Build_EveryVertexIsBound()
    {
        var cloth = CoarseSquare(out _, out _);

        for (int v = 0; v < cloth.Topology.Positions.Length; v++)
        {
            Assert.True(cloth.BoundParticleCount(v) > 0, $"頂点 {v}");
        }
    }

    [Fact]
    public void Build_TinyPartGetsOneParticle()
    {
        // 粒子の間隔より小さい三角形（ボタンのような小さな部品）
        var vertices = new[] { Vector3.Zero, new Vector3(0.002f, 0f, 0f), new Vector3(0f, 0.002f, 0f) };

        var cloth = ParticleCloth.Build(vertices, new[] { 0, 1, 2 }, Spacing);

        Assert.Equal(1, cloth.Model.ParticleCount);
        Assert.All(Enumerable.Range(0, 3), v => Assert.Equal(1, cloth.BoundParticleCount(v)));
    }

    [Fact]
    public void DeformWelded_AtRest_ReturnsOriginalVertices()
    {
        var cloth = CoarseSquare(out _, out _);
        var welded = new Vector3[cloth.Topology.Positions.Length];

        cloth.DeformWelded(cloth.Model.RestPositions, welded);

        Assert.Equal(cloth.Topology.Positions, welded);
    }

    [Fact]
    public void DeformWelded_FollowsUniformTranslation()
    {
        var cloth = CoarseSquare(out _, out _);
        var offset = new Vector3(0.1f, -0.2f, 0.3f);
        var moved = cloth.Model.RestPositions.Select(p => p + offset).ToArray();
        var welded = new Vector3[cloth.Topology.Positions.Length];

        cloth.DeformWelded(moved, welded);

        for (int v = 0; v < welded.Length; v++)
        {
            Assert.True(Vector3.Distance(cloth.Topology.Positions[v] + offset, welded[v]) < 1e-5f, $"頂点 {v}");
        }
    }

    [Fact]
    public void Edit_FineMeshFollowsParticlesWithoutStretching()
    {
        // 1m四方・上端をピン留め・メッシュは 1cm 間隔（1万頂点）
        ProceduralMeshes.Grid(100, 100, 1f, 1f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles, Spacing);
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.15f };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 1f - Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.5f, 0.3f, 0f));
        Vector3 start = solver.Positions[center];

        for (int f = 1; f <= 15; f++)
        {
            solver.Grab(center, start + new Vector3(0f, 0.02f, 0.1f) * f / 15, 0.03f);
            solver.Solve(40);
        }

        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(solver.Positions, welded);
        // メッシュの辺の伸び（95パーセンタイル）が小さい
        var stretch = new List<float>();
        var tris = cloth.Topology.Triangles;
        for (int t = 0; t < tris.Length; t += 3)
        for (int e = 0; e < 3; e++)
        {
            int a = tris[t + e], b = tris[t + (e + 1) % 3];
            stretch.Add(Vector3.Distance(welded[a], welded[b]) / Vector3.Distance(cloth.Topology.Positions[a], cloth.Topology.Positions[b]) - 1f);
        }
        stretch.Sort();
        float p95 = stretch[(int)(stretch.Count * 0.95f)];
        Assert.True(p95 < 0.05f, $"伸び（95パーセンタイル）: {p95:P2}");
        // つまんだ所は、ちゃんと手前に動いている
        Assert.True(solver.Positions[center].Z > 0.08f, $"つまんだ点のZ: {solver.Positions[center].Z}");
    }
}

public class ClothHierarchyTests
{
    static ParticleCloth SkirtSizedCloth()
    {
        // スカート1枚に近い大きさ（約0.4㎡）
        ProceduralMeshes.Grid(100, 100, 0.63f, 0.63f, out var vertices, out var triangles);
        return ParticleCloth.Build(vertices, triangles);
    }

    static ClothSolver PullHem(ParticleCloth cloth, ClothHierarchy? hierarchy, int iterations, int settleSolves)
    {
        var solver = new ClothSolver(cloth.Model, hierarchy) { InfluenceRadius = 0.15f };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 0.63f - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.315f, 0.3f, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 15; f++)
        {
            solver.Grab(center, start + new Vector3(0f, 0.02f, 0.1f) * f / 15, 0.03f);
            solver.Solve(iterations);
        }
        // つまんだまま、同じ目標で解き続ける（手を止めている間の仕上げ）
        for (int s = 0; s < settleSolves; s++) solver.Solve(iterations);
        return solver;
    }

    static float Percentile99Stretch(ClothSolver solver)
    {
        var model = solver.Model;
        var stretch = new List<float>();
        for (int k = 0; k < model.StretchRestLengths.Length; k++)
        {
            float length = Vector3.Distance(solver.Positions[model.StretchPairs[2 * k]], solver.Positions[model.StretchPairs[2 * k + 1]]);
            stretch.Add(length / model.StretchRestLengths[k] - 1f);
        }
        stretch.Sort();
        return stretch[(int)((stretch.Count - 1) * 0.99f)];
    }

    [Fact]
    public void Build_CreatesCoarserLevels()
    {
        var cloth = SkirtSizedCloth();
        var hierarchy = cloth.Hierarchy;

        Assert.Equal(2, hierarchy.LevelCount);
        Assert.Equal(0.04f, hierarchy.LevelSpacing(0), 4);
        Assert.Equal(0.02f, hierarchy.LevelSpacing(1), 4);
        Assert.True(hierarchy.LevelParticleCount(0) < hierarchy.LevelParticleCount(1));
        Assert.True(hierarchy.LevelParticleCount(1) < cloth.Model.ParticleCount);
    }

    [Fact]
    public void Build_EveryParticleOutsideALevelHasParents()
    {
        var cloth = SkirtSizedCloth();
        var hierarchy = cloth.Hierarchy;

        for (int level = 0; level < hierarchy.LevelCount; level++)
        {
            int withParents = 0;
            for (int p = 0; p < cloth.Model.ParticleCount; p++)
            {
                if (hierarchy.ParentCount(level, p) > 0) withParents++;
            }
            // この段に含まれない粒子は、すべて親を持つ
            Assert.Equal(cloth.Model.ParticleCount - hierarchy.LevelParticleCount(level), withParents);
        }
    }

    [Fact]
    public void Solve_WithHierarchy_StretchesLessThanWithout()
    {
        var cloth = SkirtSizedCloth();

        float with = Percentile99Stretch(PullHem(cloth, cloth.Hierarchy, 40, 0));
        float without = Percentile99Stretch(PullHem(cloth, null, 40, 0));

        Assert.True(with < without, $"粗い段あり: {with:P2}, なし: {without:P2}");
    }

    [Fact]
    public void Solve_KeepsImprovingWhileHeld()
    {
        var cloth = SkirtSizedCloth();

        float justAfterDrag = Percentile99Stretch(PullHem(cloth, cloth.Hierarchy, 40, 0));
        float afterSettling = Percentile99Stretch(PullHem(cloth, cloth.Hierarchy, 40, 20));

        // 制約どうしが矛盾しているのではなく、解き続ければ伸びは減っていく
        Assert.True(afterSettling < justAfterDrag * 0.5f, $"直後: {justAfterDrag:P2}, 仕上げ後: {afterSettling:P2}");
        Assert.True(afterSettling < 0.03f, $"仕上げ後: {afterSettling:P2}");
    }
}
