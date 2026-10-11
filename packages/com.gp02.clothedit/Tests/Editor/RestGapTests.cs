using System;
using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 体や内側の布までの隙間（<see cref="RestGapMeasurer"/>）を測ること、隙間を使うと内側へ動けて伸びが小さくなることを確かめる。
    /// </summary>
    public class RestGapTests
    {
        readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        // ---- テスト用の衣装（体・スカート・ペチコート） ----

        GameObject CreateTestCloth()
        {
            GameObject root = Track(TestClothMenu.CreateInScene(null));
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Track(renderer.sharedMesh);
                foreach (Material m in renderer.sharedMaterials) Track(m);
            }
            return root;
        }

        static int IslandOfSourceTriangle(ClothEditSession session, int sourceTriangle)
        {
            GP02.Core.MeshTopology topology = session.Cloth.Topology;
            return topology.TriangleIsland[topology.SourceToTriangle[sourceTriangle]];
        }

        // 体の胴体の半径（高さ y、0.72〜0.90m の区間の直線補間。TestClothMenu.CreateBodyMesh の輪郭と同じ）
        static float TorsoRadius(float y)
        {
            return 0.105f + (0.10f - 0.105f) * (y - 0.72f) / (0.90f - 0.72f);
        }

        [Test]
        public void TestCloth_OuterSkirtPassesThroughPetticoat_AndPetticoatGapIsDistanceToBody()
        {
            GameObject root = CreateTestCloth();
            SkinnedMeshRenderer skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
            int petticoatTriangle = (int)skirt.sharedMesh.GetIndexStart(1) / 3;

            using (var session = new ClothEditSession(skirt))
            {
                Assert.That(session.RestAllowance, Is.Not.Null);
                Assert.That(session.RestAllowance.Length, Is.EqualTo(session.ParticleCount));
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(1), "体だけが対象（自分は含まない）");
                Debug.Log("[GP02] テスト用の衣装: " + session.RestGap);

                int outerIsland = IslandOfSourceTriangle(session, 0);
                int petticoatIsland = IslandOfSourceTriangle(session, petticoatTriangle);
                int[] particleIsland = session.Cloth.ParticleIsland;
                NVector3[] rest = session.Cloth.Model.RestPositions;
                NVector3[] normals = session.Cloth.Model.RestNormals;
                float[] gaps = session.RestAllowance;

                // 外側のスカート：上から半分までの粒子は、3mm 内側のペチコートと接触の組になり、
                // ペチコートは押せば動くので、隙間はペチコートを通り越して体まで（または上限）を測る
                LayerContacts contacts = session.LayerContacts;
                int outerCount = 0, paired = 0, through = 0;
                float outerSum = 0f;
                for (int i = 0; i < gaps.Length; i++)
                {
                    if (particleIsland[i] != outerIsland) continue;
                    float t = (TestClothMenu.SkirtTopY - rest[i].Y) / TestClothMenu.SkirtLength;
                    if (t < 0.05f || t > 0.5f) continue;
                    outerCount++;
                    outerSum += gaps[i];
                    if (!contacts.HasInnerLayer[i]) continue;
                    paired++;
                    if (gaps[i] > TestClothMenu.PetticoatGap + 0.002f) through++;
                }
                Assert.That(outerCount, Is.GreaterThan(500));
                Debug.Log($"[GP02] 外側のスカートの隙間（上から 5〜50%）: 平均 {outerSum / outerCount * 1000f:F2} mm / ペチコートと組になった粒子 {paired} / 体まで測った粒子 {through}");
                Assert.That(paired, Is.GreaterThan(outerCount * 0.95f), "ペチコートと組になっていない粒子が多い");
                Assert.That(through, Is.GreaterThan(paired * 0.95f), "ペチコートを通り越して測れていない");
                float restGapSum = 0f, restGapMin = float.MaxValue;
                for (int k = 0; k < contacts.Count; k++)
                {
                    Assert.That(particleIsland[contacts.Outer[k]], Is.EqualTo(outerIsland));
                    Assert.That(particleIsland[contacts.Inner[k]], Is.EqualTo(petticoatIsland));
                    restGapSum += contacts.RestGaps[k];
                    restGapMin = Mathf.Min(restGapMin, contacts.RestGaps[k]);
                }
                // ポーズで少し形が変わり、相手の粒子は当たった所に一番近い粒子なので、元の隙間は 3mm から少しずれる
                Debug.Log($"[GP02] スカートとペチコートの元の隙間: 平均 {restGapSum / contacts.Count * 1000f:F2} mm / 最小 {restGapMin * 1000f:F2} mm");
                Assert.That(restGapSum / contacts.Count, Is.EqualTo(TestClothMenu.PetticoatGap).Within(0.0005f));
                Assert.That(restGapMin, Is.GreaterThan(0f));

                // ペチコート：前（+z）と後ろの、ウエストに近い粒子は、胴体に当たる。胴体の半径から距離を求めて比べる
                int petticoatCount = 0;
                float worstError = 0f, gapSum = 0f;
                for (int i = 0; i < gaps.Length; i++)
                {
                    if (particleIsland[i] != petticoatIsland) continue;
                    if (Mathf.Abs(rest[i].X) > 0.015f || rest[i].Y < 0.74f) continue;

                    // 内向きに少しずつ進めて、胴体の半径の内側に入る距離を探す
                    NVector3 direction = -NVector3.Normalize(normals[i]);
                    float expectedDistance = -1f;
                    for (float d = 0f; d < 0.2f; d += 0.0001f)
                    {
                        NVector3 q = rest[i] + direction * d;
                        if (q.Y < 0.725f) break;   // 胴体の底（0.72m）より下に出る向きは、胴体に当たらない（輪郭の式が使えない）
                        if (Mathf.Sqrt(q.X * q.X + q.Z * q.Z) <= TorsoRadius(q.Y))
                        {
                            expectedDistance = d;
                            break;
                        }
                    }
                    if (expectedDistance < 0f) continue;
                    Assert.That(expectedDistance, Is.GreaterThan(0.01f));
                    float expected = expectedDistance - RestGapMeasurer.Margin;
                    petticoatCount++;
                    gapSum += gaps[i];
                    worstError = Mathf.Max(worstError, Mathf.Abs(gaps[i] - expected));
                }
                Assert.That(petticoatCount, Is.GreaterThan(10));
                Debug.Log($"[GP02] ペチコートの隙間（前後のウエスト付近 {petticoatCount} 粒子）: 平均 {gapSum / petticoatCount * 1000f:F1} mm / 胴体までの距離との最大の差 {worstError * 1000f:F2} mm");
                // 胴体は 24 角形なので、辺の中央は半径より最大 0.9mm 内側。ポーズでスカートの形もわずかに変わる
                Assert.That(worstError, Is.LessThan(0.004f));
                Assert.That(gapSum / petticoatCount, Is.GreaterThan(0.02f));
            }
        }

        // ---- 単純な形（円筒の体と、スカートの円錐台） ----

        // 上から下へ向かう輪を積んだ筒。外向きが表になる向きで三角形を作る（TestClothMenu と同じ）
        Mesh CreateTube(string name, float topRadius, float bottomRadius, float topY, float bottomY, int segments, int rows)
        {
            var vertices = new List<Vector3>();
            for (int k = 0; k <= rows; k++)
            {
                float t = (float)k / rows;
                float radius = Mathf.Lerp(topRadius, bottomRadius, t);
                float y = Mathf.Lerp(topY, bottomY, t);
                for (int i = 0; i <= segments; i++)
                {
                    float angle = 2f * Mathf.PI * i / segments;
                    float c = i == segments ? 1f : Mathf.Cos(angle), s = i == segments ? 0f : Mathf.Sin(angle);
                    vertices.Add(new Vector3(radius * c, y, radius * s));
                }
            }
            var triangles = new List<int>();
            int ring = segments + 1;
            for (int k = 0; k < rows; k++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = k * ring + i, b = a + 1, c = a + ring, d = c + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(b); triangles.Add(d); triangles.Add(c);
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return Track(mesh);
        }

        MeshRenderer AddMeshRenderer(Transform parent, string name, Mesh mesh)
        {
            var go = Track(new GameObject(name));
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        // 体（円筒）とスカート（筒）を、位置と向きを付けたルートの下に作る
        GameObject CreateTubeScene(float bodyRadius, float skirtTopRadius, float skirtBottomRadius, out MeshRenderer body, out MeshRenderer skirt)
        {
            var root = Track(new GameObject("GapRoot"));
            root.transform.SetPositionAndRotation(new Vector3(1f, 2f, -0.5f), Quaternion.Euler(0f, 40f, 0f));
            body = AddMeshRenderer(root.transform, "Body", CreateTube("GapBody", bodyRadius, bodyRadius, 1.3f, 0f, 48, 60));
            skirt = AddMeshRenderer(root.transform, "Skirt", CreateTube("GapSkirt", skirtTopRadius, skirtBottomRadius, 1.0f, 0.55f, 96, 45));
            return root;
        }

        [Test]
        public void StraightSkirtOverBody_GapIsDistanceToBodyMinusMargin()
        {
            CreateTubeScene(0.10f, 0.15f, 0.15f, out MeshRenderer body, out MeshRenderer skirt);
            using (var session = new ClothEditSession(skirt))
            {
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(1));
                Assert.That(session.RestGap.OtherTriangleCount, Is.GreaterThan(0));
                // 体は半径 10cm の 48 角形（辺の中央は約 0.2mm 内側）、スカートは半径 15cm（96 角形）。水平に 5cm
                float expected = 0.05f - RestGapMeasurer.Margin;
                float[] gaps = session.RestAllowance;
                int hits = 0;
                for (int i = 0; i < gaps.Length; i++)
                {
                    if (session.Cloth.Model.RestNormals[i] == NVector3.Zero) continue;
                    Assert.That(gaps[i], Is.EqualTo(expected).Within(0.0015f), $"粒子 {i}");
                    hits++;
                }
                Assert.That(hits, Is.GreaterThan(session.ParticleCount / 2));
                Assert.That(session.RestGap.Mean, Is.EqualTo(expected).Within(0.001f));
            }
        }

        [Test]
        public void GapIsMaxWhenNothingIsInside_AndIgnoresDisabledOrOtherAvatarRenderers()
        {
            GameObject root = CreateTubeScene(0.10f, 0.15f, 0.15f, out MeshRenderer body, out MeshRenderer skirt);

            // 体が無効 → 何にも当たらないので、上限
            body.enabled = false;
            using (var session = new ClothEditSession(skirt))
            {
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(0));
                Assert.That(session.RestGap.HitCount, Is.EqualTo(0));
                Assert.That(session.RestGap.Mean, Is.EqualTo(RestGapMeasurer.MaxGap).Within(1e-6f));
                Assert.That(session.RestGap.Max, Is.EqualTo(RestGapMeasurer.MaxGap).Within(1e-6f));
            }

            // 体が非アクティブ → 同じ
            body.enabled = true;
            body.gameObject.SetActive(false);
            using (var session = new ClothEditSession(skirt))
            {
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(0));
                Assert.That(session.RestGap.Mean, Is.EqualTo(RestGapMeasurer.MaxGap).Within(1e-6f));
            }

            // 別のアバター（別のルート）の体は対象外
            body.gameObject.SetActive(true);
            body.transform.SetParent(null, true);
            using (var session = new ClothEditSession(skirt))
            {
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(0));
                Assert.That(session.RestGap.Mean, Is.EqualTo(RestGapMeasurer.MaxGap).Within(1e-6f));
            }
        }

        [Test]
        public void InnerLayerOfSameMeshCounts_AndOwnIslandIsSkipped()
        {
            // 同じメッシュの内側の筒（半径 12cm）と外側の筒（半径 15cm）を1つのメッシュにまとめる
            Mesh inner = CreateTube("GapInner", 0.12f, 0.12f, 1.0f, 0.55f, 96, 45);
            Mesh outer = CreateTube("GapOuter", 0.15f, 0.15f, 1.0f, 0.55f, 96, 45);
            var combine = new CombineInstance[] { new CombineInstance { mesh = inner }, new CombineInstance { mesh = outer } };
            Mesh both = Track(new Mesh { name = "GapBoth" });
            both.CombineMeshes(combine, true, false);

            var go = Track(new GameObject("GapLayers"));
            go.AddComponent<MeshFilter>().sharedMesh = both;
            var renderer = go.AddComponent<MeshRenderer>();
            using (var session = new ClothEditSession(renderer))
            {
                Assert.That(session.Cloth.Topology.IslandCount, Is.EqualTo(2));
                Assert.That(session.RestGap.OtherRendererCount, Is.EqualTo(0));

                // 外側の筒の粒子は、内側の筒までの 3cm - 余白。内側の筒の粒子は、自分の島の裏側には当たらず、何にも当たらないので上限
                float expected = 0.03f - RestGapMeasurer.Margin;
                int outerChecked = 0, innerChecked = 0;
                for (int i = 0; i < session.ParticleCount; i++)
                {
                    NVector3 p = session.Cloth.Model.RestPositions[i];
                    if (session.Cloth.Model.RestNormals[i] == NVector3.Zero) continue;
                    float radius = Mathf.Sqrt(p.X * p.X + p.Z * p.Z);
                    if (radius > 0.14f)
                    {
                        Assert.That(session.RestAllowance[i], Is.EqualTo(expected).Within(0.0015f), $"外側の粒子 {i}");
                        outerChecked++;
                    }
                    else
                    {
                        // 内側の筒の内向きの先は、自分の島の反対側（除く）と、その先の外側の筒（27cm 以上先）なので、上限
                        Assert.That(session.RestAllowance[i], Is.EqualTo(RestGapMeasurer.MaxGap).Within(1e-6f), $"内側の粒子 {i}");
                        innerChecked++;
                    }
                }
                Assert.That(outerChecked, Is.GreaterThan(100));
                Assert.That(innerChecked, Is.GreaterThan(100));
            }
        }

        // 体から離れたふんわりしたスカート。上端をピン留めし、裾の前をつまんで体の方へ引く。
        // 隙間を使わないと元の形より内側へ動けず、布が伸びる
        float PullHemInward(bool useGap, out float maxInward, out float gapMean)
        {
            // 体は半径 5cm、スカートは上端の半径 15cm・裾の半径 40cm。体までは 10cm 以上離れている
            CreateTubeScene(0.05f, 0.15f, 0.40f, out MeshRenderer body, out MeshRenderer skirt);
            using (var session = new ClothEditSession(skirt))
            {
                gapMean = session.RestGap.Mean;
                var settings = new ClothEditSettings { UseRestSurfaceAllowance = useGap };
                settings.ApplyTo(session.Solver, session.RestAllowance);
                session.Solver.InfluenceRadius = 0.2f;

                float top = float.MinValue;
                NVector3[] rest = session.Cloth.Model.RestPositions;
                foreach (NVector3 p in rest) top = Mathf.Max(top, p.Y);
                for (int i = 0; i < rest.Length; i++)
                {
                    if (rest[i].Y > top - session.Cloth.Spacing) session.Solver.SetPinned(i, true);
                }

                // 裾の前（スカートのローカルの +z 側）の外から、軸に向かうレイでつまむ。ワールドへ直す
                Transform t = skirt.transform;
                var ray = new Ray(t.TransformPoint(new Vector3(0f, 0.6f, 1f)), t.TransformDirection(Vector3.back));
                Assert.That(session.Raycast(ray, out Vector3 hit, out Vector3 normal, out int triangle), Is.True);
                Assert.That(session.BeginGrab(hit, 0.05f, triangle), Is.True);
                Vector3 inward = -normal;
                Vector3 target = hit + inward * 0.15f;
                for (int f = 1; f <= 10; f++)
                {
                    session.DragTo(Vector3.Lerp(hit, target, f / 10f));
                    session.Solver.Solve(20);
                }
                session.Solver.Settle(0.01f, 400, out _);
                float stretch = session.Solver.MeasureMaxStretch();

                // つまんだ範囲以外が、元の形の面より内側へ動いた最大の深さ
                maxInward = 0f;
                ReadOnlySpan<NVector3> now = session.Solver.Positions;
                NVector3[] normals = session.Cloth.Model.RestNormals;
                for (int i = 0; i < rest.Length; i++)
                {
                    if (session.Solver.IsGrabbed(i)) continue;
                    maxInward = Mathf.Max(maxInward, -NVector3.Dot(now[i] - rest[i], normals[i]));
                }
                return stretch;
            }
        }

        [Test]
        public void UsingGap_LetsClothMoveInward_UpToTheGap()
        {
            float stretchWithout = PullHemInward(false, out float inwardWithout, out float gapMean);
            DestroyCreated();
            float stretchWith = PullHemInward(true, out float inwardWith, out _);

            Debug.Log($"[GP02] 隙間なし: 伸び {stretchWithout * 100f:F1} %、内側へ動いた最大 {inwardWithout * 1000f:F1} mm / " +
                $"隙間あり: 伸び {stretchWith * 100f:F1} %、内側へ動いた最大 {inwardWith * 1000f:F1} mm（隙間の平均 {gapMean * 1000f:F1} mm）");
            Assert.That(gapMean, Is.GreaterThan(0.05f), "体から離れているので、隙間は大きい");
            Assert.That(inwardWithout, Is.LessThan(0.003f), "隙間を使わなければ、元の形より内側へはほとんど入らない");
            Assert.That(inwardWith, Is.GreaterThan(inwardWithout + 0.005f), "隙間を使うと内側へ動ける");
            Assert.That(inwardWith, Is.LessThan(gapMean + 0.01f), "隙間より深くは入らない");
            // つまんだ範囲の目標も、入ってよい深さで止まる（体の中まで連れて行かない）ので、どちらも大きくは伸びない
            Assert.That(stretchWithout, Is.LessThan(0.08f), "内側へ引いても、目標が元の形の外に保たれる");
            Assert.That(stretchWith, Is.LessThan(0.15f), "隙間の分だけ内側へ動いても、大きくは伸びない");
        }

        void DestroyCreated()
        {
            foreach (Object o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        // ---- 設定との関係 ----

        [Test]
        public void ApplyTo_PassesAllowanceOnlyWhenEnabled_AndSetsBrushCompliance()
        {
            ProceduralMeshes.Grid(4, 4, 0.1f, 0.1f, out var vertices, out var triangles);
            var cloth = ParticleCloth.Build(vertices, triangles);
            ClothSolver solver = cloth.CreateSolver();

            // 渡した配列の長さが粒子数と合わなければ、ソルバーが拒否する（隙間を使うときだけ渡す）
            var settings = new ClothEditSettings { BrushCompliance = 0.07f, UseRestSurfaceAllowance = true };
            Assert.Throws<ArgumentException>(() => settings.ApplyTo(solver, new float[3]));

            var gaps = new float[cloth.Model.ParticleCount];
            settings.ApplyTo(solver, gaps);
            Assert.That(solver.BrushCompliance, Is.EqualTo(0.07f));

            // オフなら配列が合っていなくても渡さない（null にする）
            settings.UseRestSurfaceAllowance = false;
            Assert.DoesNotThrow(() => settings.ApplyTo(solver, new float[3]));
            Assert.DoesNotThrow(() => settings.ApplyTo(solver));
        }

        [Test]
        public void Session_StartsWithItsOwnGap_AndRebuildMeasuresAgain()
        {
            GameObject root = CreateTestCloth();
            SkinnedMeshRenderer skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
            using (var first = new ClothEditSession(skirt))
            {
                var rebased = first.Cloth.Rebase(first.Solver.Positions);
                using (var next = new ClothEditSession(skirt, rebased))
                {
                    Assert.That(next.RestGap, Is.Not.Null);
                    Assert.That(next.RestAllowance, Is.Not.SameAs(first.RestAllowance), "作り直しのたびに測り直す");
                    Assert.That(next.RestAllowance.Length, Is.EqualTo(next.ParticleCount));
                    Assert.That(next.RestGap.Mean, Is.EqualTo(first.RestGap.Mean).Within(1e-4f));
                }
            }
        }
    }
}
