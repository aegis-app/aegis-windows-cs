using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AegisWin;

// ========================================
// 🗾 都道府県マスタ / 🌧️ レーダー / 📰 ニュース
// ========================================
// 📌 レーダーは気象庁ナウキャストを主、RainViewer を代替とする2段構え。
// 表示ズーム z8 に対して RainViewer は z7 タイルしか無いため、z7 タイルを
// 512px に拡大して敷く配置計算 (RainTileCoverage) が要る。
// 📌 ニュースは NHK RSS (cat0 + cat1) を統合し、災害関連を優先表示する。

public static partial class Data
{
    // ----------------------------------------
    // 🗾 都道府県マスタ (気象庁の予報区コード)
    // ----------------------------------------
    public static readonly (string Name, string Code)[] Prefectures =
    {
        ("北海道", "011000"), ("青森県", "020000"), ("岩手県", "030000"),
        ("宮城県", "040000"), ("秋田県", "050000"), ("山形県", "060000"),
        ("福島県", "070000"), ("茨城県", "080000"), ("栃木県", "090000"),
        ("群馬県", "100000"), ("埼玉県", "110000"), ("千葉県", "120000"),
        ("東京都", "130000"), ("神奈川県", "140000"), ("新潟県", "150000"),
        ("富山県", "160000"), ("石川県", "170000"), ("福井県", "180000"),
        ("山梨県", "190000"), ("長野県", "200000"), ("岐阜県", "210000"),
        ("静岡県", "220000"), ("愛知県", "230000"), ("三重県", "240000"),
        ("滋賀県", "250000"), ("京都府", "260000"), ("大阪府", "270000"),
        ("兵庫県", "280000"), ("奈良県", "290000"), ("和歌山県", "300000"),
        ("鳥取県", "310000"), ("島根県", "320000"), ("岡山県", "330000"),
        ("広島県", "340000"), ("山口県", "350000"), ("徳島県", "360000"),
        ("香川県", "370000"), ("愛媛県", "380000"), ("高知県", "390000"),
        ("福岡県", "400000"), ("佐賀県", "410000"), ("長崎県", "420000"),
        ("熊本県", "430000"), ("大分県", "440000"), ("宮崎県", "450000"),
        ("鹿児島県", "460100"), ("沖縄県", "471000"),
    };

    /// <summary>予報区コードから都道府県名を引く</summary>
    public static string? PrefNameByCode(string code)
    {
        foreach (var (name, c) in Prefectures)
            if (c == code) return name;
        return null;
    }

    /// <summary>都道府県の予報区域候補 (区域名, 区域コード) を気象庁予報から得る。
    /// 地点追加ダイアログの候補表示用</summary>
    public static async Task<List<(string Name, string Code)>> ForecastAreasAsync(string prefCode)
    {
        string json = await FetchAsync(
            $"https://www.jma.go.jp/bosai/forecast/data/forecast/{prefCode}.json", acceptJson: true);
        var root = ParseJson(json);
        var block = FirstBlockWith(root, "weathers");
        var areas = ArrOf(block, "areas");
        var result = new List<(string, string)>();
        if (areas is not null)
        {
            foreach (var a in areas)
            {
                string? name = StrOf(Child(a, "area"), "name");
                string? code = StrOf(Child(a, "area"), "code");
                if (name is not null && code is not null) result.Add((name, code));
            }
        }
        return result;
    }

    // ----------------------------------------
    // 🌧️ レーダー (気象庁ナウキャスト + RainViewer + 地理院タイル)
    // ----------------------------------------
    /// <summary>RainViewer タイルの描画サイズ (z7 タイルを z8 相当の 512px で描く)</summary>
    public const int RainTileDrawSize = 512;

    /// <summary>レーダーフレームのキャッシュ再利用上限 (10分)</summary>
    public static bool RadarFrameCacheReusable(DateTime frameTime, DateTime now)
        => (now - frameTime).TotalMinutes <= 10;

    /// <summary>気象庁ナウキャストのタイル URL</summary>
    public static string JmaTileUrl(string basetime, string validtime, int z, int x, int y)
        => $"https://www.jma.go.jp/bosai/jmatile/data/nowc/{basetime}/none/{validtime}/surf/hrpns/{z}/{x}/{y}.png";

    /// <summary>地理院タイル (標準地図) の URL</summary>
    public static string GsiTileUrl(int z, int x, int y)
        => $"https://cyberjapandata.gsi.go.jp/xyz/std/{z}/{x}/{y}.png";

    /// <summary>RainViewer のタイル URL</summary>
    public static string RainViewerTileUrl(string path, int z, int x, int y)
        => $"https://tilecache.rainviewer.com{path}/256/{z}/{x}/{y}/2/1_1.png";

    /// <summary>緯度経度からタイル座標 (XYZ) を計算する</summary>
    public static (int X, int Y) LatLonToTile(double lat, double lon, int z)
    {
        int n = 1 << z;
        int x = (int)Math.Floor((lon + 180.0) / 360.0 * n);
        double latRad = lat * Math.PI / 180.0;
        int y = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
        return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }

    /// <summary>z7 タイル1枚の配置 (タイル座標と、z8 3×3 キャンバス内の描画オフセット px)</summary>
    public sealed record RainTilePlacement(int TileX, int TileY, int OffsetX, int OffsetY);

    /// <summary>
    /// z8 の 3×3 タイル範囲 (中心 cx8, cy8) を覆う z7 タイルの配置を返す。
    /// z7 タイルは z8 の 2×2 を覆い、キャンバス上では 512px 四方になる。
    /// </summary>
    public static List<RainTilePlacement> RainTileCoverage(int cx8, int cy8)
    {
        int minX8 = cx8 - 1, minY8 = cy8 - 1;
        var result = new List<RainTilePlacement>();
        int startX7 = (int)Math.Floor(minX8 / 2.0), endX7 = (int)Math.Floor((cx8 + 1) / 2.0);
        int startY7 = (int)Math.Floor(minY8 / 2.0), endY7 = (int)Math.Floor((cy8 + 1) / 2.0);
        for (int tx = startX7; tx <= endX7; tx++)
            for (int ty = startY7; ty <= endY7; ty++)
                result.Add(new RainTilePlacement(tx, ty,
                    (tx * 2 - minX8) * 256, (ty * 2 - minY8) * 256));
        return result;
    }

    /// <summary>レーダーフレームの古さ表示。20分を超えたら遅延の注意を添える</summary>
    public static string RadarFrameAgeText(DateTime frameTime, DateTime now)
    {
        int minutes = Math.Max(0, (int)(now - frameTime).TotalMinutes);
        string text = $"約{minutes}分前の観測";
        if (minutes > 20) text += "（古い観測です。配信が遅れている可能性があります）";
        return text;
    }

    /// <summary>レーダーの出典注記</summary>
    public const string RadarNoteJma = "出典: 気象庁ナウキャスト（高解像度降水ナウキャスト） / 地理院タイル";
    public const string RadarNoteRainViewer = "出典: RainViewer / 地理院タイル";
    public const string RadarNoteFallback =
        "気象庁ナウキャストを取得できなかったため、RainViewer のレーダーを表示しています";

    /// <summary>気象庁ナウキャストの最新フレーム (basetime, validtime) を得る</summary>
    public static async Task<(string Basetime, string Validtime)> FetchJmaRadarFrameAsync()
    {
        string json = await FetchAsync(
            "https://www.jma.go.jp/bosai/jmatile/data/nowc/targetTimes_N1.json", acceptJson: true);
        var root = ParseJson(json);
        if (root is JsonArray arr && arr.Count > 0)
        {
            var last = arr[arr.Count - 1];
            string? b = StrOf(last, "basetime");
            string? v = StrOf(last, "validtime");
            if (b is not null && v is not null) return (b, v);
        }
        throw new InvalidOperationException("ナウキャストのフレーム情報が空でした");
    }

    /// <summary>RainViewer の最新フレーム (UNIX時刻, タイルパス) を得る</summary>
    public static async Task<(long Time, string Path)> FetchRainViewerFrameAsync()
    {
        string json = await FetchAsync("https://api.rainviewer.com/public/weather-maps.json", acceptJson: true);
        var root = ParseJson(json);
        var past = ArrOf(Child(root, "radar"), "past");
        if (past is not null && past.Count > 0)
        {
            var last = past[past.Count - 1];
            long t = (long)(NumOf(last, "time") ?? 0);
            string? path = StrOf(last, "path");
            if (path is not null) return (t, path);
        }
        throw new InvalidOperationException("RainViewer のフレーム情報が空でした");
    }

    // ----------------------------------------
    // 🖼️ バイナリ取得 (タイル用: 3回試行・20秒タイムアウト)
    // ----------------------------------------
    /// <summary>🧪 テスト用フック: バイト取得の試行単位の差し替え</summary>
    public static Func<int, string, Task<byte[]>>? FetchBytesAttemptOverride;

    private static readonly int[] BytesRetryDelaysMs = { 1000, 2000 };

    public static async Task<byte[]> FetchBytesAsync(string url)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= BytesRetryDelaysMs.Length; attempt++)
        {
            try
            {
                if (FetchBytesAttemptOverride is not null)
                    return await FetchBytesAttemptOverride(attempt, url);
                using var cts = new CancellationTokenSource(20000);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", $"AEGIS-Windows-CS/{Version}");
                using var res = await Http.SendAsync(req, cts.Token);
                if (!res.IsSuccessStatusCode)
                {
                    int code = (int)res.StatusCode;
                    // 429 以外の 4xx はリトライしない (存在しないタイル等)
                    if (code >= 400 && code < 500 && code != 429) throw new HttpStatusException(code);
                    throw new HttpStatusException(code);
                }
                return await res.Content.ReadAsByteArrayAsync(cts.Token);
            }
            catch (Exception e)
            {
                last = e;
                if (e is HttpStatusException hse && hse.StatusCode >= 400
                    && hse.StatusCode < 500 && hse.StatusCode != 429) throw;
                if (attempt == BytesRetryDelaysMs.Length) throw;
                await Task.Delay(BytesRetryDelaysMs[attempt]);
            }
        }
        throw last!;
    }

    // ----------------------------------------
    // 📰 ニュース (NHK RSS)
    // ----------------------------------------
    /// <summary>ニュース1件</summary>
    public sealed record NewsItem(string Title, string Link, string Summary, DateTime? Published);

    /// <summary>災害関連とみなすキーワード</summary>
    public static readonly string[] DisasterKeywords =
    {
        "地震", "津波", "台風", "大雨", "洪水", "土砂災害", "噴火", "火山",
        "避難", "警報", "豪雨", "浸水", "停電", "ミサイル", "Jアラート", "熱中症",
    };

    /// <summary>タイトルまたは要約に災害キーワードを含むか</summary>
    public static bool IsDisasterNews(string title, string summary)
        => DisasterKeywords.Any(k => title.Contains(k) || summary.Contains(k));

    /// <summary>HTML をプレーンテキスト化し、空白を畳んで maxLen 字に切り詰める</summary>
    public static string SummarizeText(string? html, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string text = Regex.Replace(html, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > maxLen ? text.Substring(0, maxLen) + "…" : text;
    }

    /// <summary>RSS (RSS 2.0) をパースする。タイトルとリンクが無い項目は捨てる</summary>
    public static List<NewsItem> ParseRss(string xml)
    {
        var result = new List<NewsItem>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception) { return result; }
        foreach (var item in doc.Descendants("item"))
        {
            string? title = item.Element("title")?.Value?.Trim();
            string? link = item.Element("link")?.Value?.Trim();
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link)) continue;
            string summary = SummarizeText(item.Element("description")?.Value);
            DateTime? published = null;
            string? pub = item.Element("pubDate")?.Value;
            if (pub is not null && DateTimeOffset.TryParse(pub,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dto))
                published = TimeZoneInfo.ConvertTime(dto, TokyoTz).DateTime;
            result.Add(new NewsItem(title, link, summary, published));
        }
        return result;
    }

    /// <summary>リンクで重複排除し、災害関連を先に並べて上限件数を返す</summary>
    public static List<NewsItem> SelectNewsItems(List<NewsItem> items, int max = 8)
    {
        var seen = new HashSet<string>();
        var unique = new List<NewsItem>();
        foreach (var item in items)
            if (seen.Add(item.Link)) unique.Add(item);
        return unique.OrderByDescending(i => IsDisasterNews(i.Title, i.Summary) ? 1 : 0)
            .Take(max).ToList();
    }

    /// <summary>NHK ニュース (cat0: 主要、cat1: 社会) を統合して取得する</summary>
    public static async Task<List<NewsItem>> FetchNewsAsync()
    {
        var all = new List<NewsItem>();
        foreach (string url in new[]
        {
            "https://www.nhk.or.jp/rss/news/cat0.xml",
            "https://www.nhk.or.jp/rss/news/cat1.xml",
        })
        {
            string xml = await FetchAsync(url);
            all.AddRange(ParseRss(xml));
        }
        return SelectNewsItems(all);
    }
}
