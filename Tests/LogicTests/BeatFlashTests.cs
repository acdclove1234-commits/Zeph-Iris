// ==================================================
// BeatFlashTests.cs - 「ビートで色替え」が拍（つかめないときはキック）だけで光ることの確かめ
//   （合成の分析結果を手で渡して描く。録音で測るのは BeatFlashBench）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class BeatFlashTests
{
    private const double Fps = 60, Period = 0.5, T0 = 1.0;

    /// <summary>描いた結果（コマの時刻と、LED のいちばん明るい成分の最大）</summary>
    private sealed record Trace(List<double> T, List<float> B)
    {
        /// <summary>明るさが 1 コマで minRise 以上上がった時刻（続けて上がったコマは 1 つにまとめる）</summary>
        public List<double> Rises(double from, double minRise = 0.04)
        {
            var list = new List<double>();
            for (int i = 1; i < B.Count; i++)
            {
                if (T[i] < from || B[i] - B[i - 1] < minRise) continue;
                if (list.Count > 0 && T[i] - list[^1] < 0.06) continue;
                list.Add(T[i]);
            }
            return list;
        }

        /// <summary>時刻 t から 0.1 秒の間のいちばん明るい値</summary>
        public float PeakAfter(double t)
        {
            float m = 0;
            for (int i = 0; i < T.Count; i++) if (T[i] >= t - 1e-9 && T[i] < t + 0.1) m = Math.Max(m, B[i]);
            return m;
        }
    }

    /// <summary>
    /// 光り方 style を、合成の分析結果で 1 秒〜end 秒まで 60 コマ／秒で描く。
    /// beat = テンポ（null ならつかめていない）、bass = 時刻 → 低音の強さ、events = 鳴った出来事（そのコマで届く）
    /// </summary>
    private static Trace Run(string style, BeatState? beat, Func<double, float> bass, IReadOnlyList<AudioEvent> events, double end = 7,
        Func<ResolvedMusic, ResolvedMusic>? tweak = null)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false, StaticColor = "#6A00FF" };
        var m = MusicTuning.Resolve(cfg) with { Effect = style };
        if (tweak is not null) m = tweak(m);
        var dev = new RgbDevice(2, "Strip", 10, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var input = new RenderInput { Cfg = cfg, Music = m, Device = new RgbDeviceSettings { Name = dev.Name } };
        input.Frame.GateOpen = true;
        input.Frame.Activity = 1;
        // 音は鳴っている（デジタルの無音の中の予測の拍では光らないため）
        input.Frame.RmsDb = -20;
        var output = new Rgb[dev.LedCount];
        var tr = new Trace(new List<double>(), new List<float>());
        double last = double.NaN;
        int next = 0;
        for (int k = 0; ; k++)
        {
            double t = T0 + k / Fps;
            if (t > end) break;
            input.T = input.TEval = t;
            input.TPrevEval = double.IsNaN(last) ? t : last;
            input.Dt = double.IsNaN(last) ? 0 : t - last;
            input.Frame.T = t;
            input.Frame.BassE = input.Frame.Bass = bass(t);
            input.Frame.LoudVU = 0.5f;
            input.Beat = beat ?? BeatState.None;
            input.Events.Clear();
            while (next < events.Count && events[next].T <= t) input.Events.Add(events[next++]);
            r.Render(input, output);
            float b = 0;
            foreach (var c in r.LastLight) b = Math.Max(b, c.MaxChannel);
            tr.T.Add(t);
            tr.B.Add(b);
            last = t;
        }
        return tr;
    }

    /// <summary>120 BPM（1 拍 0.5 秒）。1 秒が 0 拍目 = 小節の頭</summary>
    private static BeatState Locked120 => new(Period, T0, 0, 1.0, true, 0);

    /// <summary>8 分音符ごとのベース（0.25 秒ごとに 0.9 まで上がって 0.08 秒で下がる）</summary>
    private static float EighthBass(double t)
    {
        double x = (t - T0) % 0.25;
        return (float)(0.9 * Math.Exp(-x / 0.08));
    }

    private static double Nearest(IEnumerable<double> xs, double t) => xs.Select(x => Math.Abs(x - t)).DefaultIfEmpty(double.PositiveInfinity).Min();

    /// <summary>
    /// テンポをつかんでいるとき: 光り始めるのは予測した拍の頭だけ。拍の間のキック（裏拍）・8 分のベースでは光らない。
    /// 拍はどれも光る
    /// </summary>
    public static void TestLockedFlashesOnlyOnBeats()
    {
        var events = new List<AudioEvent>();
        int id = 1;
        for (int k = 0; k < 13; k++)
        {
            double tb = T0 + k * Period;
            if (k % 4 != 2) events.Add(new AudioEvent(id++, AudioEventKind.Kick, tb + 0.01, 3f));   // 拍のキック（少し遅れて検出）
            events.Add(new AudioEvent(id++, AudioEventKind.Kick, tb + 0.25, 3f));                   // 裏拍のキック
        }
        var beats = Enumerable.Range(0, 13).Select(k => T0 + k * Period).ToList();
        foreach (var style in new[] { MusicStyles.BeatHue })
        {
            var tr = Run(style, Locked120, EighthBass, events);
            var r = tr.Rises(2.0);
            var off = r.Where(t => Nearest(beats, t) > 0.03).ToList();
            T.Check(r.Count > 0 && off.Count == 0, $"{style} locked: every flash starts on a predicted beat ({r.Count} flashes, {off.Count} off the beat: {string.Join(" ", off.Take(4).Select(t => t.ToString("F2")))})");
            var lit = beats.Where(b => b >= 2.0 && b <= 6.5).ToList();
            int missed = lit.Count(b => Nearest(r, b) > 0.03);
            T.Check(missed == 0, $"{style} locked: every beat flashes ({lit.Count - missed}/{lit.Count})");
        }
    }

    /// <summary>
    /// テンポをつかんでいるとき: 拍の光の強さは、その拍で鳴った音で決まる（キック > スネアだけ > 何もない）。小節の頭だからとは強くしない
    /// （6.15.10 までは、何もない拍もキックの 6 割ほどで光り、小節の頭がいちばん明るかった。小節の頭の見当は外れやすい）
    /// </summary>
    public static void TestBeatStrengthFollowsTheHit()
    {
        // 小節の 1・2 拍目にキック、3 拍目にスネアだけ、4 拍目は何もない（少し遅れて検出）
        var events = new List<AudioEvent>();
        int id = 1;
        for (int k = 0; k < 19; k++)
        {
            double tb = T0 + k * Period + 0.01;
            if (k % 4 is 0 or 1) events.Add(new AudioEvent(id++, AudioEventKind.Kick, tb, 3f));
            else if (k % 4 == 2) events.Add(new AudioEvent(id++, AudioEventKind.Snare, tb, 3f));
        }
        (double Head, double Kick, double Snare, double None) Peaks()
        {
            var tr = Run(MusicStyles.BeatHue, Locked120, _ => 0.2f, events, end: 10);
            double Avg(int slot) => Enumerable.Range(8, 10).Where(k => k % 4 == slot).Average(k => tr.PeakAfter(T0 + k * Period));
            return (Avg(0), Avg(1), Avg(2), Avg(3));
        }
        var (head, kick2, snare, none) = Peaks();
        BeatFlash.Tune.HitDriven = false;
        (double Head, double Kick, double Snare, double None) old;
        try { old = Peaks(); }
        finally { BeatFlash.Tune.HitDriven = true; }
        T.Info($"BeatHue peaks: kick on the bar head {head:F2}, kick on beat 2 {kick2:F2}, snare only {snare:F2}, nothing {none:F2} " +
               $"(6.15.10: {old.Head:F2}, {old.Kick:F2}, {old.Snare:F2}, {old.None:F2})");
        T.Check(Math.Abs(head - kick2) <= 0.03, $"the bar head is not brighter than another kick beat (the bar position is often wrong) ({head:F2} vs {kick2:F2})");
        T.Check(snare <= kick2 - 0.1 && snare >= none + 0.05, $"a snare-only beat is weaker than a kick beat and brighter than an empty beat ({snare:F2}, kick {kick2:F2}, empty {none:F2})");
        T.Check(none <= 0.5 * kick2 && none <= old.None - 0.15, $"a beat with no hit stays dim ({none:F2} vs kick {kick2:F2}; 6.15.10 {old.None:F2})");
        T.Check(old.Head >= old.Kick + 0.03 && old.None >= 0.6 * old.Kick, $"(reference) 6.15.10: the bar head was brightest and an empty beat still flashed ({old.Head:F2}, {old.Kick:F2}, {old.None:F2})");
    }

    /// <summary>
    /// テンポをつかんでいても、ドラムのない間奏では拍で光り続けない（6.15.10 は鳴っている音がないのに同じ速さで点滅し続けた）。
    /// キックが戻れば、最初のキックからまた明るく光る
    /// </summary>
    public static void TestLockedBreakWithoutDrumsStaysDim()
    {
        var events = new List<AudioEvent>();
        int id = 1;
        for (int k = 0; k < 25; k++)
            if (k < 8 || k >= 16) events.Add(new AudioEvent(id++, AudioEventKind.Kick, T0 + k * Period + 0.01, 3f));
        (double Kick, double Break, double Back) Measure()
        {
            var tr = Run(MusicStyles.BeatHue, Locked120, _ => 0.2f, events, end: 13.5);
            double kick = Enumerable.Range(4, 4).Average(k => tr.PeakAfter(T0 + k * Period));
            // 間奏の 3 拍目から（はじめの 2 拍は、覚えていた強さで光り始めることがある）
            double brk = Enumerable.Range(10, 6).Max(k => tr.PeakAfter(T0 + k * Period));
            return (kick, brk, tr.PeakAfter(T0 + 16 * Period));
        }
        var now = Measure();
        BeatFlash.Tune.HitDriven = false;
        (double Kick, double Break, double Back) old;
        try { old = Measure(); }
        finally { BeatFlash.Tune.HitDriven = true; }
        T.Info($"locked break: kick beats {now.Kick:F2}, brightest break beat {now.Break:F2}, first kick back {now.Back:F2} (6.15.10: {old.Kick:F2}, {old.Break:F2}, {old.Back:F2})");
        T.Check(now.Break <= 0.5 * now.Kick && now.Break <= old.Break - 0.15, $"locked break without drums: the beats stay dim ({now.Break:F2}, kick beats {now.Kick:F2}; 6.15.10 {old.Break:F2})");
        T.Check(now.Back >= 0.9 * now.Kick, $"locked break: the first kick after the break is a full flash ({now.Back:F2} vs {now.Kick:F2})");
    }

    /// <summary>
    /// テンポをつかめていないとき: キックで光る（色を進めるキックは必ず）。直後（0.2 秒）の弱いキック・8 分のベースでは光らない
    /// </summary>
    public static void TestUnlockedFlashesOnKicksWithSpacing()
    {
        var events = new List<AudioEvent>();
        int id = 1;
        var kicks = new List<double>();
        for (int k = 0; k < 13; k++)
        {
            double tk = T0 + 0.1 + k * 0.5;
            kicks.Add(tk);
            events.Add(new AudioEvent(id++, AudioEventKind.Kick, tk, 3f));
            events.Add(new AudioEvent(id++, AudioEventKind.Kick, tk + 0.2, 2f));
        }
        foreach (var style in new[] { MusicStyles.BeatHue })
        {
            var tr = Run(style, null, EighthBass, events);
            var r = tr.Rises(2.0);
            var off = r.Where(t => Nearest(kicks, t) > 0.03).ToList();
            int missed = kicks.Where(t => t >= 2.0 && t <= 6.5).Count(t => Nearest(r, t) > 0.03);
            T.Check(r.Count > 0 && off.Count == 0 && missed == 0,
                $"{style} unlocked: flashes on each main kick only ({r.Count} flashes, {off.Count} elsewhere, {missed} kicks missed)");
        }
    }

    /// <summary>テンポもドラムもない（ドラムのない曲）: 今までどおり低音で光る（拍の光だけでは止まって見えるため）</summary>
    public static void TestDrumlessFallsBackToBass()
    {
        // 0.6 秒ごとの低音（ベースの音。キックの出来事はない）
        static float Notes(double t) => (float)(0.9 * Math.Exp(-((t - T0) % 0.6) / 0.12));
        foreach (var style in new[] { MusicStyles.BeatHue })
        {
            var tr = Run(style, null, Notes, Array.Empty<AudioEvent>());
            var notes = Enumerable.Range(0, 12).Select(k => T0 + k * 0.6).Where(t => t >= 3 && t <= 6.5).ToList();
            var r = tr.Rises(3.0, 0.03);
            int hit = notes.Count(t => Nearest(r, t) <= 0.04);
            T.Check(hit >= notes.Count - 1, $"{style} without drums or tempo: still pulses with the bass notes ({hit}/{notes.Count})");
        }
    }

    /// <summary>テンポを使わない設定（Off）では、「ビートで色替え」は今までの光り方と同じ値になる</summary>
    public static void TestTempoOffKeepsTheOldLook()
    {
        var a = new BeatHueEffect(followBeat: true);
        var b = new BeatHueEffect();
        var ca = Context();
        var cb = Context();
        a.Reset(ca);
        b.Reset(cb);
        var da = new LinColor[4];
        var db = new LinColor[4];
        bool same = true;
        for (int k = 0; k < 240; k++)
        {
            double t = T0 + k / Fps;
            foreach (var c in new[] { ca, cb })
            {
                c.TPrev = c.T;
                c.T = t;
                c.Dt = (float)(1 / Fps);
                c.Frame.T = t;
                c.Frame.BassE = EighthBass(t);
                c.Frame.TrebleE = (float)(0.3 + 0.2 * Math.Sin(t * 7));
            }
            a.Update(ca);
            b.Update(cb);
            a.Render(ca, new LedSegment(0, 4, false, false), da);
            b.Render(cb, new LedSegment(0, 4, false, false), db);
            for (int j = 0; j < 4; j++) same &= da[j] == db[j];
        }
        T.Check(same, "tempo mode Off: BeatHue draws exactly as before (bass and melody)");
        T.Check(a.FollowsBeat && !b.FollowsBeat, "only the BeatHue style itself follows the beat");

        static EffectContext Context()
        {
            var c = new EffectContext { Music = ResolvedMusic.Default with { TempoMode = TempoModes.Off }, Gamma = false };
            c.P = new float[4];
            c.LedCount = 4;
            c.T = T0;
            return c;
        }
    }

    /// <summary>
    /// 拍に乗せるのは光り方「ビートで色替え」の速いデバイス用。ほかの光り方の代わり・遅いデバイス用は今までどおり
    /// </summary>
    public static void TestOnlyTheStylesThemselvesFollowTheBeat()
    {
        T.Check(EffectCatalog.Create(EffectVariant.BeatHue) is BeatHueEffect { FollowsBeat: true }, "BeatHue: follows the beat");
        T.Check(EffectCatalog.Create(EffectVariant.BeatHueSlow) is BeatHueEffect { FollowsBeat: false }, "BeatHue (slow devices, also used for other styles there): unchanged");
        T.Check(EffectCatalog.Create(EffectVariant.PulseLoudSlow) is PulseEffect, "loudness pulse (meter on slow devices): a plain pulse, no beat flash");
    }

    /// <summary>
    /// 「激しい点滅を抑える」がオン: 速いテンポ（200 BPM）でも、強い光り始めは 1 秒に 3 回まで。
    /// 最後の守り（FlashGuard）を外しても、拍の光の点滅の制限（BeatFlash の FlashLimiter）だけで守れる
    /// </summary>
    public static void TestReduceFlashingLimitsFastBeats()
    {
        var beat = new BeatState(0.3, T0, 0, 1.0, true, 0);
        var events = Enumerable.Range(0, 25).Select(k => new AudioEvent(k + 1, AudioEventKind.Kick, T0 + k * 0.3 + 0.01, 3f)).ToList();
        bool saved = DeviceRenderer.NoFlashGuard;
        try
        {
            foreach (bool noGuard in new[] { false, true })
            {
                DeviceRenderer.NoFlashGuard = noGuard;
                foreach (var style in new[] { MusicStyles.BeatHue })
                {
                    var tr = Run(style, beat, _ => 0.2f, events, end: 7, tweak: m => m with { ReduceFlashing = true, FlashAmp = 1 });
                    int strong = tr.Rises(2.0, 0.15).Count;
                    T.Check(strong <= 3 * 5 + 1, $"{style}{(noGuard ? " (FlashGuard off)" : "")}: at most 3 strong flashes per second at 200 BPM with reduce flashing ({strong} in 5 s)");
                }
            }
        }
        finally { DeviceRenderer.NoFlashGuard = saved; }
    }

    /// <summary>
    /// 曲が変わった（AudioEngine.NewSong）: 覚えている前の曲のテンポを忘れ、拍の並びを確かめる（拍の並びを「前の曲のもの」とし、確かめる間はロックしていないことにする）。
    /// 同じ音が続くなら（拍が続いている曲がつながったアルバム・DJ ミックスなど）、続く 3 拍で確かめて、ロックも拍の並びもそのまま使う。
    /// BeatTracker.Reset（取り込み直しなど）では、覚えているテンポも忘れる
    /// </summary>
    public static void TestNewSongForgetsTempoMemory()
    {
        var mem = typeof(BeatTracker).GetField("_memPeriod", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var g = new SignalGen(48000, 16, seed: 120);
        g.Loop(120, 0.2, 16, hats: true);
        var e = new AudioEngine(48000);
        var run = new EngineRun(e).Feed(g.Buf, 0, 12);
        double before = (double)mem.GetValue(e.Tracker)!;
        var old = run.Beats[^1];
        int steers = e.Tracker.SteerCount;
        T.Check(before > 0 && old.Locked, $"120 BPM loop: locked and the tempo is remembered (memory {before:F1} frames)");
        e.NewSong();
        run.Feed(g.Buf, 12, 12.2);
        T.Check((double)mem.GetValue(e.Tracker)! == 0, "NewSong: the remembered tempo is forgotten");
        T.Check(e.Tracker.Lock == BeatLock.Unlocked && e.Tracker.GridStale && run.Beats[^1].Stale && !run.Beats[^1].Locked,
            $"NewSong: while the grid is checked, it is not trusted (lock {e.Tracker.Lock}, stale {e.Tracker.GridStale})");
        run.Feed(g.Buf, 12.2, 16);
        int from = run.Frames.FindIndex(f => f.T >= run.At(12.2));
        int relock = from < 0 ? -1 : run.Locks.FindIndex(from, l => l == BeatLock.Locked);
        double relockSec = relock < 0 ? double.NaN : run.Frames[relock].T - run.At(12);
        var now = run.Beats[^1];
        // 同じ並びのまま（拍の時刻が前の並びの延長から 20 ms 以内）
        double drift = (now.LastBeatT - old.LastBeatT) / old.Period;
        drift = Math.Abs(drift - Math.Round(drift)) * old.Period;
        T.Check(relock >= 0 && relockSec < 2.5 && !now.Stale && now.Locked && drift < 0.02 && e.Tracker.SteerCount == steers,
            $"same music after NewSong: the lock comes back on the same grid after the check ({relockSec:F1} s, grid moved {drift * 1000:F0} ms, stale {now.Stale})");
        e.Tracker.Reset();
        T.Check((double)mem.GetValue(e.Tracker)! == 0 && !e.Tracker.GridStale, "BeatTracker.Reset: the remembered tempo is forgotten too (and the grid is not stale)");
    }

    /// <summary>
    /// 曲が変わった（NewSong）とき、拍の並びがそのまま続いてロックし続けても、そのテンポを覚え直さない（ロックが一度外れるか、
    /// MemHoldSec 秒たつまで）。覚え直すと、曲が変わった合図の効き目がなくなる
    /// </summary>
    public static void TestNewSongDoesNotRememberOldTempoAgain()
    {
        var mem = typeof(BeatTracker).GetField("_memPeriod", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var g = new SignalGen(48000, 30, seed: 121);
        g.Loop(120, 0.2, 30, hats: true);
        var e = new AudioEngine(48000);
        var run = new EngineRun(e).Feed(g.Buf, 0, 12);
        T.Check((double)mem.GetValue(e.Tracker)! > 0 && run.Beats[^1].Locked, "120 BPM loop: locked and the tempo is remembered");
        e.NewSong();
        run.Feed(g.Buf, 12, 22);
        T.Check((double)mem.GetValue(e.Tracker)! == 0 && run.Beats[^1].Locked,
            $"NewSong: still locked 10 s later, but the tempo is not remembered again (memory {(double)mem.GetValue(e.Tracker)!:F1})");
        double saved = BeatTracker.Tune.MemHoldSec;
        try
        {
            BeatTracker.Tune.MemHoldSec = 3;
            e.NewSong();
            run.Feed(g.Buf, 22, 30);
            T.Check((double)mem.GetValue(e.Tracker)! > 0, "NewSong: after MemHoldSec, a lock that keeps going is remembered again");
        }
        finally { BeatTracker.Tune.MemHoldSec = saved; }
    }

    /// <summary>
    /// 曲名がちらついて NewSong が何度も届いても（0.5 秒ごと）、新しい曲のテンポでロックできる
    /// （前の知らせから SongMinGapSec 秒たっていなければ、探し直さない。探し直すたびに立ち上がりの記録を捨てると、テンポを求められなかった）
    /// </summary>
    public static void TestRepeatedNewSongStillLocks()
    {
        var g = new SignalGen(48000, 30, seed: 123);
        g.Loop(120, 0.2, 12, hats: true);
        g.Loop(96, 12.17, 30, hats: true);
        var e = new AudioEngine(48000);
        var run = new EngineRun(e).Feed(g.Buf, 0, 12.5);
        for (double t = 12.5; t < 24; t += 0.5)
        {
            e.NewSong();
            run.Feed(g.Buf, t, t + 0.5);
        }
        run.Feed(g.Buf, 24, 30);
        var end = run.Beats[^1];
        T.Check(end.Locked && !end.Stale && Math.Abs(end.Period / 0.625 - 1) < 0.03,
            $"NewSong every 0.5 s for 11.5 s: locked on the new song anyway ({60 / end.Period:F1} BPM, locked {end.Locked}, stale {end.Stale})");
    }

    /// <summary>
    /// 曲が変わった（SongChanged）あとに音が止まり、拍の並びを忘れた（無音 4 秒）: 公開している値も「前の曲の拍の並び」でなくなる
    /// </summary>
    public static void TestStaleClearsAfterSilence()
    {
        var g = new SignalGen(48000, 20, seed: 124);
        g.Loop(120, 0.2, 12, hats: true);
        var e = new AudioEngine(48000);
        var run = new EngineRun(e).Feed(g.Buf, 0, 12);
        e.Tracker.SongChanged();
        T.Check(e.Tracker.GridStale, "SongChanged while locked: the grid is the old song's");
        run.Feed(g.Buf, 12, 20);
        var b = run.Beats[^1];
        T.Check(!e.Tracker.GridStale && !b.Stale && !b.Locked && b.Confidence == 0,
            $"8 s of silence: the grid is forgotten and not published as stale (stale {b.Stale}, tracker {e.Tracker.GridStale})");
    }

    /// <summary>
    /// 遅いデバイス用の光り方（c.Slow）は、つかめていなくても予測した拍で光るが、拍の並びが前の曲のもの（GridStale）の間は光らない
    /// </summary>
    public static void TestSlowDeviceDoesNotFlashOnStaleGrid()
    {
        foreach (bool stale in new[] { false, true })
        {
            var d = new BeatDriver();
            var c = FlashContext(reduce: false);
            c.Slow = true;
            c.Beat = new BeatState(0.5, 1.0, 0, 0.8, false, 0) { Stale = stale };
            c.HasTempo = true;
            c.Locked = false;
            c.GridStale = stale;
            c.Conf = 0.8f;
            c.Period = 0.5;
            double v = d.FlashAt(c, 1.01);
            if (stale) T.Check(v == 0, $"slow device, stale grid: no flash on the predicted beat ({v:F2})");
            else T.Check(v > 0.5, $"slow device, unlocked grid of this song: flashes on the predicted beat ({v:F2})");
        }
    }

    /// <summary>
    /// 曲間なしで別のテンポの曲に変わり、少し遅れて NewSong が届いた: 前の曲の拍の並びは使わず（GridStale）、
    /// 新しい曲のテンポと拍の位置で又ロックする（前の曲の並びのまま光り続けない）
    /// </summary>
    public static void TestSongChangeLocksOnTheNewSong()
    {
        var g = new SignalGen(48000, 30, seed: 122);
        g.Loop(120, 0.2, 12, hats: true);
        // 次の曲: 96 BPM、拍の位置も前の並びの延長からずらす
        g.Loop(96, 12.17, 30, hats: true);
        var newKicks = g.KickTimes.Where(t => t >= 12.1).ToList();
        foreach (bool reverify in new[] { true, false })
        {
            bool saved = BeatTracker.Tune.SongReverify;
            try
            {
                BeatTracker.Tune.SongReverify = reverify;
                var e = new AudioEngine(48000);
                var run = new EngineRun(e).Feed(g.Buf, 0, 13);
                T.Check(!reverify || run.Beats[^1].Locked, "120 BPM: locked (still on the old grid 1 s after the change)");
                e.NewSong();
                run.Feed(g.Buf, 13, 13.1);
                if (reverify) T.Check(e.Tracker.Lock == BeatLock.Unlocked && run.Beats[^1].Stale && !run.Beats[^1].Locked, "NewSong: the old grid is not trusted any more (unlocked, stale)");
                run.Feed(g.Buf, 13.1, 30);
                // 新しい曲のテンポ（±3%）で、拍の頭がキックから 40 ms 以内でロックした最初の時刻
                double first = double.NaN;
                for (int i = 0; i < run.Frames.Count; i++)
                {
                    double s = run.Frames[i].T - run.T0;
                    var b = run.Beats[i];
                    if (s < 13 || run.Locks[i] != BeatLock.Locked || Math.Abs(b.Period / 0.625 - 1) > 0.03) continue;
                    double off = newKicks.Min(k => { double x = (run.At(k) - b.LastBeatT) / b.Period; return Math.Abs(x - Math.Round(x)) * b.Period; });
                    if (off < 0.04) { first = s - 12.17; break; }
                }
                var end = run.Beats[^1];
                bool onNew = Math.Abs(end.Period / 0.625 - 1) < 0.03 && end.Locked && !end.Stale;
                if (reverify)
                    T.Check(!double.IsNaN(first) && first < 9 && onNew,
                        $"after the change, locked on the new song's tempo and beat {first:F1} s after the change (end {60 / end.Period:F1} BPM, stale {end.Stale})");
                else T.Info($"SongReverify off: locked on the new song {first:F1} s after the change (end {60 / end.Period:F1} BPM)");
            }
            finally { BeatTracker.Tune.SongReverify = saved; }
        }
    }

    /// <summary>
    /// 拍の並びが前の曲のもの（GridStale）の間、つかめていないときの拍の見当では光らない。キックでは光る
    /// （曲が変わった直後に、前の曲のテンポの拍で光り続けて見えた）
    /// </summary>
    public static void TestStaleGridDoesNotFlash()
    {
        foreach (bool stale in new[] { false, true })
        {
            var bf = new BeatFlash();
            var c = FlashContext(reduce: false);
            c.GridStale = stale;
            Step(bf, c, 1.0, 1.1, false, evs: new AudioEvent(1, AudioEventKind.Kick, 1.05, 3f));
            Step(bf, c, 1.9, 2.0, false, cross: 1.98);
            bool grid = bf.BeatStarted;
            Step(bf, c, 2.4, 2.5, false, evs: new AudioEvent(2, AudioEventKind.Kick, 2.45, 3f));
            bool kick = bf.KickStarted;
            if (stale) T.Check(!grid && kick, $"stale grid: no flash on the predicted beat (grid {grid}), the kick still flashes ({kick})");
            else T.Check(grid && kick, $"grid of this song: the predicted beat makes a weak flash ({grid}) and the kick flashes ({kick})");
        }
    }

    /// <summary>
    /// テンポの見当はあるがつかめていないとき、ドラムが止まってパッドだけになったら、拍の見当で光り続けない
    /// （鳴っている音がないのに、決まった速さで点滅し続けて見えた）。ドラムが鳴っている間は光る
    /// </summary>
    public static void TestUnlockedGridStopsWithoutDrums()
    {
        var beat = new BeatState(Period, T0, 0, 0.6, false, 0);
        // 1〜3.5 秒はキック（拍の頭）、そのあとはドラムなし。低音はパッドの一定の音
        var events = Enumerable.Range(0, 6).Select(k => new AudioEvent(k + 1, AudioEventKind.Kick, T0 + k * Period + 0.01, 3f)).ToList();
        foreach (var style in new[] { MusicStyles.BeatHue })
        {
            var tr = Run(style, beat, _ => 0.5f, events, end: 9);
            int during = tr.Rises(1.4).Count(t => t < 3.6);
            int after = tr.Rises(3.5 + 2.0).Count;
            T.Check(during >= 3, $"{style} unlocked: flashes while the drums play ({during})");
            T.Check(after == 0, $"{style} unlocked: no beat-grid flashes from 2 s after the drums stop ({after} in 3.5 s)");
        }
    }

    // ---- BeatFlash を直接動かす確かめ（1 コマずつ、出来事と拍の頭を手で渡す）

    private static EffectContext FlashContext(bool reduce) => new()
    {
        Music = ResolvedMusic.Default with { TempoMode = TempoModes.Auto, ReduceFlashing = reduce, FlashAmp = 1 },
        Gamma = false,
        // 音は鳴っている（デジタルの無音の中の予測の拍では光らないため）
        Frame = new AudioFrame { RmsDb = -20 },
    };

    /// <summary>1 コマ分: 前のコマの時刻 prev から t まで。cross = またいだ拍の頭の時刻、trigger = 色を進める合図の時刻</summary>
    private static void Step(BeatFlash bf, EffectContext c, double prev, double t, bool locked, double? cross = null, double? trigger = null, params AudioEvent[] evs)
    {
        c.TPrev = prev;
        c.T = t;
        c.Dt = (float)Math.Max(0, t - prev);
        c.Beat = new BeatState(Period, T0, 0, 1.0, locked, 0);
        c.HasTempo = true;
        c.Locked = locked;
        c.Period = Period;
        c.Conf = 1;
        c.BeatCrossed = cross.HasValue;
        c.BeatCrossT = cross ?? t;
        c.Trigger = trigger.HasValue;
        c.TriggerT = trigger ?? t;
        c.Events.Clear();
        c.Events.AddRange(evs);
        bf.Update(c);
    }

    /// <summary>
    /// 拍の頭と出来事は時刻の順に処理する: 1 コマ（10 回／秒のデバイス）の中で、拍の頭より前に色を進めるキックがあれば、
    /// そのキックで光る（以前は先に拍の頭で光らせたので、キックの光が始められず、色だけが変わっていた）。
    /// 今の光より前の時刻に遅れて届いた色を進めるキックも、1 つ前の光として入れる
    /// </summary>
    public static void TestTriggerKickBeforeGridInSameFrameFlashes()
    {
        var bf = new BeatFlash();
        var c = FlashContext(reduce: true);
        Step(bf, c, 1.0, 1.1, false, evs: new AudioEvent(1, AudioEventKind.Kick, 1.05, 3f));
        Step(bf, c, 1.8, 1.9, false);
        Step(bf, c, 1.9, 2.0, false, cross: 1.98, trigger: 1.88, evs: new AudioEvent(2, AudioEventKind.Kick, 1.88, 3f));
        // 光はキックの時刻（1.88）から始まる: その直前は前の光の残りだけ、直後は光り始めの明るさ
        double before = bf.At(1.88 - 1e-4), after = bf.At(1.88 + 1e-6);
        T.Check(bf.KickStarted && after > 0.3 && after > before + 0.2,
            $"a colour-trigger kick before the beat head in the same frame starts a flash at the kick ({before:F2} → {after:F2} at 1.88)");
        T.Check(!bf.BeatStarted, "the beat head right after that kick does not flash again");
        T.Check(bf.Limiter.Accepted == 2, $"the flash limiter counts only flashes that really start ({bf.Limiter.Accepted} accepted)");

        var b2 = new BeatFlash();
        var c2 = FlashContext(reduce: false);
        Step(b2, c2, 1.0, 1.1, false, evs: new AudioEvent(1, AudioEventKind.Kick, 1.05, 3f));
        Step(b2, c2, 1.9, 2.0, false, cross: 1.98);
        T.Check(b2.BeatStarted, "unlocked with drums playing: the predicted beat makes a weak flash");
        Step(b2, c2, 2.0, 2.1, false, trigger: 1.85, evs: new AudioEvent(2, AudioEventKind.Kick, 1.85, 3f));
        T.Check(b2.KickStarted && b2.At(1.86) > 0.3, $"a colour-trigger kick that arrives late (before the current flash) is inserted as the older flash ({b2.At(1.86):F2} at its time)");
    }

    /// <summary>
    /// 時刻が戻った（音の遅れの設定を変えた）: 光の時刻も同じだけ戻すので、残光が続き、次の拍でもすぐに光る
    /// （以前は光の時刻が未来に残り、追いつくまで真っ暗だった）
    /// </summary>
    public static void TestTimeGoingBackKeepsTheFlash()
    {
        var bf = new BeatFlash();
        var c = FlashContext(reduce: false);
        double prev = T0;
        int id = 1;
        for (double t = T0 + 1 / Fps; t <= 3.05; t += 1 / Fps)
        {
            double? cross = Math.Floor((t - T0) / Period) > Math.Floor((prev - T0) / Period) ? T0 + Math.Floor((t - T0) / Period) * Period : null;
            var evs = cross is double tb ? new[] { new AudioEvent(id++, AudioEventKind.Kick, tb, 3f) } : Array.Empty<AudioEvent>();
            Step(bf, c, prev, t, true, cross, evs: evs);
            prev = t;
        }
        // 0.4 秒戻る（DeviceRenderer は前のコマの時刻も今の時刻にそろえる）
        double back = prev - 0.4;
        Step(bf, c, back, back, true);
        T.Check(bf.Value > 0.6, $"after time goes back 0.4 s, the afterglow continues ({bf.Value:F2})");
        prev = back;
        int beats = 0;
        for (double t = back + 1 / Fps; t <= back + 1.2; t += 1 / Fps)
        {
            double? cross = Math.Floor((t - T0) / Period) > Math.Floor((prev - T0) / Period) ? T0 + Math.Floor((t - T0) / Period) * Period : null;
            // 戻ったあともキックは鳴り続ける（キックのない拍は弱く光るだけなので）
            var evs = cross is double tb ? new[] { new AudioEvent(id++, AudioEventKind.Kick, tb, 3f) } : Array.Empty<AudioEvent>();
            Step(bf, c, prev, t, true, cross, evs: evs);
            if (bf.BeatStarted && bf.Value > 0.3) beats++;
            prev = t;
        }
        T.Check(beats >= 2, $"after time goes back, the next beats flash right away ({beats} in 1.2 s)");
    }
}
