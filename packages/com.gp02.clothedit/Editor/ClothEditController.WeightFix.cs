using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    // 動作チェックの修正の第2段: ウェイトを体に寄せる。
    // 動作チェックでウェイトが原因と分かった所（外へ逃がしても直らない所）の服の頂点に、編集の姿勢で一番近い体の点のウェイトを写す。
    // 適用する前に、候補のウェイトで動作チェックをやり直して前後を出し（PreviewWeightFix）、利用者が見てから適用する（ApplyWeightFix）。
    // 適用は、(a) シェイプキーのデータにウェイトの変更として記録し（書き出しとビルド時の適用で、元のメッシュに当てる）、
    // (b) 今の編集のメッシュのウェイトを変えて、編集を作り直す。どちらも Unity の Undo の1段で戻る。データから外す（RemoveWeightFix）こともできる
    public sealed partial class ClothEditController
    {
        WeightFixPreview _weightPreview;
        ClothEditSession _weightShapeSession;   // 確認の形と今の形が同じかを、最後に比べたときの編集と形の版
        int _weightShapeVersion = -1;
        bool _weightShapeSame;

        /// <summary>
        /// 最後の「ウェイトを体に寄せる」の確認（候補で調べ直した前後）。まだ確認していない・捨てたときは null。
        /// 形を変えると古くなる（<see cref="CanApplyWeightFix"/> が false）。
        /// </summary>
        public WeightFixPreview WeightFixPreview => _session != null ? _weightPreview : null;

        /// <summary>最後に適用した「ウェイトを体に寄せる」の結果。</summary>
        public WeightFixReport LastWeightFix { get; private set; }

        /// <summary>直前の「ウェイトを体に寄せる」の確認・適用・外すができなかった理由。できたときは空。</summary>
        public string LastWeightFixError { get; private set; } = "";

        /// <summary>今の結果（古くないもの）の、ウェイトが原因の貫通のか所の数。結果が無い・古いときは 0。</summary>
        public int WeightCausedRegionCount => _poseCheck != null && !IsPoseCheckStale ? _poseCheck.WeightCausedRegionCount : 0;

        /// <summary>
        /// ウェイトを体に寄せる確認ができるか（新しい結果に、ウェイトが原因の貫通があり、体の点を測ってあり、ボーンウェイトのある衣装を編集している）。
        /// </summary>
        public bool CanPreviewWeightFix
        {
            get
            {
                if (_session == null || IsPoseCheckRunning || WeightCausedRegionCount == 0) return false;
                if (_poseCheck.Anchors == null || _poseCheck.Body == null || _poseRig == null || _poseRig.IsDisposed) return false;
                for (int r = 0; r < _session.RendererCount; r++)
                {
                    if (_session.Renderers[r] is SkinnedMeshRenderer) return true;
                }
                return false;
            }
        }

        /// <summary>確認した候補を、今適用できるか（確認のあとに形を変えていない。保存して作り直しただけなら適用できる）。</summary>
        public bool CanApplyWeightFix
        {
            get
            {
                WeightFixPreview preview = _weightPreview;
                if (preview == null || _session == null || IsPoseCheckRunning || preview.After == null || preview.Before != _poseCheck) return false;
                if (preview.Candidates.Count != _session.RendererCount) return false;
                for (int r = 0; r < _session.RendererCount; r++)
                {
                    WeightFixCandidate candidate = preview.Candidates[r];
                    if (candidate == null) continue;
                    Mesh mesh = GetMesh(_session.Renderers[r]);
                    if (candidate.Renderer != _session.Renderers[r] || mesh == null || mesh.vertexCount != candidate.Weights.VertexCount) return false;
                }
                return SameShapeAsPreview(preview);
            }
        }

        /// <summary>確認した候補が、形を変えたので古くなったか。</summary>
        public bool IsWeightFixPreviewStale => _weightPreview != null && !CanApplyWeightFix;

        // 確認したときの形と、今の形が同じか（保存のあとの作り直しは、形が同じなので同じとみなす）。同じ版の間は比べ直さない
        bool SameShapeAsPreview(WeightFixPreview preview)
        {
            if (_weightShapeSession == _session && _weightShapeVersion == _session.ShapeVersion) return _weightShapeSame;
            _weightShapeSession = _session;
            _weightShapeVersion = _session.ShapeVersion;
            ReadOnlySpan<NVector3> now = _session.WeldedPositions;
            bool same = preview.Welded != null && preview.Welded.Length == now.Length;
            for (int i = 0; same && i < now.Length; i++)
            {
                NVector3 a = preview.Welded[i], b = now[i];
                same = Math.Abs(a.X - b.X) <= 1e-5f && Math.Abs(a.Y - b.Y) <= 1e-5f && Math.Abs(a.Z - b.Z) <= 1e-5f;
            }
            _weightShapeSame = same;
            return same;
        }

        /// <summary>確認した候補を捨てる（調べ直したアバターの複製も消す）。</summary>
        public void DiscardWeightFixPreview()
        {
            if (_weightPreview == null) return;
            _weightPreview.DisposeRig();
            _weightPreview = null;
            _weightShapeSession = null;
            Notify();
        }

        /// <summary>
        /// 編集中の衣装のデータに記録した、ボーンウェイトの変更の頂点の数（すべてのレンダラーの合計）。
        /// </summary>
        public int RecordedWeightEditVertexCount
        {
            get
            {
                if (_session == null) return 0;
                int count = 0;
                for (int r = 0; r < _session.RendererCount; r++)
                {
                    Renderer renderer = _session.Renderers[r];
                    var component = renderer != null ? renderer.GetComponent<GP02ShapeKeys>() : null;
                    if (component != null && component.Data != null && component.Data.HasWeightEdit) count += component.Data.WeightEdit.VertexCount;
                }
                return count;
            }
        }

        /// <summary>
        /// ウェイトを体に寄せる候補を作り、候補のウェイトで動作チェックをやり直して、前後を返す（まだ適用しない。シーンもアセットも変えない）。
        /// 対象は、今の結果でウェイトが原因と分かった所（<see cref="PoseCheckSummary.PlanWeightFix"/>）。体どうしの重なりは対象にしない。
        /// 強さ・半径は設定（<see cref="ClothEditSettings.WeightFixStrength"/>・<see cref="ClothEditSettings.WeightFixRadius"/>）。できなければ null（理由は <see cref="LastWeightFixError"/>）。
        /// </summary>
        public WeightFixPreview PreviewWeightFix() => PreviewWeightFix(null);

        /// <summary>
        /// <see cref="PreviewWeightFix()"/> と同じだが、strengthOverride を渡すと、この候補を作るときだけ設定の強さの代わりにその強さ（0〜1）を使う
        /// （確認の「弱めて試す」。<see cref="WeightFixPreview.WeakerStrength"/>）。設定の <see cref="ClothEditSettings.WeightFixStrength"/> は変えない
        /// （タイツなど、次の衣装では設定の強さのままにするため）。候補の <see cref="WeightFixPreview.Strength"/> には、実際に使った強さが入る。
        /// </summary>
        public WeightFixPreview PreviewWeightFix(float? strengthOverride)
        {
            LastWeightFixError = "";
            if (_session != null) ApplyPoseFixScope();
            if (!EnsureAlive() || !CanPreviewWeightFix)
            {
                LastWeightFixError = _session == null ? L10n.Tr("編集中ではありません。", "You are not editing.")
                    : WeightCausedRegionCount == 0 ? L10n.Tr("ウェイトが原因の貫通がありません（動作チェックをしてから使えます）。", "There is no penetration caused by weights (run the motion check first).")
                    : L10n.Tr("もう一度、動作チェックをしてください。", "Run the motion check again.");
                return null;
            }
            FinishStrokeKeepingPoseDepth();
            DiscardWeightFixPreview();
            var timer = Stopwatch.StartNew();
            PoseCheckResult before = _poseCheck;
            WeightFixPreview preview;
            try
            {
                if (ShowProgress) EditorUtility.DisplayProgressBar("GP02", L10n.Tr("体のウェイトを写しています…", "Copying the body's weights..."), 0.1f);
                preview = BuildWeightCandidates(before, strengthOverride, out string error);
                if (preview == null)
                {
                    LastWeightFixError = error;
                    return null;
                }
                if (preview.ChangedVertices == 0)
                {
                    LastWeightFixError = L10n.Tr("体のウェイトを写せる頂点がありませんでした（近くに体が無い、または体のボーンが服のボーンに対応しません）。",
                        "No vertex could take the body's weights (no body nearby, or the body's bones do not match the outfit's bones).");
                    return null;
                }
                if (ShowProgress) EditorUtility.DisplayProgressBar("GP02", L10n.Tr("候補のウェイトで、もう一度ポーズを試しています…", "Trying the poses again with the candidate weights..."), 0.4f);
                PoseCheckResult after = RunDetachedPoseCheck(before, preview, out PoseRig rig, out error);
                if (after == null)
                {
                    LastWeightFixError = error;
                    return null;
                }
                preview.After = after;
                preview.Rig = rig;
                // 候補で調べ直した結果も、同じ直す範囲のポーズで比べる
                preview.Effect = before.WeightPlan.MeasureEffect(before.FixSummary, after.FixSummary, PoseEscapeOptions.DefaultMaxPush);
            }
            finally
            {
                if (ShowProgress) EditorUtility.ClearProgressBar();
            }
            preview.Milliseconds = timer.Elapsed.TotalMilliseconds;
            _weightPreview = preview;
            _weightShapeSession = null;
            ClothEditLog.Info($"[GP02] {_session.DescribeTargets()}: ウェイトを体に寄せる（確認）: {preview}");
            Notify();
            return preview;
        }

        // 今の結果から、レンダラーごとの候補のウェイトを作る（Core の WeightTransfer）。strengthOverride があれば、設定の強さの代わりに使う
        WeightFixPreview BuildWeightCandidates(PoseCheckResult before, float? strengthOverride, out string error)
        {
            error = null;
            PoseRig rig = _poseRig;
            if (rig == null || rig.IsDisposed || before.Body == null || before.Anchors == null || before.WeightPlan == null || before.RestWelded == null)
            {
                error = L10n.Tr("もう一度、動作チェックをしてください。", "Run the motion check again.");
                return null;
            }
            BoneWeightSets bodyWeights = rig.BuildBodyWeights(before.Body, out Transform[] bodyBones, out int[] bodyParents);
            WeightTransferOptions options = _settings.BuildWeightTransferOptions();
            if (strengthOverride.HasValue && !float.IsNaN(strengthOverride.Value)) options.Strength = Math.Max(0f, Math.Min(1f, strengthOverride.Value));
            ParticleCloth cloth = _session.Cloth;
            float[] influence = WeightTransfer.Spread(before.RestWelded.Length, cloth.Topology.Triangles, before.RestWelded, before.WeightPlan.WeldedSeed, options.Radius);
            var preview = new WeightFixPreview
            {
                Before = before,
                Plan = before.WeightPlan,
                Strength = options.Strength,
                Radius = options.Radius,
                Session = _session,
                Welded = _session.WeldedPositions.ToArray(),
            };
            for (int r = 0; r < _session.RendererCount; r++)
            {
                var skinned = _session.Renderers[r] as SkinnedMeshRenderer;
                Mesh mesh = _session.GetSourceMesh(r);
                BoneWeightSets clothWeights = skinned != null && mesh != null ? ShapeKeyWeights.Read(mesh) : null;
                if (clothWeights == null || clothWeights.Bones.Length == 0)
                {
                    preview.Candidates.Add(null);
                    continue;
                }
                Transform[] rendererBones = skinned.bones;
                Matrix4x4[] bindposes = mesh.bindposes;
                int baseCount = bindposes.Length;
                var baseBones = new Transform[baseCount];
                for (int i = 0; i < baseCount; i++) baseBones[i] = i < rendererBones.Length ? rendererBones[i] : null;

                // 体のボーンごとの、服のボーンの番号（服のボーンに無ければ、足すボーンとして末尾の番号）
                var added = new List<Transform>();
                var direct = new int[bodyBones.Length];
                for (int i = 0; i < bodyBones.Length; i++)
                {
                    Transform clothBone = rig.FindClothBone(r, bodyBones[i]);
                    int index = clothBone != null ? Array.IndexOf(baseBones, clothBone) : -1;
                    if (clothBone != null && index < 0)
                    {
                        index = added.IndexOf(clothBone);
                        if (index < 0)
                        {
                            added.Add(clothBone);
                            index = added.Count - 1;
                        }
                        index += baseCount;
                    }
                    direct[i] = index;
                }
                int[] map = WeightTransfer.ResolveBoneMap(direct, bodyParents);
                options.MaxBonesPerVertex = BoneWeightLimit(skinned, mesh);
                _session.GetVertexRange(r, out int start, out _);
                WeightTransferResult transfer = WeightTransfer.Transfer(clothWeights, cloth.Topology.VertexToWelded, start, cloth.Topology.Triangles, influence,
                    before.Anchors, before.Body, bodyWeights, map, baseCount + added.Count, options, before.RestWelded);
                preview.MissingAnchors += transfer.MissingAnchors;
                preview.Unmapped += transfer.Unmapped;

                // 使われなかった、足すボーンを除いて番号を詰める
                var used = new bool[added.Count];
                BoneWeightSets weights = transfer.Weights;
                for (int k = 0; k < weights.Bones.Length; k++)
                {
                    if (weights.Bones[k] >= baseCount) used[weights.Bones[k] - baseCount] = true;
                }
                var finalIndex = new int[added.Count];
                var finalAdded = new List<Transform>();
                for (int i = 0; i < added.Count; i++)
                {
                    finalIndex[i] = used[i] ? baseCount + finalAdded.Count : -1;
                    if (used[i]) finalAdded.Add(added[i]);
                }
                for (int k = 0; k < weights.Bones.Length; k++)
                {
                    if (weights.Bones[k] >= baseCount) weights.Bones[k] = finalIndex[weights.Bones[k] - baseCount];
                }
                var bones = new Transform[baseCount + finalAdded.Count];
                Array.Copy(baseBones, bones, baseCount);
                var allBindposes = new Matrix4x4[baseCount + finalAdded.Count];
                Array.Copy(bindposes, allBindposes, baseCount);
                for (int i = 0; i < finalAdded.Count; i++)
                {
                    bones[baseCount + i] = finalAdded[i];
                    allBindposes[baseCount + i] = AddedBindpose(rig, skinned, finalAdded[i]);
                }
                preview.Candidates.Add(new WeightFixCandidate
                {
                    Renderer = skinned,
                    Weights = weights,
                    Changed = transfer.Changed,
                    ChangedCount = transfer.ChangedCount,
                    Bones = bones,
                    Bindposes = allBindposes,
                    AddedBones = finalAdded.ToArray(),
                });
            }
            return preview;
        }

        // 1頂点に付けるボーンの数の上限（レンダラーの設定。制限なしなら、Unity の標準の 4 本か、メッシュが今使っている一番多い数）
        static int BoneWeightLimit(SkinnedMeshRenderer skinned, Mesh mesh)
        {
            int limit = ClothEditSession.GetBoneWeightLimit(skinned);
            if (limit == int.MaxValue) limit = Math.Max(WeightTransferOptions.DefaultMaxBonesPerVertex, ShapeKeyWeights.MaxBonesPerVertex(mesh));
            return Math.Max(1, Math.Min(255, limit));
        }

        // 衣装のボーンに足すボーンのバインドポーズ。体がそのボーンを使っていれば体のバインドポーズから（体と同じ姿勢を基準にする）、
        // 無ければ今の姿勢を基準にする
        static Matrix4x4 AddedBindpose(PoseRig rig, SkinnedMeshRenderer renderer, Transform bone)
        {
            if (rig.TryGetBodyBindpose(bone, out Matrix4x4 bodyBindpose, out Transform bodyRenderer))
            {
                return bodyBindpose * bodyRenderer.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            }
            return bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
        }

        /// <summary>
        /// 確認した候補（<see cref="PreviewWeightFix()"/>）を適用する。Unity の Undo の1段。
        /// (a) 衣装のシェイプキーのデータ（無ければ作る）に、ボーンウェイトの変更として記録する（前の変更と合わせる。同じ頂点は新しいほう）。
        /// (b) 衣装のメッシュのウェイトを変える（GP02 が作ったメッシュなら書き換え、そうでなければ複製をアセットにして差し替える）。
        /// 足すボーンは、レンダラーのボーンの末尾に足す。そのあと編集を作り直し、候補で調べ直した結果を今の結果にする。
        /// 保存していない編集があるときは、その形をメッシュ空間の差分で引き継いで作り直す（保存していない編集のまま。Ctrl+Z の1回で、ウェイトと一緒に戻る）。
        /// discardUnsaved が true のときだけ、保存していない形を捨てて作り直す（そのときは、動作チェックの結果が古くなる）。
        /// できなければ null（理由は <see cref="LastWeightFixError"/>）。
        /// </summary>
        public WeightFixReport ApplyWeightFix(bool discardUnsaved = false)
        {
            LastWeightFixError = "";
            if (!EnsureAlive()) return null;
            if (!CanApplyWeightFix)
            {
                LastWeightFixError = _weightPreview == null ? L10n.Tr("先に「確認」で、候補のウェイトを試してください。", "First try the candidate weights with \"Check\".")
                    : L10n.Tr("確認のあとに形が変わったので、もう一度確認してください。", "The shape changed after the check. Check again.");
                return null;
            }
            FinishStroke();
            bool unsaved = HasUnsavedEdits;
            WeightFixPreview preview = _weightPreview;
            _weightPreview = null;
            var report = new WeightFixReport { Preview = preview, ChangedVertices = preview.ChangedVertices };
            string undoName = L10n.Tr("GP02 ウェイトを体に寄せる", "GP02 Fit Weights to Body");

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                for (int r = 0; r < _session.RendererCount; r++)
                {
                    WeightFixCandidate candidate = preview.Candidates[r];
                    if (candidate == null || candidate.ChangedCount == 0) continue;
                    SkinnedMeshRenderer skinned = candidate.Renderer;
                    Mesh current = GetMesh(skinned);
                    // (a) データに記録する。データの元のメッシュを、差し替える前のメッシュにするため、メッシュを差し替える前に行う
                    if (RecordToData)
                    {
                        GP02ShapeKeyData data = ShapeKeyLibrary.FindOrCreateData(skinned, DataFolder);
                        if (data != null && data.VertexCount == current.vertexCount)
                        {
                            if (data.HasWeightEdit) ShapeKeyBackup.Create(data, "WeightEdit");
                            Undo.RecordObject(data, undoName);
                            ShapeKeyWeightEdit fresh = ShapeKeyWeights.CreateEdit(candidate.Weights, candidate.Changed, candidate.Bones, candidate.Bindposes, ShapeKeyExporter.FindAvatarRoot(skinned.transform));
                            data.WeightEdit = ShapeKeyWeights.Merge(data.WeightEdit, fresh);
                            EditorUtility.SetDirty(data);
                            report.RecordedVertices += data.WeightEdit.VertexCount;
                        }
                    }
                    // (b) 今の編集のメッシュ
                    Mesh working = WriteWeights(skinned, current, candidate.Weights, candidate.Bindposes, undoName);
                    report.Meshes.Add(working);
                    Undo.RecordObject(skinned, undoName);
                    skinned.bones = candidate.Bones;
                    EditorUtility.SetDirty(skinned);
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Undo.CollapseUndoOperations(group);
                RevertRecords(group);
                preview.DisposeRig();
                LastWeightFixError = L10n.Tr("ウェイトを寄せられませんでした。Console のエラーを確認してください。\n", "Could not fit the weights. Check the errors in the Console.\n") + ex.Message;
                RebuildSession(!discardUnsaved);
                return null;
            }
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();

            // 作り直す（ピンと選んだ点は、位置と部品で引き継ぐ。保存していない形は、メッシュ空間の差分で引き継ぐ）。
            // 保存していない形を捨てていなければ、候補で調べ直した結果がそのまま今の結果になる
            // （動作チェックは、メッシュ空間の差分を入れた編集のメッシュに候補のウェイトを入れて試しているので、引き継いだ形は適用のあとの状態と同じ）
            PoseCheckResult after = preview.After;
            PoseRig afterRig = preview.DetachRig();
            bool carried = RebuildSession(!discardUnsaved);
            report.CarriedUnsaved = unsaved && carried;
            report.DiscardedUnsaved = unsaved && !carried;
            if (!report.DiscardedUnsaved && _session != null && after != null && afterRig != null && !afterRig.IsDisposed)
            {
                after.Session = _session;
                after.ShapeVersion = _session.ShapeVersion;
                after.UpdateParticleDepth(_session.Cloth);
                InstallPoseCheck(after, afterRig);
                LastPoseEscape = null;
            }
            else
            {
                afterRig?.Dispose();
            }
            LastWeightFix = report;
            ClothEditLog.Info($"[GP02] {(_session != null ? _session.DescribeTargets() : "")}: ウェイトを体に寄せました。{report}");
            Notify();
            return report;
        }

        // レンダラーのメッシュのボーンウェイトを変える。GP02 が作ったメッシュならそのまま書き換え（Undo つき）、そうでなければ複製をアセットにして差し替える
        Mesh WriteWeights(SkinnedMeshRenderer renderer, Mesh current, BoneWeightSets weights, Matrix4x4[] bindposes, string undoName)
        {
            if (ClothEditSession.IsOwnMeshAsset(current, SaveFolder))
            {
                Undo.RegisterCompleteObjectUndo(current, undoName);
                ShapeKeyWeights.Write(current, weights, bindposes);
                EditorUtility.SetDirty(current);
                return current;
            }
            Mesh copy = Object.Instantiate(current);
            copy.name = current.name.EndsWith("_GP02") ? current.name : current.name + "_GP02";
            ShapeKeyWeights.Write(copy, weights, bindposes);
            string folder = SaveFolder.TrimEnd('/');
            EnsureAssetFolder(folder);
            string fileName = copy.name;
            foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}.asset"));
            Undo.RegisterCreatedObjectUndo(copy, undoName);
            Undo.RecordObject(renderer, undoName);
            renderer.sharedMesh = copy;
            EditorUtility.SetDirty(renderer);
            return copy;
        }

        static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>
        /// 編集中の衣装のデータから、ボーンウェイトの変更を外し、今の編集のメッシュの、変えた頂点のウェイトを元のメッシュ（データの元のメッシュ）のものに戻す
        /// （足したボーンも外す）。Unity の Undo の1段。そのあと編集を作り直す（動作チェックの結果は古くなる）。
        /// 保存していない編集があるときは、その形を引き継いで作り直す（discardUnsaved が true のときだけ捨てる）。外す変更が無ければ false。
        /// </summary>
        public bool RemoveWeightFix(bool discardUnsaved = false)
        {
            LastWeightFixError = "";
            if (!EnsureAlive()) return false;
            if (RecordedWeightEditVertexCount == 0)
            {
                LastWeightFixError = L10n.Tr("データに記録したウェイトの変更はありません。", "There is no weight change recorded in the data.");
                return false;
            }
            FinishStroke();
            DiscardWeightFixPreview();
            string undoName = L10n.Tr("GP02 ウェイトの変更を外す", "GP02 Remove Weight Change");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                for (int r = 0; r < _session.RendererCount; r++)
                {
                    if (!(_session.Renderers[r] is SkinnedMeshRenderer skinned)) continue;
                    var component = skinned.GetComponent<GP02ShapeKeys>();
                    GP02ShapeKeyData data = component != null ? component.Data : null;
                    if (data == null || !data.HasWeightEdit) continue;
                    ShapeKeyBackup.Create(data, "RemoveWeightEdit");
                    Mesh source = data.SourceMesh;
                    Mesh current = GetMesh(skinned);
                    if (source != null && current != null && current != source && current.vertexCount == source.vertexCount)
                    {
                        Mesh working;
                        if (ClothEditSession.IsOwnMeshAsset(current, SaveFolder))
                        {
                            Undo.RegisterCompleteObjectUndo(current, undoName);
                            working = current;
                        }
                        else
                        {
                            working = Object.Instantiate(current);
                            working.name = current.name.EndsWith("_GP02") ? current.name : current.name + "_GP02";
                            string folder = SaveFolder.TrimEnd('/');
                            EnsureAssetFolder(folder);
                            AssetDatabase.CreateAsset(working, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{working.name}.asset"));
                            Undo.RegisterCreatedObjectUndo(working, undoName);
                        }
                        Transform[] bones = ShapeKeyWeights.Restore(working, source, data.WeightEdit, skinned);
                        EditorUtility.SetDirty(working);
                        Undo.RecordObject(skinned, undoName);
                        skinned.sharedMesh = working;
                        skinned.bones = bones;
                        EditorUtility.SetDirty(skinned);
                    }
                    else if (current == source)
                    {
                        Transform[] fitted = ShapeKeyWeights.FitBones(skinned, source);
                        if (fitted != null)
                        {
                            Undo.RecordObject(skinned, undoName);
                            skinned.bones = fitted;
                        }
                    }
                    Undo.RecordObject(data, undoName);
                    data.WeightEdit = new ShapeKeyWeightEdit();
                    EditorUtility.SetDirty(data);
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Undo.CollapseUndoOperations(group);
                RevertRecords(group);
                LastWeightFixError = L10n.Tr("ウェイトの変更を外せませんでした。Console のエラーを確認してください。\n", "Could not remove the weight change. Check the errors in the Console.\n") + ex.Message;
                RebuildSession(!discardUnsaved);
                return false;
            }
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
            LastWeightFix = null;
            RebuildSession(!discardUnsaved);
            ClothEditLog.Info($"[GP02] {(_session != null ? _session.DescribeTargets() : "")}: データからウェイトの変更を外しました。");
            Notify();
            return true;
        }
    }
}
