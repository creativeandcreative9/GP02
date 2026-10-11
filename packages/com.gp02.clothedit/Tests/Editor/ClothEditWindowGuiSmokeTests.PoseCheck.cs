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
        // 動作チェック: Humanoid のテスト用アバターのスカートを編集し、かんたん表示の「動作チェック」の区切り（始める前・結果）、
        // ポーズを見せているシーンビュー（赤い点）、編集の姿勢での最悪の深さの色分け、くわしく表示（試すポーズ・ポーズごとの結果）、
        // 「元のポーズで外へ逃がす」のあとを描く
        [UnityTest]
        public IEnumerator Window_And_SceneView_ShowThePoseCheck()
        {
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
            SkinnedMeshRenderer skirt = null;
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (renderer.name == TestAvatarMenu.SkirtName) skirt = renderer;
            }
            Assert.That(skirt, Is.Not.Null);
            try
            {
                SetField(window, "_target", skirt);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldTarget", "_foldTool", "_foldBrush", "_foldSymmetry", "_foldDisplay", "_foldSave", "_foldGuide", "_foldDiagnostics", "_foldSelection" })
                {
                    SetField(window, fold, false);
                }
                SetField(window, "_foldPoseCheck", true);
                controller.Settings.ShowStretchHeatmap = false;
                controller.Settings.ShowPoseCheckDepth = false;
                controller.ApplySettings();
                Assert.That(controller.GetPoseCheckProblem(), Is.Null);
                yield return Pump(10);
                Shot(window, "posecheck_window_simple_before");

                // 調べる（画面の更新を待たずに最後まで）
                PoseCheckResult result = controller.RunPoseCheck();
                Assert.That(result, Is.Not.Null, controller.LastPoseCheckError);
                Assert.That(result.PenetratingPoseCount, Is.GreaterThan(0));
                yield return Pump(10);
                Shot(window, "posecheck_window_simple_result");

                // 一番悪いポーズを、シーンで見せる（元のアバターは隠れ、複製がそのポーズになる）
                int shown = controller.ShowNextPenetratingPose();
                Assert.That(shown, Is.GreaterThanOrEqualTo(0));
                Assert.That(controller.PoseCheckRig.IsShown, Is.True);
                sceneView.LookAt(new Vector3(0f, 0.55f, 0.15f), Quaternion.Euler(20f, 200f, 0f), 0.9f);
                yield return Pump(20);
                Shot(sceneView, "posecheck_scene_pose");
                Shot(window, "posecheck_window_showing");

                // 元の姿勢に戻して、最悪の深さで色分け
                controller.EndPosePreview();
                Assert.That(controller.IsPosePreviewActive, Is.False);
                controller.Settings.ShowPoseCheckDepth = true;
                controller.ApplySettings();
                sceneView.LookAt(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(10f, 200f, 0f), 0.8f);
                yield return Pump(20);
                Shot(sceneView, "posecheck_scene_depth");

                // くわしく表示: 試すポーズとポーズごとの結果
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                SetField(window, "_foldPoseList", true);
                SetField(window, "_foldPoseEntries", true);
                window.position = new Rect(10, 10, 460, 1000);
                yield return Pump(10);
                Shot(window, "posecheck_window_detailed");

                // 元のポーズで外へ逃がす（終わると調べ直して、前後の件数を出す）
                ClothEditView.Mode = ClothEditViewMode.Simple;
                Assert.That(controller.CanEscapePoseCheck, Is.True);
                typeof(ClothEditWindow).GetMethod("EscapePoseCheckFromWindow", Private).Invoke(window, null);
                Assert.That(controller.LastPoseEscape, Is.Not.Null);
                yield return Pump(10);
                Shot(window, "posecheck_window_after_escape");
            }
            finally
            {
                savedSettings.Save();
                window.Close();
                sceneView.Close();
            }
        }
    }
}
