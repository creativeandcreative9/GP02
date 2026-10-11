using System.Numerics;
using GP02.Core;
using Unity.Burst;
using UnityEditor;

namespace GP02.EditorTools
{
    /// <summary>
    /// <see cref="ClothSolver"/> の反復を Burst で計算する高速版。
    /// com.unity.burst が入っているときだけコンパイルされる（この asmdef の versionDefines と defineConstraints）。
    /// 計算の式と順番は <see cref="ClothSolver"/> の C# 版と同じ。
    ///
    /// C# の配列を固定（fixed）し、先頭のポインタを Burst の static メソッドに渡す。
    /// System.Numerics.Vector3 の配列は float 3つ並び、int[] のペアは int 2つ並び、bool[] は 1バイトずつとして読む。
    /// </summary>
    public sealed unsafe class BurstClothSolverKernel : IClothSolverKernel
    {
        public string Name => "Burst";

        /// <summary>
        /// Burst が実際に使われているか。エディタの Jobs &gt; Burst &gt; Enable Compilation を切ると false になり、
        /// 同じ式が（遅い）C# のまま動く。
        /// </summary>
        public bool IsAccelerated => BurstCompiler.IsEnabled;

        public void Iterate(in ClothSolverIterationData d, int iterations)
        {
            fixed (Vector3* positions = d.Positions)
            fixed (float* inverseMass = d.InverseMass, bendRest = d.BendRestLengths, bendLambda = d.BendLambda,
                stretchRest = d.StretchRestLengths, stretchLambda = d.StretchLambda, grabDistance = d.GrabDistance, pinDistance = d.PinDistance)
            fixed (int* bendPairs = d.BendPairs, activeBend = d.ActiveBend, stretchPairs = d.StretchPairs, activeStretch = d.ActiveStretch,
                activeParticles = d.ActiveParticles, nearestPin = d.NearestPin)
            fixed (bool* inBrush = d.InBrush)
            fixed (Vector3* restPositions = d.RestPositions, restNormals = d.RestNormals, brushStart = d.BrushStart, brushLambda = d.BrushLambda)
            fixed (int* restAnchor = d.RestAnchor)
            fixed (float* restAllowance = d.RestAllowance)
            {
                var args = new BurstClothKernels.IterationArgs
                {
                    Positions = (float*)positions,
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
                    BrushCompliance = brushStart != null && brushLambda != null ? d.BrushCompliance : 0f,
                    BrushStart = (float*)brushStart,
                    BrushLambda = (float*)brushLambda,
                    BrushMoveX = d.BrushMove.X,
                    BrushMoveY = d.BrushMove.Y,
                    BrushMoveZ = d.BrushMove.Z,
                };
                BurstClothKernels.Iterate(&args, iterations);
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
