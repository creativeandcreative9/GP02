using GP02.Core;
using UnityEditor;

namespace GP02.EditorTools
{
    // 貫通（体が布を突き抜けている所）の検出と押し出しの設定。
    public sealed partial class ClothEditSettings
    {
        /// <summary>体から保つ余白の既定値（メートル）。</summary>
        public const float DefaultPenetrationMargin = PenetrationOptions.DefaultMargin;

        /// <summary>体から保つ余白の上限（メートル）。</summary>
        public const float MaxPenetrationMargin = 0.01f;

        /// <summary>「貫通を直す」の時間の上限（ミリ秒）。粒子が多く Burst が無いと、仕上げに時間がかかるため。</summary>
        public const double PenetrationFixMilliseconds = 10000.0;

        /// <summary>
        /// 体から保つ余白（メートル、0〜<see cref="MaxPenetrationMargin"/>）。これより体に近い布も「直す」で押し出し、
        /// 編集中も布はこれより体に近づかない（<see cref="ClothSolver.BodyMargin"/>）。
        /// </summary>
        public float PenetrationMargin = DefaultPenetrationMargin;

        /// <summary>貫通の検出と押し出しの設定（余白と、仕上げの許容する伸び）。</summary>
        public PenetrationOptions BuildPenetrationOptions()
        {
            return new PenetrationOptions
            {
                Margin = SafePenetrationMargin(),
                StretchTolerance = SettleTolerance >= 0f ? SettleTolerance : DefaultSettleTolerance,
                TimeLimitMilliseconds = PenetrationFixMilliseconds,
            };
        }

        // 直していない値（負・NaN・上限超え）でも計算を壊さない余白
        float SafePenetrationMargin() => PenetrationMargin >= 0f ? System.Math.Min(PenetrationMargin, MaxPenetrationMargin) : DefaultPenetrationMargin;

        void ResetPenetrationDefaults()
        {
            PenetrationMargin = DefaultPenetrationMargin;
        }

        void SanitizePenetration()
        {
            if (!(PenetrationMargin >= 0f)) PenetrationMargin = float.IsNaN(PenetrationMargin) ? DefaultPenetrationMargin : 0f;
            else if (PenetrationMargin > MaxPenetrationMargin) PenetrationMargin = MaxPenetrationMargin;
        }

        void ApplyPenetrationTo(ClothSolver solver)
        {
            solver.BodyMargin = SafePenetrationMargin();
        }

        void LoadPenetration(string keyPrefix)
        {
            PenetrationMargin = EditorPrefs.GetFloat(keyPrefix + "PenetrationMargin", DefaultPenetrationMargin);
        }

        void SavePenetration(string keyPrefix)
        {
            EditorPrefs.SetFloat(keyPrefix + "PenetrationMargin", PenetrationMargin);
        }

        static void DeletePenetration(string keyPrefix)
        {
            EditorPrefs.DeleteKey(keyPrefix + "PenetrationMargin");
        }
    }
}
