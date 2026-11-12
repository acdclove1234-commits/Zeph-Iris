// ==================================================
// RgbEffects.cs - LED の光り方 (温度・レインボー・呼吸・固定色) の 1 コマ分の色を計算する
//                 (音楽連動の光り方は Core/Rgb/Effects、組み立ては Core/Rgb/DeviceRenderer.cs)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;

namespace ProFanController;

public static class RgbModes
{
    public const string Temperature = "Temperature";
    public const string Rainbow = "Rainbow";
    /// <summary>虹色が並んだまま止まっている</summary>
    public const string RainbowStatic = "RainbowStatic";
    public const string Breathing = "Breathing";
    public const string Static = "Static";
    public const string Music = "Music";

    // 動きのある光り方（AmbientEffects が描く）
    public const string Lightning = "Lightning";
    public const string Wave = "Wave";
    public const string Birdsong = "Birdsong";
    public const string Fire = "Fire";
    public const string Aurora = "Aurora";
    public const string Meteor = "Meteor";
    public const string Heartbeat = "Heartbeat";

    /// <summary>動きのある光り方（雷・波など）</summary>
    public static readonly string[] Ambient = { Lightning, Wave, Birdsong, Fire, Aurora, Meteor, Heartbeat };

    /// <summary>全体の「光り方」で選べるもの（画面・トレイのメニューの並び順）</summary>
    public static readonly string[] All =
        { Temperature, Rainbow, RainbowStatic, Breathing, Static, Lightning, Wave, Birdsong, Fire, Aurora, Meteor, Heartbeat, Music };

    public static bool IsKnown(string? mode) => mode is not null && Array.IndexOf(All, mode) >= 0;

    /// <summary>「色」で選んだ色を使う光り方</summary>
    public static bool UsesColor(string mode) => mode is Static or Breathing or Wave or Heartbeat;

    /// <summary>「速さ」を使う光り方</summary>
    public static bool UsesSpeed(string mode) => mode is Rainbow or Breathing || Array.IndexOf(Ambient, mode) >= 0;

    /// <summary>光り方の表示名（画面・トレイ・デバイスごとの選択で同じ言葉）</summary>
    public static string Label(string mode) => mode switch
    {
        Temperature => L("温度"),
        Rainbow => L("レインボー（流れる）"),
        RainbowStatic => L("レインボー（固定）"),
        Breathing => L("呼吸"),
        Static => L("固定色"),
        Music => L("音楽連動"),
        Lightning => L("雷"),
        Wave => L("波"),
        Birdsong => L("さえずり"),
        Fire => L("炎"),
        Aurora => L("オーロラ"),
        Meteor => L("流れ星"),
        Heartbeat => L("心拍"),
        _ => mode,
    };
}

/// <summary>
/// デバイス・ゾーンごとの光り方（RgbDeviceSettings.LightMode / RgbZoneSettings.LightMode の値）。
/// 空は「全体（ゾーンならデバイス）と同じ」。音楽連動は全体の設定でだけ選べる（音の取り込みは全体で 1 つのため）
/// </summary>
public static class RgbLightModes
{
    public const string Same = "";
    public const string Off = "Off";
    /// <summary>選べる光り方（Same のほか）</summary>
    public static readonly string[] Overrides =
        { RgbModes.Static, RgbModes.Rainbow, RgbModes.RainbowStatic, RgbModes.Breathing, RgbModes.Temperature,
          RgbModes.Lightning, RgbModes.Wave, RgbModes.Birdsong, RgbModes.Fire, RgbModes.Aurora, RgbModes.Meteor, RgbModes.Heartbeat, Off };

    public static string Normalize(string? mode) => mode is not null && Array.IndexOf(Overrides, mode) >= 0 ? mode : Same;

    /// <summary>"#RRGGBB" 以外は空（全体の色を使う）</summary>
    public static string NormalizeColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? hex.ToUpperInvariant() : "";

    /// <summary>色を使う光り方か（固定色・呼吸）</summary>
    public static bool UsesColor(string mode) => RgbModes.UsesColor(mode);

    /// <summary>動き続ける光り方か（毎コマ送る必要がある）</summary>
    public static bool IsAnimated(string mode) => mode is RgbModes.Rainbow or RgbModes.Breathing || AmbientEffects.Handles(mode);

    /// <summary>設定のどこかに、動き続ける光り方の上書きがあるか（全体が止まった光り方でも、毎コマ描いて送るため）</summary>
    public static bool AnyAnimated(RgbConfig cfg)
    {
        foreach (var d in cfg.DeviceSettings)
        {
            if (d is null) continue;
            if (IsAnimated(d.LightMode)) return true;
            if (d.Zones is null) continue;
            foreach (var z in d.Zones)
                if (z is not null && IsAnimated(z.LightMode)) return true;
        }
        return false;
    }
}

/// <summary>
/// デバイス 1 台の LED を、光り方の上書きごとのまとまりに分けたもの（全体と同じ光り方の LED は含まない）。
/// DeviceRenderer が設定の変わったときだけ作り直し、毎コマその範囲だけ描き直す
/// </summary>
public sealed class RgbLightPlan
{
    public sealed record Segment(int Start, int Length, string Mode, string Color);

    public IReadOnlyList<Segment> Segments { get; }
    /// <summary>デバイス全部の LED が上書きされている（全体の光り方を描く必要がない）</summary>
    public bool CoversAll { get; }
    /// <summary>デバイス全体の光り方（ゾーンを分けない）。上書きがなければ空</summary>
    public string DeviceMode { get; }

    private RgbLightPlan(List<Segment> segments, bool coversAll, string deviceMode)
    {
        Segments = segments;
        CoversAll = coversAll;
        DeviceMode = deviceMode;
    }

    public static readonly RgbLightPlan None = new(new List<Segment>(), false, "");

    /// <param name="ledCount">デバイスの LED の数</param>
    /// <param name="zones">ゾーンごとの LED の数（なければ null。デバイス全体を 1 つとして扱う）</param>
    /// <param name="zoneSettings">k 番目のゾーンの設定（ファンごとに分けたゾーンは、ゾーンの設定と合わせたもの。RgbZoneSplit）</param>
    public static RgbLightPlan Build(RgbDeviceSettings s, int ledCount, IReadOnlyList<int>? zones, Func<int, RgbZoneSettings?> zoneSettings)
    {
        string devMode = RgbLightModes.Normalize(s.LightMode);
        string devColor = RgbLightModes.NormalizeColor(s.LightColor);
        var list = new List<Segment>();
        int covered = 0;
        void Add(int start, int len, string mode, string color)
        {
            if (len <= 0 || mode.Length == 0) return;
            len = Math.Min(len, ledCount - start);
            if (len <= 0) return;
            // 隣のまとまりと同じ光り方・色ならつなげる（虹の並びがゾーンごとに切れないように）
            if (list.Count > 0 && list[^1] is var last && last.Start + last.Length == start && last.Mode == mode && last.Color == color)
                list[^1] = last with { Length = last.Length + len };
            else list.Add(new Segment(start, len, mode, color));
            covered += len;
        }
        if (zones is null || zones.Count == 0 || zones.Sum() != ledCount)
        {
            Add(0, ledCount, devMode, devColor);
        }
        else
        {
            int start = 0;
            for (int k = 0; k < zones.Count; k++)
            {
                var z = zoneSettings(k);
                string zm = RgbLightModes.Normalize(z?.LightMode);
                string zc = RgbLightModes.NormalizeColor(z?.LightColor);
                // ゾーンの光り方が空ならデバイスの光り方。ゾーンの色が空ならデバイスの色
                if (zm.Length == 0) Add(start, zones[k], devMode, zc.Length > 0 ? zc : devColor);
                else Add(start, zones[k], zm, zc.Length > 0 ? zc : devColor);
                start += zones[k];
            }
        }
        return list.Count == 0 ? None : new RgbLightPlan(list, covered >= ledCount, devMode);
    }
}

/// <summary>音楽連動の光り方（RgbConfig.MusicStyle の値）</summary>
public static class MusicStyles
{
    /// <summary>ビートのたびに次の色へ切り替わり、ぱっと光って消えていく</summary>
    public const string BeatHue = "BeatHue";
    /// <summary>低音に合わせて脈打ち、ふわっと長く残光が残る。色はビートでは変わらず、ゆっくり移り変わる</summary>
    public const string Pulse = "Pulse";
    /// <summary>低音・中音・高音の強さで色が混ざる（濁らないよう色相で混ぜる）</summary>
    public const string Mix = "Mix";
    /// <summary>音量メーター（重力で落ちてくるピークの点つき）</summary>
    public const string Meter = "Meter";
    /// <summary>スペクトラム: LED の並びに低音〜高音の帯を並べる</summary>
    public const string Spectrum = "Spectrum";
    /// <summary>エナジー: 低音・中音・高音の帯が、それぞれの強さの分だけ伸びる</summary>
    public const string Energy = "Energy";
    /// <summary>流れる波: 今の音の色が端から流れていく（ウォーターフォール）</summary>
    public const string Scroll = "Scroll";
    /// <summary>波紋: キックのたびに中央から光の輪が広がる</summary>
    public const string Ripple = "Ripple";
    /// <summary>きらめき: ハイハットやスネアで、ランダムな LED がきらっと光る</summary>
    public const string Sparkle = "Sparkle";
    /// <summary>テンポ追従: ビートに合わせて光の帯が走る</summary>
    public const string Chase = "Chase";
    /// <summary>ムード: 曲の雰囲気（音の明るさ）で色がゆっくり変わる。点滅しない</summary>
    public const string Mood = "Mood";
    /// <summary>ストロボ: ビートで白く強く点滅する（「激しい点滅を抑える」をオフにしたときだけ）</summary>
    public const string Strobe = "Strobe";
    /// <summary>パーツごとの役割: ゾーンごとに、低音・中音・高音・音全体のどれかに反応する</summary>
    public const string Role = "Role";

    public static readonly string[] All = { BeatHue, Pulse, Mix, Meter, Spectrum, Energy, Scroll, Ripple, Sparkle, Role, Chase, Mood, Strobe };
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    public Rgb Scale(double k)
    {
        k = Math.Clamp(k, 0, 1);
        return new((byte)Math.Round(R * k), (byte)Math.Round(G * k), (byte)Math.Round(B * k));
    }

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>色相（0〜360）・彩度・明度から色を作る</summary>
    public static Rgb FromHsv(double h, double s = 1, double v = 1)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>"#7C5CFF" のような文字から色を作る（読めなければ既定の紫）</summary>
    public static Rgb Parse(string? hex)
    {
        string s = (hex ?? "").Trim().TrimStart('#');
        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)
            ? new((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new(0x7C, 0x5C, 0xFF);
    }

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// 音楽連動以外の光り方（温度・レインボー・呼吸・固定色）の 1 コマ分の色を計算する（純粋な計算なのでテストしやすい）。
/// 音楽連動は DeviceRenderer（Core/Rgb）が描く。どちらも最後は OutputStage（ガンマ補正・明るさ・0〜255 への変換）を通る
/// </summary>
public static class RgbEffects
{
    /// <summary>緊急時の赤い点滅の色（1 秒に 1 回: 0.5 秒ずつ明るい赤と暗い赤。明るさ・ガンマ補正はかけない）</summary>
    public static readonly Rgb EmergencyOn = new(0xFF, 0, 0);
    public static readonly Rgb EmergencyOff = new(0x20, 0, 0);

    /// <summary>温度の色・アニメーションなど、時間で変わるモードか（変わらないモードは 1 秒に 1 回でよい）</summary>
    public static bool IsAnimated(string mode) => mode is RgbModes.Rainbow or RgbModes.Breathing or RgbModes.Music || AmbientEffects.Handles(mode);
    // RainbowStatic は止まっているので、温度・固定色と同じく変化があったときだけ送る

    /// <summary>緊急時の赤い点滅の、時刻 t（秒）の色</summary>
    public static Rgb EmergencyColor(double t) => (long)Math.Floor(t * 2) % 2 == 0 ? EmergencyOn : EmergencyOff;

    /// <param name="t">経過秒数（アニメーションの位置）</param>
    /// <param name="ledCount">そのデバイスの LED の数</param>
    /// <param name="temperature">温度モード用の温度（なければ null）</param>
    /// <param name="zones">ゾーンごとの LED の数（分からなければ null。デバイス全体を 1 つのまとまりとして扱う）</param>
    public static Rgb[] Frame(RgbConfig cfg, double t, int ledCount, double? temperature, bool emergency,
        IReadOnlyList<int>? zones = null)
    {
        var colors = new Rgb[Math.Max(0, ledCount)];
        if (colors.Length == 0) return colors;

        // 緊急全開のときは、どのモードでも赤く点滅（安全のお知らせが最優先）
        if (emergency && cfg.BlinkOnEmergency)
        {
            Array.Fill(colors, EmergencyColor(t));
            return colors;
        }

        var pos = Positions(colors.Length, zones);
        var light = new LinColor[colors.Length];
        FillLinear(cfg.Mode, cfg, Rgb.Parse(cfg.StaticColor), t, temperature, pos, light);
        OutputStage.Quantize(light, colors, (float)(Math.Clamp(cfg.Brightness, 0, 100) / 100));
        return colors;
    }

    /// <summary>
    /// 音楽連動以外の光り方の色を、LED に送る「光の量」（ガンマ補正済み、明るさを掛ける前）で dst に入れる。
    /// 毎コマ呼んでも new しない（pos は Positions で 1 回だけ作っておく。baseColor は Rgb.Parse(cfg.StaticColor) を覚えておく）
    /// </summary>
    /// <param name="mode">光り方（RgbModes。cfg.Mode と違う光り方も描ける: 音が止まったときの呼吸・固定色など）</param>
    public static void FillLinear(string mode, RgbConfig cfg, Rgb baseColor, double t, double? temperature,
        ReadOnlySpan<double> pos, Span<LinColor> dst)
    {
        int n = Math.Min(pos.Length, dst.Length);
        bool gamma = cfg.Gamma;
        double speed = Math.Clamp(cfg.Speed, 0.1, 10);
        switch (mode)
        {
            case RgbModes.Rainbow:
                // 1 周 30 秒（速さ 1 のとき）で、LED の並びに沿って虹が流れる。速さ 3 で 10 秒
                for (int i = 0; i < n; i++) dst[i] = OutputStage.Lin(Rgb.FromHsv(t * 12 * speed + pos[i] * 360), gamma);
                break;

            case RgbModes.RainbowStatic:
                // 虹色を LED の並びに沿って 1 周分並べたまま、動かさない
                for (int i = 0; i < n; i++) dst[i] = OutputStage.Lin(Rgb.FromHsv(pos[i] * 360), gamma);
                break;

            case RgbModes.Breathing:
            {
                // 1 呼吸 6 秒（速さ 1 のとき）。真っ暗にはせず 1 割は残す。
                // 色を 0〜255 に丸めずに小数のまま暗くするので、暗いところでも段々にならない
                float k = (float)(0.1 + 0.9 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t * speed / 6)));
                var c = new LinColor(
                    OutputStage.Lin(baseColor.R / 255f * k, gamma),
                    OutputStage.Lin(baseColor.G / 255f * k, gamma),
                    OutputStage.Lin(baseColor.B / 255f * k, gamma));
                dst[..n].Fill(c);
                break;
            }

            case RgbModes.Static:
                dst[..n].Fill(OutputStage.Lin(baseColor, gamma));
                break;

            case var m when AmbientEffects.Handles(m):
                AmbientEffects.Fill(m, cfg, baseColor, t, pos, dst);
                break;

            case RgbModes.Music:
                // 音楽連動は DeviceRenderer（Core/Rgb）が描く。ここでは選んだ色を暗めに出すだけ
                dst[..n].Fill(OutputStage.Lin(baseColor.Scale(0.3), gamma));
                break;

            default:
            {
                var (r, g, b) = temperature is double temp ? RgbSync.ColorFor(temp, cfg.LowTemp, cfg.HighTemp) : ((byte)0x5B, (byte)0x61, (byte)0x78);
                dst[..n].Fill(OutputStage.Lin(new Rgb(r, g, b), gamma));
                break;
            }
        }
    }

    /// <summary>
    /// LED ごとの「まとまりの中での位置」（0〜1）。虹色や音量メーターは、デバイス全体ではなくゾーンごとに並べる
    /// （マザーボードは、何もつないでいない ARGB 端子の分の LED も数に入っているため、全体で割ると基板の LED が 1 色になる）。
    /// LED が 1〜2 個しかない小さなゾーンが続くときは、まとめて 1 つのまとまりにする（ファンごとに 1 個、など）
    /// </summary>
    public static double[] Positions(int count, IReadOnlyList<int>? zones)
    {
        var pos = new double[count];
        var segments = new List<(int Start, int Length)>();
        Segments(count, zones, segments);
        foreach (var (start, length) in segments)
        {
            for (int i = 0; i < length; i++) pos[start + i] = length <= 1 ? 0 : (double)i / length;
        }
        return pos;
    }

    /// <summary>
    /// LED のまとまり（開始位置・数）を dst に入れる。ゾーンの LED の数の合計が count と合わなければ、全体を 1 つにする。
    /// LED が 1〜2 個のゾーンが続くところは 1 つにまとめる（Positions と音楽連動の光り方で同じ分け方を使う）
    /// </summary>
    public static void Segments(int count, IReadOnlyList<int>? zones, List<(int Start, int Length)> dst)
    {
        var withZones = new List<(int Start, int Length, int Zone)>();
        SegmentZones(count, zones, withZones);
        dst.Clear();
        foreach (var (start, length, _) in withZones) dst.Add((start, length));
    }

    /// <summary>
    /// Segments と同じ分け方で、まとまりごとに元のゾーンの番号も入れる（小さいゾーンをまとめたところは最初のゾーン。
    /// ゾーンが分からず全体を 1 つにしたときは −1）。ゾーンごとの設定を、どのまとまりに使うかを決めるのに使う
    /// </summary>
    public static void SegmentZones(int count, IReadOnlyList<int>? zones, List<(int Start, int Length, int Zone)> dst)
    {
        dst.Clear();
        if (count <= 0) return;
        int sum = 0;
        if (zones is not null) for (int k = 0; k < zones.Count; k++) sum += zones[k];
        if (zones is { Count: > 0 } && sum == count)
        {
            int start = 0, smallStart = -1, smallZone = -1;
            for (int k = 0; k < zones.Count; k++)
            {
                int z = zones[k];
                if (z <= 2)
                {
                    if (smallStart < 0) { smallStart = start; smallZone = k; }
                }
                else
                {
                    if (smallStart >= 0) { dst.Add((smallStart, start - smallStart, smallZone)); smallStart = -1; }
                    dst.Add((start, z, k));
                }
                start += z;
            }
            if (smallStart >= 0 && start > smallStart) dst.Add((smallStart, start - smallStart, smallZone));
        }
        else
        {
            dst.Add((0, count, -1));
        }
    }

    /// <summary>LED ごとのゾーンの番号（ゾーンの LED の数の合計が count と合わなければ、すべて −1）</summary>
    public static int[] LedZones(int count, IReadOnlyList<int>? zones)
    {
        var map = new int[Math.Max(0, count)];
        Array.Fill(map, -1);
        int sum = 0;
        if (zones is not null) for (int k = 0; k < zones.Count; k++) sum += zones[k];
        if (zones is not { Count: > 0 } || sum != count) return map;
        int i = 0;
        for (int k = 0; k < zones.Count; k++)
            for (int j = 0; j < zones[k]; j++) map[i++] = k;
        return map;
    }
}

/// <summary>
/// ゾーンをファンごとに分ける（RgbZoneSettings.Fans）。Lian Li の UNI HUB のチャンネルや、マザーボードの 1 本の端子に
/// 数珠つなぎにしたファンは、OpenRGB では 1 つのゾーンなので、LED を等分してファンごとのゾーンとして扱う。
/// 分けた部分の名前は「ゾーン名 #1」…。設定は、分けた部分の設定があればそれ、なければ元のゾーンの設定を使う
/// </summary>
public static class RgbZoneSplit
{
    public const int MaxFans = 8;

    public static string PartName(string zone, int k) => $"{zone} #{k + 1}";

    /// <summary>分けたあとのゾーン（LED の数・名前・元のゾーンの名前。分けていないゾーンは元の名前のまま）</summary>
    public sealed record Expanded(IReadOnlyList<int>? Zones, IReadOnlyList<string> Names, IReadOnlyList<string> Parents)
    {
        public string Name(int k) => k >= 0 && k < Names.Count ? Names[k] : "";

        /// <summary>k 番目のゾーンの設定（分けた部分は、元のゾーンの設定と合わせる）</summary>
        public RgbZoneSettings? Settings(RgbDeviceSettings s, int k)
        {
            if (k < 0 || k >= Names.Count) return null;
            var own = s.ZoneFor(Names[k]);
            if (Names[k] == Parents[k]) return own;
            return Merge(own, s.ZoneFor(Parents[k]), Names[k]);
        }
    }

    /// <summary>デバイスのゾーンを、設定のファンの数で分ける（どのゾーンも分けなければ、元のまま）</summary>
    public static Expanded Expand(IReadOnlyList<int>? zones, Func<int, string> zoneName, RgbDeviceSettings s)
    {
        if (zones is null || zones.Count == 0) return new Expanded(zones, new[] { zoneName(0) }, new[] { zoneName(0) });
        var counts = new List<int>();
        var names = new List<string>();
        var parents = new List<string>();
        for (int k = 0; k < zones.Count; k++)
        {
            string name = zoneName(k);
            int fans = s.ZoneFor(name)?.Fans ?? 0;
            if (fans >= 2 && zones[k] >= fans)
            {
                // 等分して、割り切れない分は前のファンから 1 個ずつ足す
                int each = zones[k] / fans, extra = zones[k] % fans;
                for (int f = 0; f < fans; f++)
                {
                    counts.Add(each + (f < extra ? 1 : 0));
                    names.Add(PartName(name, f));
                    parents.Add(name);
                }
            }
            else
            {
                counts.Add(zones[k]);
                names.Add(name);
                parents.Add(name);
            }
        }
        return new Expanded(counts, names, parents);
    }

    /// <summary>分けた部分の設定: 部分で選んだ値があればそれ、なければ元のゾーンの値（ケースの中の場所は部分のものだけ）</summary>
    public static RgbZoneSettings Merge(RgbZoneSettings? part, RgbZoneSettings? zone, string name)
    {
        part ??= new RgbZoneSettings { Name = name };
        zone ??= new RgbZoneSettings();
        return new RgbZoneSettings
        {
            Name = name,
            Effect = part.Effect == RgbZoneSettings.SameAsDevice ? zone.Effect : part.Effect,
            Mirror = part.Mirror ?? zone.Mirror,
            Reverse = part.Reverse ?? zone.Reverse,
            GainR = part.GainR != 100 ? part.GainR : zone.GainR,
            GainG = part.GainG != 100 ? part.GainG : zone.GainG,
            GainB = part.GainB != 100 ? part.GainB : zone.GainB,
            Level = part.Level != 100 ? part.Level : zone.Level,
            Sound = part.Sound == MusicSounds.Auto ? zone.Sound : part.Sound,
            Place = part.Place,
            LightMode = part.LightMode.Length > 0 ? part.LightMode : zone.LightMode,
            LightColor = part.LightColor.Length > 0 ? part.LightColor : zone.LightColor,
        };
    }
}
