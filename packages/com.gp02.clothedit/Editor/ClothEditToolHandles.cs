using UnityEditor;

namespace GP02.EditorTools
{
    /// <summary>
    /// 編集中は、Unity の道具のハンドル（移動の矢印など。<see cref="Tools.hidden"/>）を隠す。
    /// 選んだオブジェクト（衣装）のハンドルが布の上に出たままだと、それをつかんで衣装のオブジェクトごと動かしてしまうため。
    /// 隠すのは編集の間だけで、編集の終了・ウィンドウを閉じる・プレイモードに入る・スクリプトの再読み込みのときに、元の値へ戻す。
    /// GP02 自身のハンドル（「選んで動かす」の <c>Handles.PositionHandle</c> など）は、Tools.hidden に関係なく出る。
    /// 持ち主（ウィンドウ）を覚えて、持ち主が戻すときだけ戻す（別のウィンドウが隠したものを、勝手に戻さない）。
    /// </summary>
    public static class ClothEditToolHandles
    {
        static object _owner;
        static bool _savedHidden;

        /// <summary>今、GP02 が Unity の道具のハンドルを隠しているか。</summary>
        public static bool IsHiding => _owner != null;

        /// <summary>持ち主が隠している間は true。</summary>
        public static bool IsHidingFor(object owner) => owner != null && ReferenceEquals(_owner, owner);

        /// <summary>
        /// 編集中かどうかに合わせる。編集中なら隠し（元の値は最初に隠すときだけ覚える）、編集中でなければ、持ち主が隠していたときだけ戻す。
        /// 毎回の更新から呼んでよい（変わらなければ何もしない）。
        /// </summary>
        public static void Sync(object owner, bool editing)
        {
            if (owner == null) return;
            if (editing) Hide(owner);
            else Restore(owner);
        }

        /// <summary>道具のハンドルを隠す。すでに誰かが隠していれば、何もしない。</summary>
        public static void Hide(object owner)
        {
            if (owner == null || _owner != null) return;
            _owner = owner;
            _savedHidden = Tools.hidden;
            Tools.hidden = true;
        }

        /// <summary>隠す前の値へ戻す。持ち主でなければ、何もしない。</summary>
        public static void Restore(object owner)
        {
            if (owner == null || !ReferenceEquals(_owner, owner)) return;
            _owner = null;
            Tools.hidden = _savedHidden;
        }
    }
}
