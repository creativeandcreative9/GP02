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
        });
    }

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

    static Texture2D Shot(Camera camera, string name)
    {
        var rt = new RenderTexture(Size, Size, 24) { antiAliasing = 4 };
        camera.targetTexture = rt;
        camera.Render();
        RenderTexture.active = rt;
        var texture = new Texture2D(Size, Size, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
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
