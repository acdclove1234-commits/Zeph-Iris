// ==================================================
// TitleWatch.cs - 曲名の見張り（MusicAi）の結果から、曲の記憶とテンポの追跡に何を知らせるかを決める（テストできるよう、画面の部分から分けてある）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>曲名を見た結果から、することを決める（MusicAi.WatchTitle）。曲名そのものは扱わない（印だけ）</summary>
public static class TitleWatch
{
    public enum Act
    {
        /// <summary>何もしない（前と同じ曲・速く見て読めなかった）</summary>
        None,
        /// <summary>止めた・曲が分からない: 曲の記憶に「覚えない」と知らせる</summary>
        Stopped,
        /// <summary>曲が始まった（最初の曲・止めたあとに同じ曲を続けた）: 曲の記憶にだけ知らせる</summary>
        Started,
        /// <summary>別の曲になった: テンポの追跡（AudioEngine.NewSong）にも知らせる</summary>
        Changed,
    }

    /// <param name="full">全部を見直した（false = 前に曲名を読めた Spotify のウィンドウだけを速く見た）</param>
    /// <param name="id">いまの曲の印（読めなければ null）</param>
    /// <param name="lastKey">前に見た結果の印（止めていれば null）</param>
    /// <param name="lastSongKey">最後に流れていた曲の印（止めても残す）</param>
    public static Act Decide(bool full, SongId? id, string? lastKey, string? lastSongKey)
    {
        // 速く見たとき: 別の曲名を読めたときだけ使う（止めた・読めないは、全部を見直すときに決める）
        if (!full && id is null) return Act.None;
        if (id?.Key == lastKey) return Act.None;
        if (id is not { } s) return Act.Stopped;
        // 止めたあとに同じ曲を続けたときは、曲が変わったとしない（拍の並びを確かめ直さない）
        return lastSongKey is not null && s.Key != lastSongKey ? Act.Changed : Act.Started;
    }
}
