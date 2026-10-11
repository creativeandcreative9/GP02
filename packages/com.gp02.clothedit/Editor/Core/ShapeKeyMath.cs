using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>in-between（途中のフレームがあるシェイプキー）の作り方。</summary>
    public enum InBetweenMode
    {
        /// <summary>途中のフレームは、最終形の比例分に、そのフレーム固有の差分を足したもの。</summary>
        Progressive = 0,

        /// <summary>途中のフレームは、その重みでの形そのもの。隣り合うフレームの間は線形に混ざる。</summary>
        Crossfade = 1,
    }

    /// <summary>シェイプキーの1フレーム。Unity の BlendShape のフレーム（重みと、頂点ごとの差分）と同じ形。</summary>
    public sealed class ShapeFrame
    {
        /// <summary>重み（0〜100）。</summary>
        public float Weight;

        /// <summary>頂点ごとの差分（元メッシュの頂点数）。</summary>
        public Vector3[] Deltas;

        public ShapeFrame(float weight, Vector3[] deltas)
        {
            Weight = weight;
            Deltas = deltas;
        }
    }

    /// <summary>
    /// 頂点ごとの差分（シェイプキーの1フレーム分）の操作。
    /// 配列は元メッシュの頂点数で、差分は元メッシュの頂点ごとに持つ（同じ位置の頂点を結合しない）。
    /// どの関数も入力は変更せず、新しい配列を返す。編集の確定や取り込みのときに1回呼ぶ処理で、毎フレームは回らない。
    /// </summary>
    public static class ShapeKeyMath
    {
        /// <summary>シェイプキーの重みの上限（Unity の BlendShape の最終フレーム）。</summary>
        public const float FullWeight = 100f;

        // 鏡の面の上にあるとみなす距離（メートル）
        const float OnPlaneTolerance = 1e-6f;

        static float Component(Vector3 v, int axis)
        {
            switch (axis)
            {
                case 0: return v.X;
                case 1: return v.Y;
                case 2: return v.Z;
                default: throw new ArgumentOutOfRangeException(nameof(axis), "軸は 0（X）・1（Y）・2（Z）のどれかです。");
            }
        }

        static Vector3 Mirrored(Vector3 v, int axis)
        {
            switch (axis)
            {
                case 0: return new Vector3(-v.X, v.Y, v.Z);
                case 1: return new Vector3(v.X, -v.Y, v.Z);
                case 2: return new Vector3(v.X, v.Y, -v.Z);
                default: throw new ArgumentOutOfRangeException(nameof(axis), "軸は 0（X）・1（Y）・2（Z）のどれかです。");
            }
        }

        static void CheckSameLength(int expected, int actual, string name)
        {
            if (actual != expected) throw new ArgumentException($"頂点数が一致しません（{expected} 個のはずが {actual} 個）。", name);
        }

        // ------------------------------------------------------------------
        // 左右の分割・反転・対称化
        // ------------------------------------------------------------------

        /// <summary>
        /// 差分を、左右の2つに分ける。left + right は元の差分に戻る。
        /// 中央付近は、中央を中心とする blendWidth の幅（片側 blendWidth / 2）でなめらかに（smoothstep）移り変わる。
        /// 中央でいきなり切ると、中央をまたぐ三角形が裂けて見えるため。
        /// </summary>
        /// <param name="axis">左右を分ける軸（0=X, 1=Y, 2=Z）</param>
        /// <param name="center">分ける位置（軸の値）</param>
        /// <param name="blendWidth">なめらかに分ける幅（メートル）。0 以下なら中央でぴったり分ける（中央ちょうどの頂点は半分ずつ）</param>
        /// <param name="leftIsPositive">true なら軸の値が大きい側を left にする（既定。Blender 由来のアバターは、キャラクターの左が +X になることが多いため）</param>
        public static void SplitLeftRight(Vector3[] deltas, Vector3[] restPositions, int axis, float center, float blendWidth,
            out Vector3[] left, out Vector3[] right, bool leftIsPositive = true)
        {
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (restPositions == null) throw new ArgumentNullException(nameof(restPositions));
            CheckSameLength(deltas.Length, restPositions.Length, nameof(restPositions));
            Component(Vector3.Zero, axis);

            left = new Vector3[deltas.Length];
            right = new Vector3[deltas.Length];
            float half = blendWidth * 0.5f;
            for (int i = 0; i < deltas.Length; i++)
            {
                float s = Component(restPositions[i], axis) - center;
                if (!leftIsPositive) s = -s;

                float weightLeft;
                if (half <= 0f)
                {
                    weightLeft = s > 0f ? 1f : s < 0f ? 0f : 0.5f;
                }
                else
                {
                    float t = Math.Min(Math.Max(0.5f + 0.5f * s / half, 0f), 1f);
                    weightLeft = t * t * (3f - 2f * t);
                }
                left[i] = deltas[i] * weightLeft;
                // 引き算で作ると、足し直したときに丸め誤差も含めて元に戻る
                right[i] = deltas[i] - left[i];
            }
        }

        /// <summary>
        /// 左右を反転した差分を返す。頂点 v には、鏡像の頂点の差分を鏡に映して入れる。
        /// 鏡像の頂点が見つからなかった頂点（mirrorMap が -1）の差分は 0 になる。
        /// </summary>
        public static Vector3[] Flip(Vector3[] deltas, int[] mirrorMap, int axis)
        {
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (mirrorMap == null) throw new ArgumentNullException(nameof(mirrorMap));
            CheckSameLength(deltas.Length, mirrorMap.Length, nameof(mirrorMap));
            Component(Vector3.Zero, axis);

            var result = new Vector3[deltas.Length];
            for (int v = 0; v < deltas.Length; v++)
            {
                int m = mirrorMap[v];
                if (m >= 0) result[v] = Mirrored(deltas[m], axis);
            }
            return result;
        }

        /// <summary>
        /// 片側の差分を、もう片側へ写して左右対称にする。
        /// keepPositiveSide が true なら軸の値が center より大きい側を残し、反対側を鏡像で置き換える。
        /// 中央の面の上の頂点は、自分と鏡像の平均にする（左右対称な差分では、中央の頂点は軸方向に動けないため）。
        /// 鏡像が見つからない頂点は、そのまま残す。
        /// </summary>
        public static Vector3[] Symmetrize(Vector3[] deltas, Vector3[] restPositions, int[] mirrorMap, int axis, float center, bool keepPositiveSide)
        {
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (restPositions == null) throw new ArgumentNullException(nameof(restPositions));
            if (mirrorMap == null) throw new ArgumentNullException(nameof(mirrorMap));
            CheckSameLength(deltas.Length, restPositions.Length, nameof(restPositions));
            CheckSameLength(deltas.Length, mirrorMap.Length, nameof(mirrorMap));
            Component(Vector3.Zero, axis);

            var result = (Vector3[])deltas.Clone();
            for (int v = 0; v < deltas.Length; v++)
            {
                float s = Component(restPositions[v], axis) - center;
                if (mirrorMap[v] < 0) continue;
                Vector3 mirrored = Mirrored(deltas[mirrorMap[v]], axis);
                if (MathF.Abs(s) < OnPlaneTolerance)
                {
                    result[v] = (deltas[v] + mirrored) * 0.5f;
                    continue;
                }
                bool onKeptSide = keepPositiveSide ? s > 0f : s < 0f;
                if (!onKeptSide) result[v] = mirrored;
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 足し算・引き算・倍率
        // ------------------------------------------------------------------

        /// <summary>a + b * weightB。</summary>
        public static Vector3[] Add(Vector3[] a, Vector3[] b, float weightB = 1f)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            CheckSameLength(a.Length, b.Length, nameof(b));
            var result = new Vector3[a.Length];
            for (int i = 0; i < a.Length; i++) result[i] = a[i] + b[i] * weightB;
            return result;
        }

        /// <summary>a - b。</summary>
        public static Vector3[] Subtract(Vector3[] a, Vector3[] b) => Add(a, b, -1f);

        /// <summary>deltas * s。</summary>
        public static Vector3[] Scale(Vector3[] deltas, float s)
        {
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            var result = new Vector3[deltas.Length];
            for (int i = 0; i < deltas.Length; i++) result[i] = deltas[i] * s;
            return result;
        }

        // ------------------------------------------------------------------
        // in-between
        // ------------------------------------------------------------------

        /// <summary>
        /// in-between のフレームを作る。途中のフレームのあとに、重み 100 の最終形が付く。
        /// - Progressive: 途中のフレーム k = 最終形 * (重み_k / 100) + frameDeltas[k]（フレーム固有の差分）。
        ///   frameDeltas が 0 なら、普通の（1フレームの）シェイプキーと同じ動きになる
        /// - Crossfade: 途中のフレーム k = frameDeltas[k] そのもの（その重みでの形）。隣り合うフレームの間は、Unity が線形に混ぜる
        /// 位置・法線・接線のどれも線形の計算なので、同じ関数を成分ごとに呼べばよい。
        /// </summary>
        /// <param name="finalDeltas">重み 100 の差分</param>
        /// <param name="weights">途中のフレームの重み（0 より大きく 100 より小さい。小さい順）</param>
        /// <param name="frameDeltas">途中のフレームごとの差分（weights と同じ数）</param>
        public static ShapeFrame[] MakeInBetweenFrames(InBetweenMode mode, Vector3[] finalDeltas,
            IReadOnlyList<float> weights, IReadOnlyList<Vector3[]> frameDeltas)
        {
            if (finalDeltas == null) throw new ArgumentNullException(nameof(finalDeltas));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (frameDeltas == null) throw new ArgumentNullException(nameof(frameDeltas));
            if (weights.Count != frameDeltas.Count) throw new ArgumentException("重みとフレームの数が一致しません。", nameof(frameDeltas));
            CheckWeights(weights);

            var frames = new ShapeFrame[weights.Count + 1];
            for (int k = 0; k < weights.Count; k++)
            {
                Vector3[] own = frameDeltas[k];
                if (own == null) throw new ArgumentException($"{k} 番目のフレームの差分がありません。", nameof(frameDeltas));
                CheckSameLength(finalDeltas.Length, own.Length, nameof(frameDeltas));
                Vector3[] deltas = mode == InBetweenMode.Progressive
                    ? Add(own, finalDeltas, weights[k] / FullWeight)
                    : (Vector3[])own.Clone();
                frames[k] = new ShapeFrame(weights[k], deltas);
            }
            frames[weights.Count] = new ShapeFrame(FullWeight, (Vector3[])finalDeltas.Clone());
            return frames;
        }

        /// <summary>
        /// 部品を順に動かしていく形（EreMorph のグループの Progressive／Crossfade）から、フレームを作る。
        /// weights[k] は、部品 k が重み 100% になる位置。最後は 100 にすること。
        /// - Progressive: 部品を 0→100% の順に動かす。フレーム k = 部品 0〜k の合計
        /// - Crossfade: 部品を 0→100%→0 の順に動かす。フレーム k = 部品 k だけ（隣り合うフレームの間は線形に混ざる）
        /// </summary>
        public static ShapeFrame[] MakeSequentialFrames(InBetweenMode mode, IReadOnlyList<float> weights, IReadOnlyList<Vector3[]> units)
        {
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (units == null) throw new ArgumentNullException(nameof(units));
            if (weights.Count != units.Count) throw new ArgumentException("重みと部品の数が一致しません。", nameof(units));
            if (weights.Count == 0) throw new ArgumentException("部品がありません。", nameof(units));
            if (weights[weights.Count - 1] != FullWeight) throw new ArgumentException("最後の重みは 100 にしてください。", nameof(weights));
            var inner = new List<float>(weights);
            inner.RemoveAt(inner.Count - 1);
            CheckWeights(inner);

            var frames = new ShapeFrame[weights.Count];
            Vector3[] sum = new Vector3[units[0].Length];
            for (int k = 0; k < units.Count; k++)
            {
                CheckSameLength(sum.Length, units[k].Length, nameof(units));
                Vector3[] deltas;
                if (mode == InBetweenMode.Progressive)
                {
                    sum = Add(sum, units[k]);
                    deltas = (Vector3[])sum.Clone();
                }
                else
                {
                    deltas = (Vector3[])units[k].Clone();
                }
                frames[k] = new ShapeFrame(weights[k], deltas);
            }
            return frames;
        }

        static void CheckWeights(IReadOnlyList<float> weights)
        {
            float previous = 0f;
            for (int k = 0; k < weights.Count; k++)
            {
                if (!(weights[k] > previous) || !(weights[k] < FullWeight))
                {
                    throw new ArgumentException("途中のフレームの重みは、0 より大きく 100 より小さい値を小さい順に並べてください。", nameof(weights));
                }
                previous = weights[k];
            }
        }

        /// <summary>
        /// フレームを、指定した重みで評価した差分を返す（Unity が BlendShape を描くときの計算に合わせる）。
        /// 最初のフレームより小さければ 0 との線形補間、最後のフレームより大きければ最後のフレームを比例して延長する。
        /// frames は重みの小さい順であること。
        /// </summary>
        public static Vector3[] EvaluateFrames(IReadOnlyList<ShapeFrame> frames, float weight, int vertexCount)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            var result = new Vector3[vertexCount];
            if (frames.Count == 0 || weight == 0f) return result;

            int upper = 0;
            while (upper < frames.Count && weight > frames[upper].Weight) upper++;

            if (upper == 0 || upper == frames.Count)
            {
                ShapeFrame frame = frames[upper == 0 ? 0 : frames.Count - 1];
                if (frame.Weight == 0f) return result;
                CheckSameLength(vertexCount, frame.Deltas.Length, nameof(frames));
                float scale = weight / frame.Weight;
                for (int v = 0; v < vertexCount; v++) result[v] = frame.Deltas[v] * scale;
                return result;
            }

            ShapeFrame lo = frames[upper - 1], hi = frames[upper];
            CheckSameLength(vertexCount, lo.Deltas.Length, nameof(frames));
            CheckSameLength(vertexCount, hi.Deltas.Length, nameof(frames));
            if (hi.Weight <= lo.Weight) return result;
            float t = (weight - lo.Weight) / (hi.Weight - lo.Weight);
            for (int v = 0; v < vertexCount; v++) result[v] = Vector3.Lerp(lo.Deltas[v], hi.Deltas[v], t);
            return result;
        }

        // ------------------------------------------------------------------
        // 法線
        // ------------------------------------------------------------------

        /// <summary>
        /// 動かした後の形で法線を計算し、元の形で計算した法線との差を返す。
        /// 返した差を、メッシュに入っている元の法線に足して使う（法線を人が調整してあるメッシュや、固いエッジを壊さないため、
        /// 法線そのものではなく差だけを持つ）。
        /// 既定（mergeCoincident が false）では、同じ位置の頂点は結合せず、元メッシュの頂点ごとに計算する。結合すると、固いエッジで分けてある法線が混ざってしまうため。
        /// </summary>
        /// <param name="restPositions">元の形の頂点（元メッシュの頂点数）</param>
        /// <param name="triangles">三角形（全サブメッシュを順に並べた、元メッシュの頂点番号）</param>
        /// <param name="deltas">頂点ごとの位置の差分</param>
        /// <param name="restNormals">メッシュに入っている元の法線。面のない頂点（法線を計算できない頂点）と、法線がゼロの頂点の差分を 0 にするのに使う</param>
        /// <param name="smoothAngleDegrees">
        /// 同じ位置の頂点の面を、どこまでなめらかにつなぐか（度）。180（既定）で全部つなぐ。
        /// 頂点の面と、法線のなす角がこの角度以内の面だけを、その頂点の法線に混ぜる（元の形で判定する）。
        /// UV の継ぎ目の頂点どうしはなめらかにつなぎ、固いエッジは分けたままにするために使う。
        /// mergeCoincident が false のときは、頂点が持つ面だけを使うので効かない。
        /// </param>
        /// <param name="mergeCoincident">
        /// true なら、同じ位置にある頂点（UV の継ぎ目などで分かれたもの）の面も、その頂点の法線に混ぜる。
        /// 継ぎ目の両側の法線がそろい、動かしたあとに継ぎ目だけ陰影が違う、ということが無くなる
        /// </param>
        public static void RecomputeNormalDeltas(Vector3[] restPositions, int[] triangles, Vector3[] deltas, Vector3[] restNormals,
            out Vector3[] normalDeltas, float smoothAngleDegrees = 180f, bool mergeCoincident = false)
        {
            if (restPositions == null) throw new ArgumentNullException(nameof(restPositions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (restNormals == null) throw new ArgumentNullException(nameof(restNormals));
            if (!(smoothAngleDegrees >= 0f)) throw new ArgumentOutOfRangeException(nameof(smoothAngleDegrees), "スムーズ角度は 0 以上にしてください。");
            CheckSameLength(restPositions.Length, deltas.Length, nameof(deltas));
            CheckSameLength(restPositions.Length, restNormals.Length, nameof(restNormals));

            int count = restPositions.Length;
            var moved = new Vector3[count];
            for (int i = 0; i < count; i++) moved[i] = restPositions[i] + deltas[i];

            var before = new Vector3[count];
            var after = new Vector3[count];
            if (mergeCoincident)
            {
                ComputeMergedNormals(restPositions, moved, triangles, smoothAngleDegrees, before, after);
            }
            else
            {
                MeshNormals.Compute(restPositions, triangles, before);
                MeshNormals.Compute(moved, triangles, after);
            }

            normalDeltas = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                if (before[i] == Vector3.Zero || after[i] == Vector3.Zero || restNormals[i] == Vector3.Zero) continue;
                normalDeltas[i] = after[i] - before[i];
            }
        }

        // 同じ位置の頂点の面も混ぜた法線を、元の形（before）と動かした後の形（after）で計算する。
        // 混ぜる面は元の形で決め、両方の形で同じ面を使う（動かした後で面の選び方が変わると、差が不連続になるため）
        static void ComputeMergedNormals(Vector3[] rest, Vector3[] moved, int[] triangles, float smoothAngleDegrees, Vector3[] before, Vector3[] after)
        {
            int vertexCount = rest.Length;
            int faceCount = triangles.Length / 3;

            var crossRest = new Vector3[faceCount];
            var crossMoved = new Vector3[faceCount];
            var unitRest = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int a = triangles[3 * f], b = triangles[3 * f + 1], c = triangles[3 * f + 2];
                // 外積の大きさは面積の2倍なので、正規化せずに足すと面積の重みになる
                crossRest[f] = Vector3.Cross(rest[b] - rest[a], rest[c] - rest[a]);
                crossMoved[f] = Vector3.Cross(moved[b] - moved[a], moved[c] - moved[a]);
                float length = crossRest[f].Length();
                unitRest[f] = length > 1e-20f ? crossRest[f] / length : Vector3.Zero;
            }

            int[] group = VertexWelder.Weld(rest, MeshTopology.DefaultWeldTolerance, out Vector3[] groupPositions);
            int groupCount = groupPositions.Length;
            BuildFaceLists(triangles, faceCount, vertexCount, null, out int[] vertexOffsets, out int[] vertexFaces);
            BuildFaceLists(triangles, faceCount, groupCount, group, out int[] groupOffsets, out int[] groupFaces);

            // 180 度以上は、角度を調べずに全部つなぐ
            bool connectAll = smoothAngleDegrees >= 180f;
            float cosLimit = MathF.Cos(smoothAngleDegrees * MathF.PI / 180f) - 1e-5f;
            var ownStamp = new int[faceCount];
            Array.Fill(ownStamp, -1);

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 sumBefore = Vector3.Zero, sumAfter = Vector3.Zero;
                int ownStart = vertexOffsets[v], ownEnd = vertexOffsets[v + 1];
                for (int k = ownStart; k < ownEnd; k++)
                {
                    int f = vertexFaces[k];
                    ownStamp[f] = v;
                    sumBefore += crossRest[f];
                    sumAfter += crossMoved[f];
                }

                int g = group[v];
                for (int k = groupOffsets[g]; k < groupOffsets[g + 1]; k++)
                {
                    int other = groupFaces[k];
                    if (ownStamp[other] == v) continue;
                    if (!connectAll && !IsWithinAngle(unitRest[other], vertexFaces, ownStart, ownEnd, unitRest, cosLimit)) continue;
                    ownStamp[other] = v;   // 同じ面を2回足さない
                    sumBefore += crossRest[other];
                    sumAfter += crossMoved[other];
                }

                before[v] = Normalized(sumBefore);
                after[v] = Normalized(sumAfter);
            }
        }

        static Vector3 Normalized(Vector3 v)
        {
            float length = v.Length();
            return length > 1e-20f ? v / length : Vector3.Zero;
        }

        // 面 candidate が、頂点の持つ面のどれかと、cosLimit 以上の近さ（なす角が限度以内）か
        static bool IsWithinAngle(Vector3 candidate, int[] ownFaces, int start, int end, Vector3[] unit, float cosLimit)
        {
            if (candidate == Vector3.Zero) return false;
            for (int k = start; k < end; k++)
            {
                Vector3 own = unit[ownFaces[k]];
                if (own != Vector3.Zero && Vector3.Dot(candidate, own) >= cosLimit) return true;
            }
            return false;
        }

        // 頂点（または、頂点をまとめたグループ）ごとの、面の番号の一覧（CSR 形式）。面の同じグループに重複して入れない
        static void BuildFaceLists(int[] triangles, int faceCount, int ownerCount, int[] vertexToOwner, out int[] offsets, out int[] faces)
        {
            offsets = new int[ownerCount + 1];
            for (int f = 0; f < faceCount; f++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int owner = vertexToOwner == null ? triangles[3 * f + corner] : vertexToOwner[triangles[3 * f + corner]];
                    if (IsRepeatedCorner(triangles, f, corner, vertexToOwner)) continue;
                    offsets[owner + 1]++;
                }
            }
            for (int i = 0; i < ownerCount; i++) offsets[i + 1] += offsets[i];

            faces = new int[offsets[ownerCount]];
            var cursor = new int[ownerCount];
            for (int f = 0; f < faceCount; f++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int owner = vertexToOwner == null ? triangles[3 * f + corner] : vertexToOwner[triangles[3 * f + corner]];
                    if (IsRepeatedCorner(triangles, f, corner, vertexToOwner)) continue;
                    faces[offsets[owner] + cursor[owner]++] = f;
                }
            }
        }

        // 面の前の角が、同じ持ち主（同じ頂点・同じグループ）なら true（つぶれた三角形で同じ面を重ねて数えない）
        static bool IsRepeatedCorner(int[] triangles, int face, int corner, int[] vertexToOwner)
        {
            int owner = vertexToOwner == null ? triangles[3 * face + corner] : vertexToOwner[triangles[3 * face + corner]];
            for (int earlier = 0; earlier < corner; earlier++)
            {
                int other = vertexToOwner == null ? triangles[3 * face + earlier] : vertexToOwner[triangles[3 * face + earlier]];
                if (other == owner) return true;
            }
            return false;
        }
    }

    /// <summary>鏡像の頂点の対応表。</summary>
    public static class VertexMirrorMap
    {
        /// <summary>
        /// 頂点ごとに、軸 axis について center で鏡に映した位置にある頂点の番号を返す。tolerance 以内に無ければ -1。
        /// 複数あれば一番近いもの（同じ距離なら自分自身、それも無ければ番号の小さい方）。
        /// 同じ位置に頂点が重なっている（UV の継ぎ目など）場合は、そのうちの1つに対応する。
        /// </summary>
        public static int[] Build(Vector3[] restPositions, int axis, float center, float tolerance)
        {
            if (restPositions == null) throw new ArgumentNullException(nameof(restPositions));
            if (axis < 0 || axis > 2) throw new ArgumentOutOfRangeException(nameof(axis), "軸は 0（X）・1（Y）・2（Z）のどれかです。");
            if (tolerance <= 0f) throw new ArgumentOutOfRangeException(nameof(tolerance));

            // セルが小さすぎると座標の桁が足りなくなるので、1e-4 より小さくしない（候補は距離で絞るので結果は変わらない）
            var grid = new PointGrid(Math.Max(tolerance, 1e-4f));
            for (int i = 0; i < restPositions.Length; i++) grid.Add(i, restPositions[i]);

            var map = new int[restPositions.Length];
            var candidates = new List<int>();
            float toleranceSq = tolerance * tolerance;
            for (int v = 0; v < restPositions.Length; v++)
            {
                Vector3 p = restPositions[v];
                float mirroredValue = 2f * center - (axis == 0 ? p.X : axis == 1 ? p.Y : p.Z);
                Vector3 target = axis == 0 ? new Vector3(mirroredValue, p.Y, p.Z)
                    : axis == 1 ? new Vector3(p.X, mirroredValue, p.Z)
                    : new Vector3(p.X, p.Y, mirroredValue);

                grid.Query(target, tolerance, candidates);
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int c = 0; c < candidates.Count; c++)
                {
                    int u = candidates[c];
                    float d = Vector3.DistanceSquared(restPositions[u], target);
                    if (d > toleranceSq) continue;
                    bool better = d < bestDistance
                        || (d == bestDistance && (u == v || (best != v && u < best)));
                    if (!better) continue;
                    best = u;
                    bestDistance = d;
                }
                map[v] = best;
            }
            return map;
        }
    }
}
