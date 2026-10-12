using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>メッシュの BlendShape 1つ分（名前とフレーム）。書き出すとメッシュに入るものと、今のメッシュに入っているものを比べるのに使う。</summary>
    public sealed class NamedShape
    {
        /// <summary>BlendShape の名前。</summary>
        public readonly string Name;

        /// <summary>フレーム（重みと頂点ごとの差分）。並びの順は問わない（比べるときに重みの順に並べる）。</summary>
        public readonly IReadOnlyList<ShapeFrame> Frames;

        public NamedShape(string name, IReadOnlyList<ShapeFrame> frames)
        {
            Name = name ?? "";
            Frames = frames ?? Array.Empty<ShapeFrame>();
        }
    }

    /// <summary>データとメッシュの BlendShape の違いの種類（メッシュに反映すると起きること）。</summary>
    public enum ShapeKeyChangeKind
    {
        /// <summary>メッシュに無い（反映すると加わる）。</summary>
        Add,

        /// <summary>メッシュにあるが、フレームの重みか差分が違う（反映すると置き換わる）。</summary>
        Update,

        /// <summary>メッシュにあるが、データにも元のメッシュにも無い（反映すると無くなる）。</summary>
        Remove,
    }

    /// <summary>違い1つ（BlendShape の名前と種類）。</summary>
    public readonly struct ShapeKeyChange
    {
        public readonly string Name;
        public readonly ShapeKeyChangeKind Kind;

        public ShapeKeyChange(string name, ShapeKeyChangeKind kind)
        {
            Name = name;
            Kind = kind;
        }

        public override string ToString() => $"{Kind} {Name}";
    }

    /// <summary>
    /// シェイプキーのデータと、レンダラーに今付いているメッシュの BlendShape が同じか（メッシュに反映済みか）を比べる。
    /// 比べるのは、名前と、各フレームの重みと頂点ごとの差分（許容誤差つき）。Unity に依存しない部分だけを受け持ち、
    /// メッシュから読むのは呼び出し側（エディターのシェイプキーの一覧）。データかメッシュが変わったときだけ呼ぶ（毎フレームは回らない）。
    /// </summary>
    public static class ShapeKeySync
    {
        /// <summary>差分の比べ方の、許容誤差の既定値（メッシュの座標の単位。メートルなら 0.01mm）。</summary>
        public const float DefaultTolerance = 1e-5f;

        /// <summary>フレームの重み（0〜100）を同じとみなす差。</summary>
        public const float WeightTolerance = 1e-3f;

        /// <summary>
        /// 頂点ごとの差分が同じか（数が同じで、各成分の差が tolerance 以下）。片方が空（長さ 0）なら、もう片方がすべて tolerance 以下のときに同じとみなす
        /// （差分の無いフレームを、空の配列で持つことがあるため）。
        /// </summary>
        public static bool SameDeltas(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b, float tolerance)
        {
            if (a.Length == 0) return IsZero(b, tolerance);
            if (b.Length == 0) return IsZero(a, tolerance);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                Vector3 p = a[i], q = b[i];
                if (Math.Abs(p.X - q.X) > tolerance || Math.Abs(p.Y - q.Y) > tolerance || Math.Abs(p.Z - q.Z) > tolerance) return false;
            }
            return true;
        }

        static bool IsZero(ReadOnlySpan<Vector3> a, float tolerance)
        {
            for (int i = 0; i < a.Length; i++)
            {
                Vector3 p = a[i];
                if (Math.Abs(p.X) > tolerance || Math.Abs(p.Y) > tolerance || Math.Abs(p.Z) > tolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// 2つのフレームの並びが同じか。フレームの数が同じで、重みの順に並べたとき、重み（<see cref="WeightTolerance"/> 以内）と差分（tolerance 以内）が同じ。
        /// </summary>
        public static bool SameFrames(IReadOnlyList<ShapeFrame> a, IReadOnlyList<ShapeFrame> b, float tolerance)
        {
            if (a == null || b == null) return (a == null || a.Count == 0) && (b == null || b.Count == 0);
            if (a.Count != b.Count) return false;
            int[] orderA = OrderByWeight(a), orderB = OrderByWeight(b);
            for (int k = 0; k < orderA.Length; k++)
            {
                ShapeFrame fa = a[orderA[k]], fb = b[orderB[k]];
                if (fa == null || fb == null) return fa == fb;
                if (Math.Abs(fa.Weight - fb.Weight) > WeightTolerance) return false;
                if (!SameDeltas(fa.Deltas, fb.Deltas, tolerance)) return false;
            }
            return true;
        }

        // フレームの番号を、重みの小さい順に並べたもの（同じ重みは元の順）
        static int[] OrderByWeight(IReadOnlyList<ShapeFrame> frames)
        {
            var order = new int[frames.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = 1; i < order.Length; i++)
            {
                int current = order[i];
                float weight = frames[current] != null ? frames[current].Weight : 0f;
                int j = i - 1;
                while (j >= 0 && (frames[order[j]] != null ? frames[order[j]].Weight : 0f) > weight)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = current;
            }
            return order;
        }

        /// <summary>
        /// 書き出すとメッシュに入る BlendShape（expected。同じ名前が重なるときは後ろのもの）と、今のメッシュの BlendShape の名前（actualNames）を比べて、
        /// 違いの一覧を返す。並びは expected の順（そのあと keep の順、今のメッシュの順）。
        /// <list type="bullet">
        /// <item>expected の名前が今のメッシュに無い → Add。ある → readActual でそのフレームを読んで比べ、違えば Update</item>
        /// <item>keep（元のメッシュにあり、書き出しでもそのまま残る BlendShape の名前。中身は比べない）が今のメッシュに無い → Add</item>
        /// <item>今のメッシュの名前が expected にも keep にも無い → Remove（データから消した・無効にしたシェイプキーなど）</item>
        /// </list>
        /// </summary>
        /// <param name="readActual">今のメッシュの、その名前の BlendShape のフレームを返す（両方にある名前だけ呼ぶ）</param>
        /// <param name="tolerance">差分を同じとみなす差（メッシュの座標の単位）</param>
        public static List<ShapeKeyChange> Compare(IReadOnlyList<NamedShape> expected, IReadOnlyList<string> actualNames,
            Func<string, IReadOnlyList<ShapeFrame>> readActual, IEnumerable<string> keep, float tolerance)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (actualNames == null) throw new ArgumentNullException(nameof(actualNames));
            if (readActual == null) throw new ArgumentNullException(nameof(readActual));
            var changes = new List<ShapeKeyChange>();
            var actual = new HashSet<string>(actualNames);

            // 同じ名前は後ろのものが勝つ（書き出しで、前のものは置き換えられる）
            var last = new Dictionary<string, int>();
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i] != null) last[expected[i].Name] = i;
            }
            var produced = new HashSet<string>();
            for (int i = 0; i < expected.Count; i++)
            {
                NamedShape shape = expected[i];
                if (shape == null || last[shape.Name] != i) continue;
                produced.Add(shape.Name);
                if (!actual.Contains(shape.Name))
                {
                    changes.Add(new ShapeKeyChange(shape.Name, ShapeKeyChangeKind.Add));
                    continue;
                }
                if (!SameFrames(shape.Frames, readActual(shape.Name), tolerance)) changes.Add(new ShapeKeyChange(shape.Name, ShapeKeyChangeKind.Update));
            }

            var kept = new HashSet<string>();
            if (keep != null)
            {
                foreach (string name in keep)
                {
                    if (name == null || produced.Contains(name) || !kept.Add(name)) continue;
                    if (!actual.Contains(name)) changes.Add(new ShapeKeyChange(name, ShapeKeyChangeKind.Add));
                }
            }

            var seen = new HashSet<string>();
            foreach (string name in actualNames)
            {
                if (name == null || !seen.Add(name)) continue;
                if (!produced.Contains(name) && !kept.Contains(name)) changes.Add(new ShapeKeyChange(name, ShapeKeyChangeKind.Remove));
            }
            return changes;
        }

        /// <summary>違いの数を、種類ごとに数える。</summary>
        public static void Count(IReadOnlyList<ShapeKeyChange> changes, out int add, out int update, out int remove)
        {
            add = update = remove = 0;
            if (changes == null) return;
            for (int i = 0; i < changes.Count; i++)
            {
                switch (changes[i].Kind)
                {
                    case ShapeKeyChangeKind.Add: add++; break;
                    case ShapeKeyChangeKind.Update: update++; break;
                    default: remove++; break;
                }
            }
        }
    }
}
