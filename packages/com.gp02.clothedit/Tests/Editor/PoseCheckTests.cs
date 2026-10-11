using System.Collections.Generic;
using System.Diagnostics;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 動作チェック（いろいろなポーズで貫通を調べる）を、Humanoid のテスト用アバター（体・スカート・タイツ・袖）で確かめる。
    /// ポーズはアバターの複製に付け、シーンのアバターは1つも動かないこと、複製は必ず消えること、服だけの別のアーマチュアも体と一緒に動くこと、
    /// 座る・足を上げるでスカートに、ひざ・ひじを曲げるとタイツ・袖に貫通が見つかること、外へ逃がすと減って1回の Undo で戻ることなど。
    /// </summary>
    public class PoseCheckTests
    {
        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        double _time = 100.0;

        const string TempFolder = "Assets/GP02_PoseCheckTestTemp";

        [TearDown]
        public void TearDown()
        {
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            PoseRig.DisposeAll();
            foreach (Mesh saved in _savedMeshes)
            {
                if (saved != null) Undo.ClearUndo(saved);
            }
            _savedMeshes.Clear();
            for (int k = _created.Count - 1; k >= 0; k--)
            {
                Object o = _created[k];
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        readonly List<Mesh> _savedMeshes = new List<Mesh>();

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

        ClothEditController Start(params Renderer[] targets)
        {
            var c = new ClothEditController(new ClothEditSettings()) { BrushRadius = 0.05f, InfluenceRadius = 0.3f, RecordToData = false, SaveFolder = TempFolder };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(targets, out string error), Is.True, error);
            return c;
        }

        double NextTime()
        {
            _time += 0.016;
            return _time;
        }

        static PoseCheckEntry Entry(PoseCheckResult result, string poseId, float stage = 1f)
        {
            foreach (PoseCheckEntry entry in result.Entries)
            {
                if (entry.Pose.Id == poseId && Mathf.Approximately(entry.Stage, stage)) return entry;
            }
            Assert.Fail($"結果に {poseId}（{stage}）がありません");
            return null;
        }

        // 階層のすべての Transform の、ローカルの位置・向き・大きさ
        static List<(Vector3, Quaternion, Vector3)> Snapshot(GameObject root)
        {
            var list = new List<(Vector3, Quaternion, Vector3)>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) list.Add((t.localPosition, t.localRotation, t.localScale));
            return list;
        }

        static void AssertUnchanged(List<(Vector3, Quaternion, Vector3)> before, GameObject root)
        {
            List<(Vector3, Quaternion, Vector3)> after = Snapshot(root);
            Assert.That(after.Count, Is.EqualTo(before.Count), "元のアバターの Transform の数が変わった");
            for (int i = 0; i < before.Count; i++)
            {
                Assert.That(after[i].Item1, Is.EqualTo(before[i].Item1), $"Transform {i} の位置が変わった");
                Assert.That(after[i].Item2, Is.EqualTo(before[i].Item2), $"Transform {i} の向きが変わった");
                Assert.That(after[i].Item3, Is.EqualTo(before[i].Item3), $"Transform {i} の大きさが変わった");
            }
        }

        static int LeftoverContainers()
        {
            int count = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go != null && go.name == PoseRig.ContainerName) count++;
            }
            return count;
        }

        [Test]
        public void TestAvatar_IsHumanoid_AndTheClothesUseTheChosenArmature()
        {
            Assert.That(PoseLibrary.FindUnknownMuscles(), Is.Empty, "ポーズの筋肉の名前が Unity に無い");
            Assert.That(PoseLibrary.All.Count, Is.InRange(12, 16));

            foreach (bool separate in new[] { false, true })
            {
                GameObject root = CreateAvatar(separate);
                var animator = root.GetComponent<Animator>();
                Assert.That(animator, Is.Not.Null);
                Assert.That(animator.avatar.isValid && animator.avatar.isHuman, Is.True, "Humanoid の Avatar になっていない");
                Transform hips = root.transform.Find("Armature/Hips");
                Assert.That(animator.GetBoneTransform(HumanBodyBones.Hips), Is.EqualTo(hips));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg), Is.EqualTo(root.transform.Find("Armature/Hips/LeftUpperLeg/LeftLowerLeg")));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.UpperChest), Is.Not.Null);
                Assert.That(PoseRig.GetProblem(Find(root, TestAvatarMenu.SkirtName)), Is.Null);

                // 体は Humanoid のボーンに、服は（別のアーマチュアなら服の下の）同じ名前のボーンにスキニングされている
                SkinnedMeshRenderer body = Find(root, TestAvatarMenu.BodyName);
                Assert.That(body.bones.Length, Is.GreaterThan(15));
                foreach (string name in new[] { TestAvatarMenu.SkirtName, TestAvatarMenu.TightsName, TestAvatarMenu.SleevesName })
                {
                    SkinnedMeshRenderer cloth = Find(root, name);
                    Assert.That(cloth.transform.parent.name, Is.EqualTo(separate ? TestAvatarMenu.OutfitName : root.name));
                    Transform outfit = root.transform.Find(TestAvatarMenu.OutfitName);
                    foreach (Transform bone in cloth.bones)
                    {
                        Assert.That(outfit != null && bone.IsChildOf(outfit), Is.EqualTo(separate), $"{name} のボーン {bone.name}");
                    }
                    // 作ったときの姿勢（T ポーズ）では、スキニングした形はメッシュの形と同じ
                    var baked = new Mesh();
                    _created.Add(baked);
                    cloth.BakeMesh(baked, true);
                    Vector3[] mesh = cloth.sharedMesh.vertices, skinned = baked.vertices;
                    for (int v = 0; v < mesh.Length; v += 37) Assert.That(Vector3.Distance(mesh[v], skinned[v]), Is.LessThan(1e-4f), $"{name} の頂点 {v}");
                }
            }
        }

        [Test]
        public void PoseRig_MovesOnlyTheCopy_HidesAndRestoresTheOriginal_AndIsAlwaysDestroyed()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, TestAvatarMenu.SkirtName);
            List<(Vector3, Quaternion, Vector3)> before = Snapshot(root);
            int containersBefore = LeftoverContainers();

            PoseRig rig = PoseRig.Create(new Renderer[] { skirt }, out string error);
            Assert.That(rig, Is.Not.Null, error);
            Assert.That(PoseRig.LiveCount, Is.EqualTo(1));
            Assert.That(LeftoverContainers(), Is.EqualTo(containersBefore + 1));
            Assert.That(rig.CloneRoot.gameObject.hideFlags & HideFlags.DontSave, Is.EqualTo(HideFlags.DontSave));
            Assert.That(rig.CloneRoot.GetComponentsInChildren<Animator>(true), Is.Empty, "複製から Animator を外していない");
            Assert.That(rig.BodyIsFallback, Is.False, rig.BodyDescription);
            StringAssert.Contains(TestAvatarMenu.BodyName, rig.BodyDescription);

            // 座る（全部）: 複製のひざが前へ出て、足は下を向く。足は床に付いたまま（腰が下がる）。元のアバターは動かない
            Transform originalKnee = root.transform.Find("Armature/Hips/LeftUpperLeg/LeftLowerLeg");
            Transform knee = rig.GetCloneTransform(originalKnee);
            Transform upperLeg = rig.GetCloneTransform(originalKnee.parent);
            Transform foot = rig.GetCloneTransform(originalKnee.Find("LeftFoot"));
            Vector3 kneeBefore = knee.position;
            Vector3 footBefore = foot.position;
            rig.ApplyMuscles(PoseLibrary.Blend(PoseLibrary.Find("sit"), rig.BaseMuscles, 1f));
            Debug.Log($"[GP02] 座る: ひざ {kneeBefore} → {knee.position} / 足首 {footBefore} → {foot.position} / 脚の付け根 {upperLeg.position}");
            Assert.That(knee.position.z - kneeBefore.z, Is.GreaterThan(0.25f), "ひざが前へ出ていない（+Z が前）");
            Assert.That(Mathf.Abs(knee.position.y - upperLeg.position.y), Is.LessThan(0.08f), "太ももが水平になっていない");
            Assert.That(knee.position.y - foot.position.y, Is.GreaterThan(0.25f), "すねが下を向いていない");
            Assert.That(Mathf.Abs(foot.position.y - footBefore.y), Is.LessThan(0.03f), "足が床から浮いた・沈んだ");
            Assert.That(upperLeg.position.y, Is.LessThan(TestAvatarMenu.HipJointY - 0.1f), "座っても腰が下がらない");
            AssertUnchanged(before, root);

            // 見せている間だけ、元のアバターを隠す
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            rig.Show();
            foreach (Renderer r in renderers) Assert.That(r.forceRenderingOff, Is.True, r.name + " が隠れていない");
            rig.Hide();
            foreach (Renderer r in renderers) Assert.That(r.forceRenderingOff, Is.False, r.name + " の表示が戻っていない");

            // 基準に戻すと、複製は元の姿勢（T ポーズ）に戻る
            rig.ApplyBase();
            Assert.That(Vector3.Distance(knee.position, kneeBefore), Is.LessThan(0.005f));

            rig.Show();
            rig.Dispose();
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0));
            Assert.That(LeftoverContainers(), Is.EqualTo(containersBefore), "複製が残っている");
            foreach (Renderer r in renderers) Assert.That(r.forceRenderingOff, Is.False, r.name + " が隠れたまま");
            AssertUnchanged(before, root);

            // 消し損ねた入れ物も、探して消せる
            var leftover = new GameObject(PoseRig.ContainerName) { hideFlags = HideFlags.HideAndDontSave };
            Assert.That(PoseRig.CleanupLeftovers(), Is.EqualTo(1));
            Assert.That(leftover == null, Is.True);
        }

        [Test]
        public void SeparateArmature_ClothBonesFollowTheAvatarBones()
        {
            GameObject root = CreateAvatar(true);
            SkinnedMeshRenderer skirt = Find(root, TestAvatarMenu.SkirtName);
            PoseRig rig = PoseRig.Create(new Renderer[] { skirt }, out string error);
            Assert.That(rig, Is.Not.Null, error);
            Debug.Log($"[GP02] 服の別のアーマチュア: 動きをうつすボーン {rig.FollowerCount}、親に付いていくボーン {rig.UnmatchedBoneCount} / {rig.BodyDescription}");
            Assert.That(rig.FollowerCount, Is.GreaterThanOrEqualTo(15), "スキニングに使うボーンとその親（腰・背骨・胸）");
            Assert.That(rig.UnmatchedBoneCount, Is.EqualTo(0));

            Transform avatarKnee = rig.GetCloneTransform(root.transform.Find("Armature/Hips/LeftUpperLeg/LeftLowerLeg"));
            Transform outfitKnee = rig.GetCloneTransform(root.transform.Find(TestAvatarMenu.OutfitName + "/Armature/Hips/LeftUpperLeg/LeftLowerLeg"));
            Transform avatarHand = rig.GetCloneTransform(root.transform.Find("Armature/Hips/Spine/Chest/UpperChest/RightShoulder/RightUpperArm/RightLowerArm/RightHand"));
            Transform outfitHand = rig.GetCloneTransform(root.transform.Find(TestAvatarMenu.OutfitName + "/Armature/Hips/Spine/Chest/UpperChest/RightShoulder/RightUpperArm/RightLowerArm/RightHand"));
            foreach (string id in new[] { "sit", "arms_up", "twist" })
            {
                rig.ApplyMuscles(PoseLibrary.Blend(PoseLibrary.Find(id), rig.BaseMuscles, 1f));
                Assert.That(Vector3.Distance(avatarKnee.position, outfitKnee.position), Is.LessThan(1e-3f), id + ": 服のひざのボーンが体に付いてこない");
                Assert.That(Vector3.Distance(avatarHand.position, outfitHand.position), Is.LessThan(1e-3f), id + ": 服の手のボーンが体に付いてこない");
                Assert.That(Quaternion.Angle(avatarKnee.rotation, outfitKnee.rotation), Is.LessThan(0.5f));
            }

            // 別のアーマチュアのスカートも、座ると貫通が見つかる（同じボーンのスカートと同じように）
            rig.Dispose();
            ClothEditController c = Start(skirt);
            PoseCheckResult result = c.RunPoseCheck();
            Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
            Assert.That(result.FollowerCount, Is.GreaterThanOrEqualTo(15));
            Assert.That(Entry(result, "sit").Result.IsPenetrating, Is.True, Entry(result, "sit").Result.ToString());
            Assert.That(result.Baseline.IsPenetrating, Is.False, result.Baseline.ToString());
        }

        [Test]
        public void Skirt_SittingAndRaisingALeg_Penetrate_ButTheBasePoseDoesNot()
        {
            GameObject root = CreateAvatar();
            List<(Vector3, Quaternion, Vector3)> before = Snapshot(root);
            ClothEditController c = Start(Find(root, TestAvatarMenu.SkirtName));
            var timer = Stopwatch.StartNew();
            PoseCheckResult result = c.RunPoseCheck();
            timer.Stop();
            Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
            Debug.Log($"[GP02] スカートの動作チェック（{timer.ElapsedMilliseconds} ms、{ClothEditSession.DescribeAcceleration(c.Session.Solver.Kernel)}）: {result.DescribeSummary()} / 基準 {result.Baseline}");
            foreach (PoseCheckEntry entry in result.Entries) Debug.Log($"[GP02]   {entry.Pose.Id} {entry.Stage}: {entry.Result}");
            Assert.That(result.HasBody, Is.True, result.BodyDescription);
            Assert.That(result.Entries.Count, Is.EqualTo(PoseLibrary.All.Count * 2));
            Assert.That(result.Baseline.IsPenetrating, Is.False, "基準の姿勢で貫通が見つかった: " + result.Baseline);
            Assert.That(Entry(result, "sit").Result.IsPenetrating, Is.True);
            Assert.That(Entry(result, "leg_forward_l").Result.IsPenetrating, Is.True);
            Assert.That(Entry(result, "leg_forward_r").Result.IsPenetrating, Is.True);
            Assert.That(Entry(result, "arms_up").Result.IsPenetrating, Is.False, "腕を上げてもスカートは突き抜けない");
            Assert.That(Entry(result, "head_turn").Result.IsPenetrating, Is.False);
            Assert.That(result.PenetratingPoseCount, Is.InRange(2, result.PoseCount - 1));
            Assert.That(c.IsPoseCheckStale, Is.False);

            // 座ったときに突き抜けるのは前側（+Z）の太ももの上
            foreach (System.Numerics.Vector3 p in Entry(result, "sit").Result.Points) Assert.That(p.Z, Is.GreaterThan(-0.05f), $"座ったときの点 {p}");

            // 編集の姿勢での色分け用に、粒子ごとの最悪の深さがある
            float[] depth = c.PoseCheckParticleDepth;
            Assert.That(depth, Is.Not.Null);
            int marked = 0;
            foreach (float d in depth) marked += d > 0f ? 1 : 0;
            Assert.That(marked, Is.GreaterThan(10));
            AssertUnchanged(before, root);
            Assert.That(PoseRig.LiveCount, Is.EqualTo(1), "結果のポーズを見せるための複製が1つだけ残る");
            c.End();
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0), "編集を終えたのに複製が残っている");
            AssertUnchanged(before, root);
        }

        [Test]
        public void TightClothes_BendingKneesAndElbows_PokeThrough()
        {
            GameObject root = CreateAvatar();
            ClothEditController tights = Start(Find(root, TestAvatarMenu.TightsName));
            PoseCheckResult legs = tights.RunPoseCheck();
            Assert.That(legs, Is.Not.Null, tights.LastPoseCheckError);
            Debug.Log($"[GP02] タイツの動作チェック: {legs.DescribeSummary()} / 基準 {legs.Baseline}");
            foreach (PoseCheckEntry entry in legs.Entries) Debug.Log($"[GP02]   {entry.Pose.Id} {entry.Stage}: {entry.Result}");
            Assert.That(legs.Baseline.IsPenetrating, Is.False, legs.Baseline.ToString());
            Assert.That(Entry(legs, "squat").Result.IsPenetrating, Is.True, "しゃがむ（ひざを深く曲げる）で、ひざが突き抜けない");
            Assert.That(Entry(legs, "sit").Result.IsPenetrating, Is.True, "座る（ひざ 90°）で、ひざが突き抜けない");
            Assert.That(Entry(legs, "arms_up").Result.IsPenetrating, Is.False);
            // ひざのあたり
            foreach (System.Numerics.Vector3 p in Entry(legs, "squat").Result.Points)
            {
                Assert.That(p.Y, Is.InRange(0.15f, 0.75f), $"しゃがんだときの点 {p}");
            }
            tights.End();

            ClothEditController sleeves = Start(Find(root, TestAvatarMenu.SleevesName));
            PoseCheckResult arms = sleeves.RunPoseCheck();
            Assert.That(arms, Is.Not.Null, sleeves.LastPoseCheckError);
            Debug.Log($"[GP02] 袖の動作チェック: {arms.DescribeSummary()} / 基準 {arms.Baseline}");
            foreach (PoseCheckEntry entry in arms.Entries) Debug.Log($"[GP02]   {entry.Pose.Id} {entry.Stage}: {entry.Result}");
            Assert.That(arms.Baseline.IsPenetrating, Is.False, arms.Baseline.ToString());
            Assert.That(Entry(arms, "arms_bent").Result.IsPenetrating, Is.True, "ひじを曲げて、ひじが突き抜けない");
            Assert.That(Entry(arms, "legs_apart").Result.IsPenetrating, Is.False, "足を開いても袖は突き抜けない");
        }

        [Test]
        public void EscapeOutward_MakesThePushedPlacesShallower_SkipsTheWeightCases_AndOneUndoRestores()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.SkirtName));
            PoseCheckResult before = c.RunPoseCheck();
            Assert.That(before, Is.Not.Null, c.LastPoseCheckError);
            Assert.That(before.GainsMeasured, Is.True, "ポーズで動く割合を測っていない");
            PoseEscapePlan plan = before.EscapePlan;
            Assert.That(plan, Is.Not.Null);
            Debug.Log($"[GP02] スカートの外へ逃がす量: 逃がす {plan.PushedRegions}、ウェイトが原因 {plan.LowGainRegions}、深すぎる {plan.TooFarRegions}");
            Assert.That(plan.PushedRegions, Is.GreaterThan(0));
            // 座って太ももがスカートを大きく突き抜ける所（ウェイトが原因）は、深すぎるので逃がさない（逃がしたあとも残ることを、下で確かめる）
            PoseCheckEntry sit = Entry(before, "sit");
            Assert.That(sit.Result.MaxDepth, Is.GreaterThan(PoseEscapeOptions.DefaultMaxPush), sit.Result.ToString());
            Assert.That(plan.TooFarRegions, Is.GreaterThan(0));
            Assert.That(c.CanEscapePoseCheck, Is.True);
            NVector3[] positions = c.Session.Solver.Positions.ToArray();

            var timer = Stopwatch.StartNew();
            PoseEscapeReport report = c.EscapePoseCheckPenetration();
            timer.Stop();
            Assert.That(report, Is.Not.Null);
            Debug.Log($"[GP02] 外へ逃がす（スカート、{timer.ElapsedMilliseconds} ms）: {report}");
            LogEscapeByPose(before, report);
            Assert.That(report.Rechecked, Is.True);
            Assert.That(report.Reverted, Is.False, "効かなかったとして戻した");
            Assert.That(report.Escape.Particles, Is.GreaterThan(0));
            Assert.That(report.PushedRegions, Is.EqualTo(plan.PushedRegions));
            Assert.That(report.Effect.PushedPairs, Is.GreaterThan(0));
            Assert.That(report.PushedDepthAfter, Is.LessThan(report.PushedDepthBefore * 0.6f), "逃がした所が浅くならない");
            Assert.That(report.Effect.ClearedPairs, Is.GreaterThan(report.Effect.PushedPairs / 3), "逃がした所の3分の1以上は、貫通が無くなる");
            Assert.That(report.Escape.MaxStretch, Is.LessThanOrEqualTo(PoseEscapeOptions.DefaultMaxStretch + 0.01f), "布が伸びた");
            Assert.That(report.RegionsAfter, Is.LessThanOrEqualTo(report.RegionsBefore), "か所が増えた");
            Assert.That(report.Effect.TotalDepthAfter, Is.LessThan(report.Effect.TotalDepthBefore));
            Assert.That(c.IsPoseCheckStale, Is.False, "逃がしたあとに調べ直している");
            Assert.That(c.LastPoseCheck, Is.Not.SameAs(before));
            // 座ったときの太ももは、ウェイトが原因なので残る（第2段のウェイトの修正の受け持ち）
            Assert.That(Entry(c.LastPoseCheck, "sit").Result.IsPenetrating, Is.True);

            // 1回の Undo で、逃がす前の形に戻る（結果は古くなる）
            c.UndoLast();
            NVector3[] undone = c.Session.Solver.Positions.ToArray();
            for (int i = 0; i < positions.Length; i++) Assert.That(NVector3.Distance(positions[i], undone[i]), Is.LessThan(1e-6f), $"粒子 {i}");
            Assert.That(c.IsPoseCheckStale, Is.True);
            Assert.That(c.CanEscapePoseCheck, Is.False, "古い結果では逃がさない");
        }

        // ポーズごとの、逃がす前と後（か所・頂点・深さの合計）
        static void LogEscapeByPose(PoseCheckResult before, PoseEscapeReport report)
        {
            if (report.After == null) return;
            for (int k = 0; k < before.Entries.Count && k < report.After.Entries.Count; k++)
            {
                PosePenetration was = before.Entries[k].Result, now = report.After.Entries[k].Result;
                if (!was.IsPenetrating && !now.IsPenetrating) continue;
                float sumWas = 0f, sumNow = 0f;
                foreach (float d in was.Depths) sumWas += Mathf.Min(d, PoseEscapeOptions.DefaultMaxPush);
                foreach (float d in now.Depths) sumNow += Mathf.Min(d, PoseEscapeOptions.DefaultMaxPush);
                Debug.Log($"[GP02]   {before.Entries[k].Name}: か所 {was.RegionCount} → {now.RegionCount}、頂点 {was.Vertices.Count} → {now.Vertices.Count}、" +
                    $"深さの合計 {sumWas * 1000f:F0} → {sumNow * 1000f:F0} mm、最大 {was.MaxDepth * 1000f:F1} → {now.MaxDepth * 1000f:F1} mm");
            }
        }

        [Test]
        public void EscapeOutward_OnTightClothes_KeepsTheClothLength()
        {
            // ぴったりのタイツ: ひざを深く曲げた所はウェイトが原因で逃がさず、逃がす所も布が伸びない所までにとどめる
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.TightsName));
            PoseCheckResult before = c.RunPoseCheck();
            Assert.That(before, Is.Not.Null, c.LastPoseCheckError);
            PoseEscapePlan plan = before.EscapePlan;
            Assert.That(plan.LowGainRegions, Is.GreaterThan(0), "ひざを深く曲げた所が、ウェイトが原因と分からない");
            PoseCheckEntry squat = Entry(before, "squat");
            int lowGain = 0;
            foreach (float gain in squat.Result.Gains) lowGain += gain < PoseEscapeOptions.DefaultMinGain ? 1 : 0;
            Assert.That(lowGain, Is.GreaterThan(0), "しゃがんだときのひざで、ポーズで動く割合が小さい頂点が無い");
            if (!c.CanEscapePoseCheck) return;

            NVector3[] positions = c.Session.Solver.Positions.ToArray();
            PoseEscapeReport report = c.EscapePoseCheckPenetration();
            Assert.That(report, Is.Not.Null);
            Debug.Log($"[GP02] 外へ逃がす（タイツ）: {report}");
            LogEscapeByPose(before, report);
            Assert.That(report.Escape.MaxStretch, Is.LessThanOrEqualTo(PoseEscapeOptions.DefaultMaxStretch + 0.01f), "ぴったりの服を伸ばして逃がした");
            if (report.Reverted)
            {
                // 効かなければ、逃がす前の形に戻り、結果も前のまま使える
                NVector3[] now = c.Session.Solver.Positions.ToArray();
                for (int i = 0; i < positions.Length; i++) Assert.That(NVector3.Distance(positions[i], now[i]), Is.LessThan(1e-6f), $"粒子 {i}");
                Assert.That(c.LastPoseCheck, Is.SameAs(before));
                Assert.That(c.IsPoseCheckStale, Is.False);
                Assert.That(c.CanPreviewPoses, Is.True);
            }
            else
            {
                Assert.That(report.PushedDepthAfter, Is.LessThan(report.PushedDepthBefore), "逃がした所が浅くならない");
                Assert.That(report.Effect.TotalDepthAfter, Is.LessThan(report.Effect.TotalDepthBefore));
            }
        }

        [Test]
        public void NonHumanoidAvatar_GivesAReason_AndDoesNotStart()
        {
            // Animator の無いテスト用の衣装
            GameObject cloth = TestClothMenu.CreateInScene(null);
            _created.Add(cloth);
            foreach (SkinnedMeshRenderer renderer in cloth.GetComponentsInChildren<SkinnedMeshRenderer>()) _created.Add(renderer.sharedMesh);
            ClothEditController c = Start(cloth.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>());
            string problem = c.GetPoseCheckProblem();
            Assert.That(problem, Is.Not.Null);
            StringAssert.Contains("Animator", problem);
            Assert.That(c.BeginPoseCheck(out string error), Is.False);
            Assert.That(error, Is.EqualTo(problem));
            Assert.That(c.RunPoseCheck(), Is.Null);
            Assert.That(c.LastPoseCheckError, Is.EqualTo(problem));
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0));

            // Generic の Avatar（Humanoid でない）
            var animator = cloth.AddComponent<Animator>();
            Avatar generic = AvatarBuilder.BuildGenericAvatar(cloth, "");
            _created.Add(generic);
            animator.avatar = generic;
            problem = c.GetPoseCheckProblem();
            Assert.That(problem, Is.Not.Null);
            StringAssert.Contains("Humanoid", problem);
            Assert.That(c.BeginPoseCheck(out _), Is.False);
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0));
        }

        [Test]
        public void Result_BecomesStale_AfterChangingTheShape_AndAfterSaving()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, TestAvatarMenu.SkirtName);
            ClothEditController c = Start(skirt);
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            Assert.That(c.IsPoseCheckStale, Is.False);

            // つまんで形を変える
            var ray = new Ray(new Vector3(0f, 0.6f, 1f), Vector3.back);
            Assert.That(c.PointerDown(ray, Vector3.back), Is.True);
            c.PointerDrag(new Ray(new Vector3(0f, 0.62f, 1f), Vector3.back));
            c.Tick(NextTime());
            c.PointerUp();
            for (int i = 0; i < 400 && c.IsBusy; i++) c.Tick(NextTime());
            Assert.That(c.IsPoseCheckStale, Is.True, "形を変えたのに古くならない");
            Assert.That(c.LastPoseCheck, Is.Not.Null, "古くなっても結果は残す");
            Assert.That(c.CanPreviewPoses, Is.False);

            // 調べ直すと新しくなり、保存（編集の作り直し）で古くなる
            Assert.That(c.RunPoseCheck(), Is.Not.Null);
            Assert.That(c.IsPoseCheckStale, Is.False);
            Assert.That(c.Save("GP02_PoseCheckTest", out string message), Is.True, message);
            _savedMeshes.Add(c.LastSavedMesh);
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.LastPoseCheck, Is.Not.Null);
            Assert.That(c.IsPoseCheckStale, Is.True, "保存したのに古くならない");
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0), "作り直しで複製を消していない");
        }

        [Test]
        public void ShowingAPose_HidesTheOriginal_AndAnyOtherOperationReturns()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, TestAvatarMenu.SkirtName);
            ClothEditController c = Start(skirt);
            PoseCheckResult result = c.RunPoseCheck();
            Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.That(skirt.forceRenderingOff, Is.True, "編集中は元の衣装を隠してある（プレビューで描く）");

            int next = c.ShowNextPenetratingPose();
            Assert.That(next, Is.EqualTo(result.RankEntries()[0]), "順に見るは、一番悪いポーズから");
            Assert.That(c.IsPosePreviewActive, Is.True);
            Assert.That(c.PoseCheckRig.IsShown, Is.True);
            foreach (Renderer r in renderers) Assert.That(r.forceRenderingOff, Is.True, r.name);
            int second = c.ShowNextPenetratingPose();
            Assert.That(second, Is.Not.EqualTo(next));

            // ほかの操作（つまむ）をすると、元の姿勢に戻る
            Assert.That(c.PointerDown(new Ray(new Vector3(0f, 0.6f, 1f), Vector3.back), Vector3.back), Is.True);
            c.PointerUp();
            Assert.That(c.IsPosePreviewActive, Is.False);
            Assert.That(c.PoseCheckRig.IsShown, Is.False);
            foreach (Renderer r in renderers) Assert.That(r.forceRenderingOff, Is.EqualTo(r == skirt), r.name);

            // 順に再生: 1秒ごとに次のポーズ（全部の段階）、最後まで行ったら元の姿勢に戻る
            Assert.That(c.RunPoseCheck(), Is.Not.Null);
            Assert.That(c.PlayPoses(), Is.True);
            Assert.That(c.IsPosePlaying, Is.True);
            double start = 1000.0;
            c.Tick(start);
            int first = c.PosePreviewEntry;
            c.Tick(start + ClothEditController.PosePlaySeconds * 1.5);
            Assert.That(c.PosePreviewEntry, Is.Not.EqualTo(first));
            Assert.That(c.PosePreview.Stage, Is.EqualTo(1f));
            c.Tick(start + ClothEditController.PosePlaySeconds * (PoseLibrary.All.Count + 1));
            Assert.That(c.IsPosePlaying, Is.False);
            Assert.That(c.IsPosePreviewActive, Is.False);

            // Undo / 編集の終了でも戻る
            c.ShowPoseEntry(0);
            c.OnUndoRedoPerformed();
            Assert.That(c.IsPosePreviewActive, Is.False);
            c.ShowPoseEntry(0);
            c.End();
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0));
            foreach (Renderer r in renderers) Assert.That(r.forceRenderingOff, Is.False, r.name);
        }

        [Test]
        public void IncrementalCheck_AdvancesWithTicks_AndCanBeStopped()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.SkirtName));
            c.Settings.PoseCheckBothStages = false;
            c.Settings.SetPoseEnabled("bow", false);
            Assert.That(c.BeginPoseCheck(out string error), Is.True, error);
            Assert.That(c.IsPoseCheckRunning, Is.True);
            c.GetPoseCheckCounts(out int done, out int total);
            Assert.That(total, Is.EqualTo(PoseLibrary.All.Count - 1 + 1), "基準＋試すポーズ（全部の段階だけ、お辞儀を除く）");
            c.Tick(NextTime());
            c.GetPoseCheckCounts(out int afterTick, out _);
            Assert.That(afterTick, Is.GreaterThan(done));

            // やめると結果は作らず、複製も残らない
            c.CancelPoseCheck();
            Assert.That(c.IsPoseCheckRunning, Is.False);
            Assert.That(c.LastPoseCheck, Is.Null);
            Assert.That(PoseRig.LiveCount, Is.EqualTo(0));

            // 最後まで回すと結果ができる。ほかの操作をすると途中でもやめる
            Assert.That(c.BeginPoseCheck(out _), Is.True);
            for (int i = 0; i < 200 && c.IsPoseCheckRunning; i++) c.Tick(NextTime());
            Assert.That(c.IsPoseCheckRunning, Is.False);
            Assert.That(c.LastPoseCheck, Is.Not.Null);
            Assert.That(c.LastPoseCheck.StageCount, Is.EqualTo(1));
            Assert.That(c.LastPoseCheck.Entries.Count, Is.EqualTo(PoseLibrary.All.Count - 1));
            Assert.That(c.BeginPoseCheck(out _), Is.True);
            c.ResetAll();
            Assert.That(c.IsPoseCheckRunning, Is.False);
            Assert.That(PoseRig.LiveCount, Is.EqualTo(1), "前の結果の複製だけが残る");
        }

        // 動作チェック1回の時間をログに出す（GP02_UNITY_ARGS=-debugCodeOptimization でも回す）
        [Test]
        public void Timing_OneMotionCheck()
        {
            GameObject root = CreateAvatar();
            foreach (string name in new[] { TestAvatarMenu.SkirtName, TestAvatarMenu.TightsName })
            {
                ClothEditController c = Start(Find(root, name));
                var timer = Stopwatch.StartNew();
                PoseCheckResult result = c.RunPoseCheck();
                timer.Stop();
                Assert.That(result, Is.Not.Null, c.LastPoseCheckError);
                Debug.Log($"[GP02] 動作チェック1回の時間（{name}）: {timer.ElapsedMilliseconds} ms（準備 {result.PrepareMilliseconds:F0} ms、" +
                    $"{result.Entries.Count} 回、1回平均 {(result.Milliseconds - result.PrepareMilliseconds) / result.Entries.Count:F1} ms）/ 布の頂点 {c.Session.WeldedVertexCount}・粒子 {c.Session.ParticleCount}" +
                    $" / {result.BodyDescription} / {ClothEditSession.DescribeAcceleration(c.Session.Solver.Kernel)}");
                c.End();
            }
        }
    }
}
