// ==================================================
// Notifier.cs - スマホへの通知 (ntfy / Discord の Webhook。外出中でも緊急全開やポンプ異常に気づける)
// Dependencies: .NET 8 / System.Net.Http / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace ProFanController;

/// <summary>
/// ntfy（https://ntfy.sh）: スマホに ntfy アプリを入れて、同じトピック名を購読するだけで通知が届く。アカウント不要。
/// Discord: サーバーのチャンネル設定で作った Webhook URL に送る。
/// 送信は別スレッドで行い、失敗してもファン制御には影響させない。
/// </summary>
public sealed class Notifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>同じ内容の通知は、この間は送らない（温度が境目を行き来したときの連発防止）</summary>
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTime> _last = new();
    private readonly object _gate = new();

    public NotifyConfig Config { get; set; } = new();

    /// <summary>直近の送信結果（設定画面の表示用）</summary>
    public string LastResult { get; private set; } = "";

    /// <summary>設定に従って通知する（レベルが低い・同じ内容が続く場合は送らない）</summary>
    /// <param name="isRecovery">「温度が下がった」など、回復を知らせる通知</param>
    public void Notify(string title, string message, AlertLevel level, bool isRecovery = false)
    {
        var cfg = Config;
        if (!cfg.Enabled) return;
        if (level == AlertLevel.Info && !(isRecovery && cfg.IncludeRecovery)) return;
        if (level == AlertLevel.Warning && !cfg.IncludeWarnings) return;
        lock (_gate)
        {
            var now = DateTime.Now;
            if (isRecovery)
            {
                // 回復の知らせ: 状態が変わったことを伝えるので、同じ文面でも送る（ただし 30 秒以内の連発はまとめる）。
                // 回復したら、次に同じ異常が起きたときにもすぐ知らせられるよう、これまでの記録を消す
                if (_last.TryGetValue(message, out var r) && now - r < TimeSpan.FromSeconds(30)) return;
                _last.Clear();
                _last[message] = now;
            }
            else
            {
                if (_last.TryGetValue(message, out var t) && now - t < RepeatInterval) return;
                _last[message] = now;
            }
        }
        _ = SendAsync(cfg, title, message, level);
    }

    /// <summary>テスト送信（設定画面のボタン）。結果の説明を返す</summary>
    public async Task<string> TestAsync(NotifyConfig cfg)
    {
        try
        {
            await SendCoreAsync(cfg, L("Zeph-Iris テスト通知"), L($"{Environment.MachineName} からのテスト通知です。この通知が届けば設定は完了です。"), AlertLevel.Info);
            return L("送信しました。スマホに届いたか確認してください");
        }
        catch (Exception ex)
        {
            return L($"送信できませんでした: {ex.Message}");
        }
    }

    private async Task SendAsync(NotifyConfig cfg, string title, string message, AlertLevel level)
    {
        // 一時的なネットワークの不調に備えて、少し間をおいて 3 回まで送り直す
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await SendCoreAsync(cfg, title, message, level);
                LastResult = L($"{DateTime.Now:HH:mm} に送信: {title}");
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt + Random.Shared.NextDouble()));
            }
            catch (Exception ex)
            {
                LastResult = L($"{DateTime.Now:HH:mm} 送信失敗: {ex.Message}");
                Logger.Warn($"通知の送信に失敗: {ex.Message}");
                return;
            }
        }
    }

    private static async Task SendCoreAsync(NotifyConfig cfg, string title, string message, AlertLevel level)
    {
        string body = L($"{message}\n（{Environment.MachineName} ・ {DateTime.Now:MM/dd HH:mm}）");
        HttpResponseMessage res;
        if (cfg.Service == NotifyServices.Discord)
        {
            if (!Uri.TryCreate(cfg.DiscordWebhook, UriKind.Absolute, out var url) || url.Scheme != "https")
                throw new InvalidOperationException(L("Discord の Webhook URL を入力してください"));
            int color = level switch { AlertLevel.Danger => 0xF87171, AlertLevel.Warning => 0xFBBF24, _ => 0x22C7FF };
            var json = new JsonObject
            {
                ["username"] = AppIdentity.Name,
                ["embeds"] = new JsonArray { new JsonObject { ["title"] = title, ["description"] = body, ["color"] = color } },
            };
            using var content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            res = await Http.PostAsync(url, content);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cfg.NtfyTopic)) throw new InvalidOperationException(L("ntfy のトピック名を入力してください"));
            var url = $"{cfg.NtfyServer.TrimEnd('/')}/{Uri.EscapeDataString(cfg.NtfyTopic.Trim())}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            // ヘッダーには ASCII しか使えないので、日本語のタイトルは RFC 2047 の形式で送る（ntfy が対応している）
            req.Headers.TryAddWithoutValidation("Title", $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}?=");
            req.Headers.TryAddWithoutValidation("Priority", level switch { AlertLevel.Danger => "5", AlertLevel.Warning => "4", _ => "3" });
            req.Headers.TryAddWithoutValidation("Tags", level switch { AlertLevel.Danger => "rotating_light", AlertLevel.Warning => "warning", _ => "white_check_mark" });
            res = await Http.SendAsync(req);
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException(L($"サーバーがエラーを返しました（{(int)res.StatusCode}）"));
        }
    }

    /// <summary>スマホの ntfy アプリで購読するための URL（QR コードにする）</summary>
    public static string NtfySubscribeUrl(NotifyConfig cfg) => $"{cfg.NtfyServer.TrimEnd('/')}/{cfg.NtfyTopic.Trim()}";
}
