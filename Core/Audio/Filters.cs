// ==================================================
// Filters.cs - 音の分析で使う小さな部品 (上がり下がりで速さの違う平滑化・指数移動平均・2 次フィルター・
//              自動音量調整の基準・無音判定の門・立ち上がりの強調・中央値)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.CompilerServices;

namespace ProFanController;

/// <summary>時定数（秒）を、1 回の更新で「目標に近づく割合」に変える</summary>
public static class TimeConst
{
    /// <summary>
    /// α = 1 − exp(−dt / τ)。dt 秒ごとに更新したとき、τ 秒で目標との差が約 37%（1/e）まで縮む割合。
    /// τ ≤ 0 なら 1（すぐに目標に追いつく）。
    /// 更新の間隔 dt を式に入れておくので、サンプリング周波数（44.1 / 48 / 96 kHz）が変わっても同じ速さで動く
    /// </summary>
    public static double Alpha(double dt, double tau) => tau <= 0 ? 1.0 : 1.0 - Math.Exp(-dt / tau);
}

/// <summary>
/// 上がるときと下がるときで速さを変えられる平滑化（エンベロープ）。
///   例: 上がる 8 ミリ秒・下がる 250 ミリ秒 → ドラムの瞬間にすぐ明るくなり、ゆっくり暗くなる
/// 時定数は秒で指定する（1 回あたりの割合を直接書くと、更新の間隔が変わったときに動きが変わってしまう）
/// </summary>
public struct AsymEnv
{
    /// <summary>いまの値</summary>
    public double V;

    /// <summary>x に向かって 1 回分近づける。tauUp = 上がるときの時定数（秒）、tauDown = 下がるときの時定数（秒）</summary>
    public double Step(double x, double dt, double tauUp, double tauDown)
    {
        double tau = x > V ? tauUp : tauDown;
        V += (x - V) * (tau <= 0 ? 1.0 : 1.0 - Math.Exp(-dt / tau));
        return V;
    }

    /// <summary>割合（TimeConst.Alpha で求めたもの）を直接渡す版。毎回 exp を計算しないので速い</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double StepA(double x, double alphaUp, double alphaDown)
    {
        V += (x - V) * (x > V ? alphaUp : alphaDown);
        return V;
    }
}

/// <summary>
/// 指数移動平均（EMA）。0 から始めると最初の数秒が小さく出すぎるので、
/// 最初のうちは「それまでの単純平均」を使い、たまってきたら指数移動平均に切り替える
/// </summary>
public struct WarmEma
{
    public double V;
    /// <summary>これまでに更新した回数</summary>
    public long N;

    public double Step(double x, double alpha)
    {
        N++;
        double a = Math.Max(alpha, 1.0 / N);
        V += (x - V) * a;
        return V;
    }

    public void Reset()
    {
        V = 0;
        N = 0;
    }
}

/// <summary>
/// 2 次の IIR フィルター（RBJ「Audio EQ Cookbook」の式）。音を 1 個ずつ通す。
/// ここではキック（バスドラム）だけを取り出す 150 Hz のローパス（それより高い音を弱める）に使う
/// </summary>
public struct Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    /// <summary>
    /// ローパスフィルターを作る。f0 = 区切りの周波数（Hz）、q = 0.7071 で区切りの付近が平ら（バターワース）。
    ///   w0 = 2π f0 / fs、α = sin w0 / (2Q)
    ///   b0 = b2 = (1 − cos w0) / 2、b1 = 1 − cos w0、a0 = 1 + α、a1 = −2 cos w0、a2 = 1 − α（すべて a0 で割る）
    /// </summary>
    public static Biquad LowPass(double sampleRate, double f0, double q)
    {
        double w0 = 2 * Math.PI * f0 / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        return new Biquad
        {
            _b0 = (1 - cos) / 2 / a0,
            _b1 = (1 - cos) / a0,
            _b2 = (1 - cos) / 2 / a0,
            _a1 = -2 * cos / a0,
            _a2 = (1 - alpha) / a0,
        };
    }

    /// <summary>
    /// ハイパスフィルターを作る（ローパスと同じ式の仲間。ハイハット・スネアの響き線の音を取り出すのに使う）。
    ///   b0 = b2 = (1 + cos w0) / 2、b1 = −(1 + cos w0)、a0・a1・a2 はローパスと同じ（すべて a0 で割る）
    /// </summary>
    public static Biquad HighPass(double sampleRate, double f0, double q)
    {
        double w0 = 2 * Math.PI * f0 / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        return new Biquad
        {
            _b0 = (1 + cos) / 2 / a0,
            _b1 = -(1 + cos) / a0,
            _b2 = (1 + cos) / 2 / a0,
            _a1 = -2 * cos / a0,
            _a2 = (1 - alpha) / a0,
        };
    }

    /// <summary>1 個通す（転置直接形 II。計算の誤差がたまりにくい形）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    /// <summary>
    /// 無音が続いて内部の値が極端に小さくなったら 0 にする。
    /// （10^-300 のような「非正規化数」は CPU の計算がとても遅くなるため。512 個ごとに 1 回呼べば十分）
    /// </summary>
    public void Flush()
    {
        if (Math.Abs(_z1) < 1e-25) _z1 = 0;
        if (Math.Abs(_z2) < 1e-25) _z2 = 0;
    }

    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
    }
}

/// <summary>
/// 直近の大きな音の高さ（dB）。大きな音が来たらすぐその高さにして、holdSec 秒止めてから、1 秒に fallDbPerSec ずつ下げる（minDb より下げない）。
/// 高さを上げるのは、音が鳴っているとき（capture）だけ。下げるのは無音の間も続ける
/// </summary>
public struct PeakHold
{
    /// <summary>いまの高さ（dB）</summary>
    public double V;
    private double _hold;

    public void Reset(double v) { V = v; _hold = 0; }

    public void Update(double x, bool capture, double dt, double holdSec, double fallDbPerSec, double minDb)
    {
        if (capture && x >= V) { V = x; _hold = holdSec; return; }
        if (_hold > 0) { _hold -= dt; return; }
        if (V > minDb) V = Math.Max(minDb, V - fallDbPerSec * dt);
    }
}

/// <summary>
/// 自動音量調整の基準（「この音量を最大として光らせる」dB 値）。
///   基準より大きい音が来たら、0.3 秒の時定数で追いつく（1 回の大きな音で、すぐに全体が暗くならないように）
///   基準より小さいときは、1 秒に 3 dB ずつ下がる（静かな曲になっても、数秒でちょうどよい明るさに戻る）
///   最小値より下がらない（無音に近いノイズを最大まで増幅しないように）
/// 音が鳴っている（門が開いている）ときだけ更新する。無音の間は固定する
/// </summary>
public struct CeilingTracker
{
    /// <summary>いまの基準（dB）</summary>
    public double C;
    /// <summary>一度でも更新した</summary>
    public bool Ready;

    /// <param name="x">今回の音量（dB）</param>
    /// <param name="alphaUp">上がるときの割合（TimeConst.Alpha(dt, 0.3)）</param>
    /// <param name="fallDb">1 回で下がる量の上限（dB。3 dB/秒 × dt）</param>
    /// <param name="minDb">これより下げない（dB）</param>
    public void Update(double x, double alphaUp, double fallDb, double minDb)
    {
        if (!Ready)
        {
            // 最初の 1 回は、いまの音量をそのまま基準にする（0 や −30 dB から追いかけ始めると最初の数秒がずれる）
            C = Math.Max(x, minDb);
            Ready = true;
            return;
        }
        if (x > C) C += (x - C) * alphaUp;
        else C -= Math.Min(C - x, fallDb);
        if (C < minDb) C = minDb;
    }
}

/// <summary>
/// 無音判定の門（ヒステリシスつき）。
///   開く: 音量が openDb を超えたら、すぐに開く（最初のドラムを逃さないため）
///   閉じる: 音量が closeDb を下回った状態が holdSec 秒続いたら閉じる
/// 開く値と閉じる値に差（7 dB）をつけて、境目の音量でパタパタ切り替わらないようにする
/// </summary>
public struct HysteresisGate
{
    public bool Open;
    private double _below;

    public bool Update(double levelDb, double dt, double openDb, double closeDb, double holdSec)
    {
        if (levelDb > openDb)
        {
            Open = true;
            _below = 0;
        }
        else if (levelDb < closeDb)
        {
            _below += dt;
            if (_below >= holdSec) Open = false;
        }
        else
        {
            _below = 0;
        }
        return Open;
    }

    public void Reset()
    {
        Open = false;
        _below = 0;
    }
}

/// <summary>
/// 立ち上がり（パンチ）の計算に使う割合をまとめたもの（1 回の分析ごとに作り直さず、値だけ入れ替える）
/// </summary>
public struct PunchCoefs
{
    /// <summary>床（ふだんの大きさ）が上がるときの割合（時定数 1.5 秒）</summary>
    public double FloorUp;
    /// <summary>床が下がるときの割合（時定数 50 ミリ秒）</summary>
    public double FloorDown;
    /// <summary>パンチが上がるときの割合（時定数 5 ミリ秒）</summary>
    public double Up;
    /// <summary>パンチが下がるときの割合（時定数 100 ミリ秒）</summary>
    public double Down;
    /// <summary>パンチを混ぜる割合（0〜1。設定の「メリハリ」）</summary>
    public double Weight;
}

/// <summary>
/// 立ち上がり（パンチ）の計算。鳴り続けている音より、急に大きくなった瞬間を強く光らせるための値。
///   床 f  … 最小値を追いかける（上がるのはゆっくり 1.5 秒、下がるのは速く 50 ミリ秒）
///   r     = (v − f) / max(0.15, 1 − f)   … 床よりどれだけ上か（残りの余裕で割る）
///   p     … r を 5 ミリ秒で上げ、100 ミリ秒で下げる
///   p'    = (1 − e^(−3p)) / (1 − e^(−3))  … 小さな値を持ち上げる（ソフトニー）
///   e     = w·p' + (1 − w)·b              … パンチと、ふつうの大きさ b を混ぜる
/// 鳴り続ける音（ボーカルの伸ばし・ベースの持続音）は床が追いつくので、パンチはだんだん 0 になる
/// </summary>
public struct PunchStream
{
    public AsymEnv Floor;
    public AsymEnv P;
    private static readonly double KneeNorm = 1.0 / (1.0 - Math.Exp(-3));

    /// <param name="v">今回の大きさ（0〜1）</param>
    /// <param name="b">ふつうの大きさ（表示用に平滑化した値、0〜1）</param>
    public double Step(double v, double b, in PunchCoefs c)
    {
        double f = Floor.V;
        double r = (v - f) / Math.Max(0.15, 1 - f);
        if (r < 0) r = 0;
        else if (r > 1) r = 1;
        Floor.StepA(v, c.FloorUp, c.FloorDown);
        double p = P.StepA(r, c.Up, c.Down);
        double knee = (1 - Math.Exp(-3 * p)) * KneeNorm;
        double e = c.Weight * knee + (1 - c.Weight) * b;
        return e < 0 ? 0 : e > 1 ? 1 : e;
    }

    public void Reset()
    {
        Floor.V = 0;
        P.V = 0;
    }
}

/// <summary>少ない数の値の中央値・平均（並べ替え用の作業配列を渡す。メモリを新しく確保しない）</summary>
public static class SmallStats
{
    /// <summary>scratch の先頭 count 個を並べ替えて中央値を返す（挿入ソート。数十個までなら速い）</summary>
    public static double MedianInPlace(double[] scratch, int count)
    {
        if (count <= 0) return 0;
        for (int i = 1; i < count; i++)
        {
            double v = scratch[i];
            int j = i - 1;
            while (j >= 0 && scratch[j] > v)
            {
                scratch[j + 1] = scratch[j];
                j--;
            }
            scratch[j + 1] = v;
        }
        int mid = count >> 1;
        return (count & 1) == 1 ? scratch[mid] : 0.5 * (scratch[mid - 1] + scratch[mid]);
    }

    /// <summary>a[i] と a[i + 1] の間を直線で補った値（x は小数の位置。範囲外は端の値）</summary>
    public static double Lerp(double[] a, double x, int count)
    {
        if (count <= 0) return 0;
        if (x <= 0) return a[0];
        int i = (int)x;
        if (i >= count - 1) return a[count - 1];
        double u = x - i;
        return a[i] + (a[i + 1] - a[i]) * u;
    }

    /// <summary>
    /// 3 点（左・中央・右）を通る放物線の頂点が、中央から何個ずれているか（−0.5〜+0.5）。
    /// 山の位置を、1 コマ（約 10.7 ミリ秒）より細かく求めるのに使う
    /// </summary>
    public static double ParabolicOffset(double left, double centre, double right)
    {
        double den = left - 2 * centre + right;
        if (Math.Abs(den) < 1e-12) return 0;
        double d = 0.5 * (left - right) / den;
        return d < -0.5 ? -0.5 : d > 0.5 ? 0.5 : d;
    }
}

/// <summary>
/// 音量（dB）の「静かなほうの 10%」の位置を追いかける（分位点の追跡）。
/// 今回の値が L より上なら step × q だけ上げ、下なら step × (1 − q) だけ下げる。これを続けると L は q 分位点に落ち着く。
/// 音の大きさが一定の曲（いまの音楽の多く）で、明るさに対応させる幅を、その曲の実際の音の上下に合わせるのに使う
/// </summary>
public struct LowTracker
{
    /// <summary>いまの値（dB）</summary>
    public double L;
    public bool Ready;

    public void Update(double x, double stepDb, double q = 0.1)
    {
        if (!double.IsFinite(x)) return;
        if (!Ready)
        {
            L = x - 6;
            Ready = true;
            return;
        }
        L += x > L ? stepDb * q : -stepDb * (1 - q);
    }

    /// <summary>基準 ceiling から、明るさに対応させる幅（dB）: 静かな側の位置 + margin。minDb〜maxDb に収める</summary>
    public readonly double Range(double ceiling, double marginDb, double minDb, double maxDb)
        => !Ready ? maxDb : Math.Clamp(ceiling - L + marginDb, minDb, Math.Max(minDb, maxDb));
}

/// <summary>
/// 強調した値（0〜1）を、その値の最近の中央値 m を基準に広げる（エキスパンダー）。
///   出力 = Keep × x + (1 − Keep) × clamp((x − m) / max(1 − m, 0.15), 0, 1)
/// 中央値くらいの値は暗く、中央値より強い瞬間（拍）だけが明るくなる。音圧の高い曲でも、静かな曲でも、拍の明暗の差が出る。
/// 中央値は 1 回ごとに Step だけ上下させて追いかける（約 1 秒で 0〜1 を動ける）
/// </summary>
public struct BeatExpander
{
    public const float Keep = 0.7f;
    private const double Step = 0.01;
    public double M;

    public float Apply(double x, bool track)
    {
        if (!double.IsFinite(x)) x = 0;
        if (track) M += x > M ? Step * 0.5 : -Step * 0.5;
        if (M < 0) M = 0;
        double r = (x - M) / Math.Max(1 - M, 0.15);
        r = r < 0 ? 0 : r > 1 ? 1 : r;
        double y = Keep * x + (1 - Keep) * r;
        return (float)(y < 0 ? 0 : y > 1 ? 1 : y);
    }
}
