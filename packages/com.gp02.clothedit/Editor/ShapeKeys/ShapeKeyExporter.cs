using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーのデータを、メッシュ（アセット）に書き出してレンダラーに割り当てる。画面に依存しない。
    /// 割り当て・アセットの作成はすべて Unity の Undo で戻せる。元のメッシュ（<see cref="GP02ShapeKeyData.SourceMesh"/>）は変更せず、
    /// 書き出しは必ず複製に対して行う。書き出しても編集データは残るので、何度でも書き出し直せる。
    /// </summary>
    public static class ShapeKeyExporter
    {
        public const string DefaultFolder = "Assets/GP02/Meshes";

        /// <summary>書き出しの前に、プレビューの一時メッシュを元に戻す（一時メッシュを書き出さないため。Undo の記録とも混ざらない）。</summary>
        static void PrepareRenderer(Renderer renderer)
        {
            ShapeKeyPreview.End(renderer);
        }

        /// <summary>書き出しや焼き込みができない理由。できるなら null。利用者に見せる文章。</summary>
        public static string GetProblem(GP02ShapeKeys component)
        {
            if (component == null) return L10n.Tr("シェイプキーのコンポーネントがありません。", "There is no shape key component.");
            if (component.Data == null) return L10n.Tr("シェイプキーのデータが設定されていません。", "No shape key data is set.");
            if (ClothEditController.FindRenderer(component.gameObject) == null)
            {
                return L10n.Tr("SkinnedMeshRenderer（または MeshFilter 付きの MeshRenderer）が同じオブジェクトにありません。", "The object has no SkinnedMeshRenderer (or MeshRenderer with a MeshFilter).");
            }
            Mesh source = GetSourceMesh(component);
            if (source == null) return L10n.Tr("元のメッシュが見つかりません。", "The source mesh is missing.");
            if (source.vertexCount != component.Data.VertexCount)
            {
                return L10n.Tr(
                    $"元のメッシュの頂点数（{source.vertexCount}）が、データの頂点数（{component.Data.VertexCount}）と違います。",
                    $"The source mesh vertex count ({source.vertexCount}) differs from the data ({component.Data.VertexCount}).");
            }
            return null;
        }

        /// <summary>書き出しの元になるメッシュ。データの元のメッシュを優先し、無ければコンポーネントのもの。</summary>
        public static Mesh GetSourceMesh(GP02ShapeKeys component)
        {
            if (component == null) return null;
            if (component.Data != null && component.Data.SourceMesh != null) return component.Data.SourceMesh;
            return component.SourceMesh;
        }

        // ------------------------------------------------------------------
        // メッシュへの書き出し
        // ------------------------------------------------------------------

        /// <summary>
        /// 有効なエントリを BlendShape として元のメッシュの複製に入れ、アセットとして保存してレンダラーに割り当てる。
        /// 前回の書き出し（<see cref="GP02ShapeKeys.ExportedMesh"/>）があれば同じアセットを上書きする（アセットが増え続けない。参照も保たれる）。
        /// </summary>
        /// <param name="overwriteSameName">true なら同じ名前の BlendShape を置き換える。false なら別の名前で加える</param>
        /// <exception cref="InvalidOperationException">書き出せないとき（理由は <see cref="GetProblem"/> と同じ）</exception>
        public static Mesh ExportToMesh(GP02ShapeKeys component, bool overwriteSameName, string folder = DefaultFolder)
        {
            string problem = GetProblem(component);
            if (problem != null) throw new InvalidOperationException(problem);
            Renderer renderer = ClothEditController.FindRenderer(component.gameObject);
            Mesh source = GetSourceMesh(component);

            PrepareRenderer(renderer);
            Mesh built = ShapeKeyLibrary.BuildMesh(source, component.Data, overwriteSameName);
            Undo.IncrementCurrentGroup();

            // 元のメッシュそのものを上書きしてしまわないよう、元と同じなら前回の書き出しとはみなさない
            Mesh previous = component.ExportedMesh != source ? component.ExportedMesh : null;
            Mesh result = SaveMeshAsset(built, previous, folder, "GP02 " + L10n.Tr("メッシュに書き出す", "Export to mesh"));
            AssignMesh(renderer, result, "GP02 " + L10n.Tr("メッシュを割り当て", "Assign mesh"));
            Undo.RecordObject(component, "GP02 " + L10n.Tr("書き出したメッシュの記録", "Record exported mesh"));
            component.ExportedMesh = result;
            EditorUtility.SetDirty(component);
            AssetDatabase.SaveAssets();
            Undo.IncrementCurrentGroup();
            return result;
        }

        // built を asset として保存する。previous が既存のアセットならその中身を置き換え、built は捨てる
        static Mesh SaveMeshAsset(Mesh built, Mesh previous, string folder, string undoName)
        {
            string previousPath = previous != null ? AssetDatabase.GetAssetPath(previous) : "";
            bool canOverwrite = !string.IsNullOrEmpty(previousPath) && previousPath.EndsWith(".asset") && AssetDatabase.IsMainAsset(previous);
            if (canOverwrite)
            {
                Undo.RegisterCompleteObjectUndo(previous, undoName);
                EditorUtility.CopySerialized(built, previous);
                Object.DestroyImmediate(built);
                EditorUtility.SetDirty(previous);
                return previous;
            }

            folder = (folder ?? DefaultFolder).TrimEnd('/');
            EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(built.name)}.asset");
            AssetDatabase.CreateAsset(built, path);
            Undo.RegisterCreatedObjectUndo(built, undoName);
            return built;
        }

        // ------------------------------------------------------------------
        // 素体への焼き込み
        // ------------------------------------------------------------------

        /// <summary>
        /// 選んだエントリを、重みをかけて素体の形に焼き込んだメッシュを新しいアセットとして保存し、レンダラーに割り当てる。
        /// 元のメッシュと、編集データはそのまま。
        /// </summary>
        /// <param name="weights">エントリごとの重み（0〜100）。null ならプレビューの重み</param>
        /// <param name="remapBoneWeights">true なら、大きく動いた頂点のボーンウェイトを動いた先の頂点から付け替える</param>
        /// <param name="remapThreshold">付け替える動きの大きさ（メートル）</param>
        public static Mesh BakeIntoBase(GP02ShapeKeys component, IReadOnlyList<ShapeKeyEntry> entries, IReadOnlyList<float> weights,
            bool remapBoneWeights, float remapThreshold, string folder = DefaultFolder)
        {
            string problem = GetProblem(component);
            if (problem != null) throw new InvalidOperationException(problem);
            if (entries == null || entries.Count == 0) throw new ArgumentException(L10n.Tr("焼き込むシェイプキーを選んでください。", "Select the shape keys to bake."), nameof(entries));
            Renderer renderer = ClothEditController.FindRenderer(component.gameObject);
            Mesh source = GetSourceMesh(component);

            PrepareRenderer(renderer);
            Mesh basis = source;
            Mesh remapped = null;
            Mesh baked;
            try
            {
                if (remapBoneWeights)
                {
                    Vector3[] total = TotalDeltas(entries, weights, source.vertexCount);
                    basis = remapped = ShapeKeyLibrary.RemapBoneWeights(source, total, remapThreshold);
                }
                baked = ShapeKeyLibrary.BakeIntoBase(basis, entries, weights);
            }
            finally
            {
                if (remapped != null) Object.DestroyImmediate(remapped);
            }
            baked.name = source.name.EndsWith("_Baked") ? source.name : source.name + "_Baked";

            Undo.IncrementCurrentGroup();
            Mesh result = SaveMeshAsset(baked, null, folder, "GP02 " + L10n.Tr("素体に焼き込む", "Bake into base"));
            AssignMesh(renderer, result, "GP02 " + L10n.Tr("メッシュを割り当て", "Assign mesh"));
            AssetDatabase.SaveAssets();
            Undo.IncrementCurrentGroup();
            return result;
        }

        // エントリを重みで評価して足した、位置の差分
        static Vector3[] TotalDeltas(IReadOnlyList<ShapeKeyEntry> entries, IReadOnlyList<float> weights, int count)
        {
            var total = new Vector3[count];
            for (int e = 0; e < entries.Count; e++)
            {
                ShapeKeyEntry entry = entries[e];
                if (entry == null || entry.Frames.Count == 0) continue;
                float weight = weights != null ? weights[e] : entry.PreviewWeight;
                if (weight == 0f) continue;
                Vector3[] d = ShapeKeyLibrary.EvaluateEntry(entry, weight, count).DeltaVertices;
                for (int v = 0; v < count; v++) total[v] += d[v];
            }
            return total;
        }

        // ------------------------------------------------------------------
        // 元のメッシュに戻す
        // ------------------------------------------------------------------

        /// <summary>レンダラーのメッシュを、元のメッシュ（<see cref="GP02ShapeKeys.SourceMesh"/>）に戻す。戻せたら true（Undo できる）。</summary>
        public static bool RestoreSourceMesh(GP02ShapeKeys component)
        {
            if (component == null) return false;
            Renderer renderer = ClothEditController.FindRenderer(component.gameObject);
            Mesh source = component.SourceMesh != null ? component.SourceMesh : (component.Data != null ? component.Data.SourceMesh : null);
            if (renderer == null || source == null) return false;

            PrepareRenderer(renderer);
            Undo.IncrementCurrentGroup();
            AssignMesh(renderer, source, "GP02 " + L10n.Tr("元のメッシュに戻す", "Restore the original mesh"));
            Undo.IncrementCurrentGroup();
            return true;
        }

        /// <summary>レンダラーが今どのメッシュを使っているか。</summary>
        public static MeshState GetMeshState(GP02ShapeKeys component, Renderer renderer)
        {
            // プレビュー中は一時メッシュが入っているので、プレビューの前のメッシュで見る
            Mesh mesh = ShapeKeyPreview.GetOriginalMesh(renderer) ?? ClothEditController.GetMesh(renderer);
            if (mesh == null) return MeshState.None;
            if (component == null) return MeshState.Other;
            if (mesh == component.SourceMesh || (component.Data != null && mesh == component.Data.SourceMesh)) return MeshState.Source;
            if (component.ExportedMesh != null && mesh == component.ExportedMesh) return MeshState.Exported;
            return MeshState.Other;
        }

        // ------------------------------------------------------------------
        // 共通
        // ------------------------------------------------------------------

        static void AssignMesh(Renderer renderer, Mesh mesh, string undoName)
        {
            Object owner = renderer is SkinnedMeshRenderer ? (Object)renderer : renderer.GetComponent<MeshFilter>();
            Undo.RecordObject(owner, undoName);
            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
            EditorUtility.SetDirty(owner);
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }

    /// <summary>レンダラーが今使っているメッシュの種類。</summary>
    public enum MeshState
    {
        /// <summary>メッシュが無い（None）。</summary>
        None,

        /// <summary>元のメッシュ。</summary>
        Source,

        /// <summary>前回「メッシュに書き出す」で作ったメッシュ。</summary>
        Exported,

        /// <summary>どちらでもない（焼き込んだメッシュ、別のメッシュなど）。</summary>
        Other,
    }
}
