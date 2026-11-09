using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using System.Reflection.Emit;

namespace MonsterTweaks
{
    internal static class Transpile
    {
        /// <summary>
        /// メソッドの中の float 定数 value を、getter の呼び出しに置き換える。
        /// occurrence が 0 なら全部、1 以上ならその番目(1 始まり)だけ。見つからなければ警告して元のまま。
        /// </summary>
        internal static IEnumerable<CodeInstruction> ReplaceFloat(IEnumerable<CodeInstruction> instructions, float value, MethodInfo getter, string where, int occurrence = 0)
        {
            int count = 0;
            bool replaced = false;
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Ldc_R4 && code.operand is float f && f == value)
                {
                    count++;
                    if (occurrence == 0 || count == occurrence)
                    {
                        // 命令をその場で書き換える(ジャンプ先のラベルなどはそのまま残る)
                        code.opcode = OpCodes.Call;
                        code.operand = getter;
                        replaced = true;
                    }
                }
                yield return code;
            }
            if (!replaced)
            {
                Plugin.Log.LogWarning($"{where}: 定数 {value} が見つかりませんでした(ゲームの更新で変わった可能性。この調整は効きません)");
            }
        }
    }
}
