using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>1つのバックアップ。</summary>
    public sealed class ShapeKeyBackupInfo
    {
        /// <summary>アセットのパス。</summary>
        public string Path;

        /// <summary>取った日時。</summary>
        public DateTime Time;

        /// <summary>何の操作の前に取ったか（英語の短い名前）。</summary>
        public string Reason;

        public override string ToString() => $"{Time.ToString(ShapeKeyHistory.TimeFormat, CultureInfo.InvariantCulture)}  {Reason}";
    }

    /// <summary>
    /// データを変える操作（削除・合成・分割・上書き・履歴から戻すなど）の前に、データのアセットの写しを <see cref="Folder"/> に残す。
    /// データごとに最新 <see cref="MaxPerData"/> 個だけ残し、古いものは消す。操作を間違えたときに「バックアップから戻す」で戻せる。
    /// Undo とは別の安全網（Unity を閉じたあとや、Undo の履歴が消えたあとでも戻せる）。
    /// アセットとして保存されていないデータ（メモリ上だけのもの）は対象にしない。
    /// </summary>
    public static class ShapeKeyBackup
    {
        public const string DefaultFolder = "Assets/GP02/Backups";
        public const int MaxPerData = 5;

        const string NameFormat = "yyyyMMdd_HHmmss_fff";

        /// <summary>バックアップを置くフォルダ。テストでは一時フォルダに変える。</summary>
        public static string Folder { get; set; } = DefaultFolder;

        /// <summary>false なら、バックアップを取らない（大量の操作を続けて行うテストなどで使う）。</summary>
        public static bool Enabled { get; set; } = true;

        static DateTime _lastTime;

        /// <summary>
        /// データの今の状態（メモリ上のもの。まだ保存していない変更も含む）を、バックアップとして保存する。
        /// 取れたらそのパス。データがアセットでない、無効にしてあるときは null。
        /// </summary>
        public static string Create(GP02ShapeKeyData data, string reason)
        {
            if (!Enabled || data == null) return null;
            string dataPath = AssetDatabase.GetAssetPath(data);
            if (string.IsNullOrEmpty(dataPath)) return null;

            string folder = FolderFor(data, dataPath);
            EnsureFolder(folder);

            // 同じミリ秒に 2 回取っても名前が重ならないよう、時刻は必ず進める
            DateTime now = DateTime.Now;
            if (now <= _lastTime) now = _lastTime.AddMilliseconds(1);
            _lastTime = now;
            string path = $"{folder}/{now.ToString(NameFormat, CultureInfo.InvariantCulture)}_{Slug(reason)}.asset";

            GP02ShapeKeyData copy = Object.Instantiate(data);
            copy.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(copy, path);
            Prune(folder);
            return path;
        }

        /// <summary>データのバックアップの一覧（新しい順）。</summary>
        public static List<ShapeKeyBackupInfo> List(GP02ShapeKeyData data)
        {
            var result = new List<ShapeKeyBackupInfo>();
            if (data == null) return result;
            string dataPath = AssetDatabase.GetAssetPath(data);
            if (string.IsNullOrEmpty(dataPath)) return result;
            string folder = FolderFor(data, dataPath);
            if (!AssetDatabase.IsValidFolder(folder)) return result;

            foreach (string guid in AssetDatabase.FindAssets("t:GP02ShapeKeyData", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = Path.GetFileNameWithoutExtension(path);
                if (file.Length < NameFormat.Length) continue;
                if (!DateTime.TryParseExact(file.Substring(0, NameFormat.Length), NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)) continue;
                string reason = file.Length > NameFormat.Length + 1 ? file.Substring(NameFormat.Length + 1) : "";
                result.Add(new ShapeKeyBackupInfo { Path = path, Time = time, Reason = reason });
            }
            result.Sort((a, b) => b.Time.CompareTo(a.Time));
            return result;
        }

        /// <summary>
        /// バックアップの状態にデータを戻す（Undo できる）。戻す前の状態も、新しいバックアップとして残す。
        /// 戻せたら true。元のメッシュなどの参照も、バックアップのものになる。
        /// </summary>
        public static bool Restore(GP02ShapeKeyData data, string backupPath)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var backup = AssetDatabase.LoadAssetAtPath<GP02ShapeKeyData>(backupPath);
            if (backup == null) return false;

            // 戻す前の状態のバックアップで上限を超えて、戻す元が消されることがあるので、中身は先に写し取る
            Mesh sourceMesh = backup.SourceMesh;
            int vertexCount = backup.VertexCount;
            var groups = new List<string>(backup.Groups);
            var entries = new List<ShapeKeyEntry>(backup.Entries.Count);
            foreach (ShapeKeyEntry entry in backup.Entries)
            {
                if (entry != null) entries.Add(ShapeKeyLibrary.CloneEntry(entry));
            }

            Create(data, "before_restore");
            Undo.RegisterCompleteObjectUndo(data, "GP02 " + L10n.Tr("バックアップから戻す", "Restore from backup"));
            data.SourceMesh = sourceMesh;
            data.VertexCount = vertexCount;
            data.Groups = groups;
            data.Entries = entries;
            EditorUtility.SetDirty(data);
            return true;
        }

        // データごとのフォルダ。データの名前が同じでも混ざらないよう、アセットの GUID の頭を付ける
        static string FolderFor(GP02ShapeKeyData data, string dataPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(dataPath);
            string shortGuid = guid.Length >= 8 ? guid.Substring(0, 8) : guid;
            return $"{(Folder ?? DefaultFolder).TrimEnd('/')}/{Sanitize(data.name)}_{shortGuid}";
        }

        static void Prune(string folder)
        {
            var files = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:GP02ShapeKeyData", new[] { folder })) files.Add(AssetDatabase.GUIDToAssetPath(guid));
            // 名前の先頭が日時なので、名前順が古い順になる
            files.Sort(StringComparer.Ordinal);
            for (int i = 0; i < files.Count - MaxPerData; i++) AssetDatabase.DeleteAsset(files[i]);
        }

        // ファイル名に使える英数字と _ だけにする（"Split left/right" → "Split_left_right"）
        static string Slug(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "backup";
            var chars = new List<char>();
            foreach (char c in reason)
            {
                bool word = c < 128 && char.IsLetterOrDigit(c);
                if (word) chars.Add(c);
                else if (chars.Count > 0 && chars[chars.Count - 1] != '_') chars.Add('_');
            }
            while (chars.Count > 0 && chars[chars.Count - 1] == '_') chars.RemoveAt(chars.Count - 1);
            return chars.Count == 0 ? "backup" : new string(chars.ToArray());
        }

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Data";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
