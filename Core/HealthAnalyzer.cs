// ==================================================
// HealthAnalyzer.cs - 長期の記録から、ファンの劣化（同じ Duty で回転数が変わる）と
//                     ホコリ・目詰まり（同じ電力の差で温度の上がり方が大きくなる）を見つける
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public enum HealthLevel { Collecting, Good, Info, Warning, Danger }

public sealed class HealthItem
{
    public required string Title { get; init; }
    public required HealthLevel Level { get; init; }
    public required string Detail { get; init; }
    /// <summary>主な数値（例: 「60% 時 1,180 rpm（基準 1,250 rpm）」）</summary>
    public string Metric { get; init; } = "";
    /// <summary>変化率（−0.08 = 8% 低下）。表示用</summary>
    public double? Change { get; init; }
}

/// <summary>
/// 比べ方:
///   ・基準 … 「掃除した」日以降（なければ記録の最初）の 7 日間。キャリブレーション済みのファンは、その測定値も基準に使える
///   ・最近 … 直近 7 日間
/// 同じ条件の時間どうしを比べるので、使い方が変わっても判定がぶれにくい:
///   ・ファン … 同じ Duty（5% ごと）で回転が安定していた分の回転数。基準・最近の両方に 20 分以上ある Duty だけ
///   ・冷え方 … 同じ日の「休んでいるとき」と「負荷をかけているとき」の温度差 ÷ 消費電力の差（°C/W）。
///              室温は両方に同じだけ入るので打ち消し合い、季節（夏・冬）で判定が変わらない。
///              休んでいる時間のない日は、室温のセンサーがあればそれとの差を使う（なければその日は使わない）。
///              2 つの求め方は値の大きさが違うので、基準と最近は同じ求め方どうしで比べる（混ぜると、変わっていないのに悪化して見える）
/// 変わり方も見る: 日ごとの値の並びから、ある日を境に急に変わった（交換・つなぎ替え・何かが当たった）のか、
/// じわじわ変わった（劣化・ホコリ）のかを見分けて、原因の候補を書き分ける。
/// 冷え方は、比べる時間のファンの回転の指示が基準と大きく違えば（ファンカーブを変えた）、ホコリとは判定しない
/// </summary>
public static class HealthAnalyzer
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    /// <summary>判定に必要な、記録の最低日数</summary>
    public const int MinDays = 10;
    /// <summary>1 つの条件（Duty・電力の区切り）で比べるのに必要な分数（基準・最近のそれぞれ）</summary>
    private const int MinSamples = 20;
    /// <summary>冷え方を比べるのに必要な、負荷をかけた時間の合計（分。基準・最近のそれぞれ）</summary>
    private const int MinLoadMinutes = 60;
    /// <summary>休んでいるときの温度を決めるのに必要な、その日の休んでいた時間（分）</summary>
    private const int MinIdleMinutes = 10;
    /// <summary>冷え方を比べる時間のファンの Duty が、基準とこれ以上違えば「設定が変わった」とみなす（ポイント）</summary>
    private const double FanSettingChangeDuty = 8;
    /// <summary>「急に変わった」とみなす: 変化のうち、ある日を境にした段差で説明できる割合</summary>
    private const double StepShare = 0.7;

    public static List<HealthItem> Analyze(IReadOnlyList<MinuteRow> rows, AppConfig cfg, DateTime now)
    {
        var list = new List<HealthItem>();
        var since = cfg.Maintenance.LastCleaned ?? DateTime.MinValue;
        var data = rows.Where(r => r.Time >= since).ToList();
        double days = data.Count == 0 ? 0 : (now - data[0].Time).TotalDays;

        var recentFrom = now - Window;
        var baseRows = data.Where(r => r.Time < (data.Count > 0 ? data[0].Time : now) + Window).ToList();
        var recentRows = data.Where(r => r.Time >= recentFrom).ToList();
        bool enough = days >= MinDays;

        foreach (var fan in cfg.Profiles.SelectMany(p => p.Fans).GroupBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            list.Add(AnalyzeFan(fan, cfg, data, baseRows, recentRows, enough, days, since, now));

        var fanDutyKeys = cfg.Profiles.SelectMany(p => p.Fans).Where(f => !f.IsPump && !f.IsGpuFan)
            .Select(f => HistoryStore.DutyKey(f.ControlId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AddCooling(list, L("CPU の冷え方"), StatRoles.CpuTemp, StatRoles.CpuPower, 30, 20, data, baseRows, recentRows, enough, days, fanDutyKeys);
        AddCooling(list, L("GPU の冷え方"), StatRoles.GpuTemp, StatRoles.GpuPower, 60, 35, data, baseRows, recentRows, enough, days, fanDutyKeys);
        return list;
    }

    // =====================================================================
    // ファン・ポンプ
    // =====================================================================

    private static HealthItem AnalyzeFan(FanConfig fan, AppConfig cfg, List<MinuteRow> data, List<MinuteRow> baseRows, List<MinuteRow> recentRows,
        bool enough, double days, DateTime since, DateTime now)
    {
        string rpmKey = HistoryStore.RpmKey(fan.ControlId), dutyKey = HistoryStore.DutyKey(fan.ControlId);
        var cal = cfg.FindCalibration(fan.ControlId);
        bool calBase = cal is not null && cal.Points.Count >= 3 && now - cal.MeasuredAt >= Window && cal.MeasuredAt >= since;
        if (!enough && !calBase)
            return Collecting(fan.Name, L($"データを集めています（比べられるまで あと {Math.Max(1, Math.Ceiling(MinDays - days)):F0} 日）。キャリブレーション済みなら、その 7 日後から判定できます。"));

        var recent = Buckets(recentRows, dutyKey, rpmKey);
        Dictionary<int, (double Median, int Count)> baseline;
        string baseText;
        if (calBase)
        {
            baseline = recent.Keys.Where(k => cal!.EstimateRpm(k) is double).ToDictionary(k => k, k => (cal!.EstimateRpm(k)!.Value, int.MaxValue));
            baseText = L($"{cal!.MeasuredAt:M/d} のキャリブレーション");
        }
        else
        {
            baseline = Buckets(baseRows, dutyKey, rpmKey).ToDictionary(b => b.Key, b => (Median(b.Value), b.Value.Count));
            baseText = L($"{baseRows.FirstOrDefault()?.Time:M/d} ごろ");
        }

        var compare = recent.Where(b => b.Value.Count >= MinSamples && baseline.TryGetValue(b.Key, out var bv) && bv.Count >= MinSamples && bv.Median > 100)
            .Select(b => (Duty: b.Key, Recent: Median(b.Value), Base: baseline[b.Key].Median, Weight: Math.Min(b.Value.Count, baseline[b.Key].Count)))
            .ToList();
        if (compare.Count == 0)
            return Collecting(fan.Name, L("同じ回転の指示（Duty）で回っていた時間が、基準と最近の両方でまだ少ないため、比べられません。"));

        double change = compare.Sum(c => (c.Recent - c.Base) / c.Base * c.Weight) / compare.Sum(c => c.Weight);
        var main = compare.OrderByDescending(c => c.Weight).First();
        string metric = L($"{main.Duty}% 時 {main.Recent:N0} rpm（{baseText} {main.Base:N0} rpm）");
        int recentMinutes = compare.Sum(c => recent[c.Duty].Count);

        // 変わり方: 日ごとの「基準からの変化」の並びで、急な段差か、じわじわか
        var daily = DailyFanChange(data, dutyKey, rpmKey, compare.ToDictionary(c => c.Duty, c => c.Base));
        var shape = Shape(daily);
        string when = shape.Sudden && shape.StepDay is DateTime d ? L($"{d:M/d} ごろから急に") : L("少しずつ");

        HealthLevel level;
        string detail;
        if (change <= -0.10)
        {
            level = change <= -0.20 ? HealthLevel.Danger : HealthLevel.Warning;
            detail = fan.IsPump
                ? L($"ポンプの回転数が{when}下がっています。ポンプの劣化や、冷却液の減少が考えられます。") + (level == HealthLevel.Danger ? L("早めに点検してください。") : L("さらに下がるようなら点検してください。"))
                : shape.Sudden
                    ? L($"回転数が{when}下がっています。羽根にケーブルなどが当たっていないか、配線がゆるんでいないか確認してください。直らなければファンの不調が考えられます。")
                    : L("回転数が少しずつ下がっています。羽根やフィルターのホコリを掃除してください。掃除しても戻らなければ、軸受けの劣化が考えられるので交換を検討してください。");
        }
        else if (change >= 0.12)
        {
            // 回転が上がる: 交換・つなぎ替えのほか、吸気がふさがって空気が流れにくいとファンは軽く回って速くなる
            level = !shape.Sudden && change >= 0.20 && !fan.IsPump ? HealthLevel.Warning : HealthLevel.Info;
            detail = fan.IsPump
                ? L($"ポンプが{when}速く回るようになっています。ポンプを交換した・つなぎ替えた場合は問題ありません。そうでなければ、冷却液の減少（空回り）を確認してください。")
                : shape.Sudden
                    ? L($"{when}速く回るようになっています。ファンを交換した・つなぎ替えた場合は問題ありません。そうでなければ、吸気のフィルターがふさがっていないか確認してください（空気が流れにくいと、ファンは軽く回って速くなります）。")
                    : L("少しずつ速く回るようになっています。吸気のフィルターやラジエーターが目詰まりして、空気が流れにくくなっている可能性があります（空気が流れにくいと、ファンは軽く回って速くなります）。掃除してください。");
        }
        else
        {
            level = HealthLevel.Good;
            detail = L("回転数は以前と変わらず、正常です。");
        }
        detail += L($"（比べた時間: 最近 {recentMinutes} 分）");
        return new HealthItem { Title = fan.Name, Level = level, Detail = detail, Metric = metric, Change = change };
    }

    /// <summary>Duty を 5% 単位に丸めて、回転が安定していた分の回転数を集める</summary>
    private static Dictionary<int, List<double>> Buckets(IEnumerable<MinuteRow> rows, string dutyKey, string rpmKey)
    {
        var map = new Dictionary<int, List<double>>();
        foreach (var r in rows)
        {
            if (r.Get(dutyKey) is not float duty || r.Get(rpmKey) is not float rpm || rpm < 100) continue;
            if (r.DutySpread.TryGetValue(dutyKey, out var spread) && spread > 3) continue;
            int bucket = (int)(Math.Round(duty / 5) * 5);
            if (!map.TryGetValue(bucket, out var list)) map[bucket] = list = new();
            list.Add(rpm);
        }
        return map;
    }

    /// <summary>日ごとの、基準からの回転数の変化（比べられる Duty の分だけ。1 日 10 分以上ある日）</summary>
    private static List<(DateTime Day, double Change)> DailyFanChange(List<MinuteRow> data, string dutyKey, string rpmKey, Dictionary<int, double> baseByDuty)
    {
        var res = new List<(DateTime, double)>();
        foreach (var g in data.GroupBy(r => r.Time.Date).OrderBy(g => g.Key))
        {
            var changes = new List<double>();
            foreach (var r in g)
            {
                if (r.Get(dutyKey) is not float duty || r.Get(rpmKey) is not float rpm || rpm < 100) continue;
                if (r.DutySpread.TryGetValue(dutyKey, out var spread) && spread > 3) continue;
                int bucket = (int)(Math.Round(duty / 5) * 5);
                if (baseByDuty.TryGetValue(bucket, out var b) && b > 100) changes.Add((rpm - b) / b);
            }
            if (changes.Count >= 10) res.Add((g.Key, Median(changes)));
        }
        return res;
    }

    // =====================================================================
    // 冷え方
    // =====================================================================

    /// <summary>
    /// 冷え方 = 温度の上がり方 ÷ 消費電力（°C/W。大きいほど冷えにくい）。
    /// 同じ日の休んでいるとき（消費電力 idleMax 未満の中央値）との差で求めるので、室温が変わっても判定が変わらない
    /// </summary>
    private static void AddCooling(List<HealthItem> list, string title, string tempRole, string powerRole, double minPower, double idleMax,
        List<MinuteRow> data, List<MinuteRow> baseRows, List<MinuteRow> recentRows, bool enough, double days, List<string> fanDutyKeys)
    {
        if (data.Count > 0 && !data.Any(r => r.Role(tempRole) is not null && r.Role(powerRole) is not null)) return;
        if (!enough)
        {
            list.Add(Collecting(title, L($"データを集めています（あと {Math.Max(1, Math.Ceiling(MinDays - days)):F0} 日）。")));
            return;
        }

        // 基準と最近は、同じ求め方どうしで比べる。「休んでいるときとの差」と「室温のセンサーとの差」は値の大きさが違い
        // （室温のセンサーは、マザーボードの上の温度で室温より高いことがある）、混ぜると冷え方が変わったように見えるため。
        // 休んでいるときとの差で比べられればそれを使い、だめなら室温のセンサーとの差どうしで比べる
        var (compare, bMin, rMin) = CompareCooling(baseRows, recentRows, tempRole, powerRole, minPower, idleMax, fanDutyKeys, useIdle: true);
        if (compare.Count == 0 || bMin < MinLoadMinutes || rMin < MinLoadMinutes)
        {
            var (byAmbient, bA, rA) = CompareCooling(baseRows, recentRows, tempRole, powerRole, minPower, idleMax, fanDutyKeys, useIdle: false);
            if (byAmbient.Count > 0 && bA >= MinLoadMinutes && rA >= MinLoadMinutes) (compare, bMin, rMin) = (byAmbient, bA, rA);
        }
        if (compare.Count == 0 || bMin < MinLoadMinutes || rMin < MinLoadMinutes)
        {
            list.Add(Collecting(title, L($"負荷をかけた時間（ゲームなど）と、休んでいる時間の記録がまだ少ないため、比べられません（基準 {bMin} 分・最近 {rMin} 分。それぞれ {MinLoadMinutes} 分以上必要）。")));
            return;
        }

        // 比べる時間のファンの回転の指示が大きく違う: ファンカーブを変えた。冷え方の違いはホコリではないかもしれない
        double dutyB = WeightedOrNaN(compare.Select(c => (c.BaseDuty, (double)c.Weight)));
        double dutyR = WeightedOrNaN(compare.Select(c => (c.RecentDuty, (double)c.Weight)));
        if (!double.IsNaN(dutyB) && !double.IsNaN(dutyR) && Math.Abs(dutyR - dutyB) >= FanSettingChangeDuty)
        {
            list.Add(new HealthItem
            {
                Title = title,
                Level = HealthLevel.Info,
                Detail = L($"負荷をかけたときのファンの回転の指示が、以前と変わっています（{dutyB:F0}% → {dutyR:F0}%）。ファンカーブを変えたなら、冷え方の違いはその影響なので、") +
                         L("ホコリかどうかは判定していません。新しい設定で比べ直すには「掃除した（ここを新しい基準にする）」を押してください。"),
                Metric = L($"ファンの指示 {dutyB:F0}% → {dutyR:F0}%"),
            });
            return;
        }

        double change = compare.Sum(c => (c.Recent - c.Base) / c.Base * c.Weight) / compare.Sum(c => c.Weight);
        var main = compare.OrderByDescending(c => c.Weight).First();
        // 温度の上がり幅に直す（例: 休んでいるときより 80 W 多いとき、0.40°C/W → 32°C 上昇）
        string metric = L($"{main.Power} W 分の温度上昇 {main.Recent * main.Power:F0}°C（以前 {main.Base * main.Power:F0}°C）");
        var (level, detail) = change switch
        {
            >= 0.25 => (HealthLevel.Warning, L("以前より明らかに冷えにくくなっています。フィルター・ラジエーター・ヒートシンクのホコリを掃除してください。改善しなければグリスの劣化も考えられます。")),
            >= 0.12 => (HealthLevel.Info, L("以前より少し冷えにくくなっています。そろそろフィルターやラジエーターの掃除をおすすめします。")),
            <= -0.15 => (HealthLevel.Info, L("以前よりよく冷えるようになっています。掃除・部品の交換・ファンの追加などをした場合は問題ありません。")),
            _ => (HealthLevel.Good, L("以前と同じように冷えています。")),
        };
        detail += L($"（比べた時間: 基準 {bMin} 分・最近 {rMin} 分。室温の違いは差し引いています）");
        list.Add(new HealthItem { Title = title, Level = level, Detail = detail, Metric = metric, Change = change });
    }

    /// <summary>基準と最近の冷えにくさを、同じ求め方（useIdle）で電力の区切りごとに並べる。比べた分数（基準・最近）も返す</summary>
    private static (List<(int Power, double Recent, double Base, double RecentDuty, double BaseDuty, int Weight)> Compare, int BaseMinutes, int RecentMinutes)
        CompareCooling(List<MinuteRow> baseRows, List<MinuteRow> recentRows, string tempRole, string powerRole, double minPower, double idleMax,
            List<string> fanDutyKeys, bool useIdle)
    {
        var b = Resistance(baseRows, tempRole, powerRole, minPower, idleMax, fanDutyKeys, useIdle);
        var r = Resistance(recentRows, tempRole, powerRole, minPower, idleMax, fanDutyKeys, useIdle);
        var compare = r.Where(x => x.Value.Count >= MinSamples && b.TryGetValue(x.Key, out var bl) && bl.Count >= MinSamples)
            .Select(x => (Power: x.Key, Recent: Median(x.Value.Select(s => s.R).ToList()), Base: Median(b[x.Key].Select(s => s.R).ToList()),
                RecentDuty: AverageOrNaN(x.Value.Select(s => s.Duty)), BaseDuty: AverageOrNaN(b[x.Key].Select(s => s.Duty)),
                Weight: Math.Min(x.Value.Count, b[x.Key].Count)))
            .ToList();
        return (compare, b.Values.Sum(v => v.Count), r.Values.Sum(v => v.Count));
    }

    /// <summary>
    /// 電力の差（20 W ごと）ごとの、温度の上がり方 ÷ 電力の差（°C/W）と、そのときのファンの Duty の平均。
    /// useIdle = true: 休んでいる時間のある日だけ。その日の「休んでいるとき」の温度・電力からの差を使う。
    /// useIdle = false: 休んでいる時間のない日だけ。室温のセンサーとの差（電力そのもので割る）を使う
    /// </summary>
    private static Dictionary<int, List<(double R, double Duty)>> Resistance(IEnumerable<MinuteRow> rows, string tempRole, string powerRole,
        double minPower, double idleMax, List<string> fanDutyKeys, bool useIdle)
    {
        var map = new Dictionary<int, List<(double, double)>>();
        foreach (var day in rows.GroupBy(r => r.Time.Date))
        {
            var idle = day.Where(r => r.Role(tempRole) is float && r.Role(powerRole) is float p && p < idleMax).ToList();
            double? tIdle = null, pIdle = null;
            if (idle.Count >= MinIdleMinutes)
            {
                tIdle = Median(idle.Select(r => (double)r.Role(tempRole)!.Value).ToList());
                pIdle = Median(idle.Select(r => (double)r.Role(powerRole)!.Value).ToList());
            }
            if (useIdle != tIdle.HasValue) continue; // 求め方の違う日は混ぜない
            foreach (var r in day)
            {
                if (r.Role(tempRole) is not float t || r.Role(powerRole) is not float p || p < minPower) continue;
                double rise, dp;
                if (tIdle is double ti && pIdle is double pi)
                {
                    rise = t - ti;
                    dp = p - pi;
                }
                else if (r.Role(StatRoles.Ambient) is float a and > 10 and < 60)
                {
                    rise = t - a;
                    dp = p;
                }
                else continue; // 休んでいる時間も室温もない日は使わない（25°C と決めつけると、季節で判定が変わる）
                if (dp < 10 || rise <= 0) continue;
                int bucket = (int)(Math.Round(dp / 20) * 20);
                if (bucket <= 0) continue;
                double duty = AverageOrNaN(fanDutyKeys.Select(k => r.Get(k) is float d ? d : double.NaN).Where(d => !double.IsNaN(d)));
                if (!map.TryGetValue(bucket, out var l)) map[bucket] = l = new();
                l.Add((rise / dp, duty));
            }
        }
        return map;
    }

    // =====================================================================
    // 共通
    // =====================================================================

    /// <summary>
    /// 日ごとの変化の並びの形: いちばん大きな段差（その日の前後の平均の差）が、全体の変化の StepShare 以上を説明し、
    /// 段差の前後がそれぞれ平らなら「急に変わった」（その日を返す）
    /// </summary>
    private static (bool Sudden, DateTime? StepDay) Shape(List<(DateTime Day, double Change)> daily)
    {
        if (daily.Count < 6) return (false, null);
        double total = daily.Skip(daily.Count - 3).Average(d => d.Change) - daily.Take(3).Average(d => d.Change);
        if (Math.Abs(total) < 0.05) return (false, null);
        int bestK = -1;
        double bestScore = 0, bestStep = 0;
        for (int k = 2; k <= daily.Count - 2; k++)
        {
            double before = daily.Take(k).Average(d => d.Change), after = daily.Skip(k).Average(d => d.Change);
            double step = after - before;
            double score = Math.Abs(step) * Math.Sqrt(k * (double)(daily.Count - k) / daily.Count);
            if (score > bestScore) { bestScore = score; bestK = k; bestStep = step; }
        }
        if (bestK < 0 || Math.Sign(bestStep) != Math.Sign(total)) return (false, null);
        // 段差の前後のばらつき（平らか）
        double Spread(IEnumerable<double> xs)
        {
            var a = xs.ToList();
            double m = a.Average();
            return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Count);
        }
        double within = Math.Max(Spread(daily.Take(bestK).Select(d => d.Change)), Spread(daily.Skip(bestK).Select(d => d.Change)));
        bool sudden = Math.Abs(bestStep) >= StepShare * Math.Abs(total) && within <= 0.35 * Math.Abs(bestStep);
        return (sudden, sudden ? daily[bestK].Day : null);
    }

    private static HealthItem Collecting(string title, string detail) => new() { Title = title, Level = HealthLevel.Collecting, Detail = detail };

    private static double AverageOrNaN(IEnumerable<double> xs)
    {
        double s = 0;
        int n = 0;
        foreach (var x in xs) if (!double.IsNaN(x)) { s += x; n++; }
        return n > 0 ? s / n : double.NaN;
    }

    private static double WeightedOrNaN(IEnumerable<(double Value, double Weight)> xs)
    {
        double s = 0, w = 0;
        foreach (var (v, wt) in xs) if (!double.IsNaN(v)) { s += v * wt; w += wt; }
        return w > 0 ? s / w : double.NaN;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
