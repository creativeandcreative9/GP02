using System.Collections.Generic;

namespace GP02.Core
{
    /// <summary>番号の組 (a, b) を、向きに関係なく1つの long のキーにする。</summary>
    internal static class PairKeys
    {
        public static readonly IEqualityComparer<long> Comparer = new MixingComparer();

        public static long Make(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        /// <summary><see cref="Make"/> の逆（小さい番号が a）。</summary>
        public static void Split(long key, out int a, out int b)
        {
            a = (int)(key >> 32);
            b = (int)(uint)key;
        }

        // long の標準のハッシュは上位と下位の XOR なので、(a, b) の組では衝突が多く、
        // 要素が増えると辞書が極端に遅くなる。値をかき混ぜてから使う。
        sealed class MixingComparer : IEqualityComparer<long>
        {
            public bool Equals(long x, long y) => x == y;

            public int GetHashCode(long key)
            {
                ulong z = (ulong)key + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return (int)(z ^ (z >> 31));
            }
        }
    }
}
