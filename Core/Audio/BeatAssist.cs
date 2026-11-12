// ==================================================
// BeatAssist.cs - 拍の AI（BeatThis）で、リアルタイムの拍の追跡（BeatTracker）を確かめて直す
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 鳴っている音の直近 WindowSec 秒を、数秒おきに拍の AI で調べ、リアルタイムの追跡が「違うテンポ」「裏拍」で
/// ロックしているとき（2 回続けて食い違ったとき）や、ロックできていないときに、AI の拍の並びに合わせる。
/// AI は少し後から音を見るので正確だが、いちばん新しい EdgeSec 秒は先が見えないので使わない。
/// AI は優先度の低い専用のスレッドで動かす（光の描画・音の取り込みを邪魔しない）。
/// 取り込みスレッドから <see cref="Push"/>、分析のコマごとに <see cref="Tick"/> を呼ぶ（どちらも同じスレッド）
/// </summary>
public sealed class BeatAssist : IDisposable
{
    /// <summary>AI に見せる長さ（秒）・最低これだけたまってから調べる（秒）</summary>
    public const double WindowSec = 20, MinSec = 10;
    /// <summary>いちばん新しいこの秒数の拍は使わない（AI は先が見えないので、端の拍はずれやすい）</summary>
    public const double EdgeSec = 1.5;
    /// <summary>調べる間隔（秒）: 追跡と合っている間はゆっくり、合っていない・ロックしていないときは早く</summary>
    public const double SlowEverySec = 15, FastEverySec = 6;
    /// <summary>周期の食い違い（割合）と、拍の位置の食い違い（周期の割合）がこれ以内なら「合っている」</summary>
    public const double PeriodTol = 0.03, PhaseTol = 0.15;
    /// <summary>このテンポより速い曲は、半分のテンポ（1 拍おき）で光らせる（BeatTracker の LockedHalveBpm と同じ考え）</summary>
    public static double HalveAboveBpm => BeatTracker.Tune.LockedHalveBpm;

    private readonly BeatThis _model;
    private readonly Resampler22k _rs;
    private readonly float[] _ring;
    private int _ringPos, _ringCount;
    /// <summary>リングのいちばん新しい音の、次の音の時刻（秒）</summary>
    private double _ringEndT = double.NaN;

    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private float[]? _job;
    private double _jobStartT;
    private volatile Result? _result;
    private bool _busy;
    private double _lastStartT = double.NegativeInfinity;
    private int _disagree;
    private bool _lastAgreed;

    /// <summary>AI の結果（時刻は音の時刻）</summary>
    public sealed record Result(List<double> Beats, List<double> Downbeats, double WindowEndT, double ComputeSec);

    /// <summary>調べた回数・合っていた回数・直した回数（調査・ログ用）</summary>
    public int Runs { get; private set; }
    public int Agreed { get; private set; }
    public int Steered { get; private set; }
    /// <summary>AI の結果が出るたびに呼ぶ（覚えた曲の記録などに使う。分析のスレッドから）</summary>
    public Action<Result>? Analyzed;
    /// <summary>調査用の記録</summary>
    public Action<string>? Log;
    /// <summary>テスト・調査用: AI をその場で動かし、結果をすぐ使う（実時間より速く音を流すとき、結果の届く時刻がばらつかないように）</summary>
    public bool Inline { get; init; }

    public BeatAssist(BeatThis model, int inputRate)
    {
        _model = model;
        _rs = new Resampler22k(inputRate);
        _ring = new float[(int)(WindowSec * BeatThis.SampleRate)];
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "拍の AI", Priority = ThreadPriority.BelowNormal };   // no-loc（スレッドの名前）
        _worker.Start();
    }

    /// <summary>鳴っている音を足す（AudioEngine.Push と同じ音・同じ時刻）</summary>
    public void Push(ReadOnlySpan<float> mono, double tFirst)
    {
        // 時刻が飛んだ（取り込みが途切れた）ら、たまった音を捨てる
        if (double.IsFinite(_ringEndT) && Math.Abs(tFirst - _ringEndT) > 0.25) _ringCount = 0;
        if (_ringCount == 0) _ringEndT = tFirst;
        _rs.Push(mono, s =>
        {
            foreach (float x in s)
            {
                _ring[_ringPos] = x;
                _ringPos = (_ringPos + 1) % _ring.Length;
                if (_ringCount < _ring.Length) _ringCount++;
            }
            _ringEndT += s.Length / (double)BeatThis.SampleRate;
        });
    }

    /// <summary>曲の拍の記憶（null なら覚えない）</summary>
    public SongMemory? Memory { get; init; }
    /// <summary>覚えた曲に合わせられている間、AI で確かめる間隔（秒。覚えた拍の抜けを埋めるため、ときどきは調べる）</summary>
    public const double RecalledEverySec = 30;
    /// <summary>覚えた拍と追跡を比べる間隔（秒）</summary>
    public const double MemoryCheckSec = 3;
    private double _lastMemoryCheck = double.NegativeInfinity;
    private int _memDisagree;
    /// <summary>覚えた曲の、今の部分に使える拍の並びがある（なければ AI で確かめる）</summary>
    private bool _memoryGrid;

    /// <summary>直近の低音の強さ（分析のコマごと。約 16 秒ぶん）。拍の位置が表か裏かを、キックで確かめるのに使う</summary>
    private readonly double[] _lowT = new double[1600], _lowV = new double[1600];
    private int _lowPos;
    /// <summary>半拍ずらした位置の低音が、この倍以上強ければ、そちらを拍とする</summary>
    public const double KickSideRatio = 1.15;
    /// <summary>キックで確かめる長さ（秒）・前後に見る幅（秒）</summary>
    public const double KickCheckSec = 12, KickWindowSec = 0.04;

    /// <summary>今の曲の頭からやり直す（出力先が変わった・取り込み直した）</summary>
    public void Reset()
    {
        _ringCount = 0;
        _ringEndT = double.NaN;
        _disagree = 0;
        _lastAgreed = false;
        _lastStartT = double.NegativeInfinity;
    }

    /// <summary>分析のコマごとに呼ぶ: 時間が来たら AI に頼み、結果が出ていれば追跡と比べて直す。覚えた曲なら、覚えた拍で直す</summary>
    public void Tick(double tNow, BeatTracker tracker, bool gateOpen, double odf = 0, double low = 0)
    {
        _lowT[_lowPos] = tNow;
        _lowV[_lowPos] = double.IsFinite(low) ? low : 0;
        _lowPos = (_lowPos + 1) % _lowT.Length;
        Memory?.Tick(tNow, odf, gateOpen);
        if (_result is { } r)
        {
            _result = null;
            _busy = false;
            Learn(r);
            if (!_memoryGrid) Decide(r, tracker);
        }
        // 覚えた曲: 覚えた拍の並びと追跡を比べ、ロックしていなければすぐ、ロック中なら 2 回続けて食い違ったら合わせる
        // （1 回で合わせると、覚えた拍が少しずれている部分で、正しく追えている追跡を動かしてしまう曲があった）。
        // 覚えた拍がそろっていない部分（_memoryGrid = false）では、ふだんどおり AI で確かめる
        if (Memory?.Aligned != true) _memoryGrid = false;
        else if (tNow - _lastMemoryCheck >= MemoryCheckSec)
        {
            _lastMemoryCheck = tNow;
            var mgOpt = Memory.GridAt(tNow);
            _memoryGrid = mgOpt is not null;
            if (mgOpt is { } mg)
            {
                var g = PhaseByKicks(HalveFast(mg.Period, mg.BeatT, Array.Empty<double>()), tNow);
                if (Agrees(tracker, g) || KeepTrackerPhase(tracker, g, tNow)) _memDisagree = 0;
                else if (tracker.Lock == BeatLock.Unlocked || ++_memDisagree >= 2)
                {
                    _memDisagree = 0;
                    Steered++;
                    Log?.Invoke($"覚えた曲の拍に合わせる: {60 / g.Period:F1} BPM");   // no-loc（ログだけ）
                    tracker.Steer(g.Period, g.BeatT);
                }
            }
        }
        if (_busy || !gateOpen || _ringCount < MinSec * BeatThis.SampleRate) return;
        double every = _memoryGrid ? RecalledEverySec
            : tracker.Lock == BeatLock.Unlocked || !_lastAgreed ? FastEverySec : SlowEverySec;
        if (tNow - _lastStartT < every) return;
        _lastStartT = tNow;
        // リングの中身を古い順に写して、AI のスレッドに渡す
        var job = new float[_ringCount];
        int start = (_ringPos - _ringCount + _ring.Length) % _ring.Length;
        int first = Math.Min(_ringCount, _ring.Length - start);
        Array.Copy(_ring, start, job, 0, first);
        if (first < _ringCount) Array.Copy(_ring, 0, job, first, _ringCount - first);
        _jobStartT = _ringEndT - _ringCount / (double)BeatThis.SampleRate;
        if (Inline)
        {
            var (bi, di) = _model.Analyze(job);
            double s0 = _jobStartT;
            var res = new Result(bi.Select(b => s0 + b).ToList(), di.Select(b => s0 + b).ToList(), s0 + job.Length / (double)BeatThis.SampleRate, 0);
            Analyzed?.Invoke(res);
            Learn(res);
            if (Memory?.Aligned != true) Decide(res, tracker);
            return;
        }
        Volatile.Write(ref _job, job);
        _busy = true;
        _wake.Set();
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            var job = Interlocked.Exchange(ref _job, null);
            if (job is null) continue;
            double t0 = _jobStartT;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (beats, downs) = _model.Analyze(job);
                var res = new Result(beats.Select(b => t0 + b).ToList(), downs.Select(b => t0 + b).ToList(),
                    t0 + job.Length / (double)BeatThis.SampleRate, sw.Elapsed.TotalSeconds);
                Analyzed?.Invoke(res);
                _result = res;
            }
            catch (Exception ex)
            {
                Log?.Invoke("拍の AI に失敗: " + ex.Message);   // no-loc（ログだけ）
                _result = new Result(new(), new(), t0, 0);
            }
        }
    }

    /// <summary>
    /// AI の拍から、いま使う拍の並び（周期・拍の時刻）を決める。端の EdgeSec 秒を除いた直近 16 秒の拍が 8 個以上あり、
    /// 間隔がほぼそろっている（拍の番号に対する時刻の直線から 30 ミリ秒以内）ときだけ。
    /// 速い曲（HalveAboveBpm より速い）は周期を 2 倍にし、小節の頭（AI の downbeat）がそろうほうの 1 拍おきを使う
    /// </summary>
    public static (double Period, double BeatT)? EstimateGrid(IReadOnlyList<double> beats, IReadOnlyList<double> downbeats, double windowEnd)
    {
        var b = beats.Where(t => t <= windowEnd - EdgeSec && t >= windowEnd - EdgeSec - 16).ToList();
        if (b.Count < 8) return null;
        var ibi = b.Zip(b.Skip(1), (x, y) => y - x).OrderBy(x => x).ToList();
        double med = ibi[ibi.Count / 2];
        if (!(med > 0)) return null;
        if (ibi.Count(d => Math.Abs(d / med - 1) < 0.1) < 0.75 * ibi.Count) return null;   // テンポが揺れている・拍が抜けている
        // 拍の番号を振って、時刻 = a + 番号 × 周期 の直線を当てはめる
        var k = b.Select(t => Math.Round((t - b[0]) / med)).ToList();
        double mk = k.Average(), mt = b.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < b.Count; i++) { sxy += (k[i] - mk) * (b[i] - mt); sxx += (k[i] - mk) * (k[i] - mk); }
        if (sxx <= 0) return null;
        double p = sxy / sxx, a = mt - p * mk;
        double rms = Math.Sqrt(b.Select((t, i) => Math.Pow(t - (a + p * k[i]), 2)).Average());
        if (rms > 0.030) return null;
        return HalveFast(p, a + p * k[^1], downbeats);
    }

    /// <summary>
    /// 速い曲（HalveAboveBpm より速い）は、半分のテンポ（1 拍おき）にする。残すのは、小節の頭（downbeat）に近い拍が多いほう。
    /// 小節の頭が分からなければ、今の拍のまま
    /// </summary>
    public static (double Period, double BeatT) HalveFast(double p, double last, IReadOnlyList<double> downbeats)
    {
        if (60 / p <= HalveAboveBpm) return (p, last);
        int Near(double anchor) => downbeats.Count(d => Math.Abs(Wrap((d - anchor) / (2 * p))) < 0.1);
        if (Near(last - p) > Near(last)) last -= p;
        return (2 * p, last);
    }

    /// <summary>位相の差を -0.5〜0.5 に</summary>
    private static double Wrap(double x) => x - Math.Round(x);

    /// <summary>
    /// テンポは AI のまま、拍が表か裏かをキックで決める。小さい AI は、曲によって拍を半拍ずれた位置（裏拍）に置くことがある
    /// （実際の 20 曲のうち、テンポは合っているのに曲の間ずっと半拍ずれた曲があった。キックは物差しの拍の上にあった）。
    /// 直近 KickCheckSec 秒の拍の位置と、半拍ずらした位置の低音の強さを比べ、ずらしたほうが KickSideRatio 倍以上強ければずらす
    /// </summary>
    private (double Period, double BeatT) PhaseByKicks((double Period, double BeatT) g, double tNow)
    {
        var (on, off) = KickBalance(g, tNow);
        if (off > on * KickSideRatio && off > 0)
        {
            Log?.Invoke($"拍の AI の位置を半拍ずらす（キックは半拍後ろ: {off / Math.Max(on, 1e-9):F2} 倍）");   // no-loc（ログだけ）
            return (g.Period, g.BeatT + g.Period / 2);
        }
        return g;
    }

    /// <summary>直近 KickCheckSec 秒の、拍の位置の低音の合計と、半拍ずらした位置の低音の合計</summary>
    private (double On, double Off) KickBalance((double Period, double BeatT) g, double tNow)
    {
        double p = g.Period, on = 0, off = 0;
        if (!(p > 0)) return (0, 0);
        for (double t = g.BeatT; t > tNow - KickCheckSec; t -= p) { if (t <= tNow - KickWindowSec) { on += LowAround(t); off += LowAround(t + p / 2); } }
        for (double t = g.BeatT + p; t <= tNow - KickWindowSec; t += p) { on += LowAround(t); off += LowAround(t + p / 2); }
        return (on, off);
    }

    /// <summary>
    /// 追跡が同じテンポでロックしていて、AI と半拍だけずれているとき: キックが AI の位置をはっきり支えていなければ、追跡の位置のままにする。
    /// 表か裏かは、AI もキックも決めきれない曲がある（実際の曲で、AI に合わせると拍の上の光が 92% → 64% に減った曲があった）
    /// </summary>
    private bool KeepTrackerPhase(BeatTracker tracker, (double Period, double BeatT) g, double tNow)
    {
        double pt = tracker.PeriodSec, lt = tracker.GridLastBeatT;
        if (tracker.Lock == BeatLock.Unlocked || !(pt > 0) || !double.IsFinite(lt)) return false;
        if (Math.Abs(pt / g.Period - 1) >= PeriodTol || Math.Abs(Wrap((lt - g.BeatT) / g.Period)) < 0.35) return false;
        var (on, off) = KickBalance(g, tNow);
        return !(on > off * KickSideRatio);
    }

    /// <summary>時刻 t の前後 KickWindowSec 秒の、いちばん強い低音</summary>
    private double LowAround(double t)
    {
        double m = 0;
        for (int i = 0; i < _lowT.Length; i++)
            if (Math.Abs(_lowT[i] - t) <= KickWindowSec && _lowV[i] > m) m = _lowV[i];
        return m;
    }

    /// <summary>追跡が、この拍の並びとテンポも位置も合っているか（ロックしていなければ合っていない）</summary>
    private static bool Agrees(BeatTracker tracker, (double Period, double BeatT) g)
    {
        double pt = tracker.PeriodSec, lt = tracker.GridLastBeatT;
        bool locked = tracker.Lock != BeatLock.Unlocked && pt > 0 && double.IsFinite(lt);
        return locked && Math.Abs(pt / g.Period - 1) < PeriodTol && Math.Abs(Wrap((lt - g.BeatT) / g.Period)) < PhaseTol;
    }

    /// <summary>
    /// AI の結果のうち、窓の両端（前は 2 秒・後ろは EdgeSec 秒）を除いた確かな拍を、曲の記憶に貯める。
    /// 間隔がそろっている窓だけを使い（拍の間の音が混じった窓は覚えない）、キックが半拍後ろにあれば半拍ずらして覚える
    /// </summary>
    private void Learn(Result r)
    {
        if (Memory is null || r.Beats.Count == 0) return;
        double from = r.WindowEndT - WindowSec + 2, to = r.WindowEndT - EdgeSec;
        var beats = r.Beats.Where(t => t >= from && t <= to).ToList();
        if (SongMemory.FitGrid(beats) is not { } fit) return;
        double last = fit.A + Math.Round((beats[^1] - fit.A) / fit.Period) * fit.Period;
        double shift = PhaseByKicks((fit.Period, last), to).BeatT - last;
        Memory.AddBeats(beats.Select(t => t + shift), r.Downbeats.Where(t => t >= from && t <= to).Select(t => t + shift));
    }

    private void Decide(Result r, BeatTracker tracker)
    {
        Runs++;
        if (EstimateGrid(r.Beats, r.Downbeats, r.WindowEndT) is not { } g0) { _lastAgreed = true; return; }
        var g = PhaseByKicks(g0, _lowT[(_lowPos - 1 + _lowT.Length) % _lowT.Length]);
        double pt = tracker.PeriodSec;
        bool locked = tracker.Lock != BeatLock.Unlocked && pt > 0 && double.IsFinite(tracker.GridLastBeatT);
        double tLast = _lowT[(_lowPos - 1 + _lowT.Length) % _lowT.Length];
        bool agree = Agrees(tracker, g) || KeepTrackerPhase(tracker, g, tLast);
        if (agree)
        {
            Agreed++;
            _disagree = 0;
            _lastAgreed = true;
            return;
        }
        _lastAgreed = false;
        if (locked && ++_disagree < 2) return;   // 1 回だけの食い違いは様子を見る（AI が迷う部分もある）
        _disagree = 0;
        Steered++;
        Log?.Invoke($"拍の AI に合わせる: {60 / g.Period:F1} BPM（追跡は {(pt > 0 ? 60 / pt : 0):F1} BPM・{(locked ? "ロック中" : "ロックなし")}）");   // no-loc（ログだけ）
        tracker.Steer(g.Period, g.BeatT);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _worker.Join(2000);
        _wake.Dispose();
    }
}
