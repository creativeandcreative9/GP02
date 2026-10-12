using System.Collections;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GP02.Tests
{
    public partial class ClothEditWindowGuiSmokeTests
    {
        // 動作チェックの第2段: テスト用アバターのタイツを編集し、かんたん表示の「おすすめの直し方」の「ウェイトを体に寄せる」（体どうしの重なりの知らせと一緒に）、
        // 候補の確認（前後の件数・伸び）、適用のあと、寄せたあとのしゃがんだポーズのシーン、
        // くわしく表示（強さ・周りへの広がり・データから外す・手持ちの AnimationClip）を描く。
        // しゃがむ・座る（大きく動くポーズ）のひざを直すので、「大きく動くポーズも直す」をオンにする（既定の範囲の画面も1枚撮る）
        [UnityTest]
        public IEnumerator Window_And_SceneView_ShowTheWeightFit()
        {
            const string folder = "Assets/GP02_GuiWeightFitTemp";
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 1000);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            GameObject root = Track(TestAvatarMenu.CreateInScene(null));
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material material in renderer.sharedMaterials) Track(material);
            }
            Track(root.GetComponent<Animator>().avatar);
            SkinnedMeshRenderer tights = null;
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (renderer.name == TestAvatarMenu.TightsName) tights = renderer;
            }
            Assert.That(tights, Is.Not.Null);
            // くわしく表示の欄に出す、手持ちの AnimationClip（座りモーションの代わり）
            AnimationClip clip = Track(new AnimationClip { name = "SitMotion", frameRate = 30f });
            foreach (string side in new[] { "Left", "Right" })
            {
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), side + " Upper Leg Front-Back"), AnimationCurve.Linear(0f, 0f, 1f, -0.667f));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), side + " Lower Leg Stretch"), AnimationCurve.Linear(0f, 1f, 1f, -0.125f));
            }
            try
            {
                SetField(window, "_target", tights);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                controller.SaveFolder = folder + "/Meshes";
                controller.DataFolder = folder + "/Data";
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldTarget", "_foldTool", "_foldBrush", "_foldSymmetry", "_foldDisplay", "_foldSave", "_foldGuide", "_foldDiagnostics", "_foldSelection" })
                {
                    SetField(window, fold, false);
                }
                SetField(window, "_foldPoseCheck", true);
                controller.Settings.ShowStretchHeatmap = false;
                controller.Settings.ShowPoseCheckDepth = false;
                controller.ApplySettings();

                // 調べると、ひざの貫通（ウェイトが原因）と、手が太ももに入る体どうしの重なりが出る
                controller.Settings.FixLargePoses = false;
                PoseCheckResult result = controller.RunPoseCheck();
                Assert.That(result, Is.Not.Null, controller.LastPoseCheckError);
                Assert.That(result.OverlapRegionCount, Is.GreaterThan(0));
                yield return Pump(10);
                Shot(window, "weightfit_window_simple_default");
                controller.Settings.FixLargePoses = true;
                controller.ApplyPoseFixScope();
                Assert.That(controller.WeightCausedRegionCount, Is.GreaterThan(0));
                yield return Pump(10);
                Shot(window, "weightfit_window_simple_result");

                // 確認（候補のウェイトで、もう一度ポーズを試す）。画面のボタンは結果をダイアログで聞くので、ここでは直接呼ぶ
                WeightFixPreview preview = controller.PreviewWeightFix();
                Assert.That(preview, Is.Not.Null, controller.LastWeightFixError);
                yield return Pump(10);
                Shot(window, "weightfit_window_simple_candidate");

                // 適用（保存していない編集は無いので、そのまま適用する）
                typeof(ClothEditWindow).GetMethod("ApplyWeightFixFromWindow", Private).Invoke(window, null);
                Assert.That(controller.LastWeightFix, Is.Not.Null, controller.LastWeightFixError);
                yield return Pump(10);
                Shot(window, "weightfit_window_after_apply");

                // 寄せたあとの、しゃがむ（全部）
                int squat = -1;
                for (int i = 0; i < controller.LastPoseCheck.Entries.Count; i++)
                {
                    PoseCheckEntry entry = controller.LastPoseCheck.Entries[i];
                    if (entry.Pose != null && entry.Pose.Id == "squat" && entry.Stage > 0.75f) squat = i;
                }
                Assert.That(squat, Is.GreaterThanOrEqualTo(0));
                controller.ShowPoseEntry(squat);
                Assert.That(controller.IsPosePreviewActive, Is.True);
                sceneView.LookAt(new Vector3(0f, 0.4f, 0.15f), Quaternion.Euler(15f, 150f, 0f), 0.8f);
                yield return Pump(20);
                Shot(sceneView, "weightfit_scene_squat_after");
                controller.EndPosePreview();

                // くわしく表示: 強さ・周りへの広がり・確認・適用・データから外す、手持ちの AnimationClip の欄
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                controller.Settings.PoseCheckClips.Add(clip);
                SetField(window, "_foldPoseClips", true);
                window.position = new Rect(10, 10, 460, 1000);
                yield return Pump(10);
                Shot(window, "weightfit_window_detailed");
            }
            finally
            {
                savedSettings.Save();
                window.Close();
                sceneView.Close();
                Undo.ClearAll();
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        // スカートに強さ 100% で当てると、体から離れた布が脚に貼り付いて伸びる。かんたん表示の窓に出る、伸びすぎる候補（確認の「弱めて（50%）試す」を勧める一言）と、
        // くわしく表示でのその候補（強さを下げる一言）、強さ 50% で試し直した候補（説明の1行目に強さ）、50% で適用したあとの結果の欄を描く。
        // 確認のダイアログは自動テストでは出せないので、候補は直接作る（ダイアログの文と流れは EditMode の WeightFixWeakerTests で確かめる）
        [UnityTest]
        public IEnumerator Window_ShowsTheStretchingSkirtCandidate_TheWeakerOne_AndTheResultAfterApplying()
        {
            const string folder = "Assets/GP02_GuiWeakerTemp";
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 1000);
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            GameObject root = Track(TestAvatarMenu.CreateInScene(null));
            SkinnedMeshRenderer skirt = null;
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material material in renderer.sharedMaterials) Track(material);
                if (renderer.name == TestAvatarMenu.SkirtName) skirt = renderer;
            }
            Track(root.GetComponent<Animator>().avatar);
            Assert.That(skirt, Is.Not.Null);
            try
            {
                SetField(window, "_target", skirt);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                controller.SaveFolder = folder + "/Meshes";
                controller.DataFolder = folder + "/Data";
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldTarget", "_foldTool", "_foldBrush", "_foldSymmetry", "_foldDisplay", "_foldSave", "_foldGuide", "_foldDiagnostics", "_foldSelection" })
                {
                    SetField(window, fold, false);
                }
                SetField(window, "_foldPoseCheck", true);
                controller.Settings.ShowStretchHeatmap = false;
                controller.Settings.ShowPoseCheckDepth = false;
                controller.Settings.WeightFixStrength = 1f;
                controller.Settings.FixLargePoses = true;   // スカートが脚に貼り付いて伸びるのは、しゃがむ・座る（大きく動くポーズ）
                controller.ApplySettings();
                Assert.That(controller.RunPoseCheck(), Is.Not.Null, controller.LastPoseCheckError);
                Assert.That(controller.CanPreviewWeightFix, Is.True);

                // 強さ 100% の候補: 体から離れた布が伸びすぎる。説明の最後に「確認で『弱めて（50%）試す』を選ぶと和らぎます」
                WeightFixPreview full = controller.PreviewWeightFix();
                Assert.That(full, Is.Not.Null, controller.LastWeightFixError);
                Assert.That(full.StretchesTooMuch, Is.True);
                yield return Pump(10);
                Shot(window, "weightfit_skirt_candidate_100");

                // くわしく表示: 同じ候補。強さのスライダーがあるので、一言は「『強さ』を下げて確認し直すと…」になる
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                window.position = new Rect(10, 10, 460, 1000);
                yield return Pump(10);
                Shot(window, "weightfit_skirt_candidate_100_detailed");
                ClothEditView.Mode = ClothEditViewMode.Simple;
                window.position = new Rect(10, 10, 420, 1000);

                // 弱めて（50%）試した候補: 設定の強さは 100% のまま。説明の1行目に「強さ 50%」
                WeightFixPreview weaker = controller.PreviewWeightFix(WeightFixPreview.WeakerStrength);
                Assert.That(weaker, Is.Not.Null, controller.LastWeightFixError);
                Assert.That(weaker.Strength, Is.EqualTo(WeightFixPreview.WeakerStrength).Within(1e-6f));
                Assert.That(controller.Settings.WeightFixStrength, Is.EqualTo(1f));
                Debug.Log($"[GP02] スカート（窓の画像）強さ 100%: {full.Describe(false).Replace('\n', '/')} | 強さ 50%: {weaker.Describe(false).Replace('\n', '/')}");
                yield return Pump(10);
                Shot(window, "weightfit_skirt_candidate_50");

                // 50% の候補を適用: 窓の上の結果の欄に「ウェイトを体に寄せました（強さ 50%。Ctrl+Z の 1 回で戻せます）。」
                typeof(ClothEditWindow).GetMethod("ApplyWeightFixFromWindow", Private).Invoke(window, null);
                Assert.That(controller.LastWeightFix, Is.Not.Null, controller.LastWeightFixError);
                Assert.That(controller.LastWeightFix.Preview.Strength, Is.EqualTo(WeightFixPreview.WeakerStrength).Within(1e-6f));
                Assert.That(controller.Settings.WeightFixStrength, Is.EqualTo(1f));
                yield return Pump(10);
                Shot(window, "weightfit_skirt_after_apply_50");
            }
            finally
            {
                savedSettings.Save();
                window.Close();
                Undo.ClearAll();
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
