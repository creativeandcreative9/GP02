using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 自分で描いた減衰カーブに名前を付けて、EditorPrefs に保存・呼び出しする（最大 <see cref="MaxCount"/> 個）。
    /// プロジェクトをまたいで使えるよう、アセットではなく EditorPrefs に入れる。画面に依存しないので、テストで確かめられる。
    /// </summary>
    public sealed class ClothEditCurveLibrary
    {
        /// <summary>保存できるカーブの数。</summary>
        public const int MaxCount = 10;

        /// <summary>名前の長さの上限（文字）。</summary>
        public const int MaxNameLength = 40;

        /// <summary>EditorPrefs のキーの既定。テストでは別のキーを渡して、本物の保存を壊さない。</summary>
        public const string DefaultKey = "GP02.Settings.SavedCurves";

        readonly string _key;
        readonly List<string> _names = new List<string>();
        readonly List<AnimationCurve> _curves = new List<AnimationCurve>();

        public ClothEditCurveLibrary(string key = DefaultKey)
        {
            _key = key;
            Load();
        }

        /// <summary>保存してあるカーブの数。</summary>
        public int Count => _names.Count;

        /// <summary>index 番目の名前（保存した順）。</summary>
        public string GetName(int index) => _names[index];

        /// <summary>index 番目のカーブ（写しを返すので、書き換えても保存されているものは変わらない）。</summary>
        public AnimationCurve GetCurve(int index) => new AnimationCurve(_curves[index].keys);

        /// <summary>名前の番号。無ければ -1。</summary>
        public int IndexOf(string name)
        {
            string trimmed = Normalize(name);
            for (int i = 0; i < _names.Count; i++)
            {
                if (_names[i] == trimmed) return i;
            }
            return -1;
        }

        /// <summary>
        /// カーブを名前を付けて保存する。同じ名前があれば上書きする。新しい名前で、すでに <see cref="MaxCount"/> 個あるときは保存せず false。
        /// 名前が空のときも false。message に結果の案内（利用者向け）を入れる。
        /// </summary>
        public bool Save(string name, AnimationCurve curve, out string message)
        {
            string trimmed = Normalize(name);
            if (trimmed.Length == 0)
            {
                message = L10n.Tr("名前を入れてください。", "Enter a name.");
                return false;
            }
            if (curve == null || curve.length == 0)
            {
                message = L10n.Tr("カーブがありません。", "There is no curve.");
                return false;
            }
            int index = IndexOf(trimmed);
            if (index < 0 && _names.Count >= MaxCount)
            {
                message = L10n.Tr($"保存できるのは {MaxCount} 個までです。いらないものを削除してください。", $"You can save up to {MaxCount} curves. Delete one you no longer need.");
                return false;
            }
            var copy = new AnimationCurve(curve.keys);
            if (index >= 0)
            {
                _curves[index] = copy;
                message = L10n.Tr($"「{trimmed}」を上書きしました。", $"Overwrote \"{trimmed}\".");
            }
            else
            {
                _names.Add(trimmed);
                _curves.Add(copy);
                message = L10n.Tr($"「{trimmed}」を保存しました。", $"Saved \"{trimmed}\".");
            }
            Store();
            return true;
        }

        /// <summary>名前のカーブを消す。無ければ false。</summary>
        public bool Delete(string name)
        {
            int index = IndexOf(name);
            if (index < 0) return false;
            _names.RemoveAt(index);
            _curves.RemoveAt(index);
            Store();
            return true;
        }

        /// <summary>保存してあるものを読み直す。</summary>
        public void Load()
        {
            _names.Clear();
            _curves.Clear();
            string text = EditorPrefs.GetString(_key, "");
            if (string.IsNullOrEmpty(text)) return;
            // 1行に「名前<タブ>カーブの文字列」。名前にタブと改行は入れない（Normalize で除く）
            foreach (string line in text.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0 || _names.Count >= MaxCount) continue;
                string name = Normalize(line.Substring(0, tab));
                if (name.Length == 0 || IndexOf(name) >= 0) continue;
                _names.Add(name);
                _curves.Add(ClothEditSettings.CurveFromString(line.Substring(tab + 1)));
            }
        }

        /// <summary>保存したものをすべて消す（テストの後片付け用）。</summary>
        public void DeleteAll()
        {
            _names.Clear();
            _curves.Clear();
            EditorPrefs.DeleteKey(_key);
        }

        void Store()
        {
            var lines = new string[_names.Count];
            for (int i = 0; i < _names.Count; i++) lines[i] = _names[i] + "\t" + ClothEditSettings.CurveToString(_curves[i]);
            EditorPrefs.SetString(_key, string.Join("\n", lines));
        }

        static string Normalize(string name)
        {
            if (name == null) return "";
            string cleaned = name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
            return cleaned.Length > MaxNameLength ? cleaned.Substring(0, MaxNameLength) : cleaned;
        }
    }
}
