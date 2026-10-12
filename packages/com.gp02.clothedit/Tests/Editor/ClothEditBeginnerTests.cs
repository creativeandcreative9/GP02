using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using L10n = GP02.EditorTools.L10n;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 初心者の立場での批評（20261011_初心者の立場での批評.md）のうち、布の編集ウィンドウとシーンビューの項目の確認。
    /// 小さな札（A1）・Unity の道具のハンドルを隠す（A2）・シーンの案内（A3）・手順の1行（A4）・服の付け根を自動でピン留め（A5）・
    /// 見出しの保存（A6）・伸びの言葉（B2）・貫通の自動の調べ直し（B3）・保存直後の注意（B4）・保存したシェイプキーの重み（B5）・届かないときの一言（B9）・
    /// アバター全体を選んだとき（D1）・体を選んだとき（D2）・ピンの色（D3）・通知の短さ（C3）。
    /// 画面の描画を使う確認（札の「×」のクリックなど）は、ClothEditWindowGuiSmokeTests.Beginner にある。
    /// </summary>
    public class ClothEditBeginnerTests
    {
        const string TempFolder = "Assets/GP02_BeginnerTestTemp";
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        readonly List<ClothEditWindow> _windows = new List<ClothEditWindow>();
        double _time = 100.0;
        UiLanguage _savedLanguage;
        ClothEditViewMode _savedView;
        bool _savedHidden;
        bool _hadShownKey;
        bool _shownKeyValue;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.Japanese;
            _savedView = ClothEditView.Mode;
            ClothEditView.Mode = ClothEditViewMode.Simple;
            _savedHidden = Tools.hidden;
            Tools.hidden = false;
            _hadShownKey = EditorPrefs.HasKey(ClothEditHelp.ShownOncePrefKey);
            _shownKeyValue = EditorPrefs.GetBool(ClothEditHelp.ShownOncePrefKey, false);
            EditorPrefs.DeleteKey(ClothEditHelp.ShownOncePrefKey);
            Selection.activeObject = null;
        }

        [TearDown]
        public void TearDown()
        {
            for (int k = _windows.Count - 1; k >= 0; k--)
            {
                if (_windows[k] != null) Object.DestroyImmediate(_windows[k]);
            }
            _windows.Clear();
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            Selection.activeObject = null;
            for (int k = _created.Count - 1; k >= 0; k--)
            {
                Object o = _created[k];
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            Tools.hidden = _savedHidden;
            L10n.Language = _savedLanguage;
            ClothEditView.Mode = _savedView;
            if (_hadShownKey) EditorPrefs.SetBool(ClothEditHelp.ShownOncePrefKey, _shownKeyValue);
            else EditorPrefs.DeleteKey(ClothEditHelp.ShownOncePrefKey);
        }

        // ---- 部品 ----

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

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
            Selection.activeObject = null;
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
            var c = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 6f }) { BrushRadius = 0.04f, InfluenceRadius = 0.3f, SaveFolder = TempFolder, DataFolder = TempFolder };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(targets, out string error), Is.True, error);
            return c;
        }

        ClothEditWindow NewWindow()
        {
            var window = ScriptableObject.CreateInstance<ClothEditWindow>();
            _windows.Add(window);
            window.Controller.SaveFolder = TempFolder;
            window.Controller.DataFolder = TempFolder;
            return window;
        }

        double NextTime()
        {
            _time += 0.016;
            return _time;
        }

        void Settle(ClothEditController c)
        {
            for (int i = 0; i < 1500 && c.IsBusy; i++) c.Tick(NextTime());
            Assert.That(c.IsBusy, Is.False, "仕上げが終わりません");
        }

        static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.That(field, Is.Not.Null, name);
            return field.GetValue(target);
        }

        static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        static object Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name, Private);
            Assert.That(method, Is.Not.Null, name);
            return method.Invoke(target, args);
        }

        // 閉じた球（極から極まで）の MeshRenderer
        MeshRenderer CreateSphere(string name, Transform parent = null)
        {
            const int around = 20, rings = 10;
            var vertices = new List<Vector3> { new Vector3(0f, 0.15f, 0f) };
            for (int r = 1; r < rings; r++)
            {
                double polar = Math.PI * r / rings;
                for (int a = 0; a < around; a++)
                {
                    double angle = 2.0 * Math.PI * a / around;
                    vertices.Add(new Vector3((float)(Math.Sin(polar) * Math.Cos(angle)) * 0.15f, (float)Math.Cos(polar) * 0.15f, (float)(Math.Sin(polar) * Math.Sin(angle)) * 0.15f));
                }
            }
            vertices.Add(new Vector3(0f, -0.15f, 0f));
            int bottom = vertices.Count - 1;
            var triangles = new List<int>();
            for (int a = 0; a < around; a++) triangles.AddRange(new[] { 0, 1 + a, 1 + (a + 1) % around });
            for (int r = 0; r < rings - 2; r++)
            {
                for (int a = 0; a < around; a++)
                {
                    int i = 1 + r * around + a, j = 1 + r * around + (a + 1) % around;
                    triangles.AddRange(new[] { i, i + around, j, j, i + around, j + around });
                }
            }
            for (int a = 0; a < around; a++) triangles.AddRange(new[] { bottom - around + a, bottom, bottom - around + (a + 1) % around });
            Mesh mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            GameObject go = Track(new GameObject(name));
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        // ---- A1: 小さな札 ----

        [Test]
        public void StartCard_ListsTheThreeThingsToRemember_AndPointsToTheFullList()
        {
            var lines = new List<string>();
            ClothEditHelp.GetStartCardLines(lines);
            Assert.That(lines, Is.EqualTo(new[] { "ドラッグ: 布をつまむ", "Alt+ドラッグ: 視点を回す", "Ctrl+Z: 1つ戻す" }));
            Assert.That(ClothEditHelp.StartCardMore(), Is.EqualTo("F1: すべての操作"));

            L10n.Language = UiLanguage.English;
            ClothEditHelp.GetStartCardLines(lines);
            Assert.That(lines.Count, Is.EqualTo(3));
            Assert.That(lines[0], Does.StartWith("Drag").And.Not.Contain("ドラッグ"));
            Assert.That(ClothEditHelp.StartCardMore(), Does.StartWith("F1").And.Not.Contain("すべて"));
        }

        [Test]
        public void StartCard_ShowsAtFirstEditWithoutOpeningTheLongList_AndStaysClosedAfterTheCross()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.StartCardVisible, Is.False, "編集していないときは出さない");
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True, window.Controller.LastError);

            Assert.That(window.OverlayShortcutsVisible, Is.False, "約30行の一覧は、自動では出さない");
            Assert.That(window.StartCardVisible, Is.True, "初めての編集では、小さな札が出る");
            Assert.That(EditorPrefs.GetBool(ClothEditHelp.ShownOncePrefKey, false), Is.False, "出しただけでは、閉じたことにしない");

            // 全部の一覧を出している間は、小さな札は引っ込む
            window.OverlaySetShortcutsVisible(true);
            Assert.That(window.OverlayShortcutsVisible, Is.True);
            Assert.That(window.StartCardVisible, Is.False);
            window.OverlaySetShortcutsVisible(false);
            Assert.That(window.StartCardVisible, Is.True);

            // 「×」で閉じたら、次からは出さない
            window.DismissStartCard();
            Assert.That(window.StartCardVisible, Is.False);
            Assert.That(EditorPrefs.GetBool(ClothEditHelp.ShownOncePrefKey, false), Is.True);
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            Assert.That(window.StartCardVisible, Is.False, "同じウィンドウで編集を始め直しても出ない");

            ClothEditWindow another = NewWindow();
            Assert.That(another.BeginFromRenderers(new List<Renderer> { Find(root, "Tights") }), Is.True);
            Assert.That(another.StartCardVisible, Is.False, "ウィンドウを開き直しても出ない（EditorPrefs に残る）");
            Assert.That(another.OverlayShortcutsVisible, Is.False);
        }

        [Test]
        public void FullShortcutList_StillOpensAndClosesTheSameWay_AndHasItsOwnCloseLine()
        {
            ClothEditWindow window = NewWindow();
            Assert.That(window.OverlayShortcutsVisible, Is.False);
            window.OverlaySetShortcutsVisible(true);
            Assert.That(window.OverlayShortcutsVisible, Is.True, "全部の一覧が出ているか、の意味は変えない");
            window.OverlaySetShortcutsVisible(false);
            Assert.That(window.OverlayShortcutsVisible, Is.False);

            var lines = new List<KeyValuePair<string, string>>();
            ClothEditHelp.GetShortcutLines(lines);
            Assert.That(lines.Count, Is.GreaterThan(25), "全部の一覧は、今までどおり長い");
            Assert.That(lines.Exists(l => l.Key == "F1" && l.Value.Contains("×")), Is.True, "× でも閉じられることを、一覧に書いてある");
        }

        // ---- A2: Unity の道具のハンドル ----

        [Test]
        public void ToolHandles_AreHiddenWhileEditing_AndRestoredWhenEditingEnds()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(Tools.hidden, Is.False);
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            Assert.That(Tools.hidden, Is.True, "編集中は、Unity の移動の矢印などを隠す");
            Assert.That(ClothEditToolHandles.IsHidingFor(window), Is.True);

            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(Tools.hidden, Is.False, "編集を終えたら、元の値へ戻す");
            Assert.That(ClothEditToolHandles.IsHiding, Is.False);
        }

        [Test]
        public void ToolHandles_GoBackToTheOriginalValue_EvenIfTheyWereAlreadyHidden()
        {
            Tools.hidden = true;   // ほかの理由で隠していた
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            Assert.That(Tools.hidden, Is.True);
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(Tools.hidden, Is.True, "元が「隠している」なら、そのまま隠した状態に戻す");
            Tools.hidden = false;
        }

        [Test]
        public void ToolHandles_AreRestoredWhenTheWindowCloses()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            Assert.That(Tools.hidden, Is.True);

            Object.DestroyImmediate(window);   // ウィンドウを閉じる（OnDisable）
            Assert.That(Tools.hidden, Is.False, "ウィンドウを閉じるときに戻す");
            Assert.That(ClothEditToolHandles.IsHiding, Is.False);
        }

        [Test]
        public void ToolHandles_AreRestoredAtPlayModeAndBeforeAScriptReload()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            Assert.That(Tools.hidden, Is.True);

            Invoke(window, "OnPlayModeStateChanged", PlayModeStateChange.ExitingEditMode);
            Assert.That(window.Controller.IsEditing, Is.False, "プレイモードに入る前に、編集は終わる");
            Assert.That(Tools.hidden, Is.False, "プレイモードに入る前に戻す");

            window.Controller.Dispose();
            ClothEditWindow second = NewWindow();
            Assert.That(second.BeginFromRenderers(new List<Renderer> { Find(root, "Tights") }), Is.True);
            Assert.That(Tools.hidden, Is.True);
            Invoke(second, "OnBeforeAssemblyReload");
            Assert.That(Tools.hidden, Is.False, "スクリプトの再読み込みの前に戻す（コンパイルをまたいで隠したままにしない）");
        }

        [Test]
        public void ToolHandles_FollowTheEditWhenItEndsWithoutTheEndButton()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            Assert.That(Tools.hidden, Is.True);

            window.Controller.End();   // Undo や対象の削除などで、ウィンドウの外から編集が終わった
            Invoke(window, "OnUpdate");
            Assert.That(Tools.hidden, Is.False, "毎回の更新で、編集中かどうかに追いつく");
            Invoke(window, "OnUpdate");
            Assert.That(Tools.hidden, Is.False);
        }

        [Test]
        public void ToolHandles_AnotherOwnerCannotRestoreWhatTheWindowHid()
        {
            var owner = new object();
            var other = new object();
            Tools.hidden = false;
            ClothEditToolHandles.Hide(owner);
            Assert.That(Tools.hidden, Is.True);
            ClothEditToolHandles.Hide(other);               // すでに隠しているので、何もしない
            ClothEditToolHandles.Restore(other);            // 持ち主でないので、戻さない
            Assert.That(Tools.hidden, Is.True);
            ClothEditToolHandles.Sync(owner, false);
            Assert.That(Tools.hidden, Is.False);
            ClothEditToolHandles.Sync(owner, false);        // 続けて呼んでも、変わらない
            Assert.That(Tools.hidden, Is.False);
            ClothEditToolHandles.Sync(null, true);          // 持ち主が無ければ何もしない
            Assert.That(Tools.hidden, Is.False);
        }

        // ---- A3: シーンの案内 ----

        [Test]
        public void SceneHint_IsOneShortLine_WithTheMostImportantOperationAndTheHelpKey()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                foreach (ClothEditMode mode in new[] { ClothEditMode.Grab, ClothEditMode.Rotate, ClothEditMode.Scale, ClothEditMode.Pin, ClothEditMode.Unpin, ClothEditMode.Relax })
                {
                    string hint = ClothEditHelp.GetHint(mode, false, false);
                    string[] lines = hint.Split('\n');
                    Assert.That(lines.Length, Is.EqualTo(1), $"{mode} は1行: {hint}");
                    Assert.That(hint, Does.Contain(ClothEditHelp.ToolName(mode)).And.Contain("F1"), hint);
                    Assert.That(hint.Length, Is.LessThanOrEqualTo(language == UiLanguage.Japanese ? 52 : 90), "短い文: " + hint);
                }
                foreach (int count in new[] { 0, 73 })
                {
                    string[] lines = ClothEditHelp.GetHint(ClothEditMode.Select, false, false, count).Split('\n');
                    Assert.That(lines.Length, Is.EqualTo(2), "選んで動かすは最大2行");
                    Assert.That(lines[1], Does.Contain("F1"));
                }
                Assert.That(ClothEditHelp.GetHint(ClothEditMode.Select, false, false, 73), Does.Contain("73"));
                foreach (ClothEditMode mode in new[] { ClothEditMode.Grab, ClothEditMode.Rotate, ClothEditMode.Scale, ClothEditMode.Select })
                {
                    Assert.That(ClothEditHelp.GetHint(mode, true, false, 5), Does.Contain("Esc"), "つまんでいる間は、取り消しのキー");
                }
                Assert.That(ClothEditHelp.GetHint(ClothEditMode.Grab, false, true), Does.Contain("Esc"));
            }
            L10n.Language = UiLanguage.Japanese;
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Pin, false, false, 0, false), Does.Contain("Ctrl+ドラッグで外す"));
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Pin, false, false, 0, true), Does.Contain("Ctrl+ドラッグで留める"), "「塗ると外す」がオンなら、意味が逆");
        }

        // ---- A4: 手順の1行 ----

        [Test]
        public void GuideNextLine_NamesTheCurrentStep_AndSaysWhenEverythingIsDone()
        {
            var states = new GuideStepState[ClothEditGuide.StepCount];
            // 編集を始めた直後（ピンもまだ）: 今の手順は 3
            ClothEditGuide.Evaluate(true, true, false, false, false, states);
            Assert.That(ClothEditGuide.CurrentStep(states), Is.EqualTo(ClothEditGuide.PinStep));
            Assert.That(ClothEditGuide.NextLine(states), Is.EqualTo("次: 3. 動かさない所をピン留め"), "任意の印は付けない");
            // ピンを留めたら 4
            ClothEditGuide.Evaluate(true, true, true, false, false, states);
            Assert.That(ClothEditGuide.NextLine(states), Is.EqualTo("次: 4. つまんで引く"));
            // 引いたら 5
            ClothEditGuide.Evaluate(true, true, true, true, false, states);
            Assert.That(ClothEditGuide.NextLine(states), Is.EqualTo("次: 5. シェイプキーとして保存"));
            // 全部済んだら
            ClothEditGuide.Evaluate(true, true, true, true, true, states);
            Assert.That(ClothEditGuide.CurrentStep(states), Is.EqualTo(-1));
            Assert.That(ClothEditGuide.NextLine(states), Is.EqualTo("✓ 手順はすべて済みました"));
            // ピンを飛ばして引いた: 手順3は飛ばしたことになり、今の手順は 5
            ClothEditGuide.Evaluate(true, true, false, true, false, states);
            Assert.That(ClothEditGuide.NextLine(states), Is.EqualTo("次: 5. シェイプキーとして保存"));

            L10n.Language = UiLanguage.English;
            ClothEditGuide.Evaluate(true, true, false, false, false, states);
            Assert.That(ClothEditGuide.NextLine(states), Is.EqualTo("Next: 3. Pin what should not move"));
            ClothEditGuide.Evaluate(true, true, true, true, true, states);
            Assert.That(ClothEditGuide.NextLine(states), Does.StartWith("✓").And.Contain("done"));
        }

        [Test]
        public void Guide_StepThree_MentionsTheAutoPinButton_AndTheSaveButton()
        {
            Assert.That(ClothEditGuide.Detail(ClothEditGuide.PinStep), Does.Contain("服の付け根（ウエスト・首まわりなど）を自動でピン留め"), "ボタンと同じ名前");
            Assert.That(ClothEditGuide.Detail(4), Does.Contain("保存"));
            Assert.That(ClothEditGuide.Detail(ClothEditGuide.PinStep), Does.Not.Contain("矩形"));
        }

        // ---- A5: 服の付け根を自動でピン留め ----

        [Test]
        public void PinAnchors_Skirt_PinsTheWaistRingAllTheWayAround_AddsToExistingPins_AndOneUndoRestores()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, "Skirt"));
            // 裾に1か所、手でピン留めしてある（選んでピン留め。Undo の記録が残る操作で）
            var hem = new List<int>();
            c.Session.CollectInSphere(new Vector3(0f, TestAvatarMenu.SkirtBottomY + 0.01f, -TestAvatarMenu.SkirtBottomRadius), 0.02f, -1, hem);
            Assert.That(hem.Count, Is.GreaterThan(0));
            c.SetSelection(hem);
            Assert.That(c.PinSelected(true), Is.GreaterThan(0));
            int existing = c.Session.PinnedCount;
            Assert.That(existing, Is.EqualTo(hem.Count));

            AnchorPinResult result = c.PinAnchors();
            Assert.That(result.WasFound, Is.True);
            Assert.That(result.Rings, Is.EqualTo(1), "スカートは1つの部品");
            Assert.That(result.Pinned, Is.GreaterThan(80), "ウエストの周りが一周ぶん留まる: " + result.Pinned);
            Assert.That(c.Session.PinnedCount, Is.EqualTo(existing + result.Pinned), "今のピンに足す（消さない）");

            // 留まった粒子は、ウエストの上の縁の近く（2cm）で、前後左右のどこにもある
            var pinned = new List<int>();
            c.Session.GetPinnedParticles(pinned);
            NVector3[] positions = c.Session.Solver.Positions.ToArray();
            int atWaist = 0, front = 0, back = 0, left = 0, right = 0;
            foreach (int i in pinned)
            {
                NVector3 p = positions[i];
                if (p.Y < TestAvatarMenu.SkirtBottomY + 0.05f) continue;   // 手で留めた裾
                atWaist++;
                Assert.That(p.Y, Is.GreaterThan(TestAvatarMenu.SkirtTopY - 0.025f), $"ウエストから 2cm より下の粒子 {i}: {p}");
                if (p.Z > 0.05f) front++;
                if (p.Z < -0.05f) back++;
                if (p.X > 0.05f) right++;
                if (p.X < -0.05f) left++;
            }
            Assert.That(atWaist, Is.EqualTo(result.Pinned), "新しく留めたのは、すべてウエスト");
            Assert.That(Math.Min(Math.Min(front, back), Math.Min(left, right)), Is.GreaterThan(result.Pinned / 8), $"一周している（前 {front} 後 {back} 左 {left} 右 {right}）");

            // 1回の Undo で、足したピンだけが消える（手で留めたピンは残る）
            c.UndoLast();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(existing), "1回の Undo で戻る");
            c.RedoLast();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(existing + result.Pinned), "Redo でまた留まる");

            // もう一度押しても、増えない（見つかった数は同じ）
            AnchorPinResult again = c.PinAnchors();
            Assert.That(again.Pinned, Is.EqualTo(0));
            Assert.That(again.Found, Is.EqualTo(result.Found));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(existing + result.Pinned));
        }

        [Test]
        public void PinAnchors_Tights_PinsOnlyTheWaist_AndTheSleevesTheirShoulderEnds()
        {
            GameObject root = CreateAvatar();
            ClothEditController tights = Start(Find(root, "Tights"));
            AnchorPinResult t = tights.PinAnchors();
            Assert.That(t.WasFound, Is.True);
            NVector3[] positions = tights.Session.Solver.Positions.ToArray();
            float top = positions.Max(p => p.Y);
            var pinned = new List<int>();
            tights.Session.GetPinnedParticles(pinned);
            Assert.That(pinned.Count, Is.EqualTo(t.Pinned));
            Assert.That(pinned.All(i => positions[i].Y > top - 0.03f), Is.True, "足首や脚は留めない。ウエストだけ");
            tights.End();

            ClothEditController sleeves = Start(Find(root, "Sleeves"));
            AnchorPinResult s = sleeves.PinAnchors();
            Assert.That(s.Rings, Is.EqualTo(2), "左右の袖は別の部品");
            positions = sleeves.Session.Solver.Positions.ToArray();
            pinned.Clear();
            sleeves.Session.GetPinnedParticles(pinned);
            Assert.That(pinned.Count, Is.GreaterThan(30));
            Assert.That(pinned.All(i => Math.Abs(positions[i].X) < TestAvatarMenu.ElbowX), Is.True, "袖口（手首の側）ではなく、肩の側");
            Assert.That(pinned.Any(i => positions[i].X > 0f) && pinned.Any(i => positions[i].X < 0f), Is.True, "左右どちらの袖も");
        }

        [Test]
        public void PinAnchors_AClosedShape_PinsNothing_AndLeavesNoUndoStep()
        {
            MeshRenderer sphere = CreateSphere("Ball");
            ClothEditController c = Start(sphere);
            c.Session.PaintPins(new Vector3(0f, 0.15f, 0f), 0.02f, true);
            int existing = c.Session.PinnedCount;

            AnchorPinResult result = c.PinAnchors();
            Assert.That(result.WasFound, Is.False);
            Assert.That(result.Pinned, Is.EqualTo(0));
            Assert.That(result.OpenParts, Is.EqualTo(0), "開いた縁が無い");
            Assert.That(c.Session.PinnedCount, Is.EqualTo(existing));
        }

        [Test]
        public void AnchorFrame_UsesTheAnimatorRoot_OrWorldUpAndTheBoundsCenter()
        {
            GameObject root = CreateAvatar();
            root.transform.position = new Vector3(1f, 0.5f, 2f);
            ClothEditController c = Start(Find(root, "Skirt"));
            c.GetAnchorFrame(out Vector3 up, out Vector3 axisPoint);
            Assert.That(Vector3.Distance(up, Vector3.up), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(axisPoint, root.transform.position), Is.LessThan(1e-5f), "アバターの根元（Animator のある親）の位置");
            Assert.That(ClothEditSymmetry.FindAnimatorRoot(Find(root, "Skirt").transform), Is.SameAs(root.transform));
            c.End();

            // 回したアバター: 高さの向きもアバターの上向き
            root.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            ClothEditController rotated = Start(Find(root, "Skirt"));
            rotated.GetAnchorFrame(out Vector3 rotatedUp, out _);
            Assert.That(Vector3.Distance(rotatedUp, root.transform.up), Is.LessThan(1e-5f));
            rotated.End();

            // Animator が無い: 世界の上向きと、衣装の境界の中心
            MeshRenderer sphere = CreateSphere("Loose");
            sphere.transform.position = new Vector3(0.3f, 1f, -0.2f);
            ClothEditController loose = Start(sphere);
            loose.GetAnchorFrame(out Vector3 looseUp, out Vector3 center);
            Assert.That(ClothEditSymmetry.FindAnimatorRoot(sphere.transform), Is.Null);
            Assert.That(looseUp, Is.EqualTo(Vector3.up));
            Assert.That(Vector3.Distance(center, sphere.transform.position), Is.LessThan(0.01f), "衣装の境界の中心");
        }

        [Test]
        public void AutoPinButton_TellsTheResultShortly_AndKeepsTheDetailsInTheWindow()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);

            AnchorPinResult result = window.AutoPinAnchors();
            Assert.That(result.Pinned, Is.GreaterThan(0));
            Assert.That(window.LastToast, Is.EqualTo($"ピン留めしました（{result.Pinned:N0} 点）"));
            Assert.That(window.LastToast.Length, Is.LessThanOrEqualTo(16));
            Assert.That(window.ResultText, Does.Contain("付け根").And.Contain("ピン留め"), "詳しい文は窓の中に残す");

            window.AutoPinAnchors();
            Assert.That(window.LastToast, Is.EqualTo("すでにピン留めしてあります"));

            MeshRenderer sphere = CreateSphere("Ball2");
            window.RequestEnd();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { sphere }), Is.True);
            window.AutoPinAnchors();
            Assert.That(window.LastToast, Is.EqualTo("付け根が見つかりません"));
            Assert.That(window.ResultText, Does.Contain("閉じた形"));
        }

        // ---- A6: 見出しの「保存」 ----

        [Test]
        public void HeaderSave_SavesWithTheNameInTheNameBox_AndOverwritesWhenReEditingFromTheList()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { skirt }), Is.True);
            Assert.That(GetField(window, "_shapeName"), Is.EqualTo("GP02_1"), "名前の欄には、自動で名前が入る");
            SetField(window, "_shapeName", "MyHem");

            Assert.That(window.SaveFromHeader(), Is.True, window.Controller.LastError);
            Mesh saved = window.Controller.LastSavedMesh;
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved.GetBlendShapeIndex("MyHem"), Is.GreaterThanOrEqualTo(0), "名前の欄の名前で保存される");
            Assert.That(window.LastToast, Is.EqualTo("保存しました"));
            Assert.That(window.ResultText, Does.Contain("MyHem"), "詳しい文（保存した場所）は窓に残る");
            Assert.That(GetField(window, "_shapeName"), Is.EqualTo("GP02_1"), "保存したあとは、次の名前になる");

            // 一覧から「布で編集」で始めたときは、同じ名前で上書き保存する（新しいシェイプキーは増えない）
            GP02ShapeKeys component = skirt.GetComponent<GP02ShapeKeys>();
            Assert.That(component, Is.Not.Null);
            ShapeKeyEntry entry = component.Data.FindEntry("MyHem");
            Assert.That(entry, Is.Not.Null);
            Assert.That(ShapeKeyClothEdit.Begin(window.Controller, component, entry, out string message), Is.True, message);
            int entries = component.Data.Entries.Count;
            Assert.That(window.SaveFromHeader(), Is.True);
            Assert.That(component.Data.Entries.Count, Is.EqualTo(entries), "同じ名前で上書き");
            Assert.That(component.Data.FindEntry("MyHem"), Is.Not.Null);
            Assert.That(window.LastToast, Is.EqualTo("保存しました"));
            ShapeKeyClothEdit.Clear();
        }

        // ---- B2: 伸びの言葉と色 ----

        [Test]
        public void StretchLevel_FollowsTheHeatmapBorders_WithWordsAndColors()
        {
            Assert.That(ClothEditHelp.GetStretchLevel(0f), Is.EqualTo(ClothEditHelp.StretchLevel.None));
            Assert.That(ClothEditHelp.GetStretchLevel(ClothEditSettings.HeatmapYellow - 1e-4f), Is.EqualTo(ClothEditHelp.StretchLevel.None));
            Assert.That(ClothEditHelp.GetStretchLevel(ClothEditSettings.HeatmapYellow), Is.EqualTo(ClothEditHelp.StretchLevel.Slight), "黄色の境目から「少し」");
            Assert.That(ClothEditHelp.GetStretchLevel(ClothEditSettings.HeatmapRed - 1e-4f), Is.EqualTo(ClothEditHelp.StretchLevel.Slight));
            Assert.That(ClothEditHelp.GetStretchLevel(ClothEditSettings.HeatmapRed), Is.EqualTo(ClothEditHelp.StretchLevel.Large), "赤の境目から「大きい」");
            Assert.That(ClothEditHelp.GetStretchLevel(5.551f), Is.EqualTo(ClothEditHelp.StretchLevel.Large), "555% のような数字も「大きい」");
            Assert.That(ClothEditHelp.GetStretchLevel(float.NaN), Is.EqualTo(ClothEditHelp.StretchLevel.None));

            Assert.That(ClothEditHelp.StretchLevelName(ClothEditHelp.StretchLevel.None), Is.EqualTo("ほぼ無し"));
            Assert.That(ClothEditHelp.StretchLevelName(ClothEditHelp.StretchLevel.Slight), Is.EqualTo("少し"));
            Assert.That(ClothEditHelp.StretchLevelName(ClothEditHelp.StretchLevel.Large), Is.EqualTo("大きい"));
            foreach (bool dark in new[] { true, false })
            {
                Color green = ClothEditHelp.StretchLevelColor(ClothEditHelp.StretchLevel.None, dark);
                Color yellow = ClothEditHelp.StretchLevelColor(ClothEditHelp.StretchLevel.Slight, dark);
                Color red = ClothEditHelp.StretchLevelColor(ClothEditHelp.StretchLevel.Large, dark);
                Assert.That(green.g, Is.GreaterThan(green.r).And.GreaterThan(green.b), "緑");
                Assert.That(red.r, Is.GreaterThan(red.g).And.GreaterThan(red.b), "赤");
                Assert.That(yellow.r, Is.GreaterThan(yellow.b).And.GreaterThan(0.4f), "黄");
                Assert.That(yellow.g, Is.GreaterThan(yellow.b), "黄");
            }
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditHelp.StretchLevelName(ClothEditHelp.StretchLevel.Slight), Is.EqualTo("slight"));
        }

        // ---- B9: 届かないときの一言 ----

        [Test]
        public void ReachMessage_SaysWhatTheSimpleViewCanDo_AndMentionsStretchinessOnlyInTheDetailedView()
        {
            string simple = ClothEditReach.Message(false);
            Assert.That(simple, Does.Contain("布の長さが足りず、ここで止まりました").And.Contain("選んで動かす（Q）").And.Contain("形だけ動かす"));
            Assert.That(simple, Does.Not.Contain("伸びやすさ"), "かんたん表示に無い設定は言わない");
            string detailed = ClothEditReach.Message(true);
            Assert.That(detailed, Does.Contain("形だけ動かす").And.Contain("伸びやすさ"));
            Assert.That(ClothEditReach.Message(), Is.EqualTo(simple), "既定はかんたん表示の文");
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditReach.Message(false), Does.Contain("Shape only").And.Not.Contain("Stretchiness"));
            Assert.That(ClothEditReach.Message(true), Does.Contain("Stretchiness"));
        }

        // ---- B4: 保存直後の注意 ----

        // シェイプキー "Fat"（重み 40）と、GP02 が保存した "GP02_1"（重み 100）を持つ SkinnedMeshRenderer
        SkinnedMeshRenderer CreateWithShapes(string name, out GP02ShapeKeyData data, string prefix = "", string suffix = "")
        {
            var vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0.1f, 0f), new Vector3(0.1f, 0.1f, 0f) };
            Mesh mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            var deltas = new[] { Vector3.zero, Vector3.zero, new Vector3(0f, 0f, -0.01f), Vector3.zero };
            mesh.AddBlendShapeFrame("Fat", 100f, deltas, null, null);
            mesh.AddBlendShapeFrame(prefix + "GP02_1" + suffix, 100f, deltas, null, null);
            GameObject go = Track(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            data = Track(ScriptableObject.CreateInstance<GP02ShapeKeyData>());
            data.VertexCount = vertices.Length;
            data.SourceMesh = mesh;
            data.Entries.Add(new ShapeKeyEntry { Name = "GP02_1" });
            GP02ShapeKeys component = go.AddComponent<GP02ShapeKeys>();
            component.Data = data;
            component.NamePrefix = prefix;
            component.NameSuffix = suffix;
            return renderer;
        }

        [Test]
        public void WeightWarning_SkipsTheShapeKeysGp02Saved_AndOnlyCountsTheOthers()
        {
            SkinnedMeshRenderer renderer = CreateWithShapes("Saved", out _);
            var items = new List<TargetWarning>();

            // 保存したシェイプキー（GP02_1）が 100 でも、注意は出ない
            renderer.SetBlendShapeWeight(1, 100f);
            ClothEditWarnings.Collect(renderer, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.False, "GP02 が保存したシェイプキーは数えない");

            // 自分のものではないシェイプキー（Fat）が 40 なら、注意が出る。保存したものは数えない
            renderer.SetBlendShapeWeight(0, 40f);
            ClothEditWarnings.Collect(renderer, items);
            TargetWarning weights = items.Find(w => w.Kind == TargetWarningKind.BlendShapeWeights);
            Assert.That(weights.Message, Does.Contain("1 個").And.Contain("Fat").And.Not.Contain("GP02_1"));
            Assert.That(weights.Fix, Is.EqualTo(TargetWarningFix.ResetBlendShapeWeights));
        }

        [Test]
        public void WeightWarning_KnowsTheNamesWithPrefixAndSuffix()
        {
            SkinnedMeshRenderer renderer = CreateWithShapes("Decorated", out _, "Mine_", "_v2");
            renderer.SetBlendShapeWeight(1, 100f);
            var items = new List<TargetWarning>();
            ClothEditWarnings.Collect(renderer, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.False, "前置き・後置きを付けた名前も、GP02 のもの");

            var names = new List<string>();
            ClothEditWarnings.CollectSavedShapeNames(renderer, names);
            Assert.That(names, Does.Contain("GP02_1").And.Contain("Mine_GP02_1_v2"));
            ClothEditWarnings.CollectSavedShapeNames(null, names);
        }

        [Test]
        public void WeightWarning_IsOnlyForBeforeEditing_NotForTheDiagnosticsWhileEditing()
        {
            Assert.That(ClothEditWarnings.IsShownWhileEditing(TargetWarningKind.BlendShapeWeights), Is.False);
            Assert.That(ClothEditWarnings.IsShownWhileEditing(TargetWarningKind.LikelyBody), Is.False);
            foreach (TargetWarningKind kind in new[] { TargetWarningKind.NoPins, TargetWarningKind.Penetration, TargetWarningKind.MissingBones, TargetWarningKind.NoBurst, TargetWarningKind.SharedMesh })
            {
                Assert.That(ClothEditWarnings.IsShownWhileEditing(kind), Is.True, kind.ToString());
            }

            // 窓: 編集を始める前は出て、編集中は出ない（体型の調整の重みが残っている衣装）
            SkinnedMeshRenderer renderer = CreateWithShapes("Adjusted", out _);
            renderer.SetBlendShapeWeight(0, 40f);
            ClothEditWindow window = NewWindow();
            window.SetTargets(new List<Renderer> { renderer });
            Assert.That(window.CurrentDiagnostics().Any(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.True, "編集を始める前の点検には出る");
            Assert.That(window.BeginFromRenderers(new List<Renderer> { renderer }), Is.True, window.Controller.LastError);
            Assert.That(window.CurrentDiagnostics().Any(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.False, "編集中の診断には出さない");
        }

        [Test]
        public void SavingAShapeKey_DoesNotRaiseTheWeightWarning()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { skirt }), Is.True);
            Assert.That(window.SaveFromHeader(), Is.True, window.Controller.LastError);
            int index = window.Controller.LastSavedMesh.GetBlendShapeIndex("GP02_1");
            Assert.That(skirt.GetBlendShapeWeight(index), Is.EqualTo(100f), "保存すると、重みは 100 になる");

            var items = new List<TargetWarning>();
            ClothEditWarnings.Collect(skirt, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.False, "保存した直後の点検にも出ない");
            Assert.That(window.CurrentDiagnostics().Any(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.False);
            window.RequestEnd();
            Assert.That(window.CurrentDiagnostics().Any(w => w.Kind == TargetWarningKind.BlendShapeWeights), Is.False, "編集を終えたあとにも出ない");
        }

        // ---- B5: 保存したシェイプキーの重み ----

        [Test]
        public void SavedKeyList_ShowsTheRealWeightInWords_AndTheOverlaySliderIsDetailedOnly()
        {
            Assert.That(ClothEditView.Shows(ClothEditUiItem.SavedKeyList, ClothEditViewMode.Simple), Is.True);
            Assert.That(ClothEditView.Shows(ClothEditUiItem.SavedKeyOverlay, ClothEditViewMode.Simple), Is.False, "かんたん表示では、重ねて見るスライダーをやめる");
            Assert.That(ClothEditView.Shows(ClothEditUiItem.SavedKeyOverlay, ClothEditViewMode.Detailed), Is.True, "くわしく表示に移す");

            Assert.That(ClothEditWindow.AppliedWeightLabel(true, 100f), Is.EqualTo("反映中（100）"));
            Assert.That(ClothEditWindow.AppliedWeightLabel(true, 0f), Is.EqualTo("反映していない（0）"));
            Assert.That(ClothEditWindow.AppliedWeightLabel(true, 37.5f), Is.EqualTo("反映中（37.5）"));
            Assert.That(ClothEditWindow.AppliedWeightLabel(false, 0f), Does.Contain("反映していない").And.Contain("未書き出し"));
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditWindow.AppliedWeightLabel(true, 100f), Is.EqualTo("Applied (100)"));

            // レンダラーの実際の重みを読む（インスペクターの重みと同じ）
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { skirt }), Is.True);
            Assert.That(window.SaveFromHeader(), Is.True, window.Controller.LastError);
            GP02ShapeKeys component = skirt.GetComponent<GP02ShapeKeys>();
            ShapeKeyEntry entry = component.Data.FindEntry("GP02_1");
            Assert.That(window.TryGetAppliedWeight(component, entry, out float weight), Is.True);
            Assert.That(weight, Is.EqualTo(100f), "インスペクターでは 100");
            skirt.SetBlendShapeWeight(skirt.sharedMesh.GetBlendShapeIndex("GP02_1"), 0f);
            Assert.That(window.TryGetAppliedWeight(component, entry, out weight), Is.True);
            Assert.That(weight, Is.EqualTo(0f));
            Assert.That(window.TryGetAppliedWeight(component, new ShapeKeyEntry { Name = "NotInTheMesh" }, out _), Is.False, "メッシュに無いものは、反映していない");
        }

        // ---- D1: アバター全体を選んだとき ----

        [Test]
        public void ChoosingTheWholeAvatar_ListsTheOutfitsInside_WithTheBodyLastAndMarked()
        {
            GameObject root = CreateAvatar();
            Assert.That(ClothEditController.FindRenderer(root), Is.Null, "アバターの根元には、衣装が無い");
            Assert.That(ClothEditStartMenu.FindCloth(new[] { root }), Is.Empty);
            Assert.That(ClothEditStartMenu.CanStart(new[] { root }), Is.True, "中に衣装があれば、右クリックのメニューも使える");

            List<ClothCandidate> candidates = ClothEditStartMenu.FindCandidates(new[] { root });
            Assert.That(candidates.Select(c => c.Renderer.name), Is.EqualTo(new[] { "Skirt", "Tights", "Sleeves", "Body" }), "体らしいものは後ろ");
            Assert.That(candidates.Select(c => c.LikelyBody), Is.EqualTo(new[] { false, false, false, true }));
            Assert.That(candidates.All(c => c.IsActive), Is.True);

            // 衣装そのものを選んだときは、一覧は要らない（そのまま対象になる）
            Assert.That(ClothEditStartMenu.FindCandidates(new[] { Find(root, "Skirt").gameObject }), Is.Empty);
            Assert.That(ClothEditStartMenu.FindCandidates(null), Is.Empty);
            Assert.That(ClothEditStartMenu.FindCandidates(new GameObject[] { null }), Is.Empty);

            // 非表示の衣装も候補に入れる
            Find(root, "Tights").gameObject.SetActive(false);
            candidates = ClothEditStartMenu.FindCandidates(new[] { root });
            Assert.That(candidates.Select(c => c.Renderer.name), Does.Contain("Tights"));
            Assert.That(candidates.First(c => c.Renderer.name == "Tights").IsActive, Is.False);
        }

        [Test]
        public void ChoosingTheWholeAvatar_ShowsTheListInTheWindow_NotADialog_AndPressingOneMakesItTheTarget()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Selection.activeGameObject = root;
            Assert.That(window.Candidates.Select(c => c.Renderer.name), Is.EqualTo(new[] { "Skirt", "Tights", "Sleeves", "Body" }));
            Assert.That(window.Targets, Is.Empty, "アバター全体を選んでも、対象は変わらない");

            // 「選択中のオブジェクトから設定」: ダイアログにせず、窓の中で案内する（ダイアログが出ると、テストが失敗する）
            Invoke(window, "PickFromSelection");
            Assert.That(window.PickHint, Does.Contain("この中の衣装"));
            Assert.That(window.Targets, Is.Empty);

            // 一覧の衣装を押すと、対象になる（選択は変えない。変えると、一覧が消えて切り替えにくい）
            window.PickCandidate(Find(root, "Tights"));
            Assert.That(window.Targets.Select(r => r.name), Is.EqualTo(new[] { "Tights" }));
            Assert.That(window.PickHint, Is.Empty);
            Assert.That(Selection.activeGameObject, Is.SameAs(root));
            Assert.That(window.Candidates.Count, Is.EqualTo(4), "切り替えられるよう、一覧は残る");
            window.PickCandidate(Find(root, "Sleeves"));
            Assert.That(window.Targets.Select(r => r.name), Is.EqualTo(new[] { "Sleeves" }));

            // 編集を始めたら、一覧は出さない
            Assert.That(window.BeginFromRenderers(window.Targets.ToList()), Is.True);
            Assert.That(window.Candidates, Is.Empty);
            window.PickCandidate(Find(root, "Skirt"));
            Assert.That(window.Targets.Select(r => r.name), Is.EqualTo(new[] { "Sleeves" }), "編集中は、対象を変えない");
        }

        [Test]
        public void SelectionFromSelection_WithNothingUsable_ExplainsInTheWindow()
        {
            ClothEditWindow window = NewWindow();
            Selection.activeObject = null;
            Invoke(window, "PickFromSelection");
            Assert.That(window.PickHint, Does.Contain("選ばれていません"));

            GameObject empty = Track(new GameObject("Empty"));
            Selection.activeGameObject = empty;
            Invoke(window, "PickFromSelection");
            Assert.That(window.PickHint, Does.Contain("Empty").And.Contain("編集できる衣装がありません"));
            Assert.That(window.Candidates, Is.Empty);
        }

        [Test]
        public void StartingFromTheRightClickMenu_OnTheWholeAvatar_OpensTheListWithoutStartingToEdit()
        {
            GameObject root = CreateAvatar();
            // 衣装そのものでなくても、メニューは使える。始めるのではなく、窓に一覧を出す（ここでは窓を作らずに、判定だけ確かめる）
            Assert.That(ClothEditStartMenu.CanStart(new[] { root }), Is.True);
            Assert.That(ClothEditStartMenu.CanStart(new[] { Track(new GameObject("Plain")) }), Is.False);
            Assert.That(ClothEditStartMenu.FindOutfitsIn(root, null).Select(r => r.name), Is.EqualTo(new[] { "Skirt", "Tights", "Sleeves" }), "体らしいものは、衣装の候補に入れない");
            Assert.That(ClothEditStartMenu.FindOutfitsIn(root, Find(root, "Tights")).Select(r => r.name), Is.EqualTo(new[] { "Skirt", "Sleeves" }), "除く指定");
            Assert.That(ClothEditStartMenu.FindOutfitsIn(null, null), Is.Empty);
        }

        // ---- D2: 体を選んだとき ----

        [Test]
        public void ChoosingTheBody_WarnsBeforeEditing_ListsOutfits_AndDoesNotStopEditing()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer body = Find(root, "Body");
            var items = new List<TargetWarning>();
            ClothEditWarnings.Collect(body, items);
            TargetWarning warning = items.Find(w => w.Kind == TargetWarningKind.LikelyBody);
            Assert.That(warning.Message, Does.Contain("「Body」は体のメッシュのようです").And.Contain("服を編集するときは、服のオブジェクトを選んでください"));
            Assert.That(warning.Severity, Is.EqualTo(MessageType.Warning), "かんたん表示でも見える重さ");
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.CannotStart), Is.False, "編集は止めない");

            foreach (string clothName in new[] { "Skirt", "Tights", "Sleeves" })
            {
                ClothEditWarnings.Collect(Find(root, clothName), items);
                Assert.That(items.Exists(w => w.Kind == TargetWarningKind.LikelyBody), Is.False, clothName + " は服");
            }

            ClothEditWindow window = NewWindow();
            window.SetTargets(new List<Renderer> { body });
            IReadOnlyList<TargetWarning> diagnostics = window.CurrentDiagnostics();
            Assert.That(diagnostics.Any(w => w.Kind == TargetWarningKind.LikelyBody), Is.True, "編集を始める前の診断に出る");
            Assert.That(Invoke(window, "OutfitsFor", body), Is.InstanceOf<List<Renderer>>());
            Assert.That(((List<Renderer>)Invoke(window, "OutfitsFor", body)).Select(r => r.name), Is.EqualTo(new[] { "Skirt", "Tights", "Sleeves" }), "衣装の候補の一覧");

            // 体のままでも、編集は始められる。始めたら、診断には出ない
            Assert.That(window.BeginFromRenderers(new List<Renderer> { body }), Is.True, window.Controller.LastError);
            Assert.That(window.CurrentDiagnostics().Any(w => w.Kind == TargetWarningKind.LikelyBody), Is.False, "編集中の診断には出さない");
        }

        [Test]
        public void TheBodyGuess_AlsoUsesTheExpressionShapeKeys()
        {
            var vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0.1f, 0f) };
            Mesh mesh = Track(new Mesh { name = "FaceMesh" });
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 2, 1 };
            foreach (string shape in new[] { "vrc.v_sil", "vrc.v_pp", "Blink" }) mesh.AddBlendShapeFrame(shape, 100f, new Vector3[3], null, null);
            GameObject go = Track(new GameObject("Mesh_A"));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            Assert.That(ClothEditStartMenu.IsLikelyBody(renderer, out string reason), Is.True);
            Assert.That(reason, Does.Contain("表情"));
            var items = new List<TargetWarning>();
            ClothEditWarnings.Collect(renderer, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.LikelyBody), Is.True);
        }

        // ---- D3: ピンの色 ----

        [Test]
        public void PinColor_IsBlue_AndDiffersFromThePenetrationSelectedAndBrushRangeColors()
        {
            Color pin = ClothEditHelp.PinColor;
            Assert.That(pin.b, Is.GreaterThan(0.8f).And.GreaterThan(pin.r + 0.4f), "青系");
            Assert.That(pin.a, Is.EqualTo(1f));
            Assert.That(ClothEditHelp.PinToolColor.b, Is.GreaterThan(ClothEditHelp.PinToolColor.r + 0.3f));

            float Distance(Color a, Color b) => Mathf.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));
            Assert.That(Distance(pin, ClothEditWindow.PenetrationColor), Is.GreaterThan(0.8f), "貫通の赤い点と見分けられる");
            Assert.That(Distance(pin, ClothEditHelp.SelectedColor), Is.GreaterThan(0.8f), "選んだ点の橙と見分けられる");
            // ブラシの範囲の点（重み 0 は薄い青、1 は黄）。色も、濃さ（半透明）も違う
            foreach (float weight in new[] { 0f, 0.5f, 1f })
            {
                Color range = ClothEditHelp.WeightColor(weight);
                Assert.That(Distance(pin, new Color(range.r, range.g, range.b)), Is.GreaterThan(0.3f), $"範囲の点（重み {weight}）");
            }
            Assert.That(ClothEditHelp.WeightColor(0f).a, Is.LessThan(0.5f), "範囲の点は、端が半透明");
            Assert.That(Distance(pin, ClothEditHelp.SelectionWeightColor(0f)), Is.GreaterThan(0.3f));
        }

        // ---- C3: 通知の短さ ----

        [Test]
        public void Toasts_AreShort_AndTheDetailsStayInTheWindow()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);

            // 左右の中心を合わせる（詳しい文は、窓の結果の欄）
            Invoke(window, "FitMirrorPlane");
            Assert.That(window.LastToast.Length, Is.LessThanOrEqualTo(16), window.LastToast);
            Assert.That(window.ResultText.Length, Is.GreaterThan(window.LastToast.Length), "詳しい文を窓に残す");

            // 今の形を左右対称にする
            Invoke(window, "SymmetrizeShape");
            Assert.That(window.LastToast.Length, Is.LessThanOrEqualTo(16), window.LastToast);
            Assert.That(window.ResultText, Is.Not.Empty);

            // 動きの入っていない数値の適用
            Invoke(window, "ApplyNumericMove");
            Assert.That(window.LastToast.Length, Is.LessThanOrEqualTo(16), window.LastToast);

            // 保存・付け根のピン留めは、別のテストで確かめている
            window.SaveFromHeader();
            Assert.That(window.LastToast.Length, Is.LessThanOrEqualTo(16), window.LastToast);
        }

        [Test]
        public void ResultBox_IsClearedWhenANewEditStarts()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = NewWindow();
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, "Skirt") }), Is.True);
            window.AutoPinAnchors();
            Assert.That(window.ResultText, Is.Not.Empty);
            Assert.That(window.RequestEnd(), Is.True);
            Assert.That(window.ResultText, Is.Empty, "編集を終えたら消える");
        }

        // ---- B3: 貫通の自動の調べ直し ----

        GameObject CreateClothWithBody(float hipBig)
        {
            GameObject root = TestClothMenu.CreateInScene(null, false);
            _created.Add(root);
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                _created.Add(renderer.sharedMesh);
                foreach (Material m in renderer.sharedMaterials) _created.Add(m);
                _created.Add(renderer);
            }
            SkinnedMeshRenderer body = Find(root, "Body");
            body.SetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName), hipBig);
            Selection.activeObject = null;
            return root;
        }

        // スカートの裾をつまんで、手前（カメラの方）へ少し引いて、仕上げる
        void PullHem(ClothEditController c)
        {
            c.Mode = ClothEditMode.Grab;
            var ray = new Ray(new Vector3(0.1f, 0.5f, 1f), Vector3.back);
            Assert.That(c.PointerDown(ray, Vector3.back), Is.True, "布に当たらない");
            for (int k = 1; k <= 10; k++)
            {
                c.PointerDrag(new Ray(new Vector3(0.1f + 0.01f * k, 0.5f, 1f), Vector3.back));
                c.Tick(NextTime());
            }
            c.PointerUp();
            Settle(c);
        }

        [Test]
        public void AutoPenetrationCheck_RefreshesTheResultAfterEveryOperation_SoItIsNeverStale()
        {
            GameObject root = CreateClothWithBody(100f);
            ClothEditController c = Start(Find(root, "Skirt"));
            Assert.That(c.HasBody, Is.True);
            Assert.That(c.IsAutoPenetrationCheck, Is.True);
            Assert.That(c.LastPenetration.PenetratingCount, Is.GreaterThan(50));
            Assert.That(c.NeedsPenetrationRefresh, Is.False);

            // ピン留めを変える（突き抜けている粒子のうち、ピン留めの数が変わる）
            c.InvertPins();
            Assert.That(c.NeedsPenetrationRefresh, Is.False, "ピンの数が変わっても、確定のたびに調べ直す");
            Assert.That(c.LastPenetration.PinnedCount, Is.GreaterThan(0), "突き抜けている粒子がピン留めになったことが、結果に出る");
            c.ClearPins();
            Assert.That(c.LastPenetration.PinnedCount, Is.EqualTo(0));
            Assert.That(c.NeedsPenetrationRefresh, Is.False);

            // 貫通を直す → Undo → Redo
            int before = c.LastPenetration.PenetratingCount;
            Assert.That(c.FixPenetration(), Is.EqualTo(0));
            Assert.That(c.NeedsPenetrationRefresh, Is.False);
            c.UndoLast();
            Assert.That(c.NeedsPenetrationRefresh, Is.False, "Undo のあと");
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(before));
            c.RedoLast();
            Assert.That(c.NeedsPenetrationRefresh, Is.False, "Redo のあと");
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0));

            // すべて元に戻す
            c.ResetAll();
            Assert.That(c.NeedsPenetrationRefresh, Is.False);
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(before), "元の形に戻したので、また突き抜けている");

            // つまむ（仕上げが終わったとき）
            PullHem(c);
            Assert.That(c.IsPenetrationStale, Is.False, "操作を確定したあとに、自動で調べ直している");
            Assert.That(c.NeedsPenetrationRefresh, Is.False);
            int auto = c.LastPenetration.PenetratingCount;
            Assert.That(c.DetectPenetration().PenetratingCount, Is.EqualTo(auto), "自動で調べた結果は、今の形で調べた結果と同じ");

            // 余白を変えても、手を止めている間に調べ直す
            c.Settings.PenetrationMargin = 0.004f;
            c.ApplySettings();
            Assert.That(c.NeedsPenetrationRefresh, Is.True, "余白が変わった");
            c.Tick(NextTime());
            Assert.That(c.NeedsPenetrationRefresh, Is.False);
            Assert.That(c.LastPenetration.Margin, Is.EqualTo(0.004f).Within(1e-6f));
        }

        [Test]
        public void AutoPenetrationCheck_AlsoCatchesAShapeChangeThatWasNotAnOperationCommit_OnTheNextTick()
        {
            GameObject root = CreateClothWithBody(0f);
            ClothEditController c = Start(Find(root, "Skirt"));
            // 外へ逃がしたのを取り消したときのように、確定を通らずに形が変わった
            c.Session.UpdateCurrentShape();
            Assert.That(c.IsPenetrationStale, Is.True);
            c.Tick(NextTime());
            Assert.That(c.IsPenetrationStale, Is.False, "手が止まっていれば、次の Tick で調べ直す");

            // つまんでいる間は調べ直さない
            c.Mode = ClothEditMode.Grab;
            Assert.That(c.PointerDown(new Ray(new Vector3(0.1f, 0.5f, 1f), Vector3.back), Vector3.back), Is.True);
            c.PointerDrag(new Ray(new Vector3(0.15f, 0.5f, 1f), Vector3.back));
            c.Tick(NextTime());
            Assert.That(c.IsBusy, Is.True);
            c.Session.UpdateCurrentShape();
            c.Tick(NextTime());
            Assert.That(c.IsPenetrationStale, Is.True, "ドラッグ中は、調べ直さない");
            c.PointerUp();
            Settle(c);
            Assert.That(c.IsPenetrationStale, Is.False, "仕上げが終わったら、調べ直している");
        }

        [Test]
        public void AutoPenetrationCheck_StopsWhenDetectingTakesLongerThanAThirdOfASecondTwiceInARow()
        {
            GameObject root = CreateClothWithBody(100f);
            ClothEditController c = Start(Find(root, "Skirt"));
            Assert.That(ClothEditController.AutoPenetrationMaxSeconds, Is.EqualTo(0.3).Within(1e-9));

            c.NoteAutoPenetrationTime(0.5);
            Assert.That(c.IsAutoPenetrationCheck, Is.True, "1回だけ遅くても、続ける（たまたまの遅れ）");
            c.NoteAutoPenetrationTime(0.01);
            c.NoteAutoPenetrationTime(0.5);
            Assert.That(c.IsAutoPenetrationCheck, Is.True, "速い回があれば、数え直す");
            c.NoteAutoPenetrationTime(0.31);
            Assert.That(c.IsAutoPenetrationCheck, Is.False, "0.3 秒より長くかかる回が2回続いたら、自動をやめる");

            // 自動をやめたら、今までどおり「古い」表示になり、「調べ直す」で新しくなる
            PullHem(c);
            Assert.That(c.IsPenetrationStale, Is.True);
            Assert.That(c.NeedsPenetrationRefresh, Is.True);
            Assert.That(ClothEditWindow.PenetrationStatus(c.LastPenetration, true), Does.Contain("調べ直す"));
            c.RecheckPenetration();
            Assert.That(c.IsPenetrationStale, Is.False);
            Assert.That(c.NeedsPenetrationRefresh, Is.False);
        }

        [Test]
        public void AutoPenetrationCheck_IsOffWithoutABody_AndTheQuietLineSaysSo()
        {
            MeshRenderer sphere = CreateSphere("Alone");
            ClothEditController c = Start(sphere);
            Assert.That(c.HasBody, Is.False);
            Assert.That(c.IsAutoPenetrationCheck, Is.False);
            Assert.That(c.NeedsPenetrationRefresh, Is.False);
            Assert.That(ClothEditWindow.QuietPenetrationLine(c.LastPenetration), Does.Contain("体が見つからない"));

            // 体が無いときは、形が変わっても「古い」とは表示しない（調べるものが無い）
            Assert.That(ClothEditWindow.ShowsStaleNotice(c.LastPenetration, true), Is.False);
            Assert.That(ClothEditWindow.ShowsStaleNotice(null, true), Is.False, "まだ調べていないときも、古いとは言わない");
        }

        [Test]
        public void QuietPenetrationLine_SaysNoPenetrationInOneLine_AndTheStatusStaysWhenStale()
        {
            GameObject root = CreateClothWithBody(0f);
            ClothEditController c = Start(Find(root, "Skirt"));
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0));
            Assert.That(ClothEditWindow.QuietPenetrationLine(c.LastPenetration), Is.EqualTo("✓ 体との貫通なし"));
            Assert.That(ClothEditWindow.QuietPenetrationLine(null), Does.Contain("まだ調べていません"));
            Assert.That(ClothEditWindow.PenetrationStatus(c.LastPenetration, false), Does.Not.Contain("調べたあとに"));
            string stale = ClothEditWindow.PenetrationStatus(c.LastPenetration, true);
            Assert.That(stale, Does.Contain("調べたあとに形かすき間が変わっています"));
            Assert.That(ClothEditWindow.ShowsStaleNotice(c.LastPenetration, true), Is.True, "体があれば、古いときに表示する");
            Assert.That(ClothEditWindow.ShowsStaleNotice(c.LastPenetration, false), Is.False);
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditWindow.QuietPenetrationLine(c.LastPenetration), Does.StartWith("✓").And.Contain("No body penetration"));
        }
    }
}
