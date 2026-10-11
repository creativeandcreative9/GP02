using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    // 初心者の通しの補助: 窓の並べ方・画面を撮る・窓に描かれた文字を取り出す・窓のボタンのクリック・シーンビューのマウス操作・ログとダイアログの記録
    public partial class BeginnerWalkthroughTests
    {
        const string OutFolder = "/repo/tools/unity/out/walkthrough";

        // run-tests.sh の仮想画面（Xvfb）と同じ大きさ。Unity は 1280x1024 と答えるが、実際の画面は 1280x960
        const int ScreenWidth = 1280;
        const int ScreenHeight = 960;

        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        readonly List<StageRecord> _stages = new List<StageRecord>();
        StageRecord _r;
        double _waitTotalMs;

        ClothEditWindow _cloth;
        SceneView _scene;
        EditorWindow _hierarchy;
        EditorWindow _inspector;
        EditorWindow _console;

        ClothEditController C => _cloth != null ? _cloth.Controller : null;

        // ---- 記録 ----

        void Op(Via via, string text)
        {
            _r.Operations.Add("〔" + ViaLabel(via) + "〕" + text);
            _r.Vias.Add(via);
        }

        void Fact(string text) => _r.Facts.Add(text);

        void Waited(string what, double ms)
        {
            _r.Waits.Add(what + " " + Seconds(ms) + "（" + Millis(ms) + "）");
            _r.WaitMs += ms;
            _waitTotalMs += ms;
        }

        // 進み具合は、コンソールに出さず（初心者が見るコンソールを汚さないため）、ファイルに書く
        static void Progress(string text)
        {
            try
            {
                Directory.CreateDirectory(OutFolder);
                File.AppendAllText(Path.Combine(OutFolder, "progress.log"), DateTime.UtcNow.ToString("HH:mm:ss") + " " + text + "\n");
            }
            catch (Exception)
            {
                // 進み具合が書けなくても、通しは続ける
            }
        }

        // 段階を1つ動かす。途中で例外が出ても、その段階を失敗として記録して、次の段階へ進む
        // （入れ子の IEnumerator は、例外をここで受けられるよう、自分で展開して動かす）
        IEnumerator Stage(string no, string title, string intent, IEnumerator body)
        {
            var record = new StageRecord { No = no, Title = title, Intent = intent };
            _stages.Add(record);
            _r = record;
            Progress("===== " + no + " " + title);
            var timer = Stopwatch.StartNew();
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool more;
                try
                {
                    more = top.MoveNext();
                }
                catch (Exception ex)
                {
                    record.Problem = ex.GetType().Name + ": " + Flat(ex.Message) + "（" + FirstFrame(ex) + "）";
                    Progress("段階が途中で止まりました: " + ex);
                    break;
                }
                if (!more)
                {
                    stack.Pop();
                    continue;
                }
                if (top.Current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }
                yield return top.Current;
            }
            CollectNotifications();
            record.ElapsedMs = timer.Elapsed.TotalMilliseconds;
            Progress("  終わり（" + Seconds(record.ElapsedMs) + "）" + (string.IsNullOrEmpty(record.Problem) ? "" : " 問題あり: " + record.Problem));
        }

        static string FirstFrame(Exception ex)
        {
            string trace = ex.StackTrace ?? "";
            int end = trace.IndexOf('\n');
            return Flat(end > 0 ? trace.Substring(0, end) : trace);
        }

        // ---- 窓の並べ方 ----

        static Type EditorType(string name) => typeof(Editor).Assembly.GetType("UnityEditor." + name);

        static EditorWindow OpenWindow(string typeName)
        {
            Type type = EditorType(typeName);
            return type != null ? EditorWindow.GetWindow(type) : null;
        }

        // 左の列: ヒエラルキーとインスペクター。真ん中: GP02 の窓。右: シーンビューとコンソール（仮想画面 1280x960 を埋める）
        void ArrangeWindows()
        {
            _hierarchy.position = new Rect(0, 0, 275, 330);
            _inspector.position = new Rect(0, 355, 275, 560);
            if (_cloth != null) _cloth.position = new Rect(280, 0, 420, 900);
            _scene.position = new Rect(705, 0, 575, 640);
            if (_console != null) _console.position = new Rect(705, 668, 575, 250);
        }

        // ---- コマを進める ----
        // Docker の1コマは、窓を5つソフトウェア描画で描くので、数百ミリ秒かかる。利用者の PC なら 60 コマ前後/秒で、コマごとに布の計算が約 12ms 進む。
        // 仕上げの時間切れ（既定 2 秒）までに進む計算の量が PC と同じくらいになるよう、つまんでいる間・仕上げの間は、コマの合間に Controller.Tick を足す。
        // 設定の値は変えない（計算の量を PC に合わせるだけ。足した回数は、記録の環境に書く）

        const double SolverDuty = 0.7;
        double _lastPumpTime = double.NaN;
        int _extraTicks;
        bool _holdSolver;   // true の間は、足す計算をしない（仕上げの途中を撮るため）

        IEnumerator Pump(int frames = 6)
        {
            for (int i = 0; i < frames; i++)
            {
                InternalEditorUtility.RepaintAllViews();
                yield return null;
                TopUpSolver();
            }
        }

        void TopUpSolver()
        {
            double now = EditorApplication.timeSinceStartup;
            double elapsed = double.IsNaN(_lastPumpTime) ? 0.0 : Math.Min(now - _lastPumpTime, 1.0);
            ClothEditController controller = C;
            if (controller != null && controller.IsEditing && controller.IsBusy && !_holdSolver)
            {
                double step = Math.Max(1.0, controller.Settings.SolveMilliseconds);
                double budget = elapsed * 1000.0 * SolverDuty - step;
                int guard = 0;
                while (budget > 0.0 && controller.IsBusy && guard++ < 60)
                {
                    controller.Tick(EditorApplication.timeSinceStartup);
                    _extraTicks++;
                    budget -= step;
                }
            }
            _lastPumpTime = EditorApplication.timeSinceStartup;
        }

        // ---- 画面を撮る ----

        static void ShotScreen(string fileBase)
        {
            Directory.CreateDirectory(OutFolder);
            Color[] pixels = InternalEditorUtility.ReadScreenPixel(Vector2.zero, ScreenWidth, ScreenHeight);
            var texture = new Texture2D(ScreenWidth, ScreenHeight, TextureFormat.RGB24, false);
            texture.SetPixels(pixels);
            texture.Apply();
            File.WriteAllBytes(Path.Combine(OutFolder, fileBase + ".png"), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        // 前の画像の布の編集ウィンドウの文字（新しく出た文字を見つけるため）
        HashSet<string> _previousClothLines = new HashSet<string>();

        /// <summary>
        /// 布の編集ウィンドウを一番上へスクロールして（ボタンを押すために下へ回したままだと、状態の行・警告が見えないため）、少し待って画面を撮り、
        /// そのときの窓の文字（布の編集ウィンドウ・シーンビュー・通知）を記録する。
        /// otherWindows に渡した窓（ヒエラルキー・インスペクターなど）の文字も取る。通知は4秒で消えるので、通知が出た操作の直後は frames を小さくする。
        /// </summary>
        IEnumerator Snap(string fileBase, string caption, int frames = 6, params EditorWindow[] otherWindows)
        {
            if (_cloth != null) ScrollWindowTo(false);
            return SnapCore(fileBase, caption, frames, otherWindows);
        }

        /// <summary>Snap と同じだが、窓のスクロールの位置は変えない（つまんでいる途中・下へスクロールした画面を、そのまま撮る）。</summary>
        IEnumerator SnapKeep(string fileBase, string caption, int frames = 6, params EditorWindow[] otherWindows)
        {
            return SnapCore(fileBase, caption, frames, otherWindows);
        }

        IEnumerator SnapCore(string fileBase, string caption, int frames, EditorWindow[] otherWindows)
        {
            yield return Pump(frames);
            var shot = new Snapshot { File = fileBase + ".png", Caption = caption };
            AddNotificationsTo(shot.Notifications);
            ShotScreen(fileBase);
            if (_cloth != null) shot.ClothLines.AddRange(TextLines(Dump(_cloth)));
            if (_scene != null) shot.SceneLines.AddRange(TextLines(Dump(_scene)));
            foreach (EditorWindow other in otherWindows)
            {
                if (other == null) continue;
                shot.OtherLines.Add("【" + other.GetType().Name + "】");
                shot.OtherLines.AddRange(TextLines(Dump(other)));
            }
            var now = new HashSet<string>(shot.ClothLines);
            foreach (string line in shot.ClothLines)
            {
                if (!_previousClothLines.Contains(line)) shot.NewLines.Add(line);
            }
            _previousClothLines = now;
            _r.Snapshots.Add(shot);
            Progress("  画像 " + shot.File);
        }

        // ---- 窓に描かれた文字の取り出し（Unity の IMGUI デバッガと同じ仕組み） ----

        sealed class DrawnItem
        {
            public Rect Rect;
            public string Style = "";
            public string Text = "";
            public string Tooltip = "";
            public string Icon = "";
            public bool Enabled = true;
        }

        /// <summary>窓に描かれたものを、描いた順に取り出す。位置は、窓の左上（タブを含む）を原点にした座標。見える範囲の外は描かれないので、出てこない。</summary>
        static List<DrawnItem> Dump(EditorWindow window)
        {
            var result = new List<DrawnItem>();
            Assembly asm = typeof(Editor).Assembly;
            Type helper = asm.GetType("UnityEditor.GUIViewDebuggerHelper");
            Type drawType = asm.GetType("UnityEditor.IMGUIDrawInstruction");
            Type unifiedType = asm.GetType("UnityEditor.IMGUIInstruction");
            Type viewType = asm.GetType("UnityEditor.GUIView");
            if (helper == null || drawType == null || unifiedType == null || viewType == null) return result;
            object parent = typeof(EditorWindow).GetField("m_Parent", Any).GetValue(window);
            if (parent == null) return result;
            helper.GetMethod("DebugWindow", Any).Invoke(null, new[] { parent });
            try
            {
                viewType.GetMethod("RepaintImmediately", Any).Invoke(parent, null);
                object drawList = Activator.CreateInstance(typeof(List<>).MakeGenericType(drawType));
                helper.GetMethod("GetDrawInstructions", Any).Invoke(null, new[] { drawList });
                object unifiedList = Activator.CreateInstance(typeof(List<>).MakeGenericType(unifiedType));
                helper.GetMethod("GetUnifiedInstructions", Any).Invoke(null, new[] { unifiedList });

                // 描いたもの1つごとの「有効か」は、まとめた一覧のほうにある
                var enabledByDraw = new Dictionary<int, bool>();
                foreach (object u in (IEnumerable)unifiedList)
                {
                    if (Convert.ToInt32(unifiedType.GetField("type").GetValue(u)) != 1) continue;   // 1 = 描いたもの
                    enabledByDraw[(int)unifiedType.GetField("typeInstructionIndex").GetValue(u)] = (bool)unifiedType.GetField("enabled").GetValue(u);
                }
                int index = 0;
                foreach (object d in (IEnumerable)drawList)
                {
                    var content = (GUIContent)drawType.GetField("usedGUIContent").GetValue(d);
                    var style = (GUIStyle)drawType.GetField("usedGUIStyle").GetValue(d);
                    result.Add(new DrawnItem
                    {
                        Rect = (Rect)drawType.GetField("rect").GetValue(d),
                        Style = style != null ? style.name ?? "" : "",
                        Text = content != null ? content.text ?? "" : "",
                        Tooltip = content != null ? content.tooltip ?? "" : "",
                        Icon = content != null && content.image != null ? content.image.name ?? "" : "",
                        Enabled = !enabledByDraw.TryGetValue(index, out bool enabled) || enabled,
                    });
                    index++;
                }
            }
            finally
            {
                helper.GetMethod("StopDebugging", Any).Invoke(null, null);
            }
            return result;
        }

        static string Categorize(DrawnItem item)
        {
            string style = item.Style.ToLowerInvariant();
            if (style == "helpbox")
            {
                string icon = item.Icon.ToLowerInvariant();
                return icon.Contains("error") ? "案内(重大)" : icon.Contains("warn") ? "案内(注意)" : "案内(情報)";
            }
            if (style.Contains("notification")) return "通知";
            if (style.Contains("button")) return "ボタン";
            if (style == "boldlabel" || style.Contains("foldout")) return "見出し";
            if (style.Contains("minilabel")) return "小さい文字";
            if (style.Contains("textfield") || style.Contains("objectfield") || style.Contains("popup") || style.Contains("dropdown")) return "入力欄";
            return "文字";
        }

        // 画面の文字の一覧にする。タブの名前・ギズモの文字・影の重ね描きは除く
        static List<string> TextLines(List<DrawnItem> items)
        {
            var lines = new List<string>();
            foreach (DrawnItem item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Text)) continue;
                string style = item.Style;
                if (style.StartsWith("SC View") || style == "dragtab-label" || style == "CenteredLabel") continue;
                string line = Categorize(item) + (item.Enabled ? "" : "（無効）") + ": " + Flat(item.Text);
                if (lines.Count > 0 && lines[lines.Count - 1] == line) continue;
                lines.Add(line);
            }
            return lines;
        }

        // ---- 窓のボタンのクリック ----

        static bool IsButtonStyle(string style)
        {
            string s = style.ToLowerInvariant();
            return s.Contains("button") && !s.Contains("objectfieldbutton");
        }

        // ExitGUI は、ボタンの処理が描画を打ち切るための例外。Unity が自分で受けるものなので、ここでは成功として扱う
        static bool SendEventSafe(EditorWindow window, Event e)
        {
            try
            {
                return window.SendEvent(e);
            }
            catch (Exception ex) when (ex.GetType().Name == "ExitGUIException")
            {
                return true;
            }
        }

        static void SendClick(EditorWindow window, Vector2 position, int clickCount = 1)
        {
            SendEventSafe(window, new Event { type = EventType.MouseMove, mousePosition = position });
            SendEventSafe(window, new Event { type = EventType.MouseDown, button = 0, clickCount = clickCount, mousePosition = position });
            SendEventSafe(window, new Event { type = EventType.MouseUp, button = 0, clickCount = clickCount, mousePosition = position });
        }

        // シーンビューでキーを押す（Esc など）。マウスはシーンビューの上にあるものとして送る
        void SceneKey(KeyCode key)
        {
            SendEventSafe(_scene, new Event { type = EventType.KeyDown, keyCode = key, mousePosition = _sceneMouse });
            SendEventSafe(_scene, new Event { type = EventType.KeyUp, keyCode = key, mousePosition = _sceneMouse });
        }

        // style が null ならボタンの描き方、"*" ならどの描き方でも、それ以外ならその名前の描き方
        DrawnItem FindItem(EditorWindow window, string text, string style)
        {
            return Dump(window).Find(d => d.Text == text && (style == null ? IsButtonStyle(d.Style) : style == "*" || string.Equals(d.Style, style, StringComparison.OrdinalIgnoreCase)));
        }

        static float ScrollY(EditorWindow window) => ((Vector2)GetField(window, "_scroll")).y;

        // 布の編集ウィンドウの、上の固定の帯（タブ 21px の下の、かんたん・くわしく・言語・説明書・? の帯）の下端。これより上のものは、スクロールしない
        const float BarBottom = 43f;

        // 窓の見える範囲（スクロールする中身）に、すっかり入っているか。上の固定の帯のものは、いつも見える
        bool InView(EditorWindow window, DrawnItem item)
        {
            if (item.Rect.yMax <= BarBottom) return true;
            float bottom = window.position.height + 21f - 28f;   // 下の状態の行の分を除く
            return item.Rect.yMin >= BarBottom + 1f && item.Rect.yMax <= bottom;
        }

        // 布の編集ウィンドウで、見える範囲に無いもの（長くなった中身の下のほう・上のほう）を、マウスホイールで探す。回した回数を wheels に入れる
        DrawnItem FindWithScroll(EditorWindow window, string text, string style, out int wheels)
        {
            wheels = 0;
            DrawnItem item = FindItem(window, text, style);
            if (window != _cloth) return item;
            float middle = (BarBottom + window.position.height + 21f - 28f) * 0.5f;
            var wheelPosition = new Vector2(window.position.width * 0.5f, 400f);
            for (int phase = 0; phase < 2; phase++)
            {
                float direction = phase == 0 ? 1f : -1f;   // まず下へ。見つからなければ上へ
                for (int i = 0; i < 80; i++)
                {
                    if (item != null && InView(window, item)) return item;
                    float before = ScrollY(window);
                    float step = 5f;
                    if (item != null)
                    {
                        // 見つかったが、端に寄っている: 真ん中のほうへ
                        direction = item.Rect.center.y > middle ? 1f : -1f;
                        step = 3f;
                    }
                    SendEventSafe(window, new Event { type = EventType.ScrollWheel, delta = new Vector2(0f, direction * step), mousePosition = wheelPosition });
                    wheels++;
                    // スクロールの位置（_scroll）は、描き直したときに窓の変数へ入るので、描き直してから読む（FindItem も描き直す）
                    item = FindItem(window, text, style);
                    if (Mathf.Approximately(ScrollY(window), before)) break;   // これ以上動かない（端まで来た）
                }
                if (item != null) break;
            }
            return item;
        }

        double _lastClickMs;   // 直前のクリックを送るのにかかった時間（ボタンの処理が終わるまで戻らないので、利用者が待つ時間になる）

        /// <summary>
        /// 窓に描かれた「text」の文字のボタン（style が null ならボタン、そうでなければその名前の描き方）を探して、その真ん中をクリックする。
        /// 見つからなければ false。布の編集ウィンドウで、見える範囲の外にあるときは、マウスホイールでスクロールして探す。
        /// </summary>
        bool ClickText(EditorWindow window, string text, out string how, string style = null)
        {
            DrawnItem item = FindWithScroll(window, text, style, out int wheels);
            string scrolled = wheels > 0 ? "（窓の中身が長いので、マウスホイールを " + wheels + " 回まわして、見える所へスクロールした）" : "";
            if (item == null)
            {
                how = "『" + text + "』が窓に描かれていなかった" + (wheels > 0 ? "（マウスホイールで上下にスクロールしても見つからなかった）" : "");
                return false;
            }
            Vector2 center = item.Rect.center;
            var clickTimer = Stopwatch.StartNew();
            SendClick(window, center);
            _lastClickMs = clickTimer.Elapsed.TotalMilliseconds;
            how = "『" + text + "』（描かれた位置 " + center.x.ToString("F0") + "," + center.y.ToString("F0") + "、" + (item.Enabled ? "有効" : "無効（灰色）") + "）をクリック" + scrolled;
            CollectNotifications();
            return true;
        }

        string ButtonState(EditorWindow window, string text)
        {
            DrawnItem item = FindWithScroll(window, text, null, out _);
            return item == null ? "描かれていない" : item.Enabled ? "有効" : "無効（灰色）";
        }

        // 布の編集ウィンドウの中身を、一番上・一番下までスクロールする（マウスホイール）
        void ScrollWindowTo(bool bottom)
        {
            var wheelPosition = new Vector2(_cloth.position.width * 0.5f, 400f);
            for (int i = 0; i < 80; i++)
            {
                float before = ScrollY(_cloth);
                SendEventSafe(_cloth, new Event { type = EventType.ScrollWheel, delta = new Vector2(0f, bottom ? 8f : -8f), mousePosition = wheelPosition });
                RepaintNow(_cloth);
                if (Mathf.Approximately(ScrollY(_cloth), before)) break;
            }
        }

        // 窓をすぐに描き直す（スクロールの位置など、描いたときに決まる値を、すぐ読めるようにする）
        static void RepaintNow(EditorWindow window)
        {
            Type viewType = typeof(Editor).Assembly.GetType("UnityEditor.GUIView");
            object parent = typeof(EditorWindow).GetField("m_Parent", Any).GetValue(window);
            if (viewType == null || parent == null) return;
            viewType.GetMethod("RepaintImmediately", Any).Invoke(parent, null);
        }

        // ---- リフレクション ----

        static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Any);
            Assert.That(field, Is.Not.Null, name);
            return field.GetValue(target);
        }

        static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Any);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        static object Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name, Any);
            Assert.That(method, Is.Not.Null, name);
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null && ex.InnerException.GetType().Name == "ExitGUIException")
            {
                return null;
            }
        }

        // ---- 通知（ShowNotification）の読み取り ----

        readonly Dictionary<EditorWindow, string> _lastNotification = new Dictionary<EditorWindow, string>();

        static string ReadNotification(EditorWindow window, out float fadeout)
        {
            fadeout = 0f;
            if (window == null) return null;
            FieldInfo field = typeof(EditorWindow).GetField("m_Notification", Any);
            FieldInfo fade = typeof(EditorWindow).GetField("m_FadeoutTime", Any);
            if (fade != null) fadeout = (float)fade.GetValue(window);
            var content = field != null ? field.GetValue(window) as GUIContent : null;
            return content != null ? content.text : null;
        }

        // 今出ている通知を、段階の記録に足す（同じ通知は、1回だけ）
        void CollectNotifications()
        {
            if (_r == null) return;
            foreach (EditorWindow window in new EditorWindow[] { _cloth, _scene })
            {
                string text = ReadNotification(window, out float fadeout);
                if (string.IsNullOrEmpty(text)) continue;
                string key = text + "#" + fadeout.ToString("F3");
                if (_lastNotification.TryGetValue(window, out string last) && last == key) continue;
                _lastNotification[window] = key;
                string where = window == _scene ? "シーンビュー" : "布の編集ウィンドウ";
                _r.Notifications.Add(where + ": 「" + Flat(text) + "」");
            }
        }

        void AddNotificationsTo(List<string> target)
        {
            foreach (EditorWindow window in new EditorWindow[] { _cloth, _scene })
            {
                string text = ReadNotification(window, out _);
                if (string.IsNullOrEmpty(text)) continue;
                target.Add((window == _scene ? "シーンビュー: " : "布の編集ウィンドウ: ") + "「" + Flat(text) + "」");
            }
            CollectNotifications();
        }

        // ---- コンソールと確認のダイアログの記録 ----

        const string DialogPrefix = "Cancelling DisplayDialog:";
        string _lastConsoleLine = "";
        int _lastConsoleRepeat;

        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (_r == null || condition == null) return;
            if (type == LogType.Assert && condition.StartsWith(DialogPrefix))
            {
                // EditorUtility.DisplayDialog は、バッチモードでは表示されずに、この文がログに出てキャンセル扱いになる
                // 「題名 本文」の順につながって出る。GP02 のダイアログの題名は「GP02」
                string text = condition.Substring(DialogPrefix.Length).Trim();
                int tail = text.IndexOf("\nThis should not be called in batch mode.", StringComparison.Ordinal);
                if (tail >= 0) text = text.Substring(0, tail);
                string title = "題名は不明";
                if (text.StartsWith("GP02 ", StringComparison.Ordinal))
                {
                    title = "GP02";
                    text = text.Substring(5);
                }
                _r.Dialogs.Add("『" + title + "』 " + Flat(text) + "（実機では、OK などのボタンを押すまで止まるダイアログ）");
                return;
            }
            string kind = type == LogType.Log ? "ログ" : type == LogType.Warning ? "警告" : type == LogType.Error ? "エラー" : type == LogType.Exception ? "例外" : "アサート";
            string line = "[" + kind + "] " + Flat(condition);
            if (line.Length > 400) line = line.Substring(0, 400) + "…";
            if (line == _lastConsoleLine && _r.Console.Count > 0)
            {
                _lastConsoleRepeat++;
                _r.Console[_r.Console.Count - 1] = line + "（" + (_lastConsoleRepeat + 1) + " 回）";
                return;
            }
            _lastConsoleLine = line;
            _lastConsoleRepeat = 0;
            _r.Console.Add(line);
        }

        // 保存していない編集の確認（ClothEditPrompts.AskUnsaved）は、記録して、決めておいた答えを返す
        readonly Queue<ClothEditUnsavedChoice> _unsavedAnswers = new Queue<ClothEditUnsavedChoice>();

        ClothEditUnsavedChoice AskUnsavedRecorder(ClothEditLeaveReason reason)
        {
            ClothEditUnsavedChoice choice = _unsavedAnswers.Count > 0 ? _unsavedAnswers.Dequeue() : ClothEditUnsavedChoice.Cancel;
            string label = choice == ClothEditUnsavedChoice.Save ? "保存する" : choice == ClothEditUnsavedChoice.Discard ? "保存しないで終わる" : "やめる";
            _r.Dialogs.Add("『GP02』 " + ClothEditPrompts.UnsavedMessage(reason) + "（ボタン: 保存する ／ やめる ／ 保存しないで終わる。ここでは「" + label + "」を選んだことにした）");
            return choice;
        }

        bool ConfirmRecorder(string title, string message, string ok, string cancel)
        {
            _r.Dialogs.Add("『" + title + "』 " + Flat(message) + "（ボタン: " + ok + " ／ " + cancel + "。ここでは「" + ok + "」を選んだことにした）");
            return true;
        }

        // ---- シーンビューのマウス操作 ----
        // 窓のボタンのクリックと同じく、SendEvent でマウスのイベントを送る。位置は、世界の点を画面の点にして渡す。
        // SendEvent に渡す位置と、カメラの画面の位置との食い違い（タブ・ツールバーの高さ）は、実際にマウスを乗せて測って補正する

        Vector2 _sceneCal = new Vector2(float.NaN, float.NaN);
        Vector2 _sceneMouse;

        bool SceneCalibrated => !float.IsNaN(_sceneCal.x);

        Vector2 ScenePoint(Vector3 world)
        {
            Camera camera = _scene.camera;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            Vector3 s = camera.WorldToScreenPoint(world);
            if (!SceneCalibrated) return new Vector2(s.x / ppp, (camera.pixelHeight - s.y) / ppp);
            return new Vector2(s.x / ppp + _sceneCal.x, _sceneCal.y - s.y / ppp);
        }

        void SceneMouse(EventType type, Vector2 position, bool shift = false, bool control = false, int clickCount = 1)
        {
            var e = new Event
            {
                type = type,
                mousePosition = position,
                button = 0,
                shift = shift,
                control = control,
                clickCount = clickCount,
                delta = position - _sceneMouse,
            };
            _sceneMouse = position;
            SendEventSafe(_scene, e);
        }

        /// <summary>probe（布の上の点）にマウスを乗せて、実際に当たった点から、イベントの位置とカメラの画面の位置の食い違いを測る。</summary>
        bool CalibrateScene(Vector3 probe)
        {
            Camera camera = _scene.camera;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            Vector3 s = camera.WorldToScreenPoint(probe);
            foreach (float dy in new[] { 0f, 21f, 47f, 26f, -21f, 68f })
            {
                var g = new Vector2(s.x / ppp, (camera.pixelHeight - s.y) / ppp + dy);
                SceneMouse(EventType.MouseMove, g);
                if (!C.HasHover) continue;
                Vector3 hit = camera.WorldToScreenPoint(C.HoverPoint);
                _sceneCal = new Vector2(g.x - hit.x / ppp, g.y + hit.y / ppp);
                return true;
            }
            return false;
        }

        // 世界の点にマウスを乗せる
        IEnumerator HoverAt(Vector3 world, int frames = 3)
        {
            SceneMouse(EventType.MouseMove, ScenePoint(world));
            yield return Pump(frames);
        }

        static List<Vector3> Line(Vector3 from, Vector3 to, int steps)
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= steps; i++) points.Add(Vector3.Lerp(from, to, (float)i / steps));
            return points;
        }

        /// <summary>押して、points の順に動かして、離す（離さないなら release を false に）。1回動かすごとに framesPerStep コマ進める。</summary>
        IEnumerator DragPath(IList<Vector3> points, int framesPerStep = 1, bool release = true, bool shift = false, bool control = false)
        {
            SceneMouse(EventType.MouseMove, ScenePoint(points[0]), shift, control);
            yield return Pump(2);
            SceneMouse(EventType.MouseDown, ScenePoint(points[0]), shift, control);
            for (int i = 1; i < points.Count; i++)
            {
                SceneMouse(EventType.MouseDrag, ScenePoint(points[i]), shift, control);
                if (framesPerStep > 0) yield return Pump(framesPerStep);
            }
            if (release)
            {
                SceneMouse(EventType.MouseUp, ScenePoint(points[points.Count - 1]), shift, control);
                yield return Pump(2);
            }
        }

        // 押したままの続きを動かす
        IEnumerator ContinueDrag(IList<Vector3> points, int framesPerStep = 1)
        {
            for (int i = 0; i < points.Count; i++)
            {
                SceneMouse(EventType.MouseDrag, ScenePoint(points[i]));
                if (framesPerStep > 0) yield return Pump(framesPerStep);
            }
        }

        // マウスを離す。frames が 0 なら、離したあとコマを進めない（離した直後の状態を撮るため）
        IEnumerator ReleaseAt(Vector3 world, int frames = 2)
        {
            SceneMouse(EventType.MouseUp, ScenePoint(world));
            if (frames > 0) yield return Pump(frames);
        }

        IEnumerator SetView(Vector3 pivot, float pitch, float yaw, float size)
        {
            _scene.LookAt(pivot, Quaternion.Euler(pitch, yaw, 0f), size, false, true);
            yield return Pump(4);
        }

        // 仕上げ（つまんだあと・動かしたあとの落ち着く計算）が終わるまで、コマを進めながら待つ
        double _lastSettleMs;

        IEnumerator WaitUntilIdle(int maxFrames = 600)
        {
            var timer = Stopwatch.StartNew();
            int frames = 0;
            while (C != null && C.IsEditing && C.IsBusy && frames < maxFrames)
            {
                yield return Pump(1);
                frames++;
            }
            _lastSettleMs = timer.Elapsed.TotalMilliseconds;
        }

        // ---- テスト用のアバターの形（TestAvatarMenu の定数から） ----

        static float SkirtRadius(float y)
        {
            float t = Mathf.InverseLerp(TestAvatarMenu.SkirtTopY, TestAvatarMenu.SkirtBottomY, y);
            return Mathf.Lerp(TestAvatarMenu.SkirtTopRadius, TestAvatarMenu.SkirtBottomRadius, t);
        }

        /// <summary>スカートの元の形の表面の点。degrees は、正面（+Z）が 0 度で、+X 側がプラス。</summary>
        static Vector3 SkirtPoint(float degrees, float y)
        {
            float r = SkirtRadius(y);
            float a = degrees * Mathf.Deg2Rad;
            return new Vector3(r * Mathf.Sin(a), y, r * Mathf.Cos(a));
        }

        // 最初（編集する前）のスカートの頂点。アバターは原点にあるので、メッシュの座標が、そのままワールドの座標
        Vector3[] _skirtRest;

        /// <summary>
        /// 元の形で SkirtPoint の点にいちばん近い頂点の、いまの位置。布を引いて形が変わったあとでも、同じ所（裾の右前など）を狙える
        /// （元の形の位置のまま狙うと、形が変わった布から外れる）。編集していなければ、元の形の点。
        /// </summary>
        Vector3 SurfacePoint(float degrees, float y)
        {
            Vector3 target = SkirtPoint(degrees, y);
            ClothEditSession session = C != null ? C.Session : null;
            if (session == null || _skirtRest == null) return target;
            Vector3[] now = session.CurrentWorldVertices;
            if (now == null || now.Length != _skirtRest.Length) return target;
            int best = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < _skirtRest.Length; i++)
            {
                float d = (_skirtRest[i] - target).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = i;
                }
            }
            return now[best];
        }

        // 輪を描くように、角度 fromDegrees から toDegrees まで、高さ y の線の上の点（いまの布の表面）
        List<Vector3> Arc(float fromDegrees, float toDegrees, float y, int steps)
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= steps; i++) points.Add(SurfacePoint(Mathf.Lerp(fromDegrees, toDegrees, (float)i / steps), y));
            return points;
        }

        // ピンの粒子を、スカートの前半分（z が 0 以上）と後ろ半分に分けて数える
        static void CountPins(ClothEditSession session, out int front, out int back)
        {
            front = 0;
            back = 0;
            if (session == null) return;
            ReadOnlySpan<System.Numerics.Vector3> positions = session.Solver.Positions;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!session.Solver.IsPinned(i)) continue;
                if (positions[i].Z >= 0f) front++;
                else back++;
            }
        }

        // 粒子の元の位置からどれだけ動いたか（最大・センチメートル）
        static float MaxMovedCm(ClothEditSession session)
        {
            if (session == null) return 0f;
            System.Numerics.Vector3[] rest = session.Cloth.Model.RestPositions;
            ReadOnlySpan<System.Numerics.Vector3> now = session.Solver.Positions;
            float max = 0f;
            for (int i = 0; i < rest.Length && i < now.Length; i++) max = Mathf.Max(max, (now[i] - rest[i]).Length());
            return max * 100f;
        }
    }
}
