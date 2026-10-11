using System;
using System.Collections.Generic;
using GP02.Core;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>ボーンウェイトの付け替えかた。</summary>
    public enum BoneWeightRemapMode
    {
        /// <summary>動いた先に一番近い頂点のウェイトをもらう。</summary>
        Nearest = 0,

        /// <summary>反対側へ動いた頂点は、自分のウェイトのボーンを左右反対のボーン（名前で決める）に付け替える。</summary>
        Mirror = 1,
    }

    /// <summary><see cref="ShapeKeyLibrary.RemapBoneWeights(Mesh, Vector3[], BoneWeightRemapOptions)"/> の設定。</summary>
    public sealed class BoneWeightRemapOptions
    {
        public BoneWeightRemapMode Mode = BoneWeightRemapMode.Nearest;

        /// <summary>これ以上動いた頂点（メートル）だけ付け替える。</summary>
        public float Threshold = 0.05f;

        /// <summary>左右の軸（0=X, 1=Y, 2=Z。メッシュ空間）。<see cref="BoneWeightRemapMode.Mirror"/> のとき使う。</summary>
        public int Axis;

        /// <summary>左右の境の位置（軸の値）。<see cref="BoneWeightRemapMode.Mirror"/> のとき使う。</summary>
        public float Center;

        /// <summary>付け替えた頂点のウェイトを、隣の頂点となじませる回数。0 ならなじませない。</summary>
        public int SmoothIterations;

        /// <summary>メッシュのボーンの番号ごとの名前（レンダラーの bones の名前）。<see cref="BoneWeightRemapMode.Mirror"/> のとき、左右の相手を決めるのに使う。</summary>
        public string[] BoneNames;
    }

    public static partial class ShapeKeyLibrary
    {
        // ------------------------------------------------------------------
        // AnimationClip のすべてのキーフレームを in-between として取り込む
        // ------------------------------------------------------------------

        /// <summary>キーフレームを in-between として取り込むときの、フレーム数の上限（大きなメッシュでデータが膨らみすぎないように）。</summary>
        public const int MaxClipFrames = 32;

        // クリップの中の、このレンダラーの BlendShape のカーブ（メッシュに無い BlendShape は除く）。キーはメッシュの BlendShape の番号
        internal static List<KeyValuePair<int, AnimationCurve>> CollectBlendShapeCurves(Mesh mesh, AnimationClip clip, string rendererPath)
        {
            var result = new List<KeyValuePair<int, AnimationCurve>>();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || binding.path != rendererPath) continue;
                if (!binding.propertyName.StartsWith(BlendShapeCurvePrefix)) continue;
                int shape = mesh.GetBlendShapeIndex(binding.propertyName.Substring(BlendShapeCurvePrefix.Length));
                if (shape < 0) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null) result.Add(new KeyValuePair<int, AnimationCurve>(shape, curve));
            }
            return result;
        }

        // 時刻 time での BlendShape の値で既存の BlendShape を合成した、重み 100 のフレーム。0 以外の値が無ければ null
        internal static ShapeKeyFrame ComposeFromCurves(Mesh mesh, List<KeyValuePair<int, AnimationCurve>> curves, float time, Dictionary<int, ShapeKeyEntry> shapeCache)
        {
            int count = mesh.vertexCount;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector3[count];
            bool anyNormals = false, anyTangents = false;
            int used = 0;
            foreach (var pair in curves)
            {
                float weight = pair.Value.Evaluate(time);
                if (weight == 0f) continue;
                if (!shapeCache.TryGetValue(pair.Key, out ShapeKeyEntry shapeEntry))
                {
                    shapeEntry = ImportFromBlendShape(mesh, pair.Key);
                    shapeCache[pair.Key] = shapeEntry;
                }
                Accumulate(vertices, EvaluateChannel(shapeEntry, weight, count, frame => frame.DeltaVertices));
                if (AnyFrame(shapeEntry, frame => frame.HasNormals))
                {
                    anyNormals = true;
                    Accumulate(normals, EvaluateChannel(shapeEntry, weight, count, frame => frame.DeltaNormals));
                }
                if (AnyFrame(shapeEntry, frame => frame.HasTangents))
                {
                    anyTangents = true;
                    Accumulate(tangents, EvaluateChannel(shapeEntry, weight, count, frame => frame.DeltaTangents));
                }
                used++;
            }
            if (used == 0) return null;
            return new ShapeKeyFrame
            {
                Weight = ShapeKeyMath.FullWeight,
                DeltaVertices = vertices,
                DeltaNormals = anyNormals ? normals : new Vector3[0],
                DeltaTangents = anyTangents ? tangents : new Vector3[0],
            };
        }

        /// <summary>
        /// アニメーションクリップの、すべてのキーフレームの時刻の BlendShape の値で既存の BlendShape を合成し、時刻の順に in-between のフレームにしたエントリを作る。
        /// 時刻が同じなら 1 つにまとめる。BlendShape の値がすべて 0 の時刻（元の形）は、フレームにしない。
        /// フレームの重みは、n 個なら 100/n, 200/n, …, 100（等間隔）。各フレームはその時刻の形そのもの（Crossfade）。名前はクリップの名前。
        /// </summary>
        /// <exception cref="InvalidOperationException">取り込める BlendShape の値が無い、またはキーフレームが <see cref="MaxClipFrames"/> を超えるとき</exception>
        public static ShapeKeyEntry ImportFromAnimationClipKeyframes(Mesh mesh, AnimationClip clip, string rendererPath)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            rendererPath = rendererPath ?? "";

            var curves = CollectBlendShapeCurves(mesh, clip, rendererPath);
            var times = new SortedSet<float>();
            foreach (var pair in curves)
            {
                foreach (Keyframe key in pair.Value.keys) times.Add(Mathf.Round(key.time * 10000f) / 10000f);
            }

            var cache = new Dictionary<int, ShapeKeyEntry>();
            var frames = new List<ShapeKeyFrame>();
            foreach (float time in times)
            {
                ShapeKeyFrame frame = ComposeFromCurves(mesh, curves, time, cache);
                if (frame != null) frames.Add(frame);
            }
            if (frames.Count == 0)
            {
                throw new InvalidOperationException(
                    $"アニメーションクリップ「{clip.name}」のキーフレームに、パス「{rendererPath}」の SkinnedMeshRenderer が持つ、メッシュ「{mesh.name}」の BlendShape の値（0 以外）がありません。");
            }
            if (frames.Count > MaxClipFrames)
            {
                throw new InvalidOperationException(
                    $"キーフレームの時刻が {frames.Count} 個あり、上限（{MaxClipFrames}）を超えています。クリップのキーフレームを減らしてから取り込んでください。");
            }

            var entry = new ShapeKeyEntry { Name = clip.name, InBetweenMode = ShapeKeyInBetweenMode.Crossfade };
            for (int i = 0; i < frames.Count; i++)
            {
                frames[i].Weight = i == frames.Count - 1 ? ShapeKeyMath.FullWeight : ShapeKeyMath.FullWeight * (i + 1) / frames.Count;
                entry.Frames.Add(frames[i]);
            }
            return entry;
        }

        // ------------------------------------------------------------------
        // マスク（頂点ごとの 0〜1 の重み）・差し引き
        // ------------------------------------------------------------------

        /// <summary>
        /// 全フレームの差分に、マスクの重みを掛けた新しいエントリを返す。complement が true なら 1 - マスク を掛ける（範囲の外だけ残す）。
        /// </summary>
        public static ShapeKeyEntry MaskEntry(ShapeKeyEntry entry, float[] mask, bool complement, string newName = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            return MapEntry(entry, newName ?? entry.Name, deltas =>
            {
                if (deltas.Length != mask.Length) throw new ArgumentException($"マスクの数（{mask.Length}）が頂点数（{deltas.Length}）と違います。", nameof(mask));
                NVector3[] numerics = ToNumericsArray(deltas);
                return ToUnityArray(complement ? ShapeMask.MultiplyComplement(numerics, mask) : ShapeMask.Multiply(numerics, mask));
            });
        }

        /// <summary>
        /// a - b の新しいエントリ（重み 100 の1フレーム）を返す。a と b は、それぞれ重み 100 での形で引く（in-between も評価する）。
        /// 法線・接線は、どちらかにあるときだけ差を取る。
        /// </summary>
        public static ShapeKeyEntry SubtractEntries(ShapeKeyEntry a, ShapeKeyEntry b, int vertexCount, string name)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            ShapeKeyFrame fa = EvaluateEntry(a, ShapeKeyMath.FullWeight, vertexCount);
            ShapeKeyFrame fb = EvaluateEntry(b, ShapeKeyMath.FullWeight, vertexCount);

            var frame = new ShapeKeyFrame { Weight = ShapeKeyMath.FullWeight, DeltaVertices = Difference(fa.DeltaVertices, fb.DeltaVertices, vertexCount) };
            if (fa.HasNormals || fb.HasNormals) frame.DeltaNormals = Difference(fa.DeltaNormals, fb.DeltaNormals, vertexCount);
            if (fa.HasTangents || fb.HasTangents) frame.DeltaTangents = Difference(fa.DeltaTangents, fb.DeltaTangents, vertexCount);

            var entry = new ShapeKeyEntry { Name = name, Group = a.Group };
            entry.Frames.Add(frame);
            return entry;
        }

        static Vector3[] Difference(Vector3[] a, Vector3[] b, int count)
        {
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 x = a != null && a.Length == count ? a[i] : Vector3.zero;
                Vector3 y = b != null && b.Length == count ? b[i] : Vector3.zero;
                result[i] = x - y;
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 別のメッシュへ写す
        // ------------------------------------------------------------------

        /// <summary>
        /// 写し先のメッシュの頂点ごとに、元のメッシュの元の形の表面で一番近い点を探して、対応（三角形と重心座標）を作る。
        /// 写し先の法線は、写し先のメッシュの法線（無ければ三角形から計算）。
        /// </summary>
        /// <param name="maxDistance">これより遠い頂点は写さない（メートル）</param>
        /// <param name="minNormalDot">元の面の向きと写し先の法線の内積がこれ未満の面は避ける。0 で逆向きの面を避ける。-1 以下で向きを問わない</param>
        public static ShapeTransfer.Mapping CreateTransferMapping(Mesh sourceRest, Mesh target, float maxDistance, float minNormalDot)
        {
            if (sourceRest == null) throw new ArgumentNullException(nameof(sourceRest));
            if (target == null) throw new ArgumentNullException(nameof(target));
            Vector3[] normals = target.normals;
            return ShapeTransfer.Map(ToNumericsArray(sourceRest.vertices), sourceRest.triangles,
                ToNumericsArray(target.vertices), normals.Length == target.vertexCount ? ToNumericsArray(normals) : null,
                normals.Length == target.vertexCount ? null : target.triangles, maxDistance, minNormalDot);
        }

        /// <summary>
        /// エントリの全フレーム（位置・法線・接線）を、対応を使って写し先の頂点数の差分にした、新しいエントリを返す。
        /// ピン留めは頂点の番号が変わるので引き継がない。
        /// </summary>
        public static ShapeKeyEntry TransferEntry(ShapeKeyEntry entry, ShapeTransfer.Mapping mapping, string newName = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (mapping == null) throw new ArgumentNullException(nameof(mapping));
            var result = new ShapeKeyEntry
            {
                Name = newName ?? entry.Name,
                Enabled = entry.Enabled,
                Group = entry.Group,
                InBetweenMode = entry.InBetweenMode,
            };
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                result.Frames.Add(new ShapeKeyFrame
                {
                    Weight = frame.Weight,
                    DeltaVertices = ToUnityArray(ShapeTransfer.Apply(mapping, ToNumericsArray(frame.DeltaVertices))),
                    DeltaNormals = frame.HasNormals ? ToUnityArray(ShapeTransfer.Apply(mapping, ToNumericsArray(frame.DeltaNormals))) : new Vector3[0],
                    DeltaTangents = frame.HasTangents ? ToUnityArray(ShapeTransfer.Apply(mapping, ToNumericsArray(frame.DeltaTangents))) : new Vector3[0],
                });
            }
            return result;
        }

        // ------------------------------------------------------------------
        // ボーンウェイトの付け替え（左右反転・なめらかにする回数つき）
        // ------------------------------------------------------------------

        /// <summary>
        /// 大きく動いた頂点（差分の長さが <see cref="BoneWeightRemapOptions.Threshold"/> 以上）のボーンウェイトを付け替えた新しいメッシュを返す。
        /// <see cref="BoneWeightRemapMode.Nearest"/> は、動いた先に一番近い（元の形の）頂点のウェイトをもらう。
        /// <see cref="BoneWeightRemapMode.Mirror"/> は、左右の境を越えて反対側へ動いた頂点だけ、自分のウェイトのボーンを左右反対のボーン（名前で決める）に付け替える。
        /// どちらも、付け替えた頂点のウェイトを隣の頂点となじませる回数（<see cref="BoneWeightRemapOptions.SmoothIterations"/>）を指定できる。
        /// 付け替える前のウェイトからもらう（付け替えた結果をさらにもらうことはない）。
        /// </summary>
        /// <exception cref="InvalidOperationException">メッシュにボーンウェイトが無いとき</exception>
        public static Mesh RemapBoneWeights(Mesh mesh, Vector3[] deltas, BoneWeightRemapOptions options)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (options == null) throw new ArgumentNullException(nameof(options));
            int count = mesh.vertexCount;
            if (deltas.Length != count) throw new ArgumentException($"差分の数（{deltas.Length}）がメッシュの頂点数（{count}）と違います。", nameof(deltas));
            NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
            if (bonesPerVertex.Length != count) throw new InvalidOperationException($"メッシュ「{mesh.name}」にボーンウェイトがありません。");
            NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();

            var offsets = new int[count + 1];
            int maxBones = 1;
            for (int v = 0; v < count; v++)
            {
                offsets[v + 1] = offsets[v] + bonesPerVertex[v];
                maxBones = Math.Max(maxBones, bonesPerVertex[v]);
            }
            var bones = new int[allWeights.Length];
            var weights = new float[allWeights.Length];
            for (int k = 0; k < bones.Length; k++)
            {
                bones[k] = allWeights[k].boneIndex;
                weights[k] = allWeights[k].weight;
            }
            var sets = new BoneWeightSets(offsets, bones, weights);

            Vector3[] positions = mesh.vertices;
            float thresholdSq = options.Threshold * options.Threshold;
            var changed = new bool[count];
            BoneWeightSets result;
            if (options.Mode == BoneWeightRemapMode.Mirror)
            {
                if (options.BoneNames == null) throw new ArgumentException("左右の相手を決めるには、ボーンの名前が要ります。", nameof(options));
                int axis = Math.Max(0, Math.Min(2, options.Axis));
                for (int v = 0; v < count; v++)
                {
                    if (deltas[v].sqrMagnitude < thresholdSq) continue;
                    float before = positions[v][axis] - options.Center;
                    float after = positions[v][axis] + deltas[v][axis] - options.Center;
                    changed[v] = before * after < 0f;
                }
                result = BoneWeightMath.MapBones(sets, BoneNameMirror.BuildIndexMap(options.BoneNames), changed);
            }
            else
            {
                int[] donor = NearestDonors(positions, deltas, thresholdSq, changed);
                result = BoneWeightMath.TakeFrom(sets, donor);
            }
            if (options.SmoothIterations > 0)
            {
                // 隣のウェイトが混ざると影響するボーンが増えるので、Unity の標準（4 本）までは残す
                result = BoneWeightMath.Smooth(result, changed, ToNumericsArray(positions), mesh.triangles, options.SmoothIterations, Math.Max(maxBones, 4));
            }
            return WithBoneWeights(mesh, result);
        }

        static Mesh WithBoneWeights(Mesh mesh, BoneWeightSets sets)
        {
            int count = sets.VertexCount;
            var newBones = new NativeArray<byte>(count, Allocator.Temp);
            var newWeights = new NativeArray<BoneWeight1>(sets.Bones.Length, Allocator.Temp);
            try
            {
                var order = new List<BoneWeight1>();
                int write = 0;
                for (int v = 0; v < count; v++)
                {
                    order.Clear();
                    for (int k = sets.Offsets[v]; k < sets.Offsets[v + 1]; k++) order.Add(new BoneWeight1 { boneIndex = sets.Bones[k], weight = sets.Weights[k] });
                    // 大きい順にそろえる（同じ重みなら番号の小さいボーンが先）
                    order.Sort((a, b) => a.weight != b.weight ? b.weight.CompareTo(a.weight) : a.boneIndex.CompareTo(b.boneIndex));
                    newBones[v] = (byte)order.Count;
                    foreach (BoneWeight1 w in order) newWeights[write++] = w;
                }
                Mesh result = Object.Instantiate(mesh);
                result.name = NameWithSuffix(mesh.name);
                result.SetBoneWeights(newBones, newWeights);
                return result;
            }
            finally
            {
                newBones.Dispose();
                newWeights.Dispose();
            }
        }

        // 動いた頂点（changed に印を付ける）ごとの、動いた先に一番近い頂点。動かない頂点は自分自身
        static int[] NearestDonors(Vector3[] positions, Vector3[] deltas, float thresholdSq, bool[] changed)
        {
            int count = positions.Length;
            var grid = new PointGrid(0.02f);
            for (int v = 0; v < count; v++) grid.Add(v, new NVector3(positions[v].x, positions[v].y, positions[v].z));

            var donor = new int[count];
            var searchResult = new List<int>();
            for (int v = 0; v < count; v++)
            {
                donor[v] = v;
                if (deltas[v].sqrMagnitude < thresholdSq) continue;
                changed[v] = true;
                Vector3 moved = positions[v] + deltas[v];
                var target = new NVector3(moved.x, moved.y, moved.z);
                int best = -1;
                float bestDistance = float.MaxValue;
                // 近い頂点が見つかるまで、探す半径を広げる。見つかった最も近い点が半径の内側なら、それが全体で一番近い
                for (float radius = 0.02f; radius <= MaxGridSearchRadius && best < 0; radius *= 2f)
                {
                    grid.Query(target, radius, searchResult);
                    foreach (int u in searchResult)
                    {
                        float d = (positions[u] - moved).sqrMagnitude;
                        if (d < bestDistance && d <= radius * radius)
                        {
                            bestDistance = d;
                            best = u;
                        }
                    }
                }
                if (best < 0)
                {
                    // 遠くに動いた頂点は、格子で探すとセルを調べすぎるので、全部の頂点から探す
                    for (int u = 0; u < count; u++)
                    {
                        float d = (positions[u] - moved).sqrMagnitude;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = u;
                        }
                    }
                }
                donor[v] = best;
            }
            return donor;
        }
    }
}
