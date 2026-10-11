using System.Collections;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GP02.Tests
{
    public partial class ClothEditWindowGuiSmokeTests
    {
        // 貫通: テスト用の衣装の体に Hip_Big を 100 で入れると、後ろの腰で体がスカートを突き抜ける。
        // 診断の「体との貫通」とシーンビューの赤い点を描き、「貫通を直す」を押したあと（点が消える）も描く
        [UnityTest]
        public IEnumerator Window_And_SceneView_ShowPenetration_AndFixIt()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 1000);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            GameObject root = Track(TestClothMenu.CreateInScene(null));
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material material in renderer.sharedMaterials) Track(material);
            }
            var body = root.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
            body.SetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName), 100f);
            try
            {
                SetField(window, "_target", skirt);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldTarget", "_foldTool", "_foldBrush", "_foldSymmetry", "_foldDisplay", "_foldSave", "_foldGuide" }) SetField(window, fold, false);
                SetField(window, "_foldDiagnostics", true);
                controller.Settings.SymmetryEnabled = false;
                controller.Settings.ShowStretchHeatmap = false;
                controller.ApplySettings();

                PenetrationReport found = controller.LastPenetration;
                Assert.That(found, Is.Not.Null, "編集を始めたときに調べていません");
                Assert.That(found.PenetratingCount, Is.GreaterThan(0), found.ToString());
                Assert.That(found.PointParticles.Count + found.PointBodyVertices.Count, Is.GreaterThan(0), "赤い点がありません");

                // 斜め後ろ（+Z・+X 側）から腰を見る
                sceneView.LookAt(new Vector3(0f, 0.74f, 0.08f), Quaternion.Euler(15f, 210f, 0f), 0.32f);
                yield return Pump(20);
                Shot(window, "penetration_window_detected");
                Shot(sceneView, "penetration_scene_detected");

                typeof(ClothEditWindow).GetMethod("FixPenetrationFromWindow", Private).Invoke(window, null);
                PenetrationReport after = controller.LastPenetration;
                Assert.That(controller.LastPenetrationFix, Is.Not.Null);
                Assert.That(after.PenetratingCount, Is.EqualTo(0), controller.LastPenetrationFix.ToString());
                Assert.That(after.PointParticles.Count + after.PointBodyVertices.Count, Is.EqualTo(0), "直したあとも赤い点が残っています");
                yield return Pump(20);
                Shot(window, "penetration_window_fixed");
                Shot(sceneView, "penetration_scene_fixed");
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
