using System;
using System.Collections.Generic;
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
    /// 保存（シェイプキーとして保存し、そのメッシュで編集を作り直す）をはさんだ Ctrl+Z / Ctrl+Y の流れ。
    /// 保存の Ctrl+Z は保存を戻し（元のメッシュで作り直す。形は元のメッシュの形に戻る）、保存の前の操作は、保存と一緒に戻ったものとして扱う
    /// （保存のときに、その記録は残さない）。保存のあとの操作は1つずつ戻せる。
    /// 作り直しで粒子の並びが変わったときは、前の粒子の位置の記録を引き継がない（引き継ぐと、やり直しや次の Ctrl+Z で形が戻らない）。
    /// </summary>
    public class SaveUndoHistoryTests
    {
        const string TempFolder = "Assets/GP02_SaveUndoHistoryTestTemp";

        readonly List<Object> _created = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        double _time = 100.0;
        UiLanguage _savedLanguage;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = L10n.Language;
            L10n.Language = UiLanguage.Japanese;
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
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

        // 編集とは関係のない、Unity の別の操作（名前の変更）。編集を始める前に1段積んでおき、Ctrl+Z がそこまで戻っていないかを見る
        GameObject RenameMarker()
        {
            var marker = new GameObject("Marker");
            _created.Add(marker);
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(marker, "名前を変える");
            marker.name = "Renamed";
            Undo.IncrementCurrentGroup();
            return marker;
        }

        ClothEditController Start(Renderer target)
        {
            var c = new ClothEditController(new ClothEditSettings())
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

        // 脚の太ももの前をつまんで、横へ引いて、仕上げる（side = -1 で左脚、1 で右脚）
        void PullThigh(ClothEditController c, int side, float distance = 0.04f)
        {
            c.Mode = ClothEditMode.Grab;
            float x = side * TestAvatarMenu.LegX;
            const float y = 0.65f;
            Assert.That(c.PointerDown(new Ray(new Vector3(x, y, 1f), Vector3.back), Vector3.back), Is.True, "布に当たらない");
            const int steps = 10;
            for (int k = 1; k <= steps; k++)
            {
                c.PointerDrag(new Ray(new Vector3(x + side * distance * k / steps, y, 1f), Vector3.back));
                c.Tick(NextTime());
            }
            c.PointerUp();
            for (int i = 0; i < 1500 && c.IsBusy; i++) c.Tick(NextTime());
            Assert.That(c.IsBusy, Is.False, "仕上げが終わりません");
        }

        static NVector3[] Positions(ClothEditSession session)
        {
            ReadOnlySpan<NVector3> positions = session.Solver.Positions;
            var copy = new NVector3[positions.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = positions[i];
            return copy;
        }

        static float MaxDistance(NVector3[] a, NVector3[] b)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length), "粒子の数が違う");
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, NVector3.Distance(a[i], b[i]));
            return max;
        }

        static float MaxDistance(Vector3[] a, Vector3[] b)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length), "頂点の数が違う");
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i]));
            return max;
        }

        // 操作1 → 保存 → 操作2 まで進める。保存した直後の形（ワールドの頂点）を返す
        Vector3[] PullSavePull(ClothEditController c, SkinnedMeshRenderer tights, out Mesh original, out Mesh saved)
        {
            original = tights.sharedMesh;
            PullThigh(c, -1);
            Assert.That(c.HasUnsavedEdits, Is.True);
            Assert.That(c.Save("GP02_SaveUndo", out string message), Is.True, message);
            saved = tights.sharedMesh;
            Assert.That(saved, Is.Not.SameAs(original), "保存で、シェイプキーを入れたメッシュに差し替わる");
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.HasUnsavedEdits, Is.False);
            Vector3[] savedShape = (Vector3[])c.Session.CurrentWorldVertices.Clone();

            PullThigh(c, 1);
            Assert.That(c.HasUnsavedEdits, Is.True, "保存のあとの操作で、形が変わっていない");
            return savedShape;
        }

        // ---- 保存 → 操作 → Ctrl+Z → Ctrl+Z ----

        [Test]
        public void SaveThenPull_CtrlZTwice_GoesBackToTheSavedShapeThenUndoesTheSave_WithoutTouchingOtherOperations()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer tights = Find(root, TestAvatarMenu.TightsName);
            GameObject marker = RenameMarker();
            ClothEditController c = Start(tights);
            Vector3[] originalShape = (Vector3[])c.Session.CurrentWorldVertices.Clone();
            Vector3[] savedShape = PullSavePull(c, tights, out Mesh original, out Mesh saved);

            // Ctrl+Z の1回目: 保存のあとの操作が戻り、保存した形になる（保存済み）
            c.UndoLast();
            Assert.That(c.IsEditing, Is.True, "Ctrl+Z の1回目で、編集が終わった");
            Assert.That(tights.sharedMesh, Is.SameAs(saved), "Ctrl+Z の1回目で、保存まで戻った");
            Assert.That(c.HasUnsavedEdits, Is.False, "保存した形に戻っていない");
            float back = MaxDistance(savedShape, c.Session.CurrentWorldVertices);
            Debug.Log($"[GP02計測] 保存のあとの操作を Ctrl+Z で戻したときの、保存した形との差の最大 {back * 1000f:F3} mm");
            Assert.That(back, Is.LessThan(1e-4f), "Ctrl+Z の1回目で、保存した形に戻らない");

            // Ctrl+Z の2回目: 保存が戻る（元のメッシュ、シェイプキーの重みも戻る）。編集は元のメッシュで作り直され、形は元のメッシュの形
            c.UndoLast();
            Assert.That(tights.sharedMesh, Is.SameAs(original), "Ctrl+Z の2回目で、保存が戻らない");
            Assert.That(c.IsEditing, Is.True, "保存を戻したら、編集が終わった");
            Assert.That(c.Session.GetSourceMesh(0), Is.SameAs(original), "元のメッシュで作り直していない");
            Assert.That(root != null && tights != null, Is.True, "アバターを作ったことまで戻った");
            Assert.That(marker.name, Is.EqualTo("Renamed"), "編集と関係のない操作まで戻った");
            Assert.That(c.HasUnsavedEdits, Is.False);
            Assert.That(MaxDistance(originalShape, c.Session.CurrentWorldVertices), Is.LessThan(1e-4f), "保存を戻した形が、元のメッシュの形ではない");

            // Ctrl+Z の3回目: 保存の前の操作は、保存と一緒に戻っているので、その前の Unity の操作（名前の変更）が戻る。編集は続く
            c.UndoLast();
            Assert.That(marker.name, Is.EqualTo("Marker"), "保存を戻したあとの Ctrl+Z が、その前の操作に届かない（何も起きない段がある）");
            Assert.That(c.IsEditing, Is.True);
            Assert.That(tights.sharedMesh, Is.SameAs(original));
            Assert.That(MaxDistance(originalShape, c.Session.CurrentWorldVertices), Is.LessThan(1e-4f), "名前の変更を戻したら、形が変わった");
        }

        // ---- 保存を戻して、やり直す ----

        [Test]
        public void UndoTheSaveThenRedo_RebuildsOnTheSavedMesh_AndTheNextOperationCanBeUndone()
        {
            GameObject root = CreateAvatar();
            SkinnedMeshRenderer tights = Find(root, TestAvatarMenu.TightsName);
            GameObject marker = RenameMarker();
            ClothEditController c = Start(tights);
            Vector3[] savedShape = PullSavePull(c, tights, out Mesh original, out Mesh saved);
            c.UndoLast();
            c.UndoLast();
            Assert.That(tights.sharedMesh, Is.SameAs(original));
            Assert.That(c.IsEditing, Is.True);

            // Ctrl+Y の1回目: 保存がやり直され、保存したメッシュで作り直す（粒子は保存した形の上に置き直す）。形は保存した形で、保存済み
            c.RedoLast();
            Assert.That(tights.sharedMesh, Is.SameAs(saved), "保存がやり直されない");
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.GetSourceMesh(0), Is.SameAs(saved));
            Assert.That(c.HasUnsavedEdits, Is.False);
            Assert.That(MaxDistance(savedShape, c.Session.CurrentWorldVertices), Is.LessThan(1e-4f), "やり直した形が、保存した形ではない");
            Vector3[] redone = (Vector3[])c.Session.CurrentWorldVertices.Clone();

            // Ctrl+Y の2回目: 保存のあとの操作は、粒子を置き直したのでやり直せない。形は崩れない（前の粒子の並びの記録を当てはめない）
            c.RedoLast();
            Assert.That(c.IsEditing, Is.True);
            float afterSecondRedo = MaxDistance(redone, c.Session.CurrentWorldVertices);
            Debug.Log($"[GP02計測] 保存をやり直したあとの Ctrl+Y で動いた頂点の最大 {afterSecondRedo * 1000f:F3} mm");
            Assert.That(afterSecondRedo, Is.LessThan(1e-4f), "粒子を置き直した布に、前の粒子の並びの記録が当てはめられた");

            // 次の操作は、Ctrl+Z で戻せる
            NVector3[] beforeNext = Positions(c.Session);
            PullThigh(c, 1, 0.03f);
            Assert.That(MaxDistance(beforeNext, Positions(c.Session)), Is.GreaterThan(0.005f), "次の操作で形が変わっていない");
            c.UndoLast();
            Assert.That(c.IsEditing, Is.True);
            float undone = MaxDistance(beforeNext, Positions(c.Session));
            Debug.Log($"[GP02計測] 保存をやり直したあとの操作を Ctrl+Z で戻したときの差の最大 {undone * 1000f:F3} mm");
            Assert.That(undone, Is.LessThan(1e-4f), "保存をやり直したあとの操作が、Ctrl+Z で戻らない");
            Assert.That(tights.sharedMesh, Is.SameAs(saved), "次の操作の Ctrl+Z で、保存まで戻った");
            Assert.That(marker.name, Is.EqualTo("Renamed"));
        }
    }
}
