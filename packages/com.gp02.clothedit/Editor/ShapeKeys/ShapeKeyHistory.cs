using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// エントリの前の版の記録（<see cref="ShapeKeyEntry.History"/>）。上書き保存のたびに、上書きする前の形を最大
    /// <see cref="ShapeKeyEntry.MaxHistory"/> 個まで、日時つきでエントリの中に残す。画面に依存しない。
    /// ここは記録を作るだけで、Undo と自動バックアップは呼び出し側（<see cref="ShapeKeyListOperations"/> など）が受け持つ。
    /// </summary>
    public static class ShapeKeyHistory
    {
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>今の日時の文字列。</summary>
        public static string Now() => DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);

        /// <summary>フレームの深いコピー（差分の配列も複製する）。</summary>
        public static List<ShapeKeyFrame> CloneFrames(IReadOnlyList<ShapeKeyFrame> frames)
        {
            var result = new List<ShapeKeyFrame>(frames.Count);
            foreach (ShapeKeyFrame f in frames)
            {
                result.Add(new ShapeKeyFrame
                {
                    Weight = f.Weight,
                    DeltaVertices = (Vector3[])f.DeltaVertices.Clone(),
                    DeltaNormals = f.DeltaNormals != null ? (Vector3[])f.DeltaNormals.Clone() : new Vector3[0],
                    DeltaTangents = f.DeltaTangents != null ? (Vector3[])f.DeltaTangents.Clone() : new Vector3[0],
                });
            }
            return result;
        }

        public static ShapeKeyVersion CloneVersion(ShapeKeyVersion version)
        {
            return new ShapeKeyVersion
            {
                SavedAt = version.SavedAt,
                Reason = version.Reason,
                Frames = CloneFrames(version.Frames),
                PinnedVertices = (int[])(version.PinnedVertices ?? new int[0]).Clone(),
            };
        }

        /// <summary>エントリの今の形を、前の版として記録する（古いものが上限を超えたら捨てる）。記録した版を返す。</summary>
        public static ShapeKeyVersion Push(ShapeKeyEntry entry, string reason)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var version = new ShapeKeyVersion
            {
                SavedAt = Now(),
                Reason = reason ?? "",
                Frames = CloneFrames(entry.Frames),
                PinnedVertices = (int[])(entry.PinnedVertices ?? new int[0]).Clone(),
            };
            entry.History.Add(version);
            while (entry.History.Count > ShapeKeyEntry.MaxHistory) entry.History.RemoveAt(0);
            return version;
        }

        /// <summary>
        /// エントリを新しい形で上書きする。上書きする前の形は前の版に残す。
        /// 今の形と新しい形が同じなら、何もしない（同じ版が履歴に並ばないように）。変えたら true。
        /// 呼び出し側が先に Undo の記録を取ること。
        /// </summary>
        public static bool Overwrite(ShapeKeyEntry entry, List<ShapeKeyFrame> frames, int[] pinnedVertices, string reason)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            int[] pins = pinnedVertices ?? new int[0];
            if (SameFrames(entry.Frames, frames) && SameInts(entry.PinnedVertices, pins)) return false;
            if (entry.Frames.Count > 0) Push(entry, reason);
            entry.Frames = frames;
            entry.PinnedVertices = pins;
            return true;
        }

        /// <summary>
        /// エントリを、前の版（<see cref="ShapeKeyEntry.History"/> の index 番目）の形に戻す。戻す前の形も新しい版として残すので、戻した操作も取り消せる。
        /// 呼び出し側が先に Undo の記録を取ること。
        /// </summary>
        public static void Restore(ShapeKeyEntry entry, int index)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (index < 0 || index >= entry.History.Count) throw new ArgumentOutOfRangeException(nameof(index));
            ShapeKeyVersion version = entry.History[index];
            List<ShapeKeyFrame> frames = CloneFrames(version.Frames);
            int[] pins = (int[])(version.PinnedVertices ?? new int[0]).Clone();
            Push(entry, L10n.Tr("以前の版に戻す前", "Before restoring an older version"));
            entry.Frames = frames;
            entry.PinnedVertices = pins;
        }

        /// <summary>版の1行の説明（「2026-10-11 15:30:12  布で編集して上書き  (1 フレーム)」）。</summary>
        public static string Describe(ShapeKeyVersion version)
        {
            string frames = version.Frames.Count > 1
                ? L10n.Tr($"{version.Frames.Count} フレーム", $"{version.Frames.Count} frames")
                : L10n.Tr("1 フレーム", "1 frame");
            return $"{version.SavedAt}  {version.Reason}  ({frames})";
        }

        static bool SameFrames(IReadOnlyList<ShapeKeyFrame> a, IReadOnlyList<ShapeKeyFrame> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Weight != b[i].Weight) return false;
                if (!SameVectors(a[i].DeltaVertices, b[i].DeltaVertices)) return false;
                if (!SameVectors(a[i].DeltaNormals, b[i].DeltaNormals)) return false;
                if (!SameVectors(a[i].DeltaTangents, b[i].DeltaTangents)) return false;
            }
            return true;
        }

        static bool SameVectors(Vector3[] a, Vector3[] b)
        {
            int la = a?.Length ?? 0, lb = b?.Length ?? 0;
            if (la != lb) return false;
            for (int i = 0; i < la; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        static bool SameInts(int[] a, int[] b)
        {
            int la = a?.Length ?? 0, lb = b?.Length ?? 0;
            if (la != lb) return false;
            for (int i = 0; i < la; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
