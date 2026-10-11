using System.Collections.Generic;
using System.Diagnostics;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// 8万頂点前後の大きなスカート状のメッシュ（3枚重ね）で、準備と操作にかかる時間を測る。
    /// 合否は、極端に遅い場合だけ失敗にする緩い上限。測った値はログに出す。
    /// </summary>
    public class LargeGarmentTests
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

        // 円錐台（上の半径 → 下の半径）。UV の継ぎ目を模して、同じ位置の頂点を1列重ねる
        static void AddSkirt(List<Vector3> vertices, List<int> triangles, float topRadius, float bottomRadius, float length, float topY,
            int around, int rings)
        {
            int baseIndex = vertices.Count;
            int stride = around + 1;
            for (int r = 0; r <= rings; r++)
            {
                float f = r / (float)rings;
                float radius = Mathf.Lerp(topRadius, bottomRadius, f);
                float y = topY - length * f;
                for (int a = 0; a <= around; a++)
                {
                    float angle = 2f * Mathf.PI * a / around;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius));
                }
            }
            for (int r = 0; r < rings; r++)
            for (int a = 0; a < around; a++)
            {
                int i = baseIndex + r * stride + a;
                triangles.AddRange(new[] { i, i + stride, i + 1, i + 1, i + stride, i + stride + 1 });
            }
        }

        static bool BruteForceRaycast(Ray ray, Vector3[] vertices, int[] triangles, out float bestDistance)
        {
            bestDistance = float.MaxValue;
            Vector3 origin = ray.origin, direction = ray.direction;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 p0 = vertices[triangles[t]];
                Vector3 edge1 = vertices[triangles[t + 1]] - p0;
                Vector3 edge2 = vertices[triangles[t + 2]] - p0;
                Vector3 pvec = Vector3.Cross(direction, edge2);
                float det = Vector3.Dot(edge1, pvec);
                if (Mathf.Abs(det) < 1e-12f) continue;
                float invDet = 1f / det;
                Vector3 tvec = origin - p0;
                float u = Vector3.Dot(tvec, pvec) * invDet;
                if (u < 0f || u > 1f) continue;
                Vector3 qvec = Vector3.Cross(tvec, edge1);
                float v = Vector3.Dot(direction, qvec) * invDet;
                if (v < 0f || u + v > 1f) continue;
                float distance = Vector3.Dot(edge2, qvec) * invDet;
                if (distance > 0f && distance < bestDistance) bestDistance = distance;
            }
            return bestDistance < float.MaxValue;
        }

        // 半径 15〜40cm、長さ 50cm の円錐台を、少しずつ大きくして3枚重ねたもの（約8万頂点）
        Mesh CreateSkirtMesh()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddSkirt(vertices, triangles, 0.15f, 0.35f, 0.5f, 1f, 200, 130);
            AddSkirt(vertices, triangles, 0.17f, 0.375f, 0.5f, 1f, 200, 130);
            AddSkirt(vertices, triangles, 0.19f, 0.40f, 0.5f, 1f, 200, 130);

            var mesh = new Mesh { name = "Skirt80k", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _created.Add(mesh);
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        Renderer CreateRenderer(Mesh mesh)
        {
            var go = new GameObject("Skirt80k");
            _created.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        [Test]
        public void Skirt80k_MeasureSessionAndEditingTimes()
        {
            Mesh mesh = CreateSkirtMesh();
            var renderer = CreateRenderer(mesh);

            var timer = Stopwatch.StartNew();
            ClothEditSession session;
            try
            {
                session = new ClothEditSession(renderer);
            }
            finally
            {
                timer.Stop();
            }
            double createMs = timer.Elapsed.TotalMilliseconds;

            using (session)
            {
                // いろいろな方向から、スカートの中心あたりを狙う100本
                var random = new System.Random(1);
                var rays = new Ray[100];
                for (int i = 0; i < rays.Length; i++)
                {
                    var target = new Vector3((float)random.NextDouble() * 0.3f - 0.15f, 0.75f + (float)random.NextDouble() * 0.4f - 0.2f,
                        (float)random.NextDouble() * 0.3f - 0.15f);
                    float azimuth = (float)random.NextDouble() * 2f * Mathf.PI;
                    float elevation = ((float)random.NextDouble() - 0.5f) * 1.2f;
                    var direction = new Vector3(Mathf.Cos(azimuth) * Mathf.Cos(elevation), Mathf.Sin(elevation), Mathf.Sin(azimuth) * Mathf.Cos(elevation));
                    rays[i] = new Ray(target + direction * 1.5f, -direction);
                }

                // 最初の1回は木の準備などを含む可能性があるので、除いて測る
                session.Raycast(rays[0], out _, out _);
                timer.Restart();
                int hits = 0;
                for (int i = 0; i < rays.Length; i++)
                {
                    if (session.Raycast(rays[i], out _, out _, out _)) hits++;
                }
                timer.Stop();
                double raycastMs = timer.Elapsed.TotalMilliseconds / rays.Length;
                Assert.That(hits, Is.GreaterThan(rays.Length / 2), "レイが当たっていません");

                // 結果が総当たりと同じ
                int compared = 0;
                for (int i = 0; i < 20; i++)
                {
                    bool expected = BruteForceRaycast(rays[i], session.CurrentWorldVertices, mesh.triangles, out float expectedDistance);
                    bool actual = session.Raycast(rays[i], out Vector3 hit, out _, out _);
                    Assert.That(actual, Is.EqualTo(expected), $"レイ {i}");
                    if (!expected) continue;
                    compared++;
                    Assert.That(Vector3.Distance(hit, rays[i].origin), Is.EqualTo(expectedDistance).Within(1e-3f), $"レイ {i}");
                }
                Assert.That(compared, Is.GreaterThan(5));

                // 総当たりの時間（比較用）
                Vector3[] current = session.CurrentWorldVertices;
                int[] meshTriangles = mesh.triangles;
                timer.Restart();
                for (int i = 0; i < 10; i++) BruteForceRaycast(rays[i], current, meshTriangles, out _);
                timer.Stop();
                double bruteMs = timer.Elapsed.TotalMilliseconds / 10;

                // 実際に引っ張って、形を変える
                session.Solver.InfluenceRadius = 0.2f;
                Vector3 grabPoint = Vector3.zero, grabNormal = Vector3.up;
                int grabTriangle = -1;
                for (int i = 0; i < rays.Length && grabTriangle < 0; i++) session.Raycast(rays[i], out grabPoint, out grabNormal, out grabTriangle);
                Assert.That(grabTriangle, Is.GreaterThanOrEqualTo(0));
                Assert.That(session.BeginGrab(grabPoint, 0.05f, grabTriangle), Is.True);
                session.DragTo(grabPoint + grabNormal * 0.05f);
                session.Solver.Solve(5);

                timer.Restart();
                const int updateRepeats = 10;
                for (int i = 0; i < updateRepeats; i++) session.UpdateCurrentShape();
                timer.Stop();
                double updateMs = timer.Elapsed.TotalMilliseconds / updateRepeats;

                // 形が変わった直後の最初の1回（境界箱の更新を含む）
                session.UpdateCurrentShape();
                timer.Restart();
                session.Raycast(rays[2], out _, out _, out _);
                timer.Stop();
                double firstAfterChangeMs = timer.Elapsed.TotalMilliseconds;
                session.Release();

                var deltaVertices = new Vector3[session.VertexCount];
                var deltaNormals = new Vector3[session.VertexCount];
                session.BuildMeshSpaceDeltas(deltaVertices, deltaNormals);   // 最初の1回は作業用の配列の確保を含む
                timer.Restart();
                const int buildRepeats = 3;
                for (int i = 0; i < buildRepeats; i++) session.BuildMeshSpaceDeltas(deltaVertices, deltaNormals);
                timer.Stop();
                double buildMs = timer.Elapsed.TotalMilliseconds / buildRepeats;

                Debug.Log($"[GP02] 大きなスカート: 頂点 {session.VertexCount} / 三角形 {meshTriangles.Length / 3} / 粒子 {session.ParticleCount}\n" +
                    $"  セッション作成（粒子化） {createMs:F0} ms（うちセッション内の計測 {session.PrepareMilliseconds:F0} ms）\n" +
                    $"  Raycast 1回（100回の平均、当たり {hits}） {raycastMs:F3} ms（参考: 総当たり {bruteMs:F2} ms）\n" +
                    $"  Raycast（形が変わった直後の1回。箱の更新込み） {firstAfterChangeMs:F2} ms\n" +
                    $"  UpdateCurrentShape 1回 {updateMs:F2} ms\n" +
                    $"  BuildMeshSpaceDeltas 1回 {buildMs:F2} ms");

                // 極端に遅い場合だけ失敗にする
                Assert.That(createMs, Is.LessThan(120000), "セッション作成");
                Assert.That(raycastMs, Is.LessThan(5.0), "Raycast");
                Assert.That(firstAfterChangeMs, Is.LessThan(200.0), "Raycast（形が変わった直後）");
                Assert.That(updateMs, Is.LessThan(500.0), "UpdateCurrentShape");
                Assert.That(buildMs, Is.LessThan(2000.0), "BuildMeshSpaceDeltas");
            }
        }
        // 保存したあとの作り直し（Rebase した布を渡す）が、粒子を置き直す普通の作り直しより十分速いこと
        [Test]
        public void Skirt80k_MeasureRebuildAfterSave()
        {
            const string tempRoot = "Assets/GP02_TestTemp";
            Mesh mesh = CreateSkirtMesh();
            Renderer renderer = CreateRenderer(mesh);
            try
            {
                var timer = Stopwatch.StartNew();
                var session = new ClothEditSession(renderer);
                double createMs = timer.Elapsed.TotalMilliseconds;
                ClothEditSession next = null;
                try
                {
                    // 上端をピン留めして、下の方をつまんで引っ張る
                    session.PaintPins(new Vector3(0.17f, 1f, 0f), 0.05f, true);
                    session.Solver.InfluenceRadius = 0.2f;
                    var ray = new Ray(new Vector3(1.5f, 0.6f, 0f), Vector3.left);
                    Assert.That(session.Raycast(ray, out Vector3 point, out Vector3 normal, out int triangle), Is.True);
                    Assert.That(session.BeginGrab(point, 0.05f, triangle), Is.True);
                    session.DragTo(point + normal * 0.05f);
                    session.Solver.Solve(5);
                    session.Release();
                    session.UpdateCurrentShape();
                    Vector3[] before = (Vector3[])session.CurrentWorldVertices.Clone();
                    int pinned = session.PinnedCount;

                    session.ApplyToScene("Pulled", tempRoot + "/Meshes");

                    // ここから作り直し（Rebase 込み）。ウィンドウの Save() と同じ順序
                    timer.Restart();
                    var rebased = session.Cloth.Rebase(session.Solver.Positions);
                    double rebaseMs = timer.Elapsed.TotalMilliseconds;
                    var pins = new List<int>();
                    session.GetPinnedParticles(pins);
                    session.Dispose();
                    next = new ClothEditSession(renderer, rebased);
                    next.PinParticles(pins);
                    timer.Stop();
                    double rebuildMs = timer.Elapsed.TotalMilliseconds;

                    Debug.Log($"[GP02] 大きなスカートの保存後の作り直し: 頂点 {next.VertexCount} / 粒子 {next.ParticleCount}\n" +
                        $"  普通の作成（粒子の置き直し） {createMs:F0} ms\n" +
                        $"  保存後の作り直し（Rebase 込み） {rebuildMs:F0} ms（うち Rebase {rebaseMs:F0} ms、セッション内の計測 {next.PrepareMilliseconds:F0} ms）");

                    // 形とピンが引き継がれている
                    Assert.That(next.PinnedCount, Is.EqualTo(pinned));
                    Assert.That(next.Solver.MeasureMaxStretch(), Is.InRange(-1e-4f, 1e-4f));
                    float maxDifference = 0f;
                    for (int v = 0; v < before.Length; v++)
                    {
                        maxDifference = Mathf.Max(maxDifference, Vector3.Distance(next.CurrentWorldVertices[v], before[v]));
                    }
                    Assert.That(maxDifference, Is.LessThan(1e-4f));

                    Assert.That(rebuildMs, Is.LessThan(3000.0), "保存後の作り直し");
                }
                finally
                {
                    session.Dispose();
                    next?.Dispose();
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }
    }
}
