using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// ローカルの Unity ですぐ試せるよう、テスト用の体とスカート（ペチコート・リボン付き）をワンクリックで作るメニュー。
    /// 形はすべてプログラムで生成する（購入したアバター・衣装のデータは使わない）。長さの単位はメートル。
    /// </summary>
    public static class TestClothMenu
    {
        public const string MenuPath = "Tools/GP02/テスト用の衣装を作る";
        public const string DefaultFolder = "Assets/GP02/TestCloth";

        // 体とスカートの寸法（アバター相当の実寸）
        public const float BodyHeight = 1.4f;
        public const float HipsHeight = 0.75f;
        public const float LegOffsetX = 0.06f;
        public const float SkirtTopY = 0.80f;
        public const float SkirtLength = 0.35f;
        public const float SkirtTopRadius = 0.13f;
        public const float SkirtBottomRadius = 0.30f;
        public const float PetticoatGap = 0.003f;
        public const int SkirtSegments = 120;
        public const int SkirtRows = 32;
        public const int RibbonCount = 6;

        // ペチコートはスカートの丈の 90% まで。裾が少し短い（縦の分割の間隔はスカートと同じ）
        const float PetticoatLengthRatio = 0.9f;
        public const int PetticoatRows = (int)(SkirtRows * PetticoatLengthRatio);

        const int HipsBone = 0;
        const int LeftLegBone = 1;
        const int RightLegBone = 2;

        [MenuItem(MenuPath)]
        static void CreateFromMenu()
        {
            CreateInScene(DefaultFolder);
        }

        // ---- シーンに置く処理 ----

        /// <summary>
        /// 体・スカート・ボーンをシーンに作り、スカートを選択する。Undo で取り消せる。
        /// </summary>
        /// <param name="assetFolder">メッシュとマテリアルを保存するフォルダ。null か空なら保存しない（テスト用）</param>
        /// <returns>全体をまとめたルート。体は "Body"、スカートは "Skirt" という名前の子</returns>
        public static GameObject CreateInScene(string assetFolder)
        {
            bool save = !string.IsNullOrEmpty(assetFolder);
            if (save) EnsureFolder(assetFolder);

            Mesh bodyMesh = CreateBodyMesh();
            Mesh skirtMesh = CreateSkirtMesh();
            Material skin = NewMaterial("TestBody_Skin", new Color(0.96f, 0.78f, 0.66f));
            Material skirtOuter = NewMaterial("TestSkirt_Outer", new Color(0.80f, 0.20f, 0.32f));
            Material petticoat = NewMaterial("TestSkirt_Petticoat", new Color(0.94f, 0.90f, 0.70f));
            Material ribbon = NewMaterial("TestSkirt_Ribbon", new Color(0.20f, 0.40f, 0.85f));

            if (save)
            {
                SaveAsset(bodyMesh, assetFolder, ".asset");
                SaveAsset(skirtMesh, assetFolder, ".asset");
                SaveAsset(skin, assetFolder, ".mat");
                SaveAsset(skirtOuter, assetFolder, ".mat");
                SaveAsset(petticoat, assetFolder, ".mat");
                SaveAsset(ribbon, assetFolder, ".mat");
            }

            var root = new GameObject("GP02_TestCloth");
            Undo.RegisterCreatedObjectUndo(root, "テスト用の衣装を作る");

            Transform[] bones = CreateBones(root.transform);
            GameObject body = AddSkinned(root.transform, "Body", bodyMesh, bones, new[] { skin });
            GameObject skirt = AddSkinned(root.transform, "Skirt", skirtMesh, bones, new[] { skirtOuter, petticoat, ribbon });

            // バインドポーズ（メッシュを作った姿勢）とは違う姿勢で編集できることを確かめるため、脚を回す。
            // 回すのは、バインドポーズを設定したあと
            ApplyPose(bones);

            Selection.activeGameObject = skirt;
            return root;
        }

        /// <summary>脚のボーンを回して、バインドポーズと違う姿勢にする。</summary>
        public static void ApplyPose(Transform[] bones)
        {
            bones[LeftLegBone].localRotation = Quaternion.Euler(-25f, 0f, 6f);
            bones[RightLegBone].localRotation = Quaternion.Euler(12f, 0f, -4f);
        }

        /// <summary>腰と左右の脚のボーンを作る。順番はメッシュの bindposes・boneWeights と同じ（腰、左脚、右脚）。</summary>
        public static Transform[] CreateBones(Transform parent)
        {
            var armature = new GameObject("Armature");
            armature.transform.SetParent(parent, false);
            var hips = new GameObject("Hips");
            hips.transform.SetParent(armature.transform, false);
            hips.transform.localPosition = new Vector3(0f, HipsHeight, 0f);
            var left = new GameObject("LeftUpperLeg");
            left.transform.SetParent(hips.transform, false);
            left.transform.localPosition = new Vector3(LegOffsetX, 0f, 0f);
            var right = new GameObject("RightUpperLeg");
            right.transform.SetParent(hips.transform, false);
            right.transform.localPosition = new Vector3(-LegOffsetX, 0f, 0f);
            return new[] { hips.transform, left.transform, right.transform };
        }

        static GameObject AddSkinned(Transform parent, string name, Mesh mesh, Transform[] bones, Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[HipsBone];
            renderer.sharedMaterials = materials;
            return go;
        }

        static Material NewMaterial(string name, Color color)
        {
            // Standard が無い環境（URP だけのプロジェクトなど）でもエラーにならないよう、順にさがす
            Shader shader = Shader.Find("Standard")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = name, color = color };
            return material;
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

        // ---- 形を作る処理（シーンに触らない） ----

        /// <summary>
        /// 体のメッシュ（胴体・頭・2本の脚）。ボーンは 腰、左脚、右脚 の順で、
        /// バインドポーズは「ルートが原点で、ボーンが CreateBones の位置にある姿勢」。
        /// </summary>
        public static Mesh CreateBodyMesh()
        {
            var builder = new MeshBuilder(1);

            // 胴体（上から下へ）。頭は上端に丸めて付ける
            Vector2[] torso =
            {
                new Vector2(0f, 1.20f), new Vector2(0.05f, 1.195f), new Vector2(0.11f, 1.17f),
                new Vector2(0.115f, 1.10f), new Vector2(0.10f, 0.90f), new Vector2(0.105f, 0.72f),
                new Vector2(0f, 0.72f),
            };
            builder.AddLathe(0, 0f, torso, 24, p => Single(HipsBone));

            Vector2[] head =
            {
                new Vector2(0f, BodyHeight), new Vector2(0.05f, BodyHeight - 0.012f), new Vector2(0.085f, BodyHeight - 0.04f),
                new Vector2(0.095f, BodyHeight - 0.09f), new Vector2(0.07f, BodyHeight - 0.15f), new Vector2(0.035f, BodyHeight - 0.18f),
                new Vector2(0.035f, 1.20f), new Vector2(0f, 1.20f),
            };
            builder.AddLathe(0, 0f, head, 24, p => Single(HipsBone));

            Vector2[] leg =
            {
                new Vector2(0f, 0.78f), new Vector2(0.055f, 0.775f), new Vector2(0.06f, 0.70f),
                new Vector2(0.05f, 0.40f), new Vector2(0.04f, 0.04f), new Vector2(0.04f, 0.0f),
                new Vector2(0f, 0.0f),
            };
            builder.AddLathe(0, LegOffsetX, leg, 16, p => WeightForLeg(p, LeftLegBone));
            builder.AddLathe(0, -LegOffsetX, leg, 16, p => WeightForLeg(p, RightLegBone));

            Mesh mesh = builder.ToMesh("GP02_TestBody");
            mesh.bindposes = BindPoses();
            return mesh;
        }

        /// <summary>
        /// スカートのメッシュ。サブメッシュは 0 = スカート、1 = ペチコート（3mm 内側・少し短い）、2 = リボン（6 個の小さな四角形）。
        /// スカートとペチコートには UV の継ぎ目（同じ位置の頂点の重なり）が1列ある。
        /// </summary>
        public static Mesh CreateSkirtMesh()
        {
            var builder = new MeshBuilder(3);

            var outer = new Vector2[SkirtRows + 1];
            for (int j = 0; j <= SkirtRows; j++) outer[j] = SkirtProfile((float)j / SkirtRows, 0f);
            builder.AddLathe(0, 0f, outer, SkirtSegments, WeightForSkirt);

            // 斜面の法線方向に PetticoatGap だけ内側へ寄せる。半径の差は gap / cos(斜面の傾き)
            float slope = (SkirtBottomRadius - SkirtTopRadius) / SkirtLength;
            float radialGap = PetticoatGap * Mathf.Sqrt(1f + slope * slope);
            var inner = new Vector2[PetticoatRows + 1];
            for (int j = 0; j <= PetticoatRows; j++) inner[j] = SkirtProfile(PetticoatLengthRatio * j / PetticoatRows, radialGap);
            builder.AddLathe(1, 0f, inner, SkirtSegments, WeightForSkirt);

            // リボン：ウエストのまわりに、外向きの小さな四角形（幅 1.2cm・高さ 1cm）を並べる。それぞれが別の島になる
            for (int k = 0; k < RibbonCount; k++)
            {
                float angle = (30f + 360f * k / RibbonCount) * Mathf.Deg2Rad;
                builder.AddQuad(2, angle, 0.14f, SkirtTopY, 0.012f, 0.010f, WeightForSkirt);
            }

            Mesh mesh = builder.ToMesh("GP02_TestSkirt");
            mesh.bindposes = BindPoses();
            return mesh;
        }

        // t = 0（上端）〜1（裾）の位置の、(半径, 高さ)
        static Vector2 SkirtProfile(float t, float radiusOffset)
        {
            float radius = Mathf.Lerp(SkirtTopRadius, SkirtBottomRadius, t) - radiusOffset;
            return new Vector2(radius, SkirtTopY - SkirtLength * t);
        }

        static Matrix4x4[] BindPoses()
        {
            // ルートも各レンダラーも原点で回転・拡大なしなので、ボーンのワールド位置の逆移動だけでよい
            return new[]
            {
                Matrix4x4.Translate(new Vector3(0f, -HipsHeight, 0f)),
                Matrix4x4.Translate(new Vector3(-LegOffsetX, -HipsHeight, 0f)),
                Matrix4x4.Translate(new Vector3(LegOffsetX, -HipsHeight, 0f)),
            };
        }

        // 脚は上のほうで腰と混ぜ、体が裂けないようにする
        static BoneWeight WeightForLeg(Vector3 p, int legBone)
        {
            float toLeg = Mathf.SmoothStep(0f, 1f, (HipsHeight + 0.05f - p.y) / 0.1f);
            return Mix(1f - toLeg, legBone == LeftLegBone ? toLeg : 0f, legBone == RightLegBone ? toLeg : 0f);
        }

        // スカートは裾に近いほど脚に引かれる（最大 60%）。左右はスカートの x 座標でなめらかに分ける（左脚は +x 側）。
        // 同じ位置の頂点（継ぎ目）には同じ値になる
        static BoneWeight WeightForSkirt(Vector3 p)
        {
            float t = Mathf.Clamp01((SkirtTopY - p.y) / SkirtLength);
            float toLegs = 0.6f * Mathf.SmoothStep(0f, 1f, t);
            float left = Mathf.Clamp01(0.5f + p.x / 0.12f);
            return Mix(1f - toLegs, toLegs * left, toLegs * (1f - left));
        }

        static BoneWeight Single(int bone)
        {
            return new BoneWeight { boneIndex0 = bone, weight0 = 1f };
        }

        static BoneWeight Mix(float hips, float left, float right)
        {
            return new BoneWeight
            {
                boneIndex0 = HipsBone, weight0 = hips,
                boneIndex1 = LeftLegBone, weight1 = left,
                boneIndex2 = RightLegBone, weight2 = right,
            };
        }

        /// <summary>頂点・UV・ボーンウェイト・サブメッシュごとの三角形をためて、Mesh にする。</summary>
        sealed class MeshBuilder
        {
            readonly List<Vector3> _positions = new List<Vector3>();
            readonly List<Vector3> _normals = new List<Vector3>();
            readonly List<Vector2> _uvs = new List<Vector2>();
            readonly List<BoneWeight> _weights = new List<BoneWeight>();
            readonly List<int>[] _triangles;

            public MeshBuilder(int subMeshCount)
            {
                _triangles = new List<int>[subMeshCount];
                for (int i = 0; i < subMeshCount; i++) _triangles[i] = new List<int>();
            }

            int Add(Vector3 position, Vector3 normal, Vector2 uv, Func<Vector3, BoneWeight> weight)
            {
                _positions.Add(position);
                _normals.Add(normal);
                _uvs.Add(uv);
                _weights.Add(weight(position));
                return _positions.Count - 1;
            }

            /// <summary>
            /// 回転体。profile は上から下へ並べた (半径, 高さ)。半径 0 の点は、1 頂点にまとめた極になる。
            /// 輪の頂点は最初と最後が同じ位置（UV の継ぎ目）。外向きが表になる向きで三角形を作る。
            /// </summary>
            public void AddLathe(int subMesh, float centerX, Vector2[] profile, int segments, Func<Vector3, BoneWeight> weight)
            {
                int ringCount = profile.Length;
                var ringStart = new int[ringCount];
                var isPole = new bool[ringCount];

                for (int k = 0; k < ringCount; k++)
                {
                    isPole[k] = profile[k].x <= 0f;
                    ringStart[k] = _positions.Count;
                    float v = 1f - (float)k / (ringCount - 1);

                    // 輪の法線は、前後の線分の法線の平均（法線は半径方向と垂直方向の成分）
                    Vector2 normal2 = Vector2.zero;
                    if (k > 0) normal2 += SegmentNormal(profile[k - 1], profile[k]);
                    if (k < ringCount - 1) normal2 += SegmentNormal(profile[k], profile[k + 1]);
                    normal2 = normal2.sqrMagnitude > 0f ? normal2.normalized : Vector2.right;

                    if (isPole[k])
                    {
                        // 極の法線は真上か真下
                        Add(new Vector3(centerX, profile[k].y, 0f), new Vector3(0f, k == 0 ? 1f : -1f, 0f), new Vector2(0.5f, v), weight);
                        continue;
                    }

                    for (int i = 0; i <= segments; i++)
                    {
                        float angle = 2f * Mathf.PI * i / segments;
                        float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
                        // 最後の頂点は、cos/sin の誤差で最初とずれないよう、角度 0 と同じ値にする
                        if (i == segments) { c = 1f; s = 0f; }
                        Add(new Vector3(centerX + profile[k].x * c, profile[k].y, profile[k].x * s),
                            new Vector3(normal2.x * c, normal2.y, normal2.x * s),
                            new Vector2((float)i / segments, v), weight);
                    }
                }

                List<int> tris = _triangles[subMesh];
                for (int k = 0; k < ringCount - 1; k++)
                {
                    for (int i = 0; i < segments; i++)
                    {
                        int a = ringStart[k] + (isPole[k] ? 0 : i), b = ringStart[k] + (isPole[k] ? 0 : i + 1);
                        int c = ringStart[k + 1] + (isPole[k + 1] ? 0 : i), d = ringStart[k + 1] + (isPole[k + 1] ? 0 : i + 1);
                        if (!isPole[k]) { tris.Add(a); tris.Add(b); tris.Add(c); }
                        if (!isPole[k + 1]) { tris.Add(b); tris.Add(d); tris.Add(c); }
                    }
                }
            }

            // 上から下へ向かう線分の、外向き（半径方向, 垂直方向）の法線
            static Vector2 SegmentNormal(Vector2 from, Vector2 to)
            {
                var direction = new Vector2(to.x - from.x, to.y - from.y);
                var normal = new Vector2(-direction.y, direction.x);
                return normal.sqrMagnitude > 0f ? normal.normalized : Vector2.zero;
            }

            /// <summary>半径 radius の円周上の angle の位置に、外を向いた四角形を1枚置く。</summary>
            public void AddQuad(int subMesh, float angle, float radius, float centerY, float width, float height, Func<Vector3, BoneWeight> weight)
            {
                float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
                var center = new Vector3(radius * c, centerY, radius * s);
                var tangent = new Vector3(-s, 0f, c) * (width * 0.5f);
                var up = new Vector3(0f, height * 0.5f, 0f);
                var normal = new Vector3(c, 0f, s);

                int a = Add(center - tangent + up, normal, new Vector2(0f, 1f), weight);
                int b = Add(center + tangent + up, normal, new Vector2(1f, 1f), weight);
                int cc = Add(center - tangent - up, normal, new Vector2(0f, 0f), weight);
                int d = Add(center + tangent - up, normal, new Vector2(1f, 0f), weight);
                List<int> tris = _triangles[subMesh];
                tris.Add(a); tris.Add(b); tris.Add(cc);
                tris.Add(b); tris.Add(d); tris.Add(cc);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                mesh.SetVertices(_positions);
                mesh.SetNormals(_normals);
                mesh.SetUVs(0, _uvs);
                mesh.subMeshCount = _triangles.Length;
                for (int i = 0; i < _triangles.Length; i++) mesh.SetTriangles(_triangles[i], i);
                mesh.boneWeights = _weights.ToArray();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
