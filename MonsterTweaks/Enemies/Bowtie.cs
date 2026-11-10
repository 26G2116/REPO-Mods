using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ボウタイ(白い体に蝶ネクタイ。叫んで強い風を吹きつける)。クラス名は EnemyBowtie。
    /// - 叫び(Yell、本来 5 秒)の時間を「狙って吹く時間 + 回転する時間」に延ばし、
    ///   後半は風を吹いたままその場で 360° 回転して全方向に風をまき散らす。
    ///   体の向きはホストが動かし、体(リジッドボディ)の同期で全員の画面でも回る。
    ///   風の当たり判定は体についているので、MOD なしの人にも回転した風が当たる
    /// </summary>
    internal static class Bowtie
    {
        private const string Section = "16. ボウタイ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> AimTime;
        internal static ConfigEntry<float> SpinTime;
        internal static ConfigEntry<float> SpinTurns;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ボウタイの変更を使う");
            AimTime = Util.Float(cfg, Section, "狙って風を吹く時間", 5f, 0.5f, 15f,
                "叫び始めてから、狙った相手に向かって風を吹く秒数(ゲーム本来は 5。この時間で叫びが終わる)");
            SpinTime = Util.Float(cfg, Section, "回転しながら風を吹く時間", 3f, 0f, 15f,
                "狙って吹いたあと、風を吹いたまま回転する秒数(0 で回転しない)");
            SpinTurns = Util.Float(cfg, Section, "回転する回数", 1f, 0.25f, 5f,
                "回転する時間のあいだに何周するか(1 で 360°)");
        }

        private class SpinMemo
        {
            internal bool Armed;
            internal bool Started;
            internal float SpinTime;
            internal float Turns;
            internal float StartYaw;
        }

        private static readonly ConditionalWeakTable<EnemyBowtie, SpinMemo> Memos = new ConditionalWeakTable<EnemyBowtie, SpinMemo>();

        private static bool Spinning(EnemyBowtie bowtie, out SpinMemo memo)
        {
            memo = null;
            // 叫びに入った直後(stateImpulse)は前回の叫びの設定が残っているので回さない
            return bowtie.currentState == EnemyBowtie.State.Yell && !bowtie.stateImpulse
                && Memos.TryGetValue(bowtie, out memo) && memo.Armed
                && bowtie.stateTimer <= memo.SpinTime;
        }

        /// <summary>叫びの始まりで、叫びの長さを「狙う時間 + 回転する時間」にする。</summary>
        [HarmonyPatch(typeof(EnemyBowtie), nameof(EnemyBowtie.StateYell))]
        private static class YellPatch
        {
            private static void Prefix(EnemyBowtie __instance, out bool __state)
            {
                __state = __instance.stateImpulse;
            }

            private static void Postfix(EnemyBowtie __instance, bool __state)
            {
                if (!__state || !Util.IsAuthority)
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                memo.Armed = Enabled.Value && SpinTime.Value > 0f;
                memo.Started = false;
                if (!Enabled.Value)
                {
                    return;
                }
                memo.SpinTime = SpinTime.Value;
                memo.Turns = SpinTurns.Value;
                __instance.stateTimer = AimTime.Value + (memo.Armed ? memo.SpinTime : 0f);
            }
        }

        /// <summary>回転中は、向きを相手ではなく回転の角度にする。</summary>
        [HarmonyPatch(typeof(EnemyBowtie), nameof(EnemyBowtie.HorizontalRotationLogic))]
        private static class HorizontalPatch
        {
            private static bool Prefix(EnemyBowtie __instance)
            {
                if (!Spinning(__instance, out var memo))
                {
                    return true;
                }
                if (!memo.Started)
                {
                    memo.Started = true;
                    memo.StartYaw = __instance.transform.eulerAngles.y;
                    Plugin.Verbose("ボウタイ: 回転しながら風を吹きます");
                }
                float progress = memo.SpinTime > 0f ? Mathf.Clamp01(1f - __instance.stateTimer / memo.SpinTime) : 1f;
                var rotation = Quaternion.Euler(0f, memo.StartYaw + 360f * memo.Turns * progress, 0f);
                // ばねは 180° を超えると逆回りするので使わず、向きを直接決める(ばねの状態も合わせて、回転後に戻らないようにする)
                __instance.horizontalRotationTarget = rotation;
                var spring = __instance.horizontalRotationSpring;
                if (spring != null)
                {
                    spring.lastRotation = rotation;
                    spring.springVelocity = Vector3.zero;
                }
                __instance.transform.rotation = rotation;
                return false;
            }
        }

        /// <summary>回転中は、上下の向きを水平に戻す(相手の方を見上げたまま回らない)。</summary>
        [HarmonyPatch(typeof(EnemyBowtie), nameof(EnemyBowtie.VerticalRotationLogic))]
        private static class VerticalPatch
        {
            private static bool Prefix(EnemyBowtie __instance)
            {
                if (!Spinning(__instance, out _))
                {
                    return true;
                }
                __instance.verticalRotationTransform.localRotation =
                    SemiFunc.SpringQuaternionGet(__instance.verticalRotationSpring, Quaternion.identity, -1f);
                return false;
            }
        }
    }
}
