using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    // 服の付け根（ウエスト・首まわりなど）の自動ピン留め: 今の形で、付け根の近くの粒子を探す。計算は計算の中核（AnchorRings）。
    public sealed partial class ClothEditSession
    {
        /// <summary>
        /// 今の形で、付け根として留める粒子を探す（ピン留めはしない）。部品（島）ごとに、開いた縁の輪から付け根の輪を決め、その線から
        /// bandWidth 以内の同じ部品の粒子を返す（<see cref="AnchorRings.Find"/>）。島は、ピン留めのダブルクリックで部品ごと留めるのと同じ
        /// （<see cref="ParticleCloth.ParticleIsland"/>）。
        /// </summary>
        /// <param name="up">高さの向き（アバターの根元の上向き）</param>
        /// <param name="axisPoint">縦の軸が通る点（アバターの根元の位置）</param>
        public AnchorSearch FindAnchors(Vector3 up, Vector3 axisPoint, float bandWidth = AnchorRings.DefaultBandWidth)
        {
            return AnchorRings.Find(_cloth.Topology, _welded, _cloth.ParticleIsland, _solver.Positions,
                new NVector3(up.x, up.y, up.z), new NVector3(axisPoint.x, axisPoint.y, axisPoint.z), bandWidth);
        }

        /// <summary>編集を始めたときの形（元の形）の、衣装の境界の中心（ワールド）。アバターの根元が無いときの、縦の軸の位置に使う。</summary>
        public Vector3 GetRestBoundsCenter()
        {
            if (_baseWorld.Length == 0) return Vector3.zero;
            Vector3 min = _baseWorld[0], max = _baseWorld[0];
            for (int i = 1; i < _baseWorld.Length; i++)
            {
                Vector3 v = _baseWorld[i];
                if (v.x < min.x) min.x = v.x;
                if (v.y < min.y) min.y = v.y;
                if (v.z < min.z) min.z = v.z;
                if (v.x > max.x) max.x = v.x;
                if (v.y > max.y) max.y = v.y;
                if (v.z > max.z) max.z = v.z;
            }
            return (min + max) * 0.5f;
        }
    }
}
