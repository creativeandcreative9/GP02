using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// Unity 初心者の操作（メニューから窓を開く → 衣装を選ぶ → 編集を開始 → ピン留め → つまむ → 戻す → 選んで動かす → 動作チェック →
    /// 貫通を直す → 保存 → 一覧 → 終了 → 閉じて開き直して戻す → 失敗しやすい例）を、最初から最後まで自動で再現して、各段階の画面（日本語）を撮り、記録する。
    /// 操作は、なるべく画面と同じ経路（窓のボタンのクリック・シーンビューやヒエラルキーへのマウスのイベント）で行い、
    /// それができなかったものだけ、ボタンの処理や Controller を直接呼ぶ（そのことを記録に書く）。製品のコードは変えない。
    /// 描画には画面が要り、時間もかかるので、通常のテストでは飛ばす。<c>tools/unity/walkthrough.sh</c> で動かす。
    /// 画像と記録は tools/unity/out/walkthrough/ に出る。
    /// </summary>
    public partial class BeginnerWalkthroughTests
    {
        const string TestAvatarName = "GP02_TestAvatar";

        // 視点（SceneView.LookAt に渡す値）。正面は、スカートが画面の半分以上を占める大きさ
        static readonly Vector3 FrontPivot = new Vector3(0f, 0.74f, 0f);
        const float FrontSize = 0.45f;

        // 横へ引く操作のために、引く側（アバターの右側。正面から見ると画面の左）が広く見えるように、中心を寄せた正面
        static readonly Vector3 PullPivot = new Vector3(0.12f, 0.72f, 0f);
        const float PullSize = 0.66f;

        GameObject _avatar;
        Renderer _skirt;
        readonly List<Object> _extraObjects = new List<Object>();
        Stopwatch _releaseTimer;

        [SetUp]
        public void RequireWalkthrough()
        {
            if (Environment.GetEnvironmentVariable("GP02_GUI") != "1" || Environment.GetEnvironmentVariable("GP02_WALKTHROUGH") != "1")
            {
                Assert.Ignore("初心者の通しは、GP02_GUI=1 と GP02_WALKTHROUGH=1 のときだけ動かします（tools/unity/walkthrough.sh）。");
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("画面が無い環境（-nographics）では描画できません。");
            }
            if (!Directory.Exists("/repo/tools/unity/out"))
            {
                // 設定（EditorPrefs）を空にして、テスト用のアセットを作るので、使い捨ての Docker の中だけで動かす
                Assert.Ignore("Docker の仮想画面の上でだけ動かします（EditorPrefs を消すため）。");
            }
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator Walkthrough()
        {
            var env = new EnvironmentInfo();
            var total = Stopwatch.StartNew();
            LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            Func<ClothEditLeaveReason, ClothEditUnsavedChoice> savedAsk = ClothEditPrompts.AskUnsaved;
            Func<string, string, string, string, bool> savedConfirm = ClothEditPrompts.Confirm;
            Func<string, WeightFixChoice> savedChoose = ClothEditPrompts.ChooseWeightFix;
            ClothEditPrompts.AskUnsaved = AskUnsavedRecorder;
            ClothEditPrompts.Confirm = ConfirmRecorder;
            ClothEditPrompts.ChooseWeightFix = ChooseWeightFixRecorder;
            try
            {
                yield return Setup(env);

                yield return Stage("00", "メニューから「布の編集」を開く", "Unity のメニュー Tools > GP02 > 布の編集 を選んで、ウィンドウを開く。何も選んでいない状態で、最初に何が見えるか。", S00_OpenWindow());
                yield return Stage("01", "何も選ばずに「編集を開始」を押そうとする", "何も選ばないまま、大きな「編集を開始」を押してみる。押せるのか、何が出るのか。", S01_StartWithoutTarget());
                yield return Stage("02", "テスト用のアバターを作り、スカートを選ぶ", "テスト用の Humanoid アバター（メニューで作る）のスカートを、ヒエラルキーで選ぶ。", S02_CreateAvatarAndSelectSkirt());
                yield return Stage("03", "「編集を開始」を押す", "「編集を開始」を押す。準備にどのくらいかかるか、最初に何が出るか。折りたたまれた手順の案内も開いてみる。", S03_StartEditing());
                yield return Stage("04", "布の上にマウスを乗せる", "最初に出た小さな札（3つの操作）を読んで「×」で閉じてから、スカートの上にマウスを動かしてみる。つまむ道具の円と、範囲の点が出るか。", S04_Hover());
                yield return Stage("05", "ピン留めに切り替えて、ウエストを留める", "動かしたくないウエストを留める。ピン留めの道具に切り替えると出る「服の付け根（ウエスト・首まわりなど）を自動でピン留め」を押し、後ろまで留まったかを、視点を回して確かめる。", S05_PinWaist());
                yield return Stage("06", "つまむに戻して、裾を外へ引く（途中）", "つまむに戻して、裾をつかんで外へ引く。引いている途中で何が見えるか（届かないときの線と一言も）。", S06_GrabAndPullHem());
                yield return Stage("07", "手を離して、形が落ち着く", "マウスを離す。形が落ち着くまでどのくらいかかるか。", S07_AfterRelease());
                yield return Stage("08", "1つ戻す・やり直す", "気に入らないので、窓の「1つ戻す」を押す（Ctrl+Z の代わり）。やり直しも試す。", S08_UndoRedo());
                yield return Stage("09", "選んで動かす（形だけ動かす）", "裾の前を塗って選び、ハンドルで下へ動かす。「形だけ動かす」で、布の伸びを気にせず長くする。", S09_SelectAndMove());
                yield return Stage("10", "動作チェックを始める", "座ったり足を上げたりしたときに体が服を突き抜けないか、「動作チェックを始める」で調べる。", S10_MotionCheck());
                yield return Stage("11", "悪いポーズを見る", "突き抜けたポーズを、「順に見る」でシーンビューに出して確かめる。", S11_ShowBadPose());
                yield return Stage("12", "おすすめの直し方に従う", "動作チェックの「おすすめの直し方」の、いちばん上のボタンを押して直す（浅い所なら「元のポーズで外へ逃がす」、ウェイトが原因なら「ウェイトを体に寄せる」）。", S12_FollowRecommendation());
                yield return Stage("13", "体との貫通（今の姿勢）を見て直す", "診断の「体との貫通」を見る。スカートを細くしようと内側へ押し込んで食い込ませると、どう出るか。「貫通を直す」を押す。", S13_Penetration());
                yield return Stage("14", "保存する", "編集中の見出しの行（「編集を終了」の隣）の「保存」を押す。保存したあと、一覧・手順の案内・インスペクターに何が出るか。", S14_Save());
                yield return Stage("15", "シェイプキーの一覧のウィンドウ", "メニューから「シェイプキーの一覧」を開いて、保存したものを確かめる。", S15_ShapeKeyList());
                yield return Stage("16", "「編集を終了」", "「編集を終了」を押す（保存済み）。別の流れで、保存していない変更があるときの確認も見る。", S16_EndEditing());
                yield return Stage("17", "編集中にウィンドウを閉じて、開き直す", "編集の途中でウィンドウを閉じてしまい、開き直す。「前回の編集が残っています」の帯と「戻す」。", S17_CloseAndReopen());
                yield return Stage("18", "失敗しやすい例", "アバターそのものを選ぶ（中の衣装の一覧から選び直す）、体（Body）を選ぶ、読み書きできないメッシュ、メッシュが無い、保存の名前の間違い、アバターの外の布、を試す。", S18_FailureExamples());
            }
            finally
            {
                ClothEditPrompts.AskUnsaved = savedAsk;
                ClothEditPrompts.Confirm = savedConfirm;
                ClothEditPrompts.ChooseWeightFix = savedChoose;
                Application.logMessageReceived -= OnLog;
                LogAssert.ignoreFailingMessages = false;
                total.Stop();
                WriteRecord(env, total.Elapsed.TotalMilliseconds);
                Cleanup();
            }
        }

        // ---- 始める前と終わったあと ----

        IEnumerator Setup(EnvironmentInfo env)
        {
            env.StartedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
            env.Unity = Application.unityVersion + "（Docker のコンテナ・Linux）";
            env.Graphics = "仮想画面（Xvfb）" + ScreenWidth + "x" + ScreenHeight + "・" + SystemInfo.graphicsDeviceType + "（ソフトウェア描画）。窓はヒエラルキー・インスペクター・GP02 の窓・シーンビュー・コンソールを並べた";
            env.Burst = ClothSolver.DefaultKernel != null ? "あり（" + ClothSolver.DefaultKernel.Name + "）。Burst が無いときの案内の帯は出ない" : "なし。初めて開いたときに、Burst の案内の帯が出る";
            env.Preferences = ForgetPreferences();
            env.Language = "日本語（GP02 の既定）・かんたん表示（GP02 の既定）";
            ClearStash();
            ClearOutput();
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Selection.activeObject = null;

            // 新しいプロジェクトで開いた直後に近い並び（ヒエラルキー・インスペクター・シーン・コンソール）。GP02 の窓は、メニューで開く
            _scene = EditorWindow.GetWindow<SceneView>();
            _hierarchy = OpenWindow("SceneHierarchyWindow");
            _inspector = OpenWindow("InspectorWindow");
            _console = OpenWindow("ConsoleWindow");
            ArrangeWindows();
            ClearConsole();
            yield return Pump(10);
        }

        // 初めて使う人の状態にする: GP02 の EditorPrefs（設定・表示のしかた・言語・初回の案内を出したか）を消す。
        // 使い捨ての Docker の中でだけ動かす前提（手元の設定を消さないため、SetUp で確かめている）
        string ForgetPreferences()
        {
            var keys = new List<string>
            {
                "GP02.Language", ClothEditView.PrefKey, ClothEditHelp.ShownOncePrefKey, BurstInstaller.GuideShownPrefKey,
                ClothEditCurveLibrary.DefaultKey, "GP02.ShapeKeyList.Settings", ClothEditLog.PrefKey,
            };
            foreach (PropertyInfo property in typeof(ClothEditSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)) keys.Add(ClothEditSettings.DefaultKeyPrefix + property.Name);
            foreach (FieldInfo field in typeof(ClothEditSettings).GetFields(BindingFlags.Public | BindingFlags.Instance)) keys.Add(ClothEditSettings.DefaultKeyPrefix + field.Name);
            int found = 0;
            foreach (string key in keys)
            {
                if (!EditorPrefs.HasKey(key)) continue;
                found++;
                EditorPrefs.DeleteKey(key);
            }
            L10n.Language = UiLanguage.Japanese;
            EditorPrefs.DeleteKey("GP02.Language");
            ClothEditView.Reload();
            // コンソールの記録は、利用者と同じく出さない（既定のまま）。画面の見え方を、初めて使う人と同じにするため
            ClothEditLog.Enabled = false;
            return found == 0 ? "GP02 の EditorPrefs は1つも残っていなかった（空の状態から）。取っておきの編集も空にした" : "GP02 の EditorPrefs " + found + " 件を消した。取っておきの編集も空にした";
        }

        // 前の実行の画像と記録を消す（段階が飛んだときに、古い画像が残らないように）
        static void ClearOutput()
        {
            if (!Directory.Exists(OutFolder)) return;
            foreach (string file in Directory.GetFiles(OutFolder))
            {
                string name = Path.GetFileName(file);
                if (Path.GetExtension(file) == ".png" || name == "walkthrough.md" || name == "progress.log") File.Delete(file);
            }
        }

        static void ClearStash()
        {
            try
            {
                if (Directory.Exists(ClothEditStash.Folder)) Directory.Delete(ClothEditStash.Folder, true);
            }
            catch (Exception ex)
            {
                Progress("取っておきを消せませんでした: " + ex.Message);
            }
        }

        static void ClearConsole()
        {
            Type entries = EditorType("LogEntries");
            MethodInfo clear = entries != null ? entries.GetMethod("Clear", Any) : null;
            if (clear != null) clear.Invoke(null, null);
        }

        void WriteRecord(EnvironmentInfo env, double totalMs)
        {
            try
            {
                Directory.CreateDirectory(OutFolder);
                env.Notes.Add("画像は " + OutFolder.Replace("/repo/", "") + "/ にあります。この記録（walkthrough.md）と同じ場所です");
                env.Notes.Add("コマの進み: Docker の1コマは、窓を5つソフトウェア描画するので数百ミリ秒かかる（利用者の PC は十数ミリ秒）。そのままだと、つまんでいる間・仕上げの間に進む布の計算が PC よりずっと少なくなるので、"
                    + "その間だけ、コマの合間に Controller.Tick（設定の SolveMilliseconds ずつ）を足して、PC に近い量にした（足した回数 " + _extraTicks + " 回。設定の値は変えていない）。待ち時間は、この補いを含めた Docker での値");
                env.Notes.Add("コンソールの赤い「Cancelling DisplayDialog…」の行は、バッチモードの Unity が、実機なら OK を押すまで止まるダイアログの代わりに出すもの。その文は、各段階の「確認のダイアログの文」に書いた");
                env.Notes.Add("視点の変更（SceneView.LookAt）・ポップアップや文字欄の値の変更・窓を閉じる操作は、実際は Alt+ドラッグ・ポップアップのメニュー・タブの右クリックで行う。メニューを開いて選ぶ操作は、同じ経路で送れないので、直接の呼び出しで代えた（操作の欄に印を付けた）");
                File.WriteAllText(Path.Combine(OutFolder, "walkthrough.md"), BuildMarkdown(_stages, env, totalMs, _waitTotalMs), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Progress("記録を書けませんでした: " + ex);
            }
        }

        // 作ったものを片づける（Assets/GP02 の下のテスト用のアセットを含む）
        void Cleanup()
        {
            try
            {
                if (C != null && C.IsEditing) C.End();
                foreach (Type type in new[] { typeof(ShapeKeyListWindow) })
                {
                    foreach (Object window in Resources.FindObjectsOfTypeAll(type))
                    {
                        if (window is EditorWindow editorWindow) editorWindow.Close();
                    }
                }
                if (_cloth != null) _cloth.Close();
                foreach (EditorWindow window in new[] { _scene, _hierarchy, _inspector, _console })
                {
                    if (window != null) window.Close();
                }
                foreach (Object extra in _extraObjects)
                {
                    if (extra != null) Object.DestroyImmediate(extra);
                }
                if (_avatar != null) Object.DestroyImmediate(_avatar);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset("Assets/GP02");
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Progress("後片付けで問題が起きました: " + ex);
            }
        }

        // ---- 00 ----

        IEnumerator S00_OpenWindow()
        {
            bool ok = EditorApplication.ExecuteMenuItem("Tools/GP02/布の編集");
            Op(Via.Menu, "メニュー「Tools > GP02 > 布の編集」を選んだ（結果: " + ok + "）");
            _cloth = EditorWindow.GetWindow<ClothEditWindow>();
            ArrangeWindows();
            Fact("窓のタイトル: 「" + _cloth.titleContent.text + "」。窓の最小の大きさ: " + _cloth.minSize.x + "x" + _cloth.minSize.y);
            Fact("編集中か: " + C.IsEditing + "。前回の編集の帯: " + _cloth.HasStashBand);
            yield return Snap("00_open_window", "メニューから開いた直後。何も選んでいない（ヒエラルキーは新しいシーン「Untitled」だけ）", 10, _hierarchy);
        }

        // ---- 01 ----

        IEnumerator S01_StartWithoutTarget()
        {
            Fact("「編集を開始」ボタンの状態: " + ButtonState(_cloth, "編集を開始"));
            if (ClickText(_cloth, "編集を開始", out string how)) Op(Via.Real, "窓のボタン" + how);
            else Op(Via.Real, "押そうとしたが、" + how);
            yield return Pump(3);
            Fact("押したあとに編集が始まったか: " + C.IsEditing + "。窓に出ている「編集できない理由」: " + (string.IsNullOrEmpty(C.LastError) ? "なし" : C.LastError));

            // 隣の「選択中のオブジェクトから設定」も押してみる（何も選んでいない）
            if (ClickText(_cloth, "選択中のオブジェクトから設定", out how)) Op(Via.Real, "窓のボタン" + how + "（何も選んでいない状態）");
            yield return Pump(3);
            yield return Snap("01_start_without_target", "何も選ばずに「編集を開始」を押そうとした（ボタンは灰色）", 6, _hierarchy);
        }

        // ---- 02 ----

        IEnumerator S02_CreateAvatarAndSelectSkirt()
        {
            bool ok = EditorApplication.ExecuteMenuItem(TestAvatarMenu.MenuPath);
            Op(Via.Menu, "メニュー「Tools > GP02 > テスト用のアバター（Humanoid）を作る」を選んだ（結果: " + ok + "）。このメニューは、作ったあとスカートを選んだ状態にする");
            _avatar = GameObject.Find(TestAvatarName);
            Assert.That(_avatar, Is.Not.Null, "テスト用のアバターができなかった");
            Transform skirt = _avatar.transform.Find(TestAvatarMenu.SkirtName);
            Assert.That(skirt, Is.Not.Null);
            _skirt = skirt.GetComponent<SkinnedMeshRenderer>();
            yield return Pump(6);

            // メニューが選んでくれた状態を一度外して、自分でヒエラルキーから選ぶ流れにする
            Selection.activeObject = null;
            Op(Via.Note, "メニューが選んでくれたスカートの選択を外した（ヒエラルキーの空きをクリックするのと同じ）。以降は、利用者が自分で衣装の行を選ぶ流れ");
            yield return Pump(4);
            yield return ViewTo("アバター全体が見える正面", new Vector3(0f, 0.78f, 0f), 6f, 180f, 1.1f);

            // ヒエラルキーで「Skirt」の行を実際にクリックする
            yield return SelectInHierarchy(TestAvatarMenu.SkirtName, skirt.gameObject, "");
            List<Renderer> targets = _cloth.Targets;
            Fact("選んだもの: " + (Selection.activeGameObject != null ? Selection.activeGameObject.name : "なし") + "。窓の「対象」: " + (targets.Count > 0 ? targets[0].name : "なし"));
            yield return Snap("02_avatar_created_skirt_selected", "テスト用のアバターを作って、スカートを選んだ。窓の「対象」にスカートが自動で入り、手順1が済んで、手順2「編集を開始」が今やること", 6, _hierarchy);
        }

        // ---- 03 ----

        IEnumerator S03_StartEditing()
        {
            yield return ViewTo("スカートが大きく見える正面", FrontPivot, 8f, 180f, FrontSize);
            bool started = false;
            bool clicked = ClickText(_cloth, "編集を開始", out string how);
            double ms = _lastClickMs;
            if (clicked)
            {
                started = C.IsEditing;
                if (started) Op(Via.Real, "窓のボタン" + how);
            }
            if (!started)
            {
                Op(Via.Real, "窓のボタン" + how + (clicked ? " → 編集は始まらなかった" : ""));
                var timer = Stopwatch.StartNew();
                Invoke(_cloth, "StartEdit");
                ms = timer.Elapsed.TotalMilliseconds;
                Op(Via.Button, "ボタンの処理 ClothEditWindow.StartEdit() を直接呼んだ");
            }
            Waited("「編集を開始」を押してから編集できるようになるまで（窓が止まって待つ）", ms);
            Assert.That(C.IsEditing, Is.True, "編集を始められなかった: " + C.LastError);
            ClothEditSession session = C.Session;
            RememberSkirtRest();
            Fact("粒子 " + session.ParticleCount.ToString("N0") + " 個、頂点 " + session.VertexCount.ToString("N0") + "、準備 " + session.PrepareMilliseconds.ToString("F0") + " ms、高速化: " + ClothEditSession.DescribeAccelerationShort(session.Solver.Kernel));
            Fact("ショートカットの全部の一覧を自動で出したか（初めての編集）: " + _cloth.OverlayShortcutsVisible + "。小さな札（3つの操作と×）を出したか: " + _cloth.StartCardVisible
                + "。Unity の道具のハンドルを隠したか（Tools.hidden）: " + Tools.hidden + "。手順の案内の区切りは閉じている: " + !(bool)GetField(_cloth, "_foldGuide"));
            yield return Snap("03a_start_editing", "「編集を開始」を押した直後。シーンビューの左上に1行の案内と、初めてなので小さな札（3つの操作と×）。Unity の移動の矢印は隠れる。窓には道具・ブラシ・対称・動作チェック・保存などが現れ、手順は畳まれていても「次: …」の1行が出る", 10, _hierarchy);

            // 折りたたまれた「手順（今どこまで済んだか）」を開いて読んでみる
            yield return ToggleGuide(true);
            yield return Snap("03b_guide_opened", "折りたたまれている「手順（今どこまで済んだか）」を開いたところ。5つの手順のうち、今やることに印が付き、説明の枠が出る", 4);
            yield return ToggleGuide(false);
        }

        // ---- 04 ----

        IEnumerator S04_Hover()
        {
            Vector3 probe = SkirtPoint(0f, 0.74f);
            bool calibrated = CalibrateScene(probe);
            Fact("シーンビューのマウスの位置の補正: " + (calibrated ? "できた（x " + _sceneCal.x.ToString("F1") + "、y " + _sceneCal.y.ToString("F1") + "）" : "できなかった"));

            // 初めての編集で出た小さな札（3つの操作）を読んだので、札の右上の「×」で閉じる
            if (_cloth.StartCardVisible) yield return CloseStartCard();
            // 全部の一覧が出ていたら（前の版の動き）、窓の右上の「?」で閉じる
            if (_cloth.OverlayShortcutsVisible)
            {
                bool clicked = ClickText(_cloth, "?", out string how);
                yield return Pump(3);
                if (clicked && !_cloth.OverlayShortcutsVisible)
                {
                    Op(Via.Real, "ショートカット一覧がシーンを覆って見づらいので、窓の右上の" + how);
                }
                else
                {
                    Op(Via.Real, "窓の右上の" + how + " → 一覧は閉じなかった");
                    _cloth.OverlaySetShortcutsVisible(false);
                    Op(Via.Button, "一覧を隠す処理 ClothEditWindow.OverlaySetShortcutsVisible(false) を直接呼んだ");
                    yield return Pump(2);
                }
            }
            Vector3 target = SurfacePoint(35f, 0.56f);
            yield return HoverAt(target, 4);
            Progress("S04 hover target=" + target + " event=" + ScenePoint(target) + " hit=" + C.HasHover);
            if (!C.HasHover)
            {
                // 狙った所で円が出なかったときは、少しずらして動かし直す（どこで外れたかは、progress.log に書く）
                foreach ((float degrees, float y) in new[] { (30f, 0.6f), (20f, 0.58f), (40f, 0.62f), (10f, 0.6f) })
                {
                    target = SurfacePoint(degrees, y);
                    yield return HoverAt(target, 3);
                    Progress("S04 hover retry target=" + target + " event=" + ScenePoint(target) + " hit=" + C.HasHover);
                    if (!C.HasHover) continue;
                    Op(Via.Note, "最初に狙った所では布に当たらなかったので、少しずらして動かし直した");
                    break;
                }
            }
            Op(Via.Real, "シーンビューで、スカートの裾に近い所へマウスを動かした（MouseMove）。道具は「つまむ」のまま");
            float error = Vector3.Distance(C.HoverPoint, target);
            Fact("マウスの下の布: " + (C.HasHover ? "当たった（狙った点との差 " + (error * 100f).ToString("F1") + " cm）" : "当たらなかった") + "、範囲の点 " + C.PreviewCount + " 個、ブラシ半径 " + (C.BrushRadius * 100f).ToString("F0") + " cm");
            yield return Snap("04_hover_grab_tool", "小さな札を「×」で閉じて、スカートの上にマウスを乗せた。つまむ道具の円（水色）と、範囲に入る点（重みで色分け）", 4);
        }

        // シーンビューの小さな札の「×」をクリックして閉じる。閉じなければ、閉じる処理を直接呼ぶ
        IEnumerator CloseStartCard()
        {
            Rect close = (Rect)GetField(_cloth, "_startCardCloseRect");
            bool clicked = false;
            if (close.width > 0f && SceneCalibrated)
            {
                Vector2 point = SceneGuiToEvent(close.center);
                SceneMouse(EventType.MouseMove, point);
                SceneMouse(EventType.MouseDown, point);
                SceneMouse(EventType.MouseUp, point);
                clicked = true;
                yield return Pump(3);
            }
            if (clicked && !_cloth.StartCardVisible)
            {
                Op(Via.Real, "シーンビューの左上の小さな札（ドラッグ: 布をつまむ・Alt+ドラッグ: 視点を回す・Ctrl+Z: 1つ戻す）を読んで、札の右上の「×」をクリックして閉じた");
                Fact("札を閉じたあと、布はつままれていない: " + !(C.IsDragging || C.IsSettling) + "、保存していない編集: " + C.HasUnsavedEdits);
                yield break;
            }
            Op(Via.Real, clicked ? "札の右上の「×」をクリックした → 閉じなかった" : "札の「×」の位置が分からなかった");
            _cloth.DismissStartCard();
            Op(Via.Button, "札を閉じる処理 ClothEditWindow.DismissStartCard() を直接呼んだ");
            yield return Pump(2);
        }

        // ---- 05 ----

        const string AutoPinLabel = "服の付け根（ウエスト・首まわりなど）を自動でピン留め";

        IEnumerator S05_PinWaist()
        {
            yield return SelectToolByButton(ClothEditMode.Pin);
            yield return HoverAt(SurfacePoint(-35f, 0.935f), 3);
            Fact("「" + AutoPinLabel + "」のボタンの状態: " + ButtonState(_cloth, AutoPinLabel));
            yield return Snap("05a_pin_tool_selected", "ピン留めの道具に切り替えた（窓の「ピン留め (P)」を押した）。マウスの円の色が変わり、窓の道具の欄に「" + AutoPinLabel + "」のボタンが出る", 4);

            // 塗る代わりに、窓のボタンで、付け根（ウエスト）を一周まとめて留める
            int before = C.Session.PinnedCount;
            yield return ClickOrCall(AutoPinLabel, () => C.Session.PinnedCount > before, () => _cloth.AutoPinAnchors(), "ClothEditWindow.AutoPinAnchors()");
            CountPins(C.Session, out int frontPins, out int backPins);
            Fact("自動でピン留めしたあとのピンの数: " + C.Session.PinnedCount + "（うち、スカートの前半分 " + frontPins + "、後ろ半分 " + backPins + "）");
            yield return Snap("05b_pinned_front_waist", "「" + AutoPinLabel + "」を押した。ウエストが一周、青い点でピン留めされる。窓の真ん中に短い通知、窓の上に結果の一言。手順3が済む", 6);

            // 後ろまで留まったかを、視点を後ろへ回して確かめる
            yield return ViewTo("後ろから見る（ウエストの後ろ側も留まったか確かめる）", new Vector3(0f, 0.72f, 0f), 8f, 0f, FrontSize);
            yield return Snap("05c_pinned_back_waist", "後ろから見た。塗らなくても、ウエストの後ろ側まで一周ピン留めされている", 6);
        }

        // ---- 06 ----

        IEnumerator S06_GrabAndPullHem()
        {
            yield return ViewTo("引く側（アバターの右。画面の左）が広く見える正面", PullPivot, 8f, 180f, PullSize);
            yield return SelectToolByButton(ClothEditMode.Grab);
            Vector3 start = SurfacePoint(35f, 0.535f);
            Vector3 pull = start + new Vector3(0.10f, 0f, 0f);
            yield return HoverAt(start, 3);
            // 裾を、外（アバターの右。画面では左）へ 10cm ゆっくり引く。1回動かすごとに2コマ進める（布の計算は、コマごとに進む）
            yield return DragPath(Line(start, pull, 10), 2, release: false);
            Op(Via.Real, "つまむ道具で、裾（アバターの右前）にマウスを乗せて押し、外（アバターの右。正面から見ると画面の左）へ 10cm ドラッグした。マウスはまだ押したまま");
            yield return Pump(10);
            Fact("引いている途中: つまんでいる " + C.IsDragging + "、届かない " + C.IsOutOfReach + "（つまんだ点と引いた先の差 " + (C.ReachDistance * 100f).ToString("F1") + " cm）、最大の伸び " + (C.LastStretch * 100f).ToString("F1") + "%、粒子が動いた最大 " + MaxMovedCm(C.Session).ToString("F1") + " cm");
            yield return SnapKeep("06a_grab_hem_dragging", "裾を外へ 10cm 引いている途中（マウスは押したまま）。つまんだ点のまわりが付いてきている。左上に「つまんでいます」の案内", 4);

            // 引きすぎ: 布の長さで届かない所まで引く
            Vector3 far = start + new Vector3(0.30f, 0f, 0f);
            yield return ContinueDrag(Line(pull, far, 10).GetRange(1, 10), 2);
            Op(Via.Real, "そのまま、布の長さで届かない所（30cm）までドラッグを続けた");
            yield return Pump(15);
            Fact("引きすぎの途中: 届かない " + C.IsOutOfReach + "（つまんだ点と引いた先の差 " + (C.ReachDistance * 100f).ToString("F1") + " cm）、最大の伸び " + (C.LastStretch * 100f).ToString("F1") + "%、粒子が動いた最大 " + MaxMovedCm(C.Session).ToString("F1") + " cm");
            yield return SnapKeep("06b_grab_hem_too_far", "布の長さで届かない所まで引いた。届かないとき、線と一言（「布の長さが足りません…」）が出るか", 4);

            // 少し戻して離す
            Vector3 back = start + new Vector3(0.14f, 0f, 0f);
            yield return ContinueDrag(Line(far, back, 8).GetRange(1, 8), 2);
            _releaseTimer = Stopwatch.StartNew();
            yield return ReleaseAt(back, 0);
            Op(Via.Real, "14cm の所まで戻して、マウスを離した（MouseUp）");
        }

        // ---- 07 ----

        IEnumerator S07_AfterRelease()
        {
            Fact("離した直後の状態: " + C.StateLabel + "（仕上げ中: " + C.IsSettling + "）");
            // 仕上げの途中を撮るため、この1枚の間だけ、足した計算は止める（足すと、1コマの間に仕上げが終わってしまう）
            _holdSolver = true;
            yield return Snap("07a_after_release_settling", "マウスを離した直後（形が落ち着く途中）。左上は「仕上げ中」の案内", 1);
            _holdSolver = false;
            yield return WaitUntilIdle();
            double ms = _releaseTimer != null ? _releaseTimer.Elapsed.TotalMilliseconds : _lastSettleMs;
            Waited("マウスを離してから形が落ち着くまで", ms);
            Fact("落ち着いたあと: 最大の伸び " + (C.LastStretch * 100f).ToString("F1") + "%、粒子が元の位置から動いた最大 " + MaxMovedCm(C.Session).ToString("F1") + " cm、状態「" + C.StateLabel + "」、編集済み: " + C.Session.IsShapeEdited());
            yield return Snap("07b_settled", "形が落ち着いたあと。裾が外へ広がった形になっている", 6);
        }

        // ---- 08 ----

        IEnumerator S08_UndoRedo()
        {
            bool edited = C.Session.IsShapeEdited();
            yield return ClickOrCall("1つ戻す", () => C.Session != null && !C.Session.IsShapeEdited(), () => C.UndoLast(), "ClothEditController.UndoLast()");
            yield return WaitUntilIdle(120);
            Fact("戻す前に編集済みだったか: " + edited + "。戻したあとの編集済み: " + C.Session.IsShapeEdited() + "、ピンの数 " + C.Session.PinnedCount + "（ピンは残る）、粒子が動いた最大 " + MaxMovedCm(C.Session).ToString("F1") + " cm");
            yield return Snap("08a_after_undo", "窓の「1つ戻す」を押したあと。引いた形が元に戻った（ピンは残る）", 6);

            yield return ClickOrCall("やり直す", () => C.Session != null && C.Session.IsShapeEdited(), () => C.RedoLast(), "ClothEditController.RedoLast()");
            yield return WaitUntilIdle(120);
            Fact("やり直したあとの編集済み: " + C.Session.IsShapeEdited() + "、粒子が動いた最大 " + MaxMovedCm(C.Session).ToString("F1") + " cm");
            if (!C.Session.IsShapeEdited())
            {
                // やり直しで戻らなかったので、この先の段階（編集した形が要る）のために、もう一度引く
                yield return RepullHem();
                Op(Via.Real, "やり直しで形が戻らなかったので、もう一度、裾を引いた（この先の段階のため）");
            }
            yield return Snap("08b_after_redo", "窓の「やり直す」を押したあと。引いた形が戻った", 6);
        }

        // 裾を引き直す（速く）
        IEnumerator RepullHem()
        {
            yield return SelectToolByButton(ClothEditMode.Grab);
            Vector3 start = SurfacePoint(35f, 0.535f);
            yield return DragPath(Line(start, start + new Vector3(0.12f, 0f, 0f), 8), 1);
            yield return WaitUntilIdle(300);
        }

        // ---- 09 ----

        IEnumerator S09_SelectAndMove()
        {
            yield return SelectToolByButton(ClothEditMode.Select);
            // 裾の前（中央）の帯を塗って選ぶ。形が変わっているので、いまの布の表面を狙う。選べなければ、高さを変えて塗り直す
            foreach (float y in new[] { 0.56f, 0.585f, 0.54f })
            {
                yield return DragPath(Arc(-25f, 25f, y, 14), 1);
                Op(Via.Real, "「選んで動かす」の道具で、裾の前の帯（高さ " + (y * 100f).ToString("F0") + " cm 付近）を、左から右へドラッグして塗って選んだ");
                if (C.SelectionCount > 0) break;
                Op(Via.Note, "選べなかったので、少し高さを変えて塗り直した");
            }
            Fact("選んだ点: " + C.SelectionCount + " 個。ハンドルの種類: " + C.Settings.HandleType + "。動かし方: " + (C.Settings.SelectionMovesAsCloth ? "布として動かす（既定）" : "形だけ動かす"));
            yield return Snap("09a_selected_hem_front", "裾の前を塗って選んだ。橙の点が選んだ点、ハンドルが出る。窓には「選んだ点」の区切り（動かし方は既定の「布として動かす」）", 6);

            // 動かし方を「形だけ動かす」にする（窓のポップアップの代わりに、設定の値を変える）
            C.Settings.SelectionMovesAsCloth = false;
            C.Settings.Save();
            C.ApplySettings();
            Op(Via.Setting, "窓の「動かし方」のポップアップの代わりに、設定の値（SelectionMovesAsCloth）を false（形だけ動かす）にした");
            yield return Pump(4);

            // 緑の矢印（上向き）をつかんで、下へドラッグする（裾を長くする）
            Vector3 before = C.SelectionPivot;
            yield return DragHandle(Vector3.up, -0.12f);
            Vector3 after = C.SelectionPivot;
            yield return WaitUntilIdle(300);
            bool moved = (after - before).magnitude > 0.01f;
            Fact("ハンドルをドラッグした結果: 選んだ点の中心が " + ((after - before) * 100f).ToString("F1") + " cm 動いた（" + (moved ? "動いた" : "動かなかった") + "）");
            if (!moved)
            {
                C.ApplySelectionTransform(new Vector3(0f, -0.08f, 0f), Quaternion.identity, Vector3.one);
                Op(Via.Controller, "ハンドルのドラッグでは動かなかったので、Controller の ApplySelectionTransform（下へ 8cm）を直接呼んだ");
                yield return WaitUntilIdle(300);
            }
            yield return Snap("09b_after_handle_move", "ハンドルの緑の矢印を下へドラッグして、裾の前を長くした（形だけ動かす）", 6);

            // 動かし終えたので、選んでいた点を解除する（Esc）
            SceneKey(KeyCode.Escape);
            yield return Pump(2);
            if (C.SelectionCount == 0)
            {
                Op(Via.Real, "動かし終えたので、シーンビューで Esc キーを押して、選んでいた点を解除した（KeyDown）");
            }
            else
            {
                C.ClearSelection();
                Op(Via.Controller, "Esc キーでは選択が解除されなかったので、Controller の ClearSelection を直接呼んだ");
            }
        }

        // 選んだ点のハンドルの、axis の向きの矢印をつかんで、distance（メートル、axis の向きがプラス）だけドラッグする
        IEnumerator DragHandle(Vector3 axis, float distance)
        {
            Vector3 pivot = C.SelectionPivot;
            Camera camera = _scene.camera;
            axis.Normalize();
            Vector3 s0 = camera.WorldToScreenPoint(pivot);
            Vector3 s1 = camera.WorldToScreenPoint(pivot + axis * 0.1f);
            float pixelsPerMeter = new Vector2(s1.x - s0.x, s1.y - s0.y).magnitude / 0.1f;
            float grabMeters = 45f / Mathf.Max(1f, pixelsPerMeter);   // 矢印の軸の、中心から 45 ピクセルの所をつかむ
            Vector3 grab = pivot + axis * grabMeters;
            Vector3 end = pivot + axis * (grabMeters + distance);

            SceneMouse(EventType.MouseMove, ScenePoint(grab));
            yield return Pump(3);
            bool over = (bool)Invoke(_cloth, "IsMouseOverHandle");
            Fact("ハンドルの矢印にマウスを乗せたか（ハンドルが優先される状態か）: " + over);
            SceneMouse(EventType.MouseDown, ScenePoint(grab));
            const int steps = 8;
            for (int i = 1; i <= steps; i++)
            {
                SceneMouse(EventType.MouseDrag, ScenePoint(Vector3.Lerp(grab, end, (float)i / steps)));
                yield return Pump(1);
            }
            SceneMouse(EventType.MouseUp, ScenePoint(end));
            yield return Pump(3);
            string arrow = axis == Vector3.up ? "緑・上向き" : axis == Vector3.forward ? "青・アバターの前向き" : "矢印";
            Op(Via.Real, "シーンビューで、ハンドルの矢印（" + arrow + "）にマウスを乗せて押し、" + (distance * 100f).ToString("F0") + " cm の分だけドラッグして離した");
        }

        // ---- 10 ----

        IEnumerator S10_MotionCheck()
        {
            yield return ViewTo("全体が見える正面", FrontPivot, 8f, 180f, FrontSize + 0.1f);
            yield return Snap("10a_motion_check_before", "動作チェックの区切り（始める前）。かんたん表示では「動作チェックを始める」のボタンだけ", 4);

            var timer = Stopwatch.StartNew();
            bool started = false;
            bool clicked = ClickText(_cloth, "動作チェックを始める", out string how);
            if (clicked) started = C.IsPoseCheckRunning || C.LastPoseCheck != null;
            if (clicked && started) Op(Via.Real, "窓のボタン" + how);
            else
            {
                Op(Via.Real, "窓のボタン" + how + (clicked ? " → 動作チェックは始まらなかった" : ""));
                bool ok = C.BeginPoseCheck(out string error);
                Op(Via.Controller, "Controller の BeginPoseCheck を直接呼んだ（" + (ok ? "始まった" : "始められない: " + error) + "）");
            }
            double beginMs = timer.Elapsed.TotalMilliseconds;
            Fact("ボタンを押してから、画面が戻るまで（準備）: " + beginMs.ToString("F0") + " ms");
            // 進み具合を見せながら、結果が出るまでコマを進める。途中で1枚撮る
            bool shotTaken = false;
            int frames = 0;
            while (C.IsPoseCheckRunning && frames < 2000)
            {
                InternalEditorUtility.RepaintAllViews();
                yield return null;
                frames++;
                if (!shotTaken && frames >= 6)
                {
                    C.GetPoseCheckCounts(out int done, out int totalSteps);
                    Fact("途中経過: " + done + " / " + totalSteps);
                    shotTaken = true;
                    yield return Snap("10b_motion_check_running", "動作チェックの途中（進み具合のバー「ポーズを試しています… n / m」）", 1);
                }
            }
            double ms = timer.Elapsed.TotalMilliseconds;
            Waited("動作チェックを始めてから結果が出るまで", ms);
            PoseCheckResult result = C.LastPoseCheck;
            Assert.That(result, Is.Not.Null, "動作チェックの結果が出なかった: " + C.LastPoseCheckError);
            Fact("結果: " + result.DescribeSummary() + "。ポーズ " + result.PoseCount + " × " + result.StageCount + " 段階、突き抜けたポーズ " + result.PenetratingPoseCount + "、体: " + result.BodyDescription);
            yield return Snap("10c_motion_check_result", "動作チェックの結果。「よく使うポーズ」と「大きく動くポーズ」の2行、悪い順の上位、「順に見る」などのボタンと「おすすめの直し方」。シーンの布には、よく使うポーズで突き抜ける所だけ色の点が出る", 6);
        }

        // ---- 11 ----

        IEnumerator S11_ShowBadPose()
        {
            string state = ButtonState(_cloth, "順に見る");
            Fact("「順に見る」ボタンの状態: " + state);
            yield return ClickOrCall("順に見る", () => C.IsPosePreviewActive, () => C.ShowNextPenetratingPose(), "ClothEditController.ShowNextPenetratingPose()");
            PoseCheckEntry shown = C.PosePreview;
            Fact("見せているポーズ: " + (shown != null ? shown.Name + "（" + shown.Describe() + "）" : "なし") + "。元のアバターは隠れ、複製がそのポーズになる");

            // 突き抜けた所（赤い点）の真ん中を見る
            Vector3 pivot = new Vector3(0f, 0.6f, 0.15f);
            if (shown != null && shown.Result != null && shown.Result.Points.Count > 0)
            {
                var sum = Vector3.zero;
                foreach (var p in shown.Result.Points) sum += new Vector3(p.X, p.Y, p.Z);
                pivot = sum / shown.Result.Points.Count;
            }
            yield return ViewTo("突き抜けた所が見える斜め前（赤い点の真ん中を中心に）", pivot, 12f, 235f, 0.8f);
            yield return Snap("11_bad_pose_shown", "悪いポーズをシーンビューに出した。赤い点が突き抜けた所。左上に「ポーズを表示中…クリックか Esc で元の姿勢に戻ります」", 4);
        }

        // ---- 12 ----

        IEnumerator S12_FollowRecommendation()
        {
            PoseCheckResult check = C.LastPoseCheck;
            Fact("「おすすめの直し方」の元: 外へ逃がせる浅い所 " + (check != null && check.EscapePlan != null ? check.EscapePlan.PushedRegions : 0) + " か所、ウェイトが原因の所 " + C.WeightCausedRegionCount
                + " か所、体どうしの重なり " + (check != null ? check.FixOverlapRegionCount : 0) + " か所（直す範囲: " + (C.Settings.FixLargePoses ? "すべてのポーズ" : "よく使うポーズ") + "）");
            string escapeState = ButtonState(_cloth, "元のポーズで外へ逃がす");
            string weightState = ButtonState(_cloth, "ウェイトを体に寄せる");
            Fact("おすすめのボタン: 「元のポーズで外へ逃がす」 " + escapeState + "、「ウェイトを体に寄せる」 " + weightState);
            if (escapeState != "有効" && weightState == "有効")
            {
                yield return FollowWeightFix();
                yield break;
            }
            Fact("「元のポーズで外へ逃がす」ボタンの状態: " + escapeState + "。逃がせるか（CanEscapePoseCheck）: " + C.CanEscapePoseCheck);
            if (C.IsPosePreviewActive)
            {
                // ポーズを見せたまま押しても、自分で元の姿勢に戻る
                Op(Via.Note, "ポーズを見せたまま「元のポーズで外へ逃がす」を押す（元の姿勢に戻す操作は、先にしていない）");
            }
            // 押した直後の通知を撮るので、視点は押す前に正面へ戻しておく
            yield return ViewTo("全体が見える正面", FrontPivot, 8f, 180f, FrontSize + 0.1f);
            yield return ClickOrCall("元のポーズで外へ逃がす", () => C.LastPoseEscape != null, () => Invoke(_cloth, "EscapePoseCheckFromWindow"), "ClothEditWindow.EscapePoseCheckFromWindow()", 1);
            Waited("「元のポーズで外へ逃がす」を押してから終わるまで（逃がして、もう一度調べ直す）", _lastClickMs);
            PoseEscapeReport report = C.LastPoseEscape;
            Fact("結果: " + (report == null ? "なし" : report.Reverted ? "効かなかったので元に戻した" : report.Rechecked ? "逃がした所の深さ（平均） " + (report.PushedDepthBefore * 1000f).ToString("F1") + " → " + (report.PushedDepthAfter * 1000f).ToString("F1") + " mm、貫通したポーズ " + report.PosesBefore + " → " + report.PosesAfter + "、か所 " + report.RegionsBefore + " → " + report.RegionsAfter : "逃がした（調べ直せなかった）"));
            yield return Snap("12_escape_outward", "「元のポーズで外へ逃がす」を押したあと。窓の真ん中に短い通知、窓には前後の件数の一言", 1);

            // 外へ逃がしても、ウェイトが原因の所は残る。おすすめの直し方に「ウェイトを体に寄せる」がまだあるので、続けて押す
            // （このスカートでは、外へ逃がせる浅い所が1か所だけあり、そこを逃がしたあとも、ウェイトが原因の所は残る）
            if (ButtonState(_cloth, "ウェイトを体に寄せる") == "有効")
            {
                Op(Via.Note, "外へ逃がしたあとも、おすすめの直し方に「ウェイトを体に寄せる」が残っているので、続けて押す");
                yield return FollowWeightFix("12b_weight_fit");
            }
        }

        // おすすめの「ウェイトを体に寄せる」を押す。候補でポーズを試した前後を見せて、適用するかを聞かれる（ここでは「適用」）。
        // 体から離れた布が伸びすぎる候補（スカートに強さ 100% で当てたとき）は、先に「弱めて（50%）試す」「やめる」「このまま適用」を聞かれる。
        // 初心者は案内どおり「弱めて（50%）試す」を選び、50% で試し直した前後を見て、適用する。
        // 適用すると編集を作り直すが、保存していない編集は、その形のまま引き継ぐので、保存の確認は出ない（適用の前後の「保存していない編集」と、
        // 動作チェックの結果が古くならないことを、Fact に残す）
        IEnumerator FollowWeightFix(string snapName = "12_escape_outward")
        {
            yield return ViewTo("全体が見える正面", FrontPivot, 8f, 180f, FrontSize + 0.1f);
            bool unsaved = C.HasUnsavedEdits;
            Fact("適用の前: 保存していない編集 " + unsaved + "、動作チェックの結果が古いか " + C.IsPoseCheckStale + "、保存したシェイプキー " + SavedKeyCount() + " 個");
            _weightFixChoiceAsked = false;
            WeightFixReport before = C.LastWeightFix;
            yield return ClickOrCall("ウェイトを体に寄せる", () => C.LastWeightFix != before, () => Invoke(_cloth, "PreviewWeightFixFromWindow", true), "ClothEditWindow.PreviewWeightFixFromWindow(true)", 2);
            Waited("「ウェイトを体に寄せる」を押してから終わるまで（候補でポーズを試し、伸びすぎるときは弱めて試し直し、確認のあと適用して、編集を作り直す）", _lastClickMs);
            WeightFixReport report = C.LastWeightFix;
            bool applied = report != null && report != before;
            if (_weightFixChoiceAsked)
            {
                string tryWeaker = ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.TryWeaker);
                Op(Via.Note, "体から離れた布が伸びすぎる候補だったので、確認に「" + tryWeaker + "」「" + ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.Cancel) + "」「" + ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.ApplyAsIs)
                    + "」の3つが出て、案内どおり「" + tryWeaker + "」を選んだ（かんたん表示のまま、強さを 50% にして試し直す）"
                    + (applied ? "。続けて、50% で試した結果の「適用」の確認が出て、「適用」を選んだ" : "。そのあと、適用はされなかった"));
            }
            int unsavedAsked = _r.Dialogs.FindAll(d => d.Contains("保存していない編集があります")).Count;
            Op(Via.Note, unsaved
                ? "保存していない編集があったが、保存の確認は" + (unsavedAsked == 0 ? "出なかった（形は、作り直すときに引き継がれる）" : "出た（" + unsavedAsked + " 回）")
                : "保存していない編集は無かった");
            Fact("適用のあと: 保存していない編集 " + C.HasUnsavedEdits + "（引き継いだか " + (report != null && report.CarriedUnsaved ? "はい" : "いいえ") + "、捨てたか " + (report != null && report.DiscardedUnsaved ? "はい" : "いいえ")
                + "）、動作チェックの結果が古いか " + C.IsPoseCheckStale + "、保存したシェイプキー " + SavedKeyCount() + " 個");
            Fact("結果: " + (!applied ? "適用されなかった（" + C.LastWeightFixError + "）" : report.ToString()) + "。編集を続けられる: " + C.IsEditing);
            if (applied && report.Preview != null)
            {
                WeightFixPreview used = report.Preview;
                Fact("適用した候補の強さ: " + used.StrengthPercent + "%（" + used.Strength.ToString("F2", CultureInfo.InvariantCulture) + "）。設定の強さは " + (C.Settings.WeightFixStrength * 100f).ToString("F0", CultureInfo.InvariantCulture)
                    + "% のまま（弱めて試すは、設定を変えない）");
                Fact("適用した候補の数字: " + WeightFixNumbers(used));
                Fact("結果の欄（窓の上）の1行目: 「" + Flat(_cloth.ResultText.Split('\n')[0]) + "」");
            }
            Fact("結果の欄（窓の上）に、保存していない編集が残ること・動作チェックの結果が新しくなったことの一言が" + (_cloth.ResultText.Contains("保存していない編集は、そのまま残っています") && _cloth.ResultText.Contains("動作チェックの結果も、新しいウェイトのものになりました") ? "ある" : "ない"));
            Fact("動作チェックの結果: " + (C.LastPoseCheck != null ? C.LastPoseCheck.DescribeSummary() + (C.IsPoseCheckStale ? "（古い）" : "") : "なし"));
            yield return Snap(snapName, "おすすめの「ウェイトを体に寄せる」を押したあと（このスカートでは、よく使うポーズに外へ逃がせる浅い所が無く、ウェイトが原因の所を勧められた）。"
                + (_weightFixChoiceAsked ? "強さ 100% の候補は伸びすぎたので、「弱めて（50%）試す」を選んだ。" : "") + "確認のあと適用し、窓に前後の件数", 2);
        }

        // 候補の数字を1行にする（強さ・体から離れた所の伸び・直す範囲の突き抜け・いちばん深い所）。強さ 100% と 50% を見比べるため
        static string WeightFixNumbers(WeightFixPreview p)
        {
            string stretch = p.StretchEntry >= 0
                ? p.StretchPoseName + " " + (p.StretchBefore * 100f).ToString("F0", CultureInfo.InvariantCulture) + "% → " + (p.StretchAfter * 100f).ToString("F0", CultureInfo.InvariantCulture) + "%"
                : "増えない";
            return "強さ " + p.StrengthPercent + "%: 体から離れた所の布の伸び " + stretch + "（伸びすぎ: " + (p.StretchesTooMuch ? "はい" : "いいえ") + "）、"
                + p.ScopeLabel + "の突き抜け " + p.RegionsBefore + " → " + p.RegionsAfter + " か所（" + p.PosesBefore + " → " + p.PosesAfter + " ポーズ）、いちばん深い所 "
                + (p.MaxDepthBefore * 1000f).ToString("F1", CultureInfo.InvariantCulture) + " → " + (p.MaxDepthAfter * 1000f).ToString("F1", CultureInfo.InvariantCulture) + " mm、ウェイトが原因の所 "
                + p.WeightRegionsBefore + " → " + p.WeightRegionsAfter + " か所、ウェイトを変える頂点 " + p.ChangedVertices;
        }

        // ---- 13 ----

        IEnumerator S13_Penetration()
        {
            // ポーズを見せたままなら、窓の「元の姿勢に戻る」で戻る
            if (C.IsPosePreviewActive)
            {
                yield return ClickOrCall("元の姿勢に戻る", () => !C.IsPosePreviewActive, () => C.EndPosePreview(), "ClothEditController.EndPosePreview()");
            }
            yield return ViewTo("全体が見える正面", FrontPivot, 8f, 180f, FrontSize + 0.1f);
            PenetrationReport report = C.LastPenetration;
            Fact("体との貫通（今の姿勢）の最後の調べ: " + (report == null ? "まだ調べていない" : report.HasBody ? "体が見つかった。突き抜けている所 " + report.RegionCount + " か所（粒子 " + report.PenetratingCount + "）、余白より近い粒子 " + report.CloseCount : "体が見つからない"));
            Fact("「貫通を直す」ボタンの状態: " + ButtonState(_cloth, "貫通を直す") + "、「調べ直す」: " + ButtonState(_cloth, "調べ直す"));
            yield return Snap("13a_penetration_status", "診断の「体との貫通」（今の姿勢）。突き抜けが無ければ「✓ 体との貫通なし」の1行", 6);
            // 動作チェックの色の点は、ほかの操作を始めると自動で消えるので、外す操作は要らない

            // スカートを細くしたくて、腰の前を内側（体のほう）へ押し込む。「形だけ動かす」なので、体に食い込む
            yield return SelectToolByButton(ClothEditMode.Select);
            if (C.SelectionCount > 0)
            {
                SceneKey(KeyCode.Escape);
                yield return Pump(2);
                if (C.SelectionCount == 0)
                {
                    Op(Via.Real, "前に選んだ点を解除するため、シーンビューで Esc キーを押した（KeyDown）");
                }
                else
                {
                    C.ClearSelection();
                    Op(Via.Controller, "Esc キーでは選択が解除されなかったので、Controller の ClearSelection を直接呼んだ");
                }
            }
            yield return DragPath(Arc(-20f, 20f, 0.87f, 10), 1);
            Op(Via.Real, "「選んで動かす」で、腰の前（ウエストの少し下）を塗って選んだ");
            Fact("選んだ点: " + C.SelectionCount + " 個（動かし方は「形だけ動かす」のまま）");
            yield return ViewTo("横から見る（体のほうへ押し込むため）", new Vector3(0f, 0.8f, 0.1f), 10f, 90f, 0.7f);
            yield return Pump(4);
            Vector3 before = C.SelectionPivot;
            yield return DragHandle(Vector3.forward, -0.12f);
            yield return WaitUntilIdle(300);
            Vector3 after = C.SelectionPivot;
            bool moved = (after - before).magnitude > 0.01f;
            Fact("腰の前を内側へ押し込んだ結果: 選んだ点の中心が " + ((after - before) * 100f).ToString("F1") + " cm 動いた");
            if (!moved)
            {
                C.ApplySelectionTransform(new Vector3(0f, 0f, -0.08f), Quaternion.identity, Vector3.one);
                Op(Via.Controller, "ハンドルのドラッグでは動かなかったので、Controller の ApplySelectionTransform（体のほうへ 8cm）を直接呼んだ");
                yield return WaitUntilIdle(300);
            }
            Fact("押し込んだあとの診断: " + (C.IsPenetrationStale ? "古い（自動では調べ直していない）" : "自動で調べ直した"));
            yield return Snap("13b_pushed_in", "腰の前を体のほうへ押し込んだ（横から見ている）。操作を確定すると自動で調べ直し、診断に件数と赤い点が出る", 6);

            if (C.LastPenetration == null || C.IsPenetrationStale)
            {
                yield return ClickOrCall("調べ直す", () => C.LastPenetration != null && !C.IsPenetrationStale, () => C.RecheckPenetration(), "ClothEditController.RecheckPenetration()");
            }
            else
            {
                Op(Via.Note, "自動で調べ直していたので、「調べ直す」は押していない");
            }
            report = C.LastPenetration;
            Fact("調べた結果: " + (report == null ? "なし" : "突き抜けている所 " + report.RegionCount + " か所（最大 " + (report.MaxDepth * 1000f).ToString("F1") + " mm、粒子 " + report.PenetratingCount + "）、余白より近い粒子 " + report.CloseCount));
            yield return ViewTo("斜め前から見る（赤い点が見える）", new Vector3(0f, 0.8f, 0.1f), 10f, 140f, 0.55f);
            yield return Snap("13c_penetration_found", "斜め前から見た。体が布を突き抜けている所が、白い縁の赤い点で出る（ピンの青い点とは違う色）。診断に件数。「貫通を直す」が押せる", 4);

            if (report != null && report.PenetratingCount > 0)
            {
                yield return ClickOrCall("貫通を直す", () => C.LastPenetrationFix != null, () => Invoke(_cloth, "FixPenetrationFromWindow"), "ClothEditWindow.FixPenetrationFromWindow()", 1);
                Waited("「貫通を直す」を押してから終わるまで", _lastClickMs);
                Fact("直したあと: " + (C.LastPenetration == null ? "なし" : "突き抜けている所 " + C.LastPenetration.RegionCount + " か所") + "、直した結果: " + (C.LastPenetrationFix == null ? "なし" : C.LastPenetrationFix.ToString()));
            }
            else
            {
                Op(Via.Note, "突き抜けている所が出なかったので、「貫通を直す」は押していない");
            }
            yield return Snap("13d_penetration_fixed", "「貫通を直す」を押したあと。赤い点が消え、窓の真ん中に短い通知、窓の上に結果の一言", 1);
        }

        // ---- 14 ----

        IEnumerator S14_Save()
        {
            yield return ViewTo("全体が見える正面", FrontPivot, 8f, 180f, FrontSize + 0.1f);
            int savedBefore = SavedKeyCount();
            Fact("保存の名前の欄（自動で入る名前）: 「" + (string)GetField(_cloth, "_shapeName") + "」");
            yield return ClickOrCall("保存", () => SavedKeyCount() > savedBefore, () => Invoke(_cloth, "Save"), "ClothEditWindow.Save()", 1);
            Waited("見出しの行の「保存」を押してから終わるまで（保存して、編集を続けられるよう作り直す）", _lastClickMs);
            Fact("保存したメッシュ: " + (C.LastSavedMesh != null ? AssetDatabase.GetAssetPath(C.LastSavedMesh) : "なし") + "。編集は続けられる: " + C.IsEditing);
            GP02ShapeKeyData data = _cloth.FindSavedKeyData(out GP02ShapeKeys component);
            Fact("保存したシェイプキー: " + (data != null ? data.Entries.Count + " 個（" + string.Join("、", data.Entries.ConvertAll(e => e.Name)) + "）。ウェイトを体に寄せるの前に保存していないので、ここで保存したものだけ（1 個）になるか: " + (data.Entries.Count == 1 ? "はい" : "いいえ") : "なし") + "。スカートにシェイプキーの数 " + (_skirt is SkinnedMeshRenderer smr && smr.sharedMesh != null ? smr.sharedMesh.blendShapeCount : -1));
            // 保存したシェイプキーの一覧は、窓のいちばん下にある。見に行く（マウスホイールで下へ）
            ScrollWindowTo(true);
            Op(Via.Real, "保存した結果を見るため、窓の中身をマウスホイールで一番下までスクロールした");
            yield return SnapKeep("14a_saved", "保存した直後。窓を一番下までスクロールして、「保存」の区切りを見ている。保存したシェイプキーの一覧（名前・「表示中（重み 100）」・布で再編集・削除）が出る", 3, _hierarchy);

            // 手順の案内は、保存したあと、どうなるか
            yield return ToggleGuide(true);
            yield return Snap("14b_guide_after_save", "保存したあとの「手順（今どこまで済んだか）」。5つめの「保存」が済んだ印になるか", 3);
            yield return ToggleGuide(false);

            // インスペクターで、スカートの SkinnedMeshRenderer の Blend Shapes を開いて見る
            string inspectorNote = OpenBlendShapesInInspector();
            Fact("インスペクター: " + inspectorNote);
            // 見やすいように、このときだけインスペクターを縦に大きくする（ヒエラルキーの上に重なる）
            _inspector.Focus();
            _inspector.position = new Rect(0, 0, 275, 930);
            yield return Pump(8);
            yield return Snap("14c_inspector_blendshapes", "インスペクターの Skinned Mesh Renderer の Blend Shapes（保存したシェイプキー）と、GP02 Shape Keys。見やすいように、インスペクターを縦に大きくして撮った", 4, _inspector);
            ArrangeWindows();
            yield return Pump(4);
        }

        // 保存したシェイプキーのデータの数（保存できたかを見る。同じメッシュのアセットへ保存し直すと、LastSavedMesh は変わらないため）
        int SavedKeyCount()
        {
            GP02ShapeKeyData data = _cloth.FindSavedKeyData(out _);
            return data != null ? data.Entries.Count : 0;
        }

        // インスペクターの SkinnedMeshRenderer の「Blend Shapes」を開く（SkinnedMeshRendererEditor が持つ、開閉の SerializedProperty を開く）
        string OpenBlendShapesInInspector()
        {
            try
            {
                foreach (Editor editor in ActiveEditorTracker.sharedTracker.activeEditors)
                {
                    if (!(editor.target is SkinnedMeshRenderer)) continue;
                    FieldInfo field = editor.GetType().GetField("m_BlendShapeWeights", Any);
                    if (field == null || !(field.GetValue(editor) is SerializedProperty property)) continue;
                    property.isExpanded = true;
                    if (_inspector != null) _inspector.Repaint();
                    return "Blend Shapes の区切りを開いた（SkinnedMeshRendererEditor の m_BlendShapeWeights.isExpanded）";
                }
                return "Blend Shapes の区切りを開く入口が見つからなかった（インスペクターのまま撮った）";
            }
            catch (Exception ex)
            {
                return "Blend Shapes の区切りを開けなかった（" + ex.GetType().Name + "）";
            }
        }

        // ---- 15 ----

        IEnumerator S15_ShapeKeyList()
        {
            bool ok = EditorApplication.ExecuteMenuItem("Tools/GP02/シェイプキーの一覧");
            Op(Via.Menu, "メニュー「Tools > GP02 > シェイプキーの一覧」を選んだ（結果: " + ok + "）。選んでいるのは編集中のスカート");
            var list = EditorWindow.GetWindow<ShapeKeyListWindow>();
            list.position = new Rect(705, 0, 575, 900);
            yield return Pump(8);
            yield return Snap("15_shape_key_list", "シェイプキーの一覧のウィンドウ（かんたん表示）。保存したシェイプキーの名前・重み・複製・削除・分割・反転と、メッシュとの比べ（保存した直後なので「✓ メッシュに反映済み」）", 6, list);
            list.Close();
            yield return Pump(4);
        }

        // ---- 16 ----

        IEnumerator S16_EndEditing()
        {
            // 保存したあと、何も変えていないので、確認は出ない
            Fact("終了の前: 保存していない編集があるか " + C.HasUnsavedEdits);
            yield return ClickOrCall("編集を終了", () => !C.IsEditing, () => Invoke(_cloth, "RequestEnd"), "ClothEditWindow.RequestEnd()");
            Fact("終了したあと: 編集中 " + C.IsEditing + "、確認のダイアログ " + _r.Dialogs.Count + " 回");
            yield return Snap("16a_end_editing_after_save", "保存したあとの「編集を終了」。確認は出ずに終わる。窓は編集前の姿に戻る（保存したシェイプキーはスカートに入ったまま）", 6);

            // 別の流れ: もう一度編集を始めて、形を変えて、保存せずに終了する
            yield return StartEditingByButton();
            yield return QuickPull(0.08f);
            if (!C.HasUnsavedEdits)
            {
                Op(Via.Note, "引いても形が変わらなかったので、反対側（アバターの左）の裾で引き直した");
                yield return QuickPull(0.08f, -35f);
            }
            Fact("もう一度始めて、少し引いた: 保存していない編集 " + C.HasUnsavedEdits);
            _unsavedAnswers.Enqueue(ClothEditUnsavedChoice.Cancel);
            yield return ClickOrCall("編集を終了", () => _r.Dialogs.Count > 0, () => Invoke(_cloth, "RequestEnd"), "ClothEditWindow.RequestEnd()", 2);
            Fact("確認で「やめる」を選んだあと: 編集中 " + C.IsEditing);
            yield return Snap("16b_end_cancelled", "保存していない変更があるときの「編集を終了」。確認で「やめる」を選んだので、編集が続いている（確認の文は、この段階の「確認のダイアログの文」）", 4);

            _unsavedAnswers.Enqueue(ClothEditUnsavedChoice.Discard);
            yield return ClickOrCall("編集を終了", () => !C.IsEditing, () => Invoke(_cloth, "RequestEnd"), "ClothEditWindow.RequestEnd()", 2);
            Fact("確認で「保存しないで終わる」を選んだあと: 編集中 " + C.IsEditing + "。引いた形は捨てられた");
            yield return Snap("16c_end_discarded", "確認で「保存しないで終わる」を選んだあと。編集が終わり、引いた形は捨てられた", 6);
        }

        // 窓の「編集を開始」を押して、編集を始める（押せなければボタンの処理を直接）。label は、操作の記録の頭に付ける印（(b) など）
        IEnumerator StartEditingByButton(string label = "")
        {
            var timer = Stopwatch.StartNew();
            bool clicked = ClickText(_cloth, "編集を開始", out string how);
            double ms = _lastClickMs;
            if (clicked && C.IsEditing)
            {
                Op(Via.Real, label + "窓のボタン" + how);
            }
            else
            {
                Op(Via.Real, label + "窓のボタン" + how + (clicked ? " → 編集は始まらなかった" : ""));
                timer.Restart();
                Invoke(_cloth, "StartEdit");
                ms = timer.Elapsed.TotalMilliseconds;
                Op(Via.Button, label + "ボタンの処理 ClothEditWindow.StartEdit() を直接呼んだ");
            }
            Waited(label + "「編集を開始」（2回目以降）", ms);
            RememberSkirtRest();
            yield return Pump(4);
        }

        // 裾を distance（メートル）だけ、外へ引いて、離して、落ち着くまで待つ。degrees が負なら、アバターの左側の裾
        IEnumerator QuickPull(float distance, float degrees = 35f)
        {
            yield return SelectToolByButton(ClothEditMode.Grab);
            yield return ViewTo("引く側が広く見える正面", PullPivot, 8f, 180f, PullSize);
            if (!SceneCalibrated) CalibrateScene(SkirtPoint(0f, 0.74f));
            Vector3 start = SurfacePoint(degrees, 0.535f);
            float dx = degrees >= 0f ? distance : -distance;
            yield return DragPath(Line(start, start + new Vector3(dx, 0f, 0f), 6), 1);
            Op(Via.Real, "つまむ道具で、裾を " + (distance * 100f).ToString("F0") + " cm、外へ引いて離した");
            yield return WaitUntilIdle(300);
        }

        // ---- 17 ----

        IEnumerator S17_CloseAndReopen()
        {
            yield return StartEditingByButton();
            // ウエストを少し塗って、裾を引く（保存していない編集を作る）
            yield return SelectToolByButton(ClothEditMode.Pin);
            yield return DragPath(Arc(-60f, 60f, 0.935f, 16), 1);
            Op(Via.Real, "ピン留めの道具で、ウエストの前側を塗った");
            yield return QuickPull(0.1f);
            Fact("閉じる前: 保存していない編集 " + C.HasUnsavedEdits + "、ピン " + C.Session.PinnedCount);
            yield return Snap("17a_before_close", "編集の途中（ピンと引いた形がある）。このあと、うっかりウィンドウを閉じる", 4);

            // ウィンドウを閉じる（タブの右クリックの「Close Tab」と同じ）。閉じるときに、編集が取っておかれる
            _cloth.Close();
            Op(Via.Button, "ウィンドウを閉じた（EditorWindow.Close。タブを右クリックして「Close Tab」を選ぶのと同じ）。閉じるときに、保存していない編集が Library に取っておかれる");
            yield return Pump(6);
            Fact("取っておいたファイル: " + (Directory.Exists(ClothEditStash.Folder) ? Directory.GetFiles(ClothEditStash.Folder, "*" + ClothEditStash.FileExtension).Length + " 個" : "なし"));
            _cloth = null;

            bool ok = EditorApplication.ExecuteMenuItem("Tools/GP02/布の編集");
            Op(Via.Menu, "メニュー「Tools > GP02 > 布の編集」で、ウィンドウを開き直した（結果: " + ok + "）");
            _cloth = EditorWindow.GetWindow<ClothEditWindow>();
            ArrangeWindows();
            yield return Pump(8);
            Fact("開き直した直後: 前回の編集の帯 " + _cloth.HasStashBand + "、編集中 " + C.IsEditing + "、対象 " + (_cloth.Targets.Count > 0 ? _cloth.Targets[0].name : "なし"));
            if (_cloth.StashInfo != null) Fact("帯の2行目: " + ClothEditStash.Describe(_cloth.StashInfo, DateTime.UtcNow));
            yield return Snap("17b_reopen_band", "ウィンドウを開き直した直後。上に「前回の編集が残っています」の帯（戻す・捨てる）", 6);

            var timer = Stopwatch.StartNew();
            bool clicked = ClickText(_cloth, "戻す", out string how);
            double ms = _lastClickMs;
            if (clicked && C.IsEditing)
            {
                Op(Via.Real, "窓のボタン" + how);
            }
            else
            {
                Op(Via.Real, "窓のボタン" + how + (clicked ? " → 戻らなかった" : ""));
                timer.Restart();
                _cloth.RestoreFromStash();
                ms = timer.Elapsed.TotalMilliseconds;
                Op(Via.Button, "ボタンの処理 ClothEditWindow.RestoreFromStash() を直接呼んだ");
            }
            Waited("帯の「戻す」を押してから、編集が戻るまで", ms);
            yield return Pump(1);
            Fact("戻したあと: 編集中 " + C.IsEditing + "、保存していない編集 " + C.HasUnsavedEdits + "、ピン " + (C.Session != null ? C.Session.PinnedCount : -1) + "、粒子が動いた最大 " + MaxMovedCm(C.Session).ToString("F1") + " cm");
            yield return Snap("17c_restored", "「戻す」を押したあと。編集が始まって、ピンと引いた形が戻った。窓の真ん中に「前回の編集を戻しました」", 1);

            // 後始末: 保存せずに終える
            _unsavedAnswers.Enqueue(ClothEditUnsavedChoice.Discard);
            yield return ClickOrCall("編集を終了", () => !C.IsEditing, () => Invoke(_cloth, "RequestEnd"), "ClothEditWindow.RequestEnd()", 2);
        }

        // ---- 18 ----

        IEnumerator S18_FailureExamples()
        {
            // (a) アバターそのもの（親）を選ぶ
            yield return SelectInHierarchy(TestAvatarName, _avatar, "(a) ");
            Fact("(a) アバターを選んだあとの窓の「対象」: " + (_cloth.Targets.Count > 0 ? _cloth.Targets[0].name : "なし") + "（アバターには布が無いので、対象は変わらない）");
            if (ClickText(_cloth, "選択中のオブジェクトから設定", out string how)) Op(Via.Real, "(a) 窓のボタン" + how);
            yield return Pump(3);
            Fact("(a) 窓の「この中の衣装」の一覧: " + (_cloth.Candidates.Count == 0 ? "出ていない" : string.Join("、", ListCandidates())));
            yield return Snap("18a_select_avatar_root", "(a) アバターそのものを選んで、「選択中のオブジェクトから設定」を押した。ダイアログは出ず、窓の対象の区切りに「この中の衣装」の一覧（体らしいものは最後に「体のようです」）が出る", 6, _hierarchy);

            // 一覧の「Tights」を押して、タイツを対象にする
            if (ClickText(_cloth, TestAvatarMenu.TightsName, out how)) Op(Via.Real, "(a) 窓の「この中の衣装」の" + how);
            else Op(Via.Real, "(a) 「この中の衣装」の一覧で、" + how);
            yield return Pump(3);
            Fact("(a) 一覧で押したあとの窓の「対象」: " + (_cloth.Targets.Count > 0 ? _cloth.Targets[0].name : "なし") + "（ヒエラルキーの選択は " + (Selection.activeGameObject != null ? Selection.activeGameObject.name : "なし") + " のまま）");
            yield return Snap("18a2_pick_from_list", "(a) 「この中の衣装」の「Tights」を押した。対象がタイツになる（ヒエラルキーの選択はアバターのまま）", 6, _hierarchy);

            // (b) 体（Body）を選んで開始
            yield return SelectInHierarchy(TestAvatarMenu.BodyName, _avatar.transform.Find(TestAvatarMenu.BodyName).gameObject, "(b) ");
            Fact("(b) 体を選んだあとの窓の「対象」: " + (_cloth.Targets.Count > 0 ? _cloth.Targets[0].name : "なし"));
            yield return Snap("18b_select_body", "(b) 体（Body）を選んだ。自動で「対象」に入り、診断に「体のメッシュのようです」の注意と、衣装の候補が出る", 6, _hierarchy);
            yield return StartEditingByButton("(b) ");
            Fact("(b) 体で編集を始めた結果: 編集中 " + C.IsEditing + (C.IsEditing ? "、粒子 " + C.Session.ParticleCount.ToString("N0") + "、準備 " + C.Session.PrepareMilliseconds.ToString("F0") + " ms" : "、理由: " + C.LastError));
            yield return Snap("18c_edit_on_body", "(b) 注意を読まずに「編集を開始」を押すと、体でも編集は始まる（注意だけで、止めはしない）", 6);
            _unsavedAnswers.Enqueue(ClothEditUnsavedChoice.Discard);
            if (C.IsEditing) Invoke(_cloth, "RequestEnd");

            // (c) 読み書きできないメッシュ
            GameObject unreadable = CreateGridCloth("Unreadable_Cloth", true);
            Op(Via.Controller, "(c) 読み書き（Read/Write）が無効なメッシュの布「Unreadable_Cloth」を、スクリプトで作った（ヒエラルキーで右クリック > 作るメニューの代わり）");
            yield return SelectInHierarchy("Unreadable_Cloth", unreadable, "(c) ");
            Fact("(c) 窓の「対象」: " + (_cloth.Targets.Count > 0 ? _cloth.Targets[0].name : "なし"));
            yield return Snap("18d_unreadable_mesh", "(c) 読み書きできないメッシュを選んだ。診断に「Read/Write が無効です…」の注意と、直すボタン", 6, _hierarchy);
            if (ClickText(_cloth, "直す: Read/Write を有効にする", out how)) Op(Via.Real, "(c) 窓のボタン" + how);
            yield return Pump(4);
            yield return Snap("18e_fix_readwrite", "(c) 「直す: Read/Write を有効にする」を押したあと。FBX から読み込んだメッシュではないので自動では直せない、という案内がダイアログで出る", 6);

            // (d) メッシュが無い
            GameObject empty = CreateGridCloth("NoMesh_Cloth", false);
            empty.GetComponent<SkinnedMeshRenderer>().sharedMesh = null;
            Op(Via.Controller, "(d) メッシュが空の SkinnedMeshRenderer「NoMesh_Cloth」を、スクリプトで作った");
            yield return SelectInHierarchy("NoMesh_Cloth", empty, "(d) ");
            yield return Snap("18f_no_mesh", "(d) メッシュが無い物を選んだ。「この対象では編集を始められません」と理由が出て、編集を開始は押せない", 6, _hierarchy);

            // (e) 保存の名前の間違い
            yield return SelectInHierarchy(TestAvatarMenu.SkirtName, _skirt.gameObject, "(e) ");
            yield return StartEditingByButton("(e) ");
            SetField(_cloth, "_shapeName", "");
            Op(Via.Setting, "(e) 保存の「シェイプキーの名前」の欄を、空にした（欄に入力する代わりに、値を直接変えた）");
            if (ClickText(_cloth, "シェイプキーとして保存", out how)) Op(Via.Real, "(e) 名前が空のまま、窓のボタン" + how);
            yield return Pump(3);
            SetField(_cloth, "_shapeName", "GP02_1");
            Op(Via.Setting, "(e) 名前を、すでにあるシェイプキーと同じ「GP02_1」にした");
            if (ClickText(_cloth, "シェイプキーとして保存", out how)) Op(Via.Real, "(e) 同じ名前で、窓のボタン" + how);
            yield return Pump(3);
            yield return Snap("18g_save_name_errors", "(e) 名前が空のとき・すでにある名前のときの保存。どちらもダイアログで理由が出る（この段階の「確認のダイアログの文」）。画面は変わらない", 6);
            _unsavedAnswers.Enqueue(ClothEditUnsavedChoice.Discard);
            if (C.IsEditing) Invoke(_cloth, "RequestEnd");

            // (f) アバターの外の布（Animator が無い）
            GameObject loose = CreateGridCloth("Loose_Cloth", false);
            Op(Via.Controller, "(f) アバターの中ではない、ただの布「Loose_Cloth」を、スクリプトで作った");
            yield return SelectInHierarchy("Loose_Cloth", loose, "(f) ");
            yield return StartEditingByButton("(f) ");
            Fact("(f) アバターの外の布で編集を始めた結果: 編集中 " + C.IsEditing + "、動作チェックの理由: " + (C.IsEditing ? C.GetPoseCheckProblem() : "（編集していない）"));
            yield return Snap("18h_cloth_without_avatar", "(f) アバター（Animator）の外の布。動作チェックは使えない理由が出る。対称・診断の注意も出る", 6);
            _unsavedAnswers.Enqueue(ClothEditUnsavedChoice.Discard);
            if (C.IsEditing) Invoke(_cloth, "RequestEnd");
        }

        // 窓の「この中の衣装」の一覧の名前（記録用）。体らしいものには印を付ける
        List<string> ListCandidates()
        {
            var names = new List<string>();
            foreach (ClothCandidate candidate in _cloth.Candidates)
            {
                if (candidate.Renderer == null) continue;
                names.Add(candidate.Renderer.name + (candidate.LikelyBody ? "（体のようです）" : ""));
            }
            return names;
        }

        // 平らな布（20x20 の格子）を作る。unreadable なら、読み書きできないメッシュにする
        GameObject CreateGridCloth(string name, bool unreadable)
        {
            ProceduralMeshes.Grid(20, 20, 0.6f, 0.6f, out System.Numerics.Vector3[] grid, out int[] triangles);
            var vertices = new Vector3[grid.Length];
            for (int i = 0; i < grid.Length; i++) vertices[i] = new Vector3(grid[i].X - 0.3f, grid[i].Y + 1.0f, 3f);
            var mesh = new Mesh { name = name + "_Mesh", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            if (unreadable) mesh.UploadMeshData(true);
            var go = new GameObject(name);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.9f, 0.6f, 0.7f) };
            _extraObjects.Add(go);
            _extraObjects.Add(mesh);
            return go;
        }

        // ---- 道具の切り替え（窓の道具のボタンを押す） ----

        IEnumerator SelectToolByButton(ClothEditMode mode)
        {
            if (C.Settings.Tool == mode && !(mode == ClothEditMode.Pin && C.Settings.PinEraseMode)) yield break;
            string label = ClothEditHelp.ToolName(mode) + " (" + ClothEditHelp.ToolKey(mode) + ")";
            bool clicked = ClickText(_cloth, label, out string how);
            yield return Pump(3);
            if (clicked && C.Settings.Tool == mode)
            {
                Op(Via.Real, "窓の道具のボタン" + how);
            }
            else
            {
                _cloth.OverlaySetTool(mode);
                Op(Via.Button, "窓の道具のボタン『" + label + "』が効かなかったので、シーンビューの道具バーのボタンの処理 OverlaySetTool を直接呼んだ");
                yield return Pump(2);
            }
        }

        // 窓のボタンを実際にクリックして、効果が出なければ、ボタンの処理を直接呼ぶ（どちらを使ったかを記録する）
        IEnumerator ClickOrCall(string text, Func<bool> worked, Action fallback, string fallbackName, int framesAfter = 3)
        {
            bool found = ClickText(_cloth, text, out string how);
            yield return Pump(framesAfter);
            if (found && worked())
            {
                Op(Via.Real, "窓のボタン" + how);
                yield break;
            }
            Op(Via.Real, "窓のボタン" + how + (found ? " → 効果が出なかった" : ""));
            double fallbackMs;
            var timer = Stopwatch.StartNew();
            fallback();
            fallbackMs = timer.Elapsed.TotalMilliseconds;
            _lastClickMs = found ? _lastClickMs : fallbackMs;
            Op(Via.Button, fallbackName + " を直接呼んだ");
            yield return Pump(framesAfter);
        }

        // ヒエラルキーで name の行を実際にクリックして選ぶ。選べなければ、Selection で選ぶ（どちらかを記録する）
        IEnumerator SelectInHierarchy(string name, GameObject expected, string label)
        {
            _hierarchy.Repaint();
            yield return Pump(3);
            bool clicked = ClickText(_hierarchy, name, out string how, "TV Line");
            yield return Pump(4);
            if (clicked && Selection.activeGameObject == expected)
            {
                Op(Via.Real, label + "ヒエラルキーの行" + how);
                yield break;
            }
            Op(Via.Real, label + "ヒエラルキーの行" + how + (clicked ? " → 選べなかった" : ""));
            Selection.activeGameObject = expected;
            Op(Via.Controller, label + "Selection.activeGameObject で「" + name + "」を選んだ");
            yield return Pump(4);
        }

        // 窓の「手順（今どこまで済んだか）」の見出しをクリックして、開く・閉じる
        IEnumerator ToggleGuide(bool open)
        {
            if ((bool)GetField(_cloth, "_foldGuide") == open) yield break;
            bool clicked = ClickText(_cloth, "手順（今どこまで済んだか）", out string how, "*");
            yield return Pump(3);
            if (clicked && (bool)GetField(_cloth, "_foldGuide") == open)
            {
                Op(Via.Real, "窓の見出し" + how + "（" + (open ? "開く" : "閉じる") + "）");
                yield break;
            }
            Op(Via.Real, "窓の見出し" + how + (clicked ? " → 開閉しなかった" : ""));
            SetField(_cloth, "_foldGuide", open);
            Op(Via.Setting, "見出しの開閉の値（_foldGuide）を直接 " + open + " にした");
            _cloth.Repaint();
            yield return Pump(3);
        }

        // 編集を始める前のスカートの頂点を覚える（形が変わったあとでも、同じ所を狙うため）
        void RememberSkirtRest()
        {
            if (_skirtRest != null || C == null || C.Session == null) return;
            if (_cloth.Targets.Count != 1 || _cloth.Targets[0] != _skirt) return;
            _skirtRest = (Vector3[])C.Session.CurrentWorldVertices.Clone();
        }

        // 視点を変える（Alt+ドラッグやギズモの代わりに、SceneView.LookAt で直接）
        IEnumerator ViewTo(string label, Vector3 pivot, float pitch, float yaw, float size)
        {
            Op(Via.Note, "シーンビューの視点を「" + label + "」に変えた（実際は Alt+ドラッグやギズモで回す。ここでは SceneView.LookAt）");
            yield return SetView(pivot, pitch, yaw, size);
        }
    }
}
