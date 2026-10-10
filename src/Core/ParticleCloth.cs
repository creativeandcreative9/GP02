using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// メッシュの表面を一定間隔の粒子に置き換えた布。
    /// 計算は粒子で行い、メッシュの頂点は近くの粒子の動きに合わせて動かす。
    /// そのため、メッシュの細かさに関係なく、同じ間隔・同じ手触りで編集できる。
    /// </summary>
    public sealed class ParticleCloth
    {
        /// <summary>粒子の間隔（メートル）の既定値。アバターの実寸で1cm。</summary>
        public const float DefaultSpacing = 0.01f;

        // 伸びの制約・曲げの制約・頂点の結びつけで使う距離（粒子の間隔に対する倍率）
        const float StretchRange = 2f;
        const float BendRange = 2.6f;
        const float BindRange = 2f;

        public MeshTopology Topology { get; }
        public ClothModel Model { get; }
        public float Spacing { get; }

        /// <summary>粒子ごとの、乗っている三角形。</summary>
        public int[] ParticleTriangle { get; }

        // まとめた後の頂点ごとに、結びつけた粒子と重み（合計1）。CSR形式
        readonly int[] _bindOffsets;
        readonly int[] _bindParticles;
        readonly float[] _bindWeights;

        ParticleCloth(MeshTopology topology, ClothModel model, float spacing, int[] particleTriangle,
            int[] bindOffsets, int[] bindParticles, float[] bindWeights)
        {
            Topology = topology;
            Model = model;
            Spacing = spacing;
            ParticleTriangle = particleTriangle;
            _bindOffsets = bindOffsets;
            _bindParticles = bindParticles;
            _bindWeights = bindWeights;
        }

        public static ParticleCloth Build(Vector3[] vertices, int[] triangles, float spacing = DefaultSpacing, uint seed = 1)
        {
            var topology = MeshTopology.Build(vertices, triangles);
            Vector3[] particles = ParticleSampler.Sample(topology, spacing, seed, out int[] particleTriangle);

            var grid = new PointGrid(BendRange * spacing);
            for (int i = 0; i < particles.Length; i++) grid.Add(i, particles[i]);
            var search = new SurfaceSearch(topology);
            var nearby = new List<int>();

            // 粒子どうしを、表面に沿ってつながっているものだけ結ぶ。
            // 伸びの制約（固い）は、本当に隣り合う粒子だけにする。隣の隣まで固くつなぐと布が曲がれなくなり、
            // 曲げたときに制約どうしが矛盾して伸びが残る。隣り合うかどうかはガブリエルグラフで判定する
            // （2つの粒子を直径とする球の中に、別の粒子がなければ隣り合う）。それ以外は曲げの制約（柔らかい）にする。
            var stretchPairs = new List<int>();
            var bendPairs = new List<int>();
            var connected = new List<int>();
            float stretchRange = StretchRange * spacing;
            float bendRange = BendRange * spacing;
            for (int p = 0; p < particles.Length; p++)
            {
                search.Run(particleTriangle[p], particles[p], bendRange);
                grid.Query(particles[p], bendRange, nearby);
                connected.Clear();
                foreach (int q in nearby)
                {
                    if (q == p || !search.Contains(particleTriangle[q])) continue;
                    if (Vector3.Distance(particles[p], particles[q]) <= bendRange) connected.Add(q);
                }

                foreach (int q in connected)
                {
                    if (q < p) continue;
                    float distance = Vector3.Distance(particles[p], particles[q]);
                    bool neighbor = distance <= stretchRange && IsGabrielEdge(particles, p, q, connected);
                    var pairs = neighbor ? stretchPairs : bendPairs;
                    pairs.Add(p);
                    pairs.Add(q);
                }
            }

            // メッシュの頂点を、表面に沿って近い粒子に結びつける
            var offsets = new int[topology.Positions.Length + 1];
            var bindParticles = new List<int>();
            var bindWeights = new List<float>();
            float bindRange = BindRange * spacing;
            for (int v = 0; v < topology.Positions.Length; v++)
            {
                Vector3 position = topology.Positions[v];
                int start = bindParticles.Count;
                int triangle = topology.VertexTriangle[v];
                if (triangle >= 0)
                {
                    search.Run(triangle, position, bindRange);
                    grid.Query(position, bindRange, nearby);
                    float total = 0f;
                    foreach (int q in nearby)
                    {
                        if (!search.Contains(particleTriangle[q])) continue;
                        float t = Vector3.Distance(position, particles[q]) / bindRange;
                        if (t >= 1f) continue;
                        float s = 1f - t * t;
                        float weight = s * s;
                        bindParticles.Add(q);
                        bindWeights.Add(weight);
                        total += weight;
                    }

                    if (total > 0f)
                    {
                        for (int k = start; k < bindWeights.Count; k++) bindWeights[k] /= total;
                    }
                    else
                    {
                        // 近くに粒子がない（ごく小さな部品など）ときは、同じ島で一番近い粒子についていく
                        int nearest = NearestInIsland(topology, particles, particleTriangle, position, topology.TriangleIsland[triangle]);
                        bindParticles.Add(nearest);
                        bindWeights.Add(1f);
                    }
                }
                offsets[v + 1] = bindParticles.Count;
            }

            var model = new ClothModel(particles, stretchPairs.ToArray(), bendPairs.ToArray());
            return new ParticleCloth(topology, model, spacing, particleTriangle, offsets, bindParticles.ToArray(), bindWeights.ToArray());
        }

        /// <summary>
        /// 粒子の位置から、まとめた後の頂点の位置を求める。
        /// 頂点は、結びつけた粒子の移動量の重み付き平均だけ動く。
        /// </summary>
        public void DeformWelded(ReadOnlySpan<Vector3> particlePositions, Vector3[] weldedPositions)
        {
            if (particlePositions.Length != Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particlePositions));
            if (weldedPositions == null || weldedPositions.Length != Topology.Positions.Length) throw new ArgumentException("頂点数が一致しません。", nameof(weldedPositions));

            Vector3[] rest = Model.RestPositions;
            for (int v = 0; v < weldedPositions.Length; v++)
            {
                Vector3 delta = Vector3.Zero;
                for (int k = _bindOffsets[v]; k < _bindOffsets[v + 1]; k++)
                {
                    int p = _bindParticles[k];
                    delta += (particlePositions[p] - rest[p]) * _bindWeights[k];
                }
                weldedPositions[v] = Topology.Positions[v] + delta;
            }
        }

        /// <summary>頂点ごとに結びつけた粒子の数（確認用）。</summary>
        public int BoundParticleCount(int weldedVertex) => _bindOffsets[weldedVertex + 1] - _bindOffsets[weldedVertex];

        // p と q を直径とする球の中に、ほかの粒子がないか。
        // 球の中の粒子は p から |pq| 以内にあるので、p の周りの粒子（candidates）だけ調べれば足りる
        static bool IsGabrielEdge(Vector3[] particles, int p, int q, List<int> candidates)
        {
            Vector3 middle = (particles[p] + particles[q]) * 0.5f;
            float radiusSq = Vector3.DistanceSquared(particles[p], particles[q]) * 0.25f;
            foreach (int r in candidates)
            {
                if (r == q) continue;
                if (Vector3.DistanceSquared(particles[r], middle) < radiusSq) return false;
            }
            return true;
        }

        static int NearestInIsland(MeshTopology topology, Vector3[] particles, int[] particleTriangle, Vector3 position, int island)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < particles.Length; i++)
            {
                if (topology.TriangleIsland[particleTriangle[i]] != island) continue;
                float sq = Vector3.DistanceSquared(particles[i], position);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }
    }
}
