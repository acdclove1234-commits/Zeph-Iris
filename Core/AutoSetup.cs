// ==================================================
// AutoSetup.cs - 自動セットアップの手順を決める (ファンを調べる → CPU・GPU で測ってカーブを作る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>自動セットアップで選んだファン</summary>
/// <param name="Kinds">今つながっている温度の種類（CPU・GPU など）</param>
/// <param name="CanCalibrate">回転数を読めるか（読めないファンはキャリブレーションできない）</param>
/// <param name="Calibrated">キャリブレーション済みか</param>
public sealed record SetupFan(string ControlId, string Name, SensorKind[] Kinds, bool CanCalibrate, bool Calibrated);

public enum SetupStepKind { Calibrate, TuneCpu, TuneGpu }

/// <summary>自動セットアップの 1 手順（キャリブレーションは 1 台ずつ、測定は CPU・GPU ごとにまとめて）</summary>
public sealed record SetupStep(SetupStepKind Kind, IReadOnlyList<(string ControlId, string Name)> Fans);

public static class AutoSetupPlan
{
    /// <summary>キャリブレーション 1 台の目安（秒）</summary>
    public const double CalibrateSec = 100;
    /// <summary>CPU・GPU の測定 1 回の目安（秒）</summary>
    public const double TuneSec = 600;

    /// <summary>
    /// 手順を作る。
    /// 1. まだ測っていないファンのキャリブレーション（負荷をかける前に。温度が高いとキャリブレーションは中止されるため）
    /// 2. CPU の測定: CPU の温度につながっているファン（どの温度にもつながっていないファンもここ）
    /// 3. GPU の測定: GPU の温度につながっているファン
    /// 対象のファンがない測定は作らない
    /// </summary>
    public static List<SetupStep> Build(IReadOnlyList<SetupFan> selected, bool cpu, bool gpu, bool calibrateMissing)
    {
        var steps = new List<SetupStep>();
        if (calibrateMissing)
            foreach (var f in selected.Where(f => f.CanCalibrate && !f.Calibrated))
                steps.Add(new SetupStep(SetupStepKind.Calibrate, new[] { (f.ControlId, f.Name) }));

        static bool IsGpu(SensorKind k) => k is SensorKind.Gpu or SensorKind.GpuHotspot;
        if (cpu)
        {
            var fans = selected.Where(f => f.Kinds.Contains(SensorKind.Cpu) || !f.Kinds.Any(k => k == SensorKind.Cpu || IsGpu(k)))
                .Select(f => (f.ControlId, f.Name)).ToList();
            if (fans.Count > 0) steps.Add(new SetupStep(SetupStepKind.TuneCpu, fans));
        }
        if (gpu)
        {
            var fans = selected.Where(f => f.Kinds.Any(IsGpu)).Select(f => (f.ControlId, f.Name)).ToList();
            if (fans.Count > 0) steps.Add(new SetupStep(SetupStepKind.TuneGpu, fans));
        }
        return steps;
    }

    /// <summary>全体の目安（分。進み具合の表示用）</summary>
    public static int EstimateMinutes(IEnumerable<SetupStep> steps)
        => (int)Math.Ceiling(steps.Sum(s => s.Kind == SetupStepKind.Calibrate ? CalibrateSec : TuneSec) / 60);
}
