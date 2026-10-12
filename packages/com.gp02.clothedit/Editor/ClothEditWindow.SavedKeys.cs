using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 「保存」の区切りの、保存したシェイプキーの簡単な一覧。名前・「布で再編集」・削除と、「詳しい一覧を開く」。
    // 保存したものを確かめるために、シェイプキーの一覧のウィンドウへ行き来しなくてよいようにする。詳しい操作は一覧のウィンドウで行う。
    // かんたん表示は、レンダラーの実際の重みを「反映中（100）」「反映していない（0）」の言葉で出す（インスペクターの重みと同じもの）。
    // くわしく表示では、「重ねて見る」スライダーも出す。これは、編集中の形に、保存した形を橙の半透明で重ねて見るためのもので、
    // レンダラーの重みとも、データの「プレビューの重み」とも別（どちらも触らない）。
    public sealed partial class ClothEditWindow
    {
        const int MaxSavedKeyRows = 6;

        readonly Dictionary<ShapeKeyEntry, float> _keyWeights = new Dictionary<ShapeKeyEntry, float>();
        ClothEditSession _keyWeightsSession;       // 重みを持っているセッション（作り直されたら、重みも捨てる）
        GP02ShapeKeyData _keySourceData;
        Vector3[] _keySourceVertices;              // データの元のメッシュの頂点（重ね描きの計算用）

        /// <summary>編集中の衣装（最初の対象）の、保存したシェイプキーのデータ。無ければ null。</summary>
        internal GP02ShapeKeyData FindSavedKeyData(out GP02ShapeKeys component)
        {
            component = null;
            Renderer target = _controller != null ? _controller.Target : null;
            if (target == null) return null;
            component = target.GetComponent<GP02ShapeKeys>();
            return component != null ? component.Data : null;
        }

        void DrawSavedKeys()
        {
            if (!Shows(ClothEditUiItem.SavedKeyList)) return;
            GP02ShapeKeyData data = FindSavedKeyData(out GP02ShapeKeys component);
            ResetKeyWeightsIfSessionChanged();
            if (data == null || data.Entries.Count == 0)
            {
                EditorGUILayout.LabelField(L10n.Tr("保存すると、ここに一覧が出ます", "Saved shape keys will be listed here"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(L10n.Tr($"保存したシェイプキー（{data.Entries.Count}）", $"Saved shape keys ({data.Entries.Count})"), EditorStyles.boldLabel);
            int shown = 0;
            _controller.Session.GetVertexRange(0, out _, out int firstVertexCount);
            for (int i = 0; i < data.Entries.Count && shown < MaxSavedKeyRows; i++)
            {
                ShapeKeyEntry entry = data.Entries[i];
                if (entry == null) continue;
                shown++;
                if (DrawSavedKeyRow(data, component, entry, firstVertexCount)) GUIUtility.ExitGUI();
            }
            if (data.Entries.Count > shown)
            {
                EditorGUILayout.LabelField(L10n.Tr($"ほか {data.Entries.Count - shown} 個は、詳しい一覧で見られます", $"{data.Entries.Count - shown} more in the detailed list"), EditorStyles.miniLabel);
            }
            if (GUILayout.Button(new GUIContent(L10n.Tr("詳しい一覧を開く", "Open the detailed list"),
                L10n.Tr("シェイプキーの一覧のウィンドウを開きます（分割・反転・書き出しなど、すべての操作ができます）", "Opens the shape key list window (split, flip, export and every other operation)"))))
            {
                OpenShapeKeyList();
            }
        }

        // 保存したシェイプキー1つの、レンダラー（編集中の衣装）での実際の重み。メッシュに入っていなければ false。
        // メッシュの BlendShape の名前は、書き出しの前置き・後置きを付けたもの
        internal bool TryGetAppliedWeight(GP02ShapeKeys component, ShapeKeyEntry entry, out float weight)
        {
            weight = 0f;
            Renderer target = _controller != null ? _controller.Target : null;
            if (!(target is SkinnedMeshRenderer skinned) || skinned.sharedMesh == null || entry == null) return false;
            int index = skinned.sharedMesh.GetBlendShapeIndex(ShapeKeyBuildOptions.FromComponent(component).Decorate(entry.Name));
            if (index < 0) return false;
            weight = skinned.GetBlendShapeWeight(index);
            return true;
        }

        /// <summary>保存したシェイプキーが、衣装に反映されているかの言葉。「反映中（100）」「反映していない（0）」「反映していない（メッシュに未書き出し）」。</summary>
        internal static string AppliedWeightLabel(bool inMesh, float weight)
        {
            if (!inMesh) return L10n.Tr("反映していない（未書き出し）", "Not applied (not exported)");
            return weight > 0f
                ? L10n.Tr($"反映中（{weight:0.#}）", $"Applied ({weight:0.#})")
                : L10n.Tr("反映していない（0）", "Not applied (0)");
        }

        // 1行: 名前、反映のようす、布で再編集、削除。くわしく表示は、もう1行に「重ねて見る」スライダー。データを変えて、この描画を打ち切る必要があるとき true
        bool DrawSavedKeyRow(GP02ShapeKeyData data, GP02ShapeKeys component, ShapeKeyEntry entry, int firstVertexCount)
        {
            bool isActive = ShapeKeyClothEdit.ActiveComponent == component && ShapeKeyClothEdit.ActiveEntryName == entry.Name;
            bool overlay = Shows(ClothEditUiItem.SavedKeyOverlay);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(entry.Name, entry.Name), isActive ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.MinWidth(48f));
                GUILayout.FlexibleSpace();

                bool inMesh = TryGetAppliedWeight(component, entry, out float applied);
                bool on = inMesh && applied > 0f;
                Color previous = GUI.contentColor;
                if (on) GUI.contentColor = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.9f, 0.5f, 1f) : new Color(0.1f, 0.5f, 0.15f, 1f);
                GUILayout.Label(new GUIContent(AppliedWeightLabel(inMesh, applied),
                    L10n.Tr("衣装に入っているシェイプキーの、実際の重みです。インスペクターの「Blend Shapes」で 0〜100 に変えられます（100 で保存した形、0 で元の形）",
                        "The actual weight of this shape key on the outfit. Change it in the Inspector's Blend Shapes, 0 to 100 (100 is the saved shape, 0 the original)")),
                    EditorStyles.miniLabel);
                GUI.contentColor = previous;

                if (GUILayout.Button(new GUIContent(L10n.Tr("布で再編集", "Re-edit"),
                    L10n.Tr("この形を読み込んで、続きを布として編集します（直したら、同じ名前で上書き保存できます）", "Loads this shape so you can keep editing it as cloth (then overwrite it under the same name)")), EditorStyles.miniButton, GUILayout.Width(78f)))
                {
                    ReEditSavedKey(component, entry);
                    return true;
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("削除", "Delete"), L10n.Tr("この保存データを一覧から消します（バックアップが自動で残ります）", "Removes this saved data from the list (a backup is kept automatically)")),
                    EditorStyles.miniButton, GUILayout.Width(52f)))
                {
                    return DeleteSavedKey(data, entry);
                }
            }

            if (!overlay) return false;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14f);
                _keyWeights.TryGetValue(entry, out float weight);
                bool canPreview = data.SourceMesh != null && data.SourceMesh.vertexCount == firstVertexCount;
                using (new EditorGUI.DisabledScope(!canPreview || entry.Frames.Count == 0))
                {
                    EditorGUI.BeginChangeCheck();
                    float newWeight = EditorGUILayout.Slider(new GUIContent(L10n.Tr("重ねて見る", "Overlay"),
                        L10n.Tr("動かすと、編集中の形に、保存した形を橙の半透明で重ねて見せます。確かめるためだけで、衣装の重みもデータの重みも変わりません",
                            "Moving it overlays the saved shape on the edited shape in translucent orange. Only for checking; neither the outfit's weight nor the weight in the data changes")),
                        weight, 0f, 100f);
                    if (EditorGUI.EndChangeCheck()) SetKeyWeight(data, entry, newWeight);
                }
            }
            return false;
        }

        void ResetKeyWeightsIfSessionChanged()
        {
            if (ReferenceEquals(_keyWeightsSession, _controller.Session)) return;
            _keyWeightsSession = _controller.Session;
            _keyWeights.Clear();
        }

        // 重みを動かした。重みが付いているシェイプキーを足し合わせて、編集中の形に重ね描きする（すべて 0 なら消す）
        internal void SetKeyWeight(GP02ShapeKeyData data, ShapeKeyEntry entry, float weight)
        {
            ClothEditSession session = _controller.Session;
            if (session == null || data == null || entry == null) return;
            _keyWeightsSession = session;
            weight = Mathf.Clamp(weight, 0f, 100f);
            if (weight <= 0f) _keyWeights.Remove(entry);
            else _keyWeights[entry] = weight;
            UpdateKeyGhost(data, session);
            SceneView.RepaintAll();
        }

        void UpdateKeyGhost(GP02ShapeKeyData data, ClothEditSession session)
        {
            if (_keyWeights.Count == 0 || data.SourceMesh == null)
            {
                session.ClearKeyGhost();
                return;
            }
            int count = data.SourceMesh.vertexCount;
            if (!ReferenceEquals(_keySourceData, data) || _keySourceVertices == null || _keySourceVertices.Length != count)
            {
                _keySourceData = data;
                _keySourceVertices = data.SourceMesh.vertices;
            }
            var total = new Vector3[count];
            foreach (KeyValuePair<ShapeKeyEntry, float> pair in _keyWeights)
            {
                if (pair.Key.Frames.Count == 0 || !ShapeKeyLibrary.IsEntryCompatible(pair.Key, count)) continue;
                Vector3[] delta = ShapeKeyLibrary.EvaluateEntry(pair.Key, pair.Value, count).DeltaVertices;
                for (int v = 0; v < count; v++) total[v] += delta[v];
            }
            session.SetKeyGhost(_keySourceVertices, total);
        }

        void ReEditSavedKey(GP02ShapeKeys component, ShapeKeyEntry entry)
        {
            if (ShapeKeyClothEdit.Begin(_controller, component, entry, out string message)) Report(L10n.Tr("読み込みました", "Loaded"), message);
            else EditorUtility.DisplayDialog("GP02", message, "OK");
        }

        // 削除の確認をして、データから消す。消したら true
        internal bool DeleteSavedKey(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            string name = entry.Name;
            if (!ClothEditPrompts.Confirm("GP02",
                L10n.Tr($"保存データ「{name}」を一覧から削除しますか？\n（バックアップが自動で残ります。メッシュに書き出し済みのシェイプキーは、そのまま残ります）",
                    $"Remove the saved data \"{name}\" from the list?\n(A backup is kept automatically. A shape key already exported to the mesh stays in the mesh.)"),
                L10n.Tr("削除する", "Delete"), L10n.Tr("やめる", "Cancel")))
            {
                return false;
            }
            ShapeKeyListOperations.Delete(data, new[] { entry });
            _keyWeights.Remove(entry);
            if (_controller.Session != null) UpdateKeyGhost(data, _controller.Session);
            if (ShapeKeyClothEdit.ActiveEntryName == name) ShapeKeyClothEdit.Clear();
            return true;
        }

        // 編集中の衣装のシェイプキーの一覧のウィンドウを開く（一覧は、選んでいるオブジェクトのものを出すので、その衣装を選ぶ）
        void OpenShapeKeyList()
        {
            Renderer target = _controller.Target;
            if (target != null) Selection.activeGameObject = target.gameObject;
            EditorApplication.ExecuteMenuItem("Tools/GP02/シェイプキーの一覧");
        }
    }
}
