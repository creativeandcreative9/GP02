using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 「保存」の区切りの、保存したシェイプキーの簡単な一覧。名前・重み・「布で再編集」・削除と、「詳しい一覧を開く」。
    // 保存したものを確かめるために、シェイプキーの一覧のウィンドウへ行き来しなくてよいようにする。詳しい操作は一覧のウィンドウで行う。
    // 重みは、その場で確かめるためのもの（データの「プレビューの重み」は触らない）。動かすと、編集中の形に橙の半透明で、保存した形を重ねて見せる。
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

        // 1行: 名前、重み、布で再編集、削除。データを変えて、この描画を打ち切る必要があるとき true
        bool DrawSavedKeyRow(GP02ShapeKeyData data, GP02ShapeKeys component, ShapeKeyEntry entry, int firstVertexCount)
        {
            bool isActive = ShapeKeyClothEdit.ActiveComponent == component && ShapeKeyClothEdit.ActiveEntryName == entry.Name;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(entry.Name, entry.Name + "\n" + L10n.Tr("重みを動かすと、保存した形を橙の半透明で重ねて見せます（確かめるためだけで、データの重みは変わりません）",
                    "Moving the weight overlays the saved shape in translucent orange (just to check it; the weight in the data is unchanged)")),
                    isActive ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.MinWidth(48f));
                GUILayout.FlexibleSpace();

                _keyWeights.TryGetValue(entry, out float weight);
                bool canPreview = data.SourceMesh != null && data.SourceMesh.vertexCount == firstVertexCount;
                using (new EditorGUI.DisabledScope(!canPreview || entry.Frames.Count == 0))
                {
                    EditorGUI.BeginChangeCheck();
                    float newWeight = EditorGUILayout.Slider(weight, 0f, 100f, GUILayout.Width(120f));
                    if (EditorGUI.EndChangeCheck()) SetKeyWeight(data, entry, newWeight);
                }

                if (GUILayout.Button(new GUIContent(L10n.Tr("布で再編集", "Re-edit"),
                    L10n.Tr("この形を読み込んで、続きを布として編集します（直したら、同じ名前で上書き保存できます）", "Loads this shape so you can keep editing it as cloth (then overwrite it under the same name)")), EditorStyles.miniButton, GUILayout.Width(66f)))
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
            if (ShapeKeyClothEdit.Begin(_controller, component, entry, out string message)) Toast(message);
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
