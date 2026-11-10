// ==================================================
// SyncTestPlayer.cs - 音と光のずれを合わせるための「同期テスト」 (0.6 秒ごとにクリック音を鳴らす)
// Dependencies: NAudio.Wasapi (MIT License), NAudio.Core (MIT License), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ProFanController;

/// <summary>
/// 同期テスト: 0.6 秒ごとに短いクリック音（1 kHz・5 ミリ秒）を、いつもの出力先から鳴らす。
/// 鳴らした音はループバックで取り込まれて Click として記録され、LED はそれに合わせて白く光る。
/// 「音の遅れの補正」を動かして、耳に聞こえるクリックと光がそろうところを探す
/// </summary>
public sealed class SyncTestPlayer : IDisposable
{
    private WasapiOut? _out;
    private MMDevice? _device;
    private AudioEngine? _engine;

    public bool Running { get; private set; }
    /// <summary>クリック音を鳴らしている出力先の ID（取り込み側の出力先と違えば、テストをやり直す）</summary>
    public string DeviceId { get; private set; } = "";
    /// <summary>失敗したときの理由（画面に出す）</summary>
    public string Error { get; private set; } = "";

    /// <summary>クリック音を鳴らし始める。engine にはクリック音を記録させる</summary>
    public void Start(AudioEngine? engine)
    {
        Stop(_engine);
        Error = "";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = _device.AudioClient.MixFormat;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, Math.Max(1, mix.Channels));
            _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, 50);
            _out.Init(new ClickProvider(format));
            // 出力先が抜かれた・使えなくなったときは止まる（画面の「同期テスト中」も自動で消える）
            _out.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) Error = L($"クリック音が止まりました: {e.Exception.Message}");
                Running = false;
            };
            DeviceId = _device.ID;
            _out.Play();
            Running = true;
            _engine = engine;
            if (engine is not null) engine.ClickDetect = true;
        }
        catch (Exception ex)
        {
            Error = L($"クリック音を鳴らせません: {ex.Message}");
            Logger.Warn($"同期テスト: {Error}");
            Stop(engine);
        }
    }

    public void Stop(AudioEngine? engine)
    {
        try { _out?.Stop(); } catch { /* 止まっている */ }
        _out?.Dispose();
        _out = null;
        _device?.Dispose();
        _device = null;
        if (engine is not null) engine.ClickDetect = false;
        if (_engine is not null) _engine.ClickDetect = false;
        _engine = null;
        Running = false;
    }

    public void Dispose() => Stop(null);

    /// <summary>0.6 秒ごとに 5 ミリ秒の 1 kHz の音（なめらかに立ち上がって消える。形は SyncClick）を作る</summary>
    private sealed class ClickProvider : IWaveProvider
    {
        private readonly int _channels;
        private readonly int _rate;
        private long _frame;

        public ClickProvider(WaveFormat format)
        {
            WaveFormat = format;
            _channels = format.Channels;
            _rate = format.SampleRate;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - count % (4 * _channels)));
            int frames = samples.Length / _channels;
            for (int i = 0; i < frames; i++)
            {
                float v = SyncClick.Sample(_frame, _rate);
                for (int c = 0; c < _channels; c++) samples[i * _channels + c] = v;
                _frame++;
            }
            return frames * _channels * 4;
        }
    }
}
