using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// MOD から起こす爆発。ホストが呼ぶ。
    /// - ダメージ: 範囲内のプレイヤーに直接与える(ホスト自身は Hurt、他の人は HurtOther RPC で本人の PC に届ける)。
    ///   ゲームの爆発はダメージを各自の PC で判定するため、MOD を入れていない参加者にも効くようにこうしている
    /// - 見た目: ホストの画面に出し、MOD を入れている参加者には Photon イベントで送って同じ見た目を出す
    ///   (MOD なしの参加者には見た目と音が出ない。ダメージは受ける)
    /// </summary>
    internal static class Boom
    {
        /// <summary>LoadProgress(173)と重ならない Photon イベント番号。</summary>
        private const byte EventCode = 174;
        private const byte KindExplosion = 1;

        private static ParticleScriptExplosion _effect;

        internal static void Init()
        {
            PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        }

        /// <summary>
        /// 爆発させる(ホスト専用)。
        /// visualSize はゲームの爆発の大きさ(敵バンの大爆発が 1)、radius はダメージが届く距離(m)。
        /// </summary>
        internal static void Explode(Vector3 position, float visualSize, float radius, int damage, bool throughWalls, Enemy source)
        {
            if (!Util.IsAuthority)
            {
                return;
            }
            SpawnVisual(position, visualSize);
            if (SemiFunc.IsMultiplayer())
            {
                PhotonNetwork.RaiseEvent(EventCode,
                    new object[] { KindExplosion, position, visualSize },
                    new RaiseEventOptions { Receivers = ReceiverGroup.Others },
                    SendOptions.SendReliable);
            }
            if (damage <= 0)
            {
                return;
            }
            int enemyIndex = source ? SemiFunc.EnemyGetIndex(source) : -1;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                Vector3 target = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
                if (Vector3.Distance(position, target) > radius)
                {
                    continue;
                }
                if (!throughWalls && Physics.Linecast(position, target, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                if (player.isLocal || !SemiFunc.IsMultiplayer())
                {
                    player.playerHealth.Hurt(damage, false, enemyIndex);
                }
                else
                {
                    // 位置に Vector3.zero を渡すと、受け取った側で距離の確認をせずにダメージを受ける
                    player.playerHealth.HurtOther(damage, Vector3.zero, false, enemyIndex);
                }
            }
        }

        private static void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != EventCode || !(photonEvent.CustomData is object[] data) || data.Length < 3)
            {
                return;
            }
            // ホスト以外からの爆発は受け付けない
            var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);
            if (sender == null || !sender.IsMasterClient)
            {
                return;
            }
            if (data[0] is byte kind && kind == KindExplosion && data[1] is Vector3 position && data[2] is float size)
            {
                SpawnVisual(position, size);
            }
        }

        /// <summary>ダメージなしの爆発エフェクトと音を出す。</summary>
        private static void SpawnVisual(Vector3 position, float size)
        {
            var effect = GetEffect();
            if (!effect)
            {
                return;
            }
            var explosion = effect.Spawn(position, size, 0, 0, 1f, true);
            if (explosion)
            {
                // Spawn は onlyParticleEffect をエフェクト側に渡さないので、ここで設定する(当たり判定を出さない)
                explosion.onlyParticleEffect = true;
            }
        }

        private static ParticleScriptExplosion GetEffect()
        {
            if (_effect && _effect.explosionPreset && _effect.explosionPrefab)
            {
                return _effect;
            }
            var preset = Resources.FindObjectsOfTypeAll<ExplosionPreset>().FirstOrDefault();
            var prefab = Resources.Load<GameObject>("Effects/Part Prefab Explosion");
            if (!preset || !prefab)
            {
                Plugin.Log.LogWarning("爆発エフェクトの素材が見つからないため、見た目なしで爆発します");
                return null;
            }
            if (!_effect)
            {
                var holder = new GameObject("MonsterTweaks Explosion");
                Object.DontDestroyOnLoad(holder);
                holder.SetActive(false); // Start で素材を読み直させない
                _effect = holder.AddComponent<ParticleScriptExplosion>();
            }
            _effect.explosionPreset = preset;
            _effect.explosionPrefab = prefab;
            return _effect;
        }
    }
}
