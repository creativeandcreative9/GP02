using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>貫通の検出と押し出しの設定。</summary>
    public sealed class PenetrationOptions
    {
        /// <summary>体から保つ余白（メートル）の既定値。</summary>
        public const float DefaultMargin = ClothSolver.DefaultBodyMargin;

        /// <summary>体の法線と布の外向きの内積の下限の既定値（約 72 度まで）。</summary>
        public const float DefaultMinAlignment = 0.3f;

        /// <summary>体から保つ余白（メートル）。これより体に近い粒子も、押し出す対象にする。</summary>
        public float Margin = DefaultMargin;

        /// <summary>体を探す距離（メートル）。閉じていない体の、穴の向こうを中と取り違えないため、これより遠い所は見ない。</summary>
        public float MaxDistance = BodySurface.DefaultMaxDistance;

        /// <summary>
        /// 体の法線と布の外向きの内積がこれより小さい所は見ない。腕と脇の間などで、反対側の面（布の外向きと逆を向いた体の面）を拾って、
        /// 布を逆向きへ押し出さないため。
        /// </summary>
        public float MinAlignment = DefaultMinAlignment;

        /// <summary>体の頂点が布の外へ出ているか（体の尖った所が粒子の間を抜けていないか）も調べる。</summary>
        public bool CheckBodyVertices = true;

        /// <summary>表示用の点（<see cref="PenetrationReport.PointParticles"/> と <see cref="PenetrationReport.PointBodyVertices"/>）の上限。</summary>
        public int MaxPoints = 2000;

        /// <summary>押し出しで「調べる→押し出す→仕上げ」を繰り返す回数の上限。</summary>
        public int MaxPasses = 3;

        /// <summary>1回の押し出しの仕上げの、反復回数の上限。</summary>
        public int MaxIterationsPerPass = 1500;

        /// <summary>仕上げで、1回の Solve にまとめる反復回数（この回数ごとに終わったかを確かめる）。</summary>
        public int IterationsPerStep = 20;

        /// <summary>仕上げの終わりとみなす最大の伸び（0.01 で 1%）。押し出す前から伸びていれば、そのときの伸びまで。</summary>
        public float StretchTolerance = 0.01f;

        /// <summary>押し出し全体の時間の上限（ミリ秒）。</summary>
        public double TimeLimitMilliseconds = 20000.0;

        /// <summary>
        /// 押し出しの途中で呼ばれる（引数は進み具合 0〜1）。false を返すと、そこでやめる（それまでの形は残る）。null なら呼ばない。
        /// 進捗バーの表示と、取り消しに使う。
        /// </summary>
        public Func<float, bool> Progress;

        public PenetrationOptions Clone() => (PenetrationOptions)MemberwiseClone();
    }

    /// <summary>貫通を調べた結果（<see cref="PenetrationDetector.Detect"/>）。</summary>
    public sealed class PenetrationReport
    {
        /// <summary>これより小さい押し出しの量（メートル）は数えない（計算の誤差）。</summary>
        public const float CountTolerance = 1e-4f;

        /// <summary><see cref="Kind"/>: 粒子が体の中にある、または体に近すぎる。</summary>
        public const byte KindParticle = 1;

        /// <summary><see cref="Kind"/>: 体の頂点が、粒子の結びついた布の面の外へ出ている。</summary>
        public const byte KindBodyVertex = 2;

        /// <summary>粒子ごとの、外へ押し出すべき距離（メートル、余白を含む）。押し出さなくてよい粒子は 0。</summary>
        public float[] Push = new float[0];

        /// <summary>粒子ごとの、見つけ方（<see cref="KindParticle"/>・<see cref="KindBodyVertex"/> の組み合わせ）。</summary>
        public byte[] Kind = new byte[0];

        /// <summary>
        /// 粒子ごとの、体の頂点が布の外に出ている所のための平面（点と外向き）と、そのための押し出しの量（無ければ 0）。
        /// 押し出しで、粒子そのものが体の外にあっても、体の尖った所の外まで出すのに使う（<see cref="ClothSolver.AddBodyExtraPlane"/>）。
        /// </summary>
        public float[] PokeRequirement = new float[0];
        public Vector3[] PokePoint = new Vector3[0];
        public Vector3[] PokeNormal = new Vector3[0];

        /// <summary>調べたときの余白（メートル）。</summary>
        public float Margin;

        /// <summary>体（検索に使う島）があったか。無ければ何も調べていない。</summary>
        public bool HasBody;

        /// <summary>体が布を突き抜けている粒子の数（体の中にある、または体の頂点が出ている所の粒子）。</summary>
        public int PenetratingCount;

        /// <summary>突き抜けてはいないが、体に余白より近い粒子の数。</summary>
        public int CloseCount;

        /// <summary>体が布を突き抜けている所の数（突き抜けている粒子の、伸びの制約でつながったまとまりの数）。</summary>
        public int RegionCount;

        /// <summary>突き抜けている粒子のうち、ピン留めのもの（押し出せない）。</summary>
        public int PinnedCount;

        /// <summary>布の外へ出ている体の頂点の数。</summary>
        public int BodyVertexCount;

        /// <summary>一番深く突き抜けている所の深さ（メートル）。突き抜けていなければ 0。</summary>
        public float MaxDepth;

        /// <summary>押し出す対象の粒子の数（突き抜けている＋近すぎる）。</summary>
        public int NeedPushCount => PenetratingCount + CloseCount;

        /// <summary>表示用の、突き抜けている粒子（多いときは間引く）。位置は今の粒子の位置から読む。</summary>
        public readonly List<int> PointParticles = new List<int>();

        /// <summary>表示用の、布の外へ出ている体の頂点の位置（多いときは間引く）。</summary>
        public readonly List<Vector3> PointBodyVertices = new List<Vector3>();

        /// <summary>調べるのにかかった時間（ミリ秒）。</summary>
        public double Milliseconds;

        public override string ToString()
        {
            if (!HasBody) return "体が見つからないので、貫通は調べていません";
            return $"体が布を突き抜けている所 {RegionCount} か所（粒子 {PenetratingCount}、最大 {MaxDepth * 1000f:F1} mm、ピン留め {PinnedCount}、布の外に出た体の頂点 {BodyVertexCount}）" +
                $" / 余白 {Margin * 1000f:F1} mm より近い粒子 {CloseCount}（{Milliseconds:F0} ms）";
        }
    }

    /// <summary>
    /// 布と体の貫通を調べる。次の2つを調べ、布の粒子ごとに「外へ押し出すべき距離」を出す。
    /// (a) 布の粒子が体の中にある、または体に余白より近い（粒子の位置での体の符号付き距離）。
    /// (b) 体の頂点が、布の今の面より外（布の外向きの側）に出ている（粒子の間を体の尖った所が抜けている）。
    ///     その面の三角形の頂点に結びついた粒子を、体の頂点の外（余白）まで押し出す対象にする。
    /// どちらも、体の法線と布の外向きがだいたい同じ向き（<see cref="PenetrationOptions.MinAlignment"/>）の所だけを見る。
    /// 布の外向きは、粒子の元の形の外向き（<see cref="ClothModel.RestNormals"/>）の側。外向きが決まらない粒子（裏面用の重なり・平らな部品）は、
    /// 体の法線の側を外とみなす。布の形は呼ぶたびに今の粒子の位置から作るので、編集中いつでも呼べる。
    /// </summary>
    public sealed class PenetrationDetector
    {
        readonly ParticleCloth _cloth;
        readonly sbyte[] _particleSign;     // 粒子の外向きと、乗っている三角形の巻き順の向きが同じなら +1、逆なら -1、外向きが無ければ 0
        readonly sbyte[] _triangleSign;     // 三角形の巻き順の向きが外向きなら +1、逆なら -1、決まらなければ 0（島ごとの多数決。裏面用の重なりは 0）
        readonly Vector3[] _welded;
        readonly ClosestTriangleTree _tree; // 布の今の形の三角形（体の頂点から一番近い布の点を探す）
        int[] _parent;                      // まとまりを数える作業用

        public ParticleCloth Cloth => _cloth;

        public PenetrationDetector(ParticleCloth cloth)
        {
            _cloth = cloth ?? throw new ArgumentNullException(nameof(cloth));
            ComputeSigns(cloth, out _particleSign, out _triangleSign);
            _welded = (Vector3[])cloth.Topology.Positions.Clone();
            _tree = new ClosestTriangleTree(_welded, cloth.Topology.Triangles);
        }

        /// <summary>
        /// 布の三角形（<see cref="MeshTopology.Triangles"/>）ごとの、巻き順の向きが布の外向きなら +1、逆なら -1、決まらなければ 0。
        /// 粒子の元の形の外向き（<see cref="ClothModel.RestNormals"/>）と巻き順の向きを比べ、島ごとの多数決で決める。
        /// 裏面用に同じ3頂点で重ねた三角形と、外向きの無い島（平らな部品）は 0。
        /// スキニングでは巻き順は変わらないので、ポーズを変えた布の外向きにもそのまま使える（動作チェック）。
        /// </summary>
        public static sbyte[] ComputeTriangleSigns(ParticleCloth cloth)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            ComputeSigns(cloth, out _, out sbyte[] triangleSign);
            return triangleSign;
        }

        static void ComputeSigns(ParticleCloth cloth, out sbyte[] particleSign, out sbyte[] triangleSign)
        {
            MeshTopology topology = cloth.Topology;
            int[] triangles = topology.Triangles;
            Vector3[] rest = topology.Positions;
            Vector3[] restNormals = cloth.Model.RestNormals;
            int n = cloth.Model.ParticleCount;

            particleSign = new sbyte[n];
            var votes = new int[topology.IslandCount];
            for (int i = 0; i < n; i++)
            {
                if (restNormals == null || restNormals[i] == Vector3.Zero) continue;
                int t = cloth.ParticleTriangle[i];
                Vector3 winding = Vector3.Cross(rest[triangles[3 * t + 1]] - rest[triangles[3 * t]], rest[triangles[3 * t + 2]] - rest[triangles[3 * t]]);
                float dot = Vector3.Dot(winding, restNormals[i]);
                if (dot == 0f) continue;
                particleSign[i] = dot > 0f ? (sbyte)1 : (sbyte)-1;
                votes[topology.TriangleIsland[t]] += particleSign[i];
            }

            // 同じ3頂点を持つ三角形（裏面用の重ね）は、どちらが外か分からない
            int triangleCount = topology.TriangleCount;
            var twin = new bool[triangleCount];
            var seen = new Dictionary<(int, int, int), int>();
            for (int t = 0; t < triangleCount; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                if (a > b) (a, b) = (b, a);
                if (b > c) (b, c) = (c, b);
                if (a > b) (a, b) = (b, a);
                if (seen.TryGetValue((a, b, c), out int other))
                {
                    twin[t] = true;
                    twin[other] = true;
                }
                else
                {
                    seen.Add((a, b, c), t);
                }
            }
            triangleSign = new sbyte[triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                int vote = votes[topology.TriangleIsland[t]];
                if (twin[t] || vote == 0) continue;
                triangleSign[t] = vote > 0 ? (sbyte)1 : (sbyte)-1;
            }
        }

        /// <summary>
        /// 今の布（solver の粒子の位置）と体の貫通を調べ、result に入れる（result の配列は使い回す）。
        /// 体が null か、検索に使う島が無ければ、何も無い結果（<see cref="PenetrationReport.HasBody"/> が false）。
        /// </summary>
        public void Detect(ClothSolver solver, BodySurface body, PenetrationOptions options, PenetrationReport result)
        {
            if (solver == null) throw new ArgumentNullException(nameof(solver));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (result == null) throw new ArgumentNullException(nameof(result));
            var timer = Stopwatch.StartNew();
            int n = _cloth.Model.ParticleCount;
            Prepare(result, n);
            float margin = Math.Max(0f, options.Margin);
            result.Margin = margin;
            result.HasBody = body != null && body.EnabledIslandCount > 0;
            if (!result.HasBody)
            {
                result.Milliseconds = timer.Elapsed.TotalMilliseconds;
                return;
            }

            ReadOnlySpan<Vector3> positions = solver.Positions;
            _cloth.DeformWelded(positions, _welded, solver.Kernel as IShapeKernel);
            int[] triangles = _cloth.Topology.Triangles;
            int[] particleTriangle = _cloth.ParticleTriangle;
            float maxDistance = options.MaxDistance;
            float minAlignment = options.MinAlignment;
            float[] push = result.Push;
            byte[] kind = result.Kind;

            // (a) 粒子が体の中にある、または近すぎる
            for (int i = 0; i < n; i++)
            {
                if (!body.TryQuery(positions[i], maxDistance, out BodyHit hit)) continue;
                float d = hit.SignedDistance;
                if (d >= margin) continue;
                int t = particleTriangle[i];
                FaceNormal(triangles, t, out float nx, out float ny, out float nz);
                float alignment = nx * hit.Normal.X + ny * hit.Normal.Y + nz * hit.Normal.Z;
                int sign = _particleSign[i];
                if (sign == 0) sign = alignment >= 0f ? 1 : -1;
                if (sign * alignment < minAlignment) continue;
                float amount = margin - d;
                if (amount > push[i]) push[i] = amount;
                kind[i] |= PenetrationReport.KindParticle;
            }

            // (b) 体の頂点が、布の今の面の外へ出ている
            int pokeVertices = 0;
            var pokeCandidates = result.PointBodyVertices;
            if (options.CheckBodyVertices)
            {
                _tree.Refit(_welded);
                float limitSq = maxDistance * maxDistance;
                for (int v = 0; v < body.VertexCount; v++)
                {
                    if (!body.IsVertexEnabled(v)) continue;
                    Vector3 p = body.GetVertex(v);
                    if (_tree.RootDistanceSquared(p.X, p.Y, p.Z) > limitSq) continue;
                    if (!_tree.FindClosest(_welded, p.X, p.Y, p.Z, maxDistance, out int t, out float qx, out float qy, out float qz,
                        out _, out _, out _, out float distanceSquared)) continue;
                    FaceNormal(triangles, t, out float nx, out float ny, out float nz);
                    Vector3 bodyNormal = body.GetVertexNormal(v);
                    float alignment = nx * bodyNormal.X + ny * bodyNormal.Y + nz * bodyNormal.Z;
                    int sign = _triangleSign[t];
                    if (sign == 0) sign = alignment >= 0f ? 1 : -1;
                    if (sign * alignment < minAlignment) continue;
                    nx *= sign;
                    ny *= sign;
                    nz *= sign;
                    float ox = p.X - qx, oy = p.Y - qy, oz = p.Z - qz;
                    float along = ox * nx + oy * ny + oz * nz;
                    // 布の面より内にある体の頂点は見ない（体に近すぎるかどうかは (a) の粒子で見る。
                    // 粒子の間の面は粒子より少し内へたわむので、面で余白を見ると、なだらかな膨らみでも粒子を押し出しすぎる）
                    if (along <= PenetrationReport.CountTolerance) continue;
                    // 一番近い布の点が布の縁にあり、体の頂点が縁の向こう（面に沿った向き）にあるものは除く（裾の下の脚など）
                    if (distanceSquared - along * along > 0.25f * distanceSquared) continue;
                    float requirement = along + margin;
                    var normal = new Vector3(nx, ny, nz);
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int welded = triangles[3 * t + corner];
                        int bound = _cloth.BoundParticleCount(welded);
                        for (int k = 0; k < bound; k++)
                        {
                            int j = _cloth.GetBoundParticle(welded, k, out float weight);
                            if (!(weight > 0f)) continue;
                            if (requirement > push[j]) push[j] = requirement;
                            kind[j] |= PenetrationReport.KindBodyVertex;
                            if (requirement > result.PokeRequirement[j])
                            {
                                result.PokeRequirement[j] = requirement;
                                result.PokePoint[j] = p;
                                result.PokeNormal[j] = normal;
                            }
                        }
                    }
                    pokeVertices++;
                    pokeCandidates.Add(p);
                }
            }
            result.BodyVertexCount = pokeVertices;

            Summarize(solver, result, margin);
            LimitPoints(result, Math.Max(0, options.MaxPoints));
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
        }

        void Prepare(PenetrationReport result, int n)
        {
            if (result.Push.Length != n)
            {
                result.Push = new float[n];
                result.Kind = new byte[n];
                result.PokeRequirement = new float[n];
                result.PokePoint = new Vector3[n];
                result.PokeNormal = new Vector3[n];
            }
            else
            {
                Array.Clear(result.Push, 0, n);
                Array.Clear(result.Kind, 0, n);
                Array.Clear(result.PokeRequirement, 0, n);
            }
            result.PenetratingCount = result.CloseCount = result.RegionCount = result.PinnedCount = result.BodyVertexCount = 0;
            result.MaxDepth = 0f;
            result.PointParticles.Clear();
            result.PointBodyVertices.Clear();
        }

        // 今の形の三角形 t の巻き順の向き（長さ1）
        void FaceNormal(int[] triangles, int t, out float nx, out float ny, out float nz)
        {
            Vector3 a = _welded[triangles[3 * t]], b = _welded[triangles[3 * t + 1]], c = _welded[triangles[3 * t + 2]];
            float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
            nx = aby * acz - abz * acy;
            ny = abz * acx - abx * acz;
            nz = abx * acy - aby * acx;
            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 1e-20f)
            {
                nx /= length;
                ny /= length;
                nz /= length;
            }
        }

        // 数と深さ、突き抜けている所のまとまりを数える
        void Summarize(ClothSolver solver, PenetrationReport result, float margin)
        {
            int n = result.Push.Length;
            float tolerance = PenetrationReport.CountTolerance;
            if (_parent == null || _parent.Length != n) _parent = new int[n];
            int[] parent = _parent;
            for (int i = 0; i < n; i++)
            {
                parent[i] = i;
                float amount = result.Push[i];
                if (amount > margin + tolerance)
                {
                    result.PenetratingCount++;
                    if (solver.IsPinned(i)) result.PinnedCount++;
                    if (amount - margin > result.MaxDepth) result.MaxDepth = amount - margin;
                }
                else if (amount > tolerance)
                {
                    result.CloseCount++;
                }
            }

            int[] pairs = _cloth.Model.StretchPairs;
            float limit = margin + tolerance;
            for (int k = 0; k + 1 < pairs.Length; k += 2)
            {
                int a = pairs[k], b = pairs[k + 1];
                if (result.Push[a] > limit && result.Push[b] > limit) Union(parent, a, b);
            }
            for (int i = 0; i < n; i++)
            {
                if (result.Push[i] > limit && Find(parent, i) == i) result.RegionCount++;
            }
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        // 表示用の点を、突き抜けている粒子（4分の3まで）と布の外に出た体の頂点から、上限までに間引いて選ぶ
        void LimitPoints(PenetrationReport result, int maxPoints)
        {
            float limit = result.Margin + PenetrationReport.CountTolerance;
            int particleBudget = result.PointBodyVertices.Count > 0 ? maxPoints * 3 / 4 : maxPoints;
            int stride = result.PenetratingCount <= particleBudget ? 1 : (result.PenetratingCount + particleBudget - 1) / Math.Max(1, particleBudget);
            int seen = 0;
            for (int i = 0; i < result.Push.Length && result.PointParticles.Count < particleBudget; i++)
            {
                if (!(result.Push[i] > limit)) continue;
                if (seen++ % stride != 0) continue;
                result.PointParticles.Add(i);
            }

            int vertexBudget = maxPoints - result.PointParticles.Count;
            List<Vector3> vertices = result.PointBodyVertices;
            if (vertices.Count <= vertexBudget) return;
            if (vertexBudget <= 0)
            {
                vertices.Clear();
                return;
            }
            int vertexStride = (vertices.Count + vertexBudget - 1) / vertexBudget;
            int kept = 0;
            for (int k = 0; k < vertices.Count && kept < vertexBudget; k += vertexStride) vertices[kept++] = vertices[k];
            vertices.RemoveRange(kept, vertices.Count - kept);
        }
    }

    /// <summary><see cref="PenetrationFixer.Fix"/> の結果。</summary>
    public sealed class PenetrationFixResult
    {
        /// <summary>直す前に、突き抜けていた粒子の数と所の数と、一番深い所の深さ（メートル）。</summary>
        public int InitialPenetrating;
        public int InitialRegions;
        public float InitialMaxDepth;

        /// <summary>直したあとに残った、突き抜けている粒子の数と所の数と、一番深い所の深さ（メートル）。</summary>
        public int RemainingPenetrating;
        public int RemainingRegions;
        public float RemainingMaxDepth;

        /// <summary>直したあとに残った、余白より近い粒子の数。</summary>
        public int RemainingClose;

        /// <summary>直したあとに残った、突き抜けている粒子のうちピン留めのもの（押し出せない）。</summary>
        public int RemainingPinned;

        /// <summary>行った「調べる→押し出す→仕上げ」の回数と、反復の合計。</summary>
        public int Passes;
        public int Iterations;

        /// <summary>押し出したあとの最大の伸び（0.01 で 1%）と、押し出す前の最大の伸び。</summary>
        public float MaxStretch;
        public float StretchBefore;

        /// <summary>かかった時間（ミリ秒）。</summary>
        public double Milliseconds;

        /// <summary>途中で取り消された（<see cref="PenetrationOptions.Progress"/> が false を返した）。</summary>
        public bool Cancelled;

        public override string ToString()
        {
            return $"突き抜けている所 {InitialRegions} か所（粒子 {InitialPenetrating}、最大 {InitialMaxDepth * 1000f:F1} mm）→ {RemainingRegions} か所（粒子 {RemainingPenetrating}、最大 {RemainingMaxDepth * 1000f:F1} mm、ピン留め {RemainingPinned}）" +
                $" / 近すぎる粒子 {RemainingClose} / {Passes} 回・反復 {Iterations} / 最大の伸び {StretchBefore * 100f:F1}% → {MaxStretch * 100f:F1}% / {Milliseconds:F0} ms";
        }
    }

    /// <summary>
    /// 貫通を直す（布で押し出す）。体の中にある・近すぎる粒子の「体から保つ距離の下限」を余白まで上げ（<see cref="ClothSolver.RequireBodyClearance"/>）、
    /// 布の計算（伸び・曲げ・テザー・層どうしの接触を守る）を回して、体の外へ押し出す。周りの布は布の計算で自然に付いてくる。
    /// 「調べる→押し出す→仕上げ」を、突き抜けている所が無くなるか、回数の上限まで繰り返す。
    /// 体の頂点が粒子の間から出ている所は、1回目は粒子が体に入っている所が無いときだけ、2回目からはまだ出ている所だけ、
    /// 体の頂点を通る平面を足して押し出す（なだらかな膨らみは、粒子を押し出すだけで直るので、平面を足して押し出しすぎないため）。
    /// ピン留めの粒子は動かさない（体に入っていれば、残った数に入る）。
    /// </summary>
    public static class PenetrationFixer
    {
        // 押し出す前から伸びていた布の仕上げで、伸びが下がらなくなったかを見る間隔（反復回数）
        const int StallIterations = 200;

        /// <summary>
        /// solver の今の形の貫通を直す。solver の体は body にする（違えば設定し直す）。つまんでいれば外す。
        /// report には最後に調べた結果が入る。
        /// </summary>
        public static PenetrationFixResult Fix(PenetrationDetector detector, ClothSolver solver, BodySurface body, PenetrationOptions options, PenetrationReport report)
        {
            if (detector == null) throw new ArgumentNullException(nameof(detector));
            if (solver == null) throw new ArgumentNullException(nameof(solver));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (report == null) throw new ArgumentNullException(nameof(report));
            var timer = Stopwatch.StartNew();
            var result = new PenetrationFixResult();
            if (!ReferenceEquals(solver.Body, body)) solver.SetBody(body);
            solver.BodyMargin = Math.Max(0f, options.Margin);
            solver.Release();
            result.StretchBefore = solver.MeasureMaxStretch();
            float stretchTolerance = Math.Max(options.StretchTolerance, result.StretchBefore);
            int step = Math.Max(1, options.IterationsPerStep);

            detector.Detect(solver, body, options, report);
            result.InitialPenetrating = report.PenetratingCount;
            result.InitialRegions = report.RegionCount;
            result.InitialMaxDepth = report.MaxDepth;

            var clear = new List<int>();
            float tolerance = PenetrationReport.CountTolerance;
            for (int pass = 0; pass < options.MaxPasses && body != null; pass++)
            {
                if (report.NeedPushCount == 0) break;
                int countBefore = report.NeedPushCount;
                float depthBefore = report.MaxDepth;
                // 平面は粒子が動くまで調べ直さないので、くぼんだ所では1次の近似がずれる。回ごとに今の位置で調べ直す
                solver.InvalidateBodyPlanes();
                clear.Clear();
                for (int i = 0; i < report.Push.Length; i++)
                {
                    if (report.Push[i] > tolerance && (report.Kind[i] & PenetrationReport.KindParticle) != 0 && !solver.IsPinned(i)) clear.Add(i);
                }
                solver.RequireBodyClearance(clear);
                int extra = 0;
                bool deferred = false;
                bool usePokes = pass > 0 || clear.Count == 0;
                for (int i = 0; i < report.PokeRequirement.Length; i++)
                {
                    if (!(report.PokeRequirement[i] > tolerance) || solver.IsPinned(i)) continue;
                    if (!usePokes)
                    {
                        deferred = true;
                        continue;
                    }
                    solver.AddBodyExtraPlane(i, report.PokePoint[i], report.PokeNormal[i]);
                    extra++;
                }
                if (clear.Count == 0 && extra == 0) break;

                int iterations = 0;
                bool cancelled = false;
                float windowStretch = float.MaxValue;
                int windowStart = 0;
                while (iterations < options.MaxIterationsPerPass && timer.Elapsed.TotalMilliseconds < options.TimeLimitMilliseconds)
                {
                    solver.Solve(step);
                    iterations += step;
                    if (solver.MeasureBodyViolation() <= tolerance)
                    {
                        float stretch = solver.MeasureMaxStretch();
                        if (stretch <= options.StretchTolerance) break;
                        // 押し出す前から伸びていた布（強く押し付けたあとなど）は、許容する伸びまで下がらないことがある。
                        // 押し出す前より伸びておらず、伸びが下がらなくなったら（StallIterations 回で 1% も減らない）やめる
                        if (stretch <= stretchTolerance && iterations - windowStart >= StallIterations)
                        {
                            if (stretch > windowStretch * 0.99f) break;
                            windowStretch = stretch;
                            windowStart = iterations;
                        }
                    }
                    if (options.Progress != null)
                    {
                        float byWork = (pass + iterations / (float)Math.Max(1, options.MaxIterationsPerPass)) / Math.Max(1, options.MaxPasses);
                        float byTime = (float)(timer.Elapsed.TotalMilliseconds / Math.Max(1.0, options.TimeLimitMilliseconds));
                        if (!options.Progress(Math.Min(1f, Math.Max(byWork, byTime))))
                        {
                            cancelled = true;
                            break;
                        }
                    }
                }
                result.Iterations += iterations;
                result.Passes++;
                detector.Detect(solver, body, options, report);
                if (cancelled)
                {
                    result.Cancelled = true;
                    break;
                }
                if (timer.Elapsed.TotalMilliseconds >= options.TimeLimitMilliseconds) break;
                // 押し出しても減らない（布の長さで届かない、ピンに引かれるなど）なら、繰り返しても同じなのでやめる
                if (extra == 0 && !deferred && report.NeedPushCount >= countBefore && report.MaxDepth >= depthBefore - tolerance) break;
            }

            result.RemainingPenetrating = report.PenetratingCount;
            result.RemainingRegions = report.RegionCount;
            result.RemainingMaxDepth = report.MaxDepth;
            result.RemainingClose = report.CloseCount;
            result.RemainingPinned = report.PinnedCount;
            result.MaxStretch = solver.MeasureMaxStretch();
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
