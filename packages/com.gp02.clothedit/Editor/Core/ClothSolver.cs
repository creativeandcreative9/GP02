using System;
using System.Diagnostics;
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
    ///
    /// 粗い段（<see cref="ClothHierarchy"/>）を渡すと、細かい粒子の計算の前に、粗い段から順に形を決める。
    /// 長さの補正が少ない反復で全体に行き渡るので、伸びが残りにくくなる。
    ///
    /// つまんでいる間は、つまんだ点とつながった部品（島）だけを計算する。
    /// <see cref="ActiveMargin"/> を有限にすると、さらに周りだけに絞れるが、
    /// 外側を固定するため、大きく引っ張ると境目が伸びる（布を周りから引き寄せられないため）。
    /// </summary>
    public sealed class ClothSolver
    {
        readonly ClothModel _model;
        readonly ClothHierarchy _hierarchy;
        readonly Vector3[] _levelStart;   // 粗い段を解き始めたときの位置（その段の粒子だけ使う）
        readonly MeshGraph _graph;
        // 内部では、すべての座標が 0.5m 以上になるよう原点をずらして計算する。
        // 座標 0 付近では、つまんだ動きが伝わるうちに値が指数的に小さくなって非正規化数になり、計算が2倍以上遅くなるため。
        // 0.5 以上の数どうしの差は 0 か一定以上の大きさになるので、非正規化数は出ない。
        readonly Vector3 _origin;
        readonly Vector3[] _positions;        // 内部の座標（ワールド座標 - _origin）
        readonly Vector3[] _worldPositions;   // 外に見せるワールド座標
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

        // 計算する範囲。固定の粒子（ピン、つまんだ範囲、計算範囲の外）は逆質量 0
        readonly float[] _inverseMass;
        readonly int[] _activeParticles;   // 計算範囲にある、ピン留めでない粒子
        readonly int[] _activeStretch;
        readonly int[] _activeBend;
        int _activeParticleCount;
        int _activeStretchCount;
        int _activeBendCount;
        bool _activeDirty = true;
        float _activePull;   // 計算範囲を決めたときの、つまみ始めからの移動距離
        float _influenceRadius = 0.3f;
        float _activeMargin = float.PositiveInfinity;

        /// <summary>伸びやすさ。0 で伸びない。</summary>
        public float StretchCompliance { get; set; } = 0f;

        /// <summary>粗い段1段あたりの反復回数。</summary>
        public int CoarseIterations { get; set; } = 10;

        /// <summary>曲がりやすさ。大きいほど柔らかく折れ曲がる。</summary>
        public float BendCompliance { get; set; } = 0.1f;

        /// <summary>
        /// つまんでいる範囲の外側へ、動きがメッシュに沿ってどこまで広がるか（メートル）。
        /// この外側は、布の長さを保つために必要な分しか動かない。
        /// </summary>
        public float InfluenceRadius
        {
            get => _influenceRadius;
            set
            {
                if (value < 0f) throw new ArgumentOutOfRangeException(nameof(value));
                _influenceRadius = value;
                _activeDirty = true;
            }
        }

        /// <summary>
        /// 影響範囲のさらに外側で、計算に含める余白（メートル）。既定は無限大（つまんだ部品全体を計算する）。
        /// 有限にすると、ブラシ半径＋影響範囲＋余白＋つまみ始めからの移動距離 より外を固定して速くする。
        /// ただし外側を固定すると布を周りから引き寄せられないので、大きく引っ張ると境目が伸びる。
        /// </summary>
        public float ActiveMargin
        {
            get => _activeMargin;
            set
            {
                if (value < 0f) throw new ArgumentOutOfRangeException(nameof(value));
                _activeMargin = value;
                _activeDirty = true;
            }
        }

        /// <summary>直前の Solve で計算した粒子の数（ピン留めを除く）。</summary>
        public int ActiveParticleCount => _activeParticleCount;

        /// <param name="hierarchy">粗い段。null なら細かい粒子だけで解く</param>
        public ClothSolver(ClothModel model, ClothHierarchy hierarchy = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _hierarchy = hierarchy;
            int n = model.ParticleCount;
            if (hierarchy != null) _levelStart = new Vector3[n];
            _graph = new MeshGraph(n, model.StretchPairs, model.StretchRestLengths);
            Vector3[] rest = model.RestPositions;
            Vector3 min = n > 0 ? rest[0] : Vector3.Zero;
            for (int i = 1; i < n; i++) min = Vector3.Min(min, rest[i]);
            _origin = min - new Vector3(0.5f);
            _positions = new Vector3[n];
            for (int i = 0; i < n; i++) _positions[i] = rest[i] - _origin;
            _worldPositions = (Vector3[])rest.Clone();
            _pinned = new bool[n];
            _stretchLambda = new float[model.StretchRestLengths.Length];
            _bendLambda = new float[model.BendRestLengths.Length];
            _grabDistance = new float[n];
            _pinDistance = new float[n];
            _nearestPin = new int[n];
            _inBrush = new bool[n];
            _inverseMass = new float[n];
            _activeParticles = new int[n];
            _activeStretch = new int[model.StretchRestLengths.Length];
            _activeBend = new int[model.BendRestLengths.Length];
        }

        public ClothModel Model => _model;

        /// <summary>現在の粒子の位置。</summary>
        public ReadOnlySpan<Vector3> Positions => _worldPositions;

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
            _activeDirty = true;
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
                _activePull = 0f;
                _activeDirty = true;
            }
            _grabbed = particle;
            _brushRadius = brushRadius;
            _grabTarget = target - _origin;
        }

        public void Release()
        {
            _grabbed = -1;
            _activeDirty = true;
        }

        /// <summary>元の形に戻す。ピン留めは保持する。</summary>
        public void ResetToRest()
        {
            SetInternalPositions(_model.RestPositions);
            _grabbed = -1;
            _activeDirty = true;
        }

        /// <summary>
        /// 粒子の位置を丸ごと置き換える（「1つ戻す」用）。つまんでいる状態は解除する。
        /// XPBD の lambda は Solve のたびに 0 に戻すので、ここでは何もしなくてよい。
        /// </summary>
        public void SetPositions(ReadOnlySpan<Vector3> positions)
        {
            if (positions.Length != _positions.Length) throw new ArgumentException("粒子数が一致しません。", nameof(positions));
            SetInternalPositions(positions);
            _grabbed = -1;
            _activeDirty = true;
        }

        public void CopyPositionsTo(Vector3[] destination)
        {
            Array.Copy(_worldPositions, destination, _worldPositions.Length);
        }

        void SetInternalPositions(ReadOnlySpan<Vector3> world)
        {
            world.CopyTo(_worldPositions);
            for (int i = 0; i < _positions.Length; i++) _positions[i] = world[i] - _origin;
        }

        /// <summary>指定位置に一番近い粒子を返す。</summary>
        public int FindNearestParticle(Vector3 point)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _positions.Length; i++)
            {
                float sq = Vector3.DistanceSquared(_worldPositions[i], point);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// 計算範囲にある伸びの制約のうち、最も大きい伸び（0.1 なら 10%）。
        /// </summary>
        public float MeasureMaxStretch()
        {
            if (_activeDirty)
            {
                UpdateActiveRegion();
                _activeDirty = false;
            }

            int[] pairs = _model.StretchPairs;
            float[] rest = _model.StretchRestLengths;
            float max = 0f;
            for (int a = 0; a < _activeStretchCount; a++)
            {
                int k = _activeStretch[a];
                // 内部の座標は原点をずらした分の丸め誤差を含むので、外に見せるワールド座標で測る
                float length = Vector3.Distance(_worldPositions[pairs[2 * k]], _worldPositions[pairs[2 * k + 1]]);
                max = MathF.Max(max, length / rest[k] - 1f);
            }
            return max;
        }

        /// <summary>
        /// 手を止めたときの仕上げ。今の状態のまま（つまんでいれば、つまんだまま）、
        /// 伸びが tolerance 以下になるか、反復回数が maxIterations に達するまで解き続ける。
        /// つまんだまま解くのは、押さえを外すとつまんでいた範囲が戻り、編集した形が変わってしまうため。
        /// </summary>
        /// <param name="iterationsDone">実際に行った反復回数（出力）</param>
        /// <returns>仕上げた後の最大の伸び</returns>
        public float Settle(float tolerance, int maxIterations, out int iterationsDone, int iterationsPerStep = 40)
        {
            if (iterationsPerStep <= 0) throw new ArgumentOutOfRangeException(nameof(iterationsPerStep));
            iterationsDone = 0;
            float stretch = MeasureMaxStretch();
            while (stretch > tolerance && iterationsDone < maxIterations)
            {
                int step = Math.Min(iterationsPerStep, maxIterations - iterationsDone);
                Solve(step);
                iterationsDone += step;
                stretch = MeasureMaxStretch();
            }
            return stretch;
        }

        /// <summary>
        /// 指定した時間（ミリ秒）だけ解く。Unity側で、1コマの時間予算に収めるために使う。
        /// 最低でも1回は解く。
        /// </summary>
        /// <returns>行った反復回数</returns>
        public int SolveFor(double milliseconds, int iterationsPerStep = 5)
        {
            if (iterationsPerStep <= 0) throw new ArgumentOutOfRangeException(nameof(iterationsPerStep));
            var timer = Stopwatch.StartNew();
            int done = 0;
            do
            {
                Solve(iterationsPerStep);
                done += iterationsPerStep;
            }
            while (timer.Elapsed.TotalMilliseconds < milliseconds);
            return done;
        }

        /// <summary>
        /// つまんだ点を目標へ動かし、制約を解く。
        /// 粗い段があれば、先に各段を <see cref="CoarseIterations"/> 回ずつ解いてから、細かい粒子を iterations 回解く。
        /// </summary>
        public void Solve(int iterations)
        {
            if (_pinDistancesDirty)
            {
                UpdatePinDistances();
                _pinDistancesDirty = false;
            }

            // 大きく引っ張るほど、遠くの布まで引き寄せられる。引っ張った距離に応じて計算範囲を広げる
            if (_grabbed >= 0)
            {
                float pull = Vector3.Distance(_grabTarget, _grabOrigin);
                if (pull > _activePull + 0.5f * _activeMargin)
                {
                    _activePull = pull;
                    _activeDirty = true;
                }
            }

            if (_activeDirty)
            {
                UpdateActiveRegion();
                _activeDirty = false;
            }

            if (_grabbed >= 0) SpreadGrabMotion();
            if (_hierarchy != null && iterations > 0) SolveCoarseLevels();

            int[] stretchPairs = _model.StretchPairs;
            float[] stretchRest = _model.StretchRestLengths;
            int[] bendPairs = _model.BendPairs;
            float[] bendRest = _model.BendRestLengths;

            for (int a = 0; a < _activeStretchCount; a++) _stretchLambda[_activeStretch[a]] = 0f;
            for (int a = 0; a < _activeBendCount; a++) _bendLambda[_activeBend[a]] = 0f;

            for (int it = 0; it < iterations; it++)
            {
                // 最後に伸びの制約とテザーを解くと、1回の反復の終わりで布の長さが最も保たれる
                SolveDistances(_activeBend, _activeBendCount, bendPairs, bendRest, _bendLambda, BendCompliance);
                SolveDistances(_activeStretch, _activeStretchCount, stretchPairs, stretchRest, _stretchLambda, StretchCompliance);

                SolveTethers();
            }

            // 動くのは計算範囲の粒子だけ（ピン留めと範囲外は動かない）
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                _worldPositions[i].X = _positions[i].X + _origin.X;
                _worldPositions[i].Y = _positions[i].Y + _origin.Y;
                _worldPositions[i].Z = _positions[i].Z + _origin.Z;
            }
        }

        void SolveCoarseLevels()
        {
            foreach (var level in _hierarchy.Levels)
            {
                foreach (int m in level.Members) _levelStart[m] = _positions[m];

                for (int it = 0; it < CoarseIterations; it++) SolveStretchOnly(level.StretchPairs, level.RestLengths);

                // この段の粒子の動きを、この段に含まれない粒子へ配る
                Vector3[] p = _positions;
                Vector3[] start = _levelStart;
                int[] offsets = level.BindOffsets;
                int[] parents = level.BindParents;
                float[] weights = level.BindWeights;
                for (int a = 0; a < _activeParticleCount; a++)
                {
                    int i = _activeParticles[a];
                    if (_inverseMass[i] == 0f) continue;
                    int end = offsets[i + 1];
                    float dx = 0f, dy = 0f, dz = 0f;
                    for (int k = offsets[i]; k < end; k++)
                    {
                        int parent = parents[k];
                        float w = weights[k];
                        dx += (p[parent].X - start[parent].X) * w;
                        dy += (p[parent].Y - start[parent].Y) * w;
                        dz += (p[parent].Z - start[parent].Z) * w;
                    }
                    p[i].X += dx;
                    p[i].Y += dy;
                    p[i].Z += dz;
                }
            }
        }

        // 伸びているときだけ縮める（縮んでいるのは許す）
        void SolveStretchOnly(int[] pairs, float[] rest)
        {
            Vector3[] p = _positions;
            float[] inverseMass = _inverseMass;
            for (int k = 0; k < rest.Length; k++)
            {
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;

                float dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, dz = p[i].Z - p[j].Z;
                float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length <= rest[k]) continue;

                float s = -(length - rest[k]) / (w * length);
                dx *= s;
                dy *= s;
                dz *= s;
                p[i].X += wi * dx; p[i].Y += wi * dy; p[i].Z += wi * dz;
                p[j].X -= wj * dx; p[j].Y -= wj * dy; p[j].Z -= wj * dz;
            }
        }

        void UpdateActiveRegion()
        {
            float limit = _brushRadius + _influenceRadius + _activeMargin + _activePull;
            int n = _positions.Length;
            _activeParticleCount = 0;
            for (int i = 0; i < n; i++)
            {
                // つながっていない部品（距離が無限大）は計算しない
                bool active = _grabbed < 0 || (!float.IsInfinity(_grabDistance[i]) && _grabDistance[i] <= limit);
                bool fixedParticle = _pinned[i] || IsGrabbed(i) || !active;
                _inverseMass[i] = fixedParticle ? 0f : 1f;
                // つまんだ範囲もテザーの対象にするので、ピン留めでなければ一覧に入れる
                if (active && !_pinned[i]) _activeParticles[_activeParticleCount++] = i;
            }

            _activeStretchCount = CollectActive(_model.StretchPairs, _activeStretch);
            _activeBendCount = CollectActive(_model.BendPairs, _activeBend);
        }

        // 少なくとも一方の粒子が動ける制約だけを集める
        int CollectActive(int[] pairs, int[] result)
        {
            int count = 0;
            for (int k = 0; k < result.Length; k++)
            {
                if (_inverseMass[pairs[2 * k]] > 0f || _inverseMass[pairs[2 * k + 1]] > 0f) result[count++] = k;
            }
            return count;
        }

        void SpreadGrabMotion()
        {
            // 現在位置ではなく、つまみ始めからの移動量で考える。
            // 中心がピン留めされていても、同じ移動を何度も足さないようにするため。
            Vector3 delta = _grabTarget - _grabOrigin - _appliedOffset;
            if (delta.LengthSquared() == 0f) return;
            _appliedOffset += delta;

            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                float f = Falloff(_grabDistance[i]);
                if (f == 0f) continue;
                _positions[i].X += delta.X * f;
                _positions[i].Y += delta.Y * f;
                _positions[i].Z += delta.Z * f;
            }
        }

        // 1（つまんでいる範囲）〜 0（影響範囲の外）
        float Falloff(float distance)
        {
            if (distance <= _brushRadius) return 1f;
            if (_influenceRadius <= 0f) return 0f;
            float t = (distance - _brushRadius) / _influenceRadius;
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
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                if (_grabbed >= 0 && !_inBrush[i]) SolveTether(i, _grabbed, _grabDistance[i]);

                // つまんだ範囲も、ピンから布の長さ以上は離れない。
                // 布の長さが足りない方向へ引っ張ると、本物の布のように目標の手前で止まる（持ち上がる）。
                int pin = _nearestPin[i];
                if (pin >= 0) SolveTether(i, pin, _pinDistance[i]);
            }
        }

        void SolveTether(int i, int origin, float maxLength)
        {
            if (float.IsInfinity(maxLength)) return;
            Vector3[] p = _positions;
            float ox = p[origin].X, oy = p[origin].Y, oz = p[origin].Z;
            float dx = p[i].X - ox, dy = p[i].Y - oy, dz = p[i].Z - oz;
            float lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared <= maxLength * maxLength) return;
            float s = maxLength / MathF.Sqrt(lengthSquared);
            p[i].X = ox + dx * s;
            p[i].Y = oy + dy * s;
            p[i].Z = oz + dz * s;
        }

        // XPBD の距離の制約を、active の先頭 count 個について順に解く。
        // Unity の Mono では System.Numerics.Vector3 の演算子が1つずつ関数の呼び出しになり、成分ごとに書くより約3倍遅い。
        // 1制約ごとのメソッドの呼び出しも避けるため、ループの中に直接書いている
        void SolveDistances(int[] active, int count, int[] pairs, float[] rest, float[] lambda, float alpha)
        {
            Vector3[] p = _positions;
            float[] inverseMass = _inverseMass;
            for (int a = 0; a < count; a++)
            {
                int k = active[a];
                int i = pairs[2 * k], j = pairs[2 * k + 1];
                float wi = inverseMass[i], wj = inverseMass[j];
                float w = wi + wj;
                if (w == 0f) continue;

                float dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, dz = p[i].Z - p[j].Z;
                float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length < 1e-12f) continue;

                float deltaLambda = (-(length - rest[k]) - alpha * lambda[k]) / (w + alpha);
                lambda[k] += deltaLambda;

                float s = deltaLambda / length;
                dx *= s;
                dy *= s;
                dz *= s;
                p[i].X += wi * dx; p[i].Y += wi * dy; p[i].Z += wi * dz;
                p[j].X -= wj * dx; p[j].Y -= wj * dy; p[j].Z -= wj * dz;
            }
        }
    }
}
