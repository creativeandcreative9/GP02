using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 貫通（体が布を突き抜けている所）の画面: 診断の区切りの中の状態・「貫通を直す」・「調べ直す」・余白の設定と、シーンビューの赤い点。
    // ほかのファイルからは、診断の区切り（DrawDiagnosticsSection）とシーンビューの重ね描き（DrawOverlay）から1行ずつ呼ぶだけにしている
    public sealed partial class ClothEditWindow
    {
        const int MaxPenetrationDots = 1000;

        /// <summary>シーンビューで、体が布を突き抜けている所を示す点の色。</summary>
        public static readonly Color PenetrationColor = new Color(1f, 0.12f, 0.1f, 1f);

        readonly List<Vector3> _penetrationDots = new List<Vector3>();

        GUIStyle _quietStyle;

        // 診断の区切りの中に出す（編集中だけ）。
        // かんたん表示: 貫通が無いときは、目立たない1行（「✓ 体との貫通なし」）。貫通があるときは、診断の注意の下に「貫通を直す」「調べ直す」。
        // 操作を確定するたびに自動で調べ直すので（ClothEditController.IsAutoPenetrationCheck）、「古い」という表示は、自動をやめた衣装のときだけ出る
        void DrawPenetrationDiagnostics()
        {
            if (_controller == null || !_controller.IsEditing || !Shows(ClothEditUiItem.PenetrationFix)) return;
            bool detailed = Shows(ClothEditUiItem.PenetrationMargin);
            EditorGUILayout.Space(2f);
            if (detailed) EditorGUILayout.LabelField(L10n.Tr("体との貫通", "Body Penetration"), EditorStyles.boldLabel);

            PenetrationReport report = _controller.LastPenetration;
            // 自動で調べ直す衣装では、つまんでいる間も「古い」と出さない（操作を確定すると、すぐ調べ直すため）
            bool stale = ShowsStaleNotice(report, _controller.NeedsPenetrationRefresh) && !_controller.IsAutoPenetrationCheck;
            bool penetrating = report != null && report.HasBody && report.PenetratingCount > 0;
            bool canFix = _controller.HasBody && report != null && report.NeedPushCount > 0 && !_controller.IsBusy;

            if (!detailed && !penetrating && !stale)
            {
                // 貫通なし・体が見つからない・まだ調べていない: 目立たない1行と、小さな「調べ直す」
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(QuietPenetrationLine(report), PenetrationStatus(report, false)), QuietStyle(), GUILayout.ExpandWidth(true));
                    DrawRecheckButton(true);
                }
            }
            else
            {
                // 突き抜けている所があるときは、同じ内容が診断の注意として上に出ているので繰り返さない（古いときは、その表示を出す）
                if (!penetrating || stale)
                {
                    string status = PenetrationStatus(report, stale);
                    if (detailed) EditorGUILayout.HelpBox(status, MessageType.Info);
                    else EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!canFix))
                    {
                        if (GUILayout.Button(new GUIContent(L10n.Tr("貫通を直す", "Fix Penetration"),
                            L10n.Tr("体が布を突き抜けている所（と、すき間より体に近い所）を、布の長さを保ったまま体の外へ押し出します。周りの布も布として付いてきます。ピン留めした点は動きません。" +
                                    "直したあとは、つまんで押し込んでも体の中へは入りません。Ctrl+Z で戻せます（1回で1段）",
                                "Pushes the places where the body pokes through the cloth (and places closer than the margin) out of the body while keeping the cloth length. The surrounding cloth follows. Pinned points do not move. " +
                                "Afterwards the cloth stays outside the body even if you push it in. Ctrl+Z undoes it (one step)"))))
                        {
                            FixPenetrationFromWindow();
                            GUIUtility.ExitGUI();
                        }
                    }
                    DrawRecheckButton(false);
                }
            }

            if (!detailed) return;
            EditorGUI.BeginChangeCheck();
            float marginMm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("余白（mm）", "Margin (mm)"),
                L10n.Tr("体と布の間に保つ距離。これより体に近い所も「貫通を直す」で押し出し、編集中も布はこれより体に近づきません。既定 2mm",
                    "Distance kept between the body and the cloth. Places closer than this are also pushed out by Fix Penetration, and the cloth does not get closer than this while editing. Default 2 mm")),
                _settings.PenetrationMargin * 1000f, 0f, ClothEditSettings.MaxPenetrationMargin * 1000f);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.PenetrationMargin = marginMm * 0.001f;
                _settingsDirty = true;
            }
        }

        // 「調べ直す」（体を今の形で集め直して調べる）。small なら、目立たない1行の右に置く小さなボタン
        void DrawRecheckButton(bool small)
        {
            using (new EditorGUI.DisabledScope(_controller.IsBusy))
            {
                var content = new GUIContent(L10n.Tr("調べ直す", "Recheck Penetration"),
                    L10n.Tr("体（同じアバターのほかのオブジェクト）を今の形で集め直して、突き抜けている所を調べ直します。体のシェイプキーやポーズを変えたあとに押してください",
                        "Collects the body (other objects of the same avatar) again in its current shape and checks again. Press this after changing the body's shape keys or pose"));
                bool pressed = small
                    ? GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(64f))
                    : GUILayout.Button(content);
                if (pressed)
                {
                    _controller.RecheckPenetration();
                    _diagnosticsAt = double.NegativeInfinity;
                    SceneView.RepaintAll();
                }
            }
        }

        GUIStyle QuietStyle()
        {
            if (_quietStyle == null)
            {
                _quietStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };
            }
            Color color = EditorGUIUtility.isProSkin ? new Color(0.62f, 0.78f, 0.64f, 1f) : new Color(0.2f, 0.45f, 0.25f, 1f);
            _quietStyle.normal.textColor = color;
            return _quietStyle;
        }

        // 「調べたあとに形が変わっています」を出すか。体が無いときは、古くなる結果が無い（形が変わっても、調べるものが無い）
        internal static bool ShowsStaleNotice(PenetrationReport report, bool needsRefresh) => report != null && report.HasBody && needsRefresh;

        // かんたん表示の、目立たない1行（突き抜けている所が無いとき）
        internal static string QuietPenetrationLine(PenetrationReport report)
        {
            if (report == null) return L10n.Tr("体との貫通: まだ調べていません", "Body penetration: not checked yet");
            if (!report.HasBody) return L10n.Tr("体が見つからないので、貫通は調べていません", "No body found, so penetration is not checked");
            return L10n.Tr("✓ 体との貫通なし", "✓ No body penetration");
        }

        internal static string PenetrationStatus(PenetrationReport report, bool stale)
        {
            string text;
            if (report == null)
            {
                text = L10n.Tr("まだ調べていません。「調べ直す」を押してください。", "Not checked yet. Press Recheck Penetration.");
            }
            else if (!report.HasBody)
            {
                text = L10n.Tr("体（同じアバターのほかのオブジェクトのうち、布の内側にあるもの）が見つからないので、貫通は調べていません。",
                    "No body (other objects of the same avatar inside the cloth) was found, so penetration is not checked.");
            }
            else if (report.PenetratingCount > 0)
            {
                text = L10n.Tr($"体が布を突き抜けている所: {report.RegionCount} か所（最大 {report.MaxDepth * 1000f:F1} mm、{report.PenetratingCount:N0} 点）",
                    $"Body poking through the cloth: {report.RegionCount} place(s) (up to {report.MaxDepth * 1000f:F1} mm, {report.PenetratingCount:N0} points)");
                if (report.PinnedCount > 0)
                {
                    text += "\n" + L10n.Tr($"うち {report.PinnedCount:N0} 点はピン留めなので押し出せません。",
                        $"{report.PinnedCount:N0} of them are pinned and cannot be pushed out.");
                }
            }
            else
            {
                text = L10n.Tr("体が布を突き抜けている所: 0 か所", "Body poking through the cloth: 0 places");
                if (report.CloseCount > 0)
                {
                    text += L10n.Tr($"（すき間 {report.Margin * 1000f:F1} mm より体に近い所: {report.CloseCount:N0} 点）",
                        $" (closer than the {report.Margin * 1000f:F1} mm margin: {report.CloseCount:N0} points)");
                }
            }
            if (stale)
            {
                text += "\n" + L10n.Tr("調べたあとに形かすき間が変わっています。「調べ直す」で新しくなります。", "The shape or margin changed after the check. Press Recheck Penetration to update.");
            }
            return text;
        }

        void FixPenetrationFromWindow()
        {
            PenetrationReport before = _controller.LastPenetration;
            int regionsBefore = before != null ? before.RegionCount : 0;
            int remaining = _controller.FixPenetration();
            PenetrationFixResult result = _controller.LastPenetrationFix;
            if (remaining >= 0 && result != null)
            {
                string details = L10n.Tr($"貫通を直しました: {regionsBefore} か所 → {result.RemainingRegions} か所（最大の伸び {result.MaxStretch * 100f:F1}%）",
                    $"Fixed penetration: {regionsBefore} → {result.RemainingRegions} place(s) (max stretch {result.MaxStretch * 100f:F1}%)");
                if (result.RemainingPinned > 0) details += L10n.Tr("。ピン留めの所は残っています", ". Pinned places remain");
                if (result.Cancelled) details += L10n.Tr("（途中でやめました）", " (cancelled)");
                Report(result.RemainingRegions == 0 ? L10n.Tr("貫通を直しました", "Fixed penetration") : L10n.Tr("貫通が残っています", "Some penetration remains"), details,
                    result.RemainingRegions == 0 ? MessageType.Info : MessageType.Warning);
            }
            _diagnosticsAt = double.NegativeInfinity;
            SceneView.RepaintAll();
        }

        // シーンビューに、最後に調べた結果の、体が布を突き抜けている所を赤い点で示す（直したあとは消える）。
        // 白い縁を付けて、ピン留めの点（青）や選んだ点（橙）と見分けられるようにする
        void DrawPenetrationDots(ClothEditSession session)
        {
            PenetrationReport report = session.LastPenetration;
            if (report == null || (report.PointParticles.Count == 0 && report.PointBodyVertices.Count == 0)) return;
            session.GetPenetrationPoints(_penetrationDots, MaxPenetrationDots);
            Handles.color = Color.white;
            for (int i = 0; i < _penetrationDots.Count; i++) Dot(_penetrationDots[i], 0.022f);
            Handles.color = PenetrationColor;
            for (int i = 0; i < _penetrationDots.Count; i++) Dot(_penetrationDots[i], 0.015f);
        }
    }
}
