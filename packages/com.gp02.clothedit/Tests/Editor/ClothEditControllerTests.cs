using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 操作の流れ（押す・ドラッグ・離す、仕上げ、Undo、保存、取り消し）を、ウィンドウなしで通す。
    /// レイとカメラの向きと経過時間を直接渡し、実際の Unity の Undo を使う。
    /// </summary>
    public class ClothEditControllerTests
    {
        const string TempRoot = "Assets/GP02_TestTemp";
        const string Folder = TempRoot + "/Meshes";
        const string TestKeyPrefix = "GP02.Test.Settings.";

        // 斜め手前（-Z 側の下）から布（XY 平面の Quad）を見る
        static readonly Vector3 CameraPosition = new Vector3(0.1f, -1f, -1f);
        static readonly Vector3 CameraForward = new Vector3(0f, 1f, 1f).normalized;

        readonly List<Object> _created = new List<Object>();
        readonly List<Object> _clearUndoOnly = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        double _time = 100.0;
        UiLanguage _savedLanguage;

        // 文言を確かめるテストがあるので、言語は日本語に固定する（利用者が English にしていても通るように）
        [SetUp]
        public void FixLanguage()
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
            foreach (Object o in _created)
            {
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            AssetDatabase.DeleteAsset(TempRoot);
            ClothEditSettings.DeleteSaved(TestKeyPrefix);
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        Renderer CreateTarget(bool skinned, string name = "ControllerTarget")
        {
            Mesh mesh = Track(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Quad.fbx")));
            GameObject go = Track(new GameObject(name));
            if (skinned)
            {
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                _clearUndoOnly.Add(renderer);
                return renderer;
            }
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            _clearUndoOnly.Add(filter);
            return go.AddComponent<MeshRenderer>();
        }

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            // 計算時間を短くして、テストを速くする
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 4f };
            var controller = new ClothEditController(settings) { SaveFolder = Folder, DataFolder = Folder, BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            _controllers.Add(controller);
            Assert.That(controller.TryStart(renderer, out string error), Is.True, error);
            return controller;
        }

        static Ray RayAt(Vector3 point) => new Ray(CameraPosition, (point - CameraPosition).normalized);

        // ヒット点から、カメラの向きに垂直な平面の上で (0, s, -s) だけ動いた点（手前に引く向き）
        static Vector3 Pulled(Vector3 from, float s) => from + new Vector3(0f, s, -s);

        void Tick(ClothEditController c)
        {
            _time += 0.016;
            c.Tick(_time);
        }

        // 押して、数回ドラッグしながら Tick を進め、離す（仕上げには入るが、進めない）
        void GrabStroke(ClothEditController c, Vector3 from, float distance, int steps = 5)
        {
            Assert.That(c.PointerDown(RayAt(from), CameraForward), Is.True);
            for (int k = 1; k <= steps; k++)
            {
                c.PointerDrag(RayAt(Pulled(from, distance * k / steps)));
                Tick(c);
            }
            c.PointerUp();
        }

        void SettleUntilDone(ClothEditController c)
        {
            for (int i = 0; i < 1000 && c.IsBusy; i++) Tick(c);
            Assert.That(c.IsBusy, Is.False, "仕上げが終わらない");
        }

        void PinStroke(ClothEditController c, bool pin, params Vector3[] points)
        {
            c.Mode = pin ? ClothEditMode.Pin : ClothEditMode.Unpin;
            Assert.That(c.PointerDown(RayAt(points[0]), CameraForward), Is.True);
            for (int i = 1; i < points.Length; i++) c.PointerDrag(RayAt(points[i]));
            c.PointerUp();
            c.Mode = ClothEditMode.Grab;
        }

        static NVector3[] Positions(ClothEditController c) => c.Session.Solver.Positions.ToArray();

        static float MaxDistance(NVector3[] a, NVector3[] b)
        {
            Assert.That(a.Length, Is.EqualTo(b.Length));
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, NVector3.Distance(a[i], b[i]));
            return max;
        }

        // ---- 押す・ドラッグ・離す・仕上げ・Undo ----

        [TestCase(true)]
        [TestCase(false)]
        public void GrabDragRelease_SettlesAndChangesShape_UndoRedoRestoresIt(bool skinned)
        {
            Renderer renderer = CreateTarget(skinned);
            ClothEditController c = StartController(renderer);
            NVector3[] before = Positions(c);

            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward), Is.True);
            Assert.That(c.IsDragging, Is.True);
            c.PointerDrag(RayAt(Pulled(new Vector3(0.1f, 0f, 0f), 0.1f)));
            for (int i = 0; i < 3; i++) Tick(c);
            Assert.That(c.Session.IsGrabbing, Is.True);
            c.PointerUp();
            Assert.That(c.IsSettling, Is.True);
            Assert.That(c.IsDragging, Is.False);
            SettleUntilDone(c);

            Assert.That(c.Session.IsGrabbing, Is.False, "仕上げのあとはつまみが外れる");
            NVector3[] after = Positions(c);
            Assert.That(MaxDistance(before, after), Is.GreaterThan(0.05f), "形が変わっている");

            Undo.PerformUndo();
            Assert.That(c.IsEditing, Is.True);
            Assert.That(Positions(c), Is.EqualTo(before));

            Undo.PerformRedo();
            Assert.That(Positions(c), Is.EqualTo(after));
        }

        [Test]
        public void ReleasingWithoutMoving_LeavesNoUndoRecord()
        {
            ClothEditController c = StartController(CreateTarget(true));
            PinStroke(c, true, new Vector3(-0.3f, 0f, 0f), new Vector3(-0.2f, 0f, 0f));
            int pinned = c.Session.PinnedCount;
            Assert.That(pinned, Is.GreaterThan(0));
            NVector3[] before = Positions(c);

            // クリックしただけ（Tick を進めても計算しない）
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward), Is.True);
            for (int i = 0; i < 3; i++) Tick(c);
            c.PointerUp();
            Assert.That(c.IsBusy, Is.False);
            for (int i = 0; i < 3; i++) Tick(c);
            Assert.That(Positions(c), Is.EqualTo(before));

            // 空の記録が残っていれば、ここでその記録が戻るだけで、ピン留めは戻らない
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
        }

        [Test]
        public void PinPainting_IsOneUndoStep()
        {
            ClothEditController c = StartController(CreateTarget(true));
            PinStroke(c, true, new Vector3(-0.3f, 0f, 0f), new Vector3(-0.1f, 0.1f, 0f), new Vector3(0.1f, 0f, 0f), new Vector3(0.3f, -0.1f, 0f));
            int pinned = c.Session.PinnedCount;
            Assert.That(pinned, Is.GreaterThan(0));

            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "1回の Undo で全部外れる");
            Undo.PerformRedo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned));

            // ピン解除も1回の操作
            PinStroke(c, false, new Vector3(-0.3f, 0f, 0f), new Vector3(0.3f, -0.1f, 0f));
            int remaining = c.Session.PinnedCount;
            Assert.That(remaining, Is.LessThan(pinned));
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned));
        }

        [Test]
        public void Undo_DuringSettling_StopsAndRestoresShape()
        {
            ClothEditController c = StartController(CreateTarget(true));
            NVector3[] before = Positions(c);

            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.08f);
            Assert.That(c.IsSettling, Is.True);
            c.UndoLast();   // 仕上げを待たずに確定してから戻す

            Assert.That(c.IsBusy, Is.False);
            Assert.That(c.Session.IsGrabbing, Is.False);
            Assert.That(Positions(c), Is.EqualTo(before));
        }

        [Test]
        public void ResetAll_IsUndoable()
        {
            ClothEditController c = StartController(CreateTarget(true));
            NVector3[] rest = Positions(c);
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            NVector3[] edited = Positions(c);

            c.ResetAll();
            Assert.That(MaxDistance(Positions(c), rest), Is.LessThan(1e-5f));
            Undo.PerformUndo();
            Assert.That(Positions(c), Is.EqualTo(edited));
        }

        [Test]
        public void Hover_FindsClothAndMisses()
        {
            ClothEditController c = StartController(CreateTarget(true));
            Assert.That(c.Hover(RayAt(new Vector3(0.1f, 0.1f, 0f))), Is.True);
            Assert.That(c.HasHover, Is.True);
            Assert.That(Vector3.Distance(c.HoverPoint, new Vector3(0.1f, 0.1f, 0f)), Is.LessThan(1e-4f));
            Assert.That(c.Hover(RayAt(new Vector3(3f, 0f, 0f))), Is.False);
            Assert.That(c.HasHover, Is.False);
            Assert.That(c.PointerDown(RayAt(new Vector3(3f, 0f, 0f)), CameraForward), Is.False, "布に当たらなければ何も始まらない");
            Assert.That(c.IsBusy, Is.False);
        }

        // ---- 取り消し（Esc） ----

        [Test]
        public void Cancel_WhileDragging_RestoresShapeAndLeavesNoRecord()
        {
            ClothEditController c = StartController(CreateTarget(true));
            PinStroke(c, true, new Vector3(-0.3f, 0f, 0f), new Vector3(-0.2f, 0f, 0f));
            int pinned = c.Session.PinnedCount;
            NVector3[] before = Positions(c);

            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward), Is.True);
            for (int k = 1; k <= 4; k++)
            {
                c.PointerDrag(RayAt(Pulled(from, 0.02f * k)));
                Tick(c);
            }
            Assert.That(MaxDistance(Positions(c), before), Is.GreaterThan(0.01f));

            Assert.That(c.Cancel(), Is.True);
            Assert.That(c.IsBusy, Is.False);
            Assert.That(c.Session.IsGrabbing, Is.False);
            Assert.That(Positions(c), Is.EqualTo(before));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned));

            // 取り消したあとのドラッグと離すは無視される
            c.PointerDrag(RayAt(Pulled(from, 0.1f)));
            Tick(c);
            c.PointerUp();
            Assert.That(Positions(c), Is.EqualTo(before));

            // 記録が残っていなければ、1回の Undo でピン留めが戻る
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
        }

        [Test]
        public void Cancel_WhileSettling_RestoresShape()
        {
            ClothEditController c = StartController(CreateTarget(true));
            NVector3[] before = Positions(c);
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.1f);
            Assert.That(c.IsSettling, Is.True);

            Assert.That(c.Cancel(), Is.True);
            Assert.That(c.IsBusy, Is.False);
            Assert.That(Positions(c), Is.EqualTo(before));
            Assert.That(c.Cancel(), Is.False, "取り消すものがなければ false");
        }

        [Test]
        public void Cancel_WhilePaintingPins_RemovesThePins()
        {
            ClothEditController c = StartController(CreateTarget(true));
            c.Mode = ClothEditMode.Pin;
            Assert.That(c.PointerDown(RayAt(new Vector3(-0.2f, 0f, 0f)), CameraForward), Is.True);
            c.PointerDrag(RayAt(new Vector3(0.2f, 0f, 0f)));
            Assert.That(c.Session.PinnedCount, Is.GreaterThan(0));

            Assert.That(c.Cancel(), Is.True);
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
        }

        [Test]
        public void Cancel_WhenIdle_DoesNothing()
        {
            ClothEditController c = StartController(CreateTarget(true));
            Assert.That(c.Cancel(), Is.False);
            Assert.DoesNotThrow(() => new ClothEditController().Cancel());
        }

        // ---- 保存 ----

        [TestCase(true)]
        [TestCase(false)]
        public void Save_AddsBlendShape_ContinuesEditing_AndSecondSaveAddsAnother(bool skinned)
        {
            Renderer renderer = CreateTarget(skinned);
            ClothEditController c = StartController(renderer);
            PinStroke(c, true, new Vector3(-0.35f, 0f, 0f), new Vector3(-0.3f, 0f, 0f));
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            Mesh original = c.Session.SourceMesh;
            int particles = c.Session.ParticleCount;
            int pinned = c.Session.PinnedCount;
            NVector3[] edited = Positions(c);

            Assert.That(c.Save("First", out string message), Is.True, message);
            Assert.That(message, Does.Contain("First"));
            Mesh saved = c.LastSavedMesh;
            _clearUndoOnly.Add(saved);
            Assert.That(saved, Is.Not.SameAs(original));
            Assert.That(AssetDatabase.GetAssetPath(saved), Does.StartWith(Folder));
            Assert.That(saved.GetBlendShapeIndex("First"), Is.EqualTo(0));
            if (skinned)
            {
                Assert.That(((SkinnedMeshRenderer)renderer).sharedMesh, Is.SameAs(saved));
                Assert.That(((SkinnedMeshRenderer)renderer).GetBlendShapeWeight(0), Is.EqualTo(100f));
            }
            else
            {
                Assert.That(renderer.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(saved));
            }

            // 編集が続けられる：粒子数・ピン・形が引き継がれる
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.SourceMesh, Is.SameAs(saved));
            Assert.That(c.Session.ParticleCount, Is.EqualTo(particles));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned));
            Assert.That(MaxDistance(Positions(c), edited), Is.LessThan(1e-5f));

            GrabStroke(c, new Vector3(-0.1f, 0.1f, 0f), 0.06f);
            SettleUntilDone(c);
            Assert.That(c.Save("Second", out message), Is.True, message);
            Assert.That(c.LastSavedMesh, Is.SameAs(saved), "2回目は同じメッシュにシェイプキーを足す");
            Assert.That(saved.blendShapeCount, Is.EqualTo(2));
            Assert.That(saved.GetBlendShapeIndex("Second"), Is.EqualTo(1));
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned));

            // 保存した編集はデータにも記録され、あとで一覧から扱える
            var component = renderer.GetComponent<GP02ShapeKeys>();
            Assert.That(component, Is.Not.Null);
            Assert.That(component.Data, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(component.Data), Does.StartWith(Folder));
            Assert.That(component.Data.SourceMesh, Is.SameAs(original), "データの元は、差し替える前のメッシュ");
            Assert.That(component.Data.FindEntry("First"), Is.Not.Null);
            Assert.That(component.Data.FindEntry("Second"), Is.Not.Null);
            Vector3[] firstDeltas = component.Data.FindEntry("First").Frames[0].DeltaVertices;
            Vector3[] meshDeltas = new Vector3[saved.vertexCount];
            saved.GetBlendShapeFrameVertices(0, 0, meshDeltas, null, null);
            float maxDifference = 0f;
            for (int i = 0; i < meshDeltas.Length; i++) maxDifference = Mathf.Max(maxDifference, (meshDeltas[i] - firstDeltas[i]).magnitude);
            Assert.That(maxDifference, Is.LessThan(1e-5f), "データとシェイプキーの差分が同じ");
        }

        [Test]
        public void Save_RejectsEmptyAndDuplicateNames_WithHelpfulMessages()
        {
            ClothEditController c = StartController(CreateTarget(true));
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.08f);
            SettleUntilDone(c);

            Assert.That(c.Save("  ", out string message), Is.False);
            Assert.That(message, Does.Contain("名前"));

            Assert.That(c.Save("Shape", out message), Is.True, message);
            _clearUndoOnly.Add(c.LastSavedMesh);
            Assert.That(c.Save("Shape", out message), Is.False);
            Assert.That(message, Does.Contain("別の名前"));
            Assert.That(c.IsEditing, Is.True);

            c.End();
            Assert.That(c.Save("Other", out message), Is.False);
            Assert.That(message, Does.Contain("編集を開始"));
        }

        [Test]
        public void UndoingASave_RestoresOriginalMesh_AndRebuildsEditing()
        {
            SkinnedMeshRenderer renderer = (SkinnedMeshRenderer)CreateTarget(true);
            ClothEditController c = StartController(renderer);
            PinStroke(c, true, new Vector3(-0.35f, 0f, 0f), new Vector3(-0.3f, 0f, 0f));
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            Mesh original = renderer.sharedMesh;
            int particles = c.Session.ParticleCount;
            int pinned = c.Session.PinnedCount;

            Assert.That(c.Save("Saved", out string message), Is.True, message);
            Mesh saved = c.LastSavedMesh;
            _clearUndoOnly.Add(saved);
            Assert.That(renderer.sharedMesh, Is.SameAs(saved));

            Undo.PerformUndo();
            Assert.That(renderer.sharedMesh, Is.SameAs(original), "メッシュの差し替えが戻る");
            Assert.That(original.blendShapeCount, Is.EqualTo(0));
            Assert.That(c.IsEditing, Is.True, "編集が作り直される");
            Assert.That(c.Session.SourceMesh, Is.SameAs(original));
            Assert.That(c.Session.ParticleCount, Is.EqualTo(particles));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned), "ピンは引き継がれる");
            Assert.That(renderer.forceRenderingOff, Is.True);

            Undo.PerformRedo();
            Assert.That(renderer.sharedMesh, Is.SameAs(saved));
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.SourceMesh, Is.SameAs(saved));
            // 保存したメッシュから粒子を置き直すので、ピンは位置で引き継ぐ。粒子の並びが変わるため、数は少しずれる
            Assert.That(c.Session.PinnedCount, Is.InRange(pinned * 0.8f, pinned * 1.2f));

            // 作り直した編集で、もう一度つまめる
            NVector3[] before = Positions(c);
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.05f);
            SettleUntilDone(c);
            Assert.That(MaxDistance(before, Positions(c)), Is.GreaterThan(0.02f));
        }

        // ---- 対象が消えたとき ----

        [Test]
        public void DestroyingTarget_WhenIdle_EndsEditingWithoutException()
        {
            Renderer renderer = CreateTarget(true);
            ClothEditController c = StartController(renderer);

            Object.DestroyImmediate(renderer.gameObject);

            Assert.DoesNotThrow(() => Tick(c));
            Assert.That(c.IsEditing, Is.False);
            Assert.DoesNotThrow(() => c.Hover(RayAt(Vector3.zero)));
            Assert.That(c.PointerDown(RayAt(Vector3.zero), CameraForward), Is.False);
            Assert.That(c.Save("X", out string message), Is.False);
            Assert.That(message, Is.Not.Empty);
        }

        [Test]
        public void DestroyingTarget_WhileDragging_EndsEditingWithoutException()
        {
            Renderer renderer = CreateTarget(true);
            ClothEditController c = StartController(renderer);
            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward), Is.True);
            c.PointerDrag(RayAt(Pulled(from, 0.05f)));
            Tick(c);

            Object.DestroyImmediate(renderer.gameObject);

            Assert.DoesNotThrow(() =>
            {
                c.PointerDrag(RayAt(Pulled(from, 0.08f)));
                Tick(c);
                c.PointerUp();
                c.Cancel();
                Tick(c);
            });
            Assert.That(c.IsEditing, Is.False);
            Assert.That(c.IsBusy, Is.False);
        }

        [Test]
        public void UndoAfterDestroyingTarget_EndsEditingWithoutException()
        {
            Renderer renderer = CreateTarget(true);
            ClothEditController c = StartController(renderer);
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.05f);
            SettleUntilDone(c);

            Object.DestroyImmediate(renderer.gameObject);
            Assert.DoesNotThrow(() => Undo.PerformUndo());
            Assert.That(c.IsEditing, Is.False);
        }

        // ---- 開始できないとき・読み込み ----

        [Test]
        public void TryStart_ExplainsWhatToDo_WhenTargetIsUnusable()
        {
            var c = new ClothEditController();
            _controllers.Add(c);

            Assert.That(c.TryStart(null, out string error), Is.False);
            Assert.That(error, Does.Contain("対象"));
            Assert.That(c.LastError, Is.EqualTo(error));
            Assert.That(c.IsEditing, Is.False);

            GameObject noMesh = Track(new GameObject("NoMesh"));
            noMesh.AddComponent<MeshFilter>();
            Assert.That(c.TryStart(noMesh.AddComponent<MeshRenderer>(), out error), Is.False);
            Assert.That(error, Does.Contain("メッシュ"));
            Assert.That(error, Does.Contain("NoMesh"));

            var points = Track(new Mesh { name = "NoFaces" });
            points.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            GameObject noFaces = Track(new GameObject("NoFaces"));
            noFaces.AddComponent<MeshFilter>().sharedMesh = points;
            Assert.That(c.TryStart(noFaces.AddComponent<MeshRenderer>(), out error), Is.False);
            Assert.That(error, Does.Contain("三角形"));

            // 成功するとエラーは消える
            Assert.That(c.TryStart(CreateTarget(true), out error), Is.True, error);
            Assert.That(c.LastError, Is.Empty);
        }

        [Test]
        public void UnreadableMesh_CanBeEditedInTheEditor()
        {
            // Read/Write が無効なメッシュ（UploadMeshData(true) でそうなる）も、エディタでは頂点と三角形を読める。
            // 確かめた結果（Unity 2022.3.22f1）：例外にならず、そのまま編集と保存ができる
            Renderer renderer = CreateTarget(true);
            Mesh mesh = ClothEditController.GetMesh(renderer);
            mesh.UploadMeshData(true);
            Assert.That(mesh.isReadable, Is.False);
            Assert.That(ClothEditController.IsMeshUnreadable(renderer), Is.True);

            ClothEditController c = StartController(renderer);
            Assert.That(c.Session.ParticleCount, Is.GreaterThan(0));
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.05f);
            SettleUntilDone(c);
            Assert.That(c.Save("Unreadable", out string message), Is.True, message);
            _clearUndoOnly.Add(c.LastSavedMesh);
            Assert.That(c.LastSavedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test]
        public void StartingEdit_WarmsUpTheSolverWithoutChangingTheShape()
        {
            Renderer renderer = CreateTarget(true);
            ClothEditController c = StartController(renderer);

            Assert.That(Positions(c), Is.EqualTo(c.Session.Cloth.Model.RestPositions));
            Assert.That(c.Session.MatchesState(ReadBackState(c)), Is.True);

            Assert.That(c.Session.WarmUp(), Is.GreaterThanOrEqualTo(0.0));
            Assert.That(Positions(c), Is.EqualTo(c.Session.Cloth.Model.RestPositions), "何度呼んでも形は変わらない");
        }

        // 今の状態を書いた ClothEditState（テスト用。Undo には登録しない）
        ClothEditState ReadBackState(ClothEditController c)
        {
            ClothEditState state = Track(ClothEditState.Create());
            c.Session.WriteState(state);
            return state;
        }

        // ---- 設定 ----

        [Test]
        public void Settings_AreAppliedToTheSolver_AndCanBeChangedWhileEditing()
        {
            var settings = new ClothEditSettings
            {
                BendCompliance = 0.5f,
                StretchCompliance = 0.02f,
                CoarseIterations = 3,
                UseAcceleration = false,
                SolveMilliseconds = 4f,
                BrushCompliance = 0.1f,
            };
            ClothEditController c = StartController(CreateTarget(true), settings);

            ClothSolver solver = c.Session.Solver;
            Assert.That(solver.BendCompliance, Is.EqualTo(0.5f));
            Assert.That(solver.StretchCompliance, Is.EqualTo(0.02f));
            Assert.That(solver.CoarseIterations, Is.EqualTo(3));
            Assert.That(solver.Kernel, Is.Null, "高速版をオフにすると C# で計算する");
            Assert.That(solver.BrushCompliance, Is.EqualTo(0.1f));

            // C# の計算でもつまんで動かせる
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.05f);
            SettleUntilDone(c);
            Assert.That(c.Session.Solver.Positions.ToArray(), Is.Not.EqualTo(c.Session.Cloth.Model.RestPositions));

            settings.BendCompliance = 0.05f;
            settings.UseAcceleration = true;
            settings.BrushCompliance = 0f;
            c.ApplySettings();
            Assert.That(solver.BrushCompliance, Is.EqualTo(0f));
            Assert.That(solver.BendCompliance, Is.EqualTo(0.05f));
            Assert.That(solver.Kernel, Is.SameAs(ClothSolver.DefaultKernel));
        }

        [Test]
        public void SettleSettings_DecideWhenSettlingEnds()
        {
            // 許容する伸びを大きくすると、1回の Tick で終わる
            var loose = new ClothEditSettings { SolveMilliseconds = 4f, SettleTolerance = 10f };
            ClothEditController c = StartController(CreateTarget(true), loose);
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.1f);
            Assert.That(c.IsSettling, Is.True);
            Tick(c);
            Assert.That(c.IsBusy, Is.False);

            // 許容する伸びが 0 でも、時間切れで終わる（開始の Tick のあと、時間切れより後の Tick で終わる）
            var strict = new ClothEditSettings { SolveMilliseconds = 4f, SettleTolerance = 0f, SettleTimeoutSeconds = 0.05f };
            ClothEditController d = StartController(CreateTarget(true, "Strict"), strict);
            GrabStroke(d, new Vector3(0.1f, 0f, 0f), 0.1f);
            Assert.That(d.IsSettling, Is.True);
            d.Tick(1000.0);
            d.Tick(1000.2);
            Assert.That(d.IsBusy, Is.False);
            Assert.That(d.Session.IsGrabbing, Is.False);
        }

        [Test]
        public void Settings_DefaultsMatchTheSolver_AndAreSanitized()
        {
            ProceduralMeshes.Grid(4, 4, 0.1f, 0.1f, out var vertices, out var triangles);
            ClothSolver solver = ParticleCloth.Build(vertices, triangles).CreateSolver();
            var settings = new ClothEditSettings();
            Assert.That(settings.BendCompliance, Is.EqualTo(solver.BendCompliance));
            Assert.That(settings.StretchCompliance, Is.EqualTo(solver.StretchCompliance));
            Assert.That(settings.CoarseIterations, Is.EqualTo(solver.CoarseIterations));
            Assert.That(settings.KeepOutsideRestSurface, Is.EqualTo(solver.KeepOutsideRestSurface));
            Assert.That(settings.BrushCompliance, Is.EqualTo(solver.BrushCompliance));
            Assert.That(settings.BrushCompliance, Is.EqualTo(0.02f));
            Assert.That(settings.UseRestSurfaceAllowance, Is.True);
            Assert.That(settings.SettleTolerance, Is.EqualTo(0.01f));
            Assert.That(settings.SettleTimeoutSeconds, Is.EqualTo(2f));
            Assert.That(settings.SolveMilliseconds, Is.EqualTo(12f));

            settings.BendCompliance = -1f;
            settings.StretchCompliance = float.NaN;
            settings.CoarseIterations = -4;
            settings.BrushCompliance = -0.5f;
            settings.Sanitize();
            Assert.That(settings.BrushCompliance, Is.EqualTo(ClothEditSettings.DefaultBrushCompliance));
            settings.BrushCompliance = 5f;
            settings.Sanitize();
            Assert.That(settings.BrushCompliance, Is.EqualTo(ClothEditSettings.MaxBrushCompliance), "上限で頭打ち");
            settings.BrushCompliance = float.NaN;
            settings.Sanitize();
            Assert.That(settings.BrushCompliance, Is.EqualTo(ClothEditSettings.DefaultBrushCompliance));
            Assert.That(settings.BendCompliance, Is.EqualTo(ClothEditSettings.DefaultBendCompliance));
            Assert.That(settings.StretchCompliance, Is.EqualTo(ClothEditSettings.DefaultStretchCompliance));
            Assert.That(settings.CoarseIterations, Is.EqualTo(ClothEditSettings.DefaultCoarseIterations));
        }

        [Test]
        public void Settings_SaveToEditorPrefs_AndResetToDefaults()
        {
            var settings = new ClothEditSettings
            {
                BendCompliance = 0.4f,
                StretchCompliance = 0.01f,
                SettleTolerance = 0.05f,
                SettleTimeoutSeconds = 5f,
                SolveMilliseconds = 20f,
                CoarseIterations = 7,
                UseAcceleration = false,
                BrushCompliance = 0.15f,
                UseRestSurfaceAllowance = false,
            };
            settings.Save(TestKeyPrefix);

            ClothEditSettings loaded = ClothEditSettings.Load(TestKeyPrefix);
            Assert.That(loaded.BendCompliance, Is.EqualTo(0.4f));
            Assert.That(loaded.StretchCompliance, Is.EqualTo(0.01f));
            Assert.That(loaded.SettleTolerance, Is.EqualTo(0.05f));
            Assert.That(loaded.SettleTimeoutSeconds, Is.EqualTo(5f));
            Assert.That(loaded.SolveMilliseconds, Is.EqualTo(20f));
            Assert.That(loaded.CoarseIterations, Is.EqualTo(7));
            Assert.That(loaded.UseAcceleration, Is.False);
            Assert.That(loaded.BrushCompliance, Is.EqualTo(0.15f));
            Assert.That(loaded.UseRestSurfaceAllowance, Is.False);

            loaded.ResetToDefaults();
            Assert.That(loaded.BendCompliance, Is.EqualTo(ClothEditSettings.DefaultBendCompliance));
            Assert.That(loaded.UseAcceleration, Is.True);
            Assert.That(loaded.BrushCompliance, Is.EqualTo(ClothEditSettings.DefaultBrushCompliance));
            Assert.That(loaded.UseRestSurfaceAllowance, Is.True);

            ClothEditSettings.DeleteSaved(TestKeyPrefix);
            ClothEditSettings afterDelete = ClothEditSettings.Load(TestKeyPrefix);
            Assert.That(afterDelete.CoarseIterations, Is.EqualTo(ClothEditSettings.DefaultCoarseIterations));
            Assert.That(afterDelete.BrushCompliance, Is.EqualTo(ClothEditSettings.DefaultBrushCompliance));
            Assert.That(afterDelete.UseRestSurfaceAllowance, Is.True);
        }
    }
}
