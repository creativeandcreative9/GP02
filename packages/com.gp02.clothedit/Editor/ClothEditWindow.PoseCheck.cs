using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 動作チェックの画面: ウィンドウの「動作チェック」の区切り（始める・やめる・結果・順に見る・順に再生・外へ逃がす、くわしく表示ではポーズの一覧など）と、
    // シーンビューの重ね描き（ポーズを見せている間は、そのポーズで突き抜けた所の赤い点。編集の姿勢では、全ポーズでの最悪の深さで粒子を色分け）。
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

        readonly List<Vector3> _poseDots = new List<Vector3>();
        readonly List<int> _poseDepthParticles = new List<int>();
        float[] _poseDepthSource;          // _poseDepthParticles を作ったときの深さの配列（変わったら作り直す）
        GUIStyle _poseEntryStyle;

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

        void DrawPoseCheckResult(PoseCheckResult result)
        {
            bool stale = _controller.IsPoseCheckStale;
            if (stale)
            {
                EditorGUILayout.HelpBox(L10n.Tr("形が変わりました。もう一度チェックしてください。", "The shape has changed. Run the check again."), MessageType.Warning);
            }
            EditorGUILayout.LabelField(result.DescribeSummary(), EditorStyles.wordWrappedLabel);
            if (!result.HasBody) return;

            if (_poseEntryStyle == null) _poseEntryStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
            int[] ranked = result.RankEntries();
            for (int k = 0; k < ranked.Length && k < 3; k++)
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
            if (ranked.Length > 3) EditorGUILayout.LabelField(L10n.Tr($"ほか {ranked.Length - 3} 件（くわしく表示で一覧）", $"{ranked.Length - 3} more (list in the detailed view)"), EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_controller.CanPreviewPoses || ranked.Length == 0))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("順に見る", "Next Problem"), L10n.Tr("突き抜けたポーズを、悪い順に1つずつシーンで見せます", "Shows the poses with penetration in the scene, worst first, one at a time"))))
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
                    $"Showing: {shown.Name} — {shown.Describe()} (red dots mark the penetration" + (shown.Result.HasOverlap ? ", purple dots the body overlaps" : "") + ")"), EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUI.BeginChangeCheck();
            bool showDepth = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("最悪の深さで色分け（編集の姿勢）", "Color by Worst Depth (edit pose)"),
                L10n.Tr("全ポーズの中で一番深く突き抜けた深さで、服の粒子を色分けします（黄 1mm → 赤 1cm 以上）。どこを直せばよいかが、ふだんの編集の画面で分かります",
                    "Colors the outfit's particles by the deepest penetration over all poses (yellow 1 mm to red 1 cm or more), so you can see where to fix in the normal edit view")),
                _settings.ShowPoseCheckDepth);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.ShowPoseCheckDepth = showDepth;
                _settingsDirty = true;
                SceneView.RepaintAll();
            }

            if (result.Baseline != null && result.Baseline.IsPenetrating)
            {
                EditorGUILayout.HelpBox(L10n.Tr($"今の姿勢でも、体が服を突き抜けている所があります（{result.Baseline.RegionCount} か所）。ポーズの結果からは除いてあります。診断の「貫通を直す」で直せます。",
                    $"The body already pokes through the outfit in the current pose ({result.Baseline.RegionCount} place(s)). These are left out of the pose results. Use \"Fix Penetration\" in the diagnostics."), MessageType.Info);
            }

            if (result.PenetratingPoseCount > 0)
            {
                PoseEscapePlan plan = result.EscapePlan;
                if (plan != null && !stale)
                {
                    EditorGUILayout.LabelField(L10n.Tr($"元のポーズで外へ逃がせる所: {plan.PushedRegions} か所 / 逃がせない所: {plan.SkippedRegions} か所（ウェイトが原因 {plan.LowGainRegions}、深すぎる {plan.TooFarRegions}、体にぴったりで余裕が無い {plan.TightRegions}）",
                        $"Places that can be moved outward in the edit pose: {plan.PushedRegions} / cannot: {plan.SkippedRegions} (caused by weights {plan.LowGainRegions}, too deep {plan.TooFarRegions}, tight with no room {plan.TightRegions})"), EditorStyles.wordWrappedMiniLabel);
                }
                using (new EditorGUI.DisabledScope(!_controller.CanEscapePoseCheck || _controller.IsBusy))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("元のポーズで外へ逃がす", "Move Outward in Edit Pose"),
                        L10n.Tr("ポーズで突き抜けた所の布を、編集の姿勢で、そのポーズで足りるだけ外へ動かします（布の長さを保って周りも付いてきます）。終わったら、もう一度チェックして前後の件数を出します。Ctrl+Z で戻せます（1回で1段）",
                            "Moves the cloth at those places outward in the edit pose, just enough for the pose (keeping the cloth length; the surroundings follow), then checks again and shows the counts before and after. Ctrl+Z undoes it (one step)"))))
                    {
                        EscapePoseCheckFromWindow();
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUILayout.LabelField(L10n.Tr("ウェイト（服と体のボーンの割り振りの違い）が原因の貫通は、外へ逃がしても直りません。編集の姿勢で動かしてもポーズではほとんど離れない所（ひざ・ひじを深く曲げたときなど）、深すぎる所（ゆったりしたスカートが座ったときに太ももに抜かれる、など）、体にぴったりで布に余裕の無い所（タイツ・腰回りなど）は、動かさずに残します。それは下の「ウェイトを体に寄せる」で直します。",
                    "Penetration caused by weights (different bone influences on the outfit and the body) is not fixed by moving outward. Places that barely move away in the pose even if moved in the edit pose (deeply bent knees and elbows, ...), places that are too deep (thighs poking through a loose skirt when sitting, ...) and tight places with no room in the cloth (tights, around the waist, ...) are left as they are. Fix those with \"Fit Weights to Body\" below."), EditorStyles.wordWrappedMiniLabel);
            }
            if (result.OverlapRegionCount > 0 && !stale)
            {
                EditorGUILayout.HelpBox(L10n.Tr($"体どうしの重なり: {result.OverlapRegionCount} か所（{result.OverlapPoseCount} ポーズ）。体どうしの重なりで、服では直せません。腕が腰に入る・ふくらはぎが太ももに入るなど、体の部品どうしが重なる所で、服がぴったり付いていっても突き抜けます（貫通の数にも、直す対象にも入れていません。ポーズを見ると紫の点で出ます）。",
                    $"Body overlaps: {result.OverlapRegionCount} place(s) in {result.OverlapPoseCount} pose(s). These are overlaps of the body itself and cannot be fixed in the outfit. Parts of the body overlap each other there (an arm going into the hip, a calf going into the thigh, ...), so the outfit pokes through even if it follows the body exactly (not counted as penetration and not fixed; purple dots when you show the pose)."), MessageType.None);
            }
            DrawWeightFixSection(result, stale);
            PoseEscapeReport escape = _controller.LastPoseEscape;
            if (escape != null && escape.Reverted)
            {
                EditorGUILayout.HelpBox(L10n.Tr("前回の外へ逃がす: 逃がしても、ポーズでの貫通が減らなかったので、元に戻しました。布に余裕が無い（ぴったりの服）か、ウェイトが原因です。ウェイトが原因の所は「ウェイトを体に寄せる」で直せます。",
                    "Last move outward: it did not reduce the penetration in the poses, so it was undone. The cloth has no room (a tight outfit), or the weights are the cause. Places caused by weights can be fixed with \"Fit Weights to Body\"."), MessageType.Info);
            }
            else if (escape != null && escape.Rechecked)
            {
                EditorGUILayout.LabelField(L10n.Tr($"前回の外へ逃がす: 逃がした所の深さ（平均） {escape.PushedDepthBefore * 1000f:F1} → {escape.PushedDepthAfter * 1000f:F1} mm、貫通したポーズ {escape.PosesBefore} → {escape.PosesAfter}、か所 {escape.RegionsBefore} → {escape.RegionsAfter}",
                    $"Last move outward: depth at the moved places (average) {escape.PushedDepthBefore * 1000f:F1} → {escape.PushedDepthAfter * 1000f:F1} mm, poses with penetration {escape.PosesBefore} → {escape.PosesAfter}, places {escape.RegionsBefore} → {escape.RegionsAfter}"), EditorStyles.wordWrappedMiniLabel);
                if (escape.Escape != null && escape.Escape.Limited > 0)
                {
                    EditorGUILayout.LabelField(L10n.Tr($"布に余裕が無い所（{escape.Escape.LimitedClusters} か所）は、布が {PoseEscapeOptions.DefaultMaxStretch * 100f:F0}% より伸びる・しわが寄る手前で止めました。",
                        $"Where the cloth has no room ({escape.Escape.LimitedClusters} place(s)), it stopped before stretching more than {PoseEscapeOptions.DefaultMaxStretch * 100f:F0}% or wrinkling."), EditorStyles.wordWrappedMiniLabel);
                }
            }
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
                    for (int i = 0; i < all.Count; i += 2)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            for (int k = i; k < i + 2 && k < all.Count; k++)
                            {
                                bool on = EditorGUILayout.ToggleLeft(all[k].Name, _settings.IsPoseEnabled(all[k].Id), GUILayout.MinWidth(120f));
                                if (on != _settings.IsPoseEnabled(all[k].Id)) _settings.SetPoseEnabled(all[k].Id, on);
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
                _settings.PoseCheckBothStages = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("半分と全部の2段階で試す", "Try Half and Full"),
                    L10n.Tr("オンにすると、各ポーズを「半分」と「全部」の2段階で試します（途中の姿勢でだけ突き抜ける所も見つかります）。オフにすると「全部」だけで、半分の時間で済みます",
                        "When on, each pose is tried at half and at full (also finds places that poke through only partway). When off, only the full pose is tried, in half the time")),
                    _settings.PoseCheckBothStages);
                float toleranceMm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("許容の深さ（mm）", "Tolerance (mm)"),
                    L10n.Tr("これより浅い突き抜けは数えません。既定 1mm", "Penetration shallower than this is not counted. Default 1 mm")),
                    _settings.PoseCheckTolerance * 1000f, 0f, ClothEditSettings.MaxPoseCheckTolerance * 1000f);
                _settings.PoseCheckTolerance = toleranceMm * 0.001f;
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
                        GUILayout.Label(entry.Name, GUILayout.MinWidth(150f));
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
                string message = report.Reverted
                    ? L10n.Tr("逃がしても貫通が減らなかったので、元に戻しました", "Moving outward did not reduce the penetration, so it was undone")
                    : report.Rechecked
                        ? L10n.Tr($"外へ逃がしました: 逃がした所の深さ（平均） {report.PushedDepthBefore * 1000f:F1} → {report.PushedDepthAfter * 1000f:F1} mm\n貫通したポーズ {report.PosesBefore} → {report.PosesAfter}、か所 {report.RegionsBefore} → {report.RegionsAfter}",
                            $"Moved outward: depth at the moved places (average) {report.PushedDepthBefore * 1000f:F1} → {report.PushedDepthAfter * 1000f:F1} mm\nposes with penetration {report.PosesBefore} → {report.PosesAfter}, places {report.RegionsBefore} → {report.RegionsAfter}")
                        : L10n.Tr("外へ逃がしました（もう一度チェックできませんでした）", "Moved outward (could not check again)");
                if (report.Escape != null && report.Escape.Cancelled) message += L10n.Tr("（途中でやめました）", " (cancelled)");
                ShowNotification(new GUIContent(message));
            }
            _diagnosticsAt = double.NegativeInfinity;
            SceneView.RepaintAll();
        }

        // ---- シーンビュー ----

        // シーンビューの重ね描き（DrawOverlay から呼ぶ）。ポーズを見せている間は、そのポーズの赤い点だけを描いて true（ほかの重ね描きは編集の姿勢の位置なので描かない）。
        // 編集の姿勢では、最悪の深さで粒子を色分けして false
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

            float[] depth = _controller.PoseCheckParticleDepth;
            if (depth == null || !_settings.ShowPoseCheckDepth) return false;
            if (_poseDepthSource != depth)
            {
                // 結果が変わったときだけ、色分けする粒子を選び直す（深いものを優先して上限までに間引く）
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
