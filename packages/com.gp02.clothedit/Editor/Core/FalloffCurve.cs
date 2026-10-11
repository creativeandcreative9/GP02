using System;

namespace GP02.Core
{
    /// <summary>
    /// ブラシの範囲の中の重み（減衰カーブ）。t（中心からの距離 / ブラシ半径、0〜1）→ 重み（0〜1）。
    /// 0〜1 を等間隔に区切ったサンプルで持ち、間は直線で補う。
    /// Unity の AnimationCurve をサンプルして <see cref="FromSamples"/> に渡せる。
    /// 作ったあとは変わらない。
    /// </summary>
    public sealed class FalloffCurve
    {
        const int PresetSampleCount = 65;

        readonly float[] _samples;

        FalloffCurve(float[] samples)
        {
            _samples = samples;
        }

        /// <summary>範囲の中は全部 1（範囲全体が同じだけ動く）。既定。</summary>
        public static FalloffCurve Constant { get; } = new FalloffCurve(new[] { 1f, 1f });

        /// <summary>中心 1 から端 0 へ、なめらかに（smoothstep）減る。</summary>
        public static FalloffCurve Smooth { get; } = FromFunction(t => 1f - t * t * (3f - 2f * t), PresetSampleCount);

        /// <summary>中心 1 から端 0 へ、まっすぐ減る。</summary>
        public static FalloffCurve Linear { get; } = new FalloffCurve(new[] { 1f, 0f });

        /// <summary>中心だけが強く動き、すぐに減る（(1 - t)²）。</summary>
        public static FalloffCurve Sharp { get; } = FromFunction(t => (1f - t) * (1f - t), PresetSampleCount);

        /// <summary>球の断面の形（√(1 - t²)）。端の近くまで強く動き、端で急に 0 になる。</summary>
        public static FalloffCurve Sphere { get; } = FromFunction(t => MathF.Sqrt(MathF.Max(0f, 1f - t * t)), PresetSampleCount);

        /// <summary>
        /// 0〜1 を等間隔に区切ったサンプルから作る（samples[0] が t=0、最後が t=1）。値は 0〜1 に収める（NaN は 0）。
        /// 配列はコピーするので、あとで書き換えても影響しない。
        /// </summary>
        public static FalloffCurve FromSamples(float[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (samples.Length == 0) throw new ArgumentException("サンプルが1つもありません。", nameof(samples));
            var copy = new float[samples.Length];
            for (int i = 0; i < copy.Length; i++)
            {
                float v = samples[i];
                copy[i] = v > 0f ? (v < 1f ? v : 1f) : 0f;   // NaN もここで 0 になる
            }
            return new FalloffCurve(copy);
        }

        /// <summary>関数を sampleCount 点でサンプルして作る。</summary>
        public static FalloffCurve FromFunction(Func<float, float> function, int sampleCount = PresetSampleCount)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount));
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++) samples[i] = function(i / (float)(sampleCount - 1));
            return FromSamples(samples);
        }

        public int SampleCount => _samples.Length;

        public float GetSample(int index) => _samples[index];

        /// <summary>t（0〜1 の外は端の値）での重み。</summary>
        public float Evaluate(float t)
        {
            int last = _samples.Length - 1;
            if (last == 0 || !(t > 0f)) return _samples[0];
            if (t >= 1f) return _samples[last];
            float x = t * last;
            int k = (int)x;
            if (k >= last) return _samples[last];
            float f = x - k;
            return _samples[k] + (_samples[k + 1] - _samples[k]) * f;
        }

        // 同じカーブか（同じ参照か、サンプルがすべて同じ）。範囲を選び直すかどうかの判断に使う
        internal bool SameAs(FalloffCurve other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null || other._samples.Length != _samples.Length) return false;
            for (int i = 0; i < _samples.Length; i++)
            {
                if (_samples[i] != other._samples[i]) return false;
            }
            return true;
        }
    }
}
