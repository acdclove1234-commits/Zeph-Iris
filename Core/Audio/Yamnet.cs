// ==================================================
// Yamnet.cs - 楽器の AI（YAMNet: Google / The TensorFlow Authors・Apache License 2.0。ONNX への変換は zeropointnine/yamnet-onnx）
//   音（16,000 Hz・モノラル・約 1 秒）→ AudioSet の 521 種類の音それぞれの「らしさ」（0〜1）
// Dependencies: ONNX Runtime (MIT License), YAMNet のモデル (Apache License 2.0)
// See CREDITS.txt for full license details.
// ==================================================

using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace ProFanController;

/// <summary>
/// 楽器の AI。直近の約 1 秒（15,600 個 = 0.975 秒）の音を見て、AudioSet の 521 種類の音（歌・ギター・ピアノ・ドラムなど）の
/// らしさを出す。モデルは exe に埋め込んである（約 16 MB）。読み込みに失敗したら null（AI なしで動く）。
/// 1 回の計算は 2 ミリ秒ほど。計算は呼んだスレッドで行う（中でスレッドを作らない）ので、呼ぶ側のスレッドの優先度がそのまま効く。
/// 作業用の配列を使い回すので、同時に呼ばれたら 1 つずつ計算する。
/// 楽器の AI（SceneTagger）は作るときに TryAcquire し、スレッドが終わるときに Release する（計算中に片付けないため）
/// </summary>
public sealed class Yamnet : IDisposable
{
    public const int SampleRate = 16000;
    /// <summary>1 回に見る音の長さ（個）。ちょうど 1 つ分の窓（0.975 秒）。これより長いと、ほとんど 0 で埋めた 2 つ目の窓ができる</summary>
    public const int WindowSamples = 15600;
    /// <summary>調べる間隔（秒。YAMNet の窓のずらし幅と同じ）</summary>
    public const double HopSec = 0.48;
    /// <summary>AudioSet の音の種類の数</summary>
    public const int Classes = 521;
    /// <summary>埋め込むモデル（yamnet.onnx）の SHA-256（ライセンスの説明・テストで確かめる）</summary>
    public const string Sha256 = "1510041dce24a2e9e84ec546807ac408ae496da6d1ed41bc3ccba649623f8e19";
    private const string InputName = "waveform", OutputName = "output_0";

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly OrtIoBinding _binding;
    private readonly float[] _in = new float[WindowSamples];
    private readonly float[] _out = new float[Classes];
    private readonly OrtValue _inValue, _outValue;

    private Yamnet(InferenceSession session)
    {
        _session = session;
        // 入れ物を最初に 1 回だけ用意し、毎回それを使う（計算のたびにメモリを確保しない）
        _inValue = OrtValue.CreateTensorValueFromMemory(_in, new long[] { WindowSamples });
        _outValue = OrtValue.CreateTensorValueFromMemory(_out, new long[] { 1, Classes });
        _binding = _session.CreateIoBinding();
        _binding.BindInput(InputName, _inValue);
        _binding.BindOutput(OutputName, _outValue);
    }

    /// <summary>モデルを読み込む（約 60 ミリ秒・メモリ約 55 MB。楽器の AI を使うときだけ）。失敗したら null と理由</summary>
    public static Yamnet? TryCreate(out string? error)
    {
        byte[] model;
        try { model = ModelBytes(); }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        return TryCreate(model, out error);
    }

    /// <summary>渡したモデルを読み込む（テストで、壊れたモデルを渡すため）</summary>
    internal static Yamnet? TryCreate(byte[] model, out string? error)
    {
        InferenceSession? session = null;
        try
        {
            using var opt = new SessionOptions
            {
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                EnableCpuMemArena = false,
            };
            // 待つ間に CPU を回し続けない（スレッドは 1 つなので、もともとほとんど待たない）
            opt.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            session = new InferenceSession(model, opt);
            CheckShape(session);
            var y = new Yamnet(session);
            error = null;
            return y;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            error = ex.Message;
            return null;
        }
    }

    /// <summary>入力が waveform（1 次元）、出力 output_0 の最後の次元が 521 か（違うモデルなら使わない）</summary>
    private static void CheckShape(InferenceSession s)
    {
        if (!s.InputMetadata.TryGetValue(InputName, out var inMeta) || inMeta.Dimensions.Length != 1 || inMeta.ElementType != typeof(float))
            throw new InvalidDataException("YAMNet: unexpected input");   // no-loc（ログだけ）
        if (!s.OutputMetadata.TryGetValue(OutputName, out var outMeta) || outMeta.Dimensions.Length != 2 || outMeta.Dimensions[^1] != Classes)
            throw new InvalidDataException("YAMNet: unexpected output");   // no-loc（ログだけ）
    }

    /// <summary>入力・出力の形（テスト用。例 "waveform[-1] -> output_0[-1,521]"）</summary>
    internal string Describe()
    {
        var i = _session.InputMetadata[InputName];
        var o = _session.OutputMetadata[OutputName];
        return $"{InputName}[{string.Join(",", i.Dimensions)}] -> {OutputName}[{string.Join(",", o.Dimensions)}]";
    }

    /// <summary>exe に埋め込んだモデル</summary>
    internal static byte[] ModelBytes()
    {
        using var s = typeof(Yamnet).Assembly.GetManifestResourceStream("ProFanController.Models.yamnet.onnx")
                      ?? throw new FileNotFoundException("yamnet");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>埋め込んだモデルの SHA-256（16 進の小文字。テスト用）</summary>
    internal static string ModelSha256() => Convert.ToHexString(SHA256.HashData(ModelBytes())).ToLowerInvariant();

    /// <summary>
    /// 15,600 個（16,000 Hz・−1〜1）の音の、521 種類の音のらしさ（0〜1）を scores に書く。
    /// 失敗したら例外（呼ぶ側で AI を止める）
    /// </summary>
    public void Classify(float[] window, float[] scores)
    {
        if (window.Length < WindowSamples || scores.Length < Classes) throw new ArgumentException("YAMNet: buffer size");   // no-loc（ログだけ）
        // 作り直しの直後は、前の楽器の AI のスレッドと新しいスレッドが同時に呼ぶことがあるので、1 つずつ
        lock (_runLock)
        {
            ObjectDisposedException.ThrowIf(_freed, this);
            Array.Copy(window, _in, WindowSamples);
            _session.RunWithBinding(_run, _binding);
            Array.Copy(_out, scores, Classes);
        }
    }

    // 片付け: 持ち主（SceneAi）と、使っている楽器の AI のスレッドの数を数え、最後の 1 つが離れたときにだけ
    // ONNX Runtime の中身を片付ける（計算中に片付けると、アプリごと落ちる）
    private readonly object _runLock = new(), _refLock = new();
    private int _users = 1;   // 持ち主の分
    private bool _ownerDone;
    private volatile bool _freed;

    /// <summary>中身を片付け終わった（テスト用）</summary>
    internal bool Freed => _freed;

    /// <summary>使う側（楽器の AI のスレッド）を 1 つ増やす。持ち主がもう片付けていれば false</summary>
    internal bool TryAcquire()
    {
        lock (_refLock)
        {
            if (_ownerDone || _freed) return false;
            _users++;
            return true;
        }
    }

    /// <summary>使う側を 1 つ減らす。誰も使わなくなったら中身を片付ける（呼んだスレッドで）</summary>
    internal void Release()
    {
        lock (_refLock)
        {
            if (_freed || --_users > 0) return;
            _freed = true;
        }
        lock (_runLock)
        {
            _binding.Dispose();
            _inValue.Dispose();
            _outValue.Dispose();
            _session.Dispose();
            _run.Dispose();
        }
    }

    /// <summary>持ち主が手放す。計算中の楽器の AI のスレッドがあれば、それが終わってから片付く（待たない）</summary>
    public void Dispose()
    {
        lock (_refLock)
        {
            if (_ownerDone) return;
            _ownerDone = true;
        }
        Release();
    }
}
