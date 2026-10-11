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

        internal void OverlaySetTool(ClothEditMode tool)
        {
            if (_settings.Tool == tool) return;
            _settings.Tool = tool;
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
