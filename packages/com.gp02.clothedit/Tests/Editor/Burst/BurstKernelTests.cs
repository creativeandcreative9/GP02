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
