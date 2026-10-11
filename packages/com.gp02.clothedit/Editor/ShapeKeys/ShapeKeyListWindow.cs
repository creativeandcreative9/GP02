using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 「Tools/GP02/シェイプキーの一覧」のウィンドウ。選んでいるオブジェクトのシェイプキーを一覧で扱う。
    /// 中身の画面は <see cref="ShapeKeyListView"/>（<see cref="GP02ShapeKeys"/> のインスペクターと同じもの）。
    /// 選択が変わると、前の対象のプレビューは元に戻る。
    /// </summary>
    public sealed class ShapeKeyListWindow : EditorWindow
    {
        [SerializeField] TreeViewState _treeState;
        ShapeKeyListView _view;
        internal ShapeKeyListView View => _view;
        internal int GuiCalls;
        Vector2 _scroll;

        [MenuItem("Tools/GP02/シェイプキーの一覧")]
        static void Open()
        {
            var window = GetWindow<ShapeKeyListWindow>(L10n.Tr("GP02 シェイプキー", "GP02 Shape Keys"));
            window.minSize = new Vector2(300f, 360f);
        }

        void OnEnable()
        {
            titleContent = new GUIContent(L10n.Tr("GP02 シェイプキー", "GP02 Shape Keys"));
            if (_treeState == null) _treeState = new TreeViewState();
            _view = new ShapeKeyListView(_treeState) { RepaintRequested = Repaint };
            Selection.selectionChanged += OnSelectionChanged;
            OnSelectionChanged();
        }

        void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            if (_view != null)
            {
                // ウィンドウを閉じる・再読み込みの前に、プレビューを必ず元に戻す
                _view.Dispose();
                _view = null;
            }
        }

        internal void OnSelectionChanged()
        {
            if (_view == null) return;
            GameObject selected = Selection.activeGameObject;
            Renderer renderer = ClothEditController.FindRenderer(selected);
            GP02ShapeKeys component = selected != null ? selected.GetComponent<GP02ShapeKeys>() : null;
            _view.SetTarget(component, renderer);
            Repaint();
        }

        void OnGUI()
        {
            GuiCalls++;
            if (_view == null) return;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            // ウィンドウの高さに合わせて、一覧を広げる（最低 200）
            float treeHeight = Mathf.Max(200f, position.height * 0.45f);
            _view.OnGUI(treeHeight);
            EditorGUILayout.EndScrollView();
        }
    }
}
