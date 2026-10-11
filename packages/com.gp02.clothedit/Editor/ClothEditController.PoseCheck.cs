using System;
using System.Collections.Generic;
using System.Diagnostics;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    // 動作チェック: いろいろなポーズを自動で試して、体が服を突き抜ける所を見つける。見つけた所を元のポーズで外へ逃がす（第1段の修正）。
    // アバターの複製（PoseRig）にポーズを付けて調べるので、シーンのアバターは触らない。画面に依存しないので、EditMode テストから動かせる。
    // 調べるのは EditorApplication.update ごとに少しずつ（Tick）。テストと「外へ逃がす」のあとの調べ直しは、一度に最後まで（RunPoseCheck）。
    // ほかの操作をしたら（FinishStroke・Undo・編集の終了）、調べている途中ならやめ、見せているポーズは元の姿勢に戻す
    public sealed partial class ClothEditController
    {
        /// <summary>動作チェックで、1回の更新（Tick）に使う時間の目安（ミリ秒）。最低でも1ポーズは調べる。</summary>
        public const double PoseCheckFrameMilliseconds = 30.0;

        /// <summary>「順に再生」で、1つのポーズを見せる時間（秒）。</summary>
        public const double PosePlaySeconds = 1.0;

        // ポーズで動く割合を測るときに、試しの形で外へ動かす量（メートル。スキニングは線形なので、割合はこの量によらない）
        const float PoseProbeDistance = 0.01f;

        // 外へ逃がしたあと、逃がした所の深さ（平均）がこの割合以下にならなければ、効かなかったとみなして戻す
        const float MinEscapeImprovement = 0.6f;

        sealed class PoseCheckJob
        {
            public PoseRig Rig;
            public ClothEditSession Session;
            public PoseCheckResult Result;
            public readonly List<PoseCheckEntry> Steps = new List<PoseCheckEntry>();
            public int Next = -1;       // -1: 基準の姿勢をまだ調べていない
            public BodySurface Body;
            public NVector3[] Combined, Welded, BodyVertices;
            public NVector3[] ProbeCombined, ProbeWelded;   // 試しの形（無ければ null）
            public float[] Baseline;
            public PoseCheckOptions Options;
            public int[] Starts;
            public Stopwatch Timer;
            public BodyAnchors Anchors;     // 基準の姿勢で測った、布の頂点ごとの一番近い体の点（体が無ければ null）
            public bool[] RestOverlap;      // 基準の姿勢で、体にぴったり付いていっても体の中と出る頂点（取り違えなので、ポーズでも体どうしの重なりにしない）
            public bool[] Covered;          // 基準の姿勢で、布に覆われていた体の頂点（覆われていなかった部品が布の外に来ても、布を通り抜けたのではないので数えない）
            public bool[] Loose;            // 基準の姿勢で体から離れている、布の頂点（伸びの目安はここだけで測る。体にぴったりの所は体と一緒に伸びてよい）
            public NVector3[] RestWelded;   // 基準の姿勢の、まとめた後の頂点の位置（伸びの基準）

            public bool Done => Next >= Steps.Count;
        }

        PoseCheckJob _poseJob;
        PoseCheckResult _poseCheck;
        PoseRig _poseRig;                 // 最後の結果のアバターの複製（ポーズを見せるのに使う）
        int _posePreviewEntry = -1;
        bool _posePlaying;
        bool _posePlayClockPending;
        double _posePlayStart;
        readonly List<int> _posePlayList = new List<int>();

        /// <summary>最後の動作チェックの結果。まだ調べていなければ null（形を変えたあとも残る。<see cref="IsPoseCheckStale"/> を見ること）。</summary>
        public PoseCheckResult LastPoseCheck => _session != null ? _poseCheck : null;

        /// <summary>最後の「元のポーズで外へ逃がす」の結果。</summary>
        public PoseEscapeReport LastPoseEscape { get; private set; }

        /// <summary>直前の動作チェックを始められなかった・途中で失敗したときの理由。</summary>
        public string LastPoseCheckError { get; private set; } = "";

        /// <summary>動作チェックの途中か。</summary>
        public bool IsPoseCheckRunning => _poseJob != null;

        /// <summary>動作チェックの進み具合（0〜1）。調べていなければ 0。</summary>
        public float PoseCheckProgress => _poseJob == null ? 0f : (_poseJob.Next + 1f) / (_poseJob.Steps.Count + 1f);

        /// <summary>調べている途中の、済んだ数と全体の数（基準の姿勢を含む）。</summary>
        public void GetPoseCheckCounts(out int done, out int total)
        {
            done = _poseJob != null ? _poseJob.Next + 1 : 0;
            total = _poseJob != null ? _poseJob.Steps.Count + 1 : 0;
        }

        /// <summary>結果を調べたあとに、形が変わった・保存した（編集を作り直した）か。古い結果は、もう一度チェックするまで外へ逃がせない。</summary>
        public bool IsPoseCheckStale => _poseCheck != null && (_session == null || _poseCheck.Session != _session || _poseCheck.ShapeVersion != _session.ShapeVersion);

        /// <summary>動作チェックが使えない理由。使えれば null（編集中で、衣装が Humanoid のアバターの中にあること）。</summary>
        public string GetPoseCheckProblem()
        {
            if (_session == null) return L10n.Tr("編集を始めると使えます。", "Available after you start editing.");
            return PoseRig.GetProblem(_session.Renderer);
        }

        /// <summary>
        /// 粒子ごとの、全ポーズでの最悪の深さ（メートル）。結果が無い・粒子の数が合わないときは null。書き換えないこと。
        /// </summary>
        public float[] PoseCheckParticleDepth
        {
            get
            {
                if (_session == null || _poseCheck == null || _poseCheck.ParticleDepth == null) return null;
                return _poseCheck.ParticleDepth.Length == _session.ParticleCount ? _poseCheck.ParticleDepth : null;
            }
        }

        /// <summary>
        /// 動作チェックを始める（Tick ごとに少しずつ進む）。途中の操作は確定してから行う。始められなければ false で、理由を error に入れる。
        /// </summary>
        public bool BeginPoseCheck(out string error) => BeginPoseCheck(out error, null);

        // sameAs があれば、その結果と同じポーズ・段階・クリップのコマ・許容の深さで調べる（外へ逃がしたあとの調べ直しで、前後を結果の番号で比べるため）
        bool BeginPoseCheck(out string error, PoseCheckResult sameAs)
        {
            error = null;
            if (!EnsureAlive())
            {
                error = GetPoseCheckProblem();
                LastPoseCheckError = error;
                return false;
            }
            FinishStroke();
            error = GetPoseCheckProblem();
            List<PosePreset> poses = sameAs != null ? sameAs.GetPoses() : _settings.GetEnabledPoses();
            bool hasClips = sameAs != null ? sameAs.ClipSampleCount > 0 : _settings.GetPoseCheckClips().Count > 0;
            if (error == null && poses.Count == 0 && !hasClips)
            {
                error = L10n.Tr("試すポーズがありません。くわしく表示の「試すポーズ」で選んでください。", "There are no poses to try. Choose them under \"Poses to Try\" in the detailed view.");
            }
            if (error != null)
            {
                LastPoseCheckError = error;
                return false;
            }

            var timer = Stopwatch.StartNew();
            PoseRig rig = PoseRig.Create(_session.Renderers, out error);
            if (rig == null)
            {
                LastPoseCheckError = error;
                return false;
            }
            PoseCheckJob job;
            try
            {
                job = PrepareJob(rig, poses, sameAs, timer, null);
            }
            catch (Exception ex)
            {
                rig.Dispose();
                Debug.LogException(ex);
                error = L10n.Tr("動作チェックの準備ができませんでした。Console のエラーを確認してください。\n", "Could not prepare the motion check. Check the errors in the Console.\n") + ex.Message;
                LastPoseCheckError = error;
                return false;
            }
            _poseJob = job;
            LastPoseCheckError = "";
            Notify();
            return true;
        }

        // candidate があれば、衣装の複製にその候補のウェイトを入れて調べる（ウェイトを体に寄せる前の確認）
        PoseCheckJob PrepareJob(PoseRig rig, List<PosePreset> poses, PoseCheckResult sameAs, Stopwatch timer, WeightFixPreview candidate)
        {
            ClothEditSession session = _session;
            WeightFixCandidate CandidateOf(int r) => candidate != null && r < candidate.Candidates.Count ? candidate.Candidates[r] : null;
            for (int r = 0; r < session.RendererCount; r++)
            {
                WeightFixCandidate c = CandidateOf(r);
                rig.SetTargetMesh(r, session.CreateEditedMesh(r, null, c), c != null ? c.AddedBones : null);
            }
            // 試しの形: 編集の姿勢で、全頂点を外向きに少し動かしたもの。貫通したポーズで一緒に取り出し、外へ動かした分がポーズでどれだけ残るかを測る
            // （ひざ・ひじを大きく曲げると、ウェイトの混ざった所では元の姿勢で動かした分がほとんど残らない。そこは外へ逃がしても直らない）
            var offsets = new NVector3[session.WeldedVertexCount];
            session.ComputeEditOutwardNormals(offsets);
            for (int w = 0; w < offsets.Length; w++) offsets[w] *= PoseProbeDistance;
            for (int r = 0; r < session.RendererCount; r++) rig.SetProbeMesh(r, session.CreateEditedMesh(r, offsets, CandidateOf(r)));
            float[] stages = sameAs != null ? sameAs.GetStages() : _settings.GetPoseStages();
            PoseCheckOptions options = _settings.BuildPoseCheckOptions();
            if (sameAs != null) options.Tolerance = sameAs.Tolerance;
            var job = new PoseCheckJob
            {
                Rig = rig,
                Session = session,
                Options = options,
                Starts = session.GetVertexStarts(),
                Combined = new NVector3[session.VertexCount],
                Welded = new NVector3[session.WeldedVertexCount],
                BodyVertices = new NVector3[rig.BodyVertexCount],
                Baseline = new float[session.WeldedVertexCount],
                Timer = timer,
            };
            if (rig.HasProbes)
            {
                job.ProbeCombined = new NVector3[session.VertexCount];
                job.ProbeWelded = new NVector3[session.WeldedVertexCount];
            }
            var targets = new Renderer[session.RendererCount];
            for (int r = 0; r < targets.Length; r++) targets[r] = session.Renderers[r];
            job.Result = new PoseCheckResult
            {
                Summary = new PoseCheckSummary(session.WeldedVertexCount),
                Session = session,
                ShapeVersion = session.ShapeVersion,
                Targets = targets,
                PoseCount = poses.Count,
                StageCount = stages.Length,
                Tolerance = job.Options.Tolerance,
                BodyDescription = rig.BodyDescription,
                FollowerCount = rig.FollowerCount,
                UnmatchedBoneCount = rig.UnmatchedBoneCount,
                GainsMeasured = job.ProbeCombined != null,
            };

            // 体の表面は、基準の姿勢で1回だけ作る（ポーズごとには頂点の位置だけを入れ替える）
            rig.ApplyBase();
            if (rig.BodyVertexCount > 0 && rig.BodyTriangles.Length > 0)
            {
                rig.BakeBody(job.BodyVertices);
                job.Body = new BodySurface(job.BodyVertices, rig.BodyTriangles);
                if (rig.BodyIsFallback)
                {
                    // 体らしいレンダラーが無いときは、ほかのレンダラーのうち布の内側にある部品だけを使う（外側の上着や髪を体とみなさない）
                    rig.BakeTargets(job.Combined, job.Starts);
                    session.ToWeldedPositions(job.Combined, job.Welded);
                    job.Body.ClassifyIslands(job.Welded);
                }
                else
                {
                    job.Body.EnableSolidIslands();
                }
                if (job.Body.EnabledIslandCount == 0) job.Body = null;
            }
            job.Result.HasBody = job.Body != null;

            IReadOnlyList<PosePreset> all = PoseLibrary.All;
            foreach (PosePreset pose in poses)
            {
                int poseIndex = -1;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] == pose) poseIndex = i;
                }
                foreach (float stage in stages)
                {
                    job.Steps.Add(new PoseCheckEntry
                    {
                        Pose = pose,
                        PoseIndex = poseIndex,
                        Stage = stage,
                        Muscles = PoseLibrary.Blend(pose, rig.BaseMuscles, stage),
                        Result = new PosePenetration { Group = poseIndex, Stage = stage },
                    });
                }
            }
            AddClipSteps(job, rig, sameAs);
            job.Result.PoseCount = poses.Count + job.Result.ClipSampleCount;
            job.Result.PrepareMilliseconds = timer.Elapsed.TotalMilliseconds;
            return job;
        }

        // 手持ちの AnimationClip のコマを足す。Humanoid のクリップは筋肉の値で、Generic のクリップはボーンのパスが合えば、そのコマを当てて試す。
        // sameAs があれば、その結果と同じクリップ・同じ時刻
        void AddClipSteps(PoseCheckJob job, PoseRig rig, PoseCheckResult sameAs)
        {
            var samples = new List<(AnimationClip Clip, float Time)>();
            if (sameAs != null)
            {
                foreach (PoseCheckEntry entry in sameAs.Entries)
                {
                    if (entry.Clip != null) samples.Add((entry.Clip, entry.ClipTime));
                }
            }
            else
            {
                foreach (AnimationClip clip in _settings.GetPoseCheckClips())
                {
                    bool humanoid = PoseRig.HumanoidCurves(clip).Count > 0;
                    if (!humanoid)
                    {
                        rig.CountGenericPaths(clip, out int matched, out int total);
                        if (matched == 0)
                        {
                            job.Result.ClipNotes.Add(L10n.Tr($"「{clip.name}」は、Humanoid のクリップでなく、ボーンのパスもこのアバターに合わないので使いません。",
                                $"\"{clip.name}\" is not a Humanoid clip and its bone paths do not match this avatar, so it is not used."));
                            continue;
                        }
                        if (matched < total)
                        {
                            job.Result.ClipNotes.Add(L10n.Tr($"「{clip.name}」（Generic）: ボーンのパス {total} 個のうち {matched} 個が合うので、その分だけ動かします。",
                                $"\"{clip.name}\" (Generic): {matched} of {total} bone paths match, so only those move."));
                        }
                    }
                    foreach (float time in ClothEditSettings.ClipSampleTimes(clip, _settings.PoseCheckClipInterval)) samples.Add((clip, time));
                }
            }
            int group = PoseLibrary.All.Count;
            foreach ((AnimationClip clip, float time) in samples)
            {
                float[] muscles = PoseRig.SampleHumanoidMuscles(clip, time, rig.BaseMuscles);
                job.Steps.Add(new PoseCheckEntry
                {
                    Clip = clip,
                    ClipTime = time,
                    PoseIndex = -1,
                    Stage = 1f,
                    Muscles = muscles,
                    Result = new PosePenetration { Group = group, Stage = 1f },
                });
                group++;
            }
            job.Result.ClipSampleCount = samples.Count;
        }

        // 1つ進める（最初は基準の姿勢）。終わったら true
        bool StepPoseCheck(PoseCheckJob job)
        {
            PoseCheckEvaluator evaluator = job.Session.PoseEvaluator;
            if (job.Next < 0)
            {
                job.Rig.ApplyBase();
                job.Rig.BakeTargets(job.Combined, job.Starts);
                job.Session.ToWeldedPositions(job.Combined, job.Welded);
                evaluator.MeasureDepth(job.Welded, job.Body, job.Options, job.Baseline);
                evaluator.Evaluate(job.Welded, job.Body, job.Options, null, job.Result.Baseline);
                job.RestWelded = (NVector3[])job.Welded.Clone();
                if (job.Body != null)
                {
                    // 布の頂点ごとの、一番近い体の点（布の外側にある体の部品は使わない）。体どうしの重なりの判定と、ウェイトを体に寄せるのに使う
                    var normals = new NVector3[job.Welded.Length];
                    evaluator.ComputeOutwardNormals(job.Welded, normals);
                    job.Anchors = BodyAnchors.Measure(job.Welded, job.Body, BodyAnchors.DefaultMaxDistance, normals);
                    job.RestOverlap = new bool[job.Welded.Length];
                    evaluator.MeasureFollowOverlap(job.Body, job.Anchors, job.Options, job.RestOverlap);
                    job.Covered = new bool[job.Body.VertexCount];
                    evaluator.MeasureCoveredBodyVertices(job.Welded, job.Body, job.Options, job.Covered);
                    job.Loose = new bool[job.Welded.Length];
                    for (int i = 0; i < job.Loose.Length; i++)
                    {
                        job.Loose[i] = !job.Anchors.Has(i) || job.Anchors.GetDistance(i) > WeightTransferOptions.DefaultFullDistance;
                    }
                }
                job.Next = 0;
                return job.Done;
            }

            PoseCheckEntry entry = job.Steps[job.Next];
            job.Rig.ApplyEntry(entry);
            if (job.Body != null)
            {
                job.Rig.BakeBody(job.BodyVertices);
                job.Body.SetVertexPositions(job.BodyVertices);
            }
            job.Rig.BakeTargets(job.Combined, job.Starts);
            job.Session.ToWeldedPositions(job.Combined, job.Welded);
            evaluator.Evaluate(job.Welded, job.Body, job.Options, job.Baseline, entry.Result, job.Anchors, job.RestOverlap, job.Covered);
            entry.Result.MaxStretch = evaluator.MeasureStretch(job.RestWelded, job.Welded, job.Loose, out entry.Result.HighStretch);
            if (entry.Result.IsPenetrating && job.ProbeCombined != null)
            {
                job.Rig.BakeProbes(job.ProbeCombined, job.Starts);
                job.Session.ToWeldedPositions(job.ProbeCombined, job.ProbeWelded);
                evaluator.MeasureGains(job.Welded, job.ProbeWelded, PoseProbeDistance, entry.Result);
            }
            job.Result.Summary.Add(entry.Result);
            job.Result.Entries.Add(entry);
            job.Next++;
            return job.Done;
        }

        // 調べ終わった結果の仕上げ（粒子ごとの最悪の深さ、外へ逃がす量、ウェイトを体に寄せる対象）。結果を今の結果にはしない
        static void FinalizePoseCheck(PoseCheckJob job)
        {
            job.Rig.ApplyBase();
            PoseCheckResult result = job.Result;
            ParticleCloth cloth = job.Session.Cloth;
            result.ParticleDepth = new float[cloth.Model.ParticleCount];
            result.Summary.ToParticleDepth(cloth, result.ParticleDepth);
            result.Anchors = job.Anchors;
            result.Body = job.Body;
            result.RestWelded = job.RestWelded;
            if (result.HasBody)
            {
                var defaults = new PoseEscapeOptions();
                result.EscapePlan = result.Summary.PlanEscape(defaults.Extra, defaults.MaxPush, defaults.MinGain, job.Anchors, WeightTransferOptions.DefaultFullDistance);
                result.WeightPlan = result.Summary.PlanWeightFix(defaults.Extra, defaults.MaxPush, defaults.MinGain, job.Anchors, WeightTransferOptions.DefaultFullDistance);
            }
            result.Milliseconds = job.Timer.Elapsed.TotalMilliseconds;
        }

        void FinishPoseCheck(PoseCheckJob job)
        {
            FinalizePoseCheck(job);
            PoseCheckResult result = job.Result;
            _poseJob = null;
            InstallPoseCheck(result, job.Rig);
            int steps = Math.Max(1, result.Entries.Count);
            ClothEditLog.Info($"[GP02] {job.Session.DescribeTargets()}: 動作チェック {result.DescribeSummary()} / ポーズ {result.PoseCount} × {result.StageCount} 段階（うちクリップのコマ {result.ClipSampleCount}）" +
                $" / 時間 合計 {result.Milliseconds:F0} ms（準備 {result.PrepareMilliseconds:F0} ms、1ポーズ平均 {(result.Milliseconds - result.PrepareMilliseconds) / steps:F0} ms）" +
                $" / {result.BodyDescription} / 服のボーン: アバターの動きをうつす {result.FollowerCount}、親に付いていく {result.UnmatchedBoneCount}" +
                (result.EscapePlan != null ? $" / 外へ逃がせるか所 {result.EscapePlan.PushedRegions}（ウェイトが原因 {result.EscapePlan.LowGainRegions}、深すぎる {result.EscapePlan.TooFarRegions}、体にぴったり {result.EscapePlan.TightRegions}）" : "") +
                $" / 体どうしの重なり {result.OverlapRegionCount} か所（{result.OverlapPoseCount} ポーズ）/ 体から離れた所の伸び（上位1%）{result.HighStretch * 100f:F1}%（最大 {result.MaxStretch * 100f:F1}%）" +
                (result.Anchors != null ? $" / 体の点が見つかった頂点 {result.Anchors.Count} / {result.Anchors.VertexCount}" : "") +
                $" / 基準の姿勢の貫通: {result.Baseline}" + (result.ClipNotes.Count > 0 ? " / " + string.Join(" ", result.ClipNotes) : ""));
            Notify();
        }

        // 結果とアバターの複製を、今の結果にする（前の複製は消す）
        void InstallPoseCheck(PoseCheckResult result, PoseRig rig)
        {
            if (_poseRig != null && _poseRig != rig) _poseRig.Dispose();
            _poseRig = rig;
            _poseCheck = result;
        }

        // 今の結果を変えずに、最後まで調べる（ウェイトを体に寄せる前の確認）。調べたアバターの複製は rig に返す（消すのは呼び出し側）。調べられなければ null
        PoseCheckResult RunDetachedPoseCheck(PoseCheckResult sameAs, WeightFixPreview candidate, out PoseRig rig, out string error)
        {
            rig = PoseRig.Create(_session.Renderers, out error);
            if (rig == null) return null;
            try
            {
                var timer = Stopwatch.StartNew();
                PoseCheckJob job = PrepareJob(rig, sameAs.GetPoses(), sameAs, timer, candidate);
                while (!StepPoseCheck(job))
                {
                }
                FinalizePoseCheck(job);
                return job.Result;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                rig.Dispose();
                rig = null;
                error = L10n.Tr("候補のウェイトで動作チェックをやり直せませんでした。Console のエラーを確認してください。\n", "Could not run the motion check with the candidate weights. Check the errors in the Console.\n") + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 動作チェックを最後まで行い、結果を返す（途中で画面を更新しない。テストと、外へ逃がしたあとの調べ直し用）。始められなければ null（理由は <see cref="LastPoseCheckError"/>）。
        /// </summary>
        public PoseCheckResult RunPoseCheck() => RunPoseCheck(null);

        PoseCheckResult RunPoseCheck(PoseCheckResult sameAs)
        {
            if (!BeginPoseCheck(out _, sameAs)) return null;
            PoseCheckResult running = _poseJob.Result;
            AdvancePoseCheck(double.PositiveInfinity);
            return _poseCheck == running ? running : null;
        }

        // 持ち時間の分だけ進める（最低でも1つ）
        void AdvancePoseCheck(double budgetMilliseconds)
        {
            PoseCheckJob job = _poseJob;
            if (job == null) return;
            var timer = Stopwatch.StartNew();
            try
            {
                do
                {
                    if (!StepPoseCheck(job)) continue;
                    FinishPoseCheck(job);
                    return;
                } while (timer.Elapsed.TotalMilliseconds < budgetMilliseconds);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                CancelPoseCheck();
                LastPoseCheckError = L10n.Tr("動作チェックの途中で問題が起きたので、やめました。Console のエラーを確認してください。", "The motion check stopped because of a problem. Check the errors in the Console.");
                return;
            }
            Notify();
        }

        /// <summary>調べている途中の動作チェックをやめる（前の結果は残る）。</summary>
        public void CancelPoseCheck()
        {
            if (_poseJob == null) return;
            PoseRig rig = _poseJob.Rig;
            _poseJob = null;
            if (rig != null && rig != _poseRig) rig.Dispose();
            Notify();
        }

        // Tick から呼ぶ（つまむ・仕上げの計算の前）
        void TickPoseCheck(double now)
        {
            if (_poseJob != null) AdvancePoseCheck(PoseCheckFrameMilliseconds);
            if (_posePlaying) TickPosePlayback(now);
        }

        // ほかの操作の前に呼ぶ: 調べている途中ならやめ、見せているポーズは元の姿勢に戻す
        void StopPoseActivity()
        {
            CancelPoseCheck();
            EndPosePreview();
        }

        // 編集を終えるときに呼ぶ: アバターの複製を消す（結果は、同じ衣装の編集を作り直したとき（保存など）のために残す）
        void DisposePoseCheckRig()
        {
            StopPoseActivity();
            if (_poseRig != null)
            {
                _poseRig.Dispose();
                _poseRig = null;
            }
            if (_weightPreview != null) _weightPreview.DisposeRig();
        }

        // 編集を始めたとき（作り直しを含む）に呼ぶ: 別の衣装の結果なら捨てる
        void ForgetPoseCheckOfOtherTargets()
        {
            if (_poseCheck == null) return;
            Renderer[] previous = _poseCheck.Targets;
            bool same = previous != null && previous.Length == _targets.Count;
            for (int r = 0; same && r < previous.Length; r++) same = previous[r] == _targets[r];
            if (same) return;
            _poseCheck = null;
            LastPoseEscape = null;
            DiscardWeightFixPreview();
            LastWeightFix = null;
        }

        // ---- ポーズを見せる ----

        /// <summary>結果のポーズを見せられるか（結果があり、古くなく、アバターの複製が残っている）。</summary>
        public bool CanPreviewPoses => _poseCheck != null && _poseRig != null && !_poseRig.IsDisposed && !IsPoseCheckStale && _poseCheck.Entries.Count > 0;

        /// <summary>ポーズを見せているか（シーンでアバターの複製をそのポーズにし、元のアバターを隠している）。</summary>
        public bool IsPosePreviewActive => _posePreviewEntry >= 0;

        /// <summary>見せている結果の番号（<see cref="PoseCheckResult.Entries"/>）。見せていなければ -1。</summary>
        public int PosePreviewEntry => _posePreviewEntry;

        /// <summary>見せている結果。見せていなければ null。</summary>
        public PoseCheckEntry PosePreview => _posePreviewEntry >= 0 && _poseCheck != null && _posePreviewEntry < _poseCheck.Entries.Count ? _poseCheck.Entries[_posePreviewEntry] : null;

        /// <summary>「順に再生」の途中か。</summary>
        public bool IsPosePlaying => _posePlaying;

        /// <summary>見せているアバターの複製（テスト用）。結果が無ければ null。</summary>
        public PoseRig PoseCheckRig => _poseRig;

        /// <summary>
        /// 結果の entry 番目のポーズを、シーンで見せる（アバターの複製をそのポーズにし、元のアバターを隠す）。見せられなければ false。
        /// 戻すのは <see cref="EndPosePreview"/>（ほかの操作をしたときも戻る）。
        /// </summary>
        public bool ShowPoseEntry(int entry)
        {
            if (!CanPreviewPoses || (uint)entry >= (uint)_poseCheck.Entries.Count) return false;
            _poseRig.ApplyEntry(_poseCheck.Entries[entry]);
            _poseRig.Show();
            _posePreviewEntry = entry;
            Notify();
            return true;
        }

        /// <summary>「順に見る」: 貫通が見つかったポーズを、悪い順に1つずつ見せる（最後まで行ったら最初へ）。見せた結果の番号を返す。無ければ -1。</summary>
        public int ShowNextPenetratingPose()
        {
            if (!CanPreviewPoses) return -1;
            _posePlaying = false;
            int[] ranked = _poseCheck.RankEntries();
            if (ranked.Length == 0) return -1;
            int position = Array.IndexOf(ranked, _posePreviewEntry);
            int next = ranked[(position + 1) % ranked.Length];
            return ShowPoseEntry(next) ? next : -1;
        }

        /// <summary>「順に再生」: ポーズを順に（1つ約1秒）見せる。2段階で調べたときは「全部」の段階を見せる。終わったら元の姿勢に戻る。</summary>
        public bool PlayPoses()
        {
            if (!CanPreviewPoses) return false;
            _posePlayList.Clear();
            for (int k = 0; k < _poseCheck.Entries.Count; k++)
            {
                if (_poseCheck.Entries[k].Stage >= 1f || _poseCheck.StageCount == 1) _posePlayList.Add(k);
            }
            if (_posePlayList.Count == 0) return false;
            _posePlaying = true;
            _posePlayClockPending = true;
            ShowPoseEntry(_posePlayList[0]);
            return true;
        }

        void TickPosePlayback(double now)
        {
            if (_posePlayClockPending)
            {
                _posePlayStart = now;
                _posePlayClockPending = false;
            }
            int index = (int)((now - _posePlayStart) / PosePlaySeconds);
            if (index >= _posePlayList.Count || !CanPreviewPoses)
            {
                EndPosePreview();
                return;
            }
            if (_posePlayList[index] != _posePreviewEntry) ShowPoseEntry(_posePlayList[index]);
        }

        /// <summary>見せているポーズをやめて、元の姿勢に戻る（元のアバターの表示を戻す）。</summary>
        public void EndPosePreview()
        {
            bool wasShown = _posePreviewEntry >= 0 || _posePlaying || (_poseRig != null && _poseRig.IsShown);
            _posePlaying = false;
            _posePreviewEntry = -1;
            if (!wasShown) return;
            if (_poseRig != null && !_poseRig.IsDisposed)
            {
                _poseRig.Hide();
                _poseRig.ApplyBase();
            }
            Notify();
        }

        // ---- 修正（第1段）: 元のポーズで外へ逃がす ----

        /// <summary>外へ逃がせるか（新しい結果があり、逃がせるか所がある）。</summary>
        public bool CanEscapePoseCheck => _poseCheck != null && !IsPoseCheckStale && !IsPoseCheckRunning && _poseCheck.EscapePlan != null && _poseCheck.EscapePlan.PushedRegions > 0
            && _session != null && _poseCheck.EscapePlan.WeldedPush.Length == _session.WeldedVertexCount;

        /// <summary>
        /// 動作チェックで見つかった所を、編集の姿勢で、そのポーズで足りる量（深さ÷ポーズで動く割合）だけ外向きに逃がす（布の長さを保って周りも付いてくる）。
        /// 編集の姿勢で動かしてもポーズではほとんど動かない所（ウェイトが原因）と、深すぎる所は逃がさない（<see cref="PoseCheckSummary.PlanEscape"/>）。
        /// Unity の Undo の1段。終わったら、もう一度チェックして前後の件数を返す。逃がせなければ null。
        /// もう一度チェックして、逃がした所が浅くならない・全体の貫通が減らないときは、逃がす前の形に戻す（<see cref="PoseEscapeReport.Reverted"/>。
        /// ぴったりの服で布に余裕が無いと、外へ出した分だけ周りが引かれて、かえって増えることがあるため）。
        /// </summary>
        public PoseEscapeReport EscapePoseCheckPenetration()
        {
            if (!EnsureAlive() || !CanEscapePoseCheck) return null;
            FinishStroke();
            PoseEscapePlan plan = _poseCheck.EscapePlan;
            var push = new float[_session.ParticleCount];
            PoseCheckSummary.ToParticles(_session.Cloth, plan.WeldedPush, push);
            var report = new PoseEscapeReport
            {
                PushedRegions = plan.PushedRegions,
                LowGainRegions = plan.LowGainRegions,
                TightRegions = plan.TightRegions,
                TooFarRegions = plan.TooFarRegions,
                PosesBefore = _poseCheck.PenetratingPoseCount,
                RegionsBefore = _poseCheck.TotalRegionCount,
                VerticesBefore = _poseCheck.PenetratingVertexCount,
                MaxDepthBefore = _poseCheck.MaxDepth,
            };
            var options = new PoseEscapeOptions
            {
                StretchTolerance = _settings.SettleTolerance >= 0f ? _settings.SettleTolerance : ClothEditSettings.DefaultSettleTolerance,
                TimeLimitMilliseconds = ClothEditSettings.PenetrationFixMilliseconds,
            };
            if (ShowProgress)
            {
                options.Progress = progress => !EditorUtility.DisplayCancelableProgressBar("GP02",
                    L10n.Tr("ポーズで突き抜ける所を、元のポーズで外へ逃がしています…", "Moving the places that poses poke through outward in the original pose..."), progress);
            }
            PoseCheckResult previous = _poseCheck;
            BeginOperation(L10n.Tr("GP02 外へ逃がす", "GP02 Move Outward"));
            int group = _opGroup;
            try
            {
                report.Escape = _session.EscapeOutward(push, options);
            }
            finally
            {
                if (ShowProgress) EditorUtility.ClearProgressBar();
            }
            CommitOperation();
            AfterShapeChanged();

            if (ShowProgress) EditorUtility.DisplayProgressBar("GP02", L10n.Tr("もう一度、ポーズを試しています…", "Trying the poses again..."), 0.5f);
            PoseCheckResult after;
            try
            {
                after = RunPoseCheck(previous);
            }
            finally
            {
                if (ShowProgress) EditorUtility.ClearProgressBar();
            }
            if (after != null)
            {
                report.Rechecked = true;
                report.After = after;
                report.PosesAfter = after.PenetratingPoseCount;
                report.RegionsAfter = after.TotalRegionCount;
                report.VerticesAfter = after.PenetratingVertexCount;
                report.MaxDepthAfter = after.MaxDepth;
                // 同じポーズ・段階を同じ順に調べ直したので、逃がしたか所を結果の番号で比べられる
                report.Effect = plan.MeasureEffect(previous.Summary, after.Summary, PoseEscapeOptions.DefaultMaxPush);

                // 逃がした所が浅くならない・全体の貫通が減らないなら、逃がす前に戻す（Undo の記録も消す。形は前と同じなので、前の結果をそのまま使う）
                if (!report.Effect.Improved(MinEscapeImprovement) && _session != null)
                {
                    RevertRecords(group);
                    _session.ReadState(_state);
                    AfterShapeChanged();
                    previous.ShapeVersion = _session.ShapeVersion;
                    _poseCheck = previous;
                    if (_poseRig != null && !_poseRig.IsDisposed)
                    {
                        for (int r = 0; r < _session.RendererCount && r < _poseRig.TargetCount; r++) _poseRig.SetTargetMesh(r, _session.CreateEditedMesh(r));
                    }
                    report.Reverted = true;
                    Notify();
                }
            }
            LastPoseEscape = report;
            ClothEditLog.Info($"[GP02] {_session.DescribeTargets()}: 元のポーズで外へ逃がしました。{report}（{DescribeAccelerationForLog()}）");
            return report;
        }
    }
}
