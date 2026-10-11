using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 頂点ごとの重み（マスク、0〜1）を使った、シェイプキーの差分の操作。
    /// 布の編集で選んだ点の範囲や、分割の平面から作ったマスクで、差分を「範囲の中」と「それ以外」に分けるのに使う。
    /// どの関数も入力は変更せず、新しい配列を返す。
    /// </summary>
    public static class ShapeMask
    {
        /// <summary>差分の各頂点に、マスクの重みを掛ける（範囲の中だけ取り出す）。</summary>
        public static Vector3[] Multiply(Vector3[] deltas, float[] mask)
        {
            Check(deltas, mask);
            var result = new Vector3[deltas.Length];
            for (int i = 0; i < deltas.Length; i++) result[i] = deltas[i] * mask[i];
            return result;
        }

        /// <summary>差分の各頂点に、1 - マスクの重みを掛ける（範囲の中を消す）。<see cref="Multiply"/> との和は元の差分に戻る。</summary>
        public static Vector3[] MultiplyComplement(Vector3[] deltas, float[] mask)
        {
            Check(deltas, mask);
            var result = new Vector3[deltas.Length];
            for (int i = 0; i < deltas.Length; i++)
            {
                // 引き算で作ると、足し直したときに丸め誤差も含めて元に戻る
                result[i] = deltas[i] - deltas[i] * mask[i];
            }
            return result;
        }

        /// <summary>マスクを 0〜1 に収める（範囲外や NaN は 0 か 1 にする）。</summary>
        public static float[] Clamp01(float[] mask)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            var result = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                float m = mask[i];
                result[i] = float.IsNaN(m) ? 0f : Math.Min(Math.Max(m, 0f), 1f);
            }
            return result;
        }

        /// <summary>1 - マスク。</summary>
        public static float[] Invert(float[] mask)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            var result = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++) result[i] = 1f - mask[i];
            return result;
        }

        /// <summary>
        /// 平面で分けるマスク。平面の法線方向の距離（normal・位置 - offset）が正の側が 1、負の側が 0 で、
        /// 平面の両側 blendWidth / 2 の幅でなめらかに（smoothstep）移り変わる。
        /// </summary>
        /// <param name="normal">平面の法線（長さは問わない。0 ベクトルは不可）</param>
        /// <param name="offset">平面の位置。正規化した法線での、原点からの距離</param>
        /// <param name="blendWidth">なめらかに移り変わる幅（メートル）。0 以下ならぴったり分ける（平面の上は 0.5）</param>
        public static float[] PlaneMask(Vector3[] positions, Vector3 normal, float offset, float blendWidth)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            float length = normal.Length();
            if (!(length > 1e-12f)) throw new ArgumentException("平面の法線が 0 です。", nameof(normal));
            normal /= length;

            var mask = new float[positions.Length];
            float half = blendWidth * 0.5f;
            for (int i = 0; i < positions.Length; i++)
            {
                float s = Vector3.Dot(positions[i], normal) - offset;
                if (half <= 0f)
                {
                    mask[i] = s > 0f ? 1f : s < 0f ? 0f : 0.5f;
                }
                else
                {
                    float t = Math.Min(Math.Max(0.5f + 0.5f * s / half, 0f), 1f);
                    mask[i] = t * t * (3f - 2f * t);
                }
            }
            return mask;
        }

        /// <summary>
        /// マスクの境目をなめらかにする。各頂点の値を、自分と隣の頂点（三角形の辺でつながる頂点）の平均に近づける操作を iterations 回行う。
        /// 同じ位置にある頂点（UV の継ぎ目で分かれた頂点）は同じ値にそろえてから行うので、継ぎ目で段差が出ない。
        /// </summary>
        public static float[] Smooth(float[] mask, Vector3[] positions, int[] triangles, int iterations)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (mask.Length != positions.Length) throw new ArgumentException($"マスクの数（{mask.Length}）が頂点数（{positions.Length}）と違います。", nameof(mask));
            if (iterations <= 0) return (float[])mask.Clone();

            int[] vertexToWelded = VertexWelder.Weld(positions, MeshTopology.DefaultWeldTolerance, out Vector3[] weldedPositions);
            int weldedCount = weldedPositions.Length;
            int[][] neighbors = BuildNeighbors(vertexToWelded, weldedCount, triangles);

            var sum = new float[weldedCount];
            var count = new int[weldedCount];
            for (int v = 0; v < mask.Length; v++)
            {
                sum[vertexToWelded[v]] += mask[v];
                count[vertexToWelded[v]]++;
            }
            var values = new float[weldedCount];
            for (int w = 0; w < weldedCount; w++) values[w] = count[w] > 0 ? sum[w] / count[w] : 0f;

            var next = new float[weldedCount];
            for (int it = 0; it < iterations; it++)
            {
                for (int w = 0; w < weldedCount; w++)
                {
                    int[] around = neighbors[w];
                    if (around.Length == 0)
                    {
                        next[w] = values[w];
                        continue;
                    }
                    float total = 0f;
                    for (int k = 0; k < around.Length; k++) total += values[around[k]];
                    next[w] = 0.5f * values[w] + 0.5f * (total / around.Length);
                }
                float[] swap = values;
                values = next;
                next = swap;
            }

            var result = new float[mask.Length];
            for (int v = 0; v < mask.Length; v++) result[v] = values[vertexToWelded[v]];
            return result;
        }

        /// <summary>
        /// 頂点どうしのつながり（三角形の辺。同じ位置の頂点は 1 つにまとめた番号）を、まとめた頂点ごとの配列で返す。
        /// </summary>
        public static int[][] BuildNeighbors(int[] vertexToWelded, int weldedCount, int[] triangles)
        {
            var sets = new HashSet<int>[weldedCount];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = vertexToWelded[triangles[t]], b = vertexToWelded[triangles[t + 1]], c = vertexToWelded[triangles[t + 2]];
                Link(sets, a, b);
                Link(sets, b, c);
                Link(sets, c, a);
            }
            var result = new int[weldedCount][];
            for (int w = 0; w < weldedCount; w++)
            {
                if (sets[w] == null)
                {
                    result[w] = new int[0];
                    continue;
                }
                var list = new int[sets[w].Count];
                sets[w].CopyTo(list);
                Array.Sort(list);
                result[w] = list;
            }
            return result;
        }

        static void Link(HashSet<int>[] sets, int a, int b)
        {
            if (a == b) return;
            (sets[a] ?? (sets[a] = new HashSet<int>())).Add(b);
            (sets[b] ?? (sets[b] = new HashSet<int>())).Add(a);
        }

        static void Check(Vector3[] deltas, float[] mask)
        {
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (deltas.Length != mask.Length) throw new ArgumentException($"マスクの数（{mask.Length}）が頂点数（{deltas.Length}）と違います。", nameof(mask));
        }
    }
}
