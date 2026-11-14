// ==================================================
// SongMemory.cs - 一度聴いた曲の拍を覚えておき、次に同じ曲が流れたら最初から合わせる
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProFanController;

/// <summary>覚えた曲 1 つ（曲名は持たない。曲名の SHA-256 と、拍の時刻・始まりの音の強さの並びだけ）</summary>
public sealed class SongRecord
{
    /// <summary>曲を見分ける印（曲名の SHA-256 の先頭。元の曲名には戻せない）</summary>
    public string Key { get; set; } = "";
    /// <summary>拍の時刻（曲の始まりからの秒）・小節の頭</summary>
    public List<float> Beats { get; set; } = new();
    public List<float> Downbeats { get; set; } = new();
    /// <summary>曲の始まり（EnvelopeSec 秒）の立ち上がりの強さ（EnvelopeFps コマ／秒。0〜255）。流れ始めた時刻を合わせるのに使う</summary>
    public byte[] Envelope { get; set; } = Array.Empty<byte>();
    public double LengthSec { get; set; }
    public int Plays { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>流れている曲の印。Legacy は 6.15.5 までの印（再生アプリごと。なければ null）</summary>
public readonly record struct SongId(string Key, string? Legacy);

/// <summary>
/// 曲の拍の記憶。曲が変わったこと（<see cref="BeginSong"/>）を知らされると、
/// その曲の始まりの立ち上がりの強さを集め、拍の AI の結果（<see cref="AddBeats"/>）を曲の始まりからの時刻で貯める。
/// 覚えている曲なら、始まりの EnvelopeSec 秒の強さの並びを比べて流れ始めた時刻を合わせ（<see cref="AlignSec"/> 秒たったところで）、
/// 覚えた拍の並びを返す（<see cref="GridAt"/>）。曲が終わったら（次の曲・止まった）、拍が十分にたまっていれば保存する。
/// 音の取り込みのスレッドだけから使う（BeginSong は別のスレッドから呼べる）
/// </summary>
public sealed class SongMemory
{
    public const int EnvelopeFps = 50;
    public const double EnvelopeSec = 30, AlignSec = 4;
    /// <summary>曲名が変わった時刻と、本当に流れ始めた時刻のずれとして探す幅（秒）</summary>
    public const double AlignMinLag = -1.5, AlignMaxLag = 2.5;
    /// <summary>始まりの強さの並びがこれ以上似ていれば同じ曲の頭とみなす（相関）</summary>
    public const double AlignMinCorr = 0.6;
    /// <summary>覚えておく曲の数（古いものから消す）</summary>
    public const int MaxSongs = 2000;
    /// <summary>この長さ以上聴いて、拍がこの割合以上たまった曲だけを覚える</summary>
    public const double MinListenSec = 45, MinCoverage = 0.5;

    private readonly string _dir;
    private readonly Dictionary<string, SongRecord> _cache = new();

    // 今の曲
    private volatile Pending? _pending;
    private string? _key;
    private double _songStartT = double.NaN;   // 曲名が変わったのに気づいた時刻（音の時刻）
    private double _offset;                     // 本当に流れ始めた時刻 = _songStartT + _offset
    private readonly List<float> _env = new();
    private double _envNextT;
    private float _envAcc;
    private readonly List<double> _beats = new(), _downs = new();
    private SongRecord? _known;
    private bool _aligned, _alignTried;
    private double _lastT;

    /// <summary>覚えた曲に合わせられている（拍の AI を休ませてよい）</summary>
    public bool Aligned => _aligned && _known is not null;
    /// <summary>調査・ログ用</summary>
    public Action<string>? Log;
    public int Saved { get; private set; }
    public int Recalled { get; private set; }

    private int _version;
    /// <summary>覚えた曲を保存した・消したたびに増える（画面がこれを見て、覚えた曲の数を出し直す。どのスレッドからでも読める）</summary>
    public int Version => Volatile.Read(ref _version);

    public SongMemory(string dir)
    {
        _dir = dir;
    }

    /// <summary>
    /// 曲名（「アーティスト - 曲名」）から、曲を見分ける印を作る（曲名そのものは持たない・保存しない）。
    /// どの再生アプリで流しても同じ印になる（Spotify で覚えた曲を YouTube Music でも使える）
    /// </summary>
    public static string KeyOf(string title) => Hash(title);

    /// <summary>6.15.5 までの印（再生アプリ名と曲名）。前に覚えた曲を、新しい印に移すときに探す</summary>
    public static string LegacyKeyOf(string app, string title) => Hash(app.ToLowerInvariant() + "\n" + title);

    /// <summary>流れている曲の印（いまの印と、前の版の印）</summary>
    public static SongId IdOf(string app, string title) => new(KeyOf(title), LegacyKeyOf(app, title));

    private static string Hash(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 16).ToLowerInvariant();
    }

    /// <summary>曲が変わった（どのスレッドからでも。key が null なら曲名が分からない＝覚えない）</summary>
    public void BeginSong(string? key) => _pending = new Pending(key ?? "", null);

    /// <summary>曲が変わった（どのスレッドからでも）。前の版の印（legacy）で覚えた曲があれば、いまの印に移す</summary>
    public void BeginSong(string key, string? legacy) => _pending = new Pending(key, legacy);

    private sealed record Pending(string Key, string? Legacy);

    /// <summary>分析のコマごとに呼ぶ（tHop は音の時刻、odf は立ち上がりの強さ）</summary>
    public void Tick(double tHop, double odf, bool gateOpen)
    {
        _lastT = tHop;
        if (Interlocked.Exchange(ref _pending, null) is { } next)
        {
            EndSong(tHop);
            if (next.Key.Length > 0) StartSong(next, tHop);
        }
        if (_key is null) return;
        // 始まりの EnvelopeSec 秒の強さを 50 コマ／秒に詰める
        if (tHop - _songStartT <= EnvelopeSec + AlignMaxLag)
        {
            _envAcc = Math.Max(_envAcc, (float)Math.Max(0, odf));
            if (tHop >= _envNextT)
            {
                _env.Add(_envAcc);
                _envAcc = 0;
                _envNextT += 1.0 / EnvelopeFps;
            }
        }
        if (!_alignTried && _known is not null && tHop - _songStartT >= AlignSec + AlignMaxLag) Align();
    }

    private void StartSong(Pending id, double t)
    {
        string key = id.Key;
        _key = key;
        _songStartT = t;
        _offset = 0;
        _env.Clear();
        _envAcc = 0;
        _envNextT = t;
        _beats.Clear();
        _downs.Clear();
        _aligned = _alignTried = false;
        _known = Load(key);
        if (id.Legacy is { Length: > 0 } legacy && legacy != key) MoveLegacy(legacy, key);
    }

    /// <summary>
    /// 前の版の印（再生アプリごと）で覚えた曲を、いまの印に移す（覚え直さなくてよい）。
    /// いまの印でもう覚えていれば、前の版のほうは同じ曲の重なりなので消す
    /// </summary>
    private void MoveLegacy(string legacy, string key)
    {
        string oldPath = PathOf(legacy);
        if (!File.Exists(oldPath)) return;
        if (_known is null && Load(legacy) is { } old)
        {
            old.Key = key;
            Save(old);
            if (!File.Exists(PathOf(key))) return;   // 保存できなかった。前のほうを残す
            _known = old;
            Log?.Invoke("前の版で覚えた曲を、どのアプリでも使える形に移しました");   // no-loc（ログだけ）
        }
        else if (_known is null) return;   // 前のほうを読めなかった。そのまま残す
        _cache.Remove(legacy);
        try { File.Delete(oldPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Interlocked.Increment(ref _version);
    }

    /// <summary>覚えた曲の始まりの強さと、いま流れている始まりを比べて、流れ始めた時刻のずれを決める</summary>
    private void Align()
    {
        _alignTried = true;
        var stored = _known!.Envelope.Select(b => (float)b).ToArray();
        var live = _env.ToArray();
        int n = (int)(AlignSec * EnvelopeFps);
        double bestCorr = -1;
        int bestLag = 0;
        // live[i] = 曲名が変わってから i コマ目。stored[j] = 曲の頭から j コマ目。live[i] ≒ stored[i - lag]
        for (int lag = (int)(AlignMinLag * EnvelopeFps); lag <= (int)(AlignMaxLag * EnvelopeFps); lag++)
        {
            double c = Corr(live, stored, lag, n);
            if (c > bestCorr) { bestCorr = c; bestLag = lag; }
        }
        if (bestCorr >= AlignMinCorr)
        {
            _offset = bestLag / (double)EnvelopeFps;
            _aligned = true;
            Recalled++;
            Log?.Invoke($"覚えた曲: 流れ始めのずれ {_offset * 1000:F0} ms（似ている度合い {bestCorr:F2}）・拍 {_known.Beats.Count} 個");   // no-loc（ログだけ）
        }
        else Log?.Invoke($"覚えた曲の頭と合いません（{bestCorr:F2}）。途中から流したのかもしれません");   // no-loc（ログだけ）
    }

    private static double Corr(float[] live, float[] stored, int lag, int n)
    {
        double sl = 0, ss = 0, sll = 0, sss = 0, sls = 0;
        int m = 0;
        for (int i = 0; i < live.Length && m < n; i++)
        {
            int j = i - lag;
            if (j < 0 || j >= stored.Length) continue;
            double a = live[i], b = stored[j];
            sl += a; ss += b; sll += a * a; sss += b * b; sls += a * b;
            m++;
        }
        if (m < n / 2) return -1;
        double cov = sls - sl * ss / m, vl = sll - sl * sl / m, vs = sss - ss * ss / m;
        return vl > 1e-9 && vs > 1e-9 ? cov / Math.Sqrt(vl * vs) : -1;
    }

    /// <summary>拍の AI の結果（音の時刻）を、今の曲の拍として貯める（窓の端は除いて渡すこと）</summary>
    public void AddBeats(IEnumerable<double> beats, IEnumerable<double> downbeats)
    {
        if (_key is null) return;
        double origin = _songStartT + _offset;
        foreach (var t in beats) Insert(_beats, t - origin);
        foreach (var t in downbeats) Insert(_downs, t - origin);
    }

    /// <summary>近くに同じ拍がなければ、時刻の順に入れる（重なった窓で同じ拍が何度も来る）</summary>
    private static void Insert(List<double> list, double t)
    {
        if (t < 0) return;
        int i = list.BinarySearch(t);
        if (i < 0) i = ~i;
        if (i < list.Count && Math.Abs(list[i] - t) < 0.07) return;
        if (i > 0 && Math.Abs(list[i - 1] - t) < 0.07) return;
        list.Insert(i, t);
    }

    /// <summary>
    /// 覚えた曲に合わせられていれば、時刻 t（音の時刻）の拍の並び（周期・拍の時刻）を返す。
    /// 前後 GridSpanSec 秒の覚えた拍に「時刻 = a + 番号 × 周期」の直線を当てはめる（曲の中でテンポが変わっても追える）。
    /// 間隔がそろっていない（拍の間の音が混じった・抜けが多い）ところでは使わない
    /// </summary>
    public (double Period, double BeatT)? GridAt(double t)
    {
        if (!Aligned) return null;
        double origin = _songStartT + _offset, rel = t - origin;
        var b = _known!.Beats.Where(x => x >= rel - GridSpanSec && x <= rel + GridSpanSec).Select(x => (double)x).ToList();
        if (FitGrid(b) is not { } g) return null;
        // rel 以前でいちばん近い拍
        double k = Math.Floor((rel - g.A) / g.Period);
        return (g.Period, origin + g.A + k * g.Period);
    }

    /// <summary>覚えた拍から拍の並びを求めるときに見る幅（前後の秒）</summary>
    public const double GridSpanSec = 6;

    /// <summary>
    /// 拍の時刻に直線を当てはめる。8 個以上あり、間隔の 75% 以上が中央値の ±10% に収まり、直線からのずれが 30 ミリ秒以内のときだけ
    /// </summary>
    public static (double Period, double A)? FitGrid(IReadOnlyList<double> b)
    {
        if (b.Count < 8) return null;
        var ibi = b.Zip(b.Skip(1), (x, y) => y - x).OrderBy(x => x).ToList();
        double med = ibi[ibi.Count / 2];
        if (!(med > 0) || ibi.Count(d => Math.Abs(d / med - 1) < 0.1) < 0.75 * ibi.Count) return null;
        var k = b.Select(x => Math.Round((x - b[0]) / med)).ToList();
        double mk = k.Average(), mt = b.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < b.Count; i++) { sxy += (k[i] - mk) * (b[i] - mt); sxx += (k[i] - mk) * (k[i] - mk); }
        if (sxx <= 0) return null;
        double p = sxy / sxx, a = mt - p * mk;
        double rms = Math.Sqrt(b.Select((x, i) => Math.Pow(x - (a + p * k[i]), 2)).Average());
        return rms <= 0.030 ? (p, a) : null;
    }

    /// <summary>覚えた曲の頭と、いま流れている曲の頭のずれ（秒。調査用）</summary>
    public double OffsetSec => _offset;

    /// <summary>今の曲を、最後に分析した時刻で終える（調査・テスト用）</summary>
    public void EndSong() => EndSong(_lastT);

    /// <summary>曲が終わった（次の曲になった・止まった）: 拍が十分にたまっていれば覚える</summary>

    public void EndSong(double t)
    {
        if (_key is null) return;
        string key = _key;
        _key = null;
        double len = t - _songStartT;
        if (len < MinListenSec || _beats.Count < 16) return;
        double covered = _beats[^1] - _beats[0];
        if (covered < MinCoverage * len) return;
        // 覚えている曲なのに頭が合わなかった（途中から流した）: 時刻の基準が違うので、混ぜずにそのままにする
        if (_known is not null && !_aligned) return;
        var rec = _known ?? new SongRecord { Key = key };
        // 前に覚えた拍があれば合わせる（聴くたびに抜けた部分が埋まる）
        var merged = rec.Beats.Select(x => (double)x).ToList();
        foreach (var x in _beats) Insert(merged, x);
        var mergedDown = rec.Downbeats.Select(x => (double)x).ToList();
        foreach (var x in _downs) Insert(mergedDown, x);
        rec.Beats = merged.Select(x => (float)x).ToList();
        rec.Downbeats = mergedDown.Select(x => (float)x).ToList();
        if (rec.Envelope.Length == 0 || !_aligned)
        {
            float max = Math.Max(1e-6f, _env.DefaultIfEmpty(0).Max());
            rec.Envelope = _env.Take((int)(EnvelopeSec * EnvelopeFps)).Select(v => (byte)Math.Clamp(v / max * 255, 0, 255)).ToArray();
        }
        rec.LengthSec = Math.Max(rec.LengthSec, len);
        rec.Plays++;
        rec.UpdatedAt = DateTime.Now;
        Save(rec);
        Saved++;
        Log?.Invoke($"曲の拍を覚えました: 拍 {rec.Beats.Count} 個・{rec.Plays} 回目");   // no-loc（ログだけ）
    }

    // ---------------------------------------------------------------------
    // 保存（data/songs/<印>.json。1 曲 数 KB）
    // ---------------------------------------------------------------------

    private string PathOf(string key) => Path.Combine(_dir, key + ".json");

    private SongRecord? Load(string key)
    {
        if (_cache.TryGetValue(key, out var r)) return r;
        try
        {
            string p = PathOf(key);
            if (!File.Exists(p)) return null;
            r = JsonSerializer.Deserialize<SongRecord>(File.ReadAllText(p));
            if (r is not null) _cache[key] = r;
            return r;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log?.Invoke($"覚えた曲を読めません: {ex.Message}");   // no-loc（ログだけ）
            return null;
        }
    }

    private void Save(SongRecord r)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(PathOf(r.Key), JsonSerializer.Serialize(r));
            _cache[r.Key] = r;
            Prune();
            Interlocked.Increment(ref _version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"曲の拍を保存できません: {ex.Message}");   // no-loc（ログだけ）
        }
    }

    /// <summary>覚えた曲が MaxSongs を超えたら、古いものから消す</summary>
    private void Prune()
    {
        var files = new DirectoryInfo(_dir).GetFiles("*.json");
        if (files.Length <= MaxSongs) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxSongs))
        {
            try { f.Delete(); } catch (IOException) { }
            _cache.Remove(Path.GetFileNameWithoutExtension(f.Name));
        }
    }

    /// <summary>覚えた曲の数（設定の画面に出す）</summary>
    public int Count => Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.json").Length : 0;

    /// <summary>覚えた曲をすべて消す</summary>
    public void Clear()
    {
        _cache.Clear();
        Interlocked.Increment(ref _version);
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir, "*.json")) { try { File.Delete(f); } catch (IOException) { } }
    }
}
