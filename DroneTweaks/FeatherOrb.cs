using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// フェザーオーブ(削除されたアイテム)の効果を「範囲内の貴重品の価値を回復する」に変える。
    ///
    /// - 1 秒ごとに、範囲内の貴重品の価値を元の額の一定割合ずつ、元の額まで戻す
    /// - 価値の回復はゲーム本来の PhysGrabObjectImpactDetector.Heal を使う
    ///   (ホストが計算して HealRPC で全員に送るので、MOD なしの参加者の画面の値段もそろう)
    /// - ゲーム本来の回復は 1 ドル未満を切り捨てるので、安い貴重品でも戻るよう端数をためてから回復する
    /// - 元の効果(物とプレイヤーを軽くする)は、設定で残さない限り止める
    ///   (ただしプレイヤー自身が軽くなる効果は各自の PC で計算されるので、MOD なしの人には元の効果が出る)
    /// </summary>
    internal static class FeatherOrb
    {
        private const string Section = "4. フェザーオーブ";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<float> RepairPercent;
        private static ConfigEntry<float> BatterySeconds;
        private static ConfigEntry<bool> KeepOriginal;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "範囲内の貴重品の価値を回復する効果に変える。電池の持ちも下の設定に変える");
            RepairPercent = cfg.Bind(Section, "1 秒ごとに回復する価値(元の額の%)", 1f,
                new ConfigDescription("範囲内の貴重品の価値を、1 秒ごとに元の額のこの割合ずつ戻す(元の額まで)", new AcceptableValueRange<float>(0.1f, 100f)));
            BatterySeconds = cfg.Bind(Section, "電池満タンで使える時間(秒)", 200f,
                new ConfigDescription("起動している間、電池がこの秒数で 100% から 0% になる", new AcceptableValueRange<float>(5f, 1200f)));
            KeepOriginal = cfg.Bind(Section, "元の効果(軽くする)も残す", false, "ON にすると、価値の回復に加えて元の効果(範囲内の物とプレイヤーを軽くする)も出す");
        }

        private class OrbMemo
        {
            internal float Timer;
        }

        private class ValuableMemo
        {
            internal float Stored; // まだ回復していない端数(ドル)
        }

        private static readonly ConditionalWeakTable<ItemOrbFeather, OrbMemo> OrbMemos = new ConditionalWeakTable<ItemOrbFeather, OrbMemo>();
        private static readonly ConditionalWeakTable<ValuableObject, ValuableMemo> ValuableMemos = new ConditionalWeakTable<ValuableObject, ValuableMemo>();

        // ---- 元の効果を止める ----

        [HarmonyPatch(typeof(ItemOrbFeather), nameof(ItemOrbFeather.Update))]
        private static class OriginalEffectPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                return !Enabled.Value || KeepOriginal.Value;
            }

            // ---- 電池の持ちと、価値の回復(ホスト) ----

            [HarmonyPostfix]
            private static void Postfix(ItemOrbFeather __instance)
            {
                var orb = __instance.itemOrb;
                if (!orb || !SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return;
                }
                var battery = orb.itemBattery;
                if (battery)
                {
                    // 電池は「減る速さ × 2 × 経過時間」ずつ減る(ItemBattery.DrainContinuous)
                    battery.batteryDrainRate = Enabled.Value ? 100f / (2f * BatterySeconds.Value) : orb.batteryDrainRate;
                }

                var memo = OrbMemos.GetOrCreateValue(__instance);
                if (!Enabled.Value || !orb.itemActive || orb.currentState != ItemOrb.State.Active
                    || (battery && battery.batteryLife <= 0f)
                    || (orb.itemEquippable && orb.itemEquippable.isEquipped))
                {
                    memo.Timer = 0f;
                    return;
                }
                memo.Timer += Time.deltaTime;
                if (memo.Timer < 1f)
                {
                    return;
                }
                memo.Timer -= 1f;
                Repair(orb);
            }
        }

        private static void Repair(ItemOrb orb)
        {
            float percent = RepairPercent.Value / 100f;
            var objects = SemiFunc.PhysGrabObjectGetAllWithinRange(orb.orbRadius, orb.transform.position, false, default(LayerMask), null);
            // 当たり判定が複数ある物は、その数だけ一覧に入るので 1 回にまとめる
            var done = new HashSet<PhysGrabObject>();
            foreach (var obj in objects)
            {
                if (!obj || !obj.isValuable || !obj.impactDetector || !done.Add(obj))
                {
                    continue;
                }
                var valuable = obj.GetComponent<ValuableObject>();
                if (!valuable || !valuable.dollarValueSet || valuable.dollarValueOriginal <= 0f)
                {
                    continue;
                }
                var memo = ValuableMemos.GetOrCreateValue(valuable);
                float missing = valuable.dollarValueOriginal - valuable.dollarValueCurrent;
                if (missing < 1f)
                {
                    memo.Stored = 0f;
                    continue;
                }
                memo.Stored = Mathf.Min(memo.Stored + valuable.dollarValueOriginal * percent, missing);
                int dollars = Mathf.FloorToInt(memo.Stored);
                if (dollars < 1)
                {
                    continue;
                }
                // ゲーム本来の回復は「元の額 × 割合」を 1 ドル未満切り捨てで足すので、少しだけ多めの割合を渡す
                // (足しすぎた分は元の額で止まる)
                float healed = obj.impactDetector.Heal((dollars + 0.01f) / valuable.dollarValueOriginal, obj.centerPoint);
                memo.Stored -= Mathf.Floor(healed);
                if (memo.Stored < 0f)
                {
                    memo.Stored = 0f;
                }
                Plugin.Verbose($"フェザーオーブ: {valuable.name} の価値を {Mathf.Floor(healed):0} 回復しました(${valuable.dollarValueCurrent:0} / ${valuable.dollarValueOriginal:0})");
            }
        }
    }
}
