// ==================================================
// MusicScene.cs - 曲の部分ごとに目立つ楽器（場面）: 楽器の AI（CED-Tiny）の 527 種類のらしさを楽器のまとまりにし、
//                 ならして・迷わないように（ヒステリシス・続いた時間・最低の長さ）場面を決める
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>曲の部分ごとに目立つ楽器のまとまり（場面）。後ろに足すだけ（数字を変えない）</summary>
public enum SceneGroup : byte { None, Vocal, Brass, Guitar, Keys, Synth, Strings, Speech }

/// <summary>
/// いまの場面（変更しない値。場面が変わったときだけ、楽器の AI のスレッドで新しく作って丸ごと差し替える）。
/// From から Group へ、SwitchT（AudioClock の秒。調べた音の終わりの時刻）から FadeSec 秒かけて移る。
/// どのデバイスも (SwitchT, いまの時刻) から同じ割合を計算するので、デバイスごとに状態を持たず、ずれない
/// </summary>
public sealed record MusicScene(SceneGroup Group, SceneGroup From, double SwitchT, float Confidence)
{
    /// <summary>場面が変わるときの、色・光り方の移り変わりの長さ（秒）</summary>
    public const double FadeSec = 2.5;
    /// <summary>場面なし（パレットの色のまま）</summary>
    public static readonly MusicScene None = new(SceneGroup.None, SceneGroup.None, double.NegativeInfinity, 0);
    /// <summary>移り変わりの進み具合（0 = まだ From、1 = Group）。始めと終わりがゆっくり</summary>
    public float Progress(double t) => EffectContext.Smoothstep((t - SwitchT) / FadeSec);
}

/// <summary>
/// AudioSet の音の種類（楽器の AI・CED-Tiny の出力の位置）と、楽器のまとまりへのまとめ方。種類は番号ではなく名前（CedTiny.Labels）で探す
/// （モデルが変わって番号がずれても、まとまりがずれない。名前が見つからなければ使わない。テストで全部見つかることを確かめる）。
/// まとまりの値は、そのまとまりの種類の（重み × らしさ）のいちばん大きいもの（重みは書いていなければ 1）
/// </summary>
public static class SceneClasses
{
    /// <summary>まとまりの番号（Groups が書く位置）。最初の 7 つは SceneGroup の Vocal〜Speech と同じ順（SceneGroup − 1）</summary>
    public const int Vocal = 0, Brass = 1, Guitar = 2, Keys = 3, Synth = 4, Strings = 5, Speech = 6, Drums = 7, Music = 8, Silence = 9;
    public const int Count = 10;
    /// <summary>場面になれるまとまりの数（Vocal〜Speech）</summary>
    public const int SceneCount = 7;

    /// <summary>まとまりの名前（ログ・調査用）</summary>
    public static readonly string[] Names = { "Vocal", "Brass", "Guitar", "Keys", "Synth", "Strings", "Speech", "Drums", "Music", "Silence" };   // no-loc（ログだけ）

    /// <summary>まとまりごとの、AudioSet の音の名前と重み（Groups の番号の順。Music・Silence は 1 つだけ）</summary>
    public static readonly (string Name, float W)[][] ClassNames =
    {
        // 歌
        new (string, float)[] { ("Singing", 1), ("Choir", 1), ("Yodeling", 1), ("Chant", 1), ("Mantra", 1), ("Male singing", 1), ("Female singing", 1),
            ("Child singing", 1), ("Synthetic singing", 1), ("Rapping", 1), ("Humming", 1), ("Vocal music", 1), ("A capella", 1) },
        // 金管・木管（木管は 0.8 倍）
        new (string, float)[] { ("Brass instrument", 1), ("French horn", 1), ("Trumpet", 1), ("Trombone", 1), ("Saxophone", 1),
            ("Wind instrument", 0.8f), ("Flute", 0.8f), ("Clarinet", 0.8f) },
        // ギター・はじく弦
        new (string, float)[] { ("Plucked string instrument", 1), ("Guitar", 1), ("Electric guitar", 1), ("Acoustic guitar", 1), ("Steel guitar", 1),
            ("Tapping (guitar technique)", 1), ("Strum", 1), ("Banjo", 1), ("Sitar", 1), ("Mandolin", 1), ("Zither", 1), ("Ukulele", 1) },
        // 鍵盤
        new (string, float)[] { ("Keyboard (musical)", 1), ("Piano", 1), ("Electric piano", 1), ("Organ", 1), ("Electronic organ", 1), ("Hammond organ", 1),
            ("Harpsichord", 1), ("Mallet percussion", 1), ("Marimba", 1), ("Glockenspiel", 1), ("Vibraphone", 1) },
        // シンセ。電子音楽のジャンルは 0.5 倍
        new (string, float)[] { ("Synthesizer", 1), ("Sampler", 1), ("Theremin", 1), ("Electronic music", 0.5f), ("House music", 0.5f), ("Techno", 0.5f),
            ("Dubstep", 0.5f), ("Electronica", 0.5f), ("Electronic dance music", 0.5f), ("Trance music", 0.5f) },
        // 弦（オーケストラは 0.7 倍）
        new (string, float)[] { ("Bowed string instrument", 1), ("String section", 1), ("Violin", 1), ("Pizzicato", 1), ("Cello", 1), ("Harp", 1), ("Orchestra", 0.7f) },
        // 話し声
        new (string, float)[] { ("Speech", 1), ("Male speech", 1), ("Female speech", 1), ("Child speech", 1),
            ("Conversation", 1), ("Narration", 1) },
        // ドラム（調査・ログだけ。ドラムだけの部分は場面なし = パレットの色）
        new (string, float)[] { ("Percussion", 1), ("Drum kit", 1), ("Drum machine", 1), ("Drum", 1), ("Snare drum", 1), ("Rimshot", 1), ("Drum roll", 1),
            ("Bass drum", 1), ("Timpani", 1), ("Tabla", 1), ("Cymbal", 1), ("Hi-hat", 1), ("Tambourine", 1), ("Beatboxing", 1) },
        new (string, float)[] { ("Music", 1) },
        new (string, float)[] { ("Silence", 1) },
    };   // no-loc（AI の音の名前）

    /// <summary>まとまりごとの、出力の位置と重み（ClassNames を名前で探したもの。見つからない名前は入れない）</summary>
    private static readonly (int I, float W)[][] Resolved = ClassNames
        .Select(g => g.Select(c => (I: CedTiny.IndexOf(c.Name), c.W)).Where(c => c.I >= 0).ToArray()).ToArray();

    /// <summary>ClassNames のうち、モデルの音の名前にないもの（テスト用。空のはず）</summary>
    internal static IEnumerable<string> MissingNames() => ClassNames.SelectMany(g => g).Where(c => CedTiny.IndexOf(c.Name) < 0).Select(c => c.Name);

    /// <summary>「音楽」「無音」の出力の位置</summary>
    public static readonly int MusicClass = CedTiny.IndexOf("Music"), SilenceClass = CedTiny.IndexOf("Silence");

    /// <summary>527 種類のらしさから、まとまりごとの値（Count 個。番号は Vocal〜Silence）を書く（メモリを確保しない）</summary>
    public static void Groups(ReadOnlySpan<float> scores, Span<float> groups)
    {
        for (int k = 0; k < Count; k++) groups[k] = Max(scores, Resolved[k]);
    }

    private static float Max(ReadOnlySpan<float> s, (int I, float W)[] classes)
    {
        float m = 0;
        foreach (var (i, w) in classes)
            if (i < s.Length) m = Math.Max(m, w * Safe(s[i]));
        return m;
    }

    /// <summary>おかしな値（NaN など）は 0、範囲は 0〜1</summary>
    private static float Safe(float x) => float.IsFinite(x) ? Math.Clamp(x, 0f, 1f) : 0f;
}

/// <summary>
/// 楽器のまとまりの値（1 秒ごと）から、いまの場面を決める（AI を使わない計算だけ。テストで値を入れて確かめられる）。
/// まとまりごとに約 1 秒でならし（ギター・鍵盤・シンセ・弦は 2 秒）、いちばん強いまとまりが入る値（EnterMin）を超え、今の場面より 1.25 倍強い状態が 2 秒（窓 2 つ）続き、
/// 前の切り替えから 8 秒たっていれば切り替える。静かな状態が 3 秒続いたら、すぐ場面なしに戻す。
/// 1 つのスレッドからだけ呼ぶ
/// </summary>
public sealed class SceneTracker
{
    /// <summary>
    /// 調整する値（テストで振って比べるため変えられる。アプリの中では変えない）。
    /// 録音の曲（ロック 4 曲・鍵盤の曲 4 曲。同じ曲の録り直しは 1 曲と数える）を、本物の AudioEngine → SceneTagger（2 秒の窓・1 秒ごと。ミッド、鍵盤・シンセ・弦はサイドも）に
    /// 33 通りの流し方（ふつうの取り込みの 192,000 Hz・10 ミリ秒ずつ、流し込む大きさ 480・512 個、始まりを 0.13〜0.88 秒ずらして窓の位置を変える、−12 dB、48,000 Hz の 11 通り。
    /// それぞれ窓の位置をさらに数ミリ秒ずらした 3 通り）で流して合わせた:
    /// ロックの曲はギターの場面が 19〜40%、鍵盤の曲は鍵盤の場面が 26〜83%（18 秒しかない鍵盤の曲だけは、33 通りのうち 6 通りで出ず、5 通りで終わりの 1 秒ほどだけ）、
    /// ギターのない曲にギター・ギターの曲に鍵盤・どの曲にも歌・金管・シンセ・弦の場面は出ない（MinDwellSec を 6〜12 秒にしても）。
    /// 切り替えは 1 分に 1.0〜2.5 回（20〜70 秒の短い鍵盤の曲は、入って・抜けて・入るだけで 2.6〜4.4 回）
    /// </summary>
    public static class Tune
    {
        /// <summary>まとまりの値をならす時定数（秒）</summary>
        public static double EmaTauSec = 1.0;
        /// <summary>
        /// ギター・鍵盤・シンセ・弦をならす時定数（秒）。これらのらしさは窓ごとの上下が大きく（ギターで 0〜0.6）、
        /// 2 秒にした（上の 33 通りの流し方で）: ギターのない曲にギター・ギターの曲に鍵盤は出ず、鍵盤の曲の 132 回のうち 126 回で鍵盤の場面が出る（平均 45%）。
        /// 1 秒・1.5 秒だと、入る値と抜ける値の間を行き来して、ギターの曲に短い鍵盤の場面が出た（32〜104 秒分）。
        /// 3.5 秒・5 秒だと、短い鍵盤の曲で場面が出ないことが増えた（132 回のうち 11〜18 回。平均 32〜36%）（長いほど、場面が変わるのが遅れる）。
        /// 歌・話し声・金管は 1 秒のまま
        /// </summary>
        public static double InstrumentEmaTauSec = 2.0;
        /// <summary>窓の間隔（秒。続いた時間を数えるとき、1 つの窓がこの長さを表す）</summary>
        public static double HopSec = CedTiny.HopSec;
        /// <summary>
        /// 場面なしの強さ（どの楽器も入る値に届かなければ場面なし）。場面なしから入るには、この Margin 倍（0.075）も超える必要がある。
        /// 0.10 だと鍵盤の入る値（0.11）より実際は高くなり（0.125）、短い鍵盤の曲で場面が出にくかった（132 回のうち 8 回出ない。平均 38%）
        /// </summary>
        public static float NoneFloor = 0.06f;
        /// <summary>
        /// 場面に入る値（Vocal, Brass, Guitar, Keys, Synth, Strings, Speech の順）。抜ける値はこの ExitRatio 倍。
        /// 歌は 0.30（CED-Tiny の歌のらしさは、歌っている曲でもほとんど 0.1 以下で、ときどき 1 つの窓だけ 0.3 くらいに跳ねる。0.20 だと短い歌の場面が出た）。
        /// 鍵盤は 0.11（鍵盤の曲でも、鍵盤のらしさは窓ごとに 0〜0.5 を行き来し、平均 0.1〜0.15。0.13 だと短い鍵盤の曲で場面が出にくく（132 回のうち 10 回出ない）、
        /// 0.10 以下だと、ギターの曲で 2 つの窓だけ鍵盤が 0.17 になったところに鍵盤の場面が出た（最低の長さが 6・8 秒のとき）。ギターは 0.20（0.18 だとロックの曲で 44% まで、0.24 だとギターの場面が早く抜け、そこに短い鍵盤の場面が出た）。
        /// 金管・シンセ・弦は、録音の曲に出てこないので前のまま
        /// </summary>
        public static readonly float[] EnterMin = { 0.30f, 0.15f, 0.20f, 0.11f, 0.25f, 0.15f, 0.40f };
        public static float ExitRatio = 0.5f;
        /// <summary>新しい場面が、今の場面のこの倍以上強くないと切り替えない（今の場面が抜ける値より弱ければ問わない）</summary>
        public static float Margin = 1.25f;
        /// <summary>新しい場面がいちばん強い状態が、この秒数続いたら切り替える（窓 2 つ。1 つだと、1 つの窓だけ跳ねた歌で場面が出た）</summary>
        public static double ConfirmSec = 2.0;
        /// <summary>切り替えてから、次に切り替えられるまでの最低の秒数（ちらちら変わらないように）</summary>
        public static double MinDwellSec = 8.0;
        /// <summary>静かな状態がこの秒数続いたら場面なしに戻す</summary>
        public static double QuietToNoneSec = 3.0;
        /// <summary>「音楽」のらしさ（ならした値）がこれ以上なら、話し声は歌として数え（ExtraVocalFromSpeech 倍）、これ未満なら話し声の場面になれる</summary>
        public static float MusicForSpeech = 0.3f;
        /// <summary>歌った声は「話し声」とも出やすいので、音楽が流れていれば話し声のこの倍を歌として数える（実際の曲では確かめていない）</summary>
        public static float ExtraVocalFromSpeech = 0.5f;
        /// <summary>「無音」のらしさがこれ以上の窓は静かとみなす</summary>
        public static float SilenceMin = 0.5f;
    }

    private readonly float[] _ema = new float[SceneClasses.Count];
    private readonly float[] _evidence = new float[SceneClasses.SceneCount];
    private bool _primed;
    private double _lastT = double.NaN;
    private SceneGroup _top = SceneGroup.None;
    private double _topSinceT = double.NaN;
    /// <summary>次に切り替えてよい時刻</summary>
    private double _nextAllowedT = double.NegativeInfinity;
    /// <summary>最後に音が鳴っていた窓の終わり（静かな時間を数える）</summary>
    private double _lastSoundT = double.NaN;

    /// <summary>いまの場面</summary>
    public MusicScene Current { get; private set; } = MusicScene.None;
    /// <summary>ならしたまとまりの値（Count 個。調査用）</summary>
    public ReadOnlySpan<float> Ema => _ema;
    /// <summary>場面を切り替えた回数（静かになって場面なしに戻したのも含む）</summary>
    public int Switches { get; private set; }

    private static float EnterMin(SceneGroup g) => g == SceneGroup.None ? Tune.NoneFloor : Tune.EnterMin[(int)g - 1];
    private static float ExitMin(SceneGroup g) => EnterMin(g) * Tune.ExitRatio;
    private float Evidence(SceneGroup g) => g == SceneGroup.None ? Tune.NoneFloor : _evidence[(int)g - 1];

    /// <summary>音の鳴っている窓 1 つ（windowEndT = 窓の終わりの時刻）。groups は SceneClasses.Groups の値。いまの場面を返す</summary>
    public MusicScene Update(double windowEndT, ReadOnlySpan<float> groups)
    {
        if (groups.Length < SceneClasses.Count) return Current;
        if (groups[SceneClasses.Silence] >= Tune.SilenceMin) return Quiet(windowEndT);
        bool settled = Settle(windowEndT);
        // 1. ならす（窓の間隔が空いたら、そのぶん強く新しい値に寄せる）
        double gap = double.IsFinite(_lastT) ? Math.Clamp(windowEndT - _lastT, 0.05, 5) : Tune.HopSec;
        _lastT = windowEndT;
        _lastSoundT = windowEndT;
        float a = (float)(1 - Math.Exp(-gap / Math.Max(1e-3, Tune.EmaTauSec)));
        float aSlow = (float)(1 - Math.Exp(-gap / Math.Max(1e-3, Tune.InstrumentEmaTauSec)));
        for (int k = 0; k < SceneClasses.Count; k++)
        {
            float x = float.IsFinite(groups[k]) ? Math.Clamp(groups[k], 0f, 1f) : 0f;
            // ギター・鍵盤・シンセ・弦は、長くならす（入ったり抜けたりを繰り返さない）
            bool slow = k is SceneClasses.Guitar or SceneClasses.Keys or SceneClasses.Synth or SceneClasses.Strings;
            float ak = slow ? aSlow : a;
            // 最初の窓も同じ時定数で 0 から寄せる（速いほうの割合で始めると、取り直した直後の 1 つの窓だけで場面に入ってしまう）
            _ema[k] = _primed ? _ema[k] + ak * (x - _ema[k]) : ak * x;
        }
        _primed = true;

        // 2. 場面ごとの強さ。音楽が流れていれば話し声の一部を歌に数え、話し声の場面は音楽がないときだけ
        bool music = _ema[SceneClasses.Music] >= Tune.MusicForSpeech;
        for (int k = 0; k < SceneClasses.SceneCount; k++) _evidence[k] = _ema[k];
        if (music)
        {
            _evidence[SceneClasses.Vocal] = Math.Max(_evidence[SceneClasses.Vocal], Tune.ExtraVocalFromSpeech * _ema[SceneClasses.Speech]);
            _evidence[SceneClasses.Speech] = 0;
        }

        // 3. いちばん強い候補（入る値を超えたものだけ。なければ場面なし）
        var top = SceneGroup.None;
        float best = Tune.NoneFloor;
        for (int k = 0; k < SceneClasses.SceneCount; k++)
        {
            var g = (SceneGroup)(k + 1);
            float e = _evidence[k];
            if (e >= EnterMin(g) && e > best) { best = e; top = g; }
        }
        if (top != _top || !double.IsFinite(_topSinceT)) { _top = top; _topSinceT = windowEndT; }

        // 4. 切り替えるか（取り直したあと、場面なしに戻すのを待っている間は切り替えない）
        var cur = Current.Group;
        if (top == cur || !settled) return Current;
        float eCur = Evidence(cur), eTop = Evidence(top);
        bool stronger = eTop >= Tune.Margin * eCur || eCur < ExitMin(cur);
        bool lasted = windowEndT - _topSinceT + Tune.HopSec >= Tune.ConfirmSec - 1e-9;
        bool dwelt = windowEndT >= _nextAllowedT - 1e-9;
        if (stronger && lasted && dwelt) SwitchTo(top, windowEndT, eTop, Tune.MinDwellSec);
        return Current;
    }

    /// <summary>
    /// 静かな窓 1 つ（音が鳴っていない・小さすぎる・AI が「無音」と言った）。場面はそのまま。
    /// 静かな状態が QuietToNoneSec 秒続いたら、すぐ場面なしに戻す（前の切り替えの移り変わりが終わっていれば）
    /// </summary>
    public MusicScene Quiet(double windowEndT)
    {
        Settle(windowEndT);
        if (!double.IsFinite(_lastSoundT)) _lastSoundT = windowEndT - Tune.HopSec;
        _top = SceneGroup.None;
        _topSinceT = double.NaN;
        bool longQuiet = windowEndT - _lastSoundT >= Tune.QuietToNoneSec - 1e-9;
        if (longQuiet && (Current.Group != SceneGroup.None || _primed))
        {
            // 次の曲は最初から: ならした値も捨てる
            Array.Clear(_ema);
            _primed = false;
            _lastT = double.NaN;
            if (Current.Group != SceneGroup.None && windowEndT >= Current.SwitchT + MusicScene.FadeSec - 1e-9)
                SwitchTo(SceneGroup.None, windowEndT, 0, MusicScene.FadeSec);
        }
        return Current;
    }

    /// <summary>
    /// 最初から（出力先が変わった・取り込み直した）。shown = いま見えている場面（省略すれば Current）。そこから時刻 t にゆっくり場面なしに移る。
    /// shown が移り変わっている途中なら、それが終わってから（途中で戻すと、色・光り方が一度に飛ぶ）。それまでは切り替えない。
    /// t を省略すると、すぐ場面なし（移り変わりなし）
    /// </summary>
    public void Reset(double t = double.NegativeInfinity, MusicScene? shown = null)
    {
        Array.Clear(_ema);
        Array.Clear(_evidence);
        _primed = false;
        _lastT = double.NaN;
        _top = SceneGroup.None;
        _topSinceT = double.NaN;
        _lastSoundT = double.NaN;
        _toNone = false;
        if (shown is not null) Current = shown;
        if (!double.IsFinite(t))
        {
            Current = MusicScene.None;
            _nextAllowedT = double.NegativeInfinity;
            return;
        }
        if (Current.Group != SceneGroup.None)
        {
            _toNone = true;
            Settle(t);
        }
        else _nextAllowedT = Current.SwitchT + MusicScene.FadeSec;   // 場面なしに移っている途中なら、それが終わるまで待つ
    }

    /// <summary>窓のない回（取り直した直後など）: Reset のあとの「場面なしに戻す」だけを進める</summary>
    public MusicScene Hold(double t)
    {
        Settle(t);
        return Current;
    }

    /// <summary>Reset のあと、場面なしに戻すのを待っている</summary>
    private bool _toNone;

    /// <summary>Reset のあとの「場面なしに戻す」を、いまの移り変わりが終わっていれば行う。戻し終えていれば（待っていなければ）true</summary>
    private bool Settle(double t)
    {
        if (!_toNone) return true;
        if (Current.Group == SceneGroup.None) { _toNone = false; return true; }
        if (t < Current.SwitchT + MusicScene.FadeSec - 1e-9) return false;
        Current = new MusicScene(SceneGroup.None, Current.Group, t, 0);
        _nextAllowedT = t + MusicScene.FadeSec;
        _toNone = false;
        return true;
    }

    private void SwitchTo(SceneGroup g, double t, float confidence, double dwell)
    {
        Current = new MusicScene(g, Current.Group, t, confidence);
        _nextAllowedT = t + dwell;
        Switches++;
    }
}
