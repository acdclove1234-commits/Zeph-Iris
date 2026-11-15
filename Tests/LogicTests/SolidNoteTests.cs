// 単色（選んだ色だけ）のとき、「楽器で光る」をすすめる案内を出すか（Palettes.SuggestEnsemble）
using ProFanController;

namespace LogicTests;

public static class SolidNoteTests
{
    public static void TestSolidSuggestsEnsemble()
    {
        RgbConfig Cfg(string mode, string palette, string style)
        {
            var c = new RgbConfig { Mode = mode, MusicStyle = style };
            c.Music ??= new MusicConfig();
            c.Music.Palette = palette;
            return c;
        }
        T.Check(Palettes.SuggestEnsemble(Cfg(RgbModes.Music, Palettes.Solid, MusicStyles.BeatHue)), "single colour + another style → suggest 「楽器で光る」");
        T.Check(!Palettes.SuggestEnsemble(Cfg(RgbModes.Music, Palettes.Solid, MusicStyles.Ensemble)), "already 「楽器で光る」 → no note");
        T.Check(!Palettes.SuggestEnsemble(Cfg(RgbModes.Music, Palettes.Rainbow, MusicStyles.BeatHue)), "colour palette → no note");
        T.Check(!Palettes.SuggestEnsemble(Cfg(RgbModes.Static, Palettes.Solid, MusicStyles.BeatHue)), "not music mode → no note");
    }
}
