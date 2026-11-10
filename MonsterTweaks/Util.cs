using BepInEx.Configuration;
using UnityEngine;

namespace MonsterTweaks
{
    internal static class Util
    {
        /// <summary>敵の AI を動かす側(ホスト、またはシングルプレイ)か。</summary>
        internal static bool IsAuthority => SemiFunc.IsMasterClientOrSingleplayer();

        internal static ConfigEntry<float> Float(ConfigFile cfg, string section, string key, float value, float min, float max, string description)
        {
            return cfg.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
        }

        internal static ConfigEntry<int> Int(ConfigFile cfg, string section, string key, int value, int min, int max, string description)
        {
            return cfg.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<int>(min, max)));
        }

        private class VisionMemo
        {
            internal float Distance, Close, CloseCrouch;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnemyVision, VisionMemo> Visions =
            new System.Runtime.CompilerServices.ConditionalWeakTable<EnemyVision, VisionMemo>();

        /// <summary>敵の視界の距離(遠く・近く・しゃがみ)を、元の値 × 倍率 にする(視界の判定はホストが行う)。</summary>
        internal static void ScaleVision(EnemyVision vision, float multiplier)
        {
            if (!vision)
            {
                return;
            }
            var memo = Visions.GetValue(vision, v => new VisionMemo { Distance = v.VisionDistance, Close = v.VisionDistanceClose, CloseCrouch = v.VisionDistanceCloseCrouch });
            vision.VisionDistance = memo.Distance * multiplier;
            vision.VisionDistanceClose = memo.Close * multiplier;
            vision.VisionDistanceCloseCrouch = memo.CloseCrouch * multiplier;
        }

        /// <summary>
        /// 生きている(倒れていない)プレイヤーのうち、position に一番近い人。
        /// range が 0 以下なら距離の制限なし。exclude は候補から外す。
        /// </summary>
        internal static PlayerAvatar NearestAlivePlayer(Vector3 position, float range, PlayerAvatar exclude = null)
        {
            PlayerAvatar best = null;
            float bestDistance = range > 0f ? range : float.MaxValue;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled || player == exclude)
                {
                    continue;
                }
                float distance = Vector3.Distance(position, player.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }
            return best;
        }
    }
}
