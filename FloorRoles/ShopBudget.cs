using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    /// <summary>
    /// ショップでの個人の所持金。
    ///
    /// - ショップに入ったら、予算(チームのお金)を人数で分けた金袋(小)を抽出口のまわりに出す(最大 20 個、ショップでは無敵)
    /// - 金袋を最初につかんだ人の所持金になり、金袋は壊れる
    /// - 商品は「最後に触った人」の所持金で買う。足りない商品は買わない(精算口から出したまま残る)
    /// - 所持金を持った人が死ぬ・抜けると、その場に所持金分の金袋を落とす。タンブル中は 1K ずつ落とす(譲渡)
    /// 所持金はホストの MOD だけが記録する数字で、チームのお金(ゲームの所持金)から引くのは本来どおり。
    /// ショップごとに配り直すので、使わなかった所持金はチームのお金に戻り、次のショップでまた分けられる。
    /// </summary>
    internal static class ShopBudget
    {
        private const string Section = "2. ショップの所持金";

        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<int> MaxBags;
        private static ConfigEntry<bool> DropOnDeath;
        private static ConfigEntry<bool> DropOnLeave;
        private static ConfigEntry<bool> TumbleDrop;
        private static ConfigEntry<float> TumbleDropInterval;
        private static ConfigEntry<int> TumbleDropAmount;
        private static ConfigEntry<bool> Announce;

        private class Bag
        {
            internal ValuableObject Valuable;
            internal PhysGrabObject Grab;
            internal PhysGrabObjectImpactDetector Impact;
            internal SurplusValuable Surplus;
            internal int Thousands;
        }

        private class PlayerState
        {
            internal string Name;
            internal Vector3 LastPosition;
            internal bool WasDead;
            internal float TumbleTimer;
        }

        /// <summary>所持金(K = $1,000)。steamID ごと。</summary>
        internal static readonly Dictionary<string, int> Budgets = new Dictionary<string, int>();

        private static readonly List<Bag> Bags = new List<Bag>();
        private static readonly Dictionary<string, PlayerState> Players = new Dictionary<string, PlayerState>();
        /// <summary>商品ごとに、最後に触った人の steamID。</summary>
        private static readonly Dictionary<ItemAttributes, string> LastToucher = new Dictionary<ItemAttributes, string>();
        private static readonly Dictionary<ItemAttributes, HashSet<PhysGrabber>> PreviousGrabbers = new Dictionary<ItemAttributes, HashSet<PhysGrabber>>();
        private static readonly List<ItemAttributes> ShopItems = new List<ItemAttributes>();
        private static float shopItemsRefresh;
        private static int shopItemsGeneration = -1;
        private static int shopGeneration = -1;
        private static bool active;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ショップで予算を金袋に分けて配り、一人ずつの所持金で買い物をする");
            MaxBags = cfg.Bind(Section, "最初の金袋の最大数", 20,
                new ConfigDescription("最初に出す金袋の数(人数分。これより多くはしない)", new AcceptableValueRange<int>(1, 40)));
            DropOnDeath = cfg.Bind(Section, "死んだら落とす", true, "所持金を持った人がショップで死んだら、所持金分の金袋をその場に落とす");
            DropOnLeave = cfg.Bind(Section, "抜けたら落とす", true, "所持金を持った人が抜けたら、最後にいた場所に所持金分の金袋を落とす");
            TumbleDrop = cfg.Bind(Section, "タンブルで落とす", true, "タンブル中は、所持金を少しずつ金袋にして落とす(ほかの人に渡す方法)");
            TumbleDropInterval = cfg.Bind(Section, "タンブルで落とす間隔(秒)", 0.5f,
                new ConfigDescription("タンブル中に金袋を落とす間隔", new AcceptableValueRange<float>(0.1f, 5f)));
            TumbleDropAmount = cfg.Bind(Section, "タンブルで落とす金額(K)", 1,
                new ConfigDescription("タンブル中に 1 回で落とす金額", new AcceptableValueRange<int>(1, 100)));
            Announce = cfg.Bind(Section, "しゃべって知らせる", true, "金袋を拾ったときや、お金が足りなくて買えなかったときに、その人にしゃべらせる");
        }

        private static bool On => Enabled.Value && active && SemiFunc.RunIsShop();

        internal static int Get(string steamID)
        {
            return string.IsNullOrEmpty(steamID) ? 0 : Budgets.GetValueOrDefault(steamID, 0);
        }

        private static void Add(string steamID, int thousands)
        {
            if (string.IsNullOrEmpty(steamID))
            {
                return;
            }
            Budgets[steamID] = Mathf.Max(0, Get(steamID) + thousands);
        }

        // ================================================================
        // 毎フレーム(ホスト)
        // ================================================================

        internal static void Tick()
        {
            if (!Enabled.Value || !SemiFunc.RunIsShop())
            {
                return;
            }
            if (shopGeneration != Phase.Generation)
            {
                shopGeneration = Phase.Generation;
                Runner.Instance.StartCoroutine(ShopStart(Phase.Generation));
            }
            if (!active)
            {
                return;
            }
            TickBags();
            TickShopItems();
            TickPlayers();
        }

        private static IEnumerator ShopStart(int generation)
        {
            Budgets.Clear();
            Bags.Clear();
            Players.Clear();
            LastToucher.Clear();
            PreviousGrabbers.Clear();
            ShopItems.Clear();
            active = true;
            // プレイヤーがそろうまで少し待つ
            yield return new WaitForSeconds(1.5f);
            if (generation != Phase.Generation || !SemiFunc.RunIsShop())
            {
                yield break;
            }
            int total = SemiFunc.StatGetRunCurrency();
            var players = SemiFunc.PlayerGetList().Where(p => p).ToList();
            int count = Mathf.Clamp(players.Count, 1, MaxBags.Value);
            if (total <= 0)
            {
                yield break;
            }
            // 7 人で 100K なら 14K × 5 と 15K × 2 のように、なるべく同じ金額に分ける
            count = Mathf.Min(count, total);
            int baseAmount = total / count;
            int remainder = total % count;
            var center = SpawnCenter();
            for (int i = 0; i < count; i++)
            {
                int amount = baseAmount + (i < remainder ? 1 : 0);
                SpawnBag(Net.FreeSpot(center, 0.8f, 2.2f), amount);
            }
            Plugin.Verbose($"ショップ: 予算 {total}K を {count} 個の金袋に分けました");
        }

        /// <summary>金袋を出す場所: ショップの抽出口の上。見つからなければホストの前。</summary>
        private static Vector3 SpawnCenter()
        {
            var point = Object.FindObjectsByType<ExtractionPoint>(FindObjectsSortMode.None).FirstOrDefault(p => p && p.isShop);
            if (point)
            {
                return point.transform.position + Vector3.up * 1.2f;
            }
            var host = SemiFunc.PlayerAvatarLocal();
            return host ? host.transform.position + host.transform.forward * 1.5f + Vector3.up : Vector3.up;
        }

        private static void SpawnBag(Vector3 position, int thousands)
        {
            var valuable = Net.SpawnMoneyBag(position, thousands);
            if (!valuable)
            {
                return;
            }
            var bag = new Bag
            {
                Valuable = valuable,
                Grab = valuable.GetComponent<PhysGrabObject>(),
                Impact = valuable.GetComponentInChildren<PhysGrabObjectImpactDetector>(),
                Surplus = valuable.GetComponent<SurplusValuable>(),
                Thousands = thousands,
            };
            if (bag.Impact)
            {
                bag.Impact.destroyInvestigate = false; // 壊したときに敵を呼ばない
            }
            Bags.Add(bag);
        }

        private static void TickBags()
        {
            for (int i = Bags.Count - 1; i >= 0; i--)
            {
                var bag = Bags[i];
                if (!bag.Valuable || !bag.Grab || bag.Grab.dead)
                {
                    Bags.RemoveAt(i);
                    continue;
                }
                // ショップでは無敵(金袋自身の「出てから 3 秒だけ無敵」の解除も止める)
                if (bag.Surplus)
                {
                    bag.Surplus.indestructibleTimer = 0f;
                }
                if (bag.Impact)
                {
                    bag.Impact.destroyDisable = true;
                }
                if (bag.Grab.playerGrabbing.Count == 0)
                {
                    continue;
                }
                var grabber = bag.Grab.playerGrabbing[0];
                var player = grabber ? grabber.playerAvatar : null;
                if (!player || string.IsNullOrEmpty(player.steamID))
                {
                    continue;
                }
                Add(player.steamID, bag.Thousands);
                if (bag.Impact)
                {
                    bag.Impact.DestroyObject(true, true);
                }
                else
                {
                    bag.Grab.DestroyPhysGrabObject();
                }
                Bags.RemoveAt(i);
                Plugin.Verbose($"所持金: {Net.Name(player)} +{bag.Thousands}K → {Get(player.steamID)}K");
                if (Announce.Value)
                {
                    Net.Speak(player, $"{bag.Thousands}K ゲット！所持金 {Get(player.steamID)}K");
                }
            }
        }

        private static void TickShopItems()
        {
            foreach (var item in CurrentShopItems)
            {
                if (!item || !item.physGrabObject)
                {
                    continue;
                }
                // 新しくつかみ始めた人を「最後に触った人」にする(先につかんでいた人が残っていても上書きしない)
                if (!PreviousGrabbers.TryGetValue(item, out var previous))
                {
                    PreviousGrabbers[item] = previous = new HashSet<PhysGrabber>();
                }
                foreach (var grabber in item.physGrabObject.playerGrabbing)
                {
                    if (grabber && !previous.Contains(grabber) && grabber.playerAvatar && !string.IsNullOrEmpty(grabber.playerAvatar.steamID))
                    {
                        LastToucher[item] = grabber.playerAvatar.steamID;
                    }
                }
                previous.Clear();
                foreach (var grabber in item.physGrabObject.playerGrabbing)
                {
                    if (grabber)
                    {
                        previous.Add(grabber);
                    }
                }
            }
        }

        /// <summary>今のショップの商品(1 秒ごとに探し直す)。決闘でも使う。</summary>
        internal static IReadOnlyList<ItemAttributes> CurrentShopItems
        {
            get
            {
                if (Time.time >= shopItemsRefresh || shopItemsGeneration != Phase.Generation)
                {
                    shopItemsRefresh = Time.time + 1f;
                    shopItemsGeneration = Phase.Generation;
                    ShopItems.Clear();
                    ShopItems.AddRange(Object.FindObjectsByType<ItemAttributes>(FindObjectsSortMode.None).Where(a => a && a.shopItem));
                }
                return ShopItems;
            }
        }

        private static void TickPlayers()
        {
            var present = new HashSet<string>();
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || string.IsNullOrEmpty(player.steamID))
                {
                    continue;
                }
                present.Add(player.steamID);
                if (!Players.TryGetValue(player.steamID, out var state))
                {
                    Players[player.steamID] = state = new PlayerState { WasDead = player.deadSet };
                }
                state.Name = player.playerName;
                if (!player.deadSet)
                {
                    state.LastPosition = player.transform.position;
                }

                // 死んだ
                if (player.deadSet && !state.WasDead && DropOnDeath.Value)
                {
                    DropAll(player.steamID, state.LastPosition, $"{state.Name} が死んだ");
                }
                state.WasDead = player.deadSet;

                // タンブル中は少しずつ落とす
                bool tumbling = player.tumble && player.tumble.isTumbling && !player.deadSet;
                if (tumbling && TumbleDrop.Value && Get(player.steamID) > 0)
                {
                    state.TumbleTimer += Time.deltaTime;
                    if (state.TumbleTimer >= TumbleDropInterval.Value)
                    {
                        state.TumbleTimer = 0f;
                        int amount = Mathf.Min(TumbleDropAmount.Value, Get(player.steamID));
                        Add(player.steamID, -amount);
                        var center = player.tumble.physGrabObject ? player.tumble.physGrabObject.centerPoint : player.transform.position;
                        SpawnBag(center + Vector3.up * 0.6f + UnityEngine.Random.insideUnitSphere * 0.2f, amount);
                    }
                }
                else
                {
                    state.TumbleTimer = 0f;
                }
            }

            // 抜けた
            foreach (var steamID in Players.Keys.Where(id => !present.Contains(id)).ToList())
            {
                var state = Players[steamID];
                if (DropOnLeave.Value)
                {
                    DropAll(steamID, state.LastPosition, $"{state.Name} が抜けた");
                }
                Players.Remove(steamID);
            }
        }

        private static void DropAll(string steamID, Vector3 position, string reason)
        {
            int amount = Get(steamID);
            if (amount <= 0)
            {
                return;
            }
            Budgets[steamID] = 0;
            SpawnBag(position + Vector3.up * 0.8f, amount);
            Plugin.Verbose($"所持金: {reason}ので {amount}K の金袋を落としました");
        }

        // ================================================================
        // 買い物(ホスト)
        // ================================================================

        private static string Buyer(ItemAttributes item)
        {
            return item && LastToucher.TryGetValue(item, out var id) ? id : null;
        }

        /// <summary>
        /// 精算口の商品を、買う人ごとの所持金で買えるものだけに絞る(リストの順に、足りなくなったら外す)。
        /// 外した商品は精算口に残る(一度出して入れ直すと、また精算の対象になる)。
        /// </summary>
        private static void FilterAffordable(List<ItemAttributes> list)
        {
            var spent = new Dictionary<string, int>();
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (!item)
                {
                    continue; // ゲームのほうで片付ける
                }
                string buyer = Buyer(item);
                int used = buyer != null ? spent.GetValueOrDefault(buyer, 0) : 0;
                if (buyer != null && used + item.value <= Get(buyer))
                {
                    spent[buyer] = used + item.value;
                    continue;
                }
                list.RemoveAt(i);
                i--;
                Rejected(item, buyer);
            }
        }

        private static readonly Dictionary<string, float> RejectSpeakTimes = new Dictionary<string, float>();

        private static void Rejected(ItemAttributes item, string buyer)
        {
            // 精算口に置いたままでも、また精算の対象になるようにする(ゲームが 0.5 秒ごとに入れ直す)
            item.inStartRoom = false;
            Plugin.Verbose($"買い物: {item.item.itemName}({item.value}K)は {(buyer ?? "触った人なし")} の所持金 {Get(buyer)}K では買えない");
            if (!Announce.Value || buyer == null)
            {
                return;
            }
            // 同じ人が続けてしゃべらないように
            if (RejectSpeakTimes.TryGetValue(buyer, out float last) && Time.time - last < 3f)
            {
                return;
            }
            RejectSpeakTimes[buyer] = Time.time;
            Net.Speak(SemiFunc.PlayerAvatarGetFromSteamID(buyer), $"お金が足りない！所持金 {Get(buyer)}K");
        }

        private static void Charge(ItemAttributes item)
        {
            string buyer = Buyer(item);
            if (buyer == null)
            {
                return;
            }
            Add(buyer, -item.value);
            Plugin.Verbose($"買い物: {buyer} が {item.item.itemName} を {item.value}K で購入 → 残り {Get(buyer)}K");
        }

        [HarmonyPatch(typeof(ShopManager), nameof(ShopManager.ShopCheck))]
        private static class ShopCheckPatch
        {
            // 精算口が閉まるときの「合計金額 ≦ チームのお金」の判定の前に、買えない商品を外す
            private static void Postfix(ShopManager __instance)
            {
                if (!Net.IsHost || !On)
                {
                    return;
                }
                FilterAffordable(__instance.shoppingList);
                __instance.totalCost = __instance.shoppingList.Where(i => i).Sum(i => i.value);
                SemiFunc.ShopUpdateCost();
            }
        }

        /// <summary>
        /// 精算の処理の中で商品が消されたら、それが「買った」こと(ゲームは買った商品だけを消す)。
        /// 精算口から押し出された商品もリストからは外れるので、リストではなく「消されたか」で数える。
        /// </summary>
        private static bool buying;

        [HarmonyPatch(typeof(ExtractionPoint), nameof(ExtractionPoint.DestroyTheFirstPhysObjectsInShopList))]
        private static class BuyOnePatch
        {
            private static void Prefix()
            {
                if (!Net.IsHost || !On || !ShopManager.instance)
                {
                    return;
                }
                var list = ShopManager.instance.shoppingList;
                // 吸い込み中に入れ直された商品も、ここでもう一度確かめる
                while (list.Count > 0 && list[0] && !CanAfford(list[0]))
                {
                    var item = list[0];
                    list.RemoveAt(0);
                    Rejected(item, Buyer(item));
                }
                buying = true;
            }

            private static void Finalizer()
            {
                buying = false;
            }
        }

        [HarmonyPatch(typeof(ExtractionPoint), nameof(ExtractionPoint.DestroyAllPhysObjectsInShoppingList))]
        private static class BuyAllPatch
        {
            private static void Prefix()
            {
                if (!Net.IsHost || !On || !ShopManager.instance)
                {
                    return;
                }
                FilterAffordable(ShopManager.instance.shoppingList);
                buying = true;
            }

            private static void Finalizer()
            {
                buying = false;
            }
        }

        [HarmonyPatch(typeof(PhysGrabObject), nameof(PhysGrabObject.DestroyPhysGrabObject))]
        private static class DestroyPatch
        {
            private static void Prefix(PhysGrabObject __instance)
            {
                if (!buying || !Net.IsHost || !On)
                {
                    return;
                }
                var item = __instance.GetComponent<ItemAttributes>();
                if (item)
                {
                    Charge(item);
                }
            }
        }

        private static bool CanAfford(ItemAttributes item)
        {
            string buyer = Buyer(item);
            return buyer != null && item.value <= Get(buyer);
        }

        internal static void OnLeavingLevel()
        {
            active = false;
            Budgets.Clear();
            Bags.Clear();
            Players.Clear();
            LastToucher.Clear();
            PreviousGrabbers.Clear();
            ShopItems.Clear();
            RejectSpeakTimes.Clear();
        }
    }
}
