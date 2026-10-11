using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>ピン留めの範囲指定（Shift＋ドラッグの矩形）で、画面上の矩形を扱う部分。画面に依存しないので、テストで確かめられる。</summary>
    public static class ClothEditPinRange
    {
        /// <summary>これより小さい矩形（ピクセル）は、ドラッグではなく手ぶれとみなして何もしない。</summary>
        public const float MinPixels = 4f;

        /// <summary>
        /// シーンビューの GUI の矩形（点単位、左上が原点）を、ビューポート座標（0〜1、左下が原点）にする。
        /// </summary>
        /// <param name="pixelWidth">カメラの描画領域の幅（ピクセル）</param>
        /// <param name="pixelHeight">カメラの描画領域の高さ（ピクセル）</param>
        /// <param name="pixelsPerPoint">GUI の 1 点が何ピクセルか（<c>EditorGUIUtility.pixelsPerPoint</c>）</param>
        public static Rect GuiRectToViewport(Rect guiRect, float pixelWidth, float pixelHeight, float pixelsPerPoint)
        {
            float xMin = guiRect.xMin * pixelsPerPoint / pixelWidth;
            float xMax = guiRect.xMax * pixelsPerPoint / pixelWidth;
            float yMin = 1f - guiRect.yMax * pixelsPerPoint / pixelHeight;
            float yMax = 1f - guiRect.yMin * pixelsPerPoint / pixelHeight;
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        /// <summary>2点（ドラッグの始点と今の位置）から、向きによらない矩形を作る。</summary>
        public static Rect FromCorners(Vector2 a, Vector2 b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>矩形が、範囲指定として使える大きさか。</summary>
        public static bool IsBigEnough(Rect guiRect, float pixelsPerPoint)
        {
            return guiRect.width * pixelsPerPoint >= MinPixels && guiRect.height * pixelsPerPoint >= MinPixels;
        }
    }
}
