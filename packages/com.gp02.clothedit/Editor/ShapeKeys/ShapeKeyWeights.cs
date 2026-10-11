using System;
using System.Collections.Generic;
using GP02.Core;
using Unity.Collections;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// メッシュのボーンウェイトの読み書きと、データに記録したボーンウェイトの変更（<see cref="ShapeKeyWeightEdit"/>）の作成・合成・当てる・外す。
    /// 変更はボーンを名前とアバターのルートからの道のりで持つので、当てるときにレンダラーのボーンから探し直す。
    /// レンダラーに無いボーン（スカートに無い背骨など）は、レンダラーのボーンの末尾に足し、メッシュのバインドポーズも同じ順に足す。
    /// </summary>
    public static class ShapeKeyWeights
    {
        /// <summary>メッシュの頂点ごとのボーンウェイト（メッシュのボーンの番号。頂点ごとに大きい順）。ウェイトが無ければ、すべての頂点が空。</summary>
        public static BoneWeightSets Read(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            int count = mesh.vertexCount;
            NativeArray<byte> perVertex = mesh.GetBonesPerVertex();
            NativeArray<BoneWeight1> all = mesh.GetAllBoneWeights();
            var offsets = new int[count + 1];
            if (perVertex.Length != count) return new BoneWeightSets(offsets, new int[0], new float[0]);
            for (int v = 0; v < count; v++) offsets[v + 1] = offsets[v] + perVertex[v];
            var bones = new int[all.Length];
            var weights = new float[all.Length];
            for (int k = 0; k < all.Length; k++)
            {
                bones[k] = all[k].boneIndex;
                weights[k] = all[k].weight;
            }
            return new BoneWeightSets(offsets, bones, weights);
        }

        /// <summary>メッシュの頂点ごとの、ボーンウェイトの数の一番多いもの。</summary>
        public static int MaxBonesPerVertex(Mesh mesh)
        {
            int max = 0;
            NativeArray<byte> perVertex = mesh.GetBonesPerVertex();
            for (int v = 0; v < perVertex.Length; v++) max = Math.Max(max, perVertex[v]);
            return max;
        }

        /// <summary>
        /// メッシュのボーンウェイトを sets にする（頂点ごとに大きい順に並べ直す）。bindposes が null でなければ、バインドポーズも置き換える。
        /// </summary>
        public static void Write(Mesh mesh, BoneWeightSets sets, Matrix4x4[] bindposes)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (sets == null || sets.VertexCount != mesh.vertexCount) throw new ArgumentException("頂点の数が、メッシュと合いません。", nameof(sets));
            if (bindposes != null) mesh.bindposes = bindposes;
            int count = sets.VertexCount;
            var perVertex = new NativeArray<byte>(count, Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(sets.Bones.Length, Allocator.Temp);
            try
            {
                var order = new List<BoneWeight1>();
                int write = 0;
                for (int v = 0; v < count; v++)
                {
                    order.Clear();
                    for (int k = sets.Offsets[v]; k < sets.Offsets[v + 1]; k++) order.Add(new BoneWeight1 { boneIndex = sets.Bones[k], weight = sets.Weights[k] });
                    // Unity は大きい順を求める（同じ重みなら番号の小さいボーンが先にして、結果を毎回同じにする）
                    order.Sort((a, b) => a.weight != b.weight ? b.weight.CompareTo(a.weight) : a.boneIndex.CompareTo(b.boneIndex));
                    perVertex[v] = (byte)Math.Min(255, order.Count);
                    for (int k = 0; k < order.Count && k < 255; k++) weights[write++] = order[k];
                }
                if (write < weights.Length)
                {
                    var trimmed = new NativeArray<BoneWeight1>(write, Allocator.Temp);
                    NativeArray<BoneWeight1>.Copy(weights, trimmed, write);
                    weights.Dispose();
                    weights = trimmed;
                }
                mesh.SetBoneWeights(perVertex, weights);
            }
            finally
            {
                perVertex.Dispose();
                weights.Dispose();
            }
        }

        /// <summary>bone のアバターのルート root からの道のり（"Armature/Hips/Spine"）。root の下に無ければ名前だけ。</summary>
        public static string PathOf(Transform bone, Transform root)
        {
            if (bone == null) return "";
            if (root == null || bone == root || !bone.IsChildOf(root)) return bone == root ? "" : bone.name;
            var parts = new List<string>();
            for (Transform t = bone; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// アバターのルート root の下で、道のり path（無ければ名前 name）のボーンを探す。道のりで見つからなければ、名前が同じもののうち、
        /// 道のりの後ろのほう（親の名前）が一番長く一致するもの。見つからなければ null。
        /// </summary>
        public static Transform FindBone(Transform root, string path, string name)
        {
            if (root == null) return null;
            if (!string.IsNullOrEmpty(path))
            {
                Transform exact = root.Find(path);
                if (exact != null) return exact;
            }
            if (string.IsNullOrEmpty(name)) return null;
            string[] wanted = string.IsNullOrEmpty(path) ? new[] { name } : path.Split('/');
            Transform best = null;
            int bestScore = -1;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != name) continue;
                int score = 0;
                Transform walk = t;
                for (int i = wanted.Length - 1; i >= 0 && walk != null && walk != root && walk.name == wanted[i]; i--, walk = walk.parent) score++;
                if (score <= bestScore) continue;
                best = t;
                bestScore = score;
            }
            return best;
        }

        /// <summary>
        /// 頂点ごとの新しいウェイト（changed の頂点だけを記録する）を、データに入れる形にする。
        /// bones はメッシュのボーンの番号ごとの Transform（末尾に足したボーンを含む）、bindposes は同じ順のバインドポーズ。
        /// </summary>
        public static ShapeKeyWeightEdit CreateEdit(BoneWeightSets weights, bool[] changed, IReadOnlyList<Transform> bones, IReadOnlyList<Matrix4x4> bindposes, Transform avatarRoot)
        {
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (changed == null || changed.Length != weights.VertexCount) throw new ArgumentException("頂点の数が合いません。", nameof(changed));
            var edit = new ShapeKeyWeightEdit();
            var used = new Dictionary<int, int>();
            var names = new List<string>();
            var paths = new List<string>();
            var binds = new List<Matrix4x4>();
            var vertices = new List<int>();
            var offsets = new List<int> { 0 };
            var editBones = new List<int>();
            var editWeights = new List<float>();
            for (int v = 0; v < changed.Length; v++)
            {
                if (!changed[v]) continue;
                for (int k = weights.Offsets[v]; k < weights.Offsets[v + 1]; k++)
                {
                    int b = weights.Bones[k];
                    if (!used.TryGetValue(b, out int e))
                    {
                        e = names.Count;
                        used.Add(b, e);
                        Transform bone = b >= 0 && b < bones.Count ? bones[b] : null;
                        names.Add(bone != null ? bone.name : "");
                        paths.Add(PathOf(bone, avatarRoot));
                        binds.Add(b >= 0 && b < bindposes.Count ? bindposes[b] : Matrix4x4.identity);
                    }
                    editBones.Add(e);
                    editWeights.Add(weights.Weights[k]);
                }
                vertices.Add(v);
                offsets.Add(editBones.Count);
            }
            edit.BoneNames = names.ToArray();
            edit.BonePaths = paths.ToArray();
            edit.Bindposes = binds.ToArray();
            edit.Vertices = vertices.ToArray();
            edit.Offsets = vertices.Count > 0 ? offsets.ToArray() : new int[0];
            edit.Bones = editBones.ToArray();
            edit.Weights = editWeights.ToArray();
            return edit;
        }

        /// <summary>
        /// 2つの変更を合わせた、新しい変更を返す。同じ頂点は newer のウェイトにする（あとから寄せ直した分で上書き）。どちらも書き換えない。
        /// </summary>
        public static ShapeKeyWeightEdit Merge(ShapeKeyWeightEdit older, ShapeKeyWeightEdit newer)
        {
            var result = new ShapeKeyWeightEdit();
            var names = new List<string>();
            var paths = new List<string>();
            var binds = new List<Matrix4x4>();
            var boneIndex = new Dictionary<string, int>();
            int[] Remap(ShapeKeyWeightEdit edit)
            {
                if (edit == null || edit.BoneNames == null) return new int[0];
                var map = new int[edit.BoneNames.Length];
                for (int j = 0; j < map.Length; j++)
                {
                    string path = edit.BonePaths != null && j < edit.BonePaths.Length ? edit.BonePaths[j] : "";
                    string key = path + "\n" + edit.BoneNames[j];
                    if (!boneIndex.TryGetValue(key, out int index))
                    {
                        index = names.Count;
                        boneIndex.Add(key, index);
                        names.Add(edit.BoneNames[j]);
                        paths.Add(path);
                        binds.Add(edit.Bindposes != null && j < edit.Bindposes.Length ? edit.Bindposes[j] : Matrix4x4.identity);
                    }
                    map[j] = index;
                }
                return map;
            }
            int[] olderMap = Remap(older), newerMap = Remap(newer);

            var vertices = new List<int>();
            var offsets = new List<int> { 0 };
            var bones = new List<int>();
            var weights = new List<float>();
            void Copy(ShapeKeyWeightEdit edit, int[] map, int k)
            {
                for (int i = edit.Offsets[k]; i < edit.Offsets[k + 1]; i++)
                {
                    bones.Add(map[edit.Bones[i]]);
                    weights.Add(edit.Weights[i]);
                }
                vertices.Add(edit.Vertices[k]);
                offsets.Add(bones.Count);
            }
            int a = 0, b = 0;
            int olderCount = older != null ? older.VertexCount : 0, newerCount = newer != null ? newer.VertexCount : 0;
            while (a < olderCount || b < newerCount)
            {
                int va = a < olderCount ? older.Vertices[a] : int.MaxValue;
                int vb = b < newerCount ? newer.Vertices[b] : int.MaxValue;
                if (vb <= va)
                {
                    Copy(newer, newerMap, b++);
                    if (va == vb) a++;
                }
                else
                {
                    Copy(older, olderMap, a++);
                }
            }
            result.BoneNames = names.ToArray();
            result.BonePaths = paths.ToArray();
            result.Bindposes = binds.ToArray();
            result.Vertices = vertices.ToArray();
            result.Offsets = vertices.Count > 0 ? offsets.ToArray() : new int[0];
            result.Bones = bones.ToArray();
            result.Weights = weights.ToArray();
            return result;
        }

        /// <summary>
        /// メッシュ（元のメッシュの複製など）に、記録したボーンウェイトの変更を当てる（メッシュを書き換える）。当てたあとのレンダラーのボーン
        /// （メッシュのバインドポーズと同じ数。足したボーンは末尾）を返すので、呼び出し側がレンダラーに入れる。
        /// ボーンは、レンダラーのボーンのうちメッシュのバインドポーズの数までから、道のり → 名前の順に探し、無ければアバターの中から探して足す
        /// （バインドポーズは記録したもの）。見つからないボーンの分は除いて残りで合計 1 にする（全部見つからなければ、その頂点は元のまま）。
        /// </summary>
        /// <param name="warnings">見つからなかったボーンなどの知らせを足す（null なら足さない）</param>
        public static Transform[] Apply(Mesh mesh, ShapeKeyWeightEdit edit, SkinnedMeshRenderer renderer, List<string> warnings)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            Transform[] current = renderer.bones;
            Matrix4x4[] bindposes = mesh.bindposes;
            if (edit == null || edit.IsEmpty) return current;

            var bones = new List<Transform>(bindposes.Length);
            for (int i = 0; i < bindposes.Length; i++) bones.Add(i < current.Length ? current[i] : null);
            var binds = new List<Matrix4x4>(bindposes);
            Transform root = ShapeKeyExporter.FindAvatarRoot(renderer.transform);
            var bonePaths = new List<string>(bones.Count);
            foreach (Transform bone in bones) bonePaths.Add(PathOf(bone, root));

            var resolved = new int[edit.BoneNames.Length];
            var missing = new List<string>();
            for (int j = 0; j < resolved.Length; j++)
            {
                string name = edit.BoneNames[j];
                string path = edit.BonePaths != null && j < edit.BonePaths.Length ? edit.BonePaths[j] : "";
                int index = -1;
                for (int i = 0; i < bones.Count && index < 0; i++)
                {
                    if (bones[i] != null && !string.IsNullOrEmpty(path) && bonePaths[i] == path) index = i;
                }
                for (int i = 0; i < bones.Count && index < 0; i++)
                {
                    if (bones[i] != null && bones[i].name == name) index = i;
                }
                if (index < 0)
                {
                    Transform found = FindBone(root, path, name);
                    if (found != null)
                    {
                        index = bones.Count;
                        bones.Add(found);
                        bonePaths.Add(PathOf(found, root));
                        binds.Add(edit.Bindposes != null && j < edit.Bindposes.Length ? edit.Bindposes[j] : found.worldToLocalMatrix * renderer.transform.localToWorldMatrix);
                    }
                    else
                    {
                        missing.Add(string.IsNullOrEmpty(path) ? name : path);
                    }
                }
                resolved[j] = index;
            }
            if (missing.Count > 0 && warnings != null)
            {
                warnings.Add(L10n.Tr($"{renderer.name}: ウェイトの変更のボーン {string.Join("、", missing)} が見つからないので、その分を除いて当てました。",
                    $"{renderer.name}: the bones {string.Join(", ", missing)} of the weight change were not found, so their part was left out."));
            }

            BoneWeightSets sets = Read(mesh);
            int count = sets.VertexCount;
            var offsets = new int[count + 1];
            var outBones = new List<int>(sets.Bones.Length + edit.Bones.Length);
            var outWeights = new List<float>(sets.Bones.Length + edit.Bones.Length);
            int e = 0;
            int skipped = 0;
            for (int v = 0; v < count; v++)
            {
                while (e < edit.Vertices.Length && edit.Vertices[e] < v) e++;
                bool replaced = false;
                if (e < edit.Vertices.Length && edit.Vertices[e] == v)
                {
                    float total = 0f;
                    for (int k = edit.Offsets[e]; k < edit.Offsets[e + 1]; k++)
                    {
                        if (resolved[edit.Bones[k]] >= 0) total += edit.Weights[k];
                    }
                    if (total > 1e-6f)
                    {
                        for (int k = edit.Offsets[e]; k < edit.Offsets[e + 1]; k++)
                        {
                            int b = resolved[edit.Bones[k]];
                            if (b < 0) continue;
                            outBones.Add(b);
                            outWeights.Add(edit.Weights[k] / total);
                        }
                        replaced = true;
                    }
                }
                if (!replaced)
                {
                    for (int k = sets.Offsets[v]; k < sets.Offsets[v + 1]; k++)
                    {
                        outBones.Add(sets.Bones[k]);
                        outWeights.Add(sets.Weights[k]);
                    }
                }
                offsets[v + 1] = outBones.Count;
            }
            for (int k = 0; k < edit.Vertices.Length; k++)
            {
                if (edit.Vertices[k] >= count) skipped++;
            }
            if (skipped > 0 && warnings != null)
            {
                warnings.Add(L10n.Tr($"{renderer.name}: ウェイトの変更のうち {skipped} 頂点は、メッシュの頂点の数を超えているので飛ばしました。",
                    $"{renderer.name}: {skipped} vertices of the weight change are beyond the mesh's vertex count and were skipped."));
            }
            Write(mesh, new BoneWeightSets(offsets, outBones.ToArray(), outWeights.ToArray()), binds.ToArray());
            return bones.ToArray();
        }

        /// <summary>
        /// working（変更を当てたメッシュ）の、変更した頂点のウェイトを source（元のメッシュ）のものに戻し、使われなくなった、
        /// source のボーンより後ろの（足した）ボーンを外す（メッシュを書き換える）。戻したあとのレンダラーのボーンを返す。
        /// </summary>
        public static Transform[] Restore(Mesh working, Mesh source, ShapeKeyWeightEdit edit, SkinnedMeshRenderer renderer)
        {
            if (working == null) throw new ArgumentNullException(nameof(working));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (working.vertexCount != source.vertexCount) throw new ArgumentException("頂点の数が、元のメッシュと違います。", nameof(source));
            BoneWeightSets sets = Read(working), original = Read(source);
            int count = sets.VertexCount;
            var restore = new bool[count];
            if (edit != null && edit.Vertices != null)
            {
                foreach (int v in edit.Vertices)
                {
                    if ((uint)v < (uint)count) restore[v] = true;
                }
            }
            var offsets = new int[count + 1];
            var bones = new List<int>(sets.Bones.Length);
            var weights = new List<float>(sets.Bones.Length);
            int maxUsed = -1;
            for (int v = 0; v < count; v++)
            {
                BoneWeightSets from = restore[v] ? original : sets;
                for (int k = from.Offsets[v]; k < from.Offsets[v + 1]; k++)
                {
                    bones.Add(from.Bones[k]);
                    weights.Add(from.Weights[k]);
                    if (from.Weights[k] > 0f) maxUsed = Math.Max(maxUsed, from.Bones[k]);
                }
                offsets[v + 1] = bones.Count;
            }
            Matrix4x4[] bindposes = working.bindposes;
            int keep = Math.Min(bindposes.Length, Math.Max(source.bindposes.Length, maxUsed + 1));
            var keptBindposes = new Matrix4x4[keep];
            Array.Copy(bindposes, keptBindposes, keep);
            Write(working, new BoneWeightSets(offsets, bones.ToArray(), weights.ToArray()), keptBindposes);
            Transform[] current = renderer.bones;
            var result = new Transform[keep];
            for (int i = 0; i < keep; i++) result[i] = i < current.Length ? current[i] : null;
            return result;
        }

        /// <summary>レンダラーのボーンを、メッシュのバインドポーズの数に合わせた配列（多ければ末尾を外し、少なければ null で埋める）。合っていれば null。</summary>
        public static Transform[] FitBones(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null) return null;
            Transform[] bones = renderer.bones;
            int count = mesh.bindposes.Length;
            if (bones.Length == count || count == 0) return null;
            var fitted = new Transform[count];
            for (int i = 0; i < count; i++) fitted[i] = i < bones.Length ? bones[i] : null;
            return fitted;
        }
    }
}
