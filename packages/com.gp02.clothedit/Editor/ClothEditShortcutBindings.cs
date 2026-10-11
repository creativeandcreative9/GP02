using System;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// GP02 のショートカットを Unity の Shortcut Manager に登録する。利用者は Edit > Shortcuts の「GP02」でキーを変えられる。
    ///
    /// 布を編集している間だけ有効にし、Unity 標準の W/E/R/Q/F などより優先させるため、シーンビューのコンテキストに
    /// タグ（<see cref="Tag"/>）付きで登録し、編集中だけタグを有効にする（<see cref="SetEditing"/>）。
    /// Shortcut Manager は、同じキーの候補のうち有効なタグの付いたものだけを残し、ワールド全体（Global）のものより
    /// ウィンドウのコンテキストのものを先に選ぶので、編集中はこちらが効く。
    /// Unity 2022.3 では、独自の道具のコンテキスト（IShortcutToolContext と ShortcutManager.RegisterContext）は公開されていないため、この方法にした。
    /// </summary>
    public static class ClothEditShortcutBindings
    {
        /// <summary>編集中だけ有効にするタグ。</summary>
        public const string Tag = "GP02.ClothEditing";

        public const string IdGrab = "GP02/Grab";
        public const string IdRotate = "GP02/Rotate";
        public const string IdScale = "GP02/Scale";
        public const string IdPin = "GP02/Pin";
        public const string IdUnpin = "GP02/Unpin";
        public const string IdRelax = "GP02/Restore Brush";
        public const string IdSelectTool = "GP02/Select and Move";
        public const string IdHoldOriginal = "GP02/Show Original Shape (hold)";
        public const string IdToggleHelp = "GP02/Shortcut List";
        public const string IdBrushRadiusDown = "GP02/Brush Radius Smaller";
        public const string IdBrushRadiusUp = "GP02/Brush Radius Larger";
        public const string IdInfluenceDown = "GP02/Influence Smaller";
        public const string IdInfluenceUp = "GP02/Influence Larger";
        public const string IdRadiusDrag = "GP02/Change Brush Radius (drag)";
        public const string IdInfluenceDrag = "GP02/Change Influence (drag)";

        /// <summary>登録するショートカットの一覧（既定のキー）。</summary>
        public readonly struct Definition
        {
            public Definition(string id, ClothEditShortcutAction action, KeyCode key, ShortcutModifiers modifiers = ShortcutModifiers.None)
            {
                Id = id;
                Action = action;
                Key = key;
                Modifiers = modifiers;
            }

            public string Id { get; }
            public ClothEditShortcutAction Action { get; }
            public KeyCode Key { get; }
            public ShortcutModifiers Modifiers { get; }
        }

        /// <summary>すべてのショートカットと既定のキー（下の属性と同じ。テストで一致を確かめている）。</summary>
        public static readonly Definition[] Definitions =
        {
            new Definition(IdGrab, ClothEditShortcutAction.Grab, KeyCode.W),
            new Definition(IdRotate, ClothEditShortcutAction.Rotate, KeyCode.E),
            new Definition(IdScale, ClothEditShortcutAction.Scale, KeyCode.R),
            new Definition(IdPin, ClothEditShortcutAction.Pin, KeyCode.P),
            new Definition(IdUnpin, ClothEditShortcutAction.Unpin, KeyCode.P, ShortcutModifiers.Shift),
            new Definition(IdRelax, ClothEditShortcutAction.Relax, KeyCode.T),
            new Definition(IdSelectTool, ClothEditShortcutAction.SelectTool, KeyCode.Q),
            new Definition(IdHoldOriginal, ClothEditShortcutAction.HoldOriginal, KeyCode.C),
            new Definition(IdToggleHelp, ClothEditShortcutAction.ToggleHelp, KeyCode.F1),
            new Definition(IdBrushRadiusDown, ClothEditShortcutAction.BrushRadiusDown, KeyCode.LeftBracket),
            new Definition(IdBrushRadiusUp, ClothEditShortcutAction.BrushRadiusUp, KeyCode.RightBracket),
            new Definition(IdInfluenceDown, ClothEditShortcutAction.InfluenceDown, KeyCode.LeftBracket, ShortcutModifiers.Shift),
            new Definition(IdInfluenceUp, ClothEditShortcutAction.InfluenceUp, KeyCode.RightBracket, ShortcutModifiers.Shift),
            new Definition(IdRadiusDrag, ClothEditShortcutAction.RadiusDrag, KeyCode.F),
            new Definition(IdInfluenceDrag, ClothEditShortcutAction.InfluenceDrag, KeyCode.F, ShortcutModifiers.Shift),
        };

        static bool _tagRegistered;
        static bool _failed;

        /// <summary>
        /// 動作を受け取る先（編集ウィンドウが登録する）。null なら何もしない。2つ目の引数は、押した（true）か離した（false）か
        /// （押している間だけの動作 <see cref="ClothEditShortcutAction.HoldOriginal"/> のため。ほかは押したときだけ呼ぶ）。
        /// </summary>
        public static Action<ClothEditShortcutAction, bool> Handler { get; set; }

        /// <summary>Shortcut Manager にタグを登録できて、ショートカットが効いているか。false なら、ウィンドウがキーを直接読む。</summary>
        public static bool IsActive => _tagRegistered && !_failed;

        /// <summary>編集中か（タグを有効にするか）を伝える。変わったときだけタグを登録・解除する。</summary>
        public static void SetEditing(bool editing)
        {
            if (editing == _tagRegistered || _failed) return;
            try
            {
                if (editing) ShortcutManager.RegisterTag(Tag);
                else ShortcutManager.UnregisterTag(Tag);
                _tagRegistered = editing;
            }
            catch (Exception ex)
            {
                // Shortcut Manager が使えない環境では、ウィンドウがキーを直接読む（今までの判定）
                _failed = true;
                _tagRegistered = false;
                Debug.LogWarning("[GP02] ショートカットを Shortcut Manager に登録できませんでした。キーを直接読みます: " + ex.Message);
            }
        }

        /// <summary>
        /// 動作に今割り当てられているキーの表示（利用者が変えていなければ既定のキー）。割り当てが無ければ空。
        /// 既定のままなら、案内の文字が環境によって変わらないよう defaultLabel を返す。
        /// </summary>
        public static string GetKeyLabel(string id, string defaultLabel)
        {
            try
            {
                IShortcutManager manager = ShortcutManager.instance;
                if (manager == null || !manager.IsShortcutOverridden(id)) return defaultLabel;
                ShortcutBinding binding = manager.GetShortcutBinding(id);
                return binding.Equals(ShortcutBinding.empty) ? "" : binding.ToString();
            }
            catch (Exception)
            {
                return defaultLabel;
            }
        }

        static void Run(ClothEditShortcutAction action) => Handler?.Invoke(action, true);

        // ---- 登録（既定のキーは Definitions と同じにすること） ----

        [Shortcut(IdGrab, typeof(SceneView), Tag, KeyCode.W)]
        static void Grab() => Run(ClothEditShortcutAction.Grab);

        [Shortcut(IdRotate, typeof(SceneView), Tag, KeyCode.E)]
        static void Rotate() => Run(ClothEditShortcutAction.Rotate);

        [Shortcut(IdScale, typeof(SceneView), Tag, KeyCode.R)]
        static void Scale() => Run(ClothEditShortcutAction.Scale);

        [Shortcut(IdPin, typeof(SceneView), Tag, KeyCode.P)]
        static void Pin() => Run(ClothEditShortcutAction.Pin);

        [Shortcut(IdUnpin, typeof(SceneView), Tag, KeyCode.P, ShortcutModifiers.Shift)]
        static void Unpin() => Run(ClothEditShortcutAction.Unpin);

        [Shortcut(IdRelax, typeof(SceneView), Tag, KeyCode.T)]
        static void Relax() => Run(ClothEditShortcutAction.Relax);

        [Shortcut(IdSelectTool, typeof(SceneView), Tag, KeyCode.Q)]
        static void SelectTool() => Run(ClothEditShortcutAction.SelectTool);

        [ClutchShortcut(IdHoldOriginal, typeof(SceneView), Tag, KeyCode.C)]
        static void HoldOriginal(ShortcutArguments args) => Handler?.Invoke(ClothEditShortcutAction.HoldOriginal, args.stage == ShortcutStage.Begin);

        [Shortcut(IdToggleHelp, typeof(SceneView), Tag, KeyCode.F1)]
        static void ToggleHelp() => Run(ClothEditShortcutAction.ToggleHelp);

        [Shortcut(IdBrushRadiusDown, typeof(SceneView), Tag, KeyCode.LeftBracket)]
        static void BrushRadiusDown() => Run(ClothEditShortcutAction.BrushRadiusDown);

        [Shortcut(IdBrushRadiusUp, typeof(SceneView), Tag, KeyCode.RightBracket)]
        static void BrushRadiusUp() => Run(ClothEditShortcutAction.BrushRadiusUp);

        [Shortcut(IdInfluenceDown, typeof(SceneView), Tag, KeyCode.LeftBracket, ShortcutModifiers.Shift)]
        static void InfluenceDown() => Run(ClothEditShortcutAction.InfluenceDown);

        [Shortcut(IdInfluenceUp, typeof(SceneView), Tag, KeyCode.RightBracket, ShortcutModifiers.Shift)]
        static void InfluenceUp() => Run(ClothEditShortcutAction.InfluenceUp);

        [Shortcut(IdRadiusDrag, typeof(SceneView), Tag, KeyCode.F)]
        static void RadiusDrag() => Run(ClothEditShortcutAction.RadiusDrag);

        [Shortcut(IdInfluenceDrag, typeof(SceneView), Tag, KeyCode.F, ShortcutModifiers.Shift)]
        static void InfluenceDrag() => Run(ClothEditShortcutAction.InfluenceDrag);
    }
}
