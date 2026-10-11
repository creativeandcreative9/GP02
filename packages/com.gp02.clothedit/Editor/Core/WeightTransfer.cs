using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>「ウェイトを体に寄せる」（<see cref="WeightTransfer"/>）の設定。</summary>
    public sealed class WeightTransferOptions
    {
        /// <summary>強さの既定値（0〜1）。</summary>
        public const float DefaultStrength = 1f;

        /// <summary>周りへなめらかに減らす半径の既定値（メートル）。</summary>
        public const float DefaultRadius = 0.03f;

        /// <summary>1頂点に付けるボーンの数の上限の既定値（Unity の標準の 4 本）。</summary>
        public const int DefaultMaxBonesPerVertex = 4;

        /// <summary>体から写したウェイトを、一番近い体の点が飛ぶ所でなじませる回数の既定値。</summary>
        public const int DefaultSmoothIterations = 3;

        /// <summary>体からの距離による割合の、そのまま混ぜる距離と、混ぜなくなる距離の既定値（メートル）。</summary>
        public const float DefaultFullDistance = 0.015f;
        public const float DefaultZeroDistance = 0.08f;

        /// <summary>
        /// 強さ（0〜1）。対象の頂点のウェイトを、元のウェイトと体のウェイトを (1 - 強さ) : 強さ で混ぜたものにする。
        /// 1 なら体のウェイトそのもの（体にぴったり付いていく）。
        /// </summary>
        public float Strength = DefaultStrength;

        /// <summary>対象の頂点から周りへ、混ぜる割合をなめらかに 0 まで減らす半径（メートル。布に沿って測る）。0 なら対象の頂点だけ。</summary>
        public float Radius = DefaultRadius;

        /// <summary>1頂点に付けるボーンの数の上限。混ぜたあとに、大きいほうからこの数だけ残して合計を 1 にする。</summary>
        public int MaxBonesPerVertex = DefaultMaxBonesPerVertex;

        /// <summary>
        /// 体から写したウェイトを、写した頂点どうしで隣となじませる回数。左右の太ももの間のように、一番近い体の点が隣の頂点で
        /// 別の部品へ飛ぶ所（とその周り、この回数の輪まで）だけで行う。急に切り替わって布が裂けたように伸びないように。
        /// 体に沿った所はならさない（ならすと、体のひざの急な切り替わりがなまって、またひざが突き抜けるため）。
        /// </summary>
        public int SmoothIterations = DefaultSmoothIterations;

        /// <summary>
        /// 体からの距離（編集の姿勢で、頂点から一番近い体の点まで）がこれ以下なら、強さのまま混ぜる（メートル）。
        /// これより遠いと、<see cref="ZeroDistance"/> へ向かって割合をなめらかに 0 まで減らす。体にぴったりの服（タイツ・袖）はそのまま体に付いていき、
        /// 体から離れた布（ゆったりしたスカートの裾）は元のウェイトのまま残す（体の骨に固く付けると、座ったときに裾がめくれ上がり、布が大きく伸びるため）。
        /// </summary>
        public float FullDistance = DefaultFullDistance;

        /// <summary>体からの距離がこれ以上の頂点は、混ぜない（メートル）。</summary>
        public float ZeroDistance = DefaultZeroDistance;

        /// <summary>これより小さいウェイトは捨てる（残りで合計を 1 にする）。</summary>
        public float MinWeight = 1e-3f;

        public WeightTransferOptions Clone() => (WeightTransferOptions)MemberwiseClone();
    }

    /// <summary><see cref="WeightTransfer.Transfer"/> の結果。</summary>
    public sealed class WeightTransferResult
    {
        /// <summary>頂点ごとの新しいウェイト（すべての頂点。変えない頂点は元のまま）。</summary>
        public BoneWeightSets Weights;

        /// <summary>頂点ごとの、ウェイトを変えたか。</summary>
        public bool[] Changed;

        /// <summary>ウェイトを変えた頂点の数。</summary>
        public int ChangedCount;

        /// <summary>混ぜる対象だったが、近くに体の点が無いので変えなかった頂点（まとめた後）の数。</summary>
        public int MissingAnchors;

        /// <summary>体の点のウェイトのボーンが、服のどのボーンにも対応しないので変えなかった頂点（まとめた後）の数。</summary>
        public int Unmapped;
    }

    /// <summary>
    /// 「ウェイトを体に寄せる」の計算。服の頂点ごとに、編集の姿勢で一番近い体の表面の点（<see cref="BodyAnchors"/>）で体の頂点のウェイトを
    /// 重心座標で補い、体のボーンを服のボーンに付け替え（<see cref="ResolveBoneMap"/>）、元のウェイトと混ぜて、ボーンの数の上限を守って正規化する。
    /// 混ぜる割合は、対象の頂点（動作チェックでウェイトが原因と分かった所）で強さそのもの、そこから半径まで布に沿ってなめらかに 0 へ減らす（<see cref="Spread"/>）。
    /// 頂点はまとめた後の番号（UV の継ぎ目で分かれた頂点は同じ番号）で計算し、元の頂点ごとのウェイトと混ぜる。
    /// </summary>
    public static class WeightTransfer
    {
        /// <summary>
        /// 対象の頂点（seeds が正の頂点。値はその頂点の割合）から、布に沿った距離（三角形の辺をたどる）で radius まで、
        /// 割合をなめらかに（smoothstep で）0 へ減らした、頂点ごとの割合（0〜1）を返す。頂点はまとめた後の番号。
        /// </summary>
        public static float[] Spread(int vertexCount, int[] triangles, ReadOnlySpan<Vector3> positions, float[] seeds, float radius)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (positions.Length != vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(positions));
            if (seeds == null || seeds.Length != vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(seeds));
            var influence = new float[vertexCount];
            var distance = new float[vertexCount];
            var source = new float[vertexCount];
            var heap = new MinHeap(vertexCount);
            for (int v = 0; v < vertexCount; v++)
            {
                distance[v] = float.PositiveInfinity;
                float s = Math.Min(1f, seeds[v]);
                if (!(s > 0f)) continue;
                influence[v] = s;
                if (!(radius > 0f)) continue;
                distance[v] = 0f;
                source[v] = s;
                heap.Push(v, 0f);
            }
            if (!(radius > 0f)) return influence;

            BuildAdjacency(vertexCount, triangles, out int[] start, out int[] neighbors);
            while (heap.Count > 0)
            {
                heap.Pop(out int v, out float d);
                if (d > distance[v]) continue;
                for (int e = start[v]; e < start[v + 1]; e++)
                {
                    int n = neighbors[e];
                    float dx = positions[n].X - positions[v].X, dy = positions[n].Y - positions[v].Y, dz = positions[n].Z - positions[v].Z;
                    float nd = d + MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (!(nd < radius) || nd >= distance[n]) continue;
                    distance[n] = nd;
                    source[n] = source[v];
                    heap.Push(n, nd);
                }
            }
            for (int v = 0; v < vertexCount; v++)
            {
                if (float.IsPositiveInfinity(distance[v]) || influence[v] > 0f) continue;
                float x = distance[v] / radius;
                influence[v] = source[v] * (1f - x * x * (3f - 2f * x));
            }
            return influence;
        }

        /// <summary>
        /// 元の頂点ごとのウェイトを、まとめた後の頂点ごとのウェイトにする（同じ番号にまとめた頂点のウェイトの平均）。
        /// 体の頂点のウェイトを、<see cref="BodySurface"/> のまとめた後の頂点に合わせるのに使う。
        /// </summary>
        public static BoneWeightSets AverageToWelded(BoneWeightSets perSource, int[] sourceToWelded, int weldedCount, int boneCount)
        {
            if (perSource == null) throw new ArgumentNullException(nameof(perSource));
            if (sourceToWelded == null || sourceToWelded.Length != perSource.VertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(sourceToWelded));
            var members = new List<int>[weldedCount];
            for (int s = 0; s < sourceToWelded.Length; s++)
            {
                int w = sourceToWelded[s];
                if ((uint)w >= (uint)weldedCount) continue;
                (members[w] ?? (members[w] = new List<int>())).Add(s);
            }
            var builder = new WeightBuilder(weldedCount, boneCount);
            for (int w = 0; w < weldedCount; w++)
            {
                List<int> list = members[w];
                if (list != null)
                {
                    float share = 1f / list.Count;
                    foreach (int s in list) builder.AddRow(perSource, s, share);
                }
                builder.EndVertex(int.MaxValue, 0f);
            }
            return builder.Build();
        }

        /// <summary>
        /// 体のボーンごとに、服のボーンの番号を決める。direct（体のボーンごとの、服で同じ働きのボーンの番号。無ければ -1）が無いボーンは、
        /// 親（parents。無ければ -1）をたどって、対応のある一番近い親のボーンにする。どこまでたどっても無ければ -1。
        /// </summary>
        public static int[] ResolveBoneMap(IReadOnlyList<int> direct, IReadOnlyList<int> parents)
        {
            if (direct == null) throw new ArgumentNullException(nameof(direct));
            if (parents == null || parents.Count != direct.Count) throw new ArgumentException("親の数がボーンの数と違います。", nameof(parents));
            var result = new int[direct.Count];
            for (int b = 0; b < direct.Count; b++)
            {
                int mapped = -1;
                int guard = direct.Count;
                for (int x = b; x >= 0 && x < direct.Count && guard-- >= 0; x = parents[x])
                {
                    if (direct[x] < 0) continue;
                    mapped = direct[x];
                    break;
                }
                result[b] = mapped;
            }
            return result;
        }

        /// <summary>
        /// 1つのメッシュ（の頂点）について、ウェイトを体に寄せる。
        /// </summary>
        /// <param name="clothWeights">メッシュの頂点ごとの元のウェイト（服のボーンの番号）</param>
        /// <param name="vertexToWelded">まとめた頂点の並び（複数のメッシュのときは順に並べたもの）での、頂点ごとのまとめた後の番号</param>
        /// <param name="vertexStart">このメッシュの頂点が、vertexToWelded のどこから並ぶか</param>
        /// <param name="weldedTriangles">まとめた後の頂点の三角形（隣となじませるのに使う）</param>
        /// <param name="influence">まとめた後の頂点ごとの、混ぜる割合（<see cref="Spread"/>。強さはまだ掛けていないもの）</param>
        /// <param name="anchors">まとめた後の頂点ごとの、一番近い体の点</param>
        /// <param name="body">アンカーを測った体（三角形の頂点を引くのに使う）</param>
        /// <param name="bodyWeights">体のまとめた後の頂点ごとのウェイト（体のボーンの番号。<see cref="AverageToWelded"/>）</param>
        /// <param name="bodyBoneToCloth">体のボーンごとの服のボーンの番号（<see cref="ResolveBoneMap"/>。-1 は対応なし）</param>
        /// <param name="clothBoneCount">服のボーンの数（足すボーンも含む）</param>
        /// <param name="weldedPositions">まとめた後の頂点の、編集の姿勢での位置（一番近い体の点が飛ぶ所を見つけるのに使う。空なら、写した所をすべてならす）</param>
        public static WeightTransferResult Transfer(BoneWeightSets clothWeights, int[] vertexToWelded, int vertexStart, int[] weldedTriangles, float[] influence,
            BodyAnchors anchors, BodySurface body, BoneWeightSets bodyWeights, int[] bodyBoneToCloth, int clothBoneCount, WeightTransferOptions options,
            ReadOnlySpan<Vector3> weldedPositions = default)
        {
            if (clothWeights == null) throw new ArgumentNullException(nameof(clothWeights));
            if (vertexToWelded == null) throw new ArgumentNullException(nameof(vertexToWelded));
            if (weldedTriangles == null) throw new ArgumentNullException(nameof(weldedTriangles));
            if (influence == null) throw new ArgumentNullException(nameof(influence));
            if (anchors == null) throw new ArgumentNullException(nameof(anchors));
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (bodyWeights == null) throw new ArgumentNullException(nameof(bodyWeights));
            if (bodyBoneToCloth == null) throw new ArgumentNullException(nameof(bodyBoneToCloth));
            if (options == null) throw new ArgumentNullException(nameof(options));
            int count = clothWeights.VertexCount;
            if (vertexStart < 0 || vertexStart + count > vertexToWelded.Length) throw new ArgumentException("頂点の範囲が、まとめた頂点の並びを超えています。", nameof(vertexStart));
            int weldedCount = influence.Length;
            if (anchors.VertexCount != weldedCount) throw new ArgumentException("アンカーの数が、まとめた後の頂点の数と違います。", nameof(anchors));
            if (bodyWeights.VertexCount != body.VertexCount) throw new ArgumentException("体のウェイトの数が、体の頂点の数と違います。", nameof(bodyWeights));

            // このメッシュの頂点が使う、まとめた後の頂点で、混ぜる割合のあるもの
            var involved = new bool[weldedCount];
            for (int k = 0; k < count; k++)
            {
                int w = vertexToWelded[vertexStart + k];
                if ((uint)w < (uint)weldedCount && influence[w] > 0f) involved[w] = true;
            }

            if (weldedPositions.Length != 0 && weldedPositions.Length != weldedCount) throw new ArgumentException("頂点の数と合いません。", nameof(weldedPositions));

            var result = new WeightTransferResult();
            BoneWeightSets sampled = Sample(anchors, body, bodyWeights, bodyBoneToCloth, clothBoneCount, involved, out bool[] hasSample, out int missing, out int unmapped);
            result.MissingAnchors = missing;
            result.Unmapped = unmapped;
            sampled = SmoothSampled(sampled, hasSample, weldedTriangles, options.SmoothIterations, clothBoneCount, anchors, weldedPositions);

            float strength = Math.Max(0f, Math.Min(1f, options.Strength));
            int maxBones = Math.Max(1, options.MaxBonesPerVertex);
            var changed = new bool[count];
            var builder = new WeightBuilder(count, clothBoneCount);
            for (int k = 0; k < count; k++)
            {
                int w = vertexToWelded[vertexStart + k];
                float alpha = (uint)w < (uint)weldedCount && hasSample[w] ? strength * Math.Min(1f, influence[w]) * Proximity(anchors.GetDistance(w), options) : 0f;
                if (!(alpha > 0f))
                {
                    builder.AddRow(clothWeights, k, 1f);
                    builder.EndVertex(int.MaxValue, 0f, false);
                    continue;
                }
                builder.AddRow(clothWeights, k, 1f - alpha, true);
                builder.AddRow(sampled, w, alpha);
                builder.EndVertex(maxBones, options.MinWeight);
                changed[k] = !builder.LastRowEquals(clothWeights, k, 1e-4f);
                if (changed[k]) result.ChangedCount++;
            }
            result.Weights = builder.Build();
            result.Changed = changed;
            return result;
        }

        /// <summary>体からの距離 distance（メートル）による、混ぜる割合（0〜1。<see cref="WeightTransferOptions.FullDistance"/> 以下で 1、<see cref="WeightTransferOptions.ZeroDistance"/> 以上で 0）。</summary>
        public static float Proximity(float distance, WeightTransferOptions options)
        {
            float full = Math.Max(0f, options.FullDistance), zero = options.ZeroDistance;
            if (!(distance > full)) return 1f;
            if (!(zero > full) || distance >= zero) return 0f;
            float x = (distance - full) / (zero - full);
            return 1f - x * x * (3f - 2f * x);
        }

        // 混ぜる頂点ごとに、アンカーの点で体のウェイトを補い、服のボーンの番号にしたもの（合計 1）。対応の無いボーンの分は除く
        static BoneWeightSets Sample(BodyAnchors anchors, BodySurface body, BoneWeightSets bodyWeights, int[] bodyBoneToCloth, int clothBoneCount, bool[] involved,
            out bool[] hasSample, out int missing, out int unmapped)
        {
            int weldedCount = involved.Length;
            hasSample = new bool[weldedCount];
            missing = 0;
            unmapped = 0;
            var builder = new WeightBuilder(weldedCount, clothBoneCount);
            for (int w = 0; w < weldedCount; w++)
            {
                if (!involved[w])
                {
                    builder.EndVertex(int.MaxValue, 0f, false);
                    continue;
                }
                if (!anchors.Has(w))
                {
                    missing++;
                    builder.EndVertex(int.MaxValue, 0f, false);
                    continue;
                }
                body.GetTriangle(anchors.GetTriangle(w), out int a, out int b, out int c);
                anchors.GetBarycentric(w, out float u, out float v, out float x);
                builder.AddMappedRow(bodyWeights, a, u, bodyBoneToCloth);
                builder.AddMappedRow(bodyWeights, b, v, bodyBoneToCloth);
                builder.AddMappedRow(bodyWeights, c, x, bodyBoneToCloth);
                if (!builder.HasPending)
                {
                    unmapped++;
                    builder.EndVertex(int.MaxValue, 0f, false);
                    continue;
                }
                builder.EndVertex(int.MaxValue, 0f);
                hasSample[w] = true;
            }
            return builder.Build();
        }

        // 写したウェイトを、写した頂点どうしで隣となじませる（自分の半分 + 写した隣の平均の半分）。
        // positions があれば、一番近い体の点が隣で飛ぶ所（体の点どうしの距離が、布の頂点どうしの距離の2倍＋1cm より遠い）と、その周り iterations 輪の中だけで行う
        static BoneWeightSets SmoothSampled(BoneWeightSets sampled, bool[] hasSample, int[] triangles, int iterations, int clothBoneCount,
            BodyAnchors anchors, ReadOnlySpan<Vector3> positions)
        {
            if (iterations <= 0) return sampled;
            int n = sampled.VertexCount;
            BuildAdjacency(n, triangles, out int[] start, out int[] neighbors);
            var smooth = new bool[n];
            if (positions.Length == n)
            {
                for (int v = 0; v < n; v++)
                {
                    if (!hasSample[v]) continue;
                    Vector3 a = anchors.GetPoint(v);
                    for (int e = start[v]; e < start[v + 1] && !smooth[v]; e++)
                    {
                        int u = neighbors[e];
                        if (!hasSample[u]) continue;
                        Vector3 b = anchors.GetPoint(u);
                        float clothDistance = Vector3.Distance(positions[v], positions[u]);
                        if (Vector3.Distance(a, b) > 2f * clothDistance + 0.01f) smooth[v] = true;
                    }
                }
                // 周りへ広げる
                var next = new bool[n];
                for (int ring = 0; ring < iterations; ring++)
                {
                    Array.Copy(smooth, next, n);
                    for (int v = 0; v < n; v++)
                    {
                        if (!smooth[v]) continue;
                        for (int e = start[v]; e < start[v + 1]; e++)
                        {
                            if (hasSample[neighbors[e]]) next[neighbors[e]] = true;
                        }
                    }
                    Array.Copy(next, smooth, n);
                }
            }
            else
            {
                Array.Copy(hasSample, smooth, n);
            }
            BoneWeightSets current = sampled;
            for (int it = 0; it < iterations; it++)
            {
                var builder = new WeightBuilder(n, clothBoneCount);
                for (int v = 0; v < n; v++)
                {
                    if (!hasSample[v])
                    {
                        builder.EndVertex(int.MaxValue, 0f, false);
                        continue;
                    }
                    if (!smooth[v])
                    {
                        builder.AddRow(current, v, 1f);
                        builder.EndVertex(int.MaxValue, 0f, false);
                        continue;
                    }
                    int around = 0;
                    for (int e = start[v]; e < start[v + 1]; e++)
                    {
                        if (hasSample[neighbors[e]]) around++;
                    }
                    builder.AddRow(current, v, around > 0 ? 0.5f : 1f);
                    if (around > 0)
                    {
                        float each = 0.5f / around;
                        for (int e = start[v]; e < start[v + 1]; e++)
                        {
                            if (hasSample[neighbors[e]]) builder.AddRow(current, neighbors[e], each);
                        }
                    }
                    builder.EndVertex(int.MaxValue, 0f);
                }
                current = builder.Build();
            }
            return current;
        }

        // 三角形の辺でつながった頂点の一覧（CSR、重ねない）
        static void BuildAdjacency(int vertexCount, int[] triangles, out int[] start, out int[] neighbors)
        {
            var edges = new HashSet<long>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[t + e], b = triangles[t + (e + 1) % 3];
                    if (a == b || (uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount) continue;
                    edges.Add(PairKeys.Make(a, b));
                }
            }
            start = new int[vertexCount + 1];
            foreach (long key in edges)
            {
                PairKeys.Split(key, out int a, out int b);
                start[a + 1]++;
                start[b + 1]++;
            }
            for (int v = 0; v < vertexCount; v++) start[v + 1] += start[v];
            neighbors = new int[start[vertexCount]];
            var fill = (int[])start.Clone();
            foreach (long key in edges)
            {
                PairKeys.Split(key, out int a, out int b);
                neighbors[fill[a]++] = b;
                neighbors[fill[b]++] = a;
            }
        }

        // 頂点ごとのウェイトを、ボーンの番号の密な配列にためてから CSR にする作業用
        sealed class WeightBuilder
        {
            readonly float[] _accumulated;
            readonly List<int> _touched = new List<int>();
            readonly List<int> _bones;
            readonly List<float> _weights;
            readonly int[] _offsets;
            readonly int[] _sortBones = new int[64];
            readonly float[] _sortWeights = new float[64];
            int _vertex;

            public WeightBuilder(int vertexCount, int boneCount)
            {
                _accumulated = new float[Math.Max(1, boneCount)];
                _offsets = new int[vertexCount + 1];
                _bones = new List<int>(vertexCount * 2);
                _weights = new List<float>(vertexCount * 2);
            }

            public bool HasPending => _touched.Count > 0;

            void Add(int bone, float weight)
            {
                if (!(weight > 0f) || (uint)bone >= (uint)_accumulated.Length) return;
                if (_accumulated[bone] == 0f) _touched.Add(bone);
                _accumulated[bone] += weight;
            }

            // 行の値を scale 倍して足す。normalize なら、行の合計が 1 でなくても合計 1 にしてから足す
            public void AddRow(BoneWeightSets sets, int vertex, float scale, bool normalize = false)
            {
                if (!(scale > 0f)) return;
                int from = sets.Offsets[vertex], to = sets.Offsets[vertex + 1];
                float total = 0f;
                if (normalize)
                {
                    for (int k = from; k < to; k++) total += Math.Max(0f, sets.Weights[k]);
                    if (!(total > 0f)) return;
                }
                float factor = normalize ? scale / total : scale;
                for (int k = from; k < to; k++) Add(sets.Bones[k], sets.Weights[k] * factor);
            }

            // 体のボーンの行を、服のボーンの番号に付け替えて足す（対応の無いボーンの分は足さない）
            public void AddMappedRow(BoneWeightSets sets, int vertex, float scale, int[] map)
            {
                if (!(scale > 0f)) return;
                for (int k = sets.Offsets[vertex]; k < sets.Offsets[vertex + 1]; k++)
                {
                    int bone = sets.Bones[k];
                    int mapped = (uint)bone < (uint)map.Length ? map[bone] : -1;
                    if (mapped >= 0) Add(mapped, sets.Weights[k] * scale);
                }
            }

            // ためた値を、大きい順に maxBones 本まで（minWeight より小さいものは除く。最低 1 本は残す）、合計 1 にして書き出す。
            // normalize が false なら、ためた値をそのまま（並べ替えだけして）書き出す
            public void EndVertex(int maxBones, float minWeight, bool normalize = true)
            {
                int count = 0;
                foreach (int bone in _touched)
                {
                    float w = _accumulated[bone];
                    _accumulated[bone] = 0f;
                    if (count < _sortBones.Length)
                    {
                        _sortBones[count] = bone;
                        _sortWeights[count] = w;
                        count++;
                    }
                }
                _touched.Clear();
                // 大きい順（同じなら番号の小さいボーンが先）に並べると、結果が毎回同じになる
                for (int i = 1; i < count; i++)
                {
                    int b = _sortBones[i];
                    float w = _sortWeights[i];
                    int j = i - 1;
                    while (j >= 0 && (_sortWeights[j] < w || (_sortWeights[j] == w && _sortBones[j] > b)))
                    {
                        _sortBones[j + 1] = _sortBones[j];
                        _sortWeights[j + 1] = _sortWeights[j];
                        j--;
                    }
                    _sortBones[j + 1] = b;
                    _sortWeights[j + 1] = w;
                }
                int keep = Math.Min(count, Math.Max(1, maxBones));
                if (normalize)
                {
                    float total = 0f;
                    for (int i = 0; i < keep; i++) total += _sortWeights[i];
                    while (keep > 1 && _sortWeights[keep - 1] < minWeight * total)
                    {
                        keep--;
                        total -= _sortWeights[keep];
                    }
                    for (int i = 0; i < keep; i++)
                    {
                        _bones.Add(_sortBones[i]);
                        _weights.Add(total > 0f ? _sortWeights[i] / total : 0f);
                    }
                }
                else
                {
                    for (int i = 0; i < keep; i++)
                    {
                        _bones.Add(_sortBones[i]);
                        _weights.Add(_sortWeights[i]);
                    }
                }
                _vertex++;
                _offsets[_vertex] = _bones.Count;
            }

            // 最後に書き出した行が、sets の vertex の行（合計 1 にしたもの）と、同じボーン・tolerance 以内のウェイトか
            public bool LastRowEquals(BoneWeightSets sets, int vertex, float tolerance)
            {
                int from = _offsets[_vertex - 1], to = _offsets[_vertex];
                int sFrom = sets.Offsets[vertex], sTo = sets.Offsets[vertex + 1];
                float total = 0f;
                for (int k = sFrom; k < sTo; k++) total += Math.Max(0f, sets.Weights[k]);
                int matched = 0;
                for (int k = sFrom; k < sTo; k++)
                {
                    float expected = total > 0f ? sets.Weights[k] / total : 0f;
                    if (expected <= tolerance) continue;
                    bool found = false;
                    for (int i = from; i < to; i++)
                    {
                        if (_bones[i] != sets.Bones[k]) continue;
                        if (Math.Abs(_weights[i] - expected) > tolerance) return false;
                        found = true;
                        matched++;
                        break;
                    }
                    if (!found) return false;
                }
                for (int i = from; i < to; i++)
                {
                    if (_weights[i] > tolerance) matched--;
                }
                return matched == 0;
            }

            public BoneWeightSets Build()
            {
                return new BoneWeightSets((int[])_offsets.Clone(), _bones.ToArray(), _weights.ToArray());
            }
        }

        // 最小ヒープ（距離の小さい順に取り出す。同じ頂点を何度入れてもよい。古いものは取り出したときに捨てる）
        sealed class MinHeap
        {
            int[] _nodes;
            float[] _keys;
            int _count;

            public MinHeap(int capacity)
            {
                capacity = Math.Max(16, capacity);
                _nodes = new int[capacity];
                _keys = new float[capacity];
            }

            public int Count => _count;

            public void Push(int node, float key)
            {
                if (_count == _nodes.Length)
                {
                    Array.Resize(ref _nodes, 2 * _count);
                    Array.Resize(ref _keys, 2 * _count);
                }
                int i = _count++;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_keys[parent] <= key) break;
                    _nodes[i] = _nodes[parent];
                    _keys[i] = _keys[parent];
                    i = parent;
                }
                _nodes[i] = node;
                _keys[i] = key;
            }

            public void Pop(out int node, out float key)
            {
                node = _nodes[0];
                key = _keys[0];
                _count--;
                if (_count == 0) return;
                int lastNode = _nodes[_count];
                float lastKey = _keys[_count];
                int i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= _count) break;
                    if (child + 1 < _count && _keys[child + 1] < _keys[child]) child++;
                    if (_keys[child] >= lastKey) break;
                    _nodes[i] = _nodes[child];
                    _keys[i] = _keys[child];
                    i = child;
                }
                _nodes[i] = lastNode;
                _keys[i] = lastKey;
            }
        }
    }
}
