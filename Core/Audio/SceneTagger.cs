// ==================================================
// SceneTagger.cs - 楽器の AI（YAMNet）で、曲の部分ごとに目立つ楽器（場面）を調べる
//   鳴っている音を 16,000 Hz にして貯め、0.48 秒ごとに直近 0.975 秒を優先度の低い専用のスレッドで調べる
// Dependencies: .NET 8 (MIT License), NAudio (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using NAudio.Dsp;

namespace ProFanController;

/// <summary>
/// 楽器の AI で、いまの場面（目立つ楽器。<see cref="MusicScene"/>）を決める。一瞬の音ではなく曲の部分を見るので、
/// 結果は 1.5〜2.5 秒ほど遅れて出る（光り方はそこから MusicScene.FadeSec 秒かけて移る。ビートの光には使わない）。
/// 取り込みスレッドから <see cref="Push"/>、分析のコマごとに <see cref="Tick"/> を呼ぶ（どちらも同じスレッド。待たない・メモリを確保しない）。
/// AI は優先度のいちばん低い専用のスレッドで動かす（光の描画・音の取り込みを邪魔しない）。前の計算が終わっていなければ、その回は飛ばす
/// </summary>
public sealed class SceneTagger : IDisposable
{
    /// <summary>この音量（dBFS。窓全体の 2 乗平均）より小さい窓は、AI を使わず静かとみなす</summary>
    public const double QuietDb = -60;
    /// <summary>AI に渡す前に、窓の音量をこの大きさ（dBFS）にそろえる（YAMNet は小さい音だと楽器を見分けにくい）。倍率の範囲</summary>
    public const double TargetDb = -18;
    public const float MinGain = 0.25f, MaxGain = 32f;
    /// <summary>この秒数だけ音が鳴るたびに、数字だけの統計を 1 行ログに出す</summary>
    public const double StatsEverySec = 60;
    /// <summary>リングの長さ（16,000 Hz の個数。窓より少し長い）</summary>
    private const int RingSamples = 16000;

    private readonly Func<float[], float[], bool> _classify;
    private readonly Resampler16k _rs;
    private readonly SamplesSink _sink;
    private readonly float[] _ring = new float[RingSamples];
    private int _ringPos, _ringCount;
    /// <summary>リングのいちばん新しい音の、次の音の時刻（秒）</summary>
    private double _ringEndT = double.NaN;

    // AI に渡す窓（交互に使う）・音量をそろえた窓・結果
    private readonly float[][] _win = { new float[Yamnet.WindowSamples], new float[Yamnet.WindowSamples] };
    private int _nextWin;
    private readonly float[] _scaled = new float[Yamnet.WindowSamples];
    private readonly float[] _scores = new float[Yamnet.Classes];
    private readonly float[] _groups = new float[SceneClasses.Count];
    /// <summary>場面の決め方（AI のスレッドだけが触る。Inline のときは取り込みスレッド）</summary>
    private readonly SceneTracker _tracker = new();

    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    /// <summary>AI のスレッドが計算中（1）。取り込みスレッドが頼む前に 1 にし、AI のスレッドが終わったら 0 にする</summary>
    private int _busy;
    // 頼んだ計算（_busy を 1 にする前に書き、AI のスレッドは _busy が 1 のときだけ読む）
    private int _jobWin;
    private double _jobEndT;
    private int _jobGen;
    private bool _jobLog;

    /// <summary>取り込みの回（Reset のたびに 1 つ進める）。前の回の音で出た結果は使わない</summary>
    private int _gen;
    /// <summary>場面の決め方を最初からにする合図（取り込みスレッドが書き、AI のスレッドが次の計算の前に読む）</summary>
    private int _trackerReset;
    /// <summary>AI のスレッドが終わるとき（モデルを手放す）</summary>
    private readonly Action? _onExit;

    private double _lastJobT = double.NegativeInfinity;
    private double _lastTickT = double.NaN;
    private double _soundSec;
    private bool _logDue;

    private MusicScene _scene = MusicScene.None;
    private volatile bool _failed;
    private int _windows, _quiet, _skipped, _switches;
    private double _msTotal;
    // ログの前回の値（AI のスレッドだけ）
    private int _logWindows, _logQuiet, _logSkipped, _logSwitches;
    private double _logMs;

    /// <summary>いまの場面（どのスレッドから読んでもよい。丸ごと差し替えられる）</summary>
    public MusicScene Current => Volatile.Read(ref _scene);
    /// <summary>AI が失敗して止まった（場面なしのまま）</summary>
    public bool Failed => _failed;
    /// <summary>AI で調べた回数・静かだった回数・前の計算が終わっていなくて飛ばした回数・場面を切り替えた回数</summary>
    public int Windows => Volatile.Read(ref _windows);
    public int Quiet => Volatile.Read(ref _quiet);
    public int Skipped => Volatile.Read(ref _skipped);
    public int Switches => Volatile.Read(ref _switches);
    /// <summary>1 回の AI の計算の平均（ミリ秒）</summary>
    public double AvgMs { get { int n = Windows; return n > 0 ? Volatile.Read(ref _msTotal) / n : 0; } }
    /// <summary>調査用の記録（ログ）</summary>
    public Action<string>? Log { get; init; }
    /// <summary>テスト・調査用: AI をその場で動かし、結果をすぐ使う（実時間より速く音を流すとき）</summary>
    public bool Inline { get; init; }
    /// <summary>テスト・調査用: AI で調べるたびに、窓の終わりの時刻・まとまりの値（そのままの値）・場面の決め方（この窓を入れたあと）を渡す（AI のスレッドから）</summary>
    internal Action<double, float[], SceneTracker>? Diag { get; init; }

    /// <summary>モデルを使う（AI のスレッドが終わるまでモデルを手放さない。持ち主が先に Dispose しても、計算中に片付かない）</summary>
    public SceneTagger(Yamnet model, int inputRate) : this(Acquire(model), inputRate, model.Release) { }

    private static Func<float[], float[], bool> Acquire(Yamnet model)
    {
        if (!model.TryAcquire()) throw new ObjectDisposedException(nameof(Yamnet));
        return (w, s) => { model.Classify(w, s); return true; };
    }

    /// <summary>テスト用: AI の代わりに classifier（窓 → 521 個のらしさ。false なら結果なし）を使う。onExit = AI のスレッドが終わるとき</summary>
    internal SceneTagger(Func<float[], float[], bool> classifier, int inputRate, Action? onExit = null)
    {
        _classify = classifier;
        _onExit = onExit;
        try
        {
            _rs = new Resampler16k(inputRate);
            _sink = OnResampled;   // 毎回ラムダを作らないよう、1 回だけ作っておく
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "楽器の AI", Priority = ThreadPriority.Lowest };   // no-loc（スレッドの名前）
            _worker.Start();
        }
        catch
        {
            onExit?.Invoke();
            throw;
        }
    }

    /// <summary>鳴っている音を足す（AudioEngine.Push と同じ音・同じ時刻）</summary>
    public void Push(ReadOnlySpan<float> mono, double tFirst)
    {
        if (_failed)
        {
            SettleToNone(tFirst);
            return;
        }
        // 時刻が飛んだ（取り込みが途切れた）ら、たまった音を捨てる
        if (double.IsFinite(_ringEndT) && Math.Abs(tFirst - _ringEndT) > 0.25) _ringCount = 0;
        if (_ringCount == 0) _ringEndT = tFirst;
        _rs.Push(mono, _sink);
    }

    private void OnResampled(ReadOnlySpan<float> s)
    {
        var ring = _ring;
        int pos = _ringPos;
        foreach (float x in s)
        {
            ring[pos] = float.IsFinite(x) ? x : 0;
            if (++pos == RingSamples) pos = 0;
        }
        _ringPos = pos;
        _ringCount = Math.Min(RingSamples, _ringCount + s.Length);
        _ringEndT += s.Length / (double)Yamnet.SampleRate;
    }

    /// <summary>
    /// 分析のコマごとに呼ぶ（テンポの追跡を使わないときも）。sounding = 音が鳴っている（門が開いていて反応の強さが十分）。
    /// 0.48 秒ごとに、直近の窓を AI のスレッドに渡す（計算中なら飛ばす）。鳴っていなければ AI を使わず「静か」と数える
    /// </summary>
    public void Tick(double tHop, bool sounding)
    {
        if (_failed || _stop) return;
        if (sounding && double.IsFinite(_lastTickT)) _soundSec += Math.Clamp(tHop - _lastTickT, 0, 0.1);
        _lastTickT = tHop;
        if (_soundSec >= StatsEverySec)
        {
            _soundSec -= StatsEverySec;
            _logDue = true;
        }
        if (tHop - _lastJobT < Yamnet.HopSec) return;
        bool quiet = !sounding;
        // まだ窓 1 つ分たまっていない（取り直した直後は、場面の決め方を最初からにするだけの回を頼む）
        bool resetOnly = !quiet && _ringCount < Yamnet.WindowSamples;
        if (resetOnly && Volatile.Read(ref _trackerReset) == 0) return;
        _lastJobT = tHop;
        if (!Inline && Volatile.Read(ref _busy) != 0)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }
        int w = resetOnly ? -2 : -1;
        double endT = tHop;
        if (!quiet && !resetOnly)
        {
            // リングの新しいほうの窓 1 つ分を、古い順に写す
            w = _nextWin;
            _nextWin ^= 1;
            var dst = _win[w];
            int start = (_ringPos - Yamnet.WindowSamples + RingSamples) % RingSamples;
            int first = Math.Min(Yamnet.WindowSamples, RingSamples - start);
            Array.Copy(_ring, start, dst, 0, first);
            if (first < Yamnet.WindowSamples) Array.Copy(_ring, 0, dst, first, Yamnet.WindowSamples - first);
            endT = _ringEndT;
        }
        bool log = _logDue;
        _logDue = false;
        int gen = Volatile.Read(ref _gen);
        if (Inline)
        {
            RunJob(w, endT, gen, log);
            return;
        }
        _jobWin = w;
        _jobEndT = endT;
        _jobGen = gen;
        _jobLog = log;
        Volatile.Write(ref _busy, 1);
        _wake.Set();
    }

    /// <summary>
    /// 今の曲の頭からやり直す（出力先が変わった・取り込み直した）。次の AI の回で、いま見えている場面からゆっくり場面なしに移る
    /// （場面が移り変わっている途中なら、それが終わってから。途中で戻すと色が一度に飛ぶ）
    /// </summary>
    public void Reset()
    {
        Interlocked.Increment(ref _gen);
        _ringCount = 0;
        _ringEndT = double.NaN;
        _lastJobT = double.NegativeInfinity;
        _lastTickT = double.NaN;
        Volatile.Write(ref _trackerReset, 1);
    }

    /// <summary>
    /// 作り直す前の楽器の AI の場面（shown）から続ける（サンプリング周波数が変わった・付け直した）。作った直後、音を流す前に、
    /// Push と同じスレッドから呼ぶ。いきなり場面なしにせず、Reset と同じようにゆっくり戻す
    /// </summary>
    public void ContinueFrom(MusicScene shown)
    {
        if (ReferenceEquals(shown, MusicScene.None)) return;
        Volatile.Write(ref _scene, shown);
        Reset();
    }

    /// <summary>止まったあと（取り込みスレッドから）: 場面の移り変わりが終わっていれば、そこからゆっくり場面なしに戻す</summary>
    private void SettleToNone(double t)
    {
        var cur = Volatile.Read(ref _scene);
        if (cur.Group == SceneGroup.None || t < cur.SwitchT + MusicScene.FadeSec) return;
        Volatile.Write(ref _scene, new MusicScene(SceneGroup.None, cur.Group, t, 0));
        Interlocked.Increment(ref _switches);
    }

    /// <summary>いまの取り込みの回（テスト用）</summary>
    internal int Generation => Volatile.Read(ref _gen);
    /// <summary>AI のスレッドが計算中（テスト用）</summary>
    internal bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>結果を出す（AI のスレッド・テストから）。頼んだあとに取り込み直していたら（回が違えば）捨てる。変わったときだけ差し替える</summary>
    internal void Deliver(MusicScene s, int gen)
    {
        if (gen != Volatile.Read(ref _gen) || _failed) return;
        var cur = Volatile.Read(ref _scene);
        if (cur == s) return;
        if (cur.Group != s.Group) Interlocked.Increment(ref _switches);
        Volatile.Write(ref _scene, s);
    }

    private void WorkerLoop()
    {
        try
        {
            while (!_stop)
            {
                _wake.WaitOne();
                if (_stop) break;
                if (Volatile.Read(ref _busy) == 0) continue;
                try { RunJob(_jobWin, _jobEndT, _jobGen, _jobLog); }
                finally { Volatile.Write(ref _busy, 0); }
            }
        }
        finally
        {
            // 計算が終わってから、モデルを手放す（Dispose は待たないので、片付けはこのスレッドで）
            _wake.Dispose();
            _onExit?.Invoke();
        }
    }

    /// <summary>1 つの窓を調べる（w = 窓の番号。−1 なら鳴っていない窓、−2 なら場面の決め方を最初からにするだけ）</summary>
    private void RunJob(int w, double endT, int gen, bool log)
    {
        if (gen != Volatile.Read(ref _gen)) return;   // 頼んだあとに取り直した: 前の回の音は調べない
        // 取り直した: いま見えている場面から続ける（前の回の計算で決め方の場面が進んでいても、見えていないので使わない）
        if (Interlocked.Exchange(ref _trackerReset, 0) == 1) _tracker.Reset(endT, Current);
        MusicScene res;
        if (w == -2) res = _tracker.Hold(endT);
        else if (w < 0)
        {
            Interlocked.Increment(ref _quiet);
            res = _tracker.Quiet(endT);
        }
        else
        {
            var win = _win[w];
            double sum = 0;
            for (int i = 0; i < win.Length; i++) sum += win[i] * (double)win[i];
            double db = 10 * Math.Log10(sum / win.Length + 1e-20);
            if (db < QuietDb)
            {
                Interlocked.Increment(ref _quiet);
                res = _tracker.Quiet(endT);
            }
            else
            {
                // 音量をそろえる（YAMNet は音の大きさで答えが大きく変わる。−20 dB 小さいだけでドラムのらしさが半分以下になる窓があった）
                float gain = (float)Math.Clamp(Math.Pow(10, (TargetDb - db) / 20), MinGain, MaxGain);
                for (int i = 0; i < win.Length; i++) _scaled[i] = Math.Clamp(win[i] * gain, -1f, 1f);
                long t0 = Stopwatch.GetTimestamp();
                bool ok;
                try { ok = _classify(_scaled, _scores); }
                catch (Exception ex)
                {
                    Fail(ex);
                    return;
                }
                if (!ok) return;
                Volatile.Write(ref _msTotal, _msTotal + (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                Interlocked.Increment(ref _windows);
                SceneClasses.Groups(_scores, _groups);
                res = _tracker.Update(endT, _groups);
                Diag?.Invoke(endT, _groups, _tracker);
            }
        }
        Deliver(res, gen);
        if (log) LogStats();
    }

    private void Fail(Exception ex)
    {
        _failed = true;
        Log?.Invoke("楽器の AI に失敗: " + ex.Message);   // no-loc（ログだけ）
        // 色・光り方は、取り込みスレッドが次の Push から、いまの場面からゆっくり場面なしに戻す（移り変わりの途中なら終わってから）
    }

    /// <summary>直近 StatsEverySec 秒の数字だけの統計（曲名などは出さない）</summary>
    private void LogStats()
    {
        if (Log is null) return;
        int wn = Windows, q = Quiet, sk = Skipped, sw = Switches;
        double ms = Volatile.Read(ref _msTotal);
        int dw = wn - _logWindows;
        double avg = dw > 0 ? (ms - _logMs) / dw : 0;
        Log($"楽器の AI の統計（{StatsEverySec:F0} 秒）: 調べた {dw} 回・静か {q - _logQuiet} 回・飛ばした {sk - _logSkipped} 回・場面の切り替え {sw - _logSwitches} 回・平均 {avg:F1} ms");   // no-loc（ログだけ）
        _logWindows = wn;
        _logQuiet = q;
        _logSkipped = sk;
        _logSwitches = sw;
        _logMs = ms;
    }

    /// <summary>
    /// 止める（待たない。音の取り込み・画面のスレッドを止めないため）。計算中なら、AI のスレッドがその計算を終えてから終わり、
    /// そこでモデルを手放す（モデルの持ち主が先に Dispose しても、計算中に片付かない）
    /// </summary>
    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }   // AI のスレッドがもう終わっていた
    }

    /// <summary>AI のスレッドが終わるまで待つ（テスト用）</summary>
    internal bool WaitExited(int ms) => _worker.Join(ms);
}

/// <summary>鳴っている音（任意のサンプリング周波数・モノラル）を、楽器の AI 用の 16,000 Hz にする（少しずつ流し込める。Resampler22k と同じ作り）</summary>
internal sealed class Resampler16k
{
    private readonly WdlResampler _r = new();
    /// <summary>出てくる音の入れ物（一度に渡す最大 4,096 個の入力から出る分。最初に用意して、あとで大きくしない）</summary>
    private float[] _out = new float[4096 * Yamnet.SampleRate / 8000 + 64];

    public Resampler16k(int inputRate)
    {
        _r.SetMode(true, 2, false);
        _r.SetFilterParms();
        _r.SetFeedMode(true);
        _r.SetRates(inputRate, Yamnet.SampleRate);
    }

    /// <summary>入力を流し込み、出てきた 16,000 Hz の音を sink に渡す</summary>
    public void Push(ReadOnlySpan<float> input, SamplesSink sink)
    {
        while (input.Length > 0)
        {
            int n = Math.Min(input.Length, 4096);
            int need = _r.ResamplePrepare(n, 1, out float[] inBuf, out int inOff);
            n = Math.Min(n, need);
            if (n <= 0) break;
            input[..n].CopyTo(inBuf.AsSpan(inOff));
            int maxOut = n * Yamnet.SampleRate / 8000 + 64;
            if (_out.Length < maxOut) _out = new float[maxOut];
            int got = _r.ResampleOut(_out, 0, n, _out.Length, 1);
            if (got > 0) sink(_out.AsSpan(0, got));
            input = input[n..];
        }
    }
}
