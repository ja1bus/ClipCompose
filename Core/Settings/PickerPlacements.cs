namespace ClipCompose.Core.Settings;

/// <summary>画面上の長方形（物理ピクセル。Right・Bottom は含まない端）。</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// 選択画面を開く位置（仕様書 §5・§17.7）。設定の値と、左上の位置の計算（物理ピクセル。Win32 に依存しない純粋な関数）。
/// </summary>
public static class PickerPlacements
{
    /// <summary>カーソルのあるモニターの作業領域の中央（既定。1.2.1 と同じ見た目）。</summary>
    public const string Center = "center";

    /// <summary>カーソルの近く（右下へ少しずらした位置）。</summary>
    public const string Cursor = "cursor";

    /// <summary>前回閉じたときの位置。</summary>
    public const string Remembered = "remembered";

    /// <summary>カーソルからずらす量（DIU。物理ピクセルではモニターの倍率を掛ける）。</summary>
    public const int CursorOffset = 16;

    public static IReadOnlyList<string> All { get; } = [Center, Cursor, Remembered];

    public static bool IsKnown(string? value) => value is Center or Cursor or Remembered;

    /// <summary>
    /// 開く位置の左上。
    /// </summary>
    /// <param name="mode">設定の値（補正済み）。</param>
    /// <param name="cursorWork">カーソルのあるモニターの作業領域。</param>
    /// <param name="cursorX">カーソルの位置。</param>
    /// <param name="cursorScale">カーソルのあるモニターの倍率（1.0 = 96 DPI）。</param>
    /// <param name="remembered">前回の左上（無ければ null）。</param>
    /// <param name="rememberedWork">前回の左上を含むモニターの作業領域（どのモニター上にも無ければ null）。</param>
    /// <param name="width">ウィンドウの幅（物理ピクセル）。</param>
    public static (int X, int Y) Place(string mode, PixelRect cursorWork, int cursorX, int cursorY, double cursorScale,
        (int X, int Y)? remembered, PixelRect? rememberedWork, int width, int height)
    {
        switch (mode)
        {
            case Cursor:
                return NearCursor(cursorWork, cursorX, cursorY, cursorScale, width, height);
            case Remembered when remembered is { } r && rememberedWork is { } work:
                return ClampInto(work, r.X, r.Y, width, height);
            default:
                return CenterIn(cursorWork, width, height);
        }
    }

    /// <summary>作業領域の中央（大きすぎる場合は左上を作業領域の左上より外へ出さない）。</summary>
    public static (int X, int Y) CenterIn(PixelRect work, int width, int height) =>
        ClampInto(work, work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);

    /// <summary>カーソルから右下へ <see cref="CursorOffset"/>×倍率 ずらした点を左上にし、作業領域に収める。</summary>
    public static (int X, int Y) NearCursor(PixelRect work, int cursorX, int cursorY, double scale, int width, int height)
    {
        var offset = (int)Math.Round(CursorOffset * scale);
        return ClampInto(work, cursorX + offset, cursorY + offset, width, height);
    }

    /// <summary>右・下へはみ出す分を左・上へ寄せる。左上は作業領域の左上より外へ出さない。</summary>
    public static (int X, int Y) ClampInto(PixelRect work, int x, int y, int width, int height)
    {
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        return (x, y);
    }
}
