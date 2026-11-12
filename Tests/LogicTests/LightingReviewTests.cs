// ==================================================
// LightingReviewTests.cs - v6.3 の点検で見つかった問題のテスト
//   (グラボへの小さな色の書き込み・音が止まったときのグラボ・温度のならし方の読む間隔と始め方・白だけのキーボード)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class LightingReviewTests
{
    private static RgbDevice Pny() => new(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }, "I2C: NVIDIA, address 0x60", "Direct", 7);
    private static RgbDevice Rx() => new(3, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }, "I2C", "Direct", 1);
    private static RgbDevice Board() => new(0, "ASRock B860 Steel Legend WiFi", 34, 0, new[] { 10, 24 }, "HID: x", "Direct", 3);

    /// <param name="Writes">数えた範囲の書き込み回数</param>
    /// <param name="Small">前に送った色との差が、どの LED も smallThreshold 以下だった書き込みの回数</param>
    private sealed record Result(int Writes, int Small);

    /// <summary>
    /// RgbSync の 1 コマと同じ順（Prepare → Render → ShouldSend → MarkSent）で、1 秒 60 コマで動かす。
    /// music なら 123 BPM でテンポをつかんだ音（音量が細かく揺れる）、そうでなければ無音。countFrom 秒より後を数える
    /// </summary>
    private static Result Run(RgbDevice dev, RgbConfig cfg, RgbDeviceSettings settings, bool music, double dur, double countFrom, int smallThreshold,
        List<double>? times = null)
    {
        var ch = new RgbDeviceChannel(dev, 0) { SharedColor = new SharedColor() };
        ch.Configure(cfg, MusicTuning.Resolve(cfg), settings, false);
        var ring = new FeatureRing();
        var f = new AudioFrame();
        var beat = new BeatState(60.0 / 123, 0.2, 0, 0.9, true, 0);
        var rng = new Random(7);
        const double Tick = 1.0 / 60;
        int writes = 0, small = 0;
        Rgb[]? last = null;
        for (double t = 0; t < dur; t += Tick)
        {
            if (music)
            {
                f.T = t;
                f.GateOpen = true;
                f.Activity = 1;
                f.LoudVU = (float)(0.4 + 0.5 * rng.NextDouble());
                f.CentroidHz = (float)(900 + 300 * Math.Sin(t));
                ring.Write(f);
            }
            ch.Prepare(t, Tick, music ? ring : null, null, music ? beat : BeatState.None, null, false, false);
            if (!ch.Render()) continue;
            var reason = ch.ShouldSend(t, Tick, true);
            if (reason == SendReason.None) continue;
            ch.MarkSent(t, Tick, reason, 1);
            var now = ch.CopyLastSent()!;
            if (t >= countFrom)
            {
                writes++;
                times?.Add(t);
                if (last is not null && RgbDeviceChannel.Similar(last, now, smallThreshold)) small++;
            }
            last = now;
        }
        return new Result(writes, small);
    }

    public static void TestGpuSkipsTinyChangesInEffectsWithoutSharedColour()
    {
        // ムード・ミックスは共有の色（ビートの色替え）を描かないので、色替えのたびに小さな変化を送ってはいけない
        var mood = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Mood, Gamma = false };
        mood.Music.Intensity = Intensities.Moderate;
        var pny = Run(Pny(), mood, new RgbDeviceSettings { Name = Pny().Name }, true, 50, 5, RgbDeviceChannel.GpuChangeThreshold);
        T.Info($"PNY + ムード: {pny.Writes} 回（うち差 24 以下 {pny.Small} 回）/ 45 秒");
        T.Check(pny.Small == 0, $"PNY in Mood never writes a sub-threshold change ({pny.Small})");

        var rxMood = Run(Rx(), mood, new RgbDeviceSettings { Name = Rx().Name, Effect = MusicStyles.Mood }, true, 50, 5, RgbDeviceChannel.FlickerProneChangeThreshold);
        T.Info($"ASRock RX + ムード（デバイスで指定）: {rxMood.Writes} 回（うち差 40 以下 {rxMood.Small} 回）/ 45 秒");
        T.Check(rxMood.Small == 0, $"flicker-prone RX in Mood never writes a sub-threshold change ({rxMood.Small})");

    }

    /// <summary>
    /// ビートの色替えがグラボに届くかを測る（暗め 15%・隣どうしが近い色のパレット「炎」で、差がしきい値より小さい色替えも含める）。
    /// steady なら、色替えのたびに、色替えが終わってから 0.3 秒後に、最後に送った色と描いた色が同じかを見る（違えば遅れ）。
    /// あわせて、毎コマの「送った色と描いた色の差（LED ごとの R・G・B の差の合計の最大）」の平均を返す
    /// </summary>
    private static (int Steps, int Late, double Mismatch, int Writes) RunSteps(RgbDevice dev, string style, string intensity,
        RgbDeviceSettings settings, bool steady, int tolerance = 0, int downbeat = 0)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style, Gamma = true, Brightness = 15 };
        cfg.Music.Intensity = intensity;
        cfg.Music.Palette = Palettes.Fire;
        var shared = new SharedColor();
        var ch = new RgbDeviceChannel(dev, 0) { SharedColor = shared };
        ch.Configure(cfg, MusicTuning.Resolve(cfg), settings, false);
        var ring = new FeatureRing();
        var f = new AudioFrame();
        var beat = new BeatState(60.0 / 123, 0.2, 0, 0.9, true, downbeat);
        var rng = new Random(11);
        const double Tick = 1.0 / 60;
        int steps = 0, late = 0, writes = 0, frames = 0;
        double mismatch = 0, seenStep = shared.LastStepT, checkAt = double.PositiveInfinity;
        for (double t = 0; t < 45; t += Tick)
        {
            f.T = t;
            f.GateOpen = true;
            f.Activity = 1;
            f.LoudVU = (float)(0.5 + 0.3 * rng.NextDouble());
            f.CentroidHz = 1000;
            ring.Write(f);
            ch.Prepare(t, Tick, ring, null, beat, null, false, false);
            if (!ch.Render()) continue;
            if (shared.LastStepT != seenStep)
            {
                seenStep = shared.LastStepT;
                if (t > 3) steps++;
                checkAt = t + ch.Renderer.ColorStepSettleSec + 0.3;
            }
            var reason = ch.ShouldSend(t, Tick, true);
            if (reason != SendReason.None)
            {
                ch.MarkSent(t, Tick, reason, 1);
                if (t > 3) writes++;
            }
            var sent = ch.CopyLastSent();
            if (sent is null || t <= 3) continue;
            int worst = 0;
            for (int i = 0; i < sent.Length; i++)
                worst = Math.Max(worst, Math.Abs(sent[i].R - ch.Frame[i].R) + Math.Abs(sent[i].G - ch.Frame[i].G) + Math.Abs(sent[i].B - ch.Frame[i].B));
            mismatch += worst;
            frames++;
            if (steady && t >= checkAt)
            {
                if (!RgbDeviceChannel.Similar(sent, ch.Frame, tolerance)) late++;
                checkAt = double.PositiveInfinity;
            }
        }
        return (steps, late, mismatch / Math.Max(1, frames), writes);
    }

    public static void TestGpuReceivesEveryColourStep()
    {
        // 自動（ビートで色替えの遅い版）: どの色替えも、差が小さくてもグラボに届く。
        // （パルスは色をビートで切り替えず、時刻でゆっくり変えるので、ここでは数えない）
        foreach (var style in new[] { MusicStyles.BeatHue })
        foreach (var intensity in new[] { Intensities.Moderate, Intensities.Intense })
        foreach (var dev in new[] { Pny(), Rx() })
        {
            int tolerance = style == MusicStyles.Pulse && intensity == Intensities.Intense ? 12 : 0;
            var r = RunSteps(dev, style, intensity, new RgbDeviceSettings { Name = dev.Name }, steady: true, tolerance);
            T.Check(r.Steps >= 20 && r.Late == 0, $"{dev.Name[..4]} {style}/{intensity}: every colour step reaches the GPU ({r.Late} late of {r.Steps}, {r.Writes} writes)");
        }
        // デバイスごとに選んだエナジー（グラボでも選べる）: 色替えの最後の色まで届く
        {
            var r = RunSteps(Pny(), MusicStyles.BeatHue, Intensities.Intense, new RgbDeviceSettings { Name = Pny().Name, Effect = MusicStyles.Energy }, steady: false);
            T.Info($"PNY Energy/Intense: 色替え {r.Steps} 回、書き込み {r.Writes} 回、送った色と描いた色の差の平均 {r.Mismatch:F1}");
            T.Check(r.Writes >= r.Steps * 0.8 && r.Mismatch <= 8, $"PNY Energy/Intense: colour steps are delivered (mismatch {r.Mismatch:F1}, {r.Writes} writes for {r.Steps} steps)");
        }
        // テンポ追従: 共有の色とは別に 2 拍ごとに帯の色が変わる。小節の頭の位置（曲ごとに違う）・強さによらず届く
        double worstChase = 0;
        foreach (var intensity in new[] { Intensities.Subtle, Intensities.Moderate, Intensities.Intense })
        for (int downbeat = 0; downbeat < 4; downbeat++)
        {
            var r = RunSteps(Pny(), MusicStyles.BeatHue, intensity, new RgbDeviceSettings { Name = Pny().Name, Effect = MusicStyles.Chase }, steady: false, downbeat: downbeat);
            worstChase = Math.Max(worstChase, r.Mismatch);
            T.Check(r.Mismatch <= 6, $"PNY Chase/{intensity}, downbeat {downbeat}: the band colour reaches the GPU (mismatch {r.Mismatch:F1}, {r.Writes} writes)");
        }
        T.Info($"PNY テンポ追従: 送った色と描いた色の差の平均（いちばん大きいもの）{worstChase:F1}");
    }

    public static void TestGpuIdleRampEndIsSent()
    {
        foreach (var dev in new[] { Pny(), Rx() })
        {
            IdleEndCase(dev, IdleEffects.Static, Palettes.Ocean, 30, "#00C0FF");
            IdleEndCase(dev, IdleEffects.Off, Palettes.Fire, 15, "#6A00FF");
        }
    }

    /// <summary>
    /// 音が止まって、止まっているときの光り方（固定色・消灯）に切り替わったあと、グラボに送った色が、
    /// 描いた色とずれたまま残らない（色が近く、切り替えの差がしきい値以下でも）
    /// </summary>
    private static void IdleEndCase(RgbDevice dev, string idleEffect, string palette, double brightness, string staticColor)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Gamma = true, Brightness = brightness, StaticColor = staticColor };
        cfg.Music.IdleEffect = idleEffect;
        cfg.Music.IdleDelaySec = 2;
        cfg.Music.Palette = palette;
        var ch = new RgbDeviceChannel(dev, 0) { SharedColor = new SharedColor() };
        ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
        var ring = new FeatureRing();
        var f = new AudioFrame();
        const double Tick = 1.0 / 60;
        int stuck = 0, pauses = 0;
        double t = 0;
        for (int k = 0; k < 10; k++)
        {
            var beat = new BeatState(60.0 / 123, t + 0.2, 0, 0.9, true, 0);
            double musicEnd = t + 6 + k * 0.37, pauseEnd = musicEnd + 12;
            for (; t < pauseEnd; t += Tick)
            {
                bool playing = t < musicEnd;
                f.T = t;
                f.GateOpen = playing;
                f.Activity = playing ? 1 : 0;
                f.LoudVU = playing ? 0.7f : 0;
                f.CentroidHz = 1000;
                ring.Write(f);
                // テンポは、音が止まって 4 秒ほどで忘れる
                ch.Prepare(t, Tick, ring, null, t < musicEnd + 4.3 ? beat : BeatState.None, null, false, false);
                if (!ch.Render()) continue;
                var reason = ch.ShouldSend(t, Tick, true);
                if (reason != SendReason.None) ch.MarkSent(t, Tick, reason, 1);
            }
            pauses++;
            if (!RgbDeviceChannel.Similar(ch.CopyLastSent()!, ch.Frame, 0)) stuck++;
        }
        T.Check(stuck == 0, $"{dev.Name[..4]} idle {idleEffect} ({palette} {brightness}%): exact idle colour at the end of every pause ({stuck} of {pauses} stuck)");
    }

    public static void TestGpuNoTinyWritesWhenSoundFlickersOnAndOff()
    {
        // 短い音がときどき鳴る（ゲーム・会話など）: 止まっているときの光り方へ切り替わりかけて戻るたびに、
        // ムード・ミックスのグラボへ小さな差を書き込まない
        foreach (var style in new[] { MusicStyles.Mood, MusicStyles.Mix })
        {
            var dev = Pny();
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style, Gamma = true, Brightness = 30 };
            cfg.Music.IdleDelaySec = 2;
            cfg.Music.Palette = Palettes.Ocean;
            var ch = new RgbDeviceChannel(dev, 0) { SharedColor = new SharedColor() };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            var ring = new FeatureRing();
            var f = new AudioFrame();
            const double Tick = 1.0 / 60;
            int small = 0, writes = 0;
            Rgb[]? last = null;
            for (double t = 0; t < 120; t += Tick)
            {
                bool sound = t % 2.5 < 0.1;
                f.T = t;
                f.GateOpen = sound;
                f.Activity = sound ? 1 : 0;
                f.LoudVU = sound ? 0.8f : 0;
                f.CentroidHz = 1000;
                ring.Write(f);
                ch.Prepare(t, Tick, ring, null, BeatState.None, null, false, false);
                if (!ch.Render()) continue;
                var reason = ch.ShouldSend(t, Tick, true);
                if (reason == SendReason.None) continue;
                ch.MarkSent(t, Tick, reason, 1);
                var now = ch.CopyLastSent()!;
                if (t > 5 && last is not null)
                {
                    writes++;
                    if (RgbDeviceChannel.Similar(last, now, RgbDeviceChannel.GpuChangeThreshold) && t % RgbSync.SlowForceResendSec > 1) small++;
                }
                last = now;
            }
            T.Check(small == 0, $"PNY {style} with short sounds every 2.5 s: no sub-threshold writes ({small} of {writes})");
        }
    }

    public static void TestFlickerProneMoodStepsBarsAtOnce()
    {
        // ASRock の Radeon でムードを選んだとき: 小節ごとの色替えを一度に送る（途中の色を 3 回に分けて書かない）
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Gamma = false };
        var ch = new RgbDeviceChannel(Rx(), 0) { SharedColor = new SharedColor() };
        ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = Rx().Name, Effect = MusicStyles.Mood }, false);
        var ring = new FeatureRing();
        var f = new AudioFrame { GateOpen = true, Activity = 1, LoudVU = 0.6f, CentroidHz = 1000 };
        double period = 60.0 / 123;
        var beat = new BeatState(period, 0.2, 0, 0.9, true, 0);
        const double Tick = 1.0 / 60;
        var writes = new List<double>();
        for (double t = 0; t < 45; t += Tick)
        {
            f.T = t;
            ring.Write(f);
            ch.Prepare(t, Tick, ring, null, beat, null, false, false);
            if (!ch.Render()) continue;
            var reason = ch.ShouldSend(t, Tick, true);
            if (reason == SendReason.None) continue;
            ch.MarkSent(t, Tick, reason, 1);
            if (t > 5) writes.Add(t);
        }
        // 小節の頭の前後 0.7 秒に入った書き込みの数（小節ごと）
        int bars = 0, worst = 0;
        for (double bar = 0.2; bar < 45; bar += 4 * period)
        {
            if (bar < 6 || bar > 44) continue;
            bars++;
            worst = Math.Max(worst, writes.Count(w => w >= bar - 0.1 && w < bar + 0.6));
        }
        T.Info($"RX + ムード: 40 秒で {writes.Count} 回、小節ごとの最多 {worst} 回（小節 {bars} 個）");
        T.Check(worst <= 1, $"one write per bar change on the flicker-prone GPU (worst {worst} in a bar)");
    }

    public static void TestGpuHeldDuringMusicPause()
    {
        // 音楽連動で音が止まったときの光り方「呼吸」: グラボは固定色（「虹・呼吸も動かす」がオフのとき）
        var cfg = new RgbConfig { Mode = RgbModes.Music, Gamma = false };
        cfg.Music.IdleEffect = IdleEffects.Breathing;
        cfg.Music.IdleDelaySec = 2;
        var pny = Run(Pny(), cfg, new RgbDeviceSettings { Name = Pny().Name }, false, 60, 15, 0);
        var rx = Run(Rx(), cfg, new RgbDeviceSettings { Name = Rx().Name }, false, 60, 15, 0);
        var pnyOn = Run(Pny(), cfg, new RgbDeviceSettings { Name = Pny().Name, AnimateOnGpu = true }, false, 60, 15, 0);
        var board = Run(Board(), cfg, new RgbDeviceSettings { Name = Board().Name }, false, 60, 15, 0);
        T.Info($"無音で「呼吸」（15〜60 秒）: PNY {pny.Writes} 回、RX {rx.Writes} 回、PNY（動かす）{pnyOn.Writes} 回、マザーボード {board.Writes} 回");
        T.Check(pny.Writes <= 1 && rx.Writes == 0, "GPUs are held still while the music is paused (idle breathing)");
        T.Check(pnyOn.Writes > 30, "with 'animate on GPU' the PNY breathes during pauses");
        T.Check(board.Writes > 1000, "the motherboard still breathes at full rate");

        // 既定の「ムード」: ちかちかしやすい RX は 20 秒ごとに一度だけ色を進める
        var moodCfg = new RgbConfig { Mode = RgbModes.Music, Gamma = false };
        moodCfg.Music.IdleDelaySec = 2;
        var times = new List<double>();
        var rxMood = Run(Rx(), moodCfg, new RgbDeviceSettings { Name = Rx().Name }, false, 120, 15, 0, times);
        T.Info($"無音で「ムード」（15〜120 秒）: RX {rxMood.Writes} 回（{string.Join(", ", times.Select(x => x.ToString("F1")))} 秒）");
        T.Check(rxMood.Writes <= 6, $"flicker-prone RX steps the idle mood colour at most every 20 s ({rxMood.Writes} writes in 105 s)");
    }

    public static void TestFlickerProneIdleMoodMatchesOthers()
    {
        // RX の止まっているときのムードの色は、20 秒ごとの段でも、ほかのデバイスの色とほぼ同じ（色が違って見えない）
        var c = new EffectContext { Pal = ColorPalette.Get(Palettes.Rainbow, null), Frame = new AudioFrame() };
        var smooth = new MoodEffect(idle: true);
        var stepped = new MoodEffect(idle: true) { IdleStepSec = DeviceRenderer.FlickerProneIdleStepSec };
        smooth.Reset(c);
        stepped.Reset(c);
        double worst = 0;
        for (double t = 0; t < 300; t += 0.5)
        {
            c.T = t;
            c.Dt = 0.5f;
            smooth.Update(c);
            stepped.Update(c);
            double xs = (double)typeof(MoodEffect).GetField("_x", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(smooth)!;
            double xq = (double)typeof(MoodEffect).GetField("_x", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(stepped)!;
            if (t > 30) worst = Math.Max(worst, Math.Abs(xs - xq));
        }
        T.Check(worst <= 0.5 * DeviceRenderer.FlickerProneIdleStepSec / MoodEffect.IdleCycleSec + 0.01, $"stepped idle mood stays within half a step of the others ({worst:F3})");
    }

    /// <summary>
    /// 温度モデル: ふだん 42 °C、1 秒ごとに 4 回に 1 回くらい +6〜18 °C 跳ねる（センサーは 1 秒ごとに値が変わる）。
    /// 秒ごとの乱数は、隣どうしで似た値にならないよう、整数のハッシュで作る（new Random(連番) は似た値が続く）
    /// </summary>
    private static double SpikyTemp(int second, int seed) =>
        42 + (U(seed, second * 3) < 0.25 ? 6 + U(seed, second * 3 + 1) * 12 : U(seed, second * 3 + 2) * 2);

    private static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    private static double U(int seed, int k) => Mix(Mix((uint)seed * 0x9E3779B9u) ^ (uint)k) / 4294967296.0;

    private static double StepsPerMinute(double pollSec, int seed)
    {
        var filter = new RgbTemperatureFilter();
        double? last = null;
        int steps = 0;
        const double Duration = 540;
        for (double t = 0; t < Duration; t += pollSec)
        {
            var d = filter.Push(SpikyTemp((int)Math.Floor(t), seed), t);
            if (t > 10 && last is not null && d != last) steps++;
            last = d;
        }
        return steps * 60 / (Duration - 10);
    }

    public static void TestTemperatureFilterSameAtFastPolling()
    {
        // 読む間隔が 0.25 秒でも 1 秒と同じくらい落ち着いている（中央値を個数ではなく時間で取る）
        double fast = 0, normal = 0, slow = 0;
        for (int seed = 1; seed <= 3; seed++)
        {
            fast += StepsPerMinute(0.25, seed) / 3;
            normal += StepsPerMinute(1, seed) / 3;
            slow += StepsPerMinute(2, seed) / 3;
        }
        T.Info($"表示の段の変化／分: 0.25 秒ごと {fast:F1}、1 秒ごと {normal:F1}、2 秒ごと {slow:F1}");
        T.Check(fast <= 4.5 && normal <= 4.5 && slow <= 4.5, "temperature display changes rarely at every polling interval");
        T.Check(fast <= normal * 1.6 + 0.5, $"fast polling is not much busier than 1 s polling ({fast:F1} vs {normal:F1})");

        // 本当に上がったときは、0.25 秒ごとでも追いつく
        var filter = new RgbTemperatureFilter();
        double? shown = null;
        for (double t = 0; t < 60; t += 0.25) shown = filter.Push(45, t);
        for (double t = 60; t < 100; t += 0.25) shown = filter.Push(75, t);
        T.Check(shown >= 73, $"a real rise is followed at 0.25 s polling ({shown})");
    }

    public static void TestTemperatureFilterIgnoresFirstSpike()
    {
        // 始めた直後（起動・センサーの変更・復帰）の 1 回の跳ねを長く引きずらない
        foreach (double poll in new[] { 1.0, 0.25 })
        {
            var filter = new RgbTemperatureFilter();
            double? last = null;
            int changes = 0;
            for (double t = 0; t < 40; t += poll)
            {
                double raw = t < 1 ? 72 : 45;   // 最初の 1 秒だけ 72 °C
                var d = filter.Push(raw, t);
                if (last is not null && d != last) changes++;
                last = d;
            }
            T.Check(changes <= 1 && last == 45, $"a spike at the start causes at most one colour step ({changes} at {poll} s polling, ends at {last})");
        }
    }

    public static void TestWhiteOnlyHelper()
    {
        var frame = new[] { new Rgb(37, 0, 255), new Rgb(10, 200, 30), Rgb.Black };
        RgbDeviceChannel.ToWhite(frame);
        T.Check(frame[0] == new Rgb(255, 255, 255) && frame[1] == new Rgb(200, 200, 200) && frame[2] == Rgb.Black,
            "white-only: each LED becomes white at the brightest channel");
        RgbDeviceChannel.ToWhite(frame, 128);
        T.Check(frame.All(c => c == new Rgb(128, 128, 128)), "white-only: constant level");
    }
}
