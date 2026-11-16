// ==================================================
// TitleWatchTests.cs - 曲名の見張り（MusicAi）の結果から、曲の記憶とテンポの追跡に何を知らせるか
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class TitleWatchTests
{
    /// <summary>
    /// 速く見て読めなかった・同じ曲は何もしない。止めたら「覚えない」、止めたあとに同じ曲を続けても曲が変わったとしない（拍の並びを確かめ直さない）。
    /// 別の曲になったときだけテンポの追跡に知らせる
    /// </summary>
    public static void TestTitleWatchDecisions()
    {
        SongId a = new("aaaa", null), b = new("bbbb", null);
        T.Check(TitleWatch.Decide(false, null, "aaaa", "aaaa") == TitleWatch.Act.None, "quick look, nothing read: nothing happens (stopping is decided by the full look)");
        T.Check(TitleWatch.Decide(true, a, "aaaa", "aaaa") == TitleWatch.Act.None && TitleWatch.Decide(false, a, "aaaa", "aaaa") == TitleWatch.Act.None, "same song: nothing happens");
        T.Check(TitleWatch.Decide(true, null, "aaaa", "aaaa") == TitleWatch.Act.Stopped, "full look, nothing read (paused): the song memory stops");
        T.Check(TitleWatch.Decide(true, a, null, "aaaa") == TitleWatch.Act.Started && TitleWatch.Decide(false, a, null, "aaaa") == TitleWatch.Act.Started,
            "the same song goes on after a pause: started again, but not a song change (no NewSong)");
        T.Check(TitleWatch.Decide(true, a, null, null) == TitleWatch.Act.Started, "the first song: started, not a song change");
        T.Check(TitleWatch.Decide(false, b, "aaaa", "aaaa") == TitleWatch.Act.Changed && TitleWatch.Decide(true, b, null, "aaaa") == TitleWatch.Act.Changed,
            "another song (also right after a pause): a song change (NewSong)");
    }
}
