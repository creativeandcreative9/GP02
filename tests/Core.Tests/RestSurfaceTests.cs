using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>元の形より内側に入らない制約（ClothSolver.KeepOutsideRestSurface）のテスト。</summary>
public class RestSurfaceTests
{
    const float Top = 0.13f, Bottom = 0.30f, Length = 0.35f, TopY = 0.8f;

    // スカート状の円錐台（Y が上）。outward = false なら三角形の向きを逆にする（裏返しのメッシュ）
    static void Cone(int around, int rings, bool outward, out Vector3[] vertices, out int[] triangles)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        for (int r = 0; r <= rings; r++)
        {
            float f = r / (float)rings;
            float radius = Top + (Bottom - Top) * f;
            for (int a = 0; a < around; a++)
            {
                float angle = 2f * MathF.PI * a / around;
                v.Add(new Vector3(MathF.Cos(angle) * radius, TopY - Length * f, MathF.Sin(angle) * radius));
            }
        }
        for (int r = 0; r < rings; r++)
        for (int a = 0; a < around; a++)
        {
            int i = r * around + a, j = r * around + (a + 1) % around;
            int k = i + around, l = j + around;
            // 角度が増える向きと下向きで、外向きの面になる並び
            if (outward) t.AddRange(new[] { i, j, k, j, l, k });
            else t.AddRange(new[] { i, k, j, j, k, l });
        }
        vertices = v.ToArray();
        triangles = t.ToArray();
    }

    // 粒子の高さでの、元の円錐台の半径（これより内側なら元の形より内側）
    static float RestRadiusAt(float y) => Top + (Bottom - Top) * ((TopY - y) / Length);

    static float RadialDepth(Vector3 p) => RestRadiusAt(p.Y) - MathF.Sqrt(p.X * p.X + p.Z * p.Z);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestNormals_PointOutward_EvenForInsideOutMesh(bool outward)
    {
        Cone(120, 35, outward, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        Vector3[] normals = cloth.Model.RestNormals;

        int outwardCount = 0;
        for (int i = 0; i < normals.Length; i++)
        {
            Vector3 p = cloth.Model.RestPositions[i];
            if (Vector3.Dot(normals[i], new Vector3(p.X, 0f, p.Z)) > 0f) outwardCount++;
        }
        Assert.True(outwardCount > 0.99f * normals.Length, $"外向き {outwardCount} / {normals.Length}");
    }

    [Fact]
    public void RestNormals_AreZero_ForFlatClothAndDoubleSidedFaces()
    {
        ProceduralMeshes.Grid(30, 30, 0.3f, 0.3f, out var flatVertices, out var flatTriangles);
        var flat = ParticleCloth.Build(flatVertices, flatTriangles);
        Assert.All(flat.Model.RestNormals, n => Assert.Equal(Vector3.Zero, n));

        // 裏面用に、同じ位置の頂点で向きを逆にした三角形を重ねる
        Cone(80, 20, true, out var vertices, out var triangles);
        int count = vertices.Length;
        var doubled = vertices.Concat(vertices).ToArray();
        var doubledTriangles = new List<int>(triangles);
        for (int k = 0; k < triangles.Length; k += 3)
        {
            doubledTriangles.Add(triangles[k] + count);
            doubledTriangles.Add(triangles[k + 2] + count);
            doubledTriangles.Add(triangles[k + 1] + count);
        }
        var cloth = ParticleCloth.Build(doubled, doubledTriangles.ToArray());
        Assert.All(cloth.Model.RestNormals, n => Assert.Equal(Vector3.Zero, n));
    }

    // 前側の裾を手前へ 10cm 引っ張り、手を止めて仕上げる。つまんだ範囲以外で、元の形より最も内側に入った深さを返す
    static float PullFrontAndMeasureDepth(bool keepOutside, IClothSolverKernel? kernel = null)
    {
        Cone(120, 35, true, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { KeepOutsideRestSurface = keepOutside, Kernel = kernel };
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.Model.RestPositions[i].Y > TopY - cloth.Spacing) solver.SetPinned(i, true);
        }
        int grabbed = solver.FindNearestParticle(new Vector3(0f, TopY - 0.25f, -RestRadiusAt(TopY - 0.25f)));
        Vector3 start = solver.Positions[grabbed];
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(grabbed, start + new Vector3(0f, 0.04f, -0.10f) * f / 10f, 0.04f);
            solver.Solve(20);
        }
        solver.Settle(0.01f, 2000, out _);

        float deepest = 0f;
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (solver.IsGrabbed(i)) continue;
            deepest = MathF.Max(deepest, RadialDepth(solver.Positions[i]));
        }
        return deepest;
    }

    // 前側の中ほどをつまんで、つまんだ点を中心に縮める（範囲は中心へ寄るので、円錐の面より内側へ向かう）。
    // つまんだ範囲を含めて、元の形より最も内側に入った深さを返す
    static float ShrinkFrontAndMeasureDepth(bool keepOutside, float brushCompliance)
    {
        Cone(120, 35, true, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { KeepOutsideRestSurface = keepOutside, BrushCompliance = brushCompliance };
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.Model.RestPositions[i].Y > TopY - cloth.Spacing) solver.SetPinned(i, true);
        }
        int grabbed = solver.FindNearestParticle(new Vector3(0f, TopY - 0.25f, -RestRadiusAt(TopY - 0.25f)));
        Vector3 pivot = solver.Positions[grabbed] + new Vector3(0f, 0f, 0.05f);   // 面より 5cm 内側を中心に縮める
        var options = new BrushOptions { Radius = 0.06f };
        for (int f = 1; f <= 10; f++)
        {
            solver.Grab(grabbed, new BrushMotion(Vector3.Zero, Quaternion.Identity, 1f - 0.5f * f / 10f, pivot), options);
            solver.Solve(20);
        }
        solver.Settle(0.01f, 2000, out _);

        float deepest = 0f;
        for (int i = 0; i < cloth.Model.ParticleCount; i++) deepest = MathF.Max(deepest, RadialDepth(solver.Positions[i]));
        return deepest;
    }

    [Fact]
    public void KeepOutside_AlsoKeepsBrushTargetsOutside()
    {
        // 制約なしでは、縮めた範囲が目標どおり内側へ入る（このテストが効いていることの確認）
        float without = ShrinkFrontAndMeasureDepth(false, ClothSolver.DefaultBrushCompliance);
        Assert.True(without > 0.01f, $"制約なしの最大の深さ {without * 1000:F1}mm");
        // 柔らかいつまみでも固定のつまみでも、つまんだ範囲を含めて元の形より内側に入らない
        float soft = ShrinkFrontAndMeasureDepth(true, ClothSolver.DefaultBrushCompliance);
        Assert.True(soft < 0.0015f, $"柔らかいつまみの最大の深さ {soft * 1000:F2}mm");
        float fixedBrush = ShrinkFrontAndMeasureDepth(true, 0f);
        Assert.True(fixedBrush < 0.0015f, $"固定のつまみの最大の深さ {fixedBrush * 1000:F2}mm");
    }

    [Fact]
    public void KeepOutside_PreventsSinkingInsideRestShape()
    {
        float without = PullFrontAndMeasureDepth(false);
        float with = PullFrontAndMeasureDepth(true);

        // 制約なしでは横の布が内側へ引き込まれる（このテストが効いていることの確認）
        Assert.True(without > 0.005f, $"制約なしの最大の深さ {without * 1000:F1}mm");
        // 曲面を1cm間隔の平面で近似するので、1mm 程度の誤差は許す
        Assert.True(with < 0.0015f, $"制約ありの最大の深さ {with * 1000:F2}mm");
    }

    [Fact]
    public void ReferenceKernel_MatchesBuiltInPath_WithRestSurface()
    {
        // データだけで計算する参照カーネル（ClothSolverKernelTests）と一致すれば、Burst 版にも必要なデータが渡っている
        Cone(80, 24, true, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        Vector3[] Run(IClothSolverKernel? kernel)
        {
            var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = kernel };
            for (int i = 0; i < cloth.Model.ParticleCount; i++)
            {
                if (cloth.Model.RestPositions[i].Y > TopY - cloth.Spacing) solver.SetPinned(i, true);
            }
            int grabbed = solver.FindNearestParticle(new Vector3(0f, TopY - 0.25f, -RestRadiusAt(TopY - 0.25f)));
            Vector3 start = solver.Positions[grabbed];
            for (int f = 1; f <= 5; f++)
            {
                solver.Grab(grabbed, start + new Vector3(0f, 0.02f, -0.08f) * f / 5f, 0.03f);
                solver.Solve(10);
            }
            return solver.Positions.ToArray();
        }

        Vector3[] expected = Run(null);
        Vector3[] actual = Run(new ClothSolverKernelTests.ReferenceKernel());
        for (int i = 0; i < expected.Length; i++) Assert.True(Vector3.Distance(expected[i], actual[i]) < 1e-5f, $"粒子 {i}");
    }
}
