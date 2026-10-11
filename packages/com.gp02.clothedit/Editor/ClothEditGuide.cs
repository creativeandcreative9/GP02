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

        /// <summary>手順の説明（今やる手順に出す）。</summary>
        public static string Detail(int step)
        {
            switch (step)
            {
                case 0:
                    return L10n.Tr("Hierarchy で衣装のオブジェクトを選ぶと、自動で対象に入ります（右クリックの「GP02 で布を編集」なら、そのまま編集が始まります）。",
                        "Select the clothing object in the Hierarchy and it becomes the target automatically (right-click > \"GP02 で布を編集\" (Edit cloth with GP02) starts editing right away).");
                case 1:
                    return L10n.Tr("下の大きな「編集を開始」を押します。衣装が粒子になるまで数秒かかります。",
                        "Press the big \"Start Editing\" button below. It takes a few seconds to turn the clothing into particles.");
                case 2:
                    return L10n.Tr("シーンビューで P キー（ピン留め）を押し、ウエストなど動かしたくない所を塗ります。Ctrl+ドラッグで外す、Shift+ドラッグで矩形、ダブルクリックで部品全体も留められます。",
                        "In the Scene view press P (Pin) and paint what should stay, such as the waist. Ctrl+Drag removes, Shift+Drag pins a rectangle and double-click a whole part.");
                case 3:
                    return L10n.Tr("W キー（つまむ）で、布をドラッグして引っ張ります。手を離すと形が落ち着きます。Ctrl+Z で戻せます。",
                        "Press W (Grab) and drag the cloth. It settles when you release. Ctrl+Z undoes.");
                default:
                    return L10n.Tr("「保存」の区切りで名前を決め、「シェイプキーとして保存」を押します。Inspector で重みを動かして確かめます。",
                        "In the Save section enter a name and press \"Save as Shape Key\". Move the weight in the Inspector to check it.");
            }
        }
    }
}
