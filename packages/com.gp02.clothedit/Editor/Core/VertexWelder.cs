using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 同じ位置にある頂点を1つの粒子にまとめる。
    /// Unityのメッシュは UV の継ぎ目などで頂点が分割されているため、
    /// まとめずに計算すると継ぎ目で布が裂ける。
    /// </summary>
    public static class VertexWelder
    {
        /// <summary>
        /// 頂点ごとの粒子番号を返す。粒子の位置は、その粒子に最初に割り当てられた頂点の位置とする。
        /// </summary>
        public static int[] Weld(Vector3[] vertices, float tolerance, out Vector3[] particlePositions)
        {
            return Weld(vertices, tolerance, null, out particlePositions);
        }

        /// <summary>
        /// <see cref="Weld(Vector3[], float, out Vector3[])"/> の、まとめてよい頂点の組を指定する版。
        /// groups[v] が同じ頂点どうしだけをまとめる（別々のレンダラーを1つの布にしたとき、たまたま同じ位置にある頂点で部品がつながらないように）。
        /// </summary>
        /// <param name="groups">頂点ごとの組の番号。null なら全頂点を1つの組とみなす</param>
        public static int[] Weld(Vector3[] vertices, float tolerance, int[] groups, out Vector3[] particlePositions)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (tolerance <= 0f) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (groups != null && groups.Length != vertices.Length) throw new ArgumentException("頂点数が一致しません。", nameof(groups));
            var particleGroup = groups != null ? new List<int>(vertices.Length) : null;

            var vertexToParticle = new int[vertices.Length];
            var positions = new List<Vector3>(vertices.Length);
            var cells = new Dictionary<(int, int, int), List<int>>();
            float toleranceSq = tolerance * tolerance;

            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 p = vertices[v];
                var cell = CellOf(p, tolerance);
                int found = -1;

                // 許容誤差内の点は隣のセルにまたがることがあるので、周囲27セルを調べる
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                for (int dy = -1; dy <= 1 && found < 0; dy++)
                for (int dz = -1; dz <= 1 && found < 0; dz++)
                {
                    if (!cells.TryGetValue((cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz), out var list)) continue;
                    foreach (int particle in list)
                    {
                        if (particleGroup != null && particleGroup[particle] != groups[v]) continue;
                        if (Vector3.DistanceSquared(positions[particle], p) <= toleranceSq)
                        {
                            found = particle;
                            break;
                        }
                    }
                }

                if (found < 0)
                {
                    found = positions.Count;
                    positions.Add(p);
                    particleGroup?.Add(groups[v]);
                    if (!cells.TryGetValue(cell, out var list))
                    {
                        list = new List<int>();
                        cells.Add(cell, list);
                    }
                    list.Add(found);
                }

                vertexToParticle[v] = found;
            }

            particlePositions = positions.ToArray();
            return vertexToParticle;
        }

        static (int, int, int) CellOf(Vector3 p, float size)
        {
            return ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size));
        }
    }
}
