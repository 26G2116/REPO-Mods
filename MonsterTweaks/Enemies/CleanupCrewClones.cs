using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// クリーンアップクルーの「分身と入れ替わる」方式(ホストだけで動く。分身は本物の敵なので MOD なしの人にもそのまま見える)。
    /// - 1 体ごとに「群れ」を作り、同じ種類の敵を分身として生成する
    /// - 出番でない体は、マップのはるか上空(y=3000 付近)で凍結し、AI を止めておく(頭の再生だけは進める)
    /// - 見えている体が顔爆弾を投げたら、少し待って(顔爆弾が発射位置を離れてから)、頭が生えている待機中の体を
    ///   同じ位置・向きへ瞬間移動させ、体力・ターゲット・状態を引き継いで入れ替える。投げ終わった体は上空で待機
    ///   (その体の顔爆弾はそのまま飛んで爆発する)
    /// - 見えている体が倒されたり退場したりしたら、残りの体も退場させる。自然に再出現できるのは本体だけ
    /// </summary>
    internal static class CleanupCrewClones
    {
        private class Group
        {
            internal EnemyBombThrower Original;
            internal readonly List<EnemyBombThrower> Bodies = new List<EnemyBombThrower>();
            internal EnemyBombThrower Visible;
            internal int Index;

            internal void Clean() => Bodies.RemoveAll(b => !b);
        }

        private class BodyState
        {
            internal Group Group;
            internal bool Parked;
            internal bool AllowSpawn;
            internal float SwapAt = -1f;
            internal float MaintainTimer;
        }

        private static readonly Dictionary<EnemyBombThrower, BodyState> Bodies = new Dictionary<EnemyBombThrower, BodyState>();
        private static int _groupCount;
        private static PrefabRef _prefab;
        private static bool _prefabSearched;

        internal static bool Active => CleanupCrew.Enabled.Value && CleanupCrew.UseClones.Value && Util.IsAuthority;

        private static BodyState StateOf(EnemyBombThrower body) => body && Bodies.TryGetValue(body, out var state) ? state : null;

        /// <summary>
        /// 待機位置。高さだけでなく水平にもマップから遠ざける(ゲームの「プレイヤーが近い」判定は高さを無視して 20m 以内を見るので、
        /// マップの真上だと待機中の分身が「近い」扱いになり、開始時の敵の待機が解けてしまう)。
        /// </summary>
        private static Vector3 ParkPosition(Group group, int slot) => new Vector3(ParkOffset + group.Index * 12f, 3000f, ParkOffset + slot * 6f);

        private const float ParkOffset = 10000f;

        /// <summary>この敵が上空で待機中の体か(ほかの敵の処理で、待機中の分身を候補から外すため)。</summary>
        internal static bool IsParked(Enemy enemy)
        {
            if (!enemy || Bodies.Count == 0)
            {
                return false;
            }
            foreach (var pair in Bodies)
            {
                if (pair.Key && pair.Key.enemy == enemy)
                {
                    return pair.Value.Parked;
                }
            }
            return false;
        }

        private static Group GroupOf(Enemy enemy)
        {
            if (!enemy || Bodies.Count == 0)
            {
                return null;
            }
            foreach (var pair in Bodies)
            {
                if (pair.Key && pair.Key.enemy == enemy)
                {
                    return pair.Value.Group;
                }
            }
            return null;
        }

        // ---------------- 分身の生成 ----------------

        private static PrefabRef FindPrefab()
        {
            if (_prefabSearched || !EnemyDirector.instance)
            {
                return _prefab;
            }
            _prefabSearched = true;
            var director = EnemyDirector.instance;
            // ほかの敵のプレハブを読み込まないように、まず名前で絞り込む。見つからなければ全部を調べる
            for (int pass = 0; pass < 2; pass++)
            foreach (var list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
            {
                if (list == null) continue;
                foreach (var setup in list)
                {
                    if (!setup || setup.spawnObjects == null) continue;
                    foreach (var spawn in setup.spawnObjects)
                    {
                        if (spawn == null) continue;
                        if (pass == 0 && (spawn.ResourcePath == null || spawn.ResourcePath.IndexOf("bomb", System.StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        var prefab = spawn.Prefab;
                        if (prefab && prefab.GetComponentInChildren<EnemyBombThrower>(true))
                        {
                            _prefab = spawn;
                            Plugin.Verbose($"クリーンアップクルーの分身の元を見つけました: {spawn.ResourcePath}");
                            return _prefab;
                        }
                    }
                }
            }
            Plugin.Log.LogWarning("クリーンアップクルーの敵が見つからないため、分身を作れません");
            return null;
        }

        private static void EnsureClones(Group group)
        {
            group.Clean();
            int wanted = CleanupCrew.MaxExtraHeads.Value + 1;
            var prefab = FindPrefab();
            while (prefab != null && group.Bodies.Count < wanted)
            {
                int slot = group.Bodies.Count;
                var position = ParkPosition(group, slot);
                var obj = VanillaAssets.Spawn(prefab, position, Quaternion.identity);
                var parent = obj ? obj.GetComponent<EnemyParent>() : null;
                var body = obj ? obj.GetComponentInChildren<EnemyBombThrower>(true) : null;
                if (!parent || !body)
                {
                    Plugin.Log.LogWarning("クリーンアップクルーの分身の生成に失敗しました");
                    return;
                }
                group.Bodies.Add(body);
                Bodies[body] = new BodyState { Group = group, Parked = true, AllowSpawn = true };
                parent.SetupDone = true;
                // 本来の出現処理(OnSpawn)を通らないので「初回の出現済み」にしておく。
                // これが立っていない敵がいると、EnemyDirector が全体の待機時間を進めない
                parent.firstSpawnPointUsed = true;
                var enemy = obj.GetComponentInChildren<Enemy>(true);
                if (enemy)
                {
                    enemy.EnemyTeleported(position);
                }
                Plugin.Verbose($"クリーンアップクルーの分身を作りました({group.Bodies.Count}/{wanted})");
            }
        }

        // ---------------- 待機(上空で凍結) ----------------

        private static void Park(EnemyBombThrower body, BodyState state)
        {
            int slot = Mathf.Max(0, state.Group.Bodies.IndexOf(body));
            state.Parked = true;
            state.SwapAt = -1f;
            // ナビメッシュのない上空へ移すので、エージェントを止めてから瞬間移動する
            if (body.enemy.NavMeshAgent) body.enemy.NavMeshAgent.Disable(1f);
            body.enemy.EnemyTeleported(ParkPosition(state.Group, slot));
            body.enemy.Freeze(1.5f);
            // 出現直後は Enemy.Spawn が凍結を解くので、次の更新ですぐ凍結し直す
            state.MaintainTimer = 0f;
        }

        private static void MaintainParked(EnemyBombThrower body, BodyState state)
        {
            var enemy = body.enemy;
            if (enemy.EnemyParent)
            {
                // 待機中に勝手に退場しないように
                enemy.EnemyParent.SpawnedTimer = Mathf.Max(enemy.EnemyParent.SpawnedTimer, 600f);
            }
            if (enemy.CurrentState == EnemyState.Despawn)
            {
                enemy.CurrentState = EnemyState.Roaming;
            }
            if (enemy.NavMeshAgent) enemy.NavMeshAgent.Disable(1f);
            // AI は止めるが、頭の再生だけは進める
            body.HeadLogic();
            state.MaintainTimer -= Time.deltaTime;
            if (state.MaintainTimer <= 0f)
            {
                state.MaintainTimer = 0.5f;
                enemy.Freeze(1.5f);
            }
        }

        /// <summary>待機中の体を退場させる。分身は再出現させず、本体は自然な再出現に任せる。</summary>
        private static void Retire(EnemyBombThrower body, BodyState state)
        {
            state.Parked = false;
            state.SwapAt = -1f;
            body.enemy.Freeze(0f);
            body.enemy.EnemyParent.Despawn();
            if (body != state.Group.Original)
            {
                body.enemy.EnemyParent.DespawnedTimer = 9999f;
            }
        }

        // ---------------- 入れ替え ----------------

        private static void Swap(EnemyBombThrower from, EnemyBombThrower to)
        {
            var fromState = StateOf(from);
            var toState = StateOf(to);
            var group = fromState.Group;
            Vector3 position = from.enemy.transform.position;
            Quaternion rotation = from.transform.rotation;

            to.enemy.Freeze(0f);
            // 向きは瞬間移動の前に入れる(瞬間移動で体がその時点の向きに合わせられる)。回転のばねも合わせて、くるっと回らないようにする
            to.transform.rotation = rotation;
            to.horizontalRotationTarget = from.horizontalRotationTarget;
            if (to.horizontalRotationSpring != null)
            {
                to.horizontalRotationSpring.lastRotation = rotation;
                to.horizontalRotationSpring.springVelocity = Vector3.zero;
            }
            to.enemy.EnemyTeleported(position);
            if (to.enemy.NavMeshAgent)
            {
                // EnemyTeleported の Warp は距離 0 だと何もしないので、強制で置き直してから、待機中に止めていたエージェントを戻す
                to.enemy.NavMeshAgent.Warp(position, true);
                to.enemy.NavMeshAgent.Enable();
            }
            if (to.enemy.Health && from.enemy.Health)
            {
                to.enemy.Health.healthCurrent = from.enemy.Health.healthCurrent;
                // 倒したときのオーブの数は群れ全体で数える(体ごとだと、群れで本来の上限 × 体の数まで落ちる)
                int orbs = Mathf.Max(to.enemy.Health.spawnValuableCurrent, from.enemy.Health.spawnValuableCurrent);
                to.enemy.Health.spawnValuableCurrent = orbs;
                from.enemy.Health.spawnValuableCurrent = orbs;
            }
            to.enemy.CurrentState = from.enemy.CurrentState == EnemyState.Despawn ? EnemyState.Roaming : from.enemy.CurrentState;
            if (to.enemy.EnemyParent && from.enemy.EnemyParent)
            {
                to.enemy.EnemyParent.SpawnedTimer = from.enemy.EnemyParent.SpawnedTimer;
            }
            var next = from.playerTarget ? EnemyBombThrower.State.GotoPlayer : EnemyBombThrower.State.Idle;
            if (from.playerTarget)
            {
                to.UpdatePlayerTarget(from.playerTarget);
            }
            // 待機前と同じ状態だと UpdateState は何もしない(状態の最初の処理が走らず、古いタイマーのまま動く)ので、いったん別の状態にしておく
            if (to.currentState == next)
            {
                to.currentState = next == EnemyBombThrower.State.Idle ? EnemyBombThrower.State.Roam : EnemyBombThrower.State.Idle;
            }
            to.UpdateState(next);
            toState.Parked = false;
            group.Visible = to;
            Park(from, fromState);
            Plugin.Verbose("クリーンアップクルー: 頭の生えた分身と入れ替わりました");
        }

        private static EnemyBombThrower ReadyParked(Group group)
        {
            group.Clean();
            // 「追加の顔爆弾の最大数」を減らしたら、それを超える分身は使わない(待機したまま)
            int usable = CleanupCrew.MaxExtraHeads.Value + 1;
            return group.Bodies.Take(usable).FirstOrDefault(b =>
            {
                var s = StateOf(b);
                return s != null && s.Parked && b.enemy && b.enemy.EnemyParent && b.enemy.EnemyParent.Spawned
                       && b.headGrown && b.head.currentState == EnemyBombThrowerHead.State.Disabled;
            });
        }

        // ---------------- パッチ ----------------

        /// <summary>出現時: 本体なら群れを作って分身を用意する。分身なら上空で待機させる(本来のランダムな位置への出現をしない)。</summary>
        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.OnSpawn))]
        private static class OnSpawnPatch
        {
            private static bool Prefix(EnemyBombThrower __instance)
            {
                if (!Active)
                {
                    return true;
                }
                var state = StateOf(__instance);
                if (state != null && state.Group.Original != __instance)
                {
                    Park(__instance, state);
                    return false;
                }
                return true;
            }

            private static void Postfix(EnemyBombThrower __instance)
            {
                if (!Active)
                {
                    return;
                }
                var state = StateOf(__instance);
                if (state == null)
                {
                    // 前のレベルで消えた敵の記録を片付ける
                    foreach (var gone in Bodies.Keys.Where(k => !k).ToList())
                    {
                        Bodies.Remove(gone);
                    }
                    var group = new Group { Original = __instance, Index = ++_groupCount };
                    group.Bodies.Add(__instance);
                    state = new BodyState { Group = group };
                    Bodies[__instance] = state;
                }
                if (state.Group.Original == __instance)
                {
                    state.Parked = false;
                    state.Group.Visible = __instance;
                    EnsureClones(state.Group);
                    // 退場していた分身を呼び戻して待機させる
                    foreach (var body in state.Group.Bodies)
                    {
                        var s = StateOf(body);
                        if (body != __instance && s != null && body.enemy.EnemyParent && !body.enemy.EnemyParent.Spawned)
                        {
                            s.AllowSpawn = true;
                            body.enemy.EnemyParent.DespawnedTimer = 0f;
                        }
                    }
                }
            }
        }

        /// <summary>分身が勝手に(本来のタイマーで)出現しないようにする。</summary>
        [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Spawn))]
        private static class ParentSpawnPatch
        {
            private static bool Prefix(EnemyParent __instance)
            {
                if (Bodies.Count == 0)
                {
                    return true;
                }
                var body = __instance.GetComponentInChildren<EnemyBombThrower>(true);
                var state = StateOf(body);
                if (state == null || state.Group.Original == body)
                {
                    return true;
                }
                // 分身は、この方式が有効で呼び戻されたときだけ出現できる。
                // 設定を OFF にしている間も止める(全抽出のあとにゲームが待ち時間を 0 に戻しても、普通の敵として出てこないように)
                if (Active && state.AllowSpawn)
                {
                    state.AllowSpawn = false;
                    return true;
                }
                __instance.DespawnedTimer = 9999f;
                return false;
            }
        }

        /// <summary>見えている体が退場(倒された・去った)したら、群れの残りも退場させる。</summary>
        [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Despawn))]
        private static class ParentDespawnPatch
        {
            private static void Postfix(EnemyParent __instance)
            {
                if (!Active)
                {
                    return;
                }
                var body = __instance.GetComponentInChildren<EnemyBombThrower>(true);
                var state = StateOf(body);
                if (state == null || state.Group.Visible != body)
                {
                    return;
                }
                var group = state.Group;
                group.Visible = null;
                group.Clean();
                // 倒されて退場したか(本来は倒された敵だけ再出現までの待ちが 3 倍になる。待機中の本体は体力が残っているので、ここで合わせる)
                bool killed = body.enemy && body.enemy.HasHealth && body.enemy.Health.healthCurrent <= 0;
                foreach (var other in group.Bodies)
                {
                    if (other == body || !other.enemy || !other.enemy.EnemyParent || !other.enemy.EnemyParent.Spawned)
                    {
                        continue;
                    }
                    var s = StateOf(other);
                    s.Parked = true;
                    s.SwapAt = -1f;
                    other.enemy.Freeze(0f);
                    other.enemy.EnemyParent.Despawn();
                    if (other != group.Original)
                    {
                        other.enemy.EnemyParent.DespawnedTimer = 9999f;
                    }
                    else if (killed)
                    {
                        other.enemy.EnemyParent.DespawnedTimer *= 3f;
                    }
                }
                Plugin.Verbose("クリーンアップクルー: 見えている体が退場したので、分身もしまいました");
            }
        }

        /// <summary>顔爆弾を投げたら、入れ替えを予約する。</summary>
        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.HeadGrownSetRPC))]
        private static class ThrowPatch
        {
            private static void Postfix(EnemyBombThrower __instance, bool _state)
            {
                var state = StateOf(__instance);
                if (_state || !Active || state == null || state.Parked || state.Group.Visible != __instance
                    || !__instance.enemy.Health || __instance.enemy.Health.dead)
                {
                    return;
                }
                state.SwapAt = Time.time + CleanupCrew.SwapDelay.Value;
            }
        }

        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.Update))]
        private static class UpdatePatch
        {
            private static bool Prefix(EnemyBombThrower __instance)
            {
                var state = StateOf(__instance);
                if (state == null)
                {
                    return true;
                }
                if (!Active)
                {
                    // 途中で設定を OFF にしたら、待機中の体をしまう(凍結が切れて普通の敵として動き出さないように)
                    // 見えている体が分身なら、それもしまう(本体は自然な再出現に任せる。分身が普通の敵として残ると 2 体になる)
                    bool visibleClone = !state.Parked && __instance != state.Group.Original;
                    if (Util.IsAuthority && (state.Parked || visibleClone) && __instance.enemy.EnemyParent && __instance.enemy.EnemyParent.Spawned)
                    {
                        if (state.Group.Visible == __instance)
                        {
                            state.Group.Visible = null;
                        }
                        Retire(__instance, state);
                        return false;
                    }
                    return true;
                }
                if (state.Parked)
                {
                    if (__instance.enemy.EnemyParent && __instance.enemy.EnemyParent.Spawned)
                    {
                        MaintainParked(__instance, state);
                    }
                    return false;
                }
                if (state.SwapAt > 0f && Time.time >= state.SwapAt && state.Group.Visible == __instance)
                {
                    var health = __instance.enemy.Health;
                    // 自分の頭が戻ってきた、または倒れかけているなら入れ替えない
                    if (__instance.head.currentState == EnemyBombThrowerHead.State.Disabled || __instance.headGrown
                        || (health && (health.dead || health.healthCurrent <= 0)))
                    {
                        state.SwapAt = -1f;
                    }
                    else if (__instance.enemy.IsStunned() || (__instance.enemy.HasRigidbody && __instance.enemy.Rigidbody.grabbed))
                    {
                        // スタン中・つかまれている間は入れ替えない(スタンやつかみが消えてしまう)。終わってから入れ替える
                    }
                    else
                    {
                        var ready = ReadyParked(state.Group);
                        if (ready)
                        {
                            Swap(__instance, ready);
                            return false;
                        }
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// 前の体が投げた顔爆弾(爆発・放射能の雲)が、入れ替わった今の体を傷つけないようにする
        /// (ゲームは投げた本人 enemyHost だけを除外するので、同じ群れの別の体は「別の敵」としてダメージを受けてしまう)。
        /// </summary>
        [HarmonyPatch(typeof(HurtCollider), nameof(HurtCollider.EnemyHurt))]
        private static class GroupFriendlyFirePatch
        {
            private static bool Prefix(HurtCollider __instance, Enemy _enemy, ref bool __result)
            {
                if (Bodies.Count == 0 || !__instance.enemyHost || !_enemy || __instance.enemyHost == _enemy)
                {
                    return true;
                }
                var group = GroupOf(__instance.enemyHost);
                if (group == null || GroupOf(_enemy) != group)
                {
                    return true;
                }
                __result = false;
                return false;
            }
        }
    }
}
