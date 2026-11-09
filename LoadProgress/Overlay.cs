using UnityEngine;

namespace LoadProgress
{
    /// <summary>IMGUI でロード進捗の表を描く。</summary>
    internal static class Overlay
    {
        private const float Pad = 10f;
        private const float NameWidth = 230f;
        private const float CellWidth = 30f;
        private const float StatusWidth = 190f;
        private const float RowHeight = 24f;
        private const float TitleHeight = 28f;

        private const string ColDone = "#5BD16B";
        private const string ColCurrent = "#FFD24A";
        private const string ColPending = "#6E6E6E";
        private const string ColDim = "#A0A0A0";

        private static Texture2D background;
        private static GUIStyle label;
        private static GUIStyle cell;
        private static GUIStyle title;

        private static void EnsureStyles()
        {
            if (label != null && background != null) return;

            background = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            background.Apply();

            // 日本語のプレイヤー名も出せるよう OS のフォントを使う
            Font font = Font.CreateDynamicFontFromOSFont(
                new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS Gothic", "Arial" }, 16);

            label = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = 16,
                richText = true,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                wordWrap = false,
            };
            label.normal.textColor = Color.white;

            cell = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
            title = new GUIStyle(label) { fontSize = 18, fontStyle = FontStyle.Bold };
        }

        public static void Draw(ProgressTracker t)
        {
            EnsureStyles();

            float scale = Mathf.Max(0.5f, Screen.height / 1080f * Mathf.Max(0.1f, Plugin.Scale.Value));
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(
                new Vector3(Plugin.PositionX.Value, Plugin.PositionY.Value, 0f),
                Quaternion.identity,
                new Vector3(scale, scale, 1f));

            int rows = t.Rows.Count;
            float width = Pad * 2 + NameWidth + CellWidth * Steps.Count + StatusWidth;
            float height = Pad * 2 + TitleHeight + RowHeight * (rows + 2);
            GUI.DrawTexture(new Rect(0f, 0f, width, height), background);

            float x0 = Pad;
            float y = Pad;

            // タイトル
            float elapsed = t.CompletedTime >= 0f ? t.CompletedTime : Time.unscaledTime - t.SessionStartTime;
            string state = t.CompletedTime >= 0f ? $"<color={ColDone}>完了</color>" : "ロード中";
            GUI.Label(new Rect(x0, y, width - Pad * 2, TitleHeight),
                $"ロード進捗  {state}  <color={ColDim}>{elapsed:0.0}s</color>", title);
            y += TitleHeight;

            // 見出し
            float cx = x0 + NameWidth;
            for (int i = 0; i < Steps.Count; i++)
            {
                GUI.Label(new Rect(cx + CellWidth * i, y, CellWidth, RowHeight), $"<color={ColDim}>{Steps.Header[i]}</color>", cell);
            }
            y += RowHeight;

            // 各プレイヤー
            foreach (ProgressTracker.Row row in t.Rows)
            {
                string name = row.Name;
                if (row.IsMaster) name = $"<color={ColCurrent}>[H]</color> " + name;
                if (row.IsLocal) name += $" <color={ColDim}>(自分)</color>";
                if (!row.Modded) name += $" <color={ColDim}>*</color>";
                GUI.Label(new Rect(x0, y, NameWidth - 4f, RowHeight), name, label);

                int current = Steps.Current(row.Mask);
                for (int i = 0; i < Steps.Count; i++)
                {
                    string mark;
                    if (Steps.Has(row.Mask, i)) mark = $"<color={ColDone}>■</color>";
                    else if (i == current) mark = $"<color={ColCurrent}>▶</color>";
                    else mark = $"<color={ColPending}>・</color>";
                    GUI.Label(new Rect(cx + CellWidth * i, y, CellWidth, RowHeight), mark, cell);
                }

                string status;
                if (current >= Steps.Count)
                {
                    status = $"<color={ColDone}>完了</color>";
                }
                else
                {
                    status = $"{Steps.Header[current]} {Steps.Labels[current]}";
                    if (!string.IsNullOrEmpty(row.Detail)) status += $" <color={ColDim}>{row.Detail}</color>";
                }
                GUI.Label(new Rect(cx + CellWidth * Steps.Count + 8f, y, StatusWidth - 8f, RowHeight), status, label);
                y += RowHeight;
            }

            // 凡例
            GUI.Label(new Rect(x0, y, width - Pad * 2, RowHeight),
                $"<color={ColDim}><size=13>■完了  ▶進行中  ・未着手    * MOD未導入(一部の段階は推定)</size></color>", label);

            GUI.matrix = oldMatrix;
        }
    }
}
