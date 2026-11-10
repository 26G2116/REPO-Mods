using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ラグラット(赤ちゃん。近くの貴重品を拾ってプレイヤーに投げつける)。クラス名は EnemyValuableThrower。
    /// - 投げる力: 本来は「物の重さ × 20(上限 100)」。係数と上限を変える
    /// - 射程: 本来は相手が見えていて 3〜5m のときに投げる(近すぎると下がり、遠いと近づく)。最短・最長を変える
    /// - 拾ってから投げるまで: 本来は相手を狙う状態に入ってから 2 秒たつまで投げない。この待ちを変える
    /// - 発見する能力: 視界の距離・視野の角度・気づくまでの速さ
    /// - 狙い: 本来は「正面と相手の方向の中間」に投げる。相手の目の高さへまっすぐ、動きの先読みと落下の補正をして投げる
    /// - 物を探す範囲: 本来はプレイヤーの周り 10m
    /// どれもホストが計算する(視界の判定・投げた物の物理はホストのもの)ので、MOD なしの人にも効く。
    /// </summary>
    internal static class Rugrat
    {
        private const string Section = "23. ラグラット";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ThrowForce;
        internal static ConfigEntry<float> ThrowForceMax;
        internal static ConfigEntry<float> RangeMin;
        internal static ConfigEntry<float> RangeMax;
        internal static ConfigEntry<float> ThrowDelay;
        internal static ConfigEntry<float> VisionRange;
        internal static ConfigEntry<float> VisionAngle;
        internal static ConfigEntry<float> NoticeSpeed;
        internal static ConfigEntry<bool> AccurateAim;
        internal static ConfigEntry<float> SearchRadius;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ラグラットの変更を使う");
            ThrowForce = Util.Float(cfg, Section, "投げる力", 40f, 5f, 200f,
                "投げる力の係数。物の重さ × この値で投げる(ゲーム本来は 20)");
            ThrowForceMax = Util.Float(cfg, Section, "投げる力の上限", 200f, 50f, 1000f,
                "重い物を投げるときの力の上限(ゲーム本来は 100)。係数を上げても重い物が上限で止まらないように");
            RangeMin = Util.Float(cfg, Section, "射程(最短)", 3f, 0.5f, 10f,
                "相手がこれより近いと、下がってから投げる(ゲーム本来は 3m)");
            RangeMax = Util.Float(cfg, Section, "射程(最長)", 10f, 3f, 30f,
                "相手がこれより遠いと、近づいてから投げる(ゲーム本来は 5m)。最短より小さいときは最短と同じ");
            ThrowDelay = Util.Float(cfg, Section, "拾ってから投げるまでの時間", 0.5f, 0f, 8f,
                "拾って相手を狙い始めてから、射程内なら投げ始めるまでの秒数(ゲーム本来は 2)");
            VisionRange = Util.Float(cfg, Section, "視界の距離の倍率", 2f, 0.5f, 5f,
                "プレイヤーに気づく距離の倍率(1 でゲーム本来と同じ)");
            VisionAngle = Util.Float(cfg, Section, "視野の広さの倍率", 1.5f, 0.5f, 3f,
                "見える角度の倍率(1 でゲーム本来と同じ。最大で真後ろまで)");
            NoticeSpeed = Util.Float(cfg, Section, "気づくまでの時間の倍率", 0.5f, 0.1f, 2f,
                "見えてから気づくまでの時間の倍率(小さいほど早く気づく。1 でゲーム本来と同じ)");
            AccurateAim = cfg.Bind(Section, "正確に狙う", true,
                "相手の目の高さへまっすぐ、動きの先読みと落下の補正をして投げる(OFF でゲーム本来の狙い)");
            SearchRadius = Util.Float(cfg, Section, "物を探す範囲", 15f, 3f, 40f,
                "投げる物を探す、プレイヤーの周りの半径(ゲーム本来は 10m)");
        }

        public static float GetThrowForce() => Enabled.Value ? ThrowForce.Value : 20f;
        public static float GetThrowForceMax() => Enabled.Value ? ThrowForceMax.Value : 100f;
        public static float GetRangeMin() => Enabled.Value ? RangeMin.Value : 3f;
        public static float GetRangeMax() => Enabled.Value ? UnityEngine.Mathf.Max(RangeMin.Value, RangeMax.Value) : 5f;
        /// <summary>狙う状態の残り時間(最初 10 秒)がこの値以下になったら投げてよい。本来は 8(= 2 秒後)。</summary>
        public static float GetSearchRadius() => Enabled.Value ? SearchRadius.Value : 10f;
        public static float GetThrowAllowedAt() => Enabled.Value ? 10f - ThrowDelay.Value : 8f;

        /// <summary>
        /// 狙う状態: 「&lt; 3f」(近すぎ)を最短、「&gt; 5f」(遠すぎ)を最長、「&lt;= 8f」(投げてよい時刻)を差し替える。
        /// 3f は 2 回出てくる(1 回目が距離の判定、2 回目は下がる距離)ので 1 回目だけ。
        /// </summary>
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.StateTargetPlayer))]
        private static class TargetPlayerPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                const string where = "EnemyValuableThrower.StateTargetPlayer";
                var a = Transpile.ReplaceFloat(instructions, 3f, AccessTools.Method(typeof(Rugrat), nameof(GetRangeMin)), where + "(最短)", 1);
                var b = Transpile.ReplaceFloat(a, 5f, AccessTools.Method(typeof(Rugrat), nameof(GetRangeMax)), where + "(最長)");
                return Transpile.ReplaceFloat(b, 8f, AccessTools.Method(typeof(Rugrat), nameof(GetThrowAllowedAt)), where + "(投げるまで)");
            }
        }

        /// <summary>投げる: 「20f * 重さ」と「上限 100f」を差し替える。</summary>
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.Throw))]
        private static class ThrowPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                const string where = "EnemyValuableThrower.Throw";
                var a = Transpile.ReplaceFloat(instructions, 20f, AccessTools.Method(typeof(Rugrat), nameof(GetThrowForce)), where + "(力)");
                return Transpile.ReplaceFloat(a, 100f, AccessTools.Method(typeof(Rugrat), nameof(GetThrowForceMax)), where + "(上限)");
            }
        }

        // ---------------- 発見する能力 ----------------

        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyValuableThrower __instance)
            {
                if (!Util.IsAuthority || !__instance.enemy || !__instance.enemy.Vision)
                {
                    return;
                }
                bool on = Enabled.Value;
                Util.ScaleVision(__instance.enemy.Vision, on ? VisionRange.Value : 1f);
                Util.ScaleVisionSense(__instance.enemy.Vision, on ? VisionAngle.Value : 1f, on ? NoticeSpeed.Value : 1f);
            }
        }

        // ---------------- 物を探す範囲 ----------------

        /// <summary>物を探す: OverlapSphere(プレイヤーの位置, 10f, ...) の 10f を差し替える。</summary>
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.StateGetValuable))]
        private static class GetValuablePatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpile.ReplaceFloat(instructions, 10f, AccessTools.Method(typeof(Rugrat), nameof(GetSearchRadius)), "EnemyValuableThrower.StateGetValuable");
            }
        }

        // ---------------- 正確に狙う ----------------

        /// <summary>
        /// 投げる処理(アニメーションのイベントで呼ばれる)を、狙いだけ変えて同じ手順で行う。
        /// 力の大きさは本来と同じ計算(本来は向きの長さが距離で変わるので、その長さも同じにする)。
        /// </summary>
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.Throw))]
        private static class AimPatch
        {
            private const float MaxObjectSpeed = 40f;

            private static bool Prefix(EnemyValuableThrower __instance)
            {
                if (!Enabled.Value || !AccurateAim.Value)
                {
                    return true;
                }
                var valuable = __instance.valuableTarget;
                var player = __instance.playerTarget;
                if (!valuable || !player)
                {
                    return false; // 本来も何もしない
                }
                if (SemiFunc.IsMasterClientOrSingleplayer())
                {
                    foreach (var grabber in valuable.playerGrabbing.ToList())
                    {
                        if (!SemiFunc.IsMultiplayer())
                        {
                            grabber.ReleaseObject(valuable.photonView.ViewID, 0.5f);
                        }
                        else
                        {
                            grabber.OverrideGrabRelease(valuable.photonView.ViewID, 0.5f);
                        }
                    }
                }
                Vector3 from = valuable.centerPoint;
                Vector3 aim = player.PlayerVisionTarget.VisionTransform.position;
                // 本来の向き(この長さで力が決まる)
                Vector3 vanilla = Vector3.Lerp(__instance.transform.forward, aim - from, 0.5f);

                valuable.ResetMass();
                float force = Mathf.Min(GetThrowForce() * valuable.rb.mass, GetThrowForceMax());
                // 物の速さはゲーム側で秒速 40m までに抑えられる(PhysGrabObject.FixedUpdate)
                float speed = valuable.rb.mass > 0f ? Mathf.Min(vanilla.magnitude * force / valuable.rb.mass, MaxObjectSpeed) : 0f;
                if (speed > 0.1f)
                {
                    // 相手の動きを先読みし、落ちる分だけ上を狙う
                    // (持っている間は重力を切っているが、手放すとすぐ戻るので、重力はあるものとして計算する)
                    float time = Vector3.Distance(from, aim) / speed;
                    aim += player.rbVelocityRaw * time;
                    time = Vector3.Distance(from, aim) / speed;
                    aim += Vector3.up * (0.5f * -Physics.gravity.y * time * time);
                }
                Vector3 direction = (aim - from).normalized * vanilla.magnitude;

                valuable.ResetIndestructible();
                valuable.rb.AddForce(direction * force, ForceMode.Impulse);
                valuable.rb.AddTorque(valuable.transform.right * 0.5f, ForceMode.Impulse);
                valuable.impactDetector.PlayerHurtMultiplier(5f, 2f);
                __instance.valuableTarget = null;
                return false;
            }
        }
    }
}
