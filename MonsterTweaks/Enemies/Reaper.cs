using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// リーパー(両手が刃物のかかし。見つけると全力で走ってくる)。クラス名は EnemyRunner。ホストだけで判断する。
    /// - 認識力: 視界の距離・視野の角度・気づくまでの速さを少し引き上げる
    /// - 構え: 見つけてから走り出すまで(Notice、本来 0.9 秒)を短くする
    /// - 狙う相手: 追いかけている間、今の相手より近くに認識できる(見えている)人がいれば乗り換える
    /// - 届かない相手: 道のない高所や物の下など、歩いて届かない相手を狙っているときは、認識できて届く人へ乗り換える
    ///   (ほかにいなければ本来どおり、ジャンプで向かう・物の下をのぞき込む)
    /// - 無重力: 刃に当たった人を無重力の杖と同じ効果で浮かせる。当たったかは、刃の当たり判定に入った人の体力が減ったかで見る
    ///   (体力は各自の PC で減り、ホストへ同期される)。減ったのが確かめられなければ、刃の範囲にいた人に短くかける
    /// どれも本来の RPC・無重力の杖の効果で全員に伝わるので、MOD なしの人にも効く。
    /// </summary>
    internal static class Reaper
    {
        private const string Section = "22. リーパー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> VisionRange;
        internal static ConfigEntry<float> VisionAngle;
        internal static ConfigEntry<float> NoticeSpeed;
        internal static ConfigEntry<float> NoticeTime;
        internal static ConfigEntry<bool> SwitchToCloser;
        internal static ConfigEntry<float> Memory;
        internal static ConfigEntry<bool> SkipUnreachable;
        internal static ConfigEntry<float> UnreachableIgnoreTime;
        internal static ConfigEntry<float> ZeroGravityHit;
        internal static ConfigEntry<float> ZeroGravityInRange;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "リーパーの変更を使う");
            VisionRange = Util.Float(cfg, Section, "視界の距離の倍率", 1.3f, 0.5f, 5f, "プレイヤーに気づく距離の倍率(1 でゲーム本来と同じ)");
            VisionAngle = Util.Float(cfg, Section, "視野の広さの倍率", 1.2f, 0.5f, 3f, "見える角度の倍率(1 でゲーム本来と同じ)");
            NoticeSpeed = Util.Float(cfg, Section, "気づくまでの時間の倍率", 0.7f, 0.1f, 2f,
                "見えてから気づくまでの時間の倍率(小さいほど早く気づく。1 でゲーム本来と同じ)");
            NoticeTime = Util.Float(cfg, Section, "見つけてから走り出すまでの時間", 0.3f, 0.05f, 3f,
                "見つけて構えてから走り出すまでの秒数(ゲーム本来は 0.9)");
            SwitchToCloser = cfg.Bind(Section, "より近い人に乗り換える", true,
                "追いかけている間、今の相手より近くに見えている人がいれば、その人に狙いを変える");
            Memory = Util.Float(cfg, Section, "見かけた人を覚えている時間", 1.5f, 0.3f, 10f,
                "見かけてからこの秒数のあいだは「認識している」とみなして、乗り換えの候補にする(壁越しの人は候補にしない)");
            SkipUnreachable = cfg.Bind(Section, "届かない相手はすぐ乗り換える", true,
                "道のない高所や物の下など、歩いて届かない相手を狙っているときは、見えていて届く人へすぐ乗り換える");
            UnreachableIgnoreTime = Util.Float(cfg, Section, "届かない相手を無視する時間", 5f, 1f, 30f,
                "届かないと分かった相手を、この秒数のあいだ狙わない(その後、届く場所に来ていればまた狙う)");
            ZeroGravityHit = Util.Float(cfg, Section, "刃に当たった人の無重力の時間", 1f, 0f, 10f,
                "刃に当たった人を無重力の杖と同じ効果で浮かせる秒数(0 で使わない)");
            ZeroGravityInRange = Util.Float(cfg, Section, "当たったか分からない人の無重力の時間", 0.5f, 0f, 10f,
                "刃の当たり判定に入ったが、体力が減ったのを確かめられなかった人を浮かせる秒数(0 で使わない)");
        }

        private static bool Active => Enabled.Value && Util.IsAuthority;

        private class RunnerMemo
        {
            internal readonly Dictionary<PlayerAvatar, float> Seen = new Dictionary<PlayerAvatar, float>();
            internal readonly Dictionary<PlayerAvatar, float> Unreachable = new Dictionary<PlayerAvatar, float>();
            internal float Timer;
            internal float UnreachableFor;
            internal readonly Dictionary<PlayerAvatar, Swing> Swings = new Dictionary<PlayerAvatar, Swing>();
            /// <summary>前の更新のときの各プレイヤーの体力(刃に入った同じ物理の更新でダメージが入っても、その前の体力と比べられるように)。</summary>
            internal readonly Dictionary<PlayerAvatar, int> LastHealth = new Dictionary<PlayerAvatar, int>();
        }

        private class Swing
        {
            internal int Health;
            internal float Since;
            internal bool Done;
        }

        private static readonly ConditionalWeakTable<EnemyRunner, RunnerMemo> Memos = new ConditionalWeakTable<EnemyRunner, RunnerMemo>();

        private static bool Ignored(RunnerMemo memo, PlayerAvatar player) =>
            SkipUnreachable.Value && player && memo.Unreachable.TryGetValue(player, out var until) && Time.time < until;

        private static Vector3 Position(EnemyRunner runner) => runner.enemy.Rigidbody ? runner.enemy.Rigidbody.transform.position : runner.transform.position;

        private static void SetTarget(EnemyRunner runner, PlayerAvatar player)
        {
            runner.targetPlayer = player;
            if (SemiFunc.IsMultiplayer())
            {
                runner.photonView.RPC("TargetPlayerRPC", RpcTarget.All, player.photonView.ViewID);
            }
        }

        // ---------------- 認識力 ----------------

        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.Update))]
        private static class PerceptionPatch
        {
            private static void Prefix(EnemyRunner __instance)
            {
                if (!Util.IsAuthority || !__instance.enemy || !__instance.enemy.Vision)
                {
                    return;
                }
                bool on = Enabled.Value;
                Util.ScaleVision(__instance.enemy.Vision, on ? VisionRange.Value : 1f);
                Util.ScaleVisionSense(__instance.enemy.Vision, on ? VisionAngle.Value : 1f, on ? NoticeSpeed.Value : 1f);
            }
        }

        // ---------------- 構え ----------------

        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.StateNotice))]
        private static class NoticePatch
        {
            private static void Prefix(EnemyRunner __instance, out bool __state) => __state = __instance.stateImpulse;

            private static void Postfix(EnemyRunner __instance, bool __state)
            {
                if (__state && Active && __instance.currentState == EnemyRunner.State.Notice)
                {
                    // 本来の処理はこの最初の更新でもう 1 フレーム分減らしている
                    __instance.stateTimer = Mathf.Max(0.01f, NoticeTime.Value - Time.deltaTime);
                }
            }
        }

        // ---------------- 狙う相手 ----------------

        /// <summary>見かけた人を覚えておく(本来は、追いかけている間に別の人を見ても無視する)。届かないと分かった人は狙わない。</summary>
        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.OnVision))]
        private static class VisionPatch
        {
            private static bool Prefix(EnemyRunner __instance)
            {
                if (!Util.IsAuthority || !__instance.enemy || !__instance.enemy.Vision)
                {
                    return true;
                }
                var player = __instance.enemy.Vision.onVisionTriggeredPlayer;
                if (!player)
                {
                    return true;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                memo.Seen[player] = Time.time;
                // 届かないと分かって無視している相手は、見えても狙わない(本来の追跡がすぐ始まって堂々巡りになるため)
                return !(Enabled.Value && Ignored(memo, player) && player != __instance.targetPlayer);
            }
        }

        private static bool Chasing(EnemyRunner.State state) =>
            state == EnemyRunner.State.AttackPlayer || state == EnemyRunner.State.AttackPlayerOver
            || state == EnemyRunner.State.Sneak || state == EnemyRunner.State.Notice
            || state == EnemyRunner.State.LookUnderStart || state == EnemyRunner.State.LookUnder;

        /// <summary>認識している(最近見かけて、今も見通せる)人のうち一番近い人。needReach なら歩いて届く人だけ。</summary>
        private static PlayerAvatar NearestRecognized(EnemyRunner runner, RunnerMemo memo, PlayerAvatar exclude, bool needReach, out float distance)
        {
            distance = float.MaxValue;
            PlayerAvatar best = null;
            Vector3 from = Position(runner);
            var forget = new List<PlayerAvatar>();
            foreach (var pair in memo.Seen)
            {
                var player = pair.Key;
                if (!player || player.isDisabled || Time.time - pair.Value > Memory.Value)
                {
                    forget.Add(player);
                    continue;
                }
                if (player == exclude || Ignored(memo, player))
                {
                    continue;
                }
                float d = Vector3.Distance(from, player.transform.position);
                if (d >= distance || !Reach.LineOfSight(runner.enemy, player) || (needReach && !Reach.Reachable(runner.enemy, player)))
                {
                    continue;
                }
                distance = d;
                best = player;
            }
            foreach (var player in forget)
            {
                memo.Seen.Remove(player);
            }
            return best;
        }

        private static void Retarget(EnemyRunner runner, PlayerAvatar next, string why)
        {
            var previous = runner.targetPlayer;
            SetTarget(runner, next);
            var state = runner.currentState;
            if (state == EnemyRunner.State.AttackPlayerOver || state == EnemyRunner.State.LookUnderStart || state == EnemyRunner.State.LookUnder)
            {
                // ジャンプで向かう・のぞき込むのをやめて、新しい相手を走って追う
                runner.UpdateState(EnemyRunner.State.AttackPlayer);
            }
            else if (state == EnemyRunner.State.AttackPlayer || state == EnemyRunner.State.Sneak)
            {
                // 新しい相手を追う時間を本来の長さ(2 秒)に戻す
                runner.stateTimer = Mathf.Max(runner.stateTimer, 2f);
            }
            Plugin.Verbose($"リーパー: 狙いを {previous?.playerName} から {next.playerName} に変えます({why})");
        }

        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.Update))]
        private static class TargetPatch
        {
            private static void Postfix(EnemyRunner __instance)
            {
                if (!Active || !__instance.enemy || __instance.enemy.IsStunned() || !Chasing(__instance.currentState))
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                memo.Timer -= Time.deltaTime;
                if (memo.Timer > 0f)
                {
                    return;
                }
                memo.Timer = 0.3f;
                var target = __instance.targetPlayer;
                if (!target || target.isDisabled)
                {
                    return;
                }
                var state = __instance.currentState;

                // 届かない相手: 0.6 秒続けて届かない、またはジャンプで向かう・物の下をのぞき込む状態になったら乗り換える
                if (SkipUnreachable.Value)
                {
                    bool forced = state == EnemyRunner.State.AttackPlayerOver || state == EnemyRunner.State.LookUnderStart || state == EnemyRunner.State.LookUnder;
                    if (forced || !Reach.Reachable(__instance.enemy, target))
                    {
                        memo.UnreachableFor += 0.3f;
                        if (forced || memo.UnreachableFor >= 0.6f)
                        {
                            var next = NearestRecognized(__instance, memo, target, true, out _);
                            if (next)
                            {
                                memo.Unreachable[target] = Time.time + UnreachableIgnoreTime.Value;
                                memo.UnreachableFor = 0f;
                                Retarget(__instance, next, "届かない");
                                return;
                            }
                        }
                    }
                    else
                    {
                        memo.UnreachableFor = 0f;
                    }
                }

                // より近くに認識できる人がいれば乗り換える(0.5m 以上近いときだけ。同じくらいの距離で行ったり来たりしない)
                if (SwitchToCloser.Value && (state == EnemyRunner.State.AttackPlayer || state == EnemyRunner.State.Sneak || state == EnemyRunner.State.Notice))
                {
                    // 届かない人は候補に入れる前に外す(届かない一番近い人のせいで、届く別の人を見落とさないように)
                    var closer = NearestRecognized(__instance, memo, target, SkipUnreachable.Value, out float closerDistance);
                    float targetDistance = Vector3.Distance(Position(__instance), target.transform.position);
                    if (closer && closerDistance + 0.5f < targetDistance)
                    {
                        Retarget(__instance, closer, "より近い");
                    }
                }
            }
        }

        // ---------------- 刃に当たった人を無重力に ----------------

        [HarmonyPatch(typeof(EnemyRunner), nameof(EnemyRunner.Update))]
        private static class ZeroGravityPatch
        {
            private const float Window = 0.8f;

            private static void Postfix(EnemyRunner __instance)
            {
                if (!Active || !__instance.hurtCollider || (ZeroGravityHit.Value <= 0f && ZeroGravityInRange.Value <= 0f))
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                float now = Time.time;

                // 刃の当たり判定が出ている間、その中に入った人を記録する(その時点の体力を覚える)
                var collider = __instance.hurtCollider.gameObject.activeInHierarchy ? __instance.hurtCollider.GetComponent<Collider>() : null;
                if (collider && collider.enabled)
                {
                    foreach (var player in SemiFunc.PlayerGetList())
                    {
                        if (!player || player.isDisabled || memo.Swings.ContainsKey(player) || !Inside(collider, player))
                        {
                            continue;
                        }
                        int before = memo.LastHealth.TryGetValue(player, out var last) ? Mathf.Max(last, player.playerHealth.health) : player.playerHealth.health;
                        memo.Swings[player] = new Swing { Health = before, Since = now };
                    }
                }
                RememberHealth(memo);
                if (memo.Swings.Count == 0)
                {
                    return;
                }

                // 体力が減った人 = 当たった人。一定時間たっても減ったのが確かめられなければ、範囲にいた人として短くかける
                List<PlayerAvatar> hit = null, inRange = null, finished = null;
                foreach (var pair in memo.Swings)
                {
                    var player = pair.Key;
                    var swing = pair.Value;
                    if (!player)
                    {
                        (finished ??= new List<PlayerAvatar>()).Add(player);
                        continue;
                    }
                    if (!swing.Done && player.playerHealth.health < swing.Health)
                    {
                        swing.Done = true;
                        (hit ??= new List<PlayerAvatar>()).Add(player);
                    }
                    if (now - swing.Since >= Window)
                    {
                        if (!swing.Done)
                        {
                            (inRange ??= new List<PlayerAvatar>()).Add(player);
                        }
                        (finished ??= new List<PlayerAvatar>()).Add(player);
                    }
                }
                if (hit != null && ZeroGravityHit.Value > 0f)
                {
                    ZeroGravityEffect.Apply(hit, ZeroGravityHit.Value);
                    Plugin.Verbose($"リーパー: 刃に当たった {hit.Count} 人を {ZeroGravityHit.Value:0.0} 秒無重力にしました");
                }
                if (inRange != null && ZeroGravityInRange.Value > 0f)
                {
                    ZeroGravityEffect.Apply(inRange, ZeroGravityInRange.Value);
                    Plugin.Verbose($"リーパー: 刃の範囲にいた {inRange.Count} 人を {ZeroGravityInRange.Value:0.0} 秒無重力にしました");
                }
                if (finished != null)
                {
                    foreach (var player in finished)
                    {
                        memo.Swings.Remove(player);
                    }
                }
            }

            private static void RememberHealth(RunnerMemo memo)
            {
                foreach (var player in SemiFunc.PlayerGetList())
                {
                    if (player && player.playerHealth)
                    {
                        memo.LastHealth[player] = player.playerHealth.health;
                    }
                }
            }

            /// <summary>その人の体(目の高さ・体の中心・足元)のどこかが刃の当たり判定の中にあるか。</summary>
            private static bool Inside(Collider collider, PlayerAvatar player)
            {
                Vector3 eyes = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
                Vector3 feet = player.transform.position + Vector3.up * 0.2f;
                Vector3 body = player.transform.position + Vector3.up * 0.7f;
                return Near(collider, eyes) || Near(collider, body) || Near(collider, feet);
            }

            private static bool Near(Collider collider, Vector3 point)
            {
                // ClosestPoint は凸でないメッシュの当たり判定には使えないので、そのときは外枠で見る
                if (collider is MeshCollider mesh && !mesh.convex)
                {
                    return collider.bounds.Contains(point);
                }
                return (collider.ClosestPoint(point) - point).sqrMagnitude <= 0.3f * 0.3f;
            }
        }
    }
}
