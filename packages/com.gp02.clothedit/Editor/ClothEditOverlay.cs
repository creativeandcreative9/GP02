using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace GP02.EditorTools
{
    /// <summary>
    /// シーンビューの道具バー（Overlay）。布を編集している間だけ出て、道具の切り替えと、対称・ショートカット一覧のオン／オフができる。
    /// 「選んで動かす」の間は、ハンドルの種類（移動・回転・拡大縮小）の切り替えも、ピン留めの間は「塗ると外す」も出る。
    /// かんたん表示では、つまむ・選んで動かす・ピン留め・元に戻すブラシだけを出す。くわしく表示では、回す・拡大縮小のブラシと、
    /// 伸びの表示・元の形の切り替えも出す（ウィンドウと同じ切り替え。<see cref="ClothEditView"/>）。
    /// 編集ウィンドウと同じ設定を触るので、どちらで変えても同じになる。
    /// 状態の反映は、画面に出ている間だけ短い間隔で読み直す（ショートカットキーやウィンドウでの変更に追従するため）。
    /// </summary>
    [Overlay(typeof(SceneView), OverlayId, "GP02", defaultDisplay = true, defaultDockZone = DockZone.RightColumn, defaultLayout = Layout.Panel)]
    public sealed class ClothEditOverlay : Overlay, ITransientOverlay
    {
        public const string OverlayId = "gp02-cloth-edit-tools";

        // 道具の並び（2つずつ2行）。ピン解除は「ピン留め」の「塗ると外す」に統合した
        static readonly ClothEditMode[] PrimaryTools =
        {
            ClothEditMode.Grab, ClothEditMode.Select,
            ClothEditMode.Pin, ClothEditMode.Relax,
        };

        // くわしく表示だけの道具（キーは E と R のまま）
        static readonly ClothEditMode[] DetailTools = { ClothEditMode.Rotate, ClothEditMode.Scale };

        const long RefreshMilliseconds = 100;

        readonly Dictionary<ClothEditMode, ToolbarToggle> _toolToggles = new Dictionary<ClothEditMode, ToolbarToggle>();
        readonly Dictionary<SelectionHandle, ToolbarToggle> _handleToggles = new Dictionary<SelectionHandle, ToolbarToggle>();
        VisualElement _detailToolRow;
        VisualElement _handleRow;
        VisualElement _pinRow;
        ToolbarToggle _pinErase;
        ToolbarToggle _symmetry;
        ToolbarToggle _stretch;
        ToolbarToggle _original;
        ToolbarToggle _shortcuts;
        ToolbarButton _manual;

        /// <summary>編集中だけ出す。</summary>
        public bool visible => ClothEditWindow.IsEditingNow;

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement { name = "gp02-tools" };
            root.style.minWidth = 220f;
            root.style.paddingLeft = root.style.paddingRight = 2f;
            root.style.paddingTop = root.style.paddingBottom = 2f;

            _toolToggles.Clear();
            for (int row = 0; row < PrimaryTools.Length / 2; row++)
            {
                VisualElement line = Row();
                for (int k = 0; k < 2; k++) line.Add(CreateToolToggle(PrimaryTools[row * 2 + k]));
                root.Add(line);
            }
            _detailToolRow = Row();
            foreach (ClothEditMode mode in DetailTools) _detailToolRow.Add(CreateToolToggle(mode));
            root.Add(_detailToolRow);

            // 「選んで動かす」の間だけ出るハンドルの種類
            _handleRow = Row();
            _handleRow.style.marginTop = 4f;
            _handleToggles.Clear();
            foreach (SelectionHandle handle in new[] { SelectionHandle.Move, SelectionHandle.Rotate, SelectionHandle.Scale })
            {
                var toggle = new ToolbarToggle();
                StyleCell(toggle);
                SelectionHandle captured = handle;
                toggle.RegisterValueChangedCallback(evt =>
                {
                    ClothEditWindow owner = ClothEditWindow.OverlayOwner;
                    if (owner != null) owner.OverlaySetHandle(captured);
                    Refresh();
                });
                _handleToggles[handle] = toggle;
                _handleRow.Add(toggle);
            }
            root.Add(_handleRow);

            // ピン留めの間だけ出る「塗ると外す」
            _pinRow = Row();
            _pinRow.style.marginTop = 4f;
            _pinErase = AddToggle(_pinRow, on => ClothEditWindow.OverlayOwner?.OverlaySetPinErase(on));
            root.Add(_pinRow);

            VisualElement options = Row();
            options.style.marginTop = 4f;
            _symmetry = AddToggle(options, enabled => ClothEditWindow.OverlayOwner?.OverlaySetSymmetry(enabled));
            _stretch = AddToggle(options, enabled => ClothEditWindow.OverlayOwner?.OverlaySetStretchHeatmap(enabled));
            _original = AddToggle(options, enabled => ClothEditWindow.OverlayOwner?.OverlaySetShowOriginal(enabled));
            root.Add(options);

            VisualElement help = Row();
            _shortcuts = AddToggle(help, visible => ClothEditWindow.OverlayOwner?.OverlaySetShortcutsVisible(visible));
            _manual = new ToolbarButton(() => ClothEditWindow.OverlayOwner?.OverlayOpenManual());
            StyleCell(_manual);
            help.Add(_manual);
            root.Add(help);

            Refresh();
            // 画面に出ている間だけ、状態を読み直す（ウィンドウやショートカットでの変更に追従）
            root.schedule.Execute(Refresh).Every(RefreshMilliseconds);
            return root;
        }

        ToolbarToggle CreateToolToggle(ClothEditMode mode)
        {
            var toggle = new ToolbarToggle();
            StyleCell(toggle);
            ClothEditMode captured = mode;
            toggle.RegisterValueChangedCallback(evt =>
            {
                ClothEditWindow owner = ClothEditWindow.OverlayOwner;
                if (owner != null)
                {
                    // 「選んで動かす」は、選んでいるときにもう一度押すと直前の道具に戻る（Q と同じ）。ほかの道具は、もう一度押しても外れない
                    if (captured == ClothEditMode.Select) owner.OverlaySetSelectTool(evt.newValue);
                    else owner.OverlaySetTool(captured);
                }
                Refresh();
            });
            _toolToggles[mode] = toggle;
            return toggle;
        }

        static VisualElement Row()
        {
            var line = new VisualElement();
            line.style.flexDirection = FlexDirection.Row;
            return line;
        }

        static void StyleCell(VisualElement element)
        {
            element.style.flexGrow = 1f;
            element.style.flexBasis = 0f;
            element.style.minWidth = 84f;
        }

        static ToolbarToggle AddToggle(VisualElement line, System.Action<bool> onChanged)
        {
            var toggle = new ToolbarToggle();
            StyleCell(toggle);
            toggle.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            line.Add(toggle);
            return toggle;
        }

        // 文字（言語）と、選ばれている状態を、ウィンドウの設定に合わせる
        internal void Refresh()
        {
            ClothEditWindow owner = ClothEditWindow.OverlayOwner;
            if (owner == null || _symmetry == null) return;
            ClothEditSettings settings = owner.OverlaySettings;

            displayName = L10n.Tr("GP02 道具", "GP02 Tools");
            ClothEditMode tool = settings.Tool == ClothEditMode.Unpin ? ClothEditMode.Pin : settings.Tool;
            foreach (KeyValuePair<ClothEditMode, ToolbarToggle> pair in _toolToggles)
            {
                ToolbarToggle toggle = pair.Value;
                toggle.text = ClothEditHelp.ToolName(pair.Key);
                toggle.tooltip = ClothEditHelp.ToolTip(pair.Key);
                toggle.SetValueWithoutNotify(tool == pair.Key);
            }

            // 回す・拡大縮小は、くわしく表示のときだけ。今の道具がそれなら、選ばれている印が見えるよう出しておく
            bool detailTool = ClothEditView.Shows(ClothEditUiItem.ToolRotate) || tool == ClothEditMode.Rotate || tool == ClothEditMode.Scale;
            _detailToolRow.style.display = detailTool ? DisplayStyle.Flex : DisplayStyle.None;

            bool selecting = tool == ClothEditMode.Select;
            _handleRow.style.display = selecting ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (KeyValuePair<SelectionHandle, ToolbarToggle> pair in _handleToggles)
            {
                Set(pair.Value, ClothEditHelp.HandleName(pair.Key), ClothEditHelp.HandleTip(pair.Key), settings.HandleType == pair.Key);
            }

            _pinRow.style.display = tool == ClothEditMode.Pin ? DisplayStyle.Flex : DisplayStyle.None;
            Set(_pinErase, L10n.Tr("塗ると外す", "Paint to erase"),
                L10n.Tr("オンにすると、ドラッグでピンを外し、Ctrl+ドラッグで留めます（マウスだけで操作したいときに）",
                    "When on, dragging removes pins and Ctrl+Drag adds them (for using only the mouse)"),
                owner.OverlayPinErase);

            Set(_symmetry, L10n.Tr("対称", "Symmetry"),
                L10n.Tr("対称編集のオン／オフ。オンにすると、左右の反対側にも同じように働きます（軸などは編集ウィンドウの「対称」で決めます）",
                    "Symmetric editing on / off. When on, tools also act mirrored on the opposite side (set the axis in the Symmetry section of the window)"),
                settings.SymmetryEnabled);
            bool displayToggles = ClothEditView.Shows(ClothEditUiItem.OverlayDisplayToggles);
            _stretch.style.display = displayToggles ? DisplayStyle.Flex : DisplayStyle.None;
            _original.style.display = displayToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Set(_stretch, L10n.Tr("伸びの表示", "Stretch"),
                L10n.Tr("布の伸びを、緑→黄→赤の点で表示します（0% → 5% → 15%）", "Shows the stretch of the cloth as green / yellow / red dots (0% / 5% / 15%)"),
                settings.ShowStretchHeatmap);
            Set(_original, L10n.Tr("元の形", "Original"),
                L10n.Tr("編集前の形を表示します。C キーを押している間だけ見ることもできます", "Shows the shape before editing. You can also hold the C key"),
                owner.OverlayShowOriginal);
            Set(_shortcuts, L10n.Tr("ショートカット", "Shortcuts"),
                L10n.Tr("ショートカット一覧を出す・隠す（F1）", "Show / hide the shortcut list (F1)"),
                owner.OverlayShortcutsVisible);
            _manual.text = L10n.Tr("説明書", "Manual");
            _manual.tooltip = L10n.Tr("使い方の説明書を開きます", "Opens the manual");
        }

        static void Set(ToolbarToggle toggle, string text, string tooltip, bool value)
        {
            toggle.text = text;
            toggle.tooltip = tooltip;
            toggle.SetValueWithoutNotify(value);
        }
    }
}
