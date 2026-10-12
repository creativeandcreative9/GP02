using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 間違えやすい選び方の助け:
    //  - アバター全体のように、衣装そのものではないオブジェクトを選んだときは、ダイアログにせず、対象の区切りに「この中の衣装」の一覧を出す（押すと対象になる）
    //  - 体（素体・顔）のメッシュらしいものを対象にしたときは、診断に注意と衣装の候補の一覧を出す（編集は止めない）
    public sealed partial class ClothEditWindow
    {
        List<ClothCandidate> _candidates = new List<ClothCandidate>();   // 選んだオブジェクトの中の衣装（編集していないときだけ）
        string _candidatesRoot = "";                                      // 一覧の見出しに出す、選んだオブジェクトの名前
        bool _candidatesDirty = true;
        string _pickHint;                                                  // 「選択中のオブジェクトから設定」で衣装が見つからなかったときの、窓の中の案内

        // 診断の「体のメッシュのようです」に付ける、衣装の候補（診断を作り直すたびに作り直す）
        readonly Dictionary<Renderer, List<Renderer>> _bodyOutfits = new Dictionary<Renderer, List<Renderer>>();
        int _bodyOutfitsVersion = -1;

        /// <summary>窓に出ている「この中の衣装」の一覧（テスト用）。</summary>
        internal IReadOnlyList<ClothCandidate> Candidates
        {
            get
            {
                RefreshCandidates();
                return _candidates;
            }
        }

        /// <summary>「選択中のオブジェクトから設定」で衣装が見つからなかったときの案内（無ければ空。テスト用）。</summary>
        internal string PickHint => _pickHint ?? "";

        void OnHierarchyChange()
        {
            _candidatesDirty = true;
            Repaint();
        }

        // 選んだものの中の衣装の一覧を作り直す（選択か階層が変わったあと）。編集中は出さない
        void RefreshCandidates()
        {
            if (!_candidatesDirty) return;
            _candidatesDirty = false;
            _candidates = _controller != null && _controller.IsEditing ? new List<ClothCandidate>() : ClothEditStartMenu.FindCandidates(Selection.gameObjects);
            _candidatesRoot = "";
            if (_candidates.Count == 0) return;
            var names = new List<string>();
            foreach (GameObject go in Selection.gameObjects)
            {
                if (go != null && go.scene.IsValid() && ClothEditController.FindRenderer(go) == null && go.GetComponentInChildren<Renderer>(true) != null) names.Add(go.name);
            }
            _candidatesRoot = string.Join("、", names);
        }

        // 対象の区切りの中: 選んだオブジェクト（アバターの根元など）の中の衣装を、押して対象にできるボタンの一覧にする
        void DrawCandidates()
        {
            RefreshCandidates();
            if (!string.IsNullOrEmpty(_pickHint)) EditorGUILayout.HelpBox(_pickHint, MessageType.Info);
            if (_candidates.Count == 0) return;

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(L10n.Tr($"この中の衣装（{_candidatesRoot}）", $"Outfits in this object ({_candidatesRoot})"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L10n.Tr("押すと、編集する衣装になります", "Press one to make it the outfit to edit"), EditorStyles.miniLabel);
            foreach (ClothCandidate candidate in _candidates)
            {
                if (candidate.Renderer == null) continue;
                bool current = _target == candidate.Renderer && _extraTargets.Count == 0;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = candidate.Renderer.name + (candidate.IsActive ? "" : L10n.Tr("（非表示）", " (hidden)"));
                    GUIContent content = new GUIContent(label, candidate.LikelyBody
                        ? L10n.Tr("体（素体・顔）のメッシュのようです。服を編集するときは、服のオブジェクトを選んでください", "This looks like the body (or face) mesh. Choose a clothing object to edit clothes")
                        : L10n.Tr("この衣装を、編集する対象にします", "Makes this outfit the one to edit"));
                    using (new EditorGUI.DisabledScope(current))
                    {
                        if (GUILayout.Button(content))
                        {
                            PickCandidate(candidate.Renderer);
                            GUIUtility.ExitGUI();
                        }
                    }
                    if (candidate.LikelyBody) GUILayout.Label(L10n.Tr("体のようです", "looks like the body"), EditorStyles.miniLabel, GUILayout.Width(88f));
                    else if (current) GUILayout.Label(L10n.Tr("対象", "target"), EditorStyles.miniLabel, GUILayout.Width(88f));
                }
            }
            if (ClothEditStartMenu.MaxCandidates <= _candidates.Count)
            {
                EditorGUILayout.LabelField(L10n.Tr("多いので、先頭の分だけ出しています。ほかは、ヒエラルキーで直接選んでください", "Only the first ones are shown. Select the others directly in the Hierarchy"), EditorStyles.wordWrappedMiniLabel);
            }
        }

        // 一覧の衣装を対象にする（選択は変えない。変えると、一覧が消えて、別の衣装に切り替えにくいため）
        internal void PickCandidate(Renderer renderer)
        {
            if (renderer == null || _controller == null || _controller.IsEditing) return;
            SetTargets(new List<Renderer> { renderer });
            _pickHint = null;
            _warningsDirty = true;
        }

        // 診断の「体のメッシュのようです」に付ける、衣装の候補（体らしいもの・体自身は除く）
        List<Renderer> OutfitsFor(Renderer body)
        {
            if (_bodyOutfitsVersion != _warningsVersion)
            {
                _bodyOutfits.Clear();
                _bodyOutfitsVersion = _warningsVersion;
            }
            if (body == null) return new List<Renderer>();
            if (!_bodyOutfits.TryGetValue(body, out List<Renderer> outfits))
            {
                Transform root = ClothEditSymmetry.FindAvatarRoot(body.transform);
                outfits = root != null ? ClothEditStartMenu.FindOutfitsIn(root.gameObject, body) : new List<Renderer>();
                _bodyOutfits[body] = outfits;
            }
            return outfits;
        }

        // 体のメッシュらしいものを対象にしたときの注意。「編集を開始」の上に出す（下の診断に出すと、押したあとで気づくため）。
        // 「この中の衣装」の一覧が出ていれば、候補のボタンはそちらに任せる（同じボタンを2回並べない）
        void DrawLikelyBodyNotice()
        {
            for (int i = 0; i < _warnings.Count; i++)
            {
                if (_warnings[i].Kind != TargetWarningKind.LikelyBody) continue;
                EditorGUILayout.HelpBox(_warnings[i].Message, MessageType.Warning);
                RefreshCandidates();
                if (_candidates.Count > 0)
                {
                    EditorGUILayout.LabelField(L10n.Tr("上の「この中の衣装」から、服を選べます", "You can choose the clothing in \"Outfits in this object\" above"), EditorStyles.miniLabel);
                }
                else
                {
                    DrawOutfitSuggestions(i < _warningOwners.Count ? _warningOwners[i] : _target);
                }
                return;
            }
        }

        // 体の注意の下: 「衣装の候補」のボタン
        void DrawOutfitSuggestions(Renderer body)
        {
            List<Renderer> outfits = OutfitsFor(body);
            if (outfits.Count == 0) return;
            EditorGUILayout.LabelField(L10n.Tr("衣装の候補（押すと、編集する衣装になります）", "Outfit candidates (press one to edit it)"), EditorStyles.miniLabel);
            int shown = 0;
            foreach (Renderer outfit in outfits)
            {
                if (outfit == null) continue;
                if (shown++ >= 8) break;
                if (GUILayout.Button(new GUIContent(outfit.name, L10n.Tr("この衣装を、編集する対象にします", "Makes this outfit the one to edit"))))
                {
                    PickCandidate(outfit);
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
