using System.Runtime.CompilerServices;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// スタン無効(バースデーボーイ、ルーム)。スタンを受け付ける EnemyStateStunned.Set を、対象の敵のときだけ無視する。
    /// スタンを決めるのはホストなので、ホストだけの導入で効く。
    /// </summary>
    internal static class StunImmunity
    {
        private enum Kind
        {
            None,
            BirthdayBoy,
            Loom,
        }

        private class KindRef
        {
            internal Kind Value;
        }

        private static readonly ConditionalWeakTable<EnemyStateStunned, KindRef> Kinds = new ConditionalWeakTable<EnemyStateStunned, KindRef>();

        private static Kind KindOf(EnemyStateStunned stunned)
        {
            return Kinds.GetValue(stunned, s =>
            {
                var enemy = s.enemy;
                if (enemy && enemy.GetComponent<EnemyBirthdayBoy>()) return new KindRef { Value = Kind.BirthdayBoy };
                if (enemy && enemy.GetComponent<EnemyShadow>()) return new KindRef { Value = Kind.Loom };
                return new KindRef { Value = Kind.None };
            }).Value;
        }

        private static bool IsImmune(EnemyStateStunned stunned)
        {
            switch (KindOf(stunned))
            {
                case Kind.BirthdayBoy: return BirthdayBoy.Enabled.Value && BirthdayBoy.StunImmune.Value;
                case Kind.Loom: return Loom.Enabled.Value && Loom.StunImmune.Value;
                default: return false;
            }
        }

        /// <summary>設定を ON にしたときにスタン中だったら、本来の流れ(タイマー切れで状態を戻す)ですぐ終わらせる。</summary>
        internal static void EndCurrentStun(Enemy enemy)
        {
            if (enemy && enemy.StateStunned && enemy.StateStunned.stunTimer > 0.05f)
            {
                enemy.StateStunned.stunTimer = 0.05f;
            }
        }

        [HarmonyPatch(typeof(EnemyStateStunned), nameof(EnemyStateStunned.Set))]
        private static class SetPatch
        {
            private static bool Prefix(EnemyStateStunned __instance)
            {
                return !IsImmune(__instance);
            }
        }
    }
}
