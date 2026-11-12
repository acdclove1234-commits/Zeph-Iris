// README の画面写真を作る: 作り物のハードウェア（DemoHardware）で本物のメイン画面を動かし、ページごとに PNG にする。
// 実行: dotnet run --project Tests/Screenshots -c Release -- <出力フォルダー> <言語 ja|en> [待つ秒数=150]
// 写るのは作り物の部品・ファンの名前だけ（この PC の部品・機器の名前は写らない）。電源プランなど PC の設定は変えない
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProFanController;
using ProFanController.UI;
using Localization = ProFanController.Localization;

namespace Screenshots;

internal static class Program
{
    private static void Run(Dispatcher d, double sec)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(sec), DispatcherPriority.Normal, (_, _) => frame.Continue = false, d);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Save(FrameworkElement e, string path)
    {
        double w = e.ActualWidth, h = e.ActualHeight;
        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("BgBrush"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, w, h));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        Console.WriteLine($"saved {path}");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    private static CurveConfig Curve(string name, string sensor, string preset, SensorKind kind)
        => new() { Name = name, SensorId = sensor, Points = CurvePresets.Create(preset, kind), HysteresisCelsius = 3, EmaAlpha = 0.2 };

    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "shots";
        string lang = args.Length > 1 ? args[1] : "en";
        double warm = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 150;
        Directory.CreateDirectory(outDir);
        // 前に撮ったときの記録・設定を消す（記録のグラフに前の回の線がつながらないように）
        foreach (var old in new[] { "data", "logs" }) try { Directory.Delete(Path.Combine(AppContext.BaseDirectory, old), true); } catch (DirectoryNotFoundException) { }
        try { File.Delete(Path.Combine(AppContext.BaseDirectory, "config.json")); } catch (IOException) { }
        new Thread(() => { Thread.Sleep(TimeSpan.FromSeconds(warm + 180)); Environment.Exit(3); }) { IsBackground = true }.Start();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Localization.Init(lang);
        XamlLocalizer.Register();
        int aborted = 0;
        DispatcherHookEventHandler abortPosted = (_, e) => { if (e.Operation.Abort()) aborted++; };
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += abortPosted;
        var app = new App();
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= abortPosted;
        if (aborted == 0) return 2;
        app.InitializeComponent();

        bool ja = lang == "ja";
        var cfg = new AppConfig();
        cfg.Normalize();
        cfg.Language = lang;
        cfg.Quick.HotkeysEnabled = false;
        cfg.AutoDevicePhotos = false;
        cfg.SetupCompleted = true;
        cfg.Rgb.Enabled = false;
        foreach (var p in cfg.Profiles)
        {
            p.Fans.Clear();
            string preset = ProfileRoles.Preset(p.Role);
            var cpuFan = new FanConfig { Name = ja ? "CPU クーラー" : "CPU cooler", ControlId = DemoHardware.FanControls[0], RpmSensorId = DemoHardware.FanRpms[0], Role = FanRoles.Fan, Enabled = true, MinDuty = 25 };
            cpuFan.Curves.Add(Curve("CPU", DemoHardware.CpuTemp, preset, SensorKind.Cpu));
            var pump = new FanConfig { Name = ja ? "ポンプ" : "Pump", ControlId = DemoHardware.FanControls[1], RpmSensorId = DemoHardware.FanRpms[1], Role = FanRoles.Pump, Enabled = true, Mode = FanModes.Fixed, FixedDuty = 70 };
            var front = new FanConfig { Name = ja ? "前面ファン" : "Front fans", ControlId = DemoHardware.FanControls[2], RpmSensorId = DemoHardware.FanRpms[2], Role = FanRoles.Fan, Enabled = true, MinDuty = 25 };
            front.Curves.Add(Curve("CPU", DemoHardware.CpuTemp, preset, SensorKind.Cpu));
            front.Curves.Add(Curve("GPU", DemoHardware.GpuTemp, preset, SensorKind.Gpu));
            var rear = new FanConfig { Name = ja ? "背面ファン" : "Rear fan", ControlId = DemoHardware.FanControls[3], RpmSensorId = DemoHardware.FanRpms[3], Role = FanRoles.Fan, Enabled = true, MinDuty = 25 };
            rear.Curves.Add(Curve("CPU", DemoHardware.CpuTemp, preset, SensorKind.Cpu));
            var top = new FanConfig { Name = ja ? "天井ファン" : "Top fans", ControlId = DemoHardware.FanControls[4], RpmSensorId = DemoHardware.FanRpms[4], Role = FanRoles.Fan, Enabled = true, MinDuty = 25 };
            top.Curves.Add(Curve("GPU", DemoHardware.GpuTemp, preset, SensorKind.Gpu));
            foreach (var f in new[] { cpuFan, pump, front, rear, top }) p.Fans.Add(f);
        }

        var controller = new AppController(cfg);
        ThemeManager.Apply(cfg.Appearance);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(App).GetField("_controller", flags)!.SetValue(app, controller);
        typeof(App).GetField("_windowIcon", flags)!.SetValue(app, BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4));
        var w = (MainWindow)typeof(App).GetMethod("CreateMainWindow", flags)!.Invoke(app, new object?[] { controller, null })!;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -6000; w.Top = -6000; w.Width = 1280; w.Height = 860;
        w.ShowActivated = false;
        w.Show();

        var engine = controller.Engine;
        engine.HardwareFactory = () => new DemoHardware();
        engine.CatalogReady += c => w.Dispatcher.InvokeAsync(() => w.OnCatalogReady(c));
        engine.SnapshotReady += s => w.Dispatcher.InvokeAsync(() => w.OnSnapshot(s));
        engine.Start(cfg, controller.EffectiveProfile);
        Console.WriteLine($"warming up {warm:F0} s…");
        Run(w.Dispatcher, warm);

        void Shot(string nav, string file, Action? prepare = null)
        {
            ((RadioButton)w.FindName(nav)).IsChecked = true;
            Run(w.Dispatcher, 1);
            if (prepare is not null) { prepare(); Run(w.Dispatcher, 2); }
            Run(w.Dispatcher, 2);
            Save((FrameworkElement)w.Content, Path.Combine(outDir, $"{file}-{lang}.png"));
        }
        Shot("NavDashboard", "dashboard");
        Shot("NavDevices", "devices");
        // カーブの画面は、上の基本設定ではなくカーブのグラフが見えるところまで送る
        Shot("NavCurves", "curves", () => ((FrameworkElement)w.FindName("CurveEditorPanel")).BringIntoView());
        // 記録は 5 分の表示にする（温まるまでの 150 秒が画面いっぱいに見える）
        Shot("NavHistory", "history", () =>
        {
            foreach (var rb in Descendants<RadioButton>(w))
                if (rb.GroupName == "Range" && (rb.Tag as string) == "5") rb.IsChecked = true;
        });
        Shot("NavSensors", "sensors");
        // 設定: アップデートの欄まで送る
        Shot("NavSettings", "settings", () => ((FrameworkElement)w.FindName("UpdateCheckButton")).BringIntoView());
        Shot("NavLighting", "lighting");

        // 自動セットアップ: 測り終えた結果を見せる（測定そのものは 20〜30 分かかるので、結果だけ入れる）
        var mw = typeof(MainWindow);
        var results = (Dictionary<SetupStepKind, AutoTuneResult>)mw.GetField("_tuneResults", flags)!.GetValue(w)!;
        List<AutoTuneStep> Steps(params (double d, double t, double r)[] s) => s.Select(x => new AutoTuneStep { Duty = x.d, SteadyTemp = x.t, AvgRpm = x.r }).ToList();
        results[SetupStepKind.TuneCpu] = new AutoTuneResult { SensorId = DemoHardware.CpuTemp, SensorName = "CPU Package", IdleTemp = 38.2,
            Steps = Steps((100, 71.5, 1980), (80, 73.0, 1590), (65, 75.4, 1300), (50, 78.9, 1000), (40, 83.1, 810)), Fans = new() { (DemoHardware.FanControls[0], "CPU") } };
        results[SetupStepKind.TuneGpu] = new AutoTuneResult { SensorId = DemoHardware.GpuTemp, SensorName = "GPU Core", IdleTemp = 37.5,
            Steps = Steps((100, 62.0, 1580), (80, 64.1, 1270), (65, 67.0, 1030), (50, 71.8, 800), (40, 76.5, 650)), Fans = new() { (DemoHardware.FanControls[4], "GPU") } };
        ((RadioButton)w.FindName("NavTest")).IsChecked = true;
        Run(w.Dispatcher, 1);
        ((TextBlock)w.FindName("TuneStatus")).Text = Localization.Lookup("測定が終わりました。カーブを確かめて、適用するプロファイルを選んでください。");
        ((FrameworkElement)w.FindName("TuneResultSwitch")).Visibility = Visibility.Visible;
        mw.GetMethod("ShowTuneResult", flags)!.Invoke(w, null);
        Run(w.Dispatcher, 2);
        Save((FrameworkElement)w.Content, Path.Combine(outDir, $"autosetup-{lang}.png"));

        engine.Stop();
        w.AllowClose = true;
        w.Close();
        app.Shutdown();
        return 0;
    }
}
