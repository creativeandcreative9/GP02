using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 表面に置いた点（粒子）どうしのつながりを作る処理。
    /// 細かい粒子の布と、粗い段の粒子（階層）の両方で使う。
    /// </summary>
    internal static class ParticleNetwork
    {
        /// <summary>
        /// points のうち members に含まれる点どうしを、表面に沿ってつながっているものだけ結ぶ。
        /// 伸びの制約（固い）は、本当に隣り合う点だけにする。隣の隣まで固くつなぐと布が曲がれなくなり、
        /// 曲げたときに制約どうしが矛盾して伸びが残る。隣り合うかどうかはガブリエルグラフで判定する
        /// （2つの点を直径とする球の中に、別の点がなければ隣り合う）。それ以外は曲げの制約（柔らかい）にする。
        /// </summary>
        /// <param name="bendPairs">曲げの制約が不要なら null</param>
        public static void Connect(SurfaceSearch search, Vector3[] points, int[] pointTriangle, IReadOnlyList<int> members,
            float stretchRange, float bendRange, List<int> stretchPairs, List<int> bendPairs)
        {
            float range = bendPairs != null ? bendRange : stretchRange;
            var grid = new PointGrid(range);
            foreach (int m in members) grid.Add(m, points[m]);
            var nearby = new List<int>();
            var connected = new List<int>();

            foreach (int p in members)
            {
                search.Run(pointTriangle[p], points[p], range);
                grid.Query(points[p], range, nearby);
                connected.Clear();
                foreach (int q in nearby)
                {
                    if (q == p || !search.Contains(pointTriangle[q])) continue;
                    if (Vector3.Distance(points[p], points[q]) <= range) connected.Add(q);
                }

                foreach (int q in connected)
                {
                    if (q < p) continue;
                    float distance = Vector3.Distance(points[p], points[q]);
                    bool neighbor = distance <= stretchRange && IsGabrielEdge(points, p, q, connected);
                    var pairs = neighbor ? stretchPairs : bendPairs;
                    if (pairs == null) continue;
                    pairs.Add(p);
                    pairs.Add(q);
                }
            }
        }

        /// <summary>
        /// 対象の点（targets）を、表面に沿って range 以内にある members の点に、距離に応じた重みで結びつける。
        /// 近くに見つからなければ、同じ島で一番近い点に結びつける。
        /// </summary>
        /// <param name="targetTriangle">対象ごとの、探索を始める三角形。-1 の対象は結びつけない</param>
        /// <param name="offsets">対象ごとの範囲（CSR、要素数は対象数＋1）</param>
        public static void Bind(MeshTopology topology, SurfaceSearch search,
            Vector3[] targets, int[] targetTriangle,
            Vector3[] points, int[] pointTriangle, IReadOnlyList<int> members, float range,
            out int[] offsets, out int[] ids, out float[] weights)
        {
            var grid = new PointGrid(range);
            foreach (int m in members) grid.Add(m, points[m]);
            var nearby = new List<int>();
            var idList = new List<int>();
            var weightList = new List<float>();
            offsets = new int[targets.Length + 1];

            for (int v = 0; v < targets.Length; v++)
            {
                int triangle = targetTriangle[v];
                if (triangle >= 0)
                {
                    Vector3 position = targets[v];
                    int start = idList.Count;
                    search.Run(triangle, position, range);
                    grid.Query(position, range, nearby);
                    float total = 0f;
                    foreach (int q in nearby)
                    {
                        if (!search.Contains(pointTriangle[q])) continue;
                        float t = Vector3.Distance(position, points[q]) / range;
                        if (t >= 1f) continue;
                        float s = 1f - t * t;
                        idList.Add(q);
                        weightList.Add(s * s);
                        total += s * s;
                    }

                    if (total > 0f)
                    {
                        for (int k = start; k < weightList.Count; k++) weightList[k] /= total;
                    }
                    else
                    {
                        // 近くに点がない（ごく小さな部品など）ときは、同じ島で一番近い点についていく
                        int island = topology.TriangleIsland[triangle];
                        int nearest = -1;
                        float bestSq = float.MaxValue;
                        foreach (int m in members)
                        {
                            if (topology.TriangleIsland[pointTriangle[m]] != island) continue;
                            float sq = Vector3.DistanceSquared(points[m], position);
                            if (sq < bestSq)
                            {
                                bestSq = sq;
                                nearest = m;
                            }
                        }
                        if (nearest >= 0)
                        {
                            idList.Add(nearest);
                            weightList.Add(1f);
                        }
                    }
                }
                offsets[v + 1] = idList.Count;
            }

            ids = idList.ToArray();
            weights = weightList.ToArray();
        }

        /// <summary>
        /// members から、互いに spacing 以上離れた点を選んで間引く（同じ島の中だけで比べる）。
        /// どの島からも最低1つは選ばれる。
        /// </summary>
        public static List<int> Thin(MeshTopology topology, Vector3[] points, int[] pointTriangle, IReadOnlyList<int> members, float spacing)
        {
            var grid = new PointGrid(spacing);
            var nearby = new List<int>();
            var selected = new List<int>();
            float spacingSq = spacing * spacing;
            int[] island = topology.TriangleIsland;

            foreach (int m in members)
            {
                int memberIsland = island[pointTriangle[m]];
                grid.Query(points[m], spacing, nearby);
                bool tooClose = false;
                foreach (int other in nearby)
                {
                    if (island[pointTriangle[other]] != memberIsland) continue;
                    if (Vector3.DistanceSquared(points[other], points[m]) < spacingSq)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;
                grid.Add(m, points[m]);
                selected.Add(m);
            }
            return selected;
        }

        // p と q を直径とする球の中に、ほかの点がないか。
        // 球の中の点は p から |pq| 以内にあるので、p の周りの点（candidates）だけ調べれば足りる
        static bool IsGabrielEdge(Vector3[] points, int p, int q, List<int> candidates)
        {
            Vector3 middle = (points[p] + points[q]) * 0.5f;
            float radiusSq = Vector3.DistanceSquared(points[p], points[q]) * 0.25f;
            foreach (int r in candidates)
            {
                if (r == q) continue;
                if (Vector3.DistanceSquared(points[r], middle) < radiusSq) return false;
            }
            return true;
        }
    }
}
