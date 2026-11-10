using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// 無重力の杖(ItemStaffZeroGravity)と同じ無重力の効果をプレイヤーにかける。ホストが使う。
    /// 杖の弾を通信つきで生成し、その範囲効果(SemiAreaOfEffect)の RPC をこちらで選んだ相手と時間で呼ぶ
    /// (ルームのロール効果と同じ方式。全員の PC でゲーム本来の無重力になり、MOD なしの人にも効く)。
    /// </summary>
    internal static class ZeroGravityEffect
    {
        internal static void Apply(IList<PlayerAvatar> players, float seconds)
        {
            if (!Util.IsAuthority || players == null || players.Count == 0 || seconds <= 0f)
            {
                return;
            }
            if (!SemiFunc.IsMultiplayer())
            {
                // シングルプレイの範囲効果は自分で範囲内の物を集める作りなので、相手を選べない。自分に直接かける
                var local = PlayerAvatar.instance;
                if (local && players.Contains(local) && Net.Runner)
                {
                    Net.Runner.StartCoroutine(LocalZeroGravity(local, seconds));
                }
                return;
            }
            var prefab = VanillaAssets.ZeroGravityProjectile;
            if (prefab == null || !PunManager.instance)
            {
                return;
            }
            var ids = new List<int>();
            foreach (var player in players)
            {
                if (player && !player.isDisabled && player.photonView)
                {
                    ids.Add(player.photonView.ViewID);
                }
            }
            if (ids.Count == 0)
            {
                return;
            }
            var projectile = VanillaAssets.Spawn(prefab, players[0].transform.position + Vector3.up * 0.5f, Quaternion.LookRotation(Vector3.down));
            if (!projectile)
            {
                return;
            }
            var areaOfEffect = projectile.GetComponentInChildren<SemiAreaOfEffect>(true);
            var view = areaOfEffect ? areaOfEffect.GetComponent<PhotonView>() : null;
            if (!areaOfEffect || !view)
            {
                Remove(projectile);
                return;
            }
            // 無重力の効果は始まるときに時間を 2 倍にする(SemiAffectZeroGravity.Start)ので、半分を渡す
            float time = seconds * 0.5f;
            var times = new float[ids.Count];
            var affectIds = new int[ids.Count];
            var endTimes = new double[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                times[i] = time;
                affectIds[i] = PunManager.instance.SemiAffectNetworkIDGet();
                endTimes[i] = PhotonNetwork.Time + time * 2f;
            }
            Vector3 center = players[0].transform.position;
            view.RPC("CreateAffectsRPC", RpcTarget.All, ids.ToArray(), times, affectIds, endTimes, center, Vector3.up);
            // 弾そのものは使わない(飛んでいって別の効果を出さないよう、すぐ消す)
            Remove(projectile);
        }

        /// <summary>シングルプレイ: 無重力の効果と同じこと(無重力 + 転倒)を自分に続ける。</summary>
        private static IEnumerator LocalZeroGravity(PlayerAvatar player, float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end && player && !player.isDisabled)
            {
                if (PlayerController.instance)
                {
                    PlayerController.instance.AntiGravity(0.1f);
                }
                if (player.tumble)
                {
                    if (!player.isTumbling)
                    {
                        player.tumble.TumbleRequest(true, false);
                    }
                    player.tumble.TumbleOverrideTime(0.5f);
                }
                yield return null;
            }
        }

        private static void Remove(GameObject projectile)
        {
            if (SemiFunc.IsMultiplayer())
            {
                PhotonNetwork.Destroy(projectile);
            }
            else
            {
                Object.Destroy(projectile);
            }
        }
    }
}
