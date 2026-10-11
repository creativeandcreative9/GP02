using System;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの一覧の操作で使う設定（左右の軸、書き出し先など）。EditorPrefs に保存し、ウィンドウとインスペクターで共有する。
    /// </summary>
    [Serializable]
    public sealed class ShapeKeyViewSettings
    {
        const string PrefKey = "GP02.ShapeKeyList.Settings";

        // 左右の操作
        public int Axis;                           // 0=X, 1=Y, 2=Z
        public float Center;                       // メッシュ空間の、鏡の位置（メートル）
        public float BlendWidthCm = 4f;            // 左右に分けるときの、中央のなめらかさの幅
        public float ToleranceMm = 2f;             // 鏡像の頂点を探す距離
        public bool LeftIsPositive = true;         // 軸の値が大きい側を _L にする
        public bool KeepPositiveSide = true;       // 左右対称にするとき、軸の値が大きい側を正とする
        public bool AsCopy;                        // 反転・左右対称を、新しいシェイプキーとして加える
        public bool DisableSources = true;         // 合成・分割・in-between のあと、元を無効にする

        // 倍率・in-between
        public float ScalePercent = 150f;
        public ShapeKeyInBetweenMode InBetweenMode = ShapeKeyInBetweenMode.Crossfade;

        // 取り込み
        public float ClipTime;
        public string ClipRendererPath = "";

        // 書き出し
        public string ExportFolder = ShapeKeyExporter.DefaultFolder;
        public bool OverwriteSameName = true;
        public bool RemapBoneWeights;
        public float RemapThresholdCm = 5f;

        // 折りたたみ
        public bool ShowOperations = true;
        public bool ShowOperationSettings;
        public bool ShowImport;
        public bool ShowExport = true;

        public float BlendWidth => Mathf.Max(0f, BlendWidthCm) * 0.01f;
        public float Tolerance => Mathf.Max(0.0001f, ToleranceMm * 0.001f);
        public float RemapThreshold => Mathf.Max(0f, RemapThresholdCm) * 0.01f;

        public static ShapeKeyViewSettings Load()
        {
            var settings = new ShapeKeyViewSettings();
            try
            {
                string json = EditorPrefs.GetString(PrefKey, "");
                if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, settings);
            }
            catch (Exception)
            {
                // 壊れた保存値は無視して既定値を使う
            }
            if (string.IsNullOrEmpty(settings.ExportFolder)) settings.ExportFolder = ShapeKeyExporter.DefaultFolder;
            settings.Axis = Mathf.Clamp(settings.Axis, 0, 2);
            return settings;
        }

        public void Save()
        {
            try { EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(this)); }
            catch (Exception)
            {
                // 設定が保存できなくても、操作は続けられる
            }
        }
    }
}
