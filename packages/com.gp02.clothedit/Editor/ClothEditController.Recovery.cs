using System;
using System.Collections.Generic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// 編集が消えないための部分: 保存していない編集があるかの判定、点の選択の設定、取っておいた状態（粒子の位置・ピン・選択）の復元。
    /// 画面に依存しないので、EditMode テストから動かせる。
    /// </summary>
    public sealed partial class ClothEditController
    {
        // 位置がこの距離（メートル）以内なら「保存した形と同じ」とみなす。Undo で戻したあとの丸め誤差を許す
        const float CleanTolerance = 1e-5f;

        // 「保存済み」とみなす粒子の位置（x, y, z, …）。編集を始めた直後・保存した直後・保存したシェイプキーを読み込んだ直後の形
        float[] _cleanPositions;

        /// <summary>
        /// 保存していない編集があるか。粒子の位置が、編集を始めた形（または最後に保存した形、保存したシェイプキーを読み込んだ形）から変わっているとき。
        /// ピンと選んだ点だけの変更は含めない（形が変わっていなければ、終えても失うものは小さいため）。編集中でなければ false。
        /// </summary>
        public bool HasUnsavedEdits
        {
            get
            {
                if (_session == null) return false;
                ReadOnlySpan<NVector3> positions = _session.Solver.Positions;
                float[] clean = _cleanPositions;
                if (clean == null || clean.Length != 3 * positions.Length) return false;
                for (int i = 0; i < positions.Length; i++)
                {
                    NVector3 p = positions[i];
                    if (Math.Abs(p.X - clean[3 * i]) > CleanTolerance || Math.Abs(p.Y - clean[3 * i + 1]) > CleanTolerance
                        || Math.Abs(p.Z - clean[3 * i + 2]) > CleanTolerance)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>編集を終える・別の衣装に切り替えるときに、確認が要るか（保存していない編集があるか）。</summary>
        public bool NeedsLeaveConfirmation => HasUnsavedEdits;

        // 今の形を「保存済み」の基準にする
        void MarkClean()
        {
            if (_session == null)
            {
                _cleanPositions = null;
                return;
            }
            ReadOnlySpan<NVector3> positions = _session.Solver.Positions;
            if (_cleanPositions == null || _cleanPositions.Length != 3 * positions.Length) _cleanPositions = new float[3 * positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                _cleanPositions[3 * i] = positions[i].X;
                _cleanPositions[3 * i + 1] = positions[i].Y;
                _cleanPositions[3 * i + 2] = positions[i].Z;
            }
        }

        /// <summary>
        /// 選んだ点を、渡した粒子の番号にする（範囲外の番号は無視する）。選んだ数を返す。編集中でなければ 0。
        /// 取っておいた選択を戻すのに使う。選ぶ操作は Unity の Undo には入らない。
        /// </summary>
        public int SetSelection(IReadOnlyList<int> particles)
        {
            if (!EnsureAlive()) return 0;
            FinishSelectionTransform();
            Array.Clear(_selected, 0, _selected.Length);
            int count = 0;
            if (particles != null)
            {
                for (int k = 0; k < particles.Count; k++)
                {
                    int i = particles[k];
                    if ((uint)i >= (uint)_selected.Length || _selected[i]) continue;
                    _selected[i] = true;
                    count++;
                }
            }
            CommitSelection();
            return count;
        }

        /// <summary>
        /// 粒子の位置・ピン・選んだ点を、取っておいた状態に戻す。Unity の Undo の1段になる（Ctrl+Z で戻す前の形に戻る）。
        /// 粒子の数が合わなければ何もせず false（呼び出し側が <see cref="ClothEditStash.MatchesSession"/> で、同じ布であることを確かめておくこと）。
        /// </summary>
        /// <param name="positions">粒子の位置（x, y, z, …。ワールド）</param>
        public bool RestoreEditState(float[] positions, IReadOnlyList<int> pinnedParticles, IReadOnlyList<int> selectedParticles)
        {
            if (!EnsureAlive() || positions == null) return false;
            int count = _session.ParticleCount;
            if (positions.Length != 3 * count) return false;

            FinishStroke();
            ClothEditState restored = ClothEditState.Create();
            try
            {
                restored.Positions = positions;
                restored.Pinned = new bool[count];
                if (pinnedParticles != null)
                {
                    for (int k = 0; k < pinnedParticles.Count; k++)
                    {
                        int i = pinnedParticles[k];
                        if ((uint)i < (uint)count) restored.Pinned[i] = true;
                    }
                }
                BeginOperation(L10n.Tr("GP02 前回の編集を戻す", "GP02 Restore Previous Edit"));
                bool ok = _session.ReadState(restored);
                CommitOperation();
                if (!ok) return false;
            }
            finally
            {
                Object.DestroyImmediate(restored);
            }
            SetSelection(selectedParticles);
            AfterShapeChanged();
            return true;
        }
    }
}
