using System.Runtime.InteropServices;
using System.Text;

namespace AegisWin;

// ========================================
// 🚀 エントリポイント
// ========================================
// 📌 単一インスタンス: Mutex で多重起動を防ぎ、既存ウィンドウがあれば
// 前面に出して終了する。
// 📌 CLI モード: --version / --cli (ブリーフィングを標準出力へ) /
// --diag (接続診断を標準出力へ)。コンソールへの出力は AttachConsole で
// 呼び出し元へつなぎ、文字コードは UTF-8 にそろえる。

internal static class Program
{
    private const string MutexName = "AEGIS-Windows-CS.SingleInstance";
    private const string WindowTitle = "🛡️ AEGIS — 地震・津波・気象";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int AttachParentProcess = -1;
    private const int SwRestore = 9;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            PrintToConsole($"AEGIS {Data.Version}");
            return;
        }
        if (args.Contains("--cli") || args.Contains("--diag"))
        {
            RunCliAsync(args.Contains("--diag")).GetAwaiter().GetResult();
            return;
        }

        // 🪟 二重起動時は既存ウィンドウを前面に出して終わる
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            var hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static async Task RunCliAsync(bool diag)
    {
        AttachConsole(AttachParentProcess);
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            // 設定を読み、データ層へ反映してから実行する
            var settings = SettingsStore.Load();
            ApplySettings(settings);
            if (diag)
            {
                var results = await Data.DiagnoseAsync();
                Console.WriteLine(Data.FormatDiag(results));
            }
            else
            {
                var sections = await Data.BuildBriefingAsync();
                foreach (var s in sections)
                {
                    Console.WriteLine($"== {s.Title} ==");
                    Console.WriteLine(s.Body);
                    Console.WriteLine();
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"エラー: {Data.FriendlyError(e)}");
        }
    }

    private static void PrintToConsole(string text)
    {
        AttachConsole(AttachParentProcess);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(text);
    }

    /// <summary>設定をデータ層の静的状態へ反映する (GUI と CLI の共通処理)</summary>
    internal static void ApplySettings(AppSettings settings)
    {
        Data.Mode = settings.Mode;
        Data.ServerUrl = settings.ServerUrl;
        Data.A2sEnabled = settings.A2sEnabled;
        Data.A2sBaseUrl = settings.A2sUrl;
        Data.A2sToken = settings.A2sToken;
        Data.ResetLocations();
        foreach (var loc in settings.CustomLocations) Data.Locations.Add(loc);
    }
}
