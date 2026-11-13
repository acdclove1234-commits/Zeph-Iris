// ==================================================
// MainWindow.Games.cs - レポート画面の「ゲームの記録」: ゲーム中の FPS（PresentMon）と温度を、1 回遊ぶごとに残す
//   見つけ方・まとめ方は Core/Games/GameTracker.cs、PresentMon の動かし方は Core/Games/PresentMonRunner.cs
// Dependencies: WPF / .NET 8 (MIT License) / PresentMon (MIT License, Intel Corporation)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ProFanController;

/// <summary>ゲームの記録の 1 行</summary>
public sealed class GameSessionVm
{
    public required GameSession Session { get; init; }
    public string App => Session.App;

    public string TimeText
    {
        get
        {
            var played = TimeSpan.FromSeconds(Session.PlayedSeconds);
            string length = played.TotalHours >= 1 ? L($"{(int)played.TotalHours} 時間 {played.Minutes} 分") : L($"{Math.Max(1, played.Minutes)} 分");
            string start = Session.Start.ToString("M/d HH:mm");
            return L($"{start} から {length}");
        }
    }

    public string FpsText => L($"平均 {Session.AvgFps:0} fps ・ 1% Low {Session.Low1Fps:0} fps ・ 0.1% Low {Session.Low01Fps:0} fps");

    public string TempText
    {
        get
        {
            var parts = new List<string>();
            if (Session.CpuTempMax is float c) parts.Add(L($"CPU 最高 {c:0}°C（平均 {Session.CpuTempAvg:0}°C）"));
            if (Session.GpuTempMax is float g) parts.Add(L($"GPU 最高 {g:0}°C（平均 {Session.GpuTempAvg:0}°C）"));
            if (Session.FanRpmAvg is float f) parts.Add(L($"ファン平均 {f:0} rpm"));
            return string.Join(" · ", parts);
        }
    }
}

public partial class MainWindow
{
    private readonly GameTracker _gameTracker = new();
    private readonly PresentMonRunner _presentMon = new();
    private readonly ObservableCollection<GameSessionVm> _gameSessions = new();
    private DateTime _presentMonRetryAt = DateTime.MinValue;
    private DateTime _presentMonStartedAt;
    private int _presentMonFailures;
    private string? _presentMonError;
    private bool _gamesLoaded;

    /// <summary>設定の読み込み・切り替えのとき: スイッチを合わせて、PresentMon を動かす・止める</summary>
    private void ApplyGamesConfig()
    {
        GamesSwitch.IsChecked = Config.Games.Enabled;
        _gameTracker.SetExcluded(Config.Games.Excluded);
        if (Config.Games.Enabled) StartPresentMon();
        else StopGames();
        UpdateGamesStatus();
    }

    private void OnGamesSwitchClick(object sender, RoutedEventArgs e)
    {
        Config.Games.Enabled = GamesSwitch.IsChecked == true;
        ScheduleCommit();
        Logger.Info(Config.Games.Enabled ? "ゲームの記録をオンにしました" : "ゲームの記録をオフにしました");
        _presentMonFailures = 0;
        _presentMonRetryAt = DateTime.MinValue;
        ApplyGamesConfig();
    }

    private void StartPresentMon()
    {
        if (_presentMon.Running || !PresentMonRunner.Available || DateTime.Now < _presentMonRetryAt) return;
        try
        {
            _presentMon.Exited -= OnPresentMonExited;
            _presentMon.Exited += OnPresentMonExited;
            _presentMon.Start(_gameTracker.AddFrame);
            _presentMonStartedAt = DateTime.Now;
            _presentMonError = null;
            Logger.Info("PresentMon を起動しました（ゲームの記録）");
        }
        catch (Exception ex)
        {
            _presentMonError = ex.Message;
            PresentMonFailed();
            Logger.Warn($"PresentMon を起動できません: {ex.Message}");
        }
    }

    /// <summary>止めていないのに PresentMon が終わった（スレッドプールから）。少し待ってから起動し直す</summary>
    private void OnPresentMonExited(int code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Logger.Warn($"PresentMon が終わりました（終了コード {code}）");
            _presentMonError = L($"PresentMon が終わりました（終了コード {code}）");
            if (DateTime.Now - _presentMonStartedAt > TimeSpan.FromMinutes(10)) _presentMonFailures = 0;
            PresentMonFailed();
        });
    }

    /// <summary>起動し直すまで、失敗するたびに長く待つ（30 秒・1 分・2 分 … 最長 10 分）</summary>
    private void PresentMonFailed()
    {
        _presentMonFailures++;
        double wait = Math.Min(600, 30 * Math.Pow(2, _presentMonFailures - 1));
        _presentMonRetryAt = DateTime.Now.AddSeconds(wait);
    }

    /// <summary>記録をやめる: 遊んでいる途中の記録を残して、PresentMon を止める</summary>
    private void StopGames()
    {
        SaveGameSession(_gameTracker.Finish());
        if (_presentMon.Running) Task.Run(_presentMon.Stop);
    }

    /// <summary>スナップショットごと（MainWindow.Report.cs の RecordLongTerm から）</summary>
    private void TickGames(EngineSnapshot s)
    {
        if (!Config.Games.Enabled) return;
        if (!_presentMon.Running) StartPresentMon();

        float? Read(string role) => _roles.TryGetValue(role, out var id) && s.Values.TryGetValue(id, out var v) ? v : null;
        var rpms = s.Fans.Where(f => f.Rpm is > 0).Select(f => f.Rpm!.Value).ToList();
        var tick = new GameTick(s.Time, ForegroundApp.ProcessId(),
            Read(StatRoles.CpuTemp), Read(StatRoles.GpuTemp), Read(StatRoles.CpuLoad), Read(StatRoles.GpuLoad), Read(StatRoles.GpuPower),
            rpms.Count > 0 ? (float)rpms.Average() : null);
        SaveGameSession(_gameTracker.Tick(tick));
        if (ReportPage.Visibility == Visibility.Visible) UpdateGamesStatus();
    }

    private void SaveGameSession(GameSession? g)
    {
        if (g is null || !GameLog.WorthKeeping(g)) return;
        try
        {
            GameLog.Append(g);
            Logger.Info($"ゲームの記録を残しました（{TimeSpan.FromSeconds(g.PlayedSeconds):h\\:mm\\:ss}・平均 {g.AvgFps:0} fps）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ゲームの記録を保存できません: {ex.Message}");
        }
        if (_gamesLoaded) _gameSessions.Insert(0, new GameSessionVm { Session = g });
    }

    private void UpdateGamesStatus()
    {
        if (GamesStatus is null) return;
        GamesStatus.Text = !Config.Games.Enabled
            ? L("オフです。オンにすると、ゲームを遊んでいるあいだの FPS と温度を記録します。")
            : !PresentMonRunner.Available
                ? L("PresentMon が見つかりません（アプリのフォルダーの PresentMon フォルダー）。アプリを入れ直すと直ります。")
                : _gameTracker.CurrentApp is { } app
                    ? L($"記録中: {app} ・ いま {_gameTracker.CurrentFps:0} fps")
                    : _presentMon.Running
                        ? L("ゲームを待っています（ゲームを手前に出して 1 分ほど遊ぶと、記録を始めます）。")
                        : L($"PresentMon を起動し直すのを待っています（{_presentMonError}）。");
        GamesEmpty.Visibility = _gameSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>レポート画面を開いたとき</summary>
    private void RefreshGames()
    {
        if (!_gamesLoaded)
        {
            _gamesLoaded = true;
            GamesList.ItemsSource = _gameSessions;
            try
            {
                foreach (var g in GameLog.Load(limit: 100)) _gameSessions.Add(new GameSessionVm { Session = g });
            }
            catch (Exception ex)
            {
                Logger.Warn($"ゲームの記録を読めません: {ex.Message}");
            }
        }
        UpdateGamesStatus();
    }

    private void OnGameDetailsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameSessionVm vm) return;
        var g = vm.Session;
        string played = TimeSpan.FromSeconds(g.PlayedSeconds).ToString(@"h\:mm\:ss");
        string start = g.Start.ToString("yyyy/MM/dd HH:mm"), end = g.End.ToString("HH:mm"), frames = g.Frames.ToString("N0");
        var lines = new List<string>
        {
            vm.TimeText,
            L($"{start} 〜 {end}（描いていた時間 {played}・{frames} フレーム）"),
            "",
            vm.FpsText,
            vm.TempText,
        };
        if (g.CpuLoadAvg is float cl) lines.Add(L($"CPU 使用率 平均 {cl:0}%"));
        if (g.GpuLoadAvg is float gl) lines.Add(L($"GPU 使用率 平均 {gl:0}%"));
        if (g.GpuPowerAvg is float gp) lines.Add(L($"GPU の電力 平均 {gp:0} W"));
        if (g.Timeline.Count >= 2)
        {
            double worst = g.Timeline.Min(p => p.Fps), best = g.Timeline.Max(p => p.Fps);
            lines.Add("");
            lines.Add(L($"10 秒ごとの平均 FPS: いちばん低い {worst:0} fps ・ いちばん高い {best:0} fps"));
        }
        if (g.Stalls > 0)
            lines.Add(L($"1 秒以上止まった: {g.Stalls} 回・合計 {g.StalledSeconds:0} 秒（ロード・一時停止・最小化など。FPS と描いていた時間には入れていません）"));
        lines.Add("");
        lines.Add(L("1% Low・0.1% Low は、遅かったほうから 1%・0.1% のところのフレーム時間（99・99.9 パーセンタイル）を FPS にしたものです。1 秒を超えて止まったフレームは入れません。平均との差が大きいほど、カクつきを感じやすくなります（ほかのソフトは、遅いほうから 1% のフレームの平均で出すことがあり、値が少し違います）。"));
        lines.Add(L("温度・使用率・ファンは、FPS と同じく、最初に描いたときから描いていたあいだの値です。"));
        MessageBox.Show(this, string.Join("\n", lines), g.App, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnGameExcludeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameSessionVm vm) return;
        if (MessageBox.Show(this, L($"{vm.App} を、これからは記録しないようにしますか？（ゲームではないアプリが記録されたとき用です。これまでの記録も消します）"),
                L("ゲームの記録"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!Config.Games.Excluded.Contains(vm.App, StringComparer.OrdinalIgnoreCase)) Config.Games.Excluded.Add(vm.App);
        ScheduleCommit();
        _gameTracker.SetExcluded(Config.Games.Excluded);
        DeleteGameSessions(s => s.App.Equals(vm.App, StringComparison.OrdinalIgnoreCase));
    }

    private void OnGameDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameSessionVm vm) return;
        DeleteGameSessions(s => GameLog.Same(s, vm.Session));
    }

    private void DeleteGameSessions(Func<GameSession, bool> match)
    {
        try
        {
            GameLog.Rewrite(s => !match(s));
        }
        catch (Exception ex)
        {
            Logger.Warn($"ゲームの記録を消せません: {ex.Message}");
            return;
        }
        foreach (var vm in _gameSessions.Where(v => match(v.Session)).ToList()) _gameSessions.Remove(vm);
        UpdateGamesStatus();
    }

    private void OnOpenGamesFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LongTermStore.Directory);
        SystemTool.Open(LongTermStore.Directory);
    }

    /// <summary>終了時（SaveReportData から）: 遊んでいる途中の記録を残して、PresentMon を止める</summary>
    private void SaveGamesOnExit()
    {
        SaveGameSession(_gameTracker.Finish());
        _presentMon.Stop();
    }
}
