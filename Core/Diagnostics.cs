// ==================================================
// Diagnostics.cs - 履歴から制御の問題を見つけて、調整案（ボタン 1 つで適用できる変更）を作る
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public enum FindingLevel { Good, Info, Warning, Danger }

public sealed class DiagnosticFinding
{
    public required FindingLevel Level { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>ボタンの文言（null なら調整案なし）</summary>
    public string? ActionLabel { get; init; }
    /// <summary>設定に調整を適用する。適用した内容の説明を返す</summary>
    public Func<AppConfig, string>? Apply { get; init; }
}

public static class Diagnostics
{
    /// <summary>この回数（10 分あたり）より多く回転の上げ下げが入れ替わったら「ふらつき」とみなす</summary>
    private const double OscillationPer10Min = 12;

    /// <param name="temperatures">実センサーの温度一覧（GPU の温度差チェックに使う）</param>
    public static List<DiagnosticFinding> Run(HistoryStore h, AppConfig cfg, string profileName,
        IReadOnlyList<SensorInfo> temperatures, TimeSpan window)
    {
        var list = new List<DiagnosticFinding>();
        if (h.Count < 120)
        {
            list.Add(new() { Level = FindingLevel.Info, Title = L("記録が足りません"), Detail = L("診断には 2 分以上の記録が必要です。しばらく使ってから（できればゲームなどで負荷をかけた後に）もう一度実行してください。") });
            return list;
        }

        int end = h.Count;
        int start = h.LowerBound(h.TimeAt(end - 1) - window);
        double minutes = Math.Max(1, (h.TimeAt(end - 1) - h.TimeAt(start)).TotalMinutes);
        var profile = cfg.FindProfile(profileName);
        if (profile is null) return list;

        string SensorName(string id) => temperatures.FirstOrDefault(t => t.Id == id)?.Name
                                        ?? cfg.VirtualSensors.FirstOrDefault(v => v.Id == id)?.Name ?? id;

        var avgRpm = new Dictionary<FanConfig, double>();

        foreach (var fan in profile.Fans)
        {
            var duty = Series(h, HistoryStore.DutyKey(fan.ControlId), start, end);
            var rpm = Series(h, HistoryStore.RpmKey(fan.ControlId), start, end);
            string id = fan.ControlId;

            if (fan.IsPump)
            {
                if (rpm.Count > 0 && rpm.Min() < fan.PumpAlertRpm * 1.2)
                {
                    list.Add(new()
                    {
                        Level = FindingLevel.Warning,
                        Title = L($"{fan.Name}: ポンプの回転数に余裕がありません"),
                        Detail = L($"最低 {rpm.Min():N0} rpm で、警告値 {fan.PumpAlertRpm:N0} rpm に近づいています。ポンプの劣化や、BIOS 側の設定が低すぎないか確認してください。"),
                    });
                }
                continue;
            }
            if (duty.Count < 60) continue;
            if (rpm.Count > 0) avgRpm[fan] = rpm.Average();

            // --- 1. ふらつき（回転の上げ下げが頻繁に入れ替わる）
            int reversals = 0, lastDir = 0;
            for (int i = 1; i < duty.Count; i++)
            {
                double diff = duty[i] - duty[i - 1];
                if (Math.Abs(diff) < 1.5) continue;
                int dir = Math.Sign(diff);
                if (lastDir != 0 && dir != lastDir) reversals++;
                lastDir = dir;
            }
            double rate = reversals / minutes * 10;
            if (rate > OscillationPer10Min && fan.Mode is FanModes.Curve or FanModes.Target)
            {
                list.Add(new()
                {
                    Level = FindingLevel.Warning,
                    Title = L($"{fan.Name}: 回転が頻繁に上下しています"),
                    Detail = L($"10 分あたり約 {rate:F0} 回、回転の上げ下げが入れ替わっています。耳につく「うなり」の原因になります。"),
                    ActionLabel = L("反応を穏やかにする"),
                    Apply = c =>
                    {
                        foreach (var f in FindFan(c, profileName, id))
                        {
                            if (f.Mode == FanModes.Target)
                            {
                                f.TargetAggressiveness = Math.Max(1, f.TargetAggressiveness - 2);
                            }
                            foreach (var cv in f.Curves)
                            {
                                cv.HysteresisCelsius = Math.Min(10, cv.HysteresisCelsius + 2);
                                cv.EmaAlpha = Math.Max(0.05, Math.Round(cv.EmaAlpha * 0.6, 2));
                            }
                            f.MaxStepDownPerTick = Math.Max(0.5, Math.Round(f.MaxStepDownPerTick * 0.7, 1));
                        }
                        return L("ヒステリシスを +2°C、反応の速さを 0.6 倍、下降の速さを 0.7 倍にしました。");
                    },
                });
            }

            // --- 2. 冷え不足（上限に張り付いている）
            double full = duty.Count(d => d >= 95) / (double)duty.Count;
            if (full > 0.2 && fan.Mode != FanModes.Fixed)
            {
                list.Add(new()
                {
                    Level = FindingLevel.Warning,
                    Title = L($"{fan.Name}: 最大回転に張り付いている時間が長いです"),
                    Detail = L($"記録の {full:P0} で 95% 以上でした。カーブではなく冷却能力の不足が考えられます。ケースのエアフロー（吸気と排気のバランス）やファンの追加を検討してください。"),
                });
            }

            // --- 3. 冷やしすぎ（温度に余裕があるのに回している）
            if (fan.Mode == FanModes.Curve && duty.Average() > fan.MinDuty + 10)
            {
                foreach (var curve in fan.Curves)
                {
                    var temps = Series(h, curve.SensorId, start, end);
                    if (temps.Count < 60) continue;
                    var kind = CurvePresets.Classify(curve.SensorId, SensorName(curve.SensorId));
                    double comfortable = kind switch { SensorKind.GpuHotspot => 80, SensorKind.Other => 50, _ => 65 };
                    if (curve.SensorId.StartsWith(VirtualSensorConfig.IdPrefix) || temps.Max() >= comfortable) continue;

                    string curveName = curve.Name;
                    list.Add(new()
                    {
                        Level = FindingLevel.Info,
                        Title = L($"{fan.Name}: もっと静かにできます"),
                        Detail = L($"「{curveName}」の温度は最高 {temps.Max():F0}°C と余裕がありますが、平均 {duty.Average():F0}% で回しています。カーブを右（高い温度側）へずらすと静かになります。"),
                        ActionLabel = L("カーブを 5°C 右へずらす"),
                        Apply = c =>
                        {
                            foreach (var cv in FindFan(c, profileName, id).SelectMany(f => f.Curves).Where(x => x.Name == curveName))
                            {
                                foreach (var p in cv.Points) p.Temp = Math.Min(110, p.Temp + 5);
                            }
                            return L($"「{curveName}」のカーブを 5°C 右へずらしました。");
                        },
                    });
                    break;
                }
            }

            // --- 4. 停止域・キャリブレーション
            var cal = cfg.FindCalibration(fan.ControlId);
            if (cal is null)
            {
                list.Add(new()
                {
                    Level = FindingLevel.Info,
                    Title = L($"{fan.Name}: キャリブレーションをすると、より正確に調整できます"),
                    Detail = L("「自動セットアップ」か、ファンカーブ画面の「特性を測る」で、止まる回転数と最大回転数を測れます（1 台 約 1〜2 分）。"),
                });
            }
            else if (fan.MinDuty < cal.StopDuty)
            {
                double newMin = Math.Min(60, cal.StopDuty + 5);
                list.Add(new()
                {
                    Level = FindingLevel.Warning,
                    Title = L($"{fan.Name}: 最低回転数が停止域に入っています"),
                    Detail = L($"このファンは {cal.StopDuty:F0}% 未満で止まりますが、最低回転数が {fan.MinDuty:F0}% です。止まったり回ったりを繰り返すと、カチッという音や寿命の低下につながります。"),
                    ActionLabel = L($"最低回転数を {newMin:F0}% にする"),
                    Apply = c =>
                    {
                        foreach (var f in c.Profiles.SelectMany(p => p.Fans).Where(f => f.ControlId == id)) f.MinDuty = newMin;
                        return L($"全プロファイルで最低回転数を {newMin:F0}% にしました。");
                    },
                });
            }

            // --- 5. 停止の検知
            int stalls = 0;
            for (int i = start; i < end; i++)
            {
                float d = h.ValueAt(HistoryStore.DutyKey(id), i), r = h.ValueAt(HistoryStore.RpmKey(id), i);
                if (d >= 30 && r < 1) stalls++;
            }
            if (stalls >= 5)
            {
                list.Add(new()
                {
                    Level = FindingLevel.Danger,
                    Title = L($"{fan.Name}: 回転していない時間がありました"),
                    Detail = L($"30% 以上を指示しているのに 0 rpm だった記録が {stalls} 秒分あります。配線外れやファンの故障を確認してください。"),
                });
            }
        }

        var hot = SensorNames.GpuHotSpotTemperature(temperatures);
        var core = SensorNames.GpuCoreTemperature(temperatures);

        // --- 6. Hot Spot を基準に回しているケースファン
        //     Hot Spot は GPU チップ内の一点の温度で、変動が激しく、ケースファンを回してもほとんど下がらない。
        //     そのため「いつも高回転」の原因になりやすい。GPU 温度（Edge）を基準にするほうが静かで安定する
        if (hot is not null && core is not null)
        {
            foreach (var fan in profile.Fans.Where(f => !f.IsPump))
            {
                var hsCurves = fan.Curves.Where(c => c.SensorId == hot.Id).Select(c => c.Name).ToList();
                bool hsTarget = fan.Mode == FanModes.Target && fan.TargetSensorId == hot.Id;
                if (hsCurves.Count == 0 && !hsTarget) continue;

                string id = fan.ControlId, coreId = core.Id;
                list.Add(new()
                {
                    Level = FindingLevel.Warning,
                    Title = L($"{fan.Name}: GPU Hot Spot を基準に回しています"),
                    Detail = L("Hot Spot は GPU チップ内でいちばん熱い一点の温度で、数秒ごとに大きく上下します。ケースファンを回してもほとんど下がらないため、")
                           + L("「ゲーム中ずっと高回転」の原因になりがちです。ケースファンは GPU 温度（Edge）を基準にするのがおすすめです。"),
                    ActionLabel = L("GPU 温度（Edge）基準に切り替える"),
                    Apply = c =>
                    {
                        // センサーの選び方の問題なので、どのプロファイルでも同じように直す
                        foreach (var f in c.Profiles.SelectMany(p => p.Fans).Where(f => f.ControlId == id))
                        {
                            foreach (var cv in f.Curves.Where(x => x.SensorId == hot.Id))
                            {
                                cv.SensorId = coreId;
                                cv.Name = cv.Name.Replace("HS", "GPU").Replace("Hot Spot", "GPU");
                                cv.Points = CurvePresets.Create(CurvePresets.Balanced, SensorKind.Gpu);
                                cv.HysteresisCelsius = Math.Max(cv.HysteresisCelsius, 4);
                            }
                            if (f.Mode == FanModes.Target && f.TargetSensorId == hot.Id)
                            {
                                f.TargetSensorId = coreId;
                                f.TargetTemp = Math.Min(f.TargetTemp, 75);
                            }
                        }
                        return L("全プロファイルで、GPU 温度（Edge）を基準にした標準のカーブに切り替えました（40°C:30% → 60°C:45% → 75°C:70% → 88°C:100%）。");
                    },
                });
            }
        }

        // --- 7. GPU の Hot Spot と Edge の差（グリス劣化の目安）
        if (hot is not null && core is not null)
        {
            double maxDelta = 0;
            for (int i = start; i < end; i++)
            {
                float a = h.ValueAt(hot.Id, i), b = h.ValueAt(core.Id, i);
                if (!float.IsNaN(a) && !float.IsNaN(b) && b > 50) maxDelta = Math.Max(maxDelta, a - b);
            }
            if (maxDelta > 25)
            {
                list.Add(new()
                {
                    Level = FindingLevel.Warning,
                    Title = L("GPU の Hot Spot と GPU 温度の差が大きいです"),
                    Detail = L($"負荷時に最大 {maxDelta:F0}°C の差がありました（目安は 20〜25°C 以内）。グリスの劣化やクーラーの取り付け圧の問題が考えられます。ケースファンでは改善しにくい症状です。"),
                });
            }
        }

        // --- 8. うなり（回転数の近いファン同士）
        var pairs = avgRpm.Keys.ToList();
        for (int i = 0; i < pairs.Count; i++)
        {
            for (int j = i + 1; j < pairs.Count; j++)
            {
                var a = pairs[i];
                var b = pairs[j];
                double diff = Math.Abs(avgRpm[a] - avgRpm[b]);
                if (avgRpm[a] < 500 || avgRpm[b] < 500 || diff < 20 || diff > 150) continue;
                if (a.Mode == FanModes.Follow || b.Mode == FanModes.Follow) continue;

                string leader = a.ControlId, follower = b.ControlId, leaderName = a.Name, followerName = b.Name;
                list.Add(new()
                {
                    Level = FindingLevel.Info,
                    Title = L($"{a.Name} と {b.Name} で「うなり」が出るかもしれません"),
                    Detail = L($"平均回転数の差が約 {diff:F0} rpm です。近い回転数のファンは音が干渉して「ウォンウォン」と聞こえることがあります。同じ役割なら、回転を揃えると解消します。"),
                    ActionLabel = L($"{followerName} を {leaderName} に連動させる"),
                    Apply = c =>
                    {
                        foreach (var f in c.Profiles.SelectMany(p => p.Fans).Where(f => f.ControlId == follower))
                        {
                            f.Mode = FanModes.Follow;
                            f.FollowControlId = leader;
                            f.FollowMatchRpm = true;
                        }
                        return L($"全プロファイルで「{followerName}」を「{leaderName}」に連動させました。");
                    },
                });
            }
        }

        if (list.All(f => f.Level is FindingLevel.Info or FindingLevel.Good && f.Apply is null))
        {
            list.Insert(0, new() { Level = FindingLevel.Good, Title = L("大きな問題は見つかりませんでした"), Detail = L($"直近 {minutes:F0} 分の記録を確認しました。") });
        }
        // 「問題なし」を先頭に、あとは重要なものから
        return list.OrderBy(f => f.Level == FindingLevel.Good ? 0 : 1).ThenByDescending(f => f.Level).ToList();
    }

    private static List<double> Series(HistoryStore h, string key, int start, int end)
    {
        var list = new List<double>(end - start);
        for (int i = start; i < end; i++)
        {
            float v = h.ValueAt(key, i);
            if (!float.IsNaN(v)) list.Add(v);
        }
        return list;
    }

    private static IEnumerable<FanConfig> FindFan(AppConfig cfg, string profile, string controlId)
        => cfg.FindProfile(profile)?.Fans.Where(f => f.ControlId == controlId) ?? Enumerable.Empty<FanConfig>();
}
