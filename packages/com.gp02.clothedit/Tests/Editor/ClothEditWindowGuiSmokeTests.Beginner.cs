using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GP02.Tests
{
    public partial class ClothEditWindowGuiSmokeTests
    {
        // 初心者の立場での批評の項目（札・手順の1行・付け根のピン留め・状態の行・一覧）を、実際に描いて確かめ、画像にする。
        // シーンビューの札の「×」や札の上のクリックで、布の操作（つまむ）が始まらないことも確かめる
        [UnityTest]
        public IEnumerator SceneView_Cards_CloseButtonsAndCardsDoNotStartTheBrush()
        {
            bool hadKey = EditorPrefs.HasKey(ClothEditHelp.ShownOncePrefKey);
            bool keyValue = EditorPrefs.GetBool(ClothEditHelp.ShownOncePrefKey, false);
            EditorPrefs.DeleteKey(ClothEditHelp.ShownOncePrefKey);
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 900);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            Renderer cloth = CreateCloth();
            ClothEditSettings savedSettings = ClothEditSettings.Load();
            try
            {
                SetField(window, "_target", cloth);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                controller.Settings.Tool = ClothEditMode.Grab;
                controller.Settings.SymmetryEnabled = false;
                controller.ApplySettings();
                // 布が画面いっぱいに見えるようにする（札の後ろにも布がある。札が無ければ、そこをつまめてしまう）
                sceneView.LookAt(new Vector3(0f, 1f, 0f), Quaternion.identity, 0.42f);
                yield return Pump(20);
                Vector2 windowOffset = MeasureWindowOffset(sceneView);
                Assert.That(window.OverlayShortcutsVisible, Is.False, "初めての編集でも、約30行の一覧は自動では出ない");
                Assert.That(window.StartCardVisible, Is.True);
                Assert.That(Tools.hidden, Is.True, "編集中は、Unity の道具のハンドルを隠す");
                Shot(sceneView, "beginner_scene_start_card");

                Rect card = GetField<Rect>(window, "_startCardRect");
                Rect close = GetField<Rect>(window, "_startCardCloseRect");
                Rect hint = GetField<Rect>(window, "_hintRect");
                Assert.That(card.width, Is.GreaterThan(100f), "小さな札が描かれている");
                Assert.That(close.width, Is.GreaterThan(8f), "「×」が描かれている");
                Assert.That(hint.width, Is.GreaterThan(100f), "案内が描かれている");
                Assert.That(hint.yMax, Is.LessThanOrEqualTo(card.y), "札は案内の下");

                // 札の下に布があることを確かめる（そこへのクリックは、本来なら、つまむ操作になる）
                Vector2 onCard = new Vector2(card.xMax - 40f, card.y + 28f);   // 右寄り（「×」より左）。布は画面の左端までは無いので、布のある側で確かめる
                Vector2 onHint = hint.center;
                Assert.That(controller.Hover(MouseRay(sceneView, onCard)), Is.True, "札の下に布がある");
                Assert.That(controller.Hover(MouseRay(sceneView, onHint)), Is.True, "案内の下にも布がある");
                int pinsBefore = controller.Session.PinnedCount;

                // 札の本体・案内をクリックしても、つまむ操作は始まらない
                foreach (Vector2 point in new[] { onCard, onHint })
                {
                    sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = W(windowOffset, point) });
                    sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = W(windowOffset, point) });
                    Assert.That(controller.IsDragging, Is.False, "札の上でマウスを押しても、つまんでいない");
                    sceneView.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = W(windowOffset, point + new Vector2(10f, 10f)) });
                    sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = W(windowOffset, point + new Vector2(10f, 10f)) });
                    yield return Pump(3);
                    Assert.That(controller.IsDragging || controller.IsSettling, Is.False);
                    Assert.That(controller.HasUnsavedEdits, Is.False, "布は動いていない");
                    Assert.That(window.StartCardVisible, Is.True, "本体のクリックでは、札は閉じない");
                }

                // 「×」を押すと閉じる。つまむ操作は始まらない。次からは出さない
                Vector2 cross = close.center;
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = W(windowOffset, cross) });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = W(windowOffset, cross) });
                Assert.That(controller.IsDragging, Is.False, "「×」を押しても、つまんでいない");
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = W(windowOffset, cross) });
                yield return Pump(6);
                Assert.That(controller.IsDragging || controller.IsSettling, Is.False);
                Assert.That(controller.HasUnsavedEdits, Is.False, "布は動いていない");
                Assert.That(controller.Session.PinnedCount, Is.EqualTo(pinsBefore));
                Assert.That(window.StartCardVisible, Is.False, "「×」で札が閉じた");
                Assert.That(EditorPrefs.GetBool(ClothEditHelp.ShownOncePrefKey, false), Is.True, "次からは出さない");
                Shot(sceneView, "beginner_scene_card_closed");

                // 札の外の布をドラッグすれば、今までどおり、つまめる
                Vector2 free = new Vector2(sceneView.position.width * 0.6f, sceneView.position.height * 0.6f);
                Assert.That(controller.Hover(MouseRay(sceneView, free)), Is.True);
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = W(windowOffset, free) });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = W(windowOffset, free) });
                Assert.That(controller.IsDragging, Is.True, "札の外では、つまめる");
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = W(windowOffset, free) });
                controller.Cancel();
                yield return Pump(3);

                // 全部のショートカットの一覧: 「?」で出る。右上の「×」で閉じる。つまむ操作は始まらない
                window.OverlaySetShortcutsVisible(true);
                yield return Pump(10);
                Assert.That(window.OverlayShortcutsVisible, Is.True);
                Rect list = GetField<Rect>(window, "_listRect");
                Rect listClose = GetField<Rect>(window, "_listCloseRect");
                Assert.That(list.height, Is.GreaterThan(200f), "全部の一覧が描かれている");
                Assert.That(listClose.width, Is.GreaterThan(8f), "一覧にも「×」がある");
                Shot(sceneView, "beginner_scene_full_list");
                Vector2 listCross = listClose.center;
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = W(windowOffset, listCross) });
                sceneView.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = W(windowOffset, listCross) });
                Assert.That(controller.IsDragging, Is.False, "一覧の「×」を押しても、つまんでいない");
                sceneView.SendEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = W(windowOffset, listCross) });
                yield return Pump(6);
                Assert.That(window.OverlayShortcutsVisible, Is.False, "一覧の「×」で閉じる");
                Assert.That(controller.HasUnsavedEdits, Is.False);

                // 編集を終えたら、Unity の道具のハンドルは元に戻る
                Assert.That(window.RequestEnd(), Is.True);
                yield return Pump(3);
                Assert.That(Tools.hidden, Is.False, "編集を終えたら、Unity の道具のハンドルが戻る");
            }
            finally
            {
                savedSettings.Save();
                if (hadKey) EditorPrefs.SetBool(ClothEditHelp.ShownOncePrefKey, keyValue);
                else EditorPrefs.DeleteKey(ClothEditHelp.ShownOncePrefKey);
                Tools.hidden = false;
                window.Close();
                sceneView.Close();
            }
        }

        // かんたん表示の編集中の窓: 手順の1行・付け根のピン留め・見出しの保存・目立たない貫通の行・状態の行（伸びの言葉と色）・保存したシェイプキーの一覧。
        // シーンビューのピンの点（青）と案内。くわしく表示の保存の一覧（重ねて見る）
        [UnityTest]
        public IEnumerator Window_Editing_ShowsTheNextStepAutoPinSaveAndTheStatusWords()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 900);
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
            SkinnedMeshRenderer skirt = root.GetComponentsInChildren<SkinnedMeshRenderer>().First(r => r.name == TestAvatarMenu.SkirtName);
            try
            {
                SetField(window, "_target", skirt);
                typeof(ClothEditWindow).GetMethod("StartEdit", Private).Invoke(window, null);
                var controller = GetField<ClothEditController>(window, "_controller");
                Assert.That(controller.IsEditing, Is.True, controller.LastError);
                controller.SaveFolder = "Assets/GP02_BeginnerGuiTemp";
                controller.DataFolder = "Assets/GP02_BeginnerGuiTemp";
                SetField(window, "_showShortcuts", false);
                foreach (string fold in new[] { "_foldGuide" }) SetField(window, fold, false);
                controller.Settings.ShowStretchHeatmap = false;
                controller.ApplySettings();
                sceneView.LookAt(new Vector3(0f, 0.8f, 0f), Quaternion.Euler(8f, 180f, 0f), 1.1f);
                yield return Pump(15);

                // 1. 編集を始めた直後。手順の区切りは畳んであるが、「次: 3. 動かさない所をピン留め」と、付け根のボタンが見える
                Shot(window, "beginner_window_editing_start");
                Shot(sceneView, "beginner_scene_editing_start");

                // 2. 付け根を自動でピン留めする。通知は短く、詳しい結果は窓の中（見出しの下）に残る。ピンの点は青
                AnchorPinResult pinned = window.AutoPinAnchors();
                Assert.That(pinned.Pinned, Is.GreaterThan(0));
                yield return Pump(15);
                Shot(window, "beginner_window_after_autopin");
                Shot(sceneView, "beginner_scene_after_autopin");

                // 3. 伸びの言葉と色（状態の行）。数字は、ツールチップに残る
                SetStretch(controller, 0.02f);
                yield return Pump(4);
                Shot(window, "beginner_window_stretch_none");
                SetStretch(controller, 0.09f);
                yield return Pump(4);
                Shot(window, "beginner_window_stretch_slight");
                SetStretch(controller, 5.551f);
                yield return Pump(4);
                Shot(window, "beginner_window_stretch_large");
                SetStretch(controller, 0f);

                // 4. 見出しの「保存」で保存する。保存したシェイプキーの一覧には、実際の重みが「表示中（重み 100）」と出る
                Assert.That(window.SaveFromHeader(), Is.True, controller.LastError);
                SetField(window, "_foldGuide", true);
                yield return Pump(10);
                Shot(window, "beginner_window_after_save_simple");

                // 5. くわしく表示では、「重ねて見る」スライダーが出る
                ClothEditView.Mode = ClothEditViewMode.Detailed;
                window.position = new Rect(10, 10, 460, 1100);
                yield return Pump(10);
                Shot(window, "beginner_window_after_save_detailed");
                ClothEditView.Mode = ClothEditViewMode.Simple;
                window.position = new Rect(10, 10, 420, 900);
                yield return Pump(4);

                Assert.That(window.RequestEnd(), Is.True);
            }
            finally
            {
                savedSettings.Save();
                if (AssetDatabase.IsValidFolder("Assets/GP02_BeginnerGuiTemp")) AssetDatabase.DeleteAsset("Assets/GP02_BeginnerGuiTemp");
                Tools.hidden = false;
                ClothEditView.Mode = ClothEditViewMode.Simple;
                window.Close();
                sceneView.Close();
            }
        }

        // 間違えやすい選び方: アバター全体を選んだとき（この中の衣装の一覧）と、体を対象にしたとき（体のメッシュのようです＋衣装の候補）。
        // 何も選んでいないときの「（衣装を選んでください）」
        [UnityTest]
        public IEnumerator Window_BeforeEditing_ShowsTheOutfitListForTheWholeAvatar_AndTheBodyWarning()
        {
            var window = EditorWindow.GetWindow<ClothEditWindow>();
            window.position = new Rect(10, 10, 420, 900);
            var sceneView = EditorWindow.GetWindow<SceneView>();
            sceneView.position = new Rect(450, 10, 800, 700);
            GameObject root = Track(TestAvatarMenu.CreateInScene(null));
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material material in renderer.sharedMaterials) Track(material);
            }
            Track(root.GetComponent<Animator>().avatar);
            try
            {
                Selection.activeObject = null;
                SetField(window, "_target", null);
                typeof(ClothEditWindow).GetMethod("SetTargets", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, new object[] { new List<Renderer>() });
                yield return Pump(10);
                Shot(window, "beginner_window_nothing_selected");

                // アバター全体を選ぶ（ダイアログは出ない。窓に一覧が出る）
                Selection.activeGameObject = root;
                yield return Pump(10);
                typeof(ClothEditWindow).GetMethod("PickFromSelection", Private).Invoke(window, null);
                yield return Pump(10);
                Assert.That(window.Candidates.Count, Is.EqualTo(4));
                Shot(window, "beginner_window_whole_avatar_selected");

                // 一覧の「Body」を対象にする → 体のメッシュのようです、と衣装の候補
                window.PickCandidate(root.GetComponentsInChildren<SkinnedMeshRenderer>().First(r => r.name == TestAvatarMenu.BodyName));
                yield return Pump(10);
                Shot(window, "beginner_window_body_target_warning");

                // 一覧の衣装を押して対象にする
                window.PickCandidate(root.GetComponentsInChildren<SkinnedMeshRenderer>().First(r => r.name == TestAvatarMenu.TightsName));
                yield return Pump(10);
                Shot(window, "beginner_window_outfit_target");
            }
            finally
            {
                Selection.activeObject = null;
                window.Close();
                sceneView.Close();
            }
        }

        static void SetStretch(ClothEditController controller, float stretch)
        {
            FieldInfo field = typeof(ClothEditController).GetField("<LastStretch>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(controller, stretch);
        }

        // SendEvent の mousePosition は窓の座標で、OnSceneGUI が受ける座標（札・ブラシの座標）とは、シーンビューの上の帯の分だけずれる。
        // ずれは、実際に1つイベントを送って、OnSceneGUI が受けた位置と比べて測る（帯の高さを決め打ちしない）
        static Vector2 MeasureWindowOffset(SceneView sceneView)
        {
            var sent = new Vector2(300f, 300f);
            Vector2? seen = null;
            void Probe(SceneView view)
            {
                if (Event.current.type == EventType.MouseMove) seen = Event.current.mousePosition;
            }
            SceneView.duringSceneGui += Probe;
            try
            {
                sceneView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = sent });
            }
            finally
            {
                SceneView.duringSceneGui -= Probe;
            }
            Assert.That(seen.HasValue, Is.True, "シーンビューがイベントを受けた");
            return sent - seen.Value;
        }

        static Vector2 W(Vector2 offset, Vector2 guiPoint) => guiPoint + offset;

        static Ray MouseRay(SceneView sceneView, Vector2 guiPoint)
        {
            Camera camera = sceneView.camera;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            return camera.ScreenPointToRay(new Vector3(guiPoint.x * ppp, camera.pixelHeight - guiPoint.y * ppp, 0f));
        }
    }
}
