using System;
using UnityEditor;

namespace GP02.EditorTools
{
    /// <summary>保存していない編集があるときの確認で、利用者が選んだもの。</summary>
    public enum ClothEditUnsavedChoice
    {
        /// <summary>保存してから続ける。</summary>
        Save,

        /// <summary>やめる（編集を続ける）。</summary>
        Cancel,

        /// <summary>保存しないで続ける（編集の内容は捨てる）。</summary>
        Discard,
    }

    /// <summary>保存していない編集を残したまま、何をしようとしているか（確認の文章に使う）。</summary>
    public enum ClothEditLeaveReason
    {
        /// <summary>「編集を終了」。</summary>
        End,

        /// <summary>別の衣装で編集を始める。</summary>
        SwitchTarget,

        /// <summary>ウェイトを体に寄せる・データから外す（メッシュのウェイトを変えて、編集を作り直す）。</summary>
        RebuildForWeights,
    }

    /// <summary>「ウェイトを体に寄せる」の候補が、体から離れた布を伸ばしすぎるときの確認で、利用者が選んだもの。</summary>
    public enum WeightFixChoice
    {
        /// <summary>強さを弱めて（<see cref="WeightFixPreview.WeakerStrength"/>）、もう一度試す。</summary>
        TryWeaker,

        /// <summary>このまま適用する。</summary>
        ApplyAsIs,

        /// <summary>やめる（何もしない）。</summary>
        Cancel,
    }

    /// <summary>
    /// 確認のダイアログ。ダイアログは自動テストを止めてしまうので、呼び出しをここに集めて、テストでは差し替えられるようにしている。
    /// </summary>
    public static class ClothEditPrompts
    {
        /// <summary>保存していない編集を残して終わる・切り替えるときの確認（保存する／やめる／保存しないで続ける）。</summary>
        public static Func<ClothEditLeaveReason, ClothEditUnsavedChoice> AskUnsaved = DefaultAskUnsaved;

        /// <summary>はい／いいえの確認。タイトル、本文、はいの文字、いいえの文字を渡す。</summary>
        public static Func<string, string, string, string, bool> Confirm = (title, message, ok, cancel) => EditorUtility.DisplayDialog(title, message, ok, cancel);

        /// <summary>
        /// 「ウェイトを体に寄せる」の候補が、体から離れた布を伸ばしすぎるときの確認（弱めて試す／やめる／このまま適用）。本文を渡す。
        /// ボタンの文字は <see cref="WeightFixButtonLabel"/>。
        /// </summary>
        public static Func<string, WeightFixChoice> ChooseWeightFix = DefaultChooseWeightFix;

        /// <summary>確認の文章（画面に出す）。</summary>
        public static string UnsavedMessage(ClothEditLeaveReason reason)
        {
            switch (reason)
            {
                case ClothEditLeaveReason.SwitchTarget:
                    return L10n.Tr("保存していない編集があります。別の衣装に切り替える前に、保存しますか？", "You have unsaved edits. Save them before switching to another outfit?");
                case ClothEditLeaveReason.RebuildForWeights:
                    return L10n.Tr("保存していない編集があります。ウェイトを変えると、そのメッシュで編集を作り直します。先にシェイプキーとして保存しますか？\n（保存しないで続けると、保存していない形は元に戻ります）",
                        "You have unsaved edits. Changing the weights restarts the edit with that mesh. Save them as a shape key first?\n(If you continue without saving, the unsaved shape is reset.)");
                default:
                    return L10n.Tr("保存していない編集があります。編集を終える前に、保存しますか？", "You have unsaved edits. Save them before ending the edit?");
            }
        }

        /// <summary>
        /// 伸びすぎる候補の確認（<see cref="ChooseWeightFix"/>）の本文: 候補の説明と、強さを弱めてもう一度試せるという案内。
        /// detailedView は、くわしく表示か（候補の説明の一言が変わる。<see cref="WeightFixPreview.Describe"/>）。
        /// </summary>
        public static string WeightFixOfferMessage(WeightFixPreview preview, bool detailedView)
        {
            return preview.Describe(detailedView) + "\n\n" + L10n.Tr(
                $"体から離れた布（スカートなど）が伸びすぎます。強さを {WeightFixPreview.WeakerPercent}% に弱めて、もう一度試せます。",
                $"Cloth away from the body (such as a skirt) stretches too much. You can lower the strength to {WeightFixPreview.WeakerPercent}% and try again.");
        }

        /// <summary>
        /// 強さを弱めて試し直した候補の、適用の確認（<see cref="Confirm"/>）の本文: 試した強さ、候補の説明、適用するかの問い。
        /// まだ伸びるときの一言（さらに下げられる）は、候補の説明の中にあるので、ここでは足さない（同じことを2回言わない）。
        /// </summary>
        public static string WeightFixWeakerResultMessage(WeightFixPreview weaker, bool detailedView)
        {
            string text = L10n.Tr($"強さ {weaker.StrengthPercent}% で試した結果です。", $"This is the result with the strength at {weaker.StrengthPercent}%.")
                + "\n" + weaker.Describe(detailedView);
            return WeightFixApplyMessage(text);
        }

        /// <summary>候補の説明に「この候補を適用しますか？」を添えた、適用の確認（<see cref="Confirm"/>）の本文。</summary>
        public static string WeightFixApplyMessage(string description)
        {
            return description + "\n\n" + L10n.Tr("この候補を適用しますか？（Ctrl+Z の 1 回で戻せます）", "Apply this candidate? (One Ctrl+Z undoes it)");
        }

        /// <summary>伸びすぎる候補の確認のボタンの文字（「弱めて（50%）試す」「やめる」「このまま適用」）。確認の文や記録でも同じ言葉を使う。</summary>
        public static string WeightFixButtonLabel(WeightFixChoice choice)
        {
            switch (choice)
            {
                case WeightFixChoice.TryWeaker:
                    return L10n.Tr($"弱めて（{WeightFixPreview.WeakerPercent}%）試す", $"Try Weaker ({WeightFixPreview.WeakerPercent}%)");
                case WeightFixChoice.ApplyAsIs:
                    return L10n.Tr("このまま適用", "Apply As Is");
                default:
                    return L10n.Tr("やめる", "Cancel");
            }
        }

        // DisplayDialogComplex の戻り値（0: 1つ目、1: 2つ目＝Cancel の位置、2: 3つ目）を、選んだものに直す
        internal static WeightFixChoice WeightFixChoiceFromButton(int button)
        {
            return button == 0 ? WeightFixChoice.TryWeaker : button == 1 ? WeightFixChoice.Cancel : WeightFixChoice.ApplyAsIs;
        }

        static WeightFixChoice DefaultChooseWeightFix(string message)
        {
            int result = EditorUtility.DisplayDialogComplex("GP02", message, WeightFixButtonLabel(WeightFixChoice.TryWeaker),
                WeightFixButtonLabel(WeightFixChoice.Cancel), WeightFixButtonLabel(WeightFixChoice.ApplyAsIs));
            return WeightFixChoiceFromButton(result);
        }

        static ClothEditUnsavedChoice DefaultAskUnsaved(ClothEditLeaveReason reason)
        {
            string discard = reason == ClothEditLeaveReason.RebuildForWeights ? L10n.Tr("保存しないで続ける", "Continue Without Saving") : L10n.Tr("保存しないで終わる", "Don't Save");
            int result = EditorUtility.DisplayDialogComplex("GP02", UnsavedMessage(reason), L10n.Tr("保存する", "Save"), L10n.Tr("やめる", "Cancel"), discard);
            return result == 0 ? ClothEditUnsavedChoice.Save : result == 1 ? ClothEditUnsavedChoice.Cancel : ClothEditUnsavedChoice.Discard;
        }
    }
}
