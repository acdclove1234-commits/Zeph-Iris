// ==================================================
// MusicScene.cs - 曲の部分ごとに目立つ楽器（場面）: 楽器の AI（YAMNet）の 521 種類のらしさを楽器のまとまりにし、
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
/// AudioSet の音の種類の番号（YAMNet の出力の位置）と、楽器のまとまりへのまとめ方。
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

    // 歌: Singing, Choir, Yodeling, Chant, Mantra, Child singing, Synthetic singing, Rapping, Humming, Vocal music, A capella
    private static readonly (int I, float W)[] VocalClasses = { (24, 1), (25, 1), (26, 1), (27, 1), (28, 1), (29, 1), (30, 1), (31, 1), (32, 1), (249, 1), (250, 1) };
    // 話し声: Speech, Child speech, Conversation, Narration
    private static readonly (int I, float W)[] SpeechClasses = { (0, 1), (1, 1), (2, 1), (3, 1) };
    // 金管・木管: Brass instrument, French horn, Trumpet, Trombone, Saxophone, Wind instrument (0.8), Flute (0.8), Clarinet (0.8)
    private static readonly (int I, float W)[] BrassClasses = { (180, 1), (181, 1), (182, 1), (183, 1), (192, 1), (190, 0.8f), (191, 0.8f), (193, 0.8f) };
    // ギター・はじく弦: Plucked string, Guitar, Electric guitar, Acoustic guitar, Steel guitar, Tapping, Strum, Banjo, Sitar, Mandolin, Zither, Ukulele
    private static readonly (int I, float W)[] GuitarClasses = { (134, 1), (135, 1), (136, 1), (138, 1), (139, 1), (140, 1), (141, 1), (142, 1), (143, 1), (144, 1), (145, 1), (146, 1) };
    // 鍵盤: Keyboard, Piano, Electric piano, Organ, Electronic organ, Hammond organ, Harpsichord, Mallet percussion, Marimba, Glockenspiel, Vibraphone
    private static readonly (int I, float W)[] KeysClasses = { (147, 1), (148, 1), (149, 1), (150, 1), (151, 1), (152, 1), (155, 1), (174, 1), (175, 1), (176, 1), (177, 1) };
    // シンセ: Synthesizer, Sampler, Theremin。電子音楽のジャンル（Electronic music, House, Techno, Dubstep, Electronica, EDM, Trance）は 0.5
    private static readonly (int I, float W)[] SynthClasses = { (153, 1), (154, 1), (208, 1), (234, 0.5f), (235, 0.5f), (236, 0.5f), (237, 0.5f), (239, 0.5f), (240, 0.5f), (242, 0.5f) };
    // 弦: Bowed string, String section, Violin, Pizzicato, Cello, Harp, Orchestra (0.7)
    private static readonly (int I, float W)[] StringsClasses = { (184, 1), (185, 1), (186, 1), (187, 1), (188, 1), (194, 1), (179, 0.7f) };
    // ドラム（調査・ログだけ。ドラムだけの部分は場面なし = パレットの色）: Percussion, Drum kit, Drum machine, Drum, Snare drum, Rimshot,
    // Drum roll, Bass drum, Timpani, Tabla, Cymbal, Hi-hat, Tambourine, Beatboxing
    private static readonly (int I, float W)[] DrumsClasses = { (156, 1), (157, 1), (158, 1), (159, 1), (160, 1), (161, 1), (162, 1), (163, 1), (164, 1), (165, 1), (166, 1), (167, 1), (169, 1), (213, 1) };
    /// <summary>ベース（Bass guitar, Double bass）。調査用だけ（色にしない・Groups には入れない）</summary>
    public static readonly int[] BassClasses = { 137, 189 };
    public const int MusicClass = 132, SilenceClass = 494;

    /// <summary>521 種類のらしさから、まとまりごとの値（Count 個。番号は Vocal〜Silence）を書く</summary>
    public static void Groups(ReadOnlySpan<float> s521, Span<float> groups)
    {
        groups[Vocal] = Max(s521, VocalClasses);
        groups[Brass] = Max(s521, BrassClasses);
        groups[Guitar] = Max(s521, GuitarClasses);
        groups[Keys] = Max(s521, KeysClasses);
        groups[Synth] = Max(s521, SynthClasses);
        groups[Strings] = Max(s521, StringsClasses);
        groups[Speech] = Max(s521, SpeechClasses);
        groups[Drums] = Max(s521, DrumsClasses);
        groups[Music] = Safe(s521[MusicClass]);
        groups[Silence] = Safe(s521[SilenceClass]);
    }

    private static float Max(ReadOnlySpan<float> s, (int I, float W)[] classes)
    {
        float m = 0;
        foreach (var (i, w) in classes) m = Math.Max(m, w * Safe(s[i]));
        return m;
    }

    /// <summary>おかしな値（NaN など）は 0、範囲は 0〜1</summary>
    private static float Safe(float x) => float.IsFinite(x) ? Math.Clamp(x, 0f, 1f) : 0f;
}

/// <summary>
/// 楽器のまとまりの値（0.48 秒ごと）から、いまの場面を決める（AI を使わない計算だけ。テストで値を入れて確かめられる）。
/// まとまりごとに約 1 秒でならし、いちばん強いまとまりが入る値（EnterMin）を超え、今の場面より 1.25 倍強い状態が 1 秒続き、
/// 前の切り替えから 6 秒たっていれば切り替える。静かな状態が 3 秒続いたら、すぐ場面なしに戻す。
/// 1 つのスレッドからだけ呼ぶ
/// </summary>
public sealed class SceneTracker
{
    /// <summary>調整する値（テストで振って比べるため変えられる。アプリの中では変えない）</summary>
    public static class Tune
    {
        /// <summary>まとまりの値をならす時定数（秒）</summary>
        public static double EmaTauSec = 1.0;
        /// <summary>窓の間隔（秒。続いた時間を数えるとき、1 つの窓がこの長さを表す）</summary>
        public static double HopSec = Yamnet.HopSec;
        /// <summary>場面なしの強さ（どの楽器も入る値に届かなければ場面なし）</summary>
        public static float NoneFloor = 0.10f;
        /// <summary>場面に入る値（Vocal, Brass, Guitar, Keys, Synth, Strings, Speech の順）。抜ける値はこの ExitRatio 倍</summary>
        public static readonly float[] EnterMin = { 0.20f, 0.15f, 0.20f, 0.20f, 0.25f, 0.15f, 0.40f };
        public static float ExitRatio = 0.5f;
        /// <summary>新しい場面が、今の場面のこの倍以上強くないと切り替えない（今の場面が抜ける値より弱ければ問わない）</summary>
        public static float Margin = 1.25f;
        /// <summary>新しい場面がいちばん強い状態が、この秒数続いたら切り替える</summary>
        public static double ConfirmSec = 1.0;
        /// <summary>切り替えてから、次に切り替えられるまでの最低の秒数（ちらちら変わらないように）</summary>
        public static double MinDwellSec = 6.0;
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
        for (int k = 0; k < SceneClasses.Count; k++)
        {
            float x = float.IsFinite(groups[k]) ? Math.Clamp(groups[k], 0f, 1f) : 0f;
            _ema[k] = _primed ? _ema[k] + a * (x - _ema[k]) : a * x;
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
