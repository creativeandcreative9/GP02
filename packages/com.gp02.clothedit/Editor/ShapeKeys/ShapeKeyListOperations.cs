using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの一覧のクリップボード（エディタを開いている間だけ有効）。別のデータにも貼れるが、頂点数が同じものに限る。
    /// </summary>
    public sealed class ShapeKeyClipboard
    {
        readonly List<ShapeKeyEntry> _entries = new List<ShapeKeyEntry>();

        public int VertexCount { get; private set; }
        public int Count => _entries.Count;

        /// <summary>貼り付けられる頂点数と、中身の数が合っているか。</summary>
        public bool CanPasteInto(GP02ShapeKeyData data) => data != null && _entries.Count > 0 && data.VertexCount == VertexCount;

        public void Set(IEnumerable<ShapeKeyEntry> entries, int vertexCount)
        {
            _entries.Clear();
            foreach (ShapeKeyEntry entry in entries) _entries.Add(ShapeKeyLibrary.CloneEntry(entry));
            VertexCount = vertexCount;
        }

        public void Clear()
        {
            _entries.Clear();
            VertexCount = 0;
        }

        /// <summary>貼り付け用の複製を作る（クリップボードの中身は変わらない）。</summary>
        public List<ShapeKeyEntry> CreateCopies()
        {
            var result = new List<ShapeKeyEntry>(_entries.Count);
            foreach (ShapeKeyEntry entry in _entries) result.Add(ShapeKeyLibrary.CloneEntry(entry));
            return result;
        }
    }

    /// <summary>
    /// シェイプキーの一覧に対する操作（画面に依存しない）。
    /// データ（<see cref="GP02ShapeKeyData"/>）を書き換える操作は、すべて Unity の Undo で戻せる。
    /// 入力が正しくないとき（頂点数が合わない、名前が空など）は ArgumentException などを投げるので、画面側は利用者に見せる文章にして受ける。
    /// </summary>
    public static partial class ShapeKeyListOperations
    {
        public static readonly ShapeKeyClipboard Clipboard = new ShapeKeyClipboard();

        // ------------------------------------------------------------------
        // 共通
        // ------------------------------------------------------------------

        static void Record(GP02ShapeKeyData data, string japanese, string english)
        {
            Undo.RecordObject(data, "GP02 " + L10n.Tr(japanese, english));
        }

        // バックアップを取ってから Undo の記録を取る。エントリを消す・書き換える操作で使う（Undo とは別の安全網）
        static void RecordDestructive(GP02ShapeKeyData data, string japanese, string english)
        {
            ShapeKeyBackup.Create(data, english);
            Record(data, japanese, english);
        }

        static void Touch(GP02ShapeKeyData data) => EditorUtility.SetDirty(data);

        static void Require(GP02ShapeKeyData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
        }

        // データの中にあるものだけを、データの並びの順で返す
        static List<ShapeKeyEntry> InDataOrder(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries)
        {
            var set = new HashSet<ShapeKeyEntry>();
            if (entries != null)
            {
                foreach (ShapeKeyEntry e in entries)
                {
                    if (e != null) set.Add(e);
                }
            }
            var result = new List<ShapeKeyEntry>(set.Count);
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null && set.Contains(e)) result.Add(e);
            }
            return result;
        }

        static void RequireCompatible(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            if (!ShapeKeyLibrary.IsEntryCompatible(entry, data.VertexCount))
            {
                throw new ArgumentException(L10n.Tr(
                    $"シェイプキー「{entry.Name}」の頂点数が、データの頂点数（{data.VertexCount}）と合いません。",
                    $"The vertex count of shape key \"{entry.Name}\" does not match the data ({data.VertexCount})."));
            }
        }

        static Mesh RequireSourceMesh(GP02ShapeKeyData data)
        {
            if (data.SourceMesh == null)
            {
                throw new InvalidOperationException(L10n.Tr(
                    "データの元のメッシュが見つかりません。左右の位置を調べるのに元のメッシュが要ります。",
                    "The source mesh of the data is missing. It is needed to find the left and right positions."));
            }
            if (data.SourceMesh.vertexCount != data.VertexCount)
            {
                throw new InvalidOperationException(L10n.Tr(
                    $"データの元のメッシュ（頂点 {data.SourceMesh.vertexCount} 個）が、データの頂点数（{data.VertexCount}）と違います。",
                    $"The source mesh ({data.SourceMesh.vertexCount} vertices) does not match the data ({data.VertexCount} vertices)."));
            }
            return data.SourceMesh;
        }

        /// <summary>データの中で重ならない名前。同じ名前があれば「名前 (1)」のように番号を付ける。</summary>
        public static string UniqueName(GP02ShapeKeyData data, string desired, ICollection<string> alsoUsed = null)
        {
            Require(data);
            if (string.IsNullOrWhiteSpace(desired)) desired = "ShapeKey";
            desired = desired.Trim();
            string name = desired;
            for (int n = 1; IsNameUsed(data, name, alsoUsed); n++) name = $"{desired} ({n})";
            return name;
        }

        static bool IsNameUsed(GP02ShapeKeyData data, string name, ICollection<string> alsoUsed)
        {
            if (alsoUsed != null && alsoUsed.Contains(name)) return true;
            return data.FindEntry(name) != null;
        }

        static int IndexOfLast(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> sources)
        {
            int last = -1;
            foreach (ShapeKeyEntry e in sources) last = Math.Max(last, data.Entries.IndexOf(e));
            return last;
        }

        // ------------------------------------------------------------------
        // 追加・複製・コピー・削除
        // ------------------------------------------------------------------

        /// <summary>エントリを末尾に加える（グループ名が一覧に無ければ加える）。名前が重なるときは番号を付ける。</summary>
        public static ShapeKeyEntry AddEntry(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            Require(data);
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            RequireCompatible(data, entry);
            entry.Name = UniqueName(data, entry.Name);
            ShapeKeyLibrary.AddEntry(data, entry);
            return entry;
        }

        /// <summary>選んだエントリを複製する。複製は元のすぐ後ろに入り、名前は「名前_copy」になる。</summary>
        public static List<ShapeKeyEntry> Duplicate(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            var result = new List<ShapeKeyEntry>();
            if (targets.Count == 0) return result;

            Record(data, "複製", "Duplicate");
            var usedNow = new HashSet<string>();
            var copies = new Dictionary<ShapeKeyEntry, ShapeKeyEntry>();
            foreach (ShapeKeyEntry source in targets)
            {
                string name = UniqueName(data, source.Name + "_copy", usedNow);
                usedNow.Add(name);
                ShapeKeyEntry copy = ShapeKeyLibrary.CloneEntry(source, name);
                ShapeKeyLibrary.StampCreated(copy);
                copies[source] = copy;
            }
            // 後ろから入れると、前のものの番号がずれない
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                data.Entries.Insert(data.Entries.IndexOf(targets[i]) + 1, copies[targets[i]]);
            }
            foreach (ShapeKeyEntry source in targets) result.Add(copies[source]);
            Touch(data);
            return result;
        }

        /// <summary>選んだエントリをクリップボードに入れる。入れた数を返す。</summary>
        public static int Copy(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return 0;
            Clipboard.Set(targets, data.VertexCount);
            return targets.Count;
        }

        /// <summary>貼り付けられない理由。貼り付けられるなら null。</summary>
        public static string GetPasteProblem(GP02ShapeKeyData data)
        {
            if (data == null) return L10n.Tr("データがありません。", "There is no data.");
            if (Clipboard.Count == 0) return L10n.Tr("コピーしたシェイプキーがありません。", "Nothing has been copied.");
            if (Clipboard.VertexCount != data.VertexCount)
            {
                return L10n.Tr(
                    $"頂点数が違うので貼り付けられません（コピーしたもの: {Clipboard.VertexCount} 個、このデータ: {data.VertexCount} 個）。",
                    $"The vertex counts differ, so it cannot be pasted (copied: {Clipboard.VertexCount}, this data: {data.VertexCount}).");
            }
            return null;
        }

        /// <summary>
        /// クリップボードのエントリを末尾に貼り付ける。頂点数が違えば何もせず false を返し、理由を error に入れる。
        /// 同じ名前があれば番号を付ける。
        /// </summary>
        public static bool TryPaste(GP02ShapeKeyData data, out List<ShapeKeyEntry> pasted, out string error)
        {
            Require(data);
            pasted = new List<ShapeKeyEntry>();
            error = GetPasteProblem(data);
            if (error != null) return false;

            Record(data, "貼り付け", "Paste");
            var usedNow = new HashSet<string>();
            foreach (ShapeKeyEntry copy in Clipboard.CreateCopies())
            {
                copy.Name = UniqueName(data, copy.Name, usedNow);
                usedNow.Add(copy.Name);
                ShapeKeyLibrary.StampCreated(copy);
                data.Entries.Add(copy);
                if (!string.IsNullOrEmpty(copy.Group) && !data.Groups.Contains(copy.Group)) data.Groups.Add(copy.Group);
                pasted.Add(copy);
            }
            Touch(data);
            return true;
        }

        /// <summary>選んだエントリを消す。消した数を返す。</summary>
        public static int Delete(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return 0;
            RecordDestructive(data, "削除", "Delete");
            var set = new HashSet<ShapeKeyEntry>(targets);
            data.Entries.RemoveAll(e => set.Contains(e));
            Touch(data);
            return targets.Count;
        }

        // ------------------------------------------------------------------
        // 名前・有効・グループ・並べ替え
        // ------------------------------------------------------------------

        /// <summary>名前を変える。空の名前は受け付けず false。</summary>
        public static bool Rename(GP02ShapeKeyData data, ShapeKeyEntry entry, string newName)
        {
            Require(data);
            if (entry == null || string.IsNullOrWhiteSpace(newName)) return false;
            newName = newName.Trim();
            if (entry.Name == newName) return true;
            Record(data, "名前の変更", "Rename");
            entry.Name = newName;
            Touch(data);
            return true;
        }

        public static void SetEnabled(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, bool enabled)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return;
            Record(data, "有効の切り替え", "Toggle enabled");
            foreach (ShapeKeyEntry e in targets) e.Enabled = enabled;
            Touch(data);
        }

        /// <summary>グループを作る。同じ名前があればそれを返す。名前が空なら null。</summary>
        public static string AddGroup(GP02ShapeKeyData data, string groupName)
        {
            Require(data);
            if (string.IsNullOrWhiteSpace(groupName)) return null;
            groupName = groupName.Trim();
            if (!data.Groups.Contains(groupName))
            {
                Record(data, "グループの追加", "Add group");
                data.Groups.Add(groupName);
                Touch(data);
            }
            return groupName;
        }

        /// <summary>まだ使われていないグループ名（「グループ 1」「グループ 2」…）。</summary>
        public static string SuggestGroupName(GP02ShapeKeyData data)
        {
            Require(data);
            for (int n = 1; ; n++)
            {
                string name = L10n.Tr("グループ ", "Group ") + n;
                if (!data.Groups.Contains(name)) return name;
            }
        }

        /// <summary>選んだエントリをグループに入れる。groupName が空ならグループから外す。無いグループは作る。</summary>
        public static void SetGroup(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, string groupName)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            groupName = string.IsNullOrWhiteSpace(groupName) ? "" : groupName.Trim();
            if (targets.Count == 0) return;
            Record(data, "グループの設定", "Set group");
            if (groupName.Length > 0 && !data.Groups.Contains(groupName)) data.Groups.Add(groupName);
            foreach (ShapeKeyEntry e in targets) e.Group = groupName;
            Touch(data);
        }

        /// <summary>グループ名を変える。新しい名前が空、または別のグループと同じなら何もせず false。</summary>
        public static bool RenameGroup(GP02ShapeKeyData data, string oldName, string newName)
        {
            Require(data);
            if (string.IsNullOrWhiteSpace(newName)) return false;
            newName = newName.Trim();
            if (oldName == newName) return true;
            if (data.Groups.Contains(newName)) return false;
            Record(data, "グループ名の変更", "Rename group");
            int index = data.Groups.IndexOf(oldName);
            if (index >= 0) data.Groups[index] = newName;
            else data.Groups.Add(newName);
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null && e.Group == oldName) e.Group = newName;
            }
            Touch(data);
            return true;
        }

        /// <summary>グループを消す。中のシェイプキーは消さず、グループなしに戻る。</summary>
        public static void RemoveGroup(GP02ShapeKeyData data, string groupName)
        {
            Require(data);
            Record(data, "グループの削除", "Remove group");
            data.Groups.Remove(groupName);
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null && e.Group == groupName) e.Group = "";
            }
            Touch(data);
        }

        /// <summary>
        /// 選んだエントリを、同じグループの中で1つ上（direction が負）か下（正）へ動かす。
        /// グループの外のエントリとは入れ替えない（一覧では別の場所に見えるため）。動いたら true。
        /// </summary>
        public static bool Move(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, int direction)
        {
            Require(data);
            if (direction == 0) return false;
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return false;
            var selected = new HashSet<ShapeKeyEntry>(targets);

            // 先に動かす側から処理すると、選んだものどうしがぶつからない
            if (direction > 0) targets.Reverse();
            bool moved = false;
            bool recorded = false;
            foreach (ShapeKeyEntry entry in targets)
            {
                int index = data.Entries.IndexOf(entry);
                int step = direction > 0 ? 1 : -1;
                int neighbor = -1;
                for (int i = index + step; i >= 0 && i < data.Entries.Count; i += step)
                {
                    ShapeKeyEntry other = data.Entries[i];
                    if (other != null && other.Group == entry.Group)
                    {
                        neighbor = i;
                        break;
                    }
                }
                if (neighbor < 0 || selected.Contains(data.Entries[neighbor])) continue;
                if (!recorded)
                {
                    Record(data, "並べ替え", "Reorder");
                    recorded = true;
                }
                data.Entries.RemoveAt(index);
                data.Entries.Insert(neighbor, entry);
                moved = true;
            }
            if (moved) Touch(data);
            return moved;
        }

        /// <summary>
        /// 選んだエントリを、insertIndex（今の並びでの位置。その前に入る）に動かし、グループも変える。ドラッグ＆ドロップ用。
        /// 選んだもの同士の順番は保つ。
        /// </summary>
        public static void MoveTo(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, int insertIndex, string groupName)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return;
            groupName = string.IsNullOrWhiteSpace(groupName) ? "" : groupName.Trim();
            insertIndex = Math.Max(0, Math.Min(insertIndex, data.Entries.Count));

            Record(data, "並べ替え", "Reorder");
            // 挿入位置より前にある、動かすもの の数だけ、位置が前にずれる
            int before = 0;
            for (int i = 0; i < insertIndex; i++)
            {
                if (targets.Contains(data.Entries[i])) before++;
            }
            var set = new HashSet<ShapeKeyEntry>(targets);
            data.Entries.RemoveAll(e => set.Contains(e));
            data.Entries.InsertRange(insertIndex - before, targets);
            if (groupName.Length > 0 && !data.Groups.Contains(groupName)) data.Groups.Add(groupName);
            foreach (ShapeKeyEntry e in targets) e.Group = groupName;
            Touch(data);
        }

        // ------------------------------------------------------------------
        // 合成・分割・反転・対称・倍率
        // ------------------------------------------------------------------

        /// <summary>
        /// 選んだエントリを、重みをかけて足し合わせた1つのエントリにする（重み 100 の1フレーム）。
        /// 重み weights は 0〜100 で、null ならエントリのプレビューの重み（すべて 0 なら 100）。in-between のエントリも、その重みでの形で足す。
        /// 新しいエントリは、選んだものの一番後ろに入る。
        /// </summary>
        /// <param name="disableSources">true なら、元のエントリを無効にする（書き出しで二重に動かないように）</param>
        public static ShapeKeyEntry Merge(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, IReadOnlyList<float> weights,
            string name, bool disableSources = true)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) throw new ArgumentException(L10n.Tr("合成するシェイプキーを選んでください。", "Select the shape keys to merge."), nameof(entries));
            if (weights != null && weights.Count != entries.Count) throw new ArgumentException("entries と weights の数が違います。", nameof(weights));
            foreach (ShapeKeyEntry e in targets) RequireCompatible(data, e);

            // weights は entries の並びなので、データの並びに直す
            float[] ordered = new float[targets.Count];
            bool anyWeight = false;
            for (int i = 0; i < targets.Count; i++)
            {
                int original = IndexInList(entries, targets[i]);
                ordered[i] = weights != null ? weights[original] : targets[i].PreviewWeight;
                if (ordered[i] != 0f) anyWeight = true;
            }
            if (!anyWeight)
            {
                for (int i = 0; i < ordered.Length; i++) ordered[i] = ShapeKeyMath.FullWeight;
            }

            int count = data.VertexCount;
            var frame = new ShapeKeyFrame { Weight = ShapeKeyMath.FullWeight, DeltaVertices = new Vector3[count] };
            Vector3[] normals = null, tangents = null;
            for (int i = 0; i < targets.Count; i++)
            {
                ShapeKeyFrame part = ShapeKeyLibrary.EvaluateEntry(targets[i], ordered[i], count);
                AddInPlace(frame.DeltaVertices, part.DeltaVertices);
                if (part.HasNormals) AddInPlace(normals ?? (normals = new Vector3[count]), part.DeltaNormals);
                if (part.HasTangents) AddInPlace(tangents ?? (tangents = new Vector3[count]), part.DeltaTangents);
            }
            frame.DeltaNormals = normals ?? new Vector3[0];
            frame.DeltaTangents = tangents ?? new Vector3[0];

            if (disableSources) RecordDestructive(data, "合成", "Merge");
            else Record(data, "合成", "Merge");
            var merged = new ShapeKeyEntry { Name = UniqueName(data, string.IsNullOrWhiteSpace(name) ? targets[0].Name + "_merge" : name), Group = targets[0].Group };
            ShapeKeyLibrary.StampCreated(merged);
            merged.Frames.Add(frame);
            data.Entries.Insert(IndexOfLast(data, targets) + 1, merged);
            if (disableSources)
            {
                foreach (ShapeKeyEntry e in targets) e.Enabled = false;
            }
            Touch(data);
            return merged;
        }

        static int IndexInList(IReadOnlyList<ShapeKeyEntry> list, ShapeKeyEntry entry)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], entry)) return i;
            }
            return -1;
        }

        static void AddInPlace(Vector3[] total, Vector3[] add)
        {
            for (int i = 0; i < total.Length; i++) total[i] += add[i];
        }

        /// <summary>
        /// エントリを左右の2つに分ける（元の名前に _L・_R を付ける）。2つを足すと元に戻る。新しい2つは元のすぐ後ろに入る。
        /// </summary>
        /// <param name="blendWidth">中央をなめらかに分ける幅（メートル）</param>
        /// <param name="leftIsPositive">true なら軸の値が大きい側を _L にする</param>
        /// <param name="disableOriginal">true なら、元のエントリを無効にする</param>
        public static void SplitLeftRight(GP02ShapeKeyData data, ShapeKeyEntry entry, int axis, float center, float blendWidth,
            bool leftIsPositive, bool disableOriginal, out ShapeKeyEntry left, out ShapeKeyEntry right)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("分割するシェイプキーがデータにありません。", "The shape key to split is not in the data."), nameof(entry));
            RequireCompatible(data, entry);
            Mesh mesh = RequireSourceMesh(data);

            ShapeKeyLibrary.SplitEntry(entry, mesh, axis, center, blendWidth, out left, out right, "_L", "_R", leftIsPositive);
            var used = new HashSet<string>();
            left.Name = UniqueName(data, left.Name, used);
            used.Add(left.Name);
            right.Name = UniqueName(data, right.Name, used);

            if (disableOriginal) RecordDestructive(data, "左右に分割", "Split left/right");
            else Record(data, "左右に分割", "Split left/right");
            ShapeKeyLibrary.StampCreated(left);
            ShapeKeyLibrary.StampCreated(right);
            int at = data.Entries.IndexOf(entry);
            data.Entries.Insert(at + 1, left);
            data.Entries.Insert(at + 2, right);
            if (disableOriginal) entry.Enabled = false;
            Touch(data);
        }

        /// <summary>
        /// エントリの左右を反転する。asCopy が true なら反転した新しいエントリを元の後ろに加え（名前は「名前_flip」）、false なら元のエントリを書き換える。
        /// </summary>
        public static ShapeKeyEntry Flip(GP02ShapeKeyData data, ShapeKeyEntry entry, int axis, float center, float tolerance, bool asCopy)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("反転するシェイプキーがデータにありません。", "The shape key to flip is not in the data."), nameof(entry));
            RequireCompatible(data, entry);
            Mesh mesh = RequireSourceMesh(data);

            ShapeKeyEntry flipped = ShapeKeyLibrary.FlipEntry(entry, mesh, axis, center, tolerance);
            // ピン留めした頂点も鏡に映す
            int[] map = VertexMirrorMap.Build(MeshBridge.ToNumerics(mesh.vertices, Vector3.one), axis, center, tolerance);
            flipped.PinnedVertices = MirrorVertices(entry.PinnedVertices, map);
            return ApplyResult(data, entry, flipped, asCopy, "_flip", "左右反転", "Flip");
        }

        /// <summary>
        /// エントリを左右対称にする。keepPositiveSide が true なら軸の値が大きい側を残し、反対側を鏡像で置き換える。
        /// asCopy が true なら新しいエントリを元の後ろに加え（名前は「名前_sym」）、false なら元のエントリを書き換える。
        /// </summary>
        public static ShapeKeyEntry Symmetrize(GP02ShapeKeyData data, ShapeKeyEntry entry, int axis, float center, float tolerance,
            bool keepPositiveSide, bool asCopy)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("対称にするシェイプキーがデータにありません。", "The shape key to symmetrize is not in the data."), nameof(entry));
            RequireCompatible(data, entry);
            Mesh mesh = RequireSourceMesh(data);

            ShapeKeyEntry result = ShapeKeyLibrary.SymmetrizeEntry(entry, mesh, axis, center, tolerance, keepPositiveSide);
            return ApplyResult(data, entry, result, asCopy, "_sym", "左右対称", "Symmetrize");
        }

        /// <summary>選んだエントリの差分を factor 倍する（元のエントリを書き換える）。</summary>
        public static void Scale(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, float factor)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return;
            foreach (ShapeKeyEntry e in targets) RequireCompatible(data, e);
            var results = new List<ShapeKeyEntry>();
            foreach (ShapeKeyEntry e in targets) results.Add(ShapeKeyLibrary.ScaleEntry(e, factor));

            RecordDestructive(data, "量の拡大縮小", "Scale");
            for (int i = 0; i < targets.Count; i++)
            {
                ShapeKeyHistory.Overwrite(targets[i], results[i].Frames, targets[i].PinnedVertices, L10n.Tr("量の拡大縮小", "Scale amount"));
            }
            Touch(data);
        }

        /// <summary>
        /// 選んだエントリの法線の差分を、位置の差分から計算し直す（元のエントリを書き換える。Undo の1段）。
        /// 既存の BlendShape から取り込んだエントリにも使える。計算し直したエントリの数を返す。
        /// </summary>
        /// <param name="smoothAngleDegrees">同じ位置の頂点の面をなめらかにつなぐ角度。180 で全部つなぐ</param>
        /// <param name="mergeCoincident">同じ位置の頂点（UV の継ぎ目など）を結合して計算するか</param>
        public static int RecomputeNormals(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, float smoothAngleDegrees, bool mergeCoincident)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return 0;
            foreach (ShapeKeyEntry e in targets) RequireCompatible(data, e);
            Mesh mesh = RequireSourceMesh(data);
            var results = new List<ShapeKeyEntry>();
            foreach (ShapeKeyEntry e in targets) results.Add(ShapeKeyLibrary.RecomputeNormalsEntry(e, mesh, smoothAngleDegrees, mergeCoincident));

            RecordDestructive(data, "法線を計算し直す", "Recompute normals");
            for (int i = 0; i < targets.Count; i++)
            {
                ShapeKeyHistory.Overwrite(targets[i], results[i].Frames, targets[i].PinnedVertices, L10n.Tr("法線を計算し直す", "Recompute normals"));
            }
            Touch(data);
            return targets.Count;
        }

        static ShapeKeyEntry ApplyResult(GP02ShapeKeyData data, ShapeKeyEntry original, ShapeKeyEntry result, bool asCopy, string suffix, string japanese, string english)
        {
            if (asCopy)
            {
                Record(data, japanese, english);
                result.Name = UniqueName(data, original.Name + suffix);
                ShapeKeyLibrary.StampCreated(result);
                data.Entries.Insert(data.Entries.IndexOf(original) + 1, result);
                Touch(data);
                return result;
            }
            RecordDestructive(data, japanese, english);
            ShapeKeyHistory.Overwrite(original, result.Frames, result.PinnedVertices, L10n.Tr(japanese, english));
            Touch(data);
            return original;
        }

        // 頂点 v が mirrorMap で鏡に映した先 m が元のピンに入っていれば、v もピンに入れる
        static int[] MirrorVertices(int[] vertices, int[] mirrorMap)
        {
            if (vertices == null || vertices.Length == 0) return new int[0];
            var set = new HashSet<int>(vertices);
            var result = new List<int>();
            for (int v = 0; v < mirrorMap.Length; v++)
            {
                if (mirrorMap[v] >= 0 && set.Contains(mirrorMap[v])) result.Add(v);
            }
            return result.ToArray();
        }

        // ------------------------------------------------------------------
        // in-between
        // ------------------------------------------------------------------

        /// <summary>
        /// 選んだエントリを順に並べて、1つの in-between 付きシェイプキーにする。entries の順がそのまま段の順になる。
        /// 各エントリは「重み 100 での形」を1つの部品として使う。
        /// - Progressive: 部品を 0→100% の順に動かす。段 k = 部品 0〜k の合計
        /// - Crossfade: 部品を 0→100%→0 の順に動かす。段 k = 部品 k だけ
        /// </summary>
        /// <param name="weights">各部品が 100% になる重み（小さい順で、最後は 100）。null なら等間隔（例: 3つなら 33.3・66.7・100）</param>
        public static ShapeKeyEntry MakeInBetween(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, string name,
            ShapeKeyInBetweenMode mode, IReadOnlyList<float> weights = null, bool disableSources = true)
        {
            Require(data);
            if (entries == null || entries.Count == 0) throw new ArgumentException(L10n.Tr("段にするシェイプキーを選んでください。", "Select the shape keys to use as steps."), nameof(entries));
            var targets = new List<ShapeKeyEntry>();
            foreach (ShapeKeyEntry e in entries)
            {
                if (e == null || !data.Entries.Contains(e)) throw new ArgumentException(L10n.Tr("選んだシェイプキーがデータにありません。", "A selected shape key is not in the data."), nameof(entries));
                RequireCompatible(data, e);
                targets.Add(e);
            }
            int steps = targets.Count;
            var stepWeights = new List<float>(steps);
            if (weights != null)
            {
                if (weights.Count != steps) throw new ArgumentException("entries と weights の数が違います。", nameof(weights));
                stepWeights.AddRange(weights);
            }
            else
            {
                for (int k = 1; k <= steps; k++) stepWeights.Add(k == steps ? ShapeKeyMath.FullWeight : ShapeKeyMath.FullWeight * k / steps);
            }

            int count = data.VertexCount;
            var shapes = new List<ShapeKeyFrame>(steps);
            foreach (ShapeKeyEntry e in targets) shapes.Add(ShapeKeyLibrary.EvaluateEntry(e, ShapeKeyMath.FullWeight, count));

            InBetweenMode coreMode = mode == ShapeKeyInBetweenMode.Progressive ? InBetweenMode.Progressive : InBetweenMode.Crossfade;
            ShapeFrame[] vertexFrames = MakeSequential(coreMode, stepWeights, shapes, f => f.DeltaVertices, count);
            ShapeFrame[] normalFrames = AnyHas(shapes, f => f.HasNormals) ? MakeSequential(coreMode, stepWeights, shapes, f => f.DeltaNormals, count) : null;
            ShapeFrame[] tangentFrames = AnyHas(shapes, f => f.HasTangents) ? MakeSequential(coreMode, stepWeights, shapes, f => f.DeltaTangents, count) : null;

            var result = new ShapeKeyEntry
            {
                Name = UniqueName(data, string.IsNullOrWhiteSpace(name) ? targets[0].Name + "_inbetween" : name),
                Group = targets[0].Group,
                InBetweenMode = mode,
            };
            for (int k = 0; k < vertexFrames.Length; k++)
            {
                result.Frames.Add(new ShapeKeyFrame
                {
                    Weight = vertexFrames[k].Weight,
                    DeltaVertices = MeshBridge.ToUnity(vertexFrames[k].Deltas, Vector3.one),
                    DeltaNormals = normalFrames != null ? MeshBridge.ToUnity(normalFrames[k].Deltas, Vector3.one) : new Vector3[0],
                    DeltaTangents = tangentFrames != null ? MeshBridge.ToUnity(tangentFrames[k].Deltas, Vector3.one) : new Vector3[0],
                });
            }

            if (disableSources) RecordDestructive(data, "in-between の作成", "Create in-between");
            else Record(data, "in-between の作成", "Create in-between");
            ShapeKeyLibrary.StampCreated(result);
            data.Entries.Insert(IndexOfLast(data, targets) + 1, result);
            if (disableSources)
            {
                foreach (ShapeKeyEntry e in targets) e.Enabled = false;
            }
            Touch(data);
            return result;
        }

        static bool AnyHas(List<ShapeKeyFrame> shapes, Func<ShapeKeyFrame, bool> has)
        {
            foreach (ShapeKeyFrame f in shapes)
            {
                if (has(f)) return true;
            }
            return false;
        }

        static ShapeFrame[] MakeSequential(InBetweenMode mode, IReadOnlyList<float> weights, List<ShapeKeyFrame> shapes,
            Func<ShapeKeyFrame, Vector3[]> channel, int count)
        {
            var units = new List<NVector3[]>(shapes.Count);
            foreach (ShapeKeyFrame shape in shapes)
            {
                Vector3[] array = channel(shape);
                units.Add(array != null && array.Length == count ? MeshBridge.ToNumerics(array, Vector3.one) : new NVector3[count]);
            }
            return ShapeKeyMath.MakeSequentialFrames(mode, weights, units);
        }

        // ------------------------------------------------------------------
        // 取り込み
        // ------------------------------------------------------------------

        static void RequireImportable(GP02ShapeKeyData data, Mesh mesh)
        {
            Require(data);
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (mesh.vertexCount != data.VertexCount)
            {
                throw new ArgumentException(L10n.Tr(
                    $"頂点数が違うので取り込めません（データ: {data.VertexCount} 個、メッシュ「{mesh.name}」: {mesh.vertexCount} 個）。",
                    $"The vertex counts differ, so it cannot be imported (data: {data.VertexCount}, mesh \"{mesh.name}\": {mesh.vertexCount})."));
            }
        }

        /// <summary>メッシュの BlendShape を、番号で選んでデータに取り込む（全フレームごと）。取り込んだエントリを返す。</summary>
        public static List<ShapeKeyEntry> ImportBlendShapes(GP02ShapeKeyData data, Mesh mesh, IReadOnlyList<int> shapeIndices)
        {
            return ImportBlendShapes(data, mesh, shapeIndices, false);
        }

        /// <summary>
        /// メッシュの BlendShape を、番号で選んでデータに取り込む（全フレームごと）。
        /// overwriteSameName が true なら、同じ名前のエントリがあれば新しいエントリを作らず、そのエントリを上書きする（前の形は履歴に残す）。
        /// </summary>
        public static List<ShapeKeyEntry> ImportBlendShapes(GP02ShapeKeyData data, Mesh mesh, IReadOnlyList<int> shapeIndices, bool overwriteSameName)
        {
            RequireImportable(data, mesh);
            if (shapeIndices == null) throw new ArgumentNullException(nameof(shapeIndices));
            var result = new List<ShapeKeyEntry>();
            if (shapeIndices.Count == 0) return result;

            var created = new List<ShapeKeyEntry>();
            foreach (int index in shapeIndices) created.Add(ShapeKeyLibrary.ImportFromBlendShape(mesh, index));

            bool anyOverwrite = false;
            if (overwriteSameName)
            {
                foreach (ShapeKeyEntry entry in created) anyOverwrite |= data.FindEntry(entry.Name) != null;
            }
            if (anyOverwrite) RecordDestructive(data, "BlendShape の取り込み", "Import BlendShapes");
            else Record(data, "BlendShape の取り込み", "Import BlendShapes");

            var used = new HashSet<string>();
            foreach (ShapeKeyEntry entry in created)
            {
                ShapeKeyEntry existing = overwriteSameName ? data.FindEntry(entry.Name) : null;
                if (existing != null)
                {
                    ShapeKeyHistory.Overwrite(existing, entry.Frames, new int[0], L10n.Tr("取り込みで上書き", "Overwritten by import"));
                    existing.InBetweenMode = entry.InBetweenMode;
                    result.Add(existing);
                    continue;
                }
                entry.Name = UniqueName(data, entry.Name, used);
                used.Add(entry.Name);
                ShapeKeyLibrary.StampCreated(entry);
                data.Entries.Add(entry);
                result.Add(entry);
            }
            Touch(data);
            return result;
        }

        /// <summary>メッシュの BlendShape を全部、データに取り込む。</summary>
        public static List<ShapeKeyEntry> ImportAllBlendShapes(GP02ShapeKeyData data, Mesh mesh, bool overwriteSameName = false)
        {
            RequireImportable(data, mesh);
            var indices = new List<int>();
            for (int i = 0; i < mesh.blendShapeCount; i++) indices.Add(i);
            return ImportBlendShapes(data, mesh, indices, overwriteSameName);
        }

        /// <summary>
        /// 別のメッシュ（このメッシュを編集して書き出したもの）を、元のメッシュとの差としてデータに取り込む。
        /// overwriteSameName が true なら、同じ名前のエントリを上書きする（前の形は履歴に残す）。
        /// </summary>
        public static ShapeKeyEntry ImportFromMeshAsset(GP02ShapeKeyData data, Mesh edited, bool overwriteSameName = false)
        {
            Require(data);
            if (edited == null) throw new ArgumentNullException(nameof(edited));
            Mesh source = RequireSourceMesh(data);
            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromMesh(source, edited);
            return AddImported(data, entry, "メッシュの取り込み", "Import mesh", overwriteSameName);
        }

        /// <summary>アニメーションクリップの指定の時刻の BlendShape の値から、1つのエントリを作って取り込む。</summary>
        public static ShapeKeyEntry ImportFromClip(GP02ShapeKeyData data, AnimationClip clip, string rendererPath, float time, bool overwriteSameName = false)
        {
            Require(data);
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            Mesh source = RequireSourceMesh(data);
            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromAnimationClip(source, clip, rendererPath, time);
            return AddImported(data, entry, "アニメーションクリップの取り込み", "Import animation clip", overwriteSameName);
        }

        /// <summary>
        /// アニメーションクリップの、すべてのキーフレームを、1つの in-between 付きシェイプキーとして取り込む
        /// （<see cref="ShapeKeyLibrary.ImportFromAnimationClipKeyframes"/>）。
        /// </summary>
        public static ShapeKeyEntry ImportFromClipKeyframes(GP02ShapeKeyData data, AnimationClip clip, string rendererPath, bool overwriteSameName = false)
        {
            Require(data);
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            Mesh source = RequireSourceMesh(data);
            ShapeKeyEntry entry = ShapeKeyLibrary.ImportFromAnimationClipKeyframes(source, clip, rendererPath);
            return AddImported(data, entry, "アニメーションクリップのキーフレームの取り込み", "Import animation clip keyframes", overwriteSameName);
        }

        static ShapeKeyEntry AddImported(GP02ShapeKeyData data, ShapeKeyEntry entry, string japanese, string english, bool overwriteSameName = false)
        {
            ShapeKeyEntry existing = overwriteSameName ? data.FindEntry(entry.Name) : null;
            if (existing != null)
            {
                RecordDestructive(data, japanese, english);
                ShapeKeyHistory.Overwrite(existing, entry.Frames, new int[0], L10n.Tr("取り込みで上書き", "Overwritten by import"));
                existing.InBetweenMode = entry.InBetweenMode;
                Touch(data);
                return existing;
            }
            Record(data, japanese, english);
            entry.Name = UniqueName(data, entry.Name);
            ShapeKeyLibrary.StampCreated(entry);
            data.Entries.Add(entry);
            Touch(data);
            return entry;
        }

        /// <summary>
        /// クリップの中での、レンダラーのパスの候補。レンダラーから親をたどって最初に見つかる Animator からの相対パスで、見つからなければ空。
        /// </summary>
        public static string GuessRendererPath(Renderer renderer)
        {
            if (renderer == null) return "";
            Animator animator = renderer.GetComponentInParent<Animator>();
            if (animator == null || animator.transform == renderer.transform) return "";
            return AnimationUtility.CalculateTransformPath(renderer.transform, animator.transform);
        }

        // ------------------------------------------------------------------
        // プレビューの重み
        // ------------------------------------------------------------------

        /// <summary>プレビューの重みを 0〜100 にそろえて設定する。一時的な値なので Undo には記録しない。</summary>
        public static void SetPreviewWeight(GP02ShapeKeyData data, ShapeKeyEntry entry, float weight)
        {
            Require(data);
            if (entry == null) return;
            entry.PreviewWeight = Mathf.Clamp(weight, 0f, 100f);
            Touch(data);
        }

        /// <summary>すべてのプレビューの重みを 0 にする（Undo には記録しない）。</summary>
        public static void ResetPreviewWeights(GP02ShapeKeyData data)
        {
            Require(data);
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null) e.PreviewWeight = 0f;
            }
            Touch(data);
        }

        /// <summary>有効なエントリに、0 でないプレビューの重みがあるか。</summary>
        public static bool HasActivePreviewWeight(GP02ShapeKeyData data)
        {
            if (data == null) return false;
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null && e.Enabled && e.PreviewWeight != 0f && e.Frames.Count > 0) return true;
            }
            return false;
        }
    }
}
