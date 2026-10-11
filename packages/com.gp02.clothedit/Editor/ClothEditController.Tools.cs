using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    /// <summary>
    /// 対称の補助（鏡の面の自動の位置合わせ・相手のいない点・今の形を左右対称にする）、視点の向き、3D カーソル。
    /// 画面に依存しない部分なので、EditMode テストから動かせる。
    /// </summary>
    public sealed partial class ClothEditController
    {
        // 視点（シーンビューのカメラ）の向き。ウィンドウが SetViewRotation で渡す
        Quaternion _viewRotation = Quaternion.identity;

        // 「動かす向き」が視線のときの、つまんだときのカメラの向き
        bool _dragAlongView;
        Vector3 _dragCameraForward = Vector3.forward;
        Vector3 _dragCameraUp = Vector3.up;

        // 3D カーソル
        bool _hasCursor;
        Vector3 _cursorPosition;
        Vector3 _cursorNormal = Vector3.up;

        // ---- 視点の向き ----

        /// <summary>
        /// 視点の向き（<see cref="HandleOrientation.View"/> のハンドルの向き）。X が画面の右、Y が画面の上、Z が奥。
        /// <see cref="SetViewRotation"/> で渡されるまでは単位。
        /// </summary>
        public Quaternion ViewRotation => _viewRotation;

        /// <summary>シーンビューのカメラの向きを渡す（ウィンドウがシーンビューのイベントごとに呼ぶ）。</summary>
        public void SetViewRotation(Quaternion rotation)
        {
            float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            _viewRotation = length > 1e-6f ? new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length) : Quaternion.identity;
        }

        /// <summary>
        /// 「動かす向き」が視線のときの動かし先。視点の平面の上のカーソルの点 planePoint の、画面の上下の動きの分だけ、
        /// つまんだ点 start を視線に沿って動かす（上へで奥へ、下へで手前へ）。動いた距離は、上下の動きと同じ長さ。
        /// </summary>
        public static Vector3 ViewDepthPoint(Vector3 start, Vector3 planePoint, Vector3 cameraForward, Vector3 cameraUp)
        {
            Vector3 forward = cameraForward.normalized;
            Vector3 up = (cameraUp - Vector3.Dot(cameraUp, forward) * forward).normalized;
            float vertical = Vector3.Dot(planePoint - start, up);
            return start + forward * vertical;
        }

        // 上の向きが渡されないときの代わり。ワールドの上から視線の成分を除いたもの（真上・真下を見ているときはワールドの前）
        static Vector3 ViewUpFallback(Vector3 forward)
        {
            Vector3 f = forward.normalized;
            Vector3 up = Vector3.up - Vector3.Dot(Vector3.up, f) * f;
            if (up.sqrMagnitude < 1e-6f) up = Vector3.forward - Vector3.Dot(Vector3.forward, f) * f;
            return up.normalized;
        }

        // ---- 3D カーソル ----

        /// <summary>3D カーソルを置いたか。</summary>
        public bool HasCursor => _hasCursor;

        /// <summary>3D カーソルの位置（ワールド）。置いていなければ原点。</summary>
        public Vector3 CursorPosition => _cursorPosition;

        /// <summary>3D カーソルを置いた面の向き（十字を描く面）。</summary>
        public Vector3 CursorNormal => _cursorNormal;

        /// <summary>レイが当たった布の面の上に、3D カーソルを置く（Shift＋右クリック）。当たらなければ何もせず false。</summary>
        public bool PlaceCursor(Ray ray)
        {
            if (!EnsureAlive()) return false;
            if (!_session.Raycast(ray, out Vector3 point, out Vector3 normal)) return false;
            PlaceCursorAt(point, normal);
            return true;
        }

        /// <summary>3D カーソルを、指定の位置に置く。</summary>
        public void PlaceCursorAt(Vector3 position, Vector3 normal)
        {
            _cursorPosition = position;
            _cursorNormal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
            _hasCursor = true;
            Notify();
        }

        /// <summary>3D カーソルを、選んだ点の中心（今の位置の平均）に置く。選んだ点が無ければ false。</summary>
        public bool PlaceCursorAtSelectionCenter()
        {
            if (_session == null || _selectionCount == 0) return false;
            NVector3 sum = NVector3.Zero;
            var positions = _session.Solver.Positions;
            for (int k = 0; k < _selectedList.Length; k++) sum += positions[_selectedList[k]];
            sum /= _selectedList.Length;
            Vector3 normal = _session.TryGetAverageNormal(_selectedList, out Vector3 n) ? n : Vector3.up;
            PlaceCursorAt(new Vector3(sum.X, sum.Y, sum.Z), normal);
            return true;
        }

        /// <summary>3D カーソルを消す（中心が「3D カーソル」のときは、選んだ点の中心に戻る）。</summary>
        public void ClearCursor()
        {
            _hasCursor = false;
            Notify();
        }

        // ---- 対称の補助 ----

        // 設定のうち、ソルバー以外（セッションの見せ方・鏡像の許容距離）に関わるものを反映する
        void ApplyViewSettings()
        {
            _session.MirrorTolerance = _settings.MirrorTolerance;
            _session.ShowGhost = _settings.ShowOriginalGhost;
            _mirroredVersion = -1;
        }

        /// <summary>
        /// 対称がオンのとき、鏡像の粒子が見つからない粒子の数（つまむと、その点では反対側が動かない）。対称がオフ・編集中でなければ 0。
        /// 面と許容距離が同じ間は数え直さない。
        /// </summary>
        public int UnmatchedMirrorCount
        {
            get
            {
                if (_session == null || !TryGetMirrorPlane(out MirrorPlane plane)) return 0;
                return _session.CountUnmatchedMirror(plane, _settings.MirrorTolerance);
            }
        }

        /// <summary><see cref="UnmatchedMirrorCount"/> の、粒子全体に対する割合（0〜1）。</summary>
        public float UnmatchedMirrorRatio => _session == null || _session.ParticleCount == 0 ? 0f : UnmatchedMirrorCount / (float)_session.ParticleCount;

        /// <summary>
        /// 鏡の面を自動で合わせる。元の形の粒子を、設定の軸で鏡に映したときに一番よく重なる面を探し、そのずれを
        /// <see cref="ClothEditSettings.SymmetryOffset"/>（アバターのローカル座標）に入れる。対称がオフでも使える。
        /// 衣装がアバターの中心からずれていても、衣装そのものの左右対称の面で対称編集できるようにするため。
        /// 設定は保存しない（ウィンドウが保存する）。合わせられたら true。message に結果の案内（利用者向け）を入れる。
        /// </summary>
        public bool AutoFitSymmetryPlane(out string message)
        {
            if (!EnsureAlive() || _avatarRoot == null)
            {
                message = L10n.Tr("編集を始めてから押してください。", "Start editing first.");
                return false;
            }
            Vector3 axis = ClothEditSymmetry.LocalAxis(_settings.SymmetryAxis);
            Vector3 normal = _avatarRoot.TransformDirection(axis);
            if (normal.sqrMagnitude < 1e-12f)
            {
                message = L10n.Tr("アバターの向きから軸を決められませんでした（スケールが 0 の可能性があります）。", "Could not determine the axis from the avatar (its scale may be 0).");
                return false;
            }
            normal.Normalize();
            Vector3 origin = _avatarRoot.position;
            float matchDistance = Mathf.Max(_settings.MirrorTolerance, 2f * _session.Cloth.Spacing);
            float offset = _session.FitMirrorOffset(normal, origin, matchDistance, out float matched);
            Vector3 local = _avatarRoot.InverseTransformPoint(origin + normal * offset);
            // 粒子の並びの差で 0.1mm より細かい値は意味が無いので丸める（ウィンドウの表示も読みやすくなる）
            float localOffset = Mathf.Round(Vector3.Dot(local, axis) * 10000f) / 10000f;
            if (Mathf.Abs(localOffset) > ClothEditSettings.MaxSymmetryOffset)
            {
                message = L10n.Tr($"見つかった面が中心から {ClothEditSettings.MaxSymmetryOffset * 100f:F0}cm 以上離れているので、使いませんでした。軸を確かめてください。",
                    $"The plane found is more than {ClothEditSettings.MaxSymmetryOffset * 100f:F0} cm from the center, so it was not used. Check the axis.");
                return false;
            }
            _settings.SymmetryOffset = localOffset;
            ApplySettings();
            message = L10n.Tr($"鏡の面を合わせました（中心のずれ {localOffset * 100f:F1}cm、鏡に映して重なった割合 {matched * 100f:F0}%）。",
                $"Fitted the mirror plane (center offset {localOffset * 100f:F1} cm, {matched * 100f:F0}% of the mirrored points overlap).");
            return true;
        }

        /// <summary>
        /// 今の形を左右対称にする。<see cref="ClothEditSettings.SymmetrizeSource"/> の側の変形（元の形からの移動）を、鏡に映して反対側へ写し、
        /// そのあと仕上げ（以降の <see cref="Tick"/>）で伸びを直す。Unity の Undo の1段。鏡の面は対称の設定（軸とずれ）で、対称がオフでも使える。
        /// 位置を変えた粒子の数を返す（0 なら何も記録しない）。
        /// </summary>
        public int SymmetrizeShape()
        {
            if (!EnsureAlive()) return 0;
            if (!ClothEditSymmetry.TryBuildMirrorPlane(_settings, _avatarRoot, true, out MirrorPlane plane)) return 0;
            FinishStroke();
            BeginOperation(L10n.Tr("GP02 左右対称にする", "GP02 Symmetrize"));
            int changed = _session.SymmetrizeShape(plane, _settings.MirrorTolerance, _settings.SymmetrizeSource == SymmetrySourceSide.Positive);
            if (changed == 0)
            {
                CommitOperation();
                return 0;
            }
            // 写した所の継ぎ目の伸びを、仕上げで直してから確定する
            _settling = true;
            _settleClockPending = true;
            InvalidatePreview();
            Notify();
            return changed;
        }
    }
}
