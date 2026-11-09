using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// シャドーチャイルド(見られると触手を伸ばして襲ってくる)。
    /// 見ていなくても、敵から一定範囲内で貴重品やアイテムをつかんでいるプレイヤーは「見ている」扱いにする。
    /// 判定はホストの AI が使う EnemyOnScreen.GetOnScreen に割り込む。
    /// ホストは物をつかんでいるプレイヤーの一覧(PhysGrabObject.playerGrabbing)を持っているので、MOD なしの参加者にも効く。
    /// </summary>
    internal static class ShadowChild
    {
        private const string Section = "4. シャドーチャイルド";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<bool> Valuables;
        internal static ConfigEntry<bool> Items;
        internal static ConfigEntry<bool> NeedLineOfSight;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "シャドーチャイルドの変更を使う");
            Range = Util.Float(cfg, Section, "つかんでいると見つかる範囲", 8f, 1f, 40f,
                "この距離(m)以内で物をつかんでいると、見ていなくても見ている扱いになる");
            Valuables = cfg.Bind(Section, "貴重品をつかんでいると見つかる", true, "貴重品(お金になる物)が対象");
            Items = cfg.Bind(Section, "アイテムをつかんでいると見つかる", true, "武器・ドローンなどショップのアイテムが対象");
            NeedLineOfSight = cfg.Bind(Section, "壁越しでは見つからない", false,
                "ON にすると、敵とつかんでいる物の間に壁があるときは見つからない");
        }

        private class ThinManRef
        {
            internal EnemyThinMan ThinMan;
        }

        private class FrameCache
        {
            internal int Frame = -1;
            internal readonly HashSet<PlayerAvatar> Players = new HashSet<PlayerAvatar>();
        }

        private static readonly ConditionalWeakTable<EnemyOnScreen, ThinManRef> ThinManOf = new ConditionalWeakTable<EnemyOnScreen, ThinManRef>();
        private static readonly ConditionalWeakTable<EnemyOnScreen, FrameCache> Cache = new ConditionalWeakTable<EnemyOnScreen, FrameCache>();

        /// <summary>この敵の範囲内で、対象の物をつかんでいるプレイヤー(1 フレームに 1 回だけ計算)。</summary>
        private static HashSet<PlayerAvatar> GrabbingPlayers(EnemyOnScreen onScreen, EnemyThinMan thinMan)
        {
            var cache = Cache.GetOrCreateValue(onScreen);
            if (cache.Frame == Time.frameCount)
            {
                return cache.Players;
            }
            cache.Frame = Time.frameCount;
            cache.Players.Clear();

            var enemy = thinMan.enemy;
            Vector3 center = enemy && enemy.CenterTransform ? enemy.CenterTransform.position : thinMan.transform.position;
            foreach (var obj in SemiFunc.PhysGrabObjectGetAllWithinRange(Range.Value, center))
            {
                if (!obj || obj.playerGrabbing.Count == 0 || obj.isEnemy || obj.isPlayer)
                {
                    continue;
                }
                bool target = (Valuables.Value && obj.isValuable) || (Items.Value && obj.GetComponent<ItemAttributes>());
                if (!target)
                {
                    continue;
                }
                if (NeedLineOfSight.Value && Physics.Linecast(center, obj.midPoint, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                foreach (var grabber in obj.playerGrabbing)
                {
                    if (grabber && grabber.playerAvatar)
                    {
                        cache.Players.Add(grabber.playerAvatar);
                    }
                }
            }
            return cache.Players;
        }

        [HarmonyPatch(typeof(EnemyOnScreen), nameof(EnemyOnScreen.GetOnScreen))]
        private static class GetOnScreenPatch
        {
            private static void Postfix(EnemyOnScreen __instance, PlayerAvatar _playerAvatar, ref bool __result)
            {
                if (__result || !_playerAvatar || !Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                var thinMan = ThinManOf.GetValue(__instance, o => new ThinManRef { ThinMan = o.GetComponent<EnemyThinMan>() }).ThinMan;
                if (!thinMan || _playerAvatar.isDisabled)
                {
                    return;
                }
                if (GrabbingPlayers(__instance, thinMan).Contains(_playerAvatar))
                {
                    __result = true;
                }
            }
        }
    }
}
