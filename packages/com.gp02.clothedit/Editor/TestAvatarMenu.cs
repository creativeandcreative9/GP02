using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 動作チェックを試すための、Humanoid のテスト用アバター（体・スカート・タイツ・袖）をワンクリックで作るメニュー。
    /// 形とウェイトはすべてプログラムで作る（購入したアバター・衣装のデータは使わない）。長さの単位はメートル。
    /// 体は +Z を向いた T ポーズ（左は -X）。体のひざ・ひじのウェイトの切り替わりは服より急にしてある
    /// （服のウェイトが体よりなめらかだと、曲げたときに体の角が服の外へ出る。改変でよく起きる「ひざ・ひじが突き抜ける」の再現）。
    /// 服は、アバターと同じボーンにスキニングしたもの（既定）と、服だけの別のアーマチュア（Modular Avatar の Merge Armature の形。
    /// ボーンの名前はアバターと同じで、"Outfit" の下にある）にスキニングしたものを作れる。
    /// </summary>
    public static class TestAvatarMenu
    {
        public const string MenuPath = "Tools/GP02/テスト用のアバター（Humanoid）を作る";
        public const string SeparateArmatureMenuPath = "Tools/GP02/テスト用のアバター（Humanoid）を作る（服を別のアーマチュアに）";
        public const string DefaultFolder = "Assets/GP02/TestAvatar";

        /// <summary>スカート・タイツ・袖の SkinnedMeshRenderer の名前。</summary>
        public const string SkirtName = "Skirt";
        public const string TightsName = "Tights";
        public const string SleevesName = "Sleeves";
        public const string BodyName = "Body";

        /// <summary>服だけの別のアーマチュアを作ったときの、服をまとめる子の名前。</summary>
        public const string OutfitName = "Outfit";

        /// <summary>Humanoid に入らない、スカートの根元のボーン（腰の子）。揺れもの用のボーンの代わり。</summary>
        public const string SkirtRootName = "Skirt_Root";

        // 骨の位置（T ポーズ、+Z を向く。左は -X）
        public const float HipsY = 0.86f;
        public const float LegX = 0.085f;
        public const float HipJointY = 0.80f;
        public const float KneeY = 0.43f;
        public const float AnkleY = 0.07f;
        public const float ShoulderY = 1.18f;
        public const float UpperArmX = 0.16f;
        public const float ElbowX = 0.41f;
        public const float WristX = 0.63f;

        // ウェイトの切り替わりの幅（片側）。体のひざ・ひじは急に、服はなめらかに
        const float BodyKneeBlend = 0.02f;
        const float BodyElbowBlend = 0.02f;
        const float ClothKneeBlend = 0.06f;
        const float ClothElbowBlend = 0.06f;

        /// <summary>タイツと袖の、体からの隙間（メートル）。</summary>
        public const float TightGap = 0.003f;

        // スカート（腰で体から約 1cm、太ももの外側で約 7mm 離れる）
        public const float SkirtTopY = 0.96f;
        public const float SkirtBottomY = 0.52f;
        public const float SkirtTopRadius = 0.115f;
        public const float SkirtBottomRadius = 0.25f;

        [MenuItem(MenuPath)]
        static void CreateFromMenu()
        {
            CreateInScene(DefaultFolder, false);
        }

        [MenuItem(SeparateArmatureMenuPath)]
        static void CreateSeparateFromMenu()
        {
            CreateInScene(DefaultFolder, true);
        }

        // ---- シーンに置く ----

        /// <summary>
        /// アバターをシーンに作り、スカートを選択する。Undo で取り消せる。ルートには Humanoid の Avatar を付けた Animator がある。
        /// </summary>
        /// <param name="assetFolder">メッシュ・マテリアル・Avatar を保存するフォルダ。null か空なら保存しない（テスト用）</param>
        /// <param name="separateArmature">服を、服だけの別のアーマチュア（"Outfit/Armature"）にスキニングする</param>
        /// <returns>アバターのルート（子に "Armature"、"Body"、服）</returns>
        public static GameObject CreateInScene(string assetFolder, bool separateArmature = false)
        {
            bool save = !string.IsNullOrEmpty(assetFolder);
            if (save) EnsureFolder(assetFolder);

            var root = new GameObject(separateArmature ? "GP02_TestAvatar_Outfit" : "GP02_TestAvatar");
            Undo.RegisterCreatedObjectUndo(root, "テスト用のアバターを作る");
            Dictionary<string, Transform> bones = CreateArmature(root.transform);

            // Avatar は、服や別のアーマチュアを足す前の骨組みで作る（骨の対応は Animator からの道のりで決まる）
            Avatar avatar = BuildAvatar(root);
            if (save) SaveAsset(avatar, assetFolder, ".asset");
            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;

            Material skin = NewMaterial("TestAvatar_Skin", new Color(0.96f, 0.78f, 0.66f));
            Material skirtMaterial = NewMaterial("TestAvatar_Skirt", new Color(0.30f, 0.35f, 0.75f));
            Material tightsMaterial = NewMaterial("TestAvatar_Tights", new Color(0.18f, 0.18f, 0.22f));
            Material sleeveMaterial = NewMaterial("TestAvatar_Sleeves", new Color(0.95f, 0.95f, 0.92f));

            Mesh bodyMesh = CreateBodyMesh(out string[] bodyBones);
            AddSkinned(root.transform, BodyName, bodyMesh, Resolve(bones, bodyBones), skin);

            Transform clothParent = root.transform;
            Dictionary<string, Transform> clothBones = bones;
            if (separateArmature)
            {
                var outfit = new GameObject(OutfitName);
                outfit.transform.SetParent(root.transform, false);
                clothParent = outfit.transform;
                clothBones = CreateArmature(outfit.transform);
            }

            Mesh skirtMesh = CreateSkirtMesh(out string[] skirtBones);
            Mesh tightsMesh = CreateTightsMesh(out string[] tightsBones);
            Mesh sleevesMesh = CreateSleevesMesh(out string[] sleeveBones);
            AddSkinned(clothParent, SkirtName, skirtMesh, Resolve(clothBones, skirtBones), skirtMaterial);
            AddSkinned(clothParent, TightsName, tightsMesh, Resolve(clothBones, tightsBones), tightsMaterial);
            AddSkinned(clothParent, SleevesName, sleevesMesh, Resolve(clothBones, sleeveBones), sleeveMaterial);

            if (save)
            {
                foreach (UnityEngine.Object asset in new UnityEngine.Object[] { bodyMesh, skirtMesh, tightsMesh, sleevesMesh })
                {
                    SaveAsset(asset, assetFolder, ".asset");
                }
                foreach (Material material in new[] { skin, skirtMaterial, tightsMaterial, sleeveMaterial }) SaveAsset(material, assetFolder, ".mat");
            }

            Transform skirt = clothParent.Find(SkirtName);
            if (skirt != null) Selection.activeGameObject = skirt.gameObject;
            return root;
        }

        static Transform[] Resolve(Dictionary<string, Transform> bones, string[] names)
        {
            var result = new Transform[names.Length];
            for (int i = 0; i < names.Length; i++) result[i] = bones[names[i]];
            return result;
        }

        /// <summary>
        /// 骨組み（"Armature" の下に Humanoid のボーンと "Skirt_Root"）を作る。名前から Transform を引く表を返す。
        /// </summary>
        public static Dictionary<string, Transform> CreateArmature(Transform parent)
        {
            var bones = new Dictionary<string, Transform>();
            Transform armature = AddBone(bones, "Armature", parent, Vector3.zero);
            Transform hips = AddBone(bones, "Hips", armature, new Vector3(0f, HipsY, 0f));
            Transform spine = AddBone(bones, "Spine", hips, new Vector3(0f, 0.95f, 0f));
            Transform chest = AddBone(bones, "Chest", spine, new Vector3(0f, 1.04f, 0f));
            Transform upperChest = AddBone(bones, "UpperChest", chest, new Vector3(0f, 1.12f, 0f));
            Transform neck = AddBone(bones, "Neck", upperChest, new Vector3(0f, 1.22f, 0f));
            AddBone(bones, "Head", neck, new Vector3(0f, 1.29f, 0f));
            foreach (int side in new[] { -1, 1 })
            {
                string s = Side(side);
                Transform shoulder = AddBone(bones, s + "Shoulder", upperChest, new Vector3(side * 0.04f, ShoulderY, 0f));
                Transform upperArm = AddBone(bones, s + "UpperArm", shoulder, new Vector3(side * UpperArmX, ShoulderY, 0f));
                Transform lowerArm = AddBone(bones, s + "LowerArm", upperArm, new Vector3(side * ElbowX, ShoulderY, 0f));
                AddBone(bones, s + "Hand", lowerArm, new Vector3(side * WristX, ShoulderY, 0f));
                Transform upperLeg = AddBone(bones, s + "UpperLeg", hips, new Vector3(side * LegX, HipJointY, 0f));
                Transform lowerLeg = AddBone(bones, s + "LowerLeg", upperLeg, new Vector3(side * LegX, KneeY, 0f));
                Transform foot = AddBone(bones, s + "Foot", lowerLeg, new Vector3(side * LegX, AnkleY, 0f));
                AddBone(bones, s + "Toes", foot, new Vector3(side * LegX, 0.02f, 0.12f));
            }
            AddBone(bones, SkirtRootName, hips, new Vector3(0f, 0.90f, 0f));
            return bones;
        }

        // 左は -X（+Z を向いたアバターの左手側）
        static string Side(int side) => side < 0 ? "Left" : "Right";

        static Transform AddBone(Dictionary<string, Transform> bones, string name, Transform parent, Vector3 worldPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            bones[name] = go.transform;
            return go.transform;
        }

        /// <summary>今の骨組み（root の下のすべての Transform）から、Humanoid の Avatar を作る。</summary>
        static Avatar BuildAvatar(GameObject root)
        {
            var skeleton = new List<SkeletonBone>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            }
            var human = new List<HumanBone>();
            foreach (string humanName in HumanTrait.BoneName)
            {
                string boneName = humanName.Replace(" ", "");
                if (FindDeep(root.transform, boneName) == null) continue;
                var bone = new HumanBone { boneName = boneName, humanName = humanName };
                bone.limit.useDefaultValues = true;
                human.Add(bone);
            }
            var description = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };
            Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.name = "GP02_TestAvatar";
            if (!avatar.isValid || !avatar.isHuman) throw new InvalidOperationException("テスト用のアバターの Humanoid の Avatar を作れませんでした。");
            return avatar;
        }

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        static GameObject AddSkinned(Transform parent, string name, Mesh mesh, Transform[] bones, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            var bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) bindposes[i] = bones[i].worldToLocalMatrix * go.transform.localToWorldMatrix;
            mesh.bindposes = bindposes;
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[0];
            renderer.sharedMaterial = material;
            renderer.updateWhenOffscreen = true;
            return go;
        }

        static Material NewMaterial(string name, Color color)
        {
            // Standard が無い環境（URP だけのプロジェクトなど）でもエラーにならないよう、順にさがす
            Shader shader = Shader.Find("Standard")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Sprites/Default");
            return new Material(shader) { name = name, color = color };
        }

        static void SaveAsset(UnityEngine.Object asset, string folder, string extension)
        {
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{asset.name}{extension}");
            AssetDatabase.CreateAsset(asset, path);
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = folder.Substring(0, folder.LastIndexOf('/'));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(folder.LastIndexOf('/') + 1));
        }

        // ---- 形とウェイト（シーンに触らない） ----

        /// <summary>
        /// 体のメッシュ（胴体・頭・腕・脚・足。部品ごとに別の島で、重なっている）。boneNames に、bones の並びの名前を返す。
        /// </summary>
        public static Mesh CreateBodyMesh(out string[] boneNames)
        {
            var builder = new TubeBuilder();

            // 胴体（下から上へ）。腰の下は丸めて閉じ、脚の付け根は胴体の中から始まる。
            // 腰より下は、脚の付け根の側へ一部（最大 60%）脚に引かれる（実際のアバターと同じく、脚を上げたときに股が脚と一緒に動くように）
            var torso = new List<Vector2>
            {
                new Vector2(0.765f, 0f), new Vector2(0.775f, 0.08f), new Vector2(0.80f, 0.115f), new Vector2(0.84f, 0.128f),
                new Vector2(0.90f, 0.115f), new Vector2(0.95f, 0.105f), new Vector2(0.99f, 0.108f), new Vector2(1.05f, 0.12f),
                new Vector2(1.12f, 0.125f), new Vector2(1.17f, 0.125f), new Vector2(1.19f, 0.10f), new Vector2(1.21f, 0.06f),
                new Vector2(1.24f, 0.045f), new Vector2(1.29f, 0.035f), new Vector2(1.30f, 0f),
            };
            builder.Add(Vector3.zero, Vector3.up, Vector3.right, Resample(torso, 0.01f), 48, p =>
            {
                if (p.y >= HipsY)
                {
                    return Chain(p.y, new[] { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" },
                        new[] { 0.95f, 1.04f, 1.12f, 1.22f, 1.29f }, new[] { 0.04f, 0.04f, 0.04f, 0.02f, 0.02f });
                }
                float toLegs = 0.6f * Mathf.SmoothStep(0f, 1f, (HipsY - p.y) / 0.10f);
                float left = Mathf.SmoothStep(0f, 1f, 0.5f - p.x / 0.08f);
                return Weights(("Hips", 1f - toLegs), ("LeftUpperLeg", toLegs * left), ("RightUpperLeg", toLegs * (1f - left)));
            });

            var head = new List<Vector2>
            {
                new Vector2(1.285f, 0f), new Vector2(1.29f, 0.04f), new Vector2(1.31f, 0.065f), new Vector2(1.35f, 0.09f),
                new Vector2(1.40f, 0.095f), new Vector2(1.45f, 0.08f), new Vector2(1.475f, 0.04f), new Vector2(1.48f, 0f),
            };
            builder.Add(Vector3.zero, Vector3.up, Vector3.right, Resample(head, 0.015f), 24, p => Single("Head"));

            foreach (int side in new[] { -1, 1 })
            {
                string s = Side(side);
                // 脚（上から下へ。軸は -Y）
                builder.Add(new Vector3(side * LegX, 0f, 0f), Vector3.down, Vector3.right, Resample(LegProfile(), 0.01f), 32, p =>
                    Chain(-p.y, new[] { "Hips", s + "UpperLeg", s + "LowerLeg", s + "Foot" },
                        new[] { -HipJointY, -KneeY, -AnkleY }, new[] { 0.04f, BodyKneeBlend, 0.025f }));
                // 足（かかとからつま先へ。軸は +Z）
                var foot = new List<Vector2>
                {
                    new Vector2(-0.05f, 0f), new Vector2(-0.045f, 0.03f), new Vector2(0f, 0.036f), new Vector2(0.08f, 0.035f),
                    new Vector2(0.14f, 0.03f), new Vector2(0.17f, 0.02f), new Vector2(0.175f, 0f),
                };
                builder.Add(new Vector3(side * LegX, 0.035f, 0f), Vector3.forward, Vector3.right, Resample(foot, 0.015f), 16, p =>
                    Chain(p.z, new[] { s + "Foot", s + "Toes" }, new[] { 0.12f }, new[] { 0.02f }));
                // 腕（肩から手へ。軸は ±X）
                builder.Add(new Vector3(0f, ShoulderY, 0f), new Vector3(side, 0f, 0f), Vector3.forward, Resample(ArmProfile(), 0.01f), 24, p =>
                    Chain(Mathf.Abs(p.x), new[] { s + "Shoulder", s + "UpperArm", s + "LowerArm", s + "Hand" },
                        new[] { UpperArmX, ElbowX, WristX }, new[] { 0.04f, BodyElbowBlend, 0.025f }));
            }
            return builder.ToMesh("GP02_TestAvatarBody", out boneNames);
        }

        // 脚の太さ。軸は -Y なので、軸に沿った位置は高さの符号を反転したもの（上から下へ昇順）
        static List<Vector2> LegProfile()
        {
            return new List<Vector2>
            {
                new Vector2(-0.86f, 0f), new Vector2(-0.85f, 0.05f), new Vector2(-0.80f, 0.072f), new Vector2(-0.70f, 0.07f),
                new Vector2(-0.58f, 0.06f), new Vector2(-0.48f, 0.05f), new Vector2(-0.43f, 0.048f), new Vector2(-0.38f, 0.05f),
                new Vector2(-0.30f, 0.052f), new Vector2(-0.20f, 0.042f), new Vector2(-0.10f, 0.034f), new Vector2(-0.06f, 0.032f),
                new Vector2(-0.05f, 0f),
            };
        }

        // 腕の太さ（肩から手の先へ。軸は ±X で、位置は中心からの距離）
        static List<Vector2> ArmProfile()
        {
            return new List<Vector2>
            {
                new Vector2(0.12f, 0f), new Vector2(0.125f, 0.04f), new Vector2(0.14f, 0.05f), new Vector2(0.20f, 0.047f),
                new Vector2(0.30f, 0.042f), new Vector2(0.38f, 0.037f), new Vector2(0.41f, 0.036f), new Vector2(0.45f, 0.037f),
                new Vector2(0.55f, 0.032f), new Vector2(0.62f, 0.027f), new Vector2(0.66f, 0.03f), new Vector2(0.72f, 0.028f),
                new Vector2(0.76f, 0.018f), new Vector2(0.77f, 0f),
            };
        }

        // profile の区間 [from, to] を、太さを補って切り出す（両端は開いたまま）
        static List<Vector2> Slice(List<Vector2> profile, float from, float to, float gap)
        {
            var result = new List<Vector2>();
            for (int k = 0; k + 1 < profile.Count; k++)
            {
                Vector2 a = profile[k], b = profile[k + 1];
                if (a.y <= 0f || b.y <= 0f) continue;   // 極のある区間（先端の丸め）は使わない
                if (b.x < from || a.x > to) continue;
                if (result.Count == 0) result.Add(new Vector2(from, Mathf.Lerp(a.y, b.y, Mathf.InverseLerp(a.x, b.x, Mathf.Max(from, a.x))) + gap));
                if (b.x < to) result.Add(new Vector2(b.x, b.y + gap));
                else
                {
                    result.Add(new Vector2(to, Mathf.Lerp(a.y, b.y, Mathf.InverseLerp(a.x, b.x, to)) + gap));
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// スカート（腰から太ももの途中まで、下へ広がる筒。上下は開いている）。上はスカートの根元、裾に近いほど脚に引かれる（最大 55%）。
        /// </summary>
        public static Mesh CreateSkirtMesh(out string[] boneNames)
        {
            var builder = new TubeBuilder();
            var profile = new List<Vector2>();
            const int rows = 44;
            for (int k = 0; k <= rows; k++)
            {
                float t = (float)k / rows;
                profile.Add(new Vector2(-Mathf.Lerp(SkirtTopY, SkirtBottomY, t), Mathf.Lerp(SkirtTopRadius, SkirtBottomRadius, t)));
            }
            builder.Add(Vector3.zero, Vector3.down, Vector3.right, profile, 96, p =>
            {
                float t = Mathf.Clamp01((SkirtTopY - p.y) / (SkirtTopY - SkirtBottomY));
                float toLegs = 0.55f * Mathf.SmoothStep(0f, 1f, t);
                float left = Mathf.Clamp01(0.5f - p.x / 0.12f);
                return Weights((SkirtRootName, 1f - toLegs), ("LeftUpperLeg", toLegs * left), ("RightUpperLeg", toLegs * (1f - left)));
            });
            return builder.ToMesh("GP02_TestAvatarSkirt", out boneNames);
        }

        /// <summary>タイツ（脚に沿って体から 3mm 外。胴体の下の端より少し下（高さ 0.74m）から足首の上（0.09m）まで、両端は開いている）。ひざのウェイトは体よりなめらか。</summary>
        public static Mesh CreateTightsMesh(out string[] boneNames)
        {
            var builder = new TubeBuilder();
            foreach (int side in new[] { -1, 1 })
            {
                string s = Side(side);
                builder.Add(new Vector3(side * LegX, 0f, 0f), Vector3.down, Vector3.right, Resample(Slice(LegProfile(), -0.74f, -0.09f, TightGap), 0.01f), 32, p =>
                    Chain(-p.y, new[] { "Hips", s + "UpperLeg", s + "LowerLeg", s + "Foot" },
                        new[] { -HipJointY, -KneeY, -AnkleY }, new[] { 0.04f, ClothKneeBlend, 0.025f }));
            }
            return builder.ToMesh("GP02_TestAvatarTights", out boneNames);
        }

        /// <summary>袖（腕に沿って体から 3mm 外。肩の先から手首の手前まで、両端は開いている）。ひじのウェイトは体よりなめらか。</summary>
        public static Mesh CreateSleevesMesh(out string[] boneNames)
        {
            var builder = new TubeBuilder();
            foreach (int side in new[] { -1, 1 })
            {
                string s = Side(side);
                builder.Add(new Vector3(0f, ShoulderY, 0f), new Vector3(side, 0f, 0f), Vector3.forward, Resample(Slice(ArmProfile(), 0.18f, 0.58f, TightGap), 0.01f), 24, p =>
                    Chain(Mathf.Abs(p.x), new[] { s + "Shoulder", s + "UpperArm", s + "LowerArm", s + "Hand" },
                        new[] { UpperArmX, ElbowX, WristX }, new[] { 0.04f, ClothElbowBlend, 0.025f }));
            }
            return builder.ToMesh("GP02_TestAvatarSleeves", out boneNames);
        }

        // 区間を、間隔 step 以下になるよう補って細かくする（曲げたときになめらかに曲がるように）
        static List<Vector2> Resample(List<Vector2> profile, float step)
        {
            var result = new List<Vector2> { profile[0] };
            for (int k = 0; k + 1 < profile.Count; k++)
            {
                Vector2 a = profile[k], b = profile[k + 1];
                int n = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(b.x - a.x) / step));
                for (int i = 1; i <= n; i++) result.Add(Vector2.Lerp(a, b, (float)i / n));
            }
            return result;
        }

        // 軸に沿った位置 s で、骨の並び bones のウェイト。関節 joints（昇順）の前後 blends の範囲で、なめらかに次の骨へ移る
        static (string, float)[] Chain(float s, string[] bones, float[] joints, float[] blends)
        {
            for (int k = 0; k < joints.Length; k++)
            {
                if (s < joints[k] - blends[k]) return Weights((bones[k], 1f));
                if (s <= joints[k] + blends[k])
                {
                    float t = Mathf.SmoothStep(0f, 1f, (s - (joints[k] - blends[k])) / (2f * blends[k]));
                    return Weights((bones[k], 1f - t), (bones[k + 1], t));
                }
            }
            return Weights((bones[bones.Length - 1], 1f));
        }

        static (string, float)[] Single(string bone) => Weights((bone, 1f));

        static (string, float)[] Weights(params (string, float)[] weights) => weights;

        /// <summary>
        /// 軸に沿った筒（回転体）をためて、1つのメッシュにする。骨は名前で受け取り、出てきた順に番号を振る。
        /// </summary>
        sealed class TubeBuilder
        {
            readonly List<Vector3> _positions = new List<Vector3>();
            readonly List<Vector2> _uvs = new List<Vector2>();
            readonly List<(string, float)[]> _weights = new List<(string, float)[]>();
            readonly List<int> _triangles = new List<int>();

            /// <summary>
            /// origin を通り axis の向きの筒。profile は軸に沿った位置（昇順）と半径で、半径 0 の点は1頂点の極（閉じた端）になる。
            /// 半径 0 の点が無い端は開いたまま。外向きが表になる巻き順にする。
            /// </summary>
            public void Add(Vector3 origin, Vector3 axis, Vector3 reference, List<Vector2> profile, int segments, Func<Vector3, (string, float)[]> weight)
            {
                axis.Normalize();
                Vector3 u = Vector3.Cross(axis, Vector3.Cross(reference, axis)).normalized;
                if (u.sqrMagnitude < 1e-6f) u = Vector3.Cross(axis, Vector3.up).normalized;
                Vector3 v = Vector3.Cross(axis, u);
                int first = _triangles.Count;
                var ringStart = new int[profile.Count];
                var pole = new bool[profile.Count];
                for (int k = 0; k < profile.Count; k++)
                {
                    Vector3 center = origin + axis * profile[k].x;
                    ringStart[k] = _positions.Count;
                    pole[k] = profile[k].y <= 0f;
                    if (pole[k])
                    {
                        AddVertex(center, new Vector2(0.5f, (float)k / (profile.Count - 1)), weight);
                        continue;
                    }
                    for (int i = 0; i < segments; i++)
                    {
                        float angle = 2f * Mathf.PI * i / segments;
                        Vector3 p = center + (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * profile[k].y;
                        AddVertex(p, new Vector2((float)i / segments, (float)k / (profile.Count - 1)), weight);
                    }
                }
                for (int k = 0; k + 1 < profile.Count; k++)
                {
                    for (int i = 0; i < segments; i++)
                    {
                        int j = (i + 1) % segments;
                        int a = ringStart[k] + (pole[k] ? 0 : i), b = ringStart[k] + (pole[k] ? 0 : j);
                        int c = ringStart[k + 1] + (pole[k + 1] ? 0 : i), d = ringStart[k + 1] + (pole[k + 1] ? 0 : j);
                        if (!pole[k]) { _triangles.Add(a); _triangles.Add(c); _triangles.Add(b); }
                        if (!pole[k + 1]) { _triangles.Add(b); _triangles.Add(c); _triangles.Add(d); }
                    }
                }
                // 巻き順の向きが軸から外を向いているかを、足した三角形全体で確かめ、内向きなら全部を裏返す
                float outward = 0f;
                for (int t = first; t < _triangles.Count; t += 3)
                {
                    Vector3 a = _positions[_triangles[t]], b = _positions[_triangles[t + 1]], c = _positions[_triangles[t + 2]];
                    Vector3 middle = (a + b + c) / 3f;
                    Vector3 fromAxis = middle - origin - axis * Vector3.Dot(middle - origin, axis);
                    outward += Vector3.Dot(Vector3.Cross(b - a, c - a), fromAxis);
                }
                if (outward < 0f)
                {
                    for (int t = first; t < _triangles.Count; t += 3) (_triangles[t + 1], _triangles[t + 2]) = (_triangles[t + 2], _triangles[t + 1]);
                }
            }

            void AddVertex(Vector3 p, Vector2 uv, Func<Vector3, (string, float)[]> weight)
            {
                _positions.Add(p);
                _uvs.Add(uv);
                _weights.Add(weight(p));
            }

            public Mesh ToMesh(string name, out string[] boneNames)
            {
                var names = new List<string>();
                var index = new Dictionary<string, int>();
                var boneWeights = new BoneWeight[_weights.Count];
                for (int vtx = 0; vtx < _weights.Count; vtx++)
                {
                    var list = new List<(int, float)>();
                    float total = 0f;
                    foreach ((string bone, float w) in _weights[vtx])
                    {
                        if (!(w > 1e-4f)) continue;
                        if (!index.TryGetValue(bone, out int b))
                        {
                            b = names.Count;
                            names.Add(bone);
                            index.Add(bone, b);
                        }
                        list.Add((b, w));
                        total += w;
                    }
                    list.Sort((x, y) => y.Item2.CompareTo(x.Item2));
                    var bw = new BoneWeight();
                    for (int k = 0; k < list.Count && k < 4; k++)
                    {
                        float w = list[k].Item2 / total;
                        switch (k)
                        {
                            case 0: bw.boneIndex0 = list[k].Item1; bw.weight0 = w; break;
                            case 1: bw.boneIndex1 = list[k].Item1; bw.weight1 = w; break;
                            case 2: bw.boneIndex2 = list[k].Item1; bw.weight2 = w; break;
                            default: bw.boneIndex3 = list[k].Item1; bw.weight3 = w; break;
                        }
                    }
                    boneWeights[vtx] = bw;
                }
                var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(_positions);
                mesh.SetUVs(0, _uvs);
                mesh.SetTriangles(_triangles, 0);
                mesh.boneWeights = boneWeights;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                boneNames = names.ToArray();
                return mesh;
            }
        }
    }
}
