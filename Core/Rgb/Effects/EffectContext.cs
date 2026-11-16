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
    /// <summary>LoudT と同じ時刻の、低音・中音・高音の強さ（BassE・MidE・TrebleE）。エナジーの帯の長さを、決まった時刻ごとに追いかけるのに使う</summary>
    public readonly float[] BassV = new float[MaxWindowSamples], MidV = new float[MaxWindowSamples], TrebleV = new float[MaxWindowSamples];
    public int LoudCount;
    /// <summary>LoudT・LoudV に入れられる数の上限</summary>
    public const int MaxWindowSamples = 160;
    /// <summary>分析結果の記録（なければ null。流れる波が、決まった時刻ごとの音を読むのに使う）</summary>
    public FeatureRing? Features;
    /// <summary>ビートの計算（任意の時刻のビートの光を求めるのに使う）</summary>
    public BeatDriver? Beats;
    public BeatState Beat = BeatState.None;
    /// <summary>曲の部分ごとに目立つ楽器（楽器の AI。使っていなければ場面なし）。移り変わりは Scene.Progress(T)</summary>
    public MusicScene Scene = MusicScene.None;
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
    /// <summary>
    /// 更新の遅いデバイス（グラボ・メモリなど。1 秒に 4〜10 回しか送れない）。デバイスの光り方を自分で選んでいて Slow でなくても true。
    /// 楽器ごとの役割は、このデバイスでは一瞬の光を見せられないので、曲の部分ごとの目立つ楽器の色だけにする（RoleEffect）
    /// </summary>
    public bool SlowDevice;
    /// <summary>
    /// 明るさを変えないデバイス（OpenRGB を通すグラボ。いつも同じ明るさ）。光り方は、明るさの代わりに色（白へ寄せるなど）で音を見せる
    /// </summary>
    public bool FixedBrightness;
    /// <summary>
    /// アプリが直接書き込むグラボ（PnyEpicXGpu。SlowDevice でもある）。1 秒に 10 回までだが、明るさを変えても一部だけ書き換わって
    /// ちかちかすることがない（メモリなど I2C のデバイスは、明るさをほぼ一定にする）
    /// </summary>
    public bool DirectWrite;
    /// <summary>光り方が「このコマは定期便を待たずに早めに送ってほしい」とき true にする（遅いデバイスの拍のふくらみなど。毎コマ false に戻る）</summary>
    public bool SendNow;
    /// <summary>
    /// 光り方が「このコマの色は、変化が小さくても送ってほしい」とき true にする（グラボの小さな変化は送らないしきい値より小さくても。
    /// 白へ寄せた色から戻すときなど。毎コマ false に戻る）
    /// </summary>
    public bool SendEvenIfSmall;
    /// <summary>色をクロスフェードせず一度に切り替える（途中の色を送るとちかちかするデバイス用）</summary>
    public bool NoFade;
    /// <summary>流れる速さの倍率（ResolvedMusic.Speed）</summary>
    public float Speed = 1;

    // ---- ビート（BeatDriver が書く） ----
    /// <summary>テンポを使ってよい（テンポの追跡が Auto で、テンポが分かっている）</summary>
    public bool HasTempo;
    /// <summary>テンポをつかんでいる（ビートを予測して光らせる）</summary>
    public bool Locked;
    /// <summary>拍の並びが前の曲のもの（曲が変わったと知らされたあと、まだ新しい曲で拍の位置を取り直していない。BeatState.Stale）</summary>
    public bool GridStale;
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

    /// <summary>描いている時刻（RenderInput.T。LED はこの時刻からデバイスの遅れの分だけ後に光る。NaN なら T と同じに扱う）</summary>
    public double Now = double.NaN;

    /// <summary>
    /// 続いている値（帯の強さ・低音・音量とそのピーク）の残光を数える時刻。
    /// T（= 描く時刻 + デバイスの遅れ − 音の遅れの補正）の分析結果がまだ届いていないときは、
    /// いちばん新しい分析結果を使うので、デバイスの遅れの分まで先に減らさない（今の時刻 Now まで。新しい結果より前にはしない）。
    /// 以前は T まで減らしていたので、デバイスの遅れ（8〜60 ミリ秒）の分だけ、届いたばかりの立ち上がりも暗く見えていた
    /// （遅れを補うための先読みが、続いている値では光を弱くするだけになっていた）。
    /// 分析結果が T まで届いている（Bluetooth などで音の遅れの補正が大きい）ときは T のまま
    /// </summary>
    public double AgeT => double.IsNaN(Now) || !(Frame.T > 0 && Frame.T < T) ? T : Math.Max(Frame.T, Math.Min(T, Now));

    /// <summary>時刻 t のビートの光（0〜1。FlashAmp を掛けた後。点滅の制限は、このコマまでに決めた分）</summary>
    public float FlashAt(double t) => Beats is null ? Fl : (float)Math.Clamp(Music.FlashAmp * Beats.FlashAt(this, t), 0, 1);

    /// <summary>音の強さ I（0〜1）→ 明るさ（光の量。いちばん暗いときの明るさ F0 を下限にする）</summary>
    public float Ilin(float i) => OutputStage.Ilin(i, F0, Gamma);

    /// <summary>
    /// この光り方で、楽器の AI の場面の色を使う（DeviceRenderer が光り方ごとに入れる。ScenePalettes.Applies）。
    /// false なら、色は選んだパレットのまま（今までとまったく同じ）
    /// </summary>
    public bool SceneOn;

    /// <summary>
    /// 色を引くパレット（選んだパレット Pal と、場面の色の混ぜ合わせ。場面の移り変わりは Scene.Progress(T)。
    /// どのデバイスも同じ時刻の計算なので、デバイスごとに色がずれない）。
    /// 色を一度に切り替えるデバイス（NoFade）は、移り変わりの半分で一度に切り替える
    /// </summary>
    public PaletteView View => SceneOn ? PaletteView.Of(Pal, Scene, T, NoFade) : new PaletteView(Pal);

    /// <summary>パレットの位置 x の色（光の量）</summary>
    public LinColor PalAt(double x) => View.At(x, Gamma);

    /// <summary>パレットの位置 x の色（OKLab。白と混ぜるときなど）</summary>
    public Lab PalLabAt(double x) => View.LabAt(x);

    /// <summary>いまの色（ビートで進む色。クロスフェード中なら途中の色）を、パレットの位置を off だけずらして引く</summary>
    public LinColor ColorAt(double off) => Color.Sample(View, off, T, Gamma);

    /// <summary>いまの色を OKLab で（白と混ぜるときなど）</summary>
    public Lab ColorLabAt(double off) => Color.SampleLab(View, off, T);

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
    /// <summary>最後に色を進めたビートの時刻（拍の前に切り替え始めたときは、切り替え始めた時刻）</summary>
    public double LastStepT = double.NegativeInfinity;
    /// <summary>拍の前に切り替え始めたときの、切り替え終わる時刻（拍の頭）。ふつうに進めたときは NaN</summary>
    public double FadeEndT = double.NaN;
    /// <summary>
    /// 最後に色を進めたビートの時刻（拍の前に切り替え始めたときも、拍の頭）。同じビートかどうかはこれで比べる
    /// （切り替え始めた時刻で比べると、拍の前から切り替えるデバイスと、拍の頭で一度に切り替えるデバイス・切り替え始めが違うデバイスが
    /// 並んだとき、同じ拍で 2 回進めてしまう）
    /// </summary>
    public double LastBeatT = double.NegativeInfinity;

    /// <summary>同じビートかを拍の頭（LastBeatT）で比べる（調べる用。false で 6.15.8 と同じく切り替え始めた時刻で比べる。アプリの中では変えない）</summary>
    internal static bool KeyOnBeat { get; set; } = true;

    /// <summary>
    /// 時刻 t のビートで色を delta 進める（ほかのデバイスがこのビートですでに進めていれば何もしない）。
    /// endT = 拍の前に切り替え始めるときの、切り替え終わる時刻（拍の頭。ふつうは NaN）。
    /// sameSec = 同じビートとみなす時間（拍で進めるときは半拍。SameStepSec より短くはしない）
    /// </summary>
    public void Step(double t, double delta, double endT = double.NaN, double sameSec = SameStepSec)
    {
        double beatT = double.IsNaN(endT) || !KeyOnBeat ? t : endT;
        if (Math.Abs(beatT - LastBeatT) < Math.Max(sameSec, SameStepSec)) return;
        Target += delta;
        if (Target > 1000) Target -= 1000;
        LastStepT = t;
        LastBeatT = beatT;
        FadeEndT = endT;
    }
}

public sealed class ColorFade
{
    // 表示中の色 = lerp( From, P(C), smoothstep((t − TSwitch)/Dur) )、From = Σ w_k × P(x_k)（OKLab で混ぜる。Σ w_k = 1）
    // ふつうは From = P(前の色) の 1 つだけ。クロスフェードの途中で次の切り替えが来たときは、そのときの色そのもの
    // （From と前の向かう色を、そのときの割合で混ぜたもの）を From にする（途中の色から始めるので、色が飛ばない）
    /// <summary>From に覚える色の数の上限（超えたら、いちばん小さい重みの色を、パレットの位置がいちばん近い色に足す）</summary>
    public const int MaxFrom = 12;
    private readonly double[] _fx = new double[MaxFrom];
    private readonly float[] _fw = new float[MaxFrom];
    private int _fn = 1;
    private double _c;
    private double _tSwitch = double.NegativeInfinity;
    private double _dur;

    public ColorFade() => Reset();

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
        _c = x;
        _fx[0] = x;
        _fw[0] = 1;
        _fn = 1;
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
            _fx[0] = _c;
            _fw[0] = 1;
            _fn = 1;
        }
        else
        {
            // 前の切り替えの途中: いまの色そのもの（From を 1 − s、前の向かう色を s）を、次の From にする
            for (int k = 0; k < _fn; k++) _fw[k] *= 1 - s;
            int same = -1;
            for (int k = 0; k < _fn; k++) if (Math.Abs(_fx[k] - _c) < 1e-9) same = k;
            if (same >= 0) _fw[same] += s;
            else
            {
                if (_fn == MaxFrom) MergeSmallest();
                _fx[_fn] = _c;
                _fw[_fn] = s;
                _fn++;
            }
        }
        _c += delta;
        if (_c > 1000)
        {
            // 長時間動かしても小数の精度が落ちないように
            _c -= 1000;
            for (int k = 0; k < _fn; k++) _fx[k] -= 1000;
        }
        _tSwitch = t;
        _dur = Math.Max(0, dur);
        Steps++;
    }

    private void MergeSmallest()
    {
        int lo = 0;
        for (int k = 1; k < _fn; k++) if (_fw[k] < _fw[lo]) lo = k;
        int hi = lo == 0 ? 1 : 0;
        for (int k = 0; k < _fn; k++)
            if (k != lo && Math.Abs(_fx[k] - _fx[lo]) < Math.Abs(_fx[hi] - _fx[lo])) hi = k;
        _fw[hi] += _fw[lo];
        _fn--;
        _fx[lo] = _fx[_fn];
        _fw[lo] = _fw[_fn];
    }

    private float Progress(double t) => _dur <= 0 ? 1f : EffectContext.Smoothstep((t - _tSwitch) / _dur);

    /// <summary>時刻 t のクロスフェードの進み具合（0〜1。1 = 切り替え終わった）</summary>
    public float ProgressAt(double t) => Progress(t);

    /// <summary>時刻 t の色（光の量）。パレットの位置を off だけずらして引く</summary>
    public LinColor Sample(in PaletteView pal, double off, double t, bool gamma)
    {
        float s = Progress(t);
        if (s >= 1) return pal.At(_c + off, gamma);   // クロスフェードしていないときは表を引くだけ
        return OkLab.ToLed(Lab.Lerp(From(pal, off), pal.LabAt(_c + off), s), gamma);
    }

    /// <summary>時刻 t の色（OKLab）</summary>
    public Lab SampleLab(in PaletteView pal, double off, double t)
    {
        float s = Progress(t);
        if (s >= 1) return pal.LabAt(_c + off);
        return Lab.Lerp(From(pal, off), pal.LabAt(_c + off), s);
    }

    private Lab From(in PaletteView pal, double off)
    {
        if (_fn == 1) return pal.LabAt(_fx[0] + off);
        float l = 0, a = 0, b = 0, w = 0;
        for (int k = 0; k < _fn; k++)
        {
            var p = pal.LabAt(_fx[k] + off);
            float wk = _fw[k];
            l += wk * p.L; a += wk * p.A; b += wk * p.B; w += wk;
        }
        float iw = w > 1e-9f ? 1f / w : 0f;
        return new Lab(l * iw, a * iw, b * iw);
    }
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
