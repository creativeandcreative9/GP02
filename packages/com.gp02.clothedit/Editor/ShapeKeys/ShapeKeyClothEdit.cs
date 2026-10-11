using System;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの一覧と、布の編集（<see cref="ClothEditController"/>）をつなぐ。
    /// 「布で編集する」で、そのレンダラーの編集を始め、エントリの差分（最後のフレーム）を編集中の形として読み込む。
    /// 編集の画面（ウィンドウ）は一覧とは別なので、ウィンドウが自分のコントローラーを <see cref="ControllerProvider"/> に登録して使う。
    /// </summary>
    public static class ShapeKeyClothEdit
    {
        /// <summary>
        /// 布の編集ウィンドウが、自分のコントローラーを返す関数を登録する（ウィンドウが開いている間だけ有効な値を返す）。
        /// 登録が無いと、一覧の「布で編集する」は動かない（シーンでつまむ操作を受けるのはウィンドウなので）。
        /// </summary>
        public static Func<ClothEditController> ControllerProvider;

        /// <summary>布の編集ウィンドウを開く処理。既定は Tools/GP02/布の編集 のメニュー。</summary>
        public static Action OpenWindow = () => EditorApplication.ExecuteMenuItem("Tools/GP02/布の編集");

        /// <summary>今「布で編集する」で読み込んでいるエントリのコンポーネント。</summary>
        public static GP02ShapeKeys ActiveComponent { get; private set; }

        /// <summary>今「布で編集する」で読み込んでいるエントリの名前（保存の既定の名前）。</summary>
        public static string ActiveEntryName { get; private set; } = "";

        /// <summary>
        /// 一覧から「布で編集する」を頼む。ウィンドウのコントローラーがあれば、編集を始めて差分を読み込む。
        /// 始められないときは false を返し、利用者向けの理由を message に入れる。
        /// </summary>
        public static bool Request(GP02ShapeKeys component, ShapeKeyEntry entry, out string message)
        {
            ClothEditController controller = ControllerProvider?.Invoke();
            if (controller == null)
            {
                OpenWindow?.Invoke();
                controller = ControllerProvider?.Invoke();
            }
            if (controller == null)
            {
                message = L10n.Tr(
                    "布の編集ウィンドウとつながっていません。「Tools/GP02/布の編集」を開いてから、もう一度試してください。",
                    "Not connected to the cloth edit window. Open \"Tools/GP02/Cloth Edit\" and try again.");
                return false;
            }
            return Begin(controller, component, entry, out message);
        }

        /// <summary>
        /// コントローラーで編集を始め（同じレンダラーを編集中ならそのまま使い）、エントリの最後のフレームの差分を、Undo の1段として読み込む。
        /// 保存したピン留めの頂点も復元する。
        /// </summary>
        public static bool Begin(ClothEditController controller, GP02ShapeKeys component, ShapeKeyEntry entry, out string message)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (component == null || component.Data == null)
            {
                message = L10n.Tr("シェイプキーのデータがありません。", "There is no shape key data.");
                return false;
            }
            Renderer renderer = ClothEditController.FindRenderer(component.gameObject);
            if (renderer == null)
            {
                message = L10n.Tr("このオブジェクトにレンダラーがありません。", "This object has no renderer.");
                return false;
            }
            GP02ShapeKeyData data = component.Data;
            if (entry == null || entry.Frames.Count == 0)
            {
                message = L10n.Tr("読み込む差分がありません。", "There are no deltas to load.");
                return false;
            }
            if (!ShapeKeyLibrary.IsEntryCompatible(entry, data.VertexCount))
            {
                message = L10n.Tr("このシェイプキーの頂点数がデータと合いません。", "The vertex count of this shape key does not match the data.");
                return false;
            }
            Mesh mesh = ClothEditController.GetMesh(renderer);
            if (mesh == null || mesh.vertexCount != data.VertexCount)
            {
                message = L10n.Tr(
                    "レンダラーのメッシュの頂点数がデータと違います。「元のメッシュに戻す」を試してください。",
                    "The renderer's mesh vertex count differs from the data. Try \"Restore the original mesh\".");
                return false;
            }

            // プレビューの一時メッシュのまま編集を始めると、その形が基準になってしまう
            ShapeKeyPreview.End(renderer);

            bool sameTarget = controller.IsEditing && controller.Target == renderer;
            if (!sameTarget && !controller.TryStart(renderer, out string error))
            {
                message = error;
                return false;
            }

            ShapeKeyFrame last = LastFrame(entry);
            if (!controller.LoadDeltasAsOperation(last.DeltaVertices, entry.PinnedVertices))
            {
                message = L10n.Tr("差分を読み込めませんでした（頂点数が合いません）。", "Could not load the deltas (the vertex counts do not match).");
                return false;
            }

            ActiveComponent = component;
            ActiveEntryName = entry.Name;
            message = L10n.Tr(
                $"「{entry.Name}」を布の編集に読み込みました。つまんで直したら、保存してください。",
                $"Loaded \"{entry.Name}\" into the cloth edit. Edit it, then save.");
            return true;
        }

        /// <summary>重みが一番大きいフレーム（エントリの最終形）。</summary>
        public static ShapeKeyFrame LastFrame(ShapeKeyEntry entry)
        {
            ShapeKeyFrame last = entry.Frames[0];
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                if (frame.Weight >= last.Weight) last = frame;
            }
            return last;
        }

        /// <summary>
        /// 今の編集を、読み込んだエントリに上書き保存する（overwrite が true）か、別の名前で追加する。
        /// newName が空なら読み込んだエントリの名前を使う。保存できなければ null。
        /// </summary>
        public static ShapeKeyEntry SaveActive(ClothEditController controller, bool overwrite, string newName = null)
        {
            if (controller == null || ActiveComponent == null || ActiveComponent.Data == null) return null;
            string name = string.IsNullOrWhiteSpace(newName) ? ActiveEntryName : newName;
            return controller.SaveToData(ActiveComponent.Data, name, overwrite);
        }

        /// <summary>読み込み中の記録を消す。</summary>
        public static void Clear()
        {
            ActiveComponent = null;
            ActiveEntryName = "";
        }
    }
}
