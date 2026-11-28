using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AegisWin;

// ========================================
// 🧪 AEGIS ロジック層フィクスチャテスト
// ========================================
// 📌 GUI 非依存のロジック層 (Data.* / A2sStream / Settings) を、
// 実 API の形式を模したフィクスチャで検証する。外部通信は行わない
// (取得系はすべてテスト用フックで差し替える)。
// 実行: dotnet run --project src/AegisTest/AegisTest.csproj -c Release

int passed = 0;
void Check(bool cond, string name)
{
    if (!cond) throw new InvalidOperationException($"FAIL: {name}");
    passed++;
}

try
{
    // ----------------------------------------
    // 🧱 基盤: バージョン・時刻・数値・地点検証
    // ----------------------------------------
    Check(Data.Version == "1.0.0", "version is 1.0.0");
    Check(Data.TryParseJstTime("2026/11/28 12:34:56", out var parsedTime)
        && parsedTime.Hour == 12 && parsedTime.Minute == 34, "jst time parse");
    Check(Data.TryParseJstTime("2026/11/28 12:34:56+09:00", out _), "jst time parse with suffix");
    Check(!Data.TryParseJstTime("2026-11-28", out _), "jst time parse rejects short");
    Check(!Data.TryParseJstTime(null, out _), "jst time parse rejects null");
    Check(Data.Round1(2.25) == 2.3 && Data.Round1(2.24) == 2.2, "round1 away from zero");
    Check(Data.RoundToLong(2.5) == 3 && Data.RoundToLong(-2.5) == -3, "round to long away from zero");
    Check(Data.NumStr(9.0) == "9.0" && Data.NumStr(4.8) == "4.8", "numstr keeps .0");
    Check(Data.FormatAgo(DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow) == "たった今", "format ago just now");
    Check(Data.FormatAgo(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow) == "5分前", "format ago minutes");

    Check(Data.ValidateLocationCoords(36.0, 139.0).Error is null, "coords ok in japan");
    Check(Data.ValidateLocationCoords(91, 139).Error is not null, "coords lat range error");
    Check(Data.ValidateLocationCoords(36, 181).Error is not null, "coords lon range error");
    Check(Data.ValidateLocationCoords(double.NaN, 139).Error is not null, "coords nan error");
    var coordWarn = Data.ValidateLocationCoords(0, 0);
    Check(coordWarn.Error is null && coordWarn.Warning is not null
        && coordWarn.Warning.Contains("日本域外"), "coords outside japan warning");

    Check(Data.FriendlyError(new TaskCanceledException()) == "タイムアウトしました", "friendly timeout");
    Check(Data.FriendlyError(new HttpStatusException(503)) == "サーバーエラー（HTTP 503）", "friendly http 503");
    Check(Data.FriendlyError(new HttpRequestException("x",
        new SocketException((int)SocketError.ConnectionRefused))) == "サーバーに接続できませんでした",
        "friendly connection refused");
    Check(Data.FriendlyError(new InvalidOperationException()) == "通信エラーが発生しました", "friendly generic");

    // ----------------------------------------
    // 🌐 HTTP: リトライ・キャッシュ・同時取得の共有
    // ----------------------------------------
    Data.RetryDelayOverride = _ => Task.CompletedTask;
    int attempts = 0;
    Data.FetchAttemptOverride = (attempt, url, json) =>
    {
        attempts++;
        if (attempts < 3) throw new HttpRequestException("down");
        return Task.FromResult("ok-body");
    };
    Check(await Data.FetchAsync("https://example.test/retry") == "ok-body" && attempts == 3,
        "http retries then succeeds");
    attempts = 0;
    Data.FetchAttemptOverride = (attempt, url, json) =>
    {
        attempts++;
        throw new HttpStatusException(404);
    };
    bool threw404 = false;
    try { await Data.FetchAsync("https://example.test/notfound"); }
    catch (HttpStatusException e) { threw404 = e.StatusCode == 404; }
    Check(threw404 && attempts == 1, "http 404 fails immediately");
    attempts = 0;
    Data.FetchAttemptOverride = (attempt, url, json) =>
    {
        attempts++;
        if (attempts < 2) throw new HttpStatusException(503);
        return Task.FromResult("recovered");
    };
    Check(await Data.FetchAsync("https://example.test/flaky") == "recovered" && attempts == 2,
        "http 503 is retried");
    // キャッシュ: 同一 URL の2回目は試行しない
    attempts = 0;
    Data.FetchAttemptOverride = (attempt, url, json) =>
    {
        attempts++;
        return Task.FromResult("cached-body");
    };
    await Data.FetchAsync("https://example.test/cache");
    Check(await Data.FetchAsync("https://example.test/cache") == "cached-body" && attempts == 1,
        "http response cache");
    Data.FetchAttemptOverride = null;
    Data.RetryDelayOverride = null;

    // ----------------------------------------
    // 📏 震度・深刻度
    // ----------------------------------------
    Check(Data.ScaleToStr(0) == "不明" && Data.ScaleToStr(10) == "1" && Data.ScaleToStr(20) == "2"
        && Data.ScaleToStr(30) == "3" && Data.ScaleToStr(40) == "4", "scale to str basic");
    Check(Data.ScaleToStr(45) == "5弱" && Data.ScaleToStr(50) == "5強"
        && Data.ScaleToStr(55) == "6弱" && Data.ScaleToStr(60) == "6強"
        && Data.ScaleToStr(70) == "7", "scale to str upper");
    Check(Data.SeverityOf(45, null) == "CRITICAL" && Data.SeverityOf(0, 6.0) == "CRITICAL", "severity critical");
    Check(Data.SeverityOf(40, null) == "HIGH" && Data.SeverityOf(0, 5.0) == "HIGH", "severity high");
    Check(Data.SeverityOf(30, null) == "MEDIUM" && Data.SeverityOf(0, 4.5) == "MEDIUM", "severity medium");
    Check(Data.SeverityOf(20, null) == "LOW" && Data.SeverityOf(10, null) == "INFO", "severity low/info");
    Check(Data.SeverityEmoji("CRITICAL") == "🔴" && Data.SeverityEmoji("HIGH") == "🟠"
        && Data.SeverityEmoji("MEDIUM") == "🟡" && Data.SeverityEmoji("LOW") == "🟢"
        && Data.SeverityEmoji("INFO") == "ℹ️", "severity emoji");

    // ----------------------------------------
    // 🌋 地震: 電文マージと表示文
    // ----------------------------------------
    Check(Data.QuakeTypeRank("DetailScale") == 4 && Data.QuakeTypeRank("ScaleAndDestination") == 3
        && Data.QuakeTypeRank("Foreign") == 3 && Data.QuakeTypeRank("Destination") == 2
        && Data.QuakeTypeRank("ScalePrompt") == 1, "quake type rank");
    Check(Data.QuakeIssueLabel("ScalePrompt", "None") == "速報", "quake label prompt");
    Check(Data.QuakeIssueLabel("DetailScale", "None") == "確定", "quake label final");
    Check(Data.QuakeIssueLabel("DetailScale", "訂正報") == "訂正", "quake label correct");

    Data.ResetLocations();
    var tg = new Data.QuakeTelegram
    {
        Time = "2026/11/19 06:41:00",
        HypoName = "群馬県南部",
        Mag = 4.8,
        DepthKm = 40,
        MaxScale = 40,
        Lat = 36.3,
        Lon = 139.0,
        IssueType = "DetailScale",
        IssueCorrect = "None",
        TsunamiKind = "None",
    };
    tg.Points.Add(new Data.QuakePoint("群馬県", "高崎市高松町", 40, false));
    tg.Points.Add(new Data.QuakePoint("埼玉県", "さいたま市大宮区天沼町", 30, false));
    tg.PrefScales.Add(("群馬県", 40));
    tg.PrefScales.Add(("埼玉県", 30));
    var merged = Data.MergeQuakeGroup(new List<Data.QuakeTelegram> { tg });
    Check(merged.Place == "群馬県南部" && merged.MaxScale == 40 && merged.Sev == "HIGH", "quake merged basics");
    Check(merged.Detail.Contains("11/19 06:41発生") && merged.Detail.Contains("確定")
        && merged.Detail.Contains("M4.8") && merged.Detail.Contains("深さ約40km"), "quake detail head");
    Check(merged.Detail.Contains("津波の心配なし"), "quake detail tsunami none");
    Check(merged.Detail.Contains("最大震度観測 高崎市高松町"), "quake detail top point");
    Check(merged.Detail.Contains("高崎市の震度 4（実測／観測点「高崎市高松町」の実測震度 4）"),
        "quake detail takasaki measured");
    Check(merged.Detail.Contains("さいたま市の震度 3（実測／観測点「さいたま市大宮区天沼町」の実測震度 3）"),
        "quake detail omiya measured");
    Check(merged.Detail.Contains("群馬県内の最大震度 4") && merged.Detail.Contains("埼玉県内の最大震度 3"),
        "quake detail pref max");
    Check(merged.Observation.Contains("観測点情報（実測）")
        && merged.Observation.Contains("最大震度観測点: 高崎市高松町 震度4"), "quake observation summary");

    // 実測なしの地点は推定で埋めない
    var tgNoPoints = new Data.QuakeTelegram
    {
        Time = "2026/11/19 06:41:00", HypoName = "遠方", MaxScale = 20, IssueType = "DetailScale",
    };
    var mergedFar = Data.MergeQuakeGroup(new List<Data.QuakeTelegram> { tgNoPoints });
    Check(mergedFar.Detail.Contains("高崎の震度 不明（観測点の実測なし）"), "quake location unknown without points");
    Check(mergedFar.Detail.Contains("M不明") && mergedFar.Detail.Contains("深さ不明"), "quake unknown mag/depth");
    // 深さ 0 は「ごく浅い」
    var tgShallow = new Data.QuakeTelegram
    {
        Time = "2026/11/19 06:41:00", HypoName = "浅い", DepthKm = 0, MaxScale = 20, IssueType = "DetailScale",
    };
    Check(Data.MergeQuakeGroup(new List<Data.QuakeTelegram> { tgShallow }).Detail.Contains("深さごく浅い"),
        "quake depth zero is shallow");
    // マージ: 速報より詳細を優先し、津波は確定値を採る
    var tgPrompt = new Data.QuakeTelegram
    {
        Time = "2026/11/19 06:41:00", HypoName = "", MaxScale = 30,
        IssueType = "ScalePrompt", TsunamiKind = "Checking", IssueTime = new DateTime(2026, 11, 19, 6, 41, 30),
    };
    var tgDetail = new Data.QuakeTelegram
    {
        Time = "2026/11/19 06:41:00", HypoName = "群馬県南部", Mag = 4.8, MaxScale = 40,
        IssueType = "DetailScale", TsunamiKind = "None", IssueTime = new DateTime(2026, 11, 19, 6, 42, 0),
    };
    var mergedPair = Data.MergeQuakeGroup(new List<Data.QuakeTelegram> { tgPrompt, tgDetail });
    Check(mergedPair.Place == "群馬県南部" && mergedPair.MaxScale == 40
        && mergedPair.Detail.Contains("津波の心配なし"), "quake merge prefers detail and decided tsunami");
    Check(Data.LocationCity(Data.Takasaki) == "高崎市" && Data.LocationCity(Data.Omiya) == "さいたま市",
        "location city fixed");
    Check(Data.LocationCity(new Location("伊勢崎市", 36.3, 139.2, "100000", "南部")) == "伊勢崎市",
        "location city from suffix");
    Check(Data.LocationCity(new Location("自宅", 36.3, 139.2, "100000", "南部")) is null,
        "location city null for plain name");

    // ----------------------------------------
    // 🌊 津波セクション
    // ----------------------------------------
    var tsunamiNone = Data.BuildTsunamiSection(null);
    Check(tsunamiNone.Level == "ok" && tsunamiNone.Body.Contains("発表されていません"), "tsunami none section");
    var tsunamiInfo = new Data.TsunamiInfo { Time = "2026/11/26 09:01:00" };
    tsunamiInfo.Areas.Add(new Data.TsunamiArea("宮城県沿岸", "Warning", "3m", "09:30", null));
    tsunamiInfo.Areas.Add(new Data.TsunamiArea("福島県沿岸", "Watch", "1m", null, "第1波の到達を確認"));
    var tsunamiSec = Data.BuildTsunamiSection(tsunamiInfo);
    Check(tsunamiSec.Level == "danger" && tsunamiSec.Body.Contains("津波警報")
        && tsunamiSec.Body.Contains("宮城県沿岸") && tsunamiSec.Body.Contains("予想 3m")
        && tsunamiSec.Body.Contains("第1波到達予想 09:30")
        && tsunamiSec.Body.Contains("第1波の到達を確認")
        && tsunamiSec.Body.Contains("発表: 2026/11/26 09:01:00"), "tsunami warning section");
    var tsunamiWatchOnly = new Data.TsunamiInfo { Time = "2026/11/26 09:01:00" };
    tsunamiWatchOnly.Areas.Add(new Data.TsunamiArea("福島県沿岸", "Watch", "1m", null, null));
    Check(Data.BuildTsunamiSection(tsunamiWatchOnly).Level == "warn", "tsunami watch level");
    var tsunamiCancelled = new Data.TsunamiInfo { Cancelled = true, Time = "2026/11/26 10:00:00" };
    Check(Data.BuildTsunamiSection(tsunamiCancelled).Level == "ok"
        && Data.BuildTsunamiSection(tsunamiCancelled).Body.Contains("解除"), "tsunami cancelled section");
    var tsunamiMany = new Data.TsunamiInfo();
    for (int i = 0; i < 15; i++)
        tsunamiMany.Areas.Add(new Data.TsunamiArea($"区域{i}", "Watch", null, null, null));
    Check(Data.BuildTsunamiSection(tsunamiMany).Body.Contains("ほか3区域"), "tsunami truncation");
    Check(Data.TsunamiGradeRank("MajorWarning") == 3 && Data.TsunamiGradeRank("Warning") == 2
        && Data.TsunamiGradeRank("Watch") == 1, "tsunami grade rank");

    Console.WriteLine($"--- part 1 done ({passed}) ---");

    // ----------------------------------------
    // 🚨 EEW: 見出し・ガイド・同一視キー・到達予想
    // ----------------------------------------
    string Fmt(DateTime t) => t.ToString("yyyy/MM/dd HH:mm:ss",
        System.Globalization.CultureInfo.InvariantCulture);
    var eewNow = Data.NowInTokyo();
    var eew = new Data.EewInfo
    {
        OriginTime = Fmt(eewNow.AddSeconds(-30)),
        HypoName = "群馬県南部",
        EstMaxScale = 45,
        Mag = 5.2,
        Depth = 10,
        Lat = 36.0,
        Lon = 139.0,
        IsWarn = true,
        Serial = 3,
        EventId = "20261126000123",
    };
    eew.Areas.Add(new Data.EewArea
    {
        Name = "群馬県南部", Pref = "群馬県", ScaleFrom = 45, ScaleTo = 50,
        ArrivalTime = eewNow.AddSeconds(40),
    });
    eew.Areas.Add(new Data.EewArea { Name = "埼玉県南部", Pref = "埼玉県", ScaleFrom = 40, ScaleTo = 99 });

    Check(Data.EewTitle(eew) == "🚨 緊急地震速報（警報）", "eew title warn");
    var eewForecast = new Data.EewInfo { OriginTime = eew.OriginTime, HypoName = "x", IsWarn = false };
    Check(Data.EewTitle(eewForecast) == "🚨 緊急地震速報（予報）", "eew title forecast");
    Check(Data.EewTitle(new Data.EewInfo { IsCancel = true }) == "🚨 緊急地震速報（取消）", "eew title cancel");
    Check(Data.EewBaseBody(eew).Contains("第3報") && Data.EewBaseBody(eew).Contains("予想最大震度: 5弱"),
        "eew base body");
    Check(Data.EewActionGuide(new Data.EewInfo { EstMaxScale = 45 }).Contains("身の安全を確保"),
        "eew guide strong");
    Check(Data.EewActionGuide(new Data.EewInfo { EstMaxScale = 40 }) == "やや強い揺れに備えてください。",
        "eew guide middle");
    Check(Data.EewActionGuide(new Data.EewInfo { EstMaxScale = 30 }) == "揺れに注意してください。",
        "eew guide caution");
    Check(Data.EewActionGuide(new Data.EewInfo { EstMaxScale = 0, IsWarn = true }).Contains("身の安全を確保"),
        "eew guide unknown warn strong");
    Check(Data.EewActionGuide(new Data.EewInfo { EstMaxScale = 0, IsWarn = false }) == "揺れに注意してください。",
        "eew guide unknown forecast caution");
    Check(Data.EewActionGuide(new Data.EewInfo { EstMaxScale = 45, IsCancel = true }) == "",
        "eew guide none for cancel");
    Check(Data.EewIdentityKey(eew) == $"eew-{eew.OriginTime}@36.0,139.0", "eew identity key coords");
    var eewNoCoord = new Data.EewInfo { OriginTime = "2026/11/26 09:00:00", HypoName = "テスト震源" };
    Check(Data.EewIdentityKey(eewNoCoord) == "eew-2026/11/26 09:00:00-テスト震源", "eew identity key hypo");
    Check(Data.EewIdentityKey(new Data.EewInfo
    {
        OriginTime = "2026/11/26 09:00:00", HypoName = "x", IsCancel = true,
    }).EndsWith("-cancel"), "eew identity key cancel");
    Check(Data.EewScaleRangeText(40, 99) == "4程度以上", "eew scale range or-more");
    Check(Data.EewScaleRangeText(45, 50) == "5弱〜5強", "eew scale range");
    Check(Data.EewScaleRangeText(40, 40) == "4", "eew scale range single");
    Check(Data.EewRegionalEstimateText(eew.Areas).Contains("埼玉県南部 震度4程度以上"),
        "eew regional estimate or-more");

    var selTakasaki = Data.SelectEewArea(eew, Data.Takasaki);
    Check(selTakasaki is not null && selTakasaki.Name == "群馬県南部", "eew select area takasaki");
    var arrTakasaki = Data.EewArrival(eew, Data.Takasaki);
    Check(arrTakasaki is not null && !arrTakasaki.Value.Estimated
        && Math.Abs((arrTakasaki.Value.Arrival - eewNow).TotalSeconds - 40) < 3,
        "eew arrival from announced");
    var arrOmiya = Data.EewArrival(eew, Data.Omiya);
    Check(arrOmiya is not null && arrOmiya.Value.Estimated, "eew arrival estimated fallback");
    var eewNoCoordInfo = new Data.EewInfo { OriginTime = Fmt(eewNow), HypoName = "x" };
    Check(Data.EewArrival(eewNoCoordInfo, Data.Takasaki) is null, "eew arrival null without coords");
    var countdown = Data.EewCountdownLines(eew, new[] { Data.Takasaki }, eewNow);
    Check(countdown.Count == 1 && countdown[0].Contains("高崎： あと約") && countdown[0].Contains("（予想）"),
        "eew countdown announced");
    var countdownPast = Data.EewCountdownLines(eew, new[] { Data.Takasaki }, eewNow.AddSeconds(50));
    Check(countdownPast.Count == 1 && countdownPast[0].Contains("到達した可能性が高いです"),
        "eew countdown arrived");
    Check(Data.EewCountdownLines(eew, new[] { Data.Takasaki }, eewNow.AddSeconds(100)).Count == 0,
        "eew countdown drops long past");
    Check(Data.EewLevel(eew, eewNow) == "danger", "eew level danger");
    Check(Data.EewLevel(new Data.EewInfo { OriginTime = Fmt(eewNow.AddMinutes(-10)) }, eewNow) == "warn",
        "eew level warn");
    Check(Data.EewLevel(new Data.EewInfo { OriginTime = Fmt(eewNow.AddMinutes(-20)) }, eewNow) is null,
        "eew level hidden after 15min");
    Check(Data.EewLevel(new Data.EewInfo { OriginTime = Fmt(eewNow.AddMinutes(1)) }, eewNow) == "danger",
        "eew level future is danger");
    var sample = Data.CreateSampleEew(eewNow);
    Check(sample.IsDemo && sample.EstMaxScale == 45
        && Math.Abs((sample.DemoArrival!.Value - eewNow).TotalSeconds - 30) < 1.5, "eew sample demo");

    // ----------------------------------------
    // 🌤️ 天気: WMO・気象庁予報・Open-Meteo
    // ----------------------------------------
    Check(Data.WmoText(0) == "快晴" && Data.WmoText(1) == "晴れ" && Data.WmoText(3) == "曇り"
        && Data.WmoText(65) == "大雨" && Data.WmoText(82) == "激しい雨"
        && Data.WmoText(99) == "雹を伴う雷雨" && Data.WmoText(12345) == "不明", "wmo text");
    Check(Data.WmoEmoji(0, null, 12) == "☀️" && Data.WmoEmoji(0, null, 23) == "🌙"
        && Data.WmoEmoji(1, null, 3) == "🌙", "wmo emoji day/night");
    Check(Data.WmoEmoji(2, 60, 12) == "🌧", "wmo emoji fair with high pop is rain");
    Check(Data.WmoEmoji(61, null, 12) == "🌧" && Data.WmoEmoji(3, null, 12) == "☁️"
        && Data.WmoEmoji(95, null, 12) == "⛈", "wmo emoji kinds");

    var jmaNow = Data.NowInTokyo();
    string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss+09:00",
        System.Globalization.CultureInfo.InvariantCulture);
    var jmaRoot = JsonNode.Parse($$"""
        [
          {"timeDefines":["{{Iso(jmaNow)}}","{{Iso(jmaNow.AddDays(1))}}"],
           "areas":[{"area":{"name":"南部","code":"100010"},
             "weatherCodes":["101","200"],"weathers":["晴れ 時々 くもり","くもり"],
             "winds":["北の風","南の風"],"waves":[]}]},
          {"timeDefines":["{{Iso(jmaNow.Date.AddHours(0))}}","{{Iso(jmaNow.Date.AddHours(6))}}","{{Iso(jmaNow.Date.AddHours(12))}}","{{Iso(jmaNow.Date.AddHours(18))}}"],
           "areas":[{"area":{"name":"南部","code":"100010"},"pops":["10","20","30","40"]}]},
          {"timeDefines":["{{Iso(jmaNow)}}","{{Iso(jmaNow)}}","{{Iso(jmaNow.AddDays(1))}}","{{Iso(jmaNow.AddDays(1))}}"],
           "areas":[{"area":{"name":"前橋","code":"100010"},"temps":["8","15","7","16"]}]}
        ]
        """);
    Check(Data.ForecastAreaCode(jmaRoot, Data.Takasaki) == "100010", "jma area code");
    var labeled = Data.ParseForecastLabeled(jmaRoot, Data.Takasaki, jmaNow);
    Check(labeled.Count == 2 && labeled[0].Label == "今日" && labeled[0].Text == "晴れ 時々 くもり"
        && labeled[1].Label == "明日", "jma forecast labeled");
    Check(Data.ParseForecast(jmaRoot, Data.Takasaki, jmaNow).Contains("今日: 晴れ 時々 くもり"),
        "jma forecast text");
    string popsEarly = Data.ParsePops(jmaRoot, jmaNow.Date);
    Check(popsEarly.Contains("0〜6時 10%") && popsEarly.Contains("18〜0時 40%"), "jma pops frames");
    string popsLate = Data.ParsePops(jmaRoot, jmaNow.Date.AddHours(13));
    Check(!popsLate.Contains("0〜6時") && !popsLate.Contains("6〜12時") && popsLate.Contains("12〜18時 30%"),
        "jma pops drops elapsed");
    Check(Data.ParseTemps(jmaRoot, Data.Takasaki) == "気温（前橋）: 今日 8〜15℃ / 明日 7〜16℃",
        "jma temps");

    // Open-Meteo: サフィックス付きキーだけを読む (サフィックス無しはダミー値)
    var omTimes = new List<string>();
    for (int i = 0; i < 48; i++) omTimes.Add(Iso(jmaNow.Date.AddHours(i)));
    string omTimesJson = string.Join(",", omTimes.Select(t => $"\"{t}\""));
    string Rep(string s, int n) => string.Join(",", Enumerable.Repeat(s, n));
    var omRoot = JsonNode.Parse($$"""
        {
          "current":{"temperature_2m":99.9,"temperature_2m_jma_seamless":12.34,
            "weather_code_jma_seamless":0,"wind_speed_10m_jma_seamless":9.6},
          "hourly":{"time":[{{omTimesJson}}],
            "temperature_2m_jma_seamless":[{{Rep("10.0", 48)}}],
            "precipitation_probability_ecmwf_ifs025":[{{Rep("20", 48)}}],
            "weather_code_jma_seamless":[{{Rep("1", 48)}}],
            "precipitation_jma_seamless":[{{Rep("0.0", 48)}}]},
          "daily":{"time":["{{Iso(jmaNow.Date)}}","{{Iso(jmaNow.Date.AddDays(1))}}","{{Iso(jmaNow.Date.AddDays(2))}}"],
            "temperature_2m_max_jma_seamless":[15.0,16.0,17.0],
            "temperature_2m_min_jma_seamless":[8.0,7.0,6.0],
            "precipitation_sum_jma_seamless":[3.2,0.0,0.0],
            "precipitation_probability_max_ecmwf_ifs025":[85,40,10],
            "weather_code_jma_seamless":[61,1,0]}
        }
        """);
    string omCurrent = Data.ParseOpenMeteoCurrent(omRoot, jmaNow);
    Check(omCurrent.Contains("現在：快晴 12.3℃") && !omCurrent.Contains("99"), "open-meteo current suffixed only");
    Check(omCurrent.Contains("風 10km/h") && omCurrent.Contains("今日 8〜15℃")
        && omCurrent.Contains("降水確率 最大85%"), "open-meteo current parts");
    Check(omCurrent.Contains("傘をお持ちください"), "open-meteo umbrella line at pop>=80");
    Check(omCurrent.Contains("今日の降水量： 3.2mm"), "open-meteo precipitation sum");
    Check(omCurrent.Contains("明日：晴れ 7〜16℃"), "open-meteo tomorrow line");
    var hourlyEntries = Data.ParseHourlyEntries(omRoot, jmaNow);
    Check(hourlyEntries.Count is > 0 and <= 12 && hourlyEntries.All(e => e.Time.Hour % 3 == 0),
        "open-meteo hourly entries 3h steps");
    Check(Data.ParseHourly(omRoot, jmaNow).StartsWith("時間別："), "open-meteo hourly text");
    var dailyEntries = Data.ParseDailyEntries(omRoot);
    Check(dailyEntries.Count == 3 && dailyEntries[0].Max == 15.0, "open-meteo daily entries");
    Check(Data.ParseWeekly(omRoot).StartsWith("週間：")
        && !Data.ParseWeekly(omRoot).Contains("85%"), "open-meteo weekly skips today");
    Check(Data.OpenMeteoModels == "jma_seamless,ecmwf_ifs025", "open-meteo models order fixed");
    Check(Data.OpenMeteoSourceNote.Contains("気象庁モデル") && Data.OpenMeteoSourceNote.Contains("ECMWF"),
        "open-meteo source note");

    // ----------------------------------------
    // 🌧️ レーダー: タイル計算・フレーム
    // ----------------------------------------
    var tokyoTile = Data.LatLonToTile(35.6812, 139.7671, 10);
    Check(tokyoTile.X == 909 && tokyoTile.Y == 403, "latlon to tile tokyo z10");
    Check(Data.JmaTileUrl("202611280900", "202611281200", 8, 10, 20)
        == "https://www.jma.go.jp/bosai/jmatile/data/nowc/202611280900/none/202611281200/surf/hrpns/8/10/20.png",
        "jma tile url");
    Check(Data.GsiTileUrl(8, 10, 20) == "https://cyberjapandata.gsi.go.jp/xyz/std/8/10/20.png",
        "gsi tile url");
    Check(Data.RainViewerTileUrl("/v2/radar/123", 7, 10, 20)
        == "https://tilecache.rainviewer.com/v2/radar/123/256/7/10/20/2/1_1.png", "rainviewer tile url");
    var coverage = Data.RainTileCoverage(100, 200);
    Check(coverage.Count == 4 && coverage.Any(p => p.OffsetX == -256 && p.OffsetY == -256)
        && coverage.Any(p => p.OffsetX == 256 && p.OffsetY == 256), "rain tile coverage z8 from z7");
    Check(Data.RainTileDrawSize == 512, "rain tile draw size");
    Check(Data.RadarFrameCacheReusable(jmaNow.AddMinutes(-9), jmaNow)
        && !Data.RadarFrameCacheReusable(jmaNow.AddMinutes(-11), jmaNow), "radar cache reusable 10min");
    Check(Data.RadarFrameAgeText(jmaNow.AddMinutes(-5), jmaNow) == "約5分前の観測", "radar age text");
    Check(Data.RadarFrameAgeText(jmaNow.AddMinutes(-25), jmaNow).Contains("古い観測です"),
        "radar age stale note");
    // バイト取得: 404 は即失敗、5xx はリトライ
    int byteAttempts = 0;
    Data.FetchBytesAttemptOverride = (attempt, url) =>
    {
        byteAttempts++;
        throw new HttpStatusException(404);
    };
    bool byteThrew = false;
    try { await Data.FetchBytesAsync("https://example.test/tile404"); }
    catch (HttpStatusException) { byteThrew = true; }
    Check(byteThrew && byteAttempts == 1, "bytes 404 immediate");
    byteAttempts = 0;
    Data.FetchBytesAttemptOverride = (attempt, url) =>
    {
        byteAttempts++;
        if (byteAttempts < 3) throw new HttpStatusException(500);
        return Task.FromResult(new byte[] { 1, 2, 3 });
    };
    var bytes = await Data.FetchBytesAsync("https://example.test/tile500");
    Check(bytes.Length == 3 && byteAttempts == 3, "bytes 500 retried");
    Data.FetchBytesAttemptOverride = null;

    // ----------------------------------------
    // 📰 ニュース: RSS・要約・選択
    // ----------------------------------------
    const string rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
        <item><title>関東で震度4の地震</title><link>https://example.test/1</link>
          <description>&lt;p&gt;地震の概要&lt;/p&gt;</description>
          <pubDate>Sat, 28 Nov 2026 10:00:00 +0900</pubDate></item>
        <item><title>通常のニュース</title><link>https://example.test/2</link>
          <description>ふつうの本文</description>
          <pubDate>Sat, 28 Nov 2026 09:00:00 +0900</pubDate></item>
        <item><title>リンク無し</title><description>捨てられる</description></item>
        <item><title>重複リンク</title><link>https://example.test/2</link>
          <description>重複</description></item>
        </channel></rss>
        """;
    var rssItems = Data.ParseRss(rss);
    Check(rssItems.Count == 3 && rssItems[0].Title == "関東で震度4の地震", "rss parse");
    Check(rssItems[0].Summary == "地震の概要", "rss summary strips html");
    Check(rssItems[0].Published is not null && rssItems[0].Published!.Value.Hour == 10, "rss pubdate jst");
    var selected = Data.SelectNewsItems(rssItems);
    Check(selected.Count == 2 && selected[0].Link == "https://example.test/1",
        "news select dedupe + disaster first");
    Check(Data.IsDisasterNews("大雨警報が発表", "") && !Data.IsDisasterNews("通常の話題", "本文"),
        "news disaster keywords");
    Check(Data.DisasterKeywords.Length == 16, "news disaster keyword count");
    Check(Data.SummarizeText("<p>abc</p>  def") == "abc def", "summarize collapses whitespace");
    string longText = Data.SummarizeText(new string('あ', 100));
    Check(longText.Length == 81 && longText.EndsWith("…"), "summarize truncates at 80");

    Console.WriteLine($"--- part 2 done ({passed}) ---");

    // ----------------------------------------
    // 🗾 都道府県マスタ
    // ----------------------------------------
    Check(Data.Prefectures.Length == 47, "prefectures count 47");
    Check(Data.PrefNameByCode("100000") == "群馬県" && Data.PrefNameByCode("110000") == "埼玉県"
        && Data.PrefNameByCode("471000") == "沖縄県", "pref name by code");
    Check(Data.PrefNameByCode("999999") is null, "pref name unknown code");

    // ----------------------------------------
    // 🩺 診断の整形 / 🔔 通知判定
    // ----------------------------------------
    var diagText = Data.FormatDiag(new List<Data.DiagResult>
    {
        new("気象庁 予報", true, "", 123),
        new("A2S", false, "未設定（地震系・警報・台風の情報は表示されません）", 0),
    });
    Check(diagText.Contains("○ 接続OK（123ms）: 気象庁 予報")
        && diagText.Contains("× 失敗（未設定（地震系・警報・台風の情報は表示されません））: A2S"),
        "diag format");
    var freshNow = Data.NowInTokyo();
    Check(Data.IsFreshEvent(freshNow.AddMinutes(-30), freshNow), "fresh 30min ago");
    Check(!Data.IsFreshEvent(freshNow.AddSeconds(-3601), freshNow), "not fresh over 1h");
    Check(Data.IsFreshEvent(freshNow.AddSeconds(299), freshNow)
        && !Data.IsFreshEvent(freshNow.AddSeconds(301), freshNow), "fresh future tolerance 300s");

    var quakeCand = Data.QuakeCandidates(new List<Quake> { merged })[0];
    Check(quakeCand.Id == "quake:2026/11/19 06:41:00:群馬県南部", "quake candidate id");
    Check(Data.ShouldNotify(quakeCand, 30, false) && !Data.ShouldNotify(quakeCand, 45, false),
        "should notify quake by scale");
    Check(Data.ShouldNotify(new Data.NotifyCandidate("tsunami", "tsunami:x", "t", "b", 0, "danger"),
        45, false), "should notify tsunami always");
    var advisoryCand = new Data.NotifyCandidate("warning", "warning:x:0", "t", "b", 0, "advisory");
    Check(!Data.ShouldNotify(advisoryCand, 30, false) && Data.ShouldNotify(advisoryCand, 30, true),
        "should notify advisory only when enabled");
    Check(Data.ShouldNotify(new Data.NotifyCandidate("warning", "w", "t", "b", 0, "warning"), 30, false),
        "should notify warning grade");

    // EEW の再通知は震度上昇時のみ
    var notifiedSet = new HashSet<string>();
    Check(Data.ShouldNotifyEew(eew, notifiedSet), "eew notify first");
    notifiedSet.Add(Data.EewNotifyId(eew));
    Check(!Data.ShouldNotifyEew(eew, notifiedSet), "eew no re-notify same scale");
    var eewStronger = new Data.EewInfo
    {
        OriginTime = eew.OriginTime, EstMaxScale = 50, IsWarn = true,
    };
    Check(Data.ShouldNotifyEew(eewStronger, notifiedSet), "eew re-notify on scale increase");
    var legacySet = new HashSet<string> { $"eew:{eew.OriginTime}" };
    Check(Data.ShouldNotifyEew(eew, legacySet), "eew legacy bare id counts as scale 0");
    var eewCancel = new Data.EewInfo { OriginTime = eew.OriginTime, IsCancel = true };
    Check(!Data.ShouldNotifyEew(eewCancel, new HashSet<string>()), "eew cancel not notified if original unseen");
    Check(Data.ShouldNotifyEew(eewCancel, notifiedSet), "eew cancel notified if original seen");
    Check(Data.EewNotifyId(eewCancel) == $"eew:{eew.OriginTime}:cancel", "eew cancel id");
    Check(Data.EewCandidate(eew, new[] { Data.Takasaki }, eewNow)!.Title == "🚨 緊急地震速報（警報）",
        "eew candidate title");

    // 警報候補: ID はコード列の Java ハッシュ (報告日時は含めない)
    Check(Data.JavaHashHex("abc") == "17862", "java hash hex abc");
    Check(Data.JavaHashHex("") == "0", "java hash hex empty");
    var warnInfo = new Data.WarningInfo { ReportDatetime = "2026/11/17 11:00:00" };
    warnInfo.Codes.Add("14"); warnInfo.Names.Add("雷注意報");
    warnInfo.Codes.Add("06"); warnInfo.Names.Add("大雪警報");
    warnInfo.MaxSeverity = 3;
    var warnCand = Data.WarningCandidate(Data.Takasaki, warnInfo);
    Check(warnCand is not null && warnCand.Grade == "warning"
        && warnCand.Id == $"warning:高崎:{Data.JavaHashHex("06,14")}", "warning candidate id and grade");
    var warnInfoLater = new Data.WarningInfo { ReportDatetime = "2026/11/17 12:00:00" };
    warnInfoLater.Codes.AddRange(warnInfo.Codes); warnInfoLater.Names.AddRange(warnInfo.Names);
    warnInfoLater.MaxSeverity = 3;
    Check(Data.WarningCandidate(Data.Takasaki, warnInfoLater)!.Id == warnCand!.Id,
        "warning candidate id ignores report datetime");
    Check(Data.WarningCandidate(Data.Takasaki, new Data.WarningInfo()) is null,
        "warning candidate null when empty");
    var specialInfo = new Data.WarningInfo { MaxSeverity = 4 };
    specialInfo.Codes.Add("01"); specialInfo.Names.Add("大雨特別警報");
    Check(Data.WarningCandidate(Data.Takasaki, specialInfo)!.Grade == "special", "warning grade special");

    var manyIds = Enumerable.Range(0, 305).Select(i => $"id{i}").ToList();
    var pruned = Data.PruneNotifiedIds(manyIds);
    Check(pruned.Count == 300 && pruned[0] == "id5" && pruned[^1] == "id304", "prune notified ids cap 300");

    // ----------------------------------------
    // 📋 ブリーフィング: まとめ導出とサーバーモード
    // ----------------------------------------
    var sumSections = new List<Section>
    {
        new("🌤 高崎の天気", "⚠ 発表中： 大雨警報、雷注意報（2026/11/28 10:00発表）\n今日: 晴れ", "warn"),
        new("🌤 大宮の天気", "⚠ 発表中： 雷注意報（2026/11/28 10:00発表）\n今日: くもり", "warn"),
        new("🌊 津波情報", "津波警報が発表されています", "danger"),
        new("🌋 地震情報", "🔴 群馬県南部\n詳細", "info"),
    };
    var summary = Data.Summarize(sumSections);
    Check(summary.Title == "📌 現在のまとめ" && summary.Level == "danger", "summary title and level");
    Check(summary.Body.Contains("大雨警報") && summary.Body.Split('、').Length >= 2
        && summary.Body.Contains("雷注意報"), "summary collects warnings");
    Check(summary.Body.IndexOf("雷注意報") == summary.Body.LastIndexOf("雷注意報"),
        "summary dedupes warning names");
    Check(summary.Body.Contains("津波: 警報・注意報あり"), "summary tsunami danger");
    Check(summary.Body.Contains("最新の地震: 🔴 群馬県南部"), "summary latest quake");
    var sumWatch = Data.Summarize(new List<Section> { new("🌊 津波情報", "注意報", "warn") });
    Check(sumWatch.Body.Contains("津波: 注意報あり") && sumWatch.Level == "warn", "summary tsunami watch");
    var sumOk = Data.Summarize(new List<Section> { new("🌊 津波情報", "なし", "ok") });
    Check(sumOk.Body.Contains("津波: なし"), "summary tsunami none");
    var sumEmpty = Data.Summarize(new List<Section>());
    Check(sumEmpty.Body.Contains("特に注意が必要な情報はありません"), "summary empty");

    // サーバーモード: severity → level 変換と行動ガイド付加
    Data.FetchCoreOverride = (url, json) => Task.FromResult("""
        {"sections":[
          {"title":"🌋 地震情報","body":"本文1","severity":"CRITICAL","action_guide":"避難してください"},
          {"title":"🌤 天気","body":"本文2","severity":"HIGH"},
          {"title":"🌊 津波","body":"本文3","severity":"MEDIUM"},
          {"title":"その他","body":"本文4","severity":"LOW"}
        ]}
        """);
    var serverSections = await Data.FetchServerBriefingAsync();
    Check(serverSections.Count == 4 && serverSections[0].Level == "danger"
        && serverSections[0].Body.Contains("▶ 行動ガイド： 避難してください"), "server briefing critical");
    Check(serverSections[1].Level == "warn" && serverSections[2].Level == "warn"
        && serverSections[3].Level == "info", "server briefing severity mapping");
    Data.FetchCoreOverride = null;
    Data.ClearResponseCache();

    // ----------------------------------------
    // ⚙️ 設定・キャッシュ (実ファイルがある場合は触らない)
    // ----------------------------------------
    if (!File.Exists(SettingsStore.CachePath))
    {
        SettingsStore.SaveCache(new List<Section> { new("t", "正常な本文", "info") });
        var cache = SettingsStore.LoadCache();
        Check(cache is not null && cache.Sections.Count == 1, "briefing cache roundtrip");
        File.Delete(SettingsStore.CachePath);
        SettingsStore.SaveCache(new List<Section> { new("t", "取得できませんでした", "warn") });
        Check(!File.Exists(SettingsStore.CachePath), "briefing cache refuses error sections");
        SettingsStore.SaveCache(new List<Section> { new("取得エラー", "本文", "warn") });
        Check(!File.Exists(SettingsStore.CachePath), "briefing cache refuses error title");
    }

    Console.WriteLine($"--- part 3 done ({passed}) ---");

    // ----------------------------------------
    // 📡 A.A.S: 検証・変換・保持・同期
    // ----------------------------------------
    const string a2sQuakeJson = """{"kind":"quake","key":"551:2026/11/19 06:41:00|@36.3,139.0","source":"p2p","sources":["p2p"],"receivedAt":"2026-11-19T06:42:05.000Z","updatedAt":"2026-11-19T06:42:05.000Z","telegramId":"q1","time":"2026/11/19 06:41:00","announcedTime":"2026/11/19 06:42:00","originTime":null,"hypocenter":"群馬県南部","magnitude":4.8,"depthKm":40,"maxScale":40,"tsunamiKind":"None","tsunamiText":null,"foreign":false,"issueType":"DetailScale","issueCorrect":"None","lat":36.3,"lon":139.0,"topPoint":"高崎市高松町","topPointIsArea":false,"prefScales":[{"pref":"群馬県","scale":40},{"pref":"埼玉県","scale":30}],"points":[{"pref":"群馬県","name":"高崎市高松町","scale":40,"isArea":false},{"pref":"埼玉県","name":"さいたま市大宮区天沼町","scale":30,"isArea":false}]}""";
    const string a2sTsunamiJson = """{"kind":"tsunami","key":"552:synthetic-552-001","source":"p2p","sources":["p2p"],"receivedAt":"2026-11-26T00:01:05.000Z","updatedAt":"2026-11-26T00:01:05.000Z","time":"2026/11/26 09:01:00","cancelled":false,"grade":"Warning","areas":[{"name":"宮城県沿岸","grade":"Warning","maxHeightLabel":"3m","maxHeightValue":3,"arrivalLabel":"09:30","firstHeightCondition":null},{"name":"福島県沿岸","grade":"Watch","maxHeightLabel":"1m","maxHeightValue":1,"arrivalLabel":null,"firstHeightCondition":"第1波の到達を確認"}]}""";
    string WarnJson(string areaCode, string areaName, string warningsInner, bool released = false)
        => "{\"kind\":\"warning\",\"key\":\"warning:100000:" + areaCode
            + "\",\"updatedAt\":\"2026-11-17T02:00:00.000Z\",\"officeCode\":\"100000\",\"areaCode\":\""
            + areaCode + "\",\"areaName\":\"" + areaName
            + "\",\"time\":\"2026/11/17 11:00:00\",\"released\":" + (released ? "true" : "false")
            + ",\"warnings\":[" + warningsInner + "]}";
    string TcJson(string linesInner)
        => "{\"kind\":\"typhoon\",\"key\":\"typhoon:T2621\",\"updatedAt\":\"2026-11-17T02:00:00.000Z\",\"lines\":["
            + linesInner + "]}";
    var wnEv = Data.ParseA2sEvent(JsonNode.Parse(
        WarnJson("100010", "南部", "{\"code\":\"14\",\"name\":\"雷注意報\",\"status\":\"発表\",\"severity\":2}")));
    var tcEv = Data.ParseA2sEvent(JsonNode.Parse(TcJson("\"・台風第21号\"")));

    var a2sNow = Data.NowInTokyo();
    string a2sOrigin = Fmt(a2sNow.AddSeconds(-30));
    string a2sAnnounced = Fmt(a2sNow.AddSeconds(-20));
    string a2sArrival = Fmt(a2sNow.AddSeconds(40));
    const string a2sEewTemplate = """{"kind":"eew","key":"eew-ORIGIN@36.0,139.0","baseKey":"eew-ORIGIN@36.0,139.0","source":"wolfx-ws","sources":["wolfx-ws"],"receivedAt":"2026-11-26T00:00:10.000Z","updatedAt":"2026-11-26T00:00:20.000Z","time":"ANNOUNCED","originTime":"ORIGIN","cancel":false,"isWarn":true,"isFinal":true,"serial":3,"eventId":"20261126000123","isAssumption":false,"hypocenter":"群馬県南部","estScale":45,"estMagnitude":5.2,"prefs":["群馬県","埼玉県"],"lat":36.0,"lon":139.0,"depthKm":10,"areaArrivals":[{"pref":"群馬県","arrivalTime":"ARRIVAL"}],"areaScales":[{"pref":"群馬県","name":"群馬県南部","scale":45,"scaleTo":50},{"pref":"埼玉県","name":"埼玉県南部","scale":40,"scaleTo":99}]}""";
    string a2sEewJson = a2sEewTemplate.Replace("ORIGIN", a2sOrigin)
        .Replace("ANNOUNCED", a2sAnnounced).Replace("ARRIVAL", a2sArrival);

    try
    {
        // 設定式の有効化
        Data.A2sEnabled = false;
        Data.A2sBaseUrl = "";
        Data.A2sToken = "";
        Data.ClearA2sState();
        Check(!Data.A2sConfigured, "a2s disabled by default");
        Data.A2sBaseUrl = "https://a2s.example.test";
        Data.A2sToken = "test-token";
        Check(!Data.A2sConfigured, "a2s not configured while disabled");
        Data.A2sEnabled = true;
        Check(Data.A2sConfigured, "a2s configured with url and token");
        Data.A2sToken = "";
        Check(!Data.A2sConfigured, "a2s not configured without token");
        Data.A2sToken = "test-token";

        // 検証
        Check(Data.ParseA2sEvent(null) is null, "a2s parse null");
        Check(Data.ParseA2sEvent(JsonNode.Parse("""{"kind":"quake"}""")) is null, "a2s parse missing key");
        Check(Data.ParseA2sEvent(JsonNode.Parse("""{"kind":"unknown","key":"x"}""")) is null, "a2s parse unknown kind");
        Check(Data.ParseA2sEvent(JsonNode.Parse("""{"kind":"quake","key":"k","time":"2026/11/19 06:41:00","hypocenter":"x","maxScale":40}""")) is null,
            "a2s parse quake missing points");
        Check(Data.ParseA2sEvent(JsonNode.Parse("""{"kind":"eew","key":"k","originTime":"2026/11/19 06:41:00","estScale":40,"isWarn":true,"cancel":false}""")) is null,
            "a2s parse eew missing baseKey");
        var a2sMixed = Data.ParseA2sEvents(JsonNode.Parse("{\"events\":[" + a2sQuakeJson + ",{\"kind\":\"bad\"}," + a2sTsunamiJson + "]}"));
        Check(a2sMixed.Count == 2 && a2sMixed[0].Kind == "quake" && a2sMixed[1].Kind == "tsunami",
            "a2s parse events filters invalid");

        // 地震の変換
        var a2sQuakeEv = Data.ParseA2sEvent(JsonNode.Parse(a2sQuakeJson));
        Check(a2sQuakeEv is not null && a2sQuakeEv.Key == "551:2026/11/19 06:41:00|@36.3,139.0", "a2s quake envelope");
        var a2sQuake = Data.A2sQuakeToQuake(a2sQuakeEv!);
        Check(a2sQuake is not null && a2sQuake.Place == "群馬県南部" && a2sQuake.MaxScale == 40
            && a2sQuake.Time == "2026/11/19 06:41:00", "a2s quake converted");
        Check(a2sQuake!.Detail.Contains("M4.8") && a2sQuake.Detail.Contains("深さ約40km")
            && a2sQuake.Detail.Contains("群馬県内の最大震度 4"), "a2s quake detail");
        Check(Data.QuakeCandidates(new List<Quake> { a2sQuake })[0].Id == "quake:2026/11/19 06:41:00:群馬県南部",
            "a2s quake notify id");
        var a2sDepthNull = JsonNode.Parse(a2sQuakeJson)!;
        a2sDepthNull["depthKm"] = null;
        Check(Data.A2sQuakeToQuake(Data.ParseA2sEvent(a2sDepthNull)!)!.Detail.Contains("深さ不明"),
            "a2s quake depth null is unknown");
        var a2sDepth0 = JsonNode.Parse(a2sQuakeJson)!;
        a2sDepth0["depthKm"] = 0;
        Check(Data.A2sQuakeToQuake(Data.ParseA2sEvent(a2sDepth0)!)!.Detail.Contains("深さごく浅い"),
            "a2s quake depth zero is shallow");
        var a2sForeign = JsonNode.Parse(a2sQuakeJson)!;
        a2sForeign["foreign"] = true;
        a2sForeign["originTime"] = "2026/11/19 06:35:00";
        a2sForeign["time"] = "2026/11/19 06:50:00";
        a2sForeign["tsunamiText"] = "太平洋の広域に津波発生の可能性があります";
        var a2sForeignQuake = Data.A2sQuakeToQuake(Data.ParseA2sEvent(a2sForeign)!);
        Check(a2sForeignQuake is not null && a2sForeignQuake.Time == "2026/11/19 06:35:00",
            "a2s foreign quake uses originTime");
        Check(a2sForeignQuake!.Detail.Contains("太平洋の広域に津波発生の可能性があります"),
            "a2s foreign quake tsunamiText shown");

        // EEW の変換
        var a2sEewEv = Data.ParseA2sEvent(JsonNode.Parse(a2sEewJson));
        var a2sEew = Data.A2sEewToInfo(a2sEewEv!);
        Check(a2sEew is not null && a2sEew.IsWarn && !a2sEew.IsCancel && a2sEew.Serial == 3
            && a2sEew.EventId == "20261126000123", "a2s eew converted flags");
        Check(a2sEew!.IsFinal, "a2s eew isFinal taken");
        Check(Data.EewBaseBody(a2sEew).Contains("（最終報）"), "a2s eew final label in body");
        Check(a2sEew.EstMaxScale == 45 && Math.Abs(a2sEew.Mag - 5.2) < 1e-9 && a2sEew.Depth == 10,
            "a2s eew converted estimates");
        Check(a2sEew.Areas.Count == 2 && a2sEew.Areas[0].ScaleFrom == 45 && a2sEew.Areas[0].ScaleTo == 50
            && a2sEew.Areas[0].ArrivalTime is not null && a2sEew.Areas[1].ScaleTo == 99,
            "a2s eew areas with arrivals and scaleTo");
        Check(Data.EewIdentityKey(a2sEew) == "eew-" + a2sOrigin + "@36.0,139.0",
            "a2s eew identity key matches baseKey");
        var a2sLocalArea = Data.SelectEewArea(a2sEew, Data.Takasaki);
        Check(a2sLocalArea is not null && a2sLocalArea.Name == "群馬県南部" && a2sLocalArea.ScaleFrom == 45,
            "a2s eew local forecast derived");
        var a2sLocalArrival = Data.EewArrival(a2sEew, Data.Takasaki);
        Check(a2sLocalArrival is not null && !a2sLocalArrival.Value.Estimated
            && Math.Abs((a2sLocalArrival.Value.Arrival - a2sNow).TotalSeconds - 40) < 3,
            "a2s eew local arrival derived");
        var a2sEewCancelNode = JsonNode.Parse(a2sEewJson)!;
        a2sEewCancelNode["cancel"] = true;
        Check(Data.A2sEewToInfo(Data.ParseA2sEvent(a2sEewCancelNode)!)!.IsCancel, "a2s eew cancel");

        // 津波の変換
        var a2sTsunamiEv = Data.ParseA2sEvent(JsonNode.Parse(a2sTsunamiJson));
        var a2sTsunamiSec = Data.A2sTsunamiSection(a2sTsunamiEv!);
        Check(a2sTsunamiSec is not null && a2sTsunamiSec.Level == "danger"
            && a2sTsunamiSec.Body.Contains("津波警報") && a2sTsunamiSec.Body.Contains("宮城県沿岸")
            && a2sTsunamiSec.Body.Contains("予想 3m") && a2sTsunamiSec.Body.Contains("第1波到達予想 09:30")
            && a2sTsunamiSec.Body.Contains("第1波の到達を確認"), "a2s tsunami section");
        var a2sTsunamiCand = Data.A2sTsunamiCandidate(a2sTsunamiEv!);
        Check(a2sTsunamiCand is not null && a2sTsunamiCand.Id == "tsunami:synthetic-552-001",
            "a2s tsunami notify id");
        var a2sTsunamiVal = JsonNode.Parse(a2sTsunamiJson)!;
        ((JsonArray)a2sTsunamiVal["areas"]!)[0]!["maxHeightLabel"] = null;
        Check(Data.A2sTsunamiSection(Data.ParseA2sEvent(a2sTsunamiVal)!)!.Body.Contains("予想 3m"),
            "a2s tsunami maxHeightValue fallback label");

        // 無効時の不変
        Data.A2sEnabled = false;
        Data.ClearA2sState();
        Check(Data.NoteA2sEvent(a2sQuakeEv!, replay: false) == Data.A2sAcceptResult.Duplicate,
            "a2s note ignored while disabled");
        Check(Data.A2sQuakesSnapshot().Count == 0, "a2s no state while disabled");
        Check((await Data.QuakeItemsAsync(10)).Count == 0, "quake items empty while disabled");
        Check(await Data.EewInfoAsync() is null, "eew info empty while disabled");
        Check((await Data.TsunamiSectionAsync()).Level == "ok", "tsunami none while disabled");
        Check(Data.A2sCurrentTsunamiCandidate() is null && Data.A2sCurrentTsunamiSection() is null,
            "a2s tsunami empty while disabled");
        Check(Data.NoteA2sEvent(wnEv!, replay: false) == Data.A2sAcceptResult.Duplicate,
            "a2s warning note ignored while disabled");
        Check(Data.A2sWarningInfoFor("100000", "100010").Names.Count == 0,
            "a2s warning empty while disabled");
        Check(await Data.TyphoonSectionAsync() is null, "typhoon none while disabled");
        Check((await Data.FetchA2sEventsAsync()).Count == 0, "a2s rest empty while not configured");
        Data.A2sEnabled = true;

        // 重複判定 (key + updatedAt)
        Data.ClearA2sState();
        Check(Data.NoteA2sEvent(a2sQuakeEv!, replay: false) == Data.A2sAcceptResult.New, "a2s note new");
        Check(Data.NoteA2sEvent(a2sQuakeEv!, replay: false) == Data.A2sAcceptResult.Duplicate, "a2s note duplicate");
        var a2sQuakeUpd = JsonNode.Parse(a2sQuakeJson)!;
        a2sQuakeUpd["updatedAt"] = "2026-11-19T06:43:05.000Z";
        a2sQuakeUpd["maxScale"] = 45;
        Check(Data.NoteA2sEvent(Data.ParseA2sEvent(a2sQuakeUpd)!, replay: false) == Data.A2sAcceptResult.Updated,
            "a2s note updated");
        Check(Data.A2sQuakesSnapshot().Count == 1 && Data.A2sQuakesSnapshot()[0].MaxScale == 45,
            "a2s quake state updated in place");
        var a2sQuakeStale = JsonNode.Parse(a2sQuakeJson)!;
        a2sQuakeStale["updatedAt"] = "2026-11-19T06:42:30.000Z";
        Check(Data.NoteA2sEvent(Data.ParseA2sEvent(a2sQuakeStale)!, replay: false) == Data.A2sAcceptResult.Duplicate,
            "a2s note stale updatedAt duplicate");
        var a2sQuake2 = JsonNode.Parse(a2sQuakeJson)!;
        a2sQuake2["key"] = "551:2026/11/19 07:41:00|@36.3,139.0";
        a2sQuake2["time"] = "2026/11/19 07:41:00";
        a2sQuake2["updatedAt"] = "2026-11-19T07:42:05.000Z";
        Check(Data.NoteA2sEvent(Data.ParseA2sEvent(a2sQuake2)!, replay: false) == Data.A2sAcceptResult.New,
            "a2s note second quake");
        var a2sItems = await Data.QuakeItemsAsync(10);
        Check(a2sItems.Count == 2 && a2sItems[0].Time == "2026/11/19 07:41:00",
            "quake items from a2s sorted desc");
        Check((await Data.QuakeItemsAsync(1)).Count == 1, "quake items respects limit");

        // EEW は保持分だけから返る (鮮度切れは返らない)
        Data.ClearA2sState();
        Check(Data.NoteA2sEvent(a2sEewEv!, replay: false) == Data.A2sAcceptResult.New, "a2s note eew");
        var a2sInfoAsync = await Data.EewInfoAsync();
        Check(a2sInfoAsync is not null && a2sInfoAsync.Serial == 3, "eew info from a2s state");
        var a2sSecAsync = await Data.EewSectionAsync();
        Check(a2sSecAsync is not null && a2sSecAsync.Title == "🚨 緊急地震速報（警報）", "eew section from a2s state");
        string a2sOldOrigin = Fmt(a2sNow.AddMinutes(-20));
        string a2sOldAnnounced = Fmt(a2sNow.AddMinutes(-19));
        var a2sOldEew = JsonNode.Parse(a2sEewJson)!;
        a2sOldEew["key"] = "eew-old";
        a2sOldEew["baseKey"] = "eew-old";
        a2sOldEew["originTime"] = a2sOldOrigin;
        a2sOldEew["time"] = a2sOldAnnounced;
        a2sOldEew["updatedAt"] = "2026-11-26T00:02:20.000Z";
        Data.ClearA2sState();
        Check(Data.NoteA2sEvent(Data.ParseA2sEvent(a2sOldEew)!, replay: false) == Data.A2sAcceptResult.New,
            "a2s note old eew");
        Data.A2sHttpGetOverride = (url, token) => Task.FromResult("{\"events\":[]}");
        Check(await Data.EewInfoAsync() is null, "eew info expired not returned");
        Data.A2sHttpGetOverride = null;

        // 津波は保持分だけから返る
        Check(Data.NoteA2sEvent(a2sTsunamiEv!, replay: false) == Data.A2sAcceptResult.New, "a2s note tsunami");
        Check((await Data.TsunamiSectionAsync()).Body.Contains("宮城県沿岸"), "tsunami section from a2s state");
        Check(Data.A2sCurrentTsunamiCandidate()?.Id == "tsunami:synthetic-552-001", "a2s tsunami candidate held");

        // 警報は区域コードで照合する
        Data.ClearA2sState();
        Check(Data.NoteA2sEvent(wnEv!, replay: false) == Data.A2sAcceptResult.New, "a2s note warning");
        var wnOther = JsonNode.Parse(WarnJson("100020", "北部",
            "{\"code\":\"06\",\"name\":\"大雪警報\",\"status\":\"発表\",\"severity\":3}"))!;
        wnOther["updatedAt"] = "2026-11-17T02:01:00.000Z";
        Check(Data.NoteA2sEvent(Data.ParseA2sEvent(wnOther)!, replay: false) == Data.A2sAcceptResult.New,
            "a2s note warning other area");
        var wnHeld = Data.A2sWarningInfoFor("100000", "100010");
        Check(wnHeld.Names.Contains("雷注意報") && !wnHeld.Names.Contains("大雪警報"),
            "a2s warning matched by area code");
        Check(Data.A2sWarningInfoFor("100000", "100030").Names.Count == 0, "a2s warning unmatched code empty");
        Check(Data.A2sWarningInfoFor("110000", "100010").Names.Count == 0, "a2s warning other office empty");
        var wnWide = Data.A2sWarningInfoFor("100000", "");
        Check(wnWide.Names.Contains("大雪警報") && wnWide.Names.Contains("雷注意報"),
            "a2s warning prefecture-wide when code empty");
        Check(Data.WarningCandidate(Data.Takasaki, wnHeld)?.Grade == "advisory",
            "a2s warning candidate grade advisory for severity 2");
        var wnRel = JsonNode.Parse(WarnJson("100010", "南部", "", released: true))!;
        wnRel["updatedAt"] = "2026-11-17T03:00:00.000Z";
        Check(Data.NoteA2sEvent(Data.ParseA2sEvent(wnRel)!, replay: false) == Data.A2sAcceptResult.Updated,
            "a2s note warning released");
        Check(Data.A2sWarningInfoFor("100000", "100010").Names.Count == 0, "a2s warning released removed");
        Check(Data.A2sWarningInfoFor("100000", "100020").Names.Contains("大雪警報"),
            "a2s warning release keeps other areas");

        // 台風とスナップショット間引き
        Check(Data.NoteA2sEvent(tcEv!, replay: false) == Data.A2sAcceptResult.New, "a2s note typhoon");
        var tcSec = await Data.TyphoonSectionAsync();
        Check(tcSec is not null && tcSec.Body.Contains("台風第21号"), "typhoon section from a2s state");
        Check(Data.A2sEventsUrl(null, "warning").Contains("limit=700")
            && Data.A2sEventsUrl(null, "warning").Contains("kind=warning"),
            "a2s warning events url kind and limit");
        Data.A2sHttpGetOverride = (url, token) => Task.FromResult("{\"events\":[]}");
        try
        {
            await Data.A2sSyncAsync(kind: "typhoon");
            Check(Data.A2sTyphoonsSnapshot().Count == 0, "a2s snapshot prune removes stale typhoon");
            Check(await Data.TyphoonSectionAsync() is null, "typhoon section gone after prune");
            await Data.A2sSyncAsync(kind: "warning");
            Check(Data.A2sWarningInfoFor("100000", "").Names.Count == 0, "a2s snapshot prune removes stale warnings");
        }
        finally { Data.A2sHttpGetOverride = null; }

        // REST: URL・トークン・初期同期 (再生扱い)・履歴回収
        Data.ClearA2sState();
        Data.A2sBaseUrl = "https://a2s.example.test/";
        Check(Data.A2sEventsUrl() == "https://a2s.example.test/v1/events?limit=100", "a2s events url");
        Check(Data.A2sEventsUrl("2026-11-26T00:00:00.000Z").Contains("&since=2026-11-26T00%3A00%3A00.000Z"),
            "a2s events url since encoded");
        string? a2sCapturedToken = null;
        Data.A2sHttpGetOverride = (url, token) =>
        {
            a2sCapturedToken = token;
            return Task.FromResult("{\"events\":[" + a2sQuakeJson + ",{\"kind\":\"bad\"}]}");
        };
        var a2sFetched = await Data.FetchA2sEventsAsync();
        Check(a2sFetched.Count == 1 && a2sCapturedToken == "test-token", "a2s rest fetch with token");
        int a2sSynced = await Data.A2sSyncAsync();
        Check(a2sSynced == 1 && Data.IsA2sReplaySuppressed("quake:2026/11/19 06:41:00:群馬県南部"),
            "a2s sync notes as replay");
        Data.ClearA2sState();
        var a2sHistory = await Data.QuakeItemsAsync(10);
        Check(a2sHistory.Count == 1 && a2sHistory[0].Place == "群馬県南部", "quake items recover history via rest");
        Data.A2sHttpGetOverride = null;

        // 再生は通知にならない。実況の続報 (震度上昇) は通知候補に現れる
        Data.ClearA2sState();
        Check(Data.NoteA2sEvent(a2sEewEv!, replay: true) == Data.A2sAcceptResult.New, "a2s note replay eew");
        var a2sReplayCandId = Data.EewCandidate(a2sEew!, new[] { Data.Takasaki }, Data.NowInTokyo())!.Id;
        Check(Data.IsA2sReplaySuppressed(a2sReplayCandId), "a2s replay candidate suppressed");
        try
        {
            Data.FetchCoreOverride = (url, acceptJson) => Task.FromException<string>(new HttpRequestException("all down"));
            Data.A2sHttpGetOverride = (url, token) => Task.FromException<string>(new HttpRequestException("a2s down"));
            Data.ClearResponseCache();
            var a2sCands1 = await Data.FetchNotifyCandidatesAsync();
            Check(!a2sCands1.Exists(c => c.Kind == "eew"), "a2s replay eew not in notify candidates");
            var a2sEewUpd = JsonNode.Parse(a2sEewJson)!;
            a2sEewUpd["updatedAt"] = "2026-11-26T00:01:20.000Z";
            a2sEewUpd["serial"] = 4;
            a2sEewUpd["estScale"] = 50;
            Check(Data.NoteA2sEvent(Data.ParseA2sEvent(a2sEewUpd)!, replay: false) == Data.A2sAcceptResult.Updated,
                "a2s note live eew update");
            Data.ClearResponseCache();
            var a2sCands2 = await Data.FetchNotifyCandidatesAsync();
            Check(a2sCands2.Exists(c => c.Kind == "eew" && c.MaxScale == 50),
                "a2s live eew update in notify candidates");
        }
        finally
        {
            Data.FetchCoreOverride = null;
            Data.A2sHttpGetOverride = null;
            Data.ClearResponseCache();
        }

        // ----------------------------------------
        // 📶 SSE: フレーム解析とストリームクライアント
        // ----------------------------------------
        var a2sParser = new A2sSseParser();
        var a2sFrames = new List<A2sSseFrame>();
        a2sFrames.AddRange(a2sParser.Push(": ping 2026-11-26T00:00:00Z\nevent: quake\nda"));
        a2sFrames.AddRange(a2sParser.Push("ta: " + a2sQuakeJson + "\n\nevent: ready\ndata: {}\n\n"));
        Check(a2sFrames.Count == 2 && a2sFrames[0].Type == "quake" && a2sFrames[1].Type == "ready",
            "sse parser frames across chunks");
        Check(A2sStreamClient.ParseFrameEvent(a2sFrames[0])?.Kind == "quake", "sse frame to event");
        Check(A2sStreamClient.ParseFrameEvent(new A2sSseFrame("warning",
            WarnJson("100010", "南部", "{\"code\":\"14\",\"name\":\"雷注意報\",\"status\":\"継続\",\"severity\":2}")))?.Kind == "warning",
            "sse warning frame to event");
        Check(A2sStreamClient.ParseFrameEvent(new A2sSseFrame("typhoon", TcJson("\"・台風第21号\"")))?.Kind == "typhoon",
            "sse typhoon frame to event");
        Check(A2sStreamClient.ParseFrameEvent(a2sFrames[1]) is null, "sse ready not an event");
        Check(A2sStreamClient.ParseFrameEvent(new A2sSseFrame("quake", "{broken")) is null,
            "sse broken json ignored");
        Check(A2sStreamClient.ReconnectDelayMs(0) == 5000 && A2sStreamClient.ReconnectDelayMs(1) == 10000
            && A2sStreamClient.ReconnectDelayMs(2) == 30000 && A2sStreamClient.ReconnectDelayMs(9) == 60000,
            "sse reconnect backoff 5-10-30-60");

        string a2sStreamText = ": connected\n"
            + "event: quake\ndata: " + a2sQuakeJson + "\n\n"
            + "event: ready\ndata: {}\n\n"
            + "event: eew\ndata: " + a2sEewJson + "\n\n"
            + "event: quake\ndata: {broken\n\n";
        var a2sReceived = new List<(Data.A2sEvent Ev, bool Replay)>();
        bool a2sReadyFired = false;
        int a2sConnects = 0;
        using var a2sCts = new CancellationTokenSource();
        var a2sClient = new A2sStreamClient("https://a2s.example.test", "test-token",
            (ev, replay) => { lock (a2sReceived) a2sReceived.Add((ev, replay)); },
            onReady: () => a2sReadyFired = true,
            transportFactory: () =>
            {
                int n = Interlocked.Increment(ref a2sConnects);
                return new FakeA2sTransport(n == 1 ? a2sStreamText : null);
            },
            delayOverride: _ => Task.CompletedTask);
        var a2sRunTask = a2sClient.RunAsync(a2sCts.Token);
        var a2sDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < a2sDeadline)
        {
            lock (a2sReceived) { if (a2sReceived.Count >= 2 && a2sReadyFired) break; }
            await Task.Delay(20);
        }
        lock (a2sReceived)
        {
            Check(a2sReceived.Count == 2 && a2sReceived[0].Ev.Kind == "quake" && a2sReceived[0].Replay
                && a2sReceived[1].Ev.Kind == "eew" && !a2sReceived[1].Replay,
                "sse stream replay flag before/after ready");
        }
        Check(a2sReadyFired, "sse stream ready callback");
        a2sCts.Cancel();
        bool a2sStopped = true;
        try { await a2sRunTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { a2sStopped = false; }
        Check(a2sStopped, "sse stream stops on cancel");
    }
    finally
    {
        Data.A2sEnabled = false;
        Data.A2sBaseUrl = "";
        Data.A2sToken = "";
        Data.A2sHttpGetOverride = null;
        Data.ClearA2sState();
    }

    Console.WriteLine($"ALL TESTS PASSED ({passed})");
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine($"TESTS FAILED after {passed} checks");
    Environment.Exit(1);
}

// A2S SSE のフェイク (実接続しない単体テスト用)。
// text があればその内容のストリームを返し、null のときはキャンセルまで
// 読み取りがブロックするストリームを返す (再接続後の常時接続の再現)
sealed class FakeA2sTransport : IA2sStreamTransport
{
    private readonly string? _text;

    public FakeA2sTransport(string? text) => _text = text;

    public Task<Stream> ConnectAsync(Uri uri, string? token, CancellationToken ct) =>
        Task.FromResult<Stream>(_text is not null
            ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_text))
            : new BlockingStream());

    public void Dispose() { }
}

// 読み取りがキャンセルまでブロックするストリーム (接続維持中の再現用)
sealed class BlockingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
