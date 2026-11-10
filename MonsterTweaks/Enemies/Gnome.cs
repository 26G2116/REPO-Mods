using BepInEx.Configuration;

namespace MonsterTweaks
{
    /// <summary>
    /// ノーム(おもちゃの小人。群れで出てきて物を壊す)。クラス名は EnemyGnome。
    /// - 耐久: 出現したときの体力に倍率をかける(HealthBoost。体力の判定はホストが行うので全員に効く)
    /// </summary>
    internal static class Gnome
    {
        private const string Section = "18. ノーム";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> HealthMultiplier;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "ノームの変更を使う");
            HealthMultiplier = Util.Float(cfg, Section, "耐久の倍率", 3f, 0.5f, 10f,
                "出現したときの体力の倍率(1 でゲーム本来と同じ)。次に出現したときから反映される");
            HealthBoost.Register(typeof(EnemyGnome), () => Enabled.Value ? HealthMultiplier.Value : 1f);
        }
    }
}
