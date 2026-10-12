using System.Collections.Generic;

namespace GP02.EditorTools
{
    /// <summary>手順の案内の、1つの手順の状態。</summary>
    public enum GuideStepState
    {
        /// <summary>まだ。</summary>
        Todo,

        /// <summary>今やること。</summary>
        Current,

        /// <summary>済んだ。</summary>
        Done,

        /// <summary>飛ばした（任意の手順で、先の手順が済んだ）。</summary>
        Skipped,
    }

    /// <summary>
    /// 初めての人向けの手順（衣装を選ぶ → 編集を開始 → ピン留め → つまんで引く → 保存）の、今どこまで済んでいるかの判定と文章。
    /// 画面に依存しないので、テストで確かめられる。
    /// </summary>
    public static class ClothEditGuide
    {
        public const int StepCount = 5;

        /// <summary>ピン留めの手順の番号（0 始まり）。動かしたくない所が無ければ飛ばしてよい手順。</summary>
        public const int PinStep = 2;

        /// <summary>各手順の状態を result（長さ <see cref="StepCount"/> 以上）に入れる。</summary>
        public static void Evaluate(bool hasTarget, bool editing, bool hasPins, bool edited, bool saved, GuideStepState[] result)
        {
            bool[] done =
            {
                hasTarget || editing,
                editing,
                editing && hasPins,
                edited,
                saved,
            };

            bool currentFound = false;
            for (int i = 0; i < StepCount; i++)
            {
                if (done[i])
                {
                    result[i] = GuideStepState.Done;
                    continue;
                }
                // ピン留めは任意。あとの手順が済んでいたら、飛ばしたことにする
                if (i == PinStep && (done[3] || done[4]))
                {
                    result[i] = GuideStepState.Skipped;
                    continue;
                }
                if (!currentFound)
                {
                    result[i] = GuideStepState.Current;
                    currentFound = true;
                }
                else
                {
                    result[i] = GuideStepState.Todo;
                }
            }
        }

        /// <summary>手順の見出し。</summary>
        public static string Title(int step)
        {
            switch (step)
            {
                case 0: return L10n.Tr("1. 衣装を選ぶ", "1. Choose the clothing");
                case 1: return L10n.Tr("2. 編集を開始", "2. Start editing");
                case 2: return L10n.Tr("3. 動かさない所をピン留め（任意）", "3. Pin what should not move (optional)");
                case 3: return L10n.Tr("4. つまんで引く", "4. Grab and pull");
                default: return L10n.Tr("5. シェイプキーとして保存", "5. Save as a shape key");
            }
        }

        /// <summary>手順の見出しから、「（任意）」を除いた短いもの。折りたたんだ案内の「次: …」の1行に使う。</summary>
        public static string ShortTitle(int step)
        {
            switch (step)
            {
                case 2: return L10n.Tr("3. 動かさない所をピン留め", "3. Pin what should not move");
                default: return Title(step);
            }
        }

        /// <summary>今やる手順（状態が <see cref="GuideStepState.Current"/> の手順）の番号。すべて済んでいれば -1。</summary>
        public static int CurrentStep(IReadOnlyList<GuideStepState> states)
        {
            for (int i = 0; i < StepCount && i < states.Count; i++)
            {
                if (states[i] == GuideStepState.Current) return i;
            }
            return -1;
        }

        /// <summary>
        /// 手順の区切りを折りたたんでいても出す、今の手順の1行（「次: 3. 動かさない所をピン留め」）。
        /// すべて済んでいれば「✓ 手順はすべて済みました」。
        /// </summary>
        public static string NextLine(IReadOnlyList<GuideStepState> states)
        {
            int step = CurrentStep(states);
            return step < 0
                ? L10n.Tr("✓ 手順はすべて済みました", "✓ All steps are done")
                : L10n.Tr("次: ", "Next: ") + ShortTitle(step);
        }

        /// <summary>手順の説明（今やる手順に出す）。</summary>
        public static string Detail(int step)
        {
            switch (step)
            {
                case 0:
                    return L10n.Tr("Hierarchyで衣装のオブジェクトを選ぶと、自動で対象に入ります（右クリックの「GP02 で布を編集」なら、そのまま編集が始まります）。アバター全体を選んだときは、窓に出る「この中の衣装」から選べます。",
                        "Select the clothing object in the Hierarchy and it becomes the target automatically (right-click > \"GP02 で布を編集\" (Edit cloth with GP02) starts editing right away). If you select the whole avatar, pick from \"Outfits in this object\" in the window.");
                case 1:
                    return L10n.Tr("下の大きな「編集を開始」を押します。衣装を編集できる状態にするまで、数秒かかります。",
                        "Press the big \"Start Editing\" button below. It takes a few seconds to get the clothing ready for editing.");
                case 2:
                    return L10n.Tr("「服の付け根（ウエスト・首まわりなど）を自動でピン留め」を押すと、一周まとめて留められます。手で留めるなら、シーンビューで P キー（ピン留め）を押して、動かしたくない所を塗ります（Ctrl+ドラッグで外す、Shift+ドラッグで四角く囲んで留める、ダブルクリックで部品全体）。",
                        "Press \"Auto-Pin the Clothing's Roots\" to pin the waist, neckline and similar places all the way around. To pin by hand, press P (Pin) in the Scene view and paint what should stay (Ctrl+Drag removes, Shift+Drag pins everything inside a box, double-click a whole part).");
                case 3:
                    return L10n.Tr("Wキー（つまむ）で、布をドラッグして引っ張ります。手を離すと形が落ち着きます。Ctrl+Z で戻せます。",
                        "Press W (Grab) and drag the cloth. It settles when you release. Ctrl+Z undoes.");
                default:
                    return L10n.Tr("上の「保存」ボタン（または下の「保存」の区切り）を押します。Inspector で重みを動かして確かめます。",
                        "Press the Save button above (or use the Save section below). Move the weight in the Inspector to check it.");
            }
        }
    }
}
