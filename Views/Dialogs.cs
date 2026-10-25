using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipCompose.Core.Localization;

namespace ClipCompose.Views;

/// <summary>カテゴリ名などの 1 行の入力。検証は呼び出し側が行い、問題があれば理由を表示して入力を続けさせる。</summary>
public sealed class NameDialog : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string> _validate;

    /// <param name="validate">入力値の問題（空文字なら問題なし）。</param>
    private NameDialog(Window? owner, string prompt, string initial, Func<string, string> validate)
    {
        _validate = validate;
        Owner = owner;
        Title = "ClipCompose";
        FontFamily = UiFont.Family;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = owner is null;

        _box = new TextBox { Text = initial, Width = 360, Margin = new Thickness(0, 6, 0, 0) };
        _error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = Strings.ButtonOk, MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Strings.ButtonCancel, MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    public string Value => _box.Text;

    /// <summary>OK なら入力値、キャンセルなら null。</summary>
    public static string? Ask(Window? owner, string prompt, string initial, Func<string, string> validate)
    {
        var dialog = new NameDialog(owner, prompt, initial, validate);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Accept()
    {
        var problem = _validate(_box.Text);
        if (problem.Length > 0)
        {
            _error.Text = problem;
            _box.Focus();
            return;
        }
        DialogResult = true;
    }
}

/// <summary>3 つ以上の選択肢を示す確認（復旧方法など。MessageBox のはい／いいえでは意味が伝わりにくいもの）。</summary>
public sealed class ChoiceDialog : Window
{
    private int _choice = -1;

    private ChoiceDialog(Window? owner, string message, IReadOnlyList<(string Label, bool Enabled)> choices, int cancelIndex)
    {
        Owner = owner;
        Title = "ClipCompose";
        FontFamily = UiFont.Family;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = owner is null;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBox
        {
            Text = message,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = choices[i].Label,
                IsEnabled = choices[i].Enabled,
                MinWidth = 100,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = i == cancelIndex,
            };
            button.Click += (_, _) =>
            {
                _choice = index;
                DialogResult = true;
            };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        Content = panel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _choice = cancelIndex;
        };
    }

    /// <summary>選んだ選択肢の番号。閉じた場合は <paramref name="cancelIndex"/>。</summary>
    public static int Ask(Window? owner, string message, IReadOnlyList<(string Label, bool Enabled)> choices, int cancelIndex)
    {
        var dialog = new ChoiceDialog(owner, message, choices, cancelIndex);
        dialog.ShowDialog();
        return dialog._choice < 0 ? cancelIndex : dialog._choice;
    }
}

/// <summary>読むだけの長い文章（使い方・同梱のライセンス表記）。</summary>
public sealed class TextWindow : Window
{
    public TextWindow(string title, string text, bool monospace = false)
    {
        Title = title;
        // ウィンドウの名前（読み上げ）はタイトルにする（指定しないと中の文章全体が名前になる）。
        System.Windows.Automation.AutomationProperties.SetName(this, title);
        FontFamily = monospace ? new System.Windows.Media.FontFamily("Consolas, Yu Gothic UI") : UiFont.Family;
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14),
            FontSize = 13,
        };
    }
}
