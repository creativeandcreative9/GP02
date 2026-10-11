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

        // 島の面の向きの平均の長さがこれ以上（向きがほぼそろっている）なら、平らな島とみなす（0.95 で約18度のばらつき）
        const float FlatIslandNormalAgreement = 0.95f;

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

        // 粒子ごとの、乗っている三角形の3頂点に対する重心座標（X, Y, Z が 1つ目・2つ目・3つ目の頂点の重み、合計1）。
        // 元の形で一度だけ計算し、Rebase でも引き継ぐ（粒子は三角形の上に乗ったまま、三角形と一緒に動く扱い）
        readonly Vector3[] _particleBarycentric;

        ParticleCloth(MeshTopology topology, ClothModel model, ClothHierarchy hierarchy, float spacing, int[] particleTriangle,
            int[] bindOffsets, int[] bindParticles, float[] bindWeights, Vector3[] particleBarycentric)
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
            _particleBarycentric = particleBarycentric;
        }

        /// <param name="vertexGroups">
        /// 頂点ごとの組の番号（複数のレンダラーをまとめた布では、レンダラーの番号）。同じ位置の頂点をまとめるのは同じ組の中だけにする。
        /// null なら全部を1つの組とみなす
        /// </param>
        public static ParticleCloth Build(Vector3[] vertices, int[] triangles, float spacing = DefaultSpacing,
            uint seed = 1, int coarseLevels = DefaultCoarseLevels, int[] vertexGroups = null)
        {
            if (coarseLevels < 0) throw new ArgumentOutOfRangeException(nameof(coarseLevels));

            var topology = MeshTopology.Build(vertices, triangles, MeshTopology.DefaultWeldTolerance, vertexGroups);
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

            var model = new ClothModel(particles, stretchPairs.ToArray(), bendPairs.ToArray(), ComputeRestNormals(topology, particles, particleTriangle));
            Vector3[] barycentric = ComputeBarycentric(topology, particles, particleTriangle);
            return new ParticleCloth(topology, model, hierarchy, spacing, particleTriangle, offsets, bindParticles, bindWeights, barycentric);
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
        /// <param name="kernel">計算を任せる部品（Burst など）。null なら C#（<see cref="ShapeKernels"/>）で計算する</param>
        public void DeformWelded(ReadOnlySpan<Vector3> particlePositions, Vector3[] weldedPositions, IShapeKernel kernel = null)
        {
            if (particlePositions.Length != Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particlePositions));
            if (weldedPositions == null || weldedPositions.Length != Topology.Positions.Length) throw new ArgumentException("頂点数が一致しません。", nameof(weldedPositions));

            var data = new WeldedDeformData(particlePositions, Model.RestPositions, Topology.Positions,
                _bindOffsets, _bindParticles, _bindWeights, weldedPositions);
            if (kernel != null) kernel.DeformWelded(data);
            else ShapeKernels.DeformWelded(data);
        }

        /// <summary>
        /// 今の粒子の位置を新しい静止状態とした、新しい布を返す（元は変更しない）。
        /// シェイプキーとして保存した後に、粒子を置き直さずに編集を続けるために使う。
        /// 粒子の数・並び・制約のペア・頂点との結びつけ・粗い段の構成は使い回し、
        /// 静止状態に関わる値（<see cref="Model"/> の長さ、<see cref="Topology"/> の位置、<see cref="Hierarchy"/> の長さ）だけを作り直す。
        /// </summary>
        public ParticleCloth Rebase(ReadOnlySpan<Vector3> particlePositions)
        {
            if (particlePositions.Length != Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particlePositions));

            Vector3[] particles = particlePositions.ToArray();
            var welded = new Vector3[Topology.Positions.Length];
            DeformWelded(particles, welded);

            MeshTopology topology = Topology.WithPositions(welded);
            var model = new ClothModel(particles, Model.StretchPairs, Model.BendPairs, ComputeRestNormals(topology, particles, ParticleTriangle));
            ClothHierarchy hierarchy = Hierarchy.WithRestPositions(particles);
            return new ParticleCloth(topology, model, hierarchy, Spacing, ParticleTriangle, _bindOffsets, _bindParticles, _bindWeights, _particleBarycentric);
        }

        /// <summary>
        /// まとめた後の頂点の位置から、粒子の位置を作る（<see cref="DeformWelded"/> の逆向き）。
        /// 粒子は三角形の上に乗っているので、元の形で求めておいた重心座標で、三角形の3頂点の位置から補間する。
        /// 既存のシェイプキーを取り込んで、布として続きを編集するのに使う。
        /// なめらかな変形なら、結果を <see cref="DeformWelded"/> に通すと元の頂点の位置にほぼ戻る。
        /// </summary>
        public void ParticlePositionsFromWelded(ReadOnlySpan<Vector3> weldedPositions, Vector3[] particlePositions)
        {
            if (weldedPositions.Length != Topology.Positions.Length) throw new ArgumentException("頂点数が一致しません。", nameof(weldedPositions));
            if (particlePositions == null || particlePositions.Length != Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particlePositions));

            int[] triangles = Topology.Triangles;
            // 毎フレーム回る処理ではないが、Mono で遅い Vector3 の演算子は避けて成分ごとに計算する
            for (int i = 0; i < particlePositions.Length; i++)
            {
                int t = ParticleTriangle[i];
                Vector3 a = weldedPositions[triangles[3 * t]], b = weldedPositions[triangles[3 * t + 1]], c = weldedPositions[triangles[3 * t + 2]];
                Vector3 w = _particleBarycentric[i];
                particlePositions[i] = new Vector3(
                    w.X * a.X + w.Y * b.X + w.Z * c.X,
                    w.X * a.Y + w.Y * b.Y + w.Z * c.Y,
                    w.X * a.Z + w.Y * b.Z + w.Z * c.Z);
            }
        }

        // 粒子を三角形に落とした重心座標。粒子は三角形の上に作ってあるので、落とす距離はほぼ 0
        static Vector3[] ComputeBarycentric(MeshTopology topology, Vector3[] particles, int[] particleTriangle)
        {
            int[] triangles = topology.Triangles;
            Vector3[] positions = topology.Positions;
            var result = new Vector3[particles.Length];
            for (int i = 0; i < particles.Length; i++)
            {
                int t = particleTriangle[i];
                Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                Vector3 v0 = b - a, v1 = c - a, v2 = particles[i] - a;
                float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
                float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
                float denominator = d00 * d11 - d01 * d01;
                if (denominator <= 1e-24f * (d00 + d11) * (d00 + d11))
                {
                    // 面積がほぼ 0 の三角形では、一番近い頂点にそのまま乗せる
                    float da = v2.LengthSquared(), db = (particles[i] - b).LengthSquared(), dc = (particles[i] - c).LengthSquared();
                    result[i] = da <= db && da <= dc ? new Vector3(1f, 0f, 0f) : db <= dc ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, 1f);
                    continue;
                }
                float v = (d11 * d20 - d01 * d21) / denominator;
                float w = (d00 * d21 - d01 * d20) / denominator;
                result[i] = new Vector3(1f - v - w, v, w);
            }
            return result;
        }

        /// <summary>
        /// 粒子ごとの、元の形の面の外向き。粒子が乗っている三角形の向き（頂点の並び）で決める。
        /// - 裏面用に同じ頂点で向きを逆にした三角形が重なっている場合は、どちらが外か分からないので 0 ベクトル（対象外）にする
        /// - 島の粒子の大半で、向きが島の重心から内側を指していれば、裏返しのメッシュとみなして島ごと反転する
        /// - 島がほぼ平ら（向きがほとんどそろっている）なら、包んでいるもの（体や内側の布）が無いので対象外にする。
        ///   平らな布の裏側へ引っ張る動きを妨げないため
        /// 面の向きだけで決めるのは、隣の粒子と向きをそろえるやり方だと、鋭い折り目（90度以上）で向きを取り違えるため。
        /// </summary>
        internal static Vector3[] ComputeRestNormals(MeshTopology topology, Vector3[] particles, int[] particleTriangle)
        {
            int[] triangles = topology.Triangles;
            Vector3[] positions = topology.Positions;

            // 同じ3頂点を持つ三角形（裏面用の重ね）を探す
            var twin = new bool[topology.TriangleCount];
            var seen = new Dictionary<(int, int, int), int>();
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                if (a > b) (a, b) = (b, a);
                if (b > c) (b, c) = (c, b);
                if (a > b) (a, b) = (b, a);
                var key = (a, b, c);
                if (seen.TryGetValue(key, out int other))
                {
                    twin[t] = true;
                    twin[other] = true;
                }
                else
                {
                    seen.Add(key, t);
                }
            }

            var normals = new Vector3[particles.Length];
            for (int i = 0; i < particles.Length; i++)
            {
                int t = particleTriangle[i];
                if (twin[t]) continue;
                Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                float length = n.Length();
                if (length > 0f) normals[i] = n / length;
            }

            // 島ごとに、向きが重心の外側を指しているかの多数決を取る
            int islandCount = topology.IslandCount;
            var centroid = new Vector3[islandCount];
            var count = new int[islandCount];
            for (int i = 0; i < particles.Length; i++)
            {
                int island = topology.TriangleIsland[particleTriangle[i]];
                centroid[island] += particles[i];
                count[island]++;
            }
            var vote = new int[islandCount];
            var voters = new int[islandCount];
            var normalSum = new Vector3[islandCount];
            for (int i = 0; i < particles.Length; i++)
            {
                if (normals[i] == Vector3.Zero) continue;
                int island = topology.TriangleIsland[particleTriangle[i]];
                float d = Vector3.Dot(normals[i], particles[i] - centroid[island] / count[island]);
                vote[island] += d >= 0f ? 1 : -1;
                voters[island]++;
                normalSum[island] += normals[i];
            }
            for (int i = 0; i < particles.Length; i++)
            {
                int island = topology.TriangleIsland[particleTriangle[i]];
                if (voters[island] == 0) continue;
                if (normalSum[island].Length() >= FlatIslandNormalAgreement * voters[island])
                {
                    normals[i] = Vector3.Zero;
                    continue;
                }
                // 重心による判定は形によって当てにならないので、はっきり内向きのときだけ反転する
                if (vote[island] < -voters[island] / 2) normals[i] = -normals[i];
            }
            return normals;
        }

        /// <summary>頂点ごとに結びつけた粒子の数（確認用）。</summary>
        public int BoundParticleCount(int weldedVertex) => _bindOffsets[weldedVertex + 1] - _bindOffsets[weldedVertex];

        /// <summary>この布用のソルバーを作る（粗い段も使う）。</summary>
        public ClothSolver CreateSolver() => new ClothSolver(Model, Hierarchy);
    }
}
