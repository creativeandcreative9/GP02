using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// ブラシの一般化（粒子ごとの目標、回転・拡大縮小、重みのカーブ、距離の測り方、対称、元に戻すブラシ）のテスト。
/// </summary>
public class BrushTests
{
    const float Tolerance = 1e-5f;
    static readonly MirrorPlane PlaneX = new MirrorPlane(Vector3.Zero, Vector3.UnitX);

    // 頂点がそのまま粒子になる平らな布（XY 平面、1cm 間隔）
    static ClothSolver FlatCloth(int cells = 20, float size = 0.2f)
    {
        ProceduralMeshes.Grid(cells, cells, size, size, out var vertices, out var triangles);
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
    }

    // 2枚の平らな布を z 方向に gap だけ離して重ねたもの（つながっていない重ね着）。前の布の粒子数を返す
    static ClothSolver TwoLayers(float gap, out int frontCount)
    {
        ProceduralMeshes.Grid(20, 20, 0.2f, 0.2f, out var front, out var frontTriangles);
        var back = front.Select(v => v + new Vector3(0f, 0f, -gap)).ToArray();
        var vertices = front.Concat(back).ToArray();
        var triangles = frontTriangles.Concat(frontTriangles.Select(i => i + front.Length)).ToArray();
        frontCount = front.Length;
        return new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
    }

    // 左右（x）対称なスカート状の円錐台（Y が上、ウエスト y = TopY）。角度 2πa/N の頂点の鏡像は、角度 π - 2πa/N の頂点
    const float TopY = 0.8f, Length = 0.35f;
    static void Cone(int around, int rings, float top, float bottom, out Vector3[] vertices, out int[] triangles)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        for (int r = 0; r <= rings; r++)
        {
            float f = r / (float)rings;
            float radius = top + (bottom - top) * f;
            for (int a = 0; a < around; a++)
            {
                double angle = 2.0 * Math.PI * a / around;
                v.Add(new Vector3((float)(Math.Cos(angle) * radius), TopY - Length * f, (float)(Math.Sin(angle) * radius)));
            }
        }
        for (int r = 0; r < rings; r++)
        for (int a = 0; a < around; a++)
        {
            int i = r * around + a, j = r * around + (a + 1) % around;
            t.AddRange(new[] { i, j, i + around, j, j + around, i + around });
        }
        vertices = v.ToArray();
        triangles = t.ToArray();
    }

    static ClothSolver SymmetricSkirt()
    {
        Cone(120, 35, 0.13f, 0.30f, out var vertices, out var triangles);
        var solver = new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
        for (int i = 0; i < solver.Model.ParticleCount; i++)
        {
            if (solver.Model.RestPositions[i].Y > TopY - 0.005f) solver.SetPinned(i, true);
        }
        return solver;
    }

    // 粒子ごとの鏡像の粒子（元の位置で一番近いもの）
    static int[] MirrorMap(ClothSolver solver)
    {
        Vector3[] rest = solver.Model.RestPositions;
        var map = new int[rest.Length];
        for (int i = 0; i < rest.Length; i++)
        {
            Vector3 m = PlaneX.Reflect(rest[i]);
            int best = 0;
            for (int j = 1; j < rest.Length; j++)
            {
                if (Vector3.DistanceSquared(rest[j], m) < Vector3.DistanceSquared(rest[best], m)) best = j;
            }
            map[i] = best;
        }
        return map;
    }

    // 左右対称からのずれの最大（粒子と、鏡像の粒子を鏡に映した位置の距離）
    static float Asymmetry(ClothSolver solver, int[] map)
    {
        float max = 0f;
        for (int i = 0; i < map.Length; i++) max = MathF.Max(max, Vector3.Distance(solver.Positions[i], PlaneX.Reflect(solver.Positions[map[i]])));
        return max;
    }

    [Fact]
    public void NewApiTranslation_MatchesOldApi()
    {
        ProceduralMeshes.Grid(30, 30, 0.3f, 0.3f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles, coarseLevels: 2);
        Vector3[] Run(bool newApi)
        {
            var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.1f };
            for (int p = 0; p < cloth.Model.ParticleCount; p++)
            {
                if (cloth.Model.RestPositions[p].Y > 0.3f - cloth.Spacing) solver.SetPinned(p, true);
            }
            int center = solver.FindNearestParticle(new Vector3(0.15f, 0.1f, 0f));
            Vector3 start = solver.Positions[center];
            var options = new BrushOptions { Radius = 0.03f };
            for (int f = 1; f <= 8; f++)
            {
                Vector3 move = new Vector3(0.01f, 0.02f, 0.08f) * f / 8f;
                if (newApi) solver.Grab(center, BrushMotion.FromTranslation(move), options);
                else solver.Grab(center, start + move, 0.03f);
                solver.Solve(10);
            }
            solver.Settle(0.02f, 200, out _);
            return solver.Positions.ToArray();
        }

        Vector3[] expected = Run(false);
        Vector3[] actual = Run(true);
        float moved = expected.Select((p, i) => Vector3.Distance(p, cloth.Model.RestPositions[i])).Max();
        Assert.True(moved > 0.05f);
        for (int i = 0; i < expected.Length; i++) Assert.True(Vector3.Distance(expected[i], actual[i]) < Tolerance, $"粒子 {i}");
    }

    [Fact]
    public void BrushMotion_Default_IsIdentity_AndMatchesQuaternionRotation()
    {
        var p = new Vector3(0.3f, -0.2f, 0.5f);
        Assert.Equal(p, BrushMotion.Identity.Apply(p));
        Assert.Equal(1f, default(BrushMotion).Scale);
        Assert.Equal(Quaternion.Identity, default(BrushMotion).Rotation);

        // 内部の行列が Vector3.Transform と同じ向きに回すこと
        Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, 1f, -0.4f)), 0.7f);
        var motion = new BrushMotion(new Vector3(0.01f, 0.02f, 0.03f), q, 1.3f, new Vector3(0.1f, 0.2f, 0.3f));
        var solver = FlatCloth();
        solver.BrushCompliance = 0f;
        int center = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        solver.Grab(center, motion, new BrushOptions { Radius = 0.03f });
        solver.Solve(1);
        for (int i = 0; i < solver.Model.ParticleCount; i++)
        {
            if (!solver.IsGrabbed(i)) continue;
            Assert.True(Vector3.Distance(motion.Apply(solver.Model.RestPositions[i]), solver.Positions[i]) < Tolerance, $"粒子 {i}");
        }
    }

    [Fact]
    public void Rotation_RotatesRegion_AndKeepsLengths()
    {
        var solver = FlatCloth();
        solver.BrushCompliance = 0f;   // 目標に固定して、範囲の動きを正確に確かめる
        int center = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        Vector3 pivot = solver.Positions[center];
        var options = new BrushOptions { Radius = 0.04f };
        // 布の面内で 40° 回す（数コマに分けて）
        for (int f = 1; f <= 8; f++)
        {
            var motion = new BrushMotion { Pivot = pivot, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f * f / 8f) };
            solver.Grab(center, motion, options);
            solver.Solve(30);
        }
        float stretch = solver.Settle(0.01f, 2000, out _);

        Vector3[] rest = solver.Model.RestPositions;
        Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        int grabbed = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!solver.IsGrabbed(i)) continue;
            grabbed++;
            Assert.True(Vector3.Distance(pivot + Vector3.Transform(rest[i] - pivot, q), solver.Positions[i]) < Tolerance, $"粒子 {i}");
        }
        Assert.True(grabbed > 20, $"つまんだ粒子 {grabbed}");
        // 範囲の中の辺の長さは変わらない（回転なので）
        int[] pairs = solver.Model.StretchPairs;
        for (int k = 0; k < pairs.Length / 2; k++)
        {
            int a = pairs[2 * k], b = pairs[2 * k + 1];
            if (!solver.IsGrabbed(a) || !solver.IsGrabbed(b)) continue;
            Assert.True(MathF.Abs(Vector3.Distance(solver.Positions[a], solver.Positions[b]) - solver.Model.StretchRestLengths[k]) < Tolerance);
        }
        // 周りの布も伸びない
        Assert.True(stretch <= 0.01f, $"仕上げ後の伸び {stretch:P1}");
    }

    [Fact]
    public void Scale_ScalesRegionAroundPivot()
    {
        var solver = FlatCloth();
        solver.BrushCompliance = 0f;
        solver.KeepOutsideRestSurface = false;
        int center = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        Vector3 pivot = solver.Positions[center];
        solver.Grab(center, new BrushMotion { Pivot = pivot, Scale = 0.8f }, new BrushOptions { Radius = 0.03f });
        solver.Solve(20);

        Vector3[] rest = solver.Model.RestPositions;
        int grabbed = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!solver.IsGrabbed(i)) continue;
            grabbed++;
            Assert.True(Vector3.Distance(pivot + (rest[i] - pivot) * 0.8f, solver.Positions[i]) < Tolerance, $"粒子 {i}");
        }
        Assert.True(grabbed > 10, $"つまんだ粒子 {grabbed}");
        Assert.True(Vector3.Distance(pivot, solver.Positions[center]) < Tolerance, $"中心 {pivot} → {solver.Positions[center]}");
    }

    [Fact]
    public void FalloffCurve_Presets_AreOneAtCenterAndSmallAtEdge()
    {
        foreach (var curve in new[] { FalloffCurve.Smooth, FalloffCurve.Linear, FalloffCurve.Sharp, FalloffCurve.Sphere })
        {
            Assert.Equal(1f, curve.Evaluate(0f), 5);
            Assert.True(curve.Evaluate(1f) < 1e-3f);
            float previous = 1f;
            for (int k = 1; k <= 20; k++)
            {
                float w = curve.Evaluate(k / 20f);
                Assert.True(w <= previous + 1e-6f, "外へ向かって減る");
                previous = w;
            }
        }
        Assert.Equal(1f, FalloffCurve.Constant.Evaluate(0.5f));
        Assert.Equal(1f, FalloffCurve.Constant.Evaluate(1f));
        Assert.Equal(0.5f, FalloffCurve.Linear.Evaluate(0.5f), 5);
        Assert.Equal(0.5f, FalloffCurve.Smooth.Evaluate(0.5f), 3);
        Assert.Equal(0.25f, FalloffCurve.Sharp.Evaluate(0.5f), 3);
        Assert.Equal(MathF.Sqrt(0.75f), FalloffCurve.Sphere.Evaluate(0.5f), 3);

        // サンプルの間は直線で補い、範囲外の値は 0〜1 に収める
        var custom = FalloffCurve.FromSamples(new[] { 1f, 2f, -1f });
        Assert.Equal(1f, custom.Evaluate(0.25f));
        Assert.Equal(0.5f, custom.Evaluate(0.75f), 5);
        Assert.Equal(0f, custom.Evaluate(1f));
        Assert.Throws<ArgumentException>(() => FalloffCurve.FromSamples(new float[0]));
    }

    [Fact]
    public void Falloff_WeightsTheMotion_CenterOneAndEdgeSmall()
    {
        var solver = FlatCloth();
        solver.BrushCompliance = 0f;
        int center = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        var move = new Vector3(0f, 0f, 0.05f);
        const float radius = 0.05f;
        solver.Grab(center, BrushMotion.FromTranslation(move), new BrushOptions { Radius = radius, Falloff = FalloffCurve.Smooth });
        solver.Solve(10);

        Vector3[] rest = solver.Model.RestPositions;
        Assert.Equal(1f, solver.BrushWeight(center));
        float minWeight = 1f;
        for (int i = 0; i < rest.Length; i++)
        {
            float w = solver.BrushWeight(i);
            Assert.Equal(w > 0f, solver.IsGrabbed(i));
            if (w <= 0f) continue;
            minWeight = MathF.Min(minWeight, w);
            // 平らな格子では、メッシュに沿った距離は辺をたどった距離（直線距離以上）
            Assert.True(Vector3.Distance(rest[i], rest[center]) <= radius + 1e-5f);
            Assert.True(Vector3.Distance(rest[i] + w * move, solver.Positions[i]) < Tolerance, $"粒子 {i}");
        }
        Assert.True(minWeight < 0.2f, $"端の重み {minWeight}");

        solver.Release();
        Assert.Equal(0f, solver.BrushWeight(center));
        Assert.False(solver.IsGrabbed(center));
    }

    [Theory]
    [InlineData(BrushDistanceMode.Surface)]
    [InlineData(BrushDistanceMode.Linear)]
    public void DistanceMode_LinearGrabsDisconnectedLayer_SurfaceDoesNot(BrushDistanceMode mode)
    {
        var solver = TwoLayers(0.005f, out int frontCount);
        solver.BrushCompliance = 0f;
        solver.KeepOutsideRestSurface = false;
        int center = solver.FindNearestParticle(new Vector3(0.1f, 0.1f, 0f));
        Assert.True(center < frontCount);
        var move = new Vector3(0f, 0.01f, 0.04f);
        for (int f = 1; f <= 4; f++)
        {
            solver.Grab(center, BrushMotion.FromTranslation(move * f / 4f), new BrushOptions { Radius = 0.03f, DistanceMode = mode });
            solver.Solve(20);
        }

        Vector3[] rest = solver.Model.RestPositions;
        int backGrabbed = 0, backMovedOutside = 0;
        for (int i = frontCount; i < rest.Length; i++)
        {
            if (solver.IsGrabbed(i))
            {
                backGrabbed++;
                Assert.True(Vector3.Distance(rest[i] + move, solver.Positions[i]) < Tolerance, $"粒子 {i}");
            }
            else if (Vector3.Distance(rest[i], solver.Positions[i]) > 1e-3f)
            {
                backMovedOutside++;
            }
        }
        if (mode == BrushDistanceMode.Surface)
        {
            Assert.Equal(0, backGrabbed);
            Assert.Equal(0, backMovedOutside);
        }
        else
        {
            Assert.True(backGrabbed > 10, $"後ろの布でつまんだ粒子 {backGrabbed}");
            // 後ろの布も、その島ごと計算範囲に入るので、範囲の外側も引かれて動く
            Assert.True(backMovedOutside > 10, $"後ろの布で範囲の外側の動いた粒子 {backMovedOutside}");
        }
    }

    // 左（x < 0）の前寄りの裾をつまみ、外へ引きながらひねる
    static BrushMotion LeftMotion(Vector3 pivot, float f)
    {
        return new BrushMotion(new Vector3(-0.04f, 0.02f, -0.04f) * f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f * f), 1f, pivot);
    }

    [Fact]
    public void Mirror_MirrorSideMovesWithMirroredMotion()
    {
        var solver = SymmetricSkirt();
        solver.BrushCompliance = 0f;
        int[] map = MirrorMap(solver);
        int seed = solver.FindNearestParticle(new Vector3(-0.17f, TopY - 0.25f, -0.17f));
        Vector3 pivot = solver.Positions[seed];
        // メッシュの三角形の分け方は左右対称でない（メッシュに沿った距離が左右で少し違う）ので、直線距離で範囲を決める
        var options = new BrushOptions { Radius = 0.04f, Falloff = FalloffCurve.Smooth, Mirror = PlaneX, DistanceMode = BrushDistanceMode.Linear };
        BrushMotion motion = LeftMotion(pivot, 1f);
        solver.Grab(seed, motion, options);
        solver.Solve(0);   // 反復なし：つまんだ範囲が目標の位置に置かれるだけ（テザーで引き戻される前の目標を確かめる）

        Assert.Equal(map[seed], solver.MirrorParticle);
        Vector3[] rest = solver.Model.RestPositions;
        int mirrored = 0, direct = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!solver.IsGrabbed(i)) continue;
            float w = solver.BrushWeight(i);
            bool mirrorSide = rest[i].X > 0f;
            Vector3 target = mirrorSide ? PlaneX.Reflect(motion.Apply(PlaneX.Reflect(rest[i]))) : motion.Apply(rest[i]);
            Assert.True(Vector3.Distance(rest[i] + w * (target - rest[i]), solver.Positions[i]) < Tolerance, $"粒子 {i}");
            // 鏡像の粒子も同じ重みでつままれている
            Assert.True(MathF.Abs(solver.BrushWeight(map[i]) - w) < 1e-3f, $"粒子 {i} の鏡像の重み");
            if (mirrorSide) mirrored++;
            else direct++;
        }
        Assert.Equal(direct, mirrored);
        Assert.True(direct > 10);

        // ドラッグして仕上げると、つまんだ中心と鏡像の中心は鏡に映した関係に動く（片側だけつまむと大きく崩れる）
        float Drag(bool mirror, out float seedMismatch)
        {
            var s = SymmetricSkirt();
            var o = new BrushOptions { Radius = 0.04f, Falloff = FalloffCurve.Smooth, Mirror = mirror ? PlaneX : (MirrorPlane?)null, DistanceMode = BrushDistanceMode.Linear };
            for (int f = 1; f <= 10; f++)
            {
                s.Grab(seed, LeftMotion(pivot, f / 10f), o);
                s.Solve(20);
            }
            s.Settle(0.005f, 3000, out _);
            // つまんだ中心と鏡像の中心の動きが、鏡に映した関係になっているか
            int m = map[seed];
            Vector3 seedMove = s.Positions[seed] - rest[seed];
            seedMismatch = Vector3.Distance(s.Positions[m] - rest[m], PlaneX.ReflectDirection(seedMove));
            return Asymmetry(s, map);
        }
        float withMirror = Drag(true, out float seedWith);
        float withoutMirror = Drag(false, out float seedWithout);
        string message = $"左右のずれ 対称あり {withMirror * 1000:F2}mm / なし {withoutMirror * 1000:F2}mm、中心どうし {seedWith * 1000:F2}mm / {seedWithout * 1000:F2}mm";
        Assert.True(seedWith < 0.002f && seedWithout > 0.01f, message);
        Assert.True(withMirror < withoutMirror, message);
    }

    [Fact]
    public void Mirror_OnCenterLine_KeepsSymmetry()
    {
        var probe = SymmetricSkirt();
        int[] map = MirrorMap(probe);
        // 前の中央の裾をつまみ、横へずれる動きと、斜めの軸の回転を与える
        int seed = probe.FindNearestParticle(new Vector3(0f, TopY - 0.25f, -0.3f));
        Vector3 pivot = probe.Positions[seed] + new Vector3(0.01f, 0f, 0f);
        var translation = new Vector3(0.03f, 0.01f, -0.02f);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 1f, 0f)), 0.4f);
        // 鏡の面に沿う成分だけにした動き（平行移動と中心は x を 0 に、回転は x 軸まわりのひねりだけ）
        var symmetricTranslation = new Vector3(0f, translation.Y, translation.Z);
        var symmetricPivot = new Vector3(0f, pivot.Y, pivot.Z);
        static Quaternion Twist(Quaternion q) => Quaternion.Normalize(new Quaternion(q.X, 0f, 0f, q.W));

        ClothSolver Run(bool mirror, bool projected, int iterations, out int mirrorParticle)
        {
            var solver = SymmetricSkirt();
            if (iterations == 0) solver.BrushCompliance = 0f;
            var options = new BrushOptions { Radius = 0.04f, Mirror = mirror ? PlaneX : (MirrorPlane?)null };
            for (int f = 1; f <= 10; f++)
            {
                var motion = projected
                    ? new BrushMotion(symmetricTranslation * f / 10f, Twist(Quaternion.Slerp(Quaternion.Identity, rotation, f / 10f)), 1f, symmetricPivot)
                    : new BrushMotion(translation * f / 10f, Quaternion.Slerp(Quaternion.Identity, rotation, f / 10f), 1f, pivot);
                solver.Grab(seed, motion, options);
                solver.Solve(iterations);
            }
            mirrorParticle = solver.MirrorParticle;
            if (iterations > 0) solver.Settle(0.005f, 3000, out _);
            return solver;
        }

        // 目標：範囲の粒子は左右（x）へ動かない
        ClothSolver goals = Run(true, false, 0, out int mirrorParticle);
        Assert.Equal(-1, mirrorParticle);
        int grabbed = 0;
        for (int i = 0; i < goals.Model.ParticleCount; i++)
        {
            if (!goals.IsGrabbed(i)) continue;
            grabbed++;
            Assert.True(MathF.Abs(goals.Positions[i].X - goals.Model.RestPositions[i].X) < Tolerance, $"粒子 {i}");
        }
        Assert.True(grabbed > 10);

        // 鏡の面に沿う成分だけにした動きを、対称なしで与えたときと同じ目標になる
        ClothSolver projected = Run(false, true, 0, out _);
        for (int i = 0; i < map.Length; i++) Assert.True(Vector3.Distance(goals.Positions[i], projected.Positions[i]) < Tolerance, $"粒子 {i}");

        // 布全体も、対称なしで同じ動きを与えたときより、ずっと左右対称に近い
        // （布が余ってたわむ所は形が1つに決まらず、反復の順序で左右に少し差が出るので、比べて確かめる）
        ClothSolver symmetric = Run(true, false, 20, out _);
        // 対称なしで同じ動きを与えると、左右対称が大きく崩れる
        float withMirror = Asymmetry(symmetric, map);
        float withoutMirror = Asymmetry(Run(false, false, 20, out _), map);
        Assert.True(withMirror * 3f < withoutMirror, $"左右のずれ 対称あり {withMirror * 1000:F2}mm / なし {withoutMirror * 1000:F2}mm");
    }

    [Fact]
    public void FindMirrorParticle_StaysOnSameLayer_AndCrossesIslandsByNormal()
    {
        // 外側のスカートと、3mm 内側のペチコート（別の島）
        Cone(120, 35, 0.13f, 0.30f, out var outer, out var outerTriangles);
        Cone(120, 35, 0.127f, 0.297f, out var inner, out var innerTriangles);
        var cloth = ParticleCloth.Build(outer.Concat(inner).ToArray(),
            outerTriangles.Concat(innerTriangles.Select(i => i + outer.Length)).ToArray());
        var solver = cloth.CreateSolver();
        Vector3[] rest = cloth.Model.RestPositions;

        int checkedCount = 0, nearestIsOtherLayer = 0;
        for (int i = 0; i < rest.Length; i += 7)
        {
            int mirror = solver.FindMirrorParticle(i, PlaneX, BrushOptions.DefaultMirrorTolerance);
            Assert.True(mirror >= 0, $"粒子 {i} の鏡像が見つからない");
            Assert.Equal(cloth.ParticleIsland[i], cloth.ParticleIsland[mirror]);
            Assert.True(Vector3.Distance(rest[mirror], PlaneX.Reflect(rest[i])) <= BrushOptions.DefaultMirrorTolerance);
            int nearest = solver.FindNearestParticle(PlaneX.Reflect(rest[i]));
            if (cloth.ParticleIsland[nearest] != cloth.ParticleIsland[i]) nearestIsOtherLayer++;
            checkedCount++;
        }
        // 一番近い粒子が別の層であることがよくある（同じ島を優先しないと、層を移ってしまう）
        Assert.True(nearestIsOtherLayer > checkedCount / 10, $"一番近い粒子が別の層だった数 {nearestIsOtherLayer} / {checkedCount}");

        // 左右の袖のように、別の島に分かれた部品では、外向きが合う粒子を選ぶ
        Cone(60, 20, 0.05f, 0.05f, out var sleeve, out var sleeveTriangles);
        var left = sleeve.Select(v => new Vector3(v.X - 0.3f, v.Y, v.Z)).ToArray();
        var right = sleeve.Select(v => new Vector3(v.X + 0.3f, v.Y, v.Z)).ToArray();
        var sleeves = ParticleCloth.Build(left.Concat(right).ToArray(), sleeveTriangles.Concat(sleeveTriangles.Select(i => i + left.Length)).ToArray());
        var sleeveSolver = sleeves.CreateSolver();
        Vector3[] sleeveRest = sleeves.Model.RestPositions;
        for (int i = 0; i < sleeveRest.Length; i += 5)
        {
            int mirror = sleeveSolver.FindMirrorParticle(i, PlaneX, BrushOptions.DefaultMirrorTolerance);
            Assert.True(mirror >= 0, $"袖の粒子 {i} の鏡像が見つからない");
            Assert.NotEqual(sleeves.ParticleIsland[i], sleeves.ParticleIsland[mirror]);
            Assert.True(Vector3.Distance(sleeveRest[mirror], PlaneX.Reflect(sleeveRest[i])) <= BrushOptions.DefaultMirrorTolerance);
        }
        // 鏡像の位置に何も無ければ -1
        Assert.Equal(-1, sleeveSolver.FindMirrorParticle(0, new MirrorPlane(new Vector3(5f, 0f, 0f), Vector3.UnitX), 0.02f));
    }

    [Fact]
    public void RelaxTowardRest_MovesRegionBackAndKeepsPins()
    {
        var solver = FlatCloth();
        int stride = 21;
        for (int c = 0; c <= 20; c++) solver.SetPinned(20 * stride + c, true);
        int center = solver.FindNearestParticle(new Vector3(0.1f, 0.05f, 0f));
        Vector3 start = solver.Positions[center];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(center, start + new Vector3(0f, 0.02f, 0.08f) * f / 10f, 0.03f);
            solver.Solve(20);
        }
        solver.Release();
        Vector3[] before = solver.Positions.ToArray();
        Vector3[] rest = solver.Model.RestPositions;

        // 中心から 4cm の範囲を、完全に元へ戻す
        int count = solver.RelaxTowardRest(center, 0.04f, 1f, FalloffCurve.Constant, BrushDistanceMode.Linear);
        int expected = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            bool inRange = Vector3.Distance(before[i], before[center]) <= 0.04f && !solver.IsPinned(i);
            if (inRange)
            {
                expected++;
                Assert.Equal(rest[i], solver.Positions[i]);
            }
            else
            {
                Assert.Equal(before[i], solver.Positions[i]);
            }
        }
        Assert.Equal(expected, count);
        Assert.True(count > 10);

        // 半分の強さ・直線の減衰なら、中心は半分だけ戻り、端はほとんど戻らない
        var other = FlatCloth();
        for (int c = 0; c <= 20; c++) other.SetPinned(20 * stride + c, true);
        other.SetPositions(before);
        int pinnedNear = 20 * stride + 10;
        other.RelaxTowardRest(center, 0.3f, 0.5f, FalloffCurve.Linear, BrushDistanceMode.Surface);
        Assert.True(Vector3.Distance(Vector3.Lerp(before[center], rest[center], 0.5f), other.Positions[center]) < Tolerance);
        Assert.Equal(rest[pinnedNear], other.Positions[pinnedNear]);
        int far = 20 * stride - 1;   // 右上の角の近く（ピンの1つ下）
        Assert.True(Vector3.Distance(before[far], other.Positions[far]) <= 0.5f * Vector3.Distance(before[far], rest[far]) + Tolerance);
    }

    [Fact]
    public void Grab_RejectsInvalidOptions()
    {
        var solver = FlatCloth();
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.Grab(0, BrushMotion.Identity, new BrushOptions { Radius = -1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => solver.Grab(-1, BrushMotion.Identity, null));
        Assert.Throws<ArgumentException>(() => solver.Grab(0, BrushMotion.Identity, new BrushOptions { Mirror = default(MirrorPlane) }));
        Assert.Throws<ArgumentException>(() => new MirrorPlane(Vector3.Zero, Vector3.Zero));
        // options が null なら中心の1点だけ
        solver.Grab(5, BrushMotion.FromTranslation(new Vector3(0f, 0f, 0.01f)), null);
        Assert.Equal(1, solver.BrushParticleCount);
    }
}
