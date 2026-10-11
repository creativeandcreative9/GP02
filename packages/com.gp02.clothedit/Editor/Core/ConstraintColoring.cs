using System;

namespace GP02.Core
{
    /// <summary>
    /// 2粒子の制約を、粒子を共有しない組（色）に分ける。同じ色の制約は、同時に解いても書き込みがぶつからない。
    /// </summary>
    internal static class ConstraintColoring
    {
        /// <summary>
        /// 番号の小さい制約から順に、使える一番小さい色を割り当てる（貪欲法）。
        /// 64色で足りない制約は最後の色（<paramref name="colorCount"/> - 1）にまとめ、その色だけは1つのスレッドで順に解く。
        /// </summary>
        /// <param name="pairs">制約ごとの2粒子（2k, 2k+1）</param>
        /// <param name="colorCount">色の数（出力）</param>
        /// <param name="hasSerialColor">最後の色が、順に解く必要のある寄せ集めか（出力）</param>
        /// <returns>制約ごとの色</returns>
        public static int[] Color(int particleCount, int[] pairs, out int colorCount, out bool hasSerialColor)
        {
            const int MaxColors = 64;
            int constraintCount = pairs.Length / 2;
            var used = new ulong[particleCount];
            var colors = new int[constraintCount];
            int maxColor = -1;
            hasSerialColor = false;

            for (int k = 0; k < constraintCount; k++)
            {
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                ulong free = ~(used[i] | used[j]);
                if (free == 0)
                {
                    colors[k] = MaxColors;
                    hasSerialColor = true;
                    continue;
                }
                int c = TrailingZeroCount(free);
                colors[k] = c;
                ulong bit = 1UL << c;
                used[i] |= bit;
                used[j] |= bit;
                if (c > maxColor) maxColor = c;
            }

            colorCount = maxColor + 1;
            if (hasSerialColor)
            {
                for (int k = 0; k < constraintCount; k++)
                {
                    if (colors[k] == MaxColors) colors[k] = colorCount;
                }
                colorCount++;
            }
            return colors;
        }

        static int TrailingZeroCount(ulong value)
        {
            int count = 0;
            while ((value & 1UL) == 0)
            {
                value >>= 1;
                count++;
            }
            return count;
        }
    }
}
