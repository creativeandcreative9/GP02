using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    public enum TargetWarningKind
    {
        /// <summary>編集を始められない（メッシュが無い、三角形が無い、など）。</summary>
        CannotStart,

        /// <summary>プレハブのアセットそのもの。</summary>
        PrefabAsset,

        /// <summary>MeshFilter のメッシュにはシェイプキーを表示する仕組みが無い。</summary>
        MeshRenderer,

        /// <summary>Read/Write が無効。</summary>
        Unreadable,

        /// <summary>頂点が多い（開始に時間がかかる）。</summary>
        ManyVertices,

        /// <summary>頂点が非常に多い（重くなる）。</summary>
        VeryManyVertices,

        /// <summary>スケールが負（裏返っている）。</summary>
        NegativeScale,

        /// <summary>スケールが軸ごとに違う（非一様）。</summary>
        NonUniformScale,

        /// <summary>シェイプキーの重みが 0 でない（今の重みの形が差分の基準になる）。</summary>
        BlendShapeWeights,

        /// <summary>ボーンが欠けている（Missing）。</summary>
        MissingBones,

        /// <summary>同じメッシュを、ほかのレンダラーも使っている。</summary>
        SharedMesh,

        /// <summary>対称がオンで、鏡像の相手のいない点が多い（編集中）。</summary>
        UnmatchedMirror,

        /// <summary>Burst が無い（計算が遅い）。</summary>
        NoBurst,

        /// <summary>ピンが1つも無い（布全体が動きやすい。編集中）。</summary>
        NoPins,

        /// <summary>体が布を突き抜けている（編集中に最後に調べた結果）。</summary>
        Penetration,

        /// <summary>体（素体・顔）のメッシュのようだ（服ではない物を選んだかもしれない。<see cref="BodyMeshGuess"/>）。</summary>
        LikelyBody,
    }

    /// <summary>診断の「直す」ボタンでできること。</summary>
    public enum TargetWarningFix
    {
        None,

        /// <summary>モデルの Import Settings で Read/Write を有効にする（<see cref="ClothEditWarnings.TryEnableReadWrite"/>）。</summary>
        EnableReadWrite,

        /// <summary>シェイプキーの重みをすべて 0 にする（<see cref="ClothEditWarnings.TryResetBlendShapeWeights"/>）。</summary>
        ResetBlendShapeWeights,

        /// <summary>鏡の面を自動で合わせる（<see cref="ClothEditController.AutoFitSymmetryPlane"/>）。</summary>
        FitMirrorPlane,

        /// <summary>Burst を入れる（<see cref="BurstInstaller.Install"/>）。</summary>
        InstallBurst,
    }

    /// <summary>対象に対する、ウィンドウに出す注意。</summary>
    public readonly struct TargetWarning
    {
        public readonly TargetWarningKind Kind;
        public readonly MessageType Severity;
        public readonly string Message;

        /// <summary>「直す」ボタンでできること。</summary>
        public readonly TargetWarningFix Fix;

        /// <summary>ボタンで自動で直せるか。</summary>
        public bool CanAutoFix => Fix != TargetWarningFix.None;

        public TargetWarning(TargetWarningKind kind, MessageType severity, string message, bool canAutoFix = false)
            : this(kind, severity, message, canAutoFix ? TargetWarningFix.EnableReadWrite : TargetWarningFix.None)
        {
        }

        public TargetWarning(TargetWarningKind kind, MessageType severity, string message, TargetWarningFix fix)
        {
            Kind = kind;
            Severity = severity;
            Message = message;
            Fix = fix;
        }

        /// <summary>重さの表示（重大・注意・情報）。</summary>
        public string SeverityLabel => ClothEditWarnings.SeverityLabel(Severity);
    }

    /// <summary>
    /// 診断: 編集を始める前と編集中に、対象と編集の問題をまとめて点検する。重さは <see cref="MessageType.Error"/>（重大）・
    /// <see cref="MessageType.Warning"/>（注意）・<see cref="MessageType.Info"/>（情報）。画面に依存しないので、テストで確かめられる。
    /// </summary>
    public static partial class ClothEditWarnings
    {
        /// <summary>スケールの軸ごとの違いがこの割合を超えたら、非一様とみなす。</summary>
        public const float NonUniformScaleTolerance = 0.01f;

        /// <summary>重さの表示。</summary>
        public static string SeverityLabel(MessageType severity)
        {
            switch (severity)
            {
                case MessageType.Error: return L10n.Tr("重大", "Critical");
                case MessageType.Warning: return L10n.Tr("注意", "Warning");
                default: return L10n.Tr("情報", "Info");
            }
        }

        /// <summary>「直す」ボタンの文字。</summary>
        public static string FixLabel(TargetWarningFix fix)
        {
            switch (fix)
            {
                case TargetWarningFix.EnableReadWrite: return L10n.Tr("Read/Write を有効にする", "Enable Read/Write");
                case TargetWarningFix.ResetBlendShapeWeights: return L10n.Tr("重みを 0 にする", "Set Weights to 0");
                case TargetWarningFix.FitMirrorPlane: return L10n.Tr("左右の中心を自動で合わせる", "Auto-Fit Mirror Plane");
                case TargetWarningFix.InstallBurst: return L10n.Tr("Burst を入れる", "Install Burst");
                default: return "";
            }
        }

        /// <summary>重い順（重大 → 注意 → 情報）に並べ替える。同じ重さの中は元の順。</summary>
        public static void SortBySeverity(List<TargetWarning> items)
        {
            var sorted = new List<TargetWarning>(items.Count);
            foreach (MessageType severity in new[] { MessageType.Error, MessageType.Warning, MessageType.Info, MessageType.None })
            {
                foreach (TargetWarning item in items)
                {
                    if (item.Severity == severity) sorted.Add(item);
                }
            }
            items.Clear();
            items.AddRange(sorted);
        }

        /// <summary>
        /// 編集中の診断にも出す注意か。編集を始める前の点検だけで出すもの（シェイプキーの重み・体のメッシュのようだ）は false。
        /// 編集中は、重みを 0 に戻す「直す」が使えず（重みを変えると編集が作り直される）、体のメッシュを選んでいても今さら選び直せないため。
        /// </summary>
        public static bool IsShownWhileEditing(TargetWarningKind kind)
        {
            return kind != TargetWarningKind.BlendShapeWeights && kind != TargetWarningKind.LikelyBody;
        }

        /// <summary>
        /// GP02 が保存したシェイプキー（その衣装の <see cref="GP02ShapeKeys"/> のデータに記録されているもの）の、メッシュでの名前
        /// （書き出しの前置き・後置きを付けたもの）を names に入れる（先に空にしない）。保存すると、メッシュに入れて重みを 100 にするので、
        /// 「シェイプキーの重みが 0 でない」の注意から除くのに使う。
        /// </summary>
        public static void CollectSavedShapeNames(Renderer target, ICollection<string> names)
        {
            if (target == null) return;
            GP02ShapeKeys component = target.GetComponent<GP02ShapeKeys>();
            if (component == null || component.Data == null) return;
            ShapeKeyBuildOptions options = ShapeKeyBuildOptions.FromComponent(component);
            foreach (ShapeKeyEntry entry in component.Data.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                names.Add(entry.Name);
                names.Add(options.Decorate(entry.Name));
            }
        }

        /// <summary>頂点がこれ以上だと、編集の開始に数秒かかる。</summary>
        public const int ManyVertices = 30000;

        /// <summary>頂点がこれ以上だと、重くなる。</summary>
        public const int VeryManyVertices = 100000;

        /// <summary>対象の注意を result に入れる（先に空にする）。対象が null なら何も入れない（ウィンドウが案内を出す）。</summary>
        public static void Collect(Renderer target, List<TargetWarning> result, int manyVertices = ManyVertices, int veryManyVertices = VeryManyVertices)
        {
            result.Clear();
            if (target == null) return;

            string problem = ClothEditController.GetStartProblem(target);
            if (problem != null)
            {
                result.Add(new TargetWarning(TargetWarningKind.CannotStart, MessageType.Error, problem));
                return;
            }

            if (PrefabUtility.IsPartOfPrefabAsset(target))
            {
                result.Add(new TargetWarning(TargetWarningKind.PrefabAsset, MessageType.Error,
                    L10n.Tr("プレハブのアセットそのものは編集できません。シーンに置いたものを選んでください。",
                        "A prefab asset itself cannot be edited. Select the instance placed in the scene.")));
            }

            // 体（素体・顔）のメッシュのようなら、服ではない物を選んだのかもしれない（編集は止めない）。衣装の候補は、窓が一覧にして出す
            if (ClothEditStartMenu.IsLikelyBody(target))
            {
                result.Add(new TargetWarning(TargetWarningKind.LikelyBody, MessageType.Warning,
                    L10n.Tr($"「{target.name}」は体のメッシュのようです。服を編集するときは、服のオブジェクトを選んでください。",
                        $"\"{target.name}\" looks like a body mesh. To edit clothes, select the clothing object.")));
            }

            if (target is MeshRenderer)
            {
                result.Add(new TargetWarning(TargetWarningKind.MeshRenderer, MessageType.Info,
                    L10n.Tr("MeshFilter のメッシュにはシェイプキーを表示する仕組みがありません。保存はできますが、見た目は変わりません。",
                        "A MeshFilter mesh cannot display shape keys. You can save, but the appearance will not change.")));
            }

            Mesh mesh = ClothEditController.GetMesh(target);
            if (mesh != null && !mesh.isReadable)
            {
                result.Add(new TargetWarning(TargetWarningKind.Unreadable, MessageType.Warning,
                    L10n.Tr("この衣装のメッシュは Read/Write が無効です。エディタ上では編集できます。メッシュを読めない他のツールと組み合わせるなら、有効にしてください（ボタンで直せます）。",
                        "This mesh has Read/Write disabled. You can edit it in the editor. Enable it if you combine this with other tools that need to read the mesh (the button fixes it)."),
                    TargetWarningFix.EnableReadWrite));
            }

            CollectTransformAndSkinning(target, mesh, result);

            int vertices = mesh != null ? mesh.vertexCount : 0;
            if (vertices >= veryManyVertices)
            {
                result.Add(new TargetWarning(TargetWarningKind.VeryManyVertices, MessageType.Warning,
                    L10n.Tr($"頂点が非常に多いメッシュです（{vertices:N0}）。編集の開始に10秒以上かかり、操作が重くなることがあります。Burst を有効にするか、布の部分だけを別のメッシュに分けてください。",
                        $"This mesh has a very large number of vertices ({vertices:N0}). Starting may take over 10 seconds and editing may feel heavy. Enable Burst, or split the cloth into a separate mesh.")));
            }
            else if (vertices >= manyVertices)
            {
                result.Add(new TargetWarning(TargetWarningKind.ManyVertices, MessageType.Info,
                    L10n.Tr($"頂点が多いメッシュです（{vertices:N0}）。編集の開始に数秒かかります。Burst が有効だと操作が軽くなります。",
                        $"This mesh has many vertices ({vertices:N0}). Starting takes a few seconds. Editing is lighter with Burst enabled.")));
            }
        }

        // スケール・シェイプキーの重み・ボーン・メッシュの共有
        static void CollectTransformAndSkinning(Renderer target, Mesh mesh, List<TargetWarning> result)
        {
            Vector3 scale = target.transform.lossyScale;
            if (scale.x * scale.y * scale.z < 0f)
            {
                result.Add(new TargetWarning(TargetWarningKind.NegativeScale, MessageType.Warning,
                    L10n.Tr($"「{target.name}」か親のスケールが負です（裏返し）。面の表裏や左右の向きが逆になり、「元の形より内側に入らない」が反対に効くことがあります。スケールを正の値にしてください。",
                        $"The scale of \"{target.name}\" or a parent is negative (mirrored). Faces and the mirror direction flip, and \"Stay Outside Original Surface\" may work in reverse. Use a positive scale.")));
            }
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            float minScale = Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            if (maxScale > 0f && (maxScale - minScale) > NonUniformScaleTolerance * maxScale)
            {
                result.Add(new TargetWarning(TargetWarningKind.NonUniformScale, MessageType.Info,
                    L10n.Tr($"「{target.name}」か親のスケールが軸ごとに違います（{scale.x:0.###}, {scale.y:0.###}, {scale.z:0.###}）。編集はできますが、点の 1cm 間隔は見た目の大きさで決まり、保存した差分はメッシュの座標に直して入れます。",
                        $"The scale of \"{target.name}\" or a parent differs per axis ({scale.x:0.###}, {scale.y:0.###}, {scale.z:0.###}). Editing works, but the 1 cm particle spacing follows the visible size, and saved deltas are converted to mesh coordinates.")));
            }

            if (target is SkinnedMeshRenderer skinned && mesh != null)
            {
                // GP02 が保存したシェイプキーは、保存すると重みが 100 になるのが普通なので、数えない（保存した直後に、壊れたように見えないため）
                var saved = new HashSet<string>();
                CollectSavedShapeNames(target, saved);
                int nonZero = 0;
                string first = null;
                for (int k = 0; k < mesh.blendShapeCount; k++)
                {
                    if (Mathf.Abs(skinned.GetBlendShapeWeight(k)) <= 1e-4f) continue;
                    string shapeName = mesh.GetBlendShapeName(k);
                    if (saved.Contains(shapeName)) continue;
                    nonZero++;
                    if (first == null) first = shapeName;
                }
                if (nonZero > 0)
                {
                    result.Add(new TargetWarning(TargetWarningKind.BlendShapeWeights, MessageType.Warning,
                        L10n.Tr($"シェイプキーの重みが 0 でないものが {nonZero} 個あります（「{first}」など）。今の重みの形が差分の基準になるので、重みを 0 に戻すと、保存したシェイプキーの形がずれます。体型の調整など、そのまま使う重みなら問題ありません。",
                            $"{nonZero} shape key(s) have a non-zero weight (such as \"{first}\"). The shape with the current weights becomes the base of the delta, so the saved shape key shifts when those weights return to 0. That is fine for weights you keep (such as body adjustments)."),
                        TargetWarningFix.ResetBlendShapeWeights));
                }

                Transform[] bones = skinned.bones;
                int missing = 0;
                foreach (Transform bone in bones)
                {
                    if (bone == null) missing++;
                }
                bool needsBones = mesh.bindposes.Length > 0;
                if (missing > 0 || (needsBones && bones.Length < mesh.bindposes.Length))
                {
                    result.Add(new TargetWarning(TargetWarningKind.MissingBones, MessageType.Error,
                        L10n.Tr($"ボーンが欠けています（Missing {missing} 個、ボーン {bones.Length} / バインドポーズ {mesh.bindposes.Length}）。ポーズの形が正しく出ず、編集の結果がずれます。衣装をアバターに着せ直すか、ボーンの割り当てを直してください。",
                            $"Bones are missing ({missing} missing, {bones.Length} bones / {mesh.bindposes.Length} bind poses). The posed shape is wrong and the edit will be off. Re-dress the outfit on the avatar or fix the bone assignment.")));
                }
            }

            int sharing = CountOtherRenderersUsing(mesh, target);
            if (sharing > 0)
            {
                result.Add(new TargetWarning(TargetWarningKind.SharedMesh, MessageType.Info,
                    L10n.Tr($"同じメッシュを、ほかのオブジェクトも使っています（{sharing} 個）。保存はメッシュの複製に入れるので、ほかのオブジェクトの見た目は変わりません。同じ形にしたいときは、保存したメッシュをそちらにも割り当ててください。",
                        $"Other renderers ({sharing}) also use the same mesh. Saving goes into a copy of the mesh, so their appearance does not change. Assign the saved mesh to them too if they should match.")));
            }
        }

        // シーンの中で、同じメッシュを使っているほかのレンダラーの数
        static int CountOtherRenderersUsing(Mesh mesh, Renderer target)
        {
            if (mesh == null) return 0;
            int count = 0;
            foreach (SkinnedMeshRenderer other in Object.FindObjectsOfType<SkinnedMeshRenderer>(true))
            {
                if (other != target && other.sharedMesh == mesh) count++;
            }
            foreach (MeshFilter filter in Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (filter.sharedMesh != mesh) continue;
                if (filter.GetComponent<MeshRenderer>() is Renderer renderer && renderer == target) continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// 編集中の点検を result に足す（先に空にしない）: 対称がオンで相手のいない点が多い、Burst が無い、ピンが1つも無い。
        /// 編集中でなければ、Burst の有無だけを見る。
        /// </summary>
        public static void CollectEditing(ClothEditController controller, List<TargetWarning> result)
        {
            if (!BurstInstaller.IsAccelerated)
            {
                result.Add(new TargetWarning(TargetWarningKind.NoBurst, MessageType.Info,
                    L10n.Tr("Burst が入っていない（または無効な）ので、計算が遅めです。重ね着や大きな衣装では、仕上げが時間切れになって伸びが残りやすくなります（無くてもすべての機能は動きます）。",
                        "Burst is not installed (or is disabled), so the calculation is slower. Layered or large clothing may hit the settle time limit and keep some stretch (all features work without it)."),
                    ClothSolver.DefaultKernel == null ? TargetWarningFix.InstallBurst : TargetWarningFix.None));
            }
            if (controller == null || !controller.IsEditing) return;
            ClothEditSession session = controller.Session;

            if (session.PinnedCount == 0)
            {
                result.Add(new TargetWarning(TargetWarningKind.NoPins, MessageType.Info,
                    L10n.Tr("ピンが1つもありません。布全体が動きやすく、つまんだ所から遠くまで引っ張られます。動かしたくない所（ウエストや肩など）をピン留めしてください（「服の付け根を自動でピン留め」が便利です）。",
                        "There are no pins. The whole cloth moves easily and is dragged far from where you grab. Pin the parts that should not move (such as the waist or shoulders).")));
            }

            if (controller.Settings.SymmetryEnabled && controller.TryGetMirrorPlane(out _))
            {
                float ratio = controller.UnmatchedMirrorRatio;
                if (ratio > ClothEditSettings.UnmatchedMirrorWarnRatio)
                {
                    result.Add(new TargetWarning(TargetWarningKind.UnmatchedMirror, MessageType.Warning,
                        L10n.Tr($"左右の相手のいない点が {ratio * 100f:F0}% あります。その点をつまんでも、反対側は動きません。衣装が中心からずれているなら「左右の中心を自動で合わせる」を押してください（左右で形が違うときは、くわしく表示の「鏡像を探す許容距離」を広げます）。",
                            $"{ratio * 100f:F0}% of the points have no mirror partner. Grabbing them does not move the opposite side. If the outfit is off-center, use Auto-Fit Mirror Plane; if the sides differ in shape, widen the mirror tolerance."),
                        TargetWarningFix.FitMirrorPlane));
                }
            }
            CollectPenetration(controller, result);
        }

        /// <summary>
        /// シェイプキーの重みをすべて 0 にする（Unity の Undo に入る）。編集中は使わないこと（重みを変えると編集が作り直される）。
        /// 変えた数を返す。
        /// </summary>
        public static int TryResetBlendShapeWeights(Renderer target, out string message)
        {
            if (!(target is SkinnedMeshRenderer skinned) || skinned.sharedMesh == null)
            {
                message = L10n.Tr("シェイプキーのあるメッシュではありません。", "This is not a mesh with shape keys.");
                return 0;
            }
            Undo.RecordObject(skinned, L10n.Tr("GP02 シェイプキーの重みを 0 にする", "GP02 Reset Shape Key Weights"));
            int changed = 0;
            for (int k = 0; k < skinned.sharedMesh.blendShapeCount; k++)
            {
                if (skinned.GetBlendShapeWeight(k) == 0f) continue;
                skinned.SetBlendShapeWeight(k, 0f);
                changed++;
            }
            message = L10n.Tr($"{changed} 個のシェイプキーの重みを 0 にしました（Ctrl+Z で戻せます）。", $"Set {changed} shape key weight(s) to 0 (Ctrl+Z undoes this).");
            return changed;
        }

        /// <summary>
        /// Read/Write が無効なメッシュを、モデルの Import Settings で有効にする。
        /// モデルファイル（FBX など）から読み込まれたメッシュだけ直せる。直せたら true。message に結果の案内を入れる。
        /// </summary>
        public static bool TryEnableReadWrite(Renderer target, out string message)
        {
            Mesh mesh = ClothEditController.GetMesh(target);
            if (mesh == null)
            {
                message = L10n.Tr("メッシュがありません。", "There is no mesh.");
                return false;
            }
            if (mesh.isReadable)
            {
                message = L10n.Tr("すでに Read/Write は有効です。", "Read/Write is already enabled.");
                return true;
            }

            string path = AssetDatabase.GetAssetPath(mesh);
            var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                message = L10n.Tr("このメッシュはモデルファイル（FBX など）から読み込まれたものではないので、自動では直せません。モデルの Import Settings で Read/Write を有効にしてください。",
                    "This mesh was not imported from a model file (such as FBX), so it cannot be fixed automatically. Enable Read/Write in the model's Import Settings.");
                return false;
            }

            importer.isReadable = true;
            importer.SaveAndReimport();
            message = L10n.Tr($"「{path}」の Read/Write を有効にしました。", $"Enabled Read/Write for \"{path}\".");
            return true;
        }
    }
}
