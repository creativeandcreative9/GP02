using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GP02.Tests
{
    /// <summary>
    /// シェイプキーの一覧まわりのテストの共通部品。形はプログラムで作った格子の板。
    /// 作ったオブジェクトとアセットは TearDown で消し、プレビューも必ず元に戻す。
    /// </summary>
    public abstract class ShapeKeyUiTestBase
    {
        protected const string TempRoot = "Assets/GP02_ShapeKeyUiTestTemp";
        protected const string MeshFolder = TempRoot + "/Meshes";
        protected const string DataFolder = TempRoot + "/Data";
        protected const int Cells = 14;
        protected const float Size = 0.28f;      // 2cm 間隔
        protected const int Stride = Cells + 1;

        readonly List<Object> _created = new List<Object>();

        [SetUp]
        public void BaseSetUp()
        {
            ShapeKeyPreview.EndAll();
            ShapeKeyListOperations.Clipboard.Clear();
            Undo.ClearAll();
        }

        [TearDown]
        public void BaseTearDown()
        {
            ShapeKeyPreview.EndAll();
            foreach (Object o in _created)
            {
                if (o == null) continue;
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            ShapeKeyListOperations.Clipboard.Clear();
            AssetDatabase.DeleteAsset(TempRoot);
            Undo.ClearAll();
        }

        protected T Track<T>(T obj) where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        /// <summary>XY 平面の格子の板。(Cells + 1)^2 個の頂点で、X 方向に左右対称（中心は Size / 2）。</summary>
        protected Mesh CreateGridMesh(string name = "Grid")
        {
            var vertices = new Vector3[Stride * Stride];
            var uv = new Vector2[vertices.Length];
            for (int r = 0; r <= Cells; r++)
            for (int c = 0; c <= Cells; c++)
            {
                vertices[r * Stride + c] = new Vector3(Size * c / Cells, Size * r / Cells, 0f);
                uv[r * Stride + c] = new Vector2((float)c / Cells, (float)r / Cells);
            }
            var triangles = new List<int>();
            for (int r = 0; r < Cells; r++)
            for (int c = 0; c < Cells; c++)
            {
                int i = r * Stride + c;
                triangles.AddRange(new[] { i, i + Stride, i + 1, i + 1, i + Stride, i + Stride + 1 });
            }
            var mesh = Track(new Mesh { name = name });
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            return mesh;
        }

        protected static Vector3[] Ramp(int count, float scale)
        {
            var deltas = new Vector3[count];
            for (int v = 0; v < count; v++) deltas[v] = new Vector3(0.0005f * (v % 3), 0.001f * (v % 7), scale * (1 + v % 5));
            return deltas;
        }

        protected static ShapeKeyEntry Entry(string name, Vector3[] deltas, float weight = 100f)
        {
            var entry = new ShapeKeyEntry { Name = name };
            entry.Frames.Add(new ShapeKeyFrame { Weight = weight, DeltaVertices = deltas });
            return entry;
        }

        protected GP02ShapeKeyData NewData(Mesh mesh, params ShapeKeyEntry[] entries)
        {
            var data = Track(ScriptableObject.CreateInstance<GP02ShapeKeyData>());
            data.SourceMesh = mesh;
            data.VertexCount = mesh.vertexCount;
            data.Entries.AddRange(entries);
            return data;
        }

        protected Renderer CreateRenderer(Mesh mesh, bool skinned, string name = "Target")
        {
            GameObject go = Track(new GameObject(name));
            if (skinned)
            {
                var skinnedRenderer = go.AddComponent<SkinnedMeshRenderer>();
                skinnedRenderer.sharedMesh = mesh;
                return skinnedRenderer;
            }
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        protected static void AssertSame(Vector3[] expected, Vector3[] actual, float tolerance, string what = "")
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(Vector3.Distance(expected[i], actual[i]), Is.LessThanOrEqualTo(tolerance), $"{what} 頂点 {i}");
            }
        }

        protected static Vector3[] Combine(Vector3[] a, float wa, Vector3[] b, float wb)
        {
            var result = new Vector3[a.Length];
            for (int i = 0; i < a.Length; i++) result[i] = a[i] * wa + b[i] * wb;
            return result;
        }

        /// <summary>操作の前後で Undo のグループを区切る（画面ではイベントごとに区切られる）。</summary>
        protected static void NextUndoGroup() => Undo.IncrementCurrentGroup();

        protected static Vector3[] FrameVertices(Mesh mesh, int shape, int frame)
        {
            var vertices = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shape, frame, vertices, null, null);
            return vertices;
        }
    }
}
