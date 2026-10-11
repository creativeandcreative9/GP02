using UnityEditor;

namespace GP02.EditorTools
{
    /// <summary>画面に出す言葉の言語。</summary>
    public enum UiLanguage
    {
        Japanese,
        English,
    }

    /// <summary>
    /// 画面の文字の日本語・英語の切り替え。文字は呼び出し側に両方並べて書く（<c>L10n.Tr("保存", "Save")</c>）。
    /// 翻訳表を別に持たないのは、日本語を正として、使う場所で英語を一緒に直せるようにするため。
    /// </summary>
    public static class L10n
    {
        const string PrefKey = "GP02.Language";
        static UiLanguage? _language;

        /// <summary>今の言語。EditorPrefs に保存する。最初は Unity の言語ではなく日本語（主な利用者に合わせる）。</summary>
        public static UiLanguage Language
        {
            get
            {
                if (_language == null) _language = (UiLanguage)EditorPrefs.GetInt(PrefKey, (int)UiLanguage.Japanese);
                return _language.Value;
            }
            set
            {
                _language = value;
                EditorPrefs.SetInt(PrefKey, (int)value);
            }
        }

        public static string Tr(string japanese, string english) => Language == UiLanguage.English ? english : japanese;
    }
}
