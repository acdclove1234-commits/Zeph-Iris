// ==================================================
// ColorBlipDiag.cs - 色が変わる途中の「一瞬ちかっと光る」瞬間（transition blip）を数える
//   BlipMeter: LED ごとに、送った値（光の量 0〜255）の並びを見て、次の 3 種類を数える
//     lum  … 1〜4 コマの間だけ、明るさ（輝度 Y = 0.2126R + 0.7152G + 0.0722B）が前のコマと後のコマの両方より
//             Rel 倍 + Abs 以上高い（短く跳ね上がって戻る）。前と後で色が違う（または途中の色が外れている）ものは「色替え中」(colour)、
//             そのうち跳ね上がりの色が後の色と違う（前の色のまま・途中の色で光った）ものは other-colour に数える
//     max  … lum と同じ判定を、いちばん強い成分（LED の PWM の値そのもの）で
//     off  … 1〜3 コマの間だけ、色（明るさをそろえた OKLab）が前の色・後の色のどちらでもなく、その間（直線）からも外れている
//   判定する（いつも動く）のは BlipMeter の物差しそのもの（TestBlipMeterFlagsOnlyShortExcursions）と、合成した曲で
//   光り方ごとの色替えのちかっとした光が上限より少ないこと（TestColorChangeBlipsAreRare）。
//   光り方・パレット・強さ・デバイスごとの数え上げは、環境変数を付けたときだけ動く:
//     BLIP_DIAG=1            … 合成した曲で全部を回す（TestColorBlipSweep）・環境モード（TestColorBlipAmbient）・
//                              ちらつき止めの単体の確かめ（TestColorBlipFilterCases）
//     BLIP_WAV=フォルダー     … 録音（song_NN.wav、ステレオ。48 kHz でも 192 kHz でもよい: そのままの周波数で AudioEngine に渡す）で TestColorBlipSweep を回す（曲名は出さず song_NN だけ）
//   絞り込み: BLIP_STYLES / BLIP_PALETTES / BLIP_INTENS / BLIP_DEVICES / BLIP_SONGS（カンマ区切り）、BLIP_SECS（1 曲の秒数）、
//     BLIP_PASSES（base = そのまま、raw = LedSteady と AttackHold を外す、nosteady、nohold）、BLIP_UNLOCK=1（テンポをロックさせない）、
//     DIRECT_OPTS=nohold,slowbeat,settle（直接書き込むグラボの描き方・送り方。書いたものだけオン。DIRECT_OPTS=none で 6.15.8 まで）、BLIP_SCENES=1（楽器の AI の場面を
//     6 秒ごとに切り替える組も足す）、BLIP_SWITCH=1（光り方・パレットを途中で切り替える組も足す）、BLIP_EXAMPLES=数
//   しきい値: BLIP_REL（既定 0.25）・BLIP_ABS（既定 3/255）・BLIP_OFF（既定 0.08）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using ProFanController;

namespace LogicTests;

/// <summary>LED ごとの「短い跳ね上がり・外れた色」を数える物差し（送った値の並びを 1 つずつ渡す）</summary>
public sealed class BlipMeter
{
    public const int MaxK = 4, MaxOffK = 3, Depth = MaxK + 2;
    public static double Rel = 0.25, Abs = 3 / 255.0, OffThr = 0.08, ColThr = 0.05, MinVisible = 0.06;

    private readonly int _n;
    private readonly float[][] _y, _m;
    private readonly Lab[][] _lab;
    private readonly Rgb[][] _rgb;
    private readonly long[] _endLum, _endMax, _endOff;
    private long _f = -1;
    public readonly string Label;

    public long Samples, LumBlips, LumColour, LumStale, MaxBlips, MaxColour, OffPath;
    public double WorstLum, WorstOff;
    public readonly List<(double Score, string Text)> Examples = new();
    public int MaxExamples = 4;
    /// <summary>サンプルの時刻（例の表示用。曲の頭からの秒）</summary>
    public double Now;

    public BlipMeter(int leds, string label = "")
    {
        _n = leds;
        Label = label;
        _y = new float[Depth][];
        _m = new float[Depth][];
        _lab = new Lab[Depth][];
        _rgb = new Rgb[Depth][];
        for (int s = 0; s < Depth; s++)
        {
            _y[s] = new float[leds];
            _m[s] = new float[leds];
            _lab[s] = new Lab[leds];
            _rgb[s] = new Rgb[leds];
        }
        _endLum = new long[leds];
        _endMax = new long[leds];
        _endOff = new long[leds];
        Array.Fill(_endLum, -1);
        Array.Fill(_endMax, -1);
        Array.Fill(_endOff, -1);
    }

    private static int Slot(long f) => (int)(f % Depth);

    public static float Luma(Rgb c) => (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
    public static float MaxOf(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B)) / 255f;

    /// <summary>明るさをそろえた色（いちばん強い成分を 1 にした光の量を OKLab に）。消えていれば黒</summary>
    public static Lab Norm(Rgb c)
    {
        float m = Math.Max(c.R, Math.Max(c.G, c.B));
        if (m <= 0) return new Lab(0, 0, 0);
        return OkLab.FromLinear(c.R / m, c.G / m, c.B / m);
    }

    public static double Dist(Lab a, Lab b)
    {
        double dl = a.L - b.L, da = a.A - b.A, db = a.B - b.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    /// <summary>点 p と、線分 a–b の距離</summary>
    public static double DistToSegment(Lab p, Lab a, Lab b)
    {
        double ux = b.L - a.L, uy = b.A - a.A, uz = b.B - a.B;
        double vx = p.L - a.L, vy = p.A - a.A, vz = p.B - a.B;
        double uu = ux * ux + uy * uy + uz * uz;
        double s = uu > 1e-12 ? Math.Clamp((ux * vx + uy * vy + uz * vz) / uu, 0, 1) : 0;
        double dx = vx - s * ux, dy = vy - s * uy, dz = vz - s * uz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>1 サンプル（その時に LED が出している色）を足す。count = false なら並びには入れるが数えない（立ち上がりの間など）</summary>
    public void Feed(ReadOnlySpan<Rgb> frame, bool count = true)
    {
        _f++;
        int s = Slot(_f);
        int n = Math.Min(_n, frame.Length);
        for (int i = 0; i < n; i++)
        {
            var c = frame[i];
            _rgb[s][i] = c;
            _y[s][i] = Luma(c);
            _m[s][i] = MaxOf(c);
            _lab[s][i] = Norm(c);
        }
        if (!count) return;
        Samples++;
        for (int i = 0; i < n; i++)
        {
            CheckBump(i, _y, _endLum, isLum: true);
            CheckBump(i, _m, _endMax, isLum: false);
            CheckOff(i);
        }
    }

    private void CheckBump(int i, float[][] v, long[] end, bool isLum)
    {
        for (int k = 1; k <= MaxK; k++)
        {
            long before = _f - k - 1;
            if (before < 0) return;
            if (_f - k <= end[i]) return;   // この窓はもう数えた跳ね上がりと重なる
            float vb = v[Slot(before)][i], va = v[Slot(_f)][i];
            float wmin = float.MaxValue;
            for (long w = _f - k; w < _f; w++) wmin = Math.Min(wmin, v[Slot(w)][i]);
            double refv = Math.Max(vb, va);
            double thr = refv * (1 + Rel) + Abs;
            if (wmin <= thr) continue;
            end[i] = _f - 1;
            bool colour = Dist(_lab[Slot(before)][i], _lab[Slot(_f)][i]) > ColThr;
            for (long w = _f - k; w < _f && !colour; w++)
            {
                var p = _lab[Slot(w)][i];
                if (Math.Min(Dist(p, _lab[Slot(before)][i]), Dist(p, _lab[Slot(_f)][i])) > ColThr) colour = true;
            }
            double score = wmin / Math.Max(refv, 1e-3);
            // 跳ね上がりの色が、後の色と違う（前の色のまま光った・途中の色で光った）
            bool stale = false;
            for (long w = _f - k; w < _f; w++) stale |= Dist(_lab[Slot(w)][i], _lab[Slot(_f)][i]) > ColThr;
            if (isLum)
            {
                LumBlips++;
                if (colour) LumColour++;
                if (colour && stale) LumStale++;
                WorstLum = Math.Max(WorstLum, score);
                if (colour) AddExample(score, $"lum x{score:F2} k{k}", i, before, k);
            }
            else
            {
                MaxBlips++;
                if (colour) MaxColour++;
            }
            return;
        }
    }

    private void CheckOff(int i)
    {
        for (int k = 1; k <= MaxOffK; k++)
        {
            long before = _f - k - 1;
            if (before < 0) return;
            if (_f - k <= _endOff[i]) return;
            int sb = Slot(before), sa = Slot(_f);
            if (_m[sb][i] < MinVisible * 0.5 || _m[sa][i] < MinVisible * 0.5) continue;
            Lab a = _lab[sb][i], b = _lab[sa][i];
            double dmin = double.MaxValue;
            bool ok = true;
            for (long w = _f - k; w < _f; w++)
            {
                int sw = Slot(w);
                if (_m[sw][i] < MinVisible) { ok = false; break; }
                dmin = Math.Min(dmin, DistToSegment(_lab[sw][i], a, b));
            }
            if (!ok || dmin <= OffThr) continue;
            _endOff[i] = _f - 1;
            OffPath++;
            WorstOff = Math.Max(WorstOff, dmin);
            AddExample(dmin * 10, $"off d{dmin:F2} k{k}", i, before, k);
            return;
        }
    }

    private void AddExample(double score, string head, int led, long before, int k)
    {
        if (MaxExamples <= 0) return;
        if (Examples.Count >= MaxExamples && score <= Examples[^1].Score) return;
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{head} t={Now:F2}s led {led}:");
        for (long f = before; f <= _f; f++)
        {
            var c = _rgb[Slot(f)][led];
            sb.Append(CultureInfo.InvariantCulture, $" [{c.R},{c.G},{c.B} Y{_y[Slot(f)][led] * 255:F0}]");
        }
        Examples.Add((score, sb.ToString()));
        Examples.Sort((x, y) => y.Score.CompareTo(x.Score));
        if (Examples.Count > MaxExamples) Examples.RemoveAt(Examples.Count - 1);
    }

    public void AddTo(BlipTotals t)
    {
        t.Samples += Samples;
        t.Lum += LumBlips;
        t.LumColour += LumColour;
        t.LumStale += LumStale;
        t.Max += MaxBlips;
        t.MaxColour += MaxColour;
        t.Off += OffPath;
    }
}

/// <summary>
/// 直接書き込むグラボの I2C の代わり: 書いた回数と、ゾーンごとの「固定色（1 回でゾーン全体）」と「直接（LED 1 個ずつ）」の切り替えを数える。
/// 直接モードに入る命令は [直接, 明るさ, LED 0, 0, R, G, B]（LED 0 番を書くのと同じ形）。6.15.8 までは黒で書いていたので、LED を書き終わるまでの間
/// LED 0 番が一瞬消えていた（BlackEntries: 直接モードに入る命令が黒だった回数）
/// </summary>
public sealed class ModeCountingBus : II2cBlockWriter
{
    private readonly Dictionary<byte, byte> _mode = new();
    public bool Counting = true;
    public long Writes, ToDirect, ToStatic, BlackEntries;

    public bool WriteBlock(byte register, ReadOnlySpan<byte> data)
    {
        byte mode = data.Length > 0 ? data[0] : (byte)0;
        if (Counting)
        {
            Writes++;
            if (_mode.TryGetValue(register, out byte old) && old != mode)
            {
                if (mode == PnyEpicXEncoder.ModeDirect)
                {
                    ToDirect++;
                    if (data.Length >= 7 && data[4] == 0 && data[5] == 0 && data[6] == 0) BlackEntries++;
                }
                else if (mode == PnyEpicXEncoder.ModeStatic) ToStatic++;
            }
        }
        _mode[register] = mode;
        return true;
    }
}

/// <summary>
/// 直接書き込むグラボの描き方・送り方の切り替え（調べる用）。環境変数 DIRECT_OPTS に、オンにするものをカンマ区切りで書く:
/// nohold（AttackHold・LedSteady を使わない。片方だけなら noattack・nosteady）、slowbeat（ビートの光・色替えを遅いデバイスの長さに）、settle（色替えが終わる拍の頭で送る）。
/// none ならどれもオフ（6.15.8 までと、いまの製品の既定）。付けなければ製品と同じ
/// </summary>
public static class DirectGpuOptions
{
    // 製品の既定（最初に触ったときの値）
    private static readonly bool NoAttack = DeviceRenderer.DirectGpuNoAttackHold, NoSteady = DeviceRenderer.DirectGpuNoSteady,
        SlowBeat = DeviceRenderer.DirectGpuSlowBeat, Settle = RgbDeviceChannel.DirectGpuSettledSend;

    public static void Apply()
    {
        if (Environment.GetEnvironmentVariable("DIRECT_OPTS") is not { Length: > 0 } s) { Reset(); return; }
        var o = s.Split(',', StringSplitOptions.TrimEntries);
        DeviceRenderer.DirectGpuNoAttackHold = o.Contains("nohold") || o.Contains("noattack");
        DeviceRenderer.DirectGpuNoSteady = o.Contains("nohold") || o.Contains("nosteady");
        DeviceRenderer.DirectGpuSlowBeat = o.Contains("slowbeat");
        RgbDeviceChannel.DirectGpuSettledSend = o.Contains("settle");
    }

    public static void Reset()
    {
        DeviceRenderer.DirectGpuNoAttackHold = NoAttack;
        DeviceRenderer.DirectGpuNoSteady = NoSteady;
        DeviceRenderer.DirectGpuSlowBeat = SlowBeat;
        RgbDeviceChannel.DirectGpuSettledSend = Settle;
    }
}

/// <summary>数え上げの合計（LED 1 個・1 分あたりにするための LED の数と秒数も持つ）</summary>
public sealed class BlipTotals
{
    public long Samples, Lum, LumColour, LumStale, Max, MaxColour, Off;
    public double LedMinutes;
    public double Rate(long x) => LedMinutes > 0 ? x / LedMinutes : 0;
    public string Format() =>
        $"lum {Rate(Lum),6:F2} (colour {Rate(LumColour),6:F2}, other-colour {Rate(LumStale),6:F2})  max {Rate(Max),6:F2} (colour {Rate(MaxColour),6:F2})  off {Rate(Off),6:F2}  per LED-min";
}

public static class ColorBlipTests
{
    private const double Fps = 60, Tick = 1 / Fps, T0 = 1000.0, WarmSec = 3;

    // ===== 物差しの確かめ（いつも動く）

    /// <summary>BlipMeter は、1〜4 コマだけの跳ね上がり・外れた色だけを数え、なめらかな色替え・ゆっくり消える光は数えない</summary>
    public static void TestBlipMeterFlagsOnlyShortExcursions()
    {
        static Rgb Lerp(Rgb a, Rgb b, double u) => new((byte)Math.Round(a.R + (b.R - a.R) * u), (byte)Math.Round(a.G + (b.G - a.G) * u), (byte)Math.Round(a.B + (b.B - a.B) * u));
        BlipMeter Run(IEnumerable<Rgb> seq)
        {
            var m = new BlipMeter(1);
            foreach (var c in seq) m.Feed(new[] { c });
            return m;
        }
        var red = new Rgb(255, 0, 0);
        var blue = new Rgb(0, 0, 255);
        var dimBlue = new Rgb(0, 0, 40);
        // 1. 赤 → 青へ 6 コマで光の量のまま混ぜる（途中はいつも 2 色の間）
        var fade = Enumerable.Repeat(red, 5).Concat(Enumerable.Range(1, 6).Select(k => Lerp(red, blue, k / 6.0))).Concat(Enumerable.Repeat(blue, 5));
        var m1 = Run(fade);
        T.Check(m1.LumBlips == 0 && m1.MaxBlips == 0 && m1.OffPath == 0, $"linear crossfade is not a blip (lum {m1.LumBlips}, max {m1.MaxBlips}, off {m1.OffPath})");
        // 2. ビートの光のように、時定数 0.12 秒で消えていく（4 コマで戻らない）
        var decay = Enumerable.Repeat(new Rgb(0, 0, 10), 4).Concat(Enumerable.Range(0, 30).Select(k => new Rgb(0, 0, (byte)Math.Round(10 + 245 * Math.Exp(-k / Fps / 0.12)))));
        var m2 = Run(decay);
        T.Check(m2.LumBlips == 0 && m2.MaxBlips == 0, $"beat flash decaying with tau 0.12 s is not a blip (lum {m2.LumBlips}, max {m2.MaxBlips})");
        // 3. 明るい青 → 暗い緑の下地へ落ちるときに、緑が 2 コマだけ明るさを保って出る（LedSteady の色の取り違え）
        var green = new Rgb(0, 140, 0);
        var swap = Enumerable.Repeat(blue, 4).Append(green).Append(new Rgb(0, 80, 0)).Concat(Enumerable.Repeat(new Rgb(0, 25, 0), 5));
        var m3 = Run(swap);
        T.Check(m3.LumBlips == 1 && m3.LumColour == 1, $"held background colour after a hue change is a colour blip (lum {m3.LumBlips}, colour {m3.LumColour})");
        // 4. 赤の固定色の間に 1 コマだけ黄色
        var spike = Enumerable.Repeat(red, 4).Append(new Rgb(255, 255, 0)).Concat(Enumerable.Repeat(red, 4));
        var m4 = Run(spike);
        T.Check(m4.LumBlips == 1 && m4.OffPath == 1 && m4.MaxBlips == 0, $"one yellow frame inside red is a luminance and off-path blip, not a max-channel one (lum {m4.LumBlips}, off {m4.OffPath}, max {m4.MaxBlips})");
        // 5. 暗い青 → 明るい青へ上がって、そのまま明るい（跳ね上がりではない）
        var rise = Enumerable.Repeat(dimBlue, 4).Concat(Enumerable.Repeat(blue, 8));
        var m5 = Run(rise);
        T.Check(m5.LumBlips == 0 && m5.MaxBlips == 0 && m5.OffPath == 0, "step up that stays is not a blip");
    }

    // ===== 色替えのちかっとした光が少ない（いつも動く。合成した曲で判定する）

    /// <summary>
    /// 光り方ごとの、色替えの途中のちかっとした光（BlipMeter の lum のうち、前後で色が違う・途中の色が外れているもの）の上限
    /// （LED 1 個・1 分あたり。速いデバイス・パレット 2 つ・強さ 2 つ・合成した 3 曲の合計）。
    /// 6.15.8 では BeatHue・Sparkle・Pulse・Ensemble・Mix・Energy などがこれを大きく超えていた（LedSteady・AttackHold が、
    /// 色が変わったのに今の色を前の明るさで出していた・ミックスの色が帯の重みで 1 コマずつ飛んでいた・エナジーの帯の重なり）
    /// </summary>
    public static readonly Dictionary<string, double> ColorBlipLimit = new()
    {
        // 6.15.8 → 直したあと（このテストの合成した曲で）: BeatHue 36.3 → 1.3、Role 16.9 → 0.2、
        // Ensemble 15.3 → 0.6、Meter 7.5 → 3.7、Sparkle 1.5 → 0.2、Ripple 10.9 → 10.3。
        // 波紋（細い輪が暗い LED を通る）は光り方そのものが短い光なので、上限は今の値から大きく増えないことだけを見る
        [MusicStyles.BeatHue] = 4, [MusicStyles.Meter] = 6,
        [MusicStyles.Spectrum] = 1, [MusicStyles.Ripple] = 14,
        [MusicStyles.Sparkle] = 1,
        [MusicStyles.Role] = 2, [MusicStyles.Ensemble] = 3,
    };

    /// <summary>どの光り方でも、色が変わるときに 1〜4 コマだけ明るく光る（色替えの途中のちかっとした光）ことが少ない</summary>
    public static void TestColorChangeBlipsAreRare()
    {
        var songs = SynthSongs(24);
        var palettes = new[] { Palettes.Rainbow, Palettes.Neon };
        var intens = new[] { Intensities.Moderate, Intensities.High };
        var styles = MusicStyles.All;
        var tot = styles.ToDictionary(x => x, _ => new BlipTotals());
        _synthetic = true;
        try
        {
            foreach (var song in songs)
            {
                double minutes = Math.Max(0, song.Mid.Length / (double)song.Fs - WarmSec) / 60;
                foreach (var inten in intens)
                {
                    var groups = BuildGroups(styles, palettes, inten, new[] { "usb" }, false, false, 0);
                    RunSong(song.Mid, song.Side, song.Fs, inten, groups, false);
                    foreach (var g in groups)
                    foreach (var L in g.Lanes)
                    {
                        L.Meter.AddTo(tot[L.Style]);
                        tot[L.Style].LedMinutes += L.Ch.Device.LedCount * minutes;
                    }
                }
            }
        }
        finally { _synthetic = false; }
        foreach (var style in styles)
        {
            var t = tot[style];
            double rate = t.Rate(t.LumColour);
            double limit = ColorBlipLimit.TryGetValue(style, out double v) ? v : 4;
            T.Info($"{style,-9} {t.Format()}");
            T.Check(rate <= limit, $"{style}: colour-change luminance blips {rate:F2} per LED-min (limit {limit})");
        }
    }

    // ===== ちらつき止めの単体の確かめ（BLIP_DIAG=1）

    /// <summary>
    /// LedSteady・AttackHold に、明るい色から別の色の暗い下地へ落ちる光を通し、1 コマごとの輝度を出す
    /// （LedSteady は明るさを保つとき、そのコマの色を使うので、下地の色が保った明るさで出る）
    /// </summary>
    public static void TestColorBlipFilterCases()
    {
        if (Environment.GetEnvironmentVariable("BLIP_DIAG") != "1") { T.Skip("BLIP_DIAG=1 not set"); return; }
        float dt = (float)Tick;
        var cases = new (string Name, LinColor Lit, LinColor Bg)[]
        {
            ("blue -> dim green bg", new(0, 0, 1), new(0, 0.10f, 0)),
            ("purple -> dim yellow bg", new(0.2f, 0.04f, 0.85f), new(0.10f, 0.10f, 0)),
            ("red -> dim cyan bg", new(1, 0, 0), new(0, 0.08f, 0.10f)),
            ("pink -> dim green bg (Neon)", new(1, 0, 0.22f), new(0, 0.10f, 0.02f)),
            // AttackHold は 50% より下へ落ちると保たないので、半分より明るい下地で見る
            ("blue -> green 55%", new(0, 0, 1), new(0, 0.55f, 0)),
            ("purple -> cyan 60%", new(0.2f, 0.04f, 0.85f), new(0, 0.47f, 0.60f)),
        };
        foreach (var (name, lit, bg) in cases)
        foreach (bool rise in new[] { false, true })
        {
            foreach (string which in new[] { "steady", "hold" })
            {
                var px = new LinColor[1];
                var steady = new LedSteady();
                var hold = new AttackHold();
                var seq = new List<string>();
                double ref0 = 0, ref1 = 0, peak = 0;
                for (int f = 0; f < 14; f++)
                {
                    // 0〜4: 明るい色（4 コマ目まで少しずつ下がる）、5 から: 下地の色（1 コマで切り替わる）。
                    // rise: 0〜3 は暗く、4 で明るくなった直後（1 コマ後）に下地の色へ
                    px[0] = f < 5 ? (rise ? (f < 4 ? lit * 0.3f : lit) : lit * (1 - 0.03f * f)) : bg;
                    if (which == "steady") steady.Apply(px, 1, dt);
                    else hold.Apply(px, 1, dt);
                    var c = px[0];
                    double y = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                    if (f == 4) ref0 = y;
                    if (f == 13) ref1 = y;
                    if (f >= 5 && f <= 9) peak = Math.Max(peak, y);
                    seq.Add($"{y * 255:F0}");
                }
                T.Info($"{which,-6} {(rise ? "rise+" : "fall+")}{name,-28} Y(x255) per frame: {string.Join(" ", seq)}   lit-end {ref0 * 255:F0}  bg {ref1 * 255:F0}  worst after switch {peak * 255:F0} (x{peak / Math.Max(ref0, ref1):F1})");
            }
        }

        // 楽器の AI の場面の移り変わり（PaletteView）: 2 つの場面の色を OKLab で混ぜ、いちばん強い成分を 2 色の間にそろえる。
        // 色相が離れた場面どうしでは途中が灰色に近づき、それを明るさだけそろえるので白っぽく光る（2.5 秒かけて）
        var groups = Enum.GetValues<SceneGroup>().Where(s => ScenePalettes.Of(s) is not null).ToArray();
        var user = ColorPalette.Get(Palettes.Neon, null);
        var worst = new List<(double Ratio, string Text)>();
        foreach (var a in groups)
        foreach (var b in groups)
        {
            if (a >= b) continue;
            var pa = ScenePalettes.Of(a)!;
            var pb = ScenePalettes.Of(b)!;
            double ya = Y(new PaletteView(user, pa, pa, 1).At(0, true)), yb = Y(new PaletteView(user, pb, pb, 1).At(0, true));
            double ymax = 0, cmin = double.MaxValue, uAt = 0;
            for (int k = 1; k < 20; k++)
            {
                float u = k / 20f;
                var c = new PaletteView(user, pa, pb, u).At(0, true);
                double y = Y(c);
                if (y > ymax) { ymax = y; uAt = u; }
                cmin = Math.Min(cmin, BlipMeter.Norm(new Rgb((byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255))).Chroma);
            }
            double ratio = ymax / Math.Max(1e-6, Math.Max(ya, yb));
            worst.Add((ratio, $"scene {a,-8} -> {b,-8}: Y ends {ya * 255:F0}/{yb * 255:F0}, max on the way {ymax * 255:F0} at u={uAt:F2} (x{ratio:F2}), min chroma {cmin:F3}"));
        }
        foreach (var w in worst.OrderByDescending(x => x.Ratio).Take(8)) T.Info(w.Text);

        static double Y(LinColor c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
    }

    // ===== 合成した曲・録音で、光り方 × パレット × 強さ × デバイスを回す

    private sealed record DevSpec(string Name, RgbDevice Device, bool Follow, bool PerTick);

    private static DevSpec[] Devices() => new[]
    {
        new DevSpec("usb", new RgbDevice(0, "Strip", 24, 4, null, "HID: x"), false, true),
        new DevSpec("dram", new RgbDevice(1, "DRAM stick", 8, RgbDevice.TypeDram, null, "I2C: x"), false, false),
        new DevSpec("dramF", new RgbDevice(2, "DRAM stick", 8, RgbDevice.TypeDram, null, "I2C: x"), true, false),
        new DevSpec("gpu", new RgbDevice(3, "GeForce RTX ARGB", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }), false, false),
        new DevSpec("gpuF", new RgbDevice(4, "GeForce RTX ARGB", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }), true, false),
        new DevSpec("pny", new RgbDevice(5, "PNY GeForce RTX ARGB Epic-X", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true }, false, false),
    };

    private sealed class Lane
    {
        public required string Style, Palette, Intensity, Dev;
        public required bool Scene;
        public required RgbDeviceChannel Ch;
        public required BlipMeter Meter;
        public required bool PerTick, Follow;
        public Lane? Leader;
        public Rgb[] Shown = Array.Empty<Rgb>();
        public bool HasShown;
        public long Sends, MidFadeSends;
        /// <summary>追いかけるデバイス: 合わせる元が暗すぎて代表の色がなく、自分の光り方で描いたコマ・そのコマを送った回数</summary>
        public long FollowMiss, FollowMissSends;
        public bool MissNow;
        // 速いデバイス: ビートの光の立ち上がりと、色を進めた（共有の色）コマとのずれ（コマ数。0〜3、4 = 光なし・4 コマ以上）
        public readonly long[] StepLag = new long[5];
        public long Frames, LockedFrames, FlashNoStep;
        /// <summary>ビートの光の立ち上がりのコマで、色のクロスフェードがまだ途中（前の色・途中の色で光った）だった回数と、立ち上がりの数</summary>
        public long FlashMidFade, FlashOnsets;
        public long FrameNo, LastFlashFrame = long.MinValue / 2;
        public float PrevFl;
        public double PrevStepT = double.NegativeInfinity;
        public bool FlashStepped;
        /// <summary>遅いデバイス: 送った色の並び（1 回の送信を 1 つ）で数える物差し（1 回だけ送った色が前後と違う、など）</summary>
        public BlipMeter SentMeter = new(0);
        /// <summary>直接書き込むグラボ: 本物の書き方（PnyEpicXEncoder）に送った色を通し、固定色モードと直接モードの切り替えを数える</summary>
        public PnyEpicXEncoder? Pny;
        public readonly ModeCountingBus PnyBus = new();
        /// <summary>途中で光り方・パレットを切り替える組（BLIP_SWITCH）</summary>
        public bool Switch;
        public RgbConfig Cfg = new();
    }

    private sealed class Group
    {
        public required string Intensity;
        public readonly List<Lane> Lanes = new();
        public readonly SharedColor Shared = new();
    }

    private static string[] EnvList(string name, IEnumerable<string> fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : fallback.ToArray();

    private static void ReadThresholds()
    {
        var inv = CultureInfo.InvariantCulture;
        if (double.TryParse(Environment.GetEnvironmentVariable("BLIP_REL"), NumberStyles.Float, inv, out double r)) BlipMeter.Rel = r;
        if (double.TryParse(Environment.GetEnvironmentVariable("BLIP_ABS"), NumberStyles.Float, inv, out double a)) BlipMeter.Abs = a;
        if (double.TryParse(Environment.GetEnvironmentVariable("BLIP_OFF"), NumberStyles.Float, inv, out double o)) BlipMeter.OffThr = o;
    }

    /// <summary>ステレオの WAV を、ミッド（モノラル）とサイドで読む（Push に渡す形）</summary>
    public static (float[] Mid, float[] Side, int Fs) ReadWavStereo(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        int ch = 2, fs = 48000, bits = 32, format = 3;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                format = br.ReadUInt16();
                ch = br.ReadInt16();
                fs = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                bool isFloat = format == 3 || (format != 1 && bits == 32);
                // 書きかけの録音（data の大きさが実際より大きい・0xFFFFFFFF）でも、あるところまで読む
                long avail = size >= 0 ? Math.Min(size, br.BaseStream.Length - br.BaseStream.Position) : br.BaseStream.Length - br.BaseStream.Position;
                int n = (int)(avail / (bits / 8) / Math.Max(1, ch));
                var mid = new float[n];
                var side = new float[n];
                float Read() => bits switch
                {
                    16 => br.ReadInt16() / 32768f,
                    24 => (br.ReadByte() | (br.ReadByte() << 8) | (br.ReadSByte() << 16)) / 8388608f,
                    _ => isFloat ? br.ReadSingle() : br.ReadInt32() / 2147483648f,
                };
                for (int i = 0; i < n; i++)
                {
                    float l = Read(), r = ch >= 2 ? Read() : l;
                    for (int c = 2; c < ch; c++) Read();
                    mid[i] = 0.5f * (l + r);
                    side[i] = 0.5f * (l - r);
                }
                return (mid, side, fs);
            }
            else br.ReadBytes(size);
        }
        return (Array.Empty<float>(), Array.Empty<float>(), fs);
    }

    /// <summary>合成した曲: 音の多い曲（118・128 BPM）と、休み・静かな部分のある曲（音が止まったときの光り方への切り替えも通る）</summary>
    private static List<(string Id, float[] Mid, float[] Side, int Fs)> SynthSongs(double secs)
    {
        var list = new List<(string, float[], float[], int)>();
        foreach (double bpm in new[] { 118.0, 128 })
        {
            var g = new SignalGen(48000, secs, seed: (int)bpm);
            g.DenseMix(bpm, 0.5, secs - 0.5);
            list.Add(($"synth{bpm:000}", g.Buf, Array.Empty<float>(), g.Fs));
        }
        {
            var g = new SignalGen(48000, secs, seed: 7);
            // 10 秒ごとに: 7 秒鳴らす → 1.5 秒静か（パッドだけ）→ 1.5 秒無音
            for (double t0 = 0.5; t0 < secs - 1; t0 += 10)
            {
                double end = Math.Min(secs - 0.5, t0 + 7);
                g.DenseMix(124, t0, end);
                if (end + 1.5 < secs) g.Tone(220, 0.01, end, end + 1.5);
            }
            list.Add(("breaks124", g.Buf, Array.Empty<float>(), g.Fs));
        }
        return list;
    }

    /// <summary>場面の切り替え（BLIP_SCENES）: 6 秒ごとに 歌 → ギター → シンセ → なし → 弦 → 金管 → …</summary>
    private static MusicScene SceneAt(double songT, ref MusicScene? last)
    {
        var order = new[] { SceneGroup.Vocal, SceneGroup.Guitar, SceneGroup.Synth, SceneGroup.None, SceneGroup.Strings, SceneGroup.Brass, SceneGroup.Keys };
        int k = (int)Math.Floor(songT / 6);
        if (k <= 0) return last = MusicScene.None;
        var g = order[(k - 1) % order.Length];
        var from = k >= 2 ? order[(k - 2) % order.Length] : SceneGroup.None;
        double sw = T0 + k * 6;
        if (last is { } l && l.Group == g && l.SwitchT == sw) return l;
        return last = new MusicScene(g, from, sw, 0.8f);
    }

    public static void TestColorBlipSweep()
    {
        string? wavDir = Environment.GetEnvironmentVariable("BLIP_WAV");
        bool synth = Environment.GetEnvironmentVariable("BLIP_DIAG") == "1";
        if (!synth && string.IsNullOrEmpty(wavDir)) { T.Skip("BLIP_DIAG=1 / BLIP_WAV not set"); return; }
        ReadThresholds();
        var inv = CultureInfo.InvariantCulture;
        double secs = double.TryParse(Environment.GetEnvironmentVariable("BLIP_SECS"), NumberStyles.Float, inv, out double s) ? s : 30;
        var songs = new List<(string Id, float[] Mid, float[] Side, int Fs)>();
        if (!string.IsNullOrEmpty(wavDir) && Directory.Exists(wavDir))
        {
            int idx = 0;
            foreach (var f in Directory.GetFiles(wavDir, "*.wav").OrderBy(x => x, StringComparer.Ordinal))
            {
                idx++;
                var (mid, side, fs) = ReadWavStereo(f);
                // 名前が song_NN でなければ、番号だけで呼ぶ（曲名を出さない）
                string name = Path.GetFileNameWithoutExtension(f);
                string id = System.Text.RegularExpressions.Regex.IsMatch(name, "^song_[0-9]+$") ? name : $"song_{idx:00}";
                if (Environment.GetEnvironmentVariable("BLIP_SONGS") is { Length: > 0 } only && !only.Split(',').Contains(id)) continue;
                int take = (int)Math.Min(mid.Length, secs * fs);
                songs.Add((id, mid[..take], side.Length >= take ? side[..take] : side, fs));
            }
        }
        if (synth) songs.AddRange(SynthSongs(secs));
        string[] passes = EnvList("BLIP_PASSES", new[] { "base", "raw" });
        // BLIP_FOLLOWHOLD=0: 合わせる元が暗いコマは自分の光り方で描く（6.15.8 まで）。BLIP_PREDICT_UNLOCKED=1: ロックしていなくても予測した拍で光らせる（6.15.8 まで）。
        // BLIP_PREFADE=0: 拍の頭から色を切り替え始める（6.15.8 まで）
        RgbDeviceChannel.KeepFollowColor = Environment.GetEnvironmentVariable("BLIP_FOLLOWHOLD") != "0";
        BeatDriver.UnlockedPrediction = Environment.GetEnvironmentVariable("BLIP_PREDICT_UNLOCKED") == "1";
        BeatDriver.PreFade = Environment.GetEnvironmentVariable("BLIP_PREFADE") != "0";
        // DIRECT_OPTS: 直接書き込むグラボの描き方・送り方を選ぶ（DirectGpuOptions。付けなければ製品と同じ）
        DirectGpuOptions.Apply();
        // BLIP_OLDFX=1: ミックス（スペクトラムの小さいゾーン・遅いデバイス用）を 6.15.8 までの計算にする
        bool oldFx = Environment.GetEnvironmentVariable("BLIP_OLDFX") == "1";
        MixEffect.Smooth = !oldFx;
        try
        {
            foreach (var pass in passes)
            {
                LedSteady.Enabled = pass is not ("raw" or "nosteady");
                AttackHold.HoldSec = pass is "raw" or "nohold" ? 0 : AttackHold.DefaultHoldSec;
                try { SweepPass(pass, songs); }
                finally { LedSteady.Enabled = true; AttackHold.HoldSec = AttackHold.DefaultHoldSec; }
            }
        }
        finally
        {
            RgbDeviceChannel.KeepFollowColor = true;
            DirectGpuOptions.Reset();
            BeatDriver.UnlockedPrediction = false;
            BeatDriver.PreFade = true;
            MixEffect.Smooth = true;
        }
    }

    private static void SweepPass(string pass, List<(string Id, float[] Mid, float[] Side, int Fs)> songs)
    {
        string[] styles = EnvList("BLIP_STYLES", MusicStyles.All);
        string[] palettes = EnvList("BLIP_PALETTES", new[] { Palettes.Rainbow, Palettes.Neon, Palettes.Ocean, Palettes.Mono });
        string[] intens = EnvList("BLIP_INTENS", Intensities.All);
        var devNames = EnvList("BLIP_DEVICES", Devices().Select(d => d.Name));
        bool scenes = Environment.GetEnvironmentVariable("BLIP_SCENES") == "1";
        bool switching = Environment.GetEnvironmentVariable("BLIP_SWITCH") == "1";
        int examples = int.TryParse(Environment.GetEnvironmentVariable("BLIP_EXAMPLES"), out int ex) ? ex : 3;
        var byKey = new Dictionary<string, BlipTotals>();
        var exampleList = new List<(double Score, string Text)>();
        long sends = 0;
        var lagStats = new Dictionary<string, long[]>();
        var pnyStats = new Dictionary<string, (long Sends, long ToDirect, long ToStatic, long Writes, double Min, long Black)>();
        var followStats = new Dictionary<string, (long Frames, long Miss, long Sends, long MissSends)>();
        var sentExamples = new List<(double Score, string Text)>();
        var sendStats = new Dictionary<string, (long Sends, long Mid, double Min)>();
        double totalSec = 0;
        void Add(string key, BlipMeter m, int leds, double minutes)
        {
            if (!byKey.TryGetValue(key, out var t)) byKey[key] = t = new BlipTotals();
            m.AddTo(t);
            t.LedMinutes += leds * minutes;
        }
        foreach (var song in songs)
        {
            double seconds = song.Mid.Length / (double)song.Fs;
            totalSec += seconds;
            double minutes = Math.Max(0, seconds - WarmSec) / 60;
            foreach (var inten in intens)
            {
                var groups = BuildGroups(styles, palettes, inten, devNames, scenes, switching, examples);
                RunSong(song.Mid, song.Side, song.Fs, inten, groups, scenes);
                foreach (var g in groups)
                foreach (var L in g.Lanes)
                {
                    int leds = L.Ch.Device.LedCount;
                    string sc = L.Scene ? "+scene" : L.Switch ? "+switch" : "";
                    Add($"A|{L.Style}{sc}|{L.Dev}", L.Meter, leds, minutes);
                    if (L.Dev == "usb" || L.Dev == devNames[0]) Add($"B|{L.Style}{sc}|{inten}", L.Meter, leds, minutes);
                    if (L.Dev == "usb" || L.Dev == devNames[0]) Add($"C|{L.Style}{sc}|{L.Palette}", L.Meter, leds, minutes);
                    Add($"D|{L.Dev}", L.Meter, leds, minutes);
                    Add($"S|{song.Id}|{L.Dev}", L.Meter, leds, minutes);
                    sends += L.Sends;
                    if (L.PerTick)
                    {
                        if (!lagStats.TryGetValue($"{L.Style}|{inten}", out var ls)) lagStats[$"{L.Style}|{inten}"] = ls = new long[10];
                        for (int q = 0; q < 5; q++) ls[q] += L.StepLag[q];
                        ls[5] += L.Frames; ls[6] += L.LockedFrames; ls[7] += L.FlashNoStep; ls[8] += L.FlashOnsets; ls[9] += L.FlashMidFade;
                    }
                    if (!L.PerTick)
                    {
                        Add($"E|{L.Style}{sc}|{L.Dev}", L.SentMeter, leds, minutes);
                        if (L.Pny is not null)
                        {
                            if (!pnyStats.TryGetValue($"{L.Style}|{inten}", out var ps)) ps = (0, 0, 0, 0, 0, 0);
                            pnyStats[$"{L.Style}|{inten}"] = (ps.Sends + L.Sends, ps.ToDirect + L.PnyBus.ToDirect, ps.ToStatic + L.PnyBus.ToStatic, ps.Writes + L.PnyBus.Writes, ps.Min + minutes,
                                ps.Black + L.PnyBus.BlackEntries);
                        }
                        if (L.Follow)
                        {
                            string fk = $"{L.Style}|{inten}|{L.Dev}";
                            if (!followStats.TryGetValue(fk, out var fs)) fs = (0, 0, 0, 0);
                            followStats[fk] = (fs.Frames + (long)(minutes * 3600), fs.Miss + L.FollowMiss, fs.Sends + L.Sends, fs.MissSends + L.FollowMissSends);
                        }
                        foreach (var e in L.SentMeter.Examples) sentExamples.Add((e.Score, $"{song.Id} {L.Style}{sc}/{L.Palette}/{inten}/{L.Dev}: {e.Text}"));
                        if (!sendStats.TryGetValue(L.Dev, out var ss)) ss = (0, 0, 0);
                        sendStats[L.Dev] = (ss.Sends + L.Sends, ss.Mid + L.MidFadeSends, ss.Min + minutes);
                    }
                    foreach (var e in L.Meter.Examples) exampleList.Add((e.Score, $"{song.Id} {L.Style}{sc}/{L.Palette}/{inten}/{L.Dev}: {e.Text}"));
                }
            }
        }
        T.Info($"===== pass {pass}: {songs.Count} songs, {totalSec:F0} s, thresholds rel {BlipMeter.Rel} abs {BlipMeter.Abs * 255:F1}/255 off {BlipMeter.OffThr}");
        void Table(string prefix, string title)
        {
            T.Info($"--- {title}");
            foreach (var kv in byKey.Where(k => k.Key.StartsWith(prefix + "|")).OrderBy(k => k.Key, StringComparer.Ordinal))
                T.Info($"{kv.Key[(prefix.Length + 1)..],-34} {kv.Value.Format()}");
        }
        Table("D", "by device (all styles, palettes, intensities)");
        Table("A", "by style x device");
        Table("B", $"by style x intensity ({devNames[0]})");
        Table("C", $"by style x palette ({devNames[0]})");
        Table("S", "by song x device");
        T.Info("--- fast device: colour steps by frames after the beat flash onset (0 = same frame; 4+ = no flash within 3 frames), locked share, flashes without a colour step");
        foreach (var kv in lagStats.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var a = kv.Value;
            long steps = a[0] + a[1] + a[2] + a[3] + a[4];
            if (steps == 0 && a[7] == 0) continue;
            T.Info($"{kv.Key,-20} steps {steps,5}  lag0 {a[0],4} lag1 {a[1],4} lag2 {a[2],4} lag3 {a[3],4} other {a[4],4}  locked {100.0 * a[6] / Math.Max(1, a[5]),3:F0}%  flash-without-step {a[7],4}  flash-mid-fade {a[9],4} of {a[8],5}");
        }
        if (pnyStats.Count > 0)
        {
            T.Info("--- direct-write GPU controller (PnyEpicXEncoder on the sent frames): zone switches per minute and I2C writes per send");
            foreach (var kv in pnyStats.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var v = kv.Value;
                double m = Math.Max(1e-9, v.Min);
                T.Info($"{kv.Key,-20} sends/min {v.Sends / m,6:F0}  static->direct/min {v.ToDirect / m,6:F1}  direct->static/min {v.ToStatic / m,6:F1}  writes/send {(double)v.Writes / Math.Max(1, v.Sends),5:F1}  black-LED0 entries/min {v.Black / m,5:F1}");
            }
        }
        if (followStats.Count > 0)
        {
            T.Info("--- followers (MatchSlowDevices): frames where the leader was too dark (SceneColor empty) and the device drew its own effect instead, and sends of such frames");
            foreach (var kv in followStats.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var v = kv.Value;
                if (v.Miss == 0) continue;
                T.Info($"{kv.Key,-26} own-render frames {100.0 * v.Miss / Math.Max(1, v.Frames),5:F1}%  sends of own-render frames {v.MissSends,5} of {v.Sends,6}");
            }
        }
        Table("E", "slow devices, counted on the sequence of sends (one send = one sample)");
        T.Info("--- slow devices: sends per second, and share of sends made while a colour step was still crossfading");
        foreach (var kv in sendStats.OrderBy(k => k.Key, StringComparer.Ordinal))
            T.Info($"{kv.Key,-6} {kv.Value.Sends / Math.Max(1e-9, kv.Value.Min * 60),6:F2} sends/s per lane  mid-fade {100.0 * kv.Value.Mid / Math.Max(1, kv.Value.Sends),5:F1}%");
        T.Info("--- worst colour-change examples (Rgb sent, Y = luminance x255; first = before, last = after)");
        foreach (var e in exampleList.OrderByDescending(x => x.Score).Take(Math.Max(10, examples * 6))) T.Info(e.Text);
        T.Info("--- worst examples on slow devices, counted per send (each [] is one send)");
        foreach (var e in sentExamples.OrderByDescending(x => x.Score).Take(Math.Max(10, examples * 6))) T.Info(e.Text);
    }

    private static List<Group> BuildGroups(string[] styles, string[] palettes, string inten, string[] devNames, bool scenes, bool switching, int examples)
    {
        var groups = new List<Group>();
        var devs = Devices().Where(d => devNames.Contains(d.Name)).ToArray();
        void AddGroup(string style, string palette, bool scene, bool sw)
        {
            var g = new Group { Intensity = inten };
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style, Gamma = true, MusicScenes = scene };
            MusicTuning.ApplyIntensity(cfg.Music, inten);
            cfg.Music.Intensity = inten;
            cfg.Music.Palette = palette;
            var music = MusicTuning.Resolve(cfg);
            Lane? leader = null;
            foreach (var d in devs)
            {
                var ch = new RgbDeviceChannel(d.Device, T0) { SharedColor = g.Shared };
                ch.Configure(cfg, music, new RgbDeviceSettings { Name = d.Device.Name }, false);
                var lane = new Lane
                {
                    Style = style, Palette = palette, Intensity = inten, Dev = d.Name, Scene = scene, Switch = sw, Cfg = cfg,
                    Ch = ch, Meter = new BlipMeter(d.Device.LedCount) { MaxExamples = examples }, PerTick = d.PerTick, Follow = d.Follow,
                    Shown = new Rgb[d.Device.LedCount], SentMeter = new BlipMeter(d.Device.LedCount) { MaxExamples = examples },
                };
                if (d.Device.DirectGpu && d.Device.LedCount == PnyEpicXEncoder.LedCount) lane.Pny = new PnyEpicXEncoder();
                if (d.Name == "usb") leader = lane;
                g.Lanes.Add(lane);
            }
            foreach (var L in g.Lanes) if (L.Follow) L.Leader = leader;
            groups.Add(g);
        }
        foreach (var style in styles)
        foreach (var palette in palettes)
            AddGroup(style, palette, false, false);
        if (scenes)
            foreach (var style in styles)
                AddGroup(style, Palettes.Neon, true, false);
        if (switching)
            AddGroup(styles[0], palettes[0], false, true);
        return groups;
    }

    /// <summary>途中で切り替える組: 4.3 秒ごとにパレット、6.1 秒ごとに光り方を次へ</summary>
    private static void MaybeSwitch(Lane L, double songT)
    {
        if (!L.Switch) return;
        var pals = new[] { Palettes.Rainbow, Palettes.Neon, Palettes.Ocean, Palettes.Sunset };
        var sty = new[] { MusicStyles.BeatHue, MusicStyles.Meter, MusicStyles.Spectrum, MusicStyles.Ensemble };
        string p = pals[(int)(songT / 4.3) % pals.Length];
        string st = sty[(int)(songT / 6.1) % sty.Length];
        if (L.Cfg.Music.Palette == p && L.Cfg.MusicStyle == st) return;
        L.Cfg.Music.Palette = p;
        L.Cfg.MusicStyle = st;
        L.Ch.Configure(L.Cfg, MusicTuning.Resolve(L.Cfg), L.Ch.Input.Device, false);
    }

    private static void RunSong(float[] mid, float[] side, int fs, string inten, List<Group> groups, bool scenes)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, inten);
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        int chunk = fs / 100;
        double nextFrame = T0;
        MusicScene? lastScene = null;
        for (int pos = 0; pos < mid.Length; pos += chunk)
        {
            int n = Math.Min(chunk, mid.Length - pos);
            if (side.Length >= pos + n) engine.Push(mid.AsSpan(pos, n), side.AsSpan(pos, n), T0 + pos / (double)fs);
            else engine.Push(mid.AsSpan(pos, n), T0 + pos / (double)fs);
            double tAudio = T0 + (pos + n) / (double)fs;
            while (nextFrame <= tAudio)
            {
                double t = nextFrame;
                double songT = t - T0;
                bool count = songT >= WarmSec;
                var scene = scenes ? SceneAt(songT, ref lastScene) : MusicScene.None;
                foreach (var g in groups)
                {
                    // 1 回目は合わせる元（追いかけないデバイス）、2 回目は追いかけるデバイス（RgbSync.RenderTick と同じ順）
                    for (int p = 0; p < 2; p++)
                    foreach (var L in g.Lanes)
                    {
                        if (L.Follow != (p == 1)) continue;
                        MaybeSwitch(L, songT);
                        var ch = L.Ch;
                        ch.Input.Scene = L.Scene ? scene : MusicScene.None;
                        // 追いかけるデバイス: アプリと同じ RgbDeviceChannel.SetFollow で合わせる色を入れる
                        // （BLIP_FOLLOWHOLD=0 なら、合わせる元が暗いコマは自分の光り方で描く 6.15.8 までの動き）
                        var sc = new SceneColor();
                        if (L.Follow && L.Leader is { } lead) sc.Add(lead.Ch.Renderer.LastLight);
                        ch.SetFollow(L.Follow, L.Leader is not null, sc, L.Cfg.Brightness);
                        if (L.Follow)
                        {
                            L.MissNow = !sc.TryGet(out _, out _);
                            if (L.MissNow && count) L.FollowMiss++;
                        }
                        ch.Prepare(t, Tick, engine.Features, engine.Events, BeatFor(engine), null, false, false);
                        if (!ch.Render()) continue;
                        var reason = ch.ShouldSend(t, Tick, true);
                        if (reason != SendReason.None)
                        {
                            ch.MarkSent(t, Tick, reason, 1);
                            Array.Copy(ch.Frame, L.Shown, L.Shown.Length);
                            L.HasShown = true;
                            if (count)
                            {
                                L.Sends++;
                                if (L.Follow && L.MissNow) L.FollowMissSends++;
                                // 色替え（共有の色のクロスフェード）の途中の色を送った
                                double since = ch.Input.TEval - g.Shared.LastStepT;
                                if (since >= 0 && since < ch.Renderer.ColorStepSettleSec) L.MidFadeSends++;
                            }
                            if (!L.PerTick)
                            {
                                L.SentMeter.Now = songT;
                                L.SentMeter.Feed(L.Shown, count);
                                if (L.Pny is { } enc)
                                {
                                    int w = 0;
                                    L.PnyBus.Counting = count;
                                    enc.Apply(L.Shown, false, L.PnyBus, ref w);
                                }
                            }
                        }
                        if (L.PerTick) TrackSteps(L, g, count);
                        // 表示されている色（送った色を次に送るまで保つ）を 1 コマごとに見る
                        if (L.HasShown)
                        {
                            L.Meter.Now = songT;
                            L.Meter.Feed(L.Shown, count);
                        }
                    }
                }
                nextFrame += Tick;
            }
        }
    }

    // 合成の曲の確かめ（TestColorChangeBlipsAreRare）の最中
    private static bool _synthetic;
    /// <summary>BLIP_UNLOCK=1 なら、テンポは使うがロックしていない状態にする（録音でテンポ追跡がほぼずっと「ロックなし」だったときの再現）。合成の曲で確かめるとき（TestColorChangeBlipsAreRare）は使わない（調べる用の値が、いつも動く確かめに漏れないように）</summary>
    private static BeatState BeatFor(AudioEngine e) =>
        !_synthetic && Environment.GetEnvironmentVariable("BLIP_UNLOCK") == "1" && e.Beat.Locked ? e.Beat with { Locked = false } : e.Beat;

    /// <summary>
    /// ビートの光（Fl）が 0.3 以上はね上がったコマと、共有の色が進んだコマのずれを数える（0 = 同じコマ。拍の前に切り替え始めた色は
    /// 光より前に進むので other に入る）。光ってから色が進むまで 1 コマ以上あると、光の最初のコマが前の色で光り、そのあと色だけが変わる。
    /// 光の立ち上がりのコマで、色のクロスフェードがまだ 9 割まで進んでいなかった回数も数える（flash-mid-fade。少ないほど、光が新しい色で光る）
    /// </summary>
    private static void TrackSteps(Lane L, Group g, bool count)
    {
        var c = L.Ch.Renderer.Context;
        L.FrameNo++;
        float fl = c.Fl;
        bool onset = fl - L.PrevFl > 0.3f;
        L.PrevFl = fl;
        bool stepped = g.Shared.LastStepT != L.PrevStepT;
        L.PrevStepT = g.Shared.LastStepT;
        if (!count) { if (onset) { L.LastFlashFrame = L.FrameNo; L.FlashStepped = stepped; } return; }
        L.Frames++;
        if (c.Locked) L.LockedFrames++;
        if (onset)
        {
            L.FlashOnsets++;
            if (c.Color.ProgressAt(c.T) < 0.9f) L.FlashMidFade++;
            // 前の光で色が進まなかった
            if (!L.FlashStepped && L.FrameNo - L.LastFlashFrame < 60) L.FlashNoStep++;
            L.LastFlashFrame = L.FrameNo;
            L.FlashStepped = false;
        }
        if (stepped)
        {
            long lag = L.FrameNo - L.LastFlashFrame;
            L.StepLag[lag is >= 0 and < 4 && !L.FlashStepped ? lag : 4]++;
            L.FlashStepped = true;
        }
    }

    // ===== 1 本だけを 1 コマずつ出す（BLIP_TRACE=光り方:パレット:強さ:デバイス:曲:始め秒:終わり秒[:LED]）

    /// <summary>
    /// 合成した曲（曲 = synth118 / synth128 / breaks124、または BLIP_WAV の song_NN）の 1 本を、1 コマずつ出す:
    /// 送った LED の色・輝度、ビートの光 Fl・合図・テンポのロック・共有の色を進めた時刻・このコマの出来事
    /// </summary>
    public static void TestColorBlipTrace()
    {
        if (Environment.GetEnvironmentVariable("BLIP_TRACE") is not { Length: > 0 } spec) { T.Skip("BLIP_TRACE not set"); return; }
        var p = spec.Split(':');
        if (p.Length < 7) { T.Skip("BLIP_TRACE=style:palette:intensity:device:song:from:to[:led]"); return; }
        var inv = CultureInfo.InvariantCulture;
        string style = p[0], palette = p[1], inten = p[2], devName = p[3], songId = p[4];
        double from = double.Parse(p[5], inv), to = double.Parse(p[6], inv);
        int led = p.Length > 7 ? int.Parse(p[7], inv) : 0;
        // 合成した曲は長さで中身が変わるので、TestColorBlipSweep と同じ BLIP_SECS（既定 30 秒）で作る
        var songs = SynthSongs(Math.Max(to + 1, double.TryParse(Environment.GetEnvironmentVariable("BLIP_SECS"), NumberStyles.Float, inv, out double ss) ? ss : 30));
        if (Environment.GetEnvironmentVariable("BLIP_WAV") is { Length: > 0 } dir && Directory.Exists(dir))
        {
            int idx = 0;
            foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x, StringComparer.Ordinal))
            {
                idx++;
                string name = Path.GetFileNameWithoutExtension(f);
                string id = System.Text.RegularExpressions.Regex.IsMatch(name, "^song_[0-9]+$") ? name : $"song_{idx:00}";
                if (id != songId) continue;
                var (mid, side, fs) = ReadWavStereo(f);
                songs.Add((id, mid, side, fs));
            }
        }
        var song = songs.FirstOrDefault(s => s.Id == songId);
        if (song.Mid is null) { T.Skip("song not found"); return; }
        var groups = BuildGroups(new[] { style }, new[] { palette }, inten, Devices().Select(d => d.Name).ToArray(), false, false, 0);
        var g = groups[0];
        var lane = g.Lanes.First(l => l.Dev == devName);
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, inten);
        var engine = new AudioEngine(song.Fs) { Settings = MusicTuning.EngineSettings(cfg) };
        int chunk = song.Fs / 100;
        double nextFrame = T0;
        var prevShown = new Rgb[lane.Shown.Length];
        for (int pos = 0; pos < song.Mid.Length && nextFrame - T0 <= to; pos += chunk)
        {
            int n = Math.Min(chunk, song.Mid.Length - pos);
            if (song.Side.Length >= pos + n) engine.Push(song.Mid.AsSpan(pos, n), song.Side.AsSpan(pos, n), T0 + pos / (double)song.Fs);
            else engine.Push(song.Mid.AsSpan(pos, n), T0 + pos / (double)song.Fs);
            double tAudio = T0 + (pos + n) / (double)song.Fs;
            while (nextFrame <= tAudio)
            {
                double t = nextFrame, songT = t - T0;
                for (int pass = 0; pass < 2; pass++)
                foreach (var L in g.Lanes)
                {
                    if (L.Follow != (pass == 1)) continue;
                    var ch = L.Ch;
                    var sc = new SceneColor();
                    if (L.Follow && L.Leader is { } lead) sc.Add(lead.Ch.Renderer.LastLight);
                    ch.SetFollow(L.Follow, L.Leader is not null, sc, L.Cfg.Brightness);
                    ch.Prepare(t, Tick, engine.Features, engine.Events, BeatFor(engine), null, false, false);
                    if (!ch.Render()) continue;
                    var reason = ch.ShouldSend(t, Tick, true);
                    bool sent = reason != SendReason.None;
                    if (sent) { ch.MarkSent(t, Tick, reason, 1); Array.Copy(ch.Frame, L.Shown, L.Shown.Length); L.HasShown = true; }
                    if (!ReferenceEquals(L, lane) || songT < from) continue;
                    var c = ch.Renderer.Context;
                    var o = L.Shown[Math.Min(led, L.Shown.Length - 1)];
                    var lt = ch.Renderer.LastLight[Math.Min(led, L.Shown.Length - 1)];
                    string ev = string.Join(",", ch.Input.Events.Select(e => $"{e.Kind}{(e.T - T0):F3}"));
                    T.Info($"{songT,7:F3} {(sent ? reason.ToString()[..3] : "   ")} out {o.R,3},{o.G,3},{o.B,3} Y{BlipMeter.Luma(o) * 255,4:F0}  light {lt.R:F3},{lt.G:F3},{lt.B:F3}  Fl {c.Fl:F2} conf {c.Conf:F2} {(c.Locked ? "L" : "-")}{(c.BeatCrossed ? "X" : "-")}{(c.Trigger ? "T" : "-")} step {(g.Shared.LastStepT - T0):F3} {ev}");
                }
                nextFrame += Tick;
            }
        }
    }

    // ===== 環境モード（音楽連動以外）と、モードの切り替えのクロスフェード

    public static void TestColorBlipAmbient()
    {
        if (Environment.GetEnvironmentVariable("BLIP_DIAG") != "1") { T.Skip("BLIP_DIAG=1 not set"); return; }
        ReadThresholds();
        string[] modes = { RgbModes.Rainbow, RgbModes.Breathing, RgbModes.Lightning, RgbModes.Wave, RgbModes.Birdsong, RgbModes.Fire, RgbModes.Aurora, RgbModes.Meteor, RgbModes.Heartbeat, RgbModes.Temperature, "switch" };
        double secs = 40;
        foreach (var d in Devices().Where(x => !x.Follow))
        {
            foreach (var mode in modes)
            {
                var cfg = new RgbConfig { Mode = mode == "switch" ? RgbModes.Rainbow : mode, Gamma = true };
                var ch = new RgbDeviceChannel(d.Device, T0);
                ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = d.Device.Name }, false);
                var meter = new BlipMeter(d.Device.LedCount) { MaxExamples = 2 };
                var shown = new Rgb[d.Device.LedCount];
                bool has = false;
                string[] cycle = { RgbModes.Rainbow, RgbModes.Static, RgbModes.Aurora, RgbModes.Breathing, RgbModes.Wave, RgbModes.Temperature };
                for (double t = T0; t < T0 + secs; t += Tick)
                {
                    double songT = t - T0;
                    if (mode == "switch")
                    {
                        string m = cycle[(int)(songT / 5) % cycle.Length];
                        if (cfg.Mode != m) { cfg.Mode = m; ch.Configure(cfg, MusicTuning.Resolve(cfg), ch.Input.Device, false); }
                    }
                    // 温度: 35〜75 ℃ を 20 秒で往復（1 秒ごとの値）
                    double temp = 55 + 20 * Math.Sin(2 * Math.PI * Math.Floor(songT) / 20);
                    ch.Prepare(t, Tick, null, null, BeatState.None, temp, false, false);
                    if (!ch.Render()) continue;
                    var reason = ch.ShouldSend(t, Tick, cfg.Mode is not (RgbModes.Static or RgbModes.Temperature or RgbModes.RainbowStatic));
                    bool count = songT >= WarmSec;
                    meter.Now = songT;
                    if (reason != SendReason.None)
                    {
                        ch.MarkSent(t, Tick, reason, 1);
                        Array.Copy(ch.Frame, shown, shown.Length);
                        has = true;
                        if (!d.PerTick) meter.Feed(shown, count);
                    }
                    if (d.PerTick && has) meter.Feed(shown, count);
                }
                var tot = new BlipTotals { LedMinutes = d.Device.LedCount * (secs - WarmSec) / 60 };
                meter.AddTo(tot);
                T.Info($"{mode,-12} {d.Name,-5} {tot.Format()}");
                foreach (var e in meter.Examples) T.Info($"      {e.Text}");
            }
        }
    }
}
