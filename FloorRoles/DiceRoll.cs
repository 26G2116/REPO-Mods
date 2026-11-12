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

        private static IEnumerator Roll(PlayerAvatar player)
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
                if (player)
                {
                    Net.Speak(player, result);
                }
            }
            finally
            {
                Rolling.Remove(player);
            }
        }
    }
}
