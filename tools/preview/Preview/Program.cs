using System.Globalization;
using System.Numerics;
using System.Text;
using GP02.Core;

// 吊るした布の下端中央をつまんで引っ張る様子を、2つの方式で計算してJSONに書き出す。
//   falloff : 範囲指定で動かす方式（制約を解かない）
//   particle: 粒子方式（布の長さと曲げの制約を解く）
// 描画は render.js が行う。

string output = args.Length > 0 ? args[0] : Path.Combine("out", "frames.json");

const int Columns = 30, Rows = 30;
const float Size = 1f;
const int DragFrames = 60, HoldFrames = 25, Iterations = 100;
var offset = new Vector3(0.15f, 0.3f, 0.35f);

ProceduralMeshes.Grid(Columns, Rows, Size, Size, out var vertices, out var triangles);
var model = ClothModel.Build(vertices, triangles);
var particle = CreateSolver(model);
var falloff = CreateSolver(model);
int grabbed = Columns / 2;
Vector3 start = model.RestPositions[grabbed];

var json = new StringBuilder();
json.Append("{\"triangles\":[").AppendJoin(',', model.Triangles).Append("],");
json.Append("\"grabbed\":").Append(grabbed).Append(",\"frames\":[");

for (int f = 0; f <= DragFrames + HoldFrames; f++)
{
    float t = MathF.Min(1f, (float)f / DragFrames);
    Vector3 target = start + offset * Ease(t);
    particle.Grab(grabbed, target);
    particle.Solve(Iterations);
    falloff.Grab(grabbed, target);
    falloff.Solve(0);

    if (f > 0) json.Append(',');
    json.Append("{\"particle\":");
    AppendFrame(json, model, particle.Positions);
    json.Append(",\"falloff\":");
    AppendFrame(json, model, falloff.Positions);
    json.Append('}');
}
json.Append("]}");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, json.ToString());
Console.WriteLine($"書き出し: {output}");

static ClothSolver CreateSolver(ClothModel model)
{
    var solver = new ClothSolver(model);
    // 曲げの硬さを試すとき用: GP02_BEND=0.05 ./run.sh
    string? bend = Environment.GetEnvironmentVariable("GP02_BEND");
    if (bend != null) solver.BendCompliance = float.Parse(bend, CultureInfo.InvariantCulture);
    for (int c = 0; c <= Columns; c++) solver.SetPinned(Rows * (Columns + 1) + c, true);
    return solver;
}

static float Ease(float t) => t * t * (3f - 2f * t);

// 位置と、三角形ごとの伸び（3辺のうち最大）を書き出す
static void AppendFrame(StringBuilder json, ClothModel model, ReadOnlySpan<Vector3> positions)
{
    json.Append("{\"positions\":[");
    for (int i = 0; i < positions.Length; i++)
    {
        if (i > 0) json.Append(',');
        json.Append(Format(positions[i].X)).Append(',').Append(Format(positions[i].Y)).Append(',').Append(Format(positions[i].Z));
    }
    json.Append("],\"strain\":[");
    var rest = model.RestPositions;
    var tris = model.Triangles;
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
