using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 編集の状態（粒子の位置とピン）を持つだけの入れ物。Unity の Undo は ScriptableObject を記録できるので、
    /// これを <c>Undo.RegisterCompleteObjectUndo</c> に渡して、Ctrl+Z / Ctrl+Y と連動させる。
    /// 位置は float の配列（x, y, z, x, y, z, …）にしている。Vector3 の配列より、記録と復元が速いため。
    /// </summary>
    public sealed class ClothEditState : ScriptableObject
    {
        /// <summary>粒子の位置（ワールド、メートル）。粒子 i は [3i], [3i+1], [3i+2]。</summary>
        public float[] Positions = new float[0];

        /// <summary>粒子ごとのピン留め。</summary>
        public bool[] Pinned = new bool[0];

        public static ClothEditState Create()
        {
            var state = CreateInstance<ClothEditState>();
            // シーンやアセットに保存されず、ドメインの再読み込みやシーンの切り替えでも消されない
            state.hideFlags = HideFlags.HideAndDontSave;
            return state;
        }
    }
}
