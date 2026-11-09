using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoadProgress
{
    /// <summary>
    /// ロード中の各プレイヤーの進捗を集める。
    /// - 自分の進捗はローカルの状態から直接読む(10 段階すべて分かる)
    /// - 他人の進捗はゲーム本体が持っている「報告済みリスト」から読む(ホストほど多く分かる)
    /// - MOD を入れている人同士は Photon イベントで進捗を送り合う(ホストは全員分を送る)
    /// </summary>
    internal class ProgressTracker : MonoBehaviour
    {
        // ゲーム本体は 123 / 124 / 199 を使う。被らない番号にする。
        private const byte EventCode = 173;
        private const byte ProtocolVersion = 1;
        private const float PollInterval = 0.1f;
        private const float ResendInterval = 1f;
        private const float SessionTimeout = 600f;
        // 自分が Main になっても全員の完了が確認できないとき、表示を閉じるまでの猶予
        private const float CloseGraceAfterMain = 10f;

        internal struct Row
        {
            public int Actor;
            public string Name;
            public bool IsMaster;
            public bool IsLocal;
            public bool Modded;
            public int Mask;
            public string Detail;
        }

        internal readonly List<Row> Rows = new List<Row>();
        internal bool SessionActive;
        internal float SessionStartTime;
        internal float CompletedTime = -1f;

        private float closeAtTime = -1f;
        private float mainReachedTime = -1f;
        private float nextPoll;
        private float nextResend;
        private bool sawReloadThisSession;
        private bool eventSubscribed;
        private ReloadScene reloadScene;
        private LevelGenerator staleGenerator;   // セッション開始時点で残っていた前のレベルの生成器
        private LevelGenerator closedGenerator;  // 表示を閉じた生成器(再表示しない)

        private readonly Dictionary<int, int> reported = new Dictionary<int, int>();
        private readonly Dictionary<int, int> masks = new Dictionary<int, int>();
        private readonly HashSet<int> reloadReadyActors = new HashSet<int>();
        private readonly HashSet<int> moddedActors = new HashSet<int>();

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (eventSubscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
            }
            eventSubscribed = false;
        }

        private static bool InMultiplayerRoom()
        {
            return PhotonNetwork.InRoom && GameManager.instance != null && SemiFunc.IsMultiplayer();
        }

        // ---------------------------------------------------------------- セッション管理

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Reload" || !InMultiplayerRoom())
            {
                return;
            }
            if (SessionActive)
            {
                EndSession("次のロードが始まったため");
            }
            StartSession("Reload");
            sawReloadThisSession = true;
            staleGenerator = LevelGenerator.Instance; // ここで残っているのは前のレベルのもの
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                reloadScene = root.GetComponentInChildren<ReloadScene>(true);
                if (reloadScene != null) break;
            }
        }

        private void StartSession(string reason)
        {
            SessionActive = true;
            SessionStartTime = Time.unscaledTime;
            CompletedTime = -1f;
            closeAtTime = -1f;
            mainReachedTime = -1f;
            nextPoll = 0f;
            nextResend = 0f;
            reported.Clear(); // セッション外の報告は受け付けていないが念のため
            if (Plugin.LogTransitions.Value)
            {
                Plugin.Log.LogInfo($"ロード開始を検出 ({reason})");
            }
        }

        private void EndSession(string reason)
        {
            if (SessionActive && Plugin.LogTransitions.Value)
            {
                Plugin.Log.LogInfo($"表示を終了: {reason} (経過 {Time.unscaledTime - SessionStartTime:0.0}s)");
            }
            SessionActive = false;
            closeAtTime = -1f;
            mainReachedTime = -1f;
            sawReloadThisSession = false;
            reloadScene = null;
            staleGenerator = null;
            reported.Clear();
            masks.Clear();
            reloadReadyActors.Clear();
            Rows.Clear();
        }

        private void Update()
        {
            if (!eventSubscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
                eventSubscribed = true;
            }

            if (!InMultiplayerRoom())
            {
                if (SessionActive) EndSession("ルームを離れたため");
                moddedActors.Clear();
                return;
            }

            LevelGenerator lg = LevelGenerator.Instance;
            bool lgValid = lg != null && lg != staleGenerator;

            // Reload シーンを通らずにロードが始まった場合の保険
            if (!SessionActive && lgValid && !lg.Generated && lg != closedGenerator)
            {
                StartSession("LevelGenerator");
            }
            if (!SessionActive)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now >= nextPoll)
            {
                nextPoll = now + PollInterval;
                Poll(lg, lgValid);
            }

            if (closeAtTime < 0f && LocalLoadFinished(lg, lgValid))
            {
                if (mainReachedTime < 0f) mainReachedTime = now;
                // 全員の完了を確認できたら閉じる。確認できない人がいても猶予を過ぎたら閉じる。
                if (AllRowsComplete() || now - mainReachedTime > CloseGraceAfterMain)
                {
                    CompletedTime = now - SessionStartTime;
                    closeAtTime = now + Mathf.Max(0f, Plugin.LingerSeconds.Value);
                    if (Plugin.LogTransitions.Value)
                    {
                        Plugin.Log.LogInfo(AllRowsComplete()
                            ? $"ロード完了 (所要 {CompletedTime:0.0}s)"
                            : $"自分のロード完了から {CloseGraceAfterMain:0}s 経過したため閉じます (未完了の人あり)");
                    }
                }
            }

            if (closeAtTime >= 0f && now >= closeAtTime)
            {
                closedGenerator = lg;
                EndSession("ロード完了");
            }
            else if (now - SessionStartTime > SessionTimeout)
            {
                closedGenerator = lg;
                EndSession("タイムアウト");
            }
        }

        private static bool LocalLoadFinished(LevelGenerator lg, bool lgValid)
        {
            return lgValid && lg.Generated && GameDirector.instance != null
                && GameDirector.instance.currentState >= GameDirector.gameState.Main;
        }

        /// <summary>
        /// ロード演出があるレベルか(GameDirector.gameStateStart と同じ条件)。
        /// 演出があるレベルでは、Main になるのは全員の演出終了を待ってから。
        /// 演出がないレベル(ロビー等)では、各プレイヤーの演出終了フラグが立たない。
        /// </summary>
        private static bool LevelHasLoadingAnimation(LevelGenerator lg)
        {
            Level level = lg.Level;
            if (level == null || RunManager.instance == null) return false;
            return SemiFunc.RunIsLevel(level) || SemiFunc.IsCurrentLevel(level, RunManager.instance.levelTutorial)
                || SemiFunc.IsLevelShop(level) || SemiFunc.IsLevelArena(level);
        }

        private bool AllRowsComplete()
        {
            if (Rows.Count == 0) return false;
            foreach (Row row in Rows)
            {
                if (row.Mask != Steps.AllMask) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 進捗の収集

        private void Poll(LevelGenerator lg, bool lgValid)
        {
            Player[] players = PhotonNetwork.PlayerList;
            bool isMaster = PhotonNetwork.IsMasterClient;

            // ホスト: Reload シーンで報告済みのプレイヤー
            if (isMaster && reloadScene != null)
            {
                foreach (Player p in reloadScene.PlayersReadyList)
                {
                    if (p != null) reloadReadyActors.Add(p.ActorNumber);
                }
            }

            // 全員に共通の段階
            int global = 0;
            if (lgValid)
            {
                if (lg.AllPlayersReady) global |= Steps.Bit(Steps.Avatar);
                if (lg.moduleAmountReceived || (isMaster && lg.State >= LevelGenerator.LevelState.ModuleSpawnLocal))
                {
                    global |= Steps.Bit(Steps.MapGen);
                }
                if (lg.EnemyReady) global |= Steps.Bit(Steps.Enemies);
                // 演出のないレベルでは個人の演出終了フラグが立たないので、自分の Main 到達で全員完了とみなす
                // (生成完了は全員のスポーン完了後なので、残りは演出だけ)
                if (LocalLoadFinished(lg, lgValid) && !LevelHasLoadingAnimation(lg))
                {
                    global |= Steps.Bit(Steps.Animation);
                }
            }

            bool changed = false;
            Rows.Clear();
            foreach (Player p in players)
            {
                if (p == null) continue;
                int actor = p.ActorNumber;
                PlayerAvatar avatar = lgValid ? FindAvatar(p) : null;

                int m = global;
                if (reloadReadyActors.Contains(actor)) m |= Steps.Bit(Steps.Reload);
                if (lgValid)
                {
                    if (isMaster && NetworkManager.instance != null
                        && NetworkManager.instance.instantiatedPlayerAvatarsList.Contains(p))
                    {
                        m |= Steps.Bit(Steps.Avatar);
                    }
                    if (lg.ModulesReadyPlayerList.Contains(p)) m |= Steps.Bit(Steps.Modules);
                    if (isMaster && lg.SpawnedReadyPlayerList.Contains(p)) m |= Steps.Bit(Steps.Spawn);
                    if (lg.EnemyReadyPlayerList.Contains(p)) m |= Steps.Bit(Steps.Enemies);
                    if (avatar != null && avatar.levelAnimationCompleted) m |= Steps.Bit(Steps.Animation);
                }
                if (p.IsLocal) m |= ObserveSelf(lg, lgValid, avatar);
                if (reported.TryGetValue(actor, out int r)) m |= r;

                // 生成完了の合図を受けた後、スポーン済みの人は生成完了とみなす
                if (lgValid && lg.Generated && Steps.Has(Steps.FillLower(m), Steps.Spawn))
                {
                    m |= Steps.Bit(Steps.Generated);
                }
                m = Steps.FillLower(m);

                masks.TryGetValue(actor, out int old);
                if (old != m)
                {
                    changed = true;
                    LogTransition(p, old, m);
                    masks[actor] = m;
                }

                Rows.Add(new Row
                {
                    Actor = actor,
                    Name = DisplayName(p, avatar),
                    IsMaster = p.IsMasterClient,
                    IsLocal = p.IsLocal,
                    Modded = p.IsLocal || moddedActors.Contains(actor),
                    Mask = m,
                    Detail = p.IsLocal ? SelfDetail(lg, lgValid, m) : null,
                });
            }
            Rows.Sort((a, b) => a.IsMaster != b.IsMaster ? (a.IsMaster ? -1 : 1) : a.Actor.CompareTo(b.Actor));

            if (Plugin.ShareProgress.Value && (changed || Time.unscaledTime >= nextResend))
            {
                nextResend = Time.unscaledTime + ResendInterval;
                Send(players, isMaster);
            }
        }

        private int ObserveSelf(LevelGenerator lg, bool lgValid, PlayerAvatar me)
        {
            int m = 0;
            if (sawReloadThisSession) m |= Steps.Bit(Steps.Reload);
            if (!lgValid) return m;

            if (me != null) m |= Steps.Bit(Steps.Avatar);
            switch (lg.navMeshBuildState)
            {
                case LevelGenerator.NavMeshBuildState.Collecting:
                case LevelGenerator.NavMeshBuildState.Building:
                case LevelGenerator.NavMeshBuildState.Validating:
                case LevelGenerator.NavMeshBuildState.Ready:
                    // ホストは貴重品の配置が終わってから NavMesh 構築の合図を出す
                    m |= Steps.Bit(Steps.Items);
                    break;
            }
            bool localReady = lg.LocalSetupReady;
            if (localReady) m |= Steps.Bit(Steps.NavMesh);
            if (me != null && !me.spawnImpulse && localReady) m |= Steps.Bit(Steps.Spawn);
            if (lg.enemyReadySent) m |= Steps.Bit(Steps.Enemies);
            if (lg.Generated) m |= Steps.Bit(Steps.Generated);
            if (me != null && me.levelAnimationCompleted) m |= Steps.Bit(Steps.Animation);
            return m;
        }

        private static string SelfDetail(LevelGenerator lg, bool lgValid, int mask)
        {
            if (!lgValid) return null;
            switch (Steps.Current(mask))
            {
                case Steps.Modules:
                    return lg.ModulesSpawnedTarget > 0 ? $"{lg.ModulesSpawned}/{lg.ModulesSpawnedTarget}" : null;
                case Steps.Enemies:
                    return lg.EnemiesSpawnTarget > 0 ? $"{lg.EnemiesSpawned}/{lg.EnemiesSpawnTarget}" : null;
                default:
                    return null;
            }
        }

        private static PlayerAvatar FindAvatar(Player p)
        {
            GameDirector gd = GameDirector.instance;
            if (gd == null || gd.PlayerList == null) return null;
            foreach (PlayerAvatar avatar in gd.PlayerList)
            {
                if (avatar != null && avatar.photonView != null && avatar.photonView.Owner != null
                    && avatar.photonView.Owner.ActorNumber == p.ActorNumber)
                {
                    return avatar;
                }
            }
            return null;
        }

        private static string DisplayName(Player p, PlayerAvatar avatar)
        {
            if (!string.IsNullOrEmpty(p.NickName)) return p.NickName;
            if (avatar != null && !string.IsNullOrEmpty(avatar.playerName)) return avatar.playerName;
            return $"Player {p.ActorNumber}";
        }

        private void LogTransition(Player p, int oldMask, int newMask)
        {
            if (!Plugin.LogTransitions.Value) return;
            int added = newMask & ~oldMask;
            for (int i = 0; i < Steps.Count; i++)
            {
                if (Steps.Has(added, i))
                {
                    Plugin.Log.LogInfo(
                        $"[{Time.unscaledTime - SessionStartTime,6:0.0}s] {DisplayName(p, null)}: {Steps.Header[i]} {Steps.Labels[i]} 完了");
                }
            }
        }

        // ---------------------------------------------------------------- ネットワーク

        private void Send(Player[] players, bool isMaster)
        {
            var actors = new List<int>();
            var values = new List<int>();
            foreach (Player p in players)
            {
                if (p == null) continue;
                // ホストは全員分、それ以外は自分の分だけ送る
                if (!isMaster && !p.IsLocal) continue;
                if (masks.TryGetValue(p.ActorNumber, out int m))
                {
                    actors.Add(p.ActorNumber);
                    values.Add(m);
                }
            }
            if (actors.Count == 0) return;

            object[] content = { ProtocolVersion, actors.ToArray(), values.ToArray() };
            var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(EventCode, content, options, SendOptions.SendReliable);
        }

        private void OnEvent(EventData e)
        {
            if (e.Code != EventCode) return;
            // 表示していない間の報告は捨てる(前のロードの報告が混ざらないように)。
            // 相手は 1 秒ごとに送り直すので、こちらのセッションが始まればすぐ届く。
            if (!SessionActive) return;

            if (!(e.CustomData is object[] data) || data.Length < 3) return;
            if (!(data[0] is byte version) || version != ProtocolVersion) return;
            if (!(data[1] is int[] actors) || !(data[2] is int[] values) || actors.Length != values.Length) return;

            Room room = PhotonNetwork.CurrentRoom;
            Player sender = room?.GetPlayer(e.Sender);
            if (sender == null) return;

            moddedActors.Add(e.Sender);
            for (int i = 0; i < actors.Length; i++)
            {
                // 本人の申告か、ホストからの情報だけ受け付ける
                if (actors[i] != e.Sender && !sender.IsMasterClient) continue;
                reported.TryGetValue(actors[i], out int cur);
                reported[actors[i]] = cur | (values[i] & Steps.AllMask);
            }
        }

        // ---------------------------------------------------------------- 表示

        private void OnGUI()
        {
            if (SessionActive && Rows.Count > 0)
            {
                Overlay.Draw(this);
            }
        }
    }
}
