using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 重ね着の層どうしの接触の組（<see cref="ClothSolver.SetLayerContacts"/>）。
    ///
    /// 外側の層の粒子 i と、そのすぐ内側にある別の島（内側の層）の粒子 j の組を、元の形で探して記録する。
    /// ソルバーは <c>dot(p_i - p_j, n_j) &gt;= 最小の隙間</c>（n_j は j の元の形の外向き）を満たすよう両方を動かすので、
    /// 外の布を内へ押すと内の布も一緒に押され、外の布が内の布を突き抜けない。外へ引くときは何もしない（片側だけの制約）。
    /// </summary>
    public sealed class LayerContacts
    {
        /// <summary>内側の層を探す距離（メートル）の既定値。これより離れた層は、触れ合っている重ね着とみなさない。</summary>
        public const float DefaultSearchDistance = 0.025f;

        /// <summary>層どうしの最小の隙間（メートル）の既定値。元の隙間がこれより狭ければ、元の隙間を使う。</summary>
        public const float DefaultMinGap = 0.0015f;

        // 内側の層の粒子の向きが、外側の粒子の向きとこれ以上そろっているときだけ組にする（層が向かい合わせの面を拾わないため）
        const float MinNormalAgreement = 0.5f;

        // 自分の島に当たったときに、その先へレイをやり直す回数の上限
        const int MaxOwnIslandSkips = 8;

        // 内向きのレイが内側の層に当たらない粒子（内側の層の裾より先など）を、近くの内側の層の縁と組にするときの、元の隙間の下限（メートル）。
        // 縁どうしが同じ高さで並んでいるだけの部品（継ぎ目で突き合わせた別の島など）を組にしないため
        const float MinEdgeRestGap = 0.0005f;

        // 組を解く範囲：外側の粒子が、内側の粒子の面に沿って、粒子の間隔のこの倍より離れていれば解かない（内側の層の縁の外にいる）
        const float ReachFactor = 1.5f;

        /// <summary>
        /// 組を解く範囲（メートル）。外側の粒子が、内側の粒子の面に沿ってこれより離れている（内側の層の縁の外にいる）ときは、組を解かない。
        /// 粒子の間隔の 1.5 倍。内側の層の上にいる外側の粒子は、一番近い内側の粒子からほぼ間隔以内にある。
        /// </summary>
        public float Reach { get; }

        /// <summary>組の数。</summary>
        public int Count => Outer.Length;

        /// <summary>組ごとの、外側の層の粒子。</summary>
        public int[] Outer { get; }

        /// <summary>組ごとの、内側の層の粒子（元の形で一番近いもの。ソルバーは Solve ごとに探し直す）。</summary>
        public int[] Inner { get; }

        /// <summary>組ごとの、保つ隙間（メートル）。最小の隙間と元の隙間の小さいほう。</summary>
        public float[] Gaps { get; }

        /// <summary>組ごとの、元の形での隙間（メートル）。</summary>
        public float[] RestGaps { get; }

        /// <summary>
        /// 粒子ごとに、すぐ内側に接触の組の相手（内側の層）がいるか（内向きのレイが内側の層に当たった粒子だけ。縁の外の組は含めない）。
        /// 体や内側の布までの隙間を測るとき、この粒子は内側の層を通り越して体までを測ってよい（内側の層は押せば動くので）。
        /// </summary>
        public bool[] HasInnerLayer { get; }

        LayerContacts(int[] outer, int[] inner, float[] gaps, float[] restGaps, bool[] hasInnerLayer, float reach)
        {
            Reach = reach;
            Outer = outer;
            Inner = inner;
            Gaps = gaps;
            RestGaps = restGaps;
            HasInnerLayer = hasInnerLayer;
        }

        /// <summary>組が無い（重ね着の無い布）。</summary>
        public static LayerContacts Empty(int particleCount) => new LayerContacts(new int[0], new int[0], new float[0], new float[0], new bool[particleCount], 0f);

        /// <summary>布の元の形から、層どうしの接触の組を探す。</summary>
        public static LayerContacts Find(ParticleCloth cloth, float searchDistance = DefaultSearchDistance, float minGap = DefaultMinGap)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            Vector3[] positions = cloth.Topology.Positions;
            return Find(cloth, new TriangleBvh(positions, cloth.Topology.Triangles), positions, searchDistance, minGap);
        }

        /// <summary>
        /// 布の元の形から、層どうしの接触の組を探す。
        /// 各粒子から元の形の内向きへ、別の島の三角形へ searchDistance までレイを飛ばし、当たった所に一番近い、その島の粒子を相手にする。
        /// 当たらない粒子も、searchDistance 以内に別の島の粒子があり、その面より外側にあれば、一番近い粒子を相手にする
        /// （内側の層の裾より先にある外側の布が、動いて内側の層の上へ来たときに突き抜けないように。縁の外にいる間は <see cref="Reach"/> で解かない）。
        /// </summary>
        /// <param name="bvh">布の三角形（<c>cloth.Topology.Triangles</c>）の木。positions の形で作ってあること</param>
        /// <param name="positions">bvh を作った、まとめた頂点の位置（元の形）</param>
        public static LayerContacts Find(ParticleCloth cloth, TriangleBvh bvh, Vector3[] positions, float searchDistance = DefaultSearchDistance, float minGap = DefaultMinGap)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            if (bvh == null) throw new ArgumentNullException(nameof(bvh));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (!(searchDistance > 0f)) throw new ArgumentOutOfRangeException(nameof(searchDistance));
            if (!(minGap >= 0f)) throw new ArgumentOutOfRangeException(nameof(minGap));

            Vector3[] rest = cloth.Model.RestPositions;
            Vector3[] normals = cloth.Model.RestNormals;
            int n = rest.Length;
            if (normals == null || cloth.Topology.IslandCount < 2) return Empty(n);

            int[] particleIsland = cloth.ParticleIsland;
            int[] triangleIsland = cloth.Topology.TriangleIsland;
            float spacing = cloth.Spacing;
            float searchRadius = 3f * spacing;
            var grid = new PointGrid(2f * spacing);
            for (int i = 0; i < n; i++) grid.Add(i, rest[i]);

            var outer = new List<int>();
            var inner = new List<int>();
            var gaps = new List<float>();
            var restGaps = new List<float>();
            var hasInner = new bool[n];
            var candidates = new List<int>();
            var missed = new List<int>();
            // 島の組ごとの、内向きのレイで見つかった組の数（どちらの島が外側かを決める）
            var order = new Dictionary<long, int>();
            int islandCount = cloth.Topology.IslandCount;

            // 1. 内向きのレイが内側の層に当たる粒子
            for (int i = 0; i < n; i++)
            {
                Vector3 normal = normals[i];
                if (normal == Vector3.Zero) continue;
                Vector3 direction = -normal;

                int hitIsland = -1;
                float hitDistance = 0f;
                float traveled = 0f;
                for (int skip = 0; skip <= MaxOwnIslandSkips; skip++)
                {
                    Vector3 origin = rest[i] + direction * traveled;
                    if (!bvh.Raycast(origin, direction, positions, out float d, out int triangle)) break;
                    float total = traveled + d;
                    if (total > searchDistance) break;
                    if (triangleIsland[triangle] != particleIsland[i])
                    {
                        hitIsland = triangleIsland[triangle];
                        hitDistance = total;
                        break;
                    }
                    traveled = total + 1e-5f;
                }
                if (hitIsland < 0)
                {
                    missed.Add(i);
                    continue;
                }

                Vector3 hit = rest[i] + direction * hitDistance;
                grid.Query(hit, searchRadius, candidates);
                int best = Nearest(candidates, rest, hit, searchRadius, j => particleIsland[j] == hitIsland);
                if (best < 0 || !TryAdd(i, best, 0f)) continue;
                hasInner[i] = true;
                long key = (long)particleIsland[i] * islandCount + hitIsland;
                order.TryGetValue(key, out int count);
                order[key] = count + 1;
            }

            // 2. 当たらない粒子（内側の層の裾より先など）は、近くの、自分の島より内側にある島の粒子（縁）と組にする。
            //    内側の層の縁が外側の層の面より少し外に出ている所（ウエストの縁など）で、内と外を取り違えないよう、島の内外はレイで見つかった組の数で決める
            foreach (int i in missed)
            {
                int island = particleIsland[i];
                Vector3 normal = normals[i];
                grid.Query(rest[i], searchDistance, candidates);
                int best = Nearest(candidates, rest, rest[i], searchDistance,
                    j => particleIsland[j] != island && Vector3.Dot(normals[j], normal) >= MinNormalAgreement && IsOutside(order, islandCount, island, particleIsland[j]));
                if (best >= 0) TryAdd(i, best, MinEdgeRestGap);
            }

            bool TryAdd(int i, int j, float minRestGap)
            {
                Vector3 innerNormal = normals[j];
                if (Vector3.Dot(innerNormal, normals[i]) < MinNormalAgreement) return false;
                float restGap = Vector3.Dot(rest[i] - rest[j], innerNormal);
                // 元の形で内側の層より内にある組は、どちらが外か分からないので使わない
                if (!(restGap > minRestGap)) return false;
                outer.Add(i);
                inner.Add(j);
                restGaps.Add(restGap);
                gaps.Add(MathF.Min(minGap, restGap));
                return true;
            }

            return new LayerContacts(outer.ToArray(), inner.ToArray(), gaps.ToArray(), restGaps.ToArray(), hasInner, ReachFactor * spacing);
        }

        static int Nearest(List<int> candidates, Vector3[] rest, Vector3 center, float radius, Func<int, bool> accept)
        {
            int best = -1;
            float bestSq = radius * radius;
            for (int c = 0; c < candidates.Count; c++)
            {
                int j = candidates[c];
                if (!accept(j)) continue;
                float sq = Vector3.DistanceSquared(rest[j], center);
                if (sq <= bestSq)
                {
                    bestSq = sq;
                    best = j;
                }
            }
            return best;
        }

        // 島 outerIsland が島 innerIsland より外側か（outer から inner へのレイの組が、逆向きより多い）
        static bool IsOutside(Dictionary<long, int> order, int islandCount, int outerIsland, int innerIsland)
        {
            order.TryGetValue((long)outerIsland * islandCount + innerIsland, out int outward);
            order.TryGetValue((long)innerIsland * islandCount + outerIsland, out int inward);
            return outward > inward;
        }
    }
}
