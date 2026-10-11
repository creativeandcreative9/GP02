using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// つまんだ範囲を柔らかい制約で引き寄せる動き（ClothSolver.BrushCompliance）と、
/// 元の形より内側へ入ってよい深さ（SetRestSurfaceAllowance）のテスト。
/// </summary>
public class SoftBrushTests
{
    const float Top = 0.13f, Bottom = 0.30f, Length = 0.35f, TopY = 0.8f;

    static float RestRadiusAt(float y) => Top + (Bottom - Top) * ((TopY - y) / Length);

    static ParticleCloth Skirt()
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        const int around = 120, rings = 35;
        for (int r = 0; r <= rings; r++)
        {
            float f = r / (float)rings;
            for (int a = 0; a < around; a++)
            {
                float angle = 2f * MathF.PI * a / around;
                v.Add(new Vector3(MathF.Cos(angle) * RestRadiusAt(TopY - Length * f), TopY - Length * f, MathF.Sin(angle) * RestRadiusAt(TopY - Length * f)));
            }
        }
        for (int r = 0; r < rings; r++)
        for (int a = 0; a < around; a++)
        {
            int i = r * around + a, j = r * around + (a + 1) % around;
            t.AddRange(new[] { i, j, i + around, j, j + around, i + around });
        }
        return ParticleCloth.Build(v.ToArray(), t.ToArray());
    }

    // ウエストをピン留めし、前側の中ほどを引く（20コマのドラッグ）。つまんだ点の始まりの位置を返す
    static Vector3 Drag(ClothSolver solver, ParticleCloth cloth, Vector3 move, out int grabbed, out float maxLagWhileDragging)
    {
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.Model.RestPositions[i].Y > TopY - cloth.Spacing) solver.SetPinned(i, true);
        }
        grabbed = solver.FindNearestParticle(new Vector3(0f, TopY - 0.25f, -RestRadiusAt(TopY - 0.25f)));
        Vector3 start = solver.Positions[grabbed];
        maxLagWhileDragging = 0f;
        for (int f = 1; f <= 20; f++)
        {
            Vector3 target = start + move * f / 20f;
            solver.Grab(grabbed, target, 0.04f);
            solver.Solve(20);
            if (f <= 5) maxLagWhileDragging = MathF.Max(maxLagWhileDragging, Vector3.Distance(solver.Positions[grabbed], target));
        }
        return start;
    }

    [Fact]
    public void UnreachableTarget_StopsShortWithoutLeavingStretch()
    {
        // ウエストから下の布は母線に沿って張っているので、外へ引く目標には布の長さで届かない
        var cloth = Skirt();
        Vector3 move = Vector3.Normalize(new Vector3(0f, 0.4f, -1f)) * 0.10f;

        var fixedSolver = new ClothSolver(cloth.Model, cloth.Hierarchy) { BrushCompliance = 0f, KeepOutsideRestSurface = false };
        Drag(fixedSolver, cloth, move, out _, out _);
        float fixedStretch = fixedSolver.Settle(0.01f, 4000, out _);

        var softSolver = new ClothSolver(cloth.Model, cloth.Hierarchy) { KeepOutsideRestSurface = false };
        Vector3 start = Drag(softSolver, cloth, move, out int grabbed, out _);
        float softStretch = softSolver.Settle(0.01f, 4000, out _);
        float remaining = Vector3.Distance(softSolver.Positions[grabbed], start + move);

        Assert.True(fixedStretch > 0.3f, $"固定したときの伸び {fixedStretch:P1}（このテストの前提）");
        Assert.True(softStretch < 0.06f, $"柔らかくしたときの伸び {softStretch:P1}");
        Assert.True(remaining > 0.01f, $"届かない分だけ手前で止まる（目標までの残り {remaining * 100:F1}cm）");
    }

    [Fact]
    public void ReachableTarget_IsReached_AndDragDoesNotLag()
    {
        // 上端をピン留めした平らな布を手前へ引く。届く目標なので、柔らかくしても目標の近くまで行く
        ProceduralMeshes.Grid(60, 60, 0.6f, 0.6f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = new ClothSolver(cloth.Model, cloth.Hierarchy);
        for (int i = 0; i < cloth.Model.ParticleCount; i++)
        {
            if (cloth.Model.RestPositions[i].Y > 0.6f - cloth.Spacing) solver.SetPinned(i, true);
        }
        int grabbed = solver.FindNearestParticle(new Vector3(0.3f, 0.2f, 0f));
        Vector3 start = solver.Positions[grabbed];
        var move = new Vector3(0f, 0.02f, -0.1f);
        float maxLag = 0f;
        for (int f = 1; f <= 20; f++)
        {
            Vector3 target = start + move * f / 20f;
            solver.Grab(grabbed, target, 0.04f);
            solver.Solve(20);
            maxLag = MathF.Max(maxLag, Vector3.Distance(solver.Positions[grabbed], target));
        }
        float stretch = solver.Settle(0.01f, 8000, out _);

        Assert.True(maxLag < 0.003f, $"ドラッグ中の遅れ {maxLag * 1000:F1}mm");
        Assert.True(Vector3.Distance(solver.Positions[grabbed], start + move) < 0.005f, "目標の 5mm 以内に着く");
        Assert.True(stretch < 0.02f, $"仕上げ後の伸び {stretch:P1}");
    }

    [Fact]
    public void RestSurfaceAllowance_LetsClothMoveInwardUpToTheGap()
    {
        var cloth = Skirt();
        Vector3 move = Vector3.Normalize(new Vector3(0f, 0.4f, -1f)) * 0.10f;
        float Deepest(float[]? allowance)
        {
            var solver = new ClothSolver(cloth.Model, cloth.Hierarchy);
            solver.SetRestSurfaceAllowance(allowance);
            Drag(solver, cloth, move, out _, out _);
            solver.Settle(0.01f, 2000, out _);
            float deepest = 0f;
            for (int i = 0; i < cloth.Model.ParticleCount; i++)
            {
                if (solver.IsGrabbed(i)) continue;
                Vector3 p = solver.Positions[i];
                deepest = MathF.Max(deepest, RestRadiusAt(p.Y) - MathF.Sqrt(p.X * p.X + p.Z * p.Z));
            }
            return deepest;
        }

        const float gap = 0.02f;
        // 深さは半径の方向で測っているので、面に垂直な方向の深さに直す（円錐の面は母線の傾きの分だけ傾いている）
        float normalFactor = Length / MathF.Sqrt(Length * Length + (Bottom - Top) * (Bottom - Top));
        float none = Deepest(null) * normalFactor;
        float withGap = Deepest(Enumerable.Repeat(gap, cloth.Model.ParticleCount).ToArray()) * normalFactor;

        Assert.True(none < 0.0015f, $"隙間なし: 最も内側 {none * 1000:F1}mm");
        // 隙間の分だけ内側へ入るが、それより深くは入らない（曲面を平面で近似する分の誤差は許す）
        Assert.True(withGap > 0.005f, $"隙間 2cm: 最も内側 {withGap * 1000:F1}mm（隙間を使っている）");
        Assert.True(withGap < gap + 0.002f, $"隙間 2cm: 最も内側 {withGap * 1000:F1}mm");
    }
}
