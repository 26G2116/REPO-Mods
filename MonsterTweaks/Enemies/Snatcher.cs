using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// スナッチャー(ポニー。舌でプレイヤーや物を捕まえる馬)。クラス名は EnemySnatcher。
    /// - 次の攻撃までの間:
    ///   ・舌の攻撃全体の待ち(MainAttackCooldownSet。攻撃が終わったあとの 3 秒、後ろ蹴りのあとの 12 秒)
    ///   ・攻撃した相手ごとの待ち(PlayerAttackCooldownSet の playerAttackCooldownMin〜Max、本来 2〜3 秒)
    ///   に倍率をかける
    /// - 狙いを変えたあとの待ち: 狙いを変える(Retarget)ときに前の相手へ付ける待ち
    ///   (本来は mainAttackCooldown と tongueRetargetCooldown の大きいほう、どちらも 12 秒)に倍率
    /// - 攻撃の合間の休み: 攻撃が終わったあとの休み(本来 3〜6 秒。ゲーム側で 3〜6 秒に制限されてから決まるので、決まった値に倍率)
    /// 一時的に書き換えた値は Finalizer で必ず戻す(元の処理で例外が出ても、縮んだままにならない)。
    /// ホストが決めるので全員に効く。
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
                "舌を外し続けて狙いを変えたとき、前の相手をまた狙えるまでの時間の倍率(ゲーム本来は 12 秒。0.33 で約 4 秒)。" +
                "その相手にもっと長い待ちが残っていれば、そちらが優先される");
            AttackCooldown = Util.Float(cfg, Section, "次の攻撃までの間の倍率", 1f / 3f, 0.05f, 3f,
                "次の舌の攻撃までの待ちの倍率。攻撃が終わったあと(本来 3 秒)、後ろ蹴りのあと(本来 12 秒)、" +
                "同じ相手への攻撃(本来 2〜3 秒)にかかる。0.33 でそれぞれ約 1 秒・4 秒・0.7〜1 秒");
            AttackPause = Util.Float(cfg, Section, "攻撃の合間の休みの倍率", 1f / 3f, 0.05f, 3f,
                "攻撃が終わってから走り回って休む時間の倍率(ゲーム本来は 3〜6 秒。0.33 で 1〜2 秒)");
        }

        private static bool Active => Enabled.Value && Util.IsAuthority;

        // ---------------- 舌の攻撃全体の待ち ----------------

        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.MainAttackCooldownSet))]
        private static class MainCooldownPatch
        {
            private static void Prefix(ref float _time)
            {
                if (Active)
                {
                    _time *= AttackCooldown.Value;
                }
            }
        }

        // ---------------- 狙いを変えたあとの待ち ----------------

        /// <summary>狙いを変える処理の間だけ、待ちの元になる 2 つの値に倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.StateRetarget))]
        private static class RetargetPatch
        {
            private static void Prefix(EnemySnatcher __instance, out (float main, float retarget, bool applied) __state)
            {
                __state = (__instance.mainAttackCooldown, __instance.tongueRetargetCooldown, false);
                if (!Active)
                {
                    return;
                }
                __instance.mainAttackCooldown *= RetargetCooldown.Value;
                __instance.tongueRetargetCooldown *= RetargetCooldown.Value;
                __state.applied = true;
            }

            /// <summary>例外が出ても必ず元に戻す。</summary>
            private static void Finalizer(EnemySnatcher __instance, (float main, float retarget, bool applied) __state)
            {
                if (__state.applied)
                {
                    __instance.mainAttackCooldown = __state.main;
                    __instance.tongueRetargetCooldown = __state.retarget;
                }
            }
        }

        // ---------------- 相手ごとの攻撃の待ち ----------------

        /// <summary>相手ごとの攻撃の待ちを決める処理の間だけ、2〜3 秒の範囲に倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.PlayerAttackCooldownSet))]
        private static class AttackCooldownPatch
        {
            private static void Prefix(EnemySnatcher __instance, out (float min, float max, bool applied) __state)
            {
                __state = (__instance.playerAttackCooldownMin, __instance.playerAttackCooldownMax, false);
                if (!Active)
                {
                    return;
                }
                __instance.playerAttackCooldownMin *= AttackCooldown.Value;
                __instance.playerAttackCooldownMax *= AttackCooldown.Value;
                __state.applied = true;
            }

            /// <summary>例外が出ても必ず元に戻す。</summary>
            private static void Finalizer(EnemySnatcher __instance, (float min, float max, bool applied) __state)
            {
                if (__state.applied)
                {
                    __instance.playerAttackCooldownMin = __state.min;
                    __instance.playerAttackCooldownMax = __state.max;
                }
            }
        }

        // ---------------- 攻撃の合間の休み ----------------

        /// <summary>
        /// 休みはゲーム側で 3〜6 秒に制限されてから決まるので、決まった直後の値に倍率をかける。
        /// 休みのタイマーは別の処理で時間とともに減るだけなので、この呼び出しの前後で値が変わっていれば新しく決まったとわかる
        /// (ゲーム側の条件を書き写さないので、ゲームの更新で条件が変わっても、減っている途中のタイマーを縮め続けることはない)。
        /// </summary>
        [HarmonyPatch(typeof(EnemySnatcher), nameof(EnemySnatcher.AttackPauseTransition))]
        private static class AttackPausePatch
        {
            private static void Prefix(EnemySnatcher __instance, out float __state)
            {
                __state = __instance.attackPauseTimer;
            }

            private static void Postfix(EnemySnatcher __instance, float __state)
            {
                if (__instance.attackPauseTimer != __state && __instance.attackPauseTimer > 0f && Active)
                {
                    __instance.attackPauseTimer *= AttackPause.Value;
                }
            }
        }
    }
}
