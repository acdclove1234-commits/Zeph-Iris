// ==================================================
// OpenRgb.cs - OpenRGB 連携 (OpenRGB の SDK サーバーに TCP で接続し、温度・レインボー・呼吸・固定色・
//              音楽連動で LED の色を変える。1 秒に 60 コマを専用のスレッドで描き、デバイスごとの速さで送る)
// Dependencies: .NET 8 / System.Net.Sockets (MIT License)
//               ※ OpenRGB 本体は別途インストールし、「SDK Server」を開始しておく（このアプリは通信するだけ）
// See CREDITS.txt for full license details.
// ==================================================

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;

namespace ProFanController;

/// <param name="Type">OpenRGB のデバイスの種類（0 = マザーボード、1 = メモリ、2 = GPU …）</param>
/// <param name="Zones">ゾーン（LED のまとまり）ごとの LED の数。虹色などをゾーンごとに並べるのに使う</param>
/// <param name="Location">つながっている場所（"HID: …"・"I2C: …" など。更新の速さの判断に使う）</param>
/// <param name="ActiveMode">OpenRGB で選ばれているモードの名前（"Direct" など。分からなければ空）</param>
/// <param name="ModeCount">デバイスが持っているモードの数（0 なら不明）</param>
public sealed record RgbDevice(int Index, string Name, int LedCount, int Type = -1, IReadOnlyList<int>? Zones = null, string Location = "",
    string ActiveMode = "", int ModeCount = 0)
{
    public const int TypeDram = 1;
    public const int TypeGpu = 2;
    // OpenRGB のデバイスの種類（そのほかの値）
    public const int TypeMotherboard = 0;
    public const int TypeCooler = 3;
    public const int TypeKeyboard = 5;
    public const int TypeMouse = 6;
    public const int TypeMousemat = 7;

    /// <summary>
    /// OpenRGB を通さず、アプリが直接書き込むグラボ（PnyEpicXGpu。1 色のゾーンは 1 回で書けて速い）。
    /// 速いデバイス（USB）と同じに扱う（遅いデバイス・グラボ用の制限をかけない）
    /// </summary>
    public bool DirectGpu { get; init; }

    /// <summary>ゾーンの名前（Zones と同じ並び。"Addressable Header 2" など。分からなければ null）</summary>
    public IReadOnlyList<string>? ZoneNames { get; init; }

    /// <summary>k 番目のゾーンの名前（分からなければ「ゾーン k+1」）。ゾーンの設定はこの名前で探す</summary>
    public string ZoneName(int k) => ZoneNames is { } n && k >= 0 && k < n.Count && n[k].Length > 0 ? n[k] : L($"ゾーン {k + 1}");

    /// <summary>
    /// 更新が遅いデバイス（GPU・メモリなど、I2C／SMBus でつながっているもの）。
    /// 速く送ると一部しか書き換わらずチカチカするので、送る回数を減らし、滑らかな光り方に置き換える
    /// </summary>
    public bool IsSlow => !DirectGpu && (Type is TypeGpu or TypeDram
        || Location.Contains("I2C", StringComparison.OrdinalIgnoreCase) || Location.Contains("SMBus", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 色を送るたびにちかちかしやすいデバイス（ASRock の Radeon グラボ）。
    /// OpenRGB 1.1 のこのグラボ用の処理は、色を送るたびにグラボのマイコンを「固定色モード」に設定し直す。
    /// 別の機種（RX 9070 XT）で 1 秒に 10 回でもちかちかすると OpenRGB の開発者が報告している（この機種では未確認）。
    /// どちらにしても、書き込む回数をできるだけ減らす
    /// </summary>
    public bool IsFlickerProne => Name.Contains("ASRock", StringComparison.OrdinalIgnoreCase)
        && (Type == TypeGpu || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

    /// <summary>OpenRGB を通して書くグラボ（GPU）の LED（直接書き込むグラボは含めない）</summary>
    public bool IsGpu => !DirectGpu && (Type == TypeGpu
        || Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 書き込みを最小限にするデバイス（グラボ）。グラボの LED は、OpenRGB が LED 1 個ずつ書き込む機種が多く
    /// （PNY Epic-X は 41 個に 41 回。実測で約 35 ミリ秒）、1 回の色替えに時間がかかる。明るさを細かく変えたり、
    /// 色を少しずつ切り替えたりすると書き込みが詰まり、ビートの色替えがほかのデバイスより遅れるので、
    /// 明るさは一定にして、色が変わるときだけすぐに書く
    /// </summary>
    public bool IsLowWrite => IsFlickerProne || IsGpu;

    /// <summary>
    /// 白（単色）の LED だけのキーボード（Logitech G610 / G413）。OpenRGB には RGB として見えるが、
    /// 色を送ると色相の違いが明るさの違いになる（G610 は赤の値を明るさに使うとみられる）ので、明るさだけを送る
    /// </summary>
    public bool IsWhiteOnly => Name.Contains("G610", StringComparison.OrdinalIgnoreCase) || Name.Contains("G413", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// LED に色が反映されるまでの遅れの目安（ミリ秒）。USB は約 8、メモリなど I2C は約 20、
    /// グラボは書き込みに時間がかかるぶん 60（この分だけ早めに色を切り替える）。直接書き込むグラボは 1 回 1〜5 ミリ秒なので 8
    /// </summary>
    public int DefaultLatencyMs => IsGpu ? 60 : IsSlow ? 20 : 8;
}

/// <summary>デバイスごとの状態（表示用）</summary>
/// <param name="Fps">実際に 1 秒に送っている回数</param>
/// <param name="ActiveEffect">いま使っている音楽連動の光り方（更新の遅いデバイスは置き換えた後の名前）</param>
public sealed record RgbDeviceStatus(string Name, int Leds, bool Slow, double Fps, string ActiveEffect, string LastError);

/// <summary>
/// OpenRGB SDK のネットワークプロトコル（必要な部分だけ）。
/// 各パケット = 16 バイトのヘッダー（"ORGB"・デバイス番号・パケット ID・データ長。すべてリトルエンディアン）＋データ。
/// コントローラー情報の形式はプロトコルの版で変わるため、形式が単純な版 1 で受け取る（サーバーが版 0 なら版 0 で）。
/// 送るパケットは 1 つのバッファにヘッダーとデータを続けて作り、1 回の書き込みで送る（毎コマ配列を作らない）
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private const uint RequestControllerCount = 0;
    private const uint RequestControllerData = 1;
    private const uint RequestProtocolVersion = 40;
    private const uint SetClientName = 50;
    /// <summary>サーバーから届く「デバイスの一覧が変わった」の知らせ</summary>
    public const uint DeviceListUpdated = 100;
    /// <summary>LED の色をまとめて変える（UPDATELEDS）</summary>
    public const uint PacketUpdateLeds = 1050;
    private const uint SetCustomMode = 1100;
    /// <summary>このアプリが読めるプロトコルの版（コントローラー情報の形式）</summary>
    public const uint MaxProtocolVersion = 1;
    /// <summary>ヘッダーの大きさ（バイト）</summary>
    public const int HeaderSize = 16;
    /// <summary>これより大きいデータは壊れているとみなす（バイト）</summary>
    private const int MaxPayload = 16 * 1024 * 1024;
    /// <summary>プロトコルの版の返事を待つ時間（ミリ秒）。版 0 の古いサーバーは返事をしない</summary>
    private const int ProtocolReplyWaitMs = 1000;
    /// <summary>接続を待つ時間（秒）</summary>
    private const int ConnectTimeoutSec = 3;
    /// <summary>
    /// 接続中にモードを問い合わせたときの返事を待つ時間（ミリ秒）。ふつうは数ミリ秒で返ってくる。
    /// OpenRGB はデバイスが消えた（一覧が変わった直後）ときは返事をしないので、長く待って LED を止めないようにする
    /// </summary>
    public const int ModeReplyWaitMs = 1000;

    private readonly TcpClient _tcp = new() { NoDelay = true, ReceiveTimeout = 3000, SendTimeout = 3000 };
    private NetworkStream? _stream;
    /// <summary>送るパケットを作るバッファ（足りなければ大きくする。使い回す）</summary>
    private byte[] _packet = new byte[1024];
    /// <summary>受け取ったヘッダー（使い回す）</summary>
    private readonly byte[] _header = new byte[HeaderSize];
    /// <summary>読み捨てるデータの置き場（使い回す）</summary>
    private readonly byte[] _scratch = new byte[4096];

    /// <summary>OpenRGB 側でデバイスの一覧が変わった（取り直しが必要）</summary>
    public bool DeviceListChanged { get; private set; }
    /// <summary>サーバーのプロトコルの版（返事がなければ 0）</summary>
    public uint ServerProtocol { get; private set; }
    /// <summary>実際に使うプロトコルの版（サーバーとこのアプリの小さいほう）</summary>
    public uint Protocol { get; private set; } = MaxProtocolVersion;

    public void Connect(string host, int port)
    {
        var connect = _tcp.ConnectAsync(host, port);
        // タイムアウトで置き去りにした接続の失敗が「待たれずに捨てられた例外」として記録されないようにする
        connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            if (!connect.Wait(TimeSpan.FromSeconds(ConnectTimeoutSec))) throw new TimeoutException(L("OpenRGB に接続できません（タイムアウト）"));
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            // 「1 つ以上のエラーが発生しました」ではなく、本当の理由（接続を拒否された、など）を伝える
            ExceptionDispatchInfo.Capture(Unwrap(ex)).Throw();
        }
        _stream = _tcp.GetStream();

        // プロトコルの版を伝え合う（版 0 の古いサーバーは返事をしないので、1 秒待って来なければ版 0）
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, MaxProtocolVersion);
        Send(0, RequestProtocolVersion, version);
        if (_tcp.Client.Poll(ProtocolReplyWaitMs * 1000, SelectMode.SelectRead))
        {
            var data = Receive(RequestProtocolVersion);
            ServerProtocol = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        }
        else
        {
            ServerProtocol = 0;
        }
        Protocol = Math.Min(ServerProtocol, MaxProtocolVersion);
        Send(0, SetClientName, "Zeph-Iris\0"u8);
    }

    /// <summary>
    /// デバイスのいまのモードの名前を OpenRGB に問い合わせて、返事を待つ（読むだけ。LED には何も書かない）。読めなければ空。
    /// waitMs 以内に返事がなければ TimeoutException。遅れて届いた返事は、次の問い合わせがデバイス番号で見分けて読み飛ばす。
    /// 描画中（RgbSync）は待たない RequestActiveMode を使う
    /// </summary>
    public string GetActiveMode(int device, int waitMs = ModeReplyWaitMs)
    {
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, Protocol);
        Send((uint)device, RequestControllerData, Protocol > 0 ? version : ReadOnlySpan<byte>.Empty);
        var data = ReceiveWithin(RequestControllerData, (uint)device, waitMs);
        return ParseActiveMode(data, Protocol).ActiveMode;
    }

    /// <summary>
    /// モードを問い合わせる（待たない）。返事は PollIncoming が受け取り、TakeActiveMode で取り出す。
    /// OpenRGB 1.1 は、そのデバイスへの書き込み（グラボは 20〜200 ミリ秒）が終わるまで返事を待たせることがあるので、
    /// 描画の途中で返事を待つと、すべての LED が一瞬止まってしまう
    /// </summary>
    public void RequestActiveMode(int device)
    {
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, Protocol);
        Send((uint)device, RequestControllerData, Protocol > 0 ? version : ReadOnlySpan<byte>.Empty);
    }

    /// <summary>PollIncoming が受け取ったモードの返事（デバイス番号 → モードの名前）</summary>
    private readonly Dictionary<uint, string> _modeReplies = new();

    /// <summary>受け取ったまま取り出していないモードの返事がある</summary>
    public bool HasModeReplies => _modeReplies.Count > 0;

    /// <summary>デバイスのモードの返事を取り出す（届いていなければ false）</summary>
    public bool TakeActiveMode(int device, out string mode)
    {
        if (_modeReplies.Remove((uint)device, out var m))
        {
            mode = m;
            return true;
        }
        mode = "";
        return false;
    }

    /// <summary>取り出さなかったモードの返事を捨てる（対象から外れたデバイスの返事など）</summary>
    public void ClearModeReplies() => _modeReplies.Clear();

    public List<RgbDevice> GetDevices()
    {
        DeviceListChanged = false;
        Send(0, RequestControllerCount, ReadOnlySpan<byte>.Empty);
        var countData = Receive(RequestControllerCount);
        if (countData.Length < 4) throw new InvalidDataException(L("OpenRGB からの応答が不正です（デバイスの数）"));
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(countData), 1024);
        var list = new List<RgbDevice>();
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, Protocol);
        for (int i = 0; i < count; i++)
        {
            // 版 1 以上は「この版の形式で」と伝える。版 0 はデータなし
            Send((uint)i, RequestControllerData, Protocol > 0 ? version : ReadOnlySpan<byte>.Empty);
            var data = Receive(RequestControllerData);
            var zoneNames = new List<string>();
            var (name, leds, type, zones, location) = ParseControllerInfo(data, Protocol, zoneNames);
            var (activeMode, modeCount) = ParseActiveMode(data, Protocol);
            list.Add(new RgbDevice(i, name, leds, type, zones, location, activeMode, modeCount) { ZoneNames = zoneNames });
        }
        return list;
    }

    /// <summary>直接色を指定できるモード（Direct / Custom）にする</summary>
    public void SetDirectMode(int device) => Send((uint)device, SetCustomMode, ReadOnlySpan<byte>.Empty);

    /// <summary>すべての LED を同じ色にする</summary>
    public void SetColor(int device, int ledCount, byte r, byte g, byte b)
    {
        if (ledCount <= 0) return;
        var colors = new Rgb[ledCount];
        Array.Fill(colors, new Rgb(r, g, b));
        UpdateLeds(device, colors, false);
    }

    /// <summary>LED ごとに色を指定する（レインボーなど）</summary>
    public void SetColors(int device, IReadOnlyList<Rgb> colors, bool swapRedGreen)
    {
        if (colors.Count == 0) return;
        var array = colors as Rgb[] ?? colors.ToArray();
        UpdateLeds(device, array, swapRedGreen);
    }

    /// <summary>
    /// LED ごとに色を指定する（描画スレッドが毎コマ使う。配列を作らない）。
    /// 書き込みにかかった時間（ミリ秒）を返す。長いときは OpenRGB の処理が追いついていない
    /// </summary>
    public double UpdateLeds(int device, ReadOnlySpan<Rgb> colors, bool swapRedGreen)
    {
        if (colors.Length == 0) return 0;
        int length = BuildUpdateLeds(ref _packet, (uint)device, colors, swapRedGreen);
        return WritePacket(length);
    }

    /// <summary>
    /// サーバーから届いているパケットを、待たずに読む（毎コマ呼ぶ）。
    /// 「デバイスの一覧が変わった」が届いていたら DeviceListChanged を true にする。
    /// コントローラー情報（RequestActiveMode の返事）はモードの名前を覚える。ほかのパケットは読み捨てる
    /// </summary>
    public void PollIncoming()
    {
        if (_stream is null) return;
        while (_tcp.Available >= HeaderSize)
        {
            ReadExactly(_header, HeaderSize);
            var (id, size) = ParseHeader(_header);
            if (id == RequestControllerData)
            {
                var data = new byte[size];
                ReadExactly(data, size);
                _modeReplies[BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4))] = ParseActiveMode(data, Protocol).ActiveMode;
                continue;
            }
            Skip(size);
            if (id == DeviceListUpdated) DeviceListChanged = true;
        }
    }

    /// <summary>サーバーが接続を閉じた（読めるのにデータが 0）。時々呼ぶ（送るデバイスがないときは書き込みの失敗で気づけないため）</summary>
    public bool IsClosedByServer()
    {
        try { return _tcp.Client.Poll(0, SelectMode.SelectRead) && _tcp.Available == 0; }
        catch { return true; }
    }

    /// <summary>ほかのスレッドから接続を切る（送信や受信で止まっている描画スレッドを抜けさせる）</summary>
    public void Abort()
    {
        try { _tcp.Client?.Close(0); } catch { /* 閉じている */ }
        try { _tcp.Dispose(); } catch { /* 閉じている */ }
    }

    /// <summary>ヘッダー（16 バイト）を書く: "ORGB"・デバイス番号・パケット ID・データ長</summary>
    public static void WriteHeader(Span<byte> dst, uint device, uint id, int payloadLength)
    {
        dst[0] = (byte)'O';
        dst[1] = (byte)'R';
        dst[2] = (byte)'G';
        dst[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], device);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], (uint)payloadLength);
    }

    /// <summary>
    /// UPDATELEDS（1050）のパケットを buffer の先頭に作り、全体の長さを返す（足りなければ buffer を大きくする）。
    /// データ = データ長（4 バイト。自分自身を含む）・LED の数（2 バイト）・LED ごとに R, G, B, 0（4 バイト）。
    /// swapRedGreen なら赤と緑を入れ替える（色の並び順が逆のマザーボード用）
    /// </summary>
    public static int BuildUpdateLeds(ref byte[] buffer, uint device, ReadOnlySpan<Rgb> colors, bool swapRedGreen)
    {
        int count = Math.Min(colors.Length, ushort.MaxValue);
        int payload = 4 + 2 + count * 4;
        int total = HeaderSize + payload;
        if (buffer.Length < total) buffer = new byte[Math.Max(total, buffer.Length * 2)];
        var span = buffer.AsSpan(0, total);
        WriteHeader(span, device, PacketUpdateLeds, payload);
        BinaryPrimitives.WriteUInt32LittleEndian(span[HeaderSize..], (uint)payload);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(HeaderSize + 4)..], (ushort)count);
        int p = HeaderSize + 6;
        for (int i = 0; i < count; i++)
        {
            var c = colors[i];
            span[p] = swapRedGreen ? c.G : c.R;
            span[p + 1] = swapRedGreen ? c.R : c.G;
            span[p + 2] = c.B;
            span[p + 3] = 0;
            p += 4;
        }
        return total;
    }

    /// <summary>例外の本当の理由の文章（AggregateException の中身を取り出す）</summary>
    public static string Describe(Exception ex) => Unwrap(ex).Message;

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner }) ex = inner;
        return ex;
    }

    /// <summary>ヘッダーとデータを 1 つのバッファに作って、1 回で書く</summary>
    private void Send(uint device, uint id, ReadOnlySpan<byte> payload)
    {
        int total = HeaderSize + payload.Length;
        if (_packet.Length < total) _packet = new byte[Math.Max(total, _packet.Length * 2)];
        WriteHeader(_packet, device, id, payload.Length);
        payload.CopyTo(_packet.AsSpan(HeaderSize));
        WritePacket(total);
    }

    /// <summary>_packet の先頭 length バイトを書き、かかった時間（ミリ秒）を返す</summary>
    private double WritePacket(int length)
    {
        var stream = _stream ?? throw new IOException(L("OpenRGB に接続していません"));
        long t0 = Stopwatch.GetTimestamp();
        stream.Write(_packet, 0, length);
        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>指定した ID の返事を待つ（途中で届く「一覧が変わった」の知らせは覚えておく）</summary>
    private byte[] Receive(uint expected)
    {
        while (true)
        {
            ReadExactly(_header, HeaderSize);
            var (id, size) = ParseHeader(_header);
            if (id == expected)
            {
                var data = new byte[size];
                ReadExactly(data, size);
                return data;
            }
            Skip(size);
            if (id == DeviceListUpdated) DeviceListChanged = true;
        }
    }

    /// <summary>
    /// Receive と同じだが、デバイス番号も合う返事だけを受け取り、waitMs 以内に届かなければ TimeoutException
    /// （接続が切れていれば IOException）。返事を待つ間に届いた「一覧が変わった」の知らせは覚えておく
    /// </summary>
    private byte[] ReceiveWithin(uint expected, uint device, int waitMs)
    {
        long until = Environment.TickCount64 + Math.Max(0, waitMs);
        while (true)
        {
            long left = until - Environment.TickCount64;
            // 読めるデータがある（または接続が閉じた）まで待つ。閉じていれば次の読み込みが IOException になる
            if (_tcp.Available < HeaderSize && (left <= 0 || !_tcp.Client.Poll((int)Math.Min(left * 1000, int.MaxValue), SelectMode.SelectRead)))
                throw new TimeoutException(L("OpenRGB から返事がありません"));
            ReadExactly(_header, HeaderSize);
            var (id, size) = ParseHeader(_header);
            if (id == expected && BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4)) == device)
            {
                var data = new byte[size];
                ReadExactly(data, size);
                return data;
            }
            Skip(size);
            if (id == DeviceListUpdated) DeviceListChanged = true;
        }
    }

    private static (uint Id, int Size) ParseHeader(byte[] header)
    {
        if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B') throw new IOException(L("OpenRGB からの応答が不正です"));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (size > MaxPayload) throw new IOException(L("OpenRGB からの応答が大きすぎます"));
        return (id, (int)size);
    }

    private void ReadExactly(byte[] buf, int n)
    {
        var stream = _stream ?? throw new IOException(L("OpenRGB に接続していません"));
        int read = 0;
        while (read < n)
        {
            int r = stream.Read(buf, read, n - read);
            if (r == 0) throw new IOException(L("OpenRGB との接続が切れました"));
            read += r;
        }
    }

    /// <summary>n バイト読み捨てる</summary>
    private void Skip(int n)
    {
        while (n > 0)
        {
            int chunk = Math.Min(n, _scratch.Length);
            ReadExactly(_scratch, chunk);
            n -= chunk;
        }
    }

    /// <summary>コントローラー情報（プロトコル版 1 の形式）から、名前と LED の数を取り出す</summary>
    public static (string Name, int LedCount) ParseController(byte[] d)
    {
        var (name, leds, _, _) = ParseControllerFull(d);
        return (name, leds);
    }

    /// <summary>名前・LED の数・種類・ゾーンごとの LED の数</summary>
    public static (string Name, int LedCount, int Type, List<int> Zones) ParseControllerFull(byte[] d)
    {
        var (name, leds, type, zones, _) = ParseControllerInfo(d);
        return (name, leds, type, zones);
    }

    /// <summary>
    /// 選ばれているモードの名前とモードの数（読めなければ空と 0）。
    /// すでに Direct なら「直接色を指定するモード」を送り直さない（送るたびに一瞬ちかちかするデバイスがあるため）
    /// </summary>
    public static (string ActiveMode, int ModeCount) ParseActiveMode(byte[] d, uint protocol)
    {
        try
        {
            int p = 8;
            ReadString(d, ref p); // 名前
            if (protocol >= 1) ReadString(d, ref p); // メーカー
            ReadString(d, ref p); // 説明
            ReadString(d, ref p); // バージョン
            ReadString(d, ref p); // シリアル
            ReadString(d, ref p); // 場所
            int modes = U16(d, ref p);
            if (p + 4 > d.Length) return ("", 0);
            int active = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            p += 4;
            string activeName = "";
            for (int i = 0; i < modes; i++)
            {
                string modeName = ReadString(d, ref p);
                if (i == active) activeName = modeName;
                p += 4 * 9;
                int colors = U16(d, ref p);
                p += colors * 4;
            }
            return (activeName, modes);
        }
        catch (InvalidDataException)
        {
            return ("", 0);
        }
    }

    /// <summary>名前・LED の数・種類・ゾーンごとの LED の数・つながっている場所（プロトコル版 1 の形式）</summary>
    public static (string Name, int LedCount, int Type, List<int> Zones, string Location) ParseControllerInfo(byte[] d) =>
        ParseControllerInfo(d, MaxProtocolVersion);

    /// <summary>名前・LED の数・種類・ゾーンごとの LED の数・つながっている場所（protocol = 0 なら版 0 の形式。メーカー名がない）</summary>
    public static (string Name, int LedCount, int Type, List<int> Zones, string Location) ParseControllerInfo(byte[] d, uint protocol) =>
        ParseControllerInfo(d, protocol, null);

    /// <summary>ParseControllerInfo と同じ。zoneNames があれば、ゾーンの名前も入れる（ゾーンの LED の数と同じ並び）</summary>
    public static (string Name, int LedCount, int Type, List<int> Zones, string Location) ParseControllerInfo(byte[] d, uint protocol, List<string>? zoneNames)
    {
        zoneNames?.Clear();
        int p = 4; // データ長
        if (d.Length < 8) throw new InvalidDataException(L("コントローラー情報が短すぎます"));
        int type = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        p += 4;    // 種類
        string name = ReadString(d, ref p);
        if (protocol >= 1) ReadString(d, ref p); // メーカー（版 1 から）
        ReadString(d, ref p); // 説明
        ReadString(d, ref p); // バージョン
        ReadString(d, ref p); // シリアル
        string location = ReadString(d, ref p); // 場所

        int modes = U16(d, ref p);
        p += 4; // 選択中のモード
        for (int i = 0; i < modes; i++)
        {
            ReadString(d, ref p);
            p += 4 * 9; // 値・フラグ・速度の最小/最大・色数の最小/最大・速度・方向・色の扱い
            int colors = U16(d, ref p);
            p += colors * 4;
        }

        int zones = U16(d, ref p);
        var zoneSizes = new List<int>();
        for (int i = 0; i < zones; i++)
        {
            string zoneName = ReadString(d, ref p);
            zoneNames?.Add(zoneName);
            if (p + 16 > d.Length) throw new InvalidDataException(L("コントローラー情報が途中で切れています"));
            zoneSizes.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 12)));
            p += 4 * 4; // 種類・LED 数の最小/最大/現在
            int matrix = U16(d, ref p);
            p += matrix; // 行列のサイズ（高さ・幅を含む）
        }

        int leds = U16(d, ref p);
        for (int i = 0; i < leds; i++)
        {
            ReadString(d, ref p);
            p += 4;
        }
        int colorCount = U16(d, ref p);
        if (p + colorCount * 4 > d.Length) throw new InvalidDataException(L("コントローラー情報を読み取れません"));
        return (name, colorCount, type, zoneSizes, location);
    }

    private static int U16(byte[] d, ref int p)
    {
        if (p + 2 > d.Length) throw new InvalidDataException(L("コントローラー情報が途中で切れています"));
        int v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
        p += 2;
        return v;
    }

    private static string ReadString(byte[] d, ref int p)
    {
        int len = U16(d, ref p);
        if (p + len > d.Length) throw new InvalidDataException(L("コントローラー情報が途中で切れています"));
        string s = Encoding.UTF8.GetString(d, p, Math.Max(0, len - 1)); // 末尾の \0 を除く
        p += len;
        return s;
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { /* 閉じている */ }
        _tcp.Dispose();
    }
}

/// <summary>接続に失敗したときの、次に試すまでの待ち時間（1, 2, 4, 8, 16, 30, 30 … 秒）</summary>
public static class ReconnectBackoff
{
    public const int MaxDelaySec = 30;

    /// <param name="failures">続けて失敗した回数（1 から）</param>
    public static int DelaySec(int failures) => Math.Min(MaxDelaySec, 1 << Math.Clamp(failures - 1, 0, 5));
}

/// <summary>
/// デバイス 1 台分の「描いて送る」ための状態。描画（DeviceRenderer）・1 コマ分の色・描くための入力・
/// 出来事の読み位置・送るタイミング・送信の速さ・1 秒あたりの回数をまとめて持つ。
/// LED を光らせるスレッドだけが使う（CopyLastSent だけは画面のスレッドから呼んでよい）
/// </summary>
public sealed class RgbDeviceChannel
{
    /// <summary>全デバイスで共有する色の位置（RgbSync が設定する）</summary>
    public SharedColor? SharedColor { get; set; }

    /// <summary>
    /// 直接色を指定するモードにしたときの、そのモードの名前（ふつうは "Direct"。デバイスによっては "Custom" など）。
    /// 定期的にモードを確かめて、これと違っていたとき（ほかのソフトに変えられた）だけ送り直す
    /// </summary>
    public string DirectModeName { get; set; } = "";

    /// <summary>直接色を指定するモードを送った直後: 次に届いたモードの名前を DirectModeName として覚える</summary>
    public bool LearnDirectMode { get; set; }

    /// <summary>止まっているモードで「変わった」とみなす色の差（R・G・B の差の合計）</summary>
    private const int StaticChangeThreshold = 6;
    /// <summary>ディザリングする送る速さの下限（回／秒）</summary>
    /// <summary>更新の遅いデバイスで、送り直すほどの変化とみなす差（LED ごとの R・G・B の差の合計）</summary>
    public const int SlowChangeThreshold = 6;
    /// <summary>
    /// グラボに送る回数の上限（1 秒あたり。自動のとき）。1 回の書き込みに時間がかかるので、
    /// 配線が空いている時間を残し、色を変えたいときにすぐ書けるようにする（色の切り替えは定期便を待たずに送る）
    /// </summary>
    public const double GpuMaxFps = 5;
    /// <summary>
    /// グラボで、送り直すほどの変化とみなす差（LED ごとの R・G・B の差の合計）。グラボは 1 回の書き込みで
    /// LED を 1 個ずつ書き換えるため、わずかな変化まで送ると、ちかちかして見える
    /// </summary>
    public const int GpuChangeThreshold = 24;
    /// <summary>
    /// ちかちかしやすいデバイス（ASRock の Radeon グラボ）に送る回数の上限（1 秒あたり。0.25 秒は空ける）。
    /// 明るさを変えないので、実際に送るのはビートで色が切り替わったときだけ。上限を低くしすぎると、
    /// 次に送れるまで待つぶん色の切り替えがほかのデバイスより遅れる
    /// </summary>
    public const double FlickerProneMaxFps = 4;
    /// <summary>ちかちかしやすいデバイスは、色がはっきり変わったときだけ送る（LED ごとの R・G・B の差の合計）</summary>
    public const int FlickerProneChangeThreshold = 40;
    public const double DitherMinFps = 50;
    /// <summary>描画のエラーを表示し続ける時間（秒）</summary>
    private const double ErrorShowSec = 10;

    private readonly object _sentGate = new();
    private readonly Rgb[] _sent;
    private EventRing? _events;
    private long _cursor;
    private bool _resync = true;
    private double _lastEval = double.NaN;
    private double _lastRender = double.NaN;
    private bool _beatCue;
    private double _seenColorStepT = double.NegativeInfinity;
    private bool _stepPending;
    private double _stepDueT = double.PositiveInfinity;
    // ビートの頭をまたいだか（予測が書き換わっても同じビートで 2 回早めに送らない・取りこぼさない）
    private readonly BeatCrossing _beatCrossing = new();
    private double _nextBeatIn = double.PositiveInfinity;
    private bool _hasSent;
    private double _lastErrorAt = double.NegativeInfinity;

    /// <param name="now">作った時刻（AudioClock の秒。1 秒あたりの回数を数え始める）</param>
    public RgbDeviceChannel(RgbDevice device, double now)
    {
        Device = device;
        Renderer = new DeviceRenderer(device);
        Frame = new Rgb[Math.Max(0, device.LedCount)];
        _sent = new Rgb[Frame.Length];
        Fps = new FpsMeter(now);
        Input.Device = new RgbDeviceSettings { Name = device.Name };
        LatencyMs = LatencyFor(device, Input.Device);
        MaxFps = MaxFpsFor(device, Input.Device);
    }

    public RgbDevice Device { get; }
    /// <summary>OpenRGB を通さず直接書き込むグラボ（なければ null。OpenRGB に送る）</summary>
    public PnyEpicXGpu? Direct { get; init; }
    public DeviceRenderer Renderer { get; }
    /// <summary>描くための入力（毎コマ書き換えて使い回す）</summary>
    public RenderInput Input { get; } = new();
    /// <summary>描いた色（毎コマ上書きする）</summary>
    public Rgb[] Frame { get; }
    public SendScheduler Scheduler { get; } = new();
    public SendRateGovernor Governor { get; } = new();
    /// <summary>実際に送っている回数（回／秒）</summary>
    public FpsMeter Fps { get; }
    public bool SwapRedGreen { get; private set; }
    /// <summary>LED に反映されるまでの遅れ（ミリ秒）</summary>
    public int LatencyMs { get; private set; }
    /// <summary>1 秒に送る回数の上限（設定。速さの調整で下げる前）</summary>
    public double MaxFps { get; private set; }
    /// <summary>前に何かを送った時刻（変化がなくても 5 秒ごとに送り直すため）</summary>
    public double LastSendAny { get; private set; } = double.NegativeInfinity;
    /// <summary>最後の描画のエラー（表示用。なければ空）</summary>
    public string LastError { get; private set; } = "";
    /// <summary>このコマでビートの頭をまたいだ（テンポをつかんでいるとき）</summary>
    public bool BeatCue => _beatCue;
    /// <summary>次のビートの頭まで何秒か（テンポをつかんでいないときは正の無限大）</summary>
    public double NextBeatIn => _nextBeatIn;

    /// <summary>実際の送る上限（回／秒）</summary>
    public double EffectiveFps => Math.Min(MaxFps, Governor.Limit);
    /// <summary>いつもの送る間隔（秒）</summary>
    public double Interval => 1.0 / Math.Max(1, EffectiveFps);

    /// <summary>デバイスの遅れ（ミリ秒）: 設定が 0 以上ならその値、なければ USB 8・I2C 20</summary>
    public static int LatencyFor(RgbDevice device, RgbDeviceSettings settings) =>
        settings.LatencyMs >= 0 ? settings.LatencyMs : device.DefaultLatencyMs;

    /// <summary>
    /// 直接書き込むグラボの「自動」の遅れ（ミリ秒）: 実際に 1 コマ書くのにかかった時間の平均 + 1。
    /// まだ測れていなければ fallback。OpenRGB を通すデバイスは、書き終わったかを知る方法がないので測らない
    /// </summary>
    public static double AutoDirectLatencyMs(double? averageWriteMs, double fallback) =>
        averageWriteMs is double avg && avg >= 0 ? Math.Clamp(avg + 1, 1, 40) : fallback;

    /// <summary>いま使っている遅れ（ミリ秒。直接書き込むグラボで「自動」なら、測った書き込み時間から決める）</summary>
    public double EffectiveLatencyMs => Direct is { } gpu && Input.Device.LatencyMs < 0
        ? AutoDirectLatencyMs(gpu.AverageWriteMs, LatencyMs)
        : LatencyMs;

    /// <summary>
    /// 直接書き込むグラボ（PNY）に送る回数の上限（1 秒あたり。自動のとき）。NvAPI の I2C は LED を 1 個ずつ書き、書いている間は
    /// CPU を使い続ける（LED ごとに色が違うコマで約 35 ミリ秒）。毎コマ（60 回）送ると書き込みが休みなく続き、
    /// アプリが 1 コア近くを使っていた。10 回にしても、ビートの瞬間は「早めに送る」で間に合う（RenderScheduler）
    /// </summary>
    public const double DirectGpuMaxFps = 10;

    /// <summary>1 秒に送る回数の上限: 設定が 1 以上ならその値、なければ遅いデバイス 8・直接書き込むグラボ 10・ほか 60</summary>
    public static double MaxFpsFor(RgbDevice device, RgbDeviceSettings settings)
    {
        double auto = device.IsFlickerProne ? FlickerProneMaxFps : device.IsGpu ? GpuMaxFps : device.DirectGpu ? DirectGpuMaxFps : device.IsSlow ? 8 : 60;
        double user = settings.MaxFps > 0 ? settings.MaxFps : auto;
        return device.IsLowWrite ? Math.Min(auto, user) : user;   // グラボは上げられない（下げるのは可）
    }

    /// <summary>
    /// 音の分析結果を読む時刻: 描く時刻 + デバイスの遅れ − 音の遅れの補正。
    /// LED は送ってから latencyMs 後に光るので、その時刻に鳴っている音を使う。
    /// 音が遅れて聞こえる（Bluetooth など。audioDelayMs が +）なら、その分だけ前の音を使う
    /// </summary>
    public static double EvalTime(double tNow, double latencyMs, double audioDelayMs) =>
        tNow + latencyMs / 1000.0 - audioDelayMs / 1000.0;

    /// <summary>設定が変わったときに呼ぶ（毎コマは呼ばない）</summary>
    public void Configure(RgbConfig cfg, ResolvedMusic music, RgbDeviceSettings settings, bool swapRedGreen)
    {
        Input.Cfg = cfg;
        Input.Music = music;
        Input.Device = settings;
        SwapRedGreen = swapRedGreen;
        LatencyMs = LatencyFor(Device, settings);
        double fps = MaxFpsFor(Device, settings);
        if (fps != MaxFps)
        {
            MaxFps = fps;
            Governor.Reset();
        }
    }

    /// <summary>
    /// 1 コマ分の入力を用意する。音がないとき（音楽連動でない・取り込んでいない）は features・events を null、beat を BeatState.None にする。
    /// 出来事は「前のコマの読む時刻」から「このコマの読む時刻」までの分だけ読む（同じ出来事を 2 回読まない・取りこぼさない）
    /// </summary>
    public void Prepare(double tNow, double tick, FeatureRing? features, EventRing? events, BeatState beat,
        double? temperature, bool emergency, bool syncTest)
    {
        var input = Input;
        double tEval = EvalTime(tNow, EffectiveLatencyMs, input.Music.AudioDelayMs);
        input.T = tNow;
        input.Dt = double.IsNaN(_lastRender) ? Math.Min(tick, 0.1) : Math.Clamp(tNow - _lastRender, 0, 0.1);
        input.TPrevEval = double.IsNaN(_lastEval) ? tEval : _lastEval;
        input.TEval = tEval;
        input.Beat = beat;
        input.Temperature = temperature;
        input.Emergency = emergency;
        input.SyncTest = syncTest;
        // 分析結果の記録も渡す（コマの間の一瞬のキックを見るため。音がなければ null）
        input.Features = features;
        input.SharedColor = SharedColor;
        if (features is not null) features.Sample(tEval, input.Frame); // 記録が空なら無音になる
        else input.Frame.Clear();
        // ディザリングは、毎コマ送るデバイスだけ（間引いて送ると、持ち越した端数の平均がずれて暗い色がちらつく）
        input.AllowDither = SendsEveryFrame(tick);

        input.Events.Clear();
        if (!ReferenceEquals(events, _events))
        {
            _events = events;
            _resync = true;
        }
        if (events is not null)
        {
            if (_resync)
            {
                // 初めて読む（音の取り込みをやり直した、など）: 今より前の出来事は読み飛ばす
                _cursor = 0;
                events.Read(ref _cursor, tEval, input.Events);
                input.Events.Clear();
                _resync = false;
            }
            else
            {
                events.Read(ref _cursor, tEval, input.Events);
            }
        }

        // テンポをつかんでいる（予測してよい）ならビートの頭、そうでなければキックの直後に、遅いデバイスへ早めに送る
        bool predictive = beat.Locked && input.Music.TempoMode == TempoModes.Auto;
        _beatCue = _beatCrossing.Step(beat, predictive, input.TPrevEval, tEval, out _);
        // 次のビートまでの秒数（読む時刻で数えても、表示の時刻で数えても同じ）
        _nextBeatIn = predictive ? beat.NextBeatAfter(tEval) - tEval : double.PositiveInfinity;
        for (int i = 0; i < input.Events.Count; i++)
        {
            if (IsAccent(input.Events[i], predictive)) Scheduler.Latch();
        }
        _lastEval = tEval;
        _lastRender = tNow;
    }

    /// <summary>
    /// 描いたコマを毎回送れる速さか（遅いデバイスでなく、送る上限が 50 回／秒以上で、間隔が描く間隔とほぼ同じ）。
    /// 直接書き込むグラボは送れるが、ディザリングで LED ごとに色がずれると 1 回で塗れなくなるので false
    /// </summary>
    public bool SendsEveryFrame(double tick) =>
        !Device.IsSlow && !Device.DirectGpu && EffectiveFps >= DitherMinFps && Interval <= tick * 1.05;

    /// <summary>遅いデバイスへ早めに送るきっかけになる出来事か</summary>
    public static bool IsAccent(AudioEvent e, bool predictive) => e.Kind switch
    {
        AudioEventKind.Click or AudioEventKind.Drop => true,
        AudioEventKind.Kick => !predictive,
        AudioEventKind.Onset => !predictive && e.Strength >= 1.5f,
        _ => false,
    };

    /// <summary>このデバイスの光り方（デバイスごとに選んでいればそれ、なければ全体の光り方）</summary>
    private string DeviceMode => Input.Device?.LightMode is { Length: > 0 } m ? m : Input.Cfg.Mode;

    /// <summary>描く（描画でエラーが出たら false。そのコマは送らない）</summary>
    public bool Render()
    {
        try
        {
            Renderer.Render(Input, Frame);
            if (WhiteOnly)
            {
                // 白い LED だけのデバイス（G610 など）: 色相の変化が明るさの変化にならないよう、R・G・B の最大を白として送る。
                // 温度モードは色だけで温度を表すので、白い LED は明るさ一定（設定の明るさ）にする（緊急時の点滅はそのまま）
                bool constant = DeviceMode == RgbModes.Temperature && !(Input.Emergency && Input.Cfg.BlinkOnEmergency) && !Input.SyncTest;
                ToWhite(Frame, constant ? (byte)Math.Round(255 * Math.Clamp(Input.Cfg.Brightness, 0, 100) / 100) : null);
            }
            // 音楽連動で色が切り替わった（どのデバイスが切り替えても全デバイスで共有）: 更新の遅いデバイスにも、
            // 次の定期便を待たずにすぐ送る（グラボだけ色の切り替えが遅れて見えないように）
            if (SharedColor is { } shared && shared.LastStepT != _seenColorStepT)
            {
                _seenColorStepT = shared.LastStepT;
                if (Device.IsSlow && !Device.IsFlickerProne) Scheduler.Latch();
                // グラボは小さな色の変化を送らない（しきい値）ので、パレットの隣の色が近いと色替えが届かない。
                // ビートの色替えを描く光り方なら、色替え（クロスフェード）が終わったところで、差が小さくても 1 回送る
                // （ムード・ミックスなど色替えを描かない光り方で送ると、わずかな変化を書き込んでちかちかする）
                if (Device.IsLowWrite && Renderer.DrawsColorSteps) _stepDueT = Input.T + Renderer.ColorStepSettleSec;
            }
            if (Input.T >= _stepDueT)
            {
                _stepPending = true;
                _stepDueT = double.PositiveInfinity;
            }
            // テンポ追従は、共有の色とは別に 2 拍ごとに帯の色を一度に変える: グラボには差が小さくてもすぐ送る
            if (Device.IsLowWrite && Renderer.DrawsColorSteps && Renderer.OwnColorStep) _stepPending = true;
            // 音が止まったときの光り方への切り替えが終わった・音楽連動に戻った: グラボは、切り替えの最後のわずかな差
            // （ちかちかしやすいグラボは、一度の切り替えでも差が 40 以下）を送っていないので、ここで 1 回送る
            // （止まっている間・戻ったあと、色が少しずれたままにならない。同じ色なら送らない）
            if (Device.IsLowWrite && Renderer.IdleRampDone) _stepPending = true;
            return true;
        }
        catch (Exception ex)
        {
            if (LastError.Length == 0 || Input.T - _lastErrorAt > ErrorShowSec) Logger.Warn($"LED の描画でエラー（{Device.Name}）: {ex.Message}");
            LastError = L($"描画できません: {ex.Message}");
            _lastErrorAt = Input.T;
            return false;
        }
    }

    /// <summary>白い LED だけのデバイスか（G610 などの機種、またはデバイスの設定で「白だけ」）</summary>
    public bool WhiteOnly => Device.IsWhiteOnly || Input.Device.WhiteOnly;

    /// <summary>
    /// 白い LED だけのデバイス用に、色を白にする: 各 LED の R・G・B の最大（level を渡したら、すべてその明るさ）。
    /// 色相が変わっても明るさが変わらないように（赤の値だけを明るさとして使うデバイスがあるため）
    /// </summary>
    public static void ToWhite(Rgb[] frame, byte? level = null)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            var c = frame[i];
            byte m = level ?? Math.Max(c.R, Math.Max(c.G, c.B));
            frame[i] = new Rgb(m, m, m);
        }
    }

    /// <summary>送るタイミングか（色が変わったかは見ない）</summary>
    /// <remarks>
    /// 更新の遅いデバイスも、ビートの頭・色の切り替えでは定期便を待たずに送る（ほかのデバイスより遅れて見えないように）。
    /// ただし、ちかちかしやすいデバイス（ASRock の Radeon グラボ）は、ビートに合わせた臨時の送信をしない
    /// （色が変わったときだけ、0.25 秒以上あけて送る）
    /// </remarks>
    public SendReason Decide(double tNow, double tick) =>
        Device.IsFlickerProne ? Scheduler.Decide(tNow, tick, Interval, false) : Scheduler.Decide(tNow, tick, Interval, _beatCue, _nextBeatIn);

    /// <summary>
    /// 送るか: タイミングが来ていて、色が前に送ったものから変わっている（デバイスと光り方ごとのしきい値より大きい）。
    /// 変わっていなくても、USB は 0.2 秒・遅いデバイスは 60 秒ごとに送り直す（ちかちかしやすいグラボは送り直さない）。
    /// グラボは、ビートの色替えなら差が小さくても送る
    /// </summary>
    public SendReason ShouldSend(double tNow, double tick, bool animated)
    {
        var reason = Decide(tNow, tick);
        if (reason == SendReason.None) return reason;
        // 送り直し（ほかのソフトに色を変えられたときに戻すため）は、ちかちかしやすいデバイスにはしない。
        // 同じ色でも書き込むたびに一瞬乱れることがあるため。繋ぎ直したときは新しいチャンネルなので必ず 1 回送る
        // USB などの速いデバイスは、どのモードでも 0.2 秒ごとに同じ色を送り続ける（信号が途切れると、
        // ARGB の電源・ハブなどが自分の光り方（虹色など）に戻ってしまうため。同じ色なので見た目は変わらない）。
        // 音楽連動でも、音が止まって消灯・静かな曲で色が止まると送らなくなるので、止まっているモードと同じにする
        double resendSec = Device.IsSlow ? RgbSync.SlowForceResendSec : RgbSync.KeepAliveSec;
        bool force = !_hasSent || (!Device.IsFlickerProne && tNow - LastSendAny >= resendSec);
        // 止まっているモード（温度・固定色・止まった虹）は、温度が RgbTemperatureFilter で段になっているので、
        // どのデバイスも段が変わったときに 1 回だけ送る（しきい値 6 = 全デバイスが同じ段にそろう）。
        // 動くモードの遅いデバイスは、わずかな変化では送らない
        int threshold = !animated ? StaticChangeThreshold
            : Device.IsFlickerProne ? FlickerProneChangeThreshold
            : Device.IsGpu ? GpuChangeThreshold
            : Device.IsSlow ? SlowChangeThreshold : 0;
        if (!force && Similar(_sent, Frame, threshold))
        {
            if (!_stepPending) return SendReason.None;
            if (Similar(_sent, Frame, 0)) { _stepPending = false; return SendReason.None; }   // 同じ色への色替え: 送るものはない
        }
        return reason;
    }

    /// <summary>送ったら呼ぶ（送った色を覚え、次に送るタイミングと送信の速さを更新する）</summary>
    public void MarkSent(double tNow, double tick, SendReason reason, double writeMs)
    {
        Scheduler.Commit(tNow, tick, Interval, reason == SendReason.None ? SendReason.Regular : reason);
        Governor.Observe(writeMs, tNow, MaxFps);
        _stepPending = false;
        lock (_sentGate)
        {
            Array.Copy(Frame, _sent, Frame.Length);
            _hasSent = true;
        }
        LastSendAny = tNow;
        Fps.Count();
    }

    /// <summary>最後に送った色のコピー（まだ送っていなければ null）。どのスレッドから呼んでもよい</summary>
    public Rgb[]? CopyLastSent()
    {
        lock (_sentGate) return _hasSent ? (Rgb[])_sent.Clone() : null;
    }

    /// <summary>表示用のエラー・注意の文章（なければ空）</summary>
    public string StatusNote(double now)
    {
        if (LastError.Length > 0 && now - _lastErrorAt < ErrorShowSec) return LastError;
        if (Governor.Limited) return L($"OpenRGB の処理が追いつかないため、1 秒に {EffectiveFps:0} 回に下げています");
        return "";
    }

    /// <summary>a と b がほぼ同じ色か（LED ごとの R・G・B の差の合計がどれも threshold 以下）</summary>
    public static bool Similar(Rgb[] a, Rgb[] b, int threshold)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i].R - b[i].R) + Math.Abs(a[i].G - b[i].G) + Math.Abs(a[i].B - b[i].B) > threshold) return false;
        }
        return true;
    }
}

/// <summary>
/// OpenRGB に色を送り続ける（専用のスレッドで 1 秒に 60 コマ。止まっているモードは 1 秒に 10 コマ）。
///   ・コマの時刻は Stopwatch の時計（AudioClock）で、予定の時刻に描く（RenderClock の高精度タイマーで待つ）
///   ・デバイスごとに描画（DeviceRenderer）を 1 つずつ持ち、毎コマ描く。送るのはデバイスの速さに合わせる
///     （USB は毎コマ、GPU などの遅いデバイスは 1 秒に 8 回＋ビートの瞬間）
///   ・OpenRGB が起動していなければ 1, 2, 4 … 30 秒ごとに繋ぎ直す。Kick() ですぐ繋ぎ直す（ファン制御には影響しない）
///   ・止めるとき（連携をやめる・アプリの終了・スリープ・デバイスを外す）は、設定の「止めるときの光り方」にする
/// </summary>
public sealed class RgbSync : IDisposable
{
    /// <summary>OpenRGB につながらなくなった時刻（AudioClock の秒。つながっている・止めているときは NaN）。画面で、長く続いたら知らせる</summary>
    public double FailingSince { get; private set; } = double.NaN;
    /// <summary>つながらない理由（最後のもの）</summary>
    public string LastError { get; private set; } = "";

    /// <summary>全デバイスで共有する音楽連動の色（デバイスごとに色がずれないように）</summary>
    private readonly SharedColor _sharedColor = new();
    /// <summary>「確かめていない型番なので OpenRGB を通す」と記録したグラボ（1 台につき 1 回だけ書く）</summary>
    private readonly HashSet<string> _unverifiedLogged = new(StringComparer.Ordinal);

    private static readonly (double At, byte R, byte G, byte B)[] Stops =
    {
        (0.00, 0x22, 0xC7, 0xFF), // 冷えている: 水色
        (0.40, 0x34, 0xD3, 0x99), // 緑
        (0.70, 0xFB, 0xBF, 0x24), // 黄
        (1.00, 0xFF, 0x3B, 0x30), // 熱い: 赤
    };

    /// <summary>動くモードのコマの間隔（秒）。1 秒に 60 コマ</summary>
    public const double AnimatedPeriod = 1.0 / 60;
    /// <summary>止まっているモード（温度・固定色など）のコマの間隔（秒）。1 秒に 10 コマ</summary>
    public const double StaticPeriod = 0.1;
    /// <summary>
    /// 速いデバイス（USB）に、色が変わっていなくても同じ色を送り直す間隔（秒。どのモードでも）。ARGB の電源・ハブには、
    /// 信号が途切れると自分の光り方に切り替わるものがあるので、途切れないようにする
    /// </summary>
    public const double KeepAliveSec = 0.2;
    /// <summary>更新の遅いデバイス（グラボ・メモリ）の送り直しの間隔（秒）。ちかちかしやすいグラボは送り直さない</summary>
    public const double SlowForceResendSec = 60;
    /// <summary>止めるとき、直接書き込むグラボの書き込みが終わるのを待つ時間の上限（ミリ秒）</summary>
    public const int StopFlushMs = 300;
    /// <summary>モードを確かめる間隔（秒。読むだけ。ほかのソフトに変えられていたときだけ直接色を指定するモードに戻す）</summary>
    public const double ModeRefreshSec = 15;
    /// <summary>モードを確かめる間隔（秒。テストで短くする）</summary>
    internal static double ModeRefreshInterval = ModeRefreshSec;
    /// <summary>止めるときの光り方を送るのにかけてよい時間（秒）</summary>
    public const double StopBudgetSec = 0.5;
    /// <summary>Stop() で描画スレッドの終了を待つ最長時間（ミリ秒）。画面を長く止めないように</summary>
    private const int StopJoinMs = 550;
    /// <summary>表示用の状態を更新する間隔（秒）</summary>
    private const double StatusEverySec = 0.5;
    /// <summary>前の描画スレッドの終了を待つ最長時間（ミリ秒）</summary>
    private const int PreviousLoopWaitMs = 5000;
    /// <summary>どのタイマーを使うかをログに 1 回だけ書いた</summary>
    private static int _timerLogged;
    /// <summary>これだけ続いた接続は「正常だった」とみなし、次に切れたときの待ち時間を 1 秒から数え直す（秒）</summary>
    private const double HealthySessionSec = 10;
    /// <summary>OpenRGB を起動したあと、つながるまで試す間隔（秒。デバイスを探しているあいだは SDK サーバーが応えないことがある）</summary>
    private const int AfterLaunchRetrySec = 3;

    /// <summary>つながらないとき OpenRGB を起動する（描画スレッドと「接続テスト」から使う）</summary>
    public OpenRgbAutoStart AutoStart { get; } = new();

    /// <summary>設定・温度を守る鍵（描画スレッドと画面のスレッドの両方から使う）</summary>
    private readonly object _gate = new();
    /// <summary>開始・停止を守る鍵（画面のスレッドから使う。描画スレッドは使わない）</summary>
    private readonly object _life = new();
    private RgbConfig _cfg = new();
    private ResolvedMusic _music = ResolvedMusic.Default;
    private int _cfgVersion;
    private double? _temp;
    private readonly RgbTemperatureFilter _tempFilter = new();
    private bool _emergency;
    private volatile LoopHandle? _loop;
    private Thread? _lastThread;
    private int _generation;
    private bool _suspended;
    private bool _disposed;
    private volatile bool _syncTest;
    private volatile bool _whiteTest;
    /// <summary>最後にログに残したデバイスの一覧（同じ一覧なら繋ぎ直しても書かない）</summary>
    private string _loggedDevices = "";
    private volatile string _status = L("停止中");
    private volatile RgbDeviceChannel[] _published = Array.Empty<RgbDeviceChannel>();
    private volatile IReadOnlyList<RgbDevice> _devices = Array.Empty<RgbDevice>();
    private volatile IReadOnlyList<RgbDeviceStatus> _deviceStatus = Array.Empty<RgbDeviceStatus>();
    private int _currentColor;
    /// <summary>スリープから戻ったときは、Direct と表示されていてもモードを送り直す（グラボのマイコンが戻っていることがある）</summary>
    private volatile bool _forceDirectOnce;

    /// <summary>接続の状態（設定画面の表示用）</summary>
    public string Status { get => _status; private set => _status = value; }
    /// <summary>OpenRGB にあるデバイスの一覧（最後に接続したときのもの）</summary>
    public IReadOnlyList<RgbDevice> Devices => _devices;
    /// <summary>いま描いている代表の色（設定画面の丸い表示用。最初のデバイスの最初の LED。明るさを反映した後）</summary>
    public (byte R, byte G, byte B) CurrentColor
    {
        get
        {
            int v = Volatile.Read(ref _currentColor);
            return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
    }
    /// <summary>音楽連動の分析エンジン（App が設定する。取り込み中でなければ null を返す）</summary>
    public Func<AudioEngine?>? AudioSource { get; set; }
    /// <summary>同期テスト中（クリック音に合わせて白く光らせる）</summary>
    public bool SyncTest
    {
        get => _syncTest;
        set
        {
            _syncTest = value;
            WakeLoop();
        }
    }
    /// <summary>色合わせのテスト中（全部のデバイスを白く光らせ、色味を見比べる）</summary>
    public bool WhiteTest
    {
        get => _whiteTest;
        set
        {
            _whiteTest = value;
            WakeLoop();
        }
    }
    /// <summary>デバイスごとの状態（設定画面の表示用。1 秒に 2 回更新）。選んだのに見つからないデバイスも LED 0 個で入る</summary>
    public IReadOnlyList<RgbDeviceStatus> DeviceStatus => _deviceStatus;

    /// <summary>最後に送った色のコピー（設定画面のプレビュー用。まだ送っていなければ null）</summary>
    public Rgb[]? GetLastFrame(string deviceName)
    {
        foreach (var ch in _published)
        {
            if (ch.Device.Name == deviceName) return ch.CopyLastSent();
        }
        return null;
    }

    /// <summary>
    /// 今すぐ繋ぎ直す（「再接続」ボタン）。接続を待っている間なら待たずに試す。
    /// 繋いだら、ちかちかしやすいデバイスにも直接色を指定するモードを送り直す（デバイスを整え直す）
    /// </summary>
    public void Kick()
    {
        lock (_life)
        {
            if (_disposed || _suspended) return;
            _forceDirectOnce = true;
            if (!LoopRunningLocked())
            {
                if (SnapshotConfig().Enabled) StartLocked();
            }
            else
            {
                KickLocked();
            }
        }
    }

    /// <summary>スリープの前: LED を「止めるときの光り方」にしてから、送るのをやめる（接続も閉じる）</summary>
    public void Suspend()
    {
        lock (_life)
        {
            if (_disposed) return;
            _suspended = true;
            if (_loop is not null) StopLocked(SnapshotConfig(), L("スリープ中"));
        }
    }

    /// <summary>スリープからの復帰: 繋ぎ直して再開する（直接色を指定するモードも送り直す）</summary>
    public void Resume()
    {
        lock (_life)
        {
            if (_disposed) return;
            _suspended = false;
            _forceDirectOnce = true;
            if (!SnapshotConfig().Enabled) return;
            if (!LoopRunningLocked()) StartLocked();
            else KickLocked();
        }
    }

    /// <summary>
    /// 設定を変える。接続先が変わったときだけ OpenRGB に繋ぎ直し、対象のデバイスの変更・光り方・明るさ・色などは
    /// 次のコマから反映する（スライダーを動かしてもちらつかないように）。
    /// 連携をオフにしたときは、LED を「止めるときの光り方」にしてから止める
    /// </summary>
    public void Configure(RgbConfig cfg)
    {
        cfg ??= new RgbConfig();
        var music = MusicTuning.Resolve(cfg);
        bool serverChanged;
        lock (_gate)
        {
            serverChanged = _cfg.Host != cfg.Host || _cfg.Port != cfg.Port;
            if (_cfg.SensorId != cfg.SensorId) _tempFilter.Reset();   // 別のセンサーになった
            _cfg = cfg;
            _music = music;
            _cfgVersion++;
        }
        lock (_life)
        {
            if (_disposed) return;
            if (!cfg.Enabled)
            {
                if (_loop is not null) StopLocked(cfg, L("停止中"));
                else if (!_suspended) Status = L("停止中");
                return;
            }
            if (_suspended) return;
            if (!LoopRunningLocked()) StartLocked();
            else if (serverChanged) KickLocked(); // 接続を待っている間なら、新しい接続先をすぐ試す
            else WakeLoop();
        }
    }

    /// <summary>画面スレッドから毎周期呼ぶ</summary>
    public void Update(double? temp, bool emergency)
    {
        lock (_gate)
        {
            _temp = _tempFilter.Push(temp, AudioClock.Now);
            _emergency = emergency;
        }
    }

    public static (byte R, byte G, byte B) ColorFor(double temp, double low, double high)
    {
        double f = Math.Clamp((temp - low) / Math.Max(1, high - low), 0, 1);
        for (int i = 0; i < Stops.Length - 1; i++)
        {
            var a = Stops[i];
            var b = Stops[i + 1];
            if (f > b.At) continue;
            double k = (f - a.At) / (b.At - a.At);
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * k);
            return (L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }
        var last = Stops[^1];
        return (last.R, last.G, last.B);
    }

    /// <summary>
    /// 止めるときの 1 コマを dst に作る（OnStop: Off = 消す・Static = 固定色を明るさに合わせて・Keep = そのまま）。
    /// 送らなくてよい（Keep）なら false
    /// </summary>
    public static bool StopFrame(RgbConfig cfg, Rgb[] dst)
    {
        switch (cfg.OnStop)
        {
            case RgbStopBehaviors.Keep:
                return false;
            case RgbStopBehaviors.Static:
            {
                var c = Rgb.Parse(cfg.StaticColor);
                double brightness = Math.Clamp(cfg.Brightness, 0, 100) / 100;
                // 「固定色」モードと同じ見え方にする（ガンマ補正: 各成分を 2.2 乗してから明るさを掛ける）
                var o = cfg.Gamma ? new Rgb(Gamma(c.R, brightness), Gamma(c.G, brightness), Gamma(c.B, brightness)) : c.Scale(brightness);
                Array.Fill(dst, o);
                return true;
            }
            default:
                Array.Fill(dst, Rgb.Black);
                return true;
        }
    }

    private static byte Gamma(byte v, double brightness) => (byte)Math.Round(Math.Pow(v / 255.0, 2.2) * 255 * brightness);

    /// <summary>
    /// 色を変えるデバイスを選ぶ（LED が 1 個以上あり、selected が空なら全部、そうでなければ名前が入っているもの）。
    /// selected にあるのに OpenRGB に見つからない名前を missing に入れる（設定からは消さない）
    /// </summary>
    public static List<RgbDevice> SelectTargets(IReadOnlyList<RgbDevice> devices, IReadOnlyList<string>? selected, List<string> missing)
    {
        missing.Clear();
        bool all = selected is null || selected.Count == 0;
        var list = new List<RgbDevice>();
        foreach (var d in devices)
        {
            if (d.LedCount > 0 && (all || selected!.Contains(d.Name))) list.Add(d);
        }
        if (!all)
        {
            foreach (var name in selected!)
            {
                if (string.IsNullOrEmpty(name) || missing.Contains(name)) continue;
                bool found = false;
                foreach (var d in devices) if (d.Name == name) { found = true; break; }
                if (!found) missing.Add(name);
            }
        }
        return list;
    }

    /// <summary>LED を「止めるときの光り方」にしてから、送るのをやめる（アプリの終了時など。画面を止めるのは長くても約 0.6 秒）</summary>
    public void Stop()
    {
        lock (_life) StopLocked(SnapshotConfig(), L("停止中"));
    }

    /// <summary>設定画面の「接続テスト」: デバイス一覧を返す</summary>
    public static Task<List<RgbDevice>> TestAsync(string host, int port) => Task.Run(() =>
    {
        using var client = new OpenRgbClient();
        client.Connect(host, port);
        return client.GetDevices();
    });

    public void Dispose()
    {
        lock (_life)
        {
            if (_disposed) return;
            StopLocked(SnapshotConfig(), L("停止中"));
            _disposed = true;
        }
    }

    // =====================================================================
    // 開始・停止（画面のスレッドから。_life の鍵の中で呼ぶ）
    // =====================================================================

    /// <summary>描画スレッド 1 本分の状態。止めたあとに古いスレッドが動いていても、新しいスレッドと混ざらない</summary>
    private sealed class LoopHandle
    {
        public LoopHandle(int generation) => Generation = generation;

        /// <summary>何本目のスレッドか（表示・ログ用）</summary>
        public readonly int Generation;
        public readonly CancellationTokenSource Cts = new();
        /// <summary>「すぐ見直して」の合図（設定の変更・Kick）</summary>
        public readonly AutoResetEvent Wake = new(false);
        public Thread? Thread;
        /// <summary>いまの接続（Stop がスレッドを待ちきれなかったとき、接続を切って抜けさせる）</summary>
        public volatile OpenRgbClient? Client;
        /// <summary>止めるときに送る光り方（null なら何も送らない）</summary>
        public volatile StopRequest? Stop;
        /// <summary>1 なら繋ぎ直す（Interlocked で読み書き）</summary>
        public int KickFlag;
    }

    /// <param name="Deadline">これを過ぎたら送らない（AudioClock の秒）</param>
    private sealed record StopRequest(RgbConfig Cfg, double Deadline);

    private RgbConfig SnapshotConfig()
    {
        lock (_gate) return _cfg;
    }

    private void StartLocked()
    {
        var h = new LoopHandle(++_generation);
        var previous = _lastThread;
        var thread = new Thread(() => RunLoop(h, previous)) { IsBackground = true, Name = $"OpenRGB 描画 #{h.Generation}" }; // no-loc（ログに出る名前）
        try { thread.Priority = ThreadPriority.AboveNormal; } catch { /* 優先度を変えられない環境 */ }
        h.Thread = thread;
        _loop = h;
        _lastThread = thread;
        Status = L("OpenRGB に接続しています…");
        thread.Start();
    }

    private void StopLocked(RgbConfig? stopCfg, string status)
    {
        FailingSince = double.NaN;
        var h = _loop;
        _loop = null;
        if (h is not null)
        {
            if (stopCfg is not null) h.Stop = new StopRequest(stopCfg, AudioClock.Now + StopBudgetSec);
            try { h.Cts.Cancel(); } catch { /* 終わっている */ }
            try { h.Wake.Set(); } catch { /* 終わっている */ }
            if (h.Thread is { } t && t != Thread.CurrentThread && !t.Join(StopJoinMs))
            {
                // 接続や送信で止まっている → 接続を切って抜けさせる（次のスレッドは、このスレッドが終わるのを待ってから始める）
                h.Client?.Abort();
                Logger.Warn("OpenRGB: 描画スレッドがすぐに止まらなかったため、接続を切りました");
            }
        }
        Status = status;
        _deviceStatus = Array.Empty<RgbDeviceStatus>();
        _published = Array.Empty<RgbDeviceChannel>();
        Volatile.Write(ref _currentColor, 0);
    }

    /// <summary>描画スレッドが動いているか（予期しないエラーで終わっていたら片付けて false。次に始め直せるように）</summary>
    private bool LoopRunningLocked()
    {
        var h = _loop;
        if (h is null) return false;
        if (h.Thread is { IsAlive: true }) return true;
        _loop = null;
        return false;
    }

    private void KickLocked()
    {
        var h = _loop;
        if (h is null) return;
        Interlocked.Exchange(ref h.KickFlag, 1);
        try { h.Wake.Set(); } catch { /* 終わっている */ }
    }

    private void WakeLoop()
    {
        try { _loop?.Wake.Set(); } catch { /* 終わっている */ }
    }

    /// <summary>このスレッドがまだ現役のときだけ、表示の状態を書き換える（止めたあとの古いスレッドは書かない）</summary>
    private bool IsCurrent(LoopHandle h) => !h.Cts.IsCancellationRequested && ReferenceEquals(_loop, h);

    private void SetStatus(LoopHandle h, string text)
    {
        if (IsCurrent(h)) Status = text;
    }

    // =====================================================================
    // 描画スレッド
    // =====================================================================

    /// <summary>接続 → 描いて送る → 切れたら待って繋ぎ直す、を止めるまで繰り返す</summary>
    private void RunLoop(LoopHandle h, Thread? previous)
    {
        var token = h.Cts.Token;
        try
        {
            // 前のスレッドがまだ終わっていなければ、終わるまで待つ（2 本が同時に送らないように）
            if (previous is not null && previous.IsAlive)
            {
                SetStatus(h, L("前の接続が終わるのを待っています…"));
                long until = Environment.TickCount64 + PreviousLoopWaitMs;
                while (previous.IsAlive && !token.IsCancellationRequested && Environment.TickCount64 < until) previous.Join(50);
            }
            bool unthrottled = RenderClock.DisablePowerThrottling();
            using var clock = new RenderClock(token.WaitHandle, h.Wake);
            if (Interlocked.Exchange(ref _timerLogged, 1) == 0)
            {
                Logger.Info(clock.HighResolution
                    ? $"LED の描画: 高精度タイマーで 1 秒に 60 コマを刻みます（省電力による間引きの解除: {(unthrottled ? "成功" : "未対応")}）"
                    : "LED の描画: 高精度タイマーが使えないため、動くモードの間だけ Windows のタイマーを 1 ミリ秒単位にします");
            }
            int failures = 0;
            string lastLogged = "";
            while (!token.IsCancellationRequested)
            {
                Interlocked.Exchange(ref h.KickFlag, 0);
                var cfg = SnapshotConfig();
                OpenRgbClient? client = null;
                bool connected = false;
                double sessionStart = double.NaN;
                try
                {
                    client = new OpenRgbClient();
                    h.Client = client;
                    SetStatus(h, L("OpenRGB に接続しています…"));
                    client.Connect(cfg.Host, cfg.Port);
                    var devices = client.GetDevices();
                    connected = true;
                    FailingSince = double.NaN;
                    if (token.IsCancellationRequested) break;
                    _devices = devices;
                    // 見つかったデバイスは、一覧が変わったときだけログに残す（ほかの PC の「光らない」を調べられるように）
                    string summary = DescribeDevices(devices);
                    if (lastLogged.Length > 0 || summary != _loggedDevices)
                    {
                        Logger.Info($"OpenRGB: 接続しました（{devices.Count} 個のデバイス{(devices.Count > 0 ? ": " + summary : "。OpenRGB の画面でデバイスが見つかっているか確かめてください")}）");
                        _loggedDevices = summary;
                    }
                    lastLogged = "";
                    sessionStart = AudioClock.Now;
                    AutoStart.Connected(sessionStart);
                    RunSession(h, client, clock, devices, cfg.Host, cfg.Port);
                    failures = 0; // 繋ぎ直しの依頼で戻った（Kick・一覧の変化など）→ すぐ繋ぎ直す
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // しばらく正常に動いていたなら、待ち時間を 1 秒から数え直す
                    // （繋がってもすぐ切れるのを繰り返すときは、待ち時間を延ばしていく）
                    if (!double.IsNaN(sessionStart) && AudioClock.Now - sessionStart >= HealthySessionSec) failures = 0;
                    failures++;
                    int delay = ReconnectBackoff.DelaySec(failures);
                    string msg = OpenRgbClient.Describe(ex);
                    AutoStart.Disconnected(AudioClock.Now);
                    LastError = msg;
                    if (double.IsNaN(FailingSince)) FailingSince = AudioClock.Now;
                    // OpenRGB が動いていなければ起動する（起動したら、短い間隔で繋ぎ直す）
                    string? launched = connected ? null : AutoStart.OnFailure(cfg, AudioClock.Now);
                    if (launched is not null)
                    {
                        failures = 0;
                        delay = AfterLaunchRetrySec;
                    }
                    SetStatus(h, launched ?? (connected
                        ? L($"OpenRGB との接続が切れました（{delay} 秒後に繋ぎ直します）: {msg}")
                        : L($"接続できません（{delay} 秒後に再試行）: {msg}")));
                    PublishDisconnected(h, msg);
                    if (msg != lastLogged)
                    {
                        Logger.Warn($"OpenRGB: {msg}");
                        lastLogged = msg;
                    }
                    WaitBeforeRetry(h, delay);
                }
                finally
                {
                    h.Client = null;
                    client?.Dispose();
                }
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.Error($"OpenRGB の描画スレッドが止まりました: {ex}");
            SetStatus(h, L($"エラーで止まりました: {OpenRgbClient.Describe(ex)}"));
        }
        catch
        {
            // 止めている途中の例外（接続を切った、など）は無視
        }
    }

    /// <summary>繋ぎ直すまで待つ。Kick() されたらすぐ戻る</summary>
    private static void WaitBeforeRetry(LoopHandle h, int seconds)
    {
        var token = h.Cts.Token;
        long until = Environment.TickCount64 + seconds * 1000L;
        var handles = new[] { token.WaitHandle, h.Wake };
        while (!token.IsCancellationRequested)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0) return;
            WaitHandle.WaitAny(handles, (int)Math.Min(left, int.MaxValue));
            if (Volatile.Read(ref h.KickFlag) != 0) return;
        }
    }

    /// <summary>
    /// 接続している間のコマの繰り返し。繋ぎ直しが必要（Kick・デバイスの一覧の変化・接続先の変更）なら戻る。
    /// 止める合図が来たら「止めるときの光り方」を送ってから戻る。通信のエラーは例外のまま投げる（呼び出し元が繋ぎ直す）
    /// </summary>
    private void RunSession(LoopHandle h, OpenRgbClient client, RenderClock clock, List<RgbDevice> devices, string host, int port)
    {
        var token = h.Cts.Token;
        var channels = new List<RgbDeviceChannel>();
        var missing = new List<string>();
        string? selectionKey = null;
        int version = int.MinValue;
        RgbConfig cfg = SnapshotConfig();
        ResolvedMusic music = ResolvedMusic.Default;
        double now = AudioClock.Now;
        var cadence = new FrameCadence(AnimatedPeriod, now);
        var renderFps = new FpsMeter(now);
        double nextModeRefresh = now + ModeRefreshInterval;
        double nextStatus = now;

        while (true)
        {
            if (token.IsCancellationRequested)
            {
                ApplyStopRequest(h, client, channels);
                return;
            }

            // 設定・温度を受け取る（設定が変わったときだけ、デバイスごとの設定を作り直す）
            int v;
            double? temp;
            bool emergency;
            lock (_gate)
            {
                v = _cfgVersion;
                temp = _temp;
                emergency = _emergency;
                if (v != version)
                {
                    cfg = _cfg;
                    music = _music;
                }
            }
            if (v != version)
            {
                version = v;
                if (cfg.Host != host || cfg.Port != port)
                {
                    // 接続先が変わった → 今のデバイスを「止めるときの光り方」にしてから、新しい接続先へ
                    SendStopFrames(client, channels, cfg, double.PositiveInfinity);
                    SetStatus(h, L("OpenRGB の接続先が変わったので、繋ぎ直しています…"));
                    return;
                }
                string key = SelectionKey(cfg);
                if (key != selectionKey)
                {
                    selectionKey = key;
                    RebuildChannels(client, devices, cfg, channels, missing, now);
                    nextStatus = now; // すぐ表示を更新する
                }
                foreach (var ch in channels)
                    ch.Configure(cfg, music, SettingsFor(cfg, ch.Device.Name), cfg.SwapRedGreen?.Contains(ch.Device.Name) == true);
            }

            if (Interlocked.Exchange(ref h.KickFlag, 0) != 0)
            {
                SetStatus(h, L("OpenRGB に繋ぎ直しています…"));
                return;
            }
            client.PollIncoming();
            if (client.DeviceListChanged)
            {
                SetStatus(h, L("OpenRGB のデバイスの一覧が変わったので、繋ぎ直しています…"));
                Logger.Info("OpenRGB: デバイスの一覧が変わったので繋ぎ直します");
                return;
            }

            bool syncTest = _syncTest;
            bool animated = channels.Count > 0 && (RgbEffects.IsAnimated(cfg.Mode) || RgbLightModes.AnyAnimated(cfg) || (emergency && cfg.BlinkOnEmergency) || syncTest);
            double period = animated ? AnimatedPeriod : StaticPeriod;
            now = AudioClock.Now;
            cadence.SetPeriod(period, now);
            clock.SetFineResolution(animated);

            if (!cadence.IsDue(now))
            {
                clock.Wait(cadence.Next - now);
                continue;
            }

            double tNow = cadence.Begin(now);
            RenderTick(client, channels, cfg, temp, emergency, syncTest, animated, tNow, period);
            cadence.Advance();
            renderFps.Count();

            if (tNow >= nextModeRefresh)
            {
                // モードを確かめ、ほかのソフト（OpenRGB の画面・エフェクトなど）に変えられていたときだけ送り直す。
                // 確かめるのは読むだけなので LED は乱れない（以前は 10 秒ごとに無条件で送り直していて、
                // OpenRGB 1.0 以降ではそのたびにマザーボードやキーボードが一瞬乱れることがあった）。
                // 問い合わせるだけで返事は待たない（返事は PollIncoming が受け取り、届いたコマで確かめる）
                foreach (var ch in channels)
                {
                    if (ch.DirectModeName.Length > 0 && !ch.LearnDirectMode) client.RequestActiveMode(ch.Device.Index);
                }
                nextModeRefresh = tNow + ModeRefreshInterval;
            }
            if (client.HasModeReplies) CheckModes(client, channels);
            if (now >= nextStatus)
            {
                nextStatus = now + StatusEverySec;
                // 直接書き込みに失敗し続けたグラボは、OpenRGB を通す作り方に切り替える
                if (channels.Exists(c => c.Direct is { Faulted: true })) RebuildChannels(client, devices, cfg, channels, missing, now);
                if (client.IsClosedByServer()) throw new IOException(L("OpenRGB との接続が切れました"));
                PublishStatus(h, channels, missing, renderFps.Sample(now), animated, now);
            }
        }
    }

    /// <summary>1 コマ: デバイスごとに入力を用意して描き、送るタイミングで変化があれば送る</summary>
    private void RenderTick(OpenRgbClient client, List<RgbDeviceChannel> channels, RgbConfig cfg, double? temp, bool emergency,
        bool syncTest, bool animated, double tNow, double tick)
    {
        AudioEngine? engine = null;
        if (cfg.Mode == RgbModes.Music)
        {
            try { engine = AudioSource?.Invoke(); }
            catch { engine = null; }
        }
        var beat = engine?.Beat ?? BeatState.None;
        bool whiteTest = _whiteTest;
        // 音楽連動で「遅いデバイスをほかのデバイスの色に合わせる」ときは、速いデバイスを先に描いて、その色から代表の色を作る
        bool follow = cfg.Mode == RgbModes.Music && cfg.MatchSlowDevices && !whiteTest && !syncTest && !emergency;
        var scene = new SceneColor();
        bool haveLeader = false;
        for (int pass = 0; pass < 2; pass++)
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            bool follower = follow && IsFollower(ch, cfg);
            // 1 回目は速いデバイス（合わせる元）、2 回目は合わせるデバイス。合わせないときは 1 回目で全部描く
            if (pass == 0 ? follower : !follower) continue;
            ch.Input.WhiteTest = whiteTest;
            ch.Input.FollowColor = null;
            if (follower && haveLeader && scene.TryGet(out var sceneColor, out float sceneLevel))
            {
                ch.Input.FollowColor = sceneColor;
                ch.Input.FollowLevel = sceneLevel / (float)Math.Max(0.1, Math.Clamp(cfg.Brightness, 0, 100) / 100);
            }
            ch.Prepare(tNow, tick, engine?.Features, engine?.Events, beat, temp, emergency, syncTest);
            if (!ch.Render()) continue;
            if (follow && !follower && !ch.WhiteOnly && !(ch.Input.Device?.HasLightOverride ?? false))   // 白だけのキーボードは色を持たないので、合わせる元にしない（光り方を上書きしたデバイスも、全体の色ではないので合わせる元にしない）
            {
                scene.Add(ch.Renderer.LastLight);
                haveLeader = true;
            }
            if (i == 0 && ch.Frame.Length > 0)
            {
                var c = ch.Frame[0];
                Volatile.Write(ref _currentColor, (c.R << 16) | (c.G << 8) | c.B);
            }
            var reason = ch.ShouldSend(tNow, tick, animated);
            if (reason == SendReason.None) continue;
            double ms = 0;
            if (ch.Direct is { } gpu) gpu.Submit(ch.Frame);   // 書き込みは専用のスレッド（待たない）
            else ms = client.UpdateLeds(ch.Device.Index, ch.Frame, ch.SwapRedGreen);
            ch.MarkSent(tNow, tick, reason, ms);
        }
    }

    /// <summary>
    /// 届いたモードの返事を確かめる: 直接色を指定するモードを送った直後のデバイスは、その名前を覚える。
    /// ほかのデバイスは、覚えた名前と違っていたら（ほかのソフトに変えられた）送り直す。
    /// 一覧が変わった（番号がずれているかもしれない）ときは何もしない（次のコマで繋ぎ直す）
    /// </summary>
    private static void CheckModes(OpenRgbClient client, List<RgbDeviceChannel> channels)
    {
        if (client.DeviceListChanged) return;
        foreach (var ch in channels)
        {
            if (!client.TakeActiveMode(ch.Device.Index, out string mode)) continue;
            if (ch.LearnDirectMode)
            {
                ch.DirectModeName = mode;
                ch.LearnDirectMode = false;
                continue;
            }
            if (mode.Length == 0 || ch.DirectModeName.Length == 0 || mode.Equals(ch.DirectModeName, StringComparison.OrdinalIgnoreCase)) continue;
            Logger.Info($"OpenRGB: {ch.Device.Name} のモードが「{mode}」に変わっていたため、直接色を指定するモードに戻します");
            client.SetDirectMode(ch.Device.Index);
        }
        client.ClearModeReplies();   // 対象外のデバイスの返事は捨てる
    }

    /// <summary>
    /// 対象のデバイスを選び直す。続けて対象のデバイスはそのまま（光り方の状態を保つ）、
    /// 新しく対象になったデバイスは直接色を指定するモードにし、外れたデバイスは「止めるときの光り方」にする
    /// </summary>
    private void RebuildChannels(OpenRgbClient client, List<RgbDevice> devices, RgbConfig cfg, List<RgbDeviceChannel> channels,
        List<string> missing, double now)
    {
        var targets = SelectTargets(devices, cfg.Devices, missing);
        var kept = new List<RgbDeviceChannel>(targets.Count);
        bool force = _forceDirectOnce, created = false;
        foreach (var d in targets)
        {
            RgbDeviceChannel? ch = null;
            foreach (var c in channels)
            {
                if (c.Device.Index == d.Index) { ch = c; break; }
            }
            // 直接書き込みをオフにした・失敗し続けたグラボは、OpenRGB を通すデバイスとして作り直す
            if (ch?.Direct is { } old && (!cfg.DirectGpuWrite || old.Faulted || !PnyEpicXGpu.Allowed(d, cfg.DirectGpuUnverified) || (!cfg.DirectGpuUnverified && !old.BoardVerified)))
            {
                old.Flush(StopFlushMs);   // 書きかけのコマを書き終えてから OpenRGB に任せる（同時に書かない）
                ch = null;
            }
            // 直接書き込みをオンに戻した → OpenRGB を通していたグラボを、直接書くデバイスとして作り直す
            if (ch is { Direct: null } && cfg.DirectGpuWrite && PnyEpicXGpu.Allowed(d, cfg.DirectGpuUnverified)) ch = null;
            if (ch is null && cfg.DirectGpuWrite && !PnyEpicXGpu.Allowed(d, cfg.DirectGpuUnverified) && PnyEpicXGpu.IsCandidate(d, out _) && _unverifiedLogged.Add(d.Name))
                Logger.Info($"OpenRGB: {d.Name} は直接書き込みを確かめていない型番なので、OpenRGB を通して光らせます（ライティング →「詳細」で直接書き込みを試せます）");
            if (ch is null && cfg.DirectGpuWrite && PnyEpicXGpu.Allowed(d, cfg.DirectGpuUnverified) && PnyEpicXGpu.TryOpen(d, cfg.DirectGpuUnverified) is { } gpu)
            {
                // PNY Epic-X のグラボは OpenRGB を通さず NvAPI で直接書く（OpenRGB は LED 1 個ずつ 41 回書くので遅い）。
                // OpenRGB にはモードも色も送らない（OpenRGB がこのグラボに書き込まないようにする）
                ch = new RgbDeviceChannel(d with { DirectGpu = true }, now) { SharedColor = _sharedColor, Direct = gpu };
                created = true;
                Logger.Info($"OpenRGB: {d.Name} は OpenRGB を通さず直接書き込みます（NvAPI）");
            }
            if (ch is null)
            {
                ch = new RgbDeviceChannel(d, now) { SharedColor = _sharedColor };
                // 繋いだら、直接色を指定するモード（Direct）を 1 回送る。Direct と表示されていても送る:
                // OpenRGB はモードを送られたときに初めて Direct 用の準備をするデバイスがある
                // （ASRock のマザーボードは、送らないと赤と緑の入れ替えが残り、色を送るたびにちらつくことがある）。
                // ちかちかしやすいデバイス（ASRock の Radeon）は、モードを送るたびに一瞬乱れるので、
                // Direct でないとき・スリープからの復帰・「再接続」ボタンのときだけ送る
                bool alreadyDirect = d.ActiveMode.Equals("Direct", StringComparison.OrdinalIgnoreCase);
                bool send = !d.IsFlickerProne || !alreadyDirect || force;
                created = true;
                if (send && !client.DeviceListChanged)
                {
                    client.SetDirectMode(d.Index);
                    // そのモードの名前（ふつうは Direct。デバイスによっては Custom など）を返事から覚える（待たない）
                    client.RequestActiveMode(d.Index);
                    ch.LearnDirectMode = true;
                }
                else
                {
                    ch.DirectModeName = alreadyDirect ? d.ActiveMode : "";
                }
            }
            kept.Add(ch);
        }
        var removed = new List<RgbDeviceChannel>();
        foreach (var c in channels)
        {
            // 同じデバイスを作り直しただけ（直接書き込み → OpenRGB）なら、止めるときの光り方は送らない
            if (!kept.Contains(c) && !kept.Exists(k => k.Device.Index == c.Device.Index)) removed.Add(c);
        }
        // 送り直しの依頼は、繋ぎ直してデバイスを作ったときに使い切る（デバイスがまだ見つからなければ、見つかるまで残す）
        if (force && created) _forceDirectOnce = false;
        channels.Clear();
        channels.AddRange(kept);
        _published = channels.ToArray();
        if (removed.Count > 0) SendStopFrames(client, removed, cfg, double.PositiveInfinity);
    }

    /// <summary>止める合図が来たとき: Stop() などが頼んだ「止めるときの光り方」を送る（0.5 秒以内・例外は出さない）</summary>
    private void ApplyStopRequest(LoopHandle h, OpenRgbClient client, List<RgbDeviceChannel> channels)
    {
        var req = h.Stop;
        if (req is null) return;
        var current = _loop;
        if (current is not null && !ReferenceEquals(current, h)) return; // もう新しいスレッドが送っている
        try
        {
            SendStopFrames(client, channels, req.Cfg, req.Deadline);
        }
        catch (Exception ex)
        {
            Logger.Warn($"OpenRGB: 止めるときの光り方を送れませんでした: {OpenRgbClient.Describe(ex)}");
        }
    }

    /// <summary>デバイスに「止めるときの光り方」を送る（Keep なら何もしない。deadline を過ぎたら残りは送らない）</summary>
    private static void SendStopFrames(OpenRgbClient client, List<RgbDeviceChannel> channels, RgbConfig cfg, double deadline)
    {
        foreach (var ch in channels)
        {
            if (AudioClock.Now > deadline) break;
            if (!StopFrame(cfg, ch.Frame)) return;
            if (ch.WhiteOnly) RgbDeviceChannel.ToWhite(ch.Frame);   // 白だけのキーボードは、止めるときの色も明るさにする
            if (ch.Direct is { } gpu)
            {
                gpu.Submit(ch.Frame);
                double left = double.IsInfinity(deadline) ? StopFlushMs : (deadline - AudioClock.Now) * 1000;
                gpu.Flush((int)Math.Clamp(left, 0, StopFlushMs));
                continue;
            }
            client.UpdateLeds(ch.Device.Index, ch.Frame, cfg.SwapRedGreen?.Contains(ch.Device.Name) == true);
        }
    }

    /// <summary>表示用の状態（全体の文章・デバイスごとの状態）を更新する（1 秒に 2 回）</summary>
    private void PublishStatus(LoopHandle h, List<RgbDeviceChannel> channels, List<string> missing, double fps, bool animated, double now)
    {
        if (!IsCurrent(h)) return;
        var list = new RgbDeviceStatus[channels.Count + missing.Count];
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            list[i] = new RgbDeviceStatus(ch.Device.Name, ch.Device.LedCount, ch.Device.IsSlow, Math.Round(ch.Fps.Sample(now), 1),
                ch.Renderer.ActiveEffect, ch.StatusNote(now));
        }
        for (int i = 0; i < missing.Count; i++)
            list[channels.Count + i] = new RgbDeviceStatus(missing[i], 0, false, 0, "", L("OpenRGB に見つかりません"));
        _deviceStatus = list;

        string notFound = missing.Count > 0 ? string.Join(L("、"), missing) : "";
        string text;
        if (channels.Count == 0)
        {
            text = missing.Count > 0
                ? L($"選んだデバイスが見つかりません: {notFound}（OpenRGB の画面に表示されているか確認してください）")
                : L("接続できましたが、色を変えられるデバイスが見つかりません。OpenRGB の画面にデバイスが表示されているか確認してください");
        }
        else
        {
            text = L($"接続中: {channels.Count} 個のデバイスの色を変えています");
            if (animated && fps > 0) text += L($"（1 秒に {fps:0} コマ）");
            if (missing.Count > 0) text += L($"。選んだデバイスが見つかりません: {notFound}");
        }
        Status = text;
    }

    /// <summary>接続が切れたとき: デバイスごとの状態を「0 回／秒・エラー」にする</summary>
    private void PublishDisconnected(LoopHandle h, string message)
    {
        if (!IsCurrent(h)) return;
        var old = _deviceStatus;
        var list = new RgbDeviceStatus[old.Count];
        for (int i = 0; i < old.Count; i++) list[i] = old[i] with { Fps = 0, LastError = message };
        _deviceStatus = list;
    }

    /// <summary>対象のデバイスの選び方が変わったかを見分けるための文字列</summary>
    /// <summary>
    /// ほかのデバイスの色に合わせるデバイスか: 更新の遅いデバイス（メモリ・OpenRGB を通すグラボ）で、
    /// デバイスごとの光り方を選んでいない（自動の）もの。直接書き込むグラボは速いので合わせない
    /// </summary>
    public static bool IsFollower(RgbDeviceChannel ch, RgbConfig cfg)
    {
        if (!(ch.Device.IsSlow || ch.Device.IsLowWrite)) return false;
        if (ch.Input.Device?.LightMode is { Length: > 0 }) return false;   // デバイスごとの光り方（固定色など）にしたデバイスは合わせない
        string effect = ch.Input.Device?.Effect ?? "Auto";
        return string.IsNullOrEmpty(effect) || effect == EffectCatalog.Auto;
    }

    /// <summary>ログ用のデバイスの一覧（名前・LED の数・つながっている場所）</summary>
    public static string DescribeDevices(IReadOnlyList<RgbDevice> devices) =>
        string.Join(L("、"), devices.Select(d => L($"{d.Name}（LED {d.LedCount} 個・{(d.Location.Length > 0 ? d.Location : L("場所不明"))}）")));

    private static string SelectionKey(RgbConfig cfg) =>
        (cfg.DirectGpuWrite ? "direct\n" : "openrgb\n") + (cfg.Devices is { Count: > 0 } d ? string.Join("\n", d) : "");

    /// <summary>デバイスの設定（なければ既定値。設定の一覧は書き換えない）</summary>
    private static RgbDeviceSettings SettingsFor(RgbConfig cfg, string name)
    {
        try
        {
            if (cfg.DeviceSettings is not null) return cfg.SettingsFor(name) ?? new RgbDeviceSettings { Name = name };
        }
        catch
        {
            // 設定の一覧に null が混ざっている、など
        }
        return new RgbDeviceSettings { Name = name };
    }
}
