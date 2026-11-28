using System.Diagnostics;

namespace AegisWin;

// ========================================
// 🖥️ メインウィンドウ
// ========================================
// 📌 構成: 上部に EEW ピン留めカード (速報時のみ)、その下にタブ
// (ブリーフィング / 地震 / レーダー / ニュース / 設定)。
// 📌 タイマー: 全体更新 5分、通知チェック 60秒、EEW カウントダウン
// 1秒、地震タブ表示中の自動更新 30秒。
// 📌 起動直後は前回キャッシュを即表示し、裏で最新化する (起動を
// 待たせない)。通知は初回シーディングで既存分を既読化してから始める。

public sealed partial class MainForm : Form
{
    private AppSettings _settings = new();

    // 🧩 主要コントロール
    private readonly TabControl _tabs = new();
    private readonly FlowLayoutPanel _briefingHost = MakeCardHost();
    private readonly Label _statusLabel = MakeLabel("");
    private readonly Panel _eewBanner = new();
    private readonly Label _eewTitle = MakeLabel("", BigFont);
    private readonly Label _eewBody = MakeLabel("");

    // 🌤 天気グリッド (ブリーフィングタブ下部)
    private readonly ComboBox _gridLocation = new();
    private readonly DataGridView _hourlyGrid = new();
    private readonly DataGridView _weeklyGrid = new();

    // 🌋 地震タブ
    private readonly Label _quakePlace = MakeLabel("", BigFont);
    private readonly Label _quakeScale = MakeLabel("", BoldFont);
    private readonly Label _quakeDetail = MakeLabel("");
    private readonly Label _quakeObservation = MakeLabel("");
    private readonly ListBox _quakeList = new();
    private readonly Button _mapButton = MakeButton("🗺 地図で見る");
    private List<Quake> _quakes = new();

    // 🌧️ レーダータブ
    private readonly ComboBox _radarLocation = new();
    private readonly PictureBox _radarPicture = new();
    private readonly Label _radarNote = MakeLabel("");
    private bool _radarLoaded;
    private (string Basetime, string Validtime, DateTime At)? _jmaFrameCache;

    // 📰 ニュースタブ
    private readonly ListBox _newsList = new();
    private List<Data.NewsItem> _news = new();

    // ⚙️ 設定タブ
    private readonly RadioButton _modeDirect = new() { Text = "直接取得", AutoSize = true };
    private readonly RadioButton _modeServer = new() { Text = "サーバーモード", AutoSize = true };
    private readonly TextBox _serverUrl = new();
    private readonly Label _serverTestResult = MakeLabel("");
    private readonly CheckBox _a2sEnabled = new() { Text = "A.A.S 連携を有効にする", AutoSize = true };
    private readonly TextBox _a2sUrl = new();
    private readonly TextBox _a2sToken = new();
    private readonly CheckBox _trayCheck = new() { Text = "閉じる/最小化でトレイに格納する", AutoSize = true };
    private readonly CheckBox _notifyEnabled = new() { Text = "バルーン通知を有効にする", AutoSize = true };
    private readonly ComboBox _notifyScale = new();
    private readonly CheckBox _notifyAdvisory = new() { Text = "注意報のみの発表も通知する", AutoSize = true };
    private readonly ListBox _customList = new();
    private readonly TextBox _locName = new();
    private readonly ComboBox _locPref = new();
    private readonly ComboBox _locArea = new();
    private readonly TextBox _locLat = new();
    private readonly TextBox _locLon = new();
    private readonly TextBox _diagResult = new();

    // 🔔 トレイ
    private readonly NotifyIcon _tray = new();
    private bool _exitRequested;

    // ⏱️ タイマーと状態
    private readonly System.Windows.Forms.Timer _mainTimer = new() { Interval = 300000 };
    private readonly System.Windows.Forms.Timer _notifyTimer = new() { Interval = 60000 };
    private readonly System.Windows.Forms.Timer _eewTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _quakeTimer = new() { Interval = 30000 };
    private Data.EewInfo? _currentEew;
    private CancellationTokenSource? _streamCts;
    private bool _refreshing;

    public MainForm()
    {
        Text = "🛡️ AEGIS — 地震・津波・気象";
        Size = new Size(760, 640);
        MinimumSize = new Size(560, 420);
        BackColor = Bg;
        Font = BaseFont;

        BuildLayout();
        LoadSettingsIntoUi();
        SetupTray();

        _mainTimer.Tick += async (_, _) => await RefreshAllAsync();
        _notifyTimer.Tick += async (_, _) => await NotifyCheckAsync();
        _eewTimer.Tick += (_, _) => UpdateEewBanner();
        _quakeTimer.Tick += async (_, _) =>
        {
            if (_tabs.SelectedTab?.Text == "地震") await LoadQuakesAsync();
        };

        Shown += async (_, _) =>
        {
            ShowCachedBriefing();
            RestartStream();
            _mainTimer.Start();
            _notifyTimer.Start();
            _eewTimer.Start();
            _quakeTimer.Start();
            await RefreshAllAsync();
            await NotifyCheckAsync();
        };
        FormClosing += OnFormClosing;
    }

    // ----------------------------------------
    // 🧱 レイアウト構築
    // ----------------------------------------
    private void BuildLayout()
    {
        // ヘッダー
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Bg };
        var title = MakeLabel("🛡️ AEGIS", TitleFont);
        title.Location = new Point(10, 8);
        header.Controls.Add(title);
        var refresh = MakeButton("🔄 更新");
        refresh.Location = new Point(180, 10);
        refresh.Click += async (_, _) => await RefreshAllAsync();
        header.Controls.Add(refresh);
        _statusLabel.Location = new Point(270, 14);
        _statusLabel.ForeColor = SubColor;
        header.Controls.Add(_statusLabel);
        Controls.Add(header);

        // 🚨 EEW ピン留めバナー (通常は非表示)
        _eewBanner.Dock = DockStyle.Top;
        _eewBanner.Height = 92;
        _eewBanner.BackColor = Color.FromArgb(0x3A, 0x1B, 0x24);
        _eewBanner.Visible = false;
        _eewTitle.Location = new Point(12, 8);
        _eewBanner.Controls.Add(_eewTitle);
        _eewBody.Location = new Point(12, 42);
        _eewBanner.Controls.Add(_eewBody);
        Controls.Add(_eewBanner);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Controls.Add(BuildBriefingTab());
        _tabs.Controls.Add(BuildQuakeTab());
        _tabs.Controls.Add(BuildRadarTab());
        _tabs.Controls.Add(BuildNewsTab());
        _tabs.Controls.Add(BuildSettingsTab());
        Controls.Add(_tabs);
        _tabs.BringToFront();
    }

    private TabPage BuildBriefingTab()
    {
        var tab = new TabPage("ブリーフィング") { BackColor = Bg };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 216, BackColor = Bg };
        _gridLocation.DropDownStyle = ComboBoxStyle.DropDownList;
        _gridLocation.Location = new Point(10, 6);
        _gridLocation.Width = 160;
        _gridLocation.SelectedIndexChanged += async (_, _) => await LoadWeatherGridsAsync();
        bottom.Controls.Add(_gridLocation);
        SetupGrid(_hourlyGrid, new[] { "時刻", "天気", "気温", "降水確率", "降水量" });
        _hourlyGrid.Location = new Point(10, 36);
        _hourlyGrid.Size = new Size(350, 170);
        bottom.Controls.Add(_hourlyGrid);
        SetupGrid(_weeklyGrid, new[] { "日付", "天気", "最低", "最高", "降水確率", "降水量" });
        _weeklyGrid.Location = new Point(372, 36);
        _weeklyGrid.Size = new Size(350, 170);
        bottom.Controls.Add(_weeklyGrid);
        tab.Controls.Add(bottom);
        tab.Controls.Add(_briefingHost);
        return tab;
    }

    private static void SetupGrid(DataGridView grid, string[] columns)
    {
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = CardBg;
        grid.ForeColor = TextColor;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0x2A, 0x36, 0x4E);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
        grid.DefaultCellStyle.BackColor = CardBg;
        grid.DefaultCellStyle.ForeColor = TextColor;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x2A, 0x36, 0x4E);
        foreach (var c in columns) grid.Columns.Add(c, c);
    }

    private TabPage BuildQuakeTab()
    {
        var tab = new TabPage("地震") { BackColor = Bg };
        var top = new Panel { Dock = DockStyle.Top, Height = 190, BackColor = CardBg, Padding = new Padding(12) };
        _quakePlace.Location = new Point(12, 10);
        top.Controls.Add(_quakePlace);
        _quakeScale.Location = new Point(12, 48);
        _quakeScale.AutoSize = false;
        _quakeScale.Size = new Size(110, 30);
        _quakeScale.TextAlign = ContentAlignment.MiddleCenter;
        _quakeScale.ForeColor = Color.White;
        top.Controls.Add(_quakeScale);
        _quakeDetail.Location = new Point(12, 86);
        _quakeDetail.MaximumSize = new Size(700, 0);
        top.Controls.Add(_quakeDetail);
        _quakeObservation.Location = new Point(12, 130);
        _quakeObservation.ForeColor = SubColor;
        _quakeObservation.MaximumSize = new Size(700, 0);
        top.Controls.Add(_quakeObservation);
        _mapButton.Location = new Point(600, 10);
        _mapButton.Click += (_, _) => OpenSelectedQuakeMap();
        top.Controls.Add(_mapButton);
        tab.Controls.Add(top);

        _quakeList.Dock = DockStyle.Fill;
        _quakeList.BackColor = Bg;
        _quakeList.ForeColor = TextColor;
        _quakeList.Font = BaseFont;
        _quakeList.SelectedIndexChanged += (_, _) => ShowSelectedQuake();
        tab.Controls.Add(_quakeList);
        _quakeList.BringToFront();
        return tab;
    }

    private TabPage BuildRadarTab()
    {
        var tab = new TabPage("レーダー") { BackColor = Bg };
        var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Bg };
        _radarLocation.DropDownStyle = ComboBoxStyle.DropDownList;
        _radarLocation.Location = new Point(10, 8);
        _radarLocation.Width = 160;
        _radarLocation.SelectedIndexChanged += async (_, _) => await LoadRadarAsync();
        top.Controls.Add(_radarLocation);
        var reload = MakeButton("🔄 レーダー更新");
        reload.Location = new Point(182, 6);
        reload.Click += async (_, _) => await LoadRadarAsync();
        top.Controls.Add(reload);
        _radarNote.Location = new Point(330, 12);
        _radarNote.ForeColor = SubColor;
        top.Controls.Add(_radarNote);
        tab.Controls.Add(top);
        _radarPicture.Dock = DockStyle.Fill;
        _radarPicture.SizeMode = PictureBoxSizeMode.Zoom;
        _radarPicture.BackColor = Bg;
        tab.Controls.Add(_radarPicture);
        _radarPicture.BringToFront();
        tab.Enter += async (_, _) =>
        {
            if (!_radarLoaded) { _radarLoaded = true; await LoadRadarAsync(); }
        };
        return tab;
    }

    private TabPage BuildNewsTab()
    {
        var tab = new TabPage("ニュース") { BackColor = Bg };
        _newsList.Dock = DockStyle.Fill;
        _newsList.BackColor = Bg;
        _newsList.ForeColor = TextColor;
        _newsList.Font = BaseFont;
        _newsList.DoubleClick += (_, _) => OpenSelectedNews();
        tab.Controls.Add(_newsList);
        return tab;
    }

    private TabPage BuildSettingsTab()
    {
        var tab = new TabPage("設定") { BackColor = Bg };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Bg,
            Padding = new Padding(12),
        };
        tab.Controls.Add(flow);

        flow.Controls.Add(MakeLabel("取得方式", BoldFont));
        _modeDirect.Font = BaseFont; _modeDirect.ForeColor = TextColor;
        _modeServer.Font = BaseFont; _modeServer.ForeColor = TextColor;
        flow.Controls.Add(_modeDirect);
        flow.Controls.Add(_modeServer);
        flow.Controls.Add(MakeLabel("サーバー URL (サーバーモード時)"));
        _serverUrl.Width = 360;
        flow.Controls.Add(_serverUrl);
        var testBtn = MakeButton("接続テスト");
        testBtn.Click += async (_, _) =>
        {
            _serverTestResult.Text = "接続中…";
            bool ok = await Data.TestServerConnectionAsync(_serverUrl.Text);
            _serverTestResult.Text = ok ? "○ 接続できました" : "× 接続できませんでした";
        };
        flow.Controls.Add(testBtn);
        flow.Controls.Add(_serverTestResult);

        flow.Controls.Add(MakeLabel("A.A.S (Aegis Alert System) 連携", BoldFont));
        _a2sEnabled.Font = BaseFont; _a2sEnabled.ForeColor = TextColor;
        flow.Controls.Add(_a2sEnabled);
        flow.Controls.Add(MakeLabel("A.A.S URL"));
        _a2sUrl.Width = 360;
        flow.Controls.Add(_a2sUrl);
        flow.Controls.Add(MakeLabel("A.A.S トークン"));
        _a2sToken.Width = 360;
        _a2sToken.UseSystemPasswordChar = true;
        flow.Controls.Add(_a2sToken);
        flow.Controls.Add(MakeLabel("※ 地震・津波・EEW・警報・台風は A.A.S からのみ取得します",
            BaseFont, SubColor));

        flow.Controls.Add(MakeLabel("トレイ・通知", BoldFont));
        _trayCheck.Font = BaseFont; _trayCheck.ForeColor = TextColor;
        flow.Controls.Add(_trayCheck);
        _notifyEnabled.Font = BaseFont; _notifyEnabled.ForeColor = TextColor;
        flow.Controls.Add(_notifyEnabled);
        flow.Controls.Add(MakeLabel("地震を通知する最大震度の下限"));
        _notifyScale.DropDownStyle = ComboBoxStyle.DropDownList;
        _notifyScale.Width = 160;
        foreach (var (label, _) in NotifyScaleOptions) _notifyScale.Items.Add(label);
        flow.Controls.Add(_notifyScale);
        _notifyAdvisory.Font = BaseFont; _notifyAdvisory.ForeColor = TextColor;
        flow.Controls.Add(_notifyAdvisory);

        flow.Controls.Add(MakeLabel("地点の追加", BoldFont));
        flow.Controls.Add(MakeLabel("名前"));
        _locName.Width = 240;
        flow.Controls.Add(_locName);
        flow.Controls.Add(MakeLabel("都道府県"));
        _locPref.DropDownStyle = ComboBoxStyle.DropDownList;
        _locPref.Width = 240;
        foreach (var (name, _) in Data.Prefectures) _locPref.Items.Add(name);
        _locPref.SelectedIndexChanged += async (_, _) => await LoadAreaCandidatesAsync();
        flow.Controls.Add(_locPref);
        flow.Controls.Add(MakeLabel("予報区域"));
        _locArea.DropDownStyle = ComboBoxStyle.DropDownList;
        _locArea.Width = 240;
        flow.Controls.Add(_locArea);
        flow.Controls.Add(MakeLabel("緯度"));
        _locLat.Width = 240;
        flow.Controls.Add(_locLat);
        flow.Controls.Add(MakeLabel("経度"));
        _locLon.Width = 240;
        flow.Controls.Add(_locLon);
        var addBtn = MakeButton("地点を追加");
        addBtn.Click += (_, _) => AddCustomLocation();
        flow.Controls.Add(addBtn);
        _customList.Width = 360;
        _customList.Height = 90;
        _customList.BackColor = CardBg;
        _customList.ForeColor = TextColor;
        flow.Controls.Add(_customList);
        var delBtn = MakeButton("選択した地点を削除");
        delBtn.Click += (_, _) => DeleteCustomLocation();
        flow.Controls.Add(delBtn);

        flow.Controls.Add(MakeLabel("診断・デモ", BoldFont));
        var diagBtn = MakeButton("🩺 接続診断を実行");
        diagBtn.Click += async (_, _) =>
        {
            _diagResult.Text = "診断中…";
            var results = await Data.DiagnoseAsync();
            _diagResult.Text = Data.FormatDiag(results);
        };
        flow.Controls.Add(diagBtn);
        _diagResult.Multiline = true;
        _diagResult.ReadOnly = true;
        _diagResult.Width = 520;
        _diagResult.Height = 130;
        _diagResult.BackColor = CardBg;
        _diagResult.ForeColor = TextColor;
        flow.Controls.Add(_diagResult);
        var demoBtn = MakeButton("🚨 EEW デモ表示");
        demoBtn.Click += (_, _) =>
        {
            _currentEew = Data.CreateSampleEew(Data.NowInTokyo());
            UpdateEewBanner();
        };
        flow.Controls.Add(demoBtn);

        var saveBtn = MakeButton("💾 設定を保存して再読み込み");
        saveBtn.Click += async (_, _) =>
        {
            SaveSettingsFromUi();
            RestartStream();
            await RefreshAllAsync();
        };
        flow.Controls.Add(saveBtn);
        return tab;
    }

    private static readonly (string Label, int Scale)[] NotifyScaleOptions =
    {
        ("震度1以上", 10), ("震度2以上", 20), ("震度3以上", 30),
        ("震度4以上", 40), ("震度5弱以上", 45),
    };

    // ----------------------------------------
    // ⚙️ 設定の読み書き
    // ----------------------------------------
    private void LoadSettingsIntoUi()
    {
        _settings = SettingsStore.Load();
        Program.ApplySettings(_settings);

        _modeDirect.Checked = _settings.Mode != "server";
        _modeServer.Checked = _settings.Mode == "server";
        _serverUrl.Text = _settings.ServerUrl;
        _a2sEnabled.Checked = _settings.A2sEnabled;
        _a2sUrl.Text = _settings.A2sUrl;
        _a2sToken.Text = _settings.A2sToken;
        _trayCheck.Checked = _settings.MinimizeToTray;
        _notifyEnabled.Checked = _settings.NotifyEnabled;
        _notifyAdvisory.Checked = _settings.NotifyWarningIncludeAdvisory;
        int idx = Array.FindIndex(NotifyScaleOptions, o => o.Scale == _settings.NotifyQuakeMinScale);
        _notifyScale.SelectedIndex = idx >= 0 ? idx : 2;
        RefreshLocationCombos();
    }

    private void RefreshLocationCombos()
    {
        _customList.Items.Clear();
        foreach (var loc in _settings.CustomLocations) _customList.Items.Add(loc.Name);
        _gridLocation.Items.Clear();
        _radarLocation.Items.Clear();
        foreach (var loc in Data.Locations)
        {
            _gridLocation.Items.Add(loc.Name);
            _radarLocation.Items.Add(loc.Name);
        }
        if (_gridLocation.Items.Count > 0) _gridLocation.SelectedIndex = 0;
        if (_radarLocation.Items.Count > 0) _radarLocation.SelectedIndex = 0;
    }

    private void SaveSettingsFromUi()
    {
        _settings.Mode = _modeServer.Checked ? "server" : "direct";
        _settings.ServerUrl = _serverUrl.Text.Trim();
        _settings.A2sEnabled = _a2sEnabled.Checked;
        _settings.A2sUrl = _a2sUrl.Text.Trim();
        _settings.A2sToken = _a2sToken.Text.Trim();
        _settings.MinimizeToTray = _trayCheck.Checked;
        _settings.NotifyEnabled = _notifyEnabled.Checked;
        _settings.NotifyWarningIncludeAdvisory = _notifyAdvisory.Checked;
        if (_notifyScale.SelectedIndex >= 0)
            _settings.NotifyQuakeMinScale = NotifyScaleOptions[_notifyScale.SelectedIndex].Scale;
        SettingsStore.Save(_settings);
        Program.ApplySettings(_settings);
        Data.ClearA2sState();
        RefreshLocationCombos();
    }

    private async Task LoadAreaCandidatesAsync()
    {
        _locArea.Items.Clear();
        if (_locPref.SelectedIndex < 0) return;
        string code = Data.Prefectures[_locPref.SelectedIndex].Code;
        try
        {
            var areas = await Data.ForecastAreasAsync(code);
            foreach (var (name, _) in areas) _locArea.Items.Add(name);
            if (_locArea.Items.Count > 0) _locArea.SelectedIndex = 0;
        }
        catch (Exception) { /* 候補が取れなくても手入力は妨げない */ }
    }

    private void AddCustomLocation()
    {
        string name = _locName.Text.Trim();
        if (name.Length == 0) { MessageBox.Show("名前を入力してください"); return; }
        if (_locPref.SelectedIndex < 0) { MessageBox.Show("都道府県を選んでください"); return; }
        if (!double.TryParse(_locLat.Text, out double lat)
            || !double.TryParse(_locLon.Text, out double lon))
        {
            MessageBox.Show("緯度・経度は数値で入力してください");
            return;
        }
        var (error, warning) = Data.ValidateLocationCoords(lat, lon);
        if (error is not null) { MessageBox.Show(error); return; }
        if (warning is not null
            && MessageBox.Show(warning, "確認", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        string prefCode = Data.Prefectures[_locPref.SelectedIndex].Code;
        string area = _locArea.SelectedItem?.ToString() ?? "";
        _settings.CustomLocations.Add(new Location(name, lat, lon, prefCode, area));
        SettingsStore.Save(_settings);
        Program.ApplySettings(_settings);
        RefreshLocationCombos();
    }

    private void DeleteCustomLocation()
    {
        int idx = _customList.SelectedIndex;
        if (idx < 0 || idx >= _settings.CustomLocations.Count) return;
        _settings.CustomLocations.RemoveAt(idx);
        SettingsStore.Save(_settings);
        Program.ApplySettings(_settings);
        RefreshLocationCombos();
    }

    // ----------------------------------------
    // 🔔 トレイ
    // ----------------------------------------
    private void SetupTray()
    {
        _tray.Icon = SystemIcons.Shield;
        _tray.Text = "AEGIS";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => ShowFromTray());
        menu.Items.Add("終了", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_settings.MinimizeToTray && !_exitRequested
            && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _mainTimer.Stop();
        _notifyTimer.Stop();
        _eewTimer.Stop();
        _quakeTimer.Stop();
        _streamCts?.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
    }

    // ----------------------------------------
    // 🔄 更新処理
    // ----------------------------------------
    private void ShowCachedBriefing()
    {
        var cache = SettingsStore.LoadCache();
        if (cache is null) return;
        FillCards(_briefingHost, cache.Sections);
        var savedAt = DateTimeOffset.FromUnixTimeMilliseconds(cache.SavedAtMillis);
        var nowJst = new DateTimeOffset(Data.NowInTokyo());
        _statusLabel.Text = $"前回表示（{Data.FormatAgo(savedAt, nowJst)}）";
    }

    private async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _statusLabel.Text = "更新中…";
        try
        {
            var sections = await Data.BuildBriefingAsync()
                .WaitAsync(TimeSpan.FromSeconds(120));
            FillCards(_briefingHost, sections);
            SettingsStore.SaveCache(sections);
            _statusLabel.Text = $"最終更新 {Data.NowInTokyo():HH:mm:ss}";
        }
        catch (Exception e)
        {
            _statusLabel.Text = $"更新に失敗しました（{Data.FriendlyError(e)}）";
        }
        try { await LoadWeatherGridsAsync(); } catch (Exception) { }
        try { await LoadQuakesAsync(); } catch (Exception) { }
        try { await LoadNewsAsync(); } catch (Exception) { }
        if (_radarLoaded) { try { await LoadRadarAsync(); } catch (Exception) { } }
        try { _currentEew = await Data.EewInfoAsync() ?? _currentEew; } catch (Exception) { }
        UpdateEewBanner();
        _refreshing = false;
    }

    private async Task LoadWeatherGridsAsync()
    {
        int idx = _gridLocation.SelectedIndex;
        if (idx < 0 || idx >= Data.Locations.Count) return;
        var loc = Data.Locations[idx];
        string json = await Data.FetchAsync(Data.OpenMeteoUrl(loc), acceptJson: true);
        var root = Data.ParseJson(json);
        var now = Data.NowInTokyo();
        _hourlyGrid.Rows.Clear();
        foreach (var e in Data.ParseHourlyEntries(root, now))
        {
            _hourlyGrid.Rows.Add(
                e.Time.ToString("M/d H時"),
                e.Code is { } c ? Data.WmoText(c) : "",
                e.Temp is { } t ? $"{Data.RoundToLong(t)}℃" : "",
                e.Pop is { } p ? $"{p}%" : "",
                e.Precip is { } pr && pr > 0 ? $"{Data.NumStr(Data.Round1(pr))}mm" : "");
        }
        _weeklyGrid.Rows.Clear();
        foreach (var d in Data.ParseDailyEntries(root))
        {
            _weeklyGrid.Rows.Add(
                d.Date.ToString("M/d (ddd)"),
                d.Code is { } c ? Data.WmoText(c) : "",
                d.Min is { } mn ? $"{Data.RoundToLong(mn)}℃" : "",
                d.Max is { } mx ? $"{Data.RoundToLong(mx)}℃" : "",
                d.Pop is { } p ? $"{p}%" : "",
                d.PrecipSum is { } pr && pr > 0 ? $"{Data.NumStr(Data.Round1(pr))}mm" : "");
        }
    }

    // ----------------------------------------
    // 🌋 地震タブ
    // ----------------------------------------
    private async Task LoadQuakesAsync()
    {
        _quakes = await Data.QuakeItemsAsync(30);
        _quakeList.Items.Clear();
        foreach (var q in _quakes)
        {
            string when = Data.TryParseJstTime(q.Time, out var t) ? t.ToString("MM/dd HH:mm") : q.Time;
            _quakeList.Items.Add($"{when} {q.Place} 震度{Data.ScaleToStr(q.MaxScale)}");
        }
        if (_quakes.Count > 0) _quakeList.SelectedIndex = 0;
        else
        {
            _quakePlace.Text = "地震情報はありません";
            _quakeScale.Text = "";
            _quakeDetail.Text = Data.A2sConfigured
                ? "A.A.S から地震情報が届いていません" : "A.A.S が未設定です（設定タブで有効にしてください）";
            _quakeObservation.Text = "";
        }
    }

    private void ShowSelectedQuake()
    {
        int idx = _quakeList.SelectedIndex;
        if (idx < 0 || idx >= _quakes.Count) return;
        var q = _quakes[idx];
        _quakePlace.Text = q.Place;
        _quakeScale.Text = $"震度 {Data.ScaleToStr(q.MaxScale)}";
        _quakeScale.BackColor = ScaleColor(q.MaxScale);
        _quakeDetail.Text = q.Detail;
        _quakeObservation.Text = q.Observation;
        _mapButton.Enabled = q.Lat != 0 || q.Lon != 0;
    }

    private void OpenSelectedQuakeMap()
    {
        int idx = _quakeList.SelectedIndex;
        if (idx < 0 || idx >= _quakes.Count) return;
        var q = _quakes[idx];
        if (q.Lat == 0 && q.Lon == 0) return;
        string url = $"https://www.openstreetmap.org/?mlat={q.Lat}&mlon={q.Lon}#map=8/{q.Lat}/{q.Lon}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ----------------------------------------
    // 📰 ニュースタブ
    // ----------------------------------------
    private async Task LoadNewsAsync()
    {
        _news = await Data.FetchNewsAsync();
        _newsList.Items.Clear();
        foreach (var n in _news)
        {
            string head = Data.IsDisasterNews(n.Title, n.Summary) ? "⚠ " : "";
            _newsList.Items.Add($"{head}{n.Title} — {n.Summary}");
        }
    }

    private void OpenSelectedNews()
    {
        int idx = _newsList.SelectedIndex;
        if (idx < 0 || idx >= _news.Count) return;
        Process.Start(new ProcessStartInfo(_news[idx].Link) { UseShellExecute = true });
    }

    // ----------------------------------------
    // 🚨 EEW バナー (1秒更新)
    // ----------------------------------------
    private void UpdateEewBanner()
    {
        if (_currentEew is null) { _eewBanner.Visible = false; return; }
        var section = Data.EewSection(_currentEew, Data.NowInTokyo());
        if (section is null)
        {
            _currentEew = null;
            _eewBanner.Visible = false;
            return;
        }
        _eewBanner.Visible = true;
        _eewBanner.BackColor = section.Level switch
        {
            "danger" => Color.FromArgb(0x3A, 0x1B, 0x24),
            "warn" => Color.FromArgb(0x3A, 0x2E, 0x1B),
            _ => CardBg,
        };
        _eewTitle.Text = section.Title;
        _eewTitle.ForeColor = LevelColor(section.Level);
        _eewBody.Text = section.Body;
    }

    // ----------------------------------------
    // 🔔 通知チェック (60秒)
    // ----------------------------------------
    private async Task NotifyCheckAsync()
    {
        if (!_settings.NotifyEnabled) return;
        List<Data.NotifyCandidate> candidates;
        try { candidates = await Data.FetchNotifyCandidatesAsync(); }
        catch (Exception) { return; }

        var notified = new HashSet<string>(_settings.NotifiedIds);
        if (!_settings.NotifySeeded)
        {
            // 🌱 初回は既存候補をすべて既読化するだけ (フラッド防止)
            foreach (var c in candidates) notified.Add(c.Id);
            _settings.NotifiedIds = Data.PruneNotifiedIds(notified.ToList());
            _settings.NotifySeeded = true;
            SettingsStore.Save(_settings);
            return;
        }

        bool changed = false;
        foreach (var cand in candidates)
        {
            bool notify;
            if (cand.Kind == "eew")
            {
                // EEW は震度上昇時のみ再通知する専用判定を使う
                var info = await Data.EewInfoAsync();
                notify = info is not null && Data.ShouldNotifyEew(info, notified)
                    && cand.Id == Data.EewNotifyId(info);
            }
            else
            {
                notify = !notified.Contains(cand.Id) && Data.ShouldNotify(cand,
                    _settings.NotifyQuakeMinScale, _settings.NotifyWarningIncludeAdvisory);
            }
            if (notify)
            {
                _tray.ShowBalloonTip(15000, cand.Title, cand.Body, ToolTipIcon.Warning);
                notified.Add(cand.Id);
                changed = true;
            }
            else if (cand.Kind != "eew" && Data.ShouldNotify(cand,
                         _settings.NotifyQuakeMinScale, _settings.NotifyWarningIncludeAdvisory))
            {
                notified.Add(cand.Id);
                changed = true;
            }
        }
        if (changed)
        {
            _settings.NotifiedIds = Data.PruneNotifiedIds(notified.ToList());
            SettingsStore.Save(_settings);
        }
    }

    // ----------------------------------------
    // 📶 A.A.S ストリーム
    // ----------------------------------------
    private void RestartStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        if (!Data.A2sConfigured) return;
        _streamCts = new CancellationTokenSource();
        var client = new A2sStreamClient(Data.A2sBaseUrl, Data.A2sToken,
            (ev, replay) =>
            {
                var result = Data.NoteA2sEvent(ev, replay);
                if (result != Data.A2sAcceptResult.Duplicate && IsHandleCreated)
                {
                    BeginInvoke(async () =>
                    {
                        try { _currentEew = await Data.EewInfoAsync() ?? _currentEew; }
                        catch (Exception) { }
                        UpdateEewBanner();
                        await LoadQuakesAsync();
                        await NotifyCheckAsync();
                    });
                }
            });
        _ = client.RunAsync(_streamCts.Token);
    }

    // ----------------------------------------
    // 🌧️ レーダータブ (気象庁ナウキャスト主 / RainViewer 代替)
    // ----------------------------------------
    private async Task LoadRadarAsync()
    {
        int idx = _radarLocation.SelectedIndex;
        if (idx < 0 || idx >= Data.Locations.Count) return;
        var loc = Data.Locations[idx];
        var now = Data.NowInTokyo();
        var (cx, cy) = Data.LatLonToTile(loc.Lat, loc.Lon, 8);

        var bmp = new Bitmap(768, 768);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x11, 0x18, 0x28));
            // 🗺️ 地理院タイル (ベース地図) を 3×3 に敷く
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    await DrawTileAsync(g, Data.GsiTileUrl(8, cx + dx, cy + dy),
                        (dx + 1) * 256, (dy + 1) * 256, 256);

            string note;
            try
            {
                // 🌧️ 気象庁ナウキャスト (10分以内ならフレームを使い回す)
                string basetime, validtime;
                if (_jmaFrameCache is { } cache
                    && Data.RadarFrameCacheReusable(cache.At, now))
                {
                    (basetime, validtime) = (cache.Basetime, cache.Validtime);
                }
                else
                {
                    (basetime, validtime) = await Data.FetchJmaRadarFrameAsync();
                    _jmaFrameCache = (basetime, validtime, now);
                }
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        await DrawTileAsync(g,
                            Data.JmaTileUrl(basetime, validtime, 8, cx + dx, cy + dy),
                            (dx + 1) * 256, (dy + 1) * 256, 256);
                var frameTime = ParseJmaValidtime(validtime);
                note = $"{Data.RadarNoteJma}（{Data.RadarFrameAgeText(frameTime, now)}）";
            }
            catch (Exception)
            {
                // ☔ 代替: RainViewer (z7 タイルを 512px で敷く)
                var (time, path) = await Data.FetchRainViewerFrameAsync();
                foreach (var p in Data.RainTileCoverage(cx, cy))
                    await DrawTileAsync(g, Data.RainViewerTileUrl(path, 7, p.TileX, p.TileY),
                        p.OffsetX, p.OffsetY, Data.RainTileDrawSize);
                var frameTime = DateTimeOffset.FromUnixTimeSeconds(time)
                    .ToOffset(TimeSpan.FromHours(9)).DateTime;
                note = $"{Data.RadarNoteFallback}（{Data.RadarFrameAgeText(frameTime, now)}）";
            }
            _radarNote.Text = note;

            // 📍 地点マーカー (タイル内の小数位置まで合わせる)
            var (fx, fy) = TileFraction(loc.Lat, loc.Lon, 8);
            float mx = (float)((fx - (cx - 1)) * 256);
            float my = (float)((fy - (cy - 1)) * 256);
            using var pen = new Pen(Color.FromArgb(0xFF, 0x6B, 0x6B), 2.5f);
            g.DrawEllipse(pen, mx - 9, my - 9, 18, 18);
            g.FillEllipse(Brushes.White, mx - 2.5f, my - 2.5f, 5, 5);
        }

        var old = _radarPicture.Image;
        _radarPicture.Image = bmp;
        old?.Dispose();
    }

    private static async Task DrawTileAsync(Graphics g, string url, int x, int y, int size)
    {
        try
        {
            byte[] bytes = await Data.FetchBytesAsync(url);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            g.DrawImage(img, x, y, size, size);
        }
        catch (Exception) { /* タイル欠けは空白のままにする */ }
    }

    private static DateTime ParseJmaValidtime(string validtime)
        => DateTime.TryParseExact(validtime, "yyyyMMddHHmm",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : Data.NowInTokyo();

    /// <summary>緯度経度を小数つきタイル座標にする (マーカー位置用)</summary>
    private static (double X, double Y) TileFraction(double lat, double lon, int z)
    {
        int n = 1 << z;
        double x = (lon + 180.0) / 360.0 * n;
        double latRad = lat * Math.PI / 180.0;
        double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        return (x, y);
    }
}
