using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipCompose.Services;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Views;

/// <summary>
/// 実行結果の軽量な通知（仕様書 §9）。モニターの作業領域の右下に数秒だけ表示し、フォーカスを奪わない（貼り付け先の操作を妨げない）。
/// モニターは基準（<see cref="ScreenAnchor"/>。貼り付け先のウィンドウ・カーソル）のあるもの、基準が無ければ主モニター（1.3.0〜）。
/// OS のトースト通知は AppUserModelID の登録などが必要になるため使わない（AreaShot と同じ方式）。
/// </summary>
public sealed class ToastWindow : Window
{
    /// <summary>作業領域の端からの余白（DIU）。</summary>
    private const double EdgeMargin = 16;

    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;
    private readonly MonitorArea _monitor;
    private IntPtr _hwnd;
    private bool _placePending;

    private ToastWindow(string title, string detail, bool warning, TimeSpan duration, ScreenAnchor? anchor)
    {
        _monitor = anchor?.Monitor() ?? DisplayArea.Primary();
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

        // 作成前の見積もり: 基準のモニターの右下（そのモニターの拡大率でウィンドウが作られるように。WPF はシステム DPI で換算する）。
        // 最終の位置は、ウィンドウを作った後と大きさが変わるたびに物理ピクセルで合わせる（Place）。
        var work = _monitor.Work;
        var sys = DisplayArea.SystemScale;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (work.Right - (Width + EdgeMargin) * _monitor.Scale) / sys;
        Top = (work.Bottom - (120 + EdgeMargin) * _monitor.Scale) / sys;
        // WPF は SizeChanged の後（LayoutUpdated）で HWND の大きさを変えるため、レイアウトの後に合わせる。
        SizeChanged += (_, _) => SchedulePlace();
    }

    public static void ShowInfo(string title, string detail, ScreenAnchor? anchor = null) =>
        Present(new ToastWindow(title, detail, false, TimeSpan.FromSeconds(3), anchor));

    public static void ShowWarning(string title, string detail, ScreenAnchor? anchor = null) =>
        Present(new ToastWindow(title, detail, true, TimeSpan.FromSeconds(6), anchor));

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
        _hwnd = hwnd;
        Place();
    }

    /// <summary>大きさが変わった後の配置を、HWND の大きさが変わった後（レイアウトの後）に 1 回だけ行う。</summary>
    private void SchedulePlace()
    {
        if (_placePending || _hwnd == IntPtr.Zero) return;
        _placePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _placePending = false;
            Place();
        });
    }

    /// <summary>
    /// 基準のモニターの作業領域の右下へ、物理ピクセルで置く。拡大率の違うモニターへ移って WPF が大きさを変えた場合も、
    /// SizeChanged から（レイアウトの後に）呼ばれて合わせ直す。
    /// </summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero || DisplayArea.WindowRect(_hwnd) is not { } rect) return;
        var margin = (int)Math.Round(EdgeMargin * DisplayArea.ScaleOf(_hwnd));
        var work = _monitor.Work;
        var x = Math.Max(work.Left, work.Right - rect.Width - margin);
        var y = Math.Max(work.Top, work.Bottom - rect.Height - margin);
        if (x != rect.Left || y != rect.Top) DisplayArea.MoveWindow(_hwnd, x, y);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _hwnd = IntPtr.Zero; // 予約した配置を閉じた後に行わない
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
