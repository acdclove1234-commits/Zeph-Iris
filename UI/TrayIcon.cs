// ==================================================
// TrayIcon.cs - タスクトレイ常駐 (アイコン・プロファイル切替メニュー・LED の光り方メニュー・通知バルーン)
// Dependencies: Windows Forms NotifyIcon / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ProFanController.UI;

public sealed class TrayIcon : IDisposable
{
    /// <summary>「不具合を報告…」のときに command へ渡す名前（App がトレイからのときだけ受け付ける）</summary>
    public const string ReportCommand = "report-problem";

    private readonly WinForms.NotifyIcon _icon;
    private readonly Drawing.Icon _appIcon;
    private readonly Action _open, _togglePause, _toggleWidget, _exit;
    private readonly Action<string> _selectProfile;
    private readonly Action<string> _command;
    // メニューの項目（表示言語を切り替えたら、メニューごと作り直す）
    private WinForms.ToolStripMenuItem _pauseItem = null!;
    private WinForms.ToolStripMenuItem _widgetItem = null!;
    private WinForms.ToolStripMenuItem _profileMenu = null!;
    private WinForms.ToolStripMenuItem _ledMenu = null!;
    private WinForms.ToolStripMenuItem _musicMenu = null!;
    private WinForms.ToolStripMenuItem _ledToggle = null!;
    private WinForms.ToolStripMenuItem _lightsOffItem = null!;
    /// <summary>「LED の光り方」の各項目と、その光り方（チェックを付けるため）</summary>
    private readonly List<(WinForms.ToolStripMenuItem Item, string Mode)> _ledModeItems = new();
    /// <summary>「音楽連動の光り方」の各項目と、その光り方</summary>
    private readonly List<(WinForms.ToolStripMenuItem Item, string Style)> _musicItems = new();

    /// <summary>
    /// いまの LED の設定を返す（メニューを開くたびに読んで、選ばれている項目にチェックを付ける）。
    /// 設定しなければ、メイン画面が表示している設定を読む
    /// </summary>
    public Func<RgbConfig?>? LightingState { get; set; }

    public TrayIcon(Action open, Action togglePause, Action toggleWidget, Action<string> selectProfile, Action<string> command, Action exit)
    {
        _appIcon = CreateAppIcon();
        _open = open;
        _togglePause = togglePause;
        _toggleWidget = toggleWidget;
        _exit = exit;
        _selectProfile = selectProfile;
        _command = command;

        _icon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            Text = AppIdentity.Name,
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _open();
        };
    }

    /// <summary>
    /// 表示言語を切り替えたとき: メニューを今の言語で作り直す（プロファイル・一時停止・ウィジェットの表示は、
    /// 呼んだ側が SetProfiles などで入れ直す）
    /// </summary>
    public void Relabel()
    {
        var old = _icon.ContextMenuStrip;
        _ledModeItems.Clear();
        _musicItems.Clear();
        _icon.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        Action open = _open, togglePause = _togglePause, toggleWidget = _toggleWidget, exit = _exit;
        Action<string> command = _command;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem(L("Zeph-Iris を開く"), null, (_, _) => open()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) });
        _profileMenu = new WinForms.ToolStripMenuItem(L("プロファイル"));
        menu.Items.Add(_profileMenu);
        var quick = new WinForms.ToolStripMenuItem(L("ワンタッチ操作"));
        foreach (var (text, cmd) in new[]
        {
            (L("ブースト（全開）5 分"), "boost:5"), (L("ブースト（全開）15 分"), "boost:15"),
            (L("静音 30 分"), "quiet:30"), (L("静音 1 時間"), "quiet:60"), (L("静音 2 時間"), "quiet:120"),
            (L("解除"), "cancel"),
        })
        {
            quick.DropDownItems.Add(new WinForms.ToolStripMenuItem(text, null, (_, _) => command(cmd)));
        }
        menu.Items.Add(quick);
        // LED: いまの光り方にチェックを付ける（メニューを開くたびに RefreshLightingMenu で更新）。
        // 選んだときは画面と同じ入口（App.ExecuteCommand）を通すので、「元に戻す」の履歴には残らない
        _ledMenu = new WinForms.ToolStripMenuItem(L("LED の光り方"));
        foreach (var mode in RgbModes.All)
        {
            var item = new WinForms.ToolStripMenuItem(ModeLabel(mode), null, (_, _) => command($"led:{mode}"));
            _ledModeItems.Add((item, mode));
            _ledMenu.DropDownItems.Add(item);
        }
        _ledMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _ledToggle = new WinForms.ToolStripMenuItem(L("LED 連携"), null, (_, _) => ToggleLighting());
        _ledMenu.DropDownItems.Add(_ledToggle);
        // 消灯: 連携は続けたまま黒にする（連携をオフにすると、機器が自分の光り方（レインボーなど）に戻ってしまうため）
        _lightsOffItem = new WinForms.ToolStripMenuItem(L("消灯（制御は続ける）"), null, (_, _) => _command(CurrentLighting()?.LightsOff == true ? "lights:on" : "lights:off"))
        {
            ToolTipText = L("LED をすべて消します。連携は続けるので、機器が自分の光り方に戻りません"),
        };
        _ledMenu.DropDownItems.Add(_lightsOffItem);
        _ledMenu.DropDown.ShowItemToolTips = true;
        menu.Items.Add(_ledMenu);

        _musicMenu = new WinForms.ToolStripMenuItem(L("音楽連動の光り方"));
        foreach (var style in MusicStyles.All)
        {
            var (label, description) = MusicTuning.Describe(style);
            var item = new WinForms.ToolStripMenuItem(label, null, (_, _) => command($"music:{style}")) { ToolTipText = description };
            _musicItems.Add((item, style));
            _musicMenu.DropDownItems.Add(item);
        }
        _musicMenu.DropDown.ShowItemToolTips = true; // 項目に乗せたとき説明を出す
        menu.Items.Add(_musicMenu);
        menu.Opening += (_, _) => RefreshLightingMenu();
        _widgetItem = new WinForms.ToolStripMenuItem(L("ミニウィジェットを表示"), null, (_, _) => toggleWidget());
        menu.Items.Add(_widgetItem);
        _pauseItem = new WinForms.ToolStripMenuItem(L("一時停止（BIOS に戻す）"), null, (_, _) => togglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem(L("不具合を報告…"), null, (_, _) => command(ReportCommand)));
        menu.Items.Add(new WinForms.ToolStripMenuItem(L("終了（BIOS 制御に戻す）"), null, (_, _) => exit()));
        return menu;
    }

    /// <summary>光り方の表示名（画面の「光り方」と同じ言葉）</summary>
    private static string ModeLabel(string mode) => RgbModes.Label(mode);

    /// <summary>いまの LED の設定（LightingState がなければ、メイン画面から読む）</summary>
    private RgbConfig? CurrentLighting()
    {
        try
        {
            if (LightingState is { } state) return state();
            if (System.Windows.Application.Current is { } app)
            {
                foreach (var w in app.Windows)
                {
                    if (w is MainWindow main) return main.LightingConfig;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"トレイ: LED の設定を読めません: {ex.Message}");
        }
        return null;
    }

    /// <summary>メニューを開く直前に、いまの光り方・オンオフにチェックを付ける</summary>
    private void RefreshLightingMenu()
    {
        var cfg = CurrentLighting();
        bool enabled = cfg?.Enabled == true;
        foreach (var (item, mode) in _ledModeItems) item.Checked = enabled && cfg!.Mode == mode;
        _ledToggle.Checked = enabled;
        _ledToggle.Text = enabled ? L("LED 連携をオフにする") : L("LED 連携をオンにする");
        _lightsOffItem.Checked = enabled && cfg!.LightsOff;
        _lightsOffItem.Enabled = enabled;
        _ledMenu.Text = cfg is null ? L("LED の光り方") : !enabled ? L("LED の光り方: オフ") : cfg.LightsOff ? L("LED の光り方: 消灯中") : L($"LED の光り方: {ModeLabel(cfg.Mode)}");

        bool music = enabled && cfg!.Mode == RgbModes.Music;
        bool reduceFlashing = cfg?.Music?.ReduceFlashing ?? true;
        foreach (var (item, style) in _musicItems)
        {
            item.Checked = music && cfg!.MusicStyle == style;
            // ストロボは「激しい点滅を抑える」をオフにしたときだけ選べる（画面の設定と同じ）
            if (style == MusicStyles.Strobe)
            {
                item.Enabled = !reduceFlashing;
                item.ToolTipText = reduceFlashing
                    ? L("激しく点滅します。ライティング画面で「激しい点滅を抑える」をオフにすると選べます")
                    : MusicTuning.Describe(style).Description;
            }
        }
    }

    /// <summary>LED 連携のオン・オフを切り替える（押した時点の設定を読み直してから決める）</summary>
    private void ToggleLighting()
        => _command(CurrentLighting()?.Enabled == true ? "led:off" : "led:on");

    public void SetPaused(bool paused)
        => _pauseItem.Text = paused ? L("制御を再開する") : L("一時停止（BIOS に戻す）");

    public void SetWidgetVisible(bool visible) => _widgetItem.Checked = visible;

    /// <summary>プロファイル一覧を作り直す。reason は普段と違うプロファイルを使っている理由（例: 自動: game）</summary>
    public void SetProfiles(IEnumerable<string> names, string manual, string effective, string? reason)
    {
        _profileMenu.DropDownItems.Clear();
        foreach (var name in names)
        {
            var item = new WinForms.ToolStripMenuItem(name, null, (_, _) => _selectProfile(name)) { Checked = name == manual };
            if (reason is not null && name == effective && name != manual) item.Text += L($"（{reason}）");
            _profileMenu.DropDownItems.Add(item);
        }
        _profileMenu.Text = L($"プロファイル: {effective}");
    }

    /// <summary>マウスを乗せたときの表示（Windows の制限で 127 文字まで）</summary>
    public void SetTooltip(string text)
        => _icon.Text = text.Length > 120 ? text[..120] : text;

    public void ShowBalloon(string title, string message, AlertLevel level)
    {
        var icon = level switch
        {
            AlertLevel.Danger => WinForms.ToolTipIcon.Error,
            AlertLevel.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5000, title, message, icon);
    }

    /// <summary>
    /// ウィンドウ（タイトルバー・Alt+Tab）用に、exe と同じアイコン（Assets/app.ico）を返す。
    /// ICO のまま渡すと、WPF が大きさごとに合う絵（32 px 以下はファンの台・40 px 以上は全体の絵）を選ぶ
    /// </summary>
    public System.Windows.Media.ImageSource CreateWindowIcon()
        => BitmapFrame.Create(AppIconStream(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

    /// <summary>exe に埋め込んだアイコン（Assets/app.ico。exe のアイコン・インストーラーと同じもの）</summary>
    private static Stream AppIconStream()
        => typeof(TrayIcon).Assembly.GetManifestResourceStream("ProFanController.AppIcon.ico")
           ?? throw new FileNotFoundException("AppIcon.ico");

    /// <summary>通知領域のアイコン: 画面の拡大率に合う小さいアイコンの大きさ（16〜32 px）の絵を、ICO から選ぶ</summary>
    private static Drawing.Icon CreateAppIcon()
    {
        using var s = AppIconStream();
        return new Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
