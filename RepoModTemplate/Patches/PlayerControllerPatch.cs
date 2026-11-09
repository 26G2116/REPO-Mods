using HarmonyLib;

namespace RepoModTemplate.Patches
{
    // サンプル: PlayerController.Start() の実行後にログを出す。
    // 対象メソッドは dnSpy で Assembly-CSharp.dll を開いて確認する。
    [HarmonyPatch(typeof(PlayerController), "Start")]
    internal static class PlayerControllerPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerController __instance)
        {
            if (Plugin.VerboseLog.Value)
            {
                Plugin.Log.LogInfo($"PlayerController.Start が呼ばれました: {__instance.name}");
            }
        }
    }
}
