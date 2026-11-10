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
    /// - スタン無効: スタンを受け付ける EnemyStateStunned.Set を無視する(ON にした時点でスタン中ならすぐ終わらせる)
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
            (typeof(EnemyBeamer), "ピエロ", null, false),
            (typeof(EnemyFloater), "メンタリスト", null, false),
            (typeof(EnemyHunter), "ハンツマン", null, false),
            (typeof(EnemySlowMouth), "スピューワー", null, false),
            (typeof(EnemySnatcher), "スナッチャー", null, false),
            (typeof(EnemyTricycle), "トライシクル", null, false),
            (typeof(EnemyUpscream), "アップスクリーム", null, false),
            (typeof(EnemyValuableThrower), "ラグラット", null, false),
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
                    StunImmune = cfg.Bind(sec, "スタン無効", stunImmune, $"{name}が銃や爆発、投げつけなどでスタンしない{note}(クラス名 {type.Name})"),
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

        // ---------------- 移動速度 ----------------

        private class AgentMemo
        {
            internal Enemy Enemy;
            internal float Speed, Acceleration;
            internal bool Captured;
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

        /// <summary>既定の速さ(状態が上書きしていないときの速さ)を、元の値 × 倍率 に保つ。</summary>
        [HarmonyPatch(typeof(EnemyNavMeshAgent), nameof(EnemyNavMeshAgent.Update))]
        private static class AgentUpdatePatch
        {
            private static void Prefix(EnemyNavMeshAgent __instance)
            {
                if (!Util.IsAuthority)
                {
                    return;
                }
                var memo = MemoOf(__instance);
                if (!memo.Captured)
                {
                    memo.Captured = true;
                    memo.Speed = __instance.DefaultSpeed;
                    memo.Acceleration = __instance.DefaultAcceleration;
                }
                float multiplier = SpeedMultiplier(memo.Enemy) * ExtraDefaultSpeed(memo.Enemy);
                float speed = memo.Speed * multiplier;
                if (Mathf.Approximately(__instance.DefaultSpeed, speed))
                {
                    return;
                }
                __instance.DefaultSpeed = speed;
                __instance.DefaultAcceleration = memo.Acceleration * Mathf.Max(1f, multiplier);
                if (__instance.OverrideTimer <= 0f && __instance.Agent)
                {
                    __instance.Agent.speed = speed;
                    __instance.Agent.acceleration = __instance.DefaultAcceleration;
                }
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
