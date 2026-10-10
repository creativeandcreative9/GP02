using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// メッシュの表面に、ほぼ一定の間隔で粒子をばらまく（ポアソンディスク・サンプリング）。
    /// 同じ入力と同じシードなら、どの環境でも同じ結果になる。
    /// </summary>
    public static class ParticleSampler
    {
        // 間隔の2乗あたりの候補点の数。多いほど隙間ができにくいが、遅くなる
        const float CandidatesPerSpacingSquared = 24f;

        /// <param name="topology">対象のメッシュ</param>
        /// <param name="spacing">粒子どうしの最小の間隔（メートル）</param>
        /// <param name="seed">乱数のシード</param>
        /// <param name="particleTriangle">粒子ごとの、乗っている三角形（出力）</param>
        /// <returns>粒子の位置</returns>
        public static Vector3[] Sample(MeshTopology topology, float spacing, uint seed, out int[] particleTriangle)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (spacing <= 0f) throw new ArgumentOutOfRangeException(nameof(spacing));

            var random = new XorShift(seed);
            Vector3[] positions = topology.Positions;
            int[] triangles = topology.Triangles;

            // 1. 三角形ごとに、面積に応じた数の候補点を置く。小さい三角形にも最低1つ置くので、どの島にも粒子が乗る
            var candidates = new List<Vector3>();
            var candidateTriangle = new List<int>();
            float density = CandidatesPerSpacingSquared / (spacing * spacing);
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                float area = 0.5f * Vector3.Cross(b - a, c - a).Length();
                int count = Math.Max(1, (int)MathF.Ceiling(area * density));
                for (int k = 0; k < count; k++)
                {
                    // 三角形の中に一様に置く
                    float r1 = MathF.Sqrt(random.NextFloat());
                    float r2 = random.NextFloat();
                    candidates.Add(a * (1f - r1) + b * (r1 * (1f - r2)) + c * (r1 * r2));
                    candidateTriangle.Add(t);
                }
            }

            // 2. 候補をランダムな順に並べ、すでに選んだ粒子から spacing 以上離れていれば採用する。
            //    別の島の粒子は、近くても気にしない（重ね着の層がそれぞれ粒子を持てるように）
            var order = new int[candidates.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = (int)(random.NextUInt() % (uint)(i + 1));
                (order[i], order[j]) = (order[j], order[i]);
            }

            int[] island = topology.TriangleIsland;
            var grid = new PointGrid(spacing);
            var nearby = new List<int>();
            var accepted = new List<Vector3>();
            var acceptedTriangle = new List<int>();
            float spacingSq = spacing * spacing;

            foreach (int candidate in order)
            {
                Vector3 p = candidates[candidate];
                int candidateIsland = island[candidateTriangle[candidate]];
                grid.Query(p, spacing, nearby);
                bool tooClose = false;
                foreach (int other in nearby)
                {
                    if (island[acceptedTriangle[other]] != candidateIsland) continue;
                    if (Vector3.DistanceSquared(accepted[other], p) < spacingSq)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                grid.Add(accepted.Count, p);
                accepted.Add(p);
                acceptedTriangle.Add(candidateTriangle[candidate]);
            }

            particleTriangle = acceptedTriangle.ToArray();
            return accepted.ToArray();
        }

        /// <summary>環境によらず同じ結果を出す、小さな乱数生成器。</summary>
        struct XorShift
        {
            uint _state;

            public XorShift(uint seed)
            {
                _state = seed == 0 ? 0x9E3779B9u : seed;
            }

            public uint NextUInt()
            {
                uint x = _state;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _state = x;
                return x;
            }

            // [0, 1)
            public float NextFloat() => (NextUInt() >> 8) * (1f / (1 << 24));
        }
    }
}
