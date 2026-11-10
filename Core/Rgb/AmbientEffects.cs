// ==================================================
// AmbientEffects.cs - 音楽連動ではない、動きのある光り方 (雷・波・さえずり・炎・オーロラ・流れ星・心拍)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 雷・波などの光り方の 1 コマ分の色。どれも「時刻と LED の位置だけ」で決まる（状態を持たない）ので、
/// 1 秒間のコマ数が違うデバイスでも同じ動きになり、雷・流れ星などの乱数も時刻から作るので、
/// 全部のデバイスで同じ瞬間に光る。速さ（cfg.Speed）で動きの速さが変わる。
/// 色は 0〜1 の sRGB で作り、明るさを掛けてから光の量（LinColor）にする（呼吸と同じ。暗いところで段々にならない）
/// </summary>
public static class AmbientEffects
{
    /// <summary>この光り方を描けるか</summary>
    public static bool Handles(string mode) => Array.IndexOf(RgbModes.Ambient, mode) >= 0;

    public static void Fill(string mode, RgbConfig cfg, Rgb baseColor, double t, ReadOnlySpan<double> pos, Span<LinColor> dst)
    {
        int n = Math.Min(pos.Length, dst.Length);
        if (n == 0) return;
        bool gamma = cfg.Gamma;
        double speed = Math.Clamp(cfg.Speed, 0.1, 10);
        double ts = t * speed;
        var c = (R: baseColor.R / 255.0, G: baseColor.G / 255.0, B: baseColor.B / 255.0);
        // 1 個の LED の幅（位置 0〜1 での）。小さな光が LED の間に落ちて見えなくならないように
        double led = 1.0 / Math.Max(1, n - 1);
        switch (mode)
        {
            case RgbModes.Lightning:
                for (int i = 0; i < n; i++) dst[i] = Lightning(ts, pos[i], gamma);
                break;
            case RgbModes.Wave:
                for (int i = 0; i < n; i++) dst[i] = Wave(ts, pos[i], c, gamma);
                break;
            case RgbModes.Birdsong:
                for (int i = 0; i < n; i++) dst[i] = Birdsong(ts, pos[i], led, gamma);
                break;
            case RgbModes.Fire:
                for (int i = 0; i < n; i++) dst[i] = Fire(ts, pos[i], i, gamma);
                break;
            case RgbModes.Aurora:
                for (int i = 0; i < n; i++) dst[i] = Aurora(ts, pos[i], gamma);
                break;
            case RgbModes.Meteor:
                for (int i = 0; i < n; i++) dst[i] = Meteor(ts, pos[i], led, gamma);
                break;
            case RgbModes.Heartbeat:
                dst[..n].Fill(Heartbeat(ts, c, gamma));
                break;
        }
    }

    // ================================================================
    // 雷: 暗い嵐の空（紫がかった紺）に、ときどき白い稲光。1 回の雷は 2〜3 回またたき、落ちた場所から広がって消える

    /// <summary>雷が鳴るかを決める区切り（秒。速さ 1 のとき）。区切りごとに 3 割くらいの確率で落ちる</summary>
    public const double LightningSlotSec = 1.6;
    public const double LightningChance = 0.3;

    private static LinColor Lightning(double ts, double p, bool gamma)
    {
        // 空: ゆっくりうねる雲の明るさ
        double cloud = 0.20 + 0.07 * Math.Sin(2 * Math.PI * (p * 1.3 + ts * 0.05)) + 0.03 * Math.Sin(2 * Math.PI * (p * 3.1 - ts * 0.08));
        double r = 0.35 * cloud, g = 0.30 * cloud, b = 1.0 * cloud;
        double flash = 0;
        long slot = (long)Math.Floor(ts / LightningSlotSec);
        for (long s = slot - 1; s <= slot; s++)   // 前の区切りの雷が、まだ光っていることがある
        {
            if (Hash(s, 1) >= LightningChance) continue;
            double t0 = (s + 0.6 * Hash(s, 2)) * LightningSlotSec;
            double age = ts - t0;
            if (age < 0 || age > 1.2) continue;
            // またたき: 2〜3 回、だんだん弱く
            int flickers = Hash(s, 3) < 0.5 ? 2 : 3;
            double env = 0;
            for (int k = 0; k < flickers; k++)
            {
                double a = age - k * (0.07 + 0.05 * Hash(s, 10 + k));
                if (a >= 0) env = Math.Max(env, Math.Pow(0.7, k) * Math.Exp(-a / 0.09));
            }
            // 落ちた場所の近くほど明るく、空全体もうっすら明るくなる
            double center = Hash(s, 4), width = 0.25 + 0.5 * Hash(s, 5);
            double near = Math.Clamp(1 - Math.Abs(p - center) / width, 0, 1);
            flash = Math.Max(flash, env * (0.35 + 0.65 * near));
        }
        // 稲光は少し青みのある白
        r += 0.85 * flash;
        g += 0.90 * flash;
        b += 1.00 * flash;
        return Lin(r, g, b, gamma);
    }

    // ================================================================
    // 波: 選んだ色の海に、明るさのうねりが寄せては返す。波の頭は白く泡立つ

    private static LinColor Wave(double ts, double p, (double R, double G, double B) c, bool gamma)
    {
        // 速さの違う 2 つの波を重ねる（ひとつだけだと、同じ形がずっと流れて単調に見える）
        double w1 = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (p * 1.5 - ts * 0.16));
        double w2 = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (p * 0.7 + ts * 0.07 + 0.3));
        double swell = w1 * (0.55 + 0.45 * w2);
        double k = 0.18 + 0.82 * swell;
        // 波の頭（いちばん高いところ）だけ白く泡立つ
        double foam = Math.Clamp((swell - 0.78) / 0.22, 0, 1);
        foam *= foam;
        return Lin(c.R * k + foam * (1 - c.R * k) * 0.8, c.G * k + foam * (1 - c.G * k) * 0.8, c.B * k + foam * (1 - c.B * k) * 0.8, gamma);
    }

    // ================================================================
    // さえずり: やわらかい若葉色の上に、小鳥の声のような小さな光が「ちちっ」と 2〜4 回跳ねる

    /// <summary>小鳥が鳴くかを決める区切り（秒）と確率</summary>
    public const double BirdSlotSec = 0.45;
    public const double BirdChance = 0.45;
    private static readonly (double R, double G, double B)[] BirdColors =
    {
        (1.0, 0.85, 0.30), (1.0, 0.55, 0.70), (0.45, 0.85, 1.0), (1.0, 1.0, 0.85), (0.70, 1.0, 0.45),
    };

    private static LinColor Birdsong(double ts, double p, double led, bool gamma)
    {
        // 木漏れ日のような、ゆっくり揺れる若葉色
        double leaf = 0.24 + 0.06 * Math.Sin(2 * Math.PI * (p * 2.0 + ts * 0.04));
        double r = 0.35 * leaf, g = 1.0 * leaf, b = 0.45 * leaf;
        long slot = (long)Math.Floor(ts / BirdSlotSec);
        double width = Math.Max(0.035, 1.2 * led);
        for (long s = slot - 2; s <= slot; s++)
        {
            if (Hash(s, 21) >= BirdChance) continue;
            double t0 = (s + 0.5 * Hash(s, 22)) * BirdSlotSec;
            double at = Hash(s, 23);
            double d = Math.Abs(p - at);
            if (d > width * 2.5) continue;
            int pips = 2 + (int)(Hash(s, 24) * 3);   // 2〜4 回
            double gap = 0.07 + 0.05 * Hash(s, 25);
            double env = 0;
            for (int k = 0; k < pips; k++)
            {
                double a = ts - (t0 + k * gap);
                if (a >= 0 && a < 0.4) env = Math.Max(env, Math.Exp(-a / 0.05));
            }
            if (env <= 0) continue;
            var col = BirdColors[(int)(Hash(s, 26) * BirdColors.Length) % BirdColors.Length];
            double shape = Math.Exp(-(d * d) / (2 * width * width)) * env;
            r += col.R * shape;
            g += col.G * shape;
            b += col.B * shape;
        }
        return Lin(r, g, b, gamma);
    }

    // ================================================================
    // 炎: 赤〜橙〜黄が、LED ごとにゆらゆらと揺らめく（下ほど熱い）

    private static LinColor Fire(double ts, double p, int i, bool gamma)
    {
        // 揺らめき: LED ごとの、なめらかにつないだ乱数（速い揺れ ＋ ゆっくりした揺れ）
        double fast = Smooth2(p * 9, ts * 4.5, 31), slow = Smooth2(p * 3, ts * 1.1, 32);
        double heat = Math.Clamp(0.25 + 0.55 * fast * (0.6 + 0.4 * slow) + 0.25 * (1 - p), 0, 1);
        // 熱さ → 色: 暗い赤 → 赤 → 橙 → 黄
        double r = Math.Clamp(heat * 1.6, 0, 1);
        double g = Math.Clamp((heat - 0.35) * 1.3, 0, 1) * 0.8;
        double b = Math.Clamp((heat - 0.85) * 2.0, 0, 1) * 0.4;
        double k = 0.35 + 0.65 * heat;
        return Lin(r * k, g * k, b * k, gamma);
    }

    // ================================================================
    // オーロラ: 緑・水色・紫の光の帯が、夜空をゆっくり流れてうねる

    private static LinColor Aurora(double ts, double p, bool gamma)
    {
        double hue = 145 + 45 * Math.Sin(2 * Math.PI * (p * 0.8 + ts * 0.07)) + 50 * Math.Sin(2 * Math.PI * (p * 0.3 - ts * 0.04) + 1.1);
        double band = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (p * 2.2 - ts * 0.12) + 1.2 * Math.Sin(2 * Math.PI * (ts * 0.05 + p * 0.5)));
        double k = 0.08 + 0.92 * band * band;
        var (r, g, b) = Hsv(hue, 0.85, k);
        return Lin(r, g, b, gamma);
    }

    // ================================================================
    // 流れ星: 暗い夜空を、ときどき光が尾を引いて流れる（向きはそのたびに変わる）

    public const double MeteorSlotSec = 2.4;
    public const double MeteorChance = 0.7;
    /// <summary>流れ星が端から端まで流れる秒数（速さ 1 のとき）</summary>
    public const double MeteorTravelSec = 0.9;

    private static LinColor Meteor(double ts, double p, double led, bool gamma)
    {
        // 夜空: ごく暗い紺に、星がうっすらまたたく
        double twinkle = Hash((long)Math.Floor(ts * 2), (int)(p * 997) + 41) > 0.97 ? 0.08 : 0;
        double r = 0.01 + twinkle, g = 0.012 + twinkle, b = 0.04 + twinkle;
        long slot = (long)Math.Floor(ts / MeteorSlotSec);
        for (long s = slot - 1; s <= slot; s++)
        {
            if (Hash(s, 51) >= MeteorChance) continue;
            double t0 = (s + 0.5 * Hash(s, 52)) * MeteorSlotSec;
            double u = (ts - t0) / MeteorTravelSec;   // 0 → 1 で端から端へ
            if (u < 0 || u > 1.6) continue;
            bool forward = Hash(s, 53) < 0.5;
            double head = -0.1 + 1.2 * u;
            double x = forward ? p : 1 - p;
            double behind = head - x;   // 頭より後ろ（尾）なら正
            double tail = 0.35;
            double k = 0;
            if (behind >= -Math.Max(0.02, led) && behind < 0) k = 1;                       // 頭
            else if (behind >= 0 && behind < tail) k = Math.Pow(1 - behind / tail, 1.3);   // 尾
            k *= Math.Clamp(1.6 - u, 0, 1);   // 最後はふっと消える
            r += 0.85 * k;
            g += 0.90 * k;
            b += 1.00 * k;
        }
        return Lin(r, g, b, gamma);
    }

    // ================================================================
    // 心拍: 選んだ色が「ドクン、ドクン」と 2 回ずつ脈打つ（速さ 1 で 1 分に 60 回）

    private static LinColor Heartbeat(double ts, (double R, double G, double B) c, bool gamma)
    {
        double ph = ts - Math.Floor(ts);
        double lub = Math.Exp(-Math.Pow(ph / 0.07, 2));
        double dub = 0.65 * Math.Exp(-Math.Pow((ph - 0.26) / 0.08, 2));
        double lub2 = Math.Exp(-Math.Pow((ph - 1) / 0.07, 2));   // 次の拍の頭（境目でつながるように）
        double k = 0.10 + 0.90 * Math.Max(Math.Max(lub, dub), lub2);
        return Lin(c.R * k, c.G * k, c.B * k, gamma);
    }

    // ================================================================

    private static LinColor Lin(double r, double g, double b, bool gamma)
        => new(OutputStage.Lin((float)Math.Clamp(r, 0, 1), gamma), OutputStage.Lin((float)Math.Clamp(g, 0, 1), gamma), OutputStage.Lin((float)Math.Clamp(b, 0, 1), gamma));

    /// <summary>整数 2 つから 0〜1 の乱数（同じ値なら毎回同じ。全デバイスで同じ雷・流れ星になる）</summary>
    public static double Hash(long a, int b)
    {
        ulong x = unchecked((ulong)a * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL);
        x ^= x >> 31;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (x >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>時間でなめらかにつないだ乱数（0〜1）。x の整数ごとに乱数を決め、間をなめらかにつなぐ</summary>
    private static double Smooth(double x, int lane, int salt)
    {
        long i = (long)Math.Floor(x);
        double f = x - i;
        f = f * f * (3 - 2 * f);
        double a = Hash(i, lane * 131 + salt), b = Hash(i + 1, lane * 131 + salt);
        return a + (b - a) * f;
    }

    /// <summary>位置と時間の両方でなめらかにつないだ乱数（0〜1）。炎の揺らめきが LED ごとにばらばらの砂嵐にならないように</summary>
    private static double Smooth2(double x, double y, int salt)
    {
        long xi = (long)Math.Floor(x), yi = (long)Math.Floor(y);
        double fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        double V(long a, long b) => Hash(a * 73856093 ^ b * 19349663, salt);
        double top = V(xi, yi) + (V(xi + 1, yi) - V(xi, yi)) * fx;
        double bottom = V(xi, yi + 1) + (V(xi + 1, yi + 1) - V(xi, yi + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    private static (double R, double G, double B) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
