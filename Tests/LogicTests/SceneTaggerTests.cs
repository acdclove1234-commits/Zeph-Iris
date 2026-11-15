// ==================================================
// SceneTaggerTests.cs - 楽器の AI（YAMNet）: 埋め込んだモデル・まとまりへのまとめ方・場面の決め方（ならし・ヒステリシス・最低の長さ）・
//                       16 kHz への変換・取り込みスレッドを待たせない・失敗・取り直し・光の描画への受け渡し・ライセンスの説明
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using ProFanController;

namespace LogicTests;

/// <summary>
/// 合成の音（SignalGen）について: YAMNet は合成の音をあまり見分けられない（設計のときに測った値、音量はそろえていない）。
///   無音 → Silence 1.00、ピンクノイズ → 水の音・ピンクノイズ（楽器は 0.00）、ドラムのループ → Drum machine 0.88・Drum kit 0.80（よい）、
///   PadChord → Synthesizer 0.59・Keyboard 0.48（よい）、VocalTone → Music 0.34・Singing 0.04（だめ）、
///   BrassTone → Music 0.38・Synthesizer 0.12・金管 0.00（だめ）、Strum → Electric piano 0.24・Guitar 0.00（鍵盤に聞こえる）、
///   BassLine → Synthesizer 0.42・Bass guitar 0.00（だめ）、混ぜた音 → ほぼ「Music」だけ（楽器はどれも 0.07 以下）。
/// なのでモデルを使うテストは、無音・ノイズ → 場面なし、ドラム → Drums、PadChord → Synth か Keys だけを確かめる。
/// 歌・金管・ギターの見分けは、合成の音では確かめられない（実際の曲でも、しきい値はまだ合わせていない）
/// </summary>
public static class SceneTaggerTests
{
    private const double T0 = 1000;

    private static Yamnet? Model(string what)
    {
        var m = Yamnet.TryCreate(out string? err);
        T.Check(m is not null, $"{what}: model loads ({err ?? "ok"})");
        return m;
    }

    // ================================================================
    // A1 モデル

    public static void TestModelLoadsAndHasTheExpectedShape()
    {
        long mem0 = Process.GetCurrentProcess().PrivateMemorySize64;
        var sw = Stopwatch.StartNew();
        using var y = Model("A1");
        if (y is null) return;
        T.Info($"load {sw.ElapsedMilliseconds} ms, private memory +{(Process.GetCurrentProcess().PrivateMemorySize64 - mem0) / 1048576} MB");
        T.Check(y.Describe() == "waveform[-1] -> output_0[-1,521]", $"input/output shape ({y.Describe()})");
        T.Check(Yamnet.ModelSha256() == Yamnet.Sha256, "embedded model is the pinned file (SHA-256)");
        T.Check(Yamnet.ModelBytes().Length == 16_093_603, "embedded model size is 16,093,603 bytes");

        var scores = new float[Yamnet.Classes];
        y.Classify(new float[Yamnet.WindowSamples], scores);
        T.Check(scores.All(s => float.IsFinite(s) && s >= 0 && s <= 1), "all 521 scores are finite and in 0..1");
        T.Check(scores[SceneClasses.SilenceClass] >= 0.9f, $"15600 zeros → Silence ≥ 0.9 ({scores[SceneClasses.SilenceClass]:F2})");

        var bad = Yamnet.TryCreate(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, out string? badErr);
        T.Check(bad is null && !string.IsNullOrWhiteSpace(badErr), "a corrupt model gives null and a reason");
        bad?.Dispose();
    }

    // ================================================================
    // A2 速さ・スレッド・メモリ

    public static void TestModelIsFastAndRunsOnTheCallingThread()
    {
        int threads0 = ThreadCount();
        using var y = Model("A2");
        if (y is null) return;
        var g = new SignalGen(Yamnet.SampleRate, 1, seed: 5);
        g.Pink(-20, 0, 1);
        var win = g.Buf.AsSpan(0, Yamnet.WindowSamples).ToArray();
        var scores = new float[Yamnet.Classes];
        var times = new List<double>();
        for (int i = 0; i < 20; i++) y.Classify(win, scores);
        int threads1 = ThreadCount();
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            y.Classify(win, scores);
            times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        times.Sort();
        T.Info($"Classify (15600 samples, 1 thread): median {times[25]:F2} ms, p90 {times[45]:F2} ms, max {times[^1]:F2} ms; " +
               $"managed allocation {alloc / 50.0:F0} B/run; process threads {threads0} → {threads1}");
        T.Check(times[25] <= 10, $"median Classify ≤ 10 ms ({times[25]:F2})");
        T.Check(threads1 <= threads0 + 1, $"ONNX Runtime adds no worker threads ({threads0} → {threads1})");
        T.Check(alloc / 50.0 <= 1024, $"Classify allocates little managed memory per run ({alloc / 50.0:F0} B)");
        T.Check(scores.All(float.IsFinite), "scores stay finite");
    }

    private static int ThreadCount()
    {
        using var p = Process.GetCurrentProcess();
        return p.Threads.Count;
    }

    // ================================================================
    // 16 kHz への変換

    public static void TestResamplerTo16k()
    {
        foreach (int fs in new[] { 48000, 44100 })
        {
            foreach (double hz in new[] { 440.0, 1000, 3000 })
            {
                var (n, freq, rms) = Resample(fs, hz, 3);
                double expect = 3.0 * Yamnet.SampleRate;
                T.Check(Math.Abs(n - expect) <= 200, $"{fs} Hz → 16 kHz: length {n} ≈ {expect:F0}");
                T.Check(Math.Abs(freq / hz - 1) < 0.01, $"{fs} Hz, {hz} Hz tone keeps its pitch ({freq:F1} Hz)");
                T.Check(Math.Abs(rms / Math.Sqrt(0.5) - 1) < 0.1, $"{fs} Hz, {hz} Hz tone keeps its level (rms {rms:F3})");
            }
            var (_, _, hiRms) = Resample(fs, 12000, 3);
            T.Info($"{fs} Hz: a 12 kHz tone (above the 8 kHz limit) leaves rms {hiRms:F3} after the filter");
            T.Check(hiRms < 0.5, $"{fs} Hz: tones above 8 kHz are attenuated (rms {hiRms:F3} of 0.707)");
        }
    }

    /// <summary>fs の正弦波を 480 個ずつ変換し、長さ・周波数（0 を横切る回数）・2 乗平均（始めと終わりの 0.2 秒を除く）</summary>
    private static (int N, double Hz, double Rms) Resample(int fs, double hz, double sec)
    {
        var src = new float[(int)(fs * sec)];
        for (int i = 0; i < src.Length; i++) src[i] = (float)Math.Sin(2 * Math.PI * hz * i / fs);
        var rs = new Resampler16k(fs);
        var outList = new List<float>();
        for (int i = 0; i < src.Length; i += 480) rs.Push(src.AsSpan(i, Math.Min(480, src.Length - i)), s => outList.AddRange(s.ToArray()));
        var o = outList.ToArray();
        int a = (int)(0.2 * Yamnet.SampleRate), b = o.Length - a;
        int cross = 0;
        double s2 = 0;
        for (int i = a; i < b; i++)
        {
            s2 += o[i] * (double)o[i];
            if (i > a && (o[i - 1] < 0) != (o[i] < 0)) cross++;
        }
        double dur = (b - a) / (double)Yamnet.SampleRate;
        return (o.Length, cross / 2.0 / dur, Math.Sqrt(s2 / Math.Max(1, b - a)));
    }

    // ================================================================
    // まとまりへのまとめ方

    public static void TestGroupMapping()
    {
        var s = new float[Yamnet.Classes];
        var g = new float[SceneClasses.Count];
        s[182] = 0.6f;   // Trumpet
        s[191] = 0.7f;   // Flute（0.8 倍）
        s[148] = 0.3f;   // Piano
        s[240] = 0.8f;   // EDM（0.5 倍）
        s[153] = 0.2f;   // Synthesizer
        s[179] = 0.5f;   // Orchestra（0.7 倍）
        s[137] = 0.9f;   // Bass guitar（まとまりに入れない）
        s[167] = 0.4f;   // Hi-hat
        s[132] = 0.95f;  // Music
        s[0] = 0.3f;     // Speech
        s[31] = 0.25f;   // Rapping
        s[494] = float.NaN;
        SceneClasses.Groups(s, g);
        T.Near(g[SceneClasses.Brass], 0.6, 1e-6, "Brass = max(trumpet 0.6, flute 0.7 × 0.8)");
        T.Near(g[SceneClasses.Keys], 0.3, 1e-6, "Keys = piano");
        T.Near(g[SceneClasses.Synth], 0.4, 1e-6, "Synth = max(synthesizer 0.2, EDM 0.8 × 0.5)");
        T.Near(g[SceneClasses.Strings], 0.35, 1e-6, "Strings = orchestra 0.5 × 0.7");
        T.Near(g[SceneClasses.Guitar], 0, 1e-6, "bass guitar is not counted as Guitar");
        T.Near(g[SceneClasses.Drums], 0.4, 1e-6, "Drums = hi-hat");
        T.Near(g[SceneClasses.Vocal], 0.25, 1e-6, "Vocal = rapping");
        T.Near(g[SceneClasses.Speech], 0.3, 1e-6, "Speech = speech");
        T.Near(g[SceneClasses.Music], 0.95, 1e-6, "Music");
        T.Near(g[SceneClasses.Silence], 0, 1e-6, "NaN scores count as 0");
        T.Check((int)SceneGroup.Speech - 1 == SceneClasses.Speech && (int)SceneGroup.Vocal - 1 == SceneClasses.Vocal,
            "group indices line up with SceneGroup (SceneGroup − 1)");
    }

    // ================================================================
    // A4 場面の決め方（値を入れて確かめる）

    private static float[] G(float vocal = 0, float brass = 0, float guitar = 0, float keys = 0, float synth = 0, float strings = 0,
        float speech = 0, float drums = 0, float music = 0.8f, float silence = 0)
    {
        var g = new float[SceneClasses.Count];
        g[SceneClasses.Vocal] = vocal; g[SceneClasses.Brass] = brass; g[SceneClasses.Guitar] = guitar; g[SceneClasses.Keys] = keys;
        g[SceneClasses.Synth] = synth; g[SceneClasses.Strings] = strings; g[SceneClasses.Speech] = speech; g[SceneClasses.Drums] = drums;
        g[SceneClasses.Music] = music; g[SceneClasses.Silence] = silence;
        return g;
    }

    private sealed record Switch(double T, SceneGroup From, SceneGroup To, bool FromQuiet);

    /// <summary>0.48 秒ごとに scoresAt(t)（null なら静か）を入れ、場面の切り替えを集める</summary>
    private static List<Switch> Simulate(Func<double, float[]?> scoresAt, double seconds, SceneTracker? tracker = null)
    {
        var tr = tracker ?? new SceneTracker();
        var list = new List<Switch>();
        var cur = tr.Current;
        for (double t = Yamnet.HopSec; t <= seconds + 1e-9; t += Yamnet.HopSec)
        {
            var g = scoresAt(t);
            var s = g is null ? tr.Quiet(t) : tr.Update(t, g);
            if (s.Group != cur.Group) list.Add(new Switch(s.SwitchT, cur.Group, s.Group, g is null || g[SceneClasses.Silence] >= 0.5f));
            cur = s;
        }
        return list;
    }

    private static readonly List<(string Name, List<Switch> S)> AllScenarios = new();

    public static void TestTrackerSwitchesAfterASectionChange()
    {
        var sw = Simulate(t => G(vocal: t >= 10 ? 0.4f : 0), 30);
        AllScenarios.Add(("step", sw));
        T.Info("vocal 0 → 0.4 at 10 s: " + string.Join(", ", sw.Select(x => $"{x.From}→{x.To} at {x.T:F2} s")));
        T.Check(sw.Count == 1 && sw[0].To == SceneGroup.Vocal, "exactly one switch, to Vocal");
        T.Check(sw.Count > 0 && sw[0].T >= 10.5 && sw[0].T <= 12.5, $"the switch starts 0.5–2.5 s after the change ({(sw.Count > 0 ? sw[0].T : double.NaN):F2} s)");

        // 抜けるとき: 歌が消えたら場面なしへ（抜ける値より下がり、1 秒続いてから・前の切り替えから 6 秒たってから）
        var back = Simulate(t => G(vocal: t < 20 ? 0.4f : 0), 40);
        AllScenarios.Add(("leave", back));
        T.Info("vocal 0.4 → 0 at 20 s: " + string.Join(", ", back.Select(x => $"{x.From}→{x.To} at {x.T:F2} s")));
        T.Check(back.Count == 2 && back[1].To == SceneGroup.None && back[1].T > 20 && back[1].T < 24, "back to None a few seconds after the voice stops");
    }

    public static void TestTrackerHysteresisAndDwell()
    {
        // 交互に強く出ても、続かなければ切り替えない
        var alt = Simulate(t => (int)Math.Round(t / Yamnet.HopSec) % 2 == 0 ? G(vocal: 0.4f) : G(guitar: 0.4f), 60);
        AllScenarios.Add(("alternate", alt));
        T.Check(alt.Count <= 2, $"vocal/guitar alternating every window: ≤ 2 switches in 60 s ({alt.Count})");

        // 入る値のまわりで揺れても、ちらちら切り替えない
        var rnd = new Random(7);
        var noisy = Simulate(_ => G(vocal: 0.20f + (float)(rnd.NextDouble() * 0.12 - 0.06)), 120);
        AllScenarios.Add(("noise", noisy));
        T.Check(noisy.Count <= 4, $"vocal 0.20 ± 0.06 for 120 s: ≤ 4 switches ({noisy.Count})");

        // 新しい楽器が少しだけ強い（1.25 倍未満）なら今の場面のまま。はっきり強くなれば切り替える
        var margin = Simulate(t => t < 20 ? G(vocal: 0.4f) : t < 40 ? G(vocal: 0.4f, guitar: 0.45f) : G(vocal: 0.4f, guitar: 0.6f), 60);
        AllScenarios.Add(("margin", margin));
        T.Info("margin: " + string.Join(", ", margin.Select(x => $"{x.From}→{x.To} at {x.T:F2} s")));
        T.Check(margin.Count(x => x.T < 40) == 1, "guitar 0.45 vs vocal 0.4 (less than 1.25×) does not take over");
        T.Check(margin.Any(x => x.To == SceneGroup.Guitar && x.T >= 40 && x.T < 43), "guitar 0.6 vs vocal 0.4 takes over within 3 s");

        // 楽器が次々に変わっても、6 秒より短い間隔では切り替えない
        var groups = new[] { "vocal", "brass", "guitar", "keys", "synth", "strings" };
        var fast = Simulate(t =>
        {
            int k = (int)(t / 2.5) % groups.Length;
            return k switch { 0 => G(vocal: 0.6f), 1 => G(brass: 0.6f), 2 => G(guitar: 0.6f), 3 => G(keys: 0.6f), 4 => G(synth: 0.6f), _ => G(strings: 0.6f) };
        }, 90);
        AllScenarios.Add(("fast changes", fast));
        T.Info($"instrument changing every 2.5 s: {fast.Count} switches in 90 s");
    }

    public static void TestTrackerQuietAndSpeech()
    {
        // 歌のあと 3.5 秒静か → 静かになってから 3.5 秒以内に場面なし
        var q = Simulate(t => t < 20 ? G(vocal: 0.4f) : null, 23.6);
        AllScenarios.Add(("quiet", q));
        T.Info("quiet: " + string.Join(", ", q.Select(x => $"{x.From}→{x.To} at {x.T:F2} s{(x.FromQuiet ? " (quiet)" : "")}")));
        T.Check(q.Count == 2 && q[1].To == SceneGroup.None && q[1].FromQuiet && q[1].T <= 23.5, "back to None within 3.5 s of the quiet start");
        // 短い静けさ（2 秒）は場面を保つ
        var shortQ = Simulate(t => t is >= 20 and < 22 ? null : G(vocal: 0.4f), 40);
        AllScenarios.Add(("short quiet", shortQ));
        T.Check(shortQ.Count == 1, $"a 2 s pause keeps the scene ({shortQ.Count} switches)");
        // AI が「無音」と言った窓も静かと数える
        var sil = Simulate(t => t < 20 ? G(vocal: 0.4f) : G(silence: 0.9f, music: 0), 24);
        AllScenarios.Add(("silence class", sil));
        T.Check(sil.Count == 2 && sil[1].To == SceneGroup.None, "Silence ≥ 0.5 counts as quiet");

        // 話し声: 音楽がなければ話し声の場面、音楽が流れていれば話し声の場面にはしない（歌として少し数える）
        var talk = Simulate(_ => G(speech: 0.6f, music: 0.1f), 20);
        AllScenarios.Add(("talk", talk));
        T.Check(talk.Count == 1 && talk[0].To == SceneGroup.Speech, "speech 0.6 without music → Speech");
        var sung = Simulate(_ => G(speech: 0.6f, music: 0.8f), 20);
        AllScenarios.Add(("sung", sung));
        T.Check(sung.All(x => x.To != SceneGroup.Speech), $"speech 0.6 with music 0.8 never gives Speech ({string.Join(",", sung.Select(x => x.To))})");
        T.Check(sung.Count == 1 && sung[0].To == SceneGroup.Vocal, "…it counts as a voice in the music (Vocal)");
        // ドラムだけ → 場面なし（パレットの色のまま）
        var drums = Simulate(_ => G(drums: 0.9f), 20);
        AllScenarios.Add(("drums", drums));
        T.Check(drums.Count == 0, "drums only → stays None");

        // 取り直し: すぐ場面なしへ、そこから移り変わる
        var tr = new SceneTracker();
        Simulate(_ => G(keys: 0.5f), 10, tr);
        T.Check(tr.Current.Group == SceneGroup.Keys, "keys scene before the reset");
        tr.Reset(50);
        T.Check(tr.Current is { Group: SceneGroup.None, From: SceneGroup.Keys, SwitchT: 50 }, "Reset fades from Keys to None starting at the reset time");
    }

    /// <summary>A4e: どの場面の並びでも、2 つの切り替えの間は 6 秒以上（静かになって場面なしに戻すときを除く）</summary>
    public static void TestTrackerNeverSwitchesFasterThanTheDwell()
    {
        if (AllScenarios.Count == 0)
        {
            TestTrackerSwitchesAfterASectionChange();
            TestTrackerHysteresisAndDwell();
            TestTrackerQuietAndSpeech();
        }
        int pairs = 0, bad = 0;
        foreach (var (name, s) in AllScenarios)
            for (int i = 1; i < s.Count; i++)
            {
                if (s[i].FromQuiet || s[i - 1].FromQuiet) continue;
                pairs++;
                if (s[i].T - s[i - 1].T < SceneTracker.Tune.MinDwellSec - 1e-6)
                {
                    bad++;
                    T.Info($"{name}: {s[i - 1].To} at {s[i - 1].T:F2} s → {s[i].To} at {s[i].T:F2} s");
                }
            }
        T.Check(bad == 0, $"no two switches closer than {SceneTracker.Tune.MinDwellSec} s ({pairs} pairs over {AllScenarios.Count} scenarios)");
        // 移り変わりの途中で次の切り替えをしない（色が飛ばないように）: 静かになって戻すときも含めて FadeSec 以上
        int mid = AllScenarios.Sum(x => x.S.Zip(x.S.Skip(1), (a, b) => b.T - a.T < MusicScene.FadeSec - 1e-6 ? 1 : 0).Sum());
        T.Check(mid == 0, $"no switch starts during the previous crossfade ({mid})");
    }

    // ================================================================
    // A3 合成の音で、モデルにできることだけ

    private sealed class TagRun
    {
        public readonly List<(double T, float[] Raw, float[] Ema)> Windows = new();
        public readonly List<(double T, MusicScene S)> Scenes = new();
        public int Inferences, Quiet;
        public double Mean(int group) => Windows.Count == 0 ? double.NaN : Windows.Average(w => w.Raw[group]);
    }

    /// <summary>48 kHz の音を、楽器の AI を付けた分析に流す（AI はその場で動かす）。時刻は曲の頭からの秒</summary>
    private static TagRun RunTagger(Yamnet model, float[] buf, int fs = 48000, string tempo = TempoModes.Auto)
    {
        var r = new TagRun();
        var e = new AudioEngine(fs) { Settings = AudioEngineSettings.Default with { TempoMode = tempo } };
        using var tg = new SceneTagger(model, fs) { Inline = true, Diag = (t, g, tr) => r.Windows.Add((t - T0, g.ToArray(), tr.Ema.ToArray())) };
        e.Tagger = tg;
        MusicScene last = tg.Current;
        new EngineRun(e, T0).Feed(buf, before: s =>
        {
            var c = tg.Current;
            if (!ReferenceEquals(c, last)) { r.Scenes.Add((c.SwitchT - T0, c)); last = c; }
        });
        r.Inferences = tg.Windows;
        r.Quiet = tg.Quiet;
        return r;
    }

    private static string Describe(TagRun r) =>
        $"{r.Inferences} inferences, {r.Quiet} quiet; Drums {r.Mean(SceneClasses.Drums):F2}, Synth {r.Mean(SceneClasses.Synth):F2}, Keys {r.Mean(SceneClasses.Keys):F2}, " +
        $"Vocal {r.Mean(SceneClasses.Vocal):F2}, Music {r.Mean(SceneClasses.Music):F2}; scenes: {(r.Scenes.Count == 0 ? "none" : string.Join(", ", r.Scenes.Select(x => $"{x.S.Group} at {x.T:F2} s")))}";

    public static void TestModelOnSyntheticSounds()
    {
        using var y = Model("A3");
        if (y is null) return;

        var drum = new SignalGen(48000, 12, seed: 21);
        drum.Loop(120, 0, 12, hats: true, snare: true);
        var d = RunTagger(y, drum.Buf);
        T.Info("drum loop 12 s: " + Describe(d));
        T.Check(d.Mean(SceneClasses.Drums) >= 0.5, $"drum loop: Drums mean ≥ 0.5 ({d.Mean(SceneClasses.Drums):F2})");
        T.Check(d.Scenes.All(x => x.S.Group == SceneGroup.None), "drum loop: the scene stays None (the palette's own colours)");

        // 音量をそろえるので、−20 dB 小さくても同じくらい
        var soft = RunTagger(y, drum.Buf.Select(x => x * 0.1f).ToArray());
        T.Info("drum loop ×0.1: " + Describe(soft));
        T.Check(Math.Abs(soft.Mean(SceneClasses.Drums) - d.Mean(SceneClasses.Drums)) <= 0.1,
            $"level independence: Drums mean at ×0.1 within 0.1 of ×1 ({soft.Mean(SceneClasses.Drums):F2} vs {d.Mean(SceneClasses.Drums):F2})");

        var pad = new SignalGen(48000, 12, seed: 22);
        pad.PadChord(0, 12, InstrumentSources.PadChord1);
        var p = RunTagger(y, pad.Buf);
        T.Info("pad chord 12 s: " + Describe(p));
        var firstPad = p.Scenes.FirstOrDefault(x => x.S.Group is SceneGroup.Synth or SceneGroup.Keys);
        T.Check(firstPad.S is not null && firstPad.T <= 4, $"pad chord: the scene becomes Synth or Keys within 4 s ({(firstPad.S is null ? "never" : $"{firstPad.S.Group} at {firstPad.T:F2} s")})");

        var silence = RunTagger(y, new float[48000 * 10]);
        T.Info("silence 10 s: " + Describe(silence));
        T.Check(silence.Inferences == 0 && silence.Quiet > 0, $"silence: no inference ({silence.Inferences}), quiet windows {silence.Quiet}");
        T.Check(silence.Scenes.Count == 0, "silence: scene None");

        var pink = new SignalGen(48000, 10, seed: 23);
        pink.Pink(-20, 0, 10);
        var n = RunTagger(y, pink.Buf);
        T.Info("pink noise −20 dBFS 10 s: " + Describe(n));
        T.Check(n.Scenes.All(x => x.S.Group == SceneGroup.None), "pink noise: scene None");

        // テンポの追跡を使わない設定でも動く
        var off = RunTagger(y, pad.Buf, tempo: TempoModes.Off);
        T.Check(off.Inferences > 10 && off.Scenes.Any(x => x.S.Group is SceneGroup.Synth or SceneGroup.Keys), $"works with tempo Off ({off.Inferences} inferences)");
    }

    // ================================================================
    // A5 取り込みスレッドの負担（メモリを確保しない・重くしない）

    public static void TestAudioThreadCostAndAllocations()
    {
        using var y = Model("A5");
        if (y is null) return;
        int fs = 48000;
        var g = new SignalGen(fs, 34, seed: 91);
        g.Loop(128, 0.2, 34, hats: true, snare: false);
        g.Pink(-30, 0, 34);
        long mem0 = Process.GetCurrentProcess().PrivateMemorySize64;
        double best0 = double.PositiveInfinity, best1 = double.PositiveInfinity;
        long alloc1 = 0;
        int windows = 0, skipped = 0;
        for (int round = 0; round < 3; round++)
        {
            best0 = Math.Min(best0, PushCost(g.Buf, fs, null, out _));
            using var tg = new SceneTagger(y, fs);
            best1 = Math.Min(best1, PushCost(g.Buf, fs, tg, out long a));
            alloc1 = Math.Max(alloc1, a);
            windows = tg.Windows;
            skipped = tg.Skipped;
        }
        long mem1 = Process.GetCurrentProcess().PrivateMemorySize64;
        T.Info($"Push per hop (TempoMode Off, best of 3): without the tagger {best0:F4} ms, with the tagger {best1:F4} ms (+{best1 - best0:F4} ms); " +
               $"allocated after warm-up {alloc1} bytes; worker ran {windows} windows, skipped {skipped}; private memory with the model loaded {mem1 / 1048576} MB (+{(mem1 - mem0) / 1048576} MB since the test started)");
        T.Check(alloc1 <= 1024, $"no allocations on the audio thread after warm-up with the tagger ({alloc1} bytes)");
        T.Check(best1 - best0 <= 0.05, $"the tagger adds ≤ 0.05 ms per hop ({best1 - best0:F4} ms)");
        T.Check(windows > 0, "the worker really classified windows during the run");
    }

    /// <summary>4 秒の準備運転のあとの、Push 1 回（512 個）あたりの平均（ミリ秒）と、その間に確保したメモリ</summary>
    private static double PushCost(float[] buf, int fs, SceneTagger? tagger, out long alloc)
    {
        var e = new AudioEngine(fs) { Settings = AudioEngineSettings.Default with { TempoMode = TempoModes.Off }, Tagger = tagger };
        int warm = 4 * fs / 512 * 512;
        for (int i = 0; i < warm; i += 512) e.Push(new ReadOnlySpan<float>(buf, i, 512), T0 + i / (double)fs);
        int hops = (buf.Length - warm) / 512;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        long ticks0 = Stopwatch.GetTimestamp();
        for (int h = 0; h < hops; h++)
        {
            int i = warm + h * 512;
            e.Push(new ReadOnlySpan<float>(buf, i, 512), T0 + i / (double)fs);
        }
        double ms = (Stopwatch.GetTimestamp() - ticks0) * 1000.0 / Stopwatch.Frequency;
        alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        return ms / hops;
    }

    // ================================================================
    // A6〜A8 待たせない・失敗・取り直し（AI の代わりの関数で）

    /// <summary>歌（Singing 0.8・Music 0.8）のらしさを書く AI の代わり</summary>
    private static bool VocalScores(float[] w, float[] s)
    {
        Array.Clear(s);
        s[24] = 0.8f;
        s[SceneClasses.MusicClass] = 0.8f;
        return true;
    }

    /// <summary>−20 dBFS くらいのノイズ（1 コマ = 512 個）</summary>
    private static readonly float[] Noise = MakeNoise();
    private static float[] MakeNoise()
    {
        var g = new SignalGen(48000, 2, seed: 31);
        g.Pink(-20, 0, 2);
        return g.Buf;
    }

    private sealed class Feeder
    {
        public double T = T0;
        private int _pos;
        public void Hop(SceneTagger tg, bool sounding = true)
        {
            if (_pos + 512 > Noise.Length) _pos = 0;
            tg.Push(Noise.AsSpan(_pos, 512), T);
            _pos += 512;
            T += 512 / 48000.0;
            tg.Tick(T, sounding);
        }
        /// <summary>sec 秒ぶん流す（各コマのあと、AI のスレッドの計算が終わるまで待つ）</summary>
        public void Run(SceneTagger tg, double sec, bool wait = true)
        {
            double end = T + sec;
            while (T < end)
            {
                Hop(tg);
                if (wait) WaitIdle(tg);
            }
        }
    }

    private static bool WaitIdle(SceneTagger tg, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (tg.Busy && sw.ElapsedMilliseconds < ms) Thread.Sleep(0);
        return !tg.Busy;
    }

    public static void TestTickNeverWaitsForTheWorker()
    {
        bool slow = false;
        ThreadPriority prio = ThreadPriority.Normal;
        bool background = false;
        int calls = 0;
        using var tg = new SceneTagger((w, s) =>
        {
            prio = Thread.CurrentThread.Priority;
            background = Thread.CurrentThread.IsBackground;
            Interlocked.Increment(ref calls);
            if (Volatile.Read(ref slow)) Thread.Sleep(300);
            return VocalScores(w, s);
        }, 48000);
        var f = new Feeder();
        f.Run(tg, 3);   // 準備運転（JIT を済ませる）
        T.Check(calls > 0 && prio == ThreadPriority.Lowest && background, $"the AI runs on a background thread at the lowest priority ({prio}, background {background})");
        Volatile.Write(ref slow, true);
        int skipped0 = tg.Skipped;
        var times = new double[2000];
        double worst = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int i = 0; i < times.Length; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                f.Hop(tg);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            worst = times.Max();
            if (worst < 2) break;   // OS の割り込みで 1 回だけ遅れたときは、もう一度測る
        }
        Array.Sort(times);
        T.Info($"Push + Tick with a 300 ms classifier: median {times[1000]:F4} ms, p99 {times[1980]:F4} ms, max {worst:F3} ms; skipped {tg.Skipped - skipped0}");
        T.Check(times[1980] < 1, $"p99 of Push + Tick < 1 ms ({times[1980]:F4})");
        T.Check(worst < 2, $"max of Push + Tick over 2000 hops < 2 ms ({worst:F3})");
        T.Check(tg.Skipped > skipped0, "windows are skipped while the worker is busy (never waited for)");

        // 計算中に片付けても、すぐ終わる（止まらない）
        var sw = Stopwatch.StartNew();
        tg.Dispose();
        T.Check(sw.ElapsedMilliseconds < 2500, $"Dispose while a slow inference runs returns ({sw.ElapsedMilliseconds} ms)");
    }

    public static void TestFailureStopsTheTagger()
    {
        var logs = new List<string>();
        int calls = 0;
        using var tg = new SceneTagger((w, s) =>
        {
            if (Interlocked.Increment(ref calls) > 20) throw new InvalidOperationException("boom");
            return VocalScores(w, s);
        }, 48000) { Log = m => { lock (logs) logs.Add(m); } };
        var f = new Feeder();
        f.Run(tg, 7);
        T.Check(tg.Current.Group == SceneGroup.Vocal, $"vocal scene before the failure ({tg.Current.Group})");
        f.Run(tg, 6);
        T.Check(tg.Failed, "a throwing classifier marks the tagger failed");
        T.Check(tg.Current.Group == SceneGroup.None, "…and the scene goes back to None (a slow fade, not a jump)");
        int callsAfter = calls;
        f.Run(tg, 5);
        T.Check(calls == callsAfter, $"no more inferences are scheduled ({calls - callsAfter})");
        lock (logs) T.Check(logs.Count(l => l.Contains("boom")) == 1 && logs.Count == 1, $"exactly one log line ({logs.Count})");
    }

    public static void TestResultFromBeforeResetIsIgnored()
    {
        using var gate = new ManualResetEventSlim(true);
        using var tg = new SceneTagger((w, s) => { gate.Wait(); return VocalScores(w, s); }, 48000);
        var f = new Feeder();
        f.Run(tg, 7);
        T.Check(tg.Current.Group == SceneGroup.Vocal, $"vocal scene before the reset ({tg.Current.Group})");

        // AI が計算している間に取り直す: その結果（歌）は使わない
        gate.Reset();
        int guard = 0;
        while (!tg.Busy && guard++ < 1000) f.Hop(tg);
        T.Check(tg.Busy, "an inference is in flight");
        int gen0 = tg.Generation;
        var shown = tg.Current;
        tg.Reset();
        f.Hop(tg);   // 計算中なので、取り直しの回は次の機会に（見えている場面はそのまま）
        T.Check(ReferenceEquals(tg.Current, shown), "while the old inference is in flight, the shown scene stays (no jump)");
        gate.Set();
        WaitIdle(tg);
        T.Check(ReferenceEquals(tg.Current, shown), "the result computed before the reset is dropped");
        // 次の AI の回で、見えている歌の場面（移り終わっている）から、ゆっくり場面なしへ
        double tReset = f.T;
        int guard2 = 0;
        while (tg.Current.Group != SceneGroup.None && guard2++ < 200) f.Run(tg, 0.05);
        var after = tg.Current;
        T.Check(after.Group == SceneGroup.None && after.From == SceneGroup.Vocal && after.SwitchT >= tReset - 1e-9 && after.SwitchT < tReset + 1.0,
            $"Reset fades from the shown Vocal to None at the next AI window ({after.SwitchT - tReset:F2} s after the reset)");
        tg.Deliver(new MusicScene(SceneGroup.Brass, SceneGroup.None, f.T, 1), gen0);
        T.Check(tg.Current.Group == SceneGroup.None, "Deliver with the old generation is ignored");

        // 新しい回の結果は使う（場面なしへの移り変わりが終わってから）
        f.Run(tg, 8);
        T.Check(tg.Current.Group == SceneGroup.Vocal, $"new results after the reset are used again ({tg.Current.Group})");
    }

    // ================================================================
    // A9 光の描画への受け渡し

    public static void TestSceneReachesTheEffects()
    {
        var dev = new RgbDevice(0, "Strip", 24, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var scene = new MusicScene(SceneGroup.Vocal, SceneGroup.None, T0, 0.5f);
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var input = new RenderInput { Cfg = cfg, Music = ResolvedMusic.Default, Device = new RgbDeviceSettings { Name = dev.Name }, Scene = scene };
        var output = new Rgb[dev.LedCount];
        for (int i = 0; i < 3; i++)
        {
            input.T = input.TEval = T0 + 1 + i / 60.0;
            input.TPrevEval = input.T - 1 / 60.0;
            input.Dt = 1 / 60.0;
            r.Render(input, output);
        }
        T.Check(ReferenceEquals(r.Context.Scene, scene), "RenderInput.Scene reaches EffectContext.Scene");
        T.Near(scene.Progress(T0 + MusicScene.FadeSec / 2), 0.5, 1e-6, "Progress is 0.5 halfway through the fade");
        T.Check(scene.Progress(T0 - 1) == 0 && scene.Progress(T0 + MusicScene.FadeSec) == 1, "Progress is 0 before and 1 after the fade");
        T.Check(MusicScene.None.Progress(T0) == 1, "None is fully settled");
        input.Scene = null!;
        r.Render(input, output);
        T.Check(ReferenceEquals(r.Context.Scene, MusicScene.None), "a missing scene counts as None");

        // 分析のスレッドが音を流している間に、ほかのスレッドから場面を読める
        var e = new AudioEngine(48000);
        using var tg = new SceneTagger(VocalScores, 48000);
        e.Tagger = tg;
        T.Check(ReferenceEquals(new AudioEngine(48000).Scene, MusicScene.None), "engine without a tagger reports None");
        var stop = false;
        Exception? err = null;
        var pusher = new Thread(() =>
        {
            try
            {
                double t = T0;
                int pos = 0;
                while (!Volatile.Read(ref stop))
                {
                    if (pos + 480 > Noise.Length) pos = 0;
                    e.Push(Noise.AsSpan(pos, 480), t);
                    pos += 480;
                    t += 0.01;
                    if (pos % 4800 == 0) Thread.Sleep(1);
                }
            }
            catch (Exception ex) { err = ex; }
        }) { IsBackground = true };
        pusher.Start();
        var seen = new HashSet<SceneGroup>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && !seen.Contains(SceneGroup.Vocal))
        {
            var s = e.Scene;
            seen.Add(s.Group);
            if (!Enum.IsDefined(typeof(SceneGroup), s.Group)) break;
        }
        Volatile.Write(ref stop, true);
        pusher.Join(2000);
        T.Check(err is null, $"pushing audio while reading the scene works ({err?.Message ?? "ok"})");
        T.Check(seen.Contains(SceneGroup.Vocal) && seen.All(g => Enum.IsDefined(typeof(SceneGroup), g)), $"the scene read from another thread is valid and reaches Vocal ({string.Join(",", seen)})");
    }

    // ================================================================
    // 設定

    public static void TestConfigDefaultIsOff()
    {
        var cfg = System.Text.Json.JsonSerializer.Deserialize<RgbConfig>("{\"Enabled\":true,\"Mode\":\"Music\",\"MusicAi\":true}")!;
        T.Check(!cfg.MusicScenes, "an old config without MusicScenes loads with the instrument AI off");
        T.Check(!new RgbConfig().MusicScenes, "new configs start with it off");
        cfg.MusicScenes = true;
        var back = System.Text.Json.JsonSerializer.Deserialize<RgbConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        T.Check(back.MusicScenes, "true survives a save/load round trip");
    }

    // ================================================================
    // A10 ライセンスの説明

    public static void TestLicenceAndPrivacyDocs()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        T.Check(d is not null, "repository root found");
        if (d is null) return;
        string Read(string rel) => File.Exists(Path.Combine(d.FullName, rel)) ? File.ReadAllText(Path.Combine(d.FullName, rel)) : "";
        string credits = Read("CREDITS.txt");
        T.Check(credits.Contains("YAMNet") && credits.Contains("Apache"), "CREDITS.txt lists YAMNet and its Apache licence");
        string lic = Read(Path.Combine("Distribution", "licenses", "YAMNet-LICENSE.txt"));
        T.Check(lic.Contains("Apache License") && lic.Contains("END OF TERMS AND CONDITIONS") && lic.Contains(Yamnet.Sha256),
            "Distribution/licenses/YAMNet-LICENSE.txt has the full Apache-2.0 text and the model's SHA-256");
        T.Check(Read(Path.Combine("Distribution", "licenses", "THIRD-PARTY-NOTICES.txt")).Contains("YAMNet"), "THIRD-PARTY-NOTICES.txt mentions YAMNet");
        string privacy = Read("PRIVACY.md");
        T.Check(privacy.Contains("YAMNet") && privacy.Contains("楽器の AI"), "PRIVACY.md describes the instrument AI (English and Japanese)");
    }

    // ================================================================
    // A11 実際の曲でしきい値を合わせる（SCENE_WAV=フォルダー。数字だけを出し、ファイル名は出さない）

    public static void TestSceneDiagnosticsOnWavFolder()
    {
        string? dir = Environment.GetEnvironmentVariable("SCENE_WAV");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            T.Skip("SCENE_WAV is not set (folder of .wav files for calibrating the instrument AI)");
            return;
        }
        using var y = Yamnet.TryCreate(out string? err);
        if (y is null) { T.Skip("model does not load: " + err); return; }
        var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToList();
        T.Info($"{files.Count} files");
        int idx = 0;
        foreach (var f in files)
        {
            idx++;
            float[] mono;
            int fs;
            try { mono = MusicWavTests.ReadWavMono(f, out fs); }
            catch (Exception) { T.Info($"#{idx}: cannot read"); continue; }
            var r = RunTagger(y, mono, fs);
            double len = mono.Length / (double)fs;
            // 場面ごとの時間の割合（場面が変わった時刻から次の場面まで）
            var share = new Dictionary<SceneGroup, double>();
            var cur = SceneGroup.None;
            double from = 0;
            foreach (var (t, s) in r.Scenes.Append((len, MusicScene.None)))
            {
                double tt = Math.Clamp(t, 0, len);
                share[cur] = share.GetValueOrDefault(cur) + Math.Max(0, tt - from);
                cur = s.Group;
                from = tt;
            }
            string pct = string.Join(" ", share.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {100 * kv.Value / len:F0}%"));
            int switches = r.Scenes.Count;
            string Pq(int g)
            {
                var v = r.Windows.Select(w => (double)w.Ema[g]).OrderBy(x => x).ToList();
                return v.Count == 0 ? "-" : $"{v[v.Count / 2]:F2}/{v[(int)(v.Count * 0.9)]:F2}";
            }
            T.Info($"#{idx}: {len:F0} s, {r.Inferences} windows, {switches * 60 / Math.Max(1, len):F1} switches/min; {pct}");
            T.Info($"     EMA p50/p90: " + string.Join(" ", Enumerable.Range(0, SceneClasses.Count).Select(g => $"{SceneClasses.Names[g]} {Pq(g)}")));
        }
    }
}
