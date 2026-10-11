using System;
using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// シェイプキーのデータと布の編集のつなぎ（<see cref="ClothEditController.LoadDeltasAsOperation(Vector3[])"/>、
    /// <see cref="ClothEditController.SaveToData"/>、<see cref="ShapeKeyClothEdit"/>）の往復を確かめる。
    /// </summary>
    public class ShapeKeyClothEditTests : ShapeKeyUiTestBase
    {
        const float RoundTrip = 0.004f;

        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        Func<ClothEditController> _savedProvider;
        Action _savedOpenWindow;
        bool _gp02FolderExisted;

        [SetUp]
        public void SetUp()
        {
            _savedProvider = ShapeKeyClothEdit.ControllerProvider;
            _savedOpenWindow = ShapeKeyClothEdit.OpenWindow;
            ShapeKeyClothEdit.ControllerProvider = null;
            ShapeKeyClothEdit.OpenWindow = () => { };
            ShapeKeyClothEdit.Clear();
            _gp02FolderExisted = AssetDatabase.IsValidFolder("Assets/GP02");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ClothEditController c in _controllers) c.Dispose();
            _controllers.Clear();
            ShapeKeyClothEdit.ControllerProvider = _savedProvider;
            ShapeKeyClothEdit.OpenWindow = _savedOpenWindow;
            ShapeKeyClothEdit.Clear();
            if (!_gp02FolderExisted) AssetDatabase.DeleteAsset("Assets/GP02");
        }

        ClothEditController Start(Renderer renderer)
        {
            var controller = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 4f });
            _controllers.Add(controller);
            Assert.That(controller.TryStart(renderer, out string error), Is.True, error);
            return controller;
        }

        // 中央のなめらかな盛り上がり
        static Vector3[] Bump(Mesh mesh, float height = -0.04f)
        {
            Vector3[] vertices = mesh.vertices;
            var bump = new Vector3[vertices.Length];
            for (int v = 0; v < bump.Length; v++)
            {
                Vector2 p = new Vector2(vertices[v].x, vertices[v].y) - new Vector2(Size / 2f, Size / 2f);
                bump[v] = new Vector3(0f, 0f, height * Mathf.Exp(-p.sqrMagnitude / 0.01f));
            }
            return bump;
        }

        static void AssertShape(ClothEditController c, Vector3[] rest, Vector3[] deltas, float tolerance, string what)
        {
            Vector3[] shown = c.Session.CurrentWorldVertices;
            for (int v = 0; v < rest.Length; v++)
            {
                Assert.That(Vector3.Distance(shown[v], rest[v] + deltas[v]), Is.LessThan(tolerance), $"{what} 頂点 {v}");
            }
        }

        // ---- LoadDeltasAsOperation ----

        [TestCase(false)]
        [TestCase(true)]
        public void LoadDeltasAsOperation_ShowsTheShape_AndUndoGoesBackToTheStart(bool skinned)
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, skinned);
            ClothEditController c = Start(renderer);
            Vector3[] rest = mesh.vertices;
            Vector3[] bump = Bump(mesh);

            NextUndoGroup();
            Assert.That(c.LoadDeltasAsOperation(bump), Is.True);
            NextUndoGroup();
            AssertShape(c, rest, bump, 0.002f, "読み込み");

            c.UndoLast();
            AssertShape(c, rest, new Vector3[rest.Length], 1e-4f, "Undo");

            Undo.PerformRedo();
            AssertShape(c, rest, bump, 0.002f, "Redo");
        }

        [Test]
        public void LoadDeltasAsOperation_RejectsWrongSizesAndWhenNotEditing()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            var idle = new ClothEditController(new ClothEditSettings());
            _controllers.Add(idle);
            Assert.That(idle.LoadDeltasAsOperation(Bump(mesh)), Is.False, "編集中でない");

            ClothEditController c = Start(renderer);
            Assert.That(c.LoadDeltasAsOperation(new Vector3[3]), Is.False);
            Assert.That(c.LoadDeltasAsOperation(null), Is.False);
        }

        [Test]
        public void LoadDeltasAsOperation_WithNothingToChangeLeavesNoUndoStep()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            ClothEditController c = Start(renderer);
            Vector3[] rest = mesh.vertices;

            NextUndoGroup();
            c.LoadDeltasAsOperation(new Vector3[mesh.vertexCount]);
            NextUndoGroup();

            AssertShape(c, rest, new Vector3[rest.Length], 1e-4f, "形は変わらない");
        }

        // ---- SaveToData ----

        [Test]
        public void SaveToData_StoresTheCurrentShapeAsAnEntry_AndLoadingItAgainReproducesTheShape()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            GP02ShapeKeyData data = NewData(mesh);
            ClothEditController c = Start(renderer);
            Vector3[] rest = mesh.vertices;
            Vector3[] bump = Bump(mesh);
            c.LoadDeltasAsOperation(bump);

            NextUndoGroup();
            ShapeKeyEntry saved = c.SaveToData(data, "  Bump  ", false);
            NextUndoGroup();

            Assert.That(saved, Is.Not.Null, c.LastError);
            Assert.That(data.Entries, Is.EqualTo(new[] { saved }));
            Assert.That(saved.Name, Is.EqualTo("Bump"), "名前の前後の空白は取る");
            Assert.That(saved.Frames.Count, Is.EqualTo(1));
            Assert.That(saved.Frames[0].Weight, Is.EqualTo(100f));
            Assert.That(saved.Frames[0].HasNormals, Is.True, "法線の差分も入る");
            AssertSame(bump, saved.Frames[0].DeltaVertices, RoundTrip);

            // 編集をやり直してから、保存した差分を読み込むと同じ形になる
            c.ResetAll();
            AssertShape(c, rest, new Vector3[rest.Length], 1e-4f, "リセット");
            Assert.That(c.LoadDeltasAsOperation(saved.Frames[0].DeltaVertices), Is.True);
            AssertShape(c, rest, bump, 0.003f, "往復");

            Undo.PerformUndo();      // 読み込みの Undo は、リセット後の形
            Undo.PerformUndo();      // リセットの Undo は、読み込んだ形
            AssertShape(c, rest, bump, 0.003f, "リセットの Undo");
        }

        [Test]
        public void SaveToData_UndoRemovesTheNewEntry_AndRedoBringsItBack()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            GP02ShapeKeyData data = NewData(mesh);
            ClothEditController c = Start(renderer);
            c.LoadDeltasAsOperation(Bump(mesh));

            NextUndoGroup();
            Assert.That(c.SaveToData(data, "Bump", false), Is.Not.Null);
            NextUndoGroup();
            Assert.That(data.Entries.Count, Is.EqualTo(1));

            Undo.PerformUndo();
            Assert.That(data.Entries.Count, Is.EqualTo(0));
            Undo.PerformRedo();
            Assert.That(data.Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void SaveToData_OverwriteReplacesTheSameName_OtherwiseAddsANumberedName()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            GP02ShapeKeyData data = NewData(mesh);
            ClothEditController c = Start(renderer);
            Vector3[] bump = Bump(mesh);
            c.LoadDeltasAsOperation(bump);
            ShapeKeyEntry first = c.SaveToData(data, "Bump", true);
            first.Group = "Face";
            first.Enabled = false;

            // 形を変えて、同じ名前で上書き
            c.ResetAll();
            Vector3[] smaller = Bump(mesh, -0.02f);
            c.LoadDeltasAsOperation(smaller);
            NextUndoGroup();
            ShapeKeyEntry second = c.SaveToData(data, "Bump", true);
            NextUndoGroup();
            Assert.That(second, Is.SameAs(first), "同じエントリを上書きする");
            Assert.That(data.Entries.Count, Is.EqualTo(1));
            Assert.That(second.Group, Is.EqualTo("Face"), "グループなどは保つ");
            Assert.That(second.Enabled, Is.False);
            AssertSame(smaller, second.Frames[0].DeltaVertices, RoundTrip);

            // 上書きしない保存は別名で追加
            ShapeKeyEntry third = c.SaveToData(data, "Bump", false);
            Assert.That(third.Name, Is.EqualTo("Bump (1)"));
            Assert.That(data.Entries.Count, Is.EqualTo(2));

            // 上書きでも、名前が無ければ追加
            ShapeKeyEntry added = c.SaveToData(data, "Other", true);
            Assert.That(added, Is.Not.SameAs(first));
            Assert.That(data.Entries.Count, Is.EqualTo(3));

            Undo.PerformUndo();
            Undo.PerformUndo();
            Undo.PerformUndo();
            AssertSame(bump, data.Entries[0].Frames[0].DeltaVertices, RoundTrip, "上書きの Undo");
        }

        [Test]
        public void SaveToData_RefusesAnEmptyNameAWrongVertexCountAndWhenNotEditing()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            GP02ShapeKeyData data = NewData(mesh);
            ClothEditController c = Start(renderer);

            Assert.That(c.SaveToData(data, "  ", false), Is.Null);
            Assert.That(c.LastError, Is.Not.Empty);

            data.VertexCount = 5;
            Assert.That(c.SaveToData(data, "A", false), Is.Null);
            Assert.That(c.LastError, Does.Contain("5"));
            Assert.That(data.Entries, Is.Empty);

            var idle = new ClothEditController(new ClothEditSettings());
            _controllers.Add(idle);
            data.VertexCount = mesh.vertexCount;
            Assert.That(idle.SaveToData(data, "A", false), Is.Null);
            Assert.Throws<ArgumentNullException>(() => c.SaveToData(null, "A", false));
        }

        [Test]
        public void SaveToData_StoresPinnedVertices_AndLoadingThemRestoresThePins()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, false);
            GP02ShapeKeyData data = NewData(mesh);
            ClothEditController c = Start(renderer);
            var pinCenter = new Vector3(0.04f, 0.04f, 0f);
            Assert.That(c.Session.PaintPins(pinCenter, 0.05f, true), Is.GreaterThan(0));
            Vector3[] bump = Bump(mesh);
            c.LoadDeltasAsOperation(bump);

            ShapeKeyEntry saved = c.SaveToData(data, "Pinned", false);

            Assert.That(saved.PinnedVertices.Length, Is.GreaterThanOrEqualTo(3));
            Vector3[] rest = mesh.vertices;
            foreach (int v in saved.PinnedVertices)
            {
                Assert.That(Vector3.Distance(new Vector3(rest[v].x, rest[v].y, 0f), pinCenter), Is.LessThan(0.05f + 0.0075f + 0.002f), $"頂点 {v}");
            }
            // 塗った範囲の中心に近い頂点（(0.04, 0.04) の周り）が入っている
            bool nearCenter = false;
            foreach (int v in saved.PinnedVertices)
            {
                if (v / Stride >= 1 && v / Stride <= 3 && v % Stride >= 1 && v % Stride <= 3) nearCenter = true;
            }
            Assert.That(nearCenter, Is.True);

            // 新しい編集に読み込むと、ピンも戻る。同じ Undo の1段で戻る
            ClothEditController fresh = Start(renderer);
            Assert.That(fresh.Session.PinnedCount, Is.EqualTo(0));
            NextUndoGroup();
            Assert.That(fresh.LoadDeltasAsOperation(saved.Frames[0].DeltaVertices, saved.PinnedVertices), Is.True);
            NextUndoGroup();
            Assert.That(fresh.Session.PinnedCount, Is.GreaterThan(0));

            // ピン無しで読み込む版は、今のピンを変えない
            int pinned = fresh.Session.PinnedCount;
            fresh.LoadDeltasAsOperation(saved.Frames[0].DeltaVertices);
            Assert.That(fresh.Session.PinnedCount, Is.EqualTo(pinned));

            fresh.UndoLast();
            Assert.That(fresh.Session.PinnedCount, Is.EqualTo(0), "ピンも Undo で戻る");
        }

        // ---- FindOrCreateData ----

        [Test]
        public void FindOrCreateData_CreatesTheComponentAndDataOnce_AndFindsThemAfterwards()
        {
            Mesh mesh = CreateGridMesh("FindMe");
            Renderer renderer = CreateRenderer(mesh, true);
            var c = new ClothEditController(new ClothEditSettings());
            _controllers.Add(c);
            Assert.That(renderer.GetComponent<GP02ShapeKeys>(), Is.Null);

            GP02ShapeKeyData data = c.FindOrCreateData(renderer);
            try
            {
                Assert.That(data, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(data), Does.StartWith(ShapeKeyLibrary.DataFolder));
                Assert.That(data.SourceMesh, Is.SameAs(mesh));
                Assert.That(data.VertexCount, Is.EqualTo(mesh.vertexCount));
                var component = renderer.GetComponent<GP02ShapeKeys>();
                Assert.That(component, Is.Not.Null);
                Assert.That(component.Data, Is.SameAs(data));
                Assert.That(component.SourceMesh, Is.SameAs(mesh));

                Assert.That(c.FindOrCreateData(renderer), Is.SameAs(data), "2回目は同じデータ");
                Assert.That(renderer.GetComponents<GP02ShapeKeys>().Length, Is.EqualTo(1));
                Assert.That(AssetDatabase.FindAssets("t:GP02ShapeKeyData", new[] { ShapeKeyLibrary.DataFolder }).Length, Is.EqualTo(1));
            }
            finally
            {
                if (data != null) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(data));
            }
        }

        [Test]
        public void FindOrCreateData_ReusesTheSourceMeshOfAnExistingComponentWithoutData()
        {
            Mesh source = CreateGridMesh("Original");
            Mesh shown = CreateGridMesh("Shown");
            Renderer renderer = CreateRenderer(shown, true);
            GP02ShapeKeys component = renderer.gameObject.AddComponent<GP02ShapeKeys>();
            component.SourceMesh = source;
            var c = new ClothEditController(new ClothEditSettings());
            _controllers.Add(c);

            GP02ShapeKeyData data = c.FindOrCreateData(renderer);
            try
            {
                Assert.That(data.SourceMesh, Is.SameAs(source), "コンポーネントが覚えている元のメッシュを使う");
                Assert.That(component.Data, Is.SameAs(data));
                Assert.That(renderer.GetComponents<GP02ShapeKeys>().Length, Is.EqualTo(1));
            }
            finally
            {
                if (data != null) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(data));
            }
        }

        [Test]
        public void FindOrCreateData_ReturnsNullWithoutAMesh()
        {
            Renderer renderer = CreateRenderer(null, true);
            var c = new ClothEditController(new ClothEditSettings());
            _controllers.Add(c);
            Assert.That(c.FindOrCreateData(renderer), Is.Null);
            Assert.Throws<ArgumentNullException>(() => c.FindOrCreateData(null));
        }

        // ---- 一覧からの「布で編集する」 ----

        [Test]
        public void Begin_StartsTheEditEndsThePreviewAndLoadsTheLastFrame_ThenSaveActiveOverwrites()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, true);
            Vector3[] bump = Bump(mesh);
            ShapeKeyEntry entry = Entry("Bump", bump);
            // 最後のフレームが読み込まれる
            entry.Frames.Insert(0, new ShapeKeyFrame { Weight = 50f, DeltaVertices = Bump(mesh, -0.01f) });
            entry.PreviewWeight = 100f;
            GP02ShapeKeyData data = NewData(mesh, entry);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            Assert.That(ShapeKeyPreview.Apply(renderer, data, out _), Is.True);
            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.True);

            var c = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 4f });
            _controllers.Add(c);
            Assert.That(ShapeKeyClothEdit.Begin(c, component, entry, out string message), Is.True, message);

            Assert.That(ShapeKeyPreview.IsActive(renderer), Is.False, "プレビューの一時メッシュのまま編集しない");
            Assert.That(ClothEditController.GetMesh(renderer), Is.SameAs(mesh));
            Assert.That(c.IsEditing, Is.True);
            AssertShape(c, mesh.vertices, bump, 0.002f, "最後のフレーム");
            Assert.That(ShapeKeyClothEdit.ActiveComponent, Is.SameAs(component));
            Assert.That(ShapeKeyClothEdit.ActiveEntryName, Is.EqualTo("Bump"));

            // 直して上書き保存
            c.ResetAll();
            Vector3[] smaller = Bump(mesh, -0.02f);
            c.LoadDeltasAsOperation(smaller);
            ShapeKeyEntry saved = ShapeKeyClothEdit.SaveActive(c, true);
            Assert.That(saved, Is.SameAs(entry));
            Assert.That(data.Entries.Count, Is.EqualTo(1));
            Assert.That(saved.Frames.Count, Is.EqualTo(1), "上書きすると1フレームになる");
            AssertSame(smaller, saved.Frames[0].DeltaVertices, RoundTrip);

            // 別名で追加
            ShapeKeyEntry copy = ShapeKeyClothEdit.SaveActive(c, false, "Bump2");
            Assert.That(copy.Name, Is.EqualTo("Bump2"));
            Assert.That(data.Entries.Count, Is.EqualTo(2));
        }

        [Test]
        public void Begin_RefusesEntriesAndMeshesThatDoNotMatch()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, true);
            ShapeKeyEntry entry = Entry("Bump", Bump(mesh));
            GP02ShapeKeyData data = NewData(mesh, entry);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);
            var c = new ClothEditController(new ClothEditSettings());
            _controllers.Add(c);

            Assert.That(ShapeKeyClothEdit.Begin(c, component, null, out string message), Is.False);
            Assert.That(message, Is.Not.Empty);
            Assert.That(ShapeKeyClothEdit.Begin(c, component, Entry("Bad", new Vector3[4]), out message), Is.False);
            ((SkinnedMeshRenderer)renderer).sharedMesh = Track(new Mesh { name = "Other", vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
            Assert.That(ShapeKeyClothEdit.Begin(c, component, entry, out message), Is.False);
            Assert.That(c.IsEditing, Is.False);
            Assert.That(ShapeKeyClothEdit.ActiveComponent, Is.Null);
        }

        [Test]
        public void Request_NeedsTheWindowsControllerAndSaysSoOtherwise()
        {
            Mesh mesh = CreateGridMesh();
            Renderer renderer = CreateRenderer(mesh, true);
            ShapeKeyEntry entry = Entry("Bump", Bump(mesh));
            GP02ShapeKeyData data = NewData(mesh, entry);
            GP02ShapeKeys component = ShapeKeyLibrary.Attach(renderer, data);

            Assert.That(ShapeKeyClothEdit.Request(component, entry, out string message), Is.False);
            Assert.That(message, Is.Not.Empty);

            var c = new ClothEditController(new ClothEditSettings { SolveMilliseconds = 4f });
            _controllers.Add(c);
            ShapeKeyClothEdit.ControllerProvider = () => c;
            Assert.That(ShapeKeyClothEdit.Request(component, entry, out message), Is.True, message);
            Assert.That(c.IsEditing, Is.True);
        }
    }
}
