using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 操作の結果の知らせ方。窓の真ん中に出る通知（Toast）は、すぐ消えるので短く（日本語で十数文字まで）して、
    // 詳しい結果は、編集中の見出しの下の「結果の欄」に残す（×で閉じるか、しばらくたつと消える）。
    public sealed partial class ClothEditWindow
    {
        /// <summary>結果の欄を出しておく時間（秒）。</summary>
        const double ResultSeconds = 120.0;

        string _resultText;
        MessageType _resultType;
        double _resultAt;

        /// <summary>結果の欄の文（無ければ空）。</summary>
        internal string ResultText => _resultText ?? "";

        /// <summary>
        /// 短い通知（窓の真ん中）を出し、詳しい結果を窓の中の結果の欄に残す。
        /// </summary>
        /// <param name="shortText">通知の文。日本語で十数文字まで</param>
        /// <param name="details">結果の欄に残す詳しい文</param>
        void Report(string shortText, string details, MessageType type = MessageType.Info)
        {
            Toast(shortText);
            _resultText = details;
            _resultType = type;
            _resultAt = EditorApplication.timeSinceStartup;
            Repaint();
        }

        void ClearResult()
        {
            _resultText = null;
        }

        // 結果の欄。編集中の見出しの下に出す。右の「×」で閉じる
        void DrawResultBox()
        {
            if (string.IsNullOrEmpty(_resultText)) return;
            if (EditorApplication.timeSinceStartup - _resultAt > ResultSeconds)
            {
                _resultText = null;
                return;
            }
            string iconName = _resultType == MessageType.Warning ? "console.warnicon.sml" : _resultType == MessageType.Error ? "console.erroricon.sml" : "console.infoicon.sml";
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUIContent icon = EditorGUIUtility.IconContent(iconName);
                if (icon != null && icon.image != null) GUILayout.Label(icon, GUILayout.Width(18f), GUILayout.Height(18f));
                GUILayout.Label(_resultText, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("×", L10n.Tr("この欄を閉じます", "Close this box")), EditorStyles.miniButton, GUILayout.Width(20f)))
                {
                    _resultText = null;
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
