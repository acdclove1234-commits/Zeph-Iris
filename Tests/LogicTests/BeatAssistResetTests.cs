// ==================================================
// BeatAssistResetTests.cs - 取り込み直したあと、前の音で出た拍の AI の結果を使わない（外部レビューで見つかった条件）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class BeatAssistResetTests
{
    /// <summary>120 BPM の拍（0.5 秒おき・2 秒おきに小節の頭）を、窓の終わり t1 まで 20 秒ぶん</summary>
    private static BeatAssist.Result Result120(double t1)
    {
        var beats = Enumerable.Range(0, 40).Select(i => t1 - 20 + i * 0.5).ToList();
        var downs = beats.Where((_, i) => i % 4 == 0).ToList();
        return new BeatAssist.Result(beats, downs, t1, 0.5);
    }

    public static void TestStaleResultIsDroppedAfterReset()
    {
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads ({err ?? "ok"})");
        if (bt is null) return;
        const double t1 = 1000;

        // くらべる: 取り込み直さなければ、出た結果で追跡を 120 BPM に合わせる
        using (var a = new BeatAssist(bt, 48000))
        {
            var tr = new BeatTracker(100);
            a.Deliver(Result120(t1), a.Generation);
            a.Tick(t1, tr, gateOpen: false);
            T.Check(a.Steered == 1 && Math.Abs(tr.PeriodSec - 0.5) < 0.01, $"control: a current result steers the tracker (steered {a.Steered}, {tr.PeriodSec:F3} s)");
        }

        // 結果が出たあと、使う前に取り込み直した
        using (var a = new BeatAssist(bt, 48000))
        {
            var tr = new BeatTracker(100);
            int gen = a.Generation;
            a.Deliver(Result120(t1), gen);
            a.Reset();
            a.Tick(t1, tr, gateOpen: false);
            T.Check(a.Steered == 0 && a.Runs == 0 && tr.Lock == BeatLock.Unlocked, $"a result that was ready before Reset is not used (steered {a.Steered}, lock {tr.Lock})");
        }

        // 頼んだあとに取り込み直し、そのあとで結果が出た
        using (var a = new BeatAssist(bt, 48000))
        {
            var tr = new BeatTracker(100);
            int learned = 0;
            a.Analyzed = _ => learned++;
            int gen = a.Generation;
            a.Reset();
            a.Deliver(Result120(t1), gen);
            a.Tick(t1, tr, gateOpen: false);
            T.Check(a.Steered == 0 && a.Runs == 0 && learned == 0, $"a result for audio from before Reset is dropped, also from the song memory (steered {a.Steered}, learned {learned})");
        }
    }

    /// <summary>
    /// 曲が変わった（SongChanged）あと: AI に見せる音に残っている前の曲の拍では、追跡を合わせない（前の曲の拍の並びにロックしてしまう）。
    /// 曲が変わったあとの拍が 12 個以上か 6 秒分そろえば、いつもどおり合わせる（8 個だけでは決めない）
    /// </summary>
    public static void TestOldSongBeatsAreNotUsedAfterSongChange()
    {
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads ({err ?? "ok"})");
        if (bt is null) return;
        const double t1 = 1000;
        using (var a = new BeatAssist(bt, 48000))
        {
            var tr = new BeatTracker(100);
            a.SongChanged(t1 - 4);
            a.Deliver(Result120(t1), a.Generation);
            a.Tick(t1, tr, gateOpen: false);
            T.Check(a.Steered == 0 && tr.Lock == BeatLock.Unlocked, $"song changed 4 s before the window end: the old song's beats do not steer (steered {a.Steered}, lock {tr.Lock})");
        }
        using (var a = new BeatAssist(bt, 48000))
        {
            // 窓の後ろの端（1.5 秒）を除くと 4.5 秒・10 拍: まだ決めない
            var tr = new BeatTracker(100);
            a.SongChanged(t1 - 6);
            a.Deliver(Result120(t1), a.Generation);
            a.Tick(t1, tr, gateOpen: false);
            T.Check(a.Steered == 0 && tr.Lock == BeatLock.Unlocked, $"song changed 6 s before: 10 beats of the new song are not enough yet (steered {a.Steered})");
        }
        using (var a = new BeatAssist(bt, 48000))
        {
            var tr = new BeatTracker(100);
            a.SongChanged(t1 - 12);
            a.Deliver(Result120(t1), a.Generation);
            a.Tick(t1, tr, gateOpen: false);
            T.Check(a.Steered == 1 && Math.Abs(tr.PeriodSec - 0.5) < 0.01, $"song changed 12 s before: enough beats of the new song steer as usual (steered {a.Steered}, {tr.PeriodSec:F3} s)");
        }
    }

    /// <summary>
    /// 曲が変わったあと、拍の AI の位置が表か裏かをキックで決めるとき、前の曲のキックは数えない
    /// （前の曲のキックが半拍ずれた位置にあると、新しい曲の拍の並びを半拍ずらしてしまう）
    /// </summary>
    public static void TestOldSongKicksDoNotShiftTheGrid()
    {
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads ({err ?? "ok"})");
        if (bt is null) return;
        const double t1 = 1000, change = t1 - 9;
        foreach (bool songChanged in new[] { false, true })
        {
            using var a = new BeatAssist(bt, 48000);
            var tr = new BeatTracker(100);
            if (songChanged) a.SongChanged(change);
            // 前の曲: 強いキックが半拍後ろ（AI の拍 + 0.25 秒）。新しい曲: キックは AI の拍の上
            for (int k = 0; k <= 1500; k++)
            {
                double t = t1 - 15 + k * 0.01;
                double ph = (t - (t1 - 20)) / 0.5;
                double frac = ph - Math.Floor(ph);
                double low = t < change ? (Math.Abs(frac - 0.5) < 0.011 ? 6 : 0) : (frac < 0.011 || frac > 0.989 ? 1 : 0);
                a.Tick(t, tr, gateOpen: false, 0, low);
            }
            a.Deliver(Result120(t1), a.Generation);
            a.Tick(t1 + 0.001, tr, gateOpen: false);
            double x = (tr.GridLastBeatT - (t1 - 20)) / 0.5;
            double off = Math.Abs(x - Math.Round(x));
            if (songChanged) T.Check(a.Steered == 1 && off < 0.1, $"song changed: steered on the AI's beat, the old song's off-beat kicks are not counted (phase {off:F2} beats)");
            else T.Check(a.Steered == 1 && off > 0.4, $"control without a song change: the earlier off-beat kicks do shift the AI's beat by half a beat (phase {off:F2} beats)");
        }
    }
}
