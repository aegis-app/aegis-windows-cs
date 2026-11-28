using System.Text;

namespace AegisWin;

// ========================================
// 📶 A.A.S SSE ストリーム (/v1/stream)
// ========================================
// 📌 SSE はチャンク境界が不定なので、行単位の状態機械 (A2sSseParser) で
// フレームを組み立てる。接続直後はサーバーが保持分を再生 (replay) し、
// "ready" フレーム以降が実況になる。再生分は通知抑制の対象。
// 📌 再接続は 5s → 10s → 30s → 60s のバックオフ。ハンドラ例外は
// 握りつぶしてストリーム自体を止めない。

/// <summary>SSE フレーム1件 (イベント種別と data 本文)</summary>
public sealed record A2sSseFrame(string Type, string Data);

/// <summary>
/// SSE テキストを行単位で解釈する状態機械。コメント行 (heartbeat) は捨て、
/// 複数行の data は改行で結合し、空行でフレームを確定する。
/// </summary>
public sealed class A2sSseParser
{
    private string _buffer = "";
    private string _eventType = "message";
    private readonly List<string> _dataLines = new();

    /// <summary>テキスト断片を投入し、確定したフレームを返す</summary>
    public List<A2sSseFrame> Push(string chunk)
    {
        var frames = new List<A2sSseFrame>();
        _buffer += chunk;
        int idx;
        while ((idx = _buffer.IndexOf('\n')) >= 0)
        {
            string line = _buffer.Substring(0, idx).TrimEnd('\r');
            _buffer = _buffer.Substring(idx + 1);
            if (line.Length == 0)
            {
                // 空行 = フレーム確定
                if (_dataLines.Count > 0)
                    frames.Add(new A2sSseFrame(_eventType, string.Join("\n", _dataLines)));
                _eventType = "message";
                _dataLines.Clear();
            }
            else if (line.StartsWith(":")) { /* コメント / heartbeat は無視 */ }
            else if (line.StartsWith("event:"))
            {
                _eventType = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:"))
            {
                string data = line.Substring(5);
                if (data.StartsWith(" ")) data = data.Substring(1);
                _dataLines.Add(data);
            }
            // id: / retry: 等の他フィールドは使わない
        }
        return frames;
    }
}

/// <summary>SSE 接続の抽象 (テストで差し替え可能にするため)</summary>
public interface IA2sStreamTransport : IDisposable
{
    Task<Stream> ConnectAsync(Uri uri, string? token, CancellationToken ct);
}

/// <summary>HttpClient による実接続トランスポート</summary>
public sealed class HttpA2sStreamTransport : IA2sStreamTransport
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<Stream> ConnectAsync(Uri uri, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        req.Headers.TryAddWithoutValidation("User-Agent", $"AEGIS-Windows-CS/{Data.Version}");
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            int code = (int)res.StatusCode;
            res.Dispose();
            throw new HttpStatusException(code);
        }
        return await res.Content.ReadAsStreamAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// A.A.S の SSE を購読し続けるクライアント。切断時はバックオフして
/// 再接続し、キャンセルで停止する。
/// </summary>
public sealed class A2sStreamClient
{
    private static readonly int[] BackoffMs = { 5000, 10000, 30000, 60000 };

    /// <summary>再接続までの待機ミリ秒 (試行回数に応じて 5s→10s→30s→60s で頭打ち)</summary>
    public static int ReconnectDelayMs(int attempt)
        => BackoffMs[Math.Clamp(attempt, 0, BackoffMs.Length - 1)];

    private readonly string _baseUrl;
    private readonly string? _token;
    private readonly Action<Data.A2sEvent, bool> _onEvent;
    private readonly Action? _onReady;
    private readonly Func<IA2sStreamTransport> _transportFactory;
    private readonly Func<int, Task>? _delayOverride;

    public A2sStreamClient(string baseUrl, string? token,
        Action<Data.A2sEvent, bool> onEvent, Action? onReady = null,
        Func<IA2sStreamTransport>? transportFactory = null,
        Func<int, Task>? delayOverride = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _onEvent = onEvent;
        _onReady = onReady;
        _transportFactory = transportFactory ?? (() => new HttpA2sStreamTransport());
        _delayOverride = delayOverride;
    }

    /// <summary>フレームを A.A.S イベントに変換する。ready 等の制御フレームと
    /// 壊れた JSON は null を返す</summary>
    public static Data.A2sEvent? ParseFrameEvent(A2sSseFrame frame)
    {
        if (frame.Type is not ("quake" or "eew" or "tsunami" or "warning" or "typhoon"))
            return null;
        return Data.ParseA2sEvent(Data.ParseJson(frame.Data));
    }

    /// <summary>購読ループ本体。キャンセルされるまで動き続ける</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReadAsync(ct);
                attempt = 0; // 正常に読み終えたらバックオフを戻す
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* 接続失敗は再接続で回復を試みる */ }
            if (ct.IsCancellationRequested) break;
            int delay = ReconnectDelayMs(attempt++);
            try
            {
                if (_delayOverride is not null) await _delayOverride(delay);
                else await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        using var transport = _transportFactory();
        var uri = new Uri($"{_baseUrl}/v1/stream");
        await using var stream = await transport.ConnectAsync(uri, _token, ct);
        var parser = new A2sSseParser();
        bool replay = true; // ready フレームが来るまでは再生扱い
        var buffer = new byte[8192];
        var decoder = new UTF8Encoding(false).GetDecoder();
        var chars = new char[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            int charCount = decoder.GetChars(buffer, 0, read, chars, 0, false);
            string chunk = new string(chars, 0, charCount);
            foreach (var frame in parser.Push(chunk))
            {
                if (frame.Type == "ready")
                {
                    replay = false;
                    try { _onReady?.Invoke(); } catch (Exception) { /* ハンドラ例外は無視 */ }
                    continue;
                }
                var ev = ParseFrameEvent(frame);
                if (ev is null) continue;
                try { _onEvent(ev, replay); }
                catch (Exception) { /* ハンドラ例外でストリームを止めない */ }
            }
        }
    }
}
