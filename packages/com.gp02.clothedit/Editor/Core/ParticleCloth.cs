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

        /// <summary>粗い段の数の既定値（1cm の粒子に対して 2cm・4cm の段を作る）。</summary>
        public const int DefaultCoarseLevels = 2;

        // 伸びの制約・曲げの制約・結びつけで使う距離（粒子の間隔に対する倍率）
        const float StretchRange = 2f;
        const float BendRange = 2.6f;
        const float BindRange = 2f;

        public MeshTopology Topology { get; }
        public ClothModel Model { get; }
        public float Spacing { get; }

        /// <summary>粗い段の粒子。ClothSolver に渡すと、粗い段から順に解く。</summary>
        public ClothHierarchy Hierarchy { get; }

        /// <summary>粒子ごとの、乗っている三角形。</summary>
        public int[] ParticleTriangle { get; }

        /// <summary>粒子ごとの、乗っている島（<see cref="MeshTopology.TriangleIsland"/>）。重ね着で層を区別するのに使う。</summary>
        public int[] ParticleIsland { get; }

        // まとめた後の頂点ごとに、結びつけた粒子と重み（合計1）。CSR形式
        readonly int[] _bindOffsets;
        readonly int[] _bindParticles;
        readonly float[] _bindWeights;

        ParticleCloth(MeshTopology topology, ClothModel model, ClothHierarchy hierarchy, float spacing, int[] particleTriangle,
            int[] bindOffsets, int[] bindParticles, float[] bindWeights)
        {
            Topology = topology;
            Model = model;
            Hierarchy = hierarchy;
            Spacing = spacing;
            ParticleTriangle = particleTriangle;
            ParticleIsland = new int[particleTriangle.Length];
            for (int i = 0; i < particleTriangle.Length; i++) ParticleIsland[i] = topology.TriangleIsland[particleTriangle[i]];
            _bindOffsets = bindOffsets;
            _bindParticles = bindParticles;
            _bindWeights = bindWeights;
        }

        public static ParticleCloth Build(Vector3[] vertices, int[] triangles, float spacing = DefaultSpacing,
            uint seed = 1, int coarseLevels = DefaultCoarseLevels)
        {
            if (coarseLevels < 0) throw new ArgumentOutOfRangeException(nameof(coarseLevels));

            var topology = MeshTopology.Build(vertices, triangles);
            Vector3[] particles = ParticleSampler.Sample(topology, spacing, seed, out int[] particleTriangle);
            SortSpatially(particles, particleTriangle, spacing);
            var search = new SurfaceSearch(topology);
            var all = new int[particles.Length];
            for (int i = 0; i < all.Length; i++) all[i] = i;

            var stretchPairs = new List<int>();
            var bendPairs = new List<int>();
            ParticleNetwork.Connect(search, particles, particleTriangle, all, StretchRange * spacing, BendRange * spacing, stretchPairs, bendPairs);

            // メッシュの頂点を、表面に沿って近い粒子に結びつける
            ParticleNetwork.Bind(topology, search, topology.Positions, topology.VertexTriangle,
                particles, particleTriangle, all, BindRange * spacing,
                out int[] offsets, out int[] bindParticles, out float[] bindWeights);

            var hierarchy = ClothHierarchy.Build(topology, search, particles, particleTriangle, spacing, coarseLevels, StretchRange, BindRange);

            var model = new ClothModel(particles, stretchPairs.ToArray(), bendPairs.ToArray());
            return new ParticleCloth(topology, model, hierarchy, spacing, particleTriangle, offsets, bindParticles, bindWeights);
        }

        /// <summary>
        /// 空間的に近い粒子が近い番号になるよう、Z階数曲線（Morton順）で並べ替える。
        /// サンプリング直後の番号はほぼランダムで、制約を解くときのメモリの読み書きが飛び回り、
        /// 粒子が1万を超えるとキャッシュに収まらず急に遅くなるため。
        /// </summary>
        static void SortSpatially(Vector3[] particles, int[] particleTriangle, float spacing)
        {
            int n = particles.Length;
            if (n < 2) return;
            Vector3 min = particles[0];
            for (int i = 1; i < n; i++) min = Vector3.Min(min, particles[i]);

            var keys = new ulong[n];
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 cell = (particles[i] - min) / spacing;
                keys[i] = Spread((uint)cell.X) | (Spread((uint)cell.Y) << 1) | (Spread((uint)cell.Z) << 2);
                order[i] = i;
            }
            Array.Sort(keys, order);

            var sortedParticles = new Vector3[n];
            var sortedTriangles = new int[n];
            for (int i = 0; i < n; i++)
            {
                sortedParticles[i] = particles[order[i]];
                sortedTriangles[i] = particleTriangle[order[i]];
            }
            Array.Copy(sortedParticles, particles, n);
            Array.Copy(sortedTriangles, particleTriangle, n);
        }

        // 下位21ビットを、3ビットおきに並べる（Morton符号の1軸分）。21ビットあれば 1cm 間隔で約20km まで表せる
        static ulong Spread(uint value)
        {
            ulong x = value & 0x1FFFFF;
            x = (x | (x << 32)) & 0x1F00000000FFFF;
            x = (x | (x << 16)) & 0x1F0000FF0000FF;
            x = (x | (x << 8)) & 0x100F00F00F00F00F;
            x = (x | (x << 4)) & 0x10C30C30C30C30C3;
            x = (x | (x << 2)) & 0x1249249249249249;
            return x;
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

        /// <summary>この布用のソルバーを作る（粗い段も使う）。</summary>
        public ClothSolver CreateSolver() => new ClothSolver(Model, Hierarchy);
    }
}
