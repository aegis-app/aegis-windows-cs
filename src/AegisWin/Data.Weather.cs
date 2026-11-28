using System.Globalization;
using System.Text.Json.Nodes;

namespace AegisWin;

// ========================================
// 🌤️ 天気予報 (気象庁予報 JSON + Open-Meteo)
// ========================================
// 📌 予報文・降水確率・気温は気象庁の予報 JSON、現在の観測に近い値と
// 時間別・週間は Open-Meteo から取る。Open-Meteo はモデル別サフィックス
// 付きキー (気温・天気は気象庁モデル、降水確率は ECMWF) だけを読み、
// サフィックス無しキーは読まない (モデル混在時の値の混同を防ぐため)。

public static partial class Data
{
    // ----------------------------------------
    // 🔤 WMO 天気コードの文と絵文字
    // ----------------------------------------
    /// <summary>WMO 天気コードの日本語文</summary>
    public static string WmoText(int code) => code switch
    {
        0 => "快晴",
        1 => "晴れ",
        2 => "一部曇り",
        3 => "曇り",
        45 or 48 => "霧",
        51 or 53 or 55 => "霧雨",
        56 or 57 => "着氷性の霧雨",
        61 or 63 => "雨",
        65 => "大雨",
        66 or 67 => "着氷性の雨",
        71 or 73 => "雪",
        75 => "大雪",
        77 => "雪あられ",
        80 or 81 => "にわか雨",
        82 => "激しい雨",
        85 or 86 => "雪",
        95 => "雷雨",
        96 or 99 => "雹を伴う雷雨",
        _ => "不明",
    };

    /// <summary>WMO 天気コードの絵文字。晴天系は降水確率が高いと雨に倒し、
    /// 夜間 (21時〜4時) は月にする</summary>
    public static string WmoEmoji(int code, int? pop, int hour)
    {
        bool fair = code is >= 0 and <= 3;
        if (fair && pop is >= 50) return "🌧";
        bool night = hour >= 21 || hour <= 4;
        return code switch
        {
            0 or 1 => night ? "🌙" : "☀️",
            2 => "⛅",
            3 => "☁️",
            45 or 48 => "🌫",
            51 or 53 or 55 => "🌦",
            61 or 63 or 65 or 66 or 67 => "🌧",
            71 or 73 or 77 or 85 or 86 => "🌨",
            75 => "❄️",
            80 or 81 => "🌦",
            82 => "🌧",
            95 or 96 or 99 => "⛈",
            _ => "🌤️",
        };
    }

    // ----------------------------------------
    // 🏢 気象庁 予報 JSON
    // ----------------------------------------
    public static string JmaForecastUrl(Location loc)
        => $"https://www.jma.go.jp/bosai/forecast/data/forecast/{loc.PrefCode}.json";

    /// <summary>予報区 (南部など) に対応する区域コード。予報・気温の
    /// エリア照合に使う</summary>
    public static string? ForecastAreaCode(JsonNode? root, Location loc)
    {
        var block = FirstBlockWith(root, "weathers") ?? (root as JsonArray)?.FirstOrDefault();
        var areas = ArrOf(block, "areas");
        if (areas is null || areas.Count == 0) return null;
        foreach (var a in areas)
            if (StrOf(Child(a, "area"), "name") == loc.AreaName)
                return StrOf(Child(a, "area"), "code");
        return StrOf(Child(areas[0], "area"), "code");
    }

    private static JsonNode? FirstBlockWith(JsonNode? root, string key)
    {
        if (root is not JsonArray blocks) return null;
        foreach (var b in blocks)
        {
            var areas = ArrOf(b, "areas");
            if (areas is not null && areas.Count > 0 && Child(areas[0], key) is not null) return b;
        }
        return null;
    }

    private static JsonNode? PickArea(JsonNode? block, Location loc, string codeKey)
    {
        var areas = ArrOf(block, "areas");
        if (areas is null || areas.Count == 0) return null;
        foreach (var a in areas)
            if (StrOf(Child(a, "area"), "name") == loc.AreaName) return a;
        return areas[0];
    }

    private static string DayLabel(DateTime date, DateTime now)
    {
        int diff = (date.Date - now.Date).Days;
        return diff switch
        {
            0 => "今日",
            1 => "明日",
            2 => "明後日",
            _ => date.ToString("M/d"),
        };
    }

    private static DateTime ParseIsoLocal(string? s)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.DateTime : default;

    /// <summary>予報を (日ラベル, 予報文) の一覧にする</summary>
    public static List<(string Label, string Text)> ParseForecastLabeled(JsonNode? root, Location loc, DateTime now)
    {
        var result = new List<(string, string)>();
        var block = FirstBlockWith(root, "weathers");
        if (block is null) return result;
        var area = PickArea(block, loc, "weathers");
        if (area is null) return result;
        var defines = ArrOf(block, "timeDefines");
        var weathers = ArrOf(area, "weathers");
        var codes = ArrOf(area, "weatherCodes");
        if (defines is null) return result;
        for (int i = 0; i < defines.Count; i++)
        {
            string? text = weathers is not null && i < weathers.Count ? StrOf(weathers[i]) : null;
            if (text is null && codes is not null && i < codes.Count
                && int.TryParse(StrOf(codes[i]), out int wc))
                text = WmoText(wc);
            if (text is null) continue;
            result.Add((DayLabel(ParseIsoLocal(StrOf(defines[i])), now), text));
        }
        return result;
    }

    /// <summary>予報文を "{ラベル}: {予報文}" の行テキストにする</summary>
    public static string ParseForecast(JsonNode? root, Location loc, DateTime now)
        => string.Join("\n", ParseForecastLabeled(root, loc, now).Select(p => $"{p.Label}: {p.Text}"));

    /// <summary>6時間刻みの降水確率。now を渡すと終了済みの枠は落とす</summary>
    public static string ParsePops(JsonNode? root, DateTime? now)
    {
        var block = FirstBlockWith(root, "pops");
        if (block is null) return "";
        var areas = ArrOf(block, "areas");
        if (areas is null || areas.Count == 0) return "";
        var area = areas[0];
        var defines = ArrOf(block, "timeDefines");
        var pops = ArrOf(area, "pops");
        if (defines is null || pops is null) return "";
        var frames = new List<string>();
        for (int i = 0; i < defines.Count && i < pops.Count && frames.Count < 6; i++)
        {
            var start = ParseIsoLocal(StrOf(defines[i]));
            if (now is { } n && start.AddHours(6) <= n) continue;
            string date = start.ToString("M/d");
            frames.Add($"{date} {start.Hour}〜{(start.Hour + 6) % 24}時 {StrOf(pops[i])}%");
        }
        return string.Join(" / ", frames);
    }

    /// <summary>今日・明日の気温。区域コードで照合し、無ければ先頭の区域</summary>
    public static string ParseTemps(JsonNode? root, Location loc)
    {
        var block = FirstBlockWith(root, "temps");
        if (block is null) return "";
        var areas = ArrOf(block, "areas");
        if (areas is null || areas.Count == 0) return "";
        string? code = ForecastAreaCode(root, loc);
        JsonNode? area = null;
        foreach (var a in areas)
            if (StrOf(Child(a, "area"), "code") == code) { area = a; break; }
        area ??= areas[0];
        string city = StrOf(Child(area, "area"), "name") ?? "";
        var temps = ArrOf(area, "temps");
        if (temps is null || temps.Count < 2) return "";
        string T(int i) => StrOf(temps[i]) ?? "";
        string text = $"気温（{city}）: 今日 {T(0)}〜{T(1)}℃";
        if (temps.Count >= 4) text += $" / 明日 {T(2)}〜{T(3)}℃";
        return text;
    }

    /// <summary>気象庁予報の本文 (予報文 + 降水確率 + 気温) を取得する</summary>
    public static async Task<string> FetchJmaTextAsync(Location loc)
    {
        string json = await FetchAsync(JmaForecastUrl(loc), acceptJson: true);
        var root = ParseJson(json);
        var now = NowInTokyo();
        var parts = new List<string>();
        string forecast = ParseForecast(root, loc, now);
        if (forecast.Length > 0) parts.Add(forecast);
        string pops = ParsePops(root, now);
        if (pops.Length > 0) parts.Add("降水確率: " + pops);
        string temps = ParseTemps(root, loc);
        if (temps.Length > 0) parts.Add(temps);
        if (parts.Count == 0) throw new InvalidOperationException("予報データが空でした");
        return string.Join("\n", parts);
    }

    // ----------------------------------------
    // 🌐 Open-Meteo (現在・時間別・週間)
    // ----------------------------------------
    public const string OpenMeteoModels = "jma_seamless,ecmwf_ifs025";

    public const string OpenMeteoSourceNote =
        "予報: Open-Meteo（気温・天気: 気象庁モデル MSM/GSM、降水確率: ECMWF IFS）";

    public static string OpenMeteoUrl(Location loc)
    {
        string lat = loc.Lat.ToString(CultureInfo.InvariantCulture);
        string lon = loc.Lon.ToString(CultureInfo.InvariantCulture);
        return "https://api.open-meteo.com/v1/forecast"
            + $"?latitude={lat}&longitude={lon}"
            + "&current=temperature_2m,weather_code,wind_speed_10m"
            + "&hourly=temperature_2m,precipitation_probability,weather_code,precipitation"
            + "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,weather_code"
            + $"&models={OpenMeteoModels}&timezone=Asia%2FTokyo&forecast_days=8";
    }

    private static double? DailyAt(JsonNode? root, string key, int idx)
    {
        var arr = ArrOf(Child(root, "daily"), key);
        return arr is not null && idx < arr.Count ? NumOf(arr[idx]) : null;
    }

    private static string MinMaxText(double? min, double? max)
        => min is { } a && max is { } b ? $"{RoundToLong(a)}〜{RoundToLong(b)}℃" : "";

    /// <summary>現在の天気パート (現在値 + 今日/明日の要約)</summary>
    public static string ParseOpenMeteoCurrent(JsonNode? root, DateTime now)
    {
        var cur = Child(root, "current");
        double? temp = NumOf(cur, "temperature_2m_jma_seamless");
        int? code = IntOf(cur, "weather_code_jma_seamless");
        double? wind = NumOf(cur, "wind_speed_10m_jma_seamless");
        double? min0 = DailyAt(root, "temperature_2m_min_jma_seamless", 0);
        double? max0 = DailyAt(root, "temperature_2m_max_jma_seamless", 0);
        int? pop0 = DailyAt(root, "precipitation_probability_max_ecmwf_ifs025", 0) is { } p0
            ? (int)RoundToLong(p0) : null;

        var lines = new List<string>();
        string head = $"現在：{WmoText(code ?? -1)}";
        if (temp is { } t) head += $" {NumStr(Round1(t))}℃";
        if (wind is { } w) head += $" / 風 {RoundToLong(w)}km/h";
        string mm = MinMaxText(min0, max0);
        if (mm.Length > 0) head += $" / 今日 {mm}";
        if (pop0 is { } pp) head += $" / 降水確率 最大{pp}%";
        lines.Add(head);

        if (pop0 is >= 80) lines.Add("☂ 降水確率が高めです。傘をお持ちください。");
        if (DailyAt(root, "precipitation_sum_jma_seamless", 0) is { } rain && rain > 0)
            lines.Add($"今日の降水量： {NumStr(Round1(rain))}mm");

        // 明日の要約
        double? min1 = DailyAt(root, "temperature_2m_min_jma_seamless", 1);
        double? max1 = DailyAt(root, "temperature_2m_max_jma_seamless", 1);
        int? code1 = DailyAt(root, "weather_code_jma_seamless", 1) is { } c1 ? (int)c1 : null;
        int? pop1 = DailyAt(root, "precipitation_probability_max_ecmwf_ifs025", 1) is { } p1
            ? (int)RoundToLong(p1) : null;
        string mm1 = MinMaxText(min1, max1);
        if (mm1.Length > 0 || code1 is not null)
        {
            string tomorrow = $"明日：{WmoText(code1 ?? -1)} {mm1}";
            if (pop1 is { } pp1) tomorrow += $" / 降水確率 最大{pp1}%";
            lines.Add(tomorrow.Trim());
        }
        return string.Join("\n", lines);
    }

    /// <summary>時間別予報 (現在から明日の終わりまで、3時間刻み・最大12件)</summary>
    public static string ParseHourly(JsonNode? root, DateTime now)
    {
        var hourly = Child(root, "hourly");
        var times = ArrOf(hourly, "time");
        if (times is null) return "";
        var temps = ArrOf(hourly, "temperature_2m_jma_seamless");
        var pops = ArrOf(hourly, "precipitation_probability_ecmwf_ifs025");
        var codes = ArrOf(hourly, "weather_code_jma_seamless");
        var precips = ArrOf(hourly, "precipitation_jma_seamless");
        var end = now.Date.AddDays(2);
        var entries = new List<string>();
        for (int i = 0; i < times.Count && entries.Count < 12; i++)
        {
            var t = ParseIsoLocal(StrOf(times[i]));
            if (t < now.Date.AddHours(now.Hour) || t >= end || t.Hour % 3 != 0) continue;
            int? code = codes is not null && i < codes.Count ? IntOf(codes[i]) : null;
            int? pop = pops is not null && i < pops.Count && NumOf(pops[i]) is { } pv
                ? (int)RoundToLong(pv) : null;
            string label = t.Date == now.Date ? $"{t.Hour}時"
                : t.Date == now.Date.AddDays(1) ? $"明日{t.Hour}時"
                : $"{t:M/d} {t.Hour}時";
            string e = $"{label} {WmoEmoji(code ?? -1, pop, t.Hour)}";
            if (temps is not null && i < temps.Count && NumOf(temps[i]) is { } tv)
                e += $"{RoundToLong(tv)}℃";
            if (pop is { } pp) e += $" {pp}%";
            if (precips is not null && i < precips.Count && NumOf(precips[i]) is { } pr && pr > 0)
                e += $" {NumStr(Round1(pr))}mm";
            entries.Add(e);
        }
        return entries.Count == 0 ? "" : "時間別： " + string.Join(" / ", entries);
    }

    /// <summary>週間予報 (今日を除き最大6日)</summary>
    public static string ParseWeekly(JsonNode? root)
    {
        var daily = Child(root, "daily");
        var times = ArrOf(daily, "time");
        if (times is null) return "";
        var entries = new List<string>();
        for (int i = 1; i < times.Count && entries.Count < 6; i++)
        {
            var d = ParseIsoLocal(StrOf(times[i]));
            int? code = DailyAt(root, "weather_code_jma_seamless", i) is { } c ? (int)c : null;
            int? pop = DailyAt(root, "precipitation_probability_max_ecmwf_ifs025", i) is { } p
                ? (int)RoundToLong(p) : null;
            string e = $"{d:M/d} {WmoEmoji(code ?? -1, pop, 12)}";
            string mm = MinMaxText(DailyAt(root, "temperature_2m_min_jma_seamless", i),
                DailyAt(root, "temperature_2m_max_jma_seamless", i));
            if (mm.Length > 0) e += mm;
            if (pop is { } pp) e += $" {pp}%";
            if (DailyAt(root, "precipitation_sum_jma_seamless", i) is { } rain && rain > 0)
                e += $" {NumStr(Round1(rain))}mm";
            entries.Add(e);
        }
        return entries.Count == 0 ? "" : "週間： " + string.Join(" / ", entries);
    }

    /// <summary>Open-Meteo の本文 (現在 + 時間別 + 週間 + 出典注記) を取得する</summary>
    public static async Task<string> FetchOpenMeteoTextAsync(Location loc)
    {
        string json = await FetchAsync(OpenMeteoUrl(loc), acceptJson: true);
        var root = ParseJson(json);
        var now = NowInTokyo();
        var parts = new List<string>();
        string current = ParseOpenMeteoCurrent(root, now);
        if (current.Length > 0) parts.Add(current);
        string hourly = ParseHourly(root, now);
        if (hourly.Length > 0) parts.Add(hourly);
        string weekly = ParseWeekly(root);
        if (weekly.Length > 0) parts.Add(weekly);
        if (parts.Count == 0) throw new InvalidOperationException("天気データが空でした");
        parts.Add(OpenMeteoSourceNote);
        return string.Join("\n", parts);
    }
}
