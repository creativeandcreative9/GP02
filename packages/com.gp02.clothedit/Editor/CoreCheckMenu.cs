using System.Diagnostics;
using GP02.Core;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using NVector3 = System.Numerics.Vector3;

// 名前空間を GP02.Editor にすると UnityEditor.Editor クラスと名前がぶつかるので、EditorTools にしている
namespace GP02.EditorTools
{
    /// <summary>
    /// Unity上で計算の中核が動くかを確かめるためのメニュー。
    /// 選んだオブジェクトのメッシュを1cm間隔の粒子にして、粒子の数などをコンソールに出す。
    /// </summary>
    internal static class CoreCheckMenu
    {
        const string MenuPath = "Tools/GP02/選んだメッシュを粒子化して確認";

        [MenuItem(MenuPath)]
        static void Run()
        {
            GameObject target = Selection.activeGameObject;
            Mesh mesh = FindMesh(target);
            if (mesh == null)
            {
                EditorUtility.DisplayDialog("GP02", "MeshFilter か SkinnedMeshRenderer を持つオブジェクトを選んでください。", "OK");
                return;
            }

            // 粒子の間隔はアバターの実寸で決めるので、オブジェクトの拡大縮小を反映する（ざっくりの確認用）
            Vector3 scale = target.transform.lossyScale;
            Vector3[] source = mesh.vertices;
            var vertices = new NVector3[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                Vector3 v = Vector3.Scale(source[i], scale);
                vertices[i] = new NVector3(v.x, v.y, v.z);
            }

            var timer = Stopwatch.StartNew();
            ParticleCloth cloth = ParticleCloth.Build(vertices, mesh.triangles);
            timer.Stop();

            Debug.Log($"[GP02] {mesh.name}: 頂点 {source.Length} / 粒子 {cloth.Model.ParticleCount} / " +
                $"部品 {cloth.Topology.IslandCount} / 粗い段 {cloth.Hierarchy.LevelCount} / 準備 {timer.ElapsedMilliseconds}ms");
        }

        [MenuItem(MenuPath, true)]
        static bool Validate() => FindMesh(Selection.activeGameObject) != null;

        static Mesh FindMesh(GameObject target)
        {
            if (target == null) return null;
            var skinned = target.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null) return skinned.sharedMesh;
            var filter = target.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
