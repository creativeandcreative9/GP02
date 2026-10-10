using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// メッシュから作った、粒子と制約の組み立て情報。計算中に変化しない。
    /// </summary>
    public sealed class ClothModel
    {
        /// <summary>頂点を同一とみなす距離（メートル）の既定値。</summary>
        public const float DefaultWeldTolerance = 1e-6f;

        public int ParticleCount => RestPositions.Length;

        /// <summary>粒子の元の位置。</summary>
        public Vector3[] RestPositions { get; }

        /// <summary>元メッシュの頂点ごとの粒子番号。</summary>
        public int[] VertexToParticle { get; }

        /// <summary>粒子番号で表した三角形（3つずつ）。潰れた三角形は除いてある。</summary>
        public int[] Triangles { get; }

        /// <summary>伸びを防ぐ制約の粒子ペア（2つずつ）。メッシュの辺に対応する。</summary>
        public int[] StretchPairs { get; }
        public float[] StretchRestLengths { get; }

        /// <summary>
        /// 曲げを抑える制約の粒子ペア（2つずつ）。
        /// 辺を共有する2つの三角形の、向かい合う頂点どうしを結ぶ。
        /// </summary>
        public int[] BendPairs { get; }
        public float[] BendRestLengths { get; }

        ClothModel(Vector3[] restPositions, int[] vertexToParticle, int[] triangles,
            int[] stretchPairs, float[] stretchRestLengths, int[] bendPairs, float[] bendRestLengths)
        {
            RestPositions = restPositions;
            VertexToParticle = vertexToParticle;
            Triangles = triangles;
            StretchPairs = stretchPairs;
            StretchRestLengths = stretchRestLengths;
            BendPairs = bendPairs;
            BendRestLengths = bendRestLengths;
        }

        public static ClothModel Build(Vector3[] vertices, int[] triangles, float weldTolerance = DefaultWeldTolerance)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (triangles.Length % 3 != 0) throw new ArgumentException("三角形の頂点数が3の倍数ではありません。", nameof(triangles));

            int[] vertexToParticle = VertexWelder.Weld(vertices, weldTolerance, out var rest);

            var particleTriangles = new List<int>(triangles.Length);
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = vertexToParticle[triangles[t]];
                int b = vertexToParticle[triangles[t + 1]];
                int c = vertexToParticle[triangles[t + 2]];
                if (a == b || b == c || c == a) continue;
                particleTriangles.Add(a);
                particleTriangles.Add(b);
                particleTriangles.Add(c);
            }

            // 辺 → その辺を持つ三角形の、向かいの頂点
            var edgeOpposite = new Dictionary<long, int>(PairKeyComparer.Instance);
            var stretchPairs = new List<int>();
            var bendPairs = new List<int>();
            var bendKeys = new HashSet<long>(PairKeyComparer.Instance);

            for (int t = 0; t < particleTriangles.Count; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int i = particleTriangles[t + e];
                    int j = particleTriangles[t + (e + 1) % 3];
                    int opposite = particleTriangles[t + (e + 2) % 3];
                    long key = PairKey(i, j);

                    if (edgeOpposite.TryGetValue(key, out int otherOpposite))
                    {
                        if (otherOpposite != opposite && bendKeys.Add(PairKey(opposite, otherOpposite)))
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
                if (edgeOpposite.ContainsKey(PairKey(bendPairs[k], bendPairs[k + 1]))) continue;
                filteredBend.Add(bendPairs[k]);
                filteredBend.Add(bendPairs[k + 1]);
            }

            return new ClothModel(
                rest,
                vertexToParticle,
                particleTriangles.ToArray(),
                stretchPairs.ToArray(),
                RestLengths(rest, stretchPairs),
                filteredBend.ToArray(),
                RestLengths(rest, filteredBend));
        }

        static float[] RestLengths(Vector3[] positions, List<int> pairs)
        {
            var lengths = new float[pairs.Count / 2];
            for (int k = 0; k < lengths.Length; k++)
            {
                lengths[k] = Vector3.Distance(positions[pairs[2 * k]], positions[pairs[2 * k + 1]]);
            }
            return lengths;
        }

        // long の標準のハッシュは上位と下位の XOR なので、(a, b) の組では衝突が多く、
        // 頂点数が増えると辞書が極端に遅くなる。値をかき混ぜてから使う。
        sealed class PairKeyComparer : IEqualityComparer<long>
        {
            public static readonly PairKeyComparer Instance = new PairKeyComparer();

            public bool Equals(long x, long y) => x == y;

            public int GetHashCode(long key)
            {
                ulong z = (ulong)key + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return (int)(z ^ (z >> 31));
            }
        }

        static long PairKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }
    }
}
