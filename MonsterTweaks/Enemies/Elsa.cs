using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;

namespace MonsterTweaks
{
    /// <summary>
    /// エルサ(小さな犬。なでないと怒りが溜まり、大きい姿に覚醒して襲ってくる)。
    /// - 覚醒までの時間: angryTimerMax(本来 15 秒。近寄って来ている間に溜まり、満タンで覚醒)
    /// - 覚醒している時間: chaseTimerMax(本来 10 秒。大きい姿で追いかける時間)
    /// - ターゲットが倒れたら、小さい姿に戻らずに一番近い生存者へ乗り換える
    /// </summary>
    internal static class Elsa
    {
        private const string Section = "1. エルサ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> AngryTime;
        internal static ConfigEntry<float> ChaseTime;
        internal static ConfigEntry<bool> Retarget;
        internal static ConfigEntry<float> RetargetRange;
        internal static ConfigEntry<bool> RetargetResetsChase;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "エルサの変更を使う");
            AngryTime = Util.Float(cfg, Section, "覚醒までの時間", 15f, 1f, 120f,
                "なでられないまま怒りが溜まって覚醒するまでの秒数(ゲーム本来は 15)");
            ChaseTime = Util.Float(cfg, Section, "覚醒している時間", 10f, 1f, 300f,
                "大きい姿で追いかけ続ける秒数(ゲーム本来は 10)");
            Retarget = cfg.Bind(Section, "倒したら次の人を狙う", true,
                "覚醒中にターゲットが倒れたら、小さい姿に戻らずに一番近い生存者を狙う");
            RetargetRange = Util.Float(cfg, Section, "次の人を探す範囲", 0f, 0f, 200f,
                "次のターゲットを探す距離(m)。0 なら距離の制限なし");
            RetargetResetsChase = cfg.Bind(Section, "乗り換えで覚醒時間をリセット", false,
                "次の人を狙うとき、覚醒している時間を最初から数え直す");
        }

        private static bool IsBigChaseState(EnemyElsa.State state)
        {
            switch (state)
            {
                case EnemyElsa.State.GoToPlayerBig:
                case EnemyElsa.State.GoToPlayerOverBig:
                case EnemyElsa.State.BackToNavMeshBig:
                case EnemyElsa.State.LookUnderStartBig:
                case EnemyElsa.State.LookUnderBig:
                case EnemyElsa.State.LookUnderStopBig:
                    return true;
                default:
                    return false;
            }
        }

        [HarmonyPatch(typeof(EnemyElsa), nameof(EnemyElsa.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyElsa __instance)
            {
                if (!Enabled.Value)
                {
                    return;
                }
                // 時間の上限は全員の画面の演出(目の光り方など)にも使われるので、毎フレーム設定値に合わせる
                __instance.angryTimerMax = AngryTime.Value;
                __instance.chaseTimerMax = ChaseTime.Value;

                if (!Retarget.Value || !Util.IsAuthority || !IsBigChaseState(__instance.currentState))
                {
                    return;
                }
                var target = __instance.playerTarget;
                if (target && !target.isDisabled)
                {
                    return;
                }
                // 本来はここで ChaseStop が小さい姿に戻すので、その前に次のターゲットを決める
                var next = Util.NearestAlivePlayer(__instance.enemy.Rigidbody.transform.position, RetargetRange.Value, target);
                if (!next)
                {
                    return;
                }
                __instance.playerTarget = next;
                if (SemiFunc.IsMultiplayer())
                {
                    __instance.photonView.RPC("UpdatePlayerTargetRPC", RpcTarget.All, next.photonView.ViewID);
                }
                if (RetargetResetsChase.Value)
                {
                    __instance.chaseTimer = 0f;
                }
                Plugin.Verbose($"エルサ: ターゲットを {next.playerName} に乗り換えました");
            }
        }
    }
}
