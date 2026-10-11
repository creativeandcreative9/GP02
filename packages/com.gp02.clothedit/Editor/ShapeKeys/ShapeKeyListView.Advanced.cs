using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 差し引き、選んだ点の範囲（マスク）で分ける・取り出す・消す、別のメッシュへ写す
    public sealed partial class ShapeKeyListView
    {
        UnityEngine.Object _transferTarget;

        void DrawAdvancedOperations(GP02ShapeKeyData data, List<ShapeKeyEntry> selected)
        {
            if (!_settings.ShowOperations) return;

            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(selected.Count != 2))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("差し引き（上 − 下）", "Subtract (upper − lower)"),
                        L10n.Tr("選んだ 2 つのシェイプキーを、重み 100 での形で引き算した新しいシェイプキーを作ります。一覧で上にあるものから下にあるものを引きます",
                            "Creates a new shape key by subtracting the lower one from the upper one, both at weight 100 (select exactly two)")))) SubtractSelected();
                    _settings.SubtractSwap = GUILayout.Toggle(_settings.SubtractSwap, new GUIContent(L10n.Tr("逆（下 − 上）", "Reverse"), L10n.Tr("引く向きを入れ替えます", "Swap the direction")), GUILayout.Width(80f));
                }
            }

            _settings.ShowRangeTools = EditorGUILayout.Foldout(_settings.ShowRangeTools, L10n.Tr("選んだ点の範囲で分ける・取り出す・消す", "Split / extract / remove by selected points"), true);
            if (_settings.ShowRangeTools)
            {
                EditorGUI.indentLevel++;
                ClothEditController controller = ShapeKeyClothEdit.ControllerProvider?.Invoke();
                bool editing = controller != null && controller.IsEditing && _renderer != null && controller.Target == _renderer;
                string status = editing
                    ? L10n.Tr($"布の編集で選んでいる点: {controller.SelectionCount} 個", $"Points selected in the cloth edit: {controller.SelectionCount}")
                    : L10n.Tr("布の編集でこのメッシュを編集中にして、点を選んでください（ブラシの「選ぶ」道具）。", "Start editing this mesh in the cloth edit and select points (the Select tool).");
                EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
                _settings.MaskSmoothIterations = EditorGUILayout.IntSlider(new GUIContent(L10n.Tr("境目のなめらかさ（回数）", "Border smoothing"),
                    L10n.Tr("範囲の境目の頂点を、隣の頂点となじませる回数です。大きいほど、境目がなだらかになります", "How many times the border is blended with neighboring vertices. Larger is smoother")), _settings.MaskSmoothIterations, 0, 10);
                _settings.MaskAsCopy = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("取り出す・消すは別のシェイプキーにする", "Extract/Remove into a new shape key"),
                    L10n.Tr("オフなら、選んだシェイプキーを書き換えます（前の形は履歴とバックアップに残ります）", "If off, the selected shape key is overwritten (the old shape stays in the history and a backup)")), _settings.MaskAsCopy);
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(new GUIContent(L10n.Tr("選んだ点で分割", "Split by selection"),
                            L10n.Tr("範囲の中（_Sel）と残り（_Rest）の 2 つに分けます。足すと元に戻ります", "Splits into the range (_Sel) and the rest (_Rest). Adding them gives the original")))) SplitBySelection();
                        if (GUILayout.Button(L10n.Tr("範囲だけ取り出す", "Extract range"))) ApplyRange(ShapeKeyMaskOperation.Extract);
                        if (GUILayout.Button(L10n.Tr("範囲を消す", "Remove range"))) ApplyRange(ShapeKeyMaskOperation.Remove);
                    }
                }
                EditorGUI.indentLevel--;
            }

            _settings.ShowTransfer = EditorGUILayout.Foldout(_settings.ShowTransfer, L10n.Tr("別のメッシュへ写す（頂点数が違ってもよい）", "Transfer to another mesh (vertex counts may differ)"), true);
            if (_settings.ShowTransfer) DrawTransfer(data, selected);
            if (EditorGUI.EndChangeCheck()) _settings.Save();
        }

        void DrawTransfer(GP02ShapeKeyData data, List<ShapeKeyEntry> selected)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(L10n.Tr(
                "写し先のメッシュの頂点ごとに、元のメッシュの元の形の表面で一番近い点を探し、その三角形の頂点の差分を混ぜて写します。2 つのメッシュは同じ座標系（同じ向き・大きさ・位置）にしてください。",
                "For each vertex of the destination mesh, the closest point on the surface of the source mesh's original shape is found, and the deltas of that triangle's vertices are blended in. Both meshes must share the same coordinate system (orientation, scale and position)."), MessageType.None);
            _transferTarget = EditorGUILayout.ObjectField(new GUIContent(L10n.Tr("写し先", "Destination"),
                L10n.Tr("写し先の Mesh、GP02 Shape Keys コンポーネントのあるオブジェクト、またはシェイプキーのデータを指定します", "A Mesh, an object with a GP02 Shape Keys component, or a shape key data asset")),
                _transferTarget, typeof(UnityEngine.Object), true);
            _settings.TransferMaxDistanceCm = EditorGUILayout.FloatField(new GUIContent(L10n.Tr("距離の上限（cm）", "Max distance (cm)"),
                L10n.Tr("これより遠い頂点は写さず、差分 0 にします", "Vertices farther than this get zero delta")), _settings.TransferMaxDistanceCm);
            _settings.TransferAvoidBackFaces = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("法線が逆向きの面を避ける", "Avoid faces with opposite normals"),
                L10n.Tr("薄い服の裏側など、向きが逆の面に写してしまわないようにします", "Avoids copying from faces facing the other way, such as the back side of thin clothes")), _settings.TransferAvoidBackFaces);
            using (new EditorGUI.DisabledScope(selected.Count == 0 || _transferTarget == null))
            {
                if (GUILayout.Button(L10n.Tr("選んだシェイプキーを写す", "Transfer the selected shape keys"))) TransferSelected(data, selected);
            }
            EditorGUI.indentLevel--;
        }

        // 写し先の指定から、写し先のデータを探す（Mesh なら、そのメッシュのデータを探して、無ければ作る）
        static GP02ShapeKeyData ResolveTransferTarget(UnityEngine.Object target)
        {
            if (target is GP02ShapeKeyData data) return data;
            if (target is GP02ShapeKeys shapeKeys) return shapeKeys.Data;
            if (target is GameObject go)
            {
                var component = go.GetComponent<GP02ShapeKeys>();
                if (component != null && component.Data != null) return component.Data;
                return FindOrCreateDataForMesh(ClothEditController.GetMesh(ClothEditController.FindRenderer(go)));
            }
            if (target is Mesh mesh) return FindOrCreateDataForMesh(mesh);
            return null;
        }

        static GP02ShapeKeyData FindOrCreateDataForMesh(Mesh mesh)
        {
            if (mesh == null) return null;
            foreach (string guid in AssetDatabase.FindAssets("t:GP02ShapeKeyData", new[] { "Assets" }))
            {
                var existing = AssetDatabase.LoadAssetAtPath<GP02ShapeKeyData>(AssetDatabase.GUIDToAssetPath(guid));
                if (existing != null && existing.SourceMesh == mesh && existing.VertexCount == mesh.vertexCount
                    && !AssetDatabase.GetAssetPath(existing).StartsWith(ShapeKeyBackup.Folder)) return existing;
            }
            return ShapeKeyLibrary.CreateData(mesh);
        }

        void TransferSelected(GP02ShapeKeyData data, List<ShapeKeyEntry> selected)
        {
            Run(() =>
            {
                GP02ShapeKeyData target = ResolveTransferTarget(_transferTarget);
                if (target == null) throw new InvalidOperationException(L10n.Tr("写し先のデータが見つかりません。Mesh かシェイプキーのデータを指定してください。", "The destination data was not found. Specify a Mesh or shape key data."));
                ShapeKeyTransferResult result = ShapeKeyListOperations.TransferEntries(data, selected, target, _settings.TransferMaxDistance, _settings.TransferAvoidBackFaces ? 0f : -1f);
                float percent = result.VertexCount > 0 ? 100f * result.MissCount / result.VertexCount : 0f;
                ShapeKeyLibrary.SaveData(target);
                Show(L10n.Tr(
                    $"{result.Entries.Count} 個を「{target.name}」へ写しました。距離の上限を超えて差分が 0 になった頂点: {result.MissCount} 個（{percent:0.#}%）。",
                    $"Transferred {result.Entries.Count} shape key(s) to \"{target.name}\". Vertices beyond the max distance (zero delta): {result.MissCount} ({percent:0.#}%)."),
                    result.MissCount > 0 ? MessageType.Warning : MessageType.Info);
            });
        }

        // ------------------------------------------------------------------
        // 操作
        // ------------------------------------------------------------------

        void SubtractSelected()
        {
            Run(() =>
            {
                List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
                if (selected.Count != 2) throw new ArgumentException(L10n.Tr("引くシェイプキーを 2 つ選んでください。", "Select exactly two shape keys."));
                ShapeKeyEntry a = _settings.SubtractSwap ? selected[1] : selected[0];
                ShapeKeyEntry b = _settings.SubtractSwap ? selected[0] : selected[1];
                ShapeKeyEntry result = ShapeKeyListOperations.Subtract(Data, a, b, null, _settings.DisableSources);
                AfterStructuralChange(new[] { result });
                Show(L10n.Tr($"「{result.Name}」を作りました。", $"Created \"{result.Name}\"."), MessageType.Info);
            });
        }

        // 布の編集で選んでいる点のマスク。取れなければ理由を出して null
        float[] GetMaskOrExplain()
        {
            ClothEditController controller = ShapeKeyClothEdit.ControllerProvider?.Invoke();
            float[] mask = _renderer != null && controller != null ? controller.GetSelectionVertexMask(_renderer) : null;
            if (mask == null)
            {
                Show(L10n.Tr(
                    "このメッシュを布の編集で編集中にして、点を選んでください（「布で編集する」→ 選ぶ道具）。",
                    "Start editing this mesh in the cloth edit and select points (\"Edit with cloth\", then the Select tool)."), MessageType.Warning);
                return null;
            }
            bool any = false;
            foreach (float m in mask)
            {
                if (m > 0f)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                Show(L10n.Tr("点が 1 つも選ばれていません。布の編集で点を選んでください。", "No points are selected. Select points in the cloth edit."), MessageType.Warning);
                return null;
            }
            return mask;
        }

        internal void SplitBySelection()
        {
            float[] mask = GetMaskOrExplain();
            if (mask == null) return;
            Run(() =>
            {
                var added = new List<ShapeKeyEntry>();
                foreach (ShapeKeyEntry entry in _tree.GetSelectedEntries())
                {
                    ShapeKeyListOperations.SplitByMask(Data, entry, mask, _settings.MaskSmoothIterations, "_Sel", "_Rest", _settings.DisableSources, out ShapeKeyEntry inside, out ShapeKeyEntry outside);
                    added.Add(inside);
                    added.Add(outside);
                }
                AfterStructuralChange(added);
            });
        }

        internal void ApplyRange(ShapeKeyMaskOperation operation)
        {
            float[] mask = GetMaskOrExplain();
            if (mask == null) return;
            Run(() =>
            {
                var results = new List<ShapeKeyEntry>();
                foreach (ShapeKeyEntry entry in _tree.GetSelectedEntries())
                {
                    results.Add(ShapeKeyListOperations.ApplyMask(Data, entry, mask, _settings.MaskSmoothIterations, operation, _settings.MaskAsCopy));
                }
                AfterStructuralChange(results);
            });
        }

        // 「アバターのローカル軸」で左右に分けるための平面を、メッシュ空間に換算する
        bool TryGetAvatarPlane(out Vector3 normal, out float offset)
        {
            normal = Vector3.right;
            offset = 0f;
            if (_renderer == null) return false;
            return ShapeKeyListOperations.TryGetMeshPlane(_renderer.transform, ShapeKeyExporter.FindAvatarRoot(_renderer.transform),
                _settings.Axis, _settings.Center, out normal, out offset);
        }
    }
}
