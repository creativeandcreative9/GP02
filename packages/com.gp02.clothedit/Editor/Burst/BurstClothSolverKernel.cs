using System.Numerics;
using GP02.Core;
using Unity.Burst;
using UnityEditor;

namespace GP02.EditorTools
{
    /// <summary>
    /// <see cref="ClothSolver"/> の反復と、形の更新（頂点への展開、境界箱の更新）を Burst で計算する高速版。
    /// com.unity.burst が入っているときだけコンパイルされる（この asmdef の versionDefines と defineConstraints）。
    /// 計算の式と順番は <see cref="ClothSolver"/> と <see cref="ShapeKernels"/> の C# 版と同じ。
    /// <see cref="ClothSolver.Kernel"/> に入れると、<see cref="IShapeKernel"/> としても使われる。
    ///
    /// C# の配列を固定（fixed）し、先頭のポインタを Burst の static メソッドに渡す。
    /// System.Numerics.Vector3 の配列は float 3つ並び、int[] のペアは int 2つ並び、bool[] は 1バイトずつとして読む。
    /// </summary>
    public sealed unsafe class BurstClothSolverKernel : IClothSolverKernel, IShapeKernel
    {
        public string Name => "Burst";

        /// <summary>
        /// Burst が実際に使われているか。エディタの Jobs &gt; Burst &gt; Enable Compilation を切ると false になり、
        /// 同じ式が（遅い）C# のまま動く。
        /// </summary>
        public bool IsAccelerated => BurstCompiler.IsEnabled;

        public void Iterate(in ClothSolverIterationData d, int iterations)
        {
            fixed (Vector3* positions = d.Positions, worldPositions = d.WorldPositions)
            fixed (float* inverseMass = d.InverseMass, bendRest = d.BendRestLengths, bendLambda = d.BendLambda,
                stretchRest = d.StretchRestLengths, stretchLambda = d.StretchLambda, grabDistance = d.GrabDistance, pinDistance = d.PinDistance)
            fixed (int* graphOffsets = d.GraphOffsets, graphNeighbors = d.GraphNeighbors,
                bendPairs = d.BendPairs, activeBend = d.ActiveBend, stretchPairs = d.StretchPairs, activeStretch = d.ActiveStretch,
                activeParticles = d.ActiveParticles, nearestPin = d.NearestPin, grabSource = d.GrabSource)
            fixed (bool* inBrush = d.InBrush)
            fixed (Vector3* restPositions = d.RestPositions, restNormals = d.RestNormals, brushGoal = d.BrushGoal, brushLambda = d.BrushLambda)
            fixed (int* restAnchor = d.RestAnchor)
            fixed (float* restAllowance = d.RestAllowance, contactGap = d.ContactGap)
            fixed (int* contactOuter = d.ContactOuter, contactInner = d.ContactInner)
            {
                var args = new BurstClothKernels.IterationArgs
                {
                    Positions = (float*)positions,
                    WorldPositions = (float*)worldPositions,
                    OriginX = d.Origin.X,
                    OriginY = d.Origin.Y,
                    OriginZ = d.Origin.Z,
                    GraphOffsets = graphOffsets,
                    GraphNeighbors = graphNeighbors,
                    InverseMass = inverseMass,
                    BendPairs = bendPairs,
                    BendRest = bendRest,
                    BendLambda = bendLambda,
                    ActiveBend = activeBend,
                    ActiveBendCount = d.ActiveBendCount,
                    BendCompliance = d.BendCompliance,
                    StretchPairs = stretchPairs,
                    StretchRest = stretchRest,
                    StretchLambda = stretchLambda,
                    ActiveStretch = activeStretch,
                    ActiveStretchCount = d.ActiveStretchCount,
                    StretchCompliance = d.StretchCompliance,
                    ActiveParticles = activeParticles,
                    ActiveParticleCount = d.ActiveParticleCount,
                    Grabbed = d.Grabbed,
                    InBrush = (byte*)inBrush,
                    GrabDistance = grabDistance,
                    GrabSource = grabSource,
                    NearestPin = nearestPin,
                    PinDistance = pinDistance,
                    // 配列が null のときは使わない（KeepOutsideRestSurface を 0 にする）
                    KeepOutsideRestSurface = d.KeepOutsideRestSurface && restNormals != null ? 1 : 0,
                    RestPositions = (float*)restPositions,
                    RestNormals = (float*)restNormals,
                    RestAnchor = restAnchor,
                    // null のとき fixed は null ポインタを返す。Burst 側は null なら 0 として扱う
                    RestAllowance = restAllowance,
                    // 配列が無いときは引き寄せない
                    BrushCompliance = brushGoal != null && brushLambda != null ? d.BrushCompliance : 0f,
                    BrushGoal = (float*)brushGoal,
                    BrushLambda = (float*)brushLambda,
                    // 接触の段は、元の形の面の向きが無ければ使えない
                    ContactCount = restNormals != null && contactOuter != null && contactInner != null && contactGap != null ? d.ContactCount : 0,
                    ContactOuter = contactOuter,
                    ContactInner = contactInner,
                    ContactGap = contactGap,
                    ContactReach = d.ContactReach,
                };
                BurstClothKernels.Iterate(&args, iterations);
            }
        }

        public void DeformWelded(in WeldedDeformData d)
        {
            fixed (Vector3* particles = d.ParticlePositions)
            fixed (Vector3* rest = d.ParticleRestPositions, basePositions = d.BasePositions, welded = d.Welded)
            fixed (int* bindOffsets = d.BindOffsets, bindParticles = d.BindParticles)
            fixed (float* bindWeights = d.BindWeights)
            {
                var args = new BurstClothKernels.DeformWeldedArgs
                {
                    ParticlePositions = (float*)particles,
                    ParticleRest = (float*)rest,
                    BasePositions = (float*)basePositions,
                    BindOffsets = bindOffsets,
                    BindParticles = bindParticles,
                    BindWeights = bindWeights,
                    Welded = (float*)welded,
                    WeldedCount = d.Welded.Length,
                };
                BurstClothKernels.DeformWelded(&args);
            }
        }

        public void ExpandVertices(in VertexExpandData d)
        {
            fixed (Vector3* vertices = d.Vertices)
            fixed (Vector3* baseVertices = d.BaseVertices, welded = d.Welded, weldedRest = d.WeldedRest)
            fixed (int* map = d.VertexToWelded)
            {
                var args = new BurstClothKernels.ExpandVerticesArgs
                {
                    Vertices = (float*)vertices,
                    BaseVertices = (float*)baseVertices,
                    Welded = (float*)welded,
                    WeldedRest = (float*)weldedRest,
                    VertexToWelded = map,
                    VertexCount = d.Vertices.Length,
                };
                BurstClothKernels.ExpandVertices(&args);
            }
        }

        public void RefitBvh(in BvhRefitData d)
        {
            fixed (Vector3* positions = d.Positions, boxMin = d.BoxMin, boxMax = d.BoxMax)
            fixed (int* leafVertices = d.LeafTriangleVertices, right = d.Right, start = d.Start, count = d.Count)
            {
                var args = new BurstClothKernels.RefitBvhArgs
                {
                    Positions = (float*)positions,
                    LeafTriangleVertices = leafVertices,
                    Right = right,
                    Start = start,
                    Count = count,
                    NodeCount = d.NodeCount,
                    BoxMin = (float*)boxMin,
                    BoxMax = (float*)boxMax,
                    Padding = d.Padding,
                };
                BurstClothKernels.RefitBvh(&args);
            }
        }

        public void SolveCoarseLevel(in ClothSolverCoarseLevelData d)
        {
            fixed (Vector3* positions = d.Positions, levelStart = d.LevelStart)
            fixed (float* inverseMass = d.InverseMass, restLengths = d.RestLengths, bindWeights = d.BindWeights)
            fixed (int* members = d.Members, pairs = d.Pairs, bindOffsets = d.BindOffsets, bindParents = d.BindParents,
                activeParticles = d.ActiveParticles)
            {
                var args = new BurstClothKernels.CoarseLevelArgs
                {
                    Positions = (float*)positions,
                    LevelStart = (float*)levelStart,
                    InverseMass = inverseMass,
                    Members = members,
                    MemberCount = d.Members.Length,
                    Pairs = pairs,
                    RestLengths = restLengths,
                    PairCount = d.RestLengths.Length,
                    BindOffsets = bindOffsets,
                    BindParents = bindParents,
                    BindWeights = bindWeights,
                    ActiveParticles = activeParticles,
                    ActiveParticleCount = d.ActiveParticleCount,
                    Iterations = d.Iterations,
                };
                BurstClothKernels.SolveCoarseLevel(&args);
            }
        }
    }

    /// <summary>読み込み時に、Burst 版を既定のカーネルとして登録する。</summary>
    [InitializeOnLoad]
    static class BurstClothSolverKernelRegistration
    {
        static BurstClothSolverKernelRegistration()
        {
            ClothSolver.DefaultKernel = new BurstClothSolverKernel();
        }
    }
}
