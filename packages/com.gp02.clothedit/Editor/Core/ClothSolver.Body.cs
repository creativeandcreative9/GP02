using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    // 体（BodySurface）との一方向の制約。布の粒子が体の中へ入らないようにする（体の外へは自由に動ける）。
    //
    // 粒子ごとに、体の表面の一番近い点で、符号付き距離を1次で近似した平面 dot(p - c, n) >= 下限 を持ち、反復の最後に解く。
    // 下限は「余白（既定 2mm）」だが、今すでに体に入っている・近すぎる粒子は、その時点の距離を下限にする（それより悪くしない）。
    // 体から離れる向きに動いたら下限を上げ（余白まで）、「貫通を直す」（RequireBodyClearance）で余白まで上げる。
    // 編集を始めただけで、あるいは別の所をつまんだだけで、勝手に押し出さないため。
    //
    // 平面は、粒子が前に調べた位置から一定以上動いたときだけ調べ直す（体の検索は C# で、毎回すべての粒子を調べると重い）。
    // 距離の関数は1だけしか変わらない（動いた距離より速く体に近づけない）ので、体から d 離れた粒子は、
    // d - 下限 の半分を動くまで調べ直さなくても体には届かない。体の近く（2cm 以内）の粒子だけ平面を解き、5mm 動いたら調べ直す。
    // 平面に触れている（下限の近くにいる）粒子は、面に沿って滑ると、くぼんだ所で平面が体の表面からずれて体に入るので、1mm 動いたら調べ直す
    // （半径 R のくぼみで、s 滑ったときのずれは約 s²/2R。1mm なら半径 5mm のくぼみでも 0.1mm）。
    public sealed partial class ClothSolver
    {
        /// <summary>体から保つ余白（メートル）の既定値。</summary>
        public const float DefaultBodyMargin = 0.002f;

        // 体からこの範囲（下限との差）にある粒子だけ、平面を解く
        const float BodyBand = 0.02f;
        // 平面を解いている粒子が、前に調べた位置からこれだけ動いたら調べ直す
        const float BodyRefreshDistance = 0.005f;
        // 平面に触れている（平面までの距離が下限からこれ以内の）粒子は、これだけ動いたら調べ直す
        const float BodyContactSlack = 0.002f;
        const float BodyContactRefreshDistance = 0.001f;
        // 「直す」で足した平面（体の尖った所が粒子の間から出ていた所）は、平面の点から面に沿ってこれより離れたら解かない
        const float BodyExtraReach = 0.03f;

        const byte BodyStale = 1;      // 次の Solve で調べ直す
        const byte BodyReset = 2;      // 調べ直すときに、下限を今の距離にする（元に戻した直後など）

        BodySurface _body;
        float _bodyMargin = DefaultBodyMargin;
        float _bodyMaxDistance = BodySurface.DefaultMaxDistance;
        Vector3[] _bodyQueryAt;        // 最後に調べた位置（内部の座標）
        float[] _bodyDistance;         // そのときの符号付き距離（体が見つからなければ +∞）
        Vector3[] _bodyPoint;          // 平面の点（体の表面の一番近い点、内部の座標）
        Vector3[] _bodyNormal;         // 平面の向き（距離の勾配）
        float[] _bodyMin;              // 保つ符号付き距離の下限（余白以下）
        float[] _bodyBudgetSq;         // 前に調べた位置から、この距離（の2乗）より動いたら調べ直す
        byte[] _bodyFlags;
        bool[] _bodyHasPlane;
        bool[] _bodyHasExtra;          // 「直す」で足した平面（下限は余白）
        Vector3[] _bodyExtraPoint;
        Vector3[] _bodyExtraNormal;
        int _bodyExtraCount;

        // カーネルに渡す平面の一覧（Solve ごとに作り直す）
        int _bodyPlaneCount;
        int[] _bodyPlaneParticle;
        Vector3[] _bodyPlanePoint;
        Vector3[] _bodyPlaneNormal;
        float[] _bodyPlaneMin;

        /// <summary>体の表面（<see cref="SetBody"/>）。無ければ null（体との制約は解かない）。</summary>
        public BodySurface Body => _body;

        /// <summary>
        /// 体から保つ余白（メートル）。粒子は体の表面からこの距離より内へ入らない（今すでに近い粒子は、今の距離より内へ入らない）。
        /// 変えても、今の距離より外へは押し出さない（余白を広げても、すでに余白より近い粒子は今の距離のまま）。
        /// </summary>
        public float BodyMargin
        {
            get => _bodyMargin;
            set
            {
                if (!(value >= 0f) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
                if (value == _bodyMargin) return;
                _bodyMargin = value;
                if (_body == null) return;
                for (int i = 0; i < _bodyMin.Length; i++) _bodyMin[i] = MathF.Min(value, MathF.Max(_bodyMin[i], _bodyDistance[i]));
            }
        }

        /// <summary>体を探す距離（メートル）。これより遠い体の表面は見ない（閉じていない体の、穴の向こうを中と取り違えないため）。</summary>
        public float BodyMaxDistance
        {
            get => _bodyMaxDistance;
            set
            {
                if (!(value > 0f) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
                _bodyMaxDistance = value;
                if (_body != null) MarkAllBodyStale(false);
            }
        }

        /// <summary>直前の Solve で解いた、体との平面の数。</summary>
        public int BodyPlaneCount => _bodyPlaneCount;

        /// <summary>
        /// 体の表面を設定する（null で外す）。すべての粒子について今の位置で体を調べ、下限を「余白」と「今の距離」の小さいほうにする
        /// （今すでに体に入っている所は、その深さのまま。押し出すのは <see cref="RequireBodyClearance"/> のあと）。
        /// </summary>
        public void SetBody(BodySurface body)
        {
            _body = body;
            _bodyPlaneCount = 0;
            ClearBodyExtraPlanes();
            if (body == null) return;
            EnsureBodyArrays();
            for (int i = 0; i < _positions.Length; i++) QueryBody(i, true);
        }

        void EnsureBodyArrays()
        {
            if (_bodyQueryAt != null) return;
            int n = _positions.Length;
            _bodyQueryAt = new Vector3[n];
            _bodyDistance = new float[n];
            _bodyPoint = new Vector3[n];
            _bodyNormal = new Vector3[n];
            _bodyMin = new float[n];
            _bodyBudgetSq = new float[n];
            _bodyFlags = new byte[n];
            _bodyHasPlane = new bool[n];
            _bodyHasExtra = new bool[n];
            _bodyExtraPoint = new Vector3[n];
            _bodyExtraNormal = new Vector3[n];
            _bodyPlaneParticle = new int[2 * n];
            _bodyPlanePoint = new Vector3[2 * n];
            _bodyPlaneNormal = new Vector3[2 * n];
            _bodyPlaneMin = new float[2 * n];
        }

        /// <summary>
        /// 粒子の下限を余白まで上げる（「貫通を直す」）。次の Solve から、体に入っている・近すぎる粒子が体の外（余白）へ押し出される。
        /// 体が無ければ何もしない。範囲外の番号は無視する。
        /// </summary>
        public void RequireBodyClearance(IReadOnlyList<int> particles)
        {
            if (_body == null || particles == null) return;
            int n = _positions.Length;
            for (int k = 0; k < particles.Count; k++)
            {
                int i = particles[k];
                if ((uint)i < (uint)n) _bodyMin[i] = _bodyMargin;
            }
        }

        /// <summary>
        /// 粒子に、体との平面をもう1つ足す（下限は余白）。体の尖った所が粒子の間から布の外へ出ているとき、
        /// 粒子そのものは体の外にあるので、尖った所（体の頂点）を通る平面で、周りの粒子を外へ出す。
        /// 平面の点から面に沿って 3cm より離れたら解かない。<see cref="SetPositions"/>・<see cref="ResetToRest"/>・<see cref="SetBody"/> で消え、
        /// つまんで（<see cref="Grab(int, Vector3, float)"/> など）解くと、すべて消える。
        /// 体が無くても使える（動作チェックで見つかった所を外へ逃がすとき。今の姿勢では体が近くに無いこともある）。
        /// </summary>
        /// <param name="point">平面の点（ワールド座標）</param>
        /// <param name="normal">平面の外向き（長さ1にそろえる）</param>
        public void AddBodyExtraPlane(int particle, Vector3 point, Vector3 normal)
        {
            if ((uint)particle >= (uint)_positions.Length) return;
            float length = normal.Length();
            if (!(length > 1e-12f)) return;
            EnsureBodyArrays();
            if (!_bodyHasExtra[particle]) _bodyExtraCount++;
            _bodyHasExtra[particle] = true;
            _bodyExtraPoint[particle] = point - _origin;
            _bodyExtraNormal[particle] = normal / length;
        }

        /// <summary><see cref="AddBodyExtraPlane"/> で足した平面を、すべて消す。</summary>
        public void ClearBodyExtraPlanes()
        {
            if (_bodyHasExtra == null || _bodyExtraCount == 0) return;
            Array.Clear(_bodyHasExtra, 0, _bodyHasExtra.Length);
            _bodyExtraCount = 0;
        }

        /// <summary>次の Solve で、すべての粒子の平面を今の位置で調べ直す（下限は変えない）。</summary>
        public void InvalidateBodyPlanes()
        {
            if (_body != null) MarkAllBodyStale(false);
        }

        /// <summary>足した平面の数。</summary>
        public int BodyExtraPlaneCount => _bodyExtraCount;

        /// <summary>粒子の、体から保つ符号付き距離の下限（メートル）。体が無ければ 0。</summary>
        public float GetBodyMinDistance(int particle) => _body != null ? _bodyMin[particle] : 0f;

        /// <summary>
        /// 直前の Solve で解いた平面のうち、一番破れているもの（下限 - 平面からの距離、メートル。破れていなければ 0）。
        /// 動かない粒子（ピン留め・計算範囲の外）は数えない。「貫通を直す」の仕上げの終わりを決めるのに使う。
        /// </summary>
        public float MeasureBodyViolation()
        {
            float worst = 0f;
            Vector3[] p = _positions;
            for (int k = 0; k < _bodyPlaneCount; k++)
            {
                int i = _bodyPlaneParticle[k];
                if (_inverseMass[i] == 0f) continue;
                Vector3 c = _bodyPlanePoint[k], nrm = _bodyPlaneNormal[k];
                float d = (p[i].X - c.X) * nrm.X + (p[i].Y - c.Y) * nrm.Y + (p[i].Z - c.Z) * nrm.Z;
                float violation = _bodyPlaneMin[k] - d;
                if (violation > worst) worst = violation;
            }
            return worst;
        }

        // 粒子 i の今の位置で体を調べ、平面と下限と、次に調べ直すまでの距離を決める
        void QueryBody(int i, bool reset)
        {
            Vector3 p = _positions[i];
            float wx = p.X + _origin.X, wy = p.Y + _origin.Y, wz = p.Z + _origin.Z;
            float d = float.PositiveInfinity;
            if (_body.TryQuery(new Vector3(wx, wy, wz), _bodyMaxDistance, out BodyHit hit))
            {
                d = hit.SignedDistance;
                float ox = wx - hit.Point.X, oy = wy - hit.Point.Y, oz = wz - hit.Point.Z;
                float length = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                // 平面の向きは距離の勾配（表面の点から粒子へ、中なら粒子から表面へ）。表面の上で向きが決まらなければ表面の法線
                Vector3 normal = length > 1e-6f
                    ? (d >= 0f ? new Vector3(ox / length, oy / length, oz / length) : new Vector3(-ox / length, -oy / length, -oz / length))
                    : hit.Normal;
                _bodyHasPlane[i] = true;
                _bodyPoint[i] = new Vector3(hit.Point.X - _origin.X, hit.Point.Y - _origin.Y, hit.Point.Z - _origin.Z);
                _bodyNormal[i] = normal;
            }
            else
            {
                _bodyHasPlane[i] = false;
            }
            _bodyDistance[i] = d;
            if (reset || (_bodyFlags[i] & BodyReset) != 0) _bodyMin[i] = MathF.Min(_bodyMargin, d);
            else _bodyMin[i] = MathF.Min(_bodyMargin, MathF.Max(_bodyMin[i], d));
            _bodyQueryAt[i] = p;
            _bodyFlags[i] = 0;

            float slack = _bodyHasPlane[i] ? d - _bodyMin[i] : _bodyMaxDistance;
            float budget = slack < BodyBand ? BodyRefreshDistance : MathF.Max(BodyRefreshDistance, 0.5f * MathF.Min(slack, _bodyMaxDistance));
            _bodyBudgetSq[i] = budget * budget;
        }

        void MarkAllBodyStale(bool reset)
        {
            byte flag = reset ? (byte)(BodyStale | BodyReset) : BodyStale;
            for (int i = 0; i < _bodyFlags.Length; i++) _bodyFlags[i] |= flag;
        }

        // 粒子の位置を外から置き換えたとき（元に戻す・やり直す・すべて元に戻す）。下限を今の距離にして、勝手に押し出さないようにする。
        // 位置が変わっていない粒子は、前に調べた距離のまま
        void ResetBodyRatchet()
        {
            ClearBodyExtraPlanes();
            if (_body == null) return;
            Vector3[] p = _positions;
            for (int i = 0; i < p.Length; i++)
            {
                float dx = p[i].X - _bodyQueryAt[i].X, dy = p[i].Y - _bodyQueryAt[i].Y, dz = p[i].Z - _bodyQueryAt[i].Z;
                if (dx * dx + dy * dy + dz * dz <= 1e-12f && _bodyFlags[i] == 0) _bodyMin[i] = MathF.Min(_bodyMargin, _bodyDistance[i]);
                else _bodyFlags[i] |= BodyStale | BodyReset;
            }
        }

        // 計算範囲の粒子について、動いた粒子の平面を調べ直し、カーネルに渡す平面の一覧を作る（Solve の反復の前に1回）
        void RefreshBodyPlanes()
        {
            _bodyPlaneCount = 0;
            if (_body == null && _bodyExtraCount == 0) return;
            // 直したあとに、またつまんで形を変え始めたら、直したときに足した平面（空間に固定）は合わなくなる。
            // 残すと、つまみの周りの布が平面とつまみに引っ張り合われて大きく伸びるので、すべて外す
            if (_grabbed >= 0 && _bodyExtraCount > 0) ClearBodyExtraPlanes();
            bool hasBody = _body != null;
            if (!hasBody && _bodyExtraCount == 0) return;
            Vector3[] p = _positions;
            for (int a = 0; a < _activeParticleCount; a++)
            {
                int i = _activeParticles[a];
                if (hasBody)
                {
                    float dx = p[i].X - _bodyQueryAt[i].X, dy = p[i].Y - _bodyQueryAt[i].Y, dz = p[i].Z - _bodyQueryAt[i].Z;
                    float moved = dx * dx + dy * dy + dz * dz;
                    bool stale = _bodyFlags[i] != 0 || moved > _bodyBudgetSq[i];
                    if (!stale && _bodyHasPlane[i] && moved > BodyContactRefreshDistance * BodyContactRefreshDistance)
                    {
                        Vector3 c = _bodyPoint[i], nrm = _bodyNormal[i];
                        float onPlane = (p[i].X - c.X) * nrm.X + (p[i].Y - c.Y) * nrm.Y + (p[i].Z - c.Z) * nrm.Z - _bodyMin[i];
                        stale = onPlane < BodyContactSlack;
                    }
                    if (stale) QueryBody(i, false);

                    if (_bodyHasPlane[i] && _bodyDistance[i] - _bodyMin[i] < BodyBand)
                    {
                        int k = _bodyPlaneCount++;
                        _bodyPlaneParticle[k] = i;
                        _bodyPlanePoint[k] = _bodyPoint[i];
                        _bodyPlaneNormal[k] = _bodyNormal[i];
                        _bodyPlaneMin[k] = _bodyMin[i];
                    }
                }
                if (_bodyExtraCount > 0 && _bodyHasExtra[i])
                {
                    Vector3 c = _bodyExtraPoint[i], nrm = _bodyExtraNormal[i];
                    float vx = p[i].X - c.X, vy = p[i].Y - c.Y, vz = p[i].Z - c.Z;
                    float along = vx * nrm.X + vy * nrm.Y + vz * nrm.Z;
                    if (vx * vx + vy * vy + vz * vz - along * along > BodyExtraReach * BodyExtraReach) continue;
                    int k = _bodyPlaneCount++;
                    _bodyPlaneParticle[k] = i;
                    _bodyPlanePoint[k] = c;
                    _bodyPlaneNormal[k] = nrm;
                    _bodyPlaneMin[k] = _bodyMargin;
                }
            }
            ProjectBrushGoalsOutsideBody();
        }

        // つまんだ範囲の目標が体の中（平面より内）にあれば、平面の上へ移す（RefreshBodyPlanes の最後に呼ぶ）。
        // 柔らかいつまみは、届かない目標のままだと体との平面と引っ張り合い続け、周りの布が大きく伸びる。
        // 固いつまみ（柔らかさ 0）の粒子は動かない扱いで平面を解かないので、粒子もそこへ置く。
        // （目標の位置で体を調べて一番近い表面へ移すと、深く押し込んだときに隣どうしの目標が離れた所へ飛んで、かえって伸びた）
        void ProjectBrushGoalsOutsideBody()
        {
            if (_grabbed < 0) return;
            bool soft = _brushCompliance > 0f;
            for (int k = 0; k < _bodyPlaneCount; k++)
            {
                int i = _bodyPlaneParticle[k];
                if (!_inBrush[i] || _pinned[i]) continue;
                Vector3 c = _bodyPlanePoint[k], nrm = _bodyPlaneNormal[k];
                Vector3 g = _brushGoal[i];
                float d = (g.X - c.X) * nrm.X + (g.Y - c.Y) * nrm.Y + (g.Z - c.Z) * nrm.Z - _bodyPlaneMin[k];
                if (d >= 0f) continue;
                g.X -= d * nrm.X;
                g.Y -= d * nrm.Y;
                g.Z -= d * nrm.Z;
                _brushGoal[i] = g;
                if (!soft) _positions[i] = g;
            }
        }

        // 体との平面を順に解く（反復の最後。動かない粒子は動かさない）
        void SolveBodyPlanes()
        {
            Vector3[] p = _positions;
            float[] inverseMass = _inverseMass;
            for (int k = 0; k < _bodyPlaneCount; k++)
            {
                int i = _bodyPlaneParticle[k];
                if (inverseMass[i] == 0f) continue;
                Vector3 c = _bodyPlanePoint[k], nrm = _bodyPlaneNormal[k];
                float d = (p[i].X - c.X) * nrm.X + (p[i].Y - c.Y) * nrm.Y + (p[i].Z - c.Z) * nrm.Z - _bodyPlaneMin[k];
                if (d >= 0f) continue;
                p[i].X -= d * nrm.X;
                p[i].Y -= d * nrm.Y;
                p[i].Z -= d * nrm.Z;
            }
        }
    }
}
