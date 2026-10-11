using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// コンソールへの [GP02] の記録（情報ログ）。ふだんは出さず、詳細設定の「記録をコンソールに出す」がオンのときだけ出す。
    /// 1回の操作で何行も出ると、初めての人がコンソールの「!」をエラーと見間違えるため。注意（警告）とエラーは、この設定に関係なく出す。
    /// </summary>
    public static class ClothEditLog
    {
        public const string PrefKey = "GP02.LogToConsole";
        static bool? _enabled;

        /// <summary>記録をコンソールに出すか。EditorPrefs に保存する。既定はオフ。</summary>
        public static bool Enabled
        {
            get
            {
                if (_enabled == null) _enabled = EditorPrefs.GetBool(PrefKey, false);
                return _enabled.Value;
            }
            set
            {
                _enabled = value;
                EditorPrefs.SetBool(PrefKey, value);
            }
        }

        public static void Info(string message)
        {
            if (Enabled) Debug.Log(message);
        }
    }
}
