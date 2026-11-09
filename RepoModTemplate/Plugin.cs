using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RepoModTemplate
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        // GUID は他の MOD と被らない一意な名前にする(作者名.MOD名 が慣例)
        public const string PluginGuid = "shunm.RepoModTemplate";
        public const string PluginName = "RepoModTemplate";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLog;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // 設定は BepInEx\config\shunm.RepoModTemplate.cfg に保存される(Gale の Config 画面から編集可)
            Enabled = Config.Bind("General", "Enabled", true, "MOD の機能を有効にするか(変更はゲーム再起動後に反映)");
            VerboseLog = Config.Bind("General", "VerboseLog", true, "詳細ログを出すかどうか");

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} は設定で無効になっています");
                return;
            }

            _harmony = new Harmony(PluginGuid);
            try
            {
                // このアセンブリ内の [HarmonyPatch] クラスをすべて適用する
                _harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony パッチの適用に失敗しました: {e}");
            }

            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
