using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 動作チェックの第2段の画面: 「ウェイトを体に寄せる」と、手持ちの AnimationClip の欄（くわしく表示）。
    // かんたん表示では、ボタンは「おすすめの直し方」の行に出し（押すと確認の前後を出してから適用）、ここには確認した候補と前回の結果だけを出す。
    // くわしく表示では、ここに強さ・半径・確認・適用・候補を捨てる・データから外すを出す
    public sealed partial class ClothEditWindow
    {
        [SerializeField] bool _foldPoseClips;

        // くわしく表示か。強さのスライダーはくわしく表示にしか無いので、候補の説明の「和らげ方」の一言が、これで変わる
        static bool ShowsWeightFixDetails => Shows(ClothEditUiItem.WeightFixDetails);

        void DrawWeightFixSection(PoseCheckResult result, bool stale)
        {
            if (!Shows(ClothEditUiItem.WeightFix)) return;
            bool detailed = ShowsWeightFixDetails;
            int weightRegions = _controller.WeightCausedRegionCount;
            WeightFixPreview preview = _controller.WeightFixPreview;
            WeightFixReport last = _controller.LastWeightFix;
            int recorded = detailed ? _controller.RecordedWeightEditVertexCount : 0;
            if (weightRegions == 0 && preview == null && last == null && recorded == 0) return;

            if (!detailed)
            {
                // かんたん表示: ボタンは「おすすめの直し方」にあるので、確認した候補（ダイアログでやめたとき）と前回の結果だけ
                DrawWeightFixCandidate(preview, false);
                DrawLastWeightFix(last);
                if (!string.IsNullOrEmpty(_controller.LastWeightFixError)) EditorGUILayout.HelpBox(_controller.LastWeightFixError, MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(L10n.Tr("ウェイトを体に寄せる", "Fit Weights to Body"), EditorStyles.boldLabel);
            if (weightRegions > 0 && result.WeightPlan != null)
            {
                WeightFixPlan plan = result.WeightPlan;
                EditorGUILayout.LabelField(L10n.Tr($"ウェイトが原因の所: {weightRegions} か所（関節を深く曲げた所 {plan.LowGainRegions}、外へ逃がすには深すぎる所 {plan.TooFarRegions}、体にぴったりで逃がす余裕の無い所 {plan.TightRegions}）。" +
                    "服のボーンの割り振り（ウェイト）が体と違うので、外へ逃がしても直りません。その所の服に、体のウェイトを写して直します。",
                    $"Places caused by weights: {weightRegions} (deeply bent joints {plan.LowGainRegions}, too deep to move outward {plan.TooFarRegions}, tight with no room to move outward {plan.TightRegions}). " +
                    "The outfit's bone influences (weights) differ from the body's, so moving outward does not fix them. The body's weights are copied to the outfit there."), EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUI.BeginChangeCheck();
            float strength = EditorGUILayout.Slider(new GUIContent(L10n.Tr("強さ（%）", "Strength (%)"),
                L10n.Tr("元のウェイトと体のウェイトを混ぜる割合。100% なら体のウェイトそのもの（体にぴったり付いていく。タイツ・袖向き）。スカートを太ももに強く寄せると、座ったときに布が脚に貼り付いて伸びることがあるので、確認の「体から離れた所の布の伸び」を見て下げてください",
                    "How much of the body's weights to mix in. 100% uses the body's weights as they are (follows the body exactly; suits tights and sleeves). Pulling a skirt strongly to the thighs can make the cloth stick to the legs and stretch when sitting, so lower it while watching \"stretch of the cloth away from the body\" in the check")),
                _settings.WeightFixStrength * 100f, 0f, 100f);
            float radius = EditorGUILayout.Slider(new GUIContent(L10n.Tr("周りへの広がり（cm）", "Feather (cm)"),
                L10n.Tr("寄せる所から周りへ、混ぜる割合をなめらかに減らす半径（布に沿って測ります）。小さいと、境目でウェイトが急に変わって段が出ることがあります",
                    "Radius over which the mix fades out around the fixed places (measured along the cloth). Too small can leave a step where the weights change suddenly")),
                _settings.WeightFixRadius * 100f, 0f, ClothEditSettings.MaxWeightFixRadius * 100f);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.WeightFixStrength = strength * 0.01f;
                _settings.WeightFixRadius = radius * 0.01f;
                _settingsDirty = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_controller.CanPreviewWeightFix || _controller.IsBusy))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("確認（試す）", "Check (Try)"),
                        L10n.Tr("体のウェイトを写した候補で、もう一度ポーズを試して、前後の件数・深さ・伸びを出します。シーンもアセットも、まだ変えません",
                            "Tries the poses again with the candidate weights and shows the counts, depth and stretch before and after. Nothing in the scene or the assets changes yet"))))
                    {
                        PreviewWeightFixFromWindow(false);
                        GUIUtility.ExitGUI();
                    }
                }
                using (new EditorGUI.DisabledScope(!_controller.CanApplyWeightFix || _controller.IsBusy))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("適用", "Apply"),
                        L10n.Tr("確認した候補のウェイトを、衣装のメッシュに入れ、シェイプキーのデータにも記録します（シェイプキーの一覧の「メッシュに反映」とアップロード時の適用でも入ります）。Ctrl+Z の 1 回で戻せます",
                            "Puts the checked candidate weights into the outfit's mesh and records them in the shape key data (also used by \"Apply to Mesh\" in the shape key list and at upload). One Ctrl+Z undoes it"))))
                    {
                        ApplyWeightFixFromWindow();
                        GUIUtility.ExitGUI();
                    }
                }
                using (new EditorGUI.DisabledScope(preview == null))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("候補を捨てる", "Discard"), L10n.Tr("確認した候補を捨てます（何も変えません）", "Discards the checked candidate (nothing changes)")), GUILayout.Width(90f)))
                    {
                        _controller.DiscardWeightFixPreview();
                    }
                }
            }

            DrawWeightFixCandidate(preview, true);
            DrawLastWeightFix(last);
            if (recorded > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(L10n.Tr($"データに記録したウェイトの変更: {recorded} 頂点", $"Weight change recorded in the data: {recorded} vertices"), EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUI.DisabledScope(_controller.IsBusy))
                    {
                        if (GUILayout.Button(new GUIContent(L10n.Tr("データから外す", "Remove"),
                            L10n.Tr("記録したウェイトの変更を外し、衣装のメッシュのウェイトを元に戻します（足したボーンも外します）。Ctrl+Z で戻せます",
                                "Removes the recorded weight change and returns the outfit's mesh to the original weights (added bones are removed too). Ctrl+Z undoes it")), GUILayout.Width(110f)))
                        {
                            RemoveWeightFixFromWindow();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(_controller.LastWeightFixError)) EditorGUILayout.HelpBox(_controller.LastWeightFixError, MessageType.Warning);
        }

        // 確認した候補（前後の数）。かんたん表示では「この候補を適用」も出す（確認のダイアログでやめたあとに、あとから適用できるように）
        void DrawWeightFixCandidate(WeightFixPreview preview, bool detailed)
        {
            if (preview == null) return;
            if (_controller.IsWeightFixPreviewStale)
            {
                EditorGUILayout.HelpBox(L10n.Tr("確認のあとに形が変わったので、この候補は適用できません。もう一度確認してください。", "The shape changed after the check, so this candidate cannot be applied. Check again."), MessageType.Warning);
                return;
            }
            EditorGUILayout.LabelField(L10n.Tr("ウェイトを体に寄せる候補: ", "Candidate for Fit Weights to Body: ") + preview.Describe(detailed), EditorStyles.wordWrappedMiniLabel);
            if (detailed) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_controller.CanApplyWeightFix || _controller.IsBusy))
                {
                    if (GUILayout.Button(L10n.Tr("この候補を適用", "Apply This Candidate")))
                    {
                        ApplyWeightFixFromWindow();
                        GUIUtility.ExitGUI();
                    }
                }
                // 確認のダイアログでやめたあとも、弱めて試せるように（候補の説明が「弱めて試す」を勧めるので、そのボタンを窓にも置く）
                if (preview.CanTryWeaker)
                {
                    using (new EditorGUI.DisabledScope(!_controller.CanPreviewWeightFix || _controller.IsBusy))
                    {
                        if (GUILayout.Button(new GUIContent(ClothEditPrompts.WeightFixButtonLabel(WeightFixChoice.TryWeaker),
                            L10n.Tr($"強さを {WeightFixPreview.WeakerPercent}% にして、もう一度候補を作って試し、前後を見せてから適用するかを聞きます（設定の強さは変えません）",
                                $"Makes the candidate again at {WeightFixPreview.WeakerPercent}% strength, tries the poses, shows before and after, and asks whether to apply (the Strength setting is not changed)"))))
                        {
                            TryWeakerWeightFix();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("やめる", "Discard"), L10n.Tr("確認した候補を捨てます（何も変えません）", "Discards the checked candidate (nothing changes)")), GUILayout.Width(70f)))
                {
                    _controller.DiscardWeightFixPreview();
                }
            }
        }

        // 前回の「ウェイトを体に寄せる」の結果（窓に残す。通知は短くしている）
        void DrawLastWeightFix(WeightFixReport last)
        {
            if (last == null || last.Preview == null) return;
            WeightFixPreview p = last.Preview;
            EditorGUILayout.LabelField(L10n.Tr($"前回のウェイトを体に寄せる: 頂点 {last.ChangedVertices}" + (p.IsBelowFullStrength ? $"、強さ {p.StrengthPercent}%" : "") +
                    $"、{p.ScopeLabel}の突き抜け {p.RegionsBefore} → {p.RegionsAfter} か所（{p.PosesBefore} → {p.PosesAfter} ポーズ）" +
                    (last.DiscardedUnsaved ? "（保存していない形を戻したので、もう一度チェックしてください）" : ""),
                $"Last weight fit: {last.ChangedVertices} vertices" + (p.IsBelowFullStrength ? $", strength {p.StrengthPercent}%" : "") +
                    $", poke-through in {p.ScopeLabel} {p.RegionsBefore} → {p.RegionsAfter} place(s) ({p.PosesBefore} → {p.PosesAfter} poses)" +
                    (last.DiscardedUnsaved ? " (the unsaved shape was reset, so run the check again)" : "")), EditorStyles.wordWrappedMiniLabel);
        }

        // 候補を作って調べ直す。ask なら、前後を見せて適用するかを聞く（おすすめの直し方のボタン。くわしく表示の「確認（試す）」は聞かない）
        internal void PreviewWeightFixFromWindow(bool ask)
        {
            WeightFixPreview preview = _controller.PreviewWeightFix();
            SceneView.RepaintAll();
            if (preview == null)
            {
                EditorUtility.DisplayDialog("GP02", _controller.LastWeightFixError, "OK");
                return;
            }
            if (ask) OfferWeightFix(preview);
        }

        // 確認した候補の前後を見せて、適用するかを聞く。体から離れた布が伸びすぎて、まだ強さを弱められるときは、
        // 「弱めて試す」も選べる（強さはくわしく表示にしか無いので、かんたん表示のまま弱められるように）
        internal void OfferWeightFix(WeightFixPreview preview)
        {
            if (!preview.CanTryWeaker)
            {
                AskApplyWeightFix(ClothEditPrompts.WeightFixApplyMessage(preview.Describe(ShowsWeightFixDetails)));
                return;
            }
            switch (ClothEditPrompts.ChooseWeightFix(ClothEditPrompts.WeightFixOfferMessage(preview, ShowsWeightFixDetails)))
            {
                case WeightFixChoice.TryWeaker:
                    TryWeakerWeightFix();
                    break;
                case WeightFixChoice.ApplyAsIs:
                    ApplyWeightFixFromWindow();
                    break;
            }
        }

        // 強さを弱めて候補を作り直し、前後を見せて、適用するかを聞く。設定の強さは変えない（次の衣装では、設定の強さのままにする）。
        // やめたときは、弱めた候補が窓に残る（「この候補を適用」であとから適用できる）
        void TryWeakerWeightFix()
        {
            WeightFixPreview weaker = _controller.PreviewWeightFix(WeightFixPreview.WeakerStrength);
            SceneView.RepaintAll();
            if (weaker == null)
            {
                EditorUtility.DisplayDialog("GP02", _controller.LastWeightFixError, "OK");
                return;
            }
            AskApplyWeightFix(ClothEditPrompts.WeightFixWeakerResultMessage(weaker, ShowsWeightFixDetails));
        }

        // 適用するかを聞いて、適用するなら適用する（message は、候補の説明に「この候補を適用しますか？」を添えたもの）
        void AskApplyWeightFix(string message)
        {
            if (ClothEditPrompts.Confirm("GP02", message, L10n.Tr("適用", "Apply"), L10n.Tr("やめる", "Cancel"))) ApplyWeightFixFromWindow();
        }

        // 保存していない編集があれば、先に保存するかを聞く。続けてよければ true（discard は、保存せずに続けるか）
        bool ConfirmRebuildForWeights(out bool discard)
        {
            discard = false;
            if (!_controller.HasUnsavedEdits) return true;
            switch (ClothEditPrompts.AskUnsaved(ClothEditLeaveReason.RebuildForWeights))
            {
                case ClothEditUnsavedChoice.Save:
                    return SaveBeforeLeaving();
                case ClothEditUnsavedChoice.Discard:
                    discard = true;
                    return true;
                default:
                    return false;
            }
        }

        void ApplyWeightFixFromWindow()
        {
            if (!ConfirmRebuildForWeights(out bool discard)) return;
            WeightFixReport report = _controller.ApplyWeightFix(discard);
            SceneView.RepaintAll();
            _diagnosticsAt = double.NegativeInfinity;
            if (report == null)
            {
                EditorUtility.DisplayDialog("GP02", _controller.LastWeightFixError, "OK");
                return;
            }
            // 窓の真ん中の通知は短く、前後の数（候補で試したもの）は、窓の上の結果の欄に残す。強さを弱めて適用したときは、その強さも添える
            WeightFixPreview used = report.Preview;
            string undoJa = used != null && used.IsBelowFullStrength ? $"（強さ {used.StrengthPercent}%。Ctrl+Z の 1 回で戻せます）" : "（Ctrl+Z の 1 回で戻せます）";
            string undoEn = used != null && used.IsBelowFullStrength ? $" (strength {used.StrengthPercent}%; one Ctrl+Z undoes it)" : " (one Ctrl+Z undoes it)";
            Report(L10n.Tr("ウェイトを体に寄せました", "Fitted the weights"),
                L10n.Tr("ウェイトを体に寄せました" + undoJa + "。", "Fitted the weights to the body" + undoEn + ".")
                + (used != null ? "\n" + used.Describe(ShowsWeightFixDetails) : ""));
        }

        void RemoveWeightFixFromWindow()
        {
            int recorded = _controller.RecordedWeightEditVertexCount;
            if (!ClothEditPrompts.Confirm("GP02", L10n.Tr($"データに記録したウェイトの変更（{recorded} 頂点）を外し、衣装のメッシュのウェイトを元に戻しますか？", $"Remove the recorded weight change ({recorded} vertices) and return the outfit's mesh to the original weights?"),
                L10n.Tr("外す", "Remove"), L10n.Tr("やめる", "Cancel")))
            {
                return;
            }
            if (!ConfirmRebuildForWeights(out bool discard)) return;
            if (!_controller.RemoveWeightFix(discard)) EditorUtility.DisplayDialog("GP02", _controller.LastWeightFixError, "OK");
            else Toast(L10n.Tr("ウェイトの変更を外しました", "Removed the weight change"));
            SceneView.RepaintAll();
        }

        // くわしく表示: 手持ちの AnimationClip（Humanoid のもの。Generic はボーンのパスが合えば使う）と、取り出す間隔
        void DrawPoseCheckClips(PoseCheckResult result, bool running)
        {
            List<AnimationClip> clips = _settings.GetPoseCheckClips();
            using (new EditorGUI.DisabledScope(running))
            {
                _foldPoseClips = EditorGUILayout.Foldout(_foldPoseClips, L10n.Tr($"手持ちの AnimationClip（{clips.Count}）", $"Your Animation Clips ({clips.Count})"), true);
                if (!_foldPoseClips) return;
                EditorGUILayout.LabelField(L10n.Tr("座りモーション・ダンスなどの Humanoid のクリップを足すと、一定の間隔で取り出したポーズも試します（結果には「クリップ名 0.5秒」のように出ます）。Generic のクリップは、ボーンのパスがこのアバターに合えば使います。",
                    "Add Humanoid clips (sitting motions, dances, ...) to also try poses taken from them at a fixed interval (shown as \"clip name 0.5 s\" in the result). Generic clips are used if their bone paths match this avatar."), EditorStyles.wordWrappedMiniLabel);
                EditorGUI.BeginChangeCheck();
                for (int i = 0; i < _settings.PoseCheckClips.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        AnimationClip clip = _settings.PoseCheckClips[i];
                        var picked = (AnimationClip)EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                        if (picked != clip) _settings.PoseCheckClips[i] = picked;
                        int samples = picked != null ? ClothEditSettings.ClipSampleTimes(picked, _settings.PoseCheckClipInterval).Count : 0;
                        GUILayout.Label(L10n.Tr($"{samples} コマ", $"{samples} frame(s)"), EditorStyles.miniLabel, GUILayout.Width(56f));
                        if (GUILayout.Button(new GUIContent("×", L10n.Tr("この AnimationClip を外します", "Removes this Animation Clip")), GUILayout.Width(22f)))
                        {
                            _settings.PoseCheckClips.RemoveAt(i);
                            i--;
                        }
                    }
                }
                var added = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent(L10n.Tr("クリップを足す", "Add Clip"), L10n.Tr("ここに AnimationClip をドラッグするか、選んで足します", "Drag an Animation Clip here or pick one to add it")),
                    null, typeof(AnimationClip), false);
                if (added != null && !_settings.PoseCheckClips.Contains(added)) _settings.PoseCheckClips.Add(added);
                int interval = EditorGUILayout.IntSlider(new GUIContent(L10n.Tr("取り出す間隔（コマ）", "Interval (frames)"),
                    L10n.Tr($"クリップのフレームレートで、このコマ数ごとにポーズを取り出します（最後のコマも入れます。1つのクリップは最大 {ClothEditSettings.MaxPoseCheckClipSamples} コマ）。既定 {ClothEditSettings.DefaultPoseCheckClipInterval}",
                        $"Takes a pose every this many frames at the clip's frame rate (the last frame is included; at most {ClothEditSettings.MaxPoseCheckClipSamples} per clip). Default {ClothEditSettings.DefaultPoseCheckClipInterval}")),
                    _settings.PoseCheckClipInterval, 1, ClothEditSettings.MaxPoseCheckClipInterval);
                _settings.PoseCheckClipInterval = interval;
                if (EditorGUI.EndChangeCheck()) _settingsDirty = true;
            }
            if (result != null)
            {
                foreach (string note in result.ClipNotes) EditorGUILayout.HelpBox(note, MessageType.Info);
            }
        }
    }
}
