namespace GP02.EditorTools
{
    /// <summary>
    /// ビルド時の自動適用（<see cref="ShapeKeyApplyMode.NonDestructive"/>）が使えるかどうか。
    /// GP02.Editor は NDMF を参照しないので、NDMF 用の asmdef（GP02.Editor.NDMF）が読み込まれたときに、そちらから立てる。
    /// 素の Unity では false のまま。
    /// </summary>
    public static class NonDestructiveSupport
    {
        /// <summary>NDMF の連携が読み込まれていて、ビルド時に自動で適用されるなら true。</summary>
        public static bool IsAvailable { get; set; }
    }
}
