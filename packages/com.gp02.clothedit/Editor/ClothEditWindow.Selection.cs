using System;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    // 「選んで動かす」の画面：ウィンドウの「選んだ点」の区切りと、シーンビューのハンドル・選ぶ操作・表示。
    // 選択と変形の計算は ClothEditController にあり、ここはイベントを読んで渡すことと、描くことだけを受け持つ。
    public sealed partial class ClothEditWindow
    {
        /// <summary>Shift＋ドラッグの矩形で何をするか。</summary>
        enum RectPurpose
        {
            Pin,
            Unpin,
            Select,
            Deselect,
        }

        const float EmptyClickPixels = 4f;           // 空の所のクリックとみなす、動きの上限（ピクセル）
        const float CountLabelOffsetX = 26f;         // 選んだ点の数を、ハンドルの中心からずらして置く位置（ピクセル）
        const float CountLabelOffsetY = -34f;

        [SerializeField] bool _foldSelection = true;
        [SerializeField] bool _foldNumeric;
        [SerializeField] Vector3 _numericMove;
        [SerializeField] Vector3 _numericRotate;
        [SerializeField] Vector3 _numericScale = new Vector3(100f, 100f, 100f);

        readonly ClothEditSelectionDisplay _selectionDisplay = new ClothEditSelectionDisplay();
        ClothEditMode _toolBeforeSelect = ClothEditMode.Grab;   // Q で戻る先
        int _sceneControlId;                                    // シーンビューの既定のコントロール（ハンドルより優先度が低い）
        bool _selectPainting;                                   // 塗って選んでいる途中
        bool _emptyClick;                                       // 布の無い所を押している（動かさずに離したら、選択を解除する）
        Vector2 _emptyClickStart;
        bool _selectionHasPinned;                               // 選んだ点にピン留めした点が含まれるか（Layout のたびに調べる）
        GUIStyle _countStyle;
        GUIStyle _countShadowStyle;

        // ---- 道具 ----

        // 「選んで動かす」以外の道具を使っている間、それを覚えておく（Q でここへ戻る）
        void RememberToolBeforeSelect()
        {
            if (_settings != null && _settings.Tool != ClothEditMode.Select) _toolBeforeSelect = _settings.Tool;
        }

        static string[] FalloffPresetNames()
        {
            return new[]
            {
                L10n.Tr("一定", "Constant"), L10n.Tr("なめらか", "Smooth"), L10n.Tr("直線", "Linear"),
                L10n.Tr("鋭い", "Sharp"), L10n.Tr("球", "Sphere"), L10n.Tr("自由に描く", "Custom"),
            };
        }

        // ---- ウィンドウの「選んだ点」の区切り ----

        void DrawSelectionSection()
        {
            if (!_controller.IsEditing) return;
            int count = _controller.SelectionCount;
            if (_settings.Tool != ClothEditMode.Select && count == 0) return;

            if (Event.current.type == EventType.Layout) _selectionHasPinned = SelectionContainsPinned();

            _foldSelection = Section(_foldSelection, L10n.Tr("選んだ点", "Selection"));
            if (!_foldSelection) return;

            EditorGUILayout.LabelField(L10n.Tr("選んだ点の数", "Selected Points"), L10n.Tr($"{count:N0} 個", $"{count:N0}"));
            if (_settings.Tool != ClothEditMode.Select)
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("「選んで動かす」に切り替え (Q)", "Switch to Select & Move (Q)"), ClothEditHelp.ToolTip(ClothEditMode.Select))))
                {
                    RememberToolBeforeSelect();
                    _settings.Tool = ClothEditMode.Select;
                    _settingsDirty = true;
                    SceneView.RepaintAll();
                }
            }

            EditorGUI.BeginChangeCheck();

            // ハンドルの種類
            var handleLabels = new[]
            {
                new GUIContent(ClothEditHelp.HandleName(SelectionHandle.Move) + " (W)", ClothEditHelp.HandleTip(SelectionHandle.Move)),
                new GUIContent(ClothEditHelp.HandleName(SelectionHandle.Rotate) + " (E)", ClothEditHelp.HandleTip(SelectionHandle.Rotate)),
                new GUIContent(ClothEditHelp.HandleName(SelectionHandle.Scale) + " (R)", ClothEditHelp.HandleTip(SelectionHandle.Scale)),
            };
            _settings.HandleType = (SelectionHandle)GUILayout.Toolbar((int)_settings.HandleType, handleLabels);

            _settings.PivotMode = (SelectionPivot)EditorGUILayout.Popup(new GUIContent(L10n.Tr("中心", "Pivot"),
                L10n.Tr("ハンドルを出す位置で、回転・拡大縮小の中心です。選んだ点の中心: 選んだ点の今の位置の平均。クリックした点: 最後にクリックして選んだ面の上の点。" +
                        "3D カーソル: Shift+右クリックで置いた面の上の点（置いていなければ選んだ点の中心）",
                    "Where the handle is shown, and the center of rotation and scaling. Selection center: the average of the selected points. Clicked point: the point on the surface you clicked last. " +
                    "3D cursor: the point on the surface placed with Shift+Right-click (the selection center if not placed)")),
                (int)_settings.PivotMode, new[] { L10n.Tr("選んだ点の中心", "Selection center"), L10n.Tr("クリックした点", "Clicked point"), L10n.Tr("3D カーソル", "3D cursor") });
            if (_settings.PivotMode == SelectionPivot.Cursor) DrawCursorButtons(count);
            _settings.HandleSpace = (HandleOrientation)EditorGUILayout.Popup(new GUIContent(L10n.Tr("向き", "Orientation"),
                L10n.Tr("ハンドルの軸の向き。グローバル: ワールドの軸。アバター: アバター（対称編集の基準）の向き。面の法線: 選んだ点の面の向きを Z にした向き。視点: 画面の右・上・奥の向き",
                    "The axes of the handle. Global: world axes. Avatar: the avatar's orientation (the symmetry reference). Surface normal: Z is the surface normal of the selected points. View: the screen's right, up and depth")),
                (int)_settings.HandleSpace, new[] { L10n.Tr("グローバル", "Global"), L10n.Tr("アバター", "Avatar"), L10n.Tr("面の法線", "Surface normal"), L10n.Tr("視点", "View") });

            // 動かし方
            int moveStyle = EditorGUILayout.Popup(new GUIContent(L10n.Tr("動かし方", "Move Style"),
                L10n.Tr("布として動かす: 選んだ点を動かすと、周りの布が布の計算で伸びずに付いてきます。布の長さで届かない所へは、手前で止まります。\n" +
                        "形だけ動かす: 選んだ点となめらかに減る範囲を、指定どおりの位置へ動かします。布の計算をしないので、布が伸びることがあります（裾を長くするなど、布の長さが変わる編集向き）。",
                    "As cloth: the surroundings follow the selected points through the cloth simulation without stretching. Points stop short where the cloth cannot reach.\n" +
                    "Shape only: the selected points and the smooth range move exactly as specified, with no cloth simulation, so the cloth may stretch (for edits that change the cloth's length, such as a longer hem).")),
                _settings.SelectionMovesAsCloth ? 0 : 1, new[] { L10n.Tr("布として動かす", "As cloth"), L10n.Tr("形だけ動かす", "Shape only") });
            _settings.SelectionMovesAsCloth = moveStyle == 0;
            EditorGUILayout.LabelField(_settings.SelectionMovesAsCloth
                ? L10n.Tr("周りが付いてきて、伸びません。届かない所は手前で止まります", "The surroundings follow without stretching. Points stop where the cloth cannot reach")
                : L10n.Tr("指定どおりに動かします。布の計算をしないので、伸びることがあります", "Moves exactly as specified. No cloth simulation, so it may stretch"),
                EditorStyles.wordWrappedMiniLabel);

            // なめらかさ
            float smoothCm = EditorGUILayout.Slider(new GUIContent(L10n.Tr("なめらかさの半径（cm）", "Smoothness Radius (cm)"),
                L10n.Tr("選んだ点の端から、動きがなめらかに減っていく範囲の半径です。0 なら選んだ点だけが動きます（周りの布は布の計算で付いてきます）",
                    "Radius of the range around the selected points where the motion fades out smoothly. 0 moves only the selected points (the surrounding cloth still follows through the simulation)")),
                _settings.SelectionFalloffRadius * 100f, 0f, ClothEditSettings.MaxSelectionFalloffRadius * 100f);
            _settings.SelectionFalloffRadius = smoothCm * 0.01f;
            _settings.SelectionFalloff = (FalloffPreset)EditorGUILayout.Popup(new GUIContent(L10n.Tr("減り方", "Falloff"),
                L10n.Tr("なめらかに減る範囲の、減り方です。ブラシの「減衰カーブ」とは別の設定です（「自由に描く」を選んだときだけ、ブラシで描いたカーブを使います）",
                    "How the motion fades out in the smooth range. This is separate from the brush Falloff (only \"Custom\" uses the curve drawn for the brush)")),
                (int)_settings.SelectionFalloff, FalloffPresetNames());

            _settings.SelectThroughOcclusion = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("隠れた点も選ぶ", "Select Hidden Points"),
                L10n.Tr("矩形で選ぶとき、他の布に隠れた点や裏側の点も選びます。オフなら、画面に見えている点だけを選びます",
                    "When selecting with a rectangle, also selects points hidden behind other cloth and on the back side. When off, only visible points are selected")),
                _settings.SelectThroughOcclusion);
            _settings.ShowSelectionWeights = EditorGUILayout.Toggle(new GUIContent(L10n.Tr("範囲の色分け", "Show Range Colors"),
                L10n.Tr("なめらかに減る範囲の点を、動きの強さで色分けして見せます", "Shows the points in the smooth range colored by how strongly they move")),
                _settings.ShowSelectionWeights);

            if (EditorGUI.EndChangeCheck())
            {
                _settingsDirty = true;
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            DrawSelectionButtons(count);

            if (_settings.SymmetryEnabled)
            {
                EditorGUILayout.HelpBox(L10n.Tr("対称がオンです。反対側は自動で鏡に映して動くので、選ばなくてよいです。",
                    "Symmetry is on. The opposite side moves mirrored automatically, so you do not need to select it."), MessageType.Info);
            }
            if (count > 0 && _selectionHasPinned)
            {
                EditorGUILayout.HelpBox(L10n.Tr("選んだ点にピン留めした点が含まれています。ピン留めした点は動きません。",
                    "The selection includes pinned points. Pinned points do not move."), MessageType.Warning);
            }

            DrawNumericMove(count);
            EditorGUILayout.Space();
        }

        // 3D カーソルの置き方（Shift+右クリックのほか、ボタンで選んだ点の中心に置く・消す）
        void DrawCursorButtons(int count)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(count == 0))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("カーソルを選んだ点の中心に", "Cursor to Selection"),
                        L10n.Tr("3D カーソルを、選んだ点の今の位置の平均に置きます", "Places the 3D cursor at the average of the selected points"))))
                    {
                        _controller.PlaceCursorAtSelectionCenter();
                        SceneView.RepaintAll();
                    }
                }
                using (new EditorGUI.DisabledScope(!_controller.HasCursor))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("カーソルを消す", "Clear Cursor"), L10n.Tr("3D カーソルを消します（中心は選んだ点の中心になります）", "Removes the 3D cursor (the pivot becomes the selection center)")), GUILayout.Width(110f)))
                    {
                        _controller.ClearCursor();
                        SceneView.RepaintAll();
                    }
                }
            }
            EditorGUILayout.LabelField(_controller.HasCursor
                ? L10n.Tr("Shift+右クリックで、布の上に置き直せます", "Shift+Right-click on the cloth to move it")
                : L10n.Tr("Shift+右クリックで、布の上に 3D カーソルを置きます", "Shift+Right-click on the cloth to place the 3D cursor"), EditorStyles.wordWrappedMiniLabel);
        }

        void DrawSelectionButtons(int count)
        {
            bool has = count > 0;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("すべて選ぶ", "Select All"), L10n.Tr("すべての点を選びます", "Selects every point")))) _controller.SelectAll();
                using (new EditorGUI.DisabledScope(!has))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("選択を解除", "Clear"), L10n.Tr("選んだ点をすべて外します（Esc でも解除できます）", "Deselects every point (Esc also clears the selection)")))) _controller.ClearSelection();
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("反転", "Invert"), L10n.Tr("選んだ点と、選んでいない点を入れ替えます", "Swaps the selected and the unselected points")))) _controller.InvertSelection();
            }
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(!has))
            {
                if (GUILayout.Button(new GUIContent(L10n.Tr("広げる", "Grow"), L10n.Tr("選んだ点の周りの1周ぶんを足します（同じ部品の中だけ）", "Adds one ring of neighbors around the selection (within the same part)")))) _controller.GrowSelection();
                if (GUILayout.Button(new GUIContent(L10n.Tr("狭める", "Shrink"), L10n.Tr("選んだ点の縁の1周ぶんを外します（同じ部品の中だけ）", "Removes the outer ring of the selection (within the same part)")))) _controller.ShrinkSelection();
                if (GUILayout.Button(new GUIContent(L10n.Tr("左右の反対側も選ぶ", "Select Mirror Side"),
                    L10n.Tr("選んだ点を鏡に映した反対側の点も選びます。鏡の面は「対称」の軸とずれで決まります（対称がオフでも使えます）。対称がオンなら、動かすときに反対側は自動で付いてくるので、選ばなくてもかまいません",
                        "Also selects the mirrored points on the opposite side. The mirror plane comes from the axis and offset in Symmetry (works even when symmetry is off). With symmetry on, the opposite side follows automatically when you move, so you do not need this"))))
                {
                    if (_controller.MirrorSelection() == 0)
                    {
                        ShowNotification(new GUIContent(L10n.Tr("反対側に新しく選べる点がありません（「対称」の軸とずれを確かめてください）",
                            "No new points on the opposite side (check the axis and offset in Symmetry)")));
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!has))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("選んだ点をピン留め", "Pin Selection"),
                        L10n.Tr("選んだ点をピン留めします。Ctrl+Z で取り消せます", "Pins the selected points. Ctrl+Z undoes this")))) _controller.PinSelected(true);
                }
                using (new EditorGUI.DisabledScope(_controller.Session == null || _controller.Session.PinnedCount == 0))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("ピン留めした点を選ぶ", "Select Pinned"),
                        L10n.Tr("ピン留めした点を、選んでいる点に足します", "Adds the pinned points to the selection")))) _controller.SelectPinned();
                }
            }
        }

        // ---- 数値で動かす ----

        void DrawNumericMove(int count)
        {
            _foldNumeric = EditorGUILayout.Foldout(_foldNumeric, L10n.Tr("数値で動かす", "Move by Numbers"), true);
            if (!_foldNumeric) return;

            string space = _settings.HandleSpace == HandleOrientation.Avatar ? L10n.Tr("アバター", "avatar")
                : _settings.HandleSpace == HandleOrientation.Normal ? L10n.Tr("面の法線", "surface normal")
                : _settings.HandleSpace == HandleOrientation.View ? L10n.Tr("視点", "view") : L10n.Tr("グローバル", "global");
            EditorGUILayout.LabelField(L10n.Tr($"軸は、今のハンドルの向き（{space}）です", $"The axes are those of the handle's current orientation ({space})"), EditorStyles.wordWrappedMiniLabel);
            _numericMove = EditorGUILayout.Vector3Field(new GUIContent(L10n.Tr("移動（cm）", "Move (cm)"), L10n.Tr("各軸の向きに動かす距離です", "Distance to move along each axis")), _numericMove);
            _numericRotate = EditorGUILayout.Vector3Field(new GUIContent(L10n.Tr("回転（°）", "Rotate (deg)"),
                L10n.Tr("各軸のまわりに回す角度です。中心は「中心」の設定", "Angle to rotate around each axis. The pivot follows the Pivot setting")), _numericRotate);
            _numericScale = EditorGUILayout.Vector3Field(new GUIContent(L10n.Tr("拡大（%）", "Scale (%)"),
                L10n.Tr("各軸に沿った倍率です。100 で変わりません", "Scale along each axis. 100 means no change")), _numericScale);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(count == 0 || _controller.IsBusy))
                {
                    if (GUILayout.Button(new GUIContent(L10n.Tr("適用", "Apply"),
                        L10n.Tr("入れた値で、選んだ点を動かします。ハンドルで動かしたときと同じ結果になります。Ctrl+Z で取り消せます（1回で1段）",
                            "Moves the selected points by the values entered, with the same result as using the handle. Ctrl+Z undoes it (one step per Apply)"))))
                    {
                        ApplyNumericMove();
                    }
                }
                if (GUILayout.Button(new GUIContent(L10n.Tr("値をリセット", "Reset Values"), L10n.Tr("入力欄を、移動 0・回転 0・拡大 100% に戻します", "Resets the fields to move 0, rotate 0, scale 100%")), GUILayout.Width(110f)))
                {
                    _numericMove = Vector3.zero;
                    _numericRotate = Vector3.zero;
                    _numericScale = new Vector3(100f, 100f, 100f);
                    GUI.FocusControl(null);
                }
            }
        }

        void ApplyNumericMove()
        {
            if (ClothEditSelectionNumeric.IsIdentity(_numericMove, _numericRotate, _numericScale))
            {
                ShowNotification(new GUIContent(L10n.Tr("動きが入っていません（移動 0・回転 0・拡大 100%）", "No motion entered (move 0, rotate 0, scale 100%)")));
                return;
            }
            ClothEditSelectionNumeric.ToWorld(_controller.HandleRotation, _numericMove, _numericRotate, _numericScale,
                out Vector3 translation, out Quaternion rotation, out Vector3 scale);
            if (!_controller.ApplySelectionTransform(translation, rotation, scale))
            {
                ShowNotification(new GUIContent(L10n.Tr("動きませんでした（選んだ点がすべてピン留めされているか、動きが小さすぎます）",
                    "Nothing moved (all selected points are pinned, or the motion is too small)")));
            }
            SceneView.RepaintAll();
        }

        bool SelectionContainsPinned()
        {
            ClothEditSession session = _controller.Session;
            if (session == null || session.PinnedCount == 0 || _controller.SelectionCount == 0) return false;
            System.Collections.Generic.IReadOnlyList<int> selected = _controller.SelectedParticles;
            for (int k = 0; k < selected.Count; k++)
            {
                if (session.Solver.IsPinned(selected[k])) return true;
            }
            return false;
        }

        // ---- シーンビュー: ハンドル ----

        // 選んだ点があるとき、ハンドルを描いて操作を受ける。Layout・Repaint を含め、すべてのイベントで同じ順に呼ぶ（コントロールの番号を揃えるため）。
        // ハンドルを押したときの位置・向きは、変形が始まってからは Controller が固定して返すので、ハンドルは動かした位置に逃げない。
        void DoSelectionHandles(Event e)
        {
            if (_controller.SelectionCount == 0) return;

            Vector3 pivot = _controller.SelectionPivot;
            Quaternion frame = _controller.HandleRotation;
            Vector3 position = pivot;
            Quaternion rotation = frame;
            Vector3 scale = Vector3.one;

            EditorGUI.BeginChangeCheck();
            switch (_settings.HandleType)
            {
                case SelectionHandle.Rotate:
                    rotation = Handles.RotationHandle(frame, pivot);
                    break;
                case SelectionHandle.Scale:
                    scale = Handles.ScaleHandle(Vector3.one, pivot, frame, HandleUtility.GetHandleSize(pivot));
                    break;
                default:
                    position = Handles.PositionHandle(pivot, frame);
                    break;
            }
            if (!EditorGUI.EndChangeCheck()) return;

            // 値が変わった最初のフレームで始める。以降は、押したときからの合計を渡す
            if (!_controller.IsTransformingSelection && !_controller.BeginSelectionTransform()) return;
            _controller.UpdateSelectionTransform(position - pivot, rotation * Quaternion.Inverse(frame), scale, e.shift);
        }

        // ハンドルを離したら、変形を終える（Esc で取り消したあとは、すでに終わっている）
        void EndHandleDragIfReleased()
        {
            if (_controller.IsTransformingSelection && GUIUtility.hotControl == 0) _controller.EndSelectionTransform();
        }

        // ---- シーンビュー: 選ぶ操作 ----

        // 「選んで動かす」で、既定のコントロールの上（ハンドルの上ではない）を押した
        void SelectMouseDown(SceneView sceneView, Event e, int controlId)
        {
            _selectPainting = false;
            _emptyClick = false;
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (e.shift)
            {
                // 矩形で選ぶ（Ctrl も押していれば外す）。布に当たらない所からも始められる
                _rectDragging = true;
                _rectPurpose = e.control ? RectPurpose.Deselect : RectPurpose.Select;
                _rectStart = _rectEnd = e.mousePosition;
                GUIUtility.hotControl = controlId;
                e.Use();
                return;
            }
            if (e.clickCount >= 2)
            {
                // ダブルクリック: 部品（島）ごと。最初のクリックで塗った所を含む部品全体になる
                _controller.SelectIslandAt(ray, e.control);
                e.Use();
                return;
            }
            if (_controller.SelectPaintBegin(ray, e.control))
            {
                _selectPainting = true;
                GUIUtility.hotControl = controlId;
                e.Use();
                return;
            }
            if (!e.control)
            {
                // 布の無い所を押した。動かさずに離したら、選択を解除する
                _emptyClick = true;
                _emptyClickStart = e.mousePosition;
                GUIUtility.hotControl = controlId;
                e.Use();
            }
        }

        // 塗る・矩形・空のクリックのドラッグ中。使ったら true
        bool SelectMouseDrag(SceneView sceneView, Event e)
        {
            if (_rectDragging)
            {
                _rectEnd = e.mousePosition;
                sceneView.Repaint();
                return true;
            }
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_selectPainting)
            {
                _controller.SelectPaintDrag(ray);
                return true;
            }
            if (_emptyClick)
            {
                // 布の無い所から始めて布の上へ動かしたら、そこから塗り始める
                if (_controller.SelectPaintBegin(ray, false))
                {
                    _emptyClick = false;
                    _selectPainting = true;
                }
                return true;
            }
            return false;
        }

        // 塗る・矩形・空のクリックを離した。使ったら true
        bool SelectMouseUp(SceneView sceneView, Event e)
        {
            if (_rectDragging)
            {
                _rectDragging = false;
                if (_rectPurpose == RectPurpose.Select || _rectPurpose == RectPurpose.Deselect) ApplySelectRect(sceneView, _rectPurpose == RectPurpose.Deselect);
                else ApplyPinRect(sceneView);
                sceneView.Repaint();
                return true;
            }
            if (_selectPainting)
            {
                _selectPainting = false;
                _controller.SelectPaintEnd();
                return true;
            }
            if (_emptyClick)
            {
                _emptyClick = false;
                if ((e.mousePosition - _emptyClickStart).sqrMagnitude <= EmptyClickPixels * EmptyClickPixels) _controller.ClearSelection();
                return true;
            }
            return false;
        }

        // Esc: 選ぶ操作の途中なら取り消す（塗った分は残る）。使ったら true
        bool SelectCancelPointer()
        {
            bool active = _selectPainting || _emptyClick;
            _selectPainting = false;
            _emptyClick = false;
            return active;
        }

        // 矩形の中の点を選ぶ（外す）
        void ApplySelectRect(SceneView sceneView, bool deselect)
        {
            if (!TryGetRectCamera(sceneView, out Camera camera, out Rect viewport)) return;
            Transform cameraTransform = camera.transform;
            int changed = _controller.SelectInRect(camera.projectionMatrix * camera.worldToCameraMatrix, cameraTransform.position, cameraTransform.forward,
                camera.orthographic, viewport, deselect);
            sceneView.ShowNotification(new GUIContent(deselect
                ? L10n.Tr($"{changed} 個の点を外しました", $"Deselected {changed} points")
                : L10n.Tr($"{changed} 個の点を選びました", $"Selected {changed} points")), 1.0);
        }

        // 矩形（GUI 座標）を、カメラのビューポート座標にする。小さすぎる矩形は使わない
        bool TryGetRectCamera(SceneView sceneView, out Camera camera, out Rect viewport)
        {
            camera = sceneView.camera;
            viewport = default;
            Rect guiRect = ClothEditPinRange.FromCorners(_rectStart, _rectEnd);
            float ppp = EditorGUIUtility.pixelsPerPoint;
            if (!ClothEditPinRange.IsBigEnough(guiRect, ppp)) return false;
            viewport = ClothEditPinRange.GuiRectToViewport(guiRect, camera.pixelWidth, camera.pixelHeight, ppp);
            return true;
        }

        // ---- シーンビュー: 表示 ----

        // 選んだ点・鏡像の側・なめらかに減る範囲・マウスの下の点を描く。変形中・仕上げ中は、つまんだ範囲の色分けが出るので描かない
        void DrawSelectionDisplay(ClothEditSession session)
        {
            if (_controller.IsTransformingSelection || _controller.IsSettling) return;

            _selectionDisplay.Update(_controller);
            ReadOnlySpan<NVector3> positions = session.Solver.Positions;

            // 一番下に、なめらかに減る範囲（重みで色分け）
            int weights = _controller.SelectionWeightCount;
            for (int k = 0; k < weights; k++)
            {
                int particle = _controller.SelectionWeightParticle(k);
                if ((uint)particle >= (uint)positions.Length) continue;
                float w = _controller.SelectionWeightValue(k);
                Handles.color = ClothEditHelp.SelectionWeightColor(w);
                DotAt(positions[particle], 0.007f + 0.005f * w);
            }

            Handles.color = ClothEditHelp.MirroredColor;
            for (int k = 0; k < _selectionDisplay.MirroredCount; k++)
            {
                int particle = _selectionDisplay.MirroredParticle(k);
                if ((uint)particle < (uint)positions.Length) DotAt(positions[particle], 0.011f);
            }

            Handles.color = ClothEditHelp.SelectedColor;
            for (int k = 0; k < _selectionDisplay.SelectedCount; k++)
            {
                int particle = _selectionDisplay.SelectedParticle(k);
                if ((uint)particle < (uint)positions.Length) DotAt(positions[particle], 0.013f);
            }

            // マウスの下の、塗ると選ばれる点。ハンドルの上にマウスがあるときは出さない
            if (_controller.HasHover && !_rectDragging && !IsMouseOverHandle())
            {
                Handles.color = ClothEditHelp.SelectHoverColor;
                int hover = _controller.HoverSelectCount;
                for (int k = 0; k < hover; k++)
                {
                    int particle = _controller.HoverSelectParticle(k);
                    if ((uint)particle < (uint)positions.Length) DotAt(positions[particle], 0.009f);
                }
            }
        }

        static void DotAt(NVector3 p, float size) => Dot(new Vector3(p.X, p.Y, p.Z), size);

        // ハンドルなど、既定のコントロール以外にマウスが近い（その間は、押してもハンドルが優先される）
        bool IsMouseOverHandle()
        {
            return GUIUtility.hotControl == 0 && HandleUtility.nearestControl != _sceneControlId;
        }

        // 選んだ点の数を、ハンドルの近くに小さく出す
        void DrawSelectionCount(SceneView sceneView)
        {
            if (_controller.SelectionCount == 0) return;
            _selectionDisplay.Update(_controller);
            Vector3 pivot = _controller.SelectionPivot;
            Transform cameraTransform = sceneView.camera.transform;
            if (Vector3.Dot(cameraTransform.forward, pivot - cameraTransform.position) <= 0f) return;   // カメラの後ろ

            if (_countStyle == null)
            {
                _countStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft, normal = { textColor = ClothEditHelp.SelectedColor } };
                // 橙の点の上でも読めるよう、黒い影を付ける
                _countShadowStyle = new GUIStyle(_countStyle) { normal = { textColor = new Color(0f, 0f, 0f, 0.85f) } };
            }
            var content = new GUIContent(_selectionDisplay.CountLabel);
            Vector2 size = _countStyle.CalcSize(content);
            Vector2 screen = HandleUtility.WorldToGUIPoint(pivot);
            Handles.BeginGUI();
            var rect = new Rect(screen.x + CountLabelOffsetX, screen.y + CountLabelOffsetY, size.x + 4f, size.y);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), content, _countShadowStyle);
            GUI.Label(rect, content, _countStyle);
            Handles.EndGUI();
        }
    }
}
