using System;
using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 「ウェイトを体に寄せる」の候補が、体から離れた布（スカートなど）を伸ばしすぎるとき、かんたん表示のまま「弱めて（50%）試す」を選べること。
    /// 強さの設定はくわしく表示にしか無いので、確認のダイアログに「弱めて（50%）試す」「やめる」「このまま適用」を出し、
    /// 弱めるときは設定の強さを変えずに、その候補を作るときだけ 50% にする。
    /// 動作チェックを使う確認（スカート・タイツ）は時間がかかるので、1つのテストで複数のことを確かめる。
    /// 候補の説明の文と、聞き方の分かれ道は、伸びの数字だけを持つ手作りの候補で、動作チェックなしに確かめる。
    /// ダイアログは自動テストを止めるので、ClothEditPrompts の3つの確認（ChooseWeightFix・Confirm・AskUnsaved）を差し替え、TearDown で必ず戻す。
    /// </summary>
    public class WeightFixWeakerTests
    {
        const string TempFolder = "Assets/GP02_WeightFixWeakerTestTemp";

        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditWindow> _windows = new List<ClothEditWindow>();

        UiLanguage _savedLanguage;
        ClothEditViewMode _savedView;
        bool _savedHidden;
        Func<string, WeightFixChoice> _savedChoose;
        Func<string, string, string, string, bool> _savedConfirm;
        Func<ClothEditLeaveReason, ClothEditUnsavedChoice> _savedAsk;

        // 確認のダイアログの記録と、決めておく答え（答えが無いときは「やめる」）
        ClothEditController _watched;
        readonly List<string> _chooseMessages = new List<string>();
        readonly List<WeightFixPreview> _candidatesAtChoose = new List<WeightFixPreview>();
        readonly Queue<WeightFixChoice> _chooseAnswers = new Queue<WeightFixChoice>();
        readonly List<string> _confirmMessages = new List<string>();
        readonly List<string> _confirmButtons = new List<string>();
        readonly List<WeightFixPreview> _candidatesAtConfirm = new List<WeightFixPreview>();
        readonly Queue<bool> _confirmAnswers = new Queue<bool>();
        int _unsavedAsked;

        [SetUp]
        public void SetUp()
        {
            // NUnit は、テストのクラスを1つだけ作って全部のテストに使うので、前のテストの記録を残さない
            _watched = null;
            _chooseMessages.Clear();
            _candidatesAtChoose.Clear();
            _chooseAnswers.Clear();
            _confirmMessages.Clear();
            _confirmButtons.Clear();
            _candidatesAtConfirm.Clear();
            _confirmAnswers.Clear();
            _unsavedAsked = 0;
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.Japanese;
            _savedView = ClothEditView.Mode;
            ClothEditView.Mode = ClothEditViewMode.Simple;
            _savedHidden = Tools.hidden;
            Tools.hidden = false;
            _savedChoose = ClothEditPrompts.ChooseWeightFix;
            _savedConfirm = ClothEditPrompts.Confirm;
            _savedAsk = ClothEditPrompts.AskUnsaved;
            ClothEditPrompts.ChooseWeightFix = message =>
            {
                _chooseMessages.Add(message);
                _candidatesAtChoose.Add(_watched != null ? _watched.WeightFixPreview : null);
                return _chooseAnswers.Count > 0 ? _chooseAnswers.Dequeue() : WeightFixChoice.Cancel;
            };
            ClothEditPrompts.Confirm = (title, message, ok, cancel) =>
            {
                _confirmMessages.Add(message);
                _confirmButtons.Add(title + "|" + ok + "|" + cancel);
                _candidatesAtConfirm.Add(_watched != null ? _watched.WeightFixPreview : null);
                return _confirmAnswers.Count > 0 && _confirmAnswers.Dequeue();
            };
            ClothEditPrompts.AskUnsaved = reason =>
            {
                _unsavedAsked++;
                return ClothEditUnsavedChoice.Cancel;
            };
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            ClothEditPrompts.ChooseWeightFix = _savedChoose;
            ClothEditPrompts.Confirm = _savedConfirm;
            ClothEditPrompts.AskUnsaved = _savedAsk;
            // 窓を先に消す（閉じるときに編集を取っておくので、衣装がまだ生きているうちに）
            foreach (ClothEditWindow window in _windows)
            {
                if (window != null) Object.DestroyImmediate(window);
            }
            _windows.Clear();
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
            Tools.hidden = _savedHidden;
            ClothEditView.Mode = _savedView;
            L10n.Language = _savedLanguage;
        }

        // ---- 部品 ----

        GameObject CreateAvatar()
        {
            GameObject root = TestAvatarMenu.CreateInScene(null);
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

        ClothEditWindow NewIdleWindow()
        {
            var window = ScriptableObject.CreateInstance<ClothEditWindow>();
            _windows.Add(window);
            window.Controller.SaveFolder = TempFolder;
            window.Controller.DataFolder = TempFolder;
            return window;
        }

        // 窓で衣装の編集を始める。設定は、手元の EditorPrefs に左右されないよう既定にして、大きく動くポーズも直す範囲に入れる
        // （スカートは、しゃがむで脚に貼り付いて伸びる。既定のよく使うポーズだけの範囲では、伸びを測るポーズが入らない）
        ClothEditWindow StartWindow(GameObject root, string rendererName)
        {
            ClothEditWindow window = NewIdleWindow();
            ClothEditController c = window.Controller;
            c.ShowProgress = false;
            c.Settings.ResetToDefaults();
            c.Settings.FixLargePoses = true;
            Assert.That(c.Settings.WeightFixStrength, Is.EqualTo(1f), "既定の強さは 100%");
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, rendererName) }), Is.True, c.LastError);
            _watched = c;
            return window;
        }

        // 伸びの数字だけを持つ、手作りの候補（前後の伸び 0.45 = 45%）。動作チェックをしないで、説明の文と聞き方の分かれ道を確かめる
        static WeightFixPreview FakePreview(float strength, float stretchBefore, float stretchAfter)
        {
            PosePreset pose = PoseLibrary.All[0];
            var before = new PoseCheckResult();
            before.Entries.Add(new PoseCheckEntry { Pose = pose, Stage = 1f, Result = new PosePenetration { HighStretch = stretchBefore } });
            var after = new PoseCheckResult();
            after.Entries.Add(new PoseCheckEntry { Pose = pose, Stage = 1f, Result = new PosePenetration { HighStretch = stretchAfter } });
            return new WeightFixPreview { Before = before, After = after, Strength = strength };
        }

        static string FirstLine(string text) => text.Split('\n')[0];

        // ---- (a) 伸びすぎる候補: 弱めて試す → 50% で試し直して、適用する ----

        [Test]
        public void StretchingSkirt_TryWeaker_TriesAgainAtHalfStrength_AppliesIt_AndKeepsTheSetting()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = StartWindow(root, TestAvatarMenu.SkirtName);
            ClothEditController c = window.Controller;
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);

            _chooseAnswers.Enqueue(WeightFixChoice.TryWeaker);
            _confirmAnswers.Enqueue(true);   // 弱めて試した結果を見て、適用する
            window.PreviewWeightFixFromWindow(true);

            // 強さ 100% の候補は、スカートが脚に貼り付いて伸びすぎる。3つの選び方の確認が出て、本文に候補の説明と「弱めて」がある
            Assert.That(_chooseMessages.Count, Is.EqualTo(1), "伸びすぎる候補なのに、選び方の確認が出ない");
            WeightFixPreview full = _candidatesAtChoose[0];
            Assert.That(full, Is.Not.Null);
            Assert.That(full.Strength, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(full.StretchesTooMuch, Is.True, $"伸びの目安: {full.StretchPoseName} {full.StretchBefore} → {full.StretchAfter}");
            string ask = _chooseMessages[0];
            StringAssert.Contains("弱めて", ask);
            StringAssert.Contains("体から離れた布（スカートなど）が伸びすぎます。強さを 50% に弱めて、もう一度試せます。", ask);
            StringAssert.Contains(full.Describe(false), ask, "本文に、候補の説明（かんたん表示のもの）がある");
            StringAssert.Contains("確認で『弱めて（50%）試す』を選ぶと和らぎます", ask);
            StringAssert.Contains(full.StretchPoseName, ask);

            // 弱めて試し直す: 強さ 50% の候補を作り、前後を見せて、適用するかを聞く
            Assert.That(_confirmMessages.Count, Is.EqualTo(1), "弱めて試したあとに、適用の確認が出ない");
            Assert.That(_confirmButtons[0], Is.EqualTo("GP02|適用|やめる"));
            WeightFixPreview half = _candidatesAtConfirm[0];
            Assert.That(half, Is.Not.Null);
            Assert.That(half, Is.Not.SameAs(full), "50% で作り直していない");
            Assert.That(half.Strength, Is.EqualTo(0.5f).Within(1e-6f), "試し直した候補の強さが 50% でない");
            Assert.That(half.StrengthPercent, Is.EqualTo(50));
            Assert.That(half.StretchIncrease, Is.LessThan(full.StretchIncrease), "弱めても、伸びが和らがない");
            Debug.Log($"[GP02] スカートの弱めて試す: 強さ 100% {full} / 強さ 50% {half}");
            string confirm = _confirmMessages[0];
            StringAssert.StartsWith("強さ 50% で試した結果です。\n", confirm);
            StringAssert.Contains(half.Describe(false), confirm);
            StringAssert.Contains("、強さ 50%", FirstLine(half.Describe(false)), "説明の1行目に強さが出る");
            StringAssert.EndsWith("この候補を適用しますか？（Ctrl+Z の 1 回で戻せます）", confirm);
            Assert.That(confirm.Contains("まだ伸びます。くわしく表示の『強さ』で、さらに下げられます。"), Is.EqualTo(half.StretchesTooMuch),
                "50% でもまだ伸びるときだけ、「まだ伸びます」と言う");
            StringAssert.DoesNotContain("弱めて", confirm, "もう 50% なので、さらに「弱めて」とは言わない");

            // 適用した: 結果の強さも 0.5。設定の強さは 1 のまま（次の衣装では、100% のままにするため）
            WeightFixReport report = c.LastWeightFix;
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Assert.That(report.Preview, Is.SameAs(half));
            Assert.That(report.Preview.Strength, Is.EqualTo(0.5f).Within(1e-6f), "適用した結果の強さが 50% でない");
            Assert.That(c.Settings.WeightFixStrength, Is.EqualTo(1f), "設定の強さを変えてしまった");
            Assert.That(_unsavedAsked, Is.EqualTo(0), "保存していない編集は無いので、保存の確認は出ない");
            Assert.That(report.ChangedVertices, Is.GreaterThan(0));

            // 窓の上の結果の欄と、真ん中の通知
            StringAssert.StartsWith("ウェイトを体に寄せました（強さ 50%。Ctrl+Z の 1 回で戻せます）。", window.ResultText);
            StringAssert.Contains(half.Describe(false), window.ResultText);
            Assert.That(window.LastToast, Is.EqualTo("ウェイトを体に寄せました"));
        }

        // ---- (b) このまま適用 (c) やめる ----

        [Test]
        public void StretchingSkirt_CancelDoesNothing_DecliningTheWeakerOneKeepsItInTheWindow_ApplyAsIsAppliesAtFullStrength()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = StartWindow(root, TestAvatarMenu.SkirtName);
            ClothEditController c = window.Controller;
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);

            // (c) やめる: 何も適用せず、適用の確認も出さない。候補は窓に残る（今の確認の「やめる」と同じ）
            _chooseAnswers.Enqueue(WeightFixChoice.Cancel);
            window.PreviewWeightFixFromWindow(true);
            Assert.That(_chooseMessages.Count, Is.EqualTo(1), "伸びすぎる候補なのに、選び方の確認が出ない");
            Assert.That(_candidatesAtChoose[0].StretchesTooMuch, Is.True);
            Assert.That(_confirmMessages, Is.Empty, "やめたあとに、適用の確認を出した");
            Assert.That(c.LastWeightFix, Is.Null, "やめたのに適用した");
            Assert.That(_unsavedAsked, Is.EqualTo(0));
            Assert.That(c.WeightFixPreview, Is.SameAs(_candidatesAtChoose[0]), "やめても、確認した候補は窓に残る");
            Assert.That(c.WeightFixPreview.Strength, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(c.CanApplyWeightFix, Is.True);

            // 弱めて試したあと、適用の確認で「やめる」: 適用しない。50% の候補は窓に残り、「この候補を適用」で適用できる
            WeightFixPreview full = c.WeightFixPreview;
            _chooseAnswers.Enqueue(WeightFixChoice.TryWeaker);
            _confirmAnswers.Enqueue(false);
            window.OfferWeightFix(full);
            Assert.That(_chooseMessages.Count, Is.EqualTo(2));
            Assert.That(_confirmMessages.Count, Is.EqualTo(1));
            Assert.That(c.LastWeightFix, Is.Null, "やめたのに適用した");
            WeightFixPreview half = c.WeightFixPreview;
            Assert.That(half, Is.Not.SameAs(full));
            Assert.That(half.Strength, Is.EqualTo(0.5f).Within(1e-6f), "やめたあとに窓に残る候補が、50% のものでない");
            Assert.That(half.StrengthPercent, Is.EqualTo(50));
            Assert.That(c.CanApplyWeightFix, Is.True);
            Assert.That(c.Settings.WeightFixStrength, Is.EqualTo(1f), "設定の強さを変えてしまった");
            StringAssert.Contains(half.Describe(false), _confirmMessages[0]);

            // くわしく表示の「確認（試す）」（ask なし）は、伸びすぎても何も聞かない。設定の強さ（100%）で候補を作り直す
            window.PreviewWeightFixFromWindow(false);
            Assert.That(_chooseMessages.Count, Is.EqualTo(2), "確認（試す）で、選び方の確認が出た");
            Assert.That(_confirmMessages.Count, Is.EqualTo(1), "確認（試す）で、適用の確認が出た");
            Assert.That(c.LastWeightFix, Is.Null);
            WeightFixPreview again = c.WeightFixPreview;
            Assert.That(again, Is.Not.SameAs(half));
            Assert.That(again.Strength, Is.EqualTo(1f).Within(1e-6f), "確認（試す）は、設定の強さで作る");
            Assert.That(again.StretchesTooMuch, Is.True);

            // (b) このまま適用: 強さ 100% のまま適用する（適用の確認はもう一度出さない）
            _chooseAnswers.Enqueue(WeightFixChoice.ApplyAsIs);
            window.OfferWeightFix(again);
            Assert.That(_chooseMessages.Count, Is.EqualTo(3));
            Assert.That(_confirmMessages.Count, Is.EqualTo(1), "このまま適用なのに、適用の確認をもう一度出した");
            WeightFixReport report = c.LastWeightFix;
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Assert.That(report.Preview, Is.SameAs(again));
            Assert.That(report.Preview.Strength, Is.EqualTo(1f).Within(1e-6f), "このまま適用が、100% で適用されない");
            Assert.That(c.Settings.WeightFixStrength, Is.EqualTo(1f));
            StringAssert.StartsWith("ウェイトを体に寄せました（Ctrl+Z の 1 回で戻せます）。", window.ResultText);
            StringAssert.DoesNotContain("強さ 100%", FirstLine(window.ResultText));
        }

        // ---- (d) 伸びすぎない候補: 3つの選び方は出ない ----

        [Test]
        public void Tights_DoNotStretch_SoOnlyTheApplyQuestionIsAsked_AndTheStrengthOverrideBeatsTheSetting()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = StartWindow(root, TestAvatarMenu.TightsName);
            ClothEditController c = window.Controller;
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);

            _confirmAnswers.Enqueue(false);
            window.PreviewWeightFixFromWindow(true);

            WeightFixPreview preview = c.WeightFixPreview;
            Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
            Assert.That(preview.StretchesTooMuch, Is.False, "体にぴったりのタイツで、布が伸びすぎると知らせた");
            Assert.That(preview.CanTryWeaker, Is.False);
            Assert.That(_chooseMessages, Is.Empty, "伸びすぎない候補で、3つの選び方の確認を出した");
            Assert.That(_confirmMessages.Count, Is.EqualTo(1), "適用の確認が出ない");
            Assert.That(_confirmButtons[0], Is.EqualTo("GP02|適用|やめる"));
            StringAssert.Contains(preview.Describe(false), _confirmMessages[0]);
            StringAssert.EndsWith("この候補を適用しますか？（Ctrl+Z の 1 回で戻せます）", _confirmMessages[0]);
            StringAssert.DoesNotContain("弱めて", _confirmMessages[0]);
            StringAssert.DoesNotContain("強さ", FirstLine(_confirmMessages[0]), "強さを弱めていないので、1行目に強さは出ない");
            Assert.That(c.LastWeightFix, Is.Null, "やめたのに適用した");
            Assert.That(c.WeightFixPreview, Is.SameAs(preview), "やめても、候補は窓に残る");

            // コントローラーの強さの上書き: 設定の強さより優先して、その候補だけに使う。設定は変えない
            c.Settings.WeightFixStrength = 0.3f;
            WeightFixPreview overridden = c.PreviewWeightFix(0.5f);
            Assert.That(overridden, Is.Not.Null, c.LastWeightFixError);
            Assert.That(overridden.Strength, Is.EqualTo(0.5f).Within(1e-6f), "上書きした強さが、候補に入っていない");
            Assert.That(c.Settings.WeightFixStrength, Is.EqualTo(0.3f).Within(1e-6f), "上書きで設定の強さが変わった");
            Assert.That(overridden.ChangedVertices, Is.GreaterThan(0));
            StringAssert.Contains("強さ 50%・半径", overridden.ToString());
        }

        // ---- 聞き方の分かれ道（手作りの候補。動作チェックなし） ----

        [Test]
        public void OfferWeightFix_AsksTheThreeWayQuestion_OnlyWhenTheCandidateStretchesAndCanStillBeWeakened()
        {
            ClothEditWindow window = NewIdleWindow();

            // 伸びすぎて、強さ 100%: 3つの選び方（ここでは「やめる」を選ぶので、何も起きない）
            _chooseAnswers.Enqueue(WeightFixChoice.Cancel);
            window.OfferWeightFix(FakePreview(1f, 0.45f, 2f));
            Assert.That(_chooseMessages.Count, Is.EqualTo(1));
            Assert.That(_confirmMessages, Is.Empty);
            StringAssert.Contains("弱めて", _chooseMessages[0]);
            StringAssert.Contains("体から離れた布（スカートなど）が伸びすぎます。強さを 50% に弱めて、もう一度試せます。", _chooseMessages[0]);

            // 伸びすぎても、もう強さ 50%: 弱める余地が無いので、ふつうの確認（適用／やめる）。さらに下げる方法を言う
            window.OfferWeightFix(FakePreview(0.5f, 0.45f, 0.9f));
            Assert.That(_chooseMessages.Count, Is.EqualTo(1), "もう 50% の候補に、弱めて試すを出した");
            Assert.That(_confirmMessages.Count, Is.EqualTo(1));
            StringAssert.Contains("くわしく表示の『強さ』で、さらに下げられます", _confirmMessages[0]);
            StringAssert.EndsWith("この候補を適用しますか？（Ctrl+Z の 1 回で戻せます）", _confirmMessages[0]);

            // 伸びすぎない: ふつうの確認
            window.OfferWeightFix(FakePreview(1f, 0.45f, 0.5f));
            Assert.That(_chooseMessages.Count, Is.EqualTo(1));
            Assert.That(_confirmMessages.Count, Is.EqualTo(2));
            StringAssert.DoesNotContain("弱めて", _confirmMessages[1]);
            Assert.That(_confirmButtons, Is.All.EqualTo("GP02|適用|やめる"));
            Assert.That(_unsavedAsked, Is.EqualTo(0));
        }

        // ---- 確認の本文（手作りの候補。動作チェックなし） ----

        [Test]
        public void Messages_OfTheThreeDialogs_HaveTheDescription_TheStillStretchesLine_AndTheQuestion()
        {
            WeightFixPreview full = FakePreview(1f, 0.45f, 2f);

            // 3つの選び方の確認: 候補の説明のあとに、弱めて試せるという案内
            string offer = ClothEditPrompts.WeightFixOfferMessage(full, false);
            StringAssert.StartsWith(full.Describe(false), offer);
            StringAssert.EndsWith("\n\n体から離れた布（スカートなど）が伸びすぎます。強さを 50% に弱めて、もう一度試せます。", offer);

            // 弱めて試した結果で、まだ伸びる: 強さ → 候補の説明 → 「まだ伸びます」 → 適用するかの問い、の順
            WeightFixPreview still = FakePreview(0.5f, 0.45f, 0.9f);
            Assert.That(still.StretchesTooMuch, Is.True);
            string stillMessage = ClothEditPrompts.WeightFixWeakerResultMessage(still, false);
            StringAssert.StartsWith("強さ 50% で試した結果です。\n" + still.Describe(false), stillMessage);
            StringAssert.EndsWith("\nまだ伸びます。くわしく表示の『強さ』で、さらに下げられます。\n\nこの候補を適用しますか？（Ctrl+Z の 1 回で戻せます）", stillMessage);

            // 50% で和らいだ: 「まだ伸びます」は言わない
            WeightFixPreview calm = FakePreview(0.5f, 0.45f, 0.55f);
            Assert.That(calm.StretchesTooMuch, Is.False);
            string calmMessage = ClothEditPrompts.WeightFixWeakerResultMessage(calm, false);
            StringAssert.StartsWith("強さ 50% で試した結果です。\n", calmMessage);
            StringAssert.DoesNotContain("まだ伸びます", calmMessage);
            StringAssert.EndsWith(calm.Describe(false) + "\n\nこの候補を適用しますか？（Ctrl+Z の 1 回で戻せます）", calmMessage);

            // くわしく表示では、候補の説明が、くわしく表示のもの（さらに下げることを勧める一言）になる
            StringAssert.Contains("『強さ』をさらに下げて確認し直すと和らぎます", ClothEditPrompts.WeightFixWeakerResultMessage(still, true));
            StringAssert.Contains(full.Describe(true), ClothEditPrompts.WeightFixOfferMessage(full, true));

            // ふつうの適用の確認
            Assert.That(ClothEditPrompts.WeightFixApplyMessage("説明"), Is.EqualTo("説明\n\nこの候補を適用しますか？（Ctrl+Z の 1 回で戻せます）"));

            // 英語
            L10n.Language = UiLanguage.English;
            StringAssert.EndsWith("\n\nCloth away from the body (such as a skirt) stretches too much. You can lower the strength to 50% and try again.", ClothEditPrompts.WeightFixOfferMessage(full, false));
            string english = ClothEditPrompts.WeightFixWeakerResultMessage(still, false);
            StringAssert.StartsWith("This is the result with the strength at 50%.\n", english);
            StringAssert.Contains("\nIt still stretches. You can lower \"Strength\" further in the detailed view.", english);
            StringAssert.EndsWith("\n\nApply this candidate? (One Ctrl+Z undoes it)", english);
        }

        // ---- (e) 候補の説明: くわしく表示・かんたん表示の一言 ----

        [Test]
        public void Describe_ChangesTheStretchHint_ByTheViewAndTheStrength_AndShowsTheStrengthOnTheFirstLine()
        {
            // 強さ 100% で伸びすぎ: 1行目に強さは出ない
            WeightFixPreview full = FakePreview(1f, 0.45f, 2f);
            Assert.That(full.StretchesTooMuch, Is.True);
            Assert.That(full.CanTryWeaker, Is.True);
            Assert.That(full.IsBelowFullStrength, Is.False);
            Assert.That(full.IsAboveWeakerStrength, Is.True);
            string simple = full.Describe(false), detailed = full.Describe(true);
            StringAssert.DoesNotContain("強さ", FirstLine(simple));
            StringAssert.Contains("体から離れた所の布の伸び: 座る（最後まで）で 45% → 200%", simple);
            // かんたん表示（強さが 50% より大きい）: 確認の「弱めて試す」を勧める。強さのスライダーは無い
            StringAssert.Contains("体から離れた布（スカートなど）が、脚などに貼り付いて伸びます。確認で『弱めて（50%）試す』を選ぶと和らぎます。", simple);
            StringAssert.DoesNotContain("さらに下げられます", simple);
            // くわしく表示: 「強さ」を下げて確認し直す
            StringAssert.Contains("『強さ』を下げて確認し直すと和らぎます（50% くらい）", detailed);
            StringAssert.DoesNotContain("弱めて", detailed);

            // 強さ 50% で、まだ伸びる: 1行目に「強さ 50%」。かんたん表示は、くわしく表示の「強さ」でさらに下げる
            WeightFixPreview half = FakePreview(0.5f, 0.45f, 0.9f);
            Assert.That(half.StretchesTooMuch, Is.True);
            Assert.That(half.CanTryWeaker, Is.False, "もう 50% なので、弱めて試すは出さない");
            Assert.That(half.IsBelowFullStrength, Is.True);
            Assert.That(half.IsAboveWeakerStrength, Is.False);
            StringAssert.Contains("強さ 50%", FirstLine(half.Describe(false)));
            StringAssert.Contains("強さ 50%", FirstLine(half.Describe(true)));
            StringAssert.Contains("くわしく表示の『強さ』で、さらに下げられます", half.Describe(false));
            StringAssert.DoesNotContain("弱めて（50%）試す", half.Describe(false));
            // くわしく表示でも、もう 50% 以下なら、「50% くらい」とは言わずに、さらに下げることを勧める
            StringAssert.Contains("『強さ』をさらに下げて確認し直すと和らぎます", half.Describe(true));
            StringAssert.DoesNotContain("50% くらい", half.Describe(true));

            // 強さ 80%（弱めたが 50% より大きい）で伸びすぎ: 1行目に強さが出て、かんたん表示はまた「弱めて試す」を勧められる
            WeightFixPreview most = FakePreview(0.8f, 0.45f, 2f);
            StringAssert.Contains("強さ 80%", FirstLine(most.Describe(false)));
            Assert.That(most.CanTryWeaker, Is.True);
            StringAssert.Contains("確認で『弱めて（50%）試す』を選ぶと和らぎます", most.Describe(false));

            // 伸びすぎない: 一言は出ない
            WeightFixPreview calm = FakePreview(1f, 0.45f, 0.5f);
            Assert.That(calm.StretchesTooMuch, Is.False);
            Assert.That(calm.CanTryWeaker, Is.False);
            foreach (string text in new[] { calm.Describe(false), calm.Describe(true) })
            {
                StringAssert.DoesNotContain("貼り付いて伸びます", text);
                StringAssert.DoesNotContain("弱めて", text);
                StringAssert.DoesNotContain("さらに下げ", text);
            }

            // 記録用の ToString は変えない（強さと、一言を含まない）
            StringAssert.Contains("/ 強さ 100%・半径", full.ToString());
            StringAssert.Contains("/ 強さ 50%・半径", half.ToString());
            StringAssert.DoesNotContain("弱めて", full.ToString());
            StringAssert.DoesNotContain("さらに下げ", half.ToString());
        }

        [Test]
        public void Describe_AndTheButtons_AreAlsoInEnglish()
        {
            L10n.Language = UiLanguage.English;
            WeightFixPreview full = FakePreview(1f, 0.45f, 2f);
            WeightFixPreview half = FakePreview(0.5f, 0.45f, 0.9f);
            StringAssert.Contains("Choosing \"Try Weaker (50%)\" in the confirmation eases this.", full.Describe(false));
            StringAssert.Contains("Lowering \"Strength\" (to about 50%) and checking again eases this.", full.Describe(true));
            StringAssert.Contains("You can lower \"Strength\" further in the detailed view.", half.Describe(false));
            StringAssert.Contains("Lowering \"Strength\" further and checking again eases this.", half.Describe(true));
            StringAssert.Contains(", strength 50%", FirstLine(half.Describe(false)));
            StringAssert.DoesNotContain("strength", FirstLine(full.Describe(false)));

            Assert.That(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.TryWeaker), Is.EqualTo("Try Weaker (50%)"));
            Assert.That(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.Cancel), Is.EqualTo("Cancel"));
            Assert.That(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.ApplyAsIs), Is.EqualTo("Apply As Is"));
            L10n.Language = UiLanguage.Japanese;
            Assert.That(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.TryWeaker), Is.EqualTo("弱めて（50%）試す"));
            Assert.That(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.Cancel), Is.EqualTo("やめる"));
            Assert.That(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.ApplyAsIs), Is.EqualTo("このまま適用"));
        }

        // ---- ダイアログの形 ----

        [Test]
        public void Prompt_HasADefault_AndTheButtonsMapToTheChoices()
        {
            Assert.That(_savedChoose, Is.Not.Null, "既定の確認が設定されていない");
            Assert.That(WeightFixPreview.WeakerStrength, Is.EqualTo(0.5f));
            Assert.That(WeightFixPreview.WeakerPercent, Is.EqualTo(50));

            // DisplayDialogComplex: 0 が1つ目（弱めて試す）、1 が2つ目（やめる）、2 が3つ目（このまま適用）
            Assert.That(ClothEditPrompts.WeightFixChoiceFromButton(0), Is.EqualTo(WeightFixChoice.TryWeaker));
            Assert.That(ClothEditPrompts.WeightFixChoiceFromButton(1), Is.EqualTo(WeightFixChoice.Cancel));
            Assert.That(ClothEditPrompts.WeightFixChoiceFromButton(2), Is.EqualTo(WeightFixChoice.ApplyAsIs));
        }
    }
}
