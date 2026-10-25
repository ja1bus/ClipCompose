using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Services;

public enum HotkeyMode
{
    Normal = 1,
    Fixed = 2,
}

public enum HotkeyStatus
{
    Unassigned,
    Registered,
    /// <summary>他のアプリまたは Windows が使用中などで登録できなかった。</summary>
    Failed,
    /// <summary>設定画面での入力中のため一時的に解除している。</summary>
    Suspended,
}

public sealed record HotkeyRegistration(Shortcut Shortcut, HotkeyStatus Status, int ErrorCode = 0)
{
    public static readonly HotkeyRegistration Unassigned = new(Shortcut.None, HotkeyStatus.Unassigned);

    public string Describe() => Status switch
    {
        HotkeyStatus.Unassigned => Strings.HotkeyUnassigned,
        HotkeyStatus.Registered => string.Format(Strings.HotkeyRegisteredFormat, Shortcut),
        HotkeyStatus.Suspended => Strings.HotkeySuspended,
        _ when ErrorCode == ERROR_HOTKEY_ALREADY_REGISTERED => string.Format(Strings.HotkeyInUseFormat, Shortcut),
        _ => string.Format(Strings.HotkeyErrorFormat, Shortcut, ErrorCode),
    };
}

/// <summary>
/// 通常モード・固定モードのグローバルホットキー（仕様書 §10）。RegisterHotKey + MOD_NOREPEAT
/// （押しっぱなしの自動リピートで連続実行しない）。メッセージ専用ウィンドウで WM_HOTKEY を受ける（常時フック・ポーリングはしない）。
/// 既定はどちらも未設定。登録に失敗しても黙って無視せず、状態として表示する。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<HotkeyMode, Shortcut> _shortcuts = new() { [HotkeyMode.Normal] = Shortcut.None, [HotkeyMode.Fixed] = Shortcut.None };
    private readonly HashSet<HotkeyMode> _registered = [];
    private int _suspendCount;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("ClipCompose.Hotkey") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>ホットキーが押された（UI スレッド）。</summary>
    public event Action<HotkeyMode, Shortcut>? Pressed;

    /// <summary>登録状態が変わった。</summary>
    public event EventHandler? StatusChanged;

    public HotkeyRegistration Normal { get; private set; } = HotkeyRegistration.Unassigned;

    public HotkeyRegistration Fixed { get; private set; } = HotkeyRegistration.Unassigned;

    public HotkeyRegistration Get(HotkeyMode mode) => mode == HotkeyMode.Normal ? Normal : Fixed;

    /// <summary>起動時の登録。失敗しても状態として残す（設定画面・トレイは使える）。</summary>
    public void Apply(Shortcut normal, Shortcut @fixed)
    {
        _shortcuts[HotkeyMode.Normal] = normal;
        _shortcuts[HotkeyMode.Fixed] = @fixed;
        RegisterAll();
    }

    /// <summary>
    /// 設定変更での登録。新しい組み合わせのどちらかが登録できなければ、以前の組み合わせに戻して false を返す
    /// （旧設定を利用可能な状態に戻す）。<paramref name="failure"/> は登録できなかったものの状態。
    /// </summary>
    public bool TryChange(Shortcut normal, Shortcut @fixed, out HotkeyRegistration? failure)
    {
        var previousNormal = _shortcuts[HotkeyMode.Normal];
        var previousFixed = _shortcuts[HotkeyMode.Fixed];
        Apply(normal, @fixed);
        failure = Normal.Status == HotkeyStatus.Failed ? Normal : Fixed.Status == HotkeyStatus.Failed ? Fixed : null;
        if (failure is null) return true;
        Apply(previousNormal, previousFixed);
        return false;
    }

    /// <summary>一時的に解除する（設定画面のショートカット入力中）。<see cref="Resume"/> と対で呼ぶ。</summary>
    public void Suspend()
    {
        if (_suspendCount++ > 0) return;
        UnregisterAll();
        Normal = Suspended(HotkeyMode.Normal);
        Fixed = Suspended(HotkeyMode.Fixed);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (_suspendCount == 0 || --_suspendCount > 0) return;
        RegisterAll();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private HotkeyRegistration Suspended(HotkeyMode mode) =>
        _shortcuts[mode].IsEmpty ? HotkeyRegistration.Unassigned : new HotkeyRegistration(_shortcuts[mode], HotkeyStatus.Suspended);

    private void RegisterAll()
    {
        UnregisterAll();
        if (_suspendCount > 0)
        {
            Normal = Suspended(HotkeyMode.Normal);
            Fixed = Suspended(HotkeyMode.Fixed);
        }
        else
        {
            Normal = Register(HotkeyMode.Normal);
            Fixed = Register(HotkeyMode.Fixed);
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private HotkeyRegistration Register(HotkeyMode mode)
    {
        var shortcut = _shortcuts[mode];
        if (shortcut.IsEmpty) return HotkeyRegistration.Unassigned;
        if (!RegisterHotKey(_source.Handle, (int)mode, (int)shortcut.Modifiers | MOD_NOREPEAT, shortcut.Key))
        {
            var error = Marshal.GetLastPInvokeError();
            ErrorLog.Write($"RegisterHotKey failed for {mode} ({shortcut}, error {error})");
            return new HotkeyRegistration(shortcut, HotkeyStatus.Failed, error);
        }
        _registered.Add(mode);
        return new HotkeyRegistration(shortcut, HotkeyStatus.Registered);
    }

    private void UnregisterAll()
    {
        foreach (var mode in _registered) UnregisterHotKey(_source.Handle, (int)mode);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && Enum.IsDefined(typeof(HotkeyMode), wParam.ToInt32()))
        {
            handled = true;
            var mode = (HotkeyMode)wParam.ToInt32();
            Pressed?.Invoke(mode, _shortcuts[mode]);
        }
        return IntPtr.Zero;
    }
}
