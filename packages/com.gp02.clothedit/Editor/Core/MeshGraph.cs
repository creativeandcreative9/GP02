using System;
using System.Collections.Generic;

namespace GP02.Core
{
    /// <summary>
    /// 粒子をつなぐ辺のグラフ。メッシュに沿った最短距離を求めるのに使う。
    /// </summary>
    internal sealed class MeshGraph
    {
        readonly int[] _offsets;
        readonly int[] _neighbors;
        readonly float[] _lengths;

        /// <summary>ノード i の隣は <see cref="Neighbors"/>[Offsets[i] .. Offsets[i + 1])。</summary>
        public int[] Offsets => _offsets;
        public int[] Neighbors => _neighbors;

        public MeshGraph(int nodeCount, int[] pairs, float[] lengths)
        {
            var degree = new int[nodeCount + 1];
            for (int k = 0; k < pairs.Length; k++) degree[pairs[k] + 1]++;
            for (int i = 0; i < nodeCount; i++) degree[i + 1] += degree[i];

            _offsets = degree;
            _neighbors = new int[pairs.Length];
            _lengths = new float[pairs.Length];
            var fill = (int[])_offsets.Clone();
            for (int k = 0; k < lengths.Length; k++)
            {
                int a = pairs[2 * k];
                int b = pairs[2 * k + 1];
                _neighbors[fill[a]] = b;
                _lengths[fill[a]++] = lengths[k];
                _neighbors[fill[b]] = a;
                _lengths[fill[b]++] = lengths[k];
            }
        }

        /// <summary>
        /// 複数の起点から、各ノードまでの最短距離を求める（ダイクストラ法）。
        /// 届かないノードは距離が無限大、起点は -1 になる。
        /// </summary>
        /// <param name="nearestSource">各ノードに一番近い起点（不要なら null）</param>
        public void ShortestDistances(int[] sources, float[] distances, int[] nearestSource)
        {
            ShortestDistances(sources, sources.Length, distances, nearestSource, float.PositiveInfinity);
        }

        /// <summary>
        /// sources の先頭 sourceCount 個を起点に、maxDistance 以内のノードまでの最短距離を求める。
        /// maxDistance より遠いノードは無限大のまま（maxDistance 以内のノードの距離は正確）。
        /// </summary>
        public void ShortestDistances(int[] sources, int sourceCount, float[] distances, int[] nearestSource, float maxDistance)
        {
            for (int i = 0; i < distances.Length; i++) distances[i] = float.PositiveInfinity;
            if (nearestSource != null)
            {
                for (int i = 0; i < nearestSource.Length; i++) nearestSource[i] = -1;
            }

            var heap = new MinHeap();
            for (int k = 0; k < sourceCount; k++)
            {
                int s = sources[k];
                distances[s] = 0f;
                if (nearestSource != null) nearestSource[s] = s;
                heap.Push(0f, s);
            }

            while (heap.TryPop(out float distance, out int node))
            {
                if (distance > distances[node]) continue;
                for (int e = _offsets[node]; e < _offsets[node + 1]; e++)
                {
                    int next = _neighbors[e];
                    float candidate = distance + _lengths[e];
                    if (candidate >= distances[next] || candidate > maxDistance) continue;
                    distances[next] = candidate;
                    if (nearestSource != null) nearestSource[next] = nearestSource[node];
                    heap.Push(candidate, next);
                }
            }
        }

        /// <summary>つながった部品（島）ごとの番号を components に入れ、部品の数を返す。</summary>
        public int Components(int[] components)
        {
            for (int i = 0; i < components.Length; i++) components[i] = -1;
            var stack = new Stack<int>();
            int count = 0;
            for (int start = 0; start < components.Length; start++)
            {
                if (components[start] >= 0) continue;
                components[start] = count;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int node = stack.Pop();
                    for (int e = _offsets[node]; e < _offsets[node + 1]; e++)
                    {
                        int next = _neighbors[e];
                        if (components[next] >= 0) continue;
                        components[next] = count;
                        stack.Push(next);
                    }
                }
                count++;
            }
            return count;
        }

        sealed class MinHeap
        {
            readonly List<(float Key, int Value)> _items = new List<(float, int)>();

            public void Push(float key, int value)
            {
                _items.Add((key, value));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].Key <= _items[i].Key) break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public bool TryPop(out float key, out int value)
            {
                if (_items.Count == 0)
                {
                    key = 0f;
                    value = -1;
                    return false;
                }

                (key, value) = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);

                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    if (left >= _items.Count) break;
                    int smallest = left + 1 < _items.Count && _items[left + 1].Key < _items[left].Key ? left + 1 : left;
                    if (_items[i].Key <= _items[smallest].Key) break;
                    (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                    i = smallest;
                }
                return true;
            }
        }
    }
}
