// ==================================================
// MediaSession.cs - Windows の「メディアの情報」（音量ボタンを押すと出る曲名）から、いま流れている曲を読む
// Dependencies: .NET 8 / Windows 10 1809 以降の Windows.Media.Control (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProFanController;

/// <summary>メディアの情報の 1 つ（再生アプリ 1 つ分）</summary>
public readonly record struct MediaSessionEntry(string AppId, bool Playing, bool Current);

/// <summary>
/// 曲の記憶が曲を見分けるために、いま流れている曲の「アーティスト - 曲名」を読む。
/// Spotify のようにウィンドウのタイトルに曲名を出すアプリだけでなく、YouTube Music（ブラウザー）・Apple Music などでも使える。
/// Windows.Media.Control（WinRT）を、Windows の SDK の部品（約 25MB）を入れずに、combase の関数と表（vtable）で直接呼ぶ。
/// 表の並びは Windows.Media.winmd から写したもの。呼ぶのは 1 秒に 1 回、UI ではないスレッドから
/// </summary>
public static class MediaSession
{
    /// <summary>曲の記憶の印を、前からの Spotify のウィンドウのタイトルと同じにするための名前</summary>
    public const string SpotifyApp = "Spotify";

    // ---- 見分け方（テストできる部分）----

    /// <summary>
    /// どの再生アプリの曲を使うか。流れているものだけを見る（止めている曲は覚えない）。
    /// preferApp（音を取り込んでいるアプリの名前）があれば、その名前を含むアプリを先に。次に Windows が「いま」とするもの、なければ最初に流れているもの
    /// </summary>
    public static int Pick(IReadOnlyList<MediaSessionEntry> sessions, string? preferApp)
    {
        if (!string.IsNullOrWhiteSpace(preferApp))
            for (int i = 0; i < sessions.Count; i++)
                if (sessions[i].Playing && sessions[i].AppId.Contains(preferApp, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].Playing && sessions[i].Current) return i;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].Playing) return i;
        return -1;
    }

    /// <summary>
    /// 曲の印のもとになる（アプリ, 「アーティスト - 曲名」）。曲名がなければ null。
    /// Spotify は、前から使っているウィンドウのタイトル（「アーティスト - 曲名」）と同じ形にして、覚えた曲がそのまま使えるようにする
    /// </summary>
    public static (string App, string Title)? SongOf(string appId, string? artist, string? title)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        artist = artist?.Trim();
        string text = string.IsNullOrEmpty(artist) ? title : $"{artist} - {title}";
        string app = appId.Contains("spotify", StringComparison.OrdinalIgnoreCase) ? SpotifyApp : appId;
        return (app, text);
    }

    // ---- Windows から読む ----

    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    private static readonly Guid IidManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    // 表の位置（0〜5 は IUnknown と IInspectable）
    private const int StaticsRequestAsync = 6;
    private const int ManagerGetCurrentSession = 6, ManagerGetSessions = 7;
    private const int SessionAppId = 6, SessionGetMediaPropertiesAsync = 7, SessionGetPlaybackInfo = 9;
    private const int PlaybackStatusSlot = 7;
    private const int PropsTitle = 6, PropsArtist = 9;
    private const int VectorGetAt = 6, VectorSize = 7;
    private const int AsyncGetResults = 8, AsyncInfoStatus = 7;
    private const int StatusPlaying = 4;

    private static readonly object Gate = new();
    private static IntPtr _manager;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPtr(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutInt(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutUInt(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IndexOutPtr(IntPtr self, uint index, out IntPtr value);

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    /// <summary>
    /// いま流れている曲（アプリ, 「アーティスト - 曲名」）。流れていなければ null。
    /// メディアの情報を使えない（古い Windows など）ときは InvalidOperationException
    /// </summary>
    public static (string App, string Title)? Current(string? preferApp)
    {
        // スレッドプールのスレッドで呼ぶので、COM を MTA で使えるようにしておく（すでに使えるときは数を合わせるだけ）
        int init = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        try
        {
            IntPtr manager = Manager();
            var sessions = new List<IntPtr>();
            var entries = new List<MediaSessionEntry>();
            IntPtr current = IntPtr.Zero, vector = IntPtr.Zero;
            try
            {
                Call<OutPtr>(manager, ManagerGetCurrentSession)(manager, out current).Ok();
                Call<OutPtr>(manager, ManagerGetSessions)(manager, out vector).Ok();
                Call<OutUInt>(vector, VectorSize)(vector, out uint count).Ok();
                for (uint i = 0; i < count && i < 32; i++)
                {
                    Call<IndexOutPtr>(vector, VectorGetAt)(vector, i, out IntPtr s).Ok();
                    if (s == IntPtr.Zero) continue;
                    sessions.Add(s);
                    entries.Add(new MediaSessionEntry(AppIdOf(s), IsPlaying(s), current != IntPtr.Zero && SameObject(s, current)));
                }
                int pick = Pick(entries, preferApp);
                if (pick < 0) return null;
                return ReadSong(sessions[pick], entries[pick].AppId);
            }
            catch
            {
                // メディアのサービスが起動し直すと、持っている manager はもう使えない（RPC_E_DISCONNECTED など）。次は取り直す
                Reset(manager);
                throw;
            }
            finally
            {
                foreach (var s in sessions) Marshal.Release(s);
                if (vector != IntPtr.Zero) Marshal.Release(vector);
                if (current != IntPtr.Zero) Marshal.Release(current);
            }
        }
        finally
        {
            if (init >= 0) CoUninitialize();   // S_OK・S_FALSE のときだけ数を戻す
        }
    }

    /// <summary>失敗したあと、次に試すまで待つ時間</summary>
    public const long RetryMs = 60_000;

    /// <summary>
    /// 失敗したあと次に試す時刻（Environment.TickCount64）。
    /// 必要なものが Windows にない（DLL・関数がない）ときは、もう試さない（long.MaxValue）。ほかは RetryMs 後に試し直す
    /// </summary>
    public static long NextTry(long now, Exception error) =>
        error is DllNotFoundException or EntryPointNotFoundException ? long.MaxValue : now + RetryMs;

    /// <summary>持っている manager を捨てる（ほかのスレッドが取り直していたら、そちらは残す）</summary>
    private static void Reset(IntPtr stale)
    {
        lock (Gate)
        {
            if (_manager == IntPtr.Zero || _manager != stale) return;
            Marshal.Release(_manager);
            _manager = IntPtr.Zero;
        }
    }

    private static IntPtr Manager()
    {
        lock (Gate)
        {
            if (_manager != IntPtr.Zero) return _manager;
            Check(WindowsCreateString(ManagerClass, ManagerClass.Length, out IntPtr name));
            IntPtr statics, op = IntPtr.Zero;
            try
            {
                var iid = IidManagerStatics;
                Check(RoGetActivationFactory(name, ref iid, out statics));
            }
            finally { WindowsDeleteString(name); }
            try
            {
                Call<OutPtr>(statics, StaticsRequestAsync)(statics, out op).Ok();
                _manager = Await(op);
                return _manager;
            }
            finally
            {
                if (op != IntPtr.Zero) Marshal.Release(op);
                Marshal.Release(statics);
            }
        }
    }

    private static (string App, string Title)? ReadSong(IntPtr session, string appId)
    {
        Call<OutPtr>(session, SessionGetMediaPropertiesAsync)(session, out IntPtr op).Ok();
        IntPtr props = IntPtr.Zero;
        try
        {
            props = Await(op);
            if (props == IntPtr.Zero) return null;
            return SongOf(appId, ReadString(props, PropsArtist), ReadString(props, PropsTitle));
        }
        finally
        {
            if (props != IntPtr.Zero) Marshal.Release(props);
            Marshal.Release(op);
        }
    }

    private static string AppIdOf(IntPtr session) => ReadString(session, SessionAppId);

    private static bool IsPlaying(IntPtr session)
    {
        if (Call<OutPtr>(session, SessionGetPlaybackInfo)(session, out IntPtr info) < 0 || info == IntPtr.Zero) return false;
        try { return Call<OutInt>(info, PlaybackStatusSlot)(info, out int status) >= 0 && status == StatusPlaying; }
        finally { Marshal.Release(info); }
    }

    private static bool SameObject(IntPtr a, IntPtr b)
    {
        var iid = new Guid("00000000-0000-0000-c000-000000000046");   // IUnknown で比べる
        if (Marshal.QueryInterface(a, ref iid, out IntPtr ua) < 0) return false;
        try
        {
            if (Marshal.QueryInterface(b, ref iid, out IntPtr ub) < 0) return false;
            try { return ua == ub; } finally { Marshal.Release(ub); }
        }
        finally { Marshal.Release(ua); }
    }

    private static string ReadString(IntPtr obj, int slot)
    {
        if (Call<OutPtr>(obj, slot)(obj, out IntPtr h) < 0 || h == IntPtr.Zero) return "";
        try
        {
            IntPtr buf = WindowsGetStringRawBuffer(h, out uint len);
            return buf == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buf, (int)len);
        }
        finally { WindowsDeleteString(h); }
    }

    /// <summary>IAsyncOperation の終わりを待って結果を受け取る（終わらなければ 2 秒であきらめる）</summary>
    private static IntPtr Await(IntPtr op)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(op, ref iid, out IntPtr info));
        try
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Call<OutInt>(info, AsyncInfoStatus)(info, out int status).Ok();
                if (status == 1) break;                                    // Completed
                if (status >= 2) throw new InvalidOperationException($"media info: async status {status}");   // Canceled・Error
                if (sw.ElapsedMilliseconds > 2000) throw new InvalidOperationException("media info: timed out");
                Thread.Sleep(5);
            }
        }
        finally { Marshal.Release(info); }
        Call<OutPtr>(op, AsyncGetResults)(op, out IntPtr result).Ok();
        return result;
    }

    private static T Call<T>(IntPtr obj, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void Check(int hr)
    {
        if (hr < 0) throw new InvalidOperationException($"media info: 0x{hr:X8}");
    }

    private static void Ok(this int hr) => Check(hr);
}
