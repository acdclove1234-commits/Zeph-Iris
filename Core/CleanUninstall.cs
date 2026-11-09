// ==================================================
// CleanUninstall.cs - アンインストール（お片付け）の手順 (何をどの順番で行うか・確認の画面に出す説明・
//                     失敗したときに続けるか・設定や記録の消し方・最後のまとめ)
//   実際の操作（ファン・LED・自動起動など）は App.Uninstall.cs が、ここで決めた順番どおりに行う
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>お片付けの 1 つの手順（この順番で行う）</summary>
public enum CleanupStep
{
    /// <summary>すべてのファンを BIOS の自動制御に戻し、戻ったことを確かめる（失敗したら、ここで中止して何も変えない）</summary>
    RestoreFans,
    /// <summary>ファン制御を止め、ハードウェア（PawnIO ドライバー）を閉じる。見張りプロセスも終わらせる（止めた制御ループを「フリーズ」と見なさないように）</summary>
    StopFanControl,
    /// <summary>ライティングの制御を止めて、デバイスを離す</summary>
    StopLighting,
    /// <summary>スマホ監視を止めて、Windows ファイアウォールの許可を消す</summary>
    RemoveFirewallRule,
    /// <summary>ログオン時の自動起動（タスクスケジューラ）を消す</summary>
    RemoveAutoStart,
    /// <summary>クラッシュしたときの記録の設定（レジストリ）を消す</summary>
    RemoveCrashDumpSetting,
    /// <summary>PawnIO を winget でアンインストールする（選んだときだけ）</summary>
    UninstallPawnIo,
    /// <summary>アプリを終了する（ふだんの終了と同じ後片付け。見張りプロセスはその前に終わっているので、起動し直されない）</summary>
    ExitApp,
    /// <summary>アプリが終わったあとに、設定・ログ・記録を消す（選んだときだけ）</summary>
    DeleteData,
}

/// <summary>利用者が選ぶこと（どちらも、既定はオフ）</summary>
public sealed record CleanupOptions(bool DeleteData = false, bool UninstallPawnIo = false);

/// <summary>確認の画面を出す前に調べた、この PC の状態</summary>
public sealed record CleanupFacts(bool AutoStartRegistered, bool FirewallRuleExists, bool CrashDumpSettingExists, bool PawnIoInstalled);

public enum StepStatus { Pending, Running, Done, Warning, Failed, Skipped }

/// <summary>手順 1 つの結果</summary>
public sealed record StepResult(CleanupStep Step, StepStatus Status, string Message, bool PcRestart = false);

public static class CleanUninstall
{
    /// <summary>お片付けで消す、アプリのフォルダーの中のフォルダー（アプリ本体の exe・dll は含めない）</summary>
    public static readonly IReadOnlyList<string> DataFolders = new[] { "logs", "data", "backups", "records", "reports", "images" };

    /// <summary>お片付けで消す、アプリのフォルダーの中のファイル（* は何文字でも）</summary>
    public static readonly IReadOnlyList<string> DataFilePatterns = new[] { "config.json", "config.json.tmp", "config.broken_*.json" };

    /// <summary>確認の画面と結果に出す、消すものの説明</summary>
    public static string DataDescription => L("設定（config.json）・ログ（logs）・記録（data・records・reports）・設定のバックアップ（backups）・デバイスの画像（images）");

    /// <summary>
    /// 行う手順を、行う順番に並べる。
    /// いちばん大事な「ファンを BIOS に戻す」を最初に行い、失敗したらそこで中止する（何も変えずに、アプリはこれまでどおり制御を続ける）。
    /// PawnIO のアンインストールは、このアプリがドライバーを閉じたあと。設定・ログ・記録は、アプリが書き込まなくなった終了のあとに消す
    /// </summary>
    public static IReadOnlyList<CleanupStep> Plan(CleanupOptions options, CleanupFacts facts)
    {
        var steps = new List<CleanupStep> { CleanupStep.RestoreFans, CleanupStep.StopFanControl, CleanupStep.StopLighting };
        if (facts.FirewallRuleExists) steps.Add(CleanupStep.RemoveFirewallRule);
        if (facts.AutoStartRegistered) steps.Add(CleanupStep.RemoveAutoStart);
        if (facts.CrashDumpSettingExists) steps.Add(CleanupStep.RemoveCrashDumpSetting);
        if (options.UninstallPawnIo && facts.PawnIoInstalled) steps.Add(CleanupStep.UninstallPawnIo);
        steps.Add(CleanupStep.ExitApp);
        if (options.DeleteData) steps.Add(CleanupStep.DeleteData);
        return steps;
    }

    /// <summary>確認の画面に出す、手順の説明</summary>
    public static string Describe(CleanupStep step) => step switch
    {
        CleanupStep.RestoreFans => L("すべてのファンを BIOS（マザーボード）の自動制御に戻し、戻ったことを確かめます（戻せなかったときは、ここで中止して何も変えません）"),
        CleanupStep.StopFanControl => L("ファンの制御を止めて、ハードウェア（PawnIO ドライバー）を閉じます。見張りプロセスにも「正しい終了」を伝えて終わらせます（止めたファンの制御を異常と見なして、起動し直さないように）"),
        CleanupStep.StopLighting => L("ライティングの制御を止めて、LED のデバイス（OpenRGB など）を離します（ふだんの終了と同じ光り方で止めます）"),
        CleanupStep.RemoveFirewallRule => L("スマホ監視を止めて、Windows ファイアウォールに追加した許可（Zeph-Iris Web）を消します"),
        CleanupStep.RemoveAutoStart => L("ログオン時の自動起動（タスクスケジューラの「Zeph-Iris」）を消します"),
        CleanupStep.RemoveCrashDumpSetting => L("クラッシュしたときの記録の設定（レジストリの Windows Error Reporting\\LocalDumps）を消します"),
        CleanupStep.UninstallPawnIo => L($"PawnIO（ファン制御用のドライバー）を winget でアンインストールします"),
        CleanupStep.ExitApp => L("アプリを終了します（ふだんの終了と同じ後片付けをします。自動で起動し直しません）"),
        CleanupStep.DeleteData => L($"アプリが終わったあとに、{DataDescription}を消します（アプリ本体の exe などは残します）"),
        _ => step.ToString(),
    };

    /// <summary>この手順が失敗したら、残りを行わずに中止するか（ファンを BIOS に戻せなかったときだけ）</summary>
    public static bool AbortsOnFailure(CleanupStep step) => step == CleanupStep.RestoreFans;

    /// <summary>
    /// これまでの結果を見て、この手順を行ってよいか。だめなら、行わない理由（行ってよければ null）。
    /// PawnIO は、このアプリがドライバーを閉じられたときだけアンインストールする（使用中のまま消そうとすると、失敗するか再起動待ちになる）
    /// </summary>
    public static string? SkipReason(CleanupStep step, IReadOnlyList<StepResult> done)
    {
        if (done.Any(r => AbortsOnFailure(r.Step) && r.Status == StepStatus.Failed)) return L("ファンを BIOS 制御に戻せなかったため、中止しました");
        if (step == CleanupStep.UninstallPawnIo
            && done.Any(r => r.Step == CleanupStep.StopFanControl && r.Status == StepStatus.Failed))
            return L("ファンの制御を止めきれず、PawnIO ドライバーが使われたままの可能性があるため、アンインストールしませんでした（") + PawnIoSetup.ManualUninstallHint + L("）");
        return null;
    }

    /// <summary>中止したか（ファンを BIOS に戻せなかった。アプリはこれまでどおり動き続ける）</summary>
    public static bool Aborted(IReadOnlyList<StepResult> results)
        => results.Any(r => AbortsOnFailure(r.Step) && r.Status == StepStatus.Failed);

    /// <summary>
    /// 終わったあとのまとめ（終了する前に画面に出す）。
    /// appFolder: アプリのフォルダー（終わったあとに、自分で消してもらう）。
    /// byUninstaller: インストーラーで入れた（終わったあと、アンインストーラーがアプリ本体を消す）
    /// </summary>
    public static string Summary(IReadOnlyList<StepResult> results, IReadOnlyList<CleanupStep> plan, string appFolder, bool byUninstaller = false)
    {
        var lines = new List<string>();
        if (Aborted(results))
        {
            var fail = results.First(r => r.Step == CleanupStep.RestoreFans);
            lines.Add(L("お片付けを中止しました。何も変えていません。"));
            lines.Add(fail.Message);
            lines.Add(L("ファンは、これまでどおりこのアプリが制御しています。PC を再起動してから、もう一度お試しください。"));
            return string.Join("\n", lines);
        }

        var problems = results.Where(r => r.Status is StepStatus.Failed or StepStatus.Warning or StepStatus.Skipped).ToList();
        lines.Add(problems.Count == 0
            ? L("お片付けが終わりました。ファンは BIOS（マザーボード）の自動制御に戻っています。")
            : L("お片付けが終わりました。うまくいかなかったことがあります（下のとおり）。"));
        foreach (var p in problems) lines.Add(L($"・{p.Message}"));
        if (results.Any(r => r.PcRestart || r.Message.Contains("PC を再起動", StringComparison.Ordinal)))
            lines.Add(L("・PC を再起動すると、すべてが元どおりになります。"));
        lines.Add("");
        if (byUninstaller)
        {
            lines.Add(plan.Contains(CleanupStep.DeleteData)
                ? L($"閉じるとアプリが終了し、続けてアンインストーラーがアプリ本体と{DataDescription}を消します（Windows の「インストールされているアプリ」からも外れます）。")
                : L("閉じるとアプリが終了し、続けてアンインストーラーがアプリ本体を消します（Windows の「インストールされているアプリ」からも外れます）。設定・ログ・記録はフォルダーに残します。"));
            return string.Join("\n", lines);
        }
        lines.Add(plan.Contains(CleanupStep.DeleteData)
            ? L($"閉じるとアプリが終了し、そのあとで{DataDescription}を消します。")
            : L("閉じるとアプリが終了します（自動では起動し直しません）。設定・ログ・記録はフォルダーに残します。"));
        lines.Add(L($"アプリ本体は動いている間は消せません。使うのをやめるなら、終了したあとに次のフォルダーを削除してください（エクスプローラーで開きます）:\n{appFolder}"));
        return string.Join("\n", lines);
    }

    // ----- 設定・ログ・記録を消す（アプリが終わったあとに、別のプロセスで行う） -----

    /// <summary>消す対象のうち、いまあるもの（アプリのフォルダーの直下だけを見る。exe・dll は名前が合っても含めない）</summary>
    public static List<string> DataTargets(string appFolder)
    {
        var list = new List<string>();
        if (!Directory.Exists(appFolder)) return list;
        foreach (var name in DataFolders)
        {
            string dir = Path.Combine(appFolder, name);
            if (Directory.Exists(dir)) list.Add(dir);
        }
        foreach (var pattern in DataFilePatterns)
        {
            foreach (var file in Directory.GetFiles(appFolder, pattern, SearchOption.TopDirectoryOnly))
            {
                // GetFiles の * は、8.3 形式の短い名前にも当たることがあるので、名前を確かめ直す
                if (!MatchesPattern(Path.GetFileName(file), pattern)) continue;
                string ext = Path.GetExtension(file);
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (!list.Contains(file, StringComparer.OrdinalIgnoreCase)) list.Add(file);
            }
        }
        return list;
    }

    /// <summary>ファイル名が、* を含む名前の型に合うか（大文字・小文字は区別しない）</summary>
    public static bool MatchesPattern(string fileName, string pattern)
    {
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 設定・ログ・記録を消す。消せなかったものの一覧を返す（空なら全部消せた）。
    /// フォルダーの中の「リンク（ジャンクション・シンボリックリンク）」は、リンクそのものだけを消し、リンク先には入らない
    /// （管理者権限で動くので、リンクをたどってほかの場所のファイルを消さないように）
    /// </summary>
    public static List<string> DeleteData(string appFolder)
    {
        var failed = new List<string>();
        foreach (var target in DataTargets(appFolder))
        {
            if (Directory.Exists(target)) DeleteTree(target, failed);
            else DeleteFile(target, failed);
        }
        return failed;
    }

    private static void DeleteTree(string dir, List<string> failed)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(dir, recursive: false); // リンクそのものだけ消す
                return;
            }
            foreach (var file in Directory.GetFiles(dir)) DeleteFile(file, failed);
            foreach (var sub in Directory.GetDirectories(dir)) DeleteTree(sub, failed);
            Directory.Delete(dir, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!failed.Contains(dir)) failed.Add(dir);
        }
    }

    private static void DeleteFile(string file, List<string> failed)
    {
        try
        {
            var attrs = File.GetAttributes(file);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(file);
        }
    }
}
