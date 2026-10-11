using System.Collections.Generic;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>シーンビューの案内の文章。今の道具と状態に合わせた操作説明と、ショートカット一覧。</summary>
    public static class ClothEditHelp
    {
        /// <summary>EditorPrefs のキー。初めて編集を始めたときにショートカット一覧を自動で出したかを覚える。</summary>
        public const string ShownOncePrefKey = "GP02.ShortcutHelpShown";

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
                    return L10n.Tr($"ピン留め（{key}）: 塗った所を固定します。動かしたくない所（ウエストなど）に使います。Ctrl+ドラッグで外す、Shift+ドラッグで矩形、ダブルクリックで部品ごと（Ctrl で外す）。「塗ると外す」をオンにすると、ドラッグで外します",
                        $"Pin ({key}): paint to fix an area so it never moves, such as the waist. Ctrl+Drag removes, Shift+Drag selects a rectangle, double-click a whole part (Ctrl removes). Turn on \"Paint to erase\" to remove by dragging");
                case ClothEditMode.Unpin:
                    return L10n.Tr($"ピン留め＋外す（{key}）: ピン留めの道具で「塗ると外す」をオンにしたものです。ドラッグで塗った所のピンを外し、Ctrl+ドラッグで留めます。Shift+ドラッグで矩形、ダブルクリックで部品ごと",
                        $"Pin + Erase ({key}): the Pin tool with \"Paint to erase\" on. Drag to remove the pins you paint over, Ctrl+Drag to add them. Shift+Drag for a rectangle, double-click for a whole part");
                case ClothEditMode.Relax:
                    return L10n.Tr($"元に戻すブラシ（{key}）: 塗った所を元の形へ近づけます。強さは「ブラシ」の区切りで決めます",
                        $"Restore Brush ({key}): paint to bring an area back toward its original shape. Set the strength in the Brush section");
                case ClothEditMode.Select:
                    return L10n.Tr($"選んで動かす（{key}）: ドラッグで点を塗って選び（Ctrl で外す、Shift+ドラッグで矩形、ダブルクリックで部品ごと）、出てきたハンドルで動かす・回す・拡大縮小します。もう一度 {key} で前の道具に戻ります",
                        $"Select & Move ({key}): drag to paint points to select (Ctrl to deselect, Shift+Drag for a rectangle, double-click for a whole part), then move / rotate / scale them with the handle. Press {key} again to go back to the previous tool");
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

        /// <summary>シーンビューの左上に出す案内（改行つき）。1行目が状態と今の道具の操作、2行目が共通の操作。「選んで動かす」は操作が多いので、2行に分ける。</summary>
        /// <param name="selectionCount">選んでいる点の数（「選んで動かす」の案内を、選ぶ前と選んだあとで変える）</param>
        /// <param name="pinErase">ピン留めの道具で「塗ると外す」がオンか（案内の、ドラッグと Ctrl+ドラッグの意味が逆になる）</param>
        public static string GetHint(ClothEditMode mode, bool dragging, bool settling, int selectionCount = 0, bool pinErase = false)
        {
            string common = L10n.Tr("Ctrl+ホイール / [ ]: ブラシ半径　C(押す): 元の形　Alt+ドラッグ: 視点移動　F1: ショートカット一覧",
                "Ctrl+Wheel / [ ]: brush radius   C (hold): original shape   Alt+Drag: orbit   F1: shortcuts");

            string first;
            if (dragging && mode == ClothEditMode.Select)
            {
                first = L10n.Tr("GP02 選んだ点を動かしています: 離すと仕上げます / Shift: ゆっくり / Ctrl: 刻み / Esc: 取り消し",
                    "GP02 Moving the selection: release to settle / Shift: slow / Ctrl: snap / Esc: cancel");
            }
            else if (dragging)
            {
                first = mode == ClothEditMode.Rotate ? L10n.Tr("GP02 回しています: 横に動かす / Ctrl: 15°刻み / Shift: 精密 / 離すと仕上げ / Esc: 取り消し",
                        "GP02 Rotating: drag sideways / Ctrl: 15-degree steps / Shift: fine / release to settle / Esc: cancel")
                    : mode == ClothEditMode.Scale ? L10n.Tr("GP02 拡大縮小しています: 横に動かす（0.2〜3倍）/ Shift: 精密 / 離すと仕上げ / Esc: 取り消し",
                        "GP02 Scaling: drag sideways (0.2x to 3x) / Shift: fine / release to settle / Esc: cancel")
                    : L10n.Tr("GP02 つまんでいます: 離すと仕上げます / Shift: 精密 / Esc: 取り消し",
                        "GP02 Grabbing: release to settle / Shift: fine / Esc: cancel");
            }
            else if (settling)
            {
                first = L10n.Tr("GP02 仕上げ中: Esc で取り消し", "GP02 Settling: Esc to cancel");
            }
            else
            {
                string name = $"{ToolName(mode)} ({ToolKey(mode)})";
                switch (mode)
                {
                    case ClothEditMode.Rotate:
                        first = L10n.Tr($"GP02 {name}: 布をクリックして、横にドラッグで回す（クリックした点が中心）",
                            $"GP02 {name}: click the cloth and drag sideways to rotate (around the clicked point)");
                        break;
                    case ClothEditMode.Scale:
                        first = L10n.Tr($"GP02 {name}: 布をクリックして、横にドラッグで拡大縮小（クリックした点が中心）",
                            $"GP02 {name}: click the cloth and drag sideways to scale (around the clicked point)");
                        break;
                    case ClothEditMode.Pin:
                    case ClothEditMode.Unpin:
                        // シーンビューの幅に収まるよう短くする（詳しい説明は、道具のツールチップ）
                        first = pinErase || mode == ClothEditMode.Unpin
                            ? L10n.Tr($"GP02 {name}: ドラッグで外す / Ctrl+ドラッグ: 留める / Shift+ドラッグ: 矩形 / ダブルクリック: 部品ごと",
                                $"GP02 {name}: drag to remove / Ctrl+Drag: pin / Shift+Drag: rectangle / Double-click: whole part")
                            : L10n.Tr($"GP02 {name}: ドラッグで留める / Ctrl+ドラッグ: 外す / Shift+ドラッグ: 矩形 / ダブルクリック: 部品ごと",
                                $"GP02 {name}: drag to pin / Ctrl+Drag: remove / Shift+Drag: rectangle / Double-click: whole part");
                        break;
                    case ClothEditMode.Relax:
                        first = L10n.Tr($"GP02 {name}: 左ドラッグで塗った所を元の形へ近づける（離すと仕上げ）",
                            $"GP02 {name}: drag to bring the painted area back toward its original shape (settles on release)");
                        break;
                    case ClothEditMode.Select:
                        first = L10n.Tr($"GP02 {name}: ドラッグで塗って選ぶ（Ctrl: 外す）/ Shift+ドラッグ: 矩形 / ダブルクリック: 部品ごと",
                                $"GP02 {name}: drag to paint-select (Ctrl: deselect) / Shift+Drag: rectangle / Double-click: whole part")
                            + "\n" + (selectionCount > 0
                                ? L10n.Tr($"{selectionCount:N0} 点を選択中: ハンドルで動かす（Shift: ゆっくり / Ctrl: 刻み）/ W E R: ハンドルの種類 / Esc: 解除 / Q: 戻る",
                                    $"{selectionCount:N0} selected: drag the handle (Shift: slow / Ctrl: snap) / W E R: handle type / Esc: clear / Q: back")
                                : L10n.Tr("空の所をクリック: 解除 / 選ぶとハンドルが出る（W E R: 移動・回転・拡大縮小）/ Esc: 解除 / Q: 戻る",
                                    "Click empty space: clear / A handle appears once selected (W E R: move, rotate, scale) / Esc: clear / Q: back"));
                        break;
                    default:
                        first = L10n.Tr($"GP02 {name}: 左ドラッグで布をつまんで動かす / Shift: 精密 / Esc: 取り消し",
                            $"GP02 {name}: drag to grab and move the cloth / Shift: fine / Esc: cancel");
                        break;
                }
            }
            return first + "\n" + common;
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
            Add(L10n.Tr("Shift+ドラッグ（ピン）", "Shift+Drag (Pin)"), "矩形の中をまとめてピン留め（Ctrl+Shift で外す）", "Pin everything inside a rectangle (Ctrl+Shift to remove)");
            Add(L10n.Tr("ダブルクリック（ピン）", "Double-click (Pin)"), "その部品（島）全体をピン留め（Ctrl で外す）", "Pin the whole part (island) (Ctrl to remove)");
            Add(L10n.Tr("ドラッグ（選ぶ）", "Drag (Select)"), "ブラシ半径で塗って選ぶ（Ctrl で外す）", "Paint-select with the brush radius (Ctrl to deselect)");
            Add(L10n.Tr("Shift+ドラッグ（選ぶ）", "Shift+Drag (Select)"), "矩形で選ぶ（Ctrl+Shift で外す）", "Select inside a rectangle (Ctrl+Shift to deselect)");
            Add(L10n.Tr("ダブルクリック（選ぶ）", "Double-click (Select)"), "その部品（島）ごと選ぶ（Ctrl で外す）", "Select the whole part (island) (Ctrl to deselect)");
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
            Add("F1", "この一覧を出す・隠す", "Show / hide this list");
            Add("Edit > Shortcuts", "「GP02」の項目で、キーを変えられます", "Change the keys under \"GP02\"");
        }
    }
}
