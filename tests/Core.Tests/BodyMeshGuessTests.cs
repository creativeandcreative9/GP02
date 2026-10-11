using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>メッシュが体（素体・顔）らしいかの推定。名前と、表情のシェイプキーの数で決める。</summary>
public class BodyMeshGuessTests
{
    static readonly string[] None = new string[0];

    [Theory]
    [InlineData("Body")]
    [InlineData("body")]
    [InlineData("BODY")]
    [InlineData("Body.001")]
    [InlineData("Body_Mesh")]
    [InlineData("Kikyo_Body")]
    [InlineData("BodyMesh")]
    [InlineData("UpperBody")]
    [InlineData("Face")]
    [InlineData("Head")]
    [InlineData("Head_Face")]
    [InlineData("素体")]
    [InlineData("顔")]
    [InlineData("体")]
    [InlineData("体_メッシュ")]
    [InlineData("ボディ")]
    [InlineData("Avatar_ボディ")]
    [InlineData("フェイス")]
    public void BodyLikeNames_AreBodies(string name)
    {
        Assert.True(BodyMeshGuess.IsLikelyBody(name, None, out string reason), name);
        Assert.StartsWith("名前に", reason);
    }

    [Theory]
    [InlineData("Skirt")]
    [InlineData("Tights")]
    [InlineData("Sleeves")]
    [InlineData("Jacket_Mesh")]
    [InlineData("スカート")]
    [InlineData("体操服")]            // 「体」は、ほかの言葉の一部のときは体とみなさない
    [InlineData("体育着")]
    [InlineData("Bodysuit")]          // 体を包む服
    [InlineData("BodyStocking")]
    [InlineData("Headband")]
    [InlineData("HeadPhone")]
    [InlineData("Headset_L")]
    [InlineData("FaceMask")]
    [InlineData("ボディスーツ")]
    [InlineData("")]
    [InlineData("   ")]
    public void GarmentLikeNames_AreNotBodies(string name)
    {
        Assert.False(BodyMeshGuess.IsLikelyBody(name, None, out string reason), name);
        Assert.Equal("", reason);
    }

    [Fact]
    public void NullName_IsNotABody_ButExpressionShapesStillCount()
    {
        Assert.False(BodyMeshGuess.IsLikelyBody(null, null));
        Assert.True(BodyMeshGuess.IsLikelyBody(null, new[] { "vrc.v_aa", "vrc.v_ih", "vrc.v_ou" }));
    }

    [Fact]
    public void ThreeOrMoreExpressionShapes_MakeAFace_EvenWithAGarmentLikeName()
    {
        // 名前に手がかりが無くても、表情のシェイプキーが 3 つ以上あれば体（顔のあるメッシュ）
        var shapes = new[] { "vrc.v_sil", "vrc.v_pp", "vrc.v_ff", "Hip_Big" };
        Assert.True(BodyMeshGuess.IsLikelyBody("Mesh1", shapes, out string reason));
        Assert.Equal("表情のシェイプキーが 3 個ある", reason);
        Assert.False(BodyMeshGuess.IsLikelyBody("Mesh1", new[] { "vrc.v_sil", "vrc.v_pp", "Hip_Big" }), "2 個では足りない");
    }

    [Theory]
    [InlineData("vrc.v_sil", true)]
    [InlineData("VRC.V_AA", true)]
    [InlineData("blink", true)]
    [InlineData("Blink_L", true)]
    [InlineData("eye_blink", true)]
    [InlineData("まばたき", true)]
    [InlineData("まばたき_右", true)]
    [InlineData("Fcl_MTH_Joy", true)]
    [InlineData("MTH_A", true)]
    [InlineData("mth_o", true)]
    [InlineData("Fcl_EYE_Close", true)]
    [InlineData("viseme_aa", true)]
    [InlineData("Hip_Big", false)]
    [InlineData("Fat", false)]
    [InlineData("GP02_1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ExpressionShapeNames(string name, bool expected)
    {
        Assert.Equal(expected, BodyMeshGuess.IsExpressionShape(name));
    }

    [Fact]
    public void CountExpressionShapes_CountsOnlyTheExpressionOnes()
    {
        var shapes = new[] { "vrc.v_sil", "Hip_Big", "Blink", "GP02_1", "まばたき", "MTH_A" };
        Assert.Equal(4, BodyMeshGuess.CountExpressionShapes(shapes));
        Assert.Equal(0, BodyMeshGuess.CountExpressionShapes(null));
        Assert.Equal(0, BodyMeshGuess.CountExpressionShapes(new string[0]));
    }

    [Fact]
    public void NameWinsOverExpressionShapes_InTheReason()
    {
        Assert.True(BodyMeshGuess.IsLikelyBody("Body", new[] { "vrc.v_sil", "vrc.v_pp", "vrc.v_ff" }, out string reason));
        Assert.Contains("body", reason);
    }

    [Fact]
    public void ATypicalAvatar_BodyAndFaceAreFoundAndClothesAreNot()
    {
        var visemes = new[] { "vrc.v_sil", "vrc.v_pp", "vrc.v_ff", "vrc.v_th", "vrc.v_dd", "vrc.v_kk", "vrc.v_ch", "vrc.v_ss", "vrc.v_nn", "vrc.v_rr", "vrc.v_aa", "vrc.v_e", "vrc.v_ih", "vrc.v_oh", "vrc.v_ou", "Blink" };
        Assert.True(BodyMeshGuess.IsLikelyBody("Body", visemes));
        Assert.True(BodyMeshGuess.IsLikelyBody("Mesh_A", visemes));
        Assert.False(BodyMeshGuess.IsLikelyBody("Skirt", new[] { "Hip_Big" }));
        Assert.False(BodyMeshGuess.IsLikelyBody("Tights", None));
        Assert.False(BodyMeshGuess.IsLikelyBody("Sleeves", None));
    }
}
