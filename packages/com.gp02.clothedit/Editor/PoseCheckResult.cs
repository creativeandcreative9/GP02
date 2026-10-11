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

        /// <summary>ポーズの名前（設定の保存に使うもの）。クリップのコマなら空。</summary>
        public string PoseId => Pose != null ? Pose.Id : "";

        /// <summary>段階の名前（半分・全部）。</summary>
        public static string StageName(float stage) => stage < 0.75f ? L10n.Tr("半分", "half") : L10n.Tr("全部", "full");

        /// <summary>クリップのコマの名前（例: Sit 0.5秒）。</summary>
        public static string ClipSampleName(AnimationClip clip, float time)
        {
            string name = clip != null ? clip.name : "?";
            return L10n.Tr($"{name} {time:0.##}秒", $"{name} {time:0.##} s");
        }

        /// <summary>画面に出す名前（例: 座る（全部）、Sit 0.5秒）。</summary>
        public string Name => Clip != null ? ClipSampleName(Clip, ClipTime) : Pose.Name + L10n.Tr($"（{StageName(Stage)}）", $" ({StageName(Stage)})");

        /// <summary>結果の短い説明（例: 3 か所・最大 12.3 mm）。体どうしの重なりがあれば、別に添える。</summary>
        public string Describe()
        {
            string overlap = Result != null && Result.HasOverlap
                ? L10n.Tr($"体どうしの重なり {Result.OverlapRegionCount} か所", $"{Result.OverlapRegionCount} body overlap(s)")
                : "";
            if (Result == null || !Result.IsPenetrating)
            {
                return overlap.Length > 0 ? L10n.Tr($"貫通なし（{overlap}）", $"no penetration ({overlap})") : L10n.Tr("貫通なし", "no penetration");
            }
            string main = L10n.Tr($"{Result.RegionCount} か所・最大 {Result.MaxDepth * 1000f:F1} mm", $"{Result.RegionCount} place(s), up to {Result.MaxDepth * 1000f:F1} mm");
            return overlap.Length > 0 ? main + L10n.Tr($"（ほかに{overlap}）", $" (plus {overlap})") : main;
        }
    }

    /// <summary>動作チェックの結果（試したすべてのポーズ）。</summary>
    public sealed class PoseCheckResult
    {
        /// <summary>ポーズ・段階ごとの結果（試した順。<see cref="Summary"/> の結果の番号と同じ）。</summary>
        public readonly List<PoseCheckEntry> Entries = new List<PoseCheckEntry>();

        /// <summary>集計（頂点ごとの最悪の深さなど）。</summary>
        public PoseCheckSummary Summary;

        /// <summary>
        /// 基準の姿勢（今の姿勢をポーズとして付け直したもの）の貫通。ポーズの結果からは、この深さを引いて数えている
        /// （今の姿勢から突き抜けている所は「貫通を直す」の受け持ち）。
        /// </summary>
        public PosePenetration Baseline = new PosePenetration();

        /// <summary>粒子ごとの、全ポーズでの最悪の深さ（メートル）。編集の姿勢での色分けに使う。</summary>
        public float[] ParticleDepth;

        /// <summary>
        /// 「元のポーズで外へ逃がす」の量（既定の設定で決めたもの）。逃がせるか所と、ウェイトが原因・深すぎるので逃がさないか所の数を持つ。
        /// 体が無ければ null。
        /// </summary>
        public PoseEscapePlan EscapePlan;

        /// <summary>
        /// 「ウェイトを体に寄せる」の対象（外へ逃がしても直らない、ウェイトが原因のか所の頂点）と、体どうしの重なりの数。体が無ければ null。
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

        /// <summary>ウェイトが原因の貫通（ウェイトを体に寄せる対象）のか所の数。</summary>
        public int WeightCausedRegionCount => WeightPlan != null ? WeightPlan.Regions : 0;

        /// <summary>貫通が見つかった結果の番号を、悪い順に並べたもの。</summary>
        public int[] RankEntries() => Summary != null ? Summary.RankEntries() : new int[0];

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

        /// <summary>逃がしたか所の数と、逃がさなかったか所の数（ウェイトが原因・深すぎる）。ポーズ・段階ごとに数える。</summary>
        public int PushedRegions, LowGainRegions, TooFarRegions, TightRegions;

        public int PosesBefore, PosesAfter;
        public int RegionsBefore, RegionsAfter;
        public int VerticesBefore, VerticesAfter;
        public float MaxDepthBefore, MaxDepthAfter;

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

        public override string ToString()
        {
            return (Reverted ? "効かなかったので戻した / " : "") + $"逃がしたか所 {PushedRegions}（ウェイトが原因で逃がさない {LowGainRegions}、深すぎて逃がさない {TooFarRegions}、体にぴったりで逃がさない {TightRegions}）" +
                (Effect != null ? $" / 逃がした所（ポーズごとの頂点 {Effect.PushedPairs}）: 深さの平均 {Effect.PushedDepthBefore * 1000f:F1} → {Effect.PushedDepthAfter * 1000f:F1} mm、貫通が無くなった {Effect.ClearedPairs}" +
                    $" / 全ポーズの深さの合計 {Effect.TotalDepthBefore * 1000.0:F0} → {Effect.TotalDepthAfter * 1000.0:F0} mm" : "") +
                $" / 貫通したポーズ {PosesBefore} → {PosesAfter}、か所 {RegionsBefore} → {RegionsAfter}、頂点 {VerticesBefore} → {VerticesAfter}" +
                $"、最大 {MaxDepthBefore * 1000f:F1} → {MaxDepthAfter * 1000f:F1} mm / {(Escape != null ? Escape.ToString() : "動かしていない")}";
        }
    }
}
