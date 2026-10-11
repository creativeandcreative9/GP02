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
            public float* WorldPositions;        // 計算のあとに、計算範囲の粒子の分を書き戻す
            public float OriginX;                // Positions + Origin = WorldPositions
            public float OriginY;
            public float OriginZ;
            public int* GraphOffsets;            // 粒子をつなぐ辺のグラフ。元の形の近い粒子を探し直すのに使う
            public int* GraphNeighbors;
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
            public float* GrabDistance;          // つまんだ範囲（いずれかの粒子）からの距離
            public int* GrabSource;              // 一番近い、つまんだ範囲の粒子（テザーの起点）
            public int* NearestPin;
            public float* PinDistance;
            public int KeepOutsideRestSurface;   // 0 / 1
            public float* RestPositions;
            public float* RestNormals;
            public int* RestAnchor;
            public float* RestAllowance;         // null なら 0
            public float BrushCompliance;        // 0 以下なら、つまんだ範囲を引き寄せない
            public float* BrushGoal;             // 粒子ごとの目標（内部の座標）
            public float* BrushLambda;
            public int ContactCount;             // 0 なら層どうしの接触の段は何もしない
            public int* ContactOuter;
            public int* ContactInner;            // 前処理で探し直す
            public float* ContactGap;
            public float ContactReach;           // 内側の粒子の面に沿ってこれより離れた組は解かない
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

        public struct DeformWeldedArgs
        {
            public float* ParticlePositions;
            public float* ParticleRest;
            public float* BasePositions;
            public int* BindOffsets;
            public int* BindParticles;
            public float* BindWeights;
            public float* Welded;
            public int WeldedCount;
        }

        public struct ExpandVerticesArgs
        {
            public float* Vertices;
            public float* BaseVertices;
            public float* Welded;
            public float* WeldedRest;
            public int* VertexToWelded;
            public int VertexCount;
        }

        public struct RefitBvhArgs
        {
            public float* Positions;
            public int* LeafTriangleVertices;
            public int* Right;
            public int* Start;
            public int* Count;
            public int NodeCount;
            public float* BoxMin;
            public float* BoxMax;
            public float Padding;
        }

        [BurstCompile(CompileSynchronously = true)]
        public static void Iterate(IterationArgs* a, int iterations)
        {
            // 前処理：λ を 0 に戻し、元の形の近い粒子を探し直す
            for (int n = 0; n < a->ActiveStretchCount; n++) a->StretchLambda[a->ActiveStretch[n]] = 0f;
            for (int n = 0; n < a->ActiveBendCount; n++) a->BendLambda[a->ActiveBend[n]] = 0f;
            if (a->KeepOutsideRestSurface != 0) UpdateRestAnchors(a);
            if (a->ContactCount > 0) UpdateContactPartners(a);

            for (int it = 0; it < iterations; it++)
            {
                // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
                if (a->BrushCompliance > 0f) SolveBrushTargets(a);
                SolveDistances(a->Positions, a->InverseMass, a->ActiveBend, a->ActiveBendCount, a->BendPairs, a->BendRest, a->BendLambda, a->BendCompliance);
                SolveDistances(a->Positions, a->InverseMass, a->ActiveStretch, a->ActiveStretchCount, a->StretchPairs, a->StretchRest, a->StretchLambda, a->StretchCompliance);
                SolveTethers(a);
                if (a->KeepOutsideRestSurface != 0) SolveRestSurface(a);
                if (a->ContactCount > 0) SolveLayerContacts(a);
            }

            // 後処理：動くのは計算範囲の粒子だけ（ピン留めと範囲外は動かない）。ワールド座標へ書き戻す
            float* p = a->Positions;
            float* world = a->WorldPositions;
            for (int n = 0; n < a->ActiveParticleCount; n++)
            {
                int i = a->ActiveParticles[n];
                world[3 * i] = p[3 * i] + a->OriginX;
                world[3 * i + 1] = p[3 * i + 1] + a->OriginY;
                world[3 * i + 2] = p[3 * i + 2] + a->OriginZ;
            }
        }

        // 今の近い粒子から、伸びの制約でつながった隣へ、近くなる限りたどる（最大16歩）。ClothSolver の C# 版と同じ
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UpdateRestAnchors(IterationArgs* a)
        {
            int* offsets = a->GraphOffsets;
            int* neighbors = a->GraphNeighbors;
            float* p = a->Positions;
            float* rest = a->RestPositions;
            for (int n = 0; n < a->ActiveParticleCount; n++)
            {
                int i = a->ActiveParticles[n];
                int anchor = a->RestAnchor[i];
                float px = p[3 * i], py = p[3 * i + 1], pz = p[3 * i + 2];
                float best = DistanceSquared(px, py, pz, rest, anchor);
                for (int step = 0; step < 16; step++)
                {
                    int next = -1;
                    int end = offsets[anchor + 1];
                    for (int k = offsets[anchor]; k < end; k++)
                    {
                        int candidate = neighbors[k];
                        float d = DistanceSquared(px, py, pz, rest, candidate);
                        if (d < best)
                        {
                            best = d;
                            next = candidate;
                        }
                    }
                    if (next < 0) break;
                    anchor = next;
                }
                a->RestAnchor[i] = anchor;
            }
        }

        // 接触の組の内側の粒子を、内側の層の隣へ、外側の粒子の今の位置に近くなる限りたどって探し直す（最大16歩）。ClothSolver の C# 版と同じ
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UpdateContactPartners(IterationArgs* a)
        {
            int* offsets = a->GraphOffsets;
            int* neighbors = a->GraphNeighbors;
            float* p = a->Positions;
            for (int k = 0; k < a->ContactCount; k++)
            {
                int i = a->ContactOuter[k];
                int partner = a->ContactInner[k];
                float px = p[3 * i], py = p[3 * i + 1], pz = p[3 * i + 2];
                float best = DistanceSquared(px, py, pz, p, partner);
                for (int step = 0; step < 16; step++)
                {
                    int next = -1;
                    int end = offsets[partner + 1];
                    for (int e = offsets[partner]; e < end; e++)
                    {
                        int candidate = neighbors[e];
                        float d = DistanceSquared(px, py, pz, p, candidate);
                        if (d < best)
                        {
                            best = d;
                            next = candidate;
                        }
                    }
                    if (next < 0) break;
                    partner = next;
                }
                a->ContactInner[k] = partner;
            }
        }

        // 外側の粒子が、内側の粒子の元の形の面から、保つ隙間より内に入っていれば、両方を逆質量で押し分ける（ClothSolver の C# 版と同じ）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveLayerContacts(IterationArgs* a)
        {
            float* p = a->Positions;
            float* normals = a->RestNormals;
            float* inverseMass = a->InverseMass;
            bool grabbing = a->Grabbed >= 0;
            float reachSquared = a->ContactReach * a->ContactReach;
            for (int k = 0; k < a->ContactCount; k++)
            {
                int i = a->ContactOuter[k], j = a->ContactInner[k];
                float nx = normals[3 * j], ny = normals[3 * j + 1], nz = normals[3 * j + 2];
                float dx = p[3 * i] - p[3 * j], dy = p[3 * i + 1] - p[3 * j + 1], dz = p[3 * i + 2] - p[3 * j + 2];
                float along = dx * nx + dy * ny + dz * nz;
                float c = along - a->ContactGap[k];
                if (c >= 0f) continue;
                if (dx * dx + dy * dy + dz * dz - along * along > reachSquared) continue;
                float wi = inverseMass[i], wj = inverseMass[j];
                if (grabbing)
                {
                    if (a->InBrush[i] != 0 && wj > 0f) wi = 0f;
                    else if (a->InBrush[j] != 0 && wi > 0f) wj = 0f;
                }
                float w = wi + wj;
                if (w == 0f) continue;
                float s = -c / w;
                p[3 * i] += wi * s * nx; p[3 * i + 1] += wi * s * ny; p[3 * i + 2] += wi * s * nz;
                p[3 * j] -= wj * s * nx; p[3 * j + 1] -= wj * s * ny; p[3 * j + 2] -= wj * s * nz;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float DistanceSquared(float x, float y, float z, float* points, int index)
        {
            float dx = x - points[3 * index], dy = y - points[3 * index + 1], dz = z - points[3 * index + 2];
            return dx * dx + dy * dy + dz * dz;
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
                // つまんだ範囲の外側は、一番近い範囲の粒子から、メッシュに沿った距離より遠くへは離れない
                if (a->Grabbed >= 0 && a->InBrush[i] == 0) SolveTether(a->Positions, i, a->GrabSource[i], a->GrabDistance[i]);

                // つまんだ範囲も、ピンから布の長さ以上は離れない
                int pin = a->NearestPin[i];
                if (pin >= 0) SolveTether(a->Positions, i, pin, a->PinDistance[i]);
            }
        }

        // つまんだ範囲の各粒子を、粒子ごとの目標へ引き寄せる（XPBD の柔らかい制約）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveBrushTargets(IterationArgs* a)
        {
            float* p = a->Positions;
            float* goal = a->BrushGoal;
            float* lambda = a->BrushLambda;
            float alpha = a->BrushCompliance;
            float scale = 1f / (1f + alpha);
            for (int n = 0; n < a->ActiveParticleCount; n++)
            {
                int i = a->ActiveParticles[n];
                if (a->InBrush[i] == 0) continue;
                float dx = (-(p[3 * i] - goal[3 * i]) - alpha * lambda[3 * i]) * scale;
                float dy = (-(p[3 * i + 1] - goal[3 * i + 1]) - alpha * lambda[3 * i + 1]) * scale;
                float dz = (-(p[3 * i + 2] - goal[3 * i + 2]) - alpha * lambda[3 * i + 2]) * scale;
                lambda[3 * i] += dx;
                lambda[3 * i + 1] += dy;
                lambda[3 * i + 2] += dz;
                p[3 * i] += dx;
                p[3 * i + 1] += dy;
                p[3 * i + 2] += dz;
            }
        }

        // 元の形の、近い粒子の面より、入ってよい深さ以上内側にあれば押し戻す（つまんでいる範囲は対象外）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveRestSurface(IterationArgs* a)
        {
            float* p = a->Positions;
            float* rest = a->RestPositions;
            float* normals = a->RestNormals;
            float* allowance = a->RestAllowance;
            bool grabbing = a->Grabbed >= 0;
            for (int n = 0; n < a->ActiveParticleCount; n++)
            {
                int i = a->ActiveParticles[n];
                if (grabbing && a->InBrush[i] != 0) continue;
                int r = a->RestAnchor[i];
                float nx = normals[3 * r], ny = normals[3 * r + 1], nz = normals[3 * r + 2];
                float d = (p[3 * i] - rest[3 * r]) * nx + (p[3 * i + 1] - rest[3 * r + 1]) * ny + (p[3 * i + 2] - rest[3 * r + 2]) * nz;
                if (allowance != null) d += allowance[r];
                if (d >= 0f) continue;
                p[3 * i] -= d * nx;
                p[3 * i + 1] -= d * ny;
                p[3 * i + 2] -= d * nz;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SolveTether(float* p, int i, int origin, float maxLength)
        {
            if (origin < 0 || math.isinf(maxLength)) return;
            float ox = p[3 * origin], oy = p[3 * origin + 1], oz = p[3 * origin + 2];
            float dx = p[3 * i] - ox, dy = p[3 * i + 1] - oy, dz = p[3 * i + 2] - oz;
            float lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared <= maxLength * maxLength) return;
            float s = maxLength / math.sqrt(lengthSquared);
            p[3 * i] = ox + dx * s;
            p[3 * i + 1] = oy + dy * s;
            p[3 * i + 2] = oz + dz * s;
        }

        // まとめた頂点ごとに、結びつけた粒子の移動量の重み付き和（ShapeKernels.DeformWelded と同じ式）
        [BurstCompile(CompileSynchronously = true)]
        public static void DeformWelded(DeformWeldedArgs* a)
        {
            float* particles = a->ParticlePositions;
            float* rest = a->ParticleRest;
            float* basePositions = a->BasePositions;
            int* offsets = a->BindOffsets;
            int* bound = a->BindParticles;
            float* weights = a->BindWeights;
            float* welded = a->Welded;
            for (int v = 0; v < a->WeldedCount; v++)
            {
                float dx = 0f, dy = 0f, dz = 0f;
                int end = offsets[v + 1];
                for (int k = offsets[v]; k < end; k++)
                {
                    int p = bound[k];
                    float w = weights[k];
                    dx += (particles[3 * p] - rest[3 * p]) * w;
                    dy += (particles[3 * p + 1] - rest[3 * p + 1]) * w;
                    dz += (particles[3 * p + 2] - rest[3 * p + 2]) * w;
                }
                welded[3 * v] = basePositions[3 * v] + dx;
                welded[3 * v + 1] = basePositions[3 * v + 1] + dy;
                welded[3 * v + 2] = basePositions[3 * v + 2] + dz;
            }
        }

        // 元の頂点 = 基準の位置 + まとめた頂点の動き（ShapeKernels.ExpandVertices と同じ式）
        [BurstCompile(CompileSynchronously = true)]
        public static void ExpandVertices(ExpandVerticesArgs* a)
        {
            float* vertices = a->Vertices;
            float* baseVertices = a->BaseVertices;
            float* welded = a->Welded;
            float* weldedRest = a->WeldedRest;
            int* map = a->VertexToWelded;
            for (int v = 0; v < a->VertexCount; v++)
            {
                int w = map[v];
                vertices[3 * v] = baseVertices[3 * v] + (welded[3 * w] - weldedRest[3 * w]);
                vertices[3 * v + 1] = baseVertices[3 * v + 1] + (welded[3 * w + 1] - weldedRest[3 * w + 1]);
                vertices[3 * v + 2] = baseVertices[3 * v + 2] + (welded[3 * w + 2] - weldedRest[3 * w + 2]);
            }
        }

        // 三角形の木の境界箱を、子から親へ作り直す（ShapeKernels.RefitBvh と同じ式）。子の番号は親より大きいので、後ろから処理する
        [BurstCompile(CompileSynchronously = true)]
        public static void RefitBvh(RefitBvhArgs* a)
        {
            float* positions = a->Positions;
            int* leafVertices = a->LeafTriangleVertices;
            float* boxMin = a->BoxMin;
            float* boxMax = a->BoxMax;
            float padding = a->Padding;
            for (int node = a->NodeCount - 1; node >= 0; node--)
            {
                float minX, minY, minZ, maxX, maxY, maxZ;
                int right = a->Right[node];
                if (right < 0)
                {
                    minX = minY = minZ = float.MaxValue;
                    maxX = maxY = maxZ = float.MinValue;
                    int end = 3 * (a->Start[node] + a->Count[node]);
                    for (int i = 3 * a->Start[node]; i < end; i++)
                    {
                        int v = leafVertices[i];
                        float x = positions[3 * v], y = positions[3 * v + 1], z = positions[3 * v + 2];
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (z < minZ) minZ = z;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                        if (z > maxZ) maxZ = z;
                    }
                    minX -= padding;
                    minY -= padding;
                    minZ -= padding;
                    maxX += padding;
                    maxY += padding;
                    maxZ += padding;
                }
                else
                {
                    int left = node + 1;
                    minX = boxMin[3 * left] < boxMin[3 * right] ? boxMin[3 * left] : boxMin[3 * right];
                    minY = boxMin[3 * left + 1] < boxMin[3 * right + 1] ? boxMin[3 * left + 1] : boxMin[3 * right + 1];
                    minZ = boxMin[3 * left + 2] < boxMin[3 * right + 2] ? boxMin[3 * left + 2] : boxMin[3 * right + 2];
                    maxX = boxMax[3 * left] > boxMax[3 * right] ? boxMax[3 * left] : boxMax[3 * right];
                    maxY = boxMax[3 * left + 1] > boxMax[3 * right + 1] ? boxMax[3 * left + 1] : boxMax[3 * right + 1];
                    maxZ = boxMax[3 * left + 2] > boxMax[3 * right + 2] ? boxMax[3 * left + 2] : boxMax[3 * right + 2];
                }
                boxMin[3 * node] = minX;
                boxMin[3 * node + 1] = minY;
                boxMin[3 * node + 2] = minZ;
                boxMax[3 * node] = maxX;
                boxMax[3 * node + 1] = maxY;
                boxMax[3 * node + 2] = maxZ;
            }
        }
    }
}
