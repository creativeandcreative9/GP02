using System;
using System.Collections.Generic;

namespace GP02.Core
{
    /// <summary>
    /// メッシュが、服ではなく体（素体・顔）らしいかを、名前と表情のシェイプキーから推定する。
    /// 服を編集するつもりで、体のメッシュを選んでしまったときに知らせるために使う。Unity に依存しないので、クラウドで自動テストできる。
    /// </summary>
    public static class BodyMeshGuess
    {
        /// <summary>表情のシェイプキーが、これ以上あれば、顔のあるメッシュ（体）とみなす。</summary>
        public const int MinExpressionShapes = 3;

        // 名前に含まれていれば体らしい言葉（小文字）。「体」は、ほかの言葉の一部（体操服など）になりやすいので、別に扱う
        static readonly string[] NameWords = { "body", "face", "head", "素体", "顔" };

        // 体ではなく、服やアクセサリーの名前によくある言葉。これを除いてから、上の言葉を探す
        static readonly string[] GarmentWords =
        {
            "bodysuit", "bodystocking", "bodychain", "bodyharness", "bodywear", "bodycon",
            "headband", "headphone", "headset", "headdress", "headwear", "headgear", "headpiece", "headscarf", "headcover",
            "facemask", "faceguard", "faceshield", "faceveil",
        };

        // 名前が、これと同じ言葉（区切り文字で分けた1語）なら体らしい
        static readonly string[] NameTokens = { "体", "ボディ", "ボディー", "フェイス" };

        // 表情のシェイプキーの目印（小文字）。vrc.v_ は VRChat のビセーム、fcl_ は VRoid の表情
        static readonly string[] ExpressionMarks = { "blink", "まばたき", "mth_", "fcl_", "viseme" };
        const string VisemePrefix = "vrc.v_";

        /// <summary>体のメッシュらしいか。</summary>
        public static bool IsLikelyBody(string meshName, IReadOnlyList<string> blendShapeNames)
        {
            return IsLikelyBody(meshName, blendShapeNames, out _);
        }

        /// <summary>体のメッシュらしいか。らしいときは、そう考えた理由（短い日本語）を reason に入れる。</summary>
        public static bool IsLikelyBody(string meshName, IReadOnlyList<string> blendShapeNames, out string reason)
        {
            string word = FindNameWord(meshName);
            if (word != null)
            {
                reason = $"名前に「{word}」を含む";
                return true;
            }
            int expressions = CountExpressionShapes(blendShapeNames);
            if (expressions >= MinExpressionShapes)
            {
                reason = $"表情のシェイプキーが {expressions} 個ある";
                return true;
            }
            reason = "";
            return false;
        }

        /// <summary>表情のシェイプキー（vrc.v_ で始まる・blink・まばたき・MTH_ などを含む名前）の数。</summary>
        public static int CountExpressionShapes(IReadOnlyList<string> blendShapeNames)
        {
            if (blendShapeNames == null) return 0;
            int count = 0;
            for (int i = 0; i < blendShapeNames.Count; i++)
            {
                if (IsExpressionShape(blendShapeNames[i])) count++;
            }
            return count;
        }

        /// <summary>表情のシェイプキーの名前か。</summary>
        public static bool IsExpressionShape(string blendShapeName)
        {
            if (string.IsNullOrEmpty(blendShapeName)) return false;
            string lower = blendShapeName.ToLowerInvariant();
            if (lower.StartsWith(VisemePrefix, StringComparison.Ordinal)) return true;
            foreach (string mark in ExpressionMarks)
            {
                if (lower.Contains(mark)) return true;
            }
            return false;
        }

        // 名前に含まれる体らしい言葉。無ければ null
        static string FindNameWord(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string lower = name.ToLowerInvariant();
            foreach (string garment in GarmentWords) lower = lower.Replace(garment, "|");
            foreach (string word in NameWords)
            {
                if (lower.Contains(word)) return word;
            }
            foreach (string token in Tokens(lower))
            {
                foreach (string candidate in NameTokens)
                {
                    if (token == candidate) return candidate;
                }
            }
            return null;
        }

        // 英数字・かな・漢字以外（_ - . 空白 など）で区切った語。語の終わりの数字は除く（Body1 → Body）
        static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            int start = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool part = i < text.Length && char.IsLetterOrDigit(text[i]);
                if (part && start < 0) start = i;
                if (!part && start >= 0)
                {
                    tokens.Add(text.Substring(start, i - start).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));
                    start = -1;
                }
            }
            return tokens;
        }
    }
}
