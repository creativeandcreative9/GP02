using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// 1つのレンダラーに対する編集の本体。画面（ウィンドウ）を持たないので、EditMode テストで確かめられる。
    ///
    /// 編集はワールド座標（現在のポーズ、実寸のメートル）で行う。粒子の 1cm 間隔をアバターの実寸にするためと、
    /// シーンに見えている形のまま触れるようにするため。
    /// シェイプキーとして保存するときは、頂点ごとの変換行列（メッシュ座標 → ワールド座標）の逆で、メッシュ座標の差分に直す。
    /// </summary>
    public sealed class ClothEditSession : IDisposable
    {
        const string ShapeNamePrefix = "GP02_";
        const string MeshSuffix = "_GP02";

        readonly Renderer _renderer;
        readonly Mesh _sourceMesh;
        readonly Vector3[] _baseWorld;            // 編集前のワールド頂点
        readonly Matrix4x4[] _vertexMatrices;     // 頂点ごとの、メッシュ座標 → ワールド座標
        readonly Matrix4x4[] _inverseMatrices;    // 上の 3x3 部分の逆行列（特異なら 0）
        readonly float[] _inverseScale;           // 1 / |det|^(1/3)（特異なら 0）
        readonly ParticleCloth _cloth;
        readonly ClothSolver _solver;
        readonly NVector3[] _welded;
        readonly NVector3[] _toposRest;
        readonly TriangleBvh _bvh;                // レイキャスト用。まとめた後の三角形（_welded の位置）を入れてある
        bool _bvhDirty;                           // 形が変わったあと、次のレイキャストの前に箱を更新する
        readonly int[] _vertexToWelded;
        readonly float[] _createdWeights;         // セッション作成時のシェイプキーの重み（保存の取り消しを見つけるため）
        readonly Mesh _previewMesh;
        readonly Material[] _materials;
        NVector3[] _stateWork;
        NVector3[] _deltaVerticesWork;
        NVector3[] _deltaNormalsWork;
        bool _previewDirty = true;
        bool _disposed;

        // つまんでいる間の状態
        int _grabParticle = -1;
        float _grabRadius;
        NVector3 _grabPointStart;
        NVector3 _grabParticleStart;

        int _pinnedCount;

        public Renderer Renderer => _renderer;
        public Mesh SourceMesh => _sourceMesh;
        public ParticleCloth Cloth => _cloth;
        public ClothSolver Solver => _solver;

        /// <summary>
        /// 計算の高速化の状態を表す文字列（ウィンドウに表示する）。
        /// Burst の有無はこのアセンブリから見えないので、ソルバーのカーネルの名前で判断する。
        /// </summary>
        public static string DescribeAcceleration(IClothSolverKernel kernel)
        {
            if (kernel == null) return "なし（Burst を入れると速くなります）";
            return kernel.IsAccelerated ? $"{kernel.Name}（有効）" : $"{kernel.Name}（無効。Jobs > Burst > Enable Compilation を確認してください）";
        }

        /// <summary>準備（粒子化）にかかった時間（ミリ秒）。隙間を測る時間は含まない（<see cref="RestGap"/> の Milliseconds）。</summary>
        public double PrepareMilliseconds { get; }

        /// <summary>
        /// 粒子ごとの、体や内側の布までの隙間（メートル）。元の形より内側へ入ってよい深さとして、
        /// <see cref="ClothEditSettings.ApplyTo"/> がソルバーに渡す。測れなかった（元の形の面の向きが無い）ときは null。
        /// </summary>
        public float[] RestAllowance { get; }

        /// <summary>隙間を測った結果（平均・最小・最大、かかった時間など）。測れなかったときは null。</summary>
        public RestGapMeasurer.Result RestGap { get; }
        public int ParticleCount => _cloth.Model.ParticleCount;
        public int VertexCount => _sourceMesh.vertexCount;

        /// <summary>現在の形のワールド頂点（元メッシュの頂点数）。UpdateCurrentShape で更新される。</summary>
        public Vector3[] CurrentWorldVertices { get; }

        public int PinnedCount => _pinnedCount;
        public bool IsGrabbing => _grabParticle >= 0;

        /// <summary>対象のレンダラーが消されていないか。</summary>
        public bool IsTargetAlive => _renderer != null;

        public ClothEditSession(Renderer renderer) : this(renderer, null)
        {
        }

        /// <summary>
        /// 既存の布を使ってセッションを作る。<see cref="ParticleCloth.Build"/>（粒子の置き直し）を呼ばないので速い。
        /// 保存の直後に、<see cref="ParticleCloth.Rebase"/> で作った布を渡して編集を続けるのに使う。
        /// 頂点ごとの行列と基準のワールド頂点は、今までどおりレンダラーから計算する。
        /// </summary>
        /// <param name="cloth">このメッシュに合う布。null なら <see cref="ParticleCloth.Build"/> で作る</param>
        /// <exception cref="ArgumentException">布がこのメッシュに合わない（頂点数・三角形数が違う）とき</exception>
        public ClothEditSession(Renderer renderer, ParticleCloth cloth)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            _renderer = renderer;
            _sourceMesh = GetMesh(renderer);
            if (_sourceMesh == null) throw new ArgumentException("メッシュがありません。MeshFilter か SkinnedMeshRenderer のメッシュを確認してください。", nameof(renderer));
            if (cloth != null && !IsClothCompatible(_sourceMesh, cloth))
            {
                throw new ArgumentException("布がこのメッシュに合いません（頂点数か三角形数が違います）。", nameof(cloth));
            }
            if (cloth == null && CountTriangles(_sourceMesh) == 0)
            {
                throw new ArgumentException($"「{_sourceMesh.name}」には三角形（面）がありません。面のあるメッシュを指定してください。", nameof(renderer));
            }
            _createdWeights = ReadBlendShapeWeights(renderer);

            var timer = Stopwatch.StartNew();
            Vector3[] baseVertices = _sourceMesh.vertices;
            // エディタでは Read/Write が無効なメッシュも読めるが、読めない環境では空の配列が返る
            if (baseVertices.Length != _sourceMesh.vertexCount)
            {
                throw new ArgumentException($"「{_sourceMesh.name}」の頂点を読み取れません。モデルの Import Settings で Read/Write を有効にしてください。", nameof(renderer));
            }
            if (renderer is SkinnedMeshRenderer skinned) ApplyBlendShapeWeights(skinned, _sourceMesh, baseVertices);

            _vertexMatrices = ComputeVertexMatrices(renderer, _sourceMesh);
            _baseWorld = new Vector3[baseVertices.Length];
            _inverseMatrices = new Matrix4x4[baseVertices.Length];
            _inverseScale = new float[baseVertices.Length];
            // MeshFilter はシェイプキーを描かないので、保存した形がレンダラーからは分からない。
            // 渡された布の静止状態（保存した形）を基準にして、編集中の形を保存前と同じにする。
            // まとめた頂点どうしの位置の差は許容距離（1e-6）以下
            bool baseFromCloth = cloth != null && !(renderer is SkinnedMeshRenderer);
            for (int v = 0; v < baseVertices.Length; v++)
            {
                if (baseFromCloth)
                {
                    NVector3 p = cloth.Topology.Positions[cloth.Topology.VertexToWelded[v]];
                    _baseWorld[v] = new Vector3(p.X, p.Y, p.Z);
                }
                else
                {
                    _baseWorld[v] = _vertexMatrices[v].MultiplyPoint3x4(baseVertices[v]);
                }
                _inverseScale[v] = Invert3x3(_vertexMatrices[v], out _inverseMatrices[v]);
            }

            // 全サブメッシュの三角形を順に並べたもの。布を渡されたときは使わないので読まない
            _cloth = cloth ?? ParticleCloth.Build(MeshBridge.ToNumerics(_baseWorld, Vector3.one), _sourceMesh.triangles);
            _solver = _cloth.CreateSolver();
            _toposRest = _cloth.Topology.Positions;
            _welded = (NVector3[])_toposRest.Clone();
            _bvh = new TriangleBvh(_welded, _cloth.Topology.Triangles);
            _vertexToWelded = _cloth.Topology.VertexToWelded;
            CurrentWorldVertices = (Vector3[])_baseWorld.Clone();
            timer.Stop();
            PrepareMilliseconds = timer.Elapsed.TotalMilliseconds;

            // 体や内側の布までの隙間。布を渡されたとき（保存後の作り直し）も、今の形で測り直す
            RestGap = RestGapMeasurer.Measure(_cloth, _bvh, _welded, renderer);
            RestAllowance = RestGap?.Gaps;
            _solver.SetRestSurfaceAllowance(RestAllowance);
            if (RestGap != null) UnityEngine.Debug.Log("[GP02] " + renderer.name + ": " + RestGap);

            _previewMesh = Object.Instantiate(_sourceMesh);
            _previewMesh.name = _sourceMesh.name + "_Preview";
            _previewMesh.hideFlags = HideFlags.HideAndDontSave;
            // Graphics.DrawMesh は頂点をそのまま描くので、スキニングとシェイプキーは使わない
            _previewMesh.ClearBlendShapes();
            _previewMesh.boneWeights = new BoneWeight[0];
            _materials = renderer.sharedMaterials;

            renderer.forceRenderingOff = true;
        }

        /// <summary>
        /// 布がこのメッシュに合うか。まとめる前の頂点数と、三角形の数（全サブメッシュの合計）が一致すること。
        /// </summary>
        public static bool IsClothCompatible(Mesh mesh, ParticleCloth cloth)
        {
            if (mesh == null || cloth == null) return false;
            return cloth.Topology.VertexToWelded.Length == mesh.vertexCount
                && cloth.Topology.SourceToTriangle.Length == CountTriangles(mesh);
        }

        /// <summary>全サブメッシュの三角形の数（<c>mesh.triangles</c> の3つ組の数）。</summary>
        public static long CountTriangles(Mesh mesh)
        {
            long indexCount = 0;
            for (int sub = 0; sub < mesh.subMeshCount; sub++) indexCount += mesh.GetIndexCount(sub);
            return indexCount / 3;
        }

        /// <summary>レンダラーのメッシュに、布が合うか（<see cref="IsClothCompatible(Mesh, ParticleCloth)"/>）。</summary>
        public static bool IsClothCompatible(Renderer renderer, ParticleCloth cloth)
        {
            return renderer != null && IsClothCompatible(GetMesh(renderer), cloth);
        }

        static float[] ReadBlendShapeWeights(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer skinned) || skinned.sharedMesh == null) return new float[0];
            var weights = new float[skinned.sharedMesh.blendShapeCount];
            for (int k = 0; k < weights.Length; k++) weights[k] = skinned.GetBlendShapeWeight(k);
            return weights;
        }

        /// <summary>
        /// レンダラーのメッシュとシェイプキーの重みが、セッションを作ったときのままか。
        /// 保存のあとに Ctrl+Z で保存そのもの（メッシュの差し替え・重み）を戻すと、false になる。
        /// </summary>
        public bool IsTargetUnchanged()
        {
            if (!IsTargetAlive || GetMesh(_renderer) != _sourceMesh) return false;
            float[] now = ReadBlendShapeWeights(_renderer);
            if (now.Length != _createdWeights.Length) return false;
            for (int k = 0; k < now.Length; k++)
            {
                if (now[k] != _createdWeights[k]) return false;
            }
            return true;
        }

        static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // 複数フレームのシェイプキーは、重みを挟む2フレーム間の線形補間。
        // 最初のフレームより小さければ 0 と最初のフレームの間、最後のフレームより大きければ最後のフレームを延長する。
        static void ApplyBlendShapeWeights(SkinnedMeshRenderer skinned, Mesh mesh, Vector3[] vertices)
        {
            int shapeCount = Mathf.Min(mesh.blendShapeCount, skinned.sharedMesh.blendShapeCount);
            Vector3[] low = null, high = null;
            for (int k = 0; k < shapeCount; k++)
            {
                float weight = skinned.GetBlendShapeWeight(k);
                int frames = mesh.GetBlendShapeFrameCount(k);
                if (weight == 0f || frames == 0) continue;
                if (low == null)
                {
                    low = new Vector3[vertices.Length];
                    high = new Vector3[vertices.Length];
                }

                int upper = 0;
                while (upper < frames && weight > mesh.GetBlendShapeFrameWeight(k, upper)) upper++;

                if (upper == 0 || upper == frames)
                {
                    int frame = upper == 0 ? 0 : frames - 1;
                    float frameWeight = mesh.GetBlendShapeFrameWeight(k, frame);
                    if (frameWeight == 0f) continue;
                    mesh.GetBlendShapeFrameVertices(k, frame, high, null, null);
                    float scale = weight / frameWeight;
                    for (int v = 0; v < vertices.Length; v++) vertices[v] += high[v] * scale;
                }
                else
                {
                    float lowWeight = mesh.GetBlendShapeFrameWeight(k, upper - 1);
                    float highWeight = mesh.GetBlendShapeFrameWeight(k, upper);
                    if (highWeight <= lowWeight) continue;
                    float t = (weight - lowWeight) / (highWeight - lowWeight);
                    mesh.GetBlendShapeFrameVertices(k, upper - 1, low, null, null);
                    mesh.GetBlendShapeFrameVertices(k, upper, high, null, null);
                    for (int v = 0; v < vertices.Length; v++) vertices[v] += Vector3.Lerp(low[v], high[v], t);
                }
            }
        }

        /// <summary>
        /// SkinnedMeshRenderer が1頂点あたりに使うボーンの本数の上限。
        /// quality が Auto なら、プロジェクトの設定（QualitySettings.skinWeights）に従う。
        /// </summary>
        public static int GetBoneWeightLimit(SkinnedMeshRenderer skinned)
        {
            if (skinned == null) throw new ArgumentNullException(nameof(skinned));
            switch (skinned.quality)
            {
                case SkinQuality.Bone1: return 1;
                case SkinQuality.Bone2: return 2;
                case SkinQuality.Bone4: return 4;
            }
            switch (QualitySettings.skinWeights)
            {
                case SkinWeights.OneBone: return 1;
                case SkinWeights.TwoBones: return 2;
                case SkinWeights.FourBones: return 4;
                default: return int.MaxValue;   // Unlimited
            }
        }

        // 線形ブレンドスキニング：M_v = Σ w_i * (bone_i.localToWorld * bindpose_i)
        // ウェイトは GetAllBoneWeights の順（頂点ごとに大きい順）で、Unity が実際に使う本数までを使う
        static Matrix4x4[] ComputeVertexMatrices(Renderer renderer, Mesh mesh)
        {
            int vertexCount = mesh.vertexCount;
            var result = new Matrix4x4[vertexCount];
            Matrix4x4 own = renderer.transform.localToWorldMatrix;

            var skinned = renderer as SkinnedMeshRenderer;
            Transform[] bones = skinned != null ? skinned.bones : null;
            int weightLimit = skinned != null ? GetBoneWeightLimit(skinned) : int.MaxValue;
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var boneWeights = mesh.GetAllBoneWeights();
            if (bones == null || bones.Length == 0 || bonesPerVertex.Length != vertexCount)
            {
                for (int v = 0; v < vertexCount; v++) result[v] = own;
                return result;
            }

            Matrix4x4[] bindposes = mesh.bindposes;
            var skin = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
            {
                // ボーンが無い・バインドポーズが無いものは、レンダラー自身の行列で代用する
                skin[b] = bones[b] != null && b < bindposes.Length ? bones[b].localToWorldMatrix * bindposes[b] : own;
            }

            int offset = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                int stored = bonesPerVertex[v];
                // Unity は、ウェイトの大きい順に上位 weightLimit 本だけを使い、合計が 1 になるよう正規化する
                int count = Math.Min(stored, weightLimit);
                float total = 0f;
                for (int i = 0; i < count; i++) total += boneWeights[offset + i].weight;
                if (count == 0 || total <= 0f)
                {
                    result[v] = own;
                }
                else
                {
                    var sum = new Matrix4x4();
                    for (int i = 0; i < count; i++)
                    {
                        BoneWeight1 bw = boneWeights[offset + i];
                        Matrix4x4 m = (uint)bw.boneIndex < (uint)skin.Length ? skin[bw.boneIndex] : own;
                        float w = bw.weight / total;
                        for (int e = 0; e < 16; e++) sum[e] += m[e] * w;
                    }
                    result[v] = sum;
                }
                offset += stored;
            }
            return result;
        }

        /// <summary>3x3 部分の逆行列を inverse に入れ、1 / |det|^(1/3) を返す。ほぼ特異なら 0 を返し、inverse は 0。</summary>
        static float Invert3x3(Matrix4x4 m, out Matrix4x4 inverse)
        {
            inverse = new Matrix4x4();
            float a = m.m00, b = m.m01, c = m.m02;
            float d = m.m10, e = m.m11, f = m.m12;
            float g = m.m20, h = m.m21, i = m.m22;
            float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            float maxElement = Mathf.Max(Mathf.Max(Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)), Mathf.Max(Mathf.Abs(c), Mathf.Abs(d))),
                Mathf.Max(Mathf.Max(Mathf.Abs(e), Mathf.Abs(f)), Mathf.Max(Mathf.Abs(g), Mathf.Max(Mathf.Abs(h), Mathf.Abs(i)))));
            if (Mathf.Abs(det) <= 1e-6f * maxElement * maxElement * maxElement) return 0f;

            float inv = 1f / det;
            inverse.m00 = (e * i - f * h) * inv; inverse.m01 = (c * h - b * i) * inv; inverse.m02 = (b * f - c * e) * inv;
            inverse.m10 = (f * g - d * i) * inv; inverse.m11 = (a * i - c * g) * inv; inverse.m12 = (c * d - a * f) * inv;
            inverse.m20 = (d * h - e * g) * inv; inverse.m21 = (b * g - a * h) * inv; inverse.m22 = (a * e - b * d) * inv;
            return 1f / Mathf.Pow(Mathf.Abs(det), 1f / 3f);
        }

        /// <summary>頂点の、メッシュ座標 → ワールド座標の行列。</summary>
        public Matrix4x4 GetVertexMatrix(int vertex) => _vertexMatrices[vertex];

        /// <summary>
        /// 粒子の位置から CurrentWorldVertices を更新する。確保はしない。
        /// 計算は、ソルバーのカーネル（<see cref="ClothSolver.Kernel"/>）が <see cref="IShapeKernel"/> なら（Burst など）それに任せ、そうでなければ C# で行う。
        /// </summary>
        public void UpdateCurrentShape()
        {
            var kernel = _solver.Kernel as IShapeKernel;
            _cloth.DeformWelded(_solver.Positions, _welded, kernel);
            // UnityEngine.Vector3 と System.Numerics.Vector3 はどちらも float 3つなので、コピーせずに読み替える
            ShapeKernels.ExpandVertices(kernel,
                MemoryMarshal.Cast<Vector3, NVector3>(new Span<Vector3>(CurrentWorldVertices)),
                MemoryMarshal.Cast<Vector3, NVector3>(new ReadOnlySpan<Vector3>(_baseWorld)),
                _welded, _toposRest, _vertexToWelded);
            _previewDirty = true;
            _bvhDirty = true;
        }

        /// <summary>現在の形に対するレイキャスト（両面）。一番近い交差を返す。</summary>
        public bool Raycast(Ray ray, out Vector3 hitPoint, out Vector3 hitNormal)
        {
            return Raycast(ray, out hitPoint, out hitNormal, out _);
        }

        /// <summary>
        /// 現在の形に対するレイキャスト（両面）。一番近い交差を返す。
        /// 面積のない三角形（頂点が同じ位置に潰れたもの）には当たらない。
        /// </summary>
        /// <param name="hitTriangle">
        /// 当たった三角形の番号。元メッシュの <c>mesh.triangles</c> の3つ組の番号（全サブメッシュを順に並べたもの）。
        /// BeginGrab・PaintPins に渡すと、同じ部品（島）の粒子だけを対象にできる。当たらなかったときは -1。
        /// </param>
        public bool Raycast(Ray ray, out Vector3 hitPoint, out Vector3 hitNormal, out int hitTriangle)
        {
            if (_bvhDirty)
            {
                _bvh.Refit(_welded, _solver.Kernel as IShapeKernel);
                _bvhDirty = false;
            }

            var origin = new NVector3(ray.origin.x, ray.origin.y, ray.origin.z);
            var direction = new NVector3(ray.direction.x, ray.direction.y, ray.direction.z);
            if (!_bvh.Raycast(origin, direction, _welded, out float distance, out int triangle))
            {
                hitPoint = Vector3.zero;
                hitNormal = Vector3.up;
                hitTriangle = -1;
                return false;
            }

            GP02.Core.MeshTopology topology = _cloth.Topology;
            hitPoint = ray.origin + ray.direction * distance;
            NVector3 a = _welded[topology.Triangles[3 * triangle]];
            NVector3 n = NVector3.Cross(_welded[topology.Triangles[3 * triangle + 1]] - a, _welded[topology.Triangles[3 * triangle + 2]] - a);
            float length = n.Length();
            Vector3 normal = length > 0f ? new Vector3(n.X, n.Y, n.Z) / length : Vector3.up;
            hitNormal = Vector3.Dot(normal, ray.direction) > 0f ? -normal : normal;
            hitTriangle = topology.TriangleToSource[triangle];
            return true;
        }

        // 元メッシュの三角形が乗っている島。三角形が無い・潰れて計算から外れている・範囲外なら -1（絞り込まない）
        int IslandOfMeshTriangle(int meshTriangle)
        {
            GP02.Core.MeshTopology topology = _cloth.Topology;
            if ((uint)meshTriangle >= (uint)topology.SourceToTriangle.Length) return -1;
            int triangle = topology.SourceToTriangle[meshTriangle];
            return triangle < 0 ? -1 : topology.TriangleIsland[triangle];
        }

        // island が負なら全粒子から、そうでなければその島の粒子だけから、一番近いものを選ぶ
        int FindNearestParticle(NVector3 point, int island)
        {
            if (island < 0) return _solver.FindNearestParticle(point);

            int[] particleIsland = _cloth.ParticleIsland;
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < positions.Length; i++)
            {
                if (particleIsland[i] != island) continue;
                float sq = NVector3.DistanceSquared(positions[i], point);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// 指定点の近くの粒子をつまむ。元に戻す用の記録は、呼び出し側（ウィンドウ）が Unity の Undo で取る。
        /// 重ね着では、近くにある別の布の粒子をつまむことがあるので、レイキャストで当たった三角形を渡す版を使うこと。
        /// </summary>
        /// <param name="brushRadius">つまむ範囲（メッシュに沿った距離、メートル）</param>
        public bool BeginGrab(Vector3 worldPoint, float brushRadius)
        {
            return BeginGrab(worldPoint, brushRadius, -1);
        }

        /// <summary>
        /// 指定点の近くの粒子をつまむ。粒子は、当たった三角形と同じ部品（島）に乗っているものの中から選ぶ
        /// （重ね着で、外側の布をクリックしたのに内側の布の粒子のほうが近い、ということを防ぐ）。
        /// </summary>
        /// <param name="hitTriangle">Raycast が返した三角形の番号。負の値や範囲外なら、すべての粒子から選ぶ</param>
        public bool BeginGrab(Vector3 worldPoint, float brushRadius, int hitTriangle)
        {
            if (_solver.GrabbedParticle >= 0) Release();

            var point = new NVector3(worldPoint.x, worldPoint.y, worldPoint.z);
            int particle = FindNearestParticle(point, IslandOfMeshTriangle(hitTriangle));
            if (particle < 0) return false;

            _grabParticle = particle;
            _grabRadius = brushRadius;
            _grabPointStart = point;
            _grabParticleStart = _solver.Positions[particle];
            // ここで範囲を決めておく（動かすのは次の DragTo から）
            _solver.Grab(particle, _grabParticleStart, brushRadius);
            return true;
        }

        /// <summary>つまんだ点を、カーソルの動いた分だけ動かす（つまみ始めからの差で決める）。</summary>
        public void DragTo(Vector3 worldPointOfCursor)
        {
            if (_grabParticle < 0) return;
            var cursor = new NVector3(worldPointOfCursor.x, worldPointOfCursor.y, worldPointOfCursor.z);
            _solver.Grab(_grabParticle, _grabParticleStart + (cursor - _grabPointStart), _grabRadius);
        }

        /// <summary>つまみを外す。</summary>
        public void Release()
        {
            _grabParticle = -1;
            _solver.Release();
        }

        /// <summary>
        /// ソルバーを1回だけ動かして、計算の部品（Burst など）の初回のコンパイルを済ませる。
        /// 最初のドラッグで一瞬止まるのを避けるため、編集を始めるときに呼ぶ。粒子の位置は元に戻すので、形は変わらない。
        /// </summary>
        /// <returns>かかった時間（ミリ秒）</returns>
        public double WarmUp()
        {
            var timer = Stopwatch.StartNew();
            NVector3[] saved = _solver.Positions.ToArray();
            _solver.Solve(1);
            _solver.SetPositions(saved);
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds;
        }

        /// <summary>元の形に戻す（ピンは保持）。</summary>
        public void ResetAll()
        {
            _grabParticle = -1;
            _solver.ResetToRest();
            UpdateCurrentShape();
        }

        /// <summary>今の粒子の位置とピンを state に書く（Unity の Undo の記録先）。</summary>
        public void WriteState(ClothEditState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            int count = ParticleCount;
            if (state.Positions == null || state.Positions.Length != count * 3) state.Positions = new float[count * 3];
            if (state.Pinned == null || state.Pinned.Length != count) state.Pinned = new bool[count];

            ReadOnlySpan<NVector3> positions = _solver.Positions;
            float[] destination = state.Positions;
            for (int i = 0; i < count; i++)
            {
                destination[i * 3] = positions[i].X;
                destination[i * 3 + 1] = positions[i].Y;
                destination[i * 3 + 2] = positions[i].Z;
                state.Pinned[i] = _solver.IsPinned(i);
            }
        }

        /// <summary>
        /// state の内容（粒子の位置とピン）をソルバーに反映する。つまみは外れる。
        /// 粒子数が合わない（古いセッションの記録など）ときは何もせず false。
        /// </summary>
        public bool ReadState(ClothEditState state)
        {
            if (state == null || state.Positions == null || state.Pinned == null) return false;
            int count = ParticleCount;
            if (state.Positions.Length != count * 3 || state.Pinned.Length != count) return false;

            if (_stateWork == null) _stateWork = new NVector3[count];
            float[] source = state.Positions;
            for (int i = 0; i < count; i++) _stateWork[i] = new NVector3(source[i * 3], source[i * 3 + 1], source[i * 3 + 2]);

            _grabParticle = -1;
            _solver.SetPositions(_stateWork);
            _pinnedCount = 0;
            for (int i = 0; i < count; i++)
            {
                _solver.SetPinned(i, state.Pinned[i]);
                if (state.Pinned[i]) _pinnedCount++;
            }
            UpdateCurrentShape();
            return true;
        }

        /// <summary>今の粒子の位置とピンが、state に書いてある内容と完全に同じか（動かさずに終わった操作を見分けるため）。</summary>
        public bool MatchesState(ClothEditState state)
        {
            int count = ParticleCount;
            if (state == null || state.Positions == null || state.Pinned == null) return false;
            if (state.Positions.Length != count * 3 || state.Pinned.Length != count) return false;

            ReadOnlySpan<NVector3> positions = _solver.Positions;
            float[] stored = state.Positions;
            for (int i = 0; i < count; i++)
            {
                if (stored[i * 3] != positions[i].X || stored[i * 3 + 1] != positions[i].Y || stored[i * 3 + 2] != positions[i].Z) return false;
                if (state.Pinned[i] != _solver.IsPinned(i)) return false;
            }
            return true;
        }

        /// <summary>現在位置がワールド座標で radius 以内の粒子を、ピン留め（または解除）する。変えた粒子の数を返す。</summary>
        public int PaintPins(Vector3 worldPoint, float radius, bool pinned)
        {
            return PaintPins(worldPoint, radius, pinned, -1);
        }

        /// <summary>
        /// 現在位置がワールド座標で radius 以内で、かつ当たった三角形と同じ部品（島）の粒子を、ピン留め（または解除）する。
        /// 重ね着で、近くにある内側の布まで巻き込まないための版。変えた粒子の数を返す。
        /// </summary>
        /// <param name="hitTriangle">Raycast が返した三角形の番号。負の値や範囲外なら、島で絞り込まない</param>
        public int PaintPins(Vector3 worldPoint, float radius, bool pinned, int hitTriangle)
        {
            int island = IslandOfMeshTriangle(hitTriangle);
            int[] particleIsland = _cloth.ParticleIsland;
            var point = new NVector3(worldPoint.x, worldPoint.y, worldPoint.z);
            float radiusSq = radius * radius;
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int changed = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (island >= 0 && particleIsland[i] != island) continue;
                if (NVector3.DistanceSquared(positions[i], point) > radiusSq) continue;
                if (_solver.IsPinned(i) == pinned) continue;
                _solver.SetPinned(i, pinned);
                _pinnedCount += pinned ? 1 : -1;
                changed++;
            }
            return changed;
        }

        /// <summary>ピン留めされた粒子の位置（ワールド）を result に入れる。maxCount を超えるときは間引く。</summary>
        public void GetPinnedPositions(List<Vector3> result, int maxCount = int.MaxValue)
        {
            GetPinnedPositions(result, null, maxCount);
        }

        /// <summary>
        /// ピン留めされた粒子の位置（ワールド）を result に、乗っている部品（島）の番号を islands に入れる（同じ順）。
        /// islands を <see cref="PinNear(IReadOnlyList{Vector3}, float, IReadOnlyList{int})"/> に渡すと、重ね着でも元の層だけを留められる。
        /// </summary>
        public void GetPinnedPositions(List<Vector3> result, List<int> islands, int maxCount = int.MaxValue)
        {
            result.Clear();
            islands?.Clear();
            if (_pinnedCount == 0 || maxCount <= 0) return;
            int stride = Mathf.Max(1, (_pinnedCount + maxCount - 1) / maxCount);
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int[] particleIsland = _cloth.ParticleIsland;
            int seen = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!_solver.IsPinned(i)) continue;
                if (seen++ % stride != 0) continue;
                result.Add(new Vector3(positions[i].X, positions[i].Y, positions[i].Z));
                islands?.Add(particleIsland[i]);
            }
        }

        /// <summary>ピン留めされた粒子の番号を result に入れる。</summary>
        public void GetPinnedParticles(List<int> result)
        {
            result.Clear();
            if (_pinnedCount == 0) return;
            for (int i = 0; i < ParticleCount; i++)
            {
                if (_solver.IsPinned(i)) result.Add(i);
            }
        }

        /// <summary>
        /// 粒子の番号でピン留めする。<see cref="ParticleCloth.Rebase"/> した布のように、粒子の番号が同じセッションへピンを引き継ぐ用。
        /// 新しくピン留めした粒子の数を返す。範囲外の番号は無視する。
        /// </summary>
        public int PinParticles(IReadOnlyList<int> particles)
        {
            if (particles == null) return 0;
            int added = 0;
            int count = ParticleCount;
            for (int k = 0; k < particles.Count; k++)
            {
                int i = particles[k];
                if ((uint)i >= (uint)count || _solver.IsPinned(i)) continue;
                _solver.SetPinned(i, true);
                _pinnedCount++;
                added++;
            }
            return added;
        }

        /// <summary>
        /// 位置が worldPositions のどれかから radius 以内にある粒子をピン留めする（セッションを作り直したときにピンを引き継ぐ用）。
        /// 新しくピン留めした粒子の数を返す。全粒子×全位置の総当たりを避けるため、粒子を格子に入れて探す。
        /// </summary>
        public int PinNear(IReadOnlyList<Vector3> worldPositions, float radius)
        {
            return PinNear(worldPositions, radius, null);
        }

        /// <summary>
        /// PinNear の、部品（島）の番号つき版。islands[k] が worldPositions[k] の島で、同じ島の粒子だけをピン留めする
        /// （重ね着で、近くにある別の層まで留めないため）。islands が null や、番号がこのメッシュの島の数以上のものは、島で絞り込まない。
        /// </summary>
        public int PinNear(IReadOnlyList<Vector3> worldPositions, float radius, IReadOnlyList<int> islands)
        {
            if (worldPositions == null || worldPositions.Count == 0 || radius <= 0f) return 0;

            ReadOnlySpan<NVector3> positions = _solver.Positions;
            var grid = new PointGrid(radius);
            for (int i = 0; i < positions.Length; i++) grid.Add(i, positions[i]);

            float radiusSq = radius * radius;
            var candidates = new List<int>();
            int[] particleIsland = _cloth.ParticleIsland;
            int islandCount = _cloth.Topology.IslandCount;
            int added = 0;
            for (int k = 0; k < worldPositions.Count; k++)
            {
                int island = islands != null && k < islands.Count ? islands[k] : -1;
                if (island >= islandCount) island = -1;
                Vector3 w = worldPositions[k];
                var point = new NVector3(w.x, w.y, w.z);
                grid.Query(point, radius, candidates);
                for (int c = 0; c < candidates.Count; c++)
                {
                    int i = candidates[c];
                    if (_solver.IsPinned(i) || NVector3.DistanceSquared(positions[i], point) > radiusSq) continue;
                    if (island >= 0 && particleIsland[i] != island) continue;
                    _solver.SetPinned(i, true);
                    _pinnedCount++;
                    added++;
                }
            }
            return added;
        }

        /// <summary>
        /// 現在の形を、元メッシュの頂点ごとのシェイプキーの差分（メッシュ座標）にする。
        /// 線形ブレンドスキニングでは、メッシュ座標の差分 d を足すとワールドで M(3x3) * d だけ動くので、
        /// 位置は M(3x3) の逆行列を掛けると見た目どおりに戻る。法線は相似変換とみなした近似。
        /// </summary>
        public void BuildMeshSpaceDeltas(Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            int count = VertexCount;
            if (deltaVertices == null || deltaVertices.Length != count) throw new ArgumentException("頂点数が一致しません。", nameof(deltaVertices));
            if (deltaNormals == null || deltaNormals.Length != count) throw new ArgumentException("頂点数が一致しません。", nameof(deltaNormals));

            if (_deltaVerticesWork == null)
            {
                _deltaVerticesWork = new NVector3[count];
                _deltaNormalsWork = new NVector3[count];
            }
            BlendShapeBuilder.Build(_cloth, _solver.Positions, _deltaVerticesWork, _deltaNormalsWork);

            for (int v = 0; v < count; v++)
            {
                Matrix4x4 inv = _inverseMatrices[v];
                NVector3 dw = _deltaVerticesWork[v];
                deltaVertices[v] = new Vector3(
                    inv.m00 * dw.X + inv.m01 * dw.Y + inv.m02 * dw.Z,
                    inv.m10 * dw.X + inv.m11 * dw.Y + inv.m12 * dw.Z,
                    inv.m20 * dw.X + inv.m21 * dw.Y + inv.m22 * dw.Z);

                // 転置を掛ける（相似変換 s*R なら、転置 / s が回転の逆になる）
                Matrix4x4 m = _vertexMatrices[v];
                NVector3 nw = _deltaNormalsWork[v];
                float s = _inverseScale[v];
                deltaNormals[v] = new Vector3(
                    (m.m00 * nw.X + m.m10 * nw.Y + m.m20 * nw.Z) * s,
                    (m.m01 * nw.X + m.m11 * nw.Y + m.m21 * nw.Z) * s,
                    (m.m02 * nw.X + m.m12 * nw.Y + m.m22 * nw.Z) * s);
            }
        }

        /// <summary>元メッシュの複製に、現在の形をシェイプキーとして加えたメッシュを作る（アセットには保存しない）。</summary>
        public Mesh CreateMeshWithBlendShape(string shapeName)
        {
            var deltaVertices = new Vector3[VertexCount];
            var deltaNormals = new Vector3[VertexCount];
            BuildMeshSpaceDeltas(deltaVertices, deltaNormals);

            Mesh mesh = Object.Instantiate(_sourceMesh);
            string sourceName = _sourceMesh.name;
            mesh.name = sourceName.EndsWith(MeshSuffix) ? sourceName : sourceName + MeshSuffix;
            MeshBridge.AddBlendShape(mesh, shapeName, deltaVertices, deltaNormals);
            return mesh;
        }

        /// <summary>
        /// 現在の形をシェイプキーとして保存し、シーンのレンダラーに反映する。保存したメッシュを返す。
        /// 以降のセッションは、返したメッシュを元として作り直すこと（シェイプキー 100 込みのワールド形状が新しい基準になる）。
        /// </summary>
        public Mesh ApplyToScene(string shapeName, string folder = "Assets/GP02/Meshes")
        {
            if (!IsTargetAlive) throw new InvalidOperationException("対象のレンダラーがありません。");
            folder = folder.TrimEnd('/');

            Mesh result;
            string sourcePath = AssetDatabase.GetAssetPath(_sourceMesh);
            bool isOwnAsset = !string.IsNullOrEmpty(sourcePath) && sourcePath.EndsWith(".asset")
                && sourcePath.StartsWith(folder + "/") && AssetDatabase.IsMainAsset(_sourceMesh);

            if (isOwnAsset)
            {
                // GP02 が作ったメッシュには、そのままシェイプキーを足す（複製が増え続けないように）
                var deltaVertices = new Vector3[VertexCount];
                var deltaNormals = new Vector3[VertexCount];
                BuildMeshSpaceDeltas(deltaVertices, deltaNormals);
                UnityEditor.Undo.RegisterCompleteObjectUndo(_sourceMesh, "GP02 シェイプキーを追加");
                MeshBridge.AddBlendShape(_sourceMesh, shapeName, deltaVertices, deltaNormals);
                EditorUtility.SetDirty(_sourceMesh);
                AssetDatabase.SaveAssets();
                result = _sourceMesh;
            }
            else
            {
                Mesh created = CreateMeshWithBlendShape(shapeName);
                EnsureFolder(folder);
                string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(created.name)}.asset");
                AssetDatabase.CreateAsset(created, path);

                UnityEditor.Undo.RecordObject(GetMeshOwner(), "GP02 メッシュを差し替え");
                SetMesh(created);
                EditorUtility.SetDirty(GetMeshOwner());
                AssetDatabase.SaveAssets();
                result = created;
            }

            if (_renderer is SkinnedMeshRenderer skinned)
            {
                int index = result.GetBlendShapeIndex(shapeName);
                UnityEditor.Undo.RecordObject(skinned, "GP02 シェイプキーの重み");
                skinned.SetBlendShapeWeight(index, 100f);
                EditorUtility.SetDirty(skinned);
            }
            return result;
        }

        Object GetMeshOwner() => _renderer is SkinnedMeshRenderer ? (Object)_renderer : _renderer.GetComponent<MeshFilter>();

        void SetMesh(Mesh mesh)
        {
            if (_renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else _renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>"GP02_1", "GP02_2" … のうち、元メッシュのシェイプキーと重ならない名前。</summary>
        public string SuggestShapeName()
        {
            for (int n = 1; ; n++)
            {
                string name = ShapeNamePrefix + n;
                if (_sourceMesh.GetBlendShapeIndex(name) < 0) return name;
            }
        }

        /// <summary>編集中の形をシーンに描く（元のレンダラーは隠してある）。サブメッシュごとにマテリアルを割り当てる。</summary>
        public void DrawPreview(Camera camera)
        {
            if (_disposed || !IsTargetAlive) return;
            if (_previewDirty)
            {
                _previewMesh.vertices = CurrentWorldVertices;
                _previewMesh.RecalculateNormals();
                _previewMesh.RecalculateBounds();
                _previewDirty = false;
            }

            int layer = _renderer.gameObject.layer;
            for (int sub = 0; sub < _previewMesh.subMeshCount; sub++)
            {
                if (_materials.Length == 0) break;
                Material material = _materials[Mathf.Min(sub, _materials.Length - 1)];
                if (material == null) continue;
                Graphics.DrawMesh(_previewMesh, Matrix4x4.identity, material, layer, camera, sub);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_renderer != null) _renderer.forceRenderingOff = false;
            if (_previewMesh != null) Object.DestroyImmediate(_previewMesh);
        }
    }
}
