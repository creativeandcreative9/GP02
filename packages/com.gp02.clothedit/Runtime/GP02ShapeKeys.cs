using UnityEngine;

namespace GP02
{
    /// <summary>
    /// SkinnedMeshRenderer か MeshFilter のあるオブジェクトに付け、シェイプキーの編集データと元のメッシュを参照する。
    /// 中身はデータの参照だけ。ビルド時にメッシュへ適用する処理（非破壊）は、別の asmdef で後から作る。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("GP02/Shape Keys")]
    public sealed class GP02ShapeKeys : MonoBehaviour
    {
        /// <summary>編集データ（アセット）。</summary>
        public GP02ShapeKeyData Data;

        /// <summary>適用する元のメッシュ。ビルド時に、これの複製へシェイプキーを加える。</summary>
        public Mesh SourceMesh;
    }
}
