using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

public enum SettingsTab
{
    Templates,
    Hotkeys,
    General,
    About,
}

/// <summary>
/// 設定画面（仕様書 §10）。タブは テンプレート / ホットキーと動作 / 全般 / バージョン情報（一番右。版と © ja1bus）。
/// - テンプレートの編集は［保存］で確定する（<see cref="TemplateEditor"/>）。
/// - ホットキー・自動貼り付け・表示言語は変更した時点で反映・保存する。ホットキーは入力欄から離れた時点で登録し、
///   登録・保存に失敗したら以前の値に戻して理由を表示する。
/// - ×は画面を閉じるだけ（常駐は続ける）。未保存の編集があれば保存・破棄・キャンセルを尋ねる。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly HashSet<ShortcutBox> _capturing = [];
    private bool _loading;
    private bool _closeWithoutConfirm;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = string.Format(Strings.VersionFormat, AppHost.VersionText);
        DataFolderText.Text = string.Format(Strings.SettingsDataFolderFormat, host.DataDirectory);
        Editor.Attach(host);
        LoadSettings();
        UpdateStatus();
        // 自動起動の状態は Windows 側で変わることがあるため、画面を前面に戻すたびに読み直す（定期的には読まない）。
        Activated += (_, _) => RefreshStartup();

        // 入力中は登録済みのホットキーを解除し、今のキーを押しても実行されないようにする。入力欄から離れたら登録する。
        foreach (var box in new[] { NormalBox, FixedBox })
        {
            box.CaptureStarted += (_, _) =>
            {
                if (_capturing.Add(box)) _host.Hotkeys.Suspend();
                UpdateStatus();
            };
            box.CaptureEnded += (_, _) => EndCapture(box);
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
        ShowHotkeyStatus(FixedStatus, FixedBox, _host.Hotkeys.Fixed);
        ImportButton.IsEnabled = ImportAppendButton.IsEnabled = !_host.TemplatesBlocked;
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
            FixedBox.Shortcut = current.FixedShortcut;
            NormalAutoPasteBox.IsChecked = current.NormalAutoPaste;
            FixedAutoPasteBox.IsChecked = current.FixedAutoPaste;
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
        if (NormalBox.Shortcut == current.NormalShortcut && FixedBox.Shortcut == current.FixedShortcut) return;
        var problem = Check(NormalBox.Shortcut) ?? Check(FixedBox.Shortcut);
        if (problem is null && !NormalBox.Shortcut.IsEmpty && NormalBox.Shortcut == FixedBox.Shortcut) problem = Strings.SettingsDuplicateHotkey;
        if (problem is null)
        {
            var updated = current.Clone();
            updated.NormalHotkey = NormalBox.Shortcut.ToString();
            updated.FixedHotkey = FixedBox.Shortcut.ToString();
            problem = NullIfEmpty(_host.ApplySettings(updated));
        }
        if (problem is not null) RevertWith(problem);
        else ClearError();
    }

    private static string? Check(Shortcut shortcut)
    {
        if (shortcut.IsEmpty) return null;
        var result = shortcut.Validate();
        return result == ShortcutValidation.Ok ? null : $"{shortcut}: {Shortcut.Describe(result)}";
    }

    // ---- 自動貼り付け・表示言語 ----

    private void OnAutoPasteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var updated = _host.Settings.Clone();
        updated.NormalAutoPaste = NormalAutoPasteBox.IsChecked == true;
        updated.FixedAutoPaste = FixedAutoPasteBox.IsChecked == true;
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
