using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// メッシュの開いた縁（三角形が1つしか付いていない辺）を、つながったものごとにまとめた輪。
    /// スカートのウエスト・裾、上着の首まわり・袖口などが1つずつの輪になる。
    /// </summary>
    public sealed class OpenRing
    {
        /// <summary>輪のある島（<see cref="MeshTopology.TriangleIsland"/>）。</summary>
        public int Island { get; }

        /// <summary>輪の辺ごとの頂点の組（2つずつ。まとめた後の頂点番号）。</summary>
        public int[] Edges { get; }

        public int EdgeCount => Edges.Length / 2;

        /// <summary>辺の長さの合計（メートル）。</summary>
        public float Length { get; }

        /// <summary>
        /// 輪の重心。辺の中点を、辺の長さで重みを付けて平均した位置。
        /// 頂点の平均だと、頂点が片側に偏って並んだ輪（前だけ細かいウエストなど）で重心がずれるため、辺の長さで重みを付ける。
        /// </summary>
        public Vector3 Centroid { get; }

        /// <summary>付け根らしさ（<see cref="AnchorRings.Score"/>）。<see cref="AnchorRings.SelectAnchorRings"/> が入れる。</summary>
        public float Score { get; internal set; }

        internal OpenRing(int island, int[] edges, float length, Vector3 centroid)
        {
            Island = island;
            Edges = edges;
            Length = length;
            Centroid = centroid;
        }
    }

    /// <summary>
    /// 自動でピン留めする粒子を探した結果（<see cref="AnchorRings.Find"/>）。
    /// </summary>
    public sealed class AnchorSearch
    {
        /// <summary>ピン留めする粒子の番号（昇順）。</summary>
        public readonly List<int> Particles = new List<int>();

        /// <summary>付け根にした輪（島ごとに1つ）。</summary>
        public readonly List<OpenRing> Rings = new List<OpenRing>();

        /// <summary>島の数。</summary>
        public int IslandCount;

        /// <summary>開いた縁のある島の数（開いた縁の無い、閉じた部品は含まない）。</summary>
        public int OpenIslandCount;
    }

    /// <summary>
    /// 服の付け根（スカート・タイツのウエスト、上着の首まわり、袖の肩の側）を、メッシュの形から自動で見つけて、その近くの粒子を選ぶ。
    ///
    /// 決め方: 部品（島）ごとに、開いた縁をつないだ輪（<see cref="OpenRing"/>）を集め、
    /// 「輪の重心の高さ − <see cref="AxisDistanceWeight"/> ×（輪の重心から、アバターの中心を通る縦の軸までの水平距離）」
    /// が一番大きい輪を、その部品の付け根とする。高いほど、中心に近いほど付け根らしい（腰・首・肩の側は、どれも体の上の側の、中心に近い輪になる）。
    /// その輪の線（辺の線分）から帯の幅以内にある、同じ部品の粒子を選ぶ。開いた縁の無い部品（閉じた形）は何も選ばない。
    ///
    /// 高さの向きと軸は呼び出し側が決める（アバターの根元の向きと位置。無ければ世界の上向きと、衣装の境界の中心）。
    /// 画面（Unity）に依存しないので、クラウドで自動テストできる。
    /// </summary>
    public static class AnchorRings
    {
        /// <summary>帯の幅（メートル）の既定値。輪の線からこの距離以内の粒子を留める。</summary>
        public const float DefaultBandWidth = 0.02f;

        /// <summary>輪の重心から縦の軸までの水平距離にかける重み。</summary>
        public const float AxisDistanceWeight = 0.5f;

        /// <summary>
        /// 開いた縁の輪を集める。positions は、まとめた後の頂点の今の位置（<see cref="MeshTopology.Positions"/> と同じ長さ）。
        /// 裏面用に同じ3頂点で重ねた三角形は1枚として数える（両面の布の縁も、開いた縁として見つけるため）。
        /// 島をまたいで1点だけで触れている縁は、島ごとに別の輪にする。
        /// </summary>
        public static List<OpenRing> FindOpenRings(MeshTopology topology, Vector3[] positions)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (positions.Length != topology.Positions.Length) throw new ArgumentException("頂点数が一致しません。", nameof(positions));

            int[] triangles = topology.Triangles;
            int triangleCount = topology.TriangleCount;

            // 同じ3頂点の三角形（裏面用の重ね）は、最初の1枚だけを数える
            var seen = new HashSet<(int, int, int)>();
            var edgeIndex = new Dictionary<long, int>(PairKeys.Comparer);
            var edgeA = new List<int>();
            var edgeB = new List<int>();
            var edgeUses = new List<int>();
            var edgeIsland = new List<int>();
            for (int t = 0; t < triangleCount; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                if (!seen.Add(SortedTriple(a, b, c))) continue;
                for (int e = 0; e < 3; e++)
                {
                    int i = triangles[3 * t + e];
                    int j = triangles[3 * t + (e + 1) % 3];
                    long key = PairKeys.Make(i, j);
                    if (edgeIndex.TryGetValue(key, out int index))
                    {
                        edgeUses[index]++;
                        continue;
                    }
                    edgeIndex.Add(key, edgeA.Count);
                    edgeA.Add(i);
                    edgeB.Add(j);
                    edgeUses.Add(1);
                    edgeIsland.Add(topology.TriangleIsland[t]);
                }
            }

            // 1枚の三角形にしか付いていない辺を、頂点（と島）でつながったものごとにまとめる
            var nodeOf = new Dictionary<long, int>();
            var parent = new List<int>();
            var boundary = new List<int>();
            for (int k = 0; k < edgeA.Count; k++)
            {
                if (edgeUses[k] != 1) continue;
                boundary.Add(k);
                int na = Node(nodeOf, parent, edgeA[k], edgeIsland[k]);
                int nb = Node(nodeOf, parent, edgeB[k], edgeIsland[k]);
                Merge(parent, na, nb);
            }

            var groups = new Dictionary<int, List<int>>();
            var order = new List<int>();   // 輪の並びを、辺を見つけた順（＝三角形の順）にそろえる
            foreach (int k in boundary)
            {
                int root = Root(parent, nodeOf[NodeKey(edgeA[k], edgeIsland[k])]);
                if (!groups.TryGetValue(root, out List<int> list))
                {
                    list = new List<int>();
                    groups.Add(root, list);
                    order.Add(root);
                }
                list.Add(k);
            }

            var rings = new List<OpenRing>(order.Count);
            foreach (int root in order)
            {
                List<int> list = groups[root];
                var edges = new int[2 * list.Count];
                float length = 0f;
                float sx = 0f, sy = 0f, sz = 0f, vx = 0f, vy = 0f, vz = 0f;
                for (int n = 0; n < list.Count; n++)
                {
                    int k = list[n];
                    edges[2 * n] = edgeA[k];
                    edges[2 * n + 1] = edgeB[k];
                    Vector3 pa = positions[edgeA[k]], pb = positions[edgeB[k]];
                    float dx = pb.X - pa.X, dy = pb.Y - pa.Y, dz = pb.Z - pa.Z;
                    float edgeLength = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    length += edgeLength;
                    sx += edgeLength * 0.5f * (pa.X + pb.X);
                    sy += edgeLength * 0.5f * (pa.Y + pb.Y);
                    sz += edgeLength * 0.5f * (pa.Z + pb.Z);
                    vx += 0.5f * (pa.X + pb.X);
                    vy += 0.5f * (pa.Y + pb.Y);
                    vz += 0.5f * (pa.Z + pb.Z);
                }
                // 辺がすべて潰れている（長さ 0）輪は、中点の平均を使う
                Vector3 centroid = length > 1e-9f
                    ? new Vector3(sx / length, sy / length, sz / length)
                    : new Vector3(vx / list.Count, vy / list.Count, vz / list.Count);
                rings.Add(new OpenRing(edgeIsland[list[0]], edges, length, centroid));
            }
            return rings;
        }

        /// <summary>
        /// 輪の付け根らしさ。重心の高さ（up の向きに、axisPoint から測る）から、重心と縦の軸（axisPoint を通り up の向き）の水平距離の
        /// <see cref="AxisDistanceWeight"/> 倍を引いたもの。大きいほど付け根らしい。up は長さ 1 でなくてもよい（0 なら世界の上向き）。
        /// </summary>
        public static float Score(Vector3 centroid, Vector3 up, Vector3 axisPoint)
        {
            up = NormalizeUp(up);
            float dx = centroid.X - axisPoint.X, dy = centroid.Y - axisPoint.Y, dz = centroid.Z - axisPoint.Z;
            float height = dx * up.X + dy * up.Y + dz * up.Z;
            float rx = dx - up.X * height, ry = dy - up.Y * height, rz = dz - up.Z * height;
            float horizontal = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            return height - AxisDistanceWeight * horizontal;
        }

        /// <summary>
        /// 島ごとに、付け根らしさ（<see cref="Score"/>）が一番大きい輪を1つ選んで result に入れる（先に空にする。島の番号の順）。
        /// 同じ点数なら、先に見つけた輪。選んだ輪の <see cref="OpenRing.Score"/> も入れる。
        /// </summary>
        public static void SelectAnchorRings(IReadOnlyList<OpenRing> rings, Vector3 up, Vector3 axisPoint, List<OpenRing> result)
        {
            if (rings == null) throw new ArgumentNullException(nameof(rings));
            if (result == null) throw new ArgumentNullException(nameof(result));
            result.Clear();
            var best = new Dictionary<int, OpenRing>();
            foreach (OpenRing ring in rings)
            {
                ring.Score = Score(ring.Centroid, up, axisPoint);
                if (!best.TryGetValue(ring.Island, out OpenRing current) || ring.Score > current.Score) best[ring.Island] = ring;
            }
            var islands = new List<int>(best.Keys);
            islands.Sort();
            foreach (int island in islands) result.Add(best[island]);
        }

        /// <summary>
        /// 付け根として留める粒子を探す。まとめた後の頂点の今の位置 welded、粒子の乗っている島 particleIsland、粒子の今の位置 particles から、
        /// 島ごとの付け根の輪を決め、その輪の線（辺の線分）から bandWidth 以内にある、同じ島の粒子を <see cref="AnchorSearch.Particles"/> に入れる。
        /// </summary>
        /// <param name="up">高さの向き（アバターの根元の上向き）</param>
        /// <param name="axisPoint">縦の軸が通る点（アバターの根元の位置）</param>
        /// <param name="bandWidth">帯の幅（メートル）。0 以下なら <see cref="DefaultBandWidth"/></param>
        public static AnchorSearch Find(MeshTopology topology, Vector3[] welded, int[] particleIsland, ReadOnlySpan<Vector3> particles,
            Vector3 up, Vector3 axisPoint, float bandWidth = DefaultBandWidth)
        {
            if (particleIsland == null) throw new ArgumentNullException(nameof(particleIsland));
            if (particleIsland.Length != particles.Length) throw new ArgumentException("粒子数が一致しません。", nameof(particleIsland));
            if (!(bandWidth > 0f)) bandWidth = DefaultBandWidth;

            var search = new AnchorSearch { IslandCount = topology.IslandCount };
            List<OpenRing> rings = FindOpenRings(topology, welded);
            var openIslands = new HashSet<int>();
            foreach (OpenRing ring in rings) openIslands.Add(ring.Island);
            search.OpenIslandCount = openIslands.Count;

            SelectAnchorRings(rings, up, axisPoint, search.Rings);
            if (search.Rings.Count == 0) return search;

            // 付け根のある島の粒子だけを格子に入れて、辺の近くの粒子を素早く探す
            var anchored = new HashSet<int>();
            foreach (OpenRing ring in search.Rings) anchored.Add(ring.Island);
            var grid = new PointGrid(bandWidth);
            for (int i = 0; i < particles.Length; i++)
            {
                if (anchored.Contains(particleIsland[i])) grid.Add(i, particles[i]);
            }

            var marked = new bool[particles.Length];
            var candidates = new List<int>();
            float bandSq = bandWidth * bandWidth;
            foreach (OpenRing ring in search.Rings)
            {
                for (int e = 0; e < ring.EdgeCount; e++)
                {
                    Vector3 pa = welded[ring.Edges[2 * e]], pb = welded[ring.Edges[2 * e + 1]];
                    float dx = pb.X - pa.X, dy = pb.Y - pa.Y, dz = pb.Z - pa.Z;
                    float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    // 長い辺は、帯の幅の 2 倍ほどに区切って探す（立方体の探索範囲が、辺の長さで大きくならないように）
                    int pieces = Math.Max(1, (int)MathF.Ceiling(length / (2f * bandWidth)));
                    float reach = length / (2f * pieces) + bandWidth;
                    for (int k = 0; k < pieces; k++)
                    {
                        float t = (k + 0.5f) / pieces;
                        grid.Query(new Vector3(pa.X + dx * t, pa.Y + dy * t, pa.Z + dz * t), reach, candidates);
                        for (int n = 0; n < candidates.Count; n++)
                        {
                            int i = candidates[n];
                            if (marked[i] || particleIsland[i] != ring.Island) continue;
                            if (DistanceSquaredToSegment(particles[i], pa, pb) > bandSq) continue;
                            marked[i] = true;
                            search.Particles.Add(i);
                        }
                    }
                }
            }
            search.Particles.Sort();
            return search;
        }

        /// <summary>点 p から線分 ab までの距離の2乗。</summary>
        public static float DistanceSquaredToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            float apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
            float lengthSq = abx * abx + aby * aby + abz * abz;
            float t = lengthSq > 1e-18f ? (apx * abx + apy * aby + apz * abz) / lengthSq : 0f;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float qx = apx - abx * t, qy = apy - aby * t, qz = apz - abz * t;
            return qx * qx + qy * qy + qz * qz;
        }

        static Vector3 NormalizeUp(Vector3 up)
        {
            float length = MathF.Sqrt(up.X * up.X + up.Y * up.Y + up.Z * up.Z);
            return length > 1e-9f ? new Vector3(up.X / length, up.Y / length, up.Z / length) : new Vector3(0f, 1f, 0f);
        }

        static (int, int, int) SortedTriple(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        static long NodeKey(int vertex, int island) => ((long)vertex << 32) | (uint)island;

        static int Node(Dictionary<long, int> nodeOf, List<int> parent, int vertex, int island)
        {
            long key = NodeKey(vertex, island);
            if (nodeOf.TryGetValue(key, out int node)) return node;
            node = parent.Count;
            parent.Add(node);
            nodeOf.Add(key, node);
            return node;
        }

        static int Root(List<int> parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        static void Merge(List<int> parent, int a, int b)
        {
            int ra = Root(parent, a), rb = Root(parent, b);
            if (ra != rb) parent[rb] = ra;
        }
    }
}
