using GP02.Core;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>「服の付け根を自動でピン留め」の結果。</summary>
    public sealed class AnchorPinResult
    {
        /// <summary>新しくピン留めした粒子の数（すでにピン留めしてあったものは含めない）。</summary>
        public int Pinned;

        /// <summary>付け根として見つけた粒子の数（すでにピン留めしてあったものを含む）。</summary>
        public int Found;

        /// <summary>付け根にした輪の数（部品ごとに1つ）。</summary>
        public int Rings;

        /// <summary>開いた縁のある部品の数。0 なら、閉じた形の衣装（付け根にできる縁が無い）。</summary>
        public int OpenParts;

        /// <summary>部品の数。</summary>
        public int Parts;

        /// <summary>付け根が見つかったか（開いた縁があり、その近くに粒子があった）。</summary>
        public bool WasFound => Found > 0;
    }

    /// <summary>
    /// 服の付け根（ウエスト・首まわりなど）の自動ピン留め。画面に依存しないので、EditMode テストから動かせる。
    /// 計算は計算の中核（<see cref="AnchorRings"/>）。ここは、高さの向きと縦の軸を決めて、今のピンに足し、Unity の Undo の1段にする。
    /// </summary>
    public sealed partial class ClothEditController
    {
        /// <summary>
        /// 付け根を決めるときの、高さの向きと縦の軸が通る点。アバターの根元（親をたどって最初に見つかる Animator の Transform）の上向きと位置。
        /// 見つからなければ、世界の上向きと、対象の衣装の境界の中心（編集を始めたときの形の）。編集中でなければ、世界の上向きと原点。
        /// </summary>
        public void GetAnchorFrame(out Vector3 up, out Vector3 axisPoint)
        {
            Transform animatorRoot = _target != null ? ClothEditSymmetry.FindAnimatorRoot(_target.transform) : null;
            if (animatorRoot != null)
            {
                up = animatorRoot.up;
                axisPoint = animatorRoot.position;
                return;
            }
            up = Vector3.up;
            axisPoint = _session != null ? _session.GetRestBoundsCenter() : Vector3.zero;
        }

        /// <summary>
        /// 服の付け根（スカート・タイツのウエスト、上着の首まわり、袖の肩の側）を自動でピン留めする。今のピンに足す（消さない）。
        /// 部品（島）ごとに、開いた縁の輪のうち一番付け根らしいものを選び、その線から帯の幅（既定 2cm）以内の粒子を留める
        /// （<see cref="AnchorRings"/>）。開いた縁の無い部品（閉じた形）は留めない。Unity の Undo の1段になる。
        /// 付け根が見つからなければ、何も記録しない。編集中でなければ、何もしない。
        /// </summary>
        public AnchorPinResult PinAnchors(float bandWidth = AnchorRings.DefaultBandWidth)
        {
            var result = new AnchorPinResult();
            if (!EnsureAlive()) return result;
            FinishStroke();
            GetAnchorFrame(out Vector3 up, out Vector3 axisPoint);
            AnchorSearch search = _session.FindAnchors(up, axisPoint, bandWidth);
            result.Parts = search.IslandCount;
            result.OpenParts = search.OpenIslandCount;
            result.Rings = search.Rings.Count;
            result.Found = search.Particles.Count;
            if (result.Found == 0) return result;

            int[] particles = search.Particles.ToArray();
            result.Pinned = ChangePins(L10n.Tr("GP02 付け根を自動でピン留め", "GP02 Auto-Pin Roots"), () => _session.SetPinned(particles, true));
            ClothEditLog.Info($"[GP02] {_session.DescribeTargets()}: 付け根を自動でピン留めしました。部品 {result.Parts} 個のうち開いた縁のある部品 {result.OpenParts} 個・輪 {result.Rings} 個・点 {result.Found} 個（新しく {result.Pinned} 個）");
            return result;
        }
    }
}
