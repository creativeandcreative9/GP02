using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの重みのプレビュー。元のメッシュ・アセットは書き換えず、保存されない一時メッシュ（HideFlags.DontSave）を
    /// レンダラーに一時的に割り当てて、重みをかけた形を見せる。
    /// レンダラーのシェイプキーの重み（BlendShape の値）も触らない。
    /// <para>
    /// 一時メッシュを割り当てたまま残すと、シーンを保存したときにメッシュが None になる。そのため次のどれでも必ず元に戻す:
    /// アセンブリの再読み込み・プレイモード・シーンの保存／切り替え・ビルド・エディタの終了・選択の変更・書き出しや Undo の前。
    /// 万一戻し損ねても、再読み込みのあとに記録（SessionState）から探して直す。
    /// 元に戻すのは、レンダラーが今も一時メッシュを使っているときだけ（他の操作で差し替えられていたら、上書きしない）。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class ShapeKeyPreview
    {
        const string SessionKey = "GP02.ShapeKeyPreview.Records";
        const string TempSuffix = "_GP02Preview";

        sealed class Item
        {
            public Renderer Renderer;
            public GP02ShapeKeyData Data;
            public Mesh Original;
            public Mesh Temp;
            public Vector3[] BaseVertices;
            public Vector3[] BaseNormals;       // メッシュに法線が無ければ空
            public Vector4[] BaseTangents;      // メッシュに接線が無ければ空
        }

        struct Suspended
        {
            public Renderer Renderer;
            public GP02ShapeKeyData Data;
        }

        static readonly List<Item> Items = new List<Item>();
        static readonly List<Suspended> SuspendedItems = new List<Suspended>();

        /// <summary>プレビューを始めた・止めた・更新したときに呼ばれる（画面の更新用）。</summary>
        public static event Action Changed;

        static ShapeKeyPreview()
        {
            AssemblyReloadEvents.beforeAssemblyReload += EndAll;
            EditorApplication.quitting += EndAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += Validate;
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += Validate;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneOpening += OnSceneOpening;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.newSceneCreated += OnNewSceneCreated;
            RecoverAfterReload();
        }

        // ------------------------------------------------------------------
        // 公開
        // ------------------------------------------------------------------

        public static int ActiveCount => Items.Count;

        public static bool IsActive(Renderer renderer) => Find(renderer) != null;

        /// <summary>プレビューに使っている一時メッシュ。始めていなければ null。</summary>
        public static Mesh GetTempMesh(Renderer renderer) => Find(renderer)?.Temp;

        /// <summary>プレビューを始める前のメッシュ。始めていなければ null。</summary>
        public static Mesh GetOriginalMesh(Renderer renderer) => Find(renderer)?.Original;

        /// <summary>
        /// データのプレビューの重みを反映する。有効なエントリの重みがすべて 0 なら、プレビューを止めて元に戻す。
        /// 始められないとき（メッシュが無い、頂点数が違う）は false を返し、理由を error に入れる。
        /// </summary>
        public static bool Apply(Renderer renderer, GP02ShapeKeyData data, out string error)
        {
            error = null;
            if (!ShapeKeyListOperations.HasActivePreviewWeight(data))
            {
                End(renderer);
                return true;
            }
            return Begin(renderer, data, out error);
        }

        /// <summary>プレビューを始める。始めていれば、重みを反映し直す。</summary>
        public static bool Begin(Renderer renderer, GP02ShapeKeyData data, out string error)
        {
            error = null;
            if (renderer == null)
            {
                error = L10n.Tr("プレビューする対象のレンダラーがありません。", "There is no renderer to preview.");
                return false;
            }
            if (data == null)
            {
                error = L10n.Tr("シェイプキーのデータがありません。", "There is no shape key data.");
                return false;
            }

            Item item = Find(renderer);
            if (item != null)
            {
                item.Data = data;
                Recompute(item);
                Changed?.Invoke();
                return true;
            }

            Mesh current = ClothEditController.GetMesh(renderer);
            if (current == null)
            {
                error = L10n.Tr("レンダラーにメッシュがありません。", "The renderer has no mesh.");
                return false;
            }
            if (current.vertexCount != data.VertexCount)
            {
                error = L10n.Tr(
                    $"メッシュの頂点数（{current.vertexCount}）が、データの頂点数（{data.VertexCount}）と違うのでプレビューできません。",
                    $"The mesh vertex count ({current.vertexCount}) differs from the data ({data.VertexCount}), so it cannot be previewed.");
                return false;
            }

            Vector3[] vertices = current.vertices;
            if (vertices.Length != current.vertexCount)
            {
                error = L10n.Tr("メッシュの頂点を読み取れません（Read/Write を確認してください）。", "The mesh vertices cannot be read (check Read/Write).");
                return false;
            }
            Mesh temp = Object.Instantiate(current);
            temp.name = current.name + TempSuffix;
            temp.hideFlags = HideFlags.DontSave;

            item = new Item
            {
                Renderer = renderer,
                Data = data,
                Original = current,
                Temp = temp,
                BaseVertices = vertices,
                BaseNormals = current.normals,
                BaseTangents = current.tangents,
            };
            Items.Add(item);
            Persist();
            SetMesh(renderer, temp);
            Recompute(item);
            Changed?.Invoke();
            return true;
        }

        /// <summary>このレンダラーのプレビューを止めて、メッシュを元に戻す。始めていなければ何もしない。</summary>
        public static void End(Renderer renderer)
        {
            Item item = Find(renderer);
            if (item == null) return;
            Finish(item);
            Persist();
            Changed?.Invoke();
        }

        /// <summary>すべてのプレビューを止めて、メッシュを元に戻す。</summary>
        public static void EndAll()
        {
            if (Items.Count == 0) return;
            foreach (Item item in Items.ToArray()) Finish(item);
            Persist();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        static Item Find(Renderer renderer)
        {
            if (renderer == null) return null;
            foreach (Item item in Items)
            {
                if (item.Renderer == renderer) return item;
            }
            return null;
        }

        static void SetMesh(Renderer renderer, Mesh mesh)
        {
            // Undo には記録しない（記録すると、Ctrl+Z で一時メッシュが戻ってきてしまう）
            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = mesh;
            }
        }

        // Items から外し、一時メッシュを使っていれば元のメッシュに戻し、一時メッシュを捨てる
        static void Finish(Item item)
        {
            Items.Remove(item);
            if (item.Renderer != null && ClothEditController.GetMesh(item.Renderer) == item.Temp)
            {
                Mesh restore = item.Original != null ? item.Original : FallbackMesh(item.Renderer);
                SetMesh(item.Renderer, restore);
            }
            if (item.Temp != null) Object.DestroyImmediate(item.Temp);
        }

        // 元のメッシュが消えていたときの戻し先（コンポーネントが覚えている元のメッシュ）
        static Mesh FallbackMesh(Renderer renderer)
        {
            var component = renderer != null ? renderer.GetComponent<GP02ShapeKeys>() : null;
            if (component == null) return null;
            if (component.SourceMesh != null) return component.SourceMesh;
            if (component.Data != null && component.Data.SourceMesh != null) return component.Data.SourceMesh;
            return component.ExportedMesh;
        }

        static void Recompute(Item item)
        {
            GP02ShapeKeyData data = item.Data;
            int count = item.BaseVertices.Length;
            var vertices = (Vector3[])item.BaseVertices.Clone();
            bool changeNormals = false, changeTangents = false;
            Vector3[] normals = item.BaseNormals.Length == count ? (Vector3[])item.BaseNormals.Clone() : null;
            Vector4[] tangents = item.BaseTangents.Length == count ? (Vector4[])item.BaseTangents.Clone() : null;

            if (data != null)
            {
                foreach (ShapeKeyEntry entry in data.Entries)
                {
                    if (entry == null || !entry.Enabled || entry.Frames.Count == 0 || entry.PreviewWeight == 0f) continue;
                    if (!ShapeKeyLibrary.IsEntryCompatible(entry, count)) continue;
                    ShapeKeyFrame frame = ShapeKeyLibrary.EvaluateEntry(entry, entry.PreviewWeight, count);
                    for (int v = 0; v < count; v++) vertices[v] += frame.DeltaVertices[v];
                    if (normals != null && frame.HasNormals)
                    {
                        changeNormals = true;
                        for (int v = 0; v < count; v++) normals[v] += frame.DeltaNormals[v];
                    }
                    if (tangents != null && frame.HasTangents)
                    {
                        changeTangents = true;
                        for (int v = 0; v < count; v++) tangents[v] += new Vector4(frame.DeltaTangents[v].x, frame.DeltaTangents[v].y, frame.DeltaTangents[v].z, 0f);
                    }
                }
            }

            Mesh temp = item.Temp;
            temp.vertices = vertices;
            if (changeNormals)
            {
                for (int v = 0; v < count; v++) normals[v] = normals[v].sqrMagnitude > 1e-12f ? normals[v].normalized : normals[v];
                temp.normals = normals;
            }
            else if (item.BaseNormals.Length == count)
            {
                temp.normals = item.BaseNormals;
            }
            if (changeTangents)
            {
                for (int v = 0; v < count; v++)
                {
                    var xyz = new Vector3(tangents[v].x, tangents[v].y, tangents[v].z);
                    if (xyz.sqrMagnitude > 1e-12f) xyz.Normalize();
                    tangents[v] = new Vector4(xyz.x, xyz.y, xyz.z, tangents[v].w);
                }
                temp.tangents = tangents;
            }
            else if (item.BaseTangents.Length == count)
            {
                temp.tangents = item.BaseTangents;
            }
            temp.RecalculateBounds();
        }

        // ------------------------------------------------------------------
        // 自動で元に戻すきっかけ
        // ------------------------------------------------------------------

        // レンダラーが消えた、または他の操作でメッシュが差し替えられたプレビューは、一時メッシュを捨てて記録から外す
        // （差し替えられたメッシュは上書きしない）
        /// <summary>
        /// 状態を確認する。レンダラーが消えた・他の操作でメッシュが差し替えられたプレビューを、一時メッシュを捨てて終わらせる。
        /// エディタの更新と Undo / Redo のたびに自動で呼ばれる。
        /// </summary>
        public static void Validate()
        {
            if (Items.Count == 0) return;
            bool changed = false;
            foreach (Item item in Items.ToArray())
            {
                if (item.Renderer != null && ClothEditController.GetMesh(item.Renderer) == item.Temp && item.Temp != null) continue;
                Finish(item);
                changed = true;
            }
            if (changed)
            {
                Persist();
                Changed?.Invoke();
            }
        }

        static void OnSelectionChanged() => EndUnselected();

        /// <summary>選択に入っていないオブジェクトのプレビューを終わらせる（選択が変わるたびに自動で呼ばれる）。</summary>
        public static void EndUnselected()
        {
            if (Items.Count == 0) return;
            bool changed = false;
            foreach (Item item in Items.ToArray())
            {
                if (item.Renderer != null && Selection.Contains(item.Renderer.gameObject)) continue;
                Finish(item);
                changed = true;
            }
            if (changed)
            {
                Persist();
                Changed?.Invoke();
            }
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                SuspendedItems.Clear();
                EndAll();
            }
        }

        // 保存の間だけ元に戻し、保存が終わったらプレビューを再開する
        static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (Items.Count == 0) return;
            SuspendedItems.Clear();
            foreach (Item item in Items) SuspendedItems.Add(new Suspended { Renderer = item.Renderer, Data = item.Data });
            EndAll();
        }

        static void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (SuspendedItems.Count == 0) return;
            Suspended[] resume = SuspendedItems.ToArray();
            SuspendedItems.Clear();
            foreach (Suspended s in resume)
            {
                if (s.Renderer != null && s.Data != null) Apply(s.Renderer, s.Data, out _);
            }
        }

        static void OnSceneOpening(string path, OpenSceneMode mode)
        {
            SuspendedItems.Clear();
            EndAll();
        }

        static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            SuspendedItems.Clear();
            EndAll();
        }

        static void OnNewSceneCreated(UnityEngine.SceneManagement.Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            SuspendedItems.Clear();
            EndAll();
        }

        // ------------------------------------------------------------------
        // 戻し損ねたときの救済
        // ------------------------------------------------------------------

        // 「レンダラーの InstanceID,元のメッシュの InstanceID;」の並び
        static void Persist()
        {
            try
            {
                var builder = new StringBuilder();
                foreach (Item item in Items)
                {
                    if (item.Renderer == null || item.Original == null) continue;
                    builder.Append(item.Renderer.GetInstanceID().ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.Original.GetInstanceID().ToString(CultureInfo.InvariantCulture)).Append(';');
                }
                SessionState.SetString(SessionKey, builder.ToString());
            }
            catch (Exception)
            {
                // 記録は救済用なので、書けなくても本体の動きは止めない
            }
        }

        // テスト用: 状態を何も戻さずに忘れる（再読み込みで C# の状態だけが消えた場合を作る）
        internal static void ForgetWithoutRestoring()
        {
            Items.Clear();
            SuspendedItems.Clear();
        }

        internal static void RecoverAfterReload()
        {
            string text;
            try { text = SessionState.GetString(SessionKey, ""); }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(text)) return;
            SessionState.EraseString(SessionKey);

            foreach (string record in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] ids = record.Split(',');
                if (ids.Length != 2 || !int.TryParse(ids[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rendererId)
                    || !int.TryParse(ids[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meshId)) continue;
                var renderer = EditorUtility.InstanceIDToObject(rendererId) as Renderer;
                if (renderer == null) continue;

                Mesh current = ClothEditController.GetMesh(renderer);
                bool isLeftoverTemp = current != null && (current.hideFlags & HideFlags.DontSave) != 0 && current.name.EndsWith(TempSuffix);
                if (current != null && !isLeftoverTemp) continue;   // 戻し済み、または他の操作で差し替え済み

                Mesh original = EditorUtility.InstanceIDToObject(meshId) as Mesh;
                if (original == null) original = FallbackMesh(renderer);
                if (original != null) SetMesh(renderer, original);
                if (isLeftoverTemp) Object.DestroyImmediate(current);
            }
        }
    }

    /// <summary>ビルドの前に、プレビューの一時メッシュを必ず元に戻す。</summary>
    public sealed class ShapeKeyPreviewBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -10000;

        public void OnPreprocessBuild(BuildReport report)
        {
            ShapeKeyPreview.EndAll();
        }
    }
}
