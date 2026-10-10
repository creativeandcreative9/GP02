using System.Diagnostics;
using System.Numerics;
using GP02.Core;

// 衣装に近い頂点数で、計算の速さと布の伸びを測る。
// 使い方: dotnet run --project tools/bench/Bench -c Release
//
// 想定する操作: 1m四方の布（上端をピン留め）の中央付近を、半径3cmのブラシでつまみ、
// 15コマかけて10cm手前へ引っ張る。

const float Size = 1f;
const int DragFrames = 15;
var offset = new Vector3(0f, 0f, 0.1f);
int[] resolutions = { 50, 100, 170, 220 };
int[] iterationCounts = { 10, 20, 40 };

Console.WriteLine("頂点数 | 準備(ms) | つまみ始め(ms) | 反復回数 | 1コマ(ms) | 最大の伸び");
Console.WriteLine("---|---|---|---|---|---");

foreach (int n in resolutions)
{
    ProceduralMeshes.Grid(n, n, Size, Size, out var vertices, out var triangles);

    var buildTimer = Stopwatch.StartNew();
    var model = ClothModel.Build(vertices, triangles);
    buildTimer.Stop();

    foreach (int iterations in iterationCounts)
    {
        var solver = new ClothSolver(model) { InfluenceRadius = 0.15f };
        for (int c = 0; c <= n; c++) solver.SetPinned(n * (n + 1) + c, true);
        int center = solver.FindNearestParticle(new Vector3(0.5f, 0.3f, 0f));
        Vector3 start = solver.Positions[center];

        // 最初の Solve はピンの距離の計算を含むので、測定から外す
        solver.Solve(0);

        var grabTimer = Stopwatch.StartNew();
        solver.Grab(center, start, 0.03f);
        grabTimer.Stop();

        var frameTimer = new Stopwatch();
        for (int f = 1; f <= DragFrames; f++)
        {
            solver.Grab(center, start + offset * f / DragFrames, 0.03f);
            frameTimer.Start();
            solver.Solve(iterations);
            frameTimer.Stop();
        }

        Console.WriteLine($"{vertices.Length} | {buildTimer.Elapsed.TotalMilliseconds:F0} | {grabTimer.Elapsed.TotalMilliseconds:F1} | {iterations} | " +
            $"{frameTimer.Elapsed.TotalMilliseconds / DragFrames:F1} | {MaxStretch(model, solver.Positions):P2}");
    }
}

static float MaxStretch(ClothModel model, ReadOnlySpan<Vector3> positions)
{
    float max = 0f;
    for (int k = 0; k < model.StretchRestLengths.Length; k++)
    {
        float length = Vector3.Distance(positions[model.StretchPairs[2 * k]], positions[model.StretchPairs[2 * k + 1]]);
        max = MathF.Max(max, length / model.StretchRestLengths[k] - 1f);
    }
    return max;
}
