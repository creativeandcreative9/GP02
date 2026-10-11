using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 形の更新（粒子の動きを頂点へ伝える処理と、レイキャスト用の境界箱の更新）を肩代わりする部品。
    /// <see cref="IClothSolverKernel"/> と同じ実装クラスが両方を実装すると、<see cref="ClothSolver.Kernel"/> を差し替えるだけで
    /// 反復と形の更新がそろって切り替わる（Kernel が IShapeKernel でなければ、C# の既定の実装 <see cref="ShapeKernels"/> が計算する）。
    /// 実装は、<see cref="ShapeKernels"/> の C# と同じ式で計算し、結果がほぼ一致するようにすること。
    /// </summary>
    public interface IShapeKernel
    {
        /// <summary>粒子の位置から、まとめた頂点の位置を求める（<see cref="ParticleCloth.DeformWelded"/>）。</summary>
        void DeformWelded(in WeldedDeformData data);

        /// <summary>まとめた頂点の動きを、元の頂点へ展開する（<see cref="ShapeKernels"/>）。</summary>
        void ExpandVertices(in VertexExpandData data);

        /// <summary>頂点が動いたあとの、三角形の木の境界箱の更新（<see cref="TriangleBvh.Refit(Vector3[], IShapeKernel)"/>）。</summary>
        void RefitBvh(in BvhRefitData data);
    }

    /// <summary>
    /// <see cref="IShapeKernel.DeformWelded"/> に渡す情報。まとめた頂点 v は、
    /// <c>Welded[v] = BasePositions[v] + Σ BindWeights[k] * (ParticlePositions[p] - ParticleRestPositions[p])</c>
    /// （k は <c>BindOffsets[v]</c> 以上 <c>BindOffsets[v + 1]</c> 未満、p = <c>BindParticles[k]</c>）。
    /// <see cref="Welded"/> 以外は読むだけにすること。
    /// </summary>
    public readonly ref struct WeldedDeformData
    {
        public readonly ReadOnlySpan<Vector3> ParticlePositions;
        public readonly Vector3[] ParticleRestPositions;
        public readonly Vector3[] BasePositions;
        public readonly int[] BindOffsets;
        public readonly int[] BindParticles;
        public readonly float[] BindWeights;

        /// <summary>出力。まとめた頂点の位置（<see cref="BasePositions"/> と同じ長さ）。</summary>
        public readonly Vector3[] Welded;

        internal WeldedDeformData(ReadOnlySpan<Vector3> particlePositions, Vector3[] particleRestPositions, Vector3[] basePositions,
            int[] bindOffsets, int[] bindParticles, float[] bindWeights, Vector3[] welded)
        {
            ParticlePositions = particlePositions;
            ParticleRestPositions = particleRestPositions;
            BasePositions = basePositions;
            BindOffsets = bindOffsets;
            BindParticles = bindParticles;
            BindWeights = bindWeights;
            Welded = welded;
        }
    }

    /// <summary>
    /// <see cref="IShapeKernel.ExpandVertices"/> に渡す情報。元の頂点 v は、
    /// <c>Vertices[v] = BaseVertices[v] + (Welded[w] - WeldedRest[w])</c>（w = <c>VertexToWelded[v]</c>）。
    /// <see cref="Vertices"/> 以外は読むだけにすること。
    /// </summary>
    public readonly ref struct VertexExpandData
    {
        /// <summary>出力。元の頂点ごとの位置。</summary>
        public readonly Span<Vector3> Vertices;
        public readonly ReadOnlySpan<Vector3> BaseVertices;
        public readonly ReadOnlySpan<Vector3> Welded;
        public readonly ReadOnlySpan<Vector3> WeldedRest;
        public readonly int[] VertexToWelded;

        internal VertexExpandData(Span<Vector3> vertices, ReadOnlySpan<Vector3> baseVertices, ReadOnlySpan<Vector3> welded,
            ReadOnlySpan<Vector3> weldedRest, int[] vertexToWelded)
        {
            Vertices = vertices;
            BaseVertices = baseVertices;
            Welded = welded;
            WeldedRest = weldedRest;
            VertexToWelded = vertexToWelded;
        }
    }

    /// <summary>
    /// <see cref="IShapeKernel.RefitBvh"/> に渡す、三角形の木（<see cref="TriangleBvh"/>）の配列への参照。コピーはしない。
    /// ノードは深さ優先の順で、子の番号は必ず親より大きい。内部ノードの左の子は node + 1、右の子は <see cref="Right"/>[node]。
    /// 葉（<c>Right[node] &lt; 0</c>）は、<c>Start[node] .. Start[node] + Count[node]</c> 番目（葉に並べた順）の三角形を持つ。
    /// 各ノードの箱は、子の箱の和（葉は三角形の頂点の最小・最大を <see cref="Padding"/> だけ広げたもの）。
    /// ノード番号の大きい順に計算すれば、子が先に終わる。<see cref="BoxMin"/> と <see cref="BoxMax"/> だけ書き換えてよい。
    /// </summary>
    public readonly struct BvhRefitData
    {
        /// <summary>頂点の位置（最新）。</summary>
        public readonly Vector3[] Positions;

        /// <summary>
        /// 葉に並べた順の、三角形の頂点番号の3つ組（i 番目の三角形は <c>LeafTriangleVertices[3 * i .. 3 * i + 3)</c>）。
        /// 葉の中の三角形が連続して並ぶので、頂点番号を順に読める。
        /// </summary>
        public readonly int[] LeafTriangleVertices;
        public readonly int[] Right;
        public readonly int[] Start;
        public readonly int[] Count;
        public readonly int NodeCount;
        public readonly Vector3[] BoxMin;
        public readonly Vector3[] BoxMax;

        /// <summary>葉の箱を広げる量（辺や頂点すれすれに当たるレイを、丸め誤差で取りこぼさないため）。</summary>
        public readonly float Padding;

        internal BvhRefitData(Vector3[] positions, int[] leafTriangleVertices, int[] right, int[] start, int[] count, int nodeCount,
            Vector3[] boxMin, Vector3[] boxMax, float padding)
        {
            Positions = positions;
            LeafTriangleVertices = leafTriangleVertices;
            Right = right;
            Start = start;
            Count = count;
            NodeCount = nodeCount;
            BoxMin = boxMin;
            BoxMax = boxMax;
            Padding = padding;
        }
    }

    /// <summary>
    /// 形の更新の C# の既定の実装。<see cref="IShapeKernel"/> に渡す kernel が null のとき、これが計算する。
    /// Unity の Mono では System.Numerics.Vector3 の演算子が1つずつ関数の呼び出しになるので、成分ごとに計算している。
    /// </summary>
    public static class ShapeKernels
    {
        public static void DeformWelded(in WeldedDeformData d)
        {
            ReadOnlySpan<Vector3> particlePositions = d.ParticlePositions;
            Vector3[] rest = d.ParticleRestPositions;
            Vector3[] basePositions = d.BasePositions;
            Vector3[] welded = d.Welded;
            int[] offsets = d.BindOffsets;
            int[] particles = d.BindParticles;
            float[] weights = d.BindWeights;
            for (int v = 0; v < welded.Length; v++)
            {
                float dx = 0f, dy = 0f, dz = 0f;
                int end = offsets[v + 1];
                for (int k = offsets[v]; k < end; k++)
                {
                    int p = particles[k];
                    float w = weights[k];
                    dx += (particlePositions[p].X - rest[p].X) * w;
                    dy += (particlePositions[p].Y - rest[p].Y) * w;
                    dz += (particlePositions[p].Z - rest[p].Z) * w;
                }
                welded[v] = new Vector3(basePositions[v].X + dx, basePositions[v].Y + dy, basePositions[v].Z + dz);
            }
        }

        /// <summary>
        /// まとめた頂点の動きを、元の頂点へ展開する。kernel が null なら C# で計算する。
        /// 元の頂点が Unity の Vector3 の配列なら、<c>MemoryMarshal.Cast</c> で System.Numerics.Vector3 のスパンにして渡す（どちらも float 3つ）。
        /// </summary>
        public static void ExpandVertices(IShapeKernel kernel, Span<Vector3> vertices, ReadOnlySpan<Vector3> baseVertices,
            ReadOnlySpan<Vector3> welded, ReadOnlySpan<Vector3> weldedRest, int[] vertexToWelded)
        {
            if (vertexToWelded == null) throw new ArgumentNullException(nameof(vertexToWelded));
            if (vertices.Length != baseVertices.Length || vertices.Length != vertexToWelded.Length)
            {
                throw new ArgumentException("頂点数が一致しません。", nameof(vertices));
            }
            if (welded.Length != weldedRest.Length) throw new ArgumentException("まとめた頂点数が一致しません。", nameof(welded));

            var data = new VertexExpandData(vertices, baseVertices, welded, weldedRest, vertexToWelded);
            if (kernel != null) kernel.ExpandVertices(data);
            else ExpandVertices(data);
        }

        public static void ExpandVertices(in VertexExpandData d)
        {
            Span<Vector3> vertices = d.Vertices;
            ReadOnlySpan<Vector3> baseVertices = d.BaseVertices;
            ReadOnlySpan<Vector3> welded = d.Welded;
            ReadOnlySpan<Vector3> weldedRest = d.WeldedRest;
            int[] map = d.VertexToWelded;
            for (int v = 0; v < vertices.Length; v++)
            {
                int w = map[v];
                vertices[v].X = baseVertices[v].X + (welded[w].X - weldedRest[w].X);
                vertices[v].Y = baseVertices[v].Y + (welded[w].Y - weldedRest[w].Y);
                vertices[v].Z = baseVertices[v].Z + (welded[w].Z - weldedRest[w].Z);
            }
        }

        public static void RefitBvh(in BvhRefitData d)
        {
            // 頂点数十万の更新を 1 コマの中で終えたいので、Vector3.Min などの呼び出しを使わず、値をそのまま比べる
            // （Unity の Mono では、小さな関数呼び出しの積み重ねが目立つため）
            Vector3[] positions = d.Positions;
            int[] leafVertices = d.LeafTriangleVertices;
            int[] rightChild = d.Right;
            int[] start = d.Start;
            int[] count = d.Count;
            Vector3[] boxMin = d.BoxMin;
            Vector3[] boxMax = d.BoxMax;
            float padding = d.Padding;
            // 子の番号は親より大きいので、後ろから処理すれば子が先に終わる
            for (int node = d.NodeCount - 1; node >= 0; node--)
            {
                float minX, minY, minZ, maxX, maxY, maxZ;
                if (rightChild[node] < 0)
                {
                    minX = minY = minZ = float.MaxValue;
                    maxX = maxY = maxZ = float.MinValue;
                    int end = 3 * (start[node] + count[node]);
                    for (int i = 3 * start[node]; i < end; i++)
                    {
                        Vector3 p = positions[leafVertices[i]];
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Z < minZ) minZ = p.Z;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                        if (p.Z > maxZ) maxZ = p.Z;
                    }
                    minX -= padding;
                    minY -= padding;
                    minZ -= padding;
                    maxX += padding;
                    maxY += padding;
                    maxZ += padding;
                }
                else
                {
                    Vector3 lMin = boxMin[node + 1], lMax = boxMax[node + 1];
                    Vector3 rMin = boxMin[rightChild[node]], rMax = boxMax[rightChild[node]];
                    minX = lMin.X < rMin.X ? lMin.X : rMin.X;
                    minY = lMin.Y < rMin.Y ? lMin.Y : rMin.Y;
                    minZ = lMin.Z < rMin.Z ? lMin.Z : rMin.Z;
                    maxX = lMax.X > rMax.X ? lMax.X : rMax.X;
                    maxY = lMax.Y > rMax.Y ? lMax.Y : rMax.Y;
                    maxZ = lMax.Z > rMax.Z ? lMax.Z : rMax.Z;
                }
                boxMin[node] = new Vector3(minX, minY, minZ);
                boxMax[node] = new Vector3(maxX, maxY, maxZ);
            }
        }
    }
}
