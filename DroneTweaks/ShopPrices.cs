using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// ドローン・オーブの店の値段を設定で変える。
    ///
    /// - 値段はホストが「アイテムの値段の範囲(Value)× 倍率」から決めて参加者に送るので、ホストだけで全員に効く
    /// - 値段の範囲(Value)は複数のアイテムで共有されているので、書き換えずに、そのアイテム専用の範囲に差し替える
    ///   (店に並べる候補を作る直前に差し替えるので、ゲーム中に設定を変えても次の店から反映される)
    /// - 0 にするとゲーム本来の値段に戻す
    /// </summary>
    internal static class ShopPrices
    {
        private const string Section = "6. 店の値段";

        private class Entry
        {
            internal string AssetName;
            internal ConfigEntry<int> Price;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<Item, Value> Originals = new Dictionary<Item, Value>();
        private static readonly Dictionary<Item, Value> Customs = new Dictionary<Item, Value>();

        internal static void Bind(ConfigFile cfg)
        {
            Add(cfg, "Item Drone Battery", "リチャージドローン", "14〜18");
            Add(cfg, "Item Drone Feather", "フェザードローン", "14〜18");
            Add(cfg, "Item Drone Heal", "ヒーラードローン", "22〜30");
            Add(cfg, "Item Drone Indestructible", "不滅ドローン", "22〜30");
            Add(cfg, "Item Drone Torque", "ロールドローン", "8〜12");
            Add(cfg, "Item Drone Zero Gravity", "無重力ドローン", "22〜30");
            Add(cfg, "Item Orb Battery", "リチャージオーブ", "22〜30");
            Add(cfg, "Item Orb Feather", "フェザーオーブ", "14〜18");
            Add(cfg, "Item Orb Heal", "ヒーラーオーブ", "72〜100");
            Add(cfg, "Item Orb Indestructible", "不滅オーブ", "22〜30");
            Add(cfg, "Item Orb Magnet", "マグネットオーブ", "72〜100");
            Add(cfg, "Item Orb Torque", "ロールオーブ", "14〜18");
            Add(cfg, "Item Orb Zero Gravity", "無重力オーブ", "38〜48");
        }

        private static void Add(ConfigFile cfg, string assetName, string label, string vanilla)
        {
            Entries.Add(new Entry
            {
                AssetName = assetName,
                Price = cfg.Bind(Section, $"{label}(K)", 0,
                    new ConfigDescription($"{label}の店の値段(単位 K = $1,000)。0 = ゲーム本来の値段(${vanilla}K の間でランダム)",
                        new AcceptableValueRange<int>(0, 999))),
            });
        }

        /// <summary>設定の値段を、各アイテムの値段の範囲に反映する。</summary>
        private static void Apply()
        {
            var stats = StatsManager.instance;
            if (!stats)
            {
                return;
            }
            float multiplier = ShopManager.instance ? ShopManager.instance.itemValueMultiplier : 4f;
            if (multiplier <= 0f)
            {
                multiplier = 4f;
            }
            foreach (var entry in Entries)
            {
                if (!stats.itemDictionary.TryGetValue(entry.AssetName, out var item) || !item)
                {
                    continue;
                }
                if (!Originals.ContainsKey(item))
                {
                    Originals[item] = item.value;
                }
                int price = entry.Price.Value;
                if (price <= 0)
                {
                    item.value = Originals[item];
                    continue;
                }
                if (!Customs.TryGetValue(item, out var custom) || !custom)
                {
                    custom = ScriptableObject.CreateInstance<Value>();
                    custom.name = $"Value - {entry.AssetName} (DroneTweaks)";
                    custom.hideFlags = HideFlags.HideAndDontSave;
                    Customs[item] = custom;
                }
                // 店の値段 = 切り上げ(範囲 × 倍率 / 1000) K。小数の誤差で 1K 増えないよう、1 ドルだけ少なくする
                float raw = (price * 1000f - 1f) / multiplier;
                custom.valueMin = raw;
                custom.valueMax = raw;
                item.value = custom;
            }
        }

        [HarmonyPatch(typeof(ShopManager), nameof(ShopManager.GetAllItemsFromStatsManager))]
        private static class ShopPoolPatch
        {
            // 候補を作るときに値段で「買える物」を選ぶので、その前に反映する
            private static void Prefix()
            {
                Apply();
            }
        }
    }
}
