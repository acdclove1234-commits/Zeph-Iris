// ==================================================
// Program.cs - ロジックテストの実行 (名前が "Tests" で終わるクラスの、"Test" で始まる public static メソッドを全部呼ぶ)
//   最後の集計では、メソッドを「判定したもの」「飛ばしたもの（素材・環境変数がない）」「数値を出すだけのもの」に分けて数える
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Reflection;

namespace LogicTests;

/// <summary>テストの結果を数える</summary>
public static class T
{
    public static int Passed;
    public static int Failed;
    public static readonly List<string> Failures = new();
    public static string Current = "";
    /// <summary>いまのメソッドの判定の数・調査の出力の数・飛ばした理由</summary>
    public static int MethodChecks, MethodInfos;
    public static string? MethodSkipped;

    public static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        MethodChecks++;
        if (ok) Passed++;
        else { Failed++; Failures.Add($"{Current}: {name}"); }
    }

    public static void Near(double actual, double expected, double tol, string name) =>
        Check(Math.Abs(actual - expected) <= tol, $"{name} (actual {actual:G6}, expected {expected:G6} ± {tol:G3})");

    public static void Info(string text)
    {
        MethodInfos++;
        Console.WriteLine("     " + text);
    }

    /// <summary>素材・環境がないので、このメソッドは判定しない（集計で「飛ばした」に数える）</summary>
    public static void Skip(string reason)
    {
        MethodSkipped = reason;
        Console.WriteLine("SKIP  " + reason);
    }
}

/// <summary>メソッドの分け方（判定のないメソッドを、成功と同じに数えないため）</summary>
public enum TestKind { Checked, Skipped, ReportOnly }

public static class TestSummary
{
    /// <summary>判定があれば Checked。判定がなく、飛ばした（Skip・何も出さずに戻った）なら Skipped、数値だけ出したなら ReportOnly</summary>
    public static TestKind Classify(int checks, int infos, bool skipped) =>
        checks > 0 ? TestKind.Checked : skipped || infos == 0 ? TestKind.Skipped : TestKind.ReportOnly;
}

public static class Program
{
    public static int Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0] : "";
        var tests = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Tests") && t.IsClass)
            .OrderBy(t => t.Name)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Test") && m.GetParameters().Length == 0)
                .OrderBy(m => m.MetadataToken)
                .Select(m => (Type: t, Method: m)))
            .Where(x => filter.Length == 0 || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var kinds = new List<(string Name, TestKind Kind)>();
        foreach (var (type, method) in tests)
        {
            T.Current = $"{type.Name}.{method.Name}";
            T.MethodChecks = 0;
            T.MethodInfos = 0;
            T.MethodSkipped = null;
            Console.WriteLine($"\n===== {T.Current}");
            try { method.Invoke(null, null); }
            catch (TargetInvocationException ex)
            {
                T.Check(false, $"threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}\n{ex.InnerException?.StackTrace}");
            }
            kinds.Add((T.Current, TestSummary.Classify(T.MethodChecks, T.MethodInfos, T.MethodSkipped is not null)));
        }
        int Count(TestKind k) => kinds.Count(x => x.Kind == k);
        Console.WriteLine($"\n{T.Passed} passed, {T.Failed} failed ({tests.Count} test methods: {Count(TestKind.Checked)} checked, " +
                          $"{Count(TestKind.Skipped)} skipped (no material or not enabled), {Count(TestKind.ReportOnly)} report only)");
        foreach (var k in new[] { TestKind.Skipped, TestKind.ReportOnly })
        {
            var names = kinds.Where(x => x.Kind == k).Select(x => x.Name).ToList();
            if (names.Count > 0) Console.WriteLine($"  {(k == TestKind.Skipped ? "skipped" : "report only")}: {string.Join(", ", names)}");
        }
        foreach (var f in T.Failures) Console.WriteLine("  FAILED: " + f);
        // 絞り込みに 1 件も当たらないときは失敗にする（dotnet run の余分な引数が絞り込みとして渡ったときに気づけるように）
        if (tests.Count == 0)
        {
            Console.WriteLine($"NO TESTS MATCHED (filter: \"{filter}\")");
            return 1;
        }
        Console.WriteLine(T.Failed == 0 ? "ALL PASS" : "SOME FAILED");
        return T.Failed == 0 ? 0 : 1;
    }
}
