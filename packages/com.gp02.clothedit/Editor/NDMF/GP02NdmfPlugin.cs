using GP02.EditorTools;
using GP02.EditorTools.NDMF;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GP02NdmfPlugin))]

namespace GP02.EditorTools.NDMF
{
    /// <summary>
    /// NDMF の Plugin。アバターのビルド時に、<see cref="GP02ShapeKeys"/> の「ビルド時に適用する」方式のものを、
    /// 複製したアバターへ適用する（元のシーンのメッシュは変えない）。処理の本体は GP02.Editor の <see cref="NonDestructiveApplier"/>。
    /// Modular Avatar より前に動かすのは、MA の衣装の統合やボーンの付け替えより前の、元のメッシュに対して頂点の並びを合わせるため。
    /// VRChat 以外のプラットフォーム（NDMF の汎用プラットフォーム）でも動くよう、<see cref="RunsOnAllPlatforms"/> を付けている
    /// （既定では VRChat のアバターだけで動く）。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class GP02NdmfPlugin : Plugin<GP02NdmfPlugin>
    {
        public override string QualifiedName => "com.gp02.clothedit";
        public override string DisplayName => "GP02 Shape Keys";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run(ApplyShapeKeysPass.Instance);
        }
    }

    /// <summary>アバターの中の <see cref="GP02ShapeKeys"/> を適用して、取り除く。</summary>
    public sealed class ApplyShapeKeysPass : Pass<ApplyShapeKeysPass>
    {
        public override string QualifiedName => "com.gp02.clothedit.apply-shape-keys";
        public override string DisplayName => "GP02 Apply Shape Keys";

        protected override void Execute(BuildContext context)
        {
            NonDestructiveApplier.Result result = NonDestructiveApplier.Apply(context.AvatarRootObject, asset => context.AssetSaver.SaveAsset(asset));
            foreach (string warning in result.Warnings) Debug.LogWarning("[GP02] " + warning);
        }
    }

    /// <summary>NDMF の連携が読み込まれたことを、NDMF を参照しない GP02.Editor に伝える。</summary>
    [InitializeOnLoad]
    static class GP02NdmfRegistration
    {
        static GP02NdmfRegistration()
        {
            NonDestructiveSupport.IsAvailable = true;
        }
    }
}
