// ==================================================
// SpectrumEffect.cs - 光り方 E3「スペクトラム」 (LED の並びに低音〜高音の 10 帯（31 Hz〜16 kHz）を並べ、それぞれの強さで光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// スペクトラム: 「始まり」側が低音、端が高音。LED の位置 x（0〜1）の強さは 10 帯の間を直線で補う:
///   B(x) = lerp(e_k, e_k+1, u − k)、u = x × 9、k = ⌊u⌋
///   e_k = 立ち上がりを強調した帯の強さ Punch。ただし低音の端（31・63 Hz）は、キックの弾み（KickBounce。τ 0.12 秒）で光る:
///     e_0,1 = max(弾み × 盛り上がり × s, F_k)、F_k = max(BedShare × 帯の強さ, w × Punch)
///     w = キックの MaskFromSec 後から MaskSec までは MaskShare、MaskRampSec かけて PunchShare へ戻す（同じキックの、遅れて上がる FFT の低音で光り直さない。
///         ずっと続くベースは見える）
///     e_2（125 Hz）= max(Punch, KickBandShare × 弾み × 盛り上がり × s)
///     s = UnlockedShare + (1 − UnlockedShare) × テンポをつかんでいる度合い（話し声の出だしのキックでは弱く）
///   （キックは音の高さが下がりながら鳴るので、FFT の 31・63 Hz はキックの 0.1〜0.17 秒後に上がりきっていた。キックの検出は 2〜8 ミリ秒。
///   「低音に少し反応するけど遅れて見える」という声があったため）
///   曲のビートが続いている間（テンポをつかみ、2 秒以内にキックがある）は、帯ごとに、ふだんの強さ m_k（GateOpen の間だけ 1 秒に FloorStepPerSec ずつ近づく）より上の分を強める:
///     e' = (1 − FloorShare) × e + FloorShare × clamp((e − m) / max(1 − m, FloorMinRange))
///   （ずっと鳴っているパッド・ベース・ハイハットで帯がいつも明るく、どの音で光っているのか分からなかった。話し声・雨では強めない）
///   LED ごとに、B が上がったらすぐ明るく、保ってから下げる。低音の端（x &lt; LowEdge）は LowHoldSec 保って τ LowReleaseSec、
///   それより上は SmoothHoldSec 保って τ SmoothReleaseSec（細かい刻みで上下しない）。間の LowBlend はなめらかにつなぐ。
///   帯の山を短く保つのは、曲のビートが続いている間だけ（話し声の低い声で細かく点滅しない）。キックの山はいつも短く保つ
///   ピークの保持: h = 最大値を 0.4 秒保ち、その後 1 秒に 1.5 ずつ下げる（ゴーストとして 0.35 倍で見せる）
///   LED の色 = P(x) × Ilin(max(B(x), 0.35 × h))（色は位置で決まる。どの高さの音がどの色か、覚えられる）
/// 24 個以上のゾーンは、隣どうしを [0.25, 0.5, 0.25] で 1 回ぼかす（帯の境目のカクカクを消す）。
/// 30 個以上のゾーンは自動で左右対称（中央が低音）。6 個未満のゾーンは、低音・中音・高音の強さで色が混ざる全体で 1 色（MixEffect）で光る
/// </summary>
public sealed class SpectrumEffect : IMusicEffect
{
    /// <summary>ピークを保つ秒数</summary>
    public const double HoldSec = 0.4;
    /// <summary>保った後にピークが下がる速さ（1 秒あたり）</summary>
    public const double FallPerSec = 1.5;
    /// <summary>ピークのゴーストの明るさ</summary>
    public const float GhostShare = 0.35f;
    /// <summary>この数以上の LED のゾーンはぼかす</summary>
    public const int BlurMinLeds = 24;
    /// <summary>
    /// LED ごとの強さを、上がってから保つ秒数と、その後に下がる時定数（秒。上がるときはすぐ）。帯の強さ（Punch）は音の立ち上がりの
    /// たびに一瞬だけ上がって 0.1 秒で戻るので、16 分のハイハット（0.11〜0.16 秒おき）で高音側の LED が 1 秒に何回も上下して
    /// ちらついていた（6.15.7: 向きの戻り 5.67 回／秒）。保つ間に次のハイハットが来れば、LED は明るいまま
    /// </summary>
    public const double SmoothHoldSec = 0.15, SmoothReleaseSec = 0.25;
    /// <summary>低音の端（x が LowEdge より下）の、保つ秒数と下がる時定数（秒）。キックごとにはっきり上下する</summary>
    public const double LowHoldSec = 0.03, LowReleaseSec = 0.12;
    /// <summary>低音の端の終わり（x）と、上の保ち方へなめらかにつなぐ幅</summary>
    public const float LowEdge = 0.2f, LowBlend = 0.1f;
    /// <summary>キックの弾みが戻る時定数（秒。音量メーターと同じ）</summary>
    public const double KickTauSec = 0.12;
    /// <summary>125 Hz の帯に入れるキックの弾みの割合</summary>
    public const float KickBandShare = 0.8f;
    /// <summary>低音の端で、ならした帯の強さ（ずっと続くベース）を見せる割合</summary>
    public const float BedShare = 0.45f;
    /// <summary>
    /// 低音の端で Punch を見せる割合（キックの MaskFromSec 後から MaskSec までは MaskShare、その後 MaskRampSec かけて PunchShare へ）。
    /// キックから MaskFromSec までの Punch はキックそのものなので弱めない（キックは検出の分だけ遅れて届くので、届く前のコマと
    /// 届いた後のコマで弱め方が変わると、1 秒間のコマ数で明るさが変わった）
    /// </summary>
    public const float PunchShare = 0.75f, MaskShare = 0.4f;
    public const double MaskFromSec = 0.04, MaskSec = 0.2, MaskRampSec = 0.2;
    /// <summary>
    /// テンポをつかめていないときの、キックの弾みの割合（話し声の言葉の出だしもキックとして届くことがあり、低音の端が言葉ごとに光っていた。
    /// テンポをつかめていない曲のキックも、この割合で光る）
    /// </summary>
    public const float UnlockedShare = 0.6f;
    /// <summary>キックの弾みにかける盛り上がりの倍率の下限（静かな部分で暗く、サビで明るく）</summary>
    public const float SectionMin = 0.5f;
    /// <summary>ふだんの強さより上の分を強める割合と、そのときの幅の下限</summary>
    public const float FloorShare = 0.35f, FloorMinRange = 0.15f;
    /// <summary>ふだんの強さが近づく速さ（1 秒あたり）</summary>
    public const double FloorStepPerSec = 0.25;
    /// <summary>キックで光らせる低音の帯の数（31・63・125 Hz）</summary>
    public const int LowBands = 3;
    /// <summary>キックの山を探す、前のコマより前の秒数（キックの検出は少し遅れて届く）</summary>
    public const double KickLookbackSec = 0.25;

    private readonly MixEffect _small = new(zone: true);
    private readonly KickBounce _kick = new();
    private float[] _hPeak = Array.Empty<float>();
    private double[] _tPeak = Array.Empty<double>();
    private float[] _v = Array.Empty<float>();
    private float[] _tmp = Array.Empty<float>();
    /// <summary>LED ごとの、ならした今の強さ</summary>
    private float[] _s = Array.Empty<float>();
    private double[] _sT = Array.Empty<double>();
    /// <summary>LED ごとの、いまの強さを決めた山の保つ秒数と下がる時定数</summary>
    private double[] _sHold = Array.Empty<double>(), _sRel = Array.Empty<double>();
    /// <summary>このコマの帯の強さ・コマの間の山とその時刻</summary>
    private readonly float[] _e = new float[AudioFrame.BandCount], _ep = new float[AudioFrame.BandCount];
    private readonly double[] _epT = new double[AudioFrame.BandCount];
    /// <summary>このコマの間に届いたキックの山の絵（帯ごと）とその時刻（なければ −∞）</summary>
    private readonly float[] _ek = new float[AudioFrame.BandCount];
    private double _kickT = double.NegativeInfinity;
    /// <summary>帯ごとのふだんの強さ</summary>
    private readonly float[] _m = new float[AudioFrame.BandCount];
    /// <summary>曲のビートが続いている度合い（0〜1。テンポをつかんでいて、2 秒以内にキックがあった）</summary>
    private float _beat;
    /// <summary>テンポをつかんでいる度合い（0〜1）</summary>
    private float _lockF;

    public void Reset(EffectContext c)
    {
        int n = c.LedCount;
        if (_hPeak.Length != n)
        {
            _hPeak = new float[n];
            _tPeak = new double[n];
            _v = new float[n];
            _tmp = new float[n];
            _s = new float[n];
            _sT = new double[n];
            _sHold = new double[n];
            _sRel = new double[n];
        }
        Array.Clear(_hPeak);
        Array.Clear(_s);
        Array.Fill(_sT, double.NegativeInfinity);
        Array.Fill(_tPeak, double.NegativeInfinity);
        Array.Clear(_m);
        _beat = 0;
        _lockF = 0;
        Array.Clear(_ek);
        _kickT = double.NegativeInfinity;
        _kick.Reset();
        _small.Reset(c);
    }

    /// <summary>時刻 t に、低音の端で Punch を見せる割合（キックの直後は小さい）</summary>
    private float PunchWeight(double t)
    {
        double age = t - _kick.LastAt(t);
        if (age < MaskFromSec) return PunchShare;
        if (age < MaskSec) return MaskShare;
        if (age < MaskSec + MaskRampSec) return MaskShare + (PunchShare - MaskShare) * (float)((age - MaskSec) / MaskRampSec);
        return PunchShare;
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        var p = c.Peak;
        for (int k = 0; k < AudioFrame.BandCount; k++)
        {
            _e[k] = f.Punch[k];
            _ep[k] = p.Punch[k];
            _epT[k] = c.PunchPeakT[k];
        }

        // 低音の端: キックの弾み（届いたキックの本当の時刻から）・続くベース・遅れて上がる FFT の低音の、いちばん強いもの
        _kick.Take(c, weighBody: true);
        // テンポをつかめていないときは、キックの弾みを UnlockedShare まで弱める
        _lockF = EffectContext.Follow(_lockF, c.HasTempo && c.Locked ? 1f : 0f, c.Dt, MixEffect.LockRiseSec, MixEffect.LockFallSec);
        float sg = c.SectionGain(SectionMin) * (UnlockedShare + (1 - UnlockedShare) * _lockF);
        float bounce = _kick.At(c.T, KickTauSec) * sg;
        var (kv, kt) = _kick.PeakIn(c.TPrev - KickLookbackSec, c.T);
        kv *= sg;
        _beat = EffectContext.Follow(_beat, c.HasTempo && c.Locked && _kick.LastT > double.NegativeInfinity ? 1f : 0f, c.Dt, MixEffect.LockRiseSec, MixEffect.LockFallSec);
        float wNow = PunchWeight(c.T);
        for (int k = 0; k < LowBands; k++)
        {
            // 31・63 Hz: 遅れて上がる FFT の低音を弱め、続くベースを見せる。125 Hz: 帯の強さそのまま
            bool edge = k < 2;
            float bed = edge ? BedShare * f.Band[k] : 0f, now = edge ? Math.Max(bed, wNow * f.Punch[k]) : f.Punch[k];
            // コマの間の山: FFT の山・続くベースの大きいほうを、その本当の時刻とともに使う
            float fp = edge ? PunchWeight(c.PunchPeakT[k]) * p.Punch[k] : p.Punch[k];
            if (fp >= bed) { _ep[k] = fp; _epT[k] = c.PunchPeakT[k]; }
            else { _ep[k] = bed; _epT[k] = c.T; }
            _e[k] = Math.Max(now, edge ? bounce : KickBandShare * bounce);
        }
        // キックの山は、帯の山とは別に、キックの本当の時刻の 1 枚の絵として持つ（帯の山と混ぜると、2 つの帯の山の時刻がずれていて、
        // 1 コマに両方が入る遅いコマほど明るくなった）
        _kickT = kt;
        Array.Clear(_ek);
        _ek[0] = _ek[1] = kv;
        _ek[2] = KickBandShare * kv;

        // ふだんの強さ（音が鳴っている間だけ、少しずつ近づける）より上の分を強める（曲のビートが続いている間だけ。
        // 話し声・雨・続く音では強めない。強めると、話し声の音節や雨のむらで強く点滅した）
        if (f.GateOpen)
        {
            float step = (float)(FloorStepPerSec * c.Dt);
            for (int k = 0; k < AudioFrame.BandCount; k++)
                _m[k] = Math.Max(0f, _m[k] + (_e[k] > _m[k] ? step : -step));
        }
        for (int k = 0; k < AudioFrame.BandCount; k++)
        {
            _e[k] = Expand(k, _e[k]);
            _ep[k] = Expand(k, _ep[k]);
            _ek[k] = Expand(k, _ek[k]);
        }
        if (c.HasSmallSegment) _small.Update(c);
    }

    /// <summary>帯 k の強さ x の、ふだんの強さより上の分を強めた値</summary>
    private float Expand(int k, float x)
    {
        float m = _m[k];
        float r = Math.Clamp((x - m) / Math.Max(1 - m, FloorMinRange), 0f, 1f);
        float share = FloorShare * _beat;
        return (1 - share) * x + share * r;
    }

    /// <summary>位置 x（0〜1）の帯の強さ。band は 10 帯（AudioFrame.BandCount）</summary>
    public static float BandCurve(float[] band, float x)
    {
        float u = Math.Clamp(x, 0f, 1f) * (AudioFrame.BandCount - 1);
        int k = Math.Min((int)u, AudioFrame.BandCount - 2);
        float f = u - k;
        return band[k] + (band[k + 1] - band[k]) * f;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        int a = s.Start, end = s.Start + s.Count;
        double t = c.AgeT;   // 続いている値なので、まだ届いていない先読みの分は減らさない（EffectContext.AgeT）
        for (int j = a; j < end; j++)
        {
            float x = c.X[j];
            float raw = BandCurve(_e, x);
            float pk = Math.Max(raw, BandCurve(_ep, x));
            // ピークの時刻 = この LED が見ている 2 つの帯のうち、強いほうがピークになった時刻
            int k = Math.Min((int)(Math.Clamp(x, 0f, 1f) * (AudioFrame.BandCount - 1)), AudioFrame.BandCount - 2);
            double tPk = _ep[k] >= _ep[k + 1] ? _epT[k] : _epT[k + 1];
            // 保ち方: 低音の端は短く、それより上は長く（間はなめらかにつなぐ）。帯の山は、曲のビートが続いている間だけ短く
            // （話し声の低い声で、低音の端が細かく点滅しない）
            float uK = Math.Clamp((x - LowEdge) / LowBlend, 0f, 1f);
            float uF = 1 - (1 - uK) * _beat;
            // 今の強さ: 上がるときはすぐ。コマの間のピークから保ち、その後ゆっくり下げる
            // （ピークの本当の時刻から計算するので、1 秒間のコマ数に左右されない）。キックの山は、帯の山と時刻の順に入れる
            float pkK = _kickT > double.NegativeInfinity ? BandCurve(_ek, x) : 0f;
            if (pkK > 0 && _kickT < tPk) Lift(j, pkK, _kickT, uK);
            Lift(j, pk, tPk, uF);
            if (pkK > 0 && _kickT >= tPk) Lift(j, pkK, _kickT, uK);
            float now = Math.Max(raw, LevelAt(j, t));
            _v[j] = Math.Max(now, GhostShare * Math.Max(0, HeightAt(j, t)));
        }
        if (s.Count >= BlurMinLeds) Blur(a, s.Count, s.Ring);

        double span = c.Pal.Span;
        for (int j = a; j < end; j++)
            dst[j] = c.PalAt(c.X[j] * span) * c.Ilin(_v[j]);
    }

    /// <summary>
    /// LED j を、時刻 tPk の山 pk まで上げる（今の強さと、ピークのゴースト。どちらも、その時刻の値より高いときだけ）。
    /// u = 保ち方（0 = 低音の端の短い保ち方、1 = ふつうの保ち方）
    /// </summary>
    private void Lift(int j, float pk, double tPk, float u)
    {
        if (pk >= LevelAt(j, tPk))
        {
            _s[j] = pk;
            _sT[j] = tPk;
            _sHold[j] = LowHoldSec + (SmoothHoldSec - LowHoldSec) * u;
            _sRel[j] = LowReleaseSec + (SmoothReleaseSec - LowReleaseSec) * u;
        }
        // ピーク: 保持時間が過ぎたら一定の速さで下げる（時刻で計算するので 1 秒間のコマ数に左右されない）
        if (pk >= HeightAt(j, tPk))
        {
            _hPeak[j] = pk;
            _tPeak[j] = Math.Max(_tPeak[j], tPk);
        }
    }

    /// <summary>LED j のならした強さの、時刻 t の値（その山の保つ秒数は保ち、その後の時定数で下げる）</summary>
    private float LevelAt(int j, double t)
    {
        double age = t - _sT[j] - _sHold[j];
        return age <= 0 ? _s[j] : _s[j] * (float)Math.Exp(-age / _sRel[j]);
    }

    /// <summary>LED j のピークの、時刻 t の高さ（保持時間が過ぎたら 1 秒に 1.5 ずつ下げる）</summary>
    private float HeightAt(int j, double t) => _hPeak[j] - (float)(FallPerSec * Math.Max(0, t - _tPeak[j] - HoldSec));

    /// <summary>[0.25, 0.5, 0.25] で 1 回ぼかす（端は端の値をくり返す。輪なら反対の端とつながる）</summary>
    private void Blur(int start, int count, bool ring)
    {
        Array.Copy(_v, start, _tmp, start, count);
        int last = start + count - 1;
        for (int j = start; j <= last; j++)
        {
            int l = j > start ? j - 1 : ring ? last : start;
            int r = j < last ? j + 1 : ring ? start : last;
            _v[j] = 0.25f * _tmp[l] + 0.5f * _tmp[j] + 0.25f * _tmp[r];
        }
    }
}
