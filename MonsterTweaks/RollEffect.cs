using System.Collections.Generic;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// ロールドローン(トルクドローン)のように、プレイヤーを転がり状態(タンブル)にして回転させる効果。ホストが使う。
    /// タンブルの開始と物理はホストが持っているので、MOD なしの参加者にも効く。
    /// </summary>
    internal class RollEffect : MonoBehaviour
    {
        private class Entry
        {
            internal PlayerAvatar Player;
            internal float EndTime;
            internal Vector3 Direction;
            internal float SpinSpeed;
            internal float Push;
        }

        private static RollEffect _instance;
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>center から radius 以内のプレイヤーを duration 秒転がす。</summary>
        internal static void Apply(Vector3 center, float radius, float duration, float spinSpeed, float push)
        {
            if (!Util.IsAuthority)
            {
                return;
            }
            if (!_instance)
            {
                var holder = new GameObject("MonsterTweaks RollEffect");
                DontDestroyOnLoad(holder);
                _instance = holder.AddComponent<RollEffect>();
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || !player.tumble)
                {
                    continue;
                }
                Vector3 offset = player.transform.position - center;
                if (offset.magnitude > radius)
                {
                    continue;
                }
                Vector3 direction = Vector3.ProjectOnPlane(offset, Vector3.up);
                if (direction.sqrMagnitude < 0.01f)
                {
                    direction = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
                }
                player.tumble.TumbleRequest(true, false);
                player.tumble.TumbleOverrideTime(duration);
                _instance._entries.RemoveAll(e => e.Player == player);
                _instance._entries.Add(new Entry
                {
                    Player = player,
                    EndTime = Time.time + duration,
                    Direction = direction.normalized,
                    SpinSpeed = spinSpeed,
                    Push = push,
                });
            }
        }

        private void FixedUpdate()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                var player = entry.Player;
                if (!player || player.isDisabled || !player.tumble || Time.time > entry.EndTime || !Util.IsAuthority)
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                var tumble = player.tumble;
                if (!tumble.isTumbling)
                {
                    tumble.TumbleRequest(true, false);
                    continue;
                }
                var rb = tumble.rb;
                if (!rb)
                {
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
