// ==================================================
// XamlLocalizer.cs - XAML に書いた日本語を、画面を作るときに画面の言語へ置き換える
//   XAML はそのまま日本語で書く（見出しは日本語の文そのもの。翻訳表は Core/Loc.cs）。
//   訳す値: TextBlock.Text・Run.Text・Content・Header・ToolTip・Window.Title・TextBox.Tag（入力欄の薄い案内に使っている）。
//   Binding で結び付けた値は訳さない（コードの側で L() を通す）。ただし Binding の StringFormat に日本語があれば訳す。
//   Tag はほかの要素では「選ばれた値」として使っているので訳さない（ファンカーブの「静音」ボタンなど）
//
//   訳すきっかけ:
//   ・ウィンドウが初めて大きさを持ったとき、そのウィンドウの中の要素を全部たどって訳す（隠れているページも含む）
//   ・あとから画面に載った要素（テンプレートから作られる部品・一覧の項目・メニュー・ポップアップ）は、
//     初めて大きさが決まったとき（SizeChanged）に訳す
//   Loaded だけでは足りない: WPF は、自分で Loaded を受け取る要素がない枝には Loaded を送らないため、
//   サイドバーなど多くの要素が日本語のまま残っていた
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace ProFanController.UI;

public static class XamlLocalizer
{
    private static bool _registered;

    /// <summary>
    /// 訳して書き込んだ値（要素・項目ごと）。同じ値をもう一度訳さないため
    /// （中国語の訳が、たまたま別の日本語の見出しと同じ文字だったときに、訳の訳にならないように）
    /// </summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, string>> Written = new();

    /// <summary>中まで訳し終えたウィンドウ</summary>
    private static readonly ConditionalWeakTable<Window, object> TranslatedWindows = new();

    /// <summary>起動時に 1 回呼ぶ。以後に作る画面・部品すべてが対象（元の言葉＝日本語のあいだは何もしない）</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((s, _) => OnShown(s)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => OnShown(s)), handledEventsToo: true);
    }

    private static void OnShown(object sender)
    {
        if (Localization.IsSource || sender is not FrameworkElement fe) return;
        try
        {
            if (fe is Window w)
            {
                if (TranslatedWindows.TryGetValue(w, out _)) { Translate(w); return; }
                TranslatedWindows.Add(w, new object());
                TranslateTree(w);
                return;
            }
            Translate(fe);
        }
        catch (Exception ex)
        {
            // 訳せなくても画面は動かす（日本語のまま出るだけ）
            Logger.Warn($"画面の言葉を訳せません（{fe.GetType().Name}）: {ex.Message}");
        }
    }

    /// <summary>root から中の要素（論理ツリーと見た目のツリーの両方）をすべてたどって訳す</summary>
    public static void TranslateTree(DependencyObject root)
    {
        if (Localization.IsSource) return;
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            switch (d)
            {
                case FrameworkElement fe: Translate(fe); break;
                case Run run: TranslateValue(run, Run.TextProperty); break;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject c) stack.Push(c);
            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }
    }

    /// <summary>1 つの要素の文字を訳す（中の要素は、それぞれが画面に載ったときに訳す）</summary>
    public static void Translate(FrameworkElement e)
    {
        switch (e)
        {
            case TextBlock tb:
                TranslateValue(tb, TextBlock.TextProperty);
                TranslateStringFormat(tb, TextBlock.TextProperty);
                // <Run Text="…"/> を並べた文は、Run ごとに訳す（Text に書いた文・Binding の文は上で扱っている）
                if (DependencyPropertyHelper.GetValueSource(tb, TextBlock.TextProperty).BaseValueSource == BaseValueSource.Default)
                    TranslateInlines(tb.Inlines);
                break;
            case TextBox box:
                TranslateValue(box, FrameworkElement.TagProperty);
                break;
            case Window w:
                TranslateValue(w, Window.TitleProperty);
                break;
        }
        if (e is HeaderedContentControl hc) TranslateValue(hc, HeaderedContentControl.HeaderProperty);
        if (e is HeaderedItemsControl hi) TranslateValue(hi, HeaderedItemsControl.HeaderProperty);
        if (e is ContentControl cc and not Window) TranslateValue(cc, ContentControl.ContentProperty);
        TranslateValue(e, FrameworkElement.ToolTipProperty);

        // 閉じている選択欄の項目（ComboBoxItem など）は、開くまで画面に載らないので、ここで先に訳す
        // （選んでいる項目の文字は、閉じた選択欄にもそのまま表示されるため）
        if (e is ItemsControl ic && ic.ItemsSource is null)
            foreach (var item in ic.Items)
                if (item is FrameworkElement child && !child.IsLoaded) Translate(child);
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) TranslateValue(run, Run.TextProperty);
            else if (inline is Span span) TranslateInlines(span.Inlines);
        }
    }

    /// <summary>文字の値（Binding・リソースなどの式でないもの）を訳す</summary>
    private static void TranslateValue(DependencyObject d, DependencyProperty p)
    {
        if (d.GetValue(p) is not string s || !HasNonAscii(s)) return;
        if (Written.TryGetValue(d, out var written) && written.TryGetValue(p, out var mine) && mine == s) return;
        var source = DependencyPropertyHelper.GetValueSource(d, p);
        if (source.IsExpression || source.IsAnimated) return;
        string t = L(s);
        if (t == s) return;
        d.SetCurrentValue(p, t);
        Written.GetOrCreateValue(d)[p] = t;
    }

    /// <summary>{Binding …, StringFormat={}{0:F1} 倍} のような、日本語の入った書式を訳す（Binding を作り直す）</summary>
    private static void TranslateStringFormat(FrameworkElement e, DependencyProperty p)
    {
        if (BindingOperations.GetBinding(e, p) is not Binding b || b.StringFormat is not { } fmt || !HasNonAscii(fmt)) return;
        string t = L(fmt);
        if (t == fmt) return;
        var copy = new Binding
        {
            Path = b.Path,
            Mode = b.Mode,
            Converter = b.Converter,
            ConverterParameter = b.ConverterParameter,
            ConverterCulture = b.ConverterCulture,
            StringFormat = t,
            FallbackValue = b.FallbackValue,
            TargetNullValue = b.TargetNullValue,
            UpdateSourceTrigger = b.UpdateSourceTrigger,
        };
        if (b.Source is not null) copy.Source = b.Source;
        else if (b.RelativeSource is not null) copy.RelativeSource = b.RelativeSource;
        else if (!string.IsNullOrEmpty(b.ElementName)) copy.ElementName = b.ElementName;
        e.SetBinding(p, copy);
    }

    /// <summary>
    /// 画面に残っている日本語（かなを含む文字）を集める（言語を切り替えたあとの確認用。ログに出して、訳し漏れを見つける）。
    /// 入力欄・Binding の値（プロファイル名など、利用者が付けた名前）は数えない
    /// </summary>
    /// <param name="visibleOnly">
    /// 画面に出ている部品だけを見る（隠れている部品は、初めて表示されたときに訳すので、まだ日本語のままでよい）
    /// </param>
    public static List<string> FindUntranslated(DependencyObject root, int max = 20, bool visibleOnly = false)
    {
        var found = new List<string>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0 && found.Count < max)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            if (visibleOnly && d is UIElement { IsVisible: false }) continue;
            void Check(DependencyObject o, DependencyProperty p)
            {
                if (o.GetValue(p) is string s && HasKana(s) && !DependencyPropertyHelper.GetValueSource(o, p).IsExpression)
                    found.Add($"{o.GetType().Name}.{p.Name}: {(s.Length > 60 ? s[..60] + "…" : s)}");
            }
            switch (d)
            {
                case TextBlock tb when tb.TemplatedParent is not TextBox: Check(tb, TextBlock.TextProperty); break;
                case HeaderedContentControl hc: Check(hc, HeaderedContentControl.HeaderProperty); Check(hc, ContentControl.ContentProperty); break;
                case HeaderedItemsControl hi: Check(hi, HeaderedItemsControl.HeaderProperty); break;
                case ContentControl cc and not Window: Check(cc, ContentControl.ContentProperty); break;
            }
            if (d is FrameworkElement fe) Check(fe, FrameworkElement.ToolTipProperty);
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject c) stack.Push(c);
            if (d is Visual)
            {
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }
        return found;
    }

    /// <summary>ひらがな・カタカナを含むか（「・」「ー」は中国語などの訳でも区切りに使うので数えない）</summary>
    private static bool HasKana(string s)
    {
        foreach (char c in s)
            if (c is >= '぀' and <= 'ヺ') return true;
        return false;
    }

    private static bool HasNonAscii(string s)
    {
        foreach (char c in s)
            if (c > 0x7F) return true;
        return false;
    }
}
