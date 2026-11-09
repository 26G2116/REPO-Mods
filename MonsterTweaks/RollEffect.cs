using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ロールドローン・ロールの杖・魔法学校のクリスタルと同じ「転がす」効果。ホストが使う。
    /// 1. 本来の効果(推奨): ロールの杖の弾を通信つきで生成し、その範囲効果(SemiAreaOfEffect)の RPC を
    ///    こちらで選んだ対象と時間で呼ぶ。全員の PC でゲーム本来のロール効果が出る(MOD なしの人にも見える)
    /// 2. 代わりの効果: 弾が見つからない・シングルプレイのとき。タンブルさせて回転させる(物理はホスト側なので全員に効く)
    /// </summary>
    internal class RollEffect : MonoBehaviour
    {
        /// <summary>center から radius 以内のプレイヤー(と貴重品)を duration 秒転がす。</summary>
        internal static void Apply(Vector3 center, Vector3 direction, float radius, float duration, bool valuables, float spinSpeed, float push)
        {
            if (!Util.IsAuthority)
            {
                return;
            }
            if (SemiFunc.IsMultiplayer() && ApplyVanilla(center, direction, radius, duration, valuables))
            {
                return;
            }
            ApplyFallback(center, radius, duration, valuables, spinSpeed, push);
        }

        // ---------------- 1. ゲーム本来のロール効果 ----------------

        private static bool ApplyVanilla(Vector3 center, Vector3 direction, float radius, float duration, bool valuables)
        {
            var prefab = VanillaAssets.TorqueProjectile;
            if (prefab == null || !PunManager.instance)
            {
                return false;
            }
            var ids = new List<int>();
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (player && !player.isDisabled && Vector3.Distance(player.transform.position, center) <= radius)
                {
                    ids.Add(player.photonView.ViewID);
                }
            }
            if (valuables)
            {
                foreach (var obj in SemiFunc.PhysGrabObjectGetAllWithinRange(radius, center))
                {
                    if (obj && obj.isValuable && !obj.dead && !obj.isPlayer && obj.photonView && !ids.Contains(obj.photonView.ViewID))
                    {
                        ids.Add(obj.photonView.ViewID);
                    }
                }
            }
            if (ids.Count == 0)
            {
                return true;
            }
            var projectile = VanillaAssets.Spawn(prefab, center + Vector3.up * 0.5f, Quaternion.LookRotation(Vector3.down));
            if (!projectile)
            {
                return false;
            }
            var areaOfEffect = projectile.GetComponentInChildren<SemiAreaOfEffect>(true);
            var view = areaOfEffect ? areaOfEffect.GetComponent<PhotonView>() : null;
            if (!view)
            {
                PhotonNetwork.Destroy(projectile);
                return false;
            }
            var times = new float[ids.Count];
            var affectIds = new int[ids.Count];
            var endTimes = new double[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                times[i] = duration;
                affectIds[i] = PunManager.instance.SemiAffectNetworkIDGet();
                endTimes[i] = PhotonNetwork.Time + duration;
            }
            view.RPC("CreateAffectsRPC", RpcTarget.All, ids.ToArray(), times, affectIds, endTimes, center, direction);
            // 弾そのものは使わない(飛んでいって別の効果を出さないよう、すぐ消す)
            PhotonNetwork.Destroy(projectile);
            return true;
        }

        // ---------------- 2. 代わりの効果 ----------------

        private class Entry
        {
            internal PlayerAvatar Player;
            internal Rigidbody Body;
            internal float EndTime;
            internal Vector3 Direction;
            internal float SpinSpeed;
            internal float Push;
        }

        private static RollEffect _instance;
        private readonly List<Entry> _entries = new List<Entry>();

        private static void ApplyFallback(Vector3 center, float radius, float duration, bool valuables, float spinSpeed, float push)
        {
            if (!_instance)
            {
                var holder = new GameObject("MonsterTweaks RollEffect");
                DontDestroyOnLoad(holder);
                _instance = holder.AddComponent<RollEffect>();
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || !player.tumble || Vector3.Distance(player.transform.position, center) > radius)
                {
                    continue;
                }
                player.tumble.TumbleRequest(true, false);
                player.tumble.TumbleOverrideTime(duration);
                _instance.Add(new Entry { Player = player }, player.transform.position, center, duration, spinSpeed, push);
            }
            if (valuables)
            {
                foreach (var obj in SemiFunc.PhysGrabObjectGetAllWithinRange(radius, center))
                {
                    if (obj && obj.isValuable && !obj.dead && obj.rb)
                    {
                        _instance.Add(new Entry { Body = obj.rb }, obj.transform.position, center, duration, spinSpeed, push);
                    }
                }
            }
        }

        private void Add(Entry entry, Vector3 position, Vector3 center, float duration, float spinSpeed, float push)
        {
            Vector3 direction = Vector3.ProjectOnPlane(position - center, Vector3.up);
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            }
            _entries.RemoveAll(e => (entry.Player && e.Player == entry.Player) || (entry.Body && e.Body == entry.Body));
            entry.EndTime = Time.time + duration;
            entry.Direction = direction.normalized;
            entry.SpinSpeed = spinSpeed;
            entry.Push = push;
            _entries.Add(entry);
        }

        private void FixedUpdate()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (Time.time > entry.EndTime || !Util.IsAuthority)
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                Rigidbody rb;
                if (entry.Player)
                {
                    var player = entry.Player;
                    if (player.isDisabled || !player.tumble)
                    {
                        _entries.RemoveAt(i);
                        continue;
                    }
                    if (!player.tumble.isTumbling)
                    {
                        player.tumble.TumbleRequest(true, false);
                        continue;
                    }
                    rb = player.tumble.rb;
                }
                else
                {
                    rb = entry.Body;
                }
                if (!rb)
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                // 進む向きに対して横の軸で回す = 地面を転がる(ロールドローンの RollTowards と同じ考え方)
                rb.angularVelocity = Vector3.Cross(Vector3.up, entry.Direction) * entry.SpinSpeed;
                if (entry.Push > 0f)
                {
                    rb.AddForce(entry.Direction * entry.Push, ForceMode.Acceleration);
                }
            }
        }
    }
}
