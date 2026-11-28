using System.Globalization;
using System.Text.Json.Nodes;

namespace AegisWin;

// ========================================
// 📡 A.A.S (Aegis Alert System) 連携
// ========================================
// 📌 地震・津波・EEW・気象警報・台風は A.A.S からのみ取得する
// (直接取得はしない)。取り込みは SSE (/v1/stream) が主、REST
// (/v1/events) が初期同期と履歴回収を担う。
// 📌 重複判定は key + updatedAt。初期同期 (replay) で取り込んだ分の
// 通知候補は抑制リストに入れ、起動直後の一斉通知を防ぐ。
// ⚠ A.A.S のデータは再配布しない (このクライアント内の表示専用)。

public static partial class Data
{
    // ----------------------------------------
    // ⚙️ 接続設定 (Settings から反映される)
    // ----------------------------------------
    public static bool A2sEnabled { get; set; }
    public static string A2sBaseUrl { get; set; } = "";
    public static string A2sToken { get; set; } = "";

    /// <summary>接続可能な状態か (有効 + URL + トークンの3点セット)</summary>
    public static bool A2sConfigured
        => A2sEnabled && !string.IsNullOrWhiteSpace(A2sBaseUrl) && !string.IsNullOrWhiteSpace(A2sToken);

    /// <summary>🧪 テスト用フック: A2S への GET の差し替え (URL, トークン) → 本文</summary>
    public static Func<string, string?, Task<string>>? A2sHttpGetOverride;

    // ----------------------------------------
    // 📦 イベントの表現と検証
    // ----------------------------------------
    /// <summary>A.A.S のイベント1件 (検証済み)。Raw は変換用に保持する</summary>
    public sealed class A2sEvent
    {
        public required string Kind { get; init; }
        public required string Key { get; init; }
        public string UpdatedAt { get; init; } = "";
        public required JsonNode Raw { get; init; }
    }

    /// <summary>
    /// イベント JSON を検証して取り込む。種別ごとに必須フィールドを確認し、
    /// 形の崩れたイベントは変換に回さず null を返す。
    /// </summary>
    public static A2sEvent? ParseA2sEvent(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        string? kind = StrOf(obj, "kind");
        string? key = StrOf(obj, "key");
        if (kind is null || string.IsNullOrEmpty(key)) return null;

        bool ok = kind switch
        {
            "quake" => StrOf(obj, "time") is not null && StrOf(obj, "hypocenter") is not null
                && NumOf(obj, "maxScale") is not null
                && ArrOf(obj, "points") is not null && ArrOf(obj, "prefScales") is not null,
            "eew" => StrOf(obj, "baseKey") is not null && StrOf(obj, "originTime") is not null
                && NumOf(obj, "estScale") is not null
                && BoolOf(obj, "isWarn") is not null && BoolOf(obj, "cancel") is not null,
            "tsunami" => StrOf(obj, "grade") is not null && ArrOf(obj, "areas") is not null,
            "warning" => StrOf(obj, "officeCode") is not null && StrOf(obj, "areaCode") is not null
                && ArrOf(obj, "warnings") is not null,
            "typhoon" => ArrOf(obj, "lines") is not null,
            _ => false,
        };
        if (!ok) return null;
        return new A2sEvent
        {
            Kind = kind,
            Key = key,
            UpdatedAt = StrOf(obj, "updatedAt") ?? "",
            Raw = obj.DeepClone(),
        };
    }

    /// <summary>/v1/events の応答 ({"events":[...]}) から有効なイベントだけを抜く</summary>
    public static List<A2sEvent> ParseA2sEvents(JsonNode? root)
    {
        var result = new List<A2sEvent>();
        if (ArrOf(root, "events") is { } events)
        {
            foreach (var node in events)
            {
                var ev = ParseA2sEvent(node);
                if (ev is not null) result.Add(ev);
            }
        }
        return result;
    }

    // ----------------------------------------
    // 🗄️ 保持ストア (重複判定・再生抑制・スナップショット間引き)
    // ----------------------------------------
    public enum A2sAcceptResult { New, Updated, Duplicate }

    private const int A2sStoreCap = 1000;
    private const int A2sSuppressedCap = 300;

    private static readonly object A2sLock = new();
    private static readonly Dictionary<string, A2sEvent> A2sStore = new();
    private static readonly Queue<string> A2sStoreOrder = new();
    private static readonly List<string> A2sSuppressed = new();
    private static readonly Dictionary<string, DateTimeOffset> A2sLastSync = new();

    /// <summary>保持状態をすべて消す (設定変更・テスト用)</summary>
    public static void ClearA2sState()
    {
        lock (A2sLock)
        {
            A2sStore.Clear();
            A2sStoreOrder.Clear();
            A2sSuppressed.Clear();
            A2sLastSync.Clear();
        }
    }

    /// <summary>通知候補 ID が再生 (初期同期) 由来で抑制対象か</summary>
    public static bool IsA2sReplaySuppressed(string candidateId)
    {
        lock (A2sLock) return A2sSuppressed.Contains(candidateId);
    }

    private static void SuppressCandidateIds(A2sEvent ev)
    {
        var ids = new List<string>();
        try
        {
            switch (ev.Kind)
            {
                case "quake":
                    if (A2sQuakeToQuake(ev) is { } q)
                        ids.AddRange(QuakeCandidates(new List<Quake> { q }).Select(c => c.Id));
                    break;
                case "eew":
                    if (A2sEewToInfo(ev) is { } info
                        && EewCandidate(info, Locations, NowInTokyo()) is { } cand)
                        ids.Add(cand.Id);
                    break;
                case "tsunami":
                    if (A2sTsunamiCandidate(ev) is { } tc) ids.Add(tc.Id);
                    break;
                case "warning":
                    foreach (var loc in Locations)
                    {
                        var info = A2sWarningInfoFor(loc.PrefCode, "");
                        if (WarningCandidate(loc, info) is { } wc) ids.Add(wc.Id);
                    }
                    break;
            }
        }
        catch (Exception) { /* 抑制登録の失敗は取り込み自体を止めない */ }
        lock (A2sLock)
        {
            foreach (var id in ids)
            {
                A2sSuppressed.Remove(id);
                A2sSuppressed.Add(id);
            }
            while (A2sSuppressed.Count > A2sSuppressedCap) A2sSuppressed.RemoveAt(0);
        }
    }

    /// <summary>
    /// イベントを保持ストアへ取り込む。未設定の間は何も取り込まない。
    /// 警報の解除 (released) は保持から外し、Updated として扱う。
    /// </summary>
    public static A2sAcceptResult NoteA2sEvent(A2sEvent ev, bool replay)
    {
        if (!A2sConfigured) return A2sAcceptResult.Duplicate;

        bool released = ev.Kind == "warning" && BoolOf(ev.Raw, "released") == true;
        A2sAcceptResult result;
        lock (A2sLock)
        {
            if (released)
            {
                if (!A2sStore.Remove(ev.Key)) return A2sAcceptResult.Duplicate;
                result = A2sAcceptResult.Updated;
            }
            else if (A2sStore.TryGetValue(ev.Key, out var held))
            {
                // updatedAt が同じか古いものは重複として捨てる
                if (string.CompareOrdinal(ev.UpdatedAt, held.UpdatedAt) <= 0)
                    return A2sAcceptResult.Duplicate;
                A2sStore[ev.Key] = ev;
                result = A2sAcceptResult.Updated;
            }
            else
            {
                A2sStore[ev.Key] = ev;
                A2sStoreOrder.Enqueue(ev.Key);
                while (A2sStore.Count > A2sStoreCap && A2sStoreOrder.Count > 0)
                    A2sStore.Remove(A2sStoreOrder.Dequeue());
                result = A2sAcceptResult.New;
            }
        }
        if (replay && result != A2sAcceptResult.Duplicate) SuppressCandidateIds(ev);
        return result;
    }

    private static List<A2sEvent> A2sSnapshotOfKind(string kind)
    {
        lock (A2sLock)
            return A2sStore.Values.Where(e => e.Kind == kind).ToList();
    }

    // ----------------------------------------
    // 🔄 REST 同期 (/v1/events)
    // ----------------------------------------
    /// <summary>/v1/events の URL を組み立てる。警報は件数が多いため上限 700</summary>
    public static string A2sEventsUrl(string? since = null, string? kind = null)
    {
        string baseUrl = A2sBaseUrl.TrimEnd('/');
        int limit = kind == "warning" ? 700 : 100;
        string url = $"{baseUrl}/v1/events?limit={limit}";
        if (kind is not null) url += $"&kind={kind}";
        if (since is not null) url += "&since=" + Uri.EscapeDataString(since);
        return url;
    }

    private static async Task<string> A2sHttpGetAsync(string url)
    {
        if (A2sHttpGetOverride is not null) return await A2sHttpGetOverride(url, A2sToken);
        using var cts = new CancellationTokenSource(25000);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"AEGIS-Windows-CS/{Version}");
        if (!string.IsNullOrEmpty(A2sToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {A2sToken}");
        using var res = await Http.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) throw new HttpStatusException((int)res.StatusCode);
        return await res.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>REST でイベント一覧を取得する (取り込みはしない)</summary>
    public static async Task<List<A2sEvent>> FetchA2sEventsAsync(string? kind = null)
    {
        if (!A2sConfigured) return new List<A2sEvent>();
        string json = await A2sHttpGetAsync(A2sEventsUrl(null, kind));
        return ParseA2sEvents(ParseJson(json));
    }

    /// <summary>
    /// REST で初期同期し、再生扱い (通知抑制つき) で取り込む。
    /// 種別指定 (警報・台風) のときは、一覧に無い保持分を間引く
    /// スナップショット同期も行う (切断中の解除・消滅の回収)。
    /// </summary>
    public static async Task<int> A2sSyncAsync(string? kind = null)
    {
        var events = await FetchA2sEventsAsync(kind);
        int accepted = 0;
        foreach (var ev in events)
            if (NoteA2sEvent(ev, replay: true) != A2sAcceptResult.Duplicate) accepted++;

        if (kind is "warning" or "typhoon")
        {
            var freshKeys = events.Where(e => e.Kind == kind).Select(e => e.Key).ToHashSet();
            lock (A2sLock)
            {
                var stale = A2sStore.Values
                    .Where(e => e.Kind == kind && !freshKeys.Contains(e.Key))
                    .Select(e => e.Key).ToList();
                foreach (var k in stale) A2sStore.Remove(k);
            }
        }
        lock (A2sLock) A2sLastSync[kind ?? "all"] = DateTimeOffset.UtcNow;
        return accepted;
    }

    /// <summary>種別の保持が空、または最終同期から60秒を超えたら古いとみなす</summary>
    public static bool A2sSnapshotStale(string kind)
    {
        lock (A2sLock)
        {
            bool has = A2sStore.Values.Any(e => e.Kind == kind);
            if (!has) return true;
            return !A2sLastSync.TryGetValue(kind, out var at)
                || (DateTimeOffset.UtcNow - at).TotalSeconds > 60;
        }
    }

    private static async Task A2sSyncIfStaleAsync(string kind)
    {
        if (!A2sConfigured || !A2sSnapshotStale(kind)) return;
        try { await A2sSyncAsync(kind); }
        catch (Exception) { /* 同期失敗時は保持分だけで続行する */ }
    }

    // ----------------------------------------
    // 🔁 A.A.S イベント → 表示データへの変換
    // ----------------------------------------
    /// <summary>A.A.S の地震イベントを表示用 Quake に変換する</summary>
    public static Quake? A2sQuakeToQuake(A2sEvent ev)
    {
        if (ev.Kind != "quake") return null;
        var o = ev.Raw;
        bool foreign = BoolOf(o, "foreign") == true;
        var tg = new QuakeTelegram
        {
            // 国外地震は発生時刻 (originTime) を正とし、国内は発表の time を使う
            Time = foreign ? (StrOf(o, "originTime") ?? StrOf(o, "time") ?? "")
                           : (StrOf(o, "time") ?? ""),
            HypoName = StrOf(o, "hypocenter") ?? "",
            Mag = NumOf(o, "magnitude") is { } m && m >= 0 ? m : null,
            DepthKm = NumOf(o, "depthKm") is { } d && d >= 0 ? (int)d : null,
            MaxScale = IntOf(o, "maxScale") ?? 0,
            Lat = NumOf(o, "lat") ?? 0,
            Lon = NumOf(o, "lon") ?? 0,
            Foreign = foreign,
            IssueType = StrOf(o, "issueType") ?? "",
            IssueCorrect = StrOf(o, "issueCorrect"),
            TsunamiKind = StrOf(o, "tsunamiKind"),
            TsunamiText = StrOf(o, "tsunamiText"),
            TopPoint = StrOf(o, "topPoint"),
            TopPointIsArea = BoolOf(o, "topPointIsArea") == true,
        };
        if (TryParseJstTime(StrOf(o, "announcedTime"), out var announced)) tg.IssueTime = announced;

        var pointPrefs = new HashSet<string>();
        if (ArrOf(o, "points") is { } points)
        {
            foreach (var p in points)
            {
                string pref = StrOf(p, "pref") ?? "";
                string name = StrOf(p, "name") ?? "";
                if (pref.Length == 0 || name.Length == 0) continue;
                tg.Points.Add(new QuakePoint(pref, name, IntOf(p, "scale") ?? 0,
                    BoolOf(p, "isArea") == true));
                pointPrefs.Add(pref);
            }
        }
        if (ArrOf(o, "prefScales") is { } prefScales)
        {
            foreach (var ps in prefScales)
            {
                string pref = StrOf(ps, "pref") ?? "";
                if (pref.Length == 0) continue;
                int scale = IntOf(ps, "scale") ?? 0;
                tg.PrefScales.Add((pref, scale));
                // 観測点が無い都道府県は、県単位の地域震度として合成する
                if (!pointPrefs.Contains(pref))
                    tg.Points.Add(new QuakePoint(pref, pref, scale, IsArea: true));
            }
        }
        return MergeQuakeGroup(new List<QuakeTelegram> { tg });
    }

    /// <summary>A.A.S の EEW イベントを EewInfo に変換する</summary>
    public static EewInfo? A2sEewToInfo(A2sEvent ev)
    {
        if (ev.Kind != "eew") return null;
        var o = ev.Raw;
        var info = new EewInfo
        {
            OriginTime = StrOf(o, "originTime") ?? "",
            AnnouncedTime = StrOf(o, "time") ?? "",
            HypoName = StrOf(o, "hypocenter") ?? "",
            EstMaxScale = IntOf(o, "estScale") ?? 0,
            Mag = NumOf(o, "estMagnitude") ?? 0,
            Depth = IntOf(o, "depthKm") ?? -1,
            Lat = NumOf(o, "lat") ?? 0,
            Lon = NumOf(o, "lon") ?? 0,
            IsWarn = BoolOf(o, "isWarn") == true,
            IsCancel = BoolOf(o, "cancel") == true,
            IsFinal = BoolOf(o, "isFinal") == true,
            IsAssumption = BoolOf(o, "isAssumption") == true,
            Serial = IntOf(o, "serial"),
            EventId = StrOf(o, "eventId"),
        };
        // 地域別到達予想は都道府県単位で届くため、地域別震度へ結合する
        var arrivals = new Dictionary<string, DateTime>();
        if (ArrOf(o, "areaArrivals") is { } arrs)
        {
            foreach (var a in arrs)
            {
                string pref = StrOf(a, "pref") ?? "";
                if (pref.Length > 0 && TryParseJstTime(StrOf(a, "arrivalTime"), out var at))
                    arrivals[pref] = at;
            }
        }
        if (ArrOf(o, "areaScales") is { } scales)
        {
            foreach (var s in scales)
            {
                string pref = StrOf(s, "pref") ?? "";
                int from = IntOf(s, "scale") ?? 0;
                var area = new EewArea
                {
                    Name = StrOf(s, "name") ?? pref,
                    Pref = pref,
                    ScaleFrom = from,
                    ScaleTo = IntOf(s, "scaleTo") ?? from,
                };
                if (arrivals.TryGetValue(pref, out var at)) area.ArrivalTime = at;
                info.Areas.Add(area);
            }
        }
        return info;
    }

    /// <summary>A.A.S の津波イベントを TsunamiInfo に変換する</summary>
    public static TsunamiInfo? A2sTsunamiToInfo(A2sEvent ev)
    {
        if (ev.Kind != "tsunami") return null;
        var o = ev.Raw;
        var info = new TsunamiInfo
        {
            Cancelled = BoolOf(o, "cancelled") == true,
            Time = StrOf(o, "time") ?? "",
        };
        if (ArrOf(o, "areas") is { } areas)
        {
            foreach (var a in areas)
            {
                string name = StrOf(a, "name") ?? "";
                if (name.Length == 0) continue;
                string? height = StrOf(a, "maxHeightLabel");
                if (height is null && NumOf(a, "maxHeightValue") is { } hv)
                    height = hv == Math.Floor(hv)
                        ? $"{(long)hv}m"
                        : hv.ToString(CultureInfo.InvariantCulture) + "m";
                info.Areas.Add(new TsunamiArea(name, StrOf(a, "grade") ?? "",
                    height, StrOf(a, "arrivalLabel"), StrOf(a, "firstHeightCondition")));
            }
        }
        return info;
    }

    /// <summary>津波イベントの通知用 ID (キーから "552:" 接頭辞を除いた電文 ID)</summary>
    public static string A2sTsunamiId(A2sEvent ev)
        => ev.Key.StartsWith("552:") ? ev.Key.Substring(4) : ev.Key;

    /// <summary>A.A.S の津波イベントを表示セクションに変換する</summary>
    public static Section? A2sTsunamiSection(A2sEvent ev)
        => ev.Kind == "tsunami" ? BuildTsunamiSection(A2sTsunamiToInfo(ev)) : null;

    /// <summary>保持中の最新の津波イベント</summary>
    public static A2sEvent? A2sCurrentTsunamiEvent()
        => A2sSnapshotOfKind("tsunami")
            .OrderByDescending(e => e.UpdatedAt, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>保持中の津波の表示セクション (無ければ null)</summary>
    public static Section? A2sCurrentTsunamiSection()
        => A2sCurrentTsunamiEvent() is { } ev ? A2sTsunamiSection(ev) : null;

    /// <summary>保持中の津波の通知候補 (無ければ null)</summary>
    public static NotifyCandidate? A2sCurrentTsunamiCandidate()
        => A2sCurrentTsunamiEvent() is { } ev ? A2sTsunamiCandidate(ev) : null;

    /// <summary>A.A.S の津波イベントの通知候補を作る (解除・区域なしは null)</summary>
    public static NotifyCandidate? A2sTsunamiCandidate(A2sEvent ev)
    {
        var info = A2sTsunamiToInfo(ev);
        if (info is null || info.Cancelled || info.Areas.Count == 0) return null;
        var section = BuildTsunamiSection(info);
        return new NotifyCandidate("tsunami", $"tsunami:{A2sTsunamiId(ev)}",
            section.Title, section.Body, 0, section.Level);
    }

    // ----------------------------------------
    // ⚠ 気象警報・注意報 (区域コードで照合して統合)
    // ----------------------------------------
    /// <summary>地点向けに統合した警報・注意報</summary>
    public sealed class WarningInfo
    {
        public List<string> Names { get; } = new();
        public List<string> Codes { get; } = new();
        public int MaxSeverity { get; set; }
        public string? ReportDatetime { get; set; }
    }

    /// <summary>
    /// 保持中の警報を (予報区コード, 区域コード) で照合して統合する。
    /// 区域コードが空のときはその予報区の全区域を対象にする。
    /// 同じ警報コードは最大の severity で1件にまとめ、severity 降順に並べる。
    /// </summary>
    public static WarningInfo A2sWarningInfoFor(string officeCode, string areaCode)
    {
        var result = new WarningInfo();
        var byCode = new Dictionary<string, (string Name, int Severity)>();
        foreach (var ev in A2sSnapshotOfKind("warning"))
        {
            var o = ev.Raw;
            if (StrOf(o, "officeCode") != officeCode) continue;
            if (areaCode.Length > 0 && StrOf(o, "areaCode") != areaCode) continue;
            string? time = StrOf(o, "time");
            if (time is not null && (result.ReportDatetime is null
                || string.CompareOrdinal(time, result.ReportDatetime) > 0))
                result.ReportDatetime = time;
            if (ArrOf(o, "warnings") is not { } warnings) continue;
            foreach (var w in warnings)
            {
                string code = StrOf(w, "code") ?? "";
                string name = StrOf(w, "name") ?? "";
                if (code.Length == 0 || name.Length == 0) continue;
                if (StrOf(w, "status") == "解除") continue;
                int severity = IntOf(w, "severity") ?? 0;
                if (!byCode.TryGetValue(code, out var cur) || severity > cur.Severity)
                    byCode[code] = (name, severity);
            }
        }
        foreach (var (code, (name, severity)) in byCode
                     .OrderByDescending(kv => kv.Value.Severity).ThenBy(kv => kv.Value.Name))
        {
            result.Codes.Add(code);
            result.Names.Add(name);
            result.MaxSeverity = Math.Max(result.MaxSeverity, severity);
        }
        return result;
    }

    // ----------------------------------------
    // 🌀 台風
    // ----------------------------------------
    /// <summary>保持中の台風イベント (新しい順)</summary>
    public static List<A2sEvent> A2sTyphoonsSnapshot()
        => A2sSnapshotOfKind("typhoon")
            .OrderByDescending(e => e.UpdatedAt, StringComparer.Ordinal).ToList();

    /// <summary>台風イベントを表示セクションに変換する</summary>
    public static Section? A2sTyphoonSection(A2sEvent ev)
    {
        if (ev.Kind != "typhoon") return null;
        var lines = ArrOf(ev.Raw, "lines");
        if (lines is null) return null;
        var texts = lines.Select(l => StrOf(l)).Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!).ToList();
        if (texts.Count == 0) return null;
        return new Section("🌀 台風情報", string.Join("\n", texts), "info");
    }

    // ----------------------------------------
    // 📥 表示・通知向けの非同期アクセサ
    // ----------------------------------------
    /// <summary>保持中の地震 (新しい順)</summary>
    public static List<Quake> A2sQuakesSnapshot()
        => A2sSnapshotOfKind("quake")
            .Select(A2sQuakeToQuake).Where(q => q is not null).Select(q => q!)
            .OrderByDescending(q => q.Time, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 地震一覧を返す。保持が無ければ REST の初期同期で履歴を回収する。
    /// 未設定の間は常に空。
    /// </summary>
    public static async Task<List<Quake>> QuakeItemsAsync(int limit)
    {
        if (!A2sConfigured) return new List<Quake>();
        var items = A2sQuakesSnapshot();
        if (items.Count == 0)
        {
            try { await A2sSyncAsync(); }
            catch (Exception) { /* 回収失敗時は空のまま */ }
            items = A2sQuakesSnapshot();
        }
        return items.Take(limit).ToList();
    }

    /// <summary>
    /// 現在の EEW を返す。保持中の最新で、発生時刻が表示範囲 (15分)
    /// を超えたものは返さない。未設定の間は null。
    /// </summary>
    public static async Task<EewInfo?> EewInfoAsync()
    {
        if (!A2sConfigured) return null;
        var info = LatestFreshEew();
        if (info is null && A2sSnapshotOfKind("eew").Count == 0)
        {
            try { await A2sSyncAsync(); }
            catch (Exception) { /* 同期失敗時は null のまま */ }
            info = LatestFreshEew();
        }
        return info;
    }

    private static EewInfo? LatestFreshEew()
    {
        var now = NowInTokyo();
        EewInfo? best = null;
        DateTime bestOrigin = DateTime.MinValue;
        foreach (var ev in A2sSnapshotOfKind("eew"))
        {
            var info = A2sEewToInfo(ev);
            if (info is null) continue;
            if (!TryParseJstTime(info.OriginTime, out var origin)) continue;
            if ((now - origin).TotalMinutes > EewDisplayMinutes) continue;
            if (origin > bestOrigin) { bestOrigin = origin; best = info; }
        }
        return best;
    }

    /// <summary>現在の EEW の表示セクション (無ければ null)</summary>
    public static async Task<Section?> EewSectionAsync()
    {
        var info = await EewInfoAsync();
        return info is null ? null : EewSection(info, NowInTokyo());
    }

    /// <summary>津波セクション。未設定・保持なしの間は「発表なし」の ok セクション</summary>
    public static async Task<Section> TsunamiSectionAsync()
    {
        if (!A2sConfigured) return BuildTsunamiSection(null);
        var section = A2sCurrentTsunamiSection();
        if (section is not null) return section;
        try { await A2sSyncAsync(); }
        catch (Exception) { /* 同期失敗時は保持分だけで判定する */ }
        return A2sCurrentTsunamiSection() ?? BuildTsunamiSection(null);
    }

    /// <summary>台風セクション。保持が無ければ同期してから判定し、
    /// 台風が無ければ null</summary>
    public static async Task<Section?> TyphoonSectionAsync()
    {
        if (!A2sConfigured) return null;
        await A2sSyncIfStaleAsync("typhoon");
        var typhoons = A2sTyphoonsSnapshot();
        return typhoons.Count > 0 ? A2sTyphoonSection(typhoons[0]) : null;
    }
}
