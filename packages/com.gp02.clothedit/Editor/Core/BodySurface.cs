using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary><see cref="BodySurface"/> の検索の結果。</summary>
    public struct BodyHit
    {
        /// <summary>符号付き距離（メートル）。体の外が正、中が負。</summary>
        public float SignedDistance;

        /// <summary>体の表面で一番近い点。</summary>
        public Vector3 Point;

        /// <summary>
        /// <see cref="Point"/> での外向きの法線（長さ1）。頂点の法線（角度で重み付けしたもの）を重心座標で補ったなめらかな向き。
        /// 布の外向きと比べて、同じ側の面かを見るのに使う。
        /// </summary>
        public Vector3 Normal;

        /// <summary>当たった三角形（まとめた後の番号、<see cref="BodySurface.GetTriangle"/>）。</summary>
        public int Triangle;

        /// <summary>当たった三角形の島（つながった部品）。</summary>
        public int Island;
    }

    /// <summary>
    /// 体の表面（三角形メッシュ、ワールド座標）。点の符号付き距離（体の外が正）・一番近い点・外向きの法線を求める。
    ///
    /// 内外は、一番近い点の場所（面・辺・頂点）の擬似法線で決める（Bærentzen と Aanæs の方法）。
    /// 面の中なら面の向き、辺なら両側の面の向きの和、頂点なら角度で重み付けした周りの面の向きの和。
    /// 面の向きだけで決めると、辺や頂点の近くの点（とくに凸な角の外側）で内外を取り違えるため。
    /// 向きは三角形の巻き順から決める（体は外向きが表のはず）。同じ位置の頂点（UV の継ぎ目）はまとめてから求める。
    ///
    /// つながった部品（島）ごとに「中」を持ち、点の符号付き距離は島ごとの値の最小（部品の和の近似）。
    /// テスト用の体のように胴体と脚の筒が重なっていても、脚の筒の面が胴体の中に入り込んだ所で外と取り違えない。
    /// 平らな板のような島（中が決まらない）は使わない。巻き順が内向きの島は裏返して使う。
    /// 体のメッシュは閉じていないこともある（首の穴など）ので、検索は maxDistance（既定 5cm）以内に限り、
    /// 一番近い点が縁（穴の縁や裾）にある点は外とする。
    /// </summary>
    public sealed class BodySurface
    {
        /// <summary>検索する距離の既定値（メートル）。これより遠い所は対象外。</summary>
        public const float DefaultMaxDistance = 0.05f;

        /// <summary>同じ位置とみなして頂点をまとめる距離（メートル）。</summary>
        public const float WeldTolerance = 1e-5f;

        // 島の「中」が決まるとみなす、体積と面積の比（球で 1、平らな板で 0）。これより小さい島は使わない
        const float MinCompactness = 0.02f;

        sealed class Island
        {
            public ClosestTriangleTree Tree;
            public bool Solid;
            public bool Enabled;
            public int TriangleCount;
            public int VertexCount;
            public float Compactness;
            public int OutsideVotes;
            public int InsideVotes;
        }

        readonly Vector3[] _positions;      // まとめた後の頂点
        readonly int[] _triangles;          // まとめた後の頂点番号の3つ組（潰れた三角形は除く）
        readonly Vector3[] _faceNormals;    // 三角形の外向き（島が裏返しなら反転済み）
        readonly Vector3[] _vertexNormals;  // 角度で重み付けした頂点の擬似法線
        readonly Vector3[] _edgeNormals;    // 辺の擬似法線（両側の面の向きの和）
        readonly int[] _triangleEdges;      // 三角形ごとの3辺（0: a-b、1: b-c、2: c-a）の辺の番号
        readonly bool[] _edgeOnBoundary;    // 縁の辺（1枚の面だけが使う辺。穴の縁や裾）
        readonly bool[] _vertexOnBoundary;  // 縁の辺の端の頂点
        readonly int[] _triangleIsland;
        readonly int[] _vertexIsland;
        readonly Island[] _islands;
        readonly int[] _vertexToWelded;     // 渡された頂点ごとの、まとめた後の番号（SetVertexPositions で使う）
        readonly int[] _weldedSourceCount;  // まとめた後の頂点ごとの、渡された頂点の数
        readonly bool[] _islandFlipped;     // 巻き順が内向きなので裏返して使う島

        /// <summary>まとめた後の頂点の数。</summary>
        public int VertexCount => _positions.Length;

        /// <summary>まとめた後の三角形の数。</summary>
        public int TriangleCount => _triangles.Length / 3;

        /// <summary>島（つながった部品）の数。</summary>
        public int IslandCount => _islands.Length;

        /// <param name="vertices">体の頂点（ワールド座標）。複数のメッシュをまとめてよい</param>
        /// <param name="triangles">頂点番号の3つ組。外向きが表になる巻き順</param>
        public BodySurface(Vector3[] vertices, int[] triangles)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (triangles.Length % 3 != 0) throw new ArgumentException("三角形の頂点数が3の倍数ではありません。", nameof(triangles));
            for (int i = 0; i < triangles.Length; i++)
            {
                if ((uint)triangles[i] >= (uint)vertices.Length) throw new ArgumentException("三角形が、存在しない頂点を指しています。", nameof(triangles));
            }

            int[] vertexToWelded = VertexWelder.Weld(vertices, WeldTolerance, out _positions);
            int n = _positions.Length;
            _vertexToWelded = vertexToWelded;
            _weldedSourceCount = new int[n];
            for (int v = 0; v < vertexToWelded.Length; v++) _weldedSourceCount[vertexToWelded[v]]++;

            // 潰れた三角形（同じ頂点を2回使う、面積がほぼ 0）を除く
            var kept = new List<int>(triangles.Length);
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = vertexToWelded[triangles[t]], b = vertexToWelded[triangles[t + 1]], c = vertexToWelded[triangles[t + 2]];
                if (a == b || b == c || c == a) continue;
                Cross(_positions[a], _positions[b], _positions[c], out float nx, out float ny, out float nz);
                if (!(nx * nx + ny * ny + nz * nz > 1e-24f)) continue;
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }
            _triangles = kept.ToArray();
            int triangleCount = _triangles.Length / 3;

            // 島（頂点でつながった部品）
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int t = 0; t < triangleCount; t++)
            {
                Union(parent, _triangles[3 * t], _triangles[3 * t + 1]);
                Union(parent, _triangles[3 * t + 1], _triangles[3 * t + 2]);
            }
            var islandOfRoot = new Dictionary<int, int>();
            _vertexIsland = new int[n];
            var used = new bool[n];
            for (int k = 0; k < _triangles.Length; k++) used[_triangles[k]] = true;
            for (int i = 0; i < n; i++)
            {
                if (!used[i])
                {
                    _vertexIsland[i] = -1;
                    continue;
                }
                int root = Find(parent, i);
                if (!islandOfRoot.TryGetValue(root, out int island))
                {
                    island = islandOfRoot.Count;
                    islandOfRoot.Add(root, island);
                }
                _vertexIsland[i] = island;
            }
            int islandCount = islandOfRoot.Count;
            _triangleIsland = new int[triangleCount];
            for (int t = 0; t < triangleCount; t++) _triangleIsland[t] = _vertexIsland[_triangles[3 * t]];

            // 面の向き（巻き順）と、島ごとの体積（島の重心から測る）・面積
            _faceNormals = new Vector3[triangleCount];
            var faceAreas = new float[triangleCount];
            var centroid = new Vector3[islandCount];
            var centroidWeight = new float[islandCount];
            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 a = _positions[_triangles[3 * t]], b = _positions[_triangles[3 * t + 1]], c = _positions[_triangles[3 * t + 2]];
                Cross(a, b, c, out float nx, out float ny, out float nz);
                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                _faceNormals[t] = new Vector3(nx / length, ny / length, nz / length);
                faceAreas[t] = 0.5f * length;
                int island = _triangleIsland[t];
                float weight = faceAreas[t];
                centroid[island] += new Vector3((a.X + b.X + c.X) / 3f * weight, (a.Y + b.Y + c.Y) / 3f * weight, (a.Z + b.Z + c.Z) / 3f * weight);
                centroidWeight[island] += weight;
            }
            var volume = new double[islandCount];
            var area = new double[islandCount];
            for (int island = 0; island < islandCount; island++)
            {
                if (centroidWeight[island] > 0f) centroid[island] /= centroidWeight[island];
            }
            for (int t = 0; t < triangleCount; t++)
            {
                int island = _triangleIsland[t];
                Vector3 o = centroid[island];
                Vector3 a = _positions[_triangles[3 * t]] - o, b = _positions[_triangles[3 * t + 1]] - o, c = _positions[_triangles[3 * t + 2]] - o;
                volume[island] += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                area[island] += faceAreas[t];
            }

            _islands = new Island[islandCount];
            var flip = new bool[islandCount];
            for (int island = 0; island < islandCount; island++)
            {
                double a = area[island];
                double compactness = a > 0.0 ? 6.0 * Math.Sqrt(Math.PI) * volume[island] / Math.Pow(a, 1.5) : 0.0;
                flip[island] = compactness < 0.0;
                _islands[island] = new Island
                {
                    Compactness = (float)Math.Abs(compactness),
                    Solid = Math.Abs(compactness) >= MinCompactness,
                };
                _islands[island].Enabled = _islands[island].Solid;
            }
            for (int t = 0; t < triangleCount; t++)
            {
                if (flip[_triangleIsland[t]]) _faceNormals[t] = -_faceNormals[t];
            }
            _islandFlipped = flip;
            for (int i = 0; i < n; i++)
            {
                if (_vertexIsland[i] >= 0) _islands[_vertexIsland[i]].VertexCount++;
            }

            // 頂点の擬似法線: 周りの面の向きを、頂点での角度で重み付けして足す
            _vertexNormals = new Vector3[n];
            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 normal = _faceNormals[t];
                for (int corner = 0; corner < 3; corner++)
                {
                    int v = _triangles[3 * t + corner];
                    Vector3 p = _positions[v];
                    Vector3 e1 = _positions[_triangles[3 * t + (corner + 1) % 3]] - p;
                    Vector3 e2 = _positions[_triangles[3 * t + (corner + 2) % 3]] - p;
                    float angle = Angle(e1, e2);
                    _vertexNormals[v] += normal * angle;
                }
            }
            for (int i = 0; i < n; i++) _vertexNormals[i] = SafeNormalize(_vertexNormals[i]);

            // 辺の擬似法線: 辺を共有する面の向きの和（3枚以上が共有する辺も全部足す。縁の辺は1枚だけ）
            var edgeIndex = new Dictionary<long, int>(PairKeys.Comparer);
            var edgeSums = new List<Vector3>();
            var edgeFaces = new List<int>();
            _triangleEdges = new int[3 * triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    long key = PairKeys.Make(_triangles[3 * t + e], _triangles[3 * t + (e + 1) % 3]);
                    if (!edgeIndex.TryGetValue(key, out int edge))
                    {
                        edge = edgeSums.Count;
                        edgeIndex.Add(key, edge);
                        edgeSums.Add(Vector3.Zero);
                        edgeFaces.Add(0);
                    }
                    edgeSums[edge] += _faceNormals[t];
                    edgeFaces[edge]++;
                    _triangleEdges[3 * t + e] = edge;
                }
            }
            _edgeNormals = new Vector3[edgeSums.Count];
            for (int e = 0; e < _edgeNormals.Length; e++) _edgeNormals[e] = SafeNormalize(edgeSums[e]);
            _edgeOnBoundary = new bool[edgeSums.Count];
            _vertexOnBoundary = new bool[n];
            for (int t = 0; t < triangleCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int edge = _triangleEdges[3 * t + e];
                    if (edgeFaces[edge] != 1) continue;
                    _edgeOnBoundary[edge] = true;
                    _vertexOnBoundary[_triangles[3 * t + e]] = true;
                    _vertexOnBoundary[_triangles[3 * t + (e + 1) % 3]] = true;
                }
            }

            // 島ごとの木
            var islandTriangles = new List<int>[islandCount];
            for (int island = 0; island < islandCount; island++) islandTriangles[island] = new List<int>();
            for (int t = 0; t < triangleCount; t++) islandTriangles[_triangleIsland[t]].Add(t);
            for (int island = 0; island < islandCount; island++)
            {
                _islands[island].TriangleCount = islandTriangles[island].Count;
                _islands[island].Tree = new ClosestTriangleTree(_positions, _triangles, islandTriangles[island].ToArray());
            }
        }

        static void Cross(Vector3 a, Vector3 b, Vector3 c, out float nx, out float ny, out float nz)
        {
            float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
            nx = aby * acz - abz * acy;
            ny = abz * acx - abx * acz;
            nz = abx * acy - aby * acx;
        }

        static float Angle(Vector3 a, Vector3 b)
        {
            float la = a.Length(), lb = b.Length();
            if (!(la > 0f) || !(lb > 0f)) return 0f;
            float cos = Vector3.Dot(a, b) / (la * lb);
            return MathF.Acos(Math.Max(-1f, Math.Min(1f, cos)));
        }

        static Vector3 SafeNormalize(Vector3 v)
        {
            float length = v.Length();
            return length > 1e-20f ? v / length : Vector3.Zero;
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        // ---- 情報 ----

        /// <summary>まとめた後の頂点 i の位置。</summary>
        public Vector3 GetVertex(int i) => _positions[i];

        /// <summary>まとめた後の頂点 i の外向き（角度で重み付けした擬似法線）。</summary>
        public Vector3 GetVertexNormal(int i) => _vertexNormals[i];

        /// <summary>頂点 i の島。どの三角形にも使われていない頂点は -1。</summary>
        public int GetVertexIsland(int i) => _vertexIsland[i];

        /// <summary>三角形 t の3頂点（まとめた後の番号）。</summary>
        public void GetTriangle(int t, out int a, out int b, out int c)
        {
            a = _triangles[3 * t];
            b = _triangles[3 * t + 1];
            c = _triangles[3 * t + 2];
        }

        /// <summary>三角形 t の外向き。</summary>
        public Vector3 GetFaceNormal(int t) => _faceNormals[t];

        /// <summary>島が「中」を持つか（平らな板でない）。持たない島は検索に使わない。</summary>
        public bool IsIslandSolid(int island) => _islands[island].Solid;

        /// <summary>島を検索に使うか（<see cref="ClassifyIslands"/> で決める。作った直後は「中」を持つ島すべて）。</summary>
        public bool IsIslandEnabled(int island) => _islands[island].Enabled;

        /// <summary>島を検索に使うかを変える（「中」を持たない島は使えない）。</summary>
        public void SetIslandEnabled(int island, bool enabled) => _islands[island].Enabled = enabled && _islands[island].Solid;

        /// <summary>島の三角形の数。</summary>
        public int GetIslandTriangleCount(int island) => _islands[island].TriangleCount;

        /// <summary>島の頂点の数。</summary>
        public int GetIslandVertexCount(int island) => _islands[island].VertexCount;

        /// <summary>島の体積と面積の比（球で 1、平らな板で 0）。</summary>
        public float GetIslandCompactness(int island) => _islands[island].Compactness;

        /// <summary><see cref="ClassifyIslands"/> の票（布の点が島の外にあった数と中にあった数）。</summary>
        public void GetIslandVotes(int island, out int outside, out int inside)
        {
            outside = _islands[island].OutsideVotes;
            inside = _islands[island].InsideVotes;
        }

        /// <summary>検索に使う島の数。</summary>
        public int EnabledIslandCount
        {
            get
            {
                int count = 0;
                foreach (Island island in _islands)
                {
                    if (island.Enabled) count++;
                }
                return count;
            }
        }

        /// <summary>検索に使う島の頂点か。</summary>
        public bool IsVertexEnabled(int i)
        {
            int island = _vertexIsland[i];
            return island >= 0 && _islands[island].Enabled;
        }

        /// <summary>検索に使う島全体の境界箱。使う島が無ければ false。</summary>
        public bool TryGetBounds(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            bool any = false;
            foreach (Island island in _islands)
            {
                if (!island.Enabled || island.TriangleCount == 0) continue;
                island.Tree.GetBounds(out Vector3 a, out Vector3 b);
                min = Vector3.Min(min, a);
                max = Vector3.Max(max, b);
                any = true;
            }
            return any;
        }

        // ---- 検索 ----

        /// <summary>
        /// 点の符号付き距離（体の外が正）と、一番近い点・外向きの法線を求める。検索に使う島（<see cref="IsIslandEnabled"/>）の、
        /// maxDistance 以内の表面だけを調べる。島ごとの符号付き距離のうち一番小さいもの（一番深く入っている島、どこにも入っていなければ一番近い島）を返す。
        /// どの島の表面も maxDistance より遠ければ false。
        /// </summary>
        public bool TryQuery(Vector3 point, float maxDistance, out BodyHit hit)
        {
            hit = default;
            bool found = false;
            float px = point.X, py = point.Y, pz = point.Z;
            float limitSq = maxDistance * maxDistance;
            for (int island = 0; island < _islands.Length; island++)
            {
                Island data = _islands[island];
                if (!data.Enabled || data.TriangleCount == 0) continue;
                if (data.Tree.RootDistanceSquared(px, py, pz) > limitSq) continue;
                if (!QueryTree(data.Tree, island, px, py, pz, maxDistance, out BodyHit candidate)) continue;
                if (!found || candidate.SignedDistance < hit.SignedDistance)
                {
                    hit = candidate;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// <see cref="TryQuery"/> の、島を1つに限る版（検索に使うかどうかに関係なく調べる。「中」を持たない島は内外が当てにならない）。
        /// </summary>
        public bool TryQueryIsland(int island, Vector3 point, float maxDistance, out BodyHit hit)
        {
            hit = default;
            Island data = _islands[island];
            if (data.TriangleCount == 0) return false;
            if (data.Tree.RootDistanceSquared(point.X, point.Y, point.Z) > maxDistance * maxDistance) return false;
            return QueryTree(data.Tree, island, point.X, point.Y, point.Z, maxDistance, out hit);
        }

        bool QueryTree(ClosestTriangleTree tree, int island, float px, float py, float pz, float maxDistance, out BodyHit hit)
        {
            hit = default;
            if (!tree.FindClosest(_positions, px, py, pz, maxDistance, out int t, out float qx, out float qy, out float qz,
                out float u, out float v, out float w, out float distanceSquared))
            {
                return false;
            }

            // 一番近い点の場所（面・辺・頂点）に合った擬似法線で、内外を決める。
            // 閉じていない部品の縁（穴の縁や裾）が一番近いときは、内外が決まらないので外とする
            // （裾の下や穴の向こうの点を中と取り違えて、布を押し出さないため。内側の重ね着を体として使うときに起きる）
            Vector3 pseudo;
            bool rim;
            int zeros = (u == 0f ? 1 : 0) + (v == 0f ? 1 : 0) + (w == 0f ? 1 : 0);
            if (zeros >= 2)
            {
                int corner = u != 0f ? 0 : v != 0f ? 1 : 2;
                int vertex = _triangles[3 * t + corner];
                pseudo = _vertexNormals[vertex];
                rim = _vertexOnBoundary[vertex];
            }
            else if (zeros == 1)
            {
                int edge = _triangleEdges[3 * t + (w == 0f ? 0 : u == 0f ? 1 : 2)];
                pseudo = _edgeNormals[edge];
                rim = _edgeOnBoundary[edge];
            }
            else
            {
                pseudo = _faceNormals[t];
                rim = false;
            }
            float ox = px - qx, oy = py - qy, oz = pz - qz;
            float side = rim ? 1f : ox * pseudo.X + oy * pseudo.Y + oz * pseudo.Z;
            float distance = MathF.Sqrt(distanceSquared);

            Vector3 na = _vertexNormals[_triangles[3 * t]], nb = _vertexNormals[_triangles[3 * t + 1]], nc = _vertexNormals[_triangles[3 * t + 2]];
            float sx = u * na.X + v * nb.X + w * nc.X, sy = u * na.Y + v * nb.Y + w * nc.Y, sz = u * na.Z + v * nb.Z + w * nc.Z;
            float sl = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
            Vector3 smooth = sl > 1e-12f ? new Vector3(sx / sl, sy / sl, sz / sl) : _faceNormals[t];

            hit.SignedDistance = side >= 0f ? distance : -distance;
            hit.Point = new Vector3(qx, qy, qz);
            hit.Normal = smooth;
            hit.Triangle = t;
            hit.Island = island;
            return true;
        }

        /// <summary>
        /// 布の点（元の形の粒子など）から、どの島を「体」として使うかを決める。
        /// 島ごとに、maxDistance 以内にある布の点が島の外にあるか中にあるかを数え、外にある割合が minOutsideRatio 以上の島だけを使う。
        /// 体は布の内側にあるので、布の点はほとんど体の外にある（突き抜けている所だけが中）。
        /// 一方、布の外側にある別の服や髪の板は、布の点がその「中」（裏側）に来るので使わない
        /// （内側の服を編集しているときに、外側の上着を体とみなして布を上着の外へ押し出さないため）。
        /// 近くに布の点が1つも無い島も使わない。使う島の数を返す。
        /// </summary>
        public int ClassifyIslands(ReadOnlySpan<Vector3> clothPoints, float maxDistance = DefaultMaxDistance, float minOutsideRatio = 0.5f)
        {
            foreach (Island island in _islands)
            {
                island.OutsideVotes = 0;
                island.InsideVotes = 0;
            }
            float limitSq = maxDistance * maxDistance;
            for (int k = 0; k < clothPoints.Length; k++)
            {
                Vector3 p = clothPoints[k];
                for (int island = 0; island < _islands.Length; island++)
                {
                    Island data = _islands[island];
                    if (!data.Solid || data.TriangleCount == 0) continue;
                    if (data.Tree.RootDistanceSquared(p.X, p.Y, p.Z) > limitSq) continue;
                    if (!QueryTree(data.Tree, island, p.X, p.Y, p.Z, maxDistance, out BodyHit hit)) continue;
                    if (hit.SignedDistance >= 0f) data.OutsideVotes++;
                    else data.InsideVotes++;
                }
            }
            int enabled = 0;
            foreach (Island island in _islands)
            {
                int total = island.OutsideVotes + island.InsideVotes;
                island.Enabled = island.Solid && total > 0 && island.OutsideVotes >= minOutsideRatio * total;
                if (island.Enabled) enabled++;
            }
            return enabled;
        }

        /// <summary>
        /// 「中」を持つ島をすべて検索に使う。使う島の数を返す。
        /// 体だけを渡したとき（服を含まない）に使う。<see cref="ClassifyIslands"/> は布の近くの島しか使わないので、
        /// ポーズを変えると近づく島（座ったときの太ももなど）を落としてしまうため。
        /// </summary>
        public int EnableSolidIslands()
        {
            int enabled = 0;
            foreach (Island island in _islands)
            {
                island.Enabled = island.Solid;
                if (island.Enabled) enabled++;
            }
            return enabled;
        }

        /// <summary>作るときに渡した頂点の数（<see cref="SetVertexPositions"/> に渡す配列の長さ）。</summary>
        public int SourceVertexCount => _vertexToWelded.Length;

        /// <summary>
        /// 頂点の位置だけを入れ替える（ポーズを変えた体）。つながり・島・島を使うかどうか・裏返しの島は、作ったときのまま。
        /// 作ったときに同じ位置としてまとめた頂点は、新しい位置の平均にする（ウェイトの違いでポーズで少しずれても、1つの頂点のまま扱う）。
        /// 面・辺・頂点の向きを計算し直し、木の箱を更新する（木の形は作ったときの位置のまま。検索の結果は変わらず、遅くなるだけ）。
        /// ポーズごとに作り直すと、つながりの計算（頂点をまとめる・辺の表・島）を毎回やり直すことになり、大きな体では重いため。
        /// </summary>
        /// <param name="vertices">作るときと同じ並び・同じ数の頂点（ワールド座標）</param>
        public void SetVertexPositions(ReadOnlySpan<Vector3> vertices)
        {
            if (vertices.Length != _vertexToWelded.Length) throw new ArgumentException("頂点の数が、作ったときと違います。", nameof(vertices));
            int n = _positions.Length;
            for (int i = 0; i < n; i++) _positions[i] = Vector3.Zero;
            for (int v = 0; v < vertices.Length; v++)
            {
                int w = _vertexToWelded[v];
                Vector3 p = vertices[v];
                _positions[w] = new Vector3(_positions[w].X + p.X, _positions[w].Y + p.Y, _positions[w].Z + p.Z);
            }
            for (int i = 0; i < n; i++)
            {
                int count = _weldedSourceCount[i];
                if (count > 1) _positions[i] = new Vector3(_positions[i].X / count, _positions[i].Y / count, _positions[i].Z / count);
            }

            int triangleCount = _triangles.Length / 3;
            for (int t = 0; t < triangleCount; t++)
            {
                Cross(_positions[_triangles[3 * t]], _positions[_triangles[3 * t + 1]], _positions[_triangles[3 * t + 2]], out float nx, out float ny, out float nz);
                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                // ポーズで潰れた三角形は、向きを持たない（擬似法線に効かない）
                float scale = length > 1e-20f ? (_islandFlipped[_triangleIsland[t]] ? -1f : 1f) / length : 0f;
                _faceNormals[t] = new Vector3(nx * scale, ny * scale, nz * scale);
            }

            Array.Clear(_vertexNormals, 0, n);
            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 normal = _faceNormals[t];
                for (int corner = 0; corner < 3; corner++)
                {
                    int v = _triangles[3 * t + corner];
                    Vector3 p = _positions[v];
                    Vector3 e1 = _positions[_triangles[3 * t + (corner + 1) % 3]] - p;
                    Vector3 e2 = _positions[_triangles[3 * t + (corner + 2) % 3]] - p;
                    _vertexNormals[v] += normal * Angle(e1, e2);
                }
            }
            for (int i = 0; i < n; i++) _vertexNormals[i] = SafeNormalize(_vertexNormals[i]);

            Array.Clear(_edgeNormals, 0, _edgeNormals.Length);
            for (int t = 0; t < triangleCount; t++)
            {
                for (int e = 0; e < 3; e++) _edgeNormals[_triangleEdges[3 * t + e]] += _faceNormals[t];
            }
            for (int e = 0; e < _edgeNormals.Length; e++) _edgeNormals[e] = SafeNormalize(_edgeNormals[e]);

            foreach (Island island in _islands) island.Tree.Refit(_positions);
        }
    }
}
