using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>選んだ粒子の集合をつまむ（ClothSolver.Grab(int[], BrushMotion, BrushOptions)）のテスト。</summary>
public class SetGrabTests
{
    static readonly MirrorPlane PlaneX = new MirrorPlane(Vector3.Zero, Vector3.UnitX);

    // 頂点がそのまま粒子になる平らな布（XY 平面、x は -0.1〜0.1、1cm 間隔）
    static ClothSolver FlatCloth()
    {
        ProceduralMeshes.Grid(20, 20, 0.2f, 0.2f, out var vertices, out var triangles);
        for (int i = 0; i < vertices.Length; i++) vertices[i] -= new Vector3(0.1f, 0f, 0f);
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles))) { Kernel = null, BrushCompliance = 0f };
    }

    static int[] ParticlesIn(ClothSolver solver, Func<Vector3, bool> predicate)
    {
        var result = new List<int>();
        for (int i = 0; i < solver.Positions.Length; i++) if (predicate(solver.Positions[i])) result.Add(i);
        return result.ToArray();
    }

    static int NearestRest(ClothSolver solver, Vector3 point)
    {
        Vector3[] rest = solver.Model.RestPositions;
        int best = 0;
        for (int i = 1; i < rest.Length; i++)
        {
            if (Vector3.DistanceSquared(rest[i], point) < Vector3.DistanceSquared(rest[best], point)) best = i;
        }
        return best;
    }

    [Fact]
    public void Set_IsGrabbedWithWeightOne_AndOnlyTheSetWhenRadiusIsZero()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > 0.02f && p.X < 0.06f && p.Y > 0.08f && p.Y < 0.12f);
        Assert.True(set.Length > 4);
        solver.Grab(set, new BrushMotion { Translation = new Vector3(0f, 0f, 0.02f) }, null);
        Assert.Equal(set.Length, solver.BrushParticleCount);
        foreach (int i in set)
        {
            Assert.True(solver.IsGrabbed(i));
            Assert.Equal(1f, solver.BrushWeight(i));
        }

        solver.Solve(20);
        foreach (int i in set) Assert.Equal(0.02f, solver.Positions[i].Z, 4);
    }

    [Fact]
    public void Radius_AddsFalloffFromTheEdgeOfTheSet()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => MathF.Abs(p.X - 0.03f) < 0.005f && MathF.Abs(p.Y - 0.1f) < 0.005f);
        Assert.Single(set);
        solver.Grab(set, new BrushMotion(), new BrushOptions { Radius = 0.03f, Falloff = FalloffCurve.Linear });
        int center = set[0];
        Vector3 c = solver.Positions[center];
        int near = solver.FindNearestParticle(c + new Vector3(0.01f, 0f, 0f));
        int far = solver.FindNearestParticle(c + new Vector3(0.02f, 0f, 0f));
        int outside = solver.FindNearestParticle(c + new Vector3(0.05f, 0f, 0f));
        Assert.Equal(1f, solver.BrushWeight(center));
        Assert.Equal(2f / 3f, solver.BrushWeight(near), 3);
        Assert.Equal(1f / 3f, solver.BrushWeight(far), 3);
        Assert.Equal(0f, solver.BrushWeight(outside));
    }

    [Fact]
    public void Mirror_MovesTheMirroredSetWithMirroredMotion()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > 0.03f && p.X < 0.07f && p.Y > 0.08f && p.Y < 0.12f);
        var motion = new BrushMotion { Translation = new Vector3(0.01f, 0.005f, 0.02f) };
        solver.Grab(set, motion, new BrushOptions { Mirror = PlaneX });
        Assert.True(solver.MirrorParticle >= 0);
        Assert.Equal(2 * set.Length, solver.BrushParticleCount);
        solver.Solve(30);

        foreach (int i in set)
        {
            Vector3 rest = solver.Model.RestPositions[i];
            int mirror = NearestRest(solver, new Vector3(-rest.X, rest.Y, rest.Z));
            Assert.True(solver.IsGrabbed(mirror));
            Vector3 moved = solver.Positions[i] - rest;
            Vector3 mirrorMoved = solver.Positions[mirror] - solver.Model.RestPositions[mirror];
            Assert.Equal(moved.X, -mirrorMoved.X, 4);
            Assert.Equal(moved.Y, mirrorMoved.Y, 4);
            Assert.Equal(moved.Z, mirrorMoved.Z, 4);
        }
    }

    [Fact]
    public void SetAcrossTheMidline_SymmetrizesMotionInsteadOfMirroring()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => MathF.Abs(p.X) < 0.025f && p.Y > 0.08f && p.Y < 0.12f);
        solver.Grab(set, new BrushMotion { Translation = new Vector3(0.02f, 0f, 0.01f) }, new BrushOptions { Mirror = PlaneX });
        Assert.Equal(-1, solver.MirrorParticle);
        Assert.Equal(set.Length, solver.BrushParticleCount);
        solver.Solve(20);
        // 鏡の面に垂直な成分（x）は消え、面に沿う成分（z）だけが残る
        foreach (int i in set)
        {
            Assert.Equal(solver.Model.RestPositions[i].X, solver.Positions[i].X, 4);
            Assert.Equal(0.01f, solver.Positions[i].Z, 4);
        }
    }

    [Fact]
    public void SameSet_DoesNotReselect_AndChangedSetDoes()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > 0.02f && p.X < 0.04f && p.Y > 0.09f && p.Y < 0.11f);
        solver.Grab(set, new BrushMotion { Translation = new Vector3(0f, 0f, 0.01f) }, null);
        solver.Solve(10);
        // 同じ集合なら、範囲を選んだときの位置からの動きのまま（選び直すと今の位置からの動きになり、2倍動く）
        solver.Grab((int[])set.Clone(), new BrushMotion { Translation = new Vector3(0f, 0f, 0.01f) }, null);
        solver.Solve(10);
        foreach (int i in set) Assert.Equal(0.01f, solver.Positions[i].Z, 4);

        int[] smaller = set.Take(1).ToArray();
        solver.Grab(smaller, new BrushMotion { Translation = new Vector3(0f, 0f, 0.01f) }, null);
        Assert.Equal(1, solver.BrushParticleCount);
        solver.Solve(10);
        Assert.Equal(0.02f, solver.Positions[smaller[0]].Z, 4);

        // 1点でつまむ版に切り替えても、選び直す
        solver.Grab(set[0], new BrushMotion(), new BrushOptions { Radius = 0f });
        Assert.Equal(1, solver.BrushParticleCount);
        Assert.Equal(set[0], solver.GrabbedParticle);
    }

    [Fact]
    public void InvalidSets_Throw()
    {
        ClothSolver solver = FlatCloth();
        Assert.Throws<ArgumentNullException>(() => solver.Grab((int[])null!, new BrushMotion(), null));
        Assert.Throws<ArgumentException>(() => solver.Grab(new int[0], new BrushMotion(), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.Grab(new[] { -1 }, new BrushMotion(), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.Grab(new[] { solver.Positions.Length }, new BrushMotion(), null));
    }
}
