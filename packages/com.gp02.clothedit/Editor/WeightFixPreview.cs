using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>「ウェイトを体に寄せる」の、1つのレンダラーの候補のウェイト（まだメッシュには入れていない）。</summary>
    public sealed class WeightFixCandidate
    {
        /// <summary>レンダラー（編集中の衣装）。</summary>
        public SkinnedMeshRenderer Renderer;

        /// <summary>頂点ごとの新しいウェイト（すべての頂点。変えない頂点は元のまま。ボーンは <see cref="Bones"/> の番号）。</summary>
        public BoneWeightSets Weights;

        /// <summary>頂点ごとの、ウェイトを変えたか。</summary>
        public bool[] Changed;

        /// <summary>ウェイトを変えた頂点の数。</summary>
        public int ChangedCount;

        /// <summary>候補を当てたあとのレンダラーのボーン（元のボーンの末尾に、足すボーン）。</summary>
        public Transform[] Bones;

        /// <summary><see cref="Bones"/> と同じ順のバインドポーズ。</summary>
        public Matrix4x4[] Bindposes;

        /// <summary>レンダラーのボーンに足すボーン（体の背骨など、衣装のボーンに無かったもの）。</summary>
        public Transform[] AddedBones = new Transform[0];

        /// <summary>source（このレンダラーのメッシュ）の複製に、候補のウェイトを入れたもの（保存しない一時メッシュ。消すのは呼び出し側）。</summary>
        public Mesh CreateMesh(Mesh source)
        {
            Mesh mesh = Object.Instantiate(source);
            mesh.name = source.name + "_GP02WeightCandidate";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            ApplyTo(mesh);
            return mesh;
        }

        /// <summary>mesh（このレンダラーのメッシュと同じ並びのもの）に、候補のウェイトとバインドポーズを入れる。</summary>
        public void ApplyTo(Mesh mesh)
        {
            ShapeKeyWeights.Write(mesh, Weights, Bindposes);
        }
    }

    /// <summary>
    /// 「ウェイトを体に寄せる」を適用する前の確認。候補のウェイトで動作チェックをやり直した結果と、前後の数。
    /// 利用者が見てから <see cref="ClothEditController.ApplyWeightFix"/> で適用する。形を変えると古くなる（適用できない）。
    /// </summary>
    public sealed class WeightFixPreview
    {
        /// <summary>寄せる前の動作チェックの結果（候補を作った元）と、候補のウェイトで調べ直した結果。</summary>
        public PoseCheckResult Before, After;

        /// <summary>対象（ウェイトが原因のか所）。</summary>
        public WeightFixPlan Plan;

        /// <summary>対象のか所が、どれだけ浅くなったか（ポーズごとに比べたもの）。</summary>
        public PoseFixEffect Effect;

        /// <summary>候補を作ったときの強さ（0〜1）と半径（メートル）。</summary>
        public float Strength, Radius;

        /// <summary>レンダラーごとの候補（Session のレンダラーの順。ウェイトの無いレンダラーは null）。</summary>
        public readonly List<WeightFixCandidate> Candidates = new List<WeightFixCandidate>();

        /// <summary>近くに体が無い・ボーンが対応しないので、変えなかった頂点（まとめた後）の数。</summary>
        public int MissingAnchors, Unmapped;

        /// <summary>かかった時間（ミリ秒）。</summary>
        public double Milliseconds;

        internal PoseRig Rig;                  // 候補で調べ直したアバターの複製（適用したら、結果と一緒に引き継ぐ）
        internal ClothEditSession Session;     // 候補を作ったときの編集
        internal NVector3[] Welded;            // 候補を作ったときの形（まとめた後の頂点。形が変わったかを見る）

        /// <summary>ウェイトを変える頂点の数（すべてのレンダラーの合計）。</summary>
        public int ChangedVertices
        {
            get
            {
                int count = 0;
                foreach (WeightFixCandidate candidate in Candidates) count += candidate != null ? candidate.ChangedCount : 0;
                return count;
            }
        }

        /// <summary>レンダラーに足すボーンの数（すべてのレンダラーの合計）。</summary>
        public int AddedBoneCount
        {
            get
            {
                int count = 0;
                foreach (WeightFixCandidate candidate in Candidates) count += candidate != null ? candidate.AddedBones.Length : 0;
                return count;
            }
        }

        public int PosesBefore => Before != null ? Before.PenetratingPoseCount : 0;
        public int PosesAfter => After != null ? After.PenetratingPoseCount : 0;
        public int RegionsBefore => Before != null ? Before.TotalRegionCount : 0;
        public int RegionsAfter => After != null ? After.TotalRegionCount : 0;
        public float MaxDepthBefore => Before != null ? Before.MaxDepth : 0f;
        public float MaxDepthAfter => After != null ? After.MaxDepth : 0f;

        /// <summary>ウェイトが原因のか所の数の前後（調べ直した結果でも、同じ決め方で数えたもの）。</summary>
        public int WeightRegionsBefore => Before != null ? Before.WeightCausedRegionCount : 0;
        public int WeightRegionsAfter => After != null ? After.WeightCausedRegionCount : 0;

        /// <summary>体どうしの重なりの所の数（服では直せないので、前後で変わらないはず）。</summary>
        public int OverlapRegions => Before != null ? Before.OverlapRegionCount : 0;

        /// <summary>
        /// 伸びの目安が一番増えたポーズ（結果の番号。増えたポーズが無ければ -1）と、そのポーズでの前後の伸び。
        /// 伸びの目安は、編集の姿勢で体から離れている所（スカートなど）の布の辺の伸びの、大きいほうから 1% の所
        /// （<see cref="PosePenetration.HighStretch"/>。体にぴったりの所は体と一緒に伸びてよいので入れない）。
        /// </summary>
        public int StretchEntry
        {
            get
            {
                int best = -1;
                float bestIncrease = 0f;
                int count = Before != null && After != null ? Math.Min(Before.Entries.Count, After.Entries.Count) : 0;
                for (int e = 0; e < count; e++)
                {
                    float increase = After.Entries[e].Result.HighStretch - Before.Entries[e].Result.HighStretch;
                    if (increase > bestIncrease)
                    {
                        bestIncrease = increase;
                        best = e;
                    }
                }
                return best;
            }
        }

        /// <summary>伸びの目安が一番増えたポーズでの、寄せる前と後の伸び（増えたポーズが無ければ 0）。</summary>
        public float StretchBefore => StretchEntry >= 0 ? Before.Entries[StretchEntry].Result.HighStretch : 0f;
        public float StretchAfter => StretchEntry >= 0 ? After.Entries[StretchEntry].Result.HighStretch : 0f;

        /// <summary>伸びの目安の増え方の一番大きいもの（0 以上）。</summary>
        public float StretchIncrease => Math.Max(0f, StretchAfter - StretchBefore);

        /// <summary>伸びの目安が一番増えたポーズの名前（無ければ空）。</summary>
        public string StretchPoseName => StretchEntry >= 0 ? After.Entries[StretchEntry].Name : "";

        /// <summary>
        /// 寄せると布が目立って伸びるか（どれかのポーズで、伸びの目安が <see cref="MaxStretchIncrease"/> より増える）。
        /// スカートを太ももに強く寄せると、座ったとき・しゃがんだときに布が脚に貼り付いて伸びる。
        /// </summary>
        public bool StretchesTooMuch => StretchIncrease > MaxStretchIncrease;

        /// <summary><see cref="StretchesTooMuch"/> の境目（伸びの増え方。0.2 で 20% 分）。</summary>
        public const float MaxStretchIncrease = 0.2f;

        /// <summary>効いたか: 対象のか所の深さの平均が 6 割以下になり、全体の深さの合計も減った。</summary>
        public bool Improved => Effect != null && Effect.Improved(0.6f);

        /// <summary>前後の要約（画面・ダイアログ用）。</summary>
        public string Describe()
        {
            string text = L10n.Tr(
                $"ウェイトを変える頂点: {ChangedVertices}" + (AddedBoneCount > 0 ? $"（足すボーン {AddedBoneCount}）" : "") +
                $"\n貫通したポーズ: {PosesBefore} → {PosesAfter}、か所: {RegionsBefore} → {RegionsAfter}、最大の深さ: {MaxDepthBefore * 1000f:F1} → {MaxDepthAfter * 1000f:F1} mm" +
                $"\nウェイトが原因の所: {WeightRegionsBefore} → {WeightRegionsAfter} か所" +
                (Effect != null && Effect.TargetPairs > 0 ? $"（深さの平均 {Effect.TargetDepthBefore * 1000f:F1} → {Effect.TargetDepthAfter * 1000f:F1} mm、直った割合 {Effect.ClearedRatio * 100f:F0}%）" : "") +
                (StretchEntry >= 0 ? $"\n体から離れた所の布の伸び: {StretchPoseName}で {StretchBefore * 100f:F0}% → {StretchAfter * 100f:F0}%" : "\n体から離れた所の布の伸び: 増えません"),
                $"Vertices whose weights change: {ChangedVertices}" + (AddedBoneCount > 0 ? $" ({AddedBoneCount} bone(s) added)" : "") +
                $"\nPoses with penetration: {PosesBefore} → {PosesAfter}, places: {RegionsBefore} → {RegionsAfter}, deepest: {MaxDepthBefore * 1000f:F1} → {MaxDepthAfter * 1000f:F1} mm" +
                $"\nPlaces caused by weights: {WeightRegionsBefore} → {WeightRegionsAfter}" +
                (Effect != null && Effect.TargetPairs > 0 ? $" (average depth {Effect.TargetDepthBefore * 1000f:F1} → {Effect.TargetDepthAfter * 1000f:F1} mm, {Effect.ClearedRatio * 100f:F0}% cleared)" : "") +
                (StretchEntry >= 0 ? $"\nStretch of the cloth away from the body: {StretchBefore * 100f:F0}% → {StretchAfter * 100f:F0}% in {StretchPoseName}" : "\nStretch of the cloth away from the body: no increase"));
            if (OverlapRegions > 0)
            {
                text += L10n.Tr($"\n体どうしの重なり: {OverlapRegions} か所（体どうしの重なりで、服では直せません。そのままにします）", $"\nBody overlaps: {OverlapRegions} (overlaps of the body itself, which cannot be fixed in the outfit; left as they are)");
            }
            if (StretchesTooMuch)
            {
                text += L10n.Tr("\n体から離れた布（スカートなど）が、脚などに貼り付いて伸びます。くわしく表示の「強さ」を下げて確認し直すと和らぎます（50% くらい）。",
                    "\nCloth away from the body (such as a skirt) sticks to the legs, etc. and stretches. Lowering \"Strength\" in the detailed view (to about 50%) and checking again eases this.");
            }
            return text;
        }

        public override string ToString()
        {
            return $"ウェイトを変える頂点 {ChangedVertices}（足すボーン {AddedBoneCount}、体が無い {MissingAnchors}、ボーンが対応しない {Unmapped}）/ 強さ {Strength * 100f:F0}%・半径 {Radius * 100f:F1}cm" +
                $" / 貫通したポーズ {PosesBefore} → {PosesAfter}、か所 {RegionsBefore} → {RegionsAfter}、最大 {MaxDepthBefore * 1000f:F1} → {MaxDepthAfter * 1000f:F1} mm" +
                $" / ウェイトが原因 {WeightRegionsBefore} → {WeightRegionsAfter}" +
                (Effect != null ? $"（対象の頂点 {Effect.TargetPairs}: 深さの平均 {Effect.TargetDepthBefore * 1000f:F1} → {Effect.TargetDepthAfter * 1000f:F1} mm、直った {Effect.ClearedPairs}、全ポーズの深さの合計 {Effect.TotalDepthBefore * 1000.0:F0} → {Effect.TotalDepthAfter * 1000.0:F0} mm）" : "") +
                $" / 体どうしの重なり {OverlapRegions} / 体から離れた所の伸び {(StretchEntry >= 0 ? $"{StretchPoseName} {StretchBefore * 100f:F1}% → {StretchAfter * 100f:F1}%" : "増えない")}" +
                $"（全ポーズの上位1% {(Before != null ? Before.HighStretch : 0f) * 100f:F1}% → {(After != null ? After.HighStretch : 0f) * 100f:F1}%）/ {Milliseconds:F0} ms";
        }

        // 候補で調べ直したアバターの複製を、呼び出し側に渡す（このあとは消さない）
        internal PoseRig DetachRig()
        {
            PoseRig rig = Rig;
            Rig = null;
            return rig;
        }

        internal void DisposeRig()
        {
            if (Rig != null && !Rig.IsDisposed) Rig.Dispose();
            Rig = null;
        }
    }

    /// <summary>「ウェイトを体に寄せる」を適用した結果。</summary>
    public sealed class WeightFixReport
    {
        /// <summary>適用した確認（前後の数）。</summary>
        public WeightFixPreview Preview;

        /// <summary>ウェイトを変えた頂点の数と、データに記録した頂点の数（データの変更の合計。前に記録した分を含む）。</summary>
        public int ChangedVertices, RecordedVertices;

        /// <summary>作ったメッシュ（アセット）と、書き換えたメッシュ。</summary>
        public readonly List<Mesh> Meshes = new List<Mesh>();

        /// <summary>保存していない編集を捨てて適用したか（そのときは、調べ直した結果を使わず、もう一度チェックが要る）。</summary>
        public bool DiscardedUnsaved;

        public override string ToString() => $"ウェイトを変えた頂点 {ChangedVertices}、データに記録 {RecordedVertices} 頂点" + (Preview != null ? " / " + Preview : "");
    }
}
