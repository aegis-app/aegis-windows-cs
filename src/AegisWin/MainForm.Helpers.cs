namespace AegisWin;

// ========================================
// 🎨 GUI 共通部品 (テーマ・カード生成)
// ========================================
// 📌 ダークテーマ固定。カードはセクションのレベルで左帯の色を変え、
// danger / warn は見出しも色づけして一目で深刻度が分かるようにする。

public sealed partial class MainForm
{
    // ----------------------------------------
    // 🎨 テーマ
    // ----------------------------------------
    private static readonly Color Bg = Color.FromArgb(0x0B, 0x12, 0x20);
    private static readonly Color CardBg = Color.FromArgb(0x1B, 0x24, 0x36);
    private static readonly Color TextColor = Color.FromArgb(0xD8, 0xDE, 0xE9);
    private static readonly Color SubColor = Color.FromArgb(0x9A, 0xA7, 0xBD);

    private static readonly Font BaseFont = new("Yu Gothic UI", 10f);
    private static readonly Font BoldFont = new("Yu Gothic UI", 10.5f, FontStyle.Bold);
    private static readonly Font TitleFont = new("Yu Gothic UI", 15f, FontStyle.Bold);
    private static readonly Font BigFont = new("Yu Gothic UI", 14f, FontStyle.Bold);

    /// <summary>セクションレベルの色</summary>
    private static Color LevelColor(string level) => level switch
    {
        "danger" => Color.FromArgb(0xFF, 0x6B, 0x6B),
        "warn" => Color.FromArgb(0xFF, 0xB4, 0x54),
        "ok" => Color.FromArgb(0x58, 0xC1, 0x84),
        _ => Color.FromArgb(0x7F, 0xA8, 0xE0),
    };

    /// <summary>震度スケールの色 (震度が大きいほど赤く)</summary>
    private static Color ScaleColor(int scale) => scale switch
    {
        >= 45 => Color.FromArgb(0xC9, 0x3A, 0x3A),
        >= 40 => Color.FromArgb(0xD9, 0x7A, 0x2B),
        >= 30 => Color.FromArgb(0xA8, 0x87, 0x1D),
        >= 20 => Color.FromArgb(0x3E, 0x6B, 0x9E),
        _ => Color.FromArgb(0x54, 0x68, 0x7F),
    };

    // ----------------------------------------
    // 🧩 部品の生成
    // ----------------------------------------
    private static Label MakeLabel(string text, Font? font = null, Color? color = null)
        => new()
        {
            Text = text,
            Font = font ?? BaseFont,
            ForeColor = color ?? TextColor,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

    private static Button MakeButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            Font = BaseFont,
            ForeColor = TextColor,
            BackColor = Color.FromArgb(0x2A, 0x36, 0x4E),
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 4),
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(0x3D, 0x4E, 0x6B);
        return btn;
    }

    /// <summary>セクション表示用カード (左帯つきパネル) を作る</summary>
    private static Panel MakeCard(Section section, int width)
    {
        var card = new Panel
        {
            BackColor = CardBg,
            Width = width,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 8, 12, 10),
        };
        var accent = new Panel
        {
            BackColor = LevelColor(section.Level),
            Width = 4,
            Dock = DockStyle.Left,
        };
        card.Controls.Add(accent);
        var title = MakeLabel(section.Title, BoldFont,
            section.Level is "danger" or "warn" ? LevelColor(section.Level) : TextColor);
        title.Location = new Point(14, 8);
        card.Controls.Add(title);
        var body = new Label
        {
            Text = section.Body,
            Font = BaseFont,
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(14, 36),
            Width = width - 28,
        };
        // 本文の高さを行数から見積もる (折り返しは概算)
        int lines = 0;
        foreach (var line in section.Body.Split('\n'))
            lines += Math.Max(1, (int)Math.Ceiling(line.Length / 46.0));
        body.Height = Math.Max(20, lines * 20 + 4);
        card.Controls.Add(body);
        card.Height = body.Top + body.Height + 10;
        return card;
    }

    /// <summary>スクロール可能なカード置き場を作る</summary>
    private static FlowLayoutPanel MakeCardHost()
        => new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Bg,
            Padding = new Padding(10),
        };

    /// <summary>カード置き場へセクション群を流し込む</summary>
    private static void FillCards(FlowLayoutPanel host, List<Section> sections)
    {
        host.SuspendLayout();
        host.Controls.Clear();
        int width = Math.Max(320, host.ClientSize.Width - 30);
        foreach (var s in sections) host.Controls.Add(MakeCard(s, width));
        host.ResumeLayout();
    }
}
