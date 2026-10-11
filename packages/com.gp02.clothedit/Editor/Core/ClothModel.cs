using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 粒子と、粒子どうしをつなぐ制約の組み立て情報。計算中に変化しない。
    /// </summary>
    public sealed class ClothModel
    {
        public int ParticleCount => RestPositions.Length;

        /// <summary>粒子の元の位置。</summary>
        public Vector3[] RestPositions { get; }

        /// <summary>伸びを防ぐ制約の粒子ペア（2つずつ）。</summary>
        public int[] StretchPairs { get; }
        public float[] StretchRestLengths { get; }

        /// <summary>曲げを抑える制約の粒子ペア（2つずつ）。伸びの制約より離れた粒子どうしを結ぶ。</summary>
        public int[] BendPairs { get; }
        public float[] BendRestLengths { get; }

        public ClothModel(Vector3[] restPositions, int[] stretchPairs, int[] bendPairs)
        {
            RestPositions = restPositions ?? throw new ArgumentNullException(nameof(restPositions));
            StretchPairs = stretchPairs ?? throw new ArgumentNullException(nameof(stretchPairs));
            BendPairs = bendPairs ?? throw new ArgumentNullException(nameof(bendPairs));
            if (stretchPairs.Length % 2 != 0) throw new ArgumentException("ペアの要素数が偶数ではありません。", nameof(stretchPairs));
            if (bendPairs.Length % 2 != 0) throw new ArgumentException("ペアの要素数が偶数ではありません。", nameof(bendPairs));
            StretchRestLengths = RestLengths(restPositions, stretchPairs);
            BendRestLengths = RestLengths(restPositions, bendPairs);
        }

        /// <summary>
        /// 三角形メッシュの頂点をそのまま粒子にする。伸びの制約はメッシュの辺、
        /// 曲げの制約は辺を共有する2つの三角形の向かい合う頂点どうし。
        /// 粒子番号は <see cref="MeshTopology.VertexToWelded"/> と同じ。
        /// </summary>
        public static ClothModel FromTriangleMesh(MeshTopology topology)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            int[] triangles = topology.Triangles;

            // 辺 → その辺を持つ三角形の、向かいの頂点
            var edgeOpposite = new Dictionary<long, int>(PairKeys.Comparer);
            var stretchPairs = new List<int>();
            var bendPairs = new List<int>();
            var bendKeys = new HashSet<long>(PairKeys.Comparer);

            for (int t = 0; t < triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int i = triangles[t + e];
                    int j = triangles[t + (e + 1) % 3];
                    int opposite = triangles[t + (e + 2) % 3];
                    long key = PairKeys.Make(i, j);

                    if (edgeOpposite.TryGetValue(key, out int otherOpposite))
                    {
                        if (otherOpposite != opposite && bendKeys.Add(PairKeys.Make(opposite, otherOpposite)))
                        {
                            bendPairs.Add(opposite);
                            bendPairs.Add(otherOpposite);
                        }
                    }
                    else
                    {
                        edgeOpposite.Add(key, opposite);
                        stretchPairs.Add(i);
                        stretchPairs.Add(j);
                    }
                }
            }

            // 曲げのペアがたまたまメッシュの辺と同じなら、伸びの制約と重複するので除く
            var filteredBend = new List<int>(bendPairs.Count);
            for (int k = 0; k < bendPairs.Count; k += 2)
            {
                if (edgeOpposite.ContainsKey(PairKeys.Make(bendPairs[k], bendPairs[k + 1]))) continue;
                filteredBend.Add(bendPairs[k]);
                filteredBend.Add(bendPairs[k + 1]);
            }

            return new ClothModel((Vector3[])topology.Positions.Clone(), stretchPairs.ToArray(), filteredBend.ToArray());
        }

        static float[] RestLengths(Vector3[] positions, int[] pairs)
        {
            var lengths = new float[pairs.Length / 2];
            for (int k = 0; k < lengths.Length; k++)
            {
                // Rebase でも呼ばれるので、Mono で遅い Vector3 の関数は使わず成分ごとに計算する
                Vector3 a = positions[pairs[2 * k]], b = positions[pairs[2 * k + 1]];
                float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                lengths[k] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return lengths;
        }
    }
}
