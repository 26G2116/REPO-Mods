using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// すべてのモンスターに個別に設定できる共通の項目: スタン無効、移動速度倍率。ホストが判断・計算するので、ホストだけの導入で効く。
    /// - スタン無効: スタンを受け付ける EnemyStateStunned.Set を無視する(ON にした時点でスタン中ならすぐ終わらせる)。
    ///   攻撃で押されはするが、体を傾ける回転を消してすぐ起こすので倒れない
    /// - 移動速度倍率: ナビメッシュの速さ(既定値と、状態ごとの一時的な上書き)と、体がそれを追いかける速さにかける。
    ///   ナビメッシュを使わずに動く動き(飛びかかり・空中移動など)には効かないことがある
    /// 設定は、すでに個別の変更があるモンスターはそのセクションに、それ以外は「モンスター: ○○」に置く。
    /// </summary>
    internal static class EnemyCommon
    {
        internal class Entry
        {
            internal Type Type;
            internal string Name;
            internal ConfigEntry<bool> StunImmune;
            internal ConfigEntry<float> Speed;
            /// <summary>個別の変更がある敵の「有効」。OFF なら共通設定も効かない(個別の変更がない敵は null)。</summary>
            internal ConfigEntry<bool> SectionEnabled;

            internal bool On => SectionEnabled == null || SectionEnabled.Value;
        }

        /// <summary>(操作クラス, 表示名, 既存のセクション, スタン無効の既定値)</summary>
        private static readonly (Type type, string name, string section, bool stunImmune)[] Definitions =
        {
            (typeof(EnemyElsa), "エルサ", "1. エルサ", false),
            (typeof(EnemyDuck), "APEX プレデター", "2. APEX プレデター", false),
            (typeof(EnemySlowWalker), "トラッジ", "3. トラッジ", false),
            (typeof(EnemyThinMan), "シャドーチャイルド", "4. シャドーチャイルド", false),
            (typeof(EnemyRobe), "ローブ", "5. ローブ", false),
            (typeof(EnemyBirthdayBoy), "バースデーボーイ", "6. バースデーボーイ", true),
            (typeof(EnemyShadow), "ルーム", "7. ルーム", true),
            (typeof(EnemyBombThrower), "クリーンアップクルー", "8. クリーンアップクルー", false),
            (typeof(EnemySpinny), "ギャンビット", "9. ギャンビット", false),
            (typeof(EnemyHeadGrabber), "ヘッドグラブ", "10. ヘッドグラブ", false),
            (typeof(EnemyHeartHugger), "ハートハガー", "11. ハートハガー", true),
            (typeof(EnemyAnimal), "アニマル", "12. アニマル", true),
            (typeof(EnemyTick), "ティック", "13. ティック", false),
            (typeof(EnemyOogly), "ウーグリー", "14. ウーグリー", false),
            (typeof(EnemyBang), "バンガー", "15. バンガー", false),
            (typeof(EnemyBowtie), "ボウタイ", "16. ボウタイ", false),
            (typeof(EnemyTumbler), "シェフ", "17. シェフ", false),
            (typeof(EnemyGnome), "ノーム", "18. ノーム", false),
            (typeof(EnemyHeadController), "ヘッドマン", "19. ヘッドマン", false),
            (typeof(EnemyHidden), "ヒドゥン", "20. ヒドゥン", false),
            (typeof(EnemyCeilingEye), "ピーパー", "21. ピーパー", false),
            (typeof(EnemyRunner), "リーパー", "22. リーパー", false),
            (typeof(EnemyValuableThrower), "ラグラット", "23. ラグラット", false),
            (typeof(EnemySlowMouth), "スピュワー", "24. スピュワー", false),
            (typeof(EnemyUpscream), "アップスクリーム", "25. アップスクリーム", false),
            (typeof(EnemyTricycle), "ベル", "26. ベル", false),
            (typeof(EnemySnatcher), "スナッチャー(ポニー)", "27. スナッチャー", false),
            (typeof(EnemyBeamer), "ピエロ", null, false),
            (typeof(EnemyFloater), "メンタリスト", null, false),
            (typeof(EnemyHunter), "ハンツマン", null, false),
            (typeof(EnemyVirus), "ウイルス", null, false),
        };

        private static readonly List<Entry> Entries = new List<Entry>();

        /// <summary>個別の変更がある敵の「有効」(各敵の Bind のあとに呼ぶこと)。</summary>
        private static ConfigEntry<bool> SectionEnabledOf(Type type)
        {
            if (type == typeof(EnemyElsa)) return Elsa.Enabled;
            if (type == typeof(EnemyDuck)) return ApexPredator.Enabled;
            if (type == typeof(EnemySlowWalker)) return Trudge.Enabled;
            if (type == typeof(EnemyThinMan)) return ShadowChild.Enabled;
            if (type == typeof(EnemyRobe)) return Robe.Enabled;
            if (type == typeof(EnemyBirthdayBoy)) return BirthdayBoy.Enabled;
            if (type == typeof(EnemyShadow)) return Loom.Enabled;
            if (type == typeof(EnemyBombThrower)) return CleanupCrew.Enabled;
            if (type == typeof(EnemySpinny)) return Gambit.Enabled;
            if (type == typeof(EnemyHeadGrabber)) return HeadGrab.Enabled;
            if (type == typeof(EnemyHeartHugger)) return HeartHugger.Enabled;
            if (type == typeof(EnemyAnimal)) return Animal.Enabled;
            if (type == typeof(EnemyTick)) return Tick.Enabled;
            if (type == typeof(EnemyOogly)) return Oogly.Enabled;
            if (type == typeof(EnemyBang)) return Banger.Enabled;
            if (type == typeof(EnemyBowtie)) return Bowtie.Enabled;
            if (type == typeof(EnemyTumbler)) return Chef.Enabled;
            if (type == typeof(EnemyGnome)) return Gnome.Enabled;
            if (type == typeof(EnemyHeadController)) return Headman.Enabled;
            if (type == typeof(EnemyHidden)) return Hidden.Enabled;
            if (type == typeof(EnemyCeilingEye)) return Peeper.Enabled;
            if (type == typeof(EnemyRunner)) return Reaper.Enabled;
            if (type == typeof(EnemyValuableThrower)) return Rugrat.Enabled;
            if (type == typeof(EnemySlowMouth)) return Spewer.Enabled;
            if (type == typeof(EnemyUpscream)) return Upscream.Enabled;
            if (type == typeof(EnemyTricycle)) return Bell.Enabled;
            if (type == typeof(EnemySnatcher)) return Snatcher.Enabled;
            return null;
        }

        internal static void Bind(ConfigFile cfg)
        {
            foreach (var (type, name, section, stunImmune) in Definitions)
            {
                string sec = section ?? $"モンスター: {name}";
                string note = section != null ? "(このセクションの「有効」が OFF のときは効かない)" : "";
                Entries.Add(new Entry
                {
                    Type = type,
                    Name = name,
                    SectionEnabled = SectionEnabledOf(type),
                    StunImmune = cfg.Bind(sec, "スタン無効", stunImmune, $"{name}が銃や爆発、投げつけなどでスタンしない。押されはするが倒れない{note}(クラス名 {type.Name})"),
                    Speed = Util.Float(cfg, sec, "移動速度倍率(共通)", 1f, 0.2f, 5f,
                        $"{name}の移動の速さの倍率(1 でゲーム本来と同じ)。個別の速度設定がある場合はそれにさらにかかる。" +
                        "ナビメッシュを使わない動き(飛びかかり・空中移動など)には効かないことがある" + note),
                });
            }
        }

        private class EntryRef
        {
            internal Entry Entry;
        }

        private static readonly ConditionalWeakTable<Enemy, EntryRef> ByEnemy = new ConditionalWeakTable<Enemy, EntryRef>();

        /// <summary>この敵の種類の設定(一覧にない敵なら null)。</summary>
        internal static Entry For(Enemy enemy)
        {
            if (!enemy)
            {
                return null;
            }
            return ByEnemy.GetValue(enemy, e =>
            {
                var root = e.EnemyParent ? (Component)e.EnemyParent : e;
                foreach (var entry in Entries)
                {
                    if (root.GetComponentInChildren(entry.Type, true))
                    {
                        return new EntryRef { Entry = entry };
                    }
                }
                return new EntryRef();
            }).Entry;
        }

        internal static bool IsStunImmune(Enemy enemy)
        {
            var entry = For(enemy);
            return entry != null && entry.On && entry.StunImmune.Value;
        }

        internal static float SpeedMultiplier(Enemy enemy)
        {
            var entry = For(enemy);
            return entry != null && entry.On ? entry.Speed.Value : 1f;
        }

        /// <summary>ナビメッシュの既定速度にだけかける追加の倍率(トラッジの「うろつくときの速度倍率」など)。</summary>
        internal static Func<Enemy, float> ExtraDefaultSpeed = _ => 1f;

        // ---------------- スタン無効 ----------------

        [HarmonyPatch(typeof(EnemyStateStunned), nameof(EnemyStateStunned.Set))]
        private static class StunSetPatch
        {
            private static bool Prefix(EnemyStateStunned __instance)
            {
                return !IsStunImmune(__instance.enemy);
            }
        }

        [HarmonyPatch(typeof(EnemyStateStunned), nameof(EnemyStateStunned.Update))]
        private static class StunUpdatePatch
        {
            private static void Prefix(EnemyStateStunned __instance)
            {
                // ON にした時点でスタン中だったら、本来の流れ(タイマー切れで状態を戻す)ですぐ終わらせる
                if (__instance.stunTimer > 0.05f && Util.IsAuthority && IsStunImmune(__instance.enemy))
                {
                    __instance.stunTimer = 0.05f;
                }
            }
        }

        /// <summary>傾いた体を起こす速さ(度/秒)。90 度倒れても 0.2 秒ほどで立ち直る。</summary>
        private const float UprightSpeed = 450f;

        /// <summary>
        /// スタン無効の敵は、攻撃で押されても倒れない。押される力(移動)はそのまま残し、体を傾ける回転だけを毎フレーム消して、
        /// 傾いていたら本来の向き(followTarget)へすぐ起こす。縦軸まわりの回転(向きを変える)は残す。
        /// 回転の原因(銃・爆発・投げつけ・落下・ぶつかりなど)を個別に直さず、結果の傾きをまとめて直す。
        /// 敵自身の技で体を傾けているとき(本来の向きへ戻す力を切っているとき)と、プレイヤーがつかんでいるときは何もしない。
        /// </summary>
        [HarmonyPatch(typeof(EnemyRigidbody), nameof(EnemyRigidbody.FixedUpdate))]
        private static class UprightPatch
        {
            private static void Postfix(EnemyRigidbody __instance)
            {
                if (!Util.IsAuthority || !__instance.enemy || !__instance.rb || __instance.rb.isKinematic || !__instance.followTarget
                    || !IsStunImmune(__instance.enemy))
                {
                    return;
                }
                if (__instance.enemy.FreezeTimer > 0f || __instance.enemy.CurrentState == EnemyState.Despawn
                    || __instance.disableFollowRotationTimer > 0f || __instance.deactivateFollowTargetPhysicsTimer > 0f
                    || (__instance.physGrabObject && __instance.physGrabObject.playerGrabbing.Count > 0))
                {
                    return;
                }
                var rb = __instance.rb;
                Vector3 up = __instance.followTarget.up;
                // 傾ける回転を消す(縦軸まわりだけ残す)
                rb.angularVelocity = Vector3.Project(rb.angularVelocity, up);
                Vector3 current = rb.rotation * Vector3.up;
                float tilt = Vector3.Angle(current, up);
                if (tilt > 1f)
                {
                    Quaternion upright = Quaternion.FromToRotation(current, up) * rb.rotation;
                    rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, upright, UprightSpeed * Time.fixedDeltaTime));
                }
            }
        }

        // ---------------- 移動速度 ----------------

        private class AgentMemo
        {
            internal Enemy Enemy;
            /// <summary>最後に既定の速さへかけた倍率(1 = 何もしていない)。</summary>
            internal float Applied = 1f;
        }

        private static readonly ConditionalWeakTable<EnemyNavMeshAgent, AgentMemo> Agents = new ConditionalWeakTable<EnemyNavMeshAgent, AgentMemo>();

        private static AgentMemo MemoOf(EnemyNavMeshAgent agent)
        {
            return Agents.GetValue(agent, a =>
            {
                var enemy = a.GetComponent<Enemy>();
                return new AgentMemo { Enemy = enemy ? enemy : a.GetComponentInParent<Enemy>() };
            });
        }

        /// <summary>
        /// 既定の速さ(状態が上書きしていないときの速さ)に倍率をかける。
        /// DefaultSpeed そのものは書き換えない(多くの敵が「DefaultSpeed × 2」などを OverrideAgent に渡していて、
        /// そこでも倍率がかかるので、DefaultSpeed に入れておくと二重にかかってしまう)。
        /// 代わりに、本来の処理が上書きの終わりに速さを DefaultSpeed へ戻した瞬間と、倍率が変わったときに、実際の速さへかける。
        /// </summary>
        [HarmonyPatch(typeof(EnemyNavMeshAgent), nameof(EnemyNavMeshAgent.Update))]
        private static class AgentUpdatePatch
        {
            private static void Prefix(EnemyNavMeshAgent __instance, out bool __state)
            {
                __state = __instance.OverrideTimer > 0f;
            }

            private static void Postfix(EnemyNavMeshAgent __instance, bool __state)
            {
                if (!Util.IsAuthority || !__instance.Agent || __instance.OverrideTimer > 0f)
                {
                    return;
                }
                var memo = MemoOf(__instance);
                float multiplier = SpeedMultiplier(memo.Enemy) * ExtraDefaultSpeed(memo.Enemy);
                bool reset = __state; // この更新で上書きが終わり、本来の処理が速さを DefaultSpeed に戻した
                if (!reset && Mathf.Approximately(memo.Applied, multiplier))
                {
                    return;
                }
                memo.Applied = multiplier;
                __instance.Agent.speed = __instance.DefaultSpeed * multiplier;
                __instance.Agent.acceleration = __instance.DefaultAcceleration * Mathf.Max(1f, multiplier);
            }
        }

        /// <summary>状態ごとの一時的な速さの上書きにも倍率をかける。</summary>
        [HarmonyPatch(typeof(EnemyNavMeshAgent), nameof(EnemyNavMeshAgent.OverrideAgent))]
        private static class OverrideAgentPatch
        {
            private static void Postfix(EnemyNavMeshAgent __instance)
            {
                ScaleAgent(__instance);
            }
        }

        [HarmonyPatch(typeof(EnemyNavMeshAgent), nameof(EnemyNavMeshAgent.UpdateAgent))]
        private static class UpdateAgentPatch
        {
            private static void Postfix(EnemyNavMeshAgent __instance)
            {
                ScaleAgent(__instance);
            }
        }

        private static void ScaleAgent(EnemyNavMeshAgent agent)
        {
            if (!Util.IsAuthority || !agent.Agent)
            {
                return;
            }
            float multiplier = SpeedMultiplier(MemoOf(agent).Enemy);
            if (Mathf.Approximately(multiplier, 1f))
            {
                return;
            }
            agent.Agent.speed *= multiplier;
            agent.Agent.acceleration *= Mathf.Max(1f, multiplier);
        }

        /// <summary>体(リジッドボディ)がナビメッシュの位置を追いかける速さにも倍率をかける(毎フレーム決め直されるので毎回かける)。</summary>
        [HarmonyPatch(typeof(EnemyRigidbody), nameof(EnemyRigidbody.Update))]
        private static class RigidbodyUpdatePatch
        {
            private static void Postfix(EnemyRigidbody __instance)
            {
                if (!Util.IsAuthority)
                {
                    return;
                }
                float multiplier = SpeedMultiplier(__instance.enemy);
                if (!Mathf.Approximately(multiplier, 1f))
                {
                    __instance.positionSpeed *= multiplier;
                }
            }
        }
    }
}
