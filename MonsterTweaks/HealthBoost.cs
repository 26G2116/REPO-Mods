using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// 敵の出現時の体力(EnemyHealth.health)に、種類ごとの倍率をかける。体力の判定はホストが行うので全員に効く。
    /// 敵ごとのファイルが Register(敵の操作クラス, 倍率を返す関数) で登録する。倍率は次に出現したときから反映される。
    /// </summary>
    internal static class HealthBoost
    {
        private static readonly Dictionary<Type, Func<float>> Multipliers = new Dictionary<Type, Func<float>>();

        internal static void Register(Type enemyType, Func<float> multiplier) => Multipliers[enemyType] = multiplier;

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
                if (!Util.IsAuthority || Multipliers.Count == 0)
                {
                    return;
                }
                // 操作クラスは Enemy と別のオブジェクトにあることがあるので、敵の種類の判定(EnemyCommon)を使う
                var type = EnemyCommon.For(__instance.enemy)?.Type;
                if (type == null || !Multipliers.TryGetValue(type, out var multiplier))
                {
                    return;
                }
                var memo = Healths.GetValue(__instance, h => new HealthMemo { Original = h.health });
                // OnSpawn の中で healthCurrent = health になる
                __instance.health = Mathf.Max(1, Mathf.RoundToInt(memo.Original * multiplier()));
            }
        }
    }
}
