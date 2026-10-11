using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// 「テスト用の衣装を作る」メニューの中身が、編集ツールでそのまま使える衣装を作るかを確かめる。
    /// </summary>
    public class TestClothMenuTests
    {
        const float Tolerance = 1e-4f;
        // アセットの保存を試すときは、既定のフォルダを汚さないよう専用のフォルダを使い、終わったら丸ごと消す
        const string TempFolder = "Assets/GP02_TestClothMenuTests";

        readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // 保存しない場合のメッシュとマテリアルはシーンの外に残るので、片づける対象に入れる
        GameObject CreateCloth(string assetFolder = null)
        {
            GameObject root = TestClothMenu.CreateInScene(assetFolder);
            Track(root);
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(renderer.sharedMesh))) Track(renderer.sharedMesh);
                foreach (Material m in renderer.sharedMaterials)
                {
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(m))) Track(m);
                }
            }
            return root;
        }

        static SkinnedMeshRenderer Find(GameObject root, string name)
        {
            return root.transform.Find(name).GetComponent<SkinnedMeshRenderer>();
        }

        static int IslandOfSourceTriangle(ClothEditSession session, int sourceTriangle)
        {
            var topology = session.Cloth.Topology;
            return topology.TriangleIsland[topology.SourceToTriangle[sourceTriangle]];
        }

        // スカートの +z 側から軸へ向かうレイ。リボンはウエスト（高さ 0.8m）にしか無く、継ぎ目（0°）も避けた向き
        static Ray RayToSkirt(float height)
        {
            return new Ray(new Vector3(0f, height, 1f), Vector3.back);
        }

        // 外側のスカートをつまんで引っぱり、解く
        static void PullOuterSkirt(ClothEditSession session)
        {
            session.Solver.InfluenceRadius = 0.2f;
            Assert.That(session.Raycast(RayToSkirt(0.6f), out Vector3 hit, out Vector3 normal, out int triangle), Is.True);
            Assert.That(session.BeginGrab(hit, 0.1f, triangle), Is.True);
            session.DragTo(hit + normal * 0.05f);
            for (int i = 0; i < 4; i++) session.Solver.Solve(10);
            session.Release();
            session.UpdateCurrentShape();
        }

        [Test]
        public void CreateInScene_MakesBodyAndSkirtWithBones()
        {
            GameObject root = CreateCloth();

            SkinnedMeshRenderer body = Find(root, "Body");
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            Assert.That(body, Is.Not.Null);
            Assert.That(skirt, Is.Not.Null);
            Assert.That(Selection.activeGameObject, Is.EqualTo(skirt.gameObject));

            foreach (SkinnedMeshRenderer renderer in new[] { body, skirt })
            {
                Mesh mesh = renderer.sharedMesh;
                Assert.That(mesh, Is.Not.Null);
                Assert.That(renderer.bones.Length, Is.EqualTo(3));
                Assert.That(renderer.bones, Has.None.Null);
                Assert.That(renderer.rootBone, Is.Not.Null);
                Assert.That(mesh.bindposes.Length, Is.EqualTo(renderer.bones.Length));
                Assert.That(mesh.GetBonesPerVertex().Length, Is.EqualTo(mesh.vertexCount));
                Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(mesh.subMeshCount));
                Assert.That(renderer.sharedMaterials, Has.None.Null);
                AssertNoNaN(mesh);
            }

            // 体は高さ約 1.4m。足先が床（y = 0）に付く
            Bounds bodyBounds = body.sharedMesh.bounds;
            Assert.That(bodyBounds.min.y, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(bodyBounds.max.y, Is.EqualTo(1.4f).Within(1e-3f));

            // 脚のボーンがバインドポーズと違う姿勢になっている
            Assert.That(skirt.bones[1].localRotation, Is.Not.EqualTo(Quaternion.identity));
            Assert.That(skirt.bones[2].localRotation, Is.Not.EqualTo(Quaternion.identity));
        }

        static void AssertNoNaN(Mesh mesh)
        {
            foreach (Vector3 v in mesh.vertices) Assert.That(float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z), Is.False);
            foreach (Vector3 n in mesh.normals) Assert.That(float.IsNaN(n.x + n.y + n.z) || float.IsInfinity(n.x + n.y + n.z), Is.False);
            foreach (Vector2 uv in mesh.uv) Assert.That(float.IsNaN(uv.x + uv.y), Is.False);
            foreach (BoneWeight w in mesh.boneWeights)
            {
                Assert.That(w.weight0 + w.weight1 + w.weight2 + w.weight3, Is.EqualTo(1f).Within(1e-5f));
            }
            Assert.That(mesh.boneWeights.Length, Is.EqualTo(mesh.vertexCount));
        }

        [Test]
        public void SkirtMesh_HasExpectedShape()
        {
            Mesh mesh = Track(TestClothMenu.CreateSkirtMesh());
            Assert.That(mesh.vertexCount, Is.InRange(5000, 10000));
            Assert.That(mesh.subMeshCount, Is.EqualTo(3));

            // UV の継ぎ目：同じ位置に重なった頂点が、スカートとペチコートの縦の列の数だけある
            var seen = new HashSet<Vector3>();
            int duplicates = 0;
            foreach (Vector3 v in mesh.vertices)
            {
                if (!seen.Add(v)) duplicates++;
            }
            Assert.That(duplicates, Is.EqualTo(TestClothMenu.SkirtRows + 1 + TestClothMenu.PetticoatRows + 1));

            // 寸法：上の半径 13cm、下の半径 30cm、丈 35cm
            Vector3[] vertices = mesh.vertices;
            int[] outer = mesh.GetTriangles(0);
            float minRadius = float.MaxValue, maxRadius = 0f, minY = float.MaxValue, maxY = float.MinValue;
            foreach (int i in outer)
            {
                float radius = new Vector2(vertices[i].x, vertices[i].z).magnitude;
                minRadius = Mathf.Min(minRadius, radius);
                maxRadius = Mathf.Max(maxRadius, radius);
                minY = Mathf.Min(minY, vertices[i].y);
                maxY = Mathf.Max(maxY, vertices[i].y);
            }
            Assert.That(minRadius, Is.EqualTo(0.13f).Within(1e-4f));
            Assert.That(maxRadius, Is.EqualTo(0.30f).Within(1e-4f));
            Assert.That(maxY - minY, Is.EqualTo(0.35f).Within(1e-4f));

            // 三角形は外向きが表（スカートの外から見て、法線が外を向く）
            for (int sub = 0; sub < 2; sub++)
            {
                int[] triangles = mesh.GetTriangles(sub);
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]];
                    Vector3 n = Vector3.Cross(vertices[triangles[t + 1]] - a, vertices[triangles[t + 2]] - a);
                    if (n.sqrMagnitude < 1e-12f) continue;
                    Assert.That(Vector3.Dot(n, new Vector3(a.x, 0f, a.z)), Is.GreaterThan(0f), $"サブメッシュ {sub} の三角形 {t / 3}");
                }
            }
        }

        [Test]
        public void SkirtSession_HasSkirtPetticoatAndRibbonIslands()
        {
            GameObject root = CreateCloth();
            using (var session = new ClothEditSession(Find(root, "Skirt")))
            {
                // スカート 1 + ペチコート 1 + リボン
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(2 + TestClothMenu.RibbonCount));
            }
        }

        [Test]
        public void GrabbingOuterSkirt_DoesNotGrabPetticoat()
        {
            GameObject root = CreateCloth();
            SkinnedMeshRenderer skirt = Find(root, "Skirt");
            int petticoatTriangle = (int)skirt.sharedMesh.GetIndexStart(1) / 3;

            using (var session = new ClothEditSession(skirt))
            {
                Assert.That(session.Raycast(RayToSkirt(0.6f), out Vector3 hit, out Vector3 normal, out int triangle), Is.True);
                // 外側（サブメッシュ 0）に当たる
                Assert.That(triangle, Is.LessThan(petticoatTriangle));

                Assert.That(session.BeginGrab(hit, 0.1f, triangle), Is.True);
                int grabbed = session.Solver.GrabbedParticle;
                Assert.That(session.Cloth.ParticleIsland[grabbed], Is.EqualTo(IslandOfSourceTriangle(session, triangle)));
                Assert.That(session.Cloth.ParticleIsland[grabbed], Is.Not.EqualTo(IslandOfSourceTriangle(session, petticoatTriangle)));
                session.Release();

                // 引っぱると、外側のスカートだけが引かれる。
                // 引いた所に寄せられた周りのスカートが内へ締まると、層どうしの接触でペチコートも内へ押されるが、引いた量よりずっと小さい
                Vector3[] before = (Vector3[])session.CurrentWorldVertices.Clone();
                PullOuterSkirt(session);
                int petticoatStart = skirt.sharedMesh.GetSubMesh(1).firstVertex;
                int petticoatCount = (int)skirt.sharedMesh.GetSubMesh(1).vertexCount;
                float skirtMove = 0f, petticoatMove = 0f;
                for (int v = 0; v < petticoatStart; v++) skirtMove = Mathf.Max(skirtMove, Vector3.Distance(before[v], session.CurrentWorldVertices[v]));
                for (int v = petticoatStart; v < petticoatStart + petticoatCount; v++) petticoatMove = Mathf.Max(petticoatMove, Vector3.Distance(before[v], session.CurrentWorldVertices[v]));
                Assert.That(skirtMove, Is.GreaterThan(0.02f));
                Assert.That(petticoatMove, Is.LessThan(0.5f * skirtMove), "ペチコートがスカートと一緒に動いています");

                // 層どうしの接触を切れば、ペチコートはまったく動かない（つまんだのが外側のスカートだけであること）
                session.ResetAll();
                session.Solver.KeepLayersApart = false;
                PullOuterSkirt(session);
                for (int v = petticoatStart; v < petticoatStart + petticoatCount; v++)
                {
                    Assert.That(Vector3.Distance(before[v], session.CurrentWorldVertices[v]), Is.LessThan(1e-5f), $"ペチコートの頂点 {v}");
                }
            }
        }

        // ポーズを付けた状態で、保存したシェイプキーが編集結果どおりに見えるか
        [Test]
        public void PosedSkirt_SavedShapeMatchesEditedShape()
        {
            GameObject root = CreateCloth();
            SkinnedMeshRenderer skirt = Find(root, "Skirt");

            using (var session = new ClothEditSession(skirt))
            {
                PullOuterSkirt(session);
                Mesh saved = Track(session.CreateMeshWithBlendShape("GP02_Test"));
                skirt.sharedMesh = saved;
                skirt.SetBlendShapeWeight(saved.GetBlendShapeIndex("GP02_Test"), 100f);

                Mesh baked = Track(new Mesh());
                skirt.BakeMesh(baked, true);
                Vector3[] bakedVertices = baked.vertices;
                Matrix4x4 toWorld = skirt.transform.localToWorldMatrix;
                float maxMove = 0f;
                for (int v = 0; v < bakedVertices.Length; v++)
                {
                    Vector3 world = toWorld.MultiplyPoint3x4(bakedVertices[v]);
                    Assert.That(Vector3.Distance(world, session.CurrentWorldVertices[v]), Is.LessThan(Tolerance), $"頂点 {v}");
                    maxMove = Mathf.Max(maxMove, saved.vertices[v].magnitude);
                }
                Assert.That(maxMove, Is.GreaterThan(0f));
            }
        }

        [Test]
        public void CreateInScene_SavesAssetsWithUniqueNames()
        {
            GameObject first = CreateCloth(TempFolder);
            Mesh firstMesh = Find(first, "Skirt").sharedMesh;
            string firstPath = AssetDatabase.GetAssetPath(firstMesh);
            Assert.That(firstPath, Is.EqualTo(TempFolder + "/GP02_TestSkirt.asset"));
            Assert.That(AssetDatabase.GetAssetPath(Find(first, "Body").sharedMesh), Does.StartWith(TempFolder + "/"));
            foreach (Material m in Find(first, "Skirt").sharedMaterials) Assert.That(AssetDatabase.GetAssetPath(m), Does.StartWith(TempFolder + "/"));

            // 2 回目は上書きせず、番号を付けて別のファイルにする
            GameObject second = CreateCloth(TempFolder);
            string secondPath = AssetDatabase.GetAssetPath(Find(second, "Skirt").sharedMesh);
            Assert.That(secondPath, Is.Not.EqualTo(firstPath));
            Assert.That(secondPath, Does.StartWith(TempFolder + "/GP02_TestSkirt"));
            Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>(firstPath), Is.Not.Null);
        }
    }
}
