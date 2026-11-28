using System.Diagnostics;

namespace AegisWin;

// ========================================
// 🩺 接続診断 と 🔔 通知候補
// ========================================
// 📌 診断は各データソースへ実際にリクエストし、成否と所要時間を返す。
// 📌 通知候補は「候補の生成」と「通知可否の判定」を分ける。候補 ID は
// 再生抑制・通知済み管理の両方で使う安定した形式にする:
//   地震: quake:{発生時刻}:{震源名}
//   EEW:  eew:{発生時刻}:s{予想震度} / 取消は eew:{発生時刻}:cancel
//   津波: tsunami:{電文ID}
//   警報: warning:{地点名}:{警報コード列の Java ハッシュ (16進)}

public static partial class Data
{
    // ----------------------------------------
    // 🩺 接続診断
    // ----------------------------------------
    /// <summary>診断1項目の結果</summary>
    public sealed record DiagResult(string Name, bool Ok, string Message, long ElapsedMs);

    private static async Task<DiagResult> ProbeAsync(string name, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            sw.Stop();
            return new DiagResult(name, true, "", sw.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            sw.Stop();
            return new DiagResult(name, false, FriendlyError(e), sw.ElapsedMilliseconds);
        }
    }

    /// <summary>全データソースへの接続診断を行う</summary>
    public static async Task<List<DiagResult>> DiagnoseAsync()
    {
        var results = new List<DiagResult>();

        if (Mode == "server")
        {
            results.Add(await ProbeAsync("AEGISサーバー", async () =>
                await FetchAsync($"{ServerUrl.TrimEnd('/')}/api/briefing", acceptJson: true)));
        }

        // 📡 A.A.S は未設定なら「失敗」として理由を明示する (地震系が
        // 表示されないことに気づけるようにするため)
        if (!A2sConfigured)
        {
            results.Add(new DiagResult("A2S", false,
                "未設定（地震系・警報・台風の情報は表示されません）", 0));
        }
        else
        {
            results.Add(await ProbeAsync("A2S", async () => await A2sHttpGetAsync(A2sEventsUrl())));
        }

        results.Add(await ProbeAsync("気象庁 予報", async () =>
            await FetchAsync(JmaForecastUrl(Takasaki), acceptJson: true)));
        results.Add(await ProbeAsync("Open-Meteo", async () =>
            await FetchAsync(OpenMeteoUrl(Takasaki), acceptJson: true)));
        results.Add(await ProbeAsync("RainViewer（レーダー）", async () =>
            await FetchAsync("https://api.rainviewer.com/public/weather-maps.json", acceptJson: true)));
        results.Add(await ProbeAsync("気象庁ナウキャスト（レーダー）", async () =>
            await FetchAsync("https://www.jma.go.jp/bosai/jmatile/data/nowc/targetTimes_N1.json", acceptJson: true)));
        results.Add(await ProbeAsync("NHK RSS（ニュース）", async () =>
            await FetchAsync("https://www.nhk.or.jp/rss/news/cat0.xml")));
        return results;
    }

    /// <summary>診断結果を表示テキストにする</summary>
    public static string FormatDiag(List<DiagResult> results)
        => string.Join("\n", results.Select(r => r.Ok
            ? $"○ 接続OK（{r.ElapsedMs}ms）: {r.Name}"
            : $"× 失敗（{r.Message}）: {r.Name}"));

    // ----------------------------------------
    // 🔔 通知候補
    // ----------------------------------------
    /// <summary>通知候補1件。Grade は種別ごとの深刻度 (警報は special/warning/advisory)</summary>
    public sealed record NotifyCandidate(string Kind, string Id, string Title,
        string Body, int MaxScale, string Grade);

    /// <summary>イベントが通知に値する新しさか (発生から1時間以内、
    /// 未来は5分先まで許容)</summary>
    public static bool IsFreshEvent(DateTime eventTime, DateTime now)
    {
        double age = (now - eventTime).TotalSeconds;
        return age <= 3600 && age >= -300;
    }

    /// <summary>地震の通知候補を作る</summary>
    public static List<NotifyCandidate> QuakeCandidates(List<Quake> quakes)
        => quakes.Select(q => new NotifyCandidate("quake",
            $"quake:{q.Time}:{q.Place}",
            $"{SeverityEmoji(q.Sev)} 地震情報: {q.Place}",
            q.Detail, q.MaxScale, q.Sev)).ToList();

    /// <summary>EEW の通知 ID。取消は :cancel、通常報は予想震度つき</summary>
    public static string EewNotifyId(EewInfo info)
    {
        string baseId = $"eew:{info.OriginTime}";
        return info.IsCancel ? baseId + ":cancel" : baseId + $":s{info.EstMaxScale}";
    }

    /// <summary>EEW の通知候補を作る</summary>
    public static NotifyCandidate? EewCandidate(EewInfo info,
        IReadOnlyList<Location> locations, DateTime now)
    {
        string body = EewBaseBody(info);
        var countdown = EewCountdownLines(info, locations, now);
        if (countdown.Count > 0) body += "\n" + string.Join("\n", countdown);
        string grade = info.IsCancel ? "cancel" : info.IsWarn ? "warning" : "forecast";
        return new NotifyCandidate("eew", EewNotifyId(info), EewTitle(info),
            body, info.EstMaxScale, grade);
    }

    /// <summary>
    /// EEW を通知すべきか。同じ発生時刻の続報は、予想震度が上がった
    /// ときだけ再通知する。旧形式の ID (震度なし) は震度0として扱う。
    /// 取消は、元の速報を通知済みのときだけ通知する。
    /// </summary>
    public static bool ShouldNotifyEew(EewInfo info, ISet<string> notified)
    {
        string id = EewNotifyId(info);
        if (notified.Contains(id)) return false;
        string baseId = $"eew:{info.OriginTime}";
        if (info.IsCancel)
            return notified.Contains(baseId)
                || notified.Any(n => n.StartsWith(baseId + ":s"));
        int maxSeen = -1;
        if (notified.Contains(baseId)) maxSeen = 0;
        foreach (var n in notified)
        {
            if (n.StartsWith(baseId + ":s")
                && int.TryParse(n.Substring(baseId.Length + 2), out int s))
                maxSeen = Math.Max(maxSeen, s);
        }
        return maxSeen < 0 || info.EstMaxScale > maxSeen;
    }

    /// <summary>Java の String.hashCode と同じハッシュを16進文字列にする
    /// (他実装と通知 ID を一致させるための互換形式)</summary>
    public static string JavaHashHex(string s)
    {
        int h = 0;
        foreach (char c in s) h = unchecked(31 * h + c);
        return Convert.ToString(h, 16);
    }

    /// <summary>警報・注意報の通知候補を作る (発表が無ければ null)</summary>
    public static NotifyCandidate? WarningCandidate(Location loc, WarningInfo info)
    {
        if (info.Names.Count == 0) return null;
        string grade = info.MaxSeverity >= 4 ? "special"
            : info.MaxSeverity >= 3 ? "warning" : "advisory";
        var sortedCodes = info.Codes.OrderBy(c => c, StringComparer.Ordinal);
        string id = $"warning:{loc.Name}:{JavaHashHex(string.Join(",", sortedCodes))}";
        string body = $"{loc.Name}: " + string.Join("、", info.Names);
        if (info.ReportDatetime is not null) body += $"（{info.ReportDatetime}発表）";
        return new NotifyCandidate("warning", id,
            $"⚠ 気象警報・注意報（{loc.Name}）", body, 0, grade);
    }

    /// <summary>候補を通知すべきか (種別ごとの条件)</summary>
    public static bool ShouldNotify(NotifyCandidate cand, int minQuakeScale, bool includeAdvisory)
        => cand.Kind switch
        {
            "eew" or "tsunami" => true,
            "quake" => cand.MaxScale >= minQuakeScale,
            "warning" => cand.Grade is "special" or "warning" || includeAdvisory,
            _ => false,
        };

    /// <summary>通知済み ID を上限件数に間引く (古いものから捨てる)</summary>
    public static List<string> PruneNotifiedIds(List<string> ids, int cap = 300)
        => ids.Count <= cap ? new List<string>(ids)
            : ids.Skip(ids.Count - cap).ToList();

    /// <summary>
    /// 通知候補を EEW → 津波 → 地震 → 警報 の順に集める。
    /// 再生 (初期同期) 由来の候補は抑制リストで除く。各ソースの失敗は
    /// そのソースを空として扱い、全体は止めない。
    /// </summary>
    public static async Task<List<NotifyCandidate>> FetchNotifyCandidatesAsync()
    {
        var result = new List<NotifyCandidate>();
        var now = NowInTokyo();

        try
        {
            if (await EewInfoAsync() is { } info
                && EewCandidate(info, Locations, now) is { } cand)
                result.Add(cand);
        }
        catch (Exception) { /* EEW ソースの失敗は無視 */ }

        try
        {
            if (A2sCurrentTsunamiCandidate() is { } cand) result.Add(cand);
        }
        catch (Exception) { /* 津波ソースの失敗は無視 */ }

        try
        {
            var quakes = await QuakeItemsAsync(10);
            foreach (var cand in QuakeCandidates(quakes))
            {
                var q = quakes.First(x => $"quake:{x.Time}:{x.Place}" == cand.Id);
                if (TryParseJstTime(q.Time, out var t) && IsFreshEvent(t, now))
                    result.Add(cand);
            }
        }
        catch (Exception) { /* 地震ソースの失敗は無視 */ }

        try
        {
            await A2sSyncIfStaleAsync("warning");
            foreach (var loc in Locations)
            {
                var info = A2sWarningInfoFor(loc.PrefCode, "");
                if (WarningCandidate(loc, info) is { } cand) result.Add(cand);
            }
        }
        catch (Exception) { /* 警報ソースの失敗は無視 */ }

        result.RemoveAll(c => IsA2sReplaySuppressed(c.Id));
        return result;
    }
}
