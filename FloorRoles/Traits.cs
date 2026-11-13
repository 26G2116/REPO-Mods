using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    /// <summary>
    /// 役職特性(レベル中だけ。役職はトラックで選んだもの)。どれもホストの MOD だけで動く。
    ///
    /// 運搬「引っ越し用運搬ロボ」
    /// - つかんでいる貴重品が衝撃で価値を失うとき、1/2 で無効にする
    /// - しゃがんで貴重品を 4 秒つかむ(1 秒後から「３」「２」「１」)と 1D100(貴重品ごとに 1 回。金袋・知識のキューブは対象外)
    ///   クリティカル: 今の価値 +20% の金袋(小)に変換(数秒無敵) / 50 以下: 価値に応じた大きさの金袋に変換 /
    ///   ファンブル: 美術館の卵で爆発 / それ以外: 何も起きない
    ///
    /// 戦闘「避雷針」
    /// - 武器(銃・近接)をつかんで上を向くとチャージ(「チャージ開始！」→「(武器名)が輝いている！」→「チャージ完了！」)。
    ///   チャージした武器の次の攻撃は、当たった 0.5 秒後にもう一度同じダメージが入る
    /// - しゃがんで電池切れの武器を 4 秒つかむと 1D100
    ///   クリティカル: 電池全回復 / 50 以下: 電池 +12.5% / それ以外: 電池 -12.5% / ファンブル: 武器が壊れる(購入したアイテムからも消える)
    ///
    /// 特殊「ギャンブラー」
    /// - 「/r」の効果が変わる(DiceRoll 側)
    /// - レベル中 1 回だけ、しゃがんで金袋(大)を 4 秒つかむと 1D100
    ///   クリティカル: 元の価値の 3 倍(上限 $250,000)の覚醒した知識のキューブに変換 / 50 以下: 価値の 0.5 倍の金袋を出す /
    ///   それ以外: 美術館の卵で爆発(金袋はそのまま) / ファンブル: 本人が自爆し、起動済みのグレネードを 0.1 秒ごとに 10 個出す
    /// </summary>
    internal static class Traits
    {
        private const string Section = "6. 役職特性";

        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<int> CriticalMax;
        private static ConfigEntry<int> FumbleMin;
        private static ConfigEntry<int> SuccessMax;
        private static ConfigEntry<int> CarrierGuardChance;
        private static ConfigEntry<int> CarrierCriticalBonus;
        private static ConfigEntry<float> ProtectSeconds;
        private static ConfigEntry<int> ChargeAngle;
        private static ConfigEntry<float> SecondHitDelay;
        private static ConfigEntry<float> BatteryStep;
        internal static ConfigEntry<float> GamblerRadius;
        internal static ConfigEntry<int> GamblerHeal;
        internal static ConfigEntry<int> GamblerBag;
        private static ConfigEntry<int> CubeMultiplier;
        private static ConfigEntry<int> CubeCap;
        private static ConfigEntry<int> GrenadeCount;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "レベル中、トラックで選んだ役職の特性を使えるようにする");
            CriticalMax = cfg.Bind(Section, "ダイスのクリティカルの上限", 5,
                new ConfigDescription("しゃがんでつかむダイスで、この数字以下ならクリティカル", new AcceptableValueRange<int>(0, 100)));
            FumbleMin = cfg.Bind(Section, "ダイスのファンブルの下限", 96,
                new ConfigDescription("しゃがんでつかむダイスで、この数字以上ならファンブル", new AcceptableValueRange<int>(1, 101)));
            SuccessMax = cfg.Bind(Section, "ダイスの成功の上限", 50,
                new ConfigDescription("しゃがんでつかむダイスで、この数字以下なら成功", new AcceptableValueRange<int>(0, 100)));
            CarrierGuardChance = cfg.Bind(Section, "運搬: 衝撃を無効にする確率(%)", 50,
                new ConfigDescription("つかんでいる貴重品が衝撃で価値を失うとき、無効にする確率", new AcceptableValueRange<int>(0, 100)));
            CarrierCriticalBonus = cfg.Bind(Section, "運搬: クリティカルの上乗せ(%)", 20,
                new ConfigDescription("クリティカルで変換する金袋の金額(今の価値に上乗せする割合)", new AcceptableValueRange<int>(0, 500)));
            ProtectSeconds = cfg.Bind(Section, "運搬: クリティカルの金袋の無敵時間(秒)", 5f,
                new ConfigDescription("クリティカルで出た金袋が価値を失わない時間", new AcceptableValueRange<float>(0f, 60f)));
            ChargeAngle = cfg.Bind(Section, "戦闘: チャージの見上げる角度", 60,
                new ConfigDescription("武器をつかんだまま、水平からこの角度以上、上を向くとチャージを始める", new AcceptableValueRange<int>(10, 89)));
            SecondHitDelay = cfg.Bind(Section, "戦闘: 2回目のダメージまでの秒数", 0.5f,
                new ConfigDescription("チャージした攻撃が当たってから、もう一度ダメージが入るまでの時間", new AcceptableValueRange<float>(0f, 5f)));
            BatteryStep = cfg.Bind(Section, "戦闘: 電池の増減(%)", 12.5f,
                new ConfigDescription("電池切れの武器のダイスで増える・減る電池の量", new AcceptableValueRange<float>(0f, 100f)));
            GamblerRadius = cfg.Bind(Section, "特殊: /r の範囲", 20f,
                new ConfigDescription("ギャンブラーの /r のクリティカル(回復)・ファンブル(自爆)が届く範囲", new AcceptableValueRange<float>(0f, 200f)));
            GamblerHeal = cfg.Bind(Section, "特殊: /r のクリティカルの回復量", 100,
                new ConfigDescription("範囲内の味方全員を回復する HP", new AcceptableValueRange<int>(0, 1000)));
            GamblerBag = cfg.Bind(Section, "特殊: /r の金袋の金額", 1000,
                new ConfigDescription("/r で 50 以下が出たときに出す金袋(小)の金額(ドル)", new AcceptableValueRange<int>(0, 100000)));
            CubeMultiplier = cfg.Bind(Section, "特殊: 覚醒キューブの倍率", 3,
                new ConfigDescription("金袋(大)のクリティカルで、元の価値の何倍の知識のキューブにするか", new AcceptableValueRange<int>(1, 100)));
            CubeCap = cfg.Bind(Section, "特殊: 覚醒キューブの上限(ドル)", 250000,
                new ConfigDescription("覚醒キューブの金額の上限", new AcceptableValueRange<int>(1000, 10000000)));
            GrenadeCount = cfg.Bind(Section, "特殊: ファンブルのグレネードの数", 10,
                new ConfigDescription("金袋(大)のファンブルで出す起動済みグレネードの数", new AcceptableValueRange<int>(0, 30)));
        }

        internal static bool Active => Enabled.Value && Roles.Enabled.Value && SemiFunc.RunIsLevel();

        internal static Role RoleOf(PlayerAvatar player)
        {
            if (!player || string.IsNullOrEmpty(player.steamID))
            {
                return Role.None;
            }
            return Roles.CurrentRoles.TryGetValue(player.steamID, out var role) ? role : Role.None;
        }

        /// <summary>/r をギャンブラーの効果にするか。</summary>
        internal static bool IsGambler(PlayerAvatar player)
        {
            return Active && RoleOf(player) == Role.Special;
        }

        // ================================================================
        // 状態(レベルごと)
        // ================================================================

        private enum Kind
        {
            None,
            CarrierValuable,
            FighterBattery,
            GamblerBag,
        }

        private class Ritual
        {
            internal PhysGrabObject Target;
            internal Kind Kind;
            internal float Elapsed;
            internal int Spoken;
            internal bool Done;
        }

        private static readonly Dictionary<PlayerAvatar, Ritual> Rituals = new Dictionary<PlayerAvatar, Ritual>();
        private static readonly Dictionary<PlayerAvatar, PhysGrabObject> Grabbed = new Dictionary<PlayerAvatar, PhysGrabObject>();
        private static float grabScanTimer;

        /// <summary>運搬のダイスを振った貴重品。</summary>
        private static readonly HashSet<PhysGrabObject> UsedValuables = new HashSet<PhysGrabObject>();
        /// <summary>金袋(大)を賭けた人。</summary>
        private static readonly HashSet<string> UsedBets = new HashSet<string>();
        /// <summary>価値を失わない物と、その期限(Time.time)。</summary>
        private static readonly Dictionary<PhysGrabObject, float> Protected = new Dictionary<PhysGrabObject, float>();

        /// <summary>チャージ済みの武器。</summary>
        private static readonly HashSet<PhysGrabObject> Charged = new HashSet<PhysGrabObject>();
        private static readonly HashSet<PlayerAvatar> Charging = new HashSet<PlayerAvatar>();
        /// <summary>チャージを使った近接武器と、2 回ダメージになる期限(同じ振りで複数の敵に当たったとき用)。</summary>
        private static readonly Dictionary<PhysGrabObject, float> MeleeWindows = new Dictionary<PhysGrabObject, float>();
        /// <summary>チャージした射撃の弾の当たり判定。</summary>
        private static readonly Dictionary<HurtCollider, float> ChargedBullets = new Dictionary<HurtCollider, float>();
        private static ItemGun chargedShotGun;

        internal static void OnLeavingLevel()
        {
            Rituals.Clear();
            Grabbed.Clear();
            UsedValuables.Clear();
            UsedBets.Clear();
            Protected.Clear();
            Charged.Clear();
            Charging.Clear();
            MeleeWindows.Clear();
            ChargedBullets.Clear();
            chargedShotGun = null;
        }

        // ================================================================
        // 毎フレーム(ホスト)
        // ================================================================

        internal static void Tick()
        {
            if (!Active || Roles.CurrentRoles.Count == 0)
            {
                return;
            }
            grabScanTimer -= Time.deltaTime;
            if (grabScanTimer <= 0f)
            {
                grabScanTimer = 0.2f;
                ScanGrabs();
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                var role = RoleOf(player);
                if (role == Role.None || !Net.Alive(player))
                {
                    Rituals.Remove(player);
                    continue;
                }
                Grabbed.TryGetValue(player, out var grabbed);
                if (grabbed && grabbed.dead)
                {
                    grabbed = null;
                }
                if (role == Role.Fighter)
                {
                    TickCharge(player, grabbed);
                }
                TickRitual(player, role, grabbed);
            }
        }

        /// <summary>だれが何をつかんでいるか(つかむ処理はホストが持っているので、全員分が分かる)。</summary>
        private static void ScanGrabs()
        {
            Grabbed.Clear();
            foreach (var obj in Object.FindObjectsByType<PhysGrabObject>(FindObjectsSortMode.None))
            {
                if (!obj || obj.dead || obj.playerGrabbing == null || obj.playerGrabbing.Count == 0)
                {
                    continue;
                }
                foreach (var grabber in obj.playerGrabbing)
                {
                    if (grabber && grabber.playerAvatar)
                    {
                        Grabbed[grabber.playerAvatar] = obj;
                    }
                }
            }
        }

        // ---- しゃがんで 4 秒つかむ ----

        private static void TickRitual(PlayerAvatar player, Role role, PhysGrabObject grabbed)
        {
            var kind = Net.IsCrouching(player) && grabbed ? RitualKind(player, role, grabbed) : Kind.None;
            if (!Rituals.TryGetValue(player, out var ritual) || ritual.Target != grabbed || (!ritual.Done && ritual.Kind != kind))
            {
                if (kind == Kind.None)
                {
                    Rituals.Remove(player);
                    return;
                }
                ritual = new Ritual { Target = grabbed, Kind = kind };
                Rituals[player] = ritual;
            }
            if (ritual.Done)
            {
                // 振ったあとは、離すまで何もしない
                if (!Net.IsCrouching(player))
                {
                    Rituals.Remove(player);
                }
                return;
            }
            if (kind == Kind.None)
            {
                Rituals.Remove(player);
                return;
            }
            ritual.Elapsed += Time.deltaTime;
            // 1 秒後から「３」「２」「１」、4 秒でダイス
            while (ritual.Spoken < 3 && ritual.Elapsed >= ritual.Spoken + 1f)
            {
                ritual.Spoken++;
                Net.Speak(player, new[] { "３", "２", "１" }[ritual.Spoken - 1]);
            }
            if (ritual.Elapsed >= 4f)
            {
                ritual.Done = true;
                Resolve(player, ritual);
            }
        }

        private static Kind RitualKind(PlayerAvatar player, Role role, PhysGrabObject grabbed)
        {
            switch (role)
            {
                case Role.Carrier:
                {
                    var valuable = grabbed.GetComponent<ValuableObject>();
                    if (valuable && valuable.dollarValueSet && !Net.IsMoneyBag(valuable) &&
                        !grabbed.GetComponent<ValuableCubeOfKnowledge>() && !UsedValuables.Contains(grabbed))
                    {
                        return Kind.CarrierValuable;
                    }
                    return Kind.None;
                }
                case Role.Fighter:
                {
                    var battery = grabbed.GetComponent<ItemBattery>();
                    return IsWeapon(grabbed) && battery && battery.batteryLife <= 0f ? Kind.FighterBattery : Kind.None;
                }
                case Role.Special:
                {
                    var valuable = grabbed.GetComponent<ValuableObject>();
                    return valuable && valuable.dollarValueSet && Net.IsBigMoneyBag(valuable) && !UsedBets.Contains(player.steamID)
                        ? Kind.GamblerBag
                        : Kind.None;
                }
                default:
                    return Kind.None;
            }
        }

        private static void Resolve(PlayerAvatar player, Ritual ritual)
        {
            int value = Random.Range(1, 101);
            bool critical = value <= CriticalMax.Value;
            bool fumble = !critical && value >= FumbleMin.Value;
            bool success = !critical && !fumble && value <= SuccessMax.Value;
            Plugin.Verbose($"役職特性のダイス: {Net.Name(player)} {ritual.Kind} = {value}");
            switch (ritual.Kind)
            {
                case Kind.CarrierValuable:
                    UsedValuables.Add(ritual.Target);
                    ResolveCarrier(player, ritual.Target, value, critical, fumble, success);
                    break;
                case Kind.FighterBattery:
                    ResolveBattery(player, ritual.Target, value, critical, fumble, success);
                    break;
                case Kind.GamblerBag:
                    UsedBets.Add(player.steamID);
                    ResolveBet(player, ritual.Target, value, critical, fumble, success);
                    break;
            }
        }

        // ---- 運搬: 貴重品 ----

        private static void ResolveCarrier(PlayerAvatar player, PhysGrabObject target, int value, bool critical, bool fumble, bool success)
        {
            var valuable = target.GetComponent<ValuableObject>();
            var center = target.centerPoint;
            int current = valuable ? Mathf.RoundToInt(valuable.dollarValueCurrent) : 0;
            if (critical)
            {
                int dollars = Mathf.Max(1, Mathf.RoundToInt(current * (1f + CarrierCriticalBonus.Value / 100f)));
                Net.Speak(player, $"ダイス{value} クリティカル！ ${dollars:N0}の金袋に変換！");
                DestroyQuietly(target);
                var bag = Net.SpawnMoneyBagDollars(center, dollars, false);
                if (bag)
                {
                    var grab = bag.GetComponent<PhysGrabObject>();
                    if (grab)
                    {
                        Protected[grab] = Time.time + ProtectSeconds.Value;
                    }
                }
            }
            else if (fumble)
            {
                Net.Speak(player, $"ダイス{value} ファンブル！");
                Runner.Instance.StartCoroutine(ExplodeLater(target, 1f, Phase.Generation));
            }
            else if (success)
            {
                Net.Speak(player, $"ダイス{value}！ ${current:N0}の金袋に変換！");
                DestroyQuietly(target);
                Net.SpawnMoneyBagDollars(center, Mathf.Max(1, current), true);
            }
            else
            {
                Net.Speak(player, $"ダイス{value}。何も起きない");
            }
        }

        // ---- 戦闘: 電池切れの武器 ----

        private static void ResolveBattery(PlayerAvatar player, PhysGrabObject target, int value, bool critical, bool fumble, bool success)
        {
            var battery = target.GetComponent<ItemBattery>();
            var attributes = target.GetComponent<ItemAttributes>();
            string name = WeaponName(target);
            if (!battery)
            {
                return;
            }
            float step = BatteryStep.Value;
            if (critical)
            {
                Net.Speak(player, $"ダイス{value} クリティカル！ 電池が全回復！");
                battery.SetBatteryLife(100);
            }
            else if (fumble)
            {
                Net.Speak(player, $"ダイス{value} ファンブル！ {name}が壊れた…");
                Charged.Remove(target);
                if (attributes && StatsManager.instance && !string.IsNullOrEmpty(attributes.instanceName) &&
                    StatsManager.instance.item.ContainsKey(attributes.instanceName))
                {
                    StatsManager.instance.ItemRemove(attributes.instanceName);
                }
                if (target.impactDetector)
                {
                    target.impactDetector.DestroyObject(true, true);
                }
                else
                {
                    target.DestroyPhysGrabObject();
                }
            }
            else if (success)
            {
                Net.Speak(player, $"ダイス{value}！ 電池が回復！");
                battery.SetBatteryLife(Mathf.RoundToInt(Mathf.Min(100f, battery.batteryLife + step)));
            }
            else
            {
                Net.Speak(player, battery.batteryLife > 0f ? $"ダイス{value}。電池が減った…" : $"ダイス{value}。電池は空のまま…");
                battery.SetBatteryLife(Mathf.RoundToInt(Mathf.Max(0f, battery.batteryLife - step)));
            }
        }

        // ---- 特殊: 金袋(大)を賭ける ----

        private static void ResolveBet(PlayerAvatar player, PhysGrabObject target, int value, bool critical, bool fumble, bool success)
        {
            var valuable = target.GetComponent<ValuableObject>();
            var center = target.centerPoint;
            int current = valuable ? Mathf.RoundToInt(valuable.dollarValueCurrent) : 0;
            int original = valuable ? Mathf.RoundToInt(valuable.dollarValueOriginal) : 0;
            if (critical)
            {
                int dollars = Mathf.Clamp(original * CubeMultiplier.Value, 1, CubeCap.Value);
                Net.Speak(player, $"ダイス{value} クリティカル！ ${dollars:N0}の覚醒キューブに！");
                DestroyQuietly(target);
                Net.SpawnAwakenedCube(center + Vector3.up * 0.3f, dollars);
            }
            else if (fumble)
            {
                Net.Speak(player, $"ダイス{value} ファンブル！");
                Runner.Instance.StartCoroutine(GamblerFumble(player, Phase.Generation));
            }
            else if (success)
            {
                int dollars = Mathf.Max(1, Mathf.RoundToInt(current * 0.5f));
                Net.Speak(player, $"ダイス{value}！ ${dollars:N0}の金袋ゲット！");
                Net.SpawnMoneyBagDollars(Net.FreeSpot(center, 0.5f, 1.2f), dollars, true);
            }
            else
            {
                Net.Speak(player, $"ダイス{value}。ハズレ！");
                Runner.Instance.StartCoroutine(ExplodeLater(target, 1f, Phase.Generation));
            }
        }

        private static IEnumerator GamblerFumble(PlayerAvatar player, int generation)
        {
            yield return new WaitForSeconds(1f);
            if (!player || generation != Phase.Generation)
            {
                yield break;
            }
            var position = player.transform.position + Vector3.up * 1f;
            Net.Kill(player);
            for (int i = 0; i < GrenadeCount.Value; i++)
            {
                if (generation != Phase.Generation)
                {
                    yield break;
                }
                Net.SpawnActiveGrenade(position + Random.insideUnitSphere * 0.3f);
                yield return new WaitForSeconds(0.1f);
            }
        }

        private static IEnumerator ExplodeLater(PhysGrabObject target, float delay, int generation)
        {
            var position = target ? target.centerPoint : Vector3.zero;
            yield return new WaitForSeconds(delay);
            if (generation != Phase.Generation)
            {
                yield break;
            }
            if (target)
            {
                position = target.centerPoint;
            }
            Net.ExplodeEgg(position);
        }

        /// <summary>貴重品を消す(変換用。壊れたときの音や揺れは出さない)。</summary>
        private static void DestroyQuietly(PhysGrabObject target)
        {
            if (!target)
            {
                return;
            }
            if (target.impactDetector)
            {
                target.impactDetector.DestroyObject(false, true);
            }
            else
            {
                target.DestroyPhysGrabObject();
            }
        }

        // ---- 戦闘: チャージ ----

        private static bool IsWeapon(PhysGrabObject obj)
        {
            return obj && (obj.GetComponent<ItemMelee>() || obj.GetComponent<ItemGun>());
        }

        private static string WeaponName(PhysGrabObject obj)
        {
            var attributes = obj ? obj.GetComponent<ItemAttributes>() : null;
            if (attributes && !string.IsNullOrEmpty(attributes.itemName))
            {
                return attributes.itemName;
            }
            return "武器";
        }

        private static void TickCharge(PlayerAvatar player, PhysGrabObject grabbed)
        {
            if (!grabbed || !IsWeapon(grabbed) || Charged.Contains(grabbed) || Charging.Contains(player))
            {
                return;
            }
            if (Net.LookPitchUp(player) < ChargeAngle.Value)
            {
                return;
            }
            Charging.Add(player);
            Runner.Instance.StartCoroutine(Charge(player, grabbed, Phase.Generation));
        }

        private static IEnumerator Charge(PlayerAvatar player, PhysGrabObject weapon, int generation)
        {
            try
            {
                string name = WeaponName(weapon);
                // 武器を持ったままなら、セリフを言い終わるごとに進む(離したら中断)
                string[] lines = { "チャージ開始！", $"{name}が輝いている！" };
                float[] durations = { 1.5f, 2.5f };
                for (int i = 0; i < lines.Length; i++)
                {
                    Net.Speak(player, lines[i]);
                    float timer = 0f;
                    while (timer < durations[i])
                    {
                        if (!StillHolding(player, weapon, generation))
                        {
                            Interrupted(player);
                            yield break;
                        }
                        timer += Time.deltaTime;
                        yield return null;
                    }
                }
                Charged.Add(weapon);
                Net.Speak(player, "チャージ完了！");
                Plugin.Verbose($"避雷針: {Net.Name(player)} の {name} をチャージしました");
            }
            finally
            {
                Charging.Remove(player);
            }
        }

        private static bool StillHolding(PlayerAvatar player, PhysGrabObject weapon, int generation)
        {
            return generation == Phase.Generation && Active && Net.Alive(player) && weapon && !weapon.dead &&
                   Grabbed.TryGetValue(player, out var grabbed) && grabbed == weapon;
        }

        private static void Interrupted(PlayerAvatar player)
        {
            if (player)
            {
                Net.Speak(player, "チャージ中断…");
            }
        }

        // ---- 戦闘: 2 回ダメージ ----

        private static void ScheduleSecondHit(HurtCollider collider, Enemy enemy)
        {
            int damage = collider.enemyDamage;
            var direction = collider.applyForce.sqrMagnitude > 0f ? collider.applyForce.normalized : collider.transform.forward;
            Runner.Instance.StartCoroutine(SecondHit(enemy, damage, direction, Phase.Generation));
        }

        private static IEnumerator SecondHit(Enemy enemy, int damage, Vector3 direction, int generation)
        {
            yield return new WaitForSeconds(SecondHitDelay.Value);
            if (generation != Phase.Generation || !enemy || !enemy.HasHealth || !enemy.Health)
            {
                yield break;
            }
            enemy.Health.Hurt(damage, direction);
            Plugin.Verbose($"避雷針: 2 回目のダメージ {damage}");
        }

        [HarmonyPatch(typeof(HurtCollider), nameof(HurtCollider.EnemyHurt))]
        private static class EnemyHurtPatch
        {
            private static void Postfix(HurtCollider __instance, Enemy _enemy, bool __result)
            {
                if (!__result || !Net.IsHost || !__instance || __instance.enemyDamage <= 0 || !_enemy || !_enemy.HasHealth)
                {
                    return;
                }
                try
                {
                    if (ChargedBullets.TryGetValue(__instance, out float bulletUntil) && Time.time <= bulletUntil)
                    {
                        ScheduleSecondHit(__instance, _enemy);
                        return;
                    }
                    var melee = __instance.GetComponentInParent<ItemMelee>();
                    var weapon = melee ? melee.GetComponent<PhysGrabObject>() : null;
                    if (!weapon)
                    {
                        return;
                    }
                    if (MeleeWindows.TryGetValue(weapon, out float until) && Time.time <= until)
                    {
                        ScheduleSecondHit(__instance, _enemy);
                        return;
                    }
                    if (Charged.Remove(weapon))
                    {
                        // 同じ振りで、ほかの敵に当たったときも 2 回にする
                        MeleeWindows[weapon] = Time.time + 0.3f;
                        ScheduleSecondHit(__instance, _enemy);
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"避雷針の処理に失敗しました: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(ItemGun), nameof(ItemGun.ShootRPC))]
        private static class ShootPatch
        {
            // 撃つ処理はホストで動き、その中で弾を出す。チャージ済みなら、この射撃の弾を覚える
            private static void Prefix(ItemGun __instance)
            {
                chargedShotGun = null;
                if (!Net.IsHost || !__instance)
                {
                    return;
                }
                var weapon = __instance.GetComponent<PhysGrabObject>();
                if (weapon && Charged.Remove(weapon))
                {
                    chargedShotGun = __instance;
                    foreach (var old in ChargedBullets.Where(p => !p.Key || Time.time > p.Value).Select(p => p.Key).ToList())
                    {
                        ChargedBullets.Remove(old);
                    }
                }
            }

            private static void Postfix()
            {
                chargedShotGun = null;
            }
        }

        [HarmonyPatch(typeof(ItemGun), nameof(ItemGun.ShootBulletRPC))]
        private static class ShootBulletPatch
        {
            private static void Postfix(ItemGun __instance)
            {
                if (chargedShotGun && chargedShotGun == __instance && __instance.hurtCollider)
                {
                    // 弾は 1 発ごとに作られるが、念のためこの射撃のあいだだけにする
                    ChargedBullets[__instance.hurtCollider] = Time.time + 1.5f;
                }
            }
        }

        // ---- 運搬: 衝撃を無効にする・無敵の金袋 ----

        [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), nameof(PhysGrabObjectImpactDetector.Break))]
        private static class BreakPatch
        {
            // 価値が減る処理はホストが決めて全員に送る。ホストで止めれば、だれの画面でも減らない
            private static bool Prefix(PhysGrabObjectImpactDetector __instance, bool _forceBreak)
            {
                if (!Net.IsHost || !__instance || !__instance.isValuable)
                {
                    return true;
                }
                var grab = __instance.physGrabObject;
                if (!grab)
                {
                    return true;
                }
                if (Protected.TryGetValue(grab, out float until))
                {
                    if (Time.time < until)
                    {
                        return false;
                    }
                    Protected.Remove(grab);
                }
                if (_forceBreak || !Active || grab.playerGrabbing == null)
                {
                    return true;
                }
                foreach (var grabber in grab.playerGrabbing)
                {
                    if (grabber && RoleOf(grabber.playerAvatar) == Role.Carrier)
                    {
                        if (Random.Range(0, 100) < CarrierGuardChance.Value)
                        {
                            Plugin.Verbose($"引っ越し用運搬ロボ: {Net.Name(grabber.playerAvatar)} が衝撃を無効にしました");
                            return false;
                        }
                        return true; // 運搬が何人でつかんでいても、判定は 1 回
                    }
                }
                return true;
            }
        }
    }
}
