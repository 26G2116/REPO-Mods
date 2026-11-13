using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    /// <summary>
    /// ゲーム本来の通信を使う小さな道具(どれもホストから呼ぶ)。MOD なしの参加者の画面にもそのまま出る。
    /// </summary>
    internal static class Net
    {
        internal static bool IsHost => SemiFunc.IsMasterClientOrSingleplayer();

        /// <summary>レベルを遊べる状態か(生成が終わり、ゲームが進行中)。</summary>
        internal static bool Ready =>
            LevelGenerator.Instance && LevelGenerator.Instance.Generated &&
            GameDirector.instance && GameDirector.instance.currentState == GameDirector.gameState.Main;

        /// <summary>
        /// プレイヤーにしゃべらせる(頭の上の吹き出しと読み上げ)。ホストからなら誰にでもしゃべらせられる。
        /// 新しいセリフが来ると前のセリフは途中で止まるので、続けるときは間を空ける。
        /// </summary>
        internal static void Speak(PlayerAvatar player, string text)
        {
            if (!player || string.IsNullOrEmpty(text))
            {
                return;
            }
            string locale = IsAscii(text) ? "en-US" : "ja-JP";
            try
            {
                if (SemiFunc.IsMultiplayer())
                {
                    player.photonView.RPC("ChatMessageSendRPC", RpcTarget.All, text, false, locale);
                }
                else
                {
                    player.ChatMessageSpeak(text, false, locale);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"しゃべらせられませんでした: {e.Message}");
            }
        }

        /// <summary>トラックのモニターに、税務官からのメッセージとして書く(打ち込み中の画面では出ない)。</summary>
        /// <summary>
        /// 税務官が今のページの文章を最後まで打ち終わったか(ホストで見る)。
        /// 打っていない瞬間でも、行と行の間の待ち時間なら、まだ続きがある。
        /// </summary>
        internal static bool TruckPageFinished()
        {
            var screen = TruckScreenText.instance;
            if (!screen || !screen.started || screen.isTyping || screen.pages == null)
            {
                return false;
            }
            int page = screen.currentPageIndex;
            if (page < 0 || page >= screen.pages.Count)
            {
                return true;
            }
            var lines = screen.pages[page].textLines;
            return lines == null || screen.currentLineIndex >= lines.Count;
        }

        internal static bool TruckMessage(string text)
        {
            var screen = TruckScreenText.instance;
            if (!screen || screen.isTyping)
            {
                return false;
            }
            screen.MessageSendCustom("", text, 0);
            return true;
        }

        /// <summary>プレイヤーを倒す(本人の PC でダメージが計算される)。</summary>
        internal static void Kill(PlayerAvatar player)
        {
            if (player && player.playerHealth && !player.deadSet)
            {
                player.playerHealth.HurtOther(9999, Vector3.zero, false, -1);
            }
        }

        /// <summary>アップグレードを上げる。全員の PC で同じ処理が動き、本人の PC ではすぐ効く。</summary>
        internal static void Upgrade(string steamID, string upgradeName, int amount)
        {
            var pun = PunManager.instance;
            if (!pun || string.IsNullOrEmpty(steamID) || amount == 0)
            {
                return;
            }
            if (SemiFunc.IsMultiplayer())
            {
                pun.photonView.RPC("TesterUpgradeCommandRPC", RpcTarget.All, steamID, upgradeName, amount);
            }
            else
            {
                pun.TesterUpgradeCommandRPC(steamID, upgradeName, amount, default);
            }
        }

        /// <summary>今のアップグレードの数。</summary>
        internal static int UpgradeLevel(string steamID, string upgradeName)
        {
            var stats = StatsManager.instance;
            if (!stats || string.IsNullOrEmpty(steamID) ||
                !stats.dictionaryOfDictionaries.TryGetValue("playerUpgrade" + upgradeName, out var dict))
            {
                return 0;
            }
            return dict.GetValueOrDefault(steamID, 0);
        }

        /// <summary>Resources のパスで全員の画面に出す。</summary>
        internal static GameObject SpawnRoomObject(string resourcePath, GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (SemiFunc.IsMultiplayer())
            {
                return PhotonNetwork.InstantiateRoomObject(resourcePath, position, rotation, 0, null);
            }
            return prefab ? Object.Instantiate(prefab, position, rotation) : null;
        }

        /// <summary>金袋(小)を、決まった金額(K = $1,000)で出す。</summary>
        internal static ValuableObject SpawnMoneyBag(Vector3 position, int thousands)
        {
            var prefab = AssetManager.instance ? AssetManager.instance.surplusValuableSmall : null;
            if (!prefab || thousands <= 0)
            {
                return null;
            }
            var spawned = SpawnRoomObject("Valuables/" + prefab.name, prefab, position, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var valuable = spawned ? spawned.GetComponent<ValuableObject>() : null;
            if (valuable)
            {
                // 値段の確定(少し待ってから行われる)の前に入れる。精算の余りの金袋と同じやり方
                valuable.dollarValueOverride = thousands * 1000;
            }
            return valuable;
        }

        /// <summary>
        /// 金袋を、決まった金額(ドル)で出す。auto = true なら金額に応じた大きさ
        /// (抽出口の余りの金袋と同じ: 10,000 超で大、5,000 超で中、それ以下は小)、false なら小。
        /// </summary>
        internal static ValuableObject SpawnMoneyBagDollars(Vector3 position, int dollars, bool auto)
        {
            var assets = AssetManager.instance;
            if (!assets || dollars <= 0)
            {
                return null;
            }
            var prefab = assets.surplusValuableSmall;
            if (auto && dollars > 10000)
            {
                prefab = assets.surplusValuableBig;
            }
            else if (auto && dollars > 5000)
            {
                prefab = assets.surplusValuableMedium;
            }
            if (!prefab)
            {
                return null;
            }
            var spawned = SpawnRoomObject("Valuables/" + prefab.name, prefab, position, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var valuable = spawned ? spawned.GetComponent<ValuableObject>() : null;
            if (valuable)
            {
                valuable.dollarValueOverride = dollars;
            }
            return valuable;
        }

        /// <summary>金袋(抽出口の余りの金袋と同じもの)か。</summary>
        internal static bool IsMoneyBag(ValuableObject valuable)
        {
            return valuable && valuable.GetComponent<SurplusValuable>();
        }

        /// <summary>金袋(大)か。</summary>
        internal static bool IsBigMoneyBag(ValuableObject valuable)
        {
            var big = AssetManager.instance ? AssetManager.instance.surplusValuableBig : null;
            return IsMoneyBag(valuable) && big && valuable.name.StartsWith(big.name, StringComparison.Ordinal);
        }

        // ---- 美術館の卵(爆発に使う) ----

        private static string eggPath;
        private static GameObject eggPrefab;

        private static bool FindEgg()
        {
            if (eggPrefab)
            {
                return true;
            }
            var run = RunManager.instance;
            if (!run || run.levels == null)
            {
                return false;
            }
            foreach (var level in run.levels)
            {
                if (!level || level.ValuablePresets == null)
                {
                    continue;
                }
                foreach (var preset in level.ValuablePresets)
                {
                    if (!preset)
                    {
                        continue;
                    }
                    var lists = new[] { preset.tiny, preset.small, preset.medium, preset.big, preset.wide, preset.tall, preset.veryTall };
                    foreach (var list in lists)
                    {
                        if (list == null)
                        {
                            continue;
                        }
                        foreach (var reference in list)
                        {
                            if (reference == null || string.IsNullOrEmpty(reference.PrefabName) ||
                                reference.PrefabName.IndexOf("Egg", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }
                            var prefab = reference.Prefab;
                            if (prefab && prefab.GetComponent<ValuableEgg>())
                            {
                                eggPrefab = prefab;
                                eggPath = reference.ResourcePath;
                                return true;
                            }
                        }
                    }
                }
            }
            Plugin.Log.LogWarning("美術館の卵が見つかりませんでした(爆発できません)");
            return false;
        }

        /// <summary>美術館の卵を 1 個出して、すぐ爆発させる(ゲーム本来の卵の爆発。MOD なしの人の画面でも爆発する)。</summary>
        internal static void ExplodeEgg(Vector3 position)
        {
            if (!FindEgg())
            {
                return;
            }
            var spawned = SpawnRoomObject(eggPath, eggPrefab, position, Quaternion.identity);
            var egg = spawned ? spawned.GetComponent<ValuableEgg>() : null;
            if (!egg)
            {
                return;
            }
            var valuable = egg.GetComponent<ValuableObject>();
            if (valuable)
            {
                valuable.dollarValueOverride = 1; // すぐ消えるので、レベルの貴重品の合計をほとんど増やさない
            }
            Runner.Instance.StartCoroutine(ExplodeEggLater(egg));
        }

        private static System.Collections.IEnumerator ExplodeEggLater(ValuableEgg egg)
        {
            // 卵の準備(Start)が終わってから
            float waited = 0f;
            while (egg && egg.particleScriptExplosion == null && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            yield return new WaitForSeconds(0.1f);
            if (egg)
            {
                egg.SetState(ValuableEgg.EggState.Explode);
            }
        }

        // ---- 覚醒した知識のキューブ ----

        private const string CubePath = "Valuables/04 Big/Valuable Wizard Cube of Knowledge";

        /// <summary>知識のキューブを出して、決まった金額で覚醒させる(ゲーム本来の覚醒の演出)。</summary>
        internal static bool SpawnAwakenedCube(Vector3 position, int dollars)
        {
            var prefab = Resources.Load<GameObject>(CubePath);
            if (!prefab)
            {
                Plugin.Log.LogWarning("知識のキューブが見つかりませんでした");
                return false;
            }
            var spawned = SpawnRoomObject(CubePath, prefab, position, Quaternion.identity);
            var cube = spawned ? spawned.GetComponent<ValuableCubeOfKnowledge>() : null;
            if (!cube)
            {
                return false;
            }
            var valuable = cube.GetComponent<ValuableObject>();
            if (valuable)
            {
                valuable.dollarValueOverride = dollars;
            }
            Runner.Instance.StartCoroutine(AwakenCubeLater(cube, dollars));
            return true;
        }

        private static System.Collections.IEnumerator AwakenCubeLater(ValuableCubeOfKnowledge cube, int dollars)
        {
            yield return new WaitForSeconds(1f);
            var valuable = cube ? cube.GetComponent<ValuableObject>() : null;
            float waited = 0f;
            while (cube && valuable && !valuable.dollarValueSet && waited < 5f)
            {
                waited += 0.1f;
                yield return new WaitForSeconds(0.1f);
            }
            if (!cube)
            {
                yield break;
            }
            // 台座なしで覚醒させる。浮かぶ位置は台座の位置の平均なので、全部今の位置にしておく(動かすのはホストだけ)
            var here = cube.rb ? cube.rb.position : cube.transform.position;
            for (int i = 0; i < cube.boundPlatformPositions.Length; i++)
            {
                cube.boundPlatformPositions[i] = here;
            }
            var startScale = cube.transform.localScale;
            var endScale = startScale * cube.awakenedSizeMultiplier;
            float duration = Mathf.Max(0.01f, cube.awakenedMoveTime + cube.finalSpinTime);
            if (SemiFunc.IsMultiplayer())
            {
                cube.photonView.RPC("AwakenRPC", RpcTarget.AllBuffered, dollars, startScale, endScale, PhotonNetwork.Time, duration);
            }
            else
            {
                cube.AwakenApply(dollars, startScale, endScale, Time.timeAsDouble, duration);
            }
        }

        // ---- 起動済みのグレネード ----

        private static Item explosiveGrenade;

        private static Item ExplosiveGrenade()
        {
            if (explosiveGrenade)
            {
                return explosiveGrenade;
            }
            var stats = StatsManager.instance;
            if (!stats)
            {
                return null;
            }
            foreach (var item in stats.itemDictionary.Values)
            {
                var prefab = item && item.prefab != null ? item.prefab.Prefab : null;
                if (prefab && prefab.GetComponent<ItemGrenadeExplosive>())
                {
                    explosiveGrenade = item;
                    return item;
                }
            }
            Plugin.Log.LogWarning("グレネードが見つかりませんでした");
            return null;
        }

        /// <summary>起動済みのグレネードを出す(数秒後に爆発する。持ち帰れない)。</summary>
        internal static void SpawnActiveGrenade(Vector3 position)
        {
            var item = ExplosiveGrenade();
            if (!item)
            {
                return;
            }
            var spawned = SpawnRoomObject(item.prefab.ResourcePath, item.prefab.Prefab, position, UnityEngine.Random.rotation);
            var grenade = spawned ? spawned.GetComponent<ItemGrenade>() : null;
            if (!grenade)
            {
                return;
            }
            SpawnedItems.Track(grenade.GetComponent<ItemAttributes>());
            grenade.isSpawnedGrenade = true;
            Runner.Instance.StartCoroutine(ActivateGrenadeLater(grenade));
        }

        private static System.Collections.IEnumerator ActivateGrenadeLater(ItemGrenade grenade)
        {
            float waited = 0f;
            while (grenade && (!grenade.physGrabObject || !grenade.physGrabObject.spawned || grenade.rb == null || grenade.rb.isKinematic) && waited < 3f)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (!grenade)
            {
                yield break;
            }
            grenade.isSpawnedGrenade = true;
            if (grenade.itemAttributes)
            {
                grenade.itemAttributes.blockExtraction = true;
            }
            var toggle = grenade.GetComponent<ItemToggle>();
            if (toggle)
            {
                toggle.ToggleItem(true, -1);
            }
            if (grenade.rb)
            {
                var direction = Quaternion.Euler(UnityEngine.Random.Range(-60f, -20f), UnityEngine.Random.Range(-180f, 180f), 0f) * Vector3.forward;
                grenade.rb.AddForce(direction * UnityEngine.Random.Range(2f, 5f), ForceMode.Impulse);
            }
        }

        // ---- プレイヤーの状態(ホストで見る) ----

        /// <summary>見ている向き(ホスト自身はカメラ、ほかの人は通信で届くカメラの向き)。</summary>
        internal static Vector3 LookDirection(PlayerAvatar player)
        {
            if (!player)
            {
                return Vector3.forward;
            }
            if (player.isLocal && CameraAim.Instance)
            {
                return CameraAim.Instance.transform.forward;
            }
            return player.clientCameraRotation * Vector3.forward;
        }

        /// <summary>水平から上へ何度の向きを見ているか(真上 = 90)。</summary>
        internal static float LookPitchUp(PlayerAvatar player)
        {
            var forward = LookDirection(player);
            return 90f - Vector3.Angle(forward, Vector3.up);
        }

        internal static bool IsCrouching(PlayerAvatar player)
        {
            return player && (player.isCrouching || player.isCrawling);
        }

        internal static bool Alive(PlayerAvatar player)
        {
            return player && !player.deadSet && !player.isDisabled;
        }

        /// <summary>まわりの物にめり込まない、床の上の位置を探す。</summary>
        internal static Vector3 FreeSpot(Vector3 center, float minRadius, float maxRadius, float clearance = 0.35f)
        {
            int mask = SemiFunc.LayerMaskGetVisionObstruct();
            for (int i = 0; i < 24; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(minRadius, maxRadius);
                var candidate = center + new Vector3(offset.x, 0f, offset.y);
                if (Physics.Linecast(center, candidate, mask, QueryTriggerInteraction.Ignore))
                {
                    continue; // 壁の向こう
                }
                if (Physics.CheckSphere(candidate, clearance, mask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                return candidate;
            }
            return center;
        }

        internal static string Name(PlayerAvatar player)
        {
            return player ? player.playerName : "?";
        }

        internal static bool IsAscii(string text)
        {
            foreach (char c in text)
            {
                if (c > 127)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// MOD が出したアイテム(グレネードなど)を「購入したアイテム」の記録から消す。
    /// 出したアイテムは、ゲームが名前を付けるときに購入したアイテムの一覧へ足してしまい、次のレベルのトラックに出てくるため。
    /// </summary>
    internal static class SpawnedItems
    {
        private class Entry
        {
            internal ItemAttributes Attributes;
            internal HashSet<string> NamesBefore;
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        internal static void Track(ItemAttributes attributes)
        {
            if (!attributes)
            {
                return;
            }
            var stats = StatsManager.instance;
            Entries.Add(new Entry
            {
                Attributes = attributes,
                NamesBefore = stats ? new HashSet<string>(stats.item.Keys) : new HashSet<string>(),
            });
        }

        internal static void Cleanup()
        {
            var stats = StatsManager.instance;
            if (stats)
            {
                foreach (var entry in Entries)
                {
                    // 壊れたアイテムでも名前は読める(Unity の null 判定は使わない)
                    string name = (object)entry.Attributes != null ? entry.Attributes.instanceName : null;
                    if (string.IsNullOrEmpty(name) || entry.NamesBefore.Contains(name))
                    {
                        continue;
                    }
                    stats.item.Remove(name);
                    stats.itemStatBattery.Remove(name);
                }
            }
            Entries.Clear();
        }
    }

    /// <summary>レベルが変わるたびに増える番号。各機能が「このレベルでもう処理したか」を覚えるのに使う。</summary>
    internal static class Phase
    {
        internal static int Generation;

        [HarmonyPatch(typeof(RunManager), nameof(RunManager.ChangeLevel))]
        private static class ChangeLevelPatch
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(bool _completedLevel, bool _levelFailed)
            {
                try
                {
                    if (Net.IsHost)
                    {
                        Roles.OnLeavingLevel(_completedLevel && !_levelFailed);
                        ShopBudget.OnLeavingLevel();
                        Duel.OnLeavingLevel();
                        Traits.OnLeavingLevel();
                        SpawnedItems.Cleanup();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"レベル終了時の処理に失敗しました: {e}");
                }
                Generation++;
            }
        }
    }

    /// <summary>毎フレームの処理とコルーチン用。シーンが変わっても消えないオブジェクト。</summary>
    internal class Runner : MonoBehaviour
    {
        private static Runner instance;
        private static readonly Dictionary<string, float> ErrorTimes = new Dictionary<string, float>();

        internal static Runner Instance
        {
            get
            {
                Ensure();
                return instance;
            }
        }

        internal static void Ensure()
        {
            if (instance)
            {
                return;
            }
            var go = new GameObject("FloorRoles.Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Runner>();
        }

        private void Update()
        {
            if (!Net.IsHost || !Net.Ready)
            {
                return;
            }
            Guard("役職", Roles.Tick);
            Guard("所持金", ShopBudget.Tick);
            Guard("決闘", Duel.Tick);
            Guard("役職特性", Traits.Tick);
        }

        private static void Guard(string label, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                // 毎フレーム同じエラーでログが埋まらないように、5 秒に 1 回だけ出す
                if (!ErrorTimes.TryGetValue(label, out float last) || Time.unscaledTime - last > 5f)
                {
                    ErrorTimes[label] = Time.unscaledTime;
                    Plugin.Log.LogError($"{label}の処理でエラー: {e}");
                }
            }
        }
    }
}
