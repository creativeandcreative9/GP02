using System;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// データの保護: データやメッシュが見つからない・頂点数が違うといった状態を見つけて、直す方法を用意する。画面に依存しない。
    /// 直す操作はすべて Unity の Undo で戻せる。
    /// </summary>
    public static class ShapeKeyProtection
    {
        [Flags]
        public enum Issue
        {
            None = 0,

            /// <summary>同じオブジェクトに、メッシュを描くレンダラーが無い。</summary>
            NoRenderer = 1,

            /// <summary>コンポーネントにデータ（アセット）が設定されていない、または消された。</summary>
            DataMissing = 2,

            /// <summary>データの元のメッシュが見つからない。</summary>
            SourceMeshMissing = 4,

            /// <summary>レンダラーのメッシュが None。</summary>
            RendererMeshMissing = 8,

            /// <summary>レンダラーのメッシュの頂点数が、データと違う。</summary>
            VertexCountMismatch = 16,

            /// <summary>コンポーネントの元のメッシュが、データの元のメッシュと違う。</summary>
            SourceMeshDiffers = 32,
        }

        public static Issue Diagnose(GP02ShapeKeys component, Renderer renderer)
        {
            Issue issues = Issue.None;
            if (renderer == null) issues |= Issue.NoRenderer;
            if (component == null || component.Data == null)
            {
                issues |= Issue.DataMissing;
                if (component != null && component.SourceMesh == null) issues |= Issue.SourceMeshMissing;
            }
            else
            {
                GP02ShapeKeyData data = component.Data;
                if (data.SourceMesh == null) issues |= Issue.SourceMeshMissing;
                else if (data.SourceMesh.vertexCount != data.VertexCount) issues |= Issue.VertexCountMismatch;
                if (component.SourceMesh != data.SourceMesh) issues |= Issue.SourceMeshDiffers;
            }

            if (renderer != null)
            {
                Mesh mesh = ClothEditController.GetMesh(renderer);
                if (mesh == null) issues |= Issue.RendererMeshMissing;
                else if (component != null && component.Data != null && mesh.vertexCount != component.Data.VertexCount) issues |= Issue.VertexCountMismatch;
            }
            return issues;
        }

        /// <summary>レンダラーのメッシュを元のメッシュに戻せるか（元のメッシュが分かっているか）。</summary>
        public static bool CanRestoreMesh(GP02ShapeKeys component)
        {
            if (component == null) return false;
            return component.SourceMesh != null || (component.Data != null && component.Data.SourceMesh != null);
        }

        /// <summary>
        /// データの元のメッシュが無いとき、レンダラーの今のメッシュ（頂点数がデータと同じとき）を元のメッシュにする。直せたら true。
        /// </summary>
        public static bool AdoptRendererMeshAsSource(GP02ShapeKeys component, Renderer renderer)
        {
            if (component == null || component.Data == null || renderer == null) return false;
            Mesh mesh = ClothEditController.GetMesh(renderer);
            if (mesh == null || mesh.vertexCount != component.Data.VertexCount) return false;
            Undo.RecordObject(component.Data, "GP02 " + L10n.Tr("元のメッシュを設定", "Set source mesh"));
            component.Data.SourceMesh = mesh;
            EditorUtility.SetDirty(component.Data);
            SyncComponentSource(component);
            return true;
        }

        /// <summary>コンポーネントの元のメッシュを、データの元のメッシュに合わせる。</summary>
        public static void SyncComponentSource(GP02ShapeKeys component)
        {
            if (component == null || component.Data == null) return;
            Undo.RecordObject(component, "GP02 " + L10n.Tr("元のメッシュを合わせる", "Sync source mesh"));
            component.SourceMesh = component.Data.SourceMesh;
            EditorUtility.SetDirty(component);
        }

        /// <summary>コンポーネントのデータを選び直す。元のメッシュが空なら、データのものを入れる。</summary>
        public static void SetData(GP02ShapeKeys component, GP02ShapeKeyData data)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            Undo.RecordObject(component, "GP02 " + L10n.Tr("データを設定", "Set data"));
            component.Data = data;
            if (data != null && data.SourceMesh != null && component.SourceMesh == null) component.SourceMesh = data.SourceMesh;
            EditorUtility.SetDirty(component);
        }
    }
}
