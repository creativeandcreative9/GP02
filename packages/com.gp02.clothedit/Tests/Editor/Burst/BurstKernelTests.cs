using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using Debug = UnityEngine.Debug;

namespace GP02.Tests
{
    /// <summary>
    /// Burst 版のカーネルが、C# 版（Kernel = null）と同じ結果になること、十分速いことを確かめる。
    /// このアセンブリは com.unity.burst が入っているときだけコンパイルされる。
    /// </summary>
    public class BurstKernelTests
    {
        const int GridCells = 100;

        static ParticleCloth BuildCloth(int cells, float size, int coarseLevels = ParticleCloth.DefaultCoarseLevels)
        {
            ProceduralMeshes.Grid(cells, cells, size, size, out var vertices, out var triangles);
            return ParticleCloth.Build(vertices, triangles, coarseLevels: coarseLevels);
        }

        // 上端をピン留めし、下寄りの中心をつまむ（tools/bench と同じ配置）
        static ClothSolver CreateSolver(ParticleCloth cloth, float size, IClothSolverKernel kernel, out int center)
        {
            var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { InfluenceRadius = 0.15f, Kernel = kernel };
            for (int p = 0; p < cloth.Model.ParticleCount; p++)
            {
                if (cloth.Model.RestPositions[p].Y > size - cloth.Spacing) solver.SetPinned(p, true);
            }
            center = solver.FindNearestParticle(new Vector3(size / 2f, size * 0.5f, 0f));
            return solver;
        }

        // つまむ柔らかさは既定（柔らかいつまみ）と、0（つまんだ範囲を固定）、大きめの値の3通り
        [TestCase(0, ClothSolver.DefaultBrushCompliance)]
        [TestCase(2, ClothSolver.DefaultBrushCompliance)]
        [TestCase(2, 0f)]
        [TestCase(2, 0.1f)]
        public void BurstKernel_MatchesCSharpResult(int coarseLevels, float brushCompliance)
        {
            const float size = 0.3f;
            ParticleCloth cloth = BuildCloth(20, size, coarseLevels);

            Vector3[] expected = Operate(cloth, size, null, brushCompliance);
            Vector3[] actual = Operate(cloth, size, new BurstClothSolverKernel(), brushCompliance);

            float moved = 0f, maxDifference = 0f;
            for (int i = 0; i < expected.Length; i++)
            {
                moved = System.Math.Max(moved, Vector3.Distance(expected[i], cloth.Model.RestPositions[i]));
                maxDifference = System.Math.Max(maxDifference, Vector3.Distance(expected[i], actual[i]));
            }
            Debug.Log($"[GP02] Burst と C# の差（粗い段 {coarseLevels}、つまむ柔らかさ {brushCompliance}）: 最大 {maxDifference:E2} m（粒子 {expected.Length}、最大の移動 {moved:F3} m）");
            Assert.That(moved, Is.GreaterThan(0.01f), "操作で動いていません");
            Assert.That(maxDifference, Is.LessThan(1e-4f));
        }

        // 曲がった布（テスト用のスカート）では、元の形より内側に入らない制約が効く。その状態でも C# と一致すること。
        // useAllowance なら、粒子ごとに違う「入ってよい深さ」（0〜1.2cm）も入れる
        [TestCase(false)]
        [TestCase(true)]
        public void BurstKernel_MatchesCSharpResult_WithRestSurface(bool useAllowance)
        {
            UnityEngine.Mesh mesh = TestClothMenu.CreateSkirtMesh();
            var cloth = ParticleCloth.Build(MeshBridge.ToNumerics(mesh.vertices, UnityEngine.Vector3.one), mesh.triangles);
            UnityEngine.Object.DestroyImmediate(mesh);
            int withNormals = 0;
            foreach (Vector3 n in cloth.Model.RestNormals) if (n != Vector3.Zero) withNormals++;
            Assert.That(withNormals, Is.GreaterThan(cloth.Model.ParticleCount / 2), "スカートで外向きが決まっていません");

            Vector3[] Run(IClothSolverKernel kernel, bool allowance)
            {
                var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = kernel };
                if (allowance)
                {
                    var depths = new float[cloth.Model.ParticleCount];
                    for (int i = 0; i < depths.Length; i++) depths[i] = 0.002f * (i % 7);
                    solver.SetRestSurfaceAllowance(depths);
                }
                float top = float.MinValue;
                foreach (Vector3 p in cloth.Model.RestPositions) top = System.Math.Max(top, p.Y);
                for (int i = 0; i < cloth.Model.ParticleCount; i++)
                {
                    if (cloth.Model.RestPositions[i].Y > top - cloth.Spacing) solver.SetPinned(i, true);
                }
                // 前側（-Z）の裾寄りを手前へ引く。横の布が内側へ引き込まれ、制約が働く
                int grabbed = solver.FindNearestParticle(new Vector3(0f, top - 0.25f, -0.3f));
                Vector3 start = solver.Positions[grabbed];
                for (int f = 1; f <= 5; f++)
                {
                    solver.Grab(grabbed, start + new Vector3(0f, 0.02f, -0.08f) * f / 5f, 0.04f);
                    solver.Solve(10);
                }
                return solver.Positions.ToArray();
            }

            Vector3[] expected = Run(null, useAllowance);
            Vector3[] actual = Run(new BurstClothSolverKernel(), useAllowance);
            float maxDifference = 0f;
            for (int i = 0; i < expected.Length; i++) maxDifference = System.Math.Max(maxDifference, Vector3.Distance(expected[i], actual[i]));
            Debug.Log($"[GP02] Burst と C# の差（スカート、内側に入らない制約あり、入ってよい深さ {(useAllowance ? "あり" : "なし")}）: 最大 {maxDifference:E2} m");
            Assert.That(maxDifference, Is.LessThan(1e-4f));

            // 入ってよい深さが結果に効いていること（効かないテストにならないように）
            if (useAllowance)
            {
                Vector3[] without = Run(null, false);
                float effect = 0f;
                for (int i = 0; i < expected.Length; i++) effect = System.Math.Max(effect, Vector3.Distance(expected[i], without[i]));
                Debug.Log($"[GP02] 入ってよい深さの有無による結果の差: 最大 {effect:E2} m");
                Assert.That(effect, Is.GreaterThan(1e-4f));
            }
        }

        // 一般化したブラシ（回転・拡大縮小、なめらかな重み、対称、距離の測り方）でも C# と一致すること。
        // テスト用のスカート（左右対称、ペチコートの層つき）の左前をつまみ、x = 0 の面で対称にする
        [TestCase(BrushDistanceMode.Surface, ClothSolver.DefaultBrushCompliance)]
        [TestCase(BrushDistanceMode.Linear, ClothSolver.DefaultBrushCompliance)]
        [TestCase(BrushDistanceMode.Linear, 0f)]
        public void BurstKernel_MatchesCSharpResult_WithGeneralBrush(BrushDistanceMode mode, float brushCompliance)
        {
            UnityEngine.Mesh mesh = TestClothMenu.CreateSkirtMesh();
            var cloth = ParticleCloth.Build(MeshBridge.ToNumerics(mesh.vertices, UnityEngine.Vector3.one), mesh.triangles);
            UnityEngine.Object.DestroyImmediate(mesh);

            Vector3[] Run(IClothSolverKernel kernel, out int mirror, out int grabbedCount)
            {
                var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = kernel, BrushCompliance = brushCompliance };
                float top = float.MinValue;
                foreach (Vector3 p in cloth.Model.RestPositions) top = System.Math.Max(top, p.Y);
                for (int i = 0; i < cloth.Model.ParticleCount; i++)
                {
                    if (cloth.Model.RestPositions[i].Y > top - cloth.Spacing) solver.SetPinned(i, true);
                }
                int grabbed = solver.FindNearestParticle(new Vector3(-0.2f, top - 0.25f, -0.2f));
                Vector3 pivot = solver.Positions[grabbed];
                var options = new BrushOptions
                {
                    Radius = 0.04f,
                    Falloff = FalloffCurve.Smooth,
                    DistanceMode = mode,
                    Mirror = new MirrorPlane(Vector3.Zero, Vector3.UnitX),
                };
                for (int f = 1; f <= 5; f++)
                {
                    var motion = new BrushMotion(new Vector3(0.01f, 0.02f, -0.05f) * f / 5f,
                        Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f * f / 5f), 1f - 0.1f * f / 5f, pivot);
                    solver.Grab(grabbed, motion, options);
                    solver.Solve(10);
                }
                mirror = solver.MirrorParticle;
                grabbedCount = solver.BrushParticleCount;
                return solver.Positions.ToArray();
            }

            Vector3[] expected = Run(null, out int mirrorParticle, out int count);
            Vector3[] actual = Run(new BurstClothSolverKernel(), out _, out _);
            float moved = 0f, maxDifference = 0f;
            for (int i = 0; i < expected.Length; i++)
            {
                moved = System.Math.Max(moved, Vector3.Distance(expected[i], cloth.Model.RestPositions[i]));
                maxDifference = System.Math.Max(maxDifference, Vector3.Distance(expected[i], actual[i]));
            }
            Debug.Log($"[GP02] Burst と C# の差（一般化したブラシ、{mode}、つまむ柔らかさ {brushCompliance}）: 最大 {maxDifference:E2} m（つまんだ粒子 {count}、最大の移動 {moved:F3} m）");
            Assert.That(mirrorParticle, Is.GreaterThanOrEqualTo(0), "鏡像の粒子が見つかりません");
            Assert.That(moved, Is.GreaterThan(0.01f), "操作で動いていません");
            Assert.That(maxDifference, Is.LessThan(1e-4f));
        }

        // 体との平面（貫通を直す・直したあとに内へ押す）でも C# と一致すること。体はテスト用の体に Hip_Big を 100 で入れた形（ポーズなし）
        [Test]
        public void BurstKernel_MatchesCSharpResult_WithBodyPlanes()
        {
            UnityEngine.Mesh skirtMesh = TestClothMenu.CreateSkirtMesh();
            var cloth = ParticleCloth.Build(MeshBridge.ToNumerics(skirtMesh.vertices, UnityEngine.Vector3.one), skirtMesh.triangles);
            UnityEngine.Object.DestroyImmediate(skirtMesh);
            UnityEngine.Mesh bodyMesh = TestClothMenu.CreateBodyMesh();
            var deltas = new UnityEngine.Vector3[bodyMesh.vertexCount];
            bodyMesh.GetBlendShapeFrameVertices(bodyMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName), 0, deltas, null, null);
            UnityEngine.Vector3[] bodyVertices = bodyMesh.vertices;
            for (int i = 0; i < bodyVertices.Length; i++) bodyVertices[i] += deltas[i];
            var body = new BodySurface(MeshBridge.ToNumerics(bodyVertices, UnityEngine.Vector3.one), bodyMesh.triangles);
            UnityEngine.Object.DestroyImmediate(bodyMesh);
            body.ClassifyIslands(cloth.Model.RestPositions);

            // 「貫通を直す」は収束の判定で反復の回数が変わるので、ここでは反復の回数を決めて解く
            Vector3[] Run(IClothSolverKernel kernel, out int planes, out int cleared)
            {
                var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = kernel };
                solver.SetBody(body);
                var clear = new List<int>();
                for (int i = 0; i < cloth.Model.ParticleCount; i++)
                {
                    if (solver.GetBodyMinDistance(i) < solver.BodyMargin - 1e-6f) clear.Add(i);
                }
                cleared = clear.Count;
                solver.RequireBodyClearance(clear);
                solver.Solve(60);
                // 直したあとに、後ろの腰の布を内へ押す（体との平面とつまんだ範囲の引き寄せが同時に効く）
                int grabbed = solver.FindNearestParticle(new Vector3(0f, 0.755f, 0.17f));
                Vector3 start = solver.Positions[grabbed];
                for (int f = 1; f <= 5; f++)
                {
                    solver.Grab(grabbed, start + new Vector3(0f, 0f, -0.03f) * f / 5f, 0.03f);
                    solver.Solve(10);
                }
                planes = solver.BodyPlaneCount;
                return solver.Positions.ToArray();
            }

            Vector3[] expected = Run(null, out int planeCount, out int clearedCount);
            Vector3[] actual = Run(new BurstClothSolverKernel(), out _, out _);
            float moved = 0f, maxDifference = 0f;
            for (int i = 0; i < expected.Length; i++)
            {
                moved = System.Math.Max(moved, Vector3.Distance(expected[i], cloth.Model.RestPositions[i]));
                maxDifference = System.Math.Max(maxDifference, Vector3.Distance(expected[i], actual[i]));
            }
            Debug.Log($"[GP02] Burst と C# の差（体との平面 {planeCount}、押し出す粒子 {clearedCount}）: 最大 {maxDifference:E2} m（最大の移動 {moved:F3} m）");
            Assert.That(clearedCount, Is.GreaterThan(20), "体に入っている粒子がありません");
            Assert.That(planeCount, Is.GreaterThan(0), "体との平面が使われていません");
            Assert.That(moved, Is.GreaterThan(0.005f), "操作で動いていません");
            Assert.That(maxDifference, Is.LessThan(1e-4f));
        }

        // ピン留め → つまんで数コマ引っ張る → 仕上げ → 離す
        static Vector3[] Operate(ParticleCloth cloth, float size, IClothSolverKernel kernel, float brushCompliance = ClothSolver.DefaultBrushCompliance)
        {
            ClothSolver solver = CreateSolver(cloth, size, kernel, out int center);
            solver.BrushCompliance = brushCompliance;
            Vector3 start = solver.Positions[center];
            for (int f = 1; f <= 5; f++)
            {
                solver.Grab(center, start + new Vector3(0f, 0.01f, 0.08f) * f / 5f, 0.02f);
                solver.Solve(5);
            }
            solver.Settle(0.02f, 40, out _);
            solver.Release();
            solver.Solve(10);
            return solver.Positions.ToArray();
        }

        // 曲がった布（テスト用のスカート）を引っ張った粒子の位置から、まとめた頂点の位置と元の頂点への展開が C# 版と一致すること
        [Test]
        public void BurstKernel_DeformWeldedAndExpand_MatchCSharp()
        {
            UnityEngine.Mesh mesh = TestClothMenu.CreateSkirtMesh();
            Vector3[] baseVertices = MeshBridge.ToNumerics(mesh.vertices, UnityEngine.Vector3.one);
            var cloth = ParticleCloth.Build(baseVertices, mesh.triangles);
            UnityEngine.Object.DestroyImmediate(mesh);

            var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { Kernel = null, InfluenceRadius = 0.15f };
            float top = float.MinValue;
            foreach (Vector3 p in cloth.Model.RestPositions) top = System.Math.Max(top, p.Y);
            for (int i = 0; i < cloth.Model.ParticleCount; i++)
            {
                if (cloth.Model.RestPositions[i].Y > top - cloth.Spacing) solver.SetPinned(i, true);
            }
            int grabbed = solver.FindNearestParticle(new Vector3(0f, top - 0.25f, -0.3f));
            solver.Grab(grabbed, solver.Positions[grabbed] + new Vector3(0f, 0.02f, -0.08f), 0.04f);
            solver.Solve(10);
            Vector3[] particles = solver.Positions.ToArray();

            var burst = new BurstClothSolverKernel();
            var expectedWelded = new Vector3[cloth.Topology.Positions.Length];
            var actualWelded = new Vector3[expectedWelded.Length];
            cloth.DeformWelded(particles, expectedWelded, null);
            cloth.DeformWelded(particles, actualWelded, burst);

            // 元の頂点（まとめる前）への展開。継ぎ目の頂点が同じ位置に2つある
            int[] map = cloth.Topology.VertexToWelded;
            var expectedVertices = new Vector3[map.Length];
            var actualVertices = new Vector3[map.Length];
            ShapeKernels.ExpandVertices(null, expectedVertices, baseVertices, expectedWelded, cloth.Topology.Positions, map);
            ShapeKernels.ExpandVertices(burst, actualVertices, baseVertices, actualWelded, cloth.Topology.Positions, map);

            float moved = 0f, weldedDifference = 0f, vertexDifference = 0f;
            for (int w = 0; w < expectedWelded.Length; w++)
            {
                moved = System.Math.Max(moved, Vector3.Distance(expectedWelded[w], cloth.Topology.Positions[w]));
                weldedDifference = System.Math.Max(weldedDifference, Vector3.Distance(expectedWelded[w], actualWelded[w]));
            }
            for (int v = 0; v < map.Length; v++) vertexDifference = System.Math.Max(vertexDifference, Vector3.Distance(expectedVertices[v], actualVertices[v]));
            Debug.Log($"[GP02] Burst と C# の差（形の更新）: まとめた頂点 最大 {weldedDifference:E2} m、元の頂点 最大 {vertexDifference:E2} m（頂点 {map.Length}、最大の移動 {moved:F3} m）");
            Assert.That(map.Length, Is.GreaterThan(expectedWelded.Length), "継ぎ目で頂点がまとまっていません");
            Assert.That(moved, Is.GreaterThan(0.01f), "操作で動いていません");
            Assert.That(weldedDifference, Is.LessThan(1e-5f));
            Assert.That(vertexDifference, Is.LessThan(1e-5f));
        }

        // 頂点を大きく動かしたあとの境界箱の更新が、C# 版と完全に一致し、Raycast の結果も変わらないこと
        [Test]
        public void BurstKernel_RefitBvh_MatchesCSharp()
        {
            UnityEngine.Mesh mesh = TestClothMenu.CreateSkirtMesh();
            Vector3[] vertices = MeshBridge.ToNumerics(mesh.vertices, UnityEngine.Vector3.one);
            int[] triangles = mesh.triangles;
            UnityEngine.Object.DestroyImmediate(mesh);
            var viaCSharp = new TriangleBvh(vertices, triangles);
            var viaBurst = new TriangleBvh(vertices, triangles);

            var random = new System.Random(3);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.05f;
            }
            viaCSharp.Refit(vertices, null);
            viaBurst.Refit(vertices, new BurstClothSolverKernel());

            Assert.That(viaBurst.NodeCount, Is.EqualTo(viaCSharp.NodeCount));
            for (int node = 0; node < viaCSharp.NodeCount; node++)
            {
                viaCSharp.GetNodeBox(node, out Vector3 expectedMin, out Vector3 expectedMax);
                viaBurst.GetNodeBox(node, out Vector3 actualMin, out Vector3 actualMax);
                Assert.That(actualMin, Is.EqualTo(expectedMin), $"ノード {node} の最小");
                Assert.That(actualMax, Is.EqualTo(expectedMax), $"ノード {node} の最大");
            }

            int hits = 0;
            for (int r = 0; r < 300; r++)
            {
                var origin = new Vector3((float)random.NextDouble() * 3f - 1.5f, (float)random.NextDouble() * 2f - 0.5f, (float)random.NextDouble() * 3f - 1.5f);
                var target = new Vector3((float)random.NextDouble() * 0.6f - 0.3f, 0.45f + (float)random.NextDouble() * 0.35f, (float)random.NextDouble() * 0.6f - 0.3f);
                Vector3 direction = Vector3.Normalize(target - origin);
                bool expected = viaCSharp.Raycast(origin, direction, vertices, out float expectedDistance, out int expectedTriangle);
                bool actual = viaBurst.Raycast(origin, direction, vertices, out float distance, out int triangle);
                Assert.That(actual, Is.EqualTo(expected), $"レイ {r}");
                if (!expected) continue;
                hits++;
                Assert.That(distance, Is.EqualTo(expectedDistance), $"レイ {r}");
                Assert.That(triangle, Is.EqualTo(expectedTriangle), $"レイ {r}");
            }
            Assert.That(hits, Is.GreaterThan(30), "当たったレイが少なすぎます");
        }

        // セッション全体で、Kernel を Burst にしたときと null にしたときで、形（CurrentWorldVertices）とクリック判定が同じこと
        [Test]
        public void BurstKernel_SessionShapeUpdateAndRaycast_MatchCSharp()
        {
            UnityEngine.Mesh mesh = TestClothMenu.CreateSkirtMesh();
            UnityEngine.Vector3[] rest = mesh.vertices;
            var go = new UnityEngine.GameObject("GP02_BurstSessionTest");
            go.AddComponent<UnityEngine.MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<UnityEngine.MeshRenderer>();
            try
            {
                using (var session = new ClothEditSession(renderer))
                {
                    session.Solver.Kernel = new BurstClothSolverKernel();
                    var ray = new UnityEngine.Ray(new UnityEngine.Vector3(0f, 0.6f, -2f), UnityEngine.Vector3.forward);
                    Assert.That(session.Raycast(ray, out UnityEngine.Vector3 hit, out _, out int triangle), Is.True);
                    Assert.That(session.BeginGrab(hit, 0.05f, triangle), Is.True);
                    session.DragTo(hit + new UnityEngine.Vector3(0.02f, 0.03f, -0.06f));
                    session.Solver.SolveFor(30);

                    var rays = new UnityEngine.Ray[100];
                    var random = new System.Random(5);
                    for (int i = 0; i < rays.Length; i++)
                    {
                        var target = new UnityEngine.Vector3((float)random.NextDouble() * 0.4f - 0.2f, 0.5f + (float)random.NextDouble() * 0.25f, (float)random.NextDouble() * 0.4f - 0.2f);
                        var direction = new UnityEngine.Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() * 0.4f - 0.2f, (float)random.NextDouble() - 0.5f).normalized;
                        rays[i] = new UnityEngine.Ray(target - direction * 1.5f, direction);
                    }

                    (UnityEngine.Vector3[], bool[], UnityEngine.Vector3[], int[]) Capture()
                    {
                        session.UpdateCurrentShape();
                        var vertices = (UnityEngine.Vector3[])session.CurrentWorldVertices.Clone();
                        var found = new bool[rays.Length];
                        var points = new UnityEngine.Vector3[rays.Length];
                        var triangles = new int[rays.Length];
                        for (int i = 0; i < rays.Length; i++) found[i] = session.Raycast(rays[i], out points[i], out _, out triangles[i]);
                        return (vertices, found, points, triangles);
                    }

                    var burst = Capture();
                    session.Solver.Kernel = null;
                    var csharp = Capture();

                    float moved = 0f, difference = 0f;
                    for (int v = 0; v < burst.Item1.Length; v++)
                    {
                        moved = System.Math.Max(moved, (csharp.Item1[v] - rest[v]).magnitude);
                        difference = System.Math.Max(difference, (burst.Item1[v] - csharp.Item1[v]).magnitude);
                    }
                    int hits = 0;
                    for (int i = 0; i < rays.Length; i++)
                    {
                        Assert.That(burst.Item2[i], Is.EqualTo(csharp.Item2[i]), $"レイ {i}");
                        if (!csharp.Item2[i]) continue;
                        hits++;
                        Assert.That((burst.Item3[i] - csharp.Item3[i]).magnitude, Is.LessThan(1e-5f), $"レイ {i}");
                        Assert.That(burst.Item4[i], Is.EqualTo(csharp.Item4[i]), $"レイ {i}");
                    }
                    Debug.Log($"[GP02] Burst と C# の差（セッションの形の更新）: 最大 {difference:E2} m（最大の移動 {moved:F3} m、当たったレイ {hits}）");
                    Assert.That(moved, Is.GreaterThan(0.005f), "操作で動いていません");
                    Assert.That(difference, Is.LessThan(1e-5f));
                    Assert.That(hits, Is.GreaterThan(10));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BurstKernel_IsAccelerated()
        {
            // Jobs > Burst > Enable Compilation が切られていると、速さの確認ができない
            Assert.That(new BurstClothSolverKernel().IsAccelerated, Is.True);
            Assert.That(new BurstClothSolverKernel().Name, Is.EqualTo("Burst"));
        }

        [Test]
        public void BurstKernel_IsRegisteredAsDefault()
        {
            Assert.That(ClothSolver.DefaultKernel, Is.TypeOf<BurstClothSolverKernel>());
        }

        // 反復1回あたりの時間（ミリ秒）。Solve(5) を繰り返した平均を5で割る。粗い段の分も含む
        static double MeasureIterationMilliseconds(ParticleCloth cloth, float size, IClothSolverKernel kernel, out int particles)
        {
            ClothSolver solver = CreateSolver(cloth, size, kernel, out int center);
            particles = cloth.Model.ParticleCount;
            Vector3 start = solver.Positions[center];
            for (int f = 1; f <= 10; f++)
            {
                solver.Grab(center, start + new Vector3(0f, 0.02f, 0.1f) * f / 10f, 0.03f);
                solver.Solve(5);   // 最初の呼び出しには、ピンの距離の計算や Burst のコンパイルが含まれる
            }

            const int repeats = 20;
            var timer = Stopwatch.StartNew();
            for (int r = 0; r < repeats; r++) solver.Solve(5);
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds / (repeats * 5);
        }

        [Test]
        public void BurstKernel_IsFasterThanCSharp()
        {
            var log = new StringBuilder("[GP02] 反復1回の時間（Solve(5) の平均 / 5。粗い段込み）\n");
            foreach (float size in new[] { 0.63f, 1f })
            {
                ParticleCloth cloth = BuildCloth(GridCells, size);
                double csharp = MeasureIterationMilliseconds(cloth, size, null, out int particles);
                double burst = MeasureIterationMilliseconds(cloth, size, new BurstClothSolverKernel(), out _);
                log.AppendLine($"  布 {size} m（粒子 {particles}）: C# {csharp:F3} ms / Burst {burst:F3} ms / {csharp / burst:F1} 倍");
                Assert.That(burst * 2.0, Is.LessThan(csharp), $"布 {size} m で Burst が C# の2倍以上速くありません");
            }
            Debug.Log(log.ToString());
        }

        // 毎コマ 12ms の使い方：ドラッグ 20 コマを SolveFor(12)、離さずに SolveFor(12) を繰り返して仕上げる
        [Test]
        public void BurstKernel_FrameBudgetScenario_LogsDragAndSettleTimes()
        {
            const double frameMilliseconds = 12.0;
            const double settleLimitMilliseconds = 5000.0;
            var log = new StringBuilder("[GP02] 毎コマ 12ms でのドラッグと仕上げ（ドラッグ 20 コマ → 手を止めて仕上げ）\n" +
                "  布 / 引っ張り / カーネル : ドラッグ1コマの反復回数（平均） / ドラッグ直後の最大の伸び / 10%以下まで / 5%以下まで\n");
            foreach (float size in new[] { 0.63f, 1f })
            {
                ParticleCloth cloth = BuildCloth(GridCells, size);
                foreach (float pull in new[] { 0.03f, 0.10f })
                {
                    foreach (bool burst in new[] { false, true })
                    {
                        ClothSolver solver = CreateSolver(cloth, size, burst ? new BurstClothSolverKernel() : null, out int center);
                        solver.Solve(0);   // ピンの距離の計算と Burst のコンパイルを、測定から外す
                        solver.Solve(5);
                        solver.Solve(5);
                        Vector3 start = solver.Positions[center];
                        Vector3 offset = new Vector3(0f, pull * 0.2f, pull * 0.98f);

                        int dragIterations = 0;
                        const int dragFrames = 20;
                        for (int f = 1; f <= dragFrames; f++)
                        {
                            solver.Grab(center, start + offset * f / dragFrames, 0.03f);
                            dragIterations += solver.SolveFor(frameMilliseconds);
                        }
                        float afterDrag = solver.MeasureMaxStretch();

                        double elapsed = 0.0, timeTo10 = -1.0, timeTo5 = -1.0;
                        float stretch = afterDrag;
                        if (stretch <= 0.10f) timeTo10 = 0.0;
                        if (stretch <= 0.05f) timeTo5 = 0.0;
                        while (timeTo5 < 0.0 && elapsed < settleLimitMilliseconds)
                        {
                            var timer = Stopwatch.StartNew();
                            solver.SolveFor(frameMilliseconds);
                            elapsed += timer.Elapsed.TotalMilliseconds;
                            stretch = solver.MeasureMaxStretch();
                            if (timeTo10 < 0.0 && stretch <= 0.10f) timeTo10 = elapsed;
                            if (timeTo5 < 0.0 && stretch <= 0.05f) timeTo5 = elapsed;
                        }

                        log.AppendLine($"  {size} m / {pull * 100f:F0} cm / {(burst ? "Burst" : "C#   ")} : " +
                            $"{dragIterations / (float)dragFrames:F1} 回 / {afterDrag * 100f:F1} % / {Format(timeTo10)} / {Format(timeTo5)}");
                        Assert.That(float.IsNaN(stretch), Is.False);
                    }
                }
            }
            Debug.Log(log.ToString());
        }

        static string Format(double milliseconds) => milliseconds < 0.0 ? "5秒以内に届かず" : $"{milliseconds:F0} ms";
    }
}
