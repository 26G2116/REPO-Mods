using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// スナッチャー(ポニー。舌でプレイヤーや物を捕まえる馬)。クラス名は EnemySnatcher。
    /// - 狙いを変えたあとの待ち: 狙いを変える(Retarget)ときに前の相手へ付ける待ち
    ///   (本来は mainAttackCooldown と tongueRetargetCooldown の大きいほう、どちらも 12 秒)に倍率
    /// - 次の攻撃までの間: 攻撃した相手ごとの待ち(playerAttackCooldownMin〜Max、本来 2〜3 秒)に倍率
    /// - 攻撃の合間の休み: 攻撃が終わったあとの休み(本来 3〜6 秒。ゲーム側で 3〜6 秒に制限されているので、決まった値に倍率)
    /// 値はその処理の間だけ一時的に書き換える(ゲーム中に設定を変えてもすぐ反映)。ホストが決めるので全員に効く。
    /// </summary>
    internal static class Snatcher
    {
        private const string Section = "27. スナッチャー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RetargetCooldown;
        internal static ConfigEntry<float> AttackCooldown;
        internal static ConfigEntry<float> AttackPause;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "スナッチャー(ポニー)の変更を使う");
            RetargetCooldown = Util.Float(cfg, Section, "狙いを変えたあとの待ちの倍率", 1f / 3f, 0.05f, 3f,
                "舌を外し続けて狙いを変えたとき、前の相手をまた狙えるまでの時間の倍率(ゲーム本来は 12 秒。0.33 で約 4 秒)");
            AttackCooldown = Util.Float(cfg, Section, "次の攻撃までの間の倍率", 1f / 3f, 0.05f, 3f,
                "攻撃した相手を次に攻撃できるまでの時間の倍率(ゲーム本来は 2〜3 秒。0.33 で約 0.7〜1 秒)");
            AttackPause = Util.Float(cfg, Section, "攻撃の合間の休みの倍率", 1f / 3f, 0.05f, 3f,
                "攻撃が終わってから次の攻撃に移るまでの休みの倍率(ゲーム本来は 3〜6 秒。0.33 で 1〜2 秒)");
        }

        /// <summary>狙いを変える処理の間だけ、待ちの元になる 2 つの値に倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.StateRetarget))]
        private static class RetargetPatch
        {
            private static void Prefix(EnemySnatcher __instance, out (float main, float retarget, bool applied) __state)
            {
                __state = (__instance.mainAttackCooldown, __instance.tongueRetargetCooldown, false);
                if (!Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                __instance.mainAttackCooldown *= RetargetCooldown.Value;
                __instance.tongueRetargetCooldown *= RetargetCooldown.Value;
                __state.applied = true;
            }

            private static void Postfix(EnemySnatcher __instance, (float main, float retarget, bool applied) __state)
            {
                if (__state.applied)
                {
                    __instance.mainAttackCooldown = __state.main;
                    __instance.tongueRetargetCooldown = __state.retarget;
                }
            }
        }

        /// <summary>相手ごとの攻撃の待ちを決める処理の間だけ、2〜3 秒の範囲に倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.PlayerAttackCooldownSet))]
        private static class AttackCooldownPatch
        {
            private static void Prefix(EnemySnatcher __instance, out (float min, float max, bool applied) __state)
            {
                __state = (__instance.playerAttackCooldownMin, __instance.playerAttackCooldownMax, false);
                if (!Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                __instance.playerAttackCooldownMin *= AttackCooldown.Value;
                __instance.playerAttackCooldownMax *= AttackCooldown.Value;
                __state.applied = true;
            }

            private static void Postfix(EnemySnatcher __instance, (float min, float max, bool applied) __state)
            {
                if (__state.applied)
                {
                    __instance.playerAttackCooldownMin = __state.min;
                    __instance.playerAttackCooldownMax = __state.max;
                }
            }
        }

        /// <summary>攻撃の合間の休み: ゲーム側で 3〜6 秒に制限されてから決まるので、決まった直後の値に倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.AttackPauseTransition))]
        private static class AttackPausePatch
        {
            private static void Prefix(EnemySnatcher __instance, EnemySnatcher.State _state, out bool __state)
            {
                // 本来の処理と同じ条件(攻撃の状態から攻撃以外の状態へ移るとき)で休みが決まる
                __state = __instance.AttackStateActive(__instance.currentState) && !__instance.AttackStateActive(_state);
            }

            private static void Postfix(EnemySnatcher __instance, bool __state)
            {
                if (__state && Enabled.Value && Util.IsAuthority)
                {
                    __instance.attackPauseTimer *= AttackPause.Value;
                    Plugin.Verbose($"スナッチャー: 攻撃の合間の休み {__instance.attackPauseTimer:0.0} 秒");
                }
            }
        }
    }
}
