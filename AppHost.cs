using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using ClipCompose.Core.Composition;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;
using ClipCompose.Core.Storage;
using ClipCompose.Services;
using ClipCompose.Views;
using Microsoft.Win32;

namespace ClipCompose;

/// <summary>
/// 起動・常駐・画面・実行の統合（仕様書 §3〜§11）。
/// - テンプレート（templates.json）と PC ごとの設定（settings.json）を別々に読み、どちらかが読めなくても他方は使う。
/// - 実行は保存済みのライブラリから ID で引き直したテンプレートだけを対象にする。
/// - ホットキーからの実行だけが自動貼り付けの対象。トレイからの実行は常にコピーのみ。
/// - 起動時は画面を出さずトレイに常駐する。画面は選択画面と設定画面（テンプレートの編集を含む）の 2 つ。
/// </summary>
public sealed class AppHost : IDisposable
{
    private const int VK_RETURN = 0x0D;

    private readonly App _app;
    private readonly CultureInfo _windowsUiCulture;
    private readonly TemplateStore _templates;
    private readonly SettingsStore _settingsStore;
    private CultureInfo _uiCulture;
    private AppSettings _settings = new();
    private string _templatesProblem = "";
    private string _settingsProblem = "";
    private ClipboardService? _clipboard;
    private ComposeRunner? _runner;
    private TrayService? _tray;
    private PickerWindow? _picker;
    private SettingsWindow? _settingsWindow;
    private SettingsTab _lastSettingsTab = SettingsTab.Templates;
    private bool _exiting;
    private bool _disposed;

    public AppHost(App app, CultureInfo windowsUiCulture)
    {
        _app = app;
        _windowsUiCulture = windowsUiCulture;
        _uiCulture = windowsUiCulture;
        var directory = PackageInfo.DataDirectory;
        _templates = new TemplateStore(directory);
        _settingsStore = new SettingsStore(directory);
    }

    /// <summary>アプリの版（csproj の Version。3 桁）。</summary>
    public static string VersionText
    {
        get
        {
            var v = typeof(AppHost).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>保存済みのライブラリ（実行・一覧の対象）。</summary>
    public TemplateLibrary Library { get; private set; } = TemplateLibrary.Empty;

    public HotkeyService Hotkeys { get; private set; } = null!;

    public FixedTemplate Fixed => FixedTemplate.Resolve(_settings.FixedTemplateId, Library);

    public bool TemplatesBlocked => _templates.SaveBlocked;

    public bool SettingsBlocked => _settingsStore.SaveBlocked;

    /// <summary>保存済みの設定（読むだけに使う。変えるときは Clone して <see cref="ApplySettings"/> へ）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>テンプレート・設定の保存先のフォルダー。</summary>
    public string DataDirectory => _templates.Directory;

    public void Start()
    {
        // 設定を読む前に出る可能性のあるメッセージは Windows の表示言語で出す。
        UiLanguage.Apply(UiLanguage.Resolve(UiLanguage.System, _windowsUiCulture));
        var settingsLoad = _settingsStore.Load();
        _settings = settingsLoad.Settings;
        _settingsProblem = settingsLoad.Problem;
        foreach (var fix in settingsLoad.Fixes) ErrorLog.Write($"Setting corrected: {fix}");
        if (settingsLoad.Kind == LoadKind.Failed) ErrorLog.Write("The settings file could not be read; running with defaults and not saving settings");
        // 画面・トレイを作る前に表示言語を決める。
        _uiCulture = UiLanguage.Resolve(_settings.Language, _windowsUiCulture);
        UiLanguage.Apply(_uiCulture);

        var templateLoad = _templates.Load();
        Library = templateLoad.Library;
        _templatesProblem = templateLoad.Problem;
        if (templateLoad.Kind == LoadKind.Failed) ErrorLog.Write("The template file could not be read; saving is stopped");

        _clipboard = new ClipboardService();
        _runner = new ComposeRunner(_clipboard, new PasteService());
        Hotkeys = new HotkeyService();
        Hotkeys.Pressed += OnHotkey;
        Hotkeys.StatusChanged += (_, _) => RefreshStatus();
        Hotkeys.Apply(_settings.NormalShortcut, _settings.FixedShortcut);

        _tray = new TrayService(() => new TrayState(Fixed, Hotkeys.Normal, Hotkeys.Fixed));
        _tray.PickerRequested += (_, _) => OpenPicker(null);
        _tray.RunFixedRequested += (_, _) => RunFixed(null);
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => Exit();
        _tray.UpdateTooltip();

        // 起動時は画面を出さない（コピー前で選ぶものが無いため）。読み込みの失敗と初回だけ設定画面を開く。
        if (settingsLoad.Kind == LoadKind.Failed) OfferSettingsReset(ShowSettings(SettingsTab.General));
        if (templateLoad.Kind == LoadKind.Failed) OfferTemplateRecovery(ShowSettings(SettingsTab.Templates));
        if (settingsLoad.Kind == LoadKind.Missing) ShowFirstRunNotice();
        else if (settingsLoad.Kind != LoadKind.Failed && templateLoad.Kind != LoadKind.Failed)
            ToastWindow.ShowInfo(Strings.ToastStartedTitle, Strings.ToastStartedDetail);
    }

    public void Dispose()
    {
        if (_disposed) return; // 通常の終了と、異常終了時の後始末の両方から呼ばれる
        _disposed = true;
        Hotkeys?.Dispose();
        _tray?.Dispose();
        _clipboard?.Dispose();
    }

    /// <summary>2 回目の起動の通知を受けた。選択画面を表示する（貼り付け先を特定できないためコピーのみ）。</summary>
    public void NotifyAlreadyRunning() => OpenPicker(null);

    /// <summary>想定外の例外の後で、開いている選択画面を閉じる（実行中の状態は RunBegunAsync の finally で戻る）。</summary>
    public void RecoverFromError() => _picker?.Close();

    // ---- 画面 ----

    /// <summary>
    /// 設定画面を開く（開いていれば前面に出す）。モーダルにしない（常駐中の選択画面・トレイを止めない）。
    /// <paramref name="tab"/> を省くと、この起動中に最後に見ていたタブ（最初は［テンプレート］）で開く。
    /// </summary>
    public SettingsWindow ShowSettings(SettingsTab? tab = null)
    {
        if (_settingsWindow is null)
        {
            var window = new SettingsWindow(this);
            window.ShowTab(_lastSettingsTab);
            window.Closed += (_, _) =>
            {
                _lastSettingsTab = window.CurrentTab;
                if (_settingsWindow == window) _settingsWindow = null;
            };
            _settingsWindow = window;
        }
        if (tab is { } t) _settingsWindow.ShowTab(t);
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        return _settingsWindow;
    }

    /// <summary>使い方（仕様書 §8 の履歴・保護指定の注意を含む）。</summary>
    public void ShowGuide() => ShowText(new TextWindow(Strings.GuideTitle, Strings.GuideText));

    /// <summary>exe と同じフォルダーに同梱した文章（LICENSE・THIRD-PARTY-NOTICES.md）を表示する。読めなければフルパスを示す。</summary>
    public void ShowBundledText(string fileName, string title, Window owner)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            ShowText(new TextWindow(title, File.ReadAllText(path), monospace: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"Could not read {path}", ex);
            ShowMessage(string.Format(Strings.BundledFileUnreadableFormat, path), MessageBoxImage.Warning, owner);
        }
    }

    /// <summary>Web ページ・公開リポジトリを既定のブラウザーで開く。</summary>
    public void OpenUrl(string url, Window owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("Could not open a web page", ex);
            ShowMessage(string.Format(Strings.OpenUrlFailedFormat, url), MessageBoxImage.Warning, owner);
        }
    }

    private void ShowText(Window window)
    {
        if (_settingsWindow is { IsVisible: true }) window.Owner = _settingsWindow;
        window.Show();
    }

    public void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_templates.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_templates.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write("Could not open the data folder", ex);
            ShowMessage($"{_templates.Directory}\n\n{ex.Message}", MessageBoxImage.Warning);
        }
    }

    /// <summary>明示的な終了。未保存の編集があれば保存・破棄・キャンセルを選ばせる。</summary>
    public void Exit()
    {
        if (_exiting) return;
        if (_settingsWindow is not null && !_settingsWindow.ConfirmLeaveEditor()) return;
        ShutdownNow();
    }

    private void ShutdownNow()
    {
        _exiting = true;
        _picker?.Close();
        _settingsWindow?.CloseWithoutConfirm();
        // ホットキー・トレイの解放は App.OnExit → Dispose で行う。
        _app.Shutdown();
    }

    /// <summary>
    /// 設定ファイルが無い初回だけ。設定画面を［ホットキーと動作］タブで開いて案内し、トレイに常駐していることを通知する。
    /// 案内したことを残すため、既定の設定を保存しておく。
    /// </summary>
    private void ShowFirstRunNotice()
    {
        var updated = _settings.Clone();
        updated.TrayNoticeShown = true;
        var result = _settingsStore.Save(updated);
        if (result.Succeeded) _settings = updated;
        else ErrorLog.Write("Could not create the settings file on first run");
        ShowSettings(SettingsTab.Hotkeys).ShowFirstRunNote();
        _tray?.ShowBalloon(Strings.TrayNoticeTitle, Strings.TrayNotice);
    }

    private void RefreshStatus()
    {
        _tray?.UpdateTooltip();
        _settingsWindow?.Refresh();
    }

    // ---- 編集・設定の保存 ----

    /// <summary>
    /// ライブラリを編集して保存する。保存に成功した場合だけ実行対象を差し替える。失敗の理由（成功なら空文字）。
    /// </summary>
    public string Commit(Func<TemplateLibrary, LibraryEdit> edit, out Guid id, bool category = false)
    {
        id = default;
        if (TemplatesBlocked) return string.Format(Strings.SaveBlockedFormat, _templates.FilePath);
        var result = edit(Library);
        if (!result.Succeeded) return TemplateRules.Describe(result.Error, category);
        var saved = _templates.Save(result.Library!);
        if (!saved.Succeeded)
        {
            ErrorLog.Write("Saving templates failed");
            return saved.Problem;
        }
        Library = result.Library!;
        id = result.Id;
        RefreshStatus();
        return "";
    }

    /// <summary>固定テンプレートを変える（null は解除）。失敗の理由（成功なら空文字）。</summary>
    public string SetFixed(Guid? id)
    {
        var updated = _settings.Clone();
        updated.FixedTemplateId = id;
        var result = _settingsStore.Save(updated);
        if (!result.Succeeded) return result.Problem;
        _settings = updated;
        RefreshStatus();
        return "";
    }

    /// <summary>テンプレートをカテゴリ内で動かして保存する（選択画面・設定画面の並べ替え）。失敗の理由（成功なら空文字）。</summary>
    public string MoveTemplate(Guid id, int offset) => Commit(l => l.MoveTemplate(id, offset), out _);

    /// <summary>カテゴリを動かして保存する。失敗の理由（成功なら空文字）。</summary>
    public string MoveCategory(Guid id, int offset) => Commit(l => l.MoveCategory(id, offset), out _, category: true);

    /// <summary>選択画面の本文パネルの開閉を覚える。保存できなくても、この起動中は覚えておく（記録だけ残す）。</summary>
    public void SetPickerPreview(bool visible)
    {
        if (_settings.PickerPreview == visible) return;
        var updated = _settings.Clone();
        updated.PickerPreview = visible;
        if (!_settingsStore.Save(updated).Succeeded) ErrorLog.Write("Could not save whether the preview pane is open");
        _settings = updated;
    }

    /// <summary>
    /// 選択画面で閉じているカテゴリを覚える。無くなったカテゴリの ID は除く（テンプレートを読めていないときは除かない）。
    /// 保存できなくても、この起動中は覚えておく。失敗の理由（成功なら空文字）。
    /// </summary>
    public string SetCollapsedCategories(IEnumerable<Guid> ids)
    {
        var known = TemplatesBlocked ? null : Library.Categories.Select(c => c.Id).ToHashSet();
        var updated = _settings.Clone();
        updated.CollapsedCategoryIds = [.. ids.Where(id => known is null || known.Contains(id)).Distinct()];
        if (updated.CollapsedCategoryIds.SequenceEqual(_settings.CollapsedCategoryIds)) return "";
        var result = _settingsStore.Save(updated);
        _settings = updated;
        if (result.Succeeded) return "";
        ErrorLog.Write("Could not save which categories are collapsed");
        return result.Problem;
    }

    /// <summary>
    /// カテゴリを中のテンプレートごと削除する（設定画面で 2 回確かめた後）。削除の前のデータを日時付きの名前（before-delete）で残す。
    /// 固定のテンプレートが含まれる場合は、先に固定の解除を保存する（できなければ削除しない）。失敗の理由（成功なら空文字）。
    /// </summary>
    public string DeleteCategoryWithTemplates(Guid id)
    {
        if (TemplatesBlocked) return string.Format(Strings.SaveBlockedFormat, _templates.FilePath);
        var edit = Library.DeleteCategoryWithTemplates(id);
        if (!edit.Succeeded) return TemplateRules.Describe(edit.Error, category: true);
        var removed = Library.TemplatesIn(id).Select(t => t.Id).ToHashSet();
        var outcome = ImportProcedure.SaveRemoving(_templates, _settingsStore, _settings, edit.Library!, removed, "before-delete");
        _settings = outcome.Settings;
        if (outcome.Succeeded) Library = edit.Library!;
        else ErrorLog.Write("Deleting a category with its templates failed");
        RefreshStatus();
        return outcome.Problem;
    }

    /// <summary>
    /// 設定画面での変更。ホットキーの登録と保存に成功した場合だけ反映する。失敗したら以前のホットキーに戻し、理由を返す。
    /// </summary>
    public string ApplySettings(AppSettings updated)
    {
        var previous = _settings;
        var hotkeysChanged = updated.NormalShortcut != previous.NormalShortcut || updated.FixedShortcut != previous.FixedShortcut;
        if (hotkeysChanged && !Hotkeys.TryChange(updated.NormalShortcut, updated.FixedShortcut, out var failure))
            return string.Format(Strings.SettingsHotkeyFailedFormat, failure!.Describe());
        var result = _settingsStore.Save(updated);
        if (!result.Succeeded)
        {
            if (hotkeysChanged) Hotkeys.Apply(previous.NormalShortcut, previous.FixedShortcut);
            return result.Problem;
        }
        _settings = updated;
        RefreshStatus();
        return "";
    }

    // ---- 実行 ----

    private void OnHotkey(HotkeyMode mode, Shortcut shortcut)
    {
        if (_exiting) return;
        var target = PasteService.CaptureTarget(shortcut);
        if (mode == HotkeyMode.Normal) OpenPicker(target, fromHotkey: true);
        else RunFixed(_settings.FixedAutoPaste ? target : null);
    }

    /// <summary>
    /// 選択画面を開く。開いていれば再利用する（多重化しない）。実行中は開かない。
    /// </summary>
    /// <param name="target">ホットキー押下時の貼り付け先。null ならコピーのみ。</param>
    private void OpenPicker(PasteTarget? target, bool fromHotkey = false)
    {
        if (_exiting || _runner is null) return;
        if (_picker is not null)
        {
            _picker.Activate();
            return;
        }
        if (Library.Templates.Count == 0)
        {
            // 選ぶものが無い。テンプレートを作る画面を開く。
            if (TemplatesBlocked) ToastWindow.ShowWarning(Strings.ToastSaveBlockedTitle, Strings.StatusSaveBlocked);
            else ToastWindow.ShowWarning(Strings.ToastNoTemplatesTitle, Strings.ToastNoTemplatesDetail);
            ShowSettings(SettingsTab.Templates);
            return;
        }
        if (!_runner.TryBegin()) return; // 実行中の要求は捨てる
        var autoPaste = fromHotkey && _settings.NormalAutoPaste && target is not null;
        var picker = new PickerWindow(this, autoPaste);
        picker.SettingsRequested += () => ShowSettings(SettingsTab.Templates);
        picker.Confirmed += async (chosen, byEnter) =>
        {
            _picker = null;
            // 確定した時点の保存済みの内容を ID で引き直す。
            if (Library.FindTemplate(chosen.Id) is not { } template)
            {
                _runner.Cancel();
                ToastWindow.ShowWarning(Strings.ErrorTemplateNotFound, "");
                return;
            }
            // 選択画面を閉じたことで前面が ClipCompose 側に移った場合だけ、貼り付け先へ戻してよい。
            PasteTarget? pasteTarget = autoPaste ? target!.Value with { TriggerKey = byEnter ? VK_RETURN : 0, RestoreFocus = true } : null;
            Notify(await _runner.RunBegunAsync(template, pasteTarget));
        };
        picker.Canceled += () =>
        {
            _picker = null;
            _runner.Cancel();
        };
        _picker = picker;
        picker.Show();
        picker.Activate();
    }

    /// <summary>固定テンプレートを選択画面なしで実行する。</summary>
    private async void RunFixed(PasteTarget? target)
    {
        if (_exiting || _runner is null) return;
        var f = Fixed;
        switch (f.State)
        {
            case FixedTemplateState.NotSet:
                ToastWindow.ShowWarning(Strings.ToastFixedNotSetTitle, Strings.ToastFixedNotSetDetail);
                return;
            case FixedTemplateState.Missing:
                ToastWindow.ShowWarning(Strings.ToastFixedMissingTitle, Strings.ToastFixedMissingDetail);
                return;
        }
        try
        {
            Notify(await _runner.RunAsync(f.Template!, target));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Running the pinned template failed", ex);
            ToastWindow.ShowWarning(Strings.ToastFailedTitle, Strings.FailWrite);
        }
    }

    /// <summary>実行結果の通知（フォーカスを奪わない）。「貼り付け完了」とは断定しない。</summary>
    private static void Notify(RunOutcome outcome)
    {
        switch (outcome.Status)
        {
            case RunStatus.Copied:
                ToastWindow.ShowInfo(Strings.ToastCopiedTitle, Strings.ToastCopiedDetail);
                break;
            case RunStatus.PasteSent:
                ToastWindow.ShowInfo(Strings.ToastPasteSentTitle, Strings.ToastPasteSentDetail);
                break;
            case RunStatus.CopiedPasteSkipped:
                ToastWindow.ShowWarning(Strings.ToastPasteSkippedTitle, $"{PasteSkipText(outcome.PasteSkip)} {Strings.ToastCopiedDetail}");
                break;
            case RunStatus.ClipboardChangedAfterCopy:
                ToastWindow.ShowWarning(Strings.ToastClipboardChangedTitle, Strings.ToastChangedAfterCopyDetail);
                break;
            case RunStatus.ClipboardChangedBeforeCopy:
                ToastWindow.ShowWarning(Strings.ToastClipboardChangedTitle, Strings.ToastChangedBeforeCopyDetail);
                break;
            case RunStatus.AlreadyComposed:
                ToastWindow.ShowWarning(Strings.ToastAlreadyComposedTitle, Strings.ToastAlreadyComposedDetail);
                break;
            case RunStatus.InputRejected:
                ToastWindow.ShowWarning(Strings.ToastInputRejectedTitle, ComposeErrorText(outcome.ComposeError));
                break;
            case RunStatus.Failed:
                ToastWindow.ShowWarning(Strings.ToastFailedTitle, outcome.Failure switch
                {
                    RunFailure.ClipboardBusy => Strings.FailClipboardBusy,
                    RunFailure.ProtectionUnavailable => Strings.FailProtection,
                    _ => Strings.FailWrite,
                });
                break;
            case RunStatus.Busy:
                break; // 実行中に重ねた要求は通知せずに捨てる
        }
    }

    private static string PasteSkipText(PasteSkipReason reason) => reason switch
    {
        PasteSkipReason.TargetElevated => Strings.PasteSkipElevated,
        PasteSkipReason.FocusNotRestored => Strings.PasteSkipFocus,
        PasteSkipReason.KeysHeld => Strings.PasteSkipKeys,
        PasteSkipReason.SendFailed => Strings.PasteSkipSend,
        _ => Strings.PasteSkipTargetGone,
    };

    private static string ComposeErrorText(ComposeError error) => error switch
    {
        ComposeError.InputMissing => Strings.InputMissing,
        ComposeError.InputEmpty => Strings.InputEmpty,
        ComposeError.InputContainsNul => Strings.InputContainsNul,
        ComposeError.InputTooLong => string.Format(Strings.InputTooLongFormat, Composer.MaxScalars.ToString("N0")),
        ComposeError.ResultTooLong => string.Format(Strings.ResultTooLongFormat, Composer.MaxScalars.ToString("N0")),
        _ => Strings.TemplateInvalid,
    };

    // ---- インポート・エクスポート・復旧 ----

    /// <summary>ファイルを選んで全件検証する。キャンセル・失敗なら null（失敗の理由は表示済み）。</summary>
    private TemplateLibrary? ChooseImportFile(Window owner)
    {
        if (TemplatesBlocked)
        {
            ShowMessage(string.Format(Strings.SaveBlockedFormat, _templates.FilePath), MessageBoxImage.Warning, owner);
            return null;
        }
        var dialog = new OpenFileDialog { Filter = Strings.FileDialogFilter, CheckFileExists = true };
        if (dialog.ShowDialog(owner) != true) return null;
        // 選ばなかったカテゴリも含め、ファイル全体を検証する（不正なカテゴリだけを黙って読み飛ばさない）。
        var parsed = _templates.ReadImport(dialog.FileName);
        if (!parsed.Succeeded)
        {
            ShowMessage(parsed.Problem, MessageBoxImage.Warning, owner);
            return null;
        }
        return parsed.Library!;
    }

    /// <summary>
    /// 追加インポート（仕様書 §11）。ファイルから選んだカテゴリを、既存のデータを残して取り込む。
    /// 現在のデータを before-import として残してから、結合した全体を一度だけ保存する。未保存の確認は呼び出し側で済ませている。
    /// </summary>
    public void ImportAppend(Window owner)
    {
        if (ChooseImportFile(owner) is not { } file) return;
        if (file.Categories.Count == 0)
        {
            ShowMessage(Strings.ImportNoCategories, MessageBoxImage.Information, owner);
            return;
        }
        var current = Library;
        var choices = file.Categories
            .Select(c => new CategoryChoice(c, file.CountIn(c.Id), LibraryMerge.SameName(current, c.Name) is not null, forImport: true))
            .ToList();
        var chosen = CategorySelectWindow.Ask(owner, Strings.ImportSelectPrompt, Strings.ImportSelectNote, Strings.ImportSelectOk, choices,
            all => ShowMergePlan(current, file, all));
        if (chosen is null) return;
        // 選んでいる間に保存済みのデータが変わっていても、最新の内容に対して組み立て直す。
        var plan = LibraryMerge.Plan(Library, file, chosen.ToDictionary(c => c.Category.Id, c => c.Mode));
        var merged = LibraryMerge.Apply(Library, file, plan);
        var outcome = ImportProcedure.SaveRemoving(_templates, _settingsStore, _settings, merged.Library, merged.RemovedTemplateIds, "before-import");
        _settings = outcome.Settings;
        if (!outcome.Succeeded)
        {
            RefreshStatus();
            ShowMessage(outcome.Problem, MessageBoxImage.Warning, owner);
            return;
        }
        Library = merged.Library;
        _settingsWindow?.ReloadAfterReplace();
        RefreshStatus();
        var added = plan.Count(p => p.Mode == MergeMode.AddAsNew);
        var replaced = plan.Count - added;
        ShowMessage(string.Format(Strings.ImportAppendDoneFormat, added, replaced, plan.Sum(p => p.TemplateCount)), MessageBoxImage.Information, owner);
    }

    /// <summary>選択画面の各行に、取り込んだ後の名前・取り込み方を表示する。</summary>
    private static void ShowMergePlan(TemplateLibrary current, TemplateLibrary file, IReadOnlyList<CategoryChoice> all)
    {
        var plan = LibraryMerge.Plan(current, file, all.Where(c => c.IsChecked).ToDictionary(c => c.Category.Id, c => c.Mode))
            .ToDictionary(p => p.Source.Id);
        foreach (var choice in all)
        {
            choice.ResultText = !plan.TryGetValue(choice.Category.Id, out var step) ? ""
                : step.Mode == MergeMode.ReplaceContents ? string.Format(Strings.ImportResultReplaceFormat, step.ResultName)
                : step.ResultName == choice.Name ? string.Format(Strings.ImportResultAddFormat, step.ResultName)
                : string.Format(Strings.ImportResultRenamedFormat, step.ResultName);
        }
    }

    /// <summary>全件置換で復元（ファイルの内容ですべてを置き換える）。未保存の確認は呼び出し側で済ませている。</summary>
    public void ImportReplace(Window owner)
    {
        if (ChooseImportFile(owner) is not { } imported) return;
        var confirm = string.Format(Strings.ImportConfirmFormat,
            Library.Categories.Count, Library.Templates.Count, imported.Categories.Count, imported.Templates.Count);
        if (MessageBox.Show(owner, confirm, "ClipCompose", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        // 置換後は固定を解除する（同じ ID が含まれていても）。解除を先に保存し、失敗したら置き換えない。
        var outcome = ImportProcedure.Replace(_templates, _settingsStore, _settings, imported);
        _settings = outcome.Settings;
        if (!outcome.Succeeded)
        {
            RefreshStatus();
            ShowMessage(outcome.Problem, MessageBoxImage.Warning, owner);
            return;
        }
        Library = imported;
        _settingsWindow?.ReloadAfterReplace();
        RefreshStatus();
        ShowMessage(string.Format(Strings.ImportDoneFormat, imported.Categories.Count, imported.Templates.Count), MessageBoxImage.Information, owner);
    }

    /// <summary>
    /// 選んだカテゴリを 1 つのファイルにエクスポートする（保存済みの内容。PC ごとの設定は含めない）。
    /// 複数のカテゴリを選んでもファイルは分けない。
    /// </summary>
    public void Export(Window owner)
    {
        if (TemplatesBlocked)
        {
            ShowMessage(string.Format(Strings.SaveBlockedFormat, _templates.FilePath), MessageBoxImage.Warning, owner);
            return;
        }
        var library = Library;
        if (library.Categories.Count == 0)
        {
            ShowMessage(Strings.ExportNoCategories, MessageBoxImage.Information, owner);
            return;
        }
        var choices = library.Categories.Select(c => new CategoryChoice(c, library.CountIn(c.Id), hasSameName: false, forImport: false)).ToList();
        var chosen = CategorySelectWindow.Ask(owner, Strings.ExportSelectPrompt, Strings.ExportSelectNote, Strings.ExportSelectOk, choices);
        if (chosen is null) return;
        var subset = library.Subset(chosen.Select(c => c.Category.Id));
        var dialog = new SaveFileDialog
        {
            Filter = Strings.FileDialogFilter,
            FileName = ExportFileName.Suggest([.. subset.Categories.Select(c => c.Name)], DateTime.Now),
            AddExtension = true,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(owner) != true) return;
        // アプリが管理するファイル（templates.json・settings.json・バックアップ・ログ）を上書きしない。
        if (ExportFileName.IsInDataDirectory(dialog.FileName, _templates.Directory))
        {
            ShowMessage(Strings.ExportIntoDataFolder, MessageBoxImage.Warning, owner);
            return;
        }
        var result = _templates.Export(dialog.FileName, subset);
        ShowMessage(result.Succeeded
                ? string.Format(Strings.ExportDoneFormat, dialog.FileName) + Environment.NewLine + Environment.NewLine
                    + string.Format(Strings.ExportCountFormat, subset.Categories.Count, subset.Templates.Count)
                : result.Problem,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning, owner);
    }

    /// <summary>テンプレートのファイルを読めなかった場合の復旧方法（利用者が選んだ場合だけ実行する）。</summary>
    public void OfferTemplateRecovery(Window owner)
    {
        if (!TemplatesBlocked) return;
        var backup = _templates.ReadBackup();
        var backupText = backup.Succeeded
            ? string.Format(Strings.RecoveryBackupOkFormat, backup.Library!.Categories.Count, backup.Library.Templates.Count)
            : string.Format(Strings.RecoveryBackupProblemFormat, backup.Problem);
        var message = string.Format(Strings.RecoveryMessageFormat, _templatesProblem) + "\n\n" + backupText + "\n\n" + Strings.RecoveryNote;
        var choice = ChoiceDialog.Ask(owner, message,
            [(Strings.RecoveryRestoreBackup, backup.Succeeded), (Strings.RecoveryStartEmpty, true), (Strings.RecoveryKeep, true)], cancelIndex: 2);
        if (choice == 2) return;
        var library = choice == 0 ? backup.Library! : TemplateLibrary.Empty;
        var result = choice == 0 ? _templates.RestoreFrom(library) : _templates.StartEmpty();
        if (!result.Succeeded)
        {
            ShowMessage(result.Problem, MessageBoxImage.Warning, owner);
            return;
        }
        Library = library;
        _templatesProblem = "";
        _settingsWindow?.ReloadAfterReplace();
        RefreshStatus();
        ShowMessage(Strings.RecoveryDone, MessageBoxImage.Information, owner);
    }

    /// <summary>設定ファイルを読めなかった場合に、退避して既定値で始めるかを尋ねる。</summary>
    public void OfferSettingsReset(Window owner)
    {
        if (!SettingsBlocked) return;
        var answer = MessageBox.Show(owner, string.Format(Strings.SettingsLoadFailedFormat, _settingsProblem), "ClipCompose",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var result = _settingsStore.ResetMovingAside(_settings);
        if (!result.Succeeded) ShowMessage(result.Problem, MessageBoxImage.Warning, owner);
        RefreshStatus();
    }

    // ---- 表示言語 ----

    /// <summary>
    /// 設定の保存後、実際の表示言語が変わる場合だけ再起動を尋ねる（選んだ言語で尋ねる）。
    /// </summary>
    public void OfferRestartIfLanguageChanged(Window? owner)
    {
        var wanted = UiLanguage.Resolve(_settings.Language, _windowsUiCulture);
        if (wanted.Name == _uiCulture.Name) return;
        var message = UiLanguage.Get(nameof(Strings.RestartPrompt), wanted);
        var answer = owner is null
            ? MessageBox.Show(message, "ClipCompose", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(owner, message, "ClipCompose", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;
        // 通常の終了と同じく、未保存の編集は保存・破棄・キャンセルを選ばせる（キャンセルなら再起動しない。言語は次回から）。
        if (_settingsWindow is not null && !_settingsWindow.ConfirmLeaveEditor()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("The executable path is not available.");
            Process.Start(new ProcessStartInfo(exe, App.RestartArgument) { UseShellExecute = false });
            ErrorLog.Write($"Restarting to change the display language: {_uiCulture.Name} → {wanted.Name}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write("Restart failed", ex);
            ShowMessage(UiLanguage.Get(nameof(Strings.RestartFailed), wanted), MessageBoxImage.Warning);
            return;
        }
        ShutdownNow();
    }

    private void ShowMessage(string text, MessageBoxImage image, Window? owner = null)
    {
        owner ??= _settingsWindow is { IsVisible: true } ? _settingsWindow : null;
        if (owner is null) MessageBox.Show(text, "ClipCompose", MessageBoxButton.OK, image);
        else MessageBox.Show(owner, text, "ClipCompose", MessageBoxButton.OK, image);
    }
}
