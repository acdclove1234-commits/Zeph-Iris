// ==================================================
// NvApiI2c.cs - NVIDIA のグラボの I2C に直接書き込む (NvAPI の I2CWriteEx。OpenRGB の i2c_smbus_nvapi と同じ呼び方)
// Dependencies: .NET 8 (MIT License), NVIDIA ドライバーの nvapi64.dll
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;

namespace ProFanController;

/// <summary>I2C のブロック書き込み（レジスタ 1 バイト + データ）。テストでは偽物に差し替える</summary>
public interface II2cBlockWriter
{
    /// <summary>書けたら true</summary>
    bool WriteBlock(byte register, ReadOnlySpan<byte> data);
}

/// <summary>
/// NvAPI でグラボの I2C（ポート 1 = RGB のコントローラーがつながっているところ）に書く。
/// nvapi_QueryInterface で関数を探す（NVIDIA が公開していない関数なので、番号は OpenRGB と同じものを使う）。
/// 1 つのスレッドからだけ使う（書き込み用のバッファを使い回す）
/// </summary>
public sealed class NvApiI2c : II2cBlockWriter, IDisposable
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetPciIdentifiers = 0x2DDFB66E;
    private const uint IdI2CWriteEx = 0x283AC65A;
    private const int MaxGpus = 64;
    private const int MaxData = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct I2CInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DevAddress;
        public IntPtr RegAddress;
        public uint RegAddrSize;
        public IntPtr Data;
        public uint Size;
        public uint Speed;
        public uint SpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusFn([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPciIdentifiersFn(IntPtr gpu, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int I2CWriteExFn(IntPtr gpu, ref I2CInfoV3 info, out uint unknown);

    private readonly IntPtr _gpu;
    private readonly byte _address;
    private readonly I2CWriteExFn _write;
    private readonly IntPtr _buffer;   // 先頭 1 バイトがレジスタ、続きがデータ
    private readonly byte[] _scratch = new byte[1 + MaxData];
    private bool _disposed;

    private NvApiI2c(IntPtr gpu, byte address, I2CWriteExFn write)
    {
        _gpu = gpu;
        _address = address;
        _write = write;
        _buffer = Marshal.AllocHGlobal(1 + MaxData);
    }

    /// <summary>グラボの PCI のサブシステムのベンダー番号（PNY = 0x196E など。開いたときに確かめた値）</summary>
    public ushort SubVendor { get; private init; }

    /// <summary>PCI の番号（上位 16 ビット = デバイス、下位 = ベンダー 0x10DE）と、サブシステムの番号（上位 = 基板、下位 = ベンダー）</summary>
    public uint DeviceId { get; private init; }
    public uint SubSystemId { get; private init; }

    /// <summary>
    /// NvAPI の gpuIndex 番目のグラボ（OpenRGB の「Nvidia NvAPI I2C on GPU n」と同じ番号）の、I2C アドレス address を開く。
    /// サブシステムのベンダー番号が subVendor と違う・NvAPI が使えないときは null
    /// </summary>
    public static NvApiI2c? Open(int gpuIndex, byte address, ushort subVendor)
    {
        try
        {
            var init = Get<InitializeFn>(IdInitialize);
            var enumGpus = Get<EnumPhysicalGpusFn>(IdEnumPhysicalGpus);
            var pci = Get<GetPciIdentifiersFn>(IdGetPciIdentifiers);
            var write = Get<I2CWriteExFn>(IdI2CWriteEx);
            if (init is null || enumGpus is null || pci is null || write is null || init() != 0) return null;
            var handles = new IntPtr[MaxGpus];
            if (enumGpus(handles, out int count) != 0 || gpuIndex < 0 || gpuIndex >= count) return null;
            if (pci(handles[gpuIndex], out uint dev, out uint sub, out _, out _) != 0 || (sub & 0xFFFF) != subVendor) return null;
            return new NvApiI2c(handles[gpuIndex], address, write) { SubVendor = (ushort)(sub & 0xFFFF), DeviceId = dev, SubSystemId = sub };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static T? Get<T>(uint id) where T : Delegate
    {
        IntPtr p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public bool WriteBlock(byte register, ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length > MaxData) return false;
        _scratch[0] = register;
        data.CopyTo(_scratch.AsSpan(1));
        Marshal.Copy(_scratch, 0, _buffer, 1 + data.Length);
        var info = new I2CInfoV3
        {
            Version = (uint)Marshal.SizeOf<I2CInfoV3>() | (3u << 16),
            IsDdcPort = 0,
            DevAddress = (byte)(_address << 1),
            RegAddress = _buffer,
            RegAddrSize = 1,
            Data = _buffer + 1,
            Size = (uint)data.Length,
            Speed = 0xFFFF,
            SpeedKhz = 0,       // 既定の速さ
            PortId = 1,
            IsPortIdSet = 1,
        };
        return _write(_gpu, ref info, out _) == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.FreeHGlobal(_buffer);
    }
}
