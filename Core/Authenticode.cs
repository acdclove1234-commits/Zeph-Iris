// ==================================================
// Authenticode.cs - exe の電子署名（Authenticode）を Windows に確かめてもらう・署名した人を読む
//   更新のインストーラー（いまのアプリと同じ発行元か）と、同梱の PresentMon（Intel の署名か）に使う
// Dependencies: .NET 8 (MIT License) / Windows WinTrust API
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ProFanController;

public enum SignState
{
    /// <summary>署名がない</summary>
    Unsigned,
    /// <summary>署名があり、Windows が正しいと認めた</summary>
    Valid,
    /// <summary>署名はあるが、壊れている・信頼できない・書き換えられている</summary>
    Invalid,
}

/// <summary>署名の様子と、署名した人（証明書の Subject。署名がなければ null）</summary>
public sealed record SignInfo(SignState State, string? Subject)
{
    public static readonly SignInfo None = new(SignState.Unsigned, null);
}

public static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2, RevokeNone = 0, ChoiceFile = 1, ActionVerify = 1, ActionClose = 2, CacheOnlyUrlRetrieval = 0x1000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint Size;
        public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProvFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

    /// <summary>
    /// ファイルの署名を確かめる。失効の確認はネットにつながないで済む範囲だけ（更新の途中で長く待たないように）。
    /// ファイルが読めないときは Invalid
    /// </summary>
    public static SignInfo Inspect(string path)
    {
        int result;
        var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = filePtr,
                StateAction = ActionVerify,
                ProvFlags = CacheOnlyUrlRetrieval,
            };
            result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            data.StateAction = ActionClose;
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new SignInfo(SignState.Invalid, null);
        }
        finally
        {
            Marshal.DestroyStructure<FileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
        if (result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown) return SignInfo.None;
        return new SignInfo(result == 0 ? SignState.Valid : SignState.Invalid, Signer(path));
    }

    /// <summary>署名した人（証明書の Subject）。読めなければ null</summary>
    public static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // 署名つきのファイルから証明書を読む API（.NET 8 で使える）
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>更新のインストーラーの署名を、いまのアプリの署名とくらべて決める</summary>
public static class SignaturePolicy
{
    /// <summary>
    /// インストーラーを使ってよいか（よければ null、だめなら理由の文）。
    ///   ・インストーラーの署名が壊れていれば、いつもだめ
    ///   ・いまのアプリが署名つきなら、インストーラーも正しく署名されていて、署名した人が同じであること
    ///     （署名つきの版からは、署名のない版・別の人が署名した版に入れ替えない）
    ///   ・いまのアプリに署名がなければ（署名を始める前の版）、署名のないインストーラーも使える
    /// </summary>
    public static string? Problem(SignInfo current, SignInfo installer)
    {
        if (installer.State == SignState.Invalid)
            return L("インストーラーの電子署名が正しくありません（壊れているか、書き換えられています）。更新を中止しました。");
        if (current.State != SignState.Valid) return null;
        if (installer.State != SignState.Valid)
            return L("いまのアプリは電子署名つきですが、ダウンロードしたインストーラーには署名がありません。更新を中止しました。");
        if (!string.Equals(Normalize(current.Subject), Normalize(installer.Subject), StringComparison.OrdinalIgnoreCase))
            return L("ダウンロードしたインストーラーの署名者が、いまのアプリの署名者と違います。更新を中止しました。");
        return null;
    }

    /// <summary>Subject の並び・空白の違いを無視してくらべる（「CN=a, O=b」と「O=b, CN=a」を同じに）</summary>
    public static string Normalize(string? subject) =>
        subject is null ? "" : string.Join(",", subject.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

    /// <summary>Subject の O=（組織）が organization か</summary>
    public static bool IsOrganization(string? subject, string organization) =>
        subject is not null && subject.Split(',').Select(p => p.Trim())
            .Any(p => p.StartsWith("O=", StringComparison.OrdinalIgnoreCase) && p[2..].Trim('"').Equals(organization, StringComparison.OrdinalIgnoreCase));
}
