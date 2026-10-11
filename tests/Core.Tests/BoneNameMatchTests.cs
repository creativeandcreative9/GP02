using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>服だけの別のアーマチュアのボーンを、アバターのボーンに対応付ける（BoneNameMatch）テスト。</summary>
public class BoneNameMatchTests
{
    // アバターのボーン: 名前・親・位置（Humanoid の一部と、太ももの下のねじれ用のボーン、背骨の下の同じ名前のボーン）
    static readonly string[] AvatarNames = { "Hips", "Spine", "Chest", "LeftUpperLeg", "LeftLowerLeg", "RightUpperLeg", "Twist", "Twist" };
    static readonly int[] AvatarParents = { -1, 0, 1, 0, 3, 0, 3, 1 };
    static readonly Vector3[] AvatarPositions =
    {
        new Vector3(0f, 0.85f, 0f), new Vector3(0f, 0.95f, 0f), new Vector3(0f, 1.07f, 0f),
        new Vector3(-0.09f, 0.80f, 0f), new Vector3(-0.09f, 0.42f, 0f), new Vector3(0.09f, 0.80f, 0f),
        new Vector3(-0.09f, 0.62f, 0f), new Vector3(0f, 1.00f, 0f),
    };

    [Theory]
    [InlineData("Hips.001", "hips")]
    [InlineData("Left Upper Leg (1)", "leftupperleg")]
    [InlineData("UpperLeg_L", "upperlegl")]
    [InlineData("J_Bip_C_Hips", "jbipchips")]
    [InlineData("Spine.01", "spine01")]
    [InlineData("", "")]
    public void Normalize_LowersCase_AndDropsSeparatorsAndCopyMarks(string name, string expected)
    {
        Assert.Equal(expected, BoneNameMatch.Normalize(name));
    }

    [Fact]
    public void InferAffixes_FindsTheCommonPrefixOrSuffix()
    {
        BoneNameMatch.InferAffixes(new[] { "Outfit_Hips", "Outfit_Spine", "Outfit_LeftUpperLeg", "Skirt_Root" }, AvatarNames, out string prefix, out string suffix);
        Assert.Equal("outfit_", prefix);
        Assert.Equal("", suffix);

        BoneNameMatch.InferAffixes(new[] { "Hips_cos", "Spine_cos", "Chest_cos", "LeftUpperLeg_cos" }, AvatarNames, out prefix, out suffix);
        Assert.Equal("", prefix);
        Assert.Equal("_cos", suffix);

        // 同じ名前なら前置き・後置きは無い。1つのボーンだけに付いた飾りは数えない
        BoneNameMatch.InferAffixes(new[] { "Hips", "Spine", "Chest", "Chest_Ribbon" }, AvatarNames, out prefix, out suffix);
        Assert.Equal("", prefix);
        Assert.Equal("", suffix);

        Assert.Equal("Hips", BoneNameMatch.StripAffixes("Outfit_Hips", "outfit_", ""));
        Assert.Equal("hips", BoneNameMatch.StripAffixes("hips_COS", "", "_cos"));
        Assert.Equal("Outfit_", BoneNameMatch.StripAffixes("Outfit_", "outfit_", ""));
    }

    [Fact]
    public void Match_ByPrefixedNames_AndPicksTheSameNameUnderTheMatchingParent()
    {
        // 服のアーマチュア（前置き Outfit_）。Twist は太ももの下のもの
        string[] names = { "Armature", "Outfit_Hips", "Outfit_Spine", "Outfit_LeftUpperLeg", "Outfit_Twist", "Outfit_LeftLowerLeg" };
        int[] parents = { -1, 0, 1, 1, 3, 3 };
        Vector3[] positions =
        {
            Vector3.Zero, AvatarPositions[0], AvatarPositions[1], AvatarPositions[3], AvatarPositions[6] + new Vector3(0.005f, 0f, 0f), AvatarPositions[4],
        };
        int[] match = BoneNameMatch.Match(names, parents, positions, AvatarNames, AvatarParents, AvatarPositions);
        Assert.Equal(new[] { -1, 0, 1, 3, 6, 4 }, match);
    }

    [Fact]
    public void Match_FallsBackToPositionUnderTheParent_AndLeavesUnrelatedBonesUnmatched()
    {
        // 名前の付け方が違う服（Thigh_L）。位置はアバターの太ももと同じ。スカートの根元（腰の 3cm 下）と、遠くの飾りは対応しない
        string[] names = { "Hips", "Thigh_L", "Skirt_Root", "Shin_L", "Bag" };
        int[] parents = { -1, 0, 0, 1, -1 };
        Vector3[] positions =
        {
            AvatarPositions[0], AvatarPositions[3] + new Vector3(0f, 0.01f, 0f), AvatarPositions[0] - new Vector3(0f, 0.03f, 0f),
            AvatarPositions[4], new Vector3(0.5f, 1f, 0.3f),
        };
        int[] match = BoneNameMatch.Match(names, parents, positions, AvatarNames, AvatarParents, AvatarPositions);
        Assert.Equal(new[] { 0, 3, -1, 4, -1 }, match);

        // 許容距離より遠ければ、位置では対応付けない
        match = BoneNameMatch.Match(names, parents, positions, AvatarNames, AvatarParents, AvatarPositions, 0.005f);
        Assert.Equal(-1, match[1]);
    }

    [Fact]
    public void Match_RejectsAParentListedAfterItsChild()
    {
        Assert.Throws<ArgumentException>(() => BoneNameMatch.Match(new[] { "A", "B" }, new[] { 1, -1 }, new[] { Vector3.Zero, Vector3.Zero },
            AvatarNames, AvatarParents, AvatarPositions));
    }
}
