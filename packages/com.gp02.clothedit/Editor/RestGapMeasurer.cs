using System.Collections.Generic;
using System.Diagnostics;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// 布の粒子ごとに、「体や内側の布までの隙間」を測る。
    ///
    /// 衣装はもともと体や内側の布の外側にあるが、ふんわりした服では間に空気の隙間がある。
    /// 隙間の分だけは内側へ動けるようにすると（<see cref="ClothSolver.SetRestSurfaceAllowance"/>）、布が融通できて伸びが残りにくい。
    /// 測り方：粒子の元の位置から、元の形の面の内向きへレイを飛ばし、最初に当たる体や内側の布までの距離を使う。
    /// 当たる対象は、(a) 同じ布の別の島（重ね着の内側の布など。自分の島は除く）と、
    /// (b) 同じアバター（<c>transform.root</c> の下）の、対象以外の有効なレンダラー。
    /// 複数のレンダラーをまとめて1つの布にしたときは、まとめたレンダラーはすべて (b) から外す（布として動けるので）。
    /// 層どうしの接触（<see cref="LayerContacts"/>）ですぐ内側の層と組になった粒子は、(a) を数えず、内側の層を通り越して体までを測る。
    /// 内側の層は押せば動くので、外側の層は内側の層ごと体の手前まで動ける。
    /// 体（<see cref="BodySurface"/>）を渡すと、体の中に入っている粒子（体が布を突き抜けている所）の隙間は 0 にする。
    /// 体の中から内向きに飛ばしたレイは体の中を通り抜けて、反対側の面や何も無い所まで届き、大きな隙間（最大 10cm）になってしまうため。
    /// </summary>
    public static class RestGapMeasurer
    {
        /// <summary>布の厚みのつもりの余白（メートル）。隙間 = 当たった距離 - 余白。</summary>
        public const float Margin = 0.002f;

        /// <summary>隙間の上限（メートル）。何にも当たらなければこの値。</summary>
        public const float MaxGap = 0.10f;

        // 自分の島に当たったときに、その先へレイをやり直す回数の上限（折れた布の裏側などで、無限に続けないため）
        const int MaxOwnIslandSkips = 8;

        /// <summary>測った結果。</summary>
        public sealed class Result
        {
            /// <summary>粒子ごとの隙間（メートル）。元の形の面の向きが決まらない粒子は 0。</summary>
            public float[] Gaps;

            /// <summary>
            /// 内側の層を通り越さずに測った、粒子ごとの隙間（層どうしの接触を解かないときに使う）。
            /// 通り越して測った粒子が無ければ <see cref="Gaps"/> と同じ配列。
            /// </summary>
            public float[] GapsWithoutPassThrough;

            /// <summary>隙間を測った粒子の数（向きが決まった粒子）。</summary>
            public int MeasuredCount;

            /// <summary>何かに当たった粒子の数（残りは上限の値）。</summary>
            public int HitCount;

            /// <summary>体の中に入っていたので、隙間を 0 にした粒子の数。</summary>
            public int InsideCount;

            public float Mean;
            public float Min;
            public float Max;

            /// <summary>当たる対象にしたレンダラーの数と、その三角形の数（同じメッシュの別の島は含まない）。</summary>
            public int OtherRendererCount;
            public int OtherTriangleCount;

            public double Milliseconds;

            public override string ToString()
            {
                if (MeasuredCount == 0) return "隙間: 測れた粒子がありません";
                return $"体や内側の布までの隙間: 平均 {Mean * 1000f:F1} mm / 最小 {Min * 1000f:F1} mm / 最大 {Max * 1000f:F1} mm" +
                    $"（測った粒子 {MeasuredCount}、当たった {HitCount}、体の中なので 0 にした {InsideCount}。ほかのレンダラー {OtherRendererCount} 個・三角形 {OtherTriangleCount}。{Milliseconds:F0} ms）";
            }
        }

        /// <summary>
        /// 隙間を測る。
        /// </summary>
        /// <param name="cloth">対象の布。元の位置（ワールド座標）と、元の形の面の外向きを使う</param>
        /// <param name="bvh">布の三角形（<c>cloth.Topology.Triangles</c>）の境界箱の木。positions の形で作ってあること</param>
        /// <param name="positions">bvh を作った、まとめた頂点の位置（元の形）</param>
        /// <param name="target">編集する対象。同じアバターの、これ以外のレンダラーが「体」になる</param>
        /// <returns>外向きが無い布（<see cref="ClothModel.RestNormals"/> が null）では null</returns>
        public static Result Measure(ParticleCloth cloth, TriangleBvh bvh, NVector3[] positions, Renderer target)
        {
            return Measure(cloth, bvh, positions, new[] { target }, null);
        }

        /// <summary>
        /// 隙間を測る（複数のレンダラーをまとめた布用）。
        /// </summary>
        /// <param name="targets">まとめて編集するレンダラー。それぞれのアバターの、これら以外のレンダラーが「体」になる</param>
        /// <param name="passThroughLayers">粒子ごとに、同じ布の別の島を数えないか（<see cref="LayerContacts.HasInnerLayer"/>）。null なら全粒子で数える</param>
        public static Result Measure(ParticleCloth cloth, TriangleBvh bvh, NVector3[] positions, IReadOnlyList<Renderer> targets, bool[] passThroughLayers)
        {
            if (cloth.Model.RestNormals == null) return null;
            var timer = Stopwatch.StartNew();
            OtherRendererMeshes others = OtherRendererMeshes.Collect(targets);
            Result result = Measure(cloth, bvh, positions, passThroughLayers, others, null);
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 隙間を測る（集めてあるほかのレンダラーを使う版）。
        /// </summary>
        /// <param name="others">同じアバターの、まとめて編集するレンダラー以外のレンダラー（<see cref="OtherRendererMeshes.Collect"/>）</param>
        /// <param name="body">体の表面。渡すと、体の中にある粒子の隙間を 0 にする。null なら調べない</param>
        public static Result Measure(ParticleCloth cloth, TriangleBvh bvh, NVector3[] positions, bool[] passThroughLayers, OtherRendererMeshes others, BodySurface body)
        {
            NVector3[] normals = cloth.Model.RestNormals;
            if (normals == null) return null;

            var timer = Stopwatch.StartNew();
            var result = new Result();
            TriangleBvh otherBvh = null;
            NVector3[] otherPositions = null;
            if (others != null && others.TriangleCount > 0)
            {
                otherPositions = others.Positions;
                otherBvh = new TriangleBvh(otherPositions, others.Triangles);
                result.OtherRendererCount = others.RendererCount;
                result.OtherTriangleCount = others.TriangleCount;
            }

            NVector3[] rest = cloth.Model.RestPositions;
            int[] particleIsland = cloth.ParticleIsland;
            int[] triangleIsland = cloth.Topology.TriangleIsland;
            var gaps = new float[rest.Length];
            float[] blockedGaps = null;
            double sum = 0.0;
            float min = float.MaxValue, max = 0f;
            int measured = 0, hit = 0;

            for (int i = 0; i < rest.Length; i++)
            {
                NVector3 n = normals[i];
                float length = n.Length();
                if (!(length > 1e-6f)) continue;   // 向きが決まらない粒子は 0（元の形より内側へは入らない）

                NVector3 direction = -n / length;
                float distance = float.MaxValue;

                // (a) 同じ布の別の島。自分の島に当たったら、その先からやり直す
                float layerDistance = float.MaxValue;
                float traveled = 0f;
                for (int skip = 0; skip <= MaxOwnIslandSkips; skip++)
                {
                    NVector3 origin = rest[i] + direction * traveled;
                    if (!bvh.Raycast(origin, direction, positions, out float d, out int triangle)) break;
                    float total = traveled + d;
                    if (triangleIsland[triangle] != particleIsland[i])
                    {
                        layerDistance = total;
                        break;
                    }
                    traveled = total + 1e-5f;
                }
                bool passThrough = passThroughLayers != null && passThroughLayers[i];
                if (!passThrough) distance = layerDistance;

                // (b) 同じアバターの、ほかのレンダラー
                if (otherBvh != null && otherBvh.Raycast(rest[i], direction, otherPositions, out float otherDistance, out _))
                {
                    if (otherDistance < distance) distance = otherDistance;
                    if (otherDistance < layerDistance) layerDistance = otherDistance;
                }

                float gap = GapFromDistance(distance);
                if (distance != float.MaxValue) hit++;
                if (body != null && body.TryQuery(rest[i], BodySurface.DefaultMaxDistance, out BodyHit bodyHit) && bodyHit.SignedDistance < 0f)
                {
                    gap = 0f;
                    layerDistance = 0f;
                    result.InsideCount++;
                }
                if (passThrough)
                {
                    if (blockedGaps == null) blockedGaps = (float[])gaps.Clone();
                    blockedGaps[i] = GapFromDistance(layerDistance);
                }
                else if (blockedGaps != null)
                {
                    blockedGaps[i] = gap;
                }
                gaps[i] = gap;
                sum += gap;
                if (gap < min) min = gap;
                if (gap > max) max = gap;
                measured++;
            }

            timer.Stop();
            result.Gaps = gaps;
            result.GapsWithoutPassThrough = blockedGaps ?? gaps;
            result.MeasuredCount = measured;
            result.HitCount = hit;
            result.Mean = measured > 0 ? (float)(sum / measured) : 0f;
            result.Min = measured > 0 ? min : 0f;
            result.Max = max;
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }

        static float GapFromDistance(float distance)
        {
            return distance == float.MaxValue ? MaxGap : System.Math.Min(MaxGap, System.Math.Max(0f, distance - Margin));
        }
    }
}
