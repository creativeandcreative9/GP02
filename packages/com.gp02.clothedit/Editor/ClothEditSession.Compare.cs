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
