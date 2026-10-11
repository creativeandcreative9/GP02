using System.Collections.Generic;
using System.IO;
using GP02.EditorTools;
using UnityEditor;
using UnityEngine;

// クラウドで見た目を確かめるための描画（tools/unity/render.sh から -executeMethod で呼ぶ）。
// テスト用の衣装を作り、編集前・編集中のプレビュー・保存したシェイプキーを画像にして、1枚に並べる。
public static class RenderScenes
{
    const int Size = 640;
    const string OutDir = "/out";

    public static void EditAndSave()
    {
        try
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
            EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
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
