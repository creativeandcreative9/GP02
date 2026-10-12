using System.Collections.Generic;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>動作チェックの、1つのポーズの1段階（または手持ちの AnimationClip の1コマ）の結果。</summary>
    public sealed class PoseCheckEntry
    {
        /// <summary>ポーズ。手持ちの AnimationClip のコマなら null。</summary>
        public PosePreset Pose;

        /// <summary>手持ちの AnimationClip（そのコマの結果のとき）。プリセットのポーズなら null。</summary>
        public AnimationClip Clip;

        /// <summary><see cref="Clip"/> のコマの時刻（秒）。</summary>
        public float ClipTime;

        /// <summary>ポーズの一覧（<see cref="PoseLibrary.All"/>）での番号。</summary>
        public int PoseIndex;

        /// <summary>段階（0.5 で半分、1 で全部）。</summary>
        public float Stage;

        /// <summary>このポーズを付けたときの筋肉の値（見せるときに使う）。Generic の AnimationClip のコマなら null（<see cref="PoseRig.ApplyEntry"/> でクリップから付ける）。</summary>
        public float[] Muscles;

        /// <summary>調べた結果。</summary>
        public PosePenetration Result;

        /// <summary>手持ちの AnimationClip のコマか。</summary>
        public bool IsClip => Clip != null;

        /// <summary>大きく動くポーズ（座る・しゃがむ・足を上げる・開く）の結果か。手持ちの AnimationClip のコマは、よく使うポーズとみなす。</summary>
        public bool IsLargeMotion => Pose != null && Pose.IsLargeMotion;

        /// <summary>ポーズの名前（設定の保存に使うもの）。クリップのコマなら空。</summary>
        public string PoseId => Pose != null ? Pose.Id : "";

        /// <summary>段階の名前（途中まで・最後まで）。「半分」「全部」だと何の半分か分かりにくいので、ポーズのどこまでかで言う。</summary>
        public static string StageName(float stage) => stage < 0.75f ? L10n.Tr("途中まで", "halfway") : L10n.Tr("最後まで", "full");

        /// <summary>クリップのコマの名前（例: Sit 0.5秒）。</summary>
        public static string ClipSampleName(AnimationClip clip, float time)
        {
            string name = clip != null ? clip.name : "?";
            return L10n.Tr($"{name} {time:0.##}秒", $"{name} {time:0.##} s");
        }

        /// <summary>画面に出す名前（例: 座る（最後まで）、Sit 0.5秒）。</summary>
        public string Name => Clip != null ? ClipSampleName(Clip, ClipTime) : Pose.Name + L10n.Tr($"（{StageName(Stage)}）", $" ({StageName(Stage)})");

        /// <summary>結果の短い説明（例: 3 か所・最大 12.3 mm）。体どうしの重なりがあれば、別に添える。</summary>
        public string Describe()
        {
            string overlap = Result != null && Result.HasOverlap
                ? L10n.Tr($"体どうしの重なり {Result.OverlapRegionCount} か所", $"{Result.OverlapRegionCount} body overlap(s)")
                : "";
            if (Result == null || !Result.IsPenetrating)
            {
                return overlap.Length > 0 ? L10n.Tr($"突き抜けなし（{overlap}）", $"no poke-through ({overlap})") : L10n.Tr("突き抜けなし", "no poke-through");
            }
            string main = L10n.Tr($"{Result.RegionCount} か所・最大 {Result.MaxDepth * 1000f:F1} mm", $"{Result.RegionCount} place(s), up to {Result.MaxDepth * 1000f:F1} mm");
            return overlap.Length > 0 ? main + L10n.Tr($"（ほかに{overlap}）", $" (plus {overlap})") : main;
        }
    }

    /// <summary>
    /// 動作チェックの結果（試したすべてのポーズ）。ポーズは全部試し、結果は「よく使うポーズ」と「大きく動くポーズ」に分けて集計する。
    /// 直す範囲（<see cref="IncludesLargePoses"/>。設定の「大きく動くポーズも直す」）のポーズの集計（<see cref="FixSummary"/>）から、
    /// 色分けの点・悪い順・外へ逃がす量・ウェイトを寄せる対象を作る（<see cref="BuildPlans"/>。設定を変えたら作り直す。調べ直しはしない）。
    /// </summary>
    public sealed class PoseCheckResult
    {
        /// <summary>ポーズ・段階ごとの結果（試した順。<see cref="Summary"/> の結果の番号と同じ）。</summary>
        public readonly List<PoseCheckEntry> Entries = new List<PoseCheckEntry>();

        /// <summary>集計（すべてのポーズ。頂点ごとの最悪の深さなど）。</summary>
        public PoseCheckSummary Summary;

        /// <summary>よく使うポーズ（手持ちの AnimationClip のコマを含む）だけの集計と、大きく動くポーズだけの集計（結果の番号は <see cref="Summary"/> と同じ）。</summary>
        public PoseCheckSummary CommonSummary, LargeSummary;

        /// <summary>直す範囲に、大きく動くポーズも入れたか（<see cref="BuildPlans"/> で決める）。</summary>
        public bool IncludesLargePoses;

        /// <summary>直す範囲のポーズの集計（大きく動くポーズも直すなら <see cref="Summary"/>、そうでなければ <see cref="CommonSummary"/>）。</summary>
        public PoseCheckSummary FixSummary => IncludesLargePoses || CommonSummary == null ? Summary : CommonSummary;

        /// <summary>
        /// 基準の姿勢（今の姿勢をポーズとして付け直したもの）の貫通。ポーズの結果からは、この深さを引いて数えている
        /// （今の姿勢から突き抜けている所は「貫通を直す」の受け持ち）。
        /// </summary>
        public PosePenetration Baseline = new PosePenetration();

        /// <summary>粒子ごとの、直す範囲のポーズでの最悪の深さ（メートル）。編集の姿勢での色分けに使う。作り直すと別の配列になる。</summary>
        public float[] ParticleDepth;

        /// <summary>
        /// 「元のポーズで外へ逃がす」の量（既定の設定で、直す範囲のポーズから決めたもの）。逃がせるか所と、ウェイトが原因・深すぎるので逃がさないか所の数を持つ。
        /// 体が無ければ null。
        /// </summary>
        public PoseEscapePlan EscapePlan;

        /// <summary>
        /// 「ウェイトを体に寄せる」の対象（直す範囲のポーズで、外へ逃がしても直らない、ウェイトが原因のか所の頂点）と、体どうしの重なりの数。体が無ければ null。
        /// </summary>
        public WeightFixPlan WeightPlan;

        /// <summary>まとめた後の頂点ごとの、編集の姿勢で一番近い体の点（ウェイトを体に寄せるのと、体どうしの重なりの判定に使う）。体が無ければ null。</summary>
        public BodyAnchors Anchors;

        /// <summary>調べたときの体の表面（アンカーの三角形を引くのに使う。頂点の位置は、最後に調べたポーズのもの）。体が無ければ null。</summary>
        public BodySurface Body;

        /// <summary>編集の姿勢（基準）での、まとめた後の頂点の位置（伸びの基準と、ウェイトを寄せる範囲の距離に使う）。</summary>
        public NVector3[] RestWelded;

        /// <summary>試した手持ちの AnimationClip のコマの数。</summary>
        public int ClipSampleCount;

        /// <summary>手持ちの AnimationClip について知らせること（使えなかったクリップなど）。</summary>
        public readonly List<string> ClipNotes = new List<string>();

        /// <summary>ポーズで動く割合（<see cref="PosePenetration.Gains"/>）を測ったか（試しの形を作れたか）。</summary>
        public bool GainsMeasured;

        /// <summary>調べたときの編集（形が変わったか・作り直したかを見るため）。</summary>
        public ClothEditSession Session;
        public int ShapeVersion;

        /// <summary>調べた衣装。</summary>
        public Renderer[] Targets;

        /// <summary>試したポーズの数と段階の数、許容の深さ（メートル）。</summary>
        public int PoseCount;
        public int StageCount;
        public float Tolerance;

        /// <summary>体が見つかったか。無ければ何も調べていない。</summary>
        public bool HasBody;

        /// <summary>体として使ったものの説明。</summary>
        public string BodyDescription = "";

        /// <summary>服の別のアーマチュアのボーンのうち、アバターのボーンの動きをうつしたものと、対応が無く親に付いていったものの数。</summary>
        public int FollowerCount;
        public int UnmatchedBoneCount;

        /// <summary>全体の時間と、準備（アバターの複製・体の表面）の時間（ミリ秒）。</summary>
        public double Milliseconds;
        public double PrepareMilliseconds;

        /// <summary>どれかのポーズで貫通として数えられた、布の頂点の数。</summary>
        public int PenetratingVertexCount => Summary != null ? Summary.PenetratingVertexCount : 0;

        /// <summary>貫通が見つかったポーズの数（段階はまとめて1つ）。</summary>
        public int PenetratingPoseCount => Summary != null ? Summary.PenetratingGroupCount : 0;

        /// <summary>全ポーズ・全段階の、貫通している所の数の合計。</summary>
        public int TotalRegionCount => Summary != null ? Summary.TotalRegionCount : 0;

        /// <summary>一番深い貫通（メートル）。</summary>
        public float MaxDepth => Summary != null ? Summary.MaxDepth : 0f;

        /// <summary>全ポーズ・全段階の、体どうしの重なりの所の数の合計（貫通の数には入れていない。服では直せない）。</summary>
        public int OverlapRegionCount => Summary != null ? Summary.TotalOverlapRegionCount : 0;

        /// <summary>体どうしの重なりが見つかったポーズの数。</summary>
        public int OverlapPoseCount => Summary != null ? Summary.OverlapGroupCount : 0;

        /// <summary>ポーズでの布の伸び（大きいほうから 1% の所の、全ポーズでの一番大きいもの。0.05 で 5%）。</summary>
        public float HighStretch => Summary != null ? Summary.HighStretch : 0f;

        /// <summary>ポーズでの布の伸びの、全ポーズでの一番大きいもの。</summary>
        public float MaxStretch => Summary != null ? Summary.MaxStretch : 0f;

        /// <summary>ウェイトが原因の貫通（直す範囲のポーズで、ウェイトを体に寄せる対象）のか所の数。</summary>
        public int WeightCausedRegionCount => WeightPlan != null ? WeightPlan.Regions : 0;

        /// <summary>貫通が見つかった結果の番号を、悪い順に並べたもの（すべてのポーズ）。</summary>
        public int[] RankEntries() => Summary != null ? Summary.RankEntries() : new int[0];

        /// <summary>直す範囲のポーズで、貫通が見つかった結果の番号を、悪い順に並べたもの。</summary>
        public int[] RankFixEntries() => FixSummary != null ? FixSummary.RankEntries() : new int[0];

        /// <summary>直す範囲の外（大きく動くポーズを直さないときの、大きく動くポーズ）で、貫通が見つかった結果の番号を、悪い順に並べたもの。</summary>
        public int[] RankOtherEntries() => !IncludesLargePoses && LargeSummary != null ? LargeSummary.RankEntries() : new int[0];

        /// <summary>「順に見る」の順: 直す範囲のポーズを悪い順に、そのあと範囲の外のポーズを悪い順に。</summary>
        public int[] RankForBrowsing()
        {
            int[] fix = RankFixEntries(), other = RankOtherEntries();
            var all = new int[fix.Length + other.Length];
            fix.CopyTo(all, 0);
            other.CopyTo(all, fix.Length);
            return all;
        }

        /// <summary>よく使うポーズで、突き抜けが無いか。</summary>
        public bool CommonClear => CommonSummary == null || CommonSummary.PenetratingEntryCount == 0;

        /// <summary>直す範囲のポーズで、突き抜けが無いか。</summary>
        public bool FixScopeClear => FixSummary == null || FixSummary.PenetratingEntryCount == 0;

        /// <summary>直す範囲のポーズの、突き抜けたか所の数（同じ所でも、ポーズ・段階ごとに1か所と数える）。</summary>
        public int FixRegionCount => FixSummary != null ? FixSummary.TotalRegionCount : 0;

        /// <summary>直す範囲のポーズの、体どうしの重なりの所の数。</summary>
        public int FixOverlapRegionCount => FixSummary != null ? FixSummary.TotalOverlapRegionCount : 0;

        /// <summary>
        /// よく使うポーズと大きく動くポーズに分けた集計（<see cref="CommonSummary"/>・<see cref="LargeSummary"/>）を作る。
        /// <see cref="Entries"/> と <see cref="Summary"/> がそろってから呼ぶ（調べ終わったとき）。
        /// </summary>
        public void BuildScopes()
        {
            if (Summary == null) return;
            int count = Summary.Entries.Count;
            var common = new bool[count];
            var large = new bool[count];
            for (int e = 0; e < count && e < Entries.Count; e++)
            {
                bool isLarge = Entries[e].IsLargeMotion;
                common[e] = !isLarge;
                large[e] = isLarge;
            }
            CommonSummary = Summary.Select(common);
            LargeSummary = Summary.Select(large);
        }

        /// <summary>
        /// 直す範囲（includeLargePoses なら大きく動くポーズも）のポーズの結果から、色分けの点（cloth の粒子ごとの最悪の深さ）・外へ逃がす量・
        /// ウェイトを寄せる対象を作り直す。保存してある各ポーズの結果から作るので、調べ直しはしない。
        /// </summary>
        public void BuildPlans(bool includeLargePoses, ParticleCloth cloth)
        {
            if (Summary == null) return;
            if (CommonSummary == null || LargeSummary == null) BuildScopes();
            IncludesLargePoses = includeLargePoses;
            UpdateParticleDepth(cloth);
            EscapePlan = null;
            WeightPlan = null;
            if (!HasBody) return;
            PoseCheckSummary scope = FixSummary;
            var defaults = new PoseEscapeOptions();
            EscapePlan = scope.PlanEscape(defaults.Extra, defaults.MaxPush, defaults.MinGain, Anchors, WeightTransferOptions.DefaultFullDistance);
            WeightPlan = scope.PlanWeightFix(defaults.Extra, defaults.MaxPush, defaults.MinGain, Anchors, WeightTransferOptions.DefaultFullDistance);
        }

        /// <summary>
        /// 色分けの点（粒子ごとの、直す範囲のポーズでの最悪の深さ）を、cloth の粒子で作り直す（毎回新しい配列にする。画面は配列が変わったら描き直すため）。
        /// 布の頂点の数が合わなければ null にする。
        /// </summary>
        public void UpdateParticleDepth(ParticleCloth cloth)
        {
            PoseCheckSummary scope = FixSummary;
            if (cloth == null || scope == null || cloth.Topology.Positions.Length != scope.VertexCount)
            {
                ParticleDepth = null;
                return;
            }
            var depth = new float[cloth.Model.ParticleCount];
            scope.ToParticleDepth(cloth, depth);
            ParticleDepth = depth;
        }

        /// <summary>
        /// よく使うポーズの結果の1行（例「よく使うポーズ: 突き抜けなし ✓」「よく使うポーズ: 3 / 10 ポーズで 5 か所（最大 8.2 mm）」）。
        /// よく使うポーズを1つも試していなければ空。
        /// </summary>
        public string DescribeCommon()
        {
            PoseCheckSummary s = CommonSummary;
            if (s == null || s.IncludedEntryCount == 0) return "";
            int bad = s.PenetratingGroupCount;
            if (bad == 0) return L10n.Tr($"{PoseLibrary.CommonLabel}: 突き抜けなし ✓", $"{PoseLibrary.CommonLabel}: no poke-through ✓");
            return L10n.Tr($"{PoseLibrary.CommonLabel}: {bad} / {s.IncludedGroupCount} ポーズで {s.TotalRegionCount} か所（最大 {s.MaxDepth * 1000f:F1} mm）",
                $"{PoseLibrary.CommonLabel}: {s.TotalRegionCount} place(s) in {bad} of {s.IncludedGroupCount} poses (up to {s.MaxDepth * 1000f:F1} mm)");
        }

        /// <summary>
        /// 大きく動くポーズの結果の1行（例「大きく動くポーズ（座る・しゃがむ・足を上げる・開く）: 4 / 5 ポーズで 12 か所。スカートなどでは普通に起きます（…）」）。
        /// 大きく動くポーズを1つも試していなければ空。
        /// </summary>
        public string DescribeLarge()
        {
            PoseCheckSummary s = LargeSummary;
            if (s == null || s.IncludedEntryCount == 0) return "";
            int bad = s.PenetratingGroupCount;
            string label = PoseLibrary.LargeMotionLabel;
            if (bad == 0) return L10n.Tr($"{label}: 突き抜けなし ✓", $"{label}: no poke-through ✓");
            if (IncludesLargePoses)
            {
                return L10n.Tr($"{label}: {bad} / {s.IncludedGroupCount} ポーズで {s.TotalRegionCount} か所（最大 {s.MaxDepth * 1000f:F1} mm）",
                    $"{label}: {s.TotalRegionCount} place(s) in {bad} of {s.IncludedGroupCount} poses (up to {s.MaxDepth * 1000f:F1} mm)");
            }
            return L10n.Tr($"{label}: {bad} / {s.IncludedGroupCount} ポーズで {s.TotalRegionCount} か所。スカートなどでは普通に起きます（VRChat では揺れもの（PhysBone）で動かすことが多いです）",
                $"{label}: {s.TotalRegionCount} place(s) in {bad} of {s.IncludedGroupCount} poses. This is normal for skirts and the like (in VRChat they are often moved by PhysBones)");
        }

        /// <summary>
        /// かんたん表示の、大きく動くポーズの結果の1行（例「大きく動くポーズ: 5 / 5 ポーズで 20 か所（スカートなどでは普通に起きます）」）。
        /// 長い説明（揺れもの（PhysBone）の話）は、「くわしい説明」の折りたたみに移した。1つも試していなければ空。
        /// </summary>
        public string DescribeLargeShort()
        {
            PoseCheckSummary s = LargeSummary;
            if (s == null || s.IncludedEntryCount == 0) return "";
            int bad = s.PenetratingGroupCount;
            string label = PoseLibrary.LargeMotionShortLabel;
            if (bad == 0) return L10n.Tr($"{label}: 突き抜けなし ✓", $"{label}: no poke-through ✓");
            if (IncludesLargePoses)
            {
                return L10n.Tr($"{label}: {bad} / {s.IncludedGroupCount} ポーズで {s.TotalRegionCount} か所（最大 {s.MaxDepth * 1000f:F1} mm）",
                    $"{label}: {s.TotalRegionCount} place(s) in {bad} of {s.IncludedGroupCount} poses (up to {s.MaxDepth * 1000f:F1} mm)");
            }
            return L10n.Tr($"{label}: {bad} / {s.IncludedGroupCount} ポーズで {s.TotalRegionCount} か所（スカートなどでは普通に起きます）",
                $"{label}: {s.TotalRegionCount} place(s) in {bad} of {s.IncludedGroupCount} poses (normal for skirts and the like)");
        }

        /// <summary>試したポーズ（試した順、重ねない。クリップのコマは含めない）。</summary>
        public List<PosePreset> GetPoses()
        {
            var poses = new List<PosePreset>();
            foreach (PoseCheckEntry entry in Entries)
            {
                if (entry.Pose != null && !poses.Contains(entry.Pose)) poses.Add(entry.Pose);
            }
            return poses;
        }

        /// <summary>試した段階（試した順、重ねない。クリップのコマは含めない）。</summary>
        public float[] GetStages()
        {
            var stages = new List<float>();
            foreach (PoseCheckEntry entry in Entries)
            {
                if (entry.Pose != null && !stages.Contains(entry.Stage)) stages.Add(entry.Stage);
            }
            return stages.ToArray();
        }

        /// <summary>要約の1行（画面・ログ用）。</summary>
        public string DescribeSummary()
        {
            if (!HasBody) return L10n.Tr("体が見つからないので、調べていません。", "No body was found, so nothing was checked.");
            if (PenetratingPoseCount == 0)
            {
                return L10n.Tr($"どのポーズでも、体が服を突き抜ける所は見つかりませんでした（{PoseCount} ポーズ・{StageCount} 段階）",
                    $"No pose made the body poke through the outfit ({PoseCount} poses, {StageCount} stage(s))");
            }
            return L10n.Tr($"貫通したポーズ: {PenetratingPoseCount} / {PoseCount}（か所の合計 {TotalRegionCount}、最大 {MaxDepth * 1000f:F1} mm）",
                $"Poses with penetration: {PenetratingPoseCount} / {PoseCount} ({TotalRegionCount} place(s) in total, up to {MaxDepth * 1000f:F1} mm)");
        }
    }

    /// <summary>「元のポーズで外へ逃がす」の結果。逃がしたあとに、もう一度チェックした前後の件数。</summary>
    public sealed class PoseEscapeReport
    {
        /// <summary>布を動かした結果。逃がせるか所が無く、何もしなかったときは null。</summary>
        public PoseEscapeResult Escape;

        /// <summary>逃がす量を決めた、逃がす前の結果（効かずに戻したときは、そのまま今の結果になる）。</summary>
        public PoseCheckResult Before;

        /// <summary>直す範囲に、大きく動くポーズも入れていたか（逃がす量と「効いたか」は、この範囲のポーズで決めた）。</summary>
        public bool IncludesLargePoses;

        /// <summary>逃がしたか所の数と、逃がさなかったか所の数（ウェイトが原因・深すぎる）。ポーズ・段階ごとに数える。</summary>
        public int PushedRegions, LowGainRegions, TooFarRegions, TightRegions;

        /// <summary>すべてのポーズでの、貫通したポーズ・か所・頂点の数と、一番深い所の前後。</summary>
        public int PosesBefore, PosesAfter;
        public int RegionsBefore, RegionsAfter;
        public int VerticesBefore, VerticesAfter;
        public float MaxDepthBefore, MaxDepthAfter;

        /// <summary>直す範囲のポーズ（<see cref="IncludesLargePoses"/>）での、貫通したポーズとか所の数の前後。</summary>
        public int ScopePosesBefore, ScopePosesAfter;
        public int ScopeRegionsBefore, ScopeRegionsAfter;

        /// <summary>
        /// 逃がしたか所が、もう一度チェックしてどれだけ浅くなったか（ポーズごとに比べる）と、全ポーズの深さの合計の前後。
        /// もう一度チェックできなければ null。
        /// </summary>
        public PoseEscapeEffect Effect;

        /// <summary>逃がしたか所の深さの平均（メートル）の前後。</summary>
        public float PushedDepthBefore => Effect != null ? Effect.PushedDepthBefore : 0f;
        public float PushedDepthAfter => Effect != null ? Effect.PushedDepthAfter : 0f;

        /// <summary>
        /// 逃がしても、逃がした所が浅くならない・全体の貫通が減らなかったので、逃がす前の形に戻したか（Undo の記録も残らない）。
        /// ぴったりの服で布に余裕が無いときや、ウェイトが原因のときに起きる。
        /// </summary>
        public bool Reverted;

        /// <summary>もう一度チェックできたか（できなければ After は意味を持たない）。</summary>
        public bool Rechecked;

        /// <summary>逃がしたあとにもう一度チェックした結果（戻したときも残す。比べるため）。</summary>
        public PoseCheckResult After;

        /// <summary>逃がさなかったか所の数。</summary>
        public int SkippedRegions => LowGainRegions + TooFarRegions + TightRegions;

        /// <summary>
        /// 外へ逃がして、深さは減ったのに、直す範囲のか所の数が増えたときだけ付ける1行（空なら足さない）。
        /// 浅い所を押し出すと、そのまわりに新しく浅い所が出るため、数だけを見ると「悪くなった」と見える。
        /// 効いたかの判定は深さで行っている（<see cref="PoseEscapeEffect.Improved"/>）ので、数が増えても、戻していなければ効いている。
        /// </summary>
        public static string DescribeNewShallow(bool reverted, bool rechecked, int regionsBefore, int regionsAfter, float depthBeforeMm, float depthAfterMm)
        {
            if (reverted || !rechecked || regionsAfter <= regionsBefore) return "";
            string depth = depthAfterMm < depthBeforeMm
                ? L10n.Tr($"逃がした所の深さは減りました（{depthBeforeMm:F1} → {depthAfterMm:F1} mm）。", $"The places moved outward are shallower ({depthBeforeMm:F1} → {depthAfterMm:F1} mm). ")
                : "";
            return depth + L10n.Tr($"そのまわりに、浅い所が新しく出ています（か所 {regionsBefore} → {regionsAfter}）。「おすすめの直し方」を見て、続けてください。",
                $"New shallow places appeared around them (places {regionsBefore} → {regionsAfter}). Check \"Suggested fixes\" and continue.");
        }

        /// <summary><see cref="DescribeNewShallow(bool, bool, int, int, float, float)"/> を、この結果の数字で作る。</summary>
        public string DescribeNewShallow()
        {
            return DescribeNewShallow(Reverted, Rechecked, ScopeRegionsBefore, ScopeRegionsAfter, PushedDepthBefore * 1000f, PushedDepthAfter * 1000f);
        }

        public override string ToString()
        {
            return (Reverted ? "効かなかったので戻した / " : "") + $"直す範囲: {(IncludesLargePoses ? "すべてのポーズ" : "よく使うポーズ")}（貫通したポーズ {ScopePosesBefore} → {ScopePosesAfter}、か所 {ScopeRegionsBefore} → {ScopeRegionsAfter}）" +
                $" / 逃がしたか所 {PushedRegions}（ウェイトが原因で逃がさない {LowGainRegions}、深すぎて逃がさない {TooFarRegions}、体にぴったりで逃がさない {TightRegions}）" +
                (Effect != null ? $" / 逃がした所（ポーズごとの頂点 {Effect.PushedPairs}）: 深さの平均 {Effect.PushedDepthBefore * 1000f:F1} → {Effect.PushedDepthAfter * 1000f:F1} mm、貫通が無くなった {Effect.ClearedPairs}" +
                    $" / 直す範囲の深さの合計 {Effect.TotalDepthBefore * 1000.0:F0} → {Effect.TotalDepthAfter * 1000.0:F0} mm" : "") +
                $" / すべてのポーズ: 貫通したポーズ {PosesBefore} → {PosesAfter}、か所 {RegionsBefore} → {RegionsAfter}、頂点 {VerticesBefore} → {VerticesAfter}" +
                $"、最大 {MaxDepthBefore * 1000f:F1} → {MaxDepthAfter * 1000f:F1} mm / {(Escape != null ? Escape.ToString() : "動かしていない")}";
        }
    }

    /// <summary>
    /// 動作チェックの結果から決めた「おすすめの直し方」（画面に依存しないので、テストで確かめる）。
    /// 1. 浅い所 → 元のポーズで外へ逃がす、2. ウェイトが原因の所 → ウェイトを体に寄せる、3. 体どうしの重なり → 服では直せない（気にしなくてよい）。
    /// 数は、直す範囲（設定の「大きく動くポーズも直す」）のポーズで、ポーズ・段階ごとに数える。
    /// 外へ逃がしても効かずに戻したあとは、外へ逃がすを勧めない（ウェイトを勧めるか、このままで大丈夫と言う）。
    /// </summary>
    public sealed class PoseFixAdvice
    {
        /// <summary>よく使うポーズで、突き抜けが無い（いちばん目立たせる）。</summary>
        public bool CommonClear;

        /// <summary>直す範囲のポーズで、突き抜けが無い。</summary>
        public bool ScopeClear;

        /// <summary>直す範囲に、大きく動くポーズも入っているか。</summary>
        public bool IncludesLargePoses;

        /// <summary>1. 浅い所（元のポーズで外へ逃がせる所）の数。外へ逃がしても効かずに戻したあとは 0。</summary>
        public int EscapeRegions;

        /// <summary>外へ逃がしても効かずに戻した、浅い所の数（勧めず、このままで大丈夫と言う）。</summary>
        public int RevertedEscapeRegions;

        /// <summary>2. ウェイトが原因の所の数。</summary>
        public int WeightRegions;

        /// <summary>3. 体どうしの重なりの所の数（服では直せない）。</summary>
        public int OverlapRegions;

        /// <summary>
        /// 前回の「外へ逃がす」が効いたあとの、新しい結果にも浅い所があるときの一言（無ければ空）。くり返し押してよいことを伝える。
        /// 効かずに戻した直後は勧めないので空。
        /// </summary>
        public string RepeatEscapeHint = "";

        /// <summary>勧める直し方が無い（直す範囲に突き抜けが無い、または外へ逃がしても効かず、ウェイトが原因の所も無い）。</summary>
        public bool NothingToDo => EscapeRegions == 0 && WeightRegions == 0;

        /// <summary>
        /// 結果 result と、最後の「外へ逃がす」（lastEscape。無ければ null）から決める。result が無い・体が無いときは null。
        /// </summary>
        public static PoseFixAdvice From(PoseCheckResult result, PoseEscapeReport lastEscape)
        {
            if (result == null || !result.HasBody || result.Summary == null) return null;
            bool reverted = lastEscape != null && lastEscape.Reverted && lastEscape.Before == result && lastEscape.IncludesLargePoses == result.IncludesLargePoses;
            int escapable = result.EscapePlan != null ? result.EscapePlan.PushedRegions : 0;
            // 外へ逃がしたあとの新しい結果（調べ直した結果）が、今の結果のときだけ
            bool afterEscape = lastEscape != null && lastEscape.After == result && lastEscape.IncludesLargePoses == result.IncludesLargePoses;
            return new PoseFixAdvice
            {
                CommonClear = result.CommonClear,
                ScopeClear = result.FixScopeClear,
                IncludesLargePoses = result.IncludesLargePoses,
                EscapeRegions = reverted ? 0 : escapable,
                RevertedEscapeRegions = reverted ? escapable : 0,
                WeightRegions = result.WeightCausedRegionCount,
                OverlapRegions = result.FixOverlapRegionCount,
                RepeatEscapeHint = DescribeRepeatHint(reverted, afterEscape && lastEscape.Rechecked, escapable),
            };
        }

        /// <summary>
        /// 外へ逃がしたあとの結果に、まだ浅い所（shallowRegions）があるときの一言。
        /// 効かずに戻した直後（lastReverted）や、逃がしたあとの結果ではないとき（afterEscape が false）は空。
        /// </summary>
        public static string DescribeRepeatHint(bool lastReverted, bool afterEscape, int shallowRegions)
        {
            if (lastReverted || !afterEscape || shallowRegions <= 0) return "";
            return L10n.Tr("まだ浅い所があれば、もう一度押せます", "If shallow places remain, you can press it again");
        }
    }
}
