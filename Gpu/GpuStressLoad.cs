// ==================================================
// GpuStressLoad.cs - 自動セットアップで GPU に負荷をかける (Direct3D 11 の計算シェーダーで、積和演算を回し続ける)
// Dependencies: Vortice.Direct3D11 / Vortice.D3DCompiler / Vortice.DXGI (MIT License), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ProFanController;

/// <summary>
/// GPU を発熱させる負荷。いちばんメモリの多い GPU（ふつうはグラボ）で、計算シェーダーの積和演算を回し続ける。
/// 1 回の計算は数ミリ秒で終わる大きさにして、2 回分を交互に積むので、GPU は休まず、画面も固まらない
/// （長い計算 1 回にすると、Windows が GPU の応答なしとみなしてドライバーをリセットするため）
/// </summary>
public sealed class GpuStressLoad : ILoadGenerator, IDisposable
{
    /// <summary>1 回の計算のスレッドのまとまりの数（× 256 スレッド）</summary>
    private const int Groups = 8192;
    /// <summary>1 回の計算で積む回数</summary>
    private const int Batch = 4;

    private const string Shader = @"
RWStructuredBuffer<float> Output : register(u0);
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float a = id.x * 1e-7, b = a + 0.1, c = a + 0.2, d = a + 0.3;
    [loop]
    for (uint i = 0; i < 4096; i++)
    {
        a = mad(a, 0.999999, 0.000001); b = mad(b, 0.999999, 0.000001);
        c = mad(c, 0.999999, 0.000001); d = mad(d, 0.999999, 0.000001);
        a = mad(a, b, -c * 0.5);       b = mad(b, c, -d * 0.5);
        c = mad(c, d, -a * 0.5);       d = mad(d, a, -b * 0.5);
    }
    Output[id.x] = a + b + c + d;
}";

    private Thread? _thread;
    private volatile bool _stop;
    private volatile string? _error;

    public string? Error => _error;
    /// <summary>負荷をかけている GPU の名前（記録用）</summary>
    public string AdapterName { get; private set; } = "";
    public bool Running => _thread is not null;

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _error = null;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "GpuStress" };
        _thread.Start();
    }

    public void Stop()
    {
        var t = _thread;
        if (t is null) return;
        _stop = true;
        t.Join(5000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        try
        {
            using var adapter = PickAdapter();
            if (adapter is null)
            {
                _error = L("負荷をかけられる GPU が見つかりません");
                return;
            }
            AdapterName = adapter.Description1.Description;
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0 }, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
            using var dev = device!;
            using var ctx = context!;

            var bytecode = Compiler.Compile(Shader, "main", "GpuStress.hlsl", "cs_5_0");
            using var cs = dev.CreateComputeShader(bytecode.Span);
            int count = Groups * 256;
            using var buffer = dev.CreateBuffer(new BufferDescription((uint)(count * sizeof(float)), BindFlags.UnorderedAccess,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)sizeof(float)));
            using var uav = dev.CreateUnorderedAccessView(buffer);
            using var q0 = dev.CreateQuery(new QueryDescription(QueryType.Event));
            using var q1 = dev.CreateQuery(new QueryDescription(QueryType.Event));
            var queries = new[] { q0, q1 };

            ctx.CSSetShader(cs);
            ctx.CSSetUnorderedAccessView(0, uav);
            Logger.Info($"GPU の負荷を開始しました（{AdapterName}）");

            // 2 回分を交互に積む: 一方の終わりを待つ間も、もう一方を GPU が計算している
            for (int k = 0; k < 2; k++) Submit(ctx, queries[k]);
            int next = 0;
            while (!_stop)
            {
                var q = queries[next];
                while (!_stop && !IsDone(ctx, q)) Thread.Sleep(1);
                if (_stop) break;
                Submit(ctx, q);
                next ^= 1;
            }
            // 積んである計算が終わるのを待ってから片付ける（終わる前に消すと、ドライバーが待たされる）
            foreach (var q in queries)
            {
                var until = DateTime.UtcNow.AddSeconds(2);
                while (!IsDone(ctx, q) && DateTime.UtcNow < until) Thread.Sleep(1);
            }
            ctx.ClearState();
            Logger.Info("GPU の負荷を止めました");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Logger.Warn($"GPU の負荷をかけられません: {ex.Message}");
        }
    }

    private static void Submit(ID3D11DeviceContext ctx, ID3D11Query q)
    {
        for (int i = 0; i < Batch; i++) ctx.Dispatch(Groups, 1, 1);
        ctx.End(q);
        ctx.Flush();
    }

    private static bool IsDone(ID3D11DeviceContext ctx, ID3D11Query q)
        => ctx.GetData(q, IntPtr.Zero, 0, AsyncGetDataFlags.None) .Code == 0; // S_OK: 終わった（S_FALSE: まだ計算中）

    /// <summary>ソフトウェアの描画（Microsoft Basic Render Driver）を除いて、専用メモリがいちばん多い GPU</summary>
    private static IDXGIAdapter1? PickAdapter()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using var f = factory!;
        IDXGIAdapter1? best = null;
        ulong bestMem = 0;
        for (uint i = 0; f.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var d = adapter.Description1;
            if ((d.Flags & AdapterFlags.Software) != 0 || d.VendorId == 0x1414 || (ulong)d.DedicatedVideoMemory <= bestMem)
            {
                adapter.Dispose();
                continue;
            }
            best?.Dispose();
            best = adapter;
            bestMem = (ulong)d.DedicatedVideoMemory;
        }
        return best;
    }
}
