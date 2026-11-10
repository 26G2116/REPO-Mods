using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// リーパー(両手が刃物のかかし。見つけると全力で走ってくる)。クラス名は EnemyRunner。
    /// - 狙う相手: 最近見かけたプレイヤーが複数いるとき、一定距離以内に誰かいれば一番近い人を狙う
    ///   (全員離れていれば、今の相手を追い続ける)
    ///   追いかけている途中(忍び寄り・攻撃中)に 0.5 秒ごとに決め直す。ホストが決めて本来の RPC で全員に伝わる
    /// </summary>
    internal static class Reaper
    {
        private const string Section = "22. リーパー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> CloseRange;
        internal static ConfigEntry<float> Memory;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "リーパーの変更を使う");
            CloseRange = Util.Float(cfg, Section, "近くの人を優先する距離", 6f, 1f, 30f,
                "見かけた人のうち、この距離(m)以内にいる一番近い人に狙いを変える(全員離れていれば今の相手のまま)");
            Memory = Util.Float(cfg, Section, "見かけた人を覚えている時間", 4f, 0.5f, 20f,
                "見かけてからこの秒数のあいだは、狙う相手の候補にする");
        }

        private class RunnerMemo
        {
            internal readonly Dictionary<PlayerAvatar, float> Seen = new Dictionary<PlayerAvatar, float>();
            internal float Timer;
        }

        private static readonly ConditionalWeakTable<EnemyRunner, RunnerMemo> Memos = new ConditionalWeakTable<EnemyRunner, RunnerMemo>();

        /// <summary>見かけた人を覚えておく(本来は、追いかけている間に別の人を見ても無視する)。</summary>
        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.OnVision))]
        private static class VisionPatch
        {
            private static void Prefix(EnemyRunner __instance)
            {
                if (!Util.IsAuthority || !__instance.enemy || !__instance.enemy.Vision)
                {
                    return;
                }
                var player = __instance.enemy.Vision.onVisionTriggeredPlayer;
                if (player)
                {
                    Memos.GetOrCreateValue(__instance).Seen[player] = Time.time;
                }
            }
        }

        private static bool Chasing(EnemyRunner runner)
        {
            var state = runner.currentState;
            return state == EnemyRunner.State.AttackPlayer || state == EnemyRunner.State.AttackPlayerOver
                || state == EnemyRunner.State.Sneak || state == EnemyRunner.State.Notice;
        }

        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.Update))]
        private static class UpdatePatch
        {
            private static void Postfix(EnemyRunner __instance)
            {
                if (!Enabled.Value || !Util.IsAuthority || !Chasing(__instance) || !Memos.TryGetValue(__instance, out var memo))
                {
                    return;
                }
                memo.Timer -= Time.deltaTime;
                if (memo.Timer > 0f)
                {
                    return;
                }
                memo.Timer = 0.5f;
                var chosen = Choose(__instance, memo);
                if (!chosen || chosen == __instance.targetPlayer)
                {
                    return;
                }
                var previous = __instance.targetPlayer;
                __instance.targetPlayer = chosen;
                if (SemiFunc.IsMultiplayer())
                {
                    __instance.photonView.RPC("TargetPlayerRPC", RpcTarget.All, chosen.photonView.ViewID);
                }
                if (__instance.currentState == EnemyRunner.State.AttackPlayer || __instance.currentState == EnemyRunner.State.Sneak)
                {
                    // 新しい相手を追う時間を本来の長さ(2 秒)に戻す
                    __instance.stateTimer = Mathf.Max(__instance.stateTimer, 2f);
                }
                Plugin.Verbose($"リーパー: 狙いを {previous?.playerName} から {chosen.playerName} に変えます");
            }
        }

        private static PlayerAvatar Choose(EnemyRunner runner, RunnerMemo memo)
        {
            float now = Time.time;
            Vector3 from = runner.enemy.Rigidbody ? runner.enemy.Rigidbody.transform.position : runner.transform.position;
            var candidates = new List<PlayerAvatar>();
            var forget = new List<PlayerAvatar>();
            foreach (var pair in memo.Seen)
            {
                var player = pair.Key;
                if (!player || player.isDisabled || now - pair.Value > Memory.Value)
                {
                    forget.Add(player);
                    continue;
                }
                candidates.Add(player);
            }
            foreach (var player in forget)
            {
                memo.Seen.Remove(player);
            }
            // 今の相手は、見えていなくても候補に入れておく(本来どおり追い続けられるように)
            if (runner.targetPlayer && !runner.targetPlayer.isDisabled && !candidates.Contains(runner.targetPlayer))
            {
                candidates.Add(runner.targetPlayer);
            }
            if (candidates.Count < 2)
            {
                return null;
            }
            PlayerAvatar nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (var player in candidates)
            {
                float distance = Vector3.Distance(from, player.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = player;
                }
            }
            return nearestDistance <= CloseRange.Value ? nearest : null;
        }
    }
}
