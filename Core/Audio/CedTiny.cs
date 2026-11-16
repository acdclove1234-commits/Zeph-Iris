// ==================================================
// CedTiny.cs - 楽器の AI（CED-Tiny: Xiaomi・mispeech/ced-tiny・Apache License 2.0）
//   音（16,000 Hz・モノラル・2 秒）→ AudioSet の 527 種類の音それぞれの「らしさ」（0〜1）
// Dependencies: ONNX Runtime (MIT License), CED-Tiny のモデル (Apache License 2.0)
// See CREDITS.txt for full license details.
// ==================================================

using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace ProFanController;

/// <summary>
/// 楽器の AI。直近の 2 秒（32,000 個）の音を見て、AudioSet の 527 種類の音（歌・ギター・ピアノ・ドラムなど）の
/// らしさを出す（モデルの中で mel スペクトログラムにし、最後に 0〜1 にしてある）。モデルは exe に埋め込んである（約 6.7 MB）。
/// 読み込みに失敗したら null（AI なしで動く）。1 回の計算は 1 スレッドで 10 ミリ秒ほど。計算は呼んだスレッドで行う
/// （中でスレッドを作らない）ので、呼ぶ側のスレッドの優先度がそのまま効く。作業用の入れ物を使い回すので、同時に呼ばれたら 1 つずつ計算する。
/// 楽器の AI（SceneTagger）は作るときに TryAcquire し、スレッドが終わるときに Release する（計算中に片付けないため）
/// </summary>
public sealed partial class CedTiny : IDisposable
{
    public const int SampleRate = 16000;
    /// <summary>1 回に見る音の長さ（個。2 秒）。1 秒の窓より、ギター・鍵盤のらしさが安定する</summary>
    public const int WindowSamples = 32000;
    /// <summary>調べる間隔（秒。窓の半分ずつ重ねる）</summary>
    public const double HopSec = 1.0;
    /// <summary>AudioSet の音の種類の数</summary>
    public const int Classes = 527;
    /// <summary>埋め込むモデル（ced-tiny.onnx）の SHA-256 と大きさ（ライセンスの説明・テストで確かめる）</summary>
    public const string Sha256 = "3890f227660be71886ff48633d11a731993e0206db8a486781412c28a0873435";
    public const int ModelBytesLength = 6_681_767;
    private const string ResourceName = "ProFanController.Models.ced-tiny.onnx";
    private const string InputName = "waveform", OutputName = "logits";

    private readonly InferenceSession _session;
    private readonly RunOptions _run;
    private readonly OrtIoBinding _binding;
    private readonly float[] _in = new float[WindowSamples];
    private readonly float[] _out = new float[Classes];
    private readonly OrtValue _inValue, _outValue;

    /// <summary>失敗したら、ここで作ったものを片付けてから例外（session は呼んだ側が片付ける）</summary>
    private CedTiny(InferenceSession session)
    {
        _session = session;
        RunOptions? run = null;
        OrtValue? inValue = null, outValue = null;
        OrtIoBinding? binding = null;
        try
        {
            run = new RunOptions();
            // 入れ物を最初に 1 回だけ用意し、毎回それを使う（計算のたびにメモリを確保しない）
            inValue = OrtValue.CreateTensorValueFromMemory(_in, new long[] { 1, WindowSamples });
            outValue = OrtValue.CreateTensorValueFromMemory(_out, new long[] { 1, Classes });
            binding = _session.CreateIoBinding();
            binding.BindInput(InputName, inValue);
            binding.BindOutput(OutputName, outValue);
        }
        catch
        {
            binding?.Dispose();
            outValue?.Dispose();
            inValue?.Dispose();
            run?.Dispose();
            throw;
        }
        _run = run;
        _inValue = inValue;
        _outValue = outValue;
        _binding = binding;
    }

    /// <summary>モデルを読み込む（テストでは 0.1 秒ほど・メモリ約 35 MB。楽器の AI を使うときだけ）。失敗したら null と理由</summary>
    public static CedTiny? TryCreate(out string? error)
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
    internal static CedTiny? TryCreate(byte[] model, out string? error)
    {
        InferenceSession? session = null;
        try
        {
            // 設定の途中で失敗しても片付くように、作ってから設定する（オブジェクト初期化子だと using に入る前に失敗しうる）
            using var opt = new SessionOptions();
            opt.IntraOpNumThreads = 1;
            opt.InterOpNumThreads = 1;
            opt.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            opt.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            opt.EnableCpuMemArena = false;
            // 待つ間に CPU を回し続けない（スレッドは 1 つなので、もともとほとんど待たない）
            opt.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            session = new InferenceSession(model, opt);
            CheckShape(session);
            var c = new CedTiny(session);
            error = null;
            return c;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            error = ex.Message;
            return null;
        }
    }

    /// <summary>入力が waveform（[まとめて, 個数]）、出力 logits の最後の次元が 527 か（違うモデルなら使わない）</summary>
    private static void CheckShape(InferenceSession s)
    {
        if (!s.InputMetadata.TryGetValue(InputName, out var inMeta) || inMeta.Dimensions.Length != 2 || inMeta.ElementType != typeof(float))
            throw new InvalidDataException("CED: unexpected input");   // no-loc（ログだけ）
        if (!s.OutputMetadata.TryGetValue(OutputName, out var outMeta) || outMeta.Dimensions.Length != 2 || outMeta.Dimensions[^1] != Classes)
            throw new InvalidDataException("CED: unexpected output");   // no-loc（ログだけ）
    }

    /// <summary>入力・出力の形（テスト用。例 "waveform[-1,-1] -> logits[-1,527]"）</summary>
    internal string Describe()
    {
        var i = _session.InputMetadata[InputName];
        var o = _session.OutputMetadata[OutputName];
        return $"{InputName}[{string.Join(",", i.Dimensions)}] -> {OutputName}[{string.Join(",", o.Dimensions)}]";
    }

    /// <summary>exe に埋め込んだモデル</summary>
    internal static byte[] ModelBytes()
    {
        using var s = typeof(CedTiny).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new FileNotFoundException("ced-tiny");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>埋め込んだモデルの SHA-256（16 進の小文字。テスト用）</summary>
    internal static string ModelSha256() => Convert.ToHexString(SHA256.HashData(ModelBytes())).ToLowerInvariant();

    private static Dictionary<string, int>? _index;

    /// <summary>音の名前（Labels と同じ書き方）の出力の位置。ない名前は −1（同じ名前が 2 つあれば前のほう）</summary>
    public static int IndexOf(string label)
    {
        var map = _index;
        if (map is null)
        {
            map = new Dictionary<string, int>(Labels.Length, StringComparer.Ordinal);
            for (int i = 0; i < Labels.Length; i++) map.TryAdd(Labels[i], i);
            _index = map;
        }
        return map.TryGetValue(label, out int k) ? k : -1;
    }

    /// <summary>
    /// 32,000 個（16,000 Hz・−1〜1）の音の、527 種類の音のらしさ（0〜1）を scores に書く。
    /// 失敗したら例外（呼ぶ側で AI を止める）
    /// </summary>
    public void Classify(float[] window, float[] scores)
    {
        if (window.Length < WindowSamples || scores.Length < Classes) throw new ArgumentException("CED: buffer size");   // no-loc（ログだけ）
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
