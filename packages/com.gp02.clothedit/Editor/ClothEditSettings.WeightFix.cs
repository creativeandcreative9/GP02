using System;
using System.Collections.Generic;
using System.Globalization;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 動作チェックの第2段の設定: ウェイトを体に寄せる（強さ・半径）と、手持ちの AnimationClip で試すこと（クリップ・間隔）。
    public sealed partial class ClothEditSettings
    {
        /// <summary>ウェイトを体に寄せる半径の上限（メートル）。</summary>
        public const float MaxWeightFixRadius = 0.1f;

        /// <summary>手持ちの AnimationClip から取り出す間隔（コマ）の既定値と上限。</summary>
        public const int DefaultPoseCheckClipInterval = 8;
        public const int MaxPoseCheckClipInterval = 120;

        /// <summary>1つのクリップから取り出すコマの上限（長いダンスで動作チェックが長くなりすぎないように。超えるときは間隔を広げる）。</summary>
        public const int MaxPoseCheckClipSamples = 60;

        /// <summary>ウェイトを体に寄せる強さ（0〜1）。1 なら、ウェイトが原因の所は体のウェイトそのものにする。</summary>
        public float WeightFixStrength = WeightTransferOptions.DefaultStrength;

        /// <summary>ウェイトを体に寄せる所から、周りへなめらかに減らす半径（メートル、0〜<see cref="MaxWeightFixRadius"/>）。</summary>
        public float WeightFixRadius = WeightTransferOptions.DefaultRadius;

        /// <summary>動作チェックで、プリセットのポーズに加えて試す手持ちの AnimationClip（くわしく表示で足す）。アセットのものは EditorPrefs に保存する。</summary>
        public readonly List<AnimationClip> PoseCheckClips = new List<AnimationClip>();

        /// <summary>手持ちの AnimationClip から、ポーズを取り出す間隔（コマ。クリップのフレームレートで数える）。</summary>
        public int PoseCheckClipInterval = DefaultPoseCheckClipInterval;

        /// <summary>ウェイトを体に寄せる計算の設定（ボーンの数の上限は、レンダラーごとに呼び出し側が入れる）。</summary>
        public WeightTransferOptions BuildWeightTransferOptions()
        {
            return new WeightTransferOptions
            {
                Strength = WeightFixStrength >= 0f ? Math.Min(1f, WeightFixStrength) : WeightTransferOptions.DefaultStrength,
                Radius = WeightFixRadius >= 0f ? Math.Min(MaxWeightFixRadius, WeightFixRadius) : WeightTransferOptions.DefaultRadius,
            };
        }

        /// <summary>試す AnimationClip（null と重なりを除いたもの）。</summary>
        public List<AnimationClip> GetPoseCheckClips()
        {
            var clips = new List<AnimationClip>();
            foreach (AnimationClip clip in PoseCheckClips)
            {
                if (clip != null && !clips.Contains(clip)) clips.Add(clip);
            }
            return clips;
        }

        /// <summary>
        /// クリップから取り出すコマの時刻（秒）。0 秒から intervalFrames コマごと（クリップのフレームレート）に、最後のコマも入れる。
        /// <see cref="MaxPoseCheckClipSamples"/> を超えるときは、間隔を広げて減らす。
        /// </summary>
        public static List<float> ClipSampleTimes(AnimationClip clip, int intervalFrames)
        {
            var times = new List<float>();
            if (clip == null) return times;
            float rate = clip.frameRate > 0f ? clip.frameRate : 30f;
            float length = Mathf.Max(0f, clip.length);
            float step = Mathf.Max(1, intervalFrames) / rate;
            if (length / step + 2f > MaxPoseCheckClipSamples) step = length / Mathf.Max(1, MaxPoseCheckClipSamples - 1);
            if (!(step > 0f))
            {
                times.Add(0f);
                return times;
            }
            for (int k = 0; k * step <= length + 1e-5f && times.Count < MaxPoseCheckClipSamples; k++) times.Add(Mathf.Min(length, k * step));
            if (times[times.Count - 1] < length - 1e-4f)
            {
                if (times.Count >= MaxPoseCheckClipSamples) times[times.Count - 1] = length;
                else times.Add(length);
            }
            return times;
        }

        void ResetWeightFixDefaults()
        {
            WeightFixStrength = WeightTransferOptions.DefaultStrength;
            WeightFixRadius = WeightTransferOptions.DefaultRadius;
            PoseCheckClips.Clear();
            PoseCheckClipInterval = DefaultPoseCheckClipInterval;
        }

        void SanitizeWeightFix()
        {
            if (!(WeightFixStrength >= 0f)) WeightFixStrength = float.IsNaN(WeightFixStrength) ? WeightTransferOptions.DefaultStrength : 0f;
            else if (WeightFixStrength > 1f) WeightFixStrength = 1f;
            if (!(WeightFixRadius >= 0f)) WeightFixRadius = float.IsNaN(WeightFixRadius) ? WeightTransferOptions.DefaultRadius : 0f;
            else if (WeightFixRadius > MaxWeightFixRadius) WeightFixRadius = MaxWeightFixRadius;
            if (PoseCheckClipInterval < 1) PoseCheckClipInterval = 1;
            else if (PoseCheckClipInterval > MaxPoseCheckClipInterval) PoseCheckClipInterval = MaxPoseCheckClipInterval;
        }

        void LoadWeightFix(string keyPrefix)
        {
            WeightFixStrength = EditorPrefs.GetFloat(keyPrefix + "WeightFixStrength", WeightTransferOptions.DefaultStrength);
            WeightFixRadius = EditorPrefs.GetFloat(keyPrefix + "WeightFixRadius", WeightTransferOptions.DefaultRadius);
            PoseCheckClipInterval = EditorPrefs.GetInt(keyPrefix + "PoseCheckClipInterval", DefaultPoseCheckClipInterval);
            PoseCheckClips.Clear();
            // クリップは「GUID:ファイルの中の番号」で持つ（FBX には複数のクリップが入っているため）
            foreach (string item in EditorPrefs.GetString(keyPrefix + "PoseCheckClips", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = item.Split(':');
                if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long localId)) continue;
                string path = AssetDatabase.GUIDToAssetPath(parts[0]);
                if (string.IsNullOrEmpty(path)) continue;
                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (!(asset is AnimationClip clip) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string _, out long id) || id != localId) continue;
                    if (!PoseCheckClips.Contains(clip)) PoseCheckClips.Add(clip);
                    break;
                }
            }
        }

        void SaveWeightFix(string keyPrefix)
        {
            EditorPrefs.SetFloat(keyPrefix + "WeightFixStrength", WeightFixStrength);
            EditorPrefs.SetFloat(keyPrefix + "WeightFixRadius", WeightFixRadius);
            EditorPrefs.SetInt(keyPrefix + "PoseCheckClipInterval", PoseCheckClipInterval);
            var items = new List<string>();
            foreach (AnimationClip clip in GetPoseCheckClips())
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId) && !string.IsNullOrEmpty(guid))
                {
                    items.Add(guid + ":" + localId.ToString(CultureInfo.InvariantCulture));
                }
            }
            EditorPrefs.SetString(keyPrefix + "PoseCheckClips", string.Join(";", items));
        }

        static void DeleteWeightFix(string keyPrefix)
        {
            foreach (string key in new[] { "WeightFixStrength", "WeightFixRadius", "PoseCheckClipInterval", "PoseCheckClips" }) EditorPrefs.DeleteKey(keyPrefix + key);
        }
    }
}
