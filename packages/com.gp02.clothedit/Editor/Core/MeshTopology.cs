using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// メッシュのつながりの情報。同じ位置の頂点をまとめた上で、
    /// 三角形どうしの隣接と、つながった部品（島）を求める。
    /// </summary>
    public sealed class MeshTopology
    {
        /// <summary>頂点を同一とみなす距離（メートル）の既定値。</summary>
        public const float DefaultWeldTolerance = 1e-6f;

        /// <summary>まとめた後の頂点の位置。</summary>
        public Vector3[] Positions { get; }

        /// <summary>元メッシュの頂点ごとの、まとめた後の頂点番号。</summary>
        public int[] VertexToWelded { get; }

        /// <summary>まとめた後の頂点番号で表した三角形（3つずつ）。潰れた三角形は除いてある。</summary>
        public int[] Triangles { get; }

        public int TriangleCount => Triangles.Length / 3;

        /// <summary>
        /// 元メッシュの三角形（Build に渡した triangles の3つ組の番号）ごとの、<see cref="Triangles"/> での番号。
        /// 潰れて除かれた三角形は -1。Unity の Mesh.triangles は全サブメッシュを順に並べたものなので、その番号がそのまま使える。
        /// </summary>
        public int[] SourceToTriangle { get; }

        /// <summary><see cref="Triangles"/> の三角形ごとの、元メッシュでの番号（<see cref="SourceToTriangle"/> の逆）。</summary>
        public int[] TriangleToSource { get; }

        /// <summary>三角形ごとの島の番号。辺を共有する三角形は同じ島になる。</summary>
        public int[] TriangleIsland { get; }

        public int IslandCount { get; }

        /// <summary>まとめた後の頂点ごとに、その頂点を含む三角形を1つ。どの三角形にも含まれなければ -1。</summary>
        public int[] VertexTriangle { get; }

        // 三角形の隣接（辺を共有する三角形）。CSR形式
        internal int[] AdjacencyOffsets { get; }
        internal int[] Adjacency { get; }

        // 三角形を囲む球（中心と半径）。表面に沿った探索の枝刈りに使う
        internal Vector3[] TriangleCenters { get; }
        internal float[] TriangleRadii { get; }

        MeshTopology(Vector3[] positions, int[] vertexToWelded, int[] triangles, int[] sourceToTriangle, int[] triangleToSource,
            int[] adjacencyOffsets, int[] adjacency)
        {
            SourceToTriangle = sourceToTriangle;
            TriangleToSource = triangleToSource;
            Positions = positions;
            VertexToWelded = vertexToWelded;
            Triangles = triangles;
            AdjacencyOffsets = adjacencyOffsets;
            Adjacency = adjacency;

            int triangleCount = triangles.Length / 3;
            TriangleCenters = new Vector3[triangleCount];
            TriangleRadii = new float[triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                Vector3 center = (a + b + c) / 3f;
                TriangleCenters[t] = center;
                TriangleRadii[t] = MathF.Sqrt(MathF.Max(Vector3.DistanceSquared(center, a),
                    MathF.Max(Vector3.DistanceSquared(center, b), Vector3.DistanceSquared(center, c))));
            }

            VertexTriangle = new int[positions.Length];
            for (int i = 0; i < VertexTriangle.Length; i++) VertexTriangle[i] = -1;
            for (int k = 0; k < triangles.Length; k++)
            {
                if (VertexTriangle[triangles[k]] < 0) VertexTriangle[triangles[k]] = k / 3;
            }

            TriangleIsland = FindIslands(triangleCount, adjacencyOffsets, adjacency, out int islandCount);
            IslandCount = islandCount;
        }

        public static MeshTopology Build(Vector3[] vertices, int[] triangles, float weldTolerance = DefaultWeldTolerance)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (triangles.Length % 3 != 0) throw new ArgumentException("三角形の頂点数が3の倍数ではありません。", nameof(triangles));

            int[] vertexToWelded = VertexWelder.Weld(vertices, weldTolerance, out var positions);

            var welded = new List<int>(triangles.Length);
            var sourceToTriangle = new int[triangles.Length / 3];
            var triangleToSourceList = new List<int>(triangles.Length / 3);
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = vertexToWelded[triangles[t]];
                int b = vertexToWelded[triangles[t + 1]];
                int c = vertexToWelded[triangles[t + 2]];
                if (a == b || b == c || c == a)
                {
                    sourceToTriangle[t / 3] = -1;
                    continue;
                }
                sourceToTriangle[t / 3] = welded.Count / 3;
                triangleToSourceList.Add(t / 3);
                welded.Add(a);
                welded.Add(b);
                welded.Add(c);
            }
            int[] weldedTriangles = welded.ToArray();

            // 辺 → その辺を持つ三角形。3枚以上が共有する辺（非多様体）も、すべて隣接として扱う
            int triangleCount = weldedTriangles.Length / 3;
            var edgeTriangles = new Dictionary<long, List<int>>(PairKeys.Comparer);
            for (int t = 0; t < triangleCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    long key = PairKeys.Make(weldedTriangles[3 * t + e], weldedTriangles[3 * t + (e + 1) % 3]);
                    if (!edgeTriangles.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        edgeTriangles.Add(key, list);
                    }
                    list.Add(t);
                }
            }

            var neighbors = new List<int>[triangleCount];
            for (int t = 0; t < triangleCount; t++) neighbors[t] = new List<int>(3);
            foreach (var list in edgeTriangles.Values)
            {
                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    neighbors[list[i]].Add(list[j]);
                    neighbors[list[j]].Add(list[i]);
                }
            }

            var offsets = new int[triangleCount + 1];
            for (int t = 0; t < triangleCount; t++) offsets[t + 1] = offsets[t] + neighbors[t].Count;
            var adjacency = new int[offsets[triangleCount]];
            for (int t = 0; t < triangleCount; t++) neighbors[t].CopyTo(adjacency, offsets[t]);

            return new MeshTopology(positions, vertexToWelded, weldedTriangles, sourceToTriangle, triangleToSourceList.ToArray(), offsets, adjacency);
        }

        static int[] FindIslands(int triangleCount, int[] offsets, int[] adjacency, out int islandCount)
        {
            var island = new int[triangleCount];
            for (int t = 0; t < triangleCount; t++) island[t] = -1;
            var stack = new Stack<int>();
            islandCount = 0;
            for (int start = 0; start < triangleCount; start++)
            {
                if (island[start] >= 0) continue;
                island[start] = islandCount;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int t = stack.Pop();
                    for (int k = offsets[t]; k < offsets[t + 1]; k++)
                    {
                        int n = adjacency[k];
                        if (island[n] >= 0) continue;
                        island[n] = islandCount;
                        stack.Push(n);
                    }
                }
                islandCount++;
            }
            return island;
        }
    }
}
