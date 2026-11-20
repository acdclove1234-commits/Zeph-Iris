// ==================================================
// LightingHistory.cs - ライティングの「元に戻す・やり直す」
//   ファンの設定の履歴（AppController）とは別に持つ。LED のスライダーを少し動かすたびに、
//   ファンの設定の履歴が押し出されないようにするため（AppController は LED だけの変更を履歴に入れない）。
//   変更が落ち着いたところ（保存のたび）で Checkpoint を呼ぶと、前の状態を 1 段として積む。
// ==================================================
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProFanController;

public sealed class LightingHistory
{
    public const int Limit = 50;

    /// <summary>
    /// 元に戻しても変えない値: 連携のオン・オフ・消灯・接続先・グラボへの直接書き込み（光り方ではなく、つながり方の設定）と、
    /// 「激しい点滅を抑える」（光に敏感な方のための確認を通さずに、オフへ戻さないため）
    /// </summary>
    public static readonly string[] KeptRgb =
    {
        nameof(RgbConfig.Enabled), nameof(RgbConfig.LightsOff), nameof(RgbConfig.Host), nameof(RgbConfig.Port),
        nameof(RgbConfig.DirectGpuWrite), nameof(RgbConfig.DirectGpuUnverified),
    };
    public static readonly string[] KeptMusic = { nameof(MusicConfig.ReduceFlashing) };

    private static readonly PropertyInfo[] RgbScalars = Scalars(typeof(RgbConfig), KeptRgb);
    private static readonly PropertyInfo[] MusicScalars = Scalars(typeof(MusicConfig), KeptMusic);

    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string _committed;

    public LightingHistory(RgbConfig cfg) => _committed = Take(cfg);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>今の状態を「確定した状態」にする（前と違えば、前の状態を元に戻す用に積む）。積んだら true</summary>
    public bool Checkpoint(RgbConfig cfg)
    {
        string now = Take(cfg);
        if (now == _committed) return false;
        _undo.Add(_committed);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
        _committed = now;
        return true;
    }

    /// <summary>ひとつ前の状態に戻す。デバイスの一覧（デバイスごとの設定など）も入れ替えたら listsChanged = true</summary>
    public bool Undo(RgbConfig cfg, out bool listsChanged) => Step(cfg, _undo, _redo, out listsChanged);

    public bool Redo(RgbConfig cfg, out bool listsChanged) => Step(cfg, _redo, _undo, out listsChanged);

    private bool Step(RgbConfig cfg, List<string> from, List<string> to, out bool listsChanged)
    {
        listsChanged = false;
        Checkpoint(cfg); // まだ確定していない変更も、1 段として扱う
        if (from.Count == 0) return false;
        string target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(_committed);
        listsChanged = Apply(cfg, target);
        // 戻した結果をそのまま確定した状態にする（決まりごとで値が少し変わっても、新しい 1 段にしない）
        _committed = Take(cfg);
        return true;
    }

    /// <summary>覚えておく状態（戻さない値を除いた、LED の設定の JSON）</summary>
    internal static string Take(RgbConfig cfg)
    {
        var node = JsonSerializer.SerializeToNode(cfg)!.AsObject();
        foreach (var k in KeptRgb) node.Remove(k);
        if (node[nameof(RgbConfig.Music)] is JsonObject music)
            foreach (var k in KeptMusic) music.Remove(k);
        return node.ToJsonString();
    }

    /// <summary>覚えておいた状態を、今の設定のオブジェクトに書き込む（オブジェクトは入れ替えない。画面の結び付けを保つため）</summary>
    private static bool Apply(RgbConfig cfg, string json)
    {
        var src = JsonSerializer.Deserialize<RgbConfig>(json) ?? new RgbConfig();
        src.Music ??= new MusicConfig();
        cfg.Music ??= new MusicConfig();

        foreach (var p in RgbScalars) Copy(p, src, cfg);
        // プリセットの名前は最後に戻す（値を書き換えている途中で「カスタム」にされないように）
        foreach (var p in MusicScalars.Where(p => p.Name != nameof(MusicConfig.Preset))) Copy(p, src.Music, cfg.Music);
        cfg.Music.Preset = src.Music.Preset;

        bool changed = false;
        if (Json(cfg.Devices) != Json(src.Devices)) { cfg.Devices = src.Devices ?? new(); changed = true; }
        if (Json(cfg.SwapRedGreen) != Json(src.SwapRedGreen)) { cfg.SwapRedGreen = src.SwapRedGreen ?? new(); changed = true; }
        if (Json(cfg.DeviceSettings) != Json(src.DeviceSettings)) { cfg.DeviceSettings = src.DeviceSettings ?? new(); changed = true; }
        return changed;
    }

    private static void Copy(PropertyInfo p, object src, object dst)
    {
        object? v = p.GetValue(src);
        if (!Equals(v, p.GetValue(dst))) p.SetValue(dst, v);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>そのまま書き写せる値（数値・文字列・真偽値）の設定項目</summary>
    private static PropertyInfo[] Scalars(Type t, string[] kept) => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !kept.Contains(p.Name))
        .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType.IsEnum
                    || p.PropertyType == typeof(double) || p.PropertyType == typeof(decimal))
        .ToArray();
}
