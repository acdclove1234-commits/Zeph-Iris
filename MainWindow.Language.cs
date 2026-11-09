// ==================================================
// MainWindow.Language.cs - 表示言語の切り替え（画面を作り直すときの、前の画面の片付けと引き継ぎ）
//   言語を選び直したら、アプリは止めずにメイン画面だけを新しい言語で作り直す（App.SwitchLanguage）。
//   ファンの制御・LED・音の取り込みは App とエンジンが持っているので止まらない。
//   画面が持っている記録（履歴・1 分ごとの記録・電力量・異常の記録・ライティングの元に戻す履歴）は、新しい画面へ渡す
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProFanController.UI;

namespace ProFanController;

/// <summary>作り直す前の画面から、新しい画面へ渡すもの</summary>
public sealed class MainWindowCarry
{
    public required HistoryStore History { get; init; }
    public required LongTermStore LongTerm { get; init; }
    public required EnergyMeter Energy { get; init; }
    public required FlightRecorder Recorder { get; init; }
    public bool HistoryRestored { get; init; }
    public DateTime NextHealthCheck { get; init; }
    public LightingHistory? LightHistory { get; init; }
    public HardwareCatalog? Catalog { get; init; }
    public EngineSnapshot? LastSnapshot { get; init; }

    // 見ていた場所（同じページ・同じタブ・同じ位置で開き直す）
    public string Page { get; init; } = "";
    public string SettingsTab { get; init; } = "";
    public double SettingsScroll { get; init; }
    public Rect Bounds { get; init; }
    public WindowState State { get; init; }
    public bool Visible { get; init; }
    public bool Active { get; init; }
}

public partial class MainWindow
{
    /// <summary>表示言語の切り替えで片付けた（新しい画面に引き継いだ）画面</summary>
    private bool _detached;
    private Action? _themeChanged;

    private static readonly string[] NavNames =
        { "NavDashboard", "NavDevices", "NavCurves", "NavHistory", "NavReport", "NavTools", "NavTest", "NavLighting", "NavSensors", "NavSettings" };
    private static readonly string[] SettingsTabNames = { "SetTabGeneral", "SetTabAuto", "SetTabLink", "SetTabSafety" };

    /// <summary>
    /// いま表示言語を切り替えられない理由（テスト・調整の途中は、画面を作り直すと結果を受け取れなくなる）。
    /// 切り替えられるなら null
    /// </summary>
    public string? LanguageSwitchBlocker() =>
        _tuner is not null ? L("おまかせ調整の途中です。")
        : _test is not null ? L("冷却テストの途中です。")
        : _calibratingId is not null ? L("キャリブレーションの途中です。")
        : _listenId is not null && DateTime.Now < _listenUntil ? L("ファンの音を確かめている途中です。")
        : _wizard is not null ? L("セットアップの途中です。")
        : null;

    /// <summary>
    /// 新しい画面を作る前に呼ぶ: 保留中の変更を保存し、設定・エンジンへの登録を外して、引き継ぐものを返す。
    /// このあと App が新しい画面を作り、この画面を閉じる
    /// </summary>
    public MainWindowCarry DetachForReload()
    {
        if (_commitTimer.IsEnabled) FlushCommit();
        _commitTimer.Stop();

        string page = NavNames.FirstOrDefault(n => FindName(n) is RadioButton { IsChecked: true }) ?? "";
        string tab = SettingsTabNames.FirstOrDefault(n => FindName(n) is RadioButton { IsChecked: true }) ?? "";
        var carry = new MainWindowCarry
        {
            History = _history,
            LongTerm = _longTerm,
            Energy = _energy,
            Recorder = _recorder,
            HistoryRestored = _historyRestored,
            NextHealthCheck = _nextHealthCheck,
            LightHistory = _lightHistory,
            Catalog = _catalog,
            LastSnapshot = _lastSnapshot,
            Page = page,
            SettingsTab = tab,
            SettingsScroll = SettingsScroller.VerticalOffset,
            Bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds,
            State = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState,
            Visible = IsVisible,
            Active = IsActive,
        };

        _detached = true;
        _controller.ConfigReplaced -= RebindAll;
        _controller.ConfigReplaced -= OnLightingConfigReplaced;
        _controller.Committed -= UpdateUndoButtons;
        _controller.EffectiveProfileChanged -= UpdateProfileIndicators;
        foreach (var o in _watched) o.PropertyChanged -= OnConfigPropertyChanged;
        _watched.Clear();
        if (_litRgb is not null) _litRgb.PropertyChanged -= OnLightingConfigChanged;
        if (_litMusic is not null) _litMusic.PropertyChanged -= OnLightingConfigChanged;
        if (_themeChanged is not null) ThemeManager.Changed -= _themeChanged;
        _recorder.Saved -= OnRecordSaved;
        LightingPage.IsVisibleChanged -= OnLightingPageVisibleChanged;
        StopLightingLive();
        StopSyncTest();
        StopWhiteTest();
        _advisorCts?.Cancel();
        return carry;
    }

    /// <summary>
    /// 作り直した画面: 前の画面が使っていたハードウェアの一覧を入れ、同じページ・タブ・位置で表示する
    /// （温度などの値は、次のエンジンの更新（1 秒以内）で入る）
    /// </summary>
    public void ResumeFrom(MainWindowCarry c)
    {
        if (c.Catalog is not null) OnCatalogReady(c.Catalog);
        _lastSnapshot = c.LastSnapshot;
        if (c.Page.Length > 0 && FindName(c.Page) is RadioButton nav) nav.IsChecked = true;
        if (c.SettingsTab.Length > 0 && FindName(c.SettingsTab) is RadioButton tab) tab.IsChecked = true;

        if (c.Bounds.Width > 0 && c.Bounds.Height > 0)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = c.Bounds.Left;
            Top = c.Bounds.Top;
            Width = c.Bounds.Width;
            Height = c.Bounds.Height;
        }
        if (!c.Visible) return;
        Show();
        WindowState = c.State;
        if (c.Active) Activate();
        // 設定画面のスクロール位置は、中身の大きさが決まってから戻す
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SettingsScroller.ScrollToVerticalOffset(c.SettingsScroll));
    }
}
