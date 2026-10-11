using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;

namespace GP02.EditorTools
{
    // 動作チェック（いろいろなポーズで貫通を調べる）の設定。
    public sealed partial class ClothEditSettings
    {
        /// <summary>許容の深さの上限（メートル）。</summary>
        public const float MaxPoseCheckTolerance = 0.01f;

        /// <summary>許容の深さ（メートル、0〜<see cref="MaxPoseCheckTolerance"/>）。これより深い貫通だけを数える。既定 1mm。</summary>
        public float PoseCheckTolerance = PoseCheckOptions.DefaultTolerance;

        /// <summary>各ポーズを「半分」と「全部」の2段階で試すか。オフなら「全部」だけ（速い）。</summary>
        public bool PoseCheckBothStages = true;

        /// <summary>試さないポーズの名前（<see cref="PosePreset.Id"/>）を「,」でつないだもの。既定はすべて試す。</summary>
        public string PoseCheckDisabled = "";

        /// <summary>
        /// 動作チェックの結果が出たら、編集の姿勢で、最悪の深さで色分けした点を出すか（好み）。
        /// ほかの操作を始めると一時的に消え、区切りの「突き抜ける所を色の点で示す」で出し直せる（<see cref="ClothEditController.IsPoseCheckDepthShown"/>）。
        /// </summary>
        public bool ShowPoseCheckDepth = true;

        /// <summary>
        /// 大きく動くポーズ（座る・しゃがむ・足を上げる・開く。<see cref="PoseCategory.Large"/>）も直す範囲に入れるか。既定はオフ。
        /// オフなら、色分けの点・悪い順・外へ逃がす・ウェイトを体に寄せるを、よく使うポーズの結果だけで決める（ポーズは全部試す）。
        /// スカートなどでは大きく動くポーズで突き抜けるのが普通で、全部を直そうとすると服が大きく膨らんだり脚に貼り付いたりするため。
        /// </summary>
        public bool FixLargePoses;

        /// <summary>ポーズを試すか。</summary>
        public bool IsPoseEnabled(string id)
        {
            if (string.IsNullOrEmpty(PoseCheckDisabled) || string.IsNullOrEmpty(id)) return true;
            foreach (string disabled in PoseCheckDisabled.Split(','))
            {
                if (disabled == id) return false;
            }
            return true;
        }

        /// <summary>ポーズを試すかを変える。</summary>
        public void SetPoseEnabled(string id, bool enabled)
        {
            if (string.IsNullOrEmpty(id) || id.Contains(",")) return;
            var disabled = new List<string>();
            if (!string.IsNullOrEmpty(PoseCheckDisabled))
            {
                foreach (string item in PoseCheckDisabled.Split(','))
                {
                    if (item.Length > 0 && item != id && !disabled.Contains(item)) disabled.Add(item);
                }
            }
            if (!enabled) disabled.Add(id);
            PoseCheckDisabled = string.Join(",", disabled);
        }

        /// <summary>試すポーズ（一覧の順）。</summary>
        public List<PosePreset> GetEnabledPoses()
        {
            var result = new List<PosePreset>();
            foreach (PosePreset preset in PoseLibrary.All)
            {
                if (IsPoseEnabled(preset.Id)) result.Add(preset);
            }
            return result;
        }

        /// <summary>試す段階（半分・全部、またはオフなら全部だけ）。</summary>
        public float[] GetPoseStages() => PoseCheckBothStages ? (float[])PoseLibrary.Stages.Clone() : new[] { 1f };

        /// <summary>動作チェックの調べ方の設定。</summary>
        public PoseCheckOptions BuildPoseCheckOptions()
        {
            return new PoseCheckOptions
            {
                Tolerance = PoseCheckTolerance >= 0f ? Math.Min(PoseCheckTolerance, MaxPoseCheckTolerance) : PoseCheckOptions.DefaultTolerance,
            };
        }

        void ResetPoseCheckDefaults()
        {
            PoseCheckTolerance = PoseCheckOptions.DefaultTolerance;
            PoseCheckBothStages = true;
            PoseCheckDisabled = "";
            ShowPoseCheckDepth = true;
            FixLargePoses = false;
        }

        void SanitizePoseCheck()
        {
            if (!(PoseCheckTolerance >= 0f)) PoseCheckTolerance = float.IsNaN(PoseCheckTolerance) ? PoseCheckOptions.DefaultTolerance : 0f;
            else if (PoseCheckTolerance > MaxPoseCheckTolerance) PoseCheckTolerance = MaxPoseCheckTolerance;
            if (PoseCheckDisabled == null) PoseCheckDisabled = "";
        }

        void LoadPoseCheck(string keyPrefix)
        {
            PoseCheckTolerance = EditorPrefs.GetFloat(keyPrefix + "PoseCheckTolerance", PoseCheckOptions.DefaultTolerance);
            PoseCheckBothStages = EditorPrefs.GetBool(keyPrefix + "PoseCheckBothStages", true);
            PoseCheckDisabled = EditorPrefs.GetString(keyPrefix + "PoseCheckDisabled", "");
            ShowPoseCheckDepth = EditorPrefs.GetBool(keyPrefix + "ShowPoseCheckDepth", true);
            FixLargePoses = EditorPrefs.GetBool(keyPrefix + "PoseCheckFixLargePoses", false);
        }

        void SavePoseCheck(string keyPrefix)
        {
            EditorPrefs.SetFloat(keyPrefix + "PoseCheckTolerance", PoseCheckTolerance);
            EditorPrefs.SetBool(keyPrefix + "PoseCheckBothStages", PoseCheckBothStages);
            EditorPrefs.SetString(keyPrefix + "PoseCheckDisabled", PoseCheckDisabled ?? "");
            EditorPrefs.SetBool(keyPrefix + "ShowPoseCheckDepth", ShowPoseCheckDepth);
            EditorPrefs.SetBool(keyPrefix + "PoseCheckFixLargePoses", FixLargePoses);
        }

        static void DeletePoseCheck(string keyPrefix)
        {
            foreach (string key in new[] { "PoseCheckTolerance", "PoseCheckBothStages", "PoseCheckDisabled", "ShowPoseCheckDepth", "PoseCheckFixLargePoses" }) EditorPrefs.DeleteKey(keyPrefix + key);
        }
    }
}
