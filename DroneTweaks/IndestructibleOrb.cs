using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// 不滅オーブ(破壊不能オーブ、削除されたアイテム)の変更。
    ///
    /// - 範囲内の物(貴重品など)を壊れなくするのはゲーム本来の効果(ホストが計算)
    /// - 範囲内のプレイヤーを無敵にする:
    ///   - ダメージ: プレイヤーのダメージは「ダメージを受ける人の PC」で計算され、体力がホストへ送られてくる。
    ///     ホストが減ったのを見たら、すぐ同じ量を回復で戻す(= MOD なしの人にも効く。ホスト自身も同じ方式)。
    ///     一撃で体力が 0 になる攻撃は戻せない
    ///   - タンブル: 転ばせる要求・転んだあとの起き上がれない時間は、すべてホストに送られて処理されるので、
    ///     ホストで止める(= MOD なしの人も転ばなくなる)。自分で転ぶ操作はそのままできる
    /// - 電池の持ち: 電池満タンで設定の秒数だけ使える(電池はホストが計算)
    /// 範囲の判定はオーブの処理と同じく各 PC で行う(オーブの起動状態はゲーム本来の通信で全員にそろう)。
    /// </summary>
    internal static class IndestructibleOrb
    {
        private const string Section = "3. 不滅オーブ";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<float> BatterySeconds;

        // 範囲の端を出入りしたときに、一瞬だけ無敵が切れるのを防ぐ
        private const float Linger = 0.25f;

        private static readonly Dictionary<PlayerAvatar, float> ProtectedUntil = new Dictionary<PlayerAvatar, float>();

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "範囲内のプレイヤーを無敵にする(ダメージを受けない・転ばない)。電池の持ちも下の設定に変える");
            BatterySeconds = cfg.Bind(Section, "電池満タンで使える時間(秒)", 60f,
                new ConfigDescription("起動している間、電池がこの秒数で 100% から 0% になる", new AcceptableValueRange<float>(5f, 600f)));
        }

        internal static bool IsProtected(PlayerAvatar player)
        {
            return player && Enabled.Value && ProtectedUntil.TryGetValue(player, out float until) && until > Time.time;
        }

        // ---- 範囲内のプレイヤーを記録し、電池の持ちを変える ----

        [HarmonyPatch(typeof(ItemOrbIndestructible), nameof(ItemOrbIndestructible.Update))]
        private static class OrbPatch
        {
            private static void Postfix(ItemOrbIndestructible __instance)
            {
                var orb = __instance.itemOrb;
                if (!orb)
                {
                    return;
                }
                var battery = orb.itemBattery;
                if (battery && SemiFunc.IsMasterClientOrSingleplayer())
                {
                    // 電池は「減る速さ × 2 × 経過時間」ずつ減る(ItemBattery.DrainContinuous)
                    battery.batteryDrainRate = Enabled.Value ? 100f / (2f * BatterySeconds.Value) : orb.batteryDrainRate;
                }

                if (!Enabled.Value || !orb.itemActive || orb.currentState != ItemOrb.State.Active
                    || (battery && battery.batteryLife <= 0f)
                    || (orb.itemEquippable && orb.itemEquippable.isEquipped)
                    || !GameDirector.instance)
                {
                    return;
                }

                if (ProtectedUntil.Count > 32)
                {
                    // 階を移るとプレイヤーのオブジェクトが作り直されるので、古いものを消す
                    var stale = new List<PlayerAvatar>();
                    foreach (var key in ProtectedUntil.Keys)
                    {
                        if (!key)
                        {
                            stale.Add(key);
                        }
                    }
                    foreach (var key in stale)
                    {
                        ProtectedUntil.Remove(key);
                    }
                    var staleHealth = new List<PlayerHealth>();
                    foreach (var key in HealStates.Keys)
                    {
                        if (!key)
                        {
                            staleHealth.Add(key);
                        }
                    }
                    foreach (var key in staleHealth)
                    {
                        HealStates.Remove(key);
                    }
                }

                Vector3 center = orb.transform.position;
                float radius = orb.orbRadius;
                float until = Time.time + Linger;
                foreach (var player in GameDirector.instance.PlayerList)
                {
                    if (player && !player.isDisabled && InRange(player, center, radius))
                    {
                        if (!IsProtected(player))
                        {
                            Plugin.Verbose($"不滅オーブ: {player.playerName} が範囲に入りました(無敵)");
                        }
                        ProtectedUntil[player] = until;
                    }
                }
            }
        }

        /// <summary>プレイヤーの体(足元から目の高さまでの線)がオーブの範囲に触れているか。</summary>
        private static bool InRange(PlayerAvatar player, Vector3 center, float radius)
        {
            var tumble = player.tumble;
            if (tumble && tumble.isTumbling && tumble.physGrabObject)
            {
                return Vector3.Distance(center, tumble.physGrabObject.centerPoint) <= radius + 0.4f;
            }
            Vector3 feet = player.transform.position;
            Vector3 eyes = player.PlayerVisionTarget && player.PlayerVisionTarget.VisionTransform
                ? player.PlayerVisionTarget.VisionTransform.position
                : feet + Vector3.up * 1.5f;
            return DistanceToSegment(center, feet, eyes) <= radius + 0.3f;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float length = ab.sqrMagnitude;
            float t = length > 0.0001f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / length) : 0f;
            return Vector3.Distance(point, a + ab * t);
        }

        // ---- 減った体力をホストがすぐ回復で戻す(ホスト) ----

        // 体力の減少はダメージを受けた人の PC からホストへ送られてくるので、範囲を出た直後に届いた分も戻せるよう少し長めに見る
        private const float HealBackGrace = 0.5f;

        // 回復を送ってから相手の PC で回復して体力が送り返されるまでの間に、同じ分を二重に送らないための記録
        private class HealState
        {
            internal int Expected;   // 戻したい体力
            internal int LastSeen;   // 前のフレームに見た体力
            internal int Pending;    // 送ったが、まだ反映が届いていない回復量
            internal float PendingTime;
        }

        private static readonly Dictionary<PlayerHealth, HealState> HealStates = new Dictionary<PlayerHealth, HealState>();

        [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Update))]
        private static class HealBackPatch
        {
            private static void Postfix(PlayerHealth __instance)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer() || __instance.isMenuAvatar)
                {
                    return;
                }
                var player = __instance.playerAvatar;
                bool active = player && !player.isDisabled && !player.deadSet && Enabled.Value
                    && ProtectedUntil.TryGetValue(player, out float until) && until + HealBackGrace > Time.time;
                if (!active)
                {
                    HealStates.Remove(__instance);
                    return;
                }

                int health = __instance.health;
                if (!HealStates.TryGetValue(__instance, out var state))
                {
                    HealStates[__instance] = new HealState { Expected = health, LastSeen = health };
                    return;
                }
                if (health > state.LastSeen)
                {
                    state.Pending = Mathf.Max(0, state.Pending - (health - state.LastSeen));
                }
                state.LastSeen = health;
                if (health >= state.Expected)
                {
                    // ほかの方法で回復した分も含めて、今の体力を基準にする
                    state.Expected = health;
                    state.Pending = 0;
                    return;
                }
                if (state.Pending > 0 && Time.time - state.PendingTime > 1.5f)
                {
                    state.Pending = 0; // 回復が届かなかった(上限に達していた等)ときは送り直す
                }
                int deficit = state.Expected - health - state.Pending;
                if (deficit > 0)
                {
                    __instance.HealOther(deficit, false);
                    state.Pending += deficit;
                    state.PendingTime = Time.time;
                    Plugin.Verbose($"不滅オーブ: {player.playerName} の減った体力 {deficit} を戻しました");
                }
            }
        }

        // ---- タンブルを止める(ホスト) ----

        /// <summary>自分で転んでいる最中か、まだ転んでいないときは、敵や物による「転ばせる・起き上がれなくする」を止める。</summary>
        private static bool BlockTumble(PlayerTumble tumble)
        {
            return IsProtected(tumble.playerAvatar) && (!tumble.isTumbling || tumble.isPlayerInputTriggered);
        }

        [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleRequestRPC))]
        private static class TumbleRequestPatch
        {
            private static bool Prefix(PlayerTumble __instance, bool _isTumbling, bool _playerInput)
            {
                return !_isTumbling || _playerInput || !BlockTumble(__instance);
            }
        }

        [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleOverrideTimeRPC))]
        private static class TumbleOverridePatch
        {
            private static bool Prefix(PlayerTumble __instance)
            {
                return !SemiFunc.IsMasterClientOrSingleplayer() || !BlockTumble(__instance);
            }
        }

        [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleStunTimeRPC))]
        private static class TumbleStunPatch
        {
            private static bool Prefix(PlayerTumble __instance)
            {
                return !SemiFunc.IsMasterClientOrSingleplayer() || !BlockTumble(__instance);
            }
        }
    }
}
