using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// マグネットオーブ(削除されたアイテム)に「貴重品をアイテムに変える」能力を足す。
    ///
    /// - 起動したマグネットオーブの範囲内に貴重品が 5 個以上ある状態が一定時間(初期値 2 秒)続くと発動する
    ///   (持っていても置いていてもよい。設定で「置いているときだけ」にもできる)
    /// - 発動すると、範囲内の貴重品とオーブが壊れ、貴重品の合計金額に応じたアイテムが出る
    ///   (オーブの近くの空いている床に重ならないように出し、カートがあればほかのアイテムはカートの中に並べる)
    /// - 1 レベルに使える回数は全オーブ合計で決まっている(初期値 1 回)
    /// - オーブは店の購入記録から消さずに壊すだけなので、次のレベルでまたトラックに出る
    /// - 出たアイテムは購入記録に入らないので、そのレベル限り(次のレベルのトラックには出ない)
    ///   ゲームは「購入していないアイテムを使い切ったとき」も同じ種類の購入数を 1 減らしてしまうので、
    ///   報酬のアイテムではそれを止める(手榴弾・地雷・回復パックなど)
    /// すべてホストが計算し、壊す・出すはゲーム本来の通信で行うので、MOD なしの参加者にも見える。
    /// </summary>
    internal static class MagnetOrb
    {
        private const string Section = "5. マグネットオーブ";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<int> UsesPerLevel;
        private static ConfigEntry<int> MinValuables;
        private static ConfigEntry<float> ChargeTime;
        private static ConfigEntry<bool> OnlyWhenPlaced;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "範囲内の貴重品をアイテムに変える能力を足す(元の効果はそのまま)");
            UsesPerLevel = cfg.Bind(Section, "1 レベルに使える回数(全オーブ合計)", 1,
                new ConfigDescription("この回数だけ発動できる。レベルが変わると戻る", new AcceptableValueRange<int>(1, 10)));
            MinValuables = cfg.Bind(Section, "必要な貴重品の数", 5,
                new ConfigDescription("範囲内にこの数以上の貴重品があると発動の準備が始まる", new AcceptableValueRange<int>(1, 30)));
            ChargeTime = cfg.Bind(Section, "発動までの時間(秒)", 2f,
                new ConfigDescription("条件を満たした状態がこの秒数続くと発動する", new AcceptableValueRange<float>(0f, 30f)));
            OnlyWhenPlaced = cfg.Bind(Section, "置いているときだけ発動する", false,
                "ON: だれもオーブを持っていないときだけ発動する(オーブで貴重品を運ぶ途中で発動しないようにしたいとき)");
        }

        // ---- 報酬の表 ----

        private class Tier
        {
            internal int Max;          // この金額以下ならこの段階
            internal bool PickOne;     // true: 一覧からランダムに 1 つ / false: 一覧すべて
            internal string[] Items;   // アイテムの内部名(Item の名前)
        }

        private static readonly Tier[] Tiers =
        {
            new Tier { Max = 3000, PickOne = true, Items = new[] { "Item Health Pack Small", "Item Mine Explosive", "Item Grenade Explosive" } },
            new Tier { Max = 6000, PickOne = true, Items = new[] { "Item Drone Battery", "Item Drone Indestructible", "Item Gun Stun" } },
            new Tier { Max = 9999, PickOne = true, Items = new[] { "Item Drone Heal", "Item Gun Shotgun", "Item Gun Laser" } },
            new Tier { Max = 19999, PickOne = false, Items = new[] { "Item Cart Medium", "Item Cart Laser", "Item Melee Sword" } },
            new Tier
            {
                Max = int.MaxValue, PickOne = false,
                Items = new[]
                {
                    "Item Cart Medium", "Item Cart Cannon",
                    "Item Gun Handgun", "Item Gun Handgun", "Item Gun Handgun", "Item Gun Handgun",
                    "Item Drone Heal", "Item Drone Heal",
                },
            },
        };

        // ---- 状態 ----

        private static int usedThisLevel;

        // レベルが変わるたびに増える。出している途中でレベルが変わったら、残りを出さない
        private static int levelGeneration;

        // 報酬として出したアイテム(購入記録から外す・レベル終わりに名前を片付けるため)
        private static readonly List<ItemAttributes> Rewards = new List<ItemAttributes>();

        private class Memo
        {
            internal float Charge;
            internal float CheckTimer;
            internal int Count;
        }

        private static readonly ConditionalWeakTable<ItemOrbMagnet, Memo> Memos = new ConditionalWeakTable<ItemOrbMagnet, Memo>();

        // ---- 発動の判定(ホスト) ----

        [HarmonyPatch(typeof(ItemOrbMagnet), nameof(ItemOrbMagnet.FixedUpdate))]
        private static class ChargePatch
        {
            private static void Postfix(ItemOrbMagnet __instance)
            {
                if (!Enabled.Value || !SemiFunc.IsMasterClientOrSingleplayer() || !SemiFunc.RunIsLevel())
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                var orb = __instance.itemOrb;
                var self = __instance.physGrabObject;
                bool ready = usedThisLevel < UsesPerLevel.Value
                    && orb && self && !self.dead
                    && orb.itemActive && orb.currentState == ItemOrb.State.Active
                    && (!orb.itemBattery || orb.itemBattery.batteryLife > 0f)
                    && (!orb.itemEquippable || !orb.itemEquippable.isEquipped)
                    && (!OnlyWhenPlaced.Value || self.playerGrabbing.Count == 0);
                if (!ready)
                {
                    memo.Charge = 0f;
                    memo.CheckTimer = 0f;
                    return;
                }

                // 範囲の検索は重いので 0.25 秒ごと
                memo.CheckTimer -= Time.fixedDeltaTime;
                if (memo.CheckTimer <= 0f)
                {
                    memo.CheckTimer = 0.25f;
                    memo.Count = FindValuables(orb, self).Count;
                }
                if (memo.Count < MinValuables.Value)
                {
                    memo.Charge = 0f;
                    return;
                }
                if (memo.Charge <= 0f)
                {
                    Plugin.Verbose($"マグネットオーブ: 範囲内に貴重品が {memo.Count} 個あります。{ChargeTime.Value:0.#} 秒後に発動します");
                }
                memo.Charge += Time.fixedDeltaTime;
                if (memo.Charge < ChargeTime.Value)
                {
                    return;
                }
                memo.Charge = 0f;
                var valuables = FindValuables(orb, self);
                if (valuables.Count >= MinValuables.Value)
                {
                    Trigger(orb, self, valuables);
                }
            }
        }

        private static List<ValuableObject> FindValuables(ItemOrb orb, PhysGrabObject self)
        {
            var result = new List<ValuableObject>();
            var seen = new HashSet<PhysGrabObject>();
            foreach (var obj in SemiFunc.PhysGrabObjectGetAllWithinRange(orb.orbRadius, orb.transform.position, false, default(LayerMask), null))
            {
                // 当たり判定が複数ある物は、その数だけ一覧に入るので 1 回にまとめる
                if (!obj || obj == self || obj.dead || !obj.isValuable || !obj.impactDetector || !seen.Add(obj))
                {
                    continue;
                }
                var valuable = obj.GetComponent<ValuableObject>();
                if (valuable && valuable.dollarValueSet)
                {
                    result.Add(valuable);
                }
            }
            return result;
        }

        private static void Trigger(ItemOrb orb, PhysGrabObject self, List<ValuableObject> valuables)
        {
            int total = 0;
            foreach (var valuable in valuables)
            {
                total += Mathf.Max(0, Mathf.FloorToInt(valuable.dollarValueCurrent));
            }
            Tier tier = Tiers[Tiers.Length - 1];
            foreach (var t in Tiers)
            {
                if (total <= t.Max)
                {
                    tier = t;
                    break;
                }
            }
            var items = tier.PickOne ? new[] { tier.Items[Random.Range(0, tier.Items.Length)] } : tier.Items;

            usedThisLevel++;
            Vector3 center = self.centerPoint;

            foreach (var valuable in valuables)
            {
                valuable.physGrabObject.impactDetector.DestroyObject(true, true);
            }
            // 購入記録はそのままなので、次のレベルではまたトラックに出る
            if (self.impactDetector)
            {
                self.impactDetector.DestroyObject(true, true);
            }

            Plugin.Log.LogInfo($"マグネットオーブ: 貴重品 {valuables.Count} 個(合計 ${total})をアイテムに変えました: {string.Join(", ", items)}");
            Runner.Instance.StartCoroutine(SpawnRewards(center, items));
        }

        private static IEnumerator SpawnRewards(Vector3 origin, string[] names)
        {
            int generation = levelGeneration;
            // 壊れた貴重品とオーブが消えるのを少し待ってから、空いている場所を探す
            yield return new WaitForSeconds(0.4f);
            if (!SameLevel(generation))
            {
                yield break;
            }

            Item cartItem = null;
            var others = new List<Item>();
            foreach (var name in names)
            {
                if (!StatsManager.instance.itemDictionary.TryGetValue(name, out var item) || item == null || !item.prefab.Prefab)
                {
                    Plugin.Log.LogWarning($"マグネットオーブ: {name} がアイテム一覧にないので出せませんでした");
                    continue;
                }
                // 中に物を入れられるカート(最初の 1 台)に、ほかのアイテムを入れる
                if (!cartItem && item.prefab.Prefab.GetComponent<PhysGrabCart>())
                {
                    cartItem = item;
                    continue;
                }
                others.Add(item);
            }

            var planned = new List<Bounds>();
            if (cartItem)
            {
                var cart = Spawn(cartItem, origin, planned);
                // カートが床に落ち着くのを待ってから、中の位置を測る
                yield return new WaitForSeconds(0.6f);
                if (!SameLevel(generation))
                {
                    yield break;
                }
                var cartComponent = cart ? cart.GetComponent<PhysGrabCart>() : null;
                if (cartComponent)
                {
                    planned.Add(WorldBounds(cart));
                }
                var remaining = new List<Item>();
                foreach (var slot in RewardPlacer.PackInCart(cartComponent, others, remaining))
                {
                    SpawnAt(slot.Item, slot.Position, slot.Rotation);
                    yield return new WaitForSeconds(0.05f);
                    if (!SameLevel(generation))
                    {
                        yield break;
                    }
                }
                others = remaining;
            }

            foreach (var item in others)
            {
                Spawn(item, origin, planned);
                yield return new WaitForSeconds(0.1f);
                if (!SameLevel(generation))
                {
                    yield break;
                }
            }
        }

        private static Bounds WorldBounds(GameObject go)
        {
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (!col.isTrigger)
                {
                    bounds.Encapsulate(col.bounds);
                }
            }
            return bounds;
        }

        /// <summary>オーブの近くの空いている床に出す。</summary>
        private static GameObject Spawn(Item item, Vector3 origin, List<Bounds> planned)
        {
            if (!RewardPlacer.FindFloorSpot(item.prefab.Prefab, origin, planned, out var position, out var rotation))
            {
                Plugin.Log.LogWarning($"マグネットオーブ: {item.name} を置ける場所が見つからなかったので、オーブの位置に出します");
            }
            return SpawnAt(item, position, rotation);
        }

        private static bool SameLevel(int generation)
        {
            return generation == levelGeneration && SemiFunc.RunIsLevel() && StatsManager.instance;
        }

        private static GameObject SpawnAt(Item item, Vector3 position, Quaternion rotation)
        {
            GameObject spawned;
            if (SemiFunc.IsMultiplayer())
            {
                spawned = PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, position, rotation, 0, null);
            }
            else
            {
                spawned = Object.Instantiate(item.prefab.Prefab, position, rotation);
            }
            var attributes = spawned ? spawned.GetComponent<ItemAttributes>() : null;
            if (attributes)
            {
                Rewards.Add(attributes);
            }
            return spawned;
        }

        private static bool IsReward(string instanceName)
        {
            foreach (var attributes in Rewards)
            {
                // 壊れたアイテムでも名前は読める(Unity の null 判定は使わない)
                if ((object)attributes != null && attributes.instanceName == instanceName)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- 報酬のアイテムを使い切っても、購入したアイテムを減らさない(ホスト) ----

        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.ItemRemove))]
        private static class ItemRemovePatch
        {
            private static bool Prefix(StatsManager __instance, string instanceName)
            {
                if (string.IsNullOrEmpty(instanceName) || !IsReward(instanceName))
                {
                    return true;
                }
                __instance.item.Remove(instanceName);
                __instance.itemStatBattery.Remove(instanceName);
                return false;
            }
        }

        // ---- レベルが変わったら回数を戻し、報酬の名前を片付ける ----

        [HarmonyPatch(typeof(RunManager), nameof(RunManager.ChangeLevel))]
        private static class ChangeLevelPatch
        {
            private static void Prefix()
            {
                usedThisLevel = 0;
                levelGeneration++;
                if (SemiFunc.IsMasterClientOrSingleplayer() && StatsManager.instance)
                {
                    foreach (var attributes in Rewards)
                    {
                        string name = (object)attributes != null ? attributes.instanceName : null;
                        if (!string.IsNullOrEmpty(name))
                        {
                            StatsManager.instance.item.Remove(name);
                            StatsManager.instance.itemStatBattery.Remove(name);
                        }
                    }
                }
                Rewards.Clear();
            }
        }

        /// <summary>コルーチン用。シーンが変わっても消えないオブジェクト。</summary>
        private class Runner : MonoBehaviour
        {
            private static Runner instance;

            internal static Runner Instance
            {
                get
                {
                    if (!instance)
                    {
                        var go = new GameObject("DroneTweaks.Runner") { hideFlags = HideFlags.HideAndDontSave };
                        Object.DontDestroyOnLoad(go);
                        instance = go.AddComponent<Runner>();
                    }
                    return instance;
                }
            }
        }
    }
}
