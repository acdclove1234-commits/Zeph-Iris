// ==================================================
// AppUpdater.cs - アプリの中からの更新 (GitHub の最新のリリースを確かめ、インストーラーをダウンロードして実行する)
//   1. https://api.github.com/repos/<リポジトリ>/releases/latest を読み、いまより新しい版の Fanlume-<版>-setup.exe があるかを見る
//      （下書き・プレリリースは使わない。送るのはアプリの名前と版だけ）
//   2. ダウンロードしたら、大きさ・GitHub が出している SHA-256・インストーラーの版を確かめる
//   3. 確かめたファイルを、書き換え・削除できないように開いたまま実行する（このアプリは管理者権限で動くので、
//      確かめてから実行するまでの間に、ふだんの権限のプログラムにすり替えられないように）
//   インストーラーは画面を出さずに動き、動いているアプリを正しく終了させて（ファンは BIOS 制御に戻る）入れ替え、起動し直す
// Dependencies: .NET 8 / System.Net.Http / System.Text.Json / System.Security.Cryptography (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProFanController;

/// <summary>GitHub にある新しい版（インストーラー 1 つ）</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, long Size, string Sha256, DateTime? PublishedAt);

public static class AppUpdater
{
    /// <summary>リリースを置いている GitHub のリポジトリ</summary>
    public const string Repository = "acdclove1234-commits/Fanlume";
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    /// <summary>ダウンロードしてよい場所（このリポジトリのリリースの添付ファイルだけ）</summary>
    public const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    /// <summary>インストーラーに、更新のあとアプリの画面を出してほしいと伝える引数（Installer/Fanlume.iss が読む）</summary>
    public const string ShowAppArgument = "/FANLUMESHOW";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 時間の上限は呼ぶ側の CancellationToken で決める
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppIdentity.Name}/{CurrentVersion.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>いま動いているアプリの版（メジャー.マイナー.ビルド）</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>latest がいまの版より新しいか（メジャー.マイナー.ビルドで比べる）</summary>
    public static bool IsNewer(Version latest, Version current)
        => new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>「v6.7.1」「6.7.1」から版を読む（読めなければ null）</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    /// <summary>
    /// GitHub の releases/latest の JSON から、更新に使うインストーラーを取り出す。
    /// 下書き・プレリリース・版が読めない・Fanlume-<版>-setup.exe がない・SHA-256 がない・このリポジトリ以外の URL なら null
    /// </summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        string tag = Str(root, "tag_name");
        if (ParseTag(tag) is not { } version) return null;
        DateTime? published = DateTime.TryParse(Str(root, "published_at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var p) ? p.ToLocalTime() : null;
        string expected = $"{AppIdentity.Name}-{version.ToString(3)}-setup.exe";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(Str(a, "name"), expected, StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(a, "state") is { Length: > 0 } state && state != "uploaded") return null;
            string url = Str(a, "browser_download_url");
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
            string digest = Str(a, "digest");
            if (size <= 0 || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            string sha = digest[7..].Trim().ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
            return new UpdateInfo(version, tag, expected, url, size, sha, published);
        }
        return null;
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>GitHub に最新のリリースを問い合わせる（使えるインストーラーがなければ null。通信の失敗は例外）</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var res = await Http.GetAsync(LatestReleaseApi, cts.Token);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // まだリリースがない
        res.EnsureSuccessStatusCode();
        return ParseRelease(await res.Content.ReadAsStringAsync(cts.Token));
    }

    /// <summary>ダウンロードして確かめたインストーラー。Stream は、書き換え・削除させないために開いたまま持つ（アプリが終わるまで）</summary>
    public sealed record VerifiedInstaller(string Path, FileStream HeldOpen);

    /// <summary>
    /// インストーラーをダウンロードし、大きさ・SHA-256・ファイルの版を確かめる。合わなければ消して例外。
    /// progress には 0〜1 を渡す
    /// </summary>
    public static async Task<VerifiedInstaller> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{AppIdentity.Name}-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, info.AssetName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                res.EnsureSuccessStatusCode();
                await using var src = await res.Content.ReadAsStreamAsync(cts.Token);
                await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, cts.Token)) > 0)
                {
                    if (done + read > info.Size) throw new InvalidDataException(L("ダウンロードしたファイルが、GitHub に書かれた大きさより大きくなりました。"));
                    await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    done += read;
                    progress?.Report((double)done / info.Size);
                }
                await dst.FlushAsync(cts.Token);
            }

            // 読み取りだけを許して開き直し、そのまま確かめる（このあと実行するまで、ほかのプログラムは書き換え・削除できない）
            var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Verify(held, info);
                var fileVersion = InstallCheck.FileVersion(path);
                if (fileVersion is null || !fileVersion.ToString(3).Equals(info.Version.ToString(3), StringComparison.Ordinal))
                    throw new InvalidDataException(L($"インストーラーの版（{fileVersion?.ToString(3) ?? "?"}）が、リリースの版（{info.Version.ToString(3)}）と違います。"));
                return new VerifiedInstaller(path, held);
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }
        catch
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 消せなくても害はない */ }
            throw;
        }
    }

    /// <summary>大きさと SHA-256 が GitHub に書かれたものと同じか（違えば例外）。stream は先頭から読む</summary>
    public static void Verify(Stream stream, UpdateInfo info)
    {
        if (stream.Length != info.Size)
            throw new InvalidDataException(L($"ダウンロードしたファイルの大きさ（{stream.Length} バイト）が、GitHub に書かれた大きさ（{info.Size} バイト）と違います。"));
        stream.Position = 0;
        string sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        if (sha != info.Sha256)
            throw new InvalidDataException(L("ダウンロードしたファイルが、GitHub に公開されたものと一致しません（SHA-256 が違います）。更新を中止しました。"));
    }

    /// <summary>インストーラーの引数（画面を出さずに動く。showApp なら、更新のあとアプリの画面を出す）</summary>
    public static string InstallerArguments(bool showApp) => "/SILENT /SUPPRESSMSGBOXES /NORESTART" + (showApp ? " " + ShowAppArgument : "");

    /// <summary>確かめたインストーラーを実行する。インストーラーは、このアプリに終了の合図を送ってから入れ替える</summary>
    public static void StartInstaller(VerifiedInstaller installer, bool showApp)
    {
        Process.Start(new ProcessStartInfo(installer.Path, InstallerArguments(showApp))
        {
            UseShellExecute = false,
            WorkingDirectory = System.IO.Path.GetDirectoryName(installer.Path)!,
        })?.Dispose();
    }
}
