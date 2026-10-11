using System;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    // 動作チェック（ポーズを変えたときの貫通）のための部分: 編集中の形を入れた一時メッシュ（アバターの複製の服に付ける）、
    // ポーズで取り出した頂点を、まとめた後の頂点にすること、見つかった所を元のポーズで外へ逃がすこと
    public sealed partial class ClothEditSession
    {
        PoseCheckEvaluator _poseEvaluator;
        int[] _weldedSourceCount;

        /// <summary>まとめた後の頂点（<see cref="GP02.Core.MeshTopology.Positions"/>）の数。動作チェックはこの頂点で調べる。</summary>
        public int WeldedVertexCount => _toposRest.Length;

        /// <summary>動作チェックで、ポーズを付けた布と体の貫通を調べる部品（まとめた後の頂点と三角形）。最初に使うときに作る。</summary>
        public PoseCheckEvaluator PoseEvaluator => _poseEvaluator ?? (_poseEvaluator = PoseCheckEvaluator.FromCloth(_cloth));

        /// <summary>レンダラーごとの、まとめた頂点（<see cref="CurrentWorldVertices"/> の並び）での先頭。</summary>
        public int[] GetVertexStarts()
        {
            var starts = new int[_parts.Length];
            for (int r = 0; r < _parts.Length; r++) starts[r] = _parts[r].VertexStart;
            return starts;
        }

        /// <summary>
        /// rendererIndex 番目のレンダラーの元メッシュの複製に、今の編集の形（メッシュ座標の差分）を足した一時メッシュ（保存しない）。
        /// シェイプキーはそのまま残すので、同じ重みのレンダラーに付けて BakeMesh すると、今の編集の形をそのポーズで取り出せる（動作チェック）。
        /// 消すのは呼び出し側。
        /// </summary>
        public Mesh CreateEditedMesh(int rendererIndex) => CreateEditedMesh(rendererIndex, null);

        /// <summary>
        /// <see cref="CreateEditedMesh(int)"/> に、まとめた後の頂点ごとに、編集の姿勢のワールド座標で weldedWorldOffset だけ動かした分を足したもの
        /// （null なら足さない）。ポーズで動く割合を測るための「試しの形」に使う。
        /// </summary>
        public Mesh CreateEditedMesh(int rendererIndex, NVector3[] weldedWorldOffset)
        {
            if (weldedWorldOffset != null && weldedWorldOffset.Length != _toposRest.Length) throw new ArgumentException("まとめた後の頂点数が一致しません。", nameof(weldedWorldOffset));
            Part part = _parts[rendererIndex];
            var deltaVertices = new Vector3[part.VertexCount];
            var deltaNormals = new Vector3[part.VertexCount];
            BuildMeshSpaceDeltas(rendererIndex, deltaVertices, deltaNormals);
            if (weldedWorldOffset != null)
            {
                for (int k = 0; k < part.VertexCount; k++)
                {
                    int v = part.VertexStart + k;
                    NVector3 d = weldedWorldOffset[_vertexToWelded[v]];
                    Matrix4x4 inv = _inverseMatrices[v];
                    deltaVertices[k] += new Vector3(
                        inv.m00 * d.X + inv.m01 * d.Y + inv.m02 * d.Z,
                        inv.m10 * d.X + inv.m11 * d.Y + inv.m12 * d.Z,
                        inv.m20 * d.X + inv.m21 * d.Y + inv.m22 * d.Z);
                }
            }
            Mesh mesh = Object.Instantiate(part.SourceMesh);
            mesh.name = part.SourceMesh.name + (weldedWorldOffset != null ? "_GP02PoseProbe" : "_GP02PoseCheck");
            mesh.hideFlags = HideFlags.HideAndDontSave;
            Vector3[] vertices = part.SourceMesh.vertices;
            Vector3[] normals = part.SourceMesh.normals;
            for (int k = 0; k < vertices.Length; k++) vertices[k] += deltaVertices[k];
            mesh.vertices = vertices;
            if (normals.Length == vertices.Length)
            {
                for (int k = 0; k < normals.Length; k++)
                {
                    Vector3 n = normals[k] + deltaNormals[k];
                    normals[k] = n.sqrMagnitude > 1e-12f ? n.normalized : normals[k];
                }
                mesh.normals = normals;
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>今の形（編集の姿勢）の、まとめた後の頂点ごとの外向き（長さ1。決まらなければ 0）を normals に入れる。</summary>
        public void ComputeEditOutwardNormals(NVector3[] normals)
        {
            PoseEvaluator.ComputeOutwardNormals(_welded, normals);
        }

        /// <summary>
        /// まとめた頂点の並び（レンダラーの順。<see cref="GetVertexStarts"/>）の位置を、まとめた後の頂点の位置にする。
        /// 同じ位置としてまとめた頂点（UV の継ぎ目）は平均にする（ポーズで少しずれても、1つの頂点として調べるため）。
        /// </summary>
        public void ToWeldedPositions(NVector3[] combined, NVector3[] welded)
        {
            if (combined == null || combined.Length != _vertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(combined));
            if (welded == null || welded.Length != _toposRest.Length) throw new ArgumentException("まとめた後の頂点数が一致しません。", nameof(welded));
            if (_weldedSourceCount == null)
            {
                _weldedSourceCount = new int[_toposRest.Length];
                for (int v = 0; v < _vertexToWelded.Length; v++) _weldedSourceCount[_vertexToWelded[v]]++;
            }
            Array.Clear(welded, 0, welded.Length);
            for (int v = 0; v < combined.Length; v++)
            {
                int w = _vertexToWelded[v];
                welded[w] = new NVector3(welded[w].X + combined[v].X, welded[w].Y + combined[v].Y, welded[w].Z + combined[v].Z);
            }
            for (int w = 0; w < welded.Length; w++)
            {
                int count = _weldedSourceCount[w];
                if (count > 1) welded[w] = new NVector3(welded[w].X / count, welded[w].Y / count, welded[w].Z / count);
                else if (count == 0) welded[w] = _welded[w];
            }
        }

        /// <summary>
        /// 粒子ごとの量（メートル）だけ、今の面の外向きに粒子を逃がす（<see cref="PoseEscapeFixer"/>）。布の長さを保って周りも付いてくる。
        /// つまみは外れる。Undo の記録は呼び出し側（<see cref="ClothEditController.EscapePoseCheckPenetration"/>）が取る。
        /// </summary>
        public PoseEscapeResult EscapeOutward(float[] particlePush, PoseEscapeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _grabParticle = -1;
            _grabSet = null;
            _solver.Release();
            PoseEscapeResult result = PoseEscapeFixer.Fix(_cloth, _solver, particlePush, options);
            UpdateCurrentShape();
            return result;
        }
    }
}
