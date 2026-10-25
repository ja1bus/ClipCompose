using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Text;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Services;

/// <summary>
/// クリップボードの読み書き（仕様書 §8。PlainClip の ClipboardService と同じ規約）。
///  - 監視・ポーリングはしない。読み書きは実行時だけ。
///  - 読み取りは CF_UNICODETEXT と保護指定だけ。遅延レンダリングで UI を固めないよう、スレッドプールで読む。
///    最初の NUL までを本文とし、上限を明らかに超える分は読まない。
///  - 書き込みはメッセージ専用ウィンドウを所有者にして UI スレッドで行う。読んだ時点から変わっていないかは、
///    開いた後・空にする前に確かめる（開いている間は他のアプリが変えられない）。
///  - 他のアプリが開いていれば短い間隔で回数を限って再試行する（UI スレッドでは待たずに await する）。
///  - 本文はログに書かない。
/// </summary>
public sealed class ClipboardService : IClipboardPort, IDisposable
{
    private const int OpenRetryCount = 10;
    private const int OpenRetryDelayMs = 25;

    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnoreFormat = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint CanIncludeInHistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadFormat = RegisterClipboardFormat("CanUploadToCloudClipboard");

    private readonly HwndSource _source;

    public ClipboardService()
    {
        var p = new HwndSourceParameters("ClipCompose.Clipboard") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 };
        _source = new HwndSource(p);
    }

    public uint CurrentSequence => GetClipboardSequenceNumber();

    public Task<ClipboardRead> ReadTextAsync(int maxScalars) => Task.Run(() => Read(maxScalars));

    public async Task<ClipboardWrite> WriteTextAsync(string text, ClipProtection protection, uint? expectedSequence)
    {
        var markers = ClipProtectionRules.MarkersToWrite(protection);
        if ((markers.ExcludeFromMonitor && ExcludeFormat == 0) || (markers.CanIncludeInHistoryZero && CanIncludeInHistoryFormat == 0)
            || (markers.CanUploadToCloudZero && CanUploadFormat == 0))
        {
            ErrorLog.Write("Clipboard protection formats are not available; nothing was written");
            return new(ClipboardWriteStatus.ProtectionUnavailable);
        }
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) await Task.Delay(OpenRetryDelayMs);
            if (!OpenClipboard(_source.Handle)) continue;
            var ok = false;
            try
            {
                if (expectedSequence is { } expected && GetClipboardSequenceNumber() != expected)
                    return new(ClipboardWriteStatus.ChangedSinceRead);
                // 目印を先に置き、本文はその後。途中で失敗したら閉じる前に空にする（他のアプリが読めるのは閉じた後）。
                // 保護指定を付けられないまま本文だけを公開しない。
                ok = EmptyClipboard()
                    && (!markers.ExcludeFromMonitor || PutDword(ExcludeFormat, 0))
                    && (!markers.CanIncludeInHistoryZero || PutDword(CanIncludeInHistoryFormat, 0))
                    && (!markers.CanUploadToCloudZero || PutDword(CanUploadFormat, 0))
                    && PutUnicodeText(text);
                if (!ok)
                {
                    var error = Marshal.GetLastPInvokeError();
                    EmptyClipboard();
                    ErrorLog.Write($"Writing to the clipboard failed (error {error})");
                }
            }
            finally
            {
                CloseClipboard();
            }
            return ok ? WrittenSequence() : new(ClipboardWriteStatus.Failed);
        }
        ErrorLog.Write("Could not open the clipboard for writing (in use by another app)");
        return new(ClipboardWriteStatus.Busy);
    }

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// 書き込み後の更新番号。Windows は EmptyClipboard・SetClipboardData のたびに番号を上げ、さらに CloseClipboard で
    /// 変換用の形式（CF_TEXT 等）を加えるときにも上げる（2026-10-25 実測: 閉じる時に +3）。開いている間の番号は最終値ではないため、
    /// 閉じた後に「番号 → 所有者が自分 → 同じ番号」を確かめる。他のアプリが書き換えるには EmptyClipboard で所有者が変わるため、
    /// 所有者が自分のまま番号が変わらなければ、その番号は自分の出力のもの。確かめられなければ自分の出力として記録しない。
    /// </summary>
    private ClipboardWrite WrittenSequence()
    {
        var before = GetClipboardSequenceNumber();
        var owner = GetClipboardOwner();
        var after = GetClipboardSequenceNumber();
        if (owner == _source.Handle && before == after) return new(ClipboardWriteStatus.Written, after);
        ErrorLog.Write("The clipboard was replaced by another app right after writing");
        return new(ClipboardWriteStatus.WrittenButReplaced);
    }

    private static ClipboardRead Read(int maxScalars)
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) Thread.Sleep(OpenRetryDelayMs);
            if (!OpenClipboard(IntPtr.Zero)) continue;
            try
            {
                var protection = ReadProtection();
                // CF_TEXT / CF_OEMTEXT しか無い場合も、OS が CF_UNICODETEXT を合成して返す。
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return new(ClipboardReadStatus.NoText, null, protection, GetClipboardSequenceNumber());
                var status = GetUnicodeText(maxScalars, out var text);
                // 本文を読んだ（遅延レンダリングがあれば済んだ）後の番号。開いている間は他のアプリが変えられない。
                return new(status, text, protection, GetClipboardSequenceNumber());
            }
            finally
            {
                CloseClipboard();
            }
        }
        ErrorLog.Write("Could not open the clipboard for reading (in use by another app)");
        return new(ClipboardReadStatus.Unavailable, null, ClipProtection.None, 0);
    }

    /// <summary>
    /// パスワードマネージャー等がコピーに付けた保護指定（開いている間だけ呼ぶ）。値を読めない指定は禁止とみなす（弱めない）。
    /// </summary>
    private static ClipProtection ReadProtection() => ClipProtectionRules.FromMarkers(
        ExcludeFormat != 0 && IsClipboardFormatAvailable(ExcludeFormat),
        ViewerIgnoreFormat != 0 && IsClipboardFormatAvailable(ViewerIgnoreFormat),
        ReadDword(CanIncludeInHistoryFormat),
        ReadDword(CanUploadFormat));

    private static MarkerValue ReadDword(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return MarkerValue.Absent;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return MarkerValue.Unreadable;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return MarkerValue.Unreadable;
        try
        {
            if ((ulong)GlobalSize(handle) < 4) return MarkerValue.Unreadable;
            return Marshal.ReadInt32(ptr) == 0 ? MarkerValue.Denied : MarkerValue.Allowed;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// CF_UNICODETEXT を最初の NUL まで読む（Windows の規約。確保領域の残りは不定）。
    /// 1 スカラー値は UTF-16 で最大 2 単位なので、<paramref name="maxScalars"/> × 2 単位を超えるなら全体を読まずに打ち切る。
    /// </summary>
    private static ClipboardReadStatus GetUnicodeText(int maxScalars, out string? text)
    {
        text = null;
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return ClipboardReadStatus.Unavailable;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return ClipboardReadStatus.Unavailable;
        try
        {
            var maxChars = (long)(ulong)GlobalSize(handle) / 2;
            var limit = (int)Math.Min(maxChars, maxScalars * 2L + 1);
            unsafe
            {
                var span = new ReadOnlySpan<char>((void*)ptr, limit);
                var nul = span.IndexOf('\0');
                if (nul < 0 && limit > maxScalars * 2L) return ClipboardReadStatus.TooLong;
                text = (nul >= 0 ? span[..nul] : span).ToString();
            }
            return ClipboardReadStatus.Ok;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool PutUnicodeText(string text)
    {
        var bytes = ((long)text.Length + 1) * 2;
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (hMem == IntPtr.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        try
        {
            unsafe
            {
                var dest = new Span<char>((void*)ptr, text.Length + 1);
                text.AsSpan().CopyTo(dest);
                dest[^1] = '\0';
            }
        }
        finally
        {
            GlobalUnlock(hMem);
        }
        if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero) return true; // 以後の解放はシステムが行う
        GlobalFree(hMem);
        return false;
    }

    private static bool PutDword(uint format, int value)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4);
        if (hMem == IntPtr.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        Marshal.WriteInt32(ptr, value);
        GlobalUnlock(hMem);
        if (SetClipboardData(format, hMem) != IntPtr.Zero) return true;
        GlobalFree(hMem);
        return false;
    }
}
