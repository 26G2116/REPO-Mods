using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ギャンビット(ルーレットのモンスター。捕まえた人にルーレットの結果を与える)。クラス名は EnemySpinny。ホストだけで動く。
    /// - 捕まえても歩き回る: ルーレットが回り始めてから結果が出るまでの間、近くの地点を順に移動先にする(本来は止まっている)。
    ///   捕まった人は口の位置へ引き寄せられ続けるので、一緒に引きずられる
    /// - 結果を周りにも: 結果が出た瞬間に、範囲内のほかのプレイヤーにも同じ効果を送る
    ///   (本来は捕まった本人が自分の PC で受け取る。ほかの人へはホストから HealOther / HurtOther で届ける)
    /// </summary>
    internal static class Gambit
    {
        private const string Section = "9. ギャンビット";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> WalkWhileHolding;
        internal static ConfigEntry<float> WalkSpeed;
        internal static ConfigEntry<bool> ShareResult;
        internal static ConfigEntry<float> ShareRadius;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ギャンビットの変更を使う");
            WalkWhileHolding = cfg.Bind(Section, "捕まえても歩き回る", true, "プレイヤーを捕まえてルーレットを回している間も、止まらずに歩き回る(捕まった人は引きずられる)");
            WalkSpeed = Util.Float(cfg, Section, "捕まえているときの歩く速さ", 2f, 0.5f, 8f, "ナビメッシュの速さ(見回りのときがおよそ 2.5)");
            ShareResult = cfg.Bind(Section, "結果を周りの全員にも", true,
                "ルーレットの結果を、範囲内のほかのプレイヤーにも与える(緑: 25 回復 / 赤: 50 ダメージ / 黒: 体力 1 まで / 白: 全回復。黄色のお金は本来どおり 1 つ)");
            ShareRadius = Util.Float(cfg, Section, "結果が届く範囲", 8f, 1f, 40f, "ギャンビットからこの距離(m)以内のプレイヤーに結果が届く");
        }

        private class Memo
        {
            internal Vector3 Destination;
            internal bool HasDestination;
            internal float Timer;
            internal float LagTimer;
        }

        /// <summary>捕まえた人が口の前の位置からこれ以上離れたら、追いつくまで歩くのを止める(m)。</summary>
        private const float LagDistance = 1f;
        /// <summary>離れたまま(地形に引っかかったまま)この秒数たったら、口の前へ引き戻す。</summary>
        private const float SnapTime = 0.75f;

        private static readonly ConditionalWeakTable<EnemySpinny, Memo> Memos = new ConditionalWeakTable<EnemySpinny, Memo>();

        private static bool Holding(EnemySpinny spinny)
        {
            // 引き寄せ中(WaitForRoulette)に動くと、位置がずれてルーレットが中止されるので、回り始めてから歩く
            return spinny.playerTarget && spinny.RouletteGoingOn() && spinny.currentState != EnemySpinny.State.WaitForRoulette;
        }

        [HarmonyPatch(typeof(EnemySpinny), nameof(EnemySpinny.Update))]
        private static class WalkPatch
        {
            private static void Postfix(EnemySpinny __instance)
            {
                if (!Enabled.Value || !WalkWhileHolding.Value || !Util.IsAuthority || !__instance.enemy || !Holding(__instance))
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                if (KeepUp(__instance, memo))
                {
                    return;
                }
                Vector3 position = __instance.enemy.transform.position;
                memo.Timer -= Time.deltaTime;
                if (!memo.HasDestination || memo.Timer <= 0f || Vector3.Distance(position, memo.Destination) < 1.5f)
                {
                    var point = SemiFunc.LevelPointGet(position, 5f, 20f);
                    memo.HasDestination = point;
                    if (point)
                    {
                        memo.Destination = point.transform.position;
                    }
                    memo.Timer = 8f;
                }
                if (memo.HasDestination)
                {
                    var agent = __instance.enemy.NavMeshAgent;
                    agent.OverrideAgent(WalkSpeed.Value, 10f, 0.25f);
                    agent.SetDestination(memo.Destination);
                }
            }

            /// <summary>
            /// 捕まえた人が遅れていたら歩くのを止める(true を返す)。地形に引っかかって離れたままなら、口の前へ引き戻す。
            /// 歩くと本来の引き寄せ(物理の力)が追いつかず、段差や家具に引っかかった人がはぐれてしまうため。
            /// </summary>
            private static bool KeepUp(EnemySpinny spinny, Memo memo)
            {
                var tumble = spinny.playerTarget.tumble;
                var lockPoint = spinny.playerLockPoint;
                if (!tumble || !tumble.rb || !lockPoint)
                {
                    return false;
                }
                float distance = Vector3.Distance(tumble.rb.position, lockPoint.position);
                if (distance <= LagDistance)
                {
                    memo.LagTimer = 0f;
                    return false;
                }
                // 追いつくまでその場で待つ
                spinny.enemy.NavMeshAgent.Stop(0.1f);
                // ほかのプレイヤーがつかんで助けようとしているときは引き戻さない
                if (tumble.physGrabObject && tumble.physGrabObject.grabbed)
                {
                    memo.LagTimer = 0f;
                    return true;
                }
                memo.LagTimer += Time.deltaTime;
                if (memo.LagTimer >= SnapTime && tumble.physGrabObject && LockPointClear(spinny, lockPoint.position))
                {
                    memo.LagTimer = 0f;
                    tumble.physGrabObject.Teleport(lockPoint.position, lockPoint.rotation);
                    tumble.rb.linearVelocity = Vector3.zero;
                    tumble.rb.angularVelocity = Vector3.zero;
                    Plugin.Verbose($"ギャンビット: 引っかかって {distance:0.0}m 離れた人を口の前へ引き戻しました");
                }
                return true;
            }

            /// <summary>口の前の位置が壁や物にめり込んでいないか(ギャンビットの中心から見通せるか)。めり込むなら引き戻さない。</summary>
            private static bool LockPointClear(EnemySpinny spinny, Vector3 lockPosition)
            {
                Vector3 from = spinny.enemy.CenterTransform ? spinny.enemy.CenterTransform.position : spinny.transform.position + Vector3.up * 0.5f;
                return !Physics.Linecast(from, lockPosition, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
            }
        }

        [HarmonyPatch(typeof(EnemySpinny), nameof(EnemySpinny.StateRouletteEffect))]
        private static class EffectPatch
        {
            private static void Prefix(EnemySpinny __instance)
            {
                // 結果が出た最初のフレームだけ
                if (!__instance.stateImpulse || !Enabled.Value || !ShareResult.Value || !Util.IsAuthority || !__instance.enemy)
                {
                    return;
                }
                var color = __instance.GetCurrentColorbyAngle(__instance.targetAngleDegrees);
                Vector3 center = __instance.enemy.CenterTransform ? __instance.enemy.CenterTransform.position : __instance.transform.position;
                int count = 0;
                foreach (var player in SemiFunc.PlayerGetList())
                {
                    if (!player || player.isDisabled || player == __instance.playerTarget
                        || Vector3.Distance(player.transform.position, center) > ShareRadius.Value)
                    {
                        continue;
                    }
                    var health = player.playerHealth;
                    switch (color)
                    {
                        case EnemySpinny.Colors.Green:
                            health.HealOther(25, true);
                            break;
                        case EnemySpinny.Colors.Red:
                            Hurt(player, 50);
                            break;
                        case EnemySpinny.Colors.Black:
                            Hurt(player, health.health - 1);
                            break;
                        case EnemySpinny.Colors.White:
                            health.HealOther(health.maxHealth, true);
                            break;
                        default:
                            continue;
                    }
                    count++;
                }
                if (count > 0)
                {
                    Plugin.Verbose($"ギャンビット: ルーレットの結果({color})を周りの {count} 人にも与えました");
                }
            }

            private static void Hurt(PlayerAvatar player, int damage)
            {
                if (damage <= 0)
                {
                    return;
                }
                if (player.isLocal || !SemiFunc.IsMultiplayer())
                {
                    player.playerHealth.Hurt(damage, false);
                }
                else
                {
                    player.playerHealth.HurtOther(damage, Vector3.zero, false);
                }
            }
        }
    }
}
