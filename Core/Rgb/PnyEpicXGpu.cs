// ==================================================
// PnyEpicXGpu.cs - PNY ARGB Epic-X のグラボ（RTX 5060 Ti〜5070 Ti の小さい版）へ、OpenRGB を通さず NvAPI で直接色を書く
//                  (1 色のゾーンは 1 回で塗る・変わった LED だけ書く・書き込みは別のスレッドで、いちばん新しいコマだけ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text.RegularExpressions;

namespace ProFanController;

/// <summary>
/// PNY ARGB Epic-X のコントローラー（I2C アドレス 0x60）の書き方。OpenRGB の PNYARGBEpicXGPUController と同じ命令に、
/// OpenRGB が使っていない「固定色」モード（0x01）を足したもの（RTX 5070 Ti で、1 回の書き込みでゾーン全体がその色になり、
/// 1 秒に 10 回切り替えてもちらつかないことを確かめた）。
///   ゾーンのレジスタ: FRONT = 0x04（前面 20 個が 0〜19、ロゴ 4 個が 20〜23）、ARROW = 0x02（矢印 17 個が 0〜16）
///   ゾーンのモード:   [モード, 明るさ, 速さ, 0, R, G, B]（固定色 = 0x01、直接 = 0x09）
///   LED 1 個:         [0x09, 0xFF, LED の番号, 0, R, G, B]
/// OpenRGB は 1 回の色替えに LED の数だけ（41 回・約 35 ミリ秒）書く。ここでは、ゾーンの LED が全部同じ色なら
/// 固定色モードで 1 回（2 ゾーンで約 1〜5 ミリ秒）、違う色なら直接モードで、前に書いた色から変わった LED だけ書く
/// </summary>
public sealed class PnyEpicXEncoder
{
    public const byte RegArrow = 0x02;
    public const byte RegFront = 0x04;
    public const byte ModeStatic = 0x01;
    public const byte ModeDirect = 0x09;
    /// <summary>OpenRGB の並び: ロゴ 4・前面 20・矢印 17</summary>
    public const int LedCount = 41;

    private enum ZoneMode { Unknown, Static, Direct }

    private sealed class Zone
    {
        public required byte Register;
        /// <summary>OpenRGB の並びでの位置 → コントローラーの LED の番号</summary>
        public required (int Frame, byte Led)[] Map;
        public ZoneMode Mode;
        public Rgb StaticColor;
        public Rgb[] Sent = Array.Empty<Rgb>();
    }

    private readonly Zone[] _zones;
    private readonly byte[] _cmd = new byte[7];

    public PnyEpicXEncoder()
    {
        var front = new List<(int, byte)>();
        for (int i = 0; i < 4; i++) front.Add((i, (byte)(20 + i)));       // ロゴ
        for (int i = 0; i < 20; i++) front.Add((4 + i, (byte)i));         // 前面
        var arrow = new List<(int, byte)>();
        for (int i = 0; i < 17; i++) arrow.Add((24 + i, (byte)i));        // 矢印
        _zones = new[]
        {
            new Zone { Register = RegFront, Map = front.ToArray() },
            new Zone { Register = RegArrow, Map = arrow.ToArray() },
        };
        foreach (var z in _zones) z.Sent = new Rgb[z.Map.Length];
    }

    /// <summary>次は全部書き直す（コントローラーの状態が分からなくなったとき）</summary>
    public void Invalidate()
    {
        foreach (var z in _zones) z.Mode = ZoneMode.Unknown;
    }

    /// <summary>
    /// 1 コマ書く。full なら変わっていなくても全部書く。書き込みに失敗したら false
    /// （そのゾーンは状態が分からないので、次は全部書く）。writes に書き込んだ回数を足す
    /// </summary>
    public bool Apply(ReadOnlySpan<Rgb> frame, bool full, II2cBlockWriter bus, ref int writes)
    {
        if (frame.Length < LedCount) return false;
        foreach (var z in _zones)
        {
            if (!ApplyZone(z, frame, full, bus, ref writes))
            {
                z.Mode = ZoneMode.Unknown;
                return false;
            }
        }
        return true;
    }

    private bool ApplyZone(Zone z, ReadOnlySpan<Rgb> frame, bool full, II2cBlockWriter bus, ref int writes)
    {
        var first = frame[z.Map[0].Frame];
        bool uniform = true;
        for (int k = 1; k < z.Map.Length && uniform; k++) uniform = frame[z.Map[k].Frame] == first;

        if (uniform)
        {
            if (!full && z.Mode == ZoneMode.Static && z.StaticColor == first) return true;
            if (!Write(bus, z.Register, ModeStatic, 0xFF, 0x00, first, ref writes)) return false;
            z.Mode = ZoneMode.Static;
            z.StaticColor = first;
            return true;
        }

        bool all = full || z.Mode != ZoneMode.Direct;
        if (z.Mode != ZoneMode.Direct)
        {
            if (!Write(bus, z.Register, ModeDirect, 0xFF, 0x00, Rgb.Black, ref writes)) return false;
            z.Mode = ZoneMode.Direct;
        }
        for (int k = 0; k < z.Map.Length; k++)
        {
            var c = frame[z.Map[k].Frame];
            // 見て分からない小さな変化（各色 SkipDelta 段以下）は書かない（書く回数が減ると、CPU の使用と遅れが減る）。
            // 小さな変化が積もって SkipDelta を超えたら書く。FullRefreshSec ごとの書き直しでは全部書く
            if (!all && Near(z.Sent[k], c)) continue;
            if (!Write(bus, z.Register, ModeDirect, 0xFF, z.Map[k].Led, c, ref writes)) return false;
            z.Sent[k] = c;
        }
        return true;
    }

    /// <summary>LED 1 個ごとに書くとき、書かずに済ませる色の差（各色の段数。0〜255 のうち）</summary>
    public const int SkipDelta = 2;

    private static bool Near(Rgb a, Rgb b)
        => Math.Abs(a.R - b.R) <= SkipDelta && Math.Abs(a.G - b.G) <= SkipDelta && Math.Abs(a.B - b.B) <= SkipDelta;

    private bool Write(II2cBlockWriter bus, byte register, byte b0, byte b1, byte b2, Rgb c, ref int writes)
    {
        _cmd[0] = b0;
        _cmd[1] = b1;
        _cmd[2] = b2;
        _cmd[3] = 0x00;
        _cmd[4] = c.R;
        _cmd[5] = c.G;
        _cmd[6] = c.B;
        writes++;
        return bus.WriteBlock(register, _cmd);
    }
}

/// <summary>
/// PNY ARGB Epic-X のグラボ 1 枚。描画のスレッドは Submit でコマを渡すだけ（待たない）。書き込みは専用のスレッドが、
/// 渡されたいちばん新しいコマだけを書く（書いている間に来たコマは飛ばす）。1 回の書き込みはふつう数ミリ秒、
/// LED ごとに色が違うコマでも約 35 ミリ秒なので、ほかのデバイスの描画を止めない。
/// グラボ 1 枚につき 1 つだけ作り、アプリが終わるまで使い回す（スレッドは待っているだけ）
/// </summary>
public sealed class PnyEpicXGpu
{
    /// <summary>コントローラーの I2C アドレス</summary>
    public const byte Address = 0x60;
    /// <summary>PNY の PCI サブシステムのベンダー番号</summary>
    public const ushort PnySubVendor = 0x196E;
    /// <summary>色が変わっていなくても、この秒数ごとに全部書き直す（ほかのソフトに色を変えられたときに戻すため）</summary>
    public const double FullRefreshSec = 5;
    /// <summary>
    /// 書き込みに使ってよい時間の割合。NvAPI の I2C は LED 1 個ずつの書き込みで、書いている間は CPU を使い続ける。
    /// 音楽連動では毎コマ色が変わるので、休みなく書くと 1 コア近くを使い続けていた（実機で 1 コアの 80〜97%）。
    /// 送る回数はふだん 1 秒に 10 回（OpenRgb の DirectGpuMaxFps）。設定で回数を上げても、書き終わったら書くのにかかった時間 ×（1 / この割合 − 1）だけ休み、書き込みが休みなく続かないようにする
    /// </summary>
    public const double BusyShare = 0.5;
    /// <summary>この回数続けて書けなかったら、ログに残す</summary>
    private const int WarnAfterFailures = 3;
    /// <summary>この回数続けて書けなかったら、直接書くのをあきらめて OpenRGB を通す（アプリを起動し直すまで）</summary>
    public const int FallbackAfterFailures = 30;

    private static readonly Regex NvApiLocation = new(@"NvAPI I2C on GPU (\d+), address 0x60", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<int, PnyEpicXGpu> Opened = new();
    private static readonly object OpenGate = new();

    private readonly II2cBlockWriter _bus;
    private readonly PnyEpicXEncoder _encoder = new();
    private readonly object _gate = new();
    private readonly Rgb[] _pending = new Rgb[PnyEpicXEncoder.LedCount];
    private readonly Rgb[] _work = new Rgb[PnyEpicXEncoder.LedCount];
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private bool _hasPending;
    private bool _forceFull = true;
    private double _lastFull = double.NegativeInfinity;
    private int _failures;
    private volatile bool _faulted;
    private double _avgWriteMs = double.NaN;

    public PnyEpicXGpu(II2cBlockWriter bus, string name)
    {
        _bus = bus;
        var t = new Thread(Run) { IsBackground = true, Name = $"PNY 直接書き込み ({name})", /* no-loc: ログに出る名前 */ Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    /// <summary>書き込みに失敗し続けたので、もう直接は書かない（OpenRGB を通す）</summary>
    public bool Faulted => _faulted;

    /// <summary>
    /// 1 コマを書くのにかかる時間の平均（ミリ秒。書き込んだコマだけをならす。まだ書いていなければ null）。
    /// 「早めに光らせる」の自動の値に使う（書き終わったときに LED が変わるので、これがほぼそのまま遅れになる）
    /// </summary>
    public double? AverageWriteMs
    {
        get
        {
            double v = Volatile.Read(ref _avgWriteMs);
            return double.IsNaN(v) ? null : v;
        }
    }

    /// <summary>最後のコマを書くのにかかった時間（ミリ秒。表示・テスト用）</summary>
    public double LastWriteMs { get; private set; }

    /// <summary>最後のコマで I2C に書いた回数（表示・テスト用）</summary>
    public int LastWrites { get; private set; }

    /// <summary>
    /// 直接書けるグラボか（OpenRGB で見つかった名前・LED の数・場所で判断する。5080・5090 の大きい版は LED の並びが違うので対象外）
    /// </summary>
    public static bool IsCandidate(RgbDevice d, out int gpuIndex)
    {
        gpuIndex = -1;
        if (d.LedCount != PnyEpicXEncoder.LedCount) return false;
        if (!d.Name.Contains("PNY", StringComparison.OrdinalIgnoreCase) || !d.Name.Contains("Epic-X", StringComparison.OrdinalIgnoreCase)) return false;
        if (d.Name.Contains("5080", StringComparison.Ordinal) || d.Name.Contains("5090", StringComparison.Ordinal)) return false;
        if (d.Zones is not { Count: 3 } z || z[0] != 4 || z[1] != 20 || z[2] != 17) return false;
        var m = NvApiLocation.Match(d.Location);
        return m.Success && int.TryParse(m.Groups[1].Value, out gpuIndex);
    }

    /// <summary>実機で直接書き込みを確かめた型番（名前の一部。空白の有無は問わない）</summary>
    public static readonly IReadOnlyList<string> VerifiedModels = new[] { "5070Ti" };

    /// <summary>実機で確かめた型番か（それ以外は、設定「確かめていないグラボにも直接書き込む」がオンのときだけ直接書く）</summary>
    public static bool IsVerified(RgbDevice d)
    {
        string name = d.Name.Replace(" ", "", StringComparison.Ordinal);
        return VerifiedModels.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>直接書いてよいか: 書き方が分かっているグラボで、確かめた型番か、確かめていない型番も使う設定のとき</summary>
    public static bool Allowed(RgbDevice d, bool allowUnverified) => IsCandidate(d, out _) && (allowUnverified || IsVerified(d));

    /// <summary>直接書けるグラボなら開く（同じグラボは前に開いたものを返す）。書けなければ null（OpenRGB を通して送る）</summary>
    public static PnyEpicXGpu? TryOpen(RgbDevice d)
    {
        if (!IsCandidate(d, out int index)) return null;
        lock (OpenGate)
        {
            if (Opened.TryGetValue(index, out var gpu))
            {
                if (gpu.Faulted) return null;
                gpu.Invalidate();
                return gpu;
            }
            var bus = NvApiI2c.Open(index, Address, PnySubVendor);
            if (bus is null) return null;
            gpu = new PnyEpicXGpu(bus, d.Name);
            Opened[index] = gpu;
            return gpu;
        }
    }

    /// <summary>次のコマは全部書き直す（繋ぎ直したとき）</summary>
    public void Invalidate()
    {
        lock (_gate) _forceFull = true;
    }

    /// <summary>コマを渡す（すぐ戻る）。書き込みはスレッドが行う</summary>
    public void Submit(ReadOnlySpan<Rgb> frame)
    {
        if (frame.Length < PnyEpicXEncoder.LedCount) return;
        lock (_gate)
        {
            frame[..PnyEpicXEncoder.LedCount].CopyTo(_pending);
            _hasPending = true;
            _idle.Reset();
        }
        _wake.Set();
    }

    /// <summary>渡したコマを書き終わるまで待つ（止めるときの光り方用）。間に合えば true</summary>
    public bool Flush(int timeoutMs) => _idle.Wait(Math.Max(0, timeoutMs));

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            while (true)
            {
                bool full;
                lock (_gate)
                {
                    if (!_hasPending)
                    {
                        _idle.Set();
                        break;
                    }
                    Array.Copy(_pending, _work, _work.Length);
                    _hasPending = false;
                    double now = AudioClock.Now;
                    full = _forceFull || now - _lastFull >= FullRefreshSec;
                    _forceFull = false;
                    if (full) _lastFull = now;
                }
                WriteFrame(full);
                // 書き込みに使う時間を BusyShare までにする（次のコマは、休んでいる間に届いた新しいもの 1 つだけを書く）
                int rest = RestMs(LastWriteMs);
                if (rest > 0) Thread.Sleep(rest);
            }
        }
    }

    /// <summary>書き終わってから休む時間（ミリ秒）: 書くのにかかった時間 ×（1 / BusyShare − 1）。最大 100 ミリ秒</summary>
    public static int RestMs(double writeMs)
        => writeMs > 0 && double.IsFinite(writeMs) ? (int)Math.Min(100, Math.Round(writeMs * (1 / BusyShare - 1))) : 0;

    /// <summary>直近の書き込みにかかった時間の合計（ミリ秒。調べる用・ログ用。TakeBusyMs で読むと 0 に戻る）</summary>
    private double _busyMs;

    /// <summary>前に読んだときから、書き込みにかかった時間の合計（ミリ秒）</summary>
    public double TakeBusyMs() => Interlocked.Exchange(ref _busyMs, 0);

    /// <summary>開いているすべてのグラボの、前に読んだときからの書き込み時間の合計（ミリ秒。ログ用）</summary>
    public static double TakeAllBusyMs()
    {
        lock (OpenGate)
        {
            double sum = 0;
            foreach (var g in Opened.Values) sum += g.TakeBusyMs();
            return sum;
        }
    }

    private void WriteFrame(bool full)
    {
        if (_faulted) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int writes = 0;
        bool ok;
        try { ok = _encoder.Apply(_work, full, _bus, ref writes); }
        catch (Exception ex)
        {
            ok = false;
            Logger.Warn($"PNY グラボ: 直接書き込みで例外: {ex.Message}");
        }
        LastWriteMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        LastWrites = writes;
        double busy = Volatile.Read(ref _busyMs);
        Volatile.Write(ref _busyMs, busy + LastWriteMs);
        if (ok)
        {
            if (writes > 0)
            {
                double avg = _avgWriteMs;
                Volatile.Write(ref _avgWriteMs, double.IsNaN(avg) ? LastWriteMs : avg + (LastWriteMs - avg) * 0.1);
            }
            if (_failures >= WarnAfterFailures) Logger.Info("PNY グラボ: 直接書き込みが戻りました");
            _failures = 0;
            return;
        }
        lock (_gate) _forceFull = true;
        if (++_failures == WarnAfterFailures) Logger.Warn("PNY グラボ: NvAPI で LED に書き込めませんでした（続けて試します）");
        if (_failures >= FallbackAfterFailures && !_faulted)
        {
            _faulted = true;
            Logger.Warn($"PNY グラボ: {FallbackAfterFailures} 回続けて書き込めなかったので、OpenRGB を通して送ります（アプリを起動し直すと、また直接書き込みを試します）");
        }
    }
}
