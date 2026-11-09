// ==================================================
// BandTimingTests.cs - 周波数の帯（スペクトラム）が、音に遅れずに動くか・光る時刻が拍からずれないかを測る
//   ・帯ごとの遅れ: 静かなノイズの上に短い音（トーンバースト）を鳴らし、帯が半分まで上がるまでの時間を測る
//   ・光る時刻: 合成したドラムの曲を、アプリと同じ時間の流れ（MusicTiming.Realtime）で光らせ、本当の拍とのずれを測る
// 帯の数を変える前と後で同じ数字を出して比べられるよう、調査（MUSIC_BANDTIMING=1）は前の版にもある API だけを使う
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class BandTiming
{
    /// <summary>
    /// 1 つの周波数の結果（ミリ秒は、音が鳴り始めてから）:
    /// Lag50 = その帯の大きさ（Band）が半分まで上がるまで・PunchPeak = 立ち上がりを強調した値が山になるまで・
    /// GroupPeak = まとめた帯（BassE / MidE / TrebleE のうち大きく動いたもの）が山になるまで
    /// </summary>
    public sealed record BandLag(double Freq, int Band, double Lag50Ms, double PunchPeakMs, string Group, double GroupPeakMs);

    /// <summary>周波数 f を受け持つ帯の番号</summary>
    public static int BandOf(double f)
    {
        int k = 0;
        while (k < AudioFrame.BandCount - 1 && BandAnalyzer.Edge(k + 1) <= f) k++;
        return k;
    }

    /// <summary>トーンバースト（1 ミリ秒で立ち上がり、5 ミリ秒で消える）を足す</summary>
    private static void Burst(SignalGen g, double freq, double amp, double from, double len)
    {
        int a = (int)Math.Round(from * g.Fs), b = Math.Min(g.Buf.Length, (int)Math.Round((from + len) * g.Fs));
        int up = g.Fs / 1000, down = 5 * g.Fs / 1000;
        for (int i = a; i < b; i++)
        {
            double env = Math.Min(1, Math.Min((i - a) / (double)up, (b - i) / (double)down));
            g.Buf[i] += (float)(amp * env * Math.Sin(2 * Math.PI * freq * (i - a) / g.Fs));
        }
    }

    /// <summary>周波数 freq のトーンバーストを 0.6 秒ごとに 18 回鳴らし、遅れの中央値を求める</summary>
    public static BandLag Measure(double freq, int fs = 48000)
    {
        const double total = 14;
        var g = new SignalGen(fs, total, seed: 3);
        g.White(-42, 0, total); // 無音判定の門を開けたままにする
        var onsets = new List<double>();
        // 0.6037 秒おき: 分析の区切り（10.7 ミリ秒）に対して、毎回ちがう位置で鳴る
        for (double t = 3.0; t < total - 0.5; t += 0.6037)
        {
            Burst(g, freq, 0.25, t, 0.12);
            onsets.Add(t);
        }
        var run = new EngineRun(new AudioEngine(fs)).Feed(g.Buf);
        int k = BandOf(freq);
        var lag50 = new List<double>();
        var punchPk = new List<double>();
        var groupPk = new List<double>();
        var groupRise = new double[3];
        var frames = run.Frames;
        foreach (double t0 in onsets)
        {
            double S(AudioFrame f) => f.T - run.T0 - t0;
            var win = frames.Where(f => S(f) >= -0.03 && S(f) <= 0.30).ToList();
            var before = win.Where(f => S(f) < 0).ToList();
            var after = win.Where(f => S(f) >= 0 && S(f) <= 0.2).ToList();
            if (before.Count == 0 || after.Count == 0) continue;
            double pre = before[^1].Band[k];
            double peak = after.Max(f => f.Band[k]);
            double half = pre + 0.5 * (peak - pre);
            if (after.FirstOrDefault(f => f.Band[k] >= half) is { } hit) lag50.Add(S(hit) * 1000);
            punchPk.Add(S(after.MaxBy(f => f.Punch[k])!) * 1000);
            Func<AudioFrame, float>[] gsel = { f => f.BassE, f => f.MidE, f => f.TrebleE };
            for (int j = 0; j < 3; j++) groupRise[j] += after.Max(gsel[j]) - gsel[j](before[^1]);
        }
        int gi = Array.IndexOf(groupRise, groupRise.Max());
        string[] names = { "BassE", "MidE", "TrebleE" };
        Func<AudioFrame, float>[] sel = { f => f.BassE, f => f.MidE, f => f.TrebleE };
        // まとめた帯は、半分まで上がった時刻（鳴り続ける音では山の位置がはっきりしないため）
        foreach (double t0 in onsets)
        {
            double S(AudioFrame f) => f.T - run.T0 - t0;
            var before = frames.Where(f => S(f) >= -0.03 && S(f) < 0).ToList();
            var after = frames.Where(f => S(f) >= 0 && S(f) <= 0.2).ToList();
            if (before.Count == 0 || after.Count == 0) continue;
            double pre = sel[gi](before[^1]), peak = after.Max(sel[gi]);
            if (after.FirstOrDefault(f => sel[gi](f) >= pre + 0.5 * (peak - pre)) is { } hit) groupPk.Add(S(hit) * 1000);
        }
        return new BandLag(freq, k, MusicTiming.Quantile(lag50, 0.5), MusicTiming.Quantile(punchPk, 0.5), names[gi], MusicTiming.Quantile(groupPk, 0.5));
    }

    /// <summary>測る周波数（Hz）: 超低音〜高音。帯の境目の近く（2.5 kHz など）も入れる</summary>
    public static readonly double[] Freqs = { 35, 50, 80, 120, 200, 300, 600, 1000, 1500, 2000, 2500, 3200, 5000, 8000, 12000 };

    /// <summary>光る時刻を測る光り方</summary>
    public static readonly string[] Styles =
    {
        MusicStyles.BeatHue, MusicStyles.Pulse, MusicStyles.Mix, MusicStyles.Spectrum, MusicStyles.Meter, MusicStyles.Energy, MusicStyles.Strobe,
    };

    /// <summary>
    /// 光る時刻の結果: Hit = 拍（8 秒以降）のうち、−40〜+60 ミリ秒に光り始めがあった割合・HitLag = そのずれの中央値（+ = 光が後）・
    /// Early = 拍の 150〜30 ミリ秒前の光り始め（拍より早く光った）の数・Total = 光り始めの数・Errs = 拍の近く（±150 ミリ秒）の光り始めのずれ
    /// </summary>
    public sealed record Flash(int Beats, double Hit, double HitLagMs, int Early, int Total, List<double> Errs);

    /// <summary>1 つの光り方で、合成した曲（funk を除く。name を指定したらその曲だけ）を、アプリと同じ時間の流れで光らせて測る</summary>
    public static Flash FlashStats(string style, string? name = null)
    {
        var errs = new List<double>();
        var hitLags = new List<double>();
        int nBeats = 0, hits = 0, early = 0, total = 0;
        foreach (var c in MusicBench.Cases())
        {
            if (c.Name.StartsWith("funk") || (name is not null && c.Name != name)) continue;
            var run = MusicTiming.Realtime(c.Gen.Buf, c.Gen.Fs, new RgbConfig { Mode = RgbModes.Music, MusicStyle = style });
            var beats = c.Beats.Select(b => b + 1000).Where(b => b >= 1008 && b <= 1000 + c.Gen.Seconds - 0.2).ToList();
            var rises = MusicTiming.Rises(run, 1000 + 8).Select(r => r.T).ToList();
            total += rises.Count;
            foreach (double b in beats)
            {
                nBeats++;
                var near = rises.Where(t => t - b >= -0.040 && t - b <= 0.060).ToList();
                if (near.Count == 0) continue;
                hits++;
                hitLags.Add((near.MinBy(t => Math.Abs(t - b)) - b) * 1000);
            }
            var all = c.Beats.Select(b => b + 1000).ToList();
            foreach (double t in rises)
            {
                double err = MusicTiming.ErrTo(all, t);
                if (Math.Abs(err) <= 0.150) errs.Add(err * 1000);
                if (err >= -0.150 && err < -0.030) early++;
            }
        }
        return new Flash(nBeats, hits / (double)Math.Max(1, nBeats), MusicTiming.Quantile(hitLags, 0.5), early, total, errs);
    }

    public static string Summary(Flash f) =>
        $"beats lit {f.Hit,4:P0} of {f.Beats,4} (lag median {f.HitLagMs,5:F1} ms), flashes {f.Total,4}, early (30–150 ms before a beat) {f.Early,3}, " +
        $"near-beat error median {MusicTiming.Quantile(f.Errs, 0.5),5:F1} ms";
}

public static class BandTimingTests
{
    /// <summary>
    /// 帯が音に遅れずに動く: 2 kHz までの帯（大きい FFT）は 35 ミリ秒以内、4 kHz からの帯（小さい FFT）は 25 ミリ秒以内に半分まで上がる。
    /// まとめた帯（中音・高音）の中で、帯によって遅れが分析 1 回分（10.7 ミリ秒）より大きく違わない
    /// </summary>
    public static void TestBandResponseTiming()
    {
        var all = BandTiming.Freqs.Select(f => BandTiming.Measure(f)).ToList();
        foreach (var r in all)
            T.Info($"{r.Freq,6:F0} Hz → band {r.Band} ({BandAnalyzer.Label(r.Band)}): half-rise {r.Lag50Ms,5:F1} ms, punch peak {r.PunchPeakMs,5:F1} ms, {r.Group} peak {r.GroupPeakMs,5:F1} ms");
        foreach (var r in all)
        {
            double limit = r.Band < BandAnalyzer.SplitBand ? 35 : 25;
            T.Check(r.Lag50Ms <= limit, $"{r.Freq:F0} Hz: band reaches half within {limit} ms ({r.Lag50Ms:F1} ms)");
        }
        var mid = all.Where(r => r.Freq >= 250 && r.Freq <= 2500).Select(r => r.Lag50Ms).ToList();
        var high = all.Where(r => r.Freq >= 3200).Select(r => r.Lag50Ms).ToList();
        T.Check(mid.Max() - mid.Min() <= 10.7, $"mid bands (250 Hz–2 kHz) move together ({mid.Min():F1}..{mid.Max():F1} ms)");
        T.Check(high.Max() - high.Min() <= 10.7, $"treble bands (4–16 kHz) move together ({high.Min():F1}..{high.Max():F1} ms)");
    }

    /// <summary>
    /// 拍で光る: 合成した曲を、アプリと同じ時間の流れで光らせたとき、ビートで色替え・パルスは拍の 78% 以上で光り始め、
    /// 遅れは 0〜25 ミリ秒（中央値）。まとめた帯（低音など）を 10 帯から作ったときは 65〜67% に下がった（帯の数を変えるときの見張り）
    /// </summary>
    public static void TestBeatsStillFlash()
    {
        foreach (string style in new[] { MusicStyles.BeatHue, MusicStyles.Pulse })
        {
            var f = BandTiming.FlashStats(style);
            T.Info($"{style}: {BandTiming.Summary(f)}");
            T.Check(f.Hit >= 0.78, $"{style}: most beats light up ({f.Hit:P0})");
            T.Check(f.HitLagMs >= 0 && f.HitLagMs <= 25, $"{style}: lights follow the beat closely ({f.HitLagMs:F1} ms)");
        }
    }

    /// <summary>
    /// 調査（MUSIC_BANDTIMING=1）:帯の遅れ・キックなどの出来事・光る時刻を、帯の数を変える前と後で比べるための数字を出す。
    /// 前の版のソースにこのファイルを置いて同じように動かし、出力を見比べる
    /// </summary>
    public static void TestBandTimingDiag()
    {
        if (Environment.GetEnvironmentVariable("MUSIC_BANDTIMING") != "1") return;
        T.Info($"bands: {AudioFrame.BandCount}");
        string part = Environment.GetEnvironmentVariable("MUSIC_BANDTIMING_PART") ?? "all";
        if (part is "all" or "band")
        {
            foreach (double f in BandTiming.Freqs)
            {
                var r = BandTiming.Measure(f);
                T.Info($"BAND {r.Freq,6:F0} Hz → band {r.Band,2} ({BandAnalyzer.Edge(r.Band),6:F0}–{BandAnalyzer.Edge(r.Band + 1),6:F0} Hz): half-rise {r.Lag50Ms,5:F1} ms, punch peak {r.PunchPeakMs,5:F1} ms, {r.Group,-7} half-rise {r.GroupPeakMs,5:F1} ms");
            }
        }
        if (part is "flash" or "song")
        {
            foreach (string style in BandTiming.Styles)
            {
                T.Info($"FLASH {style,-9} {BandTiming.Summary(BandTiming.FlashStats(style))}");
                if (part != "song") continue;
                foreach (var c in MusicBench.Cases().Where(c => !c.Name.StartsWith("funk")))
                    T.Info($"  SONG {c.Name,-22} {BandTiming.Summary(BandTiming.FlashStats(style, c.Name))}");
            }
            return;
        }
        // キック・スネア・ハイハットなどの出来事とテンポ（帯の数に関係なく、同じになるはず）
        foreach (var c in MusicBench.Cases())
        {
            var run = new EngineRun(new AudioEngine(c.Gen.Fs)).Feed(c.Gen.Buf);
            string kinds = string.Join(" ", run.Events.GroupBy(e => e.Kind).OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Count()}"));
            double sum = run.Events.Sum(e => Math.Round((e.T - run.T0) * 1e6) / 1e6 + e.Strength);
            var last = run.Beats[^1];
            T.Info($"EVENTS {c.Name,-16} {kinds}  checksum {sum:F6}  locked {run.Beats.Count(b => b.Locked)} frames, final {(last.Locked && last.Period > 0 ? 60 / last.Period : 0):F2} BPM");
        }
        foreach (string style in BandTiming.Styles)
            T.Info($"FLASH {style,-9} {BandTiming.Summary(BandTiming.FlashStats(style))}");
    }
}
