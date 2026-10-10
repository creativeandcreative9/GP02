using System.Globalization;
using System.Numerics;
using System.Text;
using GP02.Core;

// 吊るした布の下端中央をつまんで引っ張る様子を、2つの方式で計算してJSONに書き出す。
//   falloff : 範囲指定で動かす方式（制約を解かない）
//   particle: 粒子方式（1cm間隔の粒子で、布の長さと曲げの制約を解く）
// どちらも同じ粒子とメッシュを使い、メッシュの頂点は粒子についていく。描画は render.js が行う。

string output = args.Length > 0 ? args[0] : Path.Combine("out", "frames.json");

const int MeshCells = 40;
const float Size = 0.6f;
const int DragFrames = 45, HoldFrames = 45, Iterations = 40;
const float Brush = 0.02f;
var offset = new Vector3(0.08f, 0.15f, 0.2f);

ProceduralMeshes.Grid(MeshCells, MeshCells, Size, Size, out var vertices, out var triangles);
var cloth = ParticleCloth.Build(vertices, triangles);
var particle = CreateSolver(cloth);
var falloff = CreateSolver(cloth);
int grabbed = particle.FindNearestParticle(new Vector3(Size / 2f, 0f, 0f));
Vector3 start = cloth.Model.RestPositions[grabbed];
int grabbedVertex = NearestVertex(cloth.Topology.Positions, start);
Console.WriteLine($"メッシュの頂点 {vertices.Length} / 粒子 {cloth.Model.ParticleCount}");

var json = new StringBuilder();
json.Append("{\"triangles\":[").AppendJoin(',', cloth.Topology.Triangles).Append("],");
json.Append("\"grabbed\":").Append(grabbedVertex).Append(",\"frames\":[");

var welded = new Vector3[cloth.Topology.Positions.Length];
for (int f = 0; f <= DragFrames + HoldFrames; f++)
{
    float t = MathF.Min(1f, (float)f / DragFrames);
    Vector3 target = start + offset * Ease(t);
    particle.Grab(grabbed, target, Brush);
    particle.Solve(Iterations);
    falloff.Grab(grabbed, target, Brush);
    falloff.Solve(0);

    if (f > 0) json.Append(',');
    json.Append("{\"particle\":");
    cloth.DeformWelded(particle.Positions, welded);
    AppendFrame(json, cloth.Topology, welded);
    json.Append(",\"falloff\":");
    cloth.DeformWelded(falloff.Positions, welded);
    AppendFrame(json, cloth.Topology, welded);
    json.Append('}');
}
json.Append("]}");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, json.ToString());
Console.WriteLine($"書き出し: {output}");

static ClothSolver CreateSolver(ParticleCloth cloth)
{
    var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.2f };
    // 曲げの硬さを試すとき用: GP02_BEND=0.05 ./run.sh
    string? bend = Environment.GetEnvironmentVariable("GP02_BEND");
    if (bend != null) solver.BendCompliance = float.Parse(bend, CultureInfo.InvariantCulture);
    for (int p = 0; p < cloth.Model.ParticleCount; p++)
    {
        if (cloth.Model.RestPositions[p].Y > Size - cloth.Spacing) solver.SetPinned(p, true);
    }
    return solver;
}

static int NearestVertex(Vector3[] positions, Vector3 point)
{
    int best = 0;
    for (int i = 1; i < positions.Length; i++)
    {
        if (Vector3.DistanceSquared(positions[i], point) < Vector3.DistanceSquared(positions[best], point)) best = i;
    }
    return best;
}

static float Ease(float t) => t * t * (3f - 2f * t);

// メッシュの頂点の位置と、三角形ごとの伸び（3辺のうち最大）を書き出す
static void AppendFrame(StringBuilder json, MeshTopology topology, Vector3[] positions)
{
    json.Append("{\"positions\":[");
    for (int i = 0; i < positions.Length; i++)
    {
        if (i > 0) json.Append(',');
        json.Append(Format(positions[i].X)).Append(',').Append(Format(positions[i].Y)).Append(',').Append(Format(positions[i].Z));
    }
    json.Append("],\"strain\":[");
    var rest = topology.Positions;
    var tris = topology.Triangles;
    for (int t = 0; t < tris.Length; t += 3)
    {
        float max = 0f;
        for (int e = 0; e < 3; e++)
        {
            int a = tris[t + e], b = tris[t + (e + 1) % 3];
            float strain = Vector3.Distance(positions[a], positions[b]) / Vector3.Distance(rest[a], rest[b]) - 1f;
            max = MathF.Max(max, strain);
        }
        if (t > 0) json.Append(',');
        json.Append(Format(max));
    }
    json.Append("]}");
}

static string Format(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
