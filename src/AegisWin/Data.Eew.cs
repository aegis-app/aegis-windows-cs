using System.Globalization;

namespace AegisWin;

// ========================================
// 🚨 緊急地震速報 (EEW) の整形と到達推定
// ========================================
// 📌 EEW は A.A.S 経由でのみ届く。ここでは表示文・行動ガイド・地点別の
// 到達予想 (発表値優先、無ければ S 波速度からの推定) と、通知候補の
// 生成を担う。到達推定はあくまで目安であり、発表値と区別して表示する。

public static partial class Data
{
    /// <summary>EEW を danger (強調表示) とする発表からの経過分</summary>
    public const int EewDangerMinutes = 5;

    /// <summary>EEW を表示する発表からの経過分の上限 (これを超えたら隠す)</summary>
    public const int EewDisplayMinutes = 15;

    /// <summary>EEW の地域別予想1件</summary>
    public sealed class EewArea
    {
        public string Name { get; set; } = "";
        public string Pref { get; set; } = "";
        public int ScaleFrom { get; set; }
        public int ScaleTo { get; set; }
        public DateTime? ArrivalTime { get; set; }
    }

    /// <summary>EEW 1件ぶんの情報 (A.A.S のイベントから変換したもの)</summary>
    public sealed class EewInfo
    {
        public string OriginTime { get; set; } = "";
        public string AnnouncedTime { get; set; } = "";
        public string HypoName { get; set; } = "";
        public int EstMaxScale { get; set; }
        public double Mag { get; set; }
        public int Depth { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public bool IsWarn { get; set; }
        public bool IsCancel { get; set; }
        public bool IsFinal { get; set; }
        public bool IsAssumption { get; set; }
        public bool IsDemo { get; set; }
        public int? Serial { get; set; }
        public string? EventId { get; set; }
        public DateTime? DemoArrival { get; set; }
        public List<EewArea> Areas { get; set; } = new();
    }

    /// <summary>同一 EEW の識別キー (取消は別キーにして通知・表示を分けられるようにする)</summary>
    public static string EewIdentityKey(EewInfo info)
    {
        string key = $"eew-{info.OriginTime}";
        if (info.Lat != 0 || info.Lon != 0)
            key += "@" + info.Lat.ToString("F1", CultureInfo.InvariantCulture)
                 + "," + info.Lon.ToString("F1", CultureInfo.InvariantCulture);
        else if (!string.IsNullOrEmpty(info.HypoName))
            key += "-" + info.HypoName;
        if (info.IsCancel) key += "-cancel";
        return key;
    }

    /// <summary>EEW の見出し (警報/予報/取消/デモ)</summary>
    public static string EewTitle(EewInfo info)
    {
        if (info.IsDemo) return "🚨 緊急地震速報（デモ表示）";
        if (info.IsCancel) return "🚨 緊急地震速報（取消）";
        return info.IsWarn ? "🚨 緊急地震速報（警報）" : "🚨 緊急地震速報（予報）";
    }

    /// <summary>予想震度に応じた行動ガイド。取消には付けない</summary>
    public static string EewActionGuide(EewInfo info)
    {
        if (info.IsCancel) return "";
        if (info.EstMaxScale >= 45) return "強い揺れが予想されます。身の安全を確保してください。";
        if (info.EstMaxScale >= 40) return "やや強い揺れに備えてください。";
        if (info.EstMaxScale > 0) return "揺れに注意してください。";
        // 震度不明時: 警報なら強め、予報なら注意喚起に留める
        return info.IsWarn ? "強い揺れが予想されます。身の安全を確保してください。" : "揺れに注意してください。";
    }

    /// <summary>EEW の本文 (見出し行・震源情報・行動ガイド)</summary>
    public static string EewBaseBody(EewInfo info)
    {
        var lines = new List<string>();
        string head = EewTitle(info);
        if (info.Serial is { } serial) head += $" 第{serial}報";
        if (info.IsFinal) head += "（最終報）";
        lines.Add(head);

        if (info.IsCancel)
        {
            lines.Add($"先ほどの緊急地震速報は取り消されました。震源: {info.HypoName}");
            return string.Join("\n", lines);
        }

        lines.Add($"震源: {info.HypoName}");
        if (!string.IsNullOrEmpty(info.OriginTime)) lines.Add($"発生時刻: {info.OriginTime}");
        string est = $"予想最大震度: {(info.EstMaxScale > 0 ? ScaleToStr(info.EstMaxScale) : "不明")}";
        if (info.Mag > 0) est += $" / M{NumStr(info.Mag)}";
        if (info.Depth >= 0) est += info.Depth == 0 ? " / 深さごく浅い" : $" / 深さ約{info.Depth}km";
        lines.Add(est);
        if (info.IsAssumption) lines.Add("※ 仮定震源要素による速報です");
        if (info.IsDemo) lines.Add("※ デモ表示です（実際の速報ではありません）");
        string guide = EewActionGuide(info);
        if (guide.Length > 0) lines.Add("▶ 行動ガイド： " + guide);
        return string.Join("\n", lines);
    }

    /// <summary>震度予想レンジの表示 (to=99 は「程度以上」)</summary>
    public static string EewScaleRangeText(int from, int to)
    {
        if (to == 99 && from > 0) return $"{ScaleToStr(from)}程度以上";
        if (from == to) return ScaleToStr(from);
        return $"{ScaleToStr(from)}〜{ScaleToStr(to)}";
    }

    /// <summary>地域別予想の一覧テキスト</summary>
    public static string EewRegionalEstimateText(List<EewArea> areas)
        => string.Join("、", areas.Select(a => $"{a.Name} 震度{EewScaleRangeText(a.ScaleFrom, a.ScaleTo)}"));

    // ----------------------------------------
    // 📍 地点別の地域選択と到達予想
    // ----------------------------------------
    /// <summary>
    /// 地点に対応する EEW 地域を選ぶ。地点の都道府県内の地域だけを候補とし、
    /// 予報区名 (例: 南部) が地域名の末尾に一致するものを最優先 (+2)、
    /// 到達予想時刻があるものを次点 (+1) としてスコア付けする。
    /// </summary>
    public static EewArea? SelectEewArea(EewInfo info, Location loc)
    {
        string? prefName = PrefNameByCode(loc.PrefCode);
        if (prefName is null) return null;
        EewArea? best = null;
        int bestScore = -1;
        foreach (var area in info.Areas)
        {
            if (area.Pref != prefName) continue;
            int score = 0;
            if (area.Name.EndsWith(loc.AreaName)) score += 2;
            if (area.ArrivalTime is not null) score += 1;
            if (score > bestScore) { bestScore = score; best = area; }
        }
        return best;
    }

    /// <summary>2点間の地表面距離 (km、ハバサイン)</summary>
    private static double SurfaceDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double rad = Math.PI / 180.0;
        double dLat = (lat2 - lat1) * rad;
        double dLon = (lon2 - lon1) * rad;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371.0 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// 地点への到達予想。優先順位は デモ値 → 地域の発表値 → S波推定
    /// (震源距離 / 3.5km/s)。震源座標が無ければ null。
    /// </summary>
    public static (DateTime Arrival, bool Estimated)? EewArrival(EewInfo info, Location loc)
    {
        if (info.IsDemo && info.DemoArrival is { } demo) return (demo, false);
        var area = SelectEewArea(info, loc);
        if (area?.ArrivalTime is { } announced) return (announced, false);
        if ((info.Lat == 0 && info.Lon == 0) || !TryParseJstTime(info.OriginTime, out var origin))
            return null;
        int depth = info.Depth < 0 ? 10 : info.Depth;
        double surface = SurfaceDistanceKm(info.Lat, info.Lon, loc.Lat, loc.Lon);
        double dist = Math.Sqrt(surface * surface + depth * depth);
        return (origin.AddSeconds(dist / 3.5), true);
    }

    /// <summary>地点別の到達カウントダウン行を作る。到達から30秒を過ぎた
    /// 地点と、到達予想が無い地点は行にしない</summary>
    public static List<string> EewCountdownLines(EewInfo info, DateTime now)
    {
        var lines = new List<string>();
        foreach (var loc in Locations)
        {
            var arrival = EewArrival(info, loc);
            if (arrival is null) continue;
            double remain = (arrival.Value.Arrival - now).TotalSeconds;
            if (remain < -30) continue;
            if (remain <= 0)
                lines.Add($"{loc.Name}： 到達した可能性が高いです");
            else
                lines.Add($"{loc.Name}： あと約{(int)Math.Ceiling(remain)}秒（{(arrival.Value.Estimated ? "推定" : "予想")}）");
        }
        return lines;
    }

    /// <summary>表示レベル。発表からの経過で決め、表示上限を超えたら null</summary>
    public static string? EewLevel(EewInfo info, DateTime now)
    {
        if (info.IsCancel) return "info";
        if (!TryParseJstTime(info.OriginTime, out var origin)) return "warn";
        double age = (now - origin).TotalMinutes;
        if (age < 0) return "danger"; // 未来の発表 (時刻ずれ) は強調側に倒す
        if (age <= EewDangerMinutes) return "danger";
        if (age <= EewDisplayMinutes) return "warn";
        return null;
    }

    /// <summary>EEW を表示セクションに整形する (表示対象外なら null)</summary>
    public static Section? EewSection(EewInfo? info, DateTime now)
    {
        if (info is null) return null;
        string? level = EewLevel(info, now);
        if (level is null) return null;
        string body = EewBaseBody(info);
        if (!info.IsCancel)
        {
            if (info.Areas.Count > 0)
                body += "\n地域別予想: " + EewRegionalEstimateText(info.Areas);
            var countdown = EewCountdownLines(info, now);
            if (countdown.Count > 0)
                body += "\n" + string.Join("\n", countdown);
        }
        return new Section(EewTitle(info), body, level);
    }

    /// <summary>デモ表示用のサンプル EEW (到達まで30秒・予想震度5弱)</summary>
    public static EewInfo CreateSampleEew(DateTime now) => new()
    {
        OriginTime = now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
        AnnouncedTime = now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
        HypoName = "群馬県南部（デモ）",
        EstMaxScale = 45,
        Mag = 5.5,
        Depth = 10,
        Lat = Takasaki.Lat,
        Lon = Takasaki.Lon,
        IsWarn = true,
        IsDemo = true,
        DemoArrival = now.AddSeconds(30),
    };
}
