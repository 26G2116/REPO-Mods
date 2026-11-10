using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// アップスクリーム(顔に手が生えている。叫んで動けなくし、吹き飛ばす)。クラス名は EnemyUpscream。
    /// - 吹き飛ばし(叫び終わりのアニメイベント AttackImpulse。本来はホストが相手 1 人を転ばせて飛ばす):
    ///   力(本来 45)と転んでいる時間(本来 1.5 秒)に倍率をかけ、範囲内のほかのプレイヤーも同じように吹き飛ばす。
    ///   転ばせる・飛ばすのはゲーム本来の RPC なので MOD なしの人にも効く(動けない・視点固定は本来どおり相手 1 人だけ)
    /// - 体力: 出現したときの体力に倍率(HealthBoost)
    /// </summary>
    internal static class Upscream
    {
        private const string Section = "25. アップスクリーム";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ForceMultiplier;
        internal static ConfigEntry<float> TumbleTimeMultiplier;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<float> HealthMultiplier;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "アップスクリームの変更を使う");
            ForceMultiplier = Util.Float(cfg, Section, "吹き飛ばす力の倍率", 2f, 0.5f, 5f,
                "叫び終わりに吹き飛ばす力の倍率(1 でゲーム本来と同じ)");
            TumbleTimeMultiplier = Util.Float(cfg, Section, "転んでいる時間の倍率", 2f, 0.5f, 5f,
                "吹き飛ばされて転んでいる時間の倍率(ゲーム本来は 1.5 秒)");
            Radius = Util.Float(cfg, Section, "吹き飛ばしの範囲", 4f, 0f, 20f,
                "叫んだ相手のほかに、この距離(m)以内で壁越しでないプレイヤーも吹き飛ばす(0 で相手だけ)");
            HealthMultiplier = Util.Float(cfg, Section, "体力の倍率", 2f, 0.5f, 10f,
                "出現したときの体力の倍率(1 でゲーム本来と同じ)。次に出現したときから反映される");
            HealthBoost.Register(typeof(EnemyUpscream), () => Enabled.Value ? HealthMultiplier.Value : 1f);
        }

        /// <summary>吹き飛ばし: 本来と同じ手順で、力・時間に倍率をかけ、範囲内の全員に行う。</summary>
        [HarmonyPatch(typeof(EnemyUpscreamAnim), nameof(EnemyUpscreamAnim.AttackImpulse))]
        private static class AttackImpulsePatch
        {
            private static bool Prefix(EnemyUpscreamAnim __instance)
            {
                if (!Enabled.Value)
                {
                    return true;
                }
                if (!Util.IsAuthority)
                {
                    return false; // 本来もホストだけが行う
                }
                var target = __instance.controller ? __instance.controller.targetPlayer : null;
                if (!target)
                {
                    return false;
                }
                Vector3 origin = __instance.transform.position;
                Launch(target, origin);
                float radius = Radius.Value;
                if (radius <= 0f)
                {
                    return false;
                }
                int count = 0;
                foreach (var player in SemiFunc.PlayerGetList())
                {
                    if (!player || player == target || player.isDisabled || !player.tumble)
                    {
                        continue;
                    }
                    Vector3 to = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
                    if (Vector3.Distance(origin, to) > radius
                        || Physics.Linecast(origin + Vector3.up * 0.5f, to, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    Launch(player, origin);
                    count++;
                }
                if (count > 0)
                {
                    Plugin.Verbose($"アップスクリーム: 叫びで相手のほか {count} 人を吹き飛ばしました");
                }
                return false;
            }
        }

        /// <summary>EnemyUpscreamAnim.AttackImpulse と同じ吹き飛ばし(力と時間に倍率)。</summary>
        private static void Launch(PlayerAvatar player, Vector3 origin)
        {
            if (!player.tumble)
            {
                return;
            }
            Vector3 direction = (player.transform.position - origin).normalized;
            direction = Vector3.Lerp(direction, Vector3.up, 0.6f);
            float force = 45f * ForceMultiplier.Value;
            player.tumble.TumbleRequest(true, false);
            player.tumble.TumbleForce(direction * force);
            player.tumble.TumbleTorque(-player.transform.right * force);
            player.tumble.TumbleOverrideTime(1.5f * TumbleTimeMultiplier.Value);
            player.tumble.ImpactHurtSet(3f, 10);
        }
    }
}
