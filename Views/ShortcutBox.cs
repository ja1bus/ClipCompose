using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;

namespace ClipCompose.Views;

/// <summary>
/// ショートカットの入力欄。フォーカスしてキーを押すと、その組み合わせ（"Ctrl+Alt+S" 形式）が入る。
/// Backspace / Delete を単独で押すと未割当にする。
/// 入力中は <see cref="CaptureStarted"/> / <see cref="CaptureEnded"/> で既存のグローバルショートカットを一時解除してもらう
/// （今のホットキーを押しても、テンプレートが実行されずに入力できるように）。入力はクリック・Tab キーで入ったとき・キーを押したときに始め、
/// フォーカスが離れたら終える。
/// </summary>
public sealed class ShortcutBox : TextBox
{
    private Shortcut _shortcut;

    public ShortcutBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        ToolTip = Strings.ShortcutBoxTip;
        // タブの切り替えでフォーカスが移っただけで説明が出て、下の登録結果（✓ 登録しました）を隠さないよう、マウスを重ねたときだけ出す。
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, false);
        VerticalContentAlignment = VerticalAlignment.Center;
        UpdateText();
    }

    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureEnded;
    public event EventHandler? ShortcutChanged;

    public Shortcut Shortcut
    {
        get => _shortcut;
        set
        {
            if (_shortcut == value) return;
            _shortcut = value;
            UpdateText();
            ShortcutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>入力中（登録済みのホットキーを一時解除してもらっている）か。</summary>
    public bool IsCapturing { get; private set; }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // タブの切り替えなどでフォーカスが移っただけでは入力を始めない（ホットキーを止めない）。
        // Tab キーで移ってきたときと、クリックしたとき（OnPreviewMouseLeftButtonDown）だけ始める。
        if (Keyboard.IsKeyDown(Key.Tab)) BeginCapture();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        BeginCapture();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsCapturing) return;
        IsCapturing = false;
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    private void BeginCapture()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // フォーカスはあるが入力を始めていない（タブの切り替えで移ってきた）ときは、キーを押した時点で始める。
        if (!IsCapturing && IsKeyboardFocused && !(e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)) BeginCapture();
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        // 修飾なしの Tab はフォーカス移動に使う。
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        e.Handled = true;

        var modifiers = ToModifiers(Keyboard.Modifiers);
        if (modifiers == KeyModifiers.None && key is Key.Back or Key.Delete)
        {
            Shortcut = Shortcut.None;
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || KeyNames.IsModifierKey(vk)) return; // 修飾キーだけの間は確定しない
        Shortcut = new Shortcut(modifiers, vk);
    }

    private void UpdateText() => Text = _shortcut.IsEmpty ? Strings.ShortcutBoxUnassigned : _shortcut.ToString();

    private static KeyModifiers ToModifiers(ModifierKeys m)
    {
        var r = KeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) r |= KeyModifiers.Control;
        if (m.HasFlag(ModifierKeys.Alt)) r |= KeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) r |= KeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) r |= KeyModifiers.Win;
        return r;
    }
}
