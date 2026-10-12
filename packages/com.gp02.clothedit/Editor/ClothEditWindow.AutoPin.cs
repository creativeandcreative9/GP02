using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 「服の付け根（ウエスト・首まわりなど）を自動でピン留め」。計算は計算の中核（AnchorRings）、Undo の1段にするのは ClothEditController.PinAnchors。
    // ボタンは、手順3が今の手順のときの案内・ピン留めの道具の欄・診断の「ピンが無い」の行に出す。
    public sealed partial class ClothEditWindow
    {
        GUIStyle _autoPinButtonStyle;   // 窓が狭いときは、ボタンの文字を折り返す（ラベルが長いので）

        // ボタン。編集中で、手が止まっているときだけ押せる
        void DrawAutoPinButton()
        {
            if (_autoPinButtonStyle == null) _autoPinButtonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true };
            using (new EditorGUI.DisabledScope(!_controller.IsEditing || _controller.IsBusy))
            {
                var content = new GUIContent(L10n.Tr("服の付け根（ウエスト・首まわりなど）を自動でピン留め", "Auto-Pin the Clothing's Roots"),
                    L10n.Tr("服の付け根（ウエスト・首まわり・袖の肩の側など）を、一周まとめてピン留めします。部品ごとに、体の上のほうの、真ん中に近い縁を選びます。今のピンは消えません。Ctrl+Z で戻せます",
                        "Pins the clothing's roots (the waist, the neckline, the shoulder end of sleeves, and so on) all the way around. For each part it picks the opening that is highest and closest to the center. Existing pins stay. Ctrl+Z undoes this"));
                if (GUILayout.Button(content, _autoPinButtonStyle))
                {
                    AutoPinAnchors();
                    GUIUtility.ExitGUI();
                }
            }
        }

        /// <summary>付け根を自動でピン留めして、結果を知らせる（通知は短く、詳しい文は結果の欄に残す）。結果を返す。</summary>
        internal AnchorPinResult AutoPinAnchors()
        {
            AnchorPinResult result = _controller.PinAnchors();
            if (result.Pinned > 0)
            {
                Report(L10n.Tr($"ピン留めしました（{result.Pinned:N0} 点）", $"Pinned ({result.Pinned:N0} points)"),
                    L10n.Tr($"付け根を自動でピン留めしました（{result.Pinned:N0} 点）。部品 {result.Rings} 個の、ウエストや首まわりなどの縁に沿って、一周ぶん留まっています。気になる所は、ピン留めの道具で Ctrl+ドラッグすると外せます。",
                        $"Auto-pinned the roots ({result.Pinned:N0} points), all the way along the waist, neckline or similar opening of {result.Rings} part(s). Ctrl+Drag with the Pin tool removes pins you do not want."));
            }
            else if (result.WasFound)
            {
                Report(L10n.Tr("すでにピン留めしてあります", "Already pinned"),
                    L10n.Tr("付け根は、すでにピン留めしてあります。", "The roots are already pinned."));
            }
            else
            {
                Report(L10n.Tr("付け根が見つかりません", "No roots found"),
                    L10n.Tr("留める縁が見つかりませんでした。付け根にできるのは、メッシュの開いた縁（スカートのウエストや、上着の首まわりなど）です。閉じた形の衣装では、手でピン留めしてください。",
                        "No opening to pin was found. The roots are the open edges of the mesh (the waist of a skirt, the neckline of a jacket, and so on). For a closed shape, pin by hand."),
                    MessageType.Warning);
            }
            _diagnosticsAt = double.NegativeInfinity;
            SceneView.RepaintAll();
            return result;
        }
    }
}
