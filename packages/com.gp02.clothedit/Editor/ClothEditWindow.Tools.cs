using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // Shortcut Manager からの動作、F キーで半径を変える操作、3D カーソル、名前を付けたカーブ、シーンビューの比較・対称の表示。
    public sealed partial class ClothEditWindow
    {
        const int MaxCompareDots = 3000;         // 動いた量・左右差・相手のいない点の、描く点の上限

        readonly ClothEditRadiusDrag _radiusDrag = new ClothEditRadiusDrag();
        bool _swallowMouseUp;                    // F の確定・取り消しや、カーソルを置いたクリックの、続く離す操作を使う（道具が動かないように）
        int _swallowButton;
        Vector2 _lastSceneMouse;                 // シーンビューの最後のマウスの位置（Shortcut Manager からの呼び出しには位置が無いため）
        bool _hasSceneMouse;
        readonly List<Vector3> _compareDots = new List<Vector3>();
        readonly List<float> _compareValues = new List<float>();
        ClothEditCurveLibrary _curveLibrary;
        [SerializeField] string _curveName = "";
        [SerializeField] int _curvePick;

        // ---- Shortcut Manager ----

        // 編集中だけ GP02 のショートカットを有効にする（OnUpdate から毎回呼ぶ。変わったときだけ登録・解除される）
        void SyncShortcutContext()
        {
            ClothEditShortcutBindings.SetEditing(_controller != null && _controller.IsEditing);
        }

        // Shortcut Manager から呼ばれる（ClothEditShortcutBindings.Handler）
        void OnShortcut(ClothEditShortcutAction action, bool pressed)
        {
            if (_controller == null || !_controller.IsEditing) return;
            if (action == ClothEditShortcutAction.HoldOriginal)
            {
                _controller.HoldOriginal = pressed;
                return;
            }
            if (!pressed) return;
            if (action == ClothEditShortcutAction.ToggleHelp)
            {
                _showShortcuts = !_showShortcuts;
                Repaint();
                SceneView.RepaintAll();
                return;
            }
            if (action == ClothEditShortcutAction.RadiusDrag || action == ClothEditShortcutAction.InfluenceDrag)
            {
                ToggleRadiusDrag(action == ClothEditShortcutAction.InfluenceDrag);
                return;
            }
            // ドラッグ中（押している間）は、道具や半径を変えない
            if (GUIUtility.hotControl != 0 || _controller.IsDragging || _controller.IsTransformingSelection) return;
            RememberToolBeforeSelect();
            ToolKeyResult result = ClothEditShortcuts.ResolveToolAction(action, _settings.Tool, _toolBeforeSelect, _settings.HandleType);
            if (result.Handled)
            {
                ApplyToolKey(result);
                return;
            }
            if (ClothEditShortcuts.TryGetRadiusAction(action, out WheelTarget target, out int step))
            {
                ChangeRadius(target, step);
                RefreshHoverAtLastMouse();
            }
        }

        void ApplyToolKey(ToolKeyResult result)
        {
            SetToolFromKey(result);
            RefreshHoverAtLastMouse();
            Repaint();
            SceneView.RepaintAll();
        }

        // キーで道具を切り替えた結果を設定に入れる。P はピン留め、Shift+P は「塗ると外す」をオンにしたピン留め（昔の「ピン解除」）
        void SetToolFromKey(ToolKeyResult result)
        {
            ClothEditShortcuts.ResolvePinMode(result.Tool, _settings.PinEraseMode, out ClothEditMode tool, out bool erase);
            _settings.Tool = tool;
            _settings.PinEraseMode = erase;
            _settings.HandleType = result.Handle;
            _settings.Save();
            _controller.ApplySettings();
        }

        void RefreshHoverAtLastMouse()
        {
            if (!_hasSceneMouse || !_controller.IsEditing || _controller.IsBusy) return;
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null) return;
            _controller.Hover(MouseRay(sceneView, _lastSceneMouse));
        }

        // HandleUtility.GUIPointToWorldRay はシーンビューの描画の中でしか使えないので、カメラから直接作る
        static Ray MouseRay(SceneView sceneView, Vector2 guiPoint)
        {
            Camera camera = sceneView.camera;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            var screen = new Vector3(guiPoint.x * ppp, camera.pixelHeight - guiPoint.y * ppp, 0f);
            return camera.ScreenPointToRay(screen);
        }

        // ---- F キーで半径を変える ----

        float CurrentRadiusValue(RadiusDragTarget target, out float min, out float max)
        {
            switch (target)
            {
                case RadiusDragTarget.InfluenceRadius:
                    min = InfluenceMinCm;
                    max = InfluenceMaxCm;
                    return _influenceCentimeters;
                case RadiusDragTarget.SelectionSmoothness:
                    min = 0f;
                    max = ClothEditSettings.MaxSelectionFalloffRadius * 100f;
                    return _settings.SelectionFalloffRadius * 100f;
                default:
                    min = BrushMinCm;
                    max = BrushMaxCm;
                    return _brushCentimeters;
            }
        }

        void SetRadiusValue(RadiusDragTarget target, float centimeters)
        {
            switch (target)
            {
                case RadiusDragTarget.InfluenceRadius:
                    _influenceCentimeters = centimeters;
                    break;
                case RadiusDragTarget.SelectionSmoothness:
                    _settings.SelectionFalloffRadius = centimeters * 0.01f;
                    _settings.Sanitize();
                    break;
                default:
                    _brushCentimeters = centimeters;
                    break;
            }
            SyncController();
            _controller.InvalidatePreview();
            Repaint();
            SceneView.RepaintAll();
        }

        // F（Shift+F）: 始める。変えている途中なら確定する（もう一度 F）
        void ToggleRadiusDrag(bool shift)
        {
            if (_radiusDrag.Active)
            {
                FinishRadiusDrag(true);
                return;
            }
            if (_controller.IsBusy || GUIUtility.hotControl != 0) return;
            RadiusDragTarget target = ClothEditRadiusDrag.TargetFor(shift, _settings.Tool);
            float current = CurrentRadiusValue(target, out float min, out float max);
            _radiusDrag.Begin(target, _hasSceneMouse ? _lastSceneMouse.x : 0f, current, min, max);
            SceneView.RepaintAll();
        }

        void FinishRadiusDrag(bool confirm)
        {
            if (!_radiusDrag.Active) return;
            RadiusDragTarget target = _radiusDrag.Target;
            float value = confirm ? _radiusDrag.Confirm() : _radiusDrag.Cancel();
            SetRadiusValue(target, value);
            if (target == RadiusDragTarget.SelectionSmoothness)
            {
                _settings.Save();
                _controller.ApplySettings();
            }
            _radiusChangedAt = EditorApplication.timeSinceStartup;
        }

        // シーンビューのイベントを、半径を変えている間・カーソルを置く操作のために先に読む。使ったら true
        bool HandleRadiusDragAndCursor(SceneView sceneView, Event e)
        {
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                _lastSceneMouse = e.mousePosition;
                _hasSceneMouse = true;
            }

            // 確定・取り消しのクリックの、続く離す操作（とコンテキストメニュー）を使う。次に押したときには忘れる
            if (e.type == EventType.MouseDown) _swallowMouseUp = false;
            if (_swallowMouseUp)
            {
                if (e.type == EventType.ContextClick)
                {
                    _swallowMouseUp = false;
                    e.Use();
                    return true;
                }
                if ((e.type == EventType.MouseUp || e.type == EventType.MouseDrag) && e.button == _swallowButton)
                {
                    // 右ボタンは、離したあとにコンテキストメニューが来ることがあるので、次に押すまで覚えておく
                    if (e.type == EventType.MouseUp && _swallowButton != 1) _swallowMouseUp = false;
                    e.Use();
                    return true;
                }
            }

            if (_radiusDrag.Active)
            {
                switch (e.type)
                {
                    case EventType.MouseMove:
                    case EventType.MouseDrag:
                        SetRadiusValue(_radiusDrag.Target, _radiusDrag.Update(e.mousePosition.x));
                        e.Use();
                        return true;
                    case EventType.MouseDown:
                        FinishRadiusDrag(e.button == 0);
                        Swallow(e.button);
                        e.Use();
                        return true;
                    case EventType.KeyDown:
                        if (e.keyCode == KeyCode.Escape)
                        {
                            FinishRadiusDrag(false);
                            e.Use();
                            return true;
                        }
                        if (!ClothEditShortcutBindings.IsActive && ClothEditShortcuts.IsRadiusDragKey(e.keyCode))
                        {
                            FinishRadiusDrag(true);
                            e.Use();
                            return true;
                        }
                        if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                        {
                            FinishRadiusDrag(true);
                            e.Use();
                            return true;
                        }
                        break;
                }
                return false;
            }

            // Shift+右クリック: 3D カーソルを布の上に置く
            if (e.type == EventType.MouseDown && e.button == 1 && e.shift && !e.alt && !e.control)
            {
                if (_controller.PlaceCursor(MouseRay(sceneView, e.mousePosition)))
                {
                    Swallow(1);
                    e.Use();
                    sceneView.Repaint();
                    return true;
                }
            }
            return false;
        }

        void Swallow(int button)
        {
            _swallowMouseUp = true;
            _swallowButton = button;
        }

        // 半径を変えている間の円と数値
        void DrawRadiusDrag()
        {
            if (!_radiusDrag.Active) return;
            float centimeters = _radiusDrag.Value;
            Vector3 center;
            Vector3 normal;
            if (_radiusDrag.Target == RadiusDragTarget.SelectionSmoothness && _controller.SelectionCount > 0)
            {
                center = _controller.SelectionPivot;
                normal = SceneView.lastActiveSceneView != null ? -SceneView.lastActiveSceneView.camera.transform.forward : Vector3.up;
            }
            else
            {
                center = _controller.HoverPoint;
                normal = _controller.HoverNormal;
            }
            Color color = _radiusDrag.Target == RadiusDragTarget.InfluenceRadius ? new Color(1f, 0.8f, 0.3f, 1f) : ToolColor(_controller.Mode);
            Handles.color = color;
            Handles.DrawWireDisc(center, normal, centimeters * 0.01f, 2f);
            Handles.color = new Color(color.r, color.g, color.b, 0.4f);
            Handles.DrawWireDisc(center, normal, _radiusDrag.StartValue * 0.01f);
            string what = _radiusDrag.Target == RadiusDragTarget.InfluenceRadius ? L10n.Tr("影響範囲", "Influence")
                : _radiusDrag.Target == RadiusDragTarget.SelectionSmoothness ? L10n.Tr("なめらかさの半径", "Smoothness") : L10n.Tr("ブラシ半径", "Brush radius");
            Handles.Label(center + normal * 0.01f, L10n.Tr($"{what} {centimeters:F1} cm（クリック / F: 確定、Esc / 右クリック: 取り消し）",
                $"{what} {centimeters:F1} cm (click / F: confirm, Esc / right-click: cancel)"));
        }

        // ---- 3D カーソル ----

        void DrawCursor()
        {
            if (!_controller.HasCursor) return;
            Vector3 p = _controller.CursorPosition;
            Vector3 n = _controller.CursorNormal;
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(n, u);
            float size = HandleUtility.GetHandleSize(p) * 0.18f;
            Handles.color = Color.white;
            Handles.DrawWireDisc(p, n, size * 0.55f, 2f);
            Handles.color = ClothEditHelp.CursorColor;
            Handles.DrawLine(p - u * size, p + u * size, 2f);
            Handles.DrawLine(p - v * size, p + v * size, 2f);
            Handles.DrawWireDisc(p, n, size * 0.45f, 2f);
        }

        // ---- 名前を付けたカーブ ----

        void DrawCurveLibrary()
        {
            if (_curveLibrary == null) _curveLibrary = new ClothEditCurveLibrary();
            using (new EditorGUILayout.HorizontalScope())
            {
                _curveName = EditorGUILayout.TextField(new GUIContent(L10n.Tr("カーブの名前", "Curve Name"),
                    L10n.Tr($"今のカーブに名前を付けて保存します（{ClothEditCurveLibrary.MaxCount} 個まで。同じ名前は上書き）", $"Saves the current curve under a name (up to {ClothEditCurveLibrary.MaxCount}; the same name overwrites)")), _curveName);
                if (GUILayout.Button(new GUIContent(L10n.Tr("保存", "Save"), L10n.Tr("今のカーブを、この名前で保存します（ほかのプロジェクトでも使えます）", "Saves the current curve under this name (available in other projects too)")), GUILayout.Width(52f)))
                {
                    _curveLibrary.Save(_curveName, _settings.CustomFalloff ?? ClothEditSettings.DefaultCustomFalloff(), out string message);
                    ShowNotification(new GUIContent(message));
                    _curvePick = Mathf.Max(0, _curveLibrary.IndexOf(_curveName));
                }
            }
            if (_curveLibrary.Count == 0) return;
            var names = new string[_curveLibrary.Count];
            for (int i = 0; i < names.Length; i++) names[i] = _curveLibrary.GetName(i);
            using (new EditorGUILayout.HorizontalScope())
            {
                _curvePick = Mathf.Clamp(_curvePick, 0, names.Length - 1);
                _curvePick = EditorGUILayout.Popup(new GUIContent(L10n.Tr("保存したカーブ", "Saved Curves"), L10n.Tr("保存したカーブを選んで呼び出します", "Pick a saved curve to load")), _curvePick, names);
                if (GUILayout.Button(new GUIContent(L10n.Tr("呼び出す", "Load"), L10n.Tr("選んだカーブを、ブラシの減衰カーブにします", "Uses the selected curve as the brush falloff")), GUILayout.Width(64f)))
                {
                    _settings.CustomFalloff = _curveLibrary.GetCurve(_curvePick);
                    _settings.Falloff = FalloffPreset.Custom;
                    _curveName = names[_curvePick];
                    _settingsDirty = true;
                    GUI.changed = true;
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("削除", "Delete"), L10n.Tr("選んだカーブを、保存した一覧から消します", "Removes the selected curve from the saved list")), GUILayout.Width(48f)))
                {
                    _curveLibrary.Delete(names[_curvePick]);
                    _curvePick = 0;
                }
            }
        }

        // ---- シーンビューの比較・対称の表示 ----

        void DrawCompareAndSymmetryDots(ClothEditSession session)
        {
            if (_settings.ShowMoveHeatmap)
            {
                session.GetMoveDots(_compareDots, _compareValues, MaxCompareDots);
                for (int i = 0; i < _compareDots.Count; i++)
                {
                    Handles.color = ClothEditHelp.MoveColor(_compareValues[i]);
                    Dot(_compareDots[i], 0.011f);
                }
            }
            if (!_controller.TryGetMirrorPlane(out MirrorPlane plane)) return;
            if (_settings.ShowMirrorDifference)
            {
                session.GetMirrorDifferenceDots(plane, _settings.MirrorTolerance, _compareDots, _compareValues, MaxCompareDots);
                for (int i = 0; i < _compareDots.Count; i++)
                {
                    Handles.color = ClothEditHelp.MirrorDifferenceColor(_compareValues[i]);
                    Dot(_compareDots[i], 0.011f);
                }
            }
            if (_settings.ShowUnmatchedMirror)
            {
                session.GetUnmatchedMirrorPositions(plane, _settings.MirrorTolerance, _compareDots, MaxCompareDots);
                Handles.color = ClothEditHelp.UnmatchedMirrorColor;
                for (int i = 0; i < _compareDots.Count; i++) Dot(_compareDots[i], 0.014f);
            }
        }

        // ブラシの円の中に、重み 75% / 50% / 25% の位置の同心円
        void DrawFalloffRings(Vector3 center, Vector3 normal, float radius, FalloffCurve curve)
        {
            if (!_settings.ShowFalloffRings || curve == null || !(radius > 0f)) return;
            Color baseColor = Handles.color;
            float[] weights = ClothEditHelp.FalloffRingWeights;
            for (int k = 0; k < weights.Length; k++)
            {
                float t = curve.OuterPositionForWeight(weights[k]);
                if (!(t > 0f) || t >= 0.999f) continue;
                Handles.color = new Color(baseColor.r, baseColor.g, baseColor.b, ClothEditHelp.FalloffRingAlpha * (1f - 0.2f * k));
                Handles.DrawWireDisc(center, normal, radius * t);
            }
            Handles.color = baseColor;
        }
    }
}
