// 更新のインストーラーの電子署名の決め方（SignaturePolicy）と、Windows での署名の読み取り（Authenticode）
using ProFanController;

namespace LogicTests;

public static class SignaturePolicyTests
{
    private const string Me = "CN=Zeph-Iris Project, O=Zeph-Iris Project, C=JP";

    public static void TestPolicy()
    {
        var unsigned = SignInfo.None;
        var mine = new SignInfo(SignState.Valid, Me);
        var reordered = new SignInfo(SignState.Valid, "C=JP, O=Zeph-Iris Project,  CN=Zeph-Iris Project");
        var other = new SignInfo(SignState.Valid, "CN=Someone Else, O=Someone Else");
        var broken = new SignInfo(SignState.Invalid, Me);

        T.Check(SignaturePolicy.Problem(unsigned, unsigned) is null, "before signing starts: an unsigned installer is accepted");
        T.Check(SignaturePolicy.Problem(unsigned, mine) is null, "unsigned app → first signed installer is accepted");
        T.Check(SignaturePolicy.Problem(mine, mine) is null && SignaturePolicy.Problem(mine, reordered) is null, "same signer (order and spaces ignored)");
        T.Check(SignaturePolicy.Problem(mine, unsigned) is not null, "signed app never goes to an unsigned installer");
        T.Check(SignaturePolicy.Problem(mine, other) is not null, "signed app never goes to another signer");
        T.Check(SignaturePolicy.Problem(unsigned, broken) is not null && SignaturePolicy.Problem(mine, broken) is not null, "a broken signature is always refused");
        T.Check(SignaturePolicy.IsOrganization("CN=Intel Corporation, O=Intel Corporation, S=California, C=US", "Intel Corporation"), "Intel organization");
        T.Check(!SignaturePolicy.IsOrganization("CN=Intel Corporation, O=Evil Corp", "Intel Corporation"), "CN alone is not enough");
    }

    /// <summary>Windows の署名の読み取り: 署名つきの Windows の exe と、署名のない一時ファイル</summary>
    public static void TestInspectOnThisPc()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(notepad))
        {
            T.Skip("explorer.exe not found");
            return;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "zi-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x4D, 0x5A, 0, 0 });
            T.Check(Authenticode.Inspect(tmp).State != SignState.Valid, "a file without a signature is not valid");
            // explorer.exe はカタログ署名（ファイルの中に署名がない）なので、Valid とは限らない。読めて例外にならないことだけ見る
            var s = Authenticode.Inspect(notepad);
            T.Check(Enum.IsDefined(s.State), $"a Windows exe can be inspected ({s.State})");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

public static class PresentMonSignatureTests
{
    /// <summary>PRESENTMON_EXE に PresentMon の exe を入れたときだけ: Intel の正しい署名として読めるか</summary>
    public static void TestPresentMonIsIntelSigned()
    {
        if (Environment.GetEnvironmentVariable("PRESENTMON_EXE") is not { Length: > 0 } path || !File.Exists(path))
        {
            T.Skip("PRESENTMON_EXE is not set");
            return;
        }
        var s = Authenticode.Inspect(path);
        T.Check(s.State == SignState.Valid && SignaturePolicy.IsOrganization(s.Subject, "Intel Corporation"), $"PresentMon is Intel-signed ({s.State}, {s.Subject})");
    }
}
