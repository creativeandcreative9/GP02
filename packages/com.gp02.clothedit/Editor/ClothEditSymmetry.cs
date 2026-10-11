using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// 対称編集の鏡の面と、「動かす向き」の軸の決め方。画面に依存しないので、テストで確かめられる。
    /// 基準はアバター（<see cref="FindAvatarRoot"/>）のローカル座標。
    /// </summary>
    public static class ClothEditSymmetry
    {
        /// <summary>
        /// アバターの基準にする Transform。レンダラーの親をたどって最初に見つかる Animator のある Transform。
        /// 見つからなければ一番上の親。親が無ければ（レンダラーが一番上なら）レンダラー自身。
        /// </summary>
        public static Transform FindAvatarRoot(Transform renderer)
        {
            if (renderer == null) return null;
            Transform top = renderer;
            for (Transform t = renderer.parent; t != null; t = t.parent)
            {
                top = t;
                if (t.GetComponent<Animator>() != null) return t;
            }
            return top;
        }

        /// <summary>軸のローカル座標での向き。</summary>
        public static Vector3 LocalAxis(SymmetryAxis axis)
        {
            switch (axis)
            {
                case SymmetryAxis.Y: return Vector3.up;
                case SymmetryAxis.Z: return Vector3.forward;
                default: return Vector3.right;
            }
        }

        /// <summary>
        /// 設定に従った鏡の面（ワールド座標）。対称がオフ、または基準の Transform が無いときは false。
        /// 面はアバターのローカル座標で、軸に垂直で、中心から offset だけずれた所を通る。
        /// </summary>
        public static bool TryBuildMirrorPlane(ClothEditSettings settings, Transform avatarRoot, out MirrorPlane plane)
        {
            plane = default;
            if (settings == null || !settings.SymmetryEnabled || avatarRoot == null) return false;

            Vector3 axis = LocalAxis(settings.SymmetryAxis);
            Vector3 normal = avatarRoot.TransformDirection(axis);
            if (normal.sqrMagnitude < 1e-12f) return false;
            normal.Normalize();
            Vector3 point = avatarRoot.TransformPoint(axis * settings.SymmetryOffset);
            plane = new MirrorPlane(ToNumerics(point), ToNumerics(normal));
            return true;
        }

        /// <summary>
        /// 「動かす向き」を軸に固定するときの、ワールドでの軸の向き（長さ1）。
        /// 軸の固定でない（自由・法線）ときは false。
        /// </summary>
        public static bool TryGetAxisDirection(MoveDirection direction, AxisSpace space, Transform avatarRoot, out Vector3 axis)
        {
            switch (direction)
            {
                case MoveDirection.AxisX: axis = Vector3.right; break;
                case MoveDirection.AxisY: axis = Vector3.up; break;
                case MoveDirection.AxisZ: axis = Vector3.forward; break;
                default:
                    axis = Vector3.zero;
                    return false;
            }
            if (space == AxisSpace.AvatarLocal && avatarRoot != null)
            {
                axis = avatarRoot.TransformDirection(axis);
                if (axis.sqrMagnitude < 1e-12f) return false;
                axis.Normalize();
            }
            return true;
        }

        public static NVector3 ToNumerics(Vector3 v) => new NVector3(v.x, v.y, v.z);

        public static Vector3 ToUnity(NVector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
