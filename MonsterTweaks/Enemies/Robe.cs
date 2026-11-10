using System.Collections.Generic;
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
    /// 方針: 対策できない理不尽はしない。ローブの強み(音を立てずに近づく)を生かす。
    /// - 忍び寄り: ゲーム本来は見つけた瞬間に突進する。見つけた相手の画面に自分が映っていなければ(気づかれていなければ)、
    ///   突進せずに相手の背後へ回り込む。最後に見てから一定時間だけ相手の位置を追う(見えていない相手をずっと追わない)
    /// - 奇襲: 気づかれていないまま奇襲距離まで近づいたら突進する。気づかれたら本来どおりすぐ突進する
    /// - 位置の把握(既定 OFF): 範囲内のプレイヤーの位置を見えていなくても把握する
    /// - 諦めない(既定 OFF): 追跡中に去らない
    /// - 倒したら次の人へ: 相手が倒れたら、見えている一番近い人に乗り換える
    /// - 突進の速さに倍率。忍び寄る速さは突進より速くならない
    /// - 認識力: 視界の距離・視野の角度・気づくまでの速さを引き上げる(ほかの敵の強化とのバランス)
    /// - 届かない相手: 道のない高所や物の下など、歩いて届かない相手は少しの間あきらめ、見えている届く人へすぐ乗り換える
    /// </summary>
    internal static class Robe
    {
        private const string Section = "5. ローブ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Stalk;
        internal static ConfigEntry<float> StalkMemory;
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
        internal static ConfigEntry<float> VisionRange;
        internal static ConfigEntry<float> VisionAngle;
        internal static ConfigEntry<float> NoticeSpeed;
        internal static ConfigEntry<bool> SkipUnreachable;
        internal static ConfigEntry<float> UnreachableIgnoreTime;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ローブの変更を使う");
            Stalk = cfg.Bind(Section, "気づかれていなければ忍び寄る", true,
                "プレイヤーを見つけても、自分がその人の画面に映っていなければ(気づかれていなければ)すぐ突進せず、" +
                "背後へ回り込んでから奇襲する。画面に映ったら(気づかれたら)ゲーム本来どおりすぐ突進する");
            StalkMemory = Util.Float(cfg, Section, "見失ってから追い続ける時間", 5f, 1f, 30f,
                "忍び寄っている相手を最後に見てから、この秒数のあいだだけ相手の位置を追う(過ぎたら見失う)");
            Hunt = cfg.Bind(Section, "プレイヤーの位置を常に把握", false,
                "範囲内のプレイヤーの位置を、見えていなくても常に把握して狩りに行く(壁越しでも分かるので強い)");
            AwarenessRange = Util.Float(cfg, Section, "把握する範囲", 30f, 5f, 150f,
                "「位置を常に把握」と「倒したら次の人を狙う」で、相手を探す距離(m)");
            StrikeRange = Util.Float(cfg, Section, "奇襲する距離", 7f, 2f, 25f,
                "気づかれていない状態でこの距離(m)まで近づいたら、突進して襲う");
            Ambush = cfg.Bind(Section, "背後に回り込む", true,
                "忍び寄るとき、相手の視界に入らない背後の地点へ回り込む。OFF なら最短で近づく");
            AmbushDistance = Util.Float(cfg, Section, "回り込む距離", 6f, 3f, 15f, "相手からこの距離(m)くらいの背後を目指す");
            PreyViewAngle = Util.Float(cfg, Section, "相手の視野(片側の角度)", 60f, 30f, 120f,
                "相手から見えるとみなす角度。大きいほど慎重に、真後ろへ回り込もうとする");
            SneakSpeed = Util.Float(cfg, Section, "忍び寄る速度倍率", 1f, 0.5f, 2f,
                "忍び寄るときの速さ(うろつくときの速さに対する倍率)。突進の速度倍率より速くはならない");
            ChaseSpeed = Util.Float(cfg, Section, "突進の速度倍率", 1.3f, 0.5f, 4f, "襲いかかるときの速さの倍率(1 でゲーム本来と同じ)");
            NeverGiveUp = cfg.Bind(Section, "諦めない", false, "追跡中に去らず、相手が把握する範囲にいる限り追い続ける");
            Retarget = cfg.Bind(Section, "倒したら次の人を狙う", true, "追跡中の相手が倒れたら、見えている一番近い人を狙う(壁越しの人は狙わない)");
            VisionRange = Util.Float(cfg, Section, "視界の距離の倍率", 2f, 0.5f, 5f,
                "プレイヤーに気づく距離の倍率(1 でゲーム本来と同じ)");
            VisionAngle = Util.Float(cfg, Section, "視野の広さの倍率", 1.5f, 0.5f, 3f,
                "見える角度の倍率(1 でゲーム本来と同じ。最大で真後ろまで)");
            NoticeSpeed = Util.Float(cfg, Section, "気づくまでの時間の倍率", 0.4f, 0.1f, 2f,
                "見えてから気づくまでの時間の倍率(小さいほど早く気づく。1 でゲーム本来と同じ)");
            SkipUnreachable = cfg.Bind(Section, "届かない相手はすぐ乗り換える", true,
                "道のない高所や物の下など、歩いて届かない相手を狙っているときは、見えている届く人へすぐ乗り換える");
            UnreachableIgnoreTime = Util.Float(cfg, Section, "届かない相手を無視する時間", 5f, 1f, 30f,
                "届かないと分かった相手を、この秒数のあいだ狙わない(その後、届く場所に来ていればまた狙う)");
        }

        private class Brain
        {
            internal PlayerAvatar Prey;
            internal float LastSeen = -100f;
            internal float ThinkTimer;
            internal float AmbushTimer;
            internal bool HasAmbush;
            internal Vector3 AmbushPoint;
            internal bool Driving;
            internal float ReachTimer;
            internal float UnreachableFor;
            /// <summary>届かないと分かった相手と、無視し終える時刻。</summary>
            internal readonly Dictionary<PlayerAvatar, float> Unreachable = new Dictionary<PlayerAvatar, float>();
        }

        private static readonly ConditionalWeakTable<EnemyRobe, Brain> Brains = new ConditionalWeakTable<EnemyRobe, Brain>();

        private static bool Active => Enabled.Value && Util.IsAuthority;

        private static Vector3 RobePosition(EnemyRobe robe) => robe.enemy.Rigidbody ? robe.enemy.Rigidbody.transform.position : robe.transform.position;

        /// <summary>ローブがその人の画面に映っているか(= 気づかれているとみなす。ゲーム本来の判定)。</summary>
        private static bool Noticed(EnemyRobe robe, PlayerAvatar player) => robe.enemy.OnScreen && robe.enemy.OnScreen.GetOnScreen(player);

        /// <summary>見つけたら突進する状態か(ゲーム本来の OnVision と同じ)。</summary>
        private static bool Wandering(EnemyRobe.State state) =>
            state == EnemyRobe.State.Idle || state == EnemyRobe.State.Roam || state == EnemyRobe.State.Investigate
            || state == EnemyRobe.State.SeekPlayer || state == EnemyRobe.State.Leave;

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

        /// <summary>
        /// 相手の背後で、相手から見えない回り込み先を探す。背後側に見つからなければ、見えない地点ならどこでもよい。
        /// どちらもなければ false(相手へまっすぐ近づく)。
        /// </summary>
        private static bool FindAmbushPoint(EnemyRobe robe, PlayerAvatar prey, out Vector3 best)
        {
            best = Vector3.zero;
            float bestScore = float.MinValue;
            bool bestBehind = false;
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
                    float behindness = -Vector3.Dot(preyForward, fromPrey); // 1 で真後ろ
                    bool behind = behindness >= 0.3f;
                    // 背後側を最優先。その中で、より真後ろ・ローブから近いほど高得点
                    float score = behindness * baseRadius * 2f - Vector3.Distance(robePos, point) * 0.5f;
                    if ((behind && !bestBehind) || (behind == bestBehind && score > bestScore))
                    {
                        bestScore = score;
                        bestBehind = behind;
                        best = point;
                    }
                }
            }
            return bestScore > float.MinValue;
        }

        // ---------------- 届くかどうか ----------------

        /// <summary>歩いてその人に攻撃が届くか(Reach.cs。道のない高所や、ローブが入れない物の下にいると届かない)。</summary>
        private static bool Reachable(EnemyRobe robe, PlayerAvatar player) => Reach.Reachable(robe.enemy, player);

        /// <summary>このローブが届かないと判断して無視している相手か(ローブごと。別のローブからは届くことがある)。</summary>
        private static bool Ignored(EnemyRobe robe, PlayerAvatar player) =>
            SkipUnreachable.Value && player && Brains.TryGetValue(robe, out var brain)
            && brain.Unreachable.TryGetValue(player, out var until) && Time.time < until;

        private static void MarkUnreachable(EnemyRobe robe, PlayerAvatar player)
        {
            Brains.GetOrCreateValue(robe).Unreachable[player] = Time.time + UnreachableIgnoreTime.Value;
            Plugin.Verbose($"ローブ: {player.playerName} には届かないので、しばらく狙いません");
        }

        /// <summary>見えていて(壁越しでなく)、届き、無視していない一番近い生存者。</summary>
        private static PlayerAvatar NearestReachableVisible(EnemyRobe robe, PlayerAvatar exclude)
        {
            var eyes = robe.enemy.Vision ? robe.enemy.Vision.VisionTransform.position : RobePosition(robe) + Vector3.up;
            PlayerAvatar best = null;
            float bestDistance = AwarenessRange.Value;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || player == exclude || Ignored(robe, player))
                {
                    continue;
                }
                Vector3 target = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
                float distance = Vector3.Distance(eyes, target);
                if (distance >= bestDistance || Physics.Linecast(eyes, target, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore)
                    || !Reachable(robe, player))
                {
                    continue;
                }
                bestDistance = distance;
                best = player;
            }
            return best;
        }

        /// <summary>ローブの目からその人が見通せるか(間に壁がない)。</summary>
        private static bool HasLineOfSight(EnemyRobe robe, PlayerAvatar player)
        {
            var eyes = robe.enemy.Vision ? robe.enemy.Vision.VisionTransform.position : RobePosition(robe) + Vector3.up;
            Vector3 target = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
            return !Physics.Linecast(eyes, target, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
        }

        /// <summary>ローブから見えている(壁越しでない)一番近い生存者。</summary>
        private static PlayerAvatar NearestVisiblePlayer(EnemyRobe robe, float range, PlayerAvatar exclude)
        {
            var eyes = robe.enemy.Vision ? robe.enemy.Vision.VisionTransform.position : RobePosition(robe) + Vector3.up;
            PlayerAvatar best = null;
            float bestDistance = range;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || player == exclude || Ignored(robe, player))
                {
                    continue;
                }
                Vector3 target = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
                float distance = Vector3.Distance(eyes, target);
                if (distance >= bestDistance || Physics.Linecast(eyes, target, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                bestDistance = distance;
                best = player;
            }
            return best;
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
            if (Brains.TryGetValue(robe, out var brain))
            {
                brain.Prey = null;
            }
            Plugin.Verbose($"ローブ: {prey.playerName} を奇襲します");
        }

        // ---------------- 見つけたとき: 気づかれていなければ突進せずに忍び寄る ----------------

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.OnVision))]
        private static class VisionPatch
        {
            private static bool Prefix(EnemyRobe __instance)
            {
                // 届かないと分かって無視している相手は、見えても狙わない(本来の突進がすぐ始まって堂々巡りになるため)
                if (Active && __instance.enemy && __instance.enemy.Vision && Ignored(__instance, __instance.enemy.Vision.onVisionTriggeredPlayer)
                    && __instance.currentState != EnemyRobe.State.Attack)
                {
                    return false;
                }
                if (!Active || !Stalk.Value || !__instance.enemy || !__instance.enemy.Vision || __instance.enemy.IsStunned()
                    || __instance.enemy.CurrentState == EnemyState.Despawn || !Wandering(__instance.currentState))
                {
                    return true;
                }
                var player = __instance.enemy.Vision.onVisionTriggeredPlayer;
                if (!player || player.isDisabled)
                {
                    return true;
                }
                float distance = Vector3.Distance(RobePosition(__instance), player.transform.position);
                // 気づかれている・すぐそばにいるなら、ゲーム本来どおりすぐ突進する
                if (Noticed(__instance, player) || distance <= 3f)
                {
                    return true;
                }
                var brain = Brains.GetOrCreateValue(__instance);
                // 忍び寄る相手は、見つけた人(すでに忍び寄っている相手がいて、その人をまだ覚えていればそのまま)
                if (!brain.Prey || brain.Prey == player || brain.Prey.isDisabled || Time.time - brain.LastSeen > StalkMemory.Value)
                {
                    if (brain.Prey != player)
                    {
                        Plugin.Verbose($"ローブ: {player.playerName} に気づかれていないので忍び寄ります");
                    }
                    brain.Prey = player;
                    brain.LastSeen = Time.time;
                }
                if (distance <= StrikeRange.Value && brain.Prey == player)
                {
                    Strike(__instance, player);
                }
                return false;
            }
        }

        // ---------------- 毎フレーム: 忍び寄り・奇襲・追跡 ----------------

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

                // 届かない相手を追っている(物の下をのぞき込んでいる)なら、見えている届く人へ乗り換える
                if (SkipUnreachable.Value && HandleUnreachable(__instance, brain, state))
                {
                    return;
                }

                // 追跡中: 相手が倒れたら見えている人へ乗り換え、(設定で)諦めない
                if (state == EnemyRobe.State.TargetPlayer)
                {
                    var target = __instance.targetPlayer;
                    if ((!target || target.isDisabled) && Retarget.Value)
                    {
                        // 届く人を優先し、いなければ見えている人
                        var next = NearestReachableVisible(__instance, target) ?? NearestVisiblePlayer(__instance, AwarenessRange.Value, target);
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

                bool huntable = Wandering(state) && (state != EnemyRobe.State.Leave || NeverGiveUp.Value || Hunt.Value || brain.Prey);
                if (!huntable)
                {
                    return;
                }

                // 相手を決める: 「位置を常に把握」なら一番近い人、そうでなければ見つけた相手を覚えている間だけ
                if (Hunt.Value)
                {
                    brain.ThinkTimer -= Time.deltaTime;
                    if (brain.ThinkTimer <= 0f || !brain.Prey || brain.Prey.isDisabled || Ignored(__instance, brain.Prey))
                    {
                        brain.ThinkTimer = 0.2f;
                        brain.Prey = NearestAliveNotIgnored(__instance, AwarenessRange.Value);
                        brain.LastSeen = Time.time;
                    }
                }
                else if (!Stalk.Value || !brain.Prey || brain.Prey.isDisabled || Ignored(__instance, brain.Prey) || Time.time - brain.LastSeen > StalkMemory.Value)
                {
                    if (brain.Prey)
                    {
                        Plugin.Verbose($"ローブ: {brain.Prey.playerName} を見失いました");
                    }
                    brain.Prey = null;
                }
                var prey = brain.Prey;
                if (!prey)
                {
                    return;
                }

                float distance = Vector3.Distance(RobePosition(__instance), prey.transform.position);
                bool noticed = Noticed(__instance, prey);
                // 襲うのは相手が見通せるときだけ(壁越しには襲わない)。
                // 気づかれていなければ奇襲する距離まで近づいてから、気づかれたら(忍び寄りの最中なら)すぐ突進する
                if (HasLineOfSight(__instance, prey)
                    && (distance <= 3f || (!noticed && distance <= StrikeRange.Value) || (noticed && !Hunt.Value)))
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
                if (Ambush.Value && (brain.AmbushTimer <= 0f || (noticed && brain.AmbushTimer > 0.3f)))
                {
                    brain.AmbushTimer = noticed ? 0.3f : 1f;
                    brain.HasAmbush = FindAmbushPoint(__instance, prey, out brain.AmbushPoint);
                }
                else if (!Ambush.Value)
                {
                    brain.HasAmbush = false;
                }
                brain.Driving = true;
            }
        }

        private static PlayerAvatar NearestAliveNotIgnored(EnemyRobe robe, float range)
        {
            Vector3 position = RobePosition(robe);
            PlayerAvatar best = null;
            float bestDistance = range;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || Ignored(robe, player))
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

        /// <summary>
        /// 追跡中・物の下をのぞき込み中に、相手に届かなければ乗り換える。乗り換えたら true。
        /// 追跡中は 0.3 秒ごとに確かめ、0.6 秒続けて届かなければ届かないとみなす(段差の上り下りの一瞬で切り替えない)。
        /// 物の下をのぞき込み始めたら(本来の LookUnder)、その場で届かないとみなす。
        /// </summary>
        private static bool HandleUnreachable(EnemyRobe robe, Brain brain, EnemyRobe.State state)
        {
            bool lookUnder = state == EnemyRobe.State.LookUnderStart || state == EnemyRobe.State.LookUnder;
            if (state != EnemyRobe.State.TargetPlayer && !lookUnder)
            {
                brain.UnreachableFor = 0f;
                return false;
            }
            var target = robe.targetPlayer;
            if (!target || target.isDisabled)
            {
                return false;
            }
            if (!lookUnder)
            {
                brain.ReachTimer -= Time.deltaTime;
                if (brain.ReachTimer > 0f)
                {
                    return false;
                }
                brain.ReachTimer = 0.3f;
                if (Reachable(robe, target))
                {
                    brain.UnreachableFor = 0f;
                    return false;
                }
                brain.UnreachableFor += 0.3f;
                if (brain.UnreachableFor < 0.6f)
                {
                    return false;
                }
            }
            var next = NearestReachableVisible(robe, target);
            if (!next)
            {
                return false; // ほかに狙える人がいなければ、本来の動き(のぞき込み・探索)に任せる
            }
            MarkUnreachable(robe, target);
            brain.UnreachableFor = 0f;
            brain.Prey = null;
            robe.targetPlayer = next;
            if (state != EnemyRobe.State.TargetPlayer)
            {
                robe.UpdateState(EnemyRobe.State.TargetPlayer);
            }
            else
            {
                robe.stateTimer = Mathf.Max(robe.stateTimer, 2f);
            }
            SetTarget(robe, next);
            Plugin.Verbose($"ローブ: 狙いを {next.playerName} に切り替えました");
            return true;
        }

        /// <summary>認識力: 視界の距離・視野の角度・気づくまでの速さ(視界の判定はホストが行う)。</summary>
        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.Update))]
        private static class PerceptionPatch
        {
            private static void Prefix(EnemyRobe __instance)
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
                // 忍び寄る速さは突進より速くしない。共通の倍率は OverrideAgent のパッチ(EnemyCommon)でかかる
                float sneak = Mathf.Min(SneakSpeed.Value, ChaseSpeed.Value);
                float speed = agent.DefaultSpeed * sneak;
                agent.OverrideAgent(speed, Mathf.Max(agent.DefaultAcceleration, speed * 3f), 0.2f);
                __instance.enemy.Rigidbody.OverrideFollowPosition(0.2f, 3f * sneak, -1f);
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
