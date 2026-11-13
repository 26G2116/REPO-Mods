using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FloorRoles
{
    /// <summary>
    /// チャットで「/r」と打つと 1D100 を振る(MOD なしの参加者が打っても動く)。
    ///
    /// - MOD なしの人の「/r」も、ふつうのチャットとしてホストに届く(ゲームに r というコマンドはない)
    /// - ホストがその人に「1D100 Start!」→「出た数字!」→(1〜5 なら)「critical!」/(96〜100 なら)「fumble!」としゃべらせる
    ///   クリティカルなら HP を 100 回復、ファンブルなら自爆する
    /// - 打った「/r」自体は、ホストの画面では読み上げない。ほかの人の画面では一瞬出るが、次のセリフで上書きされる
    /// </summary>
    internal static class DiceRoll
    {
        private const string Section = "5. ダイス(/r)";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<string> Command;
        private static ConfigEntry<bool> LevelOnly;
        private static ConfigEntry<int> CriticalMax;
        private static ConfigEntry<int> FumbleMin;
        private static ConfigEntry<int> CriticalHeal;
        private static ConfigEntry<bool> FumbleExplode;

        /// <summary>振っている最中の人(続けて打っても重ならないように)。</summary>
        private static readonly HashSet<PlayerAvatar> Rolling = new HashSet<PlayerAvatar>();

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "チャットでコマンドを打つと 1D100 を振る(結果はその人がしゃべる)");
            Command = cfg.Bind(Section, "コマンド", "/r", "1D100 を振るチャットのコマンド(大文字・小文字は区別しない)");
            LevelOnly = cfg.Bind(Section, "レベル中だけ", false, "ON にすると、レベル(探索中)だけで振れる。OFF ならショップやトラックでも振れる");
            CriticalMax = cfg.Bind(Section, "クリティカルの上限", 5,
                new ConfigDescription("この数字以下ならクリティカル(0 = なし)", new AcceptableValueRange<int>(0, 100)));
            FumbleMin = cfg.Bind(Section, "ファンブルの下限", 96,
                new ConfigDescription("この数字以上ならファンブル(101 = なし)", new AcceptableValueRange<int>(1, 101)));
            CriticalHeal = cfg.Bind(Section, "クリティカルで回復するHP", 100,
                new ConfigDescription("クリティカルが出た人の HP を回復する量(0 = 回復しない)", new AcceptableValueRange<int>(0, 1000)));
            FumbleExplode = cfg.Bind(Section, "ファンブルで自爆", true, "ファンブルが出た人を自爆させる");
        }

        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ChatMessageSpeak))]
        private static class SpeakPatch
        {
            // チャットのセリフは、全員の PC でこの処理を通って読み上げられる。ホストだけがコマンドを拾う
            private static bool Prefix(PlayerAvatar __instance, string _message)
            {
                if (!Enabled.Value || !Net.IsHost || string.IsNullOrEmpty(_message) ||
                    !string.Equals(_message.Trim(), Command.Value.Trim(), System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (LevelOnly.Value && !SemiFunc.RunIsLevel())
                {
                    return true;
                }
                if (!Rolling.Contains(__instance))
                {
                    Rolling.Add(__instance);
                    Runner.Instance.StartCoroutine(Roll(__instance));
                }
                return false; // 「/r」自体はホストの画面では読み上げない
            }
        }

        /// <summary>ギャンブラーが振る前に言うセリフ(まわりにダイスを振ることを知らせる)。</summary>
        private static readonly string[] GamblerLines =
        {
            "地獄の沙汰も運次第！",
            "一か八か、勝負！",
            "運命のダイス、いくぜ！",
            "女神よ、微笑め！",
            "人生はギャンブルだ！",
            "勝てば天国、負ければ地獄！",
            "ツキはこっちにある！",
            "賽は投げられた！",
            "全部賭ける！",
            "ここが勝負どころ！",
        };

        private static IEnumerator Roll(PlayerAvatar player)
        {
            if (Traits.IsGambler(player))
            {
                return GamblerRoll(player);
            }
            return NormalRoll(player);
        }

        /// <summary>
        /// ギャンブラー(特殊の役職特性)の /r。クリティカルで範囲内の味方全員を回復、ファンブルで範囲内の味方全員が自爆、
        /// 50 以下で金袋(小)を出し、それ以外は(出た目 / 2、切り上げ)のダメージを受ける。
        /// </summary>
        private static IEnumerator GamblerRoll(PlayerAvatar player)
        {
            int generation = Phase.Generation;
            try
            {
                Net.Speak(player, GamblerLines[Random.Range(0, GamblerLines.Length)]);
                yield return new WaitForSeconds(2f);
                if (!Net.Alive(player) || generation != Phase.Generation)
                {
                    yield break;
                }
                Net.Speak(player, "1D100 Start!");
                yield return new WaitForSeconds(1.5f);
                if (!Net.Alive(player) || generation != Phase.Generation)
                {
                    yield break;
                }
                int value = Random.Range(1, 101);
                bool critical = value <= CriticalMax.Value;
                bool fumble = !critical && value >= FumbleMin.Value;
                Plugin.Verbose($"1D100(ギャンブラー): {Net.Name(player)} = {value}");
                float radius = Traits.GamblerRadius.Value;
                if (critical)
                {
                    Net.Speak(player, $"{value}! critical!");
                    foreach (var other in PlayersAround(player, radius))
                    {
                        other.playerHealth.HealOther(Traits.GamblerHeal.Value, true);
                    }
                }
                else if (fumble)
                {
                    Net.Speak(player, $"{value}! fumble!");
                    yield return new WaitForSeconds(1.5f);
                    if (!player || generation != Phase.Generation)
                    {
                        yield break;
                    }
                    foreach (var other in PlayersAround(player, radius))
                    {
                        Net.Kill(other);
                    }
                }
                else if (value <= 50)
                {
                    int dollars = Traits.GamblerBag.Value;
                    Net.Speak(player, $"{value}! ${dollars:N0}ゲット！");
                    if (dollars > 0)
                    {
                        var center = player.transform.position + Vector3.up * 1f;
                        Net.SpawnMoneyBagDollars(Net.FreeSpot(center, 0.6f, 1.2f), dollars, false);
                    }
                }
                else
                {
                    int damage = Mathf.CeilToInt(value / 2f);
                    Net.Speak(player, $"{value}! {damage}ダメージ！");
                    yield return new WaitForSeconds(0.5f);
                    if (Net.Alive(player) && generation == Phase.Generation)
                    {
                        player.playerHealth.HurtOther(damage, Vector3.zero, false, -1);
                    }
                }
            }
            finally
            {
                Rolling.Remove(player);
            }
        }

        /// <summary>振った人のまわりにいる、生きている味方(本人を含む)。</summary>
        private static List<PlayerAvatar> PlayersAround(PlayerAvatar player, float radius)
        {
            var result = new List<PlayerAvatar>();
            if (!player)
            {
                return result;
            }
            var center = player.transform.position;
            foreach (var other in SemiFunc.PlayerGetList())
            {
                if (Net.Alive(other) && other.playerHealth && Vector3.Distance(center, other.transform.position) <= radius)
                {
                    result.Add(other);
                }
            }
            return result;
        }

        private static IEnumerator NormalRoll(PlayerAvatar player)
        {
            try
            {
                Net.Speak(player, "1D100 Start!");
                yield return new WaitForSeconds(1.5f);
                if (!player)
                {
                    yield break;
                }
                int value = Random.Range(1, 101);
                Net.Speak(player, $"{value}!");
                Plugin.Verbose($"1D100: {Net.Name(player)} = {value}");
                string result = value <= CriticalMax.Value ? "critical!" : value >= FumbleMin.Value ? "fumble!" : null;
                if (result == null)
                {
                    yield break;
                }
                yield return new WaitForSeconds(1.5f);
                if (!player || player.deadSet)
                {
                    yield break;
                }
                Net.Speak(player, result);
                bool critical = value <= CriticalMax.Value;
                if (critical && CriticalHeal.Value > 0)
                {
                    player.playerHealth.HealOther(CriticalHeal.Value, true);
                    Plugin.Verbose($"1D100: {Net.Name(player)} クリティカルで {CriticalHeal.Value} 回復");
                }
                else if (!critical && FumbleExplode.Value)
                {
                    // 「fumble!」を言い終わってから自爆
                    yield return new WaitForSeconds(1f);
                    if (player && !player.deadSet)
                    {
                        Net.Kill(player);
                        Plugin.Verbose($"1D100: {Net.Name(player)} ファンブルで自爆");
                    }
                }
            }
            finally
            {
                Rolling.Remove(player);
            }
        }
    }
}
