using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipCompose.Core.Composition;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Text;

namespace ClipCompose.Views;

/// <summary>
/// 設定画面の［テンプレート］タブ（仕様書 §4・§10）。カテゴリ見出し付きの 1 つの一覧（検索欄付き）と編集欄。
/// - 一覧・実行・固定は保存済みのデータだけを使う。編集中の内容は［保存］に成功するまで実行対象にしない。
/// - 保存に失敗したら編集中の内容と「未保存」の表示を残し、再試行またはキャンセルできるようにする。
/// - ↑↓は、カテゴリの見出しを選んでいればカテゴリを、テンプレートを選んでいればカテゴリ内でテンプレートを動かす（検索中は動かさない）。
/// - ［複製］は保存済みのテンプレートから名前・本文・カテゴリを引き継いだ新規編集を始める。［保存］するまで追加しない。
/// - 検索は一覧を絞るだけで、編集中の内容は捨てない（編集中の項目が一覧から隠れても編集欄は残す）。
/// </summary>
public partial class TemplateEditor : UserControl
{
    private AppHost _host = null!;

    /// <summary>編集中のテンプレート（null で新規、または未選択）。</summary>
    private Template? _editing;
    private bool _editingNew;
    private bool _loadingEditor;
    private bool _restoringSelection;

    public TemplateEditor()
    {
        InitializeComponent();
    }

    public void Attach(AppHost host)
    {
        _host = host;
        Refresh();
        LoadEditor(null, isNew: false);
    }

    public bool IsDirty
    {
        get
        {
            if (_editingNew) return NameBox.Text.Length > 0 || BodyBox.Text.Length > 0;
            if (_editing is null) return false;
            return NameBox.Text != _editing.Name || BodyBox.Text != _editing.Body
                || (EditorCategoryBox.SelectedItem as Category)?.Id != _editing.CategoryId;
        }
    }

    private Window? Owner => Window.GetWindow(this);

    private TreeRow? SelectedRow => List.SelectedItem as TreeRow;

    private string Query => SearchBox.Text.Trim();

    private Template? SelectedTemplate => (List.SelectedItem as TemplateRow)?.Template;

    /// <summary>選んでいる行のカテゴリ（見出しならそのカテゴリ、テンプレートなら所属先）。</summary>
    private Category? SelectedCategory => SelectedRow is { } row ? _host.Library.FindCategory(row.CategoryId) : null;

    /// <summary>
    /// 未保存の変更があれば、保存・破棄・キャンセルを尋ねる。続けてよければ true（保存に失敗した場合は false）。
    /// </summary>
    public bool ConfirmLeaveEditor()
    {
        if (!IsDirty) return true;
        var answer = Owner is { } owner
            ? MessageBox.Show(owner, Strings.UnsavedPrompt, "ClipCompose", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(Strings.UnsavedPrompt, "ClipCompose", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => Discard(),
            _ => false,
        };

        bool Discard()
        {
            LoadEditor(_editingNew ? null : _editing is null ? null : _host.Library.FindTemplate(_editing.Id), isNew: false);
            return true;
        }
    }

    /// <summary>
    /// 保存済みのデータから一覧を作り直す。選択はできるだけ保つ
    /// （<paramref name="selectTemplate"/> を優先し、次に <paramref name="selectCategory"/> の見出し）。
    /// </summary>
    public void Refresh(Guid? selectCategory = null, Guid? selectTemplate = null)
    {
        var library = _host.Library;
        if (selectTemplate is null && selectCategory is null)
        {
            if (SelectedRow is CategoryRow c) selectCategory = c.CategoryId;
            else selectTemplate = SelectedTemplate?.Id ?? (_editingNew ? null : _editing?.Id);
        }
        // 編集中のカテゴリは ID で覚えておく。一覧を差し替えると選択が外れ、カテゴリは名前を含む値なので改名後は一致しないため。
        var editorCategory = (EditorCategoryBox.SelectedItem as Category)?.Id;
        var rows = TreeRows.From(TemplateListView.ForEditor(library, Query), _host.Fixed.Template?.Id, collapsible: false);
        _restoringSelection = true;
        var wasLoading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            List.ItemsSource = rows;
            List.SelectedItem = rows.FirstOrDefault(r => r is TemplateRow t && t.Template.Id == selectTemplate)
                ?? rows.FirstOrDefault(r => r is CategoryRow c && c.CategoryId == selectCategory);
            if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
            EditorCategoryBox.ItemsSource = library.Categories;
            EditorCategoryBox.SelectedItem = library.Categories.FirstOrDefault(c => c.Id == editorCategory);
        }
        finally
        {
            _loadingEditor = wasLoading;
            _restoringSelection = false;
        }
        NoCategoriesText.Visibility = library.Categories.Count == 0 && !_host.TemplatesBlocked ? Visibility.Visible : Visibility.Collapsed;
        NoMatchText.Visibility = rows.Count == 0 && library.Categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 編集中のテンプレートが無くなった（インポート・復旧）場合は編集を終える。
        if (_editing is not null && library.FindTemplate(_editing.Id) is null) LoadEditor(null, isNew: false);
        UpdateStatus();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _host is null) return;
        Refresh();
    }

    /// <summary>
    /// インポート・復旧で全件を置き換えた後。編集欄も保存済みの最新データから読み直す（同じ ID が残っていても古い本文を残さない）。
    /// 置き換えの前に未保存の確認を通しているため、ここで失う編集は無い。
    /// </summary>
    public void ReloadAfterReplace()
    {
        SearchBox.Text = "";
        LoadEditor(_editingNew || _editing is null ? null : _host.Library.FindTemplate(_editing.Id), isNew: false);
        Refresh();
    }

    /// <summary>保存停止の表示とボタンの状態。</summary>
    public void UpdateStatus()
    {
        var messages = new List<string>();
        if (_host.TemplatesBlocked) messages.Add(Strings.StatusSaveBlocked);
        if (_host.SettingsBlocked) messages.Add(Strings.StatusSettingsBlocked);
        BlockedText.Text = string.Join(Environment.NewLine, messages);
        BlockedBanner.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var blocked = _host.TemplatesBlocked;
        var row = SelectedRow;
        var template = SelectedTemplate;
        AddCategoryButton.IsEnabled = !blocked;
        NewTemplateButton.IsEnabled = !blocked && _host.Library.Categories.Count > 0;
        DuplicateButton.IsEnabled = !blocked && template is not null;
        RenameButton.IsEnabled = !blocked && row is CategoryRow;
        DeleteButton.IsEnabled = !blocked && row is not null;
        // 検索で絞っている間は、見えていない項目との位置関係が分からないため並べ替えない。
        UpButton.IsEnabled = DownButton.IsEnabled = !blocked && row is not null && Query.Length == 0;
        PinButton.IsEnabled = template is not null && !_host.SettingsBlocked;
        PinText.Text = template is not null && template.Id == _host.Fixed.Template?.Id ? Strings.ButtonUnpin : Strings.ButtonPin;

        var editing = _editing is not null || _editingNew;
        EditorPanel.IsEnabled = editing;
        SaveButton.IsEnabled = editing && !blocked && IsDirty;
        RevertButton.IsEnabled = editing && IsDirty;
        EditorStateText.Text = !editing ? Strings.EditorNoSelection
            : _editingNew ? Strings.EditorNewTemplate
            : IsDirty ? Strings.EditorUnsaved : "";
    }

    // ---- 編集 ----

    private void LoadEditor(Template? template, bool isNew, Guid? categoryId = null)
    {
        _loadingEditor = true;
        try
        {
            _editing = template;
            _editingNew = isNew;
            NameBox.Text = template?.Name ?? "";
            BodyBox.Text = template?.Body ?? "";
            var category = template?.CategoryId ?? categoryId;
            EditorCategoryBox.SelectedItem = _host.Library.Categories.FirstOrDefault(c => c.Id == category);
            EditorErrorText.Text = "";
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateEditorInfo();
        UpdateButtons();
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || !IsInitialized || _host is null) return;
        EditorErrorText.Text = "";
        UpdateEditorInfo();
        UpdateButtons();
    }

    private void UpdateEditorInfo()
    {
        CountText.Text = string.Format(Strings.EditorCountFormat, TextMetrics.ScalarCount(BodyBox.Text).ToString("N0"), TemplateRules.BodyMax.ToString("N0"));
        UsageText.Text = Composer.UsesClipboard(BodyBox.Text) ? Strings.UsesClipboard : Strings.TextOnly;
    }

    private void OnInsertVariable(object sender, RoutedEventArgs e)
    {
        var caret = BodyBox.SelectionStart;
        BodyBox.SelectedText = Composer.Variable;
        BodyBox.SelectionStart = caret + Composer.Variable.Length;
        BodyBox.SelectionLength = 0;
        BodyBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    /// <summary>編集中の内容を保存する。保存に成功した場合だけ一覧・実行対象に反映する。</summary>
    private bool Save()
    {
        if (EditorCategoryBox.SelectedItem is not Category category)
        {
            EditorErrorText.Text = Strings.ErrorCategoryNotFound;
            return false;
        }
        var name = NameBox.Text;
        var body = BodyBox.Text;
        var editingId = _editing?.Id;
        var error = _editingNew
            ? _host.Commit(l => l.AddTemplate(category.Id, name, body), out var id)
            : _host.Commit(l => l.UpdateTemplate(editingId!.Value, category.Id, name, body), out id);
        if (error.Length > 0)
        {
            EditorErrorText.Text = error;
            UpdateButtons();
            return false;
        }
        LoadEditor(_host.Library.FindTemplate(id), isNew: false);
        // 検索の条件に合わず保存した項目が一覧に出ない場合は、検索を解除して表示する。
        if (Query.Length > 0 && _host.Library.FindTemplate(id) is { } saved && _host.Library.FindCategory(saved.CategoryId) is { } c
            && !TemplateListView.Matches(saved, c, TemplateListView.Terms(Query)))
            SearchBox.Text = "";
        Refresh(category.Id, id);
        return true;
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_editingNew) LoadEditor(null, isNew: false);
        else LoadEditor(_editing is null ? null : _host.Library.FindTemplate(_editing.Id), isNew: false);
    }

    // ---- 選択 ----

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        var target = SelectedRow;
        var selected = SelectedTemplate;
        if (selected is not null && selected.Id == _editing?.Id && !_editingNew)
        {
            UpdateButtons();
            return;
        }
        if (!ConfirmLeaveEditor())
        {
            // 編集を続ける: 選択を編集中のテンプレート（新規なら元の行）へ戻す。
            _restoringSelection = true;
            try
            {
                List.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0]
                    : ((IEnumerable<TreeRow>)List.ItemsSource).FirstOrDefault(r => r is TemplateRow t && t.Template.Id == _editing?.Id);
            }
            finally
            {
                _restoringSelection = false;
            }
            UpdateButtons();
            return;
        }
        // 保存を選んだ場合は一覧が作り直され、保存したテンプレートが選ばれている。利用者が選んだ行を選び直す。
        if (target is not null && !ReferenceEquals(List.SelectedItem, target))
        {
            Refresh(target is CategoryRow ? target.CategoryId : null, (target as TemplateRow)?.Template.Id);
            selected = selected is null ? null : _host.Library.FindTemplate(selected.Id);
        }
        // カテゴリの見出しを選んだときは編集欄を空にする（並べ替え・名前変更・削除の対象を選んだだけ）。
        LoadEditor(selected, isNew: false);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(List, d) is not ListBoxItem item) return;
        if (item.DataContext is CategoryRow) OnRenameCategory(sender, e);
        else NameBox.Focus();
    }

    // ---- カテゴリ ----

    private void OnAddCategory(object sender, RoutedEventArgs e)
    {
        var name = NameDialog.Ask(Owner, Strings.CategoryNameAddPrompt, "",
            text => TemplateRules.Describe(_host.Library.CheckCategoryName(text, null, out _), category: true));
        if (name is null) return;
        var error = _host.Commit(l => l.AddCategory(name), out var id, category: true);
        if (error.Length > 0) ShowError(error);
        else Refresh(selectCategory: id);
    }

    private void OnRenameCategory(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not CategoryRow { Category: var category }) return;
        var name = NameDialog.Ask(Owner, Strings.CategoryNameRenamePrompt, category.Name,
            text => TemplateRules.Describe(_host.Library.CheckCategoryName(text, category.Id, out _), category: true));
        if (name is null) return;
        var error = _host.Commit(l => l.RenameCategory(category.Id, name), out _, category: true);
        if (error.Length > 0) ShowError(error);
        else Refresh(selectCategory: category.Id);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        switch (SelectedRow)
        {
            case CategoryRow c:
                DeleteCategory(c.Category);
                break;
            case TemplateRow t:
                DeleteTemplate(t.Template);
                break;
        }
    }

    private void DeleteCategory(Category category)
    {
        var count = _host.Library.CountIn(category.Id);
        if (count > 0)
        {
            DeleteCategoryWithTemplates(category, count);
            return;
        }
        if (!Confirm(string.Format(Strings.ConfirmDeleteCategoryFormat, category.Name))) return;
        var error = _host.Commit(l => l.DeleteCategory(category.Id), out _, category: true);
        if (error.Length > 0) ShowError(error);
        else Refresh();
    }

    /// <summary>
    /// 中にテンプレートがあるカテゴリの削除。誤操作を防ぐため 2 回確かめる（どちらも初期の選択はキャンセル）。
    /// 削除の前のデータは templates.before-delete-日時.json として残し、［全件置換で復元］で戻せる。
    /// </summary>
    private void DeleteCategoryWithTemplates(Category category, int count)
    {
        // 削除するカテゴリの中を編集中（新規・複製を含む）なら、先に保存・破棄・キャンセルを選ぶ。
        if (!ConfirmLeaveEditor()) return;
        var first = string.Format(Strings.ConfirmDeleteCategoryWithTemplatesFormat, category.Name, count.ToString("N0"));
        var pinned = _host.Fixed.Template is { } f && f.CategoryId == category.Id;
        if (pinned) first += "\n\n" + Strings.ConfirmDeleteCategoryPinned;
        if (!Confirm(first)) return;
        if (!Confirm(string.Format(Strings.ConfirmDeleteCategoryFinalFormat, category.Name), MessageBoxImage.Warning)) return;
        var error = _host.DeleteCategoryWithTemplates(category.Id);
        if (error.Length > 0)
        {
            ShowError(error);
            Refresh(selectCategory: category.Id);
            return;
        }
        if (_editing is { } editing && editing.CategoryId == category.Id) LoadEditor(null, isNew: false);
        else if (_editingNew) LoadEditor(null, isNew: false);
        Refresh();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        if (Query.Length > 0) return;
        switch (SelectedRow)
        {
            case CategoryRow c:
                var categoryError = _host.MoveCategory(c.CategoryId, offset);
                if (categoryError.Length > 0) ShowError(categoryError);
                else Refresh(selectCategory: c.CategoryId);
                break;
            case TemplateRow t:
                var templateError = _host.MoveTemplate(t.Template.Id, offset);
                if (templateError.Length > 0) ShowError(templateError);
                else Refresh(t.CategoryId, t.Template.Id);
                break;
        }
    }

    // ---- テンプレート ----

    private void OnNewTemplate(object sender, RoutedEventArgs e)
    {
        var category = SelectedCategory ?? _host.Library.Categories.FirstOrDefault();
        if (category is null || !ConfirmLeaveEditor()) return;
        _restoringSelection = true;
        List.SelectedItem = ((IEnumerable<TreeRow>)List.ItemsSource).FirstOrDefault(r => r is CategoryRow c && c.CategoryId == category.Id);
        _restoringSelection = false;
        LoadEditor(null, isNew: true, category.Id);
        NameBox.Focus();
    }

    /// <summary>
    /// 選んだテンプレートの複製を始める。保存済みの最新の内容（ID で引き直したもの）から、名前・本文・カテゴリを引き継いだ新規編集にする。
    /// 元のテンプレートと固定は変えない。［保存］で新しい ID で追加し、［元に戻す］・破棄では何も追加しない。
    /// </summary>
    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } selected || _host.TemplatesBlocked) return;
        var id = selected.Id;
        // 元の項目に未保存の編集があれば、保存・破棄・キャンセルを選ぶ（保存に失敗したら中止）。
        if (!ConfirmLeaveEditor()) return;
        if (_host.Library.FindTemplate(id) is not { } source)
        {
            ShowError(Strings.ErrorTemplateNotFound);
            return;
        }
        _restoringSelection = true;
        try
        {
            List.SelectedItem = ((IEnumerable<TreeRow>)List.ItemsSource).FirstOrDefault(r => r is CategoryRow c && c.CategoryId == source.CategoryId);
        }
        finally
        {
            _restoringSelection = false;
        }
        LoadEditor(null, isNew: true, source.CategoryId);
        _loadingEditor = true;
        try
        {
            NameBox.Text = _host.Library.SuggestCopyName(source.CategoryId, source.Name);
            BodyBox.Text = source.Body;
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateEditorInfo();
        UpdateButtons();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void DeleteTemplate(Template template)
    {
        var pinned = template.Id == _host.Fixed.Template?.Id;
        var message = string.Format(Strings.ConfirmDeleteTemplateFormat, template.Name);
        if (pinned) message += "\n\n" + Strings.ConfirmDeleteTemplatePinned;
        if (!Confirm(message)) return;
        var error = _host.Commit(l => l.DeleteTemplate(template.Id), out _);
        if (error.Length > 0)
        {
            ShowError(error);
            return;
        }
        if (_editing?.Id == template.Id) LoadEditor(null, isNew: false);
        // 削除後に別のテンプレートを自動で固定しない。固定の解除を保存できなくても、参照先不在として扱われる。
        if (pinned)
        {
            var unpinError = _host.SetFixed(null);
            if (unpinError.Length > 0) ShowError(unpinError);
        }
        Refresh(selectCategory: template.CategoryId);
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template) return;
        var error = _host.SetFixed(template.Id == _host.Fixed.Template?.Id ? null : template.Id);
        if (error.Length > 0) ShowError(error);
        Refresh(template.CategoryId, template.Id);
    }

    private void OnRecovery(object sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner) return;
        if (_host.TemplatesBlocked) _host.OfferTemplateRecovery(owner);
        else if (_host.SettingsBlocked) _host.OfferSettingsReset(owner);
    }

    private bool Confirm(string message, MessageBoxImage image = MessageBoxImage.Question) =>
        (Owner is { } owner
            ? MessageBox.Show(owner, message, "ClipCompose", MessageBoxButton.OKCancel, image, MessageBoxResult.Cancel)
            : MessageBox.Show(message, "ClipCompose", MessageBoxButton.OKCancel, image, MessageBoxResult.Cancel)) == MessageBoxResult.OK;

    private void ShowError(string message)
    {
        if (Owner is { } owner) MessageBox.Show(owner, message, "ClipCompose", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, "ClipCompose", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
