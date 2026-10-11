using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 粗い段の粒子（階層）。細かい粒子だけで解くと、長さの補正が全体に行き渡るまでに多くの反復が要る。
    /// そこで、間引いた粗い粒子で大きな形を先に決め、その動きを細かい粒子へ配ってから仕上げる
    /// （Hierarchical Position Based Dynamics の考え方）。
    ///
    /// 粗い段の粒子は細かい粒子の一部（同じ番号）なので、段どうしで位置を受け渡す必要がない。
    /// 粗い段では伸びだけを防ぐ（縮みは許す）。粗い粒子どうしの間には細かい布がたわんで収まれるため。
    /// </summary>
    public sealed class ClothHierarchy
    {
        internal sealed class Level
        {
            public float Spacing;
            public int[] Members;          // この段の粒子（細かい粒子の番号）
            public int[] StretchPairs;     // 細かい粒子の番号で表したペア
            public float[] RestLengths;
            public int[] BindOffsets;      // 細かい粒子ごと（この段に含まれない粒子だけが持つ）
            public int[] BindParents;
            public float[] BindWeights;
        }

        // 粗い段から順
        internal Level[] Levels { get; }

        ClothHierarchy(Level[] levels)
        {
            Levels = levels;
        }

        public int LevelCount => Levels.Length;

        /// <summary>段ごとの粒子の間隔（0 が一番粗い）。</summary>
        public float LevelSpacing(int level) => Levels[level].Spacing;

        public int LevelParticleCount(int level) => Levels[level].Members.Length;

        /// <summary>この段に含まれない粒子が、動きを受け取る親の数（この段に含まれる粒子は 0）。</summary>
        public int ParentCount(int level, int particle) => Levels[level].BindOffsets[particle + 1] - Levels[level].BindOffsets[particle];

        /// <param name="levels">粗い段の数。間隔は細かい粒子の 2倍、4倍…になる</param>
        internal static ClothHierarchy Build(MeshTopology topology, SurfaceSearch search, Vector3[] particles, int[] particleTriangle,
            float spacing, int levels, float stretchRange, float bindRange)
        {
            var built = new List<Level>();
            IReadOnlyList<int> previous = AllIndices(particles.Length);
            float levelSpacing = spacing;
            for (int l = 0; l < levels; l++)
            {
                levelSpacing *= 2f;
                List<int> members = ParticleNetwork.Thin(topology, particles, particleTriangle, previous, levelSpacing);
                if (members.Count == previous.Count) break;   // これ以上粗くならない

                var pairs = new List<int>();
                ParticleNetwork.Connect(search, particles, particleTriangle, members, stretchRange * levelSpacing, 0f, pairs, null);

                // この段に含まれない粒子を、この段の粒子に結びつける
                var isMember = new bool[particles.Length];
                foreach (int m in members) isMember[m] = true;
                var startTriangle = new int[particles.Length];
                for (int i = 0; i < particles.Length; i++) startTriangle[i] = isMember[i] ? -1 : particleTriangle[i];
                ParticleNetwork.Bind(topology, search, particles, startTriangle, particles, particleTriangle, members,
                    bindRange * levelSpacing, out int[] offsets, out int[] parents, out float[] weights);

                var restLengths = new float[pairs.Count / 2];
                for (int k = 0; k < restLengths.Length; k++)
                {
                    restLengths[k] = Vector3.Distance(particles[pairs[2 * k]], particles[pairs[2 * k + 1]]);
                }

                built.Add(new Level
                {
                    Spacing = levelSpacing,
                    Members = members.ToArray(),
                    StretchPairs = pairs.ToArray(),
                    RestLengths = restLengths,
                    BindOffsets = offsets,
                    BindParents = parents,
                    BindWeights = weights,
                });
                previous = members;
            }

            built.Reverse();
            return new ClothHierarchy(built.ToArray());
        }

        /// <summary>
        /// 粒子の位置が変わったあとの階層を返す（元は変更しない）。
        /// 段の構成（メンバー・ペア・結びつけ）はそのまま使い、位置から決まる RestLengths だけを計算し直す。
        /// </summary>
        internal ClothHierarchy WithRestPositions(Vector3[] particles)
        {
            var levels = new Level[Levels.Length];
            for (int l = 0; l < levels.Length; l++)
            {
                Level source = Levels[l];
                int[] pairs = source.StretchPairs;
                var restLengths = new float[source.RestLengths.Length];
                for (int k = 0; k < restLengths.Length; k++)
                {
                    Vector3 a = particles[pairs[2 * k]], b = particles[pairs[2 * k + 1]];
                    float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                    restLengths[k] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                levels[l] = new Level
                {
                    Spacing = source.Spacing,
                    Members = source.Members,
                    StretchPairs = source.StretchPairs,
                    RestLengths = restLengths,
                    BindOffsets = source.BindOffsets,
                    BindParents = source.BindParents,
                    BindWeights = source.BindWeights,
                };
            }
            return new ClothHierarchy(levels);
        }

        static int[] AllIndices(int count)
        {
            var all = new int[count];
            for (int i = 0; i < count; i++) all[i] = i;
            return all;
        }
    }
}
