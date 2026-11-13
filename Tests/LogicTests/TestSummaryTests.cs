// テストの集計の分け方（判定のないメソッドを成功と同じに数えない）
namespace LogicTests;

public static class TestSummaryTests
{
    public static void TestClassify()
    {
        T.Check(TestSummary.Classify(3, 0, false) == TestKind.Checked, "checks → checked");
        T.Check(TestSummary.Classify(1, 5, true) == TestKind.Checked, "checks win over skip");
        T.Check(TestSummary.Classify(0, 0, false) == TestKind.Skipped, "returned without output → skipped");
        T.Check(TestSummary.Classify(0, 4, true) == TestKind.Skipped, "Skip called → skipped");
        T.Check(TestSummary.Classify(0, 4, false) == TestKind.ReportOnly, "numbers only → report only");
    }
}
