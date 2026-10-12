using System.Collections.Generic;
using GP02.Core;
using UnityEditor;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>アバター全体などを選んだときに、窓に出す「この中の衣装」の1つ。</summary>
    public sealed class ClothCandidate
    {
        public Renderer Renderer;

        /// <summary>体（素体・顔）のメッシュらしいか（<see cref="BodyMeshGuess"/>）。体らしいものは、一覧の後ろに回して印を付ける。</summary>
        public bool LikelyBody;

        /// <summary>シーンで表示されているか（非表示の衣装も候補に入れる）。</summary>
        public bool IsActive => Renderer != null && Renderer.gameObject.activeInHierarchy;
    }

    /// <summary>
    /// ヒエラルキーの右クリック「GP02 で布を編集」。選んだ衣装で、すぐ編集を始める（複数選んでいれば、重ね着としてまとめて）。
    /// 編集していないときにヒエラルキーで選んだ衣装を自動で対象にする処理も、同じ選び方（<see cref="FindCloth"/>）を使う。
    /// アバター全体のように、衣装そのものではないオブジェクトを選んだときは、その中の衣装の一覧（<see cref="FindCandidates"/>）を窓に出す。
    /// </summary>
    public static class ClothEditStartMenu
    {
        public const string MenuPath = "GameObject/GP02 で布を編集";

        /// <summary>一覧に出す衣装の数の上限（多すぎると窓が長くなるので）。</summary>
        public const int MaxCandidates = 40;

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

        /// <summary>
        /// 選んだオブジェクトそのものには編集できる布が無いとき、その中（子孫）にある、編集できる布（SkinnedMeshRenderer、または MeshFilter 付きの MeshRenderer）の候補。
        /// 選んだものに布そのものがあるオブジェクトは、対象になるので見ない。表示されていない衣装も含める（体らしいものは後ろに回す。<see cref="ClothCandidate.LikelyBody"/>）。
        /// 同じものは1つにする。上限は <see cref="MaxCandidates"/>。プロジェクトのアセット（プレハブそのもの）は含めない。
        /// </summary>
        public static List<ClothCandidate> FindCandidates(IEnumerable<GameObject> selection)
        {
            var found = new List<ClothCandidate>();
            if (selection == null) return found;
            foreach (GameObject go in selection)
            {
                if (go == null || !go.scene.IsValid() || ClothEditController.FindRenderer(go) != null) continue;
                AddCandidatesIn(go, found, null);
            }
            return SortCandidates(found);
        }

        /// <summary>
        /// アバター（root の下）にある衣装の候補。体らしいもの・except・編集できないものは除く。体のメッシュを選んでしまったときに、
        /// 「こちらでは？」と出す一覧に使う。
        /// </summary>
        public static List<Renderer> FindOutfitsIn(GameObject root, Renderer except)
        {
            var found = new List<ClothCandidate>();
            var outfits = new List<Renderer>();
            if (root == null) return outfits;
            AddCandidatesIn(root, found, except);
            foreach (ClothCandidate candidate in found)
            {
                if (!candidate.LikelyBody) outfits.Add(candidate.Renderer);
            }
            return outfits;
        }

        static void AddCandidatesIn(GameObject root, List<ClothCandidate> found, Renderer except)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == except || ClothEditController.FindRenderer(renderer.gameObject) != renderer) continue;
                if (ClothEditController.GetStartProblem(renderer) != null) continue;
                bool known = false;
                foreach (ClothCandidate candidate in found) known |= candidate.Renderer == renderer;
                if (known) continue;
                found.Add(new ClothCandidate { Renderer = renderer, LikelyBody = IsLikelyBody(renderer) });
            }
        }

        // 体らしくないものを先に（ヒエラルキーの順のまま）、体らしいものを後ろに。上限で切る
        static List<ClothCandidate> SortCandidates(List<ClothCandidate> found)
        {
            var sorted = new List<ClothCandidate>(found.Count);
            foreach (ClothCandidate candidate in found)
            {
                if (!candidate.LikelyBody) sorted.Add(candidate);
            }
            foreach (ClothCandidate candidate in found)
            {
                if (candidate.LikelyBody) sorted.Add(candidate);
            }
            if (sorted.Count > MaxCandidates) sorted.RemoveRange(MaxCandidates, sorted.Count - MaxCandidates);
            return sorted;
        }

        /// <summary>レンダラーのメッシュが、体（素体・顔）らしいか。名前と、メッシュの表情のシェイプキーで決める（<see cref="BodyMeshGuess"/>）。</summary>
        public static bool IsLikelyBody(Renderer renderer, out string reason)
        {
            reason = "";
            if (renderer == null) return false;
            return BodyMeshGuess.IsLikelyBody(renderer.name, BlendShapeNames(ClothEditController.GetMesh(renderer)), out reason);
        }

        public static bool IsLikelyBody(Renderer renderer) => IsLikelyBody(renderer, out _);

        /// <summary>メッシュのシェイプキーの名前（メッシュが無ければ空）。</summary>
        public static List<string> BlendShapeNames(Mesh mesh)
        {
            var names = new List<string>();
            if (mesh == null) return names;
            for (int i = 0; i < mesh.blendShapeCount; i++) names.Add(mesh.GetBlendShapeName(i));
            return names;
        }

        /// <summary>選んだものに、編集できる布が1つでもあるか。アバター全体のように、中に布があるものも、右クリックのメニューで窓に一覧を出せる。</summary>
        public static bool CanStart(IEnumerable<GameObject> selection)
        {
            return FindCloth(selection).Count > 0 || FindCandidates(selection).Count > 0;
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
