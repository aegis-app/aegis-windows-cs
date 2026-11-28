using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace AegisWin;

// ========================================
// 🧱 データ層の基盤 (時刻・地点・HTTP・JSON ヘルパー)
// ========================================
// 📌 Data は部分クラスで機能別ファイルに分割している:
//   Data.cs          … 本ファイル (基盤)
//   Data.Quake.cs    … 地震・津波の整形
//   Data.Eew.cs      … 緊急地震速報 (EEW) の整形と到達推定
//   Data.Weather.cs  … 天気予報 (気象庁 + Open-Meteo)
//   Data.Briefing.cs … ブリーフィング (まとめ) の構築
//   Data.Features.cs … レーダー・ニュース・都道府県マスタ
//   Data.DiagNotify.cs … 接続診断と通知候補
//   Data.A2s.cs      … A.A.S (Aegis Alert System) 連携
// GUI (MainForm) はこの層だけを呼ぶ。ロジックは GUI 非依存に保ち、
// AegisTest から単体テストできるようにしている。

/// <summary>対象地点 (緯度経度つき)。PrefCode は気象庁の都道府県コード、
/// AreaName は予報区名 (例: 群馬県「南部」)</summary>
public sealed record Location(string Name, double Lat, double Lon, string PrefCode, string AreaName);

/// <summary>表示セクション。Level は info | ok | warn | danger</summary>
public sealed record Section(string Title, string Body, string Level);

/// <summary>地震1件の表示用データ。Lat/Lon は震源座標 (0 = なし)、
/// Time は発生時刻文字列、Observation は観測点情報の整形済みテキスト</summary>
public sealed record Quake(string Place, string Detail, string Sev, int MaxScale,
    double Lat = 0, double Lon = 0, string Time = "", string Observation = "");

/// <summary>HTTP ステータスエラー。メッセージは "HTTP {code}" 形式に統一する</summary>
public sealed class HttpStatusException : Exception
{
    public int StatusCode { get; }
    public HttpStatusException(int statusCode) : base($"HTTP {statusCode}") => StatusCode = statusCode;
}

public static partial class Data
{
    /// <summary>アプリのバージョン</summary>
    public const string Version = "1.0.0";

    // ----------------------------------------
    // 🕐 日本時間 (Asia/Tokyo) の扱い
    // ----------------------------------------
    // 📌 タイムゾーン ID は OS で異なる (Linux/macOS: Asia/Tokyo、
    // Windows: Tokyo Standard Time)。両方を試し、どちらも無い環境では
    // UTC+9 固定のカスタムゾーンにフォールバックする。
    private static readonly TimeZoneInfo TokyoTz = ResolveTokyoTz();

    private static TimeZoneInfo ResolveTokyoTz()
    {
        foreach (string id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { /* 次の候補へ */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    }

    /// <summary>現在の日本時間</summary>
    public static DateTime NowInTokyo() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TokyoTz).DateTime;

    /// <summary>"yyyy/MM/dd HH:mm:ss" 形式の日本時間文字列をパースする。
    /// 先頭19文字だけを見るので、末尾に余分な文字があってもよい</summary>
    public static bool TryParseJstTime(string? s, out DateTime result)
    {
        result = default;
        if (string.IsNullOrEmpty(s) || s.Length < 19) return false;
        return DateTime.TryParseExact(s.AsSpan(0, 19), "yyyy/MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>経過時間の表示 (キャッシュ表示用)</summary>
    public static string FormatAgo(DateTimeOffset savedAt, DateTimeOffset now)
    {
        var span = now - savedAt;
        if (span.TotalSeconds < 60) return "たった今";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}時間前";
        return $"{(int)span.TotalDays}日前";
    }

    // ----------------------------------------
    // 📍 対象地点
    // ----------------------------------------
    public static readonly Location Takasaki = new("高崎", 36.3219, 139.0023, "100000", "南部");
    public static readonly Location Omiya = new("大宮", 35.9064, 139.6239, "110000", "南部");

    /// <summary>現在の対象地点一覧 (固定2地点 + ユーザー追加分)</summary>
    public static List<Location> Locations { get; private set; } = new() { Takasaki, Omiya };

    /// <summary>地点一覧を固定2地点に戻す (設定の再読み込み用)</summary>
    public static void ResetLocations() => Locations = new List<Location> { Takasaki, Omiya };

    /// <summary>地点座標の検証。戻り値は (エラー文, 警告文)。
    /// エラーがある場合は追加不可、警告は確認のうえ追加できる</summary>
    public static (string? Error, string? Warning) ValidateLocationCoords(double lat, double lon)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon)) return ("緯度・経度が数値ではありません", null);
        if (lat < -90 || lat > 90) return ("緯度は -90〜90 の範囲で入力してください", null);
        if (lon < -180 || lon > 180) return ("経度は -180〜180 の範囲で入力してください", null);
        // 🌏 日本域外は誤入力の可能性が高いため警告する (追加自体は可能)
        if (lat < 20 || lat > 46 || lon < 122 || lon > 154)
            return (null, "日本域外の座標です。このまま追加しますか？");
        return (null, null);
    }

    // ----------------------------------------
    // 🔢 数値の整形
    // ----------------------------------------
    /// <summary>小数第1位への四捨五入 (中点は切り上げ側 = AwayFromZero)</summary>
    public static double Round1(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    /// <summary>整数への四捨五入 (中点は切り上げ側 = AwayFromZero)</summary>
    public static long RoundToLong(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Java の double 文字列化に合わせた表記 (9.0 のように .0 を保つ)</summary>
    public static string NumStr(double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        return v.ToString(CultureInfo.InvariantCulture);
    }

    // ----------------------------------------
    // 🌐 HTTP 取得 (リトライ・キャッシュ・重複排除つき)
    // ----------------------------------------
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const int FetchTimeoutMs = 25000;
    private static readonly int[] RetryDelaysMs = { 1000, 3000 };
    private const int CacheSeconds = 30;
    private const int CacheCapacity = 128;

    private sealed record CacheEntry(string Body, DateTimeOffset At);
    private static readonly Dictionary<string, CacheEntry> ResponseCache = new();
    private static readonly Dictionary<string, Task<string>> InFlight = new();
    private static readonly object HttpLock = new();

    // 🧪 テスト用フック (本番では常に null)
    /// <summary>取得全体の差し替え (URL, acceptJson) → 本文</summary>
    public static Func<string, bool, Task<string>>? FetchCoreOverride;
    /// <summary>試行単位の差し替え (試行番号, URL, acceptJson) → 本文</summary>
    public static Func<int, string, bool, Task<string>>? FetchAttemptOverride;
    /// <summary>リトライ間隔の差し替え (遅延ミリ秒) → 完了タスク</summary>
    public static Func<int, Task>? RetryDelayOverride;

    /// <summary>レスポンスキャッシュを空にする (テスト・手動更新用)</summary>
    public static void ClearResponseCache()
    {
        lock (HttpLock) { ResponseCache.Clear(); }
    }

    /// <summary>
    /// URL の本文を取得する。30秒キャッシュと同一URLの同時取得の共有を行い、
    /// ネットワーク失敗・タイムアウト・HTTP 5xx のときだけ最大3回試行する
    /// (4xx はリトライしても結果が変わらないため即失敗)。
    /// </summary>
    public static Task<string> FetchAsync(string url, bool acceptJson = false)
    {
        if (FetchCoreOverride is not null) return FetchCoreOverride(url, acceptJson);

        lock (HttpLock)
        {
            if (ResponseCache.TryGetValue(url, out var hit)
                && (DateTimeOffset.UtcNow - hit.At).TotalSeconds < CacheSeconds)
                return Task.FromResult(hit.Body);
            if (InFlight.TryGetValue(url, out var running)) return running;
            var task = FetchWithRetryAsync(url, acceptJson);
            InFlight[url] = task;
            _ = task.ContinueWith(t =>
            {
                lock (HttpLock)
                {
                    InFlight.Remove(url);
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        if (ResponseCache.Count >= CacheCapacity) ResponseCache.Clear();
                        ResponseCache[url] = new CacheEntry(t.Result, DateTimeOffset.UtcNow);
                    }
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task<string> FetchWithRetryAsync(string url, bool acceptJson)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            try
            {
                if (FetchAttemptOverride is not null)
                    return await FetchAttemptOverride(attempt, url, acceptJson);
                return await FetchOnceAsync(url, acceptJson);
            }
            catch (Exception e)
            {
                last = e;
                if (!IsRetryable(e) || attempt == RetryDelaysMs.Length) throw;
                int delay = RetryDelaysMs[attempt];
                if (RetryDelayOverride is not null) await RetryDelayOverride(delay);
                else await Task.Delay(delay);
            }
        }
        throw last!;
    }

    private static bool IsRetryable(Exception e)
    {
        if (e is HttpStatusException hse) return hse.StatusCode >= 500;
        // タイムアウト (TaskCanceledException) と通信失敗はリトライ対象
        return e is HttpRequestException or TaskCanceledException or IOException;
    }

    private static async Task<string> FetchOnceAsync(string url, bool acceptJson)
    {
        using var cts = new CancellationTokenSource(FetchTimeoutMs);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"AEGIS-Windows-CS/{Version}");
        if (acceptJson) req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var res = await Http.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) throw new HttpStatusException((int)res.StatusCode);
        return await res.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>例外を表示用の日本語文にする (生の例外文は出さない)</summary>
    public static string FriendlyError(Exception e)
    {
        switch (e)
        {
            case TaskCanceledException:
                return "タイムアウトしました";
            case HttpStatusException hse when hse.StatusCode >= 500:
                return $"サーバーエラー（HTTP {hse.StatusCode}）";
            case HttpStatusException hse:
                return $"サーバーエラー（HTTP {hse.StatusCode}）";
            case HttpRequestException hre when hre.StatusCode is not null:
                return $"サーバーエラー（HTTP {(int)hre.StatusCode}）";
            case HttpRequestException hre when IsConnectionFailure(hre):
                return "サーバーに接続できませんでした";
            case HttpRequestException:
                return "通信が切断されました";
            default:
                return "通信エラーが発生しました";
        }
    }

    private static bool IsConnectionFailure(HttpRequestException e)
    {
        for (Exception? cur = e; cur is not null; cur = cur.InnerException)
        {
            if (cur is System.Net.Sockets.SocketException) return true;
            string m = cur.Message;
            if (m.Contains("Name or service not known") || m.Contains("nodename nor servname")
                || m.Contains("Connection refused") || m.Contains("No connection could be made")
                || m.Contains("名前を解決できません") || m.Contains("接続が拒否"))
                return true;
        }
        return false;
    }

    // ----------------------------------------
    // 🧩 JSON ノードの読み取りヘルパー
    // ----------------------------------------
    // 📌 System.Text.Json.Nodes は型が合わないと例外を投げるため、
    // 「無ければ null / 既定値」を返す薄いラッパで統一する。
    public static JsonNode? Child(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var v) ? v : null;

    public static string? StrOf(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? StrOf(JsonNode? node, string key) => StrOf(Child(node, key));

    public static double? NumOf(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s)
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    public static double? NumOf(JsonNode? node, string key) => NumOf(Child(node, key));

    public static int? IntOf(JsonNode? node) => NumOf(node) is { } d ? (int)d : null;

    public static int? IntOf(JsonNode? node, string key) => IntOf(Child(node, key));

    public static bool? BoolOf(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static bool? BoolOf(JsonNode? node, string key) => BoolOf(Child(node, key));

    public static JsonArray? ArrOf(JsonNode? node) => node as JsonArray;

    public static JsonArray? ArrOf(JsonNode? node, string key) => Child(node, key) as JsonArray;

    public static JsonObject? ObjOf(JsonNode? node) => node as JsonObject;

    /// <summary>JSON 文字列をパースする。失敗したら null</summary>
    public static JsonNode? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (Exception) { return null; }
    }
}
