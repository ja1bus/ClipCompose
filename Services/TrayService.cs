using System.Windows.Forms;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Text;

namespace ClipCompose.Services;

/// <summary>固定スロット 1 つの状態（テンプレートとショートカット）。</summary>
public sealed record TraySlot(FixedTemplate Fixed, HotkeyRegistration Hotkey);

/// <summary>メニューを開くときに表示する現在の状態。<see cref="Slots"/> はスロット 1〜3。</summary>
public sealed record TrayState(IReadOnlyList<TraySlot> Slots, HotkeyRegistration NormalHotkey);

/// <summary>
/// タスクトレイのアイコンとメニュー（仕様書 §3）。WinForms の NotifyIcon を使う（WindowsDesktop 共有フレームワーク内）。
/// 左クリックで選択画面（コピーのみ）。メニュー: 選んでコピー / 固定テンプレートをコピー（参照先のあるスロットごと） / 設定 / 終了。
/// メニューの内容は開くたびに保存済みのデータから作り直す。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Drawing.Icon? _ownedIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Func<TrayState> _state;
    /// <summary>メニューの先頭の項目の太字（作り直すたびに作らず、1 つを使い回して Dispose で解放する）。</summary>
    private System.Drawing.Font? _boldFont;

    public TrayService(Func<TrayState> state)
    {
        _state = state;
        _ownedIcon = LoadAppIcon();
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();
        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
            Text = "ClipCompose",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) PickerRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? PickerRequested;
    /// <summary>固定スロットのコピーが選ばれた（引数はスロット番号）。</summary>
    public event EventHandler<int>? RunFixedRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    /// <summary>ツールチップを更新する（通常ホットキー、固定テンプレートと固定ホットキー）。</summary>
    public void UpdateTooltip()
    {
        var s = _state();
        var normal = s.NormalHotkey.Status == HotkeyStatus.Registered ? ShortcutDisplay.Format(s.NormalHotkey.Shortcut) : null;
        var slots = s.Slots
            .Where(x => x.Fixed.State == FixedTemplateState.Found)
            .Select(x => new TrayTooltipSlot(x.Fixed.Slot, x.Fixed.Template!.Name,
                x.Hotkey.Status == HotkeyStatus.Registered ? ShortcutDisplay.Format(x.Hotkey.Shortcut) : null))
            .ToList();
        _icon.Text = TrayTooltip.Build(normal, slots); // NotifyIcon.Text は 127 文字まで
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _menu.Dispose();
        _icon.Dispose();
        _ownedIcon?.Dispose();
        _boldFont?.Dispose();
    }

    private void Rebuild()
    {
        var s = _state();
        foreach (var item in _menu.Items.OfType<ToolStripItem>().ToList()) item.Dispose();
        _menu.Items.Clear();

        var picker = new ToolStripMenuItem(Strings.TrayOpenPicker, null, (_, _) => PickerRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = _boldFont ??= new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold),
            ShortcutKeyDisplayString = HotkeyText(s.NormalHotkey),
        };
        _menu.Items.Add(picker);

        // 参照先のあるスロットだけ出す（固定・解除は選択画面の［固定する］と設定画面で行う）。
        foreach (var slot in s.Slots.Where(x => x.Fixed.State == FixedTemplateState.Found))
        {
            var number = slot.Fixed.Slot;
            _menu.Items.Add(new ToolStripMenuItem(string.Format(Strings.TrayRunFixedFormat, number, MenuText(slot.Fixed.Template!.Name)), null,
                (_, _) => RunFixedRequested?.Invoke(this, number))
            {
                ShortcutKeyDisplayString = HotkeyText(slot.Hotkey),
            });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TraySettings, null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
    }

    private static string HotkeyText(HotkeyRegistration r) => r.Status == HotkeyStatus.Unassigned ? "" : r.Describe();

    /// <summary>WinForms のメニューでは "&amp;" がアクセスキーになるため、名前の "&amp;" はそのまま表示されるよう重ねる。</summary>
    private static string MenuText(string name) => name.Replace("&", "&&");

    /// <summary>exe に埋め込まれたアイコン（csproj の ApplicationIcon）を通知領域の大きさで取り出す。</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            return System.Drawing.Icon.ExtractIcon(path, 0, SystemInformation.SmallIconSize.Width);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }
}
