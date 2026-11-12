// ==================================================
// DeviceRenderer.cs - デバイス 1 台分の LED の色を 1 コマずつ作る (光り方の選択・切り替えのクロスフェード・
//                     音が止まったときの光り方・点滅の制限・緊急時の赤点滅・明るさとガンマ補正)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 1 コマ描くのに必要な情報（LED を光らせるスレッドが毎コマ書き換えて使い回す）
/// </summary>
public sealed class RenderInput
{
    public RgbConfig Cfg = new();
    public ResolvedMusic Music = ResolvedMusic.Default;
    /// <summary>描画の時刻（AudioClock の秒）</summary>
    public double T;
    /// <summary>
    /// このデバイスを前に描いてからの秒数。DeviceRenderer は T の差（0〜0.25 秒に収める）を使い、
    /// T が進んでいないときだけこの値を使う（1 秒に 4〜8 回しか描かない GPU でも、減衰が遅くならないように）
    /// </summary>
    public double Dt;
    /// <summary>音の分析結果を読む時刻（= T + デバイスの遅れ − 音の遅れの補正）</summary>
    public double TEval;
    /// <summary>前のコマの TEval（初回・おかしな値なら、DeviceRenderer が今回の範囲を作り直す）</summary>
    public double TPrevEval;
    /// <summary>TEval の時点の分析結果（音がないときは無音）</summary>
    public AudioFrame Frame = new();
    /// <summary>テンポとビートの位置</summary>
    public BeatState Beat = BeatState.None;
    /// <summary>前のコマから今のコマまでに起きた出来事（キックなど。時刻 T は TEval 以前）</summary>
    public readonly List<AudioEvent> Events = new();
    /// <summary>温度モード用の温度（なければ null）</summary>
    public double? Temperature;
    public bool Emergency;
    /// <summary>同期テスト中（クリック音に合わせて白く光らせる）</summary>
    public bool SyncTest;
    /// <summary>色合わせのテスト中（全部のデバイスを白く光らせて、色味を見比べる。色合わせ・明るさは掛ける）</summary>
    public bool WhiteTest;
    /// <summary>
    /// 音楽連動で、ほかのデバイスの色に合わせるときの色（いちばん強い成分が 1）と明るさ（0〜1）。合わせないときは null。
    /// RgbSync が速いデバイスを描いたあとに入れる
    /// </summary>
    public LinColor? FollowColor;
    public float FollowLevel;
    /// <summary>このデバイスの設定</summary>
    public RgbDeviceSettings Device = new();
    /// <summary>
    /// 分析結果の記録（なくてもよい）。あれば、前のコマ（TPrevEval）から今のコマ（TEval）までの間のピークも見るので、
    /// 1 秒に 8 回しか描かないデバイスでも、コマの間の一瞬のキックを取りこぼさない（AudioEngine.Features を入れる）
    /// </summary>
    public FeatureRing? Features;
    /// <summary>
    /// このコマでディザリングしてよいか（描いたコマを毎回送るデバイスだけ true。間引いて送るなら false）。
    /// 遅いデバイスは、これが true でもディザリングしない
    /// </summary>
    public bool AllowDither = true;
    /// <summary>全デバイスで共有する色の位置（null ならデバイスごとに数える）。RgbSync が 1 つ作って全デバイスに渡す</summary>
    public SharedColor? SharedColor;
}

/// <summary>
/// デバイス 1 台分の描画。光り方の状態（波紋・残光・色の切り替えなど）を持つので、デバイスごとに 1 つ作る。
/// LED を光らせるスレッドからだけ使う。1 コマの流れ:
///   1. 緊急時は赤い点滅だけ（いちばん上。明るさ・ガンマ補正もかけない）
///   2. 同期テスト中は、クリック音のたびに白く光らせる（0.08 秒で消える）。それ以外は消灯
///   3. 音楽連動以外のモードは RgbEffects（これまでと同じ見た目）
///   4. 音楽連動: ビートの計算（BeatDriver）→ 光り方（IMusicEffect）→ 音が止まったときの光り方と混ぜる（IdleMixer）
///   5. 明るさを掛け、モード・光り方・パレットを変えた直後は 0.4 秒のクロスフェード（SnapshotFade）
///   6. OutputStage で 0〜255 にする（速いデバイスは暗い色をディザリング）
/// 毎コマの処理では new しない（配列は LED の数・設定が変わったときだけ作り直す）
/// </summary>
public sealed class DeviceRenderer
{
    /// <summary>減衰の計算に使う、前のコマからの秒数の上限（1 秒に 4 回のデバイスでも正しく減衰させる）</summary>
    public const double MaxDtSec = 0.25;
    /// <summary>
    /// コマの間のピークを探す間隔（秒）。分析の 1 コマ（約 10.7 ミリ秒）より十分細かくする
    /// （粗いと、1 秒間のコマ数が少ないほどピークを低く見積もって暗くなる）
    /// </summary>
    public const double PeakStepSec = 0.002;
    /// <summary>コマの間のピークを探す範囲の上限（秒）</summary>
    public const double PeakWindowMaxSec = 0.3;
    /// <summary>同期テストの白い光が消える時定数（秒）</summary>
    public const double SyncFlashTauSec = 0.08;
    /// <summary>温度モードで、温度をならす時定数（秒）。色が段々に跳ばないように</summary>
    public const double TemperatureTauSec = 0.5;
    /// <summary>更新の遅いデバイスの、音楽連動でいちばん暗いときの明るさ（0〜1）。チカチカを抑える</summary>
    public const double SlowMinBrightness = 0.75;
    /// <summary>消灯するとき・点灯に戻すときの時定数（秒）</summary>
    public const double LightsOffSec = 0.25, LightsOnSec = 0.20;
    /// <summary>消灯の明るさの倍率（1 = ふつう、0 = 消灯。まだ決めていなければ −1）</summary>
    private float _offLevel = -1f;
    /// <summary>ちかちかしやすいデバイスで、音が止まったときのムードの色を進める間隔（秒。1 分に 3 回まで書く）</summary>
    public const double FlickerProneIdleStepSec = 20;

    private enum Layer { None, SyncTest, WhiteTest, Mode, Music }

    private readonly EffectContext _ctx = new();
    private readonly BeatDriver _beat = new();
    private readonly IdleMixer _idle = new();
    private readonly SnapshotFade _fade = new();
    private readonly OutputStage _out = new();
    private readonly AttackHold _attack = new();
    private readonly SectionMeter _section = new();
    private readonly IMusicEffect?[] _effects = new IMusicEffect?[EffectCatalog.VariantCount];
    private readonly int[] _effectLayout = new int[EffectCatalog.VariantCount];
    private MoodEffect? _idleMood;
    private int _idleMoodLayout = -1;
    private readonly AudioFrame _scratch = new();
    private readonly AudioFrame _silent = new();
    private readonly List<(int Start, int Length, int Zone)> _segZones = new();
    /// <summary>光り方ごとの、最後に動かしたコマの番号（前のコマで動かしていなければ最初からにする）</summary>
    private readonly long[] _effectFrame = new long[EffectCatalog.VariantCount];
    private long _frame;

    // LED の並び（設定のオブジェクトが変わったときだけ、中身を比べて作り直す）
    private int _layoutN = -1;
    private RgbDeviceSettings? _settingsRef;
    private string _layoutKey = "";
    private string _caseKey = "";
    private double[] _pos = Array.Empty<double>();
    // ゾーンごとの光り方（まとまりごと。null ならデバイスの光り方）と、このコマで使う光り方
    private string?[] _segEffect = Array.Empty<string?>();
    private EffectVariant[] _segVariant = Array.Empty<EffectVariant>();
    private MusicRole[] _segRole = Array.Empty<MusicRole>();
    private bool _hasZoneEffects;
    private int _zoneSig;
    // 色合わせ（LED ごとの赤・緑・青の倍率。デバイスとゾーンの色合わせ・明るさを掛けたもの）
    private LinColor[] _gain = Array.Empty<LinColor>();
    private bool _hasGain;
    private LinColor[] _main = Array.Empty<LinColor>();
    private LinColor[] _idleBuf = Array.Empty<LinColor>();
    private LinColor[] _light = Array.Empty<LinColor>();
    // デバイス・ゾーンごとの光り方（上書きがなければ None）と、その色（null なら全体の色）
    private RgbLightPlan _plan = RgbLightPlan.None;
    private bool _planHasAmbient;
    // ゾーン（ファンごとに分けたものを含む）
    private RgbZoneSplit.Expanded _zx = new(null, new[] { "" }, new[] { "" });
    private Rgb?[] _planColor = Array.Empty<Rgb?>();

    /// <summary>このデバイスの、光り方を上書きした範囲（テスト・表示用）</summary>
    public RgbLightPlan LightPlan => _plan;

    // いま描いているもの（変わったらクロスフェード）
    private Layer _layer = Layer.None;
    private string _mode = "";
    private EffectVariant _variant;
    private EffectVariant? _activeVariant;
    private ColorPalette? _pal;
    private string _idleName = "";
    private bool _hasLast;
    private bool _inEmergency;
    private bool _ditherAllowed;

    // 設定の文字列から作ったものの覚え（同じ文字列なら作り直さない）
    private string? _staticHex;
    private Rgb _staticRgb;
    private string? _palName, _palHex;
    private ColorPalette _palette = ColorPalette.Get(Palettes.Rainbow, null);

    private double _lastT = double.NaN;
    private double _gridCursor = double.NaN;   // コマの間のピーク: ここまでの時刻は見た
    private double _lastClickT = double.NegativeInfinity;
    private double _tempSmooth = double.NaN;
    private double _tempT;
    private float _prevIdleW;
    private double _fullIdleFor;
    private bool _idleFlushed;
    private (long, long) _ownStep;
    private bool _hasOwnStep;

    public DeviceRenderer(RgbDevice device)
    {
        Device = device;
        Array.Fill(_effectLayout, -1);
        Array.Fill(_effectFrame, long.MinValue / 2);
        // きらめきの乱数はデバイスごと（同じデバイスなら毎回同じ並び）
        _ctx.Rng = new XorShift32((uint)(device.Index + 1) * 2654435761u ^ (uint)device.LedCount * 40503u ^ 0x9E3779B9u);
    }

    public RgbDevice Device { get; }

    /// <summary>いま使っている音楽連動の光り方（表示用。更新の遅いデバイスは置き換えた後の名前）</summary>
    public string ActiveEffect { get; private set; } = "";

    /// <summary>いま動かしている光り方（遅いデバイス用の版を含む。音楽連動でなければ null）</summary>
    public EffectVariant? ActiveVariant => _layer == Layer.Music ? _variant : null;

    /// <summary>音が止まったときの光り方の割合（0〜1。表示・テスト用）</summary>
    public float IdleWeight => _idle.Weight;

    /// <summary>
    /// いま、ビートの色替え（全デバイスで共有する色・追いかけの色の段）を描いているか（音楽連動で音が鳴っているとき）。
    /// グラボは小さな色の変化を送らないので、このときだけ、色替えが終わったところで差が小さくても 1 回送る。
    /// ムード・ミックスなど色替えを描かない光り方では、共有の色が変わっても見た目は変わらないので送らない
    /// </summary>
    public bool DrawsColorSteps => _layer == Layer.Music && _idle.Weight < 1 && UsesColorSteps(_variant);

    /// <summary>色替えが終わるまでの秒数（クロスフェードの長さ。一度に切り替えるなら 0）</summary>
    public double ColorStepSettleSec => BeatDriver.CrossfadeSec(_ctx);

    /// <summary>
    /// このコマで、音が止まったときの光り方に切り替わってから 0.5 秒たった、またはビートの色替えを描く光り方で音楽連動に戻った。
    /// グラボは切り替えの最後のわずかな差を送らない（ちかちかしやすいグラボは、一度の切り替えでも差が 40 以下だと送らない）ので、
    /// ここで 1 回送る。ムード・ミックスなど少しずつ変わる光り方へ戻ったときは、ふだんと同じく小さな差は送らない
    /// </summary>
    public bool IdleRampDone { get; private set; }

    /// <summary>このコマで、テンポ追従の帯の色が一度に変わった（共有の色とは別に 2 拍ごと）</summary>
    public bool OwnColorStep { get; private set; }

    /// <summary>ほかのデバイスの色に合わせているときの、光り方の表示名</summary>
    public static string FollowEffectName => L("ほかのデバイスの色");

    /// <summary>音が止まったときの光り方に切り替わってから、最後の色を 1 回送るまでの秒数（すぐ音が戻るときは送らない）</summary>
    public const double IdleFlushAfterSec = 0.5;

    /// <summary>
    /// ビートの色替えを描く光り方か（共有の色を描く: ビートで色替え・エナジー・流れる波・波紋・ストロボ、
    /// 小さなゾーンでパルスを描く音量メーター、2 拍ごとに色の段を進めるテンポ追従）。
    /// パルスは色を時刻でゆっくり変える（ビートで切り替えない）ので入れない
    /// </summary>
    public static bool UsesColorSteps(EffectVariant v) => v is EffectVariant.BeatHue or EffectVariant.BeatHueSlow
        or EffectVariant.PulseLoudSlow
        or EffectVariant.Energy or EffectVariant.Scroll or EffectVariant.Ripple or EffectVariant.Strobe
        or EffectVariant.Meter or EffectVariant.Chase;

    /// <summary>激しい点滅の制限（受け付けた光の数など。表示・テスト用）</summary>
    public FlashLimiter Limiter => _beat.Limiter;

    /// <summary>最後のコマの色（光の量。明るさを掛けた後、0〜255 にする前）。テスト・プレビュー用</summary>
    public ReadOnlySpan<LinColor> LastLight => _light;

    /// <summary>1 コマ分の材料（テスト用）</summary>
    internal EffectContext Context => _ctx;

    /// <summary>k 番目のまとまりで、最後のコマに使った音楽連動の光り方（テスト用）</summary>
    internal EffectVariant SegmentVariant(int k) => _segVariant[k];

    /// <summary>いま動かしている光り方のオブジェクト（テスト用）</summary>
    internal IMusicEffect? CurrentEffect => _activeVariant is EffectVariant v ? _effects[(int)v] : null;

    /// <summary>
    /// このデバイスで実際に使う光り方。ちかちかしやすいデバイス（ASRock の Radeon グラボ）は、
    /// 絶えず色を書き換える「レインボー（流れる）」「呼吸」を、止まった虹・固定色にする
    /// （光る場所ごとに 1 色しか出せないので流れて見えず、書き込むたびに乱れることがあるため）
    /// </summary>
    public string ModeFor(string mode, RgbDeviceSettings? settings = null) =>
        !Device.IsLowWrite || (settings?.AnimateOnGpu == true && !Device.IsFlickerProne) ? mode : mode switch
    {
        RgbModes.Rainbow => RgbModes.RainbowStatic,
        RgbModes.Breathing => RgbModes.Static,
        _ => mode,
    };

    /// <summary>
    /// 動きのある光り方（雷・波など）を描く時刻。グラボなど書き込みを減らすデバイスは、時刻 0 の絵で止める
    /// （レインボー（流れる）を止まった虹にするのと同じ。動かし続けると、書き込みのたびにちかちかするため）。
    /// 「虹・呼吸も動かす」をオンにしたグラボ（ちかちかしやすい機種を除く）は動かす
    /// </summary>
    public double TimeFor(string mode, RgbDeviceSettings? settings, double t) =>
        AmbientEffects.Handles(mode) && Device.IsLowWrite && !(settings?.AnimateOnGpu == true && !Device.IsFlickerProne) ? 0 : t;

    /// <summary>
    /// ちかちかしやすいデバイスで使う音楽連動の光り方（書き込みの回数を減らす）。
    ///   ・全体の設定に任せている（自動）ときは、色が絶えず少しずつ変わるミックス・ムードも「ビートで色替え」にする
    ///   ・デバイスごとに選んだ光り方は、遅いデバイス用の版にする（ムードを選んだときはムードのまま）
    /// </summary>
    public static EffectVariant FlickerProneVariant(EffectVariant v, string? deviceEffect)
    {
        bool auto = string.IsNullOrEmpty(deviceEffect) || deviceEffect == EffectCatalog.Auto;
        return v switch
        {
            EffectVariant.Mood => auto ? EffectVariant.BeatHueSlow : EffectVariant.Mood,
            EffectVariant.Pulse or EffectVariant.PulseSlow => EffectVariant.PulseSlow,
            EffectVariant.PulseLoudSlow or EffectVariant.Meter => EffectVariant.PulseLoudSlow,
            _ => EffectVariant.BeatHueSlow,
        };
    }

    /// <summary>
    /// 書き込みを最小限にする描き方（明るさ一定・色は一度に切り替え）にするか。
    /// ちかちかしやすいデバイスは常に、ほかのグラボはビートで色が切り替わる光り方のときだけ
    /// </summary>
    private bool IsLowWriteFor(EffectVariant variant) => Device.IsFlickerProne
        || (Device.IsGpu && variant is EffectVariant.BeatHue or EffectVariant.BeatHueSlow or EffectVariant.Pulse
            or EffectVariant.PulseSlow or EffectVariant.PulseLoudSlow);

    /// <summary>1 コマ描いて output（長さ = LED の数）に入れる</summary>
    public void Render(RenderInput input, Rgb[] output)
    {
        int n = output.Length;
        IdleRampDone = false;
        OwnColorStep = false;
        if (n == 0) return;
        var cfg = input.Cfg ?? new RgbConfig();
        var settings = input.Device ?? new RgbDeviceSettings();
        EnsureLayout(n, settings, cfg);
        float dt = MeasureDt(input);
        if (!ReferenceEquals(cfg.StaticColor, _staticHex))
        {
            _staticHex = cfg.StaticColor;
            _staticRgb = Rgb.Parse(cfg.StaticColor);
        }

        // 1. 緊急時は赤く点滅（どのモードより優先。これまでと同じ見た目）
        if (input.Emergency && cfg.BlinkOnEmergency)
        {
            var e = RgbEffects.EmergencyColor(input.T);
            Array.Fill(output, e);
            Array.Fill(_light, new LinColor(e.R / 255f, e.G / 255f, e.B / 255f));
            _fade.Cancel();
            _inEmergency = true;
            _hasLast = true;
            return;
        }
        bool afterEmergency = _inEmergency;
        _inEmergency = false;
        _frame++;

        // 2. 何を描くか決め、変わったらクロスフェードを始める
        var music = input.Music ?? ResolvedMusic.Default;
        Layer layer = input.SyncTest ? Layer.SyncTest : input.WhiteTest ? Layer.WhiteTest : cfg.Mode == RgbModes.Music ? Layer.Music : Layer.Mode;
        EffectVariant variant = _variant;
        if (layer == Layer.Music)
        {
            variant = EffectCatalog.Resolve(music.Effect, settings.Effect, Device.IsSlow, music.StrobeAllowed);
            // 光り方の置き換えは、ちかちかしやすいデバイス（ASRock の Radeon）だけ。
            // ほかのグラボは全体と同じ光り方にする（グラボだけ違う動きになって、連動していないように見えないように）
            if (Device.IsFlickerProne) variant = FlickerProneVariant(variant, settings.Effect);
            if (!ReferenceEquals(music.Palette, _palName) || !ReferenceEquals(cfg.StaticColor, _palHex))
            {
                _palName = music.Palette;
                _palHex = cfg.StaticColor;
                _palette = ColorPalette.Get(music.Palette, cfg.StaticColor);
            }
        }
        int zoneSig = layer == Layer.Music ? ResolveZoneVariants(variant, music) : 0;
        bool changed = layer != _layer
            || (layer == Layer.Mode && cfg.Mode != _mode)
            || (layer == Layer.Music && (variant != _variant || zoneSig != _zoneSig || !ReferenceEquals(_palette, _pal) || music.IdleEffect != _idleName));
        // 緊急の赤い点滅が終わったときも、赤からクロスフェードする（音楽連動の状態はそのまま続ける）
        // （ちかちかしやすいデバイスは、途中の色を何度も送ることになるのでクロスフェードしない）
        if ((changed || afterEmergency) && _hasLast && (_layer != Layer.None || afterEmergency) && !Device.IsLowWrite) _fade.Start(_light, input.T);
        if (changed)
        {
            if (layer == Layer.Music && _layer != Layer.Music) _idle.Reset();
            if (layer != Layer.Music) _activeVariant = null;   // 音楽連動に戻ったら光り方を最初から
            _layer = layer;
            _mode = cfg.Mode;
            _variant = variant;
            _zoneSig = zoneSig;
            _pal = _palette;
            _idleName = music.IdleEffect;
        }

        // 3. 光の量（明るさを掛ける前）を _main に描く
        switch (layer)
        {
            case Layer.SyncTest:
                RenderSyncTest(input);
                ActiveEffect = "";
                break;
            case Layer.WhiteTest:
                Array.Fill(_main, LinColor.White);
                ActiveEffect = "";
                break;
            case Layer.Music when input.FollowColor is LinColor follow:
                // ほかのデバイスの色に合わせる（明るさは、書き込みを減らすグラボは一定、ほかは遅いデバイスの下限まで）
                float level = Device.IsLowWrite ? 1f : (float)Math.Max(SlowMinBrightness, Math.Clamp(input.FollowLevel, 0f, 1f));
                Array.Fill(_main, follow * level);
                ActiveEffect = FollowEffectName;
                break;
            case Layer.Music:
                RenderMusic(input, cfg, music, variant, dt);
                ActiveEffect = EffectCatalog.NameOf(variant);
                break;
            default:
                RgbEffects.FillLinear(ModeFor(cfg.Mode, settings), cfg, _staticRgb, TimeFor(cfg.Mode, settings, input.T), SmoothTemperature(cfg, input), _pos, _main);
                ActiveEffect = "";
                break;
        }
        // デバイス・ゾーンごとの光り方（グラボだけ赤、水冷のヘッドだけ白、など）: その LED だけ描き直す
        if (layer is Layer.Mode or Layer.Music && _plan.Segments.Count > 0)
        {
            ApplyLightPlan(input, cfg, settings);
            if (_plan.CoversAll) ActiveEffect = "";
        }

        // 4. 明るさ・クロスフェード → 0〜255
        float bri = (float)(Math.Clamp(cfg.Brightness, 0, 100) / 100);
        // 消灯: 制御は続けたまま、なめらかに黒へ（同期テスト・白で見比べるときは消さない）
        bool off = cfg.LightsOff && layer is not (Layer.SyncTest or Layer.WhiteTest);
        if (_offLevel < 0) _offLevel = off ? 0f : 1f;
        else _offLevel = EffectContext.Follow(_offLevel, off ? 0f : 1f, (float)Math.Clamp(dt, 0, 0.25), LightsOnSec, LightsOffSec);
        if (_offLevel < 0.01f && off) _offLevel = 0f;
        bri *= _offLevel;
        if (_hasGain)
        {
            // 色合わせ: LED ごとに赤・緑・青の倍率を掛ける（光の量に掛けるので、白の色味をそのまま合わせられる）
            for (int i = 0; i < n; i++)
            {
                var m = _main[i];
                var g = _gain[i];
                _light[i] = new LinColor(m.R * bri * g.R, m.G * bri * g.G, m.B * bri * g.B);
            }
        }
        else
        {
            for (int i = 0; i < n; i++) _light[i] = _main[i] * bri;
        }
        _fade.Apply(_light, input.T);
        // 音楽連動: 明るくなった直後の小さな下がりを止める（低音の値の上下で、光の立ち上がりが段つきになってちらつく）。
        // ゾーン全体が一緒に明るくなる光り方だけ（波紋・テンポ追従など光が動く光り方では、通り過ぎた光が残って尾を引くため）。
        // 書き込みを減らすデバイス（グラボ）は、光り方の側で立ち上がりを遅らせているので使わない
        if (layer == Layer.Music && !Device.IsLowWrite && AttackHold.Applies(variant)) _attack.Apply(_light, n, dt);
        else _attack.Reset();
        // ディザリングは、1 秒に何十回も送る速いデバイスで、動きのある光り方だけ（止まった色は毎回同じ値を送る）
        // 送る速さが下がって間引くようになったら、持ち越していた端数を捨てる（戻ったときに古い端数を使わない）
        bool allow = !Device.IsSlow && input.AllowDither;
        if (!allow && _ditherAllowed) _out.Reset();
        _ditherAllowed = allow;
        // 虹・呼吸などでは使わない（OpenRGB が間引いて書くと端数の持ち越しがずれて、暗い色がちらつくことがあるため）。
        // 雷・波・オーロラなど暗いところがゆっくり変わる光り方は、前に送った値を保つ書き方にする（下の WriteHold）。
        // 書き込みを減らすデバイス（グラボ）は止まった絵なので、ふつうに四捨五入する
        bool ambient = layer == Layer.Mode && !Device.IsLowWrite && (AmbientEffects.Handles(cfg.Mode) || _planHasAmbient);
        bool dither = allow && (layer is Layer.Music or Layer.SyncTest || _fade.Active);
        _out.DitherFloorCode = 0f;
        // 雷・波など: ディザリングの代わりに、前に送った値を保つ（暗い色が 2・3・2・3 と行き来しない）
        if (ambient && !dither) _out.WriteHold(_light, output, 1f);
        else _out.Write(_light, output, 1f, dither);
        _hasLast = true;
    }

    /// <summary>前のコマからの秒数。描画の時刻 T の差（なければ input.Dt）を 0〜0.25 秒に収める</summary>
    private float MeasureDt(RenderInput input)
    {
        double dt = !double.IsNaN(_lastT) && input.T > _lastT ? input.T - _lastT : input.Dt;
        _lastT = input.T;
        if (!(dt > 0)) return 0;
        return (float)Math.Min(dt, MaxDtSec);
    }

    /// <summary>
    /// LED の数・設定が変わったときだけ、LED の並び（ゾーンごとの左右対称・向き）・ゾーンごとの光り方・色合わせを作り直す。
    /// 設定のオブジェクトが同じなら何もしない。変わっても、並びの中身が同じなら並びは作り直さない
    /// （明るさなどほかの設定を変えただけで、光り方が最初からにならないように）
    /// </summary>
    private void EnsureLayout(int n, RgbDeviceSettings settings, RgbConfig cfg)
    {
        // ケースの配置（オン・オフと流れる向き）は全体の設定なので、デバイスの設定とは別に見る
        string caseKey = cfg.CaseLayout ? CaseDirections.Normalize(cfg.CaseDirection) : "";
        if (n == _layoutN && ReferenceEquals(settings, _settingsRef) && caseKey == _caseKey) return;
        _settingsRef = settings;
        _caseKey = caseKey;
        // ファンごとに分けたゾーン（RgbZoneSettings.Fans）は、分けた部分をそれぞれ 1 つのゾーンとして扱う
        _zx = RgbZoneSplit.Expand(Device.Zones, Device.ZoneName, settings);
        RgbEffects.SegmentZones(n, _zx.Zones, _segZones);
        var zoneSet = new RgbZoneSettings?[_segZones.Count];
        var key = new System.Text.StringBuilder();
        key.Append(n).Append('|').Append(settings.Mirror?.ToString() ?? "-").Append('|').Append(settings.Reverse).Append('|').Append(caseKey);
        _hasZoneEffects = false;
        if (_segEffect.Length != zoneSet.Length)
        {
            _segEffect = new string?[zoneSet.Length];
            _segVariant = new EffectVariant[zoneSet.Length];
            _segRole = new MusicRole[zoneSet.Length];
        }
        for (int k = 0; k < zoneSet.Length; k++)
        {
            int zone = _segZones[k].Zone;
            // ゾーンの情報がないデバイス（全体で 1 つのまとまり）は、最初のゾーンの名前で設定を探す（ケースの中の場所など）
            var z = zone >= 0 ? _zx.Settings(settings, zone)
                : _segZones.Count == 1 ? _zx.Settings(settings, 0) : null;
            zoneSet[k] = z;
            key.Append('|').Append(z?.Mirror?.ToString() ?? "-").Append(',').Append(z?.Reverse?.ToString() ?? "-").Append(',').Append(z?.Place ?? "");
            // 反応する音（光り方「パーツごとの役割」用。設定がなければパーツの種類・ケースの中の場所から決める）
            _segRole[k] = MusicSounds.Resolve(z?.Sound, Device, _zx.Name(Math.Max(0, zone)), z?.Place);
            key.Append(',').Append((int)_segRole[k]);
            string? effect = z is null || z.Effect == RgbZoneSettings.SameAsDevice ? null : z.Effect;
            _segEffect[k] = effect;
            if (effect is not null) _hasZoneEffects = true;
        }
        BuildGains(n, settings);
        BuildLightPlan(n, settings);
        string layoutKey = key.ToString();
        if (n == _layoutN && layoutKey == _layoutKey) return;
        _layoutKey = layoutKey;

        var c = _ctx;
        if (n != _layoutN)
        {
            _main = new LinColor[n];
            _idleBuf = new LinColor[n];
            _light = new LinColor[n];
            c.P = new float[n];
            c.X = new float[n];
            c.Jx = new float[n];
            _out.Reset();
            _fade.Cancel();
            _hasLast = false;
        }
        _pos = RgbEffects.Positions(n, _zx.Zones);
        c.Segments = new LedSegment[_segZones.Count];
        c.SegmentCount = _segZones.Count;
        c.HasSmallSegment = false;
        for (int k = 0; k < _segZones.Count; k++)
        {
            var (start, count, _) = _segZones[k];
            // ケースの中の場所を決めたゾーン: 位置はケース全体での、光が流れる向きの位置（0〜1）。
            // どのデバイスも同じ長さの帯の一部として光るので、波・虹・メーターがデバイスをまたいでつながる
            if (caseKey.Length > 0 && CaseLayout.Find(zoneSet[k]?.Place) is { } place)
            {
                var along = CaseLayout.Positions(place, count, caseKey);
                var placed = new LedSegment(start, count, Mirror: false, Reverse: false, Ring: false, Span: CaseLayout.Span, Role: _segRole[k]);
                c.Segments[k] = placed;
                if (placed.Small) c.HasSmallSegment = true;
                for (int j = 0; j < count; j++)
                {
                    float u = along[j];
                    c.P[start + j] = u;
                    c.X[start + j] = u;
                    c.Jx[start + j] = u * (CaseLayout.Span - 1);
                    _pos[start + j] = u;
                }
                continue;
            }
            // 左右対称・向き: ゾーンの設定があればそれ、なければデバイスの設定。
            // 左右対称の設定がどちらにもなければ、LED が 30 個以上のゾーンだけ自動でオン（長いテープは中央から）
            bool? mirror = zoneSet[k]?.Mirror ?? settings.Mirror;
            bool reverse = zoneSet[k]?.Reverse ?? settings.Reverse;
            var s = new LedSegment(start, count, mirror ?? count >= EffectContext.AutoMirrorLeds, reverse, Role: _segRole[k]);
            c.Segments[k] = s;
            if (s.Small) c.HasSmallSegment = true;
            float ne = s.Ne;
            for (int j = 0; j < count; j++)
            {
                // ゾーンの中の位置 p（端から端で 0〜1）、x（始まりからの位置。左右対称なら中央が 0）
                float p = count <= 1 ? 0f : j / (float)(count - 1);
                if (reverse) p = 1 - p;
                float x = s.Mirror ? Math.Abs(2 * p - 1) : p;
                c.P[start + j] = p;
                c.X[start + j] = x;
                c.Jx[start + j] = x * Math.Max(0f, ne - 1);
            }
        }
        c.LedCount = n;
        c.LayoutVersion++;
        _layoutN = n;
    }

    /// <summary>LED ごとの色合わせの倍率を作る（デバイスの色合わせ × ゾーンの色合わせ。どれも 100% なら掛けない）</summary>
    private void BuildGains(int n, RgbDeviceSettings settings)
    {
        if (_gain.Length != n) _gain = new LinColor[n];
        var ledZone = RgbEffects.LedZones(n, _zx.Zones);
        float level = settings.Level / 100f;
        float dr = settings.GainR / 100f * level, dg = settings.GainG / 100f * level, db = settings.GainB / 100f * level;
        var zones = new Dictionary<int, LinColor>();
        _hasGain = false;
        for (int i = 0; i < n; i++)
        {
            int zone = ledZone[i];
            if (!zones.TryGetValue(zone, out var g))
            {
                var z = zone >= 0 ? _zx.Settings(settings, zone) : null;
                float zl = z is null ? 1f : z.Level / 100f;
                g = z is null ? new LinColor(dr, dg, db) : new LinColor(dr * z.GainR / 100f * zl, dg * z.GainG / 100f * zl, db * z.GainB / 100f * zl);
                zones[zone] = g;
            }
            _gain[i] = g;
            if (g.R != 1f || g.G != 1f || g.B != 1f) _hasGain = true;
        }
    }

    /// <summary>
    /// まとまりごとの音楽連動の光り方を決める（ゾーンで光り方を選んでいなければ、デバイスの光り方）。
    /// 選び方が変わったか比べるための数を返す
    /// </summary>
    private int ResolveZoneVariants(EffectVariant deviceVariant, ResolvedMusic music)
    {
        int sig = 17;
        for (int k = 0; k < _segVariant.Length; k++)
        {
            var v = deviceVariant;
            if (_hasZoneEffects && _segEffect[k] is string effect)
            {
                v = EffectCatalog.Resolve(music.Effect, effect, Device.IsSlow, music.StrobeAllowed);
                if (Device.IsFlickerProne) v = FlickerProneVariant(v, effect);
            }
            _segVariant[k] = v;
            sig = unchecked(sig * 31 + (int)v + 1);
        }
        return sig;
    }

    /// <summary>音楽連動の 1 コマ（音が止まったときの光り方との混ぜ合わせまで）を _main に描く</summary>
    private void RenderMusic(RenderInput input, RgbConfig cfg, ResolvedMusic music, EffectVariant variant, float dt)
    {
        var c = _ctx;
        c.Music = music;
        c.Gamma = cfg.Gamma;
        // 更新の遅いデバイス（GPU など）は 1 秒に数回しか色が変わらないので、明るさの上下が大きいとチカチカして見える。
        // いちばん暗いときの明るさを 75% 以上にして、明るさはほぼ一定・色の切り替えでビートを見せる
        // グラボ（書き込みに時間がかかる・ちかちかしやすい）は明るさを変えず、ビートで色だけを切り替える（書き込みを最小にする）
        // グラボは、どの光り方でも明るさを一定にする（グラボは 1 回の書き込みで LED を 1 個ずつ書き換えるので、
        // 明るさの細かい変化を送り続けると、ちかちかして見える）。色はほかのデバイスと同じように変わる。
        // さらにビートで色が切り替わる光り方では、色を一度に切り替える（途中の色を書いて、色替えが遅れないように）
        bool lowWrite = IsLowWriteFor(variant);
        double floor = Device.IsLowWrite ? 1.0 : Device.IsSlow ? Math.Max(music.MinBrightness, SlowMinBrightness) : music.MinBrightness;
        c.F0 = OutputStage.Floor(floor, cfg.Gamma);
        c.Pal = _palette;
        c.Slow = EffectCatalog.IsSlowVariant(variant);
        c.NoFade = lowWrite;
        c.Speed = (float)music.Speed;
        c.Frame = input.Frame ?? _silent;
        c.Beat = input.Beat ?? BeatState.None;
        c.Events = input.Events;
        c.Features = input.Features;
        c.Beats = _beat;
        c.Shared = input.SharedColor;

        // 今回見る時間の範囲 (t0, t1]。前のコマの時刻がおかしければ（初回・止まっていた・音の遅れの補正を戻した）作り直す
        double t1 = input.TEval;
        double t0 = input.TPrevEval;
        if (!(t0 <= t1)) t0 = t1;                     // 時刻が戻った: 今回は何もまたがない
        else if (t1 - t0 > 0.5) t0 = t1 - dt;         // 初回・長く止まっていた
        c.TPrev = t0;
        c.T = t1;
        c.Dt = dt;
        SampleWindow(input.Features, t0, t1);
        c.Section = _section.Update(c.Frame, dt);

        _beat.Update(c, t0, t1);
        BeatDriver.SyncSharedColor(c);

        var fx = Use(variant);
        _activeVariant = variant;
        var chaseFx = fx as ChaseEffect;
        if (_hasZoneEffects)
        {
            // ゾーンごとに選んだ光り方も、1 コマに 1 回だけ進める
            for (int k = 0; k < _segVariant.Length; k++)
            {
                if (_segVariant[k] == variant) continue;
                var zfx = Use(_segVariant[k]);
                chaseFx ??= zfx as ChaseEffect;
            }
        }
        if (chaseFx is { } chase)
        {
            var k = chase.ColorStep;
            OwnColorStep = _hasOwnStep && k != _ownStep;
            _ownStep = k;
            _hasOwnStep = true;
        }
        else
        {
            _hasOwnStep = false;
        }

        _idle.Update(c.Frame, music.IdleDelaySec, dt);
        float w = _idle.Weight;
        // 止まっているときの光り方が全部になって 0.5 秒（1 回だけ）。音楽連動に戻ったとき（ビートの色替えを描く光り方だけ。
        // 止まっている間に進んだ色替えは、差が小さいと届いていないことがある）
        _fullIdleFor = w >= 1 ? _fullIdleFor + dt : 0;
        bool settled = _fullIdleFor >= IdleFlushAfterSec && !_idleFlushed;
        if (settled) _idleFlushed = true;
        if (w < 1) _idleFlushed = false;
        IdleRampDone = settled || (w <= 0 && _prevIdleW > 0 && UsesColorSteps(variant));
        _prevIdleW = w;
        if (w < 1) RenderMusicSegments(variant, _main);
        if (w > 0)
        {
            RenderIdle(input, cfg, music);
            // ちかちかしやすいデバイスは、途中の色を何度も書かないよう、半分を過ぎたところで一度に切り替える
            float s = Device.IsFlickerProne ? (w >= 0.5f ? 1f : 0f) : EffectContext.Smoothstep(w);
            if (s >= 1) _idleBuf.AsSpan().CopyTo(_main);
            else if (s > 0) for (int i = 0; i < _main.Length; i++) _main[i] = LinColor.Lerp(_main[i], _idleBuf[i], s);
        }
    }

    private void RenderSegments(IMusicEffect fx, LinColor[] dst)
    {
        var c = _ctx;
        for (int k = 0; k < c.SegmentCount; k++) fx.Render(c, c.Segments[k], dst);
    }

    /// <summary>
    /// 光り方のオブジェクトを取り出して、このコマの分だけ進める（1 コマに 1 回。初めてなら作る）。
    /// 前のコマで使っていなかった（切り替えた・音楽連動に戻った）・LED の並びが変わったときは最初に戻す
    /// </summary>
    private IMusicEffect Use(EffectVariant v)
    {
        int i = (int)v;
        var fx = _effects[i] ??= EffectCatalog.Create(v);
        if (_effectFrame[i] == _frame) return fx;
        if (_effectFrame[i] != _frame - 1 || _effectLayout[i] != _ctx.LayoutVersion)
        {
            fx.Reset(_ctx);
            _effectLayout[i] = _ctx.LayoutVersion;
        }
        _effectFrame[i] = _frame;
        bool slow = _ctx.Slow;
        _ctx.Slow = EffectCatalog.IsSlowVariant(v);
        fx.Update(_ctx);
        _ctx.Slow = slow;
        return fx;
    }

    /// <summary>まとまりごとに、そのまとまりの光り方で描く</summary>
    private void RenderMusicSegments(EffectVariant deviceVariant, LinColor[] dst)
    {
        var c = _ctx;
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var v = _hasZoneEffects && k < _segVariant.Length ? _segVariant[k] : deviceVariant;
            var fx = _effects[(int)v]!;
            if (v == deviceVariant)
            {
                fx.Render(c, c.Segments[k], dst);
                continue;
            }
            bool slow = c.Slow;
            c.Slow = EffectCatalog.IsSlowVariant(v);
            fx.Render(c, c.Segments[k], dst);
            c.Slow = slow;
        }
    }

    /// <summary>
    /// 音が止まったときの光り方（ムード・呼吸・固定色・消灯）を _idleBuf に描く。
    /// グラボは、光り方のモードと同じ決まり（ModeFor）で呼吸を固定色にする（「虹・呼吸も動かす」がオンなら呼吸のまま）。
    /// ちかちかしやすいデバイスのムードは、色を 20 秒ごとに一度に進める（少しずつ書き換えない）
    /// </summary>
    private void RenderIdle(RenderInput input, RgbConfig cfg, ResolvedMusic music)
    {
        switch (music.IdleEffect)
        {
            case IdleEffects.Breathing:
                RgbEffects.FillLinear(ModeFor(RgbModes.Breathing, input.Device), cfg, _staticRgb, input.T, null, _pos, _idleBuf);
                break;
            case IdleEffects.Static:
                RgbEffects.FillLinear(RgbModes.Static, cfg, _staticRgb, input.T, null, _pos, _idleBuf);
                break;
            case IdleEffects.Off:
                Array.Clear(_idleBuf);
                break;
            default:
                _idleMood ??= new MoodEffect(idle: true) { IdleStepSec = Device.IsFlickerProne ? FlickerProneIdleStepSec : 0 };
                if (_idleMoodLayout != _ctx.LayoutVersion)
                {
                    _idleMood.Reset(_ctx);
                    _idleMoodLayout = _ctx.LayoutVersion;
                }
                _idleMood.Update(_ctx);
                RenderSegments(_idleMood, _idleBuf);
                break;
        }
    }

    /// <summary>
    /// 前のコマから今のコマまでの間の分析結果のピークを _ctx.Peak に入れる（今のコマの値も含む）。
    /// 見る時刻は「2 ミリ秒の倍数」の決まった時刻（コマの時刻からではない）。しかも、分析結果がもう届いている時刻
    /// （LatestT 以前）だけを見て、まだ届いていない時刻は次のコマに回す。こうすると、どの時刻にどの値を読むかが
    /// 1 秒間のコマ数で変わらない（届いたばかりの値を何度も読み直すと、コマ数が多いほどピークを高く見積もる）。
    /// 記録がなければ、今のコマの値をそのまま使う
    /// </summary>
    private void SampleWindow(FeatureRing? ring, double t0, double t1)
    {
        var c = _ctx;
        var pk = c.Peak;
        pk.CopyFrom(c.Frame);
        double tNow = c.FrameTime;
        c.BassPeakT = c.LoudPeakT = tNow;
        Array.Fill(c.PunchPeakT, tNow);
        c.LoudCount = 0;
        if (ring is null)
        {
            _gridCursor = double.NaN;
            AddLoud(tNow, c.Frame.LoudVU);
            return;
        }
        double end = Math.Min(t1, ring.LatestT);
        // どこから見るか: 前に見た刻みの続き（初回・時刻が戻った・長く止まっていたときは、今回の範囲の始まりから）
        double from = Math.Max(t0, t1 - PeakWindowMaxSec);
        if (!double.IsNaN(_gridCursor) && _gridCursor <= t1 && _gridCursor >= t1 - PeakWindowMaxSec) from = _gridCursor;
        long k0 = (long)Math.Floor(from / PeakStepSec) + 1;
        long k1 = (long)Math.Floor(end / PeakStepSec);
        for (long k = k0; k <= k1; k++)
        {
            double t = k * PeakStepSec;
            if (!ring.Sample(t, _scratch)) break;
            _gridCursor = t;
            var s = _scratch;
            double ts = t;
            if (s.BassE > pk.BassE) { pk.BassE = s.BassE; c.BassPeakT = ts; }
            pk.SubE = Math.Max(pk.SubE, s.SubE);
            pk.MidE = Math.Max(pk.MidE, s.MidE);
            pk.TrebleE = Math.Max(pk.TrebleE, s.TrebleE);
            pk.Sub = Math.Max(pk.Sub, s.Sub);
            pk.Bass = Math.Max(pk.Bass, s.Bass);
            pk.Mid = Math.Max(pk.Mid, s.Mid);
            pk.Treble = Math.Max(pk.Treble, s.Treble);
            pk.KickEnv = Math.Max(pk.KickEnv, s.KickEnv);
            if (s.LoudVU > pk.LoudVU) { pk.LoudVU = s.LoudVU; c.LoudPeakT = ts; }
            AddLoud(ts, s.LoudVU);
            for (int b = 0; b < AudioFrame.BandCount; b++)
            {
                pk.Band[b] = Math.Max(pk.Band[b], s.Band[b]);
                if (s.Punch[b] > pk.Punch[b]) { pk.Punch[b] = s.Punch[b]; c.PunchPeakT[b] = ts; }
            }
        }
        // 何も見なかったときも、ここより前は見ない（次のコマはここから）
        if (double.IsNaN(_gridCursor) || _gridCursor < from) _gridCursor = from;
    }

    /// <summary>音量の並び（LoudT・LoudV）に 1 つ足す（いっぱいなら足さない）</summary>
    private void AddLoud(double t, float v)
    {
        var c = _ctx;
        if (c.LoudCount >= EffectContext.MaxWindowSamples) return;
        c.LoudT[c.LoudCount] = t;
        c.LoudV[c.LoudCount] = v;
        c.LoudCount++;
    }

    /// <summary>同期テスト: クリック音（Click）の本当の時刻から、白い光を 0.08 秒で消していく。それ以外は消灯</summary>
    private void RenderSyncTest(RenderInput input)
    {
        var ev = input.Events;
        for (int i = 0; i < ev.Count; i++)
            if (ev[i].Kind == AudioEventKind.Click) _lastClickT = Math.Max(_lastClickT, ev[i].T);
        double age = input.TEval - _lastClickT;
        float k = age >= 0 ? (float)Math.Exp(-age / SyncFlashTauSec) : 0f;
        Array.Fill(_main, LinColor.White * k);
    }

    /// <summary>デバイス・ゾーンごとの光り方の範囲と色を、設定から作り直す（設定が変わったときだけ）</summary>
    private void BuildLightPlan(int n, RgbDeviceSettings settings)
    {
        _plan = RgbLightPlan.Build(settings, n, _zx.Zones, k => _zx.Settings(settings, k));
        _planHasAmbient = false;
        foreach (var seg in _plan.Segments) if (AmbientEffects.Handles(seg.Mode)) _planHasAmbient = true;
        if (_planColor.Length != _plan.Segments.Count) _planColor = new Rgb?[_plan.Segments.Count];
        for (int k = 0; k < _plan.Segments.Count; k++)
            _planColor[k] = _plan.Segments[k].Color.Length > 0 ? Rgb.Parse(_plan.Segments[k].Color) : null;
    }

    /// <summary>
    /// 光り方を上書きした LED を描き直す（全体の光り方の上に描く）。グラボなど書き込みを減らすデバイスは、
    /// 全体の光り方と同じく、流れる虹・呼吸を止まった虹・固定色にする
    /// </summary>
    private void ApplyLightPlan(RenderInput input, RgbConfig cfg, RgbDeviceSettings settings)
    {
        var segs = _plan.Segments;
        int n = _main.Length;
        for (int k = 0; k < segs.Count; k++)
        {
            var s = segs[k];
            int start = Math.Min(s.Start, n), len = Math.Min(s.Length, n - start);
            if (len <= 0) continue;
            var dst = _main.AsSpan(start, len);
            if (s.Mode == RgbLightModes.Off)
            {
                dst.Clear();
                continue;
            }
            // 温度は、全体が温度モードならならした値、そうでなければ段にした値（RgbTemperatureFilter）をそのまま使う
            double? temp = s.Mode == RgbModes.Temperature ? (cfg.Mode == RgbModes.Temperature ? _tempSmooth : input.Temperature) : null;
            if (temp is double tv && double.IsNaN(tv)) temp = input.Temperature;
            RgbEffects.FillLinear(ModeFor(s.Mode, settings), cfg, _planColor[k] ?? _staticRgb, TimeFor(s.Mode, settings, input.T), temp, _pos.AsSpan(start, len), dst);
        }
    }

    /// <summary>温度モードの温度を 1.5 秒でならす（温度の値が 1 秒ごとに跳んでも、色はなめらかに変わる）</summary>
    private double? SmoothTemperature(RgbConfig cfg, RenderInput input)
    {
        if (cfg.Mode != RgbModes.Temperature || input.Temperature is not double temp)
        {
            _tempSmooth = double.NaN;
            return input.Temperature;
        }
        double gap = input.T - _tempT;
        _tempT = input.T;
        // 温度は RgbSync（RgbTemperatureFilter）で段にしてある。グラボは段ごとに 1 回で切り替える（途中の色を書かない）。
        // ほかのデバイスは 0.5 秒でなめらかにつなぐ
        if (Device.IsLowWrite || double.IsNaN(_tempSmooth) || !(gap >= 0) || gap > 5) return _tempSmooth = temp;
        _tempSmooth += (temp - _tempSmooth) * (1 - Math.Exp(-gap / TemperatureTauSec));
        return _tempSmooth;
    }
}
