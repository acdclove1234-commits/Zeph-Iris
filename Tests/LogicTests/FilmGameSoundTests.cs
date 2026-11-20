// ==================================================
// FilmGameSoundTests.cs - 映画・ゲームの音でも、どの光り方も音に連動して光ることを確かめる（合成した音。録音がなくても動く）
//   プリセット「映画・ゲーム」をなくしたので、10 種類の光り方のどれでも、テンポのない音（せりふ・銃声・爆発・BGM の盛り上がり・
//   静かな場面）に合わせて、静かなところで暗く、大きな音で明るくなり、ちらつかないことを見る。
//   ModeSyncBench と同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描く）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;
using static LogicTests.ModeSyncBenchTests;

namespace LogicTests;

public static class FilmGameSoundTests
{
    private static (float[] Buf, int Fs)? _clip;

    /// <summary>
    /// 映画・ゲームに似た 70 秒の音（テンポなし）:
    ///   0〜70 秒 ずっと小さな環境音、0〜8 秒 静かな BGM（数えない）、8〜20 秒 せりふ、20〜34 秒 銃声と爆発（低い地鳴り）、
    ///   34〜44 秒 小さなせりふと足音、44〜62 秒 BGM が盛り上がり、ときどき大きな打撃音（ドーン）、62〜70 秒 静か
    /// </summary>
    public static (float[] Buf, int Fs) Clip()
    {
        if (_clip is { } c) return c;
        var g = new SignalGen(48000, 70, seed: 7);
        var rng = new Random(7);
        g.Pink(-52, 0, 70);
        g.PadChord(0, 8, new[] { 220.0, 261.6, 329.6 }, amp: 0.02);
        // せりふ: 1.6 秒しゃべって 0.9 秒あける（音の高さは話し声くらい）
        double[] speech = { 140, 175, 160, 190, 150, 210, 130, 170 };
        for (double t = 8; t < 19.5; t += 2.5) g.VocalTone(t, t + 1.6, speech, 0.2, amp: 0.12, vibratoCents: 10);
        // 銃声（4〜6 発の連射）と爆発（ドン + 0.35 秒のノイズ）。間隔はばらばら
        g.Pink(-36, 20, 34);
        double tg = 20.4;
        while (tg < 33.5)
        {
            int shots = 4 + rng.Next(3);
            for (int k = 0; k < shots; k++) g.Snare(tg + k * 0.085, 0.45);
            tg += shots * 0.085 + 0.6 + rng.NextDouble() * 1.4;
        }
        foreach (double te in new[] { 21.3, 24.1, 26.9, 29.2, 32.6 })
        {
            g.Kick(te, 0.95);
            g.White(-20, te, te + 0.35);
        }
        // 小さなせりふと足音
        for (double t = 34.5; t < 43.5; t += 3) g.VocalTone(t, t + 1.8, speech, 0.2, amp: 0.06, vibratoCents: 10);
        for (double t = 34.3; t < 44; t += 0.5 + rng.NextDouble() * 0.15) g.Snare(t, 0.05);
        // BGM の盛り上がりと打撃音
        g.PadChord(44, 52, new[] { 146.8, 220.0, 293.7 }, amp: 0.03);
        g.PadChord(52, 62, new[] { 146.8, 220.0, 293.7, 370.0 }, amp: 0.07);
        foreach (double tb in new[] { 50.0, 53.4, 56.3, 58.7, 60.9 })
        {
            g.Kick(tb, 0.9);
            g.BrassTone(tb, tb + 1.2, new[] { 73.4 }, 1.2, amp: 0.14);
        }
        _clip = (g.Buf, g.Fs);
        return _clip.Value;
    }

    /// <summary>光り方ごとの値（24 個のテープ・グラボ）。同じ条件は 1 回だけ計算する</summary>
    private static readonly Dictionary<string, Dictionary<string, ModeScore>> Cache = new();

    private static Dictionary<string, ModeScore> Scores(RgbDevice? device = null)
    {
        string key = device?.Name ?? "strip";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var (buf, fs) = Clip();
        var list = RunSong(buf, fs, MusicStyles.All.ToArray(), out _, out _, device);
        return Cache[key] = list.ToDictionary(s => s.Mode);
    }

    /// <summary>
    /// どの光り方も、映画・ゲームの音の大きさに合わせて明るさが変わる（0.5 秒でならした明るさと音の大きさの相関 loud。
    /// 入れたときの値は 0.48〜0.78）。音の立ち上がり（銃声・爆発・打撃音）でも
    /// すぐ明るくなる（resp 0.33〜0.72、遅れ 0 ms）。ちらつかない（MusicFlickerTests と同じ上限）
    /// </summary>
    public static void TestEveryStyleFollowsFilmAndGameSound()
    {
        foreach (var s in Scores().Values)
        {
            T.Info(Format(s));
            T.Check(s.Loud >= 0.4, $"{s.Mode}: brighter with louder film/game sound, darker in quiet scenes (loud {s.Loud:F2})");
            T.Check(s.Range >= 0.05, $"{s.Mode}: the brightness changes enough to see (range {s.Range:F2})");
            T.Check(s.Resp >= 0.25 && s.LagMs <= 50, $"{s.Mode}: lights up on shots, explosions and hits (resp {s.Resp:F2}, lag {s.LagMs:F0} ms)");
            MusicFlickerTests.CheckSteady("film/game", s);
        }
    }

    /// <summary>
    /// 更新の遅いデバイス（メモリ。遅い版の光り方で描く）でも、どの光り方も映画・ゲームの音の大きさに合わせて明るさが変わる
    /// （入れたときの値: loud 0.36〜0.70・range 0.17〜0.52）
    /// </summary>
    public static void TestSlowDevicesFollowFilmAndGameSound()
    {
        var dram = new RgbDevice(5, "DRAM", 8, RgbDevice.TypeDram, null, "I2C: dram");
        foreach (var s in Scores(dram).Values)
        {
            T.Info(Format(s));
            T.Check(s.Loud >= 0.3, $"slow {s.Mode}: brighter with louder film/game sound (loud {s.Loud:F2})");
            T.Check(s.Range >= 0.1, $"slow {s.Mode}: the brightness changes enough to see (range {s.Range:F2})");
            MusicFlickerTests.CheckSteady("slow film/game", s);
        }
    }
    /// <summary>
    /// グラボ（OpenRGB で書くグラボは明るさを一定にして、色だけ変える）の、0.5 秒ごとの「色が見て分かるほど変わった」割合。
    /// 本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描く。色は LED の平均（0〜1 の RGB）、変わった = 0.5 秒の中の色の差が 0.04 以上
    /// </summary>
    private static Dictionary<string, (double Loud, double Talk, double Quiet)> GpuChanges()
    {
        var (buf, fs) = Clip();
        var dev = new RgbDevice(2, "GPU", 12, RgbDevice.TypeGpu, new[] { 4, 8 });
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var lanes = MusicStyles.All.Select(m => (Mode: m, R: new DeviceRenderer(dev),
            In: new RenderInput { Cfg = cfg, Music = ResolvedMusic.Default with { Effect = m }, Device = new RgbDeviceSettings { Name = dev.Name } },
            Out: new Rgb[dev.LedCount], Colors: new List<(double R, double G, double B)>())).ToList();
        const double t0 = 1000.0, fps = 60;
        int chunk = fs / 100;
        double next = t0, last = double.NaN;
        for (int pos = 0; pos < buf.Length; pos += chunk)
        {
            int n = Math.Min(chunk, buf.Length - pos);
            engine.Push(buf.AsSpan(pos, n), ReadOnlySpan<float>.Empty, t0 + pos / (double)fs);
            double tAudio = t0 + (pos + n) / (double)fs;
            while (next <= tAudio)
            {
                foreach (var L in lanes)
                {
                    var input = L.In;
                    input.T = input.TEval = next;
                    input.TPrevEval = double.IsNaN(last) ? next : last;
                    input.Dt = double.IsNaN(last) ? 1 / fps : next - last;
                    input.Beat = engine.Beat;
                    input.Scene = engine.Scene;
                    input.Features = engine.Features;
                    engine.Features.Sample(next, input.Frame);
                    input.Events.Clear();
                    long cursor = L.Colors.Count == 0 ? 0 : _cursors[L.Mode];
                    engine.Events.Read(ref cursor, next, input.Events);
                    _cursors[L.Mode] = cursor;
                    L.R.Render(input, L.Out);
                    L.Colors.Add((L.Out.Average(c => c.R / 255.0), L.Out.Average(c => c.G / 255.0), L.Out.Average(c => c.B / 255.0)));
                }
                last = next;
                next += 1 / fps;
            }
        }
        double Share(List<(double R, double G, double B)> c, params (double From, double To)[] spans)
        {
            int win = (int)(0.5 * fps), changed = 0, total = 0;
            foreach (var (from, to) in spans)
            for (int a = (int)(from * fps); a + win <= Math.Min(c.Count, (int)(to * fps)); a += win)
            {
                double d = 0;
                for (int i = a + 1; i < a + win; i++)
                    d = Math.Max(d, Math.Abs(c[i].R - c[a].R) + Math.Abs(c[i].G - c[a].G) + Math.Abs(c[i].B - c[a].B));
                total++;
                if (d >= 0.04 * 3) changed++;
            }
            return total == 0 ? 0 : changed / (double)total;
        }
        return lanes.ToDictionary(L => L.Mode, L => (Share(L.Colors, (20, 34), (44, 62)), Share(L.Colors, (8, 20)), Share(L.Colors, (64, 70))));
    }
    private static readonly Dictionary<string, long> _cursors = new();

    /// <summary>
    /// OpenRGB で書くグラボ（明るさは一定で、色だけ変える）でも、どの光り方も爆発・BGM の打撃音で色が変わり、
    /// 静かな場面（環境音だけ）より大きな音の場面のほうがよく変わる。
    /// テンポのない音では、ビートで色が進む光り方は強いキック（爆発・打撃音）で色を進める（色は「強さ」の間隔ごと。
    /// 「ふつう」は 2 回に 1 回）ので、入れたときの値は大きな音の場面の 9%（ビートで色替えなど）〜81%（スペクトラム）
    /// </summary>
    public static void TestGraphicsCardsFollowFilmAndGameSound()
    {
        foreach (var (mode, (loud, talk, quiet)) in GpuChanges())
        {
            T.Info($"{mode,-9} loud scenes {loud:P0} of 0.5 s windows change color, dialogue {talk:P0}, quiet {quiet:P0}");
            T.Check(loud >= 0.05 && loud > quiet, $"graphics card {mode}: the color changes with loud film/game sound ({loud:P0}) more than in quiet scenes ({quiet:P0})");
        }
    }
}
