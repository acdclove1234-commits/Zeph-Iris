// ==================================================
// InstrumentFeatureChecks.cs - 楽器ごとの手がかりの判定つきテスト (ベースの音をキックと数えない・キックは今までどおり・
//                              打楽器と持続音・左右・12 音の色・ベースの高さと出だし・楽器らしさ・計算の重さ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class InstrumentFeatureTests
{
    private static AudioEngine NewEngine(int fs = 48000) => new(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };

    /// <summary>[from, to) 秒の、指定した種類の出来事の 1 秒あたりの数</summary>
    private static double Rate(List<AudioEvent> evs, double t0, AudioEventKind k, double from, double to) =>
        evs.Count(e => e.Kind == k && e.T - t0 >= from && e.T - t0 < to) / (to - from);

    /// <summary>ドラムのないベースの音（アルペジオ・ベースライン）では、キックが 1 秒に 0.5 回未満（6.15.7 では 1 秒に 3〜4 回）</summary>
    public static void TestBassNotesAreNotKicks()
    {
        var cases = new List<(string Name, SignalGen G)>();
        foreach (var (rate, reg, n) in new[] { (8.0, 0.125, "bass 8/s"), (4.0, 0.125, "bass 4/s"), (6.0, 0.25, "low 6/s"), (8.0, 0.25, "low 8/s") })
            cases.Add(("arpeggio " + n, MusicLight.Arpeggio(rate, 16, reg).G));
        {
            var g = new SignalGen(48000, 16, seed: 4);
            g.BassLine(0.2, 16, 4, InstrumentSources.BassNotes);
            cases.Add(("bass line 4/s", g));
        }
        {
            var g = new SignalGen(48000, 16, seed: 9);
            g.BassLine(0.2, 16, 8, InstrumentSources.BassNotes, 0.4);
            g.Pink(-46, 0.2, 16);
            cases.Add(("bass line 8/s + noise", g));
        }
        {
            // ベースの音とハイハットが同時に鳴る（ハイハットの高い音を「カチッ」と取り違えない）
            var g = new SignalGen(48000, 16, seed: 7);
            g.BassLine(0.2, 16, 4, InstrumentSources.BassNotes);
            for (double t = 0.2; t < 16; t += 0.25) g.Hat(t, 0.06);
            cases.Add(("bass line + hats together", g));
        }
        bool saved = OnsetDetector.KickTune.PercGate;
        try
        {
            foreach (var (name, g) in cases)
            {
                OnsetDetector.KickTune.PercGate = false;
                var off = new EngineRun(NewEngine()).Feed(g.Buf);
                OnsetDetector.KickTune.PercGate = true;
                var on = new EngineRun(NewEngine()).Feed(g.Buf);
                double before = Rate(off.Events, off.T0, AudioEventKind.Kick, 2, 16), after = Rate(on.Events, on.T0, AudioEventKind.Kick, 2, 16);
                T.Check(after < 0.5, $"{name}: false kicks {before:F1}/s without the percussiveness gate -> {after:F2}/s with it (< 0.5)");
                if (name.StartsWith("arpeggio bass")) T.Check(before >= 2, $"{name}: the old way counts the bass notes as kicks ({before:F1}/s), so the test measures the gate");
            }
        }
        finally { OnsetDetector.KickTune.PercGate = saved; }
    }

    /// <summary>キックは今までどおり見つかる（合成した 10 曲・音の多い曲・ベースライン入り・「カチッ」のないキック）。時刻も変わらない</summary>
    public static void TestKickRecallUnchanged()
    {
        var songs = MusicBench.Cases().Select(c => (c.Name, c.Gen)).ToList();
        {
            var g = new SignalGen(48000, 16, seed: 3);
            g.DenseMix(124, 0.2, 16);
            songs.Add(("dense mix 124", g));
        }
        {
            var g = new SignalGen(48000, 16, seed: 5);
            g.BassLine(0.2, 16, 8, InstrumentSources.BassNotes);
            g.Loop(120, 0.2, 16, hats: true);
            songs.Add(("bass line 8/s + drum loop", g));
        }
        bool saved = OnsetDetector.KickTune.PercGate;
        int totalOff = 0, totalOn = 0, totalKicks = 0;
        try
        {
            foreach (var (name, g) in songs)
            {
                var kicks = g.KickTimes.Where(t => t > 2).ToList();
                (int Hit, double ErrMs) Measure(bool gate)
                {
                    OnsetDetector.KickTune.PercGate = gate;
                    var run = new EngineRun(NewEngine()).Feed(g.Buf);
                    var ks = run.Kind(AudioEventKind.Kick).Select(e => e.T - run.T0).ToList();
                    var errs = kicks.Select(t => ks.Where(k => Math.Abs(k - t) < 0.03).Select(k => k - t).DefaultIfEmpty(double.NaN).First()).Where(double.IsFinite).ToList();
                    return (errs.Count, errs.Count > 0 ? errs.Average(Math.Abs) * 1000 : 0);
                }
                var off = Measure(false);
                var on = Measure(true);
                totalOff += off.Hit;
                totalOn += on.Hit;
                totalKicks += kicks.Count;
                T.Check(on.Hit >= off.Hit && Math.Abs(on.ErrMs - off.ErrMs) < 0.5,
                    $"{name}: kicks found {off.Hit}/{kicks.Count} -> {on.Hit}/{kicks.Count}, time error {off.ErrMs:F1} -> {on.ErrMs:F1} ms");
            }
        }
        finally { OnsetDetector.KickTune.PercGate = saved; }
        T.Info($"all songs: {totalOff} -> {totalOn} of {totalKicks} kicks");

        // 「カチッ」のないキック（808 のような音）も、ほとんど見つかる（いつもの大きさより目立つので）
        var c = new SignalGen(48000, 16, seed: 6);
        for (double t = 0.2; t < 16; t += 0.5) c.Kick(t, 0.8, click: false);
        MusicBench.Backing(c, 120, 0.2, 16);
        var rc = new EngineRun(NewEngine()).Feed(c.Buf);
        var kc = c.KickTimes.Where(t => t > 2).ToList();
        int hit = kc.Count(t => rc.Kind(AudioEventKind.Kick).Any(e => Math.Abs(e.T - rc.T0 - t) < 0.03));
        T.Check(hit >= 0.85 * kc.Count, $"kicks without a click: {hit}/{kc.Count} found (≥ 85%)");
    }

    /// <summary>打楽器らしさ: 「カチッ」があればキック、なくても目立つ低音ならキック、どちらもなければベースの音</summary>
    public static void TestPercussivenessScore()
    {
        T.Near(OnsetDetector.Percussiveness(10, -20, 2), 1, 1e-9, "a clear click (10 dB rise, 20 dB below the kick)");
        T.Near(OnsetDetector.Percussiveness(1, -20, 2), 0, 1e-9, "no rise in the click band and not prominent: a bass note");
        T.Near(OnsetDetector.Percussiveness(10, -45, 2), 0, 1e-9, "a rise far too small for a kick click (bass note under a hi-hat)");
        T.Near(OnsetDetector.Percussiveness(1, -45, 12), 1, 1e-9, "no click but 12 dB above the usual low level: a kick without a click");
        T.Near(OnsetDetector.Percussiveness(3, -31.5, 8), 0.5, 1e-9, "half way on every cue");
        T.Check(double.IsNaN(OnsetDetector.Percussiveness(double.NaN, double.NaN, double.NaN)), "nothing known: NaN (the gate does not apply)");
        T.Near(OnsetDetector.Percussiveness(double.NaN, -20, 12), 1, 1e-9, "only the prominence known");
    }

    /// <summary>キックの包絡線: 打楽器らしさを確かめる前（commit = false）は、ベースの音を「最近のキックの大きさ」として覚えない</summary>
    public static void TestKickReferenceOnlyAfterTheGate()
    {
        foreach (bool commit in new[] { false, true })
        {
            var g = new SignalGen(48000, 2, seed: 2);
            g.Tone(60, 0.9, 0.5, 0.7);   // 大きいベースの音
            g.Kick(1.2, 0.2);            // 15 dB ほど小さいキック
            var kp = new KickPath(48000);
            bool second = false;
            for (int i = 0; i < g.Buf.Length; i++)
            {
                kp.Sample(g.Buf[i]);
                if (i == (int)(0.52 * 48000)) kp.TryFindOnset(OnsetDetector.KickMinRiseDb, OnsetDetector.KickMaxBelowRecentDb, commit, out _, out _);
                if (i == (int)(1.215 * 48000)) second = kp.TryFindOnset(OnsetDetector.KickMinRiseDb, OnsetDetector.KickMaxBelowRecentDb, out _, out _);
            }
            if (commit) T.Check(!second, "committed loud bass note: the much smaller kick is then rejected (as before)");
            else T.Check(second, "not committed: the smaller kick after a loud bass note is still found");
        }
    }

    /// <summary>
    /// 1〜5 kHz の包絡線の上がり（RiseAt）とキックの包絡線との大きさの差: キックの「カチッ」では「カチッ」の点が 1、
    /// カチッのないキック・ベースの音（静かな背景で、はじく音が少し高い帯に漏れる）では 0
    /// </summary>
    public static void TestClickRiseAtKickOnset()
    {
        foreach (var (name, make) in new (string, Action<SignalGen>)[]
        {
            ("kick with a click", g => g.Kick(0.5, 0.7)),
            ("kick without a click", g => g.Kick(0.5, 0.7, click: false)),
            ("bass note", g => g.BassLine(0.5, 0.9, 2, new[] { 55.0 }, 0.5)),
        })
        {
            var g = new SignalGen(48000, 1, seed: 3);
            g.Pink(-50, 0, 1);
            make(g);
            var tp = TransientPath.ForSnare(48000);
            var kp = new KickPath(48000);
            int end = (int)(0.515 * 48000);
            for (int i = 0; i < end; i++)
            {
                tp.Sample(g.Buf[i]);
                kp.Sample(g.Buf[i]);
            }
            bool found = kp.TryFindOnset(OnsetDetector.KickMinRiseDb, OnsetDetector.KickMaxBelowRecentDb, false, out int ago, out _);
            double rise = tp.RiseAt(ago, 0.010, 0.006, out double peakDb);
            double rel = peakDb - kp.FoundPeakDb;
            double click = OnsetDetector.Percussiveness(rise, rel, double.NaN);
            T.Info($"{name}: 1-5 kHz rise {rise:F1} dB, {rel:F1} dB from the kick envelope, click score {click:F2}");
            bool want = name == "kick with a click";
            T.Check(found && (want ? click >= 0.99 : click <= 0.01), $"{name}: click score {click:F2} ({(want ? "1" : "0")})");
        }
    }

    /// <summary>打楽器と持続音: ドラムだけなら打楽器の分が、パッドだけなら持続音の分が大きい</summary>
    public static void TestPercussiveAndHarmonicParts()
    {
        var d = new SignalGen(48000, 8, seed: 11);
        d.Loop(120, 0.2, 8, hats: true, snare: true);
        var rd = new StereoRun(NewEngine()).Feed(d.Buf);
        var (_, pl, pr, _) = InstrumentSources.Make("pad", 8);
        var rp = new StereoRun(NewEngine()).Feed(pl, pr);
        double dP = rd.Mean(2, 8, f => f.PercMid + f.PercHigh), dH = rd.Mean(2, 8, f => f.HarmMid + f.HarmHigh);
        double pP = rp.Mean(2, 8, f => f.PercMid + f.PercHigh), pH = rp.Mean(2, 8, f => f.HarmMid + f.HarmHigh);
        T.Info($"drums: percussive mid+high {dP:F2}, harmonic {dH:F2}; pad: percussive {pP:F2}, harmonic {pH:F2}");
        T.Check(dP > dH + 0.2, "drum loop: the percussive part is larger than the harmonic part");
        T.Check(pH > pP + 0.2, "pad: the harmonic part is larger than the percussive part");
        double hatP = rd.Mean(2, 8, f => f.PercHigh), padHigh = rp.Mean(2, 8, f => f.PercHigh);
        T.Check(hatP > padHigh + 0.2, $"hats show up as percussive high ({hatP:F2} vs pad {padHigh:F2})");
    }

    /// <summary>左右: 真ん中の音は広がり 0・左右で違う音は広い・片方だけの音も広い。モノラルで流せば横は 0</summary>
    public static void TestStereoWidth()
    {
        var (_, vl, vr, _) = InstrumentSources.Make("vocal", 4);
        var (_, pl, pr, _) = InstrumentSources.Make("pad", 4);
        var centre = new StereoRun(NewEngine()).Feed(vl, vr);
        var wide = new StereoRun(NewEngine()).Feed(pl, pr);
        var left = new StereoRun(NewEngine()).Feed(vl, new float[vl.Length]);
        var mono = new StereoRun(NewEngine()).Feed(pl);
        double wc = centre.Mean(1.5, 4, f => f.WidthMid), ww = wide.Mean(1.5, 4, f => f.WidthMid), wl = left.Mean(1.5, 4, f => f.WidthMid), wm = mono.Mean(1.5, 4, f => f.WidthMid);
        T.Info($"width mid: centred {wc:F2}, wide pad {ww:F2}, left only {wl:F2}, mono push {wm:F2}");
        T.Check(wc < 0.05, "centred voice: no width");
        T.Check(ww > 0.6, "decorrelated pad: wide");
        T.Check(wl > 0.9, "left channel only: fully to the side");
        T.Check(wm == 0, "mono push: width 0");
        T.Check(centre.Mean(1.5, 4, f => f.Centre) > centre.Mean(1.5, 4, f => f.Side) + 0.3, "centred voice: centre level above side level");
        T.Check(wide.Mean(1.5, 4, f => f.Side) > 0.5, "wide pad: side level is high");
    }

    /// <summary>12 音の色: パッドの和音（ド・ミ・ソ → ラ・ド・ミ）の 3 音が、いちばん強い 3 つの音名になる</summary>
    public static void TestChromaFollowsChords()
    {
        var (_, pl, pr, _) = InstrumentSources.Make("pad", 8);
        var run = new StereoRun(NewEngine()).Feed(pl, pr);
        foreach (var (from, to, chord, name) in new[] { (1.0, 2.0, new[] { 0, 4, 7 }, "C major"), (3.0, 4.0, new[] { 0, 4, 9 }, "A minor"), (5.0, 6.0, new[] { 0, 4, 7 }, "C major again") })
        {
            var fr = run.Between(from, to).ToList();
            var top = Enumerable.Range(0, 12).OrderByDescending(k => fr.Average(x => x.Chroma[k])).Take(3).OrderBy(k => k).ToArray();
            T.Check(top.SequenceEqual(chord), $"{name}: strongest pitch classes {string.Join(",", top)} = {string.Join(",", chord)}");
            T.Check(fr.Count(x => chord.Contains(x.PitchClass)) >= 0.9 * fr.Count, $"{name}: the dominant pitch class is a chord tone");
        }
    }

    /// <summary>ベース: 音の高さ（YIN）が合う・音の出だしを 40 ミリ秒以内に見つける・ドラムだけではベースの出だしにならない</summary>
    public static void TestBassPitchAndOnsets()
    {
        var (_, l, r, g) = InstrumentSources.Make("bass", 12);
        var run = new StereoRun(NewEngine()).Feed(l, r);
        int ok = 0, n = 0;
        for (int i = 0; i < g.BassNoteTimes.Count; i++)
        {
            foreach (var fr in run.Between(g.BassNoteTimes[i] + 0.06, g.BassNoteTimes[i] + 0.2))
            {
                n++;
                if (fr.BassF0Hz > 0 && Math.Abs(12 * Math.Log2(fr.BassF0Hz / g.BassNoteHz[i])) < 0.5) ok++;
            }
        }
        T.Check(ok >= 0.9 * n, $"bass pitch right in {ok}/{n} frames (≥ 90%)");
        var voiced = run.Frames.Where(f => f.BassF0Hz > 0).ToList();
        int pcOk = voiced.Count(f => f.BassPitchClass == (((int)Math.Round(12 * Math.Log2(f.BassF0Hz / 440)) + 9) % 12 + 12) % 12);
        T.Check(voiced.Count > 0 && pcOk == voiced.Count, "bass pitch class matches the pitch");

        static List<double> Onsets(StereoRun rr)
        {
            var o = new List<double>();
            for (int i = 1; i < rr.Frames.Count; i++) if (rr.Frames[i].BassOnset > rr.Frames[i - 1].BassOnset + 0.15) o.Add(rr.Frames[i].T - rr.T0);
            return o;
        }
        var ons = Onsets(run);
        var notes = g.BassNoteTimes.Where(t => t > 1).ToList();
        var lags = notes.Select(t => ons.Where(o => o - t > -0.01 && o - t < 0.08).DefaultIfEmpty(double.NaN).First() - t).Where(double.IsFinite).OrderBy(x => x).ToList();
        double p50 = lags.Count > 0 ? lags[lags.Count / 2] * 1000 : double.NaN;
        T.Check(lags.Count >= 0.85 * notes.Count && p50 <= 40, $"bass note onsets: {lags.Count}/{notes.Count} found (≥ 85%), lag p50 {p50:F0} ms (≤ 40)");
        int onsLate = ons.Count(t => t > 1);
        T.Check(onsLate <= notes.Count + 2, $"no extra bass onsets ({onsLate} for {notes.Count} notes)");

        var d = new SignalGen(48000, 12, seed: 8);
        d.Loop(120, 0.2, 12, hats: true, snare: true);
        var rd = new StereoRun(NewEngine()).Feed(d.Buf);
        double falseRate = Onsets(rd).Count(t => t > 1) / 11.0;
        double drumLevel = rd.Mean(2, 12, f => f.BassLevel), bassLevel = run.Mean(2, 12, f => f.BassLevel);
        T.Check(falseRate <= 0.3, $"drum loop only: {falseRate:F2} bass onsets/s (≤ 0.3)");
        T.Check(drumLevel <= 0.6 * bassLevel, $"bass level: drum loop {drumLevel:F2} ≤ 60% of the bass line {bassLevel:F2} (the kick's boom is not counted as bass)");
    }

    /// <summary>
    /// 楽器らしさ: それぞれの合成音（声に似た音・はじいた和音・パッド・金管に似た音）で、自分のらしさがいちばん大きく、次の 2 倍以上。
    /// ベースだけでは、どのらしさもほとんど出ない（ベースの大きさだけが出る）
    /// </summary>
    public static void TestTexturesFollowTheirSource()
    {
        var pick = new Dictionary<string, Func<AudioFrame, double>>
        {
            ["vocal"] = f => f.Vocal,
            ["guitar"] = f => f.GuitarKeys,
            ["pad"] = f => f.Pad,
            ["brass"] = f => f.Brass,
        };
        foreach (var name in InstrumentSources.Names)
        {
            var (_, l, r, _) = InstrumentSources.Make(name);
            var run = new StereoRun(NewEngine()).Feed(l, r);
            var m = pick.ToDictionary(kv => kv.Key, kv => run.Mean(2, 8, kv.Value));
            string all = string.Join(" ", m.Select(kv => $"{kv.Key} {kv.Value:F2}"));
            if (name == "bass")
            {
                double lvl = run.Mean(2, 8, f => f.BassLevel);
                T.Check(m.Values.All(v => v <= 0.1) && lvl >= 0.7, $"bass line: textures stay low ({all}), bass level {lvl:F2} ≥ 0.70");
                continue;
            }
            double own = m[name], next = m.Where(kv => kv.Key != name).Max(kv => kv.Value);
            T.Check(own >= 0.3 && own >= 2 * next, $"{name}: own texture {own:F2} ≥ 0.30 and ≥ 2× the next ({next:F2}) [{all}]");
        }
    }

    /// <summary>ビブラートの下限の確かめ用: 512 個ごとの時刻（先頭からの秒）・下限・揺れ（セント）・ボーカルらしさ・ギター鍵盤らしさ</summary>
    private sealed record VibRun(List<(double T, double Floor, double Vib, double Vocal, double Guitar)> Hops)
    {
        public double MeanVocal(double from, double to) => Hops.Where(h => h.T >= from && h.T < to).Average(h => h.Vocal);
        public double MeanGuitar(double from, double to) => Hops.Where(h => h.T >= from && h.T < to).Average(h => h.Guitar);
    }

    /// <summary>左・右の音を 512 個ずつ流し、下限・揺れ・らしさを集める（track = 下限を追う。オフなら 6 セントのまま）</summary>
    private static VibRun RunVib(float[] l, float[] r, bool track)
    {
        bool saved = InstrumentFeatures.TrackVibFloor;
        InstrumentFeatures.TrackVibFloor = track;
        try
        {
            var e = NewEngine();
            var mid = new float[512];
            var side = new float[512];
            var hops = new List<(double T, double Floor, double Vib, double Vocal, double Guitar)>();
            for (int i = 0; i + 512 <= l.Length; i += 512)
            {
                for (int k = 0; k < 512; k++) { mid[k] = 0.5f * (l[i + k] + r[i + k]); side[k] = 0.5f * (l[i + k] - r[i + k]); }
                e.Push(mid, side, 1000 + i / 48000.0);
                var ins = e.Instruments;
                hops.Add(((i + 512) / 48000.0, ins.DiagVibFloor, ins.DiagVib, ins.Vocal, ins.GuitarKeys));
            }
            return new VibRun(hops);
        }
        finally { InstrumentFeatures.TrackVibFloor = saved; }
    }

    /// <summary>
    /// 左右に広い、音の多い合成の曲（ドラム・ベース・和音の DenseMix は左右で別のノイズ、パッドは左右で別の揺れ、ギターは左寄り）。
    /// vocal なら、voFrom〜voTo 秒に真ん中の声（±40 セントのビブラート）を重ねる
    /// </summary>
    private static (float[] L, float[] R) WideMix(double sec, double padDetune, bool vocal, double voFrom = 6, double voTo = 14, double guitarAmp = 0.08, double padAmp = 0.05, bool drums = true)
    {
        var l = new SignalGen(48000, sec, seed: 81);
        var r = new SignalGen(48000, sec, seed: 82);
        if (drums)
        {
            l.DenseMix(118, 0.2, sec);
            r.DenseMix(118, 0.2, sec);
        }
        for (double t = 0.2; t < sec - 0.1; t += 2)
        {
            var c = ((int)(t / 2)) % 2 == 0 ? InstrumentSources.PadChord1 : InstrumentSources.PadChord2;
            l.PadChord(t, Math.Min(sec, t + 2.3), c, padAmp, padDetune, seed: 61 + (int)t);
            r.PadChord(t, Math.Min(sec, t + 2.3), c, padAmp, padDetune, seed: 71 + (int)t);
        }
        for (double t = 0.2; t < sec - 0.5; t += 0.5)
        {
            var c = ((int)(t * 2)) % 4 < 2 ? new[] { 196.0, 246.9, 293.7, 392 } : new[] { 220.0, 261.6, 329.6, 440 };
            l.Strum(t, c, guitarAmp);
            r.Strum(t, c, guitarAmp * 0.35);
        }
        if (vocal)
        {
            l.VocalTone(voFrom, voTo, InstrumentSources.VocalNotes, 0.6);
            r.VocalTone(voFrom, voTo, InstrumentSources.VocalNotes, 0.6);
        }
        return (l.Buf, r.Buf);
    }

    /// <summary>
    /// ビブラートの下限は、鳴っている音に合わせて追う（声らしくない回の揺れの中央値。6〜20 セント）: 声だけの音（真ん中・声の帯）では動かず 6 セントのまま
    /// （ビブラートの声は声のまま）。左右に広い音の多い曲（楽器が重なっただけで揺れが 13〜14 セント。実際の曲と同じくらい）では、下限が上がって、
    /// 歌のない部分のボーカルらしさが下限 6 セントのままより小さい。その曲に重ねた真ん中の声（±40 セント）は、ボーカルらしさ 0.15 以上のまま。
    /// ギターがはっきり鳴る曲（揺れが小さい）では、下限は 6 セント近くのままで、ギター・鍵盤らしさは減らない
    /// （合成音では、ギターが揺れに埋もれて消える曲を作れなかったので、ギターが増えることは録音で確かめた）
    /// </summary>
    public static void TestVibratoFloorFollowsTheMix()
    {
        static double Median(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s[s.Count / 2]; }
        static double P90(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s[(int)(0.9 * (s.Count - 1))]; }
        {
            var (_, l, r, _) = InstrumentSources.Make("vocal");
            var run = RunVib(l, r, true);
            var fl = run.Hops.Where(h => h.T > 2).Select(h => h.Floor).ToList();
            T.Check(Median(fl) <= 6.5 && P90(fl) <= 7, $"vocal alone: the vibrato floor stays at 6 cents (median {Median(fl):F1}, p90 {P90(fl):F1})");
        }
        {
            // 音の多い曲（ギターは小さい）: 揺れが実際の曲くらい → 下限が上がり、歌のない部分のボーカルらしさが減る
            var (l, r) = WideMix(16, 15, false);
            var on = RunVib(l, r, true);
            var off = RunVib(l, r, false);
            double vib = Median(off.Hops.Where(h => h.T > 2).Select(h => h.Vib));
            var fl = on.Hops.Where(h => h.T > 8).Select(h => h.Floor).ToList();
            double vOff = off.MeanVocal(8, 16), vOn = on.MeanVocal(8, 16);
            T.Check(vib >= 10, $"dense wide mix: the instruments alone wobble like a real mix (median {vib:F1} cents ≥ 10)");
            T.Check(Median(fl) >= 9 && P90(fl) <= 20, $"dense wide mix: the floor rises to the mix (median after 8 s {Median(fl):F1} ≥ 9, p90 {P90(fl):F1} ≤ 20)");
            T.Check(vOn <= 0.75 * vOff, $"dense wide mix without singing: vocal {vOff:F3} with a fixed 6-cent floor -> {vOn:F3} (≤ 75%)");
            // 同じ曲に真ん中の声を重ねる: 声はボーカルのまま
            var (vl, vr) = WideMix(16, 15, true, 8, 15);
            var von = RunVib(vl, vr, true);
            double sung = von.MeanVocal(9, 15), unsung = on.MeanVocal(9, 15);
            T.Check(sung >= 0.15 && sung >= 1.5 * unsung, $"dense wide mix + a centred ±40-cent voice: vocal {sung:F3} ≥ 0.15 and ≥ 1.5× without the voice ({unsung:F3})");
        }
        {
            // ギターがはっきり鳴る曲: 揺れが小さいので下限は 6 セント近くのまま、ギター・鍵盤らしさは減らない
            var (l, r) = WideMix(16, 15, false, guitarAmp: 0.25);
            var on = RunVib(l, r, true);
            var off = RunVib(l, r, false);
            double f = Median(on.Hops.Where(h => h.T > 8).Select(h => h.Floor));
            double gOff = off.MeanGuitar(4, 16), gOn = on.MeanGuitar(4, 16);
            T.Check(f <= 8 && gOn >= gOff - 0.005 && gOn >= 0.12, $"clear guitar in the mix: floor {f:F1} ≤ 8 cents, guitar {gOff:F3} -> {gOn:F3} (not lower, ≥ 0.12)");
        }
    }

    /// <summary>音のざらつき: ノイズでは大きく、和音（パッド）では小さい</summary>
    public static void TestFlatness()
    {
        var n = new SignalGen(48000, 4, seed: 12);
        n.White(-20, 0.2, 4);
        var (_, pl, pr, _) = InstrumentSources.Make("pad", 4);
        double fn = new StereoRun(NewEngine()).Feed(n.Buf).Mean(1.5, 4, f => f.Flatness);
        double fp = new StereoRun(NewEngine()).Feed(pl, pr).Mean(1.5, 4, f => f.Flatness);
        T.Check(fn > 0.4 && fp < 0.05, $"flatness: white noise {fn:F2} > 0.40, pad {fp:F2} < 0.05");
    }

    /// <summary>新しい値も、コマの写し・間の補い・無音に戻すで正しく扱う（音名・音の高さは近いほうのコマ）</summary>
    public static void TestInstrumentFieldsCopyAndLerp()
    {
        var a = new AudioFrame { T = 1, PercLow = 0.2f, HarmMid = 0.4f, Vocal = 0.1f, WidthMid = 0, PitchClass = 3, BassF0Hz = 55, BassPitchClass = 9, BassOnset = 1 };
        var b = new AudioFrame { T = 2, PercLow = 0.6f, HarmMid = 0.8f, Vocal = 0.5f, WidthMid = 1, PitchClass = 7, BassF0Hz = 82.4f, BassPitchClass = 4, BassOnset = 0 };
        a.Chroma[3] = 1;
        b.Chroma[7] = 1;
        var m = new AudioFrame();
        m.Lerp(a, b, 0.25);
        T.Near(m.PercLow, 0.3, 1e-6, "lerp PercLow");
        T.Near(m.Vocal, 0.2, 1e-6, "lerp Vocal");
        T.Near(m.Chroma[3], 0.75, 1e-6, "lerp chroma");
        T.Check(m.PitchClass == 3 && m.BassPitchClass == 9 && m.BassF0Hz == 55, "pitch values come from the nearer frame");
        m.Lerp(a, b, 0.75);
        T.Check(m.PitchClass == 7 && m.BassPitchClass == 4 && Math.Abs(m.BassF0Hz - 82.4f) < 1e-3, "pitch values from the nearer frame (the later one)");
        var c = new AudioFrame();
        c.CopyFrom(b);
        T.Check(c.HarmMid == 0.8f && c.WidthMid == 1 && c.Chroma[7] == 1 && c.PitchClass == 7, "copy keeps the instrument values");
        c.Clear();
        T.Check(c.HarmMid == 0 && c.Chroma[7] == 0 && c.PitchClass == -1 && c.BassPitchClass == -1 && c.BassF0Hz == 0, "clear resets them (pitch to −1)");
    }

    /// <summary>ステレオの PCM（32bit 浮動小数・16bit 整数）のサイド = (L − R) / 2。1 チャンネルでは 0 個</summary>
    public static void TestPcmSide()
    {
        var f = new float[] { 0.5f, 0.1f, -0.2f, 0.4f };
        var bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        var dst = new float[2];
        int n = PcmConvert.ToSide(bytes, PcmKind.Float32, 2, dst);
        T.Check(n == 2 && Math.Abs(dst[0] - 0.2f) < 1e-6 && Math.Abs(dst[1] + 0.3f) < 1e-6, "float32 stereo: side = (L − R) / 2");
        var s = new short[] { 16384, -16384 };
        var sb = new byte[4];
        Buffer.BlockCopy(s, 0, sb, 0, 4);
        n = PcmConvert.ToSide(sb, PcmKind.Int16, 2, dst);
        T.Check(n == 1 && Math.Abs(dst[0] - 0.5f) < 1e-4, "int16 stereo: side");
        T.Check(PcmConvert.ToSide(bytes, PcmKind.Float32, 1, dst) == 0, "mono: no side");
    }

    /// <summary>44.1 kHz・96 kHz（落としてから分析する）でも、ステレオの広がりとベースの高さが求まる</summary>
    public static void TestOtherSampleRates()
    {
        foreach (int fs in new[] { 44100, 96000 })
        {
            var l = new SignalGen(fs, 4, seed: 13);
            var r = new SignalGen(fs, 4, seed: 14);
            l.BassLine(0.2, 4, 2, new[] { 55.0 });
            r.BassLine(0.2, 4, 2, new[] { 55.0 });
            l.PadChord(0.2, 4, InstrumentSources.PadChord1, seed: 1);
            r.PadChord(0.2, 4, InstrumentSources.PadChord1, seed: 2);
            var run = new StereoRun(NewEngine(fs)).Feed(l.Buf, r.Buf);
            var voiced = run.Between(1.5, 4).Where(x => x.BassF0Hz > 0).ToList();
            double f0 = voiced.Count > 0 ? voiced.Average(x => x.BassF0Hz) : 0;
            double w = run.Mean(1.5, 4, x => x.WidthMid);
            T.Check(Math.Abs(f0 - 55) < 2 && w > 0.5, $"{fs} Hz: bass f0 {f0:F1} Hz (55), width {w:F2} > 0.5");
        }
    }

    /// <summary>計算の重さ: 楽器ごとの手がかりを足した分（ステレオ）。分析中にメモリを確保しない</summary>
    public static void TestInstrumentCpuPerHop()
    {
        int fs = 48000;
        var l = new SignalGen(fs, 30, seed: 91);
        var r = new SignalGen(fs, 30, seed: 92);
        l.Loop(128, 0.2, 30, hats: true);
        r.Loop(128, 0.2, 30, hats: true);
        l.PadChord(0.2, 30, InstrumentSources.PadChord1, seed: 3);
        r.PadChord(0.2, 30, InstrumentSources.PadChord1, seed: 4);
        l.Pink(-30, 0, 30);
        var mid = new float[l.Buf.Length];
        var side = new float[l.Buf.Length];
        for (int i = 0; i < mid.Length; i++) { mid[i] = 0.5f * (l.Buf[i] + r.Buf[i]); side[i] = 0.5f * (l.Buf[i] - r.Buf[i]); }
        bool saved = AudioEngine.InstrumentsEnabled;
        var cost = new Dictionary<bool, double>();
        long allocOn = 0;
        try
        {
            // 交互に 3 回ずつ測って、それぞれのいちばん速い回を使う（ほかの処理の影響を減らす）
            foreach (bool on in new[] { false, true, false, true, false, true })
            {
                AudioEngine.InstrumentsEnabled = on;
                var e = new AudioEngine(fs) { Settings = AudioEngineSettings.Default with { TempoMode = TempoModes.Off } };
                int warm = 10 * fs / 512 * 512;
                for (int i = 0; i < warm; i += 512) e.Push(new ReadOnlySpan<float>(mid, i, 512), new ReadOnlySpan<float>(side, i, 512), 1000 + i / (double)fs);
                int hops = (mid.Length - warm) / 512;
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int h = 0; h < hops; h++)
                {
                    int i = warm + h * 512;
                    e.Push(new ReadOnlySpan<float>(mid, i, 512), new ReadOnlySpan<float>(side, i, 512), 1000 + i / (double)fs);
                }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / hops;
                if (on) allocOn = GC.GetAllocatedBytesForCurrentThread() - a0;
                cost[on] = cost.TryGetValue(on, out double old) ? Math.Min(old, ms) : ms;
            }
        }
        finally { AudioEngine.InstrumentsEnabled = saved; }
        T.Info($"Push per hop (stereo, TempoMode Off): without instrument features {cost[false]:F3} ms, with {cost[true]:F3} ms (+{cost[true] - cost[false]:F3} ms)");
        T.Check(cost[true] - cost[false] < 0.15, "instrument features add less than 0.15 ms per hop");
        T.Check(allocOn <= 1024, $"no allocations per hop after warm-up ({allocOn} bytes)");
    }
}
