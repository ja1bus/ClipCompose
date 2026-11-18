using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Settings;
using ClipCompose.Services;

namespace ClipCompose.Views;

/// <summary>言語の選択肢。</summary>
public sealed record LanguageOption(string Value, string Name)
{
    /// <summary>選択肢の名前として UI Automation（読み上げ）に渡る文字列。</summary>
    public override string ToString() => Name;
}

/// <summary>選択画面を開く位置の選択肢。</summary>
public sealed record PlacementOption(string Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>［ショートカットと動作］タブの固定スロット 1 つ分の部品（1.3.0〜）。</summary>
internal sealed record SlotControls(int Slot, TextBlock Label, Button Unpin, ShortcutBox Box, CheckBox AutoPaste, TextBlock Status);

public enum SettingsTab
{
    Templates,
    Hotkeys,
    General,
    About,
}

/// <summary>
/// 設定画面（仕様書 §10）。タブは テンプレート / ショートカットと動作（通常と固定スロット 1〜3、開く位置） / 全般 / バージョン情報（一番右。版と © ja1bus）。
/// - テンプレートの編集は［保存］で確定する（<see cref="TemplateEditor"/>）。
/// - ホットキー・自動貼り付け・表示言語は変更した時点で反映・保存する。ホットキーは入力欄から離れた時点で登録し、
///   登録・保存に失敗したら以前の値に戻して理由を表示する。
/// - ×は画面を閉じるだけ（常駐は続ける）。未保存の編集があれば保存・破棄・キャンセルを尋ねる。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly HashSet<ShortcutBox> _capturing = [];
    private readonly List<SlotControls> _slots = [];
    private bool _loading;
    private bool _closeWithoutConfirm;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = string.Format(Strings.VersionFormat, AppHost.VersionText);
        DataFolderText.Text = string.Format(Strings.SettingsDataFolderFormat, host.DataDirectory);
        Editor.Attach(host);
        BuildSlotRows();
        LoadSettings();
        UpdateStatus();
        // 自動起動の状態は Windows 側で変わることがあるため、画面を前面に戻すたびに読み直す（定期的には読まない）。
        Activated += (_, _) => RefreshStartup();

        // 入力中は登録済みのホットキーを解除し、今のキーを押しても実行されないようにする。入力欄から離れたら登録する。
        foreach (var box in AllBoxes)
        {
            box.CaptureStarted += (_, _) =>
            {
                if (_capturing.Add(box)) _host.Hotkeys.Suspend();
                UpdateStatus();
            };
            box.CaptureEnded += (_, _) => EndCapture(box);
        }
    }

    /// <summary>通常と固定スロット 1〜3 のショートカットの入力欄。</summary>
    private IEnumerable<ShortcutBox> AllBoxes => [NormalBox, .. _slots.Select(x => x.Box)];

    /// <summary>
    /// 固定スロットの行を作る（仕様書 §10・§17.5）。1 行目「固定 N: テンプレート名」と［解除］、2 行目「自動貼り付けする」とショートカット、
    /// 3 行目に登録の結果。ショートカットの入力・登録・一時解除・失敗時の戻しは通常モードと同じ。
    /// </summary>
    private void BuildSlotRows()
    {
        foreach (var n in FixedSlotRules.Numbers)
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            var unpin = new Button { Content = Strings.ButtonUnpinSlot, Padding = new Thickness(10, 1, 10, 1), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 2) };
            System.Windows.Automation.AutomationProperties.SetName(unpin, string.Format(Strings.ButtonUnpinSlotNameFormat, n));
            System.Windows.Automation.AutomationProperties.SetAutomationId(unpin, $"UnpinButton{n}");
            var autoPaste = new CheckBox { Content = Strings.SettingsFixedSlotAutoPaste, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(autoPaste, string.Format(Strings.SettingsFixedSlotAutoPasteNameFormat, n));
            System.Windows.Automation.AutomationProperties.SetAutomationId(autoPaste, $"FixedAutoPasteBox{n}");
            var box = new ShortcutBox { Height = 26, Margin = new Thickness(0, 2, 0, 2) };
            System.Windows.Automation.AutomationProperties.SetName(box, string.Format(Strings.SettingsFixedSlotHotkeyNameFormat, n));
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, $"FixedBox{n}");
            var status = new TextBlock { Foreground = Brushes.Firebrick, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };

            Grid.SetColumn(unpin, 1);
            Grid.SetRow(autoPaste, 1);
            Grid.SetRow(box, 1);
            Grid.SetColumn(box, 1);
            Grid.SetRow(status, 2);
            Grid.SetColumnSpan(status, 2);
            foreach (UIElement child in new UIElement[] { label, unpin, autoPaste, box, status }) grid.Children.Add(child);
            FixedSlotsPanel.Children.Add(grid);

            var slot = n;
            unpin.Click += (_, _) => Unpin(slot);
            autoPaste.Checked += (_, _) => OnSlotAutoPasteChanged(slot);
            autoPaste.Unchecked += (_, _) => OnSlotAutoPasteChanged(slot);
            _slots.Add(new SlotControls(n, label, unpin, box, autoPaste, status));
        }
    }

    /// <summary>今表示しているタブ（閉じたときに覚えておき、次に開くときに使う）。</summary>
    public SettingsTab CurrentTab =>
        Tabs.SelectedItem == HotkeysTab ? SettingsTab.Hotkeys
        : Tabs.SelectedItem == GeneralTab ? SettingsTab.General
        : Tabs.SelectedItem == AboutTab ? SettingsTab.About
        : SettingsTab.Templates;

    public void ShowTab(SettingsTab tab) => Tabs.SelectedItem = tab switch
    {
        SettingsTab.Hotkeys => HotkeysTab,
        SettingsTab.General => GeneralTab,
        SettingsTab.About => AboutTab,
        _ => TemplatesTab,
    };

    /// <summary>初回起動の案内を［ホットキーと動作］タブに表示する。</summary>
    public void ShowFirstRunNote() => FirstRunBanner.Visibility = Visibility.Visible;

    public bool ConfirmLeaveEditor()
    {
        if (!Editor.IsDirty) return true;
        ShowTab(SettingsTab.Templates);
        return Editor.ConfirmLeaveEditor();
    }

    public void ReloadAfterReplace() => Editor.ReloadAfterReplace();

    /// <summary>保存済みのデータ・設定・ホットキーの状態が変わった。</summary>
    public void Refresh()
    {
        Editor.Refresh();
        UpdateStatus();
    }

    /// <summary>終了・再起動のとき。未保存の確認は呼び出し側で済ませている。</summary>
    public void CloseWithoutConfirm()
    {
        _closeWithoutConfirm = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeWithoutConfirm || ConfirmLeaveEditor()) return;
        e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // 入力中に閉じた場合も、一時解除したホットキーを必ず戻す。
        foreach (var box in _capturing.ToList()) EndCapture(box);
    }

    private void UpdateStatus()
    {
        ShowHotkeyStatus(NormalStatus, NormalBox, _host.Hotkeys.Normal);
        var fixedAll = _host.FixedAll;
        foreach (var slot in _slots)
        {
            ShowHotkeyStatus(slot.Status, slot.Box, _host.Hotkeys.Fixed(slot.Slot));
            var f = fixedAll[slot.Slot - 1];
            var name = f.State switch
            {
                FixedTemplateState.Found => f.Template!.Name,
                FixedTemplateState.Missing => Strings.SettingsFixedSlotMissing,
                _ => Strings.SettingsFixedSlotUnset,
            };
            slot.Label.Text = string.Format(Strings.SettingsFixedSlotFormat, slot.Slot, name);
            slot.Label.ToolTip = slot.Label.Text;
            // 設定を保存できない間は、固定の解除・自動貼り付けの変更をさせない。
            slot.Unpin.IsEnabled = f.State != FixedTemplateState.NotSet && !_host.SettingsBlocked;
            slot.AutoPaste.IsEnabled = !_host.SettingsBlocked;
        }
        ImportButton.IsEnabled = ImportAppendButton.IsEnabled = RestoreButton.IsEnabled = !_host.TemplatesBlocked;
        RecoveryButton.IsEnabled = _host.TemplatesBlocked || _host.SettingsBlocked;
        Editor.UpdateStatus();
    }

    private static readonly Brush StatusOk = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));
    private static readonly Brush StatusPlain = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    /// <summary>
    /// 入力欄の下に登録の結果を常に表示する（入力中・登録しました・登録できませんでした・未設定）。
    /// もう一方の欄の入力中に一時解除しているだけのときは何も出さない。
    /// </summary>
    private void ShowHotkeyStatus(TextBlock target, ShortcutBox box, HotkeyRegistration r)
    {
        (target.Text, target.Foreground) = _capturing.Contains(box) ? (Strings.HotkeyStatusCapturing, StatusPlain)
            : r.Status switch
            {
                HotkeyStatus.Registered => (Strings.HotkeyStatusRegistered, StatusOk),
                HotkeyStatus.Failed => (r.Describe(), Brushes.Firebrick),
                HotkeyStatus.Unassigned => (Strings.HotkeyUnassigned, StatusPlain),
                _ => ("", StatusPlain),
            };
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var current = _host.Settings;
            NormalBox.Shortcut = current.NormalShortcut;
            foreach (var slot in _slots)
            {
                slot.Box.Shortcut = current.Slot(slot.Slot).Shortcut;
                slot.AutoPaste.IsChecked = current.Slot(slot.Slot).AutoPaste;
            }
            NormalAutoPasteBox.IsChecked = current.NormalAutoPaste;
            StartupNoticeBox.IsChecked = current.StartupNotice;
            var placements = PickerPlacements.All.Select(v => new PlacementOption(v, PlacementName(v))).ToList();
            PlacementBox.ItemsSource = placements;
            PlacementBox.SelectedItem = placements.FirstOrDefault(o => o.Value == current.PickerPlacement) ?? placements[0];
            var languages = UiLanguage.All.Select(v => new LanguageOption(v, UiLanguage.DisplayName(v))).ToList();
            LanguageBox.ItemsSource = languages;
            LanguageBox.SelectedItem = languages.FirstOrDefault(o => o.Value == UiLanguage.Normalize(current.Language)) ?? languages[0];
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- ホットキー ----

    private void EndCapture(ShortcutBox box)
    {
        if (!_capturing.Remove(box)) return;
        _host.Hotkeys.Resume();
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        var current = _host.Settings;
        if (NormalBox.Shortcut == current.NormalShortcut && _slots.All(x => x.Box.Shortcut == current.Slot(x.Slot).Shortcut)) return;
        var problem = AllBoxes.Select(b => Check(b.Shortcut)).FirstOrDefault(p => p is not null);
        // 通常と固定スロット 1〜3 の 4 本は、設定したものどうしで同じ組み合わせにできない。
        var assigned = AllBoxes.Select(b => b.Shortcut).Where(x => !x.IsEmpty).ToList();
        if (problem is null && assigned.Distinct().Count() != assigned.Count) problem = Strings.SettingsDuplicateHotkey;
        if (problem is null)
        {
            var updated = current.Clone();
            updated.NormalHotkey = NormalBox.Shortcut.ToString();
            foreach (var slot in _slots) updated.Slot(slot.Slot).Hotkey = slot.Box.Shortcut.ToString();
            problem = NullIfEmpty(_host.ApplySettings(updated));
        }
        if (problem is not null) RevertWith(problem);
        else ClearError();
    }

    private static string? Check(Shortcut shortcut)
    {
        if (shortcut.IsEmpty) return null;
        var result = shortcut.Validate();
        return result == ShortcutValidation.Ok ? null : $"{ShortcutDisplay.Format(shortcut)}: {Shortcut.Describe(result)}";
    }

    // ---- 固定スロット ----

    private void OnSlotAutoPasteChanged(int slot)
    {
        if (_loading) return;
        var updated = _host.Settings.Clone();
        updated.Slot(slot).AutoPaste = _slots[slot - 1].AutoPaste.IsChecked == true;
        Apply(updated);
    }

    /// <summary>スロットのテンプレートだけを外す（ショートカット・自動貼り付けは残す）。</summary>
    private void Unpin(int slot)
    {
        var error = _host.SetFixed(slot, null);
        if (error.Length > 0) RevertWith(error);
        else ClearError();
        UpdateStatus();
    }

    // ---- 選択画面を開く位置・起動時の通知 ----

    private static string PlacementName(string value) => value switch
    {
        PickerPlacements.Cursor => Strings.PlacementCursor,
        PickerPlacements.Remembered => Strings.PlacementRemembered,
        _ => Strings.PlacementCenter,
    };

    private void OnPlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var updated = _host.Settings.Clone();
        // 「前回の位置」以外へ変えても、覚えている位置は消さない。
        updated.PickerPlacement = (PlacementBox.SelectedItem as PlacementOption)?.Value ?? PickerPlacements.Center;
        Apply(updated);
    }

    private void OnStartupNoticeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var updated = _host.Settings.Clone();
        updated.StartupNotice = StartupNoticeBox.IsChecked == true;
        Apply(updated);
    }

    // ---- 自動貼り付け・表示言語 ----

    private void OnAutoPasteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var updated = _host.Settings.Clone();
        updated.NormalAutoPaste = NormalAutoPasteBox.IsChecked == true;
        Apply(updated);
    }

    private void OnLanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var updated = _host.Settings.Clone();
        updated.Language = (LanguageBox.SelectedItem as LanguageOption)?.Value ?? UiLanguage.System;
        if (Apply(updated)) _host.OfferRestartIfLanguageChanged(this);
    }

    private bool Apply(AppSettings updated)
    {
        var error = _host.ApplySettings(updated);
        if (error.Length > 0)
        {
            RevertWith(error);
            return false;
        }
        ClearError();
        return true;
    }

    /// <summary>反映できなかった。入力欄を保存済みの値に戻して理由を表示する。</summary>
    private void RevertWith(string message)
    {
        LoadSettings();
        ErrorText.Text = message;
    }

    private void ClearError() => ErrorText.Text = "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ---- サインイン時の自動起動 ----

    private bool _startupBusy;

    private async void RefreshStartup()
    {
        if (_startupBusy) return;
        _startupBusy = true;
        try
        {
            ShowStartup(await StartupService.GetStateAsync());
        }
        finally
        {
            _startupBusy = false;
        }
    }

    /// <summary>
    /// チェックの操作。操作中は二重に操作させず、結果は Windows から読み直した状態で表示する
    /// （有効にできなかった場合にチェックだけを付けたままにしない）。
    /// </summary>
    private async void OnStartupClick(object sender, RoutedEventArgs e)
    {
        var wanted = StartupBox.IsChecked == true;
        if (_startupBusy) return;
        _startupBusy = true;
        StartupBox.IsEnabled = false;
        try
        {
            var state = await StartupService.SetEnabledAsync(wanted);
            ShowStartup(state);
            if (wanted && !StartupStates.IsOn(state) && state != StartupState.DisabledByUser) ErrorText.Text = Strings.StartupChangeFailed;
            else if (!wanted && StartupStates.IsOn(state)) ErrorText.Text = Strings.StartupChangeFailed;
            else ClearError();
        }
        finally
        {
            _startupBusy = false;
        }
    }

    private void ShowStartup(StartupState state)
    {
        var view = StartupStates.View(state);
        StartupBox.IsChecked = view.Checked;
        StartupBox.IsEnabled = view.CanChange;
        StartupStatus.Text = view.Status;
        StartupStatus.Visibility = view.Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartupSettingsButton.Visibility = view.ShowWindowsSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenStartupSettings(object sender, RoutedEventArgs e) => _host.OpenUrl(StartupService.WindowsSettingsUri, this);

    // ---- データ・その他 ----

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeaveEditor()) _host.ImportReplace(this);
    }

    private void OnImportAppend(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeaveEditor()) _host.ImportAppend(this);
    }

    private void OnExport(object sender, RoutedEventArgs e) => _host.Export(this);

    private void OnOpenFolder(object sender, RoutedEventArgs e) => _host.OpenDataFolder();

    private void OnRestoreEarlier(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeaveEditor()) _host.RestoreEarlier(this);
    }

    private void OnRecovery(object sender, RoutedEventArgs e)
    {
        if (_host.TemplatesBlocked) _host.OfferTemplateRecovery(this);
        else if (_host.SettingsBlocked) _host.OfferSettingsReset(this);
    }

    private void OnGuide(object sender, RoutedEventArgs e) => _host.ShowGuide();

    private static string LanguageCode => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    private void OnOpenManual(object sender, RoutedEventArgs e) => _host.OpenUrl(AppLinks.Manual(LanguageCode), this);

    private void OnOpenPrivacy(object sender, RoutedEventArgs e) => _host.OpenUrl(AppLinks.Privacy(LanguageCode), this);

    private void OnOpenSource(object sender, RoutedEventArgs e) => _host.OpenUrl(AppLinks.Source, this);

    private void OnShowLicense(object sender, RoutedEventArgs e) => _host.ShowBundledText("LICENSE", Strings.AboutLicenseLink, this);

    private void OnShowNotices(object sender, RoutedEventArgs e) => _host.ShowBundledText("THIRD-PARTY-NOTICES.md", Strings.AboutNoticesLink, this);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
