using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ベル(三輪車の幽霊。行く手をふさがれるとベルで警告し、それでもどかないと突進する)。クラス名は EnemyTricycle。
    /// - 警告の回数: 本来はベル 1 回 → 2 回 → 3 回の 3 段階のあとに攻撃。設定した段階まで鳴らしたら攻撃に移る
    ///   (状態の切り替え SetState を差し替える。10 秒以内に鳴らしていれば警告済みとして、すぐ攻撃する)
    /// - 突進の続く時間: 攻撃の長さ(本来 5 秒。相手を見失っているときは 1〜2 秒と、短い突進の 3 秒)に倍率
    /// どれもホストが決めて、本来の RPC で全員に伝わる。
    /// </summary>
    internal static class Bell
    {
        private const string Section = "26. ベル";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Warnings;
        internal static ConfigEntry<float> AttackDuration;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ベルの変更を使う");
            Warnings = Util.Int(cfg, Section, "警告の回数", 1, 1, 3,
                "攻撃する前にベルで警告する段階の数(ゲーム本来は 3。1 なら 1 回鳴らしてどかなければ攻撃)");
            AttackDuration = Util.Float(cfg, Section, "突進の続く時間の倍率", 2f, 0.5f, 5f,
                "突進(攻撃)が続く時間の倍率(ゲーム本来は 5 秒)");
        }

        private class RingMemo
        {
            internal float Time = -100f;
        }

        private static readonly ConditionalWeakTable<EnemyTricycle, RingMemo> Rings = new ConditionalWeakTable<EnemyTricycle, RingMemo>();

        /// <summary>この秒数以内にベルを鳴らしていれば「警告済み」とみなす。</summary>
        private const float WarningMemory = 10f;

        [HarmonyPatch(typeof(EnemyTricycle), nameof(EnemyTricycle.DoBellRing))]
        private static class RingPatch
        {
            private static void Postfix(EnemyTricycle __instance)
            {
                Rings.GetOrCreateValue(__instance).Time = UnityEngine.Time.time;
            }
        }

        /// <summary>警告の段階を飛ばして、攻撃の前の状態へ移す。</summary>
        [HarmonyPatch(typeof(EnemyTricycle), nameof(EnemyTricycle.SetState))]
        private static class SetStatePatch
        {
            private static void Prefix(EnemyTricycle __instance, ref EnemyTricycle.State newState)
            {
                if (!Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                bool skip = (newState == EnemyTricycle.State.BellRing2 && Warnings.Value < 2)
                    || (newState == EnemyTricycle.State.BellRing3 && Warnings.Value < 3);
                if (!skip)
                {
                    return;
                }
                var cur = __instance.currentState;
                bool fromRing = cur == EnemyTricycle.State.BellRing || cur == EnemyTricycle.State.BellRing2 || cur == EnemyTricycle.State.BellRing3;
                bool rangRecently = Rings.TryGetValue(__instance, out var ring) && Time.time - ring.Time < WarningMemory;
                if (!fromRing && !rangRecently)
                {
                    // 叩かれた・つかまれたときなど、まだ一度も鳴らしていないのに段階が進む場合は、まず 1 回警告する
                    newState = EnemyTricycle.State.BellRing;
                    return;
                }
                // 本来 3 段階目の終わりと同じく、警告の回数を数え直してから攻撃の前の状態へ
                __instance.bellRingCount = 0;
                __instance.bellRingResetTimer = 0f;
                newState = EnemyTricycle.State.StateBeforeAttack;
                Plugin.Verbose("ベル: 警告を打ち切って攻撃します");
            }
        }

        /// <summary>攻撃の前の状態で決まる攻撃の長さ(attackTimerMax)に倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemyTricycle), nameof(EnemyTricycle.StateStateBeforeAttack))]
        private static class BeforeAttackPatch
        {
            private const float Unset = -12345f;

            private static void Prefix(EnemyTricycle __instance, out float __state)
            {
                __state = __instance.attackTimerMax;
                __instance.attackTimerMax = Unset;
            }

            private static void Postfix(EnemyTricycle __instance, float __state)
            {
                if (__instance.attackTimerMax == Unset)
                {
                    return; // この更新では決まらなかった(Finalizer で元に戻す)
                }
                if (Enabled.Value && Util.IsAuthority)
                {
                    __instance.attackTimerMax *= AttackDuration.Value;
                }
            }

            /// <summary>目印の値が残っていたら元に戻す(元の処理で例外が出ても必ず戻る)。</summary>
            private static void Finalizer(EnemyTricycle __instance, float __state)
            {
                if (__instance.attackTimerMax == Unset)
                {
                    __instance.attackTimerMax = __state;
                }
            }
        }

        /// <summary>相手を見失っているときの短い突進(本来 3 秒。attackTimerMax ではなく stateTimerMax で決まる)にも倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemyTricycle), nameof(EnemyTricycle.StateAttackShort))]
        private static class AttackShortPatch
        {
            private static void Prefix(EnemyTricycle __instance, out bool __state)
            {
                __state = __instance.stateStart;
            }

            private static void Postfix(EnemyTricycle __instance, bool __state)
            {
                if (__state && Enabled.Value && Util.IsAuthority && __instance.currentState == EnemyTricycle.State.AttackShort)
                {
                    __instance.stateTimerMax *= AttackDuration.Value;
                }
            }
        }
    }
}
