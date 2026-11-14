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
/// 曲の記憶をオンにしていれば、流れている曲（Spotify のウィンドウのタイトル、または Windows のメディアの情報の「アーティスト - 曲名」）が
/// 変わったのを 1 秒ごとに見て、曲が変わったと知らせる。曲名はその場で SHA-256 の印にするだけで、保存も記録もしない
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
    private bool _watching;
    private long _mediaRetryAt;           // メディアの情報を次に試す時刻（Environment.TickCount64）。失敗したら少し待ってから試し直す
    private bool _mediaDown;              // 読めない状態をログに出したか（同じことを何度も出さない）

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

    /// <summary>曲が変わったかを見る（1 秒ごと。メディアの情報は少し待つことがあるので、画面を止めないよう別のスレッドで読む）</summary>
    private void WatchTitle()
    {
        if (_watching) return;
        _watching = true;
        bool appOnly = _audio.AppOnly && _audio.SourcePid > 0;
        int pid = _audio.SourcePid;
        Task.Run(() => FindKey(appOnly, pid)).ContinueWith(t =>
        {
            _watching = false;
            if (!_wantMemory) return;   // 見ているあいだに曲の記憶をやめた
            SongId? id = t.IsCompletedSuccessfully ? t.Result : null;
            if (id?.Key == _lastKey) return;
            _lastKey = id?.Key;
            if (id is { } s) Memory.BeginSong(s.Key, s.Legacy);
            else Memory.BeginSong(null);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>いま流れている曲の印（分からなければ null）</summary>
    private SongId? FindKey(bool appOnly, int pid)
    {
        string? source = null;
        if (appOnly)
        {
            try { using var p = Process.GetProcessById(pid); source = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
        }
        bool spotify = !appOnly || string.Equals(source, MediaSession.SpotifyApp, StringComparison.OrdinalIgnoreCase);

        // 1. Spotify のウィンドウのタイトル。前から覚えている曲と同じ印になるよう、先に見る
        //    （曲を流している間だけ「アーティスト - 曲名」の形になる。止めると「Spotify Premium」など）
        if (spotify && WindowTitle(source) is { } w) return SongMemory.IdOf(w.App, w.Title);

        // 2. Windows のメディアの情報（YouTube Music・Apple Music・ブラウザーなど。流れている曲だけ）
        //    Windows のメディアのサービスが止まったときなども、少し待ってから試し直すので、アプリを起動し直さなくても戻る
        long now = Environment.TickCount64;
        if (now >= Interlocked.Read(ref _mediaRetryAt))
        {
            try
            {
                var song = MediaSession.Current(appOnly ? source : null);
                if (_mediaDown)
                {
                    _mediaDown = false;
                    Logger.Info("メディアの情報をまた読めるようになりました");   // no-loc（ログだけ）
                }
                return song is { } s ? SongMemory.IdOf(s.App, s.Title) : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                Interlocked.Exchange(ref _mediaRetryAt, MediaSession.NextTry(now, ex));
                if (!_mediaDown)
                {
                    _mediaDown = true;
                    Logger.Info($"メディアの情報を読めないので、しばらく再生アプリのウィンドウのタイトルで曲を見分けます: {ex.Message}");   // no-loc（ログだけ）
                }
            }
        }

        // 3. メディアの情報を使えないときは、前と同じく、音を取り込んでいるアプリのウィンドウのタイトル
        if (appOnly && !spotify && WindowTitle(source) is { } a) return SongMemory.IdOf(a.App, a.Title);
        return null;
    }

    /// <summary>アプリ（null なら Spotify）のウィンドウのタイトルが「アーティスト - 曲名」の形なら返す</summary>
    private static (string App, string Title)? WindowTitle(string? processName)
    {
        Process[] procs;
        try { procs = processName is null ? Players.SelectMany(Process.GetProcessesByName).ToArray() : Process.GetProcessesByName(processName); }
        catch (InvalidOperationException) { return null; }
        string app = "", title = "";
        foreach (var p in procs)
        {
            try
            {
                if (title.Length == 0 && p.MainWindowTitle is { Length: > 0 } t) { title = t; app = p.ProcessName; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        return title.Contains(" - ", StringComparison.Ordinal) ? (app, title) : null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _audio.AssistFactory = null;
        _audio.RefreshAssist();
        _model?.Dispose();
    }
}
