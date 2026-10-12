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

        /// <summary>
        /// まとめた後の頂点の位置 targetWelded から粒子の位置を作り、<see cref="DeformWelded"/> で頂点に戻したときに
        /// targetWelded に近くなるよう直す（<see cref="ParticlePositionsFromWelded"/> の改良版）。
        /// 重心座標の補間だけだと、頂点は近くの粒子の重み付き平均で動くので、頂点 → 粒子 → 頂点の往復で形が少しなまる（1〜3mm）。
        /// 1回の反復では、(1) 頂点ごとの残りの差を、頂点に結びつけた粒子へ結びつけの重みで割り戻し（結びつけの転置なので、頂点に見えない粒子のでこぼこは足さない）、
        /// (2) 伸びの制約を半分の強さで1回ずつ静止状態の長さへ寄せる。(2) が無いと、頂点には合っても、頂点の間の粒子が引っ張られて伸びが増えるため。
        /// (2) を入れた結果が下の条件に合わないとき（形そのものが伸びていて、寄せると頂点から離れるときなど）は、(1) だけの結果を試す。
        /// 反復は、頂点の差の最大が減り、かつ粒子の最大の伸びが「補間だけの粒子」「頂点の形そのもの（メッシュの辺）」「<see cref="FitStretchAllowance"/>」の一番大きいものを超えないときだけ採り、
        /// そうでなければそこで止める（頂点が粒子より粗くて合わせきれない所や、布が大きく伸びた形で、粒子を引っ張りすぎないように）。
        /// </summary>
        /// <param name="iterations">反復の上限（0 なら <see cref="ParticlePositionsFromWelded"/> と同じ）</param>
        /// <param name="iterationsDone">採った反復の回数</param>
        /// <returns>結果の粒子の位置での、頂点の位置の差の最大（メートル）</returns>
        public float FitParticlesToWelded(ReadOnlySpan<Vector3> targetWelded, Vector3[] particlePositions, int iterations, out int iterationsDone)
        {
            if (iterations < 0) throw new ArgumentOutOfRangeException(nameof(iterations));
            ParticlePositionsFromWelded(targetWelded, particlePositions);
            iterationsDone = 0;

            int weldedCount = Topology.Positions.Length;
            int particleCount = particlePositions.Length;
            var welded = new Vector3[weldedCount];
            DeformWelded(particlePositions, welded);
            float residual = MaxDistance(targetWelded, welded);
            if (iterations == 0 || residual == 0f) return residual;

            float stretchLimit = MathF.Max(FitStretchAllowance, MathF.Max(MeasureMaxStretch(particlePositions), MeasureMaxEdgeStretch(targetWelded)));
            var candidate = new Vector3[particleCount];
            var candidateWelded = new Vector3[weldedCount];
            var sumX = new float[particleCount];
            var sumY = new float[particleCount];
            var sumZ = new float[particleCount];
            var weightSum = new float[particleCount];
            for (int k = 0; k < iterations; k++)
            {
                Array.Clear(sumX, 0, particleCount);
                Array.Clear(sumY, 0, particleCount);
                Array.Clear(sumZ, 0, particleCount);
                Array.Clear(weightSum, 0, particleCount);
                for (int w = 0; w < weldedCount; w++)
                {
                    float dx = targetWelded[w].X - welded[w].X, dy = targetWelded[w].Y - welded[w].Y, dz = targetWelded[w].Z - welded[w].Z;
                    for (int b = _bindOffsets[w]; b < _bindOffsets[w + 1]; b++)
                    {
                        int i = _bindParticles[b];
                        float weight = _bindWeights[b];
                        sumX[i] += weight * dx;
                        sumY[i] += weight * dy;
                        sumZ[i] += weight * dz;
                        weightSum[i] += weight;
                    }
                }
                // 伸びを寄せた候補を先に試し、採れなければ（形そのものが伸びていて、寄せると頂点から離れるときなど）、寄せない候補を試す
                bool accepted = false;
                for (int attempt = 0; attempt < 2 && !accepted; attempt++)
                {
                    for (int i = 0; i < particleCount; i++)
                    {
                        Vector3 p = particlePositions[i];
                        float s = weightSum[i];
                        candidate[i] = s > 0f ? new Vector3(p.X + FitStep * sumX[i] / s, p.Y + FitStep * sumY[i] / s, p.Z + FitStep * sumZ[i] / s) : p;
                    }
                    if (attempt == 0) RelaxStretch(candidate, FitRelaxation);

                    DeformWelded(candidate, candidateWelded);
                    float next = MaxDistance(targetWelded, candidateWelded);
                    if (next >= residual * FitRequiredProgress || MeasureMaxStretch(candidate) > stretchLimit) continue;
                    Array.Copy(candidate, particlePositions, particleCount);
                    Array.Copy(candidateWelded, welded, weldedCount);
                    residual = next;
                    accepted = true;
                }
                if (!accepted) break;
                iterationsDone++;
            }
            return residual;
        }

        /// <summary>
        /// <see cref="FitParticlesToWelded"/> で、粒子の最大の伸びがこれ（0.02 なら 2%）以下なら、補間だけより増えても反復を採る。
        /// 伸びの小さい形（外へ逃がした形など）は、補間だけだとなまって伸びがさらに小さく見積もられ、頂点に合わせるわずかな伸びも採れなくなるため。
        /// 仕上げの許容（既定 1%）の 2 倍で、次に触ったときの仕上げで戻る程度の伸び
        /// </summary>
        public const float FitStretchAllowance = 0.02f;

        // FitParticlesToWelded の、頂点の差を割り戻す歩幅（1 より少し大きいと収束が速い。2 未満なら差は増えない）
        const float FitStep = 1.5f;
        // FitParticlesToWelded の、伸びの制約を静止状態の長さへ寄せる強さ（1 で一度に寄せきる）
        const float FitRelaxation = 0.5f;
        // FitParticlesToWelded で、反復を採るのに必要な頂点の差の減り方（前の 98% 未満になること）
        const float FitRequiredProgress = 0.98f;

        // 伸びの制約を、静止状態の長さへ strength の割合だけ寄せる（制約の順に1回。ガウス・ザイデル）
        void RelaxStretch(Vector3[] positions, float strength)
        {
            int[] pairs = Model.StretchPairs;
            float[] rest = Model.StretchRestLengths;
            for (int k = 0; k < rest.Length; k++)
            {
                int a = pairs[2 * k], b = pairs[2 * k + 1];
                Vector3 pa = positions[a], pb = positions[b];
                float dx = pb.X - pa.X, dy = pb.Y - pa.Y, dz = pb.Z - pa.Z;
                float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length < 1e-9f) continue;
                float c = 0.5f * strength * (length - rest[k]) / length;
                positions[a] = new Vector3(pa.X + c * dx, pa.Y + c * dy, pa.Z + c * dz);
                positions[b] = new Vector3(pb.X - c * dx, pb.Y - c * dy, pb.Z - c * dz);
            }
        }

        /// <summary>
        /// 粒子の位置での、伸びの制約の最大の伸び（0.1 なら 10%。縮みは 0 とみなす）。
        /// ソルバーの <see cref="ClothSolver.MeasureMaxStretch"/> と同じ測り方を、布全体で行う。
        /// </summary>
        public float MeasureMaxStretch(ReadOnlySpan<Vector3> particlePositions)
        {
            if (particlePositions.Length != Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particlePositions));
            int[] pairs = Model.StretchPairs;
            float[] rest = Model.StretchRestLengths;
            float max = 0f;
            for (int k = 0; k < rest.Length; k++)
            {
                Vector3 a = particlePositions[pairs[2 * k]], b = particlePositions[pairs[2 * k + 1]];
                float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                max = MathF.Max(max, MathF.Sqrt(dx * dx + dy * dy + dz * dz) / rest[k] - 1f);
            }
            return max;
        }

        /// <summary>
        /// まとめた後の頂点の位置での、メッシュの辺の最大の伸び（静止状態の辺の長さに対する割合、0.1 なら 10%。縮みは 0 とみなす）。
        /// 頂点の形そのものが、布をどれだけ伸ばしているかの目安。
        /// </summary>
        public float MeasureMaxEdgeStretch(ReadOnlySpan<Vector3> weldedPositions)
        {
            if (weldedPositions.Length != Topology.Positions.Length) throw new ArgumentException("頂点数が一致しません。", nameof(weldedPositions));
            int[] triangles = Topology.Triangles;
            Vector3[] rest = Topology.Positions;
            float max = 0f;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[t + e], b = triangles[t + (e + 1) % 3];
                    float rx = rest[a].X - rest[b].X, ry = rest[a].Y - rest[b].Y, rz = rest[a].Z - rest[b].Z;
                    float restLength = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                    if (restLength < 1e-6f) continue;
                    float dx = weldedPositions[a].X - weldedPositions[b].X, dy = weldedPositions[a].Y - weldedPositions[b].Y, dz = weldedPositions[a].Z - weldedPositions[b].Z;
                    max = MathF.Max(max, MathF.Sqrt(dx * dx + dy * dy + dz * dz) / restLength - 1f);
                }
            }
            return max;
        }

        static float MaxDistance(ReadOnlySpan<Vector3> a, Vector3[] b)
        {
            float max = 0f;
            for (int i = 0; i < b.Length; i++)
            {
                float dx = a[i].X - b[i].X, dy = a[i].Y - b[i].Y, dz = a[i].Z - b[i].Z;
                max = MathF.Max(max, dx * dx + dy * dy + dz * dz);
            }
            return MathF.Sqrt(max);
        }

        /// <summary>
        /// other が、この布と同じ粒子（数・乗っている三角形・静止状態の位置が tolerance メートル以内）を持つか。
        /// 同じメッシュから同じ位置で作り直した布なら同じになる。粒子の位置の記録（Undo など）を、作り直した布にそのまま使えるかを確かめる。
        /// </summary>
        public bool HasSameParticles(ParticleCloth other, float tolerance)
        {
            if (other == null) return false;
            if (ReferenceEquals(other, this)) return true;
            int count = Model.ParticleCount;
            if (other.Model.ParticleCount != count) return false;
            Vector3[] mine = Model.RestPositions, theirs = other.Model.RestPositions;
            float toleranceSquared = tolerance * tolerance;
            for (int i = 0; i < count; i++)
            {
                if (ParticleTriangle[i] != other.ParticleTriangle[i]) return false;
                float dx = mine[i].X - theirs[i].X, dy = mine[i].Y - theirs[i].Y, dz = mine[i].Z - theirs[i].Z;
                if (dx * dx + dy * dy + dz * dz > toleranceSquared) return false;
            }
            return true;
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

        /// <summary>
        /// 粒子ごとの値（選んだ粒子を 1 とするなど）を、まとめた後の頂点ごとの値にする。
        /// 頂点は近くの粒子に結びついているので、結びついた粒子の値の重み付き平均（重みの合計は 1）になる。
        /// 選んだ点の範囲を、頂点ごとの 0〜1 の重み（マスク）にするのに使う。
        /// </summary>
        /// <param name="particleValues">粒子ごとの値。長さは粒子の数</param>
        /// <param name="weldedValues">まとめた後の頂点ごとの結果を入れる配列。長さは <see cref="MeshTopology.Positions"/> と同じ</param>
        public void AverageOverBinding(float[] particleValues, float[] weldedValues)
        {
            if (particleValues == null || particleValues.Length != Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particleValues));
            if (weldedValues == null || weldedValues.Length != Topology.Positions.Length) throw new ArgumentException("頂点数が一致しません。", nameof(weldedValues));

            for (int w = 0; w < weldedValues.Length; w++)
            {
                float sum = 0f, weightSum = 0f;
                for (int k = _bindOffsets[w]; k < _bindOffsets[w + 1]; k++)
                {
                    sum += particleValues[_bindParticles[k]] * _bindWeights[k];
                    weightSum += _bindWeights[k];
                }
                weldedValues[w] = weightSum > 0f ? sum / weightSum : 0f;
            }
        }

        /// <summary>頂点ごとに結びつけた粒子の数（確認用）。</summary>
        public int BoundParticleCount(int weldedVertex) => _bindOffsets[weldedVertex + 1] - _bindOffsets[weldedVertex];

        /// <summary>
        /// まとめた後の頂点 weldedVertex に結びつけた、k 番目（0 〜 <see cref="BoundParticleCount"/> - 1）の粒子と、その重み。
        /// 布の面のある点を動かすには、どの粒子を動かせばよいかを知るのに使う（体の尖った所が粒子の間から出ている所を押し出すとき）。
        /// </summary>
        public int GetBoundParticle(int weldedVertex, int k, out float weight)
        {
            int index = _bindOffsets[weldedVertex] + k;
            weight = _bindWeights[index];
            return _bindParticles[index];
        }

        /// <summary>この布用のソルバーを作る（粗い段も使う）。</summary>
        public ClothSolver CreateSolver() => new ClothSolver(Model, Hierarchy);
    }
}
