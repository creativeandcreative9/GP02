using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの一覧の画面（IMGUI）。「Tools/GP02/シェイプキーの一覧」のウィンドウと、<see cref="GP02ShapeKeys"/> のインスペクターの両方で使う。
    /// 一覧（TreeView）、選んだものへの操作、取り込み、書き出し、データの保護の表示までを1つにまとめている。
    /// データの書き換えは <see cref="ShapeKeyListOperations"/>、書き出しは <see cref="ShapeKeyExporter"/>、プレビューは <see cref="ShapeKeyPreview"/> に任せ、
    /// ここは呼び出しと表示だけを受け持つ。使い終わったら <see cref="Dispose"/> を呼ぶこと（プレビューを元に戻すため）。
    /// </summary>
    public sealed partial class ShapeKeyListView : IDisposable
    {
        static readonly string[] AxisLabels = { "X", "Y", "Z" };

        readonly ShapeKeyViewSettings _settings;
        readonly TreeViewState _treeState;
        ShapeKeyTreeView _tree;
        GP02ShapeKeys _component;
        Renderer _renderer;
        HashSet<string> _duplicateNames = new HashSet<string>();
        string _previewError;
        string _message;
        MessageType _messageType;
        UnityEngine.Object _importMeshAsset;
        AnimationClip _importClip;
        string _clothSaveName = "";
        bool _needsReload;
        List<ShapeKeyEntry> _pendingSelect;
        string _pendingRename;
        bool _disposed;

        /// <summary>画面の更新が必要になったときに呼ばれる（ウィンドウが Repaint するため）。</summary>
        public Action RepaintRequested;

        public ShapeKeyListView(TreeViewState treeState = null, ShapeKeyViewSettings settings = null)
        {
            _treeState = treeState ?? new TreeViewState();
            _settings = settings ?? ShapeKeyViewSettings.Load();
            _tree = new ShapeKeyTreeView(_treeState, this);
            Undo.undoRedoPerformed += OnUndoRedo;
            ShapeKeyPreview.Changed += OnPreviewChanged;
        }

        public TreeViewState TreeState => _treeState;
        internal ShapeKeyTreeView Tree => _tree;
        public ShapeKeyViewSettings Settings => _settings;
        public GP02ShapeKeys Component => _component;
        public Renderer Renderer => _renderer;
        public GP02ShapeKeyData Data => _component != null ? _component.Data : null;

        /// <summary>選んでいるシェイプキー（データの並びの順）。</summary>
        public List<ShapeKeyEntry> SelectedEntries => _tree.GetSelectedEntries();

        // ------------------------------------------------------------------
        // 対象
        // ------------------------------------------------------------------

        /// <summary>
        /// 対象を設定する。対象が変わるときは、前の対象のプレビューを止めて、プレビューの重みを 0 に戻す。
        /// component が null でも、renderer があれば「データを作成」を出せる。
        /// </summary>
        public void SetTarget(GP02ShapeKeys component, Renderer renderer)
        {
            if (component == _component && renderer == _renderer) return;
            StopPreview();
            _component = component;
            _renderer = renderer;
            _message = null;
            _previewError = null;
            _clothSaveName = "";
            _treeState.selectedIDs.Clear();
            ReloadTree();
            Repaint();
        }

        /// <summary>
        /// プレビューを止めてメッシュを元に戻し、プレビューの重みを 0 に戻す。
        /// 重みを残すと、次に開いたときにスライダーだけ動いていてメッシュは元のまま、という食い違いになるため。
        /// </summary>
        public void StopPreview()
        {
            if (_renderer != null) ShapeKeyPreview.End(_renderer);
            GP02ShapeKeyData data = Data;
            if (data != null && HasAnyWeight(data)) ShapeKeyListOperations.ResetPreviewWeights(data);
            _previewError = null;
        }

        static bool HasAnyWeight(GP02ShapeKeyData data)
        {
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null && e.PreviewWeight != 0f) return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Undo.undoRedoPerformed -= OnUndoRedo;
            ShapeKeyPreview.Changed -= OnPreviewChanged;
            StopPreview();
        }

        void Repaint() => RepaintRequested?.Invoke();

        // TreeView の描画の途中（行の描画・キー・ドロップ）から呼ばれる操作が、描画中の行を作り直すと壊れる。
        // そのため、作り直しと選択・名前の変更は次の OnGUI の最初に行う
        void ReloadTree()
        {
            _needsReload = true;
            Repaint();
        }

        void SelectLater(IEnumerable<ShapeKeyEntry> entries)
        {
            _pendingSelect = new List<ShapeKeyEntry>(entries);
            ReloadTree();
        }

        /// <summary>保留していた一覧の作り直しなどを、今行う（OnGUI の最初に自動で呼ばれる。テストからも使う）。</summary>
        internal void FlushTree()
        {
            if (!_needsReload && _pendingSelect == null && _pendingRename == null) return;
            _needsReload = false;
            _tree.Reload();
            if (_pendingSelect != null)
            {
                _tree.SelectEntries(_pendingSelect);
                _pendingSelect = null;
            }
            if (_pendingRename != null)
            {
                _tree.BeginRenameGroup(_pendingRename);
                _pendingRename = null;
            }
        }

        void OnPreviewChanged() => Repaint();

        void OnUndoRedo()
        {
            if (_disposed) return;
            ReloadTree();
            if (Data != null && _renderer != null && ShapeKeyPreview.IsActive(_renderer)) ApplyPreview();
            Repaint();
        }

        // ------------------------------------------------------------------
        // TreeView からの呼び出し
        // ------------------------------------------------------------------

        internal List<ShapeKeyEntry> GetEntriesInGroup(string group)
        {
            var result = new List<ShapeKeyEntry>();
            GP02ShapeKeyData data = Data;
            if (data == null) return result;
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e != null && e.Group == group) result.Add(e);
            }
            return result;
        }

        /// <summary>行に出す警告。無ければ null。isError が true なら赤、false なら黄。</summary>
        internal string GetProblem(ShapeKeyEntry entry, out bool isError)
        {
            isError = false;
            GP02ShapeKeyData data = Data;
            if (data == null || entry == null) return null;
            Mesh mesh = _renderer != null ? ClothEditController.GetMesh(_renderer) : null;
            int expected = mesh != null ? mesh.vertexCount : data.VertexCount;
            if (!ShapeKeyLibrary.IsEntryCompatible(entry, expected))
            {
                isError = true;
                return L10n.Tr(
                    $"頂点数が合いません。メッシュは {expected} 個ですが、このシェイプキーの差分は別の数です。別のメッシュ用のデータかもしれません。",
                    $"The vertex count does not match. The mesh has {expected}, but this shape key was made for a different mesh.");
            }
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                isError = true;
                return L10n.Tr("名前が空です。書き出せません。", "The name is empty, so it cannot be exported.");
            }
            if (_duplicateNames.Contains(entry.Name))
            {
                return L10n.Tr("同じ名前のシェイプキーが他にもあります。書き出すと後のもので上書きされます。", "Another shape key has the same name. The later one overwrites it on export.");
            }
            if (entry.Frames.Count == 0) return L10n.Tr("差分がありません。", "There are no deltas.");
            return null;
        }

        internal void OnSelectionChanged() => Repaint();

        internal void SetEnabled(IReadOnlyList<ShapeKeyEntry> entries, bool enabled)
        {
            ShapeKeyListOperations.SetEnabled(Data, entries, enabled);
            AfterChange();
        }

        internal void Rename(ShapeKeyEntry entry, string newName)
        {
            ShapeKeyListOperations.Rename(Data, entry, newName);
            ReloadTree();
        }

        internal void SetPreviewWeight(ShapeKeyEntry entry, float weight)
        {
            ShapeKeyListOperations.SetPreviewWeight(Data, entry, weight);
            ApplyPreview();
        }

        internal void SetPreviewWeights(IReadOnlyList<ShapeKeyEntry> entries, float weight)
        {
            foreach (ShapeKeyEntry e in entries) ShapeKeyListOperations.SetPreviewWeight(Data, e, weight);
            ApplyPreview();
        }

        internal void RenameGroup(string oldName, string newName)
        {
            if (!ShapeKeyListOperations.RenameGroup(Data, oldName, newName))
            {
                Show(L10n.Tr("同じ名前のグループがあるか、名前が空です。", "A group with that name already exists, or the name is empty."), MessageType.Warning);
            }
            ReloadTree();
        }

        internal void MoveEntriesTo(IReadOnlyList<ShapeKeyEntry> entries, int insertIndex, string group)
        {
            Run(() =>
            {
                ShapeKeyListOperations.MoveTo(Data, entries, insertIndex, group);
                AfterStructuralChange(entries);
            });
        }

        // ------------------------------------------------------------------
        // プレビュー
        // ------------------------------------------------------------------

        void ApplyPreview()
        {
            if (_renderer == null || Data == null) return;
            if (!ShapeKeyPreview.Apply(_renderer, Data, out string error)) _previewError = error;
            else _previewError = null;
        }

        // データの中身が変わったあとの更新（有効の切り替えなど）
        void AfterChange()
        {
            ReloadTree();
            if (_renderer != null && ShapeKeyPreview.IsActive(_renderer)) ApplyPreview();
            Repaint();
        }

        void AfterStructuralChange(IEnumerable<ShapeKeyEntry> toSelect)
        {
            SelectLater(toSelect);
            if (_renderer != null && ShapeKeyPreview.IsActive(_renderer)) ApplyPreview();
            Repaint();
        }

        // ------------------------------------------------------------------
        // メッセージと例外の受け止め
        // ------------------------------------------------------------------

        void Show(string message, MessageType type)
        {
            _message = message;
            _messageType = type;
            Repaint();
        }

        // 想定している失敗（頂点数が合わない、名前が空など）は、画面のメッセージにする
        void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                Show(ex.Message, MessageType.Error);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Show(L10n.Tr("操作に失敗しました。Console のエラーを確認してください。\n", "The operation failed. See the error in the Console.\n") + ex.Message, MessageType.Error);
            }
        }

        // ------------------------------------------------------------------
        // 描画
        // ------------------------------------------------------------------

        /// <summary>画面全体を描く。treeHeight は一覧の高さ（ピクセル）。</summary>
        public void OnGUI(float treeHeight = 240f)
        {
            // 「かんたん」「くわしく」の切り替え（布の編集ウィンドウと共通）。かんたん表示では、主な操作だけを出す
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                ClothEditView.DrawToggle(EditorStyles.miniButton);
            }
            if (_component == null && _renderer == null)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "シェイプキーを扱うオブジェクト（SkinnedMeshRenderer か MeshRenderer）を選んでください。",
                    "Select an object with a SkinnedMeshRenderer or MeshRenderer."), MessageType.Info);
                return;
            }

            if (!DrawTargetAndProtection()) return;
            GP02ShapeKeyData data = Data;

            UpdateDuplicateNames(data);
            FlushTree();
            _tree.ReloadIfStale();

            DrawMessage();
            if (ClothEditView.Shows(ClothEditUiItem.ShapeKeySearchSort)) DrawFilterBar();
            DrawToolbar();
            Rect treeRect = GUILayoutUtility.GetRect(0f, Mathf.Max(treeHeight, 80f), GUILayout.ExpandWidth(true));
            _tree.OnGUI(treeRect);
            DrawPreviewStatus(data);
            DrawClothEditStatus(data);
            EditorGUILayout.Space(2f);
            if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyDetails)) DrawDetails(data);
            DrawOperations(data);
            if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyImport)) DrawImport(data);
            if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyApplyMode)) ShapeKeyApplyModeGUI.Draw(_component, _renderer);
            DrawExport(data);
            if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyBackups)) DrawBackups(data);
        }

        void UpdateDuplicateNames(GP02ShapeKeyData data)
        {
            _duplicateNames.Clear();
            var seen = new HashSet<string>();
            foreach (ShapeKeyEntry e in data.Entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                if (!seen.Add(e.Name)) _duplicateNames.Add(e.Name);
            }
        }

        void DrawMessage()
        {
            if (string.IsNullOrEmpty(_message)) return;
            EditorGUILayout.HelpBox(_message, _messageType);
            Rect last = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && last.Contains(Event.current.mousePosition))
            {
                _message = null;
                Event.current.Use();
            }
        }

        // 対象・データ・メッシュの状態を確認して、問題があれば直し方つきで出す。一覧を出せるなら true
        bool DrawTargetAndProtection()
        {
            ShapeKeyProtection.Issue issues = ShapeKeyProtection.Diagnose(_component, _renderer);

            if ((issues & ShapeKeyProtection.Issue.NoRenderer) != 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "このオブジェクトに SkinnedMeshRenderer（または MeshFilter 付きの MeshRenderer）がありません。書き出しやプレビューはできません。",
                    "This object has no SkinnedMeshRenderer (or MeshRenderer with a MeshFilter). Export and preview are unavailable."), MessageType.Warning);
            }

            if (_component == null)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "このオブジェクトには、シェイプキーのデータがまだありません。",
                    "This object has no shape key data yet."), MessageType.Info);
                using (new EditorGUI.DisabledScope(_renderer == null || ClothEditController.GetMesh(_renderer) == null))
                {
                    if (GUILayout.Button(L10n.Tr("データを作成", "Create data")))
                    {
                        Run(() =>
                        {
                            GP02ShapeKeyData created = ShapeKeyLibrary.FindOrCreateData(_renderer);
                            if (created != null) SetTarget(_renderer.GetComponent<GP02ShapeKeys>(), _renderer);
                        });
                    }
                }
                return false;
            }

            // データの欄は、くわしく表示のときだけ（問題があるときは、下のメッセージと直すボタンを出す）
            if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyDataField)) DrawDataField();

            if ((issues & ShapeKeyProtection.Issue.DataMissing) != 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "シェイプキーのデータが見つかりません（削除された可能性があります）。上の「データ」に、元のデータを指定し直してください。",
                    "The shape key data is missing (it may have been deleted). Assign the data again in the Data field above."), MessageType.Error);
                if (_component.ExportedMesh != null || _component.SourceMesh != null)
                {
                    EditorGUILayout.HelpBox(L10n.Tr(
                        "書き出したメッシュはアセットとして残っているので、そのまま使えます。",
                        "The exported mesh is saved as an asset and can still be used."), MessageType.Info);
                }
                if (_renderer != null && ClothEditController.GetMesh(_renderer) != null && GUILayout.Button(L10n.Tr("今のメッシュで新しいデータを作成", "Create new data from the current mesh")))
                {
                    Run(() =>
                    {
                        GP02ShapeKeyData created = ShapeKeyLibrary.CreateData(_component.SourceMesh != null ? _component.SourceMesh : ClothEditController.GetMesh(_renderer));
                        ShapeKeyProtection.SetData(_component, created);
                        ReloadTree();
                    });
                }
                DrawRestoreButton(issues);
                return false;
            }

            if ((issues & ShapeKeyProtection.Issue.SourceMeshMissing) != 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "元のメッシュが見つかりません。左右の操作・取り込み・書き出しには、元のメッシュが必要です。",
                    "The source mesh is missing. It is needed for left/right operations, import and export."), MessageType.Error);
                if (GUILayout.Button(L10n.Tr("今のメッシュを元のメッシュにする", "Use the current mesh as the source mesh")))
                {
                    if (!ShapeKeyProtection.AdoptRendererMeshAsSource(_component, _renderer))
                    {
                        Show(L10n.Tr("今のメッシュの頂点数がデータと違うので、元のメッシュにできません。元のメッシュのアセットを「元のメッシュ」に指定してください。",
                            "The current mesh has a different vertex count from the data. Assign the original mesh asset as the source mesh."), MessageType.Warning);
                    }
                }
                DrawSourceMeshField();
            }

            if ((issues & ShapeKeyProtection.Issue.RendererMeshMissing) != 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "レンダラーのメッシュが None になっています。「元のメッシュに戻す」で直せます。",
                    "The renderer's mesh is None. Use \"Restore the original mesh\" to fix it."), MessageType.Error);
                DrawRestoreButton(issues);
            }
            else if ((issues & ShapeKeyProtection.Issue.VertexCountMismatch) != 0)
            {
                Mesh mesh = ClothEditController.GetMesh(_renderer);
                EditorGUILayout.HelpBox(L10n.Tr(
                    $"レンダラーのメッシュ（頂点 {(mesh != null ? mesh.vertexCount : 0)} 個）が、データ（頂点 {Data.VertexCount} 個）と合いません。プレビューと書き出しはできません。" +
                    "元のメッシュに戻すか、このメッシュ用のデータを指定し直してください。",
                    $"The renderer's mesh ({(mesh != null ? mesh.vertexCount : 0)} vertices) does not match the data ({Data.VertexCount} vertices). Preview and export are unavailable. " +
                    "Restore the original mesh, or assign the data made for this mesh."), MessageType.Warning);
                DrawRestoreButton(issues);
            }

            if ((issues & ShapeKeyProtection.Issue.SourceMeshDiffers) != 0 && (issues & ShapeKeyProtection.Issue.SourceMeshMissing) == 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "コンポーネントの元のメッシュが、データの元のメッシュと違います。",
                    "The component's source mesh differs from the data's source mesh."), MessageType.Info);
                if (GUILayout.Button(L10n.Tr("データに合わせる", "Match the data"))) ShapeKeyProtection.SyncComponentSource(_component);
            }
            return true;
        }

        void DrawDataField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = (GP02ShapeKeyData)EditorGUILayout.ObjectField(L10n.Tr("データ", "Data"), _component.Data, typeof(GP02ShapeKeyData), false);
                if (EditorGUI.EndChangeCheck())
                {
                    StopPreview();
                    ShapeKeyProtection.SetData(_component, picked);
                    ReloadTree();
                }
                using (new EditorGUI.DisabledScope(_component.Data == null))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("保存", "Save"), L10n.Tr("データをアセットとして保存します。", "Save the data asset.")), GUILayout.Width(48f)))
                    {
                        ShapeKeyLibrary.SaveData(_component.Data);
                        Show(L10n.Tr("データを保存しました。", "Saved the data."), MessageType.Info);
                    }
                }
            }
        }

        void DrawSourceMeshField()
        {
            EditorGUI.BeginChangeCheck();
            var mesh = (Mesh)EditorGUILayout.ObjectField(L10n.Tr("元のメッシュ", "Source mesh"), Data.SourceMesh, typeof(Mesh), false);
            if (EditorGUI.EndChangeCheck() && mesh != null)
            {
                if (mesh.vertexCount != Data.VertexCount)
                {
                    Show(L10n.Tr($"頂点数が違います（データ {Data.VertexCount} 個、メッシュ {mesh.vertexCount} 個）。", $"The vertex counts differ (data {Data.VertexCount}, mesh {mesh.vertexCount})."), MessageType.Warning);
                }
                else
                {
                    Undo.RecordObject(Data, "GP02 " + L10n.Tr("元のメッシュを設定", "Set source mesh"));
                    Data.SourceMesh = mesh;
                    EditorUtility.SetDirty(Data);
                    ShapeKeyProtection.SyncComponentSource(_component);
                }
            }
        }

        void DrawRestoreButton(ShapeKeyProtection.Issue issues)
        {
            using (new EditorGUI.DisabledScope(!ShapeKeyProtection.CanRestoreMesh(_component) || _renderer == null))
            {
                if (GUILayout.Button(L10n.Tr("元のメッシュに戻す", "Restore the original mesh"))) RestoreSourceMesh();
            }
        }

        void RestoreSourceMesh()
        {
            Run(() =>
            {
                if (ShapeKeyExporter.RestoreSourceMesh(_component))
                {
                    Show(L10n.Tr("レンダラーのメッシュを元のメッシュに戻しました。", "Restored the renderer's original mesh."), MessageType.Info);
                }
                else
                {
                    Show(L10n.Tr("元のメッシュが分からないので戻せません。", "The original mesh is unknown, so it cannot be restored."), MessageType.Warning);
                }
            });
            ReloadTree();
        }

        // ---- ツールバー ----

        void DrawToolbar()
        {
            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            bool any = selected.Count > 0;
            bool copyPaste = ClothEditView.Shows(ClothEditUiItem.ShapeKeyCopyPaste);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(!any))
                {
                    if (ToolbarButton(L10n.Tr("複製", "Duplicate"), L10n.Tr("選んだシェイプキーを複製します（Ctrl+D）", "Duplicate the selected shape keys (Ctrl+D)"))) DuplicateSelected();
                    if (copyPaste && ToolbarButton(L10n.Tr("コピー", "Copy"), L10n.Tr("選んだシェイプキーをコピーします（Ctrl+C）。別のデータにも貼れます", "Copy the selected shape keys (Ctrl+C). They can be pasted into another data"))) CopySelected();
                }
                if (copyPaste)
                {
                    using (new EditorGUI.DisabledScope(ShapeKeyListOperations.GetPasteProblem(Data) != null))
                    {
                        string tip = ShapeKeyListOperations.GetPasteProblem(Data) ?? L10n.Tr("コピーしたシェイプキーを貼り付けます（Ctrl+V）", "Paste the copied shape keys (Ctrl+V)");
                        if (ToolbarButton(L10n.Tr("貼り付け", "Paste"), tip)) Paste();
                    }
                }
                using (new EditorGUI.DisabledScope(!any))
                {
                    if (ToolbarButton(L10n.Tr("削除", "Delete"), L10n.Tr("選んだシェイプキーを削除します（Delete）", "Delete the selected shape keys (Delete)"))) DeleteSelected();
                }
                if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyReorder))
                {
                    GUILayout.Space(8f);
                    using (new EditorGUI.DisabledScope(!any || !IsReorderAllowed))
                    {
                        if (ToolbarButton("▲", L10n.Tr("上へ", "Move up"))) MoveSelected(-1);
                        if (ToolbarButton("▼", L10n.Tr("下へ", "Move down"))) MoveSelected(1);
                    }
                }
                if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyBulkEnable))
                {
                    GUILayout.Space(8f);
                    using (new EditorGUI.DisabledScope(!any))
                    {
                        if (ToolbarButton(L10n.Tr("有効", "On"), L10n.Tr("選んだシェイプキーをまとめて有効にします", "Enable the selected shape keys"))) SetEnabled(selected, true);
                        if (ToolbarButton(L10n.Tr("無効", "Off"), L10n.Tr("選んだシェイプキーをまとめて無効にします（書き出しに含めません）", "Disable the selected shape keys (they are not exported)"))) SetEnabled(selected, false);
                    }
                }
                GUILayout.FlexibleSpace();
                if (ClothEditView.Shows(ClothEditUiItem.ShapeKeyGroups) && ToolbarButton(L10n.Tr("＋グループ", "+ Group"), L10n.Tr("新しいグループを作ります。シェイプキーはドラッグで入れられます", "Create a group. Drag shape keys into it"))) AddGroup();
            }
        }

        static bool ToolbarButton(string label, string tooltip) => GUILayout.Button(new GUIContent(label, tooltip), EditorStyles.toolbarButton);

        // ---- プレビューの状態 ----

        void DrawPreviewStatus(GP02ShapeKeyData data)
        {
            if (_renderer == null) return;
            bool active = ShapeKeyPreview.IsActive(_renderer);
            bool hasWeights = ShapeKeyListOperations.HasActivePreviewWeight(data);
            if (!string.IsNullOrEmpty(_previewError)) EditorGUILayout.HelpBox(_previewError, MessageType.Warning);

            if (active)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(L10n.Tr("プレビュー中（元のメッシュは書き換えていません）", "Previewing (the original mesh is not modified)"), EditorStyles.miniLabel);
                    if (GUILayout.Button(L10n.Tr("プレビューを終了", "End preview"), GUILayout.Width(120f))) StopPreview();
                }
            }
            else if (hasWeights)
            {
                EditorGUILayout.HelpBox(L10n.Tr("重みが 0 でないのに、プレビューは止まっています。", "Weights are not 0, but the preview is stopped."), MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L10n.Tr("プレビューを再開", "Resume preview"))) ApplyPreview();
                    if (GUILayout.Button(L10n.Tr("重みをすべて 0 にする", "Reset all weights to 0")))
                    {
                        ShapeKeyListOperations.ResetPreviewWeights(data);
                    }
                }
            }
        }

        // ---- 布で編集中の保存 ----

        void DrawClothEditStatus(GP02ShapeKeyData data)
        {
            if (_component == null || ShapeKeyClothEdit.ActiveComponent != _component) return;
            ClothEditController controller = ShapeKeyClothEdit.ControllerProvider?.Invoke();
            if (controller == null || !controller.IsEditing) return;

            if (string.IsNullOrEmpty(_clothSaveName)) _clothSaveName = ShapeKeyClothEdit.ActiveEntryName;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L10n.Tr("布で編集中", "Editing with cloth"), EditorStyles.boldLabel);
                _clothSaveName = EditorGUILayout.TextField(L10n.Tr("保存する名前", "Name"), _clothSaveName);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L10n.Tr("同じ名前で上書き保存", "Save (overwrite)"))) SaveClothEdit(controller, true);
                    if (GUILayout.Button(L10n.Tr("別名で追加", "Save as new"))) SaveClothEdit(controller, false);
                }
            }
        }

        void SaveClothEdit(ClothEditController controller, bool overwrite)
        {
            Run(() =>
            {
                ShapeKeyEntry saved = ShapeKeyClothEdit.SaveActive(controller, overwrite, _clothSaveName);
                if (saved == null)
                {
                    Show(string.IsNullOrEmpty(controller.LastError) ? L10n.Tr("保存できませんでした。", "Could not save.") : controller.LastError, MessageType.Warning);
                    return;
                }
                AfterStructuralChange(new[] { saved });
                Show(L10n.Tr($"「{saved.Name}」を保存しました。", $"Saved \"{saved.Name}\"."), MessageType.Info);
            });
        }

        // ---- 選んだものへの操作 ----

        void DrawOperations(GP02ShapeKeyData data)
        {
            if (!ClothEditView.Shows(ClothEditUiItem.ShapeKeyMoreOperations))
            {
                DrawMainOperations();
                return;
            }
            _settings.ShowOperations = EditorGUILayout.Foldout(_settings.ShowOperations, L10n.Tr("選んだシェイプキーへの操作", "Operations on the selection"), true);
            if (!_settings.ShowOperations) return;

            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            bool any = selected.Count > 0;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(!any))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("合成", "Merge"), L10n.Tr("重みをかけて足して、1つにします。重みはスライダーの値（すべて 0 なら 100）", "Add them into one with their weights (the slider values, or 100 if all are 0)")))) MergeSelected();
                    if (GUILayout.Button(new GUIContent(L10n.Tr("左右に分割", "Split L/R"), L10n.Tr("_L と _R の2つに分けます", "Split into _L and _R")))) SplitSelected();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L10n.Tr("左右反転", "Flip"))) FlipSelected();
                    if (GUILayout.Button(L10n.Tr("左右対称にする", "Symmetrize"))) SymmetrizeSelected();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    _settings.ScalePercent = EditorGUILayout.FloatField(L10n.Tr("量（%）", "Amount (%)"), _settings.ScalePercent);
                    if (GUILayout.Button(L10n.Tr("拡大縮小", "Scale"), GUILayout.Width(80f))) ScaleSelected();
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("法線を計算し直す", "Recompute normals"),
                    L10n.Tr("選んだシェイプキーの法線を、動かした形から計算し直します（取り込んだものにも使えます）。角度などは下の「操作の設定」で変えられます",
                        "Recomputes the normals of the selected shape keys from the moved shape (works on imported ones too). The angle and other options are in the settings below")))) RecomputeNormalsSelected();
                using (new EditorGUILayout.HorizontalScope())
                {
                    _settings.InBetweenMode = (ShapeKeyInBetweenMode)EditorGUILayout.EnumPopup(L10n.Tr("in-between", "In-between"), _settings.InBetweenMode);
                    if (GUILayout.Button(new GUIContent(L10n.Tr("順に並べる", "Chain"),
                        L10n.Tr("選んだものを一覧の順に並べて、1つの in-between 付きシェイプキーにします", "Chain the selection in list order into one shape key with in-between frames")), GUILayout.Width(80f))) MakeInBetween();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L10n.Tr("グループに入れる ▾", "Move to group ▾"))) ShowGroupMenu();
                    if (GUILayout.Button(L10n.Tr("グループから外す", "Remove from group"))) SetGroupOfSelected("");
                }
            }
            DrawAdvancedOperations(data, selected);

            _settings.ShowOperationSettings = EditorGUILayout.Foldout(_settings.ShowOperationSettings, L10n.Tr("操作の設定（左右など）", "Operation settings (left/right etc.)"), true);
            if (_settings.ShowOperationSettings)
            {
                EditorGUI.indentLevel++;
                _settings.Axis = EditorGUILayout.Popup(L10n.Tr("左右を分ける軸", "Mirror axis"), _settings.Axis, AxisLabels);
                _settings.SplitInAvatarSpace = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("分割の軸をアバターのローカルで見る", "Split axis in avatar-local space"),
                    L10n.Tr("オンなら、「左右に分割」の軸と中心を、アバターのルート（Animator のある親）の X/Y/Z とメートルで見ます。オフなら、メッシュ自身の座標です。反転・対称は常にメッシュの座標です",
                        "If on, the axis and center of Split L/R are the X/Y/Z and meters of the avatar root (the parent with the Animator). If off, they are the mesh's own coordinates. Flip and Symmetrize always use the mesh's coordinates")), _settings.SplitInAvatarSpace);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _settings.Center = EditorGUILayout.FloatField(L10n.Tr("中心（m）", "Center (m)"), _settings.Center);
                    if (GUILayout.Button(L10n.Tr("メッシュの中心", "Mesh center"), GUILayout.Width(100f))) UseMeshCenter(data);
                }
                _settings.BlendWidthCm = EditorGUILayout.FloatField(new GUIContent(L10n.Tr("中央のなめらかさの幅（cm）", "Center blend width (cm)"),
                    L10n.Tr("左右に分割するとき、中央をこの幅でなめらかにつなぎます", "When splitting, the center is blended over this width")), _settings.BlendWidthCm);
                _settings.ToleranceMm = EditorGUILayout.FloatField(new GUIContent(L10n.Tr("鏡像を探す距離（mm）", "Mirror distance (mm)"),
                    L10n.Tr("反転・対称で、反対側の頂点を探す許容距離", "Tolerance for finding the mirrored vertex")), _settings.ToleranceMm);
                _settings.LeftIsPositive = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("軸の + 側を L にする", "Positive side is L"),
                    L10n.Tr("分割で、軸の値が大きい側を _L にします", "The side with the larger axis value becomes _L")), _settings.LeftIsPositive);
                _settings.KeepPositiveSide = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("対称: + 側を正とする", "Symmetrize: keep + side"),
                    L10n.Tr("左右対称にするとき、軸の値が大きい側を残して反対側に写します", "Keep the larger-axis side and copy it to the other side")), _settings.KeepPositiveSide);
                _settings.AsCopy = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("反転・対称は別のシェイプキーにする", "Flip/Symmetrize into a new key"),
                    L10n.Tr("オフなら選んだシェイプキーを書き換えます", "If off, the selected shape key is overwritten")), _settings.AsCopy);
                _settings.NormalSmoothAngle = EditorGUILayout.Slider(new GUIContent(L10n.Tr("法線のスムーズ角度（°）", "Normal smooth angle (deg)"),
                    L10n.Tr("法線を計算し直すとき、同じ位置の頂点の面を、この角度以内ならなめらかにつなぎます。180 で全部つなぎます。固いエッジを残したいときは小さくします。「同じ位置の頂点を結合」がオンのときだけ効きます",
                        "When recomputing normals, faces of vertices at the same position are joined smoothly if within this angle. 180 joins everything; lower it to keep hard edges. Only used when \"Merge vertices at the same position\" is on")),
                    _settings.NormalSmoothAngle, 0f, 180f);
                _settings.NormalMergeCoincident = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("法線: 同じ位置の頂点を結合", "Normals: merge vertices at the same position"),
                    L10n.Tr("UV の継ぎ目などで分かれた頂点の法線をそろえます。オフなら、頂点ごとにそれぞれの面だけで計算します",
                        "Aligns the normals of vertices split at UV seams and the like. If off, each vertex uses only its own faces")), _settings.NormalMergeCoincident);
                _settings.DisableSources = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("合成・分割・順に並べたあと元を無効にする", "Disable the originals after merge/split/chain"),
                    L10n.Tr("書き出しで二重に動かないように、元のシェイプキーを無効にします", "Disable the originals so they are not applied twice on export")), _settings.DisableSources);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck()) _settings.Save();
        }

        // かんたん表示: 左右に分割と左右反転だけ。軸と中心は「くわしく」の操作の設定で決める（既定はメッシュの X 軸・中心 0）
        void DrawMainOperations()
        {
            bool any = _tree.GetSelectedEntries().Count > 0;
            using (new EditorGUI.DisabledScope(!any))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("左右に分割", "Split L/R"), L10n.Tr("選んだシェイプキーを _L と _R の2つに分けます。軸と中心は「くわしく」の操作の設定で変えられます", "Splits the selected shape keys into _L and _R. The axis and center can be changed in the Detailed view's operation settings")))) SplitSelected();
                    if (GUILayout.Button(new GUIContent(L10n.Tr("左右反転", "Flip"), L10n.Tr("選んだシェイプキーを、左右を入れ替えた形にします", "Mirrors the selected shape keys left to right")))) FlipSelected();
                }
            }
        }

        void UseMeshCenter(GP02ShapeKeyData data)
        {
            if (data.SourceMesh == null) return;
            Vector3 c = data.SourceMesh.bounds.center;
            if (_settings.SplitInAvatarSpace && _renderer != null)
            {
                // メッシュの中心を、アバターのローカルに換算する
                Transform root = ShapeKeyExporter.FindAvatarRoot(_renderer.transform);
                c = root.InverseTransformPoint(_renderer.transform.TransformPoint(c));
            }
            _settings.Center = _settings.Axis == 0 ? c.x : _settings.Axis == 1 ? c.y : c.z;
        }

        // ---- 取り込み ----

        void DrawImport(GP02ShapeKeyData data)
        {
            _settings.ShowImport = EditorGUILayout.Foldout(_settings.ShowImport, L10n.Tr("取り込み", "Import"), true);
            if (!_settings.ShowImport) return;

            Mesh source = data.SourceMesh;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(source == null))
            {
                int shapes = source != null ? source.blendShapeCount : 0;
                using (new EditorGUI.DisabledScope(shapes == 0))
                {
                    if (GUILayout.Button(L10n.Tr($"元メッシュの BlendShape から取り込む（{shapes} 個）…", $"Import from the source mesh's BlendShapes ({shapes})...")))
                    {
                        ShowBlendShapeMenu(source);
                    }
                }

                _settings.ImportOverwrite = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("同じ名前は上書きする", "Overwrite the same name"),
                    L10n.Tr("取り込むシェイプキーと同じ名前のものがあれば、新しく作らずそれを上書きします。前の形は「前の版」に残ります",
                        "If a shape key with the same name exists, it is overwritten instead of adding a new one. The previous shape stays in its history")), _settings.ImportOverwrite);
                _importMeshAsset = EditorGUILayout.ObjectField(L10n.Tr("別のメッシュから", "From another mesh"), _importMeshAsset, typeof(Mesh), false);
                using (new EditorGUI.DisabledScope(_importMeshAsset == null))
                {
                    if (GUILayout.Button(L10n.Tr("このメッシュとの差を取り込む", "Import the difference from this mesh"))) ImportMesh((Mesh)_importMeshAsset);
                }

                _importClip = (AnimationClip)EditorGUILayout.ObjectField(L10n.Tr("AnimationClip から", "From AnimationClip"), _importClip, typeof(AnimationClip), false);
                using (new EditorGUI.DisabledScope(_importClip == null))
                {
                    _settings.ClipTime = EditorGUILayout.FloatField(L10n.Tr("時刻（秒）", "Time (s)"), _settings.ClipTime);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _settings.ClipRendererPath = EditorGUILayout.TextField(new GUIContent(L10n.Tr("レンダラーのパス", "Renderer path"),
                            L10n.Tr("クリップの中での、このレンダラーのパス（Animator のオブジェクトからの相対）", "Path of this renderer in the clip (relative to the Animator object)")), _settings.ClipRendererPath);
                        if (GUILayout.Button(L10n.Tr("自動", "Auto"), GUILayout.Width(48f))) _settings.ClipRendererPath = ShapeKeyListOperations.GuessRendererPath(_renderer);
                    }
                    if (GUILayout.Button(L10n.Tr("この時刻の BlendShape を取り込む", "Import the BlendShapes at this time"))) ImportClip();
                    if (GUILayout.Button(new GUIContent(L10n.Tr("すべてのキーフレームを in-between として取り込む", "Import all keyframes as in-between frames"),
                        L10n.Tr("クリップの BlendShape のキーフレームの時刻ごとの形を、1つのシェイプキーの in-between のフレームにします（時刻の順に等間隔の重み）",
                            "Turns the shape at each keyframe time of the clip's BlendShapes into one frame of a single in-between shape key (evenly spaced weights in time order)")))) ImportClipKeyframes();
                }
            }
            if (EditorGUI.EndChangeCheck()) _settings.Save();
        }

        void ShowBlendShapeMenu(Mesh source)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L10n.Tr("（まとめて全部）", "(All)")), false, () => Run(() =>
            {
                List<ShapeKeyEntry> added = ShapeKeyListOperations.ImportAllBlendShapes(Data, source, _settings.ImportOverwrite);
                AfterStructuralChange(added);
                Show(L10n.Tr($"{added.Count} 個の BlendShape を取り込みました。", $"Imported {added.Count} BlendShapes."), MessageType.Info);
            }));
            menu.AddSeparator("");
            for (int i = 0; i < source.blendShapeCount; i++)
            {
                int index = i;
                string name = source.GetBlendShapeName(i);
                menu.AddItem(new GUIContent(name.Replace('/', '／')), false, () => Run(() =>
                {
                    List<ShapeKeyEntry> added = ShapeKeyListOperations.ImportBlendShapes(Data, source, new[] { index }, _settings.ImportOverwrite);
                    AfterStructuralChange(added);
                }));
            }
            menu.ShowAsContext();
        }

        void ImportMesh(Mesh mesh)
        {
            Run(() =>
            {
                ShapeKeyEntry entry = ShapeKeyListOperations.ImportFromMeshAsset(Data, mesh, _settings.ImportOverwrite);
                AfterStructuralChange(new[] { entry });
                Show(L10n.Tr($"「{entry.Name}」として取り込みました。", $"Imported as \"{entry.Name}\"."), MessageType.Info);
            });
        }

        void ImportClip()
        {
            Run(() =>
            {
                ShapeKeyEntry entry = ShapeKeyListOperations.ImportFromClip(Data, _importClip, _settings.ClipRendererPath, _settings.ClipTime, _settings.ImportOverwrite);
                AfterStructuralChange(new[] { entry });
                Show(L10n.Tr($"「{entry.Name}」として取り込みました。", $"Imported as \"{entry.Name}\"."), MessageType.Info);
            });
        }

        void ImportClipKeyframes()
        {
            Run(() =>
            {
                ShapeKeyEntry entry = ShapeKeyListOperations.ImportFromClipKeyframes(Data, _importClip, _settings.ClipRendererPath, _settings.ImportOverwrite);
                AfterStructuralChange(new[] { entry });
                Show(L10n.Tr($"「{entry.Name}」として取り込みました（{entry.Frames.Count} フレームの in-between）。", $"Imported as \"{entry.Name}\" ({entry.Frames.Count}-frame in-between)."), MessageType.Info);
            });
        }

        // ------------------------------------------------------------------
        // 操作（ボタン・右クリックメニューから）
        // ------------------------------------------------------------------

        internal void DuplicateSelected()
        {
            Run(() => AfterStructuralChange(ShapeKeyListOperations.Duplicate(Data, _tree.GetSelectedEntries())));
        }

        internal void CopySelected()
        {
            int count = ShapeKeyListOperations.Copy(Data, _tree.GetSelectedEntries());
            if (count > 0) Show(L10n.Tr($"{count} 個をコピーしました。", $"Copied {count}."), MessageType.Info);
        }

        internal void Paste()
        {
            Run(() =>
            {
                if (!ShapeKeyListOperations.TryPaste(Data, out List<ShapeKeyEntry> pasted, out string error))
                {
                    Show(error, MessageType.Warning);
                    return;
                }
                AfterStructuralChange(pasted);
            });
        }

        internal void DeleteSelected()
        {
            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            if (selected.Count == 0)
            {
                // グループだけ選んでいるときは、グループを消す（中のシェイプキーは残る）
                List<string> groups = _tree.GetSelectedGroups();
                if (groups.Count == 0) return;
                foreach (string g in groups) ShapeKeyListOperations.RemoveGroup(Data, g);
                ReloadTree();
                return;
            }
            Run(() =>
            {
                ShapeKeyListOperations.Delete(Data, selected);
                ReloadTree();
                _treeState.selectedIDs.Clear();
                if (_renderer != null && ShapeKeyPreview.IsActive(_renderer)) ApplyPreview();
            });
        }

        void MoveSelected(int direction)
        {
            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            if (ShapeKeyListOperations.Move(Data, selected, direction)) AfterStructuralChange(selected);
        }

        void AddGroup()
        {
            string name = ShapeKeyListOperations.SuggestGroupName(Data);
            ShapeKeyListOperations.AddGroup(Data, name);
            ReloadTree();
            _pendingRename = name;
        }

        void SetGroupOfSelected(string group)
        {
            ShapeKeyListOperations.SetGroup(Data, _tree.GetSelectedEntries(), group);
            AfterStructuralChange(_tree.GetSelectedEntries());
        }

        void ShowGroupMenu()
        {
            var menu = new GenericMenu();
            foreach (string group in Data.Groups)
            {
                string g = group;
                menu.AddItem(new GUIContent(g), false, () => SetGroupOfSelected(g));
            }
            if (Data.Groups.Count > 0) menu.AddSeparator("");
            menu.AddItem(new GUIContent(L10n.Tr("新しいグループ", "New group")), false, () =>
            {
                string name = ShapeKeyListOperations.SuggestGroupName(Data);
                ShapeKeyListOperations.AddGroup(Data, name);
                SetGroupOfSelected(name);
                _pendingRename = name;
                ReloadTree();
            });
            menu.ShowAsContext();
        }

        void MergeSelected()
        {
            Run(() => AfterStructuralChange(new[] { ShapeKeyListOperations.Merge(Data, _tree.GetSelectedEntries(), null, null, _settings.DisableSources) }));
        }

        void SplitSelected()
        {
            Run(() =>
            {
                var added = new List<ShapeKeyEntry>();
                foreach (ShapeKeyEntry entry in _tree.GetSelectedEntries())
                {
                    ShapeKeyEntry left, right;
                    if (_settings.SplitInAvatarSpace)
                    {
                        if (_renderer == null) throw new InvalidOperationException(L10n.Tr("アバターのローカル軸で分けるには、レンダラーが要ります。", "A renderer is needed to split along the avatar-local axis."));
                        if (!TryGetAvatarPlane(out Vector3 normal, out float offset))
                        {
                            throw new InvalidOperationException(L10n.Tr("アバターのローカル軸を、メッシュの座標に換算できません（スケールが 0 など）。", "The avatar-local axis cannot be converted to mesh coordinates (zero scale, etc.)."));
                        }
                        ShapeKeyListOperations.SplitByPlane(Data, entry, normal, offset, _settings.BlendWidth, _settings.LeftIsPositive, _settings.DisableSources, out left, out right);
                    }
                    else
                    {
                        ShapeKeyListOperations.SplitLeftRight(Data, entry, _settings.Axis, _settings.Center, _settings.BlendWidth,
                            _settings.LeftIsPositive, _settings.DisableSources, out left, out right);
                    }
                    added.Add(left);
                    added.Add(right);
                }
                AfterStructuralChange(added);
            });
        }

        void FlipSelected()
        {
            Run(() =>
            {
                var results = new List<ShapeKeyEntry>();
                foreach (ShapeKeyEntry entry in _tree.GetSelectedEntries())
                {
                    results.Add(ShapeKeyListOperations.Flip(Data, entry, _settings.Axis, _settings.Center, _settings.Tolerance, _settings.AsCopy));
                }
                AfterStructuralChange(results);
            });
        }

        void SymmetrizeSelected()
        {
            Run(() =>
            {
                var results = new List<ShapeKeyEntry>();
                foreach (ShapeKeyEntry entry in _tree.GetSelectedEntries())
                {
                    results.Add(ShapeKeyListOperations.Symmetrize(Data, entry, _settings.Axis, _settings.Center, _settings.Tolerance, _settings.KeepPositiveSide, _settings.AsCopy));
                }
                AfterStructuralChange(results);
            });
        }

        void ScaleSelected()
        {
            Run(() =>
            {
                List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
                ShapeKeyListOperations.Scale(Data, selected, _settings.ScalePercent / 100f);
                AfterStructuralChange(selected);
            });
        }

        void RecomputeNormalsSelected()
        {
            Run(() =>
            {
                List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
                int done = ShapeKeyListOperations.RecomputeNormals(Data, selected, _settings.NormalSmoothAngle, _settings.NormalMergeCoincident);
                AfterStructuralChange(selected);
                Show(L10n.Tr($"{done} 個のシェイプキーの法線を計算し直しました。", $"Recomputed the normals of {done} shape key(s)."), MessageType.Info);
            });
        }

        void MakeInBetween()
        {
            Run(() => AfterStructuralChange(new[]
            {
                ShapeKeyListOperations.MakeInBetween(Data, _tree.GetSelectedEntries(), null, _settings.InBetweenMode, null, _settings.DisableSources),
            }));
        }

        void EditWithCloth()
        {
            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            if (selected.Count != 1)
            {
                Show(L10n.Tr("布で編集するシェイプキーを1つだけ選んでください。", "Select exactly one shape key to edit with cloth."), MessageType.Warning);
                return;
            }
            Run(() =>
            {
                if (ShapeKeyClothEdit.Request(_component, selected[0], out string message)) Show(message, MessageType.Info);
                else Show(message, MessageType.Warning);
            });
        }

        // ---- 右クリックメニュー ----

        internal void ShowContextMenu()
        {
            if (Data == null) return;
            List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
            bool any = selected.Count > 0;
            var menu = new GenericMenu();

            AddItem(menu, L10n.Tr("布で編集する", "Edit with cloth"), selected.Count == 1, EditWithCloth);
            if (!ClothEditView.Shows(ClothEditUiItem.ShapeKeyMoreOperations))
            {
                // かんたん表示: 主な操作だけ
                menu.AddSeparator("");
                AddItem(menu, L10n.Tr("複製", "Duplicate"), any, DuplicateSelected);
                AddItem(menu, L10n.Tr("削除", "Delete"), any, DeleteSelected);
                menu.AddSeparator("");
                AddItem(menu, L10n.Tr("左右に分割", "Split L/R"), any, SplitSelected);
                AddItem(menu, L10n.Tr("左右反転", "Flip"), any, FlipSelected);
                menu.ShowAsContext();
                return;
            }
            AddItem(menu, L10n.Tr("これだけ表示（ソロ）", "Show only this (solo)"), selected.Count == 1, () => SetSolo(IsSolo(selected[0]) ? null : selected[0]));
            AddVersionMenu(menu, selected);
            menu.AddSeparator("");
            AddItem(menu, L10n.Tr("複製", "Duplicate"), any, DuplicateSelected);
            AddItem(menu, L10n.Tr("コピー", "Copy"), any, CopySelected);
            AddItem(menu, L10n.Tr("貼り付け", "Paste"), ShapeKeyListOperations.GetPasteProblem(Data) == null, Paste);
            AddItem(menu, L10n.Tr("削除", "Delete"), any, DeleteSelected);
            menu.AddSeparator("");
            AddItem(menu, L10n.Tr("合成（重み付きの和）", "Merge (weighted sum)"), any, MergeSelected);
            AddItem(menu, L10n.Tr("左右に分割", "Split L/R"), any, SplitSelected);
            AddItem(menu, L10n.Tr("左右反転", "Flip"), any, FlipSelected);
            AddItem(menu, L10n.Tr("左右対称にする", "Symmetrize"), any, SymmetrizeSelected);
            AddItem(menu, L10n.Tr("量を拡大縮小", "Scale amount"), any, ScaleSelected);
            AddItem(menu, L10n.Tr("法線を計算し直す", "Recompute normals"), any, RecomputeNormalsSelected);
            AddItem(menu, L10n.Tr("in-between にする（順に並べる）", "Chain into in-between"), any, MakeInBetween);
            AddItem(menu, L10n.Tr("差し引き（上 − 下）", "Subtract (upper − lower)"), selected.Count == 2, SubtractSelected);
            AddItem(menu, L10n.Tr("選んだ点で分割", "Split by selected points"), any, SplitBySelection);
            AddItem(menu, L10n.Tr("選んだ点の範囲だけ取り出す", "Extract the selected range"), any, () => ApplyRange(ShapeKeyMaskOperation.Extract));
            AddItem(menu, L10n.Tr("選んだ点の範囲を消す", "Remove the selected range"), any, () => ApplyRange(ShapeKeyMaskOperation.Remove));
            menu.AddSeparator("");
            AddItem(menu, L10n.Tr("素体に焼き込む印を付ける", "Mark as prebake"), any, () => SetPrebake(selected, true));
            AddItem(menu, L10n.Tr("素体に焼き込む印を外す", "Unmark prebake"), any, () => SetPrebake(selected, false));
            menu.AddSeparator("");
            AddItem(menu, L10n.Tr("有効にする", "Enable"), any, () => SetEnabled(selected, true));
            AddItem(menu, L10n.Tr("無効にする", "Disable"), any, () => SetEnabled(selected, false));
            AddItem(menu, L10n.Tr("グループに入れる…", "Move to group..."), any, ShowGroupMenu);
            AddItem(menu, L10n.Tr("グループから外す", "Remove from group"), any, () => SetGroupOfSelected(""));
            List<string> groups = _tree.GetSelectedGroups();
            if (groups.Count == 1) AddItem(menu, L10n.Tr("グループ名を変更", "Rename group"), true, () => { _pendingRename = groups[0]; ReloadTree(); });
            menu.AddSeparator("");
            AddItem(menu, L10n.Tr("選んだものを素体に焼き込む", "Bake into the base"), any && ShapeKeyExporter.GetProblem(_component) == null, BakeSelected);
            menu.ShowAsContext();
        }

        static void AddItem(GenericMenu menu, string label, bool enabled, Action action)
        {
            if (enabled) menu.AddItem(new GUIContent(label), false, () => action());
            else menu.AddDisabledItem(new GUIContent(label));
        }
    }
}
