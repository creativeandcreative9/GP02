using GP02.Core;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// Burst（任意の高速化）が無いときの案内と、パッケージの追加。
    /// BOOTH の .unitypackage では依存パッケージが自動で入らないので、画面から1回で入れられるようにする。
    /// Burst が無くても全機能は動くが、重ね着や大きな衣装では仕上げが時間切れになり、伸びが残りやすい。
    /// </summary>
    public static class BurstInstaller
    {
        /// <summary>開発用のプロジェクト（unity/Packages/manifest.json）と同じ版。</summary>
        public const string PackageId = "com.unity.burst@1.8.18";

        static AddRequest _request;

        /// <summary>高速版のカーネルが登録されているか（Burst が入っていて、コンパイルが有効）。</summary>
        public static bool IsAccelerated => ClothSolver.DefaultKernel != null && ClothSolver.DefaultKernel.IsAccelerated;

        public static bool IsInstalling => _request != null && !_request.IsCompleted;

        public static void Install()
        {
            if (IsInstalling) return;
            _request = Client.Add(PackageId);
            EditorApplication.update += Watch;
        }

        static void Watch()
        {
            if (_request == null || !_request.IsCompleted) return;
            EditorApplication.update -= Watch;
            if (_request.Status == StatusCode.Failure)
            {
                string reason = _request.Error != null ? _request.Error.message : "";
                Debug.LogWarning("[GP02] " + L10n.Tr($"Burst を入れられませんでした。Package Manager から com.unity.burst を追加してください。{reason}",
                    $"Could not add Burst. Add com.unity.burst from the Package Manager. {reason}"));
            }
            _request = null;
        }

        /// <summary>Burst が無いときだけ、案内とボタンを出す。</summary>
        public static void DrawNotice()
        {
            if (IsAccelerated) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L10n.Tr("Burst を入れると、計算が3〜5倍速くなり、重ね着や大きな衣装でも形がきれいに落ち着きます（無くてもすべての機能は動きます）。",
                    "Installing Burst makes the calculation 3-5x faster, so layered or large clothing settles cleanly (all features work without it)."), EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(IsInstalling))
                {
                    string label = IsInstalling ? L10n.Tr("Burst を入れています…", "Installing Burst...") : L10n.Tr("Burst を入れる", "Install Burst");
                    if (GUILayout.Button(new GUIContent(label, L10n.Tr($"Package Manager で {PackageId} を追加します。入ったあと、スクリプトの再読み込みが終わると有効になります",
                        $"Adds {PackageId} with the Package Manager. It takes effect after scripts reload")))) Install();
                }
            }
        }
    }
}
