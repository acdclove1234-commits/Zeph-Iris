// ==================================================
// SectionMeter.cs - 曲の盛り上がり（0〜1）を求める (静かな A メロでは控えめに、サビでは大きく光らせるため)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 曲の盛り上がり: いまの部分の音量と、曲の大きいところの音量との差から 0〜1 を求める。
///   いまの部分 L = 聞こえ方に合わせた音量（dB）を、上がるとき 0.6 秒・下がるとき 1.5 秒でならした値
///   曲の大きいところ R = max(L, R − 0.25 dB/秒)   … 大きいところを覚えておき、ゆっくり忘れる（1 分で 15 dB）
///   盛り上がり = clamp(1 − (R − L) / 12 dB, 0, 1)
/// 自動の音量調整（基準は 3 dB/秒で下がる）は、静かな部分も数秒で「最大」に合わせてしまうので、その前の音量で見る。
/// 音が止まっている間は止まった値のまま（曲間の無音で、次の曲を暗く始めない）
/// </summary>
public sealed class SectionMeter
{
    public const double RiseSec = 0.6;
    public const double FallSec = 1.5;
    public const double ForgetDbPerSec = 0.25;
    public const double RangeDb = 12;

    private double _level = double.NaN;
    private double _ref = double.NaN;

    /// <summary>調べる用: false なら、いつも 1（盛り上がりで明るさを変えない）</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>いまの盛り上がり（0〜1）</summary>
    public float Value { get; private set; } = 1;

    public void Reset()
    {
        _level = _ref = double.NaN;
        Value = 1;
    }

    /// <summary>1 コマ進める（音が鳴っていないコマは何もしない）</summary>
    public float Update(AudioFrame f, double dt)
    {
        if (!Enabled) return Value = 1;
        if (!f.GateOpen || f.LoudDb <= -100 || !(dt > 0)) return Value;
        double x = f.LoudDb;
        if (double.IsNaN(_level))
        {
            _level = _ref = x;
        }
        else
        {
            double tau = x > _level ? RiseSec : FallSec;
            _level += (x - _level) * (1 - Math.Exp(-dt / tau));
            _ref = Math.Max(_level, _ref - ForgetDbPerSec * dt);
        }
        Value = (float)Math.Clamp(1 - (_ref - _level) / RangeDb, 0, 1);
        return Value;
    }
}
