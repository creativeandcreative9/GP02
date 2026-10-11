using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // 届かないときの見える化（シーンビュー）。「つまむ」「選んで動かす」で、引いている先と、つまんだ点（選んだ点の中心）の今の位置が離れたら、
    // 線で結んで色を変え、一言（ClothEditReach.Message）を案内の下に出す。距離は ClothEditController.ReachDistance。
    public sealed partial class ClothEditWindow
    {
        // Repaint のたびに呼ぶ。届かない状態が少し続いたら線を描き、一言を出す印（_reachShown）を立てる
        void DrawReachLine()
        {
            bool outOfReach = Shows(ClothEditUiItem.ReachWarning) && _controller.IsOutOfReach;
            double now = EditorApplication.timeSinceStartup;
            if (!outOfReach)
            {
                _reachOutSince = double.PositiveInfinity;
                _reachShown = false;
                return;
            }
            if (double.IsPositiveInfinity(_reachOutSince)) _reachOutSince = now;
            _reachShown = now - _reachOutSince >= ClothEditReach.ShowAfterSeconds;
            if (!_reachShown) return;

            Vector3 current = _controller.ReachCurrent;
            Vector3 target = _controller.ReachTarget;
            Handles.color = ClothEditReach.LineColor;
            Handles.DrawLine(current, target, 3f);
            Dot(current, 0.02f);

            // 引いている先に輪を描く（カメラのほうを向けて）
            SceneView view = SceneView.currentDrawingSceneView;
            Vector3 normal = view != null && view.camera != null ? -view.camera.transform.forward : Vector3.up;
            Handles.DrawWireDisc(target, normal, HandleUtility.GetHandleSize(target) * 0.08f, 3f);
        }
    }
}
