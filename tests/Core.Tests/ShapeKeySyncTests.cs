using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

/// <summary>
/// シェイプキーのデータと、メッシュに今入っている BlendShape を比べる（メッシュに反映済みか）ことのテスト。
/// 名前と、各フレームの重み・差分（許容誤差つき）で比べ、足りないもの・違うもの・余分なものを数える。
/// </summary>
public class ShapeKeySyncTests
{
    static Vector3[] Ramp(int count, float scale)
    {
        var deltas = new Vector3[count];
        for (int i = 0; i < count; i++) deltas[i] = new Vector3(i * scale, -i * scale * 0.5f, scale);
        return deltas;
    }

    static NamedShape Shape(string name, params (float Weight, Vector3[] Deltas)[] frames)
    {
        var list = new List<ShapeFrame>();
        foreach (var (weight, deltas) in frames) list.Add(new ShapeFrame(weight, deltas));
        return new NamedShape(name, list);
    }

    // 今のメッシュの代わり（名前ごとのフレーム）。読んだ名前を覚えておく
    sealed class FakeMesh
    {
        public readonly List<string> Names = new List<string>();
        public readonly Dictionary<string, IReadOnlyList<ShapeFrame>> Frames = new Dictionary<string, IReadOnlyList<ShapeFrame>>();
        public readonly List<string> Read = new List<string>();

        public FakeMesh With(NamedShape shape)
        {
            Names.Add(shape.Name);
            Frames[shape.Name] = shape.Frames;
            return this;
        }

        public IReadOnlyList<ShapeFrame> ReadFrames(string name)
        {
            Read.Add(name);
            return Frames[name];
        }
    }

    [Fact]
    public void SameData_HasNoChanges_EvenWithTinyDifferencesAndAnotherFrameOrder()
    {
        Vector3[] a = Ramp(50, 0.001f);
        Vector3[] nearlyA = (Vector3[])a.Clone();
        nearlyA[10] += new Vector3(4e-6f, -4e-6f, 0f);
        var expected = new List<NamedShape>
        {
            Shape("GP02_1", (100f, a)),
            Shape("Blink", (50f, Ramp(50, 0.002f)), (100f, Ramp(50, 0.004f))),
        };
        // メッシュのフレームは重みの順。データは逆の順でも同じ
        FakeMesh mesh = new FakeMesh()
            .With(Shape("Smile", (100f, Ramp(50, 0.003f))))
            .With(Shape("GP02_1", (100f, nearlyA)))
            .With(Shape("Blink", (100f, Ramp(50, 0.004f)), (50f, Ramp(50, 0.002f))));
        List<ShapeKeyChange> changes = ShapeKeySync.Compare(expected, mesh.Names, mesh.ReadFrames, new[] { "Smile" }, ShapeKeySync.DefaultTolerance);
        Assert.Empty(changes);
        // 元のメッシュから残る BlendShape（Smile）は、中身を読まない
        Assert.DoesNotContain("Smile", mesh.Read);
    }

    [Fact]
    public void Changes_AreAddUpdateAndRemove()
    {
        Vector3[] a = Ramp(20, 0.001f);
        Vector3[] moved = (Vector3[])a.Clone();
        moved[3] += new Vector3(0f, 2e-4f, 0f);
        var expected = new List<NamedShape>
        {
            Shape("New", (100f, a)),
            Shape("Edited", (100f, moved)),
            Shape("Reweighted", (50f, a), (100f, a)),
            Shape("Same", (100f, a)),
        };
        FakeMesh mesh = new FakeMesh()
            .With(Shape("Edited", (100f, a)))
            .With(Shape("Reweighted", (40f, a), (100f, a)))
            .With(Shape("Same", (100f, a)))
            .With(Shape("Deleted", (100f, a)));
        List<ShapeKeyChange> changes = ShapeKeySync.Compare(expected, mesh.Names, mesh.ReadFrames, null, ShapeKeySync.DefaultTolerance);
        Assert.Equal(new[]
        {
            new ShapeKeyChange("New", ShapeKeyChangeKind.Add),
            new ShapeKeyChange("Edited", ShapeKeyChangeKind.Update),
            new ShapeKeyChange("Reweighted", ShapeKeyChangeKind.Update),
            new ShapeKeyChange("Deleted", ShapeKeyChangeKind.Remove),
        }, changes);
        ShapeKeySync.Count(changes, out int add, out int update, out int remove);
        Assert.Equal((1, 2, 1), (add, update, remove));

        // 許容誤差を大きくすると、少し動かしたものは同じとみなす
        List<ShapeKeyChange> loose = ShapeKeySync.Compare(expected, mesh.Names, mesh.ReadFrames, null, 1e-3f);
        Assert.DoesNotContain(new ShapeKeyChange("Edited", ShapeKeyChangeKind.Update), loose);
    }

    [Fact]
    public void KeptNames_AreNotRemoved_ButAreAddedWhenMissing_AndTheLastOfTheSameNameWins()
    {
        Vector3[] a = Ramp(10, 0.001f), b = Ramp(10, 0.002f);
        var expected = new List<NamedShape>
        {
            Shape("Dup", (100f, a)),
            Shape("Dup", (100f, b)),
        };
        FakeMesh mesh = new FakeMesh().With(Shape("Dup", (100f, b))).With(Shape("Original", (100f, a)));
        // 同じ名前は後ろのもの（b）と比べる。元のメッシュの BlendShape は消さない。元のメッシュにあって、今のメッシュに無いものは加わる
        List<ShapeKeyChange> changes = ShapeKeySync.Compare(expected, mesh.Names, mesh.ReadFrames, new[] { "Original", "Missing", "Dup" }, ShapeKeySync.DefaultTolerance);
        Assert.Equal(new[] { new ShapeKeyChange("Missing", ShapeKeyChangeKind.Add) }, changes);
    }

    [Fact]
    public void SameFramesAndDeltas_HandleEmptyAndDifferentCounts()
    {
        Vector3[] a = Ramp(8, 0.001f);
        Assert.True(ShapeKeySync.SameDeltas(a, (Vector3[])a.Clone(), 1e-5f));
        Assert.False(ShapeKeySync.SameDeltas(a, Ramp(7, 0.001f), 1e-5f));
        // 空の差分は、すべて 0 と同じ
        Assert.True(ShapeKeySync.SameDeltas(Array.Empty<Vector3>(), new Vector3[8], 1e-5f));
        Assert.False(ShapeKeySync.SameDeltas(Array.Empty<Vector3>(), a, 1e-5f));
        Assert.True(ShapeKeySync.SameDeltas(null, new Vector3[3], 1e-5f));

        var one = new List<ShapeFrame> { new ShapeFrame(100f, a) };
        var two = new List<ShapeFrame> { new ShapeFrame(50f, a), new ShapeFrame(100f, a) };
        Assert.False(ShapeKeySync.SameFrames(one, two, 1e-5f));
        Assert.True(ShapeKeySync.SameFrames(null, new List<ShapeFrame>(), 1e-5f));
        Assert.False(ShapeKeySync.SameFrames(one, null, 1e-5f));
        Assert.True(ShapeKeySync.SameFrames(one, new List<ShapeFrame> { new ShapeFrame(100f + 5e-4f, (Vector3[])a.Clone()) }, 1e-5f));
        Assert.False(ShapeKeySync.SameFrames(one, new List<ShapeFrame> { new ShapeFrame(99f, a) }, 1e-5f));
        Assert.Throws<ArgumentNullException>(() => ShapeKeySync.Compare(null!, new string[0], _ => one, null, 1e-5f));
    }
}
