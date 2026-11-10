using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// ヘッドグラブ(小さい。倒れたプレイヤーの頭を被って持ち去る)。クラス名は EnemyHeadGrabber。ホストだけで動く。
    /// - マップ全域の頭を認識: 頭を探す GetClosestDeathHead の距離の上限(本来 10m / 気づいたとき 30m)を無制限にする。
    ///   さらにプレイヤーを追っている間も 1 秒ごとに頭を探す(本来は見回り中だけ)
    /// - 次の頭を探すまでの待ち時間(本来 120 秒)を設定できる
    /// - 速度は共通設定「移動速度倍率(共通)」(このセクション)で調整する
    /// </summary>
    internal static class HeadGrab
    {
        private const string Section = "10. ヘッドグラブ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> MapWide;
        internal static ConfigEntry<float> Cooldown;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ヘッドグラブの変更を使う");
            MapWide = cfg.Bind(Section, "マップ全域の頭を認識", true,
                "倒れたプレイヤーの頭がマップのどこにあっても、すぐに見つけて回収しに行く(プレイヤーを追っている間も探す)");
            Cooldown = Util.Float(cfg, Section, "次の頭を探すまでの時間", 5f, 0f, 120f,
                "頭を狙ったり離したりしたあと、次の頭を探すまでの秒数(ゲーム本来は 120)");
        }

        public static float GetCooldown() => Enabled.Value ? Cooldown.Value : 120f;

        [HarmonyPatch(typeof(EnemyHeadGrabber), nameof(EnemyHeadGrabber.GetClosestDeathHead))]
        private static class ClosestHeadPatch
        {
            private static void Prefix(ref float _distanceMax)
            {
                if (Enabled.Value && MapWide.Value)
                {
                    _distanceMax = float.MaxValue;
                }
            }
        }

        [HarmonyPatch(typeof(EnemyHeadGrabber), nameof(EnemyHeadGrabber.UpdateHeadTarget))]
        private static class TargetCooldownPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 120f, AccessTools.Method(typeof(HeadGrab), nameof(GetCooldown)), "EnemyHeadGrabber.UpdateHeadTarget");
            }
        }

        [HarmonyPatch(typeof(EnemyHeadGrabber), nameof(EnemyHeadGrabber.DeathHeadRelease))]
        private static class ReleaseCooldownPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 120f, AccessTools.Method(typeof(HeadGrab), nameof(GetCooldown)), "EnemyHeadGrabber.DeathHeadRelease");
            }
        }

        /// <summary>プレイヤーを追っている・届かない状態でも、1 秒ごとに頭を探す(本来は見回り系の状態だけ)。</summary>
        [HarmonyPatch(typeof(EnemyHeadGrabber), nameof(EnemyHeadGrabber.Update))]
        private static class ChaseSearchPatch
        {
            private static void Postfix(EnemyHeadGrabber __instance)
            {
                if (!Enabled.Value || !MapWide.Value || !Util.IsAuthority || __instance.headTargetActive)
                {
                    return;
                }
                var state = __instance.currentState;
                if (state == EnemyHeadGrabber.State.GotoPlayer || state == EnemyHeadGrabber.State.GotoPlayerOver || state == EnemyHeadGrabber.State.CantReach)
                {
                    __instance.NearbyHeadLogic();
                }
            }
        }
    }
}
