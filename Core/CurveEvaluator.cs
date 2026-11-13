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

public sealed class CurveEvaluator
{
    private readonly CurvePoint[] _points;
    private double? _ema;
    private double? _effective;
    // 一瞬の跳ねを除くための直近 3 回の生温度（古い順）
    private double _r0, _r1;
    private int _samples;

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
        _r0 = old._r0;
        _r1 = old._r1;
        _samples = old._samples;
        LastRequest = old.LastRequest;
    }

    /// <summary>直近の生温度を受け取り、要求 Duty(%) を返す。dtSeconds は前の周期から経った時間</summary>
    public double Evaluate(double rawTemp, double dtSeconds = 1.0)
    {
        // 1. 一瞬の跳ねを除く: 直近 3 回の真ん中を使う。
        //    Intel の CPU などは 1 回だけ温度が跳ねることがあり、そのたびにファンが回り上がって、
        //    下げのヒステリシスのせいで上がったまま残るのを防ぐ（続いて上がったときは 1 周期遅れで追従する）
        double filtered = _samples >= 2 ? CurveMath.Median3(_r0, _r1, rawTemp) : rawTemp;
        _r0 = _r1;
        _r1 = rawTemp;
        if (_samples < 2) _samples++;

        // 2. EMA でセンサーのノイズをならす（ならし具合は 1 秒あたり。更新間隔が変わっても同じ速さになる）
        double alpha = CurveMath.AlphaFor(Config.EmaAlpha, dtSeconds);
        _ema = _ema is null ? filtered : alpha * filtered + (1 - alpha) * _ema.Value;

        // 3. ヒステリシス: 上昇には即追従、下降は HysteresisCelsius 分だけ遅れて追従
        //    （effective は常に [ema, ema + hysteresis] の範囲に収まる）
        double ema = _ema.Value;
        _effective = Math.Clamp(_effective ?? ema, ema, ema + Config.HysteresisCelsius);

        double req = CurveMath.Interpolate(_points, _effective.Value);
        LastRequest = req;
        return req;
    }
}
