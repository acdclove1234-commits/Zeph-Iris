// ==================================================
// AppController.cs - 設定・エンジン・プロファイル切り替えのまとめ役
//   (保存と元に戻す/やり直し・自動切り替え・スケジュール・ワンタッチ操作・電源プラン)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 画面はこのクラスの Config を直接編集し、編集が落ち着いたら Commit() を呼ぶ。
/// 実際に使うプロファイルは「ワンタッチ静音 ＞ アプリの自動切り替え ＞ スケジュール ＞ 普段使う」の優先順で決まる。
/// UI スレッドからだけ呼ぶこと。
/// </summary>
public sealed class AppController : IDisposable
{
    private const int UndoLimit = 50;

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _committedJson;
    /// <summary>LED の設定を除いた、エンジンに渡した設定（LED だけの変更でエンジンに渡し直さないため）</summary>
    private string _committedEngineJson = "";
    private static readonly RgbConfig EmptyRgb = new();

    /// <summary>
    /// 「元に戻す」で使う昔の設定に、今の LED の設定を入れる（LED の変更は履歴に入れていないので、
    /// ファンの設定を戻したときに LED まで昔に戻らないように）
    /// </summary>
    private string KeepCurrentLighting(string json)
    {
        var old = AppConfig.FromJson(json);
        old.Rgb = AppConfig.FromJson(Config.ToJson()).Rgb;
        return old.ToJson();
    }

    /// <summary>LED の設定を除いた設定の JSON</summary>
    private string EngineJson()
    {
        var rgb = Config.Rgb;
        Config.Rgb = EmptyRgb;
        try { return Config.ToJson(); }
        finally { Config.Rgb = rgb; }
    }
    private string? _autoProfile;
    private string? _scheduleProfile;
    private string? _quietProfile;
    private DateTime? _quietUntil;
    private DateTime? _boostUntil;
    private string? _appliedPowerPlanGuid;
    private string? _originalPowerPlan;
    private bool _powerPlanChanged;

    public FanEngine Engine { get; } = new();
    public ProfileSwitcher Switcher { get; } = new();
    public AppConfig Config { get; private set; }

    /// <summary>自動切り替えで一致しているアプリ名（なければ null）</summary>
    public string? AutoProcess { get; private set; }

    /// <summary>設定オブジェクトが丸ごと入れ替わった（元に戻す・ウィザード）。画面は再バインドが必要</summary>
    public event Action? ConfigReplaced;
    /// <summary>保存が完了した（プロファイル一覧などの再表示用）</summary>
    public event Action? Committed;
    /// <summary>実際に使っているプロファイルが変わった</summary>
    public event Action? EffectiveProfileChanged;
    /// <summary>ワンタッチ操作（ブースト・静音）の状態が変わった</summary>
    public event Action? QuickChanged;

    public AppController(AppConfig config)
    {
        Config = config;
        _committedJson = config.ToJson();
        _committedEngineJson = EngineJson();
    }

    /// <summary>今エンジンが使っているプロファイル</summary>
    public string EffectiveProfile
    {
        get
        {
            foreach (var candidate in new[] { _quietProfile, _autoProfile, _scheduleProfile })
            {
                if (candidate is not null && Config.FindProfile(candidate) is not null) return candidate;
            }
            return Config.ActiveProfile;
        }
    }

    /// <summary>なぜそのプロファイルなのか（画面表示用）。普段使うプロファイルのときは null</summary>
    public string? EffectiveReason
    {
        get
        {
            string p = EffectiveProfile;
            if (p == Config.ActiveProfile) return null;
            if (p == _quietProfile) return L("ワンタッチ静音");
            if (p == _autoProfile) return L($"自動: {AutoProcess}");
            if (p == _scheduleProfile) return L("スケジュール");
            return null;
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public DateTime? BoostUntil => _boostUntil;
    public DateTime? QuietUntil => _quietUntil;

    /// <summary>「ブースト中（残り 4:12）」のような表示。何もしていなければ null</summary>
    public string? QuickStatusText
    {
        get
        {
            static string Left(DateTime until)
            {
                var t = until - DateTime.Now;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            }
            if (_boostUntil is DateTime b) return L($"ブースト中（残り {Left(b)}）");
            if (_quietUntil is DateTime q) return L($"静音モード中（残り {Left(q)}）");
            return null;
        }
    }

    public void Start()
    {
        _originalPowerPlan = PowerPlans.ActiveGuid();
        Switcher.UpdateRules(Config.AutoSwitchRules);
        _scheduleProfile = MatchSchedule(DateTime.Now);
        Engine.Start(Config, EffectiveProfile);
        Switcher.Start();
        ApplyPowerPlan();
    }

    /// <summary>1 秒ごとに呼ぶ。ワンタッチ操作の期限切れとスケジュールを確認する</summary>
    public void Tick(DateTime now)
    {
        RetryUnsaved(now);
        string before = EffectiveProfile;
        bool quickChanged = false;
        if (_boostUntil is DateTime b && now >= b) { _boostUntil = null; quickChanged = true; }
        if (_quietUntil is DateTime q && now >= q) { _quietUntil = null; _quietProfile = null; quickChanged = true; }
        _scheduleProfile = MatchSchedule(now);
        Reconcile(before, engineApplied: false);
        if (quickChanged) QuickChanged?.Invoke();
    }

    private string? MatchSchedule(DateTime now)
        => Config.ScheduleRules.FirstOrDefault(r => r.Matches(now) && Config.FindProfile(r.ProfileName) is not null)?.ProfileName;

    // ----- ワンタッチ操作 -----

    public void StartBoost(TimeSpan duration)
    {
        _boostUntil = DateTime.Now + duration;
        Engine.SetBoost(_boostUntil);
        QuickChanged?.Invoke();
    }

    /// <summary>静音プロファイルを一時的に使う。静音プロファイルがなければ false</summary>
    public bool StartQuiet(TimeSpan duration)
    {
        string? quiet = Config.FindProfile(Config.Quick.QuietProfile)?.Name
                        ?? Config.Profiles.FirstOrDefault(p => p.Role == ProfileRoles.Silent)?.Name;
        if (quiet is null) return false;

        string before = EffectiveProfile;
        CancelBoostSilently();
        _quietProfile = quiet;
        _quietUntil = DateTime.Now + duration;
        Reconcile(before, engineApplied: false);
        QuickChanged?.Invoke();
        return true;
    }

    public void CancelQuick()
    {
        string before = EffectiveProfile;
        CancelBoostSilently();
        _quietProfile = null;
        _quietUntil = null;
        Reconcile(before, engineApplied: false);
        QuickChanged?.Invoke();
    }

    private void CancelBoostSilently()
    {
        if (_boostUntil is null) return;
        _boostUntil = null;
        Engine.SetBoost(null);
    }

    // ----- 設定の保存と元に戻す -----

    /// <summary>編集内容を保存してエンジンに反映する。変更がなければ何もしない</summary>
    public void Commit(bool recordUndo = true)
    {
        string json = Config.ToJson();
        if (json == _committedJson) return;
        // LED の設定だけが変わったときは、ファン制御のエンジンに設定を渡し直さない（LED は App がすぐ反映している）
        string engineJson = EngineJson();
        bool engineChanged = engineJson != _committedEngineJson;
        _committedEngineJson = engineJson;

        // LED の設定だけの変更は「元に戻す」の履歴に入れない（スライダーを少し動かすたびにファンの設定の履歴が押し出されないように）
        if (recordUndo && engineChanged)
        {
            _undo.Push(_committedJson);
            TrimUndo();
            _redo.Clear();
        }
        string before = EffectiveProfile;
        _committedJson = json;
        Persist(json);
        Switcher.UpdateRules(Config.AutoSwitchRules);
        _scheduleProfile = MatchSchedule(DateTime.Now);
        if (engineChanged) Engine.ApplyConfig(Config.Clone(), EffectiveProfile);
        Committed?.Invoke();
        Reconcile(before, engineApplied: engineChanged);
        ApplyPowerPlan(); // プロファイルが同じでも、電源プランの設定だけ変えた場合に反映する
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(_committedJson);
        Replace(KeepCurrentLighting(_undo.Pop()));
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(_committedJson);
        Replace(KeepCurrentLighting(_redo.Pop()));
    }

    /// <summary>ウィザードなどで作った新しい設定に丸ごと置き換える（元に戻せる）</summary>
    public void ReplaceConfig(AppConfig newConfig)
    {
        _undo.Push(_committedJson);
        TrimUndo();
        _redo.Clear();
        Replace(newConfig.ToJson());
    }

    public void SetActiveProfile(string name)
    {
        if (Config.FindProfile(name) is null) return;
        string before = EffectiveProfile;
        Config.ActiveProfile = name;
        Commit(recordUndo: false);
        Reconcile(before, engineApplied: true);
        EffectiveProfileChanged?.Invoke();
    }

    /// <summary>ProfileSwitcher の通知を UI スレッドに移してから呼ぶ</summary>
    public void SetAutoMatch(string? process, string? profile)
    {
        string before = EffectiveProfile;
        AutoProcess = process;
        _autoProfile = profile;
        Logger.Info(process is null ? "自動切り替え: 一致なし" : $"自動切り替え: {process} → {profile}");
        Reconcile(before, engineApplied: false);
        EffectiveProfileChanged?.Invoke();
    }

    /// <summary>実際に使うプロファイルが変わっていたら、エンジンと電源プランに反映する</summary>
    private void Reconcile(string before, bool engineApplied)
    {
        if (before == EffectiveProfile) return;
        if (!engineApplied) Engine.ApplyConfig(Config.Clone(), EffectiveProfile);
        ApplyPowerPlan();
        EffectiveProfileChanged?.Invoke();
    }

    private void ApplyPowerPlan()
    {
        // 「変更しない」のプロファイルでは、今の電源プランをそのまま使う
        string guid = Config.FindProfile(EffectiveProfile)?.PowerPlanGuid ?? "";
        if (guid.Length == 0 || guid == _appliedPowerPlanGuid) return;
        _appliedPowerPlanGuid = guid;
        _powerPlanChanged = true;
        // powercfg は少し時間がかかるので、画面を止めないよう別スレッドで実行する。切り替えられなければ知らせる
        string profile = EffectiveProfile;
        Task.Run(() =>
        {
            if (PowerPlans.SetActive(guid)) return;
            if (_appliedPowerPlanGuid == guid) _appliedPowerPlanGuid = null;
            PowerPlanFailed?.Invoke(profile, guid);
        });
    }

    /// <summary>電源プランを切り替えられなかった（プロファイルの名前・プランの GUID）。別のスレッドで呼ばれる</summary>
    public event Action<string, string>? PowerPlanFailed;

    private void Replace(string json)
    {
        string before = EffectiveProfile;
        Config = AppConfig.FromJson(json);
        _committedJson = Config.ToJson();
        _committedEngineJson = EngineJson();
        Persist(_committedJson);
        Switcher.UpdateRules(Config.AutoSwitchRules);
        Engine.ApplyConfig(Config.Clone(), EffectiveProfile);
        ConfigReplaced?.Invoke();
        Committed?.Invoke();
        Reconcile(before, engineApplied: true);
    }

    private void TrimUndo()
    {
        if (_undo.Count <= UndoLimit) return;
        var keep = _undo.Take(UndoLimit).Reverse().ToList();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }

    /// <summary>設定を config.json に書けなかった（書けなくなった最初の 1 回だけ。理由の文）。画面のスレッドで呼ばれる</summary>
    public event Action<string>? SaveFailed;
    /// <summary>書けなかった設定を、あとで書けた</summary>
    public event Action? SaveRecovered;
    /// <summary>まだ書けていない設定（書けたら null）。Tick で 30 秒ごとに書き直す</summary>
    private string? _unsavedJson;
    private DateTime _lastSaveRetry;

    private void Persist(string json)
    {
        try
        {
            ConfigStore.SaveJson(json);
            if (_unsavedJson is not null)
            {
                _unsavedJson = null;
                Logger.Info("書けなかった設定を保存できました");
                SaveRecovered?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"設定の保存に失敗: {ex}");
            bool first = _unsavedJson is null;
            _unsavedJson = json;
            _lastSaveRetry = DateTime.Now;
            if (first) SaveFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>書けなかった設定を、30 秒ごとに書き直す（Tick から）</summary>
    private void RetryUnsaved(DateTime now)
    {
        if (_unsavedJson is not { } json || now - _lastSaveRetry < TimeSpan.FromSeconds(30)) return;
        _lastSaveRetry = now;
        Persist(json);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_unsavedJson is { } unsaved) Persist(unsaved);   // 書けなかった設定を、終わる前にもう一度書く
        Switcher.Dispose();
        Engine.Stop();
        // 起動時の電源プランに戻す（このアプリが切り替えた場合のみ）
        if (_powerPlanChanged && Config.RestorePowerPlanOnExit && _originalPowerPlan is not null)
        {
            PowerPlans.SetActive(_originalPowerPlan);
        }
    }
}
