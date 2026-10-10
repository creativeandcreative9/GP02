using System;
using System.Numerics;

namespace GP02.Core
{
    public static class MeshNormals
    {
        /// <summary>
        /// 面積で重み付けした頂点法線を計算する。
        /// 面のない頂点の法線はゼロベクトルになる。
        /// </summary>
        public static void Compute(ReadOnlySpan<Vector3> positions, int[] triangles, Vector3[] normals)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (normals == null) throw new ArgumentNullException(nameof(normals));
            if (normals.Length != positions.Length) throw new ArgumentException("法線の配列の長さが位置と一致しません。", nameof(normals));

            Array.Clear(normals, 0, normals.Length);
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t];
                int b = triangles[t + 1];
                int c = triangles[t + 2];
                // 外積の大きさは面積の2倍なので、正規化せずに足すと面積の重みになる
                Vector3 faceNormal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += faceNormal;
                normals[b] += faceNormal;
                normals[c] += faceNormal;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                float length = normals[i].Length();
                normals[i] = length > 1e-20f ? normals[i] / length : Vector3.Zero;
            }
        }
    }
}
