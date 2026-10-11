using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>ホイールで変える大きさの種類。</summary>
    public enum WheelTarget
    {
        None,

        /// <summary>ブラシ半径（Ctrl＋ホイール）。</summary>
        BrushRadius,

        /// <summary>影響範囲（Ctrl＋Shift＋ホイール）。</summary>
        InfluenceRadius,
    }

    /// <summary><see cref="ClothEditShortcuts.ResolveToolKey"/> の結果。</summary>
    public readonly struct ToolKeyResult
    {
        public ToolKeyResult(ClothEditMode tool, SelectionHandle handle)
        {
            Handled = true;
            Tool = tool;
            Handle = handle;
        }

        /// <summary>キーが道具・ハンドルの切り替えとして使われたか。false なら、ほかの値は意味を持たない。</summary>
        public bool Handled { get; }

        /// <summary>切り替えたあとの道具。</summary>
        public ClothEditMode Tool { get; }

        /// <summary>切り替えたあとのハンドルの種類（「選んで動かす」の間の W/E/R）。</summary>
        public SelectionHandle Handle { get; }
    }

    /// <summary>
    /// ショートカットの動作。Unity の Shortcut Manager に登録する1つ1つ（<see cref="ClothEditShortcutBindings"/>）に対応する。
    /// </summary>
    public enum ClothEditShortcutAction
    {
        /// <summary>つまむ（「選んで動かす」の間は移動ハンドル）。</summary>
        Grab,

        /// <summary>回す（「選んで動かす」の間は回転ハンドル）。</summary>
        Rotate,

        /// <summary>拡大縮小（「選んで動かす」の間は拡大縮小ハンドル）。</summary>
        Scale,
        Pin,
        Unpin,
        Relax,

        /// <summary>「選んで動かす」（もう一度で前の道具）。</summary>
        SelectTool,

        /// <summary>押している間だけ元の形を見せる。</summary>
        HoldOriginal,

        /// <summary>ショートカット一覧を出す・隠す。</summary>
        ToggleHelp,
        BrushRadiusDown,
        BrushRadiusUp,
        InfluenceDown,
        InfluenceUp,

        /// <summary>マウスを横に動かしてブラシ半径を変える（「選んで動かす」の間は、なめらかさの半径）。</summary>
        RadiusDrag,

        /// <summary>マウスを横に動かして影響範囲を変える。</summary>
        InfluenceDrag,
    }

    /// <summary>
    /// シーンビューのショートカット。キーと操作の対応だけを持つ（画面に依存しないので、テストで確かめられる）。
    /// キーは Unity の Shortcut Manager に登録してあり（<see cref="ClothEditShortcutBindings"/>）、ここの判定は、それが使えないときの代わりと、
    /// 配列によってキーコードが違う [ ] の文字での判定に使う。
    /// </summary>
    public static class ClothEditShortcuts
    {
        /// <summary>
        /// 道具を切り替える動作の結果。<see cref="ResolveToolKey"/> の、キーの代わりに動作で受け取る版（Shortcut Manager から呼ばれる）。
        /// 「選んで動かす」の間は、つまむ・回す・拡大縮小が、道具ではなくハンドルの種類を切り替える。道具の動作でなければ Handled が false。
        /// </summary>
        public static ToolKeyResult ResolveToolAction(ClothEditShortcutAction action, ClothEditMode current, ClothEditMode previousTool, SelectionHandle handle)
        {
            bool selecting = current == ClothEditMode.Select;
            switch (action)
            {
                case ClothEditShortcutAction.Grab: return selecting ? new ToolKeyResult(current, SelectionHandle.Move) : new ToolKeyResult(ClothEditMode.Grab, handle);
                case ClothEditShortcutAction.Rotate: return selecting ? new ToolKeyResult(current, SelectionHandle.Rotate) : new ToolKeyResult(ClothEditMode.Rotate, handle);
                case ClothEditShortcutAction.Scale: return selecting ? new ToolKeyResult(current, SelectionHandle.Scale) : new ToolKeyResult(ClothEditMode.Scale, handle);
                case ClothEditShortcutAction.Pin: return new ToolKeyResult(ClothEditMode.Pin, handle);
                case ClothEditShortcutAction.Unpin: return new ToolKeyResult(ClothEditMode.Unpin, handle);
                case ClothEditShortcutAction.Relax: return new ToolKeyResult(ClothEditMode.Relax, handle);
                case ClothEditShortcutAction.SelectTool: return new ToolKeyResult(selecting ? LeaveSelectTool(previousTool) : ClothEditMode.Select, handle);
                default: return default;
            }
        }

        /// <summary>半径を1段変える動作なら、変える大きさとステップ（[ で -1、] で +1）。</summary>
        public static bool TryGetRadiusAction(ClothEditShortcutAction action, out WheelTarget target, out int step)
        {
            switch (action)
            {
                case ClothEditShortcutAction.BrushRadiusDown: target = WheelTarget.BrushRadius; step = -1; return true;
                case ClothEditShortcutAction.BrushRadiusUp: target = WheelTarget.BrushRadius; step = 1; return true;
                case ClothEditShortcutAction.InfluenceDown: target = WheelTarget.InfluenceRadius; step = -1; return true;
                case ClothEditShortcutAction.InfluenceUp: target = WheelTarget.InfluenceRadius; step = 1; return true;
                default: target = WheelTarget.None; step = 0; return false;
            }
        }

        /// <summary>F キー（Shift+F）での半径の変え方。Shortcut Manager が使えないときの代わり。</summary>
        public static bool IsRadiusDragKey(KeyCode key) => key == KeyCode.F;

        /// <summary>ホイール1目盛り（Event.delta.y で 3）が何ステップか。</summary>
        const float WheelDeltaPerStep = 3f;

        /// <summary>キーに対応する道具。W つまむ、E 回す、R 拡大縮小、P ピン留め（Shift で解除）、T 元に戻すブラシ。</summary>
        public static bool TryGetTool(KeyCode key, bool shift, out ClothEditMode tool)
        {
            switch (key)
            {
                case KeyCode.W: tool = ClothEditMode.Grab; return true;
                case KeyCode.E: tool = ClothEditMode.Rotate; return true;
                case KeyCode.R: tool = ClothEditMode.Scale; return true;
                case KeyCode.P: tool = shift ? ClothEditMode.Unpin : ClothEditMode.Pin; return true;
                case KeyCode.T: tool = ClothEditMode.Relax; return true;
                default:
                    tool = ClothEditMode.Grab;
                    return false;
            }
        }

        /// <summary>
        /// 道具のキーを押したときの結果。<see cref="TryGetTool"/> に、「選んで動かす」の分を足したもの。
        /// Q: 「選んで動かす」に切り替え、もう一度押すと直前の道具（previousTool）に戻る。
        /// 「選んで動かす」の間は、W/E/R が道具ではなくハンドルの種類（移動・回転・拡大縮小）を切り替える（道具はそのまま）。
        /// P・Shift+P・T は、いつでも今までどおり道具を変える。Shift は P のときだけ意味があり、ほかのキーに付いていたら何もしない。
        /// </summary>
        /// <param name="current">今の道具</param>
        /// <param name="previousTool">「選んで動かす」に入る前の道具（Q で戻る先）。「選んで動かす」自身なら、つまむに戻る</param>
        /// <param name="handle">今のハンドルの種類</param>
        public static ToolKeyResult ResolveToolKey(KeyCode key, bool shift, ClothEditMode current, ClothEditMode previousTool, SelectionHandle handle)
        {
            if (key == KeyCode.Q)
            {
                if (shift) return default;
                return new ToolKeyResult(current == ClothEditMode.Select ? LeaveSelectTool(previousTool) : ClothEditMode.Select, handle);
            }
            if (shift && key != KeyCode.P) return default;
            if (current == ClothEditMode.Select)
            {
                switch (key)
                {
                    case KeyCode.W: return new ToolKeyResult(current, SelectionHandle.Move);
                    case KeyCode.E: return new ToolKeyResult(current, SelectionHandle.Rotate);
                    case KeyCode.R: return new ToolKeyResult(current, SelectionHandle.Scale);
                }
            }
            return TryGetTool(key, shift, out ClothEditMode tool) ? new ToolKeyResult(tool, handle) : default;
        }

        /// <summary>「選んで動かす」をやめたときに戻る道具。前の道具が分からない（「選んで動かす」自身）なら、つまむ。</summary>
        public static ClothEditMode LeaveSelectTool(ClothEditMode previousTool)
        {
            return previousTool == ClothEditMode.Select ? ClothEditMode.Grab : previousTool;
        }

        /// <summary>押している間だけ元の形を見せるキー（C）。</summary>
        public static bool IsHoldOriginalKey(KeyCode key) => key == KeyCode.C;

        /// <summary>ショートカット一覧を出し入れするキー（F1）。</summary>
        public static bool IsHelpKey(KeyCode key) => key == KeyCode.F1;

        /// <summary>
        /// [ ] キーでの半径の変え方。[ で小さく（-1）、] で大きく（+1）。Shift を押していると影響範囲。
        /// 配列によってキーコードが違う（日本語配列など）ので、入力された文字も見る。
        /// </summary>
        public static bool TryGetRadiusKey(KeyCode key, char character, bool shift, out WheelTarget target, out int step)
        {
            step = 0;
            if (key == KeyCode.LeftBracket || character == '[' || character == '{') step = -1;
            else if (key == KeyCode.RightBracket || character == ']' || character == '}') step = 1;
            target = step == 0 ? WheelTarget.None : shift ? WheelTarget.InfluenceRadius : WheelTarget.BrushRadius;
            return step != 0;
        }

        /// <summary>修飾キーから、ホイールで変える大きさ。Ctrl＋ホイールで半径、Ctrl＋Shift＋ホイールで影響範囲。Ctrl なしは視点のズームなので何もしない。</summary>
        public static WheelTarget GetWheelTarget(bool control, bool shift)
        {
            if (!control) return WheelTarget.None;
            return shift ? WheelTarget.InfluenceRadius : WheelTarget.BrushRadius;
        }

        /// <summary>ホイールの <c>Event.delta.y</c>（下へ回すと正）から、<see cref="ClothEditController.AdjustRadius"/> に渡すステップ。上へ回すと大きくなる。</summary>
        public static float WheelSteps(float deltaY) => -deltaY / WheelDeltaPerStep;
    }
}
