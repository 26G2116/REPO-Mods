using System.Collections.Generic;
using System.Linq;
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
            MultiAttack = cfg.Bind(Section, "チャージ完了時に物を持つ全員を攻撃", true,
                "攻撃のチャージがたまった瞬間、範囲内で貴重品・アイテムを持っている全員を、本来の相手と一緒に攻撃する(引き寄せ・ダメージ)");
            MultiAttackDamage = Util.Int(cfg, Section, "一緒に攻撃される人へのダメージ", 50, 0, 200, "本来の相手へのダメージは 50");
        }

        internal static ConfigEntry<bool> MultiAttack;
        internal static ConfigEntry<int> MultiAttackDamage;

        // ---------------- チャージ完了時に、物を持つ全員を攻撃 ----------------

        private class AttackMemo
        {
            internal readonly List<PlayerAvatar> Extras = new List<PlayerAvatar>();
        }

        private static readonly ConditionalWeakTable<EnemyThinMan, AttackMemo> Attacks = new ConditionalWeakTable<EnemyThinMan, AttackMemo>();

        /// <summary>チャージがたまった瞬間(Notice)に、一緒に攻撃する人を決める。</summary>
        [HarmonyPatch(typeof(EnemyThinMan), nameof(EnemyThinMan.StateNotice))]
        private static class NoticePatch
        {
            private static void Prefix(EnemyThinMan __instance)
            {
                var memo = Attacks.GetOrCreateValue(__instance);
                memo.Extras.Clear();
                if (!Enabled.Value || !MultiAttack.Value || !Util.IsAuthority || !__instance.enemy || !__instance.enemy.OnScreen)
                {
                    return;
                }
                foreach (var player in GrabbingPlayers(__instance.enemy.OnScreen, __instance))
                {
                    if (player && !player.isDisabled && player != __instance.playerTarget)
                    {
                        memo.Extras.Add(player);
                    }
                }
                if (memo.Extras.Count > 0)
                {
                    Plugin.Verbose($"シャドーチャイルド: 本来の相手と一緒に {memo.Extras.Count} 人を攻撃します");
                }
            }
        }

        /// <summary>本来の相手と同じく、ダメージ・転がり・つかみ解除を一緒に攻撃する人にも与える。</summary>
        [HarmonyPatch(typeof(EnemyThinMan), nameof(EnemyThinMan.StateDamage))]
        private static class DamagePatch
        {
            private static void Prefix(EnemyThinMan __instance)
            {
                if (!Util.IsAuthority || !__instance.playerTarget || !Attacks.TryGetValue(__instance, out var memo) || memo.Extras.Count == 0)
                {
                    return;
                }
                bool first = __instance.stateImpulse;
                int enemyIndex = SemiFunc.EnemyGetIndex(__instance.enemy);
                foreach (var player in memo.Extras)
                {
                    if (!player || player.isDisabled || !player.tumble)
                    {
                        continue;
                    }
                    if (first && MultiAttackDamage.Value > 0)
                    {
                        player.playerHealth.HurtOther(MultiAttackDamage.Value, player.transform.position, false, enemyIndex);
                    }
                    player.tumble.TumbleRequest(true, false);
                    player.tumble.TumbleOverrideTime(1f);
                    player.tumble.OverrideEnemyHurt(1f);
                    var tumbleObject = player.tumble.physGrabObject;
                    foreach (var grabber in tumbleObject.playerGrabbing.ToArray())
                    {
                        grabber.OverrideGrabRelease(tumbleObject.photonView.ViewID, 1f);
                    }
                }
            }

            private static void Postfix(EnemyThinMan __instance)
            {
                // 攻撃が終わったら(Despawn へ移ったら)忘れる
                if (__instance.currentState != EnemyThinMan.State.Damage && Attacks.TryGetValue(__instance, out var memo))
                {
                    memo.Extras.Clear();
                }
            }
        }

        /// <summary>本来の相手と同じ力で、一緒に攻撃する人もシャドーチャイルドのほうへ引き寄せる。</summary>
        [HarmonyPatch(typeof(EnemyThinMan), nameof(EnemyThinMan.StateDamageFixed))]
        private static class DamageFixedPatch
        {
            private static void Postfix(EnemyThinMan __instance)
            {
                if (!Util.IsAuthority || __instance.currentState != EnemyThinMan.State.Damage || !Attacks.TryGetValue(__instance, out var memo))
                {
                    return;
                }
                Vector3 target = __instance.enemy.Vision.VisionTransform.position;
                foreach (var player in memo.Extras)
                {
                    if (!player || player.isDisabled || !player.tumble || !player.tumble.physGrabObject)
                    {
                        continue;
                    }
                    var rb = player.tumble.physGrabObject.rb;
                    Vector3 direction = (target - player.tumble.physGrabObject.centerPoint).normalized;
                    rb.AddForce(direction * 5000f * Time.fixedDeltaTime, ForceMode.Force);
                    Vector3 torque = SemiFunc.PhysFollowDirection(rb.transform, direction, rb, 10f) * 2f;
                    rb.AddTorque(torque / rb.mass, ForceMode.Force);
                }
            }
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
