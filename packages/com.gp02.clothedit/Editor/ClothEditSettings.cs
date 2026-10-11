using GP02.Core;
using UnityEditor;

namespace GP02.EditorTools
{
    /// <summary>
    /// 触りながら調整するための設定値。ウィンドウの「詳細設定」で変え、EditorPrefs に保存する。
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

        public void ResetToDefaults()
        {
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
        }

        /// <summary>保存した値を消す（次の Load で既定値に戻る）。</summary>
        public static void DeleteSaved(string keyPrefix = DefaultKeyPrefix)
        {
            foreach (string key in new[]
            {
                "BendCompliance", "StretchCompliance", "SettleTolerance", "SettleTimeoutSeconds",
                "SolveMilliseconds", "CoarseIterations", "UseAcceleration", "KeepOutsideRestSurface",
                "BrushCompliance", "UseRestSurfaceAllowance",
            })
            {
                EditorPrefs.DeleteKey(keyPrefix + key);
            }
        }
    }
}
