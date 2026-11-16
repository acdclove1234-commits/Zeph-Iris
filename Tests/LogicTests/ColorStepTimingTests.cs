// ==================================================
// ColorStepTimingTests.cs - ビートで色を進める時刻と、ビートの光の時刻がそろうこと
//   (テンポをつかんでいれば拍の頭で切り替え終わる・つかんでいなければキックの光と同じコマで切り替わる・
//    クロスフェードの途中で次の色へ進めても色が飛ばない・合わせる元が暗いコマでも、遅いデバイスは最後の色のまま)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class ColorStepTimingTests
{
    private const double Fps = 60;

    /// <summary>1 台分の BeatDriver と EffectContext を、60 コマ／秒で進める</summary>
    private sealed class Lane
    {
        public readonly EffectContext C = new();
        public readonly BeatDriver Driver = new();
        public double T;

        public Lane(BeatState beat, string intensity, SharedColor? shared)
        {
            var cfg = new RgbConfig { Mode = RgbModes.Music };
            MusicTuning.ApplyIntensity(cfg.Music, intensity);
            C.Music = MusicTuning.Resolve(cfg);
            C.Beat = beat;
            C.Shared = shared;
            C.Color.Reset();
            C.Beats = Driver;
        }

        public void Step(params AudioEvent[] events)
        {
            double t0 = T;
            T += 1 / Fps;
            C.TPrev = t0;
            C.T = T;
            C.Dt = (float)(1 / Fps);
            C.Events.Clear();
            C.Events.AddRange(events);
            Driver.Update(C, t0, T);
            BeatDriver.SyncSharedColor(C);
        }
    }

    /// <summary>
    /// テンポをつかんでいるとき: 色を進める拍では、拍の頭のコマでもうクロスフェードが終わっている（ビートの光が新しい色で光る）。
    /// 6.15.8 までは拍の頭から切り替え始めたので、光のいちばん明るいところが前の色・途中の色だった
    /// </summary>
    public static void TestLockedColourStepIsDoneOnTheBeat()
    {
        foreach (var intensity in new[] { Intensities.Subtle, Intensities.Moderate, Intensities.High })
        foreach (bool shared in new[] { false, true })
        foreach (bool preFade in new[] { true, false })
        {
            var beat = new BeatState(0.5, 0.25, 0, 0.9, true, 0);
            BeatDriver.PreFade = preFade;
            try
            {
                var L = new Lane(beat, intensity, shared ? new SharedColor() : null);
                int stepBeats = 0, doneAtBeat = 0, steps = 0;
                double lastTarget = L.C.Color.Target, targetAtBeat = L.C.Color.Target;
                for (int f = 0; f < 60 * 12; f++)
                {
                    L.Step();
                    if (L.C.Color.Target != lastTarget) { steps++; lastTarget = L.C.Color.Target; }
                    if (!L.C.BeatCrossed) continue;
                    // 色を進める拍 = 前の拍のコマから、この拍のコマまでに色が進んだ（拍の前から切り替え始めても、拍の頭で進めても）
                    bool stepBeat = L.C.Color.Target != targetAtBeat;
                    targetAtBeat = L.C.Color.Target;
                    if (!stepBeat || L.T < 2) continue;
                    stepBeats++;
                    if (L.C.Color.ProgressAt(L.C.T) >= 0.999f) doneAtBeat++;
                }
                string label = $"{intensity}{(shared ? " shared" : "")}{(preFade ? "" : " (6.15.8 timing)")}";
                int every = L.C.Music.ColorStepBeats;
                T.Check(steps >= 20 / every, $"{label}: colour steps every {every} beats ({steps} steps in 12 s)");
                if (preFade)
                    T.Check(stepBeats > 0 && doneAtBeat == stepBeats, $"{label}: the crossfade is finished on the beat frame ({doneAtBeat} of {stepBeats} step beats)");
                else
                    T.Check(doneAtBeat < stepBeats, $"{label}: (reference) the crossfade started on the beat, so the flash frame was mid-fade ({doneAtBeat} of {stepBeats})");
            }
            finally { BeatDriver.PreFade = true; }
        }
    }

    /// <summary>
    /// 色を共有する 2 台で、拍の前から切り替え始める時間（クロスフェード）が違っても、共有の色は 1 拍で 1 回だけ進む。
    /// 拍の頭で一度に切り替えるデバイス（OpenRGB のグラボ: NoFade）や、遅いデバイス（切り替えが長い）がほかのデバイスと並んだとき、
    /// 以前は「切り替え始めた時刻」で同じ拍かを比べていたので、差が 0.15 秒以上あると同じ拍で 2 回進め、
    /// 速いデバイスが拍の頭でもう一度クロスフェードし直していた（色替えの途中で光る）
    /// </summary>
    public static void TestSharedColourStepsOnceWithMixedDevices()
    {
        var setups = new (string Intensity, string Kind)[]
        {
            (Intensities.Subtle, "nofade"), (Intensities.Moderate, "nofade"), (Intensities.High, "nofade"),
            (Intensities.Intense, "slow"), (Intensities.Moderate, "slow"), (Intensities.Subtle, "slow"),
        };
        foreach (var (intensity, kind) in setups)
        foreach (bool otherFirst in new[] { false, true })
        {
            var (steps, stepBeats, midFade, apart) = MixedLanes(intensity, kind, otherFirst);
            string label = $"{intensity} strip + {kind} lane{(otherFirst ? " (other lane first)" : "")}";
            T.Check(Math.Abs(steps - stepBeats) <= 1,
                $"{label}: the shared colour steps once per step beat ({steps} steps, {stepBeats} step beats)");
            T.Check(midFade == 0, $"{label}: the strip is never mid-crossfade on a step beat ({midFade} of {stepBeats})");
            T.Check(apart == 0,
                $"{label}: on every step beat both lanes are on the shared colour ({apart} of {stepBeats} beats apart)");
        }
        // 6.15.8 と同じく切り替え始めた時刻で比べると（SharedColor.KeyOnBeat = false）、同じ拍で 2 回進めていた
        SharedColor.KeyOnBeat = false;
        try
        {
            var (s1, b1, m1, _) = MixedLanes(Intensities.Subtle, "nofade", true);
            T.Check(s1 > b1 + 1 && m1 > 0, $"(reference) keyed on the fade start: Subtle strip + nofade lane stepped twice per beat ({s1} steps, {b1} step beats, {m1} mid-fade)");
        }
        finally { SharedColor.KeyOnBeat = true; }
    }

    /// <summary>2 台で 12 秒（120 BPM・テンポをつかんでいる）: 共有の色が進んだ回数・色を進める拍の数・速いデバイスが拍の頭で切り替え途中だった拍・色がそろっていなかった拍</summary>
    private static (int Steps, int StepBeats, int MidFade, int Apart) MixedLanes(string intensity, string kind, bool otherFirst)
    {
        {
            var beat = new BeatState(0.5, 0.25, 0, 0.9, true, 0);
            var shared = new SharedColor();
            var fast = new Lane(beat, intensity, shared);
            var other = new Lane(beat, intensity, shared);
            if (kind == "nofade") other.C.NoFade = true;
            else other.C.Slow = true;
            int every = fast.C.Music.ColorStepBeats;
            int stepBeats = 0, steps = 0, midFade = 0, apart = 0;
            double last = shared.Target;
            for (int f = 0; f < 60 * 12; f++)
            {
                if (otherFirst) { other.Step(); fast.Step(); }
                else { fast.Step(); other.Step(); }
                if (shared.Target != last) { steps++; last = shared.Target; }
                if (!fast.C.BeatCrossed || fast.C.BeatIndex % every != 0) continue;
                stepBeats++;
                if (fast.T > 2 && fast.C.Color.ProgressAt(fast.C.T) < 0.999f) midFade++;
                if (Math.Abs(fast.C.Color.Target - shared.Target) > 1e-9 || Math.Abs(other.C.Color.Target - shared.Target) > 1e-9) apart++;
            }
            return (steps, stepBeats, midFade, apart);
        }
    }

    /// <summary>
    /// 拍の前に切り替え始めたあとで、テンポの予測がその拍を 0.15 秒以上後ろへ動かしても、同じ拍でもう一度は進めない
    /// （同じ拍かを固定の 0.15 秒で比べていたので、遅い曲で予測が動くと 1 拍で 2 回進めていた。いまは半拍で比べる。BeatCrossing と同じ）
    /// </summary>
    public static void TestPhaseShiftAfterPreFadeDoesNotStepTwice()
    {
        foreach (double bpm in new[] { 88.0, 120.0 })
        foreach (bool useShared in new[] { false, true })
        {
            var (steps, crossings, shifts) = ShiftedLane(bpm, useShared);
            string label = $"{bpm:F0} BPM{(useShared ? " shared" : "")}";
            T.Check(shifts >= 5 && Math.Abs(steps - crossings) <= 1,
                $"{label}: a 0.17 s phase shift after the pre-fade gives no extra colour step ({steps} steps, {crossings} beats, {shifts} shifts)");
        }
        // 6.15.8 と同じく固定の 0.15 秒で比べると（BeatDriver.HalfBeatSame = false）、予測が動くたびに 1 回多く進めていた
        BeatDriver.HalfBeatSame = false;
        try
        {
            var (s1, c1, n1) = ShiftedLane(88, true);
            T.Check(s1 >= c1 + n1 - 1, $"(reference) a fixed 0.15 s window: one extra step per shift ({s1} steps, {c1} beats, {n1} shifts)");
        }
        finally { BeatDriver.HalfBeatSame = true; }
    }

    /// <summary>強さ「強め」の 1 台で 20 秒: 4 拍に 1 回、拍の前に切り替え始めたあとで予測を 0.17 秒後ろへ動かす。色を進めた回数・拍の数・動かした回数</summary>
    private static (int Steps, int Crossings, int Shifts) ShiftedLane(double bpm, bool useShared)
    {
        {
            double p = 60 / bpm;
            var beat = new BeatState(p, 0.25, 0, 0.9, true, 0);
            var L = new Lane(beat, Intensities.High, useShared ? new SharedColor() : null);
            int crossings = 0, steps = 0, shifts = 0;
            long shiftedIndex = -1;
            double last = L.C.Color.Target;
            for (int f = 0; f < 60 * 20; f++)
            {
                // 4 拍に 1 回、拍の 20〜45 ms 前（強め: 80 ms 前から切り替え始めている）に、予測をその拍ごと 0.17 秒後ろへ動かす
                var b = L.C.Beat;
                double tn = b.NextBeatAfter(L.T);
                long idx = b.Index(tn + 0.25 * p);
                if (L.T > 2 && idx % 4 == 0 && idx != shiftedIndex && tn - L.T < 0.045 && tn - L.T > 0.02)
                {
                    L.C.Beat = new BeatState(p, b.LastBeatT + 0.17, b.IndexAtLast, 0.9, true, 0);
                    shiftedIndex = idx;
                    shifts++;
                }
                L.Step();
                if (L.C.Color.Target != last) { steps++; last = L.C.Color.Target; }
                if (L.C.BeatCrossed) crossings++;
            }
            return (steps, crossings, shifts);
        }
    }

    /// <summary>
    /// テンポをつかんでいないとき: 予測した拍では光らない（色はキックで進むので、拍の光は前の色で光ってしまう）。
    /// 強いキックでは、光と色の切り替えが同じコマで、そのコマでもう新しい色（一度に切り替える）
    /// </summary>
    public static void TestUnlockedFlashAndColourStepComeTogether()
    {
        foreach (bool shared in new[] { false, true })
        {
            var beat = new BeatState(0.5, 0.25, 0, 0.9, false, 0);
            var L = new Lane(beat, Intensities.Moderate, shared ? new SharedColor() : null);
            float maxFl = 0;
            for (int f = 0; f < 120; f++) { L.Step(); maxFl = Math.Max(maxFl, L.C.Fl); }
            T.Check(maxFl < 1e-6f, $"unlocked{(shared ? " shared" : "")}: no flash from the predicted beats alone (max {maxFl:F3})");
            int ok = 0, n = 0;
            long id = 1;
            for (int k = 0; k < 8; k++)
            {
                double before = L.C.Color.Target;
                var ev = new AudioEvent(id++, AudioEventKind.Kick, L.T + 0.5 / Fps, 2f);
                L.Step(ev);
                // 合図 ColorStepBeats 回ごとに進む
                if (L.C.Color.Target == before) { for (int f = 0; f < 30; f++) L.Step(); continue; }
                n++;
                if (L.C.Fl > 0.3f && L.C.Color.ProgressAt(L.C.T) >= 0.999f) ok++;
                for (int f = 0; f < 30; f++) L.Step();
            }
            T.Check(n >= 3 && ok == n, $"unlocked{(shared ? " shared" : "")}: the kick flash and the new colour are in the same frame ({ok} of {n} steps)");

            BeatDriver.UnlockedPrediction = true;
            try
            {
                var old = new Lane(beat, Intensities.Moderate, shared ? new SharedColor() : null);
                float oldFl = 0;
                for (int f = 0; f < 120; f++) { old.Step(); oldFl = Math.Max(oldFl, old.C.Fl); }
                T.Check(oldFl > 0.3f, $"(reference) 6.15.8 flashed on the predicted beats while unlocked (max {oldFl:F2})");
            }
            finally { BeatDriver.UnlockedPrediction = false; }
        }
    }

    /// <summary>クロスフェードの途中で次の色へ進めても、その時刻の色はそのまま（途中の色から次の色へ切り替える）</summary>
    public static void TestColorFadeRestepKeepsTheShownColour()
    {
        PaletteView pal = ColorPalette.Get(Palettes.Rainbow, null);
        // 拍ごと（0.25〜0.5 秒ごと）に、0.5 秒のクロスフェード（強さ「控えめ」）で進める: いつも前の切り替えの途中
        var fade = new ColorFade();
        double worst = Restep(fade, pal, 0.25, 0.5, 0.5, 60);
        T.Check(worst < 1e-4, $"re-stepping mid-fade once per beat keeps the shown colour (worst OKLab jump {worst:E1})");
        // いちばん詰まったとき（同じビートとみなす 0.15 秒ごとに、0.5 秒のクロスフェード。覚える色の数の上限を超える）も、飛びは見分けられないほど小さい
        var busy = new ColorFade();
        double worstBusy = Restep(busy, pal, SharedColor.SameStepSec, 0.3, 0.5, 200);
        T.Check(worstBusy < 0.01, $"re-stepping every 0.15-0.3 s with 0.5 s crossfades keeps the shown colour within OKLab 0.01 (worst {worstBusy:F4})");
        double t = 1000;
        T.Check(fade.Sample(pal, 0, t, true) == pal.At(fade.Target, true), "after the last crossfade: exactly the palette colour");
    }

    /// <summary>ランダムな間隔（gapMin〜gapMax 秒）で n 回進め、進める直前と直後の色の差の最大（OKLab）を返す</summary>
    private static double Restep(ColorFade fade, in PaletteView pal, double gapMin, double gapMax, double dur, int n)
    {
        fade.Reset(0);
        var rng = new Random(3);
        double worst = 0, t = 10;
        for (int k = 0; k < n; k++)
        {
            double off = k % 2 == 0 ? 0 : 0.33;
            var a = fade.SampleLab(pal, off, t);
            var a2 = fade.SampleLab(pal, 0.5, t);
            fade.Step(t, 0.1 + 0.2 * rng.NextDouble(), dur);
            worst = Math.Max(worst, Math.Max(BlipMeter.Dist(a, fade.SampleLab(pal, off, t)), BlipMeter.Dist(a2, fade.SampleLab(pal, 0.5, t))));
            t += gapMin + (gapMax - gapMin) * rng.NextDouble();
        }
        return worst;
    }

    /// <summary>
    /// 遅いデバイスをほかのデバイスの色に合わせるとき、合わせる元が暗すぎて代表の色がないコマは、最後に合わせた色のまま（自分の光り方に切り替えない）。
    /// 合わせるのをやめたら忘れる
    /// </summary>
    public static void TestFollowerKeepsTheLastColourWhenTheLeaderIsDark()
    {
        var ch = new RgbDeviceChannel(new RgbDevice(1, "DRAM stick", 8, RgbDevice.TypeDram, null, "I2C: x"), 0);
        var lit = new SceneColor();
        lit.Add(new[] { new LinColor(0, 0, 0.8f), new LinColor(0, 0, 0.6f) });
        ch.SetFollow(true, true, lit, 100);
        var first = ch.Input.FollowColor;
        var dark = new SceneColor();
        dark.Add(new[] { new LinColor(0, 0, 0.001f), LinColor.Black });
        ch.SetFollow(true, true, dark, 100);
        T.Check(first is not null && ch.Input.FollowColor == first && ch.Input.FollowLevel < 0.01f,
            $"leader dark: keeps the last follow colour at the leader's (dark) level ({ch.Input.FollowColor}, {ch.Input.FollowLevel:F3})");
        ch.SetFollow(false, true, lit, 100);
        ch.SetFollow(true, true, dark, 100);
        T.Check(ch.Input.FollowColor is null, "after following stopped, the old colour is forgotten (the device draws its own effect)");
        RgbDeviceChannel.KeepFollowColor = false;
        try
        {
            ch.SetFollow(true, true, lit, 100);
            ch.SetFollow(true, true, dark, 100);
            T.Check(ch.Input.FollowColor is null, "(reference) 6.15.8: a dark leader made the follower draw its own effect");
        }
        finally { RgbDeviceChannel.KeepFollowColor = true; }
    }
}
