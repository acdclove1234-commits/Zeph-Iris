// ==================================================
// FlickerDiagTests.cs - 光り方のちらつきを数える（送る 0〜255 の値で見る）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FlickerDiagTests
{
    /// <summary>
    /// 1 つの LED の、送った値（0〜255）の 1 コマごとの変化から、ちらつきを数える。
    ///   jitter = 向きが短い間に何度も反転した回数（上がって下がってまた上がる。ゆっくり変わる光では起きない）
    ///   blink  = 消える（0）と点く（>0）の行き来の回数
    /// どちらも 1 秒・1 LED あたり
    /// </summary>
    public static (double Jitter, double Blink) Measure(RgbDevice dev, RgbConfig cfg, double seconds = 20, double fps = 60)
        => MeasureAll(dev, cfg, seconds, fps) is var m ? (m.Jitter, m.Blink) : default;

    /// <summary>Measure と同じ。Toggle = 1 段でも、4 コマ以内に向きが戻った回数（ディザリングの行き来も数える）</summary>
    public static (double Jitter, double Blink, double Toggle) MeasureAll(RgbDevice dev, RgbConfig cfg, double seconds = 20, double fps = 60)
    {
        var r = new DeviceRenderer(dev);
        var input = new RenderInput { Cfg = cfg, Device = new RgbDeviceSettings { Name = dev.Name }, Temperature = 55 };
        var o = new Rgb[dev.LedCount];
        int n = dev.LedCount, frames = (int)(seconds * fps);
        var prev = new int[n];
        var dirPrev = new int[n];
        var lastFlip = new int[n];
        long jitter = 0, blink = 0, toggle = 0;
        var dir1 = new int[n];
        var flip1 = new int[n];
        for (int f = 0; f < frames; f++)
        {
            double t = 5 + f / fps;
            input.T = t;
            input.TEval = t;
            r.Render(input, o);
            for (int i = 0; i < n; i++)
            {
                int v = Math.Max(o[i].R, Math.Max(o[i].G, o[i].B));
                if (f > 0)
                {
                    int d1 = Math.Sign(v - prev[i]);
                    if (d1 != 0)
                    {
                        if (dir1[i] != 0 && d1 != dir1[i]) { if (f - flip1[i] <= 4) toggle++; flip1[i] = f; }
                        dir1[i] = d1;
                    }
                    // 1 段だけの上下（ディザリング）は見えないので数えない。2 段以上の変化だけ
                    int d = Math.Abs(v - prev[i]) >= 2 ? Math.Sign(v - prev[i]) : 0;
                    if (d != 0)
                    {
                        if (dirPrev[i] != 0 && d != dirPrev[i])
                        {
                            if (f - lastFlip[i] <= 4) jitter++;   // 4 コマ（0.07 秒）以内にまた向きが変わった
                            lastFlip[i] = f;
                        }
                        dirPrev[i] = d;
                    }
                    if ((v == 0) != (prev[i] == 0)) blink++;
                }
                prev[i] = v;
            }
        }
        double per = seconds * n;
        return (jitter / per, blink / per, toggle / per);
    }

    public static void TestFlickerDiag()
    {
        var strip = new RgbDevice(0, "Strip", 60, 4, null, "HID: x");
        foreach (var mode in new[] { RgbModes.Rainbow, RgbModes.Breathing }.Concat(RgbModes.Ambient))
        {
            var cfg = new RgbConfig { Mode = mode, Brightness = 100, Gamma = true, StaticColor = "#0060FF", Speed = 1 };
            var (j, b, tg) = MeasureAll(strip, cfg);
            var cfg2 = new RgbConfig { Mode = mode, Brightness = 30, Gamma = true, StaticColor = "#0060FF", Speed = 1 };
            var (j2, b2, tg2) = MeasureAll(strip, cfg2);
            T.Info($"{mode,-14} 100%: jitter {j:F2} blink {b:F2} toggle {tg:F2}   30%: jitter {j2:F2} blink {b2:F2} toggle {tg2:F2}  (per LED per s)");
        }
    }

    /// <summary>前に送った値を保つ書き方: 境目のあたりで揺れても値は変わらず、0.75 段を超えて変わったら変わる</summary>
    public static void TestHoldQuantizer()
    {
        var o = new OutputStage();
        var dst = new Rgb[1];
        byte At(float code) { o.WriteHold(new[] { new LinColor(code / 255f, 0, 0) }, dst, 1f); return dst[0].R; }
        T.Check(At(2.4f) == 2, "first value is rounded");
        bool steady = true;
        foreach (float c in new[] { 2.6f, 2.3f, 2.7f, 2.5f, 2.2f, 2.65f }) steady &= At(c) == 2;
        T.Check(steady, "wobbling around 2.5 keeps 2 (no 2-3-2-3)");
        T.Check(At(2.8f) == 3, "moving more than 0.75 above changes to 3");
        T.Check(At(2.4f) == 3 && At(2.2f) == 2, "and back down only after moving 0.75 below");
        T.Check(At(0.2f) == 0, "near black goes to 0");
    }

    /// <summary>
    /// 波・炎・オーロラ・心拍は、明るさ 100% でも 30% でもちらつかない（2 段以上の小刻みな上下・消えたり点いたりがない）。
    /// 雷・さえずり・流れ星は、光り方そのもののまたたきだけ（1 LED あたり 1 秒に 1 回未満）
    /// </summary>
    public static void TestAmbientModesDoNotFlicker()
    {
        var strip = new RgbDevice(0, "Strip", 60, 4, null, "HID: x");
        foreach (var mode in RgbModes.Ambient)
        foreach (double bri in new[] { 100.0, 30.0 })
        {
            var cfg = new RgbConfig { Mode = mode, Brightness = bri, Gamma = true, StaticColor = "#0060FF", Speed = 1 };
            var (j, b, tg) = MeasureAll(strip, cfg);
            bool designed = mode is RgbModes.Lightning or RgbModes.Birdsong or RgbModes.Meteor;
            if (designed) T.Check(j < 1 && b < 1 && tg < 1, $"{mode} at {bri}%: only its own flashes (jitter {j:F2}/s, blink {b:F2}/s, 1-step toggles {tg:F2}/s)");
            else T.Check(j < 0.05 && b < 0.05 && tg < 0.05, $"{mode} at {bri}%: no flicker, not even 1-step toggling (jitter {j:F2}/s, blink {b:F2}/s, toggles {tg:F2}/s)");
        }
    }
}
