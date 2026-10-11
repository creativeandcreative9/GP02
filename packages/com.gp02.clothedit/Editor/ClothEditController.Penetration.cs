using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 貫通（体が布を突き抜けている所）を調べる・直す操作。画面に依存しないので、EditMode テストから動かせる。
    // 編集を始めたとき（保存後の作り直しも）と、直したあと、Undo / Redo のあとに調べ直す。
    public sealed partial class ClothEditController
    {
        /// <summary>体（同じアバターのほかのレンダラーのうち、布の内側にあるもの）が見つかったか。編集中でなければ false。</summary>
        public bool HasBody => _session != null && _session.Body != null;

        /// <summary>最後に調べた貫通の結果。編集中でない・まだ調べていなければ null。</summary>
        public PenetrationReport LastPenetration => _session?.LastPenetration;

        /// <summary>最後に直した結果。編集中でない・まだ直していなければ null。</summary>
        public PenetrationFixResult LastPenetrationFix => _session?.LastPenetrationFix;

        /// <summary>調べたあとに形が変わったか（<see cref="LastPenetration"/> が古いかもしれない）。</summary>
        public bool IsPenetrationStale => _session != null && _session.IsPenetrationStale;

        /// <summary>設定（余白など）に従った、貫通の検出と押し出しの設定。</summary>
        public PenetrationOptions BuildPenetrationOptions() => _settings.BuildPenetrationOptions();

        /// <summary>
        /// 今の形で、体が布を突き抜けている所を調べる（形は変えない。Undo にも入れない）。編集中でなければ null。
        /// </summary>
        public PenetrationReport DetectPenetration()
        {
            if (!EnsureAlive()) return null;
            PenetrationReport report = _session.DetectPenetration(BuildPenetrationOptions());
            ClothEditLog.Info("[GP02] " + _session.DescribeTargets() + ": " + report);
            Notify();
            return report;
        }

        /// <summary>
        /// 体を集め直してから調べる（「調べ直す」）。体のシェイプキーの重みやポーズを変えたあとは、これで新しい体の形になる。
        /// 途中の操作は確定してから行う。編集中でなければ null。
        /// </summary>
        public PenetrationReport RecheckPenetration()
        {
            if (!EnsureAlive()) return null;
            FinishStroke();
            _session.RebuildBody();
            return DetectPenetration();
        }

        /// <summary>
        /// 体が布を突き抜けている所を、布の長さを保ったまま体の外（余白）へ押し出す。周りの布も布の計算で付いてくる。
        /// 「調べる→押し出す→仕上げ」を繰り返し、仕上げまで行う。Unity の Undo の1段。ピン留めの粒子は動かさない。
        /// 直したあとも、布は体の中へ入らない（つまんで押し込んでも、余白で止まる）。
        /// 残った突き抜けている粒子の数を返す（ピン留めで押し出せなかったものを含む）。編集中でなければ -1。
        /// </summary>
        public int FixPenetration()
        {
            if (!EnsureAlive()) return -1;
            FinishStroke();
            PenetrationOptions options = BuildPenetrationOptions();
            if (ShowProgress)
            {
                options.Progress = progress => !EditorUtility.DisplayCancelableProgressBar("GP02",
                    L10n.Tr("体が布を突き抜けている所を、布で押し出しています…", "Pushing the cloth out of the body..."), progress);
            }
            BeginOperation(L10n.Tr("GP02 貫通を直す", "GP02 Fix Penetration"));
            PenetrationFixResult result;
            try
            {
                result = _session.FixPenetration(options);
            }
            finally
            {
                if (ShowProgress) EditorUtility.ClearProgressBar();
            }
            CommitOperation();
            AfterShapeChanged();
            ClothEditLog.Info($"[GP02] {_session.DescribeTargets()}: 貫通を直しました。{result}（{DescribeAccelerationForLog()}）");
            return result.RemainingPenetrating;
        }

        string DescribeAccelerationForLog()
        {
            IClothSolverKernel kernel = _session.Solver.Kernel;
            return kernel != null && kernel.IsAccelerated ? "高速化 " + kernel.Name : "高速化なし（C#）";
        }

        // Undo / Redo で形が戻ったら、調べた結果も今の形で調べ直す（直したのを戻すと、赤い点がまた出る）。OnUndoRedoPerformed から呼ぶ
        void RedetectPenetrationIfStale()
        {
            if (_session == null || _session.Body == null || _session.LastPenetration == null || !_session.IsPenetrationStale) return;
            _session.DetectPenetration(BuildPenetrationOptions());
        }

        // 編集を始めたとき（保存後の作り直しも）に1回調べる（StartSession から呼ぶ）
        void DetectPenetrationOnStart()
        {
            PenetrationReport report = _session.DetectPenetration(BuildPenetrationOptions());
            if (report.HasBody) ClothEditLog.Info("[GP02] " + _session.DescribeTargets() + ": " + report);
        }
    }
}
