using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>ブラシの範囲を決める距離の測り方。</summary>
    public enum BrushDistanceMode
    {
        /// <summary>メッシュに沿った距離。近くにあっても、つながっていない布（重ね着の別の層など）はつままない。</summary>
        Surface,

        /// <summary>つまんだ時点の位置での直線距離。つながっていない重ね着の層もまとめてつまめる。</summary>
        Linear,
    }

    /// <summary>対称（ミラー）編集の鏡の面（ワールド座標）。</summary>
    public readonly struct MirrorPlane
    {
        /// <summary>面上の1点。</summary>
        public readonly Vector3 Point;

        /// <summary>面の法線（長さ1）。</summary>
        public readonly Vector3 Normal;

        /// <param name="normal">面の法線。長さは 1 にそろえる。0 ベクトルは不可</param>
        public MirrorPlane(Vector3 point, Vector3 normal)
        {
            float length = normal.Length();
            if (!(length > 1e-12f) || float.IsInfinity(length)) throw new ArgumentException("法線が 0 ベクトルです。", nameof(normal));
            Point = point;
            Normal = normal / length;
        }

        /// <summary>法線が決まっているか（<c>default</c> では false）。</summary>
        public bool IsValid => Normal != Vector3.Zero;

        /// <summary>面からの符号付き距離（法線の向きが正）。</summary>
        public float SignedDistance(Vector3 p) => Vector3.Dot(p - Point, Normal);

        /// <summary>点を鏡に映す。</summary>
        public Vector3 Reflect(Vector3 p) => p - 2f * SignedDistance(p) * Normal;

        /// <summary>向き（ベクトル）を鏡に映す。</summary>
        public Vector3 ReflectDirection(Vector3 v) => v - 2f * Vector3.Dot(v, Normal) * Normal;
    }

    /// <summary>
    /// つまむ範囲の決め方（<see cref="ClothSolver.Grab(int, BrushMotion, BrushOptions)"/>）。
    /// ソルバーは呼ばれたときに値を写し取るので、同じものを使い回して書き換えてよい。
    /// </summary>
    public sealed class BrushOptions
    {
        /// <summary><see cref="MirrorTolerance"/> の既定値（メートル）。粒子の間隔（1cm）より少し広くする。</summary>
        public const float DefaultMirrorTolerance = 0.02f;

        /// <summary>つまむ範囲の半径（メートル）。0 なら中心の粒子だけ。</summary>
        public float Radius { get; set; }

        /// <summary>範囲の中の重み。null なら <see cref="FalloffCurve.Constant"/>（範囲全体が同じだけ動く）。</summary>
        public FalloffCurve Falloff { get; set; } = FalloffCurve.Constant;

        /// <summary>範囲を決める距離の測り方。</summary>
        public BrushDistanceMode DistanceMode { get; set; } = BrushDistanceMode.Surface;

        /// <summary>
        /// 対称編集の鏡の面。null なら対称にしない。
        /// 中心の粒子の鏡像（<see cref="ClothSolver.FindMirrorParticle"/>）も範囲の中心にして、鏡に映した動きで動かす。
        /// </summary>
        public MirrorPlane? Mirror { get; set; }

        /// <summary>鏡像の粒子を探す許容距離（メートル）。</summary>
        public float MirrorTolerance { get; set; } = DefaultMirrorTolerance;

        /// <summary>
        /// つながりの許容距離 ε（メートル）。<see cref="BrushDistanceMode.Surface"/> で範囲を測るとき、元の形でこの距離以内にある
        /// 別の部品（島）の粒子にも距離を伝える（分かれたパーツを一緒に動かすため）。0 なら伝えない。
        /// 範囲の決め方だけに使い、布の計算のつながり（伸び・曲げ・テザー）は変えない。
        /// </summary>
        public float ConnectDistance { get; set; }

        public BrushOptions Clone() => (BrushOptions)MemberwiseClone();
    }
}
