using System.Collections.Generic;
using System.Linq;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 編集側の道具を、ウィンドウなしで確かめる: 診断、ショートカットの既定のキーと動作、F キーで半径を変える計算、
    /// 対称の補助（鏡の面の自動の位置合わせ・相手のいない点・今の形を左右対称にする・左右差）、つながりの許容距離 ε、
    /// 視点の向き、3D カーソル、名前を付けたカーブ、比較の表示（元の形の重ね描き・動いた量）、設定の保存。
    /// 平らな布は XY 平面（法線 -Z が表、カメラは -Z 側）。
    /// </summary>
    public class ClothEditEditingToolsTests
    {
        const string TempRoot = "Assets/GP02_EditingToolsTestTemp";
        const string KeyPrefix = "GP02.Test.EditingToolsSettings.";
        const string CurveKey = "GP02.Test.EditingToolsCurves";

        readonly List<Object> _created = new List<Object>();
        readonly List<Object> _clearUndoOnly = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        double _time = 100.0;
        UiLanguage _savedLanguage;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.Japanese;
        }

        [TearDown]
        public void TearDown()
        {
            L10n.Language = _savedLanguage;
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            foreach (Object o in _clearUndoOnly)
            {
                if (o != null) Undo.ClearUndo(o);
            }
            _clearUndoOnly.Clear();
            for (int k = _created.Count - 1; k >= 0; k--)
            {
                Object o = _created[k];
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            AssetDatabase.DeleteAsset(TempRoot);
            ClothEditSettings.DeleteSaved(KeyPrefix);
            new ClothEditCurveLibrary(CurveKey).DeleteAll();
            ClothEditShortcutBindings.SetEditing(false);
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // ---- 部品 ----

        static void AppendGrid(List<Vector3> vertices, List<int> triangles, float size, int cells, float z)
        {
            int start = vertices.Count;
            for (int y = 0; y <= cells; y++)
            {
                for (int x = 0; x <= cells; x++) vertices.Add(new Vector3((x / (float)cells - 0.5f) * size, (y / (float)cells - 0.5f) * size, z));
            }
            for (int y = 0; y < cells; y++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int a = start + y * (cells + 1) + x, b = a + 1, c = a + cells + 1, d = c + 1;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            }
        }

        // 原点を中心にした一辺 0.6m の平らな布（MeshRenderer）。layerZ の数だけ重ねる（同じメッシュの別の島）
        Renderer CreateFlat(string name, Transform parent = null, params float[] layerZ)
        {
            if (layerZ.Length == 0) layerZ = new[] { 0f };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (float z in layerZ) AppendGrid(vertices, triangles, 0.6f, 30, z);
            Mesh mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            GameObject go = Track(new GameObject(name));
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _clearUndoOnly.Add(go.GetComponent<MeshFilter>());
            return go.AddComponent<MeshRenderer>();
        }

        // シェイプキーを1つ持つ、SkinnedMeshRenderer の小さな板
        SkinnedMeshRenderer CreateSkinned(string name)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AppendGrid(vertices, triangles, 0.2f, 4, 0f);
            Mesh mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.AddBlendShapeFrame("Fat", 100f, Enumerable.Repeat(new Vector3(0f, 0f, -0.01f), vertices.Count).ToArray(), null, null);
            GameObject go = Track(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            _clearUndoOnly.Add(renderer);
            return renderer;
        }

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 6f };
            var c = new ClothEditController(settings) { SaveFolder = TempRoot, DataFolder = TempRoot, BrushRadius = 0.03f, InfluenceRadius = 0.3f };
            _controllers.Add(c);
            Assert.That(c.TryStart(renderer, out string error), Is.True, error);
            return c;
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

        static Ray FrontRay(float x, float y) => new Ray(new Vector3(x, y, -1.5f), Vector3.forward);

        // 上端（y が最大の行）をピン留めする
        static void PinTop(ClothEditController c)
        {
            c.Session.PaintPins(new Vector3(0f, 0.3f, 0f), 0.02f, true);
            c.Session.PaintPins(new Vector3(-0.2f, 0.3f, 0f), 0.02f, true);
            c.Session.PaintPins(new Vector3(0.2f, 0.3f, 0f), 0.02f, true);
        }

        // (x, y) の布をつまみ、カメラの方（-Z）へ towardCamera だけ引いて、仕上げまで待つ（「動かす向き」を視線にして、下へドラッグ）
        void Pull(ClothEditController c, float x, float y, float towardCamera)
        {
            c.Settings.Tool = ClothEditMode.Grab;
            MoveDirection saved = c.Settings.MoveDirection;
            c.Settings.MoveDirection = MoveDirection.View;
            Assert.That(c.PointerDown(FrontRay(x, y), Vector3.forward, Vector3.up, 0f), Is.True);
            c.PointerDrag(FrontRay(x, y - towardCamera));
            for (int i = 0; i < 20; i++) c.Tick(NextTime());
            c.PointerUp();
            Settle(c);
            c.Settings.MoveDirection = saved;
        }

        // ---- 診断 ----

        [Test]
        public void Diagnostics_FindScaleWeightsBonesAndSharedMeshes()
        {
            var items = new List<TargetWarning>();
            SkinnedMeshRenderer fine = CreateSkinned("Fine");
            ClothEditWarnings.Collect(fine, items);
            Assert.That(items, Is.Empty, "問題のない対象");

            // スケールが負・非一様
            fine.transform.localScale = new Vector3(-1f, 1f, 1f);
            ClothEditWarnings.Collect(fine, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.NegativeScale && w.Severity == MessageType.Warning), Is.True);
            fine.transform.localScale = new Vector3(1f, 2f, 1f);
            ClothEditWarnings.Collect(fine, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.NonUniformScale && w.Severity == MessageType.Info), Is.True);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.NegativeScale), Is.False);
            fine.transform.localScale = Vector3.one;

            // シェイプキーの重みが 0 でない → 直すボタンで 0 にできる
            fine.SetBlendShapeWeight(0, 40f);
            ClothEditWarnings.Collect(fine, items);
            TargetWarning weights = items.Find(w => w.Kind == TargetWarningKind.BlendShapeWeights);
            Assert.That(weights.Fix, Is.EqualTo(TargetWarningFix.ResetBlendShapeWeights));
            Assert.That(weights.CanAutoFix, Is.True);
            Assert.That(weights.Message, Does.Contain("Fat"));
            Assert.That(ClothEditWarnings.TryResetBlendShapeWeights(fine, out string message), Is.EqualTo(1), message);
            Assert.That(fine.GetBlendShapeWeight(0), Is.EqualTo(0f));
            ClothEditWarnings.Collect(fine, items);
            Assert.That(items, Is.Empty);

            // ボーンが欠けている
            SkinnedMeshRenderer skinned = CreateSkinned("Bones");
            skinned.sharedMesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            skinned.bones = new Transform[] { null, skinned.transform };
            ClothEditWarnings.Collect(skinned, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.MissingBones && w.Severity == MessageType.Error), Is.True);

            // 同じメッシュを使うほかのレンダラー
            SkinnedMeshRenderer twin = Track(new GameObject("Twin")).AddComponent<SkinnedMeshRenderer>();
            twin.sharedMesh = fine.sharedMesh;
            ClothEditWarnings.Collect(fine, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.SharedMesh && w.Severity == MessageType.Info && !w.CanAutoFix), Is.True);

            // 重い順に並ぶ
            var mixed = new List<TargetWarning>
            {
                new TargetWarning(TargetWarningKind.SharedMesh, MessageType.Info, "i"),
                new TargetWarning(TargetWarningKind.MissingBones, MessageType.Error, "e"),
                new TargetWarning(TargetWarningKind.NegativeScale, MessageType.Warning, "w"),
            };
            ClothEditWarnings.SortBySeverity(mixed);
            Assert.That(mixed.Select(w => w.Severity), Is.EqualTo(new[] { MessageType.Error, MessageType.Warning, MessageType.Info }));
            Assert.That(ClothEditWarnings.SeverityLabel(MessageType.Error), Is.EqualTo("重大"));
            Assert.That(ClothEditWarnings.FixLabel(TargetWarningFix.FitMirrorPlane), Is.Not.Empty);
        }

        [Test]
        public void Diagnostics_WhileEditing_ReportNoPinsAndUnmatchedMirrorPoints()
        {
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.AddComponent<Animator>();
            Renderer flat = CreateFlat("Diag", avatar.transform);
            flat.transform.localPosition = new Vector3(0.12f, 0f, 0f);
            ClothEditController c = StartController(flat);

            var items = new List<TargetWarning>();
            ClothEditWarnings.CollectEditing(c, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.NoPins), Is.True);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.NoBurst), Is.EqualTo(!BurstInstaller.IsAccelerated));
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.UnmatchedMirror), Is.False, "対称がオフなら見ない");

            PinTop(c);
            c.Settings.SymmetryEnabled = true;
            c.ApplySettings();
            items.Clear();
            ClothEditWarnings.CollectEditing(c, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.NoPins), Is.False);
            TargetWarning unmatched = items.Find(w => w.Kind == TargetWarningKind.UnmatchedMirror);
            Assert.That(unmatched.Fix, Is.EqualTo(TargetWarningFix.FitMirrorPlane), "衣装が中心から 12cm ずれているので、相手のいない点が多い");

            Assert.That(c.AutoFitSymmetryPlane(out string message), Is.True, message);
            items.Clear();
            ClothEditWarnings.CollectEditing(c, items);
            Assert.That(items.Exists(w => w.Kind == TargetWarningKind.UnmatchedMirror), Is.False);
        }

        // ---- ショートカット ----

        [Test]
        public void Shortcuts_AreRegisteredWithTheDefaultKeys()
        {
            var ids = new HashSet<string>(ShortcutManager.instance.GetAvailableShortcutIds());
            var seen = new HashSet<string>();
            foreach (ClothEditShortcutBindings.Definition definition in ClothEditShortcutBindings.Definitions)
            {
                Assert.That(seen.Add(definition.Id), Is.True, "同じ ID が2回: " + definition.Id);
                Assert.That(ids, Does.Contain(definition.Id), "Shortcut Manager に登録されていない: " + definition.Id);
                if (ShortcutManager.instance.IsShortcutOverridden(definition.Id)) continue;
                ShortcutBinding binding = ShortcutManager.instance.GetShortcutBinding(definition.Id);
                KeyCombination key = binding.keyCombinationSequence.Single();
                Assert.That(key.keyCode, Is.EqualTo(definition.Key), definition.Id);
                Assert.That(key.modifiers, Is.EqualTo(definition.Modifiers), definition.Id);
            }
            // すべての動作に1つずつ
            foreach (ClothEditShortcutAction action in System.Enum.GetValues(typeof(ClothEditShortcutAction)))
            {
                Assert.That(ClothEditShortcutBindings.Definitions.Count(d => d.Action == action), Is.EqualTo(1), action.ToString());
            }
            // 既定のキーのまま、案内に出すキーの文字は今までどおり
            Assert.That(ClothEditHelp.ToolKey(ClothEditMode.Grab), Is.EqualTo("W"));
            Assert.That(ClothEditHelp.ToolKey(ClothEditMode.Unpin), Is.EqualTo("Shift+P"));
        }

        [Test]
        public void Shortcuts_DefaultKeys_DoTheSameAsTheDirectKeyJudgement()
        {
            var tools = (ClothEditMode[])System.Enum.GetValues(typeof(ClothEditMode));
            foreach (ClothEditShortcutBindings.Definition definition in ClothEditShortcutBindings.Definitions)
            {
                bool shift = (definition.Modifiers & ShortcutModifiers.Shift) != 0;
                foreach (ClothEditMode current in tools)
                {
                    foreach (ClothEditMode previous in new[] { ClothEditMode.Grab, ClothEditMode.Pin, ClothEditMode.Select })
                    {
                        ToolKeyResult byKey = ClothEditShortcuts.ResolveToolKey(definition.Key, shift, current, previous, SelectionHandle.Rotate);
                        ToolKeyResult byAction = ClothEditShortcuts.ResolveToolAction(definition.Action, current, previous, SelectionHandle.Rotate);
                        Assert.That(byAction.Handled, Is.EqualTo(byKey.Handled), $"{definition.Id} / {current}");
                        if (!byKey.Handled) continue;
                        Assert.That(byAction.Tool, Is.EqualTo(byKey.Tool), $"{definition.Id} / {current}");
                        Assert.That(byAction.Handle, Is.EqualTo(byKey.Handle), $"{definition.Id} / {current}");
                    }
                }
                bool radiusByKey = ClothEditShortcuts.TryGetRadiusKey(definition.Key, '\0', shift, out WheelTarget keyTarget, out int keyStep);
                bool radiusByAction = ClothEditShortcuts.TryGetRadiusAction(definition.Action, out WheelTarget actionTarget, out int actionStep);
                Assert.That(radiusByAction, Is.EqualTo(radiusByKey), definition.Id);
                if (radiusByKey)
                {
                    Assert.That(actionTarget, Is.EqualTo(keyTarget), definition.Id);
                    Assert.That(actionStep, Is.EqualTo(keyStep), definition.Id);
                }
            }
            Assert.That(ClothEditShortcuts.IsHoldOriginalKey(ClothEditShortcutBindings.Definitions.Single(d => d.Action == ClothEditShortcutAction.HoldOriginal).Key), Is.True);
            Assert.That(ClothEditShortcuts.IsHelpKey(ClothEditShortcutBindings.Definitions.Single(d => d.Action == ClothEditShortcutAction.ToggleHelp).Key), Is.True);
            Assert.That(ClothEditShortcuts.IsRadiusDragKey(ClothEditShortcutBindings.Definitions.Single(d => d.Action == ClothEditShortcutAction.RadiusDrag).Key), Is.True);
        }

        [Test]
        public void Shortcuts_TagIsRegisteredOnlyWhileEditing()
        {
            ClothEditShortcutBindings.SetEditing(true);
            Assert.That(ClothEditShortcutBindings.IsActive, Is.True);
            ClothEditShortcutBindings.SetEditing(false);
            Assert.That(ClothEditShortcutBindings.IsActive, Is.False);

            // 動作は受け取る先に渡る
            var received = new List<(ClothEditShortcutAction, bool)>();
            System.Action<ClothEditShortcutAction, bool> saved = ClothEditShortcutBindings.Handler;
            try
            {
                ClothEditShortcutBindings.Handler = (action, pressed) => received.Add((action, pressed));
                typeof(ClothEditShortcutBindings).GetMethod("Grab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
                Assert.That(received, Is.EqualTo(new[] { (ClothEditShortcutAction.Grab, true) }));
            }
            finally
            {
                ClothEditShortcutBindings.Handler = saved;
            }
        }

        // ---- F キーで半径を変える ----

        [Test]
        public void RadiusDrag_ChangesWithSidewaysMotion_AndCancelRestores()
        {
            Assert.That(ClothEditRadiusDrag.TargetFor(false, ClothEditMode.Grab), Is.EqualTo(RadiusDragTarget.BrushRadius));
            Assert.That(ClothEditRadiusDrag.TargetFor(true, ClothEditMode.Grab), Is.EqualTo(RadiusDragTarget.InfluenceRadius));
            Assert.That(ClothEditRadiusDrag.TargetFor(false, ClothEditMode.Select), Is.EqualTo(RadiusDragTarget.SelectionSmoothness));
            Assert.That(ClothEditRadiusDrag.TargetFor(true, ClothEditMode.Select), Is.EqualTo(RadiusDragTarget.InfluenceRadius));

            var drag = new ClothEditRadiusDrag();
            drag.Begin(RadiusDragTarget.BrushRadius, 300f, 3f, 1f, 20f);
            Assert.That(drag.Active, Is.True);
            float rate = 19f / ClothEditRadiusDrag.PixelsForFullRange;
            Assert.That(drag.Update(400f), Is.EqualTo(3f + 100f * rate).Within(1e-4f), "右で大きく");
            Assert.That(drag.Update(280f), Is.EqualTo(3f - 20f * rate).Within(1e-4f), "左で小さく");
            Assert.That(drag.Update(-5000f), Is.EqualTo(1f), "下限");
            Assert.That(drag.Update(5000f), Is.EqualTo(20f), "上限");
            Assert.That(drag.Cancel(), Is.EqualTo(3f), "取り消すと元の値");
            Assert.That(drag.Active, Is.False);

            drag.Begin(RadiusDragTarget.SelectionSmoothness, 0f, 0f, 0f, 30f);
            drag.Update(80f);
            Assert.That(drag.Confirm(), Is.EqualTo(80f * 30f / ClothEditRadiusDrag.PixelsForFullRange).Within(1e-4f), "0 からでも大きくできる");
            Assert.That(drag.Active, Is.False);
        }

        // ---- 対称の補助 ----

        [Test]
        public void AutoFitSymmetryPlane_FindsTheOutfitsCenter_InTheAvatarsLocalSpace()
        {
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.AddComponent<Animator>();
            avatar.transform.position = new Vector3(0.3f, 0f, 0f);
            avatar.transform.localScale = new Vector3(2f, 2f, 2f);
            Renderer flat = CreateFlat("OffCenter", avatar.transform);
            flat.transform.position = new Vector3(0.3f + 0.08f, 0f, 0f);   // アバターの中心から 8cm（ローカルでは 4cm）
            flat.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            ClothEditController c = StartController(flat);
            c.Settings.SymmetryEnabled = true;
            c.ApplySettings();
            Assert.That(c.UnmatchedMirrorRatio, Is.GreaterThan(0.2f), "ずれたままでは相手のいない点が多い");

            Assert.That(c.AutoFitSymmetryPlane(out string message), Is.True, message);
            Assert.That(c.Settings.SymmetryOffset, Is.EqualTo(0.04f).Within(0.002f));
            Assert.That(c.UnmatchedMirrorRatio, Is.LessThan(0.02f));
            Assert.That(c.TryGetMirrorPlane(out MirrorPlane plane), Is.True);
            Assert.That(plane.Point.X, Is.EqualTo(0.38f).Within(0.004f));
        }

        [Test]
        public void SymmetrizeShape_CopiesTheKeptSide_SettlesAndUndoesInOneStep()
        {
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.AddComponent<Animator>();
            Renderer flat = CreateFlat("Sym", avatar.transform);
            ClothEditController c = StartController(flat);
            PinTop(c);
            Pull(c, 0.15f, -0.1f, 0.06f);
            c.Settings.SymmetryEnabled = true;
            c.Settings.ShowMirrorDifference = true;
            c.ApplySettings();
            Assert.That(c.TryGetMirrorPlane(out MirrorPlane plane), Is.True);
            float before = c.Session.MaxMirrorDifference(plane, c.Settings.MirrorTolerance);
            Assert.That(before, Is.GreaterThan(0.03f), "片側だけ引いた");
            NVector3[] pulled = c.Session.Solver.Positions.ToArray();

            c.Settings.SymmetrizeSource = SymmetrySourceSide.Positive;
            int changed = c.SymmetrizeShape();
            Assert.That(changed, Is.GreaterThan(100));
            Assert.That(c.IsSettling, Is.True, "写したあとは仕上げる");
            Settle(c);
            float after = c.Session.MaxMirrorDifference(plane, c.Settings.MirrorTolerance);
            Assert.That(after, Is.LessThan(0.005f), $"左右差 {before} → {after}");
            // 反対側（-X）も引かれている
            int left = c.Session.FindParticle(new Vector3(-0.15f, -0.1f, 0f), -1);
            Assert.That(c.Session.Solver.Positions[left].Z, Is.LessThan(-0.03f));

            c.UndoLast();
            NVector3[] undone = c.Session.Solver.Positions.ToArray();
            float worst = 0f;
            for (int i = 0; i < undone.Length; i++) worst = Mathf.Max(worst, NVector3.Distance(undone[i], pulled[i]));
            Assert.That(worst, Is.LessThan(1e-5f), "Undo の1段で、左右対称にする前に戻る");

            // 基準を −X にすると、引いていない側が写されて、引いた所はほぼ元に戻る
            c.Settings.SymmetrizeSource = SymmetrySourceSide.Negative;
            Assert.That(c.SymmetrizeShape(), Is.GreaterThan(100));
            Settle(c);
            int right = c.Session.FindParticle(new Vector3(0.15f, -0.1f, 0f), -1);
            Assert.That(c.Session.Solver.Positions[right].Z, Is.GreaterThan(-0.02f));
            Assert.That(c.Session.MaxMirrorDifference(plane, c.Settings.MirrorTolerance), Is.LessThan(0.005f));
        }

        [Test]
        public void MirrorTolerance_IsUsedByTheBrush_AndTheUnmatchedCount()
        {
            GameObject avatar = Track(new GameObject("Avatar"));
            avatar.AddComponent<Animator>();
            Renderer flat = CreateFlat("Tolerance", avatar.transform);
            flat.transform.localPosition = new Vector3(0.0125f, 0f, 0f);
            var settings = new ClothEditSettings { SolveMilliseconds = 6f, SymmetryEnabled = true, MirrorTolerance = 0.003f };
            ClothEditController c = StartController(flat, settings);
            Assert.That(c.BuildBrushOptions().MirrorTolerance, Is.EqualTo(0.003f));
            int narrow = c.UnmatchedMirrorCount;
            c.Settings.MirrorTolerance = 0.05f;
            c.ApplySettings();
            int wide = c.UnmatchedMirrorCount;
            Assert.That(narrow, Is.GreaterThan(wide), "許容距離を広げると相手が見つかる");
            Assert.That(c.Session.MirrorTolerance, Is.EqualTo(0.05f));
        }

        // ---- つながりの許容距離 ε ----

        [Test]
        public void ConnectDistance_ReachesTheOtherIslandOnlyWhenSet()
        {
            Renderer flat = CreateFlat("Layers", null, 0f, 0.004f);
            ClothEditController c = StartController(flat);
            int[] island = c.Session.Cloth.ParticleIsland;
            int seed = c.Session.FindParticle(new Vector3(0f, 0f, 0f), -1);
            var indices = new int[ClothEditController.MaxPreviewParticles];
            var weights = new float[ClothEditController.MaxPreviewParticles];

            BrushOptions options = c.BuildBrushOptions();
            options.Radius = 0.03f;
            int count = c.Session.CollectBrushRange(seed, options, indices, weights, indices.Length);
            Assert.That(indices.Take(count).All(i => island[i] == island[seed]), Is.True, "ε が 0 なら別の層には広がらない");

            c.Settings.ConnectDistance = 0.012f;
            c.ApplySettings();
            options = c.BuildBrushOptions();
            Assert.That(options.ConnectDistance, Is.EqualTo(0.012f));
            options.Radius = 0.03f;
            count = c.Session.CollectBrushRange(seed, options, indices, weights, indices.Length);
            Assert.That(indices.Take(count).Any(i => island[i] != island[seed]), Is.True, "ε 以内の別の層にも広がる");

            c.Settings.ConnectDistance = 1f;
            c.Settings.Sanitize();
            Assert.That(c.Settings.ConnectDistance, Is.EqualTo(ClothEditSettings.MaxConnectDistance));
        }

        // ---- 視点の向き・3D カーソル ----

        [Test]
        public void ViewOrientation_HandleAndMoveDirection()
        {
            Renderer flat = CreateFlat("View");
            ClothEditController c = StartController(flat);
            Quaternion view = Quaternion.Euler(10f, 30f, 0f);
            c.SetViewRotation(view);
            c.SelectAll();
            c.Settings.HandleSpace = HandleOrientation.View;
            Assert.That(Quaternion.Angle(c.HandleRotation, view), Is.LessThan(1e-3f));

            // 上下の動きを、視線に沿った奥行きに読み替える
            Vector3 depth = ClothEditController.ViewDepthPoint(Vector3.zero, new Vector3(0.3f, 0.1f, 0f), Vector3.forward, Vector3.up);
            Assert.That(Vector3.Distance(depth, new Vector3(0f, 0f, 0.1f)), Is.LessThan(1e-6f));

            c.ClearSelection();
            c.Settings.Tool = ClothEditMode.Grab;
            c.Settings.MoveDirection = MoveDirection.View;
            Assert.That(c.PointerDown(FrontRay(0.1f, 0f), Vector3.forward, Vector3.up, 0f), Is.True);
            Vector3 start = c.GrabTarget;
            c.PointerDrag(FrontRay(0.16f, -0.05f));
            Vector3 moved = c.GrabTarget - start;
            Assert.That(moved.z, Is.EqualTo(-0.05f).Within(1e-4f), "下へで手前へ（カメラは -Z 側）");
            Assert.That(Mathf.Abs(moved.x) + Mathf.Abs(moved.y), Is.LessThan(1e-5f), "画面の向きには動かない");
            c.Cancel();
        }

        [Test]
        public void Cursor_IsPlacedOnTheSurface_AndBecomesThePivot()
        {
            Renderer flat = CreateFlat("Cursor");
            ClothEditController c = StartController(flat);
            Assert.That(c.HasCursor, Is.False);
            Assert.That(c.PlaceCursor(FrontRay(0.1f, 0.05f)), Is.True);
            Assert.That(c.HasCursor, Is.True);
            Assert.That(Vector3.Distance(c.CursorPosition, new Vector3(0.1f, 0.05f, 0f)), Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(c.CursorNormal.z), Is.GreaterThan(0.99f));
            Assert.That(c.PlaceCursor(FrontRay(5f, 5f)), Is.False, "布の無い所には置かない");

            c.Settings.Tool = ClothEditMode.Select;
            c.SelectPaintBegin(FrontRay(-0.1f, -0.1f), false);
            c.SelectPaintEnd();
            Assert.That(c.SelectionCount, Is.GreaterThan(0));
            c.Settings.PivotMode = SelectionPivot.Cursor;
            Assert.That(Vector3.Distance(c.SelectionPivot, c.CursorPosition), Is.LessThan(1e-6f));

            // 回すと、カーソルのまわりに回る（選んだ点とカーソルの距離が変わらない）
            int particle = c.SelectedParticles[0];
            NVector3 p = c.Session.Solver.Positions[particle];
            float radius = Vector3.Distance(new Vector3(p.X, p.Y, p.Z), c.CursorPosition);
            c.Settings.SelectionMovesAsCloth = false;
            Assert.That(c.ApplySelectionTransform(Vector3.zero, Quaternion.AngleAxis(30f, Vector3.forward), Vector3.one), Is.True);
            p = c.Session.Solver.Positions[particle];
            Assert.That(Vector3.Distance(new Vector3(p.X, p.Y, p.Z), c.CursorPosition), Is.EqualTo(radius).Within(1e-3f));

            Assert.That(c.PlaceCursorAtSelectionCenter(), Is.True);
            c.Settings.PivotMode = SelectionPivot.Center;
            Vector3 center = c.SelectionPivot;
            Assert.That(Vector3.Distance(center, c.CursorPosition), Is.LessThan(1e-5f));
            c.ClearCursor();
            c.Settings.PivotMode = SelectionPivot.Cursor;
            Assert.That(Vector3.Distance(c.SelectionPivot, center), Is.LessThan(1e-5f), "カーソルが無ければ選んだ点の中心");
        }

        // ---- 名前を付けたカーブ ----

        [Test]
        public void CurveLibrary_SavesLoadsOverwritesAndLimits()
        {
            var library = new ClothEditCurveLibrary(CurveKey);
            library.DeleteAll();
            AnimationCurve curve = AnimationCurve.Linear(0f, 1f, 1f, 0.2f);
            Assert.That(library.Save("  ", curve, out _), Is.False, "名前が空");
            Assert.That(library.Save("Soft\tEdge", curve, out string message), Is.True, message);
            Assert.That(library.GetName(0), Is.EqualTo("Soft Edge"));

            var reloaded = new ClothEditCurveLibrary(CurveKey);
            Assert.That(reloaded.Count, Is.EqualTo(1));
            AnimationCurve loaded = reloaded.GetCurve(0);
            Assert.That(loaded.Evaluate(1f), Is.EqualTo(0.2f).Within(1e-4f));
            Assert.That(loaded.Evaluate(0.5f), Is.EqualTo(curve.Evaluate(0.5f)).Within(1e-4f));

            Assert.That(reloaded.Save("Soft Edge", AnimationCurve.Linear(0f, 1f, 1f, 0f), out _), Is.True, "同じ名前は上書き");
            Assert.That(reloaded.Count, Is.EqualTo(1));
            Assert.That(new ClothEditCurveLibrary(CurveKey).GetCurve(0).Evaluate(1f), Is.EqualTo(0f).Within(1e-4f));

            for (int i = 1; i < ClothEditCurveLibrary.MaxCount; i++) Assert.That(reloaded.Save("Curve " + i, curve, out _), Is.True);
            Assert.That(reloaded.Save("One too many", curve, out message), Is.False, "上限");
            Assert.That(message, Does.Contain(ClothEditCurveLibrary.MaxCount.ToString()));
            Assert.That(reloaded.Delete("Curve 3"), Is.True);
            Assert.That(reloaded.Save("One too many", curve, out _), Is.True, "消せば入る");
            Assert.That(new ClothEditCurveLibrary(CurveKey).Count, Is.EqualTo(ClothEditCurveLibrary.MaxCount));
        }

        // ---- 比較の表示 ----

        [Test]
        public void Compare_GhostFollowsTheSetting_AndMoveDotsShowTheMovedArea()
        {
            Renderer flat = CreateFlat("Compare");
            ClothEditController c = StartController(flat);
            Assert.That(c.Session.ShowGhost, Is.False);
            c.Settings.ShowOriginalGhost = true;
            c.ApplySettings();
            Assert.That(c.Session.ShowGhost, Is.True);

            var positions = new List<Vector3>();
            var distances = new List<float>();
            c.Session.GetMoveDots(positions, distances, 500);
            Assert.That(distances.Max(), Is.LessThan(1e-6f), "まだ動いていない");

            PinTop(c);
            Pull(c, 0f, -0.2f, 0.05f);
            c.Session.GetMoveDots(positions, distances, 500);
            Assert.That(positions.Count, Is.LessThanOrEqualTo(500));
            Assert.That(distances.Max(), Is.GreaterThan(0.03f));
            int moved = c.Session.FindParticle(new Vector3(0f, -0.2f, -0.05f), -1);
            Assert.That(c.Session.GetMoveDistance(moved), Is.GreaterThan(0.03f));

            Assert.That(ClothEditHelp.MoveColor(0f).b, Is.GreaterThan(0.9f));
            Assert.That(ClothEditHelp.MoveColor(ClothEditSettings.MoveHeatmapMax).r, Is.GreaterThan(0.9f));
            Assert.That(ClothEditHelp.MirrorDifferenceColor(0f).g, Is.GreaterThan(0.8f));
            Assert.That(ClothEditHelp.MirrorDifferenceColor(1f).r, Is.GreaterThan(0.9f));
        }

        // ---- 設定 ----

        [Test]
        public void Settings_NewValues_HaveDefaults_AreSanitized_AndSaved()
        {
            var settings = new ClothEditSettings();
            Assert.That(settings.MirrorTolerance, Is.EqualTo(BrushOptions.DefaultMirrorTolerance));
            Assert.That(settings.ConnectDistance, Is.EqualTo(0f));
            Assert.That(settings.ShowUnmatchedMirror, Is.True);
            Assert.That(settings.SymmetrizeSource, Is.EqualTo(SymmetrySourceSide.Positive));
            Assert.That((int)MoveDirection.View, Is.EqualTo(5), "末尾に足す");
            Assert.That((int)HandleOrientation.View, Is.EqualTo(3), "末尾に足す");
            Assert.That((int)SelectionPivot.Cursor, Is.EqualTo(2), "末尾に足す");

            settings.MirrorTolerance = float.NaN;
            settings.ConnectDistance = -1f;
            settings.SymmetrizeSource = (SymmetrySourceSide)9;
            settings.Sanitize();
            Assert.That(settings.MirrorTolerance, Is.EqualTo(BrushOptions.DefaultMirrorTolerance));
            Assert.That(settings.ConnectDistance, Is.EqualTo(0f));
            Assert.That(settings.SymmetrizeSource, Is.EqualTo(SymmetrySourceSide.Positive));
            settings.MirrorTolerance = 5f;
            settings.Sanitize();
            Assert.That(settings.MirrorTolerance, Is.EqualTo(ClothEditSettings.MaxMirrorTolerance));

            var saved = new ClothEditSettings
            {
                MirrorTolerance = 0.03f, ShowUnmatchedMirror = false, ShowMirrorDifference = true, SymmetrizeSource = SymmetrySourceSide.Negative,
                ConnectDistance = 0.01f, ShowFalloffRings = true, ShowOriginalGhost = true, ShowMoveHeatmap = true,
                MoveDirection = MoveDirection.View, HandleSpace = HandleOrientation.View, PivotMode = SelectionPivot.Cursor,
            };
            saved.Save(KeyPrefix);
            ClothEditSettings loaded = ClothEditSettings.Load(KeyPrefix);
            Assert.That(loaded.MirrorTolerance, Is.EqualTo(0.03f));
            Assert.That(loaded.ShowUnmatchedMirror, Is.False);
            Assert.That(loaded.ShowMirrorDifference, Is.True);
            Assert.That(loaded.SymmetrizeSource, Is.EqualTo(SymmetrySourceSide.Negative));
            Assert.That(loaded.ConnectDistance, Is.EqualTo(0.01f));
            Assert.That(loaded.ShowFalloffRings && loaded.ShowOriginalGhost && loaded.ShowMoveHeatmap, Is.True);
            Assert.That(loaded.MoveDirection, Is.EqualTo(MoveDirection.View));
            Assert.That(loaded.HandleSpace, Is.EqualTo(HandleOrientation.View));
            Assert.That(loaded.PivotMode, Is.EqualTo(SelectionPivot.Cursor));
            loaded.ResetToDefaults();
            Assert.That(loaded.ConnectDistance, Is.EqualTo(0f));
            Assert.That(loaded.ShowOriginalGhost, Is.False);
        }
    }
}
