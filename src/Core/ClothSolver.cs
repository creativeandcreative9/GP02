using System;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>
    /// 「つまんで引っ張る」編集のための粒子計算。
    ///
    /// 1回の Solve で次の2段階を行う。
    /// 1. つまんだ点の移動量を、メッシュに沿った距離に応じて周りへ配る（影響の広がりを決める）
    /// 2. XPBD（Extended Position Based Dynamics）の制約を繰り返し解き、布の伸びと曲げを直す
    ///
    /// 時間は進めない（重力も速度も持たない）ので、時間刻みは 1 とみなし、コンプライアンスは値をそのまま使う。
    /// </summary>
    public sealed class ClothSolver
    {
        readonly ClothModel _model;
        readonly MeshGraph _graph;
        readonly Vector3[] _positions;
        readonly bool[] _pinned;
        readonly float[] _stretchLambda;
        readonly float[] _bendLambda;

        // テザー：つまんだ点・ピンから、メッシュに沿った距離より遠くへは離れない。
        // 少ない反復回数でも布が伸びないようにするための制約（Long Range Attachments）。
        readonly float[] _grabDistance;
        readonly float[] _pinDistance;
        readonly int[] _nearestPin;
        bool _pinDistancesDirty = true;

        // つまんでいる範囲。中心の粒子から、メッシュに沿ってブラシ半径以内の粒子
        readonly bool[] _inBrush;
        int _grabbed = -1;
        float _brushRadius;
        Vector3 _grabTarget;
        Vector3 _grabOrigin;      // つまみ始めたときの中心の位置
        Vector3 _appliedOffset;   // これまでに範囲内へ加えた移動量

        /// <summary>伸びやすさ。0 で伸びない。</summary>
        public float StretchCompliance { get; set; } = 0f;

        /// <summary>曲がりやすさ。大きいほど柔らかく折れ曲がる。</summary>
        public float BendCompliance { get; set; } = 0.1f;

        /// <summary>
        /// つまんでいる範囲の外側へ、動きがメッシュに沿ってどこまで広がるか（メートル）。
        /// この外側は、布の長さを保つために必要な分しか動かない。
        /// </summary>
        public float InfluenceRadius { get; set; } = 0.3f;

        public ClothSolver(ClothModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            int n = model.ParticleCount;
            _graph = new MeshGraph(n, model.StretchPairs, model.StretchRestLengths);
            _positions = (Vector3[])model.RestPositions.Clone();
            _pinned = new bool[n];
            _stretchLambda = new float[model.StretchRestLengths.Length];
            _bendLambda = new float[model.BendRestLengths.Length];
            _grabDistance = new float[n];
            _pinDistance = new float[n];
            _nearestPin = new int[n];
            _inBrush = new bool[n];
        }

        public ClothModel Model => _model;

        /// <summary>現在の粒子の位置。</summary>
        public ReadOnlySpan<Vector3> Positions => _positions;

        /// <summary>つまんでいる範囲の中心の粒子。つまんでいなければ -1。</summary>
        public int GrabbedParticle => _grabbed;

        /// <summary>つまんでいる範囲に含まれるか。</summary>
        public bool IsGrabbed(int particle) => _grabbed >= 0 && _inBrush[particle];

        public bool IsPinned(int particle) => _pinned[particle];

        /// <summary>ピン留めした粒子は動かない。</summary>
        public void SetPinned(int particle, bool pinned)
        {
            if (_pinned[particle] == pinned) return;
            _pinned[particle] = pinned;
            _pinDistancesDirty = true;
        }

        /// <summary>
        /// 粒子をつまみ、指定位置へ動かす。実際に動くのは次の Solve。
        /// </summary>
        /// <param name="particle">つまむ範囲の中心</param>
        /// <param name="target">中心の粒子の移動先。範囲内の粒子は、中心と同じだけ平行移動する</param>
        /// <param name="brushRadius">つまむ範囲（メッシュに沿った距離、メートル）。0 なら中心の1点だけ</param>
        public void Grab(int particle, Vector3 target, float brushRadius = 0f)
        {
            if ((uint)particle >= (uint)_positions.Length) throw new ArgumentOutOfRangeException(nameof(particle));
            if (brushRadius < 0f) throw new ArgumentOutOfRangeException(nameof(brushRadius));
            if (particle != _grabbed || brushRadius != _brushRadius)
            {
                // メッシュに沿った距離で選ぶので、近くにあってもつながっていない布（重ね着など）はつままない
                _graph.ShortestDistances(new[] { particle }, _grabDistance, null);
                for (int i = 0; i < _inBrush.Length; i++) _inBrush[i] = _grabDistance[i] <= brushRadius;
                _grabOrigin = _positions[particle];
                _appliedOffset = Vector3.Zero;
            }
            _grabbed = particle;
            _brushRadius = brushRadius;
            _grabTarget = target;
        }

        public void Release()
        {
            _grabbed = -1;
        }

        /// <summary>元の形に戻す。ピン留めは保持する。</summary>
        public void ResetToRest()
        {
            Array.Copy(_model.RestPositions, _positions, _positions.Length);
            _grabbed = -1;
        }

        public void CopyPositionsTo(Vector3[] destination)
        {
            Array.Copy(_positions, destination, _positions.Length);
        }

        /// <summary>指定位置に一番近い粒子を返す。</summary>
        public int FindNearestParticle(Vector3 point)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _positions.Length; i++)
            {
                float sq = Vector3.DistanceSquared(_positions[i], point);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>つまんだ点を目標へ動かし、制約を指定回数だけ繰り返し解く。</summary>
        public void Solve(int iterations)
        {
            if (_pinDistancesDirty)
            {
                UpdatePinDistances();
                _pinDistancesDirty = false;
            }

            if (_grabbed >= 0) SpreadGrabMotion();

            Array.Clear(_stretchLambda, 0, _stretchLambda.Length);
            Array.Clear(_bendLambda, 0, _bendLambda.Length);

            int[] stretchPairs = _model.StretchPairs;
            float[] stretchRest = _model.StretchRestLengths;
            int[] bendPairs = _model.BendPairs;
            float[] bendRest = _model.BendRestLengths;

            for (int it = 0; it < iterations; it++)
            {
                // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
                for (int k = 0; k < bendRest.Length; k++)
                {
                    SolveDistance(bendPairs[2 * k], bendPairs[2 * k + 1], bendRest[k], BendCompliance, ref _bendLambda[k]);
                }

                for (int k = 0; k < stretchRest.Length; k++)
                {
                    SolveDistance(stretchPairs[2 * k], stretchPairs[2 * k + 1], stretchRest[k], StretchCompliance, ref _stretchLambda[k]);
                }

                SolveTethers();
            }
        }

        void SpreadGrabMotion()
        {
            // 現在位置ではなく、つまみ始めからの移動量で考える。
            // 中心がピン留めされていても、同じ移動を何度も足さないようにするため。
            Vector3 delta = _grabTarget - _grabOrigin - _appliedOffset;
            if (delta.LengthSquared() == 0f) return;
            _appliedOffset += delta;

            for (int i = 0; i < _positions.Length; i++)
            {
                if (_pinned[i]) continue;
                _positions[i] += delta * Falloff(_grabDistance[i]);
            }
        }

        // 1（つまんでいる範囲）〜 0（影響範囲の外）
        float Falloff(float distance)
        {
            if (distance <= _brushRadius) return 1f;
            if (InfluenceRadius <= 0f) return 0f;
            float t = (distance - _brushRadius) / InfluenceRadius;
            if (t >= 1f) return 0f;
            float s = 1f - t;
            return s * s * (3f - 2f * s);
        }

        void UpdatePinDistances()
        {
            int count = 0;
            for (int i = 0; i < _pinned.Length; i++) if (_pinned[i]) count++;
            var sources = new int[count];
            count = 0;
            for (int i = 0; i < _pinned.Length; i++) if (_pinned[i]) sources[count++] = i;
            _graph.ShortestDistances(sources, _pinDistance, _nearestPin);
        }

        void SolveTethers()
        {
            for (int i = 0; i < _positions.Length; i++)
            {
                if (_pinned[i]) continue;
                bool grabbed = IsGrabbed(i);
                if (_grabbed >= 0 && !grabbed) SolveTether(i, _positions[_grabbed], _grabDistance[i]);

                // つまんだ範囲も、ピンから布の長さ以上は離れない。
                // 布の長さが足りない方向へ引っ張ると、本物の布のように目標の手前で止まる（持ち上がる）。
                int pin = _nearestPin[i];
                if (pin >= 0) SolveTether(i, _positions[pin], _pinDistance[i]);
            }
        }

        void SolveTether(int i, Vector3 origin, float maxLength)
        {
            if (float.IsInfinity(maxLength)) return;
            Vector3 d = _positions[i] - origin;
            float length = d.Length();
            if (length <= maxLength) return;
            _positions[i] = origin + d / length * maxLength;
        }

        void SolveDistance(int i, int j, float restLength, float alpha, ref float lambda)
        {
            float wi = InverseMass(i);
            float wj = InverseMass(j);
            float w = wi + wj;
            if (w == 0f) return;

            Vector3 d = _positions[i] - _positions[j];
            float length = d.Length();
            if (length < 1e-12f) return;

            float c = length - restLength;
            float deltaLambda = (-c - alpha * lambda) / (w + alpha);
            lambda += deltaLambda;

            Vector3 correction = d / length * deltaLambda;
            _positions[i] += wi * correction;
            _positions[j] -= wj * correction;
        }

        float InverseMass(int particle)
        {
            return _pinned[particle] || IsGrabbed(particle) ? 0f : 1f;
        }
    }
}
