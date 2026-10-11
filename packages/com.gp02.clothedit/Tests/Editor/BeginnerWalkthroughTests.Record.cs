using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GP02.Tests
{
    // 初心者の通しの「記録」: 段階ごとの操作・画面の文字・かかった時間・画像を集めて、walkthrough.md にする
    public partial class BeginnerWalkthroughTests
    {
        /// <summary>操作をどの経路で行ったか。「画面と同じ経路」かどうかを、記録を読む人が見分けるための印。</summary>
        enum Via
        {
            /// <summary>画面と同じ経路: 窓のボタンへのクリック・シーンビューやヒエラルキーへのマウスのイベント。</summary>
            Real,

            /// <summary>メニューを選んだのと同じ（EditorApplication.ExecuteMenuItem）。</summary>
            Menu,

            /// <summary>ボタンが呼ぶ処理を、直接呼んだ（実クリックが効かなかった・押せない操作）。</summary>
            Button,

            /// <summary>Controller などの部品を直接呼んだ。</summary>
            Controller,

            /// <summary>ポップアップ・スライダーなどの代わりに、設定値を直接変えた。</summary>
            Setting,

            /// <summary>操作ではないメモ（テストの都合・補足）。</summary>
            Note,
        }

        static string ViaLabel(Via via)
        {
            switch (via)
            {
                case Via.Real: return "実操作";
                case Via.Menu: return "メニュー";
                case Via.Button: return "ボタン処理を直接";
                case Via.Controller: return "Controller を直接";
                case Via.Setting: return "設定値を直接";
                default: return "メモ";
            }
        }

        /// <summary>1枚の画像と、そのときの画面の文字。</summary>
        sealed class Snapshot
        {
            public string File = "";
            public string Caption = "";

            /// <summary>撮る直前に読んだ、窓の通知（ShowNotification）。</summary>
            public readonly List<string> Notifications = new List<string>();

            /// <summary>布の編集ウィンドウに描かれていた文字（上から順）。</summary>
            public readonly List<string> ClothLines = new List<string>();

            /// <summary>シーンビューに描かれていた文字（左上の案内など）。</summary>
            public readonly List<string> SceneLines = new List<string>();

            /// <summary>ヒエラルキー・インスペクター・一覧のウィンドウなど、そのほかの窓の文字。</summary>
            public readonly List<string> OtherLines = new List<string>();

            /// <summary>前の画像の布の編集ウィンドウから、新しく出た・変わった文字。</summary>
            public readonly List<string> NewLines = new List<string>();
        }

        /// <summary>1つの段階の記録。</summary>
        sealed class StageRecord
        {
            public string No = "";
            public string Title = "";
            public string Intent = "";
            public readonly List<string> Operations = new List<string>();
            public readonly HashSet<Via> Vias = new HashSet<Via>();
            public readonly List<string> Facts = new List<string>();
            public readonly List<string> Notifications = new List<string>();
            public readonly List<string> Dialogs = new List<string>();
            public readonly List<string> Console = new List<string>();
            public readonly List<string> Waits = new List<string>();
            public readonly List<Snapshot> Snapshots = new List<Snapshot>();
            public double ElapsedMs;
            public double WaitMs;
            public string Problem = "";
        }

        sealed class EnvironmentInfo
        {
            public string Unity = "";
            public string Graphics = "";
            public string Burst = "";
            public string Language = "";
            public string StartedUtc = "";
            public string Preferences = "";
            public readonly List<string> Notes = new List<string>();
        }

        static string Flat(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\r", "").Replace("\n", " ／ ").Trim();
        }

        static string Seconds(double ms)
        {
            return (ms / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " 秒";
        }

        static string Millis(double ms)
        {
            return ms.ToString("N0", CultureInfo.InvariantCulture) + " ms";
        }

        static void AppendList(StringBuilder sb, string heading, List<string> items, string emptyText)
        {
            sb.Append("- ").Append(heading);
            if (items.Count == 0)
            {
                sb.Append(": ").AppendLine(emptyText);
                return;
            }
            sb.AppendLine(":");
            foreach (string item in items)
            {
                sb.Append("  - ").AppendLine(item);
            }
        }

        static string BuildMarkdown(IReadOnlyList<StageRecord> stages, EnvironmentInfo env, double totalMs, double waitMs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 初心者の通し（自動再現）の記録");
            sb.AppendLine();
            sb.AppendLine("Unity 初心者が、初めて GP02 を使って衣装（スカート）を編集し、シェイプキーとして保存するまでの操作を、自動で最初から最後まで再現して撮った記録です。");
            sb.AppendLine("画像は同じフォルダにあります（左上から ヒエラルキー・インスペクター ／ GP02 布の編集ウィンドウ ／ シーンビュー・コンソール）。");
            sb.AppendLine("批評・改善は、この記録と画像を見て別に行います（このテストは製品のコードを変えていません）。");
            sb.AppendLine();
            sb.AppendLine("## 環境");
            sb.AppendLine();
            sb.AppendLine("- 実行: " + env.StartedUtc);
            sb.AppendLine("- Unity: " + env.Unity);
            sb.AppendLine("- 画面: " + env.Graphics);
            sb.AppendLine("- Burst（高速版）: " + env.Burst);
            sb.AppendLine("- 言語・表示: " + env.Language);
            sb.AppendLine("- 初めて使う人の状態: " + env.Preferences);
            foreach (string note in env.Notes) sb.AppendLine("- " + note);
            sb.AppendLine();
            sb.AppendLine("## 見方");
            sb.AppendLine();
            sb.AppendLine("操作ごとに、画面と同じ経路かどうかを次の印で書いています。");
            sb.AppendLine();
            sb.AppendLine("- 〔実操作〕 画面と同じ経路。窓のボタンの位置（描かれた位置を調べて）へのクリック、シーンビューやヒエラルキーへのマウスのイベント");
            sb.AppendLine("- 〔メニュー〕 メニュー項目を選んだのと同じ（EditorApplication.ExecuteMenuItem）");
            sb.AppendLine("- 〔ボタン処理を直接〕 ボタンが呼ぶ処理を直接呼んだ（実クリックが効かなかった・そもそも押せない操作の確認）");
            sb.AppendLine("- 〔Controller を直接〕 ClothEditController などを直接呼んだ");
            sb.AppendLine("- 〔設定値を直接〕 ポップアップ・スライダーなどの代わりに、設定の値を直接変えた");
            sb.AppendLine("- 〔メモ〕 操作ではない補足（シーンビューの視点を変えた・テストの都合で選択を外した、など。実際は Alt+ドラッグやギズモで視点を回す）");
            sb.AppendLine();
            sb.AppendLine("「画面に出た文字」は次の3つを分けて書いています。");
            sb.AppendLine();
            sb.AppendLine("- ウィンドウの通知: `ShowNotification` で窓の真ん中に出る一言（4秒で消える）。操作の直後に読んだもの");
            sb.AppendLine("- 確認のダイアログ: 実機では OK を押すまで止まるダイアログ。バッチモードの Unity は表示せずに自動でキャンセルし、その文をログに出すので、そこから読んだもの。保存していない編集の確認（保存する／やめる／保存しないで終わる）は、`ClothEditPrompts` の差し替えで読んだもの");
            sb.AppendLine("- コンソール: Unity のコンソールに出た警告・エラー・ログ（`[GP02]` で始まる情報ログも含む）");
            sb.AppendLine();
            sb.AppendLine("時間は、Docker の Unity（Linux・CPU を共有・ソフトウェア描画）で測ったものです。利用者の PC とは違うので、絶対値ではなく、待たされるかどうか・相対的な長さを見てください。");
            sb.AppendLine();

            sb.AppendLine("## 一覧");
            sb.AppendLine();
            sb.AppendLine("| 段階 | 内容 | 経路 | 待ち時間 | 画像 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (StageRecord stage in stages)
            {
                var vias = new List<string>();
                foreach (Via via in new[] { Via.Real, Via.Menu, Via.Button, Via.Controller, Via.Setting })
                {
                    if (stage.Vias.Contains(via)) vias.Add(ViaLabel(via));
                }
                string images = stage.Snapshots.Count == 0 ? "なし" : string.Join("<br>", stage.Snapshots.ConvertAll(s => s.File));
                string wait = stage.WaitMs > 0 ? Seconds(stage.WaitMs) : "-";
                string status = string.IsNullOrEmpty(stage.Problem) ? "" : "（問題あり）";
                sb.Append("| ").Append(stage.No).Append(" | ").Append(stage.Title).Append(status).Append(" | ").Append(string.Join("、", vias))
                    .Append(" | ").Append(wait).Append(" | ").Append(images).AppendLine(" |");
            }
            sb.AppendLine();

            foreach (StageRecord stage in stages)
            {
                sb.AppendLine("## " + stage.No + " " + stage.Title);
                sb.AppendLine();
                sb.AppendLine("- 初心者がしようとしたこと: " + stage.Intent);
                AppendList(sb, "実際にした操作", stage.Operations, "なし");
                AppendList(sb, "ウィンドウの通知（ShowNotification）", stage.Notifications, "なし");
                AppendList(sb, "確認のダイアログの文", stage.Dialogs, "なし");
                AppendList(sb, "コンソールの警告・エラー・ログ", stage.Console, "なし");
                if (stage.Facts.Count > 0) AppendList(sb, "確かめた値", stage.Facts, "なし");
                sb.Append("- かかった時間: ");
                if (stage.Waits.Count == 0) sb.Append("利用者が待つ操作なし");
                else sb.Append(string.Join("、", stage.Waits));
                sb.AppendLine("（この段階の所要 " + Seconds(stage.ElapsedMs) + "。撮影・描画の待ちを含む）");
                if (!string.IsNullOrEmpty(stage.Problem)) sb.AppendLine("- 問題: " + stage.Problem);
                sb.AppendLine();

                foreach (Snapshot shot in stage.Snapshots)
                {
                    sb.AppendLine("### 画像 `" + shot.File + "`");
                    sb.AppendLine();
                    sb.AppendLine("![" + shot.File + "](" + shot.File + ")");
                    sb.AppendLine();
                    sb.AppendLine("- 撮ったもの: " + shot.Caption);
                    if (shot.Notifications.Count > 0) sb.AppendLine("- そのとき窓に出ていた通知: " + string.Join(" ／ ", shot.Notifications));
                    if (shot.NewLines.Count > 0)
                    {
                        sb.AppendLine("- 前の画像から新しく出た・変わった、布の編集ウィンドウの文字:");
                        foreach (string line in shot.NewLines) sb.AppendLine("  - " + line);
                    }
                    AppendDetails(sb, "布の編集ウィンドウに描かれていた文字（全部）", shot.ClothLines);
                    AppendDetails(sb, "シーンビューに描かれていた文字", shot.SceneLines);
                    AppendDetails(sb, "そのほかの窓の文字", shot.OtherLines);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("## 全体の時間");
            sb.AppendLine();
            sb.AppendLine("- 通し全体（撮影・描画の待ちを含む）: " + Seconds(totalMs));
            sb.AppendLine("- 利用者が待つ操作（編集の開始・仕上げ・動作チェック・保存など）の合計: " + Seconds(waitMs));
            sb.AppendLine();
            sb.AppendLine("段階ごとの待ち時間:");
            sb.AppendLine();
            foreach (StageRecord stage in stages)
            {
                if (stage.Waits.Count == 0) continue;
                sb.AppendLine("- " + stage.No + " " + stage.Title + ": " + string.Join("、", stage.Waits));
            }
            sb.AppendLine();
            return sb.ToString();
        }

        static void AppendDetails(StringBuilder sb, string title, List<string> lines)
        {
            if (lines.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine("<details><summary>" + title + "（" + lines.Count + " 行）</summary>");
            sb.AppendLine();
            sb.AppendLine("```text");
            foreach (string line in lines) sb.AppendLine(line);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }
    }
}
