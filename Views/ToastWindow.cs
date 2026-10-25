using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Views;

/// <summary>
/// 実行結果の軽量な通知（仕様書 §9）。画面右下に数秒だけ表示し、フォーカスを奪わない（貼り付け先の操作を妨げない）。
/// OS のトースト通知は AppUserModelID の登録などが必要になるため使わない（AreaShot と同じ方式）。
/// </summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;

    private ToastWindow(string title, string detail, bool warning, TimeSpan duration)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.Height;
        Width = 400;
        UseLayoutRounding = true;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = warning ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x60)) : Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (detail.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            });
        }
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x2B, 0x2B, 0x2B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Child = panel,
        };
        FontFamily = UiFont.Family;
        FontSize = 12.5;

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
        MouseLeftButtonUp += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();

        // 主モニターの作業領域の右下（DIU）。高さは内容で決まるため、大きさが決まるたびに下端を合わせる。
        var work = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Right - Width - 16;
        Top = work.Bottom - 120;
        SizeChanged += (_, _) => Top = work.Bottom - ActualHeight - 16;
    }

    public static void ShowInfo(string title, string detail) => Present(new ToastWindow(title, detail, false, TimeSpan.FromSeconds(3)));

    public static void ShowWarning(string title, string detail) => Present(new ToastWindow(title, detail, true, TimeSpan.FromSeconds(6)));

    private static void Present(ToastWindow toast)
    {
        _current?.Close();
        _current = toast;
        toast.Closed += (_, _) => { if (_current == toast) _current = null; };
        toast.Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void FadeOut()
    {
        _timer.Stop();
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }
}
