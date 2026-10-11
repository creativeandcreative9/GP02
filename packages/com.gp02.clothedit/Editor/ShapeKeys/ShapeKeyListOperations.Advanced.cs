using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>一覧の並べ替え。</summary>
    public enum ShapeKeyListSort
    {
        /// <summary>データに入っている順（ドラッグで並べ替えた順）。</summary>
        DataOrder = 0,

        /// <summary>名前の順（大文字小文字を区別しない）。</summary>
        Name = 1,

        /// <summary>作った順（古いものが先。日時の無い古いデータは、データの順で先頭）。</summary>
        Created = 2,
    }

    /// <summary>選んだ点の範囲（マスク）で、シェイプキーの差分をどうするか。</summary>
    public enum ShapeKeyMaskOperation
    {
        /// <summary>範囲の中だけ残す（取り出す）。</summary>
        Extract = 0,

        /// <summary>範囲の中を消す（範囲の外だけ残す）。</summary>
        Remove = 1,
    }

    /// <summary>別のメッシュへ写した結果。</summary>
    public sealed class ShapeKeyTransferResult
    {
        /// <summary>写し先のデータに加えたエントリ。</summary>
        public readonly List<ShapeKeyEntry> Entries = new List<ShapeKeyEntry>();

        /// <summary>写し先の頂点のうち、距離の上限の中に元の面が見つからず、差分が 0 になった数。</summary>
        public int MissCount;

        /// <summary>写し先の頂点数。</summary>
        public int VertexCount;
    }

    public static partial class ShapeKeyListOperations
    {
        // ------------------------------------------------------------------
        // 素体への焼き込みの印
        // ------------------------------------------------------------------

        /// <summary>選んだエントリに、素体への焼き込みの印を付ける（外す）。Undo できる。</summary>
        public static void SetPrebake(GP02ShapeKeyData data, IReadOnlyList<ShapeKeyEntry> entries, bool prebake)
        {
            Require(data);
            List<ShapeKeyEntry> targets = InDataOrder(data, entries);
            if (targets.Count == 0) return;
            Record(data, prebake ? "素体に焼き込む印を付ける" : "素体に焼き込む印を外す", prebake ? "Mark as prebake" : "Unmark prebake");
            foreach (ShapeKeyEntry e in targets) e.Prebake = prebake;
            Touch(data);
        }

        /// <summary>焼き込むときの重みを 0〜100 にそろえて設定する。Undo できる。</summary>
        public static void SetPrebakeWeight(GP02ShapeKeyData data, ShapeKeyEntry entry, float weight)
        {
            Require(data);
            if (entry == null) return;
            weight = Mathf.Clamp(weight, 0f, 100f);
            if (entry.PrebakeWeight == weight) return;
            Record(data, "焼き込む重みの変更", "Change prebake weight");
            entry.PrebakeWeight = weight;
            Touch(data);
        }

        // ------------------------------------------------------------------
        // in-between のフレーム
        // ------------------------------------------------------------------

        /// <summary>フレームの重みの下限（0 のフレームは形を作らないので、わずかに正の値にする）。</summary>
        public const float MinFrameWeight = 0.01f;

        /// <summary>
        /// エントリの index 番目のフレームを消す。最後の 1 つは消せない（差分が無くなるため）。消せたら true。
        /// 消す前のデータのバックアップを取り、前の形は履歴にも残す。
        /// </summary>
        public static bool DeleteFrame(GP02ShapeKeyData data, ShapeKeyEntry entry, int index)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) return false;
            if (entry.Frames.Count <= 1 || index < 0 || index >= entry.Frames.Count) return false;
            RecordDestructive(data, "フレームの削除", "Delete frame");
            List<ShapeKeyFrame> frames = ShapeKeyHistory.CloneFrames(entry.Frames);
            frames.RemoveAt(index);
            ShapeKeyHistory.Overwrite(entry, frames, entry.PinnedVertices, L10n.Tr("フレームの削除", "Delete frame"));
            Touch(data);
            return true;
        }

        /// <summary>
        /// エントリの index 番目のフレームの重みを変える（0.01〜100 に収め、重みの順に並べ直す）。
        /// 他のフレームと同じ重み（0.01 未満の差）になるときは何もせず false。
        /// </summary>
        public static bool SetFrameWeight(GP02ShapeKeyData data, ShapeKeyEntry entry, int index, float weight)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) return false;
            if (index < 0 || index >= entry.Frames.Count) return false;
            weight = Mathf.Clamp(weight, MinFrameWeight, ShapeKeyMath.FullWeight);
            for (int i = 0; i < entry.Frames.Count; i++)
            {
                if (i != index && Mathf.Abs(entry.Frames[i].Weight - weight) < MinFrameWeight) return false;
            }
            if (Mathf.Approximately(entry.Frames[index].Weight, weight)) return true;

            RecordDestructive(data, "フレームの重みの変更", "Change frame weight");
            List<ShapeKeyFrame> frames = ShapeKeyHistory.CloneFrames(entry.Frames);
            frames[index].Weight = weight;
            frames.Sort((a, b) => a.Weight.CompareTo(b.Weight));
            ShapeKeyHistory.Overwrite(entry, frames, entry.PinnedVertices, L10n.Tr("フレームの重みの変更", "Change frame weight"));
            Touch(data);
            return true;
        }

        // ------------------------------------------------------------------
        // 差し引き
        // ------------------------------------------------------------------

        /// <summary>
        /// a - b の新しいエントリを作る（重み 100 での形で引く。in-between も評価する）。新しいエントリは a と b の後ろに入る。
        /// 名前が空なら「a_minus_b」。
        /// </summary>
        /// <param name="disableSources">true なら、a と b を無効にする（書き出しで二重に動かないように）</param>
        public static ShapeKeyEntry Subtract(GP02ShapeKeyData data, ShapeKeyEntry a, ShapeKeyEntry b, string name = null, bool disableSources = false)
        {
            Require(data);
            if (a == null || b == null || !data.Entries.Contains(a) || !data.Entries.Contains(b))
            {
                throw new ArgumentException(L10n.Tr("引くシェイプキーを2つ選んでください。", "Select two shape keys to subtract."));
            }
            RequireCompatible(data, a);
            RequireCompatible(data, b);

            string desired = string.IsNullOrWhiteSpace(name) ? a.Name + "_minus_" + b.Name : name;
            ShapeKeyEntry result = ShapeKeyLibrary.SubtractEntries(a, b, data.VertexCount, UniqueName(data, desired));
            if (disableSources) RecordDestructive(data, "差し引き", "Subtract");
            else Record(data, "差し引き", "Subtract");
            ShapeKeyLibrary.StampCreated(result);
            data.Entries.Insert(IndexOfLast(data, new[] { a, b }) + 1, result);
            if (disableSources)
            {
                a.Enabled = false;
                b.Enabled = false;
            }
            Touch(data);
            return result;
        }

        // ------------------------------------------------------------------
        // 選んだ点の範囲（マスク）で分ける・取り出す・消す
        // ------------------------------------------------------------------

        // マスクの検査と、境目をなめらかにする
        static float[] PrepareMask(GP02ShapeKeyData data, float[] mask, int smoothIterations)
        {
            if (mask == null) throw new ArgumentException(L10n.Tr("範囲（マスク）がありません。布の編集で点を選んでください。", "There is no range (mask). Select points in the cloth edit."), nameof(mask));
            if (mask.Length != data.VertexCount)
            {
                throw new ArgumentException(L10n.Tr(
                    $"範囲の頂点数（{mask.Length}）が、データの頂点数（{data.VertexCount}）と違います。",
                    $"The vertex count of the range ({mask.Length}) differs from the data ({data.VertexCount})."), nameof(mask));
            }
            float[] result = ShapeMask.Clamp01(mask);
            if (smoothIterations > 0)
            {
                Mesh mesh = RequireSourceMesh(data);
                result = ShapeMask.Smooth(result, MeshBridge.ToNumerics(mesh.vertices, Vector3.one), mesh.triangles, smoothIterations);
            }
            return result;
        }

        /// <summary>
        /// エントリを、マスクの範囲の中と外の 2 つに分ける（元の名前に insideSuffix・outsideSuffix を付ける）。2 つを足すと元に戻る。
        /// 新しい 2 つは元のすぐ後ろに入る。
        /// </summary>
        /// <param name="mask">頂点ごとの 0〜1 の重み（データの頂点数）。1 が範囲の中</param>
        /// <param name="smoothIterations">範囲の境目をなめらかにする回数（0 でそのまま）</param>
        /// <param name="disableOriginal">true なら、元のエントリを無効にする</param>
        public static void SplitByMask(GP02ShapeKeyData data, ShapeKeyEntry entry, float[] mask, int smoothIterations,
            string insideSuffix, string outsideSuffix, bool disableOriginal, out ShapeKeyEntry inside, out ShapeKeyEntry outside)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("分割するシェイプキーがデータにありません。", "The shape key to split is not in the data."), nameof(entry));
            RequireCompatible(data, entry);
            float[] prepared = PrepareMask(data, mask, smoothIterations);

            inside = ShapeKeyLibrary.MaskEntry(entry, prepared, false, entry.Name + insideSuffix);
            outside = ShapeKeyLibrary.MaskEntry(entry, prepared, true, entry.Name + outsideSuffix);
            var used = new HashSet<string>();
            inside.Name = UniqueName(data, inside.Name, used);
            used.Add(inside.Name);
            outside.Name = UniqueName(data, outside.Name, used);

            if (disableOriginal) RecordDestructive(data, "選んだ点で分割", "Split by selection");
            else Record(data, "選んだ点で分割", "Split by selection");
            ShapeKeyLibrary.StampCreated(inside);
            ShapeKeyLibrary.StampCreated(outside);
            int at = data.Entries.IndexOf(entry);
            data.Entries.Insert(at + 1, inside);
            data.Entries.Insert(at + 2, outside);
            if (disableOriginal) entry.Enabled = false;
            Touch(data);
        }

        /// <summary>
        /// エントリの、マスクの範囲の中だけを取り出す（Extract）か、範囲の中を消す（Remove）。
        /// asCopy が true なら新しいエントリを元の後ろに加え（名前は「名前_Sel」「名前_Rest」）、false なら元のエントリを書き換える（前の形は履歴に残す）。
        /// </summary>
        public static ShapeKeyEntry ApplyMask(GP02ShapeKeyData data, ShapeKeyEntry entry, float[] mask, int smoothIterations,
            ShapeKeyMaskOperation operation, bool asCopy)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("対象のシェイプキーがデータにありません。", "The shape key is not in the data."), nameof(entry));
            RequireCompatible(data, entry);
            float[] prepared = PrepareMask(data, mask, smoothIterations);

            bool remove = operation == ShapeKeyMaskOperation.Remove;
            string japanese = remove ? "選んだ点の範囲を消す" : "選んだ点の範囲だけ取り出す";
            string english = remove ? "Remove the selected range" : "Extract the selected range";
            ShapeKeyEntry result = ShapeKeyLibrary.MaskEntry(entry, prepared, remove);
            return ApplyResult(data, entry, result, asCopy, remove ? "_Rest" : "_Sel", japanese, english);
        }

        /// <summary>
        /// エントリを、平面の両側の 2 つに分ける（元の名前に _L・_R を付ける）。2 つを足すと元に戻る。新しい 2 つは元のすぐ後ろに入る。
        /// 平面は元のメッシュのメッシュ空間で指定する。アバターのローカル軸で分けるときは、呼び出し側が軸をメッシュ空間に換算して渡す。
        /// </summary>
        /// <param name="normal">平面の法線（メッシュ空間）。この向きが正の側が left（leftIsPositive が true のとき）</param>
        /// <param name="offset">平面の位置（正規化した法線での、原点からの距離）</param>
        /// <param name="blendWidth">中央をなめらかに分ける幅（メートル）</param>
        public static void SplitByPlane(GP02ShapeKeyData data, ShapeKeyEntry entry, Vector3 normal, float offset, float blendWidth,
            bool leftIsPositive, bool disableOriginal, out ShapeKeyEntry left, out ShapeKeyEntry right)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("分割するシェイプキーがデータにありません。", "The shape key to split is not in the data."), nameof(entry));
            RequireCompatible(data, entry);
            Mesh mesh = RequireSourceMesh(data);

            float[] mask = ShapeMask.PlaneMask(MeshBridge.ToNumerics(mesh.vertices, Vector3.one), new System.Numerics.Vector3(normal.x, normal.y, normal.z), offset, blendWidth);
            if (!leftIsPositive) mask = ShapeMask.Invert(mask);
            SplitByMask(data, entry, mask, 0, "_L", "_R", disableOriginal, out left, out right);
        }

        /// <summary>
        /// アバターのローカル軸（axis: 0=X, 1=Y, 2=Z。avatarRoot の座標）の center の位置を通る平面を、meshSpace の座標（メッシュ空間）の平面に換算する。
        /// meshSpace はメッシュを持つレンダラーの Transform（頂点はこの Transform のローカル座標とみなす）。
        /// 平面はスケールが一様でなくても正しく換算する。換算できなければ（スケール 0 など）false。
        /// </summary>
        public static bool TryGetMeshPlane(Transform meshSpace, Transform avatarRoot, int axis, float center, out Vector3 normal, out float offset)
        {
            normal = Vector3.right;
            offset = 0f;
            if (meshSpace == null || avatarRoot == null) return false;
            Vector3 axisLocal = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            Vector3 pointWorld = avatarRoot.TransformPoint(axisLocal * center);
            // 平面 e・x_a = c の法線は、ローカルからワールドへ直すと (ワールド→ローカル)の転置 × e になる
            Vector3 normalWorld = avatarRoot.worldToLocalMatrix.transpose.MultiplyVector(axisLocal);

            Matrix4x4 meshToWorld = meshSpace.localToWorldMatrix;
            if (Mathf.Abs(meshToWorld.determinant) < 1e-12f) return false;
            Vector3 pointMesh = meshToWorld.inverse.MultiplyPoint3x4(pointWorld);
            Vector3 normalMesh = meshToWorld.transpose.MultiplyVector(normalWorld);
            if (normalMesh.sqrMagnitude < 1e-20f) return false;
            normal = normalMesh.normalized;
            offset = Vector3.Dot(normal, pointMesh);
            return true;
        }

        // ------------------------------------------------------------------
        // 別のメッシュへ写す
        // ------------------------------------------------------------------

        /// <summary>
        /// 選んだエントリを、別のメッシュ（頂点数が違ってもよい）用のデータへ写す。
        /// 写し先の頂点ごとに、元のメッシュの元の形の表面で一番近い点を探し、その三角形の頂点の差分を重心座標で混ぜる。
        /// 距離の上限を超える頂点の差分は 0。法線が逆向きの面は避ける。2 つのメッシュは同じ座標系（同じ向き・大きさ・位置）にあること。
        /// 写し先のデータに新しいエントリとして加える（名前が重なるときは番号を付ける）。元のデータは変えない。
        /// </summary>
        /// <param name="maxDistance">これより遠い頂点は写さない（メートル）</param>
        /// <param name="minNormalDot">写し先の法線との内積がこれ未満の面は避ける（0 で逆向きを避ける。-1 以下で向きを問わない）</param>
        public static ShapeKeyTransferResult TransferEntries(GP02ShapeKeyData source, IReadOnlyList<ShapeKeyEntry> entries, GP02ShapeKeyData target,
            float maxDistance, float minNormalDot = 0f)
        {
            Require(source);
            Require(target);
            if (maxDistance < 0f) throw new ArgumentOutOfRangeException(nameof(maxDistance));
            List<ShapeKeyEntry> targets = InDataOrder(source, entries);
            if (targets.Count == 0) throw new ArgumentException(L10n.Tr("写すシェイプキーを選んでください。", "Select the shape keys to transfer."), nameof(entries));
            foreach (ShapeKeyEntry e in targets) RequireCompatible(source, e);
            Mesh sourceMesh = RequireSourceMesh(source);
            if (target.SourceMesh == null)
            {
                throw new InvalidOperationException(L10n.Tr("写し先のデータの元のメッシュが見つかりません。", "The source mesh of the destination data is missing."));
            }
            if (target.SourceMesh.vertexCount != target.VertexCount)
            {
                throw new InvalidOperationException(L10n.Tr(
                    $"写し先のデータの元のメッシュ（頂点 {target.SourceMesh.vertexCount} 個）が、データの頂点数（{target.VertexCount}）と違います。",
                    $"The source mesh of the destination data ({target.SourceMesh.vertexCount} vertices) does not match its vertex count ({target.VertexCount})."));
            }

            ShapeTransfer.Mapping mapping = ShapeKeyLibrary.CreateTransferMapping(sourceMesh, target.SourceMesh, maxDistance, minNormalDot);
            var created = new List<ShapeKeyEntry>();
            foreach (ShapeKeyEntry e in targets) created.Add(ShapeKeyLibrary.TransferEntry(e, mapping));

            Record(target, "別のメッシュへ写す", "Transfer to another mesh");
            var result = new ShapeKeyTransferResult { MissCount = mapping.MissCount, VertexCount = mapping.VertexCount };
            var used = new HashSet<string>();
            foreach (ShapeKeyEntry entry in created)
            {
                entry.Name = UniqueName(target, entry.Name, used);
                used.Add(entry.Name);
                ShapeKeyLibrary.StampCreated(entry);
                if (!string.IsNullOrEmpty(entry.Group) && !target.Groups.Contains(entry.Group)) target.Groups.Add(entry.Group);
                target.Entries.Add(entry);
                result.Entries.Add(entry);
            }
            Touch(target);
            return result;
        }

        // ------------------------------------------------------------------
        // 前の版・バックアップ
        // ------------------------------------------------------------------

        /// <summary>エントリを、前の版（<see cref="ShapeKeyEntry.History"/> の index 番目）に戻す。Undo できる。戻す前のバックアップを取り、戻す前の形も履歴に残す。</summary>
        public static void RestoreVersion(GP02ShapeKeyData data, ShapeKeyEntry entry, int versionIndex)
        {
            Require(data);
            if (entry == null || !data.Entries.Contains(entry)) throw new ArgumentException(L10n.Tr("シェイプキーがデータにありません。", "The shape key is not in the data."), nameof(entry));
            if (versionIndex < 0 || versionIndex >= entry.History.Count) throw new ArgumentOutOfRangeException(nameof(versionIndex));
            ShapeKeyVersion version = entry.History[versionIndex];
            foreach (ShapeKeyFrame frame in version.Frames)
            {
                if (frame.DeltaVertices == null || frame.DeltaVertices.Length != data.VertexCount)
                {
                    throw new InvalidOperationException(L10n.Tr(
                        "この版は、今のデータとは頂点数が違うので戻せません。",
                        "This version has a different vertex count from the current data, so it cannot be restored."));
                }
            }
            RecordDestructive(data, "以前の版に戻す", "Restore older version");
            ShapeKeyHistory.Restore(entry, versionIndex);
            Touch(data);
        }

        /// <summary>バックアップから戻す（<see cref="ShapeKeyBackup.Restore"/>）。戻せたら true。Undo できる。</summary>
        public static bool RestoreFromBackup(GP02ShapeKeyData data, string backupPath)
        {
            Require(data);
            return ShapeKeyBackup.Restore(data, backupPath);
        }

        // ------------------------------------------------------------------
        // 並べ替え・絞り込み（一覧の表示用）
        // ------------------------------------------------------------------

        /// <summary>並べ替えた順のエントリを返す（元の一覧は変えない）。同じ順位のものはデータの順。</summary>
        public static List<ShapeKeyEntry> OrderForDisplay(IReadOnlyList<ShapeKeyEntry> entries, ShapeKeyListSort sort)
        {
            var indexed = new List<KeyValuePair<int, ShapeKeyEntry>>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null) indexed.Add(new KeyValuePair<int, ShapeKeyEntry>(i, entries[i]));
            }
            if (sort != ShapeKeyListSort.DataOrder)
            {
                indexed.Sort((a, b) =>
                {
                    int c = sort == ShapeKeyListSort.Name
                        ? string.Compare(a.Value.Name, b.Value.Name, StringComparison.OrdinalIgnoreCase)
                        : a.Value.CreatedTicks.CompareTo(b.Value.CreatedTicks);
                    return c != 0 ? c : a.Key.CompareTo(b.Key);
                });
            }
            var result = new List<ShapeKeyEntry>(indexed.Count);
            foreach (var pair in indexed) result.Add(pair.Value);
            return result;
        }

        /// <summary>データの並びを、sort の順に並べ替えて確定する（Undo できる）。グループの中の順も、全体の順に従う。</summary>
        public static void SortEntries(GP02ShapeKeyData data, ShapeKeyListSort sort)
        {
            Require(data);
            if (sort == ShapeKeyListSort.DataOrder) return;
            List<ShapeKeyEntry> ordered = OrderForDisplay(data.Entries, sort);
            Record(data, "並べ替えの確定", "Apply sort");
            data.Entries = ordered;
            Touch(data);
        }

        /// <summary>
        /// 名前が検索の文字に合うか。空白で区切った文字がすべて名前に含まれていれば合う（大文字小文字は区別しない）。空の検索はすべてに合う。
        /// </summary>
        public static bool MatchesFilter(ShapeKeyEntry entry, string filter)
        {
            if (entry == null) return false;
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string name = entry.Name ?? "";
            foreach (string token in filter.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }
    }
}
