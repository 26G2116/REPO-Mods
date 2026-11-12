using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    internal enum Role
    {
        None = -1,
        Carrier = 0,
        Fighter = 1,
        Special = 2,
    }

    /// <summary>
    /// トラック(ショップのあと)で役職を選び、次のレベルの開始時にステータス報酬を渡す。
    ///
    /// - トラックに入ったら、次のマップを先に抽選し、役職ごとの報酬(3 種類から 2 つ)を抽選してモニターに出す
    /// - 人数分のロールオーブを出す。トラックを出発したとき、ロールオーブを 1 つだけインベントリに入れている人は、
    ///   入れたスロット(1 運搬 / 2 戦闘 / 3 特殊)の役職になる。0 個・2 個以上なら役職なし
    /// - 報酬はゲーム本来のアップグレードと同じ(全員の PC で同じ処理が動く)ので、MOD なしの参加者にも効く。
    ///   上げるのは出発の瞬間(ショップで使ったのと同じ扱い)で、次のマップの開始時に反映される
    /// - 出発(モニターのチャット)はホストだけができる
    /// 役職特性・マップごとの追加特性は、あとで足す(今は報酬だけ)。
    /// </summary>
    internal static class Roles
    {
        private const string Section = "1. 役職";
        private const string RollOrbAsset = "Item Orb Torque";
        private const string RemovedFolder = "Items/Removed Items";

        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<bool> SpawnOrbs;
        private static ConfigEntry<bool> HostOnlyDeparture;
        private static ConfigEntry<bool> PrerollLevel;
        private static ConfigEntry<bool> ShowOnMonitor;
        private static ConfigEntry<int> RewardCount;
        private static ConfigEntry<int> RewardAmount;
        private static ConfigEntry<int> SpeedCap;
        private static ConfigEntry<int> LaunchCap;

        private class Stat
        {
            internal string Key;   // アップグレードの名前(ゲーム内部)
            internal string Label; // 表示名
            internal Func<int> Cap; // 上限(0 以下 = なし)
        }

        private static readonly Stat[][] RoleStats =
        {
            new[] // 運搬: スタミナ・パワー系
            {
                new Stat { Key = "Stamina", Label = "スタミナ" },
                new Stat { Key = "CrouchRest", Label = "しゃがみ回復" },
                new Stat { Key = "Strength", Label = "力" },
            },
            new[] // 戦闘: 体力・移動系
            {
                new Stat { Key = "Health", Label = "体力" },
                new Stat { Key = "Speed", Label = "スプリント速度", Cap = () => SpeedCap.Value },
                new Stat { Key = "Range", Label = "射程" },
            },
            new[] // 特殊: その他
            {
                new Stat { Key = "ExtraJump", Label = "追加ジャンプ" },
                new Stat { Key = "TumbleWings", Label = "タンブルウイング" },
                new Stat { Key = "Launch", Label = "タンブル発射", Cap = () => LaunchCap.Value },
            },
        };

        private static readonly string[] RoleNames = { "運搬", "戦闘", "特殊" };

        // ---- このトラックの状態 ----
        private static int lobbyGeneration = -1;
        private static Level nextLevel;
        private static List<Stat>[] draws;
        private static readonly List<ItemAttributes> SpawnedOrbs = new List<ItemAttributes>();
        /// <summary>ロールオーブを出す前からあった「購入したアイテム」の名前(片付けで消さない)。</summary>
        private static readonly HashSet<string> ItemsBeforeOrbs = new HashSet<string>();

        // ---- 出発時に渡した報酬(次のレベルの開始時にしゃべらせる) ----
        private static readonly Dictionary<string, Role> NextRoles = new Dictionary<string, Role>();
        private static readonly Dictionary<string, string> Announcements = new Dictionary<string, string>();
        private static int announceGeneration = -1;
        private static bool announceScheduled;

        /// <summary>今のレベルでの役職(役職特性をあとで足すときに使う)。</summary>
        internal static readonly Dictionary<string, Role> CurrentRoles = new Dictionary<string, Role>();

        private static Item rollOrb;
        private static float departureWarnTime;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "トラックで役職を選び、次のレベルでステータス報酬を渡す");
            SpawnOrbs = cfg.Bind(Section, "ロールオーブを出す", true, "トラックに入ったとき、人数分のロールオーブを各プレイヤーの前に出す");
            HostOnlyDeparture = cfg.Bind(Section, "出発はホストだけ", true, "トラックのモニター(チャット)で出発できるのをホストだけにする");
            PrerollLevel = cfg.Bind(Section, "次のマップを先に決める", true, "トラックに入った時点で次のマップを抽選し、モニターに出す");
            ShowOnMonitor = cfg.Bind(Section, "モニターに表示", true, "役職ごとの報酬をトラックのモニターに出す");
            RewardCount = cfg.Bind(Section, "報酬の数", 2,
                new ConfigDescription("役職ごとの 3 種類のアップグレードから、いくつ抽選するか", new AcceptableValueRange<int>(1, 3)));
            RewardAmount = cfg.Bind(Section, "報酬の上がり幅", 1,
                new ConfigDescription("報酬 1 つあたり、アップグレードをいくつ上げるか", new AcceptableValueRange<int>(1, 5)));
            SpeedCap = cfg.Bind(Section, "スプリント速度の上限", 4,
                new ConfigDescription("役職の報酬でスプリント速度をここまでしか上げない(0 = 上限なし)", new AcceptableValueRange<int>(0, 99)));
            LaunchCap = cfg.Bind(Section, "タンブル発射の上限", 2,
                new ConfigDescription("役職の報酬でタンブル発射をここまでしか上げない(0 = 上限なし)", new AcceptableValueRange<int>(0, 99)));
        }

        // ================================================================
        // 毎フレーム(ホスト)
        // ================================================================

        internal static void Tick()
        {
            if (!Enabled.Value)
            {
                return;
            }
            if (SemiFunc.RunIsLobby() && lobbyGeneration != Phase.Generation)
            {
                lobbyGeneration = Phase.Generation;
                Runner.Instance.StartCoroutine(LobbyStart(Phase.Generation));
            }
            if (SemiFunc.RunIsLevel() && NextRoles.Count > 0 && announceGeneration != Phase.Generation && !announceScheduled)
            {
                announceScheduled = true;
                Runner.Instance.StartCoroutine(StartLevelRoles(Phase.Generation));
            }
        }

        // ================================================================
        // トラックに入ったとき
        // ================================================================

        private static IEnumerator LobbyStart(int generation)
        {
            // プレイヤーがトラックの中に立つまで少し待つ
            yield return new WaitForSeconds(1.5f);
            if (generation != Phase.Generation || !SemiFunc.RunIsLobby())
            {
                yield break;
            }

            nextLevel = PrerollLevel.Value ? PickNextLevel() : null;
            draws = DrawRewards();
            SpawnedOrbs.Clear();
            if (SpawnOrbs.Value)
            {
                SpawnRollOrbs();
            }
            Plugin.Verbose($"トラック: 次のマップ {LevelName(nextLevel)}、報酬 {DescribeDraws(draws)}");

            if (!ShowOnMonitor.Value)
            {
                yield break;
            }
            // モニターの最初の文章を打ち終わるまで待ってから書く
            float waited = 0f;
            while (generation == Phase.Generation && SemiFunc.RunIsLobby() && waited < 60f)
            {
                if (Net.TruckMessage(MonitorText()))
                {
                    yield break;
                }
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }
        }

        private static string MonitorText()
        {
            var text = new StringBuilder();
            if (nextLevel)
            {
                text.Append("次のマップ: ").Append(LevelName(nextLevel)).Append('\n');
            }
            text.Append("ロールオーブを1つだけスロットに入れて役職を選べ\n");
            for (int role = 0; role < 3; role++)
            {
                text.Append(role + 1).Append('.').Append(RoleNames[role]).Append(" 報酬:");
                text.Append(string.Join("、", draws[role].Select(s => $"{s.Label}+{RewardAmount.Value}")));
                if (role < 2)
                {
                    text.Append('\n');
                }
            }
            return text.ToString();
        }

        private static List<Stat>[] DrawRewards()
        {
            var result = new List<Stat>[3];
            for (int role = 0; role < 3; role++)
            {
                var pool = RoleStats[role].ToList();
                var picked = new List<Stat>();
                int count = Mathf.Clamp(RewardCount.Value, 1, pool.Count);
                for (int i = 0; i < count; i++)
                {
                    int index = UnityEngine.Random.Range(0, pool.Count);
                    picked.Add(pool[index]);
                    pool.RemoveAt(index);
                }
                result[role] = picked;
            }
            return result;
        }

        private static string DescribeDraws(List<Stat>[] value)
        {
            if (value == null)
            {
                return "-";
            }
            return string.Join(" / ", value.Select((list, i) => $"{RoleNames[i]}: {string.Join("、", list.Select(s => s.Label))}"));
        }

        // ---- 次のマップ ----

        /// <summary>ゲームと同じ決め方(前回のマップ以外からランダム)で、次のマップを先に決める。</summary>
        private static Level PickNextLevel()
        {
            var run = RunManager.instance;
            if (!run || run.levels == null || run.levels.Count == 0)
            {
                return null;
            }
            var levels = run.levels.FindAll(l => l);
            var available = levels.FindAll(l => l != run.previousRunLevel);
            if (available.Count == 0)
            {
                available = levels;
            }
            return available.Count > 0 ? available[UnityEngine.Random.Range(0, available.Count)] : null;
        }

        internal static string LevelName(Level level)
        {
            if (!level)
            {
                return "?";
            }
            try
            {
                if (level.NarrativeNameLocalized)
                {
                    string localized = level.NarrativeNameLocalized.GetLocalizedString();
                    if (!string.IsNullOrEmpty(localized))
                    {
                        return localized;
                    }
                }
            }
            catch (Exception)
            {
                // 翻訳がなければ下の名前を使う
            }
            return string.IsNullOrEmpty(level.NarrativeName) ? level.name : level.NarrativeName;
        }

        [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetRunLevel))]
        private static class SetRunLevelPatch
        {
            private static bool Prefix(RunManager __instance)
            {
                if (!Net.IsHost || !Enabled.Value || !nextLevel || __instance.levels == null || !__instance.levels.Contains(nextLevel))
                {
                    return true;
                }
                __instance.levelCurrent = nextLevel;
                Plugin.Verbose($"次のマップを {LevelName(nextLevel)} にしました(トラックで抽選済み)");
                nextLevel = null;
                return false;
            }
        }

        // ---- ロールオーブ ----

        private static Item RollOrb()
        {
            if (rollOrb)
            {
                return rollOrb;
            }
            if (StatsManager.instance && StatsManager.instance.itemDictionary.TryGetValue(RollOrbAsset, out var item) && item)
            {
                rollOrb = item;
                return rollOrb;
            }
            rollOrb = Resources.LoadAll<Item>(RemovedFolder).FirstOrDefault(i => i && i.name == RollOrbAsset);
            if (!rollOrb)
            {
                Plugin.Log.LogWarning($"ロールオーブ({RollOrbAsset})が見つかりませんでした");
            }
            return rollOrb;
        }

        private static bool IsRollOrb(ItemAttributes attributes)
        {
            return attributes && attributes.item && attributes.item.name == RollOrbAsset;
        }

        private static void SpawnRollOrbs()
        {
            var item = RollOrb();
            if (!item)
            {
                return;
            }
            // 出したオーブは、購入済みのロールオーブ(トラックに出なかった分)の名前を借りることがある。
            // その名前は片付けで消さないように、出す前の一覧を覚えておく
            ItemsBeforeOrbs.Clear();
            if (StatsManager.instance)
            {
                ItemsBeforeOrbs.UnionWith(StatsManager.instance.item.Keys);
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                var forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                var center = player.transform.position + Vector3.up * 1.0f;
                var position = center + forward * 0.6f;
                if (Physics.Linecast(center, position, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore))
                {
                    position = center; // 目の前が壁なら、その人の位置に出す
                }
                var spawned = Net.SpawnRoomObject(item.prefab.ResourcePath, item.prefab.Prefab, position, Quaternion.identity);
                var attributes = spawned ? spawned.GetComponent<ItemAttributes>() : null;
                if (attributes)
                {
                    SpawnedOrbs.Add(attributes);
                }
            }
            Plugin.Verbose($"ロールオーブを {SpawnedOrbs.Count} 個出しました");
        }

        // ================================================================
        // トラックを出発したとき(ホスト)
        // ================================================================

        internal static void OnLeavingLevel(bool completed)
        {
            if (Enabled.Value && SemiFunc.RunIsLobby() && completed && draws != null)
            {
                DecideRoles();
                GrantRewards();
                draws = null; // 次のトラックで抽選し直す(古い抽選を使わない)
            }
            else if (!SemiFunc.RunIsLobby())
            {
                // レベルを出るときは、そのレベルの役職をおしまいにする
                CurrentRoles.Clear();
            }
            CleanupOrbs();
            announceScheduled = false;
        }

        private static void DecideRoles()
        {
            NextRoles.Clear();
            Announcements.Clear();
            // インベントリに入っているロールオーブを、持ち主ごとにまとめる
            var spots = new Dictionary<PlayerAvatar, List<int>>();
            foreach (var equippable in Object.FindObjectsByType<ItemEquippable>(FindObjectsSortMode.None))
            {
                if (!equippable || !equippable.IsEquipped() || !IsRollOrb(equippable.GetComponent<ItemAttributes>()))
                {
                    continue;
                }
                var owner = equippable.GetOwnerPlayerAvatar();
                if (!owner)
                {
                    continue;
                }
                if (!spots.TryGetValue(owner, out var list))
                {
                    spots[owner] = list = new List<int>();
                }
                list.Add(equippable.inventorySpotIndex);
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || string.IsNullOrEmpty(player.steamID))
                {
                    continue;
                }
                var role = Role.None;
                if (spots.TryGetValue(player, out var list) && list.Count == 1 && list[0] >= 0 && list[0] <= 2)
                {
                    role = (Role)list[0];
                }
                if (role != Role.None)
                {
                    NextRoles[player.steamID] = role;
                }
                Plugin.Verbose($"役職: {Net.Name(player)} = {(role == Role.None ? "なし" : RoleNames[(int)role])}(ロールオーブ {(list == null ? 0 : list.Count)} 個)");
            }
        }

        /// <summary>
        /// 出したロールオーブを「購入したアイテム」の記録から消す(次のレベルのトラックに出てこないように)。
        /// 出したアイテムは、ゲームが名前を付けるときに購入したアイテムの一覧へ足してしまう。
        /// </summary>
        private static void CleanupOrbs()
        {
            var stats = StatsManager.instance;
            if (stats)
            {
                foreach (var attributes in SpawnedOrbs)
                {
                    // 壊れたアイテムでも名前は読める(Unity の null 判定は使わない)
                    string name = (object)attributes != null ? attributes.instanceName : null;
                    if (string.IsNullOrEmpty(name) || ItemsBeforeOrbs.Contains(name))
                    {
                        continue;
                    }
                    stats.item.Remove(name);
                    stats.itemStatBattery.Remove(name);
                    int hash = name.GetHashCode();
                    RemoveSpot(stats.playerInventorySpot1, hash);
                    RemoveSpot(stats.playerInventorySpot2, hash);
                    RemoveSpot(stats.playerInventorySpot3, hash);
                }
            }
            SpawnedOrbs.Clear();
        }

        private static void RemoveSpot(Dictionary<string, int> spots, int hash)
        {
            if (spots == null)
            {
                return;
            }
            foreach (var key in spots.Where(p => p.Value == hash).Select(p => p.Key).ToList())
            {
                spots.Remove(key);
            }
        }

        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.ItemRemove))]
        private static class ItemRemovePatch
        {
            // ロールオーブ(電池で使い切る)が壊れても、購入したロールオーブの数を減らさない
            private static bool Prefix(StatsManager __instance, string instanceName)
            {
                if (string.IsNullOrEmpty(instanceName) || ItemsBeforeOrbs.Contains(instanceName) ||
                    !SpawnedOrbs.Any(a => (object)a != null && a.instanceName == instanceName))
                {
                    return true;
                }
                __instance.item.Remove(instanceName);
                __instance.itemStatBattery.Remove(instanceName);
                return false;
            }
        }

        // ================================================================
        // 報酬は出発した瞬間に渡す(ホスト)
        // ================================================================

        /// <summary>
        /// アップグレードは出発の瞬間(次のマップを読み込む前)に上げる。ショップでアップグレードを使ったのと同じ扱いになり、
        /// 次のマップの開始時に、全員の PC がアップグレードの数を読み直して HP・スタミナ・スプリント速度などに反映する。
        /// (マップが始まってから上げると、読み込みの遅い人の PC で、開始時の読み直しと二重に足されることがある)
        /// </summary>
        private static void GrantRewards()
        {
            foreach (var pair in NextRoles)
            {
                var labels = new List<string>();
                foreach (var stat in ResolveStats(pair.Key, pair.Value))
                {
                    int amount = RewardAmount.Value;
                    int cap = stat.Cap != null ? stat.Cap() : 0;
                    if (cap > 0)
                    {
                        amount = Mathf.Min(amount, cap - Net.UpgradeLevel(pair.Key, stat.Key));
                    }
                    if (amount <= 0)
                    {
                        continue;
                    }
                    Net.Upgrade(pair.Key, stat.Key, amount);
                    labels.Add($"{stat.Label}+{amount}");
                }
                string line = $"{RoleNames[(int)pair.Value]}: " + (labels.Count > 0 ? string.Join("、", labels) : "報酬なし(上限)");
                Announcements[pair.Key] = line;
                Plugin.Verbose($"報酬: {pair.Key} {line}");
            }
        }

        /// <summary>次のレベルが始まったら、その人の役職を有効にし、もらった報酬をしゃべらせる。</summary>
        private static IEnumerator StartLevelRoles(int generation)
        {
            // 全員のアバターができてから
            yield return new WaitForSeconds(2f);
            if (generation != Phase.Generation || !SemiFunc.RunIsLevel())
            {
                announceScheduled = false;
                yield break;
            }
            announceGeneration = generation;
            CurrentRoles.Clear();
            foreach (var pair in NextRoles)
            {
                CurrentRoles[pair.Key] = pair.Value;
                var player = SemiFunc.PlayerAvatarGetFromSteamID(pair.Key);
                if (player && Announcements.TryGetValue(pair.Key, out var line))
                {
                    Net.Speak(player, line);
                }
            }
            NextRoles.Clear();
            Announcements.Clear();
            announceScheduled = false;
        }

        /// <summary>抽選した報酬のうち、上限に達しているものは、同じ役職の残りに差し替える。</summary>
        private static List<Stat> ResolveStats(string steamID, Role role)
        {
            var drawn = draws != null ? draws[(int)role] : new List<Stat>();
            var result = new List<Stat>();
            var spare = RoleStats[(int)role].Where(s => !drawn.Contains(s)).ToList();
            foreach (var stat in drawn)
            {
                if (!Capped(steamID, stat))
                {
                    result.Add(stat);
                    continue;
                }
                var substitute = spare.FirstOrDefault(s => !Capped(steamID, s));
                if (substitute != null)
                {
                    spare.Remove(substitute);
                    result.Add(substitute);
                }
            }
            return result;
        }

        private static bool Capped(string steamID, Stat stat)
        {
            int cap = stat.Cap != null ? stat.Cap() : 0;
            return cap > 0 && Net.UpgradeLevel(steamID, stat.Key) >= cap;
        }

        // ================================================================
        // 出発はホストだけ
        // ================================================================

        [HarmonyPatch(typeof(TruckScreenText), nameof(TruckScreenText.HoldChat))]
        private static class HoldChatPatch
        {
            // モニターのチャットは、つかみ続けて文字が打ち終わると出発の合図になる。
            // 送るのはホストなので、ホスト以外がつかんでいるときは打ち終わる直前で巻き戻す
            private static void Prefix(TruckScreenText __instance)
            {
                if (!Enabled.Value || !HostOnlyDeparture.Value || !SemiFunc.IsMultiplayer() || !SemiFunc.IsMasterClient() || !SemiFunc.RunIsLobby())
                {
                    return;
                }
                if (!__instance.chatActive || !__instance.staticGrabObject || __instance.staticGrabObject.playerGrabbing.Count == 0)
                {
                    return;
                }
                var grabber = __instance.staticGrabObject.playerGrabbing[0];
                if (!grabber || grabber.playerAvatar == SemiFunc.PlayerAvatarLocal())
                {
                    return;
                }
                if (__instance.chatMessageTimer + 1.5f * Time.deltaTime * 2f < __instance.chatMessageTimerMax)
                {
                    return;
                }
                __instance.chatMessageTimer = 0f;
                __instance.chatCharacterIndex = 0;
                if (Time.unscaledTime - departureWarnTime > 4f)
                {
                    departureWarnTime = Time.unscaledTime;
                    Net.Speak(grabber.playerAvatar, "出発はホストだけ！");
                }
            }
        }
    }
}
