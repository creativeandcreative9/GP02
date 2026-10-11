using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 布の頂点ごとの、編集の姿勢で一番近い体の表面の点（体の三角形と重心座標）と、そこからの距離。
    /// 「ウェイトを体に寄せる」（体の頂点のウェイトを、その点で補間して布の頂点に写す）と、
    /// 「布が体にぴったり付いていっても、そのポーズで体の中に入るか」（体どうしの重なり。<see cref="TryFollow"/>）を見るのに使う。
    /// 三角形の番号は、測ったときの <see cref="BodySurface"/> のもの（同じ体をポーズで動かしても変わらない）。
    /// </summary>
    public sealed class BodyAnchors
    {
        /// <summary>体を探す距離（メートル）の既定値。</summary>
        public const float DefaultMaxDistance = 0.1f;

        // 布の外向きと「布の頂点から体の点への向き」の内積がこれより大きい点は、布の外側にあるので使わない
        // （A ポーズで下ろした手のように、服の外にある体の部品のウェイトを写さないため）
        const float MaxOutsideAlignment = 0.3f;

        readonly int[] _triangle;
        readonly float[] _u, _v, _w;
        readonly float[] _distance;
        readonly Vector3[] _point;

        BodyAnchors(int count)
        {
            _point = new Vector3[count];
            _triangle = new int[count];
            _u = new float[count];
            _v = new float[count];
            _w = new float[count];
            _distance = new float[count];
            for (int i = 0; i < count; i++) _triangle[i] = -1;
        }

        /// <summary>布の頂点の数。</summary>
        public int VertexCount => _triangle.Length;

        /// <summary>頂点に体の点が見つかったか。</summary>
        public bool Has(int vertex) => _triangle[vertex] >= 0;

        /// <summary>頂点の体の点が乗っている体の三角形（<see cref="BodySurface.GetTriangle"/> の番号）。無ければ -1。</summary>
        public int GetTriangle(int vertex) => _triangle[vertex];

        /// <summary>頂点の体の点の、三角形の3頂点（a, b, c）に対する重心座標。</summary>
        public void GetBarycentric(int vertex, out float u, out float v, out float w)
        {
            u = _u[vertex];
            v = _v[vertex];
            w = _w[vertex];
        }

        /// <summary>頂点から体の点までの符号付き距離（メートル。体の外が正）。</summary>
        public float GetDistance(int vertex) => _distance[vertex];

        /// <summary>頂点の体の点の位置（測ったときの体の形での位置）。</summary>
        public Vector3 GetPoint(int vertex) => _point[vertex];

        /// <summary>体の点が見つかった頂点の数。</summary>
        public int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _triangle.Length; i++)
                {
                    if (_triangle[i] >= 0) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// 布の頂点（体と同じ座標）ごとに、maxDistance 以内で一番近い体の点を探す。
        /// outwardNormals（布の頂点ごとの外向き。決まらない頂点は 0）を渡すと、布の外側にある体の点は使わない（その頂点は「無し」）。
        /// </summary>
        public static BodyAnchors Measure(ReadOnlySpan<Vector3> vertices, BodySurface body, float maxDistance = DefaultMaxDistance, ReadOnlySpan<Vector3> outwardNormals = default)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (outwardNormals.Length != 0 && outwardNormals.Length != vertices.Length) throw new ArgumentException("外向きの数が頂点の数と合いません。", nameof(outwardNormals));
            var anchors = new BodyAnchors(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i];
                if (!body.TryQuery(p, maxDistance, out BodyHit hit)) continue;
                if (outwardNormals.Length != 0)
                {
                    Vector3 n = outwardNormals[i];
                    float dx = hit.Point.X - p.X, dy = hit.Point.Y - p.Y, dz = hit.Point.Z - p.Z;
                    float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (length > 1e-3f && (n.X * dx + n.Y * dy + n.Z * dz) / length > MaxOutsideAlignment) continue;
                }
                anchors._triangle[i] = hit.Triangle;
                anchors._u[i] = hit.U;
                anchors._v[i] = hit.V;
                anchors._w[i] = hit.W;
                anchors._distance[i] = hit.SignedDistance;
                anchors._point[i] = hit.Point;
            }
            return anchors;
        }

        /// <summary>
        /// ポーズを付けた体（測ったときと同じ <see cref="BodySurface"/> を <see cref="BodySurface.SetVertexPositions"/> で動かしたもの）で、
        /// 頂点が体にぴったり付いていったときの位置と向き。体の点（重心座標で補った位置）から、その点のなめらかな外向きに、
        /// 編集の姿勢での距離（minOffset より小さければ minOffset）だけ外。体の点が無ければ false。
        /// ウェイトを体の点のものにしたときの動きの近似（線形ブレンドスキニングで、体の点の周りを固く動かしたもの）。
        /// </summary>
        public bool TryFollow(int vertex, BodySurface posedBody, float minOffset, out Vector3 position, out Vector3 normal)
        {
            position = default;
            normal = default;
            int t = _triangle[vertex];
            if (t < 0 || posedBody == null || t >= posedBody.TriangleCount) return false;
            posedBody.GetTriangle(t, out int a, out int b, out int c);
            float u = _u[vertex], v = _v[vertex], w = _w[vertex];
            Vector3 pa = posedBody.GetVertex(a), pb = posedBody.GetVertex(b), pc = posedBody.GetVertex(c);
            Vector3 na = posedBody.GetVertexNormal(a), nb = posedBody.GetVertexNormal(b), nc = posedBody.GetVertexNormal(c);
            float nx = u * na.X + v * nb.X + w * nc.X, ny = u * na.Y + v * nb.Y + w * nc.Y, nz = u * na.Z + v * nb.Z + w * nc.Z;
            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 1e-12f)
            {
                normal = new Vector3(nx / length, ny / length, nz / length);
            }
            else
            {
                normal = posedBody.GetFaceNormal(t);
                if (normal == Vector3.Zero) return false;
            }
            float offset = Math.Max(_distance[vertex], minOffset);
            position = new Vector3(u * pa.X + v * pb.X + w * pc.X + normal.X * offset, u * pa.Y + v * pb.Y + w * pc.Y + normal.Y * offset,
                u * pa.Z + v * pb.Z + w * pc.Z + normal.Z * offset);
            return true;
        }
    }
}
