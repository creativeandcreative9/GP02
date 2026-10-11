using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>F キーで変える大きさの種類。</summary>
    public enum RadiusDragTarget
    {
        /// <summary>ブラシ半径（F）。</summary>
        BrushRadius,

        /// <summary>影響範囲（Shift+F）。</summary>
        InfluenceRadius,

        /// <summary>「選んで動かす」のなめらかさの半径（「選んで動かす」の間の F）。</summary>
        SelectionSmoothness,
    }

    /// <summary>
    /// シーンビューで F を押してマウスを横に動かし、半径を変える操作（Blender 方式）の計算。画面に依存しないので、テストで確かめられる。
    /// 始めたときの値から、横の動きに比例して変わり（右で大きく）、範囲に収める。確定（クリック・もう一度 F）か取り消し（Esc・右クリック）で終わる。
    /// 値はセンチメートルで扱う（ウィンドウのスライダーと同じ）。
    /// </summary>
    public sealed class ClothEditRadiusDrag
    {
        /// <summary>範囲の端から端まで動かすのにかかる横の動き（ピクセル）。</summary>
        public const float PixelsForFullRange = 400f;

        float _startX;

        /// <summary>変えている途中か。</summary>
        public bool Active { get; private set; }

        /// <summary>変えている大きさの種類。</summary>
        public RadiusDragTarget Target { get; private set; }

        /// <summary>始めたときの値（cm）。取り消すとこの値に戻す。</summary>
        public float StartValue { get; private set; }

        /// <summary>今の値（cm）。</summary>
        public float Value { get; private set; }

        public float Min { get; private set; }
        public float Max { get; private set; }

        /// <summary>
        /// キーの組み合わせから、変える大きさ。Shift なら影響範囲、「選んで動かす」の間はなめらかさの半径、ほかはブラシ半径。
        /// </summary>
        public static RadiusDragTarget TargetFor(bool shift, ClothEditMode tool)
        {
            if (shift) return RadiusDragTarget.InfluenceRadius;
            return tool == ClothEditMode.Select ? RadiusDragTarget.SelectionSmoothness : RadiusDragTarget.BrushRadius;
        }

        /// <summary>始める。mouseX は今のマウスの横位置（ピクセル）。</summary>
        public void Begin(RadiusDragTarget target, float mouseX, float current, float min, float max)
        {
            Active = true;
            Target = target;
            _startX = mouseX;
            Min = min;
            Max = Mathf.Max(min, max);
            StartValue = Mathf.Clamp(current, Min, Max);
            Value = StartValue;
        }

        /// <summary>マウスが動いた。新しい値（cm）を返す。</summary>
        public float Update(float mouseX)
        {
            if (!Active) return Value;
            float rate = (Max - Min) / PixelsForFullRange;
            Value = Mathf.Clamp(StartValue + (mouseX - _startX) * rate, Min, Max);
            return Value;
        }

        /// <summary>確定する。確定した値（cm）を返す。</summary>
        public float Confirm()
        {
            Active = false;
            return Value;
        }

        /// <summary>取り消す。始めたときの値（cm）を返す。</summary>
        public float Cancel()
        {
            Active = false;
            Value = StartValue;
            return StartValue;
        }
    }
}
