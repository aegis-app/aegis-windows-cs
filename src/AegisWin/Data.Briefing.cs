using System.Text.Json.Nodes;

namespace AegisWin;

// ========================================
// 📋 ブリーフィング (まとめ) の構築
// ========================================
// 📌 構成: 先頭に「📌 現在のまとめ」カード、その後に 地点別天気 →
// 台風 → 津波 → EEW → 地震 の順でセクションを並べる。
// 📌 サーバーモードでは AEGIS サーバーの /api/briefing をそのまま表示し、
// 深刻度 (severity) を表示レベルへ変換する。まとめカードは両モード共通で
// セクション群から導出する。

public static partial class Data
{
    // ----------------------------------------
    // ⚙️ 取得方式 (Settings から反映される)
    // ----------------------------------------
    /// <summary>取得方式: "direct" | "server"</summary>
    public static string Mode { get; set; } = "direct";

    /// <summary>サーバーモード時の接続先ベース URL</summary>
    public static string ServerUrl { get; set; } = "http://127.0.0.1:8765";

    /// <summary>地点の天気セクションを作る (警報行 + 気象庁予報 + Open-Meteo)</summary>
    public static async Task<Section> WeatherSectionAsync(Location loc)
    {
        var bodyParts = new List<string>();
        WarningInfo warnings = new();

        // 🏢 気象庁予報 (警報の区域コード照合にも使う)
        try
        {
            string json = await FetchAsync(JmaForecastUrl(loc), acceptJson: true);
            var root = ParseJson(json);
            var now = NowInTokyo();
            string forecast = ParseForecast(root, loc, now);
            if (forecast.Length > 0) bodyParts.Add(forecast);
            string pops = ParsePops(root, now);
            if (pops.Length > 0) bodyParts.Add("降水確率: " + pops);
            string temps = ParseTemps(root, loc);
            if (temps.Length > 0) bodyParts.Add(temps);
            string areaCode = ForecastAreaCode(root, loc) ?? "";
            warnings = A2sWarningInfoFor(loc.PrefCode, areaCode);
        }
        catch (Exception)
        {
            warnings = A2sWarningInfoFor(loc.PrefCode, "");
            bodyParts.Add("気象庁の予報を取得できませんでした");
        }

        // 🌐 Open-Meteo (現在・時間別・週間)
        try
        {
            bodyParts.Add(await FetchOpenMeteoTextAsync(loc));
        }
        catch (Exception)
        {
            bodyParts.Add("現在の天気を取得できませんでした");
        }

        string body = string.Join("\n", bodyParts);
        if (warnings.Names.Count > 0)
        {
            string line = "⚠ 発表中： " + string.Join("、", warnings.Names);
            if (warnings.ReportDatetime is not null) line += $"（{warnings.ReportDatetime}発表）";
            body = line + "\n" + body;
        }
        string level = warnings.Names.Any(n => n.Contains("特別警報")) ? "danger"
            : warnings.Names.Count > 0 ? "warn" : "info";
        return new Section($"🌤 {loc.Name}の天気", body, level);
    }

    /// <summary>地震セクションを作る (直近3件)</summary>
    public static async Task<Section> QuakeSectionAsync()
    {
        var items = await QuakeItemsAsync(3);
        if (items.Count == 0)
            return new Section("🌋 地震情報", "最近の地震情報はありません", "info");
        var lines = items.Select(q =>
            $"{SeverityEmoji(q.Sev)} {q.Place}\n{q.Detail}");
        string level = items.Any(q => q.Sev == "CRITICAL") ? "danger"
            : items.Any(q => q.Sev == "HIGH") ? "warn" : "info";
        return new Section("🌋 地震情報", string.Join("\n", lines), level);
    }

    /// <summary>ブリーフィング全体を構築する (先頭はまとめカード)</summary>
    public static async Task<List<Section>> BuildBriefingAsync()
    {
        List<Section> sections;
        if (Mode == "server")
        {
            sections = await FetchServerBriefingAsync();
        }
        else
        {
            sections = new List<Section>();
            foreach (var loc in Locations)
            {
                try { sections.Add(await WeatherSectionAsync(loc)); }
                catch (Exception e)
                {
                    sections.Add(new Section($"🌤 {loc.Name}の天気",
                        $"取得できませんでした（{FriendlyError(e)}）", "warn"));
                }
            }
            if (await TyphoonSectionAsync() is { } typhoon) sections.Add(typhoon);
            sections.Add(await TsunamiSectionAsync());
            if (await EewSectionAsync() is { } eew) sections.Add(eew);
            sections.Add(await QuakeSectionAsync());
        }
        sections.Insert(0, Summarize(sections));
        return sections;
    }

    // ----------------------------------------
    // 🖥️ サーバーモード
    // ----------------------------------------
    /// <summary>AEGIS サーバーの /api/briefing からセクション群を得る</summary>
    public static async Task<List<Section>> FetchServerBriefingAsync()
    {
        string url = $"{ServerUrl.TrimEnd('/')}/api/briefing";
        string json = await FetchAsync(url, acceptJson: true);
        var root = ParseJson(json);
        var result = new List<Section>();
        if (ArrOf(root, "sections") is { } arr)
        {
            foreach (var node in arr)
            {
                string title = StrOf(node, "title") ?? "";
                string body = StrOf(node, "body") ?? "";
                string severity = StrOf(node, "severity") ?? "INFO";
                string level = severity switch
                {
                    "CRITICAL" => "danger",
                    "HIGH" or "MEDIUM" => "warn",
                    _ => "info",
                };
                string? guide = StrOf(node, "action_guide");
                if (!string.IsNullOrEmpty(guide)) body += $"\n▶ 行動ガイド： {guide}";
                if (title.Length > 0) result.Add(new Section(title, body, level));
            }
        }
        return result;
    }

    /// <summary>サーバーモードの接続テスト (/api/history が応えるか)</summary>
    public static async Task<bool> TestServerConnectionAsync(string serverUrl)
    {
        try
        {
            await FetchAsync($"{serverUrl.TrimEnd('/')}/api/history", acceptJson: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    // ----------------------------------------
    // 📌 まとめカードの導出
    // ----------------------------------------
    /// <summary>
    /// セクション群から「📌 現在のまとめ」を導出する。天気セクションの
    /// 「⚠ 発表中： 」行から警報名を集め (発表注記は落とす)、津波は
    /// レベルから有無を、EEW・台風はセクションの有無から拾う。
    /// </summary>
    public static Section Summarize(List<Section> sections)
    {
        var lines = new List<string>();
        bool danger = false, warn = false;

        // ⚠ 警報・注意報 (地点をまたいで重複排除)
        var warningNames = new List<string>();
        foreach (var s in sections)
        {
            foreach (var line in s.Body.Split('\n'))
            {
                if (!line.StartsWith("⚠ 発表中： ")) continue;
                string rest = line.Substring("⚠ 発表中： ".Length);
                int paren = rest.IndexOf('（');
                if (paren >= 0) rest = rest.Substring(0, paren);
                foreach (var name in rest.Split('、'))
                {
                    string n = name.Trim();
                    if (n.Length > 0 && !warningNames.Contains(n)) warningNames.Add(n);
                }
            }
        }
        if (warningNames.Count > 0)
        {
            lines.Add("⚠ 警報・注意報: " + string.Join("、", warningNames));
            if (warningNames.Any(n => n.Contains("特別警報"))) danger = true; else warn = true;
        }

        // 🌊 津波
        var tsunami = sections.FirstOrDefault(s => s.Title.Contains("津波"));
        if (tsunami is not null)
        {
            if (tsunami.Level == "danger") { lines.Add("津波: 警報・注意報あり"); danger = true; }
            else if (tsunami.Level == "warn") { lines.Add("津波: 注意報あり"); warn = true; }
            else lines.Add("津波: なし");
        }

        // 🚨 EEW
        if (sections.Any(s => s.Title.Contains("緊急地震速報")))
        {
            lines.Add("緊急地震速報: 発表中");
            danger = true;
        }

        // 🌀 台風
        if (sections.Any(s => s.Title.Contains("台風")))
        {
            lines.Add("台風: 情報あり");
            warn = true;
        }

        // 🌋 最新の地震
        var quakes = sections.FirstOrDefault(s => s.Title.Contains("地震情報"));
        if (quakes is not null)
        {
            string first = quakes.Body.Split('\n').FirstOrDefault() ?? "";
            if (first.Length > 0 && !first.Contains("最近の地震情報はありません"))
                lines.Add($"最新の地震: {first}");
        }

        if (lines.Count == 0)
            lines.Add("現在、特に注意が必要な情報はありません。");
        string level = danger ? "danger" : warn ? "warn" : "ok";
        return new Section("📌 現在のまとめ", string.Join("\n", lines), level);
    }
}
