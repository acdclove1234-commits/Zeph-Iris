// ==================================================
// CaseLayout.cs - ケースの中の位置に合わせて光を流すための、場所の一覧と LED の位置の計算
//                 (ゾーンごとに場所を選ぶと、LED 1 個ずつのケースの中の座標を作り、光が流れる向きの位置 0〜1 にする)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>ケースの中で光が流れる向き</summary>
public static class CaseDirections
{
    public const string Up = "Up";
    public const string Down = "Down";
    public const string BackToFront = "BackToFront";
    public const string FrontToBack = "FrontToBack";
    public const string Outward = "Outward";
    public const string Inward = "Inward";

    public static IReadOnlyList<(string Id, string Label)> All => _all.Value;
    private static readonly PerLanguage<IReadOnlyList<(string Id, string Label)>> _all = new(() => new[]
    {
        (Up, L("下から上へ")),
        (Down, L("上から下へ")),
        (BackToFront, L("後ろ（背面）から前へ")),
        (FrontToBack, L("前から後ろ（背面）へ")),
        (Outward, L("中心から外へ")),
        (Inward, L("外から中心へ")),
    });

    public static string Normalize(string? id) => All.Any(d => d.Id == id) ? id! : Up;
}

/// <summary>
/// ケースの中の場所 1 つ。座標はケースを横（ガラスの側）から見たもの: x は 0 = 背面 〜 1 = 前面、y は 0 = 上 〜 1 = 下。
/// 輪（ファン）なら、Rings 個の輪の中心を (X1, Y1) から (X2, Y2) まで等間隔に並べ、LED を輪の数で等分する（つながったファン）。
/// 線（テープ・グラボ・メモリ）なら、最初の LED が (X1, Y1)、最後の LED が (X2, Y2)
/// </summary>
public sealed record CasePlace(string Id, string Label, string Group, double X1, double Y1, double X2, double Y2, int Rings = 0, double Radius = 0)
{
    public bool IsRing => Rings > 0;
}

public static class CaseLayout
{
    /// <summary>場所を決めた LED の帯の、仮の長さ（LED の数として扱う）。流れる速さ・帯の長さはこの長さで数える</summary>
    public const float Span = 40f;

    private const double FanR = 0.085, TopR = 0.075, HeadR = 0.06;

    /// <summary>選べる場所（先頭の「決めない」を除く。名前は表示言語で作り直す）</summary>
    public static IReadOnlyList<CasePlace> Places => _places.Value;
    private static readonly PerLanguage<IReadOnlyList<CasePlace>> _places = new(() => new CasePlace[]
    {
        new("front-3-down", L("前面のファン 3 つ（上から順）"), L("ファン"), 0.88, 0.22, 0.88, 0.70, 3, FanR),
        new("front-3-up", L("前面のファン 3 つ（下から順）"), L("ファン"), 0.88, 0.70, 0.88, 0.22, 3, FanR),
        new("front-2-down", L("前面のファン 2 つ（上から順）"), L("ファン"), 0.88, 0.30, 0.88, 0.62, 2, FanR),
        new("front-top", L("前面のファン（上）"), L("ファン"), 0.88, 0.22, 0.88, 0.22, 1, FanR),
        new("front-mid", L("前面のファン（中）"), L("ファン"), 0.88, 0.46, 0.88, 0.46, 1, FanR),
        new("front-bottom", L("前面のファン（下）"), L("ファン"), 0.88, 0.70, 0.88, 0.70, 1, FanR),
        new("top-3-back", L("天面のファン 3 つ（前から順）"), L("ファン"), 0.72, 0.11, 0.30, 0.11, 3, TopR),
        new("top-3-front", L("天面のファン 3 つ（後ろから順）"), L("ファン"), 0.30, 0.11, 0.72, 0.11, 3, TopR),
        new("top-2-back", L("天面のファン 2 つ（前から順）"), L("ファン"), 0.62, 0.11, 0.38, 0.11, 2, TopR),
        new("rear", L("背面のファン"), L("ファン"), 0.11, 0.24, 0.11, 0.24, 1, FanR),
        new("bottom-3-back", L("底面のファン 3 つ（前から順）"), L("ファン"), 0.72, 0.89, 0.30, 0.89, 3, TopR),
        new("bottom-3-front", L("底面のファン 3 つ（後ろから順）"), L("ファン"), 0.30, 0.89, 0.72, 0.89, 3, TopR),
        new("cpu", L("CPU クーラー・水冷ヘッド"), L("部品"), 0.38, 0.30, 0.38, 0.30, 1, HeadR),
        new("gpu", L("グラボ（背面側から前へ）"), L("部品"), 0.14, 0.62, 0.74, 0.62),
        new("gpu-rev", L("グラボ（前から背面側へ）"), L("部品"), 0.74, 0.62, 0.14, 0.62),
        new("ram", L("メモリ（上から下へ）"), L("部品"), 0.56, 0.13, 0.56, 0.40),
        new("ram-up", L("メモリ（下から上へ）"), L("部品"), 0.56, 0.40, 0.56, 0.13),
        new("board-io", L("マザーボード（背面の I/O カバー）"), L("部品"), 0.17, 0.14, 0.17, 0.50),
        new("board-chipset", L("マザーボード（下側・チップセット）"), L("部品"), 0.45, 0.78, 0.72, 0.78),
        new("strip-front", L("前面の縦のテープ（上から下へ）"), L("テープ"), 0.97, 0.04, 0.97, 0.96),
        new("strip-front-up", L("前面の縦のテープ（下から上へ）"), L("テープ"), 0.97, 0.96, 0.97, 0.04),
        new("strip-top", L("天面の縁のテープ（前から後ろへ）"), L("テープ"), 0.96, 0.02, 0.04, 0.02),
        new("strip-bottom", L("底面の縁のテープ（前から後ろへ）"), L("テープ"), 0.96, 0.98, 0.04, 0.98),
    });

    public static CasePlace? Find(string? id) => string.IsNullOrEmpty(id) ? null : Places.FirstOrDefault(p => p.Id == id);

    /// <summary>場所 place に並んだ count 個の LED の、ケースの中の座標（x, y）</summary>
    public static (double X, double Y)[] Points(CasePlace place, int count)
    {
        var pts = new (double X, double Y)[Math.Max(0, count)];
        if (count == 0) return pts;
        if (!place.IsRing)
        {
            for (int j = 0; j < count; j++)
            {
                double u = count <= 1 ? 0.5 : j / (double)(count - 1);
                pts[j] = (place.X1 + (place.X2 - place.X1) * u, place.Y1 + (place.Y2 - place.Y1) * u);
            }
            return pts;
        }
        // 輪: LED を輪の数で等分する（割り切れない分は前の輪に 1 個ずつ）。各輪の最初の LED は真上から時計回り
        int rings = Math.Clamp(place.Rings, 1, Math.Max(1, count));
        int index = 0;
        for (int r = 0; r < rings; r++)
        {
            int m = count / rings + (r < count % rings ? 1 : 0);
            double u = rings <= 1 ? 0 : r / (double)(rings - 1);
            double cx = place.X1 + (place.X2 - place.X1) * u, cy = place.Y1 + (place.Y2 - place.Y1) * u;
            for (int j = 0; j < m; j++, index++)
            {
                double a = 2 * Math.PI * j / Math.Max(1, m);
                pts[index] = (cx + place.Radius * Math.Sin(a), cy - place.Radius * Math.Cos(a));
            }
        }
        return pts;
    }

    /// <summary>ケースの中の座標 → 光が流れる向きの位置（0 = 始まり 〜 1 = 終わり）</summary>
    public static float Along(string? direction, double x, double y)
    {
        double s = CaseDirections.Normalize(direction) switch
        {
            CaseDirections.Down => y,
            CaseDirections.BackToFront => x,
            CaseDirections.FrontToBack => 1 - x,
            CaseDirections.Outward => Distance(x, y),
            CaseDirections.Inward => 1 - Distance(x, y),
            _ => 1 - y,
        };
        return (float)Math.Clamp(s, 0, 1);
    }

    /// <summary>ケースの中心からの距離（角で 1）</summary>
    private static double Distance(double x, double y)
    {
        double dx = x - 0.5, dy = y - 0.5;
        return Math.Sqrt(dx * dx + dy * dy) / Math.Sqrt(0.5);
    }

    /// <summary>場所 place に並んだ count 個の LED の、光が流れる向きの位置（0〜1）</summary>
    public static float[] Positions(CasePlace place, int count, string? direction)
    {
        var pts = Points(place, count);
        var s = new float[pts.Length];
        for (int j = 0; j < pts.Length; j++) s[j] = Along(direction, pts[j].X, pts[j].Y);
        return s;
    }
}
