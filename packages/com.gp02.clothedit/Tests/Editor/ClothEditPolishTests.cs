using System.Collections.Generic;
using System.IO;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using NVector3 = System.Numerics.Vector3;
using L10n = GP02.EditorTools.L10n;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 画面の仕上げ（範囲を指定したピン留め、ドラッグ中の範囲の表示、手順の案内、説明書の場所）と、法線の計算し直しを、ウィンドウなしで確かめる。
    /// 形はプログラムで作った布（XY 平面の 1m 四方、-Z 向きが表）。
    /// </summary>
    public class ClothEditPolishTests : ShapeKeyUiTestBase
    {
        const string KeyPrefix = "GP02.Test.PolishSettings.";

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
            ClothEditSettings.DeleteSaved(KeyPrefix);
        }

        T TrackLocal<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // 5cm 間隔の頂点を持つ 1m 四方の布（XY 平面、x=0 で左右対称）。offsetX だけ横にずらせる
        static void AppendGrid(List<Vector3> vertices, List<int> triangles, float offsetX)
        {
            ProceduralMeshes.Grid(20, 20, 1f, 1f, out NVector3[] grid, out int[] gridTriangles);
            int start = vertices.Count;
            foreach (NVector3 g in grid) vertices.Add(new Vector3(g.X - 0.5f + offsetX, g.Y - 0.5f, 0f));
            foreach (int t in gridTriangles) triangles.Add(start + t);
        }

        Renderer CreateCloth(string name, params float[] gridOffsets)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (float offset in gridOffsets) AppendGrid(vertices, triangles, offset);
            Mesh mesh = TrackLocal(new Mesh { name = name + "Mesh" });
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            GameObject go = TrackLocal(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            _clearUndoOnly.Add(renderer);
            return renderer;
        }

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 4f };
            var controller = new ClothEditController(settings) { RecordToData = false, BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            _controllers.Add(controller);
            Assert.That(controller.TryStart(renderer, out string error), Is.True, error);
            return controller;
        }

        // カメラを作る（正面から見る: 布の表の -Z 側。裏から: +Z 側）
        Camera MakeCamera(Vector3 position, Vector3 lookAt)
        {
            GameObject go = TrackLocal(new GameObject("TestCamera"));
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(lookAt - position);
            Camera camera = go.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;
            return camera;
        }

        static Matrix4x4 ViewProjection(Camera camera) => camera.projectionMatrix * camera.worldToCameraMatrix;

        static int CountPinned(ClothEditSession session, System.Func<NVector3, bool> where)
        {
            int count = 0;
            for (int i = 0; i < session.ParticleCount; i++)
            {
                if (session.Solver.IsPinned(i) && where(session.Solver.Positions[i])) count++;
            }
            return count;
        }

        static int CountAll(ClothEditSession session, System.Func<NVector3, bool> where)
        {
            int count = 0;
            for (int i = 0; i < session.ParticleCount; i++)
            {
                if (where(session.Solver.Positions[i])) count++;
            }
            return count;
        }

        // ---- 矩形でのピン留め ----

        [Test]
        public void PinInViewport_PinsOnlyTheParticlesInsideTheRectangle()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            Camera camera = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);

            // 画面の右半分（布は画面の中央に小さく映る）。x > 0 の粒子だけが入る
            var rect = Rect.MinMaxRect(0.5f, 0f, 1f, 1f);
            int changed = c.Session.PinInViewport(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true, true);

            Assert.That(changed, Is.EqualTo(c.Session.PinnedCount));
            Assert.That(changed, Is.GreaterThan(0));
            Assert.That(CountPinned(c.Session, p => p.X < -0.001f), Is.EqualTo(0), "左側は留まらない");
            Assert.That(CountPinned(c.Session, p => p.X > 0.001f), Is.EqualTo(CountAll(c.Session, p => p.X > 0.001f)), "右側はすべて留まる");

            // 解除で、同じ範囲のピンだけ外れる
            int unpinned = c.Session.PinInViewport(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, false, true);
            Assert.That(unpinned, Is.EqualTo(changed));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
        }

        [Test]
        public void PinInViewport_SmallRectangleOnlyTakesWhatIsInsideIt()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            Camera camera = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);

            // 布の中心のまわり（ワールドで約 ±0.1m）だけ。カメラから 3m で視野角 60 度: 画面の半分の高さは 3*tan(30°)=1.732m
            float half = 0.1f / 1.732f * 0.5f;
            var rect = Rect.MinMaxRect(0.5f - half, 0.5f - half, 0.5f + half, 0.5f + half);
            c.Session.PinInViewport(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true, true);

            int pinned = c.Session.PinnedCount;
            Assert.That(pinned, Is.InRange(200, 700), "中心の小さな範囲（約 0.2m 四方 = 粒子 400 個ほど）");
            Assert.That(CountPinned(c.Session, p => Mathf.Abs(p.X) > 0.11f || Mathf.Abs(p.Y) > 0.11f), Is.EqualTo(0), "範囲の外は留まらない");
        }

        [Test]
        public void PinInViewport_BackSideIsExcludedUnlessTheSettingIsOff()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            // 裏（+Z 側）から見る。布の表は -Z を向いているので、裏側からは裏面が見える
            Camera back = MakeCamera(new Vector3(0f, 0f, 3f), Vector3.zero);
            var whole = Rect.MinMaxRect(0f, 0f, 1f, 1f);

            int frontOnly = c.Session.PinInViewport(ViewProjection(back), back.transform.position, back.transform.forward, false, whole, true, true);
            Assert.That(frontOnly, Is.EqualTo(0), "裏側は除く設定なら、何も入らない");

            int all = c.Session.PinInViewport(ViewProjection(back), back.transform.position, back.transform.forward, false, whole, true, false);
            Assert.That(all, Is.EqualTo(c.Session.ParticleCount), "除かない設定なら全部入る");
            c.Session.ClearPins();

            // 表（-Z 側）から見れば、除く設定でも入る
            Camera front = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);
            int fromFront = c.Session.PinInViewport(ViewProjection(front), front.transform.position, front.transform.forward, false, whole, true, true);
            Assert.That(fromFront, Is.EqualTo(c.Session.ParticleCount));
        }

        [Test]
        public void PinInViewport_OrthographicUsesTheViewDirection_AndBehindTheCameraIsSkipped()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            Camera camera = MakeCamera(new Vector3(0f, 0f, 3f), Vector3.zero);   // 裏から
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            var whole = Rect.MinMaxRect(0f, 0f, 1f, 1f);

            Assert.That(c.Session.PinInViewport(ViewProjection(camera), camera.transform.position, camera.transform.forward, true, whole, true, true), Is.EqualTo(0));
            Camera front = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);
            front.orthographic = true;
            front.orthographicSize = 1f;
            Assert.That(c.Session.PinInViewport(ViewProjection(front), front.transform.position, front.transform.forward, true, whole, true, true), Is.EqualTo(c.Session.ParticleCount));

            // カメラの後ろにある布は入らない
            c.Session.ClearPins();
            Camera away = MakeCamera(new Vector3(0f, 0f, -3f), new Vector3(0f, 0f, -10f));
            Assert.That(c.Session.PinInViewport(ViewProjection(away), away.transform.position, away.transform.forward, false, whole, true, false), Is.EqualTo(0));
        }

        [Test]
        public void PinInViewport_WithMirror_AlsoPinsTheOppositeSide()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            Camera camera = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);
            // 画面の、x が 0.25〜0.5 にあたる帯だけ（画面の半分の幅は 1.732m）
            float left = 0.5f + 0.25f / 1.732f * 0.5f;
            float right = 0.5f + 0.5f / 1.732f * 0.5f;
            var rect = Rect.MinMaxRect(left, 0f, right, 1f);
            var plane = new MirrorPlane(NVector3.Zero, NVector3.UnitX);

            c.Session.PinInViewport(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true, true, plane);

            int rightPinned = CountPinned(c.Session, p => p.X > 0.001f);
            int leftPinned = CountPinned(c.Session, p => p.X < -0.001f);
            Assert.That(rightPinned, Is.GreaterThan(50));
            Assert.That(leftPinned, Is.GreaterThan(50), "鏡像の側も留まる");
            Assert.That(Mathf.Abs(leftPinned - rightPinned), Is.LessThanOrEqualTo(Mathf.Max(6, rightPinned / 10)), $"左右の数が近い（左 {leftPinned} / 右 {rightPinned}）");
            Assert.That(CountPinned(c.Session, p => Mathf.Abs(p.X) < 0.2f), Is.EqualTo(0), "帯の外は留まらない");
        }

        [Test]
        public void ClothEditController_PinInRect_IsOneUndoStep_AndFollowsTheSymmetrySetting()
        {
            Transform root = TrackLocal(new GameObject("Avatar")).transform;
            root.gameObject.AddComponent<Animator>();
            Renderer renderer = CreateCloth("Cloth", 0f);
            renderer.transform.SetParent(root, true);
            ClothEditController c = StartController(renderer, new ClothEditSettings { SolveMilliseconds = 4f, SymmetryEnabled = true, SymmetryAxis = SymmetryAxis.X });
            Camera camera = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);
            var rect = Rect.MinMaxRect(0.5f, 0f, 1f, 1f);

            int changed = c.PinInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true);
            Assert.That(changed, Is.EqualTo(c.Session.PinnedCount));
            Assert.That(CountPinned(c.Session, p => p.X < -0.1f), Is.GreaterThan(50), "対称がオンなので反対側も");

            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "1回の Undo で範囲のピン留めが戻る");
            Undo.PerformRedo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(changed));

            // 同じ範囲をもう一度留めても何も変わらず、Undo の記録も残らない
            Assert.That(c.PinInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true), Is.EqualTo(0));
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "直前の Undo は範囲のピン留めそのもの（空の記録は無い）");
        }

        // ---- 部品（島）全体 ----

        [Test]
        public void PinIsland_PinsTheWholePartOnly_AndMirrorsToTheOtherPart()
        {
            // 左右に分かれた2枚の布（x=-1.5〜-0.5 と 0.5〜1.5）。2つの島
            ClothEditController c = StartController(CreateCloth("TwoParts", -1f, 1f));
            ClothEditSession session = c.Session;
            Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(2));

            Assert.That(session.Raycast(new Ray(new Vector3(1f, 0f, -3f), Vector3.forward), out _, out _, out int rightTriangle), Is.True);
            int changed = session.PinIsland(rightTriangle, true);
            Assert.That(changed, Is.EqualTo(session.PinnedCount));
            int rightCount = CountAll(session, p => p.X > 0f);
            Assert.That(CountPinned(session, p => p.X > 0f), Is.EqualTo(rightCount), "右の部品はすべて留まる");
            Assert.That(CountPinned(session, p => p.X < 0f), Is.EqualTo(0), "左の部品は留まらない");

            // 解除
            Assert.That(session.PinIsland(rightTriangle, false), Is.EqualTo(changed));
            Assert.That(session.PinnedCount, Is.EqualTo(0));

            // 鏡像（x=0 の面）の部品も一緒に
            var plane = new MirrorPlane(NVector3.Zero, NVector3.UnitX);
            Assert.That(session.PinIsland(rightTriangle, true, plane), Is.EqualTo(session.ParticleCount), "左右の部品がどちらも留まる");

            // 三角形が無ければ何もしない
            session.ClearPins();
            Assert.That(session.PinIsland(-1, true), Is.EqualTo(0));
        }

        [Test]
        public void ClothEditController_PinIslandAt_IsOneUndoStep()
        {
            ClothEditController c = StartController(CreateCloth("TwoParts", -1f, 1f));
            var ray = new Ray(new Vector3(-1f, 0f, -3f), Vector3.forward);

            int changed = c.PinIslandAt(ray, true);
            Assert.That(changed, Is.GreaterThan(100));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(changed));
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));

            // 布に当たらなければ何もしない
            Assert.That(c.PinIslandAt(new Ray(new Vector3(0f, 5f, -3f), Vector3.forward), true), Is.EqualTo(0));
        }

        // ---- すべて外す・反転 ----

        [Test]
        public void ClearAndInvertPins_ChangeTheRightParticles_AndEachIsOneUndoStep()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            Camera camera = MakeCamera(new Vector3(0f, 0f, -3f), Vector3.zero);
            var rect = Rect.MinMaxRect(0.5f, 0f, 1f, 1f);
            c.PinInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true);
            int pinned = c.Session.PinnedCount;
            int total = c.Session.ParticleCount;
            Assert.That(pinned, Is.InRange(1, total - 1));

            // 反転
            var before = new List<int>();
            c.Session.GetPinnedParticles(before);
            Assert.That(c.InvertPins(), Is.GreaterThan(0));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(total - pinned));
            foreach (int i in before) Assert.That(c.Session.Solver.IsPinned(i), Is.False, "留めていた所は外れる");

            // すべて外す
            Assert.That(c.ClearPins(), Is.EqualTo(total - pinned));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
            // 留まっていなければ何も変わらず、Undo の記録も残らない
            Assert.That(c.ClearPins(), Is.EqualTo(0));

            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(total - pinned), "すべて外すを1回で戻す");
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned), "反転を1回で戻す");
            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "範囲のピン留めを1回で戻す");
        }

        [Test]
        public void PinRect_Helpers_ConvertGuiRectsToViewportRects()
        {
            // 点単位の GUI 矩形（左上が原点）→ ビューポート（左下が原点）。画面 800x600 ピクセル、等倍
            Rect viewport = ClothEditPinRange.GuiRectToViewport(new Rect(200f, 150f, 400f, 300f), 800f, 600f, 1f);
            Assert.That(viewport.xMin, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(viewport.xMax, Is.EqualTo(0.75f).Within(1e-5f));
            Assert.That(viewport.yMin, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(viewport.yMax, Is.EqualTo(0.75f).Within(1e-5f));

            // 上の端を選ぶと、ビューポートでは上（1 に近い）
            Rect top = ClothEditPinRange.GuiRectToViewport(new Rect(0f, 0f, 800f, 60f), 800f, 600f, 1f);
            Assert.That(top.yMax, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(top.yMin, Is.EqualTo(0.9f).Within(1e-5f));

            // 高解像度の画面（1 点が 2 ピクセル）
            Rect hiDpi = ClothEditPinRange.GuiRectToViewport(new Rect(0f, 0f, 200f, 150f), 800f, 600f, 2f);
            Assert.That(hiDpi.xMax, Is.EqualTo(0.5f).Within(1e-5f));

            Rect corners = ClothEditPinRange.FromCorners(new Vector2(30f, 40f), new Vector2(10f, 90f));
            Assert.That(corners, Is.EqualTo(Rect.MinMaxRect(10f, 40f, 30f, 90f)));
            Assert.That(ClothEditPinRange.IsBigEnough(new Rect(0f, 0f, 3f, 50f), 1f), Is.False, "細すぎる");
            Assert.That(ClothEditPinRange.IsBigEnough(new Rect(0f, 0f, 20f, 20f), 1f), Is.True);
        }

        [Test]
        public void Settings_PinRectFrontOnly_DefaultsOn_IsSaved_AndReset()
        {
            var settings = new ClothEditSettings();
            Assert.That(settings.PinRectFrontOnly, Is.True);

            settings.PinRectFrontOnly = false;
            settings.Save(KeyPrefix);
            Assert.That(ClothEditSettings.Load(KeyPrefix).PinRectFrontOnly, Is.False);
            settings.ResetToDefaults();
            Assert.That(settings.PinRectFrontOnly, Is.True);
            ClothEditSettings.DeleteSaved(KeyPrefix);
            Assert.That(ClothEditSettings.Load(KeyPrefix).PinRectFrontOnly, Is.True);
        }

        // ---- ドラッグ中の範囲の表示 ----

        static Ray RayAt(Vector3 point) => new Ray(new Vector3(0.1f, -1f, -1f), (point - new Vector3(0.1f, -1f, -1f)).normalized);

        static readonly Vector3 CameraForward = new Vector3(0f, 1f, 1f).normalized;

        [Test]
        public void GrabRange_IsShownWhileDragging_WithWeights_BoundedAndWithoutAllocatingPerFrame()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            c.BrushRadius = 0.25f;   // 範囲に入る粒子（約 2000 個）が、表示の上限（1500）を超えるくらい大きく
            c.Settings.Falloff = FalloffPreset.Linear;
            var from = new Vector3(0.1f, 0f, 0f);

            Assert.That(c.PointerDown(RayAt(from), CameraForward, 100f), Is.True);
            Assert.That(c.IsDragging, Is.True);
            int shown = c.PreviewCount;
            Assert.That(shown, Is.GreaterThan(500), "ドラッグ中も、つまんだ範囲の粒子を表示する");
            Assert.That(shown, Is.LessThanOrEqualTo(ClothEditController.MaxPreviewParticles), "点の数には上限がある");

            float maxWeight = 0f;
            for (int k = 0; k < shown; k++)
            {
                float w = c.PreviewWeight(k);
                Assert.That(w, Is.InRange(0f, 1f));
                Assert.That(c.Session.Solver.BrushWeight(c.PreviewParticle(k)), Is.EqualTo(w).Within(1e-6f), "重みはソルバーの重みそのもの");
                maxWeight = Mathf.Max(maxWeight, w);
            }
            Assert.That(maxWeight, Is.EqualTo(1f).Within(1e-5f), "中心は重み 1");
            Assert.That(c.HasMirrorHover, Is.False, "つまんでいる間は、止まった鏡像の円は出さない（点で見せる）");

            // 動かしても、表示の数は変わらない（範囲を集め直さない）
            c.PointerDrag(RayAt(from + new Vector3(0f, 0.1f, -0.1f)), 100f, false, false);
            Assert.That(c.PreviewCount, Is.EqualTo(shown));
            Assert.That(Vector3.Distance(c.BrushRingCenter, c.GrabTarget), Is.LessThan(1e-5f), "円はつまんだ点の動かし先に付いていく");
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void GrabRange_WithSymmetry_IncludesTheMirrorSide()
        {
            Transform root = TrackLocal(new GameObject("Avatar")).transform;
            root.gameObject.AddComponent<Animator>();
            Renderer renderer = CreateCloth("Cloth", 0f);
            renderer.transform.SetParent(root, true);
            ClothEditController c = StartController(renderer, new ClothEditSettings { SolveMilliseconds = 4f, SymmetryEnabled = true, SymmetryAxis = SymmetryAxis.X });
            c.BrushRadius = 0.08f;

            Assert.That(c.PointerDown(RayAt(new Vector3(0.3f, 0f, 0f)), CameraForward, 100f), Is.True);
            int right = 0, left = 0;
            for (int k = 0; k < c.PreviewCount; k++)
            {
                if (c.Session.Solver.Positions[c.PreviewParticle(k)].X > 0f) right++;
                else left++;
            }
            Assert.That(right, Is.GreaterThan(10));
            Assert.That(left, Is.GreaterThan(10), "対称のときは鏡像側の点も表示する");
            Assert.That(Mathf.Abs(left - right), Is.LessThanOrEqualTo(Mathf.Max(6, right / 5)), $"左右の数が近い（左 {left} / 右 {right}）");
            Assert.That(c.Cancel(), Is.True);
        }

        [Test]
        public void GrabRange_IsNotShown_WhenTheBrushPreviewIsOff()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f), new ClothEditSettings { SolveMilliseconds = 4f, ShowBrushPreview = false });
            Assert.That(c.PointerDown(RayAt(new Vector3(0.1f, 0f, 0f)), CameraForward, 100f), Is.True);
            Assert.That(c.PreviewCount, Is.EqualTo(0));
            Assert.That(c.Cancel(), Is.True);
        }

        // ---- 手順の案内 ----

        static GuideStepState[] Steps(bool hasTarget, bool editing, bool hasPins, bool edited, bool saved)
        {
            var result = new GuideStepState[ClothEditGuide.StepCount];
            ClothEditGuide.Evaluate(hasTarget, editing, hasPins, edited, saved, result);
            return result;
        }

        [Test]
        public void Guide_FollowsHowFarYouHaveGone()
        {
            const GuideStepState Todo = GuideStepState.Todo, Current = GuideStepState.Current, Done = GuideStepState.Done, Skipped = GuideStepState.Skipped;

            Assert.That(Steps(false, false, false, false, false), Is.EqualTo(new[] { Current, Todo, Todo, Todo, Todo }), "まず衣装を選ぶ");
            Assert.That(Steps(true, false, false, false, false), Is.EqualTo(new[] { Done, Current, Todo, Todo, Todo }), "衣装を選んだら、編集を開始");
            Assert.That(Steps(true, true, false, false, false), Is.EqualTo(new[] { Done, Done, Current, Todo, Todo }), "編集を始めたら、ピン留め");
            Assert.That(Steps(true, true, true, false, false), Is.EqualTo(new[] { Done, Done, Done, Current, Todo }), "ピンを留めたら、引く");
            Assert.That(Steps(true, true, true, true, false), Is.EqualTo(new[] { Done, Done, Done, Done, Current }), "引いたら、保存");
            Assert.That(Steps(true, true, true, true, true), Is.EqualTo(new[] { Done, Done, Done, Done, Done }));

            // ピン留めは任意: 飛ばして引いたら、飛ばしたことにする
            Assert.That(Steps(true, true, false, true, false), Is.EqualTo(new[] { Done, Done, Skipped, Done, Current }));
            // 編集を始めていれば、衣装を選んだことになる（対象の欄が空になっても）
            Assert.That(Steps(false, true, false, false, false)[0], Is.EqualTo(Done));
            // ピンだけでは、編集を始めていなければ済みにならない
            Assert.That(Steps(true, false, true, false, false)[2], Is.Not.EqualTo(Done));
        }

        [Test]
        public void Guide_TextExistsInBothLanguages_ForEveryStep()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                for (int i = 0; i < ClothEditGuide.StepCount; i++)
                {
                    Assert.That(ClothEditGuide.Title(i), Is.Not.Empty);
                    Assert.That(ClothEditGuide.Detail(i), Is.Not.Empty);
                }
            }
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditGuide.Title(0), Does.StartWith("1."));
            Assert.That(ClothEditGuide.Title(4), Does.StartWith("5."));
        }

        [Test]
        public void Session_IsShapeEdited_IsFalseForPinsOnly_AndTrueAfterAnEdit()
        {
            ClothEditController c = StartController(CreateCloth("Cloth", 0f));
            Assert.That(c.Session.IsShapeEdited(), Is.False);
            int version = c.Session.ShapeVersion;

            c.Session.PaintPins(Vector3.zero, 0.2f, true);
            Assert.That(c.Session.IsShapeEdited(), Is.False, "ピン留めだけでは形は変わらない");

            Assert.That(c.PointerDown(RayAt(new Vector3(0.3f, 0f, 0f)), CameraForward, 100f), Is.True);
            c.PointerDrag(RayAt(new Vector3(0.3f, 0.05f, -0.05f)), 100f, false, false);
            for (int i = 0; i < 20; i++)
            {
                _time += 0.016;
                c.Tick(_time);
            }
            Assert.That(c.Session.IsShapeEdited(), Is.True);
            Assert.That(c.Session.ShapeVersion, Is.Not.EqualTo(version), "形が変わると番号が変わる");
            Assert.That(c.Cancel(), Is.True);
        }

        // ---- 説明書 ----

        [Test]
        public void Documentation_BothManualsExistInThePackage_AndNameThePackageFolder()
        {
            string root = ClothEditDocs.FindPackageRoot();
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(Path.Combine(root, "package.json")), Is.True, "パッケージの根は package.json があるフォルダ");

            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                string path = ClothEditDocs.FindDocument(language);
                Assert.That(path, Is.Not.Null, $"{language} の説明書が見つからない");
                Assert.That(Path.GetDirectoryName(path), Does.EndWith(ClothEditDocs.FolderName));
                Assert.That(new FileInfo(path).Length, Is.GreaterThan(3000), "中身がある");
            }
            Assert.That(ClothEditDocs.FileNameFor(UiLanguage.English), Is.EqualTo("20261011_Manual_en.md"));
            Assert.That(ClothEditDocs.FileNameFor(UiLanguage.Japanese), Is.EqualTo("20261011_使い方.md"));
        }

        [Test]
        public void Documentation_ManualsMentionTheFeaturesTheWindowOffers()
        {
            string ja = File.ReadAllText(ClothEditDocs.FindDocument(UiLanguage.Japanese));
            string en = File.ReadAllText(ClothEditDocs.FindDocument(UiLanguage.English));
            foreach (string word in new[] { "ピン留め", "対称", "シェイプキー", "法線", "Shift", "EreMorph", "詳細設定", "BOOTH" })
            {
                Assert.That(ja, Does.Contain(word), $"日本語の説明書に「{word}」が無い");
            }
            foreach (string word in new[] { "Pin", "Symmetry", "shape key", "normals", "Shift", "EreMorph", "Advanced", "BOOTH" })
            {
                Assert.That(en, Does.Contain(word), $"英語の説明書に「{word}」が無い");
            }
        }

        // ---- 道具のツールチップ ----

        [Test]
        public void EveryToolHasANameKeyAndTooltip_InBothLanguages()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                foreach (ClothEditMode mode in System.Enum.GetValues(typeof(ClothEditMode)))
                {
                    Assert.That(ClothEditHelp.ToolName(mode), Is.Not.Empty);
                    Assert.That(ClothEditHelp.ToolKey(mode), Is.Not.Empty);
                    Assert.That(ClothEditHelp.ToolTip(mode), Does.Contain(ClothEditHelp.ToolKey(mode)), "ツールチップにキーが入っている");
                }
            }
            L10n.Language = UiLanguage.Japanese;
            var lines = new List<KeyValuePair<string, string>>();
            ClothEditHelp.GetShortcutLines(lines);
            Assert.That(lines.Exists(l => l.Key.Contains("Shift") && l.Value.Contains("矩形")), Is.True, "矩形でのピン留めがショートカット一覧にある");
        }

        // ---- 法線の計算し直し ----

        // 継ぎ目で頂点が分かれた板の右半分を、Z 方向へ傾ける
        Mesh CreateSeamMesh(out int seamLeft, out int seamRight)
        {
            const int Columns = 10, Rows = 4;
            ProceduralMeshes.GridWithSeam(Columns, Rows, 0.2f, 0.08f, out NVector3[] vertices, out int[] triangles);
            var unityVertices = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) unityVertices[i] = new Vector3(vertices[i].X, vertices[i].Y, vertices[i].Z);
            Mesh mesh = Track(new Mesh { name = "Seam" });
            mesh.vertices = unityVertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            int stride = Columns + 1;
            seamLeft = 2 * stride + Columns / 2;
            seamRight = vertices.Length - (Rows + 1) + 2;
            return mesh;
        }

        static Vector3[] BendRightHalf(Mesh mesh)
        {
            var deltas = new Vector3[mesh.vertexCount];
            Vector3[] vertices = mesh.vertices;
            float center = mesh.bounds.center.x;
            for (int i = 0; i < deltas.Length; i++)
            {
                float x = vertices[i].x - center;
                if (x > 0f) deltas[i] = new Vector3(0f, 0f, -x * 0.5f);
            }
            return deltas;
        }

        [Test]
        public void RecomputeNormals_MergesSeamVertices_AndIsOneUndoStep()
        {
            Mesh mesh = CreateSeamMesh(out int seamLeft, out int seamRight);
            Assert.That(Vector3.Distance(mesh.vertices[seamLeft], mesh.vertices[seamRight]), Is.LessThan(1e-6f), "継ぎ目の2頂点は同じ位置");
            ShapeKeyEntry entry = Entry("Bend", BendRightHalf(mesh));
            GP02ShapeKeyData data = NewData(mesh, entry);

            // 法線を持たない（作ったばかりの）エントリ
            Assert.That(entry.Frames[0].HasNormals, Is.False);

            NextUndoGroup();
            int done = ShapeKeyListOperations.RecomputeNormals(data, new[] { entry }, 180f, true);
            NextUndoGroup();
            Assert.That(done, Is.EqualTo(1));
            Vector3[] normals = entry.Frames[0].DeltaNormals;
            Assert.That(normals.Length, Is.EqualTo(mesh.vertexCount));
            Assert.That(Vector3.Distance(normals[seamLeft], normals[seamRight]), Is.LessThan(1e-4f), "結合したので、継ぎ目の両側の法線の差分がそろう");

            // 結合しないと、片側（動かない側）の法線は変わらない
            ShapeKeyListOperations.RecomputeNormals(data, new[] { entry }, 180f, false);
            Assert.That(entry.Frames[0].DeltaNormals[seamLeft].magnitude, Is.LessThan(1e-4f));
            Assert.That(entry.Frames[0].DeltaNormals[seamRight].magnitude, Is.GreaterThan(0.05f));
            NextUndoGroup();

            Undo.PerformUndo();
            Assert.That(Vector3.Distance(entry.Frames[0].DeltaNormals[seamLeft], entry.Frames[0].DeltaNormals[seamRight]), Is.LessThan(1e-4f), "1回の Undo で、直前の計算し直しだけが戻る");
            Undo.PerformUndo();
            Assert.That(entry.Frames[0].HasNormals, Is.False, "もう1回戻すと、法線のない状態");
        }

        [Test]
        public void RecomputeNormals_WorksOnImportedBlendShapes_ForEveryFrame()
        {
            Mesh mesh = CreateSeamMesh(out int seamLeft, out int seamRight);
            Vector3[] half = BendRightHalf(mesh);
            // 既存の BlendShape（in-between つき）を持つメッシュから取り込む
            var zero = new Vector3[mesh.vertexCount];
            Vector3[] quarter = Scaled(half, 0.5f);
            mesh.AddBlendShapeFrame("Existing", 50f, quarter, zero, zero);
            mesh.AddBlendShapeFrame("Existing", 100f, half, zero, zero);
            GP02ShapeKeyData data = NewData(mesh);

            List<ShapeKeyEntry> imported = ShapeKeyListOperations.ImportBlendShapes(data, mesh, new[] { 0 });
            Assert.That(imported.Count, Is.EqualTo(1));
            ShapeKeyEntry entry = imported[0];
            Assert.That(entry.Frames.Count, Is.EqualTo(2));

            ShapeKeyListOperations.RecomputeNormals(data, new[] { entry }, 180f, true);

            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                Assert.That(frame.HasNormals, Is.True, $"重み {frame.Weight} のフレームにも法線");
                Assert.That(Vector3.Distance(frame.DeltaNormals[seamLeft], frame.DeltaNormals[seamRight]), Is.LessThan(1e-4f));
            }
            // 半分の傾きのフレームは、法線の変化も小さい
            Assert.That(entry.Frames[0].DeltaNormals[seamRight].magnitude, Is.LessThan(entry.Frames[1].DeltaNormals[seamRight].magnitude));
        }

        static Vector3[] Scaled(Vector3[] source, float s)
        {
            var result = new Vector3[source.Length];
            for (int i = 0; i < source.Length; i++) result[i] = source[i] * s;
            return result;
        }

        // 床（Z=0 の面）と壁（X=0 の面）が直角に会う固いエッジ。辺の頂点は床と壁で別々に持つ
        Mesh CreateHardEdgeMesh()
        {
            var vertices = new[]
            {
                new Vector3(-1, 0, 0), new Vector3(0, 0, 0), new Vector3(-1, 1, 0), new Vector3(0, 1, 0),   // 床 0〜3
                new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(0, 1, 1),     // 壁 4〜7
            };
            Mesh mesh = Track(new Mesh { name = "HardEdge" });
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 };
            mesh.RecalculateNormals();
            return mesh;
        }

        [Test]
        public void RecomputeNormals_SmoothAngleKeepsHardEdgesApart()
        {
            // 元の形で 90 度に折れた辺。限度が 30 度なら床と壁をつなげず、100 度ならつなぐ
            Mesh mesh = CreateHardEdgeMesh();
            var deltas = new Vector3[mesh.vertexCount];
            deltas[5] = deltas[7] = new Vector3(0.3f, 0f, 0f);   // 壁だけを倒す
            ShapeKeyEntry sharp = Entry("Sharp", deltas);
            ShapeKeyEntry smooth = Entry("Smooth", (Vector3[])deltas.Clone());
            GP02ShapeKeyData data = NewData(mesh, sharp, smooth);

            ShapeKeyListOperations.RecomputeNormals(data, new[] { sharp }, 30f, true);
            ShapeKeyListOperations.RecomputeNormals(data, new[] { smooth }, 100f, true);

            // 床の辺の頂点（1）と壁の辺の頂点（4）は同じ位置
            Vector3[] hard = sharp.Frames[0].DeltaNormals;
            Vector3[] soft = smooth.Frames[0].DeltaNormals;
            Assert.That(hard[1].magnitude, Is.LessThan(1e-4f), "床は動かないので、つなげなければ法線も変わらない");
            Assert.That(hard[4].magnitude, Is.GreaterThan(0.05f), "壁の辺は倒れた分だけ変わる");
            Assert.That(Vector3.Distance(soft[1], soft[4]), Is.LessThan(1e-4f), "100 度ならつなぐので、両方が同じ向き");
            Assert.That(soft[1].magnitude, Is.GreaterThan(0.01f));
        }

        [Test]
        public void RecomputeNormals_Errors_AreExplained()
        {
            Mesh mesh = CreateSeamMesh(out _, out _);
            ShapeKeyEntry entry = Entry("Bend", BendRightHalf(mesh));
            GP02ShapeKeyData data = NewData(mesh, entry);

            // 空の選択は何もしない
            Assert.That(ShapeKeyListOperations.RecomputeNormals(data, new ShapeKeyEntry[0], 180f, true), Is.EqualTo(0));
            // 頂点数の違うエントリ
            ShapeKeyEntry wrong = Entry("Wrong", new Vector3[4]);
            data.Entries.Add(wrong);
            Assert.Throws<System.ArgumentException>(() => ShapeKeyListOperations.RecomputeNormals(data, new[] { wrong }, 180f, true));
            // 元のメッシュが無い
            data.SourceMesh = null;
            Assert.Throws<System.InvalidOperationException>(() => ShapeKeyListOperations.RecomputeNormals(data, new[] { entry }, 180f, true));
        }

        [Test]
        public void RecomputeNormals_ViewSettings_HaveDefaults()
        {
            var settings = new ShapeKeyViewSettings();
            Assert.That(settings.NormalSmoothAngle, Is.EqualTo(180f));
            Assert.That(settings.NormalMergeCoincident, Is.True);
        }

        // ---- シーンビューの道具バー ----

        [Test]
        public void Overlay_ToolBarHasAllTheButtons_ReflectsTheWindowSettings_AndChangesThem()
        {
            var window = ScriptableObject.CreateInstance<ClothEditWindow>();
            ClothEditSettings settings = window.OverlaySettings;
            ClothEditMode savedTool = settings.Tool;
            bool savedSymmetry = settings.SymmetryEnabled, savedHeatmap = settings.ShowStretchHeatmap;
            try
            {
                Assert.That(ClothEditWindow.OverlayOwner, Is.SameAs(window), "ウィンドウが開いていれば、道具バーはそのウィンドウを操作する");
                Assert.That(ClothEditWindow.IsEditingNow, Is.False, "編集していなければ、道具バーは出さない");
                settings.Tool = ClothEditMode.Rotate;
                settings.SymmetryEnabled = false;
                settings.ShowStretchHeatmap = false;

                var overlay = new ClothEditOverlay();
                UnityEngine.UIElements.VisualElement root = overlay.CreatePanelContent();
                var toggles = root.Query<UnityEditor.UIElements.ToolbarToggle>().ToList();
                Assert.That(toggles.Count, Is.EqualTo(14), "道具 6（つまむ・選んで動かす・ピン留め・元に戻すブラシ・回す・拡大縮小）+ ハンドル 3 + 塗ると外す + 対称・伸び・元の形・ショートカット");
                Assert.That(root.Query<UnityEditor.UIElements.ToolbarButton>().ToList().Count, Is.EqualTo(1), "説明書");

                // 道具: 今の道具だけが選ばれ、名前とツールチップが付く
                var tools = new[] { ClothEditMode.Grab, ClothEditMode.Select, ClothEditMode.Pin, ClothEditMode.Relax, ClothEditMode.Rotate, ClothEditMode.Scale };
                for (int i = 0; i < tools.Length; i++)
                {
                    Assert.That(toggles[i].text, Is.EqualTo(ClothEditHelp.ToolName(tools[i])));
                    Assert.That(toggles[i].tooltip, Is.EqualTo(ClothEditHelp.ToolTip(tools[i])));
                    Assert.That(toggles[i].value, Is.EqualTo(tools[i] == ClothEditMode.Rotate), $"{tools[i]} の選択");
                }
                foreach (var toggle in toggles) Assert.That(toggle.tooltip, Is.Not.Empty, "すべてのボタンにツールチップ");

                // ウィンドウ側の変更が、道具バーに反映される
                settings.Tool = ClothEditMode.Pin;
                settings.SymmetryEnabled = true;
                overlay.Refresh();
                Assert.That(toggles[2].value, Is.True);
                Assert.That(toggles[4].value, Is.False);
                Assert.That(toggles[10].value, Is.True, "対称");

                // 道具バー側の変更が、ウィンドウの設定になる
                window.OverlaySetTool(ClothEditMode.Scale);
                window.OverlaySetStretchHeatmap(true);
                window.OverlaySetSymmetry(false);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Scale));
                Assert.That(settings.ShowStretchHeatmap, Is.True);
                Assert.That(settings.SymmetryEnabled, Is.False);
                window.OverlaySetShortcutsVisible(true);
                Assert.That(window.OverlayShortcutsVisible, Is.True);

                // 言語を変えると、文字が変わる
                L10n.Language = UiLanguage.English;
                overlay.Refresh();
                Assert.That(toggles[0].text, Is.EqualTo("Grab"));
                Assert.That(toggles[10].text, Is.EqualTo("Symmetry"));
            }
            finally
            {
                settings.Tool = savedTool;
                settings.SymmetryEnabled = savedSymmetry;
                settings.ShowStretchHeatmap = savedHeatmap;
                settings.Save();
                Object.DestroyImmediate(window);
            }
            Assert.That(ClothEditWindow.OverlayOwner, Is.Null, "ウィンドウが無くなれば、道具バーは何も操作しない");
        }
    }
}
