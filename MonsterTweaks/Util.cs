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
