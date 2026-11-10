using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ウーグリー(緑色の浮いているもの。天井を漂い、テレポートで移動する)。クラス名は EnemyOogly。
    /// - テレポートの間隔: 天井をうろつく時間(本来、近くにプレイヤーがいると 60〜120 秒)を短くする
    /// - テレポート先: 範囲内にいるプレイヤーを 1 人選び、その人がいる部屋のレベルポイント(の天井)を優先する。
    ///   範囲内に誰もいない・その部屋に天井がないときは、ゲーム本来の選び方に任せる
    /// どちらもホストが決めて、ゲーム本来のテレポート(RPC)で全員に伝わる。
    /// </summary>
    internal static class Oogly
    {
        private const string Section = "14. ウーグリー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> IntervalMin;
        internal static ConfigEntry<float> IntervalMax;
        internal static ConfigEntry<bool> PreferPlayerRoom;
        internal static ConfigEntry<float> SearchRange;
        internal static ConfigEntry<float> MinPlayerDistance;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ウーグリーの変更を使う");
            IntervalMin = Util.Float(cfg, Section, "テレポートの間隔(最短)", 15f, 3f, 120f,
                "天井をうろついてから次にテレポートするまでの秒数の下限(ゲーム本来は 60)");
            IntervalMax = Util.Float(cfg, Section, "テレポートの間隔(最長)", 30f, 3f, 180f,
                "同じく上限(ゲーム本来は 120)。最短より小さいときは最短と同じになる");
            PreferPlayerRoom = cfg.Bind(Section, "プレイヤーのいる部屋へ優先してテレポート", true,
                "範囲内にいるプレイヤーの部屋へテレポートする");
            SearchRange = Util.Float(cfg, Section, "プレイヤーを探す範囲", 40f, 5f, 200f,
                "ウーグリーからこの距離(m)以内にいるプレイヤーの部屋を選ぶ(ゲーム本来は 16)");
            MinPlayerDistance = Util.Float(cfg, Section, "プレイヤーからの最低距離", 4f, 0f, 20f,
                "テレポート先の床の位置を、プレイヤーからこの距離(m)以上離す(ゲーム本来は 8)。真上に出すぎないように");
        }

        // ---------------- テレポートの間隔 ----------------

        [HarmonyPatch(typeof(EnemyOogly), nameof(EnemyOogly.StateCeilingRoam))]
        private static class CeilingRoamPatch
        {
            private static void Prefix(EnemyOogly __instance, out bool __state)
            {
                // 状態の始まりで、去ったあと(didLeave)ではないときだけ差し替える
                __state = __instance.stateStart && !__instance.didLeave;
            }

            private static void Postfix(EnemyOogly __instance, bool __state)
            {
                if (!__state || !Enabled.Value || !Util.IsAuthority || __instance.currentState != EnemyOogly.State.CeilingRoam)
                {
                    return;
                }
                // 本来 60〜120 秒になる場合だけ(終盤のトラックへ向かう・プレイヤーへ向かう短い時間はそのまま)
                if (__instance.stateTimerMax < 60f)
                {
                    return;
                }
                float min = IntervalMin.Value;
                float max = Mathf.Max(min, IntervalMax.Value);
                __instance.stateTimerMax = Random.Range(min, max);
                Plugin.Verbose($"ウーグリー: 次のテレポートまで {__instance.stateTimerMax:0} 秒");
            }
        }

        // ---------------- テレポート先 ----------------

        [HarmonyPatch(typeof(EnemyOogly), nameof(EnemyOogly.FindLevelPointAndCreateCeilingRoamPoints))]
        private static class FindLevelPointPatch
        {
            private static bool Prefix(EnemyOogly __instance)
            {
                if (!Enabled.Value || !PreferPlayerRoom.Value || !Util.IsAuthority || !__instance.rb)
                {
                    return true;
                }
                // 終盤・去るとき・最初の出現・調べに行くときは、ゲーム本来の選び方
                if (SemiFunc.EnemyDirectorEndingHeadToTruck() || __instance.doLeave
                    || (__instance.firstSpawn && __instance.enemy && __instance.enemy.EnemyParent && __instance.enemy.EnemyParent.firstSpawnPoint)
                    || __instance.investigatePosition != Vector3.zero)
                {
                    return true;
                }
                try
                {
                    return !TryPlayerRoom(__instance);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"ウーグリー: テレポート先の選択に失敗したので本来の選び方にします: {e.Message}");
                    return true;
                }
            }
        }

        private static bool TryPlayerRoom(EnemyOogly oogly)
        {
            Vector3 position = oogly.rb.position;
            var players = SemiFunc.PlayerGetList()
                .Where(p => p && !p.isDisabled && Vector3.Distance(position, p.transform.position) <= SearchRange.Value)
                .ToList();
            if (players.Count == 0)
            {
                return false;
            }
            var allPlayers = SemiFunc.PlayerGetList().Where(p => p && !p.isDisabled).ToList();
            float minDistance = MinPlayerDistance.Value;
            bool FarEnough(LevelPoint point) => allPlayers.All(p => Vector3.Distance(p.transform.position, point.transform.position) >= minDistance);

            // 選ぶ人の順番をばらばらにして、天井が見つかるまで順に試す
            foreach (var player in players.OrderBy(_ => Random.value))
            {
                var rooms = player.RoomVolumeCheck ? player.RoomVolumeCheck.CurrentRooms : null;
                List<LevelPoint> points = SemiFunc.LevelPointsGetAll()
                    .Where(point => point && point.Room && rooms != null && rooms.Contains(point.Room) && FarEnough(point))
                    .ToList();
                if (points.Count == 0)
                {
                    // 部屋がわからない・部屋が小さすぎるときは、その人の近く
                    points = SemiFunc.LevelPointGetWithinDistance(player.transform.position, minDistance, 16f)
                        .Where(FarEnough)
                        .ToList();
                }
                foreach (var point in points.OrderBy(_ => Random.value).Take(5))
                {
                    oogly.currentLevelPoint = point;
                    oogly.currentLevelPointPosition = point.transform.position;
                    if (oogly.FindHighestCeilingPoint())
                    {
                        oogly.CreateRoamPoints(oogly.ceilingPoint);
                        oogly.ceilingRoamPoints = new List<Vector3>(oogly.roamPoints);
                        oogly.firstSpawn = false;
                        Plugin.Verbose($"ウーグリー: {player.playerName} のいる部屋へテレポートします");
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
