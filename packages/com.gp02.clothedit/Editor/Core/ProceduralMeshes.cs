using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// テストと見た目の確認に使う形状を作る。
    /// リポジトリに購入データを入れられないため、検証用の形はここで生成する。
    /// </summary>
    public static class ProceduralMeshes
    {
        /// <summary>
        /// XY平面上の長方形の布。左下が原点、Yが上。頂点は (columns+1)×(rows+1) 個。
        /// 頂点番号は 行 * (columns+1) + 列。
        /// </summary>
        public static void Grid(int columns, int rows, float width, float height,
            out Vector3[] vertices, out int[] triangles)
        {
            if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
            if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));

            int stride = columns + 1;
            vertices = new Vector3[stride * (rows + 1)];
            for (int r = 0; r <= rows; r++)
            for (int c = 0; c <= columns; c++)
            {
                vertices[r * stride + c] = new Vector3(width * c / columns, height * r / rows, 0f);
            }

            triangles = new int[columns * rows * 6];
            int t = 0;
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                int i = r * stride + c;
                triangles[t++] = i;
                triangles[t++] = i + stride;
                triangles[t++] = i + 1;
                triangles[t++] = i + 1;
                triangles[t++] = i + stride;
                triangles[t++] = i + stride + 1;
            }
        }

        /// <summary>
        /// Grid と同じ形だが、中央の列で頂点を分割した布（UVの継ぎ目を模したもの）。
        /// 継ぎ目の頂点は同じ位置に2つずつある。
        /// </summary>
        public static void GridWithSeam(int columns, int rows, float width, float height,
            out Vector3[] vertices, out int[] triangles)
        {
            if (columns < 2 || columns % 2 != 0) throw new ArgumentOutOfRangeException(nameof(columns), "2以上の偶数を指定してください。");

            Grid(columns, rows, width, height, out var gridVertices, out var gridTriangles);
            int stride = columns + 1;
            int seamColumn = columns / 2;

            // 継ぎ目の列の頂点を複製し、右半分の三角形は複製側を使う
            vertices = new Vector3[gridVertices.Length + rows + 1];
            Array.Copy(gridVertices, vertices, gridVertices.Length);
            var duplicateOf = new int[gridVertices.Length];
            for (int i = 0; i < duplicateOf.Length; i++) duplicateOf[i] = i;
            for (int r = 0; r <= rows; r++)
            {
                int original = r * stride + seamColumn;
                int duplicate = gridVertices.Length + r;
                vertices[duplicate] = gridVertices[original];
                duplicateOf[original] = duplicate;
            }

            triangles = (int[])gridTriangles.Clone();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                // 三角形の重心が継ぎ目より右なら右半分
                float centerX = (gridVertices[triangles[t]].X + gridVertices[triangles[t + 1]].X + gridVertices[triangles[t + 2]].X) / 3f;
                if (centerX <= width * seamColumn / columns) continue;
                for (int k = 0; k < 3; k++) triangles[t + k] = duplicateOf[triangles[t + k]];
            }
        }
    }
}
