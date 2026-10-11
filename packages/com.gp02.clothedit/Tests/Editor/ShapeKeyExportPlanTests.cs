using System;
using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 書き出しの前の確認の一覧（<see cref="ShapeKeyExportPlan"/>）、名前の前置き・後置き、頂点数違いを飛ばす設定、素体への焼き込みの印、
    /// ビルド時の適用、トグル用の AnimationClip、重ね着のまとめて書き出しを確かめる。
    /// </summary>
    public class ShapeKeyExportPlanTests : ShapeKeyUiTestBase
    {
        const string AnimFolder = TempRoot + "/Animations";

        Mesh SourceWithExisting(string name = "Skirt")
        {
            Mesh mesh = CreateGridMesh(name);
            mesh.AddBlendShapeFrame("Existing", 100f, Ramp(mesh.vertexCount, 0.001f), null, null);
            return mesh;
        }

        static List<ExportAction> Actions(List<ExportPlanItem> plan)
        {
            var result = new List<ExportAction>();
            foreach (ExportPlanItem item in plan) result.Add(item.Action);
            return result;
        }

        static string[] BlendShapeNames(Mesh mesh)
        {
            var names = new string[mesh.blendShapeCount];
            for (int i = 0; i < names.Length; i++) names[i] = mesh.GetBlendShapeName(i);
            return names;
        }

        // ---- 確認の一覧 ----

        [Test]
        public void Plan_ListsAddOverwriteSkipAndPrebake_AndTheExportDoesExactlyThat()
        {
            Mesh source = SourceWithExisting();
            int n = source.vertexCount;
            var off = Entry("Off", Ramp(n, 0.01f));
            off.Enabled = false;
            var baked = Entry("Baked", Ramp(n, 0.002f));
            baked.Prebake = true;
            GP02ShapeKeyData data = NewData(source,
                Entry("New", Ramp(n, 0.003f)), Entry("Existing", Ramp(n, 0.004f)), Entry("New", Ramp(n, 0.005f)),
                Entry("Wrong", new Vector3[3]), off, baked);
            var options = new ShapeKeyBuildOptions { OverwriteSameName = true, SkipIncompatible = true };

            List<ExportPlanItem> plan = ShapeKeyExportPlan.Create(source, data, options);

            Assert.That(Actions(plan), Is.EqualTo(new[]
            {
                ExportAction.Superseded, ExportAction.Overwrite, ExportAction.Overwrite, ExportAction.SkipIncompatible, ExportAction.Prebake,
            }), "無効なものは一覧に出ない。同じ名前の前のものは置き換えられる");
            Assert.That(plan[1].SourceIndex, Is.EqualTo(0), "元のメッシュの BlendShape を置き換える");
            Assert.That(plan[2].SourceIndex, Is.EqualTo(-1), "前のシェイプキーを置き換える");
            Assert.That(ShapeKeyExportPlan.Summarize(plan), Does.Contain("2"));

            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, options, out List<ExportPlanItem> used));
            Assert.That(BlendShapeNames(built), Is.EqualTo(new[] { "Existing", "New" }));
            AssertSame(Ramp(n, 0.004f), FrameVertices(built, built.GetBlendShapeIndex("Existing"), 0), 1e-6f, "元の Existing を置き換えた");
            AssertSame(Ramp(n, 0.005f), FrameVertices(built, built.GetBlendShapeIndex("New"), 0), 1e-6f, "同じ名前は後のものが残る");
            Assert.That(Actions(used), Is.EqualTo(Actions(plan)), "書き出しは計画のとおり");
        }

        [Test]
        public void Plan_WithoutOverwrite_RenamesSameNamesWithNumbers()
        {
            Mesh source = SourceWithExisting();
            int n = source.vertexCount;
            GP02ShapeKeyData data = NewData(source, Entry("New", Ramp(n, 0.003f)), Entry("Existing", Ramp(n, 0.004f)), Entry("New", Ramp(n, 0.005f)));
            var options = new ShapeKeyBuildOptions { OverwriteSameName = false };

            List<ExportPlanItem> plan = ShapeKeyExportPlan.Create(source, data, options);

            Assert.That(Actions(plan), Is.EqualTo(new[] { ExportAction.Add, ExportAction.Rename, ExportAction.Rename }));
            Assert.That(plan[1].BlendShapeName, Is.EqualTo("Existing (1)"));
            Assert.That(plan[2].BlendShapeName, Is.EqualTo("New (1)"));
            Mesh built = Track(ShapeKeyLibrary.BuildMesh(source, data, options, out _));
            Assert.That(BlendShapeNames(built), Is.EqualTo(new[] { "Existing", "New", "Existing (1)", "New (1)" }));
        }

        [Test]
        public void Plan_MarksUnexportableEntriesAsErrors_AndTheExportFailsUnlessIncompatibleOnesAreSkipped()
        {
            Mesh source = CreateGridMesh();
            int n = source.vertexCount;
            GP02ShapeKeyData data = NewData(source, Entry("Good", Ramp(n, 0.003f)), Entry("Wrong", new Vector3[3]));

            List<ExportPlanItem> strict = ShapeKeyExportPlan.Create(source, data, new ShapeKeyBuildOptions());
            Assert.That(strict[1].Action, Is.EqualTo(ExportAction.Invalid));
            Assert.That(ShapeKeyExportPlan.FirstProblem(strict), Is.Not.Null);
            Assert.Throws<ArgumentException>(() => ShapeKeyLibrary.BuildMesh(source, data, new ShapeKeyBuildOptions(), out _));

            Mesh skipped = Track(ShapeKeyLibrary.BuildMesh(source, data, new ShapeKeyBuildOptions { SkipIncompatible = true }, out _));
            Assert.That(BlendShapeNames(skipped), Is.EqualTo(new[] { "Good" }));

            data.Entries.Add(Entry("", Ramp(n, 0.001f)));
            Assert.That(ShapeKeyExportPlan.FirstProblem(ShapeKeyExportPlan.Create(source, data, new ShapeKeyBuildOptions { SkipIncompatible = true })), Is.Not.Null, "名前が空は飛ばせない");

            data.VertexCount = n + 1;
            List<ExportPlanItem> whole = ShapeKeyExportPlan.Create(source, data, new ShapeKeyBuildOptions { SkipIncompatible = true });
            Assert.That(whole.TrueForAll(i => i.Action == ExportAction.Invalid), Is.True, "データ全体が合わないときは、飛ばして済ませない");
        }

        // ---- 名前の前置き・後置き ----

        [Test]
        public void NamePrefixAndSuffix_AreAppliedToTheExportedBlendShapesOnly()
        {
            Mesh source = SourceWithExisting();
            int n = source.vertexCount;
            GP02ShapeKeyData data = NewData(source, Entry("Lift", Ramp(n, 0.003f)), Entry("Existing", Ramp(n, 0.004f)));
            Renderer renderer = CreateRenderer(source, true);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            component.NamePrefix = "GP02_";
            component.NameSuffix = "_v2";

            Mesh result = ShapeKeyExporter.ExportToMesh(component, ShapeKeyBuildOptions.FromComponent(component, true), MeshFolder);

            Assert.That(BlendShapeNames(result), Is.EqualTo(new[] { "Existing", "GP02_Lift_v2", "GP02_Existing_v2" }), "前置きがあるので、元の Existing は置き換えず加える");
            Assert.That(data.Entries[0].Name, Is.EqualTo("Lift"), "データの名前は変わらない");
            List<ExportPlanItem> plan = ShapeKeyExporter.GetPlan(component, ShapeKeyBuildOptions.FromComponent(component, true));
            Assert.That(plan[0].BlendShapeName, Is.EqualTo("GP02_Lift_v2"));
        }

        // ---- 素体への焼き込みの印 ----

        [Test]
        public void Prebake_BakesIntoTheBaseAtItsWeight_AndRemovingTheMarkRestoresTheBase()
        {
            Mesh source = SourceWithExisting();
            int n = source.vertexCount;
            Vector3[] rest = source.vertices;
            Vector3[] bakeDeltas = Ramp(n, 0.01f);
            ShapeKeyEntry bake = Entry("Wide", bakeDeltas);
            bake.Prebake = true;
            bake.PrebakeWeight = 50f;
            ShapeKeyEntry lift = Entry("Lift", Ramp(n, 0.003f));
            GP02ShapeKeyData data = NewData(source, bake, lift);

            Mesh baked = Track(ShapeKeyLibrary.BuildMesh(source, data, true));

            Vector3[] vertices = baked.vertices;
            for (int v = 0; v < n; v++) Assert.That(Vector3.Distance(vertices[v], rest[v] + bakeDeltas[v] * 0.5f), Is.LessThan(1e-6f), $"頂点 {v}");
            Assert.That(BlendShapeNames(baked), Is.EqualTo(new[] { "Existing", "Lift" }), "焼き込んだものは BlendShape にならない");
            Assert.That(source.vertices[0], Is.EqualTo(rest[0]), "元のメッシュは変わらない");

            bake.Prebake = false;
            Mesh restored = Track(ShapeKeyLibrary.BuildMesh(source, data, true));
            vertices = restored.vertices;
            for (int v = 0; v < n; v++) Assert.That(Vector3.Distance(vertices[v], rest[v]), Is.LessThan(1e-6f), $"印を外すと素体は元に戻る 頂点 {v}");
            Assert.That(BlendShapeNames(restored), Is.EqualTo(new[] { "Existing", "Wide", "Lift" }));
        }

        [Test]
        public void Prebake_SetPrebakeAndWeightAreUndoable_AndClonesKeepTheMark()
        {
            Mesh source = CreateGridMesh();
            ShapeKeyEntry a = Entry("A", Ramp(source.vertexCount, 0.002f));
            GP02ShapeKeyData data = NewData(source, a);

            NextUndoGroup();
            ShapeKeyListOperations.SetPrebake(data, new[] { a }, true);
            NextUndoGroup();
            ShapeKeyListOperations.SetPrebakeWeight(data, a, 30f);
            NextUndoGroup();
            ShapeKeyListOperations.SetPrebakeWeight(data, a, 140f);
            NextUndoGroup();
            Assert.That(a.Prebake, Is.True);
            Assert.That(a.PrebakeWeight, Is.EqualTo(100f), "0〜100 に収める");

            ShapeKeyEntry copy = ShapeKeyListOperations.Duplicate(data, new[] { a })[0];
            NextUndoGroup();
            Assert.That(copy.Prebake, Is.True, "複製は印も引き継ぐ");
            Assert.That(copy.PrebakeWeight, Is.EqualTo(100f));
            Assert.That(copy.CreatedTicks, Is.GreaterThan(a.CreatedTicks - 1));

            Undo.PerformUndo();
            Assert.That(data.Entries.Count, Is.EqualTo(1));
            Undo.PerformUndo();
            Assert.That(data.Entries[0].PrebakeWeight, Is.EqualTo(30f));
            Undo.PerformUndo();
            Assert.That(data.Entries[0].PrebakeWeight, Is.EqualTo(100f));
            Assert.That(data.Entries[0].Prebake, Is.True);
            Undo.PerformUndo();
            Assert.That(data.Entries[0].Prebake, Is.False);
        }

        [Test]
        public void Prebake_AlsoAppliesInTheBuildTimeApplier_AndInitialWeightsUseTheFinalNames()
        {
            Mesh source = CreateGridMesh("Skirt");
            int n = source.vertexCount;
            Vector3[] rest = source.vertices;
            ShapeKeyEntry bake = Entry("Wide", Ramp(n, 0.01f));
            bake.Prebake = true;
            ShapeKeyEntry lift = Entry("Lift", Ramp(n, 0.003f));
            lift.PreviewWeight = 40f;
            GP02ShapeKeyData data = NewData(source, bake, lift);
            GameObject root = Track(new GameObject("Avatar"));
            Renderer renderer = CreateRenderer(source, true, "Body");
            renderer.transform.SetParent(root.transform, false);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            component.ApplyMode = ShapeKeyApplyMode.NonDestructive;
            component.InitialWeight = ShapeKeyInitialWeight.PreviewWeight;
            component.NamePrefix = "GP02_";

            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(root, mesh => Track(mesh));

            Assert.That(result.Applied, Is.EqualTo(1));
            var skinned = (SkinnedMeshRenderer)renderer;
            Mesh built = skinned.sharedMesh;
            Assert.That(BlendShapeNames(built), Is.EqualTo(new[] { "GP02_Lift" }));
            Vector3[] vertices = built.vertices;
            Vector3[] wide = Ramp(n, 0.01f);
            for (int v = 0; v < n; v++) Assert.That(Vector3.Distance(vertices[v], rest[v] + wide[v]), Is.LessThan(1e-6f), $"焼き込み 頂点 {v}");
            Assert.That(skinned.GetBlendShapeWeight(0), Is.EqualTo(40f), "最初の重みは最終的な名前の BlendShape に入る");
        }

        [Test]
        public void BuildTimeApplier_SkipsIncompatibleEntriesWithAWarningOnlyWhenTheComponentSaysSo()
        {
            Mesh source = CreateGridMesh("Skirt");
            int n = source.vertexCount;
            GP02ShapeKeyData data = NewData(source, Entry("Good", Ramp(n, 0.003f)), Entry("Wrong", new Vector3[3]));
            GameObject root = Track(new GameObject("Avatar"));
            Renderer renderer = CreateRenderer(source, true, "Body");
            renderer.transform.SetParent(root.transform, false);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            component.ApplyMode = ShapeKeyApplyMode.NonDestructive;

            Assert.Throws<ArgumentException>(() => NonDestructiveApplier.Apply(root, mesh => Track(mesh)), "既定では見落とさないよう失敗する");

            component.SkipIncompatible = true;
            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(root, mesh => Track(mesh));
            Assert.That(result.Warnings.Count, Is.EqualTo(1));
            Assert.That(BlendShapeNames(((SkinnedMeshRenderer)renderer).sharedMesh), Is.EqualTo(new[] { "Good" }));
        }

        // ---- トグル用の AnimationClip ----

        static EditorCurveBinding[] Bindings(AnimationClip clip) => AnimationUtility.GetCurveBindings(clip);

        [Test]
        public void ToggleClips_AreCreatedPerBlendShapeWithWeight0And100AndThePathFromTheAvatarRoot()
        {
            Mesh source = CreateGridMesh("Skirt");
            int n = source.vertexCount;
            ShapeKeyEntry bake = Entry("Wide", Ramp(n, 0.01f));
            bake.Prebake = true;
            ShapeKeyEntry disabled = Entry("Off", Ramp(n, 0.01f));
            disabled.Enabled = false;
            GP02ShapeKeyData data = NewData(source, Entry("Lift", Ramp(n, 0.003f)), bake, disabled, Entry("Open", Ramp(n, 0.004f)));
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.AddComponent<Animator>();
            var group = Track(new GameObject("Clothes"));
            group.transform.SetParent(avatar.transform, false);
            Renderer renderer = CreateRenderer(source, true, "Skirt");
            renderer.transform.SetParent(group.transform, false);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            component.NamePrefix = "GP02_";
            ShapeKeyBuildOptions options = ShapeKeyBuildOptions.FromComponent(component, true);

            List<ShapeKeyAnimationExporter.ToggleClips> clips = ShapeKeyAnimationExporter.CreateToggleClips(component, options, AnimFolder);

            Assert.That(clips.Count, Is.EqualTo(2), "BlendShape になるものだけ（焼き込み・無効は除く）");
            Assert.That(clips[0].BlendShapeName, Is.EqualTo("GP02_Lift"));
            Assert.That(AssetDatabase.GetAssetPath(clips[0].On), Is.EqualTo(AnimFolder + "/Skirt_GP02_Lift_On.anim"));
            Assert.That(AssetDatabase.GetAssetPath(clips[0].Off), Is.EqualTo(AnimFolder + "/Skirt_GP02_Lift_Off.anim"));
            foreach (var pair in new[] { (clips[0].Off, 0f), (clips[0].On, 100f), (clips[1].Off, 0f), (clips[1].On, 100f) })
            {
                EditorCurveBinding[] bindings = Bindings(pair.Item1);
                Assert.That(bindings.Length, Is.EqualTo(1));
                Assert.That(bindings[0].path, Is.EqualTo("Clothes/Skirt"), "アバターのルート（Animator のある親）からのパス");
                Assert.That(bindings[0].type, Is.EqualTo(typeof(SkinnedMeshRenderer)));
                Assert.That(bindings[0].propertyName, Does.StartWith("blendShape.GP02_"));
                AnimationCurve curve = AnimationUtility.GetEditorCurve(pair.Item1, bindings[0]);
                Assert.That(curve.Evaluate(0f), Is.EqualTo(pair.Item2).Within(1e-4f));
                Assert.That(curve.Evaluate(1f / 60f), Is.EqualTo(pair.Item2).Within(1e-4f), "1 フレーム分は同じ値を保つ");
            }

            // もう一度作っても、同じクリップのアセットの中身が置き換わるだけ（アニメーターの参照は保たれる）
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(clips[0].On));
            List<ShapeKeyAnimationExporter.ToggleClips> again = ShapeKeyAnimationExporter.CreateToggleClips(component, options, AnimFolder);
            Assert.That(again[0].On, Is.SameAs(clips[0].On));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(again[0].On)), Is.EqualTo(guid));
            Assert.That(AssetDatabase.FindAssets("t:AnimationClip", new[] { AnimFolder }).Length, Is.EqualTo(4));
            Assert.That(Bindings(again[0].On).Length, Is.EqualTo(1));
        }

        [Test]
        public void ToggleClips_UseTheRootOfTheHierarchyWhenThereIsNoAnimator_AndRejectMeshRenderers()
        {
            Mesh source = CreateGridMesh("Skirt");
            GP02ShapeKeyData data = NewData(source, Entry("Lift", Ramp(source.vertexCount, 0.003f)));
            GameObject top = Track(new GameObject("Top"));
            Renderer skinned = CreateRenderer(source, true, "Body");
            skinned.transform.SetParent(top.transform, false);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(skinned, data);

            var clips = ShapeKeyAnimationExporter.CreateToggleClips(component, new ShapeKeyBuildOptions(), AnimFolder);
            Assert.That(Bindings(clips[0].On)[0].path, Is.EqualTo("Body"));

            Renderer meshRenderer = CreateRenderer(source, false, "Static");
            GP02ShapeKeys other = ShapeKeyLibrary.Attach(meshRenderer, NewData(source, Entry("Lift", Ramp(source.vertexCount, 0.003f))));
            Assert.Throws<InvalidOperationException>(() => ShapeKeyAnimationExporter.CreateToggleClips(other, new ShapeKeyBuildOptions(), AnimFolder));
        }

        // ---- 重ね着のまとめて書き出し ----

        [Test]
        public void ExportAll_ExportsEveryComponentUnderTheAvatar_AndReportsTheOnesThatFail()
        {
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.AddComponent<Animator>();
            var components = new List<GP02ShapeKeys>();
            foreach (string name in new[] { "Dress", "Coat", "Broken" })
            {
                Mesh mesh = CreateGridMesh(name);
                int n = mesh.vertexCount;
                ShapeKeyEntry entry = Entry("Lift", Ramp(n, 0.003f));
                GP02ShapeKeyData data = NewData(mesh, name == "Broken" ? new[] { entry, Entry("Wrong", new Vector3[3]) } : new[] { entry });
                Renderer renderer = CreateRenderer(mesh, true, name);
                renderer.transform.SetParent(avatar.transform, false);
                components.Add(ShapeKeyLibrary.Attach(renderer, data));
            }
            components[0].NamePrefix = "Dress_";
            Assert.That(ShapeKeyExporter.FindComponents(avatar.transform).Count, Is.EqualTo(3));
            Assert.That(ShapeKeyExporter.FindAvatarRoot(components[2].transform), Is.SameAs(avatar.transform));

            List<ShapeKeyExporter.BatchItem> results = ShapeKeyExporter.ExportAll(components, true, MeshFolder, true, AnimFolder);

            Assert.That(results.Count, Is.EqualTo(3));
            Assert.That(results[0].Error, Is.Null);
            Assert.That(results[0].Mesh.GetBlendShapeName(0), Is.EqualTo("Dress_Lift"));
            Assert.That(results[0].Clips.Count, Is.EqualTo(1));
            Assert.That(results[1].Error, Is.Null);
            Assert.That(ClothEditController.GetMesh(components[1].GetComponent<Renderer>()), Is.SameAs(results[1].Mesh));
            Assert.That(results[2].Mesh, Is.Null);
            Assert.That(results[2].Error, Does.Contain("Broken"), "失敗したものは理由つきで知らせ、残りは続ける");
            Assert.That(Bindings(results[1].Clips[0].On)[0].path, Is.EqualTo("Coat"));
        }
    }
}
