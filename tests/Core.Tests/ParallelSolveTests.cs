using System.Numerics;
using GP02.Core;

namespace GP02.Core.Tests;

public class ParallelSolveTests
{
    [Fact]
    public void Coloring_NoTwoConstraintsOfSameColorShareParticle()
    {
        ProceduralMeshes.Grid(40, 40, 0.4f, 0.4f, out var vertices, out var triangles);
        var model = ParticleCloth.Build(vertices, triangles).Model;

        foreach (int[] pairs in new[] { model.StretchPairs, model.BendPairs })
        {
            int[] colors = ConstraintColoring.Color(model.ParticleCount, pairs, out int colorCount, out bool hasSerial);
            Assert.False(hasSerial);
            Assert.InRange(colorCount, 2, 64);

            var owner = new Dictionary<(int color, int particle), int>();
            for (int k = 0; k < colors.Length; k++)
            {
                Assert.InRange(colors[k], 0, colorCount - 1);
                foreach (int particle in new[] { pairs[2 * k], pairs[2 * k + 1] })
                {
                    Assert.True(owner.TryAdd((colors[k], particle), k), $"制約 {k} と {owner.GetValueOrDefault((colors[k], particle))} が色 {colors[k]} で粒子 {particle} を共有");
                }
            }
        }
    }

    [Fact]
    public void Coloring_PutsOverflowIntoSerialColor()
    {
        // 1つの粒子に70本の制約がつながると、64色では足りない
        var pairs = new List<int>();
        for (int j = 1; j <= 70; j++) { pairs.Add(0); pairs.Add(j); }

        int[] colors = ConstraintColoring.Color(71, pairs.ToArray(), out int colorCount, out bool hasSerial);

        Assert.True(hasSerial);
        Assert.Equal(65, colorCount);
        Assert.Equal(6, colors.Count(c => c == colorCount - 1));
    }

    sealed class RecordingWork : PhaseRunner.IWork
    {
        public readonly int[] RunCount;
        public readonly int[] EnterOrder;
        public readonly int ChunkCount_;
        int _entered;
        public bool OrderBroken;

        public RecordingWork(int phases, int chunks)
        {
            RunCount = new int[phases * chunks];
            EnterOrder = new int[phases];
            ChunkCount_ = chunks;
        }

        public bool IsSerial(int phase) => phase % 7 == 3;

        public int ChunkCount(int phase) => IsSerial(phase) ? 1 : ChunkCount_;

        public void EnterPhase(int phase) => EnterOrder[phase] = Interlocked.Increment(ref _entered);

        public void RunChunk(int phase, int chunk)
        {
            // 段階が始まる前の小片や、前の段階が終わっていない小片があれば順番が壊れている
            if (EnterOrder[phase] != phase + 1) OrderBroken = true;
            if (phase > 0)
            {
                for (int c = 0; c < ChunkCount(phase - 1); c++) if (Volatile.Read(ref RunCount[(phase - 1) * ChunkCount_ + c]) != 1) OrderBroken = true;
            }
            Thread.SpinWait(50);
            Interlocked.Increment(ref RunCount[phase * ChunkCount_ + chunk]);
        }
    }

    [Fact]
    public void PhaseRunner_RunsEveryChunkOnceInPhaseOrder()
    {
        var runner = new PhaseRunner(4);
        for (int round = 0; round < 5; round++)
        {
            var work = new RecordingWork(300, 6);
            runner.Run(work, 300);

            Assert.False(work.OrderBroken);
            for (int p = 0; p < 300; p++)
            {
                for (int c = 0; c < 6; c++) Assert.Equal(c < work.ChunkCount(p) ? 1 : 0, work.RunCount[p * 6 + c]);
            }
        }
    }

    // 複数スレッドでは区画の数が固定なので、スレッド数を変えても結果はビットまで同じになる
    [Fact]
    public void Solve_GivesIdenticalResultForAnyThreadCount()
    {
        ProceduralMeshes.Grid(60, 60, 0.6f, 0.6f, out var vertices, out var triangles);
        var cloth = ParticleCloth.Build(vertices, triangles);
        Vector3[] Run(int threads)
        {
            var solver = new ClothSolver(cloth.Model, cloth.Hierarchy) { ThreadCount = threads, InfluenceRadius = 0.2f };
            for (int p = 0; p < cloth.Model.ParticleCount; p++)
            {
                if (cloth.Model.RestPositions[p].Y > 0.6f - cloth.Spacing) solver.SetPinned(p, true);
            }
            int grabbed = solver.FindNearestParticle(new Vector3(0.3f, 0.2f, 0f));
            Vector3 start = solver.Positions[grabbed];
            for (int f = 1; f <= 8; f++)
            {
                solver.Grab(grabbed, start + new Vector3(0f, 0.03f, 0.12f) * f / 8, 0.03f);
                solver.Solve(20);
            }
            solver.Release();
            solver.Solve(20);
            return solver.Positions.ToArray();
        }

        Vector3[] single = Run(2);
        foreach (int threads in new[] { 3, 4 })
        {
            Vector3[] parallel = Run(threads);
            for (int i = 0; i < single.Length; i++) Assert.True(single[i] == parallel[i], $"{threads}スレッドで粒子 {i} が違う");
        }
    }
}
