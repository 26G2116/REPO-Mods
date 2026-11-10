using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// MOD どうしの通信。
    /// - Photon イベント 174 で「種類(byte) + 中身」を送る。受け取るのはホストからの分だけ
    /// - 自分が MOD を入れていることを、Photon のプレイヤー情報(カスタムプロパティ)で知らせる
    /// </summary>
    internal class Net : MonoBehaviour
    {
        /// <summary>LoadProgress(173)と重ならない Photon イベント番号。</summary>
        private const byte EventCode = 174;
        private const string PresenceKey = "shunm.MonsterTweaks";

        internal const byte KindExplosion = 1;
        internal const byte KindHeadSpawn = 2;
        internal const byte KindHeadPose = 3;
        internal const byte KindHeadRemove = 4;
        /// <summary>ホスト → 全員: ピーパーの追加のターゲット(ピーパーの ViewID, プレイヤーの ViewID の配列)。</summary>
        internal const byte KindPeeperTargets = 5;
        /// <summary>参加者 → ホスト: 自分のスタミナの割合(0〜1)。</summary>
        internal const byte KindStamina = 6;

        /// <summary>毎フレーム呼ぶ処理(MOD を入れた全員の PC で動く)。</summary>
        internal static event Action Tick;

        private static readonly Dictionary<byte, Action<object[]>> Handlers = new Dictionary<byte, Action<object[]>>();
        private static readonly Dictionary<byte, Action<int, object[]>> ClientHandlers = new Dictionary<byte, Action<int, object[]>>();

        internal static void Init()
        {
            var holder = new GameObject("MonsterTweaks Net");
            DontDestroyOnLoad(holder);
            holder.AddComponent<Net>();
            PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        }

        internal static void Register(byte kind, Action<object[]> handler) => Handlers[kind] = handler;

        /// <summary>参加者からホストへ送られる種類の受け取り(ホストだけで呼ばれる。引数は送った人の ActorNumber と中身)。</summary>
        internal static void RegisterFromClient(byte kind, Action<int, object[]> handler) => ClientHandlers[kind] = handler;

        /// <summary>参加者からホストへ送る(ホスト自身なら何もしない)。</summary>
        internal static void SendToHost(byte kind, params object[] data)
        {
            if (!SemiFunc.IsMultiplayer() || PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom)
            {
                return;
            }
            var payload = new object[data.Length + 1];
            payload[0] = kind;
            Array.Copy(data, 0, payload, 1, data.Length);
            PhotonNetwork.RaiseEvent(EventCode, payload,
                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
                SendOptions.SendUnreliable);
        }

        /// <summary>ホストから他の全員へ送る(中身の先頭に種類が付く)。</summary>
        internal static void SendToOthers(byte kind, params object[] data)
        {
            if (!SemiFunc.IsMultiplayer() || !PhotonNetwork.IsMasterClient)
            {
                return;
            }
            var payload = new object[data.Length + 1];
            payload[0] = kind;
            Array.Copy(data, 0, payload, 1, data.Length);
            PhotonNetwork.RaiseEvent(EventCode, payload,
                new RaiseEventOptions { Receivers = ReceiverGroup.Others },
                kind == KindHeadPose ? SendOptions.SendUnreliable : SendOptions.SendReliable);
        }

        /// <summary>部屋の全員が MonsterTweaks を入れているか(シングルプレイなら true)。</summary>
        internal static bool EveryoneHasMod()
        {
            if (!SemiFunc.IsMultiplayer())
            {
                return true;
            }
            foreach (var player in PhotonNetwork.PlayerList)
            {
                if (player.CustomProperties == null || !player.CustomProperties.ContainsKey(PresenceKey))
                {
                    return false;
                }
            }
            return true;
        }

        private static void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != EventCode || !(photonEvent.CustomData is object[] data) || data.Length < 1 || !(data[0] is byte kind))
            {
                return;
            }
            var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);
            if (sender == null)
            {
                return;
            }
            var body = new object[data.Length - 1];
            Array.Copy(data, 1, body, 0, body.Length);
            Action call;
            if (sender.IsMasterClient && Handlers.TryGetValue(kind, out var handler))
            {
                call = () => handler(body);
            }
            else if (!sender.IsMasterClient && PhotonNetwork.IsMasterClient && ClientHandlers.TryGetValue(kind, out var clientHandler))
            {
                call = () => clientHandler(sender.ActorNumber, body);
            }
            else
            {
                return;
            }
            try
            {
                call();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MOD 間の通信(種類 {kind})の処理に失敗しました: {e.Message}");
            }
        }

        private float _presenceTimer;

        private void Update()
        {
            try
            {
                Tick?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"毎フレームの処理に失敗しました: {e.Message}");
            }
            // 部屋に入ったら(入り直したら)、MOD を入れていることを知らせる
            _presenceTimer -= Time.unscaledDeltaTime;
            if (_presenceTimer > 0f)
            {
                return;
            }
            _presenceTimer = 2f;
            var local = PhotonNetwork.InRoom ? PhotonNetwork.LocalPlayer : null;
            if (local != null && (local.CustomProperties == null || !local.CustomProperties.ContainsKey(PresenceKey)))
            {
                local.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PresenceKey, Plugin.PluginVersion } });
            }
        }
    }
}
