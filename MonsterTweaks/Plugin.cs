using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// いくつかの敵の挙動を変える MOD。
    /// 敵の AI はホスト(マスタークライアント)が動かしているので、基本はホストだけ導入すれば効く。
    /// 敵ごとに設定のセクションと「有効」スイッチがあり、ゲーム中に切り替えても反映される。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.MonsterTweaks";
        public const string PluginName = "MonsterTweaks";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> VerboseLog;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            VerboseLog = Config.Bind("0. 全体", "詳しいログ", false, "挙動を変えたときにログを出す(動作確認用)");

            Elsa.Bind(Config);
            ApexPredator.Bind(Config);
            Trudge.Bind(Config);
            ShadowChild.Bind(Config);
            Robe.Bind(Config);
            BirthdayBoy.Bind(Config);
            Loom.Bind(Config);
            CleanupCrew.Bind(Config);
            Gambit.Bind(Config);
            HeadGrab.Bind(Config);
            HeartHugger.Bind(Config);
            Animal.Bind(Config);
            EnemyCommon.Bind(Config);
            Trudge.RegisterRoamSpeed();

            Net.Init();
            Boom.Init();
            CleanupCrew.Init();
            _harmony = new Harmony(PluginGuid);
            PatchEach();
            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }

        /// <summary>
        /// パッチをクラスごとに当てる。ゲームの更新で 1 つが壊れても、他の敵の変更は動くようにする。
        /// </summary>
        private void PatchEach()
        {
            var types = typeof(Plugin).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);
            foreach (var type in types)
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Log.LogError($"{type.FullName} のパッチに失敗しました(この敵の変更は動きません): {e}");
                }
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static void Verbose(string message)
        {
            if (VerboseLog != null && VerboseLog.Value)
            {
                Log.LogInfo(message);
            }
        }
    }
}
