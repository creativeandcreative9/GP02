using System.Collections.Generic;
using GP02.Core;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// ウェイトの変更のテスト用の小さなアバター（プログラムで作る）。格子の板（<see cref="ShapeKeyUiTestBase"/> の CreateGridMesh）の
    /// 下の半分をボーン A、上の半分をボーン B でスキニングし、レンダラーのボーンに無いボーン C もアーマチュアに置く。
    /// NDMF のテストからも使う。Root を消せば、メッシュ以外はすべて消える。
    /// </summary>
    public sealed class WeightEditTestAvatar
    {
        /// <summary>格子の1列の頂点の数（<see cref="ShapeKeyUiTestBase"/> と同じ）。</summary>
        public const int Stride = 15;

        public GameObject Root;
        public Transform Armature, BoneA, BoneB, BoneC;
        public SkinnedMeshRenderer Renderer;
        public Mesh Source;

        public static WeightEditTestAvatar Create(Mesh grid, string name = "WeightAvatar")
        {
            var a = new WeightEditTestAvatar { Source = grid, Root = new GameObject(name) };
            a.Root.AddComponent<Animator>();
            a.Armature = new GameObject("Armature").transform;
            a.Armature.SetParent(a.Root.transform, false);
            a.BoneA = new GameObject("BoneA").transform;
            a.BoneA.SetParent(a.Armature, false);
            a.BoneA.localPosition = new Vector3(0.14f, 0f, 0f);
            a.BoneB = new GameObject("BoneB").transform;
            a.BoneB.SetParent(a.BoneA, false);
            a.BoneB.localPosition = new Vector3(0f, 0.14f, 0f);
            a.BoneC = new GameObject("BoneC").transform;
            a.BoneC.SetParent(a.Armature, false);
            a.BoneC.localPosition = new Vector3(0.3f, 0.1f, 0f);
            var child = new GameObject("Skirt");
            child.transform.SetParent(a.Root.transform, false);
            a.Renderer = child.AddComponent<SkinnedMeshRenderer>();

            int count = grid.vertexCount;
            var weights = new BoneWeight[count];
            for (int v = 0; v < count; v++) weights[v] = new BoneWeight { boneIndex0 = v / Stride < 7 ? 0 : 1, weight0 = 1f };
            grid.boneWeights = weights;
            grid.bindposes = new[] { a.BindposeOf(a.BoneA), a.BindposeOf(a.BoneB) };
            a.Renderer.sharedMesh = grid;
            a.Renderer.bones = new[] { a.BoneA, a.BoneB };
            a.Renderer.rootBone = a.BoneA;
            return a;
        }

        /// <summary>レンダラーの座標から見たボーンのバインドポーズ（今の姿勢を基準にしたもの）。</summary>
        public Matrix4x4 BindposeOf(Transform bone) => bone.worldToLocalMatrix * Renderer.transform.localToWorldMatrix;

        /// <summary>下から rows 列の頂点を、C 0.75・A 0.25 にした変更（ボーンは名前とアバターのルートからの道のりで持つ）。</summary>
        public ShapeKeyWeightEdit CreateEdit(int rows = 2)
        {
            int count = Source.vertexCount;
            var offsets = new int[count + 1];
            var bones = new List<int>();
            var values = new List<float>();
            var changed = new bool[count];
            for (int v = 0; v < count; v++)
            {
                if (v < rows * Stride)
                {
                    changed[v] = true;
                    bones.Add(2);
                    values.Add(0.75f);
                    bones.Add(0);
                    values.Add(0.25f);
                }
                else
                {
                    bones.Add(v / Stride < 7 ? 0 : 1);
                    values.Add(1f);
                }
                offsets[v + 1] = bones.Count;
            }
            var sets = new BoneWeightSets(offsets, bones.ToArray(), values.ToArray());
            return ShapeKeyWeights.CreateEdit(sets, changed, new[] { BoneA, BoneB, BoneC },
                new[] { BindposeOf(BoneA), BindposeOf(BoneB), BindposeOf(BoneC) }, Root.transform);
        }

        /// <summary>メッシュの頂点 vertex の、ボーン bone（メッシュのボーンの番号）のウェイト。</summary>
        public static float WeightOf(Mesh mesh, int vertex, int bone)
        {
            BoneWeightSets sets = ShapeKeyWeights.Read(mesh);
            float total = 0f;
            for (int k = sets.Offsets[vertex]; k < sets.Offsets[vertex + 1]; k++)
            {
                if (sets.Bones[k] == bone) total += sets.Weights[k];
            }
            return total;
        }
    }

    /// <summary>
    /// ボーンウェイトの変更（ウェイトを体に寄せた結果）の記録・合成・当てる・戻すと、書き出し・ビルド時の適用で変更が入ることを、
    /// 小さなアバター（<see cref="WeightEditTestAvatar"/>）で確かめる。
    /// </summary>
    public class ShapeKeyWeightEditTests : ShapeKeyUiTestBase
    {
        WeightEditTestAvatar MakeAvatar()
        {
            WeightEditTestAvatar a = WeightEditTestAvatar.Create(CreateGridMesh("Skirt"));
            Track(a.Root);
            return a;
        }

        Mesh Copy(Mesh mesh) => Track(Object.Instantiate(mesh));

        [Test]
        public void CreateEdit_RecordsOnlyTheChangedVertices_WithBoneNamesAndPaths()
        {
            WeightEditTestAvatar a = MakeAvatar();
            ShapeKeyWeightEdit edit = a.CreateEdit();
            Assert.That(edit.VertexCount, Is.EqualTo(2 * WeightEditTestAvatar.Stride));
            Assert.That(edit.IsEmpty, Is.False);
            Assert.That(edit.BoneNames, Is.EquivalentTo(new[] { "BoneC", "BoneA" }), "変えた頂点で使うボーンだけ");
            Assert.That(edit.BonePaths[System.Array.IndexOf(edit.BoneNames, "BoneC")], Is.EqualTo("Armature/BoneC"));
            Assert.That(edit.BonePaths[System.Array.IndexOf(edit.BoneNames, "BoneA")], Is.EqualTo("Armature/BoneA"));
            Assert.That(edit.Bindposes.Length, Is.EqualTo(edit.BoneNames.Length));
            Assert.That(new ShapeKeyWeightEdit().IsEmpty, Is.True);
        }

        [Test]
        public void Apply_AddsAMissingBoneAtTheEnd_AndWritesTheWeights()
        {
            WeightEditTestAvatar a = MakeAvatar();
            Mesh mesh = Copy(a.Source);
            var warnings = new List<string>();
            Transform[] bones = ShapeKeyWeights.Apply(mesh, a.CreateEdit(), a.Renderer, warnings);

            Assert.That(warnings, Is.Empty);
            Assert.That(bones, Is.EqualTo(new[] { a.BoneA, a.BoneB, a.BoneC }), "レンダラーに無いボーン C を末尾に足す");
            Assert.That(mesh.bindposes.Length, Is.EqualTo(3));
            Assert.That(mesh.bindposes[2], Is.EqualTo(a.BindposeOf(a.BoneC)));
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, 0, 2), Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, 0, 0), Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, 100, 0), Is.EqualTo(1f), "変えていない頂点はそのまま");
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, 200, 1), Is.EqualTo(1f));
            Assert.That(WeightEditTestAvatar.WeightOf(a.Source, 0, 0), Is.EqualTo(1f), "元のメッシュは変えない");
        }

        [Test]
        public void Apply_FindsTheBonesInACopyOfTheAvatar_AndLeavesOutBonesThatAreMissing()
        {
            WeightEditTestAvatar a = MakeAvatar();
            ShapeKeyWeightEdit edit = a.CreateEdit();

            // 複製したアバター（ビルドのとき）では、複製の中のボーンを道のりで探す
            GameObject clone = Track(Object.Instantiate(a.Root));
            var cloneRenderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Mesh mesh = Copy(a.Source);
            Transform[] bones = ShapeKeyWeights.Apply(mesh, edit, cloneRenderer, null);
            Assert.That(bones.Length, Is.EqualTo(3));
            Assert.That(bones[2], Is.EqualTo(clone.transform.Find("Armature/BoneC")), "複製のボーン C");

            // ボーン C が無ければ、その分を除いて合計 1 にし、知らせる
            a.BoneC.name = "Renamed";
            Mesh other = Copy(a.Source);
            var warnings = new List<string>();
            Transform[] fewer = ShapeKeyWeights.Apply(other, edit, a.Renderer, warnings);
            Assert.That(fewer.Length, Is.EqualTo(2));
            Assert.That(warnings.Count, Is.EqualTo(1));
            StringAssert.Contains("BoneC", warnings[0]);
            Assert.That(WeightEditTestAvatar.WeightOf(other, 0, 0), Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Merge_TheNewerChangeWinsForTheSameVertex()
        {
            WeightEditTestAvatar a = MakeAvatar();
            ShapeKeyWeightEdit older = a.CreateEdit(2);

            // 新しい変更: 1〜3 列目を B 1.0 にする（1 列目は古い変更と重なる）
            int count = a.Source.vertexCount;
            var offsets = new int[count + 1];
            var bones = new List<int>();
            var values = new List<float>();
            var changed = new bool[count];
            for (int v = 0; v < count; v++)
            {
                changed[v] = v >= WeightEditTestAvatar.Stride && v < 3 * WeightEditTestAvatar.Stride;
                bones.Add(changed[v] ? 1 : 0);
                values.Add(1f);
                offsets[v + 1] = bones.Count;
            }
            ShapeKeyWeightEdit newer = ShapeKeyWeights.CreateEdit(new BoneWeightSets(offsets, bones.ToArray(), values.ToArray()), changed,
                new[] { a.BoneA, a.BoneB, a.BoneC }, new[] { a.BindposeOf(a.BoneA), a.BindposeOf(a.BoneB), a.BindposeOf(a.BoneC) }, a.Root.transform);
            ShapeKeyWeightEdit merged = ShapeKeyWeights.Merge(older, newer);
            Assert.That(merged.VertexCount, Is.EqualTo(3 * WeightEditTestAvatar.Stride));

            Mesh mesh = Copy(a.Source);
            Transform[] result = ShapeKeyWeights.Apply(mesh, merged, a.Renderer, null);
            Assert.That(result.Length, Is.EqualTo(3));
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, 0, 2), Is.EqualTo(0.75f).Within(1e-6f), "古い変更だけの頂点");
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, WeightEditTestAvatar.Stride + 3, 1), Is.EqualTo(1f).Within(1e-6f), "重なった頂点は新しい変更");
            Assert.That(WeightEditTestAvatar.WeightOf(mesh, 2 * WeightEditTestAvatar.Stride + 3, 1), Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Restore_PutsBackTheOriginalWeights_AndRemovesTheAddedBone()
        {
            WeightEditTestAvatar a = MakeAvatar();
            ShapeKeyWeightEdit edit = a.CreateEdit();
            Mesh working = Copy(a.Source);
            a.Renderer.sharedMesh = working;
            a.Renderer.bones = ShapeKeyWeights.Apply(working, edit, a.Renderer, null);
            Assert.That(a.Renderer.bones.Length, Is.EqualTo(3));

            Transform[] bones = ShapeKeyWeights.Restore(working, a.Source, edit, a.Renderer);
            Assert.That(bones, Is.EqualTo(new[] { a.BoneA, a.BoneB }));
            Assert.That(working.bindposes.Length, Is.EqualTo(2));
            for (int v = 0; v < working.vertexCount; v++)
            {
                int bone = v / WeightEditTestAvatar.Stride < 7 ? 0 : 1;
                Assert.That(WeightEditTestAvatar.WeightOf(working, v, bone), Is.EqualTo(1f).Within(1e-6f), $"頂点 {v}");
            }
        }

        [Test]
        public void ExportAndBuildTimeApply_IncludeTheRecordedWeightChange()
        {
            WeightEditTestAvatar a = MakeAvatar();
            int n = a.Source.vertexCount;
            GP02ShapeKeyData data = NewData(a.Source, Entry("Lift", Ramp(n, 0.003f)));
            data.WeightEdit = a.CreateEdit();
            Assert.That(data.HasWeightEdit, Is.True);
            var component = a.Renderer.gameObject.AddComponent<GP02ShapeKeys>();
            component.Data = data;
            component.SourceMesh = a.Source;

            // メッシュに書き出す: BlendShape と一緒に、ウェイトの変更が入り、足したボーンがレンダラーに付く
            Mesh exported = ShapeKeyExporter.ExportToMesh(component, true, MeshFolder);
            Assert.That(a.Renderer.sharedMesh, Is.SameAs(exported));
            Assert.That(exported.blendShapeCount, Is.EqualTo(1));
            Assert.That(a.Renderer.bones, Is.EqualTo(new[] { a.BoneA, a.BoneB, a.BoneC }));
            Assert.That(exported.bindposes.Length, Is.EqualTo(3));
            Assert.That(WeightEditTestAvatar.WeightOf(exported, 0, 2), Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(WeightEditTestAvatar.WeightOf(a.Source, 0, 0), Is.EqualTo(1f), "元のメッシュは変えない");

            // 元のメッシュに戻すと、足したボーンも外れる
            Assert.That(ShapeKeyExporter.RestoreSourceMesh(component), Is.True);
            Assert.That(a.Renderer.sharedMesh, Is.SameAs(a.Source));
            Assert.That(a.Renderer.bones, Is.EqualTo(new[] { a.BoneA, a.BoneB }));

            // ビルド時の適用（NDMF の本体）: 複製のアバターで、複製のボーン C を足す。シーンのアバターは変わらない
            component.ApplyMode = ShapeKeyApplyMode.NonDestructive;
            GameObject clone = Track(Object.Instantiate(a.Root));
            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(clone, null);
            Assert.That(result.Applied, Is.EqualTo(1));
            Assert.That(result.Warnings, Is.Empty);
            var built = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Track(built.sharedMesh);
            Assert.That(built.bones.Length, Is.EqualTo(3));
            Assert.That(built.bones[2], Is.EqualTo(clone.transform.Find("Armature/BoneC")));
            Assert.That(built.sharedMesh.bindposes.Length, Is.EqualTo(3));
            Assert.That(built.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(WeightEditTestAvatar.WeightOf(built.sharedMesh, 0, 2), Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(WeightEditTestAvatar.WeightOf(built.sharedMesh, 100, 0), Is.EqualTo(1f));
            Assert.That(a.Renderer.sharedMesh, Is.SameAs(a.Source));
            Assert.That(a.Renderer.bones.Length, Is.EqualTo(2));
        }
    }
}
