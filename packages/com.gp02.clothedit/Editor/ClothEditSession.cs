using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        const int MaxUndoDepth = 20;
        const string ShapeNamePrefix = "GP02_";
        const string MeshSuffix = "_GP02";

        readonly Renderer _renderer;
        readonly Mesh _sourceMesh;
        readonly int[] _triangles;
        readonly Vector3[] _baseWorld;            // 編集前のワールド頂点
        readonly Matrix4x4[] _vertexMatrices;     // 頂点ごとの、メッシュ座標 → ワールド座標
        readonly Matrix4x4[] _inverseMatrices;    // 上の 3x3 部分の逆行列（特異なら 0）
        readonly float[] _inverseScale;           // 1 / |det|^(1/3)（特異なら 0）
        readonly ParticleCloth _cloth;
        readonly ClothSolver _solver;
        readonly NVector3[] _welded;
        readonly NVector3[] _toposRest;
        readonly int[] _vertexToWelded;
        readonly List<NVector3[]> _undoStack = new List<NVector3[]>();
        readonly Mesh _previewMesh;
        readonly Material[] _materials;
        NVector3[] _deltaVerticesWork;
        NVector3[] _deltaNormalsWork;
        bool _previewDirty = true;
        bool _disposed;

        // つまんでいる間の状態
        int _grabParticle = -1;
        float _grabRadius;
        NVector3 _grabPointStart;
        NVector3 _grabParticleStart;
        bool _grabPushedUndo;

        int _pinnedCount;

        public Renderer Renderer => _renderer;
        public Mesh SourceMesh => _sourceMesh;
        public ParticleCloth Cloth => _cloth;
        public ClothSolver Solver => _solver;

        /// <summary>準備（粒子化）にかかった時間（ミリ秒）。</summary>
        public double PrepareMilliseconds { get; }
        public int ParticleCount => _cloth.Model.ParticleCount;
        public int VertexCount => _sourceMesh.vertexCount;

        /// <summary>現在の形のワールド頂点（元メッシュの頂点数）。UpdateCurrentShape で更新される。</summary>
        public Vector3[] CurrentWorldVertices { get; }

        public int PinnedCount => _pinnedCount;
        public bool IsGrabbing => _grabParticle >= 0;
        public int UndoCount => _undoStack.Count;

        /// <summary>対象のレンダラーが消されていないか。</summary>
        public bool IsTargetAlive => _renderer != null;

        public ClothEditSession(Renderer renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            _renderer = renderer;
            _sourceMesh = GetMesh(renderer);
            if (_sourceMesh == null) throw new ArgumentException("メッシュがありません。MeshFilter か SkinnedMeshRenderer のメッシュを確認してください。", nameof(renderer));

            var timer = Stopwatch.StartNew();
            Vector3[] baseVertices = _sourceMesh.vertices;
            if (renderer is SkinnedMeshRenderer skinned) ApplyBlendShapeWeights(skinned, _sourceMesh, baseVertices);
            _triangles = _sourceMesh.triangles;

            _vertexMatrices = ComputeVertexMatrices(renderer, _sourceMesh);
            _baseWorld = new Vector3[baseVertices.Length];
            _inverseMatrices = new Matrix4x4[baseVertices.Length];
            _inverseScale = new float[baseVertices.Length];
            for (int v = 0; v < baseVertices.Length; v++)
            {
                _baseWorld[v] = _vertexMatrices[v].MultiplyPoint3x4(baseVertices[v]);
                _inverseScale[v] = Invert3x3(_vertexMatrices[v], out _inverseMatrices[v]);
            }

            _cloth = ParticleCloth.Build(MeshBridge.ToNumerics(_baseWorld, Vector3.one), _triangles);
            _solver = _cloth.CreateSolver();
            _welded = new NVector3[_cloth.Topology.Positions.Length];
            _toposRest = _cloth.Topology.Positions;
            _vertexToWelded = _cloth.Topology.VertexToWelded;
            CurrentWorldVertices = (Vector3[])_baseWorld.Clone();
            timer.Stop();
            PrepareMilliseconds = timer.Elapsed.TotalMilliseconds;

            _previewMesh = Object.Instantiate(_sourceMesh);
            _previewMesh.name = _sourceMesh.name + "_Preview";
            _previewMesh.hideFlags = HideFlags.HideAndDontSave;
            // Graphics.DrawMesh は頂点をそのまま描くので、スキニングとシェイプキーは使わない
            _previewMesh.ClearBlendShapes();
            _previewMesh.boneWeights = new BoneWeight[0];
            _materials = renderer.sharedMaterials;

            renderer.forceRenderingOff = true;
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

        // 線形ブレンドスキニング：M_v = Σ w_i * (bone_i.localToWorld * bindpose_i)
        static Matrix4x4[] ComputeVertexMatrices(Renderer renderer, Mesh mesh)
        {
            int vertexCount = mesh.vertexCount;
            var result = new Matrix4x4[vertexCount];
            Matrix4x4 own = renderer.transform.localToWorldMatrix;

            var skinned = renderer as SkinnedMeshRenderer;
            Transform[] bones = skinned != null ? skinned.bones : null;
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
                int count = bonesPerVertex[v];
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
                offset += count;
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

        /// <summary>粒子の位置から CurrentWorldVertices を更新する。確保はしない。</summary>
        public void UpdateCurrentShape()
        {
            _cloth.DeformWelded(_solver.Positions, _welded);
            for (int v = 0; v < CurrentWorldVertices.Length; v++)
            {
                int w = _vertexToWelded[v];
                NVector3 delta = _welded[w] - _toposRest[w];
                Vector3 b = _baseWorld[v];
                CurrentWorldVertices[v] = new Vector3(b.x + delta.X, b.y + delta.Y, b.z + delta.Z);
            }
            _previewDirty = true;
        }

        /// <summary>現在の形に対するレイキャスト（Möller–Trumbore、両面）。一番近い交差を返す。</summary>
        public bool Raycast(Ray ray, out Vector3 hitPoint, out Vector3 hitNormal)
        {
            Vector3 origin = ray.origin, direction = ray.direction;
            Vector3[] vertices = CurrentWorldVertices;
            float bestT = float.MaxValue;
            int bestTriangle = -1;

            for (int t = 0; t < _triangles.Length; t += 3)
            {
                Vector3 p0 = vertices[_triangles[t]];
                Vector3 edge1 = vertices[_triangles[t + 1]] - p0;
                Vector3 edge2 = vertices[_triangles[t + 2]] - p0;
                Vector3 pvec = Vector3.Cross(direction, edge2);
                float det = Vector3.Dot(edge1, pvec);
                if (Mathf.Abs(det) < 1e-12f) continue;

                float invDet = 1f / det;
                Vector3 tvec = origin - p0;
                float u = Vector3.Dot(tvec, pvec) * invDet;
                if (u < 0f || u > 1f) continue;
                Vector3 qvec = Vector3.Cross(tvec, edge1);
                float v = Vector3.Dot(direction, qvec) * invDet;
                if (v < 0f || u + v > 1f) continue;
                float distance = Vector3.Dot(edge2, qvec) * invDet;
                if (distance > 0f && distance < bestT)
                {
                    bestT = distance;
                    bestTriangle = t;
                }
            }

            if (bestTriangle < 0)
            {
                hitPoint = Vector3.zero;
                hitNormal = Vector3.up;
                return false;
            }

            hitPoint = origin + direction * bestT;
            Vector3 a = vertices[_triangles[bestTriangle]];
            Vector3 normal = Vector3.Cross(vertices[_triangles[bestTriangle + 1]] - a, vertices[_triangles[bestTriangle + 2]] - a).normalized;
            hitNormal = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
            return true;
        }

        /// <summary>
        /// 指定点の近くの粒子をつまむ。つまむ前の粒子の位置を、元に戻す用に記録する。
        /// </summary>
        /// <param name="brushRadius">つまむ範囲（メッシュに沿った距離、メートル）</param>
        public bool BeginGrab(Vector3 worldPoint, float brushRadius)
        {
            if (_solver.GrabbedParticle >= 0) Release();

            var point = new NVector3(worldPoint.x, worldPoint.y, worldPoint.z);
            int particle = _solver.FindNearestParticle(point);
            if (particle < 0) return false;

            PushUndo();
            _grabPushedUndo = true;
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

        /// <summary>つまみを外す。動かさずに離しただけなら、元に戻す用の記録は取り消す。</summary>
        public void Release()
        {
            if (_grabParticle >= 0 && _grabPushedUndo && _undoStack.Count > 0 && PositionsEqual(_undoStack[_undoStack.Count - 1]))
            {
                _undoStack.RemoveAt(_undoStack.Count - 1);
            }
            _grabPushedUndo = false;
            _grabParticle = -1;
            _solver.Release();
        }

        bool PositionsEqual(NVector3[] snapshot)
        {
            ReadOnlySpan<NVector3> now = _solver.Positions;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] != now[i]) return false;
            }
            return true;
        }

        void PushUndo()
        {
            _undoStack.Add(_solver.Positions.ToArray());
            if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        }

        /// <summary>直前のストロークの前の粒子位置に戻す。戻すものが無ければ false。</summary>
        public bool Undo()
        {
            if (_undoStack.Count == 0) return false;
            NVector3[] snapshot = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _grabParticle = -1;
            _grabPushedUndo = false;
            _solver.SetPositions(snapshot);
            UpdateCurrentShape();
            return true;
        }

        /// <summary>元の形に戻す（ピンは保持）。直前の形は Undo で戻せる。</summary>
        public void ResetAll()
        {
            PushUndo();
            _grabParticle = -1;
            _grabPushedUndo = false;
            _solver.ResetToRest();
            UpdateCurrentShape();
        }

        /// <summary>現在位置がワールド座標で radius 以内の粒子を、ピン留め（または解除）する。変えた粒子の数を返す。</summary>
        public int PaintPins(Vector3 worldPoint, float radius, bool pinned)
        {
            var point = new NVector3(worldPoint.x, worldPoint.y, worldPoint.z);
            float radiusSq = radius * radius;
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int changed = 0;
            for (int i = 0; i < positions.Length; i++)
            {
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
            result.Clear();
            if (_pinnedCount == 0 || maxCount <= 0) return;
            int stride = Mathf.Max(1, (_pinnedCount + maxCount - 1) / maxCount);
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            int seen = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!_solver.IsPinned(i)) continue;
                if (seen++ % stride == 0) result.Add(new Vector3(positions[i].X, positions[i].Y, positions[i].Z));
            }
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
