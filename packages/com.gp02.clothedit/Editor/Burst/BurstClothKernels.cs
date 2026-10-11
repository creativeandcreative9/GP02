using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace GP02.EditorTools
{
    /// <summary>
    /// Burst でコンパイルされる計算の本体。<c>ClothSolver</c>（Core）の C# 版と、式も順番も同じにしてある。
    /// 引数は管理されない値（ポインタと数）だけで、Burst の direct call として C# から呼べる。
    /// 座標は粒子ごとに x, y, z の float 3つ。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class BurstClothKernels
    {
        public struct IterationArgs
        {
            public float* Positions;
            public float* InverseMass;
            public int* BendPairs;
            public float* BendRest;
            public float* BendLambda;
            public int* ActiveBend;
            public int ActiveBendCount;
            public float BendCompliance;
            public int* StretchPairs;
            public float* StretchRest;
            public float* StretchLambda;
            public int* ActiveStretch;
            public int ActiveStretchCount;
            public float StretchCompliance;
            public int* ActiveParticles;
            public int ActiveParticleCount;
            public int Grabbed;
            public byte* InBrush;
            public float* GrabDistance;
            public int* NearestPin;
            public float* PinDistance;
        }

        public struct CoarseLevelArgs
        {
            public float* Positions;
            public float* LevelStart;
            public float* InverseMass;
            public int* Members;
            public int MemberCount;
            public int* Pairs;
            public float* RestLengths;
            public int PairCount;
            public int* BindOffsets;
            public int* BindParents;
            public float* BindWeights;
            public int* ActiveParticles;
            public int ActiveParticleCount;
            public int Iterations;
        }

        [BurstCompile(CompileSynchronously = true)]
        public static void Iterate(IterationArgs* a, int iterations)
        {
            for (int it = 0; it < iterations; it++)
            {
                // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
                SolveDistances(a->Positions, a->InverseMass, a->ActiveBend, a->ActiveBendCount, a->BendPairs, a->BendRest, a->BendLambda, a->BendCompliance);
                SolveDistances(a->Positions, a->InverseMass, a->ActiveStretch, a->ActiveStretchCount, a->StretchPairs, a->StretchRest, a->StretchLambda, a->StretchCompliance);
                SolveTethers(a);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        public static void SolveCoarseLevel(CoarseLevelArgs* a)
        {
            float* p = a->Positions;
            float* start = a->LevelStart;
            float* inverseMass = a->InverseMass;

            for (int m = 0; m < a->MemberCount; m++)
            {
                int i = a->Members[m];
                start[3 * i] = p[3 * i];
                start[3 * i + 1] = p[3 * i + 1];
                start[3 * i + 2] = p[3 * i + 2];
            }

            for (int it = 0; it < a->Iterations; it++) SolveStretchOnly(p, inverseMass, a->Pairs, a->RestLengths, a->PairCount);

            // この段の粒子の動きを、この段に含まれない粒子へ配る
            int* offsets = a->BindOffsets;
            int* parents = a->BindParents;
            float* weights = a->BindWeights;
            for (int n = 0; n < a->ActiveParticleCount; n++)
            {
                int i = a->ActiveParticles[n];
                if (inverseMass[i] == 0f) continue;
                int end = offsets[i + 1];
                float dx = 0f, dy = 0f, dz = 0f;
                for (int k = offsets[i]; k < end; k++)
                {
                    int parent = parents[k];
                    float w = weights[k];
                    dx += (p[3 * parent] - start[3 * parent]) * w;
                    dy += (p[3 * parent + 1] - start[3 * parent + 1]) * w;
                    dz += (p[3 * parent + 2] - start[3 * parent + 2]) * w;
                }
                p[3 * i] += dx;
                p[3 * i + 1] += dy;
                p[3 * i + 2] += dz;
            }
        }

        // 伸びているときだけ縮める（縮んでいるのは許す）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveStretchOnly(float* p, float* inverseMass, int* pairs, float* rest, int count)
        {
            for (int k = 0; k < count; k++)
            {
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;

                float dx = p[3 * i] - p[3 * j], dy = p[3 * i + 1] - p[3 * j + 1], dz = p[3 * i + 2] - p[3 * j + 2];
                float length = math.sqrt(dx * dx + dy * dy + dz * dz);
                if (length <= rest[k]) continue;

                float s = -(length - rest[k]) / (w * length);
                dx *= s;
                dy *= s;
                dz *= s;
                p[3 * i] += wi * dx; p[3 * i + 1] += wi * dy; p[3 * i + 2] += wi * dz;
                p[3 * j] -= wj * dx; p[3 * j + 1] -= wj * dy; p[3 * j + 2] -= wj * dz;
            }
        }

        // XPBD の距離の制約を、active の先頭 count 個について順に解く
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveDistances(float* p, float* inverseMass, int* active, int count, int* pairs, float* rest, float* lambda, float alpha)
        {
            for (int a = 0; a < count; a++)
            {
                int k = active[a];
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;

                float dx = p[3 * i] - p[3 * j], dy = p[3 * i + 1] - p[3 * j + 1], dz = p[3 * i + 2] - p[3 * j + 2];
                float length = math.sqrt(dx * dx + dy * dy + dz * dz);
                if (length < 1e-12f) continue;

                float deltaLambda = (-(length - rest[k]) - alpha * lambda[k]) / (w + alpha);
                lambda[k] += deltaLambda;

                float s = deltaLambda / length;
                dx *= s;
                dy *= s;
                dz *= s;
                p[3 * i] += wi * dx; p[3 * i + 1] += wi * dy; p[3 * i + 2] += wi * dz;
                p[3 * j] -= wj * dx; p[3 * j + 1] -= wj * dy; p[3 * j + 2] -= wj * dz;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveTethers(IterationArgs* a)
        {
            for (int n = 0; n < a->ActiveParticleCount; n++)
            {
                int i = a->ActiveParticles[n];
                if (a->Grabbed >= 0 && a->InBrush[i] == 0) SolveTether(a->Positions, i, a->Grabbed, a->GrabDistance[i]);

                // つまんだ範囲も、ピンから布の長さ以上は離れない
                int pin = a->NearestPin[i];
                if (pin >= 0) SolveTether(a->Positions, i, pin, a->PinDistance[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveTether(float* p, int i, int origin, float maxLength)
        {
            if (math.isinf(maxLength)) return;
            float ox = p[3 * origin], oy = p[3 * origin + 1], oz = p[3 * origin + 2];
            float dx = p[3 * i] - ox, dy = p[3 * i + 1] - oy, dz = p[3 * i + 2] - oz;
            float lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared <= maxLength * maxLength) return;
            float s = maxLength / math.sqrt(lengthSquared);
            p[3 * i] = ox + dx * s;
            p[3 * i + 1] = oy + dy * s;
            p[3 * i + 2] = oz + dz * s;
        }
    }
}
