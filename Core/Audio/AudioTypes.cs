// ==================================================
// AudioTypes.cs - 音楽連動の共通の型 (時計・分析結果の 1 コマ・ビートの状態・出来事の記録)
//   音を取り込むスレッドが書き、LED を光らせるスレッドと画面が読む
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

/// <summary>
/// 音楽連動の時計（秒）。DateTime.Now は 15.6 ミリ秒ごとにしか進まず、時刻合わせで飛ぶことがあるので使わない。
/// 音の取り込み・分析・LED の描画は、すべてこの時計で時刻をそろえる
/// </summary>
public static class AudioClock
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>音の中の「出来事」の種類</summary>
public enum AudioEventKind
{
    /// <summary>バスドラム（30〜120 Hz の立ち上がり）</summary>
    Kick,
    /// <summary>スネア・クラップ（中音の強い立ち上がり。キックと同時ではない）</summary>
    Snare,
    /// <summary>ハイハット・シンバル（4 kHz 以上の立ち上がり）</summary>
    Hat,
    /// <summary>全体の立ち上がり（どの楽器かは問わない）</summary>
    Onset,
    /// <summary>曲の盛り上がり（静かな部分のあと、低音が戻ってきた瞬間）</summary>
    Drop,
    /// <summary>同期テストのクリック音（AudioEngine.ClickDetect が true のときだけ）</summary>
    Click,
}

/// <param name="Id">通し番号（1 から増えていく）。LED 側はどこまで読んだかをこれで覚える</param>
/// <param name="T">音が鳴った本当の時刻（AudioClock の秒。検出した時刻ではない）</param>
/// <param name="Strength">強さ（しきい値の何倍か。1 以上）</param>
public readonly record struct AudioEvent(long Id, AudioEventKind Kind, double T, float Strength);

/// <summary>
/// ある瞬間の音の分析結果（約 10.7 ミリ秒ごとに 1 つ）。値は特に書いていなければ 0〜1。
/// 毎回作り直さないよう、配列は最初に確保して使い回す
/// </summary>
public sealed class AudioFrame
{
    /// <summary>周波数の帯の数（一般的な 10 バンドのグラフィックイコライザーと同じ、1 オクターブごとの 10 帯: 31・63・125・250・500 Hz・1・2・4・8・16 kHz）</summary>
    public const int BandCount = 10;

    /// <summary>このコマのいちばん新しい音の時刻（AudioClock の秒）</summary>
    public double T;
    /// <summary>帯ごとの「鳴っている大きさ」（表示用。滑らか）</summary>
    public readonly float[] Band = new float[BandCount];
    /// <summary>帯ごとの「立ち上がりを強調した大きさ」（LED 用。ドラムの瞬間に強く出る）</summary>
    public readonly float[] Punch = new float[BandCount];
    /// <summary>
    /// まとめた帯の大きさ: 超低音 40〜85 Hz・低音 40〜230 Hz・中音 230〜2,200 Hz・高音 2.2〜16 kHz
    /// （判定用の細かい 24 帯から求める。10 帯からまとめると、低音の中でベースの音がキックを埋もれさせるため。BandAnalyzer を参照）
    /// </summary>
    public float Sub, Bass, Mid, Treble;
    /// <summary>まとめた帯の「立ち上がりを強調した大きさ」。BassE はキックの検出（遅れがほぼない）も含む</summary>
    public float SubE, BassE, MidE, TrebleE;
    /// <summary>キックの包絡線（150 Hz 以下の音の大きさ。遅れ 2〜8 ミリ秒）</summary>
    public float KickEnv;
    /// <summary>聞こえ方に合わせた音量（VU メーターのような動き）</summary>
    public float LoudVU;
    /// <summary>聞こえ方に合わせた音量（dBFS）</summary>
    public float LoudDb = -120;
    /// <summary>全体の音量（dBFS）</summary>
    public float RmsDb = -120;
    /// <summary>自動音量調整の基準（dBFS。この値が「最大」として光る）</summary>
    public float CeilingDb = -30;
    /// <summary>音の重心（Hz。低い音が中心なら小さく、明るい音なら大きい）</summary>
    public float CentroidHz = 500;
    /// <summary>反応の強さ（音が鳴っているか × 「特定のアプリだけ」の割合）</summary>
    public float Activity;
    /// <summary>音が鳴っている（無音判定の門が開いている）</summary>
    public bool GateOpen;
    /// <summary>まだ届いていない未来の時刻を求められたので、いちばん新しいコマをそのまま返した</summary>
    public bool Extrapolated;

    public void CopyFrom(AudioFrame o)
    {
        T = o.T;
        Array.Copy(o.Band, Band, BandCount);
        Array.Copy(o.Punch, Punch, BandCount);
        Sub = o.Sub; Bass = o.Bass; Mid = o.Mid; Treble = o.Treble;
        SubE = o.SubE; BassE = o.BassE; MidE = o.MidE; TrebleE = o.TrebleE;
        KickEnv = o.KickEnv; LoudVU = o.LoudVU; LoudDb = o.LoudDb; RmsDb = o.RmsDb; CeilingDb = o.CeilingDb;
        CentroidHz = o.CentroidHz; Activity = o.Activity; GateOpen = o.GateOpen; Extrapolated = o.Extrapolated;
    }

    /// <summary>a と b の間（u = 0 で a、1 で b）を直線で補う</summary>
    public void Lerp(AudioFrame a, AudioFrame b, double u)
    {
        float k = (float)Math.Clamp(u, 0, 1);
        float L(float x, float y) => x + (y - x) * k;
        T = a.T + (b.T - a.T) * k;
        for (int i = 0; i < BandCount; i++)
        {
            Band[i] = L(a.Band[i], b.Band[i]);
            Punch[i] = L(a.Punch[i], b.Punch[i]);
        }
        Sub = L(a.Sub, b.Sub); Bass = L(a.Bass, b.Bass); Mid = L(a.Mid, b.Mid); Treble = L(a.Treble, b.Treble);
        SubE = L(a.SubE, b.SubE); BassE = L(a.BassE, b.BassE); MidE = L(a.MidE, b.MidE); TrebleE = L(a.TrebleE, b.TrebleE);
        KickEnv = L(a.KickEnv, b.KickEnv); LoudVU = L(a.LoudVU, b.LoudVU); LoudDb = L(a.LoudDb, b.LoudDb);
        RmsDb = L(a.RmsDb, b.RmsDb); CeilingDb = L(a.CeilingDb, b.CeilingDb); CentroidHz = L(a.CentroidHz, b.CentroidHz);
        Activity = L(a.Activity, b.Activity);
        GateOpen = k < 0.5 ? a.GateOpen : b.GateOpen;
        Extrapolated = false;
    }

    /// <summary>無音の状態に戻す</summary>
    public void Clear()
    {
        T = 0;
        Array.Clear(Band);
        Array.Clear(Punch);
        Sub = Bass = Mid = Treble = SubE = BassE = MidE = TrebleE = KickEnv = LoudVU = Activity = 0;
        LoudDb = RmsDb = -120;
        CeilingDb = -30;
        CentroidHz = 500;
        GateOpen = Extrapolated = false;
    }
}

/// <summary>
/// テンポとビートの位置（変更しない値。分析スレッドが丸ごと差し替える）。
/// 過去・未来のどの時刻でも、ビートの何拍目か・拍の中のどこか（0〜1）を計算で求められる
/// </summary>
/// <param name="Period">1 拍の長さ（秒）</param>
/// <param name="LastBeatT">基準にするビートの時刻（AudioClock の秒。まだなければ負の無限大）</param>
/// <param name="IndexAtLast">基準のビートが何拍目か</param>
/// <param name="Confidence">確かさ（0〜1）</param>
/// <param name="Locked">テンポをつかんでいる（ビートを予測して光らせてよい）</param>
/// <param name="DownbeatOffset">小節の頭（1 拍目）になる拍の番号の余り（0〜3）</param>
public sealed record BeatState(double Period, double LastBeatT, long IndexAtLast, double Confidence, bool Locked, int DownbeatOffset)
{
    public static readonly BeatState None = new(0.5, double.NegativeInfinity, 0, 0, false, 0);

    public double Bpm => Period > 0 ? 60 / Period : 0;
    public bool HasTempo => Period > 0 && !double.IsInfinity(LastBeatT);

    /// <summary>時刻 t が拍の中のどこか（0 = 拍の頭、1 に近いほど次の拍の直前）</summary>
    public double Phase(double t)
    {
        if (!HasTempo) return 0;
        double x = (t - LastBeatT) / Period;
        return x - Math.Floor(x);
    }

    /// <summary>時刻 t が何拍目か</summary>
    public long Index(double t) => HasTempo ? IndexAtLast + (long)Math.Floor((t - LastBeatT) / Period) : 0;

    /// <summary>小節の中の何拍目か（0〜3。0 が小節の頭）</summary>
    public int Bar(double t) => (int)(((Index(t) - DownbeatOffset) % 4 + 4) % 4);

    /// <summary>t0 から t1 の間（t0 は含まず t1 は含む）に拍の頭をまたいだか</summary>
    public bool Crossed(double t0, double t1) => HasTempo && t1 > t0 && Index(t1) > Index(t0);

    /// <summary>t より後で最初の拍の頭の時刻</summary>
    public double NextBeatAfter(double t)
    {
        if (!HasTempo) return double.PositiveInfinity;
        double k = Math.Floor((t - LastBeatT) / Period) + 1;
        return LastBeatT + k * Period;
    }
}

/// <summary>
/// 分析結果の記録（直近 128 コマ ≈ 1.4 秒）。書くのは分析スレッドだけ。
/// LED 側は「音の遅れの補正」のために、少し前の時刻の値を補いながら読む
/// </summary>
public sealed class FeatureRing
{
    public const int Capacity = 128;
    private readonly AudioFrame[] _frames = new AudioFrame[Capacity];
    private readonly object _gate = new();
    private int _count;
    private int _head; // 次に書く位置

    public FeatureRing()
    {
        for (int i = 0; i < Capacity; i++) _frames[i] = new AudioFrame();
    }

    /// <summary>いちばん新しいコマの時刻（まだなければ負の無限大）</summary>
    public double LatestT
    {
        get { lock (_gate) return _count == 0 ? double.NegativeInfinity : _frames[(_head - 1 + Capacity) % Capacity].T; }
    }

    public int Count { get { lock (_gate) return _count; } }

    /// <summary>新しいコマを追加する（中身をコピーする）。時刻は前のコマより新しいこと</summary>
    public void Write(AudioFrame f)
    {
        lock (_gate)
        {
            _frames[_head].CopyFrom(f);
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    public void Clear()
    {
        lock (_gate) { _count = 0; _head = 0; }
    }

    /// <summary>いちばん新しいコマを dst にコピーする（なければ無音にして false）</summary>
    public bool Latest(AudioFrame dst)
    {
        lock (_gate)
        {
            if (_count == 0) { dst.Clear(); return false; }
            dst.CopyFrom(_frames[(_head - 1 + Capacity) % Capacity]);
            dst.Extrapolated = false;
            return true;
        }
    }

    /// <summary>
    /// 時刻 t の値を dst に入れる（前後のコマの間を直線で補う）。
    /// t がいちばん新しいコマより新しければ、いちばん新しいコマを返して Extrapolated = true。
    /// いちばん古いコマより古ければ、いちばん古いコマを返す。記録が空なら無音にして false
    /// </summary>
    public bool Sample(double t, AudioFrame dst)
    {
        lock (_gate)
        {
            if (_count == 0) { dst.Clear(); return false; }
            int newest = (_head - 1 + Capacity) % Capacity;
            int oldest = (_head - _count + Capacity) % Capacity;
            if (t >= _frames[newest].T)
            {
                dst.CopyFrom(_frames[newest]);
                dst.Extrapolated = t > _frames[newest].T;
                return true;
            }
            if (t <= _frames[oldest].T)
            {
                dst.CopyFrom(_frames[oldest]);
                dst.Extrapolated = false;
                return true;
            }
            // 新しい方から探す（ふつうは 0〜数コマ前なのですぐ見つかる）
            for (int k = 1; k < _count; k++)
            {
                var b = _frames[(newest - k + 1 + Capacity) % Capacity];
                var a = _frames[(newest - k + Capacity) % Capacity];
                if (t >= a.T)
                {
                    double span = b.T - a.T;
                    dst.Lerp(a, b, span > 0 ? (t - a.T) / span : 1);
                    return true;
                }
            }
            dst.CopyFrom(_frames[oldest]);
            dst.Extrapolated = false;
            return true;
        }
    }
}

/// <summary>
/// 出来事（キック・スネアなど）の記録（直近 64 個）。
/// 検出には少し時間がかかるため、出来事の時刻 T は追加した時点より過去になる。
/// 読む側は「どこまで読んだか（通し番号）」を自分で覚えておき、Read で続きを受け取る
/// </summary>
public sealed class EventRing
{
    public const int Capacity = 64;
    private readonly AudioEvent[] _items = new AudioEvent[Capacity];
    private readonly object _gate = new();
    private long _nextId = 1;
    private int _count;
    private int _head;

    /// <summary>種類ごとの、これまでに追加した数（記録の統計用。書くのは分析スレッドだけ）</summary>
    public readonly long[] CountByKind = new long[8];

    /// <summary>これまでに追加した最後の通し番号（まだなければ 0）</summary>
    public long LastId { get { lock (_gate) return _nextId - 1; } }

    /// <summary>出来事を追加し、付けた通し番号を返す</summary>
    public long Add(AudioEventKind kind, double t, float strength)
    {
        lock (_gate)
        {
            long id = _nextId++;
            int ki = (int)kind;
            if (ki >= 0 && ki < CountByKind.Length) CountByKind[ki]++;
            _items[_head] = new AudioEvent(id, kind, t, strength);
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            return id;
        }
    }

    /// <summary>
    /// cursor より後に追加された出来事のうち、時刻が tUntil 以前のものを、追加した順に dst へ足す。
    /// 時刻がまだ tUntil より先の出来事に当たったら、そこで止める（次の回に読む）。
    /// cursor は読み終えた最後の通し番号に進める。戻り値は足した数
    /// </summary>
    public int Read(ref long cursor, double tUntil, List<AudioEvent> dst)
    {
        lock (_gate)
        {
            int added = 0;
            long oldestId = _nextId - _count;
            if (cursor < oldestId - 1) cursor = oldestId - 1; // 読み遅れて上書きされた分は飛ばす
            for (long id = cursor + 1; id < _nextId; id++)
            {
                var e = _items[(int)((_head - (_nextId - id) + Capacity * 2) % Capacity)];
                if (e.T > tUntil) break;
                dst.Add(e);
                cursor = id;
                added++;
            }
            return added;
        }
    }

    /// <summary>時刻が t0 より後で t1 以前の出来事を dst へ足す（画面の表示用。読んだ位置は覚えない）</summary>
    public int Between(double t0, double t1, List<AudioEvent> dst)
    {
        lock (_gate)
        {
            int added = 0;
            for (int k = _count; k >= 1; k--)
            {
                var e = _items[(_head - k + Capacity) % Capacity];
                if (e.T > t0 && e.T <= t1) { dst.Add(e); added++; }
            }
            return added;
        }
    }

    public void Clear()
    {
        lock (_gate) { _count = 0; _head = 0; }
    }
}
