using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 一覧の絞り込み・並べ替え・ソロと、選んだシェイプキーの詳細（素体への焼き込みの印、in-between のフレーム、前の版）
    public sealed partial class ShapeKeyListView
    {
        static readonly string[] SortLabels = { "データの順", "名前", "作った順" };
        static readonly string[] SortLabelsEn = { "Data order", "Name", "Created" };

        Vector2 _frameScroll;

        // ------------------------------------------------------------------
        // 絞り込み・並べ替え・ソロ
        // ------------------------------------------------------------------

        /// <summary>ドラッグ・▲▼での並べ替えができるか（検索や並べ替えで、表示がデータの並びと違うときはできない）。</summary>
        internal bool IsReorderAllowed => string.IsNullOrWhiteSpace(_settings.FilterText) && _settings.Sort == ShapeKeyListSort.DataOrder;

        internal bool IsSolo(ShapeKeyEntry entry) => _renderer != null && entry != null && ShapeKeyPreview.GetSolo(_renderer) == entry;

        /// <summary>「これだけ表示」: entry だけをシーンに見せる。null ならやめる。</summary>
        internal void SetSolo(ShapeKeyEntry entry)
        {
            if (_renderer == null || Data == null) return;
            if (!ShapeKeyPreview.SetSolo(_renderer, Data, entry, out string error)) _previewError = error;
            else _previewError = null;
            Repaint();
        }

        void DrawFilterBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                GUILayout.Label(new GUIContent(L10n.Tr("検索", "Search"), L10n.Tr("名前に含まれる文字で絞り込みます（空白で区切ると、すべて含むものだけ）", "Filter by a part of the name (separate words by spaces to require all)")), GUILayout.Width(48f));
                string text = EditorGUILayout.TextField(_settings.FilterText ?? "");
                if (!string.IsNullOrEmpty(text) && GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f))) text = "";
                string[] labels = L10n.Language == UiLanguage.English ? SortLabelsEn : SortLabels;
                var sort = (ShapeKeyListSort)EditorGUILayout.Popup((int)_settings.Sort, labels, GUILayout.Width(88f));
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.FilterText = text;
                    _settings.Sort = sort;
                    _settings.Save();
                    ReloadTree();
                }
                if (_renderer != null && ShapeKeyPreview.GetSolo(_renderer) != null && GUILayout.Button(
                    new GUIContent(L10n.Tr("ソロ解除", "End solo"), L10n.Tr("「これだけ表示」をやめて、重みどおりのプレビューに戻します", "End the solo and go back to the preview by weights")), GUILayout.Width(64f)))
                {
                    SetSolo(null);
                }
            }

            if (_settings.Sort != ShapeKeyListSort.DataOrder)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(L10n.Tr("表示だけ並べ替えています（データの並びは変わりません）", "Only the display is sorted (the data order is unchanged)"), EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("この並びで確定", "Apply this order"),
                        L10n.Tr("今の並べ替えを、データの並びにします（メッシュに入れる BlendShape の順番も変わります）", "Make this order the data order (the BlendShape order in the mesh changes too)")), EditorStyles.miniButton, GUILayout.Width(110f)))
                    {
                        Run(() =>
                        {
                            ShapeKeyListOperations.SortEntries(Data, _settings.Sort);
                            _settings.Sort = ShapeKeyListSort.DataOrder;
                            _settings.Save();
                            ReloadTree();
                        });
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // 選んだシェイプキーの詳細
        // ------------------------------------------------------------------

        void DrawDetails(GP02ShapeKeyData data)
        {
            _settings.ShowDetails = EditorGUILayout.Foldout(_settings.ShowDetails, L10n.Tr("選んだシェイプキーの詳細（焼き込み・フレーム・前の版）", "Details of the selection (prebake, frames, versions)"), true);
            if (!_settings.ShowDetails) return;

            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            if (selected.Count == 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr("シェイプキーを選ぶと、素体への焼き込みの印・in-between のフレーム・前の版を扱えます。", "Select a shape key to handle its prebake mark, in-between frames and older versions."), MessageType.None);
                return;
            }

            DrawPrebake(data, selected);
            if (selected.Count != 1) return;
            DrawFrames(data, selected[0]);
            DrawVersions(data, selected[0]);
        }

        void DrawPrebake(GP02ShapeKeyData data, List<ShapeKeyEntry> selected)
        {
            int marked = 0;
            foreach (ShapeKeyEntry e in selected)
            {
                if (e.Prebake) marked++;
            }
            EditorGUI.showMixedValue = marked > 0 && marked < selected.Count;
            EditorGUI.BeginChangeCheck();
            bool prebake = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("素体に焼き込む（BlendShape にしない）", "Bake into the base (no BlendShape)"),
                L10n.Tr("メッシュへの反映・ビルド時の適用で、このシェイプキーを BlendShape にせず、先に指定の重みで素体の形に焼き込みます。印を外して反映し直せば素体は元に戻ります（一覧では青い背景）",
                    "On export and on build, this shape key is not made a BlendShape: it is baked into the base at the given weight first. Remove the mark and export again to get the base back (blue in the list)")),
                marked == selected.Count && marked > 0);
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUI.showMixedValue = false;
            if (changed) SetPrebake(selected, prebake);

            if (selected.Count == 1 && selected[0].Prebake)
            {
                EditorGUI.BeginChangeCheck();
                float weight = EditorGUILayout.Slider(new GUIContent(L10n.Tr("焼き込む重み", "Bake weight")), selected[0].PrebakeWeight, 0f, 100f);
                if (EditorGUI.EndChangeCheck()) Run(() => ShapeKeyListOperations.SetPrebakeWeight(data, selected[0], weight));
            }
        }

        internal void SetPrebake(IReadOnlyList<ShapeKeyEntry> entries, bool prebake)
        {
            Run(() =>
            {
                ShapeKeyListOperations.SetPrebake(Data, entries, prebake);
                ReloadTree();
                Repaint();
            });
        }

        void DrawFrames(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            EditorGUILayout.LabelField(L10n.Tr($"フレーム（{entry.Frames.Count} 個）", $"Frames ({entry.Frames.Count})"), EditorStyles.boldLabel);

            // 重みを 0〜100 で動かして、補間の形をシーンで確かめる（このシェイプキーだけを見せる）
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                float weight = EditorGUILayout.Slider(new GUIContent(L10n.Tr("補間のプレビュー", "Interpolation preview"),
                    L10n.Tr("重みを 0〜100 で動かして、フレームの間の補間をシーンで確かめます。このシェイプキーだけを見せます", "Move the weight 0-100 to check the interpolation between frames in the scene. Only this shape key is shown")),
                    entry.PreviewWeight, 0f, 100f);
                if (EditorGUI.EndChangeCheck())
                {
                    ShapeKeyListOperations.SetPreviewWeight(data, entry, weight);
                    if (_renderer != null)
                    {
                        if (!ShapeKeyPreview.SetSolo(_renderer, data, entry, out string error)) _previewError = error;
                        else _previewError = null;
                    }
                }
            }

            if (entry.Frames.Count == 0) return;
            var frameIndices = new List<int>();
            for (int i = 0; i < entry.Frames.Count; i++) frameIndices.Add(i);
            // MaxHeight だと中身より低く切られることがあるので、行数から高さを決める
            float rowHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing + 2f;
            using (var scroll = new EditorGUILayout.ScrollViewScope(_frameScroll, GUILayout.Height(Mathf.Min(entry.Frames.Count, 5) * rowHeight + 6f)))
            {
                _frameScroll = scroll.scrollPosition;
                foreach (int i in frameIndices)
                {
                    ShapeKeyFrame frame = entry.Frames[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"#{i + 1}", GUILayout.Width(26f));
                        GUILayout.Label(L10n.Tr("重み", "Weight"), GUILayout.Width(44f));
                        EditorGUI.BeginChangeCheck();
                        float newWeight = EditorGUILayout.DelayedFloatField(frame.Weight, GUILayout.MinWidth(40f));
                        if (EditorGUI.EndChangeCheck()) ChangeFrameWeight(entry, i, newWeight);
                        if (GUILayout.Button(new GUIContent(L10n.Tr("見る", "View"), L10n.Tr("このフレームの重みでシーンに見せます", "Preview at this frame's weight")), EditorStyles.miniButton, GUILayout.Width(46f)))
                        {
                            ShapeKeyListOperations.SetPreviewWeight(data, entry, frame.Weight);
                            if (_renderer != null) ShapeKeyPreview.SetSolo(_renderer, data, entry, out _);
                        }
                        using (new EditorGUI.DisabledScope(entry.Frames.Count <= 1))
                        {
                            if (GUILayout.Button(new GUIContent(L10n.Tr("削除", "Delete"), L10n.Tr("このフレームを消します（最後の 1 つは消せません）", "Delete this frame (the last one cannot be deleted)")), EditorStyles.miniButton, GUILayout.Width(54f)))
                            {
                                DeleteFrame(entry, i);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }
            }
        }

        void ChangeFrameWeight(ShapeKeyEntry entry, int index, float weight)
        {
            Run(() =>
            {
                if (!ShapeKeyListOperations.SetFrameWeight(Data, entry, index, weight))
                {
                    Show(L10n.Tr("他のフレームと同じ重みにはできません。", "The weight cannot be the same as another frame's."), MessageType.Warning);
                    return;
                }
                AfterChange();
            });
        }

        void DeleteFrame(ShapeKeyEntry entry, int index)
        {
            Run(() =>
            {
                if (ShapeKeyListOperations.DeleteFrame(Data, entry, index)) Show(L10n.Tr("フレームを消しました。", "Deleted the frame."), MessageType.Info);
                AfterChange();
            });
        }

        void DrawVersions(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            EditorGUILayout.LabelField(L10n.Tr($"前の版（{entry.History.Count} / {ShapeKeyEntry.MaxHistory}）", $"Older versions ({entry.History.Count} / {ShapeKeyEntry.MaxHistory})"), EditorStyles.boldLabel);
            if (entry.History.Count == 0)
            {
                EditorGUILayout.LabelField(L10n.Tr("上書き保存すると、前の形がここに残ります。", "Older shapes are kept here when you overwrite."), EditorStyles.miniLabel);
                return;
            }
            // 新しいものを上に出す
            for (int i = entry.History.Count - 1; i >= 0; i--)
            {
                int index = i;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(ShapeKeyHistory.Describe(entry.History[i]), EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("戻す", "Restore"), L10n.Tr("この版に戻します。今の形も履歴に残り、Ctrl+Z でも戻せます", "Go back to this version. The current shape stays in the history and Ctrl+Z also undoes it")),
                        EditorStyles.miniButton, GUILayout.Width(60f)))
                    {
                        RestoreVersion(entry, index);
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        void RestoreVersion(ShapeKeyEntry entry, int index)
        {
            Run(() =>
            {
                ShapeKeyListOperations.RestoreVersion(Data, entry, index);
                AfterChange();
                Show(L10n.Tr($"「{entry.Name}」を以前の版に戻しました。", $"Restored an older version of \"{entry.Name}\"."), MessageType.Info);
            });
        }

        // 右クリックメニューの「以前の版に戻す」
        void AddVersionMenu(GenericMenu menu, List<ShapeKeyEntry> selected)
        {
            string label = L10n.Tr("以前の版に戻す", "Restore older version");
            if (selected.Count != 1 || selected[0].History.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent(label));
                return;
            }
            ShapeKeyEntry entry = selected[0];
            for (int i = entry.History.Count - 1; i >= 0; i--)
            {
                int index = i;
                menu.AddItem(new GUIContent(label + "/" + ShapeKeyHistory.Describe(entry.History[i]).Replace('/', '／')), false, () => RestoreVersion(entry, index));
            }
        }
    }
}
