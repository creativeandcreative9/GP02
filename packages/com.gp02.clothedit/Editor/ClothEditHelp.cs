using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>シーンビューの案内の文章。今の道具と状態に合わせた操作説明と、ショートカット一覧。</summary>
    public static class ClothEditHelp
    {
        /// <summary>
        /// EditorPrefs のキー。シーンビューの小さな札（編集を始めると出る、3つの操作と「F1: すべての操作」）を「×」で閉じたかを覚える。
        /// 閉じたら、次からは出さない（全部のショートカット一覧は、「?」・F1・道具バーでいつでも出し入れできる）。
        /// </summary>
        public const string ShownOncePrefKey = "GP02.ShortcutHelpShown";

        /// <summary>小さな札を、もう閉じたか。</summary>
        public static bool IsStartCardDismissed() => EditorPrefs.GetBool(ShownOncePrefKey, false);

        /// <summary>小さな札を閉じたことを覚える（次からは出さない）。</summary>
        public static void DismissStartCard() => EditorPrefs.SetBool(ShownOncePrefKey, true);

        /// <summary>ショートカット一覧を出し入れするキーの表示（Edit &gt; Shortcuts で変えていればそのキー。割り当てが無ければ「?」ボタン）。</summary>
        public static string HelpKey()
        {
            string key = ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdToggleHelp, "F1");
            return key.Length > 0 ? key : "?";
        }

        /// <summary>
        /// 小さな札の行。初めて使う人が、まず覚える3つの操作。最後の1行（<see cref="StartCardMore"/>）は、全部の一覧への案内。
        /// </summary>
        public static void GetStartCardLines(List<string> result)
        {
            result.Clear();
            result.Add(L10n.Tr("ドラッグ: 布をつまむ", "Drag: grab the cloth"));
            result.Add(L10n.Tr("Alt+ドラッグ: 視点を回す", "Alt+Drag: orbit the view"));
            result.Add(L10n.Tr("Ctrl+Z: 1つ戻す", "Ctrl+Z: undo one step"));
        }

        /// <summary>小さな札の、全部の一覧への案内の行。</summary>
        public static string StartCardMore() => HelpKey() + L10n.Tr(": すべての操作", ": all controls");

        /// <summary>
        /// 伸びのヒートマップの色。0% は緑、<see cref="ClothEditSettings.HeatmapYellow"/> で黄、<see cref="ClothEditSettings.HeatmapRed"/> 以上で赤。間は線形に補う。
        /// </summary>
        public static Color HeatColor(float stretch)
        {
            var green = new Color(0.2f, 0.85f, 0.3f, 1f);
            var yellow = new Color(1f, 0.9f, 0.1f, 1f);
            var red = new Color(1f, 0.15f, 0.1f, 1f);
            if (!(stretch > 0f)) return green;
            if (stretch <= ClothEditSettings.HeatmapYellow) return Color.Lerp(green, yellow, stretch / ClothEditSettings.HeatmapYellow);
            if (stretch >= ClothEditSettings.HeatmapRed) return red;
            return Color.Lerp(yellow, red, (stretch - ClothEditSettings.HeatmapYellow) / (ClothEditSettings.HeatmapRed - ClothEditSettings.HeatmapYellow));
        }

        /// <summary>ブラシを当てる前の範囲の点の色。重み 0（範囲の端）は青く薄く、1（中心）は黄色く濃い。</summary>
        public static Color WeightColor(float weight)
        {
            weight = Mathf.Clamp01(weight);
            Color c = Color.Lerp(new Color(0.25f, 0.5f, 1f), new Color(1f, 0.92f, 0.25f), weight);
            c.a = 0.35f + 0.65f * weight;
            return c;
        }

        /// <summary>
        /// 動いた量のヒートマップの色。0 は薄い青、<see cref="ClothEditSettings.MoveHeatmapMax"/> の半分で黄、それ以上で赤。間は線形に補う。
        /// </summary>
        public static Color MoveColor(float distance)
        {
            var blue = new Color(0.3f, 0.5f, 1f, 0.45f);
            var yellow = new Color(1f, 0.9f, 0.1f, 1f);
            var red = new Color(1f, 0.15f, 0.1f, 1f);
            float half = 0.5f * ClothEditSettings.MoveHeatmapMax;
            if (!(distance > 0f)) return blue;
            if (distance <= half) return Color.Lerp(blue, yellow, distance / half);
            if (distance >= ClothEditSettings.MoveHeatmapMax) return red;
            return Color.Lerp(yellow, red, (distance - half) / half);
        }

        /// <summary>左右差の色。0 は緑、<see cref="ClothEditSettings.MirrorDifferenceRed"/> の 1/4 で黄、それ以上で赤。</summary>
        public static Color MirrorDifferenceColor(float difference)
        {
            var green = new Color(0.2f, 0.85f, 0.3f, 0.5f);
            var yellow = new Color(1f, 0.9f, 0.1f, 1f);
            var red = new Color(1f, 0.15f, 0.1f, 1f);
            float quarter = 0.25f * ClothEditSettings.MirrorDifferenceRed;
            if (!(difference > 0f)) return green;
            if (difference <= quarter) return Color.Lerp(green, yellow, difference / quarter);
            if (difference >= ClothEditSettings.MirrorDifferenceRed) return red;
            return Color.Lerp(yellow, red, (difference - quarter) / (ClothEditSettings.MirrorDifferenceRed - quarter));
        }

        /// <summary>対称の相手のいない点の色（目立つ赤紫）。</summary>
        public static Color UnmatchedMirrorColor => new Color(1f, 0.1f, 0.9f, 1f);

        /// <summary>
        /// ピン留めの点の色（濃い青）。貫通の点（赤、白い縁）・選んだ点（橙）・ブラシの範囲の点（水色から黄色。小さくて半透明）と見分けられるよう、
        /// 濃い青にして、ブラシの範囲の点より大きく描く。
        /// </summary>
        public static Color PinColor => new Color(0.05f, 0.22f, 0.95f, 1f);

        /// <summary>ピン留めの道具の円・四角の色（<see cref="PinColor"/> を少し明るくして、細い線でも見やすいようにしたもの）。</summary>
        public static Color PinToolColor => new Color(0.25f, 0.45f, 1f, 1f);

        /// <summary>伸びの大きさの段階（かんたん表示の状態の行に、数字の代わりに言葉と色で出す）。</summary>
        public enum StretchLevel
        {
            /// <summary>ほぼ無し（緑）。</summary>
            None,

            /// <summary>少し（黄）。</summary>
            Slight,

            /// <summary>大きい（赤）。</summary>
            Large,
        }

        /// <summary>
        /// 最大の伸び（0.1 で 10%）の段階。境目は伸びのヒートマップと同じで、<see cref="ClothEditSettings.HeatmapYellow"/> 未満は「ほぼ無し」、
        /// <see cref="ClothEditSettings.HeatmapRed"/> 未満は「少し」、それ以上は「大きい」。数でない値（NaN）は「ほぼ無し」。
        /// </summary>
        public static StretchLevel GetStretchLevel(float stretch)
        {
            if (!(stretch >= ClothEditSettings.HeatmapYellow)) return StretchLevel.None;
            return stretch < ClothEditSettings.HeatmapRed ? StretchLevel.Slight : StretchLevel.Large;
        }

        /// <summary>伸びの段階の言葉（「ほぼ無し」「少し」「大きい」）。</summary>
        public static string StretchLevelName(StretchLevel level)
        {
            switch (level)
            {
                case StretchLevel.Slight: return L10n.Tr("少し", "slight");
                case StretchLevel.Large: return L10n.Tr("大きい", "large");
                default: return L10n.Tr("ほぼ無し", "none");
            }
        }

        /// <summary>伸びの段階の色（緑・黄・赤）。文字に使うので、暗い背景（Pro スキン）と明るい背景で、読みやすい濃さを変える。</summary>
        public static Color StretchLevelColor(StretchLevel level, bool darkBackground)
        {
            switch (level)
            {
                case StretchLevel.Slight: return darkBackground ? new Color(1f, 0.82f, 0.2f, 1f) : new Color(0.65f, 0.45f, 0f, 1f);
                case StretchLevel.Large: return darkBackground ? new Color(1f, 0.4f, 0.35f, 1f) : new Color(0.8f, 0.1f, 0.1f, 1f);
                default: return darkBackground ? new Color(0.35f, 0.85f, 0.4f, 1f) : new Color(0.1f, 0.5f, 0.15f, 1f);
            }
        }

        /// <summary>3D カーソルの色。</summary>
        public static Color CursorColor => new Color(1f, 0.25f, 0.25f, 1f);

        /// <summary>減り方の同心円の色（ブラシの円の色を薄くして使う）。</summary>
        public const float FalloffRingAlpha = 0.45f;

        /// <summary>減り方の同心円で見せる重み。</summary>
        public static readonly float[] FalloffRingWeights = { 0.75f, 0.5f, 0.25f };

        /// <summary>選んだ点の色（橙）。</summary>
        public static Color SelectedColor => new Color(1f, 0.55f, 0.08f, 1f);

        /// <summary>対称がオンのとき、選んだ点の鏡像の側（動くが選んではいない点）の色。選んだ点の色を薄くしたもの。</summary>
        public static Color MirroredColor => new Color(1f, 0.55f, 0.08f, 0.35f);

        /// <summary>「選んで動かす」のブラシの円の色。選んだ点の橙とは違う色にする。</summary>
        public static Color SelectBrushColor => new Color(1f, 0.4f, 0.75f, 1f);

        /// <summary>マウスの下の、塗ると選ばれる点の色（ブラシの円と同じ色を薄くしたもの）。</summary>
        public static Color SelectHoverColor => new Color(1f, 0.4f, 0.75f, 0.55f);

        /// <summary>
        /// 選んだ点の周りの、なめらかに減る範囲の点の色。重み 0（端）は薄い青、1（選んだ点の近く）は黄で、選んだ点の橙につながる。
        /// ブラシを当てる前の範囲の色（<see cref="WeightColor"/>）とは、選んだ点と区別できるよう、端を薄くしている。
        /// </summary>
        public static Color SelectionWeightColor(float weight)
        {
            weight = Mathf.Clamp01(weight);
            Color c = Color.Lerp(new Color(0.35f, 0.65f, 1f), new Color(1f, 0.85f, 0.2f), weight);
            c.a = 0.25f + 0.6f * weight;
            return c;
        }

        public static string HandleName(SelectionHandle handle)
        {
            switch (handle)
            {
                case SelectionHandle.Rotate: return L10n.Tr("回転", "Rotate");
                case SelectionHandle.Scale: return L10n.Tr("拡大縮小", "Scale");
                default: return L10n.Tr("移動", "Move");
            }
        }

        /// <summary>ハンドルの種類を切り替えるキー（「選んで動かす」の間だけ）。</summary>
        public static string HandleKey(SelectionHandle handle)
        {
            switch (handle)
            {
                case SelectionHandle.Rotate: return "E";
                case SelectionHandle.Scale: return "R";
                default: return "W";
            }
        }

        public static string HandleTip(SelectionHandle handle)
        {
            string key = HandleKey(handle);
            switch (handle)
            {
                case SelectionHandle.Rotate:
                    return L10n.Tr($"回転ハンドル（{key}）: 選んだ点を、中心のまわりに回します。「選んで動かす」の間は、{key} キーでこのハンドルに切り替わります",
                        $"Rotate handle ({key}): rotates the selected points around the pivot. While Select & Move is active, the {key} key switches to this handle");
                case SelectionHandle.Scale:
                    return L10n.Tr($"拡大縮小ハンドル（{key}）: 選んだ点を、中心から大きく・小さくします（軸ごとにも変えられます）。「選んで動かす」の間は、{key} キーでこのハンドルに切り替わります",
                        $"Scale handle ({key}): scales the selected points around the pivot (per axis, too). While Select & Move is active, the {key} key switches to this handle");
                default:
                    return L10n.Tr($"移動ハンドル（{key}）: 選んだ点を、矢印の向きや面の上で動かします。「選んで動かす」の間は、{key} キーでこのハンドルに切り替わります",
                        $"Move handle ({key}): moves the selected points along an arrow or on a plane. While Select & Move is active, the {key} key switches to this handle");
            }
        }

        /// <summary>選んだ点の数の表示（ハンドルの近くに出す小さな文字）。</summary>
        public static string SelectionCountLabel(int count)
        {
            return L10n.Tr($"{count:N0} 点を選択中", $"{count:N0} selected");
        }

        public static string ToolName(ClothEditMode mode)
        {
            switch (mode)
            {
                case ClothEditMode.Rotate: return L10n.Tr("回す", "Rotate");
                case ClothEditMode.Scale: return L10n.Tr("拡大縮小", "Scale");
                case ClothEditMode.Pin: return L10n.Tr("ピン留め", "Pin");
                case ClothEditMode.Unpin: return L10n.Tr("ピン留め＋外す", "Pin + Erase");
                case ClothEditMode.Relax: return L10n.Tr("元に戻すブラシ", "Restore Brush");
                case ClothEditMode.Select: return L10n.Tr("選んで動かす", "Select & Move");
                default: return L10n.Tr("つまむ", "Grab");
            }
        }

        /// <summary>道具の説明（ツールバーやオーバーレイのツールチップ）。</summary>
        public static string ToolTip(ClothEditMode mode)
        {
            string key = ToolKey(mode);
            switch (mode)
            {
                case ClothEditMode.Rotate:
                    return L10n.Tr($"回す（{key}）: 布をクリックして横にドラッグすると、クリックした点を中心に回ります。Ctrl で15°刻み",
                        $"Rotate ({key}): click the cloth and drag sideways to rotate around the clicked point. Ctrl for 15-degree steps");
                case ClothEditMode.Scale:
                    return L10n.Tr($"拡大縮小（{key}）: 布をクリックして横にドラッグすると、クリックした点を中心に大きく・小さくなります（0.2〜3倍）",
                        $"Scale ({key}): click the cloth and drag sideways to scale around the clicked point (0.2x to 3x)");
                case ClothEditMode.Pin:
                    return L10n.Tr($"ピン留め（{key}）: 塗った所を固定します。動かしたくない所（ウエストなど）に使います。Ctrl+ドラッグで外す、Shift+ドラッグで四角く囲んで留める、ダブルクリックで部品ごと（Ctrl で外す）。「塗ると外す」をオンにすると、ドラッグで外します",
                        $"Pin ({key}): paint to fix an area so it never moves, such as the waist. Ctrl+Drag removes, Shift+Drag pins everything inside a box, double-click a whole part (Ctrl removes). Turn on \"Paint to erase\" to remove by dragging");
                case ClothEditMode.Unpin:
                    return L10n.Tr($"ピン留め＋外す（{key}）: ピン留めの道具で「塗ると外す」をオンにしたものです。ドラッグで塗った所のピンを外し、Ctrl+ドラッグで留めます。Shift+ドラッグで四角く囲んで、ダブルクリックで部品ごと",
                        $"Pin + Erase ({key}): the Pin tool with \"Paint to erase\" on. Drag to remove the pins you paint over, Ctrl+Drag to add them. Shift+Drag for a box, double-click for a whole part");
                case ClothEditMode.Relax:
                    return L10n.Tr($"元に戻すブラシ（{key}）: 塗った所を元の形へ近づけます。強さは、くわしく表示の「ブラシ」の区切りで決めます",
                        $"Restore Brush ({key}): paint to bring an area back toward its original shape. Set the strength in the Brush section of the Detailed view");
                case ClothEditMode.Select:
                    return L10n.Tr($"選んで動かす（{key}）: ドラッグで点を塗って選び（Ctrl で外す、Shift+ドラッグで四角く囲んで、ダブルクリックで部品ごと）、出てきたハンドルで動かす・回す・拡大縮小します。もう一度 {key} で前の道具に戻ります",
                        $"Select & Move ({key}): drag to paint points to select (Ctrl to deselect, Shift+Drag for a box, double-click for a whole part), then move / rotate / scale them with the handle. Press {key} again to go back to the previous tool");
                default:
                    return L10n.Tr($"つまむ（{key}）: 布をドラッグして、つまんだ所を引っ張ります。周りの布も伸びずに付いてきます。Shift で精密操作",
                        $"Grab ({key}): drag the cloth to pull the grabbed area. The cloth around follows without stretching. Shift for fine control");
            }
        }

        /// <summary>
        /// ショートカットキーの表示（道具の名前のあとに添える）。Edit > Shortcuts で変えていれば、そのキー（<see cref="ClothEditShortcutBindings.GetKeyLabel"/>）。
        /// </summary>
        public static string ToolKey(ClothEditMode mode)
        {
            switch (mode)
            {
                case ClothEditMode.Rotate: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdRotate, "E");
                case ClothEditMode.Scale: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdScale, "R");
                case ClothEditMode.Pin: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdPin, "P");
                case ClothEditMode.Unpin: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdPinErase, "Shift+P");
                case ClothEditMode.Relax: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdRelax, "T");
                case ClothEditMode.Select: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdSelectTool, "Q");
                default: return ClothEditShortcutBindings.GetKeyLabel(ClothEditShortcutBindings.IdGrab, "W");
            }
        }

        /// <summary>
        /// シーンビューの左上に出す案内。今の道具の一番大事な操作と「F1: 操作の一覧」だけの、短い文にする（細かい操作はショートカット一覧へ）。
        /// 1行。「選んで動かす」だけは、選ぶ操作と動かす操作の2行。
        /// </summary>
        /// <param name="selectionCount">選んでいる点の数（「選んで動かす」の案内を、選ぶ前と選んだあとで変える）</param>
        /// <param name="pinErase">ピン留めの道具で「塗ると外す」がオンか（案内の、ドラッグと Ctrl+ドラッグの意味が逆になる）</param>
        public static string GetHint(ClothEditMode mode, bool dragging, bool settling, int selectionCount = 0, bool pinErase = false)
        {
            string more = L10n.Tr($"　{HelpKey()}: 操作の一覧", $"   {HelpKey()}: all controls");

            if (dragging && mode == ClothEditMode.Select)
            {
                return L10n.Tr("選んだ点を動かしています: 離すと仕上げます（Esc: 取り消し）", "Moving the selection: release to settle (Esc: cancel)");
            }
            if (dragging)
            {
                return mode == ClothEditMode.Rotate ? L10n.Tr("回しています: 離すと仕上げます（Esc: 取り消し）", "Rotating: release to settle (Esc: cancel)")
                    : mode == ClothEditMode.Scale ? L10n.Tr("拡大縮小しています: 離すと仕上げます（Esc: 取り消し）", "Scaling: release to settle (Esc: cancel)")
                    : L10n.Tr("つまんでいます: 離すと仕上げます（Esc: 取り消し）", "Grabbing: release to settle (Esc: cancel)");
            }
            if (settling) return L10n.Tr("仕上げ中です（Esc: 取り消し）", "Settling (Esc: cancel)");

            string name = $"{ToolName(mode)} ({ToolKey(mode)})";
            switch (mode)
            {
                case ClothEditMode.Rotate:
                    return L10n.Tr($"{name}: 布をクリックして、横にドラッグで回す{more}", $"{name}: click the cloth, then drag sideways to rotate{more}");
                case ClothEditMode.Scale:
                    return L10n.Tr($"{name}: 布をクリックして、横にドラッグで大きさを変える{more}", $"{name}: click the cloth, then drag sideways to resize{more}");
                case ClothEditMode.Pin:
                case ClothEditMode.Unpin:
                    return pinErase || mode == ClothEditMode.Unpin
                        ? L10n.Tr($"{name}: ドラッグで外す（Ctrl+ドラッグで留める）{more}", $"{name}: drag to remove pins (Ctrl+Drag to pin){more}")
                        : L10n.Tr($"{name}: ドラッグで動かさない所を塗る（Ctrl+ドラッグで外す）{more}", $"{name}: drag to paint what should not move (Ctrl+Drag to remove){more}");
                case ClothEditMode.Relax:
                    return L10n.Tr($"{name}: ドラッグで元の形へ近づける{more}", $"{name}: drag to bring the cloth back to its original shape{more}");
                case ClothEditMode.Select:
                    return L10n.Tr($"{name}: ドラッグで点を選ぶ", $"{name}: drag to select points") + "\n"
                        + (selectionCount > 0
                            ? L10n.Tr($"{selectionCount:N0} 点を選択中: ハンドルをドラッグで動かす{more}", $"{selectionCount:N0} selected: drag the handle to move{more}")
                            : L10n.Tr($"選ぶと出てくるハンドルで動かす{more}", $"A handle appears once you select; drag it to move{more}"));
                default:
                    return L10n.Tr($"{name}: 布をドラッグして動かす{more}", $"{name}: drag the cloth to move it{more}");
            }
        }

        /// <summary>ショートカット一覧（キー、説明）。</summary>
        public static void GetShortcutLines(List<KeyValuePair<string, string>> result)
        {
            result.Clear();
            void Add(string key, string ja, string en) => result.Add(new KeyValuePair<string, string>(key, L10n.Tr(ja, en)));

            Add("W", "つまむ（選んで動かす中は、移動ハンドル）", "Grab (the move handle while in Select & Move)");
            Add("E", "回す（横ドラッグ。Ctrl で15°刻み）（選んで動かす中は、回転ハンドル）", "Rotate (drag sideways; Ctrl for 15-degree steps) (the rotate handle while in Select & Move)");
            Add("R", "拡大縮小（横ドラッグ。0.2〜3倍）（選んで動かす中は、拡大縮小ハンドル）", "Scale (drag sideways; 0.2x to 3x) (the scale handle while in Select & Move)");
            Add("Q", "選んで動かす（もう一度押すと前の道具）", "Select & Move (press again to return to the previous tool)");
            Add("P", "ピン留め（ドラッグで留める）", "Pin (drag to pin)");
            Add("Shift+P", "ピン留め＋外す（「塗ると外す」がオンのピン留め。ドラッグで外す）", "Pin + Erase (Pin with Paint to erase on; drag to remove)");
            Add("T", "元に戻すブラシ", "Restore Brush");
            Add(L10n.Tr("Shift+ドラッグ", "Shift+Drag"), "動きを 1/5 にする（精密操作）", "Move at 1/5 speed (fine control)");
            Add(L10n.Tr("Ctrl+ドラッグ（ピン）", "Ctrl+Drag (Pin)"), "塗った所のピンを外す（「塗ると外す」がオンなら、留める）", "Remove the pins you paint over (adds them when Paint to erase is on)");
            Add(L10n.Tr("Shift+ドラッグ（ピン）", "Shift+Drag (Pin)"), "四角で囲んだ中をまとめてピン留め（Ctrl+Shift で外す）", "Pin everything inside a box you drag (Ctrl+Shift to remove)");
            Add(L10n.Tr("ダブルクリック（ピン）", "Double-click (Pin)"), "その部品全体をピン留め（Ctrl で外す）", "Pin the whole part (Ctrl to remove)");
            Add(L10n.Tr("ドラッグ（選ぶ）", "Drag (Select)"), "ブラシ半径で塗って選ぶ（Ctrl で外す）", "Paint-select with the brush radius (Ctrl to deselect)");
            Add(L10n.Tr("Shift+ドラッグ（選ぶ）", "Shift+Drag (Select)"), "四角で囲んで選ぶ（Ctrl+Shift で外す）", "Select inside a box you drag (Ctrl+Shift to deselect)");
            Add(L10n.Tr("ダブルクリック（選ぶ）", "Double-click (Select)"), "その部品ごと選ぶ（Ctrl で外す）", "Select the whole part (Ctrl to deselect)");
            Add(L10n.Tr("空の所をクリック（選ぶ）", "Click empty space (Select)"), "選択を解除", "Clear the selection");
            Add(L10n.Tr("ハンドルのドラッグ", "Drag the handle"), "選んだ点を動かす（Shift でゆっくり、Ctrl で刻み）", "Move the selected points (Shift to slow down, Ctrl to snap)");
            Add("F", "マウスを横に動かしてブラシ半径を変える（選んで動かす中は、なめらかさの半径）。クリックか F で確定、Esc か右クリックで取り消し",
                "Move the mouse sideways to change the brush radius (the smoothness radius in Select & Move). Click or F to confirm, Esc or right-click to cancel");
            Add("Shift+F", "マウスを横に動かして影響範囲を変える", "Move the mouse sideways to change the influence range");
            Add(L10n.Tr("Shift+右クリック", "Shift+Right-click"), "3D カーソルを置く（中心を「3D カーソル」にしたときの回転・拡大縮小の中心）", "Place the 3D cursor (the pivot when Pivot is \"3D cursor\")");
            Add(L10n.Tr("Ctrl+ホイール", "Ctrl+Wheel"), "ブラシ半径", "Brush radius");
            Add(L10n.Tr("Ctrl+Shift+ホイール", "Ctrl+Shift+Wheel"), "影響範囲", "Influence range");
            Add("[  ]", "ブラシ半径（Shift で影響範囲）", "Brush radius (Shift for influence range)");
            Add(L10n.Tr("C（押している間）", "C (hold)"), "元の形を見せる", "Show the original shape");
            Add("Esc", "ドラッグ・仕上げ中の操作を取り消す（何もしていなければ、選択を解除）", "Cancel the stroke being dragged or settled (clears the selection when idle)");
            Add("Ctrl+Z / Ctrl+Y", "1操作ずつ戻す・やり直す", "Undo / redo one operation at a time");
            Add(L10n.Tr("Alt+ドラッグ", "Alt+Drag"), "視点を動かす", "Orbit the view");
            Add("F1", "この一覧を出す・隠す（右上の × でも閉じられます）", "Show / hide this list (the × at its top right also closes it)");
            Add("Edit > Shortcuts", "「GP02」の項目で、キーを変えられます", "Change the keys under \"GP02\"");
        }
    }
}
