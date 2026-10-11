using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの一覧の TreeView。グループ（折りたたみ）の下にシェイプキーが並ぶ。
    /// 行ごとに、有効のチェック・名前・プレビューの重み・警告を出す。ドラッグ＆ドロップで並べ替えとグループの移動ができる。
    /// データの書き換えは <see cref="ShapeKeyListView"/> を通して行う（Undo と、プレビューの更新のため）。
    /// </summary>
    sealed class ShapeKeyTreeView : TreeView
    {
        const int GroupIdBase = 1000000;
        const string DragKey = "GP02.ShapeKeyDrag";
        const float ToggleWidth = 18f;
        const float IconWidth = 18f;
        const float SoloWidth = 20f;
        static readonly Color PrebakeColor = new Color(0.20f, 0.45f, 0.95f, 0.30f);

        sealed class EntryItem : TreeViewItem
        {
            public ShapeKeyEntry Entry;
            public int Index;
        }

        sealed class GroupItem : TreeViewItem
        {
            public string Group;
        }

        readonly ShapeKeyListView _owner;
        readonly List<string> _groupNames = new List<string>();
        int _builtEntryCount = -1;
        int _builtGroupCount = -1;
        static GUIContent _warningIcon;

        public ShapeKeyTreeView(TreeViewState state, ShapeKeyListView owner) : base(state)
        {
            _owner = owner;
            rowHeight = EditorGUIUtility.singleLineHeight + 4f;
            showAlternatingRowBackgrounds = true;
            showBorder = true;
            Reload();
        }

        /// <summary>表示している行の数と、データの数が合っていなければ作り直す（外から書き換えられたとき）。</summary>
        public void ReloadIfStale()
        {
            GP02ShapeKeyData data = _owner.Data;
            int entries = data != null ? data.Entries.Count : 0;
            int groups = data != null ? data.Groups.Count : 0;
            if (entries != _builtEntryCount || groups != _builtGroupCount) Reload();
        }

        // ------------------------------------------------------------------
        // 行の作成
        // ------------------------------------------------------------------

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            var items = new List<TreeViewItem>();
            GP02ShapeKeyData data = _owner.Data;
            _groupNames.Clear();
            if (data != null)
            {
                _groupNames.AddRange(data.Groups);
                foreach (ShapeKeyEntry e in data.Entries)
                {
                    if (e != null && !string.IsNullOrEmpty(e.Group) && !_groupNames.Contains(e.Group)) _groupNames.Add(e.Group);
                }

                // 検索と並べ替えは表示だけのもの（データの並びは変えない）。行の番号（id）はデータでの位置なので、表示の順が違っても選択は崩れない
                List<int> order = DisplayOrder(data, out bool filtering);
                for (int g = 0; g < _groupNames.Count; g++)
                {
                    int groupRow = items.Count;
                    items.Add(new GroupItem { id = GroupIdBase + g, depth = 0, displayName = _groupNames[g], Group = _groupNames[g] });
                    foreach (int i in order)
                    {
                        ShapeKeyEntry e = data.Entries[i];
                        if (e.Group == _groupNames[g]) items.Add(MakeEntryItem(e, i, 1));
                    }
                    // 検索中は、合うものが無いグループは隠す
                    if (filtering && items.Count == groupRow + 1) items.RemoveAt(groupRow);
                }
                foreach (int i in order)
                {
                    ShapeKeyEntry e = data.Entries[i];
                    if (string.IsNullOrEmpty(e.Group)) items.Add(MakeEntryItem(e, i, 0));
                }
            }
            _builtEntryCount = data != null ? data.Entries.Count : 0;
            _builtGroupCount = data != null ? data.Groups.Count : 0;

            if (items.Count == 0) root.children = new List<TreeViewItem>();
            else SetupParentsAndChildrenFromDepths(root, items);
            return root;
        }

        // 検索に合うエントリの、データでの位置を、並べ替えた順に返す
        List<int> DisplayOrder(GP02ShapeKeyData data, out bool filtering)
        {
            ShapeKeyViewSettings settings = _owner.Settings;
            string filter = settings != null ? settings.FilterText : "";
            filtering = !string.IsNullOrWhiteSpace(filter);
            List<ShapeKeyEntry> ordered = ShapeKeyListOperations.OrderForDisplay(data.Entries, settings != null ? settings.Sort : ShapeKeyListSort.DataOrder);
            var positions = new Dictionary<ShapeKeyEntry, int>();
            for (int i = 0; i < data.Entries.Count; i++)
            {
                if (data.Entries[i] != null && !positions.ContainsKey(data.Entries[i])) positions.Add(data.Entries[i], i);
            }
            var result = new List<int>(ordered.Count);
            foreach (ShapeKeyEntry e in ordered)
            {
                if (filtering && !ShapeKeyListOperations.MatchesFilter(e, filter)) continue;
                result.Add(positions[e]);
            }
            return result;
        }

        static EntryItem MakeEntryItem(ShapeKeyEntry entry, int index, int depth)
        {
            return new EntryItem { id = index + 1, depth = depth, displayName = entry.Name, Entry = entry, Index = index };
        }

        // ------------------------------------------------------------------
        // 選択の取得・設定
        // ------------------------------------------------------------------

        /// <summary>選んでいるシェイプキー（グループの行は含めない）。データの並びの順。</summary>
        public List<ShapeKeyEntry> GetSelectedEntries()
        {
            var result = new List<ShapeKeyEntry>();
            GP02ShapeKeyData data = _owner.Data;
            if (data == null) return result;
            var ids = new HashSet<int>(GetSelection());
            for (int i = 0; i < data.Entries.Count; i++)
            {
                if (data.Entries[i] != null && ids.Contains(i + 1)) result.Add(data.Entries[i]);
            }
            return result;
        }

        /// <summary>選んでいるグループの名前。</summary>
        public List<string> GetSelectedGroups()
        {
            var result = new List<string>();
            foreach (int id in GetSelection())
            {
                int g = id - GroupIdBase;
                if (g >= 0 && g < _groupNames.Count) result.Add(_groupNames[g]);
            }
            return result;
        }

        public void SelectEntries(IEnumerable<ShapeKeyEntry> entries)
        {
            GP02ShapeKeyData data = _owner.Data;
            var ids = new List<int>();
            if (data != null && entries != null)
            {
                foreach (ShapeKeyEntry e in entries)
                {
                    int index = data.Entries.IndexOf(e);
                    if (index >= 0) ids.Add(index + 1);
                }
            }
            SetSelection(ids, TreeViewSelectionOptions.RevealAndFrame);
        }

        public void BeginRenameGroup(string group)
        {
            int g = _groupNames.IndexOf(group);
            if (g < 0) return;
            TreeViewItem item = FindItem(GroupIdBase + g, rootItem);
            if (item != null) BeginRename(item);
        }

        // ------------------------------------------------------------------
        // 行の描画
        // ------------------------------------------------------------------

        protected override void RowGUI(RowGUIArgs args)
        {
            if (args.item is GroupItem group) DrawGroupRow(args, group);
            else if (args.item is EntryItem entry) DrawEntryRow(args, entry);
        }

        void DrawEntryRow(RowGUIArgs args, EntryItem item)
        {
            ShapeKeyEntry entry = item.Entry;
            GP02ShapeKeyData data = _owner.Data;
            string problem = _owner.GetProblem(entry, out bool isError);
            Rect row = args.rowRect;
            if (Event.current.type == EventType.Repaint)
            {
                // 素体に焼き込む印のあるものは青い背景（BlendShape にならず、素体の形になる）
                if (entry.Prebake) EditorGUI.DrawRect(row, PrebakeColor);
                if (problem != null) EditorGUI.DrawRect(row, isError ? new Color(0.85f, 0.15f, 0.15f, 0.40f) : new Color(0.9f, 0.7f, 0.1f, 0.25f));
            }

            float indent = GetContentIndent(item);
            float sliderWidth = Mathf.Clamp(row.width * 0.42f, 96f, 200f);
            var toggleRect = new Rect(row.x + indent, row.y + 2f, ToggleWidth, EditorGUIUtility.singleLineHeight);
            var iconRect = new Rect(row.xMax - IconWidth - 2f, row.y + 2f, IconWidth, EditorGUIUtility.singleLineHeight);
            var sliderRect = new Rect(iconRect.x - sliderWidth - 2f, row.y + 2f, sliderWidth, EditorGUIUtility.singleLineHeight);
            var soloRect = new Rect(sliderRect.x - SoloWidth - 2f, row.y + 2f, SoloWidth, EditorGUIUtility.singleLineHeight);
            float nameX = toggleRect.xMax + 2f;
            var nameRect = new Rect(nameX, row.y + 2f, Mathf.Max(30f, soloRect.x - nameX - 4f), EditorGUIUtility.singleLineHeight);

            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUI.Toggle(toggleRect, entry.Enabled);
            if (EditorGUI.EndChangeCheck()) _owner.SetEnabled(new List<ShapeKeyEntry> { entry }, enabled);

            EditorGUI.BeginChangeCheck();
            string newName = EditorGUI.DelayedTextField(nameRect, entry.Name);
            if (EditorGUI.EndChangeCheck()) _owner.Rename(entry, newName);

            bool isSolo = _owner.IsSolo(entry);
            EditorGUI.BeginChangeCheck();
            bool solo = GUI.Toggle(soloRect, isSolo, new GUIContent("S", L10n.Tr(
                "これだけ表示（ソロ）。このシェイプキーだけをシーンに見せます（他の重みは変えません）。スライダーが 0 のときは 100 で見せます",
                "Show only this one (solo). Only this shape key is shown in the scene (other weights are unchanged). Shown at 100 when the slider is 0")), EditorStyles.miniButton);
            if (EditorGUI.EndChangeCheck()) _owner.SetSolo(solo ? entry : null);

            using (new EditorGUI.DisabledScope(!entry.Enabled))
            {
                EditorGUI.BeginChangeCheck();
                float weight = EditorGUI.Slider(sliderRect, entry.PreviewWeight, 0f, 100f);
                if (EditorGUI.EndChangeCheck()) _owner.SetPreviewWeight(entry, weight);
            }

            if (problem != null)
            {
                if (_warningIcon == null) _warningIcon = new GUIContent(EditorGUIUtility.IconContent("console.warnicon.sml").image);
                var content = new GUIContent(_warningIcon) { tooltip = problem };
                GUI.Label(iconRect, content);
            }
            else if (data != null && entry.Frames.Count > 1)
            {
                // in-between は段の数を出す
                GUI.Label(iconRect, new GUIContent("◆", L10n.Tr($"in-between（{entry.Frames.Count} 段）", $"In-between ({entry.Frames.Count} steps)")));
            }
        }

        void DrawGroupRow(RowGUIArgs args, GroupItem item)
        {
            Rect row = args.rowRect;
            float indent = GetContentIndent(item);
            float sliderWidth = Mathf.Clamp(row.width * 0.42f, 96f, 200f);
            var toggleRect = new Rect(row.x + indent, row.y + 2f, ToggleWidth, EditorGUIUtility.singleLineHeight);
            var sliderRect = new Rect(row.xMax - sliderWidth - IconWidth - 4f, row.y + 2f, sliderWidth, EditorGUIUtility.singleLineHeight);
            float nameX = toggleRect.xMax + 2f;
            var nameRect = new Rect(nameX, row.y + 2f, Mathf.Max(30f, sliderRect.x - nameX - 4f), EditorGUIUtility.singleLineHeight);

            List<ShapeKeyEntry> children = _owner.GetEntriesInGroup(item.Group);
            int enabledCount = 0;
            float maxWeight = 0f;
            foreach (ShapeKeyEntry e in children)
            {
                if (e.Enabled) enabledCount++;
                maxWeight = Mathf.Max(maxWeight, e.PreviewWeight);
            }

            using (new EditorGUI.DisabledScope(children.Count == 0))
            {
                EditorGUI.showMixedValue = enabledCount > 0 && enabledCount < children.Count;
                EditorGUI.BeginChangeCheck();
                bool enabled = EditorGUI.Toggle(toggleRect, enabledCount == children.Count && children.Count > 0);
                if (EditorGUI.EndChangeCheck()) _owner.SetEnabled(children, enabled);
                EditorGUI.showMixedValue = false;

                // グループのスライダーは、中のシェイプキーの重みをまとめて動かす
                EditorGUI.BeginChangeCheck();
                float weight = EditorGUI.Slider(sliderRect, maxWeight, 0f, 100f);
                if (EditorGUI.EndChangeCheck()) _owner.SetPreviewWeights(children, weight);
            }

            if (!args.isRenaming)
            {
                var label = new GUIContent($"{item.Group}  ({children.Count})");
                GUI.Label(nameRect, label, EditorStyles.boldLabel);
            }
        }

        // ------------------------------------------------------------------
        // 名前の変更（グループ）
        // ------------------------------------------------------------------

        protected override bool CanRename(TreeViewItem item) => item is GroupItem;

        protected override Rect GetRenameRect(Rect rowRect, int row, TreeViewItem item)
        {
            float indent = GetContentIndent(item) + ToggleWidth + 2f;
            return new Rect(rowRect.x + indent, rowRect.y, Mathf.Max(60f, rowRect.width * 0.4f), rowRect.height);
        }

        protected override void RenameEnded(RenameEndedArgs args)
        {
            if (!args.acceptedRename || string.IsNullOrWhiteSpace(args.newName)) return;
            if (!(FindItem(args.itemID, rootItem) is GroupItem item)) return;
            _owner.RenameGroup(item.Group, args.newName);
        }

        // ------------------------------------------------------------------
        // 入力
        // ------------------------------------------------------------------

        protected override bool CanMultiSelect(TreeViewItem item) => true;

        protected override void SelectionChanged(IList<int> selectedIds) => _owner.OnSelectionChanged();

        protected override void ContextClickedItem(int id) => _owner.ShowContextMenu();

        protected override void ContextClicked() => _owner.ShowContextMenu();

        protected override void DoubleClickedItem(int id)
        {
            if (FindItem(id, rootItem) is GroupItem item) BeginRename(item);
        }

        protected override void KeyEvent()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;
            bool command = e.control || e.command;
            if (e.keyCode == KeyCode.Delete || (e.keyCode == KeyCode.Backspace && e.command))
            {
                _owner.DeleteSelected();
                e.Use();
            }
            else if (command && e.keyCode == KeyCode.C)
            {
                _owner.CopySelected();
                e.Use();
            }
            else if (command && e.keyCode == KeyCode.V)
            {
                _owner.Paste();
                e.Use();
            }
            else if (command && e.keyCode == KeyCode.D)
            {
                _owner.DuplicateSelected();
                e.Use();
            }
        }

        // ------------------------------------------------------------------
        // ドラッグ＆ドロップ（並べ替えとグループの移動）
        // ------------------------------------------------------------------

        // 検索や並べ替えで表示がデータの並びと違うときは、ドラッグでの並べ替えはしない（行の位置とデータの位置が合わないため）
        protected override bool CanStartDrag(CanStartDragArgs args) => args.draggedItem is EntryItem && _owner.IsReorderAllowed;

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            var entries = new List<ShapeKeyEntry>();
            GP02ShapeKeyData data = _owner.Data;
            if (data == null) return;
            foreach (int id in args.draggedItemIDs)
            {
                int index = id - 1;
                if (index >= 0 && index < data.Entries.Count && data.Entries[index] != null) entries.Add(data.Entries[index]);
            }
            if (entries.Count == 0) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(DragKey, entries);
            DragAndDrop.objectReferences = new Object[0];
            DragAndDrop.StartDrag(entries.Count == 1 ? entries[0].Name : $"{entries.Count} shape keys");
        }

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            var entries = DragAndDrop.GetGenericData(DragKey) as List<ShapeKeyEntry>;
            GP02ShapeKeyData data = _owner.Data;
            if (entries == null || data == null) return DragAndDropVisualMode.None;

            string group = "";
            int insertIndex = data.Entries.Count;
            switch (args.dragAndDropPosition)
            {
                case DragAndDropPosition.UponItem:
                    if (args.parentItem is GroupItem upon)
                    {
                        group = upon.Group;
                        insertIndex = EndOfGroup(data, group);
                    }
                    else if (args.parentItem is EntryItem onEntry)
                    {
                        group = onEntry.Entry.Group ?? "";
                        insertIndex = onEntry.Index;
                    }
                    break;
                case DragAndDropPosition.BetweenItems:
                    if (args.parentItem is GroupItem parentGroup)
                    {
                        group = parentGroup.Group;
                        insertIndex = IndexBefore(parentGroup, args.insertAtIndex, EndOfGroup(data, group));
                    }
                    else
                    {
                        // ルート直下は、グループに入っていないシェイプキー
                        insertIndex = IndexBefore(rootItem, args.insertAtIndex, data.Entries.Count);
                    }
                    break;
                case DragAndDropPosition.OutsideItems:
                    break;
            }

            if (args.performDrop) _owner.MoveEntriesTo(entries, insertIndex, group);
            return DragAndDropVisualMode.Move;
        }

        // 親の子のうち insertAtIndex 以降で最初のシェイプキーの、データでの位置。無ければ fallback
        static int IndexBefore(TreeViewItem parent, int insertAtIndex, int fallback)
        {
            if (parent.children == null) return fallback;
            for (int i = Mathf.Max(0, insertAtIndex); i < parent.children.Count; i++)
            {
                if (parent.children[i] is EntryItem e) return e.Index;
            }
            return fallback;
        }

        // グループの最後のシェイプキーの次の位置。空のグループはリストの最後
        static int EndOfGroup(GP02ShapeKeyData data, string group)
        {
            int last = -1;
            for (int i = 0; i < data.Entries.Count; i++)
            {
                if (data.Entries[i] != null && data.Entries[i].Group == group) last = i;
            }
            return last >= 0 ? last + 1 : data.Entries.Count;
        }
    }
}
