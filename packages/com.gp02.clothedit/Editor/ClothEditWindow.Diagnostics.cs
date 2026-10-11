using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // ウィンドウの「診断」の区切り: 編集を始める前と編集中に、問題をまとめて点検して一覧（重大・注意・情報）にし、直せるものには「直す」ボタンを出す。
    // 点検の中身は ClothEditWarnings にある
    public sealed partial class ClothEditWindow
    {
        [SerializeField] bool _foldDiagnostics = true;

        readonly List<TargetWarning> _diagnostics = new List<TargetWarning>();
        readonly List<Renderer> _diagnosticOwners = new List<Renderer>();   // _diagnostics と同じ順の対象（編集中の点検は null）
        double _diagnosticsAt = double.NegativeInfinity;
        int _diagnosticsShape = -1;
        int _warningsVersion;          // 対象ごとの点検を作り直すたびに増える
        int _diagnosticsWarningsVersion = -1;
        const double DiagnosticsRefreshSeconds = 1.0;

        // 対象ごとの点検（_warnings）と、編集中の点検をまとめる。対象の点検は DrawTargetSection が Layout で作り直している
        void RefreshDiagnostics()
        {
            double now = EditorApplication.timeSinceStartup;
            int shape = _controller.Session != null ? _controller.Session.ShapeVersion : -1;
            if (now - _diagnosticsAt < DiagnosticsRefreshSeconds && shape == _diagnosticsShape && _diagnosticsWarningsVersion == _warningsVersion) return;
            _diagnosticsAt = now;
            _diagnosticsShape = shape;
            _diagnosticsWarningsVersion = _warningsVersion;

            var editingItems = new List<TargetWarning>();
            ClothEditWarnings.CollectEditing(_controller, editingItems);
            var items = new List<(TargetWarning Item, Renderer Owner)>();
            for (int i = 0; i < _warnings.Count; i++) items.Add((_warnings[i], i < _warningOwners.Count ? _warningOwners[i] : _target));
            foreach (TargetWarning item in editingItems) items.Add((item, null));

            _diagnostics.Clear();
            _diagnosticOwners.Clear();
            foreach (MessageType severity in new[] { MessageType.Error, MessageType.Warning, MessageType.Info, MessageType.None })
            {
                foreach ((TargetWarning item, Renderer owner) in items)
                {
                    if (item.Severity != severity) continue;
                    _diagnostics.Add(item);
                    _diagnosticOwners.Add(owner);
                }
            }
        }

        void DrawDiagnosticsSection()
        {
            if (_target == null && !_controller.IsEditing) return;
            if (Event.current.type == EventType.Layout) RefreshDiagnostics();

            int errors = 0, warnings = 0, infos = 0;
            foreach (TargetWarning item in _diagnostics)
            {
                if (item.Severity == MessageType.Error) errors++;
                else if (item.Severity == MessageType.Warning) warnings++;
                else infos++;
            }
            string summary = _diagnostics.Count == 0
                ? L10n.Tr("診断（問題なし）", "Diagnostics (no problems)")
                : L10n.Tr($"診断（重大 {errors}・注意 {warnings}・情報 {infos}）", $"Diagnostics ({errors} critical, {warnings} warnings, {infos} info)");
            _foldDiagnostics = Section(_foldDiagnostics, summary);
            if (!_foldDiagnostics) return;

            if (_diagnostics.Count == 0)
            {
                EditorGUILayout.HelpBox(L10n.Tr("見つかった問題はありません。", "No problems were found."), MessageType.Info);
            }
            for (int i = 0; i < _diagnostics.Count; i++)
            {
                TargetWarning item = _diagnostics[i];
                Renderer owner = _diagnosticOwners[i];
                string prefix = $"[{item.SeverityLabel}] ";
                if (owner != null && CollectTargets().Count > 1) prefix += owner.name + ": ";
                EditorGUILayout.HelpBox(prefix + item.Message, item.Severity);
                if (!item.CanAutoFix) continue;
                bool blockedWhileEditing = item.Fix == TargetWarningFix.ResetBlendShapeWeights && _controller.IsEditing;
                using (new EditorGUI.DisabledScope(blockedWhileEditing || (item.Fix == TargetWarningFix.InstallBurst && BurstInstaller.IsInstalling)))
                {
                    string label = L10n.Tr("直す: ", "Fix: ") + ClothEditWarnings.FixLabel(item.Fix);
                    string tip = blockedWhileEditing ? L10n.Tr("編集を終えてから直してください（重みを変えると、編集が作り直されます）", "Fix this after ending the edit (changing weights rebuilds the edit)") : "";
                    if (GUILayout.Button(new GUIContent(label, tip)))
                    {
                        ApplyFix(item.Fix, owner);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            DrawPenetrationDiagnostics();
            if (GUILayout.Button(new GUIContent(L10n.Tr("点検し直す", "Check Again"), L10n.Tr("対象と編集の状態を、今の内容で点検し直します", "Checks the target and the edit again with their current state"))))
            {
                _warningsDirty = true;
                _diagnosticsAt = double.NegativeInfinity;
            }
            EditorGUILayout.Space();
        }

        void ApplyFix(TargetWarningFix fix, Renderer owner)
        {
            Renderer target = owner != null ? owner : _target;
            string message = null;
            switch (fix)
            {
                case TargetWarningFix.EnableReadWrite:
                    FixReadWrite(target);
                    return;
                case TargetWarningFix.ResetBlendShapeWeights:
                    ClothEditWarnings.TryResetBlendShapeWeights(target, out message);
                    break;
                case TargetWarningFix.FitMirrorPlane:
                    FitMirrorPlane();
                    break;
                case TargetWarningFix.InstallBurst:
                    BurstInstaller.Install();
                    break;
            }
            _warningsDirty = true;
            _diagnosticsAt = double.NegativeInfinity;
            if (!string.IsNullOrEmpty(message)) ShowNotification(new GUIContent(message));
            SceneView.RepaintAll();
        }
    }
}
