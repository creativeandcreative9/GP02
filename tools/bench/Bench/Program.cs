using System.Diagnostics;
using System.Numerics;
using GP02.Core;

// メッシュの細かさを変えながら、粒子化（1cm間隔）したときの速さと布の伸びを測る。
// 使い方: dotnet run --project tools/bench/Bench -c Release
//
// 想定する操作: 布（上端をピン留め）の下寄りを、半径3cmのブラシでつまみ、
// 15コマかけて手前へ10cm（少し上へ2cm）引っ張る。
// 布の大きさは、スカート1枚（約0.4㎡）と、1㎡の2通り。

const int DragFrames = 15;
var offset = new Vector3(0f, 0.02f, 0.1f);
float[] sizes = { 0.63f, 1f };
int[] meshCells = { 50, 100, 220 };
int[] iterationCounts = { 20, 40 };

Console.WriteLine("布(m) | メッシュの頂点 | 粒子 | 準備(ms) | 反復 | 1コマ(ms) | 粒子間の伸び 最大 / 99% | メッシュの辺の伸び 95%");
Console.WriteLine("---|---|---|---|---|---|---|---");

foreach (float size in sizes)
foreach (int cells in meshCells)
{
    ProceduralMeshes.Grid(cells, cells, size, size, out var vertices, out var triangles);
    var buildTimer = Stopwatch.StartNew();
    var cloth = ParticleCloth.Build(vertices, triangles);
    buildTimer.Stop();

    foreach (int iterations in iterationCounts)
    {
        var solver = new ClothSolver(cloth.Model) { InfluenceRadius = 0.15f };
        for (int p = 0; p < cloth.Model.ParticleCount; p++)
        {
            if (cloth.Model.RestPositions[p].Y > size - cloth.Spacing) solver.SetPinned(p, true);
        }
        int center = solver.FindNearestParticle(new Vector3(size / 2f, 0.3f, 0f));
        Vector3 start = solver.Positions[center];

        // 最初の Solve はピンの距離の計算を含むので、測定から外す
        solver.Solve(0);

        var frameTimer = new Stopwatch();
        for (int f = 1; f <= DragFrames; f++)
        {
            solver.Grab(center, start + offset * f / DragFrames, 0.03f);
            frameTimer.Start();
            solver.Solve(iterations);
            frameTimer.Stop();
        }

        var particleStretch = Stretches(solver.Positions, cloth.Model.StretchPairs, cloth.Model.RestPositions);
        var welded = new Vector3[cloth.Topology.Positions.Length];
        cloth.DeformWelded(solver.Positions, welded);
        var meshStretch = MeshEdgeStretches(cloth.Topology, welded);

        Console.WriteLine($"{size} | {vertices.Length} | {cloth.Model.ParticleCount} | {buildTimer.Elapsed.TotalMilliseconds:F0} | {iterations} | " +
            $"{frameTimer.Elapsed.TotalMilliseconds / DragFrames:F1} | {particleStretch[^1]:P1} / {Percentile(particleStretch, 0.99f):P1} | {Percentile(meshStretch, 0.95f):P1}");
    }
}

static List<float> Stretches(ReadOnlySpan<Vector3> positions, int[] pairs, Vector3[] rest)
{
    var list = new List<float>(pairs.Length / 2);
    for (int k = 0; k < pairs.Length; k += 2)
    {
        int a = pairs[k], b = pairs[k + 1];
        list.Add(Vector3.Distance(positions[a], positions[b]) / Vector3.Distance(rest[a], rest[b]) - 1f);
    }
    list.Sort();
    return list;
}

static List<float> MeshEdgeStretches(MeshTopology topology, Vector3[] positions)
{
    var pairs = new List<int>();
    var tris = topology.Triangles;
    for (int t = 0; t < tris.Length; t += 3)
    for (int e = 0; e < 3; e++)
    {
        pairs.Add(tris[t + e]);
        pairs.Add(tris[t + (e + 1) % 3]);
    }
    return Stretches(positions, pairs.ToArray(), topology.Positions);
}

static float Percentile(List<float> sorted, float p) => sorted[(int)((sorted.Count - 1) * p)];
