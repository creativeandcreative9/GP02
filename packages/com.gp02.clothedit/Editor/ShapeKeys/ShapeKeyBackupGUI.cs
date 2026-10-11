using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 自動バックアップ（<see cref="ShapeKeyBackup"/>）の一覧と「バックアップから戻す」の画面。
    /// シェイプキーの一覧と、データのアセットのインスペクター（<see cref="GP02ShapeKeyDataEditor"/>）の両方で使う。
    /// </summary>
    public static class ShapeKeyBackupGUI
    {
        static GP02ShapeKeyData _cacheData;
        static List<ShapeKeyBackupInfo> _cacheList;
        static double _cacheTime;

        /// <summary>操作の名前（英語のファイル名の部分）を、画面に出す言葉にする。知らないものはそのまま。</summary>
        public static string DescribeReason(string reason)
        {
            switch (reason)
            {
                case "Delete": return L10n.Tr("削除の前", "Before delete");
                case "Merge": return L10n.Tr("合成の前", "Before merge");
                case "Split_left_right": return L10n.Tr("左右に分割の前", "Before split L/R");
                case "Split_by_selection": return L10n.Tr("選んだ点で分割の前", "Before split by selection");
                case "Scale": return L10n.Tr("量の拡大縮小の前", "Before scale");
                case "Recompute_normals": return L10n.Tr("法線の計算し直しの前", "Before recompute normals");
                case "Flip": return L10n.Tr("左右反転の前", "Before flip");
                case "Symmetrize": return L10n.Tr("左右対称の前", "Before symmetrize");
                case "Create_in_between": return L10n.Tr("in-between の作成の前", "Before creating in-between");
                case "Subtract": return L10n.Tr("差し引きの前", "Before subtract");
                case "Overwrite": return L10n.Tr("上書き保存の前", "Before overwrite");
                case "Delete_frame": return L10n.Tr("フレームの削除の前", "Before deleting a frame");
                case "Change_frame_weight": return L10n.Tr("フレームの重みの変更の前", "Before changing a frame weight");
                case "Restore_older_version": return L10n.Tr("以前の版に戻す前", "Before restoring an older version");
                case "before_restore": return L10n.Tr("バックアップから戻す前", "Before restoring a backup");
                case "Import_BlendShapes":
                case "Import_mesh":
                case "Import_animation_clip":
                case "Import_animation_clip_keyframes": return L10n.Tr("取り込み（上書き）の前", "Before import (overwrite)");
                case "Extract_the_selected_range":
                case "Remove_the_selected_range": return L10n.Tr("選んだ点の範囲の取り出し・削除の前", "Before range extract/remove");
                default: return reason;
            }
        }

        /// <summary>バックアップの一覧と、戻すボタンを描く。戻したあとに onRestored を呼ぶ。</summary>
        public static void Draw(GP02ShapeKeyData data, Action onRestored)
        {
            if (data == null) return;
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(data)))
            {
                EditorGUILayout.HelpBox(L10n.Tr("このデータはアセットとして保存されていないので、バックアップは取れません。", "This data is not saved as an asset, so backups cannot be made."), MessageType.Info);
                return;
            }

            // 一覧の取得はアセットの検索なので、描画のたびにはせず、1 秒ごと（と、戻したあと）にだけ行う
            if (_cacheData != data || _cacheList == null || EditorApplication.timeSinceStartup - _cacheTime > 1.0)
            {
                _cacheData = data;
                _cacheList = ShapeKeyBackup.List(data);
                _cacheTime = EditorApplication.timeSinceStartup;
            }
            List<ShapeKeyBackupInfo> backups = _cacheList;
            EditorGUILayout.LabelField(L10n.Tr(
                $"削除・合成・分割・上書き・履歴から戻すなどの前に、自動で最新 {ShapeKeyBackup.MaxPerData} 個を {ShapeKeyBackup.Folder} に残します。",
                $"Before delete, merge, split, overwrite, restoring from the history and so on, the latest {ShapeKeyBackup.MaxPerData} are saved automatically in {ShapeKeyBackup.Folder}."), EditorStyles.wordWrappedMiniLabel);
            if (backups.Count == 0)
            {
                EditorGUILayout.LabelField(L10n.Tr("まだバックアップはありません。", "There are no backups yet."), EditorStyles.miniLabel);
                return;
            }
            foreach (ShapeKeyBackupInfo backup in backups)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent($"{backup.Time:yyyy-MM-dd HH:mm:ss}  {DescribeReason(backup.Reason)}", backup.Path), EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("バックアップから戻す", "Restore from backup"), L10n.Tr("データ全体をこのバックアップの状態に戻します（戻す前の状態もバックアップに残り、Ctrl+Z でも戻せます）", "Restore the whole data to this backup (the current state is backed up first, and Ctrl+Z also undoes it)")),
                        EditorStyles.miniButton, GUILayout.Width(130f)))
                    {
                        if (EditorUtility.DisplayDialog("GP02", L10n.Tr(
                                $"データ「{data.name}」を {backup.Time:yyyy-MM-dd HH:mm:ss} の状態に戻します。よろしいですか？\n（戻す前の状態もバックアップに残ります）",
                                $"Restore the data \"{data.name}\" to the state of {backup.Time:yyyy-MM-dd HH:mm:ss}?\n(The current state is backed up first.)"),
                            L10n.Tr("戻す", "Restore"), L10n.Tr("やめる", "Cancel")))
                        {
                            if (ShapeKeyListOperations.RestoreFromBackup(data, backup.Path))
                            {
                                _cacheList = null;
                                onRestored?.Invoke();
                            }
                        }
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }
    }

    /// <summary>
    /// シェイプキーのデータ（<see cref="GP02ShapeKeyData"/> のアセット）のインスペクター。
    /// 大きな差分の配列を並べるデフォルトの表示は重いので出さず、概要と「バックアップから戻す」を出す。
    /// </summary>
    [CustomEditor(typeof(GP02ShapeKeyData))]
    public sealed class GP02ShapeKeyDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var data = (GP02ShapeKeyData)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(L10n.Tr("元のメッシュ", "Source mesh"), data.SourceMesh, typeof(Mesh), false);
                EditorGUILayout.IntField(L10n.Tr("頂点数", "Vertex count"), data.VertexCount);
                EditorGUILayout.IntField(L10n.Tr("シェイプキーの数", "Shape keys"), data.Entries.Count);
            }
            EditorGUILayout.HelpBox(L10n.Tr(
                "シェイプキーの編集は、オブジェクトの GP02 Shape Keys コンポーネント、または「Tools/GP02/シェイプキーの一覧」で行います。",
                "Edit shape keys with the GP02 Shape Keys component of the object, or \"Tools/GP02/Shape Key List\"."), MessageType.None);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(L10n.Tr("バックアップ", "Backups"), EditorStyles.boldLabel);
            ShapeKeyBackupGUI.Draw(data, () => Repaint());
        }
    }
}
