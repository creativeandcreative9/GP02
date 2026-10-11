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
    }
}
