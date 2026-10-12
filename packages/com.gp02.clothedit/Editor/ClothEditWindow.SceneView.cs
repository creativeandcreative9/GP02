using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    // シーンビューの入力（マウス、ショートカット、ホイール）と重ね描き。
    public sealed partial class ClothEditWindow
    {
        const int MaxPinDots = 3000;             // 描画するピンの点の上限（多いと重くなる）
        const int MaxHeatDots = 3000;            // 伸びのヒートマップの点の上限
        const double RadiusLabelSeconds = 1.5;   // 半径を変えたあと、数値を出しておく時間

        readonly List<Vector3> _pinDots = new List<Vector3>();
        readonly List<Vector3> _heatDots = new List<Vector3>();
        readonly List<float> _heatValues = new List<float>();
        readonly List<KeyValuePair<string, string>> _shortcutLines = new List<KeyValuePair<string, string>>();
        double _radiusChangedAt = double.NegativeInfinity;

        // Shift＋ドラッグの矩形で、ピン留め・解除、点の選択・解除をまとめて行う
        bool _rectDragging;
        RectPurpose _rectPurpose;
        Vector2 _rectStart;
        Vector2 _rectEnd;

        // ピン解除の道具（昔の保存値）は、ピン留めの道具の「外す」モードとして同じ道具に数える
        static bool IsPinTool(ClothEditMode mode) => ClothEditPinInput.IsPinTool(mode);

        // 届かないときの線と一言。届かない状態が少し続いてから出す（動かし始めの一瞬の遅れで点滅しないように）
        bool _reachShown;
        double _reachOutSince = double.PositiveInfinity;

        // 減り方の同心円を出す道具（重みのある道具だけ。ピンと選ぶ道具は円の内側すべて）
        static bool UsesFalloffRings(ClothEditMode mode) => mode == ClothEditMode.Grab || mode == ClothEditMode.Rotate || mode == ClothEditMode.Scale || mode == ClothEditMode.Relax;

        // Graphics.DrawMesh は、シーンのカメラが描く前に呼ばないと次のコマまで表示されない。
        // ショートカットは、シーンビュー自身のキー処理より先に読む必要があるので、ここで受ける
        void OnBeforeSceneGUI(SceneView sceneView)
        {
            if (_controller == null) return;
            ClothEditSession session = _controller.Session;
            if (session == null) return;

            Event e = Event.current;
            // 「視点」の向きのハンドル・数値で動かすときの軸は、このシーンビューのカメラの向き
            _controller.SetViewRotation(sceneView.camera.transform.rotation);
            if (e.type == EventType.Repaint)
            {
                // ポーズを見せている間は、アバターの複製が服を描く（編集中の形のプレビューは編集の姿勢なので描かない）
                if (!_controller.IsPosePreviewActive) session.DrawPreview(sceneView.camera);
                return;
            }
            if (HandleRadiusDragAndCursor(sceneView, e)) return;
            HandleShortcuts(sceneView, e);
        }

        // 編集中で、マウスがこのシーンビューの上にあるときだけ反応して、イベントを使う。
        // キーは Shortcut Manager（ClothEditShortcutBindings）が先に受けるので、ここで読むのは、それが使えないときと、
        // 配列によってキーコードが違う [ ] を文字で判定するときだけ
        void HandleShortcuts(SceneView sceneView, Event e)
        {
            // C を離したら、マウスの位置にかかわらず元の形の表示をやめる
            if (e.type == EventType.KeyUp && ClothEditShortcuts.IsHoldOriginalKey(e.keyCode))
            {
                _controller.HoldOriginal = false;
                return;
            }
            if (e.type == EventType.MouseLeaveWindow) _controller.HoldOriginal = false;
            if (EditorWindow.mouseOverWindow != sceneView) return;

            if (e.type == EventType.ScrollWheel)
            {
                WheelTarget wheel = ClothEditShortcuts.GetWheelTarget(e.control, e.shift);
                if (wheel == WheelTarget.None || GUIUtility.hotControl != 0) return;
                ChangeRadius(wheel, ClothEditShortcuts.WheelSteps(e.delta.y));
                RefreshHover(e);
                e.Use();
                return;
            }

            if (e.type != EventType.KeyDown) return;
            // 右ボタンを押してのカメラ移動（W/E など）や、Alt・Ctrl・Command の組み合わせは、Unity に任せる
            if (e.alt || e.command || Tools.viewTool == ViewTool.FPS) return;
            KeyCode key = e.keyCode;

            if (ClothEditShortcutBindings.IsActive)
            {
                // Shortcut Manager が [ ] のキーコードを受けられない配列（日本語配列など）だけ、文字で判定する
                if (e.control || GUIUtility.hotControl != 0 || key == KeyCode.LeftBracket || key == KeyCode.RightBracket) return;
                if (ClothEditShortcuts.TryGetRadiusKey(KeyCode.None, e.character, e.shift, out WheelTarget charTarget, out int charStep))
                {
                    ChangeRadius(charTarget, charStep);
                    RefreshHover(e);
                    e.Use();
                }
                return;
            }

            if (ClothEditShortcuts.IsHelpKey(key))
            {
                _showShortcuts = !_showShortcuts;
                Repaint();
                sceneView.Repaint();
                e.Use();
                return;
            }
            if (e.control) return;
            if (ClothEditShortcuts.IsHoldOriginalKey(key))
            {
                _controller.HoldOriginal = true;
                e.Use();
                return;
            }
            // ドラッグ中（押している間）は、道具や半径を変えない
            if (GUIUtility.hotControl != 0) return;

            // Q は「選んで動かす」（もう一度押すと直前の道具）。「選んで動かす」の間の W/E/R はハンドルの種類。
            // Shift は P（ピン留め＋外す）のときだけ意味があり、W/E/R/T/Q に Shift が付いていたら何もしない
            RememberToolBeforeSelect();
            ToolKeyResult keyResult = ClothEditShortcuts.ResolveToolKey(key, e.shift, _settings.Tool, _toolBeforeSelect, _settings.HandleType);
            if (keyResult.Handled)
            {
                SetToolFromKey(keyResult);
                RefreshHover(e);
                Repaint();
                sceneView.Repaint();
                e.Use();
                return;
            }
            if (ClothEditShortcuts.TryGetRadiusKey(key, e.character, e.shift, out WheelTarget target, out int step))
            {
                ChangeRadius(target, step);
                RefreshHover(e);
                e.Use();
                return;
            }
            if (ClothEditShortcuts.IsRadiusDragKey(key))
            {
                _lastSceneMouse = e.mousePosition;
                _hasSceneMouse = true;
                ToggleRadiusDrag(e.shift);
                e.Use();
            }
        }

        // マウスを動かさなくても、道具や半径を変えたら、範囲の表示をその場で作り直す
        void RefreshHover(Event e)
        {
            if (_controller.IsEditing && !_controller.IsBusy) _controller.Hover(HandleUtility.GUIPointToWorldRay(e.mousePosition));
        }

        void ChangeRadius(WheelTarget target, float steps)
        {
            if (target == WheelTarget.BrushRadius) _brushCentimeters = ClothEditController.AdjustRadius(_brushCentimeters, steps, BrushMinCm, BrushMaxCm);
            else if (target == WheelTarget.InfluenceRadius) _influenceCentimeters = ClothEditController.AdjustRadius(_influenceCentimeters, steps, InfluenceMinCm, InfluenceMaxCm);
            else return;
            SyncController();
            _controller.InvalidatePreview();
            _radiusChangedAt = EditorApplication.timeSinceStartup;
            Repaint();
            SceneView.RepaintAll();
        }

        void OnSceneGUI(SceneView sceneView)
        {
            if (_controller == null || !_controller.IsEditing) return;
            Event e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            _sceneControlId = controlId;
            sceneView.wantsMouseMove = true;
            bool selecting = _controller.Mode == ClothEditMode.Select;

            // 札の「×」と札の上のクリックを、先に受ける（布の操作・ハンドルより上に見えるため）。使ったイベントは、ここで終わり
            DoCardClicks(sceneView);
            if (e.type == EventType.Used) return;

            if (e.type == EventType.Layout)
            {
                // クリックでオブジェクトの選択が変わらないようにする。ハンドルは、これより近いコントロールとして先に当たる
                HandleUtility.AddDefaultControl(controlId);
                if (selecting)
                {
                    DoSelectionHandles(e);
                    EndHandleDragIfReleased();
                }
                return;
            }

            if (e.type == EventType.Repaint)
            {
                DrawOverlay();
                if (selecting)
                {
                    DoSelectionHandles(e);
                    DrawSelectionCount(sceneView);
                }
                DrawCards(sceneView);
                return;
            }

            // Ctrl を押した・離したときも描き直す（ピン留めの円の色が、留める・外すで変わる）
            if ((e.type == EventType.KeyDown || e.type == EventType.KeyUp) && (e.keyCode == KeyCode.LeftControl || e.keyCode == KeyCode.RightControl)) sceneView.Repaint();

            // Esc：ドラッグ中・仕上げ中の操作を取り消して、押す前の形に戻す。何もしていなければ、選んだ点を解除する
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (_rectDragging)
                {
                    _rectDragging = false;
                    GUIUtility.hotControl = 0;
                    e.Use();
                    sceneView.Repaint();
                    return;
                }
                if (EndPosePreviewOnEscape(e)) return;
                bool pointerCancelled = SelectCancelPointer();
                if (_controller.Cancel() || pointerCancelled)
                {
                    GUIUtility.hotControl = 0;
                    e.Use();
                }
                else if (selecting && _controller.SelectionCount > 0)
                {
                    _controller.ClearSelection();
                    e.Use();
                }
                return;
            }

            // ハンドルが先にイベントを使う（ハンドルの上のクリックで、塗る選び方が始まらないようにする）
            if (selecting)
            {
                DoSelectionHandles(e);
                EndHandleDragIfReleased();
            }

            // 視点操作（Alt）と右・中ボタンは何もしない
            if (e.alt) return;

            switch (e.type)
            {
                case EventType.MouseMove:
                    _controller.Hover(HandleUtility.GUIPointToWorldRay(e.mousePosition));
                    sceneView.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button == 0 && EndPosePreviewOnClick(e)) break;
                    if (e.button != 0 || HandleUtility.nearestControl != controlId) break;
                    if (selecting)
                    {
                        SelectMouseDown(sceneView, e, controlId);
                        break;
                    }
                    if (IsPinTool(_controller.Mode))
                    {
                        // ピン留めは「選んで動かす」と同じ修飾キー: ドラッグ＝留める、Ctrl＝外す、Shift＋ドラッグ＝矩形、ダブルクリック＝部品（島）ごと
                        // （「塗ると外す」がオンなら、留める・外すが逆）
                        PinInput pin = ClothEditPinInput.Resolve(e.shift, e.control, e.clickCount, _controller.PinEraseMode);
                        if (pin.Gesture == PinGesture.Rectangle)
                        {
                            _rectDragging = true;
                            _rectPurpose = pin.Erase ? RectPurpose.Unpin : RectPurpose.Pin;
                            _rectStart = _rectEnd = e.mousePosition;
                            GUIUtility.hotControl = controlId;
                            e.Use();
                            break;
                        }
                        if (pin.Gesture == PinGesture.Island)
                        {
                            _controller.PinIslandAt(HandleUtility.GUIPointToWorldRay(e.mousePosition), !pin.Erase);
                            e.Use();
                            break;
                        }
                    }
                    Transform cameraTransform = sceneView.camera.transform;
                    if (!_controller.PointerDown(HandleUtility.GUIPointToWorldRay(e.mousePosition), cameraTransform.forward, cameraTransform.up, e.mousePosition.x, e.control)) break;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (e.button != 0 || GUIUtility.hotControl != controlId) break;
                    if (selecting)
                    {
                        if (SelectMouseDrag(sceneView, e)) e.Use();
                        break;
                    }
                    if (_rectDragging)
                    {
                        _rectEnd = e.mousePosition;
                        sceneView.Repaint();
                        e.Use();
                        break;
                    }
                    // Shift：精密操作（動きを 1/5 に）。Ctrl：回転を 15° 刻みに
                    _controller.PointerDrag(HandleUtility.GUIPointToWorldRay(e.mousePosition), e.mousePosition.x, e.shift, e.control);
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (e.button != 0 || GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    if (selecting)
                    {
                        if (SelectMouseUp(sceneView, e)) e.Use();
                        break;
                    }
                    if (_rectDragging)
                    {
                        _rectDragging = false;
                        ApplyPinRect(sceneView);
                        sceneView.Repaint();
                        e.Use();
                        break;
                    }
                    _controller.PointerUp();
                    e.Use();
                    break;
            }
        }

        // 四角で囲んだ中の点をピン留め（解除）する
        void ApplyPinRect(SceneView sceneView)
        {
            if (!TryGetRectCamera(sceneView, out Camera camera, out Rect viewport)) return;
            bool pinned = _rectPurpose == RectPurpose.Pin;
            Transform cameraTransform = camera.transform;
            int changed = _controller.PinInRect(camera.projectionMatrix * camera.worldToCameraMatrix, cameraTransform.position, cameraTransform.forward,
                camera.orthographic, viewport, pinned);
            sceneView.ShowNotification(new GUIContent(pinned
                ? L10n.Tr($"{changed} 点をピン留めしました", $"Pinned {changed} points")
                : L10n.Tr($"{changed} 点のピンを外しました", $"Unpinned {changed} points")), 1.0);
        }

        // ---- 重ね描き ----

        // pinErase: ピン留めの道具で、今の操作が外す操作か（外すときは黄色）
        static Color ToolColor(ClothEditMode mode, bool pinErase = false)
        {
            switch (mode)
            {
                case ClothEditMode.Pin: return pinErase ? new Color(0.9f, 0.9f, 0.2f, 1f) : ClothEditHelp.PinToolColor;
                case ClothEditMode.Unpin: return new Color(0.9f, 0.9f, 0.2f, 1f);
                case ClothEditMode.Rotate: return new Color(0.8f, 0.5f, 1f, 1f);
                case ClothEditMode.Scale: return new Color(0.4f, 1f, 0.5f, 1f);
                case ClothEditMode.Relax: return new Color(1f, 0.65f, 0.2f, 1f);
                case ClothEditMode.Select: return ClothEditHelp.SelectBrushColor;
                default: return new Color(0.2f, 0.8f, 1f, 1f);
            }
        }

        static void Dot(Vector3 p, float size)
        {
            Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * size, EventType.Repaint);
        }

        void DrawOverlay()
        {
            ClothEditSession session = _controller.Session;
            if (session == null) return;
            if (DrawPoseCheckOverlay(session)) return;

            if (_settings.SymmetryEnabled) DrawMirrorPlane(session);

            // 伸びのヒートマップ
            if (_settings.ShowStretchHeatmap)
            {
                session.GetStretchDots(_heatDots, _heatValues, MaxHeatDots);
                for (int i = 0; i < _heatDots.Count; i++)
                {
                    Handles.color = ClothEditHelp.HeatColor(_heatValues[i]);
                    Dot(_heatDots[i], 0.012f);
                }
            }

            DrawCompareAndSymmetryDots(session);
            DrawCursor();
            DrawReachLine();

            if (session.PinnedCount > 0)
            {
                session.GetPinnedPositions(_pinDots, MaxPinDots);
                Handles.color = ClothEditHelp.PinColor;
                for (int i = 0; i < _pinDots.Count; i++) Dot(_pinDots[i], 0.015f);
            }
            DrawPenetrationDots(session);

            bool selectMode = _controller.Mode == ClothEditMode.Select;
            bool selectBusy = selectMode && (_controller.IsTransformingSelection || _controller.IsSettling);
            if (selectMode) DrawSelectionDisplay(session);

            if (_radiusDrag.Active)
            {
                DrawRadiusDrag();
                return;
            }

            // 選んだ点を動かしている間は、マウスがハンドルの上にあるので、つまんだ範囲の色分けだけ出す（ブラシの円は出さない）
            if (!_controller.HasHover && !(selectBusy && _controller.PreviewCount > 0)) return;

            // ブラシを当てる前の、範囲に入る粒子（重みで色分け）。選んだ点の変形中・仕上げ中は、動かしている範囲
            int previewCount = _controller.PreviewCount;
            if (previewCount > 0)
            {
                ReadOnlySpan<System.Numerics.Vector3> positions = session.Solver.Positions;
                for (int k = 0; k < previewCount; k++)
                {
                    int particle = _controller.PreviewParticle(k);
                    if ((uint)particle >= (uint)positions.Length) continue;
                    float w = _controller.PreviewWeight(k);
                    System.Numerics.Vector3 p = positions[particle];
                    Handles.color = ClothEditHelp.WeightColor(w);
                    Dot(new Vector3(p.X, p.Y, p.Z), 0.008f + 0.006f * w);
                }
            }

            if (selectBusy || (selectMode && (_rectDragging || IsMouseOverHandle()))) return;

            Handles.color = ToolColor(_controller.Mode, IsPinTool(_controller.Mode) && ClothEditPinInput.IsErase(_controller.Mode, _settings.PinEraseMode, Event.current.control));
            Handles.DrawWireDisc(_controller.BrushRingCenter, _controller.HoverNormal, _controller.BrushRadius);
            if (UsesFalloffRings(_controller.Mode)) DrawFalloffRings(_controller.BrushRingCenter, _controller.HoverNormal, _controller.BrushRadius, _settings.GetFalloffCurve());
            if (_controller.HasMirrorHover)
            {
                Color mirrorColor = Handles.color;
                mirrorColor.a = 0.6f;
                Handles.color = mirrorColor;
                Handles.DrawWireDisc(_controller.MirrorHoverPoint, _controller.MirrorHoverNormal, _controller.BrushRadius);
            }

            // ホイールやキーで半径を変えた直後は、数値を添える
            if (EditorApplication.timeSinceStartup - _radiusChangedAt < RadiusLabelSeconds)
            {
                Handles.Label(_controller.HoverPoint + _controller.HoverNormal * 0.01f,
                    L10n.Tr($"半径 {_brushCentimeters:F1} cm / 影響範囲 {_influenceCentimeters:F0} cm", $"Radius {_brushCentimeters:F1} cm / Influence {_influenceCentimeters:F0} cm"));
            }
        }

        // 対称の鏡の面を、編集中の布のまわりに半透明の四角で示す
        void DrawMirrorPlane(ClothEditSession session)
        {
            if (!_controller.TryGetMirrorPlane(out MirrorPlane plane)) return;
            Bounds bounds = session.Renderer.bounds;
            Vector3 normal = ClothEditSymmetry.ToUnity(plane.Normal);
            Vector3 point = ClothEditSymmetry.ToUnity(plane.Point);
            Vector3 center = bounds.center - normal * Vector3.Dot(bounds.center - point, normal);
            Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(normal, u);
            float half = Mathf.Max(0.1f, bounds.extents.magnitude);
            var corners = new[] { center + (u + v) * half, center + (u - v) * half, center + (-u - v) * half, center + (-u + v) * half };
            Handles.DrawSolidRectangleWithOutline(corners, new Color(0.3f, 0.7f, 1f, 0.08f), new Color(0.3f, 0.7f, 1f, 0.5f));
        }

        // Shift＋ドラッグで選んでいる矩形（GUI 座標。Handles.BeginGUI の中で呼ぶ）
        void DrawRectSelection()
        {
            Rect rect = ClothEditPinRange.FromCorners(_rectStart, _rectEnd);
            Color color;
            switch (_rectPurpose)
            {
                case RectPurpose.Pin: color = ClothEditHelp.PinToolColor; break;
                case RectPurpose.Unpin: color = new Color(0.9f, 0.9f, 0.2f); break;
                case RectPurpose.Select: color = ClothEditHelp.SelectedColor; break;
                default: color = new Color(0.6f, 0.8f, 1f); break;
            }
            EditorGUI.DrawRect(rect, new Color(color.r, color.g, color.b, 0.12f));
            const float Edge = 1f;
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, Edge), color);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - Edge, rect.width, Edge), color);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, Edge, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - Edge, rect.yMin, Edge, rect.height), color);
        }
    }
}
