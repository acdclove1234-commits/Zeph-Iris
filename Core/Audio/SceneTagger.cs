// ==================================================
// SceneTagger.cs - 楽器の AI（CED-Tiny）で、曲の部分ごとに目立つ楽器（場面）を調べる
//   鳴っている音（ミッドとサイド）を 16,000 Hz にして貯め、1 秒ごとに直近 2 秒を優先度の低い専用のスレッドで調べる
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

/// <summary>
/// 楽器の AI で、いまの場面（目立つ楽器。<see cref="MusicScene"/>）を決める。一瞬の音ではなく曲の部分を見るので、
/// 結果は 2〜5 秒ほど遅れて出る（2 秒の窓を 1 秒ごとに調べ、窓 2 つ続いたら切り替える。光り方はそこから MusicScene.FadeSec 秒かけて移る。ビートの光には使わない）。
/// 取り込みスレッドから <see cref="Push"/>、分析のコマごとに <see cref="Tick"/> を呼ぶ（どちらも同じスレッド。待たない・メモリを確保しない）。
/// AI は優先度のいちばん低い専用のスレッドで動かす（光の描画・音の取り込みを邪魔しない）。前の計算が終わっていなければ、その回は飛ばす
/// </summary>
public sealed class SceneTagger : IDisposable
{
    /// <summary>この音量（dBFS。窓全体の 2 乗平均）より小さい窓は、AI を使わず静かとみなす</summary>
    public const double QuietDb = -60;
    /// <summary>AI に渡す前に、窓の音量をこの大きさ（dBFS）にそろえる（小さい音・大きい音で楽器のらしさが変わらないように）。倍率の範囲</summary>
    public const double TargetDb = -18;
    public const float MinGain = 0.25f, MaxGain = 32f;
    /// <summary>この秒数だけ音が鳴るたびに、数字だけの統計を 1 行ログに出す</summary>
    public const double StatsEverySec = 60;
    /// <summary>リングの長さ（16,000 Hz の個数。窓より少し長い）</summary>
    private const int RingSamples = CedTiny.WindowSamples + 4000;

    /// <summary>
    /// サイド（左右の差）の窓も AI で調べる（鍵盤・シンセ・弦だけ、ミッドとサイドの大きいほう。ギター・歌・話し声・金管・ドラムはミッドだけ）。
    /// 鍵盤は左右に広げてあることが多く、ミッドでは歌・ドラムに隠れる（録音の鍵盤の曲で、ミッドだけでは鍵盤の場面がほとんど出なかった）。
    /// ギターはミッドだけ: サイドも使うと、ロックの曲のほとんど（88〜99%）がギターの場面になった（ミッドだけで 19〜40%）。
    /// サイドがミッドより SideRangeDb 以上小さい窓（ほぼモノラル）・静かな窓は、サイドを調べない（雑音を大きくして調べないように）
    /// </summary>
    public const double SideRangeDb = 24;
    /// <summary>サイドのらしさをミッドと比べて大きいほうにするまとまり</summary>
    private static readonly int[] SideGroups = { SceneClasses.Keys, SceneClasses.Synth, SceneClasses.Strings };

    private readonly Func<float[], float[], bool> _classify;
    private readonly AiResampler _rs, _rsSide;
    private readonly SamplesSink _sink, _sinkSide;
    private readonly float[] _ring = new float[RingSamples], _ringSide = new float[RingSamples];
    private int _ringPos, _ringCount, _ringPosSide;
    /// <summary>
    /// リングのいちばん新しい音の、次の音の時刻（秒）。入力の音の数と取り込みの時刻から求める（AiResampler.EndT。出てきた数を足していかない）
    /// </summary>
    private double _ringEndT = double.NaN;

    // AI に渡す窓（交互に使う）・音量をそろえた窓・結果
    private readonly float[][] _win = { new float[CedTiny.WindowSamples], new float[CedTiny.WindowSamples] };
    private readonly float[][] _winSide = { new float[CedTiny.WindowSamples], new float[CedTiny.WindowSamples] };
    private int _nextWin;
    private readonly float[] _scaled = new float[CedTiny.WindowSamples];
    private readonly float[] _scores = new float[CedTiny.Classes], _scoresSide = new float[CedTiny.Classes];
    private readonly float[] _groups = new float[SceneClasses.Count], _groupsSide = new float[SceneClasses.Count];
    private int _sideWindows;
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
    private int _logWindows, _logQuiet, _logSkipped, _logSwitches, _logSide;
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
    /// <summary>サイドも AI で調べた回数（ほぼモノラルの窓は調べない）</summary>
    public int SideWindows => Volatile.Read(ref _sideWindows);
    /// <summary>窓 1 つ分の AI の計算の平均（ミリ秒。サイドも調べた窓は、ミッドとサイドの 2 回分）</summary>
    public double AvgMs { get { int n = Windows; return n > 0 ? Volatile.Read(ref _msTotal) / n : 0; } }
    /// <summary>調査用の記録（ログ）</summary>
    public Action<string>? Log { get; init; }
    /// <summary>テスト・調査用: AI をその場で動かし、結果をすぐ使う（実時間より速く音を流すとき）</summary>
    public bool Inline { get; init; }
    /// <summary>テスト・調査用: AI で調べるたびに、窓の終わりの時刻・まとまりの値（そのままの値）・場面の決め方（この窓を入れたあと）を渡す（AI のスレッドから）</summary>
    internal Action<double, float[], SceneTracker>? Diag { get; init; }
    /// <summary>調査用: AI で調べるたびに、窓の終わりの時刻・ミッドの 527 個のらしさ・サイドの 527 個のらしさ（調べなかったら null）を渡す</summary>
    internal Action<double, float[], float[]?>? DiagScores { get; init; }

    /// <summary>モデルを使う（AI のスレッドが終わるまでモデルを手放さない。持ち主が先に Dispose しても、計算中に片付かない）</summary>
    public SceneTagger(CedTiny model, int inputRate) : this(Acquire(model), inputRate, model.Release) { }

    private static Func<float[], float[], bool> Acquire(CedTiny model)
    {
        if (!model.TryAcquire()) throw new ObjectDisposedException(nameof(CedTiny));
        return (w, s) => { model.Classify(w, s); return true; };
    }

    /// <summary>
    /// テスト用: AI の代わりに classifier（窓 → 527 個のらしさ。false なら結果なし）を使う。onExit = AI のスレッドが終わるとき
    /// </summary>
    internal SceneTagger(Func<float[], float[], bool> classifier, int inputRate, Action? onExit = null)
    {
        _classify = classifier;
        _onExit = onExit;
        try
        {
            _rs = new AiResampler(inputRate, CedTiny.SampleRate);
            _rsSide = new AiResampler(inputRate, CedTiny.SampleRate);
            _sink = OnResampled;   // 毎回ラムダを作らないよう、1 回だけ作っておく
            _sinkSide = OnResampledSide;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "楽器の AI", Priority = ThreadPriority.Lowest };   // no-loc（スレッドの名前）
            _worker.Start();
        }
        catch
        {
            onExit?.Invoke();
            throw;
        }
    }

    /// <summary>Push に渡す音のサンプリング周波数（AudioEngine は、これが分析の周波数なら落とした音を、取り込みの周波数なら届いたままの音を渡す）</summary>
    public int InputRate => _rs.InputRate;

    /// <summary>テスト用: ミッドを 16,000 Hz にする変換器（入れた数・出した数）</summary>
    internal AiResampler Resampler => _rs;

    /// <summary>テスト用: ミッドとサイドのリングの、新しいほうの n 個（古い順。窓と同じ切り出し方）</summary>
    internal (float[] Mid, float[] Side) RingTail(int n)
    {
        n = Math.Min(n, RingSamples);
        var mid = new float[n];
        var side = new float[n];
        for (int i = 0; i < n; i++)
        {
            mid[i] = _ring[(_ringPos - n + i + 2 * RingSamples) % RingSamples];
            side[i] = _ringSide[(_ringPosSide - n + i + 2 * RingSamples) % RingSamples];
        }
        return (mid, side);
    }

    /// <summary>鳴っている音を足す（モノラル。サイドは 0 = 調べない）</summary>
    public void Push(ReadOnlySpan<float> mono, double tFirst) => Push(mono, ReadOnlySpan<float>.Empty, tFirst);

    /// <summary>
    /// 鳴っている音を足す（AudioEngine.Push と同じ音・同じ時刻。mono = ミッド、side = サイド。side が mono と同じ長さでなければ、
    /// この分のサイドは 0 にする）
    /// </summary>
    public void Push(ReadOnlySpan<float> mono, ReadOnlySpan<float> side, double tFirst)
    {
        if (_failed)
        {
            SettleToNone(tFirst);
            return;
        }
        // 時刻が飛んだ・戻った（取り込みが途切れた）ら、たまった音を捨てる。少しだけ先に飛んだ（音が届かなかった）ら、その間を無音で埋める。
        // サイドの変換器には、ミッドとまったく同じ数の音を同じ順に入れる（捨てるのも 2 つ一緒）: 2 つは同じ設定なので、出てくる数も位置もいつも同じ
        // （前はサイドのない回のあとにサイドだけ最初からにしていて、1 回に出てくる数がちょうどでない大きさ（44.1 kHz で 448 個など）で数がずれ、
        // そろえるためにサイドに 0 を挟んだり本物の音を上書きしたりしていた）
        long fill = _rs.Sync(tFirst);
        if (fill < 0)
        {
            _ringCount = 0;
            _rsSide.Reset();
            fill = 0;
        }
        if (fill > 0)
        {
            _rs.PushSilence(fill, _sink);
            _rsSide.PushSilence(fill, _sinkSide);
        }
        _rs.Push(mono, _sink);
        _ringEndT = _rs.EndT;
        // サイドがない回（モノラル・無音で埋めた回）・長さが合わない回は、サイドに同じ数の無音を入れる（左右の差がない = サイドは無音）
        if (side.Length == mono.Length && !side.IsEmpty) _rsSide.Push(side, _sinkSide);
        else _rsSide.PushSilence(mono.Length, _sinkSide);
        Debug.Assert(_ringPosSide == _ringPos && _rsSide.OutputCount == _rs.OutputCount, "サイドのリングがミッドとずれた");   // no-loc（調べる用）
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
    }

    /// <summary>サイドの 16,000 Hz の音（ミッドと同じ設定の変換器に同じ数の音を入れているので、いつもミッドと同じ数・同じ位置に出てくる）</summary>
    private void OnResampledSide(ReadOnlySpan<float> s)
    {
        var ring = _ringSide;
        int pos = _ringPosSide;
        foreach (float x in s)
        {
            ring[pos] = float.IsFinite(x) ? x : 0;
            if (++pos == RingSamples) pos = 0;
        }
        _ringPosSide = pos;
    }

    /// <summary>
    /// 分析のコマごとに呼ぶ（テンポの追跡を使わないときも）。sounding = 音が鳴っている（門が開いていて反応の強さが十分）。
    /// 1 秒ごとに、直近の窓（2 秒）を AI のスレッドに渡す（計算中なら飛ばす）。鳴っていなければ AI を使わず「静か」と数える
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
        if (tHop - _lastJobT < CedTiny.HopSec) return;
        bool quiet = !sounding;
        // まだ窓 1 つ分たまっていない（取り直した直後は、場面の決め方を最初からにするだけの回を頼む）
        bool resetOnly = !quiet && _ringCount < CedTiny.WindowSamples;
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
            int start = (_ringPos - CedTiny.WindowSamples + RingSamples) % RingSamples;
            int first = Math.Min(CedTiny.WindowSamples, RingSamples - start);
            Array.Copy(_ring, start, dst, 0, first);
            if (first < CedTiny.WindowSamples) Array.Copy(_ring, 0, dst, first, CedTiny.WindowSamples - first);
            // サイドも同じ長さだけ（サイドのリングの新しいほうから）
            var dstSide = _winSide[w];
            int startSide = (_ringPosSide - CedTiny.WindowSamples + RingSamples) % RingSamples;
            int firstSide = Math.Min(CedTiny.WindowSamples, RingSamples - startSide);
            Array.Copy(_ringSide, startSide, dstSide, 0, firstSide);
            if (firstSide < CedTiny.WindowSamples) Array.Copy(_ringSide, 0, dstSide, firstSide, CedTiny.WindowSamples - firstSide);
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
        // 変換器の中に残っている前の音も捨てる（ミッドとサイドを一緒に。数・位置がずれないように）
        _rs.Reset();
        _rsSide.Reset();
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
            double db = LevelDb(win);
            if (db < QuietDb)
            {
                Interlocked.Increment(ref _quiet);
                res = _tracker.Quiet(endT);
            }
            else
            {
                long t0 = Stopwatch.GetTimestamp();
                bool ok;
                // サイドは、ほぼモノラル（ミッドより SideRangeDb 以上小さい）・静かな窓では調べない
                var winSide = _winSide[w];
                double dbSide = LevelDb(winSide);
                bool side = dbSide >= QuietDb && dbSide >= db - SideRangeDb;
                try
                {
                    ok = Classify(win, db, _scores);
                    // サイドで答えが出なかったら、ミッドだけで決める（ミッドの結果は捨てない）
                    if (ok && side) side = Classify(winSide, dbSide, _scoresSide);
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    return;
                }
                if (!ok) return;
                Volatile.Write(ref _msTotal, _msTotal + (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                Interlocked.Increment(ref _windows);
                SceneClasses.Groups(_scores, _groups);
                if (side)
                {
                    // 鍵盤・シンセ・弦は、ミッドとサイドの大きいほう（ギター・歌・話し声・金管・ドラムはミッドだけ: SideGroups）
                    Interlocked.Increment(ref _sideWindows);
                    SceneClasses.Groups(_scoresSide, _groupsSide);
                    foreach (int k in SideGroups) _groups[k] = Math.Max(_groups[k], _groupsSide[k]);
                }
                DiagScores?.Invoke(endT, _scores, side ? _scoresSide : null);
                res = _tracker.Update(endT, _groups);
                Diag?.Invoke(endT, _groups, _tracker);
            }
        }
        Deliver(res, gen);
        if (log) LogStats();
    }

    /// <summary>窓全体の 2 乗平均の音量（dBFS）</summary>
    private static double LevelDb(float[] win)
    {
        double sum = 0;
        for (int i = 0; i < win.Length; i++) sum += win[i] * (double)win[i];
        return 10 * Math.Log10(sum / win.Length + 1e-20);
    }

    /// <summary>窓 win（音量 db）を、音量をそろえてから AI で調べる（AI のスレッドだけ。_scaled を使い回す）</summary>
    private bool Classify(float[] win, double db, float[] scores)
    {
        // 音量をそろえる（AI は音の大きさで答えが変わる。前の AI では −20 dB 小さいだけでドラムのらしさが半分以下になる窓があった）
        float gain = (float)Math.Clamp(Math.Pow(10, (TargetDb - db) / 20), MinGain, MaxGain);
        for (int i = 0; i < win.Length; i++) _scaled[i] = Math.Clamp(win[i] * gain, -1f, 1f);
        return _classify(_scaled, scores);
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
        int wn = Windows, q = Quiet, sk = Skipped, sw = Switches, sd = SideWindows;
        double ms = Volatile.Read(ref _msTotal);
        int dw = wn - _logWindows;
        double avg = dw > 0 ? (ms - _logMs) / dw : 0;
        Log($"楽器の AI の統計（{StatsEverySec:F0} 秒）: 調べた {dw} 回（サイドも {sd - _logSide} 回）・静か {q - _logQuiet} 回・飛ばした {sk - _logSkipped} 回・場面の切り替え {sw - _logSwitches} 回・窓 1 つの平均 {avg:F1} ms");   // no-loc（ログだけ）
        _logWindows = wn;
        _logSide = sd;
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
