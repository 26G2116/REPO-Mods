using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// クリーンアップクルー(全身に顔。頭の顔爆弾を投げる)。クラス名は EnemyBombThrower。
    /// 既定は CleanupCrewClones の「分身と入れ替わる」方式。このファイルの処理は、それを OFF にしたときの「複製」方式。
    /// 本来は顔爆弾が 1 体に 1 個(EnemyBombThrowerHead)しかなく、爆発して戻るまで次の頭が生えない。
    /// この MOD では、投げた直後から頭を再生し、本物の顔爆弾がまだ動いていたら「追加の顔爆弾」を投げる。
    /// - 追加の顔爆弾は、本物の見た目を複製したもの(通信用の部品は外す)。動き・爆発はホストが計算する
    /// - 爆発のダメージは Boom(HurtOther)で全員に届く。見た目は MOD を入れた人にだけ Net で送る
    /// - 既定では、部屋の全員が MOD を入れているときだけ使う(MOD なしの人には見えない爆弾になるため)
    /// </summary>
    internal static class CleanupCrew
    {
        private const string Section = "8. クリーンアップクルー";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> UseClones;
        internal static ConfigEntry<float> SwapDelay;
        internal static ConfigEntry<bool> AllowWithoutMod;
        internal static ConfigEntry<float> RegrowDelay;
        internal static ConfigEntry<int> MaxExtraHeads;
        internal static ConfigEntry<float> Fuse;
        internal static ConfigEntry<int> Damage;
        internal static ConfigEntry<float> Radius;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "顔爆弾を投げたあと、爆発を待たずに次の顔爆弾を投げられるようにする");
            UseClones = cfg.Bind(Section, "分身と入れ替わる方式", true,
                "ON: 見えない場所に本物の分身(同じ敵)を用意し、顔爆弾を投げたら頭の生えた分身と入れ替わる。本物の敵なので MOD なしの人にもそのまま見える。" +
                "OFF: 顔爆弾の見た目を複製して投げる(MOD なしの人には見えない)");
            SwapDelay = Util.Float(cfg, Section, "入れ替えまでの時間", 0.15f, 0.05f, 3f,
                "顔爆弾を投げてから分身と入れ替わるまでの秒数(短すぎると顔爆弾が発射位置を離れる前に入れ替わる)");
            AllowWithoutMod = cfg.Bind(Section, "複製方式: MOD なしの人がいても使う", false,
                "分身方式を OFF にしたときだけ使う。OFF なら部屋の全員が MOD を入れているときだけ複製を投げる");
            RegrowDelay = Util.Float(cfg, Section, "複製方式: 頭の再生までの時間", 1f, 0f, 10f, "投げてから次の頭が生えるまでの秒数");
            MaxExtraHeads = Util.Int(cfg, Section, "追加の顔爆弾の最大数", 3, 1, 10,
                "本物の 1 個とは別に、同時に飛ばせる顔爆弾の数(分身方式では分身の数。変更は次に出現したときから)");
            Fuse = Util.Float(cfg, Section, "複製方式: 爆発までの時間", 7f, 2f, 20f, "投げてから爆発するまでの秒数(本物はおよそ 7〜8 秒)");
            Damage = Util.Int(cfg, Section, "複製方式: ダメージ", 75, 0, 200, "本物の顔爆弾は 75");
            Radius = Util.Float(cfg, Section, "複製方式: 爆発範囲", 3.5f, 1f, 15f, "ダメージが届く距離(m)");
        }

        internal static void Init()
        {
            Net.Register(Net.KindHeadSpawn, OnHeadSpawn);
            Net.Register(Net.KindHeadPose, OnHeadPose);
            Net.Register(Net.KindHeadRemove, OnHeadRemove);
        }

        /// <summary>複製方式が動くか(分身方式が ON なら動かない)。</summary>
        private static bool Active => Enabled.Value && !UseClones.Value && Util.IsAuthority && (AllowWithoutMod.Value || Net.EveryoneHasMod());

        // ---------------- ホスト側: 頭の再生と、追加の顔爆弾の発射 ----------------

        private class Memo
        {
            internal float RegrowTimer;
            internal readonly List<ExtraHead> Extras = new List<ExtraHead>();

            internal int AliveCount()
            {
                Extras.RemoveAll(h => !h);
                return Extras.Count;
            }
        }

        private static readonly ConditionalWeakTable<EnemyBombThrower, Memo> Memos = new ConditionalWeakTable<EnemyBombThrower, Memo>();
        private static int _nextId = 1;

        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.HeadLogic))]
        private static class HeadLogicPatch
        {
            private static void Postfix(EnemyBombThrower __instance)
            {
                var memo = Memos.GetOrCreateValue(__instance);
                // 本物の顔爆弾が戻っていれば本来の処理に任せる
                if (!Active || __instance.headGrown || __instance.head.currentState == EnemyBombThrowerHead.State.Disabled)
                {
                    memo.RegrowTimer = 0f;
                    return;
                }
                if (memo.AliveCount() >= MaxExtraHeads.Value)
                {
                    return;
                }
                memo.RegrowTimer += Time.deltaTime;
                if (memo.RegrowTimer >= RegrowDelay.Value)
                {
                    memo.RegrowTimer = 0f;
                    __instance.HeadGrownSet(true);
                }
            }
        }

        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.HeadGrownSetRPC))]
        private static class HeadGrownSetPatch
        {
            private static bool Prefix(EnemyBombThrower __instance, bool _state, PhotonMessageInfo _info)
            {
                // ホスト以外から届いた RPC は本来の処理(送り主の確認で弾く)に任せる
                if (!SemiFunc.MasterOnlyRPC(_info))
                {
                    return true;
                }
                // 投げる(頭がなくなる)瞬間に、本物の顔爆弾がまだ動いていたら、本物には触らず追加の顔爆弾を投げる
                if (_state || !Active || __instance.head.currentState == EnemyBombThrowerHead.State.Disabled)
                {
                    return true;
                }
                __instance.headGrown = false;
                __instance.headCollider.gameObject.SetActive(false);
                __instance.headCooldown = 1f;
                SpawnExtra(__instance);
                return false;
            }
        }

        private static void SpawnExtra(EnemyBombThrower thrower)
        {
            var spawn = thrower.headSpawnTransform;
            var clone = VisualClone(thrower.head, spawn.position, spawn.rotation, true);
            if (!clone)
            {
                return;
            }
            var extra = clone.AddComponent<ExtraHead>();
            extra.Id = _nextId++;
            extra.Owner = thrower;
            extra.Target = thrower.playerTarget;
            Memos.GetOrCreateValue(thrower).Extras.Add(extra);
            Net.SendToOthers(Net.KindHeadSpawn, extra.Id, thrower.photonView.ViewID, spawn.position, spawn.rotation);
            Plugin.Verbose("クリーンアップクルー: 追加の顔爆弾を投げました");
        }

        /// <summary>
        /// 顔爆弾の見た目だけを複製する。非アクティブな親の下で複製し、Awake が走る前に
        /// 通信・つかみ判定などのスクリプトを全部外す(PhotonView の ID 重複やゲーム側の処理への混入を防ぐ)。
        /// </summary>
        private static GameObject VisualClone(EnemyBombThrowerHead head, Vector3 position, Quaternion rotation, bool physics)
        {
            if (!head)
            {
                return null;
            }
            var holder = new GameObject("MonsterTweaks HeadHolder");
            holder.SetActive(false);
            var clone = Object.Instantiate(head.gameObject, position, rotation, holder.transform);
            // RequireComponent の依存があるので、外れるまで何回か繰り返す
            for (int pass = 0; pass < 4; pass++)
            {
                foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    Object.DestroyImmediate(behaviour);
                }
            }
            int defaultLayer = LayerMask.NameToLayer("Default");
            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.tag = "Untagged";
                t.gameObject.layer = defaultLayer;
            }
            clone.name = "MonsterTweaks ExtraHead";
            clone.transform.SetParent(null, true);
            Object.Destroy(holder);
            // 子の表示・非表示は複製元(飛んでいる本物)のまま。ルートだけ表示する
            clone.SetActive(true);
            var rb = FindRigidbody(clone);
            if (rb)
            {
                rb.isKinematic = !physics;
                rb.useGravity = physics;
            }
            var animator = clone.GetComponentInChildren<Animator>();
            if (animator)
            {
                animator.SetBool("active", true);
            }
            return clone;
        }

        private static Rigidbody FindRigidbody(GameObject obj)
        {
            var rb = obj.GetComponent<Rigidbody>();
            return rb ? rb : obj.GetComponentInChildren<Rigidbody>();
        }

        /// <summary>ホストが動かす追加の顔爆弾(本物の StateSpawn / StateActive をまねる)。</summary>
        private class ExtraHead : MonoBehaviour
        {
            internal int Id;
            internal EnemyBombThrower Owner;
            internal PlayerAvatar Target;

            private Rigidbody _rb;
            private Animator _animator;
            private float _age;
            private float _jumpCooldown;
            private float _poseTimer;

            private void Start()
            {
                _rb = FindRigidbody(gameObject);
                _animator = GetComponentInChildren<Animator>();
                if (_rb && Owner && Owner.headSpawnTransform)
                {
                    var spawn = Owner.headSpawnTransform;
                    Vector3 direction = Vector3.Lerp(spawn.forward, Vector3.up, 0.3f);
                    _rb.AddForce(direction * 20f, ForceMode.Impulse);
                    _rb.AddTorque(spawn.right * 0.25f, ForceMode.Impulse);
                }
            }

            private void Update()
            {
                _age += Time.deltaTime;
                if (_animator && _rb)
                {
                    _animator.speed = Mathf.Clamp(_rb.angularVelocity.magnitude * 0.25f, 2f, 2.5f);
                }
                _poseTimer -= Time.deltaTime;
                if (_poseTimer <= 0f)
                {
                    _poseTimer = 0.1f;
                    Net.SendToOthers(Net.KindHeadPose, Id, transform.position, transform.rotation);
                }
                // MOD なしの人が途中参加したら、その人には見えないので爆発させずに消す
                if (!Util.IsAuthority || (!AllowWithoutMod.Value && !Net.EveryoneHasMod()))
                {
                    Fizzle();
                    return;
                }
                if (_age >= Fuse.Value)
                {
                    Explode();
                }
            }

            private void Fizzle()
            {
                Net.SendToOthers(Net.KindHeadRemove, Id);
                Destroy(gameObject);
            }

            private void FixedUpdate()
            {
                // 投げてから 2 秒は飛ぶだけ。そのあと相手に向かって転がり、跳ねる
                if (!_rb || _age < 2f)
                {
                    return;
                }
                if (!Target || Target.isDisabled)
                {
                    Target = Util.NearestAlivePlayer(transform.position, 0f);
                    if (!Target)
                    {
                        return;
                    }
                }
                Vector3 toTarget = Target.transform.position - transform.position;
                if (toTarget.magnitude <= 2f)
                {
                    return;
                }
                Vector3 direction = toTarget.normalized;
                _rb.AddTorque(Vector3.Cross(Vector3.up, direction) * 1.5f, ForceMode.Force);
                _jumpCooldown -= Time.fixedDeltaTime;
                if (_jumpCooldown <= 0f)
                {
                    if (Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down, 0.6f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
                    {
                        _rb.AddForce(Vector3.Lerp(direction, Vector3.up, 0.8f) * 15f, ForceMode.Impulse);
                    }
                    _jumpCooldown = 1f;
                }
            }

            private void Explode()
            {
                Boom.Explode(transform.position, 2f, Radius.Value, Damage.Value, false, Owner ? Owner.enemy : null);
                Net.SendToOthers(Net.KindHeadRemove, Id);
                Destroy(gameObject);
            }
        }

        // ---------------- 参加者側(MOD あり): 追加の顔爆弾の見た目 ----------------

        private static readonly Dictionary<int, GameObject> ClientHeads = new Dictionary<int, GameObject>();

        private static void OnHeadSpawn(object[] data)
        {
            if (data.Length < 4 || !(data[0] is int id) || !(data[1] is int viewId) || !(data[2] is Vector3 position) || !(data[3] is Quaternion rotation))
            {
                return;
            }
            var view = PhotonView.Find(viewId);
            var thrower = view ? view.GetComponent<EnemyBombThrower>() : null;
            var clone = thrower ? VisualClone(thrower.head, position, rotation, false) : null;
            if (!clone)
            {
                return;
            }
            var follow = clone.AddComponent<ClientHead>();
            follow.TargetPosition = position;
            follow.TargetRotation = rotation;
            ClientHeads[id] = clone;
        }

        private static void OnHeadPose(object[] data)
        {
            if (data.Length >= 3 && data[0] is int id && data[1] is Vector3 position && data[2] is Quaternion rotation
                && ClientHeads.TryGetValue(id, out var clone) && clone)
            {
                var follow = clone.GetComponent<ClientHead>();
                follow.TargetPosition = position;
                follow.TargetRotation = rotation;
            }
        }

        private static void OnHeadRemove(object[] data)
        {
            if (data.Length >= 1 && data[0] is int id && ClientHeads.TryGetValue(id, out var clone))
            {
                if (clone)
                {
                    Object.Destroy(clone);
                }
                ClientHeads.Remove(id);
            }
        }

        /// <summary>ホストから届く位置へなめらかに動かす。届かなくなっても 30 秒で消える。</summary>
        private class ClientHead : MonoBehaviour
        {
            internal Vector3 TargetPosition;
            internal Quaternion TargetRotation;
            private float _age;

            private void Update()
            {
                transform.position = Vector3.Lerp(transform.position, TargetPosition, Time.deltaTime * 15f);
                transform.rotation = Quaternion.Slerp(transform.rotation, TargetRotation, Time.deltaTime * 15f);
                _age += Time.deltaTime;
                if (_age > 30f)
                {
                    Destroy(gameObject);
                }
            }
        }
    }
}
