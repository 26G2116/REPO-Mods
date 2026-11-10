using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// バンガー(爆弾ドクロ。導火線に火がつくと追いかけてきて爆発する)。クラス名は EnemyBang。
    /// - 爆発までの時間: 導火線の進み(本来 15 秒で爆発)。爆発するかどうかはホストの導火線で決まり、RPC で全員に伝わる。
    ///   MOD なしの人の画面では導火線の見た目が本来の速さのまま(爆発は同時に起きる)
    /// - 威力: ゲームの爆発のダメージ(30)は各自の PC で決まるので変えられない。
    ///   そこで、ホストが爆発の範囲内のプレイヤーに「設定したダメージ − 30」を上乗せする(MOD なしの人にも効く)
    /// </summary>
    internal static class Banger
    {
        private const string Section = "15. バンガー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> FuseTime;
        internal static ConfigEntry<int> Damage;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "バンガーの変更を使う");
            FuseTime = Util.Float(cfg, Section, "爆発までの時間", 7f, 1f, 30f,
                "導火線に火がついてから爆発するまでの秒数(ゲーム本来は 15)");
            Damage = Util.Int(cfg, Section, "爆発のダメージ", 100, 30, 100,
                "爆発に巻き込まれたプレイヤーが受けるダメージ(ゲーム本来は 30、最大 100)");
        }

        public static float GetFuseTime() => Enabled.Value ? FuseTime.Value : 15f;

        /// <summary>導火線の進み: fuseLerp += Time.deltaTime / 15f の 15 を差し替える。</summary>
        [HarmonyPatch(typeof(EnemyBang), nameof(EnemyBang.FuseLogic))]
        private static class FusePatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 15f, AccessTools.Method(typeof(Banger), nameof(GetFuseTime)), "EnemyBang.FuseLogic");
            }
        }

        /// <summary>導火線の爆発(ExplodeRPC。全員の PC で呼ばれる)のあと、ホストが不足分のダメージを上乗せする。</summary>
        [HarmonyPatch(typeof(EnemyBang), nameof(EnemyBang.ExplodeRPC))]
        private static class ExplodePatch
        {
            private static void Postfix(EnemyBang __instance)
            {
                if (!Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                var explosion = __instance.explosionScript;
                if (!explosion)
                {
                    return;
                }
                int extra = Damage.Value - explosion.explosionDamage;
                if (extra <= 0)
                {
                    return;
                }
                float radius = HurtRadius(explosion);
                Vector3 position = explosion.transform.position;
                if (Plugin.Instance)
                {
                    Plugin.Instance.StartCoroutine(BonusDamage(position, radius, extra, __instance.enemy));
                }
                Plugin.Verbose($"バンガー: 爆発(半径 {radius:0.0}m)に {extra} ダメージを上乗せします");
            }
        }

        /// <summary>
        /// ゲームの爆発はできてから 0.2 秒間プレイヤーにダメージを与えるので、その間に範囲へ入った人にも上乗せする(1 人 1 回)。
        /// バンガー本体はすぐ消えるので、プラグイン側でコルーチンを動かす。
        /// </summary>
        private static IEnumerator BonusDamage(Vector3 position, float radius, int extra, Enemy source)
        {
            var hit = new HashSet<PlayerAvatar>();
            float end = Time.time + 0.25f;
            while (true)
            {
                Boom.HurtPlayers(position, radius, extra, false, source, hit);
                if (Time.time >= end)
                {
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>ゲームの爆発がプレイヤーにダメージを与える範囲(m)。当たり判定の球の大きさは爆発の大きさ倍になる。</summary>
        private static float HurtRadius(ParticlePrefabExplosion explosion)
        {
            var hurt = explosion.HurtCollider;
            var sphere = hurt ? hurt.GetComponent<SphereCollider>() : null;
            if (!sphere)
            {
                return 3f * explosion.explosionSize;
            }
            var parent = hurt.transform.parent;
            float parentScale = parent ? parent.lossyScale.x : 1f;
            return sphere.radius * explosion.explosionSize * parentScale;
        }
    }
}
