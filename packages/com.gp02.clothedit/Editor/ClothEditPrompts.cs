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

        /// <summary>確認の文章（画面に出す）。</summary>
        public static string UnsavedMessage(ClothEditLeaveReason reason)
        {
            return reason == ClothEditLeaveReason.SwitchTarget
                ? L10n.Tr("保存していない編集があります。別の衣装に切り替える前に、保存しますか？", "You have unsaved edits. Save them before switching to another outfit?")
                : L10n.Tr("保存していない編集があります。編集を終える前に、保存しますか？", "You have unsaved edits. Save them before ending the edit?");
        }

        static ClothEditUnsavedChoice DefaultAskUnsaved(ClothEditLeaveReason reason)
        {
            int result = EditorUtility.DisplayDialogComplex("GP02", UnsavedMessage(reason),
                L10n.Tr("保存する", "Save"), L10n.Tr("やめる", "Cancel"), L10n.Tr("保存しないで終わる", "Don't Save"));
            return result == 0 ? ClothEditUnsavedChoice.Save : result == 1 ? ClothEditUnsavedChoice.Cancel : ClothEditUnsavedChoice.Discard;
        }
    }
}
