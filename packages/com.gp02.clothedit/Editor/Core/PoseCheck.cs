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

        /// <summary>体どうしの重なりを調べるレイの長さ（メートル）の既定値。太ももの太さより長くする（ふくらはぎが太ももに入った所で、太ももを抜けるまで届くように）。</summary>
        public const float DefaultOverlapRayLength = 0.3f;

        /// <summary>
        /// 体どうしの重なりを調べるレイの長さ（メートル）。布の頂点が体にぴったり付いていった位置から外向きに飛ばし、
        /// 最初に当たった面を中から抜けたら、そこはほかの体の部品の中（<see cref="BodySurface.IsInsideAlongRay"/>）。
        /// </summary>
        public float OverlapRayLength = DefaultOverlapRayLength;

        /// <summary>体にぴったり付いていった位置を、体の面から少なくともこれだけ外に置く（メートル。面の上だと内外が決まらないため）。</summary>
        public float FollowOffset = 0.001f;

        /// <summary>
        /// 編集の姿勢で、体の頂点が布に覆われているとみなす、布までの距離の上限（メートル。<see cref="PoseCheckEvaluator.MeasureCoveredBodyVertices"/>）。
        /// 広がったスカートの裾と脚のあいだより大きく取る（覆われているかは、布の内側にあることと、一番近い布の点が縁でないことで決まる）。
        /// </summary>
        public float CoverDistance = 0.3f;

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

        /// <summary>
        /// 体どうしの重なりのために突き抜けている頂点（まとめた後の番号、小さい順）。<see cref="Vertices"/> には入れず、数えない。
        /// 布が体にぴったり付いていっても（ウェイトを体と同じにしても）、そのポーズでほかの体の部品の中に入る所
        /// （腕を下ろすと腕が腰に入る、深くしゃがむとふくらはぎが太ももに入る、など）。服では直せない。
        /// アンカー（<see cref="BodyAnchors"/>）を渡して調べたときだけ入る。
        /// </summary>
        public readonly List<int> OverlapVertices = new List<int>();

        /// <summary><see cref="OverlapVertices"/> と同じ順の、深さ（メートル）。</summary>
        public readonly List<float> OverlapDepths = new List<float>();

        /// <summary>体どうしの重なりの所の数（<see cref="OverlapVertices"/> の、三角形の辺でつながったまとまりの数）。</summary>
        public int OverlapRegionCount;

        /// <summary><see cref="OverlapVertices"/> のうち、一番深いものの深さ（メートル）。</summary>
        public float OverlapMaxDepth;

        /// <summary>体どうしの重なりの表示用の点（そのポーズでのワールド座標）。</summary>
        public readonly List<Vector3> OverlapPoints = new List<Vector3>();

        /// <summary>
        /// このポーズでの布の伸び（辺の長さの、編集の姿勢に対する比 - 1）の、一番大きいものと、大きいほうから 1% の所（たまたま伸びた辺に左右されない目安）。
        /// 測っていなければ 0。測る辺は呼び出し側が選ぶ（動作チェックでは、編集の姿勢で体から離れている所だけ。体にぴったりの所は体と一緒に伸びてよいため）。
        /// ウェイトを体に寄せすぎると、スカートなどが脚に貼り付いて伸びる（<see cref="PoseCheckEvaluator.MeasureStretch(ReadOnlySpan{Vector3}, ReadOnlySpan{Vector3}, bool[], out float)"/>）。
        /// </summary>
        public float MaxStretch, HighStretch;

        /// <summary>体どうしの重なりが見つかったか。</summary>
        public bool HasOverlap => OverlapVertices.Count > 0;

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
            OverlapVertices.Clear();
            OverlapDepths.Clear();
            OverlapPoints.Clear();
            OverlapRegionCount = 0;
            OverlapMaxDepth = 0f;
            MaxStretch = 0f;
            HighStretch = 0f;
        }

        public override string ToString()
        {
            if (!HasBody) return "体が無いので調べていません";
            return $"貫通 {RegionCount} か所（頂点 {PenetratingCount}、最大 {MaxDepth * 1000f:F1} mm、布の外に出た体の頂点 {BodyVertexCount}）" +
                (HasOverlap ? $" / 体どうしの重なり {OverlapRegionCount} か所（頂点 {OverlapVertices.Count}、最大 {OverlapMaxDepth * 1000f:F1} mm）" : "") +
                (MaxStretch > 0f ? $" / 伸び 最大 {MaxStretch * 100f:F1}%・上位1% {HighStretch * 100f:F1}%" : "") +
                $"（{Milliseconds:F0} ms）";
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
        readonly float[] _depth;            // 頂点ごとの深さ（作業用。下の3つの一番大きいもの）
        readonly float[] _insideDepth;      // (a) 布の頂点が体の中にある深さ
        readonly float[] _pokeDepth;        // (b) 体の頂点が布の外へ出ている深さ（その体の頂点が、ほかの体の部品の中にないもの）
        readonly float[] _pokeOverlapDepth; // (b) のうち、その体の頂点がほかの体の部品の中にあるもの（体どうしの重なり）
        readonly float[] _classDepth;       // 分けたあとの、頂点ごとの深さ（作業用）
        readonly int[] _parent;             // まとまりを数える作業用
        readonly byte[] _class;             // 頂点ごとの分け方（作業用。0: 数えない、1: 貫通、2: 体どうしの重なり）
        readonly byte[] _classWork;
        readonly int[] _edges;              // 布の辺（頂点番号の組。重ねない）。伸びを測る
        int[] _neighborStart, _neighbors;   // 辺でつながった頂点（分け方をならすのに使う。最初に使うときに作る）
        float[] _stretchWork;
        readonly List<Vector3> _pokePoints = new List<Vector3>();
        readonly List<int> _pokeTriangles = new List<int>();
        readonly List<bool> _pokeOverlap = new List<bool>();
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
            _insideDepth = new float[vertexCount];
            _pokeDepth = new float[vertexCount];
            _pokeOverlapDepth = new float[vertexCount];
            _classDepth = new float[vertexCount];
            _parent = new int[vertexCount];
            _class = new byte[vertexCount];
            _classWork = new byte[vertexCount];

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
            var edges = new List<int>(2 * edgeUse.Count);
            foreach (long key in edgeUse.Keys)
            {
                PairKeys.Split(key, out int a, out int b);
                edges.Add(a);
                edges.Add(b);
            }
            _edges = edges.ToArray();
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
            bool hasBody = Measure(clothVertices, body, options, false, null);
            Array.Copy(_depth, depth, _vertexCount);
            return hasBody;
        }

        // 深さを _depth（と、どこから来たかごとの _insideDepth・_pokeDepth・_pokeOverlapDepth）に、布の外へ出た体の頂点を _pokePoints・_pokeTriangles に入れる。
        // classifyPokes なら、布の外へ出た体の頂点が、ほかの体の部品の中にある（体どうしの重なりのため）かも調べて分ける。
        // covered（体の頂点ごとの MeasureCoveredBodyVertices の結果。null なら見ない）で false の体の頂点は数えない
        bool Measure(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options, bool classifyPokes, bool[] covered)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (clothVertices.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(clothVertices));
            Array.Clear(_depth, 0, _vertexCount);
            Array.Clear(_insideDepth, 0, _vertexCount);
            Array.Clear(_pokeDepth, 0, _vertexCount);
            Array.Clear(_pokeOverlapDepth, 0, _vertexCount);
            _pokePoints.Clear();
            _pokeTriangles.Clear();
            _pokeOverlap.Clear();
            if (body == null || body.EnabledIslandCount == 0) return false;

            clothVertices.CopyTo(_positions);
            float maxDistance = options.MaxDistance;

            // (a) 布の頂点が体の中にある
            for (int i = 0; i < _vertexCount; i++)
            {
                if (!body.TryQuery(_positions[i], maxDistance, out BodyHit hit)) continue;
                if (hit.SignedDistance < 0f) _insideDepth[i] = -hit.SignedDistance;
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
                    // 編集の姿勢で布に覆われていなかった体の頂点（下ろした手・お辞儀で下りてくる胸など）は、布を通り抜けて出たのではない。
                    // 布の外に並んだだけか、体の部品どうしが重なっていて、後者は布の頂点が体の中に入るほう（a）で分かる
                    if (covered != null && !covered[v]) continue;
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
                    // 体の頂点が、ほかの体の部品の中にある（腕が腰に入った所の腕の頂点など）なら、体どうしの重なりのために布の外へ出ている
                    bool overlap = classifyPokes && body.IsInsideAlongRay(p, bodyNormal, options.OverlapRayLength, options.FollowOffset);
                    float[] target = overlap ? _pokeOverlapDepth : _pokeDepth;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int w = _triangles[3 * t + corner];
                        if (along > target[w]) target[w] = along;
                    }
                    _pokePoints.Add(p);
                    _pokeTriangles.Add(t);
                    _pokeOverlap.Add(overlap);
                }
            }
            for (int i = 0; i < _vertexCount; i++) _depth[i] = Math.Max(_insideDepth[i], Math.Max(_pokeDepth[i], _pokeOverlapDepth[i]));
            return true;
        }

        /// <summary>
        /// 布の頂点の位置（ワールド座標）で貫通を調べ、result に入れる（result は先に空にする）。
        /// baselineDepth（基準の姿勢の <see cref="MeasureDepth"/> の結果。null なら 0）より options.Tolerance 以上深い頂点を数え、
        /// 三角形の辺でつながったものを1か所にまとめる。result の Group と Stage は呼び出し側が入れる（ここでは変えない）。
        /// </summary>
        public void Evaluate(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options, float[] baselineDepth, PosePenetration result)
        {
            Evaluate(clothVertices, body, options, baselineDepth, result, null, null, null);
        }

        /// <summary>
        /// <see cref="Evaluate(ReadOnlySpan{Vector3}, BodySurface, PoseCheckOptions, float[], PosePenetration)"/> に、体どうしの重なりの判定を加えたもの。
        /// 数えた頂点のうち、布が体にぴったり付いていっても（アンカーの点に付いていっても）このポーズで体の中に入るもの（<see cref="IsFollowInsideBody"/>）は、
        /// <see cref="PosePenetration.OverlapVertices"/> に分け、貫通としては数えない（体どうしが重なっているので、服では直せない）。
        /// restOverlap（基準の姿勢の <see cref="MeasureFollowOverlap"/> の結果。null なら無し）で true の頂点は分けない（基準の姿勢から体の中と出るのは、
        /// 体の穴などによる取り違えのため）。anchors が null なら分けない。
        /// coveredBodyVertices（体の頂点ごとの <see cref="MeasureCoveredBodyVertices"/> の結果。null なら見ない）で false の体の頂点は、布の外へ出ても数えない
        /// （布に覆われていなかった部品なので、布を通り抜けたのではない）。
        /// </summary>
        public void Evaluate(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options, float[] baselineDepth, PosePenetration result,
            BodyAnchors anchors, bool[] restOverlap, bool[] coveredBodyVertices = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (baselineDepth != null && baselineDepth.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(baselineDepth));
            if (anchors != null && anchors.VertexCount != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(anchors));
            if (restOverlap != null && restOverlap.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(restOverlap));
            if (coveredBodyVertices != null && body != null && coveredBodyVertices.Length != body.VertexCount) throw new ArgumentException("体の頂点の数と合いません。", nameof(coveredBodyVertices));
            var timer = Stopwatch.StartNew();
            int group = result.Group;
            float stage = result.Stage;
            result.Clear();
            result.Group = group;
            result.Stage = stage;
            bool classify = anchors != null;
            result.HasBody = Measure(clothVertices, body, options, classify, coveredBodyVertices);
            if (!result.HasBody)
            {
                result.Milliseconds = timer.Elapsed.TotalMilliseconds;
                return;
            }

            float tolerance = Math.Max(0f, options.Tolerance);
            for (int i = 0; i < _vertexCount; i++)
            {
                _class[i] = 0;
                float baseline = baselineDepth != null ? baselineDepth[i] : 0f;
                float limit = baseline + tolerance;
                if (!(_depth[i] > limit)) continue;
                if (!classify)
                {
                    _class[i] = 1;
                    _classDepth[i] = _depth[i];
                    continue;
                }
                // 布が体にぴったり付いていっても体の中に入るなら、全部が体どうしの重なり。
                // そうでなければ、布の外へ出た体の頂点のうち、ほかの体の部品の中にあるもの（体どうしの重なり）の分を除いて数える
                bool followInside = (restOverlap == null || !restOverlap[i]) && IsFollowInsideBody(i, body, anchors, options);
                float plain = followInside ? 0f : Math.Max(_pokeDepth[i], _insideDepth[i]);
                if (plain > limit)
                {
                    _class[i] = 1;
                    _classDepth[i] = plain;
                }
                else
                {
                    _class[i] = 2;
                    _classDepth[i] = _depth[i];
                }
            }
            if (classify) SmoothClasses();
            for (int i = 0; i < _vertexCount; i++)
            {
                if (_class[i] == 0) continue;
                float d = _classDepth[i];
                if (_class[i] == 2)
                {
                    result.OverlapVertices.Add(i);
                    result.OverlapDepths.Add(d);
                    if (d > result.OverlapMaxDepth) result.OverlapMaxDepth = d;
                    continue;
                }
                result.Vertices.Add(i);
                result.Depths.Add(d);
                if (d > result.MaxDepth) result.MaxDepth = d;
            }
            result.RegionCount = CountRegions(1);
            LabelRegions(result);
            result.OverlapRegionCount = CountRegions(2);
            CollectPoints(result, Math.Max(0, options.MaxPoints));
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// 頂点 vertex が、ポーズを付けた体（アンカーを測った体を動かしたもの）にぴったり付いていったとき（<see cref="BodyAnchors.TryFollow"/>）に、
        /// 体のどこかの部品の中に入るか。一番近い面で内外を決める検索（ほかの島の中）と、外向きのレイ（つながった部品が自分と重なっている所）の両方で見る。
        /// アンカーが無ければ false。
        /// </summary>
        public bool IsFollowInsideBody(int vertex, BodySurface body, BodyAnchors anchors, PoseCheckOptions options)
        {
            if (body == null || anchors == null || options == null || !anchors.Has(vertex)) return false;
            if (!anchors.TryFollow(vertex, body, Math.Max(1e-5f, options.FollowOffset), out Vector3 position, out Vector3 normal)) return false;
            if (body.TryQuery(position, options.MaxDistance, out BodyHit hit) && hit.SignedDistance < -Math.Max(1e-5f, options.Tolerance)) return true;
            return body.IsInsideAlongRay(position, normal, options.OverlapRayLength);
        }

        // 分け方をならす: 数えた頂点のうち、数えた隣の大半（2つ以上、反対の分け方の隣が同じ分け方の隣の2倍より多い）が反対の分け方なら、それに合わせる
        // （体どうしの重なりの境目で、1頂点ずつ分け方が入れ替わって、小さな「か所」が多数できないように）。入れ替えた頂点の深さは、全部の深さにする
        void SmoothClasses()
        {
            if (_neighborStart == null) BuildNeighbors();
            for (int pass = 0; pass < 2; pass++)
            {
                bool changed = false;
                for (int i = 0; i < _vertexCount; i++)
                {
                    byte cls = _class[i];
                    _classWork[i] = cls;
                    if (cls == 0) continue;
                    int same = 0, other = 0;
                    for (int e = _neighborStart[i]; e < _neighborStart[i + 1]; e++)
                    {
                        byte n = _class[_neighbors[e]];
                        if (n == 0) continue;
                        if (n == cls) same++;
                        else other++;
                    }
                    if (other >= 2 && other > 2 * same)
                    {
                        _classWork[i] = cls == 1 ? (byte)2 : (byte)1;
                        changed = true;
                    }
                }
                if (!changed) break;
                for (int i = 0; i < _vertexCount; i++)
                {
                    if (_classWork[i] == _class[i]) continue;
                    _class[i] = _classWork[i];
                    _classDepth[i] = _depth[i];
                }
            }
        }

        void BuildNeighbors()
        {
            var start = new int[_vertexCount + 1];
            for (int k = 0; k < _edges.Length; k++) start[_edges[k] + 1]++;
            for (int i = 0; i < _vertexCount; i++) start[i + 1] += start[i];
            var neighbors = new int[_edges.Length];
            var fill = (int[])start.Clone();
            for (int k = 0; k < _edges.Length; k += 2)
            {
                int a = _edges[k], b = _edges[k + 1];
                neighbors[fill[a]++] = b;
                neighbors[fill[b]++] = a;
            }
            _neighborStart = start;
            _neighbors = neighbors;
        }

        /// <summary>
        /// すべての頂点について <see cref="IsFollowInsideBody"/> を調べ、overlap に入れる。基準の姿勢で呼び、その結果を
        /// <see cref="Evaluate(ReadOnlySpan{Vector3}, BodySurface, PoseCheckOptions, float[], PosePenetration, BodyAnchors, bool[], bool[])"/> の restOverlap に渡す。
        /// </summary>
        public void MeasureFollowOverlap(BodySurface body, BodyAnchors anchors, PoseCheckOptions options, bool[] overlap)
        {
            if (overlap == null || overlap.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(overlap));
            for (int i = 0; i < _vertexCount; i++) overlap[i] = IsFollowInsideBody(i, body, anchors, options);
        }

        /// <summary>
        /// 編集の姿勢の布（clothVertices）と体で、体の頂点ごとに、この布に覆われているか（布の内側にあり、一番近い布の点が縁でなく、
        /// <see cref="PoseCheckOptions.CoverDistance"/> 以内）を covered（長さは body.VertexCount）に入れる。
        /// ポーズで布の外へ出た体の頂点のうち、覆われていなかったもの（下ろした手・お辞儀で下りてくる胸など、もともと布の外にあった部品）は、
        /// 布を通り抜けて出たのではないので、<see cref="Evaluate(ReadOnlySpan{Vector3}, BodySurface, PoseCheckOptions, float[], PosePenetration, BodyAnchors, bool[], bool[])"/> で数えない。
        /// </summary>
        public void MeasureCoveredBodyVertices(ReadOnlySpan<Vector3> clothVertices, BodySurface body, PoseCheckOptions options, bool[] covered)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (clothVertices.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(clothVertices));
            if (covered == null || covered.Length != body.VertexCount) throw new ArgumentException("体の頂点の数と合いません。", nameof(covered));
            Array.Clear(covered, 0, covered.Length);
            if (_triangles.Length == 0) return;
            clothVertices.CopyTo(_positions);
            if (_tree == null) _tree = new ClosestTriangleTree(_positions, _triangles);
            else _tree.Refit(_positions);
            float distance = Math.Max(0f, options.CoverDistance);
            float limitSq = distance * distance;
            for (int v = 0; v < body.VertexCount; v++)
            {
                Vector3 p = body.GetVertex(v);
                if (_tree.RootDistanceSquared(p.X, p.Y, p.Z) > limitSq) continue;
                if (!_tree.FindClosest(_positions, p.X, p.Y, p.Z, distance, out int t, out float qx, out float qy, out float qz,
                    out float bu, out float bv, out float bw, out _)) continue;
                // 一番近い布の点が縁なら、体の頂点は布の外（スカートの上のおなか・袖口の先の手など）
                if (IsOnBoundary(t, bu, bv, bw)) continue;
                FaceNormal(t, out float nx, out float ny, out float nz);
                int sign = _triangleSign[t];
                if (sign == 0)
                {
                    Vector3 bodyNormal = body.GetVertexNormal(v);
                    float alignment = nx * bodyNormal.X + ny * bodyNormal.Y + nz * bodyNormal.Z;
                    if (Math.Abs(alignment) < 1e-3f) continue;
                    sign = alignment > 0f ? 1 : -1;
                }
                float along = ((p.X - qx) * nx + (p.Y - qy) * ny + (p.Z - qz) * nz) * sign;
                covered[v] = along <= PenetrationReport.CountTolerance;
            }
        }

        /// <summary>
        /// rest（編集の姿勢）から posed（ポーズ）への、布の辺の伸び（長さの比 - 1。縮みは 0）の一番大きいものを返し、大きいほうから 1% の所を high に入れる。
        /// 元の長さが 1mm より短い辺は見ない。
        /// </summary>
        public float MeasureStretch(ReadOnlySpan<Vector3> rest, ReadOnlySpan<Vector3> posed, out float high) => MeasureStretch(rest, posed, null, out high);

        /// <summary>
        /// <see cref="MeasureStretch(ReadOnlySpan{Vector3}, ReadOnlySpan{Vector3}, out float)"/> を、両端の頂点が include で true の辺だけで測る（null なら全部）。
        /// </summary>
        public float MeasureStretch(ReadOnlySpan<Vector3> rest, ReadOnlySpan<Vector3> posed, bool[] include, out float high)
        {
            if (rest.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(rest));
            if (posed.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(posed));
            if (include != null && include.Length != _vertexCount) throw new ArgumentException("頂点の数と合いません。", nameof(include));
            int edgeCount = _edges.Length / 2;
            if (_stretchWork == null || _stretchWork.Length < edgeCount) _stretchWork = new float[edgeCount];
            int count = 0;
            float max = 0f;
            for (int k = 0; k < edgeCount; k++)
            {
                int a = _edges[2 * k], b = _edges[2 * k + 1];
                if (include != null && (!include[a] || !include[b])) continue;
                float rx = rest[a].X - rest[b].X, ry = rest[a].Y - rest[b].Y, rz = rest[a].Z - rest[b].Z;
                float restLength = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                if (!(restLength > 1e-3f)) continue;
                float px = posed[a].X - posed[b].X, py = posed[a].Y - posed[b].Y, pz = posed[a].Z - posed[b].Z;
                float stretch = Math.Max(0f, MathF.Sqrt(px * px + py * py + pz * pz) / restLength - 1f);
                _stretchWork[count++] = stretch;
                if (stretch > max) max = stretch;
            }
            high = 0f;
            if (count == 0) return 0f;
            Array.Sort(_stretchWork, 0, count);
            high = _stretchWork[Math.Min(count - 1, (int)(0.99f * count))];
            return max;
        }

        bool IsCounted(int vertex) => _class[vertex] != 0;

        // 分け方 cls の頂点の、三角形の辺でつながったまとまりの数
        int CountRegions(byte cls)
        {
            int[] parent = _parent;
            for (int i = 0; i < _vertexCount; i++) parent[i] = i;
            for (int t = 0; t < _triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = _triangles[t + e], b = _triangles[t + (e + 1) % 3];
                    if (_class[a] == cls && _class[b] == cls) Union(parent, a, b);
                }
            }
            int regions = 0;
            for (int i = 0; i < _vertexCount; i++)
            {
                if (_class[i] == cls && Find(parent, i) == i) regions++;
            }
            return regions;
        }

        // 数えた頂点に「か所」の番号を振り、か所ごとの一番深い深さを出す（CountRegions(1) のあとに呼ぶ。根の番号の小さい順に 0, 1, …）
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

        // 表示用の点: 数えた布の頂点（4分の3まで）と、数えた所に関わる体の頂点（体どうしの重なりのものは除く）。体どうしの重なりの頂点は、別に間引いて入れる
        void CollectPoints(PosePenetration result, int maxPoints)
        {
            var related = _pokePoints;
            int pokeKept = 0;
            for (int k = 0; k < related.Count; k++)
            {
                int t = _pokeTriangles[k];
                bool counted = _class[_triangles[3 * t]] == 1 || _class[_triangles[3 * t + 1]] == 1 || _class[_triangles[3 * t + 2]] == 1;
                if (!counted || _pokeOverlap[k]) continue;
                related[pokeKept] = related[k];
                _pokeTriangles[pokeKept] = t;
                pokeKept++;
            }
            related.RemoveRange(pokeKept, related.Count - pokeKept);
            _pokeTriangles.RemoveRange(pokeKept, _pokeTriangles.Count - pokeKept);
            _pokeOverlap.Clear();
            result.BodyVertexCount = pokeKept;
            if (maxPoints == 0) return;

            int vertexBudget = pokeKept > 0 ? maxPoints * 3 / 4 : maxPoints;
            int count = result.Vertices.Count;
            int stride = count <= vertexBudget ? 1 : (count + vertexBudget - 1) / Math.Max(1, vertexBudget);
            for (int k = 0; k < count && result.Points.Count < vertexBudget; k += stride) result.Points.Add(_positions[result.Vertices[k]]);
            int overlapCount = result.OverlapVertices.Count;
            int overlapStride = overlapCount <= maxPoints ? 1 : (overlapCount + maxPoints - 1) / maxPoints;
            for (int k = 0; k < overlapCount && result.OverlapPoints.Count < maxPoints; k += overlapStride) result.OverlapPoints.Add(_positions[result.OverlapVertices[k]]);
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

        /// <summary>全結果の、体どうしの重なりの所の数の合計（貫通の数には入れていない）。</summary>
        public int TotalOverlapRegionCount
        {
            get
            {
                int count = 0;
                foreach (PosePenetration entry in _entries) count += entry.OverlapRegionCount;
                return count;
            }
        }

        /// <summary>体どうしの重なりが見つかったポーズの数（<see cref="PosePenetration.Group"/> が同じ結果は1つと数える）。</summary>
        public int OverlapGroupCount
        {
            get
            {
                var groups = new HashSet<int>();
                foreach (PosePenetration entry in _entries)
                {
                    if (entry.HasOverlap) groups.Add(entry.Group);
                }
                return groups.Count;
            }
        }

        /// <summary>全結果の布の伸びの、一番大きいものと、大きいほうから 1% の所の一番大きいもの（<see cref="PosePenetration.MaxStretch"/>）。</summary>
        public float MaxStretch
        {
            get
            {
                float max = 0f;
                foreach (PosePenetration entry in _entries) max = Math.Max(max, entry.MaxStretch);
                return max;
            }
        }

        public float HighStretch
        {
            get
            {
                float max = 0f;
                foreach (PosePenetration entry in _entries) max = Math.Max(max, entry.HighStretch);
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
        public PoseEscapePlan PlanEscape(float extra, float maxPush, float minGain) => PlanEscape(extra, maxPush, minGain, null, 0f);

        /// <summary>
        /// <see cref="PlanEscape(float, float, float)"/> に、体にぴったりの所を逃がさないことを加えたもの。か所の頂点の半分以上で、一番近い体の点（anchors）までが
        /// tightDistance 以下なら、外へ逃がす布の余裕が無い（逃がすと周りが引かれて上下にずれ、別のポーズでかえって深くなることがある）ので逃がさず、
        /// ウェイトを体に寄せる対象にする（<see cref="PlanWeightFix(float, float, float, BodyAnchors, float)"/> と同じ決め方）。
        /// </summary>
        public PoseEscapePlan PlanEscape(float extra, float maxPush, float minGain, BodyAnchors anchors, float tightDistance)
        {
            if (anchors != null && anchors.VertexCount != _worstDepth.Length) throw new ArgumentException("頂点の数が、集計と合いません。", nameof(anchors));
            var plan = new PoseEscapePlan { WeldedPush = new float[_worstDepth.Length], AllowedRegions = new bool[_entries.Count][] };
            extra = Math.Max(0f, extra);
            for (int e = 0; e < _entries.Count; e++)
            {
                PosePenetration entry = _entries[e];
                int regions = entry.RegionDepths.Count;
                if (regions == 0) continue;
                ClassifyRegions(entry, extra, minGain, out float[] required, out bool[] lowGain);
                bool[] tight = TightRegions(entry, anchors, tightDistance);
                bool measured = entry.Gains.Count == entry.Vertices.Count;
                var allowed = new bool[regions];
                plan.AllowedRegions[e] = allowed;
                for (int r = 0; r < regions; r++)
                {
                    if (lowGain[r]) plan.LowGainRegions++;
                    else if (required[r] > maxPush) plan.TooFarRegions++;
                    else if (tight != null && tight[r]) plan.TightRegions++;
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

        // か所ごとの、体にぴったりか（か所の頂点の半分以上で、一番近い体の点までが tightDistance 以下）。anchors が null なら null
        static bool[] TightRegions(PosePenetration entry, BodyAnchors anchors, float tightDistance)
        {
            if (anchors == null) return null;
            int regions = entry.RegionDepths.Count;
            var votes = new int[regions];
            var counts = new int[regions];
            for (int k = 0; k < entry.Vertices.Count; k++)
            {
                int r = entry.VertexRegions[k];
                counts[r]++;
                int v = entry.Vertices[k];
                if ((uint)v < (uint)anchors.VertexCount && anchors.Has(v) && anchors.GetDistance(v) <= tightDistance) votes[r]++;
            }
            var tight = new bool[regions];
            for (int r = 0; r < regions; r++) tight[r] = counts[r] > 0 && 2 * votes[r] >= counts[r];
            return tight;
        }

        // か所ごとの「外へ逃がすのに要る量」（深さ＋余白を、ポーズで動く割合で割ったものの一番大きいもの）と、
        // 動く割合が minGain より小さい頂点があるか（ウェイトが原因）。割合を測っていない結果は、割合 1 とみなす
        static void ClassifyRegions(PosePenetration entry, float extra, float minGain, out float[] required, out bool[] lowGain)
        {
            int regions = entry.RegionDepths.Count;
            required = new float[regions];
            lowGain = new bool[regions];
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
        }

        /// <summary>
        /// 「ウェイトを体に寄せる」の対象を決める。ポーズ・段階ごとの「か所」のうち、外へ逃がしても直らないもの
        /// （<see cref="PlanEscape(float, float, float)"/> で逃がさないか所: ポーズで動く割合が minGain より小さい頂点がある＝ウェイトの混ざり方の違い、
        /// または maxPush を超えて動かさないと足りない＝服が体に付いていかない）の頂点を、対象（度合い 1）にする。
        /// 外へ逃がせるか所の頂点も、ポーズで動く割合が小さいものは、その分だけ対象にする（<see cref="WeightFixPlan.WeldedSeed"/>）。
        /// 体どうしの重なり（<see cref="PosePenetration.OverlapVertices"/>）は、もともと「か所」に入っていないので対象にならない。
        /// </summary>
        public WeightFixPlan PlanWeightFix(float extra, float maxPush, float minGain) => PlanWeightFix(extra, maxPush, minGain, null, 0f);

        /// <summary>
        /// <see cref="PlanWeightFix(float, float, float)"/> に、ぴったりの服の所も対象に加えたもの。か所の頂点の半分以上で、一番近い体の点（anchors）までが
        /// tightDistance 以下なら、外へ逃がす布の余裕が無い（逃がすと伸びる）ので、ウェイトで直す対象にする（体に沿った服は、体と同じウェイトにすれば付いていく）。
        /// </summary>
        public WeightFixPlan PlanWeightFix(float extra, float maxPush, float minGain, BodyAnchors anchors, float tightDistance)
        {
            if (anchors != null && anchors.VertexCount != _worstDepth.Length) throw new ArgumentException("頂点の数が、集計と合いません。", nameof(anchors));
            var plan = new WeightFixPlan { WeldedSeed = new float[_worstDepth.Length], TargetRegions = new bool[_entries.Count][] };
            extra = Math.Max(0f, extra);
            for (int e = 0; e < _entries.Count; e++)
            {
                PosePenetration entry = _entries[e];
                plan.OverlapRegions += entry.OverlapRegionCount;
                int regions = entry.RegionDepths.Count;
                if (regions == 0) continue;
                ClassifyRegions(entry, extra, minGain, out float[] required, out bool[] lowGain);
                bool[] tight = TightRegions(entry, anchors, tightDistance);
                var target = new bool[regions];
                plan.TargetRegions[e] = target;
                for (int r = 0; r < regions; r++)
                {
                    if (lowGain[r]) plan.LowGainRegions++;
                    else if (required[r] > maxPush) plan.TooFarRegions++;
                    else if (tight != null && tight[r]) plan.TightRegions++;
                    else continue;
                    target[r] = true;
                }
                bool measured = entry.Gains.Count == entry.Vertices.Count;
                for (int k = 0; k < entry.Vertices.Count; k++)
                {
                    int v = entry.Vertices[k];
                    if ((uint)v >= (uint)plan.WeldedSeed.Length) continue;
                    // ウェイトが原因のか所は全部。外へ逃がせるか所でも、ポーズで動く割合が小さい頂点ほど、ウェイトが原因の度合いが大きい
                    // （割合 minGain 以下で 1、割合 1 で 0）。ぴったりの服のひざの浅い突き抜けは、外へ逃がす布の余裕も無いため
                    float seed = target[entry.VertexRegions[k]] ? 1f : measured ? Math.Max(0f, Math.Min(1f, (1f - entry.Gains[k]) / Math.Max(1e-3f, 1f - minGain))) : 0f;
                    if (seed > plan.WeldedSeed[v]) plan.WeldedSeed[v] = seed;
                }
            }
            for (int v = 0; v < plan.WeldedSeed.Length; v++)
            {
                if (plan.WeldedSeed[v] > 0f) plan.SeedVertexCount++;
            }
            return plan;
        }

        /// <summary>
        /// 直した所（regions: 結果の番号ごと・か所の番号ごとの印）が、直したあとに同じポーズ・段階を同じ順に調べ直した集計 after で、
        /// どれだけ浅くなったかと、全ポーズの深さの合計（1つの深さは depthCap で頭打ち）の前後を測る。before は regions を作った集計。
        /// </summary>
        public static PoseFixEffect MeasureFixEffect(PoseCheckSummary before, PoseCheckSummary after, bool[][] regions, float depthCap)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            var effect = new PoseFixEffect();
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
            for (int e = 0; e < count && regions != null && e < regions.Length; e++)
            {
                bool[] marked = regions[e];
                if (marked == null) continue;
                PosePenetration was = before.Entries[e], now = after.Entries[e];
                for (int k = 0; k < now.Vertices.Count; k++) depthAfter[now.Vertices[k]] = now.Depths[k];
                for (int k = 0; k < was.Vertices.Count; k++)
                {
                    int r = was.VertexRegions[k];
                    if (r < 0 || r >= marked.Length || !marked[r]) continue;
                    float d = depthAfter[was.Vertices[k]];
                    effect.TargetPairs++;
                    sumBefore += was.Depths[k];
                    sumAfter += d;
                    if (!(d > 0f)) effect.ClearedPairs++;
                }
                for (int k = 0; k < now.Vertices.Count; k++) depthAfter[now.Vertices[k]] = 0f;
            }
            if (effect.TargetPairs > 0)
            {
                effect.TargetDepthBefore = (float)(sumBefore / effect.TargetPairs);
                effect.TargetDepthAfter = (float)(sumAfter / effect.TargetPairs);
            }
            return effect;
        }
    }

    /// <summary><see cref="PoseCheckSummary.PlanWeightFix"/> の結果: 「ウェイトを体に寄せる」の対象の頂点と、か所の数（ポーズ・段階ごとに数える）。</summary>
    public sealed class WeightFixPlan
    {
        /// <summary>
        /// まとめた後の頂点ごとの、対象の度合い（0〜1）。ウェイトが原因のか所の頂点は 1、外へ逃がせるか所の頂点は、ポーズで動く割合が小さいほど大きい、ほかは 0。
        /// </summary>
        public float[] WeldedSeed;

        /// <summary>結果（<see cref="PoseCheckSummary.Entries"/> の番号）ごとの、対象のか所（か所の番号ごとに true）。か所の無い結果は null。</summary>
        public bool[][] TargetRegions;

        /// <summary>対象のか所のうち、ポーズで動く割合が小さい頂点のあるもの（ひざ・ひじを深く曲げた所など）と、外へ逃がすには深すぎるもの（座ったときの太ももなど）の数。</summary>
        public int LowGainRegions, TooFarRegions;

        /// <summary>対象のか所のうち、体に沿ったぴったりの服で、外へ逃がす余裕の無いもの（浅くても、体と同じウェイトにして直す）の数。</summary>
        public int TightRegions;

        /// <summary>体どうしの重なりの所の数（対象にしない。服では直せない）。</summary>
        public int OverlapRegions;

        /// <summary>対象の頂点の数。</summary>
        public int SeedVertexCount;

        /// <summary>対象のか所の数（ウェイトが原因の貫通）。</summary>
        public int Regions => LowGainRegions + TooFarRegions + TightRegions;

        /// <summary>直したあとに調べ直した集計 after で、対象のか所の前後を測る（<see cref="PoseCheckSummary.MeasureFixEffect"/>）。</summary>
        public PoseFixEffect MeasureEffect(PoseCheckSummary before, PoseCheckSummary after, float depthCap) => PoseCheckSummary.MeasureFixEffect(before, after, TargetRegions, depthCap);
    }

    /// <summary><see cref="PoseCheckSummary.MeasureFixEffect"/> の結果。</summary>
    public sealed class PoseFixEffect
    {
        /// <summary>直した所の（ポーズ・段階, 頂点）の組の数と、そのうち調べ直して貫通が無くなった数。</summary>
        public int TargetPairs, ClearedPairs;

        /// <summary>直した所の深さの平均（メートル）の前後。</summary>
        public float TargetDepthBefore, TargetDepthAfter;

        /// <summary>全ポーズ・全段階の、数えた頂点の深さ（頭打ちにしたもの）の合計（メートル）の前後。</summary>
        public double TotalDepthBefore, TotalDepthAfter;

        /// <summary>直した所のうち、貫通が無くなった割合（0〜1）。</summary>
        public float ClearedRatio => TargetPairs > 0 ? (float)ClearedPairs / TargetPairs : 0f;

        /// <summary>効いたか: 直した所の深さの平均が ratio 倍以下になり、全体の深さの合計も減った。</summary>
        public bool Improved(float ratio) => TargetPairs > 0 && TargetDepthAfter <= TargetDepthBefore * ratio && TotalDepthAfter < TotalDepthBefore;
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

        /// <summary>体にぴったりで、外へ逃がす布の余裕が無いので逃がさないか所の数（ウェイトを体に寄せて直す）。</summary>
        public int TightRegions;

        /// <summary>逃がさないか所の数。</summary>
        public int SkippedRegions => LowGainRegions + TooFarRegions + TightRegions;

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
