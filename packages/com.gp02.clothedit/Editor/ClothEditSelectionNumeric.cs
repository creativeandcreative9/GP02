using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 「数値で動かす」の入力を、<see cref="ClothEditController.ApplySelectionTransform"/> に渡すワールドの値に直す。
    /// 入力は、今のハンドルの向き（<see cref="ClothEditController.HandleRotation"/>）の軸で入れる。画面に依存しないので、テストで確かめられる。
    /// </summary>
    public static class ClothEditSelectionNumeric
    {
        /// <summary>拡大（%）の下限。0 や負の値で、点がつぶれたり裏返ったりしないようにする。</summary>
        public const float MinScalePercent = 1f;

        /// <summary>
        /// 入力をワールドの値に直す。
        /// translation = frame × 移動（cm→m）、rotation = frame × Euler(回転) × frame の逆（frame の軸のまわりの回転を、ワールドでの回転にする）、
        /// scale = 拡大 / 100（frame の軸に沿った倍率。<see cref="MinScalePercent"/> を下回らない）。
        /// </summary>
        /// <param name="frame">ハンドルの向き</param>
        /// <param name="moveCentimeters">移動（cm、X/Y/Z）</param>
        /// <param name="rotateDegrees">回転（度、X/Y/Z。Unity の <see cref="Quaternion.Euler(Vector3)"/> の順）</param>
        /// <param name="scalePercent">拡大（%、X/Y/Z。100 で変わらない）</param>
        public static void ToWorld(Quaternion frame, Vector3 moveCentimeters, Vector3 rotateDegrees, Vector3 scalePercent,
            out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            translation = frame * (moveCentimeters * 0.01f);
            rotation = frame * Quaternion.Euler(rotateDegrees) * Quaternion.Inverse(frame);
            scale = new Vector3(
                Mathf.Max(scalePercent.x, MinScalePercent) * 0.01f,
                Mathf.Max(scalePercent.y, MinScalePercent) * 0.01f,
                Mathf.Max(scalePercent.z, MinScalePercent) * 0.01f);
        }

        /// <summary>動きが何も入っていない（移動 0、回転 0、拡大 100%）か。</summary>
        public static bool IsIdentity(Vector3 moveCentimeters, Vector3 rotateDegrees, Vector3 scalePercent)
        {
            const float Epsilon = 1e-4f;
            return moveCentimeters.sqrMagnitude < Epsilon * Epsilon
                && rotateDegrees.sqrMagnitude < Epsilon * Epsilon
                && (scalePercent - new Vector3(100f, 100f, 100f)).sqrMagnitude < Epsilon * Epsilon;
        }
    }
}
