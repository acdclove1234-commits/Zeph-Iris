// ==================================================
// ReactiveLatencyTests.cs - 音が鳴ってから光るまでの遅れ（キック・スネア・ハイハット。合成音で、アプリと同じ時間の流れを再現する）
//   ・音は 10 ミリ秒ずつ、5 ミリ秒遅れて届く（ループバックの取り込みと同じ）
//   ・60 コマ/秒で描き、LED は送ってから 8 ミリ秒後に光る（USB のデバイス）
//   ・遅れ = 出来事を初めて読んだコマが光る時刻 − 本当に鳴った時刻（正解の時刻）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class ReactiveLatency
{
    public const double DeliverySec = 0.005, LedSec = 0.008, Fps = 60;

    /// <summary>種類ごとの結果: 正解の数・見つけた数・正解にない出来事の数・光るまでの遅れ（ミリ秒）・見つかるまでの遅れ（ミリ秒）</summary>
    public sealed record Kind(int Truth, int Found, int Extra, List<double> LightMs, List<double> DetectMs)
    {
        public double Recall => Truth == 0 ? double.NaN : Found / (double)Truth;
        public double P50 => MusicTiming.Quantile(LightMs, 0.5);
        public double P90 => MusicTiming.Quantile(LightMs, 0.9);
        public double DetP50 => MusicTiming.Quantile(DetectMs, 0.5);
        public string Format() => LightMs.Count == 0 ? $"found {Found}/{Truth}, extra {Extra}" :
            $"found {Found}/{Truth}, extra {Extra}, found after {DetP50,4:F1} ms, light after {P50,4:F1} ms (10-90% {MusicTiming.Quantile(LightMs, 0.1):F1}..{P90:F1})";
    }

    /// <summary>
    /// アプリと同じ時間の流れで流し、出来事が光に出るまでの遅れを測る。
    /// truth = 種類ごとの正解の時刻（信号の先頭からの秒）。tol 以内の同じ種類の出来事を「見つけた」とする。
    /// ignoreNear = この時刻から 30 ミリ秒以内の出来事は、正解にない出来事として数えない（キックと同時のハイハットなど）
    /// </summary>
    public static Dictionary<AudioEventKind, Kind> Measure(float[] buf, int fs, Dictionary<AudioEventKind, List<double>> truth,
        double from = 2.0, double tol = 0.025, Dictionary<AudioEventKind, List<double>>? ignoreNear = null, AudioEngineSettings? settings = null)
    {
        var e = new AudioEngine(fs);
        if (settings is not null) e.Settings = settings;
        const double t0 = 1000;
        int chunk = fs / 100, pos = 0;
        long cursor = 0, detCursor = 0;
        var read = new List<AudioEvent>();
        var detScratch = new List<AudioEvent>();
        var seenAt = new Dictionary<long, double>();      // 出来事の番号 → 光った時刻
        var availAt = new Dictionary<long, double>();     // 出来事の番号 → 届いた時刻（塊の最後の音 + 届くまでの遅れ）
        var all = new List<AudioEvent>();
        double total = buf.Length / (double)fs;
        for (int k = 0; ; k++)
        {
            double now = t0 + k / Fps;
            if (now - t0 > total) break;
            while (pos < buf.Length && t0 + (pos + chunk) / (double)fs <= now - DeliverySec)
            {
                int n = Math.Min(chunk, buf.Length - pos);
                e.Push(new ReadOnlySpan<float>(buf, pos, n), t0 + pos / (double)fs);
                detScratch.Clear();
                e.Events.Read(ref detCursor, double.PositiveInfinity, detScratch);
                foreach (var ev in detScratch)
                {
                    availAt[ev.Id] = t0 + (pos + n) / (double)fs + DeliverySec;
                    all.Add(ev);
                }
                pos += n;
            }
            double tEval = now + LedSec;
            read.Clear();
            e.Events.Read(ref cursor, tEval, read);
            foreach (var ev in read) seenAt.TryAdd(ev.Id, now + LedSec);
        }
        var result = new Dictionary<AudioEventKind, Kind>();
        double end = total - 0.1;
        foreach (var (kind, times) in truth)
        {
            var evs = all.Where(x => x.Kind == kind && x.T > t0 + from).ToList();
            var used = new HashSet<long>();
            var light = new List<double>();
            var det = new List<double>();
            var want = times.Where(t => t > from + 0.05 && t < end).ToList();
            int found = 0;
            foreach (double t in want)
            {
                var near = evs.Where(x => Math.Abs(x.T - (t0 + t)) <= tol && !used.Contains(x.Id)).OrderBy(x => Math.Abs(x.T - (t0 + t))).FirstOrDefault();
                if (near.Id == 0) continue;
                found++;
                used.Add(near.Id);
                if (seenAt.TryGetValue(near.Id, out double lit)) light.Add((lit - (t0 + t)) * 1000);
                if (availAt.TryGetValue(near.Id, out double av)) det.Add((av - (t0 + t)) * 1000);
            }
            var ignore = ignoreNear is not null && ignoreNear.TryGetValue(kind, out var ig) ? ig : null;
            int extra = evs.Count(x => !used.Contains(x.Id) && x.T < t0 + end
                && times.All(t => Math.Abs(x.T - (t0 + t)) > 0.03)
                && (ignore is null || ignore.All(t => Math.Abs(x.T - (t0 + t)) > 0.03)));
            result[kind] = new Kind(want.Count, found, extra, light, det);
        }
        return result;
    }

    /// <summary>キック・ハイハット（TestKickEventTiming と同じ）・キック・スネア・ハイハットのループ・MusicBench の曲を流して、種類ごとにまとめる</summary>
    public static Dictionary<AudioEventKind, Kind> Suite(Action<string>? log = null)
    {
        var sum = new Dictionary<AudioEventKind, Kind>();
        void Add(string name, Dictionary<AudioEventKind, Kind> r)
        {
            log?.Invoke($"{name,-28} " + string.Join(" | ", r.Select(x => $"{x.Key} {x.Value.Format()}")));
            foreach (var (k, v) in r)
            {
                if (!sum.TryGetValue(k, out var s)) sum[k] = s = new Kind(0, 0, 0, new(), new());
                s.LightMs.AddRange(v.LightMs);
                s.DetectMs.AddRange(v.DetectMs);
                sum[k] = s with { Truth = s.Truth + v.Truth, Found = s.Found + v.Found, Extra = s.Extra + v.Extra };
            }
        }
        var g = new SignalGen(48000, 20, seed: 7);
        g.Loop(120, 0.3, 20, hats: true);
        Add("loop kick+hat 120", Measure(g.Buf, g.Fs, new() { [AudioEventKind.Kick] = g.KickTimes, [AudioEventKind.Hat] = g.HatTimes },
            ignoreNear: new() { [AudioEventKind.Hat] = g.KickTimes }));
        var g2 = new SignalGen(48000, 16, seed: 8);
        g2.Loop(100, 0.3, 16, hats: true, snare: true);
        Add("loop kick+snare+hat 100", Measure(g2.Buf, g2.Fs, new() { [AudioEventKind.Kick] = g2.KickTimes, [AudioEventKind.Snare] = g2.SnareTimes, [AudioEventKind.Hat] = g2.HatTimes },
            ignoreNear: new() { [AudioEventKind.Hat] = g2.KickTimes.Concat(g2.SnareTimes).ToList() }));
        foreach (var c in MusicBench.Cases())
        {
            if (c.Name.StartsWith("quiet")) continue;   // 小さな音の曲は FaintSoundTests で見る
            var gen = c.Gen;
            // 同時に鳴る音（キックと同時のハイハット・スネア）は、どちらで見つけても正解の取りこぼしにしない
            var kicks = gen.KickTimes;
            var snares = gen.SnareTimes.Where(t => kicks.All(k => Math.Abs(k - t) > 0.03)).ToList();
            var hats = gen.HatTimes.Where(t => kicks.All(k => Math.Abs(k - t) > 0.03) && gen.SnareTimes.All(s => Math.Abs(s - t) > 0.03)).ToList();
            Add(c.Name, Measure(gen.Buf, gen.Fs, new() { [AudioEventKind.Kick] = kicks, [AudioEventKind.Snare] = snares, [AudioEventKind.Hat] = hats },
                ignoreNear: new()
                {
                    [AudioEventKind.Snare] = kicks.Concat(gen.HatTimes).ToList(),
                    [AudioEventKind.Hat] = kicks.Concat(gen.SnareTimes).ToList(),
                }));
        }
        return sum;
    }
}

public static class ReactiveLatencyTests
{
    /// <summary>6.15.7 の決め方（OnsetDetector.EventTune.LowLatency = false）と今の決め方で Suite を流す</summary>
    private static (Dictionary<AudioEventKind, ReactiveLatency.Kind> Before, Dictionary<AudioEventKind, ReactiveLatency.Kind> After) BeforeAfter()
    {
        bool saved = OnsetDetector.EventTune.LowLatency;
        try
        {
            Action<string>? log = Environment.GetEnvironmentVariable("LATENCY_DETAIL") == "1" ? T.Info : null;
            OnsetDetector.EventTune.LowLatency = false;
            var before = ReactiveLatency.Suite(log);
            OnsetDetector.EventTune.LowLatency = true;
            var after = ReactiveLatency.Suite(log);
            return (before, after);
        }
        finally { OnsetDetector.EventTune.LowLatency = saved; }
    }

    /// <summary>
    /// 合成のドラム（ループ・MusicBench の 10 曲）で、スネア・ハイハットが鳴ってから光るまで 33 ミリ秒以下（中央値。
    /// 6.15.7 は約 51・58 ミリ秒）。キックは 6.15.7 より遅くならない。見つける数は減らず、正解にない出来事は増えない
    /// </summary>
    public static void TestSnareAndHatLightWithin33ms()
    {
        var (before, after) = BeforeAfter();
        foreach (var (k, v) in before) T.Info($"6.15.7 {k,-5} {v.Format()}");
        foreach (var (k, v) in after) T.Info($"now    {k,-5} {v.Format()}");
        foreach (var kind in new[] { AudioEventKind.Snare, AudioEventKind.Hat })
        {
            var a = after[kind];
            var b = before[kind];
            T.Check(a.P50 <= 33, $"{kind}: light ≤ 33 ms after the sound (median {a.P50:F1} ms, 6.15.7 {b.P50:F1} ms)");
            T.Check(a.P90 <= b.P90, $"{kind}: the slow tail is not later than 6.15.7 (90% {a.P90:F1} ms, 6.15.7 {b.P90:F1} ms)");
            T.Check(a.Found >= b.Found, $"{kind}: found at least as many as 6.15.7 ({a.Found}/{a.Truth}, 6.15.7 {b.Found}/{b.Truth})");
            T.Check(a.Extra <= b.Extra, $"{kind}: no more events without a sound than 6.15.7 ({a.Extra}, 6.15.7 {b.Extra})");
        }
        var ka = after[AudioEventKind.Kick];
        var kb = before[AudioEventKind.Kick];
        T.Check(ka.P50 <= kb.P50 + 0.5 && ka.P90 <= kb.P90 + 0.5, $"Kick: not later than 6.15.7 (median {ka.P50:F1} / 90% {ka.P90:F1} ms, 6.15.7 {kb.P50:F1} / {kb.P90:F1} ms)");
        T.Check(ka.Found >= kb.Found && ka.Extra <= kb.Extra, $"Kick: same hits and extra events ({ka.Found}/{ka.Truth} extra {ka.Extra}, 6.15.7 {kb.Found} extra {kb.Extra})");
    }

    /// <summary>
    /// 回と回の間に確かめる（ProbeBetweenHops）と、ハイハット・スネアが見つかるのが早くなる（10 ミリ秒ずつ届く音で）。
    /// 見つける数は変わらない（同じ音を 2 回出さない）
    /// </summary>
    public static void TestProbeBetweenHopsFindsEarlier()
    {
        bool saved = OnsetDetector.EventTune.Probe;
        try
        {
            OnsetDetector.EventTune.Probe = false;
            var off = ReactiveLatency.Suite();
            OnsetDetector.EventTune.Probe = true;
            var on = ReactiveLatency.Suite();
            foreach (var kind in new[] { AudioEventKind.Snare, AudioEventKind.Hat })
            {
                var a = on[kind];
                var b = off[kind];
                T.Info($"{kind}: probe on {a.Format()} | off {b.Format()}");
                T.Check(MusicTiming.Quantile(a.DetectMs, 0.5) <= MusicTiming.Quantile(b.DetectMs, 0.5) - 2, $"{kind}: found ≥ 2 ms earlier with the probe (median {a.DetP50:F1} ms vs {b.DetP50:F1} ms)");
                T.Check(Math.Abs(a.Found - b.Found) <= Math.Max(2, 0.02 * b.Found) && a.Extra <= b.Extra, $"{kind}: the probe finds the same hits ({a.Found} vs {b.Found}, extra {a.Extra} vs {b.Extra})");
            }
        }
        finally { OnsetDetector.EventTune.Probe = saved; }
    }

    /// <summary>
    /// 早く出したハイハット・スネアの時刻は、包絡線の立ち上がりの中点なので、本当に鳴った時刻に近い（中央値 ±3 ミリ秒）。
    /// 10 ミリ秒ずつ届く音で、ループのハイハット・スネアを見る
    /// </summary>
    public static void TestFastEventTimesAreAccurate()
    {
        var g = new SignalGen(48000, 16, seed: 8);
        g.Loop(100, 0.3, 16, hats: true, snare: true);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        foreach (var (kind, truth) in new[] { (AudioEventKind.Hat, g.HatTimes), (AudioEventKind.Snare, g.SnareTimes) })
        {
            var errs = new List<double>();
            foreach (double t in truth.Where(t => t > 2.05 && t < 15.9))
            {
                var near = run.Kind(kind).Where(e => Math.Abs(e.T - run.At(t)) < 0.03).OrderBy(e => Math.Abs(e.T - run.At(t))).FirstOrDefault();
                if (near.Id != 0) errs.Add((near.T - run.At(t)) * 1000);
            }
            double med = MusicTiming.Quantile(errs, 0.5);
            T.Info($"{kind}: {errs.Count} events, true-time error median {med:F1} ms (10-90% {MusicTiming.Quantile(errs, 0.1):F1}..{MusicTiming.Quantile(errs, 0.9):F1})");
            T.Check(errs.Count > 0 && Math.Abs(med) <= 3, $"{kind}: event time within 3 ms of the sound (median)");
        }
    }

    /// <summary>
    /// 音の多い曲（DenseMix）の 16 分音符のハイハット: 見つける割合が 6.15.7（約 61%）より上がる。
    /// 時刻が正解から 25 ミリ秒以内のものだけを数えても、6.15.7 より多い
    /// </summary>
    public static void TestDenseMixHatsFound()
    {
        bool saved = OnsetDetector.EventTune.LowLatency;
        try
        {
            foreach (double bpm in new[] { 124.0, 96.0 })
            {
                var g = new SignalGen(48000, 16, seed: (int)bpm + 100);
                g.DenseMix(bpm, 0.3, 16);
                var hats = g.HatTimes.Where(t => t > 2 && t < 15.9).ToList();
                (int Events, int Matched) Run(bool lowLatency)
                {
                    OnsetDetector.EventTune.LowLatency = lowLatency;
                    var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
                    var ev = run.Kind(AudioEventKind.Hat).Where(e => e.T > run.At(2) && e.T < run.At(15.9)).ToList();
                    return (ev.Count, hats.Count(t => ev.Any(e => Math.Abs(e.T - run.At(t)) <= 0.025)));
                }
                var before = Run(false);
                var after = Run(true);
                T.Info($"dense {bpm} BPM: Hat events {after.Events} for {hats.Count} hats ({after.Events / (double)hats.Count:P0}; 6.15.7 {before.Events}), " +
                       $"hats with an event within 25 ms {after.Matched} ({after.Matched / (double)hats.Count:P0}; 6.15.7 {before.Matched})");
                T.Check(after.Events >= 0.7 * hats.Count && after.Events <= 1.05 * hats.Count, $"dense {bpm} BPM: Hat events for 70-105% of the hats");
                T.Check(after.Matched > before.Matched && after.Matched >= 0.6 * hats.Count, $"dense {bpm} BPM: more hats found on time than 6.15.7, and at least 60%");
            }
        }
        finally { OnsetDetector.EventTune.LowLatency = saved; }
    }

    /// <summary>
    /// キックとスネアが同時（DenseMix の 2・4 拍目）・キックとハイハットだけ: 早く出すスネアはキックを取り違えない
    /// （キックの「カチッ」も響き線の帯に出るので、キックの包絡線が上がるのを待ってから確かめる）
    /// </summary>
    public static void TestFastSnareDoesNotFireOnKicks()
    {
        var g = new SignalGen(48000, 20, seed: 7);
        g.Loop(120, 0.3, 20, hats: true);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        int snares = run.Kind(AudioEventKind.Snare).Count(e => e.T > run.At(2));
        T.Info($"kick+hat loop: Snare events {snares}");
        T.Check(snares == 0, "kick+hat loop: no Snare events");
        bool saved = OnsetDetector.EventTune.LowLatency;
        try
        {
            var d = new SignalGen(48000, 16, seed: 224);
            d.DenseMix(124, 0.3, 16);
            OnsetDetector.EventTune.LowLatency = false;
            int before = new EngineRun(new AudioEngine(48000)).Feed(d.Buf).Kind(AudioEventKind.Snare).Count(e => e.T > 1002);
            OnsetDetector.EventTune.LowLatency = true;
            int after = new EngineRun(new AudioEngine(48000)).Feed(d.Buf).Kind(AudioEventKind.Snare).Count(e => e.T > 1002);
            T.Info($"dense mix (snare together with the kick): Snare events {after} (6.15.7 {before})");
            T.Check(after <= before, "dense mix: no more Snare events on kick+snare hits than 6.15.7");
        }
        finally { OnsetDetector.EventTune.LowLatency = saved; }
    }

    /// <summary>
    /// 小さな音の確かめ: 早く出すハイハットは、曲全体の音量より 16 dB 以上小さいと出さない。「反応の感度」を上げると出す。
    /// 20 dB・14 dB 小さい「チッ」・低音の大きい曲の小さなハイハットは FaintSoundTests でも確かめている
    /// </summary>
    public static void TestFastHatsNeedLoudEnoughSound()
    {
        var g = new SignalGen(48000, 12, seed: 41);
        var hats = new List<double>();
        for (double t = 0.3; t < 12; t += 0.5)
        {
            g.Kick(t, 0.8);
            g.Tone(55, 0.25, t + 0.02, t + 0.48);
            hats.Add(t + 0.25);
        }
        foreach (var t in hats) g.Hat(t, 0.006);   // 曲全体よりずっと小さい
        int Fired(double sens)
        {
            var run = new EngineRun(new AudioEngine(48000) { Settings = new AudioEngineSettings(BeatSensitivity: sens) }).Feed(g.Buf);
            return hats.Count(t => t > 2 && run.Kind(AudioEventKind.Hat).Any(e => Math.Abs(e.T - run.At(t)) <= 0.03));
        }
        int n = hats.Count(t => t > 2);
        int normal = Fired(1.0), high = Fired(10.0);
        T.Info($"very quiet hats under loud bass: Hat events {normal}/{n} at sensitivity 1, {high}/{n} at sensitivity 10");
        T.Check(normal == 0, "very quiet hats give no Hat events at the normal sensitivity");
        T.Check(high > normal, "a higher sensitivity lets them through");
    }

    /// <summary>
    /// 先読み（デバイスの遅れ 8・20・60 ミリ秒）があっても、届いたばかりの低音の光は同じ明るさ（以前は先読みの分だけ
    /// 先に減らしていたので、60 ミリ秒では暗かった）。新しい分析結果が届かなくなったら、時間とともに暗くなる
    /// </summary>
    public static void TestDeviceLeadDoesNotDimFreshSound()
    {
        float Render(double leadSec, double staleSec)
        {
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Pulse, Brightness = 100, Gamma = false, StaticColor = "#FF0000" };
            var m = MusicTuning.Resolve(cfg);
            var dev = new RgbDevice(2, "Strip", 30, 4, null, "HID: x");
            var r = new DeviceRenderer(dev);
            var input = new RenderInput { Cfg = cfg, Music = m, Device = new RgbDeviceSettings { Name = dev.Name } };
            var ring = new FeatureRing();
            var f = new AudioFrame { GateOpen = true, Activity = 1 };
            var output = new Rgb[dev.LedCount];
            const double hop = 512 / 48000.0;
            double tf = 100;
            double last = double.NaN;
            // 1 秒の静かな低音のあと、1 コマだけ大きな低音（そこで分析結果が止まる）
            for (int k = 0; k < 94; k++)
            {
                tf += hop;
                f.T = tf;
                f.BassE = f.Bass = k == 93 ? 1f : 0.1f;
                f.LoudVU = 0.5f;
                ring.Write(f);
                double now = tf + 0.004 + (k == 93 ? staleSec : 0);
                input.T = now;
                input.TEval = now + leadSec;
                input.TPrevEval = double.IsNaN(last) ? input.TEval : last;
                input.Dt = double.IsNaN(last) ? 0 : (float)(hop + (k == 93 ? staleSec : 0));
                input.Features = ring;
                ring.Sample(input.TEval, input.Frame);
                input.Beat = BeatState.None;
                input.Events.Clear();
                r.Render(input, output);
                last = input.TEval;
            }
            var l = r.LastLight;
            float v = 0;
            for (int i = 0; i < dev.LedCount; i++) v = Math.Max(v, l[i].MaxChannel);
            return v;
        }
        float b8 = Render(0.008, 0), b20 = Render(0.020, 0), b60 = Render(0.060, 0);
        float stale = Render(0.020, 0.150);
        T.Info($"Pulse brightness right after a bass hit: lead 8 ms {b8:F3}, 20 ms {b20:F3}, 60 ms {b60:F3}; 150 ms later without new frames {stale:F3}");
        T.Check(b8 > 0.3f, "the bass hit lights the LEDs");
        T.Check(Math.Abs(b60 - b8) <= 0.02f * b8 && Math.Abs(b20 - b8) <= 0.02f * b8, "the device lead does not dim a fresh bass hit (within 2 %)");
        T.Check(stale < 0.9f * b20, "without new frames the light still fades with time");
    }

    /// <summary>
    /// パーツごとの役割（高音のパーツ）: 強さ「激しい」・点滅の制限オフでも、ハイハットの光は RoleEffect.HatMinGapSec（0.15 秒）より詰めない。
    /// 0.1 秒ごとのハイハット（16 分音符を全部見つけたとき）では 1 つおきに光る。0.2 秒ごとなら全部光る
    /// </summary>
    public static void TestRoleHatFlashesKeepAMinimumGap()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, Intensities.Intense);
        cfg.Music.ReduceFlashing = false;
        var m = MusicTuning.Resolve(cfg);
        int Flashes(double every)
        {
            var fx = new RoleEffect();
            var c = new EffectContext { Music = m };
            c.Frame.Activity = 1;
            c.Frame.GateOpen = true;
            fx.Reset(c);
            int rises = 0;
            float prev = 0;
            double next = 0.5;
            for (int k = 0; k < 180; k++)
            {
                double t = k / 60.0;
                c.TPrev = c.T;
                c.T = t;
                c.Dt = k == 0 ? 0 : 1 / 60f;
                c.Frame.T = t;
                c.Events.Clear();
                while (next <= t && next < 2.45)
                {
                    c.Events.Add(new AudioEvent(k + 1, AudioEventKind.Hat, next, 2f));
                    next += every;
                }
                fx.Update(c);
                float v = fx.LevelOf(MusicRole.Treble);
                if (t >= 0.4 && v - prev > 0.1f) rises++;   // 最初の下地の明るさの立ち上がりは数えない
                prev = v;
            }
            return rises;
        }
        int fast = Flashes(0.1), slow = Flashes(0.2);
        T.Info($"Role treble at Intense: {fast} flashes for 20 hats 0.1 s apart, {slow} flashes for 10 hats 0.2 s apart");
        T.Check(fast <= 11 && fast >= 9, "hats 0.1 s apart light every other hat");
        T.Check(slow == 10, "hats 0.2 s apart all light");
    }

    /// <summary>ハイパスフィルター: 区切りより十分上はそのまま通し、下は 4 次（2 回）で大きく弱める</summary>
    public static void TestHighPassBiquad()
    {
        double Gain(double hz)
        {
            var a = Biquad.HighPass(48000, 5000, 0.7071);
            var b = Biquad.HighPass(48000, 5000, 0.7071);
            // 2 乗平均で比べる（12 kHz は 1 周期に 4 個しかないので、山の高さは見る位置で変わる）
            double sum = 0, sumIn = 0;
            for (int i = 0; i < 48000; i++)
            {
                double x = Math.Sin(2 * Math.PI * hz * i / 48000);
                double y = b.Process(a.Process(x));
                if (i < 24000) continue;
                sum += y * y;
                sumIn += x * x;
            }
            return 10 * Math.Log10(sum / sumIn + 1e-24);
        }
        double g1k = Gain(1000), g5k = Gain(5000), g12k = Gain(12000);
        T.Info($"5 kHz high-pass twice: 1 kHz {g1k:F1} dB, 5 kHz {g5k:F1} dB, 12 kHz {g12k:F1} dB");
        T.Check(g12k > -1.5, "12 kHz passes (within 1.5 dB)");
        T.Check(Math.Abs(g5k + 6) < 1, "5 kHz is at about −6 dB (two Butterworth stages)");
        T.Check(g1k < -50, "1 kHz is attenuated by more than 50 dB");
    }

    /// <summary>
    /// ハイハットの包絡線（5 kHz 以上）: ハイハットの立ち上がりの時刻を 1 ミリ秒以内で見つける。
    /// キック（低い音）や続いている低音では、立ち上がりを見つけない
    /// </summary>
    public static void TestTransientPathFindsHatOnsets()
    {
        var errs = new List<double>();
        foreach (double offset in new[] { 0.0, 0.0013, 0.0041, 0.0077 })
        {
            var g = new SignalGen(48000, 1.0, seed: 5);
            g.Tone(55, 0.3, 0, 1.0);
            double tHat = 0.5 + offset;
            g.Hat(tHat, 0.1);
            var p = TransientPath.ForHat(48000);
            int end = (int)Math.Round((tHat + 0.006) * 48000);
            bool quietBefore = true;
            for (int i = 0; i < end; i++)
            {
                p.Sample(g.Buf[i]);
                if (i == (int)(0.45 * 48000) && p.TryFindOnset(6, out _, out _)) quietBefore = false;
            }
            T.Check(quietBefore, $"hat at +{offset * 1000:F1} ms: no onset found in the bass tone before it");
            bool found = p.TryFindOnset(6, out int ago, out double rise);
            double t = (end - 1 - ago) / 48000.0;
            errs.Add((t - g.HatTimes[0]) * 1000);
            T.Check(found && rise > 12, $"hat at +{offset * 1000:F1} ms: onset found 6 ms later (rise {rise:F0} dB)");
        }
        T.Info($"hat onset time error: {string.Join(", ", errs.Select(e => e.ToString("F2")))} ms");
        T.Check(errs.All(e => Math.Abs(e) <= 1.0), "hat onset time within 1 ms");

        var k = new SignalGen(48000, 1.0, seed: 6);
        k.Kick(0.5, 0.8, click: false);
        var kp = TransientPath.ForHat(48000);
        for (int i = 0; i < (int)(0.52 * 48000); i++) kp.Sample(k.Buf[i]);
        T.Check(!kp.TryFindOnset(6, out _, out _) || kp.RecentPeakDb() < -60, "a kick without a click gives no hat onset");
    }

    /// <summary>
    /// キックの包絡線の「上がっている途中か」: キックの始まり（5 ミリ秒後）は true、
    /// 前のキックが下がっている途中・スネアの胴の音（キックよりずっと小さい）は false
    /// </summary>
    public static void TestKickPathIsRising()
    {
        var g = new SignalGen(48000, 2.0, seed: 9);
        g.Kick(0.5, 0.8);
        g.Kick(1.0, 0.8);
        g.Snare(1.5, 0.4);
        var kp = new KickPath(48000);
        var at = new Dictionary<double, bool>();
        var probes = new[] { 0.505, 1.005, 1.04, 1.505 };
        int next = 0;
        for (int i = 0; i < g.Buf.Length && next < probes.Length; i++)
        {
            kp.Sample(g.Buf[i]);
            if ((i + 1) % AudioEngine.Hop == 0)
            {
                kp.EndHop(true, 1, 0, 0.01, 0.01, default);
                // 最近のキックの大きさを覚えさせる（OnsetDetector がキックを認めたときと同じ）
                kp.TryFindOnset(OnsetDetector.KickMinRiseDb, OnsetDetector.KickMaxBelowRecentDb, out _, out _);
            }
            if (i + 1 == (int)Math.Round(probes[next] * 48000))
            {
                at[probes[next]] = kp.IsRising(0.020, OnsetDetector.KickMinRiseDb, OnsetDetector.KickMaxBelowRecentDb);
                next++;
            }
        }
        T.Info($"IsRising: 5 ms into the 1st kick {at[0.505]}, 5 ms into the 2nd {at[1.005]}, 40 ms after it {at[1.04]}, 5 ms into a snare {at[1.505]}");
        T.Check(at[0.505] && at[1.005], "true 5 ms after a kick starts");
        T.Check(!at[1.04], "false while the previous kick decays");
        T.Check(!at[1.505], "false for a snare body (much smaller than the kicks)");
    }
}
