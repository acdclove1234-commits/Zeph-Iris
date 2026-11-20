// ==================================================
// PresenceMeter.cs - 音の大きさの手ごたえ（0〜1）を求める (映画の静かな場面・部屋の音で、光りすぎないように)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 音の手ごたえ: 自動の音量調整は、映画の夜の環境音や部屋の音のような小さな音も数秒で「最大」に合わせるので、
/// そのままでは静かな場面ほど明るく・激しく光ってしまう。調整の前の音量（dB）で見て、静かな場面では明るさを下げる。
///   いまの音量 L = 聞こえ方に合わせた音量を、上がるとき 0.6 秒・下がるとき 1.5 秒でならした値（今の値 − 6 dB のほうが大きければそれ。
///                  大きな音にはすぐ反応し、虫の声のような一瞬の小さな音では上がらない）
///   大きいところ R = max(L, R − 0.25 dB/秒)（SectionMeter と同じ。音が止まっている間は止まった値のまま）
///   くらべた値 = clamp(1 − (R − L − 16 dB) / 10 dB, 0, 1) … 曲の静かな部分（6〜14 dB 下）では下げない
///   音量そのもの = clamp((L + 52 dB) / 8 dB, 0, 1) … −44 dB より小さい音だけ下げる（無音のあとの部屋の音など）
///   手ごたえ = 小さいほう（下がるときは 0.8 秒でならし、上がるときはすぐ戻す: 大きな音にはすぐ反応する）
/// 明るさは 手ごたえ 0 で 10%、1 でそのまま。手ごたえが 0.15 未満の間は静かな場面とし、更新の遅いデバイス（メモリなど）は
/// 音が止まったときの光り方にする。
/// 音が 2 秒以上止まったら、次の音で R を測り直す（曲の切れ目のあとの静かな曲の頭を暗くしない）。
/// 銃声・爆発などの出来事は Lift で、その音量の分だけすぐ上げる（ならした音量が追いつく前に、光の頭が暗く送られていた。
/// 1 秒に 10 回しか送れないメモリでは、その 1 回が光のいちばん明るいところだった）
/// </summary>
public sealed class PresenceMeter
{
    public const double RiseSec = 0.6;
    public const double FallSec = 1.5;
    public const double ForgetDbPerSec = 0.25;
    /// <summary>大きいところより、これ以上小さい音から明るさを下げる（dB）</summary>
    public const double QuietBelowDb = 16;
    public const double QuietRangeDb = 10;
    /// <summary>この音量（dB）で 0、これに FloorRangeDb を足した音量で 1</summary>
    public const double FloorDb = -52;
    public const double FloorRangeDb = 8;
    /// <summary>今の値は、これだけ引いてから使う（dB。一瞬の小さな音で手ごたえを上げない）</summary>
    public const double NowMarginDb = 6;
    public const double DownSec = 0.8;
    public const double UpSec = 0.03;
    /// <summary>音がこの秒数止まったら、大きいところを測り直す</summary>
    public const double ResetAfterSilenceSec = 2;
    /// <summary>手ごたえがこれ未満になったら静かな場面とする（遅いデバイスは音が止まったときの光り方へ。IdleMixer）。QuietExitValue 以上で戻す</summary>
    public const float QuietValue = 0.15f, QuietExitValue = 0.4f;
    /// <summary>手ごたえが 0 のときの明るさの倍率</summary>
    public const float MinGain = 0.1f;

    private double _level = double.NaN;
    private double _ref = double.NaN;
    private double _closedFor;

    /// <summary>調べる用: false なら、いつも 1（明るさを変えない）</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>いまの手ごたえ（0〜1）</summary>
    public float Value { get; private set; } = 1;

    /// <summary>静かな場面か（虫の声のような小さな音で、行ったり来たりしないよう QuietValue〜QuietExitValue の幅を持たせる）</summary>
    public bool Hushed { get; private set; }

    /// <summary>明るさの倍率（MinGain〜1）</summary>
    public float Gain => MinGain + (1 - MinGain) * Value;

    public void Reset()
    {
        _level = _ref = double.NaN;
        _closedFor = 0;
        Value = 1;
        Hushed = false;
    }

    /// <summary>1 コマ進める（音が鳴っていないコマは、止まっている時間だけ数える）</summary>
    public float Update(AudioFrame f, double dt)
    {
        if (!Enabled) { Hushed = false; return Value = 1; }
        if (!(dt > 0)) return Value;
        if (!f.GateOpen || f.LoudDb <= -100)
        {
            _closedFor += dt;
            return Value;
        }
        double x = f.LoudDb;
        if (double.IsNaN(_level) || _closedFor >= ResetAfterSilenceSec)
        {
            _level = _ref = x;
        }
        else
        {
            double tau = x > _level ? RiseSec : FallSec;
            _level += (x - _level) * (1 - Math.Exp(-dt / tau));
            _ref = Math.Max(_level, _ref - ForgetDbPerSec * dt);
        }
        _closedFor = 0;
        double now = Math.Max(_level, x - NowMarginDb);
        double rel = Math.Clamp(1 - (_ref - now - QuietBelowDb) / QuietRangeDb, 0, 1);
        double abs = Math.Clamp((now - FloorDb) / FloorRangeDb, 0, 1);
        double target = Math.Min(rel, abs);
        double v = Value;
        v += (target - v) * (1 - Math.Exp(-dt / (target > v ? UpSec : DownSec)));
        Value = (float)Math.Clamp(v, 0, 1);
        Hushed = Hushed ? Value < QuietExitValue : Value < QuietValue;
        return Value;
    }

    /// <summary>
    /// 出来事（銃声・キックなど）の音量 db で、手ごたえをすぐ上げる（次のコマを待たない）。
    /// Update と同じ式なので、1〜2 コマ後に Update が届く値より上にはしない。部屋の小さな音では上がらない
    /// </summary>
    public void Lift(float db)
    {
        if (!Enabled || float.IsNaN(db) || double.IsNaN(_level)) return;
        double now = Math.Max(_level, db - NowMarginDb);
        double rel = Math.Clamp(1 - (Math.Max(_ref, now) - now - QuietBelowDb) / QuietRangeDb, 0, 1);
        double abs = Math.Clamp((now - FloorDb) / FloorRangeDb, 0, 1);
        double target = Math.Min(rel, abs);
        if (target > Value) Value = (float)target;
        Hushed = Hushed ? Value < QuietExitValue : Value < QuietValue;
    }
}
