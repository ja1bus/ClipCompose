using System.Runtime.InteropServices;
using ClipCompose.Core.Settings;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Services;

/// <summary>モニターの作業領域（物理ピクセル）と倍率（1.0 = 96 DPI）。</summary>
public readonly record struct MonitorArea(PixelRect Work, double Scale);

/// <summary>
/// モニターの作業領域（仕様書 §5・§9・§17.6）。選択画面・通知を、画面・貼り付け先・カーソルを含むモニターに置くために使う。
/// AreaShot 1.4.0 の DisplayInfo と同じ方法（共通資産には切り出さない）。
/// </summary>
public static class DisplayArea
{
    /// <summary>点（物理ピクセル）を含むモニター。どのモニターにも無ければ最も近いモニター。</summary>
    public static MonitorArea At(int x, int y) =>
        FromMonitor(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));

    /// <summary>ウィンドウ（の大部分）があるモニター。</summary>
    public static MonitorArea Of(IntPtr hwnd) => FromMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    /// <summary>主モニター。</summary>
    public static MonitorArea Primary() => FromMonitor(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY));

    /// <summary>点がどれかのモニター上にあるか（前回の位置がモニターの取り外しなどで画面外になっていないか）。</summary>
    public static bool IsOnAnyMonitor(int x, int y) => MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    /// <summary>カーソルの位置（取れなければ主モニターの作業領域の中央）。</summary>
    public static (int X, int Y) Cursor()
    {
        if (GetCursorPos(out var p)) return (p.X, p.Y);
        var work = Primary().Work;
        return (work.Left + work.Width / 2, work.Top + work.Height / 2);
    }

    /// <summary>システム DPI の倍率（WPF がウィンドウを作る前の Left / Top の換算に使う値）。</summary>
    public static double SystemScale
    {
        get
        {
            var dpi = GetDpiForSystem();
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
    }

    /// <summary>ウィンドウの位置と大きさ（物理ピクセル）。取れなければ null。</summary>
    public static PixelRect? WindowRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) ? new PixelRect(r.Left, r.Top, r.Right, r.Bottom) : null;

    /// <summary>ウィンドウを物理ピクセルの位置へ動かす（大きさ・重なり順・アクティブは変えない）。</summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>ウィンドウの倍率（1.0 = 96 DPI）。</summary>
    public static double ScaleOf(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : SystemScale;
    }

    private static MonitorArea FromMonitor(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            // 取得できない場合は WPF の主モニターの作業領域（DIU）をシステム DPI で換算して使う。
            var w = System.Windows.SystemParameters.WorkArea;
            var s = SystemScale;
            return new MonitorArea(new PixelRect((int)(w.Left * s), (int)(w.Top * s), (int)(w.Right * s), (int)(w.Bottom * s)), s);
        }
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : SystemScale;
        var r = info.rcWork;
        return new MonitorArea(new PixelRect(r.Left, r.Top, r.Right, r.Bottom), scale);
    }
}
