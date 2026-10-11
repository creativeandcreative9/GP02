using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // メッシュへの反映（データとメッシュが同じかの表示・確認の一覧・名前の前置き後置き・トグル用 AnimationClip・まとめて反映）と、素体への焼き込み。
    // 中身は「メッシュに書き出す」（ShapeKeyExporter.ExportToMesh）だが、画面の言葉は「メッシュに反映」にそろえる
    // （布の編集の「シェイプキーとして保存」でもメッシュに入るので、データとメッシュが違うときだけ、反映を勧める）
    public sealed partial class ShapeKeyListView
    {
        static readonly string[] RemapModeLabels = { "近くの頂点からもらう", "左右を反転したウェイトをもらう" };
        static readonly string[] RemapModeLabelsEn = { "Take from the nearest vertex", "Take the left/right-flipped weights" };

        /// <summary>データとメッシュを比べ直す間隔（秒）。データかメッシュが変わったと分かったときは、すぐに比べ直す。</summary>
        const double MeshStatusInterval = 2.0;

        Vector2 _planScroll;
        Renderer _batchFor;
        Transform _batchRoot;
        List<GP02ShapeKeys> _batchComponents;
        double _batchTime;
        readonly Dictionary<ExportAction, GUIStyle> _planStyles = new Dictionary<ExportAction, GUIStyle>();
        ShapeKeyMeshStatus _meshStatus;
        int _meshStatusKey;
        double _meshStatusTime;
        GUIStyle _syncedStyle, _applyButtonStyle;

        /// <summary>
        /// データと、レンダラーに今付いているメッシュを比べた結果（メッシュに反映済みか）。データかメッシュ・名前の設定が変わったとき、
        /// または <see cref="MeshStatusInterval"/> 秒ごとに比べ直す（毎回の描画では比べない）。
        /// </summary>
        internal ShapeKeyMeshStatus GetMeshStatus()
        {
            ShapeKeyBuildOptions options = ShapeKeyBuildOptions.FromComponent(_component, _settings.OverwriteSameName);
            int key = MeshStatusKey(options);
            double now = EditorApplication.timeSinceStartup;
            if (_meshStatus == null || key != _meshStatusKey || now - _meshStatusTime > MeshStatusInterval)
            {
                _meshStatus = ShapeKeyMeshSync.Compare(_component, _renderer, options);
                _meshStatusKey = key;
                _meshStatusTime = now;
            }
            return _meshStatus;
        }

        // 比べ直すかを決める鍵（データ・メッシュの変更の回数、名前の設定）
        int MeshStatusKey(ShapeKeyBuildOptions options)
        {
            unchecked
            {
                int h = 17;
                GP02ShapeKeyData data = Data;
                h = h * 31 + (_component != null ? _component.GetInstanceID() : 0);
                h = h * 31 + (data != null ? data.GetInstanceID() : 0);
                h = h * 31 + (data != null ? EditorUtility.GetDirtyCount(data) : 0);
                Mesh mesh = _renderer != null ? ShapeKeyPreview.GetOriginalMesh(_renderer) ?? ClothEditController.GetMesh(_renderer) : null;
                h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                h = h * 31 + (mesh != null ? EditorUtility.GetDirtyCount(mesh) : 0);
                h = h * 31 + (mesh != null ? mesh.blendShapeCount : 0);
                h = h * 31 + (options.NamePrefix ?? "").GetHashCode();
                h = h * 31 + (options.NameSuffix ?? "").GetHashCode();
                h = h * 31 + (options.SkipIncompatible ? 1 : 0) + (options.OverwriteSameName ? 2 : 0);
                return h;
            }
        }

        void DrawExport(GP02ShapeKeyData data)
        {
            _settings.ShowExport = EditorGUILayout.Foldout(_settings.ShowExport, L10n.Tr("メッシュへの反映", "Apply to Mesh"), true);
            if (!_settings.ShowExport) return;

            string problem = ShapeKeyExporter.GetProblem(_component);
            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Warning);

            // かんたん表示: 「✓ メッシュに反映済み」か、違うときだけ「メッシュに反映（変更 N 件）」
            if (!ClothEditView.Shows(ClothEditUiItem.ShapeKeyExportDetails))
            {
                DrawMainExport(problem);
                return;
            }

            EditorGUI.BeginChangeCheck();
            _settings.ExportFolder = EditorGUILayout.TextField(L10n.Tr("保存先", "Folder"), _settings.ExportFolder);
            _settings.OverwriteSameName = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("同じ名前を上書きする", "Overwrite same names"),
                L10n.Tr("オフなら、同じ名前があるとき「名前 (1)」のように別の名前で加えます", "If off, a same name gets a number such as \"name (1)\"")), _settings.OverwriteSameName);
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            DrawNaming();
            ShapeKeyBuildOptions options = ShapeKeyBuildOptions.FromComponent(_component, _settings.OverwriteSameName);
            List<ExportPlanItem> plan = problem == null ? ShapeKeyExporter.GetPlan(_component, options) : null;
            DrawPlan(plan);
            bool planFailed = plan != null && ShapeKeyExportPlan.FirstProblem(plan) != null;

            ShapeKeyMeshStatus status = problem == null && !planFailed ? GetMeshStatus() : null;
            if (status != null) DrawMeshStatusLine(status);
            using (new EditorGUI.DisabledScope(problem != null || planFailed))
            {
                string label = status != null && status.Problem == null && status.ChangeCount > 0
                    ? L10n.Tr($"メッシュに反映（変更 {status.ChangeCount} 件）", $"Apply to Mesh ({status.ChangeCount} change(s))")
                    : L10n.Tr("メッシュに反映", "Apply to Mesh");
                if (GUILayout.Button(new GUIContent(label,
                    L10n.Tr("上の一覧のとおりに、有効なシェイプキーを、衣装のメッシュ（元のメッシュの複製）に BlendShape として入れて、レンダラーに付けます。データは残ります。反映済みでも、作り直したいときに押せます",
                        "Puts the enabled shape keys into the outfit's mesh (a copy of the original) as BlendShapes exactly as listed above, and assigns it to the renderer. The data is kept. You can press it even when already applied, to rebuild the mesh"))))
                {
                    ExportToMesh(options);
                }
            }

            DrawClipOptions(problem, planFailed, options);
            DrawBatchExport(options);
            DrawBake(problem);

            MeshState state = _renderer != null ? ShapeKeyExporter.GetMeshState(_component, _renderer) : MeshState.None;
            EditorGUILayout.LabelField(L10n.Tr("レンダラーのメッシュ: ", "Renderer mesh: ") + DescribeMeshState(state), EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(!ShapeKeyProtection.CanRestoreMesh(_component) || _renderer == null || state == MeshState.Source))
            {
                if (GUILayout.Button(L10n.Tr("元のメッシュに戻す", "Restore the original mesh"))) RestoreSourceMesh();
            }

            EditorGUILayout.HelpBox(L10n.Tr(
                "データ（アセット）を削除しても、反映したメッシュはアセットとして残ります。このツールを消しても、メッシュは消えません。",
                "Even if the data asset is deleted, the mesh the shape keys were applied to remains as an asset. Removing this tool does not delete the mesh."), MessageType.None);
        }

        // かんたん表示のメッシュへの反映。データとメッシュが同じなら「✓ メッシュに反映済み」の1行、違うときだけ目立つボタン。
        // 保存先・名前の前置き・反映の内容の一覧などは「くわしく」で出す
        void DrawMainExport(string problem)
        {
            ShapeKeyBuildOptions options = ShapeKeyBuildOptions.FromComponent(_component, _settings.OverwriteSameName);
            List<ExportPlanItem> plan = problem == null ? ShapeKeyExporter.GetPlan(_component, options) : null;
            string planProblem = plan != null ? ShapeKeyExportPlan.FirstProblem(plan) : null;
            if (planProblem != null) EditorGUILayout.HelpBox(planProblem, MessageType.Warning);
            if (problem != null || planProblem != null)
            {
                using (new EditorGUI.DisabledScope(true)) GUILayout.Button(L10n.Tr("メッシュに反映", "Apply to Mesh"));
                return;
            }
            if (_component != null && _component.ApplyMode == ShapeKeyApplyMode.NonDestructive)
            {
                // ビルド時に適用する設定なら、シーンのメッシュには反映しない（くわしく表示で変えられる）
                EditorGUILayout.LabelField(L10n.Tr("アップロードのとき（ビルド時）に反映する設定です。シーンのメッシュは元のまま変えません（「くわしく」で変えられます）。",
                    "Set to apply at upload (build time). The mesh in the scene is left as it is (change this in the Detailed view)."), EditorStyles.wordWrappedMiniLabel);
                return;
            }

            ShapeKeyMeshStatus status = GetMeshStatus();
            if (status.Problem == null && status.ChangeCount == 0)
            {
                DrawMeshStatusLine(status);
                return;
            }
            DrawMeshStatusLine(status);
            if (_applyButtonStyle == null) _applyButtonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            Color background = GUI.backgroundColor;
            GUI.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.75f, 1f) : new Color(0.55f, 0.8f, 1f);
            try
            {
                using (new EditorGUI.DisabledScope(status.Problem != null))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr($"メッシュに反映（変更 {status.ChangeCount} 件）", $"Apply to Mesh ({status.ChangeCount} change(s))"),
                        L10n.Tr("シェイプキーのデータを、衣装のメッシュ（元のメッシュの複製）に BlendShape として入れて、レンダラーに付けます。データは残ります。保存先などの設定は「くわしく」で変えられます",
                            "Puts the shape key data into the outfit's mesh (a copy of the original) as BlendShapes and assigns it to the renderer. The data is kept. Folder and other settings are in the Detailed view")),
                        _applyButtonStyle, GUILayout.Height(26f)))
                    {
                        ExportToMesh(options);
                    }
                }
            }
            finally
            {
                GUI.backgroundColor = background;
            }
        }

        // 「✓ メッシュに反映済み」か、メッシュと違う所の1行
        void DrawMeshStatusLine(ShapeKeyMeshStatus status)
        {
            if (status.Problem != null)
            {
                EditorGUILayout.LabelField(L10n.Tr("メッシュと比べられません: ", "Cannot compare with the mesh: ") + status.Problem, EditorStyles.wordWrappedMiniLabel);
                return;
            }
            if (status.ChangeCount == 0)
            {
                if (_syncedStyle == null)
                {
                    _syncedStyle = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
                    _syncedStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.88f, 0.45f) : new Color(0.08f, 0.45f, 0.1f);
                }
                EditorGUILayout.LabelField(new GUIContent(L10n.Tr("✓ メッシュに反映済み", "✓ Applied to the mesh"),
                    L10n.Tr("シェイプキーのデータと、衣装のメッシュの BlendShape は同じです（布の編集の「シェイプキーとして保存」でも、メッシュに入っています）。もう一度押す必要はありません",
                        "The shape key data and the BlendShapes of the outfit's mesh are the same (\"Save as Shape Key\" in the cloth edit also puts them into the mesh). There is no need to apply again")), _syncedStyle);
                return;
            }
            EditorGUILayout.LabelField(L10n.Tr("メッシュと違う所: ", "Differs from the mesh: ") + status.Describe(), EditorStyles.wordWrappedMiniLabel);
        }

        // 名前の前置き・後置きと、頂点数違いを飛ばす設定（コンポーネントに持つので、ビルド時の適用でも同じになる）
        void DrawNaming()
        {
            if (_component == null) return;
            EditorGUI.BeginChangeCheck();
            string prefix = EditorGUILayout.TextField(new GUIContent(L10n.Tr("名前の前置き", "Name prefix"),
                L10n.Tr("メッシュに入れる BlendShape の名前の前に付ける文字です（例 GP02_）。データのシェイプキーの名前は変わりません。ビルド時の適用でも同じ名前になります",
                    "Text put before the exported BlendShape names (for example GP02_). The names in the data are unchanged. The build-time apply uses the same names")), _component.NamePrefix);
            string suffix = EditorGUILayout.TextField(new GUIContent(L10n.Tr("名前の後置き", "Name suffix"),
                L10n.Tr("メッシュに入れる BlendShape の名前の後ろに付ける文字です", "Text put after the exported BlendShape names")), _component.NameSuffix);
            bool skip = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("頂点数が違うものは飛ばして反映する", "Skip shape keys with a different vertex count"),
                L10n.Tr("オフなら、頂点数がメッシュと合わないシェイプキーがあるとメッシュへの反映（とビルド）が失敗します。オンなら、そのシェイプキーだけ飛ばします",
                    "If off, a shape key whose vertex count does not match the mesh makes the export (and the build) fail. If on, only that shape key is skipped")), _component.SkipIncompatible);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_component, "GP02 " + L10n.Tr("反映の設定の変更", "Change export settings"));
                _component.NamePrefix = prefix ?? "";
                _component.NameSuffix = suffix ?? "";
                _component.SkipIncompatible = skip;
                EditorUtility.SetDirty(_component);
            }
        }

        // 書き出しの前に、何が起きるか（追加・上書き・別名・焼き込み・飛ばす）の一覧
        void DrawPlan(List<ExportPlanItem> plan)
        {
            _settings.ShowExportPlan = EditorGUILayout.Foldout(_settings.ShowExportPlan,
                L10n.Tr("反映の内容（実行前の確認）", "What applying will do (check before running)") + (plan != null ? "  —  " + ShapeKeyExportPlan.Summarize(plan) : ""), true);
            if (!_settings.ShowExportPlan || plan == null) return;
            if (plan.Count == 0)
            {
                EditorGUILayout.LabelField(L10n.Tr("有効なシェイプキーがありません。", "There are no enabled shape keys."), EditorStyles.miniLabel);
                return;
            }
            float height = Mathf.Min(plan.Count, 8) * (EditorGUIUtility.singleLineHeight + 1f) + 6f;
            using (var scroll = new EditorGUILayout.ScrollViewScope(_planScroll, EditorStyles.helpBox, GUILayout.Height(height)))
            {
                _planScroll = scroll.scrollPosition;
                foreach (ExportPlanItem item in plan)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(new GUIContent(ShapeKeyExportPlan.Label(item.Action), item.Detail), PlanStyle(item.Action), GUILayout.Width(62f));
                        string text = string.IsNullOrEmpty(item.BlendShapeName) || item.BlendShapeName == item.Entry.Name
                            ? item.Entry.Name
                            : item.Entry.Name + "  →  " + item.BlendShapeName;
                        GUILayout.Label(new GUIContent(text, item.Detail), EditorStyles.miniLabel);
                    }
                }
            }
        }

        GUIStyle PlanStyle(ExportAction action)
        {
            if (_planStyles.TryGetValue(action, out GUIStyle style) && style != null) return style;
            style = new GUIStyle(EditorStyles.miniBoldLabel);
            Color color;
            switch (action)
            {
                case ExportAction.Add: color = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.85f, 0.45f) : new Color(0.1f, 0.5f, 0.1f); break;
                case ExportAction.Overwrite: color = EditorGUIUtility.isProSkin ? new Color(1f, 0.7f, 0.3f) : new Color(0.7f, 0.4f, 0f); break;
                case ExportAction.Rename: color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.8f, 1f) : new Color(0f, 0.4f, 0.7f); break;
                case ExportAction.Prebake: color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.65f, 1f) : new Color(0.1f, 0.2f, 0.8f); break;
                case ExportAction.Invalid: color = new Color(0.9f, 0.25f, 0.25f); break;
                default: color = Color.gray; break;
            }
            style.normal.textColor = color;
            _planStyles[action] = style;
            return style;
        }

        void DrawClipOptions(string problem, bool planFailed, ShapeKeyBuildOptions options)
        {
            EditorGUI.BeginChangeCheck();
            _settings.CreateClips = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("トグル用の AnimationClip も作る", "Also create toggle AnimationClips"),
                L10n.Tr("シェイプキーごとに、重み 0 と 100 のクリップを作ります。パスはアバターのルート（Animator のある親）からのレンダラーのパスです。VRChat の FX レイヤーなどにそのまま使えます",
                    "Creates a weight-0 clip and a weight-100 clip for each shape key. The path is the renderer's path from the avatar root (the parent with the Animator). Usable as-is in VRChat's FX layer")), _settings.CreateClips);
            if (_settings.CreateClips) _settings.ClipFolder = EditorGUILayout.TextField(L10n.Tr("クリップの保存先", "Clip folder"), _settings.ClipFolder);
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            using (new EditorGUI.DisabledScope(problem != null || planFailed || !(_renderer is SkinnedMeshRenderer)))
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("AnimationClip だけ作る", "Create AnimationClips only"),
                    L10n.Tr("メッシュには反映せず、上の一覧の BlendShape の名前で、重み 0 と 100 のクリップを作ります（既にあるクリップは中身だけ置き換えるので、アニメーターの参照は保たれます）",
                        "Does not export the mesh; creates the weight-0 and weight-100 clips with the BlendShape names listed above (existing clips are updated in place, so animator references are kept)"))))
                {
                    CreateClips(options);
                }
            }
        }

        void CreateClips(ShapeKeyBuildOptions options)
        {
            Run(() =>
            {
                List<ShapeKeyAnimationExporter.ToggleClips> clips = ShapeKeyAnimationExporter.CreateToggleClips(_component, options, _settings.ClipFolder);
                Show(L10n.Tr($"{clips.Count} 個のシェイプキーの AnimationClip（重み 0 と 100）を {_settings.ClipFolder} に作りました。",
                    $"Created AnimationClips (weight 0 and 100) for {clips.Count} shape key(s) in {_settings.ClipFolder}."), MessageType.Info);
            });
        }

        // 同じアバターの下の、複数の GP02 Shape Keys（重ね着の服ごと）を一度に書き出す
        void DrawBatchExport(ShapeKeyBuildOptions options)
        {
            if (_renderer == null) return;
            // 階層を毎回たどると重いので、1 秒ごとにだけ探し直す
            if (_batchRoot == null || _batchFor != _renderer || EditorApplication.timeSinceStartup - _batchTime > 1.0)
            {
                _batchFor = _renderer;
                _batchRoot = ShapeKeyExporter.FindAvatarRoot(_renderer.transform);
                _batchComponents = ShapeKeyExporter.FindComponents(_batchRoot);
                _batchTime = EditorApplication.timeSinceStartup;
            }
            Transform root = _batchRoot;
            List<GP02ShapeKeys> components = _batchComponents;
            if (components.Count < 2) return;

            if (GUILayout.Button(new GUIContent(L10n.Tr($"このアバターの GP02 をまとめてメッシュに反映（{components.Count} 個）", $"Apply all GP02 under this avatar to their meshes ({components.Count})"),
                L10n.Tr($"「{root.name}」の下にある、すべての GP02 Shape Keys（重ね着の服ごと）を一度にメッシュに反映します。実行の前に内容を確認します",
                    $"Exports every GP02 Shape Keys under \"{root.name}\" (each clothing layer) at once. The contents are shown for confirmation first"))))
            {
                ExportAllUnder(root, components);
                GUIUtility.ExitGUI();
            }
        }

        void ExportAllUnder(Transform root, List<GP02ShapeKeys> components)
        {
            _meshStatus = null;
            // 実行の前に、コンポーネントごとに何が起きるかを見せて確かめる
            var summary = new StringBuilder();
            foreach (GP02ShapeKeys component in components)
            {
                string problem = ShapeKeyExporter.GetProblem(component);
                if (problem != null)
                {
                    summary.AppendLine($"{component.gameObject.name}: {L10n.Tr("反映できません", "cannot apply")} ({problem})");
                    continue;
                }
                List<ExportPlanItem> plan = ShapeKeyExporter.GetPlan(component, ShapeKeyBuildOptions.FromComponent(component, _settings.OverwriteSameName));
                summary.AppendLine($"{component.gameObject.name}: {ShapeKeyExportPlan.Summarize(plan)}");
            }
            if (!EditorUtility.DisplayDialog("GP02", L10n.Tr(
                    $"「{root.name}」の下の {components.Count} 個をメッシュに反映します。\n\n{summary}",
                    $"Export {components.Count} component(s) under \"{root.name}\".\n\n{summary}"),
                L10n.Tr("反映する", "Apply"), L10n.Tr("やめる", "Cancel"))) return;

            Run(() =>
            {
                List<ShapeKeyExporter.BatchItem> results = ShapeKeyExporter.ExportAll(components, _settings.OverwriteSameName, _settings.ExportFolder,
                    _settings.CreateClips, _settings.ClipFolder, root);
                int ok = 0;
                var errors = new StringBuilder();
                foreach (ShapeKeyExporter.BatchItem item in results)
                {
                    if (item.Error == null) ok++;
                    else errors.AppendLine(item.Error);
                }
                string message = L10n.Tr($"{ok} 個をメッシュに反映しました。", $"Applied {ok}.");
                if (errors.Length > 0) message += L10n.Tr($"\n反映できなかったもの:\n{errors}", $"\nFailed:\n{errors}");
                Show(message, errors.Length > 0 ? MessageType.Warning : MessageType.Info);
            });
            ReloadTree();
        }

        void ExportToMesh(ShapeKeyBuildOptions options)
        {
            _meshStatus = null;
            Run(() =>
            {
                Mesh mesh = ShapeKeyExporter.ExportToMesh(_component, options, _settings.ExportFolder);
                string message = L10n.Tr($"メッシュに反映しました（{AssetDatabase.GetAssetPath(mesh)}、BlendShape {mesh.blendShapeCount} 個）。",
                    $"Applied to the mesh ({AssetDatabase.GetAssetPath(mesh)}, {mesh.blendShapeCount} BlendShapes).");
                if (_settings.CreateClips && _renderer is SkinnedMeshRenderer)
                {
                    List<ShapeKeyAnimationExporter.ToggleClips> clips = ShapeKeyAnimationExporter.CreateToggleClips(_component, options, _settings.ClipFolder);
                    message += L10n.Tr($" AnimationClip を {clips.Count} 組作りました。", $" Created {clips.Count} pair(s) of AnimationClips.");
                }
                Show(message, MessageType.Info);
            });
            ReloadTree();
        }

        // ---- 素体への焼き込み ----

        void DrawBake(string problem)
        {
            EditorGUI.BeginChangeCheck();
            _settings.RemapBoneWeights = EditorGUILayout.ToggleLeft(new GUIContent(L10n.Tr("大きく動いた頂点のウェイトを付け替える", "Remap bone weights of far-moved vertices"),
                L10n.Tr("焼き込みのとき、大きく動いた頂点のボーンウェイトを付け替えます", "When baking, the bone weights of vertices that moved far are remapped")), _settings.RemapBoneWeights);
            if (_settings.RemapBoneWeights)
            {
                EditorGUI.indentLevel++;
                string[] labels = L10n.Language == UiLanguage.English ? RemapModeLabelsEn : RemapModeLabels;
                _settings.RemapMode = (BoneWeightRemapMode)EditorGUILayout.Popup(new GUIContent(L10n.Tr("付け替えかた", "Remap mode"),
                    L10n.Tr("近くの頂点からもらう: 動いた先に一番近い頂点のウェイトをもらいます。\n左右を反転したウェイトをもらう: 左右の境を越えて反対側へ動いた頂点だけ、自分のウェイトのボーンを左右反対のボーン（名前の Left/Right、.L/.R、_L/_R などで決めます）に付け替えます。左右の軸と中心は「操作の設定」の値です",
                        "Take from the nearest vertex: takes the weights of the vertex nearest to where it moved.\nTake the left/right-flipped weights: only vertices that moved across the left/right border get their bones swapped for the opposite-side bones (found by names such as Left/Right, .L/.R, _L/_R). The axis and center are the values in the operation settings")),
                    (int)_settings.RemapMode, labels);
                _settings.RemapThresholdCm = EditorGUILayout.FloatField(L10n.Tr("付け替える動きの大きさ（cm）", "Move distance to remap (cm)"), _settings.RemapThresholdCm);
                _settings.RemapSmoothIterations = EditorGUILayout.IntSlider(new GUIContent(L10n.Tr("なめらかにする回数", "Smoothing passes"),
                    L10n.Tr("付け替えた頂点のウェイトを、隣の頂点となじませる回数です（0 でなじませない）", "How many times the weights of the remapped vertices are blended with their neighbors (0 for none)")), _settings.RemapSmoothIterations, 0, 10);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            using (new EditorGUI.DisabledScope(problem != null || _tree.GetSelectedEntries().Count == 0))
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("選んだものを素体に焼き込む", "Bake the selection into the base"),
                    L10n.Tr("選んだシェイプキーを、スライダーの重みで素体に焼き込んだメッシュを作って割り当てます。元のメッシュは変わりません。印を付けて毎回メッシュへの反映で焼き込みたいときは、上の詳細で「素体に焼き込む」にします",
                        "Bake the selection with the slider weights into a new mesh and assign it. The original mesh is unchanged. To bake on every export instead, mark it with \"Bake into the base\" in the details above"))))
                {
                    BakeSelected();
                }
            }
        }

        string DescribeMeshState(MeshState state)
        {
            switch (state)
            {
                case MeshState.None: return L10n.Tr("なし（None）", "None");
                case MeshState.Source: return L10n.Tr("元のメッシュ", "Original");
                case MeshState.Exported: return L10n.Tr("反映したメッシュ", "Applied");
                default: return L10n.Tr("その他（焼き込みなど）", "Other (baked etc.)");
            }
        }

        void BakeSelected()
        {
            Run(() =>
            {
                List<ShapeKeyEntry> selected = _tree.GetSelectedEntries();
                float[] weights = new float[selected.Count];
                bool anyWeight = false;
                for (int i = 0; i < weights.Length; i++)
                {
                    weights[i] = selected[i].PreviewWeight;
                    if (weights[i] != 0f) anyWeight = true;
                }
                if (!anyWeight)
                {
                    if (!EditorUtility.DisplayDialog("GP02", L10n.Tr(
                        "選んだシェイプキーの重みがすべて 0 です。重み 100 で焼き込みますか？",
                        "All selected weights are 0. Bake with weight 100?"), L10n.Tr("100 で焼き込む", "Bake at 100"), L10n.Tr("やめる", "Cancel"))) return;
                    for (int i = 0; i < weights.Length; i++) weights[i] = 100f;
                }
                BoneWeightRemapOptions remap = null;
                if (_settings.RemapBoneWeights)
                {
                    remap = new BoneWeightRemapOptions
                    {
                        Mode = _settings.RemapMode,
                        Threshold = _settings.RemapThreshold,
                        Axis = _settings.Axis,
                        Center = _settings.Center,
                        SmoothIterations = _settings.RemapSmoothIterations,
                    };
                }
                Mesh mesh = ShapeKeyExporter.BakeIntoBase(_component, selected, weights, remap, _settings.ExportFolder);
                Show(L10n.Tr($"素体に焼き込んで {AssetDatabase.GetAssetPath(mesh)} に保存し、レンダラーに割り当てました。元のメッシュは変わっていません。",
                    $"Baked into the base, saved to {AssetDatabase.GetAssetPath(mesh)} and assigned it. The original mesh is unchanged."), MessageType.Info);
            });
            ReloadTree();
        }

        // ---- バックアップ ----

        void DrawBackups(GP02ShapeKeyData data)
        {
            _settings.ShowBackups = EditorGUILayout.Foldout(_settings.ShowBackups, L10n.Tr("バックアップ（自動）", "Backups (automatic)"), true);
            if (!_settings.ShowBackups) return;
            ShapeKeyBackupGUI.Draw(data, () =>
            {
                _treeState.selectedIDs.Clear();
                ReloadTree();
                if (_renderer != null && ShapeKeyPreview.IsActive(_renderer)) ApplyPreview();
                Show(L10n.Tr("バックアップから戻しました。", "Restored from the backup."), MessageType.Info);
            });
        }
    }
}
