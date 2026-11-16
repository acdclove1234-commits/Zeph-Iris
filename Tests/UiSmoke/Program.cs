// ==================================================
// UiSmoke - 画面の自動確認: 本物のメイン画面を、いくつかの言語で作り、全部のページと設定のタブを開く。
//   ・開くときに例外が出ない
//   ・日本語以外の言語で、訳していない日本語が画面に残っていない
//   ・言語を切り替えても（画面を作り直しても）同じ
//   LED のデバイス・ハードウェアは見本を入れて、ライティング・デバイスの画面も中身を出して確かめる。
//   ファン制御・LED・音の取り込み・ネットへの問い合わせは動かさない
//   実行: dotnet run --project Tests/UiSmoke -c Release [言語 …]
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProFanController;
using ProFanController.UI;
using Localization = ProFanController.Localization;

static class UiSmoke
{
    static readonly string[] Navs = { "NavDashboard", "NavDevices", "NavCurves", "NavHistory", "NavReport", "NavTools", "NavTest", "NavLighting", "NavSensors", "NavSettings" };
    static readonly string[] Tabs = { "SetTabGeneral", "SetTabAuto", "SetTabLink", "SetTabSafety" };
    static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static int _failures;
    static readonly List<string> Errors = new();

    static void Fail(string text)
    {
        _failures++;
        Console.WriteLine("FAIL  " + text);
    }

    static void Pump(Dispatcher d)
    {
        for (int i = 0; i < 4; i++) d.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    [STAThread]
    static int Main(string[] args)
    {
        string[] languages = args.Length > 0 ? args : new[] { "ja", "en", "de", "ru", "zh-Hans", "th" };
        // 固まったときに CI を止めないよう、3 分で打ち切る
        new Thread(() => { Thread.Sleep(TimeSpan.FromMinutes(3)); Console.WriteLine("FAIL  timed out"); Environment.Exit(3); }) { IsBackground = true }.Start();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Errors.Add(e.Exception.ToString());
            e.Handled = true;
        };
        Localization.Init(languages[0]);
        XamlLocalizer.Register();
        // App を作ると本物の起動（多重起動の確認・ファン制御・トレイ・見張り）が予約されるので、その予約を全部取り消す
        int aborted = 0;
        DispatcherHookEventHandler abort = (_, e) => { if (e.Operation.Abort()) aborted++; };
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += abort;
        var app = new App();
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= abort;
        if (aborted == 0)
        {
            Console.WriteLine("FAIL  could not stop the real startup");
            return 2;
        }
        app.InitializeComponent();

        var cfg = new AppConfig();
        cfg.Normalize();
        cfg.Language = languages[0];
        cfg.Quick.HotkeysEnabled = false;
        cfg.Rgb.DeviceSettings.Add(new RgbDeviceSettings
        {
            Name = "Lian Li Uni Hub - SL",
            Zones = new()
            {
                new RgbZoneSettings { Name = "Channel 1", Fans = 3 },
                new RgbZoneSettings { Name = "Channel 1 #2", LightMode = RgbModes.Static, LightColor = "#FF0000" },
            },
        });
        var controller = new AppController(cfg);
        ThemeManager.Apply(cfg.Appearance);
        typeof(App).GetField("_controller", Flags)!.SetValue(app, controller);
        typeof(App).GetField("_windowIcon", Flags)!.SetValue(app, BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4));

        var w = (MainWindow)typeof(App).GetMethod("CreateMainWindow", Flags)!.Invoke(app, new object?[] { controller, null })!;
        Show(w);
        Check(w, languages[0]);
        foreach (string lang in languages.Skip(1))
        {
            // 画面を作り直す言語の切り替え（設定の「表示言語」と同じ）
            cfg.Language = lang;
            app.SwitchLanguage();
            Pump(w.Dispatcher);
            w = (MainWindow)typeof(App).GetField("_window", Flags)!.GetValue(app)!;
            Show(w);
            Check(w, lang);
        }
        w.AllowClose = true;
        w.Close();
        app.Shutdown();
        foreach (var e in Errors.Distinct()) Fail("unhandled exception: " + e);
        Console.WriteLine(_failures == 0 ? $"UI smoke: ALL PASS ({languages.Length} languages x {Navs.Length} pages)" : $"UI smoke: {_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static void Show(MainWindow w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -6000;
        w.Top = -6000;
        w.Width = 1300;
        w.Height = 900;
        w.ShowActivated = false;
        if (!w.IsVisible) w.Show();
        Pump(w.Dispatcher);
        // 見本のハードウェアと LED のデバイス（デバイス・ライティングの画面に中身を出す）
        var devices = new List<DeviceInfo>
        {
            new("/lpc/nct6686d/0", "ASRock B860 Steel Legend WiFi", "Motherboard", "Nuvoton NCT6686D", 9, 6, 6, new List<string>()),
            new("/intelcpu/0", "Intel Core Ultra 5 245KF", "Cpu", null, 30, 0, 0, new List<string>()),
            new("/gpu-nvidia/0", "NVIDIA GeForce RTX 5070 Ti", "Gpu", null, 3, 3, 1, new List<string>()),
        };
        typeof(MainWindow).GetField("_catalog", Flags)!.SetValue(w, new HardwareCatalog(devices, new List<HardwareGroup>(), new List<SensorInfo>(), new List<ControlInfo>(), true));
        var rgb = new List<RgbDevice>
        {
            new(0, "NVIDIA GeForce RTX 5070 Ti", 6, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1, 1, 1 }, "I2C: x"),
            new(1, "Lian Li Uni Hub - SL", 96, 4, new[] { 48, 48 }, "HID: x") { ZoneNames = new[] { "Channel 1", "Channel 2" } },
            new(2, "ASRock B860 Steel Legend WiFi", 34, 0, new[] { 10, 8, 8, 8 }, "HID: x") { ZoneNames = new[] { "Steel Legend", "Header 1", "Header 2", "Header 3" } },
        };
        typeof(MainWindow).GetField("_rgbKnownDevices", Flags)!.SetValue(w, rgb);
    }

    /// <summary>
    /// 直接書き込むグラボ（PNY Epic-X）の行: 画面の行を作る本物の処理（RebuildRgbDeviceRows）で、エンジンと同じく直接書き込むグラボとして出る
    /// （目印・説明が「書き込みを最小限に（1 秒に最大 5 回）」「明るさは一定」にならない。1 秒に送る回数は自動 10 回で、8 回などの設定も消えない）。
    /// 「直接書き込む」をオフにすると（設定を変えるだけで、行は作り直される）、これまでどおり OpenRGB を通すグラボ（最大 5 回）。その行でほかの項目を変えても、
    /// 直接書き込んでいたときの 8 回は消えず、オンに戻すと 8 回に戻る。送っている最中に直接開けなかった（状態が OpenRGB を通している）なら、直接書き込みの表示にしない。
    /// 日本語の画面でだけ見る（訳はあとで入れるので、ほかの言語の画面には出さない）
    /// </summary>
    static void CheckDirectGpuRow(MainWindow w)
    {
        var known = typeof(MainWindow).GetField("_rgbKnownDevices", Flags)!;
        var before = known.GetValue(w);
        var cfg = (AppConfig)typeof(MainWindow).GetProperty("Config", Flags | BindingFlags.Public)!.GetValue(w)!;
        var pny = new RgbDevice(9, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }, "I2C: Nvidia NvAPI I2C on GPU 0, address 0x60");
        bool directBefore = cfg.Rgb.DirectGpuWrite;
        var saved = cfg.Rgb.DeviceSettings.ToList();
        RgbDeviceVm? Row() => (((ItemsControl)w.FindName("RgbDevices")).ItemsSource as IEnumerable<RgbDeviceVm>)?.FirstOrDefault(r => r.Name == pny.Name);
        void CheckDirect(RgbDeviceVm? row, string when)
        {
            if (row is null) { Fail($"direct GPU row ({when}): no row"); return; }
            var fps = row.FpsOptions.Select(o => o.Id).ToList();
            if (!row.IsDirectGpu || row.IsGpu || row.IsSlow || row.AutoFps != 10) Fail($"direct GPU row ({when}): direct {row.IsDirectGpu}, OpenRGB GPU {row.IsGpu}, slow {row.IsSlow}, auto {row.AutoFps} fps (want direct, 10 fps)");
            if (!row.ShowSlowBadge || !row.ShowFps) Fail($"direct GPU row ({when}): the badge and the sends-per-second choice are shown");
            if (row.FpsId != "8" || !fps.Contains("8") || !fps.Contains("30") || !fps.Contains("5")) Fail($"direct GPU row ({when}): 8 sends per second stays selected ({row.FpsId}; choices {string.Join(",", fps)})");
            if (row.SlowBadgeText.Contains("最小限") || row.SlowBadgeText.Contains("最大 5") || !row.SlowBadgeText.Contains("8 回")) Fail($"direct GPU row ({when}): badge \"{row.SlowBadgeText}\"");
            if (row.SlowBadgeTip.Contains("明るさは一定") || row.SlowBadgeTip.Contains("最大 5 回")) Fail($"direct GPU row ({when}): tooltip still says the brightness is fixed / 5 per second");
        }
        void CheckOpenRgb(RgbDeviceVm? row, string when)
        {
            if (row is null) { Fail($"OpenRGB GPU row ({when}): no row"); return; }
            var fps = row.FpsOptions.Select(o => o.Id).ToList();
            if (row.IsDirectGpu || !row.IsGpu || row.AutoFps != 5) Fail($"OpenRGB GPU row ({when}): direct {row.IsDirectGpu}, OpenRGB GPU {row.IsGpu}, auto {row.AutoFps} fps (want OpenRGB GPU, 5 fps)");
            if (fps.Any(id => int.Parse(id) > 5) || row.FpsId != "0") Fail($"OpenRGB GPU row ({when}): choices up to 5 ({string.Join(",", fps)}), 8 shown as automatic ({row.FpsId})");
            if (!row.SlowBadgeText.Contains("最小限")) Fail($"OpenRGB GPU row ({when}): badge \"{row.SlowBadgeText}\"");
        }
        try
        {
            cfg.Rgb.DirectGpuWrite = true;
            cfg.Rgb.DeviceSettings.RemoveAll(s => s.Name == pny.Name);
            cfg.Rgb.DeviceSettings.Add(new RgbDeviceSettings { Name = pny.Name, MaxFps = 8 });
            known.SetValue(w, new List<RgbDevice> { pny });
            typeof(MainWindow).GetMethod("RebuildRgbDeviceRows", Flags)!.Invoke(w, null);
            CheckDirect(Row(), "direct writing on");
            // 設定を変えるだけ（行は自分で作り直さない）
            cfg.Rgb.DirectGpuWrite = false;
            var off = Row();
            CheckOpenRgb(off, "direct writing turned off");
            // OpenRGB を通す行でほかの項目を変えても、直接書き込んでいたときの 8 回は消えない
            if (off is not null) off.Reverse = !off.Reverse;
            int keptFps = cfg.Rgb.DeviceSettings.FirstOrDefault(s => s.Name == pny.Name)?.MaxFps ?? -1;
            if (keptFps != 8) Fail($"OpenRGB GPU row: editing another item keeps the saved 8 sends per second (saved {keptFps})");
            cfg.Rgb.DirectGpuWrite = true;
            CheckDirect(Row(), "direct writing turned on again");
            // 送っている最中の状態が「OpenRGB を通している」（直接開けなかった）なら、直接書き込みの表示にしない
            var live = new[] { new RgbDeviceStatus(pny.Name, pny.LedCount, false, 5, "", "", Direct: false) };
            if (RgbDeviceVm.AsDriven(pny, cfg.Rgb, live).DirectGpu) Fail("direct GPU row: the engine runs it through OpenRGB, but the row would say direct");
            if (!RgbDeviceVm.AsDriven(pny, cfg.Rgb, new[] { live[0] with { Direct = true } }).DirectGpu) Fail("direct GPU row: the engine writes it directly, but the row would say OpenRGB");
        }
        finally
        {
            cfg.Rgb.DirectGpuWrite = directBefore;
            cfg.Rgb.DeviceSettings.Clear();
            cfg.Rgb.DeviceSettings.AddRange(saved);
            known.SetValue(w, before);
        }
    }

    /// <summary>いま画面に出ているページで、訳していない日本語を集める（日本語の画面では見ない）</summary>
    static void Scan(MainWindow w, string lang, string where, HashSet<string> left)
    {
        if (lang == "ja") return;
        foreach (var s in XamlLocalizer.FindUntranslated(w, 50, visibleOnly: true)) left.Add($"{where}: {s.Replace("\n", "\\n")}");
    }

    static void Check(MainWindow w, string lang)
    {
        int before = _failures;
        var left = new HashSet<string>();
        foreach (string nav in Navs)
        {
            try
            {
                ((RadioButton)w.FindName(nav)).IsChecked = true;
                Pump(w.Dispatcher);
                if (nav == "NavDevices") typeof(MainWindow).GetMethod("RebuildDevices", Flags)!.Invoke(w, null);
                if (nav == "NavLighting")
                {
                    if (lang == "ja") CheckDirectGpuRow(w);
                    typeof(MainWindow).GetMethod("RebuildRgbDeviceRows", Flags)!.Invoke(w, null);
                    // ゾーンの設定を開いて、ファンごとに分けた行も出す
                    if (((ItemsControl)w.FindName("RgbDevices")).ItemsSource is System.Collections.IEnumerable rows)
                        foreach (var r in rows) r.GetType().GetProperty("ShowDetails")?.SetValue(r, true);
                }
                if (nav == "NavSettings")
                    foreach (string tab in Tabs)
                    {
                        ((RadioButton)w.FindName(tab)).IsChecked = true;
                        Pump(w.Dispatcher);
                        Scan(w, lang, $"{nav}/{tab}", left);
                    }
                Pump(w.Dispatcher);
                Scan(w, lang, nav, left);
            }
            catch (Exception ex)
            {
                Fail($"{lang}: opening {nav} threw {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            }
        }
        foreach (var s in left) Fail($"{lang}: untranslated text: {s}");
        Console.WriteLine(_failures == before ? $"PASS  {lang}: every page and settings tab opens{(lang == "ja" ? "" : ", no Japanese left")}" : $"      {lang}: {_failures - before} problem(s)");
    }
}
