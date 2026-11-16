// ==================================================
// SceneLifetimeTests.cs - 楽器の AI のモデルの片付け（計算中に片付けない・同時に呼んでも 1 つずつ）・
//                         作り直した AI が前の場面から続く・取り直しは見えている場面から・描画のクロスフェードの合図
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using ProFanController;

namespace LogicTests;

public static class SceneLifetimeTests
{
    private const double T0 = 1000;

    private static bool VocalScores(float[] w, float[] s)
    {
        Array.Clear(s);
        s[CedTiny.IndexOf("Singing")] = 0.8f;
        s[SceneClasses.MusicClass] = 0.8f;
        return true;
    }

    private static bool DrumScores(float[] w, float[] s)
    {
        Array.Clear(s);
        s[CedTiny.IndexOf("Drum kit")] = 0.8f;
        s[SceneClasses.MusicClass] = 0.8f;
        return true;
    }

    /// <summary>
    /// 計算中に止めても、AI のスレッドは計算を終えてからモデルを手放す。持ち主（SceneAi）が先にモデルを片付けても、
    /// 使っている AI がある間は中身を片付けない（前は計算中に片付けて、AccessViolation でアプリごと落ちることがあった）
    /// </summary>
    public static void TestModelOutlivesTheLastInference()
    {
        // 1. AI の代わりの関数: 計算中に Dispose → すぐ戻る。モデルを手放すのは計算が終わってから
        using var gate = new ManualResetEventSlim(false);
        int inside = 0, finished = 0, exitOrder = 0, seq = 0;
        var tg = new SceneTagger((w, s) =>
        {
            Volatile.Write(ref inside, 1);
            gate.Wait();
            Volatile.Write(ref finished, Interlocked.Increment(ref seq));
            return VocalScores(w, s);
        }, 48000, () => Volatile.Write(ref exitOrder, Interlocked.Increment(ref seq)));
        var f = new SceneJumpTests.Feeder();
        var guard = Stopwatch.StartNew();
        while (Volatile.Read(ref inside) == 0 && guard.ElapsedMilliseconds < 5000) f.Hop(tg, wait: false);
        var sw = Stopwatch.StartNew();
        tg.Dispose();
        long ms = sw.ElapsedMilliseconds;
        bool exitedEarly = tg.WaitExited(100);
        gate.Set();
        bool exited = tg.WaitExited(3000);
        T.Check(Volatile.Read(ref inside) == 1 && ms < 50, $"Dispose returns at once while an inference runs ({ms} ms)");
        T.Check(!exitedEarly && exited, "the AI thread exits only after the running inference has finished");
        T.Check(exitOrder > 0 && finished > 0 && exitOrder > finished, $"the model is released after the inference returned (order {finished} → {exitOrder})");

        // 2. 本物のモデル: 持ち主が先に Dispose しても、AI が使っている間は片付かない
        var m = CedTiny.TryCreate(out string? err);
        T.Check(m is not null, $"model loads ({err ?? "ok"})");
        if (m is null) return;
        var real = new SceneTagger(m, 48000);
        f.Run(real, 3);
        int n0 = real.Windows;
        m.Dispose();
        T.Check(!m.Freed, "the owner's Dispose does not free a model that a tagger still uses");
        f.Run(real, 2);
        T.Check(n0 > 0 && real.Windows > n0 && !real.Failed, $"the tagger keeps classifying after the owner let go ({n0} → {real.Windows})");
        real.Dispose();
        T.Check(real.WaitExited(3000) && m.Freed, "the model is freed when the last tagger's thread exits");
        bool threw = false;
        try { using var again = new SceneTagger(m, 48000); }
        catch (ObjectDisposedException) { threw = true; }
        T.Check(threw, "a new tagger on a freed model is refused (not a crash)");
        threw = false;
        try { m.Classify(new float[CedTiny.WindowSamples], new float[CedTiny.Classes]); }
        catch (ObjectDisposedException) { threw = true; }
        T.Check(threw, "Classify on a freed model throws ObjectDisposedException (not an access violation)");

        // 3. 作り直しの直後のように、2 つの AI が同じモデルを同時に使っても、1 つずつ計算する
        var shared = CedTiny.TryCreate(out err);
        if (shared is null) { T.Check(false, $"model loads again ({err})"); return; }
        var a = new SceneTagger(shared, 48000);
        var b = new SceneTagger(shared, 44100);
        Exception? bad = null;
        var th = new Thread(() =>
        {
            try { new SceneJumpTests.Feeder().Run(b, 4); }
            catch (Exception ex) { bad = ex; }
        });
        th.Start();
        new SceneJumpTests.Feeder().Run(a, 4);
        th.Join();
        T.Check(bad is null && !a.Failed && !b.Failed && a.Windows > 0 && b.Windows > 0, $"two taggers on one model work at the same time ({a.Windows}, {b.Windows}, {bad?.Message ?? "ok"})");
        shared.Dispose();
        a.Dispose();
        b.Dispose();
        T.Check(a.WaitExited(3000) && b.WaitExited(3000) && shared.Freed, "…and the model is freed after both have stopped");
    }

    /// <summary>
    /// サンプリング周波数が変わって楽器の AI を作り直しても、前の場面から続ける（いきなり場面なしにしない）。
    /// 移り変わりの途中なら、それが終わってから、ゆっくり場面なしに戻す
    /// </summary>
    public static void TestNewTaggerContinuesTheOldScene()
    {
        foreach (double into in new[] { 6.0, 0.4 })
        {
            var f = new SceneJumpTests.Feeder();
            var old = new SceneTagger(VocalScores, 48000);
            f.RunUntil(old, () => old.Current.Group == SceneGroup.Vocal, 10);
            var sw = old.Current;
            f.RunUntil(old, () => f.T >= sw.SwitchT + into, 8);
            var shown = old.Current;
            old.Dispose();
            using var fresh = new SceneTagger(DrumScores, 44100);
            fresh.ContinueFrom(shown);
            T.Check(ReferenceEquals(fresh.Current, shown), $"{into} s into the fade: the new tagger starts from the old scene");
            f.Worst = 0;
            f.Look(fresh);
            f.Run(fresh, 8);
            T.Info($"{into} s into the fade: largest jump {f.Worst:F3}; scene {fresh.Current.Group} from {fresh.Current.From}");
            T.Check(f.Worst < 0.05, $"{into} s into the fade: no jump when the tagger is replaced ({f.Worst:F3})");
            T.Check(fresh.Current is { Group: SceneGroup.None, From: SceneGroup.Vocal }, "…and it fades slowly from the vocal scene to None");
        }
    }

    /// <summary>取り直しは、決め方が進めた場面ではなく、見えている場面から（結果を捨てた計算で決め方だけ進んでいたとき）</summary>
    public static void TestTrackerResetStartsFromTheShownScene()
    {
        var tr = new SceneTracker();
        var g = new float[SceneClasses.Count];
        g[SceneClasses.Music] = 0.8f;
        g[SceneClasses.Keys] = 0.5f;
        double t = T0;
        for (; t < T0 + 10; t += SceneTracker.Tune.HopSec) tr.Update(t, g);
        var shown = tr.Current;
        g[SceneClasses.Keys] = 0;
        g[SceneClasses.Vocal] = 0.6f;
        for (; tr.Current.Group != SceneGroup.Vocal && t < T0 + 30; t += SceneTracker.Tune.HopSec) tr.Update(t, g);
        T.Check(shown.Group == SceneGroup.Keys && tr.Current.Group == SceneGroup.Vocal, "the tracker has moved on to Vocal while Keys is shown");
        tr.Reset(t, shown);
        T.Check(tr.Current is { Group: SceneGroup.None, From: SceneGroup.Keys } && tr.Current.SwitchT == t, $"Reset fades from the shown Keys ({tr.Current.From})");
        // 移り変わりの途中を見せているなら、それが終わるまで待つ
        var mid = new MusicScene(SceneGroup.Guitar, SceneGroup.Keys, t, 0.5f);
        tr.Reset(t + 1, mid);
        T.Check(ReferenceEquals(tr.Current, mid), "a fade in progress is kept");
        T.Check(ReferenceEquals(tr.Hold(t + 2), mid), "…until it has finished");
        var after = tr.Hold(t + MusicScene.FadeSec + 0.1);
        T.Check(after is { Group: SceneGroup.None, From: SceneGroup.Guitar }, $"…then it fades from Guitar to None ({after.Group} from {after.From})");
        tr.Reset();
        T.Check(ReferenceEquals(tr.Current, MusicScene.None), "Reset without a time is still an immediate None");
    }

    /// <summary>描画: ふつうの場面の切り替えはクロスフェードを始めない（場面の 2.5 秒の移り変わりのまま）。一度に変わったときだけ始める</summary>
    public static void TestRendererCrossfadesOnlyOnJumps()
    {
        var dev = new RgbDevice(0, "Strip", 24, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var music = ResolvedMusic.Default with { Effect = MusicStyles.Mood, Palette = Palettes.Rainbow, SceneColors = true };
        var input = new RenderInput { Cfg = new RgbConfig { Mode = RgbModes.Music }, Music = music, Device = new RgbDeviceSettings { Name = dev.Name } };
        var output = new Rgb[dev.LedCount];
        double t = T0;
        void Frame(MusicScene s)
        {
            input.Scene = s;
            input.T = input.TEval = t;
            input.TPrevEval = t - 1 / 60.0;
            input.Dt = 1 / 60.0;
            r.Render(input, output);
            t += 1 / 60.0;
        }
        for (int i = 0; i < 30; i++) Frame(MusicScene.None);
        T.Check(!r.Crossfading, "no crossfade while nothing changes");
        var vocal = new MusicScene(SceneGroup.Vocal, SceneGroup.None, t, 0.8f);
        Frame(vocal);
        T.Check(!r.Crossfading, "a normal switch (fade just starting) does not crossfade");
        for (int i = 0; i < 200; i++) Frame(vocal);
        var guitar = new MusicScene(SceneGroup.Guitar, SceneGroup.Vocal, t - 0.05, 0.8f);
        Frame(guitar);
        T.Check(!r.Crossfading, "a switch delivered 50 ms late does not crossfade");
        Frame(guitar);
        var late = new MusicScene(SceneGroup.Keys, SceneGroup.Guitar, t - 1.0, 0.8f);
        Frame(late);
        T.Check(r.Crossfading, "a scene that arrives in the middle of its fade crossfades (it would jump)");
        for (int i = 0; i < 200; i++) Frame(late);
        T.Check(!r.Crossfading, "…for 0.4 s only");
        Frame(MusicScene.None);
        T.Check(r.Crossfading, "a scene that drops straight to None crossfades");
        for (int i = 0; i < 60; i++) Frame(MusicScene.None);
        input.Music = music with { SceneColors = false };
        Frame(MusicScene.None);
        T.Check(!r.Crossfading, "turning the switch off with nothing shown does not crossfade");
        input.Music = music;
        for (int i = 0; i < 60; i++) Frame(late);
        input.Music = music with { SceneColors = false };
        Frame(late);
        T.Check(r.Crossfading, "turning the switch off while a scene is shown crossfades");
    }
}
