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
        internal static ConfigEntry<bool> UseEgg;
        internal static ConfigEntry<int> EggCount;
        internal static ConfigEntry<float> EggSpread;
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
            UseEgg = cfg.Bind(Section, "人類美術館の卵の爆発を使う", true,
                "自爆に、人類美術館の緑の卵(約 $4000)の爆発をそのまま使う。MOD なしの参加者にも爆発が見え、ダメージもゲーム本来どおり(1 個でほぼ即死級)。" +
                "OFF なら下の「MOD の爆発」の設定を使う");
            EggCount = Util.Int(cfg, Section, "卵の数", 3, 1, 8, "自爆の位置に並べて爆発させる卵の数(多いほど広範囲)");
            EggSpread = Util.Float(cfg, Section, "卵を並べる半径", 2.5f, 0f, 10f, "卵を 2 個以上にしたとき、ひよこの周りに円形に並べる半径(m)");
            Damage = Util.Int(cfg, Section, "MOD の爆発のダメージ", 50, 0, 200, "卵を使わないとき、範囲内のプレイヤーが受けるダメージ(体力は通常 100)");
            Radius = Util.Float(cfg, Section, "MOD の爆発の範囲", 8f, 1f, 30f, "卵を使わないとき、ダメージが届く距離(m)");
            ThroughWalls = cfg.Bind(Section, "MOD の爆発は壁越しにも当たる", false, "卵を使わないとき、壁の向こうのプレイヤーにもダメージを与える");
            VisualSize = Util.Float(cfg, Section, "MOD の爆発の見た目の大きさ", 1.5f, 0.5f, 4f,
                "卵を使わないときの爆発エフェクトの大きさ(敵「バン」の大爆発が 1。MOD なしの参加者には見えない)");
            DespawnAfter = cfg.Bind(Section, "自爆したら消える", true,
                "自爆したあと姿を消す(しばらくすると通常どおり再出現する)。OFF なら小さい姿に戻って残る");
        }

        // ---- 本来の定数の代わりに呼ばれる値(無効のときはゲーム本来の値を返す) ----
        public static float GetChaseTime() => Enabled.Value ? ChaseTime.Value : 10f;
        public static float GetTowardsSpeed() => Enabled.Value ? 5f * SpeedMultiplier.Value : 5f;

        /// <summary>
        /// 人類美術館の卵を通信つきで生成し、すぐ「爆発」状態にする。卵は全員の PC で本来どおり爆発する
        /// (見た目・音・ダメージの計算はゲーム本来の処理)。卵が見つからなければ false。
        /// </summary>
        private static bool EggExplosion(UnityEngine.Vector3 center)
        {
            var egg = VanillaAssets.Egg;
            if (egg == null)
            {
                return false;
            }
            int count = EggCount.Value;
            bool spawned = false;
            for (int i = 0; i < count; i++)
            {
                var position = count == 1
                    ? center
                    : center + UnityEngine.Quaternion.Euler(0f, 360f * i / count, 0f) * UnityEngine.Vector3.forward * EggSpread.Value;
                var obj = VanillaAssets.Spawn(egg, position, UnityEngine.Quaternion.identity);
                if (!obj)
                {
                    continue;
                }
                spawned = true;
                if (SemiFunc.IsMultiplayer())
                {
                    SpawnedEggs.Add(obj.GetComponent<Photon.Pun.PhotonView>().ViewID);
                    // ValuableEgg の photonView は Start で入るので、生成直後は PhotonView を直接使う
                    obj.GetComponent<Photon.Pun.PhotonView>().RPC("SetStateRPC", Photon.Pun.RpcTarget.All, ValuableEgg.EggState.Explode);
                }
                else
                {
                    obj.GetComponent<ValuableEgg>().SetStateRPC(ValuableEgg.EggState.Explode);
                }
            }
            return spawned;
        }

        /// <summary>この MOD が生成した卵(PhotonView の ID)。</summary>
        private static readonly HashSet<int> SpawnedEggs = new HashSet<int>();

        /// <summary>
        /// 卵は爆発すると各自の PC で Object.Destroy されるだけなので、部屋の生成記録が残り、
        /// 途中参加者の画面に割れていない卵が出てしまう。ホストで PhotonNetwork.Destroy して記録ごと消す。
        /// </summary>
        [HarmonyPatch(typeof(ValuableEgg), nameof(ValuableEgg.Explode))]
        private static class EggExplodePatch
        {
            private static void Postfix(ValuableEgg __instance)
            {
                if (!SemiFunc.IsMultiplayer() || !Photon.Pun.PhotonNetwork.IsMasterClient)
                {
                    return;
                }
                var view = __instance.GetComponent<Photon.Pun.PhotonView>();
                if (view && SpawnedEggs.Remove(view.ViewID))
                {
                    Photon.Pun.PhotonNetwork.Destroy(__instance.gameObject);
                }
            }
        }

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
                if (!UseEgg.Value || !EggExplosion(center))
                {
                    Boom.Explode(center, VisualSize.Value, Radius.Value, Damage.Value, ThroughWalls.Value, enemy);
                }
                Plugin.Verbose($"APEX プレデター: 自爆しました(相手が倒れた: {targetLost})");
                if (DespawnAfter.Value && enemy.EnemyParent)
                {
                    enemy.EnemyParent.Despawn();
                }
            }
        }
    }
}
