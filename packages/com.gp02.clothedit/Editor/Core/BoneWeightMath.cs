using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace GP02.Core
{
    /// <summary>
    /// ボーンの名前から、左右の相手を決める。Left/Right、.L/.R、_L/_R、L_ / R_ の接頭、左/右 などを扱う。
    /// 名前だけで決めるので、相手が名前に現れない骨（背骨など）は「相手なし」になる。
    /// </summary>
    public static class BoneNameMirror
    {
        static readonly char[] Delimiters = { '.', '_', '-', ' ' };

        /// <summary>左右の相手の名前を返す。左右を表す部分が無ければ false。</summary>
        public static bool TryGetMirror(string name, out string mirror)
        {
            mirror = null;
            if (string.IsNullOrEmpty(name)) return false;

            // 1) 区切り文字で分けた部分が L / R / Left / Right（大文字小文字は問わない）
            var builder = new StringBuilder(name.Length + 4);
            bool swapped = false;
            int start = 0;
            for (int i = 0; i <= name.Length; i++)
            {
                bool end = i == name.Length;
                if (!end && Array.IndexOf(Delimiters, name[i]) < 0) continue;
                string token = name.Substring(start, i - start);
                string other = swapped ? null : SwapWord(token);
                if (other != null) swapped = true;
                builder.Append(other ?? token);
                if (!end) builder.Append(name[i]);
                start = i + 1;
            }
            if (swapped)
            {
                mirror = builder.ToString();
                return true;
            }

            // 2) 日本語の 左 / 右
            int left = name.IndexOf('左'), right = name.IndexOf('右');
            if (left >= 0 || right >= 0)
            {
                int at = left >= 0 && (right < 0 || left < right) ? left : right;
                mirror = name.Substring(0, at) + (name[at] == '左' ? '右' : '左') + name.Substring(at + 1);
                return true;
            }

            // 3) 単語が続けて書かれている: LeftHand / HandRight
            string[,] pairs = { { "Left", "Right" }, { "Right", "Left" }, { "left", "right" }, { "right", "left" } };
            for (int p = 0; p < pairs.GetLength(0); p++)
            {
                string word = pairs[p, 0], opposite = pairs[p, 1];
                if (name.Length > word.Length && name.StartsWith(word, StringComparison.Ordinal) && StartsNewWord(name, word.Length))
                {
                    mirror = opposite + name.Substring(word.Length);
                    return true;
                }
                if (name.Length > word.Length && name.EndsWith(word, StringComparison.Ordinal) && IsWordBoundaryBefore(name, name.Length - word.Length, word))
                {
                    mirror = name.Substring(0, name.Length - word.Length) + opposite;
                    return true;
                }
            }

            // 4) 1 文字の接頭・接尾: LHand / ArmL（誤って「Lower」などを左右にしないよう、続く文字が大文字のときと、前が小文字・数字のときだけ）
            if (name.Length > 2 && (name[0] == 'L' || name[0] == 'R') && char.IsUpper(name[1]))
            {
                mirror = (name[0] == 'L' ? "R" : "L") + name.Substring(1);
                return true;
            }
            char last = name[name.Length - 1];
            if (name.Length > 2 && (last == 'L' || last == 'R') && (char.IsLower(name[name.Length - 2]) || char.IsDigit(name[name.Length - 2])))
            {
                mirror = name.Substring(0, name.Length - 1) + (last == 'L' ? "R" : "L");
                return true;
            }
            return false;
        }

        // token が左右を表す単語ならその反対を、そうでなければ null
        static string SwapWord(string token)
        {
            switch (token.ToLowerInvariant())
            {
                case "l": return char.IsUpper(token[0]) ? "R" : "r";
                case "r": return char.IsUpper(token[0]) ? "L" : "l";
                case "left": return MatchCase(token, "right", "Right", "RIGHT");
                case "right": return MatchCase(token, "left", "Left", "LEFT");
                default: return null;
            }
        }

        static string MatchCase(string token, string lower, string title, string upper)
        {
            if (token == token.ToUpperInvariant()) return upper;
            return char.IsUpper(token[0]) ? title : lower;
        }

        // 接頭の単語の次が、新しい単語の始まり（大文字・数字・区切り）か
        static bool StartsNewWord(string name, int index) => char.IsUpper(name[index]) || char.IsDigit(name[index]);

        // 接尾の単語の前が、単語の境目か（小文字や数字のあとに、大文字で始まる単語が続く）
        static bool IsWordBoundaryBefore(string name, int index, string word) => index > 0 && char.IsUpper(word[0]) && !char.IsUpper(name[index - 1]);

        /// <summary>
        /// ボーンの名前の並びから、ボーンの番号ごとの左右の相手の番号を作る。
        /// 相手がいない骨（名前に左右が無い、相手の名前の骨が無い）は自分の番号になる。
        /// </summary>
        public static int[] BuildIndexMap(IReadOnlyList<string> names)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            var indexOf = new Dictionary<string, int>();
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] != null && !indexOf.ContainsKey(names[i])) indexOf.Add(names[i], i);
            }
            var map = new int[names.Count];
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = i;
                if (names[i] != null && TryGetMirror(names[i], out string mirror) && indexOf.TryGetValue(mirror, out int j)) map[i] = j;
            }
            return map;
        }
    }

    /// <summary>
    /// 頂点ごとのボーンウェイトの集まり（CSR 形式）。頂点 v のウェイトは Offsets[v] から Offsets[v + 1] の手前まで。
    /// 1 頂点に付くボーンの数は頂点ごとに違ってよい。
    /// </summary>
    public sealed class BoneWeightSets
    {
        public readonly int[] Offsets;
        public readonly int[] Bones;
        public readonly float[] Weights;

        public int VertexCount => Offsets.Length - 1;

        public BoneWeightSets(int[] offsets, int[] bones, float[] weights)
        {
            if (offsets == null || offsets.Length == 0) throw new ArgumentException("頂点の区切りが空です。", nameof(offsets));
            if (bones == null) throw new ArgumentNullException(nameof(bones));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (bones.Length != weights.Length || offsets[offsets.Length - 1] != bones.Length) throw new ArgumentException("ボーンとウェイトの数が合いません。");
            Offsets = offsets;
            Bones = bones;
            Weights = weights;
        }
    }

    /// <summary>ボーンウェイトの付け替え（大きく動いた頂点のウェイトを、左右反転やなめらかにする）。</summary>
    public static class BoneWeightMath
    {
        /// <summary>頂点 v のウェイトを donor[v] 番目の頂点のものに差し替えた、新しい集まりを返す。</summary>
        public static BoneWeightSets TakeFrom(BoneWeightSets source, int[] donor)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (donor == null) throw new ArgumentNullException(nameof(donor));
            if (donor.Length != source.VertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(donor));

            var offsets = new int[donor.Length + 1];
            for (int v = 0; v < donor.Length; v++) offsets[v + 1] = offsets[v] + source.Offsets[donor[v] + 1] - source.Offsets[donor[v]];
            var bones = new int[offsets[donor.Length]];
            var weights = new float[bones.Length];
            for (int v = 0; v < donor.Length; v++)
            {
                int from = source.Offsets[donor[v]], count = source.Offsets[donor[v] + 1] - from;
                Array.Copy(source.Bones, from, bones, offsets[v], count);
                Array.Copy(source.Weights, from, weights, offsets[v], count);
            }
            return new BoneWeightSets(offsets, bones, weights);
        }

        /// <summary>
        /// selected の頂点のボーンを、boneMap で別のボーンに置き換えた集まりを返す（ウェイトの大きさは変えない）。
        /// 置き換えで同じボーンが重なったら足し合わせる。
        /// </summary>
        public static BoneWeightSets MapBones(BoneWeightSets source, int[] boneMap, bool[] selected)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (boneMap == null) throw new ArgumentNullException(nameof(boneMap));
            if (selected == null || selected.Length != source.VertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(selected));

            var bones = (int[])source.Bones.Clone();
            var weights = (float[])source.Weights.Clone();
            for (int v = 0; v < source.VertexCount; v++)
            {
                if (!selected[v]) continue;
                for (int k = source.Offsets[v]; k < source.Offsets[v + 1]; k++)
                {
                    int bone = bones[k];
                    if ((uint)bone < (uint)boneMap.Length) bones[k] = boneMap[bone];
                }
            }
            // 同じボーンが重なった頂点だけ、まとめ直す
            var offsets = source.Offsets;
            bool anyDuplicate = false;
            for (int v = 0; v < source.VertexCount && !anyDuplicate; v++)
            {
                if (!selected[v]) continue;
                for (int a = offsets[v]; a < offsets[v + 1] && !anyDuplicate; a++)
                {
                    for (int b = a + 1; b < offsets[v + 1]; b++)
                    {
                        if (bones[a] == bones[b])
                        {
                            anyDuplicate = true;
                            break;
                        }
                    }
                }
            }
            if (!anyDuplicate) return new BoneWeightSets(offsets, bones, weights);

            var newOffsets = new int[source.VertexCount + 1];
            var newBones = new List<int>(bones.Length);
            var newWeights = new List<float>(bones.Length);
            for (int v = 0; v < source.VertexCount; v++)
            {
                int begin = newBones.Count;
                for (int k = offsets[v]; k < offsets[v + 1]; k++)
                {
                    int at = -1;
                    for (int j = begin; j < newBones.Count; j++)
                    {
                        if (newBones[j] == bones[k])
                        {
                            at = j;
                            break;
                        }
                    }
                    if (at >= 0) newWeights[at] += weights[k];
                    else
                    {
                        newBones.Add(bones[k]);
                        newWeights.Add(weights[k]);
                    }
                }
                newOffsets[v + 1] = newBones.Count;
            }
            return new BoneWeightSets(newOffsets, newBones.ToArray(), newWeights.ToArray());
        }

        /// <summary>
        /// selected の頂点のウェイトを、隣の頂点（三角形の辺でつながる頂点）のウェイトとなじませる。
        /// 各回で「自分の半分 + 隣の平均の半分」にし、ウェイトの大きいほうから maxBonesPerVertex 個だけ残して、合計を 1 にそろえる。
        /// selected でない頂点は変えない（隣として値を読むだけ）。
        /// </summary>
        public static BoneWeightSets Smooth(BoneWeightSets source, bool[] selected, Vector3[] positions, int[] triangles, int iterations, int maxBonesPerVertex)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (selected == null || selected.Length != source.VertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(selected));
            if (positions == null || positions.Length != source.VertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (maxBonesPerVertex < 1) throw new ArgumentOutOfRangeException(nameof(maxBonesPerVertex));
            if (iterations <= 0) return source;

            int count = source.VertexCount;
            int[] vertexToWelded = VertexWelder.Weld(positions, MeshTopology.DefaultWeldTolerance, out Vector3[] weldedPositions);
            int[][] neighbors = ShapeMask.BuildNeighbors(vertexToWelded, weldedPositions.Length, triangles);
            var representative = new int[weldedPositions.Length];
            for (int w = 0; w < representative.Length; w++) representative[w] = -1;
            for (int v = 0; v < count; v++)
            {
                if (representative[vertexToWelded[v]] < 0) representative[vertexToWelded[v]] = v;
            }

            BoneWeightSets current = source;
            var accumulated = new Dictionary<int, float>();
            var sorted = new List<KeyValuePair<int, float>>();
            for (int it = 0; it < iterations; it++)
            {
                var offsets = new int[count + 1];
                var bones = new List<int>(current.Bones.Length);
                var weights = new List<float>(current.Weights.Length);
                for (int v = 0; v < count; v++)
                {
                    if (!selected[v])
                    {
                        for (int k = current.Offsets[v]; k < current.Offsets[v + 1]; k++)
                        {
                            bones.Add(current.Bones[k]);
                            weights.Add(current.Weights[k]);
                        }
                        offsets[v + 1] = bones.Count;
                        continue;
                    }

                    accumulated.Clear();
                    int[] around = neighbors[vertexToWelded[v]];
                    float ownShare = around.Length > 0 ? 0.5f : 1f;
                    Add(accumulated, current, v, ownShare);
                    if (around.Length > 0)
                    {
                        float each = 0.5f / around.Length;
                        for (int n = 0; n < around.Length; n++) Add(accumulated, current, representative[around[n]], each);
                    }

                    sorted.Clear();
                    foreach (KeyValuePair<int, float> pair in accumulated) sorted.Add(pair);
                    // 大きい順（同じなら番号の小さいボーン）にそろえると、結果が毎回同じになる
                    sorted.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
                    int keep = Math.Min(sorted.Count, maxBonesPerVertex);
                    float total = 0f;
                    for (int k = 0; k < keep; k++) total += sorted[k].Value;
                    for (int k = 0; k < keep; k++)
                    {
                        bones.Add(sorted[k].Key);
                        weights.Add(total > 0f ? sorted[k].Value / total : 0f);
                    }
                    offsets[v + 1] = bones.Count;
                }
                current = new BoneWeightSets(offsets, bones.ToArray(), weights.ToArray());
            }
            return current;
        }

        static void Add(Dictionary<int, float> into, BoneWeightSets sets, int vertex, float scale)
        {
            for (int k = sets.Offsets[vertex]; k < sets.Offsets[vertex + 1]; k++)
            {
                into.TryGetValue(sets.Bones[k], out float old);
                into[sets.Bones[k]] = old + sets.Weights[k] * scale;
            }
        }
    }
}
