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
    /// シーンビューのショートカット。キーと操作の対応だけを持つ（画面に依存しないので、テストで確かめられる）。
    /// 実際にイベントを読んで消費するのはウィンドウ。
    /// </summary>
    public static class ClothEditShortcuts
    {
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
