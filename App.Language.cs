// ==================================================
// App.Language.cs - 表示言語の切り替え（アプリを止めずに、画面・タスクトレイのメニュー・ミニウィジェットを作り直す）
//   以前は「起動し直しますか」と聞いてアプリを起動し直していた（そのあいだファンは BIOS の制御になり、
//   「いいえ」を押すと、同じ言語を選び直しても切り替えられなかった）
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ProFanController.UI;

namespace ProFanController;

public partial class App
{
    /// <summary>
    /// 設定の「表示言語」（保存済み）を、いますぐ当てる。翻訳表を読み直し、メイン画面を新しい言語で作り直して、
    /// 同じページ・同じ位置で開く。ファンの制御・LED・音の取り込みは止めない。
    /// 新しい言語が今と同じなら何もしない
    /// </summary>
    public void SwitchLanguage()
    {
        var old = _window;
        if (old is null || _controller is null) return;
        string before = Localization.Language;
        Localization.Init(_controller.Config.Language);
        if (Localization.Language == before) return;
        Logger.Info($"表示言語を切り替えます: {before} → {Localization.Language}（画面を作り直します）");

        try
        {
            // ホットキーは前の画面のウィンドウに登録しているので、いったん外す（新しい画面に登録し直す）
            ReleaseHotkeys(old);
            var carry = old.DetachForReload();
            // フォントの並び（中国語・韓国語・タイ語は、その言語のフォントを先に探す）も言語で変わる
            ThemeManager.Apply(_controller.Config.Appearance);
            var window = CreateMainWindow(_controller, carry);
            window.ResumeFrom(carry);
            old.AllowClose = true;
            old.Close();

            _tray?.Relabel();
            _tray?.SetPaused(_paused);
            _tray?.SetWidgetVisible(_widget is not null);
            UpdateTrayProfiles();
            UpdateQuickStatus();
            window.SetWidgetSwitch(_widget is not null);
            if (_widget is not null)
            {
                // ミニウィジェットは、新しい画面の一覧を見て開き直す（位置・透明度は設定のまま）
                CloseWidget(save: false);
                ShowWidget();
            }
            UpdateHotkeys();

            // 訳し漏れの確認: 新しい画面に日本語が残っていれば、ログに書く（直すときの手がかり）
            if (!Localization.IsSource)
                window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    var left = XamlLocalizer.FindUntranslated(window);
                    if (left.Count > 0) Logger.Info($"表示言語 {Localization.Language}: 訳されずに残っている文字 {left.Count} 件以上: {string.Join(" / ", left)}");
                });
        }
        catch (Exception ex)
        {
            // 作り直せなくても制御は続ける（次に起動したときは新しい言語になる）。起動し直すかを尋ねる
            Logger.Error($"表示言語の切り替えで画面を作り直せませんでした: {ex}");
            if (MessageBox.Show(L($"表示言語を切り替えられませんでした（{ex.Message}）。ファンの制御はそのまま続いています。アプリを起動し直すと、選んだ言語で表示されます。いま起動し直しますか？"),
                    AppIdentity.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                RestartApplication();
        }
    }

    /// <summary>前の画面のウィンドウに登録したホットキーを外す</summary>
    private void ReleaseHotkeys(MainWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (_hotkeysRegistered && handle != IntPtr.Zero)
            foreach (var h in Hotkeys) UnregisterHotKey(handle, h.Id);
        _hotkeysRegistered = false;
        _hotkeySource?.RemoveHook(HotkeyHook);
        _hotkeySource = null;
    }
}
