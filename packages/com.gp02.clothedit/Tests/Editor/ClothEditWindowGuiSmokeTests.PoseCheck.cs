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
        // テスト用のアバターを作り、name の衣装で編集を始める（動作チェックの区切りだけを開く）
        ClothEditController StartPoseCheckEdit(ClothEditWindow window, string name)
        {
            GameObject root = Track(TestAvatarMenu.CreateInScene(null));
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material material in renderer.sharedMaterials) Track(material);
            }
            Track(root.GetComponent<Animator>().avatar);
            SkinnedMeshRenderer target = null;
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (renderer.name == name) target = renderer;
            }
            Assert.That(target, Is.Not.Null);
            SetField(window, "_target", target);
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
            controller.Settings.FixLargePoses = false;
            controller.ApplySettings();
            return controller;
        }

        // 動作チェック: Humanoid のテスト用アバターのスカートを編集し、かんたん表示の「動作チェック」の区切り（始める前・結果）、
        // 結果は「よく使うポーズ」と「大きく動くポーズ」に分けて出し、既定ではよく使うポーズだけを直す（スカートは「問題ありません ✓」になりやすい）。
        // ポーズを見せているシーンビュー（赤い点）、編集の姿勢での色分けの点、「大きく動くポーズも直す」をオンにしたときのおすすめの直し方、
        // くわしい説明、くわしく表示（試すポーズ・ポーズごとの結果）、「元のポーズで外へ逃がす」のあとを描く
        [UnityTest]
        public IEnumerator Window_And_SceneView_ShowThePoseCheck()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 1000);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            try
            {
                ClothEditController controller = StartPoseCheckEdit(window, TestAvatarMenu.SkirtName);
                controller.Settings.ShowPoseCheckDepth = false;
                Assert.That(controller.GetPoseCheckProblem(), Is.Null);
                yield return Pump(10);
                Shot(window, "posecheck_window_simple_before");

                // 調べる（画面の更新を待たずに最後まで）。既定では、よく使うポーズだけを直す
                PoseCheckResult result = controller.RunPoseCheck();
                Assert.That(result, Is.Not.Null, controller.LastPoseCheckError);
                Assert.That(result.PenetratingPoseCount, Is.GreaterThan(0));
                Assert.That(result.IncludesLargePoses, Is.False);
                Assert.That(controller.IsPoseCheckDepthShown, Is.False, "好みの設定がオフなら、色分けの点は出さない");
                yield return Pump(10);
                Shot(window, "posecheck_window_simple_result");

                // 悪い順の一覧は、折りたたんでいる。見出しの開閉だけの値で、開くと一覧と「順に見る」などが出る
                Assert.That(GetField<bool>(window, "_foldPoseRanked"), Is.False);
                SetField(window, "_foldPoseRanked", true);
                yield return Pump(10);
                Shot(window, "posecheck_window_simple_list");
                SetField(window, "_foldPoseRanked", false);
                yield return Pump(5);

                // 「順に見る」の最初のポーズを、シーンで見せる（元のアバターは隠れ、複製がそのポーズになる）
                int shown = controller.ShowNextPenetratingPose();
                Assert.That(shown, Is.GreaterThanOrEqualTo(0));
                Assert.That(controller.PoseCheckRig.IsShown, Is.True);
                sceneView.LookAt(new Vector3(0f, 0.55f, 0.15f), Quaternion.Euler(20f, 200f, 0f), 0.9f);
                yield return Pump(20);
                Shot(sceneView, "posecheck_scene_pose");
                // ポーズを見せている間は、折りたたんでいても自動で開く（開閉の値は変えない）
                Assert.That(GetField<bool>(window, "_foldPoseRanked"), Is.False);
                Shot(window, "posecheck_window_showing");
                Assert.That(GetField<bool>(window, "_foldPoseRanked"), Is.False, "見せている間に描いても、開閉の値は変わらない");

                // 「大きく動くポーズも直す」をオンにすると、調べ直さずに、おすすめの直し方が座るなども数える。色分けの点も出す
                controller.EndPosePreview();
                Assert.That(controller.IsPosePreviewActive, Is.False);
                controller.Settings.FixLargePoses = true;
                Assert.That(controller.ApplyPoseFixScope(), Is.True);
                Assert.That(controller.LastPoseCheck, Is.SameAs(result));
                controller.SetPoseCheckDepthShown(true);
                Assert.That(controller.IsPoseCheckDepthShown, Is.True);
                sceneView.LookAt(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(10f, 200f, 0f), 0.8f);
                yield return Pump(20);
                Shot(sceneView, "posecheck_scene_depth");
                Shot(window, "posecheck_window_simple_large");

                // くわしい説明を開く
                SetField(window, "_foldPoseExplain", true);
                yield return Pump(10);
                Shot(window, "posecheck_window_explanation");
                SetField(window, "_foldPoseExplain", false);

                // くわしく表示: 内訳・試すポーズ（種類ごと）とポーズごとの結果
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                SetField(window, "_foldPoseList", true);
                SetField(window, "_foldPoseEntries", true);
                window.position = new Rect(10, 10, 460, 1000);
                yield return Pump(10);
                Shot(window, "posecheck_window_detailed");

                // 元のポーズで外へ逃がす（終わると調べ直して、前後の件数を窓に出す。通知は短く）
                ClothEditView.Mode = ClothEditViewMode.Simple;
                SetField(window, "_foldPoseList", false);
                SetField(window, "_foldPoseEntries", false);
                Assert.That(controller.CanEscapePoseCheck, Is.True);
                typeof(ClothEditWindow).GetMethod("EscapePoseCheckFromWindow", Private).Invoke(window, null);
                Assert.That(controller.LastPoseEscape, Is.Not.Null);
                yield return Pump(10);
                Shot(window, "posecheck_window_after_escape");

                // ほかの操作（ピン留め）を始めると、色分けの点が消える
                controller.SetPoseCheckDepthShown(true);
                controller.InvertPins();
                Assert.That(controller.IsPoseCheckDepthShown, Is.False);
                controller.ClearPins();
            }
            finally
            {
                savedSettings.Save();
                window.Close();
                sceneView.Close();
            }
        }

        // よく使うポーズで突き抜ける袖（腕を体の前で曲げる・ひじ）では、既定の範囲でも「おすすめの直し方」に件数とボタンが出る
        [UnityTest]
        public IEnumerator Window_ShowsTheSuggestedFixes_ForEverydayPoses()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 1000);
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            try
            {
                ClothEditController controller = StartPoseCheckEdit(window, TestAvatarMenu.SleevesName);
                PoseCheckResult result = controller.RunPoseCheck();
                Assert.That(result, Is.Not.Null, controller.LastPoseCheckError);
                Assert.That(result.CommonClear, Is.False, "腕を体の前で曲げると、袖のひじが突き抜ける");
                PoseFixAdvice advice = PoseFixAdvice.From(result, null);
                Assert.That(advice.EscapeRegions + advice.WeightRegions, Is.GreaterThan(0));
                yield return Pump(10);
                Shot(window, "posecheck_window_sleeves_advice");
            }
            finally
            {
                savedSettings.Save();
                window.Close();
            }
        }
    }
}
