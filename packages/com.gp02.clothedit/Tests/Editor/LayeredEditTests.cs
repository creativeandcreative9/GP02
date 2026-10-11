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
    /// 重ね着（スカートとペチコートを別々のレンダラーにしたテスト用の衣装）をまとめて1つの布として編集することを確かめる。
    /// スカートを内へ押す・回すとペチコートも押されて突き抜けないこと、保存で両方のメッシュにシェイプキーが入り、Undo で両方戻ること。
    /// </summary>
    public class LayeredEditTests
    {
        const string TempRoot = "Assets/GP02_LayeredTestTemp";
        const string Folder = TempRoot + "/Meshes";
        static readonly Vector3 CameraPosition = new Vector3(0f, 0.55f, -1.5f);

        readonly List<Object> _created = new List<Object>();
        readonly List<Object> _clearUndoOnly = new List<Object>();   // アセットなので消さず、Undo の記録だけ消す
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
            // 作った順と逆に消す（ルートより先に、その下のコンポーネントの Undo の記録を消すため）
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

        // テスト用の衣装（ペチコートは別のレンダラー）。返す配列は スカート、ペチコート の順
        SkinnedMeshRenderer[] CreateLayeredCloth(out GameObject root)
        {
            root = Track(TestClothMenu.CreateInScene(null, true));
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material m in renderer.sharedMaterials) Track(m);
                _created.Add(renderer);
            }
            return new[]
            {
                root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>(),
                root.transform.Find("Petticoat").GetComponent<SkinnedMeshRenderer>(),
            };
        }

        ClothEditController StartController(IReadOnlyList<Renderer> targets, ClothEditSettings settings = null)
        {
            var c = new ClothEditController(settings ?? new ClothEditSettings()) { SaveFolder = Folder, DataFolder = Folder, BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(targets, out string error), Is.True, error);
            PinWaist(c.Session);
            return c;
        }

        // 実際の使い方に合わせて、ウエスト（一番上の段）を、どの層もピン留めする
        static void PinWaist(ClothEditSession session)
        {
            float top = float.MinValue;
            foreach (NVector3 r in session.Cloth.Model.RestPositions) top = Mathf.Max(top, r.Y);
            var particles = new List<int>();
            NVector3[] rest = session.Cloth.Model.RestPositions;
            for (int i = 0; i < rest.Length; i++)
            {
                if (rest[i].Y > top - 1.5f * session.Cloth.Spacing) particles.Add(i);
            }
            session.PinParticles(particles);
        }

        double NextTime()
        {
            _time += 0.016;
            return _time;
        }

        void SettleUntilDone(ClothEditController c)
        {
            for (int i = 0; i < 1000 && c.IsBusy; i++) c.Tick(NextTime());
            Assert.That(c.IsBusy, Is.False, "仕上げが終わりません");
        }

        static Ray RayTo(Vector3 point) => new Ray(CameraPosition, (point - CameraPosition).normalized);

        // 前の、高さ y の布を、面の法線に沿って depth だけ内へ押して離し、仕上げる
        void PushInward(ClothEditController c, float y, float depth)
        {
            c.Settings.MoveDirection = MoveDirection.SurfaceNormal;
            c.Mode = ClothEditMode.Grab;
            Ray ray = new Ray(new Vector3(0f, y, -1.5f), Vector3.forward);
            Assert.That(c.PointerDown(ray, Vector3.forward), Is.True, "布に当たりません");
            Vector3 hit = c.HoverPoint;
            Vector3 normal = c.HoverNormal;
            for (int k = 1; k <= 20; k++)
            {
                c.PointerDrag(RayTo(hit - normal * (depth * k / 20f)));
                c.Tick(NextTime());
            }
            c.PointerUp();
            SettleUntilDone(c);
        }

        // 前の、高さ y の布を、面の法線まわりに回して離し、仕上げる
        void Rotate(ClothEditController c, float y, float pixels)
        {
            c.Settings.RotateAxis = RotateAxisMode.SurfaceNormal;
            c.Settings.Falloff = FalloffPreset.Smooth;
            c.Mode = ClothEditMode.Rotate;
            c.BrushRadius = 0.12f;
            Ray ray = new Ray(new Vector3(0f, y, -1.5f), Vector3.forward);
            Assert.That(c.PointerDown(ray, Vector3.forward, 100f), Is.True, "布に当たりません");
            for (int k = 1; k <= 20; k++)
            {
                c.PointerDrag(ray, 100f + pixels * k / 20f, false, false);
                c.Tick(NextTime());
            }
            c.PointerUp();
            SettleUntilDone(c);
        }

        // 粒子が乗っている三角形が、何番目のレンダラーのものか
        static int RendererOfParticle(ClothEditSession session, int particle)
        {
            int triangle = session.Cloth.Topology.TriangleToSource[session.Cloth.ParticleTriangle[particle]];
            Assert.That(session.TryGetMeshTriangle(triangle, out int renderer, out _), Is.True);
            return renderer;
        }

        // スカートがペチコートの内に入った最大の深さ（メートル、正なら突き抜けている）。見た目どうしで比べるため、今の形の頂点で比べる。
        // スカートの各頂点について、同じ高さの軸から頂点へ向かう水平なレイが、今の形のペチコートに最初に当たる距離と、頂点の軸からの距離を比べる
        static float MaxPenetration(ClothEditSession session)
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
            foreach (int v in session.GetSourceMesh(0).GetTriangles(0))   // サブメッシュ 0 がスカート（1 はリボン）
            {
                NVector3 p = positions[v];
                // ペチコートがある高さだけ（ウエストのピンの段と、ペチコートの裾の近くより下は除く）
                if (p.Y > TestClothMenu.SkirtTopY - 0.02f || p.Y < TestClothMenu.SkirtTopY - 0.8f * TestClothMenu.SkirtLength) continue;
                var radial = new NVector3(p.X, 0f, p.Z);
                float radius = radial.Length();
                if (!bvh.Raycast(new NVector3(0f, p.Y, 0f), radial / radius, positions, out float innerRadius, out _)) continue;
                worst = Mathf.Max(worst, innerRadius - radius);
            }
            return worst;
        }

        // ペチコートの粒子が動いた最大の距離
        static float MaxPetticoatMove(ClothEditSession session)
        {
            float max = 0f;
            NVector3[] rest = session.Cloth.Model.RestPositions;
            for (int i = 0; i < rest.Length; i++)
            {
                if (RendererOfParticle(session, i) != 1) continue;
                max = Mathf.Max(max, NVector3.Distance(session.Solver.Positions[i], rest[i]));
            }
            return max;
        }

        [Test]
        public void LayeredSession_CombinesRenderers_IntoOneClothWithLayerContacts()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out _);
            using (var session = new ClothEditSession(layers))
            {
                Assert.That(session.RendererCount, Is.EqualTo(2));
                Assert.That(session.Renderer, Is.SameAs(layers[0]));
                Assert.That(session.VertexCount, Is.EqualTo(layers[0].sharedMesh.vertexCount + layers[1].sharedMesh.vertexCount));
                session.GetVertexRange(1, out int start, out int count);
                Assert.That(start, Is.EqualTo(layers[0].sharedMesh.vertexCount));
                Assert.That(count, Is.EqualTo(layers[1].sharedMesh.vertexCount));
                // スカート・リボン 6 個・ペチコート。同じ位置の頂点があっても、レンダラーをまたいではつながない
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(1 + TestClothMenu.RibbonCount + 1));
                Assert.That(layers[0].forceRenderingOff && layers[1].forceRenderingOff, Is.True);

                // 体だけが「体」。まとめて編集しているペチコートは含まない
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(1));
                LayerContacts contacts = session.LayerContacts;
                Assert.That(contacts.Count, Is.GreaterThan(1000));
                for (int k = 0; k < contacts.Count; k++)
                {
                    Assert.That(RendererOfParticle(session, contacts.Outer[k]), Is.EqualTo(0));
                    Assert.That(RendererOfParticle(session, contacts.Inner[k]), Is.EqualTo(1));
                }

                // スカートの、ペチコートと組になった粒子は、ペチコートを通り越して体までの隙間を使う（ペチコートまでの 1mm ではない）
                int through = 0, total = 0;
                for (int k = 0; k < contacts.Count; k++)
                {
                    total++;
                    if (session.RestAllowance[contacts.Outer[k]] > TestClothMenu.PetticoatGap + 0.002f) through++;
                }
                Assert.That(through, Is.GreaterThan(total * 0.9f));
                Assert.That(MaxPenetration(session), Is.LessThan(-0.002f), "元の形では、スカートはペチコートの外");
            }
            Assert.That(layers[0].forceRenderingOff || layers[1].forceRenderingOff, Is.False);
        }

        [Test]
        public void PushingSkirtInward_PushesPetticoat_WithoutPassingThrough()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out _);
            ClothEditController c = StartController(layers);
            var timer = Stopwatch.StartNew();
            PushInward(c, 0.55f, 0.04f);
            float penetration = MaxPenetration(c.Session);
            float petticoat = MaxPetticoatMove(c.Session);
            Debug.Log($"[GP02] 重ね着をまとめて内へ 4cm 押す: ペチコートの最大の移動 {petticoat * 1000f:F1} mm / スカートがペチコートに入った最大 {penetration * 1000f:F2} mm / 最大の伸び {c.LastStretch:P1} / {timer.ElapsedMilliseconds} ms（粒子 {c.Session.ParticleCount}）");
            Assert.That(petticoat, Is.GreaterThan(0.015f), "ペチコートが押されていません");
            Assert.That(penetration, Is.LessThan(0.0015f), "スカートがペチコートを突き抜けています");

            // スカートだけを編集すると（ペチコートは体の扱い）、ペチコートは動かず、スカートは 1mm ほどしか内へ入れない
            c.End();
            ClothEditController single = StartController(new Renderer[] { layers[0] });
            PushInward(single, 0.55f, 0.04f);
            Debug.Log($"[GP02] スカートだけで内へ 4cm 押す: 最大の伸び {single.LastStretch:P1}");
        }

        [Test]
        public void RotatingSkirt_MovesPetticoat_WithoutPassingThrough()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out _);
            ClothEditController c = StartController(layers);
            Rotate(c, 0.5f, 120f);
            float penetration = MaxPenetration(c.Session);
            float petticoat = MaxPetticoatMove(c.Session);
            float layered = c.LastStretch;
            c.End();

            ClothEditController single = StartController(new Renderer[] { layers[0] });
            Rotate(single, 0.5f, 120f);
            Debug.Log($"[GP02] 回す（60°）: 重ね着をまとめて 最大の伸び {layered:P1}・ペチコートの最大の移動 {petticoat * 1000f:F1} mm・スカートがペチコートに入った最大 {penetration * 1000f:F2} mm / スカートだけ 最大の伸び {single.LastStretch:P1}");
            Assert.That(petticoat, Is.GreaterThan(0.001f), "ペチコートが動いていません");
            // 粒子は 1cm 間隔で、頂点は近くの粒子の動きの平均なので、大きく回すと 1〜2mm のずれは残る
            Assert.That(penetration, Is.LessThan(0.002f), "スカートがペチコートを突き抜けています");
        }

        [Test]
        public void Save_AddsTheShapeToBothMeshes_AndUndoRestoresBoth()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out _);
            Mesh skirtOriginal = layers[0].sharedMesh, petticoatOriginal = layers[1].sharedMesh;
            ClothEditController c = StartController(layers);
            PushInward(c, 0.55f, 0.04f);
            int particles = c.Session.ParticleCount;
            int pinned = c.Session.PinnedCount;

            Assert.That(c.Save("Layered", out string message), Is.True, message);
            Assert.That(c.LastSavedMeshes.Count, Is.EqualTo(2));
            for (int r = 0; r < 2; r++)
            {
                Mesh saved = c.LastSavedMeshes[r];
                _clearUndoOnly.Add(saved);
                Assert.That(layers[r].sharedMesh, Is.SameAs(saved));
                Assert.That(AssetDatabase.GetAssetPath(saved), Does.StartWith(Folder));
                int index = saved.GetBlendShapeIndex("Layered");
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.That(layers[r].GetBlendShapeWeight(index), Is.EqualTo(100f));
                // 押されたペチコートにも、差分が入っている
                var deltas = new Vector3[saved.vertexCount];
                saved.GetBlendShapeFrameVertices(index, 0, deltas, null, null);
                float max = 0f;
                foreach (Vector3 d in deltas) max = Mathf.Max(max, d.magnitude);
                Assert.That(max, Is.GreaterThan(0.01f), $"レンダラー {r} の差分が小さすぎます");

                // データへの記録もレンダラーごと
                var component = layers[r].GetComponent<GP02ShapeKeys>();
                Assert.That(component, Is.Not.Null);
                Assert.That(component.Data.FindEntry("Layered"), Is.Not.Null);
                Assert.That(component.Data.VertexCount, Is.EqualTo(saved.vertexCount));
            }
            Assert.That(c.IsEditing, Is.True);
            Assert.That(c.Session.RendererCount, Is.EqualTo(2));
            Assert.That(c.Session.ParticleCount, Is.EqualTo(particles));
            Assert.That(c.Session.PinnedCount, Is.EqualTo(pinned));
            Assert.That(c.Save("Layered", out message), Is.False, "同じ名前は保存できない");

            // 1回の Undo で、両方のメッシュが元に戻る
            Undo.PerformUndo();
            Assert.That(layers[0].sharedMesh, Is.SameAs(skirtOriginal));
            Assert.That(layers[1].sharedMesh, Is.SameAs(petticoatOriginal));
            Assert.That(c.IsEditing, Is.True, "編集が作り直される");
            Assert.That(c.Session.RendererCount, Is.EqualTo(2));
            Assert.That(c.Session.GetSourceMesh(1), Is.SameAs(petticoatOriginal));
        }

        [Test]
        public void UndoingAStroke_RestoresBothLayers()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out _);
            ClothEditController c = StartController(layers);
            PushInward(c, 0.55f, 0.04f);
            Assert.That(MaxPetticoatMove(c.Session), Is.GreaterThan(0.015f));

            c.UndoLast();
            Assert.That(MaxPetticoatMove(c.Session), Is.LessThan(1e-6f), "ペチコートも元に戻る");
            float skirtMoved = 0f;
            NVector3[] rest = c.Session.Cloth.Model.RestPositions;
            for (int i = 0; i < rest.Length; i++) skirtMoved = Mathf.Max(skirtMoved, NVector3.Distance(c.Session.Solver.Positions[i], rest[i]));
            Assert.That(skirtMoved, Is.LessThan(1e-6f));
        }

        [Test]
        public void FindLayeredRenderers_FindsThePetticoat_ButNotTheBody()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out GameObject root);
            List<Renderer> found = ClothEditController.FindLayeredRenderers(new Renderer[] { layers[0] });
            Assert.That(found, Is.EquivalentTo(new Renderer[] { layers[1] }));
            Assert.That(ClothEditController.FindLayeredRenderers(layers), Is.Empty);
            Assert.That(root.transform.Find("Body"), Is.Not.Null);
        }

        [Test]
        public void TryStart_WithDuplicatesAndMissingTargets()
        {
            SkinnedMeshRenderer[] layers = CreateLayeredCloth(out _);
            var c = new ClothEditController { SaveFolder = Folder, DataFolder = Folder };
            _controllers.Add(c);
            Assert.That(c.TryStartLayered(new Renderer[] { layers[0], layers[1], layers[0] }, out string error), Is.True, error);
            Assert.That(c.Targets.Count, Is.EqualTo(2));
            Assert.That(c.Target, Is.SameAs(layers[0]));
            Assert.That(c.TryStartLayered(new Renderer[] { layers[0], null }, out error), Is.False);
            Assert.That(c.IsEditing, Is.False);
            Assert.That(error, Is.Not.Empty);
        }
    }
}
