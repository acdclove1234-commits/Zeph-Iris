// スマホ監視の鍵の間違いの数え方（外部レビューの指摘で直したところ）
using System.Net;
using ProFanController;

namespace LogicTests;

public static class WebLimiterTests
{
    /// <summary>たくさんのアドレスから試しても、締め出している端末の記録は消えない</summary>
    public static void TestTableFullDoesNotWipeBlockedClients()
    {
        var failures = new Dictionary<IPAddress, Queue<DateTime>>();
        var gate = new object();
        var now = new DateTime(2026, 11, 14, 21, 0, 0);
        var attacker = IPAddress.Parse("192.168.1.66");
        for (int i = 0; i < 10; i++) WebServer.RecordFailure(failures, gate, attacker, now.AddSeconds(i));
        for (int i = 0; i < 400; i++)
            WebServer.RecordFailure(failures, gate, IPAddress.Parse($"10.0.{i / 250}.{i % 250 + 1}"), now.AddSeconds(10 + i * 0.01));
        T.Check(failures.Count <= WebServer.MaxTrackedClients, $"the table stays bounded ({failures.Count})");
        T.Check(failures.TryGetValue(attacker, out var q) && q.Count == 10, "the blocked client's recent failures survive (no full wipe)");
    }

    public static void TestStaleEntriesAreRemovedFirst()
    {
        var failures = new Dictionary<IPAddress, Queue<DateTime>>();
        var gate = new object();
        var t0 = new DateTime(2026, 11, 14, 21, 0, 0);
        for (int i = 0; i < WebServer.MaxTrackedClients; i++)
            WebServer.RecordFailure(failures, gate, IPAddress.Parse($"10.1.{i / 250}.{i % 250 + 1}"), t0);
        WebServer.RecordFailure(failures, gate, IPAddress.Parse("192.168.5.5"), t0.AddMinutes(2));
        T.Check(failures.Count == 1, $"entries older than 1 minute are dropped when the table is full ({failures.Count})");
    }

    /// <summary>IPv6 はアドレスごと（LAN の中の機器は前半 64 ビットが同じなので、まとめると、ほかの機器の失敗で締め出される）</summary>
    public static void TestIpv6CountedPerAddress()
    {
        var a = WebServer.FailureKey(IPAddress.Parse("fe80::1:2:3:4%12"));
        var b = WebServer.FailureKey(IPAddress.Parse("fe80::9:8:7:6%12"));
        T.Check(!a.Equals(b), "two devices on the same link are counted separately");
        T.Check(a.Equals(WebServer.FailureKey(IPAddress.Parse("fe80::1:2:3:4%7"))), "the same address with another scope id is the same device");
        T.Check(WebServer.FailureKey(IPAddress.Parse("192.168.1.10")).Equals(IPAddress.Parse("192.168.1.10")), "IPv4 is per address");
        T.Check(WebServer.FailureKey(IPAddress.Parse("::ffff:192.168.1.10")).Equals(IPAddress.Parse("192.168.1.10")), "IPv4-mapped IPv6 is treated as IPv4");
    }
}
