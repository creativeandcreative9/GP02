using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ClothSolverTests
{
    const float Tolerance = 1e-5f;
    const int Columns = 20, Rows = 20;
    const float Size = 1f;

    static ClothSolver CreateHangingCloth(out int bottomCenter)
    {
        ProceduralMeshes.Grid(Columns, Rows, Size, Size, out var vertices, out var triangles);
        var solver = new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
        int stride = Columns + 1;
        for (int c = 0; c <= Columns; c++) solver.SetPinned(Rows * stride + c, true);
        bottomCenter = Columns / 2;
        return solver;
    }

    // 指定位置まで、少しずつ引っ張る（実際のドラッグ操作と同じ）
    static void Drag(ClothSolver solver, int particle, Vector3 offset, int steps = 30, int iterations = 100)
    {
        Vector3 start = solver.Positions[particle];
        for (int s = 1; s <= steps; s++)
        {
            solver.Grab(particle, start + offset * s / steps);
            solver.Solve(iterations);
        }
    }

    static float MaxStretch(ClothSolver solver)
    {
        var model = solver.Model;
        float max = 0f;
        for (int k = 0; k < model.StretchRestLengths.Length; k++)
        {
            float length = Vector3.Distance(solver.Positions[model.StretchPairs[2 * k]], solver.Positions[model.StretchPairs[2 * k + 1]]);
            max = MathF.Max(max, length / model.StretchRestLengths[k] - 1f);
        }
        return max;
    }

    [Fact]
    public void Solve_WithoutGrab_KeepsShape()
    {
        var solver = CreateHangingCloth(out _);

        solver.Solve(50);

        var rest = solver.Model.RestPositions;
        for (int i = 0; i < rest.Length; i++)
        {
            Assert.True(Vector3.Distance(rest[i], solver.Positions[i]) < Tolerance);
        }
    }

    [Fact]
    public void SetPositions_RestoresSnapshotAndReleasesGrab()
    {
        var solver = CreateHangingCloth(out int grabbed);
        var snapshot = solver.Positions.ToArray();
        Drag(solver, grabbed, new Vector3(0.1f, 0.2f, 0.3f), steps: 5);
        Assert.NotEqual(snapshot[grabbed], solver.Positions[grabbed]);

        solver.SetPositions(snapshot);

        Assert.Equal(-1, solver.GrabbedParticle);
        for (int i = 0; i < snapshot.Length; i++) Assert.Equal(snapshot[i], solver.Positions[i]);
        // 戻した後に解いても、元の形のまま動かない
        solver.Solve(20);
        for (int i = 0; i < snapshot.Length; i++) Assert.True(Vector3.Distance(snapshot[i], solver.Positions[i]) < Tolerance);
    }

    [Fact]
    public void SetPositions_RejectsWrongLength()
    {
        var solver = CreateHangingCloth(out _);

        Assert.Throws<ArgumentException>(() => solver.SetPositions(new Vector3[3]));
    }

    [Fact]
    public void Grab_MovesGrabbedParticleToTarget()
    {
        var solver = CreateHangingCloth(out int grabbed);
        solver.BrushCompliance = 0f;   // つまんだ点を目標に固定する
        var offset = new Vector3(0.1f, 0.2f, 0.3f);
        Vector3 target = solver.Positions[grabbed] + offset;

        Drag(solver, grabbed, offset);

        Assert.True(Vector3.Distance(target, solver.Positions[grabbed]) < Tolerance);
    }

    [Fact]
    public void Grab_DoesNotMovePinnedParticles()
    {
        var solver = CreateHangingCloth(out int grabbed);

        Drag(solver, grabbed, new Vector3(0.2f, 0.3f, 0.4f));

        var rest = solver.Model.RestPositions;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!solver.IsPinned(i)) continue;
            Assert.Equal(rest[i], solver.Positions[i]);
        }
    }

    [Fact]
    public void Grab_KeepsClothLength()
    {
        var solver = CreateHangingCloth(out int grabbed);

        // 1mの布の端を約47cm引っ張る、かなり大きな操作
        Drag(solver, grabbed, new Vector3(0.15f, 0.25f, 0.35f));

        Assert.True(MaxStretch(solver) < 0.05f, $"最大の伸び: {MaxStretch(solver):P2}");
    }

    [Fact]
    public void Grab_StretchesMuchLessThanFalloffOnly()
    {
        var particle = CreateHangingCloth(out int grabbed);
        var falloffOnly = CreateHangingCloth(out _);
        var offset = new Vector3(0.15f, 0.25f, 0.35f);

        Drag(particle, grabbed, offset);
        // 制約を解かない＝周りへ配るだけの、範囲指定方式と同じ動き
        Drag(falloffOnly, grabbed, offset, iterations: 0);

        float particleStretch = MaxStretch(particle);
        float falloffStretch = MaxStretch(falloffOnly);
        Assert.True(particleStretch * 5f < falloffStretch, $"粒子方式: {particleStretch:P2}, 範囲指定方式: {falloffStretch:P2}");
    }

    [Fact]
    public void Grab_FarParticlesMoveLessThanNearOnes()
    {
        var solver = CreateHangingCloth(out int grabbed);
        int stride = Columns + 1;
        int near = grabbed + stride;               // つまんだ点の1つ上
        int far = (Rows - 2) * stride;             // 左上のピンの近く

        Drag(solver, grabbed, new Vector3(0f, 0f, 0.3f));

        var rest = solver.Model.RestPositions;
        float nearMove = Vector3.Distance(rest[near], solver.Positions[near]);
        float farMove = Vector3.Distance(rest[far], solver.Positions[far]);
        Assert.True(nearMove > 0.1f, $"近い点の移動: {nearMove}");
        Assert.True(farMove < nearMove * 0.2f, $"遠い点の移動: {farMove}, 近い点の移動: {nearMove}");
    }

    [Fact]
    public void Grab_WithBrush_MovesWholeRegionTogether()
    {
        var solver = CreateHangingCloth(out int center);
        solver.BrushCompliance = 0f;   // つまんだ範囲を目標に固定する
        const float brush = 0.1f;   // 格子の間隔は 0.05m なので、2マス分
        var offset = new Vector3(0f, 0.05f, 0.2f);
        Vector3 start = solver.Positions[center];

        for (int s = 1; s <= 20; s++)
        {
            solver.Grab(center, start + offset * s / 20, brush);
            solver.Solve(50);
        }

        var rest = solver.Model.RestPositions;
        int grabbedCount = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!solver.IsGrabbed(i)) continue;
            grabbedCount++;
            Assert.True(Vector3.Distance(rest[i] + offset, solver.Positions[i]) < Tolerance, $"粒子 {i}");
        }
        Assert.True(grabbedCount > 1, $"つまんだ粒子の数: {grabbedCount}");
    }

    [Fact]
    public void Grab_WithBrush_IgnoresDisconnectedLayer()
    {
        // 1cm離して重ねた2枚の布（重ね着を想定）
        ProceduralMeshes.Grid(10, 10, 1f, 1f, out var front, out var frontTriangles);
        var back = front.Select(v => v + new Vector3(0f, 0f, -0.01f)).ToArray();
        var vertices = front.Concat(back).ToArray();
        var triangles = frontTriangles.Concat(frontTriangles.Select(i => i + front.Length)).ToArray();
        var solver = new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
        int center = 5 * 11 + 5;

        solver.Grab(center, solver.Positions[center] + new Vector3(0f, 0f, 0.1f), brushRadius: 0.2f);
        solver.Solve(50);

        var rest = solver.Model.RestPositions;
        for (int i = front.Length; i < vertices.Length; i++)
        {
            Assert.False(solver.IsGrabbed(i));
            Assert.Equal(rest[i], solver.Positions[i]);
        }
    }

    [Fact]
    public void Grab_PinnedCenter_DoesNotAccumulate()
    {
        var solver = CreateHangingCloth(out int center);
        solver.SetPinned(center, true);
        Vector3 target = solver.Positions[center] + new Vector3(0f, 0f, 0.2f);

        var snapshots = new List<Vector3[]>();
        for (int s = 0; s < 6; s++)
        {
            solver.Grab(center, target);
            solver.Solve(100);
            snapshots.Add(solver.Positions.ToArray());
        }

        // 同じ目標のまま解き続けると、変化はだんだん小さくなり、元の形から離れていかない
        // （移動を毎回足してしまうと、変化が減らずに形がずれ続ける）
        var rest = solver.Model.RestPositions;
        float firstChange = snapshots[0].Zip(snapshots[1], Vector3.Distance).Max();
        float lastChange = snapshots[4].Zip(snapshots[5], Vector3.Distance).Max();
        float firstOffset = snapshots[1].Zip(rest, Vector3.Distance).Max();
        float lastOffset = snapshots[5].Zip(rest, Vector3.Distance).Max();
        Assert.True(lastChange < firstChange * 0.5f, $"最初の変化量: {firstChange}, 最後の変化量: {lastChange}");
        Assert.True(lastOffset <= firstOffset, $"元の形からのずれ: {firstOffset} → {lastOffset}");
        Assert.Equal(rest[center], solver.Positions[center]);
    }

    [Fact]
    public void Grab_OnlySolvesNearTheGrab()
    {
        ProceduralMeshes.Grid(Columns, Rows, Size, Size, out var vertices, out var triangles);
        var solver = new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)))
        {
            InfluenceRadius = 0.2f,
            ActiveMargin = 0.05f,
        };
        int center = Columns / 2;

        Drag(solver, center, new Vector3(0f, 0.05f, 0.2f));

        // 計算範囲 = 影響範囲 + 余白 + 引っ張った距離。
        // メッシュに沿った距離は直線距離以上なので、直線距離が計算範囲より遠い粒子は一切動かない
        var rest = solver.Model.RestPositions;
        float limit = 0.2f + 0.05f + new Vector3(0f, 0.05f, 0.2f).Length();
        int untouched = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (Vector3.Distance(rest[i], rest[center]) <= limit + 1e-4f) continue;
            Assert.Equal(rest[i], solver.Positions[i]);
            untouched++;
        }
        Assert.True(untouched > 0);
        Assert.True(solver.ActiveParticleCount < rest.Length);
    }

    [Fact]
    public void Grab_IsDeterministic()
    {
        var a = CreateHangingCloth(out int grabbed);
        var b = CreateHangingCloth(out _);
        var offset = new Vector3(0.1f, 0.1f, 0.2f);

        Drag(a, grabbed, offset);
        Drag(b, grabbed, offset);

        Assert.True(a.Positions.SequenceEqual(b.Positions));
    }

    [Fact]
    public void Strokes_Accumulate()
    {
        var solver = CreateHangingCloth(out int first);
        int second = 2 * (Columns + 1) + 1;        // 左下寄りの、離れた点
        var offset = new Vector3(0f, 0f, 0.2f);
        Drag(solver, first, offset);
        solver.Release();
        float firstMove = Vector3.Distance(solver.Model.RestPositions[first], solver.Positions[first]);

        Drag(solver, second, offset);

        // 2回目の編集のあとも、1回目に動かした点は大部分が残る。
        // 布は伸びないので、2回目に引いた分の布はどこかから来る。1回目の所から少し持ってくるのは物理的に正しい（残したい所はピン留めする）
        float firstMoveAfter = Vector3.Distance(solver.Model.RestPositions[first], solver.Positions[first]);
        Assert.True(firstMoveAfter > firstMove * 0.7f, $"1回目: {firstMove}, 2回目のあと: {firstMoveAfter}");
    }

    [Fact]
    public void ResetToRest_RestoresShapeAndKeepsPins()
    {
        var solver = CreateHangingCloth(out int grabbed);
        Drag(solver, grabbed, new Vector3(0f, 0f, 0.2f));

        solver.ResetToRest();

        Assert.True(solver.Positions.SequenceEqual(solver.Model.RestPositions));
        Assert.Equal(-1, solver.GrabbedParticle);
        Assert.True(solver.IsPinned(Rows * (Columns + 1)));
    }

    [Fact]
    public void FindNearestParticle_ReturnsClosest()
    {
        var solver = CreateHangingCloth(out int bottomCenter);

        int found = solver.FindNearestParticle(new Vector3(0.5f + 0.01f, -0.02f, 0.03f));

        Assert.Equal(bottomCenter, found);
    }

    // 内部では原点をずらして計算するので、置き場所によらず同じ形になることを確かめる
    [Fact]
    public void Result_DoesNotDependOnWherePlaced()
    {
        var shift = new Vector3(12.3f, -4.5f, 6.7f);
        ProceduralMeshes.Grid(Columns, Rows, Size, Size, out var vertices, out var triangles);
        var moved = vertices.Select(v => v + shift).ToArray();
        var atOrigin = new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(vertices, triangles)));
        var elsewhere = new ClothSolver(ClothModel.FromTriangleMesh(MeshTopology.Build(moved, triangles)));
        int stride = Columns + 1;
        for (int c = 0; c <= Columns; c++)
        {
            atOrigin.SetPinned(Rows * stride + c, true);
            elsewhere.SetPinned(Rows * stride + c, true);
        }

        int bottomCenter = Columns / 2;
        Drag(atOrigin, bottomCenter, new Vector3(0f, 0.1f, 0.2f), steps: 10, iterations: 20);
        Drag(elsewhere, bottomCenter, new Vector3(0f, 0.1f, 0.2f), steps: 10, iterations: 20);

        // 座標が大きいほど float の丸め誤差が増えるので、許容誤差は少し広げる
        for (int i = 0; i < atOrigin.Positions.Length; i++)
        {
            Assert.True(Vector3.Distance(atOrigin.Positions[i] + shift, elsewhere.Positions[i]) < 1e-4f, $"粒子 {i}");
        }
    }
}
