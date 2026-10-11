using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// つまんだ範囲の動き（ワールド座標）。点 p の行き先 = Pivot + Rotation * (Scale * (p - Pivot)) + Translation。
    /// 範囲の粒子 i の目標は、つまみ始めの位置 start_i から、重み w_i の分だけこの動きに近づけた位置
    /// start_i + w_i * (行き先(start_i) - start_i)。
    ///
    /// 既定値（<c>default</c>）は何もしない動き（回転なし、拡大率 1）。
    /// </summary>
    public struct BrushMotion
    {
        Quaternion _rotation;
        float _scale;
        bool _hasScale;

        /// <summary>平行移動（メートル）。</summary>
        public Vector3 Translation { get; set; }

        /// <summary>回転と拡大縮小の中心（ワールド座標）。</summary>
        public Vector3 Pivot { get; set; }

        /// <summary>回転。成分がすべて 0（<c>default</c>）なら回転なしとみなす。長さは使うときに 1 にそろえる。</summary>
        public Quaternion Rotation
        {
            get => IsZero(_rotation) ? Quaternion.Identity : _rotation;
            set => _rotation = value;
        }

        /// <summary>拡大率。既定は 1。</summary>
        public float Scale
        {
            get => _hasScale ? _scale : 1f;
            set
            {
                _scale = value;
                _hasScale = true;
            }
        }

        public BrushMotion(Vector3 translation, Quaternion rotation, float scale, Vector3 pivot)
        {
            _rotation = rotation;
            _scale = scale;
            _hasScale = true;
            Translation = translation;
            Pivot = pivot;
        }

        /// <summary>何もしない動き。</summary>
        public static BrushMotion Identity => default;

        /// <summary>平行移動だけの動き。</summary>
        public static BrushMotion FromTranslation(Vector3 translation) => new BrushMotion { Translation = translation };

        /// <summary>点 p の行き先。</summary>
        public Vector3 Apply(Vector3 p)
        {
            return Pivot + Vector3.Transform((p - Pivot) * Scale, NormalizedRotation()) + Translation;
        }

        internal Quaternion NormalizedRotation()
        {
            Quaternion q = Rotation;
            float length = q.Length();
            return length > 0f && !float.IsInfinity(length) && !float.IsNaN(length) ? Quaternion.Multiply(q, 1f / length) : Quaternion.Identity;
        }

        static bool IsZero(Quaternion q) => q.X == 0f && q.Y == 0f && q.Z == 0f && q.W == 0f;
    }

    /// <summary>
    /// ソルバーの内部の座標での動き。粒子の移動量 = L p + B（L は行列 - 単位行列）。
    /// 単位行列を引いて持つのは、平行移動だけのときに L が厳密に 0 になり、移動量が丸め誤差なしで B になるため。
    /// </summary>
    internal struct AffineMotion : IEquatable<AffineMotion>
    {
        public float L00, L01, L02, L10, L11, L12, L20, L21, L22;
        public float Bx, By, Bz;

        public static AffineMotion FromTranslation(Vector3 t)
        {
            return new AffineMotion { Bx = t.X, By = t.Y, Bz = t.Z };
        }

        /// <summary>ワールド座標の動きを、内部の座標（ワールド - origin）での動きにする。</summary>
        public static AffineMotion FromBrushMotion(in BrushMotion motion, Vector3 origin)
        {
            Quaternion q = motion.NormalizedRotation();
            float s = motion.Scale;
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            var m = new AffineMotion
            {
                // 回転行列（列ベクトルに左から掛ける形。Vector3.Transform(v, q) と同じ向き）× 拡大率 - 単位行列
                L00 = s * (1f - 2f * (y * y + z * z)) - 1f,
                L01 = s * (2f * (x * y - z * w)),
                L02 = s * (2f * (x * z + y * w)),
                L10 = s * (2f * (x * y + z * w)),
                L11 = s * (1f - 2f * (x * x + z * z)) - 1f,
                L12 = s * (2f * (y * z - x * w)),
                L20 = s * (2f * (x * z - y * w)),
                L21 = s * (2f * (y * z + x * w)),
                L22 = s * (1f - 2f * (x * x + y * y)) - 1f,
            };
            // 内部の座標での移動量 = L p + T - L * (中心の内部の座標)
            Vector3 pivot = motion.Pivot - origin;
            Vector3 lp = m.Multiply(pivot);
            m.Bx = motion.Translation.X - lp.X;
            m.By = motion.Translation.Y - lp.Y;
            m.Bz = motion.Translation.Z - lp.Z;
            return m;
        }

        /// <summary>L v（移動量の、位置に比例する部分）。</summary>
        public Vector3 Multiply(Vector3 v)
        {
            return new Vector3(
                L00 * v.X + L01 * v.Y + L02 * v.Z,
                L10 * v.X + L11 * v.Y + L12 * v.Z,
                L20 * v.X + L21 * v.Y + L22 * v.Z);
        }

        /// <summary>
        /// 鏡に映した動き R∘M∘R（R は法線 n（長さ1）、面上の点 point の鏡映。内部の座標）。
        /// H = I - 2nnᵀ、c = 2(n·point)n とすると、L' = H L H、B' = H(L c + B)。
        /// 毎コマ呼ばれるので、配列を作らずに展開して計算する。
        /// </summary>
        public AffineMotion Mirrored(Vector3 point, Vector3 n)
        {
            // H L H = L - 2 n uᵀ - 2 v nᵀ + 4 k n nᵀ（u = Lᵀn、v = L n、k = n·L n）
            float ux = n.X * L00 + n.Y * L10 + n.Z * L20;
            float uy = n.X * L01 + n.Y * L11 + n.Z * L21;
            float uz = n.X * L02 + n.Y * L12 + n.Z * L22;
            Vector3 v = Multiply(n);
            float k = n.X * v.X + n.Y * v.Y + n.Z * v.Z;
            float k4 = 4f * k;
            var r = new AffineMotion
            {
                L00 = L00 - 2f * n.X * ux - 2f * v.X * n.X + k4 * n.X * n.X,
                L01 = L01 - 2f * n.X * uy - 2f * v.X * n.Y + k4 * n.X * n.Y,
                L02 = L02 - 2f * n.X * uz - 2f * v.X * n.Z + k4 * n.X * n.Z,
                L10 = L10 - 2f * n.Y * ux - 2f * v.Y * n.X + k4 * n.Y * n.X,
                L11 = L11 - 2f * n.Y * uy - 2f * v.Y * n.Y + k4 * n.Y * n.Y,
                L12 = L12 - 2f * n.Y * uz - 2f * v.Y * n.Z + k4 * n.Y * n.Z,
                L20 = L20 - 2f * n.Z * ux - 2f * v.Z * n.X + k4 * n.Z * n.X,
                L21 = L21 - 2f * n.Z * uy - 2f * v.Z * n.Y + k4 * n.Z * n.Y,
                L22 = L22 - 2f * n.Z * uz - 2f * v.Z * n.Z + k4 * n.Z * n.Z,
            };
            float d = n.X * point.X + n.Y * point.Y + n.Z * point.Z;
            Vector3 lc = Multiply(new Vector3(2f * d * n.X, 2f * d * n.Y, 2f * d * n.Z));
            float x = lc.X + Bx, y = lc.Y + By, z = lc.Z + Bz;
            float dot2 = 2f * (n.X * x + n.Y * y + n.Z * z);
            r.Bx = x - dot2 * n.X;
            r.By = y - dot2 * n.Y;
            r.Bz = z - dot2 * n.Z;
            return r;
        }

        public bool Equals(AffineMotion o)
        {
            return L00 == o.L00 && L01 == o.L01 && L02 == o.L02
                && L10 == o.L10 && L11 == o.L11 && L12 == o.L12
                && L20 == o.L20 && L21 == o.L21 && L22 == o.L22
                && Bx == o.Bx && By == o.By && Bz == o.Bz;
        }

        public override bool Equals(object obj) => obj is AffineMotion o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(L00, L11, L22, Bx, By, Bz);
    }
}
