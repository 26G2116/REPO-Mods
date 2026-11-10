using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;

namespace MonsterTweaks
{
    /// <summary>
    /// ローブ(黒い体に白い面。攻撃時に叫ぶ)の AI 強化。ホストだけで動く。
    /// ゲーム本来の状態(TargetPlayer = 突進して攻撃、SeekPlayer = 探して歩く)をそのまま使い、
    /// 「いつ・どこへ」だけを決め直すので、MOD なしの参加者の画面でも動きとアニメが合う。
    /// - 狩り: 範囲内のプレイヤーの位置を常に把握し、一番近い生存者を獲物にする
    /// - 奇襲: 獲物の画面に映っていない状態で奇襲距離まで近づいたら突進する
    /// - 待ち伏せ: 獲物の視界(向きと壁)に入らない地点を選んで回り込む。後ろ側ほど優先
    /// - 諦めない: 追跡中に去らない。相手が倒れたら次の人へ
    /// - 追跡速度: 突進の速さに倍率
    /// </summary>
    internal static class Robe
    {
        private const string Section = "5. ローブ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Hunt;
        internal static ConfigEntry<float> AwarenessRange;
        internal static ConfigEntry<float> StrikeRange;
        internal static ConfigEntry<bool> Ambush;
        internal static ConfigEntry<float> AmbushDistance;
        internal static ConfigEntry<float> PreyViewAngle;
        internal static ConfigEntry<float> SneakSpeed;
        internal static ConfigEntry<float> ChaseSpeed;
        internal static ConfigEntry<bool> NeverGiveUp;
        internal static ConfigEntry<bool> Retarget;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ローブの変更を使う");
            Hunt = cfg.Bind(Section, "プレイヤーの位置を常に把握", true,
                "範囲内のプレイヤーの位置を、見えていなくても常に把握して狩りに行く");
            AwarenessRange = Util.Float(cfg, Section, "把握する範囲", 30f, 5f, 150f, "位置を把握できる距離(m)");
            StrikeRange = Util.Float(cfg, Section, "奇襲する距離", 7f, 2f, 25f,
                "獲物に見られていない状態でこの距離(m)まで近づいたら、突進して襲う");
            Ambush = cfg.Bind(Section, "死角に回り込む", true,
                "獲物の視界に入らない地点(後ろ側を優先)へ回り込んでから襲う。OFF なら最短で近づく");
            AmbushDistance = Util.Float(cfg, Section, "待ち伏せする距離", 6f, 3f, 15f, "獲物からこの距離(m)くらいの死角を探す");
            PreyViewAngle = Util.Float(cfg, Section, "獲物の視野(片側の角度)", 60f, 30f, 120f,
                "プレイヤーが見えているとみなす角度。大きいほど慎重に、後ろへ回り込もうとする");
            SneakSpeed = Util.Float(cfg, Section, "忍び寄る速度倍率", 2f, 0.5f, 6f, "死角へ回り込むときの速さ(探索時の速さに対する倍率)");
            ChaseSpeed = Util.Float(cfg, Section, "突進の速度倍率", 1.3f, 0.5f, 4f, "襲いかかるときの速さの倍率(1 でゲーム本来と同じ)");
            NeverGiveUp = cfg.Bind(Section, "諦めない", true, "追跡中に去らず、相手の位置を把握している限り追い続ける");
            Retarget = cfg.Bind(Section, "倒したら次の人を狙う", true, "追跡中の相手が倒れたら、把握している範囲で一番近い人を狙う");
        }

        private class Brain
        {
            internal PlayerAvatar Prey;
            internal float ThinkTimer;
            internal float AmbushTimer;
            internal bool HasAmbush;
            internal Vector3 AmbushPoint;
            internal bool Driving;
        }

        private static readonly ConditionalWeakTable<EnemyRobe, Brain> Brains = new ConditionalWeakTable<EnemyRobe, Brain>();

        private static bool Active => Enabled.Value && Util.IsAuthority;

        private static Vector3 RobePosition(EnemyRobe robe) => robe.enemy.Rigidbody ? robe.enemy.Rigidbody.transform.position : robe.transform.position;

        /// <summary>この地点がプレイヤーの視界(向き・距離・壁)に入っているか。</summary>
        private static bool VisibleTo(PlayerAvatar player, Vector3 point)
        {
            var vision = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform : player.transform;
            Vector3 eye = vision.position;
            Vector3 to = point - eye;
            if (to.magnitude > 40f || Vector3.Angle(vision.forward, to) > PreyViewAngle.Value)
            {
                return false;
            }
            return !Physics.Linecast(eye, point, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
        }

        /// <summary>獲物の死角になる、回り込み先を探す。見つからなければ false。</summary>
        private static bool FindAmbushPoint(EnemyRobe robe, PlayerAvatar prey, out Vector3 best)
        {
            best = Vector3.zero;
            float bestScore = float.MinValue;
            Vector3 preyPos = prey.transform.position;
            var vision = prey.PlayerVisionTarget ? prey.PlayerVisionTarget.VisionTransform : prey.transform;
            Vector3 preyForward = Vector3.ProjectOnPlane(vision.forward, Vector3.up).normalized;
            Vector3 robePos = RobePosition(robe);
            float baseRadius = AmbushDistance.Value;
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = baseRadius * (ring == 0 ? 1f : 1.6f);
                for (int i = 0; i < 12; i++)
                {
                    Vector3 dir = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
                    if (!NavMesh.SamplePosition(preyPos + dir * radius, out var hit, 2f, NavMesh.AllAreas))
                    {
                        continue;
                    }
                    Vector3 point = hit.position;
                    if (VisibleTo(prey, point + Vector3.up * 1.2f))
                    {
                        continue;
                    }
                    Vector3 fromPrey = Vector3.ProjectOnPlane(point - preyPos, Vector3.up).normalized;
                    // 後ろ側ほど高得点、ローブから近いほど高得点
                    float score = -Vector3.Dot(preyForward, fromPrey) * baseRadius * 2f - Vector3.Distance(robePos, point) * 0.5f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = point;
                    }
                }
            }
            return bestScore > float.MinValue;
        }

        private static void SetTarget(EnemyRobe robe, PlayerAvatar player)
        {
            robe.targetPlayer = player;
            if (SemiFunc.IsMultiplayer())
            {
                robe.photonView.RPC("TargetPlayerRPC", RpcTarget.All, player.photonView.ViewID);
            }
        }

        private static void Strike(EnemyRobe robe, PlayerAvatar prey)
        {
            // ゲーム本来(OnVision)と同じ順番: 相手を決める → 状態を変える → 全員に相手を知らせる
            robe.targetPlayer = prey;
            robe.UpdateState(EnemyRobe.State.TargetPlayer);
            SetTarget(robe, prey);
            Plugin.Verbose($"ローブ: {prey.playerName} を奇襲します");
        }

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyRobe __instance)
            {
                if (!Active || !__instance.enemy || __instance.enemy.CurrentState == EnemyState.Despawn || __instance.enemy.IsStunned())
                {
                    return;
                }
                var brain = Brains.GetOrCreateValue(__instance);
                brain.Driving = false;
                var state = __instance.currentState;

                // 追跡中: 相手が倒れたら乗り換え、把握している限り諦めない
                if (state == EnemyRobe.State.TargetPlayer)
                {
                    var target = __instance.targetPlayer;
                    if ((!target || target.isDisabled) && Retarget.Value)
                    {
                        var next = Util.NearestAlivePlayer(RobePosition(__instance), AwarenessRange.Value, target);
                        if (next)
                        {
                            SetTarget(__instance, next);
                            target = next;
                        }
                    }
                    if (NeverGiveUp.Value && target && !target.isDisabled
                        && Vector3.Distance(RobePosition(__instance), target.transform.position) <= AwarenessRange.Value)
                    {
                        __instance.chaseTime = 0f;
                        if (!__instance.stateImpulse)
                        {
                            __instance.stateTimer = Mathf.Max(__instance.stateTimer, 1.5f);
                        }
                    }
                    return;
                }

                if (!Hunt.Value)
                {
                    return;
                }
                bool huntable = state == EnemyRobe.State.Idle || state == EnemyRobe.State.Roam || state == EnemyRobe.State.Investigate
                    || state == EnemyRobe.State.SeekPlayer || (state == EnemyRobe.State.Leave && NeverGiveUp.Value);
                if (!huntable)
                {
                    return;
                }

                brain.ThinkTimer -= Time.deltaTime;
                if (brain.ThinkTimer <= 0f || !brain.Prey || brain.Prey.isDisabled)
                {
                    brain.ThinkTimer = 0.2f;
                    brain.Prey = Util.NearestAlivePlayer(RobePosition(__instance), AwarenessRange.Value);
                }
                var prey = brain.Prey;
                if (!prey)
                {
                    return;
                }

                float distance = Vector3.Distance(RobePosition(__instance), prey.transform.position);
                bool seen = __instance.enemy.OnScreen && __instance.enemy.OnScreen.GetOnScreen(prey);
                if (distance <= StrikeRange.Value && (!seen || distance <= 3f))
                {
                    Strike(__instance, prey);
                    return;
                }

                // 回り込み・接近は「探して歩く」状態で行う(移動先は StateSeekPlayer の後で上書き)
                if (state != EnemyRobe.State.SeekPlayer)
                {
                    __instance.UpdateState(EnemyRobe.State.SeekPlayer);
                    return;
                }
                if (!__instance.stateImpulse)
                {
                    __instance.stateTimer = 20f;
                }
                brain.AmbushTimer -= Time.deltaTime;
                if (Ambush.Value && (brain.AmbushTimer <= 0f || (seen && brain.AmbushTimer > 0.3f)))
                {
                    brain.AmbushTimer = seen ? 0.3f : 1f;
                    brain.HasAmbush = FindAmbushPoint(__instance, prey, out brain.AmbushPoint);
                }
                else if (!Ambush.Value)
                {
                    brain.HasAmbush = false;
                }
                brain.Driving = true;
            }
        }

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.StateSeekPlayer))]
        private static class SeekPlayerPatch
        {
            private static void Postfix(EnemyRobe __instance)
            {
                if (!Active || __instance.currentState != EnemyRobe.State.SeekPlayer || !Brains.TryGetValue(__instance, out var brain) || !brain.Driving || !brain.Prey)
                {
                    return;
                }
                Vector3 destination = brain.HasAmbush ? brain.AmbushPoint : brain.Prey.transform.position;
                var agent = __instance.enemy.NavMeshAgent;
                // DefaultSpeed には共通の倍率がすでに入っていて、OverrideAgent でもう一度かかるので 1 回分割っておく
                float common = EnemyCommon.SpeedMultiplier(__instance.enemy);
                float speed = agent.DefaultSpeed * SneakSpeed.Value / common;
                agent.OverrideAgent(speed, Mathf.Max(agent.DefaultAcceleration, speed * 3f), 0.2f);
                __instance.enemy.Rigidbody.OverrideFollowPosition(0.2f, 3f * SneakSpeed.Value, -1f);
                agent.SetDestination(destination);
            }
        }

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.MoveTowardPlayer))]
        private static class ChaseSpeedPatch
        {
            private static void Postfix(EnemyRobe __instance)
            {
                if (!Active || Mathf.Approximately(ChaseSpeed.Value, 1f))
                {
                    return;
                }
                var agent = __instance.enemy.NavMeshAgent;
                float multiplier = ChaseSpeed.Value;
                // 直前の OverrideAgent で共通の倍率がかかっていて、ここでもう一度かかるので 1 回分割っておく
                float common = EnemyCommon.SpeedMultiplier(__instance.enemy);
                agent.OverrideAgent(agent.Agent.speed * multiplier / common, agent.DefaultAcceleration * multiplier, 0.2f);
            }
        }

        /// <summary>
        /// 体(リジッドボディ)が追従する速さは、MoveTowardPlayer の直後に呼ばれる StateTargetPlayer が
        /// 毎フレーム 5 に上書きするので、そのあとで倍率をかける。
        /// </summary>
        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.StateTargetPlayer))]
        private static class ChaseFollowPatch
        {
            private static void Postfix(EnemyRobe __instance)
            {
                if (!Active || Mathf.Approximately(ChaseSpeed.Value, 1f) || __instance.currentState != EnemyRobe.State.TargetPlayer)
                {
                    return;
                }
                __instance.enemy.Rigidbody.OverrideFollowPosition(0.2f, 5f * ChaseSpeed.Value, 30f);
            }
        }
    }
}
