using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 説明書（パッケージの Documentation~ フォルダ）の場所の探し方と、開く処理。
    /// Documentation~ はチルダ付きで Unity に読み込まれない（アセットにならない）ので、ファイルの場所をたどって開く。
    /// </summary>
    public static class ClothEditDocs
    {
        public const string FolderName = "Documentation~";
        public const string JapaneseFileName = "20261011_使い方.md";
        public const string EnglishFileName = "20261011_Manual_en.md";

        /// <summary>言語に合わせた説明書のファイル名。</summary>
        public static string FileNameFor(UiLanguage language) => language == UiLanguage.English ? EnglishFileName : JapaneseFileName;

        /// <summary>
        /// パッケージのフォルダ（package.json があるフォルダ）。パッケージとして入っていればその場所、
        /// Assets の下に展開されていれば、このコードの 1 つ上のフォルダ。分からなければ null。
        /// </summary>
        public static string FindPackageRoot()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(ClothEditDocs).Assembly);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath) && Directory.Exists(package.resolvedPath)) return package.resolvedPath;

            // .unitypackage で Assets の下に入れた場合: このスクリプトのフォルダ（Editor）の親がパッケージの根
            foreach (string guid in AssetDatabase.FindAssets("ClothEditDocs t:MonoScript"))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith("/ClothEditDocs.cs")) continue;
                string editorFolder = Path.GetDirectoryName(Path.GetFullPath(assetPath));
                DirectoryInfo parent = editorFolder != null ? Directory.GetParent(editorFolder) : null;
                if (parent != null) return parent.FullName;
            }
            return null;
        }

        /// <summary>説明書のファイルの場所。見つからなければ null。</summary>
        public static string FindDocument(UiLanguage language)
        {
            string root = FindPackageRoot();
            if (root == null) return null;
            string path = Path.Combine(root, FolderName, FileNameFor(language));
            return File.Exists(path) ? path : null;
        }

        /// <summary>今の言語の説明書を開く。開けなければ、理由を利用者向けの文章にして false を返す。</summary>
        public static bool TryOpen(out string message)
        {
            string path = FindDocument(L10n.Language);
            if (path == null)
            {
                message = L10n.Tr(
                    $"説明書（{FileNameFor(L10n.Language)}）が見つかりません。配布物の「{FolderName}」フォルダにあります（Unity には読み込まれないフォルダです）。",
                    $"The manual ({FileNameFor(L10n.Language)}) was not found. It is in the \"{FolderName}\" folder of the distribution (Unity does not import that folder).");
                return false;
            }
            EditorUtility.OpenWithDefaultApp(path);
            message = path;
            return true;
        }
    }
}
