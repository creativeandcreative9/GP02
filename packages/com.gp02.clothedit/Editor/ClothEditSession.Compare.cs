using System.Collections.Generic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    // 編集前後の比較: 元の形を半透明で重ねる表示と、動いた量のヒートマップ。
    public sealed partial class ClothEditSession
    {
        static Material _ghostMaterial;
        Mesh[] _ghostMeshes;
        float[] _moveWork;
        int _moveVersion = -1;

        /// <summary>元の形の半透明の重ね描きの色。</summary>
        public static readonly Color GhostColor = new Color(0.3f, 0.7f, 1f, 0.35f);

        /// <summary>
        /// 元の形（編集前）を半透明で重ねて描くか。<see cref="PreviewBlend"/>（元の形との間の見せ方）とは別で、編集中の形と同時に見える。
        /// </summary>
        public bool ShowGhost { get; set; }

        /// <summary>
        /// 動いた量のヒートマップ用に、粒子の位置と、元の形からの移動距離（メートル）を最大 maxCount 個入れる。
        /// 動いた粒子（hotThreshold 以上）を優先し、残りは間引く。形が変わったときだけ測り直す。
        /// </summary>
        public void GetMoveDots(List<Vector3> positions, List<float> distances, int maxCount, float hotThreshold = 0.001f)
        {
            int n = ParticleCount;
            if (_moveWork == null) _moveWork = new float[n];
            if (_moveVersion != _shapeVersion)
            {
                System.ReadOnlySpan<NVector3> p = _solver.Positions;
                NVector3[] rest = _cloth.Model.RestPositions;
                for (int i = 0; i < n; i++)
                {
                    float dx = p[i].X - rest[i].X, dy = p[i].Y - rest[i].Y, dz = p[i].Z - rest[i].Z;
                    _moveWork[i] = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                _moveVersion = _shapeVersion;
            }
            CollectDots(_moveWork, hotThreshold, positions, distances, maxCount);
        }

        /// <summary>粒子の、元の形からの移動距離（メートル）。</summary>
        public float GetMoveDistance(int particle)
        {
            NVector3 d = _solver.Positions[particle] - _cloth.Model.RestPositions[particle];
            return d.Length();
        }

        // 元の形を、奥行きを書かず・比べず、半透明の単色で描く（編集中の形の手前でも奥でも見える）
        void DrawGhost(Camera camera)
        {
            Material material = GetGhostMaterial();
            if (material == null) return;
            if (_ghostMeshes == null)
            {
                _ghostMeshes = new Mesh[_parts.Length];
                for (int r = 0; r < _parts.Length; r++)
                {
                    Part part = _parts[r];
                    Mesh mesh = Object.Instantiate(part.PreviewMesh);
                    mesh.name = part.SourceMesh.name + "_Original";
                    mesh.hideFlags = HideFlags.HideAndDontSave;
                    mesh.SetVertices(_baseWorld, part.VertexStart, part.VertexCount);
                    var colors = new Color32[part.VertexCount];
                    for (int k = 0; k < colors.Length; k++) colors[k] = new Color32(255, 255, 255, 255);
                    mesh.colors32 = colors;
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    _ghostMeshes[r] = mesh;
                }
            }
            for (int r = 0; r < _parts.Length; r++)
            {
                Mesh mesh = _ghostMeshes[r];
                int layer = _parts[r].Renderer.gameObject.layer;
                for (int sub = 0; sub < mesh.subMeshCount; sub++) Graphics.DrawMesh(mesh, Matrix4x4.identity, material, layer, camera, sub);
            }
        }

        // ---- 保存したシェイプキーを、重みで確かめる半透明の重ね描き ----

        static Material _keyGhostMaterial;
        Mesh _keyGhostMesh;
        Vector3[] _keyGhostWork;

        /// <summary>保存したシェイプキーの重ね描きの色（元の形の水色とは違う橙）。</summary>
        public static readonly Color KeyGhostColor = new Color(1f, 0.6f, 0.2f, 0.4f);

        /// <summary>保存したシェイプキーを重ね描きしているか。</summary>
        public bool HasKeyGhost => _keyGhostMesh != null;

        /// <summary>
        /// 保存したシェイプキー（最初のレンダラーのデータ）を、重みをかけた形で半透明に重ねて描く。編集中の形は変えない。
        /// source は元のメッシュの頂点（メッシュ座標）、delta はその重みでの差分（メッシュ座標）。頂点ごとの行列（現在のポーズ）でワールドに直す。
        /// null、または長さが最初のレンダラーの頂点数と違えば、消す。
        /// </summary>
        public void SetKeyGhost(Vector3[] source, Vector3[] delta)
        {
            Part part = _parts[0];
            if (source == null || delta == null || source.Length != part.VertexCount || delta.Length != part.VertexCount)
            {
                DisposeKeyGhost();
                return;
            }
            if (_keyGhostMesh == null)
            {
                _keyGhostMesh = Object.Instantiate(part.PreviewMesh);
                _keyGhostMesh.name = part.SourceMesh.name + "_KeyPreview";
                _keyGhostMesh.hideFlags = HideFlags.HideAndDontSave;
                var colors = new Color32[part.VertexCount];
                for (int k = 0; k < colors.Length; k++) colors[k] = new Color32(255, 255, 255, 255);
                _keyGhostMesh.colors32 = colors;
            }
            if (_keyGhostWork == null || _keyGhostWork.Length != part.VertexCount) _keyGhostWork = new Vector3[part.VertexCount];
            for (int k = 0; k < part.VertexCount; k++)
            {
                _keyGhostWork[k] = _vertexMatrices[part.VertexStart + k].MultiplyPoint3x4(source[k] + delta[k]);
            }
            _keyGhostMesh.SetVertices(_keyGhostWork);
            _keyGhostMesh.RecalculateNormals();
            _keyGhostMesh.RecalculateBounds();
        }

        /// <summary>保存したシェイプキーの重ね描きを消す。</summary>
        public void ClearKeyGhost() => DisposeKeyGhost();

        /// <summary>重ね描きの、ワールドの頂点（テスト用。重ね描きが無ければ null）。</summary>
        public Vector3[] KeyGhostVertices => _keyGhostMesh != null ? _keyGhostWork : null;

        void DrawKeyGhost(Camera camera)
        {
            if (_keyGhostMesh == null) return;
            if (_keyGhostMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return;
                _keyGhostMaterial = new Material(shader) { name = "GP02_KeyGhost", hideFlags = HideFlags.HideAndDontSave };
                _keyGhostMaterial.SetColor("_Color", KeyGhostColor);
                _keyGhostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _keyGhostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _keyGhostMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _keyGhostMaterial.SetInt("_ZWrite", 0);
                _keyGhostMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _keyGhostMaterial.renderQueue = 3101;
            }
            int layer = _parts[0].Renderer.gameObject.layer;
            for (int sub = 0; sub < _keyGhostMesh.subMeshCount; sub++) Graphics.DrawMesh(_keyGhostMesh, Matrix4x4.identity, _keyGhostMaterial, layer, camera, sub);
        }

        void DisposeKeyGhost()
        {
            if (_keyGhostMesh == null) return;
            Object.DestroyImmediate(_keyGhostMesh);
            _keyGhostMesh = null;
        }

        static Material GetGhostMaterial()
        {
            if (_ghostMaterial != null) return _ghostMaterial;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            _ghostMaterial = new Material(shader) { name = "GP02_OriginalGhost", hideFlags = HideFlags.HideAndDontSave };
            _ghostMaterial.SetColor("_Color", GhostColor);
            _ghostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _ghostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _ghostMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _ghostMaterial.SetInt("_ZWrite", 0);
            // 編集中の形の奥に入った所も見えるよう、手前のものに隠れないで描く
            _ghostMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _ghostMaterial.renderQueue = 3100;
            return _ghostMaterial;
        }

        void DisposeGhost()
        {
            if (_ghostMeshes == null) return;
            foreach (Mesh mesh in _ghostMeshes)
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
            _ghostMeshes = null;
        }
    }
}
