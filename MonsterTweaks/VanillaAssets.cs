using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ゲーム本来の物(貴重品・アイテムの弾など)を探して生成する。
    /// PhotonNetwork で生成するので、MOD なしの参加者の画面にもゲーム本来の見た目で出る。
    /// </summary>
    internal static class VanillaAssets
    {
        private static PrefabRef _egg;
        private static bool _eggSearched;
        private static PrefabRef _torqueProjectile;
        private static bool _torqueSearched;

        /// <summary>人類美術館の緑の卵(ValuableEgg)の貴重品。見つからなければ null。</summary>
        internal static PrefabRef Egg
        {
            get
            {
                if (!_eggSearched && RunManager.instance)
                {
                    _eggSearched = true;
                    _egg = FindValuable<ValuableEgg>("Egg");
                }
                return _egg;
            }
        }

        /// <summary>ロールの杖(ItemStaffTorque)が撃つ弾。中にロール効果の範囲効果(SemiAreaOfEffect)がある。</summary>
        internal static PrefabRef TorqueProjectile
        {
            get
            {
                if (!_torqueSearched && StatsManager.instance)
                {
                    _torqueSearched = true;
                    _torqueProjectile = FindTorqueProjectile();
                }
                return _torqueProjectile;
            }
        }

        /// <summary>ホストが生成する(マルチなら全員に出る部屋のオブジェクトとして)。</summary>
        internal static GameObject Spawn(PrefabRef prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null)
            {
                return null;
            }
            if (SemiFunc.IsMultiplayer())
            {
                return PhotonNetwork.InstantiateRoomObject(prefab.ResourcePath, position, rotation, 0, null);
            }
            var original = prefab.Prefab;
            return original ? Object.Instantiate(original, position, rotation) : null;
        }

        private static IEnumerable<PrefabRef> AllValuables()
        {
            foreach (var level in RunManager.instance.levels)
            {
                if (!level || level.ValuablePresets == null)
                {
                    continue;
                }
                foreach (var preset in level.ValuablePresets)
                {
                    if (!preset)
                    {
                        continue;
                    }
                    foreach (var list in new[] { preset.tiny, preset.small, preset.medium, preset.big, preset.wide, preset.tall, preset.veryTall })
                    {
                        if (list == null)
                        {
                            continue;
                        }
                        foreach (var valuable in list)
                        {
                            if (valuable != null)
                            {
                                yield return valuable;
                            }
                        }
                    }
                }
            }
        }

        private static PrefabRef FindValuable<T>(string nameHint) where T : Component
        {
            // 名前で当たりを付けてから中身を確認する(全部の貴重品を読み込むのを避ける)
            foreach (bool useHint in new[] { true, false })
            {
                foreach (var valuable in AllValuables())
                {
                    if (useHint && (valuable.PrefabName == null || !valuable.PrefabName.Contains(nameHint)))
                    {
                        continue;
                    }
                    var prefab = valuable.Prefab;
                    if (prefab && prefab.GetComponent<T>())
                    {
                        Plugin.Verbose($"{typeof(T).Name} を見つけました: {valuable.ResourcePath}");
                        return valuable;
                    }
                }
            }
            Plugin.Log.LogWarning($"{typeof(T).Name} の貴重品が見つかりませんでした(MOD の爆発・効果で代わりにします)");
            return null;
        }

        private static PrefabRef FindTorqueProjectile()
        {
            foreach (bool useHint in new[] { true, false })
            {
                foreach (var item in StatsManager.instance.itemDictionary.Values)
                {
                    if (!item || item.prefab == null)
                    {
                        continue;
                    }
                    if (useHint && (item.prefab.PrefabName == null || !item.prefab.PrefabName.Contains("Torque")))
                    {
                        continue;
                    }
                    var prefab = item.prefab.Prefab;
                    var staff = prefab ? prefab.GetComponent<ItemStaffTorque>() : null;
                    if (staff && staff.projectilePrefab != null)
                    {
                        Plugin.Verbose($"ロールの杖の弾を見つけました: {staff.projectilePrefab.ResourcePath}");
                        return staff.projectilePrefab;
                    }
                }
            }
            Plugin.Log.LogWarning("ロールの杖の弾が見つかりませんでした(MOD のロール効果で代わりにします)");
            return null;
        }
    }
}
