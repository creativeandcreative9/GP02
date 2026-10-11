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

        // sameAs があれば、その結果と同じポーズ・段階・許容の深さで調べる（外へ逃がしたあとの調べ直しで、前後を結果の番号で比べるため）
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
            if (error == null && poses.Count == 0)
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
                job = PrepareJob(rig, poses, sameAs, timer);
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

        PoseCheckJob PrepareJob(PoseRig rig, List<PosePreset> poses, PoseCheckResult sameAs, Stopwatch timer)
        {
            ClothEditSession session = _session;
            for (int r = 0; r < session.RendererCount; r++) rig.SetTargetMesh(r, session.CreateEditedMesh(r));
            // 試しの形: 編集の姿勢で、全頂点を外向きに少し動かしたもの。貫通したポーズで一緒に取り出し、外へ動かした分がポーズでどれだけ残るかを測る
            // （ひざ・ひじを大きく曲げると、ウェイトの混ざった所では元の姿勢で動かした分がほとんど残らない。そこは外へ逃がしても直らない）
            var offsets = new NVector3[session.WeldedVertexCount];
            session.ComputeEditOutwardNormals(offsets);
            for (int w = 0; w < offsets.Length; w++) offsets[w] *= PoseProbeDistance;
            for (int r = 0; r < session.RendererCount; r++) rig.SetProbeMesh(r, session.CreateEditedMesh(r, offsets));
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
            job.Result.PrepareMilliseconds = timer.Elapsed.TotalMilliseconds;
            return job;
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
                job.Next = 0;
                return job.Done;
            }

            PoseCheckEntry entry = job.Steps[job.Next];
            job.Rig.ApplyMuscles(entry.Muscles);
            if (job.Body != null)
            {
                job.Rig.BakeBody(job.BodyVertices);
                job.Body.SetVertexPositions(job.BodyVertices);
            }
            job.Rig.BakeTargets(job.Combined, job.Starts);
            job.Session.ToWeldedPositions(job.Combined, job.Welded);
            evaluator.Evaluate(job.Welded, job.Body, job.Options, job.Baseline, entry.Result);
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

        void FinishPoseCheck(PoseCheckJob job)
        {
            job.Rig.ApplyBase();
            PoseCheckResult result = job.Result;
            ParticleCloth cloth = job.Session.Cloth;
            result.ParticleDepth = new float[cloth.Model.ParticleCount];
            result.Summary.ToParticleDepth(cloth, result.ParticleDepth);
            if (result.HasBody)
            {
                var defaults = new PoseEscapeOptions();
                result.EscapePlan = result.Summary.PlanEscape(defaults.Extra, defaults.MaxPush, defaults.MinGain);
            }
            result.Milliseconds = job.Timer.Elapsed.TotalMilliseconds;
            _poseJob = null;
            if (_poseRig != null && _poseRig != job.Rig) _poseRig.Dispose();
            _poseRig = job.Rig;
            _poseCheck = result;
            int steps = Math.Max(1, result.Entries.Count);
            Debug.Log($"[GP02] {job.Session.DescribeTargets()}: 動作チェック {result.DescribeSummary()} / ポーズ {result.PoseCount} × {result.StageCount} 段階" +
                $" / 時間 合計 {result.Milliseconds:F0} ms（準備 {result.PrepareMilliseconds:F0} ms、1ポーズ平均 {(result.Milliseconds - result.PrepareMilliseconds) / steps:F0} ms）" +
                $" / {result.BodyDescription} / 服のボーン: アバターの動きをうつす {result.FollowerCount}、親に付いていく {result.UnmatchedBoneCount}" +
                (result.EscapePlan != null ? $" / 外へ逃がせるか所 {result.EscapePlan.PushedRegions}（ウェイトが原因 {result.EscapePlan.LowGainRegions}、深すぎる {result.EscapePlan.TooFarRegions}）" : "") +
                $" / 基準の姿勢の貫通: {result.Baseline}");
            Notify();
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
            _poseRig.ApplyMuscles(_poseCheck.Entries[entry].Muscles);
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
            Debug.Log($"[GP02] {_session.DescribeTargets()}: 元のポーズで外へ逃がしました。{report}（{DescribeAccelerationForLog()}）");
            return report;
        }
    }
}
