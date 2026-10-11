using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// <see cref="GP02ShapeKeys"/> のインスペクター。シェイプキーの一覧（ウィンドウと同じ <see cref="ShapeKeyListView"/>）を出し、
    /// データやメッシュが見つからない・頂点数が違うときは警告と直し方を出す。
    /// 選択が変わる・インスペクターが閉じる・再読み込みの前に、プレビューは元に戻る。
    /// </summary>
    [CustomEditor(typeof(GP02ShapeKeys))]
    public sealed class GP02ShapeKeysEditor : Editor
    {
        [SerializeField] TreeViewState _treeState;
        ShapeKeyListView _view;

        void OnEnable()
        {
            if (_treeState == null) _treeState = new TreeViewState();
            _view = new ShapeKeyListView(_treeState) { RepaintRequested = Repaint };
            Bind();
        }

        void OnDisable()
        {
            if (_view != null)
            {
                _view.Dispose();
                _view = null;
            }
        }

        void Bind()
        {
            var component = target as GP02ShapeKeys;
            Renderer renderer = component != null ? ClothEditController.FindRenderer(component.gameObject) : null;
            _view.SetTarget(component, renderer);
        }

        public override void OnInspectorGUI()
        {
            if (_view == null) return;
            // 同じインスペクターで対象が切り替わる（ロックしたとき）場合にも追従する
            Bind();

            if (GUILayout.Button(L10n.Tr("ウィンドウで開く", "Open in window"))) EditorApplication.ExecuteMenuItem("Tools/GP02/シェイプキーの一覧");
            _view.OnGUI(240f);
        }
    }
}
