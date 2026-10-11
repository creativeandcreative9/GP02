using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>「元のポーズで外へ逃がす」（<see cref="PoseEscapeFixer"/>）の設定。</summary>
    public sealed class PoseEscapeOptions
    {
        /// <summary>深さに足す余白（メートル）の既定値。</summary>
        public const float DefaultExtra = 0.002f;

        /// <summary>1つの粒子を外へ逃がす量（メートル）の上限の既定値。</summary>
        public const float DefaultMaxPush = 0.02f;

        /// <summary>ポーズで動く割合の下限の既定値。</summary>
        public const float DefaultMinGain = 0.7f;

        /// <summary>深さに足す余白（メートル。<see cref="PoseCheckSummary.PlanEscape"/> で足す）。ポーズでの深さちょうどだと、補間の誤差で少し残るため。</summary>
        public float Extra = DefaultExtra;

        /// <summary>
        /// 1つの粒子を外へ逃がす量（メートル）の上限。ウェイトが原因の深い貫通（座ったときに太ももが服を突き抜けるなど）を
        /// 深さの分だけ逃がすと、元の姿勢の服が大きく膨らむので、これを超えるか所は逃がさない（その分はウェイトの修正の受け持ち）。
        /// </summary>
        public float MaxPush = DefaultMaxPush;

        /// <summary>
        /// ポーズで動く割合（<see cref="PosePenetration.Gains"/>）の下限。これより小さい頂点のあるか所は逃がさない
        /// （ひざ・ひじを大きく曲げたときのように、編集の姿勢で動かした分がポーズでほとんど残らない所。服と体のウェイトの違いが原因）。
        /// </summary>
        public float MinGain = DefaultMinGain;

        /// <summary>これより小さい逃がす量は無視する（メートル）。</summary>
        public float MinPush = PenetrationReport.CountTolerance;

        /// <summary>外へ逃がしたときに許す伸びの既定値（0.03 で 3%）。</summary>
        public const float DefaultMaxStretch = 0.03f;

        /// <summary>
        /// 外へ逃がしたときに許す伸び（0.03 で 3%）。ぴったりの服は、外へ出すと周りの長さが足りずに伸びるので、
        /// 逃がす所（つながった、逃がす粒子のまとまり）の周りの伸びがこれを超えたら、その所はそれ以上外へ出さない（布の長さを保つ）。
        /// 逃がす前から伸びていれば、そのときの伸びに少し足したものまで。
        /// </summary>
        public float MaxStretch = DefaultMaxStretch;

        /// <summary>外へ逃がしたときに許す、しわの深さの既定値（0.04 で 4%）。</summary>
        public const float DefaultMaxFold = 0.04f;

        /// <summary>
        /// 外へ逃がしたときに許す、しわ（1つおきの粒子の間が、元の長さより縮んだ割合。0.04 で 4%）。外へ出した所へ周りの布が寄ってくると、
        /// 寄った布が余ってしわになる（広がったスカートの布を、細いウエストへ寄せると縦のしわが出るなど）ので、
        /// 所の周りのしわがこれを超えたら、その所はそれ以上外へ出さない。逃がす前からしわがあれば、その分は数えない。
        /// </summary>
        public float MaxFold = DefaultMaxFold;

        /// <summary>逃がす量を周りへなめらかに広げる半径の既定値（メートル）。</summary>
        public const float DefaultFeatherRadius = 0.015f;

        /// <summary>
        /// 逃がす量を、周りの粒子へなめらかに広げる半径（メートル。0 なら広げない）。逃がす所の縁で量が急に 0 になると、
        /// 布が段のように折れて筋が残るので、縁から外へ向かって少しずつ減らす。
        /// </summary>
        public float FeatherRadius = DefaultFeatherRadius;

        /// <summary>少しずつ外へ出すときの、1段の量（メートル）。</summary>
        public float PushStep = 0.001f;

        /// <summary>1段ごとの反復回数。</summary>
        public int IterationsPerPushStep = 30;

        /// <summary>反復回数の上限（少しずつ出す分と、仕上げの分の合計）。</summary>
        public int MaxIterations = 3000;

        /// <summary>仕上げで、1回の Solve にまとめる反復回数（この回数ごとに終わったかを確かめる）。</summary>
        public int IterationsPerStep = 20;

        /// <summary>仕上げの終わりとみなす最大の伸び（0.01 で 1%）。逃がす前から伸びていれば、そのときの伸びまで。</summary>
        public float StretchTolerance = 0.01f;

        /// <summary>時間の上限（ミリ秒）。</summary>
        public double TimeLimitMilliseconds = 10000.0;

        /// <summary>途中で呼ばれる（引数は進み具合 0〜1）。false を返すとそこでやめる（それまでの形は残る）。null なら呼ばない。</summary>
        public Func<float, bool> Progress;

        public PoseEscapeOptions Clone() => (PoseEscapeOptions)MemberwiseClone();
    }

    /// <summary><see cref="PoseEscapeFixer.Fix"/> の結果。</summary>
    public sealed class PoseEscapeResult
    {
        /// <summary>外へ逃がした（面を足した）粒子の数。</summary>
        public int Particles;

        /// <summary>逃がす量があったが、ピン留めなので動かさなかった粒子の数。</summary>
        public int Pinned;

        /// <summary>外向きが決まらない（平らな部品・裏面用の重なり）ので動かさなかった粒子の数。</summary>
        public int NoDirection;

        /// <summary>逃がす量が上限（<see cref="PoseEscapeOptions.MaxPush"/>）を超えたので、上限で止めた粒子の数。</summary>
        public int Capped;

        /// <summary>周りが伸びすぎる（<see cref="PoseEscapeOptions.MaxStretch"/>）か、しわが寄る（<see cref="PoseEscapeOptions.MaxFold"/>）ので、決めた量より手前で止めた粒子の数。</summary>
        public int Limited;

        /// <summary>逃がす所（つながった、逃がす粒子のまとまり）の数と、そのうち伸び・しわで手前に止めた所の数。</summary>
        public int Clusters;
        public int LimitedClusters;

        /// <summary>逃がした量のうち一番大きいもの（メートル）。</summary>
        public float MaxPush;

        /// <summary>行った反復の合計。</summary>
        public int Iterations;

        /// <summary>逃がす前と後の最大の伸び（0.01 で 1%）。</summary>
        public float StretchBefore;
        public float MaxStretch;

        /// <summary>終わったときに残っていた、面より内の深さの最大（メートル。0 ならすべて逃がせた）。</summary>
        public float RemainingViolation;

        /// <summary>かかった時間（ミリ秒）。</summary>
        public double Milliseconds;

        /// <summary>途中で取り消された（<see cref="PoseEscapeOptions.Progress"/> が false を返した）。</summary>
        public bool Cancelled;

        public override string ToString()
        {
            return $"外へ逃がした粒子 {Particles}（最大 {MaxPush * 1000f:F1} mm、伸び・しわで手前に止めた {Limited}（所 {LimitedClusters} / {Clusters}）、上限で止めた {Capped}、ピン留め {Pinned}、向きが決まらない {NoDirection}）" +
                $" / 残り {RemainingViolation * 1000f:F2} mm / 反復 {Iterations} / 最大の伸び {StretchBefore * 100f:F1}% → {MaxStretch * 100f:F1}% / {Milliseconds:F0} ms";
        }
    }

    /// <summary>
    /// 動作チェックで見つかった貫通を、編集の姿勢（元のポーズ）で布を外向きに逃がして減らす（第1段の修正）。
    /// 粒子ごとに逃がす量（<see cref="PoseCheckSummary.PlanEscape"/> の量を <see cref="PoseCheckSummary.ToParticles"/> で粒子にしたもの）を受け取り、
    /// その粒子の今の面の外向きに、元の位置からその量だけ外の平面を置いて（<see cref="ClothSolver.AddBodyExtraPlane"/>）、布の計算で解く。
    /// 量は周りの粒子へなめらかに広げる（<see cref="PoseEscapeOptions.FeatherRadius"/>）。ほかの粒子は面に沿って付いてくるが、今の位置より内へは入らない。
    /// ピン留めの粒子は動かさない。
    /// <para>
    /// 平面は一度に置かず、少しずつ（<see cref="PoseEscapeOptions.PushStep"/>）外へ出す。逃がす所（つながった、量のある粒子のまとまり。
    /// 広げた分は、量をもらった所に入れる）ごとに、周りの伸びが <see cref="PoseEscapeOptions.MaxStretch"/> を、しわが <see cref="PoseEscapeOptions.MaxFold"/> を超えたら、
    /// その所は1段戻して止める（ぴったりの服を深さの分だけ出すと、布の長さが足りずに大きく伸びるか、寄せた布がしわになるため。伸ばすより、逃がしきれない分を残す）。
    /// 所ごとにそろえて動かすのは、粒子ごとに止めると、出した所が波打つため。
    /// </para>
    /// ポーズでの貫通がウェイト（服と体のボーンの割り振りの違い）のせいなら、元の姿勢で逃がしても、ポーズでは同じだけは離れないので直りきらない
    /// （ゆったりしたスカートが座ったときに太ももに抜かれる、など）。それはウェイトの修正（第2段）の受け持ち。
    /// </summary>
    public static class PoseEscapeFixer
    {
        // 逃がす前から伸びていた布で、伸びが下がらなくなったかを見る間隔（反復回数）
        const int StallIterations = 200;

        // 所の「周り」とみなす範囲（伸びの制約でつながった粒子をたどる回数）。外へ出した所の縁の伸びを見るため
        const int NeighborRings = 3;

        // 逃がす前から伸びていた布で、許す伸びに足す分
        const float StretchMargin = 0.005f;

        // 伸びが許す量を超えた所を、戻す前に待つ段の数。計算が追いつく前の一時的な伸びで戻さないため（待つ間は出さない）
        const int HoldSteps = 5;

        /// <summary>
        /// solver の今の形で、particlePush の量だけ粒子を外へ逃がす。つまんでいれば外す。足した平面は、次につまむか、粒子の位置を置き換えるまで残る
        /// （貫通を直す、と同じ。仕上げで元へ戻らないように）。
        /// </summary>
        /// <param name="particlePush">粒子ごとの逃がす量（メートル）。長さは粒子の数</param>
        public static PoseEscapeResult Fix(ParticleCloth cloth, ClothSolver solver, float[] particlePush, PoseEscapeOptions options)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            if (solver == null) throw new ArgumentNullException(nameof(solver));
            if (options == null) throw new ArgumentNullException(nameof(options));
            int n = cloth.Model.ParticleCount;
            if (particlePush == null || particlePush.Length != n) throw new ArgumentException("粒子数が一致しません。", nameof(particlePush));
            var timer = Stopwatch.StartNew();
            var result = new PoseEscapeResult();
            solver.Release();
            result.StretchBefore = solver.MeasureMaxStretch();
            float stretchTolerance = Math.Max(options.StretchTolerance, result.StretchBefore);
            float stretchLimit = Math.Max(options.MaxStretch, result.StretchBefore + StretchMargin);

            // 今の形の外向き（粒子が乗っている三角形の向きに、島ごとの外向きの符号を掛けたもの）と、逃がす量
            sbyte[] signs = PenetrationDetector.ComputeTriangleSigns(cloth);
            var welded = new Vector3[cloth.Topology.Positions.Length];
            cloth.DeformWelded(solver.Positions, welded, solver.Kernel as IShapeKernel);
            int[] triangles = cloth.Topology.Triangles;
            Vector3[] start = solver.Positions.ToArray();
            var normals = new Vector3[n];
            var target = new float[n];
            var seeds = new List<int>();
            for (int i = 0; i < n; i++)
            {
                float push = particlePush[i];
                bool wanted = push > options.MinPush;
                if (solver.IsPinned(i))
                {
                    if (wanted) result.Pinned++;
                    continue;
                }
                int t = cloth.ParticleTriangle[i];
                Vector3 a = welded[triangles[3 * t]], b = welded[triangles[3 * t + 1]], c = welded[triangles[3 * t + 2]];
                float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
                float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
                float nx = aby * acz - abz * acy, ny = abz * acx - abx * acz, nz = abx * acy - aby * acx;
                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (signs[t] == 0 || !(length > 1e-20f))
                {
                    if (wanted) result.NoDirection++;
                    continue;
                }
                float scale = signs[t] / length;
                normals[i] = new Vector3(nx * scale, ny * scale, nz * scale);
                if (!wanted) continue;
                float amount = push;
                if (amount > options.MaxPush)
                {
                    amount = options.MaxPush;
                    result.Capped++;
                }
                target[i] = amount;
                seeds.Add(i);
            }

            if (seeds.Count > 0)
            {
                BuildAdjacency(cloth.Model, out int[] adjacencyStart, out int[] adjacency, out float[] adjacencyLength);
                // 所: 量のある粒子のつながったまとまり。広げた分の粒子は、一番多く量をもらった所に入れる（近くの所どうしをつなげない）
                int[] group = GroupSeeds(n, adjacencyStart, adjacency, seeds, out int groupCount);
                Feather(n, adjacencyStart, adjacency, adjacencyLength, seeds, target, normals, group, options.FeatherRadius, options.MinPush);
                var pushed = new List<int>();
                var held = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (normals[i] == Vector3.Zero) continue;
                    if (group[i] >= 0) pushed.Add(i);
                    else held.Add(i);
                }
                result.Clusters = groupCount;
                ListGroups(n, adjacencyStart, adjacency, pushed, group, groupCount, out int[] memberStart, out int[] members, out int[] aroundStart, out int[] around);
                var peak = new float[groupCount];
                for (int g = 0; g < groupCount; g++)
                {
                    for (int k = memberStart[g]; k < memberStart[g + 1]; k++) peak[g] = Math.Max(peak[g], target[members[k]]);
                }

                // 逃がさない粒子は、今の位置より内へ入らないようにする（面に沿っては動ける）。外へ出した所の分の長さを、
                // 周りが内へ寄って出すと、そこが別のポーズで新しく突き抜けるため（ウエストの前を出すと後ろが体へ寄り、反ったときに突き抜けるなど）
                var current = new float[n];
                SetPlanes(solver, held, start, normals, current);

                // 逃がす前のしわ（その分は数えない）
                var foldBefore = new float[n];
                MeasureFold(cloth.Model, solver.Positions, foldBefore);
                var fold = new float[n];

                var level = new float[groupCount];    // 所ごとの出す量の上限（粒子は min(決めた量, これ)）。1段ごとに上げる
                var state = new byte[groupCount];     // 0: 出している途中、1: 決めた量まで出した、2: 伸びすぎで戻した
                var hold = new int[groupCount];       // 伸びが許す量を超えてから待った段の数
                var stretch = new float[n];
                float step = Math.Max(1e-5f, options.PushStep);
                int stepIterations = Math.Max(1, options.IterationsPerPushStep);
                int iterations = 0;
                bool cancelled = false;

                // 少しずつ外へ出す。所の周りが伸びすぎたら、しばらく待ち、それでも下がらなければ1段戻して、それ以上は出さない
                // （戻したあとも伸びすぎなら、また待って戻す）
                while (iterations < options.MaxIterations && timer.Elapsed.TotalMilliseconds < options.TimeLimitMilliseconds)
                {
                    bool growing = false;
                    for (int g = 0; g < groupCount; g++)
                    {
                        if (state[g] != 0) continue;
                        growing = true;
                        if (hold[g] > 0) continue;
                        level[g] = Math.Min(peak[g], level[g] + step);
                        if (level[g] >= peak[g]) state[g] = 1;
                        Apply(level[g], target, current, members, memberStart[g], memberStart[g + 1]);
                    }
                    SetPlanes(solver, pushed, start, normals, current);
                    solver.Solve(stepIterations);
                    iterations += stepIterations;
                    solver.MeasureParticleStretch(stretch);
                    MeasureFold(cloth.Model, solver.Positions, fold);
                    bool waiting = false;
                    for (int g = 0; g < groupCount; g++)
                    {
                        // 伸びとしわの、許す量に対する割合の一番大きいもの
                        float worst = 0f;
                        for (int k = aroundStart[g]; k < aroundStart[g + 1]; k++)
                        {
                            int i = around[k];
                            worst = Math.Max(worst, stretch[i] / stretchLimit);
                            worst = Math.Max(worst, (fold[i] - foldBefore[i]) / Math.Max(1e-4f, options.MaxFold));
                        }
                        if (!(worst > 1f))
                        {
                            hold[g] = 0;
                            continue;
                        }
                        if (!(level[g] > 0f)) continue;
                        if (hold[g] < HoldSteps)
                        {
                            hold[g]++;
                            waiting = true;
                            if (state[g] == 1) state[g] = 0;
                            continue;
                        }
                        level[g] = Math.Max(0f, level[g] - step);
                        Apply(level[g], target, current, members, memberStart[g], memberStart[g + 1]);
                        state[g] = 2;
                        hold[g] = 0;
                        waiting = true;
                    }
                    if (!growing && !waiting) break;
                    if (options.Progress != null && !options.Progress(Progress(iterations, timer, options) * 0.5f))
                    {
                        cancelled = true;
                        break;
                    }
                }
                SetPlanes(solver, pushed, start, normals, current);

                // 仕上げ: 平面まで出し、伸びが下がるまで解く
                int windowStart = iterations;
                float windowStretch = float.MaxValue;
                float tolerance = PenetrationReport.CountTolerance;
                int settleStep = Math.Max(1, options.IterationsPerStep);
                while (!cancelled && iterations < options.MaxIterations && timer.Elapsed.TotalMilliseconds < options.TimeLimitMilliseconds)
                {
                    solver.Solve(settleStep);
                    iterations += settleStep;
                    if (solver.MeasureBodyViolation() <= tolerance)
                    {
                        float maxStretch = solver.MeasureMaxStretch();
                        if (maxStretch <= stretchTolerance) break;
                        // 下がらなくなったらやめる
                        if (iterations - windowStart >= StallIterations)
                        {
                            if (maxStretch > windowStretch * 0.99f) break;
                            windowStretch = maxStretch;
                            windowStart = iterations;
                        }
                    }
                    if (options.Progress != null && !options.Progress(0.5f + 0.5f * Progress(iterations, timer, options)))
                    {
                        cancelled = true;
                        break;
                    }
                }
                result.Cancelled = cancelled;
                result.Iterations = iterations;

                for (int g = 0; g < groupCount; g++)
                {
                    bool limited = false;
                    for (int k = memberStart[g]; k < memberStart[g + 1]; k++)
                    {
                        int i = members[k];
                        if (current[i] > 0f) result.Particles++;
                        if (current[i] > result.MaxPush) result.MaxPush = current[i];
                        if (current[i] < target[i] - 1e-6f)
                        {
                            result.Limited++;
                            limited = true;
                        }
                    }
                    if (limited) result.LimitedClusters++;
                }
            }

            result.RemainingViolation = result.Particles > 0 ? solver.MeasureBodyViolation() : 0f;
            result.MaxStretch = solver.MeasureMaxStretch();
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }

        // 粒子ごとの、しわの深さ: その粒子をはさむ1つおきの組（曲げの制約）が、元の長さより縮んだ割合の一番大きいもの（縮んでいなければ 0）
        static void MeasureFold(ClothModel model, ReadOnlySpan<Vector3> positions, float[] result)
        {
            Array.Clear(result, 0, result.Length);
            int[] pairs = model.BendPairs;
            float[] rest = model.BendRestLengths;
            if (pairs == null || rest == null) return;
            for (int k = 0; k < rest.Length; k++)
            {
                if (!(rest[k] > 1e-8f)) continue;
                int a = pairs[2 * k], b = pairs[2 * k + 1];
                float dx = positions[a].X - positions[b].X, dy = positions[a].Y - positions[b].Y, dz = positions[a].Z - positions[b].Z;
                float shrink = 1f - MathF.Sqrt(dx * dx + dy * dy + dz * dz) / rest[k];
                if (!(shrink > 0f)) continue;
                if (shrink > result[a]) result[a] = shrink;
                if (shrink > result[b]) result[b] = shrink;
            }
        }

        static void Apply(float level, float[] target, float[] current, int[] members, int from, int to)
        {
            for (int k = from; k < to; k++)
            {
                int i = members[k];
                current[i] = Math.Min(target[i], level);
            }
        }

        static float Progress(int iterations, Stopwatch timer, PoseEscapeOptions options)
        {
            float byWork = iterations / (float)Math.Max(1, options.MaxIterations);
            float byTime = (float)(timer.Elapsed.TotalMilliseconds / Math.Max(1.0, options.TimeLimitMilliseconds));
            return Math.Min(1f, Math.Max(byWork, byTime));
        }

        // 粒子ごとに、元の位置から外向きに current だけ外の平面を置く（平面の下限は体の余白なので、その分だけ手前に置く）。
        // current が 0 の粒子も、元の位置より内へは入らないように平面を置く
        static void SetPlanes(ClothSolver solver, List<int> particles, Vector3[] start, Vector3[] normals, float[] current)
        {
            float margin = solver.BodyMargin;
            for (int k = 0; k < particles.Count; k++)
            {
                int i = particles[k];
                Vector3 p = start[i], nrm = normals[i];
                float offset = current[i] - margin;
                solver.AddBodyExtraPlane(i, new Vector3(p.X + nrm.X * offset, p.Y + nrm.Y * offset, p.Z + nrm.Z * offset), nrm);
            }
        }

        // 伸びの制約でつながった粒子の一覧（粒子 i のつながりは adjacency[start[i]..start[i+1]]、その元の長さは length）
        static void BuildAdjacency(ClothModel model, out int[] start, out int[] adjacency, out float[] length)
        {
            int n = model.ParticleCount;
            int[] pairs = model.StretchPairs;
            float[] rest = model.StretchRestLengths;
            start = new int[n + 1];
            for (int k = 0; k < pairs.Length; k++) start[pairs[k] + 1]++;
            for (int i = 0; i < n; i++) start[i + 1] += start[i];
            adjacency = new int[pairs.Length];
            length = new float[pairs.Length];
            var fill = (int[])start.Clone();
            for (int k = 0; k < pairs.Length / 2; k++)
            {
                int a = pairs[2 * k], b = pairs[2 * k + 1];
                length[fill[a]] = rest[k];
                adjacency[fill[a]++] = b;
                length[fill[b]] = rest[k];
                adjacency[fill[b]++] = a;
            }
        }

        // 量のある粒子（seeds）を、つながったまとまり（所）に分ける。所の番号を粒子ごとに返す（所に入らない粒子は -1）
        static int[] GroupSeeds(int n, int[] start, int[] adjacency, List<int> seeds, out int groupCount)
        {
            var group = new int[n];
            for (int i = 0; i < n; i++) group[i] = -1;
            var isSeed = new bool[n];
            foreach (int i in seeds) isSeed[i] = true;
            var queue = new Queue<int>();
            groupCount = 0;
            foreach (int seed in seeds)
            {
                if (group[seed] >= 0) continue;
                int id = groupCount++;
                group[seed] = id;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    for (int e = start[i]; e < start[i + 1]; e++)
                    {
                        int j = adjacency[e];
                        if (!isSeed[j] || group[j] >= 0) continue;
                        group[j] = id;
                        queue.Enqueue(j);
                    }
                }
            }
            return group;
        }

        // 量を、半径 radius の中の周りの粒子へ、縁に向かってなめらかに減らしながら広げる（布に沿った距離で測る）。
        // 量が minPush を超えた粒子は、一番多く量をくれた粒子の所（group）に入れる。量のある粒子は、広がってきた量のほうが大きければそれにする（くぼみを作らない）
        static void Feather(int n, int[] start, int[] adjacency, float[] length, List<int> seeds, float[] target, Vector3[] normals, int[] group, float radius, float minPush)
        {
            if (!(radius > 0f)) return;
            var isSeed = new bool[n];
            foreach (int i in seeds) isSeed[i] = true;
            var feathered = new float[n];
            var source = new int[n];
            var distance = new float[n];
            var stamp = new int[n];
            var queue = new Queue<int>();
            for (int k = 0; k < seeds.Count; k++)
            {
                int seed = seeds[k];
                int mark = k + 1;
                stamp[seed] = mark;
                distance[seed] = 0f;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    for (int e = start[i]; e < start[i + 1]; e++)
                    {
                        int j = adjacency[e];
                        if (normals[j] == Vector3.Zero) continue;   // ピン留め・向きが決まらない粒子は動かさない
                        float d = distance[i] + length[e];
                        if (!(d < radius)) continue;
                        if (stamp[j] == mark && distance[j] <= d) continue;
                        stamp[j] = mark;
                        distance[j] = d;
                        queue.Enqueue(j);
                        float x = d / radius;
                        float value = target[seed] * (1f - x * x * (3f - 2f * x));
                        if (value > feathered[j])
                        {
                            feathered[j] = value;
                            source[j] = seed;
                        }
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (isSeed[i])
                {
                    if (feathered[i] > target[i]) target[i] = feathered[i];
                    continue;
                }
                if (!(feathered[i] > minPush)) continue;
                target[i] = feathered[i];
                group[i] = group[source[i]];
            }
        }

        // 所ごとの粒子と、周り（NeighborRings 回たどった範囲。所の粒子を含む）の一覧
        static void ListGroups(int n, int[] start, int[] adjacency, List<int> pushed, int[] group, int groupCount,
            out int[] memberStart, out int[] members, out int[] aroundStart, out int[] around)
        {
            memberStart = new int[groupCount + 1];
            foreach (int i in pushed) memberStart[group[i] + 1]++;
            for (int g = 0; g < groupCount; g++) memberStart[g + 1] += memberStart[g];
            members = new int[pushed.Count];
            var fill = (int[])memberStart.Clone();
            foreach (int i in pushed) members[fill[group[i]]++] = i;

            var mark = new int[n];
            for (int i = 0; i < n; i++) mark[i] = -1;
            aroundStart = new int[groupCount + 1];
            var list = new List<int>();
            var frontier = new List<int>();
            var next = new List<int>();
            for (int g = 0; g < groupCount; g++)
            {
                aroundStart[g] = list.Count;
                frontier.Clear();
                for (int k = memberStart[g]; k < memberStart[g + 1]; k++)
                {
                    int i = members[k];
                    mark[i] = g;
                    list.Add(i);
                    frontier.Add(i);
                }
                for (int ring = 0; ring < NeighborRings && frontier.Count > 0; ring++)
                {
                    next.Clear();
                    foreach (int i in frontier)
                    {
                        for (int e = start[i]; e < start[i + 1]; e++)
                        {
                            int j = adjacency[e];
                            if (mark[j] == g) continue;
                            mark[j] = g;
                            list.Add(j);
                            next.Add(j);
                        }
                    }
                    frontier.Clear();
                    frontier.AddRange(next);
                }
            }
            aroundStart[groupCount] = list.Count;
            around = list.ToArray();
        }
    }
}
