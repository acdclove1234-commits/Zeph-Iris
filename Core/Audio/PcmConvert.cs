// ==================================================
// PcmConvert.cs - 取り込んだ音のデータ (32bit 浮動小数・16/24/32bit 整数、2ch 以上) をモノラルの数字に直す
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ProFanController;

/// <summary>音のデータの形式</summary>
public enum PcmKind
{
    Unsupported,
    Float32,
    Int16,
    Int24,
    Int32,
}

/// <summary>
/// Windows から届く音のデータを、-1〜1 のモノラル（全チャンネルの平均）に直す。
/// 毎回配列を作らないよう、書き込み先は呼ぶ側が用意する
/// </summary>
public static class PcmConvert
{
    // WaveFormatEncoding の値（NAudio と同じ）
    public const int EncodingPcm = 1;
    public const int EncodingIeeeFloat = 3;
    public const int EncodingExtensible = 0xFFFE;

    /// <summary>WAVE_FORMAT_EXTENSIBLE の中身の種類（KSDATAFORMAT_SUBTYPE_PCM / _IEEE_FLOAT）</summary>
    public static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>形式を判定する。subFormat は WAVE_FORMAT_EXTENSIBLE のときだけ渡す</summary>
    public static PcmKind Detect(int encoding, int bitsPerSample, Guid? subFormat)
    {
        bool isFloat = encoding == EncodingIeeeFloat || (encoding == EncodingExtensible && subFormat == SubTypeIeeeFloat);
        bool isPcm = encoding == EncodingPcm || (encoding == EncodingExtensible && subFormat == SubTypePcm);
        // 古いドライバーは SubFormat を正しく入れないことがあるので、ビット数で推定する
        if (encoding == EncodingExtensible && !isFloat && !isPcm)
        {
            isFloat = bitsPerSample == 32;
            isPcm = bitsPerSample is 16 or 24;
        }
        if (isFloat) return bitsPerSample == 32 ? PcmKind.Float32 : PcmKind.Unsupported;
        if (isPcm)
        {
            return bitsPerSample switch
            {
                16 => PcmKind.Int16,
                24 => PcmKind.Int24,
                32 => PcmKind.Int32,
                _ => PcmKind.Unsupported,
            };
        }
        return PcmKind.Unsupported;
    }

    public static int BytesPerSample(PcmKind kind) => kind switch
    {
        PcmKind.Float32 => 4,
        PcmKind.Int16 => 2,
        PcmKind.Int24 => 3,
        PcmKind.Int32 => 4,
        _ => 0,
    };

    /// <summary>
    /// data（チャンネルが交互に並んだ音）をモノラルにして dst に書く。戻り値は書いた個数（フレーム数）。
    /// dst が足りなければ入る分だけ書く
    /// </summary>
    public static int ToMono(ReadOnlySpan<byte> data, PcmKind kind, int channels, Span<float> dst)
    {
        int bps = BytesPerSample(kind);
        channels = Math.Max(1, channels);
        if (bps == 0) return 0;
        int frameBytes = bps * channels;
        int frames = Math.Min(data.Length / frameBytes, dst.Length);
        float inv = 1f / channels;
        switch (kind)
        {
            case PcmKind.Float32:
            {
                var s = MemoryMarshal.Cast<byte, float>(data[..(frames * frameBytes)]);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    int b = i * channels;
                    for (int c = 0; c < channels; c++) sum += s[b + c];
                    dst[i] = sum * inv;
                }
                break;
            }
            case PcmKind.Int16:
            {
                var s = MemoryMarshal.Cast<byte, short>(data[..(frames * frameBytes)]);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    int b = i * channels;
                    for (int c = 0; c < channels; c++) sum += s[b + c] / 32768f;
                    dst[i] = sum * inv;
                }
                break;
            }
            case PcmKind.Int24:
            {
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    int b = i * frameBytes;
                    for (int c = 0; c < channels; c++)
                    {
                        int p = b + c * 3;
                        // リトルエンディアンの 24bit を、符号付きの 32bit の上位 24bit に入れてから割る
                        int v = (data[p] << 8) | (data[p + 1] << 16) | (data[p + 2] << 24);
                        sum += v / 2147483648f;
                    }
                    dst[i] = sum * inv;
                }
                break;
            }
            case PcmKind.Int32:
            {
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    int b = i * frameBytes;
                    for (int c = 0; c < channels; c++) sum += BinaryPrimitives.ReadInt32LittleEndian(data[(b + c * 4)..]) / 2147483648f;
                    dst[i] = sum * inv;
                }
                break;
            }
        }
        return frames;
    }

    /// <summary>
    /// data の左右（1 番目と 2 番目のチャンネル）の差の半分 (L − R) / 2（サイド）を dst に書く。戻り値は書いた個数（フレーム数）。
    /// 1 チャンネルの音や対応していない形式では 0（サイドなし）。左右の広がりを見るのに使う（ToMono と同じ目盛り。2 チャンネルなら
    /// ToMono の値がミッド (L + R) / 2）
    /// </summary>
    public static int ToSide(ReadOnlySpan<byte> data, PcmKind kind, int channels, Span<float> dst)
    {
        int bps = BytesPerSample(kind);
        if (bps == 0 || channels < 2) return 0;
        int frameBytes = bps * channels;
        int frames = Math.Min(data.Length / frameBytes, dst.Length);
        for (int i = 0; i < frames; i++)
        {
            int b = i * frameBytes;
            dst[i] = 0.5f * (Sample(data, b, kind) - Sample(data, b + bps, kind));
        }
        return frames;
    }

    /// <summary>offset バイト目からの音 1 個（−1〜1）</summary>
    private static float Sample(ReadOnlySpan<byte> data, int offset, PcmKind kind) => kind switch
    {
        PcmKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(data[offset..]),
        PcmKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32768f,
        PcmKind.Int24 => ((data[offset] << 8) | (data[offset + 1] << 16) | (data[offset + 2] << 24)) / 2147483648f,
        PcmKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) / 2147483648f,
        _ => 0,
    };
}
