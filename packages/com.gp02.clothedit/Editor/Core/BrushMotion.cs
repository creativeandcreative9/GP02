using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// つまんだ範囲の動き（ワールド座標）。点 p の行き先 = Pivot + Rotation * (Scale * (p - Pivot)) + Translation。
    /// 範囲の粒子 i の目標は、つまみ始めの位置 start_i から、重み w_i の分だけこの動きに近づけた位置
    /// start_i + w_i * (行き先(start_i) - start_i)。
    ///
    /// 軸ごとの拡大縮小（<see cref="ScaleAxes"/>）を設定すると、行き先は
    /// Pivot + Rotation * (F * diag(Scale * ScaleAxes) * F⁻¹ * (p - Pivot)) + Translation（F は <see cref="ScaleFrame"/> の回転）。
    /// 先に F の軸で非一様に拡大してから Rotation で回す。ScaleAxes を設定しない（または (1,1,1)）ときは上の式の一様な拡大そのもの。
    ///
    /// 既定値（<c>default</c>）は何もしない動き（回転なし、拡大率 1）。
    /// </summary>
    public struct BrushMotion
    {
        Quaternion _rotation;
        float _scale;
        bool _hasScale;
        Vector3 _scaleAxes;
        bool _hasScaleAxes;
        Quaternion _scaleFrame;

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

        /// <summary>
        /// 軸ごとの拡大率（<see cref="ScaleFrame"/> の軸 X・Y・Z に沿う倍率）。<see cref="Scale"/> に掛かる。
        /// 既定は (1,1,1)。<c>default</c> の構造体でも (1,1,1) になる。
        /// </summary>
        public Vector3 ScaleAxes
        {
            get => _hasScaleAxes ? _scaleAxes : Vector3.One;
            set
            {
                _scaleAxes = value;
                _hasScaleAxes = true;
            }
        }

        /// <summary>
        /// <see cref="ScaleAxes"/> の軸の向き。成分がすべて 0（<c>default</c>）なら単位（ワールドの軸）とみなす。長さは使うときに 1 にそろえる。
        /// </summary>
        public Quaternion ScaleFrame
        {
            get => IsZero(_scaleFrame) ? Quaternion.Identity : _scaleFrame;
            set => _scaleFrame = value;
        }

        /// <summary>軸ごとの拡大縮小が設定されているか（(1,1,1) のままなら false）。</summary>
        public bool HasAxisScale => _hasScaleAxes && _scaleAxes != Vector3.One;

        public BrushMotion(Vector3 translation, Quaternion rotation, float scale, Vector3 pivot)
        {
            _rotation = rotation;
            _scale = scale;
            _hasScale = true;
            _scaleAxes = Vector3.One;
            _hasScaleAxes = false;
            _scaleFrame = default;
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
            if (!HasAxisScale) return Pivot + Vector3.Transform((p - Pivot) * Scale, NormalizedRotation()) + Translation;

            // ScaleFrame の軸に直して拡大し、元の向きに戻してから回す
            Quaternion frame = NormalizedScaleFrame();
            Vector3 local = Vector3.Transform(p - Pivot, Quaternion.Conjugate(frame));
            local *= ScaleAxes * Scale;
            return Pivot + Vector3.Transform(Vector3.Transform(local, frame), NormalizedRotation()) + Translation;
        }

        internal Quaternion NormalizedRotation() => Normalized(Rotation);

        internal Quaternion NormalizedScaleFrame() => Normalized(ScaleFrame);

        static Quaternion Normalized(Quaternion q)
        {
            float length = q.Length();
            return length > 0f && !float.IsInfinity(length) && !float.IsNaN(length) ? Quaternion.Multiply(q, 1f / length) : Quaternion.Identity;
        }

        /// <summary>
        /// 回転 Rotation を掛ける前の、拡大縮小の 3x3 行列 F diag(Scale * ScaleAxes) Fᵀ（列ベクトルに左から掛ける形）。
        /// </summary>
        internal void GetScaleMatrix(out float m00, out float m01, out float m02, out float m10, out float m11, out float m12,
            out float m20, out float m21, out float m22)
        {
            Quaternion q = NormalizedScaleFrame();
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            // 回転行列 F の列（F の軸 X・Y・Z）
            float f00 = 1f - 2f * (y * y + z * z), f01 = 2f * (x * y - z * w), f02 = 2f * (x * z + y * w);
            float f10 = 2f * (x * y + z * w), f11 = 1f - 2f * (x * x + z * z), f12 = 2f * (y * z - x * w);
            float f20 = 2f * (x * z - y * w), f21 = 2f * (y * z + x * w), f22 = 1f - 2f * (x * x + y * y);
            Vector3 s = ScaleAxes * Scale;
            m00 = s.X * f00 * f00 + s.Y * f01 * f01 + s.Z * f02 * f02;
            m01 = s.X * f00 * f10 + s.Y * f01 * f11 + s.Z * f02 * f12;
            m02 = s.X * f00 * f20 + s.Y * f01 * f21 + s.Z * f02 * f22;
            m10 = m01;
            m11 = s.X * f10 * f10 + s.Y * f11 * f11 + s.Z * f12 * f12;
            m12 = s.X * f10 * f20 + s.Y * f11 * f21 + s.Z * f12 * f22;
            m20 = m02;
            m21 = m12;
            m22 = s.X * f20 * f20 + s.Y * f21 * f21 + s.Z * f22 * f22;
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
            AffineMotion m;
            if (motion.HasAxisScale)
            {
                // L = R * (F S Fᵀ) - I。軸ごとの拡大は、回転を掛ける前の行列 M = F S Fᵀ にまとめる
                motion.GetScaleMatrix(out float s00, out float s01, out float s02, out float s10, out float s11, out float s12,
                    out float s20, out float s21, out float s22);
                float r00 = 1f - 2f * (y * y + z * z), r01 = 2f * (x * y - z * w), r02 = 2f * (x * z + y * w);
                float r10 = 2f * (x * y + z * w), r11 = 1f - 2f * (x * x + z * z), r12 = 2f * (y * z - x * w);
                float r20 = 2f * (x * z - y * w), r21 = 2f * (y * z + x * w), r22 = 1f - 2f * (x * x + y * y);
                m = new AffineMotion
                {
                    L00 = r00 * s00 + r01 * s10 + r02 * s20 - 1f,
                    L01 = r00 * s01 + r01 * s11 + r02 * s21,
                    L02 = r00 * s02 + r01 * s12 + r02 * s22,
                    L10 = r10 * s00 + r11 * s10 + r12 * s20,
                    L11 = r10 * s01 + r11 * s11 + r12 * s21 - 1f,
                    L12 = r10 * s02 + r11 * s12 + r12 * s22,
                    L20 = r20 * s00 + r21 * s10 + r22 * s20,
                    L21 = r20 * s01 + r21 * s11 + r22 * s21,
                    L22 = r20 * s02 + r21 * s12 + r22 * s22 - 1f,
                };
                return FinishFromBrushMotion(m, motion, origin);
            }
            m = new AffineMotion
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
            return FinishFromBrushMotion(m, motion, origin);
        }

        static AffineMotion FinishFromBrushMotion(AffineMotion m, in BrushMotion motion, Vector3 origin)
        {
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
