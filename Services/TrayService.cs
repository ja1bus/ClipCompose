using System.Windows.Forms;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Localization;

namespace ClipCompose.Services;

/// <summary>メニューを開くときに表示する現在の状態。</summary>
public sealed record TrayState(FixedTemplate Fixed, HotkeyRegistration NormalHotkey, HotkeyRegistration FixedHotkey);

/// <summary>
/// タスクトレイのアイコンとメニュー（仕様書 §3）。WinForms の NotifyIcon を使う（WindowsDesktop 共有フレームワーク内）。
/// 左クリックで選択画面（コピーのみ）。メニュー: 選んでコピー / 固定テンプレートをコピー（固定しているときだけ） / 設定 / 終了。
/// メニューの内容は開くたびに保存済みのデータから作り直す。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Drawing.Icon? _ownedIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Func<TrayState> _state;

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
    public event EventHandler? RunFixedRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    /// <summary>ツールチップを更新する（通常ホットキー、固定テンプレートと固定ホットキー）。</summary>
    public void UpdateTooltip()
    {
        var s = _state();
        var lines = new List<string> { "ClipCompose" };
        if (s.NormalHotkey.Status == HotkeyStatus.Registered)
            lines.Add(string.Format(Strings.TrayTooltipNormalFormat, s.NormalHotkey.Shortcut));
        if (s.Fixed.State == FixedTemplateState.Found)
        {
            lines.Add(s.FixedHotkey.Status == HotkeyStatus.Registered
                ? string.Format(Strings.TrayTooltipFixedHotkeyFormat, s.Fixed.Template!.Name, s.FixedHotkey.Shortcut)
                : string.Format(Strings.TrayTooltipFixedFormat, s.Fixed.Template!.Name));
        }
        else
        {
            lines.Add(Strings.TrayTooltipNoFixed);
        }
        var tip = string.Join("\n", lines);
        _icon.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon.Text は 127 文字まで
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
    }

    private void Rebuild()
    {
        var s = _state();
        foreach (var item in _menu.Items.OfType<ToolStripItem>().ToList()) item.Dispose();
        _menu.Items.Clear();

        var picker = new ToolStripMenuItem(Strings.TrayOpenPicker, null, (_, _) => PickerRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold),
            ShortcutKeyDisplayString = HotkeyText(s.NormalHotkey),
        };
        _menu.Items.Add(picker);

        // 固定しているときだけ出す（固定・解除は選択画面の📌と設定画面で行う）。
        if (s.Fixed.State == FixedTemplateState.Found)
        {
            _menu.Items.Add(new ToolStripMenuItem(string.Format(Strings.TrayRunFixedFormat, s.Fixed.Template!.Name), null,
                (_, _) => RunFixedRequested?.Invoke(this, EventArgs.Empty))
            {
                ShortcutKeyDisplayString = HotkeyText(s.FixedHotkey),
            });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TraySettings, null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
    }

    private static string HotkeyText(HotkeyRegistration r) => r.Status == HotkeyStatus.Unassigned ? "" : r.Describe();

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
