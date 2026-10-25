using System.Runtime.InteropServices;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Input;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Services;

/// <summary>
/// 自動貼り付け（仕様書 §9）。コピーは済んでいる前提で、次の順に確かめてから Ctrl+V 相当を送る。Enter は送らない。
/// 1. 貼り付け先が有効（存在・表示中・自分以外）で、管理者として動いていない。
/// 2. 前面が貼り付け先でない場合、選択画面を閉じた直後（前面が ClipCompose 自身・デスクトップ等）だけ通常の
///    SetForegroundWindow で一度戻す（入力キューの接続などで強制しない）。利用者が別のアプリへ移っていたら奪い返さない
///    （<see cref="PasteFocusRule"/>）。固定モードは前面を奪っていないため戻さない。
/// 3. ショートカットの修飾キー・起動キーが離されるのを期限付きで待つ。
/// 4. 前面が貼り付け先のまま、クリップボードが書き込んだときのままかを確かめる。
/// 送った入力は「投入できた」ことしか分からないため、結果は「送信」として返す（貼り付けの完了とはしない）。
/// </summary>
public sealed class PasteService : IPastePort
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const int FocusTimeoutMs = 500;
    private const int KeyReleaseTimeoutMs = 2000;
    private const int PollMs = 10;
    /// <summary>前面になった直後の貼り付け先がキー入力を受け付けられるまでの余裕。</summary>
    private const int SettleMs = 40;

    /// <summary>ClipCompose が送った入力の目印（dwExtraInfo）。</summary>
    private static readonly UIntPtr InjectionSignature = new(0x436C436D);

    private static readonly int[] HeldKeysToWait = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift・Ctrl・Alt・左右 Win

    public async Task<PasteOutcome> PasteAsync(PasteTarget target, uint writtenSequence)
    {
        var root = WindowHelper.RootOf(target.Window);
        if (!WindowHelper.IsPasteTarget(root)) return CopiedOnly(PasteSkipReason.TargetGone);
        if (WindowHelper.IsElevatedTarget(root)) return CopiedOnly(PasteSkipReason.TargetElevated);

        if (!await RestoreForegroundAsync(root, target.RestoreFocus)) return CopiedOnly(PasteSkipReason.FocusNotRestored);
        if (!await WaitForKeysReleasedAsync(target.TriggerKey)) return CopiedOnly(PasteSkipReason.KeysHeld);
        await Task.Delay(SettleMs);

        if (!IsWindow(root)) return CopiedOnly(PasteSkipReason.TargetGone);
        // 待っている間に利用者が別のアプリへ移った場合は、奪い返さずにコピーのみにする。
        if (WindowHelper.RootOf(GetForegroundWindow()) != root) return CopiedOnly(PasteSkipReason.FocusNotRestored);
        if (GetClipboardSequenceNumber() != writtenSequence)
        {
            ErrorLog.Write("The clipboard changed before pasting; Ctrl+V was not sent");
            return new(PasteStatus.ClipboardChanged);
        }
        return SendCtrlV() ? new(PasteStatus.Sent) : CopiedOnly(PasteSkipReason.SendFailed);

        // コピーのみで終えるとき、待っている間にクリップボードが変わっていたら「Ctrl+V して」とは案内しない。
        PasteOutcome CopiedOnly(PasteSkipReason reason)
        {
            if (GetClipboardSequenceNumber() != writtenSequence) return new(PasteStatus.ClipboardChanged);
            ErrorLog.Write($"Automatic paste skipped: {reason}");
            return new(PasteStatus.CopiedOnly, reason);
        }
    }

    private static async Task<bool> RestoreForegroundAsync(IntPtr root, bool restoreAllowed)
    {
        var foreground = WindowHelper.RootOf(GetForegroundWindow());
        // 貼り付け先として扱えない前面（ClipCompose 自身・デスクトップ・タスクバー・無し）は、利用者が別のアプリへ移ったものとはみなさない。
        var action = PasteFocusRule.Decide(foreground == root, !WindowHelper.IsPasteTarget(foreground), restoreAllowed);
        if (action == FocusAction.Proceed) return true;
        if (action == FocusAction.Abandon) return false;
        if (IsIconic(root)) return false; // 最小化したウィンドウは戻さない（利用者が意図して閉じた可能性がある）
        SetForegroundWindow(root);
        for (var waited = 0; waited < FocusTimeoutMs; waited += PollMs)
        {
            if (WindowHelper.RootOf(GetForegroundWindow()) == root) return true;
            await Task.Delay(PollMs);
        }
        return false;
    }

    private static async Task<bool> WaitForKeysReleasedAsync(int triggerVk)
    {
        for (var waited = 0; waited < KeyReleaseTimeoutMs; waited += PollMs)
        {
            if (!AnyHeld(triggerVk)) return true;
            await Task.Delay(PollMs);
        }
        return false;
    }

    private static bool AnyHeld(int triggerVk)
    {
        foreach (var vk in HeldKeysToWait)
            if (GetAsyncKeyState(vk) < 0) return true;
        return triggerVk != 0 && GetAsyncKeyState(triggerVk) < 0;
    }

    /// <summary>
    /// Ctrl↓ V↓ V↑ Ctrl↑ を送る。途中までしか投入できなかった場合は、押したままになったキーを離す入力を送る
    /// （Ctrl が押されたままの状態を残さない）。
    /// </summary>
    private static unsafe bool SendCtrlV()
    {
        var inputs = stackalloc INPUT[4];
        inputs[0] = Key(VK_CONTROL, up: false);
        inputs[1] = Key(VK_V, up: false);
        inputs[2] = Key(VK_V, up: true);
        inputs[3] = Key(VK_CONTROL, up: true);
        var sent = SendInput(4, inputs, sizeof(INPUT));
        if (sent == 4) return true;
        ErrorLog.Write($"SendInput sent {sent}/4 inputs (error {Marshal.GetLastPInvokeError()})");

        var release = stackalloc INPUT[2];
        uint count = 0;
        if (sent == 2) release[count++] = Key(VK_V, up: true);
        if (sent is >= 1 and <= 3) release[count++] = Key(VK_CONTROL, up: true);
        if (count > 0 && SendInput(count, release, sizeof(INPUT)) != count)
            ErrorLog.Write($"Releasing keys after a partial SendInput failed (error {Marshal.GetLastPInvokeError()})");
        return false;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                dwExtraInfo = InjectionSignature,
            },
        },
    };

    /// <summary>ホットキーの押下時に、前面のウィンドウを貼り付け先として記録する。自分のウィンドウは対象にしない。</summary>
    public static PasteTarget? CaptureTarget(Shortcut shortcut)
    {
        var root = WindowHelper.RootOf(GetForegroundWindow());
        if (!WindowHelper.IsPasteTarget(root)) return null;
        return new PasteTarget(root, shortcut.Modifiers, shortcut.Key);
    }
}

/// <summary>ウィンドウの判定の小道具。</summary>
public static class WindowHelper
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private static readonly bool SelfElevated = IsProcessElevated(GetCurrentProcess(), out _);

    public static IntPtr RootOf(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnProcessId;
    }

    /// <summary>貼り付け先として扱えるか。ClipCompose 自身・タスクバー・デスクトップは対象外。</summary>
    public static unsafe bool IsPasteTarget(IntPtr hwnd)
    {
        var root = RootOf(hwnd);
        if (root == IntPtr.Zero || !IsWindow(root) || !IsWindowVisible(root) || IsOwnWindow(root)) return false;
        var buffer = stackalloc char[64];
        var length = GetClassName(root, buffer, 64);
        var name = new string(buffer, 0, Math.Max(length, 0));
        return name is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "NotifyIconOverflowWindow");
    }

    /// <summary>
    /// 貼り付け先が管理者として実行中で、ClipCompose（通常の権限）からは入力が届かない（UIPI）可能性が高いか。
    /// トークンを読めない（アクセス拒否）場合も、昇格したプロセスとみなす。
    /// </summary>
    public static bool IsElevatedTarget(IntPtr hwnd)
    {
        if (SelfElevated) return false;
        var root = RootOf(hwnd);
        if (root == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out var pid);
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false; // 判定できない場合は送ってみる（PlainClip と同じ）
        try
        {
            return IsProcessElevated(process, out var accessDenied) || accessDenied;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool IsProcessElevated(IntPtr process, out bool accessDenied)
    {
        accessDenied = false;
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            accessDenied = Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED;
            return false;
        }
        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
