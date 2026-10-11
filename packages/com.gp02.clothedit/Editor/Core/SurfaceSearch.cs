using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// ある点の近くで、メッシュの表面に沿ってつながっている三角形を調べる。
    /// 空間的に近くても、表面でつながっていない部分（重ね着の別の層、折り返しの裏側など）を区別するために使う。
    /// </summary>
    internal sealed class SurfaceSearch
    {
        readonly MeshTopology _topology;
        readonly int[] _stamp;
        int[] _stack = new int[64];
        int _generation;

        public SurfaceSearch(MeshTopology topology)
        {
            _topology = topology;
            _stamp = new int[topology.TriangleCount];
        }

        /// <summary>
        /// 開始の三角形から、辺でつながった三角形をたどり、点から radius 以内にかかる三角形に印を付ける。
        /// 結果は次に Run を呼ぶまで <see cref="Contains"/> で調べられる。
        /// </summary>
        public void Run(int startTriangle, Vector3 point, float radius)
        {
            _generation++;
            if (_generation == int.MaxValue)
            {
                Array.Clear(_stamp, 0, _stamp.Length);
                _generation = 1;
            }

            Vector3[] centers = _topology.TriangleCenters;
            float[] radii = _topology.TriangleRadii;
            int[] offsets = _topology.AdjacencyOffsets;
            int[] adjacency = _topology.Adjacency;

            // 粒子化では数千万回たどるので、Unity の Mono で遅い Stack<int> と Vector3 の演算子は使わない
            int[] stamp = _stamp;
            int generation = _generation;
            int[] stack = _stack;
            int top = 0;
            stamp[startTriangle] = generation;
            stack[top++] = startTriangle;
            while (top > 0)
            {
                int t = stack[--top];
                int end = offsets[t + 1];
                for (int k = offsets[t]; k < end; k++)
                {
                    int n = adjacency[k];
                    if (stamp[n] == generation) continue;
                    // 三角形を囲む球が、探索範囲にかからなければたどらない
                    float reach = radius + radii[n];
                    float dx = centers[n].X - point.X, dy = centers[n].Y - point.Y, dz = centers[n].Z - point.Z;
                    if (dx * dx + dy * dy + dz * dz > reach * reach) continue;
                    stamp[n] = generation;
                    if (top == stack.Length)
                    {
                        Array.Resize(ref _stack, 2 * stack.Length);
                        stack = _stack;
                    }
                    stack[top++] = n;
                }
            }
        }

        public bool Contains(int triangle) => _stamp[triangle] == _generation;
    }
}
