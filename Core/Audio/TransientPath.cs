// ==================================================
// TransientPath.cs - ハイハット・スネアの遅れの少ない立ち上がりの時刻 (FFT を使わず、音の波から直接求める。KickPath の高い音版)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.CompilerServices;

namespace ProFanController;

/// <summary>
/// 高い音の立ち上がりの時刻（FFT を使わず、音の波から直接求める）。
///   1. ハイパス（2 次を 2 回 = 4 次）で、その音の帯だけにする（ローパスもあれば 2 次を 1 回）
///      ・ハイハット: 5 kHz 以上
///      ・スネアの響き線の音: 1〜5 kHz
///   2. 絶対値を、上がるのは 1 ミリ秒・下がるのは数十ミリ秒で追いかける（エンベロープ）
///   3. 検出したときに、直近の山と、その前の谷の dB でちょうど中間を初めて超えた点を「立ち上がり」とする（KickPath と同じ）
/// FFT は窓の長さ（1024 個で 21 ミリ秒・2048 個で 43 ミリ秒）の分だけ遅れて時刻もぼやけるが、こちらは 1〜3 ミリ秒しか遅れない。
/// 取り込みスレッドからだけ使う
/// </summary>
public sealed class TransientPath
{
    /// <summary>包絡線の山から、立ち上がりの中点までの、実際の音の始まりからの遅れ（秒。フィルターと 1 ミリ秒の追従で少し遅れる分）</summary>
    private const double OnsetLagSec = 0.0005;

    private readonly int _sampleRate;
    private readonly double _highHz, _lowHz;
    private Biquad _hp1, _hp2, _lp;
    private readonly double _aAttack, _aRelease;
    private double _env;
    private readonly float[] _envRing;
    private readonly int _envMask;
    private int _envPos;
    private readonly int _search;

    /// <param name="sampleRate">サンプリング周波数（Hz）</param>
    /// <param name="highPassHz">ハイパスの区切り（Hz）</param>
    /// <param name="lowPassHz">ローパスの区切り（Hz。0 ならローパスなし）</param>
    /// <param name="attackSec">包絡線の上がる速さ（秒）</param>
    /// <param name="releaseSec">包絡線の下がる速さ（秒）</param>
    /// <param name="searchSec">立ち上がりを探す範囲（秒。新しい端から）</param>
    /// <param name="ringSec">包絡線を覚えておく長さの下限（秒。0 なら探す範囲の 2 倍あまり）</param>
    public TransientPath(int sampleRate, double highPassHz, double lowPassHz, double attackSec, double releaseSec, double searchSec, double ringSec = 0)
    {
        _sampleRate = sampleRate;
        _highHz = highPassHz;
        _lowHz = lowPassHz > 0 && lowPassHz < sampleRate * 0.45 ? lowPassHz : 0;
        _aAttack = 1 - Math.Exp(-1.0 / (attackSec * sampleRate));
        _aRelease = 1 - Math.Exp(-1.0 / (releaseSec * sampleRate));
        _search = Math.Max(8, (int)Math.Round(searchSec * sampleRate));
        int size = 1;
        while (size < 2 * _search + 2 || size < ringSec * sampleRate) size <<= 1;
        _envRing = new float[size];
        _envMask = size - 1;
        Reset();
    }

    /// <summary>ハイハット用（5 kHz 以上・上がり 1 ミリ秒・下がり 30 ミリ秒・直近 24 ミリ秒を探す）</summary>
    public static TransientPath ForHat(int sampleRate) => new(sampleRate, 5000, 0, 0.001, 0.030, 0.024);

    /// <summary>
    /// スネアの響き線用（1〜5 kHz・上がり 1 ミリ秒・下がり 50 ミリ秒・直近 32 ミリ秒を探す）。
    /// 長い FFT のスネアの候補を、少しあとから RiseOverMaxAt で確かめるので、包絡線は 120 ミリ秒以上覚えておく
    /// </summary>
    public static TransientPath ForSnare(int sampleRate) => new(sampleRate, 1000, 5000, 0.001, 0.050, 0.032, 0.120);

    /// <summary>スネアの胴の音用（150〜400 Hz・上がり 2 ミリ秒・下がり 40 ミリ秒・直近 24 ミリ秒を探す）</summary>
    public static TransientPath ForSnareBody(int sampleRate) => new(sampleRate, 150, 400, 0.002, 0.040, 0.024);

    /// <summary>今の包絡線の値（振幅。テスト・調査用）</summary>
    public double Env => _env;

    /// <summary>直近の探す範囲の中の、包絡線のいちばん大きい値（dB。振幅。フルスケールのサイン波の山 = 0 dB 付近）</summary>
    public double RecentPeakDb()
    {
        int newest = (_envPos - 1) & _envMask;
        float peak = 0;
        for (int k = 0; k < _search; k++)
        {
            float v = _envRing[(newest - k) & _envMask];
            if (v > peak) peak = v;
        }
        return 20 * Math.Log10(peak + 1e-12);
    }

    /// <summary>
    /// 新しい端から agoSamples 個前に始まった音で、この帯の包絡線がどれだけ上がったか（dB。状態は変えない）。
    /// 山 = 始まりの 1 ミリ秒前から afterSec 後までのいちばん大きい値、谷 = その前 beforeSec のいちばん小さい値。
    /// キックの「カチッ」（ばちが当たる音）が、キックの始まりと同時に出ているかを確かめるのに使う
    /// </summary>
    public double RiseAt(int agoSamples, double beforeSec, double afterSec, out double peakDb)
    {
        int newest = (_envPos - 1) & _envMask;
        int lead = (int)Math.Round(0.001 * _sampleRate);
        int from = Math.Max(0, agoSamples - (int)Math.Round(afterSec * _sampleRate));
        int to = Math.Min(_envMask, agoSamples + lead);
        int pre = Math.Min(_envMask, to + (int)Math.Round(beforeSec * _sampleRate));
        float peak = 0, trough = float.MaxValue;
        for (int k = from; k <= to; k++) peak = Math.Max(peak, _envRing[(newest - k) & _envMask]);
        for (int k = to + 1; k <= pre; k++) trough = Math.Min(trough, _envRing[(newest - k) & _envMask]);
        peakDb = 20 * Math.Log10(peak + 1e-12);
        if (peak <= 1e-9f || trough == float.MaxValue) return 0;
        return 20 * Math.Log10(peak / Math.Max(trough, 1e-9f));
    }

    /// <summary>
    /// 新しい端から agoSamples 個前の時刻で、この帯の包絡線が、その前よりどれだけ上がったか（dB。状態は変えない）。
    /// 山 = agoSamples から afterSec 後（新しい端まで）のいちばん大きい値、前 = その前 beforeSec のいちばん大きい値。
    /// RiseAt は前のいちばん小さい値（谷）と比べるが、こちらはいちばん大きい値と比べる。
    /// 低い音がずっと鳴っていると包絡線が細かく揺れて、谷と比べると 3〜4 dB 上がって見えるが、いちばん大きい値と比べると 1 dB も上がらない。
    /// 見る範囲が記録より長ければ、記録のある分だけ見る
    /// </summary>
    public double RiseOverMaxAt(int agoSamples, double beforeSec, double afterSec)
    {
        int newest = (_envPos - 1) & _envMask;
        int to = Math.Clamp(agoSamples, 0, _envMask);
        int from = Math.Max(0, to - (int)Math.Round(afterSec * _sampleRate));
        int pre = Math.Min(_envMask, to + (int)Math.Round(beforeSec * _sampleRate));
        float peak = 0, before = 0;
        for (int k = from; k <= to; k++) peak = Math.Max(peak, _envRing[(newest - k) & _envMask]);
        for (int k = to + 1; k <= pre; k++) before = Math.Max(before, _envRing[(newest - k) & _envMask]);
        if (peak <= 1e-9f || pre <= to) return 0;
        return 20 * Math.Log10(peak / Math.Max(before, 1e-9f));
    }

    public void Reset()
    {
        _hp1 = Biquad.HighPass(_sampleRate, _highHz, 0.7071);
        _hp2 = Biquad.HighPass(_sampleRate, _highHz, 0.7071);
        _lp = _lowHz > 0 ? Biquad.LowPass(_sampleRate, _lowHz, 0.7071) : default;
        _env = 0;
        Array.Clear(_envRing);
        _envPos = 0;
    }

    /// <summary>音を 1 個通す（取り込みスレッドから、すべての音に対して呼ぶ）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample(double x)
    {
        double y = _hp2.Process(_hp1.Process(x));
        if (_lowHz > 0) y = _lp.Process(y);
        y = Math.Abs(y);
        _env += (y - _env) * (y > _env ? _aAttack : _aRelease);
        _envRing[_envPos] = (float)_env;
        _envPos = (_envPos + 1) & _envMask;
    }

    /// <summary>512 個ごとに呼ぶ（無音が続いたときの非正規化数を防ぐ）</summary>
    public void EndHop()
    {
        _hp1.Flush();
        _hp2.Flush();
        if (_lowHz > 0) _lp.Flush();
        if (_env < 1e-20) _env = 0;
    }

    /// <summary>
    /// 直近の範囲の中で、包絡線が立ち上がった時刻を探す（KickPath.TryFindOnset と同じ決め方。状態は変えない）。
    ///   1. いちばん大きい点（山）を探す
    ///   2. 山より前（さらに同じ長さまで）でいちばん小さい点（谷）を探す
    ///   3. 谷から山へ上がる途中で、dB でちょうど中間を初めて超えた点を「立ち上がり」とする
    /// 山が谷より minRiseDb 以上大きくなければ false。
    /// samplesAgo = 立ち上がりが、いちばん新しい音から何個前か（実際の音の始まりに合わせて少し戻してある）
    /// </summary>
    public bool TryFindOnset(double minRiseDb, out int samplesAgo, out double riseDb)
    {
        samplesAgo = 0;
        riseDb = 0;
        int newest = (_envPos - 1) & _envMask;
        int peakAgo = 0;
        float peak = -1;
        for (int k = 0; k < _search; k++)
        {
            float v = _envRing[(newest - k) & _envMask];
            if (v > peak)
            {
                peak = v;
                peakAgo = k;
            }
        }
        if (peak <= 1e-9f) return false;
        int troughAgo = peakAgo;
        float trough = peak;
        for (int k = peakAgo; k < peakAgo + _search && k <= _envMask; k++)
        {
            float v = _envRing[(newest - k) & _envMask];
            if (v < trough)
            {
                trough = v;
                troughAgo = k;
            }
        }
        riseDb = 20 * Math.Log10(peak / Math.Max(trough, 1e-9f));
        if (riseDb < minRiseDb) return false;
        double mid = Math.Sqrt(peak * (double)Math.Max(trough, 1e-9f));
        int crossAgo = peakAgo;
        for (int k = troughAgo; k >= peakAgo; k--)
        {
            if (_envRing[(newest - k) & _envMask] >= mid)
            {
                crossAgo = k;
                break;
            }
        }
        samplesAgo = crossAgo + (int)Math.Round(OnsetLagSec * _sampleRate);
        return true;
    }
}
