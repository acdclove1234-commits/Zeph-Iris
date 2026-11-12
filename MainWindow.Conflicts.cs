// ==================================================
// MainWindow.Conflicts.cs - ファン・LED を取り合うほかのソフト（メーカーのソフト・FanControl など）が動いたら知らせる
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Threading;

namespace ProFanController;

public partial class MainWindow
{
    /// <summary>はじめて調べるまでの時間（起動直後のほかのお知らせとぶつからないよう、少し待つ）と、そのあと調べる間隔</summary>
    private static readonly TimeSpan ConflictFirstCheck = TimeSpan.FromSeconds(20), ConflictInterval = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _conflictTimer;
    /// <summary>この起動のあいだに、もう知らせたソフト（終了したら外し、また起動したらもう一度知らせる）</summary>
    private readonly HashSet<string> _conflictsShown = new();
    private bool _conflictChecking;

    /// <summary>ハードウェアが見つかったら見張りを始める（何度呼んでも 1 つだけ）</summary>
    private void StartConflictWatch()
    {
        if (_conflictTimer is not null) return;
        _conflictTimer = new DispatcherTimer { Interval = ConflictFirstCheck };
        _conflictTimer.Tick += async (_, _) =>
        {
            _conflictTimer.Interval = ConflictInterval;
            await CheckConflictsAsync();
        };
        _conflictTimer.Start();
    }

    private async Task CheckConflictsAsync()
    {
        if (_conflictChecking) return;
        _conflictChecking = true;
        try
        {
            // 動いているアプリの一覧は少し時間がかかるので、画面のスレッドでは読まない
            var running = await Task.Run(InstallCheck.FindRunningConflictApps);
            _conflictsShown.IntersectWith(running.Select(a => a.Label));   // 終了したソフトは忘れる
            var fresh = running
                .Where(a => a.Matters(Config.Rgb.Enabled, Config.Safety.AllowGpuFanControl) && !_conflictsShown.Contains(a.Label))
                .ToList();
            if (fresh.Count == 0) return;
            foreach (var a in fresh) _conflictsShown.Add(a.Label);
            Logger.Warn("取り合いになるソフトが動いています: " + string.Join(", ", fresh.Select(a => $"{a.Label}（{a.Kind}）")));   // no-loc（ログだけ）
            ShowConflictAlert(fresh);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ほかのソフトを確かめられません: {ex.Message}");   // no-loc（ログだけ）
        }
        finally
        {
            _conflictChecking = false;
        }
    }

    private void ShowConflictAlert(IReadOnlyList<ConflictingApp> apps)
    {
        bool fans = apps.Any(a => a.FightsFans);
        bool leds = apps.Any(a => a.Kind is ConflictKind.Led or ConflictKind.Both);
        string what = fans && leds ? L("ファンの回転と LED の色を取り合うので、回転数が勝手に変わる・色が戻る・ちらつく原因になります。")
                    : fans ? L("ファンの回転を取り合うので、回転数が勝手に変わる・設定が反映されない原因になります。")
                    : L("LED の色を取り合うので、色が勝手に戻る・ちらつく原因になります。");
        string text = L($"{string.Join(Localization.ListSeparator, apps.Select(a => a.Label))} が動いています。") + "\n" + what
                      + string.Concat(apps.Where(a => a.Tip is not null).Select(a => "\n" + L("・") + $"{a.Label}: {a.Tip}"));

        // FanControl から乗り換える人には、カーブを移すボタンを出す
        if (apps.Any(a => a.Process == "FanControl"))
            ShowAlert(text, AlertLevel.Warning, L("FanControl から取り込む…"), () => OnImportFanControlClick(this, new RoutedEventArgs()));
        else if (!fans)
            ShowAlert(text, AlertLevel.Info, L("ライティングを開く"), () => NavLighting.IsChecked = true);
        else
            ShowAlert(text, AlertLevel.Warning);
    }
}
