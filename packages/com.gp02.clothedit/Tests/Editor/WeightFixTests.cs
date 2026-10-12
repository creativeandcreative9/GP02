using System.Collections.Generic;
using System.Diagnostics;
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
    /// 動作チェックの修正の第2段を、Humanoid のテスト用アバター（体・スカート・タイツ・袖）で確かめる。
    /// ウェイトを体に寄せると、タイツのひざ・袖のひじ・スカートの座ったときの貫通が減ること（同じアーマチュアと、服だけの別のアーマチュア）、
    /// 体どうしの重なりは貫通として数えず分けること、適用は Undo の1段で戻り、シェイプキーのデータに記録され、書き出しとビルド時の適用にも入り、
    /// データから外すと戻ること、手持ちの AnimationClip（Humanoid・Generic）のコマもポーズとして試すこと。
    /// </summary>
    public class WeightFixTests
    {
        const string TempFolder = "Assets/GP02_WeightFixTestTemp";

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

        GameObject CreateAvatar(bool separateArmature = false)
        {
            GameObject root = TestAvatarMenu.CreateInScene(null, separateArmature);
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

        // これらのテストは、ウェイトを寄せる仕組みを、大きく動くポーズ（座る・しゃがむなど）も直す範囲に入れて確かめる
        // （既定のよく使うポーズだけの範囲は PoseCheckScopeTests で確かめる）
        ClothEditController Start(Renderer target, bool recordToData = false)
        {
            var c = new ClothEditController(new ClothEditSettings { FixLargePoses = true })
            {
                BrushRadius = 0.05f, InfluenceRadius = 0.3f, RecordToData = recordToData, SaveFolder = TempFolder, DataFolder = TempFolder,
            };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(new[] { target }, out string error), Is.True, error);
            return c;
        }

        static PoseCheckEntry EntryOf(PoseCheckResult result, string poseId, float stage = 1f)
        {
            foreach (PoseCheckEntry entry in result.Entries)
            {
                if (entry.Pose != null && entry.Pose.Id == poseId && Mathf.Approximately(entry.Stage, stage)) return entry;
            }
            Assert.Fail($"結果に {poseId}（{stage}）がありません");
            return null;
        }

        // ポーズ1つの深さの合計（1つの深さは、外へ逃がす量の上限で頭打ち。深い所1つで決まらないように）
        static float TotalDepth(PosePenetration result)
        {
            float total = 0f;
            foreach (float d in result.Depths) total += Mathf.Min(d, PoseEscapeOptions.DefaultMaxPush);
            return total;
        }

        // ポーズごとの、寄せる前と後
        static void LogByPose(string label, PoseCheckResult before, PoseCheckResult after)
        {
            for (int k = 0; k < before.Entries.Count && k < after.Entries.Count; k++)
            {
                PosePenetration was = before.Entries[k].Result, now = after.Entries[k].Result;
                if (!was.IsPenetrating && !now.IsPenetrating && !was.HasOverlap && !now.HasOverlap) continue;
                Debug.Log($"[GP02] {label} {before.Entries[k].Name}: か所 {was.RegionCount} → {now.RegionCount}、最大 {was.MaxDepth * 1000f:F1} → {now.MaxDepth * 1000f:F1} mm、" +
                    $"深さの合計 {TotalDepth(was) * 1000f:F0} → {TotalDepth(now) * 1000f:F0} mm、体どうしの重なり {was.OverlapRegionCount} → {now.OverlapRegionCount}、" +
                    $"体から離れた所の伸び {was.HighStretch * 100f:F0}% → {now.HighStretch * 100f:F0}%");
            }
        }

        // 2つのウェイトが同じか（頂点ごとに、ボーンの名前ごとの重みで比べる。ボーンの番号の並びが違ってもよい。
        // 記録した変更を当て直すときに合計 1 に割り直すので、float の丸めの分だけ違ってよい）
        static void AssertSameSkin(BoneWeightSets expected, Transform[] expectedBones, BoneWeightSets actual, Transform[] actualBones, string what = "")
        {
            Assert.That(actual.VertexCount, Is.EqualTo(expected.VertexCount), what);
            var map = new Dictionary<string, float>();
            for (int v = 0; v < expected.VertexCount; v++)
            {
                map.Clear();
                for (int k = expected.Offsets[v]; k < expected.Offsets[v + 1]; k++)
                {
                    string name = expectedBones[expected.Bones[k]].name;
                    map[name] = (map.TryGetValue(name, out float w) ? w : 0f) + expected.Weights[k];
                }
                for (int k = actual.Offsets[v]; k < actual.Offsets[v + 1]; k++)
                {
                    string name = actualBones[actual.Bones[k]].name;
                    map[name] = (map.TryGetValue(name, out float w) ? w : 0f) - actual.Weights[k];
                }
                foreach (KeyValuePair<string, float> pair in map)
                {
                    Assert.That(Mathf.Abs(pair.Value), Is.LessThan(1e-4f), $"{what} 頂点 {v} のボーン {pair.Key}");
                }
            }
        }

        // ---- ウェイトを体に寄せると、貫通が減る ----

        [TestCase(false)]
        [TestCase(true)]
        public void FittingWeights_ReducesTheKneeAndElbowPenetrationOfTightClothes(bool separateArmature)
        {
            GameObject root = CreateAvatar(separateArmature);
            string label = separateArmature ? "（服だけの別のアーマチュア）" : "";

            // タイツ: ひざを曲げる（しゃがむ・座る）
            SkinnedMeshRenderer tights = Find(root, TestAvatarMenu.TightsName);
            Mesh mesh = tights.sharedMesh;
            BoneWeightSets original = ShapeKeyWeights.Read(mesh);
            Transform[] originalBones = tights.bones;
            ClothEditController c = Start(tights);
            PoseCheckResult before = c.RunPoseCheck();
            Assert.That(before, Is.Not.Null, c.LastPoseCheckError);
            Assert.That(c.WeightCausedRegionCount, Is.GreaterThan(0), "ひざの貫通が、ウェイトが原因と分からない");
            Assert.That(c.CanPreviewWeightFix, Is.True);
            var timer = Stopwatch.StartNew();
            WeightFixPreview preview = c.PreviewWeightFix();
            Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
            Debug.Log($"[GP02] タイツ{label}のウェイトを体に寄せる（確認、{timer.ElapsedMilliseconds} ms）: {preview}");
            LogByPose("タイツ" + label, before, preview.After);
            Assert.That(preview.ChangedVertices, Is.GreaterThan(0));
            Assert.That(preview.Unmapped, Is.EqualTo(0), "体のボーンが服のボーンに対応しない");
            foreach (string id in new[] { "squat", "sit" })
            {
                PosePenetration was = EntryOf(before, id).Result, now = EntryOf(preview.After, id).Result;
                Assert.That(was.IsPenetrating, Is.True, id + ": " + was);
                Assert.That(TotalDepth(now), Is.LessThan(TotalDepth(was) * 0.3f), $"{id}: 深さの合計が減らない（{was} → {now}）");
                Assert.That(now.MaxDepth, Is.LessThan(was.MaxDepth * 0.5f), $"{id}: 一番深い所が浅くならない（{was} → {now}）");
            }
            Assert.That(preview.RegionsAfter, Is.LessThan(preview.RegionsBefore));
            Assert.That(preview.WeightRegionsAfter, Is.LessThan(preview.WeightRegionsBefore / 2));
            Assert.That(preview.Improved, Is.True);
            Assert.That(preview.StretchesTooMuch, Is.False, "体にぴったりのタイツで、布が伸びると知らせた");
            Assert.That(preview.OverlapRegions, Is.EqualTo(before.OverlapRegionCount));

            // 確認だけでは、シーンもメッシュも変わらない（今の結果も前のまま）
            Assert.That(tights.sharedMesh, Is.SameAs(mesh));
            AssertSameSkin(original, originalBones, ShapeKeyWeights.Read(mesh), tights.bones, "確認のあと");
            Assert.That(tights.bones, Is.EqualTo(originalBones));
            Assert.That(c.LastPoseCheck, Is.SameAs(before));
            Assert.That(c.CanApplyWeightFix, Is.True);
            c.End();

            // 袖: ひじを曲げる
            ClothEditController sleeves = Start(Find(root, TestAvatarMenu.SleevesName));
            PoseCheckResult armsBefore = sleeves.RunPoseCheck();
            Assert.That(armsBefore, Is.Not.Null, sleeves.LastPoseCheckError);
            Assert.That(sleeves.WeightCausedRegionCount, Is.GreaterThan(0), "ひじの貫通が、ウェイトが原因と分からない");
            WeightFixPreview arms = sleeves.PreviewWeightFix();
            Assert.That(arms, Is.Not.Null, sleeves.LastWeightFixError);
            Debug.Log($"[GP02] 袖{label}のウェイトを体に寄せる（確認）: {arms}");
            LogByPose("袖" + label, armsBefore, arms.After);
            PosePenetration elbowWas = EntryOf(armsBefore, "arms_bent").Result, elbowNow = EntryOf(arms.After, "arms_bent").Result;
            Assert.That(elbowWas.IsPenetrating, Is.True, elbowWas.ToString());
            Assert.That(TotalDepth(elbowNow), Is.LessThan(TotalDepth(elbowWas) * 0.5f), $"ひじ: 深さの合計が減らない（{elbowWas} → {elbowNow}）");
            Assert.That(elbowNow.MaxDepth, Is.LessThan(elbowWas.MaxDepth), $"ひじ: 一番深い所が浅くならない（{elbowWas} → {elbowNow}）");
            Assert.That(arms.WeightRegionsAfter, Is.LessThan(arms.WeightRegionsBefore));
            Assert.That(arms.StretchesTooMuch, Is.False);
        }

        [Test]
        public void FittingWeights_ReducesTheSkirtSittingPenetration_AndLowerStrengthStretchesLess()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.SkirtName));
            PoseCheckResult before = c.RunPoseCheck();
            Assert.That(before, Is.Not.Null, c.LastPoseCheckError);
            Assert.That(c.Settings.WeightFixStrength, Is.EqualTo(1f), "既定の強さは 100%");
            WeightFixPreview full = c.PreviewWeightFix();
            Assert.That(full, Is.Not.Null, c.LastWeightFixError);
            Debug.Log($"[GP02] スカートのウェイトを体に寄せる（強さ 100%）: {full}");
            LogByPose("スカート（100%）", before, full.After);

            // 座ると太ももがスカートを突き抜ける所が減る（裾は太ももから 8cm ほど離れているので、裾の一番深い所は残す。脚に貼り付けないため）
            PosePenetration was = EntryOf(before, "sit").Result, now = EntryOf(full.After, "sit").Result;
            Assert.That(was.IsPenetrating, Is.True);
            Assert.That(now.RegionCount, Is.LessThan(was.RegionCount), $"座る: か所が減らない（{was} → {now}）");
            Assert.That(TotalDepth(now), Is.LessThan(TotalDepth(was)), $"座る: 深さの合計が減らない（{was} → {now}）");
            Assert.That(full.Effect.TotalDepthAfter, Is.LessThan(full.Effect.TotalDepthBefore));
            Assert.That(full.AddedBoneCount, Is.GreaterThan(0), "スカートのボーンに無い、体のボーン（脚など）を足す");

            // 100% では、体から離れたスカートの布が脚に貼り付いて伸びる（伸びの目安で分かる）。強さを下げると和らぐ
            Assert.That(full.StretchesTooMuch, Is.True, $"伸びの目安: {full.StretchPoseName} {full.StretchBefore} → {full.StretchAfter}");
            StringAssert.Contains(full.StretchPoseName, full.Describe(false));
            c.Settings.WeightFixStrength = 0.5f;
            WeightFixPreview half = c.PreviewWeightFix();
            Assert.That(half, Is.Not.Null, c.LastWeightFixError);
            Debug.Log($"[GP02] スカートのウェイトを体に寄せる（強さ 50%）: {half}");
            LogByPose("スカート（50%）", before, half.After);
            Assert.That(half.StretchIncrease, Is.LessThan(full.StretchIncrease));
            Assert.That(EntryOf(half.After, "sit").Result.RegionCount, Is.LessThan(was.RegionCount), "強さ 50% でも、座ったときの貫通は減る");
            Assert.That(c.WeightFixPreview, Is.SameAs(half), "確認し直すと、前の候補は捨てる");
        }

        // ---- 体どうしの重なり ----

        [Test]
        public void BodyOverlaps_AreReportedSeparately_AndAreNotFixed()
        {
            GameObject root = CreateAvatar();
            ClothEditController tights = Start(Find(root, TestAvatarMenu.TightsName));
            PoseCheckResult result = tights.RunPoseCheck();
            Assert.That(result, Is.Not.Null, tights.LastPoseCheckError);

            // 腕を下ろすと、手が太ももに入る（体どうしの重なり）。タイツの貫通としては数えず、分けて出す
            PoseCheckEntry armsDown = EntryOf(result, "arms_down");
            Debug.Log($"[GP02] タイツの腕を下ろす: {armsDown.Result} / {armsDown.Describe()}");
            Assert.That(armsDown.Result.IsPenetrating, Is.False, armsDown.Result.ToString());
            Assert.That(armsDown.Result.HasOverlap, Is.True, armsDown.Result.ToString());
            StringAssert.Contains(GP02.EditorTools.L10n.Tr("体どうしの重なり", "body overlap"), armsDown.Describe());
            Assert.That(result.OverlapRegionCount, Is.GreaterThan(0));
            Assert.That(result.OverlapPoseCount, Is.GreaterThan(0));
            Assert.That(result.WeightPlan.OverlapRegions, Is.EqualTo(result.OverlapRegionCount));

            // 重なりだけの頂点は、ウェイトを寄せる対象にしない
            var penetrating = new HashSet<int>();
            foreach (PoseCheckEntry entry in result.Entries) penetrating.UnionWith(entry.Result.Vertices);
            int overlapOnly = 0;
            foreach (int v in armsDown.Result.OverlapVertices)
            {
                if (penetrating.Contains(v)) continue;
                overlapOnly++;
                Assert.That(result.WeightPlan.WeldedSeed[v], Is.EqualTo(0f), $"重なりだけの頂点 {v} を対象にした");
            }
            Assert.That(overlapOnly, Is.GreaterThan(0));
            tights.End();

            // 体を横に倒すと、袖の腕が胴に入る
            ClothEditController sleeves = Start(Find(root, TestAvatarMenu.SleevesName));
            PoseCheckResult arms = sleeves.RunPoseCheck();
            Assert.That(arms, Is.Not.Null, sleeves.LastPoseCheckError);
            PoseCheckEntry sideBend = EntryOf(arms, "side_bend");
            Debug.Log($"[GP02] 袖の体を横に倒す: {sideBend.Result}");
            Assert.That(sideBend.Result.IsPenetrating, Is.False, sideBend.Result.ToString());
            Assert.That(sideBend.Result.HasOverlap, Is.True, sideBend.Result.ToString());
        }

        // ---- 適用・Undo・データ・外す ----

        [Test]
        public void Apply_RecordsInTheData_RemovingRestores_AndEachIsOneUndoStep()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer tights = Find(root, TestAvatarMenu.TightsName);
            Mesh original = tights.sharedMesh;
            BoneWeightSets originalWeights = ShapeKeyWeights.Read(original);
            Transform[] originalBones = tights.bones;
            ClothEditController c = Start(tights, recordToData: true);
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            WeightFixPreview preview = c.PreviewWeightFix();
            Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
            WeightFixCandidate candidate = preview.Candidates[0];
            Undo.IncrementCurrentGroup();

            WeightFixReport report = c.ApplyWeightFix();
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Debug.Log($"[GP02] タイツに適用: {report}");
            Mesh working = tights.sharedMesh;
            Assert.That(working, Is.Not.SameAs(original), "元のメッシュは書き換えず、複製を作って差し替える");
            StringAssert.StartsWith(TempFolder + "/", AssetDatabase.GetAssetPath(working));
            AssertSameSkin(candidate.Weights, candidate.Bones, ShapeKeyWeights.Read(working), tights.bones, "適用したメッシュ");
            AssertSameSkin(originalWeights, originalBones, ShapeKeyWeights.Read(original), originalBones, "元のメッシュ");

            // シェイプキーのデータに、変えた頂点だけを記録する（データの元のメッシュは、差し替える前のメッシュ）
            var component = tights.GetComponent<GP02ShapeKeys>();
            Assert.That(component != null && component.Data != null, Is.True, "データを作っていない");
            GP02ShapeKeyData data = component.Data;
            Assert.That(data.SourceMesh, Is.SameAs(original));
            Assert.That(data.HasWeightEdit, Is.True);
            Assert.That(data.WeightEdit.VertexCount, Is.EqualTo(candidate.ChangedCount));
            Assert.That(report.RecordedVertices, Is.EqualTo(candidate.ChangedCount));
            Assert.That(c.RecordedWeightEditVertexCount, Is.EqualTo(candidate.ChangedCount));

            // 編集は新しいメッシュで作り直し、候補で調べ直した結果を、そのまま今の結果にする
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.GetSourceMesh(0), Is.SameAs(working));
            Assert.That(c.LastPoseCheck, Is.SameAs(preview.After));
            Assert.That(c.IsPoseCheckStale, Is.False);
            Assert.That(c.WeightCausedRegionCount, Is.EqualTo(preview.WeightRegionsAfter));
            Assert.That(c.WeightFixPreview, Is.Null);
            Assert.That(c.CanApplyWeightFix, Is.False, "同じ候補を2回は適用しない");

            // データから外すと、メッシュのウェイトが元に戻る
            Undo.IncrementCurrentGroup();
            Assert.That(c.RemoveWeightFix(), Is.True, c.LastWeightFixError);
            Assert.That(data.HasWeightEdit, Is.False);
            Assert.That(c.RecordedWeightEditVertexCount, Is.EqualTo(0));
            AssertSameSkin(originalWeights, originalBones, ShapeKeyWeights.Read(tights.sharedMesh), tights.bones, "外したあと");
            Assert.That(tights.bones.Length, Is.EqualTo(originalBones.Length));
            Assert.That(c.IsEditing, Is.True);

            // Undo の1回で外す前に、もう1回で適用の前（元のメッシュ・ボーン、データの記録なし）に戻る
            Undo.PerformUndo();
            Assert.That(data.HasWeightEdit, Is.True, "外したのが戻らない");
            AssertSameSkin(candidate.Weights, candidate.Bones, ShapeKeyWeights.Read(tights.sharedMesh), tights.bones, "外したのを戻したあと");
            Undo.PerformUndo();
            Assert.That(tights.sharedMesh, Is.SameAs(original), "メッシュが戻らない");
            Assert.That(tights.bones, Is.EqualTo(originalBones));
            var afterUndo = tights.GetComponent<GP02ShapeKeys>();
            Assert.That(afterUndo == null || afterUndo.Data == null || !afterUndo.Data.HasWeightEdit, Is.True, "データの記録が戻らない");
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.GetSourceMesh(0), Is.SameAs(original), "元のメッシュで編集を作り直していない");
            Assert.That(c.RecordedWeightEditVertexCount, Is.EqualTo(0));
        }

        [Test]
        public void ExportAndBuild_IncludeTheAppliedWeightChange_WithTheAddedBones()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, TestAvatarMenu.SkirtName);
            Mesh original = skirt.sharedMesh;
            int originalBoneCount = skirt.bones.Length;
            ClothEditController c = Start(skirt, recordToData: true);
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            WeightFixPreview preview = c.PreviewWeightFix();
            Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
            WeightFixCandidate candidate = preview.Candidates[0];
            Assert.That(candidate.AddedBones.Length, Is.GreaterThan(0), "スカートのボーンに無い、体のボーン（脚など）を足す");
            Assert.That(c.ApplyWeightFix(), Is.Not.Null, c.LastWeightFixError);
            Mesh working = skirt.sharedMesh;
            Transform[] applied = skirt.bones;
            Assert.That(applied.Length, Is.EqualTo(originalBoneCount + candidate.AddedBones.Length), "足すボーンは、レンダラーのボーンの末尾");
            Assert.That(working.bindposes.Length, Is.EqualTo(applied.Length));
            c.End();
            GP02ShapeKeys component = skirt.GetComponent<GP02ShapeKeys>();
            BoneWeightSets workingWeights = ShapeKeyWeights.Read(working);

            // 元のメッシュに戻すと、足したボーンも外れる。メッシュに書き出すと（元のメッシュから作り直す）、適用したのと同じウェイトとボーンになる
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(component), Is.True);
            Assert.That(skirt.sharedMesh, Is.SameAs(original));
            Assert.That(skirt.bones.Length, Is.EqualTo(originalBoneCount));
            Mesh exported = ShapeKeyExporter.ExportToMesh(component, true, TempFolder);
            Assert.That(skirt.sharedMesh, Is.SameAs(exported));
            Assert.That(skirt.bones.Length, Is.EqualTo(applied.Length));
            Assert.That(exported.bindposes.Length, Is.EqualTo(applied.Length));
            AssertSameSkin(workingWeights, applied, ShapeKeyWeights.Read(exported), skirt.bones, "書き出したメッシュ");

            // ビルド時の適用（NDMF の本体）: 元のメッシュのまま、アバターの複製に同じウェイトと、複製のボーンが入る
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(component), Is.True);
            component.ApplyMode = ShapeKeyApplyMode.NonDestructive;
            GameObject clone = Object.Instantiate(root);
            _created.Add(clone);
            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(clone, null);
            Assert.That(result.Warnings, Is.Empty, string.Join("\n", result.Warnings));
            SkinnedMeshRenderer built = Find(clone, TestAvatarMenu.SkirtName);
            _created.Add(built.sharedMesh);
            Assert.That(built.sharedMesh, Is.Not.SameAs(original));
            Assert.That(built.bones.Length, Is.EqualTo(applied.Length));
            foreach (Transform bone in built.bones) Assert.That(bone.IsChildOf(clone.transform), Is.True, bone.name + " が複製のボーンでない");
            AssertSameSkin(workingWeights, applied, ShapeKeyWeights.Read(built.sharedMesh), built.bones, "ビルドの複製");
            Assert.That(skirt.sharedMesh, Is.SameAs(original), "シーンのメッシュは変わらない");
        }

        // ---- 手持ちの AnimationClip ----

        [Test]
        public void AnimationClips_TheirFramesAreTriedAsPoses_HumanoidAndGeneric()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.TightsName));
            foreach (PosePreset pose in PoseLibrary.All) c.Settings.SetPoseEnabled(pose.Id, false);

            // Humanoid: 1 秒で両脚を前に上げ、ひざを深く曲げる（しゃがむような形）
            var humanoid = new AnimationClip { name = "GP02TestSquat", frameRate = 30f };
            _created.Add(humanoid);
            foreach (string side in new[] { "Left", "Right" })
            {
                AnimationUtility.SetEditorCurve(humanoid, EditorCurveBinding.FloatCurve("", typeof(Animator), side + " Upper Leg Front-Back"), AnimationCurve.Linear(0f, 0f, 1f, -1f));
                AnimationUtility.SetEditorCurve(humanoid, EditorCurveBinding.FloatCurve("", typeof(Animator), side + " Lower Leg Stretch"), AnimationCurve.Linear(0f, 1f, 1f, -0.9f));
            }
            // Generic: 0.5 秒で左ひざだけを 100° 曲げる（ボーンのパスがこのアバターに合う）
            const string kneePath = "Armature/Hips/LeftUpperLeg/LeftLowerLeg";
            Transform knee = root.transform.Find(kneePath);
            Quaternion straight = knee.localRotation, bent = straight * Quaternion.Euler(100f, 0f, 0f);
            var generic = new AnimationClip { name = "GP02TestKnee", frameRate = 30f };
            _created.Add(generic);
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++)
            {
                AnimationUtility.SetEditorCurve(generic, EditorCurveBinding.FloatCurve(kneePath, typeof(Transform), "m_LocalRotation." + axes[i]), AnimationCurve.Linear(0f, straight[i], 0.5f, bent[i]));
            }
            // ボーンのパスが合わない Generic のクリップは使わずに、そう知らせる
            var other = new AnimationClip { name = "GP02TestOtherRig", frameRate = 30f };
            _created.Add(other);
            AnimationUtility.SetEditorCurve(other, EditorCurveBinding.FloatCurve("OtherRig/Bone", typeof(Transform), "m_LocalRotation.x"), AnimationCurve.Linear(0f, 0f, 1f, 0.5f));
            c.Settings.PoseCheckClips.AddRange(new[] { humanoid, generic, other });
            c.Settings.PoseCheckClipInterval = 8;
            List<float> humanoidTimes = ClothEditSettings.ClipSampleTimes(humanoid, 8);
            List<float> genericTimes = ClothEditSettings.ClipSampleTimes(generic, 8);
            Assert.That(humanoidTimes.Count, Is.EqualTo(5), "0・8・16・24 コマと最後のコマ");
            Assert.That(genericTimes.Count, Is.EqualTo(3));

            PoseCheckResult result = c.RunPoseCheck();
            Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
            foreach (PoseCheckEntry entry in result.Entries) Debug.Log($"[GP02] クリップのコマ {entry.Name}: {entry.Result}");
            Assert.That(result.ClipSampleCount, Is.EqualTo(humanoidTimes.Count + genericTimes.Count));
            Assert.That(result.Entries.Count, Is.EqualTo(result.ClipSampleCount), "プリセットのポーズは全部外した");
            Assert.That(result.GetPoses(), Is.Empty);
            Assert.That(result.ClipNotes.Count, Is.EqualTo(1), string.Join("\n", result.ClipNotes));
            StringAssert.Contains(other.name, result.ClipNotes[0]);

            // 名前は「クリップ名 1秒」のように出す。深くしゃがんだ最後のコマで、ひざの貫通が見つかる
            PoseCheckEntry first = result.Entries[0];
            PoseCheckEntry last = result.Entries[humanoidTimes.Count - 1];
            Assert.That(last.IsClip, Is.True);
            Assert.That(last.Clip, Is.SameAs(humanoid));
            Assert.That(last.ClipTime, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(last.Name, Is.EqualTo(PoseCheckEntry.ClipSampleName(humanoid, 1f)));
            StringAssert.Contains(humanoid.name, last.Name);
            Assert.That(last.Result.IsPenetrating, Is.True, "しゃがんだコマで、ひざの貫通が見つからない: " + last.Result);
            Assert.That(last.Result.MaxDepth, Is.GreaterThan(first.Result.MaxDepth), "最初のコマ（脚を伸ばしたまま）より深くならない");
            PoseCheckEntry kneeBent = result.Entries[result.Entries.Count - 1];
            Assert.That(kneeBent.Clip, Is.SameAs(generic));
            Assert.That(kneeBent.Result.IsPenetrating, Is.True, "Generic のクリップでひざを曲げたコマで、貫通が見つからない: " + kneeBent.Result);

            // 結果のコマを、編集の画面で見せられる（複製のひざが曲がる）
            int lastIndex = humanoidTimes.Count + genericTimes.Count - 1;
            c.ShowPoseEntry(lastIndex);
            Assert.That(c.IsPosePreviewActive, Is.True);
            Transform cloneKnee = c.PoseCheckRig.GetCloneTransform(knee);
            Assert.That(Quaternion.Angle(cloneKnee.localRotation, bent), Is.LessThan(1f), "Generic のクリップのひざの角度にならない");
            Assert.That(Quaternion.Angle(knee.localRotation, straight), Is.LessThan(1e-3f), "元のアバターが動いた");
            c.EndPosePreview();
        }
    }
}
