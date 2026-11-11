using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// 店に出すアイテムの ON / OFF。
    ///
    /// 削除されたアイテムは、見た目(プレハブ)と定義(Item)が Resources の "Items/Removed Items" に残っているが、
    /// ゲームのアイテム一覧(StatsManager.itemDictionary)には読み込まれないので店にも出ない。
    /// アイテム一覧を作った直後に、それらを一覧の最後へ足す(既存のアイテムの番号はずれない)。
    /// - 一覧に入るので、ほかの MOD(RepoAdminMenu のアイテム召喚など)からも出せる
    /// - 店の品ぞろえ・購入・次の階への持ち越しはホストが計算するので、ホストだけ導入すれば店に並ぶ
    /// - 出したアイテムは Resources のパスで生成されるので、MOD なしの参加者の画面にも出て、元のプログラムどおりに動く
    /// 店に出すかどうかは、店の候補を作った直後に OFF のものを候補から外して決める(ゲーム中に変えても次の店から反映)。
    /// </summary>
    internal static class ShopItems
    {
        private const string RemovedFolder = "Items/Removed Items";

        private class Entry
        {
            internal string AssetName;
            internal bool Removed;
            internal ConfigEntry<bool> InShop;
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        internal static void Bind(ConfigFile cfg)
        {
            Add(cfg, "Item Drone Heal", "ヒーラードローン", true);
            Add(cfg, "Item Orb Feather", "フェザーオーブ", true);
            Add(cfg, "Item Orb Heal", "ヒーラーオーブ", true);
            Add(cfg, "Item Orb Indestructible", "不滅オーブ", true);
            Add(cfg, "Item Orb Magnet", "マグネットオーブ", true);
            Add(cfg, "Item Orb Torque", "ロールオーブ", true);
            Add(cfg, "Item Drone Torque", "ロールドローン", false);
        }

        private static void Add(ConfigFile cfg, string assetName, string label, bool removed)
        {
            string description = removed
                ? $"{label}(削除されたアイテム)を店に並べる候補に入れる。OFF でも RepoAdminMenu などの召喚からは出せる"
                : $"{label}を店に並べる候補に入れる(OFF にすると店に出なくなる)";
            Entries.Add(new Entry
            {
                AssetName = assetName,
                Removed = removed,
                InShop = cfg.Bind("1. 店に出すアイテム", label, true, description),
            });
        }

        // ---- 削除されたアイテムをアイテム一覧へ足す ----

        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.LoadItemsFromFolder))]
        private static class LoadItemsPatch
        {
            private static void Postfix(StatsManager __instance)
            {
                var removed = new Dictionary<string, Item>();
                foreach (var item in Resources.LoadAll<Item>(RemovedFolder))
                {
                    if (item && !removed.ContainsKey(item.name))
                    {
                        removed.Add(item.name, item);
                    }
                }

                foreach (var entry in Entries)
                {
                    if (!entry.Removed)
                    {
                        continue;
                    }
                    if (!removed.TryGetValue(entry.AssetName, out var item))
                    {
                        Plugin.Log.LogWarning($"{entry.AssetName} が見つかりませんでした(ゲームの更新でなくなった可能性があります)");
                        continue;
                    }
                    if (item.prefab == null || !item.prefab.IsValid())
                    {
                        Plugin.Log.LogWarning($"{entry.AssetName} のプレハブが見つからないので追加しません");
                        continue;
                    }
                    // 削除済みの印が付いていると、買っても次の階のトラックに出てこない(ItemManager.GetPurchasedItems)
                    item.disabled = false;
                    if (__instance.itemDictionary.TryAdd(item.name, item))
                    {
                        Plugin.Verbose($"アイテム一覧に {item.name}({item.itemName})を追加しました");
                    }
                }
            }
        }

        // ---- 店の候補から OFF のものを外す ----

        [HarmonyPatch(typeof(ShopManager), nameof(ShopManager.GetAllItemsFromStatsManager))]
        private static class ShopPoolPatch
        {
            private static void Postfix(ShopManager __instance)
            {
                var blocked = new HashSet<string>();
                foreach (var entry in Entries)
                {
                    if (!entry.InShop.Value)
                    {
                        blocked.Add(entry.AssetName);
                    }
                }
                if (blocked.Count == 0)
                {
                    return;
                }

                int count = Remove(__instance.potentialItems, blocked)
                    + Remove(__instance.potentialItemConsumables, blocked)
                    + Remove(__instance.potentialItemUpgrades, blocked)
                    + Remove(__instance.potentialItemHealthPacks, blocked);
                foreach (var list in __instance.potentialSecretItems.Values)
                {
                    count += Remove(list, blocked);
                }
                if (count > 0)
                {
                    Plugin.Verbose($"設定で OFF のアイテムを店の候補から {count} 個外しました");
                }
            }

            private static int Remove(List<Item> list, HashSet<string> blocked)
            {
                return list == null ? 0 : list.RemoveAll(item => item && blocked.Contains(item.name));
            }
        }
    }
}
