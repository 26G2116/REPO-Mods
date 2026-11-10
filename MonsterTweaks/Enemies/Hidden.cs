using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace MonsterTweaks
{
    /// <summary>
    /// ヒドゥン(透明人間。プレイヤーをつかんで遠くへ運ぶ)。クラス名は EnemyHidden。
    /// - 運ぶ先: 本来は「プレイヤーから 50m 以上離れた場所」。これを、出現しているほかのモンスターの近くに変える
    /// - 電池: つかんでいるプレイヤーの持ち物(インベントリの道具と手に持っている物)の電池をすぐ空にし、
    ///   空にした電池の量(100% = 電池 1 本ぶん)の割合だけヒドゥンの体力を回復する
    /// どちらもホストが行い、電池の残量・体力・移動はゲーム本来の同期で全員に伝わる。
    /// </summary>
    internal static class Hidden
    {
        private const string Section = "20. ヒドゥン";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> CarryToMonsters;
        internal static ConfigEntry<float> CarryTime;
        internal static ConfigEntry<bool> DrainBattery;
        internal static ConfigEntry<float> HealMultiplier;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ヒドゥンの変更を使う");
            CarryToMonsters = cfg.Bind(Section, "ほかのモンスターの所へ運ぶ", true,
                "つかんだプレイヤーを、一番近くにいるほかのモンスターの所へ運ぶ(いなければ本来どおり遠くへ)");
            CarryTime = Util.Float(cfg, Section, "運ぶ時間の上限", 15f, 3f, 40f,
                "プレイヤーを運び続ける最長の秒数(ゲーム本来は 10)。モンスターの所まで届くように少し長め");
            DrainBattery = cfg.Bind(Section, "つかんだ人の電池を吸い取る", true,
                "つかんでいるプレイヤーの持ち物の電池をすぐ空にして、その分ヒドゥンの体力を回復する");
            HealMultiplier = Util.Float(cfg, Section, "回復の倍率", 1f, 0f, 5f,
                "吸い取った電池 1 本ぶん(100%)で最大体力の何倍を回復するか(1 なら電池半分で体力の半分)");
        }

        private static bool Carrying(EnemyHidden hidden) =>
            hidden.currentState == EnemyHidden.State.PlayerPickup || hidden.currentState == EnemyHidden.State.PlayerMove;

        // ---------------- 運ぶ先 ----------------

        [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.StatePlayerMove))]
        private static class PlayerMovePatch
        {
            /// <summary>
            /// 運び始めの最初の更新で、本来の行き先探しの代わりに行き先を決める
            /// (本来の探し方が失敗すると、同じ更新の中ですぐプレイヤーを放してしまうため、先に決める)。
            /// </summary>
            private static void Prefix(EnemyHidden __instance, out bool __state)
            {
                __state = __instance.stateImpulse;
                if (!__state || !Enabled.Value || !CarryToMonsters.Value || !Util.IsAuthority)
                {
                    return;
                }
                var monster = NearestOtherMonster(__instance);
                if (!monster || !NavMesh.SamplePosition(monster.transform.position, out var hit, 4f, -1))
                {
                    return;
                }
                // 本来の最初の更新と同じ準備をして、本来の行き先探しは飛ばす
                __instance.stateTimer = 5f;
                __instance.maxMoveTimer = CarryTime.Value;
                __instance.agentDestination = hit.position;
                __instance.stateImpulse = false;
                __instance.enemy.NavMeshAgent.SetDestination(hit.position);
                Plugin.Verbose($"ヒドゥン: つかんだプレイヤーを {monster.EnemyParent?.enemyName ?? monster.name} の所へ運びます");
            }

            private static void Postfix(EnemyHidden __instance, bool __state)
            {
                // 本来の行き先のときも、運ぶ時間の上限は設定に合わせる
                if (__state && Enabled.Value && Util.IsAuthority && __instance.currentState == EnemyHidden.State.PlayerMove)
                {
                    __instance.maxMoveTimer = CarryTime.Value;
                }
            }
        }

        private static Enemy NearestOtherMonster(EnemyHidden hidden)
        {
            Enemy best = null;
            float bestDistance = float.MaxValue;
            Vector3 from = hidden.transform.position;
            foreach (var parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (!parent || !parent.Spawned || !parent.Enemy || parent.Enemy == hidden.enemy)
                {
                    continue;
                }
                var enemy = parent.Enemy;
                if (enemy.CurrentState == EnemyState.Despawn || (enemy.HasHealth && enemy.Health.dead))
                {
                    continue;
                }
                float distance = Vector3.Distance(from, enemy.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = enemy;
                }
            }
            return best;
        }

        // ---------------- 電池を吸い取る ----------------

        private class DrainMemo
        {
            internal float Timer;
        }

        private static readonly ConditionalWeakTable<EnemyHidden, DrainMemo> Drains = new ConditionalWeakTable<EnemyHidden, DrainMemo>();

        [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.Update))]
        private static class UpdatePatch
        {
            private static void Postfix(EnemyHidden __instance)
            {
                if (!Enabled.Value || !DrainBattery.Value || !Util.IsAuthority || !Carrying(__instance))
                {
                    return;
                }
                var player = __instance.playerTarget;
                if (!player || player.isDisabled)
                {
                    return;
                }
                // つかんだらすぐ。途中で持ち替えた道具も吸うように、運んでいる間は 0.5 秒ごとに確かめる
                var memo = Drains.GetOrCreateValue(__instance);
                memo.Timer -= Time.deltaTime;
                if (memo.Timer > 0f)
                {
                    return;
                }
                memo.Timer = 0.5f;
                float drained = DrainPlayer(player);
                if (drained <= 0f)
                {
                    return;
                }
                var health = __instance.enemy ? __instance.enemy.Health : null;
                int heal = health ? Mathf.RoundToInt(health.health * drained / 100f * HealMultiplier.Value) : 0;
                if (heal > 0 && !health.dead)
                {
                    health.Heal(heal);
                }
                Plugin.Verbose($"ヒドゥン: {player.playerName} の電池を {drained:0}% 吸い取り、体力を {heal} 回復しました");
            }
        }

        /// <summary>プレイヤーの持ち物の電池を空にする。空にした量の合計(%。電池 1 本 = 100)を返す。</summary>
        private static float DrainPlayer(PlayerAvatar player)
        {
            var batteries = new HashSet<ItemBattery>();
            foreach (var item in Object.FindObjectsByType<ItemEquippable>(FindObjectsSortMode.None))
            {
                if (item && item.IsEquipped() && item.GetOwnerPlayerAvatar() == player)
                {
                    var battery = item.GetComponent<ItemBattery>();
                    if (battery) batteries.Add(battery);
                }
            }
            var held = player.physGrabber ? player.physGrabber.grabbedPhysGrabObject : null;
            if (held)
            {
                var battery = held.GetComponent<ItemBattery>();
                if (battery) batteries.Add(battery);
            }
            float total = 0f;
            foreach (var battery in batteries)
            {
                if (battery.itemAttributes && battery.itemAttributes.shopItem)
                {
                    continue;
                }
                float life = Mathf.Clamp(battery.batteryLife, 0f, 100f);
                if (life <= 0f)
                {
                    continue;
                }
                total += life;
                battery.SetBatteryLife(0); // ホストの値を変えて、本来の RPC で全員に伝える
            }
            return total;
        }
    }
}
