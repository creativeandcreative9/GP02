using System;
using System.Collections.Generic;
using UnityEngine;

namespace GP02.EditorTools
{
    /// <summary>
    /// 動作チェックのポーズの種類。結果の文を分けて出し、直す範囲（<see cref="ClothEditSettings.FixLargePoses"/>）を決める。
    /// </summary>
    public enum PoseCategory
    {
        /// <summary>よく使うポーズ（歩く・腕・お辞儀・反る・ひねる・首・横に倒す）。手持ちの AnimationClip のコマもこちら。</summary>
        Common = 0,

        /// <summary>
        /// 大きく動くポーズ（座る・しゃがむ・足を前に上げる・足を横に開く）。スカートなどでは突き抜けるのが普通
        /// （VRChat では揺れもの（PhysBone）で動かすことが多い）ので、既定では直す範囲に入れない。
        /// </summary>
        Large = 1,
    }

    /// <summary>
    /// 動作チェックで試すポーズ。Humanoid の筋肉の名前（<see cref="HumanTrait.MuscleName"/>）と、その値（-1〜1）で決める。
    /// 書いていない筋肉は、基準（編集中の今の姿勢）のまま。
    /// </summary>
    public sealed class PosePreset
    {
        /// <summary>設定の保存に使う名前（並べ替えても変わらない）。</summary>
        public readonly string Id;

        readonly string _nameJa, _nameEn;

        /// <summary>筋肉の名前と、ポーズでの値（全部のとき）。</summary>
        public readonly (string Muscle, float Value)[] Muscles;

        /// <summary>ポーズの種類（よく使う・大きく動く）。</summary>
        public readonly PoseCategory Category;

        public PosePreset(string id, string nameJa, string nameEn, params (string Muscle, float Value)[] muscles)
            : this(id, PoseCategory.Common, nameJa, nameEn, muscles)
        {
        }

        public PosePreset(string id, PoseCategory category, string nameJa, string nameEn, params (string Muscle, float Value)[] muscles)
        {
            Id = id;
            Category = category;
            _nameJa = nameJa;
            _nameEn = nameEn;
            Muscles = muscles;
        }

        /// <summary>画面の言語に合わせた名前。</summary>
        public string Name => L10n.Tr(_nameJa, _nameEn);

        /// <summary>大きく動くポーズか（<see cref="PoseCategory.Large"/>）。</summary>
        public bool IsLargeMotion => Category == PoseCategory.Large;

        public override string ToString() => Id;
    }

    /// <summary>
    /// 動作チェックのポーズの一覧（15個。大きく動くもの 5 個、よく使うもの 10 個）と、筋肉の値の作り方。
    /// 値は、Unity の筋肉の向き（値が負のほうが名前の前の語。例えば「Upper Leg Front-Back」は負が前、-1 で約 120°）に合わせて、
    /// 関節が自然に曲がる範囲で決めた（座る: 太もも 90°・ひざ 90° が Front-Back -0.667・Stretch -0.125）。
    /// </summary>
    public static class PoseLibrary
    {
        // 左右の筋肉の名前を作る
        static (string, float)[] Both(string part, float value) => new[] { ("Left " + part, value), ("Right " + part, value) };

        static (string, float)[] Join(params (string, float)[][] parts)
        {
            var list = new List<(string, float)>();
            foreach ((string, float)[] part in parts) list.AddRange(part);
            return list.ToArray();
        }

        static (string, float)[] One(string muscle, float value) => new[] { (muscle, value) };

        static readonly PosePreset[] Presets =
        {
            new PosePreset("sit", PoseCategory.Large, "座る", "Sit",
                Join(Both("Upper Leg Front-Back", -0.667f), Both("Lower Leg Stretch", -0.125f), Both("Upper Leg In-Out", 0.1f))),
            new PosePreset("squat", PoseCategory.Large, "しゃがむ", "Squat",
                Join(Both("Upper Leg Front-Back", -1f), Both("Lower Leg Stretch", -0.9f), Both("Upper Leg In-Out", 0.35f), Both("Foot Up-Down", -0.3f),
                    One("Spine Front-Back", -0.5f), One("Chest Front-Back", -0.3f))),
            new PosePreset("leg_forward_l", PoseCategory.Large, "左足を前に上げる", "Raise Left Leg",
                Join(One("Left Upper Leg Front-Back", -0.6f), One("Left Lower Leg Stretch", 0.25f))),
            new PosePreset("leg_forward_r", PoseCategory.Large, "右足を前に上げる", "Raise Right Leg",
                Join(One("Right Upper Leg Front-Back", -0.6f), One("Right Lower Leg Stretch", 0.25f))),
            new PosePreset("legs_apart", PoseCategory.Large, "足を横に開く", "Legs Apart",
                Both("Upper Leg In-Out", 0.75f)),
            new PosePreset("walk", "歩く（前後）", "Walk (Stride)",
                Join(One("Left Upper Leg Front-Back", -0.1f), One("Left Lower Leg Stretch", 0.7f),
                    One("Right Upper Leg Front-Back", 1f), One("Right Lower Leg Stretch", 0.55f))),
            new PosePreset("arms_down", "腕を下ろす", "Arms Down",
                Join(Both("Arm Down-Up", -0.85f), Both("Arm Front-Back", 0.15f), Both("Forearm Stretch", 0.85f))),
            new PosePreset("arms_up", "腕を上げる（バンザイ）", "Arms Up",
                Join(Both("Shoulder Down-Up", 1f), Both("Arm Down-Up", 1f), Both("Arm Front-Back", 0.15f))),
            new PosePreset("arms_forward", "腕を前に", "Arms Forward",
                Join(Both("Arm Down-Up", 0.25f), Both("Arm Front-Back", -0.65f))),
            new PosePreset("arms_bent", "腕を体の前で曲げる", "Arms Bent in Front",
                Join(Both("Arm Down-Up", -0.55f), Both("Arm Front-Back", -0.2f), Both("Forearm Stretch", -0.2f))),
            new PosePreset("bow", "お辞儀", "Bow",
                Join(One("Spine Front-Back", -1f), One("Chest Front-Back", -1f), One("UpperChest Front-Back", -1f))),
            new PosePreset("lean_back", "反る", "Lean Back",
                Join(One("Spine Front-Back", 0.8f), One("Chest Front-Back", 0.8f), One("UpperChest Front-Back", 0.8f))),
            new PosePreset("twist", "体をひねる", "Twist",
                Join(One("Spine Twist Left-Right", 1f), One("Chest Twist Left-Right", 1f), One("UpperChest Twist Left-Right", 1f))),
            new PosePreset("head_turn", "首を回す", "Turn Head",
                Join(One("Neck Turn Left-Right", 1f), One("Head Turn Left-Right", 1f))),
            new PosePreset("side_bend", "体を横に倒す", "Side Bend",
                Join(One("Spine Left-Right", 1f), One("Chest Left-Right", 1f), One("UpperChest Left-Right", 1f))),
        };

        /// <summary>試す段階（半分・全部）。</summary>
        public static readonly float[] Stages = { 0.5f, 1f };

        /// <summary>ポーズの一覧（並びは画面に出す順）。</summary>
        public static IReadOnlyList<PosePreset> All => Presets;

        /// <summary>大きく動くポーズのまとめた呼び名（結果の文に出す）。</summary>
        public static string LargeMotionLabel => L10n.Tr("大きく動くポーズ（座る・しゃがむ・足を上げる・開く）", "Big-motion poses (sit, squat, raise a leg, legs apart)");

        /// <summary>よく使うポーズのまとめた呼び名（結果の文に出す）。</summary>
        public static string CommonLabel => L10n.Tr("よく使うポーズ", "Everyday poses");

        static Dictionary<string, int> _muscleIndex;

        /// <summary>筋肉の名前の番号（<see cref="HumanTrait.MuscleName"/> の番号）。無ければ -1。</summary>
        public static int MuscleIndex(string muscle)
        {
            if (_muscleIndex == null)
            {
                _muscleIndex = new Dictionary<string, int>();
                string[] names = HumanTrait.MuscleName;
                for (int i = 0; i < names.Length; i++) _muscleIndex[names[i]] = i;
            }
            return muscle != null && _muscleIndex.TryGetValue(muscle, out int index) ? index : -1;
        }

        /// <summary>id のポーズ。無ければ null。</summary>
        public static PosePreset Find(string id)
        {
            foreach (PosePreset preset in Presets)
            {
                if (preset.Id == id) return preset;
            }
            return null;
        }

        /// <summary>
        /// 基準の筋肉の値から、ポーズへ stage（0 で基準、1 で全部）だけ寄せた筋肉の値を、新しい配列で返す。書いていない筋肉は基準のまま。
        /// </summary>
        public static float[] Blend(PosePreset pose, float[] baseMuscles, float stage)
        {
            if (pose == null) throw new ArgumentNullException(nameof(pose));
            if (baseMuscles == null) throw new ArgumentNullException(nameof(baseMuscles));
            var muscles = (float[])baseMuscles.Clone();
            foreach ((string muscle, float value) in pose.Muscles)
            {
                int index = MuscleIndex(muscle);
                if (index < 0 || index >= muscles.Length) continue;
                muscles[index] = Mathf.LerpUnclamped(baseMuscles[index], value, stage);
            }
            return muscles;
        }

        /// <summary>一覧のポーズが使う筋肉の名前のうち、Unity に無いもの（テスト用。無ければ空）。</summary>
        public static List<string> FindUnknownMuscles()
        {
            var unknown = new List<string>();
            foreach (PosePreset preset in Presets)
            {
                foreach ((string muscle, float _) in preset.Muscles)
                {
                    if (MuscleIndex(muscle) < 0) unknown.Add(preset.Id + ": " + muscle);
                }
            }
            return unknown;
        }
    }
}
