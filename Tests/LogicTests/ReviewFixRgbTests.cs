// ==================================================
// ReviewFixRgbTests.cs - レビューで見つかった LED まわりの不具合の修正を確かめるロジックテスト
//                        (分析結果の記録を渡す・ビートの予測が書き換わっても合図を 1 回にする・間引いて送るときはディザリングしない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class ReviewFixRgbTests
{
    private const double P = 1.0 / 60;

    private static RgbDevice Fast(int n = 4) => new(3, "Strip", n, 4, null, "HID: x");
    private static RgbDevice Slow() => new(0, "GPU", 4, RgbDevice.TypeGpu, null, "I2C: /dev/i2c-3");

    private static RgbDeviceChannel Channel(RgbDevice dev, RgbConfig cfg, RgbDeviceSettings? ds = null)
    {
        var ch = new RgbDeviceChannel(dev, 0);
        ch.Configure(cfg, MusicTuning.Resolve(cfg), ds ?? new RgbDeviceSettings { Name = dev.Name }, false);
        return ch;
    }

    /// <summary>本当のビート（0.1 秒から 0.5 秒ごと）を、予測が少しずつ揺れながら毎コマ作り直される形で返す</summary>
    private static BeatState Wobbly(double t, double amp)
    {
        const double period = 0.5, start = 0.1;
        double jitter = amp * Math.Sin(2 * Math.PI * 2.7 * t) + amp * 0.5 * Math.Sin(2 * Math.PI * 11.3 * t);
        long k = (long)Math.Floor((t - start) / period);
        return new BeatState(period, start + k * period + jitter, k, 0.9, true, 0);
    }

    // ---- #13: RgbDeviceChannel.Prepare が分析結果の記録を RenderInput.Features に入れる ----

    public static void TestPrepareSetsFeatures()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var ch = Channel(Fast(), cfg);
        var ring = new FeatureRing();
        var f = new AudioFrame();
        for (int i = 1; i <= 50; i++)
        {
            f.T = i * 512.0 / 48000;
            ring.Write(f);
        }
        ch.Prepare(0.2, P, ring, new EventRing(), BeatState.None, null, false, false);
        T.Check(ReferenceEquals(ch.Input.Features, ring), "Prepare passes the feature ring to the renderer");
        T.Check(ch.Render(), "renders with the feature ring");
        ch.Prepare(0.2 + P, P, null, null, BeatState.None, null, false, false);
        T.Check(ch.Input.Features is null, "no engine → Features is null");
    }

    // ---- #14: ビートの予測が書き換わっても、同じビートを 2 回合図にしない・取りこぼさない ----

    public static void TestBeatCrossingSurvivesRepublish()
    {
        // 後ろへずれる: 10.500 のビートを合図にした後、予測が 10.510 に動く → 2 回目は出さない
        var x = new BeatCrossing();
        var a = new BeatState(0.5, 10.500, 20, 1, true, 0);
        T.Check(x.Step(a, true, 10.490, 10.505, out double tb) && Math.Abs(tb - 10.5) < 1e-9, "beat at 10.500 fires");
        var b = new BeatState(0.5, 10.510, 20, 1, true, 0);
        T.Check(b.Crossed(10.505, 10.522), "(old check would fire the same beat again)");
        T.Check(!x.Step(b, true, 10.505, 10.522, out _), "republished grid (+10 ms): the same beat does not fire twice");
        T.Check(x.Step(b, true, 11.000, 11.015, out tb) && Math.Abs(tb - 11.01) < 1e-9, "the next beat fires normally");

        // 前へずれる: まだ来ていない 12.010 のビートが 11.995 に動き、t0 = 12.000 より前になる → 次のコマで出す
        var y = new BeatCrossing();
        var c = new BeatState(0.5, 11.510, 23, 1, true, 0);
        T.Check(y.Step(c, true, 11.500, 11.515, out _), "beat at 11.510 fires");
        T.Check(!y.Step(c, true, 11.985, 12.000, out _), "beat at 12.010 not reached yet");
        var d = new BeatState(0.5, 11.995, 24, 1, true, 0);
        T.Check(!d.Crossed(12.000, 12.015), "(old check would skip this beat)");
        T.Check(y.Step(d, true, 12.000, 12.015, out tb) && Math.Abs(tb - 11.995) < 1e-9, "republished grid (−15 ms): the beat still fires once");
        T.Check(!y.Step(d, true, 12.015, 12.030, out _), "and only once");

        // テンポを失ったら覚えを捨てる。使わない・時刻が進まないコマでは出さない
        T.Check(!y.Step(BeatState.None, true, 12.030, 12.045, out _) && double.IsNaN(y.LastFiredT), "no tempo → forget");
        T.Check(!y.Step(d, false, 12.4, 12.6, out _), "inactive → never fires");
        T.Check(!y.Step(d, true, 12.6, 12.6, out _), "time not advancing → never fires");

        // 予測が毎コマ揺れても、1 拍に 1 回だけ（20 秒で 40 回）
        foreach (double amp in new[] { 0.0, 0.004, 0.012 })
        {
            var z = new BeatCrossing();
            int fires = 0, oldFires = 0;
            double lastFire = double.NegativeInfinity, minGap = double.PositiveInfinity;
            for (int k = 1; k <= 1200; k++)
            {
                double t0 = (k - 1) * P, t1 = k * P;
                var s = Wobbly(t1, amp);
                if (s.Crossed(t0, t1)) oldFires++;
                if (!z.Step(s, true, t0, t1, out _)) continue;
                fires++;
                minGap = Math.Min(minGap, t1 - lastFire);
                lastFire = t1;
            }
            T.Info($"jitter ±{amp * 1000:F0} ms: {fires} fires (old check {oldFires})");
            T.Check(fires == 40, $"jitter ±{amp * 1000:F0} ms: one fire per beat ({fires}/40)");
            T.Check(minGap >= 0.4, $"jitter ±{amp * 1000:F0} ms: fires are a beat apart (min {minGap * 1000:F0} ms)");
        }
    }

    public static void TestBeatDriverTriggersOncePerBeat()
    {
        var music = ResolvedMusic.Default with { TempoMode = TempoModes.Auto, ColorStepBeats = 1 };
        var driver = new BeatDriver();
        var c = new EffectContext { Music = music };
        int triggers = 0;
        for (int k = 1; k <= 1200; k++)
        {
            double t0 = (k - 1) * P, t1 = k * P;
            c.Beat = Wobbly(t1, 0.012);
            c.Events.Clear();
            driver.Update(c, t0, t1);
            if (c.Trigger) triggers++;
        }
        T.Check(triggers == 40, $"BeatDriver: one trigger per beat while the prediction wobbles ({triggers}/40)");
    }

    public static void TestBeatCueOncePerBeat()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var ch = Channel(Slow(), cfg);
        var features = new FeatureRing();
        var events = new EventRing();
        int cues = 0;
        for (int k = 0; k <= 1200; k++)
        {
            double t = k * P;
            ch.Prepare(t, P, features, events, Wobbly(t + 0.02, 0.012), null, false, false);
            if (ch.BeatCue) cues++;
        }
        T.Check(cues == 40, $"slow device: one beat cue per beat while the prediction wobbles ({cues}/40)");
    }

    // ---- #15: 間引いて送るデバイスではディザリングしない ----

    public static void TestDitherOnlyWhenEveryFrameIsSent()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var features = new FeatureRing();
        var full = Channel(Fast(), cfg);
        full.Prepare(1, P, features, null, BeatState.None, null, false, false);
        T.Check(full.Input.AllowDither, "60 fps USB device sends every frame → dithering allowed");
        var capped = Channel(Fast(), cfg, new RgbDeviceSettings { Name = "Strip", MaxFps = 30 });
        capped.Prepare(1, P, features, null, BeatState.None, null, false, false);
        T.Check(!capped.Input.AllowDither, "USB device capped at 30 fps → no dithering");
        var slow = Channel(Slow(), cfg);
        slow.Prepare(1, P, features, null, BeatState.None, null, false, false);
        T.Check(!slow.Input.AllowDither, "slow device → no dithering");
        // 送るのが遅いと分かって送る速さを下げたら、ディザリングもやめる
        var governed = Channel(Fast(), cfg);
        for (int i = 0; i < 200; i++) governed.MarkSent(i * P, P, SendReason.Regular, 12);
        governed.Prepare(4, P, features, null, BeatState.None, null, false, false);
        T.Check(governed.EffectiveFps < RgbDeviceChannel.DitherMinFps && !governed.Input.AllowDither,
            $"governor lowered the rate to {governed.EffectiveFps:F0} fps → no dithering");

        // 描画: 音楽連動の暗い一定の色は、ディザリングを許しても許さなくても毎回同じ値
        // （6.15.7 までは許すとコマごとに 2・3・2・3 と揺れていた。暗い LED のちらつきに見えるので、前に送った値を保つ書き方にした）
        foreach (bool allow in new[] { true, false })
        {
            var r = new DeviceRenderer(Fast());
            var input = new RenderInput
            {
                Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = true, StaticColor = "#6A00FF" },
                Music = ResolvedMusic.Default with { Effect = MusicStyles.Mood, Palette = Palettes.Neon, MinBrightness = 0.05 },
                Device = new RgbDeviceSettings { Name = "Strip" },
                AllowDither = allow,
            };
            input.Frame.GateOpen = true;
            input.Frame.Activity = 1;
            var output = new Rgb[4];
            var values = new HashSet<byte>();
            double last = double.NaN;
            for (int i = 0; i < 30; i++)
            {
                double t = 1 + i * 1e-4;
                input.T = input.TEval = input.Frame.T = t;
                input.TPrevEval = double.IsNaN(last) ? t : last;
                input.Dt = double.IsNaN(last) ? 0 : t - last;
                r.Render(input, output);
                values.Add(output[0].B);
                last = t;
            }
            T.Check(values.Count == 1, $"music mode, AllowDither={allow}: no temporal dithering ({values.Count} distinct value(s) over 30 frames)");
        }
    }

    // ---- ARGB の電源が自分の光り方に戻る: 音楽連動で色が止まっても、USB には 0.2 秒ごとに送り続ける ----

    /// <summary>
    /// 音楽連動・無音（音が止まったら消灯）で 10 秒動かし、消灯しきった後（3 秒〜）の送った間隔のいちばん長いものと送った回数を返す
    /// </summary>
    private static (double MaxGap, int Writes) SilentMusicGaps(RgbDevice dev)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        cfg.Music.IdleEffect = IdleEffects.Off;
        cfg.Music.IdleDelaySec = 1;
        var ch = Channel(dev, cfg);
        double lastSend = double.NaN, maxGap = 0;
        int writes = 0;
        for (double t = 0; t < 10; t += P)
        {
            ch.Prepare(t, P, null, null, BeatState.None, null, false, false);
            if (!ch.Render()) continue;
            var reason = ch.ShouldSend(t, P, true);
            if (reason == SendReason.None) continue;
            ch.MarkSent(t, P, reason, 1);
            if (t < 3) continue;
            if (!double.IsNaN(lastSend)) maxGap = Math.Max(maxGap, t - lastSend);
            lastSend = t;
            writes++;
        }
        if (!double.IsNaN(lastSend)) maxGap = Math.Max(maxGap, 10 - lastSend);
        return (maxGap, writes);
    }

    public static void TestMusicKeepsArgbSignalAlive()
    {
        var board = new RgbDevice(2, "ASRock B860 Steel Legend WiFi", 34, 0, new[] { 1, 8, 8, 9, 8 }, "HID: x", "Direct", 3);
        var (gap, writes) = SilentMusicGaps(board);
        T.Check(gap <= RgbSync.KeepAliveSec + 2 * P, $"board: silent music is re-sent at least every {RgbSync.KeepAliveSec} s (longest gap {gap:F2} s)");
        T.Check(writes <= 7 / RgbSync.KeepAliveSec + 1, $"board: only the keep-alive is sent once dark ({writes} writes in 7 s)");
        var (_, slowWrites) = SilentMusicGaps(Slow());
        T.Check(slowWrites <= 1, $"GPU: still not re-sent while nothing changes ({slowWrites} writes in 7 s)");
    }
}
