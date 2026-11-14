// ==================================================
// BeatThis.cs - AI で曲の拍を見つける（Beat This!: Foscarin, Schlüter, Widmer 2024・JKU Linz・MIT）
//   音（22,050 Hz・モノラル）→ mel スペクトログラム（ONNX・1 秒 50 コマ・128 帯）→ 拍の AI（ONNX・30 秒ずつ）→ 山を拾う
// Dependencies: ONNX Runtime (MIT License), NAudio (MIT License), Beat This! / beat-this-rs のモデル (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Dsp;

namespace ProFanController;

/// <summary>
/// 拍の AI。曲を少し後から（30 秒ぶんをまとめて）見るので、リアルタイムの拍の追跡よりずっと正確。
/// 使い方: 鳴っている音を <see cref="Resampler22k"/> で 22,050 Hz にして貯め、数秒おきに <see cref="Analyze"/> で拍の時刻を得る。
/// モデルは exe に埋め込んである（約 11 MB）。読み込みに失敗したら null（AI なしで動く）
/// </summary>
public sealed class BeatThis : IDisposable
{
    public const int SampleRate = 22050;
    /// <summary>mel スペクトログラムのコマ（1 秒あたり）</summary>
    public const int Fps = 50;
    /// <summary>AI が一度に見る長さ（コマ。30 秒）と、つなぎ目で捨てる端（コマ）。元の Python の split_piece と同じ</summary>
    public const int ChunkFrames = 1500, BorderFrames = 6;
    /// <summary>山を拾う幅（前後 ±3 コマ＝±60 ミリ秒の中でいちばん高いところ）</summary>
    private const int PeakRadius = 3;

    private readonly InferenceSession _mel, _beat;

    /// <summary>
    /// 1 回の解析が終わるたびに、ONNX Runtime が CPU で確保した作業用メモリ（アリーナ）を返す。
    /// 返さないと、毎回少しずつ長さの違う音を渡すうちにアリーナが育ち、音楽連動中だけで約 1.2 GB に膨らむ
    /// （返しても約 60 MB のまま。解析は 1 割ほど遅くなるだけ）
    /// </summary>
    private readonly RunOptions _run = ShrinkAfterRun();

    private BeatThis(InferenceSession mel, InferenceSession beat) { _mel = mel; _beat = beat; }

    private static RunOptions ShrinkAfterRun()
    {
        var r = new RunOptions();
        r.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
        return r;
    }

    /// <summary>モデルを読み込む（少し時間とメモリを使うので、音楽連動で使うときだけ）。失敗したら null と理由</summary>
    public static BeatThis? TryCreate(out string? error)
    {
        try
        {
            var opt = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var mel = new InferenceSession(Resource("mel_spectrogram"), opt);
            var beat = new InferenceSession(Resource("beat_this_small"), opt);
            error = null;
            return new BeatThis(mel, beat);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static byte[] Resource(string name)
    {
        using var s = typeof(BeatThis).Assembly.GetManifestResourceStream($"ProFanController.Models.{name}.onnx")
                      ?? throw new FileNotFoundException(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>22,050 Hz の音から、拍とその小節の頭の時刻（秒。音の先頭から）を見つける</summary>
    public (List<double> Beats, List<double> Downbeats) Analyze(ReadOnlySpan<float> audio22k)
    {
        if (audio22k.Length < SampleRate) return (new(), new());
        var mel = Mel(audio22k, out int frames);
        var (beat, down) = Logits(mel, frames);
        return (Peaks(beat), Peaks(down));
    }

    /// <summary>音 → mel スペクトログラム（コマ × 128 を 1 列に並べたもの）</summary>
    private float[] Mel(ReadOnlySpan<float> audio, out int frames)
    {
        var input = new DenseTensor<float>(audio.ToArray(), new[] { 1, audio.Length });
        using var res = _mel.Run(new[] { NamedOnnxValue.CreateFromTensor("audio_pcm", input) }, _mel.OutputMetadata.Keys.ToList(), _run);
        var t = res.First().AsTensor<float>();
        frames = t.Dimensions[1];
        return t.ToArray();
    }

    /// <summary>
    /// mel → 拍・小節の頭のロジット（コマごと。0 より大きければ候補）。30 秒ずつ、前後に BorderFrames ずつ重ねて切り、
    /// 重なったところは先の窓の値を使う（元の Python の split_piece / aggregate_prediction の "keep_first" と同じ）
    /// </summary>
    private (float[] Beat, float[] Down) Logits(float[] mel, int frames)
    {
        const int bins = 128;
        var beat = new float[frames];
        var down = new float[frames];
        Array.Fill(beat, -1000f);
        Array.Fill(down, -1000f);
        var starts = new List<int>();
        for (int s = -BorderFrames; s < frames - BorderFrames; s += ChunkFrames - 2 * BorderFrames) starts.Add(s);
        if (frames > ChunkFrames - 2 * BorderFrames && starts.Count > 0) starts[^1] = frames - ChunkFrames + BorderFrames;

        // 後ろの窓から書いて、前の窓で上書きする（＝前の窓を優先）
        for (int ci = starts.Count - 1; ci >= 0; ci--)
        {
            int start = starts[ci];
            int from = Math.Max(start, 0), to = Math.Min(start + ChunkFrames, frames);
            int padLeft = Math.Max(0, -start);
            int padRight = Math.Max(0, Math.Min(BorderFrames, start + ChunkFrames - frames));
            int len = padLeft + (to - from) + padRight;
            var chunk = new float[len * bins];
            Array.Copy(mel, from * bins, chunk, padLeft * bins, (to - from) * bins);
            var input = new DenseTensor<float>(chunk, new[] { 1, len, bins });
            using var res = _beat.Run(new[] { NamedOnnxValue.CreateFromTensor("spectrogram", input) }, _beat.OutputMetadata.Keys.ToList(), _run);
            var b = res.First(r => r.Name == "beat").AsTensor<float>();
            var d = res.First(r => r.Name == "downbeat").AsTensor<float>();
            for (int i = BorderFrames; i < len - BorderFrames; i++)
            {
                int f = start + i;
                if (f < 0 || f >= frames) continue;
                beat[f] = b[0, i];
                down[f] = d[0, i];
            }
        }
        return (beat, down);
    }

    /// <summary>
    /// ロジットの山（0 より大きく、前後 ±PeakRadius コマでいちばん高い）を時刻にする。
    /// 隣り合ったコマの山は 1 つにまとめる（位置は平均）。元の Python の "minimal" の後処理と同じ
    /// </summary>
    public static List<double> Peaks(float[] logits)
    {
        var frames = new List<int>();
        for (int i = 0; i < logits.Length; i++)
        {
            float v = logits[i];
            if (v <= 0) continue;
            bool max = true;
            for (int j = Math.Max(0, i - PeakRadius); j <= Math.Min(logits.Length - 1, i + PeakRadius) && max; j++)
                if (logits[j] > v) max = false;
            if (max) frames.Add(i);
        }
        var times = new List<double>();
        for (int i = 0; i < frames.Count;)
        {
            int j = i;
            while (j + 1 < frames.Count && frames[j + 1] - frames[j] <= 1) j++;
            double mean = 0;
            for (int k = i; k <= j; k++) mean += frames[k];
            times.Add(mean / (j - i + 1) / Fps);
            i = j + 1;
        }
        return times;
    }

    public void Dispose()
    {
        _mel.Dispose();
        _beat.Dispose();
        _run.Dispose();
    }
}

/// <summary>音のかたまりを受け取る</summary>
public delegate void SamplesSink(ReadOnlySpan<float> samples);

/// <summary>鳴っている音（任意のサンプリング周波数・モノラル）を、拍の AI 用の 22,050 Hz にする（少しずつ流し込める）</summary>
public sealed class Resampler22k
{
    private readonly WdlResampler _r = new();
    private float[] _out = new float[4096];

    public Resampler22k(int inputRate)
    {
        _r.SetMode(true, 2, false);
        _r.SetFilterParms();
        _r.SetFeedMode(true);
        _r.SetRates(inputRate, BeatThis.SampleRate);
    }

    /// <summary>入力を流し込み、出てきた 22,050 Hz の音を sink に渡す</summary>
    public void Push(ReadOnlySpan<float> input, SamplesSink sink)
    {
        // 変換器が一度に受け取れる量には上限があるので、小分けにして全部渡す（渡しきれなかった音を捨てないように）
        while (input.Length > 0)
        {
            int n = Math.Min(input.Length, 4096);
            int need = _r.ResamplePrepare(n, 1, out float[] inBuf, out int inOff);
            n = Math.Min(n, need);
            if (n <= 0) break;
            input[..n].CopyTo(inBuf.AsSpan(inOff));
            int maxOut = n * BeatThis.SampleRate / 8000 + 64;
            if (_out.Length < maxOut) _out = new float[maxOut];
            int got = _r.ResampleOut(_out, 0, n, _out.Length, 1);
            if (got > 0) sink(_out.AsSpan(0, got));
            input = input[n..];
        }
    }
}
