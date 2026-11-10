using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// ノーム(おもちゃの小人。群れで出てきて物を壊す)。クラス名は EnemyGnome。
    /// - 耐久: 出現したときの体力(EnemyHealth.health)に倍率をかける(体力の判定はホストが行うので全員に効く)
    /// </summary>
    internal static class Gnome
    {
        private const string Section = "18. ノーム";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> HealthMultiplier;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ノームの変更を使う");
            HealthMultiplier = Util.Float(cfg, Section, "耐久の倍率", 3f, 0.5f, 10f,
                "出現したときの体力の倍率(1 でゲーム本来と同じ)。次に出現したときから反映される");
        }

        private class HealthMemo
        {
            internal int Original;
        }

        private static readonly ConditionalWeakTable<EnemyHealth, HealthMemo> Healths = new ConditionalWeakTable<EnemyHealth, HealthMemo>();

        [HarmonyPatch(typeof(EnemyHealth), nameof(EnemyHealth.OnSpawn))]
        private static class HealthSpawnPatch
        {
            private static void Prefix(EnemyHealth __instance)
            {
                // EnemyGnome は Enemy と別のオブジェクトにあることがあるので、敵の種類の判定(EnemyCommon)を使う
                if (!Util.IsAuthority || EnemyCommon.For(__instance.enemy)?.Type != typeof(EnemyGnome))
                {
                    return;
                }
                var memo = Healths.GetValue(__instance, h => new HealthMemo { Original = h.health });
                // OnSpawn の中で healthCurrent = health になる
                __instance.health = Enabled.Value
                    ? UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(memo.Original * HealthMultiplier.Value))
                    : memo.Original;
            }
        }
    }
}
