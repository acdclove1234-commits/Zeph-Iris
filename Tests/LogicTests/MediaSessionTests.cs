// 曲の記憶が、Windows のメディアの情報からどの再生アプリの曲を使うか（MediaSession.Pick・SongOf）
using ProFanController;

namespace LogicTests;

public static class MediaSessionTests
{
    public static void TestPickPlayingOnly()
    {
        var paused = new List<MediaSessionEntry> { new("Brave", false, true), new("Spotify.exe", false, false) };
        T.Check(MediaSession.Pick(paused, null) == -1, "nothing playing → no song (a paused song is not remembered)");
        T.Check(MediaSession.Pick(new List<MediaSessionEntry>(), null) == -1, "no sessions → no song");
    }

    public static void TestPickPrefersCurrentThenFirst()
    {
        var s = new List<MediaSessionEntry> { new("Chrome", true, false), new("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", true, true) };
        T.Check(MediaSession.Pick(s, null) == 1, "Windows' current session wins when it is playing");
        var noCurrent = new List<MediaSessionEntry> { new("MSEdge", false, true), new("Chrome", true, false) };
        T.Check(MediaSession.Pick(noCurrent, null) == 1, "current session paused → first playing one");
    }

    public static void TestPickPrefersCapturedApp()
    {
        var s = new List<MediaSessionEntry> { new("Spotify.exe", true, true), new("Brave", true, false) };
        T.Check(MediaSession.Pick(s, "brave") == 1, "listening to one app only → that app's song, even if another is current");
        T.Check(MediaSession.Pick(s, "foobar2000") == 0, "captured app has no media info → fall back to the current one");
        var pausedPref = new List<MediaSessionEntry> { new("Brave", false, false), new("Spotify.exe", true, true) };
        T.Check(MediaSession.Pick(pausedPref, "brave") == 1, "captured app paused → not its old song");
    }

    public static void TestSongOf()
    {
        T.Check(MediaSession.SongOf("Chrome", "Artist", "Song") == ("Chrome", "Artist - Song"), "artist - title");
        T.Check(MediaSession.SongOf("Chrome", "", "Video") == ("Chrome", "Video"), "no artist → title alone");
        T.Check(MediaSession.SongOf("Chrome", "Artist", "  ") is null, "no title → not a song");
        T.Check(MediaSession.SongOf("Chrome", "Artist", null) is null, "null title → not a song");
    }

    public static void TestSpotifyKeepsOldKey()
    {
        // 前から Spotify のウィンドウのタイトル（「アーティスト - 曲名」）で覚えた曲と、同じ印になる
        string oldKey = SongMemory.KeyOf("Spotify", "Artist - Song");
        foreach (var id in new[] { "Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" })
        {
            var s = MediaSession.SongOf(id, "Artist", "Song");
            T.Check(s is { } v && SongMemory.KeyOf(v.App, v.Title) == oldKey, $"{id} → same key as the window title");
        }
        var other = MediaSession.SongOf("Brave", "Artist", "Song")!.Value;
        T.Check(SongMemory.KeyOf(other.App, other.Title) != oldKey, "the same song in another player is a separate entry (as before)");
    }
}
