using System;
using System.Collections.Generic;
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
        readonly Stack<int> _stack = new Stack<int>();
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

            _stamp[startTriangle] = _generation;
            _stack.Push(startTriangle);
            while (_stack.Count > 0)
            {
                int t = _stack.Pop();
                for (int k = offsets[t]; k < offsets[t + 1]; k++)
                {
                    int n = adjacency[k];
                    if (_stamp[n] == _generation) continue;
                    // 三角形を囲む球が、探索範囲にかからなければたどらない
                    if (Vector3.Distance(point, centers[n]) - radii[n] > radius) continue;
                    _stamp[n] = _generation;
                    _stack.Push(n);
                }
            }
        }

        public bool Contains(int triangle) => _stamp[triangle] == _generation;
    }
}
