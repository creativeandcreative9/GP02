using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 選んだ点をシーンビューに点で見せるための、間引いた粒子の番号のキャッシュ。
    /// 選択が変わったとき（<see cref="ClothEditController.SelectionVersion"/>）と、対称の設定で鏡像の側が変わったときだけ作り直し、
    /// 描くたびには番号の配列を読むだけにする（毎コマの確保や、全粒子の走査をしない）。位置は形が変わっても最新になるよう、描くときに読む。
    /// 画面に依存しないので、テストで確かめられる。
    /// </summary>
    public sealed class ClothEditSelectionDisplay
    {
        /// <summary>描く、選んだ点の上限。超えるときは等間隔に間引く。</summary>
        public const int MaxSelectedDots = 3000;

        /// <summary>描く、鏡像の側の点の上限。</summary>
        public const int MaxMirroredDots = 1500;

        readonly int[] _selected = new int[MaxSelectedDots];
        readonly int[] _mirrored = new int[MaxMirroredDots];
        int _selectedCount;
        int _mirroredCount;
        ClothEditSession _session;
        int _version = -1;
        int _mirroredStamp = -1;
        int _labelCount = -1;
        UiLanguage _labelLanguage;
        string _label = "";

        /// <summary>作り直した回数（テスト用）。</summary>
        public int RebuildCount { get; private set; }

        /// <summary>描く、選んだ点の数。</summary>
        public int SelectedCount => _selectedCount;

        /// <summary>描く、鏡像の側の点の数。</summary>
        public int MirroredCount => _mirroredCount;

        /// <summary>k 番目の、選んだ点の粒子の番号。</summary>
        public int SelectedParticle(int k) => _selected[k];

        /// <summary>k 番目の、鏡像の側の粒子の番号。</summary>
        public int MirroredParticle(int k) => _mirrored[k];

        /// <summary>選んだ点の数の文字。数か言語が変わったときだけ作り直す。</summary>
        public string CountLabel => _label;

        /// <summary>
        /// 選択が変わっていれば作り直す。描く前に毎回呼んでよい（変わっていなければ何もしない）。
        /// </summary>
        public void Update(ClothEditController controller)
        {
            ClothEditSession session = controller.Session;
            int version = controller.SelectionVersion;
            bool selectionChanged = !ReferenceEquals(session, _session) || version != _version;
            if (selectionChanged)
            {
                _session = session;
                _version = version;
                RebuildSelected(controller);
                _mirroredStamp = -1;   // 選択が変わったので、鏡像の側も読み直す
            }

            int stamp = session == null ? 0 : controller.MirroredSelectionStamp;
            if (selectionChanged || stamp != _mirroredStamp)
            {
                _mirroredStamp = stamp;
                RebuildMirrored(controller);
                RebuildCount++;
            }

            int count = controller.SelectionCount;
            if (count != _labelCount || L10n.Language != _labelLanguage)
            {
                _labelCount = count;
                _labelLanguage = L10n.Language;
                _label = ClothEditHelp.SelectionCountLabel(count);
            }
        }

        void RebuildSelected(ClothEditController controller)
        {
            _selectedCount = 0;
            if (_session == null) return;
            System.Collections.Generic.IReadOnlyList<int> selected = controller.SelectedParticles;
            int stride = Stride(selected.Count, MaxSelectedDots);
            for (int k = 0; k < selected.Count && _selectedCount < MaxSelectedDots; k += stride) _selected[_selectedCount++] = selected[k];
        }

        void RebuildMirrored(ClothEditController controller)
        {
            _mirroredCount = 0;
            if (_session == null) return;
            System.Collections.Generic.IReadOnlyList<int> mirrored = controller.MirroredSelection;
            int stride = Stride(mirrored.Count, MaxMirroredDots);
            for (int k = 0; k < mirrored.Count && _mirroredCount < MaxMirroredDots; k += stride) _mirrored[_mirroredCount++] = mirrored[k];
        }

        static int Stride(int count, int max)
        {
            int stride = (count + max - 1) / max;
            return Mathf.Max(1, stride);
        }
    }
}
