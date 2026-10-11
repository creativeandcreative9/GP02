using UnityEditor;

namespace GP02.EditorTools
{
    // シーンビューの道具バー（ClothEditOverlay）から、ウィンドウの設定を変えるための入口。
    // 道具バーとウィンドウは同じ設定（_settings）を触るので、どちらで変えても同じになる。
    public sealed partial class ClothEditWindow
    {
        static ClothEditWindow _overlayOwner;

        /// <summary>道具バーが操作するウィンドウ。開いていなければ null。</summary>
        internal static ClothEditWindow OverlayOwner => _overlayOwner;

        /// <summary>編集中か。道具バーは、編集中だけ出す。</summary>
        internal static bool IsEditingNow => _overlayOwner != null && _overlayOwner._controller != null && _overlayOwner._controller.IsEditing;

        internal ClothEditSettings OverlaySettings => _settings;
        internal bool OverlayShortcutsVisible => _showShortcuts;
        internal bool OverlayShowOriginal => _controller != null && _controller.ShowOriginal;

        /// <summary>ピン留めの道具の「塗ると外す」がオンか（昔の「ピン解除」の道具もオン扱い）。</summary>
        internal bool OverlayPinErase => _settings.PinEraseMode || _settings.Tool == ClothEditMode.Unpin;

        internal void OverlaySetTool(ClothEditMode tool)
        {
            // 昔の「ピン解除」は、「塗ると外す」をオンにしたピン留めにする
            bool erase = _settings.PinEraseMode;
            if (tool == ClothEditMode.Unpin)
            {
                tool = ClothEditMode.Pin;
                erase = true;
            }
            if (_settings.Tool == tool && _settings.PinEraseMode == erase) return;
            RememberToolBeforeSelect();
            _settings.Tool = tool;
            _settings.PinEraseMode = erase;
            CommitOverlaySettings();
        }

        internal void OverlaySetPinErase(bool erase)
        {
            if (_settings.PinEraseMode == erase && _settings.Tool != ClothEditMode.Unpin) return;
            if (_settings.Tool == ClothEditMode.Unpin) _settings.Tool = ClothEditMode.Pin;
            _settings.PinEraseMode = erase;
            CommitOverlaySettings();
        }

        // 「選んで動かす」のボタン。入れると今の道具を覚え、切るとその道具に戻る（Q と同じ）
        internal void OverlaySetSelectTool(bool on)
        {
            RememberToolBeforeSelect();
            OverlaySetTool(on ? ClothEditMode.Select : ClothEditShortcuts.LeaveSelectTool(_toolBeforeSelect));
        }

        internal void OverlaySetHandle(SelectionHandle handle)
        {
            if (_settings.HandleType == handle) return;
            _settings.HandleType = handle;
            CommitOverlaySettings();
        }

        internal void OverlaySetSymmetry(bool enabled)
        {
            if (_settings.SymmetryEnabled == enabled) return;
            _settings.SymmetryEnabled = enabled;
            CommitOverlaySettings();
        }

        internal void OverlaySetStretchHeatmap(bool enabled)
        {
            if (_settings.ShowStretchHeatmap == enabled) return;
            _settings.ShowStretchHeatmap = enabled;
            CommitOverlaySettings();
        }

        internal void OverlaySetShowOriginal(bool enabled)
        {
            if (_controller == null || _controller.ShowOriginal == enabled) return;
            _controller.ShowOriginal = enabled;
            Repaint();
            SceneView.RepaintAll();
        }

        internal void OverlaySetShortcutsVisible(bool visible)
        {
            if (_showShortcuts == visible) return;
            _showShortcuts = visible;
            Repaint();
            SceneView.RepaintAll();
        }

        internal void OverlayOpenManual() => OpenManual();

        // ウィンドウの OnGUI が設定を保存するのと同じ手順を、すぐに行う
        void CommitOverlaySettings()
        {
            _settings.Sanitize();
            _settings.Save();
            if (_controller != null)
            {
                _controller.ApplySettings();
                _controller.InvalidatePreview();
            }
            Repaint();
            SceneView.RepaintAll();
        }
    }
}
