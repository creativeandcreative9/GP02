using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 編集が消えない・始めやすい・Burst の案内:
    //  - 「編集を終了」・別の衣装への切り替えで、保存していない編集を確認する
    //  - スクリプトのコンパイル・プレイモード・ウィンドウを閉じるときに、編集中の状態を取っておき（ClothEditStash）、戻ったときに帯で知らせる
    //  - ヒエラルキーで選んだ衣装を、編集していないときは自動で対象にする。右クリック「GP02 で布を編集」から1回で始める
    //  - Burst が無いとき、初めて開いたときに1回だけ、帯で案内する
    public sealed partial class ClothEditWindow
    {
        // 今の対象に合う取っておき（ヘッダーだけ）。無ければ null
        ClothEditStash.Record _stashInfo;
        readonly List<Renderer> _stashTargets = new List<Renderer>();   // 取っておきを探したときの対象
        bool _stashSearched;
        string _stashMessage;                                           // 戻せなかった理由（帯に出す）

        // 取っておきを書いた時点の状態。同じ状態のまま、もう一度書かない（再読み込みの前と、ウィンドウを閉じるときの二重書きを避ける）
        ClothEditSession _stashedSession;
        int _stashedShape = -1;
        int _stashedPins = -1;
        int _stashedSelection = -1;

        // Burst の初回案内。初めて開いたときだけ決める（再読み込みをまたいで覚えておく）
        [SerializeField] bool _burstBandDecided;
        [SerializeField] bool _burstBandVisible;

        /// <summary>このウィンドウの操作の部品（テスト用）。</summary>
        internal ClothEditController Controller => _controller;

        /// <summary>最後に出した通知の文（テスト用。通知は短くする約束を確かめる）。</summary>
        internal string LastToast { get; private set; } = "";

        // ウィンドウに一言出す。まだ表示されていないウィンドウ（テストなど）では出せないので、失敗しても続ける
        void Toast(string text)
        {
            LastToast = text;
            try
            {
                ShowNotification(new GUIContent(text));
            }
            catch (System.Exception)
            {
                // 表示されていないウィンドウでは、出さなくてよい
            }
        }

        /// <summary>Burst の初回案内を出すか。Burst が無く、まだ案内を出したことが無いとき。</summary>
        internal static bool ShouldShowBurstGuide(bool burstInstalled, bool alreadyShown) => !burstInstalled && !alreadyShown;

        // ---- 取っておく ----

        /// <summary>今の編集を取っておく。取っておく価値が無ければ（何も変えていない）何もしない。</summary>
        internal void StashNow()
        {
            if (_controller == null || !_controller.IsEditing) return;
            ClothEditSession session = _controller.Session;
            if (session == null) return;
            if (ReferenceEquals(_stashedSession, session) && _stashedShape == session.ShapeVersion && _stashedPins == session.PinnedCount
                && _stashedSelection == _controller.SelectionVersion)
            {
                return;
            }
            ClothEditStash.Record record = ClothEditStash.Capture(_controller);
            if (record == null || !ClothEditStash.Write(record)) return;
            _stashedSession = session;
            _stashedShape = session.ShapeVersion;
            _stashedPins = session.PinnedCount;
            _stashedSelection = _controller.SelectionVersion;
        }

        void OnBeforeAssemblyReload()
        {
            StashNow();
            ClothEditToolHandles.Restore(this);   // 再読み込みをまたいで、Unity の道具のハンドルを隠したままにしない
        }

        // 取っておきを探し直す。対象が変わったとき・プレイモードから戻ったとき・ウィンドウを開いたとき・保存や破棄のあと
        internal void RefreshStashInfo(bool force)
        {
            List<Renderer> targets = CollectTargets();
            if (!force && _stashSearched && SameList(_stashTargets, targets)) return;
            _stashSearched = true;
            _stashTargets.Clear();
            _stashTargets.AddRange(targets);
            _stashMessage = null;
            ClothEditStash.TryFind(targets, out _stashInfo);
        }

        /// <summary>前回の編集が残っているか（帯を出すか）。編集中は出さない。</summary>
        internal bool HasStashBand => _controller != null && !_controller.IsEditing && _stashInfo != null;

        internal ClothEditStash.Record StashInfo => _stashInfo;

        /// <summary>取っておきを戻せなかった理由（帯に出す。無ければ空）。</summary>
        internal string StashMessage => _stashMessage;

        /// <summary>今の対象（1つ目と、重ね着の2つ目以降）。</summary>
        internal List<Renderer> Targets => CollectTargets();

        static bool SameList(List<Renderer> a, List<Renderer> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // 保存したら、取っておきは要らない
        void AfterSaved()
        {
            ClothEditStash.DeleteFor(_controller.Session != null ? _controller.Session.Renderers : CollectTargets());
            _stashedSession = null;
            _stashInfo = null;
        }

        /// <summary>取っておいた編集を戻す。編集を始めて、位置・ピン・選択を戻す。戻せなければ、始めた編集は終えて、理由を帯に出す。</summary>
        internal bool RestoreFromStash()
        {
            if (_stashInfo == null || _controller.IsEditing) return false;
            if (!ClothEditStash.TryRead(ClothEditStash.PathForKey(_stashInfo.Key), false, out ClothEditStash.Record record))
            {
                _stashMessage = L10n.Tr("取っておいた編集を読めませんでした。", "The stashed edit could not be read.");
                return false;
            }
            if (!StartEdit()) return false;

            if (!ClothEditStash.MatchesSession(record, _controller.Session, out string reason)
                || !_controller.RestoreEditState(record.Positions, record.PinnedParticles, record.SelectedParticles))
            {
                // 戻さずに知らせる。取っておきは残すので、元に戻してからもう一度試せる
                _controller.End();
                _stashMessage = string.IsNullOrEmpty(reason) ? L10n.Tr("取っておいた編集を戻せませんでした。", "The stashed edit could not be restored.") : reason;
                RefreshStashInfoKeepMessage();
                return false;
            }
            Toast(L10n.Tr("前回の編集を戻しました", "Restored the previous edit"));
            _stashInfo = null;
            return true;
        }

        void RefreshStashInfoKeepMessage()
        {
            string message = _stashMessage;
            RefreshStashInfo(true);
            _stashMessage = message;
        }

        /// <summary>取っておいた編集を捨てる。</summary>
        internal void DiscardStash()
        {
            if (_stashInfo != null) ClothEditStash.DeleteKey(_stashInfo.Key);
            _stashInfo = null;
            _stashMessage = null;
        }

        // ---- ウィンドウの上の帯 ----

        void DrawBands()
        {
            DrawRestoreBand();
            DrawBurstBand();
        }

        void DrawRestoreBand()
        {
            if (!HasStashBand) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L10n.Tr("前回の編集が残っています", "A previous edit was kept"), EditorStyles.boldLabel);
                EditorGUILayout.LabelField(ClothEditStash.Describe(_stashInfo, System.DateTime.UtcNow), EditorStyles.miniLabel);
                if (!string.IsNullOrEmpty(_stashMessage)) EditorGUILayout.HelpBox(_stashMessage, MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("戻す", "Restore"),
                        L10n.Tr("編集を始めて、前回の形・ピン・選んだ点を戻します。Ctrl+Z で戻す前の形に戻せます",
                            "Starts editing and restores the previous shape, pins and selection. Ctrl+Z returns to the shape before restoring"))))
                    {
                        RestoreFromStash();
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button(new GUIContent(L10n.Tr("捨てる", "Discard"), L10n.Tr("取っておいた編集を消します", "Deletes the stashed edit"))))
                    {
                        DiscardStash();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        void DrawBurstBand()
        {
            if (!_burstBandDecided)
            {
                // 初めて開いたときに1回だけ決める。Burst の登録が済んでいない読み込み中に決めないよう、描画のときに決める
                _burstBandDecided = true;
                _burstBandVisible = ShouldShowBurstGuide(BurstKernelExists(), EditorPrefs.GetBool(BurstInstaller.GuideShownPrefKey, false));
                if (_burstBandVisible) EditorPrefs.SetBool(BurstInstaller.GuideShownPrefKey, true);
            }
            if (!_burstBandVisible) return;
            if (BurstKernelExists())
            {
                _burstBandVisible = false;   // その間に入った
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L10n.Tr("Burst を入れると速く、きれいに仕上がります", "Install Burst for faster, cleaner results"), EditorStyles.boldLabel);
                EditorGUILayout.LabelField(L10n.Tr("重ね着や大きな衣装でも、引いたあとの形がきれいに落ち着きます（無くてもすべての機能は動きます）。「くわしく」の詳細設定からも、いつでも入れられます。",
                    "Layered or large outfits settle cleanly after pulling (all features work without it). You can also install it any time from Advanced in the Detailed view."), EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(BurstInstaller.IsInstalling))
                    {
                        if (GUILayout.Button(new GUIContent(BurstInstaller.IsInstalling ? L10n.Tr("入れています…", "Installing...") : L10n.Tr("入れる", "Install"),
                            L10n.Tr($"Package Manager で {BurstInstaller.PackageId} を追加します。入ったあと、スクリプトの再読み込みが終わると有効になります",
                                $"Adds {BurstInstaller.PackageId} with the Package Manager. It takes effect after scripts reload"))))
                        {
                            BurstInstaller.Install();
                            _burstBandVisible = false;
                        }
                    }
                    if (GUILayout.Button(new GUIContent(L10n.Tr("今はしない", "Not now"), L10n.Tr("この案内を閉じます。もう出しません", "Closes this notice. It will not appear again"))))
                    {
                        _burstBandVisible = false;
                    }
                }
            }
        }

        static bool BurstKernelExists() => ClothSolver.DefaultKernel != null;

        // ---- 終える・切り替える前の確認 ----

        /// <summary>
        /// 編集を終える・別の衣装へ切り替える前に、保存していない編集があれば確認する（保存する／保存しないで終わる／やめる）。
        /// 続けてよければ true。「保存する」を選んで保存に失敗したときは false（編集は残る）。
        /// </summary>
        internal bool ConfirmLeaveEditing(ClothEditLeaveReason reason)
        {
            if (_controller == null || !_controller.IsEditing || !_controller.NeedsLeaveConfirmation) return true;
            switch (ClothEditPrompts.AskUnsaved(reason))
            {
                case ClothEditUnsavedChoice.Save:
                    return SaveBeforeLeaving();
                case ClothEditUnsavedChoice.Discard:
                    return true;
                default:
                    return false;
            }
        }

        // 一覧の「布で編集する」で別の衣装を始めるときの確認（ShapeKeyClothEdit.ConfirmLeave から呼ばれる）
        bool ConfirmLeaveForShapeKeyEdit(ClothEditLeaveReason reason)
        {
            return ConfirmLeaveEditing(reason);
        }

        bool SaveBeforeLeaving()
        {
            GP02ShapeKeys entryComponent = ShapeKeyClothEdit.ActiveComponent;
            if (entryComponent != null && _controller.Target != null && entryComponent.gameObject == _controller.Target.gameObject) return SaveEntry(true);
            return Save();
        }

        /// <summary>「編集を終了」。保存していない編集があれば確認してから終える。終えたら、取っておきも消す（保存したか、捨てると決めたため）。</summary>
        internal bool RequestEnd()
        {
            if (!_controller.IsEditing) return true;
            if (!ConfirmLeaveEditing(ClothEditLeaveReason.End)) return false;
            ClothEditStash.DeleteFor(_controller.Session != null ? _controller.Session.Renderers : CollectTargets());
            _stashedSession = null;
            _controller.End();
            ShapeKeyClothEdit.Clear();
            ClearResult();
            SyncToolHandles();   // Unity の道具のハンドルを、元に戻す
            _candidatesDirty = true;
            RefreshStashInfo(true);
            Repaint();
            return true;
        }

        // ---- 対象の自動指定・右クリックから始める ----

        // 編集していないときに、ヒエラルキーで衣装を選んだら、それを対象にする
        void OnHierarchySelectionChanged()
        {
            _candidatesDirty = true;
            _pickHint = null;
            Repaint();
            if (_controller == null || _controller.IsEditing) return;
            TrySetTargetsFromSelection();
        }

        /// <summary>今の選択に編集できる布があれば、それを対象にする（編集していないときだけ）。対象が変わったら true。</summary>
        internal bool TrySetTargetsFromSelection()
        {
            if (_controller == null || _controller.IsEditing) return false;
            List<Renderer> cloth = ClothEditStartMenu.FindCloth(Selection.gameObjects);
            if (cloth.Count == 0 || SameList(CollectTargets(), cloth)) return false;
            SetTargets(cloth);
            return true;
        }

        internal void SetTargets(List<Renderer> renderers)
        {
            _target = renderers.Count > 0 ? renderers[0] : null;
            _extraTargets.Clear();
            for (int i = 1; i < renderers.Count; i++) _extraTargets.Add(renderers[i]);
            _warningsDirty = true;
            RefreshStashInfo(true);
            Repaint();
        }

        // ウィンドウを開いたとき: 対象が決まっていなければ、選んでいる衣装、無ければ前回の編集の衣装を対象にする
        void InitTargetOnEnable()
        {
            if (_controller.IsEditing || _target != null) return;
            if (TrySetTargetsFromSelection()) return;
            if (ClothEditStash.TryResolveLatestTargets(out List<Renderer> targets, out _)) SetTargets(targets);
        }

        /// <summary>ヒエラルキーの右クリック「GP02 で布を編集」。選んだ衣装で、すぐ編集を始める（複数なら重ね着としてまとめて）。</summary>
        internal static void StartFromSelection(GameObject[] selection)
        {
            List<Renderer> cloth = ClothEditStartMenu.FindCloth(selection);
            if (cloth.Count == 0 && ClothEditStartMenu.FindCandidates(selection).Count == 0) return;
            var window = GetWindow<ClothEditWindow>(L10n.Tr("GP02 布の編集", "GP02 Cloth Edit"));
            window.minSize = new Vector2(300f, 360f);
            if (cloth.Count > 0)
            {
                window.BeginFromRenderers(cloth);
                return;
            }
            // アバター全体のように、衣装そのものではないものを選んだ: 編集は始めず、窓に「この中の衣装」の一覧を出す
            if (!window._controller.IsEditing)
            {
                window._candidatesDirty = true;
                window._pickHint = L10n.Tr("編集する衣装を、下の「この中の衣装」から選んでください。", "Pick the outfit to edit from \"Outfits in this object\" below.");
                window._foldTarget = true;
                window.Repaint();
            }
        }

        /// <summary>
        /// 渡した衣装で編集を始める。すでに同じ衣装を編集中なら何もしない。別の衣装を編集中で、保存していない編集があれば確認する。
        /// 前回の編集が残っているときは、始めずに帯を出して選んでもらう（黙って上書きしないため）。始めたら true。
        /// </summary>
        internal bool BeginFromRenderers(List<Renderer> renderers)
        {
            if (renderers == null || renderers.Count == 0) return false;
            if (_controller.IsEditing)
            {
                if (SameList(CollectTargets(), renderers)) return true;
                if (!ConfirmLeaveEditing(ClothEditLeaveReason.SwitchTarget)) return false;
                ClothEditStash.DeleteFor(_controller.Session != null ? _controller.Session.Renderers : CollectTargets());
                _controller.End();
                ShapeKeyClothEdit.Clear();
            }
            SetTargets(renderers);
            // 前回の編集が残っているときは、黙って上書きせず、帯で選んでもらう（ウィンドウは StartFromSelection が前に出している）
            if (_stashInfo != null) return false;
            return StartEdit();
        }
    }
}
