using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// プレイヤーのスタミナ(ダッシュの残り)をホストが知るための仕組み。ゲームはスタミナを同期しないので:
    /// - MOD を入れた参加者は、自分のスタミナの割合を 0.5 秒ごとにホストへ送る(Net の KindStamina)
    /// - MOD なしの参加者は、同期されている「ダッシュ中か」から、ゲーム本来の減り方・回復の仕方で推定する
    /// - ホスト自身は自分の PlayerController の値をそのまま使う
    /// </summary>
    internal static class Stamina
    {
        private class Estimate
        {
            internal float Ratio = 1f;
            internal float RechargeWait;
        }

        private static readonly ConditionalWeakTable<PlayerAvatar, Estimate> Estimates = new ConditionalWeakTable<PlayerAvatar, Estimate>();
        private static readonly Dictionary<int, (float ratio, float time)> Reports = new Dictionary<int, (float, float)>();
        private static float _sendTimer;

        internal static void Init()
        {
            Net.RegisterFromClient(Net.KindStamina, (actor, data) =>
            {
                if (data.Length >= 1 && data[0] is float ratio)
                {
                    Reports[actor] = (Mathf.Clamp01(ratio), Time.time);
                }
            });
            Net.Tick += OnTick;
        }

        /// <summary>スタミナの割合(0〜1)。ホストで使う。</summary>
        internal static float Of(PlayerAvatar player)
        {
            if (!player)
            {
                return 1f;
            }
            if (player.isLocal)
            {
                var controller = PlayerController.instance;
                return controller && controller.EnergyStart > 0f ? Mathf.Clamp01(controller.EnergyCurrent / controller.EnergyStart) : 1f;
            }
            var owner = player.photonView ? player.photonView.Owner : null;
            if (owner != null && Reports.TryGetValue(owner.ActorNumber, out var report) && Time.time - report.time < 2f)
            {
                return report.ratio;
            }
            return Estimates.GetOrCreateValue(player).Ratio;
        }

        private static void OnTick()
        {
            if (!SemiFunc.IsMultiplayer() || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated)
            {
                return;
            }
            var controller = PlayerController.instance;
            if (!controller)
            {
                return;
            }
            if (!Photon.Pun.PhotonNetwork.IsMasterClient)
            {
                // 参加者: 自分のスタミナを送る
                _sendTimer -= Time.deltaTime;
                if (_sendTimer <= 0f)
                {
                    _sendTimer = 0.5f;
                    float ratio = controller.EnergyStart > 0f ? controller.EnergyCurrent / controller.EnergyStart : 1f;
                    Net.SendToHost(Net.KindStamina, Mathf.Clamp01(ratio));
                }
                return;
            }
            // ホスト: MOD なしの参加者のスタミナを推定する(本来の減り方と回復の仕方。強化は分からないので含めない)
            float start = Mathf.Max(1f, controller.EnergyStart);
            float drain = controller.EnergySprintDrain / start;
            float recharge = controller.sprintRechargeAmount / start;
            float wait = controller.sprintRechargeTime;
            float dt = Time.deltaTime;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isLocal)
                {
                    continue;
                }
                var e = Estimates.GetOrCreateValue(player);
                if (player.isSprinting)
                {
                    e.Ratio = Mathf.Max(0f, e.Ratio - drain * dt);
                    e.RechargeWait = wait;
                }
                else if (e.RechargeWait > 0f)
                {
                    e.RechargeWait -= dt;
                }
                else
                {
                    e.Ratio = Mathf.Min(1f, e.Ratio + recharge * dt);
                }
            }
        }
    }
}
