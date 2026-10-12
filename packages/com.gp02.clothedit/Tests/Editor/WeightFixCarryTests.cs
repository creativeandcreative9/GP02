using System;
using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;
using NVector3 = System.Numerics.Vector3;

namespace GP02.Tests
{
    /// <summary>
    /// 「ウェイトを体に寄せる」「ウェイトの変更を外す」が編集を作り直すとき、保存していない形を、メッシュ空間の差分で引き継ぐこと。
    /// 保存の確認は出ず、引き継いだ形は保存していない編集のまま残り、適用のあとの動作チェックの結果は、確認のときの結果がそのまま出る。
    /// Ctrl+Z の1回で、ウェイトと一緒に戻る（形は消えない）。引き継いだあとの別の操作を Ctrl+Z しても、引き継いだ形に戻る。
    /// 保存した形を読み込む（LoadDeltasAsOperation）と保存済みの形になるので、ここでは使わず、布をつまんで引いて保存していない編集を作る。
    /// </summary>
    public class WeightFixCarryTests
    {
        const string TempFolder = "Assets/GP02_WeightFixCarryTestTemp";

        // 引き継いだ形のメッシュ空間の差分が、引き継ぐ前とどれだけ違ってよいか（メートル。1回引き継ぐごと）。
        // 「シェイプキーを布で再編集」の往復のテスト（ShapeKeyLibraryTests.Session_GetThenLoadMeshSpaceDeltasReproducesTheShape）の、差分の許容と同じ。
        // 2回引き継ぐ（適用して、Ctrl+Z で戻す）ときは、その 1.5 倍まで
        const float DeltaTolerance = 0.004f;

        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        readonly List<ClothEditWindow> _windows = new List<ClothEditWindow>();
        double _time = 100.0;

        UiLanguage _savedLanguage;
        ClothEditViewMode _savedView;
        bool _savedHidden;
        Func<ClothEditLeaveReason, ClothEditUnsavedChoice> _savedAsk;
        Func<string, string, string, string, bool> _savedConfirm;
        int _unsavedAsked;

        [SetUp]
        public void SetUp()
        {
            _unsavedAsked = 0;
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.Japanese;
            _savedView = ClothEditView.Mode;
            ClothEditView.Mode = ClothEditViewMode.Simple;
            _savedHidden = Tools.hidden;
            Tools.hidden = false;
            _savedAsk = ClothEditPrompts.AskUnsaved;
            _savedConfirm = ClothEditPrompts.Confirm;
            // 聞かれたら数える（聞かれないことを確かめる）。聞かれたときは「やめる」にして、適用が止まるようにする
            ClothEditPrompts.AskUnsaved = reason =>
            {
                _unsavedAsked++;
                return ClothEditUnsavedChoice.Cancel;
            };
            ClothEditPrompts.Confirm = (title, message, ok, cancel) => true;
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            ClothEditPrompts.AskUnsaved = _savedAsk;
            ClothEditPrompts.Confirm = _savedConfirm;
            foreach (ClothEditWindow window in _windows)
            {
                if (window != null) Object.DestroyImmediate(window);
            }
            _windows.Clear();
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
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

        // 大きく動くポーズ（座る・しゃがむ）のひざも直す範囲に入れる（WeightFixTests と同じ）
        ClothEditController Start(Renderer target)
        {
            var c = new ClothEditController(new ClothEditSettings { FixLargePoses = true })
            {
                BrushRadius = 0.05f, InfluenceRadius = 0.12f, RecordToData = true, SaveFolder = TempFolder, DataFolder = TempFolder, ShowProgress = false,
            };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(new[] { target }, out string error), Is.True, error);
            return c;
        }

        double NextTime()
        {
            _time += 0.05;
            return _time;
        }

        // 左脚の太ももの前をつまんで、体の外（左）へ 4cm 引いて、仕上げる（保存していない編集になる）。ひざは動かさない
        void PullThigh(ClothEditController c, float y = 0.65f, float toLeft = 0.04f)
        {
            c.Mode = ClothEditMode.Grab;
            float x = -TestAvatarMenu.LegX;
            Assert.That(c.PointerDown(new Ray(new Vector3(x, y, 1f), Vector3.back), Vector3.back), Is.True, "布に当たらない");
            const int steps = 10;
            for (int k = 1; k <= steps; k++)
            {
                c.PointerDrag(new Ray(new Vector3(x - toLeft * k / steps, y, 1f), Vector3.back));
                c.Tick(NextTime());
            }
            c.PointerUp();
            for (int i = 0; i < 1500 && c.IsBusy; i++) c.Tick(NextTime());
            Assert.That(c.IsBusy, Is.False, "仕上げが終わりません");
        }

        // 保存していない編集のある所まで進めて、動作チェックと確認までする
        WeightFixPreview PullThenPreview(ClothEditController c)
        {
            PullThigh(c);
            Assert.That(c.HasUnsavedEdits, Is.True, "引いたのに、保存していない編集が無い");
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            Assert.That(c.WeightCausedRegionCount, Is.GreaterThan(0), "ひざの貫通が、ウェイトが原因と分からない");
            WeightFixPreview preview = c.PreviewWeightFix();
            Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
            Assert.That(c.CanApplyWeightFix, Is.True);
            return preview;
        }

        static float MaxDistance(Vector3[] a, Vector3[] b, out float largest)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length));
            float max = 0f;
            largest = 0f;
            for (int v = 0; v < a.Length; v++)
            {
                max = Mathf.Max(max, Vector3.Distance(a[v], b[v]));
                largest = Mathf.Max(largest, a[v].magnitude);
            }
            return max;
        }

        // hops は、差分を引き継いだ回数（引き継ぐたびに、粒子への補間の誤差が少し足される）
        static void AssertSameDeltas(Vector3[] expected, Vector3[] actual, string what, int hops = 1)
        {
            float max = MaxDistance(expected, actual, out float largest);
            Debug.Log($"[GP02] 引き継いだ差分の誤差（{what}）: 最大 {max * 1000f:F2} mm（差分の最大の大きさ {largest * 1000f:F1} mm、{hops} 回）");
            Assert.That(max, Is.LessThan(DeltaTolerance * (1f + 0.5f * (hops - 1))), what);
        }

        static NVector3[] Positions(ClothEditSession session)
        {
            ReadOnlySpan<NVector3> positions = session.Solver.Positions;
            var copy = new NVector3[positions.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = positions[i];
            return copy;
        }

        // ---- 引き継ぐ ----

        [Test]
        public void Apply_CarriesTheUnsavedShape_KeepsThePoseCheckFresh_AndOneUndoRestoresTheWeightsKeepingTheShape()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer tights = Find(root, TestAvatarMenu.TightsName);
            Mesh original = tights.sharedMesh;
            ClothEditController c = Start(tights);
            WeightFixPreview preview = PullThenPreview(c);
            Vector3[] before = c.Session.GetMeshSpaceDeltas();
            float largest = 0f;
            foreach (Vector3 d in before) largest = Mathf.Max(largest, d.magnitude);
            Assert.That(largest, Is.GreaterThan(0.01f), "引いた形が小さすぎて、テストにならない");
            Undo.IncrementCurrentGroup();

            WeightFixReport report = c.ApplyWeightFix();
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Assert.That(_unsavedAsked, Is.EqualTo(0), "コントローラーは、保存の確認を出さない");
            Assert.That(report.CarriedUnsaved, Is.True);
            Assert.That(report.DiscardedUnsaved, Is.False);
            Assert.That(tights.sharedMesh, Is.Not.SameAs(original), "ウェイトを入れたメッシュに差し替わる");

            // 保存していない編集のまま残る（保存済みの印は付けない）。形は、適用の前とほぼ同じ
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.GetSourceMesh(0), Is.SameAs(tights.sharedMesh));
            Assert.That(c.HasUnsavedEdits, Is.True, "引き継いだ形が、保存済みになった");
            Vector3[] carried = c.Session.GetMeshSpaceDeltas();
            AssertSameDeltas(before, carried, "適用のあと");
            Assert.That(c.LastStretch, Is.EqualTo(c.Session.Solver.MeasureMaxStretch()).Within(1e-6f));

            // 動作チェックの結果は、確認のときのもの（候補で調べ直した結果）がそのまま今の結果
            Assert.That(c.IsPoseCheckStale, Is.False, "適用のあとに、動作チェックの結果が古くなった");
            Assert.That(c.LastPoseCheck, Is.SameAs(preview.After));
            Assert.That(c.WeightCausedRegionCount, Is.EqualTo(preview.WeightRegionsAfter));

            // Ctrl+Z の1回で、ウェイトが戻る（作り直される）。保存していない形は残る
            c.UndoLast();
            Assert.That(tights.sharedMesh, Is.SameAs(original), "ウェイトが戻らない");
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.GetSourceMesh(0), Is.SameAs(original), "元のメッシュで編集を作り直していない");
            Assert.That(c.HasUnsavedEdits, Is.True, "Ctrl+Z で、保存していない形が消えた");
            AssertSameDeltas(before, c.Session.GetMeshSpaceDeltas(), "Ctrl+Z のあと", 2);
            Assert.That(_unsavedAsked, Is.EqualTo(0));
        }

        [Test]
        public void AfterCarrying_AnotherOperationThenCtrlZ_GoesBackToTheCarriedShape()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.TightsName));
            PullThenPreview(c);
            Assert.That(c.ApplyWeightFix(), Is.Not.Null, c.LastWeightFixError);
            NVector3[] carried = Positions(c.Session);
            Vector3[] carriedDeltas = c.Session.GetMeshSpaceDeltas();

            // 別の操作（右脚の太ももを引く）をして、形が変わる
            c.Mode = ClothEditMode.Grab;
            Assert.That(c.PointerDown(new Ray(new Vector3(TestAvatarMenu.LegX, 0.65f, 1f), Vector3.back), Vector3.back), Is.True);
            for (int k = 1; k <= 10; k++)
            {
                c.PointerDrag(new Ray(new Vector3(TestAvatarMenu.LegX + 0.004f * k, 0.65f, 1f), Vector3.back));
                c.Tick(NextTime());
            }
            c.PointerUp();
            for (int i = 0; i < 1500 && c.IsBusy; i++) c.Tick(NextTime());
            NVector3[] moved = Positions(c.Session);
            float change = 0f;
            for (int i = 0; i < moved.Length; i++) change = Mathf.Max(change, NVector3.Distance(moved[i], carried[i]));
            Assert.That(change, Is.GreaterThan(0.01f), "別の操作で形が変わっていない");

            // Ctrl+Z で、引き継いだ形に戻る（引き継いだ形が消えて、元の形になったりしない）
            c.UndoLast();
            Assert.That(c.HasUnsavedEdits, Is.True);
            NVector3[] back = Positions(c.Session);
            float maxBack = 0f;
            for (int i = 0; i < back.Length; i++) maxBack = Mathf.Max(maxBack, NVector3.Distance(back[i], carried[i]));
            Assert.That(maxBack, Is.LessThan(1e-4f), "Ctrl+Z で、引き継いだ形に戻らない");
            AssertSameDeltas(carriedDeltas, c.Session.GetMeshSpaceDeltas(), "別の操作を戻したあと");
        }

        [Test]
        public void RemoveWeightFix_AlsoCarriesTheUnsavedShape_WithoutAsking()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer tights = Find(root, TestAvatarMenu.TightsName);
            Mesh original = tights.sharedMesh;
            ClothEditController c = Start(tights);
            PullThenPreview(c);
            Assert.That(c.ApplyWeightFix(), Is.Not.Null, c.LastWeightFixError);
            Vector3[] before = c.Session.GetMeshSpaceDeltas();
            Assert.That(c.HasUnsavedEdits, Is.True);
            Undo.IncrementCurrentGroup();

            Assert.That(c.RemoveWeightFix(), Is.True, c.LastWeightFixError);
            Assert.That(_unsavedAsked, Is.EqualTo(0));
            Assert.That(c.RecordedWeightEditVertexCount, Is.EqualTo(0));
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.HasUnsavedEdits, Is.True, "外したときに、保存していない形が消えた");
            AssertSameDeltas(before, c.Session.GetMeshSpaceDeltas(), "外したあと");
            Assert.That(c.IsPoseCheckStale || c.LastPoseCheck == null, Is.True, "ウェイトを戻したので、結果は古くなってよい");

            // 外したのを Ctrl+Z で戻しても、形は残る
            c.UndoLast();
            Assert.That(c.HasUnsavedEdits, Is.True);
            AssertSameDeltas(before, c.Session.GetMeshSpaceDeltas(), "外したのを戻したあと", 2);
            Assert.That(tights.sharedMesh, Is.Not.SameAs(original), "外したのを戻したので、ウェイトを入れたメッシュ");
        }

        [Test]
        public void Apply_WithDiscardUnsaved_ResetsTheShape_AndTheResultNeedsCheckingAgain()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.TightsName));
            PullThenPreview(c);

            WeightFixReport report = c.ApplyWeightFix(discardUnsaved: true);
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Assert.That(report.DiscardedUnsaved, Is.True);
            Assert.That(report.CarriedUnsaved, Is.False);
            Assert.That(c.HasUnsavedEdits, Is.False, "捨てたのに、形が残っている");
            Assert.That(c.IsPoseCheckStale, Is.True, "形を捨てたので、調べ直した結果は使わず、もう一度チェックが要る");
        }

        [Test]
        public void Apply_WithoutUnsavedEdits_IsNeitherCarriedNorDiscarded_AndKeepsTheFreshResult()
        {
            GameObject root = CreateAvatar();
            ClothEditController c = Start(Find(root, TestAvatarMenu.TightsName));
            Assert.That(c.RunPoseCheck(), Is.Not.Null, c.LastPoseCheckError);
            WeightFixPreview preview = c.PreviewWeightFix();
            Assert.That(preview, Is.Not.Null, c.LastWeightFixError);
            WeightFixReport report = c.ApplyWeightFix();
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Assert.That(report.CarriedUnsaved, Is.False);
            Assert.That(report.DiscardedUnsaved, Is.False);
            Assert.That(c.LastPoseCheck, Is.SameAs(preview.After));
            Assert.That(c.IsPoseCheckStale, Is.False);
        }

        // ---- 窓 ----

        [Test]
        public void Window_Apply_DoesNotAskToSave_AndTheResultSaysTheEditIsKept()
        {
            GameObject root = CreateAvatar();
            ClothEditWindow window = ScriptableObject.CreateInstance<ClothEditWindow>();
            _windows.Add(window);
            ClothEditController c = window.Controller;
            c.SaveFolder = TempFolder;
            c.DataFolder = TempFolder;
            c.ShowProgress = false;
            c.Settings.ResetToDefaults();
            c.Settings.FixLargePoses = true;
            c.BrushRadius = 0.05f;
            c.InfluenceRadius = 0.12f;
            Assert.That(window.BeginFromRenderers(new List<Renderer> { Find(root, TestAvatarMenu.TightsName) }), Is.True, c.LastError);
            WeightFixPreview preview = PullThenPreview(c);
            Vector3[] before = c.Session.GetMeshSpaceDeltas();

            // 保存していない編集があるまま、窓から適用する（候補が伸びすぎないので、適用の確認だけが出る）
            window.OfferWeightFix(preview);

            Assert.That(_unsavedAsked, Is.EqualTo(0), "保存していない編集があっても、保存の確認は出ない");
            WeightFixReport report = c.LastWeightFix;
            Assert.That(report, Is.Not.Null, c.LastWeightFixError);
            Assert.That(report.CarriedUnsaved, Is.True);
            Assert.That(c.HasUnsavedEdits, Is.True);
            AssertSameDeltas(before, c.Session.GetMeshSpaceDeltas(), "窓から適用したあと");
            Assert.That(c.IsPoseCheckStale, Is.False);
            StringAssert.StartsWith("ウェイトを体に寄せました（Ctrl+Z の 1 回で戻せます）。", window.ResultText);
            StringAssert.Contains("保存していない編集は、そのまま残っています（まだ保存していません）。", window.ResultText);
            StringAssert.Contains("動作チェックの結果も、新しいウェイトのものになりました。", window.ResultText);
            Assert.That(window.LastToast, Is.EqualTo("ウェイトを体に寄せました"));
        }
    }
}
