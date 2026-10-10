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
        /// <param name="cloth">編集に使った布</param>
        /// <param name="particlePositions">編集後の粒子の位置</param>
        /// <param name="deltaVertices">頂点ごとの位置の差分（出力。元メッシュの頂点数）</param>
        /// <param name="deltaNormals">頂点ごとの法線の差分（出力。元メッシュの頂点数）</param>
        public static void Build(ParticleCloth cloth, ReadOnlySpan<Vector3> particlePositions,
            Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            MeshTopology topology = cloth.Topology;
            int vertexCount = topology.VertexToWelded.Length;
            if (deltaVertices == null || deltaVertices.Length != vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaVertices));
            if (deltaNormals == null || deltaNormals.Length != vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaNormals));

            var edited = new Vector3[topology.Positions.Length];
            cloth.DeformWelded(particlePositions, edited);

            // 法線は、まとめた後の頂点で計算する。分割された頂点ごとに計算すると継ぎ目で差分がずれる。
            // 元メッシュの硬い角は、Unity側の元の法線に差分を足すことで保たれる。
            var restNormals = new Vector3[edited.Length];
            var editedNormals = new Vector3[edited.Length];
            MeshNormals.Compute(topology.Positions, topology.Triangles, restNormals);
            MeshNormals.Compute(edited, topology.Triangles, editedNormals);

            for (int v = 0; v < vertexCount; v++)
            {
                int w = topology.VertexToWelded[v];
                deltaVertices[v] = edited[w] - topology.Positions[w];
                deltaNormals[v] = editedNormals[w] - restNormals[w];
            }
        }
    }
}
