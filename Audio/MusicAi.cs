// ==================================================
// MusicAi.cs - 音楽連動の拍の AI と曲の記憶をまとめる（モデルの読み込み・曲の変わり目の見張り・取り込みへの取り付け）
// Dependencies: .NET 8 / WPF (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Windows.Threading;

namespace ProFanController;

/// <summary>
/// 音楽連動を使っている間だけ、拍の AI のモデルを読み込む（約 0.8 秒・メモリを使うので、使わないときは外す）。
/// 曲の記憶をオンにしていれば、再生アプリのウィンドウのタイトル（「アーティスト - 曲名」）が変わったのを 1 秒ごとに見て、
/// 曲が変わったと知らせる。タイトルはその場で SHA-256 の印にするだけで、保存も記録もしない
/// </summary>
public sealed class MusicAi : IDisposable
{
    /// <summary>タイトルを見るアプリ（アプリの音だけを取り込んでいないとき。ふだん曲名をタイトルに出すもの）</summary>
    private static readonly string[] Players = { "Spotify" };

    private readonly AudioCapture _audio;
    private readonly DispatcherTimer _timer;
    private BeatThis? _model;
    private bool _loading;
    private bool _wantAi, _wantMemory;
    private string? _lastKey = "";

    /// <summary>曲の記憶（data/songs）</summary>
    public SongMemory Memory { get; }

    public MusicAi(AudioCapture audio)
    {
        _audio = audio;
        Memory = new SongMemory(Path.Combine(AppContext.BaseDirectory, "data", "songs")) { Log = m => Logger.Info(m) };
        _audio.AssistFactory = CreateAssist;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => WatchTitle();
    }

    /// <summary>音楽連動の設定に合わせる（音楽連動が動いているか・AI・記憶のオンオフ）</summary>
    public void Apply(bool musicRunning, bool ai, bool memory)
    {
        bool wantAi = musicRunning && ai, wantMemory = musicRunning && memory && ai;
        if (wantAi == _wantAi && wantMemory == _wantMemory) return;
        _wantAi = wantAi;
        _wantMemory = wantMemory;
        if (wantMemory) _timer.Start();
        else
        {
            _timer.Stop();
            if (_lastKey is { Length: > 0 }) Memory.BeginSong(null);
            _lastKey = "";
        }
        if (!wantAi)
        {
            _audio.RefreshAssist();   // AssistFactory が null を返すので外れる
            _model?.Dispose();
            _model = null;
            Logger.Info("拍の AI を止めました");   // no-loc（ログだけ）
            return;
        }
        if (_model is not null) { _audio.RefreshAssist(); return; }
        if (_loading) return;
        _loading = true;
        // モデルの読み込みは少し時間がかかるので、画面を止めないよう別のスレッドで
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var m = BeatThis.TryCreate(out string? err);
            Logger.Info(m is null ? $"拍の AI を読み込めません: {err}" : $"拍の AI を読み込みました（{sw.ElapsedMilliseconds} ms）");   // no-loc（ログだけ）
            return m;
        }).ContinueWith(t =>
        {
            _loading = false;
            if (!_wantAi) { t.Result?.Dispose(); return; }
            _model = t.Result;
            _audio.RefreshAssist();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private BeatAssist? CreateAssist(int rate)
    {
        if (!_wantAi || _model is null) return null;
        return new BeatAssist(_model, rate)
        {
            Memory = _wantMemory ? Memory : null,
            Log = m => Logger.Info(m),
        };
    }

    /// <summary>再生アプリのタイトルから、曲が変わったかを見る（1 秒ごと）</summary>
    private void WatchTitle()
    {
        string? key = null;
        try
        {
            var (app, title) = CurrentTitle();
            // 曲を流している間だけ「アーティスト - 曲名」の形になる（止めると「Spotify Premium」など）
            if (title.Contains(" - ", StringComparison.Ordinal)) key = SongMemory.KeyOf(app, title);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (key == _lastKey) return;
        _lastKey = key;
        Memory.BeginSong(key);
    }

    private (string App, string Title) CurrentTitle()
    {
        IEnumerable<Process> procs;
        if (_audio.AppOnly && _audio.SourcePid > 0)
        {
            string name;
            try { using var p = Process.GetProcessById(_audio.SourcePid); name = p.ProcessName; }
            catch (ArgumentException) { return ("", ""); }
            procs = Process.GetProcessesByName(name);
        }
        else procs = Players.SelectMany(Process.GetProcessesByName);
        string app = "", title = "";
        foreach (var p in procs)
        {
            try
            {
                if (title.Length == 0 && p.MainWindowTitle is { Length: > 0 } t) { title = t; app = p.ProcessName; }
            }
            finally { p.Dispose(); }
        }
        return (app, title);
    }

    public void Dispose()
    {
        _timer.Stop();
        _audio.AssistFactory = null;
        _audio.RefreshAssist();
        _model?.Dispose();
    }
}
