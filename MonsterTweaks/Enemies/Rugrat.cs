using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// ラグラット(赤ちゃん。近くの貴重品を拾ってプレイヤーに投げつける)。クラス名は EnemyValuableThrower。
    /// - 投げる力: 本来は「物の重さ × 20(上限 100)」。係数と上限を変える
    /// - 射程: 本来は相手が見えていて 3〜5m のときに投げる(近すぎると下がり、遠いと近づく)。最短・最長を変える
    /// - 拾ってから投げるまで: 本来は相手を狙う状態に入ってから 2 秒たつまで投げない。この待ちを変える
    /// どれもホストが計算する(投げた物の物理はホストのもの)ので、MOD なしの人にも効く。
    /// </summary>
    internal static class Rugrat
    {
        private const string Section = "23. ラグラット";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ThrowForce;
        internal static ConfigEntry<float> ThrowForceMax;
        internal static ConfigEntry<float> RangeMin;
        internal static ConfigEntry<float> RangeMax;
        internal static ConfigEntry<float> ThrowDelay;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ラグラットの変更を使う");
            ThrowForce = Util.Float(cfg, Section, "投げる力", 40f, 5f, 200f,
                "投げる力の係数。物の重さ × この値で投げる(ゲーム本来は 20)");
            ThrowForceMax = Util.Float(cfg, Section, "投げる力の上限", 200f, 50f, 1000f,
                "重い物を投げるときの力の上限(ゲーム本来は 100)。係数を上げても重い物が上限で止まらないように");
            RangeMin = Util.Float(cfg, Section, "射程(最短)", 3f, 0.5f, 10f,
                "相手がこれより近いと、下がってから投げる(ゲーム本来は 3m)");
            RangeMax = Util.Float(cfg, Section, "射程(最長)", 10f, 3f, 30f,
                "相手がこれより遠いと、近づいてから投げる(ゲーム本来は 5m)。最短より小さいときは最短と同じ");
            ThrowDelay = Util.Float(cfg, Section, "拾ってから投げるまでの時間", 0.5f, 0f, 8f,
                "拾って相手を狙い始めてから、射程内なら投げ始めるまでの秒数(ゲーム本来は 2)");
        }

        public static float GetThrowForce() => Enabled.Value ? ThrowForce.Value : 20f;
        public static float GetThrowForceMax() => Enabled.Value ? ThrowForceMax.Value : 100f;
        public static float GetRangeMin() => Enabled.Value ? RangeMin.Value : 3f;
        public static float GetRangeMax() => Enabled.Value ? UnityEngine.Mathf.Max(RangeMin.Value, RangeMax.Value) : 5f;
        /// <summary>狙う状態の残り時間(最初 10 秒)がこの値以下になったら投げてよい。本来は 8(= 2 秒後)。</summary>
        public static float GetThrowAllowedAt() => Enabled.Value ? 10f - ThrowDelay.Value : 8f;

        /// <summary>
        /// 狙う状態: 「&lt; 3f」(近すぎ)を最短、「&gt; 5f」(遠すぎ)を最長、「&lt;= 8f」(投げてよい時刻)を差し替える。
        /// 3f は 2 回出てくる(1 回目が距離の判定、2 回目は下がる距離)ので 1 回目だけ。
        /// </summary>
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.StateTargetPlayer))]
        private static class TargetPlayerPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                const string where = "EnemyValuableThrower.StateTargetPlayer";
                var a = Transpile.ReplaceFloat(instructions, 3f, AccessTools.Method(typeof(Rugrat), nameof(GetRangeMin)), where + "(最短)", 1);
                var b = Transpile.ReplaceFloat(a, 5f, AccessTools.Method(typeof(Rugrat), nameof(GetRangeMax)), where + "(最長)");
                return Transpile.ReplaceFloat(b, 8f, AccessTools.Method(typeof(Rugrat), nameof(GetThrowAllowedAt)), where + "(投げるまで)");
            }
        }

        /// <summary>投げる: 「20f * 重さ」と「上限 100f」を差し替える。</summary>
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.Throw))]
        private static class ThrowPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                const string where = "EnemyValuableThrower.Throw";
                var a = Transpile.ReplaceFloat(instructions, 20f, AccessTools.Method(typeof(Rugrat), nameof(GetThrowForce)), where + "(力)");
                return Transpile.ReplaceFloat(a, 100f, AccessTools.Method(typeof(Rugrat), nameof(GetThrowForceMax)), where + "(上限)");
            }
        }
    }
}
