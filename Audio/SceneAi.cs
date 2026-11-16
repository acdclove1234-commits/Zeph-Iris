// ==================================================
// SceneAi.cs - 音楽連動の楽器の AI（CED-Tiny）をまとめる（モデルの読み込み・取り込みへの取り付け・止めたときの片付け）
// Dependencies: .NET 8 / WPF (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

/// <summary>
/// 音楽連動が動いていて「AI で楽器を聞き分ける」がオンの間だけ、楽器の AI のモデルを読み込む（0.1 秒ほど・メモリ約 35 MB。
/// 使わないときは外してメモリを返す）。読み込めなかったら、アプリを起動し直すまで試さない（場面なしのまま、静かに動く）
/// </summary>
public sealed class SceneAi : IDisposable
{
    private readonly AudioCapture _audio;
    private CedTiny? _model;
    private bool _loading;
    private bool _want;
    private bool _failed;

    public SceneAi(AudioCapture audio)
    {
        _audio = audio;
        _audio.TaggerFactory = CreateTagger;
    }

    /// <summary>音楽連動の設定に合わせる（音楽連動が動いているか・楽器の AI のオンオフ）。画面のスレッドから呼ぶ</summary>
    public void Apply(bool musicRunning, bool scenesOn)
    {
        bool want = musicRunning && scenesOn;
        if (want == _want) return;
        _want = want;
        if (!want)
        {
            _audio.RefreshTagger();   // TaggerFactory が null を返すので外れる（待たない）
            // モデルを手放す（計算中の楽器の AI のスレッドがあれば、それが終わったときに片付く）
            if (_model is not null)
            {
                _model.Dispose();
                _model = null;
                Logger.Info("楽器の AI を止めました");   // no-loc（ログだけ）
            }
            return;
        }
        if (_model is not null) { _audio.RefreshTagger(); return; }
        if (_loading || _failed) return;
        _loading = true;
        // モデルの読み込みは少し時間がかかるので、画面を止めないよう別のスレッドで
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var m = CedTiny.TryCreate(out string? err);
            Logger.Info(m is null ? $"楽器の AI を読み込めません: {err}" : $"楽器の AI を読み込みました（{sw.ElapsedMilliseconds} ms）");   // no-loc（ログだけ）
            return m;
        }).ContinueWith(t =>
        {
            _loading = false;
            var m = t.IsCompletedSuccessfully ? t.Result : null;
            if (m is null) { _failed = true; return; }
            if (!_want) { m.Dispose(); return; }
            _model = m;
            _audio.RefreshTagger();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private SceneTagger? CreateTagger(int rate)
    {
        if (!_want || _model is null) return null;
        return new SceneTagger(_model, rate) { Log = m => Logger.Info(m) };
    }

    public void Dispose()
    {
        _want = false;
        _audio.TaggerFactory = null;
        _audio.RefreshTagger();
        _model?.Dispose();
        _model = null;
    }
}
