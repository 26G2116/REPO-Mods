using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// シェフ(カエルのシェフ。跳ねて近づき、構えてから突進してくる)。クラス名は EnemyTumbler。
    /// - 前隙: 見つけたときに止まる時間(Notice、本来 1 秒)と、突進前の構え(Tell、本来 2 秒)
    /// - 突進後の行動不能: 突進して止まってからの待ち(Tumble、本来 1 秒)と、起き上がり(TumbleEnd、本来 1 秒)
    /// どれも状態の長さをホストが決め、状態の切り替えは RPC で全員に伝わる(MOD なしの人にも効く)。
    /// </summary>
    internal static class Chef
    {
        private const string Section = "17. シェフ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> NoticeTime;
        internal static ConfigEntry<float> TellTime;
        internal static ConfigEntry<float> RecoveryTime;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "シェフの変更を使う");
            NoticeTime = Util.Float(cfg, Section, "見つけたときに止まる時間", 0.4f, 0.05f, 3f,
                "プレイヤーを見つけてから近づき始めるまでの秒数(ゲーム本来は 1)");
            TellTime = Util.Float(cfg, Section, "突進の構えの時間", 0.8f, 0.1f, 5f,
                "突進の前に構える秒数(前隙。ゲーム本来は 2)");
            RecoveryTime = Util.Float(cfg, Section, "突進後に動けない時間", 0.6f, 0.1f, 5f,
                "突進が止まってから動き出すまでの秒数(ゲーム本来は合計 2 秒。止まったあとの待ちと起き上がりに半分ずつ)");
        }

        /// <summary>状態に入った最初の更新で、その状態の長さ(stateTimer)を差し替える。</summary>
        private static void OverrideTimer(EnemyTumbler chef, bool impulse, float seconds, bool alreadyTicked)
        {
            if (!impulse || !Enabled.Value || !Util.IsAuthority)
            {
                return;
            }
            // 本来の処理はこの最初の更新でもう 1 フレーム分減らしていることがある
            chef.stateTimer = Mathf.Max(0.01f, seconds - (alreadyTicked ? Time.deltaTime : 0f));
        }

        [HarmonyPatch(typeof(EnemyTumbler), nameof(EnemyTumbler.StateNotice))]
        private static class NoticePatch
        {
            private static void Prefix(EnemyTumbler __instance, out bool __state) => __state = __instance.stateImpulse;
            private static void Postfix(EnemyTumbler __instance, bool __state) => OverrideTimer(__instance, __state, NoticeTime.Value, true);
        }

        [HarmonyPatch(typeof(EnemyTumbler), nameof(EnemyTumbler.StateTell))]
        private static class TellPatch
        {
            private static void Prefix(EnemyTumbler __instance, out bool __state) => __state = __instance.stateImpulse;
            private static void Postfix(EnemyTumbler __instance, bool __state) => OverrideTimer(__instance, __state, TellTime.Value, true);
        }

        /// <summary>突進: 速さが落ちてから(速さ 1 未満の間だけ)減る待ち時間。突進の始まりは速いので減っていない。</summary>
        [HarmonyPatch(typeof(EnemyTumbler), nameof(EnemyTumbler.StateTumble))]
        private static class TumblePatch
        {
            private static void Prefix(EnemyTumbler __instance, out bool __state) => __state = __instance.stateImpulse;
            private static void Postfix(EnemyTumbler __instance, bool __state) => OverrideTimer(__instance, __state, RecoveryTime.Value * 0.5f, false);
        }

        [HarmonyPatch(typeof(EnemyTumbler), nameof(EnemyTumbler.StateTumbleEnd))]
        private static class TumbleEndPatch
        {
            private static void Prefix(EnemyTumbler __instance, out bool __state) => __state = __instance.stateImpulse;
            private static void Postfix(EnemyTumbler __instance, bool __state) => OverrideTimer(__instance, __state, RecoveryTime.Value * 0.5f, true);
        }
    }
}
