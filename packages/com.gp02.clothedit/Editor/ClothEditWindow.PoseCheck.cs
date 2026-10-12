using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 動作チェックの画面: ウィンドウの「動作チェック」の区切りと、シーンビューの重ね描き。
    //  - 区切り: 始める・やめる、結果（よく使うポーズ／大きく動くポーズに分けた2行）、悪い順の上位（大きく動くポーズは別の小さな見出しの下）、
    //    順に見る・順に再生・元の姿勢に戻る、「大きく動くポーズも直す」「突き抜ける所を色の点で示す」、おすすめの直し方（件数とボタン）、くわしい説明（折りたたみ）。
    //    くわしく表示では、外へ逃がせる所の内訳・試すポーズの一覧・ポーズごとの結果なども出す
    //  - シーンビュー: ポーズを見せている間は、そのポーズで突き抜けた所の赤い点。編集の姿勢では、直す範囲のポーズでの最悪の深さで点を色分け
    //    （ほかの操作を始めると消え、区切りのトグルで出し直せる）
    // ほかのファイルからは、区切りの描画・重ね描き・シーンビューのクリックと Esc・案内の文から1行ずつ呼ぶだけにしている
    public sealed partial class ClothEditWindow
    {
        const int MaxPoseDots = 1500;
        const int MaxPoseDepthDots = 3000;

        /// <summary>最悪の深さの色分けで、黄色になる深さと、赤になる深さ（メートル）。</summary>
        public const float PoseDepthYellow = 0.001f;
        public const float PoseDepthRed = 0.01f;

        /// <summary>ポーズを見せているときの、体どうしの重なり（服では直せない所）の点の色。</summary>
        public static readonly Color PoseOverlapColor = new Color(0.62f, 0.38f, 0.95f, 1f);

        [SerializeField] bool _foldPoseCheck = true;
        [SerializeField] bool _foldPoseList;
        [SerializeField] bool _foldPoseEntries;
        [SerializeField] bool _foldPoseExplain;

        readonly List<Vector3> _poseDots = new List<Vector3>();
        readonly List<int> _poseDepthParticles = new List<int>();
        float[] _poseDepthSource;          // _poseDepthParticles を作ったときの深さの配列（変わったら作り直す）
        GUIStyle _poseEntryStyle, _poseClearStyle, _poseHeadlineStyle;

        /// <summary>最悪の深さ（メートル）の色。1mm で黄、1cm 以上で赤。</summary>
        public static Color PoseDepthColor(float depth)
        {
            float t = Mathf.Clamp01((depth - PoseDepthYellow) / (PoseDepthRed - PoseDepthYellow));
            return Color.Lerp(new Color(1f, 0.9f, 0.15f, 1f), PenetrationColor, t);
        }

        void DrawPoseCheckSection()
        {
            if (!_controller.IsEditing || !Shows(ClothEditUiItem.PoseCheck)) return;
            _foldPoseCheck = Section(_foldPoseCheck, L10n.Tr("動作チェック", "Motion Check"));
            if (!_foldPoseCheck) return;

            string problem = _controller.GetPoseCheckProblem();
            if (problem != null)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Info);
                EditorGUILayout.Space();
                return;
            }

            if (_controller.IsPoseCheckRunning)
            {
                _controller.GetPoseCheckCounts(out int done, out int total);
                Rect bar = GUILayoutUtility.GetRect(18f, 20f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(bar, _controller.PoseCheckProgress, L10n.Tr($"ポーズを試しています… {done} / {total}", $"Trying poses... {done} / {total}"));
                if (GUILayout.Button(new GUIContent(L10n.Tr("やめる", "Stop"), L10n.Tr("動作チェックをやめます（前の結果は残ります）", "Stops the motion check (the previous result stays)"))))
                {
                    _controller.CancelPoseCheck();
                }
            }
            else
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("動作チェックを始める", "Start Motion Check"),
                    L10n.Tr("座る・足を上げる・腕を上げる・お辞儀など、いろいろなポーズを自動で試して、体が服を突き抜ける所を探します。シーンのアバターは動かしません（見えない複製で試します）",
                        "Automatically tries many poses (sit, raise a leg, raise the arms, bow, ...) and finds where the body pokes through the outfit. The avatar in the scene is not moved (an invisible copy is used)")),
                    GUILayout.Height(28f)))
                {
                    if (!_controller.BeginPoseCheck(out string error)) EditorUtility.DisplayDialog("GP02", error, "OK");
                    GUIUtility.ExitGUI();
                }
                if (!string.IsNullOrEmpty(_controller.LastPoseCheckError)) EditorGUILayout.HelpBox(_controller.LastPoseCheckError, MessageType.Warning);
            }

            PoseCheckResult result = _controller.LastPoseCheck;
            if (result != null && !_controller.IsPoseCheckRunning) DrawPoseCheckResult(result);
            if (Shows(ClothEditUiItem.PoseCheckDetails)) DrawPoseCheckDetails(result);
            EditorGUILayout.Space();
        }

        void EnsurePoseStyles()
        {
            if (_poseEntryStyle == null) _poseEntryStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
            if (_poseHeadlineStyle == null) _poseHeadlineStyle = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            if (_poseClearStyle == null)
            {
                // 「よく使うポーズでは問題ありません ✓」を、いちばん目立たせる（大きめの太字・緑）
                _poseClearStyle = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true, fontSize = EditorStyles.boldLabel.fontSize + 2 };
                _poseClearStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.88f, 0.45f) : new Color(0.08f, 0.45f, 0.1f);
            }
        }

        void DrawPoseCheckResult(PoseCheckResult result)
        {
            EnsurePoseStyles();
            bool stale = _controller.IsPoseCheckStale;
            bool detailed = Shows(ClothEditUiItem.PoseCheckDetails);
            if (stale)
            {
                EditorGUILayout.HelpBox(L10n.Tr("形が変わりました。もう一度チェックしてください。", "The shape has changed. Run the check again."), MessageType.Warning);
            }
            if (!result.HasBody)
            {
                EditorGUILayout.LabelField(result.DescribeSummary(), EditorStyles.wordWrappedLabel);
                return;
            }

            // 結果: よく使うポーズ（問題が無ければ、いちばん目立たせる）と、大きく動くポーズ
            string common = result.DescribeCommon();
            if (common.Length > 0)
            {
                if (result.CommonClear) EditorGUILayout.LabelField(L10n.Tr("よく使うポーズでは問題ありません ✓", "No problems in everyday poses ✓"), _poseClearStyle);
                else EditorGUILayout.LabelField(common, _poseHeadlineStyle);
            }
            string large = result.DescribeLarge();
            if (large.Length > 0) EditorGUILayout.LabelField(large, EditorStyles.wordWrappedMiniLabel);
            if (detailed) EditorGUILayout.LabelField(L10n.Tr("すべてのポーズ: ", "All poses: ") + result.DescribeSummary(), EditorStyles.wordWrappedMiniLabel);
            if (result.Baseline != null && result.Baseline.IsPenetrating)
            {
                EditorGUILayout.LabelField(L10n.Tr($"今の姿勢でも突き抜けている所（{result.Baseline.RegionCount} か所）は、ここでは数えていません。「貫通を直す」で直せます。",
                    $"Places that already poke through in the current pose ({result.Baseline.RegionCount}) are not counted here. Use \"Fix Penetration\" for them."), EditorStyles.wordWrappedMiniLabel);
            }

            // 次にすること（おすすめの直し方）を先に出す。悪い順の一覧の下だと、スクロールしないと見えないため
            DrawPoseFixAdvice(result, stale, detailed);
            DrawWeightFixSection(result, stale);
            DrawLastPoseEscape(detailed);
            DrawRankedPoses(result, detailed);
            DrawPoseBrowseButtons(result);
            DrawPoseScopeToggles(result);
            DrawPoseExplanation(result);
        }

        // 悪い順の上位。直す範囲のポーズを先に、大きく動くポーズ（直さないとき）は別の小さな見出しの下に出す
        void DrawRankedPoses(PoseCheckResult result, bool detailed)
        {
            int[] fix = result.RankFixEntries();
            DrawRankedList(result, fix, 3);
            int[] other = result.RankOtherEntries();
            if (other.Length == 0) return;
            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(L10n.Tr("大きく動くポーズ（直す範囲の外）", "Big-motion poses (not being fixed)"), EditorStyles.miniBoldLabel);
            DrawRankedList(result, other, detailed ? 3 : 2);
        }

        void DrawRankedList(PoseCheckResult result, int[] ranked, int max)
        {
            for (int k = 0; k < ranked.Length && k < max; k++)
            {
                PoseCheckEntry entry = result.Entries[ranked[k]];
                string label = $"{k + 1}. {entry.Name}: {entry.Describe()}";
                using (new EditorGUI.DisabledScope(!_controller.CanPreviewPoses))
                {
                    if (GUILayout.Button(new GUIContent(label, L10n.Tr("押すと、シーンでこのポーズを見せます", "Click to show this pose in the scene")), _poseEntryStyle))
                    {
                        _controller.ShowPoseEntry(ranked[k]);
                        SceneView.RepaintAll();
                    }
                }
            }
            if (ranked.Length > max) EditorGUILayout.LabelField(L10n.Tr($"ほか {ranked.Length - max} 件（くわしく表示で一覧）", $"{ranked.Length - max} more (list in the detailed view)"), EditorStyles.miniLabel);
        }

        void DrawPoseBrowseButtons(PoseCheckResult result)
        {
            int penetrating = result.RankForBrowsing().Length;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_controller.CanPreviewPoses || penetrating == 0))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("順に見る", "Next Problem"), L10n.Tr("突き抜けたポーズを、上の一覧の順（悪い順）に1つずつシーンで見せます", "Shows the poses with poke-through in the scene, one at a time, in the order of the list above (worst first)"))))
                    {
                        _controller.ShowNextPenetratingPose();
                        SceneView.RepaintAll();
                    }
                }
                using (new EditorGUI.DisabledScope(!_controller.CanPreviewPoses))
                {
                    bool playing = _controller.IsPosePlaying;
                    if (GUILayout.Button(new GUIContent(playing ? L10n.Tr("停止", "Stop") : L10n.Tr("順に再生", "Play All"),
                        L10n.Tr("試したポーズを、1つ約1秒ずつ順に見せます", "Shows the tried poses one after another, about a second each"))))
                    {
                        if (playing) _controller.EndPosePreview();
                        else _controller.PlayPoses();
                        SceneView.RepaintAll();
                    }
                }
                using (new EditorGUI.DisabledScope(!_controller.IsPosePreviewActive))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("元の姿勢に戻る", "Back to Edit Pose"), L10n.Tr("ポーズを見せるのをやめて、編集の姿勢に戻ります（ほかの操作をしても戻ります）", "Stops showing the pose and returns to the edit pose (any other operation also does this)"))))
                    {
                        _controller.EndPosePreview();
                        SceneView.RepaintAll();
                    }
                }
            }
            PoseCheckEntry shown = _controller.PosePreview;
            if (shown != null)
            {
                EditorGUILayout.LabelField(L10n.Tr($"表示中: {shown.Name} — {shown.Describe()}（赤い点が突き抜けた所" + (shown.Result.HasOverlap ? "、紫の点が体どうしの重なり" : "") + "）",
                    $"Showing: {shown.Name} — {shown.Describe()} (red dots mark the poke-through" + (shown.Result.HasOverlap ? ", purple dots the body overlaps" : "") + ")"), EditorStyles.wordWrappedMiniLabel);
            }
        }

        // 「大きく動くポーズも直す」（設定。かんたん表示にも出す）と、「突き抜ける所を色の点で示す」（一時的。ほかの操作を始めると消える）
        void DrawPoseScopeToggles(PoseCheckResult result)
        {
            if (result.LargeSummary != null && result.LargeSummary.IncludedEntryCount > 0)
            {
                EditorGUI.BeginChangeCheck();
                bool fixLarge = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("大きく動くポーズも直す", "Also fix big-motion poses"),
                    L10n.Tr("オンにすると、座る・しゃがむ・足を上げる・開くで突き抜ける所も、色の点・外へ逃がす・ウェイトを体に寄せるの対象にします。スカートなどは大きく膨らんだり脚に貼り付いたりしやすいので、ふだんはオフのままで大丈夫です（チェックのやり直しは要りません）",
                        "When on, places that poke through when sitting, squatting, raising a leg or spreading the legs are also colored and fixed (Move Outward, Fit Weights to Body). Skirts and the like tend to bulge or stick to the legs, so leaving it off is usually fine (no need to check again)")),
                    _settings.FixLargePoses);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.FixLargePoses = fixLarge;
                    _settingsDirty = true;
                    _controller.ApplyPoseFixScope();
                    SceneView.RepaintAll();
                }
            }
            if (!result.FixScopeClear)
            {
                EditorGUI.BeginChangeCheck();
                bool dots = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("突き抜ける所を色の点で示す", "Show the poke-through as colored dots"),
                    L10n.Tr("直す範囲のポーズで一番深く突き抜けた深さで、編集の姿勢の服に点を出します（黄 1mm → 赤 1cm 以上）。ほかの操作（ブラシ・選ぶ・ピン留めなど）を始めると消えます。ここでいつでも出し直せます",
                        "Puts dots on the outfit in the edit pose, colored by the deepest poke-through in the poses being fixed (yellow 1 mm to red 1 cm or more). They disappear when you start another operation (brush, select, pin, ...). Turn them back on here at any time")),
                    _controller.IsPoseCheckDepthShown);
                if (EditorGUI.EndChangeCheck())
                {
                    _controller.SetPoseCheckDepthShown(dots);
                    SceneView.RepaintAll();
                }
            }
        }

        // おすすめの直し方: 1. 浅い所 → 外へ逃がす、2. ウェイトが原因の所 → ウェイトを体に寄せる、3. 体どうしの重なり → 服では直せない
        void DrawPoseFixAdvice(PoseCheckResult result, bool stale, bool detailed)
        {
            PoseFixAdvice advice = PoseFixAdvice.From(result, _controller.LastPoseEscape);
            if (advice == null) return;
            bool anything = advice.EscapeRegions > 0 || advice.WeightRegions > 0 || advice.RevertedEscapeRegions > 0 || advice.OverlapRegions > 0;
            if (!anything && advice.ScopeClear && !(result.CommonClear && !advice.IncludesLargePoses && result.RankOtherEntries().Length > 0)) return;

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L10n.Tr("おすすめの直し方", "Suggested fixes"), EditorStyles.boldLabel);
                if (stale)
                {
                    EditorGUILayout.LabelField(L10n.Tr("形が変わったので、もう一度チェックしてから直してください。", "The shape changed, so run the check again before fixing."), EditorStyles.wordWrappedMiniLabel);
                }
                int number = 0;
                if (advice.ScopeClear)
                {
                    // 直す範囲には問題が無い。大きく動くポーズだけに突き抜けがあるときは、直したいときの方法を一言
                    EditorGUILayout.LabelField(advice.IncludesLargePoses
                        ? L10n.Tr("直すことはありません ✓", "Nothing to fix ✓")
                        : L10n.Tr("直すことはありません ✓（大きく動くポーズも直したいときは、下の「大きく動くポーズも直す」をオンにします）",
                            "Nothing to fix ✓ (to also fix the big-motion poses, turn on \"Also fix big-motion poses\" below)"), EditorStyles.wordWrappedLabel);
                }
                if (advice.EscapeRegions > 0)
                {
                    number++;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(new GUIContent(L10n.Tr($"{number}. 浅い所が {advice.EscapeRegions} か所", $"{number}. Shallow places: {advice.EscapeRegions}"),
                            L10n.Tr("編集の姿勢で外へ少し（2cm まで）動かせば、そのポーズで突き抜けなくなる所です", "Places that stop poking through in the pose if moved a little (up to 2 cm) outward in the edit pose")), EditorStyles.wordWrappedLabel);
                        using (new EditorGUI.DisabledScope(!_controller.CanEscapePoseCheck || _controller.IsBusy))
                        {
                            if (GUILayout.Button(new GUIContent(L10n.Tr("元のポーズで外へ逃がす", "Move Outward in Edit Pose"),
                                L10n.Tr("突き抜けた所の布を、編集の姿勢で、そのポーズで足りるだけ外へ動かします（布の長さを保って周りも付いてきます）。終わったら、もう一度チェックして前後の件数を出します。効かなければ元に戻します。Ctrl+Z で戻せます（1回で1段）",
                                    "Moves the cloth at those places outward in the edit pose, just enough for the pose (keeping the cloth length; the surroundings follow), then checks again and shows the counts before and after. It is undone if it does not help. Ctrl+Z undoes it (one step)")),
                                GUILayout.Width(170f)))
                            {
                                EscapePoseCheckFromWindow();
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }
                if (advice.RevertedEscapeRegions > 0)
                {
                    EditorGUILayout.LabelField(L10n.Tr($"浅い所 {advice.RevertedEscapeRegions} か所は、外へ逃がしても減らなかったので、このままで大丈夫です（浅い突き抜けで、見た目にはほとんど出ません）。",
                        $"The {advice.RevertedEscapeRegions} shallow place(s) did not improve when moved outward, so leaving them is fine (shallow poke-through that barely shows)."), EditorStyles.wordWrappedMiniLabel);
                }
                if (advice.WeightRegions > 0 && Shows(ClothEditUiItem.WeightFix))
                {
                    number++;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(new GUIContent(L10n.Tr($"{number}. ウェイトが原因の所が {advice.WeightRegions} か所", $"{number}. Caused by weights: {advice.WeightRegions}"),
                            L10n.Tr("服のボーンの割り振り（ウェイト）が体と違うので、外へ逃がしても直らない所です。その所の服に、体のウェイトを写して直します", "Places where the outfit's bone influences (weights) differ from the body's, so moving outward does not help. The body's weights are copied to the outfit there")), EditorStyles.wordWrappedLabel);
                        using (new EditorGUI.DisabledScope(!_controller.CanPreviewWeightFix || _controller.IsBusy))
                        {
                            if (GUILayout.Button(new GUIContent(L10n.Tr("ウェイトを体に寄せる", "Fit Weights to Body"),
                                L10n.Tr("ウェイトが原因の所の服に、体のウェイトを写します。先に候補でもう一度ポーズを試して、前後の件数と伸びを見せてから、適用するかを聞きます（スカートなど体から離れた布が伸びすぎるときは、強さを弱めて試し直せます）。Ctrl+Z の 1 回で戻せます",
                                    "Copies the body's weights to the outfit where the weights cause poke-through. It first tries the poses again with the candidate and shows the counts and stretch before and after, then asks whether to apply (if cloth away from the body, such as a skirt, stretches too much, you can try again with a weaker strength). One Ctrl+Z undoes it")),
                                GUILayout.Width(170f)))
                            {
                                PreviewWeightFixFromWindow(true);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }
                if (advice.NothingToDo && advice.RevertedEscapeRegions > 0)
                {
                    EditorGUILayout.LabelField(L10n.Tr("このままで大丈夫です ✓", "It is fine as it is ✓"), EditorStyles.wordWrappedLabel);
                }
                if (advice.OverlapRegions > 0)
                {
                    number++;
                    EditorGUILayout.LabelField(new GUIContent(L10n.Tr($"{number}. 体どうしの重なり {advice.OverlapRegions} か所: 服では直せません（気にしなくて大丈夫です）",
                            $"{number}. Body overlaps: {advice.OverlapRegions} — these cannot be fixed in the outfit (no need to worry)"),
                        L10n.Tr("腕が腰に入る・ふくらはぎが太ももに入るなど、体の部品どうしが重なる所です。ポーズを見ると紫の点で出ます", "Parts of the body overlap each other there (an arm going into the hip, a calf into the thigh, ...). They show as purple dots when you show the pose")),
                        EditorStyles.wordWrappedLabel);
                }
                if (detailed && result.EscapePlan != null && !stale && !advice.ScopeClear)
                {
                    PoseEscapePlan plan = result.EscapePlan;
                    EditorGUILayout.LabelField(L10n.Tr($"内訳: 元のポーズで外へ逃がせる所 {plan.PushedRegions} か所 / 逃がせない所 {plan.SkippedRegions} か所（関節を深く曲げた所 {plan.LowGainRegions}、外へ逃がすには深すぎる所 {plan.TooFarRegions}、体にぴったりで余裕が無い所 {plan.TightRegions}）",
                        $"Breakdown: can be moved outward in the edit pose {plan.PushedRegions} / cannot {plan.SkippedRegions} (deeply bent joints {plan.LowGainRegions}, too deep to move outward {plan.TooFarRegions}, tight with no room {plan.TightRegions})"), EditorStyles.wordWrappedMiniLabel);
                }
                if (detailed && result.OverlapRegionCount > 0)
                {
                    EditorGUILayout.LabelField(L10n.Tr($"体どうしの重なり（すべてのポーズ）: {result.OverlapRegionCount} か所（{result.OverlapPoseCount} ポーズ）。突き抜けの数にも、直す対象にも入れていません",
                        $"Body overlaps (all poses): {result.OverlapRegionCount} place(s) in {result.OverlapPoseCount} pose(s). Not counted as poke-through and not fixed"), EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        // 前回の「外へ逃がす」の結果（窓に残す。通知は短くしている）
        void DrawLastPoseEscape(bool detailed)
        {
            PoseEscapeReport escape = _controller.LastPoseEscape;
            if (escape == null) return;
            string scope = escape.IncludesLargePoses ? L10n.Tr("すべてのポーズ", "all poses") : L10n.Tr("よく使うポーズ", "everyday poses");
            if (escape.Reverted)
            {
                EditorGUILayout.LabelField(L10n.Tr("前回の外へ逃がす: 突き抜けが減らなかったので、元に戻しました（形は変わっていません）。",
                    "Last move outward: the poke-through did not decrease, so it was undone (the shape is unchanged)."), EditorStyles.wordWrappedMiniLabel);
            }
            else if (escape.Rechecked)
            {
                EditorGUILayout.LabelField(L10n.Tr($"前回の外へ逃がす: {scope}の突き抜け {escape.ScopeRegionsBefore} → {escape.ScopeRegionsAfter} か所（逃がした所の深さの平均 {escape.PushedDepthBefore * 1000f:F1} → {escape.PushedDepthAfter * 1000f:F1} mm）",
                    $"Last move outward: poke-through in {scope} {escape.ScopeRegionsBefore} → {escape.ScopeRegionsAfter} place(s) (average depth at the moved places {escape.PushedDepthBefore * 1000f:F1} → {escape.PushedDepthAfter * 1000f:F1} mm)"), EditorStyles.wordWrappedMiniLabel);
                if (detailed)
                {
                    EditorGUILayout.LabelField(L10n.Tr($"すべてのポーズ: 突き抜けたポーズ {escape.PosesBefore} → {escape.PosesAfter}、か所 {escape.RegionsBefore} → {escape.RegionsAfter}",
                        $"All poses: poses with poke-through {escape.PosesBefore} → {escape.PosesAfter}, places {escape.RegionsBefore} → {escape.RegionsAfter}"), EditorStyles.wordWrappedMiniLabel);
                }
                if (escape.Escape != null && escape.Escape.Limited > 0)
                {
                    EditorGUILayout.LabelField(L10n.Tr($"布に余裕が無い所（{escape.Escape.LimitedClusters} か所）は、布が {PoseEscapeOptions.DefaultMaxStretch * 100f:F0}% より伸びる・しわが寄る手前で止めました。",
                        $"Where the cloth has no room ({escape.Escape.LimitedClusters} place(s)), it stopped before stretching more than {PoseEscapeOptions.DefaultMaxStretch * 100f:F0}% or wrinkling."), EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        // くわしい説明（折りたたみ）。かんたん表示の要点の、長い説明をここに移した
        void DrawPoseExplanation(PoseCheckResult result)
        {
            _foldPoseExplain = EditorGUILayout.Foldout(_foldPoseExplain, L10n.Tr("くわしい説明", "More explanation"), true);
            if (!_foldPoseExplain) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(L10n.Tr("か所の数え方: 同じ所でも、ポーズ（途中まで・最後まで）ごとに1か所と数えます。「途中まで」はポーズの半分まで動かしたところ、「最後まで」はポーズの最後まで動かしたところです。",
                "How places are counted: the same spot counts once per pose and stage. \"Halfway\" is the pose half done, \"full\" is the whole pose."), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(L10n.Tr("浅い所: 編集の姿勢で外へ少し（2cm まで）動かせば、そのポーズで突き抜けなくなる所です。「元のポーズで外へ逃がす」で直します。効かなかったときは自動で元に戻します。",
                "Shallow places: places that stop poking through in the pose if moved a little (up to 2 cm) outward in the edit pose. Fix them with \"Move Outward in Edit Pose\"; it is undone automatically if it does not help."), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(L10n.Tr("ウェイトが原因の所: 服のボーンの割り振り（ウェイト）が体と違うので、外へ逃がしても直らない所です。ひざ・ひじを深く曲げた所、ゆったりしたスカートが座ったときに太ももに抜かれる所、タイツなど体にぴったりで布に余裕の無い所などです。「ウェイトを体に寄せる」で、その所の服に体のウェイトを写して直します（適用の前に、前後の件数を見せて聞きます）。",
                "Caused by weights: the outfit's bone influences (weights) differ from the body's, so moving outward does not help. For example deeply bent knees and elbows, thighs poking through a loose skirt when sitting, and tight places with no room in the cloth such as tights. \"Fit Weights to Body\" copies the body's weights to the outfit there (it shows the counts before and after and asks before applying)."), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(L10n.Tr("体どうしの重なり: 腕が腰に入る・ふくらはぎが太ももに入る・お辞儀でおなかが折れ込むなど、体の部品どうしが重なる所です。服がぴったり付いていっても突き抜けるので、服では直せません。突き抜けの数にも、直す対象にも入れていません（ポーズを見ると紫の点で出ます）。",
                "Body overlaps: parts of the body overlap each other (an arm going into the hip, a calf into the thigh, the belly folding when bowing, ...). The outfit pokes through even if it follows the body exactly, so it cannot be fixed in the outfit. They are not counted as poke-through and not fixed (purple dots when you show the pose)."), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(L10n.Tr("大きく動くポーズ（座る・しゃがむ・足を上げる・開く）: スカートなどが太ももに抜かれるのは普通です。VRChat では揺れもの（PhysBone）で動かすことが多いです。直したいときは「大きく動くポーズも直す」をオンにします（服が大きく膨らんだり、脚に貼り付いたりすることがあります）。",
                "Big-motion poses (sit, squat, raise a leg, legs apart): thighs poking through a skirt is normal there. In VRChat skirts are often moved by PhysBones. To fix them anyway, turn on \"Also fix big-motion poses\" (the outfit may bulge or stick to the legs)."), EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;
        }

        void DrawPoseCheckDetails(PoseCheckResult result)
        {
            EditorGUILayout.Space(2f);
            bool running = _controller.IsPoseCheckRunning;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(running))
            {
                int enabled = _settings.GetEnabledPoses().Count;
                _foldPoseList = EditorGUILayout.Foldout(_foldPoseList, L10n.Tr($"試すポーズ（{enabled} / {PoseLibrary.All.Count}）", $"Poses to Try ({enabled} / {PoseLibrary.All.Count})"), true);
                if (_foldPoseList)
                {
                    IReadOnlyList<PosePreset> all = PoseLibrary.All;
                    foreach (PoseCategory category in new[] { PoseCategory.Common, PoseCategory.Large })
                    {
                        EditorGUILayout.LabelField(category == PoseCategory.Large ? PoseLibrary.LargeMotionLabel : PoseLibrary.CommonLabel, EditorStyles.miniBoldLabel);
                        var inCategory = new List<PosePreset>();
                        foreach (PosePreset preset in all)
                        {
                            if (preset.Category == category) inCategory.Add(preset);
                        }
                        for (int i = 0; i < inCategory.Count; i += 2)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                for (int k = i; k < i + 2 && k < inCategory.Count; k++)
                                {
                                    bool on = EditorGUILayout.ToggleLeft(inCategory[k].Name, _settings.IsPoseEnabled(inCategory[k].Id), GUILayout.MinWidth(120f));
                                    if (on != _settings.IsPoseEnabled(inCategory[k].Id)) _settings.SetPoseEnabled(inCategory[k].Id, on);
                                }
                            }
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(L10n.Tr("すべて選ぶ", "Select All"))) _settings.PoseCheckDisabled = "";
                        if (GUILayout.Button(L10n.Tr("すべて外す", "Select None")))
                        {
                            foreach (PosePreset preset in PoseLibrary.All) _settings.SetPoseEnabled(preset.Id, false);
                        }
                    }
                }
                _settings.PoseCheckBothStages = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("途中までと最後までの2段階で試す", "Try Halfway and Full"),
                    L10n.Tr("オンにすると、各ポーズを「途中まで」（半分まで動かしたところ）と「最後まで」の2段階で試します（途中の姿勢でだけ突き抜ける所も見つかります）。オフにすると「最後まで」だけで、半分の時間で済みます",
                        "When on, each pose is tried halfway and at full (also finds places that poke through only partway). When off, only the full pose is tried, in half the time")),
                    _settings.PoseCheckBothStages);
                float toleranceMm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("許容の深さ（mm）", "Tolerance (mm)"),
                    L10n.Tr("これより浅い突き抜けは数えません。既定 1mm", "Poke-through shallower than this is not counted. Default 1 mm")),
                    _settings.PoseCheckTolerance * 1000f, 0f, ClothEditSettings.MaxPoseCheckTolerance * 1000f);
                _settings.PoseCheckTolerance = toleranceMm * 0.001f;
                _settings.ShowPoseCheckDepth = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("結果が出たら色の点を出す", "Show the Colored Dots After a Check"),
                    L10n.Tr("オンにすると、動作チェックの結果が出たときに、突き抜ける所の色の点を出します（ほかの操作を始めると消えます）。オフでも、区切りの「突き抜ける所を色の点で示す」で出せます",
                        "When on, the colored dots for the poke-through appear when a check finishes (they disappear when you start another operation). Even when off, \"Show the poke-through as colored dots\" shows them")),
                    _settings.ShowPoseCheckDepth);
            }
            if (EditorGUI.EndChangeCheck()) _settingsDirty = true;
            if (Shows(ClothEditUiItem.PoseCheckClips)) DrawPoseCheckClips(result, running);

            if (result == null || running) return;
            _foldPoseEntries = EditorGUILayout.Foldout(_foldPoseEntries, L10n.Tr("ポーズごとの結果", "Result per Pose"), true);
            if (_foldPoseEntries)
            {
                for (int k = 0; k < result.Entries.Count; k++)
                {
                    PoseCheckEntry entry = result.Entries[k];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string name = entry.IsLargeMotion ? entry.Name + L10n.Tr("［大］", " [big]") : entry.Name;
                        GUILayout.Label(new GUIContent(name, entry.IsLargeMotion ? L10n.Tr("大きく動くポーズ", "Big-motion pose") : ""), GUILayout.MinWidth(150f));
                        GUILayout.Label(entry.Describe(), entry.Result.IsPenetrating ? EditorStyles.boldLabel : EditorStyles.label);
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(!_controller.CanPreviewPoses))
                        {
                            if (GUILayout.Button(L10n.Tr("見る", "Show"), GUILayout.Width(44f)))
                            {
                                _controller.ShowPoseEntry(k);
                                SceneView.RepaintAll();
                            }
                        }
                    }
                }
            }
            EditorGUILayout.LabelField(result.BodyDescription, EditorStyles.wordWrappedMiniLabel);
            if (result.FollowerCount > 0 || result.UnmatchedBoneCount > 0)
            {
                EditorGUILayout.LabelField(L10n.Tr($"服の別のアーマチュアのボーン: アバターのボーンの動きをうつす {result.FollowerCount}、対応が無く親に付いていく {result.UnmatchedBoneCount}",
                    $"Bones of the outfit's own armature: {result.FollowerCount} follow avatar bones, {result.UnmatchedBoneCount} have no match and follow their parent"), EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.LabelField(L10n.Tr($"かかった時間: {result.Milliseconds / 1000.0:F1} 秒", $"Time: {result.Milliseconds / 1000.0:F1} s"), EditorStyles.miniLabel);
        }

        void EscapePoseCheckFromWindow()
        {
            PoseEscapeReport report = _controller.EscapePoseCheckPenetration();
            if (report != null)
            {
                // 窓の真ん中の通知は短く（詳しい前後は、窓の結果の欄に残る）
                string message = report.Escape != null && report.Escape.Cancelled ? L10n.Tr("途中でやめました", "Stopped partway")
                    : report.Reverted ? L10n.Tr("元に戻しました（効果なし）", "Undone (no effect)")
                    : L10n.Tr("外へ逃がしました", "Moved outward");
                Toast(message);
            }
            _diagnosticsAt = double.NegativeInfinity;
            SceneView.RepaintAll();
        }

        // ---- シーンビュー ----

        // シーンビューの重ね描き（DrawOverlay から呼ぶ）。ポーズを見せている間は、そのポーズの赤い点だけを描いて true（ほかの重ね描きは編集の姿勢の位置なので描かない）。
        // 編集の姿勢では、直す範囲のポーズでの最悪の深さで点を色分けして false（ほかの操作を始めたら消える。区切りのトグルで出し直せる）
        bool DrawPoseCheckOverlay(ClothEditSession session)
        {
            PoseCheckEntry shown = _controller.PosePreview;
            if (shown != null)
            {
                // 体どうしの重なり（服では直せない所）は紫、突き抜けた所は赤（白い縁）
                DrawPoseDots(shown.Result.OverlapPoints, PoseOverlapColor);
                DrawPoseDots(shown.Result.Points, PenetrationColor);
                return true;
            }

            if (!_controller.IsPoseCheckDepthShown) return false;
            float[] depth = _controller.PoseCheckParticleDepth;
            if (depth == null) return false;
            if (_poseDepthSource != depth)
            {
                // 結果が変わったときだけ、色分けする点を選び直す（深いものを優先して上限までに間引く）
                _poseDepthSource = depth;
                _poseDepthParticles.Clear();
                int count = 0;
                for (int i = 0; i < depth.Length; i++)
                {
                    if (depth[i] > 0f) count++;
                }
                int stride = count <= MaxPoseDepthDots ? 1 : (count + MaxPoseDepthDots - 1) / MaxPoseDepthDots;
                int seen = 0;
                for (int i = 0; i < depth.Length; i++)
                {
                    if (!(depth[i] > 0f) || seen++ % stride != 0) continue;
                    _poseDepthParticles.Add(i);
                }
            }
            System.ReadOnlySpan<System.Numerics.Vector3> positions = session.Solver.Positions;
            for (int k = 0; k < _poseDepthParticles.Count; k++)
            {
                int i = _poseDepthParticles[k];
                if ((uint)i >= (uint)positions.Length) continue;
                Handles.color = PoseDepthColor(depth[i]);
                Dot(new Vector3(positions[i].X, positions[i].Y, positions[i].Z), 0.009f + 0.006f * Mathf.Clamp01(depth[i] / PoseDepthRed));
            }
            return false;
        }

        void DrawPoseDots(List<System.Numerics.Vector3> points, Color color)
        {
            if (points == null || points.Count == 0) return;
            int stride = points.Count <= MaxPoseDots ? 1 : (points.Count + MaxPoseDots - 1) / MaxPoseDots;
            _poseDots.Clear();
            for (int k = 0; k < points.Count; k += stride) _poseDots.Add(new Vector3(points[k].X, points[k].Y, points[k].Z));
            Handles.color = Color.white;
            for (int i = 0; i < _poseDots.Count; i++) Dot(_poseDots[i], 0.022f);
            Handles.color = color;
            for (int i = 0; i < _poseDots.Count; i++) Dot(_poseDots[i], 0.015f);
        }

        // シーンビューでクリックしたとき（OnSceneGUI の MouseDown から呼ぶ）。ポーズを見せていれば元の姿勢に戻して、そのクリックは使う
        bool EndPosePreviewOnClick(Event e)
        {
            if (!_controller.IsPosePreviewActive) return false;
            _controller.EndPosePreview();
            e.Use();
            Repaint();
            return true;
        }

        // シーンビューで Esc を押したとき。ポーズを見せていれば元の姿勢に戻す
        bool EndPosePreviewOnEscape(Event e)
        {
            if (!_controller.IsPosePreviewActive) return false;
            _controller.EndPosePreview();
            e.Use();
            Repaint();
            return true;
        }

        // シーンビューの左上の案内に足す1行（ポーズを見せている間・調べている間）
        string PoseCheckHint()
        {
            if (_controller.IsPoseCheckRunning)
            {
                _controller.GetPoseCheckCounts(out int done, out int total);
                return "\n" + L10n.Tr($"動作チェック中… {done} / {total}", $"Motion check... {done} / {total}");
            }
            PoseCheckEntry shown = _controller.PosePreview;
            if (shown == null) return "";
            string overlap = shown.Result != null && shown.Result.HasOverlap
                ? L10n.Tr("。紫の点は体どうしの重なり（服では直せない所）", ". Purple dots are body overlaps (cannot be fixed in the outfit)")
                : "";
            return "\n" + L10n.Tr($"ポーズを表示中: {shown.Name}（{shown.Describe()}）{overlap}。クリックか Esc で元の姿勢に戻ります",
                $"Showing pose: {shown.Name} ({shown.Describe()}){overlap}. Click or press Esc to return to the edit pose");
        }
    }
}
