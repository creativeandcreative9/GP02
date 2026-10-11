using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class BoneWeightMathTests
{
    [Theory]
    [InlineData("LeftArm", "RightArm")]
    [InlineData("RightHand", "LeftHand")]
    [InlineData("Arm.L", "Arm.R")]
    [InlineData("Arm.r", "Arm.l")]
    [InlineData("UpperLeg_L", "UpperLeg_R")]
    [InlineData("L_UpperArm", "R_UpperArm")]
    [InlineData("Shoulder L", "Shoulder R")]
    [InlineData("Foot_Left", "Foot_Right")]
    [InlineData("left_hand", "right_hand")]
    [InlineData("HandLeft", "HandRight")]
    [InlineData("LHand", "RHand")]
    [InlineData("ArmL", "ArmR")]
    [InlineData("左腕", "右腕")]
    [InlineData("Bip001 L Thigh", "Bip001 R Thigh")]
    [InlineData("J_Bip_L_UpperArm", "J_Bip_R_UpperArm")]
    public void BoneNameMirror_FindsTheOtherSide(string name, string expected)
    {
        Assert.True(BoneNameMirror.TryGetMirror(name, out string mirror));
        Assert.Equal(expected, mirror);
        Assert.True(BoneNameMirror.TryGetMirror(expected, out string back));
        Assert.Equal(name, back, ignoreCase: true);
    }

    [Theory]
    [InlineData("Spine")]
    [InlineData("Hips")]
    [InlineData("Lower")]
    [InlineData("Root")]
    [InlineData("Head")]
    [InlineData("Chest")]
    [InlineData("")]
    public void BoneNameMirror_ReturnsFalseForCenterBones(string name)
    {
        Assert.False(BoneNameMirror.TryGetMirror(name, out _));
    }

    [Fact]
    public void BuildIndexMap_PairsBonesByNameAndLeavesOthersAlone()
    {
        var names = new[] { "Hips", "Arm.L", "Arm.R", "Leg.L", "Head" };

        int[] map = BoneNameMirror.BuildIndexMap(names);

        Assert.Equal(new[] { 0, 2, 1, 3, 4 }, map);   // Leg.L の相手 Leg.R は無いので自分のまま
    }

    static BoneWeightSets Sets(params (int bone, float weight)[][] vertices)
    {
        var offsets = new int[vertices.Length + 1];
        var bones = new List<int>();
        var weights = new List<float>();
        for (int v = 0; v < vertices.Length; v++)
        {
            foreach (var (bone, weight) in vertices[v])
            {
                bones.Add(bone);
                weights.Add(weight);
            }
            offsets[v + 1] = bones.Count;
        }
        return new BoneWeightSets(offsets, bones.ToArray(), weights.ToArray());
    }

    static float WeightOf(BoneWeightSets sets, int vertex, int bone)
    {
        float total = 0f;
        for (int k = sets.Offsets[vertex]; k < sets.Offsets[vertex + 1]; k++)
        {
            if (sets.Bones[k] == bone) total += sets.Weights[k];
        }
        return total;
    }

    [Fact]
    public void MapBones_SwapsOnlySelectedVerticesAndMergesDuplicates()
    {
        BoneWeightSets sets = Sets(
            new[] { (1, 0.7f), (0, 0.3f) },
            new[] { (1, 0.5f), (2, 0.5f) },
            new[] { (3, 1f) });
        int[] map = { 0, 2, 1, 3 };

        BoneWeightSets result = BoneWeightMath.MapBones(sets, map, new[] { true, true, false });

        Assert.Equal(0.7f, WeightOf(result, 0, 2), 6);
        Assert.Equal(0.3f, WeightOf(result, 0, 0), 6);
        Assert.Equal(0f, WeightOf(result, 0, 1), 6);
        // 頂点 1 は 1 と 2 が入れ替わるだけで、合計は 1 のまま
        Assert.Equal(0.5f, WeightOf(result, 1, 1), 6);
        Assert.Equal(0.5f, WeightOf(result, 1, 2), 6);
        Assert.Equal(1f, WeightOf(result, 2, 3), 6);

        // 2 つのボーンが同じボーンに写るなら足し合わせる
        BoneWeightSets merged = BoneWeightMath.MapBones(sets, new[] { 0, 1, 1, 3 }, new[] { false, true, false });
        Assert.Equal(1f, WeightOf(merged, 1, 1), 6);
        Assert.Equal(1, merged.Offsets[2] - merged.Offsets[1]);
    }

    [Fact]
    public void Smooth_BlendsSelectedVerticesTowardTheirNeighborsAndKeepsTheSumAtOne()
    {
        // 一列に並んだ 5 頂点の帯。左の 2 つはボーン 0、右の 3 つはボーン 1
        var positions = new Vector3[10];
        var triangles = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            positions[i] = new Vector3(i * 0.01f, 0, 0);
            positions[5 + i] = new Vector3(i * 0.01f, 0.01f, 0);
        }
        for (int i = 0; i < 4; i++) triangles.AddRange(new[] { i, i + 5, i + 1, i + 1, i + 5, i + 6 });
        var vertexSets = new (int, float)[10][];
        for (int i = 0; i < 5; i++)
        {
            vertexSets[i] = i < 2 ? new[] { (0, 1f) } : new[] { (1, 1f) };
            vertexSets[5 + i] = vertexSets[i];
        }
        BoneWeightSets sets = Sets(vertexSets);
        var selected = new bool[10];
        selected[2] = selected[7] = true;

        BoneWeightSets once = BoneWeightMath.Smooth(sets, selected, positions, triangles.ToArray(), 1, 4);
        BoneWeightSets twice = BoneWeightMath.Smooth(sets, selected, positions, triangles.ToArray(), 2, 4);

        Assert.True(WeightOf(once, 2, 0) > 0.05f && WeightOf(once, 2, 0) < 0.9f, "頂点 2 は境目の側のウェイトが混ざる");
        Assert.Equal(1f, WeightOf(once, 2, 0) + WeightOf(once, 2, 1), 5);
        Assert.Equal(1f, WeightOf(twice, 2, 0) + WeightOf(twice, 2, 1), 5);
        Assert.Equal(1f, WeightOf(once, 0, 0), 6);   // 選んでいない頂点は変わらない
        Assert.Equal(1f, WeightOf(once, 4, 1), 6);
        Assert.Same(sets, BoneWeightMath.Smooth(sets, selected, positions, triangles.ToArray(), 0, 4));

        // 付けるボーンの数の上限を守る
        BoneWeightSets limited = BoneWeightMath.Smooth(sets, selected, positions, triangles.ToArray(), 3, 1);
        Assert.Equal(1, limited.Offsets[3] - limited.Offsets[2]);
        Assert.Equal(1f, limited.Weights[limited.Offsets[2]], 6);
    }

    [Fact]
    public void TakeFrom_CopiesTheDonorWeights()
    {
        BoneWeightSets sets = Sets(new[] { (0, 1f) }, new[] { (1, 0.6f), (2, 0.4f) }, new[] { (3, 1f) });

        BoneWeightSets result = BoneWeightMath.TakeFrom(sets, new[] { 1, 1, 0 });

        Assert.Equal(0.6f, WeightOf(result, 0, 1), 6);
        Assert.Equal(0.4f, WeightOf(result, 0, 2), 6);
        Assert.Equal(0.6f, WeightOf(result, 1, 1), 6);
        Assert.Equal(1f, WeightOf(result, 2, 0), 6);
    }
}
