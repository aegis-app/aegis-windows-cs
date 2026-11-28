namespace AegisWin;

// ========================================
// 🌋 地震・津波の整形
// ========================================
// 📌 地震データは A.A.S 経由で「統合済みの1イベント」として届くが、
// 表示整形は電文単位のマージ (MergeQuakeGroup) を経由させる。統合側の
// 形式が変わっても、表示文・観測点情報・地点別震度はここで一元的に作る。
// 📌 地点別震度は「実測のみ」: 観測点の実測が無い地点は推定で埋めず
// 「不明（観測点の実測なし）」と明示する。

public static partial class Data
{
    // ----------------------------------------
    // 📏 震度スケールと深刻度
    // ----------------------------------------
    /// <summary>震度スケール値 (10,20,…,70) を表示文字列にする</summary>
    public static string ScaleToStr(int scale) => scale switch
    {
        <= 0 => "不明",
        < 15 => "1",
        < 25 => "2",
        < 35 => "3",
        < 45 => "4",
        45 => "5弱",
        < 55 => "5強",
        55 => "6弱",
        < 65 => "6強",
        _ => "7",
    };

    /// <summary>深刻度を震度とマグニチュードから判定する</summary>
    public static string SeverityOf(int scale, double? mag)
    {
        double m = mag ?? 0;
        if (scale >= 45 || m >= 6.0) return "CRITICAL";
        if (scale >= 40 || m >= 5.0) return "HIGH";
        if (scale >= 30 || m >= 4.5) return "MEDIUM";
        if (scale >= 20) return "LOW";
        return "INFO";
    }

    /// <summary>深刻度の絵文字</summary>
    public static string SeverityEmoji(string sev) => sev switch
    {
        "CRITICAL" => "🔴",
        "HIGH" => "🟠",
        "MEDIUM" => "🟡",
        "LOW" => "🟢",
        _ => "ℹ️",
    };

    // ----------------------------------------
    // 🛰️ 電文 (統合前の1報) の表現
    // ----------------------------------------
    /// <summary>観測点1件 (Addr は観測点名または地域名)</summary>
    public sealed record QuakePoint(string Pref, string Addr, int Scale, bool IsArea);

    /// <summary>地震電文1報ぶんの生データ。MergeQuakeGroup で表示用に統合する</summary>
    public sealed class QuakeTelegram
    {
        public string Time { get; set; } = "";
        public DateTime IssueTime { get; set; }
        public string HypoName { get; set; } = "";
        public double? Mag { get; set; }
        public int? DepthKm { get; set; }
        public int MaxScale { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public bool Foreign { get; set; }
        public string IssueType { get; set; } = "";
        public string? IssueCorrect { get; set; }
        public string? TsunamiKind { get; set; }
        public string? TsunamiText { get; set; }
        public string? TopPoint { get; set; }
        public bool TopPointIsArea { get; set; }
        public List<QuakePoint> Points { get; } = new();
        public List<(string Pref, int Scale)> PrefScales { get; } = new();
    }

    /// <summary>電文種別の優先順位 (詳細 > 震度速報系 > 震源情報 > 速報)</summary>
    public static int QuakeTypeRank(string? issueType) => issueType switch
    {
        "DetailScale" => 4,
        "ScaleAndDestination" => 3,
        "Foreign" => 3,
        "Destination" => 2,
        "ScalePrompt" => 1,
        _ => 0,
    };

    /// <summary>電文の状態ラベル (訂正 / 速報 / 確定)</summary>
    public static string QuakeIssueLabel(string? issueType, string? issueCorrect)
    {
        if (!string.IsNullOrEmpty(issueCorrect) && issueCorrect != "None") return "訂正";
        if (issueType == "ScalePrompt") return "速報";
        return "確定";
    }

    private static bool IsDecidedTsunami(string? kind)
        => kind is not null and not "Checking" and not "Unknown";

    /// <summary>
    /// 同一イベントの電文群を表示用 Quake に統合する。優先順位の高い電文を
    /// 主とし、震源・マグニチュード・深さ・座標は「最初に値が入った電文」、
    /// 津波区分は Checking/Unknown でない確定値を優先して採用する。
    /// 観測点は (都道府県|地点名|地域フラグ) で重複排除し最大震度を残す。
    /// </summary>
    public static Quake MergeQuakeGroup(List<QuakeTelegram> group)
    {
        var ordered = group
            .OrderByDescending(t => QuakeTypeRank(t.IssueType))
            .ThenByDescending(t => t.IssueTime)
            .ToList();
        var head = ordered[0];

        string hypo = ordered.Select(t => t.HypoName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";
        double? mag = ordered.Select(t => t.Mag).FirstOrDefault(m => m is not null);
        int? depth = ordered.Select(t => t.DepthKm).FirstOrDefault(d => d is not null);
        var coord = ordered.FirstOrDefault(t => t.Lat != 0 || t.Lon != 0);
        int maxScale = ordered.Max(t => t.MaxScale);
        var tsunami = ordered.FirstOrDefault(t => IsDecidedTsunami(t.TsunamiKind)) ?? head;

        // 🗺️ 観測点の統合 (キーは 都道府県|地点名|地域フラグ)
        var pointMap = new Dictionary<string, QuakePoint>();
        foreach (var t in ordered)
        {
            foreach (var p in t.Points)
            {
                string key = $"{p.Pref}|{p.Addr}|{p.IsArea}";
                if (!pointMap.TryGetValue(key, out var cur) || p.Scale > cur.Scale)
                    pointMap[key] = p;
            }
        }
        var points = pointMap.Values.ToList();

        // 都道府県別最大震度 (明示値と観測点からの集計の最大)
        var prefMax = new Dictionary<string, int>();
        foreach (var t in ordered)
            foreach (var (pref, scale) in t.PrefScales)
                prefMax[pref] = Math.Max(prefMax.GetValueOrDefault(pref), scale);
        foreach (var p in points)
            prefMax[p.Pref] = Math.Max(prefMax.GetValueOrDefault(p.Pref), p.Scale);

        string detail = BuildQuakeDetail(head, hypo, mag, depth, maxScale,
            tsunami.TsunamiKind, tsunami.TsunamiText, points, prefMax);
        string observation = BuildObservationSummary(points, maxScale);
        return new Quake(hypo, detail, SeverityOf(maxScale, mag), maxScale,
            coord?.Lat ?? 0, coord?.Lon ?? 0, head.Time, observation);
    }

    private static string BuildQuakeDetail(QuakeTelegram head, string hypo, double? mag, int? depth,
        int maxScale, string? tsunamiKind, string? tsunamiText,
        List<QuakePoint> points, Dictionary<string, int> prefMax)
    {
        var parts = new List<string>();

        // 発生時刻と電文ラベル
        string when = TryParseJstTime(head.Time, out var t)
            ? t.ToString("MM/dd HH:mm") : head.Time;
        parts.Add($"{when}発生");
        parts.Add(QuakeIssueLabel(head.IssueType, head.IssueCorrect));
        parts.Add(mag is { } m ? $"M{NumStr(m)}" : "M不明");
        parts.Add(depth switch
        {
            null => "深さ不明",
            0 => "深さごく浅い",
            { } d => $"深さ約{d}km",
        });

        // 🌊 津波区分のサフィックス
        string? tsunamiSuffix = tsunamiKind switch
        {
            "None" => "津波の心配なし",
            "NonEffective" => "若干の海面変動の可能性あり",
            "Watch" => "津波注意報レベル",
            "Warning" or "MajorWarning" => "津波警報レベル",
            "Checking" => "津波の有無は確認中",
            _ => null,
        };
        if (tsunamiSuffix is not null) parts.Add(tsunamiSuffix);
        if (head.Foreign && !string.IsNullOrEmpty(tsunamiText)) parts.Add(tsunamiText);

        // 最大震度観測点 (地域でない観測点を優先し、残りは件数で示す)
        var tops = points.Where(p => p.Scale == maxScale).ToList();
        if (tops.Count > 0)
        {
            var top = tops.FirstOrDefault(p => !p.IsArea) ?? tops[0];
            string s = $"最大震度観測 {top.Addr}";
            if (top.IsArea) s += "（地域）";
            if (tops.Count > 1) s += $" ほか{tops.Count - 1}地点";
            parts.Add(s);
        }

        // 📍 地点別: 実測震度と都道府県内の最大震度
        var seenPrefs = new HashSet<string>();
        foreach (var loc in Locations)
        {
            parts.Add(LocationIntensityText(loc, points));
            string? prefName = PrefNameByCode(loc.PrefCode);
            if (prefName is not null && seenPrefs.Add(prefName)
                && prefMax.TryGetValue(prefName, out int ps))
                parts.Add($"{prefName}内の最大震度 {ScaleToStr(ps)}");
        }

        return string.Join(" / ", parts);
    }

    /// <summary>地点に対応する市区町村名。固定地点は決め打ちし、追加地点は
    /// 名前が市区町村で終わるときだけ採用する (一致判定に使えるため)</summary>
    public static string? LocationCity(Location loc)
    {
        if (loc.Name == Takasaki.Name) return "高崎市";
        if (loc.Name == Omiya.Name) return "さいたま市";
        if (loc.Name.EndsWith("市") || loc.Name.EndsWith("区")
            || loc.Name.EndsWith("町") || loc.Name.EndsWith("村"))
            return loc.Name;
        return null;
    }

    /// <summary>地点の実測震度テキスト。観測点名が市区町村で始まるものを
    /// 実測として採用し、無ければ「不明（観測点の実測なし）」とする</summary>
    public static string LocationIntensityText(Location loc, List<QuakePoint> points)
    {
        string? city = LocationCity(loc);
        if (city is not null)
        {
            var hit = points.Where(p => !p.IsArea && p.Addr.StartsWith(city))
                .OrderByDescending(p => p.Scale).FirstOrDefault();
            if (hit is not null)
                return $"{city}の震度 {ScaleToStr(hit.Scale)}（実測／観測点「{hit.Addr}」の実測震度 {ScaleToStr(hit.Scale)}）";
        }
        return $"{loc.Name}の震度 不明（観測点の実測なし）";
    }

    /// <summary>観測点情報の要約 (最大震度観測点 + 主要観測点 上位5件)</summary>
    public static string BuildObservationSummary(List<QuakePoint> points, int maxScale)
    {
        if (points.Count == 0) return "観測点情報（実測）\n観測点の情報はありません";
        var lines = new List<string> { "観測点情報（実測）" };
        var tops = points.Where(p => p.Scale == maxScale)
            .OrderBy(p => p.IsArea).ThenBy(p => p.Addr).ToList();
        if (tops.Count > 0)
        {
            var top = tops[0];
            lines.Add($"最大震度観測点: {top.Addr} 震度{ScaleToStr(top.Scale)}" + (top.IsArea ? "（地域）" : ""));
        }
        var majors = points.OrderByDescending(p => p.Scale).ThenBy(p => p.Addr).Take(5).ToList();
        lines.Add("主要観測点: " + string.Join("、",
            majors.Select(p => $"{p.Addr} 震度{ScaleToStr(p.Scale)}" + (p.IsArea ? "（地域）" : ""))));
        return string.Join("\n", lines);
    }

    // ----------------------------------------
    // 🌊 津波情報の整形
    // ----------------------------------------
    /// <summary>津波の対象区域1件</summary>
    public sealed record TsunamiArea(string Name, string Grade, string? HeightLabel,
        string? ArrivalLabel, string? Condition);

    /// <summary>津波情報1件ぶん (解除は Cancelled = true)</summary>
    public sealed class TsunamiInfo
    {
        public bool Cancelled { get; set; }
        public string Time { get; set; } = "";
        public List<TsunamiArea> Areas { get; } = new();
    }

    /// <summary>一度に表示する津波区域の上限 (超過分は「ほかN区域」)</summary>
    public const int TsunamiMaxAreas = 12;

    /// <summary>津波グレードの優先順位</summary>
    public static int TsunamiGradeRank(string? grade) => grade switch
    {
        "MajorWarning" => 3,
        "Warning" => 2,
        "Watch" => 1,
        _ => 0,
    };

    /// <summary>津波グレードの表示名</summary>
    public static string TsunamiGradeLabel(string? grade) => grade switch
    {
        "MajorWarning" => "大津波警報",
        "Warning" => "津波警報",
        "Watch" => "津波注意報",
        _ => grade ?? "",
    };

    /// <summary>津波情報を表示セクションに整形する</summary>
    public static Section BuildTsunamiSection(TsunamiInfo? info)
    {
        const string title = "🌊 津波情報";
        if (info is null)
            return new Section(title, "現在、津波警報・注意報は発表されていません。", "ok");
        if (info.Cancelled)
            return new Section(title,
                "津波警報・注意報はすべて解除されました。" + AnnouncedSuffix(info.Time), "ok");
        if (info.Areas.Count == 0)
            return new Section(title, "現在、津波警報・注意報は発表されていません。", "ok");

        int topRank = info.Areas.Max(a => TsunamiGradeRank(a.Grade));
        string level = topRank >= 2 ? "danger" : "warn";
        var lines = new List<string>();
        if (topRank >= 2)
            lines.Add("🔴 津波警報が発表されています。今すぐ高台や避難ビルなど安全な場所へ避難してください。");
        else
            lines.Add("🟠 津波注意報が発表されています。海から上がって、岸から離れてください。");

        // グレード別にまとめ、優先順位の高い順に並べる
        int shown = 0;
        foreach (var gradeGroup in info.Areas.GroupBy(a => a.Grade)
                     .OrderByDescending(g => TsunamiGradeRank(g.Key)))
        {
            var names = new List<string>();
            foreach (var area in gradeGroup)
            {
                if (shown >= TsunamiMaxAreas) break;
                names.Add(FormatTsunamiArea(area));
                shown++;
            }
            if (names.Count > 0)
                lines.Add($"{TsunamiGradeLabel(gradeGroup.Key)}: " + string.Join("、", names));
        }
        int rest = info.Areas.Count - shown;
        if (rest > 0) lines.Add($"ほか{rest}区域");

        return new Section(title, string.Join("\n", lines) + AnnouncedSuffix(info.Time), level);
    }

    private static string FormatTsunamiArea(TsunamiArea area)
    {
        var notes = new List<string>();
        if (!string.IsNullOrEmpty(area.HeightLabel)) notes.Add($"予想 {area.HeightLabel}");
        if (!string.IsNullOrEmpty(area.ArrivalLabel)) notes.Add($"第1波到達予想 {area.ArrivalLabel}");
        else if (!string.IsNullOrEmpty(area.Condition)) notes.Add(area.Condition);
        return notes.Count == 0 ? area.Name : $"{area.Name}（{string.Join("、", notes)}）";
    }

    private static string AnnouncedSuffix(string time)
        => string.IsNullOrEmpty(time) ? "" : $"\n発表: {time}";
}
