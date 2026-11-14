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

    public static void TestRetryAfterFailure()
    {
        // メディアのサービスが止まった・応答しない → 少し待って試し直す（アプリを起動し直さなくても戻る）
        T.Check(MediaSession.NextTry(1000, new InvalidOperationException("RPC_E_DISCONNECTED")) == 1000 + MediaSession.RetryMs, "service error → retry later");
        T.Check(MediaSession.RetryMs is >= 10_000 and <= 300_000, "retry wait is not too short or too long");
        // 必要なものが Windows にない → もう試さない
        T.Check(MediaSession.NextTry(1000, new DllNotFoundException()) == long.MaxValue, "missing DLL → never retry");
        T.Check(MediaSession.NextTry(1000, new EntryPointNotFoundException()) == long.MaxValue, "missing function → never retry");
    }

    public static void TestSameSongInAnyPlayer()
    {
        // どの再生アプリで流しても同じ印（Spotify で覚えた曲を YouTube Music でも使う）
        string key = SongMemory.KeyOf("Artist - Song");
        foreach (var app in new[] { "Spotify.exe", "Chrome", "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App" })
        {
            var s = MediaSession.SongOf(app, "Artist", "Song")!.Value;
            T.Check(SongMemory.IdOf(s.App, s.Title).Key == key, $"{app} → the same key");
        }
        T.Check(SongMemory.KeyOf("Artist - Other") != key, "another song → another key");
    }

    public static void TestSpotifyKeepsOldKey()
    {
        // 6.15.5 までに Spotify のウィンドウのタイトル（「アーティスト - 曲名」）で覚えた曲を探せる（前の版の印が同じ）
        string oldKey = SongMemory.LegacyKeyOf("Spotify", "Artist - Song");
        foreach (var id in new[] { "Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" })
        {
            var s = MediaSession.SongOf(id, "Artist", "Song");
            T.Check(s is { } v && SongMemory.IdOf(v.App, v.Title).Legacy == oldKey, $"{id} → finds the old Spotify entry");
        }
        var other = MediaSession.SongOf("Brave", "Artist", "Song")!.Value;
        T.Check(SongMemory.IdOf(other.App, other.Title).Legacy == SongMemory.LegacyKeyOf("Brave", "Artist - Song"), "another player → its own old entry");
        T.Check(SongMemory.KeyOf("Artist - Song") != oldKey, "the new key differs from the old one (old entries are moved, not mixed up)");
    }

    public static void TestOldEntryIsMoved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zi-songmove-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var id = SongMemory.IdOf("Spotify", "Artist - Song");
            string oldPath = Path.Combine(dir, id.Legacy + ".json"), newPath = Path.Combine(dir, id.Key + ".json");
            var rec = new SongRecord { Key = id.Legacy!, Beats = { 1f, 2f, 3f }, Plays = 2 };
            File.WriteAllText(oldPath, System.Text.Json.JsonSerializer.Serialize(rec));

            var mem = new SongMemory(dir);
            int v0 = mem.Version;
            mem.BeginSong(id.Key, id.Legacy);
            mem.Tick(0, 0, false);
            T.Check(File.Exists(newPath) && !File.Exists(oldPath), "old entry moved to the new key");
            var moved = System.Text.Json.JsonSerializer.Deserialize<SongRecord>(File.ReadAllText(newPath))!;
            T.Check(moved.Key == id.Key && moved.Beats.Count == 3 && moved.Plays == 2, "beats and plays kept");
            T.Check(mem.Count == 1 && mem.Version > v0, "still one song; the screen is told to refresh the count");

            // 新しい印でもう覚えている → 前の版の重なりは消すだけ（新しいほうは変えない）
            File.WriteAllText(oldPath, System.Text.Json.JsonSerializer.Serialize(new SongRecord { Key = id.Legacy!, Plays = 9 }));
            var mem2 = new SongMemory(dir);
            mem2.BeginSong(id.Key, id.Legacy);
            mem2.Tick(0, 0, false);
            T.Check(!File.Exists(oldPath), "duplicate old entry removed");
            var kept = System.Text.Json.JsonSerializer.Deserialize<SongRecord>(File.ReadAllText(newPath))!;
            T.Check(kept.Plays == 2, "the new entry is not replaced by the old one");

            // 前の版の印がない・前の印を渡さない → 何もしない
            var mem3 = new SongMemory(dir);
            mem3.BeginSong(SongMemory.KeyOf("Other - Song"), SongMemory.LegacyKeyOf("Chrome", "Other - Song"));
            mem3.Tick(0, 0, false);
            mem3.BeginSong(SongMemory.KeyOf("Third - Song"));
            mem3.Tick(1, 0, false);
            T.Check(mem3.Count == 1, "no old entry → nothing created or removed");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
