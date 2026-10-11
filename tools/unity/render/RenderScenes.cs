using System;
using System.Collections.Generic;
using System.IO;
using GP02.EditorTools;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// クラウドで見た目を確かめるための描画（tools/unity/render.sh から -executeMethod で呼ぶ）。
// テスト用の衣装を作り、編集前・編集中のプレビュー・保存したシェイプキーを画像にして、1枚に並べる。
public static class RenderScenes
{
    const int Size = 640;
    const string OutDir = "/out";

    // 全部まとめて描く（render.sh の既定）。Unity を起動し直さずに済むよう、1回の起動で続けて描く
    public static void All()
    {
        Run(() =>
        {
            EditAndSaveScenes();
            ClearScene();
            ToolScenes();
            ClearScene();
            LayerScenes();
            ClearScene();
            SelectScenes();
            ClearScene();
            SymmetryScenes();
            ClearScene();
            PenetrationScenes();
        });
    }

    public static void Symmetry() => Run(SymmetryScenes);

    public static void Penetration() => Run(PenetrationScenes);

    public static void Layers() => Run(LayerScenes);

    public static void EditAndSave() => Run(EditAndSaveScenes);

    public static void Tools() => Run(ToolScenes);

    public static void Select() => Run(SelectScenes);

    static void Run(Action action)
    {
        try
        {
            action();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static void ClearScene()
    {
        foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()) Object.DestroyImmediate(root);
    }

    static void EditAndSaveScenes()
    {
        {
            Camera camera = SetUpCamera(new Vector3(0.9f, 0.95f, -1.3f), new Vector3(0f, 0.7f, 0f));
            GameObject root = TestClothMenu.CreateInScene(null);
            var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
            var shots = new List<Texture2D> { Shot(camera, "1_before") };

            Mesh saved;
            using (var session = new ClothEditSession(skirt))
            {
                // 実際の使い方に合わせて、ウエスト（一番上の段）をピン留めする
                float top = float.MinValue;
                foreach (Vector3 v in session.CurrentWorldVertices) top = Mathf.Max(top, v.y);
                var rest = session.Cloth.Model.RestPositions;
                for (int i = 0; i < rest.Length; i++)
                {
                    if (rest[i].Y > top - 1.5f * session.Cloth.Spacing) session.Solver.SetPinned(i, true);
                }

                // スカートの前側の裾寄りを、手前・少し上へ 10cm 引っ張り、手を止めて仕上げる
                var origin = new Vector3(0f, 0.55f, -1f);
                session.Raycast(new Ray(origin, Vector3.forward), out Vector3 hit, out _, out int triangle);
                session.BeginGrab(hit, 0.04f, triangle);
                for (int f = 1; f <= 20; f++)
                {
                    session.DragTo(hit + new Vector3(0f, 0.04f, -0.10f) * f / 20f);
                    session.Solver.SolveFor(12);
                }
                for (int i = 0; i < 100; i++) session.Solver.SolveFor(12);
                Debug.Log($"[GP02描画] 仕上げ後の最大の伸び {session.Solver.MeasureMaxStretch():P1} / 粒子 {session.ParticleCount} / 高速化 {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)}");
                session.Release();
                session.UpdateCurrentShape();
                session.DrawPreview(camera);
                shots.Add(Shot(camera, "2_preview"));
                saved = session.CreateMeshWithBlendShape("GP02_Render");
            }

            // 保存したシェイプキーを Unity のスキニング（BakeMesh）で取り出して描く。
            // -executeMethod では毎フレームの更新が回らず、SkinnedMeshRenderer の描画が古いままになるため
            skirt.sharedMesh = saved;
            int index = saved.GetBlendShapeIndex("GP02_Render");
            foreach (float weight in new[] { 100f, 50f })
            {
                skirt.SetBlendShapeWeight(index, weight);
                shots.Add(ShotBaked(camera, skirt, $"3_saved_{weight:F0}"));
            }

            SaveGrid(shots, 2, "edit_and_save");
        }
    }

    // ---- 道具（対称でつまむ・回す・拡大縮小・元に戻すブラシ）の結果 ----
    // シーンビューの重ね描き（ブラシの円、範囲の点）は写らないので、形の結果だけを確かめる。
    // 操作は ClothEditController に、画面と同じ呼び出し（PointerDown / PointerDrag / PointerUp / Tick）で行う

    static void ToolScenes()
    {
        Vector3 cameraPosition = new Vector3(0f, 0.75f, -1.35f);
        Camera camera = SetUpCamera(cameraPosition, new Vector3(0f, 0.55f, 0f));
        GameObject root = TestClothMenu.CreateInScene(null);
        var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
        // テスト用の衣装は脚にポーズが付いていて左右対称でないので、対称編集を見るために、まっすぐ立った姿勢に戻す
        foreach (Transform bone in skirt.bones) bone.localRotation = Quaternion.identity;
        var shots = new List<Texture2D> { Shot(camera, "tool_0_before") };

        // 1. 右の裾を外（+X）へ引く。対称がオンなので、左の裾も外（-X）へ出る
        shots.Add(Scenario(camera, skirt, "tool_1_symmetric_pull",
            new ClothEditSettings { SymmetryEnabled = true, MoveDirection = MoveDirection.AxisX, Falloff = FalloffPreset.Smooth },
            c => PullSideways(c, camera, 0.12f)));

        // 2. 同じ操作で対称がオフ（右だけが出る）
        shots.Add(Scenario(camera, skirt, "tool_2_one_sided_pull",
            new ClothEditSettings { SymmetryEnabled = false, MoveDirection = MoveDirection.AxisX, Falloff = FalloffPreset.Smooth },
            c => PullSideways(c, camera, 0.12f)));

        // 3. 前の裾寄りを、面の法線まわりに回す
        shots.Add(Scenario(camera, skirt, "tool_3_rotate",
            new ClothEditSettings { RotateAxis = RotateAxisMode.SurfaceNormal, Falloff = FalloffPreset.Smooth },
            c =>
            {
                c.Mode = ClothEditMode.Rotate;
                c.BrushRadius = 0.12f;
                Ray ray = FrontRay(0f, 0.5f);
                c.PointerDown(ray, camera.transform.forward, 100f);
                for (int k = 1; k <= 20; k++)
                {
                    c.PointerDrag(ray, 100f + 6f * k, false, false);
                    c.Tick(NextTime());
                }
                c.PointerUp();
                Settle(c);
                Debug.Log($"[GP02描画] 回す: {c.GrabAngle:F0}°");
            }));

        // 4. 前の裾寄りを、拡大する
        shots.Add(Scenario(camera, skirt, "tool_4_scale",
            new ClothEditSettings { Falloff = FalloffPreset.Smooth },
            c =>
            {
                c.Mode = ClothEditMode.Scale;
                c.BrushRadius = 0.12f;
                Ray ray = FrontRay(0f, 0.5f);
                c.PointerDown(ray, camera.transform.forward, 100f);
                for (int k = 1; k <= 20; k++)
                {
                    c.PointerDrag(ray, 100f + 6f * k, false, false);
                    c.Tick(NextTime());
                }
                c.PointerUp();
                Settle(c);
                Debug.Log($"[GP02描画] 拡大縮小: ×{c.GrabScale:F2}");
            }));

        // 5. 右の裾を引いたあと、引いて出た所を元に戻すブラシで戻す
        shots.Add(Scenario(camera, skirt, "tool_5_relax",
            new ClothEditSettings { MoveDirection = MoveDirection.AxisX, Falloff = FalloffPreset.Smooth, RelaxStrength = 0.6f },
            c =>
            {
                PullSideways(c, camera, 0.12f);
                // 引いて右へ一番出た所（カメラから見える位置）を塗る
                float maxX = float.MinValue;
                Vector3 outermost = Vector3.zero;
                foreach (System.Numerics.Vector3 p in c.Session.Solver.Positions)
                {
                    if (p.X > maxX && p.Y > 0.45f && p.Y < 0.65f)
                    {
                        maxX = p.X;
                        outermost = new Vector3(p.X, p.Y, p.Z);
                    }
                }
                c.Mode = ClothEditMode.Relax;
                c.BrushRadius = 0.12f;
                var ray = new Ray(camera.transform.position, (outermost - camera.transform.position).normalized);
                c.PointerDown(ray, camera.transform.forward, 100f);
                for (int k = 0; k < 6; k++) c.PointerDrag(ray);
                c.PointerUp();
                Settle(c);
            }));

        SaveGrid(shots, 3, "tools");
    }

    // ---- 重ね着（スカートとペチコートを別のレンダラーにしたもの）をまとめて編集する ----
    // 上の段はスカートだけを編集（ペチコートは体の扱い）、下の段はスカートとペチコートをまとめて編集。
    // 回す・縮める・両側を外へ引くの3つを比べ、最大の伸びと、スカートがペチコートに入った深さをログに出す。
    // まとめて編集すると計算する粒子が約2倍になり、描画用の作業プロジェクトには Burst が無いので、
    // 仕上げの時間切れを長めにして、時間切れではなく形の違いを比べる
    const float LayerSettleSeconds = 10f;

    static void LayerScenes()
    {
        Camera camera = SetUpCamera(new Vector3(0f, 0.62f, -1.25f), new Vector3(0f, 0.58f, 0f));
        GameObject root = TestClothMenu.CreateInScene(null, true);
        var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
        var petticoat = root.transform.Find("Petticoat").GetComponent<SkinnedMeshRenderer>();
        foreach (Transform bone in skirt.bones) bone.localRotation = Quaternion.identity;
        Texture2D before = Shot(camera, "layer_0_before");
        var shots = new List<Texture2D>();

        foreach (bool layered in new[] { false, true })
        {
            Renderer[] targets = layered ? new Renderer[] { skirt, petticoat } : new Renderer[] { skirt };
            string prefix = layered ? "layer_together" : "layer_skirt_only";
            shots.Add(before);
            shots.Add(Scenario(camera, targets, prefix + "_1_rotate",
                new ClothEditSettings { RotateAxis = RotateAxisMode.SurfaceNormal, Falloff = FalloffPreset.Smooth, SettleTimeoutSeconds = LayerSettleSeconds },
                c => RotateOrScale(c, ClothEditMode.Rotate, 120f), petticoat));
            shots.Add(Scenario(camera, targets, prefix + "_2_shrink",
                new ClothEditSettings { Falloff = FalloffPreset.Smooth, SettleTimeoutSeconds = LayerSettleSeconds },
                c => RotateOrScale(c, ClothEditMode.Scale, -90f), petticoat));
            shots.Add(Scenario(camera, targets, prefix + "_3_pull_both_sides",
                new ClothEditSettings { SymmetryEnabled = true, MoveDirection = MoveDirection.AxisX, Falloff = FalloffPreset.Smooth, SettleTimeoutSeconds = LayerSettleSeconds },
                c => PullSideways(c, camera, 0.12f), petticoat));
        }
        SaveGrid(shots, 4, "layers");
    }

    // ---- 点を選んで動かす（スカートとペチコートをまとめて編集し、前の裾の点を矩形で選ぶ） ----
    // ① 外下へ 4cm（移動） ② アバターの向きで X だけ 1.3 倍 ③ 面の法線の向きで 20 度回す ④ 内へ 3cm（ペチコートを押す）⑤ 外上へ 3.6cm
    // ⑥ ①と同じ動きを「形だけ動かす」（布の計算なし）で。
    // ①②③は、ウエストをピン留めした伸びない布の裾には届かない動き（裾を下へ引く・裾の幅や高さを変える）なので、選んだ点は目標の手前で止まる。④⑤は届く
    // select.png は形だけ、select_marked.png は選んだ点（赤）となめらかに減る範囲（黄〜橙。重みが大きいほど橙）の印を重ねたもの。
    // ログに、選んだ点が目標のどこまで動いたか、最大の伸び、スカートがペチコートに入った最大の深さを出す

    static void SelectScenes()
    {
        Camera camera = SetUpCamera(new Vector3(0f, 0.62f, -1.25f), new Vector3(0f, 0.58f, 0f));
        camera.aspect = 1f;
        GameObject root = TestClothMenu.CreateInScene(null, true);
        var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
        var petticoat = root.transform.Find("Petticoat").GetComponent<SkinnedMeshRenderer>();
        foreach (Transform bone in skirt.bones) bone.localRotation = Quaternion.identity;
        Texture2D before = Shot(camera, "select_0_before");
        var plain = new List<Texture2D> { before };
        var marked = new List<Texture2D>();

        var cases = new[]
        {
            ("select_1_move", HandleOrientation.Global, new Vector3(0f, -0.028f, -0.028f), Quaternion.identity, Vector3.one, true),
            ("select_2_scale_x_avatar", HandleOrientation.Avatar, Vector3.zero, Quaternion.identity, new Vector3(1.3f, 1f, 1f), true),
            ("select_3_rotate_normal", HandleOrientation.Normal, Vector3.zero, Quaternion.identity, Vector3.one, true),
            ("select_4_push_inward", HandleOrientation.Global, new Vector3(0f, 0f, 0.03f), Quaternion.identity, Vector3.one, true),
            // 布の長さで届く動き（伸びない布は、①のように裾を下へ引いたり、②③のように裾の幅や高さを変えたりはできない）
            ("select_5_move_out_up", HandleOrientation.Global, new Vector3(0f, 0.02f, -0.03f), Quaternion.identity, Vector3.one, true),
            // ①と同じ動きを「形だけ動かす」で（布の計算をしないので、目標どおりに動いて、周りは伸びる）
            ("select_6_shape_only_move", HandleOrientation.Global, new Vector3(0f, -0.028f, -0.028f), Quaternion.identity, Vector3.one, false),
        };
        bool first = true;
        foreach (var (name, space, translation, baseRotation, scale, asCloth) in cases)
        {
            var settings = new ClothEditSettings
            {
                SelectionFalloff = FalloffPreset.Smooth,
                SelectionFalloffRadius = 0.05f,
                HandleSpace = space,
                SelectionMovesAsCloth = asCloth,
                SettleTimeoutSeconds = LayerSettleSeconds,
            };
            var controller = new ClothEditController(settings) { BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            try
            {
                if (!controller.TryStartLayered(new Renderer[] { skirt, petticoat }, out string startError)) throw new InvalidOperationException(startError);
                ClothEditSession session = controller.Session;
                float top = float.MinValue;
                foreach (Vector3 v in session.CurrentWorldVertices) top = Mathf.Max(top, v.y);
                var waist = new List<Vector3>();
                foreach (System.Numerics.Vector3 r in session.Cloth.Model.RestPositions)
                {
                    if (r.Y > top - 1.5f * session.Cloth.Spacing) waist.Add(new Vector3(r.X, r.Y, r.Z));
                }
                session.PinNear(waist, 0.004f);

                // 前の裾の帯を、矩形で選ぶ（外の布の後ろにあるペチコートは選ばれない）
                Vector3 low = camera.WorldToViewportPoint(new Vector3(-0.12f, 0.455f, -0.25f));
                Vector3 high = camera.WorldToViewportPoint(new Vector3(0.12f, 0.52f, -0.25f));
                var rect = Rect.MinMaxRect(low.x, low.y, high.x, high.y);
                Matrix4x4 viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;
                controller.SelectInRect(viewProjection, camera.transform.position, camera.transform.forward, false, rect, false);
                int petticoatSelected = 0;
                for (int k = 0; k < controller.SelectedParticles.Count; k++)
                {
                    int triangle = session.Cloth.Topology.TriangleToSource[session.Cloth.ParticleTriangle[controller.SelectedParticles[k]]];
                    if (session.TryGetMeshTriangle(triangle, out int rendererIndex, out _) && rendererIndex == 1) petticoatSelected++;
                }
                if (first)
                {
                    session.UpdateCurrentShape();
                    session.DrawPreview(camera);
                    DrawSelectionMarkers(camera, controller);
                    marked.Add(Shot(camera, "select_0_before_marked"));
                    first = false;
                }

                Vector3 pivot = controller.SelectionPivot;
                Quaternion frame = controller.HandleRotation;
                Quaternion rotation = name == "select_3_rotate_normal" ? Quaternion.AngleAxis(20f, frame * Vector3.forward) : baseRotation;
                var selected = new List<int>(controller.SelectedParticles);
                var start = new List<Vector3>();
                foreach (int i in selected) start.Add(new Vector3(session.Solver.Positions[i].X, session.Solver.Positions[i].Y, session.Solver.Positions[i].Z));

                var timer = System.Diagnostics.Stopwatch.StartNew();
                // ハンドルを引く操作に合わせて、少しずつ動かす
                controller.BeginSelectionTransform();
                for (int k = 1; k <= 12; k++)
                {
                    float f = k / 12f;
                    controller.UpdateSelectionTransform(translation * f, Quaternion.Slerp(Quaternion.identity, rotation, f), Vector3.Lerp(Vector3.one, scale, f), false);
                    controller.Tick(NextTime());
                }
                controller.EndSelectionTransform();
                Settle(controller);
                timer.Stop();

                // 目標に対する、選んだ点の動いた割合
                Quaternion inverse = Quaternion.Inverse(frame);
                float errorSum = 0f, errorMax = 0f;
                for (int k = 0; k < selected.Count; k++)
                {
                    Vector3 local = Vector3.Scale(inverse * (start[k] - pivot), scale);
                    Vector3 goal = pivot + rotation * (frame * local) + translation;
                    var p = session.Solver.Positions[selected[k]];
                    float error = Vector3.Distance(new Vector3(p.X, p.Y, p.Z), goal);
                    errorSum += error;
                    errorMax = Mathf.Max(errorMax, error);
                }
                session.UpdateCurrentShape();
                session.DrawPreview(camera);
                string penetration = $" / スカートがペチコートに入った最大 {SkirtPenetration(session, petticoat) * 1000f:F1} mm";
                Debug.Log($"[GP02描画] {name}: 選んだ点 {selected.Count}（ペチコート {petticoatSelected}）/ 目標との差 平均 {errorSum / selected.Count * 1000f:F1} mm 最大 {errorMax * 1000f:F1} mm / 最大の伸び {controller.LastStretch:P1}{penetration} / 粒子 {session.ParticleCount} / 操作 {timer.ElapsedMilliseconds} ms / 高速化 {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)}");
                plain.Add(Shot(camera, name));
                session.DrawPreview(camera);
                DrawSelectionMarkers(camera, controller);
                marked.Add(Shot(camera, name + "_marked"));
            }
            finally
            {
                controller.Dispose();
            }
        }
        SaveGrid(plain, 4, "select");
        SaveGrid(marked, 4, "select_marked");
    }

    // 選んだ点（赤）と、なめらかに減る範囲（黄〜橙）を、小さな四角で重ねる（シーンビューの重ね描きの代わり）
    static void DrawSelectionMarkers(Camera camera, ClothEditController controller)
    {
        ClothEditSession session = controller.Session;
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var triangles = new List<int>();
        Vector3 right = camera.transform.right, up = camera.transform.up, toCamera = -camera.transform.forward;
        void Add(int particle, Color color)
        {
            var p = session.Solver.Positions[particle];
            Vector3 c = new Vector3(p.X, p.Y, p.Z) + toCamera * 0.006f;
            int v = vertices.Count;
            const float h = 0.003f;
            vertices.Add(c - right * h - up * h);
            vertices.Add(c - right * h + up * h);
            vertices.Add(c + right * h + up * h);
            vertices.Add(c + right * h - up * h);
            for (int k = 0; k < 4; k++) colors.Add(color);
            triangles.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3 });
        }
        for (int k = 0; k < controller.SelectionWeightCount; k++)
        {
            float w = controller.SelectionWeightValue(k);
            Add(controller.SelectionWeightParticle(k), Color.Lerp(new Color(1f, 0.95f, 0.2f), new Color(1f, 0.5f, 0.05f), w));
        }
        foreach (int i in controller.SelectedParticles) Add(i, new Color(0.9f, 0.05f, 0.05f));
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        var material = new Material(Shader.Find("Sprites/Default"));
        Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, camera);
    }

    // ---- 対称の補助 ----
    // 上の段: ① 右（+X）の前の裾だけを持ち上げた（左右差の色分けつき。緑→黄→赤）② 「今の形を左右対称にする」（+X を基準）で左へ写した（色分けつき）③ ②の形だけ
    // 下の段: 衣装がアバターの中心から 6cm ずれている。④ そのまま対称編集で右の前の裾を持ち上げる（鏡の面がずれていて、相手のいない点＝赤紫が多く、左は動かない）
    // ⑤ 「鏡の面を自動で合わせる」のあとで同じ操作（左右そろって上がる）⑥ ⑤の左右差の色分け
    // ログに、左右差の最大、相手のいない点の割合、合わせた中心のずれを出す

    static void SymmetryScenes()
    {
        Camera camera = SetUpCamera(new Vector3(0f, 0.75f, -1.35f), new Vector3(0f, 0.55f, 0f));
        GameObject root = TestClothMenu.CreateInScene(null);
        var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
        foreach (Transform bone in skirt.bones) bone.localRotation = Quaternion.identity;
        var shots = new List<Texture2D>();

        // 上の段: 片側を持ち上げて、左右対称にする
        var controller = new ClothEditController(new ClothEditSettings { Falloff = FalloffPreset.Smooth }) { BrushRadius = 0.06f, InfluenceRadius = 0.3f };
        try
        {
            if (!controller.TryStart(skirt, out string error)) throw new InvalidOperationException(error);
            PinWaist(controller.Session);
            LiftFrontHem(controller, camera, 0.12f);
            ClothEditSymmetry.TryBuildMirrorPlane(controller.Settings, controller.AvatarRoot, true, out GP02.Core.MirrorPlane plane);
            float before = controller.Session.MaxMirrorDifference(plane, controller.Settings.MirrorTolerance);
            controller.Session.UpdateCurrentShape();
            controller.Session.DrawPreview(camera);
            DrawDifferenceDots(camera, controller.Session, plane, controller.Settings.MirrorTolerance);
            shots.Add(Shot(camera, "sym_1_one_sided"));
            controller.Settings.SymmetrizeSource = SymmetrySourceSide.Positive;
            int changed = controller.SymmetrizeShape();
            Settle(controller);
            float after = controller.Session.MaxMirrorDifference(plane, controller.Settings.MirrorTolerance);
            Debug.Log($"[GP02描画] 左右対称にする: 写した点 {changed} / 左右差の最大 {before * 1000f:F1} mm → {after * 1000f:F1} mm / 最大の伸び {controller.LastStretch:P1}");
            controller.Session.UpdateCurrentShape();
            controller.Session.DrawPreview(camera);
            DrawDifferenceDots(camera, controller.Session, plane, controller.Settings.MirrorTolerance);
            shots.Add(Shot(camera, "sym_2_symmetrized_marked"));
            controller.Session.DrawPreview(camera);
            shots.Add(Shot(camera, "sym_3_symmetrized"));
        }
        finally
        {
            controller.Dispose();
        }

        // 下の段: 中心からずれた衣装。アバターの基準（Animator）を -X へ 6cm ずらす
        var avatar = new GameObject("Avatar");
        avatar.AddComponent<Animator>();
        avatar.transform.position = new Vector3(-0.06f, 0f, 0f);
        root.transform.SetParent(avatar.transform, true);
        foreach (bool fit in new[] { false, true })
        {
            var settings = new ClothEditSettings { SymmetryEnabled = true, Falloff = FalloffPreset.Smooth };
            controller = new ClothEditController(settings) { BrushRadius = 0.06f, InfluenceRadius = 0.3f };
            try
            {
                if (!controller.TryStart(skirt, out string error)) throw new InvalidOperationException(error);
                PinWaist(controller.Session);
                float unmatchedBefore = controller.UnmatchedMirrorRatio;
                string fitLog = "";
                if (fit)
                {
                    controller.AutoFitSymmetryPlane(out string message);
                    fitLog = $" / {message}";
                }
                LiftFrontHem(controller, camera, 0.12f);
                controller.TryGetMirrorPlane(out GP02.Core.MirrorPlane plane);
                float difference = controller.Session.MaxMirrorDifference(plane, settings.MirrorTolerance);
                Debug.Log($"[GP02描画] 中心からずれた衣装（{(fit ? "自動で合わせた" : "合わせない")}）: 相手のいない点 {unmatchedBefore:P1} → {controller.UnmatchedMirrorRatio:P1} / 中心のずれ {settings.SymmetryOffset * 100f:F1} cm / 左右差の最大 {difference * 1000f:F1} mm{fitLog}");
                controller.Session.UpdateCurrentShape();
                controller.Session.DrawPreview(camera);
                if (!fit)
                {
                    var unmatched = new List<Vector3>();
                    controller.Session.GetUnmatchedMirrorPositions(plane, settings.MirrorTolerance, unmatched, 4000);
                    var colors = new List<Color>();
                    foreach (Vector3 _ in unmatched) colors.Add(ClothEditHelp.UnmatchedMirrorColor);
                    DrawDots(camera, unmatched, colors);
                    shots.Add(Shot(camera, "sym_4_offcenter_not_fitted"));
                }
                else
                {
                    shots.Add(Shot(camera, "sym_5_offcenter_fitted"));
                    controller.Session.DrawPreview(camera);
                    DrawDifferenceDots(camera, controller.Session, plane, settings.MirrorTolerance);
                    shots.Add(Shot(camera, "sym_6_offcenter_fitted_marked"));
                }
            }
            finally
            {
                controller.Dispose();
            }
        }
        SaveGrid(shots, 3, "symmetry");
    }

    // 右（+X）の前の裾寄りをつまみ、視点の平面の上で外上へ持ち上げて離し、仕上げる
    // ---- 貫通を直す（体のシェイプキー Hip_Big を 100 にすると、後ろの腰で体がスカートを突き抜ける。それを布で押し出す） ----
    // 1〜2段目: スカート（ペチコートは同じメッシュ）。体 Hip_Big 0 / 100（突き抜けている）/ 調べた所（赤い点）/ 直したあと / 保存したシェイプキー / 直したあとを横から
    // 3段目: スカートとペチコートを別のレンダラーにして、まとめて直す（ペチコートも一緒に押し出される）
    // 体は -executeMethod では SkinnedMeshRenderer の描画が古いままなので、BakeMesh で取り出して描く。
    // ウエストはピン留めしない（ピン留めした点は動かないので、真下の膨らみを布の長さで避けられず、伸びる）。
    // ログに、調べた結果、直した結果（残り・最大の伸び・時間）、保存した形が体に入った最大の深さを出す

    static void PenetrationScenes()
    {
        Camera camera = SetUpCamera(new Vector3(0.45f, 0.88f, 0.72f), new Vector3(0f, 0.7f, 0.03f));
        camera.aspect = 1f;
        // 後ろから見るので、光も後ろ（少し左）から当てる
        Light light = Object.FindObjectOfType<Light>();
        Quaternion backLight = Quaternion.Euler(35f, 170f, 0f);
        light.transform.rotation = backLight;
        Vector3 backPosition = camera.transform.position;
        Quaternion backRotation = camera.transform.rotation;
        var shots = new List<Texture2D>();
        foreach (bool separate in new[] { false, true })
        {
            GameObject root = TestClothMenu.CreateInScene(null, separate);
            var body = root.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            var skirt = root.transform.Find("Skirt").GetComponent<SkinnedMeshRenderer>();
            SkinnedMeshRenderer petticoat = separate ? root.transform.Find("Petticoat").GetComponent<SkinnedMeshRenderer>() : null;
            foreach (Transform bone in skirt.bones) bone.localRotation = Quaternion.identity;
            int hipBig = body.sharedMesh.GetBlendShapeIndex(TestClothMenu.HipBigShapeName);
            var renderers = separate ? new[] { body, skirt, petticoat } : new[] { body, skirt };
            string prefix = separate ? "pen_layered" : "pen";

            if (!separate)
            {
                body.SetBlendShapeWeight(hipBig, 0f);
                shots.Add(ShotWithBaked(camera, prefix + "_0_hip_big_0", renderers));
            }
            body.SetBlendShapeWeight(hipBig, 100f);
            shots.Add(ShotWithBaked(camera, prefix + "_1_hip_big_100", renderers));

            var controller = new ClothEditController(new ClothEditSettings()) { BrushRadius = 0.05f, InfluenceRadius = 0.3f };
            GameObject bodyView = BakedView(body);
            Mesh saved = null;
            try
            {
                Renderer[] targets = separate ? new Renderer[] { skirt, petticoat } : new Renderer[] { skirt };
                if (!controller.TryStartLayered(targets, out string error)) throw new InvalidOperationException(error);
                ClothEditSession session = controller.Session;
                GP02.Core.PenetrationReport found = controller.LastPenetration;
                Debug.Log($"[GP02描画] {prefix}: 編集を始めたときに調べた結果: {found} / {session.BodyDescription}");
                var points = new List<Vector3>();
                session.GetPenetrationPoints(points, 2000);
                session.UpdateCurrentShape();
                session.DrawPreview(camera);
                shots.Add(Shot(camera, prefix + "_2_detected_marked", (c, t) => PaintDots(c, t, points, new Color(1f, 0.12f, 0.1f))));

                var timer = System.Diagnostics.Stopwatch.StartNew();
                int remaining = controller.FixPenetration();
                timer.Stop();
                Debug.Log($"[GP02描画] {prefix}: 貫通を直しました（{timer.ElapsedMilliseconds} ms）: {controller.LastPenetrationFix} / 残り {remaining} / 直したあとに調べた結果: {controller.LastPenetration} / 粒子 {session.ParticleCount} / 高速化 {ClothEditSession.DescribeAcceleration(session.Solver.Kernel)}");
                session.GetPenetrationPoints(points, 2000);
                session.DrawPreview(camera);
                shots.Add(Shot(camera, prefix + "_3_fixed", (c, t) => PaintDots(c, t, points, new Color(1f, 0.12f, 0.1f))));
                if (!separate)
                {
                    saved = session.CreateMeshWithBlendShape("Fit_HipBig");
                }
            }
            finally
            {
                controller.Dispose();
            }

            if (saved != null)
            {
                // 保存したシェイプキーを 100 にして、Unity のスキニングで取り出した形を描く。体に入った最大の深さもログに出す
                skirt.sharedMesh = saved;
                skirt.SetBlendShapeWeight(saved.GetBlendShapeIndex("Fit_HipBig"), 100f);
                shots.Add(ShotWithBaked(camera, prefix + "_4_saved_100", skirt));
                Debug.Log($"[GP02描画] {prefix}: 保存したシェイプキー（100）のスカートが体に入った最大の深さ {DeepestInBody(skirt, body) * 1000f:F1} mm");

                // 横（+X 側）の少し後ろから、光もカメラの側から当てる（膨らみの上を布がどう覆っているかの輪郭を見る）
                camera.transform.position = new Vector3(0.78f, 0.8f, 0.3f);
                camera.transform.LookAt(new Vector3(0f, 0.7f, 0.05f));
                light.transform.rotation = Quaternion.Euler(30f, -110f, 0f);
                shots.Add(ShotWithBaked(camera, prefix + "_5_saved_side", skirt));
                camera.transform.SetPositionAndRotation(backPosition, backRotation);
                light.transform.rotation = backLight;
            }
            Object.DestroyImmediate(bodyView.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(bodyView);
            Object.DestroyImmediate(root);
        }
        SaveGrid(shots, 3, "penetration");
    }

    // skinned を BakeMesh で取り出した形の表示を作り、元のレンダラーを隠す（消すのは呼び出し側。元のレンダラーは隠したまま）
    static GameObject BakedView(SkinnedMeshRenderer skinned)
    {
        var baked = new Mesh();
        skinned.BakeMesh(baked, true);
        var view = new GameObject("Baked_" + skinned.name);
        view.transform.SetPositionAndRotation(skinned.transform.position, skinned.transform.rotation);
        view.AddComponent<MeshFilter>().sharedMesh = baked;
        view.AddComponent<MeshRenderer>().sharedMaterials = skinned.sharedMaterials;
        skinned.forceRenderingOff = true;
        return view;
    }

    // いくつかの SkinnedMeshRenderer を BakeMesh で取り出して描く（隠れていたものは、隠れたままにする）
    static Texture2D ShotWithBaked(Camera camera, string name, params SkinnedMeshRenderer[] skinned)
    {
        var views = new List<GameObject>();
        var hidden = new List<bool>();
        foreach (SkinnedMeshRenderer s in skinned)
        {
            hidden.Add(s.forceRenderingOff);
            if (!s.forceRenderingOff) views.Add(BakedView(s));
        }
        Texture2D texture = Shot(camera, name);
        for (int i = 0; i < skinned.Length; i++) skinned[i].forceRenderingOff = hidden[i];
        foreach (GameObject view in views)
        {
            Object.DestroyImmediate(view.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(view);
        }
        return texture;
    }

    // 点を、奥行きに関係なく画像の上に描く（白い縁の赤い四角。シーンビューの赤い点の代わり）
    static void PaintDots(Camera camera, Texture2D texture, List<Vector3> points, Color color)
    {
        foreach (int half in new[] { 3, 2 })
        {
            Color c = half == 3 ? Color.white : color;
            foreach (Vector3 p in points)
            {
                Vector3 screen = camera.WorldToScreenPoint(p);
                if (screen.z <= 0f) continue;
                int cx = Mathf.RoundToInt(screen.x), cy = Mathf.RoundToInt(screen.y);
                for (int dy = -half; dy <= half; dy++)
                {
                    for (int dx = -half; dx <= half; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x >= 0 && y >= 0 && x < texture.width && y < texture.height) texture.SetPixel(x, y, c);
                    }
                }
            }
        }
    }

    // skinned（BakeMesh）の頂点のうち、体（BakeMesh）の中に一番深く入ったものの深さ（メートル。入っていなければ 0 以下）
    static float DeepestInBody(SkinnedMeshRenderer skinned, SkinnedMeshRenderer body)
    {
        System.Numerics.Vector3[] Bake(SkinnedMeshRenderer s)
        {
            var baked = new Mesh();
            s.BakeMesh(baked, true);
            Matrix4x4 toWorld = Matrix4x4.TRS(s.transform.position, s.transform.rotation, Vector3.one);
            Vector3[] local = baked.vertices;
            Object.DestroyImmediate(baked);
            var world = new System.Numerics.Vector3[local.Length];
            for (int v = 0; v < local.Length; v++)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(local[v]);
                world[v] = new System.Numerics.Vector3(w.x, w.y, w.z);
            }
            return world;
        }
        var surface = new GP02.Core.BodySurface(Bake(body), body.sharedMesh.triangles);
        float deepest = float.MinValue;
        foreach (System.Numerics.Vector3 p in Bake(skinned))
        {
            if (surface.TryQuery(p, GP02.Core.BodySurface.DefaultMaxDistance, out GP02.Core.BodyHit hit)) deepest = Mathf.Max(deepest, -hit.SignedDistance);
        }
        return deepest;
    }

    static void LiftFrontHem(ClothEditController c, Camera camera, float x)
    {
        c.Mode = ClothEditMode.Grab;
        c.Settings.MoveDirection = MoveDirection.Free;
        Vector3 cameraPosition = camera.transform.position;
        var ray = new Ray(cameraPosition, (new Vector3(x, 0.47f, -0.3f) - cameraPosition).normalized);
        if (!c.PointerDown(ray, camera.transform.forward, camera.transform.up, 100f)) throw new InvalidOperationException("スカートに当たりませんでした。");
        Vector3 hit = c.HoverPoint;
        for (int k = 1; k <= 20; k++)
        {
            Vector3 target = hit + new Vector3(0.03f, 0.08f, 0f) * (k / 20f);
            c.PointerDrag(new Ray(cameraPosition, (target - cameraPosition).normalized));
            c.Tick(NextTime());
        }
        c.PointerUp();
        Settle(c);
    }

    // 左右差（緑 → 黄 → 赤）の点を重ねる（シーンビューの「左右差を色分け」の代わり）
    static void DrawDifferenceDots(Camera camera, ClothEditSession session, GP02.Core.MirrorPlane plane, float tolerance)
    {
        var positions = new List<Vector3>();
        var values = new List<float>();
        session.GetMirrorDifferenceDots(plane, tolerance, positions, values, 4000);
        var colors = new List<Color>();
        foreach (float v in values) colors.Add(ClothEditHelp.MirrorDifferenceColor(v));
        DrawDots(camera, positions, colors);
    }

    // 小さな四角をカメラに向けて重ねる
    static void DrawDots(Camera camera, List<Vector3> positions, List<Color> colors)
    {
        var vertices = new List<Vector3>();
        var vertexColors = new List<Color>();
        var triangles = new List<int>();
        Vector3 right = camera.transform.right, up = camera.transform.up, toCamera = -camera.transform.forward;
        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 c = positions[i] + toCamera * 0.006f;
            int v = vertices.Count;
            const float h = 0.0025f;
            vertices.Add(c - right * h - up * h);
            vertices.Add(c - right * h + up * h);
            vertices.Add(c + right * h + up * h);
            vertices.Add(c + right * h - up * h);
            Color color = colors[i];
            color.a = 1f;
            for (int k = 0; k < 4; k++) vertexColors.Add(color);
            triangles.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3 });
        }
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetColors(vertexColors);
        mesh.SetTriangles(triangles, 0);
        Graphics.DrawMesh(mesh, Matrix4x4.identity, new Material(Shader.Find("Sprites/Default")), 0, camera);
    }

    static void PinWaist(ClothEditSession session)
    {
        float top = float.MinValue;
        foreach (Vector3 v in session.CurrentWorldVertices) top = Mathf.Max(top, v.y);
        var waist = new List<Vector3>();
        foreach (System.Numerics.Vector3 r in session.Cloth.Model.RestPositions)
        {
            if (r.Y > top - 1.5f * session.Cloth.Spacing) waist.Add(new Vector3(r.X, r.Y, r.Z));
        }
        session.PinNear(waist, 0.004f);
    }

    // 前の裾寄りを、面の法線まわりに回す（pixels が回す量）か、拡大縮小する（負で縮める）
    static void RotateOrScale(ClothEditController c, ClothEditMode mode, float pixels)
    {
        c.Mode = mode;
        c.BrushRadius = 0.12f;
        Ray ray = FrontRay(0f, 0.5f);
        c.PointerDown(ray, Vector3.forward, 100f);
        for (int k = 1; k <= 20; k++)
        {
            c.PointerDrag(ray, 100f + pixels * k / 20f, false, false);
            c.Tick(NextTime());
        }
        c.PointerUp();
        Settle(c);
    }

    // スカートの頂点が、ペチコートの面より内に入った最大の深さ（メートル）。同じ高さの軸から頂点へ向かう水平なレイで比べる。
    // まとめて編集しているときは編集中のペチコートの形、そうでなければ元のペチコートの形と比べる
    static float SkirtPenetration(ClothEditSession session, SkinnedMeshRenderer petticoat)
    {
        Vector3[] world = session.CurrentWorldVertices;
        session.GetVertexRange(0, out int skirtStart, out int skirtCount);
        System.Numerics.Vector3[] petticoatVertices;
        if (session.RendererCount > 1)
        {
            session.GetVertexRange(1, out int start, out int count);
            petticoatVertices = new System.Numerics.Vector3[count];
            for (int v = 0; v < count; v++) petticoatVertices[v] = new System.Numerics.Vector3(world[start + v].x, world[start + v].y, world[start + v].z);
        }
        else
        {
            var baked = new Mesh();
            petticoat.BakeMesh(baked, true);
            Matrix4x4 toWorld = Matrix4x4.TRS(petticoat.transform.position, petticoat.transform.rotation, Vector3.one);
            Vector3[] local = baked.vertices;
            Object.DestroyImmediate(baked);
            petticoatVertices = new System.Numerics.Vector3[local.Length];
            for (int v = 0; v < local.Length; v++)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(local[v]);
                petticoatVertices[v] = new System.Numerics.Vector3(w.x, w.y, w.z);
            }
        }
        var bvh = new GP02.Core.TriangleBvh(petticoatVertices, petticoat.sharedMesh.triangles);
        Mesh skirtMesh = session.GetSourceMesh(0);
        int[] skirtTriangles = skirtMesh.GetTriangles(0);   // サブメッシュ 0 がスカート（1 はリボン）
        float worst = float.MinValue;
        foreach (int k in skirtTriangles)
        {
            Vector3 p = world[skirtStart + k];
            if (p.y > TestClothMenu.SkirtTopY - 0.02f || p.y < TestClothMenu.SkirtTopY - 0.85f * TestClothMenu.SkirtLength) continue;
            var radial = new System.Numerics.Vector3(p.x, 0f, p.z);
            float radius = radial.Length();
            if (!bvh.Raycast(new System.Numerics.Vector3(0f, p.y, 0f), radial / radius, petticoatVertices, out float inner, out _)) continue;
            worst = Mathf.Max(worst, inner - radius);
        }
        return worst;
    }

    static double _time = 100.0;

    static double NextTime()
    {
        _time += 0.016;
        return _time;
    }

    // 前（-Z 側）から、高さ y・横 x の位置を見るレイ
    static Ray FrontRay(float x, float y) => new Ray(new Vector3(x, y, -1.5f), Vector3.forward);

    // 右（+X 側）から、高さ y の位置を見るレイ
    static Ray SideRay(float y) => new Ray(new Vector3(1.5f, y, 0f), Vector3.left);

    // 右の裾を、X 軸に固定して外へ distance だけ引いて離し、仕上げる
    static void PullSideways(ClothEditController c, Camera camera, float distance)
    {
        c.Mode = ClothEditMode.Grab;
        c.BrushRadius = 0.07f;
        if (!c.PointerDown(SideRay(0.55f), camera.transform.forward, 100f)) throw new InvalidOperationException("スカートに当たりませんでした。");
        Vector3 hit = c.HoverPoint;
        Vector3 cameraPosition = camera.transform.position;
        for (int k = 1; k <= 20; k++)
        {
            Vector3 target = hit + new Vector3(distance * k / 20f, 0f, 0f);
            c.PointerDrag(new Ray(cameraPosition, (target - cameraPosition).normalized));
            c.Tick(NextTime());
        }
        c.PointerUp();
        Settle(c);
    }

    static void Settle(ClothEditController c)
    {
        for (int i = 0; i < 1000 && c.IsBusy; i++) c.Tick(NextTime());
    }

    // 新しい編集を始め（ウエストをピン留め）、act で操作して、結果を撮る
    static Texture2D Scenario(Camera camera, SkinnedMeshRenderer skirt, string name, ClothEditSettings settings, Action<ClothEditController> act)
    {
        return Scenario(camera, new Renderer[] { skirt }, name, settings, act, null);
    }

    // targets をまとめて編集する版。petticoat を渡すと、スカートがペチコートに入った深さもログに出す
    static Texture2D Scenario(Camera camera, Renderer[] targets, string name, ClothEditSettings settings, Action<ClothEditController> act, SkinnedMeshRenderer petticoat)
    {
        var controller = new ClothEditController(settings) { BrushRadius = 0.05f, InfluenceRadius = 0.3f };
        try
        {
            if (!controller.TryStartLayered(targets, out string error)) throw new InvalidOperationException(error);
            ClothEditSession session = controller.Session;

            // 実際の使い方に合わせて、ウエスト（一番上の段）をピン留めする
            float top = float.MinValue;
            foreach (Vector3 v in session.CurrentWorldVertices) top = Mathf.Max(top, v.y);
            var waist = new List<Vector3>();
            foreach (System.Numerics.Vector3 r in session.Cloth.Model.RestPositions)
            {
                if (r.Y > top - 1.5f * session.Cloth.Spacing) waist.Add(new Vector3(r.X, r.Y, r.Z));
            }
            session.PinNear(waist, 0.004f);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            act(controller);
            timer.Stop();
            session = controller.Session;
            session.UpdateCurrentShape();
            session.DrawPreview(camera);
            string penetration = petticoat != null ? $" / スカートがペチコートに入った最大 {SkirtPenetration(session, petticoat) * 1000f:F1} mm" : "";
            Debug.Log($"[GP02描画] {name}: 最大の伸び {controller.LastStretch:P1}{penetration} / ピン {session.PinnedCount} / 粒子 {session.ParticleCount} / 層どうしの接触 {session.LayerContacts.Count} 組 / 操作 {timer.ElapsedMilliseconds} ms");
            return Shot(camera, name);
        }
        finally
        {
            controller.Dispose();
        }
    }

    static Camera SetUpCamera(Vector3 position, Vector3 target)
    {
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.transform.position = position;
        camera.transform.LookAt(target);
        camera.fieldOfView = 40f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.85f, 0.88f, 0.92f);
        var light = new GameObject("Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);
        return camera;
    }

    // paint を渡すと、描いた画像に上から描き足す（カメラは描いたときのまま。シーンビューの重ね描きのように、奥行きに関係なく見える印に使う）
    static Texture2D Shot(Camera camera, string name, Action<Camera, Texture2D> paint = null)
    {
        var rt = new RenderTexture(Size, Size, 24) { antiAliasing = 4 };
        camera.targetTexture = rt;
        camera.Render();
        RenderTexture.active = rt;
        var texture = new Texture2D(Size, Size, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        paint?.Invoke(camera, texture);
        texture.Apply();
        RenderTexture.active = null;
        camera.targetTexture = null;
        Object.DestroyImmediate(rt);
        File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), texture.EncodeToPNG());
        return texture;
    }

    static Texture2D ShotBaked(Camera camera, SkinnedMeshRenderer skinned, string name)
    {
        var baked = new Mesh();
        skinned.BakeMesh(baked, true);
        var view = new GameObject("Baked");
        view.transform.SetPositionAndRotation(skinned.transform.position, skinned.transform.rotation);
        view.AddComponent<MeshFilter>().sharedMesh = baked;
        view.AddComponent<MeshRenderer>().sharedMaterials = skinned.sharedMaterials;
        skinned.forceRenderingOff = true;
        Texture2D texture = Shot(camera, name);
        skinned.forceRenderingOff = false;
        Object.DestroyImmediate(view);
        Object.DestroyImmediate(baked);
        return texture;
    }

    static void SaveGrid(List<Texture2D> shots, int columns, string name)
    {
        int rows = (shots.Count + columns - 1) / columns;
        var grid = new Texture2D(Size * columns, Size * rows, TextureFormat.RGB24, false);
        for (int i = 0; i < shots.Count; i++)
        {
            // Texture2D の原点は左下なので、1枚目が左上に来るように行を逆にする
            int x = i % columns, y = rows - 1 - i / columns;
            grid.SetPixels(x * Size, y * Size, Size, Size, shots[i].GetPixels());
        }
        grid.Apply();
        File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), grid.EncodeToPNG());
    }
}
