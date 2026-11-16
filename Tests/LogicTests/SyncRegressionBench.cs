// ==================================================
// SyncRegressionBench.cs - 調査用: 6.15.7 と同じ決め方と今の決め方で、録音した曲のキック・スネア・ハイハットの数、
//                          テンポの追跡（ロック・最初にロックした時刻・テンポ）、キックの門（打楽器らしさ）で捨てた割合、
//                          光り方ごとの拍との合い方（ModeSyncBench の hit・beat・off）を並べる
//   ・SYNC_WAV=<フォルダー> のときだけ動く（中の *.wav をすべて。ステレオならミッドとサイドに分けて、アプリと同じに流す）
//   ・表示するのは番号だけ: ファイル名が song_NN なら song_NN、それ以外は file_NN（曲名・パスは出さない）
//   ・SYNC_SYNTH=<フォルダー>: 合成した確かめ用の曲（song_01〜04。ステレオ・32 bit 浮動小数）を書く
// 調べる用の環境変数:
//   SYNC_CFG=old,cur,nogate,gateonly,fastonly（比べる決め方。既定 old,cur,nogate）
//     old      = 6.15.7 と同じ（KickTune.PercGate・EventTune.LowLatency・EventTune.Probe・SnareTune.EarlyDecide・InstrumentsEnabled をすべてオフ）
//     cur      = 今のまま（すべてオン）
//     nogate   = 今のまま、キックの門だけオフ
//     gateonly = 6.15.7 に、キックの門だけ足す
//     fastonly = 今のまま、短い FFT の早い出し方（LowLatency）だけオフ
//     new      = 今のコードの既定のまま
//     表示名:スイッチ = 6.15.8 の組から、スイッチだけ変える（例 A:gm=1+near=1+low=1。スイッチの名前は Knob を見る）
//     表示名=new:スイッチ = 今のコードの既定から、スイッチだけ変える
//   SYNC_MINPERC=0.3（すべての組のキックの門の下限を変える）
//   SYNC_STYLE_APP=1（光り方の値も、ミッドとサイド・拍の AI つきで求める。既定はミッドだけ・AI なし）
//   SYNC_AI=0（拍の AI を付けない。既定は付ける: アプリの既定と同じ）
//   SYNC_STYLES=0（光り方ごとの値を出さない）・SYNC_STYLE_LIST=BeatHue,Pulse（光り方を変える）
//   SYNC_SEC=90（曲の頭からこの秒数だけ流す）
//   SYNC_PREDEC=1（96 / 192 kHz の録音を、ここで別の長い FIR で 48 kHz に落としてから流す。アプリの中で落とした結果と比べる）
//   SYNC_DIAG=1（キックの候補の手がかり: 目立ち・「カチッ」の上がり・キックからの大きさの分布を、拍の上と外で分けて出す。
//              スネアの出来事を、長い FFT の決め方と比べて分ける（早く出したスネアが、長い FFT ならキックと同時・ハイハットの漏れだったか））
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Text.RegularExpressions;
using ProFanController;

namespace LogicTests;

public static class SyncRegressionBenchTests
{
    /// <summary>比べる決め方（テストから変えられるスイッチの組）</summary>
    /// <summary>
    /// 比べる決め方（テストから変えられるスイッチの組）。Spec は Knob の「名前=値」を + でつないだもの。
    /// 名前のある組は、すべてのスイッチを書く（コードの既定を変えても、6.15.7・6.15.8 と同じ決め方のまま比べられる）
    /// </summary>
    private sealed record Cfg(string Name, string Spec);

    /// <summary>6.15.8 の決め方（キックの門はいつも・門で落とした候補はスネアにもなる・テンポの追跡にも数えない）</summary>
    private const string Spec6158 = "gate=1+ll=1+probe=1+early=1+inst=1+fs=1+gm=0+near=0+low=0+cb=0.010+epos=1+ctx=0+gu=1+onb=0.07+mp=0.5";
    /// <summary>6.15.7 の決め方（門・早い出し方・楽器の手がかりをすべてオフ）</summary>
    private const string Spec6157 = "gate=0+ll=0+probe=0+early=0+inst=0+fs=1+gm=0+near=0+low=0+cb=0.010+epos=1+ctx=0+gu=1+onb=0.07+mp=0.5";

    private static readonly Dictionary<string, Cfg> Configs = new()
    {
        ["old"] = new("6.15.7", Spec6157),
        ["cur"] = new("6.15.8", Spec6158),
        ["nogate"] = new("cur-gate", Spec6158 + "+gate=0"),
        ["gateonly"] = new("old+gate", Spec6157 + "+gate=1"),
        ["fastonly"] = new("cur-fast", Spec6158 + "+ll=0+probe=0+early=0"),
        // 今のコードの既定のまま（何も変えない）
        ["new"] = new("new", ""),
    };

    /// <summary>
    /// SYNC_CFG の 1 つ: 名前のある組、または「表示名:スイッチ」（6.15.8 の組から、スイッチだけ変える。例 A:gm=1+near=1）。
    /// 「表示名=new:スイッチ」なら、今のコードの既定から変える
    /// </summary>
    private static Cfg? ParseCfg(string token)
    {
        if (Configs.TryGetValue(token, out var c)) return c;
        int colon = token.IndexOf(':');
        if (colon <= 0) return null;
        string name = token[..colon], spec = token[(colon + 1)..];
        if (name.EndsWith("=new", StringComparison.Ordinal)) return new(name[..^4], spec);
        return new(name, Spec6158 + "+" + spec);
    }

    /// <summary>スイッチの名前と、値の入れ方・読み方</summary>
    private static readonly Dictionary<string, (Action<double> Set, Func<double> Get)> Knob = new()
    {
        ["gate"] = (v => OnsetDetector.KickTune.PercGate = v != 0, () => OnsetDetector.KickTune.PercGate ? 1 : 0),
        ["mp"] = (v => OnsetDetector.KickTune.MinPercussive = v, () => OnsetDetector.KickTune.MinPercussive),
        ["ll"] = (v => OnsetDetector.EventTune.LowLatency = v != 0, () => OnsetDetector.EventTune.LowLatency ? 1 : 0),
        ["probe"] = (v => OnsetDetector.EventTune.Probe = v != 0, () => OnsetDetector.EventTune.Probe ? 1 : 0),
        ["early"] = (v => OnsetDetector.SnareTune.EarlyDecide = v != 0, () => OnsetDetector.SnareTune.EarlyDecide ? 1 : 0),
        ["inst"] = (v => AudioEngine.InstrumentsEnabled = v != 0, () => AudioEngine.InstrumentsEnabled ? 1 : 0),
        ["fs"] = (v => OnsetDetector.EventTune.FastSnare = v != 0, () => OnsetDetector.EventTune.FastSnare ? 1 : 0),
        ["gm"] = (v => OnsetDetector.KickTune.GateMode = (int)v, () => OnsetDetector.KickTune.GateMode),
        ["onb"] = (v => OnsetDetector.KickTune.OnBeatSec = v, () => OnsetDetector.KickTune.OnBeatSec),
        ["gu"] = (v => OnsetDetector.KickTune.GateUnlocked = v != 0, () => OnsetDetector.KickTune.GateUnlocked ? 1 : 0),
        ["near"] = (v => OnsetDetector.KickTune.GatedIsKickNear = v != 0, () => OnsetDetector.KickTune.GatedIsKickNear ? 1 : 0),
        ["low"] = (v => OnsetDetector.KickTune.GatedLowOnset = v != 0, () => OnsetDetector.KickTune.GatedLowOnset ? 1 : 0),
        ["cb"] = (v => OnsetDetector.KickTune.ClickBeforeSec = v, () => OnsetDetector.KickTune.ClickBeforeSec),
        ["snmin"] = (v => OnsetDetector.SnareTune.MinStrength = v, () => OnsetDetector.SnareTune.MinStrength),
        ["ctx"] = (v => OnsetDetector.KickTune.ContextSec = v, () => OnsetDetector.KickTune.ContextSec),
        ["cmr"] = (v => OnsetDetector.KickTune.ContextMinRatio = v, () => OnsetDetector.KickTune.ContextMinRatio),
        ["ca"] = (v => OnsetDetector.KickTune.ContextAlpha = v, () => OnsetDetector.KickTune.ContextAlpha),
        ["epos"] = (v => OnsetDetector.SnareTune.EarlyPositive = v != 0, () => OnsetDetector.SnareTune.EarlyPositive ? 1 : 0),
    };

    private static Dictionary<string, double> Save() => Knob.ToDictionary(k => k.Key, k => k.Value.Get());

    private static void Restore(Dictionary<string, double> k)
    {
        foreach (var (name, v) in k) Knob[name].Set(v);
    }

    /// <summary>コードの既定に戻してから、組のスイッチを入れる（minPerc は SYNC_MINPERC があるときだけ）</summary>
    private static void Apply(Cfg c, Dictionary<string, double> defaults, double? minPerc, bool? instruments = null)
    {
        Restore(defaults);
        foreach (var part in c.Spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && Knob.TryGetValue(kv[0], out var k) && double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
                k.Set(v);
            else T.Info($"unknown knob '{part}' in {c.Name}");
        }
        if (minPerc is double mp) OnsetDetector.KickTune.MinPercussive = mp;
        if (instruments is bool b) AudioEngine.InstrumentsEnabled = b;
    }

    /// <summary>1 回流した結果</summary>
    private sealed class RunResult
    {
        public required string Cfg;
        public double Kick, Snare, Hat, Onset;
        public double Locked, Synced, AppBpm;
        public double FirstLock = double.NaN;
        public required BeatEval Beat;
        /// <summary>キックの候補（キックの包絡線でも確かめられた低音の立ち上がり）の数と、門の下限より打楽器らしさが低かった数</summary>
        public int Cands, LowPerc;
        /// <summary>物差しの拍のうち、キックが ±50 ms にあった割合・キックのうち拍（±70 ms）の上・裏拍（±50 ms）の上の割合</summary>
        public double BeatsWithKick, KickOnBeat, KickOnHalf;
        /// <summary>スネアのうち、同じ回のキック（±30 ms）と重なった割合・物差しの拍（±50 ms）の上の割合</summary>
        public double SnareWithKick, SnareOnBeat;
        public double Xrt;
        public int AiRuns, AiSteered, AiHalf;
        public List<(double T, double Prom, double Rise, double Rel)> Candidates = new();
        public List<AudioEvent> Events = new();
        /// <summary>長い FFT でスネアの候補を決めたとき（時刻・キックと同時・ハイハットの漏れ）</summary>
        public List<(double T, bool KickNear, bool HatOnly)> SnareDecisions = new();
    }

    private static readonly Lazy<BeatThis?> Model = new(() => BeatThis.TryCreate(out _));

    public static void TestSyncRegressionBench()
    {
        string? dir = Environment.GetEnvironmentVariable("SYNC_WAV");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var keys = (Environment.GetEnvironmentVariable("SYNC_CFG") ?? "old,cur,nogate").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cfgs = keys.Select(ParseCfg).OfType<Cfg>().ToList();
        if (cfgs.Count == 0) { T.Skip("SYNC_CFG names no known config"); return; }
        double? minPercArg = double.TryParse(Environment.GetEnvironmentVariable("SYNC_MINPERC"), out double mp) ? mp : null;
        double minPerc = minPercArg ?? OnsetDetector.KickTune.MinPercussive;        bool useAi = Environment.GetEnvironmentVariable("SYNC_AI") != "0";
        bool styles = Environment.GetEnvironmentVariable("SYNC_STYLES") != "0";
        bool diag = Environment.GetEnvironmentVariable("SYNC_DIAG") == "1";
        // SYNC_STYLE_APP=1: 光り方の値も、アプリと同じにミッドとサイド・拍の AI つきで流して求める（既定はミッドだけ・AI なし）
        bool styleApp = Environment.GetEnvironmentVariable("SYNC_STYLE_APP") == "1";
        double maxSec = double.TryParse(Environment.GetEnvironmentVariable("SYNC_SEC"), out double ms) && ms > 0 ? ms : double.PositiveInfinity;
        string[] styleList = (Environment.GetEnvironmentVariable("SYNC_STYLE_LIST")
            ?? string.Join(",", MusicStyles.BeatHue, MusicStyles.Pulse, MusicStyles.Mix, MusicStyles.Role, MusicStyles.Ensemble))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        BeatThis? model = useAi ? Model.Value : null;
        T.Info($"configs {string.Join(", ", cfgs.Select(c => c.Name))}; kick gate min {minPerc:F2}; beat AI {(model is null ? "off" : "on")}; styles {(styles ? string.Join(",", styleList) : "off")}");

        var files = Directory.GetFiles(dir, "*.wav").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var rows = new Dictionary<string, List<RunResult>>();
        var styleRows = new Dictionary<string, List<ModeSyncBenchTests.ModeScore>>();
        var saved = Save();
        try
        {
            for (int fi = 0; fi < files.Count; fi++)
            {
                string id = SongId(files[fi], fi);
                var (mid, side, fs) = ReadWavStereo(files[fi]);
                // SYNC_PREDEC=1: 高いサンプリング周波数の録音を、ここで別のフィルター（長い FIR）で 44.1 / 48 kHz に落としてから流す
                // （アプリの中の Decimator を通した結果と比べ、分析が落とした周波数で動いているかを確かめる）
                if (Environment.GetEnvironmentVariable("SYNC_PREDEC") == "1" && Decimator.FactorFor(fs) is int pf and > 1)
                {
                    mid = PreDecimate(mid, pf);
                    if (side.Length > 0) side = PreDecimate(side, pf);
                    fs /= pf;
                }
                if (mid.Length == 0) { T.Info($"{id}: unreadable, skipped"); continue; }
                if (mid.Length > maxSec * fs)
                {
                    mid = mid.AsSpan(0, (int)(maxSec * fs)).ToArray();
                    if (side.Length > mid.Length) side = side.AsSpan(0, mid.Length).ToArray();
                }
                List<double>? refBeats = null;
                double refBpm = 0;
                List<(double T, double Odf)>? refHops = null;
                var results = new List<RunResult>();
                foreach (var c in cfgs)
                {
                    Apply(c, saved, minPercArg);
                    var r = Run(mid, side, fs, c, model, ref refBeats, ref refBpm, ref refHops);
                    results.Add(r);
                    (rows.TryGetValue(c.Name, out var l) ? l : rows[c.Name] = new()).Add(r);
                }
                var probe = new AudioEngine(fs);
                T.Info($"{id} {mid.Length / (double)fs,4:F0}s {(side.Length > 0 ? "stereo" : "mono  ")} {fs} Hz (analysis {probe.AnalysisRate} Hz, FFT {probe.FftSize})  ref {refBpm,5:F1} BPM");
                foreach (var r in results) T.Info("   " + Format(r, minPerc));
                // 同じ決め方の組で、出来事がどれだけ違うか（old と cur-gate など、キックの門以外の違いを見る）
                if (results.Count >= 2) T.Info("   events vs first: " + string.Join("  ", results.Skip(1).Select(r => $"{r.Cfg} {EventDiff(results[0].Events, r.Events)}")));
                if (diag) foreach (var r in results) { CueReport(r, refBeats!, minPerc); SnareReport(r); }
                if (styles)
                {
                    foreach (var c in cfgs)
                    {
                        // 光り方の値は、楽器の手がかりも求めて比べる（出来事は InstrumentsEnabled で変わらない。Ensemble・Role が手がかりを使う）
                        Apply(c, saved, minPercArg, instruments: true);
                        var scores = styleApp
                            ? ModeSyncBenchTests.RunSong(mid, fs, styleList, out _, out _, side: side, model: model)
                            : ModeSyncBenchTests.RunSong(mid, fs, styleList, out _, out _);
                        foreach (var s in scores)
                        {
                            T.Info($"   {c.Name,-8} " + ModeSyncBenchTests.Format(s));
                            string key = c.Name + "|" + s.Mode;
                            (styleRows.TryGetValue(key, out var l) ? l : styleRows[key] = new()).Add(s);
                        }
                    }
                }
            }
        }
        finally { Restore(saved); }

        if (rows.Count == 0) return;
        T.Info($"totals over {rows.First().Value.Count} songs:");
        foreach (var c in cfgs.Where(c => rows.ContainsKey(c.Name)))
        {
            var l = rows[c.Name];
            double A(Func<RunResult, double> f) => l.Select(f).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
            int cands = l.Sum(r => r.Cands), low = l.Sum(r => r.LowPerc);
            T.Info($"   {c.Name,-8} kick {A(r => r.Kick),4:F2} snare {A(r => r.Snare),4:F2} (w/ kick {A(r => r.SnareWithKick) * 100,2:F0}% on-beat {A(r => r.SnareOnBeat) * 100,2:F0}%) hat {A(r => r.Hat),4:F2}/s  beats w/ kick {A(r => r.BeatsWithKick) * 100,3:F0}%  " +
                   $"kick on-beat {A(r => r.KickOnBeat) * 100,3:F0}% half {A(r => r.KickOnHalf) * 100,3:F0}%  locked {A(r => r.Locked) * 100,3:F0}% synced {A(r => r.Synced) * 100,3:F0}%  " +
                   $"first lock {A(r => r.FirstLock),4:F1} s (never {l.Count(r => double.IsNaN(r.FirstLock))})  flash on-beat {A(r => r.Beat.OnFrac) * 100,3:F0}% wrong {A(r => r.Beat.WrongFrac) * 100,3:F0}%  " +
                   $"switches {A(r => r.Beat.SwitchesPerMin),4:F1}/min  low-perc cands {low}/{cands} ({(cands > 0 ? low * 100.0 / cands : 0),3:F0}%)  AI half-shifts {l.Sum(r => r.AiHalf)}  x{A(r => r.Xrt),4:F0} realtime");
        }
        if (styleRows.Count > 0)
        {
            T.Info("style averages:");
            foreach (var c in cfgs)
                foreach (var m in styleList)
                {
                    if (!styleRows.TryGetValue(c.Name + "|" + m, out var l) || l.Count == 0) continue;
                    double A(Func<ModeSyncBenchTests.ModeScore, double> f) => l.Select(f).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
                    T.Info($"   {c.Name,-8} {m,-9} hit {A(s => s.Hit) * 100,3:F0}% +sub {A(s => s.Half) * 100,3:F0}% stray {A(s => s.RandAmp) * 100,3:F0}%  beat {A(s => s.Beat) * 100,3:F0}%  " +
                           $"off {A(s => s.OffMs),4:F0} ms  resp {A(s => s.Resp),5:F2}  jit {A(s => s.Jit),5:F2}  flashes {A(s => s.Flashes),5:F0}");
                }
        }
    }

    /// <summary>表示する曲の印（song_NN ならそのまま。それ以外の名前は出さない）</summary>
    private static string SongId(string path, int index)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        return Regex.IsMatch(n, @"^song_\d{1,4}$") ? n : $"file_{index + 1:00}";
    }

    private static RunResult Run(float[] mid, float[] side, int fs, Cfg c, BeatThis? model,
        ref List<double>? refBeats, ref double refBpm, ref List<(double T, double Odf)>? refHops)
    {
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var hops = new List<(double T, double Odf)>();
        // 物差しの強さ（MusicWavTests.AnalyzeSong と同じ重み）。帯の立ち上がりは決め方で変わらないので、最初の回のものを使う
        e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        var res = new RunResult { Cfg = c.Name, Beat = null! };
        e.Onsets.DiagKickCandidate = (t, prom, rise, rel) => res.Candidates.Add((t, prom, rise, rel));
        e.Onsets.DiagSnare = (t, s, g1, kickNear, hatOnly) => res.SnareDecisions.Add((t, kickNear, hatOnly));
        BeatAssist? assist = null;
        if (model is not null)
        {
            e.Assist = assist = new BeatAssist(model, fs) { Inline = true };
            assist.Log = m => { if (m.Contains("半拍")) res.AiHalf++; };
        }
        var run = new EngineRun(e);
        long cursor = 0;
        double lastT = double.NegativeInfinity;
        // アプリの取り込みと同じ 10 ミリ秒ずつ（48 kHz で 480 個、192 kHz で 1,920 個。回の途中の早い出し方 ProbeBetweenHops は Push ごとに動くので、細かさをそろえる）
        int chunk = Math.Max(1, fs / 100);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < mid.Length; i += chunk)
        {
            int n = Math.Min(chunk, mid.Length - i);
            var sd = side.Length >= i + n ? new ReadOnlySpan<float>(side, i, n) : ReadOnlySpan<float>.Empty;
            e.Push(new ReadOnlySpan<float>(mid, i, n), sd, run.T0 + i / (double)fs);
            if (e.Features.LatestT > lastT)
            {
                var f = new AudioFrame();
                e.Features.Latest(f);
                lastT = f.T;
                run.Frames.Add(f);
                run.Beats.Add(e.Beat);
                run.Locks.Add(e.BeatLock);
            }
            e.Events.Read(ref cursor, double.PositiveInfinity, run.Events);
        }
        sw.Stop();
        if (assist is not null)
        {
            res.AiRuns = assist.Runs;
            res.AiSteered = assist.Steered;
            assist.Dispose();
        }
        res.Xrt = mid.Length / (double)fs / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
        if (refBeats is null)
        {
            refBeats = BeatReference.Track(hops, out refBpm);
            refHops = hops;
        }
        res.Beat = BeatEvaluator.Evaluate(run, refHops!, refBeats, refBpm);
        res.Events = run.Events.ToList();

        double from = run.T0 + 8, end = run.Frames.Count > 0 ? run.Frames[^1].T : from;
        double secs = Math.Max(1e-9, end - from);
        double Rate(AudioEventKind k) => run.Events.Count(x => x.Kind == k && x.T >= from) / secs;
        res.Kick = Rate(AudioEventKind.Kick);
        res.Snare = Rate(AudioEventKind.Snare);
        res.Hat = Rate(AudioEventKind.Hat);
        res.Onset = Rate(AudioEventKind.Onset);
        int after = 0, locked = 0, synced = 0;
        var bpms = new List<double>();
        for (int i = 0; i < run.Frames.Count; i++)
        {
            double s = run.Frames[i].T - run.T0;
            if (double.IsNaN(res.FirstLock) && run.Locks[i] == BeatLock.Locked) res.FirstLock = s;
            if (s < 5) continue;
            after++;
            if (run.Locks[i] == BeatLock.Locked) locked++;
            if (run.Locks[i] != BeatLock.Unlocked) { synced++; if (run.Beats[i].HasTempo) bpms.Add(run.Beats[i].Bpm); }
        }
        res.Locked = locked / (double)Math.Max(1, after);
        res.Synced = synced / (double)Math.Max(1, after);
        res.AppBpm = Median(bpms);

        // キックと物差しの拍（曲の頭からの秒にそろえる）
        var rb = refBeats.Select(t => t - run.T0).Where(t => t >= 8).ToList();
        var mids = rb.Zip(rb.Skip(1), (a, b) => 0.5 * (a + b)).ToList();
        var kicks = run.Events.Where(x => x.Kind == AudioEventKind.Kick).Select(x => x.T - run.T0).Where(t => t >= 8).OrderBy(t => t).ToList();
        res.BeatsWithKick = rb.Count > 0 ? rb.Count(t => Dist(kicks, t) <= 0.05) / (double)rb.Count : double.NaN;
        res.KickOnBeat = kicks.Count > 0 ? kicks.Count(t => Dist(rb, t) <= 0.07) / (double)kicks.Count : double.NaN;
        res.KickOnHalf = kicks.Count > 0 ? kicks.Count(t => Dist(rb, t) > 0.07 && Dist(mids, t) <= 0.05) / (double)kicks.Count : double.NaN;
        var snares = run.Events.Where(x => x.Kind == AudioEventKind.Snare).Select(x => x.T - run.T0).Where(t => t >= 8).OrderBy(t => t).ToList();
        res.SnareWithKick = snares.Count > 0 ? snares.Count(t => Dist(kicks, t) <= 0.03) / (double)snares.Count : double.NaN;
        res.SnareOnBeat = snares.Count > 0 ? snares.Count(t => Dist(rb, t) <= 0.05) / (double)snares.Count : double.NaN;
        var cands = res.Candidates.Where(x => x.T >= from).ToList();
        res.Cands = cands.Count;
        res.LowPerc = cands.Count(x => Perc(x) is double p && double.IsFinite(p) && p < OnsetDetector.KickTune.MinPercussive);
        return res;
    }

    private static double Perc((double T, double Prom, double Rise, double Rel) x) => OnsetDetector.Percussiveness(x.Rise, x.Rel, x.Prom);

    private static string Format(RunResult r, double minPerc) =>
        $"{r.Cfg,-8} kick {r.Kick,4:F2} snare {r.Snare,4:F2} (w/ kick {r.SnareWithKick * 100,2:F0}% on-beat {r.SnareOnBeat * 100,2:F0}%) hat {r.Hat,4:F2} onset {r.Onset,4:F2}/s  beats w/ kick {r.BeatsWithKick * 100,3:F0}%  kick on-beat {r.KickOnBeat * 100,3:F0}% half {r.KickOnHalf * 100,3:F0}%  " +
        $"locked {r.Locked * 100,3:F0}% synced {r.Synced * 100,3:F0}%  first lock {(double.IsNaN(r.FirstLock) ? " never" : $"{r.FirstLock,4:F1} s")}  app {r.AppBpm,5:F1} BPM  " +
        $"flash on-beat {r.Beat.OnFrac * 100,3:F0}% wrong {r.Beat.WrongFrac * 100,3:F0}% sw {r.Beat.SwitchesPerMin,4:F1}/min  " +
        $"perc<{minPerc:F2}: {r.LowPerc}/{r.Cands} cands ({(r.Cands > 0 ? r.LowPerc * 100.0 / r.Cands : 0),3:F0}%)  AI runs {r.AiRuns} steer {r.AiSteered} half {r.AiHalf}  x{r.Xrt,4:F0} rt";

    /// <summary>2 つの出来事の並びの違い（種類ごとの数の差と、同じ種類・±1 ms で対応がつかなかった数）</summary>
    private static string EventDiff(List<AudioEvent> a, List<AudioEvent> b)
    {
        if (a.Count == b.Count && a.Zip(b).All(p => p.First.Kind == p.Second.Kind && Math.Abs(p.First.T - p.Second.T) < 1e-9 && p.First.Strength == p.Second.Strength))
            return "identical";
        var parts = new List<string>();
        foreach (var k in new[] { AudioEventKind.Kick, AudioEventKind.Snare, AudioEventKind.Hat, AudioEventKind.Onset })
        {
            var ta = a.Where(x => x.Kind == k).Select(x => x.T).OrderBy(t => t).ToList();
            var tb = b.Where(x => x.Kind == k).Select(x => x.T).OrderBy(t => t).ToList();
            int onlyA = ta.Count(t => Dist(tb, t) > 0.001), onlyB = tb.Count(t => Dist(ta, t) > 0.001);
            if (onlyA > 0 || onlyB > 0) parts.Add($"{k} -{onlyA}/+{onlyB}");
        }
        return parts.Count == 0 ? "same times" : string.Join(" ", parts);
    }

    /// <summary>
    /// キックの候補の手がかりの分布。物差しの拍（±40 ms）の上の候補は本物のキックが多いはず、外はベースの音の出だしが多いはず。
    /// 「カチッ」だけ・目立ちだけで門を通る割合も出す（どちらの手がかりが本物のキックを落としているか）
    /// </summary>
    private static void CueReport(RunResult r, List<double> refBeats, double minPerc)
    {
        double t0 = 1000.0; // EngineRun の始まり
        var rb = refBeats.ToList();
        var cands = r.Candidates.Where(x => x.T - t0 >= 8).ToList();
        foreach (var (name, set) in new[] { ("on-beat", cands.Where(x => Dist(rb, x.T) <= 0.04).ToList()), ("off-beat", cands.Where(x => Dist(rb, x.T) > 0.04).ToList()) })
        {
            if (set.Count == 0) { T.Info($"   {r.Cfg,-8} cues {name}: none"); continue; }
            double Pass(Func<(double T, double Prom, double Rise, double Rel), double> f) => set.Count(x => f(x) >= minPerc) * 100.0 / set.Count;
            T.Info($"   {r.Cfg,-8} cues {name,-8} n {set.Count,4}  prom dB {Q(set.Select(x => x.Prom))}  click rise dB {Q(set.Select(x => x.Rise))}  click rel dB {Q(set.Select(x => x.Rel))}  " +
                   $"pass: perc {Pass(Perc),3:F0}% click-only {Pass(x => OnsetDetector.Percussiveness(x.Rise, x.Rel, double.NaN)),3:F0}% prom-only {Pass(x => OnsetDetector.Percussiveness(double.NaN, double.NaN, x.Prom)),3:F0}%  perc > 0 / 0.1 / 0.2 / 0.3: {set.Count(x => !(Perc(x) <= 0)) * 100.0 / set.Count,3:F0}% {set.Count(x => !(Perc(x) < 0.1)) * 100.0 / set.Count,3:F0}% {set.Count(x => !(Perc(x) < 0.2)) * 100.0 / set.Count,3:F0}% {set.Count(x => !(Perc(x) < 0.3)) * 100.0 / set.Count,3:F0}%");
        }
    }

    /// <summary>
    /// スネアの出来事を、長い FFT の決め方と比べて分ける（早く出したスネアが、長い FFT ならどう決められたか）:
    /// agree = 長い FFT でもスネア、kick = キックと同時、hat = ハイハットの漏れ、none = 長い FFT では候補にならなかった
    /// </summary>
    private static void SnareReport(RunResult r)
    {
        var sn = r.Events.Where(x => x.Kind == AudioEventKind.Snare && x.T - 1000.0 >= 8).ToList();
        int agree = 0, kick = 0, hat = 0, none = 0;
        foreach (var s in sn)
        {
            var d = r.SnareDecisions.Where(x => Math.Abs(x.T - s.T) <= 0.04).OrderBy(x => Math.Abs(x.T - s.T)).ToList();
            if (d.Count == 0) none++;
            else if (!d[0].KickNear && !d[0].HatOnly) agree++;
            else if (d[0].KickNear) kick++;
            else hat++;
        }
        T.Info($"   {r.Cfg,-8} snares {sn.Count}: long-FFT agrees {agree}, kick {kick}, hat leak {hat}, no long-FFT candidate {none}");
    }

    private static string Q(IEnumerable<double> xs)
    {
        var a = xs.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (a.Length == 0) return "-";
        double P(double q) => a[(int)Math.Clamp(q * (a.Length - 1), 0, a.Length - 1)];
        return $"{P(0.1),5:F1}/{P(0.5),5:F1}/{P(0.9),5:F1}";
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return double.NaN;
        var a = xs.OrderBy(x => x).ToArray();
        return a[a.Length / 2];
    }

    /// <summary>並べた時刻 xs のうち、t にいちばん近いものまでの距離（なければ無限大）</summary>
    private static double Dist(List<double> xs, double t)
    {
        int k = xs.BinarySearch(t);
        if (k >= 0) return 0;
        k = ~k;
        double best = double.PositiveInfinity;
        if (k < xs.Count) best = xs[k] - t;
        if (k > 0) best = Math.Min(best, t - xs[k - 1]);
        return best;
    }

    /// <summary>
    /// 調べる用（SYNC_PREDEC）: factor 分の 1 に落とす。アプリの Decimator（32·factor + 1 タップ）とは別の、長い FIR
    /// （ブラックマン窓の sinc 128·factor + 1 タップ・通過帯域の端 20 kHz）。遅れは前を詰めて 0 にする
    /// </summary>
    private static float[] PreDecimate(float[] x, int factor)
    {
        int taps = 128 * factor + 1, m = taps - 1, half = m / 2;
        double fc = 20000.0 / (48000.0 * factor); // 入力のサンプリング周波数に対する割合（48 kHz 系を想定。44.1 kHz 系でも 20 kHz はナイキストの内側）
        var h = new double[taps];
        double sum = 0;
        for (int n = 0; n < taps; n++)
        {
            double k = n - half;
            double sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            h[n] = sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * n / m) + 0.08 * Math.Cos(4 * Math.PI * n / m));
            sum += h[n];
        }
        var y = new float[x.Length / factor];
        for (int j = 0; j < y.Length; j++)
        {
            int c = j * factor;
            double acc = 0;
            for (int n = 0; n < taps; n++)
            {
                int i = c + n - half;
                if ((uint)i < (uint)x.Length) acc += h[n] * x[i];
            }
            y[j] = (float)(acc / sum);
        }
        return y;
    }

    /// <summary>WAV を読んで、ミッド (L + R) / 2 とサイド (L − R) / 2 にする（アプリの PcmConvert.ToMono・ToSide と同じ。1 チャンネルならサイドは空）</summary>
    public static (float[] Mid, float[] Side, int Fs) ReadWavStereo(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        int ch = 2, fs = 48000, bits = 32, format = 3;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                format = br.ReadUInt16();
                ch = br.ReadInt16();
                fs = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                bool isFloat = format == 3 || (format != 1 && bits == 32);
                int bytes = bits / 8;
                long avail = Math.Min(size, br.BaseStream.Length - br.BaseStream.Position);
                int n = (int)(avail / bytes / Math.Max(1, ch));
                var mid = new float[n];
                var side = ch >= 2 ? new float[n] : Array.Empty<float>();
                float Read() => bits switch
                {
                    16 => br.ReadInt16() / 32768f,
                    24 => (br.ReadByte() | (br.ReadByte() << 8) | (br.ReadSByte() << 16)) / 8388608f,
                    _ => isFloat ? br.ReadSingle() : br.ReadInt32() / 2147483648f,
                };
                for (int i = 0; i < n; i++)
                {
                    float s = 0, l = 0, r = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = Read();
                        s += v;
                        if (c == 0) l = v;
                        else if (c == 1) r = v;
                    }
                    mid[i] = s / ch;
                    if (ch >= 2) side[i] = 0.5f * (l - r);
                }
                return (mid, side, fs);
            }
            else br.ReadBytes(size + (size & 1));
        }
        return (Array.Empty<float>(), Array.Empty<float>(), fs);
    }

    /// <summary>
    /// 確かめ用の合成した曲を SYNC_SYNTH のフォルダーに書く（ステレオ・32 bit 浮動小数・48 kHz・45 秒）。
    /// 実際の曲でキックの門が落としそうな作りを、1 曲に 1 つずつ入れる:
    ///   song_01 … 音の多い曲（DenseMix 124 BPM。合成のキックは「カチッ」が大きい）。ハイハットを左右に広げる
    ///   song_02 … 「カチッ」のないキック（808 のような音）＋ 8 分のベース 120 BPM
    ///   song_03 … 声・和音・8 分のベースで 1〜5 kHz が埋まり、キックで全体が沈む（サイドチェイン）＋ リミッター 128 BPM
    ///   song_04 … キックと同時に始まるベース（重ねた低音）＋ 大きなハイハット・スネア 100 BPM
    /// </summary>
    public static void TestWriteSyncSynth()
    {
        string? dir = Environment.GetEnvironmentVariable("SYNC_SYNTH");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        const int fs = 48000;
        const double sec = 45;
        for (int song = 1; song <= 4; song++)
        {
            var c = new SignalGen(fs, sec, seed: 100 + song);
            var s = new SignalGen(fs, sec, seed: 200 + song);
            switch (song)
            {
                case 1:
                    c.DenseMix(124, 0.5, sec - 0.5);
                    for (double t = 0.5; t < sec - 0.5; t += 60 / 124.0 / 4) s.Hat(t, 0.03);
                    s.Pink(-42, 0.5, sec - 0.5);
                    break;
                case 2:
                {
                    double p = 60 / 120.0;
                    int k = 0;
                    for (double t = 0.5; t < sec - 0.5; t += p, k++)
                    {
                        c.Kick(t, 0.6, click: false);
                        if (k % 2 == 1) c.Snare(t, 0.25);
                        s.Hat(t + p / 2, 0.05);
                    }
                    c.BassLine(0.5 + p / 2, sec - 0.5, 4, new[] { 55.0, 55, 65.4, 49 }, 0.22);
                    c.Pink(-34, 0.5, sec - 0.5);
                    s.PadChord(0.5, sec - 0.5, new[] { 261.6, 329.6, 392 }, 0.03);
                    break;
                }
                case 3:
                {
                    c.DenseMix(128, 0.5, sec - 0.5);
                    c.VocalTone(0.5, sec - 0.5, new[] { 392.0, 440, 349.2, 392 }, 60 / 128.0 * 2, 0.10);
                    c.BassLine(0.5 + 60 / 128.0 / 2, sec - 0.5, 128 / 60.0 * 2, new[] { 55.0, 82.4, 65.4, 49 }, 0.15);
                    s.PadChord(0.5, sec - 0.5, new[] { 523.3, 659.3, 784 }, 0.04);
                    s.Pink(-36, 0.5, sec - 0.5);
                    // キックで全体が沈む（6 dB。150 ms で戻る）・その後リミッター（tanh）
                    var kickT = c.KickTimes.ToList();
                    foreach (var g in new[] { c, s })
                    {
                        int ki = 0;
                        for (int i = 0; i < g.Buf.Length; i++)
                        {
                            double t = i / (double)fs;
                            while (ki + 1 < kickT.Count && kickT[ki + 1] <= t) ki++;
                            double since = ki < kickT.Count && kickT[ki] <= t ? t - kickT[ki] : 10;
                            double duck = since < 0.15 ? 1 - 0.5 * (1 - since / 0.15) : 1;
                            g.Buf[i] = (float)(g.Buf[i] * duck);
                        }
                    }
                    for (int i = 0; i < c.Buf.Length; i++)
                    {
                        c.Buf[i] = (float)(0.9 * Math.Tanh(2.0 * c.Buf[i]));
                        s.Buf[i] = (float)(0.9 * Math.Tanh(2.0 * s.Buf[i]));
                    }
                    break;
                }
                case 4:
                {
                    double p = 60 / 100.0;
                    int k = 0;
                    for (double t = 0.5; t < sec - 0.5; t += p, k++)
                    {
                        c.Kick(t, 0.5);
                        if (k % 2 == 1) c.Snare(t, 0.45);
                        for (int q = 0; q < 4; q++) s.Hat(t + q * p / 4, q % 2 == 0 ? 0.12 : 0.08);
                    }
                    c.BassLine(0.5, sec - 0.5, 100 / 60.0, new[] { 55.0, 61.7, 49, 65.4 }, 0.30);
                    c.Pink(-32, 0.5, sec - 0.5);
                    break;
                }
            }
            string path = Path.Combine(dir, $"song_{song:00}.wav");
            using var bw = new BinaryWriter(File.Create(path));
            int n = c.Buf.Length;
            bw.Write("RIFF"u8.ToArray()); bw.Write(36 + n * 8); bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)3); bw.Write((short)2); bw.Write(fs); bw.Write(fs * 8); bw.Write((short)8); bw.Write((short)32);
            bw.Write("data"u8.ToArray()); bw.Write(n * 8);
            for (int i = 0; i < n; i++)
            {
                bw.Write(Math.Clamp(c.Buf[i] + s.Buf[i], -1f, 1f));
                bw.Write(Math.Clamp(c.Buf[i] - s.Buf[i], -1f, 1f));
            }
            T.Info($"wrote song_{song:00}.wav");
        }
    }
}
