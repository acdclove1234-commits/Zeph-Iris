// セットアップウィザードの最初の推定（どれがポンプか・使わない端子）と、ファンの下限（SetupGuess）
using ProFanController;

namespace LogicTests;

public static class SetupGuessTests
{
    public static void TestRoleGuess()
    {
        T.Check(SetupGuess.Role("Fan #1", 900) == SetupGuess.Fan, "ordinary speed → fan");
        T.Check(SetupGuess.Role("Fan #2", 0) == SetupGuess.Unused, "0 rpm → unused (nothing connected or stopped)");
        T.Check(SetupGuess.Role("Fan #3", 2800) == SetupGuess.Pump, "very fast → pump (pumps stay on BIOS by default)");
        T.Check(SetupGuess.Role("Pump Fan", 1300) == SetupGuess.Pump, "named Pump → pump even when slow");
        T.Check(SetupGuess.Role("W_PUMP+", 0) == SetupGuess.Pump, "W_PUMP → pump even with no reading");
        T.Check(SetupGuess.Role("AIO Pump", 1800) == SetupGuess.Pump, "AIO → pump");
    }

    public static void TestMaybePumpHint()
    {
        T.Check(SetupGuess.MaybePump(SetupGuess.Fan, 1800), "fast 'fan' → hint that it may be the pump");
        T.Check(!SetupGuess.MaybePump(SetupGuess.Fan, 1200), "normal fan → no hint");
        T.Check(!SetupGuess.MaybePump(SetupGuess.Pump, 1800), "already a pump → no hint");
    }

    public static void TestFanMinDuty()
    {
        double pumpMin = FanEngine.PumpHardMinDuty;
        T.Check(SetupGuess.FanMinDuty(null, 900, pumpMin) == SetupGuess.DefaultFanMinDuty, "not measured, normal fan → 25%");
        T.Check(SetupGuess.FanMinDuty(null, 1900, pumpMin) == pumpMin, "not measured, fast (maybe a pump) → not below the pump minimum");
        T.Check(SetupGuess.FanMinDuty(20, 1900, pumpMin) == 25, "measured → stop duty + 5 (the measurement wins)");
        T.Check(SetupGuess.FanMinDuty(70, 900, pumpMin) == 60, "measured → capped at 60%");
    }
}
