using System.Collections.Generic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// 同じアバター（<c>transform.root</c> の下）の、編集する布以外の有効なレンダラー（体・ほかの服など）の三角形を、
    /// 今のポーズのワールド座標で1つにまとめたもの。SkinnedMeshRenderer は BakeMesh で、ポーズとシェイプキーの重みが入った形にする。
    /// 体や内側の布までの隙間の測定（<see cref="RestGapMeasurer"/>）と、体の表面（<see cref="GP02.Core.BodySurface"/>）に使う。
    /// </summary>
    public sealed class OtherRendererMeshes
    {
        /// <summary>頂点（ワールド座標）。</summary>
        public NVector3[] Positions = new NVector3[0];

        /// <summary>頂点番号の3つ組（全レンダラーの全サブメッシュを順に並べたもの）。</summary>
        public int[] Triangles = new int[0];

        /// <summary>三角形を入れたレンダラー。</summary>
        public readonly List<Renderer> Renderers = new List<Renderer>();

        public int RendererCount => Renderers.Count;
        public int TriangleCount => Triangles.Length / 3;

        /// <summary>
        /// targets と同じアバター（transform.root の下）の、targets 以外の有効なレンダラーを集める。
        /// 非アクティブなオブジェクト・無効なレンダラー・読めないメッシュ（実行時の Read/Write 無効）は入れない。
        /// まとめたレンダラーが別々のアバターにあれば、それぞれの下を見る。
        /// </summary>
        public static OtherRendererMeshes Collect(IReadOnlyList<Renderer> targets)
        {
            var result = new OtherRendererMeshes();
            if (targets == null) return result;

            var allPositions = new List<NVector3>();
            var allTriangles = new List<int>();
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            var excluded = new HashSet<Renderer>();
            var roots = new List<Transform>();
            foreach (Renderer target in targets)
            {
                if (target == null) continue;
                excluded.Add(target);
                if (!roots.Contains(target.transform.root)) roots.Add(target.transform.root);
            }
            var others = new List<Renderer>();
            foreach (Transform root in roots) others.AddRange(root.GetComponentsInChildren<Renderer>(false));
            foreach (Renderer renderer in others)
            {
                if (excluded.Contains(renderer) || !renderer.enabled) continue;

                Mesh mesh;
                Matrix4x4 toWorld;
                Mesh baked = null;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) continue;
                    baked = new Mesh();
                    // スケールを含めて焼くと、レンダラーの位置と回転だけでワールド座標になる
                    skinned.BakeMesh(baked, true);
                    mesh = baked;
                    Transform t = skinned.transform;
                    toWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                }
                else if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    mesh = filter.sharedMesh;
                    toWorld = renderer.transform.localToWorldMatrix;
                }
                else
                {
                    continue;
                }

                try
                {
                    vertices.Clear();
                    mesh.GetVertices(vertices);
                    if (vertices.Count != mesh.vertexCount || vertices.Count == 0) continue;

                    int baseIndex = allPositions.Count;
                    for (int v = 0; v < vertices.Count; v++)
                    {
                        Vector3 w = toWorld.MultiplyPoint3x4(vertices[v]);
                        allPositions.Add(new NVector3(w.x, w.y, w.z));
                    }
                    int before = allTriangles.Count;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        if (mesh.GetTopology(sub) != UnityEngine.MeshTopology.Triangles) continue;
                        triangles.Clear();
                        mesh.GetTriangles(triangles, sub);
                        for (int k = 0; k < triangles.Count; k++) allTriangles.Add(baseIndex + triangles[k]);
                    }
                    if (allTriangles.Count > before) result.Renderers.Add(renderer);
                }
                finally
                {
                    if (baked != null) Object.DestroyImmediate(baked);
                }
            }

            result.Positions = allPositions.ToArray();
            result.Triangles = allTriangles.ToArray();
            return result;
        }
    }
}
