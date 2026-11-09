using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ルーム(大柄な黒い衣装。両手で叩きつけて衝撃波を出す)。クラス名は EnemyShadow。
    /// - スタン無効(StunImmunity)
    /// - 叩きつけ(EnableHurtCollider。ホストだけが呼ぶ)のときに、周りのプレイヤーと貴重品へロール効果をかける。
    ///   効果はロールの杖・魔法学校のクリスタルと同じゲーム本来のもの(RollEffect。MOD なしの人にも見える)
    /// </summary>
    internal static class Loom
    {
        private const string Section = "7. ルーム";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> StunImmune;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<float> Duration;
        internal static ConfigEntry<bool> Valuables;
        internal static ConfigEntry<float> SpinSpeed;
        internal static ConfigEntry<float> Push;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ルームの変更を使う");
            StunImmune = cfg.Bind(Section, "スタン無効", true, "銃や爆発、投げつけなどでスタンしない");
            Radius = Util.Float(cfg, Section, "ロール効果の範囲", 6f, 1f, 20f, "叩きつけた位置からこの距離(m)以内が転がる");
            Duration = Util.Float(cfg, Section, "ロール効果の時間", 5f, 0.5f, 15f, "転がされる秒数");
            Valuables = cfg.Bind(Section, "貴重品も転がす", true, "範囲内の貴重品にもロール効果をかける");
            SpinSpeed = Util.Float(cfg, Section, "代わりの効果の回転の速さ", 15f, 1f, 40f,
                "ゲーム本来の効果が使えないとき(シングルプレイなど)の、転がる回転の速さ(ラジアン/秒)");
            Push = Util.Float(cfg, Section, "代わりの効果の外へ押す力", 6f, 0f, 30f,
                "ゲーム本来の効果が使えないときの、叩きつけた位置から外へ転がしていく力");
        }

        private class Memo
        {
            internal float LastTime = -10f;
        }

        private static readonly ConditionalWeakTable<EnemyShadow, Memo> LastSlap = new ConditionalWeakTable<EnemyShadow, Memo>();

        [HarmonyPatch(typeof(EnemyShadow), nameof(EnemyShadow.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyShadow __instance)
            {
                if (Enabled.Value && StunImmune.Value && Util.IsAuthority)
                {
                    StunImmunity.EndCurrentStun(__instance.enemy);
                }
            }
        }

        [HarmonyPatch(typeof(EnemyShadow), nameof(EnemyShadow.EnableHurtCollider))]
        private static class SlapPatch
        {
            private static void Prefix(EnemyShadow __instance, Vector3 _pos)
            {
                if (!Enabled.Value || !Util.IsAuthority)
                {
                    return;
                }
                // 叩きつけの終わり際に毎フレーム呼ばれることがあるので、1 回の叩きつけにつき 1 回だけ
                var memo = LastSlap.GetOrCreateValue(__instance);
                if (Time.time - memo.LastTime < 0.5f)
                {
                    return;
                }
                memo.LastTime = Time.time;
                RollEffect.Apply(_pos, __instance.transform.forward, Radius.Value, Duration.Value, Valuables.Value, SpinSpeed.Value, Push.Value);
                Plugin.Verbose("ルーム: 叩きつけでロール効果をかけました");
            }
        }
    }
}
