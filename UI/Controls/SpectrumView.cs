// ==================================================
// SpectrumView.cs - ライティング画面の「いまの音」の表示 (10 バンドのイコライザーと同じ帯のスペクトラムと、キックで光るランプ)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

/// <summary>
/// 音の分析結果（AudioFrame）の 10 本の帯（31 Hz〜16 kHz）を、低音（左）〜高音（右）の棒グラフで描く。
/// 棒の下に、イコライザーと同じ帯の名前（31・63・…・16k）を出す。
///   ・太い棒 = その帯の「鳴っている大きさ」（Band。滑らか）
///   ・中の細い明るい棒 = 「立ち上がりを強調した大きさ」（Punch。ドラムの瞬間に伸びる）
///   ・白い線 = 少し前までのいちばん高い位置（ゆっくり落ちてくる）
/// 画面のコマごと（最大 1 秒に 60 回）に SetFrame で値を渡す。配列は最初に確保して使い回し、
/// ブラシや文字も作り置きするので、描くたびに新しいオブジェクトを作らない
/// </summary>
public sealed class SpectrumView : FrameworkElement
{
    /// <summary>帯の数</summary>
    private const int Bands = AudioFrame.BandCount;
    /// <summary>棒と棒の間のすき間（帯の幅に対する割合。最小 2 ピクセル）</summary>
    private const double GapShare = 0.22;
    /// <summary>白い線（ピーク）が落ちる速さ（グラフの高さ全体を 1 として、1 秒あたり）</summary>
    private const double PeakFallPerSec = 0.6;
    /// <summary>白い線が落ち始めるまで待つ時間（秒）</summary>
    private const double PeakHoldSec = 0.35;
    /// <summary>下の目盛りの文字のための余白（ピクセル）</summary>
    private const double LabelHeight = 14;
    /// <summary>目盛りで高さを何等分するか（横線はこれより 1 本少ない）</summary>
    private const int GridSteps = 8;

    // 背景・目盛り・文字・ピークの線は、いまのテーマの色（ThemePalette）を使う
    private static Brush BgBrush => ThemePalette.ChartBg;
    private static Pen GridPen => ThemePalette.ChartGrid;
    private static Brush PeakBrush => ThemePalette.Cached("spectrum.peak", c => new SolidColorBrush(ThemeManager.WithAlpha(c.Text, 0xCC)));
    private static Brush LabelBrush => ThemePalette.ChartLabel;
    /// <summary>帯ごとの色（低音 = ピンク → 中音 = 紫 → 高音 = 水色）。太い棒は少し透かす</summary>
    private static readonly Brush[] BarBrushes = CreateBandBrushes(0xB0);
    /// <summary>中の細い棒（立ち上がり）は不透明で明るく</summary>
    private static readonly Brush[] PunchBrushes = CreateBandBrushes(0xFF);

    private readonly float[] _band = new float[Bands];
    private readonly float[] _punch = new float[Bands];
    private readonly float[] _peak = new float[Bands];
    private readonly double[] _peakAt = new double[Bands];
    private bool _gateOpen;
    private double _lastT = double.NaN;

    // 帯の名前の文字（作るのに手間がかかるので、DPI が変わったときだけ作り直す）
    private readonly FormattedText?[] _labels = new FormattedText?[Bands];
    private double _labelDpi;

    public SpectrumView()
    {
        SnapsToDevicePixels = true;
    }

    /// <summary>
    /// 新しい値を渡して描き直す。band・punch は 0〜1 の 10 個の値（中身をコピーするので、呼んだ側は配列を使い回してよい）。
    /// t は今の時刻（秒。ピークの線を落とす速さの計算に使う）
    /// </summary>
    public void SetFrame(float[] band, float[] punch, bool gateOpen, double t)
    {
        double dt = double.IsNaN(_lastT) ? 0 : Math.Clamp(t - _lastT, 0, 0.1);
        _lastT = t;
        int n = Math.Min(Bands, Math.Min(band.Length, punch.Length));
        for (int i = 0; i < n; i++)
        {
            float b = Math.Clamp(band[i], 0f, 1f);
            _band[i] = b;
            _punch[i] = Math.Clamp(punch[i], 0f, 1f);
            // ピーク: 超えたら持ち上げ、少し待ってからゆっくり落とす
            if (b >= _peak[i])
            {
                _peak[i] = b;
                _peakAt[i] = t;
            }
            else if (t - _peakAt[i] > PeakHoldSec)
            {
                _peak[i] = Math.Max(b, _peak[i] - (float)(PeakFallPerSec * dt));
            }
        }
        _gateOpen = gateOpen;
        InvalidateVisual();
    }

    /// <summary>すべて 0 にする（音の取り込みが止まったとき）</summary>
    public void Clear()
    {
        Array.Clear(_band);
        Array.Clear(_punch);
        Array.Clear(_peak);
        _gateOpen = false;
        _lastT = double.NaN;
        InvalidateVisual();
    }

    /// <summary>モニターの倍率が変わった（別のモニターへ移した）ら、文字をその倍率で描き直す</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, w, h), 8, 8);

        const double padX = 8, padTop = 8;
        double plotH = Math.Max(1, h - padTop - LabelHeight - 4);
        double plotW = Math.Max(1, w - padX * 2);
        double baseY = padTop + plotH;

        // 目盛りの横線（12.5% ごとに 7 本。表示だけで、音の拾い方には関係しない）
        for (int k = 1; k < GridSteps; k++)
        {
            double y = Math.Round(baseY - plotH * k / GridSteps) + 0.5;
            dc.DrawLine(GridPen, new Point(padX, y), new Point(padX + plotW, y));
        }

        double slot = plotW / Bands;
        double gap = Math.Max(2, Math.Round(slot * GapShare));
        double barW = Math.Max(1, slot - gap);
        double punchW = Math.Max(1, barW * 0.4);
        // 無音のときは全体を薄くして「待機中」が分かるようにする
        if (!_gateOpen) dc.PushOpacity(0.45);
        for (int i = 0; i < Bands; i++)
        {
            double x = padX + i * slot + gap / 2;
            double bh = _band[i] * plotH;
            if (bh >= 0.5) dc.DrawRectangle(BarBrushes[i], null, new Rect(x, baseY - bh, barW, bh));
            double ph = _punch[i] * plotH;
            if (ph >= 0.5) dc.DrawRectangle(PunchBrushes[i], null, new Rect(x + (barW - punchW) / 2, baseY - ph, punchW, ph));
            double pk = _peak[i] * plotH;
            if (pk >= 1) dc.DrawRectangle(PeakBrush, null, new Rect(x, baseY - pk - 1, barW, 2));
        }
        if (!_gateOpen) dc.Pop();

        // 下の目盛り: 棒ごとに帯の名前（31・63・…・16k）。狭くて重なるときは 1 つおき（31・125・500・2k・8k）
        EnsureLabels();
        double ly = baseY + 3;
        double widest = _labels.Max(l => l?.Width ?? 0);
        int every = widest + 4 <= slot ? 1 : 2;
        for (int i = 0; i < Bands; i += every)
        {
            if (_labels[i] is not { } label) continue;
            double lx = Math.Clamp(padX + i * slot + slot / 2 - label.Width / 2, padX, padX + plotW - label.Width);
            dc.DrawText(label, new Point(lx, ly));
        }
    }

    private void EnsureLabels()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_labels[0] is not null && dpi == _labelDpi) return;
        _labelDpi = dpi;
        var face = new Typeface(TextElementFont(), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        for (int i = 0; i < Bands; i++)
            _labels[i] = new FormattedText(BandAnalyzer.Label(i), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, LabelBrush, dpi);
    }

    /// <summary>画面全体と同じ文字の種類（見つからなければ Segoe UI）</summary>
    private FontFamily TextElementFont()
        => System.Windows.Documents.TextElement.GetFontFamily(this) ?? new FontFamily("Segoe UI");

    /// <summary>低音 → 高音で、ピンク → 紫 → 水色に変わる帯の数だけの色を作る</summary>
    private static Brush[] CreateBandBrushes(byte alpha)
    {
        var low = Color.FromRgb(0xF4, 0x72, 0xB6);
        var mid = Color.FromRgb(0x7C, 0x5C, 0xFF);
        var high = Color.FromRgb(0x22, 0xC7, 0xFF);
        var brushes = new Brush[Bands];
        for (int i = 0; i < Bands; i++)
        {
            double u = i / (double)(Bands - 1);
            var (a, b, k) = u < 0.5 ? (low, mid, u * 2) : (mid, high, (u - 0.5) * 2);
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * k);
            brushes[i] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, L(a.R, b.R), L(a.G, b.G), L(a.B, b.B))));
        }
        return brushes;
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>
/// キックのたびにぱっと光って消えていく丸いランプ（同期テストのクリック音では白く光る）。
/// 明るさは SetGlow で 0〜1 を渡す。色の違うブラシを毎回作らず、透明度を重ねて描く
/// </summary>
public sealed class BeatLamp : FrameworkElement
{
    /// <summary>この差より小さい変化では描き直さない（ほとんど見分けがつかないため）</summary>
    private const double RedrawStep = 0.004;

    private static Brush OffBrush => ThemePalette.Track;
    private static readonly Brush KickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)));
    private static readonly Brush HaloBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xF4, 0x72, 0xB6)));
    private static readonly Brush ClickBrush = Freeze(new SolidColorBrush(Colors.White));

    private double _kick, _click;

    /// <param name="kick">キックの光の強さ（0〜1）</param>
    /// <param name="click">同期テストのクリックの光の強さ（0〜1）</param>
    public void SetGlow(double kick, double click)
    {
        kick = Math.Clamp(kick, 0, 1);
        click = Math.Clamp(click, 0, 1);
        if (Math.Abs(kick - _kick) < RedrawStep && Math.Abs(click - _click) < RedrawStep) return;
        _kick = kick;
        _click = click;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var c = new Point(w / 2, h / 2);
        double r = Math.Min(w, h) / 2;
        // 外側のぼんやりした光（強いときだけ大きく見える）
        if (_kick > 0.01)
        {
            dc.PushOpacity(_kick);
            dc.DrawEllipse(HaloBrush, null, c, r, r);
            dc.Pop();
        }
        double core = r * 0.62;
        dc.DrawEllipse(OffBrush, null, c, core, core);
        if (_kick > 0.01)
        {
            dc.PushOpacity(_kick);
            dc.DrawEllipse(KickBrush, null, c, core, core);
            dc.Pop();
        }
        if (_click > 0.01)
        {
            dc.PushOpacity(_click);
            dc.DrawEllipse(ClickBrush, null, c, core * 0.7, core * 0.7);
            dc.Pop();
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
