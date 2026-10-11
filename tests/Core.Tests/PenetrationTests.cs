using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 貫通の検出（PenetrationDetector）と、布で押し出す（PenetrationFixer と ClothSolver の体との制約）のテスト。
/// 体は円柱にこぶを付けたもの、布は体を囲む筒。
/// </summary>
[Collection("DefaultKernel")]
public class PenetrationTests
{
    const float BodyRadius = 0.10f;
    const float TubeRadius = 0.115f;
    const float TubeTop = 0.7f, TubeBottom = 0.3f;
    const float BumpHeight = 0.025f, BumpY = 0.5f, BumpSigma = 0.04f;

    // 円柱の体。こぶは角度 0（+X）、高さ 0.5m の所に、高さ bump（半径の増え）、広がり 4cm のガウス形
    static BodySurface BumpyBody(float bump = BumpHeight, int segments = 64)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        BodySurfaceTests.Lathe(v, t, 0f, 0f, 1f, 0f, 100, segments, (y, angle) =>
        {
            float a = angle > MathF.PI ? angle - 2f * MathF.PI : angle;
            float s = BodyRadius * a;
            float dy = y - BumpY;
            return BodyRadius + bump * MathF.Exp(-(dy * dy + s * s) / (2f * BumpSigma * BumpSigma));
        });
        return new BodySurface(v.ToArray(), t.ToArray());
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

    // 上端の段をピン留めし、体までの隙間（1.5cm - 余白）の分は元の形より内側へ動けるようにする（編集のセッションと同じ使い方）
    static ClothSolver CreateSolver(ParticleCloth cloth, IClothSolverKernel? kernel = null)
    {
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = kernel };
        var allowance = new float[cloth.Model.ParticleCount];
        Array.Fill(allowance, TubeRadius - BodyRadius - RestGapMargin);
        solver.SetRestSurfaceAllowance(allowance);
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.Model.RestPositions[i].Y > TubeTop - cloth.Spacing) solver.SetPinned(i, true);
        }
        return solver;
    }

    const float RestGapMargin = 0.002f;

    static float AngleOf(Vector3 p) => MathF.Abs(MathF.Atan2(p.Z, p.X));

    // 布の今の形の頂点（粒子についていく、まとめた頂点）が体に入った最大の深さ（メートル、入っていなければ負）
    static float DeepestWeldedVertex(ParticleCloth cloth, ClothSolver solver, BodySurface body)
    {
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(solver.Positions, welded);
        float worst = float.MinValue;
        foreach (Vector3 p in welded)
        {
            if (!body.TryQuery(p, 0.05f, out BodyHit hit)) continue;
            worst = MathF.Max(worst, -hit.SignedDistance);
        }
        return worst;
    }

    [Fact]
    public void Detect_FindsTheBumpThroughTheTube_AndNothingWithoutTheBump()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        var detector = new PenetrationDetector(cloth);
        var report = new PenetrationReport();

        detector.Detect(solver, BumpyBody(), new PenetrationOptions(), report);
        Assert.True(report.HasBody);
        Assert.True(report.PenetratingCount > 20, report.ToString());
        Assert.Equal(1, report.RegionCount);
        Assert.InRange(report.MaxDepth, 0.008f, 0.0115f);
        Assert.Equal(0, report.PinnedCount);
        Assert.NotEmpty(report.PointParticles);
        foreach (int i in report.PointParticles)
        {
            Vector3 p = solver.Positions[i];
            Assert.True(AngleOf(p) < 1.0f && MathF.Abs(p.Y - BumpY) < 0.08f, $"こぶから離れた粒子 {i} {p}");
        }
        // 押し出す量は、余白を含めた体の外までの距離
        int deepest = Array.IndexOf(report.Push, report.Push.Max());
        Assert.InRange(report.Push[deepest], report.MaxDepth + report.Margin - 1e-5f, report.MaxDepth + report.Margin + 1e-5f);

        // こぶの無い体（隙間 1.5cm）では何も無い
        detector.Detect(solver, BumpyBody(0f), new PenetrationOptions(), report);
        Assert.Equal(0, report.NeedPushCount);
        Assert.Equal(0, report.RegionCount);
        Assert.Equal(0f, report.MaxDepth);

        // 体が無ければ調べない
        detector.Detect(solver, null!, new PenetrationOptions(), report);
        Assert.False(report.HasBody);
        Assert.Equal(0, report.NeedPushCount);
    }

    [Fact]
    public void Fix_PushesTheTubeOut_KeepingTheClothLength_AndLeavesTheRestAlone()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        BodySurface body = BumpyBody();
        var detector = new PenetrationDetector(cloth);
        var report = new PenetrationReport();
        Vector3[] before = solver.Positions.ToArray();

        PenetrationFixResult result = PenetrationFixer.Fix(detector, solver, body, new PenetrationOptions(), report);
        Console.WriteLine("[GP02] 筒の布とこぶ: " + result);

        Assert.True(result.InitialPenetrating > 20);
        Assert.Equal(0, result.RemainingPenetrating);
        Assert.True(result.MaxStretch < 0.03f, $"最大の伸び {result.MaxStretch:P1}");
        // 粒子だけでなく、粒子についていく布の頂点も体の外
        Assert.True(DeepestWeldedVertex(cloth, solver, body) < 0.0005f, $"頂点が体に入っている {DeepestWeldedVertex(cloth, solver, body) * 1000f:F2} mm");

        float bumpMove = 0f, farMove = 0f;
        for (int i = 0; i < before.Length; i++)
        {
            float move = Vector3.Distance(before[i], solver.Positions[i]);
            if (solver.IsPinned(i)) Assert.Equal(0f, move);
            Vector3 p = before[i];
            if (AngleOf(p) < 0.2f && MathF.Abs(p.Y - BumpY) < 0.01f) bumpMove = MathF.Max(bumpMove, move);
            // こぶの反対側（こぶから 20cm 以上）はほとんど動かない。布の長さを保つので、こぶを回り込む分だけ筒がこぶの側へ少し寄る
            // （反復を増やして伸びを減らすほど寄る。1500 回で約 2.3mm。体までの隙間 1.5cm に比べて小さい）
            if (AngleOf(p) > 2.2f) farMove = MathF.Max(farMove, move);
        }
        Console.WriteLine($"[GP02] こぶの上の移動 {bumpMove * 1000f:F1} mm / 反対側の最大の移動 {farMove * 1000f:F2} mm");
        Assert.True(bumpMove > 0.009f, "こぶの上の布が外へ出ていない");
        Assert.True(farMove < 0.003f, $"こぶの無い所が動いている {farMove * 1000f:F2} mm");
    }

    [Fact]
    public void PinnedParticlesInsideTheBody_StayAndAreReported()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        BodySurface body = BumpyBody();
        // こぶの真上を 2cm の範囲でピン留め
        var pinned = new List<int>();
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (Vector3.Distance(cloth.Model.RestPositions[i], new Vector3(TubeRadius, BumpY, 0f)) < 0.02f)
            {
                solver.SetPinned(i, true);
                pinned.Add(i);
            }
        }
        Assert.NotEmpty(pinned);
        var report = new PenetrationReport();
        PenetrationFixResult result = PenetrationFixer.Fix(new PenetrationDetector(cloth), solver, body, new PenetrationOptions(), report);
        Console.WriteLine("[GP02] こぶの上をピン留め: " + result);
        foreach (int i in pinned) Assert.Equal(cloth.Model.RestPositions[i], solver.Positions[i]);
        Assert.True(result.RemainingPinned > 0);
        Assert.True(result.RemainingPenetrating >= result.RemainingPinned);
        Assert.True(result.RemainingPenetrating < result.InitialPenetrating, "ピンの周りは押し出される");
    }

    [Fact]
    public void BodyConstraint_DoesNotPushExistingPenetration_ButKeepsTheClothOutsideAfterFix()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        BodySurface body = BumpyBody();
        Vector3[] penetrating = solver.Positions.ToArray();

        // 体を設定して解いても、すでに入っている所は勝手に押し出さない
        solver.SetBody(body);
        solver.Solve(60);
        for (int i = 0; i < penetrating.Length; i++) Assert.True(Vector3.Distance(penetrating[i], solver.Positions[i]) < 1e-5f, $"粒子 {i}");

        var report = new PenetrationReport();
        var detector = new PenetrationDetector(cloth);
        PenetrationFixResult result = PenetrationFixer.Fix(detector, solver, body, new PenetrationOptions(), report);
        Assert.Equal(0, result.RemainingPenetrating);

        // 直したあと、こぶの上をつまんで内へ 3cm 押しても、体の中へは入らない（余白で止まる）
        int center = solver.FindNearestParticle(new Vector3(TubeRadius + 0.012f, BumpY, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(center, start - new Vector3(0.03f * f / 10f, 0f, 0f), 0.02f);
            solver.Solve(20);
        }
        solver.Settle(0.01f, 400, out _);
        float worst = float.MaxValue;
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (!solver.IsGrabbed(i)) continue;
            Assert.True(body.TryQuery(solver.Positions[i], 0.05f, out BodyHit hit));
            worst = MathF.Min(worst, hit.SignedDistance);
        }
        solver.Release();
        Console.WriteLine($"[GP02] 直したあとに内へ押したときの、体からの最小の距離 {worst * 1000f:F2} mm");
        Assert.True(worst > PenetrationOptions.DefaultMargin - 0.0005f, $"体に入った {worst * 1000f:F2} mm");

        // 元に戻す（位置を置き換える）と、突き抜けた形に戻り、解いてもそのまま（押し出しは元に戻した形を変えない）
        solver.SetPositions(penetrating);
        solver.Solve(60);
        for (int i = 0; i < penetrating.Length; i++) Assert.True(Vector3.Distance(penetrating[i], solver.Positions[i]) < 1e-5f, $"粒子 {i}");
        detector.Detect(solver, body, new PenetrationOptions(), report);
        Assert.Equal(result.InitialPenetrating, report.PenetratingCount);
    }

    // 直したあとに、こぶの上をつまんで内へ 4cm 押す。柔らかいつまみでも固いつまみ（柔らかさ 0）でも、粒子は体の中へ入らない
    // （つまみの目標を体の外へ移すので、固いつまみの粒子も体の外に置かれる）。直したときに足した平面は、つまむと消える。
    // そのあとにもう一度直すと、押し付けて伸びた布も仕上げで戻る
    [Theory]
    [InlineData(ClothSolver.DefaultBrushCompliance)]
    [InlineData(0f)]
    public void AfterFix_PushingIn_StopsAtTheBody_AndGrabbingDropsTheExtraPlanes(float brushCompliance)
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        solver.BrushCompliance = brushCompliance;
        BodySurface body = BumpyBody();
        var detector = new PenetrationDetector(cloth);
        var report = new PenetrationReport();
        PenetrationFixer.Fix(detector, solver, body, new PenetrationOptions(), report);
        Assert.Equal(0, report.PenetratingCount);
        int far = solver.FindNearestParticle(new Vector3(-TubeRadius, BumpY, 0f));
        solver.AddBodyExtraPlane(far, solver.Positions[far], -Vector3.UnitX);
        Assert.True(solver.BodyExtraPlaneCount > 0);

        int center = solver.FindNearestParticle(new Vector3(TubeRadius + 0.012f, BumpY, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(center, start - new Vector3(0.04f * f / 10f, 0f, 0f), 0.03f);
            solver.Solve(20);
        }
        for (int k = 0; k < 20; k++) solver.Solve(20);
        Assert.Equal(0, solver.BodyExtraPlaneCount);
        float worst = float.MaxValue;
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (body.TryQuery(solver.Positions[i], 0.05f, out BodyHit hit)) worst = MathF.Min(worst, hit.SignedDistance);
        }
        float pushed = solver.MeasureMaxStretch();
        Console.WriteLine($"[GP02] 直したあとに内へ 4cm 押す（つまむ柔らかさ {brushCompliance}）: 体からの最小の距離 {worst * 1000f:F2} mm / 最大の伸び {pushed:P1}");
        Assert.True(worst > PenetrationOptions.DefaultMargin - 0.0005f, $"体に入った {worst * 1000f:F2} mm");
        // 柔らかいつまみは、目標を体の外へ移すので、体と引っ張り合って伸びることはない。
        // 固いつまみは、範囲の粒子をそのまま体の表面に沿わせるので、つまんでいる間は周りが伸びる（離して直せば戻る）
        if (brushCompliance > 0f) Assert.True(pushed < 0.1f, $"最大の伸び {pushed:P1}");

        PenetrationFixResult again = PenetrationFixer.Fix(detector, solver, body, new PenetrationOptions(), report);
        Console.WriteLine("[GP02] 押したあとにもう一度直す: " + again);
        Assert.Equal(0, again.RemainingPenetrating);
        // 押し出す前から伸びていても、伸びが下がる間は仕上げを続ける
        if (again.Iterations > 0) Assert.True(again.MaxStretch < MathF.Max(0.03f, again.StretchBefore * 0.5f), $"最大の伸び {again.StretchBefore:P1} → {again.MaxStretch:P1}");
    }

    [Fact]
    public void SleeveNextToTheTorso_IsNotPushedTowardTheArm()
    {
        // 胴体（半径 10cm）と腕（半径 4cm、x = 0.155）。袖（半径 5.4cm の筒）の内側（胴体に向いた側）は、胴体の 1mm 外
        var v = new List<Vector3>();
        var t = new List<int>();
        BodySurfaceTests.Lathe(v, t, 0f, 0f, 1f, 0f, 50, 64, (_, _) => 0.10f);
        BodySurfaceTests.Lathe(v, t, 0.155f, 0f, 1f, 0f, 50, 32, (_, _) => 0.04f);
        var body = new BodySurface(v.ToArray(), t.ToArray());
        ParticleCloth sleeve = Tube(0.054f, 0.155f, 48, 40);
        var solver = new ClothSolver(sleeve.Model, sleeve.Hierarchy) { Kernel = null };
        var report = new PenetrationReport();
        new PenetrationDetector(sleeve).Detect(solver, body, new PenetrationOptions(), report);
        // 胴体の面は袖の外向きと逆を向いているので、近くても押し出さない（押すと袖が腕に押し付けられる）
        Assert.Equal(0, report.NeedPushCount);

        // 胴体が袖の内側に食い込んでも同じ（どちらへ出すか決められない）
        var options = new PenetrationOptions { Margin = 0.004f };
        new PenetrationDetector(sleeve).Detect(solver, body, options, report);
        Assert.Equal(0, report.NeedPushCount);
    }

    [Fact]
    public void NarrowSpikeBetweenParticles_IsFoundByBodyVertices_AndFixed()
    {
        // 体（半径 10cm の円柱）に、細いとげ（底の半径 4mm、高さ 2.2cm の円錐）を付ける。先は布（半径 11.5cm）の 3mm 外
        var v = new List<Vector3>();
        var t = new List<int>();
        BodySurfaceTests.Lathe(v, t, 0f, 0f, 1f, 0f, 50, 64, (_, _) => BodyRadius);
        int s = v.Count;
        const int around = 12;
        var tip = new Vector3(0.118f, BumpY + 0.003f, 0.002f);
        var axis = Vector3.UnitX;
        Vector3 baseCenter = tip - axis * 0.022f;
        v.Add(tip);
        for (int i = 0; i < around; i++)
        {
            float angle = 2f * MathF.PI * i / around;
            v.Add(baseCenter + new Vector3(0f, 0.004f * MathF.Cos(angle), 0.004f * MathF.Sin(angle)));
        }
        v.Add(baseCenter);
        for (int i = 0; i < around; i++)
        {
            int a = s + 1 + i, b = s + 1 + (i + 1) % around;
            t.AddRange(new[] { s, a, b });
            t.AddRange(new[] { s + 1 + around, b, a });
        }
        var body = new BodySurface(v.ToArray(), t.ToArray());
        Assert.Equal(2, body.IslandCount);
        Assert.True(body.IsIslandSolid(1));

        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        var detector = new PenetrationDetector(cloth);
        var report = new PenetrationReport();
        detector.Detect(solver, body, new PenetrationOptions(), report);
        Console.WriteLine("[GP02] 細いとげ: " + report);
        Assert.True(report.BodyVertexCount >= 1, "とげの先が布の外に出ているのが見つからない");
        Assert.True(report.PenetratingCount > 0);
        Assert.NotEmpty(report.PointBodyVertices);

        PenetrationFixResult result = PenetrationFixer.Fix(detector, solver, body, new PenetrationOptions(), report);
        Console.WriteLine("[GP02] 細いとげを直す: " + result);
        Assert.Equal(0, result.RemainingPenetrating);
        Assert.Equal(0, report.BodyVertexCount);
        // 点のような出っ張りを伸びない布で覆うと、先の周りがどうしても少し伸びる（なだらかなこぶより大きい）
        Assert.True(result.MaxStretch < 0.06f, $"最大の伸び {result.MaxStretch:P1}");
        // とげの先が、布の面より内側
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(solver.Positions, welded);
        float tipRadius = new Vector2(tip.X, tip.Z).Length();
        float clothRadiusAtTip = 0f;
        foreach (Vector3 p in welded)
        {
            if (MathF.Abs(p.Y - tip.Y) < 0.004f && AngleOf(p) < 0.04f) clothRadiusAtTip = MathF.Max(clothRadiusAtTip, new Vector2(p.X, p.Z).Length());
        }
        Assert.True(clothRadiusAtTip > tipRadius, $"布 {clothRadiusAtTip} / とげの先 {tipRadius}");
    }

    [Fact]
    public void BodyMargin_ChangingTheMarginDoesNotPushTheClothOut()
    {
        ParticleCloth cloth = Tube();
        ClothSolver solver = CreateSolver(cloth);
        solver.SetBody(BumpyBody(0f));   // 隙間 1.5cm
        Vector3[] before = solver.Positions.ToArray();
        solver.BodyMargin = 0.03f;       // 余白を隙間より広くしても、今の距離より外へは押し出さない
        solver.Solve(40);
        for (int i = 0; i < before.Length; i++) Assert.True(Vector3.Distance(before[i], solver.Positions[i]) < 1e-5f, $"粒子 {i}");
        int any = solver.FindNearestParticle(new Vector3(TubeRadius, BumpY, 0f));
        Assert.InRange(solver.GetBodyMinDistance(any), 0.0145f, 0.0155f);
        solver.BodyMargin = 0.001f;
        Assert.Equal(0.001f, solver.GetBodyMinDistance(any));
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.BodyMargin = -1f);
    }

    [Fact]
    public void Kernel_ReceivingAllData_MatchesBuiltInPath_WithBodyPlanes()
    {
        ParticleCloth cloth = Tube(segments: 64, rows: 30);
        BodySurface body = BumpyBody();

        Vector3[] Run(IClothSolverKernel? kernel)
        {
            ClothSolver solver = CreateSolver(cloth, kernel);
            var report = new PenetrationReport();
            var options = new PenetrationOptions { MaxPasses = 2, MaxIterationsPerPass = 60 };
            PenetrationFixer.Fix(new PenetrationDetector(cloth), solver, body, options, report);
            // 直したあとに内へ押す（体との平面と、つまんだ範囲の引き寄せが同時に効く）
            int center = solver.FindNearestParticle(new Vector3(TubeRadius + 0.012f, BumpY, 0f));
            Vector3 start = solver.Positions[center];
            for (int f = 1; f <= 5; f++)
            {
                solver.Grab(center, start - new Vector3(0.02f * f / 5f, 0f, 0f), 0.02f);
                solver.Solve(10);
            }
            Assert.True(solver.BodyPlaneCount > 0);
            return solver.Positions.ToArray();
        }

        var reference = new ClothSolverKernelTests.ReferenceKernel();
        Vector3[] expected = Run(null);
        Vector3[] actual = Run(reference);
        Assert.True(reference.IterateCalls > 0);
        float moved = 0f;
        for (int i = 0; i < expected.Length; i++) moved = MathF.Max(moved, Vector3.Distance(expected[i], cloth.Model.RestPositions[i]));
        Assert.True(moved > 0.005f);
        for (int i = 0; i < expected.Length; i++) Assert.True(Vector3.Distance(expected[i], actual[i]) < 1e-5f, $"粒子 {i}");
    }
}
