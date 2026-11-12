using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace FloorRoles
{
    /// <summary>
    /// ショップでの決闘。同じ商品を 2 人以上でつかんだまま決まった時間がたつと、全員に 1D100 を振らせ、
    /// いちばん大きい数字を出した人が自爆する(小さいほうが勝ち)。
    /// セリフはその人の吹き出しと読み上げで出るので、MOD なしの参加者にも見える。
    /// 自爆した人の所持金は、ショップの所持金の決まりで金袋になって落ちる。
    /// </summary>
    internal static class Duel
    {
        private const string Section = "3. 決闘";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<float> HoldTime;
        private static ConfigEntry<string> OpeningLine;

        private class Contest
        {
            internal HashSet<PlayerAvatar> Players;
            internal float Time;
        }

        private static readonly Dictionary<PhysGrabObject, Contest> Contests = new Dictionary<PhysGrabObject, Contest>();
        /// <summary>決闘中の商品。</summary>
        private static readonly HashSet<PhysGrabObject> Fighting = new HashSet<PhysGrabObject>();
        /// <summary>決闘のあと、全員が手を離すまでの商品(続けて決闘にならないように)。</summary>
        private static readonly HashSet<PhysGrabObject> Cooldown = new HashSet<PhysGrabObject>();
        private static int generation = -1;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ショップで同じ商品を 2 人以上がつかみ続けると、1D100 で決闘する(大きい数字の人が自爆)");
            HoldTime = cfg.Bind(Section, "つかみ続ける時間(秒)", 3f,
                new ConfigDescription("同じ商品を同じ顔ぶれでつかみ続けてから決闘が始まるまでの時間", new AcceptableValueRange<float>(1f, 30f)));
            OpeningLine = cfg.Bind(Section, "最初のセリフ", "いざ尋常に、勝負！", "決闘が始まるときに、決闘する全員にしゃべらせるセリフ");
        }

        internal static void Tick()
        {
            if (!Enabled.Value || !SemiFunc.RunIsShop())
            {
                return;
            }
            if (generation != Phase.Generation)
            {
                generation = Phase.Generation;
                Contests.Clear();
                Fighting.Clear();
                Cooldown.Clear();
            }
            foreach (var item in ShopBudget.CurrentShopItems)
            {
                var grab = item ? item.physGrabObject : null;
                if (!grab)
                {
                    continue;
                }
                var players = Grabbers(grab);
                if (Fighting.Contains(grab))
                {
                    continue;
                }
                if (Cooldown.Contains(grab))
                {
                    if (players.Count == 0)
                    {
                        Cooldown.Remove(grab);
                    }
                    continue;
                }
                if (players.Count < 2)
                {
                    Contests.Remove(grab);
                    continue;
                }
                // 顔ぶれが変わったら数え直す(あとから来た人を、つかみ続けていないのに決闘させない)
                if (!Contests.TryGetValue(grab, out var contest) || !contest.Players.SetEquals(players))
                {
                    Contests[grab] = contest = new Contest { Players = new HashSet<PlayerAvatar>(players), Time = 0f };
                }
                contest.Time += Time.deltaTime;
                if (contest.Time >= HoldTime.Value)
                {
                    Contests.Remove(grab);
                    Fighting.Add(grab);
                    Runner.Instance.StartCoroutine(Fight(grab, players, Phase.Generation));
                }
            }
        }

        private static List<PlayerAvatar> Grabbers(PhysGrabObject grab)
        {
            return grab.playerGrabbing
                .Where(g => g && g.playerAvatar && !g.playerAvatar.deadSet)
                .Select(g => g.playerAvatar)
                .Distinct()
                .ToList();
        }

        private static IEnumerator Fight(PhysGrabObject grab, List<PlayerAvatar> players, int gen)
        {
            try
            {
                Plugin.Verbose($"決闘: {string.Join(" vs ", players.Select(Net.Name))}");
                foreach (var player in players)
                {
                    Net.Speak(player, OpeningLine.Value);
                }
                yield return new WaitForSeconds(2.5f);
                if (!Still(players, gen))
                {
                    yield break;
                }
                foreach (var player in players)
                {
                    Net.Speak(player, "1D100");
                }
                yield return new WaitForSeconds(1.5f);

                // いちばん大きい数字が 1 人に決まるまで、同じ数字の人だけで振り直す(毎回の数字をしゃべらせる)
                var contenders = players;
                PlayerAvatar loser = null;
                var log = new List<string>();
                while (loser == null)
                {
                    if (!Still(contenders, gen))
                    {
                        yield break;
                    }
                    var rolls = contenders.ToDictionary(p => p, p => Random.Range(1, 101));
                    foreach (var pair in rolls)
                    {
                        Net.Speak(pair.Key, $"{pair.Value}！");
                    }
                    log.Add(string.Join("、", rolls.Select(r => $"{Net.Name(r.Key)} {r.Value}")));
                    int max = rolls.Values.Max();
                    var top = rolls.Where(r => r.Value == max).Select(r => r.Key).ToList();
                    yield return new WaitForSeconds(2f);
                    if (top.Count == 1)
                    {
                        loser = top[0];
                        break;
                    }
                    // 同点: その人たちで振り直し
                    if (!Still(top, gen))
                    {
                        yield break;
                    }
                    foreach (var player in top)
                    {
                        Net.Speak(player, "同点！もう一回！");
                    }
                    contenders = top;
                    yield return new WaitForSeconds(2f);
                }
                Plugin.Verbose($"決闘: {string.Join(" → ", log)} → {Net.Name(loser)} が自爆");
                if (gen != Phase.Generation || !SemiFunc.RunIsShop() || !loser || loser.deadSet)
                {
                    yield break;
                }
                Net.Kill(loser);
            }
            finally
            {
                Fighting.Remove(grab);
                if (gen == Phase.Generation)
                {
                    Cooldown.Add(grab);
                }
            }
        }

        private static bool Still(List<PlayerAvatar> players, int gen)
        {
            return gen == Phase.Generation && SemiFunc.RunIsShop() && players.All(p => p && !p.deadSet);
        }

        internal static void OnLeavingLevel()
        {
            Contests.Clear();
            Fighting.Clear();
            Cooldown.Clear();
        }
    }
}
