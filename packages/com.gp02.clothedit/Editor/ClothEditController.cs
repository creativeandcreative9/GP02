using System;
using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>ブラシの使い方。</summary>
    public enum ClothEditMode
    {
        Grab,
        Pin,
        Unpin,
    }

    /// <summary>
    /// 布の編集の操作の流れ（押す・ドラッグ・離す、仕上げ、Unity の Undo との連動、保存、作り直し）。
    /// 画面（EditorWindow、シーンビューのイベント）には依存しない。入力は「レイ」「カメラの向き」「経過時間」で受け取るので、
    /// EditMode テストから動かせる。ウィンドウは、イベントを読んでここに渡し、描画するだけ。
    /// 編集の中身（粒子・保存）は <see cref="ClothEditSession"/> が持つ。
    /// </summary>
    public sealed class ClothEditController : IDisposable
    {
        public const string DefaultSaveFolder = "Assets/GP02/Meshes";

        readonly ClothEditSettings _settings;
        Renderer _target;
        ClothEditSession _session;
        ClothEditState _state;          // Unity の Undo の記録先（セッションごとに作る）
        float _influenceRadius = 0.3f;
        float _brushRadius = 0.03f;
        bool _disposed;

        // 操作の状態
        bool _opOpen;                   // 変更の記録を取り始めて、まだ書き込んでいない
        int _opGroup;
        bool _ignoreUndoCallback;
        bool _pointerDown;              // 押してから離す（または取り消す）まで
        ClothEditMode _strokeMode;      // 押したときのモード
        bool _dragging;                 // つまんで動かしている
        bool _moved;                    // つまんだ点が実際に動いた
        bool _settling;
        bool _settleClockPending;       // 離したあとの最初の Tick で、仕上げの開始時刻を決める
        double _settleStart;
        Plane _dragPlane;
        Vector3 _dragStartPoint;

        int _hoverTriangle = -1;        // カーソルの下の三角形（重ね着で、どの布をつまむか・ピン留めするかを決める）

        public ClothEditController(ClothEditSettings settings = null)
        {
            _settings = settings ?? new ClothEditSettings();
            // Ctrl+Z / Ctrl+Y、メニューの Edit > Undo、ボタンのどれでもここに来る
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        /// <summary>画面の更新が必要になったときに呼ばれる。</summary>
        public event Action Changed;

        // ---- 設定・状態 ----

        public ClothEditSettings Settings => _settings;
        public Renderer Target => _target;
        public ClothEditSession Session => _session;
        public bool IsEditing => _session != null;
        public bool IsDragging => _dragging;
        public bool IsSettling => _settling;
        public bool IsBusy => _dragging || _settling;

        /// <summary>直前の編集開始・作り直しが失敗したときの理由。成功すると空になる。</summary>
        public string LastError { get; private set; } = "";

        /// <summary>最後に保存したメッシュ。</summary>
        public Mesh LastSavedMesh { get; private set; }

        /// <summary>計算の最後に測った、最大の伸び（0.1 で 10%）。</summary>
        public float LastStretch { get; private set; }

        public ClothEditMode Mode { get; set; }

        /// <summary>保存先のフォルダ。</summary>
        public string SaveFolder { get; set; } = DefaultSaveFolder;

        /// <summary>メッシュの変換中に進捗バーを出すか（ウィンドウだけ true にする）。</summary>
        public bool ShowProgress { get; set; }

        /// <summary>ブラシ半径（メートル）。</summary>
        public float BrushRadius
        {
            get => _brushRadius;
            set => _brushRadius = Mathf.Max(0f, value);
        }

        /// <summary>つまんだ動きが広がる範囲（メートル）。編集中でもすぐ反映される。</summary>
        public float InfluenceRadius
        {
            get => _influenceRadius;
            set
            {
                value = Mathf.Max(0f, value);
                if (value == _influenceRadius) return;   // 毎回の描画から呼ばれるので、変わらないときはソルバーを触らない
                _influenceRadius = value;
                if (_session != null) _session.Solver.InfluenceRadius = _influenceRadius;
            }
        }

        public bool HasHover { get; private set; }
        public Vector3 HoverPoint { get; private set; }
        public Vector3 HoverNormal { get; private set; } = Vector3.up;

        public string StateLabel => _dragging ? "つまんでいる" : _settling ? "仕上げ中" : "待機中";

        /// <summary>設定を変えたあとに呼ぶ。編集中のソルバーに反映する。</summary>
        public void ApplySettings()
        {
            _settings.Sanitize();
            if (_session != null) _settings.ApplyTo(_session.Solver, _session.RestAllowance);
            Notify();
        }

        // ---- 対象の確認 ----

        public static Renderer FindRenderer(GameObject target)
        {
            if (target == null) return null;
            var skinned = target.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null) return skinned;
            var meshRenderer = target.GetComponent<MeshRenderer>();
            return meshRenderer != null && meshRenderer.GetComponent<MeshFilter>() != null ? meshRenderer : null;
        }

        public static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (renderer is MeshRenderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
            return null;
        }

        /// <summary>編集を始められない理由。始められるなら null。利用者に見せる文章にしてある。</summary>
        public static string GetStartProblem(Renderer renderer)
        {
            if (renderer == null)
            {
                return "対象が指定されていません。「対象」に、布の SkinnedMeshRenderer（または MeshFilter 付きの MeshRenderer）を指定してください。";
            }
            Mesh mesh = GetMesh(renderer);
            if (mesh == null)
            {
                return $"「{renderer.name}」にメッシュが設定されていません。メッシュを持つ SkinnedMeshRenderer か、MeshFilter 付きの MeshRenderer を指定してください。";
            }
            if (ClothEditSession.CountTriangles(mesh) == 0)
            {
                return $"「{renderer.name}」のメッシュには三角形（面）がありません。面のあるメッシュを指定してください。";
            }
            return null;
        }

        /// <summary>Read/Write が無効なメッシュか。エディタ上では編集できるが、利用者に知らせるために使う。</summary>
        public static bool IsMeshUnreadable(Renderer renderer)
        {
            Mesh mesh = GetMesh(renderer);
            return mesh != null && !mesh.isReadable;
        }

        // ---- 編集の開始と終了 ----

        /// <summary>
        /// 編集を始める。始められないときは false を返し、利用者向けの理由を error に入れる（<see cref="LastError"/> にも残る）。
        /// </summary>
        public bool TryStart(Renderer target, out string error)
        {
            _target = target;
            return StartSession(null, null, null, null, out error);
        }

        /// <param name="carriedPins">引き継ぐピンのワールド位置（Undo による作り直しのとき）</param>
        /// <param name="carriedPinIslands">引き継ぐピンごとの部品（島）の番号。重ね着で元の層だけを留めるため</param>
        /// <param name="reuseCloth">粒子を置き直さずに使う布（保存後の <see cref="ParticleCloth.Rebase"/> の結果）。メッシュに合わなければ使わない</param>
        /// <param name="carriedPinParticles">reuseCloth を使うときに引き継ぐピンの粒子の番号（番号が同じなので位置で探さない）</param>
        bool StartSession(IReadOnlyList<Vector3> carriedPins, IReadOnlyList<int> carriedPinIslands,
            ParticleCloth reuseCloth, IReadOnlyList<int> carriedPinParticles, out string error)
        {
            End();
            error = GetStartProblem(_target);
            if (error != null)
            {
                Fail(error);
                return false;
            }

            if (reuseCloth != null && !ClothEditSession.IsClothCompatible(_target, reuseCloth)) reuseCloth = null;
            try
            {
                if (ShowProgress) EditorUtility.DisplayProgressBar("GP02", "メッシュを粒子にしています…", 0.5f);
                _session = new ClothEditSession(_target, reuseCloth);
            }
            catch (ArgumentException ex)
            {
                // 想定している失敗（Read/Write が読めない、三角形がない、など）。理由は ex.Message に書いてある
                _session = null;
                error = ex.Message;
                Fail(error);
                return false;
            }
            catch (Exception ex)
            {
                _session = null;
                Debug.LogException(ex);
                error = "編集を始められませんでした。メッシュの形が特殊な可能性があります。Console のエラーを確認してください。\n" + ex.Message;
                Fail(error);
                return false;
            }
            finally
            {
                if (ShowProgress) EditorUtility.ClearProgressBar();
            }

            _settings.ApplyTo(_session.Solver, _session.RestAllowance);
            _session.Solver.InfluenceRadius = _influenceRadius;
            if (reuseCloth != null)
            {
                if (carriedPinParticles != null && carriedPinParticles.Count > 0)
                {
                    int pinned = _session.PinParticles(carriedPinParticles);
                    Debug.Log($"[GP02] ピン {pinned} 粒子を引き継ぎました。");
                }
            }
            else if (carriedPins != null && carriedPins.Count > 0)
            {
                int pinned = _session.PinNear(carriedPins, _session.Cloth.Spacing * 0.75f, carriedPinIslands);
                Debug.Log($"[GP02] ピン {carriedPins.Count} 点から {pinned} 粒子を引き継ぎました。");
            }

            // Burst のコンパイルをここで済ませる（最初のドラッグで止まらないように）
            double warmUp = _session.WarmUp();

            // 作り直した直後の形が、Undo の戻り先の基準になる
            _state = ClothEditState.Create();
            _session.WriteState(_state);
            LastError = "";
            LastStretch = 0f;
            HasHover = false;
            Debug.Log($"[GP02] {_target.name}: 頂点 {_session.VertexCount} / 粒子 {_session.ParticleCount} / 準備 {_session.PrepareMilliseconds:F0}ms / 初回の計算 {warmUp:F0}ms");
            Notify();
            return true;
        }

        void Fail(string message)
        {
            LastError = message;
            Debug.LogWarning("[GP02] " + message);
            Notify();
        }

        /// <summary>編集を終える。元のレンダラーの表示を戻す。</summary>
        public void End()
        {
            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            _opOpen = false;
            HasHover = false;
            if (_state != null)
            {
                Undo.ClearUndo(_state);
                Object.DestroyImmediate(_state);
                _state = null;
            }
            if (_session == null) return;
            _session.Dispose();
            _session = null;
            Notify();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            End();
        }

        // 編集中のレンダラーが消されていたら、編集を終わらせる。生きていれば true
        bool EnsureAlive()
        {
            if (_session == null) return false;
            if (_session.IsTargetAlive) return true;
            End();
            return false;
        }

        // ---- ポインタ（マウス）の入力 ----

        /// <summary>カーソルの下の布を調べる（ブラシの円の表示用）。当たれば true。</summary>
        public bool Hover(Ray ray)
        {
            if (!EnsureAlive()) return false;
            return UpdateHover(ray);
        }

        bool UpdateHover(Ray ray)
        {
            bool hit = _session.Raycast(ray, out Vector3 point, out Vector3 normal, out _hoverTriangle);
            HasHover = hit;
            if (hit)
            {
                HoverPoint = point;
                HoverNormal = normal;
            }
            return hit;
        }

        /// <summary>
        /// 押した。布に当たったら、モードに応じてつまむ・ピン留め・ピン解除を始め、true を返す（イベントを使ってよい）。
        /// 当たらなければ false。
        /// </summary>
        /// <param name="cameraForward">カメラの向き。つまんだ点を動かす平面（ヒット点を通り、カメラの向きに垂直）を決める</param>
        public bool PointerDown(Ray ray, Vector3 cameraForward)
        {
            if (!EnsureAlive()) return false;
            if (!UpdateHover(ray)) return false;

            // 前のストロークの仕上げ中なら、そこで確定する
            FinishStroke();
            _pointerDown = true;
            _strokeMode = Mode;
            _moved = false;
            switch (_strokeMode)
            {
                case ClothEditMode.Grab:
                    if (_session.BeginGrab(HoverPoint, _brushRadius, _hoverTriangle))
                    {
                        BeginOperation("GP02 つまむ");
                        _dragPlane = new Plane(-cameraForward, HoverPoint);
                        _dragStartPoint = HoverPoint;
                        _dragging = true;
                    }
                    break;
                case ClothEditMode.Pin:
                case ClothEditMode.Unpin:
                    // 押してから離すまでのドラッグを、1回の操作にまとめる
                    BeginOperation(_strokeMode == ClothEditMode.Pin ? "GP02 ピン留め" : "GP02 ピン解除");
                    PaintPins();
                    break;
            }
            Notify();
            return true;
        }

        /// <summary>押したまま動かした。つまんでいれば動かし、ピン留めなら塗る。</summary>
        public void PointerDrag(Ray ray)
        {
            if (!EnsureAlive() || !_pointerDown) return;

            if (_strokeMode == ClothEditMode.Grab)
            {
                if (!_dragging) return;
                // つまんで動かしている間は、カーソルの下の点を調べない（形が変わるたびに境界箱を更新する手間を省く）
                if (_dragPlane.Raycast(ray, out float distance))
                {
                    Vector3 point = ray.GetPoint(distance);
                    _session.DragTo(point);
                    if ((point - _dragStartPoint).sqrMagnitude > 1e-12f) _moved = true;
                }
            }
            else
            {
                UpdateHover(ray);
                if (_opOpen) PaintPins();
            }
            Notify();
        }

        /// <summary>
        /// 離した。ピン留めなら確定する。つまんでいたなら仕上げに入る（以降の <see cref="Tick"/> で進む）。
        /// 動かさずに離したときは、何も記録せずに終わる。
        /// </summary>
        public void PointerUp()
        {
            if (!EnsureAlive() || !_pointerDown) return;
            _pointerDown = false;

            if (_strokeMode != ClothEditMode.Grab)
            {
                CommitOperation();
            }
            else if (_dragging)
            {
                if (_moved)
                {
                    _dragging = false;
                    _settling = true;
                    _settleClockPending = true;
                }
                else
                {
                    FinishStroke();   // クリックしただけ。計算せず、Undo の記録も残さない
                }
            }
            Notify();
        }

        /// <summary>
        /// 操作を取り消す（Esc）。ドラッグ中・仕上げ中・ピンを塗っている途中なら、押す前の形に戻し、Undo の記録も残さない。
        /// 取り消すものがあれば true。
        /// </summary>
        public bool Cancel()
        {
            if (!EnsureAlive()) return false;
            if (!(_pointerDown || _dragging || _settling || _opOpen)) return false;

            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            if (_session.IsGrabbing) _session.Release();
            if (_opOpen)
            {
                _opOpen = false;
                RevertRecords(_opGroup);
            }
            // _state には、操作を始める前の形が入っている（書き込むのは確定のとき）
            _session.ReadState(_state);
            AfterShapeChanged();
            return true;
        }

        void PaintPins()
        {
            if (!HasHover) return;
            _session.PaintPins(HoverPoint, _brushRadius, _strokeMode == ClothEditMode.Pin, _hoverTriangle);
        }

        // ---- 時間の経過 ----

        /// <summary>
        /// 一定時間ごと（EditorApplication.update）に呼ぶ。つまんでいる間と仕上げの間、計算を進める。
        /// 対象が消されていたら編集を終える。
        /// </summary>
        /// <param name="now">現在の時刻（秒。仕上げの時間切れの判定に使う）</param>
        public void Tick(double now)
        {
            if (!EnsureAlive()) return;
            if (!_dragging && !_settling) return;
            // 動かしていないうちは計算しない（クリックしただけで形が変わらないように）
            if (_dragging && !_moved) return;

            if (_settleClockPending)
            {
                _settleStart = now;
                _settleClockPending = false;
            }

            _session.Solver.SolveFor(_settings.SolveMilliseconds);
            _session.UpdateCurrentShape();
            LastStretch = _session.Solver.MeasureMaxStretch();

            // つまみを外すのは仕上げが終わってから。先に外すと、つまんでいた範囲が戻ってしまう
            if (_settling && (LastStretch <= _settings.SettleTolerance || now - _settleStart > _settings.SettleTimeoutSeconds))
            {
                _session.Release();
                _settling = false;
                CommitOperation();
            }
            Notify();
        }

        // 仕上げを待たずに、つまみを外して確定する（次の操作や保存の前など）
        void FinishStroke()
        {
            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            if (_session != null && _session.IsGrabbing) _session.Release();
            CommitOperation();
        }

        // ---- Undo ----

        // 変更の直前に呼ぶ。RecordObject は配列の要素ごとに差分を作り、粒子が数万あると遅いので Complete を使う
        void BeginOperation(string name)
        {
            // 前の操作と同じ Undo のグループにならないように区切る
            Undo.IncrementCurrentGroup();
            _opGroup = Undo.GetCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_state, name);
            _opOpen = true;
        }

        // 変更が終わったら呼ぶ。何も変わっていなければ、積んだ記録を取り消す（空の Undo を残さない）
        void CommitOperation()
        {
            if (!_opOpen) return;
            _opOpen = false;
            if (_session == null || _state == null) return;

            if (_session.MatchesState(_state))
            {
                RevertRecords(_opGroup);
                return;
            }
            _session.WriteState(_state);
            Undo.IncrementCurrentGroup();
        }

        void RevertRecords(int group)
        {
            _ignoreUndoCallback = true;
            try { Undo.RevertAllDownToGroup(group); }
            finally { _ignoreUndoCallback = false; }
        }

        /// <summary>Unity の Undo を1つ戻す（途中の操作は確定してから）。この後、編集が作り直されることがある。</summary>
        public void UndoLast()
        {
            if (_session != null) FinishStroke();
            Undo.PerformUndo();
        }

        /// <summary>Unity の Undo をやり直す（途中の操作は確定してから）。</summary>
        public void RedoLast()
        {
            if (_session != null) FinishStroke();
            Undo.PerformRedo();
        }

        /// <summary>
        /// Undo / Redo が行われた通知（<c>Undo.undoRedoPerformed</c>）。コンストラクターで登録済みなので、通常は呼ばなくてよい。
        /// </summary>
        public void OnUndoRedoPerformed()
        {
            if (_session == null || _ignoreUndoCallback) return;

            // 仕上げ中・ドラッグ中なら止める。書き込まない記録は、Undo がすでに戻している
            _pointerDown = false;
            _dragging = false;
            _settling = false;
            _settleClockPending = false;
            _opOpen = false;
            if (_session.IsGrabbing) _session.Release();

            if (!_session.IsTargetUnchanged())
            {
                // 保存そのもの（メッシュの差し替えやシェイプキーの重み）が戻された、または対象が消えた。今のレンダラーで作り直す
                RebuildSession();
                return;
            }

            if (_session.ReadState(_state)) AfterShapeChanged();
        }

        void RebuildSession()
        {
            if (_target == null || GetStartProblem(_target) != null)
            {
                End();
                return;
            }
            var pins = new List<Vector3>();
            var pinIslands = new List<int>();
            _session.GetPinnedPositions(pins, pinIslands);
            StartSession(pins, pinIslands, null, null, out _);
        }

        void AfterShapeChanged()
        {
            LastStretch = _session.Solver.MeasureMaxStretch();
            Notify();
        }

        // ---- ボタン操作 ----

        /// <summary>すべて元に戻す（ピンは残す）。Undo で戻せる。</summary>
        public void ResetAll()
        {
            if (!EnsureAlive()) return;
            FinishStroke();
            BeginOperation("GP02 すべて元に戻す");
            _session.ResetAll();
            CommitOperation();
            AfterShapeChanged();
        }

        /// <summary>"GP02_1", "GP02_2" … のうち、今のメッシュのシェイプキーと重ならない名前。編集中でなければ空。</summary>
        public string SuggestShapeName() => _session != null ? _session.SuggestShapeName() : "";

        /// <summary>
        /// 今の形をシェイプキーとして保存し、そのメッシュで編集を続ける（粒子とピンは引き継ぐ）。
        /// 失敗したら false を返し、利用者向けの理由を message に入れる。成功したときの message は保存先の案内。
        /// </summary>
        public bool Save(string shapeName, out string message)
        {
            message = null;
            if (!EnsureAlive())
            {
                message = "編集中ではありません。「編集を開始」を押してから保存してください。";
                return false;
            }

            FinishStroke();
            string name = shapeName == null ? "" : shapeName.Trim();
            if (name.Length == 0)
            {
                message = "シェイプキーの名前が空です。名前を入れてから保存してください。";
                return false;
            }
            if (_session.SourceMesh.GetBlendShapeIndex(name) >= 0)
            {
                message = $"同じ名前のシェイプキー「{name}」が既にあります。別の名前にして保存してください。";
                return false;
            }

            // 保存の Undo（メッシュの差し替えなど）が、直前の編集と同じグループにならないようにする
            Undo.IncrementCurrentGroup();
            Mesh saved;
            try
            {
                saved = _session.ApplyToScene(name, SaveFolder);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = $"保存できませんでした。保存先「{SaveFolder}」に書き込めるか確認してください（フォルダが読み取り専用になっていないか、同名のファイルが開かれていないか）。\n{ex.Message}";
                return false;
            }

            LastSavedMesh = saved;
            message = $"シェイプキー「{name}」を {AssetDatabase.GetAssetPath(saved)} に保存しました。";
            Debug.Log("[GP02] " + message);
            // 保存の Undo が、次の編集と同じグループにならないようにする
            Undo.IncrementCurrentGroup();

            // シェイプキー込みの形（編集中の形そのもの）が新しい基準になる。
            // 粒子は置き直さず、今の位置を静止状態にした布で作り直す。ピンは粒子の番号が同じなので番号で引き継ぐ
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ParticleCloth rebased;
            var pinParticles = new List<int>();
            try
            {
                rebased = _session.Cloth.Rebase(_session.Solver.Positions);
                _session.GetPinnedParticles(pinParticles);
            }
            catch (Exception ex)
            {
                // 想定外の失敗は、普通の作り直し（粒子の置き直し）に戻す
                Debug.LogException(ex);
                var pins = new List<Vector3>();
                var pinIslands = new List<int>();
                _session.GetPinnedPositions(pins, pinIslands);
                if (!StartSession(pins, pinIslands, null, null, out string retryError)) message += "\n編集を続けられませんでした。「編集を開始」からやり直してください。\n" + retryError;
                return true;
            }
            double rebaseMs = timer.Elapsed.TotalMilliseconds;
            if (!StartSession(null, null, rebased, pinParticles, out string error)) message += "\n編集を続けられませんでした。「編集を開始」からやり直してください。\n" + error;
            Debug.Log($"[GP02] 保存後の作り直し {timer.Elapsed.TotalMilliseconds:F0}ms（うち静止状態の更新 {rebaseMs:F0}ms）");
            return true;
        }

        void Notify()
        {
            Changed?.Invoke();
        }

        // ---- シェイプキーのデータとの連携（別の変更と衝突しないよう、クラスの末尾にまとめている） ----

        /// <summary>
        /// メッシュ空間の差分（シェイプキーのデータの1フレーム分）を、編集中の形として読み込む。Unity の Undo の1段になる。
        /// 頂点数が合わない・編集中でないときは何もせず false。
        /// </summary>
        public bool LoadDeltasAsOperation(Vector3[] deltas)
        {
            return LoadDeltasAsOperation(deltas, null);
        }

        /// <summary>
        /// 差分を読み込み、pinnedVertices（元メッシュの頂点番号）が空でなければ、今のピンをその頂点の近くの粒子に置き換える。
        /// ピンの読み込みも同じ Undo の1段に含まれる。
        /// </summary>
        public bool LoadDeltasAsOperation(Vector3[] deltas, int[] pinnedVertices)
        {
            if (!EnsureAlive() || deltas == null || deltas.Length != _session.VertexCount) return false;
            FinishStroke();
            BeginOperation("GP02 シェイプキーを読み込む");
            bool loaded = _session.LoadMeshSpaceDeltas(deltas);
            if (loaded && pinnedVertices != null && pinnedVertices.Length > 0) ReplacePinsWithVertices(pinnedVertices);
            // 何も変わらなかったとき（差分が 0 など）は、Undo の記録も残さない
            CommitOperation();
            AfterShapeChanged();
            return loaded;
        }

        /// <summary>
        /// 今の編集（粒子の形とピン）を、シェイプキーのデータのエントリとして保存する。保存したエントリを返す。Unity の Undo で戻せる。
        /// 編集は続けられる（メッシュは差し替えない）。ピン留めした粒子の近くの頂点番号もエントリに入れる。
        /// </summary>
        /// <param name="overwrite">true なら同じ名前のエントリを上書きする（無ければ追加）。false なら、同じ名前があれば「名前 (1)」のように別名で追加する</param>
        /// <returns>保存できなければ null（理由は <see cref="LastError"/>）</returns>
        public ShapeKeyEntry SaveToData(GP02ShapeKeyData data, string entryName, bool overwrite)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!EnsureAlive())
            {
                LastError = "編集中ではありません。";
                return null;
            }
            string name = entryName == null ? "" : entryName.Trim();
            if (name.Length == 0)
            {
                LastError = "シェイプキーの名前が空です。名前を入れてから保存してください。";
                return null;
            }
            if (data.VertexCount != _session.VertexCount)
            {
                LastError = $"データの頂点数（{data.VertexCount}）が、編集中のメッシュの頂点数（{_session.VertexCount}）と違うので保存できません。";
                return null;
            }

            FinishStroke();
            Vector3[] deltas = _session.GetMeshSpaceDeltas();
            // 法線の差分は、元の形（データの元のメッシュ）に対して計算する
            Mesh rest = data.SourceMesh != null && data.SourceMesh.vertexCount == deltas.Length ? data.SourceMesh : _session.SourceMesh;
            ShapeKeyEntry fresh = ShapeKeyLibrary.CreateEntryFromDeltas(rest, name, deltas);
            fresh.PinnedVertices = CollectPinnedVertices();

            Undo.IncrementCurrentGroup();
            ShapeKeyEntry result;
            ShapeKeyEntry existing = overwrite ? data.FindEntry(name) : null;
            if (existing != null)
            {
                Undo.RecordObject(data, "GP02 シェイプキーを上書き保存");
                existing.Frames = fresh.Frames;
                existing.PinnedVertices = fresh.PinnedVertices;
                EditorUtility.SetDirty(data);
                result = existing;
            }
            else
            {
                fresh.Name = ShapeKeyListOperations.UniqueName(data, name);
                ShapeKeyLibrary.AddEntry(data, fresh);
                result = fresh;
            }
            ShapeKeyLibrary.SaveData(data);
            Undo.IncrementCurrentGroup();
            LastError = "";
            return result;
        }

        /// <summary>
        /// レンダラーのシェイプキーのデータを返す。コンポーネントとデータが無ければ作って付ける（Undo できる）。
        /// レンダラーにメッシュが無ければ null。
        /// </summary>
        public GP02ShapeKeyData FindOrCreateData(Renderer renderer)
        {
            return ShapeKeyLibrary.FindOrCreateData(renderer);
        }

        // ピン留めした粒子の近くにある頂点の番号。復元（ReplacePinsWithVertices）と同じ距離で見る
        int[] CollectPinnedVertices()
        {
            var pins = new List<Vector3>();
            _session.GetPinnedPositions(pins);
            if (pins.Count == 0) return new int[0];

            float radius = _session.Cloth.Spacing * 0.75f;
            var grid = new PointGrid(radius);
            for (int i = 0; i < pins.Count; i++) grid.Add(i, new System.Numerics.Vector3(pins[i].x, pins[i].y, pins[i].z));

            var result = new List<int>();
            var candidates = new List<int>();
            Vector3[] vertices = _session.CurrentWorldVertices;
            float radiusSq = radius * radius;
            for (int v = 0; v < vertices.Length; v++)
            {
                grid.Query(new System.Numerics.Vector3(vertices[v].x, vertices[v].y, vertices[v].z), radius, candidates);
                foreach (int c in candidates)
                {
                    if ((pins[c] - vertices[v]).sqrMagnitude <= radiusSq)
                    {
                        result.Add(v);
                        break;
                    }
                }
            }
            return result.ToArray();
        }

        // 今のピンをすべて外し、頂点の（今の形での）位置の近くの粒子をピン留めする
        void ReplacePinsWithVertices(int[] vertexIndices)
        {
            _session.PaintPins(Vector3.zero, 1e5f, false);
            Vector3[] vertices = _session.CurrentWorldVertices;
            var positions = new List<Vector3>(vertexIndices.Length);
            foreach (int v in vertexIndices)
            {
                if ((uint)v < (uint)vertices.Length) positions.Add(vertices[v]);
            }
            _session.PinNear(positions, _session.Cloth.Spacing * 0.75f);
        }
    }
}
