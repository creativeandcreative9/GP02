using System.Collections.Generic;
using System.Linq;
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
    /// 「選んで動かす」の画面まわりのうち、画面に依存しない部分：キーの対応、数値入力のワールドへの直し方、表示のキャッシュ、
    /// マウスの下の点、案内とショートカット一覧の文章。シーンビューのイベントとハンドルの描画は GUI のスモークテストで確かめる。
    /// </summary>
    public class ClothEditSelectionUiTests
    {
        const string TempRoot = "Assets/GP02_SelectionUiTestTemp";
        const string Folder = TempRoot + "/Meshes";

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
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // 原点を中心にした、一辺 size（既定 0.6m）の平らな布（法線 -Z が表、カメラは -Z 側）
        Renderer CreateFlat(string name = "SelectionUiFlat", float size = 0.6f)
        {
            int Cells = Mathf.RoundToInt(size / 0.02f);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int y = 0; y <= Cells; y++)
            {
                for (int x = 0; x <= Cells; x++) vertices.Add(new Vector3((x / (float)Cells - 0.5f) * size, (y / (float)Cells - 0.5f) * size, 0f));
            }
            for (int y = 0; y < Cells; y++)
            {
                for (int x = 0; x < Cells; x++)
                {
                    int a = y * (Cells + 1) + x, b = a + 1, c = a + Cells + 1, d = c + 1;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            }
            Mesh mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            GameObject go = Track(new GameObject(name));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _clearUndoOnly.Add(go.GetComponent<MeshFilter>());
            return go.AddComponent<MeshRenderer>();
        }

        ClothEditController StartController(Renderer renderer, ClothEditSettings settings = null)
        {
            settings = settings ?? new ClothEditSettings { SolveMilliseconds = 6f };
            var c = new ClothEditController(settings) { SaveFolder = Folder, DataFolder = Folder, BrushRadius = 0.03f, InfluenceRadius = 0.3f };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(new[] { renderer }, out string error), Is.True, error);
            return c;
        }

        static Ray FrontRay(float x, float y) => new Ray(new Vector3(x, y, -1.5f), Vector3.forward);

        static Vector3 Pos(ClothEditController c, int i)
        {
            NVector3 p = c.Session.Solver.Positions[i];
            return new Vector3(p.X, p.Y, p.Z);
        }

        static Vector3 Mean(ClothEditController c, IReadOnlyList<int> particles)
        {
            Vector3 sum = Vector3.zero;
            foreach (int i in particles) sum += Pos(c, i);
            return sum / particles.Count;
        }

        void Settle(ClothEditController c)
        {
            for (int i = 0; i < 1500 && c.IsBusy; i++)
            {
                _time += 0.016;
                c.Tick(_time);
            }
            Assert.That(c.IsBusy, Is.False, "仕上げが終わりません");
        }

        // ---- キーの対応 ----

        [Test]
        public void Shortcuts_Q_EntersSelectMove_AndPressingAgainReturnsToThePreviousTool()
        {
            foreach (ClothEditMode previous in new[] { ClothEditMode.Grab, ClothEditMode.Rotate, ClothEditMode.Pin, ClothEditMode.Unpin, ClothEditMode.Relax })
            {
                ToolKeyResult enter = ClothEditShortcuts.ResolveToolKey(KeyCode.Q, false, previous, previous, SelectionHandle.Rotate);
                Assert.That(enter.Handled, Is.True);
                Assert.That(enter.Tool, Is.EqualTo(ClothEditMode.Select));
                Assert.That(enter.Handle, Is.EqualTo(SelectionHandle.Rotate), "ハンドルの種類は変えない");

                ToolKeyResult back = ClothEditShortcuts.ResolveToolKey(KeyCode.Q, false, ClothEditMode.Select, previous, SelectionHandle.Scale);
                Assert.That(back.Handled, Is.True);
                Assert.That(back.Tool, Is.EqualTo(previous), "もう一度押すと直前の道具");
                Assert.That(back.Handle, Is.EqualTo(SelectionHandle.Scale));
            }
            // 戻る先が「選んで動かす」自身（記録が無い）ときは、つまむ
            Assert.That(ClothEditShortcuts.ResolveToolKey(KeyCode.Q, false, ClothEditMode.Select, ClothEditMode.Select, SelectionHandle.Move).Tool, Is.EqualTo(ClothEditMode.Grab));
            Assert.That(ClothEditShortcuts.LeaveSelectTool(ClothEditMode.Select), Is.EqualTo(ClothEditMode.Grab));
            Assert.That(ClothEditShortcuts.LeaveSelectTool(ClothEditMode.Pin), Is.EqualTo(ClothEditMode.Pin));
        }

        [Test]
        public void Shortcuts_WithSelectMove_WER_SwitchTheHandleAndKeepTheTool()
        {
            var expected = new Dictionary<KeyCode, SelectionHandle>
            {
                { KeyCode.W, SelectionHandle.Move }, { KeyCode.E, SelectionHandle.Rotate }, { KeyCode.R, SelectionHandle.Scale },
            };
            foreach (KeyValuePair<KeyCode, SelectionHandle> pair in expected)
            {
                ToolKeyResult r = ClothEditShortcuts.ResolveToolKey(pair.Key, false, ClothEditMode.Select, ClothEditMode.Pin, SelectionHandle.Move);
                Assert.That(r.Handled, Is.True, pair.Key.ToString());
                Assert.That(r.Tool, Is.EqualTo(ClothEditMode.Select), "道具はそのまま");
                Assert.That(r.Handle, Is.EqualTo(pair.Value));

                // 「選んで動かす」以外では、今までどおり道具を変える（ハンドルの種類は変えない）
                ToolKeyResult other = ClothEditShortcuts.ResolveToolKey(pair.Key, false, ClothEditMode.Pin, ClothEditMode.Pin, SelectionHandle.Rotate);
                Assert.That(other.Handled, Is.True);
                Assert.That(other.Tool, Is.Not.EqualTo(ClothEditMode.Select));
                Assert.That(other.Handle, Is.EqualTo(SelectionHandle.Rotate));
            }
        }

        [Test]
        public void Shortcuts_P_ShiftP_T_ChangeTheToolEvenInSelectMove_AndShiftOnTheOthersDoesNothing()
        {
            ToolKeyResult pin = ClothEditShortcuts.ResolveToolKey(KeyCode.P, false, ClothEditMode.Select, ClothEditMode.Grab, SelectionHandle.Move);
            Assert.That(pin.Handled && pin.Tool == ClothEditMode.Pin, Is.True);
            ToolKeyResult unpin = ClothEditShortcuts.ResolveToolKey(KeyCode.P, true, ClothEditMode.Select, ClothEditMode.Grab, SelectionHandle.Move);
            Assert.That(unpin.Handled && unpin.Tool == ClothEditMode.Unpin, Is.True);
            ToolKeyResult relax = ClothEditShortcuts.ResolveToolKey(KeyCode.T, false, ClothEditMode.Select, ClothEditMode.Grab, SelectionHandle.Move);
            Assert.That(relax.Handled && relax.Tool == ClothEditMode.Relax, Is.True);

            foreach (ClothEditMode current in new[] { ClothEditMode.Grab, ClothEditMode.Select })
            {
                foreach (KeyCode key in new[] { KeyCode.W, KeyCode.E, KeyCode.R, KeyCode.T, KeyCode.Q })
                {
                    Assert.That(ClothEditShortcuts.ResolveToolKey(key, true, current, ClothEditMode.Grab, SelectionHandle.Move).Handled, Is.False, $"Shift+{key}");
                }
            }
            Assert.That(ClothEditShortcuts.ResolveToolKey(KeyCode.A, false, ClothEditMode.Select, ClothEditMode.Grab, SelectionHandle.Move).Handled, Is.False);
            Assert.That(ClothEditShortcuts.ResolveToolKey(KeyCode.F1, false, ClothEditMode.Grab, ClothEditMode.Grab, SelectionHandle.Move).Handled, Is.False);
        }

        // ---- 数値で動かす ----

        [Test]
        public void Numeric_ToWorld_TurnsTheEnteredValuesIntoWorldMotion()
        {
            ClothEditSelectionNumeric.ToWorld(Quaternion.identity, Vector3.zero, Vector3.zero, new Vector3(100f, 100f, 100f), out Vector3 t, out Quaternion r, out Vector3 s);
            Assert.That(t, Is.EqualTo(Vector3.zero));
            Assert.That(Quaternion.Angle(r, Quaternion.identity), Is.LessThan(1e-3f));
            Assert.That(s, Is.EqualTo(Vector3.one));

            // 単位の向き: cm を m に、% を倍率に
            ClothEditSelectionNumeric.ToWorld(Quaternion.identity, new Vector3(1f, 2f, 3f), new Vector3(0f, 90f, 0f), new Vector3(150f, 100f, 50f), out t, out r, out s);
            Assert.That(Vector3.Distance(t, new Vector3(0.01f, 0.02f, 0.03f)), Is.LessThan(1e-6f));
            Assert.That(Vector3.Distance(r * Vector3.forward, Vector3.right), Is.LessThan(1e-4f), "Y まわりに 90°");
            Assert.That(Vector3.Distance(s, new Vector3(1.5f, 1f, 0.5f)), Is.LessThan(1e-6f));

            // 向きがあるとき: 移動は向きの軸に沿い、回転は向きの軸のまわり
            Quaternion frame = Quaternion.Euler(0f, 90f, 0f);   // 向きの X はワールドの -Z、Z はワールドの X
            ClothEditSelectionNumeric.ToWorld(frame, new Vector3(10f, 0f, 0f), new Vector3(0f, 0f, 90f), Vector3.one * 100f, out t, out r, out s);
            Assert.That(Vector3.Distance(t, new Vector3(0f, 0f, -0.1f)), Is.LessThan(1e-5f), "向きの X 軸に 10cm");
            Vector3 viewAxis = frame * Vector3.forward;   // 向きの Z 軸（ワールドの X）
            Assert.That(Vector3.Distance(r * viewAxis, viewAxis), Is.LessThan(1e-4f), "向きの Z 軸のまわりの回転なので、その軸は動かない");
            Vector3 localX = frame * Vector3.right;
            Assert.That(Vector3.Distance(r * localX, frame * Vector3.up), Is.LessThan(1e-4f), "向きの X 軸が Y 軸の側へ 90°回る");
            Assert.That(Vector3.Distance(r * Vector3.up, frame * (Quaternion.Euler(0f, 0f, 90f) * (Quaternion.Inverse(frame) * Vector3.up))), Is.LessThan(1e-4f));
        }

        [Test]
        public void Numeric_ToWorld_KeepsTheScaleAboveZero_AndIsIdentityDetected()
        {
            ClothEditSelectionNumeric.ToWorld(Quaternion.identity, Vector3.zero, Vector3.zero, new Vector3(0f, -50f, 100f), out _, out _, out Vector3 s);
            Assert.That(s.x, Is.EqualTo(ClothEditSelectionNumeric.MinScalePercent * 0.01f).Within(1e-6f));
            Assert.That(s.y, Is.EqualTo(ClothEditSelectionNumeric.MinScalePercent * 0.01f).Within(1e-6f));
            Assert.That(s.z, Is.EqualTo(1f));

            Vector3 hundred = new Vector3(100f, 100f, 100f);
            Assert.That(ClothEditSelectionNumeric.IsIdentity(Vector3.zero, Vector3.zero, hundred), Is.True);
            Assert.That(ClothEditSelectionNumeric.IsIdentity(new Vector3(0f, 1f, 0f), Vector3.zero, hundred), Is.False);
            Assert.That(ClothEditSelectionNumeric.IsIdentity(Vector3.zero, new Vector3(0f, 0f, 5f), hundred), Is.False);
            Assert.That(ClothEditSelectionNumeric.IsIdentity(Vector3.zero, Vector3.zero, new Vector3(100f, 101f, 100f)), Is.False);
        }

        [Test]
        public void Numeric_ApplyThroughTheController_MovesAlongTheHandleAxes()
        {
            Renderer renderer = CreateFlat("NumericMove");
            var settings = new ClothEditSettings { HandleSpace = HandleOrientation.Normal, SelectionFalloffRadius = 0f, BrushCompliance = 0f, SolveMilliseconds = 6f };
            ClothEditController c = StartController(renderer, settings);
            c.BrushRadius = 0.04f;
            Assert.That(c.SelectPaintBegin(FrontRay(0f, 0f), false), Is.True);
            c.SelectPaintEnd();
            List<int> selected = c.SelectedParticles.ToList();
            Vector3 before = Mean(c, selected);

            // 面の法線の向きの Z 軸に 2cm（布の表は -Z なので、カメラの側へ 2cm）
            ClothEditSelectionNumeric.ToWorld(c.HandleRotation, new Vector3(0f, 0f, 2f), Vector3.zero, new Vector3(100f, 100f, 100f), out Vector3 t, out Quaternion r, out Vector3 s);
            Assert.That(Vector3.Distance(t, new Vector3(0f, 0f, -0.02f)), Is.LessThan(1e-4f));
            Assert.That(c.ApplySelectionTransform(t, r, s), Is.True);
            Settle(c);
            Vector3 moved = Mean(c, selected) - before;
            Assert.That(moved.z, Is.EqualTo(-0.02f).Within(0.002f));
            Assert.That(new Vector2(moved.x, moved.y).magnitude, Is.LessThan(0.002f));
        }

        // ---- 表示のキャッシュ ----

        [Test]
        public void Display_RebuildsOnlyWhenTheSelectionChanges()
        {
            ClothEditController c = StartController(CreateFlat("DisplayCache"));
            var display = new ClothEditSelectionDisplay();
            display.Update(c);
            Assert.That(display.SelectedCount, Is.EqualTo(0));
            int rebuilds = display.RebuildCount;
            for (int i = 0; i < 5; i++) display.Update(c);
            Assert.That(display.RebuildCount, Is.EqualTo(rebuilds), "選択が変わらなければ作り直さない");

            c.BrushRadius = 0.03f;
            Assert.That(c.SelectPaintBegin(FrontRay(0f, 0f), false), Is.True);
            c.SelectPaintEnd();
            display.Update(c);
            Assert.That(display.RebuildCount, Is.GreaterThan(rebuilds));
            Assert.That(display.SelectedCount, Is.EqualTo(c.SelectionCount));
            for (int k = 0; k < display.SelectedCount; k++) Assert.That(c.IsSelected(display.SelectedParticle(k)), Is.True);

            rebuilds = display.RebuildCount;
            for (int i = 0; i < 5; i++) display.Update(c);
            Assert.That(display.RebuildCount, Is.EqualTo(rebuilds));

            // 形が変わっても（動かしても）、番号は同じ。位置は描くときに読む
            Assert.That(c.ApplySelectionTransform(new Vector3(0f, 0f, -0.02f), Quaternion.identity, Vector3.one), Is.True);
            Settle(c);
            display.Update(c);
            Assert.That(display.RebuildCount, Is.EqualTo(rebuilds), "動かしただけでは選択は変わらない");

            c.ClearSelection();
            display.Update(c);
            Assert.That(display.RebuildCount, Is.GreaterThan(rebuilds));
            Assert.That(display.SelectedCount, Is.EqualTo(0));
        }

        [Test]
        public void Display_ThinsOutAHugeSelection()
        {
            ClothEditController c = StartController(CreateFlat("DisplayThin", 1.2f));
            c.SelectAll();
            Assert.That(c.SelectionCount, Is.GreaterThan(ClothEditSelectionDisplay.MaxSelectedDots), "上限を超える数を選んでいる");
            var display = new ClothEditSelectionDisplay();
            display.Update(c);
            Assert.That(display.SelectedCount, Is.GreaterThan(ClothEditSelectionDisplay.MaxSelectedDots / 2).And.LessThanOrEqualTo(ClothEditSelectionDisplay.MaxSelectedDots));
            var seen = new HashSet<int>();
            for (int k = 0; k < display.SelectedCount; k++) Assert.That(seen.Add(display.SelectedParticle(k)), Is.True, "同じ点を2度描かない");
        }

        [Test]
        public void Display_FollowsTheMirrorSideWhenSymmetryChanges_WithoutANewSelection()
        {
            var settings = new ClothEditSettings { SymmetryEnabled = true, SolveMilliseconds = 6f };
            ClothEditController c = StartController(CreateFlat("DisplayMirror"), settings);
            c.BrushRadius = 0.04f;
            Assert.That(c.SelectPaintBegin(FrontRay(0.12f, 0.05f), false), Is.True);
            c.SelectPaintEnd();
            var display = new ClothEditSelectionDisplay();
            display.Update(c);
            Assert.That(display.MirroredCount, Is.GreaterThan(0), "対称がオンなら鏡像の側がある");
            for (int k = 0; k < display.MirroredCount; k++) Assert.That(c.IsSelected(display.MirroredParticle(k)), Is.False);

            int version = c.SelectionVersion;
            c.Settings.SymmetryEnabled = false;
            display.Update(c);
            Assert.That(c.SelectionVersion, Is.EqualTo(version));
            Assert.That(display.MirroredCount, Is.EqualTo(0), "対称をオフにしたら、選択は同じでも鏡像の側は消える");
            c.Settings.SymmetryEnabled = true;
            display.Update(c);
            Assert.That(display.MirroredCount, Is.GreaterThan(0));
        }

        [Test]
        public void Display_CountLabelFollowsTheCountAndTheLanguage()
        {
            ClothEditController c = StartController(CreateFlat("DisplayLabel"));
            var display = new ClothEditSelectionDisplay();
            c.SelectAll();
            display.Update(c);
            Assert.That(display.CountLabel, Does.Contain(c.SelectionCount.ToString("N0")).And.Contain("選択中"));
            string japanese = display.CountLabel;
            L10n.Language = UiLanguage.English;
            display.Update(c);
            Assert.That(display.CountLabel, Does.Contain("selected"));
            Assert.That(display.CountLabel, Is.Not.EqualTo(japanese));
            Assert.That(display.CountLabel, Is.SameAs(display.CountLabel), "同じ文字列を使い回す");
        }

        // ---- マウスの下の点 ----

        [Test]
        public void HoverSelect_ShowsExactlyWhatPaintingWouldSelect_AndOnlyInSelectMove()
        {
            ClothEditController c = StartController(CreateFlat("HoverSelect"));
            c.BrushRadius = 0.03f;
            Assert.That(c.HoverSelectCount, Is.EqualTo(0), "カーソルが布に乗っていない");

            c.Mode = ClothEditMode.Grab;
            Assert.That(c.Hover(FrontRay(0.05f, 0.02f)), Is.True);
            Assert.That(c.HoverSelectCount, Is.EqualTo(0), "選んで動かす以外の道具では出さない");

            c.Mode = ClothEditMode.Select;
            int count = c.HoverSelectCount;
            Assert.That(count, Is.GreaterThan(10));
            var shown = Enumerable.Range(0, count).Select(c.HoverSelectParticle).ToList();

            Assert.That(c.SelectPaintBegin(FrontRay(0.05f, 0.02f), false), Is.True);
            c.SelectPaintEnd();
            Assert.That(shown.OrderBy(i => i).ToList(), Is.EqualTo(c.SelectedParticles.ToList()), "塗ると、見えていた点が選ばれる");

            c.BrushRadius = 0.01f;
            Assert.That(c.HoverSelectCount, Is.LessThan(count), "半径を小さくすると減る");
            c.Hover(FrontRay(-0.1f, -0.1f));
            int moved = c.HoverSelectCount;
            Assert.That(moved, Is.GreaterThan(0));
            Assert.That(Pos(c, c.HoverSelectParticle(0)).x, Is.LessThan(0f), "カーソルについていく");
        }

        [Test]
        public void HoverSelect_ThinsOutAHugeBrush_AndFallsBackToTheNearestParticle()
        {
            ClothEditController c = StartController(CreateFlat("HoverThin"));
            c.Mode = ClothEditMode.Select;
            c.BrushRadius = 0.3f;
            Assert.That(c.Hover(FrontRay(0f, 0f)), Is.True);
            Assert.That(c.HoverSelectCount, Is.GreaterThan(ClothEditController.MaxHoverSelectParticles / 2).And.LessThanOrEqualTo(ClothEditController.MaxHoverSelectParticles));

            c.BrushRadius = 0.0005f;
            Assert.That(c.Hover(FrontRay(0.0033f, 0.0033f)), Is.True);
            Assert.That(c.HoverSelectCount, Is.EqualTo(1), "半径が小さくて粒子が入らないときは、一番近い1つ（塗るときと同じ）");
        }

        // ---- 案内・ショートカット一覧 ----

        [Test]
        public void Help_SelectMove_HasANameKeyTooltipAndColors_InBothLanguages()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                Assert.That(ClothEditHelp.ToolKey(ClothEditMode.Select), Is.EqualTo("Q"));
                Assert.That(ClothEditHelp.ToolName(ClothEditMode.Select), Is.Not.EqualTo(ClothEditHelp.ToolName(ClothEditMode.Grab)));
                Assert.That(ClothEditHelp.ToolTip(ClothEditMode.Select), Does.Contain("Q"));
                foreach (SelectionHandle handle in System.Enum.GetValues(typeof(SelectionHandle)))
                {
                    Assert.That(ClothEditHelp.HandleName(handle), Is.Not.Empty);
                    Assert.That(ClothEditHelp.HandleKey(handle), Is.Not.Empty);
                    Assert.That(ClothEditHelp.HandleTip(handle), Does.Contain(ClothEditHelp.HandleKey(handle)));
                }
            }
            L10n.Language = UiLanguage.Japanese;
            string ja = ClothEditHelp.HandleName(SelectionHandle.Move) + ClothEditHelp.HandleName(SelectionHandle.Rotate) + ClothEditHelp.HandleName(SelectionHandle.Scale);
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditHelp.HandleName(SelectionHandle.Move) + ClothEditHelp.HandleName(SelectionHandle.Rotate) + ClothEditHelp.HandleName(SelectionHandle.Scale), Is.Not.EqualTo(ja));

            Assert.That(ClothEditHelp.SelectBrushColor, Is.Not.EqualTo(ClothEditHelp.SelectedColor), "ブラシの円と選んだ点は違う色");
            Assert.That(ClothEditHelp.MirroredColor.a, Is.LessThan(ClothEditHelp.SelectedColor.a), "鏡像の側は薄い");
            Assert.That(ClothEditHelp.SelectionWeightColor(1f).a, Is.GreaterThan(ClothEditHelp.SelectionWeightColor(0f).a));
        }

        [Test]
        public void Help_SelectMoveHints_ChangeWithTheSelectionAndTheState()
        {
            // 案内は短く（細かい操作はショートカット一覧へ）。選ぶ前は「点を選ぶ」、選んだあとは「ハンドルで動かす」と選んだ数
            string empty = ClothEditHelp.GetHint(ClothEditMode.Select, false, false, 0);
            Assert.That(empty, Does.Contain("選んで動かす").And.Contain("Q").And.Contain("ドラッグで点を選ぶ").And.Contain("ハンドル").And.Contain("F1"));
            string withSelection = ClothEditHelp.GetHint(ClothEditMode.Select, false, false, 1234);
            Assert.That(withSelection, Does.Contain("1,234").And.Contain("ハンドル").And.Contain("F1"));
            Assert.That(withSelection, Is.Not.EqualTo(empty));
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Select, true, false, 10), Does.Contain("動かして").And.Not.Contain("つまんで").And.Contain("Esc"));
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Select, false, true, 10), Does.Contain("仕上げ"));
            L10n.Language = UiLanguage.English;
            Assert.That(ClothEditHelp.GetHint(ClothEditMode.Select, false, false, 3), Does.Contain("Select & Move").And.Contain("selected"));
        }

        [Test]
        public void Help_ShortcutList_ExplainsQAndTheSelectionOperations()
        {
            foreach (UiLanguage language in new[] { UiLanguage.Japanese, UiLanguage.English })
            {
                L10n.Language = language;
                var lines = new List<KeyValuePair<string, string>>();
                ClothEditHelp.GetShortcutLines(lines);
                Assert.That(lines.Exists(l => l.Key == "Q"), Is.True, "Q");
                Assert.That(lines.Count(l => l.Key.Contains("Shift") && (l.Value.Contains("四角") || l.Value.Contains("box"))), Is.GreaterThanOrEqualTo(2), "ピン留めと選ぶの、四角で囲む操作");
                Assert.That(lines.Exists(l => l.Key.Contains("ダブルクリック") || l.Key.Contains("Double-click")), Is.True);
                Assert.That(lines.Exists(l => l.Key.Contains("ハンドル") || l.Key.Contains("handle")), Is.True);
                Assert.That(lines.Find(l => l.Key == "W").Value, Does.Contain(language == UiLanguage.Japanese ? "ハンドル" : "handle"));
                Assert.That(lines.Select(l => l.Key + "|" + l.Value).Distinct().Count(), Is.EqualTo(lines.Count));
            }
        }

        [Test]
        public void Settings_SelectTool_IsSavedAndRestored()
        {
            const string Prefix = "GP02.Test.SelectionUiSettings.";
            try
            {
                var settings = new ClothEditSettings { Tool = ClothEditMode.Select, HandleType = SelectionHandle.Scale };
                settings.Save(Prefix);
                ClothEditSettings loaded = ClothEditSettings.Load(Prefix);
                Assert.That(loaded.Tool, Is.EqualTo(ClothEditMode.Select));
                Assert.That(loaded.HandleType, Is.EqualTo(SelectionHandle.Scale));
            }
            finally
            {
                ClothEditSettings.DeleteSaved(Prefix);
            }
        }

        // ---- シーンビューの道具バー ----

        [Test]
        public void Overlay_SelectMoveButtonAndTheHandleToggles_FollowAndChangeTheSettings()
        {
            var window = ScriptableObject.CreateInstance<ClothEditWindow>();
            ClothEditSettings settings = window.OverlaySettings;
            ClothEditMode savedTool = settings.Tool;
            SelectionHandle savedHandle = settings.HandleType;
            try
            {
                settings.Tool = ClothEditMode.Rotate;
                settings.HandleType = SelectionHandle.Move;
                var overlay = new ClothEditOverlay();
                UnityEngine.UIElements.VisualElement root = overlay.CreatePanelContent();
                var toggles = root.Query<UnityEditor.UIElements.ToolbarToggle>().ToList();
                // 並び: つまむ・選んで動かす・ピン留め・元に戻すブラシ・回す・拡大縮小、ハンドル3つ、塗ると外す、対称・伸び・元の形、ショートカット
                UnityEditor.UIElements.ToolbarToggle select = toggles[1];
                UnityEngine.UIElements.VisualElement handleRow = toggles[6].parent;

                Assert.That(select.text, Is.EqualTo(ClothEditHelp.ToolName(ClothEditMode.Select)));
                Assert.That(select.tooltip, Is.EqualTo(ClothEditHelp.ToolTip(ClothEditMode.Select)));
                Assert.That(select.value, Is.False);
                Assert.That(handleRow.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None), "選んで動かす以外では、ハンドルの切り替えは出さない");

                // 入れると今の道具を覚え、ハンドルの切り替えが出る
                window.OverlaySetSelectTool(true);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Select));
                overlay.Refresh();
                Assert.That(select.value, Is.True);
                Assert.That(toggles[0].value, Is.False, "ほかの道具は選ばれていない");
                Assert.That(handleRow.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));
                var handles = new[] { SelectionHandle.Move, SelectionHandle.Rotate, SelectionHandle.Scale };
                for (int i = 0; i < handles.Length; i++)
                {
                    Assert.That(toggles[6 + i].text, Is.EqualTo(ClothEditHelp.HandleName(handles[i])));
                    Assert.That(toggles[6 + i].tooltip, Is.EqualTo(ClothEditHelp.HandleTip(handles[i])));
                    Assert.That(toggles[6 + i].value, Is.EqualTo(handles[i] == SelectionHandle.Move));
                }

                // ハンドルの種類は、設定を変え、キー（W/E/R）で変えたときも追従する
                window.OverlaySetHandle(SelectionHandle.Rotate);
                Assert.That(settings.HandleType, Is.EqualTo(SelectionHandle.Rotate));
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Select), "道具は変わらない");
                settings.HandleType = SelectionHandle.Scale;
                overlay.Refresh();
                Assert.That(toggles[8].value, Is.True);
                Assert.That(toggles[7].value, Is.False);

                // もう一度押すと、直前の道具へ戻る
                window.OverlaySetSelectTool(false);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Rotate));
                overlay.Refresh();
                Assert.That(select.value, Is.False);
                Assert.That(handleRow.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None));

                // ほかの道具を選んでから入れ直すと、その道具に戻る
                window.OverlaySetTool(ClothEditMode.Pin);
                window.OverlaySetSelectTool(true);
                window.OverlaySetSelectTool(false);
                Assert.That(settings.Tool, Is.EqualTo(ClothEditMode.Pin));

                L10n.Language = UiLanguage.English;
                overlay.Refresh();
                Assert.That(select.text, Is.EqualTo("Select & Move"));
                Assert.That(toggles[6].text, Is.EqualTo("Move"));
            }
            finally
            {
                settings.Tool = savedTool;
                settings.HandleType = savedHandle;
                settings.Save();
                Object.DestroyImmediate(window);
            }
        }

        // ---- 説明書 ----

        [Test]
        public void Manuals_ExplainSelectMove()
        {
            string ja = System.IO.File.ReadAllText(ClothEditDocs.FindDocument(UiLanguage.Japanese));
            string en = System.IO.File.ReadAllText(ClothEditDocs.FindDocument(UiLanguage.English));
            foreach (string word in new[] { "選んで動かす", "布として動かす", "形だけ動かす", "なめらかさ", "数値で動かす", "ダブルクリック", "ハンドル", "1cm" })
            {
                Assert.That(ja, Does.Contain(word), $"日本語の説明書に「{word}」が無い");
            }
            foreach (string word in new[] { "Select & Move", "As cloth", "Shape only", "Smoothness", "Move by Numbers", "double-click", "handle", "1 cm" })
            {
                Assert.That(en, Does.Contain(word), $"英語の説明書に「{word}」が無い");
            }
        }
    }
}
