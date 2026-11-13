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
}
