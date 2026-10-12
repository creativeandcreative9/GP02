using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 画面の見せ方。かんたん表示（既定）は主な操作だけ、くわしく表示は機能をすべて出す。
    /// 布の編集ウィンドウ・シーンビューの道具バー・シェイプキーの一覧が、同じ切り替えを使う。
    /// </summary>
    public enum ClothEditViewMode
    {
        Simple = 0,
        Detailed = 1,
    }

    /// <summary>
    /// 画面の部品の種類。かんたん表示でも出すものは <see cref="ClothEditView.SimpleItems"/> に入れてあり、
    /// それ以外は「くわしく表示」のときだけ出す（機能は減らさず、見せ方だけを変える）。
    /// </summary>
    public enum ClothEditUiItem
    {
        // ---- 布の編集ウィンドウ ----

        /// <summary>手順の案内。</summary>
        Guide,

        /// <summary>対象の指定（オブジェクト欄と「選択中のオブジェクトから設定」）。</summary>
        TargetField,

        /// <summary>重ね着の追加（選択中のオブジェクトを追加・同じアバターの重ね着をまとめて追加）。</summary>
        TargetAddLayers,

        /// <summary>「編集を開始」。</summary>
        StartEditing,

        /// <summary>診断の重大・注意（あるときだけ出す）。</summary>
        DiagnosticsSevere,

        /// <summary>診断の情報（1行に畳んで、押すと開く）。</summary>
        DiagnosticsInfoLine,

        /// <summary>診断の見出し・「問題なし」・「点検し直す」。</summary>
        DiagnosticsPanel,

        ToolGrab,
        ToolSelect,
        ToolPin,

        /// <summary>元に戻すブラシ。</summary>
        ToolRelax,

        /// <summary>回すブラシ（キーは E のまま）。</summary>
        ToolRotate,

        /// <summary>拡大縮小のブラシ（キーは R のまま）。</summary>
        ToolScale,

        /// <summary>ピン留めの道具の「塗ると外す」。</summary>
        PinEraseToggle,

        /// <summary>道具ごとの細かい設定（動かす向き・回転軸・矩形は表側だけ・元に戻すブラシの強さ）。</summary>
        ToolOptions,

        Undo,
        Redo,

        /// <summary>すべて元に戻す・ピンをすべて外す・ピンを反転。</summary>
        HistoryExtras,

        /// <summary>ブラシの大きさ。</summary>
        BrushRadius,

        /// <summary>影響範囲・減衰カーブ・カーブの保存・距離の測り方・つながりの許容距離 ε・元に戻すブラシの強さ。</summary>
        BrushDetails,

        /// <summary>対称編集のオン／オフ。</summary>
        SymmetryToggle,

        /// <summary>「鏡の面を自動で合わせる」。</summary>
        SymmetryAutoFit,

        /// <summary>軸・中心のずれ・許容距離・相手のいない点・左右差・今の形を左右対称にする。</summary>
        SymmetryDetails,

        /// <summary>表示の区切り（範囲の色・ヒートマップ・元の形の重ね表示・比較）。</summary>
        DisplaySection,

        /// <summary>選んで動かす: ハンドルの種類。</summary>
        SelectionHandleType,

        /// <summary>選んで動かす: 布として動かす／形だけ動かす。</summary>
        SelectionMoveStyle,

        /// <summary>選んで動かす: なめらかさの半径。</summary>
        SelectionSmoothness,

        /// <summary>選んで動かす: 中心・向き・減り方・隠れた点・範囲の色分け・選ぶ操作のボタン・数値で動かす。</summary>
        SelectionDetails,

        /// <summary>保存: シェイプキーの名前と保存ボタン。</summary>
        SaveButton,

        /// <summary>保存: 保存したシェイプキーの簡単な一覧。</summary>
        SavedKeyList,

        /// <summary>保存したシェイプキーの一覧の「重ねて見る」スライダー（編集中の形に、保存した形を橙の半透明で重ねて見る。かんたん表示では、レンダラーの実際の重みを言葉で出す）。</summary>
        SavedKeyOverlay,

        /// <summary>詳細設定の区切り（曲がりやすさ・仕上げ・高速化など）。</summary>
        AdvancedSettings,

        /// <summary>ウィンドウの一番下の、状態の数値の1行。</summary>
        StatusLine,

        /// <summary>届かないときの線と一言（シーンビュー）。</summary>
        ReachWarning,

        /// <summary>体との貫通の状態・「貫通を直す」・「調べ直す」（編集中）。</summary>
        PenetrationFix,

        /// <summary>貫通の余白（mm）の設定。</summary>
        PenetrationMargin,

        /// <summary>動作チェック: 始める・やめる・結果の要約・順に見る・順に再生・元の姿勢に戻る・最悪の深さの色分け・元のポーズで外へ逃がす（編集中）。</summary>
        PoseCheck,

        /// <summary>動作チェック: 試すポーズの一覧・2段階の切り替え・許容の深さ・ポーズごとの結果の一覧・体として使ったもの。</summary>
        PoseCheckDetails,

        /// <summary>動作チェック: 「ウェイトを体に寄せる」（ウェイトが原因の貫通があるときだけ。押すと確認してから適用）と、その前後の数。</summary>
        WeightFix,

        /// <summary>ウェイトを体に寄せる: 強さ・半径・確認・適用・候補を捨てる・データに記録した変更を外す。</summary>
        WeightFixDetails,

        /// <summary>動作チェック: 手持ちの AnimationClip の一覧と、取り出す間隔。</summary>
        PoseCheckClips,

        // ---- シーンビューの道具バー ----

        /// <summary>道具バーの「伸びの表示」「元の形」。</summary>
        OverlayDisplayToggles,

        // ---- シェイプキーの一覧 ----

        ShapeKeyEnabled,
        ShapeKeyName,
        ShapeKeyWeight,
        ShapeKeyDuplicate,
        ShapeKeyDelete,

        /// <summary>左右に分割。</summary>
        ShapeKeySplit,

        /// <summary>左右反転。</summary>
        ShapeKeyFlip,

        /// <summary>メッシュに書き出す。</summary>
        ShapeKeyExport,

        /// <summary>データの欄。</summary>
        ShapeKeyDataField,

        /// <summary>検索・並べ替え。</summary>
        ShapeKeySearchSort,

        /// <summary>コピー・貼り付け。</summary>
        ShapeKeyCopyPaste,

        /// <summary>上へ・下へ。</summary>
        ShapeKeyReorder,

        /// <summary>まとめて有効・無効。</summary>
        ShapeKeyBulkEnable,

        /// <summary>グループ。</summary>
        ShapeKeyGroups,

        /// <summary>これだけ表示（ソロ）。</summary>
        ShapeKeySolo,

        /// <summary>選んだシェイプキーの詳細（焼き込み・フレーム・前の版）。</summary>
        ShapeKeyDetails,

        /// <summary>合成・左右対称・量・法線・in-between・差し引き・選んだ点の範囲・別のメッシュへ写す。</summary>
        ShapeKeyMoreOperations,

        /// <summary>操作の設定（左右の軸・中心など）。</summary>
        ShapeKeyOperationSettings,

        /// <summary>取り込み。</summary>
        ShapeKeyImport,

        /// <summary>適用のしかた（メッシュに書き出す／ビルド時に適用）。</summary>
        ShapeKeyApplyMode,

        /// <summary>書き出しの細かい設定（保存先・名前の前置き・書き出しの内容・AnimationClip・まとめて書き出し・焼き込み）。</summary>
        ShapeKeyExportDetails,

        /// <summary>バックアップ。</summary>
        ShapeKeyBackups,
    }

    /// <summary>
    /// かんたん表示とくわしく表示の切り替え（EditorPrefs に保存する）と、どの部品をかんたん表示でも出すかの判定。
    /// 判定は画面に依存しないので、テストで確かめられる。
    /// </summary>
    public static class ClothEditView
    {
        public const string PrefKey = "GP02.ViewMode";

        // かんたん表示でも出す部品。ここに無いものは、くわしく表示のときだけ出す
        static readonly HashSet<ClothEditUiItem> Simple = new HashSet<ClothEditUiItem>
        {
            ClothEditUiItem.Guide,
            ClothEditUiItem.TargetField,
            ClothEditUiItem.StartEditing,
            ClothEditUiItem.DiagnosticsSevere,
            ClothEditUiItem.DiagnosticsInfoLine,
            ClothEditUiItem.ToolGrab,
            ClothEditUiItem.ToolSelect,
            ClothEditUiItem.ToolPin,
            ClothEditUiItem.ToolRelax,
            ClothEditUiItem.PinEraseToggle,
            ClothEditUiItem.Undo,
            ClothEditUiItem.Redo,
            ClothEditUiItem.BrushRadius,
            ClothEditUiItem.SymmetryToggle,
            ClothEditUiItem.SymmetryAutoFit,
            ClothEditUiItem.SelectionHandleType,
            ClothEditUiItem.SelectionMoveStyle,
            ClothEditUiItem.SelectionSmoothness,
            ClothEditUiItem.SaveButton,
            ClothEditUiItem.SavedKeyList,
            ClothEditUiItem.StatusLine,
            ClothEditUiItem.ReachWarning,
            ClothEditUiItem.PenetrationFix,
            ClothEditUiItem.PoseCheck,
            ClothEditUiItem.WeightFix,
            ClothEditUiItem.ShapeKeyEnabled,
            ClothEditUiItem.ShapeKeyName,
            ClothEditUiItem.ShapeKeyWeight,
            ClothEditUiItem.ShapeKeyDuplicate,
            ClothEditUiItem.ShapeKeyDelete,
            ClothEditUiItem.ShapeKeySplit,
            ClothEditUiItem.ShapeKeyFlip,
            ClothEditUiItem.ShapeKeyExport,
        };

        static ClothEditViewMode? _mode;

        /// <summary>表示のしかたが変わったときに呼ばれる（開いている画面が描き直すため）。</summary>
        public static event Action Changed;

        /// <summary>かんたん表示でも出す部品の一覧。</summary>
        public static IReadOnlyCollection<ClothEditUiItem> SimpleItems => Simple;

        /// <summary>今の表示のしかた。EditorPrefs に保存する。既定はかんたん表示。</summary>
        public static ClothEditViewMode Mode
        {
            get
            {
                if (_mode == null)
                {
                    int saved = EditorPrefs.GetInt(PrefKey, (int)ClothEditViewMode.Simple);
                    _mode = Enum.IsDefined(typeof(ClothEditViewMode), saved) ? (ClothEditViewMode)saved : ClothEditViewMode.Simple;
                }
                return _mode.Value;
            }
            set
            {
                if (_mode == value) return;
                _mode = value;
                EditorPrefs.SetInt(PrefKey, (int)value);
                Changed?.Invoke();
            }
        }

        public static bool IsDetailed => Mode == ClothEditViewMode.Detailed;

        /// <summary>今の表示のしかたで、その部品を出すか。</summary>
        public static bool Shows(ClothEditUiItem item) => Shows(item, Mode);

        /// <summary>mode の表示のしかたで、その部品を出すか。くわしく表示は、すべて出す。</summary>
        public static bool Shows(ClothEditUiItem item, ClothEditViewMode mode)
        {
            return mode == ClothEditViewMode.Detailed || Simple.Contains(item);
        }

        /// <summary>保存してある設定を読み直す（テストで EditorPrefs を書き換えたあとなどに使う）。</summary>
        public static void Reload()
        {
            _mode = null;
        }

        /// <summary>ウィンドウの上部に出す「かんたん／くわしく」の切り替え。切り替えたら、すべての画面を描き直す。</summary>
        public static void DrawToggle(GUIStyle style, float width = 124f)
        {
            var labels = new[]
            {
                new GUIContent(L10n.Tr("かんたん", "Simple"), L10n.Tr("主な操作だけを出します（はじめての方向け）", "Shows only the main operations (for new users)")),
                new GUIContent(L10n.Tr("くわしく", "Detailed"), L10n.Tr("すべての機能と設定を出します", "Shows every feature and setting")),
            };
            int picked = GUILayout.Toolbar((int)Mode, labels, style ?? EditorStyles.miniButton, GUILayout.Width(width));
            if (picked == (int)Mode) return;
            Mode = (ClothEditViewMode)picked;
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
    }
}
