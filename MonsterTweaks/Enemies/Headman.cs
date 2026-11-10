using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace MonsterTweaks
{
    /// <summary>
    /// ヘッドマン(浮かぶ大きな頭)。クラス名は EnemyHeadController(動きは共通の Enemy の状態で動く)。
    /// - ライト: プレイヤーの懐中電灯の光(立っているときに前を照らすもの)が当たったら、背後からでもすぐ気づく。
    ///   ゲーム本来の「見つけた」処理(EnemyVision.VisionTrigger)を呼ぶので、その後の追跡は本来どおり
    /// - 死亡時: その場にバンガーを呼び出す。本物の敵として生成するので MOD なしの人にも見える。
    ///   呼び出したバンガーは、一度退場したら再出現しない(設定で変更可)
    /// </summary>
    internal static class Headman
    {
        private const string Section = "19. ヘッドマン";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> LightDetect;
        internal static ConfigEntry<float> LightRange;
        internal static ConfigEntry<int> SummonCount;
        internal static ConfigEntry<bool> SummonOnce;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ヘッドマンの変更を使う");
            LightDetect = cfg.Bind(Section, "ライトを当てられたら気づく", true,
                "プレイヤーの懐中電灯の光が当たったら、背後からでもすぐそのプレイヤーに気づく");
            LightRange = Util.Float(cfg, Section, "ライトに気づく距離", 20f, 2f, 60f,
                "この距離(m)以内で光が当たったら気づく(懐中電灯の光が届く距離より遠くには反応しない)");
            SummonCount = Util.Int(cfg, Section, "死亡時に呼び出すバンガーの数", 1, 0, 5,
                "倒されたときにその場へ呼び出すバンガーの数(0 で呼び出さない)");
            SummonOnce = cfg.Bind(Section, "呼び出したバンガーは一度きり", true,
                "呼び出したバンガーが退場(爆発・倒される・時間切れ)したら、もう出現しない。OFF にすると普通のバンガーとして残り続ける");
        }

        // ---------------- ライトに気づく ----------------

        private class LightMemo
        {
            internal float Timer;
        }

        private static readonly ConditionalWeakTable<EnemyHeadController, LightMemo> Lights = new ConditionalWeakTable<EnemyHeadController, LightMemo>();

        [HarmonyPatch(typeof(EnemyHeadController), nameof(EnemyHeadController.Update))]
        private static class UpdatePatch
        {
            private static void Postfix(EnemyHeadController __instance)
            {
                if (!Enabled.Value || !LightDetect.Value || !Util.IsAuthority)
                {
                    return;
                }
                var memo = Lights.GetOrCreateValue(__instance);
                memo.Timer -= Time.deltaTime;
                if (memo.Timer > 0f)
                {
                    return;
                }
                memo.Timer = 0.2f;
                CheckLights(__instance.Enemy);
            }
        }

        private static void CheckLights(Enemy enemy)
        {
            if (!enemy || !enemy.HasVision || !enemy.Vision || enemy.DisableChaseTimer > 0f)
            {
                return;
            }
            var state = enemy.CurrentState;
            if (state == EnemyState.Spawn || state == EnemyState.Despawn || state == EnemyState.Stunned)
            {
                return;
            }
            Vector3 target = enemy.CenterTransform ? enemy.CenterTransform.position : enemy.transform.position;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                // すでにこの人を追いかけているなら何もしない
                if ((state == EnemyState.Chase || state == EnemyState.ChaseBegin) && enemy.TargetPlayerAvatar == player)
                {
                    continue;
                }
                if (!IsLitBy(player, target))
                {
                    continue;
                }
                int id = player.photonView.ViewID;
                if (!enemy.Vision.VisionTriggered.ContainsKey(id))
                {
                    continue;
                }
                enemy.Vision.onVisionTriggeredDistance = Vector3.Distance(enemy.Vision.VisionTransform.position, player.PlayerVisionTarget.VisionTransform.position);
                enemy.Vision.VisionTrigger(id, player, false, false);
                Plugin.Verbose($"ヘッドマン: {player.playerName} のライトに気づきました");
                return;
            }
        }

        /// <summary>プレイヤーの懐中電灯の光が target に当たっているか(光の円すいの中で、間に壁がない)。</summary>
        private static bool IsLitBy(PlayerAvatar player, Vector3 target)
        {
            var flashlight = player.flashlightController;
            if (!flashlight || !flashlight.LightActive || !flashlight.spotlight || !flashlight.spotlight.enabled)
            {
                return false;
            }
            var light = flashlight.spotlight;
            Vector3 origin = light.transform.position;
            Vector3 toTarget = target - origin;
            float distance = toTarget.magnitude;
            if (distance > Mathf.Min(light.range, LightRange.Value) || distance < 0.01f)
            {
                return false;
            }
            if (Vector3.Angle(light.transform.forward, toTarget) > light.spotAngle * 0.5f + 3f)
            {
                return false;
            }
            return !Physics.Linecast(origin, target, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
        }

        // ---------------- 死亡時にバンガーを呼び出す ----------------

        [HarmonyPatch(typeof(EnemyHeadController), nameof(EnemyHeadController.OnDeath))]
        private static class DeathPatch
        {
            private static void Postfix(EnemyHeadController __instance)
            {
                if (!Enabled.Value || !Util.IsAuthority || SummonCount.Value <= 0 || !__instance.Enemy)
                {
                    return;
                }
                try
                {
                    Summon(__instance.Enemy.transform.position, SummonCount.Value);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"ヘッドマン: バンガーの呼び出しに失敗しました: {e.Message}");
                }
            }
        }

        /// <summary>呼び出したバンガー → 出現させる場所(出現前)。</summary>
        private static readonly Dictionary<EnemyParent, Vector3> Pending = new Dictionary<EnemyParent, Vector3>();
        /// <summary>呼び出したバンガー(一度きりなら、退場後は再出現させない)。</summary>
        private static readonly HashSet<EnemyParent> Summoned = new HashSet<EnemyParent>();

        private static PrefabRef _bangRef, _directorRef;
        private static bool _searched;

        private static void FindBangPrefabs()
        {
            if (_searched && _bangRef != null)
            {
                return;
            }
            _searched = true;
            var director = EnemyDirector.instance;
            if (!director)
            {
                return;
            }
            foreach (var list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
            {
                if (list == null) continue;
                foreach (var setup in list)
                {
                    if (!setup || setup.spawnObjects == null) continue;
                    PrefabRef bang = null, bangDirector = null;
                    foreach (var spawn in setup.spawnObjects)
                    {
                        var prefab = spawn?.Prefab;
                        if (!prefab) continue;
                        if (bang == null && prefab.GetComponentInChildren<EnemyBang>(true)) bang = spawn;
                        if (bangDirector == null && prefab.GetComponentInChildren<EnemyBangDirector>(true)) bangDirector = spawn;
                    }
                    if (bang != null)
                    {
                        _bangRef = bang;
                        _directorRef = bangDirector ?? _directorRef;
                        if (_directorRef != null)
                        {
                            return;
                        }
                    }
                }
            }
            if (_bangRef == null)
            {
                Plugin.Log.LogWarning("ヘッドマン: バンガーの敵が見つからないため、呼び出せません");
            }
        }

        private static void Summon(Vector3 position, int count)
        {
            Cleanup();
            FindBangPrefabs();
            if (_bangRef == null || !LevelGenerator.Instance)
            {
                return;
            }
            // バンガーは群れをまとめる EnemyBangDirector がないと動かない。この階にいなければ先に作る
            if (!EnemyBangDirector.instance)
            {
                if (_directorRef == null)
                {
                    Plugin.Log.LogWarning("ヘッドマン: バンガーのまとめ役が見つからないため、呼び出せません");
                    return;
                }
                VanillaAssets.Spawn(_directorRef, position, Quaternion.identity);
            }
            for (int i = 0; i < count; i++)
            {
                Vector3 spot = position + (count > 1 ? Quaternion.Euler(0f, 360f * i / count, 0f) * Vector3.forward * 1.2f : Vector3.zero);
                if (NavMesh.SamplePosition(spot, out var hit, 3f, -1))
                {
                    spot = hit.position;
                }
                var obj = VanillaAssets.Spawn(_bangRef, spot, Quaternion.identity);
                var parent = obj ? obj.GetComponent<EnemyParent>() : null;
                if (!parent)
                {
                    Plugin.Log.LogWarning("ヘッドマン: バンガーの生成に失敗しました");
                    continue;
                }
                // LevelGenerator.EnemySpawnObject と同じ準備(生成後の数合わせ EnemiesSpawnTarget は増やさない。
                // 増やすと、あとから生成した敵にプレイヤーが登録されなくなる)
                parent.SetupDone = true;
                var enemy = obj.GetComponentInChildren<Enemy>();
                if (enemy)
                {
                    enemy.EnemyTeleported(spot);
                }
                Pending[parent] = spot;
                Summoned.Add(parent);
            }
            Plugin.Verbose($"ヘッドマン: バンガーを {count} 体呼び出しました");
        }

        /// <summary>呼び出したバンガーをすぐ出現させ、一度きりなら退場後は出現させない。</summary>
        [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Update))]
        private static class ParentUpdatePatch
        {
            private static void Postfix(EnemyParent __instance)
            {
                if (Summoned.Count == 0 || !Util.IsAuthority || !Summoned.Contains(__instance) || __instance.Spawned)
                {
                    return;
                }
                if (Pending.ContainsKey(__instance))
                {
                    __instance.DespawnedTimer = Mathf.Min(__instance.DespawnedTimer, 0.3f);
                }
                else if (SummonOnce.Value)
                {
                    __instance.DespawnedTimer = Mathf.Max(__instance.DespawnedTimer, 99999f);
                }
            }
        }

        /// <summary>出現場所: 呼び出したバンガーの最初の出現は、ヘッドマンが倒れた場所にする。</summary>
        [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemySpawn))]
        private static class EnemySpawnPatch
        {
            private static bool Prefix(Enemy enemy, ref bool __result)
            {
                if (Pending.Count == 0 || !enemy || !enemy.EnemyParent || !Pending.TryGetValue(enemy.EnemyParent, out var spot))
                {
                    return true;
                }
                Pending.Remove(enemy.EnemyParent);
                enemy.EnemyTeleported(spot);
                enemy.EnemyParent.firstSpawnPointUsed = true;
                __result = true;
                return false;
            }
        }

        /// <summary>前の階で呼び出した(もう消えた)バンガーの記録を消す。</summary>
        private static void Cleanup()
        {
            var gone = new List<EnemyParent>();
            foreach (var key in Pending.Keys)
            {
                if (!key) gone.Add(key);
            }
            foreach (var key in gone)
            {
                Pending.Remove(key);
            }
            Summoned.RemoveWhere(p => !p);
        }
    }
}
