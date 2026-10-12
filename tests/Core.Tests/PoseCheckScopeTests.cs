using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// 動作チェックの集計を、範囲を絞って作る（よく使うポーズだけで直す、など）ことのテスト。
/// 集計に入れない結果も番号は持つので、範囲を絞っても、結果の番号は元の集計と同じになること。
/// 数・最悪の深さ・悪い順・外へ逃がす量・ウェイトを寄せる対象・前後の比べ方が、入れた結果だけで決まること。
/// </summary>
public class PoseCheckScopeTests
{
    // 結果 1つ（頂点・か所・深さ・ポーズで動く割合）
    static PosePenetration Entry(int group, float stage, params (int Vertex, int Region, float Depth, float Gain)[] items)
    {
        var e = new PosePenetration { Group = group, Stage = stage, HasBody = true };
        foreach (var (vertex, region, depth, gain) in items)
        {
            e.Vertices.Add(vertex);
            e.Depths.Add(depth);
            e.VertexRegions.Add(region);
            e.Gains.Add(gain);
            while (e.RegionDepths.Count <= region) e.RegionDepths.Add(0f);
            e.RegionDepths[region] = MathF.Max(e.RegionDepths[region], depth);
            e.MaxDepth = MathF.Max(e.MaxDepth, depth);
        }
        e.RegionCount = e.RegionDepths.Count;
        return e;
    }

    // よく使うポーズ（グループ 0: 歩く、グループ 1: お辞儀）と、大きく動くポーズ（グループ 2: 座る）の結果
    static (PoseCheckSummary All, bool[] Common) Sample()
    {
        var all = new PoseCheckSummary(12);
        // 0: 歩く（途中まで）: 浅い所 1つ（頂点 0, 1）
        all.Add(Entry(0, 0.5f, (0, 0, 0.002f, 0.9f), (1, 0, 0.003f, 0.9f)));
        // 1: 歩く（最後まで）: 浅い所 1つ（頂点 1, 2）と、ウェイトが原因の所（割合の小さい頂点 3）
        all.Add(Entry(0, 1f, (1, 0, 0.004f, 0.9f), (2, 0, 0.002f, 1f), (3, 1, 0.003f, 0.3f)));
        // 2: お辞儀（最後まで）: 貫通なし、体どうしの重なり 1か所
        var bow = Entry(1, 1f);
        bow.OverlapVertices.Add(9);
        bow.OverlapDepths.Add(0.01f);
        bow.OverlapRegionCount = 1;
        bow.MaxStretch = 0.02f;
        all.Add(bow);
        // 3: 座る（最後まで）: 深い所（頂点 5, 6）と、浅い所（頂点 7）。体どうしの重なり 2か所。よく伸びる
        var sit = Entry(2, 1f, (5, 0, 0.06f, 1f), (6, 0, 0.03f, 1f), (7, 1, 0.002f, 1f));
        sit.OverlapRegionCount = 2;
        sit.OverlapVertices.AddRange(new[] { 10, 11 });
        sit.OverlapDepths.AddRange(new[] { 0.01f, 0.02f });
        sit.MaxStretch = 0.5f;
        sit.HighStretch = 0.3f;
        all.Add(sit);
        return (all, new[] { true, true, true, false });
    }

    [Fact]
    public void Select_KeepsTheEntryNumbers_ButCountsOnlyTheIncludedEntries()
    {
        var (all, common) = Sample();
        PoseCheckSummary scoped = all.Select(common);

        // 結果は同じものを同じ順に持つ（番号が同じ）
        Assert.Equal(all.Entries.Count, scoped.Entries.Count);
        for (int e = 0; e < all.Entries.Count; e++) Assert.Same(all.Entries[e], scoped.Entries[e]);
        Assert.True(scoped.IsIncluded(0));
        Assert.False(scoped.IsIncluded(3));
        Assert.False(scoped.IsIncluded(4));
        Assert.Equal(3, scoped.IncludedEntryCount);
        Assert.Equal(2, scoped.IncludedGroupCount);
        Assert.Equal(4, all.IncludedEntryCount);
        Assert.Equal(3, all.IncludedGroupCount);

        // 数: 範囲の中だけ
        Assert.Equal(2, scoped.PenetratingEntryCount);
        Assert.Equal(1, scoped.PenetratingGroupCount);
        Assert.Equal(3, scoped.TotalRegionCount);
        Assert.Equal(0.004f, scoped.MaxDepth);
        Assert.Equal(1, scoped.TotalOverlapRegionCount);
        Assert.Equal(1, scoped.OverlapGroupCount);
        Assert.Equal(0.02f, scoped.MaxStretch);
        Assert.Equal(0f, scoped.HighStretch);
        Assert.Equal(3, all.PenetratingEntryCount);
        Assert.Equal(2, all.PenetratingGroupCount);
        Assert.Equal(5, all.TotalRegionCount);
        Assert.Equal(0.06f, all.MaxDepth);
        Assert.Equal(3, all.TotalOverlapRegionCount);
        Assert.Equal(0.5f, all.MaxStretch);

        // 最悪の深さ: 座る（範囲の外）の頂点は 0
        Assert.Equal(0.002f, scoped.WorstDepth[0]);
        Assert.Equal(0.004f, scoped.WorstDepth[1]);
        Assert.Equal(1, scoped.WorstEntry[1]);
        Assert.Equal(0f, scoped.WorstDepth[5]);
        Assert.Equal(-1, scoped.WorstEntry[5]);
        Assert.Equal(0.06f, all.WorstDepth[5]);
        Assert.Equal(4, scoped.PenetratingVertexCount);
        Assert.Equal(7, all.PenetratingVertexCount);

        // 悪い順: 範囲の中の結果だけ（番号は元の集計の番号）
        Assert.Equal(new[] { 1, 0 }, scoped.RankEntries());
        Assert.Equal(new[] { 3, 1, 0 }, all.RankEntries());

        // 範囲の外だけを選ぶと、座るだけ
        var large = new bool[4];
        large[3] = true;
        PoseCheckSummary outside = all.Select(large);
        Assert.Equal(new[] { 3 }, outside.RankEntries());
        Assert.Equal(1, outside.IncludedGroupCount);
        Assert.Equal(2, outside.TotalRegionCount);

        // null なら同じ範囲のまま、写しを作る。短い配列なら、後ろの結果は入れない
        Assert.Equal(scoped.RankEntries(), scoped.Select(null).RankEntries());
        Assert.Equal(1, all.Select(new[] { true }).IncludedEntryCount);
    }

    [Fact]
    public void Plans_FromAScopedSummary_IgnoreTheEntriesOutsideTheScope()
    {
        var (all, common) = Sample();
        PoseCheckSummary scoped = all.Select(common);

        PoseEscapePlan escape = scoped.PlanEscape(0.002f, 0.02f, 0.7f);
        // 逃がす: 歩く（途中まで）のか所 0、歩く（最後まで）のか所 0。ウェイトが原因: 歩く（最後まで）のか所 1。座るは入れない
        Assert.Equal(2, escape.PushedRegions);
        Assert.Equal(1, escape.LowGainRegions);
        Assert.Equal(0, escape.TooFarRegions);
        Assert.Null(escape.AllowedRegions[3]);
        Assert.Null(escape.AllowedRegions[2]);
        Assert.Equal(new[] { true, false }, escape.AllowedRegions[1]);
        Assert.Equal(0f, escape.WeldedPush[5]);
        Assert.Equal(0f, escape.WeldedPush[7]);
        Assert.Equal((0.004f + 0.002f) / 0.9f, escape.WeldedPush[1], 5);

        // 全部なら、座るの浅い所（頂点 7）も逃がし、深い所は深すぎるので逃がさない
        PoseEscapePlan escapeAll = all.PlanEscape(0.002f, 0.02f, 0.7f);
        Assert.Equal(3, escapeAll.PushedRegions);
        Assert.Equal(1, escapeAll.TooFarRegions);
        Assert.Equal(0.004f, escapeAll.WeldedPush[7], 5);

        WeightFixPlan weights = scoped.PlanWeightFix(0.002f, 0.02f, 0.7f);
        Assert.Equal(1, weights.Regions);
        Assert.Equal(1, weights.LowGainRegions);
        Assert.Equal(1, weights.OverlapRegions);
        Assert.Null(weights.TargetRegions[3]);
        Assert.Equal(1f, weights.WeldedSeed[3]);
        Assert.Equal(0f, weights.WeldedSeed[5]);
        WeightFixPlan weightsAll = all.PlanWeightFix(0.002f, 0.02f, 0.7f);
        Assert.Equal(2, weightsAll.Regions);
        Assert.Equal(1, weightsAll.TooFarRegions);
        Assert.Equal(3, weightsAll.OverlapRegions);
        Assert.Equal(1f, weightsAll.WeldedSeed[5]);
    }

    [Fact]
    public void Effects_CompareOnlyTheEntriesInTheScope()
    {
        var (before, common) = Sample();
        PoseCheckSummary scopedBefore = before.Select(common);
        PoseEscapePlan plan = scopedBefore.PlanEscape(0.002f, 0.02f, 0.7f);

        // 逃がしたあと: 歩くの浅い所は無くなったが、座る（範囲の外）は深くなった
        var after = new PoseCheckSummary(12);
        after.Add(Entry(0, 0.5f));
        after.Add(Entry(0, 1f, (3, 0, 0.003f, 0.3f)));
        after.Add(Entry(1, 1f));
        after.Add(Entry(2, 1f, (5, 0, 0.08f, 1f), (6, 0, 0.05f, 1f), (7, 1, 0.006f, 1f), (8, 1, 0.006f, 1f), (9, 1, 0.006f, 1f), (10, 1, 0.006f, 1f), (11, 1, 0.006f, 1f)));
        PoseCheckSummary scopedAfter = after.Select(common);

        // 範囲の中で比べると、効いた（範囲の外の座るが深くなっても、数えない）
        PoseEscapeEffect effect = plan.MeasureEffect(scopedBefore, scopedAfter, 0.02f);
        Assert.Equal(4, effect.PushedPairs);
        Assert.Equal(4, effect.ClearedPairs);
        Assert.Equal(0.014, effect.TotalDepthBefore, 5);
        Assert.Equal(0.003, effect.TotalDepthAfter, 5);
        Assert.True(effect.Improved(0.6f));

        // 全部で比べると、座るが深くなった分だけ合計が増えるので、効いたとはみなさない
        PoseEscapeEffect whole = plan.MeasureEffect(before, after, 0.02f);
        Assert.True(whole.TotalDepthAfter > whole.TotalDepthBefore);
        Assert.False(whole.Improved(0.6f));

        // ウェイトを寄せる対象の前後も同じ
        WeightFixPlan weights = scopedBefore.PlanWeightFix(0.002f, 0.02f, 0.7f);
        PoseFixEffect fix = weights.MeasureEffect(scopedBefore, scopedAfter, 0.02f);
        Assert.Equal(1, fix.TargetPairs);
        Assert.Equal(0, fix.ClearedPairs);
        Assert.Equal(0.014, fix.TotalDepthBefore, 5);
        Assert.Equal(0.003, fix.TotalDepthAfter, 5);
        Assert.Equal(scopedBefore.TotalDepth(0.02f), fix.TotalDepthBefore, 6);
    }
}
