using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FloorRoles
{
    /// <summary>
    /// 店のアップグレードの品ぞろえを決まった形にする(ステータスは役職の報酬でもらう前提)。
    /// - マップの人数確認 × 4 と デスヘッドバッテリー × 1 は必ず
    /// - それ以外のアップグレードから、人数分(最大 5 個)をランダム
    /// 店の品ぞろえはホストが決めるので、ホストだけで全員に効く。
    /// ただし店に「アップグレードを置ける場所」が足りないと、置けた分だけになる。
    /// </summary>
    internal static class ShopUpgrades
    {
        private const string Section = "4. 店のアップグレード";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<int> MapPlayerCount;
        private static ConfigEntry<int> DeathHeadBattery;
        private static ConfigEntry<int> RandomMax;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "店のアップグレードを「人数確認 × 4、デスヘッドバッテリー × 1、ほかから人数分(最大 5)」にする");
            MapPlayerCount = cfg.Bind(Section, "マップの人数確認の数", 4, new ConfigDescription("必ず並べる数", new AcceptableValueRange<int>(0, 10)));
            DeathHeadBattery = cfg.Bind(Section, "デスヘッドバッテリーの数", 1, new ConfigDescription("必ず並べる数", new AcceptableValueRange<int>(0, 10)));
            RandomMax = cfg.Bind(Section, "ほかのアップグレードの最大数", 5,
                new ConfigDescription("それ以外のアップグレードを人数分並べるときの上限", new AcceptableValueRange<int>(0, 20)));
        }

        [HarmonyPatch(typeof(ShopManager), nameof(ShopManager.GetAllItemsFromStatsManager))]
        private static class ShopPoolPatch
        {
            private static void Postfix(ShopManager __instance)
            {
                if (!Enabled.Value || !Net.IsHost || !StatsManager.instance)
                {
                    return;
                }
                var upgrades = StatsManager.instance.itemDictionary.Values
                    .Where(i => i && i.itemType == SemiFunc.itemType.item_upgrade && i.prefab != null && i.prefab.Prefab)
                    .ToList();
                var map = upgrades.FirstOrDefault(i => i.prefab.Prefab.GetComponent<ItemUpgradeMapPlayerCount>());
                var deathHead = upgrades.FirstOrDefault(i => i.prefab.Prefab.GetComponent<ItemUpgradeDeathHeadBattery>());
                var others = upgrades.Where(i => i != map && i != deathHead).ToList();

                // 店はリストの後ろから順に置くので、必ず置きたいものを後ろにする
                var pool = new List<Item>();
                int players = GameDirector.instance ? GameDirector.instance.PlayerList.Count : 1;
                int randomCount = others.Count > 0 ? Mathf.Clamp(players, 1, RandomMax.Value) : 0;
                for (int i = 0; i < randomCount; i++)
                {
                    pool.Add(others[Random.Range(0, others.Count)]);
                }
                for (int i = 0; deathHead && i < DeathHeadBattery.Value; i++)
                {
                    pool.Add(deathHead);
                }
                for (int i = 0; map && i < MapPlayerCount.Value; i++)
                {
                    pool.Add(map);
                }
                __instance.potentialItemUpgrades.Clear();
                __instance.potentialItemUpgrades.AddRange(pool);
                __instance.itemUpgradesAmount = pool.Count;
                Plugin.Verbose($"店のアップグレード: {string.Join("、", pool.Select(i => i.itemName))}");
            }
        }

        [HarmonyPatch(typeof(PunManager), nameof(PunManager.ShopPopulateItemVolumes), new[] { typeof(List<PunManager.ItemSpawn>) })]
        private static class PopulatePatch
        {
            // アップグレードを置ける場所が足りずに残った分は、置けたアップグレードのそば(なければ抽出口のそば)に出す
            private static void Postfix(PunManager __instance, List<PunManager.ItemSpawn> _spawns)
            {
                if (!Enabled.Value || !Net.IsHost || !ShopManager.instance)
                {
                    return;
                }
                var leftover = ShopManager.instance.potentialItemUpgrades.ToList();
                if (leftover.Count == 0)
                {
                    return;
                }
                var upgradePaths = new HashSet<string>(StatsManager.instance.itemDictionary.Values
                    .Where(i => i && i.itemType == SemiFunc.itemType.item_upgrade && i.prefab != null)
                    .Select(i => i.prefab.ResourcePath));
                var anchors = _spawns == null
                    ? new List<Vector3>()
                    : _spawns.Where(s => s.prefab != null && upgradePaths.Contains(s.prefab.ResourcePath)).Select(s => s.position).ToList();
                if (anchors.Count == 0)
                {
                    var point = Object.FindObjectsByType<ExtractionPoint>(FindObjectsSortMode.None).FirstOrDefault(p => p);
                    anchors.Add(point ? point.transform.position + Vector3.up * 1.2f : Vector3.up);
                }
                for (int i = 0; i < leftover.Count; i++)
                {
                    var anchor = anchors[i % anchors.Count] + Vector3.up * 0.3f;
                    var spawn = new PunManager.ItemSpawn
                    {
                        prefab = leftover[i].prefab,
                        position = Net.FreeSpot(anchor, 0.35f, 1.0f, 0.15f),
                        rotation = Quaternion.identity,
                    };
                    if (_spawns != null)
                    {
                        _spawns.Add(spawn);
                    }
                    else
                    {
                        __instance.ItemSpawnApply(spawn);
                    }
                }
                ShopManager.instance.potentialItemUpgrades.Clear();
                Plugin.Log.LogInfo($"店のアップグレード: 置き場所が足りなかった {leftover.Count} 個を、ほかのアップグレードのそばに出しました");
            }
        }
    }
}
