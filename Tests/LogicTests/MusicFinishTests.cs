// 音楽連動の仕上げ: 立ち上がりの段つき止め（AttackHold）・曲の盛り上がり（SectionMeter）・拍の間を控えめに（OffBeatDuck）
using ProFanController;

namespace LogicTests;

public static class MusicFinishTests
{
    private const float Dt = 1 / 60f;

    private static float Level(LinColor c) => c.MaxChannel;

    public static void TestAttackHoldRemovesTheStepInARise()
    {
        var h = new AttackHold();
        var px = new LinColor[1];
        float Step(float v, float dt = Dt)
        {
            px[0] = new LinColor(v, v * 0.5f, 0);
            h.Apply(px, 1, dt);
            return Level(px[0]);
        }
        // 実際の曲で見えた段つき: 0.57 → 0.59 → 0.77 → 0.75 → 0.87 → 0.81
        Step(0.30f);
        Step(0.57f);
        Step(0.59f);
        float a = Step(0.77f);
        float dip = Step(0.745f);
        float b = Step(0.867f);
        T.Check(Math.Abs(dip - a) < 1e-4, $"a small dip right after a rise is held at the peak ({a:F3} → {dip:F3})");
        T.Check(Math.Abs(b - 0.867f) < 1e-4, "the next rise goes through at once (the flash still starts on time)");
        // 保つのは 35 ms まで: その後は今の値（ゆっくり暗くなる光はそのまま）
        float h1 = Step(0.81f), h2 = Step(0.75f), after = Step(0.70f);
        T.Check(h1 > 0.866f && h2 > 0.866f && Math.Abs(after - 0.70f) < 1e-4, $"held for 2 frames at 60 fps, then the decay goes through ({h1:F3}, {h2:F3}, {after:F3})");
        // 色は今の色のまま、明るさだけを保つ
        Step(0.2f);
        Step(0.9f);
        px[0] = new LinColor(0, 0, 0.8f);
        h.Apply(px, 1, Dt);
        T.Check(px[0].R == 0 && Math.Abs(px[0].B - 0.9f) < 1e-4, "the held frame keeps the new colour, only its brightness is held");
        // 半分より下へ一気に暗くなるとき（ストロボ・切り替え）は待たない
        Step(0.9f);
        float cut = Step(0.3f);
        T.Check(Math.Abs(cut - 0.3f) < 1e-4, "a cut below half goes through at once");
        // 8 fps（1 コマが保つ時間より長い）では何もしない
        var slow = new AttackHold();
        var q = new[] { new LinColor(0.8f, 0, 0) };
        slow.Apply(q, 1, 0.125f);
        q[0] = new LinColor(0.6f, 0, 0);
        slow.Apply(q, 1, 0.125f);
        T.Check(Math.Abs(q[0].R - 0.6f) < 1e-4, "at 8 fps nothing is held");
        T.Check(AttackHold.Applies(EffectVariant.Pulse) && AttackHold.Applies(EffectVariant.BeatHue) && !AttackHold.Applies(EffectVariant.Ripple) && !AttackHold.Applies(EffectVariant.Chase),
            "used for whole-zone flashes only (moving light would leave a trail)");
    }

    private static AudioFrame Frame(float loudDb, bool open = true) => new() { LoudDb = loudDb, GateOpen = open };

    public static void TestSectionMeterFollowsTheSongDynamics()
    {
        var s = new SectionMeter();
        float Run(float db, double sec, bool open = true)
        {
            float v = s.Value;
            for (; sec > 0; sec -= Dt) v = s.Update(Frame(db, open), Dt);
            return v;
        }
        float chorus = Run(-12, 10);
        T.Check(chorus > 0.99f, $"the loudest part of the song is 1 ({chorus:F2})");
        float verse = Run(-18, 6);
        // 6 dB 下でも、6 秒の間に大きいところの記憶が 1.5 dB 下がるので、差は約 4.5 dB → 1 − 4.5/12 ≈ 0.63
        T.Check(verse is > 0.5f and < 0.75f, $"a verse 6 dB quieter is about 0.6 ({verse:F2})");
        float quiet = Run(-26, 6);
        T.Check(quiet < 0.1f, $"a break 14 dB quieter is near 0 ({quiet:F2})");
        float back = Run(-12, 2);
        T.Check(back > 0.9f, $"back to full within about 2 s when the chorus returns ({back:F2})");
        float silent = Run(-120, 5, open: false);
        T.Check(Math.Abs(silent - back) < 1e-6, "silence between songs keeps the last value");
        // ずっと静かな曲でも、1 分ほどで「その曲の大きいところ」に合わせ直す
        float settle = Run(-24, 60);
        T.Check(settle > 0.95f, $"a song that stays quiet is back to full within a minute ({settle:F2})");
        SectionMeter.Enabled = false;
        try { T.Check(s.Update(Frame(-40), Dt) == 1, "can be switched off for measuring"); }
        finally { SectionMeter.Enabled = true; }
    }

    public static void TestOffBeatDuck()
    {
        var c = new EffectContext { Locked = true, Conf = 1, Phase = 0 };
        T.Check(c.OffBeatDuck(0.25f) == 1, "on the beat: full");
        c.Phase = 0.5;
        T.Check(Math.Abs(c.OffBeatDuck(0.25f) - 0.75f) < 1e-4, "half way between beats: 1 − duck");
        c.Phase = 0.94;
        T.Check(c.OffBeatDuck(0.25f) == 1, "just before the next beat: full");
        c.Phase = 3.5;
        T.Check(Math.Abs(c.OffBeatDuck(0.25f) - 0.75f) < 1e-4, "phase counts within the beat");
        c.Conf = 0.4f;
        T.Check(Math.Abs(c.OffBeatDuck(0.25f) - 0.9f) < 1e-4, "scaled by the tempo confidence");
        c.Locked = false;
        T.Check(c.OffBeatDuck(0.25f) == 1, "without a locked tempo nothing is ducked");
        c.Section = 0.5f;
        T.Check(Math.Abs(c.SectionGain(0.6f) - 0.8f) < 1e-4, "section gain = min + (1 − min) × section");
    }
}
