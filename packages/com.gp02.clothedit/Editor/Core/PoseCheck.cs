using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>動作チェック（ポーズを変えたときの、体と布の貫通）の調べ方の設定。</summary>
    public sealed class PoseCheckOptions
    {
        /// <summary>許容の深さ（メートル）の既定値。</summary>
        public const float DefaultTolerance = 0.001f;

        /// <summary>
        /// 許容の深さ（メートル）。これより深い貫通だけを数える。
        /// 基準の姿勢でも突き抜けていた頂点は、基準の深さよりこれだけ深くなったときだけ数える（ポーズで起きた所だけを見るため。
        /// 今の姿勢の貫通は「貫通を直す」の受け持ち）。
        /// </summary>
        public float Tolerance = DefaultTolerance;

        /// <summary>布の頂点から体を探す距離（メートル）の既定値。太ももが布を丸ごと突き抜けるような深い貫通も測れるよう、静止した姿勢の検出（5cm）より長い。</summary>
        public const float DefaultMaxDistance = 0.08f;

        /// <summary>
        /// 布の頂点から体を探す距離（メートル）。これより深く体に入った頂点は、貫通として見つからない（閉じていない体の、穴の向こうを中と取り違えないため、遠い所は見ない）。
        /// </summary>
        public float MaxDistance = DefaultMaxDistance;

        /// <summary>体の頂点が布の外へ出ているかを見るときの、布の面からの距離（メートル）。</summary>
        public float PokeDistance = BodySurface.DefaultMaxDistance;

        /// <summary>
        /// 体の頂点が布の外へ出ているかを見るとき、体の頂点の外向きと布の面の外向きの内積がこれより小さいものは見ない
        /// （袖の内側の面と、その向こうの胴体のように、向かい合った面を突き抜けと取り違えないため）。
        /// </summary>
        public float MinAlignment = PenetrationOptions.DefaultMinAlignment;

        /// <summary>体の頂点が布の外へ出ているか（体の尖った所が布の頂点の間を抜けていないか）も調べる。</summary>
        public bool CheckBodyVertices = true;

        /// <summary>表示用の点（<see cref="PosePenetration.Points"/>）の上限。</summary>
        public int MaxPoints = 1500;

        public PoseCheckOptions Clone() => (PoseCheckOptions)MemberwiseClone();
    }

    /// <summary>1つのポーズ（の1段階）を調べた結果（<see cref="PoseCheckEvaluator.Evaluate"/>）。</summary>
    public sealed class PosePenetration
    {
        /// <summary>呼び出し側のポーズの番号（同じポーズの段階をまとめて数えるため）。</summary>
        public int Group = -1;

        /// <summary>呼び出し側の段階（0.5 で半分、1 で全部など）。</summary>
        public float Stage = 1f;

        /// <summary>体があったか。無ければ何も調べていない。</summary>
        public bool HasBody;

        /// <summary>貫通として数えた布の頂点（まとめた後の番号、小さい順）。</summary>
        public readonly List<int> Vertices = new List<int>();

        /// <summary><see cref="Vertices"/> と同じ順の、貫通の深さ（メートル）。</summary>
        public readonly List<float> Depths = new List<float>();

        /// <summary><see cref="Vertices"/> と同じ順の、その頂点が入っている「か所」の番号（0 〜 <see cref="RegionCount"/> - 1）。</summary>
        public readonly List<int> VertexRegions = new List<int>();

        /// <summary>か所ごとの、一番深い頂点の深さ（メートル）。</summary>
        public readonly List<float> RegionDepths = new List<float>();

        /// <summary>
        /// <see cref="Vertices"/> と同じ順の、編集の姿勢で布を外向きに動かしたとき、このポーズで外向きに動く割合（<see cref="PoseCheckEvaluator.MeasureGains"/>）。
        /// 1 なら同じだけ動く。ウェイトが混ざった関節（ひざ・ひじ）を大きく曲げると小さくなる（線形ブレンドスキニングで、外向きの動きが縮むため）。
        /// 測っていなければ空。
        /// </summary>
        public readonly List<float> Gains = new List<float>();

        /// <summary>貫通している所の数（数えた頂点の、三角形の辺でつながったまとまりの数）。</summary>
        public int RegionCount;

        /// <summary>数えた頂点のうち、一番深いものの深さ（メートル）。無ければ 0。</summary>
        public float MaxDepth;

        /// <summary>布の外へ出ていた体の頂点のうち、数えた所に関わるものの数。</summary>
        public int BodyVertexCount;

        /// <summary>表示用の点（そのポーズでのワールド座標。数えた布の頂点と、布の外へ出た体の頂点。多いときは間引く）。</summary>
        public readonly List<Vector3> Points = new List<Vector3>();

        /// <summary>調べるのにかかった時間（ミリ秒）。</summary>
        public double Milliseconds;

        /// <summary>貫通として数えた頂点の数。</summary>
        public int PenetratingCount => Vertices.Count;

        /// <summary>貫通が見つかったか。</summary>
        public bool IsPenetrating => Vertices.Count > 0;

        public void Clear()
        {
            HasBody = false;
            Vertices.Clear();
            Depths.Clear();
            VertexRegions.Clear();
            RegionDepths.Clear();
            Gains.Clear();
            Points.Clear();
            RegionCount = 0;
            MaxDepth = 0f;
            BodyVertexCount = 0;
            Milliseconds = 0.0;
        }

        public override string ToString()
        {
            if (!HasBody) return "体が無いので調べていません";
            return $"貫通 {RegionCount} か所（頂点 {PenetratingCount}、最大 {MaxDepth * 1000f:F1} mm、布の外に出た体の頂点 {BodyVertexCount}）（{Milliseconds:F0} ms）";
        }
    }

    /// <summary>
    /// ポーズを付けた布と体の貫通を調べる（動作チェック）。布はメッシュの頂点（同じ位置をまとめた後の頂点と三角形）で調べる。
    /// 粒子ではなく頂点で見るのは、ポーズで動くのはスキニングされた頂点で、見た目に出るのも頂点だから。
    /// 次の2つを調べ、頂点ごとの深さを出す。
    /// (a) 布の頂点が体の中にある（体の符号付き距離が負）。向きは見ない。体の中にあれば、どちら向きの面でも見た目に突き抜けるため。
    /// (b) 体の頂点が、布の面より外（布の外向きの側）に出ている（体の尖った所が布の頂点の間を抜けている）。その面の3頂点の深さにする。
    ///     向かい合った面（袖の内側と胴体など）を取り違えないよう、体の外向きと布の外向きがだいたい同じ向きのものだけを見る。
    ///     一番近い布の点が布の縁（開いた裾・袖口）の上にあるものは見ない（裾の下から外へ出た足のように、縁の向こうにあるだけのため）。
    /// 布の外向きは、三角形の巻き順の向きに <see cref="PenetrationDetector.ComputeTriangleSigns"/> の符号を掛けたもの（スキニングで巻き順は変わらない）。
    /// 基準の姿勢の深さを渡すと、それより許容の深さ以上深くなった頂点だけを数える（今の姿勢から突き抜けている所は、ポーズのせいではないため）。
    /// 数えた頂点は、三角形の辺でつながったものを1か所にまとめる。重ね着の布どうしの貫通は数えない（体だけ）。
    /// </summary>
    public sealed class PoseCheckEvaluator
    {
        readonly int _vertexCount;
        readonly int[] _triangles;
        readonly sbyte[] _triangleSign;
        readonly bool[] _boundaryEdge;      // 三角形ごとの3辺（0: a-b、1: b-c、2: c-a）が、布の縁（1枚の三角形だけが使う辺）か
        readonly bool[] _boundaryVertex;    // 布の縁の辺の端の頂点
        readonly Vector3[] _positions;      // 調べている形（まとめた後の頂点）
        readonly float[] _depth;            // 頂点ごとの深さ（作業用）
        readonly int[] _parent;             // まとまりを数える作業用
        readonly List<Vector3> _pokePoints = new List<Vector3>();
        readonly List<int> _pokeTriangles = new List<int>();
        Vector3[] _normalWork;
        ClosestTriangleTree _tree;          // 布の三角形（体の頂点から一番近い布の点を探す）。最初の形で作り、あとは箱だけ更新する

        /// <summary>布の頂点（まとめた後）の数。</summary>
        public int VertexCount => _vertexCount;

        /// <summary>布の三角形（まとめた後の頂点番号の3つ組）。</summary>
        public int[] Triangles => _triangles;

        /// <param name="vertexCount">布の頂点の数</param>
        /// <param name="triangles">頂点番号の3つ組</param>
        /// <param name="triangleSign">三角形ごとの、巻き順の向きが外向きなら +1、逆なら -1、決まらなければ 0。null なら全部 0（(b) では体の向きの側を外とみなす）</param>
        public PoseCheckEvaluator(int vertexCount, int[] triangles, sbyte[] triangleSign = null)
        {
            if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (triangles.Length % 3 != 0) throw new ArgumentException("三角形の頂点数が3の倍数ではありません。", nameof(triangles));
            for (int k = 0; k < triangles.Length; k++)
            {
                if ((uint)triangles[k] >= (uint)vertexCount) throw new ArgumentException("三角形が、存在しない頂点を指しています。", nameof(triangles));
            }
            if (triangleSign != null && triangleSign.Length != triangles.Length / 3) throw new ArgumentException("三角形の数と合いません。", nameof(triangleSign));
            _vertexCount = vertexCount;
            _triangles = triangles;
            _triangleSign = triangleSign ?? new sbyte[triangles.Length / 3];
            _positions = new Vector3[vertexCount];
            _depth = new float[vertexCount];
            _parent = new int[vertexCount];

            var edgeUse = new Dictionary<long, int>(PairKeys.Comparer);
            for (int k = 0; k < triangles.Length; k += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    long key = PairKeys.Make(triangles[k + e], triangles[k + (e + 1) % 3]);
                    edgeUse.TryGetValue(key, out int count);
                    edgeUse[key] = count + 1;
                }
            }
            _boundaryEdge = new bool[triangles.Length];
            _boundaryVertex = new bool[vertexCount];
            for (int k = 0; k < triangles.Length; k += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[k + e], b = triangles[k + (e + 1) % 3];
                    if (edgeUse[PairKeys.Make(a, b)] != 1) continue;
                    _boundaryEdge[k + e] = true;
                    _boundaryVertex[a] = true;
                    _boundaryVertex[b] = true;
                }
            }
        }

        // 一番近い布の点（三角形 t の重心座標 u, v, w）が、布の縁（開いた裾・袖口・襟）の上か
        bool IsOnBoundary(int t, float u, float v, float w)
        {
            int zeros = (u == 0f ? 1 : 0) + (v == 0f ? 1 : 0) + (w == 0f ? 1 : 0);
            if (zeros >= 2) return _boundaryVertex[_triangles[3 * t + (u != 0f ? 0 : v != 0f ? 1 : 2)]];
            if (zeros == 1) return _boundaryEdge[3 * t + (w == 0f ? 0 : u == 0f ? 1 : 2)];
            return false;
        }

        /// <summary>布（粒子化したもの）の、まとめた後の頂点と三角形で調べる部品を作る。</summary>
        public static PoseCheckEvaluator FromCloth(ParticleCloth cloth)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            return new PoseCheckEvaluator(cloth.Topology.Positions.Length, cloth.Topology.Triangles, PenetrationDetector.ComputeTriangleSigns(cloth));
        }

        /// <summary>
        /// 布の頂点の位置（ワールド座標）で、頂点ごとの貫通の深さを測り、depth に入れる（貫通していなければ 0）。
        /// 体が null か、検索に使う島が無ければ、すべて 0 で false を返す。
        /// </summary>
        public bool MeasureDepth(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options, float[] depth)
        {
            if (depth == null || depth.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(depth));
            bool hasBody = Measure(clothVertices, body, options);
            Array.Copy(_depth, depth, _vertexCount);
            return hasBody;
        }

        // 深さを _depth に、布の外へ出た体の頂点を _pokePoints・_pokeTriangles に入れる
        bool Measure(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (clothVertices.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(clothVertices));
            Array.Clear(_depth, 0, _vertexCount);
            _pokePoints.Clear();
            _pokeTriangles.Clear();
            if (body == null || body.EnabledIslandCount == 0) return false;

            clothVertices.CopyTo(_positions);
            float maxDistance = options.MaxDistance;

            // (a) 布の頂点が体の中にある
            for (int i = 0; i < _vertexCount; i++)
            {
                if (!body.TryQuery(_positions[i], maxDistance, out BodyHit hit)) continue;
                if (hit.SignedDistance < 0f) _depth[i] = -hit.SignedDistance;
            }

            // (b) 体の頂点が、布の面の外へ出ている
            if (options.CheckBodyVertices && _triangles.Length > 0)
            {
                if (_tree == null) _tree = new ClosestTriangleTree(_positions, _triangles);
                else _tree.Refit(_positions);
                float pokeDistance = options.PokeDistance;
                float limitSq = pokeDistance * pokeDistance;
                float minAlignment = options.MinAlignment;
                for (int v = 0; v < body.VertexCount; v++)
                {
                    if (!body.IsVertexEnabled(v)) continue;
                    Vector3 p = body.GetVertex(v);
                    if (_tree.RootDistanceSquared(p.X, p.Y, p.Z) > limitSq) continue;
                    if (!_tree.FindClosest(_positions, p.X, p.Y, p.Z, pokeDistance, out int t, out float qx, out float qy, out float qz,
                        out float bu, out float bv, out float bw, out float distanceSquared)) continue;
                    // 一番近い布の点が縁の上なら、体の頂点は縁の向こう（裾の下の足・袖口の先の手など）にあるので、布の外とは言えない
                    if (IsOnBoundary(t, bu, bv, bw)) continue;
                    FaceNormal(t, out float nx, out float ny, out float nz);
                    Vector3 bodyNormal = body.GetVertexNormal(v);
                    float alignment = nx * bodyNormal.X + ny * bodyNormal.Y + nz * bodyNormal.Z;
                    int sign = _triangleSign[t];
                    if (sign == 0) sign = alignment >= 0f ? 1 : -1;
                    if (sign * alignment < minAlignment) continue;
                    float along = ((p.X - qx) * nx + (p.Y - qy) * ny + (p.Z - qz) * nz) * sign;
                    if (along <= PenetrationReport.CountTolerance) continue;
                    // 一番近い布の点が布の縁にあり、体の頂点が縁の向こう（面に沿った向き）にあるものは除く（裾の下の脚・袖口の先の手など）
                    if (distanceSquared - along * along > 0.25f * distanceSquared) continue;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int w = _triangles[3 * t + corner];
                        if (along > _depth[w]) _depth[w] = along;
                    }
                    _pokePoints.Add(p);
                    _pokeTriangles.Add(t);
                }
            }
            return true;
        }

        /// <summary>
        /// 布の頂点の位置（ワールド座標）で貫通を調べ、result に入れる（result は先に空にする）。
        /// baselineDepth（基準の姿勢の <see cref="MeasureDepth"/> の結果。null なら 0）より options.Tolerance 以上深い頂点を数え、
        /// 三角形の辺でつながったものを1か所にまとめる。result の Group と Stage は呼び出し側が入れる（ここでは変えない）。
        /// </summary>
        public void Evaluate(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options, float[] baselineDepth, PosePenetration result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (baselineDepth != null && baselineDepth.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(baselineDepth));
            var timer = Stopwatch.StartNew();
            int group = result.Group;
            float stage = result.Stage;
            result.Clear();
            result.Group = group;
            result.Stage = stage;
            result.HasBody = Measure(clothVertices, body, options);
            if (!result.HasBody)
            {
                result.Milliseconds = timer.Elapsed.TotalMilliseconds;
                return;
            }

            float tolerance = Math.Max(0f, options.Tolerance);
            for (int i = 0; i < _vertexCount; i++)
            {
                float baseline = baselineDepth != null ? baselineDepth[i] : 0f;
                if (!(_depth[i] > baseline + tolerance)) continue;
                result.Vertices.Add(i);
                result.Depths.Add(_depth[i]);
                if (_depth[i] > result.MaxDepth) result.MaxDepth = _depth[i];
            }
            result.RegionCount = CountRegions(baselineDepth, tolerance);
            LabelRegions(result);
            CollectPoints(result, baselineDepth, tolerance, Math.Max(0, options.MaxPoints));
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
        }

        bool IsCounted(int vertex, float[] baselineDepth, float tolerance)
        {
            float baseline = baselineDepth != null ? baselineDepth[vertex] : 0f;
            return _depth[vertex] > baseline + tolerance;
        }

        // 数えた頂点の、三角形の辺でつながったまとまりの数
        int CountRegions(float[] baselineDepth, float tolerance)
        {
            int[] parent = _parent;
            for (int i = 0; i < _vertexCount; i++) parent[i] = i;
            for (int t = 0; t < _triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = _triangles[t + e], b = _triangles[t + (e + 1) % 3];
                    if (IsCounted(a, baselineDepth, tolerance) && IsCounted(b, baselineDepth, tolerance)) Union(parent, a, b);
                }
            }
            int regions = 0;
            for (int i = 0; i < _vertexCount; i++)
            {
                if (IsCounted(i, baselineDepth, tolerance) && Find(parent, i) == i) regions++;
            }
            return regions;
        }

        // 数えた頂点に「か所」の番号を振り、か所ごとの一番深い深さを出す（CountRegions のあとに呼ぶ。根の番号の小さい順に 0, 1, …）
        void LabelRegions(PosePenetration result)
        {
            var labelOfRoot = new Dictionary<int, int>();
            for (int k = 0; k < result.Vertices.Count; k++)
            {
                int root = Find(_parent, result.Vertices[k]);
                if (!labelOfRoot.TryGetValue(root, out int label))
                {
                    label = result.RegionDepths.Count;
                    labelOfRoot.Add(root, label);
                    result.RegionDepths.Add(0f);
                }
                result.VertexRegions.Add(label);
                if (result.Depths[k] > result.RegionDepths[label]) result.RegionDepths[label] = result.Depths[k];
            }
        }

        /// <summary>
        /// 布の頂点の外向き（長さ1）を normals に入れる。周りの三角形の向き（面積で重み付け）に、外向きの符号を掛けて足したもの。
        /// 外向きが決まらない頂点（周りの三角形の符号がすべて 0）は 0 ベクトル。
        /// </summary>
        public void ComputeOutwardNormals(ReadOnlySpan<Vector3> clothVertices, Vector3[] normals)
        {
            if (clothVertices.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(clothVertices));
            if (normals == null || normals.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(normals));
            Array.Clear(normals, 0, _vertexCount);
            for (int t = 0; t < _triangles.Length / 3; t++)
            {
                int sign = _triangleSign[t];
                if (sign == 0) continue;
                int ia = _triangles[3 * t], ib = _triangles[3 * t + 1], ic = _triangles[3 * t + 2];
                Vector3 a = clothVertices[ia], b = clothVertices[ib], c = clothVertices[ic];
                float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
                float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
                var n = new Vector3((aby * acz - abz * acy) * sign, (abz * acx - abx * acz) * sign, (abx * acy - aby * acx) * sign);
                normals[ia] += n;
                normals[ib] += n;
                normals[ic] += n;
            }
            for (int i = 0; i < _vertexCount; i++)
            {
                float length = normals[i].Length();
                normals[i] = length > 1e-20f ? normals[i] / length : Vector3.Zero;
            }
        }

        /// <summary>
        /// result の数えた頂点ごとに、編集の姿勢で外向きに動かしたとき、このポーズで外向きに動く割合を測り、<see cref="PosePenetration.Gains"/> に入れる。
        /// probeVertices は、編集の姿勢で全頂点を外向きに probeDistance だけ動かした布を、同じポーズで取り出したもの（clothVertices と同じ並び）。
        /// スキニングは頂点の位置について線形なので、この割合は動かす量によらない。割合は、ポーズでの頂点の外向きの成分（外向きが決まらなければ 1）。
        /// </summary>
        public void MeasureGains(ReadOnlySpan<Vector3> clothVertices, ReadOnlySpan<Vector3> probeVertices, float probeDistance, PosePenetration result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (probeVertices.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(probeVertices));
            if (!(probeDistance > 0f)) throw new ArgumentOutOfRangeException(nameof(probeDistance));
            result.Gains.Clear();
            if (result.Vertices.Count == 0) return;
            if (_normalWork == null) _normalWork = new Vector3[_vertexCount];
            ComputeOutwardNormals(clothVertices, _normalWork);
            for (int k = 0; k < result.Vertices.Count; k++)
            {
                int v = result.Vertices[k];
                Vector3 n = _normalWork[v];
                if (n == Vector3.Zero)
                {
                    result.Gains.Add(1f);
                    continue;
                }
                Vector3 moved = probeVertices[v] - clothVertices[v];
                result.Gains.Add(Vector3.Dot(moved, n) / probeDistance);
            }
        }

        // 表示用の点: 数えた布の頂点（4分の3まで）と、数えた所に関わる体の頂点
        void CollectPoints(PosePenetration result, float[] baselineDepth, float tolerance, int maxPoints)
        {
            var related = _pokePoints;
            int pokeKept = 0;
            for (int k = 0; k < related.Count; k++)
            {
                int t = _pokeTriangles[k];
                bool counted = IsCounted(_triangles[3 * t], baselineDepth, tolerance) || IsCounted(_triangles[3 * t + 1], baselineDepth, tolerance)
                    || IsCounted(_triangles[3 * t + 2], baselineDepth, tolerance);
                if (!counted) continue;
                related[pokeKept] = related[k];
                _pokeTriangles[pokeKept] = t;
                pokeKept++;
            }
            related.RemoveRange(pokeKept, related.Count - pokeKept);
            _pokeTriangles.RemoveRange(pokeKept, _pokeTriangles.Count - pokeKept);
            result.BodyVertexCount = pokeKept;
            if (maxPoints == 0) return;

            int vertexBudget = pokeKept > 0 ? maxPoints * 3 / 4 : maxPoints;
            int count = result.Vertices.Count;
            int stride = count <= vertexBudget ? 1 : (count + vertexBudget - 1) / Math.Max(1, vertexBudget);
            for (int k = 0; k < count && result.Points.Count < vertexBudget; k += stride) result.Points.Add(_positions[result.Vertices[k]]);
            int pokeBudget = maxPoints - result.Points.Count;
            if (pokeBudget <= 0 || pokeKept == 0) return;
            int pokeStride = pokeKept <= pokeBudget ? 1 : (pokeKept + pokeBudget - 1) / pokeBudget;
            for (int k = 0; k < pokeKept && result.Points.Count < maxPoints; k += pokeStride) result.Points.Add(related[k]);
        }

        // 調べている形の三角形 t の巻き順の向き（長さ1）
        void FaceNormal(int t, out float nx, out float ny, out float nz)
        {
            Vector3 a = _positions[_triangles[3 * t]], b = _positions[_triangles[3 * t + 1]], c = _positions[_triangles[3 * t + 2]];
            float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
            nx = aby * acz - abz * acy;
            ny = abz * acx - abx * acz;
            nz = abx * acy - aby * acx;
            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 1e-20f)
            {
                nx /= length;
                ny /= length;
                nz /= length;
            }
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
    }

    /// <summary>
    /// 動作チェックの全ポーズの集計。ポーズ（段階）ごとの結果を順に足すと、頂点ごとの最悪の深さと、それが起きた結果の番号を持つ。
    /// </summary>
    public sealed class PoseCheckSummary
    {
        readonly float[] _worstDepth;
        readonly int[] _worstEntry;
        readonly List<PosePenetration> _entries = new List<PosePenetration>();

        public PoseCheckSummary(int vertexCount)
        {
            if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
            _worstDepth = new float[vertexCount];
            _worstEntry = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) _worstEntry[i] = -1;
        }

        /// <summary>布の頂点（まとめた後）の数。</summary>
        public int VertexCount => _worstDepth.Length;

        /// <summary>足した結果（足した順）。</summary>
        public IReadOnlyList<PosePenetration> Entries => _entries;

        /// <summary>頂点ごとの、全ポーズでの最悪の深さ（メートル。数えられなかった頂点は 0）。書き換えないこと。</summary>
        public float[] WorstDepth => _worstDepth;

        /// <summary>頂点ごとの、最悪の深さが起きた結果の番号（<see cref="Entries"/> の番号。無ければ -1）。書き換えないこと。</summary>
        public int[] WorstEntry => _worstEntry;

        /// <summary>結果を足す。頂点ごとの最悪の深さを更新し、結果の番号を返す。</summary>
        public int Add(PosePenetration entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            int index = _entries.Count;
            _entries.Add(entry);
            for (int k = 0; k < entry.Vertices.Count; k++)
            {
                int v = entry.Vertices[k];
                if ((uint)v >= (uint)_worstDepth.Length) continue;
                float depth = entry.Depths[k];
                if (depth > _worstDepth[v])
                {
                    _worstDepth[v] = depth;
                    _worstEntry[v] = index;
                }
            }
            return index;
        }

        /// <summary>貫通が見つかった結果の数。</summary>
        public int PenetratingEntryCount
        {
            get
            {
                int count = 0;
                foreach (PosePenetration entry in _entries)
                {
                    if (entry.IsPenetrating) count++;
                }
                return count;
            }
        }

        /// <summary>貫通が見つかったポーズの数（<see cref="PosePenetration.Group"/> が同じ結果は1つと数える）。</summary>
        public int PenetratingGroupCount
        {
            get
            {
                var groups = new HashSet<int>();
                foreach (PosePenetration entry in _entries)
                {
                    if (entry.IsPenetrating) groups.Add(entry.Group);
                }
                return groups.Count;
            }
        }

        /// <summary>全結果の、貫通している所の数の合計。</summary>
        public int TotalRegionCount
        {
            get
            {
                int count = 0;
                foreach (PosePenetration entry in _entries) count += entry.RegionCount;
                return count;
            }
        }

        /// <summary>全結果で一番深い貫通の深さ（メートル）。</summary>
        public float MaxDepth
        {
            get
            {
                float max = 0f;
                foreach (PosePenetration entry in _entries) max = Math.Max(max, entry.MaxDepth);
                return max;
            }
        }

        /// <summary>どれかのポーズで貫通として数えられた頂点の数。</summary>
        public int PenetratingVertexCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _worstDepth.Length; i++)
                {
                    if (_worstDepth[i] > 0f) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// 貫通が見つかった結果の番号を、悪い順（一番深い所の深さ、同じなら所の数、同じなら足した順）に並べて返す。
        /// </summary>
        public int[] RankEntries()
        {
            var ranked = new List<int>();
            for (int k = 0; k < _entries.Count; k++)
            {
                if (_entries[k].IsPenetrating) ranked.Add(k);
            }
            ranked.Sort((a, b) =>
            {
                int byDepth = _entries[b].MaxDepth.CompareTo(_entries[a].MaxDepth);
                if (byDepth != 0) return byDepth;
                int byRegions = _entries[b].RegionCount.CompareTo(_entries[a].RegionCount);
                return byRegions != 0 ? byRegions : a.CompareTo(b);
            });
            return ranked.ToArray();
        }

        /// <summary>
        /// 粒子ごとの最悪の深さを particleDepth に入れる。粒子は、その粒子に結びついた頂点（<see cref="ParticleCloth.GetBoundParticle"/>）の
        /// 最悪の深さのうち一番深いもの。頂点を外へ動かすには、その頂点に結びついた粒子をすべて動かせばよいので、一番深いものに合わせる。
        /// </summary>
        public void ToParticleDepth(ParticleCloth cloth, float[] particleDepth)
        {
            if (cloth != null && cloth.Topology.Positions.Length != _worstDepth.Length) throw new ArgumentException("布の頂点の数が、集計と合いません。", nameof(cloth));
            ToParticles(cloth, _worstDepth, particleDepth);
        }

        /// <summary>
        /// 頂点（まとめた後）ごとの値を、粒子ごとの値にする。粒子は、その粒子に結びついた頂点の値のうち一番大きいもの（結びついた頂点が無ければ 0）。
        /// </summary>
        public static void ToParticles(ParticleCloth cloth, float[] weldedValues, float[] particleValues)
        {
            if (cloth == null) throw new ArgumentNullException(nameof(cloth));
            if (weldedValues == null || weldedValues.Length != cloth.Topology.Positions.Length) throw new ArgumentException("頂点の数が一致しません。", nameof(weldedValues));
            if (particleValues == null || particleValues.Length != cloth.Model.ParticleCount) throw new ArgumentException("粒子数が一致しません。", nameof(particleValues));
            Array.Clear(particleValues, 0, particleValues.Length);
            for (int w = 0; w < weldedValues.Length; w++)
            {
                float value = weldedValues[w];
                if (!(value > 0f)) continue;
                int bound = cloth.BoundParticleCount(w);
                for (int k = 0; k < bound; k++)
                {
                    int i = cloth.GetBoundParticle(w, k, out float weight);
                    if (weight > 0f && value > particleValues[i]) particleValues[i] = value;
                }
            }
        }

        /// <summary>
        /// 「元のポーズで外へ逃がす」の量を決める。ポーズ・段階ごとの「か所」ごとに、頂点の「深さ＋余白」をポーズで動く割合（<see cref="PosePenetration.Gains"/>）で
        /// 割った量（編集の姿勢でこれだけ動かせば、そのポーズで足りる量）を求める。
        /// か所の中に割合が minGain より小さい頂点がある（編集の姿勢で動かしても、ポーズではほとんど動かない。ウェイトが原因）か、
        /// 量が maxPush を超える（深すぎる）か所は、か所ごと逃がさない（一部だけ動かすと、深い所の周りに輪のような凹凸が残るため）。
        /// 頂点ごとに、逃がすか所の量のうち一番大きいものを返す。割合を測っていない結果は、割合 1 とみなす。
        /// </summary>
        public PoseEscapePlan PlanEscape(float extra, float maxPush, float minGain)
        {
            var plan = new PoseEscapePlan { WeldedPush = new float[_worstDepth.Length], AllowedRegions = new bool[_entries.Count][] };
            extra = Math.Max(0f, extra);
            for (int e = 0; e < _entries.Count; e++)
            {
                PosePenetration entry = _entries[e];
                int regions = entry.RegionDepths.Count;
                if (regions == 0) continue;
                var required = new float[regions];
                var lowGain = new bool[regions];
                bool measured = entry.Gains.Count == entry.Vertices.Count;
                for (int k = 0; k < entry.Vertices.Count; k++)
                {
                    int r = entry.VertexRegions[k];
                    float gain = measured ? entry.Gains[k] : 1f;
                    if (!(gain >= minGain))
                    {
                        lowGain[r] = true;
                        continue;
                    }
                    required[r] = Math.Max(required[r], (entry.Depths[k] + extra) / gain);
                }
                var allowed = new bool[regions];
                plan.AllowedRegions[e] = allowed;
                for (int r = 0; r < regions; r++)
                {
                    if (lowGain[r]) plan.LowGainRegions++;
                    else if (required[r] > maxPush) plan.TooFarRegions++;
                    else
                    {
                        allowed[r] = true;
                        plan.PushedRegions++;
                    }
                }
                for (int k = 0; k < entry.Vertices.Count; k++)
                {
                    int r = entry.VertexRegions[k];
                    if (!allowed[r]) continue;
                    int v = entry.Vertices[k];
                    if ((uint)v >= (uint)plan.WeldedPush.Length) continue;
                    float push = (entry.Depths[k] + extra) / (measured ? entry.Gains[k] : 1f);
                    if (push > plan.WeldedPush[v]) plan.WeldedPush[v] = push;
                }
            }
            return plan;
        }
    }

    /// <summary><see cref="PoseCheckSummary.PlanEscape"/> の結果: 頂点ごとの外へ逃がす量と、か所の数（ポーズ・段階ごとに数える）。</summary>
    public sealed class PoseEscapePlan
    {
        /// <summary>まとめた後の頂点ごとの、編集の姿勢で外向きに動かす量（メートル。逃がさない頂点は 0）。</summary>
        public float[] WeldedPush;

        /// <summary>結果（<see cref="PoseCheckSummary.Entries"/> の番号）ごとの、逃がすか所（か所の番号ごとに true）。か所の無い結果は null。</summary>
        public bool[][] AllowedRegions;

        /// <summary>逃がすか所の数。</summary>
        public int PushedRegions;

        /// <summary>編集の姿勢で動かしても、ポーズではほとんど動かないので逃がさないか所の数（ウェイトが原因）。</summary>
        public int LowGainRegions;

        /// <summary>動かす量が上限を超えるので逃がさないか所の数（深すぎる。たいていウェイトが原因）。</summary>
        public int TooFarRegions;

        /// <summary>逃がさないか所の数。</summary>
        public int SkippedRegions => LowGainRegions + TooFarRegions;

        /// <summary>
        /// 逃がしたあとに同じポーズ・段階を同じ順に調べ直した集計 after で、逃がしたか所（ポーズごとの頂点）がどれだけ浅くなったかと、
        /// 全ポーズの深さの合計（1つの深さは depthCap で頭打ち）の前後を測る。before はこの計画を作った集計。
        /// 頭打ちにするのは、逃がさない深い所（ウェイトが原因）が、周りの布がわずかに動いただけで合計を大きく動かさないようにするため。
        /// </summary>
        public PoseEscapeEffect MeasureEffect(PoseCheckSummary before, PoseCheckSummary after, float depthCap)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            var effect = new PoseEscapeEffect();
            foreach (PosePenetration entry in before.Entries)
            {
                for (int k = 0; k < entry.Depths.Count; k++) effect.TotalDepthBefore += Math.Min(entry.Depths[k], depthCap);
            }
            foreach (PosePenetration entry in after.Entries)
            {
                for (int k = 0; k < entry.Depths.Count; k++) effect.TotalDepthAfter += Math.Min(entry.Depths[k], depthCap);
            }
            var depthAfter = new float[Math.Max(before.VertexCount, after.VertexCount)];
            double sumBefore = 0.0, sumAfter = 0.0;
            int count = Math.Min(before.Entries.Count, after.Entries.Count);
            for (int e = 0; e < count && e < AllowedRegions.Length; e++)
            {
                bool[] allowed = AllowedRegions[e];
                if (allowed == null) continue;
                PosePenetration was = before.Entries[e], now = after.Entries[e];
                for (int k = 0; k < now.Vertices.Count; k++) depthAfter[now.Vertices[k]] = now.Depths[k];
                for (int k = 0; k < was.Vertices.Count; k++)
                {
                    int r = was.VertexRegions[k];
                    if (r < 0 || r >= allowed.Length || !allowed[r]) continue;
                    float d = depthAfter[was.Vertices[k]];
                    effect.PushedPairs++;
                    sumBefore += was.Depths[k];
                    sumAfter += d;
                    if (!(d > 0f)) effect.ClearedPairs++;
                }
                for (int k = 0; k < now.Vertices.Count; k++) depthAfter[now.Vertices[k]] = 0f;
            }
            if (effect.PushedPairs > 0)
            {
                effect.PushedDepthBefore = (float)(sumBefore / effect.PushedPairs);
                effect.PushedDepthAfter = (float)(sumAfter / effect.PushedPairs);
            }
            return effect;
        }
    }

    /// <summary><see cref="PoseEscapePlan.MeasureEffect"/> の結果。</summary>
    public sealed class PoseEscapeEffect
    {
        /// <summary>逃がしたか所の（ポーズ・段階, 頂点）の組の数と、そのうち調べ直して貫通が無くなった数。</summary>
        public int PushedPairs, ClearedPairs;

        /// <summary>逃がしたか所の深さの平均（メートル）の前後。</summary>
        public float PushedDepthBefore, PushedDepthAfter;

        /// <summary>全ポーズ・全段階の、数えた頂点の深さ（頭打ちにしたもの）の合計（メートル）の前後。</summary>
        public double TotalDepthBefore, TotalDepthAfter;

        /// <summary>
        /// 効いたか: 逃がしたか所の深さの平均が ratio 倍以下になり、全体の深さの合計も減った
        /// （ぴったりの服で布に余裕が無いと、外へ出した分だけ周りが引かれて、かえって増えることがある）。
        /// </summary>
        public bool Improved(float ratio) => PushedPairs > 0 && PushedDepthAfter <= PushedDepthBefore * ratio && TotalDepthAfter < TotalDepthBefore;
    }
}
