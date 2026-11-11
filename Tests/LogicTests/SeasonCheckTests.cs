// 季節で室温が上がったら、自動セットアップの測り直しを勧める（SeasonCheck）
using ProFanController;

namespace LogicTests;

public static class SeasonCheckTests
{
    private static SeasonCheck Fed(double minutes, double? cpu, double? load, double? room)
    {
        var s = new SeasonCheck();
        for (double t = 0; t < minutes * 60; t++) s.Feed(cpu, load, room, 1);
        return s;
    }

    public static void TestRoomRise()
    {
        var now = new DateTime(2027, 7, 20);
        var b = new SetupBaselineConfig { AppliedAt = now.AddMonths(-6), CpuIdleTemp = 38, RoomTemp = 22 };
        T.Check(!Fed(30, 45, 5, 28).Check(b, now).Remind, "less than an hour of data: wait");
        var r = Fed(90, 45, 5, 28).Check(b, now);
        T.Check(r.Remind && r.ByRoom && Math.Abs(r.Rise - 6) < 0.5, $"room 6 °C warmer than at setup: remind ({r.Rise:F1})");
        T.Check(!Fed(90, 45, 5, 25).Check(b, now).Remind, "room 3 °C warmer: no");
        b.RemindedAt = now.AddDays(-10);
        T.Check(!Fed(90, 45, 5, 28).Check(b, now).Remind, "reminded 10 days ago: not again");
        b.RemindedAt = now.AddDays(-40);
        T.Check(Fed(90, 45, 5, 28).Check(b, now).Remind, "after 30 days: again");
        T.Check(!Fed(90, 45, 5, 28).Check(null, now).Remind, "never ran auto setup: nothing");
    }

    public static void TestIdleRiseWithoutRoomSensor()
    {
        var now = new DateTime(2027, 7, 20);
        var b = new SetupBaselineConfig { AppliedAt = now.AddMonths(-6), CpuIdleTemp = 38 };
        var hot = Fed(90, 45, 5, null).Check(b, now);
        T.Check(hot.Remind && !hot.ByRoom && Math.Abs(hot.Rise - 7) < 0.5, $"CPU idle 7 °C warmer: remind ({hot.Rise:F1})");
        T.Check(!Fed(90, 42, 5, null).Check(b, now).Remind, "CPU idle 4 °C warmer: no");
        T.Check(!Fed(90, 70, 60, null).Check(b, now).Remind, "busy CPU (games) is not counted as idle");
    }

    public static void TestSeedForOlderSetups()
    {
        T.Check(Fed(30, 40, 5, 23).TrySeed(DateTime.Now) is null, "needs an hour of data to seed");
        var seed = Fed(90, 40, 5, 23).TrySeed(DateTime.Now);
        T.Check(seed is { CpuIdleTemp: 40, RoomTemp: 23 }, $"seeds from the current idle and room temperature ({seed?.CpuIdleTemp}, {seed?.RoomTemp})");
        var noRoom = Fed(90, 40, 5, null).TrySeed(DateTime.Now);
        T.Check(noRoom is { CpuIdleTemp: 40, RoomTemp: null }, "without a room sensor only the idle temperature");
    }
}
