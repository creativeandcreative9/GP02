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

        // 一覧の表示
        public string FilterText = "";             // 名前の検索
        public ShapeKeyListSort Sort;              // 表示の並べ替え（データの並びは変えない）

        // 左右の操作
        public bool SplitInAvatarSpace;            // 左右に分割する軸を、アバターのローカル軸で見る（オフならメッシュ空間）
        public int Axis;                           // 0=X, 1=Y, 2=Z
        public float Center;                       // メッシュ空間の、鏡の位置（メートル）
        public float BlendWidthCm = 4f;            // 左右に分けるときの、中央のなめらかさの幅
        public float ToleranceMm = 2f;             // 鏡像の頂点を探す距離
        public bool LeftIsPositive = true;         // 軸の値が大きい側を _L にする
        public bool KeepPositiveSide = true;       // 左右対称にするとき、軸の値が大きい側を正とする
        public bool AsCopy;                        // 反転・左右対称を、新しいシェイプキーとして加える
        public bool DisableSources = true;         // 合成・分割・in-between のあと、元を無効にする

        // 法線の計算し直し
        public float NormalSmoothAngle = 180f;     // 同じ位置の頂点の面を、なめらかにつなぐ角度（180 で全部）
        public bool NormalMergeCoincident = true;  // 同じ位置の頂点（UV の継ぎ目など）を結合して計算する

        // 倍率・in-between
        public float ScalePercent = 150f;
        public ShapeKeyInBetweenMode InBetweenMode = ShapeKeyInBetweenMode.Crossfade;

        // 選んだ点の範囲（マスク）・差し引き・別のメッシュへ写す
        public int MaskSmoothIterations = 2;       // 範囲の境目をなめらかにする回数
        public bool MaskAsCopy = true;             // 取り出す・消すを、別のシェイプキーにする
        public bool SubtractSwap;                  // 差し引きを B - A にする
        public float TransferMaxDistanceCm = 2f;   // 別のメッシュへ写すときの、距離の上限
        public bool TransferAvoidBackFaces = true; // 法線が逆向きの面を避ける

        // 取り込み
        public float ClipTime;
        public string ClipRendererPath = "";
        public bool ImportOverwrite;               // 取り込みで同じ名前は上書き（前の形は履歴に残る）

        // 書き出し
        public string ExportFolder = ShapeKeyExporter.DefaultFolder;
        public bool OverwriteSameName = true;
        public bool RemapBoneWeights;
        public float RemapThresholdCm = 5f;
        public BoneWeightRemapMode RemapMode;
        public int RemapSmoothIterations;
        public bool CreateClips;                   // 書き出しと一緒にトグル用の AnimationClip を作る
        public string ClipFolder = ShapeKeyAnimationExporter.DefaultFolder;

        // 折りたたみ
        public bool ShowOperations = true;
        public bool ShowOperationSettings;
        public bool ShowImport;
        public bool ShowExport = true;
        public bool ShowDetails = true;
        public bool ShowExportPlan = true;
        public bool ShowRangeTools;
        public bool ShowTransfer;
        public bool ShowBackups;

        public float BlendWidth => Mathf.Max(0f, BlendWidthCm) * 0.01f;
        public float Tolerance => Mathf.Max(0.0001f, ToleranceMm * 0.001f);
        public float RemapThreshold => Mathf.Max(0f, RemapThresholdCm) * 0.01f;
        public float TransferMaxDistance => Mathf.Max(0f, TransferMaxDistanceCm) * 0.01f;

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
            if (string.IsNullOrEmpty(settings.ClipFolder)) settings.ClipFolder = ShapeKeyAnimationExporter.DefaultFolder;
            // 検索の文字は次に開いたときに残らないほうが、「一覧が空に見える」混乱が無い
            settings.FilterText = "";
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
