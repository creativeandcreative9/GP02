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
    /// 編集画面の道具（回す・拡大縮小・元に戻すブラシ）、対称編集、動かす向き、精密操作、ホイール、見える化、警告、設定を、
    /// ウィンドウなしで確かめる。形は Unity の Quad（XY 平面、1m 四方、x=0 で左右対称）。
    /// </summary>
    public class ClothEditToolTests
    {
        const string TempRoot = "Assets/GP02_ToolTestTemp";
        const string TestKeyPrefix = "GP02.Test.ToolSettings.";

        // 斜め手前（-Z 側の下）から布（XY 平面の Quad）を見る
        static readonly Vector3 CameraPosition = new Vector3(0.1f, -1f, -1f);
        static readonly Vector3 CameraForward = new Vector3(0f, 1f, 1f).normalized;

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

        Renderer CreateTarget(string name = "ToolTarget", Transform parent = null)
        {
            Mesh mesh = Track(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Quad.fbx")));
            GameObject go = Track(new GameObject(name));
            if (parent != null) go.transform.SetParent(parent, true);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            _clearUndoOnly.Add(renderer);
            return renderer;
        }

        // 5cm 間隔の頂点を持つ 1m 四方の布（XY 平面、x=0 で左右対称、-Z 向き）。Quad は頂点が4つしかなく、粒子で作った盛り上がりが
        // 見える形（レイキャストの対象）に出ないので、形が変わった所を狙うテストにはこちらを使う
        Renderer CreateGridTarget(string name = "GridTarget")
        {
            ProceduralMeshes.Grid(20, 20, 1f, 1f, out NVector3[] grid, out int[] gridTriangles);
            var vertices = new Vector3[grid.Length];
            for (int i = 0; i < grid.Length; i++) vertices[i] = new Vector3(grid[i].X - 0.5f, grid[i].Y - 0.5f, 0f);
            Mesh mesh = Track(new Mesh { name = "GridCloth" });
            mesh.vertices = vertices;
            mesh.triangles = gridTriangles;
            GameObject go = Track(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            _clearUndoOnly.Add(renderer);
            return renderer;
        }

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 4f };
            var controller = new ClothEditController(settings) { SaveFolder = TempRoot + "/Meshes", BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            _controllers.Add(controller);
            Assert.That(controller.TryStart(renderer, out string error), Is.True, error);
            return controller;
        }

        static Ray RayAt(Vector3 point) => new Ray(CameraPosition, (point - CameraPosition).normalized);

        static Vector3 Pulled(Vector3 from, float s) => from + new Vector3(0f, s, -s);

        void Tick(ClothEditController c)
        {
            _time += 0.016;
            c.Tick(_time);
        }

        void SettleUntilDone(ClothEditController c)
        {
            for (int i = 0; i < 1000 && c.IsBusy; i++) Tick(c);
            Assert.That(c.IsBusy, Is.False, "仕上げが終わらない");
        }

        void GrabStroke(ClothEditController c, Vector3 from, float distance, int steps = 5, bool precise = false)
        {
            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            for (int k = 1; k <= steps; k++)
            {
                c.PointerDrag(RayAt(Pulled(from, distance * k / steps)), 100f, precise, false);
                Tick(c);
            }
            c.PointerUp();
        }

        // 横に pixels だけドラッグして離す（回す・拡大縮小）
        void SidewaysStroke(ClothEditController c, Vector3 at, float pixels, bool precise = false, bool snap = false, int steps = 5)
        {
            Assert.That(c.PointerDown(RayAt(at), CameraForward, 100f), Is.True);
            for (int k = 1; k <= steps; k++)
            {
                c.PointerDrag(RayAt(at), 100f + pixels * k / steps, precise, snap);
                Tick(c);
            }
            c.PointerUp();
        }

        void PaintStroke(ClothEditController c, ClothEditMode mode, params Vector3[] points)
        {
            c.Mode = mode;
            Assert.That(c.PointerDown(RayAt(points[0]), CameraForward, 100f), Is.True);
            for (int i = 1; i < points.Length; i++) c.PointerDrag(RayAt(points[i]));
            c.PointerUp();
        }

        static NVector3[] Positions(ClothEditController c) => c.Session.Solver.Positions.ToArray();

        static Vector3 ToUnity(NVector3 v) => new Vector3(v.X, v.Y, v.Z);

        static float MaxDistance(NVector3[] a, NVector3[] b)
        {
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, NVector3.Distance(a[i], b[i]));
            return max;
        }

        static Vector3 ParticleAt(ClothEditController c, Vector3 point) => ToUnity(c.Session.Solver.Positions[c.Session.FindParticle(point, -1)]);

        // ---- 回す ----

        [Test]
        public void Rotate_AroundTheClickedPoint_AboutTheSurfaceNormal()
        {
            // つまむ柔らかさを 0 にして、つまんだ範囲が目標にぴったり置かれるようにする（柔らかいと、周りの布の抵抗で目標まで回りきらない）
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, RotateAxis = RotateAxisMode.SurfaceNormal, BrushCompliance = 0f };
            ClothEditController c = StartController(CreateTarget(), settings);
            c.Mode = ClothEditMode.Rotate;
            int probe = c.Session.FindParticle(new Vector3(0.13f, 0f, 0f), -1);
            Vector3 p0 = ToUnity(c.Session.Solver.Positions[probe]);

            // 30 ピクセル = 15°
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward, 100f), Is.True);
            Vector3 pivot = c.HoverPoint;
            for (int k = 1; k <= 3; k++)
            {
                c.PointerDrag(RayAt(new Vector3(0.1f, 0f, 0f)), 100f + 10f * k, false, false);
                Tick(c);
            }
            Assert.That(c.GrabAngle, Is.EqualTo(15f).Within(1e-3f));

            // 面の法線は視点側（-Z）を向く。右へドラッグすると、視点から見て時計回り
            Quaternion rotation = Quaternion.AngleAxis(15f, new Vector3(0f, 0f, -1f));
            Vector3 expected = pivot + rotation * (p0 - pivot);
            Vector3 actual = ToUnity(c.Session.Solver.Positions[probe]);
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(0.0015f), $"回した位置 {actual} と期待 {expected}（元 {p0}）");
            Assert.That(Vector3.Distance(actual, p0), Is.GreaterThan(0.005f), "実際に動いている");

            c.PointerUp();
            Assert.That(c.IsSettling, Is.True);
            SettleUntilDone(c);
        }

        [Test]
        public void Rotate_IsOneUndoStep_AndCtrlSnapsTo15Degrees()
        {
            ClothEditController c = StartController(CreateTarget());
            c.Mode = ClothEditMode.Rotate;
            NVector3[] before = Positions(c);

            // 22 ピクセル = 11°。Ctrl で 15°
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward, 100f), Is.True);
            c.PointerDrag(RayAt(new Vector3(0.1f, 0f, 0f)), 122f, false, false);
            Assert.That(c.GrabAngle, Is.EqualTo(11f).Within(1e-3f));
            c.PointerDrag(RayAt(new Vector3(0.1f, 0f, 0f)), 122f, false, true);
            Assert.That(c.GrabAngle, Is.EqualTo(15f).Within(1e-3f));
            Tick(c);
            c.PointerUp();
            SettleUntilDone(c);
            NVector3[] after = Positions(c);
            Assert.That(MaxDistance(before, after), Is.GreaterThan(0.002f));

            Undo.PerformUndo();
            Assert.That(Positions(c), Is.EqualTo(before), "1回の Undo で回す前に戻る");
            Undo.PerformRedo();
            Assert.That(Positions(c), Is.EqualTo(after));
        }

        [Test]
        public void Rotate_WithoutMoving_LeavesNoUndoRecord()
        {
            ClothEditController c = StartController(CreateTarget());
            PaintStroke(c, ClothEditMode.Pin, new Vector3(-0.3f, 0f, 0f), new Vector3(-0.2f, 0f, 0f));
            int pinned = c.Session.PinnedCount;
            NVector3[] before = Positions(c);

            c.Mode = ClothEditMode.Rotate;
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward, 100f), Is.True);
            c.PointerDrag(RayAt(new Vector3(0.1f, 0f, 0f)), 100f, false, false);
            c.PointerUp();
            Assert.That(c.IsBusy, Is.False, "動かさずに離せば仕上げに入らない");
            Assert.That(Positions(c), Is.EqualTo(before));

            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "空の記録は残らず、戻るのはピン留め");
            Assert.That(pinned, Is.GreaterThan(0));
        }

        [Test]
        public void Rotate_ViewAxisIsTheDefault_AndChangesTheShape()
        {
            ClothEditController c = StartController(CreateTarget());
            Assert.That(c.Settings.RotateAxis, Is.EqualTo(RotateAxisMode.View));
            c.Mode = ClothEditMode.Rotate;
            NVector3[] before = Positions(c);
            SidewaysStroke(c, new Vector3(0.1f, 0f, 0f), 40f);
            Assert.That(c.GrabAngle, Is.EqualTo(20f).Within(1e-3f));
            SettleUntilDone(c);
            Assert.That(MaxDistance(before, Positions(c)), Is.GreaterThan(0.003f));
        }

        // ---- 拡大縮小 ----

        [Test]
        public void ScaleFromPixels_IsOneAtZero_AndClampedTo0_2And3()
        {
            Assert.That(ClothEditController.ScaleFromPixels(0f), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(ClothEditController.ScaleFromPixels(100f), Is.EqualTo(Mathf.Exp(0.6f)).Within(1e-4f));
            Assert.That(ClothEditController.ScaleFromPixels(10000f), Is.EqualTo(ClothEditController.MaxScale));
            Assert.That(ClothEditController.ScaleFromPixels(-10000f), Is.EqualTo(ClothEditController.MinScale));
            Assert.That(ClothEditController.MinScale, Is.EqualTo(0.2f));
            Assert.That(ClothEditController.MaxScale, Is.EqualTo(3f));
            Assert.That(ClothEditController.AngleFromPixels(30f, false), Is.EqualTo(15f).Within(1e-4f));
            Assert.That(ClothEditController.AngleFromPixels(22f, true), Is.EqualTo(15f).Within(1e-4f));
            Assert.That(ClothEditController.AngleFromPixels(-22f, true), Is.EqualTo(-15f).Within(1e-4f));
            Assert.That(ClothEditController.AngleFromPixels(10f, true), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void Scale_TheClickedPointStaysPut_AndIsOneUndoStep()
        {
            ClothEditController c = StartController(CreateTarget());
            c.Mode = ClothEditMode.Scale;
            NVector3[] before = Positions(c);

            // 小さくする（縮める方向は布が伸びないので無理なく動く）
            SidewaysStroke(c, new Vector3(0.1f, 0f, 0f), -100f);
            Assert.That(c.GrabScale, Is.EqualTo(Mathf.Exp(-0.6f)).Within(1e-3f));
            SettleUntilDone(c);
            NVector3[] after = Positions(c);
            Assert.That(MaxDistance(before, after), Is.GreaterThan(0.001f), "縮める向きは周りの布に抵抗されるが、動いてはいる");

            // 中心の粒子はほとんど動かない
            int center = c.Session.FindParticle(new Vector3(0.1f, 0f, 0f), -1);
            Assert.That(NVector3.Distance(before[center], after[center]), Is.LessThan(0.01f));

            Undo.PerformUndo();
            Assert.That(Positions(c), Is.EqualTo(before));
            Undo.PerformRedo();
            Assert.That(Positions(c), Is.EqualTo(after));
        }

        [Test]
        public void Scale_DragFarKeepsTheFactorWithinRange()
        {
            ClothEditController c = StartController(CreateTarget());
            c.Mode = ClothEditMode.Scale;
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward, 100f), Is.True);
            c.PointerDrag(RayAt(new Vector3(0.1f, 0f, 0f)), 5000f, false, false);
            Assert.That(c.GrabScale, Is.EqualTo(3f));
            c.PointerDrag(RayAt(new Vector3(0.1f, 0f, 0f)), -5000f, false, false);
            Assert.That(c.GrabScale, Is.EqualTo(0.2f));
            Assert.That(c.Cancel(), Is.True);
        }

        // ---- 元に戻すブラシ ----

        [Test]
        public void Relax_BringsThePaintedAreaBackTowardsRest_AndIsOneUndoStep()
        {
            ClothEditController c = StartController(CreateGridTarget());
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            NVector3[] edited = Positions(c);

            int lifted = 0;
            float most = 0f;
            for (int i = 0; i < edited.Length; i++)
            {
                float d = NVector3.Distance(edited[i], c.Session.Cloth.Model.RestPositions[i]);
                if (d > most) { most = d; lifted = i; }
            }
            Assert.That(most, Is.GreaterThan(0.05f));

            c.Settings.RelaxStrength = 1f;
            c.BrushRadius = 0.06f;
            // 持ち上がった所の真上（視点から見える位置）を塗る
            PaintStroke(c, ClothEditMode.Relax, ToUnity(edited[lifted]));
            Assert.That(c.IsSettling, Is.True, "手を離すと仕上げに入る");
            SettleUntilDone(c);

            float after = NVector3.Distance(c.Session.Solver.Positions[lifted], c.Session.Cloth.Model.RestPositions[lifted]);
            Assert.That(after, Is.LessThan(most * 0.3f), $"戻した所は留めたまま、周りが合わせる（{most:F3} → {after:F3}）");
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "仕上げの間だけ留める。終われば外れる");

            Undo.PerformUndo();
            Assert.That(Positions(c), Is.EqualTo(edited), "1回の Undo で塗る前に戻る");
        }

        [Test]
        public void Relax_DoesNotMovePinnedParticles_AndNothingToDoLeavesNoRecord()
        {
            ClothEditController c = StartController(CreateTarget());
            NVector3[] rest = Positions(c);
            // 何も動かしていない布に塗っても、形は変わらず、Undo の記録も残らない
            c.Settings.RelaxStrength = 1f;
            PaintStroke(c, ClothEditMode.Relax, new Vector3(0.1f, 0f, 0f));
            Assert.That(c.IsBusy, Is.False);
            Assert.That(Positions(c), Is.EqualTo(rest));
        }

        // ---- 対称編集 ----

        static ClothEditSettings SymmetricSettings() => new ClothEditSettings { SolveMilliseconds = 4f, SymmetryEnabled = true, SymmetryAxis = SymmetryAxis.X };

        // 粒子 i の鏡像の粒子 j との、左右対称からのずれ（j の位置と、i の位置を鏡に映した点の距離）
        static float Asymmetry(ClothEditController c, int i)
        {
            Assert.That(c.TryGetMirrorPlane(out MirrorPlane plane), Is.True);
            int j = c.Session.Solver.FindMirrorParticle(i, plane, 0.02f);
            Assert.That(j, Is.GreaterThanOrEqualTo(0));
            return NVector3.Distance(c.Session.Solver.Positions[j], plane.Reflect(c.Session.Solver.Positions[i]));
        }

        [Test]
        public void Symmetry_Grab_MovesTheMirrorSideToo()
        {
            ClothEditController c = StartController(CreateTarget(), SymmetricSettings());
            int seed = c.Session.FindParticle(new Vector3(0.2f, 0f, 0f), -1);
            GrabStroke(c, new Vector3(0.2f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            Assert.That(NVector3.Distance(c.Session.Solver.Positions[seed], c.Session.Cloth.Model.RestPositions[seed]), Is.GreaterThan(0.05f));
            Assert.That(Asymmetry(c, seed), Is.LessThan(0.006f), "左右対称に動く");

            // 対称がオフなら、反対側はこの場所では動かない
            var plain = new ClothEditSettings { SolveMilliseconds = 4f };
            ClothEditController d = StartController(CreateTarget("Plain"), plain);
            GrabStroke(d, new Vector3(0.2f, 0f, 0f), 0.08f);
            SettleUntilDone(d);
            d.Settings.SymmetryEnabled = true;
            Assert.That(Asymmetry(d, seed), Is.GreaterThan(0.03f), "対称でないので左右がずれている");
        }

        [Test]
        public void Symmetry_Grab_IsOneUndoStep()
        {
            ClothEditController c = StartController(CreateTarget(), SymmetricSettings());
            NVector3[] before = Positions(c);
            GrabStroke(c, new Vector3(0.2f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            NVector3[] after = Positions(c);
            Undo.PerformUndo();
            Assert.That(Positions(c), Is.EqualTo(before));
            Undo.PerformRedo();
            Assert.That(Positions(c), Is.EqualTo(after));
        }

        [Test]
        public void Symmetry_Rotate_MirrorsTheRotation()
        {
            var settings = SymmetricSettings();   // 回転軸は視線の向き（斜めなので、布が面の外へ傾く）
            settings.BrushCompliance = 0f;        // つまんだ範囲を目標にぴったり置く（周りの布の抵抗で、左右がずれないように）
            ClothEditController c = StartController(CreateTarget(), settings);
            c.Mode = ClothEditMode.Rotate;
            Vector3 at = new Vector3(0.25f, 0.05f, 0f);
            int probe = c.Session.FindParticle(new Vector3(0.28f, 0.05f, 0f), -1);
            Assert.That(c.PointerDown(RayAt(at), CameraForward, 100f), Is.True);
            for (int k = 1; k <= 3; k++)
            {
                c.PointerDrag(RayAt(at), 100f + 20f * k, false, false);
                Tick(c);
            }
            float moved = NVector3.Distance(c.Session.Solver.Positions[probe], c.Session.Cloth.Model.RestPositions[probe]);
            Assert.That(moved, Is.GreaterThan(0.005f));
            // 粒子の格子は左右で半粒子分ずれることがあるので、ずれは動きの半分強まで許す（回していなければ、ずれは動きと同じ大きさになる）
            Assert.That(Asymmetry(c, probe), Is.LessThan(moved * 0.6f), $"反対側は鏡に映したように回る（動き {moved}）");
            c.PointerUp();
            SettleUntilDone(c);
        }

        [Test]
        public void Symmetry_Pin_PinsBothSides_AndUnpinClearsBoth_InOneUndoStep()
        {
            ClothEditController c = StartController(CreateTarget(), SymmetricSettings());
            PaintStroke(c, ClothEditMode.Pin, new Vector3(0.3f, 0.1f, 0f), new Vector3(0.3f, 0.2f, 0f));
            int pinned = c.Session.PinnedCount;
            Assert.That(pinned, Is.GreaterThan(10));

            int right = 0, left = 0;
            NVector3[] p = Positions(c);
            for (int i = 0; i < p.Length; i++)
            {
                if (!c.Session.Solver.IsPinned(i)) continue;
                if (p[i].X > 0f) right++;
                else left++;
            }
            Assert.That(left, Is.GreaterThan(0));
            Assert.That(right, Is.GreaterThan(0));
            Assert.That(Mathf.Abs(left - right), Is.LessThanOrEqualTo(Mathf.Max(4, pinned / 8)), $"左右の数が近い（左 {left} / 右 {right}）");

            // 反対側から消しても、両側とも消える
            PaintStroke(c, ClothEditMode.Unpin, new Vector3(-0.3f, 0.1f, 0f), new Vector3(-0.3f, 0.2f, 0f));
            Assert.That(c.Session.PinnedCount, Is.LessThan(pinned / 4), "反対側から解除しても両側が外れる");

            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned), "ピン解除も1回の操作");
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "ピン留めも両側まとめて1回の操作");
        }

        [Test]
        public void Symmetry_Relax_RestoresBothSides()
        {
            ClothEditController c = StartController(CreateGridTarget(), SymmetricSettings());
            GrabStroke(c, new Vector3(0.2f, 0f, 0f), 0.08f);
            SettleUntilDone(c);
            NVector3[] edited = Positions(c);
            int seed = c.Session.FindParticle(new Vector3(0.2f, 0f, 0f), -1);
            seed = c.Session.FindParticle(ToUnity(edited[seed]), -1);
            int mirror = c.Session.Solver.FindMirrorParticle(seed, SymmetricPlane(c), 0.02f);
            float seedBefore = NVector3.Distance(edited[seed], c.Session.Cloth.Model.RestPositions[seed]);
            float mirrorBefore = NVector3.Distance(edited[mirror], c.Session.Cloth.Model.RestPositions[mirror]);

            c.Settings.RelaxStrength = 1f;
            c.BrushRadius = 0.06f;
            PaintStroke(c, ClothEditMode.Relax, ToUnity(edited[seed]));
            SettleUntilDone(c);
            Assert.That(NVector3.Distance(c.Session.Solver.Positions[seed], c.Session.Cloth.Model.RestPositions[seed]), Is.LessThan(seedBefore * 0.3f));
            Assert.That(NVector3.Distance(c.Session.Solver.Positions[mirror], c.Session.Cloth.Model.RestPositions[mirror]), Is.LessThan(mirrorBefore * 0.3f), "反対側も戻る");
        }

        static MirrorPlane SymmetricPlane(ClothEditController c)
        {
            Assert.That(c.TryGetMirrorPlane(out MirrorPlane plane), Is.True);
            return plane;
        }

        [Test]
        public void Hover_ShowsAMirrorCircle_ExceptOnTheCenterLine()
        {
            ClothEditController c = StartController(CreateTarget(), SymmetricSettings());
            Assert.That(c.Hover(RayAt(new Vector3(0.2f, 0.1f, 0f))), Is.True);
            Assert.That(c.HasMirrorHover, Is.True);
            Assert.That(c.MirrorHoverPoint.x, Is.EqualTo(-0.2f).Within(0.012f));
            Assert.That(c.MirrorHoverPoint.y, Is.EqualTo(0.1f).Within(0.012f));

            Assert.That(c.Hover(RayAt(new Vector3(0.001f, 0.1f, 0f))), Is.True);
            Assert.That(c.HasMirrorHover, Is.False, "中央線の上では鏡像の円は出さない");

            c.Settings.SymmetryEnabled = false;
            Assert.That(c.Hover(RayAt(new Vector3(0.2f, 0.1f, 0f))), Is.True);
            Assert.That(c.HasMirrorHover, Is.False);
        }

        // ---- アバターの基準・鏡の面・軸 ----

        [Test]
        public void FindAvatarRoot_IsTheFirstParentWithAnAnimator_ElseTheTopmostParent_ElseItself()
        {
            Assert.That(ClothEditSymmetry.FindAvatarRoot(null), Is.Null);

            var top = Track(new GameObject("Top")).transform;
            var middle = Track(new GameObject("Middle")).transform;
            var cloth = Track(new GameObject("Cloth")).transform;
            middle.SetParent(top);
            cloth.SetParent(middle);
            Assert.That(ClothEditSymmetry.FindAvatarRoot(cloth), Is.SameAs(top), "Animator が無ければ一番上の親");

            middle.gameObject.AddComponent<Animator>();
            Assert.That(ClothEditSymmetry.FindAvatarRoot(cloth), Is.SameAs(middle), "Animator のある最初の親");

            top.gameObject.AddComponent<Animator>();
            Assert.That(ClothEditSymmetry.FindAvatarRoot(cloth), Is.SameAs(middle), "近い方");

            var alone = Track(new GameObject("Alone")).transform;
            alone.gameObject.AddComponent<Animator>();
            Assert.That(ClothEditSymmetry.FindAvatarRoot(alone), Is.SameAs(alone), "親が無ければ自分自身");
        }

        [Test]
        public void MirrorPlane_FollowsTheAvatarRoot_AxisAndOffset()
        {
            var root = Track(new GameObject("Avatar")).transform;
            root.gameObject.AddComponent<Animator>();
            root.SetPositionAndRotation(new Vector3(1f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));

            var settings = new ClothEditSettings { SymmetryEnabled = true, SymmetryAxis = SymmetryAxis.X, SymmetryOffset = 0.05f };
            Assert.That(ClothEditSymmetry.TryBuildMirrorPlane(settings, root, out MirrorPlane plane), Is.True);
            // ローカルの X は、Y まわりに 90° 回すとワールドの -Z
            Assert.That(ToUnity(plane.Normal).x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(ToUnity(plane.Normal).z, Is.EqualTo(-1f).Within(1e-5f));
            // ローカルの 5cm ずれ = ワールドの -Z へ 5cm
            Assert.That(Vector3.Distance(ToUnity(plane.Point), new Vector3(1f, 0f, -0.05f)), Is.LessThan(1e-5f));

            settings.SymmetryEnabled = false;
            Assert.That(ClothEditSymmetry.TryBuildMirrorPlane(settings, root, out _), Is.False, "オフなら作らない");
            settings.SymmetryEnabled = true;
            Assert.That(ClothEditSymmetry.TryBuildMirrorPlane(settings, null, out _), Is.False, "基準が無ければ作らない");

            settings.SymmetryAxis = SymmetryAxis.Y;
            Assert.That(ClothEditSymmetry.TryBuildMirrorPlane(settings, root, out MirrorPlane planeY), Is.True);
            Assert.That(ToUnity(planeY.Normal).y, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void MirrorPlane_UsesTheAnimatorParentOfTheRenderer()
        {
            var root = Track(new GameObject("Avatar")).transform;
            root.gameObject.AddComponent<Animator>();
            root.position = new Vector3(0.25f, 0f, 0f);
            Renderer renderer = CreateTarget("Body", root);
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, SymmetryEnabled = true };
            ClothEditController c = StartController(renderer, settings);

            Assert.That(c.AvatarRoot, Is.SameAs(root));
            Assert.That(c.TryGetMirrorPlane(out MirrorPlane plane), Is.True);
            Assert.That(plane.Point.X, Is.EqualTo(0.25f).Within(1e-5f));
            BrushOptions options = c.BuildBrushOptions();
            Assert.That(options.Mirror.HasValue, Is.True);
            Assert.That(options.Mirror.Value.Point.X, Is.EqualTo(0.25f).Within(1e-5f));
        }

        [Test]
        public void AxisDirection_WorldOrAvatarLocal()
        {
            var root = Track(new GameObject("Avatar")).transform;
            root.rotation = Quaternion.Euler(0f, 90f, 0f);

            Assert.That(ClothEditSymmetry.TryGetAxisDirection(MoveDirection.AxisX, AxisSpace.World, root, out Vector3 world), Is.True);
            Assert.That(world, Is.EqualTo(Vector3.right));
            Assert.That(ClothEditSymmetry.TryGetAxisDirection(MoveDirection.AxisX, AxisSpace.AvatarLocal, root, out Vector3 local), Is.True);
            Assert.That(Vector3.Distance(local, new Vector3(0f, 0f, -1f)), Is.LessThan(1e-5f));
            Assert.That(ClothEditSymmetry.TryGetAxisDirection(MoveDirection.Free, AxisSpace.World, root, out _), Is.False);
            Assert.That(ClothEditSymmetry.TryGetAxisDirection(MoveDirection.SurfaceNormal, AxisSpace.World, root, out _), Is.False);
        }

        // ---- 動かす向き・精密操作 ----

        [Test]
        public void TryClosestPointOnLine_FindsTheClosestPoint_AndRefusesParallelRays()
        {
            // 原点を通る X 軸。レイは (0.3, 1, 0) から -Y の向き → 最も近い点は (0.3, 0, 0)
            Assert.That(ClothEditController.TryClosestPointOnLine(new Ray(new Vector3(0.3f, 1f, 0f), Vector3.down), Vector3.zero, Vector3.right, out Vector3 point), Is.True);
            Assert.That(Vector3.Distance(point, new Vector3(0.3f, 0f, 0f)), Is.LessThan(1e-5f));
            // 斜めに見ていても、直線との最近点
            Assert.That(ClothEditController.TryClosestPointOnLine(new Ray(new Vector3(0.3f, 1f, 1f), new Vector3(0f, -1f, -1f)), Vector3.zero, Vector3.right, out point), Is.True);
            Assert.That(Vector3.Distance(point, new Vector3(0.3f, 0f, 0f)), Is.LessThan(1e-5f));
            // 直線と同じ向きに見ている → 決まらない
            Assert.That(ClothEditController.TryClosestPointOnLine(new Ray(new Vector3(-1f, 0.2f, 0f), Vector3.right), Vector3.zero, Vector3.right, out _), Is.False);
        }

        [Test]
        public void MoveDirection_Free_FollowsTheViewPlane()
        {
            ClothEditController c = StartController(CreateTarget());
            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            c.PointerDrag(RayAt(Pulled(from, 0.1f)));
            Vector3 delta = c.GrabTarget - c.HoverPoint;
            Assert.That(Vector3.Distance(delta, new Vector3(0f, 0.1f, -0.1f)), Is.LessThan(2e-3f));
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void MoveDirection_AxisZ_MovesOnlyAlongZ()
        {
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, MoveDirection = MoveDirection.AxisZ };
            ClothEditController c = StartController(CreateTarget(), settings);
            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            Vector3 start = c.HoverPoint;
            c.PointerDrag(RayAt(Pulled(from, 0.1f)));
            Vector3 delta = c.GrabTarget - start;
            Assert.That(delta.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(delta.y, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(delta.z, Is.LessThan(-0.05f), "カーソルを手前へ引けば Z も手前へ動く");
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void MoveDirection_SurfaceNormal_MovesAlongTheNormal()
        {
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, MoveDirection = MoveDirection.SurfaceNormal };
            ClothEditController c = StartController(CreateTarget(), settings);
            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            Vector3 start = c.HoverPoint;
            Vector3 normal = c.HoverNormal;
            c.PointerDrag(RayAt(Pulled(from, 0.1f)));
            Vector3 delta = c.GrabTarget - start;
            Assert.That(Vector3.Cross(delta, normal).magnitude, Is.LessThan(1e-4f), "法線と平行");
            Assert.That(delta.magnitude, Is.GreaterThan(0.05f));
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void MoveDirection_AvatarLocalAxis_UsesTheAvatarRoot()
        {
            var root = Track(new GameObject("Avatar")).transform;
            root.gameObject.AddComponent<Animator>();
            root.rotation = Quaternion.Euler(0f, 90f, 0f);   // ローカル X = ワールド -Z
            Renderer renderer = CreateTarget("Body", root);   // ワールドの姿勢は保つ（Quad は XY 平面のまま）
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, MoveDirection = MoveDirection.AxisX, DirectionSpace = AxisSpace.AvatarLocal };
            ClothEditController c = StartController(renderer, settings);

            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            Vector3 start = c.HoverPoint;
            c.PointerDrag(RayAt(Pulled(from, 0.1f)));
            Vector3 delta = c.GrabTarget - start;
            Assert.That(Mathf.Abs(delta.x), Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(delta.y), Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(delta.z), Is.GreaterThan(0.05f));
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void PreciseDrag_MovesAFifth_AndSwitchingMidDragDoesNotJump()
        {
            ClothEditController c = StartController(CreateTarget());
            Vector3 from = new Vector3(0.1f, 0f, 0f);
            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            Vector3 start = c.HoverPoint;
            var unit = new Vector3(0f, 1f, -1f);   // Pulled(from, s) の s 1 あたりの動き

            c.PointerDrag(RayAt(Pulled(from, 0.25f)), 100f, true, false);
            Assert.That(Vector3.Distance(c.GrabTarget - start, unit * 0.25f * ClothEditController.PrecisionFactor), Is.LessThan(2e-3f), "Shift で 1/5");

            // 途中で Shift を離す: 今の位置から続きが始まる（0.25 の位置へ飛ばない）
            c.PointerDrag(RayAt(Pulled(from, 0.25f)), 100f, false, false);
            Assert.That(Vector3.Distance(c.GrabTarget - start, unit * 0.25f * ClothEditController.PrecisionFactor), Is.LessThan(2e-3f), "切り替えただけでは飛ばない");
            c.PointerDrag(RayAt(Pulled(from, 0.35f)), 100f, false, false);
            Assert.That(Vector3.Distance(c.GrabTarget - start, unit * (0.05f + 0.10f)), Is.LessThan(3e-3f), "そこからは等倍で動く");
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void PreciseGrab_TargetsTheSamePlaceAsAFifthOfTheDrag()
        {
            Vector3 from = new Vector3(0.1f, 0f, 0f);
            ClothEditController precise = StartController(CreateTarget("Precise"));
            Assert.That(precise.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            for (int k = 1; k <= 5; k++) precise.PointerDrag(RayAt(Pulled(from, 0.25f * k / 5)), 100f, true, false);
            ClothEditController normal = StartController(CreateTarget("Normal"));
            Assert.That(normal.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            for (int k = 1; k <= 5; k++) normal.PointerDrag(RayAt(Pulled(from, 0.05f * k / 5)), 100f, false, false);

            Assert.That(Vector3.Distance(precise.GrabTarget, normal.GrabTarget), Is.LessThan(0.002f));
            // 2つのコントローラーは同じ Undo の履歴を共有するので、取り消し（記録をさかのぼって消す）は片方だけ試す。もう片方は TearDown で終わる
            Assert.That(normal.Cancel(), Is.True);
        }

        // ---- ホイール・キー ----

        [Test]
        public void AdjustRadius_ScalesByTenPercentPerStep_AndClamps()
        {
            Assert.That(ClothEditController.AdjustRadius(3f, 1f, 1f, 20f), Is.EqualTo(3.3f).Within(1e-4f));
            Assert.That(ClothEditController.AdjustRadius(3f, -1f, 1f, 20f), Is.EqualTo(3f / 1.1f).Within(1e-4f));
            Assert.That(ClothEditController.AdjustRadius(ClothEditController.AdjustRadius(3f, 4f, 1f, 20f), -4f, 1f, 20f), Is.EqualTo(3f).Within(1e-4f), "戻せる");
            Assert.That(ClothEditController.AdjustRadius(19f, 5f, 1f, 20f), Is.EqualTo(20f));
            Assert.That(ClothEditController.AdjustRadius(1.1f, -5f, 1f, 20f), Is.EqualTo(1f));
        }

        [Test]
        public void WheelAndRadiusKeys_MapToTheRightTargets()
        {
            Assert.That(ClothEditShortcuts.GetWheelTarget(false, false), Is.EqualTo(WheelTarget.None), "Ctrl なしは視点のズーム");
            Assert.That(ClothEditShortcuts.GetWheelTarget(false, true), Is.EqualTo(WheelTarget.None));
            Assert.That(ClothEditShortcuts.GetWheelTarget(true, false), Is.EqualTo(WheelTarget.BrushRadius));
            Assert.That(ClothEditShortcuts.GetWheelTarget(true, true), Is.EqualTo(WheelTarget.InfluenceRadius));
            Assert.That(ClothEditShortcuts.WheelSteps(-3f), Is.EqualTo(1f), "上へ回すと大きく");
            Assert.That(ClothEditShortcuts.WheelSteps(3f), Is.EqualTo(-1f));

            Assert.That(ClothEditShortcuts.TryGetRadiusKey(KeyCode.LeftBracket, '[', false, out WheelTarget target, out int step), Is.True);
            Assert.That(target, Is.EqualTo(WheelTarget.BrushRadius));
            Assert.That(step, Is.EqualTo(-1));
            Assert.That(ClothEditShortcuts.TryGetRadiusKey(KeyCode.RightBracket, ']', true, out target, out step), Is.True);
            Assert.That(target, Is.EqualTo(WheelTarget.InfluenceRadius));
            Assert.That(step, Is.EqualTo(1));
            Assert.That(ClothEditShortcuts.TryGetRadiusKey(KeyCode.None, ']', false, out _, out step), Is.True, "キーコードが違う配列でも文字で分かる");
            Assert.That(step, Is.EqualTo(1));
            Assert.That(ClothEditShortcuts.TryGetRadiusKey(KeyCode.A, 'a', false, out target, out _), Is.False);
            Assert.That(target, Is.EqualTo(WheelTarget.None));
        }

        [Test]
        public void ToolShortcuts_MapKeysToTools()
        {
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.W, false, out ClothEditMode tool), Is.True);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.E, false, out tool), Is.True);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Rotate));
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.R, false, out tool), Is.True);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Scale));
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.P, false, out tool), Is.True);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Pin));
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.P, true, out tool), Is.True);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Unpin));
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.T, false, out tool), Is.True);
            Assert.That(tool, Is.EqualTo(ClothEditMode.Relax));
            Assert.That(ClothEditShortcuts.TryGetTool(KeyCode.Q, false, out _), Is.False);
            Assert.That(ClothEditShortcuts.IsHoldOriginalKey(KeyCode.C), Is.True);
            Assert.That(ClothEditShortcuts.IsHelpKey(KeyCode.F1), Is.True);
            Assert.That(ClothEditShortcuts.IsHelpKey(KeyCode.F2), Is.False);
        }

        [Test]
        public void Mode_IsKeptInTheSettings_SoItIsSaved()
        {
            var settings = new ClothEditSettings { SolveMilliseconds = 4f };
            ClothEditController c = StartController(CreateTarget(), settings);
            c.Mode = ClothEditMode.Rotate;
            Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Rotate));
            settings.Tool = ClothEditMode.Relax;
            Assert.That(c.Mode, Is.EqualTo(ClothEditMode.Relax));
        }

        // ---- 範囲の表示（ブラシを当てる前） ----

        [Test]
        public void HoverPreview_ListsTheParticlesInRangeWithWeights()
        {
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, Falloff = FalloffPreset.Linear };
            ClothEditController c = StartController(CreateTarget(), settings);
            c.BrushRadius = 0.05f;
            Assert.That(c.Hover(RayAt(new Vector3(0.1f, 0.1f, 0f))), Is.True);

            int count = c.PreviewCount;
            Assert.That(count, Is.InRange(40, ClothEditController.MaxPreviewParticles));
            float maxWeight = 0f;
            var seen = new HashSet<int>();
            for (int k = 0; k < count; k++)
            {
                float w = c.PreviewWeight(k);
                Assert.That(w, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
                maxWeight = Mathf.Max(maxWeight, w);
                Assert.That(seen.Add(c.PreviewParticle(k)), Is.True);
                Assert.That(Vector3.Distance(ToUnity(c.Session.Solver.Positions[c.PreviewParticle(k)]), c.HoverPoint), Is.LessThan(0.065f));
            }
            Assert.That(maxWeight, Is.EqualTo(1f));

            // 半径を大きくすると範囲が広がる。上限を超えるときは間引かれる
            c.BrushRadius = 0.5f;
            c.Hover(RayAt(new Vector3(0.1f, 0.1f, 0f)));
            Assert.That(c.PreviewCount, Is.InRange(count, ClothEditController.MaxPreviewParticles));
            Assert.That(c.PreviewCount, Is.GreaterThan(700));

            // 表示しない設定、重みのない道具（ピン留め）では出さない
            settings.ShowBrushPreview = false;
            c.Hover(RayAt(new Vector3(0.1f, 0.1f, 0f)));
            Assert.That(c.PreviewCount, Is.EqualTo(0));
            settings.ShowBrushPreview = true;
            c.Mode = ClothEditMode.Pin;
            c.Hover(RayAt(new Vector3(0.1f, 0.1f, 0f)));
            Assert.That(c.PreviewCount, Is.EqualTo(0));
        }

        [Test]
        public void HoverPreview_FollowsTheFalloffCurveAndDistanceMode_AndShowsBothSidesWithSymmetry()
        {
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, Falloff = FalloffPreset.Constant };
            ClothEditController c = StartController(CreateTarget(), settings);
            c.BrushRadius = 0.04f;
            c.Hover(RayAt(new Vector3(0.2f, 0.1f, 0f)));
            int single = c.PreviewCount;
            for (int k = 0; k < single; k++) Assert.That(c.PreviewWeight(k), Is.EqualTo(1f), "一定のカーブでは全部 1");

            settings.Falloff = FalloffPreset.Smooth;
            c.ApplySettings();
            c.Hover(RayAt(new Vector3(0.2f, 0.1f, 0f)));
            float min = 1f;
            for (int k = 0; k < c.PreviewCount; k++) min = Mathf.Min(min, c.PreviewWeight(k));
            Assert.That(min, Is.LessThan(0.5f), "なめらかなカーブでは端が弱い");

            settings.SymmetryEnabled = true;
            c.ApplySettings();
            c.Hover(RayAt(new Vector3(0.2f, 0.1f, 0f)));
            Assert.That(c.PreviewCount, Is.GreaterThan(single * 1.6f).And.LessThan(single * 2.4f));
            bool left = false, right = false;
            for (int k = 0; k < c.PreviewCount; k++)
            {
                float x = c.Session.Solver.Positions[c.PreviewParticle(k)].X;
                if (x < -0.1f) left = true;
                if (x > 0.1f) right = true;
            }
            Assert.That(left && right, Is.True, "左右両方の範囲が入っている");
        }

        // ---- 伸びのヒートマップ ----

        [Test]
        public void HeatColor_GoesGreenYellowRed()
        {
            Assert.That(ClothEditHelp.HeatColor(0f), Is.EqualTo(ClothEditHelp.HeatColor(-1f)));
            Color green = ClothEditHelp.HeatColor(0f);
            Color yellow = ClothEditHelp.HeatColor(0.05f);
            Color red = ClothEditHelp.HeatColor(0.15f);
            Assert.That(green.g, Is.GreaterThan(green.r));
            Assert.That(yellow.r, Is.GreaterThan(0.9f));
            Assert.That(yellow.g, Is.GreaterThan(0.8f));
            Assert.That(red.r, Is.GreaterThan(0.9f));
            Assert.That(red.g, Is.LessThan(0.3f));
            Assert.That(ClothEditHelp.HeatColor(1f), Is.EqualTo(red), "赤で頭打ち");
            Color mid = ClothEditHelp.HeatColor(0.10f);
            Assert.That(mid.g, Is.LessThan(yellow.g).And.GreaterThan(red.g));
        }

        [Test]
        public void StretchDots_ShowTheStretchedParticles_WithinTheLimit()
        {
            ClothEditController c = StartController(CreateTarget());
            var positions = new List<Vector3>();
            var stretches = new List<float>();
            c.Session.GetStretchDots(positions, stretches, 500);
            Assert.That(positions.Count, Is.InRange(100, 500));
            Assert.That(positions.Count, Is.EqualTo(stretches.Count));
            foreach (float s in stretches) Assert.That(s, Is.LessThan(0.01f), "元の形では伸びていない");

            // ドラッグ中（仕上げの前）は一部が伸びている。伸びている粒子を優先して出す
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward, 100f), Is.True);
            c.PointerDrag(RayAt(Pulled(new Vector3(0.1f, 0f, 0f), 0.15f)));
            Tick(c);
            c.Session.GetStretchDots(positions, stretches, 500);
            Assert.That(positions.Count, Is.LessThanOrEqualTo(500));
            float max = 0f;
            foreach (float s in stretches) max = Mathf.Max(max, s);
            Assert.That(max, Is.GreaterThan(0.02f), $"伸びている粒子が含まれる（最大 {max:P1}）");
            Assert.That(c.Cancel(), Is.True);

            c.Session.GetStretchDots(positions, stretches, 0);
            Assert.That(positions.Count, Is.EqualTo(0));
        }

        // ---- 元の形との比較 ----

        [Test]
        public void Compare_ShowOriginal_HoldC_AndBlend_ChangeThePreviewShape()
        {
            ClothEditController c = StartController(CreateTarget());
            Vector3[] original = (Vector3[])c.Session.CurrentWorldVertices.Clone();
            GrabStroke(c, new Vector3(0.1f, 0f, 0f), 0.1f);
            SettleUntilDone(c);
            Vector3[] edited = (Vector3[])c.Session.CurrentWorldVertices.Clone();
            int moved = 0;
            for (int v = 1; v < original.Length; v++) if ((original[v] - edited[v]).magnitude > 0.01f) { moved = v; break; }
            Assert.That(moved, Is.GreaterThan(0), "動いた頂点がある");

            Assert.That(c.IsComparing, Is.False);
            Assert.That(c.Session.GetPreviewVertices()[moved], Is.EqualTo(edited[moved]));

            c.ShowOriginal = true;
            Assert.That(c.IsComparing, Is.True);
            Assert.That(Vector3.Distance(c.Session.GetPreviewVertices()[moved], original[moved]), Is.LessThan(1e-6f));
            c.ShowOriginal = false;
            Assert.That(Vector3.Distance(c.Session.GetPreviewVertices()[moved], edited[moved]), Is.LessThan(1e-6f));

            c.HoldOriginal = true;
            Assert.That(Vector3.Distance(c.Session.GetPreviewVertices()[moved], original[moved]), Is.LessThan(1e-6f), "C を押している間は元の形");
            c.HoldOriginal = false;
            Assert.That(Vector3.Distance(c.Session.GetPreviewVertices()[moved], edited[moved]), Is.LessThan(1e-6f));

            c.CompareBlend = 0.5f;
            Assert.That(Vector3.Distance(c.Session.GetPreviewVertices()[moved], (original[moved] + edited[moved]) * 0.5f), Is.LessThan(1e-5f));
            c.CompareBlend = 0f;
            Assert.That(Vector3.Distance(c.Session.GetPreviewVertices()[moved], original[moved]), Is.LessThan(1e-6f));

            // 見せ方を変えても、編集の中身（粒子）は変わらない。編集を始め直すと 100% に戻る
            Assert.That(Vector3.Distance(c.Session.CurrentWorldVertices[moved], edited[moved]), Is.LessThan(1e-6f));
            Assert.That(c.TryStart(c.Target, out _), Is.True);
            Assert.That(c.IsComparing, Is.False);
            Assert.That(c.CompareBlend, Is.EqualTo(1f));
        }

        // ---- 警告 ----

        [Test]
        public void Warnings_ExplainTheProblemsOfTheTarget()
        {
            var warnings = new List<TargetWarning>();
            ClothEditWarnings.Collect(null, warnings);
            Assert.That(warnings, Is.Empty, "対象が無いときはウィンドウが案内する");

            // 問題なし
            Renderer ok = CreateTarget("Fine");
            ClothEditWarnings.Collect(ok, warnings);
            Assert.That(warnings, Is.Empty);

            // メッシュが無い
            var noMesh = Track(new GameObject("NoMesh")).AddComponent<SkinnedMeshRenderer>();
            ClothEditWarnings.Collect(noMesh, warnings);
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(warnings[0].Kind, Is.EqualTo(TargetWarningKind.CannotStart));
            Assert.That(warnings[0].Message, Does.Contain("メッシュ"));

            // MeshFilter のメッシュ
            var go = Track(new GameObject("Static"));
            go.AddComponent<MeshFilter>().sharedMesh = Track(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Quad.fbx")));
            var meshRenderer = go.AddComponent<MeshRenderer>();
            ClothEditWarnings.Collect(meshRenderer, warnings);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.MeshRenderer && w.Severity == MessageType.Info), Is.True);

            // Read/Write が無効（自動で直すボタン）
            Mesh unreadable = Track(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Quad.fbx")));
            unreadable.UploadMeshData(true);
            var third = Track(new GameObject("Unreadable")).AddComponent<SkinnedMeshRenderer>();
            third.sharedMesh = unreadable;
            ClothEditWarnings.Collect(third, warnings);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.Unreadable && w.CanAutoFix && w.Severity == MessageType.Warning), Is.True);

            // 頂点が多い（しきい値を下げて確かめる）
            ClothEditWarnings.Collect(ok, warnings, manyVertices: 2, veryManyVertices: 1000);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.ManyVertices), Is.True);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.VeryManyVertices), Is.False);
            ClothEditWarnings.Collect(ok, warnings, manyVertices: 1, veryManyVertices: 2);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.VeryManyVertices && w.Severity == MessageType.Warning), Is.True);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.ManyVertices), Is.False, "どちらか一方だけ");
        }

        [Test]
        public void EnableReadWrite_CannotFixAMeshThatIsNotFromAModelFile_AndSaysWhy()
        {
            Mesh unreadable = Track(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Quad.fbx")));
            unreadable.UploadMeshData(true);
            var renderer = Track(new GameObject("Unreadable")).AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = unreadable;
            Assert.That(ClothEditWarnings.TryEnableReadWrite(renderer, out string message), Is.False);
            Assert.That(message, Does.Contain("Read/Write"));

            Assert.That(ClothEditWarnings.TryEnableReadWrite(CreateTarget("Fine"), out message), Is.True, "すでに有効");
            var empty = Track(new GameObject("Empty")).AddComponent<SkinnedMeshRenderer>();
            Assert.That(ClothEditWarnings.TryEnableReadWrite(empty, out _), Is.False);
        }

        // ---- 設定 ----

        [Test]
        public void Settings_NewValues_HaveDefaults_AreSanitized_SavedAndReset()
        {
            var settings = new ClothEditSettings();
            Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(settings.Falloff, Is.EqualTo(FalloffPreset.Constant));
            Assert.That(settings.DistanceMode, Is.EqualTo(BrushDistanceMode.Surface));
            Assert.That(settings.SymmetryEnabled, Is.False);
            Assert.That(settings.SymmetryAxis, Is.EqualTo(SymmetryAxis.X));
            Assert.That(settings.MoveDirection, Is.EqualTo(MoveDirection.Free));
            Assert.That(settings.DirectionSpace, Is.EqualTo(AxisSpace.World));
            Assert.That(settings.RotateAxis, Is.EqualTo(RotateAxisMode.View));
            Assert.That(settings.ShowBrushPreview, Is.True);
            Assert.That(settings.ShowStretchHeatmap, Is.False);
            Assert.That(settings.CompareBlend, Is.EqualTo(1f));

            settings.RelaxStrength = float.NaN;
            settings.SymmetryOffset = float.NaN;
            settings.Tool = (ClothEditMode)99;
            settings.Falloff = (FalloffPreset)99;
            settings.CompareBlend = 4f;
            settings.CustomFalloff = null;
            settings.Sanitize();
            Assert.That(settings.RelaxStrength, Is.EqualTo(ClothEditSettings.DefaultRelaxStrength));
            Assert.That(settings.SymmetryOffset, Is.EqualTo(0f));
            Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(settings.Falloff, Is.EqualTo(FalloffPreset.Constant));
            Assert.That(settings.CompareBlend, Is.EqualTo(1f));
            Assert.That(settings.CustomFalloff, Is.Not.Null);
            settings.SymmetryOffset = 50f;
            settings.Sanitize();
            Assert.That(settings.SymmetryOffset, Is.EqualTo(ClothEditSettings.MaxSymmetryOffset));

            var custom = new AnimationCurve(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(0.5f, 0.25f, -1.5f, -1.5f), new Keyframe(1f, 0f, 0f, 0f));
            var saved = new ClothEditSettings
            {
                Tool = ClothEditMode.Relax,
                Falloff = FalloffPreset.Custom,
                CustomFalloff = custom,
                DistanceMode = BrushDistanceMode.Linear,
                SymmetryEnabled = true,
                SymmetryAxis = SymmetryAxis.Z,
                SymmetryOffset = 0.035f,
                MoveDirection = MoveDirection.AxisY,
                DirectionSpace = AxisSpace.AvatarLocal,
                RotateAxis = RotateAxisMode.SurfaceNormal,
                RelaxStrength = 0.3f,
                ShowBrushPreview = false,
                ShowStretchHeatmap = true,
            };
            saved.Save(TestKeyPrefix);
            ClothEditSettings loaded = ClothEditSettings.Load(TestKeyPrefix);
            Assert.That(loaded.Tool, Is.EqualTo(ClothEditMode.Relax));
            Assert.That(loaded.Falloff, Is.EqualTo(FalloffPreset.Custom));
            Assert.That(loaded.DistanceMode, Is.EqualTo(BrushDistanceMode.Linear));
            Assert.That(loaded.SymmetryEnabled, Is.True);
            Assert.That(loaded.SymmetryAxis, Is.EqualTo(SymmetryAxis.Z));
            Assert.That(loaded.SymmetryOffset, Is.EqualTo(0.035f).Within(1e-6f));
            Assert.That(loaded.MoveDirection, Is.EqualTo(MoveDirection.AxisY));
            Assert.That(loaded.DirectionSpace, Is.EqualTo(AxisSpace.AvatarLocal));
            Assert.That(loaded.RotateAxis, Is.EqualTo(RotateAxisMode.SurfaceNormal));
            Assert.That(loaded.RelaxStrength, Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(loaded.ShowBrushPreview, Is.False);
            Assert.That(loaded.ShowStretchHeatmap, Is.True);
            Assert.That(loaded.CustomFalloff.length, Is.EqualTo(3));
            for (float t = 0f; t <= 1f; t += 0.125f) Assert.That(loaded.CustomFalloff.Evaluate(t), Is.EqualTo(custom.Evaluate(t)).Within(1e-5f), $"t={t}");

            loaded.ResetToDefaults();
            Assert.That(loaded.Tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(loaded.Falloff, Is.EqualTo(FalloffPreset.Constant));
            Assert.That(loaded.SymmetryEnabled, Is.False);
            Assert.That(loaded.MoveDirection, Is.EqualTo(MoveDirection.Free));
            Assert.That(loaded.ShowBrushPreview, Is.True);
            Assert.That(loaded.ShowStretchHeatmap, Is.False);
            Assert.That(loaded.CustomFalloff.length, Is.EqualTo(2));

            ClothEditSettings.DeleteSaved(TestKeyPrefix);
            ClothEditSettings afterDelete = ClothEditSettings.Load(TestKeyPrefix);
            Assert.That(afterDelete.Tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(afterDelete.SymmetryAxis, Is.EqualTo(SymmetryAxis.X));
        }

        [Test]
        public void FalloffCurve_FromSettings_PresetsAndCustomCurve()
        {
            var settings = new ClothEditSettings();
            foreach (var pair in new[]
            {
                (FalloffPreset.Constant, FalloffCurve.Constant), (FalloffPreset.Smooth, FalloffCurve.Smooth), (FalloffPreset.Linear, FalloffCurve.Linear),
                (FalloffPreset.Sharp, FalloffCurve.Sharp), (FalloffPreset.Sphere, FalloffCurve.Sphere),
            })
            {
                settings.Falloff = pair.Item1;
                Assert.That(settings.GetFalloffCurve(), Is.SameAs(pair.Item2), pair.Item1.ToString());
            }

            // 自由に描いたカーブを 64 点でサンプルする。変わらない間は同じものを返す
            settings.Falloff = FalloffPreset.Custom;
            settings.CustomFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            FalloffCurve curve = settings.GetFalloffCurve();
            Assert.That(curve.SampleCount, Is.EqualTo(64));
            Assert.That(curve.Evaluate(0f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(curve.Evaluate(0.25f), Is.EqualTo(0.75f).Within(0.01f));
            Assert.That(curve.Evaluate(1f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(settings.GetFalloffCurve(), Is.SameAs(curve));

            settings.CustomFalloff = AnimationCurve.Constant(0f, 1f, 0.5f);
            FalloffCurve flat = settings.GetFalloffCurve();
            Assert.That(flat, Is.Not.SameAs(curve));
            Assert.That(flat.Evaluate(0.7f), Is.EqualTo(0.5f).Within(1e-5f));

            // 範囲外の値は 0〜1 に収まる
            settings.CustomFalloff = new AnimationCurve(new Keyframe(0f, 3f), new Keyframe(1f, -2f));
            FalloffCurve clamped = settings.GetFalloffCurve();
            for (int i = 0; i < clamped.SampleCount; i++) Assert.That(clamped.GetSample(i), Is.InRange(0f, 1f));
        }

        [Test]
        public void CurveFromPreset_CopiesThePresetShape()
        {
            foreach (FalloffPreset preset in new[] { FalloffPreset.Constant, FalloffPreset.Smooth, FalloffPreset.Linear, FalloffPreset.Sharp, FalloffPreset.Sphere })
            {
                AnimationCurve curve = ClothEditSettings.CurveFromPreset(preset);
                var settings = new ClothEditSettings { Falloff = preset };
                FalloffCurve exact = settings.GetFalloffCurve();
                for (float t = 0f; t <= 1f; t += 0.05f)
                {
                    Assert.That(curve.Evaluate(t), Is.EqualTo(exact.Evaluate(t)).Within(0.04f), $"{preset} t={t}");
                }
            }
        }

        [Test]
        public void BuildBrushOptions_FollowsTheSettings()
        {
            var settings = new ClothEditSettings { SolveMilliseconds = 4f, Falloff = FalloffPreset.Sharp, DistanceMode = BrushDistanceMode.Linear };
            ClothEditController c = StartController(CreateTarget(), settings);
            c.BrushRadius = 0.07f;
            BrushOptions options = c.BuildBrushOptions();
            Assert.That(options.Radius, Is.EqualTo(0.07f));
            Assert.That(options.Falloff, Is.SameAs(FalloffCurve.Sharp));
            Assert.That(options.DistanceMode, Is.EqualTo(BrushDistanceMode.Linear));
            Assert.That(options.Mirror.HasValue, Is.False);

            settings.SymmetryEnabled = true;
            settings.SymmetryOffset = 0.1f;
            options = c.BuildBrushOptions();
            Assert.That(options.Mirror.HasValue, Is.True);
            Assert.That(options.Mirror.Value.Point.X, Is.EqualTo(0.1f).Within(1e-5f));
        }

        [Test]
        public void LinearDistance_GrabsTheOverlappingLayerToo()
        {
            // 同じ位置に重ねた2枚の布: メッシュに沿う距離だと当てた層だけ、直線だと両方が範囲に入る
            Mesh mesh = Track(new Mesh { name = "TwoLayers" });
            ProceduralMeshes.Grid(20, 20, 0.2f, 0.2f, out var front, out var frontTriangles);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int layer = 0; layer < 2; layer++)
            {
                int offset = vertices.Count;
                foreach (NVector3 v in front) vertices.Add(new Vector3(v.X - 0.1f, v.Y - 0.1f, layer * 0.004f));
                foreach (int index in frontTriangles) triangles.Add(index + offset);
            }
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            var renderer = Track(new GameObject("Layers")).AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            _clearUndoOnly.Add(renderer);

            var settings = new ClothEditSettings { SolveMilliseconds = 4f, DistanceMode = BrushDistanceMode.Surface };
            ClothEditController c = StartController(renderer, settings);
            c.BrushRadius = 0.03f;
            Assert.That(c.Hover(RayAt(new Vector3(0f, 0f, -0.004f))), Is.True);
            int surfaceCount = c.PreviewCount;
            Assert.That(surfaceCount, Is.GreaterThan(10));

            settings.DistanceMode = BrushDistanceMode.Linear;
            c.ApplySettings();
            c.Hover(RayAt(new Vector3(0f, 0f, -0.004f)));
            Assert.That(c.PreviewCount, Is.GreaterThan((int)(surfaceCount * 1.5f)), "直線距離だと重なった層も範囲に入る");
        }

        // ---- 案内・言語 ----

        [Test]
        public void Hints_MatchTheToolAndTheState_InBothLanguages()
        {
            foreach (ClothEditMode mode in new[] { ClothEditMode.Grab, ClothEditMode.Rotate, ClothEditMode.Scale, ClothEditMode.Pin, ClothEditMode.Unpin, ClothEditMode.Relax })
            {
                L10n.Language = UiLanguage.Japanese;
                string ja = ClothEditHelp.GetHint(mode, false, false);
                Assert.That(ja, Does.Contain(ClothEditHelp.ToolName(mode)), mode.ToString());
                Assert.That(ja, Does.Contain(ClothEditHelp.ToolKey(mode)));
                Assert.That(ja, Does.Contain("F1"));
                L10n.Language = UiLanguage.English;
                string en = ClothEditHelp.GetHint(mode, false, false);
                Assert.That(en, Does.Contain(ClothEditHelp.ToolName(mode)));
                Assert.That(en, Is.Not.EqualTo(ja));
            }
            L10n.Language = UiLanguage.Japanese;
            // 案内は短い1行（細かい操作は、ショートカット一覧と道具のツールチップ）。つまんでいる間は、取り消しのキーを出す
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Rotate, true, false), Does.Contain("回して").And.Contain("Esc"));
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Rotate, false, false), Does.Contain("横にドラッグ"));
            Assert.That(ClothEditHelp.ToolTip(ClothEditMode.Rotate), Does.Contain("15°"), "15°刻みは、ツールチップに残してある");
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Grab, false, true), Does.Contain("仕上げ"));
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Grab, true, false), Does.Contain("つまんで"));

            var lines = new List<KeyValuePair<string, string>>();
            ClothEditHelp.GetShortcutLines(lines);
            var keys = new HashSet<string>();
            foreach (var line in lines) keys.Add(line.Key);
            foreach (string key in new[] { "W", "E", "R", "P", "Shift+P", "T", "Esc", "F1" }) Assert.That(keys, Does.Contain(key));
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(12));
        }

        [Test]
        public void Language_SwitchesTheMessages()
        {
            L10n.Language = UiLanguage.Japanese;
            string ja = ClothEditController.GetStartProblem(null);
            Assert.That(ja, Does.Contain("対象"));
            Assert.That(ClothEditHelp.ToolName(ClothEditMode.Rotate), Is.EqualTo("回す"));

            L10n.Language = UiLanguage.English;
            string en = ClothEditController.GetStartProblem(null);
            Assert.That(en, Does.Contain("target"));
            Assert.That(en, Is.Not.EqualTo(ja));
            Assert.That(ClothEditHelp.ToolName(ClothEditMode.Rotate), Is.EqualTo("Rotate"));
            Assert.That(ClothEditSession.DescribeAcceleration(null), Does.Contain("None"));

            var controller = new ClothEditController(new ClothEditSettings());
            _controllers.Add(controller);
            Assert.That(controller.Save("x", out string message), Is.False);
            Assert.That(message, Does.Contain("not editing"), "英語のメッセージ");
        }
    }
}
