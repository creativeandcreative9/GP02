using System.Collections.Generic;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>メッシュへの書き出し（とビルド時の適用）の設定。</summary>
    public sealed class ShapeKeyBuildOptions
    {
        /// <summary>true なら、同じ名前の BlendShape を置き換える（順番は保つ）。false なら「名前 (1)」のように別の名前で加える。</summary>
        public bool OverwriteSameName = true;

        /// <summary>BlendShape の名前の前に付ける文字。</summary>
        public string NamePrefix = "";

        /// <summary>BlendShape の名前の後ろに付ける文字。</summary>
        public string NameSuffix = "";

        /// <summary>true なら、頂点数がメッシュと合わないエントリを飛ばして書き出す。false なら、書き出し自体を失敗にする。</summary>
        public bool SkipIncompatible;

        /// <summary>エントリの名前に前置き・後置きを付けた、BlendShape の名前。</summary>
        public string Decorate(string entryName) => (NamePrefix ?? "") + entryName + (NameSuffix ?? "");

        /// <summary>コンポーネントの前置き・後置き・頂点数違いを飛ばす設定を使うオプション。</summary>
        public static ShapeKeyBuildOptions FromComponent(GP02ShapeKeys component, bool overwriteSameName = true)
        {
            return new ShapeKeyBuildOptions
            {
                OverwriteSameName = overwriteSameName,
                NamePrefix = component != null ? component.NamePrefix ?? "" : "",
                NameSuffix = component != null ? component.NameSuffix ?? "" : "",
                SkipIncompatible = component != null && component.SkipIncompatible,
            };
        }
    }

    /// <summary>書き出しで、エントリ1つに起きること。</summary>
    public enum ExportAction
    {
        /// <summary>新しい BlendShape として加える。</summary>
        Add,

        /// <summary>同じ名前の BlendShape を置き換える（元のメッシュにあるもの、または前のエントリが加えるもの）。</summary>
        Overwrite,

        /// <summary>同じ名前があるので、「名前 (1)」のように別の名前で加える。</summary>
        Rename,

        /// <summary>BlendShape にせず、素体に焼き込む（<see cref="ShapeKeyEntry.Prebake"/>）。</summary>
        Prebake,

        /// <summary>同じ名前の後のエントリに置き換えられるので、書き出されない。</summary>
        Superseded,

        /// <summary>頂点数がメッシュと合わないので飛ばす。</summary>
        SkipIncompatible,

        /// <summary>差分が無いので飛ばす。</summary>
        SkipEmpty,

        /// <summary>書き出せない（名前が空、頂点数が合わない）。書き出し自体が失敗する。</summary>
        Invalid,
    }

    /// <summary>書き出しの計画の1行。</summary>
    public sealed class ExportPlanItem
    {
        public ShapeKeyEntry Entry;

        /// <summary>メッシュに入る BlendShape の名前（前置き・後置きを付けたあと）。焼き込み・飛ばすものは空。</summary>
        public string BlendShapeName = "";

        public ExportAction Action;

        /// <summary>Overwrite で、元のメッシュの BlendShape を置き換えるときの、その番号。そうでなければ -1。</summary>
        public int SourceIndex = -1;

        /// <summary>利用者に見せる説明（理由）。</summary>
        public string Detail = "";

        /// <summary>メッシュに BlendShape として入るか。</summary>
        public bool ProducesBlendShape => Action == ExportAction.Add || Action == ExportAction.Overwrite || Action == ExportAction.Rename;
    }

    /// <summary>
    /// 書き出しの前に「何が起きるか」を計算する。実際の書き出し（<see cref="ShapeKeyLibrary.BuildMesh(Mesh, GP02ShapeKeyData, ShapeKeyBuildOptions, out List{ExportPlanItem})"/>）も
    /// この計画のとおりに行うので、一覧で見たものと書き出される結果は食い違わない。メッシュは作らず、何も書き換えない。
    /// </summary>
    public static class ShapeKeyExportPlan
    {
        /// <summary>有効なエントリ（無効なものは含めない）について、書き出しで起きることの一覧をデータの並びで作る。</summary>
        public static List<ExportPlanItem> Create(Mesh source, GP02ShapeKeyData data, ShapeKeyBuildOptions options)
        {
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            if (data == null) throw new System.ArgumentNullException(nameof(data));
            options = options ?? new ShapeKeyBuildOptions();

            var items = new List<ExportPlanItem>();
            int vertexCount = source.vertexCount;
            var usedNames = new HashSet<string>();
            for (int i = 0; i < source.blendShapeCount; i++) usedNames.Add(source.GetBlendShapeName(i));
            // 同じ名前を置き換えるとき、前に加えた計画の行（後ろのもので置き換えられる）
            var latestByName = new Dictionary<string, ExportPlanItem>();

            if (data.VertexCount != vertexCount)
            {
                // データ全体がメッシュと合わない。個々のエントリでは判断できないので、全部を書き出せない扱いにする
                foreach (ShapeKeyEntry e in data.Entries)
                {
                    if (e == null || !e.Enabled || e.Frames.Count == 0) continue;
                    items.Add(new ExportPlanItem
                    {
                        Entry = e,
                        Action = ExportAction.Invalid,
                        Detail = L10n.Tr(
                            $"データ（頂点 {data.VertexCount} 個）が、メッシュ「{source.name}」（頂点 {vertexCount} 個）と頂点数が違います。",
                            $"The data ({data.VertexCount} vertices) and the mesh \"{source.name}\" ({vertexCount} vertices) have different vertex counts."),
                    });
                }
                return items;
            }

            for (int e = 0; e < data.Entries.Count; e++)
            {
                ShapeKeyEntry entry = data.Entries[e];
                if (entry == null || !entry.Enabled) continue;
                var item = new ExportPlanItem { Entry = entry };

                if (entry.Frames.Count == 0)
                {
                    item.Action = ExportAction.SkipEmpty;
                    item.Detail = L10n.Tr("差分がないので反映しません。", "No deltas, so it is not applied.");
                    items.Add(item);
                    continue;
                }
                if (!entry.Prebake && string.IsNullOrEmpty(entry.Name))
                {
                    item.Action = ExportAction.Invalid;
                    item.Detail = L10n.Tr($"{e} 番目のシェイプキーの名前が空です。", $"The name of shape key #{e} is empty.");
                    items.Add(item);
                    continue;
                }
                if (!ShapeKeyLibrary.IsEntryCompatible(entry, vertexCount))
                {
                    item.Action = options.SkipIncompatible ? ExportAction.SkipIncompatible : ExportAction.Invalid;
                    item.Detail = L10n.Tr(
                        $"差分の数が頂点数（{vertexCount}）と違います。別のメッシュ用の差分かもしれません。",
                        $"The delta count differs from the vertex count ({vertexCount}). It may have been made for another mesh.");
                    items.Add(item);
                    continue;
                }

                if (entry.Prebake)
                {
                    item.Action = ExportAction.Prebake;
                    item.Detail = L10n.Tr($"素体に重み {entry.PrebakeWeight:0.#} で焼き込みます。", $"Baked into the base at weight {entry.PrebakeWeight:0.#}.");
                    items.Add(item);
                    continue;
                }

                string name = options.Decorate(entry.Name);
                item.BlendShapeName = name;
                if (options.OverwriteSameName)
                {
                    int sourceIndex = source.GetBlendShapeIndex(name);
                    if (latestByName.TryGetValue(name, out ExportPlanItem earlier))
                    {
                        earlier.Action = ExportAction.Superseded;
                        earlier.Detail = L10n.Tr("同じ名前の後のシェイプキーに置き換えられます。", "Replaced by a later shape key with the same name.");
                    }
                    if (sourceIndex >= 0)
                    {
                        item.Action = ExportAction.Overwrite;
                        item.SourceIndex = sourceIndex;
                        item.Detail = L10n.Tr($"元のメッシュの BlendShape「{name}」を置き換えます。", $"Replaces the BlendShape \"{name}\" of the source mesh.");
                    }
                    else if (earlier != null)
                    {
                        item.Action = ExportAction.Overwrite;
                        item.Detail = L10n.Tr($"同じ名前「{name}」の前のシェイプキーを置き換えます。", $"Replaces the earlier shape key named \"{name}\".");
                    }
                    else
                    {
                        item.Action = ExportAction.Add;
                        item.Detail = L10n.Tr($"BlendShape「{name}」を加えます。", $"Adds the BlendShape \"{name}\".");
                    }
                    latestByName[name] = item;
                }
                else
                {
                    string unique = name;
                    for (int n = 1; usedNames.Contains(unique); n++) unique = $"{name} ({n})";
                    usedNames.Add(unique);
                    item.BlendShapeName = unique;
                    if (unique == name)
                    {
                        item.Action = ExportAction.Add;
                        item.Detail = L10n.Tr($"BlendShape「{unique}」を加えます。", $"Adds the BlendShape \"{unique}\".");
                    }
                    else
                    {
                        item.Action = ExportAction.Rename;
                        item.Detail = L10n.Tr($"同じ名前があるので「{unique}」として加えます。", $"The name is taken, so it is added as \"{unique}\".");
                    }
                }
                items.Add(item);
            }
            return items;
        }

        /// <summary>計画の中の、書き出し自体を失敗させる行の説明。無ければ null。</summary>
        public static string FirstProblem(IReadOnlyList<ExportPlanItem> items)
        {
            foreach (ExportPlanItem item in items)
            {
                if (item.Action == ExportAction.Invalid) return item.Detail;
            }
            return null;
        }

        /// <summary>行の短い見出し（「追加」「上書き」…）。</summary>
        public static string Label(ExportAction action)
        {
            switch (action)
            {
                case ExportAction.Add: return L10n.Tr("追加", "Add");
                case ExportAction.Overwrite: return L10n.Tr("上書き", "Overwrite");
                case ExportAction.Rename: return L10n.Tr("別名", "Renamed");
                case ExportAction.Prebake: return L10n.Tr("焼き込み", "Bake");
                case ExportAction.Superseded: return L10n.Tr("置換される", "Replaced");
                case ExportAction.SkipIncompatible: return L10n.Tr("飛ばす", "Skip");
                case ExportAction.SkipEmpty: return L10n.Tr("飛ばす", "Skip");
                default: return L10n.Tr("エラー", "Error");
            }
        }

        /// <summary>数のまとめ（「追加 3・上書き 1・飛ばす 1」）。</summary>
        public static string Summarize(IReadOnlyList<ExportPlanItem> items)
        {
            int add = 0, overwrite = 0, rename = 0, bake = 0, skip = 0, invalid = 0;
            foreach (ExportPlanItem item in items)
            {
                switch (item.Action)
                {
                    case ExportAction.Add: add++; break;
                    case ExportAction.Overwrite: overwrite++; break;
                    case ExportAction.Rename: rename++; break;
                    case ExportAction.Prebake: bake++; break;
                    case ExportAction.Invalid: invalid++; break;
                    default: skip++; break;
                }
            }
            var parts = new List<string>();
            if (add > 0) parts.Add(L10n.Tr($"追加 {add}", $"Add {add}"));
            if (overwrite > 0) parts.Add(L10n.Tr($"上書き {overwrite}", $"Overwrite {overwrite}"));
            if (rename > 0) parts.Add(L10n.Tr($"別名 {rename}", $"Renamed {rename}"));
            if (bake > 0) parts.Add(L10n.Tr($"焼き込み {bake}", $"Bake {bake}"));
            if (skip > 0) parts.Add(L10n.Tr($"飛ばす {skip}", $"Skip {skip}"));
            if (invalid > 0) parts.Add(L10n.Tr($"エラー {invalid}", $"Error {invalid}"));
            return parts.Count == 0 ? L10n.Tr("反映するものはありません", "Nothing to apply") : string.Join(L10n.Tr("・", ", "), parts);
        }
    }
}
