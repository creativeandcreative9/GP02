using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// ヒエラルキーの右クリック「GP02 で布を編集」。選んだ衣装で、すぐ編集を始める（複数選んでいれば、重ね着としてまとめて）。
    /// 編集していないときにヒエラルキーで選んだ衣装を自動で対象にする処理も、同じ選び方（<see cref="FindCloth"/>）を使う。
    /// </summary>
    public static class ClothEditStartMenu
    {
        public const string MenuPath = "GameObject/GP02 で布を編集";

        /// <summary>
        /// 選んだオブジェクトのうち、編集できる布（SkinnedMeshRenderer、または MeshFilter 付きの MeshRenderer）を、選んだ順に集める。
        /// プロジェクトのアセット（プレハブそのもの）は含めない（シーンのオブジェクトだけ）。同じものは1つにする。
        /// </summary>
        public static List<Renderer> FindCloth(IEnumerable<GameObject> selection)
        {
            var result = new List<Renderer>();
            if (selection == null) return result;
            foreach (GameObject go in selection)
            {
                if (go == null || !go.scene.IsValid()) continue;
                Renderer renderer = ClothEditController.FindRenderer(go);
                if (renderer != null && !result.Contains(renderer)) result.Add(renderer);
            }
            return result;
        }

        /// <summary>選んだものに、編集できる布が1つでもあるか（右クリックのメニューを使えるか）。</summary>
        public static bool CanStart(IEnumerable<GameObject> selection)
        {
            return FindCloth(selection).Count > 0;
        }

        [MenuItem(MenuPath, true)]
        static bool Validate()
        {
            return CanStart(Selection.gameObjects);
        }

        [MenuItem(MenuPath, false, 12)]
        static void Run()
        {
            ClothEditWindow.StartFromSelection(Selection.gameObjects);
        }
    }
}
