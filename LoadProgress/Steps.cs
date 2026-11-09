namespace LoadProgress
{
    /// <summary>ロードの 10 ステップ。ビット番号として使う。Docs/loading_flow.md 参照。</summary>
    internal static class Steps
    {
        public const int Count = 10;

        public const int Reload = 0;     // 1  中継シーン(Reload)到着
        public const int Avatar = 1;     // 2  本番シーン読み込み・キャラ生成
        public const int MapGen = 2;     // 3  ホストのマップ生成完了
        public const int Modules = 3;    // 4  マップのパーツ受信完了
        public const int Items = 4;      // 5a アイテム・貴重品の受信(NavMesh 構築の合図を受けた)
        public const int NavMesh = 5;    // 5b NavMesh・ミニマップ等のローカル準備完了
        public const int Spawn = 6;      // 6  スポーン地点へ移動
        public const int Enemies = 7;    // 7  敵の受信完了
        public const int Generated = 8;  // 8  生成完了(ゲーム開始の合図)
        public const int Animation = 9;  // 9  トラックのロード演出終了

        public const int AllMask = (1 << Count) - 1;

        public static readonly string[] Header = { "1", "2", "3", "4", "5a", "5b", "6", "7", "8", "9" };

        public static readonly string[] Labels =
        {
            "中継シーン",
            "キャラ生成",
            "マップ生成",
            "マップ受信",
            "貴重品受信",
            "NavMesh構築",
            "スポーン",
            "敵受信",
            "生成完了",
            "演出終了",
        };

        public static int Bit(int step) => 1 << step;

        public static bool Has(int mask, int step) => (mask & Bit(step)) != 0;

        /// <summary>
        /// ステップは順番に進むので、ある段階が完了していればそれより前も完了とみなす。
        /// (MOD なしのプレイヤーで観測できない段階を埋めるため)
        /// </summary>
        public static int FillLower(int mask)
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                if (Has(mask, i))
                {
                    return mask | ((1 << (i + 1)) - 1);
                }
            }
            return mask;
        }

        /// <summary>未完了の最初のステップ。全部終わっていれば Count。</summary>
        public static int Current(int mask)
        {
            for (int i = 0; i < Count; i++)
            {
                if (!Has(mask, i)) return i;
            }
            return Count;
        }
    }
}
