using System;
using System.Collections.Generic;
using System.Diagnostics;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 貫通（体が布を突き抜けている所）の検出と「貫通を直す」を、テスト用の衣装（体のシェイプキー Hip_Big を 100）で確かめる。
    /// 直すと突き抜けが無くなり、1回の Undo で戻り、保存するとシェイプキーとして入ること。重ね着をまとめて編集してもペチコートごと押し出されること。
    /// </summary>
    public class PenetrationTests
    {
        const string TempRoot = "Assets/GP02_PenetrationTestTemp";
        const string Folder = TempRoot + "/Meshes";
        const string SettingsPrefix = "GP02.Tests.PenetrationSettings.";

        readonly List<Object> _created = new List<Object>();
        readonly List<Object> _clearUndoOnly = new List<Object>();
        readonly List<ClothEditController> _controllers = new List<ClothEditController>();
        double _time = 100.0;

        [TearDown]
        public void TearDown()
        {
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
            ClothEditSettings.DeleteSaved(SettingsPrefix);
        }

        // テスト用の衣装。Hip_Big の重みを入れる。separatePetticoat ならペチコートは別のレンダラー
        GameObject CreateCloth(float hipBig, bool separatePetticoat = false)
        {
            GameObject root = TestClothMenu.CreateInScene(null, separatePetticoat);
            _created.Add(root);
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                _created.Add(renderer.sharedMesh);
                foreach (Material m in renderer.sharedMaterials) _created.Add(m);
                _created.Add(renderer);
            }
            SkinnedMeshRenderer body = Find(root, "Body");
            int index = body.sharedMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "体に Hip_Big が無い");
            body.SetBlendShapeWeight(index, hipBig);
            return root;
        }

        static SkinnedMeshRenderer Find(GameObject root, string name) => root.transform.Find(name).GetComponent<SkinnedMeshRenderer>();

        ClothEditController Start(IReadOnlyList<Renderer> targets, ClothEditSettings settings = null)
        {
            var c = new ClothEditController(settings ?? new ClothEditSettings()) { SaveFolder = Folder, DataFolder = Folder, BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(targets, out string error), Is.True, error);
            return c;
        }

        double NextTime()
        {
            _time += 0.016;
            return _time;
        }

        // 布の今の形の頂点（粒子についていく頂点）が、体に入った最大の深さ（メートル、入っていなければ負）
        static float DeepestVertexInBody(ClothEditSession session)
        {
            session.UpdateCurrentShape();
            float worst = float.MinValue;
            foreach (Vector3 v in session.CurrentWorldVertices)
            {
                if (!session.Body.TryQuery(new NVector3(v.x, v.y, v.z), 0.05f, out BodyHit hit)) continue;
                worst = Mathf.Max(worst, -hit.SignedDistance);
            }
            return worst;
        }

        static NVector3[] Positions(ClothEditSession session) => session.Solver.Positions.ToArray();

        // 体の中に入っている粒子の数と、一番深い粒子の深さ（メートル、入っていなければ負）
        static int ParticlesInBody(ClothEditSession session, out float deepest)
        {
            int count = 0;
            deepest = float.MinValue;
            ReadOnlySpan<NVector3> positions = session.Solver.Positions;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!session.Body.TryQuery(positions[i], 0.05f, out BodyHit hit)) continue;
                deepest = Mathf.Max(deepest, -hit.SignedDistance);
                if (hit.SignedDistance < -PenetrationReport.CountTolerance) count++;
            }
            return count;
        }

        [Test]
        public void HipBig_PenetratesTheSkirt_AndIsDetectedWhenEditingStarts()
        {
            GameObject root = CreateCloth(100f);
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            ClothEditController c = Start(new Renderer[] { skirt });
            Assert.That(c.HasBody, Is.True, c.Session.BodyDescription);
            PenetrationReport report = c.LastPenetration;
            Assert.That(report, Is.Not.Null, "編集を始めたときに調べる");
            Debug.Log("[GP02] Hip_Big 100 のスカート: " + report + " / " + c.Session.BodyDescription);
            Assert.That(report.HasBody, Is.True);
            Assert.That(report.PenetratingCount, Is.GreaterThan(50));
            Assert.That(report.RegionCount, Is.InRange(1, 3), "スカートとペチコートの後ろ");
            Assert.That(report.MaxDepth, Is.InRange(0.006f, 0.02f));
            Assert.That(report.PointParticles.Count, Is.GreaterThan(0));
            // 突き抜けているのは後ろ（+z）の腰の高さだけ
            foreach (int i in report.PointParticles)
            {
                NVector3 p = c.Session.Solver.Positions[i];
                Assert.That(p.Z, Is.GreaterThan(0.05f), $"粒子 {i} {p}");
                Assert.That(p.Y, Is.InRange(0.70f, 0.80f), $"粒子 {i} {p}");
            }
            var points = new List<Vector3>();
            c.Session.GetPenetrationPoints(points, 100);
            Assert.That(points.Count, Is.InRange(1, 100));
            var warnings = new List<TargetWarning>();
            ClothEditWarnings.CollectEditing(c, warnings);
            Assert.That(warnings.Exists(w => w.Kind == TargetWarningKind.Penetration && w.Severity == MessageType.Warning), Is.True, "診断に出る");

            // Hip_Big が 0 なら突き抜けていない
            c.End();
            Find(root, "Body").SetBlendShapeWeight(Find(root, "Body").sharedMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName), 0f);
            ClothEditController plain = Start(new Renderer[] { skirt });
            Assert.That(plain.LastPenetration.HasBody, Is.True);
            Assert.That(plain.LastPenetration.PenetratingCount, Is.EqualTo(0), plain.LastPenetration.ToString());
            Assert.That(plain.LastPenetration.NeedPushCount, Is.EqualTo(0));
        }

        [Test]
        public void FixPenetration_RemovesIt_KeepsTheClothLength_AndOneUndoRestores()
        {
            GameObject root = CreateCloth(100f);
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            ClothEditController c = Start(new Renderer[] { skirt });
            NVector3[] before = Positions(c.Session);
            int penetratingBefore = c.LastPenetration.PenetratingCount;
            Assert.That(DeepestVertexInBody(c.Session), Is.GreaterThan(0.005f), "直す前は、布の頂点が体に入っている");

            var timer = Stopwatch.StartNew();
            int remaining = c.FixPenetration();
            timer.Stop();
            PenetrationFixResult result = c.LastPenetrationFix;
            float deepest = DeepestVertexInBody(c.Session);
            Debug.Log($"[GP02] 貫通を直す（スカート、Hip_Big 100）: {result} / 頂点が体に入った最大 {deepest * 1000f:F2} mm / 合計 {timer.ElapsedMilliseconds} ms / 粒子 {c.Session.ParticleCount} / {ClothEditSession.DescribeAcceleration(c.Session.Solver.Kernel)}");
            Assert.That(remaining, Is.EqualTo(0));
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0), "直したあとに調べ直している");
            Assert.That(c.LastPenetration.CloseCount, Is.LessThan(penetratingBefore / 4), "余白より近い所もほとんど残らない");
            Assert.That(deepest, Is.LessThan(0.0015f), "粒子についていく頂点も、ほぼ体の外");
            Assert.That(result.MaxStretch, Is.LessThan(0.1f), $"最大の伸び {result.MaxStretch:P1}");
            Assert.That(c.IsBusy, Is.False);

            // 1回の Undo で、直す前の形に戻る（勝手に押し出し直さない）。調べた結果も、戻した形で調べ直される
            c.UndoLast();
            NVector3[] undone = Positions(c.Session);
            for (int i = 0; i < before.Length; i++) Assert.That(NVector3.Distance(before[i], undone[i]), Is.LessThan(1e-6f), $"粒子 {i}");
            Assert.That(c.IsPenetrationStale, Is.False, "Undo のあとに調べ直していない");
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(penetratingBefore));
            for (int f = 0; f < 5; f++) c.Session.Solver.Solve(10);
            NVector3[] solved = Positions(c.Session);
            for (int i = 0; i < before.Length; i++) Assert.That(NVector3.Distance(before[i], solved[i]), Is.LessThan(1e-5f), $"元に戻したあとに解いても押し出さない: 粒子 {i}");

            // やり直すと、直した形になる
            c.RedoLast();
            Assert.That(c.IsPenetrationStale, Is.False, "Redo のあとに調べ直していない");
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0));
        }

        [Test]
        public void AfterFix_PushingTheClothIn_StopsAtTheBody()
        {
            GameObject root = CreateCloth(100f);
            ClothEditController c = Start(new Renderer[] { Find(root, "Skirt") });
            Assert.That(c.FixPenetration(), Is.EqualTo(0));

            // 後ろの腰の高さの布を、面の法線に沿って内へ 3cm 押す
            c.Settings.MoveDirection = MoveDirection.SurfaceNormal;
            c.Mode = ClothEditMode.Grab;
            var ray = new Ray(new Vector3(0f, 0.755f, 1f), Vector3.back);
            Assert.That(c.PointerDown(ray, Vector3.back), Is.True);
            Vector3 hit = c.HoverPoint, normal = c.HoverNormal;
            var camera = new Vector3(0f, 0.755f, 1f);
            for (int k = 1; k <= 20; k++)
            {
                Vector3 target = hit - normal * (0.03f * k / 20f);
                c.PointerDrag(new Ray(camera, (target - camera).normalized));
                c.Tick(NextTime());
            }
            c.PointerUp();
            for (int i = 0; i < 1000 && c.IsBusy; i++) c.Tick(NextTime());
            c.DetectPenetration();
            int inside = ParticlesInBody(c.Session, out float deepestParticle);
            float deepestVertex = DeepestVertexInBody(c.Session);
            Debug.Log($"[GP02] 直したあとに内へ 3cm 押す: {c.LastPenetration} / 体に入った粒子 {inside}（最大 {deepestParticle * 1000f:F2} mm）/ 頂点が体に入った最大 {deepestVertex * 1000f:F2} mm");
            // 粒子は体の中へ入らない
            Assert.That(inside, Is.EqualTo(0), "押し込んでも粒子は体の中へ入らない");
            // ただし Hip_Big の膨らみの角は丸みの半径が数 mm と鋭いので、強く押し付けると、粒子（1cm 間隔）の間を結ぶ頂点が角を横切り、
            // 角が布の外へ出ることがある。その深さは粒子の並び方しだいで、1コマの計算の実時間の予算によって 0.2〜3.6 mm と揺れる
            // （予算を 2〜40 ms、仕上げの時間切れを 2〜20 秒に変えて測った。予算を増やしても小さくなるとは限らない）。
            // なので、深さは緩い上限だけを見て、角が出たときは検出されて赤い点が出ること、もう一度「貫通を直す」で直せることを確かめる
            Assert.That(deepestVertex, Is.LessThan(0.005f), "粒子についていく頂点も、角の分（数 mm）より深くは入らない");
            if (deepestVertex > 0.0005f)
            {
                Assert.That(c.LastPenetration.PenetratingCount, Is.GreaterThan(0), "角が布の外へ出たら、検出されて赤い点が出る");
            }
            // そのときは、もう一度「貫通を直す」で直せる
            if (c.LastPenetration.PenetratingCount > 0)
            {
                Assert.That(c.FixPenetration(), Is.EqualTo(0), c.LastPenetrationFix?.ToString());
            }
        }

        [Test]
        public void FixThenSave_AddsTheShapeKey_AndTheSavedShapeIsOutsideTheBody()
        {
            GameObject root = CreateCloth(100f);
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            ClothEditController c = Start(new Renderer[] { skirt });
            Assert.That(c.FixPenetration(), Is.EqualTo(0));
            Assert.That(c.Save("Fit_HipBig", out string message), Is.True, message);
            Mesh saved = c.LastSavedMesh;
            _clearUndoOnly.Add(saved);
            int index = saved.GetBlendShapeIndex("Fit_HipBig");
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            Assert.That(skirt.GetBlendShapeWeight(index), Is.EqualTo(100f));

            // 保存のあとに作り直したセッションでも調べ直していて、突き抜けていない
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.LastPenetration, Is.Not.Null);
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0), c.LastPenetration.ToString());

            // Unity のスキニング（BakeMesh）で取り出した保存後の形も、体の外
            var baked = new Mesh();
            _created.Add(baked);
            skirt.BakeMesh(baked, true);
            Matrix4x4 toWorld = Matrix4x4.TRS(skirt.transform.position, skirt.transform.rotation, Vector3.one);
            float worst = float.MinValue;
            foreach (Vector3 v in baked.vertices)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(v);
                if (!c.Session.Body.TryQuery(new NVector3(w.x, w.y, w.z), 0.05f, out BodyHit hit)) continue;
                worst = Mathf.Max(worst, -hit.SignedDistance);
            }
            Debug.Log($"[GP02] 保存したシェイプキーの頂点が体に入った最大 {worst * 1000f:F2} mm");
            Assert.That(worst, Is.LessThan(0.0015f));
        }

        // スカートがペチコートの内に入った最大の深さ（LayeredEditTests と同じ測り方）
        static float SkirtIntoPetticoat(ClothEditSession session)
        {
            session.UpdateCurrentShape();
            Vector3[] world = session.CurrentWorldVertices;
            var positions = new NVector3[world.Length];
            for (int v = 0; v < world.Length; v++) positions[v] = new NVector3(world[v].x, world[v].y, world[v].z);
            session.GetVertexRange(1, out int petticoatStart, out _);
            int[] petticoatTriangles = session.GetSourceMesh(1).triangles;
            for (int k = 0; k < petticoatTriangles.Length; k++) petticoatTriangles[k] += petticoatStart;
            var bvh = new TriangleBvh(positions, petticoatTriangles);
            float worst = float.MinValue;
            foreach (int v in session.GetSourceMesh(0).GetTriangles(0))
            {
                NVector3 p = positions[v];
                if (p.Y > TestClothMenu.SkirtTopY - 0.02f || p.Y < TestClothMenu.SkirtTopY - 0.8f * TestClothMenu.SkirtLength) continue;
                var radial = new NVector3(p.X, 0f, p.Z);
                float radius = radial.Length();
                if (!bvh.Raycast(new NVector3(0f, p.Y, 0f), radial / radius, positions, out float innerRadius, out _)) continue;
                worst = Mathf.Max(worst, innerRadius - radius);
            }
            return worst;
        }

        [Test]
        public void Layered_FixPushesThePetticoatOutToo()
        {
            GameObject root = CreateCloth(100f, true);
            SkinnedMeshRenderer skirt = Find(root, "Skirt"), petticoat = Find(root, "Petticoat");
            ClothEditController c = Start(new Renderer[] { skirt, petticoat });
            Assert.That(c.Session.RendererCount, Is.EqualTo(2));
            PenetrationReport report = c.LastPenetration;
            int petticoatPenetrating = 0;
            for (int i = 0; i < report.Push.Length; i++)
            {
                if (report.Push[i] <= report.Margin + PenetrationReport.CountTolerance) continue;
                int triangle = c.Session.Cloth.Topology.TriangleToSource[c.Session.Cloth.ParticleTriangle[i]];
                if (c.Session.TryGetMeshTriangle(triangle, out int rendererIndex, out _) && rendererIndex == 1) petticoatPenetrating++;
            }
            Debug.Log($"[GP02] 重ね着（Hip_Big 100）: {report}（ペチコートの粒子 {petticoatPenetrating}）");
            Assert.That(petticoatPenetrating, Is.GreaterThan(20), "ペチコートも突き抜けている");
            NVector3[] before = Positions(c.Session);

            var timer = Stopwatch.StartNew();
            Assert.That(c.FixPenetration(), Is.EqualTo(0));
            timer.Stop();
            float petticoatMove = 0f;
            NVector3[] after = Positions(c.Session);
            for (int i = 0; i < after.Length; i++)
            {
                int triangle = c.Session.Cloth.Topology.TriangleToSource[c.Session.Cloth.ParticleTriangle[i]];
                if (c.Session.TryGetMeshTriangle(triangle, out int rendererIndex, out _) && rendererIndex == 1) petticoatMove = Mathf.Max(petticoatMove, NVector3.Distance(before[i], after[i]));
            }
            float layer = SkirtIntoPetticoat(c.Session);
            Debug.Log($"[GP02] 重ね着の貫通を直す: {c.LastPenetrationFix} / ペチコートの最大の移動 {petticoatMove * 1000f:F1} mm / スカートがペチコートに入った最大 {layer * 1000f:F2} mm / 頂点が体に入った最大 {DeepestVertexInBody(c.Session) * 1000f:F2} mm / 合計 {timer.ElapsedMilliseconds} ms");
            Assert.That(petticoatMove, Is.GreaterThan(0.005f), "ペチコートも押し出される");
            Assert.That(layer, Is.LessThan(0.002f), "スカートはペチコートの外のまま");
            Assert.That(DeepestVertexInBody(c.Session), Is.LessThan(0.0015f));
        }

        [Test]
        public void ParticlesInsideTheBody_GetZeroGap()
        {
            GameObject root = CreateCloth(100f);
            using (var session = new ClothEditSession(Find(root, "Skirt")))
            {
                Assert.That(session.Body, Is.Not.Null);
                Assert.That(session.RestGap.InsideCount, Is.GreaterThan(20), session.RestGap.ToString());
                NVector3[] rest = session.Cloth.Model.RestPositions;
                int inside = 0;
                for (int i = 0; i < rest.Length; i++)
                {
                    if (!session.Body.TryQuery(rest[i], 0.05f, out BodyHit hit) || hit.SignedDistance >= 0f) continue;
                    if (session.Cloth.Model.RestNormals[i] == NVector3.Zero) continue;
                    inside++;
                    Assert.That(session.RestAllowance[i], Is.EqualTo(0f), $"体の中の粒子 {i} の隙間");
                    Assert.That(session.RestAllowanceWithoutLayers[i], Is.EqualTo(0f));
                }
                Assert.That(inside, Is.EqualTo(session.RestGap.InsideCount));
            }
        }

        [Test]
        public void Recheck_PicksUpTheBodyShapeChangedWhileEditing()
        {
            GameObject root = CreateCloth(0f);
            ClothEditController c = Start(new Renderer[] { Find(root, "Skirt") });
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0));
            SkinnedMeshRenderer body = Find(root, "Body");
            body.SetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName), 100f);

            // 調べただけでは古い体のまま。「調べ直す」で体を集め直す
            c.DetectPenetration();
            Assert.That(c.LastPenetration.PenetratingCount, Is.EqualTo(0));
            c.RecheckPenetration();
            Assert.That(c.LastPenetration.PenetratingCount, Is.GreaterThan(50), c.LastPenetration.ToString());
            Assert.That(c.FixPenetration(), Is.EqualTo(0));
        }

        [Test]
        public void EditingThePetticoatAlone_DoesNotTreatTheOuterSkirtAsBody()
        {
            GameObject root = CreateCloth(0f, true);
            ClothEditController c = Start(new Renderer[] { Find(root, "Petticoat") });
            // スカート（ペチコートの 3mm 外）は布の外側にあるので「体」に入れない。入れると、ペチコートがスカートの中にあるので突き抜けているとみなしてしまう
            Assert.That(c.HasBody, Is.True);
            Assert.That(c.LastPenetration.NeedPushCount, Is.EqualTo(0), c.LastPenetration + " / " + c.Session.BodyDescription);
            var front = new Ray(new Vector3(0f, 0.65f, -1f), Vector3.forward);
            Assert.That(c.Session.Raycast(front, out Vector3 hit, out Vector3 normal), Is.True);
            // ペチコートとスカートの間の点。スカートを体に入れていれば、スカートの「中」（すぐ近く）になる。
            // 使っていなければ、体（胴体・脚）は 5cm より遠いので見つからない
            Vector3 between = hit + normal * 0.0015f;
            bool near = c.Session.Body.TryQuery(new NVector3(between.x, between.y, between.z), 0.05f, out BodyHit bodyHit);
            Assert.That(!near || bodyHit.SignedDistance > 0.01f, Is.True, $"外側のスカートを体として使っている（距離 {bodyHit.SignedDistance * 1000f:F1} mm）");

            // スカートだけを編集するときは、内側のペチコートを体として使う（スカートはペチコートの中へ入らない）
            c.End();
            ClothEditController skirtOnly = Start(new Renderer[] { Find(root, "Skirt") });
            Assert.That(skirtOnly.LastPenetration.PenetratingCount, Is.EqualTo(0));
            Assert.That(skirtOnly.Session.Raycast(front, out hit, out normal), Is.True);
            Vector3 insidePetticoat = hit - normal * 0.0045f;
            Assert.That(skirtOnly.Session.Body.TryQuery(new NVector3(insidePetticoat.x, insidePetticoat.y, insidePetticoat.z), 0.05f, out bodyHit), Is.True);
            Assert.That(bodyHit.SignedDistance, Is.LessThan(0f), "内側のペチコートを体として使っていない");
        }

        [Test]
        public void Settings_PenetrationMargin_SavesLoadsResets_AndGoesToTheSolver()
        {
            var settings = new ClothEditSettings { PenetrationMargin = 0.004f };
            settings.Save(SettingsPrefix);
            ClothEditSettings loaded = ClothEditSettings.Load(SettingsPrefix);
            Assert.That(loaded.PenetrationMargin, Is.EqualTo(0.004f));
            loaded.PenetrationMargin = 1f;
            loaded.Sanitize();
            Assert.That(loaded.PenetrationMargin, Is.EqualTo(ClothEditSettings.MaxPenetrationMargin));
            loaded.PenetrationMargin = float.NaN;
            loaded.Sanitize();
            Assert.That(loaded.PenetrationMargin, Is.EqualTo(ClothEditSettings.DefaultPenetrationMargin));
            loaded.PenetrationMargin = 0.005f;
            loaded.ResetToDefaults();
            Assert.That(loaded.PenetrationMargin, Is.EqualTo(ClothEditSettings.DefaultPenetrationMargin));
            ClothEditSettings.DeleteSaved(SettingsPrefix);
            Assert.That(ClothEditSettings.Load(SettingsPrefix).PenetrationMargin, Is.EqualTo(ClothEditSettings.DefaultPenetrationMargin));

            GameObject root = CreateCloth(0f);
            ClothEditController c = Start(new Renderer[] { Find(root, "Skirt") }, new ClothEditSettings { PenetrationMargin = 0.003f });
            Assert.That(c.Session.Solver.BodyMargin, Is.EqualTo(0.003f));
            Assert.That(c.LastPenetration.Margin, Is.EqualTo(0.003f));
            c.Settings.PenetrationMargin = 0.001f;
            c.ApplySettings();
            Assert.That(c.Session.Solver.BodyMargin, Is.EqualTo(0.001f));
            Assert.That(c.BuildPenetrationOptions().Margin, Is.EqualTo(0.001f));
        }

        // 検出と押し出しにかかる時間（Burst あり・なし）をログに出す。GP02_UNITY_ARGS=-debugCodeOptimization でも回す
        [Test]
        public void Timing_DetectAndFix_WithAndWithoutAcceleration()
        {
            foreach (bool accelerate in new[] { true, false })
            {
                if (accelerate && ClothSolver.DefaultKernel == null) continue;
                GameObject root = CreateCloth(100f, true);
                var settings = new ClothEditSettings { UseAcceleration = accelerate };
                ClothEditController c = Start(new Renderer[] { Find(root, "Skirt"), Find(root, "Petticoat") }, settings);
                var timer = Stopwatch.StartNew();
                PenetrationReport report = c.DetectPenetration();
                double detect = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                int remaining = c.FixPenetration();
                double fix = timer.Elapsed.TotalMilliseconds;
                Debug.Log($"[GP02] 時間（{ClothEditSession.DescribeAcceleration(c.Session.Solver.Kernel)}、粒子 {c.Session.ParticleCount}）: 体を集める {c.Session.BodyMilliseconds:F0} ms / 調べる {detect:F0} ms（{report.Milliseconds:F0} ms）/ 直す {fix:F0} ms（{c.LastPenetrationFix}）/ 残り {remaining}");
                Assert.That(remaining, Is.EqualTo(0));
                c.End();
            }
        }
    }
}
