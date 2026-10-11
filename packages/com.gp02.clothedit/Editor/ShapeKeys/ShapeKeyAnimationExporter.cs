using System;
using System.Collections.Generic;
using System.IO;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーごとに、重み 0 と重み 100 の AnimationClip を作る（トグル用）。VRChat の FX レイヤーなどの
    /// アニメーターのステートにそのまま使える。クリップのパスは、アバターのルート（Animator のある親）からのレンダラーのパス。
    /// 既にあるクリップは中身だけを置き換えるので、アニメーターからの参照（GUID）は保たれる。
    /// VRChat SDK には依存しない（ふつうの AnimationClip）。
    /// </summary>
    public static class ShapeKeyAnimationExporter
    {
        public const string DefaultFolder = "Assets/GP02/Animations";

        // 1 フレームだけのクリップは長さが 0 になるので、60fps の 1 フレーム分だけ値を保つ
        const float HoldTime = 1f / 60f;
        const string BlendShapeCurvePrefix = "blendShape.";

        /// <summary>1つのシェイプキーの、重み 0 と重み 100 のクリップ。</summary>
        public sealed class ToggleClips
        {
            /// <summary>BlendShape の名前（前置き・後置きを付けたあと）。</summary>
            public string BlendShapeName;

            /// <summary>重み 0 のクリップ。</summary>
            public AnimationClip Off;

            /// <summary>重み 100 のクリップ。</summary>
            public AnimationClip On;
        }

        /// <summary>
        /// アバターのルートからのレンダラーのパス。レンダラーがルートそのものなら空。
        /// </summary>
        /// <exception cref="ArgumentException">レンダラーがルートの下にないとき</exception>
        public static string GetRendererPath(Renderer renderer, Transform avatarRoot)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (avatarRoot == null) avatarRoot = ShapeKeyExporter.FindAvatarRoot(renderer.transform);
            if (renderer.transform == avatarRoot) return "";
            if (!renderer.transform.IsChildOf(avatarRoot))
            {
                throw new ArgumentException(L10n.Tr(
                    $"「{renderer.name}」は、アバターのルート「{avatarRoot.name}」の下にありません。",
                    $"\"{renderer.name}\" is not under the avatar root \"{avatarRoot.name}\"."));
            }
            return AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot);
        }

        /// <summary>
        /// コンポーネントのシェイプキーのうち、書き出すと BlendShape になるもの（有効で、焼き込みの印が無いもの）ごとに、重み 0 と 100 のクリップを作る。
        /// 名前は <c>オブジェクト名_BlendShape名_Off.anim</c> と <c>_On.anim</c>。
        /// 「同じ名前を上書きする」のとき、元のメッシュの BlendShape を置き換えるものも対象になる（その BlendShape の名前で作る）。
        /// </summary>
        /// <param name="options">書き出しと同じ設定（名前の前置き・後置きなど）。BlendShape の名前は書き出しの計画と同じになる</param>
        /// <param name="avatarRoot">パスの基準。null なら <see cref="ShapeKeyExporter.FindAvatarRoot"/></param>
        /// <exception cref="InvalidOperationException">SkinnedMeshRenderer でないとき（BlendShape が使えない）、または書き出せない状態のとき</exception>
        public static List<ToggleClips> CreateToggleClips(GP02ShapeKeys component, ShapeKeyBuildOptions options, string folder = DefaultFolder, Transform avatarRoot = null)
        {
            string problem = ShapeKeyExporter.GetProblem(component);
            if (problem != null) throw new InvalidOperationException(problem);
            Renderer renderer = ClothEditController.FindRenderer(component.gameObject);
            if (!(renderer is SkinnedMeshRenderer))
            {
                throw new InvalidOperationException(L10n.Tr(
                    "MeshRenderer には BlendShape のアニメーションが使えないので、AnimationClip は作れません（SkinnedMeshRenderer が必要です）。",
                    "BlendShape animation cannot be used on a MeshRenderer, so no AnimationClip can be created (a SkinnedMeshRenderer is required)."));
            }

            List<ExportPlanItem> plan = ShapeKeyExporter.GetPlan(component, options);
            string path = GetRendererPath(renderer, avatarRoot);
            folder = (folder ?? DefaultFolder).TrimEnd('/');
            EnsureFolder(folder);

            var result = new List<ToggleClips>();
            var made = new HashSet<string>();
            foreach (ExportPlanItem item in plan)
            {
                if (!item.ProducesBlendShape || !made.Add(item.BlendShapeName)) continue;
                string baseName = Sanitize(component.gameObject.name + "_" + item.BlendShapeName);
                result.Add(new ToggleClips
                {
                    BlendShapeName = item.BlendShapeName,
                    Off = WriteClip($"{folder}/{baseName}_Off.anim", path, item.BlendShapeName, 0f),
                    On = WriteClip($"{folder}/{baseName}_On.anim", path, item.BlendShapeName, ShapeKeyMath.FullWeight),
                });
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        // 値を一定に保つ 1 本のカーブだけのクリップをアセットに書く。既にあれば中身を置き換える
        static AnimationClip WriteClip(string assetPath, string rendererPath, string blendShapeName, float value)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            bool isNew = clip == null;
            if (isNew) clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(assetPath) };
            else clip.ClearCurves();

            clip.frameRate = 60f;
            var binding = EditorCurveBinding.FloatCurve(rendererPath, typeof(SkinnedMeshRenderer), BlendShapeCurvePrefix + blendShapeName);
            var curve = new AnimationCurve(new Keyframe(0f, value), new Keyframe(HoldTime, value));
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
            AnimationUtility.SetEditorCurve(clip, binding, curve);

            if (isNew) AssetDatabase.CreateAsset(clip, assetPath);
            else EditorUtility.SetDirty(clip);
            return clip;
        }

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
