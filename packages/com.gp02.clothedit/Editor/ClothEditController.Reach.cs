using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// 届かないときの見える化。「つまむ」と「選んで動かす」で、引いている先と、つまんだ点（選んだ点の中心）の今の位置との距離を返す。
    /// 布の長さで届かない所へ引くと、つまんだ点は手前で止まるので、この距離が開く（<see cref="ClothEditReach"/> が、開いたとみなす基準）。
    /// 画面は、この値を見て線と一言を出す。画面に依存しないので、EditMode テストから確かめられる。
    /// </summary>
    public sealed partial class ClothEditController
    {
        enum ReachKind
        {
            None,

            /// <summary>1点（ブラシの中心）でつまんでいる。引いている先は <see cref="_dragTarget"/>。</summary>
            Grab,

            /// <summary>選んだ点を動かしている。引いている先は、選んだ点の中心を動きで動かした位置。</summary>
            Set,
        }

        ReachKind _reachKind;
        int _reachParticle;
        NVector3 _reachGrabOffset;     // 押した点（面の上）から、つまんだ粒子までの差
        NVector3 _reachStart;          // 押した点（つまむ）、または動かし始めの選んだ点の中心
        BrushMotion _reachMotion;      // 選んだ点を動かす動き（最後に受け取ったもの）

        // つまみ始め（「つまむ」だけ。回す・拡大縮小は対象にしない）
        void BeginReachGrab(int particle, Vector3 pressedPoint)
        {
            _reachKind = ReachKind.Grab;
            _reachParticle = particle;
            NVector3 p = _session.Solver.Positions[particle];
            _reachStart = new NVector3(pressedPoint.x, pressedPoint.y, pressedPoint.z);
            _reachGrabOffset = _reachStart - p;
        }

        // 選んだ点を動かし始め
        void BeginReachSet()
        {
            _reachKind = ReachKind.Set;
            _reachStart = MeanOfSelection();
            _reachMotion = default;
        }

        void ClearReach()
        {
            _reachKind = ReachKind.None;
        }

        NVector3 MeanOfSelection()
        {
            var sum = NVector3.Zero;
            if (_session == null || _selectedList.Length == 0) return sum;
            System.ReadOnlySpan<NVector3> positions = _session.Solver.Positions;
            for (int k = 0; k < _selectedList.Length; k++) sum += positions[_selectedList[k]];
            return sum / _selectedList.Length;
        }

        /// <summary>引いている間（仕上げの間も）、引いている先と今の位置を測っているか。</summary>
        public bool IsReachTracking => _session != null && _reachKind != ReachKind.None && (_dragging || _settling);

        /// <summary>引いている先（ワールド）。測っていなければ原点。</summary>
        public Vector3 ReachTarget
        {
            get
            {
                if (!IsReachTracking) return Vector3.zero;
                if (_reachKind == ReachKind.Grab) return _dragTarget;
                NVector3 goal = _reachMotion.Apply(_reachStart);
                return new Vector3(goal.X, goal.Y, goal.Z);
            }
        }

        /// <summary>つまんだ点（選んだ点なら、その中心）の今の位置（ワールド）。測っていなければ原点。</summary>
        public Vector3 ReachCurrent
        {
            get
            {
                if (!IsReachTracking) return Vector3.zero;
                NVector3 current = _reachKind == ReachKind.Grab
                    ? _session.Solver.Positions[_reachParticle] + _reachGrabOffset
                    : MeanOfSelection();
                return new Vector3(current.X, current.Y, current.Z);
            }
        }

        /// <summary>
        /// 引いている先と、つまんだ点の今の位置との距離（メートル）。0 に近ければ、手の動きに付いてきている。
        /// 布の長さで届かない所へ引くと開く。測っていなければ 0。
        /// </summary>
        public float ReachDistance => IsReachTracking ? Vector3.Distance(ReachTarget, ReachCurrent) : 0f;

        /// <summary>引いた量（つまみ始めの位置から、引いている先までの距離。メートル）。測っていなければ 0。</summary>
        public float ReachPulled
        {
            get
            {
                if (!IsReachTracking) return 0f;
                Vector3 start = new Vector3(_reachStart.X, _reachStart.Y, _reachStart.Z);
                return Vector3.Distance(ReachTarget, start);
            }
        }

        /// <summary>布の長さが足りなくて、引いている先に届いていないか（<see cref="ClothEditReach.IsOutOfReach"/>）。</summary>
        public bool IsOutOfReach => IsReachTracking && ClothEditReach.IsOutOfReach(ReachDistance, ReachPulled);
    }
}
