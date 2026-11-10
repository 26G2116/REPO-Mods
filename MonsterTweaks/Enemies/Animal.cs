using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// アニマル(六本足。見つけた相手の部屋で暴れ回る)。クラス名は EnemyAnimal。ホストだけで動く。
    /// - スタン無効: 共通設定(EnemyCommon)。このセクションの「スタン無効」
    /// - 転んでいない相手を優先: 追いかけ・暴れている間、今の相手が転んでいる(スタン中)か倒れていたら、
    ///   視界に入っている人のうち転んでいない一番近い人へ乗り換える。気づいた瞬間も同じ条件で選び直す。
    ///   アニマルの相手はホストの中だけで持っている(通信しない)ので、差し替えるだけで全員に効く
    /// - 体当たりで突き飛ばす: 駆け寄り中・暴れ中の体当たりが当たったプレイヤーを、アップスクリームの
    ///   吹き飛ばしと同じ手順(転ばせて力を加える。ゲーム本来の RPC)で突き飛ばす。MOD なしの人にも効く。
    ///   当たりの判定は当たった本人の PC でしか行われないので、ホストは次の 2 つで当たりを知る:
    ///   ホスト自身は Hurt に渡される敵の番号、ほかの人は「体力が減った知らせが来たとき、すぐ近くに攻撃中のアニマルがいる」
    /// </summary>
    internal static class Animal
    {
        private const string Section = "12. アニマル";

        /// <summary>ほかの人の体力が減ったとき、この距離(m)以内に攻撃中のアニマルがいれば体当たりとみなす。</summary>
        private const float HitAttributionRange = 3.5f;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> PreferStanding;
        internal static ConfigEntry<bool> Knockback;
        internal static ConfigEntry<float> KnockbackForce;
        internal static ConfigEntry<float> KnockbackTumbleTime;
        internal static ConfigEntry<float> KnockbackInterval;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "アニマルの変更を使う");
            PreferStanding = cfg.Bind(Section, "転んでいない相手を優先", true,
                "視界に入っている人のうち、転んで(スタンして)いない人を優先して狙う。今の相手が転んだら、転んでいない人へ乗り換える");
            Knockback = cfg.Bind(Section, "体当たりで突き飛ばす", true,
                "体当たりが当たったプレイヤーを転ばせて、アニマルと反対の向きへ強く突き飛ばす(ダメージは増えない)");
            KnockbackForce = Util.Float(cfg, Section, "突き飛ばす力", 45f, 5f, 150f,
                "突き飛ばす強さ(45 でアップスクリーム本来の吹き飛ばしと同じ)");
            KnockbackTumbleTime = Util.Float(cfg, Section, "突き飛ばされて転んでいる時間", 1.5f, 0.5f, 5f,
                "突き飛ばされてから起き上がれるまでの秒数(アップスクリーム本来は 1.5)");
            KnockbackInterval = Util.Float(cfg, Section, "同じ人を突き飛ばす間隔", 5f, 0f, 30f,
                "一度突き飛ばした人は、この秒数のあいだ突き飛ばさない(続けて飛ばされて抜け出せなくなるのを防ぐ。体当たりのダメージは本来どおり受ける)");
        }

        // ---------------- 体当たりで突き飛ばす ----------------

        private static readonly Dictionary<PlayerAvatar, float> LastKnockback = new Dictionary<PlayerAvatar, float>();

        private static bool KnockbackActive => Enabled.Value && Knockback.Value && Util.IsAuthority;

        private static bool Attacking(EnemyAnimal animal) =>
            animal.currentState == EnemyAnimal.State.GoToPlayer || animal.currentState == EnemyAnimal.State.WreakHavoc;

        private static Vector3 BodyPosition(EnemyAnimal animal) =>
            animal.enemy.Rigidbody ? animal.enemy.Rigidbody.transform.position : animal.transform.position;

        /// <summary>プレイヤーのすぐ近くにいる攻撃中のアニマル。いなければ null。</summary>
        private static EnemyAnimal AttackingAnimalNear(PlayerAvatar player)
        {
            if (!EnemyDirector.instance)
            {
                return null;
            }
            EnemyAnimal best = null;
            float bestDistance = HitAttributionRange;
            foreach (var parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (!parent || !parent.Spawned || !parent.Enemy)
                {
                    continue;
                }
                var animal = parent.Enemy.GetComponent<EnemyAnimal>();
                if (!animal || !animal.enemy || !Attacking(animal))
                {
                    continue;
                }
                float distance = Vector3.Distance(BodyPosition(animal), player.transform.position);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = animal;
                }
            }
            return best;
        }

        /// <summary>EnemyUpscreamAnim.AttackImpulse と同じ手順の突き飛ばし(落下時の追加ダメージは付けない)。</summary>
        private static void Launch(PlayerAvatar player, EnemyAnimal animal)
        {
            if (!player || player.isDisabled || player.deadSet || !player.tumble)
            {
                return;
            }
            float now = Time.time;
            if (LastKnockback.TryGetValue(player, out float last) && now - last < KnockbackInterval.Value)
            {
                return;
            }
            // 階をまたいで残った記録(壊れたプレイヤー)を掃除してから記録する
            if (LastKnockback.Count > 32)
            {
                LastKnockback.Clear();
            }
            LastKnockback[player] = now;

            Vector3 direction = Vector3.ProjectOnPlane(player.transform.position - BodyPosition(animal), Vector3.up);
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = animal.transform.forward;
            }
            // 体当たりなので、アップスクリーム(上向き 6 割)より横向きに飛ばす
            direction = Vector3.Lerp(direction.normalized, Vector3.up, 0.4f);
            float force = KnockbackForce.Value;
            player.tumble.TumbleRequest(true, false);
            player.tumble.TumbleForce(direction * force);
            player.tumble.TumbleTorque(-player.transform.right * force);
            player.tumble.TumbleOverrideTime(KnockbackTumbleTime.Value);
            Plugin.Verbose($"アニマル: 体当たりで {player.playerName} を突き飛ばしました");
        }

        /// <summary>ホスト自身(とシングルプレイ)の被弾。体力が実際に減ったときだけ、敵の番号からアニマルかどうかを確かめる。</summary>
        [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
        private static class HurtPatch
        {
            private static void Prefix(PlayerHealth __instance, out int __state)
            {
                __state = __instance.health;
            }

            private static void Postfix(PlayerHealth __instance, int enemyIndex, int __state)
            {
                if (!KnockbackActive || enemyIndex < 0 || __instance.health >= __state || __instance.health <= 0)
                {
                    return;
                }
                var enemy = SemiFunc.EnemyGetFromIndex(enemyIndex);
                var animal = enemy ? enemy.GetComponent<EnemyAnimal>() : null;
                if (animal && animal.enemy)
                {
                    Launch(__instance.playerAvatar, animal);
                }
            }
        }

        /// <summary>
        /// ほかの人の被弾。体力が減った知らせ(本来の RPC)がホストに届いたとき、その人のすぐ近くに攻撃中のアニマルがいれば
        /// 体当たりが当たったとみなす(知らせには原因が入っていないため)。
        /// </summary>
        [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.UpdateHealthRPC))]
        private static class UpdateHealthPatch
        {
            private static void Prefix(PlayerHealth __instance, out int __state)
            {
                // 参加直後の最初の知らせは「減った」のではなく初期値の受け取りなので除く
                __state = __instance.healthSet ? __instance.health : int.MinValue;
            }

            private static void Postfix(PlayerHealth __instance, bool effect, bool hurtByHeal, int __state)
            {
                // 送り主が正しくないと本来の処理は何もしない(体力が変わらない)ので、前後の値で確かめる
                if (!KnockbackActive || !effect || hurtByHeal || __instance.health >= __state || __instance.health <= 0)
                {
                    return;
                }
                var player = __instance.playerAvatar;
                if (!player || player.isDisabled)
                {
                    return;
                }
                var animal = AttackingAnimalNear(player);
                if (animal)
                {
                    Launch(player, animal);
                }
            }
        }

        // ---------------- 転んでいない相手を優先 ----------------

        private static bool Usable(PlayerAvatar player) => player && !player.isDisabled && !player.isTumbling;

        /// <summary>視界に入っていて転んでいない人のうち、一番近い人。いなければ null。</summary>
        private static PlayerAvatar BestSeenStanding(EnemyAnimal animal)
        {
            var vision = animal.enemy.Vision;
            if (!vision)
            {
                return null;
            }
            PlayerAvatar best = null;
            float bestDistance = float.MaxValue;
            Vector3 position = animal.enemy.Rigidbody ? animal.enemy.Rigidbody.transform.position : animal.transform.position;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!Usable(player) || !vision.VisionTriggered.TryGetValue(player.photonView.ViewID, out bool seen) || !seen)
                {
                    continue;
                }
                float distance = Vector3.Distance(position, player.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }
            return best;
        }

        private static bool Active => Enabled.Value && PreferStanding.Value && Util.IsAuthority;

        [HarmonyPatch(typeof(EnemyAnimal), nameof(EnemyAnimal.OnVision))]
        private static class OnVisionPatch
        {
            private static void Postfix(EnemyAnimal __instance)
            {
                if (!Active || __instance.currentState != EnemyAnimal.State.PlayerNotice || Usable(__instance.playerTarget))
                {
                    return;
                }
                var better = BestSeenStanding(__instance);
                if (better)
                {
                    __instance.playerTarget = better;
                    // 本来の「気づいた」演出は乗り換え前の相手に送られているので、乗り換えた相手にも送り直す
                    if (__instance.enemy && !__instance.enemy.IsStunned())
                    {
                        int id = better.photonView.ViewID;
                        if (GameManager.Multiplayer())
                        {
                            __instance.photonView.RPC("NoticeRPC", Photon.Pun.RpcTarget.All, id);
                        }
                        else
                        {
                            __instance.enemyAnimalAnim.NoticeSet(id);
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(EnemyAnimal), nameof(EnemyAnimal.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyAnimal __instance)
            {
                if (!Active || !__instance.enemy)
                {
                    return;
                }
                var state = __instance.currentState;
                if (state != EnemyAnimal.State.PlayerNotice && state != EnemyAnimal.State.GoToPlayer && state != EnemyAnimal.State.WreakHavoc)
                {
                    return;
                }
                if (Usable(__instance.playerTarget))
                {
                    return;
                }
                var better = BestSeenStanding(__instance);
                if (better && better != __instance.playerTarget)
                {
                    __instance.playerTarget = better;
                    Plugin.Verbose($"アニマル: 転んでいない {better.playerName} に乗り換えました");
                }
            }
        }
    }
}
