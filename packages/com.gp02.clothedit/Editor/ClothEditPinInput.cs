namespace GP02.EditorTools
{
    /// <summary>ピン留めの道具のマウス操作の種類。</summary>
    public enum PinGesture
    {
        /// <summary>ドラッグで塗る。</summary>
        Paint,

        /// <summary>Shift＋ドラッグの矩形。</summary>
        Rectangle,

        /// <summary>ダブルクリックで、その部品（島）ごと。</summary>
        Island,
    }

    /// <summary>ピン留めの道具の1回の操作。何をするか（<see cref="Gesture"/>）と、留めるか外すか（<see cref="Erase"/>）。</summary>
    public readonly struct PinInput
    {
        public PinInput(PinGesture gesture, bool erase)
        {
            Gesture = gesture;
            Erase = erase;
        }

        public PinGesture Gesture { get; }

        /// <summary>true ならピンを外す操作、false なら留める操作。</summary>
        public bool Erase { get; }
    }

    /// <summary>
    /// ピン留めの道具の修飾キーの解釈。「選んで動かす」と同じ決まりにしてある:
    /// ドラッグ＝加える（留める）、Ctrl＋ドラッグ＝外す、Shift＋ドラッグ＝矩形、Ctrl＋Shift＋ドラッグ＝矩形で外す、
    /// ダブルクリック＝部品ごと（Ctrl で外す）。マウスだけの人のために、道具の「塗ると外す」（<paramref name="eraseMode"/>）をオンにすると、
    /// 何も押さないドラッグが「外す」になり、Ctrl を押すと「留める」になる（Ctrl は、今のモードの反対にする）。
    /// 画面に依存しないので、テストで確かめられる。
    /// </summary>
    public static class ClothEditPinInput
    {
        /// <summary>マウスを押したときの修飾キーとクリック回数から、操作を決める。</summary>
        /// <param name="clickCount">クリックの回数（2 以上でダブルクリック）</param>
        /// <param name="eraseMode">「塗ると外す」がオンか（<see cref="ClothEditMode.Unpin"/> もオン扱い）</param>
        public static PinInput Resolve(bool shift, bool control, int clickCount, bool eraseMode)
        {
            PinGesture gesture = shift ? PinGesture.Rectangle : clickCount >= 2 ? PinGesture.Island : PinGesture.Paint;
            return new PinInput(gesture, control != eraseMode);
        }

        /// <summary>
        /// 塗る操作（ドラッグ）が、ピンを外すものか。<see cref="ClothEditMode.Unpin"/>（保存値の互換で残してある道具）は
        /// 「ピン留め＋外す」として扱うので、「塗ると外す」がオンなのと同じ。
        /// </summary>
        public static bool IsErase(ClothEditMode tool, bool eraseMode, bool control)
        {
            return control != (eraseMode || tool == ClothEditMode.Unpin);
        }

        /// <summary>ピン留めの道具か（<see cref="ClothEditMode.Unpin"/> も、「ピン留め＋外す」として同じ道具に数える）。</summary>
        public static bool IsPinTool(ClothEditMode tool)
        {
            return tool == ClothEditMode.Pin || tool == ClothEditMode.Unpin;
        }
    }
}
