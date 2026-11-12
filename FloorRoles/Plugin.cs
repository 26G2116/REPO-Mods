using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FloorRoles
{
    /// <summary>
    /// 階層毎選択型役職 MOD。
    /// - トラック(ショップのあと)で、ロールオーブを入れたインベントリのスロット(1 運搬 / 2 戦闘 / 3 特殊)で役職を選ぶ
    ///   役職ごとにステータス報酬(アップグレード)を 2 つ、次のレベルの開始時にもらえる。次のマップはトラックの時点で決まっている
    /// - ショップでは、予算を金袋に分けて出し、最初に触った人の所持金になる。商品は最後に触った人の所持金で買う
    ///   死ぬと所持金を落とし、タンブルすると 1K ずつ落とす(譲渡)。同じ商品を 2 人でつかみ続けると決闘
    /// - 店のアップグレードの品ぞろえを決まった数にする
    /// - チャットで「/r」と打つと 1D100 を振る(1〜5 クリティカル、96〜100 ファンブル)
    /// 全部ホストが計算し、ゲーム本来の通信だけを使うので、ホストだけ導入すれば MOD なしの参加者にも効く。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.FloorRoles";
        public const string PluginName = "FloorRoles";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> VerboseLog;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            VerboseLog = Config.Bind("0. 全体", "詳しいログ", false, "役職・所持金・決闘の処理をしたときにログを出す(動作確認用)");

            Roles.Bind(Config);
            ShopBudget.Bind(Config);
            Duel.Bind(Config);
            ShopUpgrades.Bind(Config);
            DiceRoll.Bind(Config);

            _harmony = new Harmony(PluginGuid);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony パッチの適用に失敗しました: {e}");
            }

            Runner.Ensure();
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
