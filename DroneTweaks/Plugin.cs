using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DroneTweaks
{
    /// <summary>
    /// ドローン・オーブを変える MOD。
    /// - 削除されたドローン・オーブ(データはゲームに残っている)を店に並べる。アイテムごとに ON / OFF
    /// - ヒーラードローンで倒れた人を復活させる・回復の電池効率を変える
    /// - 不滅オーブの範囲内のプレイヤーを無敵にする・電池の持ちを変える
    /// - フェザーオーブの効果を「範囲内の貴重品の価値を回復する」に変える
    /// 店の品ぞろえ・ドローンの狙い・電池・復活はホストが計算しているので、基本はホストだけ導入すれば全員に効く。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.DroneTweaks";
        public const string PluginName = "DroneTweaks";
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> VerboseLog;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            VerboseLog = Config.Bind("0. 全体", "詳しいログ", false, "アイテムの追加や復活をしたときにログを出す(動作確認用)");

            ShopItems.Bind(Config);
            HealDrone.Bind(Config);
            IndestructibleOrb.Bind(Config);
            FeatherOrb.Bind(Config);

            _harmony = new Harmony(PluginGuid);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony パッチの適用に失敗しました: {e}");
            }

            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }

        internal static void Verbose(string message)
        {
            if (VerboseLog.Value)
            {
                Log.LogInfo(message);
            }
        }

        // OnDestroy でパッチを外さない: BepInEx の管理オブジェクトはシーン遷移で壊れることがあり、
        // そのときに外すと MOD の変更が黙って全部効かなくなる
    }
}
