using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// APEX プレデター(ひよこ。触ると怒って大きくなり、追いかけてくる)。
    /// - 怒っている間の移動速度: ナビメッシュで追う速度(本来 10)と、まっすぐ飛んでくる速度(本来 5)に倍率をかける
    /// - 怒っている時間: ChaseStop の 10 秒
    /// - 怒りが終わって小さい姿に戻る瞬間に、広い範囲で自爆する
    /// </summary>
    internal static class ApexPredator
    {
        private const string Section = "2. APEX プレデター";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> SpeedMultiplier;
        internal static ConfigEntry<float> ChaseTime;
        internal static ConfigEntry<bool> SelfDestruct;
        internal static ConfigEntry<bool> SelfDestructOnTargetLost;
        internal static ConfigEntry<int> Damage;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<bool> ThroughWalls;
        internal static ConfigEntry<float> VisualSize;
        internal static ConfigEntry<bool> DespawnAfter;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "APEX プレデターの変更を使う");
            SpeedMultiplier = Util.Float(cfg, Section, "怒っているときの速度倍率", 1f, 0.2f, 5f,
                "怒って追いかけてくるときの移動速度の倍率(1 でゲーム本来と同じ)");
            ChaseTime = Util.Float(cfg, Section, "怒っている時間", 10f, 1f, 120f,
                "怒って追いかけ続ける秒数(ゲーム本来は 10)");
            SelfDestruct = cfg.Bind(Section, "怒りが終わったら自爆", true,
                "怒りが終わって小さい姿に戻るときに自爆する");
            SelfDestructOnTargetLost = cfg.Bind(Section, "相手が倒れて終わったときも自爆", true,
                "追いかけていた相手が倒れたせいで怒りが終わったときも自爆する");
            Damage = Util.Int(cfg, Section, "自爆のダメージ", 50, 0, 200, "範囲内のプレイヤーが受けるダメージ(体力は通常 100)");
            Radius = Util.Float(cfg, Section, "自爆の範囲", 8f, 1f, 30f, "ダメージが届く距離(m)");
            ThroughWalls = cfg.Bind(Section, "壁越しにも当たる", false, "壁の向こうのプレイヤーにもダメージを与える");
            VisualSize = Util.Float(cfg, Section, "爆発の見た目の大きさ", 1.5f, 0.5f, 4f,
                "爆発エフェクトの大きさ(敵「バン」の大爆発が 1)");
            DespawnAfter = cfg.Bind(Section, "自爆したら消える", true,
                "自爆したあと姿を消す(しばらくすると通常どおり再出現する)。OFF なら小さい姿に戻って残る");
        }

        // ---- 本来の定数の代わりに呼ばれる値(無効のときはゲーム本来の値を返す) ----
        public static float GetChaseTime() => Enabled.Value ? ChaseTime.Value : 10f;
        public static float GetTowardsSpeed() => Enabled.Value ? 5f * SpeedMultiplier.Value : 5f;

        private static bool IsChaseState(EnemyDuck.State state)
        {
            return state == EnemyDuck.State.ChaseNavmesh || state == EnemyDuck.State.ChaseTowards || state == EnemyDuck.State.ChaseMoveBack;
        }

        [HarmonyPatch(typeof(EnemyDuck), nameof(EnemyDuck.ChaseStop))]
        private static class ChaseStopPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 10f, AccessTools.Method(typeof(ApexPredator), nameof(GetChaseTime)), "EnemyDuck.ChaseStop");
            }
        }

        [HarmonyPatch(typeof(EnemyDuck), nameof(EnemyDuck.StateChaseTowards))]
        private static class ChaseTowardsPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 5f, AccessTools.Method(typeof(ApexPredator), nameof(GetTowardsSpeed)), "EnemyDuck.StateChaseTowards");
            }
        }

        [HarmonyPatch(typeof(EnemyDuck), nameof(EnemyDuck.StateChaseNavmesh))]
        private static class ChaseNavmeshPatch
        {
            private static void Postfix(EnemyDuck __instance)
            {
                if (!Enabled.Value || __instance.currentState != EnemyDuck.State.ChaseNavmesh)
                {
                    return;
                }
                float speed = 10f * SpeedMultiplier.Value;
                __instance.enemy.NavMeshAgent.OverrideAgent(speed, speed, 0.1f);
            }
        }

        [HarmonyPatch(typeof(EnemyDuck), nameof(EnemyDuck.UpdateState))]
        private static class UpdateStatePatch
        {
            private static void Prefix(EnemyDuck __instance, EnemyDuck.State _state)
            {
                if (!Enabled.Value || !SelfDestruct.Value || !Util.IsAuthority)
                {
                    return;
                }
                if (_state != EnemyDuck.State.DeTransform || !IsChaseState(__instance.currentState))
                {
                    return;
                }
                var target = __instance.playerTarget;
                bool targetLost = !target || target.isDisabled;
                if (targetLost && !SelfDestructOnTargetLost.Value)
                {
                    return;
                }
                var enemy = __instance.enemy;
                var center = enemy.CenterTransform ? enemy.CenterTransform.position : __instance.transform.position;
                Boom.Explode(center, VisualSize.Value, Radius.Value, Damage.Value, ThroughWalls.Value, enemy);
                Plugin.Verbose($"APEX プレデター: 自爆しました(相手が倒れた: {targetLost})");
                if (DespawnAfter.Value && enemy.EnemyParent)
                {
                    enemy.EnemyParent.Despawn();
                }
            }
        }
    }
}
