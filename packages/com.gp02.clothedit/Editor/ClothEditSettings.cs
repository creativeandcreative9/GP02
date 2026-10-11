using System;
using System.Globalization;
using System.Text;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>ブラシの範囲の中の重み（減衰カーブ）の選び方。Custom は <see cref="ClothEditSettings.CustomFalloff"/> を使う。</summary>
    public enum FalloffPreset
    {
        Constant,
        Smooth,
        Linear,
        Sharp,
        Sphere,
        Custom,
    }

    /// <summary>対称編集の軸（アバターのローカル座標）。この軸に垂直な面が鏡になる。</summary>
    public enum SymmetryAxis
    {
        X,
        Y,
        Z,
    }

    /// <summary>つまんだ点を動かす向き。</summary>
    public enum MoveDirection
    {
        /// <summary>視点の平面の上を自由に動かす。</summary>
        Free,

        /// <summary>つまんだ点の面の法線に沿って動かす。</summary>
        SurfaceNormal,

        AxisX,
        AxisY,
        AxisZ,
    }

    /// <summary>X/Y/Z 軸に固定するときの、軸の基準。</summary>
    public enum AxisSpace
    {
        World,

        /// <summary>アバター（<see cref="ClothEditSymmetry.FindAvatarRoot"/>）のローカル座標。</summary>
        AvatarLocal,
    }

    /// <summary>「回す」道具の回転軸。</summary>
    public enum RotateAxisMode
    {
        /// <summary>視線の向き。</summary>
        View,

        /// <summary>つまんだ点の面の法線。</summary>
        SurfaceNormal,
    }

    /// <summary>
    /// 触りながら調整するための設定値。ウィンドウの各区切りで変え、EditorPrefs に保存する。
    /// 画面に依存しないので、<see cref="ClothEditController"/> のテストでも使える（Load しなければ既定値のまま）。
    /// </summary>
    public sealed class ClothEditSettings
    {
        // ClothSolver の既定値と同じにしておく（テストで一致を確かめている）
        public const float DefaultBendCompliance = 0.1f;
        public const float DefaultStretchCompliance = 0f;
        public const float DefaultSettleTolerance = 0.01f;
        public const float DefaultSettleTimeoutSeconds = 2f;
        public const float DefaultSolveMilliseconds = 12f;
        public const int DefaultCoarseIterations = 10;
        public const float DefaultBrushCompliance = ClothSolver.DefaultBrushCompliance;
        public const float MaxBrushCompliance = 0.2f;
        public const float DefaultRelaxStrength = 0.5f;
        public const float MaxSymmetryOffset = 2f;

        /// <summary>伸びのヒートマップで黄色になる伸び（0.05 で 5%）。0 は緑。</summary>
        public const float HeatmapYellow = 0.05f;

        /// <summary>伸びのヒートマップで赤になる伸び（0.15 で 15%）。</summary>
        public const float HeatmapRed = 0.15f;

        const int CurveSampleCount = 64;

        /// <summary>EditorPrefs のキーの先頭。テストでは別の値を渡して、本物の設定を壊さない。</summary>
        public const string DefaultKeyPrefix = "GP02.Settings.";

        /// <summary>曲がりやすさ（<see cref="ClothSolver.BendCompliance"/>）。大きいほど柔らかく折れ曲がる。</summary>
        public float BendCompliance = DefaultBendCompliance;

        /// <summary>伸びやすさ（<see cref="ClothSolver.StretchCompliance"/>）。0 で伸びない。</summary>
        public float StretchCompliance = DefaultStretchCompliance;

        /// <summary>仕上げの終わりとみなす最大の伸び（0.01 で 1%）。</summary>
        public float SettleTolerance = DefaultSettleTolerance;

        /// <summary>仕上げの時間切れ（秒）。伸びが収まらなくても、この時間で打ち切る。</summary>
        public float SettleTimeoutSeconds = DefaultSettleTimeoutSeconds;

        /// <summary>1回の更新で計算に使う時間（ミリ秒）。大きいほど速く収束するが、操作が重くなる。</summary>
        public float SolveMilliseconds = DefaultSolveMilliseconds;

        /// <summary>粗い段1段あたりの反復回数（<see cref="ClothSolver.CoarseIterations"/>）。</summary>
        public int CoarseIterations = DefaultCoarseIterations;

        /// <summary>高速版（Burst など。<see cref="ClothSolver.DefaultKernel"/>）を使うか。オフにすると C# で計算する（比較用）。</summary>
        public bool UseAcceleration = true;

        /// <summary>
        /// 元の形より内側に入らないようにするか（<see cref="ClothSolver.KeepOutsideRestSurface"/>）。
        /// 体や内側の布へのすり抜けを防ぐ。内側へ押し込む編集をしたいときはオフにする。
        /// </summary>
        public bool KeepOutsideRestSurface = true;

        /// <summary>
        /// つまんだ範囲の柔らかさ（<see cref="ClothSolver.BrushCompliance"/>）。大きいほど、布の長さで届かない所へ引いたときに早く手前で止まる。
        /// 0 で、つまんだ所を目標に固定する（届かない所まで連れて行くので、伸びが残る）。
        /// </summary>
        public float BrushCompliance = DefaultBrushCompliance;

        /// <summary>
        /// 体や内側の布までの隙間を使うか。オンなら、隙間の分だけ元の形より内側へ動ける
        /// （<see cref="ClothEditSession.RestAllowance"/> を <see cref="ClothSolver.SetRestSurfaceAllowance"/> に渡す）。
        /// オフなら隙間を 0 にする（元の形より内側へは入らない）。
        /// </summary>
        public bool UseRestSurfaceAllowance = true;

        // ---- 道具・ブラシ・対称・向き・表示（ウィンドウの各区切り） ----

        /// <summary>今の道具。</summary>
        public ClothEditMode Tool = ClothEditMode.Grab;

        /// <summary>減衰カーブの選び方。</summary>
        public FalloffPreset Falloff = FalloffPreset.Constant;

        /// <summary>自由に描いた減衰カーブ（横軸 0〜1 が中心→ブラシ半径、縦軸 0〜1 が重み）。</summary>
        public AnimationCurve CustomFalloff = DefaultCustomFalloff();

        /// <summary>ブラシの範囲を決める距離の測り方。</summary>
        public BrushDistanceMode DistanceMode = BrushDistanceMode.Surface;

        /// <summary>対称編集。</summary>
        public bool SymmetryEnabled;

        /// <summary>対称の軸（アバターのローカル座標）。</summary>
        public SymmetryAxis SymmetryAxis = SymmetryAxis.X;

        /// <summary>鏡の面の、アバターの中心からのずれ（アバターのローカル座標でのメートル）。</summary>
        public float SymmetryOffset;

        /// <summary>つまんだ点を動かす向き。</summary>
        public MoveDirection MoveDirection = MoveDirection.Free;

        /// <summary>X/Y/Z 軸に固定するときの軸の基準。</summary>
        public AxisSpace DirectionSpace = AxisSpace.World;

        /// <summary>「回す」道具の回転軸。</summary>
        public RotateAxisMode RotateAxis = RotateAxisMode.View;

        /// <summary>「元に戻すブラシ」が1回で元の形へ近づける割合（0〜1）。</summary>
        public float RelaxStrength = DefaultRelaxStrength;

        /// <summary>ブラシを当てる前に、範囲に入る粒子を重みで色分けして表示するか。</summary>
        public bool ShowBrushPreview = true;

        /// <summary>伸びのヒートマップを表示するか。</summary>
        public bool ShowStretchHeatmap;

        // 保存しない（編集を始めるたびに 100% の見た目から始めたいため）

        /// <summary>編集中の形と元の形の間の見せ方（0 で元の形、1 で編集中の形）。保存しない。</summary>
        public float CompareBlend = 1f;

        /// <summary>元の形を表示するか。保存しない。</summary>
        public bool ShowOriginal;

        static FalloffCurve _cachedCurve;
        static FalloffPreset _cachedPreset;
        static int _cachedCustomHash;

        /// <summary>初めて描くときの減衰カーブ（中心 1 から端 0 へまっすぐ）。</summary>
        public static AnimationCurve DefaultCustomFalloff()
        {
            return AnimationCurve.Linear(0f, 1f, 1f, 0f);
        }

        /// <summary>プリセットの形を、自由に描けるカーブ（キー付き）にしたもの。選んだプリセットを下書きにして描き足すため。</summary>
        public static AnimationCurve CurveFromPreset(FalloffPreset preset)
        {
            FalloffCurve source = PresetCurve(preset);
            if (source == null) return DefaultCustomFalloff();
            int keys = source.SampleCount <= 2 ? 2 : 17;
            var curve = new AnimationCurve();
            for (int k = 0; k < keys; k++)
            {
                float t = k / (float)(keys - 1);
                curve.AddKey(new Keyframe(t, source.Evaluate(t)));
            }
            for (int k = 0; k < curve.length; k++) SetLinearTangents(curve, k);
            return curve;
        }

        // 折れ線として扱うため、接線を隣のキーへの傾きにする（既定の接線だと、キーの間でふくらんで 0〜1 をはみ出すため）
        static void SetLinearTangents(AnimationCurve curve, int index)
        {
            Keyframe key = curve[index];
            float inTangent = 0f, outTangent = 0f;
            if (index > 0)
            {
                Keyframe previous = curve[index - 1];
                inTangent = (key.value - previous.value) / Mathf.Max(1e-6f, key.time - previous.time);
            }
            if (index < curve.length - 1)
            {
                Keyframe next = curve[index + 1];
                outTangent = (next.value - key.value) / Mathf.Max(1e-6f, next.time - key.time);
            }
            if (index == 0) inTangent = outTangent;
            if (index == curve.length - 1) outTangent = inTangent;
            key.inTangent = inTangent;
            key.outTangent = outTangent;
            curve.MoveKey(index, key);
        }

        static FalloffCurve PresetCurve(FalloffPreset preset)
        {
            switch (preset)
            {
                case FalloffPreset.Constant: return FalloffCurve.Constant;
                case FalloffPreset.Smooth: return FalloffCurve.Smooth;
                case FalloffPreset.Linear: return FalloffCurve.Linear;
                case FalloffPreset.Sharp: return FalloffCurve.Sharp;
                case FalloffPreset.Sphere: return FalloffCurve.Sphere;
                default: return null;
            }
        }

        /// <summary>
        /// 今の選び方の <see cref="FalloffCurve"/>。毎回呼んでよい（プリセットは共有のもの、自由に描いたカーブは変わらない間は同じものを返す）。
        /// Custom は 64 点でサンプルする。
        /// </summary>
        public FalloffCurve GetFalloffCurve()
        {
            FalloffCurve preset = PresetCurve(Falloff);
            if (preset != null) return preset;

            AnimationCurve custom = CustomFalloff ?? DefaultCustomFalloff();
            int hash = HashCurve(custom);
            if (_cachedCurve != null && _cachedPreset == FalloffPreset.Custom && _cachedCustomHash == hash) return _cachedCurve;

            var samples = new float[CurveSampleCount];
            for (int i = 0; i < samples.Length; i++) samples[i] = custom.Evaluate(i / (float)(samples.Length - 1));
            _cachedCurve = FalloffCurve.FromSamples(samples);
            _cachedPreset = FalloffPreset.Custom;
            _cachedCustomHash = hash;
            return _cachedCurve;
        }

        // キーの中身から作る簡単な識別子（キーの配列を作らずに、変わったかどうかだけ見る）
        static int HashCurve(AnimationCurve curve)
        {
            unchecked
            {
                int hash = curve.length;
                for (int i = 0; i < curve.length; i++)
                {
                    Keyframe k = curve[i];
                    hash = hash * 31 + k.time.GetHashCode();
                    hash = hash * 31 + k.value.GetHashCode();
                    hash = hash * 31 + k.inTangent.GetHashCode();
                    hash = hash * 31 + k.outTangent.GetHashCode();
                }
                return hash;
            }
        }

        static string CurveToString(AnimationCurve curve)
        {
            var text = new StringBuilder();
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe k = curve[i];
                if (i > 0) text.Append(';');
                text.Append(k.time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(k.value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(SafeTangent(k.inTangent).ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(SafeTangent(k.outTangent).ToString("R", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // 無限大の接線（階段状の変化）は、保存の文字列を壊さないよう大きな値にする
        static float SafeTangent(float tangent)
        {
            if (float.IsNaN(tangent)) return 0f;
            return Mathf.Clamp(tangent, -1e6f, 1e6f);
        }

        static AnimationCurve CurveFromString(string text)
        {
            if (string.IsNullOrEmpty(text)) return DefaultCustomFalloff();
            var curve = new AnimationCurve();
            foreach (string part in text.Split(';'))
            {
                string[] f = part.Split(',');
                if (f.Length != 4) return DefaultCustomFalloff();
                if (!float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float time)
                    || !float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float inTangent)
                    || !float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float outTangent))
                {
                    return DefaultCustomFalloff();
                }
                curve.AddKey(new Keyframe(time, value, inTangent, outTangent));
            }
            return curve.length > 0 ? curve : DefaultCustomFalloff();
        }

        public void ResetToDefaults()
        {
            Tool = ClothEditMode.Grab;
            Falloff = FalloffPreset.Constant;
            CustomFalloff = DefaultCustomFalloff();
            DistanceMode = BrushDistanceMode.Surface;
            SymmetryEnabled = false;
            SymmetryAxis = SymmetryAxis.X;
            SymmetryOffset = 0f;
            MoveDirection = MoveDirection.Free;
            DirectionSpace = AxisSpace.World;
            RotateAxis = RotateAxisMode.View;
            RelaxStrength = DefaultRelaxStrength;
            ShowBrushPreview = true;
            ShowStretchHeatmap = false;
            CompareBlend = 1f;
            ShowOriginal = false;

            BendCompliance = DefaultBendCompliance;
            StretchCompliance = DefaultStretchCompliance;
            SettleTolerance = DefaultSettleTolerance;
            SettleTimeoutSeconds = DefaultSettleTimeoutSeconds;
            SolveMilliseconds = DefaultSolveMilliseconds;
            CoarseIterations = DefaultCoarseIterations;
            UseAcceleration = true;
            KeepOutsideRestSurface = true;
            BrushCompliance = DefaultBrushCompliance;
            UseRestSurfaceAllowance = true;
        }

        /// <summary>負の値や、計算を壊す値を直す。</summary>
        public void Sanitize()
        {
            if (!(BendCompliance >= 0f)) BendCompliance = DefaultBendCompliance;       // NaN も既定に戻る
            if (!(StretchCompliance >= 0f)) StretchCompliance = DefaultStretchCompliance;
            if (!(SettleTolerance >= 0f)) SettleTolerance = DefaultSettleTolerance;
            if (!(SettleTimeoutSeconds >= 0f)) SettleTimeoutSeconds = DefaultSettleTimeoutSeconds;
            if (!(SolveMilliseconds >= 0f)) SolveMilliseconds = DefaultSolveMilliseconds;
            if (CoarseIterations < 0) CoarseIterations = DefaultCoarseIterations;
            if (!(BrushCompliance >= 0f)) BrushCompliance = DefaultBrushCompliance;
            else if (BrushCompliance > MaxBrushCompliance) BrushCompliance = MaxBrushCompliance;

            if (!Enum.IsDefined(typeof(ClothEditMode), Tool)) Tool = ClothEditMode.Grab;
            if (!Enum.IsDefined(typeof(FalloffPreset), Falloff)) Falloff = FalloffPreset.Constant;
            if (CustomFalloff == null || CustomFalloff.length == 0) CustomFalloff = DefaultCustomFalloff();
            if (!Enum.IsDefined(typeof(BrushDistanceMode), DistanceMode)) DistanceMode = BrushDistanceMode.Surface;
            if (!Enum.IsDefined(typeof(SymmetryAxis), SymmetryAxis)) SymmetryAxis = SymmetryAxis.X;
            if (!(Mathf.Abs(SymmetryOffset) <= MaxSymmetryOffset)) SymmetryOffset = float.IsNaN(SymmetryOffset) ? 0f : Mathf.Clamp(SymmetryOffset, -MaxSymmetryOffset, MaxSymmetryOffset);
            if (!Enum.IsDefined(typeof(MoveDirection), MoveDirection)) MoveDirection = MoveDirection.Free;
            if (!Enum.IsDefined(typeof(AxisSpace), DirectionSpace)) DirectionSpace = AxisSpace.World;
            if (!Enum.IsDefined(typeof(RotateAxisMode), RotateAxis)) RotateAxis = RotateAxisMode.View;
            if (!(RelaxStrength >= 0f)) RelaxStrength = DefaultRelaxStrength;
            else if (RelaxStrength > 1f) RelaxStrength = 1f;
            if (!(CompareBlend >= 0f)) CompareBlend = 1f;
            else if (CompareBlend > 1f) CompareBlend = 1f;
        }

        /// <summary>
        /// 計算に関わる値（曲がりやすさなど）をソルバーに反映する。
        /// </summary>
        /// <param name="restAllowance">
        /// 測った、粒子ごとの隙間（<see cref="ClothEditSession.RestAllowance"/>）。
        /// <see cref="UseRestSurfaceAllowance"/> がオンのときだけソルバーに渡し、オフか null なら隙間を 0 にする
        /// </param>
        public void ApplyTo(ClothSolver solver, float[] restAllowance = null)
        {
            if (solver == null) return;
            Sanitize();
            solver.BendCompliance = BendCompliance;
            solver.StretchCompliance = StretchCompliance;
            solver.CoarseIterations = CoarseIterations;
            solver.Kernel = UseAcceleration ? ClothSolver.DefaultKernel : null;
            solver.KeepOutsideRestSurface = KeepOutsideRestSurface;
            solver.BrushCompliance = BrushCompliance;
            solver.SetRestSurfaceAllowance(UseRestSurfaceAllowance ? restAllowance : null);
        }

        public static ClothEditSettings Load(string keyPrefix = DefaultKeyPrefix)
        {
            var settings = new ClothEditSettings
            {
                BendCompliance = EditorPrefs.GetFloat(keyPrefix + "BendCompliance", DefaultBendCompliance),
                StretchCompliance = EditorPrefs.GetFloat(keyPrefix + "StretchCompliance", DefaultStretchCompliance),
                SettleTolerance = EditorPrefs.GetFloat(keyPrefix + "SettleTolerance", DefaultSettleTolerance),
                SettleTimeoutSeconds = EditorPrefs.GetFloat(keyPrefix + "SettleTimeoutSeconds", DefaultSettleTimeoutSeconds),
                SolveMilliseconds = EditorPrefs.GetFloat(keyPrefix + "SolveMilliseconds", DefaultSolveMilliseconds),
                CoarseIterations = EditorPrefs.GetInt(keyPrefix + "CoarseIterations", DefaultCoarseIterations),
                UseAcceleration = EditorPrefs.GetBool(keyPrefix + "UseAcceleration", true),
                KeepOutsideRestSurface = EditorPrefs.GetBool(keyPrefix + "KeepOutsideRestSurface", true),
                BrushCompliance = EditorPrefs.GetFloat(keyPrefix + "BrushCompliance", DefaultBrushCompliance),
                UseRestSurfaceAllowance = EditorPrefs.GetBool(keyPrefix + "UseRestSurfaceAllowance", true),
                Tool = (ClothEditMode)EditorPrefs.GetInt(keyPrefix + "Tool", (int)ClothEditMode.Grab),
                Falloff = (FalloffPreset)EditorPrefs.GetInt(keyPrefix + "Falloff", (int)FalloffPreset.Constant),
                CustomFalloff = CurveFromString(EditorPrefs.GetString(keyPrefix + "CustomFalloff", "")),
                DistanceMode = (BrushDistanceMode)EditorPrefs.GetInt(keyPrefix + "DistanceMode", (int)BrushDistanceMode.Surface),
                SymmetryEnabled = EditorPrefs.GetBool(keyPrefix + "SymmetryEnabled", false),
                SymmetryAxis = (SymmetryAxis)EditorPrefs.GetInt(keyPrefix + "SymmetryAxis", (int)SymmetryAxis.X),
                SymmetryOffset = EditorPrefs.GetFloat(keyPrefix + "SymmetryOffset", 0f),
                MoveDirection = (MoveDirection)EditorPrefs.GetInt(keyPrefix + "MoveDirection", (int)MoveDirection.Free),
                DirectionSpace = (AxisSpace)EditorPrefs.GetInt(keyPrefix + "DirectionSpace", (int)AxisSpace.World),
                RotateAxis = (RotateAxisMode)EditorPrefs.GetInt(keyPrefix + "RotateAxis", (int)RotateAxisMode.View),
                RelaxStrength = EditorPrefs.GetFloat(keyPrefix + "RelaxStrength", DefaultRelaxStrength),
                ShowBrushPreview = EditorPrefs.GetBool(keyPrefix + "ShowBrushPreview", true),
                ShowStretchHeatmap = EditorPrefs.GetBool(keyPrefix + "ShowStretchHeatmap", false),
            };
            settings.Sanitize();
            return settings;
        }

        public void Save(string keyPrefix = DefaultKeyPrefix)
        {
            EditorPrefs.SetFloat(keyPrefix + "BendCompliance", BendCompliance);
            EditorPrefs.SetFloat(keyPrefix + "StretchCompliance", StretchCompliance);
            EditorPrefs.SetFloat(keyPrefix + "SettleTolerance", SettleTolerance);
            EditorPrefs.SetFloat(keyPrefix + "SettleTimeoutSeconds", SettleTimeoutSeconds);
            EditorPrefs.SetFloat(keyPrefix + "SolveMilliseconds", SolveMilliseconds);
            EditorPrefs.SetInt(keyPrefix + "CoarseIterations", CoarseIterations);
            EditorPrefs.SetBool(keyPrefix + "UseAcceleration", UseAcceleration);
            EditorPrefs.SetBool(keyPrefix + "KeepOutsideRestSurface", KeepOutsideRestSurface);
            EditorPrefs.SetFloat(keyPrefix + "BrushCompliance", BrushCompliance);
            EditorPrefs.SetBool(keyPrefix + "UseRestSurfaceAllowance", UseRestSurfaceAllowance);
            EditorPrefs.SetInt(keyPrefix + "Tool", (int)Tool);
            EditorPrefs.SetInt(keyPrefix + "Falloff", (int)Falloff);
            EditorPrefs.SetString(keyPrefix + "CustomFalloff", CurveToString(CustomFalloff ?? DefaultCustomFalloff()));
            EditorPrefs.SetInt(keyPrefix + "DistanceMode", (int)DistanceMode);
            EditorPrefs.SetBool(keyPrefix + "SymmetryEnabled", SymmetryEnabled);
            EditorPrefs.SetInt(keyPrefix + "SymmetryAxis", (int)SymmetryAxis);
            EditorPrefs.SetFloat(keyPrefix + "SymmetryOffset", SymmetryOffset);
            EditorPrefs.SetInt(keyPrefix + "MoveDirection", (int)MoveDirection);
            EditorPrefs.SetInt(keyPrefix + "DirectionSpace", (int)DirectionSpace);
            EditorPrefs.SetInt(keyPrefix + "RotateAxis", (int)RotateAxis);
            EditorPrefs.SetFloat(keyPrefix + "RelaxStrength", RelaxStrength);
            EditorPrefs.SetBool(keyPrefix + "ShowBrushPreview", ShowBrushPreview);
            EditorPrefs.SetBool(keyPrefix + "ShowStretchHeatmap", ShowStretchHeatmap);
        }

        /// <summary>保存した値を消す（次の Load で既定値に戻る）。</summary>
        public static void DeleteSaved(string keyPrefix = DefaultKeyPrefix)
        {
            foreach (string key in new[]
            {
                "BendCompliance", "StretchCompliance", "SettleTolerance", "SettleTimeoutSeconds",
                "SolveMilliseconds", "CoarseIterations", "UseAcceleration", "KeepOutsideRestSurface",
                "BrushCompliance", "UseRestSurfaceAllowance",
                "Tool", "Falloff", "CustomFalloff", "DistanceMode", "SymmetryEnabled", "SymmetryAxis", "SymmetryOffset",
                "MoveDirection", "DirectionSpace", "RotateAxis", "RelaxStrength", "ShowBrushPreview", "ShowStretchHeatmap",
            })
            {
                EditorPrefs.DeleteKey(keyPrefix + key);
            }
        }
    }
}
