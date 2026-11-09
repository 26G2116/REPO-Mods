using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ルーム(大柄な黒い衣装。両手で叩きつけて衝撃波を出す)。クラス名は EnemyShadow。
    /// 叩きつけ(EnableHurtCollider。ホストだけが呼ぶ)のときに、周りのプレイヤーへロール効果をかける。
    /// </summary>
    internal static class Loom
    {
        private const string Section = "7. ルーム";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<float> Duration;
        internal static ConfigEntry<float> SpinSpeed;
        internal static ConfigEntry<float> Push;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ルームの変更を使う(叩きつけの衝撃でロール効果)");
            Radius = Util.Float(cfg, Section, "ロール効果の範囲", 6f, 1f, 20f, "叩きつけた位置からこの距離(m)以内のプレイヤーが転がる");
            Duration = Util.Float(cfg, Section, "ロール効果の時間", 3f, 0.5f, 15f, "転がされて起き上がれない秒数");
            SpinSpeed = Util.Float(cfg, Section, "回転の速さ", 15f, 1f, 40f, "転がる回転の速さ(ラジアン/秒)");
            Push = Util.Float(cfg, Section, "外へ押す力", 6f, 0f, 30f, "叩きつけた位置から外へ転がしていく力。0 ならその場で回るだけ");
        }

        private class Memo
        {
            internal float LastTime = -10f;
        }

        private static readonly ConditionalWeakTable<EnemyShadow, Memo> LastSlap = new ConditionalWeakTable<EnemyShadow, Memo>();

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
                RollEffect.Apply(_pos, Radius.Value, Duration.Value, SpinSpeed.Value, Push.Value);
                Plugin.Verbose("ルーム: 叩きつけでロール効果をかけました");
            }
        }
    }
}
