using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LoadProgress
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.LoadProgress";
        public const string PluginName = "LoadProgress";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShareProgress;
        internal static ConfigEntry<float> PositionX;
        internal static ConfigEntry<float> PositionY;
        internal static ConfigEntry<float> Scale;
        internal static ConfigEntry<float> LingerSeconds;
        internal static ConfigEntry<bool> LogTransitions;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "MOD の機能を有効にするか(変更はゲーム再起動後に反映)");
            ShareProgress = Config.Bind("Network", "ShareProgress", true,
                "自分の進捗(ホストなら全員の進捗)を、MOD を入れている他のプレイヤーに送るか");
            PositionX = Config.Bind("Display", "PositionX", 20f, "表示位置 X(画面左上からのピクセル)");
            PositionY = Config.Bind("Display", "PositionY", 20f, "表示位置 Y(画面左上からのピクセル)");
            Scale = Config.Bind("Display", "Scale", 1f, "表示の大きさ(1080p 基準の倍率)");
            LingerSeconds = Config.Bind("Display", "LingerSeconds", 2f, "ロード完了後に表示を残す秒数");
            LogTransitions = Config.Bind("Debug", "LogTransitions", true,
                "各プレイヤーのステップ進行をログ(LogOutput.log)に出す");

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} は設定で無効になっています");
                return;
            }

            // BepInEx の管理オブジェクトはシーン遷移で壊れることがあるので、専用オブジェクトを作る
            var go = new GameObject("LoadProgress");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<ProgressTracker>();

            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }
    }
}
