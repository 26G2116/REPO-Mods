using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// バースデーボーイ(風船を置いていく。風船を割られると激怒して襲ってくる)。
    /// - スタン無効: スタンを受け付ける EnemyStateStunned.Set を、バースデーボーイのときだけ無視する
    /// - つかまれたら即激怒: つかんだ人をターゲットにして、風船を割られたときと同じ GoToPlayerAngry へ
    /// どちらもホストが判断し、状態は RPC で全員に伝わるので、ホストだけの導入で効く。
    /// </summary>
    internal static class BirthdayBoy
    {
        private const string Section = "6. バースデーボーイ";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> StunImmune;
        internal static ConfigEntry<bool> RageOnGrab;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "バースデーボーイの変更を使う");
            StunImmune = cfg.Bind(Section, "スタン無効", true, "銃や爆発、投げつけなどでスタンしない");
            RageOnGrab = cfg.Bind(Section, "つかまれたら激怒", true, "プレイヤーにつかまれた瞬間、風船を割られたときと同じ激怒状態でその人を襲う");
        }

        private static bool StunImmuneActive => Enabled.Value && StunImmune.Value;

        private class BoyRef
        {
            internal bool IsBoy;
        }

        private static readonly ConditionalWeakTable<EnemyStateStunned, BoyRef> IsBoyCache = new ConditionalWeakTable<EnemyStateStunned, BoyRef>();

        private static bool CanRage(EnemyBirthdayBoy boy)
        {
            switch (boy.currentState)
            {
                case EnemyBirthdayBoy.State.Spawn:
                case EnemyBirthdayBoy.State.Despawn:
                case EnemyBirthdayBoy.State.GoToPlayerAngry:
                case EnemyBirthdayBoy.State.FlyBackUp:
                case EnemyBirthdayBoy.State.FlyBackToNavMesh:
                    return false;
                default:
                    return !boy.AttackState();
            }
        }

        [HarmonyPatch(typeof(EnemyStateStunned), nameof(EnemyStateStunned.Set))]
        private static class StunSetPatch
        {
            private static bool Prefix(EnemyStateStunned __instance)
            {
                if (!StunImmuneActive)
                {
                    return true;
                }
                bool isBoy = IsBoyCache.GetValue(__instance, s => new BoyRef { IsBoy = s.enemy && s.enemy.GetComponent<EnemyBirthdayBoy>() }).IsBoy;
                return !isBoy;
            }
        }

        [HarmonyPatch(typeof(EnemyBirthdayBoy), nameof(EnemyBirthdayBoy.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyBirthdayBoy __instance)
            {
                var enemy = __instance.enemy;
                if (!Enabled.Value || !Util.IsAuthority || !enemy)
                {
                    return;
                }
                // 設定を ON にしたときにスタン中だったら、本来の流れ(タイマー切れで状態を戻す)ですぐ終わらせる
                if (StunImmune.Value && enemy.StateStunned && enemy.StateStunned.stunTimer > 0.05f)
                {
                    enemy.StateStunned.stunTimer = 0.05f;
                }
                if (!RageOnGrab.Value || !enemy.Rigidbody || !enemy.Rigidbody.physGrabObject)
                {
                    return;
                }
                var grabbing = enemy.Rigidbody.physGrabObject.playerGrabbing;
                if (grabbing.Count == 0 || !CanRage(__instance))
                {
                    return;
                }
                var grabber = grabbing[0] ? grabbing[0].playerAvatar : null;
                if (!grabber || grabber.isDisabled)
                {
                    return;
                }
                __instance.UpdatePlayerTarget(grabber);
                __instance.replaceOldBalloons = false;
                __instance.UpdateState(EnemyBirthdayBoy.State.GoToPlayerAngry);
                Plugin.Verbose($"バースデーボーイ: {grabber.playerName} につかまれて激怒しました");
            }
        }
    }
}
