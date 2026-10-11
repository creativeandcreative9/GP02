using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 軸ごとの拡大縮小（BrushMotion.ScaleAxes / ScaleFrame）と、集合の範囲を調べる CollectSetRange のテスト。
/// 「点を選んで動かす」道具の計算の土台。
/// </summary>
public class AxisScaleSetTests
{
    const float Tolerance = 1e-5f;
    static readonly MirrorPlane PlaneX = new MirrorPlane(Vector3.Zero, Vector3.UnitX);

    // 頂点がそのまま粒子になる平らな布（XY 平面、x は -0.1〜0.1、y は 0〜0.2、1cm 間隔）
    static ClothSolver FlatCloth()
    {
        ProceduralMeshes.Grid(20, 20, 0.2f, 0.2f, out var vertices, out var triangles);
        for (int i = 0; i < vertices.Length; i++) vertices[i] -= new Vector3(0.1f, 0f, 0f);
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles))) { Kernel = null, BrushCompliance = 0f, KeepOutsideRestSurface = false };
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

    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = Tolerance)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"期待 {expected} 実際 {actual}");
    }

    static BrushMotion SampleMotion()
    {
        return new BrushMotion(new Vector3(0.01f, -0.02f, 0.03f), Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 0.7f), 1.1f, new Vector3(0.02f, 0.1f, 0.01f))
        {
            ScaleAxes = new Vector3(1.5f, 0.7f, 1.2f),
            ScaleFrame = Quaternion.CreateFromYawPitchRoll(-0.5f, 0.35f, 0.6f),
        };
    }

    [Fact]
    public void ScaleAxes_DefaultIsOne_EvenForDefaultStruct()
    {
        BrushMotion motion = default;
        Assert.Equal(Vector3.One, motion.ScaleAxes);
        Assert.Equal(Quaternion.Identity, motion.ScaleFrame);
        Assert.False(motion.HasAxisScale);
        Assert.False(new BrushMotion(Vector3.Zero, Quaternion.Identity, 2f, Vector3.Zero).HasAxisScale);
        Assert.False(new BrushMotion { ScaleAxes = Vector3.One }.HasAxisScale);
        Assert.True(new BrushMotion { ScaleAxes = new Vector3(1f, 2f, 1f) }.HasAxisScale);
    }

    [Fact]
    public void Apply_ScalesAlongTheAxesOfTheFrame()
    {
        // F を Z まわり 90 度にすると、F の X 軸はワールドの Y 軸になる
        var motion = new BrushMotion
        {
            ScaleAxes = new Vector3(2f, 1f, 1f),
            ScaleFrame = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        };
        AssertClose(new Vector3(0f, 2f, 0f), motion.Apply(new Vector3(0f, 1f, 0f)));
        AssertClose(new Vector3(1f, 0f, 0f), motion.Apply(new Vector3(1f, 0f, 0f)));
        AssertClose(new Vector3(0f, 0f, 3f), motion.Apply(new Vector3(0f, 0f, 3f)));

        // 斜めの点は、F の軸の成分ごとに拡大される
        AssertClose(new Vector3(1f, 2f, 0f), motion.Apply(new Vector3(1f, 1f, 0f)));
    }

    [Fact]
    public void Apply_ScalesTheFrameAxesByTheirFactors_ThenRotatesAboutThePivot()
    {
        BrushMotion motion = SampleMotion();
        Quaternion frame = Quaternion.Normalize(motion.ScaleFrame);
        Quaternion rotation = Quaternion.Normalize(motion.Rotation);
        Vector3[] axes = { Vector3.Transform(Vector3.UnitX, frame), Vector3.Transform(Vector3.UnitY, frame), Vector3.Transform(Vector3.UnitZ, frame) };
        float[] factors = { motion.ScaleAxes.X * motion.Scale, motion.ScaleAxes.Y * motion.Scale, motion.ScaleAxes.Z * motion.Scale };
        for (int k = 0; k < 3; k++)
        {
            Vector3 expected = motion.Pivot + Vector3.Transform(axes[k] * factors[k], rotation) + motion.Translation;
            AssertClose(expected, motion.Apply(motion.Pivot + axes[k]));
        }
        // 中心は、平行移動だけ動く
        AssertClose(motion.Pivot + motion.Translation, motion.Apply(motion.Pivot));
    }

    [Fact]
    public void Apply_WithoutAxisScale_IsTheUniformFormula()
    {
        var motion = new BrushMotion(new Vector3(0.01f, 0f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), 1.3f, new Vector3(0.05f, 0.1f, 0f));
        Vector3 p = new Vector3(0.07f, 0.12f, 0.02f);
        Vector3 expected = motion.Pivot + Vector3.Transform((p - motion.Pivot) * 1.3f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)) + motion.Translation;
        Assert.Equal(expected, motion.Apply(p));

        // 軸の倍率がすべて同じなら、一様な拡大と同じ
        var uniform = new BrushMotion(motion.Translation, motion.Rotation, 1f, motion.Pivot) { ScaleAxes = new Vector3(1.3f, 1.3f, 1.3f), ScaleFrame = Quaternion.CreateFromYawPitchRoll(1f, 2f, 3f) };
        AssertClose(expected, uniform.Apply(p));
    }

    [Fact]
    public void Solver_MovesTheSetToWhereApplySays_ForNonUniformScaleOnRotatedAxes()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > -0.03f && p.X < 0.05f && p.Y > 0.06f && p.Y < 0.14f);
        Vector3[] start = set.Select(i => solver.Positions[i]).ToArray();
        BrushMotion motion = SampleMotion();
        solver.Grab(set, motion, null);
        solver.Solve(30);
        for (int k = 0; k < set.Length; k++) AssertClose(motion.Apply(start[k]), solver.Positions[set[k]], 2e-5f);
    }

    [Fact]
    public void Solver_AxisScale_OnlyMovesAlongTheFrameAxis()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > -0.04f && p.X < 0.04f && p.Y > 0.06f && p.Y < 0.14f);
        Vector3 pivot = new Vector3(0f, 0.1f, 0f);
        // F の X 軸がワールドの Y 軸（Z まわり 90 度）。その軸だけ 1.5 倍
        var motion = new BrushMotion { Pivot = pivot, ScaleAxes = new Vector3(1.5f, 1f, 1f), ScaleFrame = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f) };
        Vector3[] start = set.Select(i => solver.Positions[i]).ToArray();
        solver.Grab(set, motion, null);
        solver.Solve(30);
        for (int k = 0; k < set.Length; k++)
        {
            Vector3 p = solver.Positions[set[k]];
            Assert.Equal(start[k].X, p.X, 4);
            Assert.Equal(pivot.Y + (start[k].Y - pivot.Y) * 1.5f, p.Y, 4);
        }
    }

    [Fact]
    public void DefaultScaleAxes_GiveExactlyTheSameResultAsBefore()
    {
        BrushMotion Make(bool explicitAxes)
        {
            var m = new BrushMotion(new Vector3(0.01f, 0.005f, 0.02f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f), 1.2f, new Vector3(0.01f, 0.1f, 0f));
            if (explicitAxes) m.ScaleAxes = Vector3.One;
            return m;
        }

        Vector3[] Run(bool explicitAxes)
        {
            ClothSolver solver = FlatCloth();
            int[] set = ParticlesIn(solver, p => p.X > -0.02f && p.X < 0.05f && p.Y > 0.07f && p.Y < 0.13f);
            solver.Grab(set, Make(explicitAxes), new BrushOptions { Radius = 0.03f, Falloff = FalloffCurve.Smooth });
            solver.Solve(20);
            return solver.Positions.ToArray();
        }

        Vector3[] without = Run(false);
        Vector3[] with = Run(true);
        for (int i = 0; i < without.Length; i++) Assert.Equal(without[i], with[i]);
    }

    [Fact]
    public void Mirror_MovesTheMirroredSetWithTheMirroredAxisScale()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > 0.03f && p.X < 0.07f && p.Y > 0.06f && p.Y < 0.14f);
        BrushMotion motion = SampleMotion();
        Vector3[] rest = solver.Model.RestPositions;
        solver.Grab(set, motion, new BrushOptions { Mirror = PlaneX });
        Assert.True(solver.MirrorParticle >= 0);
        solver.Solve(30);

        foreach (int i in set)
        {
            int mirror = NearestRest(solver, PlaneX.Reflect(rest[i]));
            Assert.True(solver.IsGrabbed(mirror));
            // 鏡像の側は 鏡映 ∘ motion ∘ 鏡映 で動く
            Vector3 expected = PlaneX.Reflect(motion.Apply(PlaneX.Reflect(rest[mirror])));
            AssertClose(expected, solver.Positions[mirror], 5e-5f);
            AssertClose(motion.Apply(rest[i]), solver.Positions[i], 5e-5f);
        }
    }

    [Fact]
    public void AcrossTheMidline_KeepsAxisScaleAlignedWithTheMirrorNormal()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => MathF.Abs(p.X) < 0.025f && p.Y > 0.08f && p.Y < 0.12f);
        Vector3[] start = set.Select(i => solver.Positions[i]).ToArray();
        // ワールドの X（鏡の面の法線）だけを 1.5 倍。鏡映と入れ替えても変わらないので、そのまま残る
        var motion = new BrushMotion { Pivot = new Vector3(0f, 0.1f, 0f), ScaleAxes = new Vector3(1.5f, 1f, 1f) };
        solver.Grab(set, motion, new BrushOptions { Mirror = PlaneX });
        Assert.Equal(-1, solver.MirrorParticle);
        solver.Solve(30);
        for (int k = 0; k < set.Length; k++)
        {
            Assert.Equal(start[k].X * 1.5f, solver.Positions[set[k]].X, 4);
            Assert.Equal(start[k].Y, solver.Positions[set[k]].Y, 4);
        }
    }

    [Fact]
    public void AcrossTheMidline_MisalignedAxisScaleBecomesUniformSoItStaysSymmetric()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => MathF.Abs(p.X) < 0.025f && p.Y > 0.08f && p.Y < 0.12f);
        Vector3[] start = set.Select(i => solver.Positions[i]).ToArray();
        // 軸が鏡の面の法線に対して 45 度。そのままでは左右が食い違うので、倍率の平均（4/3）の一様な拡大になる
        var motion = new BrushMotion
        {
            Pivot = new Vector3(0f, 0.1f, 0f),
            ScaleAxes = new Vector3(2f, 1f, 1f),
            ScaleFrame = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f),
        };
        solver.Grab(set, motion, new BrushOptions { Mirror = PlaneX });
        solver.Solve(30);
        float expectedScale = (2f + 1f + 1f) / 3f;
        for (int k = 0; k < set.Length; k++)
        {
            Assert.Equal(start[k].X * expectedScale, solver.Positions[set[k]].X, 4);
            Assert.Equal(0.1f + (start[k].Y - 0.1f) * expectedScale, solver.Positions[set[k]].Y, 4);
        }
    }

    // ---- CollectSetRange ----

    static int[] Collect(ClothSolver solver, int[] set, BrushOptions? options, out float[] weights, int max = 100000)
    {
        var indices = new int[solver.Positions.Length];
        weights = new float[solver.Positions.Length];
        int count = solver.CollectSetRange(set, options, indices, weights, Math.Min(max, indices.Length));
        weights = weights.Take(count).ToArray();
        return indices.Take(count).ToArray();
    }

    [Fact]
    public void CollectSetRange_SetHasWeightOne_InsideRadiusFalloff_OutsideAbsent()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => MathF.Abs(p.X - 0.03f) < 0.005f && MathF.Abs(p.Y - 0.1f) < 0.005f);
        Assert.Single(set);
        var options = new BrushOptions { Radius = 0.03f, Falloff = FalloffCurve.Linear };
        int[] indices = Collect(solver, set, options, out float[] weights);

        float WeightOf(int p) => weights[Array.IndexOf(indices, p)];
        Vector3 c = solver.Positions[set[0]];
        Assert.Equal(1f, WeightOf(set[0]));
        Assert.Equal(2f / 3f, WeightOf(solver.FindNearestParticle(c + new Vector3(0.01f, 0f, 0f))), 3);
        Assert.Equal(1f / 3f, WeightOf(solver.FindNearestParticle(c + new Vector3(0.02f, 0f, 0f))), 3);
        Assert.DoesNotContain(solver.FindNearestParticle(c + new Vector3(0.05f, 0f, 0f)), indices);
        Assert.DoesNotContain(solver.FindNearestParticle(c + new Vector3(0.04f, 0f, 0f)), indices);
        Assert.All(weights, w => Assert.InRange(w, 0f, 1f));
        Assert.All(weights, w => Assert.True(w > 0f));   // 重み 0 の粒子は含めない
        Assert.Equal(indices.OrderBy(i => i).ToArray(), indices);
    }

    [Fact]
    public void CollectSetRange_RadiusZero_IsJustTheSet()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > 0.02f && p.X < 0.05f && p.Y > 0.08f && p.Y < 0.12f);
        int[] indices = Collect(solver, set, null, out float[] weights);
        Assert.Equal(set.OrderBy(i => i).ToArray(), indices);
        Assert.All(weights, w => Assert.Equal(1f, w));
    }

    [Theory]
    [InlineData(BrushDistanceMode.Surface, false)]
    [InlineData(BrushDistanceMode.Linear, false)]
    [InlineData(BrushDistanceMode.Surface, true)]
    [InlineData(BrushDistanceMode.Linear, true)]
    public void CollectSetRange_MatchesTheRangeGrabUses(BrushDistanceMode mode, bool mirror)
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > 0.03f && p.X < 0.06f && p.Y > 0.08f && p.Y < 0.12f);
        var options = new BrushOptions { Radius = 0.025f, Falloff = FalloffCurve.Smooth, DistanceMode = mode, Mirror = mirror ? PlaneX : null };
        int[] indices = Collect(solver, set, options, out float[] weights);

        solver.Grab(set, new BrushMotion(), options);
        Assert.Equal(solver.BrushParticleCount, indices.Length);
        for (int k = 0; k < indices.Length; k++)
        {
            Assert.True(solver.IsGrabbed(indices[k]));
            Assert.Equal(solver.BrushWeight(indices[k]), weights[k], 6);
        }
        if (mirror) Assert.Contains(indices, i => solver.Positions[i].X < 0f);
    }

    [Fact]
    public void CollectSetRange_DoesNotChangeTheGrabbedState()
    {
        ClothSolver solver = FlatCloth();
        int[] grabbed = ParticlesIn(solver, p => p.X > 0.02f && p.X < 0.04f && p.Y > 0.09f && p.Y < 0.11f);
        solver.Grab(grabbed, new BrushMotion { Translation = new Vector3(0f, 0f, 0.01f) }, new BrushOptions { Radius = 0.02f, Falloff = FalloffCurve.Linear });
        int count = solver.BrushParticleCount;
        int center = solver.GrabbedParticle;
        float[] before = Enumerable.Range(0, solver.Positions.Length).Select(i => solver.BrushWeight(i)).ToArray();

        int[] other = ParticlesIn(solver, p => p.X < -0.04f && p.Y > 0.02f && p.Y < 0.05f);
        Collect(solver, other, new BrushOptions { Radius = 0.04f, Mirror = PlaneX }, out _);

        Assert.Equal(count, solver.BrushParticleCount);
        Assert.Equal(center, solver.GrabbedParticle);
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], solver.BrushWeight(i));
        // 同じ集合でつまみ続けられる（範囲を選び直して位置が飛ばない）
        solver.Grab(grabbed, new BrushMotion { Translation = new Vector3(0f, 0f, 0.01f) }, new BrushOptions { Radius = 0.02f, Falloff = FalloffCurve.Linear });
        solver.Solve(20);
        foreach (int i in grabbed) Assert.Equal(0.01f, solver.Positions[i].Z, 4);
    }

    [Fact]
    public void CollectSetRange_ThinsEvenlyWhenTooMany_AndValidatesArguments()
    {
        ClothSolver solver = FlatCloth();
        int[] set = ParticlesIn(solver, p => p.X > -0.05f && p.X < 0.05f && p.Y > 0.05f && p.Y < 0.15f);
        Assert.True(set.Length > 50);
        int[] indices = Collect(solver, set, null, out float[] weights, 20);
        Assert.InRange(indices.Length, 10, 20);
        Assert.Equal(indices.OrderBy(i => i).ToArray(), indices);
        Assert.All(indices, i => Assert.Contains(i, set));

        var buffer = new int[10];
        var wbuffer = new float[10];
        Assert.Equal(0, solver.CollectSetRange(new int[0], null, buffer, wbuffer, 10));
        Assert.Equal(0, solver.CollectSetRange(set, null, buffer, wbuffer, 0));
        Assert.Throws<ArgumentNullException>(() => solver.CollectSetRange(null!, null, buffer, wbuffer, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.CollectSetRange(new[] { -1 }, null, buffer, wbuffer, 10));
    }
}
