using System.Text.Json;

namespace AegisWin;

// ========================================
// ⚙️ 設定とキャッシュの保存 (%AppData%\AEGIS\)
// ========================================
// 📌 ユーザー設定は settings.json、起動直後に表示する前回ブリーフィングは
// briefing-cache.json に保存する。書き込みは一時ファイル経由のアトミック
// 置換にし、終了・強制終了の途中でもファイルが壊れたまま残らないようにする。
// 読み込みに失敗した場合は例外を投げず、初期値 (設定) / null (キャッシュ)
// で続行する (壊れたファイルでアプリが起動できなくなるのを防ぐため)。

/// <summary>ユーザー設定一式。プロパティ名がそのまま settings.json のキーになる</summary>
public sealed class AppSettings
{
    /// <summary>取得方式: "direct" (直接取得) | "server" (AEGISサーバーモード)</summary>
    public string Mode { get; set; } = "direct";

    /// <summary>サーバーモード時の接続先 (AEGIS serve のベース URL)</summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:8765";

    /// <summary>ユーザーが追加した任意地点</summary>
    public List<Location> CustomLocations { get; set; } = new();

    // ----------------------------------------
    // 🔔 トレイ常駐と通知
    // ----------------------------------------
    /// <summary>閉じる/最小化でトレイに格納する (既定 OFF)</summary>
    public bool MinimizeToTray { get; set; } = false;

    /// <summary>バルーン通知の有効/無効 (既定 ON)</summary>
    public bool NotifyEnabled { get; set; } = true;

    /// <summary>地震を通知する最大震度の下限 (震度スケール値。既定 30 = 震度3)</summary>
    public int NotifyQuakeMinScale { get; set; } = 30;

    /// <summary>注意報のみの発表も通知するか (既定 OFF = 警報・特別警報のみ)</summary>
    public bool NotifyWarningIncludeAdvisory { get; set; } = false;

    /// <summary>通知済みイベント ID (重複排除用。再起動後も保持する)</summary>
    public List<string> NotifiedIds { get; set; } = new();

    /// <summary>通知チェック実施済みフラグ。false の間は初回扱いで、候補を
    /// すべて NotifiedIds に登録するだけのシーディングを行い通知しない
    /// (起動直後に既存イベントを一斉通知するフラッドの防止)</summary>
    public bool NotifySeeded { get; set; } = false;

    // ----------------------------------------
    // 📡 A.A.S (Aegis Alert System) 連携
    // ----------------------------------------
    /// <summary>A.A.S からの取得を有効にする (既定 OFF。URL とトークンの
    /// 両方が設定されているときだけ実際に接続する)</summary>
    public bool A2sEnabled { get; set; } = false;

    /// <summary>A.A.S のベース URL (例: https://a2s.example.net)</summary>
    public string A2sUrl { get; set; } = "";

    /// <summary>A.A.S の認証トークン (Authorization: Bearer で送る)</summary>
    public string A2sToken { get; set; } = "";
}

/// <summary>設定・キャッシュの読み書き。保存先は %AppData%\AEGIS\</summary>
public static class SettingsStore
{
    public static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AEGIS");
    public static string SettingsPath => Path.Combine(DirPath, "settings.json");
    public static string CachePath => Path.Combine(DirPath, "briefing-cache.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception) { /* 壊れた設定ファイルは初期値で起動する */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch (Exception) { /* 保存失敗は致命的ではない */ }
    }

    /// <summary>ブリーフィングのキャッシュ (保存時刻とセクション一覧)</summary>
    public sealed class BriefingCache
    {
        public long SavedAtMillis { get; set; }
        public List<Section> Sections { get; set; } = new();
    }

    public static void SaveCache(List<Section> sections)
    {
        // 📌 取得失敗を含む内容はキャッシュしない (次回起動時に失敗表示を
        // 「前回表示」として見せ続けないため)
        foreach (var s in sections)
        {
            if (s.Body.Contains("取得できませんでした") || s.Title.Contains("取得エラー")) return;
        }
        try
        {
            var cache = new BriefingCache
            {
                SavedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sections = sections,
            };
            WriteAtomic(CachePath, JsonSerializer.Serialize(cache, JsonOpts));
        }
        catch (Exception) { /* キャッシュ保存失敗は致命的ではない */ }
    }

    public static BriefingCache? LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var loaded = JsonSerializer.Deserialize<BriefingCache>(File.ReadAllText(CachePath), JsonOpts);
                if (loaded is not null && loaded.Sections.Count > 0) return loaded;
            }
        }
        catch (Exception) { /* 壊れたキャッシュは無視する */ }
        return null;
    }

    // 📁 一時ファイルへ書いてから置換するアトミック書き込み
    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(DirPath);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
