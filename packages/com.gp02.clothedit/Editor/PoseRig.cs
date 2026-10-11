using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GP02.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// 動作チェック用の、アバターの複製（見えない・保存しない）。複製に Humanoid のポーズを付け、体と服をポーズの形で取り出す（BakeMesh）。
    /// シーンのアバターは触らない（複製を見せている間だけ、元のアバターのレンダラーを隠す。隠したものは必ず戻す）。
    /// <para>
    /// 複製は、見えない入れ物（非アクティブ）の下に作る。入れ物が非アクティブなので、複製のスクリプトは目を覚まさない。
    /// そのあと Transform・レンダラー・MeshFilter 以外のコンポーネント（Animator・物理・各種スクリプト）を外す。
    /// Humanoid に入っていない服のボーン（服だけの別のアーマチュア）は、アバターのボーンに名前（前置き・後置きを除く）と位置で対応付け
    /// （<see cref="BoneNameMatch"/>）、対応先のボーンの動きをうつす。対応が無いボーンは親に付いていく。
    /// Humanoid のボーンの下にあるボーン（揺れもの用など）は、親と一緒に動く。
    /// </para>
    /// <para>
    /// 複製は、ドメインの再読み込み・プレイモード・シーンの切り替え・エディタの終了の前に必ず消す。消し損ねても、読み込みのあとに名前で探して消し、
    /// 隠したままのレンダラーを戻す（記録は SessionState）。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public sealed class PoseRig : IDisposable
    {
        /// <summary>複製を入れる、見えない入れ物の名前。</summary>
        public const string ContainerName = "GP02_PoseCheck";

        const string HiddenRecordKey = "GP02.PoseRig.Hidden";

        // 体として使うレンダラーの、Humanoid のボーンへのウェイトの数の、一番多いものに対する割合の下限
        const float BodyCoverageRatio = 0.6f;
        const int MinBodyCoverage = 3;

        static readonly List<PoseRig> Live = new List<PoseRig>();
        static readonly Type[] KeptTypes = { typeof(Transform), typeof(SkinnedMeshRenderer), typeof(MeshRenderer), typeof(MeshFilter) };

        static PoseRig()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode) DisposeAll();
            };
            EditorSceneManager.sceneClosing += (scene, removing) => DisposeAll();
            EditorSceneManager.sceneOpening += (path, mode) => DisposeAll();
            EditorSceneManager.newSceneCreated += (scene, setup, mode) => DisposeAll();
            EditorApplication.delayCall += () => CleanupLeftovers();
        }

        /// <summary>今ある複製の数（テスト用）。</summary>
        public static int LiveCount => Live.Count;

        /// <summary>すべての複製を消す（隠したレンダラーも戻す）。</summary>
        public static void DisposeAll()
        {
            foreach (PoseRig rig in Live.ToArray()) rig.Dispose();
        }

        /// <summary>
        /// 消し損ねた複製（入れ物）を探して消し、隠したままのレンダラーを戻す。読み込みのあとに自動で呼ばれる。消した入れ物の数を返す。
        /// </summary>
        public static int CleanupLeftovers()
        {
            int removed = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.name != ContainerName || (go.hideFlags & HideFlags.DontSave) == 0) continue;
                bool owned = false;
                foreach (PoseRig rig in Live) owned |= rig._container == go;
                if (owned) continue;
                Object.DestroyImmediate(go);
                removed++;
            }
            if (Live.Count == 0) RestoreRecordedRenderers();
            return removed;
        }

        // 記録に残っている、隠したままのレンダラーを戻す
        static void RestoreRecordedRenderers()
        {
            string text;
            try { text = SessionState.GetString(HiddenRecordKey, ""); }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(text)) return;
            SessionState.EraseString(HiddenRecordKey);
            foreach (string id in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int instance)) continue;
                if (EditorUtility.InstanceIDToObject(instance) is Renderer renderer) renderer.forceRenderingOff = false;
            }
        }

        readonly GameObject _container;
        readonly Transform _clone;
        readonly Transform _originalRoot;
        readonly Transform[] _cloneTransforms;
        readonly Vector3[] _baseLocalPosition;
        readonly Quaternion[] _baseLocalRotation;
        readonly Vector3[] _baseLocalScale;
        readonly HumanPoseHandler _handler;
        readonly float[] _baseMuscles;
        readonly Vector3 _bodyPosition;
        readonly Quaternion _bodyRotation;
        readonly Transform _hips;
        readonly Transform[] _feet;            // 足首とつま先（ポーズで浮いた足を床に戻すため）
        Vector3 _hipsAnchor;
        float _floor;
        bool _hasAnchor;

        // 服の別のアーマチュアのボーンと、動きをうつすアバターのボーン（それぞれ元の姿勢でのワールドの位置と向き）
        readonly Transform[] _followers;
        readonly Transform[] _followTargets;
        readonly Vector3[] _followerBasePosition, _targetBasePosition;
        readonly Quaternion[] _followerBaseRotation, _targetBaseRotation;

        readonly Renderer[] _targets;          // 編集中の衣装の複製（Session のレンダラーの順）
        readonly Mesh[] _targetMeshes;         // 衣装の複製に付けた一時メッシュ（このクラスが消す）
        readonly SkinnedMeshRenderer[] _probes; // 試しの形を取り出すための、衣装の複製と同じボーンのレンダラー（非アクティブ・見せない）
        readonly Mesh[] _probeMeshes;          // 試しの形の一時メッシュ（このクラスが消す）
        readonly Renderer[] _body;             // 体の複製
        readonly int[] _bodyStarts;            // 体の頂点の並びでの、レンダラーごとの先頭
        readonly int[] _bodyTriangles;
        readonly List<(Renderer Renderer, bool Previous)> _hidden = new List<(Renderer, bool)>();
        readonly List<Vector3> _vertexWork = new List<Vector3>();
        Mesh _bakeWork;
        bool _shown;
        bool _disposed;

        /// <summary>複製したアバターのルート（シーンのもの）。</summary>
        public Transform OriginalRoot => _originalRoot;

        /// <summary>複製のルート（見えない・保存しない）。</summary>
        public Transform CloneRoot => _clone;

        /// <summary>基準（複製したときの姿勢）の筋肉の値。書き換えないこと。</summary>
        public float[] BaseMuscles => _baseMuscles;

        /// <summary>体の頂点の数（<see cref="BakeBody"/> に渡す配列の長さ）。</summary>
        public int BodyVertexCount { get; }

        /// <summary>体の三角形（<see cref="BakeBody"/> の頂点の番号）。</summary>
        public int[] BodyTriangles => _bodyTriangles;

        /// <summary>体らしいレンダラー（Humanoid のボーンに広くウェイトがあるもの）が見つからず、ほかのレンダラーをすべて体の候補にしたか。</summary>
        public bool BodyIsFallback { get; }

        /// <summary>体として使ったレンダラーの説明（ログ・画面用）。</summary>
        public string BodyDescription { get; }

        /// <summary>服の別のアーマチュアのボーンのうち、アバターのボーンに対応付けた数と、対応しなかった（親に付いていく）スキニングのボーンの数。</summary>
        public int FollowerCount => _followers.Length;
        public int UnmatchedBoneCount { get; }

        /// <summary>複製を見せているか。</summary>
        public bool IsShown => _shown;

        /// <summary>消したか。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 動作チェックが使えない理由。使えれば null。衣装の親に、Humanoid の Avatar が付いた Animator が必要。
        /// </summary>
        public static string GetProblem(Renderer target)
        {
            if (target == null) return L10n.Tr("編集中の衣装がありません。", "There is no outfit being edited.");
            Transform root = ClothEditSymmetry.FindAvatarRoot(target.transform);
            Animator animator = root != null ? root.GetComponent<Animator>() : null;
            if (animator == null)
            {
                return L10n.Tr("衣装の親に Animator が見つかりません。動作チェックは、Animator の付いた Humanoid のアバターの中にある衣装で使えます（衣装をアバターの下に置いてください）。",
                    "No Animator was found above the outfit. The motion check works for outfits inside a Humanoid avatar with an Animator (put the outfit under the avatar).");
            }
            if (animator.avatar == null)
            {
                return L10n.Tr($"「{root.name}」の Animator に Avatar が設定されていません。Humanoid の Avatar を設定すると、動作チェックが使えます。",
                    $"The Animator of \"{root.name}\" has no Avatar. Set a Humanoid Avatar to use the motion check.");
            }
            if (!animator.avatar.isValid || !animator.avatar.isHuman)
            {
                return L10n.Tr($"「{root.name}」の Avatar が Humanoid ではありません。動作チェックは Humanoid のポーズで試すので、モデルの Import Settings の Rig で Animation Type を Humanoid にしてください。",
                    $"The Avatar of \"{root.name}\" is not Humanoid. The motion check tries Humanoid poses, so set Animation Type to Humanoid in the model's Import Settings (Rig).");
            }
            return null;
        }

        /// <summary>
        /// targets（編集中の衣装。Session のレンダラーの順）が入っているアバターを複製する。作れなければ null で、理由を error に入れる。
        /// </summary>
        public static PoseRig Create(IReadOnlyList<Renderer> targets, out string error)
        {
            error = targets != null && targets.Count > 0 ? GetProblem(targets[0]) : GetProblem(null);
            if (error != null) return null;
            Transform root = ClothEditSymmetry.FindAvatarRoot(targets[0].transform);
            foreach (Renderer target in targets)
            {
                if (target == null || ClothEditSymmetry.FindAvatarRoot(target.transform) != root)
                {
                    error = L10n.Tr("まとめて編集している衣装が、別々のアバターに入っています。動作チェックは1つのアバターで行います。",
                        "The outfits edited together are in different avatars. The motion check works on one avatar.");
                    return null;
                }
            }
            var container = new GameObject(ContainerName) { hideFlags = HideFlags.HideAndDontSave };
            container.SetActive(false);
            try
            {
                var rig = new PoseRig(container, root, targets);
                Live.Add(rig);
                return rig;
            }
            catch (Exception ex)
            {
                Object.DestroyImmediate(container);
                Debug.LogException(ex);
                error = L10n.Tr("動作チェックの準備ができませんでした（アバターの複製に失敗しました）。Console のエラーを確認してください。\n",
                    "Could not prepare the motion check (failed to copy the avatar). Check the errors in the Console.\n") + ex.Message;
                return null;
            }
        }

        PoseRig(GameObject container, Transform root, IReadOnlyList<Renderer> targets)
        {
            _container = container;
            _originalRoot = root;
            Animator animator = root.GetComponent<Animator>();

            // 非アクティブな入れ物の下に作るので、複製のスクリプトは目を覚まさない（OnEnable も Awake も呼ばれない）
            GameObject clone = Object.Instantiate(root.gameObject, container.transform, true);
            clone.name = root.name + " (GP02)";
            StripComponents(clone);
            clone.SetActive(true);
            _clone = clone.transform;
            SetHideFlags(clone);

            Transform[] originals = root.GetComponentsInChildren<Transform>(true);
            _cloneTransforms = clone.GetComponentsInChildren<Transform>(true);
            if (originals.Length != _cloneTransforms.Length) throw new InvalidOperationException("複製の階層が元と一致しません。");
            var indexOf = new Dictionary<Transform, int>();
            for (int i = 0; i < originals.Length; i++) indexOf[originals[i]] = i;

            int count = _cloneTransforms.Length;
            _baseLocalPosition = new Vector3[count];
            _baseLocalRotation = new Quaternion[count];
            _baseLocalScale = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Transform t = _cloneTransforms[i];
                _baseLocalPosition[i] = t.localPosition;
                _baseLocalRotation[i] = t.localRotation;
                _baseLocalScale[i] = t.localScale;
            }

            // Humanoid のボーン（元の Animator が結びつけているもの。Rebind は姿勢を戻してしまうので呼ばない）
            var human = new HashSet<Transform>();
            var originalHuman = new HashSet<Transform>();
            var feet = new List<Transform>();
            Transform hips = null;
            for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
            {
                Transform bone = animator.GetBoneTransform((HumanBodyBones)b) ?? FindHumanBoneByName(root, animator.avatar, (HumanBodyBones)b);
                if (bone == null || !indexOf.TryGetValue(bone, out int index)) continue;
                originalHuman.Add(bone);
                human.Add(_cloneTransforms[index]);
                if (b == (int)HumanBodyBones.Hips) hips = _cloneTransforms[index];
                if (b == (int)HumanBodyBones.LeftFoot || b == (int)HumanBodyBones.RightFoot || b == (int)HumanBodyBones.LeftToes || b == (int)HumanBodyBones.RightToes)
                {
                    feet.Add(_cloneTransforms[index]);
                }
            }
            _hips = hips ?? throw new InvalidOperationException("Humanoid の Hips が見つかりません。");
            _feet = feet.ToArray();

            _handler = new HumanPoseHandler(animator.avatar, _clone);
            var pose = new HumanPose();
            _handler.GetHumanPose(ref pose);
            _baseMuscles = (float[])pose.muscles.Clone();
            _bodyPosition = pose.bodyPosition;
            _bodyRotation = pose.bodyRotation;

            MatchFollowers(human, out _followers, out _followTargets, out int unmatched);
            UnmatchedBoneCount = unmatched;
            int followers = _followers.Length;
            _followerBasePosition = new Vector3[followers];
            _followerBaseRotation = new Quaternion[followers];
            _targetBasePosition = new Vector3[followers];
            _targetBaseRotation = new Quaternion[followers];
            for (int k = 0; k < followers; k++)
            {
                _followerBasePosition[k] = _followers[k].position;
                _followerBaseRotation[k] = _followers[k].rotation;
                _targetBasePosition[k] = _followTargets[k].position;
                _targetBaseRotation[k] = _followTargets[k].rotation;
            }

            _targets = new Renderer[targets.Count];
            _targetMeshes = new Mesh[targets.Count];
            _probes = new SkinnedMeshRenderer[targets.Count];
            _probeMeshes = new Mesh[targets.Count];
            for (int k = 0; k < targets.Count; k++)
            {
                if (!indexOf.TryGetValue(targets[k].transform, out int index)) throw new InvalidOperationException("衣装がアバターの中にありません。");
                _targets[k] = _cloneTransforms[index].GetComponent<Renderer>();
            }

            List<Renderer> body = SelectBody(root, targets, originalHuman, out bool fallback);
            BodyIsFallback = fallback;
            _body = new Renderer[body.Count];
            _bodyStarts = new int[body.Count];
            var triangles = new List<int>();
            var names = new StringBuilder();
            int vertexCount = 0;
            for (int k = 0; k < body.Count; k++)
            {
                _body[k] = _cloneTransforms[indexOf[body[k].transform]].GetComponent<Renderer>();
                Mesh mesh = ClothEditController.GetMesh(_body[k]);
                _bodyStarts[k] = vertexCount;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != UnityEngine.MeshTopology.Triangles) continue;
                    foreach (int t in mesh.GetTriangles(sub)) triangles.Add(vertexCount + t);
                }
                vertexCount += mesh.vertexCount;
                if (names.Length > 0) names.Append("、");
                names.Append(body[k].name);
            }
            _bodyTriangles = triangles.ToArray();
            BodyVertexCount = vertexCount;
            BodyDescription = body.Count == 0
                ? L10n.Tr("体が見つかりません（同じアバターに、衣装のほかのレンダラーがありません）", "No body was found (the avatar has no other renderers)")
                : fallback
                    ? L10n.Tr($"体らしいレンダラー（Humanoid のボーンに広くウェイトがあるもの）が無いので、ほかのレンダラー {body.Count} 個のうち布の内側にある部品を使います: {names}",
                        $"No body-like renderer (one weighted to many Humanoid bones) was found, so the parts inside the cloth from {body.Count} other renderers are used: {names}")
                    : L10n.Tr($"体: {names}（三角形 {_bodyTriangles.Length / 3:N0}）", $"Body: {names} ({_bodyTriangles.Length / 3:N0} triangles)");

            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                renderer.forceRenderingOff = false;
                // ポーズで大きく動くので、表示の境界は毎回計算する（複製だけの設定）
                if (renderer is SkinnedMeshRenderer skinned) skinned.updateWhenOffscreen = true;
            }
            ApplyBase();
        }

        static Transform FindHumanBoneByName(Transform root, Avatar avatar, HumanBodyBones bone)
        {
            string humanName = HumanTrait.BoneName[(int)bone];
            foreach (HumanBone humanBone in avatar.humanDescription.human)
            {
                if (humanBone.humanName != humanName) continue;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == humanBone.boneName) return t;
                }
            }
            return null;
        }

        // Transform・レンダラー・MeshFilter 以外を外す。ほかのコンポーネントに必要とされているものは、後の回で外す
        static void StripComponents(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            for (int pass = 0; pass < 10; pass++)
            {
                bool removed = false;
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || IsKept(component) || IsRequiredByOthers(component)) continue;
                    Object.DestroyImmediate(component);
                    removed = true;
                }
                if (!removed) break;
            }
            // 互いに必要としていて外せなかったものは、止めておく
            foreach (Behaviour behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour != null && !IsKept(behaviour)) behaviour.enabled = false;
            }
        }

        static bool IsKept(Component component)
        {
            foreach (Type type in KeptTypes)
            {
                if (type.IsInstanceOfType(component)) return true;
            }
            return false;
        }

        static bool IsRequiredByOthers(Component component)
        {
            Type type = component.GetType();
            foreach (Component other in component.GetComponents<Component>())
            {
                if (other == null || other == component) continue;
                foreach (Attribute attribute in Attribute.GetCustomAttributes(other.GetType(), typeof(RequireComponent), true))
                {
                    var require = (RequireComponent)attribute;
                    if (Requires(require.m_Type0, type) || Requires(require.m_Type1, type) || Requires(require.m_Type2, type)) return true;
                }
            }
            return false;
        }

        static bool Requires(Type required, Type type) => required != null && required.IsAssignableFrom(type);

        static void SetHideFlags(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
                foreach (Component component in t.GetComponents<Component>())
                {
                    if (component != null) component.hideFlags = HideFlags.HideAndDontSave;
                }
            }
        }

        // 服の別のアーマチュアのボーン（Humanoid のボーンの下でも、その祖先でもない、スキニングに使われているボーンとその親）を、アバターのボーンに対応付ける
        void MatchFollowers(HashSet<Transform> human, out Transform[] followers, out Transform[] targets, out int unmatched)
        {
            var underHuman = new Dictionary<Transform, bool>();
            bool IsUnderHuman(Transform t)
            {
                if (t == null || t == _clone) return false;
                if (underHuman.TryGetValue(t, out bool cached)) return cached;
                bool result = human.Contains(t) || IsUnderHuman(t.parent);
                underHuman[t] = result;
                return result;
            }
            var humanAncestors = new HashSet<Transform>();
            foreach (Transform bone in human)
            {
                for (Transform t = bone.parent; t != null && t != _clone; t = t.parent) humanAncestors.Add(t);
            }

            var candidates = new HashSet<Transform>();
            var skinnedBones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer skinned in _clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (Transform bone in skinned.bones)
                {
                    if (bone != null) skinnedBones.Add(bone);
                    for (Transform t = bone; t != null && t != _clone && !humanAncestors.Contains(t) && !IsUnderHuman(t); t = t.parent)
                    {
                        if (!candidates.Add(t)) break;
                    }
                }
            }

            // 並びは階層の順（親が先）
            var clothList = new List<Transform>();
            var avatarList = new List<Transform>();
            foreach (Transform t in _cloneTransforms)
            {
                if (candidates.Contains(t)) clothList.Add(t);
                else if (IsUnderHuman(t)) avatarList.Add(t);
            }
            var clothIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < clothList.Count; i++) clothIndex[clothList[i]] = i;
            var avatarIndex = new Dictionary<Transform, int>();
            for (int j = 0; j < avatarList.Count; j++) avatarIndex[avatarList[j]] = j;

            var clothNames = new string[clothList.Count];
            var clothParents = new int[clothList.Count];
            var clothPositions = new NVector3[clothList.Count];
            for (int i = 0; i < clothList.Count; i++)
            {
                Transform t = clothList[i];
                clothNames[i] = t.name;
                clothParents[i] = t.parent != null && clothIndex.TryGetValue(t.parent, out int p) ? p : -1;
                clothPositions[i] = ToNumerics(t.position);
            }
            var avatarNames = new string[avatarList.Count];
            var avatarParents = new int[avatarList.Count];
            var avatarPositions = new NVector3[avatarList.Count];
            for (int j = 0; j < avatarList.Count; j++)
            {
                Transform t = avatarList[j];
                avatarNames[j] = t.name;
                avatarParents[j] = t.parent != null && avatarIndex.TryGetValue(t.parent, out int p) ? p : -1;
                avatarPositions[j] = ToNumerics(t.position);
            }

            int[] match = BoneNameMatch.Match(clothNames, clothParents, clothPositions, avatarNames, avatarParents, avatarPositions);
            var followerList = new List<Transform>();
            var targetList = new List<Transform>();
            unmatched = 0;
            for (int i = 0; i < match.Length; i++)
            {
                if (match[i] < 0)
                {
                    // 入れ物（服のオブジェクトやアーマチュアの根元）は、対応しなくてよい
                    if (skinnedBones.Contains(clothList[i])) unmatched++;
                    continue;
                }
                followerList.Add(clothList[i]);
                targetList.Add(avatarList[match[i]]);
            }
            followers = followerList.ToArray();
            targets = targetList.ToArray();
        }

        static NVector3 ToNumerics(Vector3 v) => new NVector3(v.x, v.y, v.z);

        // 体として使うレンダラー: Humanoid のボーンに広くウェイトがあるもの（服は一部のボーンだけ、または服だけのアーマチュアのボーンにウェイトがある）
        static List<Renderer> SelectBody(Transform root, IReadOnlyList<Renderer> targets, HashSet<Transform> human, out bool fallback)
        {
            var candidates = new List<Renderer>();
            var coverage = new List<int>();
            int best = 0;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                bool isTarget = false;
                foreach (Renderer target in targets) isTarget |= target == renderer;
                if (isTarget) continue;
                Mesh mesh = ClothEditController.GetMesh(renderer);
                if (mesh == null || mesh.vertexCount == 0) continue;
                int covered = 0;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    Transform[] bones = skinned.bones;
                    var used = new bool[bones.Length];
                    var weights = mesh.GetAllBoneWeights();
                    for (int k = 0; k < weights.Length; k++)
                    {
                        int b = weights[k].boneIndex;
                        if (weights[k].weight > 0f && (uint)b < (uint)used.Length) used[b] = true;
                    }
                    var seen = new HashSet<Transform>();
                    for (int b = 0; b < bones.Length; b++)
                    {
                        if (used[b] && bones[b] != null && human.Contains(bones[b]) && seen.Add(bones[b])) covered++;
                    }
                }
                candidates.Add(renderer);
                coverage.Add(covered);
                best = Math.Max(best, covered);
            }
            var body = new List<Renderer>();
            float threshold = Math.Max(MinBodyCoverage, BodyCoverageRatio * best);
            for (int k = 0; k < candidates.Count; k++)
            {
                if (coverage[k] >= threshold) body.Add(candidates[k]);
            }
            fallback = body.Count == 0;
            return fallback ? candidates : body;
        }

        // ---- ポーズ ----

        /// <summary>複製したときの姿勢に戻す（Humanoid の筋肉の値を通して付け直すので、ポーズを付けたときと同じ計算になる）。</summary>
        public void ApplyBase()
        {
            ApplyMuscles(_baseMuscles);
        }

        /// <summary>
        /// 筋肉の値（長さは <see cref="HumanTrait.MuscleCount"/>）でポーズを付ける。腰の位置は基準のまま（重心を保つために腰がずれた分は戻す）にしてから、
        /// 一番低い足が基準の床の高さになるように、全体を上下に動かす（座る・しゃがむで足が宙に浮かないように。全体を動かすだけなので、貫通の結果は変わらない）。
        /// 服の別のアーマチュアのボーンは、対応先のアバターのボーンの動きをうつす。
        /// </summary>
        public void ApplyMuscles(float[] muscles)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PoseRig));
            if (muscles == null || muscles.Length != _baseMuscles.Length) throw new ArgumentException("筋肉の値の数が違います。", nameof(muscles));
            for (int i = 0; i < _cloneTransforms.Length; i++)
            {
                Transform t = _cloneTransforms[i];
                t.localPosition = _baseLocalPosition[i];
                t.localRotation = _baseLocalRotation[i];
                t.localScale = _baseLocalScale[i];
            }
            var pose = new HumanPose { bodyPosition = _bodyPosition, bodyRotation = _bodyRotation, muscles = (float[])muscles.Clone() };
            _handler.SetHumanPose(ref pose);
            Vector3 up = _clone.up;
            if (!_hasAnchor)
            {
                _hipsAnchor = _hips.position;
                _floor = LowestFoot(up);
                _hasAnchor = true;
            }
            else
            {
                _hips.position = _hipsAnchor;
                float lowest = LowestFoot(up);
                if (!float.IsInfinity(lowest)) _hips.position += up * (_floor - lowest);
            }
            for (int k = 0; k < _followers.Length; k++)
            {
                Transform target = _followTargets[k];
                Quaternion delta = target.rotation * Quaternion.Inverse(_targetBaseRotation[k]);
                Vector3 position = target.position + delta * (_followerBasePosition[k] - _targetBasePosition[k]);
                _followers[k].SetPositionAndRotation(position, delta * _followerBaseRotation[k]);
            }
        }

        // 足（足首・つま先）のうち一番低いものの、up の向きの高さ。足が無ければ無限大
        float LowestFoot(Vector3 up)
        {
            float lowest = float.PositiveInfinity;
            foreach (Transform foot in _feet) lowest = Math.Min(lowest, Vector3.Dot(foot.position, up));
            return lowest;
        }

        /// <summary>複製の Transform（元のアバターと同じ並び）。テスト用。</summary>
        public Transform GetCloneTransform(Transform original)
        {
            Transform[] originals = _originalRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < originals.Length && i < _cloneTransforms.Length; i++)
            {
                if (originals[i] == original) return _cloneTransforms[i];
            }
            return null;
        }

        // ---- 形の取り出し ----

        /// <summary>k 番目の衣装の複製に、メッシュ（編集中の形を入れた一時メッシュ）を付ける。メッシュはこのクラスが消す。</summary>
        public void SetTargetMesh(int k, Mesh mesh)
        {
            if (_targetMeshes[k] != null && _targetMeshes[k] != mesh) Object.DestroyImmediate(_targetMeshes[k]);
            _targetMeshes[k] = mesh;
            Renderer renderer = _targets[k];
            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else if (renderer != null)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = mesh;
            }
        }

        /// <summary>
        /// k 番目の衣装の「試しの形」（編集の形から、少し外へ動かした一時メッシュ）を付ける。メッシュはこのクラスが消す。
        /// 衣装の複製と同じボーン・同じシェイプキーの重みの、見せないレンダラーに付けるので、<see cref="BakeProbes"/> で同じポーズの形を取り出せる
        /// （編集の姿勢で外へ動かした分が、ポーズでどれだけ残るかを測る。<see cref="PoseCheckEvaluator.MeasureGains"/>）。
        /// </summary>
        public void SetProbeMesh(int k, Mesh mesh)
        {
            if (_probeMeshes[k] != null && _probeMeshes[k] != mesh) Object.DestroyImmediate(_probeMeshes[k]);
            _probeMeshes[k] = mesh;
            if (!(_targets[k] is SkinnedMeshRenderer source) || mesh == null) return;
            if (_probes[k] == null)
            {
                // 衣装のオブジェクトを丸ごと複製すると子（ボーンなど）まで増えるので、レンダラーだけを作って同じボーンを付ける
                var go = new GameObject(source.name + " (GP02 probe)") { hideFlags = HideFlags.HideAndDontSave };
                go.SetActive(false);
                Transform t = go.transform;
                t.SetParent(source.transform.parent, false);
                t.localPosition = source.transform.localPosition;
                t.localRotation = source.transform.localRotation;
                t.localScale = source.transform.localScale;
                SkinnedMeshRenderer probe = go.AddComponent<SkinnedMeshRenderer>();
                probe.hideFlags = HideFlags.HideAndDontSave;
                probe.bones = source.bones;
                probe.rootBone = source.rootBone;
                _probes[k] = probe;
            }
            _probes[k].sharedMesh = mesh;
            for (int i = 0; i < mesh.blendShapeCount; i++) _probes[k].SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
        }

        /// <summary>すべての衣装に試しの形を付けたか。</summary>
        public bool HasProbes
        {
            get
            {
                foreach (Mesh mesh in _probeMeshes)
                {
                    if (mesh == null) return false;
                }
                return _probeMeshes.Length > 0;
            }
        }

        /// <summary>試しの形を今のポーズで取り出す（<see cref="BakeTargets"/> と同じ並び）。</summary>
        public void BakeProbes(NVector3[] destination, IReadOnlyList<int> starts)
        {
            for (int k = 0; k < _targets.Length; k++)
            {
                if (_probes[k] != null) Bake(_probes[k], destination, starts[k]);
                else if (_targets[k] != null && _probeMeshes[k] != null) BakeStatic(_probeMeshes[k], _targets[k].transform, destination, starts[k]);
            }
        }

        /// <summary>衣装の数。</summary>
        public int TargetCount => _targets.Length;

        /// <summary>
        /// 衣装の複製を今のポーズで取り出し、ワールド座標の頂点を destination の、レンダラーごとの先頭 starts[k] から入れる。
        /// </summary>
        public void BakeTargets(NVector3[] destination, IReadOnlyList<int> starts)
        {
            for (int k = 0; k < _targets.Length; k++) Bake(_targets[k], destination, starts[k]);
        }

        /// <summary>体の複製を今のポーズで取り出し、ワールド座標の頂点を destination（長さ <see cref="BodyVertexCount"/>）に入れる。</summary>
        public void BakeBody(NVector3[] destination)
        {
            for (int k = 0; k < _body.Length; k++) Bake(_body[k], destination, _bodyStarts[k]);
        }

        void Bake(Renderer renderer, NVector3[] destination, int offset)
        {
            Matrix4x4 toWorld;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                if (_bakeWork == null) _bakeWork = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                // スケールを含めて焼くと、レンダラーの位置と回転だけでワールド座標になる
                skinned.BakeMesh(_bakeWork, true);
                _bakeWork.GetVertices(_vertexWork);
                toWorld = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
            }
            else
            {
                BakeStatic(ClothEditController.GetMesh(renderer), renderer.transform, destination, offset);
                return;
            }
            for (int v = 0; v < _vertexWork.Count; v++)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(_vertexWork[v]);
                destination[offset + v] = new NVector3(w.x, w.y, w.z);
            }
        }

        // スキニングの無いメッシュ: そのまま Transform でワールド座標にする
        void BakeStatic(Mesh mesh, Transform transform, NVector3[] destination, int offset)
        {
            mesh.GetVertices(_vertexWork);
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            for (int v = 0; v < _vertexWork.Count; v++)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(_vertexWork[v]);
                destination[offset + v] = new NVector3(w.x, w.y, w.z);
            }
        }

        // ---- 見せる ----

        /// <summary>複製を見せ、元のアバターのレンダラーを隠す（戻すのは <see cref="Hide"/>）。</summary>
        public void Show()
        {
            if (_disposed || _shown) return;
            _container.SetActive(true);
            foreach (Renderer renderer in _originalRoot.GetComponentsInChildren<Renderer>(true))
            {
                _hidden.Add((renderer, renderer.forceRenderingOff));
                renderer.forceRenderingOff = true;
            }
            _shown = true;
            PersistHidden();
        }

        /// <summary>複製を隠し、元のアバターのレンダラーの表示を戻す。</summary>
        public void Hide()
        {
            if (!_shown) return;
            foreach ((Renderer renderer, bool previous) in _hidden)
            {
                if (renderer != null) renderer.forceRenderingOff = previous;
            }
            _hidden.Clear();
            if (_container != null) _container.SetActive(false);
            _shown = false;
            PersistHidden();
        }

        // 隠したレンダラー（もとは見えていたもの）を記録する。戻し損ねたときに、読み込みのあとで戻すため
        static void PersistHidden()
        {
            try
            {
                var builder = new StringBuilder();
                foreach (PoseRig rig in Live)
                {
                    foreach ((Renderer renderer, bool previous) in rig._hidden)
                    {
                        if (renderer != null && !previous) builder.Append(renderer.GetInstanceID().ToString(CultureInfo.InvariantCulture)).Append(';');
                    }
                }
                SessionState.SetString(HiddenRecordKey, builder.ToString());
            }
            catch (Exception)
            {
                // 記録は救済用なので、書けなくても本体の動きは止めない
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Hide();
            _disposed = true;
            Live.Remove(this);
            PersistHidden();
            _handler?.Dispose();
            foreach (Mesh mesh in _targetMeshes)
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
            foreach (Mesh mesh in _probeMeshes)
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
            if (_bakeWork != null) Object.DestroyImmediate(_bakeWork);
            if (_container != null) Object.DestroyImmediate(_container);
        }
    }
}
