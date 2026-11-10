using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ハートハガー(ハート型の植物。霧を吹きかけて魅了し、引き寄せて噛みつく)。クラス名は EnemyHeartHugger。
    /// - スタン無効: 共通設定(EnemyCommon)。このセクションの「スタン無効」
    /// - 認識範囲: 視界の距離(遠く・近く)に倍率(ホストが判定するので全員に効く)
    /// - 霧の範囲: 霧の当たり判定(ホストだけが動かす見えない玉 EnemyHeartHuggerGasChecker)の速さと大きさに倍率。
    ///   見た目の霧の粒子も、ホストと MOD を入れた人の画面では同じだけ遠くへ飛ばす(MOD なしの人の画面では元の見た目)
    /// </summary>
    internal static class HeartHugger
    {
        private const string Section = "11. ハートハガー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> VisionRange;
        internal static ConfigEntry<float> GasRange;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ハートハガーの変更を使う");
            VisionRange = Util.Float(cfg, Section, "認識範囲の倍率", 2f, 0.5f, 5f, "プレイヤーに気づく距離の倍率(1 でゲーム本来と同じ)");
            GasRange = Util.Float(cfg, Section, "魅惑の霧の範囲の倍率", 2f, 0.5f, 5f,
                "霧が届く距離の倍率(1 でゲーム本来と同じ)。霧の見た目が広がるのはホストと MOD を入れた人の画面だけ");
        }

        private class ParticleMemo
        {
            internal float Speed;
            internal float Applied = -1f;
        }

        private static readonly ConditionalWeakTable<EnemyHeartHugger, ParticleMemo> Particles = new ConditionalWeakTable<EnemyHeartHugger, ParticleMemo>();

        [HarmonyPatch(typeof(EnemyHeartHugger), nameof(EnemyHeartHugger.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyHeartHugger __instance)
            {
                if (!__instance.enemy)
                {
                    return;
                }
                if (Util.IsAuthority)
                {
                    Util.ScaleVision(__instance.enemy.Vision, Enabled.Value ? VisionRange.Value : 1f);
                }
                // 見た目の霧(各自の PC の粒子)も、霧の範囲に合わせて速く飛ばす
                var particles = __instance.gasParticles;
                if (particles)
                {
                    var memo = Particles.GetValue(__instance, h => new ParticleMemo { Speed = h.gasParticles.main.startSpeedMultiplier });
                    float wanted = Enabled.Value ? GasRange.Value : 1f;
                    if (!Mathf.Approximately(memo.Applied, wanted))
                    {
                        memo.Applied = wanted;
                        var main = particles.main;
                        main.startSpeedMultiplier = memo.Speed * wanted;
                    }
                }
            }
        }

        /// <summary>霧の当たり判定の玉: 飛ぶ速さ(= 届く距離)と大きさに倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemyHeartHuggerGasChecker), nameof(EnemyHeartHuggerGasChecker.Start))]
        private static class GasCheckerPatch
        {
            private static void Postfix(EnemyHeartHuggerGasChecker __instance)
            {
                if (!Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                float multiplier = GasRange.Value;
                __instance.velocity *= multiplier;
                __instance.scaleTarget *= Mathf.Sqrt(multiplier);
            }
        }
    }
}
