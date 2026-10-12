using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 動作チェックの結果の文（かんたん表示の短い見た目、外へ逃がしたあとの一言）を、Unity の画面に頼らずに確かめる。
    /// 外へ逃がして深さは減ったのに、か所の数が増えたとき（浅い所を押し出すと、そのまわりに浅い所が新しく出る）の説明と、
    /// 「まだ浅い所があれば、もう一度押せます」の一言（効かずに戻した直後は勧めない）。日本語と英語。
    /// </summary>
    public class PoseEscapeTextTests
    {
        UiLanguage _savedLanguage;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = L10n.Language;
        }

        [TearDown]
        public void TearDown()
        {
            L10n.Language = _savedLanguage;
        }

        [Test]
        public void NewShallow_IsAddedOnlyWhenThePlaceCountGrew_Japanese()
        {
            L10n.Language = UiLanguage.Japanese;
            string grew = PoseEscapeReport.DescribeNewShallow(false, true, 45, 52, 7.2f, 0.9f);
            StringAssert.Contains("逃がした所の深さは減りました（7.2 → 0.9 mm）", grew);
            StringAssert.Contains("浅い所が新しく出ています", grew);
            StringAssert.Contains("か所 45 → 52", grew);
            StringAssert.Contains("おすすめの直し方", grew);

            Assert.That(PoseEscapeReport.DescribeNewShallow(false, true, 45, 30, 7.2f, 0.9f), Is.Empty, "減ったときは足さない");
            Assert.That(PoseEscapeReport.DescribeNewShallow(false, true, 45, 45, 7.2f, 0.9f), Is.Empty, "同じときは足さない");
            Assert.That(PoseEscapeReport.DescribeNewShallow(true, true, 45, 52, 7.2f, 0.9f), Is.Empty, "戻したときは足さない（形は変わっていない）");
            Assert.That(PoseEscapeReport.DescribeNewShallow(false, false, 45, 52, 7.2f, 0.9f), Is.Empty, "調べ直せなかったときは足さない");
        }

        [Test]
        public void NewShallow_IsAddedOnlyWhenThePlaceCountGrew_English()
        {
            L10n.Language = UiLanguage.English;
            string grew = PoseEscapeReport.DescribeNewShallow(false, true, 45, 52, 7.2f, 0.9f);
            StringAssert.Contains("(7.2 → 0.9 mm)", grew);
            StringAssert.Contains("New shallow places appeared", grew);
            StringAssert.Contains("places 45 → 52", grew);
            StringAssert.Contains("Suggested fixes", grew);

            Assert.That(PoseEscapeReport.DescribeNewShallow(false, true, 45, 30, 7.2f, 0.9f), Is.Empty);
            Assert.That(PoseEscapeReport.DescribeNewShallow(false, true, 45, 45, 7.2f, 0.9f), Is.Empty);
            Assert.That(PoseEscapeReport.DescribeNewShallow(true, true, 45, 52, 7.2f, 0.9f), Is.Empty);
        }

        [Test]
        public void NewShallow_UsesTheNumbersOfTheReport()
        {
            L10n.Language = UiLanguage.Japanese;
            var report = new PoseEscapeReport
            {
                Rechecked = true,
                ScopeRegionsBefore = 45,
                ScopeRegionsAfter = 52,
                Effect = new PoseEscapeEffect { PushedPairs = 10, PushedDepthBefore = 0.0072f, PushedDepthAfter = 0.0009f },
            };
            string text = report.DescribeNewShallow();
            StringAssert.Contains("7.2 → 0.9 mm", text);
            StringAssert.Contains("45 → 52", text);
            report.Reverted = true;
            Assert.That(report.DescribeNewShallow(), Is.Empty);
        }

        [Test]
        public void RepeatHint_IsShownWhileShallowPlacesRemain_ButNotRightAfterAnUndo()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                string hint = PoseFixAdvice.DescribeRepeatHint(false, true, 3);
                StringAssert.Contains(language == UiLanguage.Japanese ? "もう一度押せます" : "press it again", hint);
                Assert.That(PoseFixAdvice.DescribeRepeatHint(false, true, 0), Is.Empty, "浅い所が無ければ言わない");
                Assert.That(PoseFixAdvice.DescribeRepeatHint(true, true, 3), Is.Empty, "戻した直後は勧めない");
                Assert.That(PoseFixAdvice.DescribeRepeatHint(false, false, 3), Is.Empty, "逃がしたあとの結果でなければ言わない");
            }
        }

        // 結果を手で作る（体あり、頂点 10）。よく使うポーズ（歩く）に浅い所、大きく動くポーズ（座る）に深い所
        static PoseCheckResult SyntheticResult()
        {
            var result = new PoseCheckResult { HasBody = true, Summary = new PoseCheckSummary(10) };
            void Add(string id, int group, params (int Vertex, float Depth)[] items)
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
                result.Entries.Add(new PoseCheckEntry { Pose = PoseLibrary.Find(id), Stage = 1f, Result = p });
                result.Summary.Add(p);
            }
            Add("walk", 0, (1, 0.003f), (2, 0.004f));
            Add("sit", 1, (6, 0.06f), (7, 0.05f));
            result.BuildScopes();
            result.BuildPlans(false, null);
            return result;
        }

        [Test]
        public void Advice_CarriesTheRepeatHintOnlyForTheResultAfterTheEscape()
        {
            L10n.Language = UiLanguage.Japanese;
            PoseCheckResult after = SyntheticResult();
            PoseFixAdvice advice = PoseFixAdvice.From(after, null);
            Assert.That(advice.EscapeRegions, Is.EqualTo(1));
            Assert.That(advice.RepeatEscapeHint, Is.Empty, "まだ逃がしていない");

            var moved = new PoseEscapeReport { Rechecked = true, After = after, Before = SyntheticResult(), IncludesLargePoses = false };
            StringAssert.Contains("もう一度押せます", PoseFixAdvice.From(after, moved).RepeatEscapeHint);

            var undone = new PoseEscapeReport { Rechecked = true, Reverted = true, After = SyntheticResult(), Before = after, IncludesLargePoses = false };
            PoseFixAdvice reverted = PoseFixAdvice.From(after, undone);
            Assert.That(reverted.RepeatEscapeHint, Is.Empty, "戻した直後は勧めない");
            Assert.That(reverted.EscapeRegions, Is.EqualTo(0));
        }

        [Test]
        public void DescribeLargeShort_IsOneShortLine_WithoutThePhysBoneStory()
        {
            PoseCheckResult result = SyntheticResult();
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                string line = result.DescribeLargeShort();
                StringAssert.Contains(language == UiLanguage.Japanese ? "大きく動くポーズ: 1 / 1 ポーズで 1 か所" : "Big-motion poses: 1 place(s) in 1 of 1 poses", line);
                StringAssert.DoesNotContain("PhysBone", line);
                StringAssert.DoesNotContain(language == UiLanguage.Japanese ? "座る" : "squat", line);
                Assert.That(line.Length, Is.LessThan(80));
                StringAssert.Contains("PhysBone", result.DescribeLarge(), "くわしい文は、くわしい説明と記録に残る");
            }
            L10n.Language = UiLanguage.Japanese;
            Assert.That(new PoseCheckResult { HasBody = true, Summary = new PoseCheckSummary(1) }.DescribeLargeShort(), Is.Empty);
        }

        [Test]
        public void PoseListFoldoutLabel_ShowsTheCount()
        {
            L10n.Language = UiLanguage.Japanese;
            Assert.That(ClothEditWindow.PoseListFoldoutLabel(5), Is.EqualTo("突き抜けたポーズの一覧（5 件）"));
            StringAssert.DoesNotContain("0 件", ClothEditWindow.PoseListFoldoutLabel(0));
            L10n.Language = UiLanguage.English;
            StringAssert.Contains("(5)", ClothEditWindow.PoseListFoldoutLabel(5));
        }
    }
}
