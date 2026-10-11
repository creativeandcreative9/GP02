using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    // 対称編集の補助: 鏡像の対応（ブラシと同じ選び方）、相手のいない点、左右差、今の形を左右対称にする、鏡の面の自動の位置合わせ。
    public sealed partial class ClothEditSession
    {
        int[] _brushMirrorMap;          // ClothSolver.FindMirrorParticles の結果（面と許容距離ごとに1つだけ持つ）
        MirrorPlane _brushMirrorPlane;
        float _brushMirrorTolerance;
        int _brushMirrorUnmatched;
        float[] _mirrorDifferenceWork;
        int _mirrorDifferenceVersion = -1;
        int[] _mirrorDifferenceMap;

        /// <summary>
        /// 鏡像を探す許容距離（メートル）。ピン・選んだ点の鏡像の側、元に戻すブラシの鏡像に使う（ブラシは <see cref="BrushOptions.MirrorTolerance"/>）。
        /// <see cref="ClothEditController.ApplySettings"/> が設定（<see cref="ClothEditSettings.MirrorTolerance"/>）から入れる。
        /// </summary>
        public float MirrorTolerance { get; set; } = BrushOptions.DefaultMirrorTolerance;

        /// <summary>
        /// 粒子ごとの鏡像の粒子（つまむブラシと同じ選び方、<see cref="ClothSolver.FindMirrorParticles"/>）。見つからなければ -1。
        /// 元の形で決まるので、面と許容距離が同じなら使い回す（返した配列は書き換えないこと）。
        /// </summary>
        public int[] GetBrushMirrorMap(MirrorPlane plane, float tolerance)
        {
            if (_brushMirrorMap != null && _brushMirrorPlane.Point == plane.Point && _brushMirrorPlane.Normal == plane.Normal && _brushMirrorTolerance == tolerance)
            {
                return _brushMirrorMap;
            }
            if (_brushMirrorMap == null) _brushMirrorMap = new int[ParticleCount];
            _brushMirrorUnmatched = _solver.FindMirrorParticles(plane, tolerance, _brushMirrorMap);
            _brushMirrorPlane = plane;
            _brushMirrorTolerance = tolerance;
            _mirrorDifferenceVersion = -1;
            return _brushMirrorMap;
        }

        /// <summary>鏡像の粒子が見つからない粒子の数（つまむブラシは、これらの点では反対側を動かせない）。</summary>
        public int CountUnmatchedMirror(MirrorPlane plane, float tolerance)
        {
            GetBrushMirrorMap(plane, tolerance);
            return _brushMirrorUnmatched;
        }

        /// <summary>鏡像の粒子が見つからない粒子の、今の位置を result に入れる（先に空にする）。maxCount を超えるときは間引く。</summary>
        public void GetUnmatchedMirrorPositions(MirrorPlane plane, float tolerance, List<Vector3> result, int maxCount)
        {
            result.Clear();
            int[] map = GetBrushMirrorMap(plane, tolerance);
            int unmatched = _brushMirrorUnmatched;
            if (unmatched == 0 || maxCount <= 0) return;
            int stride = Mathf.Max(1, (unmatched + maxCount - 1) / maxCount);
            ReadOnlySpan<NVector3> p = _solver.Positions;
            int seen = 0;
            for (int i = 0; i < map.Length && result.Count < maxCount; i++)
            {
                if (map[i] >= 0 || seen++ % stride != 0) continue;
                result.Add(new Vector3(p[i].X, p[i].Y, p[i].Z));
            }
        }

        /// <summary>
        /// 左右差（<see cref="ClothSolver.MeasureMirrorDifference"/>、メートル）の色分け用に、粒子の位置と差を最大 maxCount 個入れる。
        /// 差の大きい粒子（hotThreshold 以上）を優先し、残りは間引く。鏡像の無い粒子は入れない。形が変わったときだけ測り直す。
        /// </summary>
        public void GetMirrorDifferenceDots(MirrorPlane plane, float tolerance, List<Vector3> positions, List<float> values, int maxCount, float hotThreshold = 0.002f)
        {
            int[] map = GetBrushMirrorMap(plane, tolerance);
            if (_mirrorDifferenceWork == null) _mirrorDifferenceWork = new float[ParticleCount];
            if (_mirrorDifferenceVersion != _shapeVersion || !ReferenceEquals(_mirrorDifferenceMap, map))
            {
                _solver.MeasureMirrorDifference(map, plane, tolerance, _mirrorDifferenceWork);
                _mirrorDifferenceVersion = _shapeVersion;
                _mirrorDifferenceMap = map;
            }
            CollectDots(_mirrorDifferenceWork, hotThreshold, positions, values, maxCount);
        }

        /// <summary>
        /// 左右差の最大（メートル）。鏡像のある粒子のうちで一番大きい差。形が変わったときだけ測り直す。
        /// </summary>
        public float MaxMirrorDifference(MirrorPlane plane, float tolerance)
        {
            GetMirrorDifferenceDots(plane, tolerance, _dotPositionsWork, _dotValuesWork, 0);
            float max = 0f;
            for (int i = 0; i < _mirrorDifferenceWork.Length; i++) max = Mathf.Max(max, _mirrorDifferenceWork[i]);
            return max;
        }

        readonly List<Vector3> _dotPositionsWork = new List<Vector3>();
        readonly List<float> _dotValuesWork = new List<float>();

        /// <summary>
        /// 今の形を左右対称にする（<see cref="ClothSolver.SymmetrizeShape"/>）。基準の側の変形を、鏡に映して反対側へ写す。
        /// 布の計算はしないので、呼び出し側（<see cref="ClothEditController.SymmetrizeShape"/>）が仕上げで伸びを直す。位置を変えた粒子の数を返す。
        /// </summary>
        public int SymmetrizeShape(MirrorPlane plane, float tolerance, bool keepPositiveSide)
        {
            int[] map = GetBrushMirrorMap(plane, tolerance);
            _grabParticle = -1;
            _grabSet = null;
            int changed = _solver.SymmetrizeShape(plane, tolerance, keepPositiveSide, map);
            if (changed > 0) UpdateCurrentShape();
            return changed;
        }

        /// <summary>
        /// 元の形の粒子を、法線 normal の面で鏡に映したときに一番よく重なる面の位置を探す（<see cref="MirrorFit.FindOffset"/>）。
        /// 戻り値は、origin から normal の向きに測った面のずれ（ワールドのメートル）。
        /// </summary>
        /// <param name="matchedRatio">見つけた面で、鏡に映した粒子のうち相手がいた割合</param>
        public float FitMirrorOffset(Vector3 normal, Vector3 origin, float matchDistance, out float matchedRatio)
        {
            return MirrorFit.FindOffset(_cloth.Model.RestPositions, ClothEditSymmetry.ToNumerics(normal), ClothEditSymmetry.ToNumerics(origin), matchDistance, out matchedRatio);
        }

        // 値の大きい粒子（hot 以上）を優先して、位置と値を入れる。値が負の粒子（鏡像が無いなど）は入れない
        void CollectDots(float[] work, float hot, List<Vector3> positions, List<float> values, int maxCount)
        {
            positions.Clear();
            values.Clear();
            int n = work.Length;
            if (maxCount <= 0 || n == 0) return;
            int hotCount = 0, coldCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (work[i] >= hot) hotCount++;
                else if (work[i] >= 0f) coldCount++;
            }
            int hotKept = Math.Min(hotCount, maxCount * 3 / 4);
            int hotStride = hotKept > 0 ? (hotCount + hotKept - 1) / hotKept : 1;
            int coldBudget = Math.Max(1, maxCount - hotKept);
            int coldStride = coldCount <= coldBudget ? 1 : (coldCount + coldBudget - 1) / coldBudget;
            ReadOnlySpan<NVector3> p = _solver.Positions;
            int hotSeen = 0, coldSeen = 0;
            for (int i = 0; i < n && positions.Count < maxCount; i++)
            {
                float value = work[i];
                if (value < 0f) continue;
                bool isHot = value >= hot;
                if (isHot ? hotSeen++ % hotStride != 0 : coldSeen++ % coldStride != 0) continue;
                positions.Add(new Vector3(p[i].X, p[i].Y, p[i].Z));
                values.Add(value);
            }
        }
    }
}
