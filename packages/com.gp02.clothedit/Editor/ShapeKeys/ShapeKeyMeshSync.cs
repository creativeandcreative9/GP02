using System.Collections.Generic;
using System.Text;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーのデータと、レンダラーに今付いているメッシュが同じか（メッシュに反映済みか）の結果（<see cref="ShapeKeyMeshSync.Compare"/>）。
    /// 違いの数は、「メッシュに反映」で変わるものの数。
    /// </summary>
    public sealed class ShapeKeyMeshStatus
    {
        /// <summary>比べられなかった理由（データが無い・頂点数が違うなど）。比べられたら null。</summary>
        public string Problem;

        /// <summary>BlendShape の違い（加わる・置き換わる・無くなる）。</summary>
        public readonly List<ShapeKeyChange> Changes = new List<ShapeKeyChange>();

        /// <summary>素体の形が違う（「素体に焼き込む」の印の分が、メッシュに入っていない・残っている）。</summary>
        public bool BaseDiffers;

        /// <summary>データに記録したウェイトの変更（ウェイトを体に寄せる）が、メッシュに入っていない（元のメッシュのまま）。</summary>
        public bool WeightsMissing;

        /// <summary>違いの数（メッシュに反映すると変わるものの数）。</summary>
        public int ChangeCount => Changes.Count + (BaseDiffers ? 1 : 0) + (WeightsMissing ? 1 : 0);

        /// <summary>比べられて、違いが無い（メッシュに反映済み）。</summary>
        public bool InSync => Problem == null && ChangeCount == 0;

        /// <summary>違いの短い説明（例「加わる: GP02_2 ／ 置き換わる: GP02_1」）。違いが無ければ空。</summary>
        public string Describe()
        {
            var add = new List<string>();
            var update = new List<string>();
            var remove = new List<string>();
            foreach (ShapeKeyChange change in Changes)
            {
                switch (change.Kind)
                {
                    case ShapeKeyChangeKind.Add: add.Add(change.Name); break;
                    case ShapeKeyChangeKind.Update: update.Add(change.Name); break;
                    default: remove.Add(change.Name); break;
                }
            }
            var text = new StringBuilder();
            void Part(string label, List<string> names)
            {
                if (names.Count == 0) return;
                if (text.Length > 0) text.Append(L10n.Tr(" ／ ", " / "));
                text.Append(label).Append(Names(names));
            }
            Part(L10n.Tr("加わる: ", "added: "), add);
            Part(L10n.Tr("置き換わる: ", "replaced: "), update);
            Part(L10n.Tr("無くなる: ", "removed: "), remove);
            if (BaseDiffers)
            {
                if (text.Length > 0) text.Append(L10n.Tr(" ／ ", " / "));
                text.Append(L10n.Tr("素体の形（焼き込み）", "base shape (prebake)"));
            }
            if (WeightsMissing)
            {
                if (text.Length > 0) text.Append(L10n.Tr(" ／ ", " / "));
                text.Append(L10n.Tr("ウェイトの変更", "weight change"));
            }
            return text.ToString();
        }

        // 名前を3つまで並べる（多いときは「ほか N」）
        static string Names(List<string> names)
        {
            const int max = 3;
            var text = new StringBuilder();
            for (int i = 0; i < names.Count && i < max; i++)
            {
                if (i > 0) text.Append(L10n.Tr("、", ", "));
                text.Append(names[i]);
            }
            if (names.Count > max) text.Append(L10n.Tr($" ほか {names.Count - max}", $" and {names.Count - max} more"));
            return text.ToString();
        }
    }

    /// <summary>
    /// シェイプキーのデータと、レンダラーに今付いているメッシュを比べる（シェイプキーの一覧の「メッシュに反映済み」の表示）。
    /// 「メッシュに反映」（<see cref="ShapeKeyExporter.ExportToMesh(GP02ShapeKeys, ShapeKeyBuildOptions, string)"/>）で入るはずの BlendShape
    /// （<see cref="ShapeKeyExportPlan"/> のとおり）と、今のメッシュの BlendShape を、名前と各フレームの重み・差分で比べる（比べ方は Core の <see cref="ShapeKeySync"/>）。
    /// 元のメッシュにあって書き出しでもそのまま残る BlendShape は、名前だけを見る。素体に焼き込む印の分と、ウェイトの変更も見る。
    /// 布の編集の「シェイプキーとして保存」は、データとメッシュの両方に入れるので、保存した直後は同じになる。
    /// 何も書き換えない。毎フレームは呼ばない（一覧は、データかメッシュが変わったときだけ比べ直す）。
    /// </summary>
    public static class ShapeKeyMeshSync
    {
        /// <summary>component のデータを、renderer に今付いているメッシュ（プレビュー中なら、プレビューの前のメッシュ）と比べる。</summary>
        public static ShapeKeyMeshStatus Compare(GP02ShapeKeys component, Renderer renderer, ShapeKeyBuildOptions options)
        {
            var status = new ShapeKeyMeshStatus();
            string problem = ShapeKeyExporter.GetProblem(component);
            if (problem != null)
            {
                status.Problem = problem;
                return status;
            }
            Mesh current = renderer != null ? ShapeKeyPreview.GetOriginalMesh(renderer) ?? ClothEditController.GetMesh(renderer) : null;
            Mesh source = ShapeKeyExporter.GetSourceMesh(component);
            if (current == null)
            {
                status.Problem = L10n.Tr("レンダラーのメッシュが None です。", "The renderer's mesh is None.");
                return status;
            }
            if (current.vertexCount != source.vertexCount)
            {
                status.Problem = L10n.Tr("レンダラーのメッシュの頂点数が、データと違います。", "The renderer's mesh has a different vertex count from the data.");
                return status;
            }
            List<ExportPlanItem> plan = ShapeKeyExportPlan.Create(source, component.Data, options ?? ShapeKeyBuildOptions.FromComponent(component));
            string planProblem = ShapeKeyExportPlan.FirstProblem(plan);
            if (planProblem != null)
            {
                status.Problem = planProblem;
                return status;
            }

            int n = source.vertexCount;
            // 許容誤差はメッシュの座標の単位で決まるので、メッシュの大きさに合わせる（メートルのメッシュなら 0.01mm）
            float tolerance = ShapeKeySync.DefaultTolerance * Mathf.Max(1f, source.bounds.size.magnitude);
            var expected = new List<NamedShape>();
            var bakeEntries = new List<ShapeKeyEntry>();
            var bakeWeights = new List<float>();
            foreach (ExportPlanItem item in plan)
            {
                if (item.Action == ExportAction.Prebake)
                {
                    bakeEntries.Add(item.Entry);
                    bakeWeights.Add(Mathf.Clamp(item.Entry.PrebakeWeight, 0f, ShapeKeyMath.FullWeight));
                    continue;
                }
                if (!item.ProducesBlendShape) continue;
                // 今のメッシュに無い名前は「加わる」だけなので、差分を写さない
                bool present = current.GetBlendShapeIndex(item.BlendShapeName) >= 0;
                expected.Add(new NamedShape(item.BlendShapeName, present ? ToFrames(item.Entry) : new ShapeFrame[0]));
            }
            var keep = new List<string>();
            for (int i = 0; i < source.blendShapeCount; i++) keep.Add(source.GetBlendShapeName(i));
            var actual = new List<string>();
            for (int i = 0; i < current.blendShapeCount; i++) actual.Add(current.GetBlendShapeName(i));
            var vertices = new Vector3[n];
            var normals = new Vector3[n];
            var tangents = new Vector3[n];
            status.Changes.AddRange(ShapeKeySync.Compare(expected, actual, name => ReadFrames(current, name, vertices, normals, tangents), keep, tolerance));

            // 素体の形: 元のメッシュに、焼き込む印の分を足したもの
            if (bakeEntries.Count > 0 || current != source) status.BaseDiffers = !SameBase(current, source, bakeEntries, bakeWeights, tolerance);
            // ウェイトの変更は、元のメッシュには入っていない（メッシュに反映すると当てる）
            status.WeightsMissing = component.Data.HasWeightEdit && current == source && renderer is SkinnedMeshRenderer;
            return status;
        }

        static ShapeFrame[] ToFrames(ShapeKeyEntry entry)
        {
            var frames = new ShapeFrame[entry.Frames.Count];
            for (int f = 0; f < frames.Length; f++)
            {
                ShapeKeyFrame frame = entry.Frames[f];
                frames[f] = new ShapeFrame(frame.Weight, ToNumerics(frame.DeltaVertices));
            }
            return frames;
        }

        static NVector3[] ToNumerics(Vector3[] source)
        {
            if (source == null) return new NVector3[0];
            var result = new NVector3[source.Length];
            for (int i = 0; i < source.Length; i++) result[i] = new NVector3(source[i].x, source[i].y, source[i].z);
            return result;
        }

        static IReadOnlyList<ShapeFrame> ReadFrames(Mesh mesh, string name, Vector3[] vertices, Vector3[] normals, Vector3[] tangents)
        {
            int index = mesh.GetBlendShapeIndex(name);
            if (index < 0) return new ShapeFrame[0];
            int count = mesh.GetBlendShapeFrameCount(index);
            var frames = new ShapeFrame[count];
            for (int f = 0; f < count; f++)
            {
                mesh.GetBlendShapeFrameVertices(index, f, vertices, normals, tangents);
                frames[f] = new ShapeFrame(mesh.GetBlendShapeFrameWeight(index, f), ToNumerics(vertices));
            }
            return frames;
        }

        // 今のメッシュの素体の頂点が、元のメッシュに焼き込む印の分を足したものと同じか
        static bool SameBase(Mesh current, Mesh source, List<ShapeKeyEntry> bakeEntries, List<float> bakeWeights, float tolerance)
        {
            Vector3[] expected = source.vertices;
            for (int e = 0; e < bakeEntries.Count; e++)
            {
                Vector3[] delta = ShapeKeyLibrary.EvaluateEntry(bakeEntries[e], bakeWeights[e], expected.Length).DeltaVertices;
                for (int v = 0; v < expected.Length && v < delta.Length; v++) expected[v] += delta[v];
            }
            return ShapeKeySync.SameDeltas(ToNumerics(current.vertices), ToNumerics(expected), tolerance);
        }
    }
}
