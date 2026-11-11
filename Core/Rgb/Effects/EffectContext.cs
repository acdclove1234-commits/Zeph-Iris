// ==================================================
// EffectContext.cs - 音楽連動の光り方 (エフェクト) の共通部分 (約束ごと IMusicEffect・LED の並び・
//                    1 コマ分の材料・色の切り替えのクロスフェード・乱数)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 音楽連動の光り方 1 つ分の約束ごと。光り方はデバイスごとに 1 つずつ作り、状態（残光・波紋など）を持つ。
/// 毎コマ: Update を 1 回 → ゾーン（LED のまとまり）ごとに Render。
/// 毎コマの処理では new しない（配列は Reset のときに用意しておく）
/// </summary>
public interface IMusicEffect
{
    /// <summary>光り方の名前（MusicStyles の値。表示用）</summary>
    string Name { get; }
    /// <summary>使い始めるとき・LED の並びが変わったとき。配列を用意し、状態を最初に戻す</summary>
    void Reset(EffectContext c);
    /// <summary>1 コマに 1 回。状態を進める（減衰・粒の発生など）</summary>
    void Update(EffectContext c);
    /// <summary>ゾーン s の LED（dst の s.Start から s.Count 個）の色（光の量）を書く</summary>
    void Render(EffectContext c, in LedSegment s, LinColor[] dst);
}

/// <summary>
/// LED のまとまり（ゾーン）1 つ。1〜2 個しかないゾーンが続くときは、まとめて 1 つにしてある（RgbEffects.Segments）
/// </summary>
/// <param name="Start">デバイスの中での最初の LED の番号</param>
/// <param name="Count">LED の数</param>
/// <param name="Mirror">中央から左右対称にする（中央が「始まり」になる）</param>
/// <param name="Reverse">向きを逆にする</param>
/// <param name="Ring">輪になっている（ファンの外周など。最後の LED の隣が最初の LED）</param>
/// <param name="Span">
/// ケースの中の場所を決めたゾーンの、帯の長さ（LED の数として数える。0 なら Count から決める）。
/// 場所を決めたゾーンは、どれもケース全体を 1 本の帯（この長さ）として、その一部を光らせる
/// </param>
public readonly record struct LedSegment(int Start, int Count, bool Mirror, bool Reverse, bool Ring = false, float Span = 0, MusicRole Role = MusicRole.All)
{
    /// <summary>
    /// 「始まりから端まで」の LED の数（左右対称なら半分）。メーターや帯の長さはこの数で数える
    /// </summary>
    public float Ne => Span > 0 ? Span : Math.Max(1f, Mirror ? Count / 2f : Count);

    /// <summary>ケースの中の場所を決めたゾーン（位置はケース全体での位置）</summary>
    public bool Placed => Span > 0;

    /// <summary>LED が少ない（6 個未満）ので、並びを使う光り方の代わりに全体で光る光り方を使う</summary>
    public bool Small => Count < EffectContext.SmallZoneLeds;
}

/// <summary>
/// 1 コマを描くための材料。デバイスごとに 1 つ作り、毎コマ DeviceRenderer が中身を書き換える。
/// 光り方（IMusicEffect）はここから読むだけ（Rng と Color だけは光り方が進めてよい）
/// </summary>
public sealed class EffectContext
{
    /// <summary>この数より LED が少ないゾーンは「小さいゾーン」（並びを使う光り方の代わりに全体で光る）</summary>
    public const int SmallZoneLeds = 6;
    /// <summary>この数以上の LED のゾーンは、左右対称（Mirror）を自動でオンにする</summary>
    public const int AutoMirrorLeds = 30;

    // ---- 時刻 ----
    /// <summary>音を読む時刻（RenderInput.TEval。AudioClock の秒）。光り方の時間はすべてこれで測る</summary>
    public double T;
    /// <summary>前のコマからの秒数（0〜0.25）。減衰は exp(−Dt/τ) で計算し、1 秒間のコマ数に左右されないようにする</summary>
    public float Dt;
    /// <summary>前のコマの T（ビートをまたいだかの判定に使う）</summary>
    public double TPrev;

    // ---- 音 ----
    /// <summary>T の時点の分析結果</summary>
    public AudioFrame Frame = new();
    /// <summary>
    /// 前のコマから今のコマまでの間の最大値（ピーク）。1 秒に 8 回しか描かないデバイスでも、
    /// コマの間の一瞬のキックを取りこぼさないために、残光・ピークの保持にはこちらを使う
    /// </summary>
    public AudioFrame Peak = new();
    /// <summary>Peak.BassE になった時刻（ピークからの経過時間だけ残光を減らすのに使う）</summary>
    public double BassPeakT;
    /// <summary>Peak.LoudVU になった時刻（音量メーターのピークの点を止め始める時刻）</summary>
    public double LoudPeakT;
    /// <summary>Peak.Punch[k] になった時刻（帯ごと）</summary>
    public readonly double[] PunchPeakT = new double[AudioFrame.BandCount];
    /// <summary>
    /// 前のコマから今のコマまでの間の音量（LoudVU）を、時刻の順に並べたもの（2 ミリ秒ごとの決まった時刻。
    /// 分析結果の記録がなければ今のコマの値だけ）。音量メーターのピークの点を、本当の時刻の順に動かすのに使う
    /// </summary>
    public readonly double[] LoudT = new double[MaxWindowSamples];
    public readonly float[] LoudV = new float[MaxWindowSamples];
    public int LoudCount;
    /// <summary>LoudT・LoudV に入れられる数の上限</summary>
    public const int MaxWindowSamples = 160;
    /// <summary>分析結果の記録（なければ null。流れる波が、決まった時刻ごとの音を読むのに使う）</summary>
    public FeatureRing? Features;
    /// <summary>ビートの計算（任意の時刻のビートの光を求めるのに使う）</summary>
    public BeatDriver? Beats;
    public BeatState Beat = BeatState.None;
    /// <summary>前のコマから今のコマまでに起きた出来事（時刻 T は本当に鳴った時刻）</summary>
    public List<AudioEvent> Events = new();

    // ---- 設定 ----
    public ResolvedMusic Music = ResolvedMusic.Default;
    /// <summary>ガンマ補正（RgbConfig.Gamma）</summary>
    public bool Gamma = true;
    /// <summary>いちばん暗いときの明るさ（光の量。OutputStage.Floor）</summary>
    public float F0;
    /// <summary>いまのパレット</summary>
    public ColorPalette Pal = ColorPalette.Get(Palettes.Rainbow, null);
    /// <summary>更新の遅いデバイス用の、ゆっくりした版の光り方を使っている</summary>
    public bool Slow;
    /// <summary>色をクロスフェードせず一度に切り替える（途中の色を送るとちかちかするデバイス用）</summary>
    public bool NoFade;
    /// <summary>流れる速さの倍率（ResolvedMusic.Speed）</summary>
    public float Speed = 1;

    // ---- ビート（BeatDriver が書く） ----
    /// <summary>テンポを使ってよい（テンポの追跡が Auto で、テンポが分かっている）</summary>
    public bool HasTempo;
    /// <summary>テンポをつかんでいる（ビートを予測して光らせる）</summary>
    public bool Locked;
    /// <summary>テンポの確かさ（0〜1。HasTempo でなければ 0）</summary>
    public float Conf;
    /// <summary>拍の中のどこか（0 = 拍の頭）</summary>
    public double Phase;
    /// <summary>何拍目か</summary>
    public long BeatIndex;
    /// <summary>1 拍の長さ（秒）</summary>
    public double Period = 0.5;
    /// <summary>いまの拍が小節の頭（1 拍目）</summary>
    public bool Downbeat;
    /// <summary>このコマの間に、予測したビートの頭をまたいだ（BeatCrossT がその時刻）</summary>
    public bool BeatCrossed;
    public double BeatCrossT;
    /// <summary>このコマで「ビートの合図」（色を進める・波紋を出すきっかけ）があった（TriggerT がその時刻）</summary>
    public bool Trigger;
    public double TriggerT;
    public float TriggerStrength;
    /// <summary>これまでの合図の回数</summary>
    public int TriggerCount;
    /// <summary>ビートの光（0〜1。強さの設定 FlashAmp と、点滅の制限をかけた後）</summary>
    public float Fl;
    /// <summary>
    /// 曲の盛り上がり（0〜1。SectionMeter）。いまの部分の音量（1.5 秒でならす）が、曲の大きいところから何 dB 下か。
    /// 静かな A メロで 0.3〜0.6、サビで 1 くらい。自動の音量調整は数秒で静かな部分も「最大」に合わせるので、その前の値で見る
    /// </summary>
    public float Section = 1;

    /// <summary>盛り上がりに合わせた明るさの倍率（静かな部分で min、盛り上がりで 1）</summary>
    public float SectionGain(float min) => min + (1 - min) * Section;

    /// <summary>
    /// 拍の間の光を控えめにする倍率: テンポをつかんでいるとき、拍の頭の前後 0.12 拍は 1、0.25 拍より離れると 1 − duck × テンポの確かさ。
    /// 低音・キックで光る光り方で、8 分・16 分のベースやキックの光を拍の光より小さくして、拍を目立たせる
    /// （拍の光と拍の間の光がほぼ同じ明るさだと、どこが拍か分からず、せわしなく見える）
    /// </summary>
    public float OffBeatDuck(float duck) => OffBeatDuckAt(T, duck);

    /// <summary>OffBeatDuck を、時刻 t（コマの間のピークの時刻など）の拍の位置で求める</summary>
    public float OffBeatDuckAt(double t, float duck)
    {
        if (!Locked || duck <= 0) return 1;
        double phase = Period > 0 ? Phase + (t - T) / Period : Phase;
        double ph = phase - Math.Floor(phase);
        double d = Math.Min(ph, 1 - ph);
        float s = Smoothstep((float)((d - 0.12) / 0.13));
        return 1 - duck * Conf * s;
    }
    /// <summary>
    /// 前のコマから今のコマまでの間で、ビートの光がいちばん強かった値と、その時刻。
    /// 光はビートの瞬間に最大なので、1 秒に 8 回しか描かなくても、残光はこの値から減らす
    /// </summary>
    public float FlPeak;
    public double FlPeakT;
    /// <summary>いまの色（パレットの位置。ビートで進み、OKLab でクロスフェードする）</summary>
    public readonly ColorFade Color = new();
    /// <summary>全デバイスで共有する「いまの色」（あれば、色の切り替えはこれに合わせる。デバイスごとに色がずれないように）</summary>
    public SharedColor? Shared;

    // ---- LED の並び（DeviceRenderer が LED の数・設定が変わったときに作る） ----
    public LedSegment[] Segments = Array.Empty<LedSegment>();
    public int SegmentCount;
    /// <summary>LED ごとのゾーン内の位置 p（0〜1。端から端。逆向きなら反転済み）</summary>
    public float[] P = Array.Empty<float>();
    /// <summary>LED ごとの「始まり」からの位置 x（0〜1。左右対称なら中央が 0、両端が 1）</summary>
    public float[] X = Array.Empty<float>();
    /// <summary>LED ごとの「始まり」から数えた番号 jx（= x × (Ne − 1)。小数）</summary>
    public float[] Jx = Array.Empty<float>();
    /// <summary>LED の並びを作り直すたびに 1 増える（光り方が配列を作り直すかの判断に使う）</summary>
    public int LayoutVersion;
    /// <summary>LED の総数</summary>
    public int LedCount;
    /// <summary>小さいゾーン（6 個未満）があるか</summary>
    public bool HasSmallSegment;

    /// <summary>デバイスごとの乱数（きらめきの場所選び）</summary>
    public XorShift32 Rng = new(0x9E3779B9);

    /// <summary>
    /// 今のコマの分析結果が「本当はいつの音か」（T 以前）。音の遅れの補正で T がまだ届いていない未来なら、
    /// いちばん新しい分析結果をそのまま使っているので、その時刻を返す。残光・ピークの保持はこの時刻から数える
    /// （そうしないと、1 秒間のコマ数が多いほど、古い値を何度も「今の値」として拾い直して明るくなる）
    /// </summary>
    public double FrameTime => Frame.T > 0 && Frame.T < T ? Frame.T : T;

    /// <summary>時刻 t のビートの光（0〜1。FlashAmp を掛けた後。点滅の制限は、このコマまでに決めた分）</summary>
    public float FlashAt(double t) => Beats is null ? Fl : (float)Math.Clamp(Music.FlashAmp * Beats.FlashAt(this, t), 0, 1);

    /// <summary>音の強さ I（0〜1）→ 明るさ（光の量。いちばん暗いときの明るさ F0 を下限にする）</summary>
    public float Ilin(float i) => OutputStage.Ilin(i, F0, Gamma);

    /// <summary>パレットの位置 x の色（光の量）</summary>
    public LinColor PalAt(double x) => Pal.At(x, Gamma);

    /// <summary>いまの色（ビートで進む色。クロスフェード中なら途中の色）を、パレットの位置を off だけずらして引く</summary>
    public LinColor ColorAt(double off) => Color.Sample(Pal, off, T, Gamma);

    /// <summary>いまの色を OKLab で（白と混ぜるときなど）</summary>
    public Lab ColorLabAt(double off) => Color.SampleLab(Pal, off, T);

    /// <summary>u（0〜1）を、始めと終わりがゆっくりな曲線にする: u²(3 − 2u)</summary>
    public static float Smoothstep(double u)
    {
        float x = (float)Math.Clamp(u, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>時定数 tau 秒の減衰で、dt 秒後に残る割合 exp(−dt/τ)</summary>
    public static float Decay(float dt, double tau) => tau <= 0 ? 0f : (float)Math.Exp(-dt / tau);

    /// <summary>
    /// 上がるとき・下がるときで速さの違う追従（τ は秒。0 ならすぐ追いつく）。
    /// v += (x − v)·(1 − exp(−dt/τ))
    /// </summary>
    public static float Follow(float v, float x, float dt, double tauUp, double tauDown)
    {
        double tau = x > v ? tauUp : tauDown;
        float k = tau <= 0 ? 1f : 1f - (float)Math.Exp(-dt / tau);
        return v + (x - v) * k;
    }
}

/// <summary>
/// いまの色（パレットの位置）と、その切り替えのクロスフェード。
/// 切り替えのたびに「前の色 → 新しい色」を OKLab の直線で、Dur 秒かけて（始めと終わりをゆっくり）つなぐ。
/// 切り替えた時刻はビートが鳴った本当の時刻なので、1 秒間のコマ数が違っても同じように変わる
/// </summary>
/// <summary>
/// 全デバイスで共有する色の位置。どれか 1 台がビートで色を進めたら、ほかのデバイスも同じ色へ切り替える
/// （デバイスごとに数えると、遅いデバイスや後から加わったデバイスだけ色がずれていくため）。
/// LED を光らせるスレッドからだけ使う
/// </summary>
public sealed class SharedColor
{
    /// <summary>同じビートとみなす時間（秒）。デバイスごとの遅れの差でビートの時刻が少しずれても、1 回だけ進める</summary>
    public const double SameStepSec = 0.15;

    /// <summary>パレットの位置</summary>
    public double Target;
    /// <summary>最後に色を進めたビートの時刻</summary>
    public double LastStepT = double.NegativeInfinity;

    /// <summary>時刻 t のビートで色を delta 進める（ほかのデバイスがこのビートですでに進めていれば何もしない）</summary>
    public void Step(double t, double delta)
    {
        if (Math.Abs(t - LastStepT) < SameStepSec) return;
        Target += delta;
        if (Target > 1000) Target -= 1000;
        LastStepT = t;
    }
}

public sealed class ColorFade
{
    // 表示中の色 = lerp( lerp(P(A), P(B), Uab), P(C), smoothstep((t − TSwitch)/Dur) )
    // ふつうは A = B（Uab = 0）。クロスフェードの途中で次の切り替えが来たときだけ、途中の色を A・B・Uab で表す
    private double _a, _b, _c;
    private float _uab;
    private double _tSwitch = double.NegativeInfinity;
    private double _dur;

    /// <summary>いまの（向かっている）色のパレットの位置</summary>
    public double Target => _c;

    /// <summary>
    /// 共有の色に合わせる（違っていれば、そこへ dur 秒かけて切り替える）。t = 切り替えを始めた時刻
    /// </summary>
    public void SyncTo(SharedColor shared, double t, double dur)
    {
        double diff = shared.Target - _c;
        // どちらかが 1000 を超えて巻き戻した直後は、差が ±1000 ずれて見えるので直す
        if (diff > 500) diff -= 1000;
        else if (diff < -500) diff += 1000;
        if (Math.Abs(diff) < 1e-9) return;
        Step(t, diff, dur);
    }

    /// <summary>何回切り替えたか</summary>
    public int Steps { get; private set; }

    /// <summary>最初の色（位置 x）に戻す。クロスフェードはしない</summary>
    public void Reset(double x = 0)
    {
        _a = _b = _c = x;
        _uab = 0;
        _tSwitch = double.NegativeInfinity;
        _dur = 0;
        Steps = 0;
    }

    /// <summary>時刻 t に、パレットの位置を delta だけ進める（dur 秒かけて切り替える）</summary>
    public void Step(double t, double delta, double dur)
    {
        float s = Progress(t);
        if (s >= 1 || _dur <= 0)
        {
            _a = _b = _c;
            _uab = 0;
        }
        else
        {
            // 前の切り替えの途中: 途中の色を「前の色の近いほう → いまの向かう色」の s の位置として覚える
            _a = _uab < 0.5f ? _a : _b;
            _b = _c;
            _uab = s;
        }
        _c += delta;
        if (_c > 1000) { _a -= 1000; _b -= 1000; _c -= 1000; }   // 長時間動かしても小数の精度が落ちないように
        _tSwitch = t;
        _dur = Math.Max(0, dur);
        Steps++;
    }

    private float Progress(double t) => _dur <= 0 ? 1f : EffectContext.Smoothstep((t - _tSwitch) / _dur);

    /// <summary>時刻 t の色（光の量）。パレットの位置を off だけずらして引く</summary>
    public LinColor Sample(ColorPalette pal, double off, double t, bool gamma)
    {
        float s = Progress(t);
        if (s >= 1) return pal.At(_c + off, gamma);   // クロスフェードしていないときは表を引くだけ
        return OkLab.ToLed(Lab.Lerp(From(pal, off), pal.LabAt(_c + off), s), gamma);
    }

    /// <summary>時刻 t の色（OKLab）</summary>
    public Lab SampleLab(ColorPalette pal, double off, double t)
    {
        float s = Progress(t);
        if (s >= 1) return pal.LabAt(_c + off);
        return Lab.Lerp(From(pal, off), pal.LabAt(_c + off), s);
    }

    private Lab From(ColorPalette pal, double off) =>
        _uab <= 0 ? pal.LabAt(_a + off) : Lab.Lerp(pal.LabAt(_a + off), pal.LabAt(_b + off), _uab);
}

/// <summary>
/// 速くて軽い乱数（xorshift32）。デバイスごとに 1 つ持つ。System.Random と違い new しないので毎コマ使ってよい
/// </summary>
public struct XorShift32
{
    private uint _s;

    public XorShift32(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;

    public uint Next()
    {
        uint x = _s;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _s = x;
        return x;
    }

    /// <summary>0 以上 1 未満の小数</summary>
    public float NextFloat() => (Next() >> 8) * (1f / 16777216f);

    /// <summary>0 以上 n 未満の整数</summary>
    public int NextInt(int n) => n <= 1 ? 0 : (int)(Next() % (uint)n);
}
