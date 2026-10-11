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

        public static string ToolName(ClothEditMode mode)
        {
            switch (mode)
            {
                case ClothEditMode.Rotate: return L10n.Tr("回す", "Rotate");
                case ClothEditMode.Scale: return L10n.Tr("拡大縮小", "Scale");
                case ClothEditMode.Pin: return L10n.Tr("ピン留め", "Pin");
                case ClothEditMode.Unpin: return L10n.Tr("ピン解除", "Unpin");
                case ClothEditMode.Relax: return L10n.Tr("元に戻すブラシ", "Restore Brush");
                default: return L10n.Tr("つまむ", "Grab");
            }
        }

        /// <summary>ショートカットキーの表示（道具の名前のあとに添える）。</summary>
        public static string ToolKey(ClothEditMode mode)
        {
            switch (mode)
            {
                case ClothEditMode.Rotate: return "E";
                case ClothEditMode.Scale: return "R";
                case ClothEditMode.Pin: return "P";
                case ClothEditMode.Unpin: return "Shift+P";
                case ClothEditMode.Relax: return "T";
                default: return "W";
            }
        }

        /// <summary>シーンビューの左上に出す案内（改行つき）。1行目が状態と今の道具の操作、2行目が共通の操作。</summary>
        public static string GetHint(ClothEditMode mode, bool dragging, bool settling)
        {
            string common = L10n.Tr("Ctrl+ホイール / [ ]: ブラシ半径　C(押す): 元の形　Alt+ドラッグ: 視点移動　F1: ショートカット一覧",
                "Ctrl+Wheel / [ ]: brush radius   C (hold): original shape   Alt+Drag: orbit   F1: shortcuts");

            string first;
            if (dragging)
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
                        first = L10n.Tr($"GP02 {name}: 左ドラッグで塗る（動かさない所を決める）/ Shift+P: ピン解除",
                            $"GP02 {name}: drag to paint the parts that should not move / Shift+P: unpin");
                        break;
                    case ClothEditMode.Unpin:
                        first = L10n.Tr($"GP02 {name}: 左ドラッグで塗ってピンを外す", $"GP02 {name}: drag to paint and remove pins");
                        break;
                    case ClothEditMode.Relax:
                        first = L10n.Tr($"GP02 {name}: 左ドラッグで塗った所を元の形へ近づける（離すと仕上げ）",
                            $"GP02 {name}: drag to bring the painted area back toward its original shape (settles on release)");
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

            Add("W", "つまむ", "Grab");
            Add("E", "回す（横ドラッグ。Ctrl で15°刻み）", "Rotate (drag sideways; Ctrl for 15-degree steps)");
            Add("R", "拡大縮小（横ドラッグ。0.2〜3倍）", "Scale (drag sideways; 0.2x to 3x)");
            Add("P", "ピン留め", "Pin");
            Add("Shift+P", "ピン解除", "Unpin");
            Add("T", "元に戻すブラシ", "Restore Brush");
            Add(L10n.Tr("Shift+ドラッグ", "Shift+Drag"), "動きを 1/5 にする（精密操作）", "Move at 1/5 speed (fine control)");
            Add(L10n.Tr("Ctrl+ホイール", "Ctrl+Wheel"), "ブラシ半径", "Brush radius");
            Add(L10n.Tr("Ctrl+Shift+ホイール", "Ctrl+Shift+Wheel"), "影響範囲", "Influence range");
            Add("[  ]", "ブラシ半径（Shift で影響範囲）", "Brush radius (Shift for influence range)");
            Add(L10n.Tr("C（押している間）", "C (hold)"), "元の形を見せる", "Show the original shape");
            Add("Esc", "ドラッグ・仕上げ中の操作を取り消す", "Cancel the stroke being dragged or settled");
            Add("Ctrl+Z / Ctrl+Y", "1操作ずつ戻す・やり直す", "Undo / redo one operation at a time");
            Add(L10n.Tr("Alt+ドラッグ", "Alt+Drag"), "視点を動かす", "Orbit the view");
            Add("F1", "この一覧を出す・隠す", "Show / hide this list");
        }
    }
}
