using UnityEngine;

namespace GP02
{
    /// <summary>シェイプキーをアバター（レンダラー）に反映するしかた。</summary>
    public enum ShapeKeyApplyMode
    {
        /// <summary>「メッシュに書き出す」で作ったメッシュを、シーンのレンダラーに割り当てる（既定。従来の動作）。</summary>
        ExportedMesh = 0,

        /// <summary>シーンのレンダラーは元のメッシュのまま。ビルド時（NDMF があるとき）だけ、BlendShape を足したメッシュに差し替える。</summary>
        NonDestructive = 1,
    }

    /// <summary>ビルド時（非破壊）に入れる BlendShape の、最初の重み。</summary>
    public enum ShapeKeyInitialWeight
    {
        /// <summary>すべて 0 にする。</summary>
        Zero = 0,

        /// <summary>エントリごとのプレビューの重み（<see cref="ShapeKeyEntry.PreviewWeight"/>）を使う。</summary>
        PreviewWeight = 1,
    }

    /// <summary>
    /// SkinnedMeshRenderer か MeshFilter のあるオブジェクトに付け、シェイプキーの編集データと元のメッシュを参照する。
    /// 中身は設定とデータの参照だけ。<see cref="ShapeKeyApplyMode.NonDestructive"/> のときにビルド時へ適用する処理は、
    /// NDMF 用の別の asmdef（GP02.Editor.NDMF）が受け持つ。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("GP02/Shape Keys")]
    public sealed class GP02ShapeKeys : MonoBehaviour
    {
        /// <summary>編集データ（アセット）。</summary>
        public GP02ShapeKeyData Data;

        /// <summary>適用する元のメッシュ。ビルド時に、これの複製へシェイプキーを加える。</summary>
        public Mesh SourceMesh;

        /// <summary>
        /// 最後に「メッシュに書き出す」で作ったメッシュ（アセット）。書き出し直すときは、これを上書きして、アセットが増え続けないようにする。
        /// データ（アセット）を消しても、このメッシュはアセットとして残る。
        /// </summary>
        public Mesh ExportedMesh;

        /// <summary>シェイプキーの適用のしかた。既定は従来どおり、書き出したメッシュを割り当てる方式。</summary>
        public ShapeKeyApplyMode ApplyMode = ShapeKeyApplyMode.ExportedMesh;

        /// <summary><see cref="ShapeKeyApplyMode.NonDestructive"/> のとき、ビルドで入れる BlendShape の最初の重み。</summary>
        public ShapeKeyInitialWeight InitialWeight = ShapeKeyInitialWeight.Zero;
    }
}
