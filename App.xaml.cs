// ==================================================
// App.xaml.cs - 起動処理 (二重起動防止・トレイ常駐・ウィジェット・ワンタッチ操作・ホットキー・
//               スマホ監視・プロファイル自動切替・終了時の BIOS 復帰・見張りプロセス・スリープ復帰・
//               スマホ通知・OpenRGB 連携・部品交換の確認)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ProFanController.UI;

namespace ProFanController;

public partial class App : Application
{
    private AppController? _controller;
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private WidgetWindow? _widget;
    private WebServer? _web;
    private DispatcherTimer? _ticker;
    private HwndSource? _hotkeySource;
    private bool _hotkeysRegistered;
    private bool _paused;
    private bool _trayHintShown;
    private volatile string _statusJson = "{}";
    private readonly Dictionary<string, DateTime> _lastBalloon = new();

    public bool WebRunning => _web?.Running == true;

    /// <summary>スマホへの通知（設定画面のテスト送信からも使う）</summary>
    public Notifier Notifier { get; } = new();
    /// <summary>OpenRGB 連携（設定画面に状態を表示する）</summary>
    public RgbSync Rgb { get; } = new();
    /// <summary>音楽連動の音の取り込み</summary>
    public AudioCapture Audio { get; } = new();
    /// <summary>音と光のずれを合わせる「同期テスト」のクリック音</summary>
    public SyncTestPlayer SyncTestPlayer { get; } = new();

    private bool _watchdogRunning;
    private DateTime _uiErrorWindow = DateTime.MinValue;
    private int _uiErrorCount;
    private bool _firstCatalog = true;
    private string _rgbJson = "";

    /// <summary>インストーラー（Installer/Fanlume.iss）や更新スクリプトがこのイベントをセットすると、アプリが正しく終了する</summary>
    private const string ExitSignalName = AppIdentity.ExitSignal;
    private EventWaitHandle? _exitSignal;
    /// <summary>メモリの使い方を 10 分ごとにログに書くタイマー（常駐の重さの内訳を見るため）</summary>
    private System.Threading.Timer? _memoryLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 画面の言語は、何かを表示する前に決める（設定の「表示言語」。空なら Windows の表示言語）。
        // XAML の日本語を訳す仕組みは、日本語で起動しても登録しておく（あとから別の言語に切り替えられるように）
        Localization.Init(ConfigStore.ReadLanguage());
        XamlLocalizer.Register();

        // 見張りプロセスは Program.Main で、App を作らずに動かす（ここには来ない）
        // シャットダウンの取り消しに備えたプロセス: 待ってから、まだ Windows が動いていればアプリを起動し直す
        int dr = Array.IndexOf(e.Args, DelayedRestartArgument);
        if (dr >= 0)
        {
            int wait = dr + 1 < e.Args.Length && int.TryParse(e.Args[dr + 1], out int w) ? Math.Clamp(w, 10, 600) : 90;
            Thread.Sleep(TimeSpan.FromSeconds(wait));
            Logger.FileSuffix = "restart";
            Logger.Warn("Windows の終了が取り消されたようなので、ファン制御を再開するためにアプリを起動し直します");
            Logger.Flush();
            StartSelf($"--minimized {RestartArgument}");
            Shutdown();
            return;
        }
        // お片付けの最後: 本体が終わってから設定・ログ・記録を消すプロセス（App.Uninstall.cs）
        if (TryRunCleanupHelper(e.Args))
        {
            Shutdown();
            return;
        }
        PawnIoAutoTried = e.Args.Contains(PawnIoTriedArgument, StringComparer.OrdinalIgnoreCase);
        bool recovered = e.Args.Contains(Watchdog.RecoveredArgument, StringComparer.OrdinalIgnoreCase);
        bool restarting = e.Args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);

        _mutex = new Mutex(true, AppIdentity.SingleInstance, out bool isFirst);
        if (!isFirst && (recovered || restarting))
        {
            // 見張りプロセスが起動し直したとき: 前のプロセスが終わりきるまで少し待つ
            try { isFirst = _mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { isFirst = true; } // 前のプロセスが異常終了した = もう誰も使っていない
        }
        if (!isFirst)
        {
            // 自動起動（--minimized）で二重に起動したときは、何も出さずに終わる
            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show(L("Fanlume はすでに起動しています。\nタスクトレイのアイコンから開いてください。"),
                    AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        // ダウンロード・デスクトップ・一時フォルダー（zip の中から開いた）で起動したら、C:\Program Files\Fanlume に入れて、そこから起動し直す
        if (RelocateFromUnstableFolder(e.Args))
        {
            _exiting = true; // 何も始めていないので、終了の処理（BIOS への切り替えなど）はしない
            try { _mutex.ReleaseMutex(); } catch { /* 持っていない */ }
            _mutex.Dispose();
            _mutex = null;
            Logger.Flush();
            Shutdown();
            return;
        }

        // 前の名前（ProFanController）の版が動いていたら、正しく終了してもらってから（終了するときに設定も書き出される）、
        // ここに設定がなければ、前の版の設定と記録を引き継ぐ（Core/LegacyMigration.cs）
        string? migratedFrom = null;
        try
        {
            LegacyMigration.StopLegacyApp(TimeSpan.FromSeconds(20));
            migratedFrom = LegacyMigration.CopySettingsIfNeeded(AppContext.BaseDirectory);
            if (migratedFrom is not null) Logger.Info($"前の版の設定と記録を引き継ぎました: {migratedFrom}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"前の版の設定を引き継げませんでした: {ex}");
        }

        // インストーラー・更新スクリプトからの「終了して」の合図を待つ（強制終了だとファンが BIOS 制御に戻らないため、正規の終了処理を通す）
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => Dispatcher.InvokeAsync(ExitApplication), null, Timeout.Infinite, true);

        EnableCrashDumps();
        // メモリの使い方（使用中・専用・.NET が管理する分）を 10 分ごとにログへ（最初は起動の 1 分後）
        _memoryLog = new System.Threading.Timer(_ => LogMemory(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        var controller = new AppController(LoadConfig());
        _controller = controller;
        var engine = controller.Engine;
        ConfigStore.Backup("startup");
        Notifier.Config = controller.Config.Notify;
        // 使われていない古いデバイスの画像を消す（元に戻すの記録は起動し直すと消えるので、起動したときに。画面を待たせないよう別スレッドで）
        var usedImages = controller.Config.DeviceImages.Select(i => i.FileName).ToList();
        _ = Task.Run(() => ImageCleanup.Run(Path.Combine(AppContext.BaseDirectory, "images"), usedImages));

        // どんな終わり方をしても、ファンを BIOS 制御に戻してから終わる
        DispatcherUnhandledException += (_, a) =>
        {
            // 同じ例外が毎フレーム起きるとログがあふれるので、1 分に 20 件までにする
            var now = DateTime.Now;
            if (now - _uiErrorWindow > TimeSpan.FromMinutes(1))
            {
                if (_uiErrorCount > 20) Logger.Error($"UI で例外: 直前の 1 分間に {_uiErrorCount} 件（21 件目以降は省略）");
                _uiErrorWindow = now;
                _uiErrorCount = 0;
            }
            if (++_uiErrorCount <= 20) Logger.Error($"UI で例外: {a.Exception}");
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Logger.Error($"致命的な例外: {a.ExceptionObject}");
            Logger.Flush();
            engine.Stop();
            Logger.Flush();
        };
        // 待たれずに捨てられた非同期処理の例外も記録する（気づかないまま失敗しないように）
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Logger.Error($"非同期処理で例外: {a.Exception}");
            a.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => engine.Stop();
        SessionEnding += (_, _) =>
        {
            // Windows の終了・サインアウトは正しい終了なので、見張りプロセスに再起動させない。
            // 先にファンを BIOS 制御に戻し、戻せたときだけ見張りに「正しい終了」を伝える
            bool restored = false;
            try { restored = StopFanEngine(engine); } catch (Exception ex) { Logger.Error($"終了時の BIOS 制御への復帰に失敗: {ex.Message}"); }
            if (restored) Watchdog.SignalStop();
            Try("LED", () => Rgb.Dispose());
            Try(L("記録の保存"), () => _window?.SaveReportData());
            // この後アプリは終了する。ほかのアプリがシャットダウンを取り消すと、ファンが BIOS 制御のまま
            // アプリがない状態になるので、90 秒後にアプリを起動し直す小さなプロセスを残す
            // （本当にシャットダウンするなら、そのプロセスも一緒に終了させられる）
            StartDelayedRestart(90);
        };

        var tray = new TrayIcon(ShowMainWindow, TogglePause, ToggleWidget, name => controller.SetActiveProfile(name),
            cmd => ExecuteCommand(cmd), ExitApplication);
        _tray = tray;
        tray.LightingState = () => controller.Config.Rgb;
        // エンジンの健康状態が変わったら知らせる（読めない部品がある・BIOS 制御で復旧を待っている など）
        engine.HealthChanged += (health, reason) => Dispatcher.InvokeAsync(() => OnEngineHealth(health, reason));
        // 見た目（テーマ・アクセント色・フォント）は画面を作る前に当てる。画面ができるたびに、タイトルバーと文字の大きさも当てる
        ThemeManager.Apply(controller.Config.Appearance);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) ThemeManager.ApplyToWindow(w, _controller?.Config.Appearance ?? new AppearanceConfig()); }));
        WatchAppearance(controller);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            // Windows のダーク／ライト・アクセント色が変わったとき（「Windows に合わせる」を選んでいるときだけ当て直す）
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
            Dispatcher.InvokeAsync(() =>
            {
                var a = _controller?.Config.Appearance;
                if (a is not null && (a.Theme == AppThemes.System || a.Accent == AppearanceConfig.SystemAccent)) ThemeManager.Apply(a);
            });
        };
        _windowIcon = tray.CreateWindowIcon();
        var window = CreateMainWindow(controller, carry: null);

        // エンジン・プロセス監視のイベントは別スレッドで来るので、UI スレッドに移す。
        // メイン画面は表示言語を切り替えると作り直すので、いつも _window（いまの画面）に渡す
        engine.CatalogReady += c => Dispatcher.InvokeAsync(() =>
        {
            _window?.OnCatalogReady(c);
            // GPU ファンの設定を変えたときなどにも呼ばれるので、起動時の確認は 1 回だけ
            if (!_firstCatalog) return;
            _firstCatalog = false;
            if (!controller.Config.SetupCompleted && controller.Config.Profiles.All(p => p.Fans.Count == 0))
            {
                // 初回起動: ウィザードを案内する
                ShowMainWindow();
                _window?.OpenWizard();
                return;
            }
            CheckHardwareChange(c);
        });
        engine.SnapshotReady += s => Dispatcher.InvokeAsync(() =>
        {
            var w = _window;
            if (w is null) return;
            w.OnSnapshot(s);
            if (controller.Config.Rgb.Enabled) Rgb.Update(w.RgbTemperature(s), s.Emergency);
            w.ForwardSnapshotToWizard(s);
            _widget?.SetProfile(s.Profile);
            tray.SetTooltip(w.TraySummary);
            _statusJson = BuildStatusJson(s);
        });
        engine.Alert += (message, level) => Dispatcher.InvokeAsync(() =>
        {
            _window?.ShowAlert(message, level);
            _window?.OnEngineAlert(message, level);
            Notifier.Notify(level == AlertLevel.Info ? L("Fanlume: 回復しました") : AppIdentity.Name, message, level, isRecovery: level == AlertLevel.Info);
            // 同じ通知は 5 分間出さない（温度が境目を行き来したときの連発防止。画面のバナーとログには毎回残す）
            if (_lastBalloon.TryGetValue(message, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(5)) return;
            _lastBalloon[message] = DateTime.Now;
            tray.ShowBalloon(AppIdentity.Name, message, level);
        });
        engine.CalibrationProgressChanged += p => Dispatcher.InvokeAsync(() => _window?.OnCalibrationProgress(p));
        engine.CalibrationFinished += (id, result, message) => Dispatcher.InvokeAsync(() => _window?.OnCalibrationFinished(id, result, message));
        controller.Switcher.MatchChanged += (process, profile) => Dispatcher.InvokeAsync(() =>
        {
            controller.SetAutoMatch(process, profile);
            if (process is not null && profile is not null && controller.Config.FindProfile(profile) is not null)
                tray.ShowBalloon(L("プロファイルを自動で切り替えました"), L($"{process} を検出 → {profile}"), AlertLevel.Info);
        });

        engine.Resumed += () => Dispatcher.InvokeAsync(() => Logger.Info("スリープ復帰後の制御を再開"));

        controller.EffectiveProfileChanged += UpdateTrayProfiles;
        controller.Committed += UpdateTrayProfiles;
        controller.Committed += ApplyServiceSettings;
        controller.QuickChanged += UpdateQuickStatus;
        controller.ConfigReplaced += () =>
        {
            WatchAppearance(controller);
            ThemeManager.Apply(controller.Config.Appearance);
            // ウィジェットは新しい設定の位置で開き直す
            if (_widget is not null) { CloseWidget(save: false); ShowWidget(); }
            UpdateHotkeys();
            UpdateWebServer();
            ApplyServiceSettings();
        };
        UpdateTrayProfiles();

        controller.Start();
        Logger.Info($"起動 (args: {string.Join(' ', e.Args)})");

        // ワンタッチ操作の残り時間・スケジュールの確認（1 秒ごと）
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) =>
        {
            controller.Tick(DateTime.Now);
            UpdateQuickStatus();
        };
        _ticker.Start();

        UpdateHotkeys();
        UpdateWebServer();
        ApplyServiceSettings();

        // スリープに入る前に BIOS 制御へ戻し、復帰したらハードウェアを開き直して制御を再開する
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || controller.Config.StartMinimized;
        if (!minimized) ShowMainWindow();
        if (controller.Config.Widget.Visible) ShowWidget();

        if (controller.Config.DryRun)
        {
            Logger.Info("DRY RUN の設定で起動しました（ファンには書き込みません）");
            window.ShowAlert(L("DRY RUN（テストモード）で動いています。温度に応じた回転数を計算するだけで、ファンは動かしていません。"), AlertLevel.Warning,
                L("制御を開始する"), () =>
                {
                    controller.Config.DryRun = false;
                    controller.Commit(recordUndo: false);
                    Logger.Info("DRY RUN を解除しました（起動時のお知らせから）");
                });
        }
        if (ConfigStore.RecoveredFrom is { } from)
        {
            ShowMainWindow();
            window.ShowAlert(L($"設定ファイルが壊れていたため、バックアップ（{File.GetLastWriteTime(from):yyyy/MM/dd HH:mm}）から復旧しました。壊れたファイルは config.broken_*.json として残しています。"), AlertLevel.Warning);
        }
        if (recovered)
        {
            string msg = L("Fanlume が予期せず終了したため、見張りプロセスが自動で起動し直しました。ファンは安全な回転数にしてから制御を再開しています。原因はログで確認できます。");
            window.ShowAlert(msg, AlertLevel.Warning, L("ログを開く"), () => SystemTool.Open(Logger.Directory));
            tray.ShowBalloon(AppIdentity.Name, L("予期せず終了したため、自動で起動し直しました。"), AlertLevel.Warning);
            Notifier.Notify(AppIdentity.Name, msg, AlertLevel.Warning);
        }
        if (InstalledFrom(e.Args) is { } installedFrom) AfterRelocation(window, installedFrom);
        if (migratedFrom is not null) AfterLegacyMigration(window, migratedFrom);
        // 前の名前で登録した自動起動・ファイアウォールの許可などを片付ける（自動起動を使っていたら、新しい名前で登録し直す）
        _ = Task.Run(() => LegacyMigration.RemoveLegacyRegistrations(() => AutoStart.Enable()));
        _ = CheckInstallAsync();
        // 更新したあとなら知らせ、新しい版を定期的に確かめる（App.Update.cs）
        StartUpdateChecks(controller, recovered);
    }

    private System.Windows.Media.ImageSource? _windowIcon;

    /// <summary>メイン画面を作り、App への知らせをつなぐ（起動したときと、表示言語を切り替えたとき）</summary>
    private MainWindow CreateMainWindow(AppController controller, MainWindowCarry? carry)
    {
        var window = new MainWindow(controller, _windowIcon!, carry);
        _window = window;
        MainWindow = window;
        window.HiddenToTray += (_, _) =>
        {
            if (_trayHintShown) return;
            _trayHintShown = true;
            _tray?.ShowBalloon(AppIdentity.Name, L("タスクトレイで動作を続けています。終了するにはアイコンを右クリックしてください。"), AlertLevel.Info);
        };
        window.WidgetToggleRequested += ToggleWidget;
        window.MaintenanceNotice += (msg, level) => Notifier.Notify(L("Fanlume: メンテナンス"), msg, level);
        window.RecordSaved += r => Logger.Info($"異常の記録: {r.CsvPath}");
        return window;
    }

    /// <summary>
    /// 起動して少し落ち着いてから、置き場所・自動起動・取り合いになるソフトを確かめて、気になることがあればお知らせに出す
    /// （ほかの PC で「新しい版を入れたのに古い画面のまま」「色が勝手に戻る」などが起きたときの原因を見つけやすくするため）
    /// </summary>
    private async Task CheckInstallAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            string exe = Environment.ProcessPath ?? "";
            var (conflicts, registered) = await Task.Run(() => (InstallCheck.FindRunningConflicts(), AutoStart.RegisteredPath()));
            string? unstable = InstallCheck.UnstableFolderName(AppContext.BaseDirectory);
            bool elsewhere = exe.Length > 0 && InstallCheck.PointsElsewhere(registered, exe);
            var parts = new List<string>();
            if (elsewhere)
            {
                Logger.Warn($"自動起動が別の場所のアプリを指しています: {registered}（いまの場所: {exe}）");
                parts.Add(L($"Windows の起動時に自動で立ち上がるのは、別の場所のアプリです（{registered}）。古い版が立ち上がっているかもしれません。"));
            }
            if (unstable is not null)
            {
                Logger.Warn($"「{unstable}」フォルダーの中から動いています: {AppContext.BaseDirectory}");
                parts.Add(L($"このアプリは「{unstable}」フォルダーの中から動いています。整理したときに消えたり、別の版が増えたりしないよう、")
                    + L($"下のボタンで {InstallCheck.RecommendedFolder} に移してください（設定はそのまま持っていき、自動起動も移した先に登録し直して、起動し直します）。"));
            }
            if (conflicts.Count > 0)
            {
                Logger.Warn($"LED・ファンを操作するほかのソフトが動いています: {string.Join("、", conflicts)}");
                parts.Add(L($"{string.Join(Localization.ListSeparator, conflicts)} が動いています。同じ LED やファンを操作するので、色が勝手に戻る・ちらつく・回転数が反映されない原因になります。そのソフトの LED・ファンの制御を止めるか、終了してください。"));
            }
            if (parts.Count == 0) return;
            if (unstable is not null)
            {
                // 置き場所を直せば、自動起動も一緒に直る（自動起動を使っていたときは、移した先に登録し直す）
                _window?.ShowAlert(string.Join("\n", parts), AlertLevel.Warning,
                    L($"{InstallCheck.RecommendedFolder} に移す"), () => MoveToRecommended(registerAutoStart: registered is not null));
                return;
            }
            _window?.ShowAlert(string.Join("\n", parts), AlertLevel.Warning,
                elsewhere ? L("自動起動をこの場所にする") : null,
                elsewhere ? () =>
                {
                    bool ok = AutoStart.Enable();
                    Logger.Info(ok ? $"自動起動をこの場所に登録し直しました: {exe}" : "自動起動を登録し直せませんでした");
                    _window?.ShowAlert(ok ? L("自動起動を、いま動いているこのアプリに登録し直しました。") : L("自動起動を登録し直せませんでした（ログを確認してください）。"),
                        ok ? AlertLevel.Info : AlertLevel.Warning);
                } : null);
        }
        catch (Exception ex)
        {
            Logger.Warn($"起動時の確認に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// いまのフォルダーの中身を C:\Program Files\Fanlume へ写し、（自動起動を使っていたなら）自動起動をそこに登録し直して、
    /// 写した先のアプリで起動し直す。このアプリは正しく終了する（ファンは BIOS 制御に戻る）
    /// </summary>
    private void MoveToRecommended(bool registerAutoStart)
    {
        try
        {
            _controller?.Commit(recordUndo: false);   // 設定を書き出してから写す
            string exe = InstallCheck.CopyInstall(AppContext.BaseDirectory, InstallCheck.RecommendedFolder);
            if (registerAutoStart && !AutoStart.Enable(exe)) Logger.Warn("移した先に自動起動を登録し直せませんでした");
            Logger.Info($"{InstallCheck.RecommendedFolder} へ移しました（自動起動の登録し直し: {(registerAutoStart ? "あり" : "なし")}）。移した先で起動し直します");
            // このアプリが終わるのを待ってから（二重には起動できないので、約 5 秒あける）、移した先のアプリを起動する
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 6 127.0.0.1 >nul & start \"\" \"{exe}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = InstallCheck.RecommendedFolder,
            };
            System.Diagnostics.Process.Start(psi)?.Dispose();
            ExitApplication();
        }
        catch (Exception ex)
        {
            Logger.Warn($"{InstallCheck.RecommendedFolder} へ移せませんでした: {ex}");
            _window?.ShowAlert(L($"{InstallCheck.RecommendedFolder} へ移せませんでした: {ex.Message}"), AlertLevel.Warning);
        }
    }

    private AppearanceConfig? _watchedAppearance;

    /// <summary>見た目の設定を変えたら、すぐに当てる（元に戻す・設定の読み込みで丸ごと入れ替わったときは見張り直す）</summary>
    private void WatchAppearance(AppController controller)
    {
        if (_watchedAppearance is not null) _watchedAppearance.PropertyChanged -= OnAppearanceChanged;
        _watchedAppearance = controller.Config.Appearance;
        _watchedAppearance.PropertyChanged += OnAppearanceChanged;
    }

    private void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is AppearanceConfig a) ThemeManager.Apply(a);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_controller is null) return;
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // BIOS 制御に戻し終わるまで少し待ってから、スリープさせる（最大 2 秒）
                try { _controller.Engine.Suspend().Wait(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { Logger.Warn($"スリープ前の BIOS 制御への復帰を確認できません: {ex.Message}"); }
                Try("LED", () => Rgb.Suspend());
                break;
            case PowerModes.Resume:
                _controller.Engine.Resume();
                Try("LED", () => Rgb.Resume());
                Try(L("音の取り込み"), () => Audio.Restart());
                break;
        }
    }

    /// <summary>設定が変わったら、見張りプロセス・通知・RGB に反映する</summary>
    private void ApplyServiceSettings()
    {
        // お片付けの途中・後は、消したもの（ファイアウォールの許可・見張りプロセス）を作り直さない
        if (_controller is null || _cleanupStarted) return;
        var cfg = _controller.Config;
        Notifier.Config = cfg.Notify;

        if (cfg.Safety.WatchdogEnabled && !_watchdogRunning)
        {
            if (!_watchdogHooked)
            {
                _watchdogHooked = true;
                Watchdog.UnexpectedExit += code => Dispatcher.InvokeAsync(() => RelaunchWatchdog(code));
            }
            _watchdogGeneration++;
            Watchdog.Launch();
            _watchdogRunning = true;
        }
        else if (!cfg.Safety.WatchdogEnabled && _watchdogRunning)
        {
            _watchdogGeneration++;
            Watchdog.SignalStop();
            _watchdogRunning = false;
        }

        ApplyLightingNow();
    }

    /// <summary>
    /// 万一アプリが落ちたとき、原因を調べるための記録（ミニダンプ）を logs\dumps に残すよう Windows に頼む（最新 5 個）。
    /// 管理者権限で動いているので設定できる。失敗しても動作には影響しない
    /// </summary>
    private static void EnableCrashDumps()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(CrashDumpKey); // お片付け（App.Uninstall.cs）で消す
            string folder = Path.Combine(Logger.Directory, "dumps");
            Directory.CreateDirectory(folder);
            key.SetValue("DumpFolder", folder, RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 1, RegistryValueKind.DWord);   // 1 = ミニダンプ（小さい）
            key.SetValue("DumpCount", 5, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.Warn($"クラッシュ時の記録を有効にできません: {ex.Message}");
        }
    }

    /// <summary>自分で起動し直したときの引数（前のプロセスが終わるのを少し待つ）</summary>
    private const string RestartArgument = "--restart";

    /// <summary>seconds 秒待ってからアプリを起動し直すプロセスを起動する（シャットダウンが取り消されたとき用）</summary>
    private static void StartDelayedRestart(int seconds)
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L("実行ファイルのパスが分かりません。"));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"{DelayedRestartArgument} {seconds}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"シャットダウン取り消しに備えた再起動の予約に失敗: {ex.Message}");
        }
    }

    private static void StartSelf(string args)
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L("実行ファイルのパスが分かりません。"));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"起動し直せませんでした: {ex.Message}");
        }
    }

    /// <summary>「○秒後に起動し直す」プロセスとして起動されたときの引数</summary>
    /// <summary>メモリの使い方を 1 行でログに書く（使用中・専用・.NET のヒープ・.NET が OS から確保した分）</summary>
    private static void LogMemory()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            Logger.Info($"メモリ: 使用中 {p.WorkingSet64 / 1048576} MB・専用 {p.PrivateMemorySize64 / 1048576} MB（.NET のヒープ {gc.HeapSizeBytes / 1048576} MB・.NET が確保 {gc.TotalCommittedBytes / 1048576} MB・スレッド {p.Threads.Count}）");
        }
        catch (Exception ex) { Logger.Warn($"メモリの記録に失敗: {ex.Message}"); }
    }

    private const string DelayedRestartArgument = "--restart-after";

    private bool _watchdogHooked;
    /// <summary>見張りのオン・オフを切り替えるたびに増やす（切り替え前に予約した起動し直しを取り消すため）</summary>
    private int _watchdogGeneration;
    private readonly List<DateTime> _watchdogRestarts = new();

    /// <summary>見張りプロセスが勝手に終わった: 1 → 5 → 30 秒待って起動し直す（10 分以内に何度も落ちるときは間隔を延ばす）</summary>
    private async void RelaunchWatchdog(int exitCode)
    {
        if (_exiting || !_watchdogRunning || _controller?.Config.Safety.WatchdogEnabled != true) return;
        _watchdogRestarts.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(10));
        int delay = _watchdogRestarts.Count switch { 0 => 1, 1 => 5, _ => 30 };
        int gen = _watchdogGeneration;
        _watchdogRestarts.Add(DateTime.Now);
        Logger.Warn($"見張りプロセスが終了しました（コード {exitCode}）。{delay} 秒後に起動し直します");
        await Task.Delay(TimeSpan.FromSeconds(delay));
        if (_exiting || !_watchdogRunning || gen != _watchdogGeneration) return;
        _watchdogGeneration++;
        try { await Task.Run(Watchdog.Launch); } // 起動前に少し待つことがあるので、画面を止めないよう別スレッドで
        catch (Exception ex) { Logger.Error($"見張りプロセスを起動し直せません: {ex.Message}"); }
    }

    /// <summary>
    /// LED・音楽連動の設定を、保存を待たずにすぐ反映する（設定画面のスライダーを動かしている間も呼ばれる）。
    /// 繋ぎ直すのは接続先・対象のデバイスが変わったときだけ。光り方・明るさ・色などは次のコマから変わる
    /// </summary>
    public void ApplyLightingNow()
    {
        if (_controller is null) return;
        var cfg = _controller.Config;
        string rgbJson = JsonSerializer.Serialize(cfg.Rgb);
        if (rgbJson == _rgbJson) return;
        _rgbJson = rgbJson;
        var copy = JsonSerializer.Deserialize<RgbConfig>(rgbJson)!;
        Rgb.AudioSource ??= () => Audio.Running ? Audio.Engine : null;
        Rgb.Configure(copy);
        // 音楽連動のときだけ音を取り込む（それ以外のときは何も読み取らない）
        if (copy.Enabled && copy.Mode == RgbModes.Music)
        {
            Audio.Configure(copy.MusicSource, MusicTuning.EngineSettings(copy));
            Audio.Start();
        }
        else
        {
            if (SyncTestPlayer.Running) SyncTestPlayer.Stop(Audio.Engine);
            Rgb.SyncTest = false;
            Audio.Stop();
        }
    }

    /// <summary>
    /// 起動時: 前回と比べて設定で使っているセンサーや端子が無くなっていないか確認する（GPU を交換した、など）。
    /// 無くなっていたら、新しいハードウェアへの置き換えを案内する。変化がなければ今のハードウェアを覚えておく
    /// </summary>
    private void CheckHardwareChange(HardwareCatalog catalog)
    {
        if (_controller is null || _window is null || _tray is null) return;
        var cfg = _controller.Config;
        if (cfg.Safety.HardwareCheckEnabled)
        {
            // 起動直後で部品がまだ見えていないときは判断せず、少し待って探し直す（3 回目は見えている分で判断する）
            var report = HardwareChange.Detect(cfg, catalog, force: _hardwareNotReady >= 3);
            if (report.NotReady)
            {
                _hardwareNotReady++;
                Logger.Warn($"部品の確認を後回しにします（{_hardwareNotReady} 回目）: {report.NotReadyReason}");
                _ = RetryHardwareCheckAsync();
                return;
            }
            if (report.HasChanges)
            {
                string what = report.DeviceChanges.Count > 0 ? string.Join(L(" ／ "), report.DeviceChanges) : L("使っていたセンサーが見つかりません");
                Logger.Warn($"ハードウェアの変更を検出: {what}");
                _tray.ShowBalloon(L("ハードウェアの変更を検出しました"), what, AlertLevel.Warning);
                Notifier.Notify(L("Fanlume: ハードウェアの変更"), L($"{what}。PC の画面で、新しいセンサーへの置き換えを確認してください。"), AlertLevel.Warning);
                ShowMainWindow();
                _window.ShowHardwareChange(report);
                return;
            }
        }
        if (HardwareChange.Remember(cfg, catalog)) _controller.Commit(recordUndo: false);
    }

    private int _hardwareNotReady;

    /// <summary>約 15 秒後にハードウェアを探し直し、見つかったら（次の CatalogReady で）もう一度確認する</summary>
    private async Task RetryHardwareCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (_controller is null || _exiting) return;
        _firstCatalog = true;
        _controller.Engine.RescanHardware();
    }

    private EngineHealth _lastHealth = EngineHealth.Starting;

    private void OnEngineHealth(EngineHealth health, string reason)
    {
        var before = _lastHealth;
        _lastHealth = health;
        switch (health)
        {
            // Recovering（探し直し・復帰直後の再試行）は、エンジン自身が必要なときだけ警告を出すので、ここでは知らせない
            case EngineHealth.Failed:
                _tray?.ShowBalloon(L("ファン制御に問題があります"), L($"{reason}（安全のため BIOS の制御にしています。自動で復旧を試みます）"), AlertLevel.Danger);
                Notifier.Notify(L("Fanlume: ファン制御の問題"), reason, AlertLevel.Danger);
                break;
            case EngineHealth.Degraded when before is EngineHealth.Ok:
                _tray?.ShowBalloon(L("一部の部品を読み取れません"), reason, AlertLevel.Warning);
                break;
            case EngineHealth.Ok when before is EngineHealth.Failed or EngineHealth.Degraded:
                _tray?.ShowBalloon(L("ファン制御が復旧しました"), L("通常の制御に戻りました。"), AlertLevel.Info);
                Notifier.Notify(L("Fanlume: 復旧"), L("ファン制御が通常に戻りました。"), AlertLevel.Info, isRecovery: true);
                break;
        }
    }

    private static AppConfig LoadConfig()
    {
        try
        {
            // 壊れていたら、ConfigStore がバックアップから自動で復旧する
            return ConfigStore.Load();
        }
        catch (Exception ex)
        {
            Logger.Error($"config.json の読み込みに失敗（バックアップからも復旧できず）: {ex}");
            string why = ex is IOException
                ? L("ほかのプログラムが config.json を開いたままにしているため読めませんでした（ファイルはそのままです）。")
                : L("元のファイルは config.broken_日時.json として残しました。");
            MessageBox.Show(L($"config.json を読み込めず、バックアップからも復旧できませんでした。\n{ex.Message}\n\n{why}\n空の設定で起動します。"),
                AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            var fresh = new AppConfig();
            fresh.Normalize();
            return fresh;
        }
    }

    private void UpdateTrayProfiles()
    {
        if (_controller is null || _tray is null) return;
        var cfg = _controller.Config;
        _tray.SetProfiles(cfg.Profiles.Select(p => p.Name), cfg.ActiveProfile, _controller.EffectiveProfile, _controller.EffectiveReason);
        _window?.UpdateProfileIndicators();
    }

    private void UpdateQuickStatus()
    {
        _window?.UpdateQuickStatus();
        _widget?.SetQuickStatus(_controller?.QuickStatusText);
    }

    public void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void TogglePause()
    {
        if (_controller is null) return;
        _paused = !_paused;
        _controller.Engine.SetPaused(_paused);
        _tray?.SetPaused(_paused);
    }

    // =====================================================================
    // ワンタッチ操作（画面・トレイ・ウィジェット・ホットキー・スマホ共通の入口）
    // =====================================================================

    /// <summary>"boost:分" / "quiet:分" / "cancel" / "profile:名前" を実行する。UI スレッドで呼ぶこと</summary>
    public bool ExecuteCommand(string command)
    {
        if (_controller is null) return false;
        string[] parts = command.Split(':', 2);
        string arg = parts.Length > 1 ? parts[1] : "";
        bool ok = true;
        switch (parts[0])
        {
            // 時間は常識的な範囲だけ受け付ける（スマホの画面から変な値が送られてきても長時間にしない）
            case "boost" when int.TryParse(arg, out int m) && m is >= 1 and <= 240:
                _controller.StartBoost(TimeSpan.FromMinutes(m));
                _tray?.ShowBalloon(L("ブースト"), L($"ポンプ以外の全ファンを {m} 分間 100% にします。"), AlertLevel.Info);
                break;
            case "quiet" when int.TryParse(arg, out int m) && m is >= 1 and <= 720:
                ok = _controller.StartQuiet(TimeSpan.FromMinutes(m));
                if (!ok) _window?.ShowAlert(L("「静音」で使うプロファイルが見つかりません。設定画面の「ワンタッチ操作」で選んでください。"), AlertLevel.Warning);
                break;
            case "cancel":
                _controller.CancelQuick();
                break;
            case "led" when arg == "off":
                _controller.Config.Rgb.Enabled = false;
                _controller.Commit(recordUndo: false);
                break;
            case "led" when arg == "on":
                _controller.Config.Rgb.Enabled = true;
                _controller.Commit(recordUndo: false);
                break;
            case "lights" when arg is "off" or "on":
                // 消灯・点灯（連携は続ける）。消灯するときは、連携がオフなら先にオンにする（オフのままだと機器が自分の光り方に戻るため）
                _controller.Config.Rgb.LightsOff = arg == "off";
                if (arg == "off") _controller.Config.Rgb.Enabled = true;
                _controller.Commit(recordUndo: false);
                break;
            case "music" when MusicStyles.All.Contains(arg):
                _controller.Config.Rgb.MusicStyle = arg;
                _controller.Config.Rgb.Mode = RgbModes.Music;
                _controller.Config.Rgb.Enabled = true;
                _controller.Config.Rgb.LightsOff = false; // 光り方を選んだら点灯する
                _controller.Config.Rgb.Music.Preset = MusicPresets.Custom;
                _controller.Commit(recordUndo: false);
                break;
            case "led" when arg is RgbModes.Temperature or RgbModes.Rainbow or RgbModes.RainbowStatic or RgbModes.Breathing or RgbModes.Static or RgbModes.Music:
                _controller.Config.Rgb.Mode = arg;
                _controller.Config.Rgb.Enabled = true;
                _controller.Config.Rgb.LightsOff = false; // 光り方を選んだら点灯する
                _controller.Commit(recordUndo: false);
                break;
            case "profile" when _controller.Config.FindProfile(arg) is not null:
                _controller.SetActiveProfile(arg);
                break;
            default:
                ok = false;
                break;
        }
        UpdateQuickStatus();
        return ok;
    }

    // ----- グローバルホットキー（Ctrl+Alt+B / Q / N） -----

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private static readonly (int Id, uint Key, string Command)[] Hotkeys =
    {
        (0x5101, 0x42, "boost:5"),   // B
        (0x5102, 0x51, "quiet:60"),  // Q
        (0x5103, 0x4E, "cancel"),    // N
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>設定に合わせてホットキーを登録・解除する（ウィンドウを隠していても有効）</summary>
    public void UpdateHotkeys()
    {
        if (_window is null || _controller is null) return;
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        if (_hotkeySource is null)
        {
            _hotkeySource = HwndSource.FromHwnd(handle);
            _hotkeySource.AddHook(HotkeyHook);
        }

        if (_hotkeysRegistered)
        {
            foreach (var h in Hotkeys) UnregisterHotKey(handle, h.Id);
            _hotkeysRegistered = false;
        }
        if (!_controller.Config.Quick.HotkeysEnabled) return;

        foreach (var h in Hotkeys)
        {
            if (!RegisterHotKey(handle, h.Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, h.Key))
                Logger.Warn($"ホットキーを登録できません（他のアプリが使用中の可能性）: {h.Command}");
        }
        _hotkeysRegistered = true;
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        var hit = Hotkeys.FirstOrDefault(h => h.Id == wParam.ToInt32());
        if (hit.Command is not null)
        {
            ExecuteCommand(hit.Command);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ----- スマホから監視 -----

    /// <summary>設定に合わせて Web サーバーを開始・停止する</summary>
    public void UpdateWebServer()
    {
        if (_controller is null || _cleanupStarted) return;
        var cfg = _controller.Config.Web;
        _web ??= new WebServer(() => _statusJson, cmd => Dispatcher.Invoke(() => ExecuteCommand(cmd)));

        if (!cfg.Enabled)
        {
            if (_web.Running)
            {
                _web.Stop();
                Task.Run(WebServer.RemoveFirewall);
            }
            return;
        }
        try
        {
            _web.Start(cfg.Port, cfg.Token);
            int port = cfg.Port;
            Task.Run(() => WebServer.AllowFirewall(port));
        }
        catch (Exception ex)
        {
            Logger.Error($"Web サーバーを開始できません: {ex.Message}");
            _window?.ShowAlert(L($"スマホ用の Web サーバーを開始できませんでした（ポート {cfg.Port}）: {ex.Message}"), AlertLevel.Warning);
        }
    }

    private static readonly JsonSerializerOptions StatusJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>スマホの画面に出す状態。Web サーバーのスレッドから読まれるので、文字列にして渡す</summary>
    private string BuildStatusJson(EngineSnapshot s)
    {
        if (_window is null || _controller is null) return "{}";
        string state = s.Emergency ? L("緊急全開") : s.Paused ? L("一時停止中") : s.DryRun ? "DRY RUN" : L("制御中");
        var status = new
        {
            time = s.Time.ToString("HH:mm:ss"),
            state,
            profile = s.Profile,
            manualProfile = _controller.Config.ActiveProfile,
            quick = _controller.QuickStatusText,
            profiles = _controller.Config.Profiles.Select(p => p.Name).ToArray(),
            temps = _window.TempCardItems.Select(t => new
            {
                title = t.Title,
                value = double.TryParse(t.ValueText, out var v) ? v : (double?)null,
                warn = t.Warn,
                danger = t.Danger,
            }),
            fans = _window.FanCardItems.Select(f => new
            {
                name = f.Name,
                rpm = f.RpmText,
                duty = f.DutyText,
                color = $"#{f.StateBrush.Color.R:X2}{f.StateBrush.Color.G:X2}{f.StateBrush.Color.B:X2}",
            }),
            metrics = _window.MetricTileItems.Select(m => new { title = m.ShortTitle, text = m.ValueText + (m.SubText.Length > 0 ? L($"（{m.SubText}）") : "") }),
        };
        return JsonSerializer.Serialize(status, StatusJsonOptions);
    }

    // ----- ミニウィジェット -----

    private void ToggleWidget()
    {
        if (_widget is null) ShowWidget(); else CloseWidget(save: true);
    }

    private void ShowWidget()
    {
        if (_controller is null || _window is null || _widget is not null) return;
        var cfg = _controller.Config;
        _widget = new WidgetWindow(_window.TempCardItems, _window.MetricTileItems, _window.FanCardItems, cfg.Widget);
        _widget.LayoutChanged += (left, top, opacity) =>
        {
            _controller.Config.Widget.Left = left;
            _controller.Config.Widget.Top = top;
            _controller.Config.Widget.Opacity = opacity;
            _controller.Commit(recordUndo: false);
        };
        _widget.OpenMainRequested += ShowMainWindow;
        _widget.CloseRequested += () => CloseWidget(save: true);
        _widget.CommandRequested += cmd => ExecuteCommand(cmd);
        _widget.SetProfile(_controller.EffectiveProfile);
        _widget.SetQuickStatus(_controller.QuickStatusText);
        _widget.Show();
        SetWidgetState(true);
    }

    private void CloseWidget(bool save)
    {
        _widget?.Close();
        _widget = null;
        if (save) SetWidgetState(false);
    }

    private void SetWidgetState(bool visible)
    {
        if (_controller is null) return;
        _controller.Config.Widget.Visible = visible;
        _controller.Commit(recordUndo: false);
        _tray?.SetWidgetVisible(visible);
        _window?.SetWidgetSwitch(visible);
    }

    private bool _exiting;

    private void ExitApplication()
    {
        if (_exiting) return;
        ShutdownServices();
        Try(L("ウィジェット"), () => _widget?.Close());
        if (_window is not null)
        {
            _window.AllowClose = true;
            Try(L("画面"), () => _window.Close());
        }
        Shutdown();
    }

    /// <summary>
    /// 終了処理。いちばん大事なのは「ファンを BIOS 制御に戻す」ことなので最初に行い、
    /// 戻せたときだけ見張りプロセスに「正しい終了」を伝える（戻せなかったら見張りが安全な回転数にする）。
    /// そのあとの片付けは、1 つ失敗してもほかを続ける
    /// </summary>
    private void ShutdownServices()
    {
        if (_exiting) return;
        _exiting = true;
        Try(L("テスト"), () => _window?.StopTestIfRunning());
        bool restored = true;
        if (_controller is not null)
        {
            try { restored = StopFanEngine(_controller.Engine); }
            catch (Exception ex) { restored = false; Logger.Error($"終了時の BIOS 制御への復帰に失敗: {ex.Message}"); }
        }
        if (restored) Watchdog.SignalStop();
        else
        {
            Logger.Warn("ファンを BIOS 制御に戻せたか確認できないため、見張りプロセスに安全な回転数にしてもらいます");
            if (_watchdogRunning) Watchdog.SignalStopUnsafe();
        }
        Try(L("記録の保存"), () => _window?.SaveReportData());
        Try(L("同期テスト"), () => SyncTestPlayer.Dispose());
        Try("LED", () => Rgb.Dispose());
        Try(L("音の取り込み"), () => Audio.Dispose());
        Try(L("スマホ監視"), () => _web?.Stop());
        Try(L("設定"), () => _controller?.Dispose());
    }

    /// <summary>ファン制御を止めて BIOS 制御に戻す。戻せたことを確認できたら true</summary>
    private static bool StopFanEngine(FanEngine engine)
    {
        return engine.Stop();
    }

    /// <summary>終了処理の 1 つの手順（失敗しても次へ進む）</summary>
    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Warn($"{what}の終了処理に失敗: {ex.Message}"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        ShutdownServices();
        Try(L("タスクトレイ"), () => _tray?.Dispose());
        try { _mutex?.ReleaseMutex(); } catch { /* 持っていない */ }
        _mutex?.Dispose();
        Logger.Flush();
        base.OnExit(e);
    }
}
