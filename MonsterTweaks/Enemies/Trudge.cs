using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// トラッジ(赤と黒の大型。こん棒を振り下ろして周りを吸い込む)。
    /// - 移動速度: 追いかけるとき(本来 0.8)と、うろつくとき(ナビメッシュの既定速度)に倍率
    /// - 前隙: 攻撃開始から振り下ろし(AttackStart のアニメイベント)までのアニメ速度。
    ///   攻撃の発生はホストのアニメイベントで決まり RPC で全員に伝わるので、ホストだけの導入でも効く
    /// - 後隙: 攻撃状態の長さ(本来 5 秒。終わると次の行動に移る)
    /// - 攻撃を始める距離(本来 8m)
    /// </summary>
    internal static class Trudge
    {
        private const string Section = "3. トラッジ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ChaseSpeed;
        internal static ConfigEntry<float> RoamSpeed;
        internal static ConfigEntry<float> WindupSpeed;
        internal static ConfigEntry<float> RecoverySpeed;
        internal static ConfigEntry<float> AttackDuration;
        internal static ConfigEntry<float> AttackTriggerDistance;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "トラッジの変更を使う");
            ChaseSpeed = Util.Float(cfg, Section, "追いかけるときの速度倍率", 1f, 0.2f, 6f,
                "プレイヤーに向かって歩くときの速さの倍率(1 でゲーム本来と同じ)");
            RoamSpeed = Util.Float(cfg, Section, "うろつくときの速度倍率", 1f, 0.2f, 6f,
                "見回り・移動のときの速さの倍率(1 でゲーム本来と同じ)");
            WindupSpeed = Util.Float(cfg, Section, "前隙の速さ", 1f, 0.25f, 4f,
                "攻撃を始めてから振り下ろすまでのモーションの速さ。2 なら前隙が半分、0.5 なら 2 倍");
            RecoverySpeed = Util.Float(cfg, Section, "振り下ろし後のモーションの速さ", 1f, 0.25f, 4f,
                "振り下ろしたあとのモーションの速さ(見た目。動き出すまでの時間は「攻撃後に動き出すまでの時間」で決まる)");
            AttackDuration = Util.Float(cfg, Section, "攻撃後に動き出すまでの時間", 5f, 0.5f, 15f,
                "攻撃を始めてから次の行動に移るまでの秒数(前隙を含む。ゲーム本来は 5)。短いほど後隙が減る。前隙より短くしても、振り下ろすまでは動き出さない");
            AttackTriggerDistance = Util.Float(cfg, Section, "攻撃を始める距離", 8f, 1f, 30f,
                "プレイヤーにこの距離(m)まで近づいたら攻撃を始める(ゲーム本来は 8)");
        }

        public static float GetChaseSpeed() => Enabled.Value ? 0.8f * ChaseSpeed.Value : 0.8f;
        public static float GetAttackDuration() => Enabled.Value ? AttackDuration.Value : 5f;
        public static float GetAttackTriggerDistance() => Enabled.Value ? AttackTriggerDistance.Value : 8f;

        // ---- 追いかける速度・攻撃を始める距離・攻撃の長さ(定数の差し替え) ----

        [HarmonyPatch(typeof(EnemySlowWalker), nameof(EnemySlowWalker.StateGoToPlayer))]
        private static class GoToPlayerPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var speed = Transpile.ReplaceFloat(instructions, 0.8f, AccessTools.Method(typeof(Trudge), nameof(GetChaseSpeed)), "EnemySlowWalker.StateGoToPlayer(速度)");
                return Transpile.ReplaceFloat(speed, 8f, AccessTools.Method(typeof(Trudge), nameof(GetAttackTriggerDistance)), "EnemySlowWalker.StateGoToPlayer(攻撃距離)");
            }
        }

        [HarmonyPatch(typeof(EnemySlowWalker), nameof(EnemySlowWalker.StateSneak))]
        private static class SneakPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 0.8f, AccessTools.Method(typeof(Trudge), nameof(GetChaseSpeed)), "EnemySlowWalker.StateSneak");
            }
        }

        [HarmonyPatch(typeof(EnemySlowWalker), nameof(EnemySlowWalker.StateAttack))]
        private static class AttackPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 5f, AccessTools.Method(typeof(Trudge), nameof(GetAttackDuration)), "EnemySlowWalker.StateAttack");
            }

            /// <summary>
            /// 「攻撃後に動き出すまでの時間」を前隙より短くすると、振り下ろす前に攻撃の状態が終わり、歩き出して攻撃が出なくなる。
            /// 振り下ろし(AttackStart)が済むまでは攻撃の状態を続ける(念のため最長 8 秒で打ち切る)。
            /// </summary>
            private static void Prefix(EnemySlowWalker __instance)
            {
                if (!Enabled.Value || !Util.IsAuthority || !__instance.animator)
                {
                    return;
                }
                if (__instance.stateImpulse)
                {
                    AttackStarted[__instance] = Time.time;
                    return;
                }
                if (Swung.Contains(__instance.animator) || __instance.stateTimer - Time.deltaTime > 0f)
                {
                    return;
                }
                if (AttackStarted.TryGetValue(__instance, out var started) && Time.time - started < 8f)
                {
                    __instance.stateTimer = Time.deltaTime + 0.05f;
                }
            }
        }

        /// <summary>攻撃の状態に入った時刻。</summary>
        private static readonly Dictionary<EnemySlowWalker, float> AttackStarted = new Dictionary<EnemySlowWalker, float>();

        // ---- うろつく速度: ナビメッシュの既定速度への追加の倍率として EnemyCommon に渡す ----

        internal static void RegisterRoamSpeed()
        {
            var previous = EnemyCommon.ExtraDefaultSpeed;
            EnemyCommon.ExtraDefaultSpeed = enemy =>
            {
                float value = previous(enemy);
                if (Enabled.Value && enemy && EnemyCommon.For(enemy)?.Type == typeof(EnemySlowWalker))
                {
                    value *= RoamSpeed.Value;
                }
                return value;
            };
        }

        // ---- 前隙・振り下ろし後のモーション速度 ----

        /// <summary>今の攻撃で振り下ろし(AttackStart)が済んだアニメ。</summary>
        private static readonly HashSet<EnemySlowWalkerAnim> Swung = new HashSet<EnemySlowWalkerAnim>();

        [HarmonyPatch(typeof(EnemySlowWalkerAnim), nameof(EnemySlowWalkerAnim.Update))]
        private static class AnimUpdatePatch
        {
            private static void Postfix(EnemySlowWalkerAnim __instance)
            {
                bool attacking = __instance.controller && __instance.controller.currentState == EnemySlowWalker.State.Attack;
                if (!attacking)
                {
                    Swung.Remove(__instance);
                    return;
                }
                // 本来の処理で止められている(凍結中)ときは触らない
                if (!Enabled.Value || __instance.animator.speed == 0f)
                {
                    return;
                }
                __instance.animator.speed = Swung.Contains(__instance) ? RecoverySpeed.Value : WindupSpeed.Value;
            }
        }

        [HarmonyPatch(typeof(EnemySlowWalkerAnim), nameof(EnemySlowWalkerAnim.AttackStart))]
        private static class AttackStartPatch
        {
            private static void Prefix(EnemySlowWalkerAnim __instance)
            {
                Swung.Add(__instance);
            }
        }
    }
}
