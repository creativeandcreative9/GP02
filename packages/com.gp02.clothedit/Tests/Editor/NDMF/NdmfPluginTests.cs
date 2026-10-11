using GP02.EditorTools;
using GP02.Tests;
using nadena.dev.ndmf;
using nadena.dev.ndmf.runtime.components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(FakeModularAvatarPlugin))]

namespace GP02.Tests
{
    /// <summary>
    /// 本物の Modular Avatar の代わりに、同じ名前（nadena.dev.modular-avatar）で動く目印の Plugin。
    /// GP02 の Plugin が先に動いたか（このとき GP02ShapeKeys がもう無いか）を記録する。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class FakeModularAvatarPlugin : Plugin<FakeModularAvatarPlugin>
    {
        /// <summary>この Plugin が動いたとき、アバターに GP02ShapeKeys が残っていなかったか。まだ動いていなければ null。</summary>
        public static bool? ShapeKeysWereGone;

        public override string QualifiedName => "nadena.dev.modular-avatar";
        public override string DisplayName => "Fake Modular Avatar (GP02 test)";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming).Run("Fake MA pass", ctx =>
            {
                ShapeKeysWereGone = ctx.AvatarRootObject.GetComponentInChildren<GP02ShapeKeys>(true) == null;
            });
        }
    }

    /// <summary>
    /// NDMF の Plugin を、NDMF の <c>AvatarProcessor.ProcessAvatar</c> を通して確かめる（NDMF が入っているプロジェクトだけで動く）。
    /// 素の Unity でルートと認識されるよう、<see cref="NDMFAvatarRoot"/> を付けたオブジェクトをアバターとして扱う。
    /// </summary>
    public class NdmfPluginTests : ShapeKeyUiTestBase
    {
        [TearDown]
        public void CleanGenerated()
        {
            AvatarProcessor.CleanTemporaryAssets();
        }

        sealed class Avatar
        {
            public GameObject Root;
            public Mesh Source;
            public SkinnedMeshRenderer Renderer;
            public GP02ShapeKeys Component;
            public GP02ShapeKeyData Data;
        }

        Avatar MakeAvatar(ShapeKeyApplyMode mode, ShapeKeyInitialWeight weight = ShapeKeyInitialWeight.Zero)
        {
            var a = new Avatar { Source = CreateGridMesh("Skirt") };
            int n = a.Source.vertexCount;
            ShapeKeyEntry lift = Entry("Lift", Ramp(n, 0.003f));
            lift.PreviewWeight = 25f;
            a.Data = NewData(a.Source, lift);
            a.Root = Track(new GameObject("TestAvatar"));
            a.Root.AddComponent<Animator>();
            a.Root.AddComponent<NDMFAvatarRoot>();
            var child = new GameObject("Skirt");
            child.transform.SetParent(a.Root.transform, false);
            a.Renderer = child.AddComponent<SkinnedMeshRenderer>();
            a.Renderer.sharedMesh = a.Source;
            a.Component = child.AddComponent<GP02ShapeKeys>();
            a.Component.Data = a.Data;
            a.Component.SourceMesh = a.Source;
            a.Component.ApplyMode = mode;
            a.Component.InitialWeight = weight;
            return a;
        }

        [Test]
        public void TheIntegrationAnnouncesItself()
        {
            Assert.That(NonDestructiveSupport.IsAvailable, Is.True);
        }

        [Test]
        public void NonDestructive_ProcessedCopyGetsTheBlendShapeAndTheSceneKeepsItsMesh()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            GameObject clone = Track(Object.Instantiate(a.Root));

            AvatarProcessor.ProcessAvatar(clone);

            var renderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(renderer.sharedMesh, Is.Not.SameAs(a.Source));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Lift"));
            AssertSame(Ramp(a.Source.vertexCount, 0.003f), FrameVertices(renderer.sharedMesh, 0, 0), 1e-6f);
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(0f));
            Assert.That(clone.GetComponentInChildren<GP02ShapeKeys>(true), Is.Null, "コンポーネントは消える");

            Assert.That(a.Renderer.sharedMesh, Is.SameAs(a.Source), "元のシーンのメッシュは変わらない");
            Assert.That(a.Source.blendShapeCount, Is.EqualTo(0));
            Assert.That(a.Component, Is.Not.Null, "元のシーンのコンポーネントは残る");
        }

        [Test]
        public void NonDestructive_TheRecordedWeightChangeIsAppliedAtBuild_WithTheMissingBoneAdded()
        {
            // ウェイトを体に寄せた結果（データに記録したボーンウェイトの変更）も、ビルドの複製に入る。レンダラーに無いボーン C は複製の中から探して足す
            WeightEditTestAvatar a = WeightEditTestAvatar.Create(CreateGridMesh("Skirt"), "TestAvatar");
            Track(a.Root);
            a.Root.AddComponent<NDMFAvatarRoot>();
            GP02ShapeKeyData data = NewData(a.Source, Entry("Lift", Ramp(a.Source.vertexCount, 0.003f)));
            data.WeightEdit = a.CreateEdit();
            var component = a.Renderer.gameObject.AddComponent<GP02ShapeKeys>();
            component.Data = data;
            component.SourceMesh = a.Source;
            component.ApplyMode = ShapeKeyApplyMode.NonDestructive;
            GameObject clone = Track(Object.Instantiate(a.Root));

            AvatarProcessor.ProcessAvatar(clone);

            var renderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(renderer.sharedMesh, Is.Not.SameAs(a.Source));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(renderer.bones.Length, Is.EqualTo(3));
            Assert.That(renderer.bones[2], Is.EqualTo(clone.transform.Find("Armature/BoneC")), "複製のボーン C を足す");
            Assert.That(renderer.sharedMesh.bindposes.Length, Is.EqualTo(3));
            Assert.That(WeightEditTestAvatar.WeightOf(renderer.sharedMesh, 0, 2), Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(WeightEditTestAvatar.WeightOf(renderer.sharedMesh, 100, 0), Is.EqualTo(1f), "変えていない頂点はそのまま");
            Assert.That(clone.GetComponentInChildren<GP02ShapeKeys>(true), Is.Null);

            Assert.That(a.Renderer.sharedMesh, Is.SameAs(a.Source), "元のシーンのメッシュは変わらない");
            Assert.That(a.Renderer.bones.Length, Is.EqualTo(2), "元のシーンのボーンも変わらない");
        }

        [Test]
        public void NonDestructive_PreviewWeightIsUsedWhenAsked()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive, ShapeKeyInitialWeight.PreviewWeight);
            GameObject clone = Track(Object.Instantiate(a.Root));

            AvatarProcessor.ProcessAvatar(clone);

            Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25f).Within(1e-4f));
        }

        [Test]
        public void ExportedMesh_ProcessedCopyOnlyLosesTheComponent()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.ExportedMesh);
            GameObject clone = Track(Object.Instantiate(a.Root));

            AvatarProcessor.ProcessAvatar(clone);

            Assert.That(clone.GetComponentInChildren<GP02ShapeKeys>(true), Is.Null);
            Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(a.Source));
        }

        [Test]
        public void TheCreatedMeshIsSavedByTheAssetSaver()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            GameObject clone = Track(Object.Instantiate(a.Root));

            AvatarProcessor.ProcessAvatar(clone);

            Mesh mesh = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
            Assert.That(UnityEditor.EditorUtility.IsPersistent(mesh), Is.True, "NDMF の AssetSaver で保存される");
        }

        [Test]
        public void ThePluginRunsBeforeModularAvatar()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            GameObject clone = Track(Object.Instantiate(a.Root));
            FakeModularAvatarPlugin.ShapeKeysWereGone = null;

            AvatarProcessor.ProcessAvatar(clone);

            Assert.That(FakeModularAvatarPlugin.ShapeKeysWereGone, Is.True, "MA より前に GP02 の処理が済んでいる");
        }
    }
}
