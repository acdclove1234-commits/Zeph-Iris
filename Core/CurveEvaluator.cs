// ==================================================
// CurveEvaluator.cs - 温度→Duty 変換 (一瞬の跳ねの除去 + EMA平滑化 + ヒステリシス + 線形補間)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class CurveMath
{
    /// <summary>温度順に並んだ点列を線形補間する。範囲外は端の値で頭打ち</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> sorted, double t)
    {
        if (sorted.Count == 0) return 100.0;
        if (t <= sorted[0].Temp) return sorted[0].Duty;
        if (t >= sorted[^1].Temp) return sorted[^1].Duty;

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (t <= b.Temp)
            {
                double range = b.Temp - a.Temp;
                if (range <= 0.001) return b.Duty;
                return a.Duty + (t - a.Temp) / range * (b.Duty - a.Duty);
            }
        }
        return 100.0;
    }

    /// <summary>
    /// 1 秒あたりのならし具合 alpha（設定の値）を、実際に経った時間 dt 秒ぶんに直す。
    /// 更新間隔を 250ms にしても 3 秒にしても、温度のならし方（反応の速さ）が変わらないようにする
    /// </summary>
    public static double AlphaFor(double alphaPerSecond, double dtSeconds)
    {
        double a = Math.Clamp(alphaPerSecond, 0, 1);
        if (a >= 1) return 1;
        double dt = Math.Clamp(dtSeconds, 0.01, 10);
        return 1 - Math.Pow(1 - a, dt);
    }

    /// <summary>3 つの値の真ん中</summary>
    public static double Median3(double a, double b, double c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}

/// <summary>
/// 一瞬の跳ねを除くための「直近の値の真ん中」。回数ではなく直近 WindowSeconds 秒ぶん（最低 3 回分）の真ん中を使う。
/// 1 秒ごとの更新なら直近 3 回の真ん中（これまでの Median3 と同じ）。更新間隔を 250ms に縮めても、
/// 無視できる跳ねの長さ（約 1 秒）と、本当の上昇に追いつくまでの遅れ（約 1 秒）は変わらない
/// </summary>
public sealed class SpikeMedian
{
    /// <summary>この秒数より古い値は使わない</summary>
    public const double WindowSeconds = 2.0;
    /// <summary>更新間隔のわずかなずれで、使う値の数が増えたり減ったりしないための余裕（秒）</summary>
    private const double Slack = 0.1;
    private const int MinSamples = 3;
    private const int MaxSamples = 64;
    // 古い順の (値, 経過秒)
    private readonly List<(double Value, double Age)> _samples = new();
    private readonly double[] _sort = new double[MaxSamples];

    /// <summary>今の値と、前の値から経った時間（秒）を受け取り、跳ねを除いた値を返す</summary>
    public double Next(double value, double dtSeconds)
    {
        double dt = Math.Clamp(dtSeconds, 0, 10);
        for (int i = 0; i < _samples.Count; i++) _samples[i] = (_samples[i].Value, _samples[i].Age + dt);
        // 古すぎる値を捨てる（ただし直前の 2 回分は残す。更新間隔が長くても 3 回の真ん中にする）
        while (_samples.Count > MinSamples - 1 && (_samples[0].Age > WindowSeconds + Slack || _samples.Count >= MaxSamples)) _samples.RemoveAt(0);
        _samples.Add((value, 0));
        // 始めの 2 回はそのまま使う（起動直後に反応を遅らせない）
        if (_samples.Count < MinSamples) return value;
        int n = _samples.Count;
        for (int i = 0; i < n; i++) _sort[i] = _samples[i].Value;
        Array.Sort(_sort, 0, n);
        return n % 2 == 1 ? _sort[n / 2] : (_sort[n / 2 - 1] + _sort[n / 2]) / 2;
    }

    /// <summary>設定変更で作り直したときに、途中の値を引き継ぐ</summary>
    public void CopyFrom(SpikeMedian other)
    {
        _samples.Clear();
        _samples.AddRange(other._samples);
    }
}

public sealed class CurveEvaluator
{
    /// <summary>温度が下がり始めてから、ヒステリシスの分をそのまま保つ時間（秒）。この間に戻ってくる上下のゆれでは回転を変えない</summary>
    public const double HysteresisHoldSeconds = 20;
    /// <summary>保つ時間が過ぎたあと、ヒステリシスの分が平滑化した温度へ近づく速さ（時定数・秒）</summary>
    public const double HysteresisDecaySeconds = 20;

    private readonly CurvePoint[] _points;
    private double? _ema;
    private double? _effective;
    // 最後に温度に押し上げられてから（effective が ema に追いつかれてから）経った時間（秒）
    private double _sinceRise;
    // 一瞬の跳ねを除くための直近 約 2 秒の生温度
    private readonly SpikeMedian _median = new();

    public CurveConfig Config { get; }
    public double? EffectiveTemp => _effective;
    /// <summary>温度が読めていたときに最後に出した要求 Duty(%)（まだなければ null）。センサーが途中で消えたとき、これより下げない</summary>
    public double? LastRequest { get; private set; }

    public CurveEvaluator(CurveConfig cfg)
    {
        Config = cfg;
        _points = cfg.Points.OrderBy(p => p.Temp).ToArray();
    }

    /// <summary>設定変更で作り直したときに、平滑化の途中状態を引き継いで回転が跳ねないようにする</summary>
    public void CopyStateFrom(CurveEvaluator old)
    {
        if (old.Config.SensorId != Config.SensorId) return;
        _ema = old._ema;
        _effective = old._effective;
        _sinceRise = old._sinceRise;
        _median.CopyFrom(old._median);
        LastRequest = old.LastRequest;
    }

    /// <summary>直近の生温度を受け取り、要求 Duty(%) を返す。dtSeconds は前の周期から経った時間</summary>
    public double Evaluate(double rawTemp, double dtSeconds = 1.0)
    {
        // 1. 一瞬の跳ねを除く: 直近 約 2 秒（1 秒ごとなら 3 回）の真ん中を使う。
        //    Intel の CPU などは 1 回だけ温度が跳ねることがあり、そのたびにファンが回り上がって、
        //    下げのヒステリシスのせいで上がったまま残るのを防ぐ（続いて上がったときは 約 1 秒遅れで追従する）
        double filtered = _median.Next(rawTemp, dtSeconds);

        // 2. EMA でセンサーのノイズをならす（ならし具合は 1 秒あたり。更新間隔が変わっても同じ速さになる）
        double alpha = CurveMath.AlphaFor(Config.EmaAlpha, dtSeconds);
        _ema = _ema is null ? filtered : alpha * filtered + (1 - alpha) * _ema.Value;

        // 3. ヒステリシス: 上昇には即追従、下降は HysteresisCelsius 分だけ遅れて追従
        //    （effective は常に [ema, ema + hysteresis] の範囲に収まる）。
        //    下がったまま HysteresisHoldSeconds 秒たったら、上乗せの分を少しずつ ema へ戻す
        //    （以前はずっと上乗せが残り、冷えたあとも「H°C 熱い」ときの回転のままだった）
        double ema = _ema.Value;
        if (_effective is not double prev || ema >= prev)
        {
            _effective = ema;
            _sinceRise = 0;
        }
        else
        {
            double dt = Math.Max(0, dtSeconds);
            double eff = Math.Min(prev, ema + Config.HysteresisCelsius);
            _sinceRise += dt;
            if (_sinceRise > HysteresisHoldSeconds) eff -= (eff - ema) * (1 - Math.Exp(-dt / HysteresisDecaySeconds));
            _effective = Math.Max(eff, ema);
        }

        double req = CurveMath.Interpolate(_points, _effective.Value);
        LastRequest = req;
        return req;
    }
}
