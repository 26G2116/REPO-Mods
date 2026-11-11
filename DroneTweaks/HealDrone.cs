using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// ヒーラードローン(削除されたアイテム)の変更。
    ///
    /// 復活:
    /// - 起動中のヒーラードローンは、近くに倒れた人の頭があれば、生きている人より優先してビームでくっつく
    ///   (ドローンが狙いを選ぶ処理・くっつく処理はホストが行い、ゲーム本来の通信で全員の画面に出る)
    /// - くっついている間、電池を消費しながら復活ゲージがたまり、必要な量に達したら復活させる
    ///   (復活はデフィブロと同じ PlayerAvatar.Revive + HealOther で、ホストから全員に反映される)
    /// - 回復中の人が倒れたら、ドローンを止めずにそのまま頭を探す
    ///
    /// 回復の電池効率:
    /// - 元の回復(2 秒ごとに 10)は、回復される人の PC で計算されるので、そのまま使う(MOD なしの人にも効く)
    /// - 元は「ビームを出している間ずっと電池が減る」。これを止めて、回復した分だけホストが電池を減らす
    ///   (電池満タンで回復できる体力 = 設定の値)
    /// </summary>
    internal static class HealDrone
    {
        private const string Section = "2. ヒーラードローン";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<int> HealPerBattery;
        private static ConfigEntry<bool> Revive;
        private static ConfigEntry<float> ReviveCost;
        private static ConfigEntry<float> ReviveTime;
        private static ConfigEntry<int> ReviveHealth;
        private static ConfigEntry<float> HeadRange;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ヒーラードローンの変更(復活・回復の電池効率)を使う");
            HealPerBattery = cfg.Bind(Section, "電池満タンで回復できる体力", 250,
                new ConfigDescription("回復した分だけ電池が減る。電池 100% でこの量まで回復できる", new AcceptableValueRange<int>(10, 3000)));
            Revive = cfg.Bind(Section, "倒れた人を復活させる", true, "近くに倒れた人の頭があれば、そちらを優先してくっつき、復活させる");
            ReviveCost = cfg.Bind(Section, "復活に必要な電池(%)", 20f,
                new ConfigDescription("頭にくっついている間にこの量の電池を使い切ると復活する", new AcceptableValueRange<float>(1f, 100f)));
            ReviveTime = cfg.Bind(Section, "復活にかかる時間(秒)", 4f,
                new ConfigDescription("頭にくっついてから復活するまでの時間(電池が足りていれば)", new AcceptableValueRange<float>(0.5f, 30f)));
            ReviveHealth = cfg.Bind(Section, "復活後の体力", 50,
                new ConfigDescription("復活した直後の体力(デフィブロは 50)", new AcceptableValueRange<int>(1, 200)));
            HeadRange = cfg.Bind(Section, "頭を探す範囲(m)", 1.5f,
                new ConfigDescription("ドローンからこの距離以内にある倒れた人の頭を狙う(生きている人を狙う範囲は 1m)", new AcceptableValueRange<float>(0.5f, 5f)));
        }

        private class Memo
        {
            internal ItemDroneHeal Heal;
            internal PlayerDeathHead Head;
            internal float Progress;
            internal PlayerAvatar HealTarget;
            internal int LastHealth;
        }

        private static readonly ConditionalWeakTable<ItemDrone, Memo> Memos = new ConditionalWeakTable<ItemDrone, Memo>();

        private static Memo GetMemo(ItemDrone drone)
        {
            return Memos.GetValue(drone, d => new Memo { Heal = d.GetComponent<ItemDroneHeal>() });
        }

        private static bool ReviveActive => Enabled.Value && Revive.Value && !SemiFunc.RunIsShop() && !SemiFunc.RunIsArena();

        // ---- 倒れた人の頭を優先して狙う ----

        [HarmonyPatch(typeof(ItemDrone), nameof(ItemDrone.TargetFindPlayer))]
        private static class TargetPatch
        {
            private static bool Prefix(ItemDrone __instance, ref bool __result)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer() || !ReviveActive || !GetMemo(__instance).Heal
                    || !__instance.itemBattery || __instance.itemBattery.batteryLife <= 0f)
                {
                    return true;
                }

                var head = FindHead(__instance.transform.position, HeadRange.Value);
                if (!head)
                {
                    return true;
                }
                var target = head.physGrabObject;
                var collider = target.GetComponentInChildren<PhysGrabObjectCollider>();
                if (!collider || (SemiFunc.IsMultiplayer() && !target.photonView))
                {
                    return true;
                }

                // SphereCheck(物を狙う処理)と同じ手順で、頭を「物」としてくっつける
                __instance.playerTumbleTarget = null;
                __instance.playerAvatarTarget = null;
                __instance.targetIsPlayer = false;
                __instance.targetIsEnemy = false;
                __instance.targetIsLocalPlayer = false;
                __instance.enemyTarget = null;
                __instance.magnetTarget = collider.transform;
                __instance.magnetTargetPhysGrabObject = target;
                __instance.magnetTargetRigidbody = target.rb;
                __instance.NewRayHitPoint(collider.transform.position, target.photonView ? target.photonView.ViewID : -1, collider.colliderID, target.transform);
                __instance.attachPoint = __instance.rayHitPosition;
                Plugin.Verbose($"ヒーラードローン: {head.playerAvatar.playerName} の頭にくっつきました");
                __result = true;
                return false;
            }
        }

        private static PlayerDeathHead FindHead(Vector3 position, float range)
        {
            PlayerDeathHead best = null;
            float bestDistance = range;
            foreach (var player in GameDirector.instance.PlayerList)
            {
                var head = player ? player.playerDeathHead : null;
                if (!IsRevivable(head))
                {
                    continue;
                }
                float distance = Vector3.Distance(position, head.physGrabObject.centerPoint);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = head;
                }
            }
            return best;
        }

        private static bool IsRevivable(PlayerDeathHead head)
        {
            return head && head.triggered && head.physGrabObject && head.playerAvatar && head.playerAvatar.isDisabled;
        }

        // ---- 回復中の人が倒れたら、止めずに頭を探す ----

        [HarmonyPatch(typeof(ItemDrone), nameof(ItemDrone.CheckTargetDeath))]
        private static class TargetDeathPatch
        {
            private static bool Prefix(ItemDrone __instance)
            {
                // 転んでいる人を狙っているときは targetIsPlayer が false になるので、狙っている人そのもので判定する
                if (!ReviveActive || !GetMemo(__instance).Heal)
                {
                    return true;
                }
                bool dead = (__instance.playerAvatarTarget && __instance.playerAvatarTarget.isDisabled)
                    || (__instance.playerTumbleTarget && __instance.playerTumbleTarget.playerAvatar && __instance.playerTumbleTarget.playerAvatar.isDisabled);
                if (!dead)
                {
                    return true;
                }
                // 元はここでドローンを OFF にする。代わりに狙いを消すと、元の処理がビームを外して「探す」状態に戻す
                __instance.playerAvatarTarget = null;
                __instance.playerTumbleTarget = null;
                __instance.magnetTarget = null;
                return false;
            }
        }

        // ---- 回復の電池効率と、復活ゲージ ----

        [HarmonyPatch(typeof(ItemDroneHeal), nameof(ItemDroneHeal.Update))]
        private static class HealPatch
        {
            private static void Postfix(ItemDroneHeal __instance)
            {
                var drone = __instance.itemDrone;
                var battery = drone ? drone.itemBattery : null;
                if (!SemiFunc.IsMasterClientOrSingleplayer() || !battery)
                {
                    return;
                }
                // 元の「ビームを出している間ずっと減る」を止め、回復・復活に使った分だけ減らす
                battery.autoDrain = !Enabled.Value;
                if (!Enabled.Value || SemiFunc.RunIsShop())
                {
                    return;
                }
                HealCharge(drone, __instance, battery);
                ReviveProgress(drone, __instance, battery);
            }
        }

        /// <summary>
        /// くっついている人の体力が増えた分だけ電池を減らす。
        /// 回復は回復される人の PC で計算され、体力はホストへ送られてくるので、ホストが見た増加分で数える
        /// (ホスト側で回復のタイミングを予想すると、PC ごとのタイマーのずれで回復と電池が合わなくなる)。
        /// </summary>
        private static void HealCharge(ItemDrone drone, ItemDroneHeal heal, ItemBattery battery)
        {
            var memo = GetMemo(drone);
            var target = drone.playerAvatarTarget;
            if (heal.itemEquippable.isEquipped || !drone.itemActivated || !drone.magnetActive || !target || !target.playerHealth)
            {
                memo.HealTarget = null;
                return;
            }
            int health = target.playerHealth.health;
            if (memo.HealTarget == target && health > memo.LastHealth)
            {
                battery.batteryLife = Mathf.Max(0f, battery.batteryLife - (health - memo.LastHealth) * 100f / HealPerBattery.Value);
            }
            memo.HealTarget = target;
            memo.LastHealth = health;
        }

        private static void ReviveProgress(ItemDrone drone, ItemDroneHeal heal, ItemBattery battery)
        {
            var memo = GetMemo(drone);
            var target = drone.magnetTargetPhysGrabObject;
            PlayerDeathHead head = null;
            if (!heal.itemEquippable.isEquipped && drone.itemActivated && drone.magnetActive && target)
            {
                head = target.GetComponent<PlayerDeathHead>();
                if (head && (!ReviveActive || !IsRevivable(head)))
                {
                    // ほかの方法(トラック・デフィブロなど)で復活して頭が遠くへ移された: ビームを外して次を探す
                    Release(drone);
                    head = null;
                }
            }
            if (!head)
            {
                memo.Head = null;
                memo.Progress = 0f;
                return;
            }
            if (memo.Head != head)
            {
                memo.Head = head;
                memo.Progress = 0f;
            }

            float cost = ReviveCost.Value;
            float step = Mathf.Min(cost / ReviveTime.Value * Time.deltaTime, cost - memo.Progress, battery.batteryLife);
            if (step > 0f)
            {
                battery.batteryLife -= step;
                memo.Progress += step;
            }
            if (memo.Progress < cost - 0.001f)
            {
                return;
            }

            var player = head.playerAvatar;
            player.Revive(false);
            int extra = ReviveHealth.Value - 1;
            if (extra > 0)
            {
                player.playerHealth.HealOther(extra, true);
            }
            memo.Head = null;
            memo.Progress = 0f;
            // 頭は復活と同時に遠くへ移されるので、すぐビームを外して次の相手を探させる
            Release(drone);
            Plugin.Verbose($"ヒーラードローン: {player.playerName} を復活させました(電池 {cost:0}% 使用、残り {battery.batteryLife:0}%)");
        }

        private static void Release(ItemDrone drone)
        {
            drone.DeactivateMagnet();
            drone.magnetTarget = null;
            drone.StateSet(ItemDrone.State.Searching);
        }
    }
}
