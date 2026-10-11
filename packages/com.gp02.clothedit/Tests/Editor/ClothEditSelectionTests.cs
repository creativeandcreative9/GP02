using System.Collections.Generic;
using System.Linq;
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
    /// 「点（粒子）を選んで動かす」道具を、ウィンドウなしで確かめる。
    /// 選ぶ（塗る・矩形・島・すべて・反転・広げる・狭める・鏡像・ピン留め）、見えている点だけを選ぶ判定（重ね着）、
    /// ハンドルの基準（中心・向き）、選んだ点の移動・回転・軸ごとの拡大縮小、精密操作、数値入力、Undo、保存、対称、取り消し。
    /// 平らな布は XY 平面（法線 -Z が表、カメラは -Z 側）。重ね着はテスト用の衣装（スカートとペチコート）。
    /// </summary>
    public class ClothEditSelectionTests
    {
        const string TempRoot = "Assets/GP02_SelectionTestTemp";
        const string Folder = TempRoot + "/Meshes";
        const string KeyPrefix = "GP02.Test.SelectionSettings.";

        static readonly Vector3 CameraPosition = new Vector3(0f, 0f, -1.5f);

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
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // ---- 部品 ----

        // 法線が -Z（表がカメラ側）になる向きの格子を足す
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

        // 原点を中心にした、一辺 0.6m の平らな布。layerZ の数だけ重ねる（同じメッシュの別の島になる）
        Renderer CreateFlat(string name, Quaternion rotation, Transform parent = null, params float[] layerZ)
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
            go.transform.localRotation = rotation;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _clearUndoOnly.Add(go.GetComponent<MeshFilter>());
            return go.AddComponent<MeshRenderer>();
        }

        Renderer CreateFlat(string name = "SelectionFlat", params float[] layerZ) => CreateFlat(name, Quaternion.identity, null, layerZ);

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            return StartController(new[] { renderer }, settings);
        }

        ClothEditController StartController(IReadOnlyList<Renderer> renderers, ClothEditSettings settings = null)
        {
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 6f };
            var c = new ClothEditController(settings) { SaveFolder = Folder, DataFolder = Folder, BrushRadius = 0.03f, InfluenceRadius = 0.3f };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(renderers, out string error), Is.True, error);
            return c;
        }

        Camera CreateCamera(Vector3 position, Vector3 lookAt, bool orthographic = false)
        {
            GameObject go = Track(new GameObject("SelectionCamera"));
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(lookAt - position);
            Camera camera = go.AddComponent<Camera>();
            camera.aspect = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;
            camera.orthographic = orthographic;
            if (orthographic) camera.orthographicSize = 0.6f;
            else camera.fieldOfView = 40f;
            return camera;
        }

        static Matrix4x4 ViewProjection(Camera camera) => camera.projectionMatrix * camera.worldToCameraMatrix;

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

        static Vector3 Pos(ClothEditController c, int i)
        {
            NVector3 p = c.Session.Solver.Positions[i];
            return new Vector3(p.X, p.Y, p.Z);
        }

        static Vector3 Rest(ClothEditController c, int i)
        {
            NVector3 p = c.Session.Cloth.Model.RestPositions[i];
            return new Vector3(p.X, p.Y, p.Z);
        }

        static Vector3 Mean(ClothEditController c, IReadOnlyList<int> particles)
        {
            Vector3 sum = Vector3.zero;
            foreach (int i in particles) sum += Pos(c, i);
            return sum / particles.Count;
        }

        static Vector3[] Snapshot(ClothEditController c)
        {
            int n = c.Session.ParticleCount;
            var result = new Vector3[n];
            for (int i = 0; i < n; i++) result[i] = Pos(c, i);
            return result;
        }

        static float MaxDistance(Vector3[] a, Vector3[] b)
        {
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i]));
            return max;
        }

        static int NearestRest(ClothEditController c, Vector3 point)
        {
            NVector3[] rest = c.Session.Cloth.Model.RestPositions;
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < rest.Length; i++)
            {
                float sq = (new Vector3(rest[i].X, rest[i].Y, rest[i].Z) - point).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        // 上の縁（高さが上から 1.5 個ぶん）をピン留めする
        static void PinTop(ClothEditController c)
        {
            NVector3[] rest = c.Session.Cloth.Model.RestPositions;
            float top = rest.Max(r => r.Y);
            var pins = new List<int>();
            for (int i = 0; i < rest.Length; i++)
            {
                if (rest[i].Y > top - 1.5f * c.Session.Cloth.Spacing) pins.Add(i);
            }
            c.Session.PinParticles(pins);
        }

        static void SelectPatch(ClothEditController c, float x, float y, float radius)
        {
            c.BrushRadius = radius;
            Assert.That(c.SelectPaintBegin(FrontRay(x, y), false), Is.True, "布に当たりません");
            c.SelectPaintEnd();
            Assert.That(c.SelectionCount, Is.GreaterThan(0));
        }

        int[] IslandOf(ClothEditController c, IEnumerable<int> particles) => particles.Select(i => c.Session.Cloth.ParticleIsland[i]).Distinct().ToArray();

        // ---- セッション: 集める ----

        [Test]
        public void Session_CollectInSphere_IsLimitedToTheHitIsland_AndCollectIslandTakesTheWholeIsland()
        {
            // 前の布と、3mm 後ろの布（同じメッシュの別の島）
            Renderer renderer = CreateFlat("TwoLayers", 0f, 0.003f);
            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(2));
                Assert.That(session.Raycast(FrontRay(0.05f, 0.05f), out Vector3 hit, out _, out int triangle), Is.True);
                var near = new List<int>();
                int count = session.CollectInSphere(hit, 0.03f, triangle, near);
                Assert.That(count, Is.EqualTo(near.Count).And.GreaterThan(10));
                int island = session.Cloth.ParticleIsland[near[0]];
                foreach (int i in near)
                {
                    Assert.That(session.Cloth.ParticleIsland[i], Is.EqualTo(island));
                    NVector3 p = session.Solver.Positions[i];
                    Assert.That(Vector3.Distance(new Vector3(p.X, p.Y, p.Z), hit), Is.LessThanOrEqualTo(0.03f + 1e-5f));
                }
                var all = new List<int>();
                Assert.That(session.CollectInSphere(hit, 0.03f, -1, all), Is.GreaterThan(count), "島で絞らなければ後ろの布も入る");
                Assert.That(all.Select(i => session.Cloth.ParticleIsland[i]).Distinct().Count(), Is.EqualTo(2));

                var whole = new List<int>();
                int islandSize = session.Cloth.ParticleIsland.Count(i => i == island);
                Assert.That(islandSize, Is.GreaterThan(session.ParticleCount / 3));
                Assert.That(session.CollectIsland(triangle, whole), Is.EqualTo(islandSize));
                Assert.That(whole.All(i => session.Cloth.ParticleIsland[i] == island), Is.True);
                Assert.That(session.CollectIsland(-1, whole), Is.EqualTo(0));
            }
        }

        [Test]
        public void Session_CollectMirror_FindsTheOppositeSide_AndGrowShrinkStayInsideTheIsland()
        {
            Renderer renderer = CreateFlat("MirrorGrow", 0f, 0.003f);
            using (var session = new ClothEditSession(renderer))
            {
                session.Raycast(FrontRay(0.1f, 0.05f), out Vector3 hit, out _, out int triangle);
                var patch = new List<int>();
                session.CollectInSphere(hit, 0.03f, triangle, patch);
                NVector3[] rest = session.Cloth.Model.RestPositions;
                var plane = new MirrorPlane(NVector3.Zero, NVector3.UnitX);

                var mirrored = new List<int>();
                session.CollectMirror(patch, plane, mirrored);
                Assert.That(mirrored.Count, Is.InRange(patch.Count * 9 / 10, patch.Count * 11 / 10));
                Assert.That(mirrored, Is.Ordered);
                foreach (int j in mirrored)
                {
                    Assert.That(rest[j].X, Is.LessThan(0f));
                    Assert.That(session.Cloth.ParticleIsland[j], Is.EqualTo(session.Cloth.ParticleIsland[patch[0]]), "同じ層の鏡像");
                    Assert.That(patch.Any(i => NVector3.Distance(plane.Reflect(rest[i]), rest[j]) < 0.02f), Is.True);
                }

                var selected = new bool[session.ParticleCount];
                foreach (int i in patch) selected[i] = true;
                session.GrowSelection(selected);
                var grown = Enumerable.Range(0, selected.Length).Where(i => selected[i]).ToList();
                Assert.That(grown.Count, Is.GreaterThan(patch.Count));
                Assert.That(patch.All(i => selected[i]), Is.True, "広げても元の粒子は残る");
                Assert.That(grown.Select(i => session.Cloth.ParticleIsland[i]).Distinct().Count(), Is.EqualTo(1), "別の島には広がらない");

                session.ShrinkSelection(selected);
                var shrunk = Enumerable.Range(0, selected.Length).Where(i => selected[i]).ToList();
                Assert.That(shrunk.Count, Is.LessThan(grown.Count));
                Assert.That(patch.All(i => selected[i]), Is.True, "広げてから狭めると、元の粒子を含む");
            }
        }

        [Test]
        public void Session_GrabSet_MovesTheSetWithAFalloff_AndReleases()
        {
            Renderer renderer = CreateFlat("GrabSet");
            using (var session = new ClothEditSession(renderer))
            {
                session.Solver.BrushCompliance = 0f;
                session.Raycast(FrontRay(0f, 0f), out Vector3 hit, out _, out int triangle);
                var patch = new List<int>();
                session.CollectInSphere(hit, 0.03f, triangle, patch);
                int[] set = patch.ToArray();

                Assert.That(session.BeginGrabSet(new int[0], null), Is.False);
                Assert.That(session.BeginGrabSet(new[] { -1 }, null), Is.False);
                Assert.That(session.IsGrabbing, Is.False);

                var options = new BrushOptions { Radius = 0.03f, Falloff = FalloffCurve.Linear };
                Assert.That(session.BeginGrabSet(set, options), Is.True);
                Assert.That(session.IsGrabbing, Is.True);
                var motion = new BrushMotion { Translation = new NVector3(0f, 0f, -0.02f) };
                session.UpdateGrabSet(motion);
                for (int i = 0; i < 20; i++) session.Solver.Solve(10);
                foreach (int i in set) Assert.That(session.Solver.Positions[i].Z, Is.EqualTo(-0.02f).Within(1e-4f));
                // 集合の外の、重みがつく粒子は途中まで動く
                int ring = Enumerable.Range(0, session.ParticleCount).First(i => !patch.Contains(i) && session.Solver.BrushWeight(i) > 0.3f);
                Assert.That(session.Solver.IsGrabbed(ring), Is.True);

                session.Release();
                Assert.That(session.IsGrabbing, Is.False);
                Assert.That(session.Solver.GrabbedParticle, Is.EqualTo(-1));
                // 1点でつまむ版に切り替えても、集合のつまみは残らない
                Assert.That(session.BeginGrab(hit, 0.02f, triangle), Is.True);
                session.UpdateGrabSet(motion);   // 集合でつまんでいないので何もしない
                Assert.That(session.Solver.BrushParticleCount, Is.LessThan(set.Length * 2));
            }
        }

        // ---- 重ね着で、見えている点だけを選ぶ ----

        [Test]
        public void IsParticleVisible_OuterLayerHidesTheInnerLayer_AndTheFarSideOfTheSameLayer()
        {
            Renderer renderer = CreateFlat("Visible", 0f, 0.003f);
            using (var session = new ClothEditSession(renderer))
            {
                session.Raycast(FrontRay(0.05f, 0.05f), out Vector3 hit, out _, out int triangle);
                int frontParticle = session.FindParticle(hit, triangle);
                int island = session.Cloth.ParticleIsland[frontParticle];
                int backParticle = Enumerable.Range(0, session.ParticleCount)
                    .Where(i => session.Cloth.ParticleIsland[i] != island)
                    .OrderBy(i => NVector3.DistanceSquared(session.Solver.Positions[i], session.Solver.Positions[frontParticle])).First();

                foreach (bool orthographic in new[] { false, true })
                {
                    Camera camera = CreateCamera(CameraPosition, Vector3.zero, orthographic);
                    Vector3 position = camera.transform.position, forward = camera.transform.forward;
                    Assert.That(session.IsParticleVisible(frontParticle, position, forward, orthographic), Is.True, $"手前の布の粒子は見える (ortho={orthographic})");
                    Assert.That(session.IsParticleVisible(backParticle, position, forward, orthographic), Is.False, $"3mm 後ろの布の粒子は隠れる (ortho={orthographic})");
                }

                // 後ろから見ると、後ろの布が手前になる
                Camera behind = CreateCamera(new Vector3(0f, 0f, 1.5f), Vector3.zero);
                Assert.That(session.IsParticleVisible(backParticle, behind.transform.position, behind.transform.forward, false), Is.True);
                Assert.That(session.IsParticleVisible(frontParticle, behind.transform.position, behind.transform.forward, false), Is.False);

                // カメラの後ろにある粒子は見えない。範囲外の番号も false
                Camera away = CreateCamera(CameraPosition, new Vector3(0f, 0f, -5f));
                Assert.That(session.IsParticleVisible(frontParticle, away.transform.position, away.transform.forward, false), Is.False);
                Assert.That(session.IsParticleVisible(-1, CameraPosition, Vector3.forward, false), Is.False);
            }
        }

        [Test]
        public void IsParticleVisible_SeparateRenderers_PetticoatBehindTheSkirtIsHidden()
        {
            GameObject root = Track(TestClothMenu.CreateInScene(null, true));
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(r.sharedMesh);
                foreach (Material m in r.sharedMaterials) Track(m);
                _clearUndoOnly.Add(r);
            }
            var layers = new Renderer[] { root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>(), root.transform.Find("Petticoat").GetComponent<SkinnedMeshRenderer>() };
            Camera camera = CreateCamera(new Vector3(0f, 0.55f, -1.5f), new Vector3(0f, 0.55f, 0f));
            using (var session = new ClothEditSession(layers))
            {
                LayerContacts contacts = session.LayerContacts;
                Assert.That(contacts.Count, Is.GreaterThan(100));
                int visibleOuter = 0, hiddenInner = 0, frontPairs = 0;
                for (int k = 0; k < contacts.Count; k++)
                {
                    NVector3 outer = session.Solver.Positions[contacts.Outer[k]];
                    if (outer.Z > -0.1f || Mathf.Abs(outer.X) > 0.1f || outer.Y > 0.7f || outer.Y < 0.5f) continue;   // 正面の、中ほどの高さ
                    frontPairs++;
                    if (session.IsParticleVisible(contacts.Outer[k], camera.transform.position, camera.transform.forward, false)) visibleOuter++;
                    if (!session.IsParticleVisible(contacts.Inner[k], camera.transform.position, camera.transform.forward, false)) hiddenInner++;
                }
                Assert.That(frontPairs, Is.GreaterThan(50));
                Assert.That(visibleOuter, Is.GreaterThan(frontPairs * 95 / 100));
                Assert.That(hiddenInner, Is.GreaterThan(frontPairs * 95 / 100));
            }
        }

        // スカートとペチコートを、別のレンダラー／同じメッシュの別の島として、矩形で選ぶ
        void CheckRectOnSkirt(bool separateRenderers)
        {
            GameObject root = Track(TestClothMenu.CreateInScene(null, separateRenderers));
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(r.sharedMesh);
                foreach (Material m in r.sharedMaterials) Track(m);
                _clearUndoOnly.Add(r);
            }
            var targets = new List<Renderer> { root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>() };
            if (separateRenderers) targets.Add(root.transform.Find("Petticoat").GetComponent<SkinnedMeshRenderer>());
            ClothEditController c = StartController(targets, new ClothEditSettings { SolveMilliseconds = 6f });
            Camera camera = CreateCamera(new Vector3(0f, 0.55f, -1.5f), new Vector3(0f, 0.55f, 0f));
            // 正面の、スカートの中ほど
            Vector3 low = camera.WorldToViewportPoint(new Vector3(-0.1f, 0.5f, -0.25f)), high = camera.WorldToViewportPoint(new Vector3(0.1f, 0.62f, -0.25f));
            Rect rect = Rect.MinMaxRect(low.x, low.y, high.x, high.y);
            LayerContacts contacts = c.Session.LayerContacts;
            var inner = new HashSet<int>();
            var outer = new HashSet<int>();
            for (int k = 0; k < contacts.Count; k++)
            {
                inner.Add(contacts.Inner[k]);
                outer.Add(contacts.Outer[k]);
            }

            c.Settings.SelectThroughOcclusion = false;
            int changed = c.SelectInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, false);
            Assert.That(changed, Is.EqualTo(c.SelectionCount).And.GreaterThan(50));
            int selectedOuter = c.SelectedParticles.Count(i => outer.Contains(i));
            int selectedInner = c.SelectedParticles.Count(i => inner.Contains(i));
            Assert.That(selectedOuter, Is.GreaterThan(30), "外の布は選ぶ");
            Assert.That(selectedInner, Is.EqualTo(0), "外の布の後ろにある内の布は選ばない");

            // 隠れた点・裏側の点も選ぶ設定
            c.ClearSelection();
            c.Settings.SelectThroughOcclusion = true;
            c.SelectInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, false);
            Assert.That(c.SelectedParticles.Count(i => inner.Contains(i)), Is.GreaterThan(30), "隠れた内の布も選ぶ");
            Assert.That(c.SelectedParticles.Count(i => outer.Contains(i)), Is.GreaterThanOrEqualTo(selectedOuter));

            // 選んだものを矩形で外す
            int removed = c.SelectInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, false, rect, true);
            Assert.That(removed, Is.GreaterThan(0));
            Assert.That(c.SelectionCount, Is.EqualTo(0));
        }

        [Test]
        public void SelectInRect_SeparateRenderers_SkipsTheInnerLayerBehindTheOuterOne() => CheckRectOnSkirt(true);

        [Test]
        public void SelectInRect_SameMesh_SkipsTheInnerLayerBehindTheOuterOne() => CheckRectOnSkirt(false);

        [Test]
        public void SelectInRect_OrthographicCamera_AlsoHidesTheInnerLayer()
        {
            Renderer renderer = CreateFlat("RectOrtho", 0f, 0.003f);
            ClothEditController c = StartController(renderer);
            Camera camera = CreateCamera(CameraPosition, Vector3.zero, true);
            var rect = Rect.MinMaxRect(0.3f, 0.3f, 0.7f, 0.7f);
            c.Settings.SelectThroughOcclusion = false;
            Assert.That(c.SelectInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, true, rect, false), Is.GreaterThan(100));
            Assert.That(IslandOf(c, c.SelectedParticles).Length, Is.EqualTo(1));
            c.ClearSelection();
            c.Settings.SelectThroughOcclusion = true;
            c.SelectInRect(ViewProjection(camera), camera.transform.position, camera.transform.forward, true, rect, false);
            Assert.That(IslandOf(c, c.SelectedParticles).Length, Is.EqualTo(2));
        }

        // ---- コントローラー: 選ぶ ----

        [Test]
        public void Paint_AddsAndRemoves_InsideTheHitIslandOnly_AndNeverTouchesUndoOrTheShape()
        {
            Renderer renderer = CreateFlat("Paint", 0f, 0.003f);
            ClothEditController c = StartController(renderer);
            Vector3[] before = Snapshot(c);
            int group = Undo.GetCurrentGroup();
            int changedEvents = 0;
            c.Changed += () => changedEvents++;

            c.Mode = ClothEditMode.Select;
            c.BrushRadius = 0.03f;
            Assert.That(c.PointerDown(FrontRay(-0.1f, 0.1f), Vector3.forward), Is.True);
            int first = c.SelectionCount;
            Assert.That(first, Is.GreaterThan(10));
            Assert.That(IslandOf(c, c.SelectedParticles).Length, Is.EqualTo(1), "後ろの布までは選ばない");
            foreach (int i in c.SelectedParticles)
            {
                Assert.That(c.IsSelected(i), Is.True);
                Assert.That(Vector3.Distance(Pos(c, i), c.HoverPoint), Is.LessThanOrEqualTo(0.03f + 1e-4f));
            }
            Assert.That(Vector3.Distance(c.LastClickPoint, c.HoverPoint), Is.LessThan(1e-4f));

            int version = c.SelectionVersion;
            c.PointerDrag(FrontRay(-0.04f, 0.1f));
            Assert.That(c.SelectionCount, Is.GreaterThan(first));
            Assert.That(c.SelectionVersion, Is.GreaterThan(version));
            c.PointerUp();
            Assert.That(c.IsBusy, Is.False);
            Assert.That(changedEvents, Is.GreaterThan(0));
            Assert.That(c.SelectedParticles, Is.Ordered);
            Assert.That(c.SelectedParticles.Count, Is.EqualTo(c.SelectionCount));

            // 外す
            int selected = c.SelectionCount;
            Assert.That(c.SelectPaintBegin(FrontRay(-0.1f, 0.1f), true), Is.True);
            c.SelectPaintEnd();
            Assert.That(c.SelectionCount, Is.LessThan(selected));
            // 当たらない所では何も起きない
            version = c.SelectionVersion;
            Assert.That(c.SelectPaintBegin(FrontRay(5f, 5f), false), Is.False);
            Assert.That(c.SelectionVersion, Is.EqualTo(version));

            Assert.That(MaxDistance(Snapshot(c), before), Is.EqualTo(0f), "選んでも形は変わらない");
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(group), "選択は Undo に入れない");
        }

        [Test]
        public void SelectIsland_SelectAll_Clear_Invert_Grow_Shrink()
        {
            Renderer renderer = CreateFlat("Operations", 0f, 0.003f);
            ClothEditController c = StartController(renderer);
            int n = c.Session.ParticleCount;
            c.Session.Raycast(FrontRay(0f, 0f), out _, out _, out int hitTriangle);
            int frontIsland = c.Session.Cloth.ParticleIsland[c.Session.FindParticle(Vector3.zero, hitTriangle)];
            int islandSize = c.Session.Cloth.ParticleIsland.Count(i => i == frontIsland);

            Assert.That(c.SelectIslandAt(FrontRay(0f, 0f), false), Is.EqualTo(islandSize));
            Assert.That(c.SelectionCount, Is.EqualTo(islandSize));
            Assert.That(IslandOf(c, c.SelectedParticles).Length, Is.EqualTo(1));
            Assert.That(c.SelectIslandAt(FrontRay(0f, 0f), true), Is.EqualTo(islandSize));
            Assert.That(c.SelectionCount, Is.EqualTo(0));
            Assert.That(c.SelectIslandAt(FrontRay(5f, 5f), false), Is.EqualTo(0));

            Assert.That(c.SelectAll(), Is.EqualTo(n));
            Assert.That(c.SelectionCount, Is.EqualTo(n));
            Assert.That(c.SelectAll(), Is.EqualTo(0));
            Assert.That(c.ClearSelection(), Is.EqualTo(n));
            Assert.That(c.SelectionCount, Is.EqualTo(0));
            Assert.That(c.ClearSelection(), Is.EqualTo(0));

            SelectPatch(c, 0f, 0f, 0.05f);
            int patch = c.SelectionCount;
            var patchSet = c.SelectedParticles.ToList();
            Assert.That(c.InvertSelection(), Is.EqualTo(n));
            Assert.That(c.SelectionCount, Is.EqualTo(n - patch));
            Assert.That(patchSet.Any(i => c.IsSelected(i)), Is.False);
            Assert.That(c.InvertSelection(), Is.EqualTo(n));
            Assert.That(c.SelectedParticles.SequenceEqual(patchSet), Is.True);

            int grown = c.GrowSelection();
            Assert.That(grown, Is.GreaterThan(0));
            Assert.That(c.SelectionCount, Is.EqualTo(patch + grown));
            Assert.That(IslandOf(c, c.SelectedParticles).Length, Is.EqualTo(1), "同じ島の中だけ");
            Assert.That(patchSet.All(i => c.IsSelected(i)), Is.True);
            int shrunk = c.ShrinkSelection();
            Assert.That(shrunk, Is.GreaterThan(0));
            Assert.That(c.SelectionCount, Is.EqualTo(patch + grown - shrunk));
            Assert.That(patchSet.All(i => c.IsSelected(i)), Is.True);
            // 狭めても、島の端は削れない（全体を選んだままでも変わらない）
            c.SelectIslandAt(FrontRay(0f, 0f), false);
            int whole = c.SelectionCount;
            Assert.That(c.ShrinkSelection(), Is.GreaterThanOrEqualTo(0));
            Assert.That(c.SelectionCount, Is.GreaterThan(whole / 2));
        }

        [Test]
        public void MirrorSelection_AddsTheOppositeSide_EvenWhenSymmetryIsOff()
        {
            Renderer renderer = CreateFlat("MirrorOp");
            ClothEditController c = StartController(renderer);
            Assert.That(c.Settings.SymmetryEnabled, Is.False);
            SelectPatch(c, 0.12f, 0.05f, 0.04f);
            var right = c.SelectedParticles.ToList();
            Assert.That(right.All(i => Rest(c, i).x > 0f), Is.True);

            int added = c.MirrorSelection();
            Assert.That(added, Is.GreaterThan(right.Count * 9 / 10));
            Assert.That(c.SelectionCount, Is.EqualTo(right.Count + added));
            // 右側の粒子のほとんどは、鏡に映した位置のすぐ近くに、選ばれた粒子がある（縁では粒子の並びのずれで欠けることがある）
            int covered = 0;
            foreach (int i in right)
            {
                Vector3 mirrorPoint = Rest(c, i);
                mirrorPoint.x = -mirrorPoint.x;
                if (c.SelectedParticles.Any(j => Rest(c, j).x < 0f && Vector3.Distance(Rest(c, j), mirrorPoint) < 0.012f)) covered++;
            }
            Assert.That(covered, Is.GreaterThan(right.Count * 95 / 100));
            // もう一度映しても、ほとんど増えない（粒子の並びが左右でぴったり対応しないので、縁で数個は足されることがある）
            Assert.That(c.MirrorSelection(), Is.LessThan(right.Count / 5));

            // 軸を変えれば別の面で映す
            c.ClearSelection();
            SelectPatch(c, 0.05f, 0.12f, 0.04f);
            c.Settings.SymmetryAxis = SymmetryAxis.Y;
            int viaY = c.MirrorSelection();
            Assert.That(viaY, Is.GreaterThan(0));
            Assert.That(c.SelectedParticles.Any(i => Rest(c, i).y < 0f), Is.True);
        }

        [Test]
        public void SymmetryOn_SelectingDoesNotAddTheMirrorSide_ButMirroredSelectionShowsIt()
        {
            Renderer renderer = CreateFlat("MirroredView");
            ClothEditController c = StartController(renderer, new ClothEditSettings { SymmetryEnabled = true, SolveMilliseconds = 6f });
            SelectPatch(c, 0.12f, 0.05f, 0.04f);
            Assert.That(c.SelectedParticles.All(i => Rest(c, i).x > 0f), Is.True, "鏡像の側は選ばない");

            IReadOnlyList<int> mirrored = c.MirroredSelection;
            Assert.That(mirrored.Count, Is.GreaterThan(c.SelectionCount * 9 / 10));
            Assert.That(mirrored.All(i => !c.IsSelected(i) && Rest(c, i).x < 0f), Is.True);
            Assert.That(c.MirroredSelection, Is.SameAs(mirrored), "選択も設定も変わらなければ同じ結果を使い回す");

            c.Settings.SymmetryEnabled = false;
            Assert.That(c.MirroredSelection.Count, Is.EqualTo(0));
            c.Settings.SymmetryEnabled = true;
            Assert.That(c.MirroredSelection.Count, Is.EqualTo(mirrored.Count));
            c.ClearSelection();
            Assert.That(c.MirroredSelection.Count, Is.EqualTo(0));
        }

        [Test]
        public void PinSelected_IsOneUndoStep_AndSelectPinnedSelectsThePinnedOnes_SelectionStaysOnUndo()
        {
            Renderer renderer = CreateFlat("PinOps");
            ClothEditController c = StartController(renderer);
            SelectPatch(c, 0f, 0f, 0.04f);
            var patch = c.SelectedParticles.ToList();
            int version = c.SelectionVersion;

            Assert.That(c.PinSelected(true), Is.EqualTo(patch.Count));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(patch.Count));
            Assert.That(patch.All(i => c.Session.Solver.IsPinned(i)), Is.True);
            Assert.That(c.SelectionVersion, Is.EqualTo(version), "ピン留めしても選択は変わらない");
            Assert.That(c.PinSelected(true), Is.EqualTo(0));

            Undo.PerformUndo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0), "ピン留めは Undo の1段で戻る");
            Assert.That(c.SelectedParticles.SequenceEqual(patch), Is.True, "Undo しても選択は残る");
            Undo.PerformRedo();
            Assert.That(c.Session.PinnedCount, Is.EqualTo(patch.Count));
            Assert.That(c.SelectionCount, Is.EqualTo(patch.Count));

            c.ClearSelection();
            Assert.That(c.SelectPinned(), Is.EqualTo(patch.Count));
            Assert.That(c.SelectedParticles.SequenceEqual(patch), Is.True);
            Assert.That(c.SelectPinned(), Is.EqualTo(0));

            Assert.That(c.PinSelected(false), Is.EqualTo(patch.Count));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(0));
            c.ClearSelection();
            Assert.That(c.PinSelected(true), Is.EqualTo(0), "選択が空なら何もしない");
        }

        // ---- ハンドルの基準 ----

        [Test]
        public void SelectionPivot_FollowsPivotMode_AndLastClickTravelsWithTheCloth()
        {
            Renderer renderer = CreateFlat("Pivot");
            ClothEditController c = StartController(renderer, new ClothEditSettings { BrushCompliance = 0f, SolveMilliseconds = 6f });
            Assert.That(c.SelectionPivot, Is.EqualTo(Vector3.zero), "選択が空なら原点");
            Assert.That(c.HasLastClickPoint, Is.False);

            c.Mode = ClothEditMode.Select;
            c.BrushRadius = 0.03f;
            Assert.That(c.PointerDown(FrontRay(-0.1f, 0.05f), Vector3.forward), Is.True);
            Vector3 clicked = c.HoverPoint;
            c.PointerDrag(FrontRay(0f, 0.05f));
            c.PointerDrag(FrontRay(0.1f, 0.05f));
            c.PointerUp();
            Assert.That(c.HasLastClickPoint, Is.True);

            c.Settings.PivotMode = SelectionPivot.Center;
            Vector3 center = c.SelectionPivot;
            Assert.That(Vector3.Distance(center, Mean(c, c.SelectedParticles)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(center, clicked), Is.GreaterThan(0.05f));

            c.Settings.PivotMode = SelectionPivot.LastClicked;
            Assert.That(Vector3.Distance(c.SelectionPivot, clicked), Is.LessThan(1e-4f), "最後にクリックした点");

            // 選んだ点を動かすと、クリックした点も付いていく（ハンドルの間は押したときの値のまま）
            Assert.That(c.BeginSelectionTransform(), Is.True);
            Vector3 pivotAtPress = c.SelectionPivot;
            c.UpdateSelectionTransform(new Vector3(0f, 0.04f, 0f), Quaternion.identity, Vector3.one, false);
            c.Tick(NextTime());
            Assert.That(Vector3.Distance(c.SelectionPivot, pivotAtPress), Is.LessThan(1e-6f), "動かしている間は動かない");
            c.EndSelectionTransform();
            Settle(c);
            Assert.That(Vector3.Distance(c.SelectionPivot, clicked + new Vector3(0f, 0.04f, 0f)), Is.LessThan(0.002f));

            // クリックした所を外すと、その点は使わず、中心に戻る
            Assert.That(c.SelectPaintBegin(FrontRay(-0.1f, 0.09f), true), Is.True);
            c.SelectPaintEnd();
            Assert.That(c.HasLastClickPoint, Is.False);
            Assert.That(c.SelectionCount, Is.GreaterThan(0));
            Assert.That(Vector3.Distance(c.SelectionPivot, Mean(c, c.SelectedParticles)), Is.LessThan(1e-4f));
        }

        [Test]
        public void HandleRotation_FollowsHandleSpace()
        {
            // アバターの基準（Animator のある親）を 40 度回し、その下で布を 30 度傾ける
            GameObject avatar = Track(new GameObject("AvatarRoot"));
            avatar.AddComponent<Animator>();
            avatar.transform.rotation = Quaternion.Euler(0f, 40f, 0f);
            Renderer renderer = CreateFlat("Tilted", Quaternion.Euler(30f, 0f, 0f), avatar.transform);
            ClothEditController c = StartController(renderer);
            Assert.That(c.AvatarRoot, Is.SameAs(avatar.transform));
            Assert.That(c.HandleRotation, Is.EqualTo(Quaternion.identity), "選択が空でも Global は単位");

            c.SelectAll();
            c.Settings.HandleSpace = HandleOrientation.Global;
            Assert.That(Quaternion.Angle(c.HandleRotation, Quaternion.identity), Is.LessThan(1e-3f));
            c.Settings.HandleSpace = HandleOrientation.Avatar;
            Assert.That(Quaternion.Angle(c.HandleRotation, avatar.transform.rotation), Is.LessThan(1e-3f));

            c.Settings.HandleSpace = HandleOrientation.Normal;
            Quaternion normal = c.HandleRotation;
            Vector3 surfaceNormal = renderer.transform.rotation * Vector3.back;   // 表（カメラ側）は -Z
            Assert.That(Vector3.Dot(normal * Vector3.forward, surfaceNormal), Is.GreaterThan(0.999f), "Z が面の法線");
            Assert.That(Vector3.Dot(normal * Vector3.up, Vector3.up), Is.GreaterThan(0.5f), "Y はワールドの上をなるべく保つ");
            Assert.That(Mathf.Abs(Vector3.Dot(normal * Vector3.right, Vector3.up)), Is.LessThan(0.01f), "X は水平");
            c.ClearSelection();
            Assert.That(c.HandleRotation, Is.EqualTo(Quaternion.identity), "選択が空なら Normal も単位");
        }

        // ---- 選んだ点の変形 ----

        [Test]
        public void MoveSelection_SelectedPointsReachTheTarget_FarPointsStay_NearbyOnesFollowPartway()
        {
            Renderer renderer = CreateFlat("Move");
            var settings = new ClothEditSettings { Falloff = FalloffPreset.Smooth, SelectionFalloffRadius = 0.05f, SolveMilliseconds = 6f };
            ClothEditController c = StartController(renderer, settings);
            PinTop(c);
            Vector3[] before = Snapshot(c);
            SelectPatch(c, 0f, -0.05f, 0.03f);
            var selected = c.SelectedParticles.ToList();
            Vector3 centerBefore = Mean(c, selected);

            Assert.That(c.BeginSelectionTransform(), Is.True);
            Assert.That(c.IsTransformingSelection, Is.True);
            Assert.That(c.IsBusy, Is.True);
            // 布の長さで届く、面の外への動き（面の中の動きは、伸びない布では届かない）
            var target = new Vector3(0f, 0f, -0.02f);
            for (int k = 1; k <= 10; k++)
            {
                c.UpdateSelectionTransform(target * (k / 10f), Quaternion.identity, Vector3.one, false);
                c.Tick(NextTime());
            }
            // ドラッグ中の範囲の色分け（つまんだ範囲）が出る
            Assert.That(c.PreviewCount, Is.GreaterThan(0));
            c.EndSelectionTransform();
            Assert.That(c.IsTransformingSelection, Is.False);
            Assert.That(c.IsSettling, Is.True);
            Settle(c);

            Vector3 moved = Mean(c, selected) - centerBefore;
            // つまんだ範囲は柔らかい制約なので、布の長さに引き止められる分だけ手前で止まる
            Assert.That(moved.z, Is.LessThan(-0.02f * 0.7f), "選んだ点が目標の近くまで動く");
            Assert.That(moved.z, Is.GreaterThan(-0.02f * 1.05f));
            Assert.That(Mathf.Abs(moved.x) + Mathf.Abs(moved.y), Is.LessThan(0.004f));
            Assert.That(c.LastStretch, Is.LessThan(0.03f));

            // 遠い点（ピンの近く、反対側の隅）はほぼ動かない
            int cornerParticle = NearestRest(c, new Vector3(0.3f, -0.3f, 0f));
            Assert.That(Vector3.Distance(Pos(c, cornerParticle), before[cornerParticle]), Is.LessThan(0.2f * moved.magnitude), "遠い点は、選んだ点よりずっと小さくしか動かない");
            foreach (int i in Enumerable.Range(0, before.Length).Where(i => c.Session.Solver.IsPinned(i))) Assert.That(Pos(c, i), Is.EqualTo(before[i]));

            // 選んだ点のすぐ外側は、途中まで動く
            int ring = NearestRest(c, centerBefore + new Vector3(0.065f, 0f, 0f));
            Assert.That(c.IsSelected(ring), Is.False);
            float ringMove = Vector3.Distance(Pos(c, ring), before[ring]);
            float selectedMove = moved.magnitude;
            Assert.That(ringMove, Is.GreaterThan(0.1f * selectedMove).And.LessThan(0.95f * selectedMove), $"外側 {ringMove} 選んだ点 {selectedMove}");

            // なめらかに減る範囲の表示用の重み
            Assert.That(c.SelectionWeightCount, Is.GreaterThan(0));
            for (int k = 0; k < c.SelectionWeightCount; k++)
            {
                Assert.That(c.IsSelected(c.SelectionWeightParticle(k)), Is.False);
                Assert.That(c.SelectionWeightValue(k), Is.InRange(1e-6f, 1f));
            }
        }

        [Test]
        public void SelectionWeights_AreRecomputedOnlyWhenTheInputsChange_AndSwitchOff()
        {
            Renderer renderer = CreateFlat("Weights");
            ClothEditController c = StartController(renderer, new ClothEditSettings { Falloff = FalloffPreset.Linear, SelectionFalloffRadius = 0.04f });
            Assert.That(c.SelectionWeightCount, Is.EqualTo(0), "選択が空なら無い");
            SelectPatch(c, 0f, 0f, 0.02f);
            int count = c.SelectionWeightCount;
            Assert.That(count, Is.GreaterThan(20));
            float maxWeight = 0f;
            for (int k = 0; k < count; k++) maxWeight = Mathf.Max(maxWeight, c.SelectionWeightValue(k));
            Assert.That(maxWeight, Is.LessThan(1f));
            int first = c.SelectionWeightParticle(0);

            c.Settings.SelectionFalloffRadius = 0.08f;
            Assert.That(c.SelectionWeightCount, Is.GreaterThan(count), "半径を広げると増える");
            c.Settings.SelectionFalloffRadius = 0f;
            Assert.That(c.SelectionWeightCount, Is.EqualTo(0), "半径 0 は選んだ点だけ");
            c.Settings.SelectionFalloffRadius = 0.04f;
            Assert.That(c.SelectionWeightCount, Is.EqualTo(count));
            Assert.That(c.SelectionWeightParticle(0), Is.EqualTo(first));
            c.Settings.ShowSelectionWeights = false;
            Assert.That(c.SelectionWeightCount, Is.EqualTo(0));
            c.Settings.ShowSelectionWeights = true;
            c.SelectAll();
            Assert.That(c.SelectionWeightCount, Is.EqualTo(0), "すべて選んでいれば、外側が無い");

            // 上限を超えるときは間引く
            c.ClearSelection();
            c.Settings.SelectionFalloffRadius = ClothEditSettings.MaxSelectionFalloffRadius;
            c.SelectPaintBegin(FrontRay(0f, 0f), false);
            c.SelectPaintEnd();
            Assert.That(c.SelectionWeightCount, Is.LessThanOrEqualTo(ClothEditController.MaxSelectionWeightParticles));
        }

        // 精密操作を使わない値は、そのまま使う。平らな布の中央の小さな塊を、硬い目標で動かして位置を確かめる
        ClothEditController StartHardMove(out List<int> selected, ClothEditSettings settings = null)
        {
            Renderer renderer = CreateFlat("Hard");
            settings = settings ?? new ClothEditSettings();
            settings.BrushCompliance = 0f;
            settings.SelectionFalloffRadius = 0f;
            settings.SolveMilliseconds = 6f;
            ClothEditController c = StartController(renderer, settings);
            PinTop(c);
            SelectPatch(c, 0f, 0f, 0.03f);
            selected = c.SelectedParticles.ToList();
            return c;
        }

        [Test]
        public void PreciseMode_ScalesTheIncrements_AndSwitchingDoesNotJump()
        {
            ClothEditController c = StartHardMove(out List<int> selected);
            Vector3 start = Mean(c, selected);
            Assert.That(c.BeginSelectionTransform(), Is.True);

            // 1. 普通に 0.10 まで
            c.UpdateSelectionTransform(new Vector3(0f, 0f, -0.10f) * 0.1f, Quaternion.identity, Vector3.one, false);
            c.Tick(NextTime());
            c.Tick(NextTime());
            Assert.That((Mean(c, selected) - start).z, Is.EqualTo(-0.01f).Within(1e-4f));
            // 2. 精密操作で 0.02 まで（増分 0.01 の PrecisionFactor 倍だけ進む）
            c.UpdateSelectionTransform(new Vector3(0f, 0f, -0.02f), Quaternion.identity, Vector3.one, true);
            c.Tick(NextTime());
            c.Tick(NextTime());
            float expected = -0.01f - 0.01f * ClothEditController.PrecisionFactor;
            Assert.That((Mean(c, selected) - start).z, Is.EqualTo(expected).Within(1e-4f));
            // 3. 同じ値のまま精密操作を切っても、飛ばない
            c.UpdateSelectionTransform(new Vector3(0f, 0f, -0.02f), Quaternion.identity, Vector3.one, false);
            c.Tick(NextTime());
            c.Tick(NextTime());
            Assert.That((Mean(c, selected) - start).z, Is.EqualTo(expected).Within(1e-4f));
            // 4. 普通に 0.05 まで（増分 0.03 がそのまま足される）
            c.UpdateSelectionTransform(new Vector3(0f, 0f, -0.05f), Quaternion.identity, Vector3.one, false);
            c.Tick(NextTime());
            c.Tick(NextTime());
            Assert.That((Mean(c, selected) - start).z, Is.EqualTo(expected - 0.03f).Within(1e-4f));
            c.Cancel();
        }

        [Test]
        public void PreciseMode_ShrinksRotationAndScaleIncrements()
        {
            ClothEditController c = StartHardMove(out List<int> selected);
            Vector3 pivot = c.SelectionPivot;
            int far = selected.OrderByDescending(i => Vector3.Distance(Pos(c, i), pivot)).First();
            Vector3 startOffset = Pos(c, far) - pivot;

            Assert.That(c.BeginSelectionTransform(), Is.True);
            // 40 度（ワールドの Z まわり）を精密操作で。実際には 40 度の PrecisionFactor 倍だけ回る
            c.UpdateSelectionTransform(Vector3.zero, Quaternion.AngleAxis(40f, Vector3.forward), Vector3.one, true);
            c.Tick(NextTime());
            c.Tick(NextTime());
            float angle = Vector3.Angle(startOffset, Pos(c, far) - pivot);
            Assert.That(angle, Is.EqualTo(40f * ClothEditController.PrecisionFactor).Within(0.2f));

            // 拡大は対数で縮める。2 倍を精密操作で → 2^PrecisionFactor 倍
            c.UpdateSelectionTransform(Vector3.zero, Quaternion.AngleAxis(40f, Vector3.forward), Vector3.one * 2f, true);
            c.Tick(NextTime());
            c.Tick(NextTime());
            float ratio = (Pos(c, far) - pivot).magnitude / startOffset.magnitude;
            Assert.That(ratio, Is.EqualTo(Mathf.Pow(2f, ClothEditController.PrecisionFactor)).Within(0.02f));
            c.Cancel();
        }

        [Test]
        public void RotateAndAxisScale_InTheNormalFrame_MatchTheFormula()
        {
            // 傾けた布の全体を、硬い目標で動かす。向きは面の法線の向き
            Renderer renderer = CreateFlat("Frame", Quaternion.Euler(30f, 0f, 0f));
            var settings = new ClothEditSettings { BrushCompliance = 0f, SelectionFalloffRadius = 0f, HandleSpace = HandleOrientation.Normal, SolveMilliseconds = 6f };
            ClothEditController c = StartController(renderer, settings);
            c.SelectAll();
            Quaternion frame = c.HandleRotation;
            Vector3 pivot = c.SelectionPivot;
            Vector3[] start = Snapshot(c);

            // 面の法線のまわりに 45 度
            Quaternion spin = Quaternion.AngleAxis(45f, frame * Vector3.forward);
            Assert.That(c.ApplySelectionTransform(Vector3.zero, spin, Vector3.one), Is.True);
            Settle(c);
            for (int i = 0; i < start.Length; i += 37)
            {
                Vector3 expected = pivot + spin * (start[i] - pivot);
                Assert.That(Vector3.Distance(Pos(c, i), expected), Is.LessThan(0.002f), $"回転 粒子 {i}");
            }

            // 向きの Y 軸（面の上向き）だけ 1.5 倍、X と Z はそのまま
            c.UndoLast();
            Assert.That(Quaternion.Angle(c.HandleRotation, frame), Is.LessThan(0.1f));
            var scale = new Vector3(1f, 1.5f, 1f);
            Assert.That(c.ApplySelectionTransform(new Vector3(0.01f, 0f, 0f), Quaternion.identity, scale), Is.True);
            Settle(c);
            Quaternion inverse = Quaternion.Inverse(frame);
            for (int i = 0; i < start.Length; i += 37)
            {
                Vector3 local = inverse * (start[i] - pivot);
                local = Vector3.Scale(local, scale);
                Vector3 expected = pivot + frame * local + new Vector3(0.01f, 0f, 0f);
                Assert.That(Vector3.Distance(Pos(c, i), expected), Is.LessThan(0.002f), $"拡大 粒子 {i}");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NumericInput_GivesTheSameResultAsDraggingTheHandle(bool withScale)
        {
            var translation = new Vector3(0.01f, 0.01f, -0.02f);
            Quaternion rotation = Quaternion.AngleAxis(30f, new Vector3(0.2f, 1f, 0.3f).normalized);
            Vector3 scale = withScale ? new Vector3(1.2f, 0.9f, 1.1f) : Vector3.one;

            ClothEditController c = StartHardMove(out List<int> selected);
            Assert.That(c.ApplySelectionTransform(translation, rotation, scale), Is.True);
            Settle(c);
            Vector3[] numeric = selected.Select(i => Pos(c, i)).ToArray();
            Vector3[] numericAll = Snapshot(c);

            Undo.PerformUndo();
            Assert.That(c.Session.IsShapeEdited(1e-5f), Is.False, "Undo で形が戻る");

            Assert.That(c.BeginSelectionTransform(), Is.True);
            for (int k = 1; k <= 8; k++)
            {
                float f = k / 8f;
                c.UpdateSelectionTransform(translation * f, Quaternion.Slerp(Quaternion.identity, rotation, f), Vector3.Lerp(Vector3.one, scale, f), false);
                c.Tick(NextTime());
            }
            c.EndSelectionTransform();
            Settle(c);
            // 選んだ点は、硬い目標なので、どちらも同じ位置になる
            for (int k = 0; k < selected.Count; k++) Assert.That(Vector3.Distance(Pos(c, selected[k]), numeric[k]), Is.LessThan(2e-4f), $"粒子 {selected[k]}");
            if (!withScale)
            {
                // 周りの布は、仕上げの許容の範囲で同じ（伸びを許さない目標では、経路によって落ち着く先が少し違う）
                Vector3[] viaHandle = Snapshot(c);
                float sum = 0f;
                for (int i = 0; i < viaHandle.Length; i++) sum += Vector3.Distance(viaHandle[i], numericAll[i]);
                Assert.That(sum / viaHandle.Length, Is.LessThan(0.008f));
            }

            // 動かさない値は、false を返し、記録も残さない
            Undo.PerformUndo();
            Assert.That(c.ApplySelectionTransform(Vector3.zero, Quaternion.identity, Vector3.one), Is.False);
            Settle(c);
            Assert.That(c.Session.IsShapeEdited(1e-5f), Is.False);
        }

        [Test]
        public void Undo_RestoresTheShape_AndTheSelectionStays_RedoMovesAgain()
        {
            ClothEditController c = StartHardMove(out List<int> selected);
            Vector3[] before = Snapshot(c);
            int version = c.SelectionVersion;
            Assert.That(c.ApplySelectionTransform(new Vector3(0f, 0.03f, -0.02f), Quaternion.identity, Vector3.one), Is.True);
            Settle(c);
            Vector3[] after = Snapshot(c);
            Assert.That(MaxDistance(after, before), Is.GreaterThan(0.015f));

            c.UndoLast();
            Assert.That(MaxDistance(Snapshot(c), before), Is.LessThan(1e-5f), "形が戻る");
            Assert.That(c.SelectionVersion, Is.EqualTo(version));
            Assert.That(c.SelectedParticles.SequenceEqual(selected), Is.True, "選択は残る");

            c.RedoLast();
            Assert.That(MaxDistance(Snapshot(c), after), Is.LessThan(1e-5f));
            Assert.That(c.SelectedParticles.SequenceEqual(selected), Is.True);

            // 選んだ点の変形は Undo の1段（選ぶ操作を挟んでも、2回で2段）
            c.UndoLast();
            c.SelectAll();
            Assert.That(c.ApplySelectionTransform(new Vector3(0.02f, 0f, 0f), Quaternion.identity, Vector3.one), Is.True);
            Settle(c);
            c.UndoLast();
            Assert.That(MaxDistance(Snapshot(c), before), Is.LessThan(1e-5f));
            Assert.That(c.SelectionCount, Is.EqualTo(c.Session.ParticleCount));
        }

        [Test]
        public void Cancel_ReturnsToTheShapeBeforePressing_AndKeepsTheSelection()
        {
            ClothEditController c = StartHardMove(out List<int> selected);
            Vector3[] before = Snapshot(c);

            Assert.That(c.BeginSelectionTransform(), Is.True);
            for (int k = 1; k <= 6; k++)
            {
                c.UpdateSelectionTransform(new Vector3(0.01f * k, 0f, -0.01f * k), Quaternion.identity, Vector3.one, false);
                c.Tick(NextTime());
            }
            Assert.That(MaxDistance(Snapshot(c), before), Is.GreaterThan(0.02f));
            Assert.That(c.Cancel(), Is.True);
            Assert.That(c.IsTransformingSelection, Is.False);
            Assert.That(c.IsBusy, Is.False);
            Assert.That(MaxDistance(Snapshot(c), before), Is.LessThan(1e-5f));
            Assert.That(c.SelectedParticles.SequenceEqual(selected), Is.True);
            Assert.That(c.Session.IsGrabbing, Is.False);

            // 離したあとの仕上げ中でも戻れる
            Assert.That(c.BeginSelectionTransform(), Is.True);
            c.UpdateSelectionTransform(new Vector3(0.03f, 0f, 0f), Quaternion.identity, Vector3.one, false);
            c.Tick(NextTime());
            c.EndSelectionTransform();
            Assert.That(c.IsSettling, Is.True);
            Assert.That(c.Cancel(), Is.True);
            Assert.That(MaxDistance(Snapshot(c), before), Is.LessThan(1e-5f));
            Assert.That(c.Session.IsGrabbing, Is.False);
            Assert.That(c.Cancel(), Is.False, "取り消すものがなければ false");

            // 動かさずに離すと、記録を残さずに終わる
            Assert.That(c.BeginSelectionTransform(), Is.True);
            c.UpdateSelectionTransform(Vector3.zero, Quaternion.identity, Vector3.one, false);
            c.EndSelectionTransform();
            Assert.That(c.IsBusy, Is.False);
            Assert.That(c.Session.IsShapeEdited(1e-5f), Is.False);

            // 選択が空なら始められない
            c.ClearSelection();
            Assert.That(c.BeginSelectionTransform(), Is.False);
            Assert.That(c.ApplySelectionTransform(Vector3.one, Quaternion.identity, Vector3.one), Is.False);
        }

        [Test]
        public void SelectTool_PaintingCanBeCancelled_AndKeepsWhatWasPainted()
        {
            Renderer renderer = CreateFlat("EscPaint");
            ClothEditController c = StartController(renderer);
            c.Mode = ClothEditMode.Select;
            Assert.That(c.PointerDown(FrontRay(0f, 0f), Vector3.forward), Is.True);
            int painted = c.SelectionCount;
            Assert.That(painted, Is.GreaterThan(0));
            Assert.That(c.Cancel(), Is.True);
            Assert.That(c.SelectionCount, Is.EqualTo(painted));
            c.PointerDrag(FrontRay(0.1f, 0f));
            Assert.That(c.SelectionCount, Is.EqualTo(painted), "取り消したあとは塗らない");
            Assert.That(c.Cancel(), Is.False);
        }

        // ---- 対称 ----

        [Test]
        public void Symmetry_TheOppositeSideMovesMirrored_AndAMidlineSelectionStaysSymmetric()
        {
            Renderer renderer = CreateFlat("SymmetricMove");
            var settings = new ClothEditSettings { SymmetryEnabled = true, BrushCompliance = 0f, SelectionFalloffRadius = 0f, SolveMilliseconds = 6f };
            ClothEditController c = StartController(renderer, settings);
            SelectPatch(c, 0.12f, -0.05f, 0.03f);
            var selected = c.SelectedParticles.ToList();
            Assert.That(c.SelectedParticles.All(i => Rest(c, i).x > 0f), Is.True);

            var move = new Vector3(0.02f, 0.03f, -0.03f);
            Assert.That(c.ApplySelectionTransform(move, Quaternion.identity, Vector3.one), Is.True);
            Settle(c);
            foreach (int i in selected)
            {
                Assert.That(Vector3.Distance(Pos(c, i), Rest(c, i) + move), Is.LessThan(1e-3f));
                Vector3 mirrorRest = Rest(c, i);
                mirrorRest.x = -mirrorRest.x;
                int j = NearestRest(c, mirrorRest);
                Assert.That(Vector3.Distance(Pos(c, j), Rest(c, j) + new Vector3(-move.x, move.y, move.z)), Is.LessThan(1e-3f), $"粒子 {i} の鏡像 {j} は鏡に映して動く");
            }

            // 中央線をまたいで選ぶと、鏡像は使わず、動きは面に沿う成分だけ（左右が崩れない）
            c.UndoLast();
            c.ClearSelection();
            SelectPatch(c, 0f, 0.1f, 0.04f);
            Assert.That(c.SelectedParticles.Any(i => Rest(c, i).x < 0f) && c.SelectedParticles.Any(i => Rest(c, i).x > 0f), Is.True);
            Assert.That(c.ApplySelectionTransform(new Vector3(0.03f, 0f, -0.03f), Quaternion.identity, Vector3.one), Is.True);
            Settle(c);
            foreach (int i in c.SelectedParticles)
            {
                Assert.That(Pos(c, i).x, Is.EqualTo(Rest(c, i).x).Within(1e-3f), "鏡の面に垂直な成分は消える");
                Assert.That(Pos(c, i).z, Is.EqualTo(-0.03f).Within(1e-3f));
            }
            // 粒子の並びは左右でぴったりは対応しないので、粗い許容で比べる（片側だけ動いたなら、センチの単位で食い違う）
            for (int i = 0; i < c.Session.ParticleCount; i += 11)
            {
                Vector3 mirrorRest = Rest(c, i);
                mirrorRest.x = -mirrorRest.x;
                int j = NearestRest(c, mirrorRest);
                Assert.That((Pos(c, i).x - Rest(c, i).x) + (Pos(c, j).x - Rest(c, j).x), Is.EqualTo(0f).Within(0.004f), $"左右対称 {i}/{j}");
                Assert.That(Pos(c, i).z, Is.EqualTo(Pos(c, j).z).Within(0.012f));
            }
        }

        // ---- 選択の引き継ぎ ----

        [Test]
        public void SavingKeepsTheSelectionByParticleNumber_AndStartingOrEndingEmptiesIt()
        {
            Renderer renderer = CreateFlat("SaveKeep");
            ClothEditController c = StartController(renderer, new ClothEditSettings { SelectionFalloffRadius = 0f, SolveMilliseconds = 6f });
            PinTop(c);
            SelectPatch(c, 0f, -0.05f, 0.03f);
            var selected = c.SelectedParticles.ToList();
            Vector3 click = c.LastClickPoint;
            Assert.That(c.ApplySelectionTransform(new Vector3(0f, 0f, -0.03f), Quaternion.identity, Vector3.one), Is.True);
            Settle(c);
            Vector3 clickMoved = c.LastClickPoint;
            Assert.That(Vector3.Distance(clickMoved, click + new Vector3(0f, 0f, -0.03f)), Is.LessThan(0.01f));

            Assert.That(c.Save("SelectionShape", out string message), Is.True, message);
            _clearUndoOnly.Add(c.LastSavedMesh);
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.SelectionCount, Is.EqualTo(selected.Count));
            Assert.That(c.SelectedParticles.SequenceEqual(selected), Is.True, "保存後の作り直しでも、粒子の番号が同じなので選択が残る");
            Assert.That(c.HasLastClickPoint, Is.True);
            Assert.That(Vector3.Distance(c.LastClickPoint, clickMoved), Is.LessThan(0.005f));

            c.End();
            Assert.That(c.SelectionCount, Is.EqualTo(0));
            Assert.That(c.SelectedParticles.Count, Is.EqualTo(0));
            Assert.That(c.IsSelected(0), Is.False);
            Assert.That(c.TryStart(renderer, out string error), Is.True, error);
            Assert.That(c.SelectionCount, Is.EqualTo(0), "新しく始めると空");
        }

        [Test]
        public void RebuildingTheSession_ReselectsParticlesNearTheSelectedPositionsInTheSameIsland()
        {
            Renderer renderer = CreateFlat("RebuildKeep", 0f, 0.003f);
            ClothEditController c = StartController(renderer, new ClothEditSettings { SolveMilliseconds = 6f });
            SelectPatch(c, 0f, -0.05f, 0.04f);
            var selected = c.SelectedParticles.ToList();
            int island = c.Session.Cloth.ParticleIsland[selected[0]];
            Vector3 centerBefore = Mean(c, selected);

            // 形を変えずに保存し、保存を Undo すると、粒子を置き直す作り直しになる（番号が変わるので位置と島で選び直す）
            Assert.That(c.Save("RebuildShape", out string message), Is.True, message);
            _clearUndoOnly.Add(c.LastSavedMesh);
            Undo.PerformUndo();
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.SelectionCount, Is.GreaterThan(selected.Count / 2).And.LessThan(selected.Count * 2));
            Assert.That(Vector3.Distance(Mean(c, c.SelectedParticles), centerBefore), Is.LessThan(0.01f));
            Assert.That(c.SelectedParticles.All(i => c.Session.Cloth.ParticleIsland[i] == island), Is.True, "同じ島の粒子だけ");
        }

        // ---- 設定 ----

        [Test]
        public void Settings_SelectionValues_HaveDefaults_AreSanitized_SavedAndReset()
        {
            var settings = new ClothEditSettings();
            Assert.That(settings.HandleType, Is.EqualTo(SelectionHandle.Move));
            Assert.That(settings.PivotMode, Is.EqualTo(SelectionPivot.Center));
            Assert.That(settings.HandleSpace, Is.EqualTo(HandleOrientation.Global));
            Assert.That(settings.SelectionFalloffRadius, Is.EqualTo(0.03f));
            Assert.That(settings.SelectThroughOcclusion, Is.False);
            Assert.That(settings.ShowSelectionWeights, Is.True);
            Assert.That((int)ClothEditMode.Select, Is.EqualTo(6), "道具の数値は末尾に足す");

            settings.HandleType = (SelectionHandle)99;
            settings.PivotMode = (SelectionPivot)99;
            settings.HandleSpace = (HandleOrientation)99;
            settings.SelectionFalloffRadius = float.NaN;
            settings.Sanitize();
            Assert.That(settings.HandleType, Is.EqualTo(SelectionHandle.Move));
            Assert.That(settings.PivotMode, Is.EqualTo(SelectionPivot.Center));
            Assert.That(settings.HandleSpace, Is.EqualTo(HandleOrientation.Global));
            Assert.That(settings.SelectionFalloffRadius, Is.EqualTo(ClothEditSettings.DefaultSelectionFalloffRadius));
            settings.SelectionFalloffRadius = 5f;
            settings.Sanitize();
            Assert.That(settings.SelectionFalloffRadius, Is.EqualTo(ClothEditSettings.MaxSelectionFalloffRadius));
            settings.SelectionFalloffRadius = -1f;
            settings.Sanitize();
            Assert.That(settings.SelectionFalloffRadius, Is.EqualTo(0f));
            settings.Tool = (ClothEditMode)99;
            settings.Sanitize();
            Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Grab));

            var saved = new ClothEditSettings
            {
                Tool = ClothEditMode.Select,
                HandleType = SelectionHandle.Scale,
                PivotMode = SelectionPivot.LastClicked,
                HandleSpace = HandleOrientation.Normal,
                SelectionFalloffRadius = 0.12f,
                SelectThroughOcclusion = true,
                ShowSelectionWeights = false,
            };
            saved.Save(KeyPrefix);
            ClothEditSettings loaded = ClothEditSettings.Load(KeyPrefix);
            Assert.That(loaded.Tool, Is.EqualTo(ClothEditMode.Select));
            Assert.That(loaded.HandleType, Is.EqualTo(SelectionHandle.Scale));
            Assert.That(loaded.PivotMode, Is.EqualTo(SelectionPivot.LastClicked));
            Assert.That(loaded.HandleSpace, Is.EqualTo(HandleOrientation.Normal));
            Assert.That(loaded.SelectionFalloffRadius, Is.EqualTo(0.12f).Within(1e-6f));
            Assert.That(loaded.SelectThroughOcclusion, Is.True);
            Assert.That(loaded.ShowSelectionWeights, Is.False);

            loaded.ResetToDefaults();
            Assert.That(loaded.Tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(loaded.HandleType, Is.EqualTo(SelectionHandle.Move));
            Assert.That(loaded.PivotMode, Is.EqualTo(SelectionPivot.Center));
            Assert.That(loaded.HandleSpace, Is.EqualTo(HandleOrientation.Global));
            Assert.That(loaded.SelectionFalloffRadius, Is.EqualTo(ClothEditSettings.DefaultSelectionFalloffRadius));
            Assert.That(loaded.SelectThroughOcclusion, Is.False);
            Assert.That(loaded.ShowSelectionWeights, Is.True);

            ClothEditSettings.DeleteSaved(KeyPrefix);
            ClothEditSettings afterDelete = ClothEditSettings.Load(KeyPrefix);
            Assert.That(afterDelete.HandleSpace, Is.EqualTo(HandleOrientation.Global));
            Assert.That(afterDelete.SelectionFalloffRadius, Is.EqualTo(ClothEditSettings.DefaultSelectionFalloffRadius));
        }
    }
}
