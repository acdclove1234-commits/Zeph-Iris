// ==================================================
// SceneJumpTests.cs - 楽器の AI の場面が、どんなときも一度に飛ばない（レビューで見つかった不具合の確かめ）:
//                     移り変わりの途中の取り直し・失敗、計算中の取り直し、片付けを待たない、オフにした・AI を外したときのクロスフェード
//   AI の代わりの関数と合成の曲（SignalGen）で確かめる（実際の曲・アプリでは確かめていない）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using ProFanController;

namespace LogicTests;

public static class SceneJumpTests
{
    private const double T0 = 1000;
    /// <summary>1 回の比べで「飛んだ」とみなす見え方の差（場面の混ざり具合の差の合計。ふつうの移り変わりは同じ時刻で比べれば 0）</summary>
    private const double JumpTol = 0.05;

    /// <summary>時刻 t に見えている場面の混ざり具合（まとまりごとの割合。合計 1）</summary>
    internal static float[] Mix(MusicScene s, double t)
    {
        var w = new float[8];
        float u = s.Progress(t);
        w[(int)s.From] += 1 - u;
        w[(int)s.Group] += u;
        return w;
    }

    /// <summary>同じ時刻 t での、2 つの場面の見え方の差（0 = 同じ、2 = まったく違う）</summary>
    internal static double Jump(MusicScene a, MusicScene b, double t)
    {
        var x = Mix(a, t);
        var y = Mix(b, t);
        double d = 0;
        for (int i = 0; i < x.Length; i++) d += Math.Abs(x[i] - y[i]);
        return d;
    }

    private static float[] G(float vocal = 0, float keys = 0, float drums = 0)
    {
        var g = new float[SceneClasses.Count];
        g[SceneClasses.Vocal] = vocal;
        g[SceneClasses.Keys] = keys;
        g[SceneClasses.Drums] = drums;
        g[SceneClasses.Music] = 0.8f;
        return g;
    }

    // ================================================================
    // 場面の決め方（SceneTracker）

    /// <summary>移り変わりの途中で取り直しても、見えている混ざり具合から続く（前は目標の色から場面なしへ移り直し、一度に飛んだ）</summary>
    public static void TestTrackerResetMidFadeDoesNotJump()
    {
        var tr = new SceneTracker();
        double resetAt = double.NaN, worst = 0, uAtReset = 0;
        bool sawNoneFromVocal = false, vocalAgain = false;
        for (double t = T0 + SceneTracker.Tune.HopSec; t < T0 + 40; t += SceneTracker.Tune.HopSec)
        {
            var before = tr.Current;
            if (double.IsNaN(resetAt) && before.Group == SceneGroup.Vocal && t >= before.SwitchT + 0.5)
            {
                resetAt = t;
                uAtReset = before.Progress(t);
                tr.Reset(t);
                worst = Math.Max(worst, Jump(before, tr.Current, t));
                before = tr.Current;
            }
            tr.Update(t, t < T0 + 10 ? G(keys: 0.5f) : G(vocal: 0.6f));
            worst = Math.Max(worst, Jump(before, tr.Current, t));
            if (!double.IsNaN(resetAt) && tr.Current is { Group: SceneGroup.None, From: SceneGroup.Vocal }) sawNoneFromVocal = true;
            if (sawNoneFromVocal && tr.Current.Group == SceneGroup.Vocal) vocalAgain = true;
        }
        T.Info($"tracker reset at fade progress {uAtReset:F2}: largest jump {worst:F3}");
        T.Check(!double.IsNaN(resetAt) && uAtReset > 0.05 && uAtReset < 0.6, $"the reset happens in the middle of a fade (progress {uAtReset:F2})");
        T.Check(worst < JumpTol, $"no jump when resetting in the middle of a fade (largest {worst:F3}, limit {JumpTol})");
        T.Check(sawNoneFromVocal && vocalAgain, "…it still returns to None (from Vocal) and later finds the vocal again");
    }

    // ================================================================
    // 楽器の AI（SceneTagger）と AI の代わりの関数

    private const int ModeKeys = 0, ModeVocal = 1, ModeDrums = 2;

    /// <summary>mode に合わせた 527 個のらしさ（鍵盤 = Piano、歌 = Singing、ドラム = Drum kit）</summary>
    private static bool Scores(int mode, float[] s)
    {
        Array.Clear(s);
        s[SceneClasses.MusicClass] = 0.8f;
        s[CedTiny.IndexOf(mode switch { ModeKeys => "Piano", ModeVocal => "Singing", _ => "Drum kit" })] = 0.8f;
        return true;
    }

    private static readonly float[] Noise = MakeNoise();
    private static float[] MakeNoise()
    {
        var g = new SignalGen(48000, 2, seed: 77);
        g.Pink(-20, 0, 2);
        return g.Buf;
    }

    /// <summary>音を 512 個ずつ流し、コマごとに見えている場面の飛びを記録する</summary>
    internal sealed class Feeder
    {
        public double T = T0;
        private int _pos;
        private MusicScene? _last;
        public double Worst;
        public void Hop(SceneTagger tg, bool wait = true)
        {
            if (_pos + 512 > Noise.Length) _pos = 0;
            tg.Push(Noise.AsSpan(_pos, 512), T);
            _pos += 512;
            T += 512 / 48000.0;
            tg.Tick(T, true);
            if (wait) WaitIdle(tg);
            Look(tg);
        }
        /// <summary>いま見えている場面を前のコマと比べる</summary>
        public void Look(SceneTagger tg)
        {
            var s = tg.Current;
            if (_last is not null) Worst = Math.Max(Worst, Jump(_last, s, T));
            _last = s;
        }
        public void Run(SceneTagger tg, double sec)
        {
            double end = T + sec;
            while (T < end) Hop(tg);
        }
        public bool RunUntil(SceneTagger tg, Func<bool> done, double maxSec)
        {
            double end = T + maxSec;
            while (T < end)
            {
                if (done()) return true;
                Hop(tg);
            }
            return done();
        }
    }

    internal static bool WaitIdle(SceneTagger tg, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (tg.Busy && sw.ElapsedMilliseconds < ms) Thread.Sleep(0);
        return !tg.Busy;
    }

    /// <summary>出力先が変わって取り直したのが、場面が移り変わっている途中でも、色は一度に飛ばない</summary>
    public static void TestTaggerResetMidFadeDoesNotJump()
    {
        int mode = ModeKeys;
        using var tg = new SceneTagger((w, s) => Scores(Volatile.Read(ref mode), s), 48000);
        var f = new Feeder();
        f.Run(tg, 10);
        T.Check(tg.Current.Group == SceneGroup.Keys, $"keys scene first ({tg.Current.Group})");
        Volatile.Write(ref mode, ModeVocal);
        f.RunUntil(tg, () => tg.Current.Group == SceneGroup.Vocal, 10);
        var sw = tg.Current;
        f.RunUntil(tg, () => f.T >= sw.SwitchT + 0.5, 2);
        float u = sw.Progress(f.T);
        f.Worst = 0;
        tg.Reset();
        f.Run(tg, 15);
        T.Info($"tagger reset at fade progress {u:F2}: largest jump {f.Worst:F3}; scene now {tg.Current.Group}");
        T.Check(sw.Group == SceneGroup.Vocal && u > 0.02 && u < 0.6, $"the reset happens in the middle of the keys → vocal fade ({u:F2})");
        T.Check(f.Worst < JumpTol, $"no jump on Reset in the middle of a fade (largest {f.Worst:F3}, limit {JumpTol})");
        T.Check(tg.Current.Group == SceneGroup.Vocal, $"the vocal is found again after the reset ({tg.Current.Group})");
    }

    /// <summary>
    /// 場面を切り替える計算の最中に取り直しても、場面なしへの移り変わりの「元」が途中で変わらない
    /// （前は、捨てた計算で決め方の場面が歌に進んでいたため、鍵盤から → 歌から に一度に変わった）
    /// </summary>
    public static void TestResetDuringASwitchingInferenceKeepsItsSource()
    {
        int mode = ModeKeys;
        using var gate = new ManualResetEventSlim(true);
        int armed = 0, blocked = 0;
        using var tg = new SceneTagger((w, s) => Scores(Volatile.Read(ref mode), s), 48000)
        {
            // 決め方が歌に切り替えた計算で、結果を出す前に止める
            Diag = (_, _, tr) =>
            {
                if (Volatile.Read(ref armed) == 1 && tr.Current.Group == SceneGroup.Vocal && Interlocked.Exchange(ref blocked, 1) == 0) gate.Wait();
            },
        };
        var f = new Feeder();
        f.Run(tg, 10);
        T.Check(tg.Current.Group == SceneGroup.Keys, $"keys scene first ({tg.Current.Group})");
        gate.Reset();
        Volatile.Write(ref armed, 1);
        Volatile.Write(ref mode, ModeVocal);
        double end = f.T + 10;
        while (Volatile.Read(ref blocked) == 0 && f.T < end)
        {
            f.Hop(tg, wait: false);
            var sw = Stopwatch.StartNew();
            while (tg.Busy && Volatile.Read(ref blocked) == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(0);
        }
        T.Check(Volatile.Read(ref blocked) == 1 && tg.Current.Group == SceneGroup.Keys, "the inference that switches to Vocal is in flight (not delivered)");
        f.Worst = 0;
        tg.Reset();
        f.Hop(tg, wait: false);
        Thread.Sleep(20);
        gate.Set();
        WaitIdle(tg);
        Volatile.Write(ref mode, ModeDrums);   // 取り直したあとはドラムだけ（場面なしのまま）
        f.Run(tg, 6);
        T.Info($"reset during a switching inference: largest jump {f.Worst:F3}; scene {tg.Current.Group} from {tg.Current.From}");
        T.Check(f.Worst < JumpTol, $"no jump: the fade to None keeps its source (largest {f.Worst:F3}, limit {JumpTol})");
        T.Check(tg.Current is { Group: SceneGroup.None, From: SceneGroup.Keys }, $"…it fades from the keys colour that was shown ({tg.Current.From})");
    }

    /// <summary>移り変わりの途中で AI が失敗しても、色は一度に飛ばない（移り終わってから、ゆっくり場面なしに戻す）</summary>
    public static void TestFailureMidFadeDoesNotJump()
    {
        int mode = ModeKeys, fail = 0;
        using var tg = new SceneTagger((w, s) =>
        {
            if (Volatile.Read(ref fail) == 1) throw new InvalidOperationException("test");
            return Scores(Volatile.Read(ref mode), s);
        }, 48000);
        var f = new Feeder();
        f.Run(tg, 10);
        Volatile.Write(ref mode, ModeVocal);
        f.RunUntil(tg, () => tg.Current.Group == SceneGroup.Vocal, 10);
        var sw = tg.Current;
        f.RunUntil(tg, () => f.T >= sw.SwitchT + 0.3, 2);
        f.Worst = 0;
        Volatile.Write(ref fail, 1);
        f.RunUntil(tg, () => tg.Failed, 3);
        float u = sw.Progress(f.T);
        f.Run(tg, 6);
        T.Info($"failure at fade progress {u:F2}: largest jump {f.Worst:F3}; scene {tg.Current.Group}");
        T.Check(tg.Failed && sw.Group == SceneGroup.Vocal && u < 0.9, $"the AI fails in the middle of a fade ({u:F2})");
        T.Check(f.Worst < JumpTol, $"no jump on a failure in the middle of a fade (largest {f.Worst:F3}, limit {JumpTol})");
        T.Check(tg.Current is { Group: SceneGroup.None, From: SceneGroup.Vocal }, $"…it still goes back to None slowly ({tg.Current.Group})");
    }

    /// <summary>
    /// 計算が長くかかっている間に止めても、Dispose は待たない（音の取り込み・画面のスレッドを止めない）。
    /// 前は最大 2 秒待ち、それでも終わらなければモデルを計算中に片付けて、アプリごと落ちることがあった
    /// </summary>
    public static void TestDisposeDoesNotWaitForASlowInference()
    {
        int slow = 0, running = 0;
        var tg = new SceneTagger((w, s) =>
        {
            if (Volatile.Read(ref slow) == 1)
            {
                Volatile.Write(ref running, 1);
                Thread.Sleep(1500);
                Volatile.Write(ref running, 0);
            }
            return Scores(ModeVocal, s);
        }, 48000);
        var f = new Feeder();
        f.Run(tg, 2);
        Volatile.Write(ref slow, 1);
        var guard = Stopwatch.StartNew();
        while (Volatile.Read(ref running) == 0 && guard.ElapsedMilliseconds < 3000) f.Hop(tg, wait: false);
        T.Check(Volatile.Read(ref running) == 1, "a slow inference is running");
        var sw = Stopwatch.StartNew();
        tg.Dispose();
        long ms = sw.ElapsedMilliseconds;
        T.Info($"Dispose during a 1.5 s inference returned in {ms} ms");
        T.Check(ms < 100, $"Dispose does not wait for the running inference ({ms} ms)");
        Thread.Sleep(1700);   // 計算が終わるのを待つ（このあと、ほかのテストに影響しないように）
    }

    // ================================================================
    // 光の描画（DeviceRenderer）

    private static float[]? _song;
    private static float[] Song()
    {
        if (_song is not null) return _song;
        var g = new SignalGen(48000, 12, seed: 128);
        g.DenseMix(128, 0.5, 11.5);
        return _song = g.Buf;
    }

    /// <summary>
    /// 曲を流して 60 コマ／秒で描き、コマごとに at（曲の頭からの秒 → 「AI で楽器を聞き分ける」・場面・パレット）を使う。
    /// switchT 秒に変えたあとの最初の 2 コマと 0.15 秒の間の、1 コマの値の変化の最大と、ほかのコマ（2 秒から変える前まで）の最大を返す
    /// </summary>
    internal static (int First, int Window, int Elsewhere) StepAtSwitch(string style, Func<double, (bool On, MusicScene Scene, string Palette)> at, double switchT = 8)
    {
        const int fs = 48000;
        const double fps = 60;
        var song = Song();
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var dev = new RgbDevice(0, "Strip", 24, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = true, StaticColor = "#6A00FF" };
        var input = new RenderInput { Cfg = cfg, Device = new RgbDeviceSettings { Name = dev.Name } };
        var output = new Rgb[dev.LedCount];
        var prev = new Rgb[dev.LedCount];
        long cursor = 0;
        int first = -1, window = 0, elsewhere = 0;
        bool have = false;
        double next = T0, last = double.NaN;
        ResolvedMusic? cached = null;
        for (int pos = 0; pos < song.Length; pos += fs / 100)
        {
            int n = Math.Min(fs / 100, song.Length - pos);
            engine.Push(song.AsSpan(pos, n), T0 + pos / (double)fs);
            double tAudio = T0 + (pos + n) / (double)fs;
            while (next <= tAudio)
            {
                double t = next;
                var (isOn, scene, pal) = at(t - T0);
                if (cached is null || cached.SceneColors != isOn || !ReferenceEquals(cached.Palette, pal))
                    cached = ResolvedMusic.Default with { Effect = style, Palette = pal, SceneColors = isOn };
                input.Music = cached;
                input.Scene = scene;
                input.T = input.TEval = t;
                input.TPrevEval = double.IsNaN(last) ? t : last;
                input.Dt = double.IsNaN(last) ? 1 / fps : t - last;
                input.Beat = engine.Beat;
                input.Features = engine.Features;
                engine.Features.Sample(t, input.Frame);
                input.Events.Clear();
                engine.Events.Read(ref cursor, t, input.Events);
                r.Render(input, output);
                if (have)
                {
                    int step = 0;
                    for (int i = 0; i < output.Length; i++)
                        step = Math.Max(step, Math.Max(Math.Abs(output[i].R - prev[i].R), Math.Max(Math.Abs(output[i].G - prev[i].G), Math.Abs(output[i].B - prev[i].B))));
                    double rel = t - T0 - switchT;
                    if (rel >= -1e-9 && rel < 0.15)
                    {
                        if (rel < 2 / fps) first = Math.Max(first, step);   // 最初の 2 コマ（コマの時刻の丸めで、1 コマ目がまだ前の設定のことがある）
                        window = Math.Max(window, step);
                    }
                    else if (t - T0 >= 2 && rel < 0) elsewhere = Math.Max(elsewhere, step);
                }
                Array.Copy(output, prev, output.Length);
                have = true;
                last = t;
                next += 1 / fps;
            }
        }
        return (first, window, elsewhere);
    }

    /// <summary>
    /// 一度に変わらないか: 変えた最初のコマの変化がふだんのコマ並み、0.15 秒の間の最大が、パレットを変えたとき（もともとの 0.4 秒のクロスフェード）並み。
    /// 前は最初のコマで一度に変わっていた（ムード・レインボーで 26 段）
    /// </summary>
    private static void CheckSmooth(string what, string style, string pal, Func<double, (bool On, MusicScene Scene, string Palette)> at)
    {
        var (first, window, elsewhere) = StepAtSwitch(style, at);
        var (_, refWindow, _) = StepAtSwitch(style, t => (false, MusicScene.None, t < 8 ? Palettes.Ocean : pal));
        T.Info($"{what}, {style}/{pal}: first frame {first} codes, largest in 0.15 s {window} (palette change Ocean→{pal}: {refWindow}), elsewhere {elsewhere}");
        T.Check(first <= Math.Max(elsewhere, 4), $"{what}, {style}/{pal}: no snap in the first frame ({first} codes vs {elsewhere} elsewhere)");
        T.Check(window <= Math.Max(elsewhere, refWindow + 3), $"{what}, {style}/{pal}: as smooth as a palette change ({window} vs {refWindow} codes)");
    }

    /// <summary>歌の場面（落ち着いている）を見せている途中で、「AI で楽器を聞き分ける」をオフにしても、色は一度に変わらない（0.4 秒のクロスフェード）</summary>
    public static void TestTurningScenesOffCrossfades()
    {
        var vocal = new MusicScene(SceneGroup.Vocal, SceneGroup.None, T0 - 10, 0.8f);
        foreach (var (style, pal) in new[] { (MusicStyles.Mood, Palettes.Rainbow), (MusicStyles.BeatHue, Palettes.Fire), (MusicStyles.Ensemble, Palettes.Solid) })
            CheckSmooth("switch off", style, pal, t => t < 8 ? (true, vocal, pal) : (false, MusicScene.None, pal));
    }

    /// <summary>楽器の AI を外した（モデルを止めた・付けられなかった）ために場面がいきなり場面なしになっても、色は一度に変わらない</summary>
    public static void TestSceneDroppedToNoneCrossfades()
    {
        var vocal = new MusicScene(SceneGroup.Vocal, SceneGroup.None, T0 - 10, 0.8f);
        foreach (var (style, pal) in new[] { (MusicStyles.Mood, Palettes.Rainbow), (MusicStyles.Ensemble, Palettes.Rainbow) })
            CheckSmooth("scene dropped to None", style, pal, t => (true, t < 8 ? vocal : MusicScene.None, pal));
    }
}
