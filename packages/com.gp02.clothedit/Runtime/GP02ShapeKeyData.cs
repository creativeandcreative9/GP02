using System;
using System.Collections.Generic;
using UnityEngine;

namespace GP02
{
    /// <summary>
    /// in-between のフレームの扱い。計算の中核の <c>GP02.Core.InBetweenMode</c> と同じ意味
    /// （Runtime は計算の中核を参照しないので、別に定義している）。
    /// </summary>
    public enum ShapeKeyInBetweenMode
    {
        /// <summary>途中のフレームは、最終形の比例分に、そのフレーム固有の差分を足したもの。</summary>
        Progressive = 0,

        /// <summary>途中のフレームは、その重みでの形そのもの。隣り合うフレームの間は線形に混ざる。</summary>
        Crossfade = 1,
    }

    /// <summary>
    /// シェイプキーの1フレーム。Unity の BlendShape のフレームと同じ形（重みと、頂点ごとの差分）。
    /// 差分は元メッシュのメッシュ空間で、頂点の並びは元メッシュと同じ。
    /// </summary>
    [Serializable]
    public sealed class ShapeKeyFrame
    {
        /// <summary>重み（0〜100）。</summary>
        public float Weight = 100f;

        /// <summary>頂点ごとの位置の差分。</summary>
        public Vector3[] DeltaVertices = new Vector3[0];

        /// <summary>頂点ごとの法線の差分。法線を扱わないときは空（長さ 0）。</summary>
        public Vector3[] DeltaNormals = new Vector3[0];

        /// <summary>頂点ごとの接線の差分（xyz のみ）。接線を扱わないときは空（長さ 0）。</summary>
        public Vector3[] DeltaTangents = new Vector3[0];

        public bool HasNormals => DeltaNormals != null && DeltaNormals.Length > 0;
        public bool HasTangents => DeltaTangents != null && DeltaTangents.Length > 0;
    }

    /// <summary>
    /// シェイプキーの前の版（上書き保存する前の形）。最大 <see cref="ShapeKeyEntry.MaxHistory"/> 個まで、エントリの中に残す。
    /// </summary>
    [Serializable]
    public sealed class ShapeKeyVersion
    {
        /// <summary>この版を残した日時（"yyyy-MM-dd HH:mm:ss"。端末の時刻）。</summary>
        public string SavedAt = "";

        /// <summary>何をして残したか（"布で編集して上書き"、"取り込みで上書き" など）。</summary>
        public string Reason = "";

        /// <summary>この版のフレーム（深いコピー）。</summary>
        public List<ShapeKeyFrame> Frames = new List<ShapeKeyFrame>();

        /// <summary>この版のピン留めの頂点番号。</summary>
        public int[] PinnedVertices = new int[0];
    }

    /// <summary>
    /// シェイプキー1つ分の編集データ。Unity のメッシュに書き出すと、同じ名前の BlendShape になる。
    /// </summary>
    [Serializable]
    public sealed class ShapeKeyEntry
    {
        /// <summary>前の版として残す数の上限。</summary>
        public const int MaxHistory = 10;

        /// <summary>BlendShape の名前。</summary>
        public string Name = "";

        /// <summary>false なら、メッシュへの書き出しに含めない。</summary>
        public bool Enabled = true;

        /// <summary>整理用のグループ名（<see cref="GP02ShapeKeyData.Groups"/> のどれか）。空ならグループなし。</summary>
        public string Group = "";

        /// <summary>プレビューで使う重み（0〜100）。</summary>
        [Range(0f, 100f)]
        public float PreviewWeight;

        /// <summary>
        /// フレームの一覧（重みの小さい順）。Unity の BlendShape にそのまま入れる絶対の差分で、
        /// 1つならふつうのシェイプキー、複数なら in-between になる。
        /// </summary>
        public List<ShapeKeyFrame> Frames = new List<ShapeKeyFrame>();

        /// <summary>
        /// in-between のフレームを編集するときの扱い。<see cref="Frames"/> の中身は、どちらでも書き出す差分そのもの。
        /// </summary>
        public ShapeKeyInBetweenMode InBetweenMode = ShapeKeyInBetweenMode.Crossfade;

        /// <summary>再編集するときに、ピン留めする頂点の番号（元メッシュの頂点番号）。</summary>
        public int[] PinnedVertices = new int[0];

        /// <summary>
        /// true なら、BlendShape にせず、書き出し（とビルド時の適用）で先に <see cref="PrebakeWeight"/> の重みで素体に焼き込む。
        /// 印はデータに残るので、外して書き出し直せば素体は元に戻る（書き出すメッシュは元のメッシュから毎回作り直す）。
        /// </summary>
        public bool Prebake;

        /// <summary>素体に焼き込むときの重み（0〜100）。</summary>
        [Range(0f, 100f)]
        public float PrebakeWeight = 100f;

        /// <summary>作った日時（DateTime.UtcNow.Ticks）。0 は不明（古いデータ）。並べ替えの「作った順」に使う。</summary>
        public long CreatedTicks;

        /// <summary>上書き保存する前の版（古い順）。最大 <see cref="MaxHistory"/> 個。</summary>
        public List<ShapeKeyVersion> History = new List<ShapeKeyVersion>();
    }

    /// <summary>
    /// 1つのメッシュに対する、シェイプキーの編集データ。アセット（Assets/GP02/Data/）として保存する。
    /// 大きな配列をシーンに埋め込まないよう、<see cref="GP02ShapeKeys"/> からはこのアセットを参照する。
    /// </summary>
    [CreateAssetMenu(fileName = "GP02ShapeKeyData", menuName = "GP02/Shape Key Data")]
    public sealed class GP02ShapeKeyData : ScriptableObject
    {
        /// <summary>差分の元になったメッシュ。</summary>
        public Mesh SourceMesh;

        /// <summary>元のメッシュの頂点数（差分の配列の長さ）。メッシュが差し替わったことを見つけるのに使う。</summary>
        public int VertexCount;

        public List<ShapeKeyEntry> Entries = new List<ShapeKeyEntry>();

        /// <summary>グループ名の一覧（表示の順）。</summary>
        public List<string> Groups = new List<string>();

        /// <summary>名前でエントリを探す。無ければ null。</summary>
        public ShapeKeyEntry FindEntry(string entryName)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i] != null && Entries[i].Name == entryName) return Entries[i];
            }
            return null;
        }

        /// <summary>このメッシュの頂点数が、データの頂点数と一致するか。</summary>
        public bool IsCompatibleWith(Mesh mesh) => mesh != null && mesh.vertexCount == VertexCount;
    }
}
