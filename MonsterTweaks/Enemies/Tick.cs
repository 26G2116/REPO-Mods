using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ティック(ダニ。つかんだ人に吸いついて体力を吸う)。クラス名は EnemyTick。
    /// - 初期体力: 出現したときの体力(EnemyHealth.health)を変える(体力の判定はホストが行う)
    /// - 反撃: 攻撃してきたプレイヤー(銃・近接武器・投げつけた物など)に吸いつく。
    ///   ゲーム本来の「つかまれたら吸いつく」と同じ状態(Bite)に入れるので、吸いつく相手の PC がゲーム本来の処理で
    ///   ティックを強制的につかむ(MOD なしの人にも効く)。離れていたら相手に向かって飛びかかる。
    ///   攻撃でスタンしていたら、そのスタンはすぐ終わらせる
    /// - 吸いつかれた人のスタン: 吸いつき始めたときに、その人を転ばせて数秒起き上がれなくする
    ///   (ゲームの敵の攻撃と同じ TumbleStunTime。ホストが処理するので全員に効く)
    /// </summary>
    internal static class Tick
    {
        private const string Section = "13. ティック";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> StartHealth;
        internal static ConfigEntry<bool> Retaliate;
        internal static ConfigEntry<float> RetaliateRange;
        internal static ConfigEntry<float> PlayerStunTime;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ティックの変更を使う");
            StartHealth = Util.Int(cfg, Section, "初期体力", 20, 1, 100,
                "出現したときの体力(ゲーム本来は 10)。吸った体力はこれに加算され、最大 100 まで膨らむ");
            Retaliate = cfg.Bind(Section, "攻撃されたら反撃で吸いつく", true,
                "銃・近接武器・投げつけた物などで攻撃してきたプレイヤーに吸いつく(攻撃でスタンしていてもすぐ立ち直る)");
            RetaliateRange = Util.Float(cfg, Section, "反撃する距離", 12f, 2f, 40f,
                "攻撃してきたプレイヤーがこの距離(m)以内なら反撃する。離れていたら飛びかかってから吸いつく");
            PlayerStunTime = Util.Float(cfg, Section, "吸いつかれた人のスタン時間", 3f, 0f, 10f,
                "吸いつかれた人が転んで起き上がれない秒数(0 でスタンしない)");
        }

        private class TickMemo
        {
            internal PlayerAvatar Attacker;
            internal float AttackTime = -100f;
            /// <summary>反撃で Bite に入れた直後(相手がつかむまでの猶予を延ばす)。</summary>
            internal bool RetaliationBite;
            /// <summary>反撃で飛びかかっている相手。</summary>
            internal PlayerAvatar LeapTarget;
            internal float LeapStart;
            internal bool LeapBoosted;
        }

        private static readonly ConditionalWeakTable<EnemyTick, TickMemo> Memos = new ConditionalWeakTable<EnemyTick, TickMemo>();

        /// <summary>攻撃を覚えておく時間(この間にスタンが解けたら反撃する)。</summary>
        private const float AttackMemory = 3f;

        private static void RecordAttack(Enemy enemy, PlayerAvatar attacker)
        {
            if (!Enabled.Value || !Retaliate.Value || !Util.IsAuthority || !enemy || !attacker)
            {
                return;
            }
            var tick = enemy.GetComponent<EnemyTick>();
            if (!tick)
            {
                return;
            }
            var memo = Memos.GetOrCreateValue(tick);
            memo.Attacker = attacker;
            memo.AttackTime = Time.time;
        }

        // ---------------- 初期体力 ----------------

        private class HealthMemo
        {
            internal int Original;
        }

        private static readonly ConditionalWeakTable<EnemyHealth, HealthMemo> Healths = new ConditionalWeakTable<EnemyHealth, HealthMemo>();

        [HarmonyPatch(typeof(EnemyHealth), nameof(EnemyHealth.OnSpawn))]
        private static class HealthSpawnPatch
        {
            private static void Prefix(EnemyHealth __instance)
            {
                if (!Util.IsAuthority || !__instance.GetComponent<EnemyTick>())
                {
                    return;
                }
                var memo = Healths.GetValue(__instance, h => new HealthMemo { Original = h.health });
                // OnSpawn の中で healthCurrent = health になる
                __instance.health = Enabled.Value ? StartHealth.Value : memo.Original;
            }
        }

        // ---------------- 攻撃してきた人を覚える ----------------

        /// <summary>銃の弾・近接武器・爆発など(HurtCollider)。攻撃した人は playerCausingHurt に入っている。</summary>
        [HarmonyPatch(typeof(HurtCollider), nameof(HurtCollider.EnemyHurt))]
        private static class HurtColliderPatch
        {
            private static void Postfix(HurtCollider __instance, Enemy _enemy, bool __result)
            {
                if (__result && __instance.playerCausingHurt)
                {
                    RecordAttack(_enemy, __instance.playerCausingHurt);
                }
            }
        }

        /// <summary>物をぶつけられたとき。ぶつけた物を最後に持っていた人が onObjectHurtPlayer に入る。</summary>
        [HarmonyPatch(typeof(EnemyTick), nameof(EnemyTick.OnHurt))]
        private static class OnHurtPatch
        {
            private static void Postfix(EnemyTick __instance)
            {
                var health = __instance.enemy ? __instance.enemy.Health : null;
                if (!health || !health.onObjectHurtPlayer)
                {
                    return;
                }
                RecordAttack(__instance.enemy, health.onObjectHurtPlayer);
                health.onObjectHurtPlayer = null; // 次の別のダメージで同じ人を犯人にしない
            }
        }

        // ---------------- 反撃 ----------------

        [HarmonyPatch(typeof(EnemyTick), nameof(EnemyTick.Update))]
        private static class UpdatePatch
        {
            private static void Postfix(EnemyTick __instance)
            {
                if (!Util.IsAuthority || !Memos.TryGetValue(__instance, out var memo) || !memo.Attacker)
                {
                    return;
                }
                if (!Enabled.Value || !Retaliate.Value || Time.time - memo.AttackTime > AttackMemory)
                {
                    memo.Attacker = null;
                    return;
                }
                var enemy = __instance.enemy;
                var state = __instance.currentState;
                if (!enemy || !enemy.HasHealth || enemy.Health.dead || __instance.anim.hasBeenFull
                    || state == EnemyTick.State.Bite || state == EnemyTick.State.RideVehicle
                    || state == EnemyTick.State.Despawn || state == EnemyTick.State.Spawn
                    || enemy.CurrentState == EnemyState.Despawn)
                {
                    memo.Attacker = null;
                    return;
                }
                var target = memo.Attacker;
                Vector3 from = enemy.Rigidbody.transform.position;
                float distance = Vector3.Distance(from, target.transform.position);
                if (target.isDisabled || !target.physGrabber || distance > RetaliateRange.Value)
                {
                    // 反撃しない相手なら、スタンもそのまま(遠くから撃たれてもスタンが効く)
                    memo.Attacker = null;
                    return;
                }
                if (enemy.IsStunned())
                {
                    // 攻撃でスタンしたら、すぐ解けるようにする(タイマー切れで本来の流れに戻る)
                    if (enemy.HasStateStunned && enemy.StateStunned.stunTimer > 0.02f)
                    {
                        enemy.StateStunned.stunTimer = 0.02f;
                    }
                    return;
                }
                memo.Attacker = null;

                if (__instance.playerTarget != target)
                {
                    __instance.playerTarget = target;
                    __instance.NetworkTargetQueue(target);
                }
                if (distance <= BiteDistance)
                {
                    StartBite(__instance, memo, distance);
                    return;
                }
                // 離れていたら、まずゲーム本来の飛びかかり(Tumble)で相手に向かって跳ぶ。届いたら吸いつく(LeapPatch)
                __instance.lastSeenGrabbedObject = target.transform;
                memo.LeapTarget = target;
                memo.LeapStart = Time.time;
                memo.LeapBoosted = false;
                __instance.UpdateState(EnemyTick.State.Tumble);
                Plugin.Verbose($"ティック: {target.playerName} に反撃で飛びかかります(距離 {distance:0.0}m)");
            }
        }

        /// <summary>この距離(m)まで近づいたら吸いつく。</summary>
        private const float BiteDistance = 2.2f;

        private static void StartBite(EnemyTick tick, TickMemo memo, float distance)
        {
            memo.LeapTarget = null;
            memo.RetaliationBite = true;
            tick.UpdateState(EnemyTick.State.Bite);
            Plugin.Verbose($"ティック: {tick.playerTarget?.playerName} に反撃で吸いつきます(距離 {distance:0.0}m)");
        }

        /// <summary>反撃の飛びかかり中: 遠い相手には勢いを足し、届いたら吸いつく。届かなければ本来どおり着地して終わる。</summary>
        [HarmonyPatch(typeof(EnemyTick), nameof(EnemyTick.Update))]
        private static class LeapPatch
        {
            private static void Postfix(EnemyTick __instance)
            {
                if (!Util.IsAuthority || !Memos.TryGetValue(__instance, out var memo) || !memo.LeapTarget)
                {
                    return;
                }
                var target = memo.LeapTarget;
                if (!Enabled.Value || __instance.currentState != EnemyTick.State.Tumble || __instance.playerTarget != target
                    || target.isDisabled || Time.time - memo.LeapStart > 3f)
                {
                    memo.LeapTarget = null;
                    return;
                }
                if (__instance.stateImpulse)
                {
                    return; // 飛びかかりの最初の勢いは、次の更新でゲーム本来の処理がつける
                }
                var rb = __instance.enemy.Rigidbody;
                Vector3 from = rb.transform.position;
                Vector3 aim = target.PlayerVisionTarget ? target.PlayerVisionTarget.VisionTransform.position : target.transform.position;
                float distance = Vector3.Distance(from, target.transform.position);
                if (!memo.LeapBoosted)
                {
                    // 本来の勢い(10)は数 m しか届かないので、遠い相手には距離に応じて足す
                    memo.LeapBoosted = true;
                    float extra = Mathf.Clamp((distance - 3f) * 1.2f, 0f, 10f);
                    if (extra > 0f)
                    {
                        rb.rb.AddForce(Vector3.Lerp(aim - from, Vector3.up, 0.3f).normalized * extra, ForceMode.Impulse);
                    }
                }
                if (distance <= BiteDistance)
                {
                    StartBite(__instance, memo, distance);
                }
            }
        }

        // ---------------- 吸いつき始め: 猶予の延長と、吸いつかれた人のスタン ----------------

        [HarmonyPatch(typeof(EnemyTick), nameof(EnemyTick.StateBite))]
        private static class BitePatch
        {
            private static void Prefix(EnemyTick __instance, out bool __state)
            {
                __state = __instance.stateImpulse;
            }

            private static void Postfix(EnemyTick __instance, bool __state)
            {
                if (!__state || !Util.IsAuthority)
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                bool retaliation = memo.RetaliationBite;
                memo.RetaliationBite = false;
                if (!Enabled.Value)
                {
                    return;
                }
                if (retaliation)
                {
                    // 本来は 0.5 秒で「相手がつかんでいるか」を確かめ始める。離れた相手が引き寄せるまで少し待つ
                    __instance.stateTimer = Mathf.Max(__instance.stateTimer, 1.5f);
                }
                var player = __instance.playerTarget;
                float stun = PlayerStunTime.Value;
                if (stun > 0f && player && !player.isDisabled && player.tumble && player.playerHealth.health > 0)
                {
                    player.tumble.TumbleRequest(true, false);
                    player.tumble.TumbleStunTime(stun);
                    Plugin.Verbose($"ティック: 吸いついた {player.playerName} を {stun:0.0} 秒スタンさせます");
                }
            }
        }
    }
}
