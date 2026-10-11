using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 動作チェックの「よく使うポーズ」と「大きく動くポーズ」の分け方と、直す範囲（設定の「大きく動くポーズも直す」）を、
    /// Humanoid のテスト用アバター（体・スカート・タイツ・袖）で確かめる。
    /// ポーズは全部試し、色分けの点・悪い順・外へ逃がす・ウェイトを寄せるは直す範囲のポーズだけで決めること、設定を変えたら調べ直さずに作り直すこと、
    /// 色分けの点がほかの操作で消え、いつでも出し直せること、おすすめの直し方の数。テスト用の衣装での件数をログに出す（報告に使う）。
    /// </summary>
    public class PoseCheckScopeTests
    {
        const string TempFolder = "Assets/GP02_PoseCheckScopeTestTemp";

        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            PoseRig.DisposeAll();
            for (int k = _created.Count - 1; k >= 0; k--)
            {
                Object o = _created[k];
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            Undo.ClearAll();
        }

        GameObject CreateAvatar()
        {
            GameObject root = TestAvatarMenu.CreateInScene(null);
            _created.Add(root);
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                _created.Add(renderer.sharedMesh);
                foreach (Material m in renderer.sharedMaterials) _created.Add(m);
            }
            _created.Add(root.GetComponent<Animator>().avatar);
            return root;
        }

        static SkinnedMeshRenderer Find(GameObject root, string name)
        {
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.name == name) return renderer;
            }
            Assert.Fail(name + " が見つかりません");
            return null;
        }

        ClothEditController Start(Renderer target)
        {
            var c = new ClothEditController(new ClothEditSettings())
            {
                BrushRadius = 0.05f, InfluenceRadius = 0.3f, RecordToData = false, SaveFolder = TempFolder, DataFolder = TempFolder,
            };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(new[] { target }, out string error), Is.True, error);
            return c;
        }

        static readonly string[] LargeIds = { "sit", "squat", "leg_forward_l", "leg_forward_r", "legs_apart" };

        [Test]
        public void PoseLibrary_HasFiveBigMotionPoses_AndTheRestAreEverydayPoses()
        {
            int large = 0;
            foreach (PosePreset pose in PoseLibrary.All)
            {
                bool expected = System.Array.IndexOf(LargeIds, pose.Id) >= 0;
                Assert.That(pose.IsLargeMotion, Is.EqualTo(expected), pose.Id);
                Assert.That(pose.Category, Is.EqualTo(expected ? PoseCategory.Large : PoseCategory.Common), pose.Id);
                if (expected) large++;
            }
            Assert.That(large, Is.EqualTo(5));
            Assert.That(PoseLibrary.All.Count - large, Is.EqualTo(10));
            Assert.That(new ClothEditSettings().FixLargePoses, Is.False, "既定は、よく使うポーズだけを直す");
            // 手持ちの AnimationClip のコマは、よく使うポーズ
            Assert.That(new PoseCheckEntry { Clip = new AnimationClip() }.IsLargeMotion, Is.False);
        }

        [Test]
        public void Skirt_DefaultScopeUsesOnlyEverydayPoses_AndChangingTheSettingRebuildsWithoutChecking()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.SkirtName));
            PoseCheckResult result = c.RunPoseCheck();
            Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
            Debug.Log($"[GP02] スカート（既定の範囲）: {result.DescribeCommon()} / {result.DescribeLarge()} / すべて {result.DescribeSummary()}");

            // ポーズは全部試す
            Assert.That(result.Entries.Count, Is.EqualTo(PoseLibrary.All.Count * 2));
            Assert.That(result.IncludesLargePoses, Is.False);
            Assert.That(result.FixSummary, Is.SameAs(result.CommonSummary));
            Assert.That(result.CommonSummary.IncludedGroupCount, Is.EqualTo(10));
            Assert.That(result.LargeSummary.IncludedGroupCount, Is.EqualTo(5));
            Assert.That(result.LargeSummary.PenetratingGroupCount, Is.GreaterThan(0), "座る・足を上げるで、スカートは突き抜ける");
            Assert.That(result.CommonSummary.TotalRegionCount + result.LargeSummary.TotalRegionCount, Is.EqualTo(result.TotalRegionCount));
            StringAssert.Contains(PoseLibrary.LargeMotionLabel, result.DescribeLarge());

            // 直す範囲の計画には、大きく動くポーズの結果を入れない
            for (int e = 0; e < result.Entries.Count; e++)
            {
                if (!result.Entries[e].IsLargeMotion) continue;
                Assert.That(result.EscapePlan.AllowedRegions[e], Is.Null, result.Entries[e].Name);
                Assert.That(result.WeightPlan.TargetRegions[e], Is.Null, result.Entries[e].Name);
            }
            foreach (int e in result.RankFixEntries()) Assert.That(result.Entries[e].IsLargeMotion, Is.False);
            foreach (int e in result.RankOtherEntries()) Assert.That(result.Entries[e].IsLargeMotion, Is.True);
            Assert.That(result.RankOtherEntries().Length, Is.GreaterThan(0));
            // 「順に見る」は、直す範囲のポーズを先に、そのあと大きく動くポーズ
            int[] browse = result.RankForBrowsing();
            Assert.That(browse.Length, Is.EqualTo(result.RankEntries().Length));
            Assert.That(browse[browse.Length - 1], Is.EqualTo(result.RankOtherEntries()[result.RankOtherEntries().Length - 1]));
            int shown = c.ShowNextPenetratingPose();
            Assert.That(shown, Is.EqualTo(browse[0]));
            c.EndPosePreview();

            // 色分けの点は、よく使うポーズの最悪の深さ
            float[] commonDepth = c.PoseCheckParticleDepth;
            Assert.That(commonDepth, Is.Not.Null);
            var expected = new float[commonDepth.Length];
            result.CommonSummary.ToParticleDepth(c.Session.Cloth, expected);
            Assert.That(commonDepth, Is.EqualTo(expected));

            // 「大きく動くポーズも直す」をオンにすると、調べ直さずに作り直す（結果の各ポーズは同じもの）
            List<PoseCheckEntry> entries = new List<PoseCheckEntry>(result.Entries);
            PoseCheckSummary summary = result.Summary;
            c.Settings.FixLargePoses = true;
            Assert.That(c.ApplyPoseFixScope(), Is.True);
            Assert.That(c.ApplyPoseFixScope(), Is.False, "同じ範囲なら作り直さない");
            Assert.That(c.LastPoseCheck, Is.SameAs(result));
            Assert.That(result.Summary, Is.SameAs(summary));
            Assert.That(result.Entries, Is.EqualTo(entries));
            Assert.That(result.IncludesLargePoses, Is.True);
            Assert.That(result.FixSummary, Is.SameAs(result.Summary));
            Assert.That(result.RankOtherEntries(), Is.Empty);
            Assert.That(result.RankFixEntries(), Is.EqualTo(result.RankEntries()));
            float[] allDepth = c.PoseCheckParticleDepth;
            Assert.That(allDepth, Is.Not.SameAs(commonDepth), "色分けの点は作り直す（画面が描き直すように、別の配列）");
            int commonMarked = 0, allMarked = 0;
            for (int i = 0; i < allDepth.Length; i++)
            {
                if (commonDepth[i] > 0f) commonMarked++;
                if (allDepth[i] > 0f) allMarked++;
                Assert.That(allDepth[i], Is.GreaterThanOrEqualTo(commonDepth[i]));
            }
            Assert.That(allMarked, Is.GreaterThan(commonMarked), "座るなどの点が加わる");
            Assert.That(result.WeightPlan.Regions + result.EscapePlan.PushedRegions, Is.EqualTo(result.TotalRegionCount));
            Assert.That(result.WeightPlan.TooFarRegions, Is.GreaterThan(0), "座ったときの深い所は、ウェイトが原因の対象になる");

            // 戻すと、よく使うポーズだけに戻る
            c.Settings.FixLargePoses = false;
            Assert.That(c.ApplyPoseFixScope(), Is.True);
            Assert.That(c.PoseCheckParticleDepth, Is.EqualTo(commonDepth));
            Assert.That(result.WeightPlan.Regions + result.EscapePlan.PushedRegions, Is.EqualTo(result.CommonSummary.TotalRegionCount));
        }

        [Test]
        public void ColorDots_DisappearWhenAnotherOperationStarts_AndCanBeShownAgain()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.SleevesName));
            double time = 100.0;
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            Assert.That(c.PoseCheckParticleDepth, Is.Not.Null);
            Assert.That(c.IsPoseCheckDepthShown, Is.True, "結果が出たら、色分けの点を出す（既定の好み）");

            // ピン留めの操作で消える
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
            c.InvertPins();
            Assert.That(c.IsPoseCheckDepthShown, Is.False, "ピン留めを始めたのに、点が残っている");
            c.ClearPins();

            // 区切りのトグルで出し直せる。選ぶ操作でも消える（次の更新で）
            c.SetPoseCheckDepthShown(true);
            Assert.That(c.IsPoseCheckDepthShown, Is.True);
            c.Tick(time += 0.016);
            Assert.That(c.IsPoseCheckDepthShown, Is.True, "何もしなければ消えない");
            Assert.That(c.SelectAll(), Is.GreaterThan(0));
            c.Tick(time += 0.016);
            Assert.That(c.IsPoseCheckDepthShown, Is.False, "選ぶ操作を始めたのに、点が残っている");
            c.ClearSelection();

            // Undo でも消える。結果が古くなったときも消える（出し直すと、古くても出す）
            c.SetPoseCheckDepthShown(true);
            c.Tick(time += 0.016);
            Assert.That(c.IsPoseCheckDepthShown, Is.True);
            c.InvertPins();
            c.SetPoseCheckDepthShown(true);
            c.UndoLast();
            Assert.That(c.IsPoseCheckDepthShown, Is.False, "Undo したのに、点が残っている");
            if (c.IsPoseCheckStale)
            {
                // 古い結果でも、トグルで出し直せる（どこを直すかの目安として）。次の更新で、また消えることはない
                c.SetPoseCheckDepthShown(true);
                c.Tick(time += 0.016);
                Assert.That(c.IsPoseCheckDepthShown, Is.True);
            }

            // 調べ直すと、また出る。動作チェックの中の操作（ポーズを見せる・範囲を変える）では消えない
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            Assert.That(c.IsPoseCheckDepthShown, Is.True);
            Assert.That(c.ShowNextPenetratingPose(), Is.GreaterThanOrEqualTo(0));
            c.EndPosePreview();
            c.Settings.FixLargePoses = true;
            c.ApplyPoseFixScope();
            Assert.That(c.IsPoseCheckDepthShown, Is.True);

            // 好みの設定がオフなら、結果が出ても出さない。トグルでは出せる
            c.Settings.ShowPoseCheckDepth = false;
            Assert.That(c.RunPoseCheck(), Is.Not.Null);
            Assert.That(c.IsPoseCheckDepthShown, Is.False);
            c.SetPoseCheckDepthShown(true);
            Assert.That(c.IsPoseCheckDepthShown, Is.True);
        }

        // 結果を手で作る（体あり、頂点 10）。よく使うポーズ（歩く）に浅い所、大きく動くポーズ（座る）に深い所、お辞儀に体どうしの重なり
        static PoseCheckResult SyntheticResult(bool walkPenetrates)
        {
            var result = new PoseCheckResult { HasBody = true, Summary = new PoseCheckSummary(10) };
            void Add(string id, PosePenetration penetration)
            {
                PosePreset pose = PoseLibrary.Find(id);
                var entry = new PoseCheckEntry { Pose = pose, Stage = 1f, Result = penetration };
                result.Entries.Add(entry);
                result.Summary.Add(penetration);
            }
            PosePenetration Pen(int group, params (int Vertex, float Depth)[] items)
            {
                var p = new PosePenetration { Group = group, HasBody = true };
                foreach (var (vertex, depth) in items)
                {
                    p.Vertices.Add(vertex);
                    p.Depths.Add(depth);
                    p.VertexRegions.Add(0);
                    p.MaxDepth = Mathf.Max(p.MaxDepth, depth);
                }
                if (items.Length > 0)
                {
                    p.RegionDepths.Add(p.MaxDepth);
                    p.RegionCount = 1;
                }
                return p;
            }
            Add("walk", walkPenetrates ? Pen(0, (1, 0.003f), (2, 0.004f)) : Pen(0));
            var bow = Pen(1);
            bow.OverlapVertices.Add(5);
            bow.OverlapDepths.Add(0.01f);
            bow.OverlapRegionCount = 1;
            Add("bow", bow);
            Add("sit", Pen(2, (6, 0.06f), (7, 0.05f)));
            result.BuildScopes();
            result.BuildPlans(false, null);
            return result;
        }

        [Test]
        public void Advice_CountsShallowWeightAndOverlapPlaces_AndStopsSuggestingTheEscapeAfterItWasUndone()
        {
            PoseCheckResult result = SyntheticResult(true);
            Assert.That(result.CommonClear, Is.False);
            PoseFixAdvice advice = PoseFixAdvice.From(result, null);
            Assert.That(advice.CommonClear, Is.False);
            Assert.That(advice.ScopeClear, Is.False);
            Assert.That(advice.EscapeRegions, Is.EqualTo(1), "歩くの浅い所");
            Assert.That(advice.WeightRegions, Is.EqualTo(0), "座る（深い）は直す範囲の外");
            Assert.That(advice.OverlapRegions, Is.EqualTo(1));
            Assert.That(advice.RevertedEscapeRegions, Is.EqualTo(0));
            Assert.That(advice.NothingToDo, Is.False);
            StringAssert.Contains("1 / 2", result.DescribeCommon());

            // 外へ逃がしても効かずに戻したあと（同じ結果・同じ範囲）は、外へ逃がすを勧めず、このままで大丈夫と言う
            var reverted = new PoseEscapeReport { Reverted = true, Before = result, IncludesLargePoses = false };
            PoseFixAdvice after = PoseFixAdvice.From(result, reverted);
            Assert.That(after.EscapeRegions, Is.EqualTo(0));
            Assert.That(after.RevertedEscapeRegions, Is.EqualTo(1));
            Assert.That(after.NothingToDo, Is.True);
            // 範囲を変えた・別の結果なら、また勧める
            Assert.That(PoseFixAdvice.From(result, new PoseEscapeReport { Reverted = true, Before = result, IncludesLargePoses = true }).EscapeRegions, Is.EqualTo(1));
            Assert.That(PoseFixAdvice.From(SyntheticResult(true), reverted).EscapeRegions, Is.EqualTo(1));

            // 大きく動くポーズも直すなら、座るの深い所はウェイトが原因の所
            result.BuildPlans(true, null);
            PoseFixAdvice all = PoseFixAdvice.From(result, null);
            Assert.That(all.IncludesLargePoses, Is.True);
            Assert.That(all.EscapeRegions, Is.EqualTo(1));
            Assert.That(all.WeightRegions, Is.EqualTo(1));

            // よく使うポーズに突き抜けが無ければ、問題なし
            PoseCheckResult clear = SyntheticResult(false);
            PoseFixAdvice clearAdvice = PoseFixAdvice.From(clear, null);
            Assert.That(clearAdvice.CommonClear, Is.True);
            Assert.That(clearAdvice.ScopeClear, Is.True);
            Assert.That(clearAdvice.NothingToDo, Is.True);
            StringAssert.Contains("✓", clear.DescribeCommon());
            Assert.That(PoseFixAdvice.From(new PoseCheckResult { HasBody = false }, null), Is.Null);
        }

        // テスト用の衣装で、よく使うポーズ／大きく動くポーズの件数、おすすめの直し方の件数、外へ逃がす・ウェイトを体に寄せるの前後を測る
        // （既定のよく使うポーズだけの範囲と、大きく動くポーズも直す範囲。外へ逃がすと形が変わるので、範囲ごとにアバターを作り直す）
        [Test]
        public void Measure_TestGarments_CountsAndFixes()
        {
            foreach (bool fixLarge in new[] { false, true })
            {
                string scope = fixLarge ? "すべてのポーズ" : "よく使うポーズ";
                foreach (string name in new[] { TestAvatarMenu.SkirtName, TestAvatarMenu.TightsName, TestAvatarMenu.SleevesName })
                {
                    GameObject root = CreateAvatar();
                    ClothEditController c = Start(Find(root, name));
                    c.Settings.FixLargePoses = fixLarge;
                    PoseCheckResult result = c.RunPoseCheck();
                    Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
                    PoseFixAdvice advice = PoseFixAdvice.From(result, null);
                    Debug.Log($"[GP02計測] {name}（直す範囲: {scope}）: {result.DescribeCommon()} / {result.DescribeLarge()} / すべて: {result.DescribeSummary()}" +
                        $" / おすすめ: 浅い所 {advice.EscapeRegions}、ウェイトが原因 {advice.WeightRegions}、体どうしの重なり {advice.OverlapRegions}（よく使うポーズで問題なし {advice.CommonClear}）");
                    Assert.That(advice.EscapeRegions + advice.WeightRegions, Is.EqualTo(result.FixSummary.TotalRegionCount), name + ": おすすめの数の合計が、直す範囲のか所と合わない");

                    if (c.CanEscapePoseCheck)
                    {
                        PoseEscapeReport report = c.EscapePoseCheckPenetration();
                        Assert.That(report, Is.Not.Null);
                        Debug.Log($"[GP02計測] {name}（{scope}）: 外へ逃がす: {(report.Reverted ? "効かなかったので戻した" : "逃がした")}、" +
                            $"直す範囲のか所 {report.ScopeRegionsBefore} → {report.ScopeRegionsAfter}、すべてのか所 {report.RegionsBefore} → {report.RegionsAfter}" +
                            (report.Effect != null ? $"、逃がした所の深さの平均 {report.Effect.PushedDepthBefore * 1000f:F1} → {report.Effect.PushedDepthAfter * 1000f:F1} mm" : ""));
                        PoseFixAdvice afterEscape = PoseFixAdvice.From(c.LastPoseCheck, c.LastPoseEscape);
                        Debug.Log($"[GP02計測] {name}（{scope}）: 外へ逃がしたあとのおすすめ: 浅い所 {afterEscape.EscapeRegions}（効かなかった {afterEscape.RevertedEscapeRegions}）、ウェイトが原因 {afterEscape.WeightRegions}");
                        if (report.Reverted) Assert.That(afterEscape.EscapeRegions, Is.EqualTo(0), "効かなかったのに、また外へ逃がすを勧めた");
                    }
                    else Debug.Log($"[GP02計測] {name}（{scope}）: 外へ逃がす所なし");

                    if (c.CanPreviewWeightFix)
                    {
                        WeightFixPreview preview = c.PreviewWeightFix();
                        Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
                        Debug.Log($"[GP02計測] {name}（{scope}）: ウェイトを体に寄せる（確認）: 直す範囲のか所 {preview.RegionsBefore} → {preview.RegionsAfter}、ウェイトが原因 {preview.WeightRegionsBefore} → {preview.WeightRegionsAfter}" +
                            $"、すべてのか所 {preview.Before.TotalRegionCount} → {preview.After.TotalRegionCount}、伸び {(preview.StretchEntry >= 0 ? $"{preview.StretchPoseName} {preview.StretchBefore * 100f:F0}% → {preview.StretchAfter * 100f:F0}%" : "増えない")}");
                    }
                    else Debug.Log($"[GP02計測] {name}（{scope}）: ウェイトが原因の所なし");
                    c.Dispose();
                    _controllers.Remove(c);
                }
            }
        }
    }
}
