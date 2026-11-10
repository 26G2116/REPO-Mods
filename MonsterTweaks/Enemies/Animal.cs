using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// アニマル(六本足。見つけた相手の部屋で暴れ回る)。クラス名は EnemyAnimal。ホストだけで動く。
    /// - スタン無効: 共通設定(EnemyCommon)。このセクションの「スタン無効」
    /// - 転んでいない相手を優先: 追いかけ・暴れている間、今の相手が転んでいる(スタン中)か倒れていたら、
    ///   視界に入っている人のうち転んでいない一番近い人へ乗り換える。気づいた瞬間も同じ条件で選び直す。
    ///   アニマルの相手はホストの中だけで持っている(通信しない)ので、差し替えるだけで全員に効く
    /// </summary>
    internal static class Animal
    {
        private const string Section = "12. アニマル";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> PreferStanding;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "アニマルの変更を使う");
            PreferStanding = cfg.Bind(Section, "転んでいない相手を優先", true,
                "視界に入っている人のうち、転んで(スタンして)いない人を優先して狙う。今の相手が転んだら、転んでいない人へ乗り換える");
        }

        private static bool Usable(PlayerAvatar player) => player && !player.isDisabled && !player.isTumbling;

        /// <summary>視界に入っていて転んでいない人のうち、一番近い人。いなければ null。</summary>
        private static PlayerAvatar BestSeenStanding(EnemyAnimal animal)
        {
            var vision = animal.enemy.Vision;
            if (!vision)
            {
                return null;
            }
            PlayerAvatar best = null;
            float bestDistance = float.MaxValue;
            Vector3 position = animal.enemy.Rigidbody ? animal.enemy.Rigidbody.transform.position : animal.transform.position;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!Usable(player) || !vision.VisionTriggered.TryGetValue(player.photonView.ViewID, out bool seen) || !seen)
                {
                    continue;
                }
                float distance = Vector3.Distance(position, player.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }
            return best;
        }

        private static bool Active => Enabled.Value && PreferStanding.Value && Util.IsAuthority;

        [HarmonyPatch(typeof(EnemyAnimal), nameof(EnemyAnimal.OnVision))]
        private static class OnVisionPatch
        {
            private static void Postfix(EnemyAnimal __instance)
            {
                if (!Active || __instance.currentState != EnemyAnimal.State.PlayerNotice || Usable(__instance.playerTarget))
                {
                    return;
                }
                var better = BestSeenStanding(__instance);
                if (better)
                {
                    __instance.playerTarget = better;
                }
            }
        }

        [HarmonyPatch(typeof(EnemyAnimal), nameof(EnemyAnimal.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyAnimal __instance)
            {
                if (!Active || !__instance.enemy)
                {
                    return;
                }
                var state = __instance.currentState;
                if (state != EnemyAnimal.State.PlayerNotice && state != EnemyAnimal.State.GoToPlayer && state != EnemyAnimal.State.WreakHavoc)
                {
                    return;
                }
                if (Usable(__instance.playerTarget))
                {
                    return;
                }
                var better = BestSeenStanding(__instance);
                if (better && better != __instance.playerTarget)
                {
                    __instance.playerTarget = better;
                    Plugin.Verbose($"アニマル: 転んでいない {better.playerName} に乗り換えました");
                }
            }
        }
    }
}
