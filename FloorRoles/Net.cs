using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    /// <summary>
    /// ゲーム本来の通信を使う小さな道具(どれもホストから呼ぶ)。MOD なしの参加者の画面にもそのまま出る。
    /// </summary>
    internal static class Net
    {
        internal static bool IsHost => SemiFunc.IsMasterClientOrSingleplayer();

        /// <summary>レベルを遊べる状態か(生成が終わり、ゲームが進行中)。</summary>
        internal static bool Ready =>
            LevelGenerator.Instance && LevelGenerator.Instance.Generated &&
            GameDirector.instance && GameDirector.instance.currentState == GameDirector.gameState.Main;

        /// <summary>
        /// プレイヤーにしゃべらせる(頭の上の吹き出しと読み上げ)。ホストからなら誰にでもしゃべらせられる。
        /// 新しいセリフが来ると前のセリフは途中で止まるので、続けるときは間を空ける。
        /// </summary>
        internal static void Speak(PlayerAvatar player, string text)
        {
            if (!player || string.IsNullOrEmpty(text))
            {
                return;
            }
            string locale = IsAscii(text) ? "en-US" : "ja-JP";
            try
            {
                if (SemiFunc.IsMultiplayer())
                {
                    player.photonView.RPC("ChatMessageSendRPC", RpcTarget.All, text, false, locale);
                }
                else
                {
                    player.ChatMessageSpeak(text, false, locale);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"しゃべらせられませんでした: {e.Message}");
            }
        }

        /// <summary>トラックのモニターに、税務官からのメッセージとして書く(打ち込み中の画面では出ない)。</summary>
        /// <summary>
        /// 税務官が今のページの文章を最後まで打ち終わったか(ホストで見る)。
        /// 打っていない瞬間でも、行と行の間の待ち時間なら、まだ続きがある。
        /// </summary>
        internal static bool TruckPageFinished()
        {
            var screen = TruckScreenText.instance;
            if (!screen || !screen.started || screen.isTyping || screen.pages == null)
            {
                return false;
            }
            int page = screen.currentPageIndex;
            if (page < 0 || page >= screen.pages.Count)
            {
                return true;
            }
            var lines = screen.pages[page].textLines;
            return lines == null || screen.currentLineIndex >= lines.Count;
        }

        internal static bool TruckMessage(string text)
        {
            var screen = TruckScreenText.instance;
            if (!screen || screen.isTyping)
            {
                return false;
            }
            screen.MessageSendCustom("", text, 0);
            return true;
        }

        /// <summary>プレイヤーを倒す(本人の PC でダメージが計算される)。</summary>
        internal static void Kill(PlayerAvatar player)
        {
            if (player && player.playerHealth && !player.deadSet)
            {
                player.playerHealth.HurtOther(9999, Vector3.zero, false, -1);
            }
        }

        /// <summary>アップグレードを上げる。全員の PC で同じ処理が動き、本人の PC ではすぐ効く。</summary>
        internal static void Upgrade(string steamID, string upgradeName, int amount)
        {
            var pun = PunManager.instance;
            if (!pun || string.IsNullOrEmpty(steamID) || amount == 0)
            {
                return;
            }
            if (SemiFunc.IsMultiplayer())
            {
                pun.photonView.RPC("TesterUpgradeCommandRPC", RpcTarget.All, steamID, upgradeName, amount);
            }
            else
            {
                pun.TesterUpgradeCommandRPC(steamID, upgradeName, amount, default);
            }
        }

        /// <summary>今のアップグレードの数。</summary>
        internal static int UpgradeLevel(string steamID, string upgradeName)
        {
            var stats = StatsManager.instance;
            if (!stats || string.IsNullOrEmpty(steamID) ||
                !stats.dictionaryOfDictionaries.TryGetValue("playerUpgrade" + upgradeName, out var dict))
            {
                return 0;
            }
            return dict.GetValueOrDefault(steamID, 0);
        }

        /// <summary>Resources のパスで全員の画面に出す。</summary>
        internal static GameObject SpawnRoomObject(string resourcePath, GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (SemiFunc.IsMultiplayer())
            {
                return PhotonNetwork.InstantiateRoomObject(resourcePath, position, rotation, 0, null);
            }
            return prefab ? Object.Instantiate(prefab, position, rotation) : null;
        }

        /// <summary>金袋(小)を、決まった金額(K = $1,000)で出す。</summary>
        internal static ValuableObject SpawnMoneyBag(Vector3 position, int thousands)
        {
            var prefab = AssetManager.instance ? AssetManager.instance.surplusValuableSmall : null;
            if (!prefab || thousands <= 0)
            {
                return null;
            }
            var spawned = SpawnRoomObject("Valuables/" + prefab.name, prefab, position, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var valuable = spawned ? spawned.GetComponent<ValuableObject>() : null;
            if (valuable)
            {
                // 値段の確定(少し待ってから行われる)の前に入れる。精算の余りの金袋と同じやり方
                valuable.dollarValueOverride = thousands * 1000;
            }
            return valuable;
        }

        /// <summary>まわりの物にめり込まない、床の上の位置を探す。</summary>
        internal static Vector3 FreeSpot(Vector3 center, float minRadius, float maxRadius, float clearance = 0.35f)
        {
            int mask = SemiFunc.LayerMaskGetVisionObstruct();
            for (int i = 0; i < 24; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(minRadius, maxRadius);
                var candidate = center + new Vector3(offset.x, 0f, offset.y);
                if (Physics.Linecast(center, candidate, mask, QueryTriggerInteraction.Ignore))
                {
                    continue; // 壁の向こう
                }
                if (Physics.CheckSphere(candidate, clearance, mask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                return candidate;
            }
            return center;
        }

        internal static string Name(PlayerAvatar player)
        {
            return player ? player.playerName : "?";
        }

        internal static bool IsAscii(string text)
        {
            foreach (char c in text)
            {
                if (c > 127)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>レベルが変わるたびに増える番号。各機能が「このレベルでもう処理したか」を覚えるのに使う。</summary>
    internal static class Phase
    {
        internal static int Generation;

        [HarmonyPatch(typeof(RunManager), nameof(RunManager.ChangeLevel))]
        private static class ChangeLevelPatch
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(bool _completedLevel, bool _levelFailed)
            {
                try
                {
                    if (Net.IsHost)
                    {
                        Roles.OnLeavingLevel(_completedLevel && !_levelFailed);
                        ShopBudget.OnLeavingLevel();
                        Duel.OnLeavingLevel();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"レベル終了時の処理に失敗しました: {e}");
                }
                Generation++;
            }
        }
    }

    /// <summary>毎フレームの処理とコルーチン用。シーンが変わっても消えないオブジェクト。</summary>
    internal class Runner : MonoBehaviour
    {
        private static Runner instance;
        private static readonly Dictionary<string, float> ErrorTimes = new Dictionary<string, float>();

        internal static Runner Instance
        {
            get
            {
                Ensure();
                return instance;
            }
        }

        internal static void Ensure()
        {
            if (instance)
            {
                return;
            }
            var go = new GameObject("FloorRoles.Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Runner>();
        }

        private void Update()
        {
            if (!Net.IsHost || !Net.Ready)
            {
                return;
            }
            Guard("役職", Roles.Tick);
            Guard("所持金", ShopBudget.Tick);
            Guard("決闘", Duel.Tick);
        }

        private static void Guard(string label, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                // 毎フレーム同じエラーでログが埋まらないように、5 秒に 1 回だけ出す
                if (!ErrorTimes.TryGetValue(label, out float last) || Time.unscaledTime - last > 5f)
                {
                    ErrorTimes[label] = Time.unscaledTime;
                    Plugin.Log.LogError($"{label}の処理でエラー: {e}");
                }
            }
        }
    }
}
