using System;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// Unity の Mesh と、計算の中核（System.Numerics）の間でデータを受け渡す。
    /// </summary>
    public static class MeshBridge
    {
        public static NVector3[] ToNumerics(Vector3[] source, Vector3 scale)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = new NVector3[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = new NVector3(source[i].x * scale.x, source[i].y * scale.y, source[i].z * scale.z);
            }
            return result;
        }

        /// <param name="inverseScale">ToNumerics で掛けた拡大率の逆数（メッシュの座標に戻すため）</param>
        public static Vector3[] ToUnity(NVector3[] source, Vector3 inverseScale)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = new Vector3[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = new Vector3(source[i].X * inverseScale.x, source[i].Y * inverseScale.y, source[i].Z * inverseScale.z);
            }
            return result;
        }

        /// <summary>
        /// メッシュにシェイプキーを1つ追加する。差分はメッシュの座標系で渡す。
        /// </summary>
        public static void AddBlendShape(Mesh mesh, string name, Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("シェイプキーの名前が空です。", nameof(name));
            if (mesh.GetBlendShapeIndex(name) >= 0) throw new ArgumentException($"同じ名前のシェイプキー「{name}」が既にあります。", nameof(name));
            if (deltaVertices == null || deltaVertices.Length != mesh.vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaVertices));
            if (deltaNormals == null || deltaNormals.Length != mesh.vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaNormals));

            mesh.AddBlendShapeFrame(name, 100f, deltaVertices, deltaNormals, null);
        }
    }
}
