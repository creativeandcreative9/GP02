using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 編集結果を、元メッシュの頂点ごとのシェイプキーの差分に変換する。
    /// Unity側では Mesh.AddBlendShapeFrame にこの差分を渡す。
    /// </summary>
    public static class BlendShapeBuilder
    {
        /// <param name="model">編集に使った布のモデル</param>
        /// <param name="editedPositions">編集後の粒子の位置</param>
        /// <param name="deltaVertices">頂点ごとの位置の差分（出力。元メッシュの頂点数）</param>
        /// <param name="deltaNormals">頂点ごとの法線の差分（出力。元メッシュの頂点数）</param>
        public static void Build(ClothModel model, ReadOnlySpan<Vector3> editedPositions,
            Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (editedPositions.Length != model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(editedPositions));
            int vertexCount = model.VertexToParticle.Length;
            if (deltaVertices == null || deltaVertices.Length != vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaVertices));
            if (deltaNormals == null || deltaNormals.Length != vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaNormals));

            // 法線は粒子単位で計算する。分割された頂点ごとに計算すると継ぎ目で差分がずれる。
            // 元メッシュの硬い角は、Unity側の元の法線に差分を足すことで保たれる。
            var restNormals = new Vector3[model.ParticleCount];
            var editedNormals = new Vector3[model.ParticleCount];
            MeshNormals.Compute(model.RestPositions, model.Triangles, restNormals);
            MeshNormals.Compute(editedPositions, model.Triangles, editedNormals);

            for (int v = 0; v < vertexCount; v++)
            {
                int p = model.VertexToParticle[v];
                deltaVertices[v] = editedPositions[p] - model.RestPositions[p];
                deltaNormals[v] = editedNormals[p] - restNormals[p];
            }
        }
    }
}
