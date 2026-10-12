using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 「適用のしかた」の画面（<see cref="GP02ShapeKeys.ApplyMode"/>）。シェイプキーの一覧（<see cref="ShapeKeyListView"/>）の中に出す。
    /// 変更は Undo で戻せる。
    /// </summary>
    public static class ShapeKeyApplyModeGUI
    {
        public static void Draw(GP02ShapeKeys component, Renderer renderer)
        {
            if (component == null) return;

            EditorGUI.BeginChangeCheck();
            var mode = (ShapeKeyApplyMode)EditorGUILayout.Popup(
                new GUIContent(L10n.Tr("適用のしかた", "Apply mode"),
                    L10n.Tr("メッシュに反映する: シェイプキーを入れたメッシュを、シーンのレンダラーに付けます（布の編集の保存でも入ります）。\nビルド時に適用: シーンは元のメッシュのまま、アバターのビルド時だけ BlendShape を足したメッシュに差し替えます（NDMF が必要）",
                        "Export to mesh: the exported mesh is assigned to the renderer in the scene.\nApply at build: the scene keeps the original mesh, and the avatar build swaps in a mesh with the BlendShapes (needs NDMF)")),
                (int)component.ApplyMode,
                new[]
                {
                    new GUIContent(L10n.Tr("メッシュに反映する", "Apply to mesh")),
                    new GUIContent(L10n.Tr("ビルド時に適用（NDMF）", "Apply at build (NDMF)")),
                });
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(component, "GP02 " + L10n.Tr("適用のしかたの変更", "Change apply mode"));
                component.ApplyMode = mode;
                EditorUtility.SetDirty(component);
            }

            if (component.ApplyMode == ShapeKeyApplyMode.NonDestructive)
            {
                EditorGUI.BeginChangeCheck();
                var weight = (ShapeKeyInitialWeight)EditorGUILayout.Popup(
                    new GUIContent(L10n.Tr("ビルド時の最初の重み", "Initial weight at build"),
                        L10n.Tr("ビルドで入れる BlendShape の最初の重みです", "The initial weight of the BlendShapes added at build")),
                    (int)component.InitialWeight,
                    new[]
                    {
                        new GUIContent(L10n.Tr("すべて 0", "All 0")),
                        new GUIContent(L10n.Tr("プレビューの重み", "Preview weights")),
                    });
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "GP02 " + L10n.Tr("最初の重みの変更", "Change initial weight"));
                    component.InitialWeight = weight;
                    EditorUtility.SetDirty(component);
                }
            }

            DrawNote(component, renderer);
        }

        static void DrawNote(GP02ShapeKeys component, Renderer renderer)
        {
            if (component.ApplyMode == ShapeKeyApplyMode.NonDestructive)
            {
                if (!NonDestructiveSupport.IsAvailable)
                {
                    EditorGUILayout.HelpBox(L10n.Tr(
                        "NDMF が無いので、ビルド時の自動適用は行われません。『メッシュに反映する』を使ってください。",
                        "NDMF is not installed, so the shape keys are not applied automatically at build. Use \"Export to mesh\" instead."), MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(L10n.Tr(
                        "シーンのメッシュは元のまま変えません。アバターのビルド時（NDMF）に、BlendShape を足したメッシュへ差し替え、このコンポーネントを取り除きます。",
                        "The mesh in the scene is left as is. When the avatar is built (NDMF), it is replaced by a mesh with the BlendShapes and this component is removed."), MessageType.Info);
                }

                if (renderer != null && ShapeKeyExporter.GetMeshState(component, renderer) == MeshState.Exported)
                {
                    EditorGUILayout.HelpBox(L10n.Tr(
                        "レンダラーは今、シェイプキーを反映したメッシュを使っています。ビルド時は元のメッシュから作り直すので問題ありませんが、シーンで元の形に戻すなら『元のメッシュに戻す』を押してください。",
                        "The renderer uses the exported mesh now. The build starts from the original mesh, so this is fine, but press \"Restore the original mesh\" to see the original shape in the scene."), MessageType.None);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(L10n.Tr(
                    "VRChat にアップロードするときは、NDMF が入っていればこのコンポーネントはビルド時に自動で取り除かれます。NDMF が無い場合は、メッシュに反映済みであることを確かめてから、このコンポーネントを外してください。",
                    "When uploading to VRChat, this component is removed automatically at build if NDMF is installed. Without NDMF, export to the mesh first and then remove this component."), MessageType.None);
            }
        }
    }
}
