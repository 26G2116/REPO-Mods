using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ピーパー(天井の目玉。目が合った人をにらみつけて動けなくし、少しずつダメージを与える)。クラス名は EnemyCeilingEye。
    /// - ゲーム本来は 1 体が 1 人しかにらまない。本来のターゲット(1 人)はそのままに、目が合ったほかの人を
    ///   「追加のターゲット」として同時ににらむ(合計の人数は設定で決める)
    /// - 追加のターゲットへのダメージ(本来と同じ: 3 秒後から 1 秒ごとに 2)はホストが与えるので全員に効く
    /// - 追加のターゲットの演出(カメラが目玉に吸い寄せられる・画面が暗くなる・ズーム・目の見た目)は、
    ///   ホストが Net で知らせて MOD を入れた人の画面で出す。MOD なしの人はダメージだけ受ける
    /// - 本来のターゲットを見失っても、追加のターゲットが残っていればその人を本来のターゲットにしてにらみ続ける
    /// </summary>
    internal static class Peeper
    {
        private const string Section = "21. ピーパー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> MaxTargets;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ピーパーの変更を使う");
            MaxTargets = Util.Int(cfg, Section, "同時ににらめる人数", 3, 1, 10,
                "1 体が同時ににらめる人数(1 でゲーム本来と同じ)。追加の人の演出は MOD を入れた人の画面だけ");
        }

        internal static void Init()
        {
            Net.Register(Net.KindPeeperTargets, data =>
            {
                if (data.Length >= 2 && data[0] is int peeper && data[1] is int[] players)
                {
                    SetShown(peeper, players);
                }
            });
        }

        // ---------------- ホスト: 追加のターゲットの管理 ----------------

        private class Extra
        {
            internal PlayerAvatar Player;
            internal float Added;
            internal float LastSeen;
            internal float NextDamage;
        }

        private class EyeMemo
        {
            internal readonly List<Extra> Extras = new List<Extra>();
            internal int[] Sent = new int[0];
        }

        private static readonly ConditionalWeakTable<EnemyCeilingEye, EyeMemo> Memos = new ConditionalWeakTable<EnemyCeilingEye, EyeMemo>();

        /// <summary>ほかのピーパーがにらんでいる(本来・追加のどちらでも)人か。</summary>
        private static bool TargetedByOther(EnemyCeilingEye self, PlayerAvatar player)
        {
            foreach (var other in self.otherEnemies)
            {
                if (!other || other == self) continue;
                if (other.targetPlayer == player) return true;
                if (Memos.TryGetValue(other, out var memo) && memo.Extras.Any(e => e.Player == player)) return true;
            }
            return false;
        }

        private static bool Sees(EnemyCeilingEye eye, PlayerAvatar player)
        {
            Vector3 origin = eye.enemy.Vision.VisionTransform.position;
            return SemiFunc.PlayerVisionCheck(origin, 20f, player, true)
                || SemiFunc.PlayerVisionCheck(origin + eye.transform.right * 0.25f, 20f, player, true)
                || SemiFunc.PlayerVisionCheck(origin - eye.transform.right * 0.25f, 20f, player, true);
        }

        /// <summary>目が合ったら: すでに本来のターゲットがいれば、追加のターゲットにする。</summary>
        [HarmonyPatch(typeof(EnemyCeilingEye), nameof(EnemyCeilingEye.OnVisionTrigger))]
        private static class VisionPatch
        {
            /// <summary>本来の「ほかの目玉がにらんでいる人は選ばない」に、ほかの目玉の追加のターゲットも含める。</summary>
            private static bool Prefix(EnemyCeilingEye __instance)
            {
                if (!Util.IsAuthority || __instance.currentState == EnemyCeilingEye.State.HasTarget)
                {
                    return true;
                }
                var player = __instance.enemy && __instance.enemy.Vision ? __instance.enemy.Vision.onVisionTriggeredPlayer : null;
                if (!player || player == __instance.targetPlayer)
                {
                    return true;
                }
                foreach (var other in __instance.otherEnemies)
                {
                    if (other && other != __instance && Memos.TryGetValue(other, out var memo) && memo.Extras.Any(e => e.Player == player))
                    {
                        return false;
                    }
                }
                return true;
            }

            private static void Postfix(EnemyCeilingEye __instance)
            {
                if (!Enabled.Value || !Util.IsAuthority || __instance.currentState != EnemyCeilingEye.State.HasTarget
                    || __instance.enemy.CurrentState == EnemyState.Despawn)
                {
                    return;
                }
                var player = __instance.enemy.Vision.onVisionTriggeredPlayer;
                if (!player || player.isDisabled || player == __instance.targetPlayer)
                {
                    return;
                }
                var memo = Memos.GetOrCreateValue(__instance);
                var existing = memo.Extras.FirstOrDefault(e => e.Player == player);
                if (existing != null)
                {
                    existing.LastSeen = Time.time;
                    return;
                }
                if (memo.Extras.Count + 1 >= MaxTargets.Value || TargetedByOther(__instance, player))
                {
                    return;
                }
                var v = __instance.enemy.Vision;
                if (!SemiFunc.PlayerVisionCheckPosition(v.VisionTransform.position, player.PlayerVisionTarget.VisionTransform.position, v.VisionDistance, player, true))
                {
                    return;
                }
                memo.Extras.Add(new Extra { Player = player, Added = Time.time, LastSeen = Time.time, NextDamage = Time.time + 3f });
                Plugin.Verbose($"ピーパー: {player.playerName} も追加でにらみます");
                Send(__instance, memo);
            }
        }

        [HarmonyPatch(typeof(EnemyCeilingEye), nameof(EnemyCeilingEye.Update))]
        private static class UpdatePatch
        {
            private static void Postfix(EnemyCeilingEye __instance)
            {
                if (Util.IsAuthority && Memos.TryGetValue(__instance, out var memo))
                {
                    HostUpdate(__instance, memo);
                }
                ApplyEffects(__instance);
            }
        }

        private static void HostUpdate(EnemyCeilingEye eye, EyeMemo memo)
        {
            bool changed = false;
            if (!Enabled.Value || eye.currentState != EnemyCeilingEye.State.HasTarget)
            {
                changed = memo.Extras.Count > 0;
                memo.Extras.Clear();
            }
            else
            {
                // ゲーム中に人数の設定を減らしたら、あとから加えた人から外す
                while (memo.Extras.Count > 0 && memo.Extras.Count + 1 > MaxTargets.Value)
                {
                    memo.Extras.RemoveAt(memo.Extras.Count - 1);
                    changed = true;
                }
                float now = Time.time;
                for (int i = memo.Extras.Count - 1; i >= 0; i--)
                {
                    var extra = memo.Extras[i];
                    var player = extra.Player;
                    if (!player || player.isDisabled || player == eye.targetPlayer)
                    {
                        memo.Extras.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    if (Sees(eye, player))
                    {
                        extra.LastSeen = now;
                    }
                    else if (now - extra.LastSeen > 0.5f)
                    {
                        // 本来のターゲットと同じく、0.5 秒見えなければ外す
                        memo.Extras.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    if (now >= extra.NextDamage)
                    {
                        extra.NextDamage = now + 1f;
                        player.playerHealth.HurtOther(2, player.transform.position, false, SemiFunc.EnemyGetIndex(eye.enemy), false);
                    }
                }
            }
            if (changed)
            {
                Send(eye, memo);
            }
        }

        /// <summary>本来のターゲットを見失いそうなとき、追加のターゲットがいればその人を本来のターゲットにする。</summary>
        [HarmonyPatch(typeof(EnemyCeilingEye), nameof(EnemyCeilingEye.UpdateState))]
        private static class UpdateStatePatch
        {
            private static bool Prefix(EnemyCeilingEye __instance, EnemyCeilingEye.State _state)
            {
                if (_state != EnemyCeilingEye.State.TargetLost || __instance.currentState != EnemyCeilingEye.State.HasTarget
                    || !Enabled.Value || !Util.IsAuthority || !Memos.TryGetValue(__instance, out var memo))
                {
                    return true;
                }
                var next = memo.Extras.FirstOrDefault(e => e.Player && !e.Player.isDisabled);
                if (next == null)
                {
                    return true;
                }
                memo.Extras.Remove(next);
                int id = next.Player.photonView.ViewID;
                if (SemiFunc.IsMultiplayer())
                {
                    __instance.photonView.RPC("TargetPlayerRPC", RpcTarget.All, id);
                }
                else
                {
                    __instance.TargetPlayerRPC(id, default);
                }
                __instance.targetPlayer = next.Player;
                __instance.stateTimer = 0.5f;
                // ダメージの間隔は、追加のターゲットだったときの続きにする(にらまれて 3 秒は猶予、その後 1 秒ごと)
                float now = Time.time;
                __instance.eyeDamageWaitTimer = Mathf.Max(0f, next.Added + 3f - now);
                __instance.eyeDamageTimer = __instance.eyeDamageWaitTimer > 0f ? 0f : Mathf.Max(0f, next.NextDamage - now);
                Send(__instance, memo);
                Plugin.Verbose($"ピーパー: 本来のターゲットを {next.Player.playerName} に切り替えます");
                return false;
            }
        }

        private static void Send(EnemyCeilingEye eye, EyeMemo memo)
        {
            var ids = memo.Extras.Where(e => e.Player).Select(e => e.Player.photonView.ViewID).ToArray();
            if (ids.SequenceEqual(memo.Sent))
            {
                return;
            }
            memo.Sent = ids;
            int viewId = eye.photonView ? eye.photonView.ViewID : 0;
            SetShown(viewId, ids);
            Net.SendToOthers(Net.KindPeeperTargets, viewId, ids);
        }

        // ---------------- 全員(MOD あり): 追加のターゲットの演出 ----------------

        private class Shown
        {
            internal int[] Players = new int[0];
            internal readonly HashSet<int> Started = new HashSet<int>();
        }

        private static readonly Dictionary<int, Shown> ShownByEye = new Dictionary<int, Shown>();

        private static void SetShown(int eyeViewId, int[] players)
        {
            if (!ShownByEye.TryGetValue(eyeViewId, out var shown))
            {
                shown = new Shown();
                ShownByEye[eyeViewId] = shown;
            }
            shown.Players = players ?? new int[0];
            shown.Started.IntersectWith(shown.Players);
            if (shown.Players.Length == 0)
            {
                ShownByEye.Remove(eyeViewId);
            }
        }

        private static void ApplyEffects(EnemyCeilingEye eye)
        {
            if (ShownByEye.Count == 0 || !eye.photonView || !ShownByEye.TryGetValue(eye.photonView.ViewID, out var shown))
            {
                return;
            }
            if (eye.currentState != EnemyCeilingEye.State.HasTarget)
            {
                return;
            }
            Vector3 center = eye.enemy.CenterTransform.position;
            foreach (int id in shown.Players)
            {
                var player = SemiFunc.PlayerAvatarGetFromPhotonID(id);
                if (!player || player.isDisabled)
                {
                    continue;
                }
                // 本来のターゲットと同じ見た目(EnemyCeilingEye.Update と同じ処理)
                SemiFunc.PlayerEyesOverride(player, center, 0.1f, eye.gameObject);
                if (player.voiceChat)
                {
                    player.voiceChat.OverridePitch(1.25f, 1f, 2f, 0.1f, 0f, 0f);
                }
                player.OverridePupilSize(2f, 5, 5f, 0.5f, 5f, 0.5f, 0.1f);
                player.playerHealth.EyeMaterialOverride(PlayerHealth.EyeOverrideState.CeilingEye, 0.25f, 0);
                if (!player.isLocal)
                {
                    continue;
                }
                if (shown.Started.Add(id))
                {
                    // にらまれた瞬間(本来の TargetPlayerRPC と同じ)
                    player.physGrabber.ReleaseObject(-1, 0.5f);
                    CameraAim.Instance.AimTargetSet(center, 0.5f, 2f, eye.gameObject, 100);
                    CameraGlitch.Instance.PlayLong();
                }
                float down = Vector3.Dot(Vector3.down, (player.transform.position - center).normalized);
                CameraAim.Instance.AimTargetSoftSet(center, 0.1f, 2f, down > 0.9f ? 5f : 10f, eye.gameObject, 100);
                PostProcessing.Instance.VignetteOverride(Color.black, 0.5f, 1f, 1f, 0.5f, 0.1f, eye.gameObject, 999);
                CameraZoom.Instance.OverrideZoomSet(40f, 0.1f, 1f, 1f, eye.gameObject, 50);
            }
        }
    }
}
