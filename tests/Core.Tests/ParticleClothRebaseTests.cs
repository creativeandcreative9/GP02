using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ParticleClothRebaseTests
{
    // 30cm四方の布。上辺をピンで留めて、真ん中を手前に引っ張る
    static ParticleCloth Cloth() => BuildCloth(out _);

    static ParticleCloth BuildCloth(out int[] triangles)
    {
        ProceduralMeshes.Grid(20, 20, 0.3f, 0.3f, out var vertices, out triangles);
        return ParticleCloth.Build(vertices, triangles);
    }

    static ClothSolver Pull(ParticleCloth cloth, float distance)
    {
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.2f };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > 0.3f - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(0.15f, 0.15f, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(center, start + new Vector3(0f, 0f, distance) * f / 10, 0.04f);
            solver.Solve(20);
        }
        solver.Release();
        return solver;
    }

    [Fact]
    public void Rebase_StretchRightAfterCreatingSolverIsZero()
    {
        var cloth = Cloth();
        var pulled = Pull(cloth, 0.05f);
        var positions = pulled.Positions.ToArray();

        var rebased = cloth.Rebase(positions);

        // 元の布のままだと伸びている
        var oldSolver = new ClothSolver(cloth.Model, cloth.Hierarchy);
        oldSolver.SetPositions(positions);
        Assert.True(oldSolver.MeasureMaxStretch() > 0.005f);
        // 新しい静止状態では、ソルバーを作った直後も、引っ張った形を入れた直後も伸びがほぼ 0
        var solver = new ClothSolver(rebased.Model, rebased.Hierarchy);
        Assert.InRange(solver.MeasureMaxStretch(), -1e-4f, 1e-4f);
        solver.SetPositions(positions);
        Assert.InRange(solver.MeasureMaxStretch(), -1e-4f, 1e-4f);
    }

    [Fact]
    public void Rebase_TopologyPositionsMatchDeformWeldedOfOriginal()
    {
        var cloth = Cloth();
        var positions = Pull(cloth, 0.05f).Positions.ToArray();
        var expected = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(positions, expected);

        var rebased = cloth.Rebase(positions);

        Assert.Equal(expected.Length, rebased.Topology.Positions.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Vector3.Distance(expected[i], rebased.Topology.Positions[i]) <= 1e-6f, $"頂点 {i}");
        }
    }

    [Fact]
    public void Rebase_DeformWeldedAtNewRestEqualsTopologyPositions()
    {
        var cloth = Cloth();
        var positions = Pull(cloth, 0.05f).Positions.ToArray();
        var rebased = cloth.Rebase(positions);

        var deformed = new Vector3[rebased.Topology.Positions.Length];
        rebased.DeformWelded(rebased.Model.RestPositions, deformed);

        for (int i = 0; i < deformed.Length; i++) Assert.Equal(rebased.Topology.Positions[i], deformed[i]);
    }

    [Fact]
    public void Rebase_KeepsStructure()
    {
        var cloth = BuildCloth(out var triangles);
        var positions = Pull(cloth, 0.05f).Positions.ToArray();

        var rebased = cloth.Rebase(positions);

        Assert.Equal(cloth.Model.ParticleCount, rebased.Model.ParticleCount);
        Assert.Equal(cloth.Model.StretchPairs, rebased.Model.StretchPairs);
        Assert.Equal(cloth.Model.BendPairs, rebased.Model.BendPairs);
        Assert.Equal(cloth.Spacing, rebased.Spacing);
        Assert.Equal(cloth.ParticleTriangle, rebased.ParticleTriangle);
        Assert.Equal(cloth.ParticleIsland, rebased.ParticleIsland);
        Assert.Equal(cloth.Topology.VertexToWelded, rebased.Topology.VertexToWelded);
        Assert.Equal(cloth.Topology.Triangles, rebased.Topology.Triangles);
        Assert.Equal(cloth.Topology.SourceToTriangle, rebased.Topology.SourceToTriangle);
        Assert.Equal(cloth.Topology.TriangleIsland, rebased.Topology.TriangleIsland);
        Assert.Equal(cloth.Hierarchy.LevelCount, rebased.Hierarchy.LevelCount);
        for (int l = 0; l < cloth.Hierarchy.LevelCount; l++)
        {
            Assert.Equal(cloth.Hierarchy.LevelParticleCount(l), rebased.Hierarchy.LevelParticleCount(l));
        }
        for (int v = 0; v < cloth.Topology.Positions.Length; v++)
        {
            Assert.Equal(cloth.BoundParticleCount(v), rebased.BoundParticleCount(v));
        }
    }

    [Fact]
    public void Rebase_HierarchyDoesNotMoveParticlesAtNewRest()
    {
        var cloth = Cloth();
        var positions = Pull(cloth, 0.05f).Positions.ToArray();
        var rebased = cloth.Rebase(positions);

        // 粗い段の長さが新しい位置から決まっていれば、何も引っ張らずに解いても動かない
        var solver = new ClothSolver(rebased.Model, rebased.Hierarchy);
        solver.SetPositions(positions);
        solver.Solve(20);

        for (int i = 0; i < positions.Length; i++)
        {
            Assert.True(Vector3.Distance(positions[i], solver.Positions[i]) <= 1e-4f, $"粒子 {i}");
        }
    }

    [Fact]
    public void Rebase_CanPullAgainAndBuildBlendShape()
    {
        var cloth = Cloth();
        var rebased = cloth.Rebase(Pull(cloth, 0.05f).Positions);

        var pulled = Pull(rebased, 0.03f);
        int vertexCount = rebased.Topology.VertexToWelded.Length;
        var dv = new Vector3[vertexCount];
        var dn = new Vector3[vertexCount];
        BlendShapeBuilder.Build(rebased, pulled.Positions, dv, dn);

        float maxDelta = 0f;
        for (int v = 0; v < vertexCount; v++)
        {
            Assert.False(float.IsNaN(dv[v].X) || float.IsNaN(dv[v].Y) || float.IsNaN(dv[v].Z), $"差分 {v}");
            Assert.False(float.IsNaN(dn[v].X) || float.IsNaN(dn[v].Y) || float.IsNaN(dn[v].Z), $"法線の差分 {v}");
            maxDelta = MathF.Max(maxDelta, dv[v].Length());
        }
        // 2回目に引っ張った分（3cm）が差分になる。1回目の5cmは含まない
        Assert.InRange(maxDelta, 0.01f, 0.04f);
    }

    [Fact]
    public void Rebase_DoesNotChangeOriginal()
    {
        var cloth = Cloth();
        var restBefore = (Vector3[])cloth.Model.RestPositions.Clone();
        var weldedBefore = (Vector3[])cloth.Topology.Positions.Clone();
        var stretchBefore = (float[])cloth.Model.StretchRestLengths.Clone();

        var rebased = cloth.Rebase(Pull(cloth, 0.05f).Positions);

        Assert.NotSame(cloth.Topology, rebased.Topology);
        Assert.NotSame(cloth.Model, rebased.Model);
        Assert.Equal(restBefore, cloth.Model.RestPositions);
        Assert.Equal(weldedBefore, cloth.Topology.Positions);
        Assert.Equal(stretchBefore, cloth.Model.StretchRestLengths);
    }

    [Fact]
    public void Rebase_RejectsWrongParticleCount()
    {
        var cloth = Cloth();
        Assert.Throws<ArgumentException>(() => cloth.Rebase(new Vector3[3]));
    }
}
