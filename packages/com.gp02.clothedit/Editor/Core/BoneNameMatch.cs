using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace GP02.Core
{
    /// <summary>
    /// 服だけの別のアーマチュア（Modular Avatar の Merge Armature で使う形）のボーンを、アバターのボーンに対応付ける。
    /// 服のボーンは、アバターと同じ名前に前置き・後置きを付けたもの（"Hips_Outfit"、"Outfit_Hips" など）が多く、位置もアバターのボーンとほぼ同じ。
    /// 名前（そのまま → 前置き・後置きを除いて → 比べやすくした名前）で探し、名前で見つからなければ、親の対応先の下で位置が近いものにする。
    /// </summary>
    public static class BoneNameMatch
    {
        /// <summary>前置き・後置きを推し量るときに、アバターの名前として使う最短の長さ（"L" などで誤って一致しないため）。</summary>
        public const int MinNameLength = 3;

        /// <summary>位置で対応付けるときの許容距離（メートル）の既定値。</summary>
        public const float DefaultPositionTolerance = 0.02f;

        /// <summary>
        /// 名前を比べやすくする。小文字にし、末尾の複製の印（".001"、" (1)"）と、区切り（空白・_・.・-・:）を除く。
        /// </summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.Trim();
            // 末尾の " (1)"
            if (s.EndsWith(")", StringComparison.Ordinal))
            {
                int open = s.LastIndexOf('(');
                if (open > 0 && IsDigits(s, open + 1, s.Length - 1)) s = s.Substring(0, open).TrimEnd();
            }
            // 末尾の ".001"
            if (s.Length > 4 && s[s.Length - 4] == '.' && IsDigits(s, s.Length - 3, s.Length)) s = s.Substring(0, s.Length - 4);
            var builder = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == ' ' || c == '_' || c == '.' || c == '-' || c == ':') continue;
                builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        static bool IsDigits(string s, int start, int end)
        {
            if (end <= start) return false;
            for (int i = start; i < end; i++)
            {
                if (!char.IsDigit(s[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// 服のボーンの名前に共通の前置き・後置きを推し量る。服の名前 = 前置き + アバターの名前 + 後置き の形（大文字小文字は問わない。
        /// アバターの名前は一番長く当てはまるもの）のうち、前置きか後置きが空でなく、2つ以上のボーンで見つかった一番多い組。
        /// 見つからなければ、どちらも空。返す前置き・後置きは小文字。
        /// </summary>
        public static void InferAffixes(IReadOnlyList<string> clothNames, IReadOnlyList<string> avatarNames, out string prefix, out string suffix)
        {
            prefix = "";
            suffix = "";
            if (clothNames == null || avatarNames == null) return;
            var votes = new Dictionary<(string, string), int>();
            foreach (string cloth in clothNames)
            {
                if (string.IsNullOrEmpty(cloth)) continue;
                string lower = cloth.ToLowerInvariant();
                int bestIndex = -1, bestLength = 0;
                foreach (string avatar in avatarNames)
                {
                    if (string.IsNullOrEmpty(avatar) || avatar.Length < MinNameLength || avatar.Length <= bestLength) continue;
                    int index = lower.IndexOf(avatar.ToLowerInvariant(), StringComparison.Ordinal);
                    if (index < 0) continue;
                    bestIndex = index;
                    bestLength = avatar.Length;
                }
                if (bestIndex < 0) continue;
                string p = lower.Substring(0, bestIndex), s = lower.Substring(bestIndex + bestLength);
                if (p.Length == 0 && s.Length == 0) continue;
                votes.TryGetValue((p, s), out int count);
                votes[(p, s)] = count + 1;
            }
            int best = 1;
            foreach (KeyValuePair<(string, string), int> vote in votes)
            {
                (string p, string s) = vote.Key;
                bool better = vote.Value > best || (vote.Value == best && best > 1 && p.Length + s.Length < prefix.Length + suffix.Length);
                if (!better) continue;
                best = vote.Value;
                prefix = p;
                suffix = s;
            }
        }

        /// <summary>前置き・後置き（大文字小文字は問わない）が付いていれば外した名前。外すと空になるなら、そのままの名前。</summary>
        public static string StripAffixes(string name, string prefix, string suffix)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            string s = name;
            if (!string.IsNullOrEmpty(prefix) && s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(prefix.Length);
            if (!string.IsNullOrEmpty(suffix) && s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - suffix.Length);
            return s;
        }

        /// <summary>
        /// 服のボーンごとに、対応するアバターのボーンの番号を返す（無ければ -1）。
        /// 名前で探し（そのまま → 推し量った前置き・後置きを除いて → 比べやすくした名前）、同じ名前が複数あれば、親の対応先の下にあるもの、
        /// その中で位置が近いものを選ぶ。名前で見つからなければ、親の対応先の下（親が対応していなければ全体）で、位置が positionTolerance 以内の一番近いもの。
        /// 並びは親が先であること（clothParents[i] &lt; i）。
        /// </summary>
        /// <param name="clothParents">服のボーンの親の番号（服のボーンの中の番号。親が服のボーンでなければ -1）</param>
        /// <param name="avatarParents">アバターのボーンの親の番号（アバターのボーンの中の番号。無ければ -1）</param>
        public static int[] Match(IReadOnlyList<string> clothNames, IReadOnlyList<int> clothParents, IReadOnlyList<Vector3> clothPositions,
            IReadOnlyList<string> avatarNames, IReadOnlyList<int> avatarParents, IReadOnlyList<Vector3> avatarPositions,
            float positionTolerance = DefaultPositionTolerance)
        {
            if (clothNames == null) throw new ArgumentNullException(nameof(clothNames));
            if (clothParents == null || clothParents.Count != clothNames.Count) throw new ArgumentException("親の数が名前の数と違います。", nameof(clothParents));
            if (clothPositions == null || clothPositions.Count != clothNames.Count) throw new ArgumentException("位置の数が名前の数と違います。", nameof(clothPositions));
            if (avatarNames == null) throw new ArgumentNullException(nameof(avatarNames));
            if (avatarParents == null || avatarParents.Count != avatarNames.Count) throw new ArgumentException("親の数が名前の数と違います。", nameof(avatarParents));
            if (avatarPositions == null || avatarPositions.Count != avatarNames.Count) throw new ArgumentException("位置の数が名前の数と違います。", nameof(avatarPositions));

            var exact = new Dictionary<string, List<int>>();
            var normalized = new Dictionary<string, List<int>>();
            for (int j = 0; j < avatarNames.Count; j++)
            {
                AddTo(exact, (avatarNames[j] ?? "").ToLowerInvariant(), j);
                AddTo(normalized, Normalize(avatarNames[j]), j);
            }
            InferAffixes(clothNames, avatarNames, out string prefix, out string suffix);

            var result = new int[clothNames.Count];
            float toleranceSq = positionTolerance * positionTolerance;
            for (int i = 0; i < clothNames.Count; i++)
            {
                int parent = clothParents[i];
                if (parent >= i) throw new ArgumentException("親が子より後に並んでいます。", nameof(clothParents));
                int parentTarget = parent >= 0 ? result[parent] : -1;
                string name = clothNames[i] ?? "";
                string stripped = StripAffixes(name, prefix, suffix);

                List<int> candidates = Lookup(exact, name.ToLowerInvariant()) ?? Lookup(exact, stripped.ToLowerInvariant())
                    ?? Lookup(normalized, Normalize(stripped)) ?? Lookup(normalized, Normalize(name));
                if (candidates != null)
                {
                    result[i] = PickBest(candidates, parentTarget, clothPositions[i], avatarParents, avatarPositions, float.PositiveInfinity);
                    continue;
                }

                // 名前で見つからない: 親の対応先の下（無ければ全体）で、位置が近いもの
                int bestIndex = -1;
                float bestSq = toleranceSq;
                for (int j = 0; j < avatarNames.Count; j++)
                {
                    if (parentTarget >= 0 && !IsDescendant(j, parentTarget, avatarParents)) continue;
                    float sq = Vector3.DistanceSquared(avatarPositions[j], clothPositions[i]);
                    if (sq > bestSq) continue;
                    bestSq = sq;
                    bestIndex = j;
                }
                result[i] = bestIndex;
            }
            return result;
        }

        static void AddTo(Dictionary<string, List<int>> map, string key, int index)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                map.Add(key, list);
            }
            list.Add(index);
        }

        static List<int> Lookup(Dictionary<string, List<int>> map, string key)
        {
            return !string.IsNullOrEmpty(key) && map.TryGetValue(key, out List<int> list) ? list : null;
        }

        // 候補のうち、親の対応先の下にあるもの（無ければ全部）から、位置が一番近いもの
        static int PickBest(List<int> candidates, int parentTarget, Vector3 position, IReadOnlyList<int> avatarParents, IReadOnlyList<Vector3> avatarPositions, float maxDistance)
        {
            bool anyUnder = false;
            if (parentTarget >= 0)
            {
                foreach (int j in candidates)
                {
                    if (IsDescendant(j, parentTarget, avatarParents)) anyUnder = true;
                }
            }
            int best = -1;
            float bestSq = maxDistance * maxDistance;
            foreach (int j in candidates)
            {
                if (anyUnder && !IsDescendant(j, parentTarget, avatarParents)) continue;
                float sq = Vector3.DistanceSquared(avatarPositions[j], position);
                if (best >= 0 && sq >= bestSq) continue;
                best = j;
                bestSq = sq;
            }
            return best;
        }

        // j が ancestor の子孫か（ancestor 自身は含めない）
        static bool IsDescendant(int j, int ancestor, IReadOnlyList<int> parents)
        {
            int guard = parents.Count;
            for (int p = parents[j]; p >= 0 && guard-- > 0; p = parents[p])
            {
                if (p == ancestor) return true;
            }
            return false;
        }
    }
}
