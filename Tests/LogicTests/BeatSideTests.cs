// 裏拍への張りつきを直す（BeatTracker.CheckSide）: ドラムのない前奏で裏拍のコードにロックしたあと、キックが拍で入ってきたら拍へ移る
using ProFanController;

namespace LogicTests;

public static class BeatSideTests
{
    /// <summary>predicted beats in [from, to) and the share that lands within ±40 ms of the kick grid (k × period)</summary>
    private static (int Beats, double OnKick) Score(EngineRun run, double period, double from, double to)
    {
        int n = 0, on = 0;
        for (int i = 1; i < run.Frames.Count; i++)
        {
            var b = run.Beats[i];
            double t0 = run.Frames[i - 1].T, t1 = run.Frames[i].T;
            if (t1 - run.T0 < from || t1 - run.T0 >= to || !b.Locked || !b.Crossed(t0, t1)) continue;
            double tb = b.NextBeatAfter(t0) - run.T0;
            double ph = tb / period - Math.Round(tb / period);
            n++;
            if (Math.Abs(ph * period) <= 0.04) on++;
        }
        return (n, n > 0 ? on / (double)n : 0);
    }

    private static EngineRun Run(double sideLowRatio)
    {
        const int fs = 48000;
        const double period = 0.6; // 100 BPM
        var g = new SignalGen(fs, 50);
        // 0〜14 秒: ドラムなし。拍の間（+0.3 秒）にだけコードとハイハット → 裏拍にロックする
        // 14 秒〜: キックが拍で入る。コードとハイハットは拍の間で鳴り続ける（中音・高音は拍の間のほうが強い）
        for (double t = 0.6; t < 49.5; t += period)
        {
            g.Snare(t + 0.3, 0.35);
            g.Hat(t + 0.3, 0.25);
            if (t >= 14) g.Kick(t, 0.8);
        }
        var saved = BeatTracker.Tune.SideLowRatio;
        BeatTracker.Tune.SideLowRatio = sideLowRatio;
        try
        {
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            return new EngineRun(e).Feed(g.Buf);
        }
        finally { BeatTracker.Tune.SideLowRatio = saved; }
    }

    public static void TestLockedOffBeatMovesToTheKicks()
    {
        var withCheck = Run(BeatTracker.Tune.SideLowRatio);
        var without = Run(0);
        var intro = Score(withCheck, 0.6, 6, 14);
        var late = Score(withCheck, 0.6, 32, 50);
        var lateOff = Score(without, 0.6, 32, 50);
        T.Info($"intro (chords between the beats only): {intro.Beats} beats, {intro.OnKick * 100:F0}% on the later kick grid");
        T.Info($"after the kicks come in: with the check {late.OnKick * 100:F0}% on the kicks ({late.Beats} beats), without {lateOff.OnKick * 100:F0}% ({lateOff.Beats})");
        T.Check(late.Beats >= 20 && late.OnKick >= 0.8, $"once kicks play on the beat, the beats follow the kicks ({late.OnKick * 100:F0}%)");
    }

    public static void TestSideCheckLeavesACorrectLockAlone()
    {
        // ふつうの曲（キック毎拍・スネア 2・4・ハイハット 8 分）: 拍に乗っていれば動かさない
        const int fs = 48000;
        var g = new SignalGen(fs, 40);
        g.Loop(120, 0.5, 39.5, hats: true, snare: true);
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var log = new List<string>();
        e.Tracker.DiagLog = m => { if (m.StartsWith("side:")) log.Add(m); };
        var run = new EngineRun(e).Feed(g.Buf);
        var s = Score(run, 0.5, 10, 40);
        T.Check(log.Count == 0, $"no half-beat shift on a plain beat ({log.Count})");
        T.Check(s.OnKick >= 0.9, $"beats stay on the kicks ({s.OnKick * 100:F0}%)");
    }
}
