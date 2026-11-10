// ==================================================
// EnergyMeter.cs - 消費電力量と電気代の目安 (CPU・GPU のセンサー値＋その他の目安を積算して日ごとに保存)
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text.Json;

namespace ProFanController;

public sealed class DayEnergy
{
    public double Kwh { get; set; }
    public double CpuKwh { get; set; }
    public double GpuKwh { get; set; }
    /// <summary>記録した時間（アプリが動いていた時間）</summary>
    public double Hours { get; set; }
}

/// <summary>
/// 電力（W）× 時間 を足し合わせて kWh にする。CPU と GPU はセンサーの値、それ以外（マザーボード・メモリ・SSD・
/// ファン・電源の損失など）は設定した目安の W を使う。壁のコンセントで測った値ではないので「目安」として表示する。
/// 画面スレッドから使う。
/// </summary>
public sealed class EnergyMeter
{
    /// <summary>これ以上データが空いたら（スリープなど）、その間は数えない</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    public static string FilePath => Path.Combine(LongTermStore.Directory, "energy.json");

    private readonly SortedDictionary<string, DayEnergy> _days = new(StringComparer.Ordinal);
    private DateTime? _last;
    private DateTime _lastSave = DateTime.Now;

    /// <summary>コンセントから引いている電力の目安（W。電源の損失を含む）</summary>
    public double CurrentWatts { get; private set; }
    public double CpuWatts { get; private set; }
    public double GpuWatts { get; private set; }
    /// <summary>CPU・GPU 以外の分（設定の値）</summary>
    public double BaseWatts { get; private set; }
    /// <summary>電源の損失の分（W）</summary>
    public double LossWatts { get; private set; }

    /// <summary>ワットチェッカーと比べるときに平均する長さ（表示が落ち着くまでの間のセンサーの値をならす）</summary>
    public static readonly TimeSpan CalibrationWindow = TimeSpan.FromSeconds(20);
    private readonly Queue<(DateTime T, double W)> _recent = new();

    public EnergyMeter()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, DayEnergy>>(File.ReadAllText(FilePath));
                if (loaded is not null) foreach (var (k, v) in loaded) _days[k] = v;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"電力量の記録を読めません: {ex.Message}");
        }
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <param name="efficiencyPct">電源の効率（%）。PC の中で使う電力をこれで割って、コンセント側の電力にする</param>
    public void Add(DateTime time, double? cpuW, double? gpuW, double baseW, double efficiencyPct = EnergyConfig.DefaultPsuEfficiency)
    {
        CpuWatts = cpuW is > 0 and < 1000 ? cpuW.Value : 0;
        GpuWatts = gpuW is > 0 and < 1500 ? gpuW.Value : 0;
        BaseWatts = Math.Max(0, baseW);
        double inside = CpuWatts + GpuWatts + BaseWatts;
        CurrentWatts = EnergyCalibration.WallWatts(inside, efficiencyPct);
        LossWatts = CurrentWatts - inside;

        _recent.Enqueue((time, CpuWatts + GpuWatts));
        while (_recent.Count > 0 && (time - _recent.Peek().T > CalibrationWindow || _recent.Peek().T > time)) _recent.Dequeue();

        if (_last is DateTime last && time > last && time - last <= MaxGap)
        {
            double h = (time - last).TotalHours;
            if (!_days.TryGetValue(Key(time), out var day)) _days[Key(time)] = day = new DayEnergy();
            day.Kwh += CurrentWatts * h / 1000;
            day.CpuKwh += CpuWatts * h / 1000;
            day.GpuKwh += GpuWatts * h / 1000;
            day.Hours += h;
        }
        _last = time;
        if (time - _lastSave >= SaveInterval) Save();
    }

    public void Save()
    {
        _lastSave = DateTime.Now;
        try
        {
            Directory.CreateDirectory(LongTermStore.Directory);
            // 2 年より前は消す
            foreach (var k in _days.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddYears(-2))) < 0).ToList()) _days.Remove(k);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"電力量の保存に失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// 直近 CalibrationWindow の CPU＋GPU のセンサーの値の平均（W）と、揺れの幅（最大 − 最小）。
    /// 記録が 5 回分より少なければ null（起動直後・センサーがないとき）
    /// </summary>
    public (double Mean, double Spread)? RecentSensorWatts()
    {
        if (_recent.Count < 5) return null;
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        foreach (var (_, w) in _recent)
        {
            sum += w;
            min = Math.Min(min, w);
            max = Math.Max(max, w);
        }
        return (sum / _recent.Count, max - min);
    }

    public DayEnergy Day(DateTime date) => _days.TryGetValue(Key(date), out var d) ? d : new DayEnergy();

    public double TodayKwh => Day(DateTime.Today).Kwh;

    /// <summary>今月の合計（kWh）</summary>
    public double MonthKwh(DateTime month)
    {
        string prefix = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return _days.Where(d => d.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(d => d.Value.Kwh);
    }

    /// <summary>今月の見込み（記録のある日の平均 × 月の日数）</summary>
    public double MonthForecastKwh(DateTime today)
    {
        string prefix = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var days = _days.Where(d => d.Key.StartsWith(prefix, StringComparison.Ordinal) && d.Key != Key(today)).ToList();
        double total = MonthKwh(today);
        if (days.Count == 0) return total;
        double avg = days.Average(d => d.Value.Kwh);
        int remaining = DateTime.DaysInMonth(today.Year, today.Month) - today.Day;
        return total + avg * remaining;
    }

    /// <summary>直近 n 日（古い順）</summary>
    public List<(DateTime Date, DayEnergy Energy)> LastDays(int n)
        => Enumerable.Range(0, n).Select(i => DateTime.Today.AddDays(-(n - 1 - i))).Select(d => (d, Day(d))).ToList();
}

/// <summary>ワットチェッカーで合わせ込んだ結果（Error があれば使わない。Note は使えるが知らせたいこと）</summary>
public sealed record EnergyCalibrationResult(double BaseWatts, double EfficiencyPct, bool TwoPoints, string? Error, string? Note);

/// <summary>
/// コンセント側の電力の計算と、ワットチェッカーの値からの合わせ込み。
/// コンセント側 = (CPU + GPU + その他) ÷ 効率 なので、分からないのは「その他」と「効率」の 2 つ。
///   ・アイドル中だけ測ったとき（1 点）: 効率は今の設定のまま、その他 = コンセント × 効率 − (CPU + GPU)
///   ・負荷中も測ったとき（2 点）: 効率 = センサーの差 ÷ コンセントの差、その他 = アイドル中のコンセント × 効率 − アイドル中の (CPU + GPU)
/// </summary>
public static class EnergyCalibration
{
    /// <summary>設定できる電源の効率の範囲（%）</summary>
    public const double MinEfficiency = 60, MaxEfficiency = 98;
    /// <summary>2 点で効率を求めるのに必要な、アイドル中と負荷中の CPU＋GPU の差（W。小さいと誤差で効率が大きく外れる）</summary>
    public const double MinSensorGap = 60;
    /// <summary>その他の分として認める上限（W）</summary>
    public const double MaxBaseWatts = 400;

    /// <summary>PC の中で使う電力（W）を、電源の効率（%）でコンセント側の電力にする</summary>
    public static double WallWatts(double insideWatts, double efficiencyPct)
    {
        double e = efficiencyPct is >= MinEfficiency and <= MaxEfficiency ? efficiencyPct : EnergyConfig.DefaultPsuEfficiency;
        return Math.Max(0, insideWatts) * 100 / e;
    }

    /// <summary>
    /// ワットチェッカーの値を記録してよいか。読んだ値（wall）と、直近のセンサーの平均・揺れの幅を見る。null なら記録してよい
    /// </summary>
    public static string? CheckReading(double wall, (double Mean, double Spread)? sensor)
    {
        if (sensor is not { } s) return L("CPU・GPU の消費電力のセンサーの値がまだありません。少し待ってからもう一度押してください");
        if (wall is < 10 or > 3000) return L("ワットチェッカーの値は 10〜3000 W の間で入れてください");
        if (s.Spread > Math.Max(15, 0.25 * s.Mean)) return L($"CPU・GPU の消費電力が揺れています（20 秒で {s.Spread:F0} W）。負荷が落ち着いてから押してください");
        if (wall <= s.Mean) return L($"ワットチェッカーの値（{wall:F0} W）が、CPU と GPU のセンサーの値（{s.Mean:F0} W）より小さくなっています。PC 本体のコンセントで測っているか確かめてください");
        return null;
    }

    /// <summary>記録した点から、その他の W と電源の効率を求める</summary>
    public static EnergyCalibrationResult Solve(double idleWall, double idleSensor, double loadWall, double loadSensor, double currentEfficiencyPct)
    {
        double eff = currentEfficiencyPct is >= MinEfficiency and <= MaxEfficiency ? currentEfficiencyPct : EnergyConfig.DefaultPsuEfficiency;
        bool idle = idleWall > 0, load = loadWall > 0;
        string? note = null;
        if (idle && load)
        {
            double ds = loadSensor - idleSensor, dw = loadWall - idleWall;
            if (ds >= MinSensorGap && dw > 0)
            {
                double e2 = 100 * ds / dw;
                if (e2 is < MinEfficiency or > MaxEfficiency)
                    return new(0, eff, true, L($"2 回の値が合いません（電源の効率が {e2:F0}% になりました）。ワットチェッカーの値が落ち着いてから、もう一度測ってください"), null);
                double b2 = idleWall * e2 / 100 - idleSensor;
                if (b2 is < 0 or > MaxBaseWatts)
                    return new(0, eff, true, L($"2 回の値が合いません（CPU・GPU 以外が {b2:F0} W になりました）。もう一度測ってください"), null);
                return new(b2, e2, true, null, null);
            }
            note = L($"負荷中の CPU・GPU の消費電力が、アイドル中より {MinSensorGap:F0} W 以上大きくないので、電源の効率は {eff:F0}% のままにしました");
        }
        if (!idle && !load) return new(0, eff, false, L("まだ測っていません"), null);
        double wall = idle ? idleWall : loadWall, sensor = idle ? idleSensor : loadSensor;
        double b = wall * eff / 100 - sensor;
        if (b is < 0 or > MaxBaseWatts)
            return new(0, eff, false, L($"値が合いません（CPU・GPU 以外が {b:F0} W になりました）。もう一度測ってください"), null);
        return new(b, eff, false, null, note);
    }
}
