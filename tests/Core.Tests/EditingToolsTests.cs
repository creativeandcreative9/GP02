using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 編集の道具の計算のテスト: つながりの許容距離 ε、鏡の面の自動の位置合わせ、鏡像の対応、左右差、今の形を左右対称にする、
/// 減り方の同心円の位置。
/// </summary>
public class EditingToolsTests
{
    static readonly MirrorPlane PlaneX = new MirrorPlane(Vector3.Zero, Vector3.UnitX);

    // 2枚の平らな布を x 方向に gap だけ離して並べたもの（別の島）。左の布は x が -0.2〜0、右の布は gap〜gap+0.2。左の布の粒子数を返す
    static ClothSolver SideBySide(float gap, out int leftCount)
    {
        ProceduralMeshes.Grid(20, 20, 0.2f, 0.2f, out var grid, out var gridTriangles);
        var left = grid.Select(v => v - new Vector3(0.2f, 0f, 0f)).ToArray();
        var right = grid.Select(v => v + new Vector3(gap, 0f, 0f)).ToArray();
        var vertices = left.Concat(right).ToArray();
        var triangles = gridTriangles.Concat(gridTriangles.Select(i => i + left.Length)).ToArray();
        leftCount = left.Length;
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles))) { Kernel = null };
    }

    // 左右（x）対称なスカート状の円錐台（Y が上）。x 方向に shift だけずらす
    static ClothSolver Skirt(float shift = 0f, int around = 120, int rings = 35)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        for (int r = 0; r <= rings; r++)
        {
            float f = r / (float)rings;
            float radius = 0.13f + (0.30f - 0.13f) * f;
            for (int a = 0; a < around; a++)
            {
                double angle = 2.0 * Math.PI * a / around;
                v.Add(new Vector3((float)(Math.Cos(angle) * radius) + shift, 0.8f - 0.35f * f, (float)(Math.Sin(angle) * radius)));
            }
        }
        for (int r = 0; r < rings; r++)
        for (int a = 0; a < around; a++)
        {
            int i = r * around + a, j = r * around + (a + 1) % around;
            t.AddRange(new[] { i, j, i + around, j, j + around, i + around });
        }
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(v.ToArray(), t.ToArray()))) { Kernel = null };
    }

    static int Nearest(ClothSolver solver, Vector3 point)
    {
        Vector3[] rest = solver.Model.RestPositions;
        int best = 0;
        for (int i = 1; i < rest.Length; i++)
        {
            if (Vector3.DistanceSquared(rest[i], point) < Vector3.DistanceSquared(rest[best], point)) best = i;
        }
        return best;
    }

    // ---- つながりの許容距離 ε ----

    [Fact]
    public void Connect_ZeroKeepsTheBrushOnItsIsland_PositiveReachesTheNearbyIsland()
    {
        ClothSolver solver = SideBySide(0.005f, out int leftCount);
        int seed = Nearest(solver, new Vector3(-0.01f, 0.1f, 0f));
        var options = new BrushOptions { Radius = 0.04f, Falloff = FalloffCurve.Linear };

        solver.Grab(seed, new BrushMotion(), options);
        for (int i = leftCount; i < solver.Positions.Length; i++) Assert.False(solver.IsGrabbed(i));

        options.ConnectDistance = 0.008f;
        solver.Grab(seed, new BrushMotion(), options);
        int across = Nearest(solver, new Vector3(0.005f, 0.1f, 0f));
        int farther = Nearest(solver, new Vector3(0.015f, 0.1f, 0f));
        Assert.True(solver.IsGrabbed(across));
        Assert.True(solver.IsGrabbed(farther));
        // 距離は ε の橋（5mm）を通って伝わる: 1cm + 0.5cm → 重み 1 - 0.015/0.04
        Assert.Equal(1f - 0.015f / 0.04f, solver.BrushWeight(across), 3);
        Assert.True(solver.BrushWeight(farther) < solver.BrushWeight(across));
        // ε は範囲を決めるだけで、粒子の数・制約は変わらない
        Assert.Equal(solver.Model.StretchRestLengths.Length, solver.Model.StretchPairs.Length / 2);
    }

    [Fact]
    public void Connect_DoesNotChangeDistancesInsideOneIsland()
    {
        ClothSolver solver = SideBySide(0.005f, out _);
        int seed = Nearest(solver, new Vector3(-0.1f, 0.1f, 0f));
        var indices0 = new int[2000];
        var weights0 = new float[2000];
        var indices1 = new int[2000];
        var weights1 = new float[2000];
        int count0 = solver.CollectBrushRange(seed, 0.05f, FalloffCurve.Linear, BrushDistanceMode.Surface, indices0, weights0, 2000);
        int count1 = solver.CollectBrushRange(seed, 0.05f, FalloffCurve.Linear, BrushDistanceMode.Surface, indices1, weights1, 2000, 0, 0.02f);
        Assert.Equal(count0, count1);
        for (int k = 0; k < count0; k++)
        {
            Assert.Equal(indices0[k], indices1[k]);
            Assert.Equal(weights0[k], weights1[k], 5);
        }
    }

    [Fact]
    public void Connect_AppliesToTheSelectionRange_AndTheRestoreBrush()
    {
        ClothSolver solver = SideBySide(0.005f, out int leftCount);
        int[] set = { Nearest(solver, new Vector3(-0.01f, 0.1f, 0f)) };
        var indices = new int[5000];
        var weights = new float[5000];
        var options = new BrushOptions { Radius = 0.03f, Falloff = FalloffCurve.Linear };
        int without = solver.CollectSetRange(set, options, indices, weights, 5000);
        Assert.All(indices.Take(without), i => Assert.True(i < leftCount));
        options.ConnectDistance = 0.006f;
        int with = solver.CollectSetRange(set, options, indices, weights, 5000);
        Assert.True(with > without);
        Assert.Contains(indices.Take(with), i => i >= leftCount);

        // 集合でつまむときも同じ範囲（右の布も動く）
        solver.Grab(set, new BrushMotion { Translation = new Vector3(0f, 0f, 0.02f) }, options);
        solver.Solve(20);
        int across = Nearest(solver, new Vector3(0.005f, 0.1f, 0f));
        Assert.True(solver.Positions[across].Z > 0.005f);
        solver.Release();

        // 元に戻すブラシも ε を使う
        solver.ResetToRest();
        var moved = solver.Positions.ToArray();
        for (int i = 0; i < moved.Length; i++) moved[i].Z += 0.01f;
        solver.SetPositions(moved);
        int changed0 = solver.RelaxTowardRest(set[0], 0.03f, 1f, FalloffCurve.Constant, BrushDistanceMode.Surface);
        solver.SetPositions(moved);
        int changed1 = solver.RelaxTowardRest(set[0], 0.03f, 1f, FalloffCurve.Constant, BrushDistanceMode.Surface, 0.006f);
        Assert.True(changed1 > changed0);
        Assert.Equal(0f, solver.Positions[across].Z, 5);
    }

    [Fact]
    public void Connect_RejectsNegative()
    {
        ClothSolver solver = SideBySide(0.005f, out _);
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.Grab(0, new BrushMotion(), new BrushOptions { ConnectDistance = -0.01f }));
    }

    // ---- 鏡の面の自動の位置合わせ ----

    [Fact]
    public void MirrorFit_FindsTheSymmetryPlaneOfAShiftedSkirt_EvenWithAOneSidedPart()
    {
        const float Shift = 0.07f;
        ClothSolver skirt = Skirt(Shift);
        var points = skirt.Model.RestPositions.ToList();
        // 片側（+X）だけにあるポケット。重心は +X にずれるが、対称の面は変わらない
        for (int i = 0; i < 400; i++) points.Add(new Vector3(Shift + 0.28f + 0.002f * (i % 20), 0.5f + 0.005f * (i / 20), 0.0f));
        float centroid = points.Average(p => p.X);
        Assert.True(MathF.Abs(centroid - Shift) > 0.01f, "テストの前提: 重心ではずれる");

        float offset = MirrorFit.FindOffset(points.ToArray(), Vector3.UnitX, Vector3.Zero, 0.02f, out float matched);
        Assert.Equal(Shift, offset, 2);
        Assert.InRange(MathF.Abs(offset - Shift), 0f, 0.003f);
        Assert.True(matched > 0.9f, $"重なった割合 {matched}");
    }

    [Fact]
    public void MirrorFit_UsesTheNormalAndOrigin()
    {
        ClothSolver skirt = Skirt(-0.04f);
        // 法線を逆にすると、ずれの符号も逆になる。基準の点をずらすと、その分だけ変わる
        float a = MirrorFit.FindOffset(skirt.Model.RestPositions, Vector3.UnitX, Vector3.Zero, 0.02f, out _);
        float b = MirrorFit.FindOffset(skirt.Model.RestPositions, -2f * Vector3.UnitX, Vector3.Zero, 0.02f, out _);
        float c = MirrorFit.FindOffset(skirt.Model.RestPositions, Vector3.UnitX, new Vector3(0.1f, 5f, 0f), 0.02f, out _);
        Assert.InRange(a, -0.043f, -0.037f);
        Assert.Equal(-a, b, 3);
        Assert.Equal(a - 0.1f, c, 3);
    }

    [Fact]
    public void MirrorFit_DoesNotSnapToTheParticleSpacing_OnAFlatLattice()
    {
        // 1cm 間隔の格子に並んだ平らな板（中心 x = 3.7cm）。一番近い点までの距離で測ると、5mm ごとに同じくらい重なってしまう
        const float Center = 0.037f;
        var points = new List<Vector3>();
        for (int x = -30; x <= 30; x++)
        for (int y = 0; y <= 40; y++)
            points.Add(new Vector3(Center + 0.01f * x, 0.01f * y, 0f));
        float offset = MirrorFit.FindOffset(points.ToArray(), Vector3.UnitX, Vector3.Zero, 0.02f, out float matched);
        Assert.InRange(offset, Center - 0.0015f, Center + 0.0015f);
        Assert.True(matched > 0.95f);
    }

    // ---- 鏡像の対応と左右差 ----

    [Fact]
    public void FindMirrorParticles_MatchesEverythingOnASymmetricSkirt_AndReportsUnmatchedWhenThePlaneIsOff()
    {
        ClothSolver skirt = Skirt();
        var map = new int[skirt.Model.ParticleCount];
        Assert.Equal(0, skirt.FindMirrorParticles(PlaneX, BrushOptions.DefaultMirrorTolerance, map));
        for (int i = 0; i < map.Length; i++) Assert.Equal(skirt.FindMirrorParticle(i, PlaneX, BrushOptions.DefaultMirrorTolerance), map[i]);

        int unmatched = skirt.FindMirrorParticles(new MirrorPlane(new Vector3(0.06f, 0f, 0f), Vector3.UnitX), BrushOptions.DefaultMirrorTolerance, map);
        Assert.True(unmatched > map.Length / 4, $"相手のいない点 {unmatched}");
    }

    // 右側（x > 0）だけを外へふくらませる
    static void BulgeRightSide(ClothSolver solver)
    {
        Vector3[] rest = solver.Model.RestPositions;
        var moved = solver.Positions.ToArray();
        for (int i = 0; i < moved.Length; i++)
        {
            if (rest[i].X <= 0.01f) continue;
            float f = MathF.Min(1f, rest[i].X / 0.1f) * (0.8f - rest[i].Y);
            moved[i] += new Vector3(0.1f * f, 0f, 0.05f * f);
        }
        solver.SetPositions(moved);
    }

    [Fact]
    public void MirrorDifference_IsZeroAtRest_AndPositiveForAOneSidedEdit()
    {
        ClothSolver skirt = Skirt();
        int n = skirt.Model.ParticleCount;
        var map = new int[n];
        skirt.FindMirrorParticles(PlaneX, BrushOptions.DefaultMirrorTolerance, map);
        var difference = new float[n];
        skirt.MeasureMirrorDifference(map, PlaneX, BrushOptions.DefaultMirrorTolerance, difference);
        Assert.All(difference, d => Assert.InRange(d, 0f, 1e-5f));

        BulgeRightSide(skirt);
        skirt.MeasureMirrorDifference(map, PlaneX, BrushOptions.DefaultMirrorTolerance, difference);
        Assert.True(difference.Max() > 0.02f);
        // 鏡像が無い粒子は -1
        map[0] = -1;
        skirt.MeasureMirrorDifference(map, PlaneX, BrushOptions.DefaultMirrorTolerance, difference);
        Assert.Equal(-1f, difference[0]);
    }

    [Fact]
    public void Symmetrize_CopiesTheKeptSideToTheOtherSide()
    {
        ClothSolver skirt = Skirt();
        int n = skirt.Model.ParticleCount;
        BulgeRightSide(skirt);
        Vector3[] before = skirt.Positions.ToArray();
        Vector3[] rest = skirt.Model.RestPositions;
        var map = new int[n];
        skirt.FindMirrorParticles(PlaneX, BrushOptions.DefaultMirrorTolerance, map);

        int changed = skirt.SymmetrizeShape(PlaneX, BrushOptions.DefaultMirrorTolerance, keepPositiveSide: true, map);
        Assert.True(changed > n / 3);
        var difference = new float[n];
        skirt.MeasureMirrorDifference(map, PlaneX, BrushOptions.DefaultMirrorTolerance, difference);
        Assert.True(difference.Max() < 1e-4f, $"左右差の最大 {difference.Max()}");
        // 基準の側（+X）は、中央線の近くのほかは変わらない
        for (int i = 0; i < n; i++)
        {
            if (rest[i].X > 0.011f) Assert.True(Vector3.Distance(before[i], skirt.Positions[i]) < 1e-6f);
        }
    }

    [Fact]
    public void Symmetrize_FromTheNegativeSide_RestoresTheUneditedShape_AndKeepsPins()
    {
        ClothSolver skirt = Skirt();
        int n = skirt.Model.ParticleCount;
        BulgeRightSide(skirt);
        int pinned = Nearest(skirt, new Vector3(0.3f, 0.45f, 0f));
        skirt.SetPinned(pinned, true);
        Vector3 pinnedBefore = skirt.Positions[pinned];

        skirt.SymmetrizeShape(PlaneX, BrushOptions.DefaultMirrorTolerance, keepPositiveSide: false);
        Vector3[] rest = skirt.Model.RestPositions;
        for (int i = 0; i < n; i++)
        {
            if (i == pinned) continue;
            Assert.True(Vector3.Distance(rest[i], skirt.Positions[i]) < 1e-5f, $"粒子 {i}");
        }
        Assert.Equal(pinnedBefore, skirt.Positions[pinned]);
    }

    // ---- 減り方の同心円 ----

    [Fact]
    public void OuterPositionForWeight_GivesTheRingRadii()
    {
        Assert.Equal(0.25f, FalloffCurve.Linear.OuterPositionForWeight(0.75f), 4);
        Assert.Equal(0.5f, FalloffCurve.Linear.OuterPositionForWeight(0.5f), 4);
        Assert.Equal(0.75f, FalloffCurve.Linear.OuterPositionForWeight(0.25f), 4);
        Assert.Equal(1f, FalloffCurve.Constant.OuterPositionForWeight(0.25f));
        Assert.Equal(0.5f, FalloffCurve.Smooth.OuterPositionForWeight(0.5f), 2);
        // 減ってからまた増えるカーブでも、外側の位置
        var bump = FalloffCurve.FromSamples(new[] { 1f, 0f, 0.6f, 0f });
        Assert.Equal(2f / 3f + 1f / 18f, bump.OuterPositionForWeight(0.5f), 3);
        Assert.Equal(0f, FalloffCurve.FromSamples(new[] { 0.1f, 0f }).OuterPositionForWeight(0.5f));
    }
}
