// ==================================================
// Advisor.cs - AI 冷却アドバイザー (履歴の統計を要約し、ローカルの Ollama に分析させる)
// Dependencies: .NET 8 / System.Net.Http / System.Text.Json (MIT License), Ollama (MIT License・別途インストール)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProFanController;

/// <summary>Ollama の HTTP API（既定 http://localhost:11434）。データは PC の外に出ない</summary>
public static class OllamaClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>モデルが一度に読める長さ（トークン）</summary>
    public const int ContextTokens = 8192;
    /// <summary>答え終わってから、モデルをグラボのメモリに置いておく時間</summary>
    public const string KeepAlive = "1m";

    public static async Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        string json = await Http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/tags", cts.Token);
        var root = JsonNode.Parse(json);
        return root?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? new();
    }

    /// <summary>回答を少しずつ受け取る（ストリーミング）。考え中の文章（thinking）は除く</summary>
    public static async IAsyncEnumerable<string> ChatStreamAsync(string baseUrl, string model, string system, string user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            // num_ctx: 渡す要約（約 2,000〜3,000 トークン）と回答・考え中の文章が入る大きさ（既定の 4,096 では、考えが長いと足りない）。
            // keep_alive: 終わったらモデルを早めに外して、グラボのメモリをゲームなどに返す（既定は 5 分持ったまま）
            ["options"] = new JsonObject { ["temperature"] = 0.3, ["num_ctx"] = ContextTokens },
            ["keep_alive"] = KeepAlive,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string err = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(L($"Ollama がエラーを返しました ({(int)res.StatusCode}): {err}"));
        }

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        bool inThink = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node?["error"]?.GetValue<string>() is { } error) throw new HttpRequestException(error);
            string piece = node?["message"]?["content"]?.GetValue<string>() ?? "";

            // <think>…</think> を出力するモデル（qwen3 など）は、その部分を表示しない
            if (piece.Contains("<think>")) { inThink = true; piece = piece[..piece.IndexOf("<think>")]; }
            if (inThink)
            {
                int close = piece.IndexOf("</think>");
                if (close < 0) continue;
                inThink = false;
                piece = piece[(close + 8)..];
            }
            if (piece.Length > 0) yield return piece;
            if (node?["done"]?.GetValue<bool>() == true) yield break;
        }
    }
}

/// <summary>AI に渡す「PC の冷却状況の要約」を作る。生データではなく統計だけを渡す</summary>
public static class AdvisorPrompt
{
    /// <summary>
    /// AI への指示（指示そのものは日本語のまま。答えは画面の言語で書いてもらう。
    /// 見出しの「## 全体の評価」なども、その言語に直して書くよう頼む）
    /// </summary>
    public static string System => Localization.IsSource
        ? SystemJa
        : SystemJa.Replace("日本語で分かりやすく", $"{Localization.Current.NativeName}（{Localization.Current.EnglishName}）で分かりやすく")
          + $"\nWrite the whole answer, including the section headings, in {Localization.Current.EnglishName} ({Localization.Current.NativeName}), not in Japanese.";

    private const string SystemJa =
        "あなたは自作 PC の冷却とファン制御に詳しいエンジニアです。" +
        "ユーザーの PC の計測データの要約を読み、日本語で分かりやすくアドバイスしてください。\n" +
        "出力の構成:\n" +
        "## 全体の評価（2〜3 文）\n" +
        "## 気になる点（箇条書き。根拠となる数値を添える）\n" +
        "## 具体的な改善案\n" +
        "## 注意点\n" +
        "データから断定できないことは「推測ですが」と明記してください。部品の交換を勧める前に、設定で改善できる方法を優先してください。\n" +
        "守ること:\n" +
        "- 温度が高いか低いかは、各温度に添えた「目安の上限」と「余裕」で判断してください。余裕が 15°C 以上あれば「高め」とは書かず、「余裕がある」と書いてください。\n" +
        "- 「具体的な改善案」には、データの最後の「アプリが計算した提案」だけを、数値を変えずに、わかりやすく説明して書いてください。" +
        "それ以外の数値の提案（ほかのカーブ・ほかのセンサー・ヒステリシス・最低回転数・ファン同士の回転数をそろえるなど）を自分で作らないでください。" +
        "提案が「なし」なら「今の設定のままで問題ありません」とだけ書いてください。\n" +
        "- BIOS・ドライバーが制御しているファンには触れないでください。\n" +
        "- 提案と注意点で矛盾したことを書かないでください。この「守ること」の内容を、答えの中に書き写さないでください。";

    public static string Build(HistoryStore h, AppConfig cfg, string profile,
        IReadOnlyList<(string Id, string Name)> temps, IReadOnlyList<(string Id, string Name, string Unit)> metrics,
        IEnumerable<DiagnosticFinding> findings, CoolingTestReport? test, string? userQuestion, IEnumerable<string>? longTerm = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (h.Count == 0) return L("記録がまだありません。");

        var first = h.TimeAt(0);
        var last = h.TimeAt(h.Count - 1);
        sb.AppendLine(L($"# 記録期間: {first:MM/dd HH:mm} 〜 {last:MM/dd HH:mm}（{(last - first).TotalMinutes:F0} 分。起動前の分は 1 分ごとの平均、起動後は 1 秒ごと）"));
        sb.AppendLine(L($"# 実行中のプロファイル: {profile}"));
        sb.AppendLine(L($"# 緊急全開: CPU 等 {cfg.EmergencyTemperature:F0}°C ／ GPU Hot Spot {cfg.EmergencyHotspotTemperature:F0}°C"));
        sb.AppendLine();

        sb.AppendLine(L("## 温度（最小 / 平均 / 95パーセンタイル / 最大, °C）"));
        var skipped = new List<string>();
        foreach (var (id, name) in temps)
        {
            if (Stats(h, id) is not { } s) continue;
            // 0°C 以下・120°C 超えは未接続の端子や読み取り誤り。AI が「故障」と誤解しないよう渡さない
            // 最小値が 70°C を超えている＝アイドル時でも高いままのセンサーも、何もつながっていない可能性が高い
            // 動いている PC の部品が、ずっと 20°C 未満（部屋より冷たい）ということはない。壊れた値のセンサー（例: VRM 14°C）
            if (s.Min <= 0 || s.Max > 120 || s.Max < 20 || (s.Min > 70 && !name.Contains("Hot Spot")))
            {
                skipped.Add(name);
                continue;
            }
            sb.Append(string.Format(inv, "- {0}: {1:F0} / {2:F0} / {3:F0} / {4:F0}", name, s.Min, s.Avg, s.P95, s.Max));
            if (LimitFor(id, name) is double limit)
                sb.Append(string.Format(inv, L("（目安の上限 {0:F0}°C・最大との余裕 {1:F0}°C）"), limit, limit - s.Max));
            sb.AppendLine();
        }
        if (skipped.Count > 0)
            sb.AppendLine(L($"（{string.Join(L("、"), skipped)} は、未接続または値が不正なセンサーのため除外しました。これらについては言及しないでください）"));

        sb.AppendLine();
        sb.AppendLine(L("## 負荷など（平均 / 最大）"));
        foreach (var (id, name, unit) in metrics)
        {
            if (Stats(h, id) is { } s) sb.AppendLine(string.Format(inv, "- {0}: {1:F0} / {2:F0} {3}", name, s.Avg, s.Max, unit));
        }

        sb.AppendLine();
        sb.AppendLine(L("## このアプリで制御しているファンの設定と実績"));
        var notMine = new List<string>();
        var proposals = new List<string>();
        foreach (var fan in cfg.FindProfile(profile)?.Fans ?? new())
        {
            // BIOS 任せのファンは設定を渡さない（渡すと、使われていないカーブの変更を AI が勧めてしまう）。名前だけ最後に並べる
            if (!fan.Enabled)
            {
                notMine.Add(fan.Name);
                continue;
            }
            var duty = Stats(h, HistoryStore.DutyKey(fan.ControlId));
            var rpm = Stats(h, HistoryStore.RpmKey(fan.ControlId));
            var cal = cfg.FindCalibration(fan.ControlId);
            sb.Append(L($"- {fan.Name}（{(fan.IsPump ? L("ポンプ") : L("ファン"))}、制御: {ModeText(fan)}）"));
            if (duty is { } d) sb.Append(string.Format(inv, L(" Duty 平均 {0:F0}% / 最大 {1:F0}%"), d.Avg, d.Max));
            if (rpm is { } r) sb.Append(string.Format(inv, L("、回転 平均 {0:F0} / 最大 {1:F0} rpm"), r.Avg, r.Max));
            sb.AppendLine();
            if (fan.Mode == FanModes.Curve)
            {
                if (fan.Curves.Count > 1)
                    sb.AppendLine(L($"  - カーブが {fan.Curves.Count} 本あり、そのときいちばん高い Duty が使われます"));
                foreach (var c in fan.Curves)
                {
                    string pts = string.Join(" → ", c.Points.Select(p => string.Format(inv, "{0:F0}°C:{1:F0}%", p.Temp, p.Duty)));
                    string sensor = temps.FirstOrDefault(t => t.Id == c.SensorId).Name ?? c.SensorId;
                    sb.AppendLine(L($"  - カーブ「{c.Name}」 センサー: {sensor} ／ {pts} ／ ヒステリシス {c.HysteresisCelsius:F1}°C"));
                    // カーブのうち、実際の温度の範囲で使われている部分（AI は点の並びだけでは、どこが効いているか読み違える）
                    if (Stats(h, c.SensorId) is { } t && c.Points.Count > 0)
                    {
                        sb.AppendLine(string.Format(inv, L("    記録の温度（{0:F0}〜{1:F0}°C）では、このカーブの Duty は {2:F0}〜{3:F0}%"),
                            t.Min, t.Max, CurveDuty(c.Points, t.Min), CurveDuty(c.Points, t.Max)));
                        // ポンプは水冷の流れを変えるので、回転を下げる提案はしない
                        if (!fan.IsPump && LimitFor(c.SensorId, sensor) is double limit && SuggestCurve(c.Points, t.Max, limit, out double shift) is { } moved)
                        {
                            string newPts = string.Join(" → ", moved.Select(p => string.Format(inv, "{0:F0}°C:{1:F0}%", p.Temp, p.Duty)));
                            double before = CurveDuty(c.Points, t.Max), after = CurveDuty(moved, t.Max);
                            proposals.Add(shift > 0
                                ? string.Format(inv, L("{0} のカーブ「{1}」（{2}）: 最高 {3:F0}°C で目安の上限まで {4:F0}°C の余裕があるので、静かにできます。Duty {5:F0}% 未満の点を {6:F0}°C 右へ → {7}（最高温度のときの Duty {8:F0}% → {9:F0}%）"),
                                    fan.Name, c.Name, sensor, t.Max, limit - t.Max, KeepDuty, shift, newPts, before, after)
                                : string.Format(inv, L("{0} のカーブ「{1}」（{2}）: 最高 {3:F0}°C で目安の上限まで {4:F0}°C しかないので、早めに回します。Duty {5:F0}% 未満の点を {6:F0}°C 左へ → {7}（最高温度のときの Duty {8:F0}% → {9:F0}%）"),
                                    fan.Name, c.Name, sensor, t.Max, limit - t.Max, KeepDuty, -shift, newPts, before, after));
                        }
                    }
                }
            }
            sb.AppendLine(string.Format(inv, L("  - 最低回転数 {0:F0}%、上昇 {1:F1}%/秒、下降 {2:F1}%/秒"), fan.MinDuty, fan.MaxStepUpPerTick, fan.MaxStepDownPerTick));
            if (cal is not null)
                sb.AppendLine(string.Format(inv, L("  - キャリブレーション: 最大 {0:F0} rpm、{1:F0}% 未満で停止、{2:F0}% で始動"), cal.MaxRpm, cal.StopDuty, cal.StartDuty));
        }

        if (notMine.Count > 0)
            sb.AppendLine(L($"（{string.Join(L("、"), notMine)} は BIOS・ドライバーが制御していて、このアプリでは変えられません。提案にも注意点にも書かないでください）"));

        var list = findings.ToList();
        if (list.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L("## 自動診断の結果"));
            foreach (var f in list) sb.AppendLine($"- [{f.Level}] {f.Title}: {f.Detail}");
        }

        if (test is not null)
        {
            sb.AppendLine();
            sb.AppendLine(L("## 直近の冷却テスト"));
            sb.AppendLine(test.ToText());
        }

        var trend = longTerm?.ToList() ?? new();
        if (trend.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L("## 長期の傾向（数週間の記録から。ファンの劣化・ホコリのチェック）"));
            foreach (var t in trend) sb.AppendLine($"- {t}");
        }

        sb.AppendLine();
        sb.AppendLine(L("## アプリが計算した提案（改善案はこの中からだけ）"));
        if (proposals.Count == 0) sb.AppendLine(L("- なし（いまの設定のままで問題ありません）"));
        foreach (var p in proposals) sb.AppendLine("- " + p);

        if (!string.IsNullOrWhiteSpace(userQuestion))
        {
            sb.AppendLine();
            sb.AppendLine(L("## ユーザーからの質問"));
            sb.AppendLine(userQuestion.Trim());
        }
        return sb.ToString();
    }

    /// <summary>
    /// 温度の目安の上限（°C）。これより上でも壊れはしないが、長く続くなら冷やしたい温度。
    /// AI は数字だけでは高い・低いを判断しにくい（GPU Hot Spot 66°C を「高め」と書いた）ので、上限と余裕を添える
    /// </summary>
    public static double? LimitFor(string id, string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("hot spot") || n.Contains("hotspot") || n.Contains("junction")) return 95;
        if (id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase) || n.StartsWith("gpu")) return n.Contains("memory") ? 95 : 83;
        if (id.StartsWith("/nvme", StringComparison.OrdinalIgnoreCase) || id.StartsWith("/hdd", StringComparison.OrdinalIgnoreCase)
            || n.Contains("ssd") || n.Contains("composite")) return 70;
        if (id.Contains("cpu", StringComparison.OrdinalIgnoreCase) || n.StartsWith("cpu")) return 90;
        if (n.Contains("vrm") || n.Contains("mos")) return 100;
        // ケース内・System などマザーボードの温度は、何を測っているかが機種ごとに違うので、目安は付けない
        return null;
    }

    /// <summary>温度の余裕がこれ以上あれば、静かにする方向へずらす（°C）</summary>
    public const double QuietMargin = 20;
    /// <summary>温度の余裕がこれより小さければ、冷やす方向へずらす（°C）</summary>
    public const double TightMargin = 5;
    /// <summary>ずらしたあとも残す余裕（°C）・一度にずらす最大（°C）</summary>
    public const double KeepMargin = 15, MaxShift = 10;
    /// <summary>この Duty 以上の点（全開に近い・緊急用）は動かさない</summary>
    public const double KeepDuty = 80;

    /// <summary>
    /// アプリが決まったルールで作るカーブの提案（AI は数値を自分で作ると、余裕があるのに回転を上げる・同じ提案を繰り返すなどがあったので、
    /// 数値はここで決め、AI には説明だけをしてもらう）。
    /// 余裕が QuietMargin 以上: Duty が KeepDuty 未満の点を、余裕が KeepMargin 残る分まで（最大 MaxShift）右へ＝静かに。
    /// 余裕が TightMargin 未満: 同じ点を 5°C 左へ＝早めに冷やす。そのほかと、ずらしても記録の範囲で 2% も変わらないときは null
    /// </summary>
    public static List<CurvePoint>? SuggestCurve(IReadOnlyList<CurvePoint> points, double sensorMax, double limit, out double shift)
    {
        shift = 0;
        var p = points.OrderBy(x => x.Temp).ToList();
        if (p.Count < 2) return null;
        for (int i = 1; i < p.Count; i++) if (p[i].Duty < p[i - 1].Duty) return null;   // 下がる区間のある変わったカーブは触らない
        double margin = limit - sensorMax;
        if (margin >= QuietMargin) shift = Math.Min(margin - KeepMargin, MaxShift);
        else if (margin < TightMargin) shift = -5;
        else return null;
        // 動かす点が、動かさない点（KeepDuty 以上）を追い越さないようにする
        var low = p.Where(x => x.Duty < KeepDuty).ToList();
        var high = p.FirstOrDefault(x => x.Duty >= KeepDuty);
        if (low.Count == 0) return null;
        if (shift > 0 && high is not null) shift = Math.Min(shift, high.Temp - 2 - low[^1].Temp);
        shift = Math.Round(shift);
        if (Math.Abs(shift) < 1) return null;
        double s = shift;
        var moved = p.Select(x => new CurvePoint { Temp = x.Duty < KeepDuty ? x.Temp + s : x.Temp, Duty = x.Duty }).ToList();
        double before = CurveDuty(p, sensorMax), after = CurveDuty(moved, sensorMax);
        if (Math.Abs(before - after) < 2) return null;   // 記録の範囲ではほとんど変わらない（いちばん下の点のまま、など）
        return moved;
    }

    /// <summary>カーブの点から、その温度での Duty（点の間はまっすぐつなぐ。端より外は端の値）</summary>
    public static double CurveDuty(IReadOnlyList<CurvePoint> points, double temp)
    {
        var p = points.OrderBy(x => x.Temp).ToList();
        if (temp <= p[0].Temp) return p[0].Duty;
        for (int i = 1; i < p.Count; i++)
            if (temp <= p[i].Temp)
                return p[i - 1].Duty + (p[i].Duty - p[i - 1].Duty) * (temp - p[i - 1].Temp) / Math.Max(1e-9, p[i].Temp - p[i - 1].Temp);
        return p[^1].Duty;
    }

    private static string ModeText(FanConfig f) => f.Mode switch
    {
        FanModes.Target => L($"目標温度 {f.TargetTemp:F0}°C"),
        FanModes.Fixed => L($"固定 {f.FixedDuty:F0}%"),
        FanModes.Follow => L("他のファンに連動"),
        _ => L("カーブ"),
    };

    private sealed record SeriesStats(double Min, double Avg, double P95, double Max);

    private static SeriesStats? Stats(HistoryStore h, string key)
    {
        if (!h.Has(key)) return null;
        var values = new List<double>(h.Count);
        for (int i = 0; i < h.Count; i++)
        {
            float v = h.ValueAt(key, i);
            if (!float.IsNaN(v)) values.Add(v);
        }
        if (values.Count == 0) return null;
        values.Sort();
        return new SeriesStats(values[0], values.Average(), values[(int)((values.Count - 1) * 0.95)], values[^1]);
    }
}
