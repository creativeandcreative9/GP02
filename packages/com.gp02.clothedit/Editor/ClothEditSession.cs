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
    /// 1つ（または重ね着の複数）のレンダラーに対する編集の本体。画面（ウィンドウ）を持たないので、EditMode テストで確かめられる。
    ///
    /// 複数のレンダラーを渡すと、ポーズ済みのワールド座標の頂点と三角形をつないで1つの布にする（島はレンダラーごとに分かれる）。
    /// 頂点の番号（<see cref="CurrentWorldVertices"/> など）は、レンダラーの順に頂点を並べたもの、
    /// 三角形の番号（<see cref="Raycast(Ray, out Vector3, out Vector3, out int)"/> の hitTriangle）は、レンダラーの順に mesh.triangles を並べたもの。
    /// 重ね着の層どうしは、接触の制約（<see cref="LayerContacts"/>）で、外の布を内へ押すと内の布も押されるようにする。
    ///
    /// 編集はワールド座標（現在のポーズ、実寸のメートル）で行う。粒子の 1cm 間隔をアバターの実寸にするためと、
    /// シーンに見えている形のまま触れるようにするため。
    /// シェイプキーとして保存するときは、頂点ごとの変換行列（メッシュ座標 → ワールド座標）の逆で、メッシュ座標の差分に直す。
    /// </summary>
    public sealed class ClothEditSession : IDisposable
    {
        const string ShapeNamePrefix = "GP02_";
        const string MeshSuffix = "_GP02";

        // まとめて編集するレンダラーごとの情報
        sealed class Part
        {
            public Renderer Renderer;
            public Mesh SourceMesh;
            public int VertexStart;               // まとめた頂点の配列（_baseWorld など）での先頭
            public int VertexCount;
            public int TriangleStart;             // まとめた三角形（mesh.triangles を並べたもの）での先頭の3つ組の番号
            public int TriangleCount;
            public float[] CreatedWeights;        // セッション作成時のシェイプキーの重み（保存の取り消しを見つけるため）
            public Mesh PreviewMesh;
            public Material[] Materials;
        }

        readonly Part[] _parts;
        readonly Renderer[] _renderers;
        readonly int _vertexCount;
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
        NVector3[] _stateWork;
        float[] _stretchWork;                     // 粒子ごとの伸び（ヒートマップ用）
        int _shapeVersion;                        // 形が変わるたびに増やす。伸びを測り直すかどうかの判断に使う
        int _stretchVersion = -1;
        float _previewBlend = 1f;
        Vector3[] _blendWork;
        NVector3[] _deltaVerticesWork;
        NVector3[] _deltaNormalsWork;
        bool _previewDirty = true;
        bool _disposed;

        // つまんでいる間の状態
        int _grabParticle = -1;
        BrushOptions _grabOptions;                // つまむ範囲の決め方（BeginGrab で写し取ったもの）
        NVector3 _grabPointStart;

        int _pinnedCount;
        readonly List<int> _pinWork = new List<int>();   // 矩形で選んだ粒子（使い回す）

        /// <summary>対象のレンダラー（複数のときは最初のもの）。</summary>
        public Renderer Renderer => _parts[0].Renderer;

        /// <summary>対象のメッシュ（複数のときは最初のレンダラーのもの）。</summary>
        public Mesh SourceMesh => _parts[0].SourceMesh;

        /// <summary>まとめて編集しているレンダラー（作ったときの順）。</summary>
        public IReadOnlyList<Renderer> Renderers => _renderers;

        /// <summary>まとめて編集しているレンダラーの数。</summary>
        public int RendererCount => _parts.Length;

        /// <summary>index 番目のレンダラーの、編集を始めたときのメッシュ。</summary>
        public Mesh GetSourceMesh(int rendererIndex) => _parts[rendererIndex].SourceMesh;

        /// <summary>index 番目のレンダラーの頂点が、まとめた頂点（<see cref="CurrentWorldVertices"/> など）のどこから何個並んでいるか。</summary>
        public void GetVertexRange(int rendererIndex, out int start, out int count)
        {
            start = _parts[rendererIndex].VertexStart;
            count = _parts[rendererIndex].VertexCount;
        }

        /// <summary>重ね着の層どうしの接触の組（無ければ組の数が 0）。</summary>
        public LayerContacts LayerContacts { get; }

        public ParticleCloth Cloth => _cloth;
        public ClothSolver Solver => _solver;

        /// <summary>
        /// 計算の高速化の状態を表す文字列（ウィンドウに表示する）。
        /// Burst の有無はこのアセンブリから見えないので、ソルバーのカーネルの名前で判断する。
        /// </summary>
        public static string DescribeAcceleration(IClothSolverKernel kernel)
        {
            if (kernel == null) return L10n.Tr("なし（Burst を入れると速くなります）", "None (install Burst to speed things up)");
            return kernel.IsAccelerated
                ? L10n.Tr($"{kernel.Name}（有効）", $"{kernel.Name} (enabled)")
                : L10n.Tr($"{kernel.Name}（無効。Jobs > Burst > Enable Compilation を確認してください）", $"{kernel.Name} (disabled. Check Jobs > Burst > Enable Compilation)");
        }

        /// <summary>準備（粒子化）にかかった時間（ミリ秒）。隙間を測る時間は含まない（<see cref="RestGap"/> の Milliseconds）。</summary>
        public double PrepareMilliseconds { get; }

        /// <summary>
        /// 粒子ごとの、体や内側の布までの隙間（メートル）。元の形より内側へ入ってよい深さとして、
        /// <see cref="ClothEditSettings.ApplyTo"/> がソルバーに渡す。測れなかった（元の形の面の向きが無い）ときは null。
        /// </summary>
        public float[] RestAllowance { get; }

        /// <summary>
        /// <see cref="RestAllowance"/> の、内側の層を通り越さずに測ったもの。層どうしの接触を解かないとき
        /// （<see cref="ClothEditSettings.KeepLayersApart"/> がオフ）に使う。重ね着が無ければ RestAllowance と同じ。
        /// </summary>
        public float[] RestAllowanceWithoutLayers { get; }

        /// <summary>設定に合う、ソルバーに渡す隙間（<see cref="ClothEditSettings.ApplyTo"/> に渡す）。</summary>
        public float[] GetRestAllowance(bool keepLayersApart) => keepLayersApart ? RestAllowance : RestAllowanceWithoutLayers;

        /// <summary>隙間を測った結果（平均・最小・最大、かかった時間など）。測れなかったときは null。</summary>
        public RestGapMeasurer.Result RestGap { get; }
        public int ParticleCount => _cloth.Model.ParticleCount;

        /// <summary>頂点の数（複数のレンダラーのときは合計）。</summary>
        public int VertexCount => _vertexCount;

        /// <summary>現在の形のワールド頂点（元メッシュの頂点数。複数のレンダラーのときは順に並べたもの）。UpdateCurrentShape で更新される。</summary>
        public Vector3[] CurrentWorldVertices { get; }

        public int PinnedCount => _pinnedCount;
        public bool IsGrabbing => _grabParticle >= 0;

        /// <summary>対象のレンダラーが（すべて）消されていないか。</summary>
        public bool IsTargetAlive
        {
            get
            {
                foreach (Part part in _parts)
                {
                    if (part.Renderer == null) return false;
                }
                return true;
            }
        }

        public ClothEditSession(Renderer renderer) : this(renderer, null)
        {
        }

        /// <summary>複数のレンダラー（重ね着など）をまとめて1つの布として編集する。</summary>
        public ClothEditSession(IReadOnlyList<Renderer> renderers) : this(renderers, null)
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
            : this(new[] { renderer ?? throw new ArgumentNullException(nameof(renderer)) }, cloth)
        {
        }

        /// <summary>
        /// 複数のレンダラーをまとめて、既存の布を使ってセッションを作る（<see cref="ClothEditSession(Renderer, ParticleCloth)"/> の複数版）。
        /// 布は、レンダラーの順に頂点と三角形を並べたメッシュに合うこと。
        /// </summary>
        /// <param name="cloth">まとめたメッシュに合う布。null なら <see cref="ParticleCloth.Build"/> で作る</param>
        public ClothEditSession(IReadOnlyList<Renderer> renderers, ParticleCloth cloth)
        {
            if (renderers == null) throw new ArgumentNullException(nameof(renderers));
            if (renderers.Count == 0) throw new ArgumentException(L10n.Tr("対象のレンダラーがありません。", "There are no target renderers."), nameof(renderers));
            _parts = new Part[renderers.Count];
            _renderers = new Renderer[renderers.Count];
            var timer = Stopwatch.StartNew();
            var baseVerticesPerPart = new Vector3[renderers.Count][];
            int vertexTotal = 0, triangleTotal = 0;
            for (int r = 0; r < renderers.Count; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) throw new ArgumentNullException(nameof(renderers));
                for (int q = 0; q < r; q++)
                {
                    if (renderers[q] == renderer) throw new ArgumentException(L10n.Tr($"「{renderer.name}」が2回指定されています。", $"\"{renderer.name}\" is specified twice."), nameof(renderers));
                }
                Mesh mesh = GetMesh(renderer);
                if (mesh == null) throw new ArgumentException(L10n.Tr("メッシュがありません。MeshFilter か SkinnedMeshRenderer のメッシュを確認してください。", "There is no mesh. Check the mesh of the MeshFilter or SkinnedMeshRenderer."), nameof(renderers));
                int triangles = (int)CountTriangles(mesh);
                if (cloth == null && triangles == 0)
                {
                    throw new ArgumentException(L10n.Tr($"「{mesh.name}」には三角形（面）がありません。面のあるメッシュを指定してください。", $"\"{mesh.name}\" has no triangles (faces). Choose a mesh that has faces."), nameof(renderers));
                }
                _parts[r] = new Part
                {
                    Renderer = renderer,
                    SourceMesh = mesh,
                    VertexStart = vertexTotal,
                    VertexCount = mesh.vertexCount,
                    TriangleStart = triangleTotal,
                    TriangleCount = triangles,
                    CreatedWeights = ReadBlendShapeWeights(renderer),
                };
                _renderers[r] = renderer;
                vertexTotal += mesh.vertexCount;
                triangleTotal += triangles;
            }
            _vertexCount = vertexTotal;
            if (cloth != null && !IsClothCompatible(cloth, vertexTotal, triangleTotal))
            {
                throw new ArgumentException(L10n.Tr("布がこのメッシュに合いません（頂点数か三角形数が違います）。", "The cloth does not match this mesh (different vertex or triangle count)."), nameof(cloth));
            }

            _vertexMatrices = new Matrix4x4[vertexTotal];
            _baseWorld = new Vector3[vertexTotal];
            _inverseMatrices = new Matrix4x4[vertexTotal];
            _inverseScale = new float[vertexTotal];
            for (int r = 0; r < _parts.Length; r++)
            {
                Part part = _parts[r];
                Renderer renderer = part.Renderer;
                Vector3[] baseVertices = part.SourceMesh.vertices;
                // エディタでは Read/Write が無効なメッシュも読めるが、読めない環境では空の配列が返る
                if (baseVertices.Length != part.VertexCount)
                {
                    throw new ArgumentException(L10n.Tr($"「{part.SourceMesh.name}」の頂点を読み取れません。モデルの Import Settings で Read/Write を有効にしてください。", $"Cannot read the vertices of \"{part.SourceMesh.name}\". Enable Read/Write in the model's Import Settings."), nameof(renderers));
                }
                if (renderer is SkinnedMeshRenderer skinned) ApplyBlendShapeWeights(skinned, part.SourceMesh, baseVertices);
                baseVerticesPerPart[r] = baseVertices;

                Matrix4x4[] matrices = ComputeVertexMatrices(renderer, part.SourceMesh);
                // MeshFilter はシェイプキーを描かないので、保存した形がレンダラーからは分からない。
                // 渡された布の静止状態（保存した形）を基準にして、編集中の形を保存前と同じにする。
                // まとめた頂点どうしの位置の差は許容距離（1e-6）以下
                bool baseFromCloth = cloth != null && !(renderer is SkinnedMeshRenderer);
                for (int k = 0; k < baseVertices.Length; k++)
                {
                    int v = part.VertexStart + k;
                    _vertexMatrices[v] = matrices[k];
                    if (baseFromCloth)
                    {
                        NVector3 p = cloth.Topology.Positions[cloth.Topology.VertexToWelded[v]];
                        _baseWorld[v] = new Vector3(p.X, p.Y, p.Z);
                    }
                    else
                    {
                        _baseWorld[v] = matrices[k].MultiplyPoint3x4(baseVertices[k]);
                    }
                    _inverseScale[v] = Invert3x3(matrices[k], out _inverseMatrices[v]);
                }
            }

            if (cloth == null)
            {
                // 全サブメッシュの三角形を順に並べたもの（レンダラーごとに頂点の番号をずらしてつなぐ）。
                // 同じ位置の頂点をまとめるのはレンダラーの中だけにして、島がレンダラーごとに分かれるようにする
                var allTriangles = new int[3 * triangleTotal];
                int[] groups = _parts.Length > 1 ? new int[vertexTotal] : null;
                for (int r = 0; r < _parts.Length; r++)
                {
                    Part part = _parts[r];
                    int[] triangles = part.SourceMesh.triangles;
                    for (int k = 0; k < triangles.Length; k++) allTriangles[3 * part.TriangleStart + k] = triangles[k] + part.VertexStart;
                    if (groups != null)
                    {
                        for (int k = 0; k < part.VertexCount; k++) groups[part.VertexStart + k] = r;
                    }
                }
                cloth = ParticleCloth.Build(MeshBridge.ToNumerics(_baseWorld, Vector3.one), allTriangles, vertexGroups: groups);
            }
            _cloth = cloth;
            _solver = _cloth.CreateSolver();
            _toposRest = _cloth.Topology.Positions;
            _welded = (NVector3[])_toposRest.Clone();
            _bvh = new TriangleBvh(_welded, _cloth.Topology.Triangles);
            _vertexToWelded = _cloth.Topology.VertexToWelded;
            CurrentWorldVertices = (Vector3[])_baseWorld.Clone();

            // 重ね着の層どうしの接触。同じメッシュの別の島（スカートとペチコートなど）も、別のレンダラーの布も同じに扱う
            LayerContacts = LayerContacts.Find(_cloth, _bvh, _welded);
            _solver.SetLayerContacts(LayerContacts);
            timer.Stop();
            PrepareMilliseconds = timer.Elapsed.TotalMilliseconds;

            // 体や内側の布までの隙間。布を渡されたとき（保存後の作り直し）も、今の形で測り直す。
            // まとめて編集するレンダラーは動けるので「体」から外し、内側の層と組になった粒子は、内側の層を通り越して体までを測る
            RestGap = RestGapMeasurer.Measure(_cloth, _bvh, _welded, _renderers, LayerContacts.HasInnerLayer);
            RestAllowance = RestGap?.Gaps;
            RestAllowanceWithoutLayers = RestGap?.GapsWithoutPassThrough;
            _solver.SetRestSurfaceAllowance(RestAllowance);
            if (RestGap != null) UnityEngine.Debug.Log("[GP02] " + DescribeTargets() + ": " + RestGap + $"（層どうしの接触 {LayerContacts.Count} 組）");

            foreach (Part part in _parts)
            {
                part.PreviewMesh = Object.Instantiate(part.SourceMesh);
                part.PreviewMesh.name = part.SourceMesh.name + "_Preview";
                part.PreviewMesh.hideFlags = HideFlags.HideAndDontSave;
                // Graphics.DrawMesh は頂点をそのまま描くので、スキニングとシェイプキーは使わない
                part.PreviewMesh.ClearBlendShapes();
                part.PreviewMesh.boneWeights = new BoneWeight[0];
                part.Materials = part.Renderer.sharedMaterials;
                part.Renderer.forceRenderingOff = true;
            }
        }

        /// <summary>対象の名前（複数のときは「、」でつなぐ）。ログ用。</summary>
        public string DescribeTargets()
        {
            var names = new string[_parts.Length];
            for (int r = 0; r < _parts.Length; r++) names[r] = _parts[r].Renderer != null ? _parts[r].Renderer.name : "(missing)";
            return string.Join("、", names);
        }

        static bool IsClothCompatible(ParticleCloth cloth, long vertexCount, long triangleCount)
        {
            return cloth.Topology.VertexToWelded.Length == vertexCount && cloth.Topology.SourceToTriangle.Length == triangleCount;
        }

        /// <summary>
        /// 布が、レンダラーのメッシュを順に並べたものに合うか（<see cref="ClothEditSession(IReadOnlyList{Renderer}, ParticleCloth)"/> に渡せるか）。
        /// </summary>
        public static bool IsClothCompatible(IReadOnlyList<Renderer> renderers, ParticleCloth cloth)
        {
            if (renderers == null || cloth == null) return false;
            long vertices = 0, triangles = 0;
            foreach (Renderer renderer in renderers)
            {
                Mesh mesh = renderer != null ? GetMesh(renderer) : null;
                if (mesh == null) return false;
                vertices += mesh.vertexCount;
                triangles += CountTriangles(mesh);
            }
            return IsClothCompatible(cloth, vertices, triangles);
        }

        /// <summary>
        /// 布がこのメッシュに合うか。まとめる前の頂点数と、三角形の数（全サブメッシュの合計）が一致すること。
        /// </summary>
        public static bool IsClothCompatible(Mesh mesh, ParticleCloth cloth)
        {
            if (mesh == null || cloth == null) return false;
            return IsClothCompatible(cloth, mesh.vertexCount, CountTriangles(mesh));
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
            if (!IsTargetAlive) return false;
            foreach (Part part in _parts)
            {
                if (GetMesh(part.Renderer) != part.SourceMesh) return false;
                float[] now = ReadBlendShapeWeights(part.Renderer);
                if (now.Length != part.CreatedWeights.Length) return false;
                for (int k = 0; k < now.Length; k++)
                {
                    if (now[k] != part.CreatedWeights[k]) return false;
                }
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
            _shapeVersion++;
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
        /// 当たった三角形の番号。元メッシュの <c>mesh.triangles</c> の3つ組の番号（全サブメッシュを順に並べたもの。
        /// 複数のレンダラーのときは、レンダラーの順に並べたもの。<see cref="TryGetMeshTriangle"/> でレンダラーごとの番号に直せる）。
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

        /// <summary>
        /// Raycast が返した三角形の番号を、レンダラーの番号と、そのメッシュの <c>mesh.triangles</c> の3つ組の番号に直す。範囲外なら false。
        /// </summary>
        public bool TryGetMeshTriangle(int hitTriangle, out int rendererIndex, out int meshTriangle)
        {
            for (int r = 0; r < _parts.Length; r++)
            {
                Part part = _parts[r];
                if (hitTriangle >= part.TriangleStart && hitTriangle < part.TriangleStart + part.TriangleCount)
                {
                    rendererIndex = r;
                    meshTriangle = hitTriangle - part.TriangleStart;
                    return true;
                }
            }
            rendererIndex = -1;
            meshTriangle = -1;
            return false;
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
        /// 指定点に一番近い粒子の番号。当たった三角形と同じ部品（島）の粒子の中から選ぶ（重ね着で別の層を選ばないため）。
        /// 見つからなければ -1。
        /// </summary>
        /// <param name="hitTriangle">Raycast が返した三角形の番号。負の値や範囲外なら、すべての粒子から選ぶ</param>
        public int FindParticle(Vector3 worldPoint, int hitTriangle)
        {
            return FindNearestParticle(ClothEditSymmetry.ToNumerics(worldPoint), IslandOfMeshTriangle(hitTriangle));
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
            return BeginGrab(worldPoint, brushRadius, hitTriangle, null);
        }

        /// <summary>
        /// BeginGrab の、範囲の決め方（重みのカーブ、距離の測り方、対称）を指定する版。
        /// options の半径は使わず、brushRadius を使う。options は写し取るので、あとで書き換えても影響しない。
        /// </summary>
        /// <param name="options">null なら、重み一定・メッシュに沿った距離・対称なし</param>
        public bool BeginGrab(Vector3 worldPoint, float brushRadius, int hitTriangle, BrushOptions options)
        {
            if (_solver.GrabbedParticle >= 0) Release();

            var point = new NVector3(worldPoint.x, worldPoint.y, worldPoint.z);
            int particle = FindNearestParticle(point, IslandOfMeshTriangle(hitTriangle));
            if (particle < 0) return false;

            _grabParticle = particle;
            _grabOptions = options != null ? options.Clone() : new BrushOptions();
            _grabOptions.Radius = brushRadius;
            _grabPointStart = point;
            // ここで範囲を決めておく（動かすのは次の DragTo / UpdateGrab から）
            _solver.Grab(particle, new BrushMotion { Pivot = point }, _grabOptions);
            return true;
        }

        /// <summary>つまんだ点を、カーソルの動いた分だけ動かす（つまみ始めからの差で決める）。</summary>
        public void DragTo(Vector3 worldPointOfCursor)
        {
            UpdateGrab(worldPointOfCursor, Quaternion.identity, 1f);
        }

        /// <summary>
        /// つまんだ範囲を、カーソルの動いた分（つまみ始めからの差）だけ平行移動し、つまんだ点を中心に回転・拡大縮小する。
        /// 回転と拡大率も、つまみ始めの形に対する値。
        /// </summary>
        public void UpdateGrab(Vector3 cursor, Quaternion rotation, float scale)
        {
            if (_grabParticle < 0) return;
            var motion = new BrushMotion(
                new NVector3(cursor.x, cursor.y, cursor.z) - _grabPointStart,
                new System.Numerics.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                scale,
                _grabPointStart);
            _solver.Grab(_grabParticle, motion, _grabOptions);
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

        /// <summary>
        /// PaintPins の、対称版。mirror を渡すと、中心の粒子の鏡像（<see cref="ClothSolver.FindMirrorParticle"/>）の側も同じ半径で塗る。
        /// 鏡像が見つからなければ、中心の側だけ。変えた粒子の数（両側の合計）を返す。
        /// </summary>
        public int PaintPins(Vector3 worldPoint, float radius, bool pinned, int hitTriangle, MirrorPlane? mirror)
        {
            int changed = PaintPins(worldPoint, radius, pinned, hitTriangle);
            if (!mirror.HasValue || !TryFindMirror(worldPoint, hitTriangle, mirror.Value, out Vector3 mirrorPoint, out int mirrorParticle)) return changed;
            return changed + PaintPinsInIsland(mirrorPoint, radius, pinned, _cloth.ParticleIsland[mirrorParticle]);
        }

        // 鏡像の側の中心。鏡像の粒子の今の位置に、中心からのずれ（カーソルと一番近い粒子の差）を鏡に映して足す
        bool TryFindMirror(Vector3 worldPoint, int hitTriangle, MirrorPlane plane, out Vector3 mirrorPoint, out int mirrorParticle)
        {
            mirrorPoint = worldPoint;
            mirrorParticle = -1;
            int seed = FindParticle(worldPoint, hitTriangle);
            if (seed < 0) return false;
            int mirror = _solver.FindMirrorParticle(seed, plane, BrushOptions.DefaultMirrorTolerance);
            if (mirror < 0 || mirror == seed) return false;
            NVector3 offset = ClothEditSymmetry.ToNumerics(worldPoint) - _solver.Positions[seed];
            mirrorPoint = ClothEditSymmetry.ToUnity(_solver.Positions[mirror] + plane.ReflectDirection(offset));
            mirrorParticle = mirror;
            return true;
        }

        int PaintPinsInIsland(Vector3 worldPoint, float radius, bool pinned, int island)
        {
            int[] particleIsland = _cloth.ParticleIsland;
            var point = new NVector3(worldPoint.x, worldPoint.y, worldPoint.z);
            float radiusSq = radius * radius;
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int changed = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (particleIsland[i] != island) continue;
                if (NVector3.DistanceSquared(positions[i], point) > radiusSq) continue;
                if (_solver.IsPinned(i) == pinned) continue;
                _solver.SetPinned(i, pinned);
                _pinnedCount += pinned ? 1 : -1;
                changed++;
            }
            return changed;
        }

        // ---- 範囲を指定したピン留め（矩形・部品全体・すべて外す・反転） ----

        // ピンの状態を変える。変えたら true
        bool SetPin(int particle, bool pinned)
        {
            if (_solver.IsPinned(particle) == pinned) return false;
            _solver.SetPinned(particle, pinned);
            _pinnedCount += pinned ? 1 : -1;
            return true;
        }

        /// <summary>すべてのピンを外す。外した粒子の数を返す。</summary>
        public int ClearPins()
        {
            int changed = 0;
            for (int i = 0; i < ParticleCount; i++)
            {
                if (SetPin(i, false)) changed++;
            }
            return changed;
        }

        /// <summary>ピン留めと、そうでないものを入れ替える。変えた粒子の数（全粒子）を返す。</summary>
        public int InvertPins()
        {
            int count = ParticleCount;
            for (int i = 0; i < count; i++) SetPin(i, !_solver.IsPinned(i));
            return count;
        }

        /// <summary>
        /// 当たった三角形と同じ部品（島）の粒子すべてを、ピン留め（または解除）する。変えた粒子の数を返す。
        /// mirror を渡すと、鏡像の部品（左右に分かれた袖など）も同じようにする。
        /// </summary>
        /// <param name="hitTriangle">Raycast が返した三角形の番号。負の値や範囲外なら何もしない</param>
        public int PinIsland(int hitTriangle, bool pinned, MirrorPlane? mirror = null)
        {
            int island = IslandOfMeshTriangle(hitTriangle);
            if (island < 0) return 0;
            int changed = PinIslandParticles(island, pinned);
            if (!mirror.HasValue) return changed;

            // 島の粒子の鏡像がほぼ別の島に乗っていれば、その島もする（一部がはみ出しているだけの島は巻き込まない）
            int[] particleIsland = _cloth.ParticleIsland;
            int[] map = BuildMirrorMap(mirror.Value);
            var hits = new Dictionary<int, int>();
            int size = 0;
            for (int i = 0; i < particleIsland.Length; i++)
            {
                if (particleIsland[i] != island) continue;
                size++;
                int other = map[i];
                if (other < 0 || particleIsland[other] == island) continue;
                hits.TryGetValue(particleIsland[other], out int count);
                hits[particleIsland[other]] = count + 1;
            }
            foreach (KeyValuePair<int, int> hit in hits)
            {
                if (hit.Value * 2 > size) changed += PinIslandParticles(hit.Key, pinned);
            }
            return changed;
        }

        int PinIslandParticles(int island, bool pinned)
        {
            int[] particleIsland = _cloth.ParticleIsland;
            int changed = 0;
            for (int i = 0; i < particleIsland.Length; i++)
            {
                if (particleIsland[i] == island && SetPin(i, pinned)) changed++;
            }
            return changed;
        }

        /// <summary>
        /// 画面上の矩形に入る粒子を、ピン留め（または解除）する。変えた粒子の数を返す。
        /// 矩形は、ビュー射影行列で画面（0〜1 の正規化したビューポート座標、左下が原点）に映した粒子の位置で判定する。
        /// 他の物に隠れているかは見ない（布どうしの重なりも、画面に入れば対象になる）。
        /// </summary>
        /// <param name="viewProjection">カメラの射影行列 × ワールドからカメラへの行列</param>
        /// <param name="cameraPosition">カメラの位置（表側かどうかの判定に使う。平行投影では使わない）</param>
        /// <param name="cameraForward">カメラの向き（平行投影のときの、表側かどうかの判定に使う）</param>
        /// <param name="orthographic">平行投影か</param>
        /// <param name="viewportRect">矩形（ビューポート座標）</param>
        /// <param name="frontOnly">true なら、カメラ側を向いた粒子（粒子が乗っている面の表が見えるもの）だけを対象にする</param>
        /// <param name="mirror">対称編集の鏡の面。渡すと、選んだ粒子の鏡像の粒子も同じようにする</param>
        public int PinInViewport(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 cameraForward, bool orthographic,
            Rect viewportRect, bool pinned, bool frontOnly, MirrorPlane? mirror = null)
        {
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int[] particleTriangle = _cloth.ParticleTriangle;
            int[] triangles = _cloth.Topology.Triangles;
            _pinWork.Clear();

            for (int i = 0; i < positions.Length; i++)
            {
                NVector3 p = positions[i];
                float clipW = viewProjection.m30 * p.X + viewProjection.m31 * p.Y + viewProjection.m32 * p.Z + viewProjection.m33;
                if (!(clipW > 1e-6f)) continue;   // カメラの後ろ
                float clipX = viewProjection.m00 * p.X + viewProjection.m01 * p.Y + viewProjection.m02 * p.Z + viewProjection.m03;
                float clipY = viewProjection.m10 * p.X + viewProjection.m11 * p.Y + viewProjection.m12 * p.Z + viewProjection.m13;
                float viewportX = clipX / clipW * 0.5f + 0.5f;
                float viewportY = clipY / clipW * 0.5f + 0.5f;
                if (viewportX < viewportRect.xMin || viewportX > viewportRect.xMax || viewportY < viewportRect.yMin || viewportY > viewportRect.yMax) continue;

                if (frontOnly)
                {
                    // 乗っている面の今の向き（MeshNormals と同じ外積の向きが表）。カメラ側を向いているものだけ
                    int t = particleTriangle[i];
                    NVector3 a = _welded[triangles[3 * t]];
                    NVector3 normal = NVector3.Cross(_welded[triangles[3 * t + 1]] - a, _welded[triangles[3 * t + 2]] - a);
                    float facing = orthographic
                        ? -(normal.X * cameraForward.x + normal.Y * cameraForward.y + normal.Z * cameraForward.z)
                        : normal.X * (cameraPosition.x - p.X) + normal.Y * (cameraPosition.y - p.Y) + normal.Z * (cameraPosition.z - p.Z);
                    if (!(facing > 0f)) continue;
                }
                _pinWork.Add(i);
            }

            int changed = 0;
            for (int k = 0; k < _pinWork.Count; k++)
            {
                if (SetPin(_pinWork[k], pinned)) changed++;
            }
            if (mirror.HasValue) changed += PinMirrorOf(_pinWork, mirror.Value, pinned);
            _pinWork.Clear();
            return changed;
        }

        // 粒子ごとの、元の形で鏡に映した位置に一番近い粒子（許容距離内、無ければ -1）。粒子数に比例する時間で済むよう、格子で探す。
        // 面の向きが分かる粒子どうしは、向きがそろうものだけ（重ね着で、近くの別の層を選ばないため）
        int[] BuildMirrorMap(MirrorPlane plane)
        {
            NVector3[] rest = _cloth.Model.RestPositions;
            NVector3[] restNormals = _cloth.Model.RestNormals;
            float tolerance = BrushOptions.DefaultMirrorTolerance;
            var grid = new PointGrid(tolerance);
            for (int i = 0; i < rest.Length; i++) grid.Add(i, rest[i]);

            var map = new int[rest.Length];
            var candidates = new List<int>();
            float limit = tolerance * tolerance;
            for (int i = 0; i < rest.Length; i++)
            {
                NVector3 target = plane.Reflect(rest[i]);
                NVector3 normal = restNormals != null ? plane.ReflectDirection(restNormals[i]) : NVector3.Zero;
                grid.Query(target, tolerance, candidates);
                int best = -1;
                float bestSq = float.MaxValue;
                for (int c = 0; c < candidates.Count; c++)
                {
                    int j = candidates[c];
                    float sq = NVector3.DistanceSquared(rest[j], target);
                    if (sq > limit || sq >= bestSq) continue;
                    if (normal != NVector3.Zero && restNormals[j] != NVector3.Zero && NVector3.Dot(restNormals[j], normal) <= 0.5f) continue;
                    bestSq = sq;
                    best = j;
                }
                map[i] = best;
            }
            return map;
        }

        // 選んだ粒子の鏡像の側も同じようにする。鏡像の側の粒子 j は、j の鏡像の粒子が選ばれていれば対象にする
        // （選んだ粒子から鏡像を引くと、格子のずれで穴があくため、鏡像の側から引く）
        int PinMirrorOf(List<int> selected, MirrorPlane plane, bool pinned)
        {
            if (selected.Count == 0 || !plane.IsValid) return 0;
            int[] map = BuildMirrorMap(plane);
            var isSelected = new bool[map.Length];
            for (int k = 0; k < selected.Count; k++) isSelected[selected[k]] = true;

            int changed = 0;
            for (int j = 0; j < map.Length; j++)
            {
                if (map[j] >= 0 && isSelected[map[j]] && SetPin(j, pinned)) changed++;
            }
            return changed;
        }

        /// <summary>形が変わるたびに増える番号。変わっていなければ、前に調べた結果を使い回せる。</summary>
        public int ShapeVersion => _shapeVersion;

        /// <summary>
        /// 粒子のどれかが、元の形の位置から tolerance（メートル）より離れているか。
        /// 手順の案内で「引いたか」を知るのに使う（ピン留めだけでは形は変わらない）。
        /// </summary>
        public bool IsShapeEdited(float tolerance = 1e-4f)
        {
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            NVector3[] rest = _cloth.Model.RestPositions;
            float limit = tolerance * tolerance;
            for (int i = 0; i < positions.Length; i++)
            {
                if (NVector3.DistanceSquared(positions[i], rest[i]) > limit) return true;
            }
            return false;
        }

        /// <summary>
        /// state（操作を始める前の形）と比べて位置が変わった粒子のうち、ピン留めでないものを一時的にピン留めし、番号を held に入れる。
        /// 元に戻すブラシのあとの仕上げで、戻した所をそのまま留めて、周りの布の側を合わせるため（そうしないと、戻した所が周りの形に引き戻される）。
        /// 仕上げが終わったら <see cref="ReleaseHeld"/> で外す。変えた粒子の数を返す。
        /// </summary>
        public int HoldChangedParticles(ClothEditState state, List<int> held)
        {
            held.Clear();
            int count = ParticleCount;
            if (state == null || state.Positions == null || state.Positions.Length != count * 3) return 0;

            ReadOnlySpan<NVector3> positions = _solver.Positions;
            float[] stored = state.Positions;
            for (int i = 0; i < count; i++)
            {
                if (_solver.IsPinned(i)) continue;
                if (stored[i * 3] == positions[i].X && stored[i * 3 + 1] == positions[i].Y && stored[i * 3 + 2] == positions[i].Z) continue;
                _solver.SetPinned(i, true);
                _pinnedCount++;
                held.Add(i);
            }
            return held.Count;
        }

        /// <summary><see cref="HoldChangedParticles"/> で一時的に留めた粒子のピンを外す。</summary>
        public void ReleaseHeld(List<int> held)
        {
            for (int k = 0; k < held.Count; k++)
            {
                int i = held[k];
                if ((uint)i >= (uint)ParticleCount || !_solver.IsPinned(i)) continue;
                _solver.SetPinned(i, false);
                _pinnedCount--;
            }
            held.Clear();
        }

        /// <summary>
        /// 元に戻すブラシ。指定点の近くの粒子を、元の位置へ strength × 重み だけ近づける（ピン留めの粒子は動かさない）。
        /// mirror を渡すと、鏡像の側にも同じように適用する。形が変わるので <see cref="UpdateCurrentShape"/> まで行う。
        /// 変えた粒子の数（両側の合計）を返す。
        /// </summary>
        /// <param name="hitTriangle">Raycast が返した三角形の番号（重ね着で、当たった布だけを対象にする）</param>
        public int RelaxAt(Vector3 worldPoint, int hitTriangle, float radius, float strength, FalloffCurve curve, BrushDistanceMode mode, MirrorPlane? mirror)
        {
            int seed = FindParticle(worldPoint, hitTriangle);
            if (seed < 0) return 0;
            int changed = _solver.RelaxTowardRest(seed, radius, strength, curve, mode);
            if (mirror.HasValue)
            {
                int other = _solver.FindMirrorParticle(seed, mirror.Value, BrushOptions.DefaultMirrorTolerance);
                if (other >= 0 && other != seed) changed += _solver.RelaxTowardRest(other, radius, strength, curve, mode);
            }
            if (changed > 0) UpdateCurrentShape();
            return changed;
        }

        /// <summary>
        /// ブラシを当てる前の表示用に、範囲に入る粒子と重みを調べる（つまんだ状態は変えない）。
        /// options の半径・カーブ・距離の測り方を使い、mirror があれば鏡像の側の範囲も続けて入れる
        /// （中央線の近くで、鏡像が範囲の2倍以内にあるときは、同じ範囲なので入れない）。
        /// 出力は呼び出し側の配列で、毎回確保しない。範囲が maxCount より多いときは間引く。
        /// </summary>
        /// <param name="seed">範囲の中心の粒子（<see cref="FindParticle"/> で探す）</param>
        /// <returns>書き込んだ数（両側の合計）</returns>
        public int CollectBrushRange(int seed, BrushOptions options, int[] indices, float[] weights, int maxCount)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if ((uint)seed >= (uint)ParticleCount) return 0;

            FalloffCurve curve = options.Falloff ?? FalloffCurve.Constant;
            int mirror = -1;
            if (options.Mirror.HasValue)
            {
                mirror = _solver.FindMirrorParticle(seed, options.Mirror.Value, options.MirrorTolerance);
                if (mirror >= 0 && NVector3.Distance(_solver.Positions[seed], _solver.Positions[mirror]) <= 2f * options.Radius) mirror = -1;
            }

            int first = _solver.CollectBrushRange(seed, options.Radius, curve, options.DistanceMode, indices, weights, mirror >= 0 ? maxCount / 2 : maxCount);
            if (mirror < 0 || mirror == seed) return first;
            return first + _solver.CollectBrushRange(mirror, options.Radius, curve, options.DistanceMode, indices, weights, maxCount - first, first);
        }

        /// <summary>
        /// 伸びのヒートマップ用に、粒子の位置と伸び（0.1 で 10%）を最大 maxCount 個、positions と stretches に入れる。
        /// 伸びている粒子（hotThreshold 以上）を優先し、残りは一定間隔で間引いた粒子で埋める。
        /// 伸びは形が変わったとき（<see cref="UpdateCurrentShape"/> のあと）にだけ測り直す。
        /// </summary>
        public void GetStretchDots(List<Vector3> positions, List<float> stretches, int maxCount, float hotThreshold = 0.02f)
        {
            positions.Clear();
            stretches.Clear();
            int n = ParticleCount;
            if (maxCount <= 0 || n == 0) return;

            if (_stretchWork == null) _stretchWork = new float[n];
            if (_stretchVersion != _shapeVersion)
            {
                _solver.MeasureParticleStretch(_stretchWork);
                _stretchVersion = _shapeVersion;
            }

            int hot = 0;
            for (int i = 0; i < n; i++)
            {
                if (_stretchWork[i] >= hotThreshold) hot++;
            }
            int hotKept = Math.Min(hot, maxCount * 3 / 4);
            int hotStride = hotKept > 0 ? (hot + hotKept - 1) / hotKept : 1;
            int coldBudget = Math.Max(1, maxCount - hotKept);
            int cold = n - hot;
            int coldStride = cold <= coldBudget ? 1 : (cold + coldBudget - 1) / coldBudget;

            ReadOnlySpan<NVector3> p = _solver.Positions;
            int hotSeen = 0, coldSeen = 0;
            for (int i = 0; i < n && positions.Count < maxCount; i++)
            {
                bool isHot = _stretchWork[i] >= hotThreshold;
                if (isHot ? hotSeen++ % hotStride != 0 : coldSeen++ % coldStride != 0) continue;
                positions.Add(new Vector3(p[i].X, p[i].Y, p[i].Z));
                stretches.Add(_stretchWork[i]);
            }
        }

        /// <summary>
        /// 見せる形の割合（0 で編集前の形、1 で編集中の形）。シーンに描く形だけが変わり、編集そのものには影響しない。
        /// </summary>
        public float PreviewBlend
        {
            get => _previewBlend;
            set
            {
                value = Mathf.Clamp01(value);
                if (value == _previewBlend) return;
                _previewBlend = value;
                _previewDirty = true;
            }
        }

        /// <summary>
        /// シーンに描く頂点（ワールド）。<see cref="PreviewBlend"/> が 1 なら <see cref="CurrentWorldVertices"/> そのもの。
        /// それ以外は編集前の形との間を補った配列（呼ぶたびに同じ配列を使い回す）。
        /// </summary>
        public Vector3[] GetPreviewVertices()
        {
            if (_previewBlend >= 1f) return CurrentWorldVertices;
            if (_blendWork == null) _blendWork = new Vector3[CurrentWorldVertices.Length];
            for (int v = 0; v < _blendWork.Length; v++) _blendWork[v] = Vector3.Lerp(_baseWorld[v], CurrentWorldVertices[v], _previewBlend);
            return _blendWork;
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
        /// 現在の形を、元メッシュの頂点ごとのシェイプキーの差分（メッシュ座標）にする（複数のレンダラーのときは、頂点を順に並べたもの）。
        /// 線形ブレンドスキニングでは、メッシュ座標の差分 d を足すとワールドで M(3x3) * d だけ動くので、
        /// 位置は M(3x3) の逆行列を掛けると見た目どおりに戻る。法線は相似変換とみなした近似。
        /// </summary>
        public void BuildMeshSpaceDeltas(Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            int count = VertexCount;
            if (deltaVertices == null || deltaVertices.Length != count) throw new ArgumentException("頂点数が一致しません。", nameof(deltaVertices));
            if (deltaNormals == null || deltaNormals.Length != count) throw new ArgumentException("頂点数が一致しません。", nameof(deltaNormals));
            ComputeWorldDeltas();
            ToMeshSpace(0, count, deltaVertices, deltaNormals);
        }

        /// <summary>
        /// <see cref="BuildMeshSpaceDeltas(Vector3[], Vector3[])"/> の、レンダラー1つ分。配列はそのメッシュの頂点数。
        /// </summary>
        public void BuildMeshSpaceDeltas(int rendererIndex, Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            Part part = _parts[rendererIndex];
            if (deltaVertices == null || deltaVertices.Length != part.VertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaVertices));
            if (deltaNormals == null || deltaNormals.Length != part.VertexCount) throw new ArgumentException("頂点数が一致しません。", nameof(deltaNormals));
            ComputeWorldDeltas();
            ToMeshSpace(part.VertexStart, part.VertexCount, deltaVertices, deltaNormals);
        }

        // 頂点ごとのワールドの差分（位置と法線）を、作業用の配列に入れる
        void ComputeWorldDeltas()
        {
            if (_deltaVerticesWork == null)
            {
                _deltaVerticesWork = new NVector3[VertexCount];
                _deltaNormalsWork = new NVector3[VertexCount];
            }
            BlendShapeBuilder.Build(_cloth, _solver.Positions, _deltaVerticesWork, _deltaNormalsWork);
        }

        // 作業用の配列の、まとめた頂点 start から count 個を、メッシュ座標に直して先頭から入れる
        void ToMeshSpace(int start, int count, Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            for (int k = 0; k < count; k++)
            {
                int v = start + k;
                Matrix4x4 inv = _inverseMatrices[v];
                NVector3 dw = _deltaVerticesWork[v];
                deltaVertices[k] = new Vector3(
                    inv.m00 * dw.X + inv.m01 * dw.Y + inv.m02 * dw.Z,
                    inv.m10 * dw.X + inv.m11 * dw.Y + inv.m12 * dw.Z,
                    inv.m20 * dw.X + inv.m21 * dw.Y + inv.m22 * dw.Z);

                // 転置を掛ける（相似変換 s*R なら、転置 / s が回転の逆になる）
                Matrix4x4 m = _vertexMatrices[v];
                NVector3 nw = _deltaNormalsWork[v];
                float s = _inverseScale[v];
                deltaNormals[k] = new Vector3(
                    (m.m00 * nw.X + m.m10 * nw.Y + m.m20 * nw.Z) * s,
                    (m.m01 * nw.X + m.m11 * nw.Y + m.m21 * nw.Z) * s,
                    (m.m02 * nw.X + m.m12 * nw.Y + m.m22 * nw.Z) * s);
            }
        }

        /// <summary>
        /// 今の編集を、元メッシュの頂点ごとのメッシュ空間の差分で返す（シェイプキーのデータ用。複数のレンダラーのときは頂点を順に並べたもの）。
        /// <see cref="BuildMeshSpaceDeltas"/> の位置の差分だけを新しい配列に入れて返す。
        /// </summary>
        public Vector3[] GetMeshSpaceDeltas()
        {
            var deltaVertices = new Vector3[VertexCount];
            var deltaNormals = new Vector3[VertexCount];
            BuildMeshSpaceDeltas(deltaVertices, deltaNormals);
            return deltaVertices;
        }

        /// <summary><see cref="GetMeshSpaceDeltas()"/> の、レンダラー1つ分（そのメッシュの頂点数）。</summary>
        public Vector3[] GetMeshSpaceDeltas(int rendererIndex)
        {
            int count = _parts[rendererIndex].VertexCount;
            var deltaVertices = new Vector3[count];
            var deltaNormals = new Vector3[count];
            BuildMeshSpaceDeltas(rendererIndex, deltaVertices, deltaNormals);
            return deltaVertices;
        }

        /// <summary>
        /// メッシュ空間の差分（<see cref="GetMeshSpaceDeltas"/> の逆）を、編集中の形として読み込む。
        /// 差分を頂点ごとの行列でワールドへ直し、まとめた後の頂点の位置にして、粒子の位置は三角形の重心座標で補間して決める。
        /// 既存のシェイプキーを取り込んで、布として続きを編集するのに使う。ピン留めは変えず、つまみは外れる。
        /// 粒子の Undo の記録（<see cref="WriteState"/>）は、呼び出し側（コントローラー）が操作の前後で取る。
        /// 頂点数が合わなければ何もせず false。
        /// </summary>
        public bool LoadMeshSpaceDeltas(Vector3[] deltas)
        {
            if (deltas == null || deltas.Length != VertexCount) return false;
            return LoadDeltas(deltas);
        }

        /// <summary>
        /// <see cref="LoadMeshSpaceDeltas(Vector3[])"/> の、レンダラー1つ分（そのメッシュの頂点数の差分）。ほかのレンダラーは編集前の形にする。
        /// </summary>
        public bool LoadMeshSpaceDeltas(int rendererIndex, Vector3[] deltas)
        {
            if ((uint)rendererIndex >= (uint)_parts.Length) return false;
            Part part = _parts[rendererIndex];
            if (deltas == null || deltas.Length != part.VertexCount) return false;
            var all = new Vector3[VertexCount];
            Array.Copy(deltas, 0, all, part.VertexStart, part.VertexCount);
            return LoadDeltas(all);
        }

        bool LoadDeltas(Vector3[] deltas)
        {
            // まとめる前の頂点が同じ位置に複数あるとき（継ぎ目）は、ワールドの差分を平均する
            int weldedCount = _toposRest.Length;
            var sum = new NVector3[weldedCount];
            var count = new int[weldedCount];
            for (int v = 0; v < deltas.Length; v++)
            {
                Matrix4x4 m = _vertexMatrices[v];
                Vector3 d = deltas[v];
                int w = _vertexToWelded[v];
                sum[w] += new NVector3(
                    m.m00 * d.x + m.m01 * d.y + m.m02 * d.z,
                    m.m10 * d.x + m.m11 * d.y + m.m12 * d.z,
                    m.m20 * d.x + m.m21 * d.y + m.m22 * d.z);
                count[w]++;
            }
            var target = new NVector3[weldedCount];
            for (int w = 0; w < weldedCount; w++) target[w] = count[w] > 0 ? _toposRest[w] + sum[w] / count[w] : _toposRest[w];

            var particles = new NVector3[ParticleCount];
            _cloth.ParticlePositionsFromWelded(target, particles);

            _grabParticle = -1;
            _solver.SetPositions(particles);
            UpdateCurrentShape();
            return true;
        }

        /// <summary>元メッシュの複製に、現在の形をシェイプキーとして加えたメッシュを作る（アセットには保存しない）。複数のレンダラーのときは最初のもの。</summary>
        public Mesh CreateMeshWithBlendShape(string shapeName)
        {
            return CreateMeshWithBlendShape(0, shapeName);
        }

        /// <summary>index 番目のレンダラーの元メッシュの複製に、現在の形をシェイプキーとして加えたメッシュを作る（アセットには保存しない）。</summary>
        public Mesh CreateMeshWithBlendShape(int rendererIndex, string shapeName)
        {
            Part part = _parts[rendererIndex];
            var deltaVertices = new Vector3[part.VertexCount];
            var deltaNormals = new Vector3[part.VertexCount];
            BuildMeshSpaceDeltas(rendererIndex, deltaVertices, deltaNormals);
            return CreateMeshWithBlendShape(part, shapeName, deltaVertices, deltaNormals);
        }

        static Mesh CreateMeshWithBlendShape(Part part, string shapeName, Vector3[] deltaVertices, Vector3[] deltaNormals)
        {
            Mesh mesh = Object.Instantiate(part.SourceMesh);
            string sourceName = part.SourceMesh.name;
            mesh.name = sourceName.EndsWith(MeshSuffix) ? sourceName : sourceName + MeshSuffix;
            MeshBridge.AddBlendShape(mesh, shapeName, deltaVertices, deltaNormals);
            return mesh;
        }

        /// <summary>
        /// 現在の形をシェイプキーとして保存し、シーンのレンダラーに反映する。保存したメッシュ（複数のレンダラーのときは最初のもの）を返す。
        /// 以降のセッションは、返したメッシュを元として作り直すこと（シェイプキー 100 込みのワールド形状が新しい基準になる）。
        /// </summary>
        public Mesh ApplyToScene(string shapeName, string folder = "Assets/GP02/Meshes")
        {
            return ApplyToSceneAll(shapeName, folder)[0];
        }

        /// <summary>
        /// 現在の形を、まとめて編集しているすべてのレンダラーに、同じ名前のシェイプキーとして保存する（動いていないレンダラーにも、差分 0 で入れる）。
        /// レンダラーの順に、保存したメッシュを返す。Unity の Undo の記録は、呼び出し側の今のグループに積む（まとめて1段で戻る）。
        /// </summary>
        public Mesh[] ApplyToSceneAll(string shapeName, string folder = "Assets/GP02/Meshes")
        {
            if (!IsTargetAlive) throw new InvalidOperationException(L10n.Tr("対象のレンダラーがありません。", "The target renderer is missing."));
            folder = folder.TrimEnd('/');
            ComputeWorldDeltas();

            var results = new Mesh[_parts.Length];
            for (int r = 0; r < _parts.Length; r++)
            {
                Part part = _parts[r];
                var deltaVertices = new Vector3[part.VertexCount];
                var deltaNormals = new Vector3[part.VertexCount];
                ToMeshSpace(part.VertexStart, part.VertexCount, deltaVertices, deltaNormals);

                Mesh result;
                string sourcePath = AssetDatabase.GetAssetPath(part.SourceMesh);
                bool isOwnAsset = !string.IsNullOrEmpty(sourcePath) && sourcePath.EndsWith(".asset")
                    && sourcePath.StartsWith(folder + "/") && AssetDatabase.IsMainAsset(part.SourceMesh);

                if (isOwnAsset)
                {
                    // GP02 が作ったメッシュには、そのままシェイプキーを足す（複製が増え続けないように）
                    UnityEditor.Undo.RegisterCompleteObjectUndo(part.SourceMesh, "GP02 シェイプキーを追加");
                    MeshBridge.AddBlendShape(part.SourceMesh, shapeName, deltaVertices, deltaNormals);
                    EditorUtility.SetDirty(part.SourceMesh);
                    result = part.SourceMesh;
                }
                else
                {
                    Mesh created = CreateMeshWithBlendShape(part, shapeName, deltaVertices, deltaNormals);
                    EnsureFolder(folder);
                    string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(created.name)}.asset");
                    AssetDatabase.CreateAsset(created, path);

                    UnityEditor.Undo.RecordObject(GetMeshOwner(part.Renderer), "GP02 メッシュを差し替え");
                    SetMesh(part.Renderer, created);
                    EditorUtility.SetDirty(GetMeshOwner(part.Renderer));
                    result = created;
                }

                if (part.Renderer is SkinnedMeshRenderer skinned)
                {
                    int index = result.GetBlendShapeIndex(shapeName);
                    UnityEditor.Undo.RecordObject(skinned, "GP02 シェイプキーの重み");
                    skinned.SetBlendShapeWeight(index, 100f);
                    EditorUtility.SetDirty(skinned);
                }
                results[r] = result;
            }
            AssetDatabase.SaveAssets();
            return results;
        }

        static Object GetMeshOwner(Renderer renderer) => renderer is SkinnedMeshRenderer ? (Object)renderer : renderer.GetComponent<MeshFilter>();

        static void SetMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
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

        /// <summary>"GP02_1", "GP02_2" … のうち、元メッシュ（複数のときはどれか）のシェイプキーと重ならない名前。</summary>
        public string SuggestShapeName()
        {
            for (int n = 1; ; n++)
            {
                string name = ShapeNamePrefix + n;
                if (!HasBlendShape(name)) return name;
            }
        }

        /// <summary>まとめて編集しているメッシュのどれかに、その名前のシェイプキーがあるか。</summary>
        public bool HasBlendShape(string name)
        {
            foreach (Part part in _parts)
            {
                if (part.SourceMesh != null && part.SourceMesh.GetBlendShapeIndex(name) >= 0) return true;
            }
            return false;
        }

        /// <summary>編集中の形をシーンに描く（元のレンダラーは隠してある）。サブメッシュごとにマテリアルを割り当てる。</summary>
        public void DrawPreview(Camera camera)
        {
            if (_disposed || !IsTargetAlive) return;
            if (_previewDirty)
            {
                Vector3[] vertices = GetPreviewVertices();
                foreach (Part part in _parts)
                {
                    part.PreviewMesh.SetVertices(vertices, part.VertexStart, part.VertexCount);
                    part.PreviewMesh.RecalculateNormals();
                    part.PreviewMesh.RecalculateBounds();
                }
                _previewDirty = false;
            }

            foreach (Part part in _parts)
            {
                int layer = part.Renderer.gameObject.layer;
                Material[] materials = part.Materials;
                for (int sub = 0; sub < part.PreviewMesh.subMeshCount; sub++)
                {
                    if (materials.Length == 0) break;
                    Material material = materials[Mathf.Min(sub, materials.Length - 1)];
                    if (material == null) continue;
                    Graphics.DrawMesh(part.PreviewMesh, Matrix4x4.identity, material, layer, camera, sub);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Part part in _parts)
            {
                if (part.Renderer != null) part.Renderer.forceRenderingOff = false;
                if (part.PreviewMesh != null) Object.DestroyImmediate(part.PreviewMesh);
            }
        }
    }
}
