// ==================================================
// AppIdentity.cs - アプリの名前と、Windows に登録する名前 (exe・タスク・ファイアウォールの規則・合図の名前)
//   6.7.0 で ProFanController から Fanlume に名前を変えた。前の名前は、前の版から引き継ぐときだけ使う（Core/LegacyMigration.cs）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class AppIdentity
{
    /// <summary>アプリの名前（画面・お知らせ・タスクスケジューラ・インストーラーで使う）</summary>
    public const string Name = "Fanlume";

    /// <summary>実行ファイルの名前</summary>
    public const string ExeName = Name + ".exe";

    /// <summary>二重起動を防ぐ印（インストーラーも、これでアプリが動いているかを調べる）</summary>
    public const string SingleInstance = @"Global\Fanlume.SingleInstance";

    /// <summary>セットすると、アプリが正しく終了する合図（インストーラーが、入れ替える前に送る）</summary>
    public const string ExitSignal = @"Global\Fanlume.Exit";

    /// <summary>前の名前（6.6.1 まで）</summary>
    public const string LegacyName = "ProFanController";

    /// <summary>前の版の exe の名前</summary>
    public const string LegacyExeName = LegacyName + ".exe";

    /// <summary>前の版が、ダウンロードなどから自動で入っていた場所</summary>
    public const string LegacyFolder = @"C:\Tools\ProFanController";

    /// <summary>前の版の、二重起動を防ぐ印（動いているかを調べる）</summary>
    public const string LegacySingleInstance = @"Global\ProFanController.SingleInstance";

    /// <summary>前の版を正しく終了させる合図</summary>
    public const string LegacyExitSignal = @"Global\ProFanController.Exit";
}
