using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    public enum TargetWarningKind
    {
        /// <summary>編集を始められない（メッシュが無い、三角形が無い、など）。</summary>
        CannotStart,

        /// <summary>プレハブのアセットそのもの。</summary>
        PrefabAsset,

        /// <summary>MeshFilter のメッシュにはシェイプキーを表示する仕組みが無い。</summary>
        MeshRenderer,

        /// <summary>Read/Write が無効。</summary>
        Unreadable,

        /// <summary>頂点が多い（開始に時間がかかる）。</summary>
        ManyVertices,

        /// <summary>頂点が非常に多い（重くなる）。</summary>
        VeryManyVertices,
    }

    /// <summary>対象に対する、ウィンドウに出す注意。</summary>
    public readonly struct TargetWarning
    {
        public readonly TargetWarningKind Kind;
        public readonly MessageType Severity;
        public readonly string Message;

        /// <summary>ボタンで自動で直せるか（<see cref="ClothEditWarnings.TryEnableReadWrite"/>）。</summary>
        public readonly bool CanAutoFix;

        public TargetWarning(TargetWarningKind kind, MessageType severity, string message, bool canAutoFix = false)
        {
            Kind = kind;
            Severity = severity;
            Message = message;
            CanAutoFix = canAutoFix;
        }
    }

    /// <summary>編集を始める前に、対象の問題を調べる。画面に依存しないので、テストで確かめられる。</summary>
    public static class ClothEditWarnings
    {
        /// <summary>頂点がこれ以上だと、編集の開始に数秒かかる。</summary>
        public const int ManyVertices = 30000;

        /// <summary>頂点がこれ以上だと、重くなる。</summary>
        public const int VeryManyVertices = 100000;

        /// <summary>対象の注意を result に入れる（先に空にする）。対象が null なら何も入れない（ウィンドウが案内を出す）。</summary>
        public static void Collect(Renderer target, List<TargetWarning> result, int manyVertices = ManyVertices, int veryManyVertices = VeryManyVertices)
        {
            result.Clear();
            if (target == null) return;

            string problem = ClothEditController.GetStartProblem(target);
            if (problem != null)
            {
                result.Add(new TargetWarning(TargetWarningKind.CannotStart, MessageType.Warning, problem));
                return;
            }

            if (PrefabUtility.IsPartOfPrefabAsset(target))
            {
                result.Add(new TargetWarning(TargetWarningKind.PrefabAsset, MessageType.Warning,
                    L10n.Tr("プレハブのアセットそのものは編集できません。シーンに置いたものを選んでください。",
                        "A prefab asset itself cannot be edited. Select the instance placed in the scene.")));
            }

            if (target is MeshRenderer)
            {
                result.Add(new TargetWarning(TargetWarningKind.MeshRenderer, MessageType.Info,
                    L10n.Tr("MeshFilter のメッシュにはシェイプキーを表示する仕組みがありません。保存はできますが、見た目は変わりません。",
                        "A MeshFilter mesh cannot display shape keys. You can save, but the appearance will not change.")));
            }

            Mesh mesh = ClothEditController.GetMesh(target);
            if (mesh != null && !mesh.isReadable)
            {
                result.Add(new TargetWarning(TargetWarningKind.Unreadable, MessageType.Warning,
                    L10n.Tr("このメッシュは Read/Write が無効です。エディタ上では編集できます。メッシュを読めない他のツールと組み合わせるなら、有効にしてください（ボタンで直せます）。",
                        "This mesh has Read/Write disabled. You can edit it in the editor. Enable it if you combine this with other tools that need to read the mesh (the button fixes it)."),
                    canAutoFix: true));
            }

            int vertices = mesh != null ? mesh.vertexCount : 0;
            if (vertices >= veryManyVertices)
            {
                result.Add(new TargetWarning(TargetWarningKind.VeryManyVertices, MessageType.Warning,
                    L10n.Tr($"頂点が非常に多いメッシュです（{vertices:N0}）。編集の開始に10秒以上かかり、操作が重くなることがあります。Burst を有効にするか、布の部分だけを別のメッシュに分けてください。",
                        $"This mesh has a very large number of vertices ({vertices:N0}). Starting may take over 10 seconds and editing may feel heavy. Enable Burst, or split the cloth into a separate mesh.")));
            }
            else if (vertices >= manyVertices)
            {
                result.Add(new TargetWarning(TargetWarningKind.ManyVertices, MessageType.Info,
                    L10n.Tr($"頂点が多いメッシュです（{vertices:N0}）。編集の開始に数秒かかります。Burst が有効だと操作が軽くなります。",
                        $"This mesh has many vertices ({vertices:N0}). Starting takes a few seconds. Editing is lighter with Burst enabled.")));
            }
        }

        /// <summary>
        /// Read/Write が無効なメッシュを、モデルの Import Settings で有効にする。
        /// モデルファイル（FBX など）から読み込まれたメッシュだけ直せる。直せたら true。message に結果の案内を入れる。
        /// </summary>
        public static bool TryEnableReadWrite(Renderer target, out string message)
        {
            Mesh mesh = ClothEditController.GetMesh(target);
            if (mesh == null)
            {
                message = L10n.Tr("メッシュがありません。", "There is no mesh.");
                return false;
            }
            if (mesh.isReadable)
            {
                message = L10n.Tr("すでに Read/Write は有効です。", "Read/Write is already enabled.");
                return true;
            }

            string path = AssetDatabase.GetAssetPath(mesh);
            var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                message = L10n.Tr("このメッシュはモデルファイル（FBX など）から読み込まれたものではないので、自動では直せません。モデルの Import Settings で Read/Write を有効にしてください。",
                    "This mesh was not imported from a model file (such as FBX), so it cannot be fixed automatically. Enable Read/Write in the model's Import Settings.");
                return false;
            }

            importer.isReadable = true;
            importer.SaveAndReimport();
            message = L10n.Tr($"「{path}」の Read/Write を有効にしました。", $"Enabled Read/Write for \"{path}\".");
            return true;
        }
    }
}
