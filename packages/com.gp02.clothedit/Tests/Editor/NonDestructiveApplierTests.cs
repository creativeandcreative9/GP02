using System;
using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// ビルド時の適用の本体（<see cref="NonDestructiveApplier"/>）を、NDMF なしで確かめる。
    /// 複製したアバターに適用しても、元のシーンのメッシュとコンポーネントは変わらないことを見る。
    /// </summary>
    public class NonDestructiveApplierTests : ShapeKeyUiTestBase
    {
        sealed class Avatar
        {
            public GameObject Root;
            public Mesh Source;
            public GP02ShapeKeyData Data;
            public SkinnedMeshRenderer Renderer;
            public GP02ShapeKeys Component;
        }

        Avatar MakeAvatar(ShapeKeyApplyMode mode, ShapeKeyInitialWeight initialWeight = ShapeKeyInitialWeight.Zero)
        {
            var a = new Avatar { Source = CreateGridMesh("Skirt") };
            int n = a.Source.vertexCount;
            a.Source.AddBlendShapeFrame("Existing", 100f, Ramp(n, 0.001f), null, null);
            ShapeKeyEntry lift = Entry("Lift", Ramp(n, 0.003f));
            lift.PreviewWeight = 40f;
            a.Data = NewData(a.Source, lift, Entry("Wide", Ramp(n, 0.002f)));
            a.Root = Track(new GameObject("Avatar"));
            var child = new GameObject("Skirt");
            child.transform.SetParent(a.Root.transform, false);
            a.Renderer = child.AddComponent<SkinnedMeshRenderer>();
            a.Renderer.sharedMesh = a.Source;
            a.Component = child.AddComponent<GP02ShapeKeys>();
            a.Component.Data = a.Data;
            a.Component.SourceMesh = a.Source;
            a.Component.ApplyMode = mode;
            a.Component.InitialWeight = initialWeight;
            return a;
        }

        GameObject Clone(Avatar a) => Track(Object.Instantiate(a.Root));

        [Test]
        public void Defaults_KeepTheExistingBehaviour()
        {
            var component = Track(new GameObject("c")).AddComponent<GP02ShapeKeys>();
            Assert.That(component.ApplyMode, Is.EqualTo(ShapeKeyApplyMode.ExportedMesh));
            Assert.That(component.InitialWeight, Is.EqualTo(ShapeKeyInitialWeight.Zero));
        }

        [Test]
        public void NonDestructive_AddsBlendShapesToACopyAndLeavesTheSceneAlone()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            GameObject clone = Clone(a);
            var saved = new List<Object>();

            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(clone, saved.Add);

            var renderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(result.Applied, Is.EqualTo(1));
            Assert.That(result.Removed, Is.EqualTo(1));
            Assert.That(clone.GetComponentInChildren<GP02ShapeKeys>(true), Is.Null, "コンポーネントは消える");
            Assert.That(renderer.sharedMesh, Is.Not.SameAs(a.Source));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(3));
            Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Existing"));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Lift"), Is.EqualTo(1));
            AssertSame(Ramp(a.Source.vertexCount, 0.003f), FrameVertices(renderer.sharedMesh, 1, 0), 1e-6f);
            Assert.That(saved, Is.EquivalentTo(new Object[] { renderer.sharedMesh }), "作ったメッシュを保存に渡す");

            Assert.That(a.Renderer.sharedMesh, Is.SameAs(a.Source), "元のシーンのメッシュは変わらない");
            Assert.That(a.Source.blendShapeCount, Is.EqualTo(1));
            Assert.That(a.Component, Is.Not.Null, "元のシーンのコンポーネントは残る");
        }

        [Test]
        public void NonDestructive_ZeroWeightsByDefault()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            GameObject clone = Clone(a);
            NonDestructiveApplier.Apply(clone, null);
            var renderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Lift")), Is.EqualTo(0f));
            Assert.That(renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Wide")), Is.EqualTo(0f));
        }

        [Test]
        public void NonDestructive_PreviewWeightsAreUsedWhenAsked()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive, ShapeKeyInitialWeight.PreviewWeight);
            GameObject clone = Clone(a);
            NonDestructiveApplier.Apply(clone, null);
            var renderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Lift")), Is.EqualTo(40f).Within(1e-4f));
            Assert.That(renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Wide")), Is.EqualTo(0f));
        }

        [Test]
        public void ExportedMesh_OnlyRemovesTheComponent()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.ExportedMesh);
            GameObject clone = Clone(a);

            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(clone, _ => Assert.Fail("保存するものは無いはず"));

            Assert.That(result.Applied, Is.EqualTo(0));
            Assert.That(result.Removed, Is.EqualTo(1));
            Assert.That(clone.GetComponentInChildren<GP02ShapeKeys>(true), Is.Null);
            Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(a.Source), "メッシュは触らない");
        }

        [Test]
        public void NonDestructive_VertexCountMismatchIsAnErrorNotASilentSkip()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            a.Data.VertexCount += 1;
            GameObject clone = Clone(a);
            Assert.Throws<InvalidOperationException>(() => NonDestructiveApplier.Apply(clone, null));
        }

        [Test]
        public void NonDestructive_MissingDataWarnsAndRemovesTheComponent()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            a.Component.Data = null;
            GameObject clone = Clone(a);

            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(clone, null);

            Assert.That(result.Applied, Is.EqualTo(0));
            Assert.That(result.Warnings.Count, Is.EqualTo(1));
            Assert.That(clone.GetComponentInChildren<GP02ShapeKeys>(true), Is.Null);
        }

        [Test]
        public void NonDestructive_UsesTheSourceMeshWhenTheRendererHoldsTheExportedOrPreviewMesh()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            // 書き出したメッシュが割り当たったままでも、元のメッシュから作る（BlendShape が二重にならない）
            Mesh exported = ShapeKeyLibrary.BuildMesh(a.Source, a.Data, true);
            Track(exported);
            a.Component.ExportedMesh = exported;
            a.Renderer.sharedMesh = exported;
            GameObject clone = Clone(a);

            NonDestructiveApplier.Apply(clone, null);

            Mesh result = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
            Assert.That(result.blendShapeCount, Is.EqualTo(3));

            // プレビューの一時メッシュ（保存されない）も使わない
            Mesh temp = Track(Object.Instantiate(a.Source));
            temp.hideFlags = HideFlags.DontSave;
            a.Component.ExportedMesh = null;
            a.Renderer.sharedMesh = temp;
            GameObject clone2 = Clone(a);
            NonDestructiveApplier.Apply(clone2, null);
            Assert.That(clone2.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.blendShapeCount, Is.EqualTo(3));
        }

        [Test]
        public void NonDestructive_AppliesToEveryComponentUnderTheAvatar()
        {
            Avatar a = MakeAvatar(ShapeKeyApplyMode.NonDestructive);
            Mesh other = CreateGridMesh("Top");
            var child = new GameObject("Top");
            child.transform.SetParent(a.Root.transform, false);
            child.SetActive(false);
            child.AddComponent<SkinnedMeshRenderer>().sharedMesh = other;
            var component = child.AddComponent<GP02ShapeKeys>();
            component.Data = NewData(other, Entry("Tight", Ramp(other.vertexCount, 0.002f)));
            component.SourceMesh = other;
            component.ApplyMode = ShapeKeyApplyMode.NonDestructive;
            GameObject clone = Clone(a);

            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(clone, null);

            Assert.That(result.Applied, Is.EqualTo(2), "非アクティブのものも処理する");
            Assert.That(clone.GetComponentsInChildren<GP02ShapeKeys>(true).Length, Is.EqualTo(0));
        }

        [Test]
        public void ToolAssemblies_DoNotReferenceNdmfOrVrchat()
        {
            foreach (Type type in new[] { typeof(NonDestructiveApplier), typeof(GP02ShapeKeys) })
            {
                foreach (var reference in type.Assembly.GetReferencedAssemblies())
                {
                    string name = reference.Name;
                    Assert.That(name.StartsWith("nadena", StringComparison.OrdinalIgnoreCase) || name.StartsWith("VRC", StringComparison.OrdinalIgnoreCase),
                        Is.False, $"{type.Assembly.GetName().Name} が {name} を参照している");
                }
            }
        }
    }
}
