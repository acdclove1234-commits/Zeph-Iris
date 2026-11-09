// ==================================================
// AppIdentity.cs - アプリの名前と、Windows に登録する名前 (exe・タスク・ファイアウォールの規則・合図の名前)
//   6.7.0 で ProFanController から Fanlume に、6.8.0 で Fanlume から Zeph-Iris に名前を変えた
//   （Fanlume は、同じ名前の別のサービスがあったため）。前の名前は、前の版から引き継ぐときだけ使う（Core/LegacyMigration.cs）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>前の名前の版（その名前で Windows に登録したもの・入っていた場所）</summary>
public sealed record LegacyApp(string Name, string Folder, bool InstalledWithInstaller)
{
    public string ExeName => Name + ".exe";
    /// <summary>二重起動を防ぐ印（動いているかを調べる）</summary>
    public string SingleInstance => $@"Global\{Name}.SingleInstance";
    /// <summary>正しく終了させる合図</summary>
    public string ExitSignal => $@"Global\{Name}.Exit";
    /// <summary>ログオン時の自動起動のタスクの名前</summary>
    public string TaskName => Name;
    /// <summary>スマホ監視のファイアウォールの許可の名前</summary>
    public string FirewallRule => Name + " Web";
    /// <summary>書き出した設定ファイルの印</summary>
    public string SettingsFormat => Name + ".Settings";
}

public static class AppIdentity
{
    /// <summary>アプリの名前（画面・お知らせ・タスクスケジューラ・インストーラーで使う）</summary>
    public const string Name = "Zeph-Iris";

    /// <summary>実行ファイルの名前</summary>
    public const string ExeName = Name + ".exe";

    /// <summary>二重起動を防ぐ印（インストーラーも、これでアプリが動いているかを調べる）</summary>
    public const string SingleInstance = @"Global\Zeph-Iris.SingleInstance";

    /// <summary>セットすると、アプリが正しく終了する合図（インストーラーが、入れ替える前に送る）</summary>
    public const string ExitSignal = @"Global\Zeph-Iris.Exit";

    /// <summary>6.7.0〜6.7.3 の名前（インストーラーで C:\Program Files\Fanlume に入っていた）</summary>
    public static readonly LegacyApp Fanlume = new("Fanlume",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf ? pf : @"C:\Program Files", "Fanlume"),
        InstalledWithInstaller: true);

    /// <summary>6.6.1 までの名前（ダウンロードなどから C:\Tools\ProFanController に入っていた）</summary>
    public static readonly LegacyApp ProFanController = new("ProFanController", @"C:\Tools\ProFanController", InstalledWithInstaller: false);

    /// <summary>前の名前の版（新しいものから順に。設定は最初に見つかったものから引き継ぐ）</summary>
    public static readonly IReadOnlyList<LegacyApp> LegacyApps = new[] { Fanlume, ProFanController };

    /// <summary>Fanlume のアンインストーラーに「設定・ログ・記録は残す」と伝える引数（Fanlume の Installer/Fanlume.iss が読む）</summary>
    public const string FanlumeUninstallKeepData = "/SILENT /SUPPRESSMSGBOXES /NORESTART /FANLUMEDATA=keep";
}
