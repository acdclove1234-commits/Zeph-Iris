// ==================================================
// WebServer.cs - スマホから見る簡易 Web 画面 (同じ Wi-Fi 内のみ・アクセス用の鍵が必要)
// Dependencies: .NET 8 / System.Net.HttpListener (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ProFanController;

public sealed class WebServer : IDisposable
{
    private const string FirewallRuleName = "Zeph-Iris Web";

    private readonly Func<string> _statusJson;
    private readonly Func<string, bool> _command;
    private HttpListener? _listener;
    private string _token = "";
    /// <summary>鍵の間違いの記録（総当たりを防ぐ。1 分に 10 回まで）</summary>
    private readonly Dictionary<IPAddress, Queue<DateTime>> _failures = new();
    private readonly object _failGate = new();

    public int Port { get; private set; }
    public bool Running => _listener?.IsListening == true;

    /// <param name="statusJson">現在の状態（JSON 文字列）を返す。どのスレッドから呼ばれてもよいこと</param>
    /// <param name="command">"boost:5" / "quiet:60" / "cancel" / "profile:名前" を実行する</param>
    public WebServer(Func<string> statusJson, Func<string, bool> command)
    {
        _statusJson = statusJson;
        _command = command;
    }

    public void Start(int port, string token)
    {
        Stop();
        Port = port;
        _token = token;
        // 管理者権限で動いているので、すべての IP アドレスで待ち受けられる（スマホから届くようにするため）
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
        Logger.Info($"Web サーバー開始: ポート {port}");
    }

    public void Stop()
    {
        if (_listener is null) return;
        try { _listener.Close(); } catch { /* 停止時の例外は無視 */ }
        _listener = null;
    }

    private async Task LoopAsync()
    {
        var listener = _listener;
        while (listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // Stop() で閉じられた
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            // 同じ家・会社のネットワーク（プライベートアドレス）とこの PC 自身からだけ受け付ける
            if (!IsLocalNetwork(req.RemoteEndPoint?.Address))
            {
                Write(ctx, 403, "text/plain", L("このネットワークからは使えません。"));
                return;
            }
            // ページ以外（ブラウザーが自動で取りに来るアイコンなど）は、鍵を調べずに 404 にする（鍵の間違いとして数えない）
            string path = req.Url?.AbsolutePath ?? "";
            if (path is not ("/" or "/api/status" or "/api/action"))
            {
                Write(ctx, 404, "text/plain", "not found");
                return;
            }
            // ページそのもの（データを含まない画面の枠）は、鍵なしで返す。鍵はページの中のスクリプトが API に付けて送る。
            // ページは「?k=鍵」で開かれたら鍵をブラウザーに覚えて、アドレス欄から鍵を消す（履歴・画面共有で鍵が見えないように）
            if (path == "/" && req.HttpMethod == "GET")
            {
                Write(ctx, 200, "text/html", WebPage.Html);
                return;
            }
            var client = FailureKey(req.RemoteEndPoint?.Address ?? IPAddress.None);
            if (TooManyFailures(client))
            {
                Write(ctx, 429, "text/plain", L("鍵の間違いが続いたため、しばらく受け付けません。1 分ほど待ってください。"));
                return;
            }
            string key = req.Headers["X-Key"] ?? req.QueryString["k"] ?? "";
            // 鍵が設定されていない・短すぎるときは、誰も入れない（空の鍵どうしが一致してしまわないように）
            if (_token.Length < MinTokenLength || key.Length < MinTokenLength || !CryptographicEquals(key, _token))
            {
                RecordFailure(client);
                Write(ctx, 401, "text/plain", L("アクセス用の鍵が違います。PC の設定画面に表示されている URL を使ってください。"));
                return;
            }

            switch (req.Url?.AbsolutePath)
            {
                case "/":
                    Write(ctx, 200, "text/html", WebPage.Html);
                    break;
                case "/api/status":
                    Write(ctx, 200, "application/json", _statusJson());
                    break;
                case "/api/action" when req.HttpMethod == "POST":
                    using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    {
                        bool ok = _command(reader.ReadToEnd().Trim());
                        Write(ctx, ok ? 200 : 400, "application/json", ok ? "{\"ok\":true}" : "{\"ok\":false}");
                    }
                    break;
                default:
                    Write(ctx, 404, "text/plain", "not found");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Web リクエストの処理に失敗: {ex.Message}");
            try { ctx.Response.Abort(); } catch { /* 既に切断されている */ }
        }
    }

    /// <summary>その端末が直近 1 分に 10 回以上鍵を間違えた（端末ごとに数えるので、ほかの機器の失敗で締め出されない）</summary>
    private bool TooManyFailures(IPAddress client)
    {
        lock (_failGate)
        {
            if (!_failures.TryGetValue(client, out var q)) return false;
            while (q.Count > 0 && DateTime.Now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count == 0) _failures.Remove(client);
            return q.Count >= 10;
        }
    }

    /// <summary>覚えておく端末の数の上限</summary>
    internal const int MaxTrackedClients = 256;

    private void RecordFailure(IPAddress client) => RecordFailure(_failures, _failGate, client, DateTime.Now);

    /// <summary>
    /// 鍵の間違いを記録する。表がいっぱいなら、1 分より前の記録を消し、まだいっぱいなら、間違いのいちばん少ない端末だけを消す
    /// （全部は消さない。たくさんのアドレスから試して、締め出しを解かれないように）
    /// </summary>
    internal static void RecordFailure(Dictionary<IPAddress, Queue<DateTime>> failures, object gate, IPAddress client, DateTime now)
    {
        lock (gate)
        {
            if (!failures.TryGetValue(client, out var q))
            {
                if (failures.Count >= MaxTrackedClients)
                {
                    foreach (var old in failures.Where(e => e.Value.Count == 0 || now - e.Value.Last() > TimeSpan.FromMinutes(1)).Select(e => e.Key).ToList())
                        failures.Remove(old);
                    // まだいっぱいなら、間違いがいちばん少ない（締め出す手前から遠い）端末を消す。締め出し中の端末は最後まで残す
                    if (failures.Count >= MaxTrackedClients)
                        failures.Remove(failures.OrderBy(e => e.Value.Count).ThenBy(e => e.Value.Last()).First().Key);
                }
                failures[client] = q = new Queue<DateTime>();
            }
            q.Enqueue(now);
        }
    }

    /// <summary>
    /// 間違いを数える単位: アドレスごと（IPv4 に包まれた IPv6 は IPv4 として、IPv6 はスコープの番号を除いて）。
    /// LAN の中では、IPv6 の前半 64 ビットは家じゅうの機器で同じなので、そこでまとめると、ほかの機器の失敗で締め出されてしまう。
    /// 鍵は 128 ビットの乱数なので、アドレスを変えながら試されても当たらない（表の大きさは MaxTrackedClients まで）
    /// </summary>
    internal static IPAddress FailureKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        return new IPAddress(address.GetAddressBytes());   // スコープの番号（%12 など）を落とす
    }

    /// <summary>プライベートアドレス（10.x・172.16〜31.x・192.168.x・169.254.x・IPv6 のローカル）か、この PC 自身か</summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
        return false;
    }

    /// <summary>鍵の最低の長さ（これより短い鍵は受け付けない）</summary>
    public const int MinTokenLength = 16;

    /// <summary>文字列比較の時間で鍵を推測されないよう、常に全文字を比較する</summary>
    private static bool CryptographicEquals(string a, string b)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static void Write(HttpListenerContext ctx, int status, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = $"{type}; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        // ほかのサイトに埋め込まれたり、種類を推測して実行されたりしないように
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline' 'self'; style-src 'unsafe-inline' 'self'; img-src 'self' data:; frame-ancestors 'none'";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    /// <summary>スマホから開くための URL（この PC の LAN 内 IPv4 アドレス）</summary>
    public static List<string> LocalUrls(int port, string token)
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => $"http://{a.Address}:{port}/?k={token}")
            .Distinct()
            .ToList();

    /// <summary>Windows ファイアウォールで、プライベートネットワーク（自宅の Wi-Fi など）からの接続だけを許可する</summary>
    public static void AllowFirewall(int port)
    {
        RemoveFirewall();
        // 許可するのは、同じネットワーク（localsubnet）から・プライベートネットワークのときだけ。
        // （HttpListener の通信は Windows の http.sys が受けるので、program= でこのアプリに限定すると一致せず、つながらなくなる）
        Netsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private remoteip=localsubnet");
    }

    public static void RemoveFirewall() => Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

    /// <summary>名前を指定してファイアウォールの許可を消す（前の名前の版が追加したもの。新しい名前の許可は、スマホ監視を始めるときに作る）</summary>
    public static void RemoveFirewallRule(string name) => Netsh($"advfirewall firewall delete rule name=\"{name}\"");

    /// <summary>このアプリが追加したファイアウォールの許可が残っているか（netsh は、その名前の規則がないと 0 以外で終わる）</summary>
    public static bool FirewallRuleExists() => FirewallRuleExists(FirewallRuleName);

    /// <summary>その名前のファイアウォールの許可があるか</summary>
    public static bool FirewallRuleExists(string name)
    {
        try
        {
            var psi = SystemTool.Start("netsh.exe", $"advfirewall firewall show rule name=\"{name}\"");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi)!;
            // 出力を読み捨てる（読まないと、出力がいっぱいになって止まることがある）
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { /* 終わっている */ } return false; }
            Task.WaitAll(new Task[] { outTask, errTask }, 1000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ファイアウォールの許可を確かめられません: {ex.Message}");
            return false;
        }
    }

    private static void Netsh(string args)
    {
        try
        {
            using var p = Process.Start(SystemTool.Start("netsh.exe", args))!;
            p.WaitForExit(10_000);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ファイアウォールの設定に失敗: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}

/// <summary>スマホ用の 1 ページだけの画面（2 秒ごとに状態を取得して表示）</summary>
internal static class WebPage
{
    /// <summary>画面の言語に訳したページ（言語ごとに 1 回作る。表示言語を切り替えたら作り直す）</summary>
    public static string Html => _html.Value;
    private static readonly PerLanguage<string> _html = new(() => Localize(Template));

    /// <summary>ページの中の日本語を、画面の言語に置き換える（HTML の文字は HTML 用に、JavaScript の文字は JSON の形で埋め込む）</summary>
    internal static string Localize(string t)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        return t.Replace("<html lang=\"ja\">", $"<html lang=\"{Localization.Language}\">")
            .Replace(">接続中…<", ">" + H(L("接続中…")) + "<")
            .Replace(">ファン<", ">" + H(L("ファン")) + "<")
            .Replace(">システム<", ">" + H(L("システム")) + "<")
            .Replace(">プロファイル<", ">" + H(L("プロファイル")) + "<")
            .Replace(">全開 5分<", ">" + H(L("全開 5分")) + "<")
            .Replace(">静音 1時間<", ">" + H(L("静音 1時間")) + "<")
            .Replace(">解除<", ">" + H(L("解除")) + "<")
            .Replace("' 更新 ・ '", J(L(" 更新 ・ ")))
            .Replace("'PC に接続できません'", J(L("PC に接続できません")));
    }

    private const string Template = """
<!doctype html>
<html lang="ja"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Zeph-Iris</title>
<style>
:root{--bg:#0b0c12;--card:#161824;--line:#252838;--text:#eceef6;--sub:#8b91a8;--ok:#34d399;--warn:#fbbf24;--danger:#f87171;--a1:#7c5cff;--a2:#22c7ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:system-ui,-apple-system,"Hiragino Sans","Yu Gothic UI",sans-serif;padding:16px}
h1{font-size:20px;margin:0 0 4px}.sub{color:var(--sub);font-size:12px}
.grid{display:grid;grid-template-columns:repeat(2,1fr);gap:10px;margin-top:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px}
.big{font-size:30px;font-weight:600}.unit{font-size:13px;color:var(--sub);margin-left:3px}
.row{display:flex;justify-content:space-between;align-items:center;padding:8px 0;border-bottom:1px solid var(--line)}.row:last-child{border:0}
.dot{display:inline-block;width:8px;height:8px;border-radius:4px;margin-right:6px}
button,select{background:#1d2030;color:var(--text);border:1px solid var(--line);border-radius:10px;padding:12px;font-size:15px;width:100%}
button.accent{background:linear-gradient(135deg,var(--a1),var(--a2));border:0;font-weight:600}
.actions{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-top:10px}
.banner{margin-top:12px;padding:10px 12px;border-radius:10px;background:#22c7ff22;border:1px solid #22c7ff66;display:none}
</style></head><body>
<h1>Zeph-Iris</h1><div class="sub" id="time">接続中…</div>
<div class="banner" id="banner"></div>
<div class="grid" id="temps"></div>
<div class="card" style="margin-top:10px"><div class="sub">ファン</div><div id="fans"></div></div>
<div class="card" style="margin-top:10px"><div class="sub">システム</div><div id="metrics"></div></div>
<div class="card" style="margin-top:10px">
  <div class="sub" style="margin-bottom:8px">プロファイル</div>
  <select id="profile" onchange="act('profile:'+this.value)"></select>
  <div class="actions">
    <button class="accent" onclick="act('boost:5')">全開 5分</button>
    <button onclick="act('quiet:60')">静音 1時間</button>
    <button onclick="act('cancel')">解除</button>
  </div>
</div>
<script>
// 「?k=鍵」で開かれたら、アドレス欄から鍵を消す（履歴・ブックマーク・画面共有に鍵を残さない）。
// 鍵をこのブラウザーに覚えるのは、PC が正しい鍵だと答えてから（古いリンクで、覚えている正しい鍵を上書きしないように）
const urlKey=new URLSearchParams(location.search).get('k')||'';
let saved='';try{saved=localStorage.getItem('zk')||'';}catch(e){}
let k=urlKey||saved;
if(urlKey){try{history.replaceState(null,'','/');}catch(e){}}
let stopped=false;
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const color=(v,w,d)=>v==null?'var(--sub)':v>=d?'var(--danger)':v>=w?'var(--warn)':'var(--ok)';
async function act(a){await fetch('/api/action',{method:'POST',body:a,headers:{'X-Key':k}});load();}
async function load(){
  if(stopped)return;
  try{
    const r=await fetch('/api/status',{headers:{'X-Key':k}});
    if(r.status===401&&k===urlKey&&saved&&saved!==urlKey){k=saved;return load();}   // 古いリンクの鍵 → 覚えている鍵で試す
    if(r.status===401||r.status===429){stopped=true;document.getElementById('time').textContent=await r.text();return;}   // PC の言語の案内
    if(r.ok&&urlKey&&k===urlKey&&saved!==urlKey){try{localStorage.setItem('zk',k);saved=k;}catch(e){}}
    const s=await r.json();
    document.getElementById('time').textContent=s.time+' 更新 ・ '+s.state;
    const b=document.getElementById('banner');b.style.display=s.quick?'block':'none';b.textContent=s.quick||'';
    document.getElementById('temps').innerHTML=s.temps.map(t=>`<div class="card"><div class="sub">${esc(t.title)}</div><span class="big" style="color:${color(t.value,t.warn,t.danger)}">${t.value==null?'--':Math.round(t.value)}</span><span class="unit">°C</span></div>`).join('');
    document.getElementById('fans').innerHTML=s.fans.map(f=>`<div class="row"><span><span class="dot" style="background:${esc(f.color)}"></span>${esc(f.name)}</span><span><b>${esc(f.rpm)}</b> <span class="unit">rpm ・ ${esc(f.duty)}</span></span></div>`).join('');
    document.getElementById('metrics').innerHTML=s.metrics.map(m=>`<div class="row"><span>${esc(m.title)}</span><b>${esc(m.text)}</b></div>`).join('');
    const sel=document.getElementById('profile');
    if(document.activeElement!==sel){sel.innerHTML=s.profiles.map(p=>`<option value="${esc(p)}" ${p===s.manualProfile?'selected':''}>${esc(p)}</option>`).join('');}
  }catch(e){document.getElementById('time').textContent='PC に接続できません';}
}
load();setInterval(load,2000);
</script></body></html>
""";
}
