using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 三角形の表面で、ある点に一番近い点（三角形の上の点）を探す。境界箱の木（BVH）を使うので、三角形が数十万でも1回の検索は短い。
    /// 別のメッシュへシェイプキーを写すときに、写し先の頂点ごとに「元のメッシュの表面で一番近い点」を求めるのに使う。
    /// 木は最初の位置で作り、位置は変えない前提（動かすなら作り直す）。複数のスレッドから同時に呼んでもよい（検索中に共有の作業領域を持たない）。
    /// </summary>
    public sealed class ClosestPointSearch
    {
        /// <summary>検索の結果。</summary>
        public struct Hit
        {
            /// <summary>当たった三角形の番号（三角形の配列での 3 つ組の番号）。</summary>
            public int Triangle;

            /// <summary>三角形の上の一番近い点。</summary>
            public Vector3 Point;

            /// <summary>三角形の 3 頂点に対する重心座標（X, Y, Z が 1 つ目・2 つ目・3 つ目の頂点の重み。合計 1）。</summary>
            public Vector3 Barycentric;

            /// <summary>探した点から <see cref="Point"/> までの距離。</summary>
            public float Distance;
        }

        const int LeafSize = 4;
        const int StackSize = 128;

        readonly Vector3[] _positions;
        readonly int[] _triangles;
        readonly int[] _order;            // 葉に並べた三角形の番号
        readonly Vector3[] _boxMin;
        readonly Vector3[] _boxMax;
        readonly int[] _right;            // 内部ノード: 右の子（左の子は node + 1）。葉: -1
        readonly int[] _start;            // 葉: _order の始点
        readonly int[] _count;            // 葉: 三角形の数
        readonly Vector3[] _faceNormals;  // 三角形ごとの向き（面積 0 なら 0 ベクトル）
        int _nodeCount;

        public int TriangleCount => _triangles.Length / 3;

        public ClosestPointSearch(Vector3[] positions, int[] triangles)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (triangles.Length % 3 != 0) throw new ArgumentException("三角形の頂点数が3の倍数ではありません。", nameof(triangles));
            for (int i = 0; i < triangles.Length; i++)
            {
                if ((uint)triangles[i] >= (uint)positions.Length) throw new ArgumentException("三角形が、存在しない頂点を指しています。", nameof(triangles));
            }

            _positions = positions;
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

            _faceNormals = new Vector3[count];
            var centers = new Vector3[count];
            for (int t = 0; t < count; t++)
            {
                Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                centers[t] = (a + b + c) / 3f;
                Vector3 n = Vector3.Cross(b - a, c - a);
                float length = n.Length();
                _faceNormals[t] = length > 1e-20f ? n / length : Vector3.Zero;
            }

            if (count > 0)
            {
                var keys = new float[count];
                Build(centers, keys, 0, count);
            }
            else
            {
                _nodeCount = 1;
                _right[0] = -1;
                _boxMin[0] = _boxMax[0] = Vector3.Zero;
            }
        }

        // 範囲 [start, start + count) を 1 つのノードにして、node の番号を返す
        int Build(Vector3[] centers, float[] keys, int start, int count)
        {
            int node = _nodeCount++;
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            Vector3 centerMin = new Vector3(float.MaxValue), centerMax = new Vector3(float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int t = _order[i];
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = _positions[_triangles[3 * t + k]];
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                centerMin = Vector3.Min(centerMin, centers[t]);
                centerMax = Vector3.Max(centerMax, centers[t]);
            }
            _boxMin[node] = min;
            _boxMax[node] = max;

            Vector3 extent = centerMax - centerMin;
            if (count <= LeafSize || (extent.X <= 0f && extent.Y <= 0f && extent.Z <= 0f))
            {
                _right[node] = -1;
                _start[node] = start;
                _count[node] = count;
                return node;
            }

            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            for (int i = start; i < start + count; i++)
            {
                Vector3 c = centers[_order[i]];
                keys[i] = axis == 0 ? c.X : axis == 1 ? c.Y : c.Z;
            }
            Array.Sort(keys, _order, start, count);

            int half = count / 2;
            Build(centers, keys, start, half);
            _right[node] = Build(centers, keys, start + half, count - half);
            return node;
        }

        /// <summary>
        /// point から maxDistance 以内で、一番近い三角形の上の点を探す。見つからなければ false。
        /// </summary>
        public bool TryFindClosest(Vector3 point, float maxDistance, out Hit hit)
        {
            return Search(point, maxDistance, false, Vector3.Zero, 0f, out hit);
        }

        /// <summary>
        /// <see cref="TryFindClosest(Vector3, float, out Hit)"/> の、向きで絞る版。
        /// 三角形の向き（頂点の並びから決まる面の法線）と referenceNormal の内積が minNormalDot 未満の三角形は、近くても避ける。
        /// 薄い服の裏側や、反対向きの面に写してしまわないための絞り込み。0 なら逆向き（90 度より大きく違う）の面を避ける。
        /// 面積 0 の三角形は向きが決まらないので避けない。
        /// </summary>
        public bool TryFindClosest(Vector3 point, float maxDistance, Vector3 referenceNormal, float minNormalDot, out Hit hit)
        {
            return Search(point, maxDistance, true, referenceNormal, minNormalDot, out hit);
        }

        bool Search(Vector3 point, float maxDistance, bool useNormal, Vector3 referenceNormal, float minNormalDot, out Hit hit)
        {
            hit = default;
            if (_triangles.Length == 0 || !(maxDistance >= 0f)) return false;

            float bestSq = maxDistance * maxDistance;
            bool found = false;
            Span<int> stack = stackalloc int[StackSize];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                if (BoxDistanceSquared(node, point) > bestSq) continue;

                if (_right[node] < 0)
                {
                    int end = _start[node] + _count[node];
                    for (int i = _start[node]; i < end; i++)
                    {
                        int t = _order[i];
                        if (useNormal)
                        {
                            Vector3 n = _faceNormals[t];
                            if (n != Vector3.Zero && Vector3.Dot(n, referenceNormal) < minNormalDot) continue;
                        }
                        Vector3 closest = ClosestOnTriangle(point, _positions[_triangles[3 * t]], _positions[_triangles[3 * t + 1]],
                            _positions[_triangles[3 * t + 2]], out Vector3 barycentric);
                        float d = Vector3.DistanceSquared(closest, point);
                        // 同じ距離なら、先に見つけた三角形を残す
                        if (d > bestSq || (found && d >= bestSq)) continue;
                        bestSq = d;
                        found = true;
                        hit.Triangle = t;
                        hit.Point = closest;
                        hit.Barycentric = barycentric;
                    }
                    continue;
                }

                // 近い方の子を後に積み、先に調べる
                int left = node + 1, right = _right[node];
                if (top + 2 > StackSize) continue;
                if (BoxDistanceSquared(left, point) <= BoxDistanceSquared(right, point))
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
            if (found) hit.Distance = MathF.Sqrt(bestSq);
            return found;
        }

        float BoxDistanceSquared(int node, Vector3 p)
        {
            Vector3 min = _boxMin[node], max = _boxMax[node];
            float dx = Math.Max(Math.Max(min.X - p.X, 0f), p.X - max.X);
            float dy = Math.Max(Math.Max(min.Y - p.Y, 0f), p.Y - max.Y);
            float dz = Math.Max(Math.Max(min.Z - p.Z, 0f), p.Z - max.Z);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// 三角形 abc の上で p に一番近い点と、その重心座標を返す。
        /// 辺や頂点の上も含める。面積がほぼ 0 の三角形でも、三角形の上の点を返す。
        /// </summary>
        public static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 barycentric)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return a;
            }

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                barycentric = new Vector3(0f, 1f, 0f);
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                barycentric = new Vector3(1f - v, v, 0f);
                return a + ab * v;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                barycentric = new Vector3(0f, 0f, 1f);
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                barycentric = new Vector3(1f - w, 0f, w);
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                barycentric = new Vector3(0f, 1f - w, w);
                return b + (c - b) * w;
            }

            float denominator = va + vb + vc;
            if (!(MathF.Abs(denominator) > 1e-30f))
            {
                // 面積がほぼ 0 の三角形: 一番近い頂点にする
                float da = ap.LengthSquared(), db = bp.LengthSquared(), dc = cp.LengthSquared();
                if (da <= db && da <= dc)
                {
                    barycentric = new Vector3(1f, 0f, 0f);
                    return a;
                }
                if (db <= dc)
                {
                    barycentric = new Vector3(0f, 1f, 0f);
                    return b;
                }
                barycentric = new Vector3(0f, 0f, 1f);
                return c;
            }
            float inverse = 1f / denominator;
            float vv = vb * inverse, ww = vc * inverse;
            barycentric = new Vector3(1f - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }
    }
}
