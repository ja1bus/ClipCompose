using ClipCompose.Services;

namespace ClipCompose.Views;

/// <summary>
/// 通知を出すモニターの基準（仕様書 §9・§17.6）。ウィンドウ（貼り付け先）か点（カーソル）。
/// ショートカットからの実行は押した時点の前面のウィンドウ、トレイ・トレイから開いた選択画面からの実行はカーソル位置。
/// </summary>
public readonly record struct ScreenAnchor(IntPtr Window, int X, int Y)
{
    public static ScreenAnchor ForWindow(IntPtr window) => new(window, 0, 0);

    public static ScreenAnchor AtCursor()
    {
        var (x, y) = DisplayArea.Cursor();
        return new(IntPtr.Zero, x, y);
    }

    /// <summary>基準のあるモニターの作業領域。</summary>
    public MonitorArea Monitor() => Window != IntPtr.Zero ? DisplayArea.Of(Window) : DisplayArea.At(X, Y);
}
