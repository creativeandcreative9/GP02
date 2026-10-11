using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 三角形の表面で、ある点に一番近い点を探すための境界箱の木。
    /// <see cref="ClosestPointSearch"/> と同じ考え方だが、毎フレームに近い頻度で呼ばれる（体との制約の探し直し）ので、
    /// Unity の Mono で関数呼び出しになる Vector3 の演算子を使わず、成分ごとに計算する。
    /// 木の形は最初の位置で決め、位置が変わったら <see cref="Refit"/> で箱だけ更新する（布の今の形に使う）。
    /// 頂点の位置は持たず、呼ぶたびに渡してもらう。検索中に共有の作業領域を持たないので、複数のスレッドから呼んでもよい。
    /// </summary>
    public sealed class ClosestTriangleTree
    {
        const int LeafSize = 4;
        const int StackSize = 96;

        readonly int[] _triangles;   // 頂点番号の3つ組（呼び出し側の三角形の番号で引く）
        readonly int[] _order;       // 葉に並べた、呼び出し側の三角形の番号
        readonly float[] _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
        readonly int[] _right;       // 内部ノード: 右の子（左の子は node + 1）。葉: -1
        readonly int[] _start;       // 葉: _order の始点
        readonly int[] _count;       // 葉: 三角形の数
        int _nodeCount;

        public int TriangleCount => _order.Length;

        /// <param name="positions">頂点の位置（木の形を決めるのに使う）</param>
        /// <param name="triangles">頂点番号の3つ組</param>
        /// <param name="subset">木に入れる三角形の番号。null なら全部</param>
        public ClosestTriangleTree(Vector3[] positions, int[] triangles, int[] subset = null)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            _triangles = triangles;
            int count = subset != null ? subset.Length : triangles.Length / 3;
            _order = new int[count];
            for (int i = 0; i < count; i++) _order[i] = subset != null ? subset[i] : i;

            int maxNodes = Math.Max(1, 2 * count);
            _minX = new float[maxNodes];
            _minY = new float[maxNodes];
            _minZ = new float[maxNodes];
            _maxX = new float[maxNodes];
            _maxY = new float[maxNodes];
            _maxZ = new float[maxNodes];
            _right = new int[maxNodes];
            _start = new int[maxNodes];
            _count = new int[maxNodes];

            if (count == 0)
            {
                _nodeCount = 1;
                _right[0] = -1;
                _minX[0] = _minY[0] = _minZ[0] = float.MaxValue;
                _maxX[0] = _maxY[0] = _maxZ[0] = float.MinValue;
                return;
            }

            var centers = new float[3][] { new float[count], new float[count], new float[count] };
            var local = new int[count];
            for (int i = 0; i < count; i++)
            {
                int t = _order[i];
                Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                centers[0][i] = (a.X + b.X + c.X) / 3f;
                centers[1][i] = (a.Y + b.Y + c.Y) / 3f;
                centers[2][i] = (a.Z + b.Z + c.Z) / 3f;
                local[i] = i;
            }
            Build(centers, local, 0, count);
            // 葉の並びを、呼び出し側の三角形の番号に直す
            var sorted = new int[count];
            for (int i = 0; i < count; i++) sorted[i] = _order[local[i]];
            Array.Copy(sorted, _order, count);
            Refit(positions);
        }

        // local[start..end) を1つのノードにする（深さ優先の順に番号を振るので、子の番号は必ず親より大きい）
        int Build(float[][] centers, int[] local, int start, int end)
        {
            int node = _nodeCount++;
            if (end - start <= LeafSize)
            {
                _right[node] = -1;
                _start[node] = start;
                _count[node] = end - start;
                return node;
            }

            int axis = 0;
            float bestExtent = -1f;
            for (int a = 0; a < 3; a++)
            {
                float[] key = centers[a];
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = start; i < end; i++)
                {
                    float v = key[local[i]];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                if (hi - lo > bestExtent)
                {
                    bestExtent = hi - lo;
                    axis = a;
                }
            }

            int mid = (start + end) / 2;
            Select(local, centers[axis], start, end - 1, mid);
            Build(centers, local, start, mid);
            _right[node] = Build(centers, local, mid, end);
            return node;
        }

        // order[lo..hi] のうち、key が k 番目に小さいものを k の位置に置く（TriangleBvh と同じ）
        static void Select(int[] order, float[] key, int lo, int hi, int k)
        {
            while (lo < hi)
            {
                float pivot = key[order[(lo + hi) >> 1]];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (key[order[i]] < pivot) i++;
                    while (key[order[j]] > pivot) j--;
                    if (i <= j)
                    {
                        (order[i], order[j]) = (order[j], order[i]);
                        i++;
                        j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return;
            }
        }

        /// <summary>頂点が動いたあとに、箱を更新する。確保はしない。</summary>
        public void Refit(Vector3[] positions)
        {
            int[] triangles = _triangles;
            for (int node = _nodeCount - 1; node >= 0; node--)
            {
                int right = _right[node];
                if (right < 0)
                {
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                    int end = _start[node] + _count[node];
                    for (int i = _start[node]; i < end; i++)
                    {
                        int t = _order[i];
                        for (int c = 0; c < 3; c++)
                        {
                            Vector3 p = positions[triangles[3 * t + c]];
                            if (p.X < minX) minX = p.X;
                            if (p.Y < minY) minY = p.Y;
                            if (p.Z < minZ) minZ = p.Z;
                            if (p.X > maxX) maxX = p.X;
                            if (p.Y > maxY) maxY = p.Y;
                            if (p.Z > maxZ) maxZ = p.Z;
                        }
                    }
                    _minX[node] = minX; _minY[node] = minY; _minZ[node] = minZ;
                    _maxX[node] = maxX; _maxY[node] = maxY; _maxZ[node] = maxZ;
                }
                else
                {
                    int left = node + 1;
                    _minX[node] = Math.Min(_minX[left], _minX[right]);
                    _minY[node] = Math.Min(_minY[left], _minY[right]);
                    _minZ[node] = Math.Min(_minZ[left], _minZ[right]);
                    _maxX[node] = Math.Max(_maxX[left], _maxX[right]);
                    _maxY[node] = Math.Max(_maxY[left], _maxY[right]);
                    _maxZ[node] = Math.Max(_maxZ[left], _maxZ[right]);
                }
            }
        }

        /// <summary>木全体の箱から点までの距離の2乗（中にあれば 0）。遠い木を調べずに済ませるため。</summary>
        public float RootDistanceSquared(float px, float py, float pz) => BoxDistanceSquared(0, px, py, pz);

        /// <summary>木全体の箱。</summary>
        public void GetBounds(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(_minX[0], _minY[0], _minZ[0]);
            max = new Vector3(_maxX[0], _maxY[0], _maxZ[0]);
        }

        float BoxDistanceSquared(int node, float px, float py, float pz)
        {
            float dx = Math.Max(Math.Max(_minX[node] - px, 0f), px - _maxX[node]);
            float dy = Math.Max(Math.Max(_minY[node] - py, 0f), py - _maxY[node]);
            float dz = Math.Max(Math.Max(_minZ[node] - pz, 0f), pz - _maxZ[node]);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// 点 (px, py, pz) から maxDistance 以内で、一番近い三角形の上の点を探す。見つからなければ false。
        /// 重心座標 (u, v, w) は、頂点・辺の上なら該当しない成分がちょうど 0 になる（<see cref="ClosestOnTriangle"/>）。
        /// 同じ距離なら、先に見つけた三角形を残す。
        /// </summary>
        public bool FindClosest(Vector3[] positions, float px, float py, float pz, float maxDistance,
            out int triangle, out float qx, out float qy, out float qz, out float u, out float v, out float w, out float distanceSquared)
        {
            triangle = -1;
            qx = qy = qz = 0f;
            u = v = w = 0f;
            distanceSquared = 0f;
            if (_order.Length == 0 || !(maxDistance >= 0f)) return false;

            float bestSq = maxDistance * maxDistance;
            bool found = false;
            int[] triangles = _triangles;
            Span<int> stack = stackalloc int[StackSize];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                if (BoxDistanceSquared(node, px, py, pz) > bestSq) continue;

                int right = _right[node];
                if (right < 0)
                {
                    int end = _start[node] + _count[node];
                    for (int i = _start[node]; i < end; i++)
                    {
                        int t = _order[i];
                        Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                        ClosestOnTriangle(px, py, pz, a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z,
                            out float cx, out float cy, out float cz, out float cu, out float cv, out float cw);
                        float dx = cx - px, dy = cy - py, dz = cz - pz;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d > bestSq || (found && d >= bestSq)) continue;
                        bestSq = d;
                        found = true;
                        triangle = t;
                        qx = cx; qy = cy; qz = cz;
                        u = cu; v = cv; w = cw;
                    }
                    continue;
                }

                // 近い方の子を後に積み、先に調べる
                if (top + 2 > StackSize) continue;
                int left = node + 1;
                if (BoxDistanceSquared(left, px, py, pz) <= BoxDistanceSquared(right, px, py, pz))
                {
                    stack[top++] = right;
                    stack[top++] = left;
                }
                else
                {
                    stack[top++] = left;
                    stack[top++] = right;
                }
            }
            if (found) distanceSquared = bestSq;
            return found;
        }

        /// <summary>
        /// 三角形 abc の上で p に一番近い点 q と、その重心座標 (u, v, w)（合計 1）。
        /// 頂点の領域なら (1,0,0) などの、辺の領域なら該当しない頂点の成分が 0 の値を返す（<see cref="ClosestPointSearch.ClosestOnTriangle"/> と同じ判定）。
        /// </summary>
        public static void ClosestOnTriangle(float px, float py, float pz, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz,
            out float qx, out float qy, out float qz, out float u, out float v, out float w)
        {
            float abx = bx - ax, aby = by - ay, abz = bz - az;
            float acx = cx - ax, acy = cy - ay, acz = cz - az;
            float apx = px - ax, apy = py - ay, apz = pz - az;
            float d1 = abx * apx + aby * apy + abz * apz;
            float d2 = acx * apx + acy * apy + acz * apz;
            if (d1 <= 0f && d2 <= 0f)
            {
                qx = ax; qy = ay; qz = az;
                u = 1f; v = 0f; w = 0f;
                return;
            }

            float bpx = px - bx, bpy = py - by, bpz = pz - bz;
            float d3 = abx * bpx + aby * bpy + abz * bpz;
            float d4 = acx * bpx + acy * bpy + acz * bpz;
            if (d3 >= 0f && d4 <= d3)
            {
                qx = bx; qy = by; qz = bz;
                u = 0f; v = 1f; w = 0f;
                return;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float t = d1 / (d1 - d3);
                qx = ax + abx * t; qy = ay + aby * t; qz = az + abz * t;
                u = 1f - t; v = t; w = 0f;
                return;
            }

            float cpx = px - cx, cpy = py - cy, cpz = pz - cz;
            float d5 = abx * cpx + aby * cpy + abz * cpz;
            float d6 = acx * cpx + acy * cpy + acz * cpz;
            if (d6 >= 0f && d5 <= d6)
            {
                qx = cx; qy = cy; qz = cz;
                u = 0f; v = 0f; w = 1f;
                return;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float t = d2 / (d2 - d6);
                qx = ax + acx * t; qy = ay + acy * t; qz = az + acz * t;
                u = 1f - t; v = 0f; w = t;
                return;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                qx = bx + (cx - bx) * t; qy = by + (cy - by) * t; qz = bz + (cz - bz) * t;
                u = 0f; v = 1f - t; w = t;
                return;
            }

            float denominator = va + vb + vc;
            if (!(Math.Abs(denominator) > 1e-30f))
            {
                // 面積がほぼ 0 の三角形: 一番近い頂点にする
                float da = apx * apx + apy * apy + apz * apz;
                float db = bpx * bpx + bpy * bpy + bpz * bpz;
                float dc = cpx * cpx + cpy * cpy + cpz * cpz;
                if (da <= db && da <= dc)
                {
                    qx = ax; qy = ay; qz = az;
                    u = 1f; v = 0f; w = 0f;
                }
                else if (db <= dc)
                {
                    qx = bx; qy = by; qz = bz;
                    u = 0f; v = 1f; w = 0f;
                }
                else
                {
                    qx = cx; qy = cy; qz = cz;
                    u = 0f; v = 0f; w = 1f;
                }
                return;
            }
            float inverse = 1f / denominator;
            float vv = vb * inverse, ww = vc * inverse;
            qx = ax + abx * vv + acx * ww;
            qy = ay + aby * vv + acy * ww;
            qz = az + abz * vv + acz * ww;
            u = 1f - vv - ww; v = vv; w = ww;
        }
    }
}
