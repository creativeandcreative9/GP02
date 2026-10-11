using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>実際の衣装にありがちな、きれいでないメッシュ（形はすべてプログラムで作る）。</summary>
public class DirtyMeshTests
{
    // 粒子化 → 布の中央付近をつまんで引っ張る → シェイプキーの差分。すべて有限の値になることを確かめ、差分を返す
    static (Vector3[] Vertices, Vector3[] Normals, ParticleCloth Cloth) PullAndBuild(Vector3[] vertices, int[] triangles, Vector3 grabPoint)
    {
        var cloth = ParticleCloth.Build(vertices, triangles);
        var solver = cloth.CreateSolver();
        solver.InfluenceRadius = 0.2f;

        int particle = solver.FindNearestParticle(grabPoint);
        Assert.True(particle >= 0);
        solver.Grab(particle, solver.Positions[particle] + new Vector3(0f, 0f, 0.05f), 0.05f);
        for (int i = 0; i < 5; i++) solver.Solve(20);
        solver.Release();

        foreach (Vector3 p in solver.Positions.ToArray()) AssertFinite(p);

        var deltaVertices = new Vector3[vertices.Length];
        var deltaNormals = new Vector3[vertices.Length];
        BlendShapeBuilder.Build(cloth, solver.Positions, deltaVertices, deltaNormals);
        foreach (Vector3 d in deltaVertices) AssertFinite(d);
        foreach (Vector3 d in deltaNormals) AssertFinite(d);
        return (deltaVertices, deltaNormals, cloth);
    }

    static void AssertFinite(Vector3 v)
    {
        Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), $"有限でない値 {v}");
    }

    [Fact]
    public void BackFaceOverlay_PullsBothSidesTogether()
    {
        ProceduralMeshes.Grid(20, 20, 0.4f, 0.4f, out var front, out var frontTriangles);
        int n = front.Length;
        var vertices = new Vector3[n * 2];
        var triangles = new int[frontTriangles.Length * 2];
        for (int i = 0; i < n; i++) vertices[i] = vertices[n + i] = front[i];
        for (int t = 0; t < frontTriangles.Length; t += 3)
        {
            for (int k = 0; k < 3; k++) triangles[t + k] = frontTriangles[t + k];
            triangles[frontTriangles.Length + t] = frontTriangles[t] + n;
            triangles[frontTriangles.Length + t + 1] = frontTriangles[t + 2] + n;
            triangles[frontTriangles.Length + t + 2] = frontTriangles[t + 1] + n;
        }

        var (delta, _, _) = PullAndBuild(vertices, triangles, new Vector3(0.2f, 0.2f, 0f));

        float moved = 0f;
        for (int i = 0; i < n; i++)
        {
            // 表と裏は同じ位置なので、同じだけ動く
            Assert.True(Vector3.Distance(delta[i], delta[n + i]) < 1e-6f);
            moved = MathF.Max(moved, delta[i].Length());
        }
        Assert.True(moved > 0.02f);
    }

    [Fact]
    public void ZeroAreaTriangles_DoNotBreakAnything()
    {
        ProceduralMeshes.Grid(20, 20, 0.4f, 0.4f, out var gridVertices, out var gridTriangles);
        int n = gridVertices.Length;
        // 末尾に、一直線上の三角形（辺と同じ線上）、3頂点が同じ位置の三角形、2頂点だけ同じ位置の三角形を足す
        var vertices = new List<Vector3>(gridVertices)
        {
            new Vector3(0.1f, 0.2f, 0f), new Vector3(0.15f, 0.2f, 0f), new Vector3(0.2f, 0.2f, 0f),   // n.. 一直線
            new Vector3(0.3f, 0.3f, 0f), new Vector3(0.3f, 0.3f, 0f), new Vector3(0.3f, 0.3f, 0f),   // 同じ位置
            new Vector3(0.12f, 0.12f, 0f), new Vector3(0.12f, 0.12f, 0f), new Vector3(0.2f, 0.12f, 0f),
        };
        var triangles = new List<int>(gridTriangles) { n, n + 1, n + 2, n + 3, n + 4, n + 5, n + 6, n + 7, n + 8 };

        var (delta, _, cloth) = PullAndBuild(vertices.ToArray(), triangles.ToArray(), new Vector3(0.2f, 0.2f, 0f));

        // 潰れた三角形は計算から外れるが、元の頂点の差分は出る
        Assert.True(cloth.Topology.TriangleCount < triangles.Count / 3);
        Assert.Equal(vertices.Count, delta.Length);
        // 元の布と同じ位置にある頂点は、同じだけ動く
        Assert.True(Vector3.Distance(delta[n + 3], delta[n + 4]) < 1e-6f);
    }

    [Fact]
    public void UnusedVertices_HaveZeroDelta()
    {
        ProceduralMeshes.Grid(20, 20, 0.4f, 0.4f, out var gridVertices, out var gridTriangles);
        int n = gridVertices.Length;
        var vertices = new List<Vector3>(gridVertices)
        {
            new Vector3(0.2f, 0.2f, 0.5f),    // 布から離れた、使われていない頂点
            new Vector3(5f, 5f, 5f),
        };

        var (delta, normals, _) = PullAndBuild(vertices.ToArray(), gridTriangles, new Vector3(0.2f, 0.2f, 0f));

        Assert.Equal(Vector3.Zero, delta[n]);
        Assert.Equal(Vector3.Zero, delta[n + 1]);
        Assert.Equal(Vector3.Zero, normals[n]);
        Assert.Equal(Vector3.Zero, normals[n + 1]);
    }

    [Fact]
    public void NoTriangles_DoesNotCrash()
    {
        // 三角形が1枚もないメッシュ。粒子は0個になるが、例外で落ちない
        var vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f) };
        var cloth = ParticleCloth.Build(vertices, new int[0]);

        Assert.Equal(0, cloth.Model.ParticleCount);
        var deltaVertices = new Vector3[2];
        var deltaNormals = new Vector3[2];
        BlendShapeBuilder.Build(cloth, cloth.CreateSolver().Positions, deltaVertices, deltaNormals);
        Assert.Equal(Vector3.Zero, deltaVertices[0]);
    }

    // 大きな布と、その横の小さな部品（5mm 四方の四角形を複数）
    static void ClothWithButtons(out Vector3[] vertices, out int[] triangles, out int clothVertexCount, out int buttonCount)
    {
        ProceduralMeshes.Grid(20, 20, 0.4f, 0.4f, out var gridVertices, out var gridTriangles);
        clothVertexCount = gridVertices.Length;
        var v = new List<Vector3>(gridVertices);
        var t = new List<int>(gridTriangles);
        buttonCount = 4;
        for (int b = 0; b < buttonCount; b++)
        {
            // 布の面から 2mm 浮かせて置く（ボタンのつもり）
            float x = 0.05f + 0.08f * b, y = 0.1f;
            int i = v.Count;
            v.Add(new Vector3(x, y, 0.002f));
            v.Add(new Vector3(x + 0.005f, y, 0.002f));
            v.Add(new Vector3(x, y + 0.005f, 0.002f));
            v.Add(new Vector3(x + 0.005f, y + 0.005f, 0.002f));
            t.AddRange(new[] { i, i + 1, i + 2, i + 1, i + 3, i + 2 });
        }
        vertices = v.ToArray();
        triangles = t.ToArray();
    }

    [Fact]
    public void SmallIslands_GetParticlesAndStayStillWhenClothMoves()
    {
        ClothWithButtons(out var vertices, out var triangles, out int clothVertexCount, out int buttonCount);

        var cloth = ParticleCloth.Build(vertices, triangles);
        // どの島にも粒子が乗っている
        var islandHasParticle = new bool[cloth.Topology.IslandCount];
        foreach (int island in cloth.ParticleIsland) islandHasParticle[island] = true;
        Assert.Equal(1 + buttonCount, cloth.Topology.IslandCount);
        Assert.All(islandHasParticle, has => Assert.True(has));

        var (delta, _, _) = PullAndBuild(vertices, triangles, new Vector3(0.2f, 0.2f, 0f));

        float clothMoved = 0f;
        for (int i = 0; i < clothVertexCount; i++) clothMoved = MathF.Max(clothMoved, delta[i].Length());
        Assert.True(clothMoved > 0.02f);
        // 小さな部品は、別の島なので布を引っ張っても動かない
        for (int i = clothVertexCount; i < vertices.Length; i++) Assert.Equal(Vector3.Zero, delta[i]);
    }

    [Fact]
    public void SmallIsland_CanBeGrabbedAndMovesTogether()
    {
        ClothWithButtons(out var vertices, out var triangles, out int clothVertexCount, out _);

        // 最初のボタンをつまんで動かす。ボタンの頂点4つが一緒に動く
        var (delta, _, _) = PullAndBuild(vertices, triangles, new Vector3(0.0525f, 0.1025f, 0.002f));

        Vector3 first = delta[clothVertexCount];
        Assert.True(first.Length() > 0.01f);
        for (int k = 1; k < 4; k++) Assert.True(Vector3.Distance(delta[clothVertexCount + k], first) < 1e-4f, $"頂点 {k}");
        for (int i = 0; i < clothVertexCount; i++) Assert.Equal(Vector3.Zero, delta[i]);
    }
}
