using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// アバターの中の <see cref="GP02ShapeKeys"/> を、ビルド用の複製に適用する。NDMF には依存しない（NDMF 用の asmdef から呼ぶ）。
    /// <see cref="ShapeKeyApplyMode.NonDestructive"/> のものは、メッシュに BlendShape を足してレンダラーに割り当て、コンポーネントを消す。
    /// <see cref="ShapeKeyApplyMode.ExportedMesh"/> のものは、メッシュはすでに割り当て済みなのでコンポーネントを消すだけ。
    /// シーンのレンダラーやアセットのメッシュは変えない（呼び出し側が渡すのは、複製したアバター）。
    /// </summary>
    public static class NonDestructiveApplier
    {
        public sealed class Result
        {
            /// <summary>メッシュを作って差し替えた数。</summary>
            public int Applied;

            /// <summary>消したコンポーネントの数（差し替えたものも含む）。</summary>
            public int Removed;

            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// root の下（root 自身も含む、非アクティブも含む）の <see cref="GP02ShapeKeys"/> をすべて処理する。
        /// </summary>
        /// <param name="saveAsset">作ったメッシュを保存する処理（NDMF なら <c>ctx.AssetSaver.SaveAsset</c>）。null なら保存しない</param>
        /// <exception cref="InvalidOperationException">データとメッシュの頂点数が合わないなど、正しく適用できないとき</exception>
        public static Result Apply(GameObject root, Action<Object> saveAsset)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var result = new Result();
            GP02ShapeKeys[] components = root.GetComponentsInChildren<GP02ShapeKeys>(true);
            foreach (GP02ShapeKeys component in components)
            {
                if (component == null) continue;
                if (component.ApplyMode == ShapeKeyApplyMode.NonDestructive && ApplyOne(component, saveAsset, result)) result.Applied++;
                Object.DestroyImmediate(component);
                result.Removed++;
            }
            return result;
        }

        static bool ApplyOne(GP02ShapeKeys component, Action<Object> saveAsset, Result result)
        {
            string path = component.gameObject.name;
            if (component.Data == null)
            {
                result.Warnings.Add($"{path}: シェイプキーのデータが設定されていないので、適用しませんでした。");
                return false;
            }
            Renderer renderer = ClothEditController.FindRenderer(component.gameObject);
            if (renderer == null)
            {
                throw new InvalidOperationException($"{path}: SkinnedMeshRenderer（または MeshFilter 付きの MeshRenderer）が同じオブジェクトにないので、シェイプキーを適用できません。");
            }
            Mesh source = ChooseSourceMesh(component, renderer);
            if (source == null) throw new InvalidOperationException($"{path}: 元のメッシュが見つからないので、シェイプキーを適用できません。");
            if (source.vertexCount != component.Data.VertexCount)
            {
                throw new InvalidOperationException(
                    $"{path}: メッシュ「{source.name}」の頂点数（{source.vertexCount}）が、データの頂点数（{component.Data.VertexCount}）と違うので、シェイプキーを適用できません。");
            }

            // 名前の前置き・後置きと「焼き込み」の印は、書き出しと同じ計画で扱う
            ShapeKeyBuildOptions options = ShapeKeyBuildOptions.FromComponent(component, true);
            Mesh built = ShapeKeyLibrary.BuildMesh(source, component.Data, options, out List<ExportPlanItem> plan);
            foreach (ExportPlanItem item in plan)
            {
                if (item.Action == ExportAction.SkipIncompatible) result.Warnings.Add($"{path}: シェイプキー「{item.Entry.Name}」は頂点数が合わないので飛ばしました。");
            }
            // ボーンウェイトの変更（ウェイトを体に寄せる）も当てる。レンダラーに無いボーンは、複製のレンダラーのボーンに足す
            Transform[] bones = ShapeKeyLibrary.ApplyWeightEdit(built, component.Data, renderer, result.Warnings);
            saveAsset?.Invoke(built);
            AssignMesh(renderer, built);
            if (bones != null && renderer is SkinnedMeshRenderer skinned) skinned.bones = bones;
            SetWeights(renderer, built, component, plan);
            return true;
        }

        /// <summary>
        /// 適用の元にするメッシュ。レンダラーが今使っているメッシュがあればそれ（ビルドの前の処理で変わっていても追従する）。
        /// ただし、書き出したメッシュ・プレビューの一時メッシュ・頂点数が違うものは使わず、データの元のメッシュにする。
        /// </summary>
        internal static Mesh ChooseSourceMesh(GP02ShapeKeys component, Renderer renderer)
        {
            Mesh current = ClothEditController.GetMesh(renderer);
            bool usable = current != null
                && current != component.ExportedMesh
                && (current.hideFlags & HideFlags.DontSave) == 0
                && current.vertexCount == component.Data.VertexCount;
            return usable ? current : ShapeKeyExporter.GetSourceMesh(component);
        }

        static void AssignMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        static void SetWeights(Renderer renderer, Mesh mesh, GP02ShapeKeys component, List<ExportPlanItem> plan)
        {
            if (!(renderer is SkinnedMeshRenderer skinned)) return;
            foreach (ExportPlanItem item in plan)
            {
                if (!item.ProducesBlendShape) continue;
                int index = mesh.GetBlendShapeIndex(item.BlendShapeName);
                if (index < 0) continue;
                skinned.SetBlendShapeWeight(index, component.InitialWeight == ShapeKeyInitialWeight.PreviewWeight ? item.Entry.PreviewWeight : 0f);
            }
        }
    }
}
