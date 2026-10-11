using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 「布の長さが足りなくて、引いている先に届かない」の判定と表示の決まり。
    /// 引いている先と、つまんだ点（選んだ点の中心）の今の位置が、1cm 以上、かつ引いた量の 2 割以上離れたら届いていないとする
    /// （ごくわずかなずれや、動かし始めの遅れで警告が出ないように、両方を満たしたときだけ）。
    /// 距離は <see cref="ClothEditController.ReachDistance"/>。画面に依存しないので、テストで確かめられる。
    /// </summary>
    public static class ClothEditReach
    {
        /// <summary>届いていないとみなす距離の下限（メートル）。</summary>
        public const float WarnDistance = 0.01f;

        /// <summary>届いていないとみなす、引いた量に対する割合の下限。</summary>
        public const float WarnRatio = 0.2f;

        /// <summary>届かない状態が、この秒数以上続いたら線と一言を出す（動かし始めの一瞬の遅れで点滅しないように）。</summary>
        public const double ShowAfterSeconds = 0.12;

        /// <summary>線の色（届かないとき）。</summary>
        public static Color LineColor => new Color(1f, 0.4f, 0.1f, 1f);

        /// <summary>距離（引いている先とつまんだ点の差）と引いた量から、届いていないか。</summary>
        public static bool IsOutOfReach(float distance, float pulled)
        {
            return distance >= WarnDistance && distance >= WarnRatio * pulled;
        }

        /// <summary>シーンビューに出す一言。</summary>
        public static string Message()
        {
            return L10n.Tr("布の長さが足りません — 形だけ動かすか、伸びやすさを上げると届きます",
                "The cloth is not long enough — move the shape only, or raise Stretchiness, to reach");
        }
    }
}
