using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// スピュワー(ゲロ吐き。プレイヤーの顔に取りついて吐かせる)。クラス名は EnemySlowMouth。
    /// - 吐く間隔: 取りついている間、次に吐かせるまでの時間(本来 1〜15 秒)に倍率
    /// - 次に取りつくまでの待ち: 離れたあとの待ち(本来 30〜120 秒)に倍率
    /// - 体力: 出現したときの体力に倍率(HealthBoost)
    /// どれもホストが決めて、状態の切り替えは本来の RPC で全員に伝わる。
    /// </summary>
    internal static class Spewer
    {
        private const string Section = "24. スピュワー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> PukeInterval;
        internal static ConfigEntry<float> PossessCooldown;
        internal static ConfigEntry<float> HealthMultiplier;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "スピュワーの変更を使う");
            PukeInterval = Util.Float(cfg, Section, "吐く間隔の倍率", 0.5f, 0.1f, 3f,
                "取りついている間、次に吐かせるまでの時間の倍率(ゲーム本来は 1〜15 秒。0.5 で半分)");
            PossessCooldown = Util.Float(cfg, Section, "次に取りつくまでの待ちの倍率", 1f / 3f, 0.05f, 3f,
                "離れてから次に取りつけるようになるまでの時間の倍率(ゲーム本来は 30〜120 秒。0.33 で 1/3)");
            HealthMultiplier = Util.Float(cfg, Section, "体力の倍率", 3f, 0.5f, 10f,
                "出現したときの体力の倍率(1 でゲーム本来と同じ)。次に出現したときから反映される");
            HealthBoost.Register(typeof(EnemySlowMouth), () => Enabled.Value ? HealthMultiplier.Value : 1f);
        }

        /// <summary>取りついている状態の始まりで、次に吐かせるまでの時間を縮める。</summary>
        [HarmonyPatch(typeof(EnemySlowMouth), nameof(EnemySlowMouth.StateAttached))]
        private static class AttachedPatch
        {
            private static void Prefix(EnemySlowMouth __instance, bool fixedUpdate, out bool __state)
            {
                __state = !fixedUpdate && __instance.stateImpulse;
            }

            private static void Postfix(EnemySlowMouth __instance, bool __state)
            {
                if (__state && Enabled.Value && Util.IsAuthority && __instance.currentState == EnemySlowMouth.State.Attached)
                {
                    __instance.stateTimer = Mathf.Max(0.1f, __instance.stateTimer * PukeInterval.Value);
                }
            }
        }

        /// <summary>離れる状態の始まりで、次に取りつけるまでの待ちを縮める。</summary>
        [HarmonyPatch(typeof(EnemySlowMouth), nameof(EnemySlowMouth.StateDetach))]
        private static class DetachPatch
        {
            private static void Prefix(EnemySlowMouth __instance, bool fixedUpdate, out bool __state)
            {
                __state = !fixedUpdate && __instance.stateImpulse;
            }

            private static void Postfix(EnemySlowMouth __instance, bool __state)
            {
                if (__state && Enabled.Value && Util.IsAuthority)
                {
                    __instance.possessCooldown *= PossessCooldown.Value;
                    Plugin.Verbose($"スピュワー: 次に取りつくまで {__instance.possessCooldown:0} 秒");
                }
            }
        }
    }
}
