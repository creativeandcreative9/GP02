using System.Collections.Generic;
using GP02.Core;
using UnityEditor;

namespace GP02.EditorTools
{
    // 診断: 体が布を突き抜けている所（編集中に最後に調べた結果）。
    public static partial class ClothEditWarnings
    {
        // 調べ直しはしない（重いので）。結果は編集の開始・保存・直したあと・「調べ直す」で新しくなる
        static void CollectPenetration(ClothEditController controller, List<TargetWarning> result)
        {
            PenetrationReport report = controller.LastPenetration;
            if (report == null || !report.HasBody || report.PenetratingCount == 0) return;
            string message = L10n.Tr($"体が布を突き抜けている所が {report.RegionCount} か所あります（最大 {report.MaxDepth * 1000f:F1} mm）。「貫通を直す」で、布の長さを保ったまま体の外へ押し出せます。",
                $"The body pokes through the cloth in {report.RegionCount} place(s) (up to {report.MaxDepth * 1000f:F1} mm). \"Fix Penetration\" pushes the cloth out of the body while keeping its length.");
            if (report.PinnedCount > 0)
            {
                message += L10n.Tr($" うち {report.PinnedCount} 点はピン留めなので動かせません。直す前に、そこのピンを外してください。",
                    $" {report.PinnedCount} of the points are pinned and cannot move; unpin them before fixing.");
            }
            result.Add(new TargetWarning(TargetWarningKind.Penetration, MessageType.Warning, message));
        }
    }
}
