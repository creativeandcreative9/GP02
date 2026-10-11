using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 三角形のレイキャスト用の境界箱の木（BVH）。
    /// 三角形の数が数十万でも、1回のレイキャストを数十マイクロ秒で終える（総当たりだと数ミリ秒かかる）。
    ///
    /// 木の形（どの三角形をどの箱に入れるか）は最初の位置で決め、頂点が動いたら <see cref="Refit"/> で箱だけ更新する。
    /// つまんで布の形が変わっても作り直しは要らない。形が大きく変わると箱が重なって遅くなるが、結果は変わらない。
    /// </summary>
    public sealed class TriangleBvh
    {
        const int LeafSize = 4;
        const int StackSize = 128;
        // 境界箱を少し広げる（辺や頂点すれすれに当たるレイを、丸め誤差で取りこぼさないため）
        const float BoxPadding = 1e-6f;

        readonly int[] _triangles;     // 頂点番号の3つ組
        readonly int[] _order;         // 葉に並べた三角形の番号
        readonly Vector3[] _boxMin;
        readonly Vector3[] _boxMax;
        readonly int[] _right;         // 内部ノード：右の子（左の子は node + 1）。葉：-1
        readonly int[] _start;         // 葉：_order の始点
        readonly int[] _count;         // 葉：三角形の数
        readonly byte[] _axis;         // 内部ノード：分割した軸
        readonly int[] _stack = new int[StackSize];
        int _nodeCount;

        public int TriangleCount => _triangles.Length / 3;
        public int NodeCount => _nodeCount;

        public TriangleBvh(Vector3[] positions, int[] triangles)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (triangles.Length % 3 != 0) throw new ArgumentException("三角形の頂点数が3の倍数ではありません。", nameof(triangles));

            _triangles = triangles;
            int count = triangles.Length / 3;
            _order = new int[count];
            for (int i = 0; i < count; i++) _order[i] = i;

            int maxNodes = Math.Max(1, 2 * count);
            _boxMin = new Vector3[maxNodes];
            _boxMax = new Vector3[maxNodes];
            _right = new int[maxNodes];
            _start = new int[maxNodes];
            _count = new int[maxNodes];
            _axis = new byte[maxNodes];

            if (count > 0)
            {
                var centers = new float[3][] { new float[count], new float[count], new float[count] };
                for (int t = 0; t < count; t++)
                {
                    Vector3 c = (positions[triangles[3 * t]] + positions[triangles[3 * t + 1]] + positions[triangles[3 * t + 2]]) / 3f;
                    centers[0][t] = c.X;
                    centers[1][t] = c.Y;
                    centers[2][t] = c.Z;
                }
                Build(centers, 0, count);
            }
            else
            {
                _nodeCount = 1;
                _right[0] = -1;
            }
            Refit(positions);
        }

        // 範囲 [start, end) の三角形から、ノードを作る（深さ優先の順に番号を振るので、子の番号は必ず親より大きい）
        int Build(float[][] centers, int start, int end)
        {
            int node = _nodeCount++;
            if (end - start <= LeafSize)
            {
                _right[node] = -1;
                _start[node] = start;
                _count[node] = end - start;
                return node;
            }

            // 三角形の中心が一番広がっている軸で、半分ずつに分ける
            int axis = 0;
            float bestExtent = -1f;
            for (int a = 0; a < 3; a++)
            {
                float[] key = centers[a];
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = start; i < end; i++)
                {
                    float v = key[_order[i]];
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
            Select(_order, centers[axis], start, end - 1, mid);
            _axis[node] = (byte)axis;
            Build(centers, start, mid);
            _right[node] = Build(centers, mid, end);
            return node;
        }

        // order[lo..hi] のうち、key が k 番目に小さいものを k の位置に置く（それより前は小さいか等しく、後ろは大きいか等しい）
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

        /// <summary>頂点が動いたあとに、境界箱を更新する。確保はしない。</summary>
        public void Refit(Vector3[] positions)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            // 頂点数十万の更新を 1 コマの中で終えたいので、Vector3.Min などの呼び出しを使わず、値をそのまま比べる
            // （Unity の Mono では、小さな関数呼び出しの積み重ねが目立つため）
            int[] triangles = _triangles;
            // 子の番号は親より大きいので、後ろから処理すれば子が先に終わる
            for (int node = _nodeCount - 1; node >= 0; node--)
            {
                float minX, minY, minZ, maxX, maxY, maxZ;
                if (_right[node] < 0)
                {
                    minX = minY = minZ = float.MaxValue;
                    maxX = maxY = maxZ = float.MinValue;
                    int end = _start[node] + _count[node];
                    for (int i = _start[node]; i < end; i++)
                    {
                        int t = 3 * _order[i];
                        for (int k = 0; k < 3; k++)
                        {
                            Vector3 p = positions[triangles[t + k]];
                            if (p.X < minX) minX = p.X;
                            if (p.Y < minY) minY = p.Y;
                            if (p.Z < minZ) minZ = p.Z;
                            if (p.X > maxX) maxX = p.X;
                            if (p.Y > maxY) maxY = p.Y;
                            if (p.Z > maxZ) maxZ = p.Z;
                        }
                    }
                    minX -= BoxPadding;
                    minY -= BoxPadding;
                    minZ -= BoxPadding;
                    maxX += BoxPadding;
                    maxY += BoxPadding;
                    maxZ += BoxPadding;
                }
                else
                {
                    Vector3 lMin = _boxMin[node + 1], lMax = _boxMax[node + 1];
                    Vector3 rMin = _boxMin[_right[node]], rMax = _boxMax[_right[node]];
                    minX = lMin.X < rMin.X ? lMin.X : rMin.X;
                    minY = lMin.Y < rMin.Y ? lMin.Y : rMin.Y;
                    minZ = lMin.Z < rMin.Z ? lMin.Z : rMin.Z;
                    maxX = lMax.X > rMax.X ? lMax.X : rMax.X;
                    maxY = lMax.Y > rMax.Y ? lMax.Y : rMax.Y;
                    maxZ = lMax.Z > rMax.Z ? lMax.Z : rMax.Z;
                }
                _boxMin[node] = new Vector3(minX, minY, minZ);
                _boxMax[node] = new Vector3(maxX, maxY, maxZ);
            }
        }

        /// <summary>
        /// 一番近い交差を探す（両面。Möller–Trumbore）。
        /// positions は Refit に渡した（最新の）頂点の位置。direction は長さ1でなくてもよいが、distance はその長さを単位とする。
        /// </summary>
        /// <param name="triangle">当たった三角形の番号（コンストラクターに渡した triangles の3つ組の番号）</param>
        public bool Raycast(Vector3 origin, Vector3 direction, Vector3[] positions, out float distance, out int triangle)
        {
            distance = 0f;
            triangle = -1;
            if (TriangleCount == 0) return false;

            // 軸に平行なレイで 0 による割り算（0 * 無限大 = NaN）にならないよう、小さな値に置き換える
            Vector3 inverse = new Vector3(SafeInverse(direction.X), SafeInverse(direction.Y), SafeInverse(direction.Z));
            float best = float.MaxValue;
            int bestTriangle = -1;
            int sp = 0;
            _stack[sp++] = 0;

            while (sp > 0)
            {
                int node = _stack[--sp];
                if (!HitsBox(origin, inverse, _boxMin[node], _boxMax[node], best)) continue;

                int right = _right[node];
                if (right < 0)
                {
                    int end = _start[node] + _count[node];
                    for (int i = _start[node]; i < end; i++)
                    {
                        int t = _order[i];
                        if (IntersectTriangle(origin, direction, positions[_triangles[3 * t]], positions[_triangles[3 * t + 1]],
                                positions[_triangles[3 * t + 2]], out float d) && d < best)
                        {
                            best = d;
                            bestTriangle = t;
                        }
                    }
                    continue;
                }

                // レイが進む向きの子を先に調べる（先に近い交差が見つかれば、遠い箱を調べずに済む）
                bool rightFirst = _axis[node] == 0 ? direction.X < 0f : _axis[node] == 1 ? direction.Y < 0f : direction.Z < 0f;
                if (rightFirst)
                {
                    _stack[sp++] = node + 1;
                    _stack[sp++] = right;
                }
                else
                {
                    _stack[sp++] = right;
                    _stack[sp++] = node + 1;
                }
            }

            if (bestTriangle < 0) return false;
            distance = best;
            triangle = bestTriangle;
            return true;
        }

        static float SafeInverse(float v)
        {
            if (MathF.Abs(v) < 1e-30f) v = v < 0f ? -1e-30f : 1e-30f;
            return 1f / v;
        }

        static bool HitsBox(Vector3 o, Vector3 inv, Vector3 mn, Vector3 mx, float maxDistance)
        {
            float t1 = (mn.X - o.X) * inv.X, t2 = (mx.X - o.X) * inv.X;
            float near = MathF.Min(t1, t2), far = MathF.Max(t1, t2);
            t1 = (mn.Y - o.Y) * inv.Y;
            t2 = (mx.Y - o.Y) * inv.Y;
            near = MathF.Max(near, MathF.Min(t1, t2));
            far = MathF.Min(far, MathF.Max(t1, t2));
            t1 = (mn.Z - o.Z) * inv.Z;
            t2 = (mx.Z - o.Z) * inv.Z;
            near = MathF.Max(near, MathF.Min(t1, t2));
            far = MathF.Min(far, MathF.Max(t1, t2));
            return far >= MathF.Max(near, 0f) && near <= maxDistance;
        }

        /// <summary>両面の Möller–Trumbore。レイの後ろ側（距離 0 以下）は当たらない。</summary>
        public static bool IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 p0, Vector3 p1, Vector3 p2, out float distance)
        {
            distance = 0f;
            Vector3 edge1 = p1 - p0;
            Vector3 edge2 = p2 - p0;
            Vector3 pvec = Vector3.Cross(direction, edge2);
            float det = Vector3.Dot(edge1, pvec);
            if (MathF.Abs(det) < 1e-12f) return false;

            float invDet = 1f / det;
            Vector3 tvec = origin - p0;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 qvec = Vector3.Cross(tvec, edge1);
            float v = Vector3.Dot(direction, qvec) * invDet;
            if (v < 0f || u + v > 1f) return false;
            float t = Vector3.Dot(edge2, qvec) * invDet;
            if (!(t > 0f)) return false;
            distance = t;
            return true;
        }
    }
}
