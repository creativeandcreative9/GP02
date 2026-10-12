using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // シーンビューの左上に出す札: 案内（今の道具の一番大事な操作）、届かないときの一言、小さな札（最初に覚える3つの操作）、ショートカットの一覧。
    // どれも不透明の暗い背景に白い文字で描く（白い体の上でも読めるように）。
    // 札の「×」と、札の上のクリックは、布の操作（つまむ・ピン留めなど）にならないよう、ここで受ける。
    public sealed partial class ClothEditWindow
    {
        static readonly Color CardBackground = new Color(0.09f, 0.1f, 0.12f, 1f);
        static readonly Color CardBorder = new Color(1f, 1f, 1f, 0.35f);
        static readonly Color CardDim = new Color(1f, 1f, 1f, 0.72f);

        const float CardX = 10f;                 // 札の左端と、一番上の札の上端（シーンビューの左上から）
        const float CardY = 10f;
        const float CardGap = 6f;                // 札どうしの間
        const float CardPadding = 8f;
        const float CardRow = 18f;               // 札の1行の高さ
        const float CloseSize = 18f;             // 「×」の大きさ
        const float GizmoReserve = 100f;         // シーンビューの右上のギズモを避けるために、右に空ける幅

        GUIStyle _cardStyle;                     // 案内の文（折り返す）
        GUIStyle _cardLineStyle;                 // 札の1行
        GUIStyle _cardKeyStyle;                  // ショートカット一覧のキー
        GUIStyle _cardValueStyle;                // ショートカット一覧の説明（長いときは折り返す）
        GUIStyle _cardCloseStyle;                // 「×」
        bool _startCardDismissed;                // 小さな札を、もう閉じたか（ClothEditHelp.ShownOncePrefKey）

        // 前に描いた札の位置（シーンビューの座標）。クリックを受けるために覚える。出していない札は空
        Rect _hintRect;
        Rect _reachRect;
        Rect _startCardRect;
        Rect _startCardCloseRect;
        Rect _listRect;
        Rect _listCloseRect;

        readonly List<string> _startCardLines = new List<string>();
        readonly List<float> _shortcutRowHeights = new List<float>();

        /// <summary>小さな札（最初に覚える3つの操作）を出しているか。全部の一覧を出している間と、×で閉じたあとは出さない。</summary>
        internal bool StartCardVisible => _controller != null && _controller.IsEditing && !_showShortcuts && !_startCardDismissed;

        /// <summary>小さな札の「×」を押した（次からは出さない）。</summary>
        internal void DismissStartCard()
        {
            _startCardDismissed = true;
            ClothEditHelp.DismissStartCard();
            _startCardRect = _startCardCloseRect = default;
            Repaint();
            SceneView.RepaintAll();
        }

        // 札の押せる所を、見えないボタンとして置く。Layout・Repaint を含め、どのイベントでも同じ順に呼ぶ（コントロールの番号をそろえるため）。
        // 札は一番上に見えるので、布の操作やハンドルより先に、ここでイベントを受ける。「×」は札の上にあるので先に、札の本体はそのあとに置く
        void DoCardClicks(SceneView sceneView)
        {
            Handles.BeginGUI();
            if (GUI.Button(_startCardCloseRect, GUIContent.none, GUIStyle.none)) DismissStartCard();
            if (GUI.Button(_listCloseRect, GUIContent.none, GUIStyle.none))
            {
                OverlaySetShortcutsVisible(false);
                sceneView.Repaint();
            }
            GUI.Button(_startCardRect, GUIContent.none, GUIStyle.none);
            GUI.Button(_listRect, GUIContent.none, GUIStyle.none);
            GUI.Button(_hintRect, GUIContent.none, GUIStyle.none);
            GUI.Button(_reachRect, GUIContent.none, GUIStyle.none);
            Handles.EndGUI();
        }

        void EnsureCardStyles()
        {
            if (_cardStyle != null) return;
            _cardStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = false,
                padding = new RectOffset((int)CardPadding, (int)CardPadding, 5, 5),
            };
            SetTextColor(_cardStyle, Color.white);
            _cardLineStyle = new GUIStyle(EditorStyles.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            SetTextColor(_cardLineStyle, Color.white);
            _cardKeyStyle = new GUIStyle(_cardLineStyle) { fontStyle = FontStyle.Bold };
            SetTextColor(_cardKeyStyle, new Color(0.62f, 0.82f, 1f, 1f));
            _cardValueStyle = new GUIStyle(_cardLineStyle) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            SetTextColor(_cardValueStyle, Color.white);
            _cardCloseStyle = new GUIStyle(EditorStyles.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            SetTextColor(_cardCloseStyle, Color.white);
        }

        // どの状態（通常・乗せた・押した）でも同じ色にする（暗い背景の上で、いつも白く読めるように）
        static void SetTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
        }

        // 不透明の暗い背景と細い縁
        static void DrawCardBox(Rect rect)
        {
            EditorGUI.DrawRect(rect, CardBackground);
            const float Edge = 1f;
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, Edge), CardBorder);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - Edge, rect.width, Edge), CardBorder);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, Edge, rect.height), CardBorder);
            EditorGUI.DrawRect(new Rect(rect.xMax - Edge, rect.yMin, Edge, rect.height), CardBorder);
        }

        // 文だけの札（折り返す）。幅は文に合わせ、maxWidth を超えるときは折り返す。置いた位置を返す
        Rect DrawTextCard(float x, float y, float maxWidth, string text, Color? textColor = null)
        {
            var content = new GUIContent(text);
            float width = Mathf.Min(maxWidth, _cardStyle.CalcSize(content).x + 2f);
            float height = _cardStyle.CalcHeight(content, width);
            var rect = new Rect(x, y, width, height);
            DrawCardBox(rect);
            Color previous = _cardStyle.normal.textColor;
            if (textColor.HasValue) SetTextColor(_cardStyle, textColor.Value);
            GUI.Label(rect, content, _cardStyle);
            SetTextColor(_cardStyle, previous);
            return rect;
        }

        // 「×」。押せる場所は DoCardClicks が置くので、ここは見た目だけ（マウスを乗せると明るくなる）
        void DrawCloseGlyph(Rect rect)
        {
            bool hover = rect.Contains(Event.current.mousePosition);
            EditorGUI.DrawRect(rect, hover ? new Color(1f, 1f, 1f, 0.28f) : new Color(1f, 1f, 1f, 0.12f));
            GUI.Label(rect, "×", _cardCloseStyle);
        }

        // シーンビューの左上に札を描く（Repaint のとき）。置いた位置は、クリックを受けるために覚える
        void DrawCards(SceneView sceneView)
        {
            EnsureCardStyles();
            float maxWidth = Mathf.Max(180f, sceneView.position.width - CardX - GizmoReserve);

            string text = ClothEditHelp.GetHint(_controller.Mode, _controller.IsDragging, _controller.IsSettling, _controller.SelectionCount, _controller.PinEraseMode);
            text += PoseCheckHint();
            if (_controller.IsComparing)
            {
                float blend = _controller.ShowOriginal || _controller.HoldOriginal ? 0f : _controller.CompareBlend;
                text += "\n" + (blend <= 0f
                    ? L10n.Tr("表示中: 元の形（編集前）", "Showing: original shape (before editing)")
                    : L10n.Tr($"表示中: 元と編集中の中間（編集中 {blend * 100f:F0}%）", $"Showing: blend of original and edited ({blend * 100f:F0}% edited)"));
            }

            Handles.BeginGUI();
            if (_rectDragging) DrawRectSelection();

            float y = CardY;
            _hintRect = DrawTextCard(CardX, y, maxWidth, text);
            y = _hintRect.yMax + CardGap;

            // 届かないときの一言（案内の下）。橙の文字
            _reachRect = default;
            if (_reachShown)
            {
                _reachRect = DrawTextCard(CardX, y, maxWidth, ClothEditReach.Message(ClothEditView.IsDetailed), new Color(1f, 0.8f, 0.45f, 1f));
                y = _reachRect.yMax + CardGap;
            }

            _startCardRect = _startCardCloseRect = _listRect = _listCloseRect = default;
            if (_showShortcuts) DrawShortcutList(sceneView, y);
            else if (StartCardVisible) DrawStartCard(y, maxWidth);
            Handles.EndGUI();
        }

        // 小さな札: 最初に覚える3つの操作と、全部の一覧への案内。右上に「×」
        void DrawStartCard(float y, float maxWidth)
        {
            ClothEditHelp.GetStartCardLines(_startCardLines);
            string more = ClothEditHelp.StartCardMore();
            float textWidth = _cardLineStyle.CalcSize(new GUIContent(more)).x;
            foreach (string line in _startCardLines) textWidth = Mathf.Max(textWidth, _cardLineStyle.CalcSize(new GUIContent(line)).x);
            float width = Mathf.Min(maxWidth, textWidth + 2f * CardPadding + CloseSize + 6f);
            float height = (_startCardLines.Count + 1) * CardRow + 2f * 4f;
            var rect = new Rect(CardX, y, width, height);
            _startCardRect = rect;
            DrawCardBox(rect);
            for (int i = 0; i < _startCardLines.Count; i++)
            {
                GUI.Label(new Rect(rect.x + CardPadding, rect.y + 4f + i * CardRow, rect.width - 2f * CardPadding - CloseSize, CardRow), _startCardLines[i], _cardLineStyle);
            }
            Color previous = _cardLineStyle.normal.textColor;
            SetTextColor(_cardLineStyle, CardDim);
            GUI.Label(new Rect(rect.x + CardPadding, rect.y + 4f + _startCardLines.Count * CardRow, rect.width - 2f * CardPadding, CardRow), more, _cardLineStyle);
            SetTextColor(_cardLineStyle, previous);

            _startCardCloseRect = new Rect(rect.xMax - CloseSize - 4f, rect.y + 4f, CloseSize, CloseSize);
            DrawCloseGlyph(_startCardCloseRect);
        }

        // 全部のショートカットの一覧。右上に「×」。
        // 幅は、右上のギズモ（視点の向きの表示）にかからないよう、右を空ける（「×」がギズモの下に隠れて押せなくならないように）。
        // 説明が長い行は折り返して、その行だけ高くする
        void DrawShortcutList(SceneView sceneView, float top)
        {
            ClothEditHelp.GetShortcutLines(_shortcutLines);
            float maxWidth = Mathf.Max(180f, sceneView.position.width - CardX - GizmoReserve);
            float keyWidth = 0f, textWidth = 0f;
            for (int i = 0; i < _shortcutLines.Count; i++)
            {
                keyWidth = Mathf.Max(keyWidth, _cardKeyStyle.CalcSize(new GUIContent(_shortcutLines[i].Key)).x);
                textWidth = Mathf.Max(textWidth, _cardLineStyle.CalcSize(new GUIContent(_shortcutLines[i].Value)).x);
            }
            float width = Mathf.Min(maxWidth, keyWidth + textWidth + 2f * CardPadding + 14f);
            float valueWidth = Mathf.Max(60f, width - keyWidth - 2f * CardPadding - 12f);

            // シーンビューが低いときは、1行の高さを縮めて、一覧が下で切れないようにする
            int rows = _shortcutLines.Count + 1;
            float rowHeight = Mathf.Clamp((sceneView.position.height - top - 36f) / rows, 14f, CardRow);
            _shortcutRowHeights.Clear();
            float rowsTotal = rowHeight;   // 見出しの行
            for (int i = 0; i < _shortcutLines.Count; i++)
            {
                float h = Mathf.Max(rowHeight, _cardValueStyle.CalcHeight(new GUIContent(_shortcutLines[i].Value), valueWidth));
                _shortcutRowHeights.Add(h);
                rowsTotal += h;
            }
            float height = rowsTotal + 12f;
            var rect = new Rect(CardX, top, width, height);
            _listRect = rect;
            DrawCardBox(rect);

            GUI.Label(new Rect(rect.x + CardPadding, rect.y + 4f, rect.width - 2f * CardPadding - CloseSize, rowHeight),
                L10n.Tr($"ショートカット（{ClothEditHelp.HelpKey()} か × で閉じる）", $"Shortcuts ({ClothEditHelp.HelpKey()} or × to close)"), _cardKeyStyle);
            float y = rect.y + 6f + rowHeight;
            for (int i = 0; i < _shortcutLines.Count; i++)
            {
                float h = _shortcutRowHeights[i];
                GUI.Label(new Rect(rect.x + CardPadding, y + 1f, keyWidth + 4f, rowHeight), _shortcutLines[i].Key, _cardKeyStyle);
                GUI.Label(new Rect(rect.x + CardPadding + keyWidth + 12f, y + 1f, valueWidth, h), _shortcutLines[i].Value, _cardValueStyle);
                y += h;
            }

            _listCloseRect = new Rect(rect.xMax - CloseSize - 4f, rect.y + 4f, CloseSize, CloseSize);
            DrawCloseGlyph(_listCloseRect);
        }
    }
}
