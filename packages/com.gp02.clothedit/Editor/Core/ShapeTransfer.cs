using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// シェイプキーの差分を、頂点数の違う別のメッシュへ写す。
    /// 写し先の頂点ごとに、元のメッシュの元の形の表面で一番近い点（三角形の上の点）を探し、その三角形の3頂点の差分を重心座標で混ぜる。
    /// 2つのメッシュは同じ座標系（同じ向き・大きさ・位置）にあるものとする。
    /// 対応の探索（<see cref="Map"/>）は1回で、位置・法線・接線・フレームの差分は同じ対応を使い回して写す（<see cref="Apply"/>）。
    /// </summary>
    public static class ShapeTransfer
    {
        /// <summary>写し先の頂点ごとの、元のメッシュの三角形と重心座標。</summary>
        public sealed class Mapping
        {
            /// <summary>写し先の頂点ごとの、元のメッシュの三角形の番号。範囲内に見つからなければ -1。</summary>
            public readonly int[] Triangles;

            /// <summary>写し先の頂点ごとの重心座標。見つからない頂点は 0。</summary>
            public readonly Vector3[] Barycentric;

            /// <summary>元のメッシュの頂点番号の3つ組（Apply で差分を引くため）。</summary>
            public readonly int[] SourceTriangleIndices;

            /// <summary>見つからなかった頂点の数。</summary>
            public readonly int MissCount;

            public int VertexCount => Triangles.Length;

            public Mapping(int[] triangles, Vector3[] barycentric, int[] sourceTriangleIndices)
            {
                Triangles = triangles;
                Barycentric = barycentric;
                SourceTriangleIndices = sourceTriangleIndices;
                int miss = 0;
                for (int i = 0; i < triangles.Length; i++)
                {
                    if (triangles[i] < 0) miss++;
                }
                MissCount = miss;
            }
        }

        /// <summary>
        /// 写し先の頂点ごとに、元のメッシュの元の形の表面で一番近い点を探す。
        /// </summary>
        /// <param name="sourceRest">元のメッシュの元の形の頂点</param>
        /// <param name="sourceTriangles">元のメッシュの三角形（頂点番号の3つ組）</param>
        /// <param name="targetPositions">写し先の頂点の位置</param>
        /// <param name="targetNormals">写し先の頂点の法線。null なら、写し先の三角形から計算する（targetTriangles が要る）</param>
        /// <param name="targetTriangles">写し先の三角形。targetNormals が null のときだけ使う</param>
        /// <param name="maxDistance">これより遠い頂点は写さない（差分 0）。メートル</param>
        /// <param name="minNormalDot">元の面の向きと写し先の法線の内積がこれ未満の面は避ける。0 なら逆向きの面を避ける。-1 以下で向きを問わない</param>
        public static Mapping Map(Vector3[] sourceRest, int[] sourceTriangles, Vector3[] targetPositions, Vector3[] targetNormals,
            int[] targetTriangles, float maxDistance, float minNormalDot = 0f)
        {
            if (sourceRest == null) throw new ArgumentNullException(nameof(sourceRest));
            if (sourceTriangles == null) throw new ArgumentNullException(nameof(sourceTriangles));
            if (targetPositions == null) throw new ArgumentNullException(nameof(targetPositions));
            if (maxDistance < 0f) throw new ArgumentOutOfRangeException(nameof(maxDistance));
            if (targetNormals != null && targetNormals.Length != targetPositions.Length)
            {
                throw new ArgumentException($"法線の数（{targetNormals.Length}）が頂点数（{targetPositions.Length}）と違います。", nameof(targetNormals));
            }

            bool useNormals = minNormalDot > -1f;
            Vector3[] normals = targetNormals;
            if (useNormals && normals == null)
            {
                if (targetTriangles == null) throw new ArgumentException("法線が無いときは、写し先の三角形が要ります。", nameof(targetTriangles));
                normals = new Vector3[targetPositions.Length];
                MeshNormals.Compute(targetPositions, targetTriangles, normals);
            }

            var search = new ClosestPointSearch(sourceRest, sourceTriangles);
            var triangles = new int[targetPositions.Length];
            var barycentric = new Vector3[targetPositions.Length];
            for (int v = 0; v < targetPositions.Length; v++)
            {
                bool hit;
                ClosestPointSearch.Hit result;
                // 法線が 0（面の無い頂点）なら向きでは絞れない
                if (useNormals && normals[v] != Vector3.Zero) hit = search.TryFindClosest(targetPositions[v], maxDistance, normals[v], minNormalDot, out result);
                else hit = search.TryFindClosest(targetPositions[v], maxDistance, out result);

                if (hit)
                {
                    triangles[v] = result.Triangle;
                    barycentric[v] = result.Barycentric;
                }
                else
                {
                    triangles[v] = -1;
                }
            }
            return new Mapping(triangles, barycentric, sourceTriangles);
        }

        /// <summary>
        /// 対応を使って、元のメッシュの頂点ごとの差分を、写し先の頂点ごとの差分にする。対応が見つからなかった頂点は 0。
        /// </summary>
        public static Vector3[] Apply(Mapping mapping, Vector3[] sourceDeltas)
        {
            if (mapping == null) throw new ArgumentNullException(nameof(mapping));
            if (sourceDeltas == null) throw new ArgumentNullException(nameof(sourceDeltas));

            int[] indices = mapping.SourceTriangleIndices;
            var result = new Vector3[mapping.VertexCount];
            for (int v = 0; v < result.Length; v++)
            {
                int t = mapping.Triangles[v];
                if (t < 0) continue;
                Vector3 w = mapping.Barycentric[v];
                Vector3 a = sourceDeltas[indices[3 * t]], b = sourceDeltas[indices[3 * t + 1]], c = sourceDeltas[indices[3 * t + 2]];
                result[v] = new Vector3(
                    w.X * a.X + w.Y * b.X + w.Z * c.X,
                    w.X * a.Y + w.Y * b.Y + w.Z * c.Y,
                    w.X * a.Z + w.Y * b.Z + w.Z * c.Z);
            }
            return result;
        }
    }
}
