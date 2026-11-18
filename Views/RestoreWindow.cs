using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Storage;

namespace ClipCompose.Views;

/// <summary>退避ファイルの一覧の行。<see cref="ToString"/> は読み上げ用（種類と日時）。</summary>
public sealed class SideCopyRow(SideCopy copy)
{
    public SideCopy Copy { get; } = copy;

    public string Text => RestoreWindow.Describe(Copy);

    public override string ToString() => Text;
}

/// <summary>
/// ［以前の状態に戻す…］の一覧（仕様書 §11・§17.9）。退避ファイルを新しい順に並べ、種類と日時（ファイル名から）を出す。
/// 開いたときはファイルの中身を読まず、行を選んだときだけ全件検証して件数を出す（結果は開いている間覚える）。
/// 検証に失敗した行は理由を出して戻せない。［戻す］は検証済みの内容を渡す（ファイルを読み直さない）。
/// </summary>
public sealed class RestoreWindow : Window
{
    private readonly Func<string, ParsedLibrary> _read;
    private readonly Func<Window, SideCopy, TemplateLibrary, bool> _restore;
    private readonly Dictionary<string, ParsedLibrary> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _list;
    private readonly TextBox _detail;
    private readonly Button _restoreButton;

    /// <param name="read">退避ファイルを全件検証する。</param>
    /// <param name="restore">確認して戻す（戻したら true。この画面を閉じる）。</param>
    private RestoreWindow(Window owner, IReadOnlyList<SideCopy> copies, Func<string, ParsedLibrary> read,
        Func<Window, SideCopy, TemplateLibrary, bool> restore)
    {
        _read = read;
        _restore = restore;
        Owner = owner;
        Title = Strings.RestoreTitle;
        FontFamily = UiFont.Family;
        FontSize = 13;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _list = new ListBox
        {
            Height = 260,
            ItemsSource = copies.Select(c => new SideCopyRow(c)).ToList(),
            DisplayMemberPath = nameof(SideCopyRow.Text),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = copies.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetName(_list, Strings.RestoreTitle);
        _list.SelectionChanged += (_, _) => ShowSelected();
        // 理由（フルパスを含む）を選んでコピーできるよう、読み取り専用のテキストボックスにする。
        _detail = new TextBox
        {
            Text = copies.Count > 0 ? Strings.RestoreSelectHint : Strings.RestoreNone,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 36,
        };
        _restoreButton = new Button { Content = Strings.ButtonRestoreRun, MinWidth = 88, Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        _restoreButton.Click += (_, _) => RestoreSelected();
        var close = new Button { Content = Strings.ButtonClose, MinWidth = 88, Padding = new Thickness(10, 3, 10, 3), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_restoreButton);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = Strings.RestorePrompt, TextWrapping = TextWrapping.Wrap, Visibility = _list.Visibility });
        panel.Children.Add(_list);
        panel.Children.Add(_detail);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            if (copies.Count > 0) _list.Focus();
        };
    }

    /// <summary>一覧を開く。戻した場合は true。</summary>
    public static bool Ask(Window owner, IReadOnlyList<SideCopy> copies, Func<string, ParsedLibrary> read,
        Func<Window, SideCopy, TemplateLibrary, bool> restore) =>
        new RestoreWindow(owner, copies, read, restore).ShowDialog() == true;

    /// <summary>「インポート前　2026/11/17 10:15:00」（日時はファイル名から。表示言語の書式）。</summary>
    public static string Describe(SideCopy copy)
    {
        var kind = copy.Kind switch
        {
            SideCopyKind.Import => Strings.RestoreKindImport,
            SideCopyKind.Delete => Strings.RestoreKindDelete,
            _ => Strings.RestoreKindRestore,
        };
        var stamp = copy.Stamp.ToString("G", CultureInfo.CurrentUICulture);
        return copy.Sequence > 1 ? $"{kind}　{stamp} ({copy.Sequence})" : $"{kind}　{stamp}";
    }

    private void ShowSelected()
    {
        if (_list.SelectedItem is not SideCopyRow row)
        {
            _detail.Text = Strings.RestoreSelectHint;
            _restoreButton.IsEnabled = false;
            return;
        }
        if (!_checked.TryGetValue(row.Copy.Path, out var parsed))
        {
            parsed = _read(row.Copy.Path);
            _checked[row.Copy.Path] = parsed;
        }
        _detail.Text = parsed.Succeeded
            ? string.Format(Strings.RestoreCountFormat, parsed.Library!.Categories.Count, parsed.Library.Templates.Count)
            : parsed.Problem;
        _restoreButton.IsEnabled = parsed.Succeeded;
    }

    private void RestoreSelected()
    {
        if (_list.SelectedItem is not SideCopyRow row || !_checked.TryGetValue(row.Copy.Path, out var parsed) || !parsed.Succeeded) return;
        if (_restore(this, row.Copy, parsed.Library!)) DialogResult = true;
    }
}
