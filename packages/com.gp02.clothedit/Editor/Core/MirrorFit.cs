using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 対称編集の鏡の面の位置を、形から自動で合わせる。衣装がアバターの中心からずれていても、衣装そのものの左右対称の面に合わせるため。
    /// </summary>
    public static class MirrorFit
    {
        /// <summary>重なり具合を測るのに使う点の数の上限（多い形は間引く）。</summary>
        public const int MaxSamples = 2000;

        /// <summary>
        /// 法線 normal の面を動かして、points を鏡に映した点が points 自身と一番よく重なる位置を探す。
        /// 重なり具合は、鏡に映した点のまわりにある点の数（幅 matchDistance/2 のガウス関数の重み）で測るので、
        /// 片側だけにある飾りなどがあっても、残りの部分の対称の面に合う（重心で決めるより、ずれに強い）。
        /// 探す範囲は、点の重心を中心に、形の幅（法線の向き）の 1/4 と matchDistance を足した分。粗く探してから2回細かくする。
        /// </summary>
        /// <param name="points">形の点（ワールド座標など、面と同じ座標）</param>
        /// <param name="normal">面の法線。長さは問わない（0 ベクトルは不可）</param>
        /// <param name="origin">結果のずれを測る基準の点</param>
        /// <param name="matchDistance">重なったとみなす距離（メートル）。粒子の間隔の 2 倍程度</param>
        /// <param name="matchedRatio">見つけた面で、鏡に映した点のうち matchDistance 以内に相手がいた割合（0〜1。出力）</param>
        /// <returns>面が通る点の、origin からの normal（長さ1にしたもの）の向きのずれ（メートル）</returns>
        public static float FindOffset(Vector3[] points, Vector3 normal, Vector3 origin, float matchDistance, out float matchedRatio)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            float length = normal.Length();
            if (!(length > 1e-12f) || float.IsInfinity(length)) throw new ArgumentException("法線が 0 ベクトルです。", nameof(normal));
            if (!(matchDistance > 0f) || float.IsInfinity(matchDistance)) throw new ArgumentOutOfRangeException(nameof(matchDistance));
            Vector3 n = normal / length;
            matchedRatio = 0f;
            if (points.Length == 0) return 0f;

            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            for (int i = 0; i < points.Length; i++)
            {
                float d = Vector3.Dot(points[i] - origin, n);
                min = MathF.Min(min, d);
                max = MathF.Max(max, d);
                sum += d;
            }
            float center = (float)(sum / points.Length);

            var grid = new PointGrid(matchDistance);
            for (int i = 0; i < points.Length; i++) grid.Add(i, points[i]);
            int stride = Math.Max(1, (points.Length + MaxSamples - 1) / MaxSamples);
            var samples = new List<Vector3>(points.Length / stride + 1);
            for (int i = 0; i < points.Length; i += stride) samples.Add(points[i]);
            var near = new List<int>();

            float range = 0.25f * (max - min) + matchDistance;
            const int CoarseSteps = 20;
            float step = range / CoarseSteps;
            float best = center;
            float bestCost = Cost(center, points, samples, grid, near, n, origin, matchDistance, out _);
            for (int k = -CoarseSteps; k <= CoarseSteps; k++)
            {
                Consider(center + k * step);
            }
            // 粗い刻みの間を、2回に分けて細かく探す（重なり具合は刻みより細かい所で凸凹するので、二分探索より確実）
            for (int pass = 0; pass < 2; pass++)
            {
                float middle = best;
                float fine = step / 10f;
                for (int k = -10; k <= 10; k++) Consider(middle + k * fine);
                step = fine;
            }
            Cost(best, points, samples, grid, near, n, origin, matchDistance, out matchedRatio);
            return best;

            void Consider(float offset)
            {
                float cost = Cost(offset, points, samples, grid, near, n, origin, matchDistance, out _);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = offset;
                }
            }
        }

        // 面のずれ offset で鏡に映した点と、元の点との重なり具合（小さいほどよく重なる）。
        // 一番近い点までの距離で測ると、粒子が格子状に並んだ平らな所では、ずれが粒子の間隔の半分ごとに同じくらい重なってしまい、
        // 間違った位置を選ぶことがある。そこで、鏡に映した点のまわりの点の数を、幅 cap/2 のガウス関数の重みで数える
        // （幅が粒子の間隔くらいあると、並びの内側ではどのずれでもほぼ同じ値になり、形の縁が位置を決める）
        static float Cost(float offset, Vector3[] points, List<Vector3> samples, PointGrid grid, List<int> near, Vector3 n, Vector3 origin, float cap, out float matchedRatio)
        {
            double total = 0;
            int matched = 0;
            float capSq = cap * cap;
            float sigma = 0.5f * cap;
            float inverse = 1f / (2f * sigma * sigma);
            float reach = 3f * sigma;
            float reachSq = reach * reach;
            for (int s = 0; s < samples.Count; s++)
            {
                Vector3 p = samples[s];
                float d = Vector3.Dot(p - origin, n) - offset;
                Vector3 q = p - 2f * d * n;
                grid.Query(q, reach, near);
                float bestSq = float.MaxValue;
                for (int c = 0; c < near.Count; c++)
                {
                    Vector3 r = points[near[c]];
                    float dx = r.X - q.X, dy = r.Y - q.Y, dz = r.Z - q.Z;
                    float sq = dx * dx + dy * dy + dz * dz;
                    if (sq < bestSq) bestSq = sq;
                    if (sq < reachSq) total += MathF.Exp(-sq * inverse);
                }
                if (bestSq < capSq) matched++;
            }
            matchedRatio = samples.Count > 0 ? matched / (float)samples.Count : 0f;
            return samples.Count > 0 ? -(float)(total / samples.Count) : 0f;
        }
    }
}
