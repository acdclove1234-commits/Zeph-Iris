// ==================================================
// SeasonCheck.cs - 季節で室温が変わったら、自動セットアップの測り直しを勧める
//   (自動セットアップのカーブは、測ったときの室温での温度の上がり方から作るので、夏に室温が上がると余裕が減る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>自動セットアップを適用したときの、測ったときの様子（設定に保存する）</summary>
public sealed class SetupBaselineConfig
{
    /// <summary>適用した日時</summary>
    public DateTime AppliedAt { get; set; }
    /// <summary>測ったときの、CPU が休んでいるときの温度（°C。CPU で測っていなければ null）</summary>
    public double? CpuIdleTemp { get; set; }
    /// <summary>測ったときの室温（°C。室温のセンサーがなければ null）</summary>
    public double? RoomTemp { get; set; }
    /// <summary>最後に測り直しを勧めた日時（勧めていなければ null）</summary>
    public DateTime? RemindedAt { get; set; }
}

/// <summary>
/// 室温の上がり方を見る（1 秒ごとに Feed、ときどき Check）:
///   室温のセンサーがあれば、室温（30 分でならす）が測ったときより RoomRise °C 以上高いとき、
///   なければ、CPU が休んでいるとき（使用率 IdleLoad % 未満）の温度（30 分でならす）が IdleRise °C 以上高いとき、
///   どちらも NeedMinutes 分以上の記録があれば勧める。勧めたら RemindEveryDays 日は勧めない
/// </summary>
public sealed class SeasonCheck
{
    public const double RoomRise = 5;
    public const double IdleRise = 6;
    public const double IdleLoad = 12;
    public const double NeedMinutes = 60;
    public const double RemindEveryDays = 30;
    private const double TauSec = 30 * 60;

    private double _room = double.NaN, _idle = double.NaN;
    private double _roomSec, _idleSec;

    /// <summary>今の室温（ならした値。記録がなければ NaN）</summary>
    public double Room => _room;
    /// <summary>今の、CPU が休んでいるときの温度（ならした値。記録がなければ NaN）</summary>
    public double Idle => _idle;

    public void Feed(double? cpuTemp, double? cpuLoad, double? roomTemp, double dtSec)
    {
        if (!(dtSec > 0) || dtSec > 30) return;
        double a = 1 - Math.Exp(-dtSec / TauSec);
        if (roomTemp is double r && double.IsFinite(r) && r is > 0 and < 60)
        {
            _room = double.IsNaN(_room) ? r : _room + (r - _room) * a;
            _roomSec += dtSec;
        }
        if (cpuTemp is double c && double.IsFinite(c) && c is > 0 and < 110 && cpuLoad is double l && l < IdleLoad)
        {
            _idle = double.IsNaN(_idle) ? c : _idle + (c - _idle) * a;
            _idleSec += dtSec;
        }
    }

    /// <summary>
    /// 測ったときの様子が残っていないとき（前の版で自動セットアップをした）: NeedMinutes 分以上の記録があれば、今の様子を基準にする
    /// </summary>
    public SetupBaselineConfig? TrySeed(DateTime now)
    {
        if (_idleSec < NeedMinutes * 60 || double.IsNaN(_idle)) return null;
        return new SetupBaselineConfig
        {
            AppliedAt = now,
            CpuIdleTemp = Math.Round(_idle, 1),
            RoomTemp = _roomSec >= NeedMinutes * 60 && !double.IsNaN(_room) ? Math.Round(_room, 1) : null,
        };
    }

    /// <summary>測り直しを勧めるか。勧めるなら、上がった温度（°C）と、室温で見たか（false なら CPU の休んでいるときの温度）</summary>
    public (bool Remind, double Rise, bool ByRoom) Check(SetupBaselineConfig? b, DateTime now)
    {
        if (b is null) return (false, 0, false);
        if (b.RemindedAt is DateTime last && (now - last).TotalDays < RemindEveryDays) return (false, 0, false);
        if (b.RoomTemp is double room0 && _roomSec >= NeedMinutes * 60 && !double.IsNaN(_room))
        {
            double rise = _room - room0;
            return (rise >= RoomRise, rise, true);
        }
        if (b.CpuIdleTemp is double idle0 && _idleSec >= NeedMinutes * 60 && !double.IsNaN(_idle))
        {
            double rise = _idle - idle0;
            return (rise >= IdleRise, rise, false);
        }
        return (false, 0, false);
    }
}
