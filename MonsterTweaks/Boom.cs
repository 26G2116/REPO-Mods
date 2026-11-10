using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// MOD から起こす爆発。ホストが呼ぶ。
    /// - ダメージ: 範囲内のプレイヤーに直接与える(ホスト自身は Hurt、他の人は HurtOther RPC で本人の PC に届ける)。
    ///   ゲームの爆発はダメージを各自の PC で判定するため、MOD を入れていない参加者にも効くようにこうしている
    /// - 見た目: ホストの画面に出し、MOD を入れている参加者には Net で送って同じ見た目を出す
    ///   (MOD なしの参加者には見た目と音が出ない。ダメージは受ける)
    /// </summary>
    internal static class Boom
    {
        private static ParticleScriptExplosion _effect;

        internal static void Init()
        {
            Net.Register(Net.KindExplosion, data =>
            {
                if (data.Length >= 2 && data[0] is Vector3 position && data[1] is float size)
                {
                    SpawnVisual(position, size);
                }
            });
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
            Net.SendToOthers(Net.KindExplosion, position, visualSize);
            HurtPlayers(position, radius, damage, throughWalls, source);
        }

        /// <summary>
        /// 範囲内のプレイヤーに直接ダメージを与える(ホスト専用)。見た目は出さない。
        /// ゲームの爆発に追加のダメージを上乗せするときにも使う。
        /// alreadyHit を渡すと、そこにいる人は飛ばし、ダメージを与えた人を書き足す(同じ爆発で 2 回当てないため)。
        /// </summary>
        internal static void HurtPlayers(Vector3 position, float radius, int damage, bool throughWalls, Enemy source, HashSet<PlayerAvatar> alreadyHit = null)
        {
            if (!Util.IsAuthority || damage <= 0)
            {
                return;
            }
            int enemyIndex = source ? SemiFunc.EnemyGetIndex(source) : -1;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || (alreadyHit != null && alreadyHit.Contains(player)))
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
                alreadyHit?.Add(player);
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
