using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;

namespace ClipCompose.Views;

/// <summary>
/// 選択画面（仕様書 §5）。カテゴリ見出し付きの 1 つの一覧から選ぶ、幅 300px 前後のコンパクトな画面。
/// - 初期フォーカスは検索欄。テンプレート名とカテゴリ名を、空白で区切った語の AND で検索する（大小文字を区別しない）。
///   検索中は開閉状態によらず、一致したテンプレートを所属カテゴリ付きで平らに並べる。
/// - カテゴリは見出しのクリック・Enter・←→（検索欄が空のとき）で開閉し、閉じている ID を設定に保存する。
/// - ↑↓で見出しとテンプレートを移動、Enter で確定（見出しなら開閉）、Esc で中止。テンプレートのダブルクリックでも確定する。
/// - IME の変換確定の Enter（Key.ImeProcessed として届く）では確定しない。
/// - 📌（Ctrl+P）で固定・解除、Alt+↑↓ / Alt+Shift+↑↓ とドラッグで並べ替え（即時に保存）、［内容］（Ctrl+Space）で本文パネル。
/// - 別のアプリへ移った（非アクティブになった）ら閉じて中止する。中止ではクリップボードを読み書きしない。
///   このため、エラーはメッセージボックス（別のウィンドウ）ではなく、この画面の中に表示する。
/// 確定・中止は 1 回だけ通知する。クリップボードの処理は呼び出し側が、この画面を閉じた後に行う。
/// </summary>
public partial class PickerWindow : Window
{
    private const string DragFormat = "ClipCompose.TreeRow";
    private const int PageRows = 10;
    private static readonly Brush DropHighlight = new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7));

    private readonly AppHost _host;
    private bool _finished;
    private Point _dragStart;
    private TreeRow? _dragRow;
    /// <summary>見出しのクリックで開閉する候補（ドラッグ・ダブルクリックになったら取り消す）。</summary>
    private CategoryRow? _pendingToggle;
    private readonly HashSet<Guid> _collapsed;
    private bool _collapseErrorShown;
    private ListBoxItem? _dropTarget;

    public PickerWindow(AppHost host, bool autoPaste)
    {
        _host = host;
        _collapsed = [.. host.Settings.CollapsedCategoryIds];
        InitializeComponent();
        ModeText.Text = autoPaste ? Strings.PickerAutoPaste : Strings.PickerCopyOnly;
        ModeText.ToolTip = autoPaste ? Strings.PickerAutoPasteTip : Strings.PickerCopyOnlyTip;
        if (host.Hotkeys.Fixed.Status == Services.HotkeyStatus.Registered)
        {
            FixedHotkeyText.Text = string.Format(Strings.PickerFixedHotkeyFormat, host.Hotkeys.Fixed.Shortcut);
            FixedHotkeyText.Visibility = Visibility.Visible;
        }
        // 高さは件数に合わせ、作業領域の約 6 割で止める（それ以上は一覧をスクロールする）。
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height * 0.6);
        SetPreviewVisible(host.Settings.PickerPreview, save: false);
        Refresh(null, null);
        SizeChanged += (_, _) => KeepInWorkArea();
        Loaded += (_, _) =>
        {
            Activate();
            SearchBox.Focus();
        };
    }

    /// <summary>確定した（引数: テンプレート、Enter で確定したか）。この画面は閉じた後。</summary>
    public event Action<Template, bool>? Confirmed;

    /// <summary>中止した（Esc・閉じる・非アクティブ化・［設定］）。</summary>
    public event Action? Canceled;

    /// <summary>［設定］が押された。この画面は中止して閉じた後。</summary>
    public event Action? SettingsRequested;

    private string Query => SearchBox.Text.Trim();

    private bool Searching => Query.Length > 0;

    /// <summary>選んでいるテンプレート（見出しを選んでいるときは null）。実行・固定・本文の対象。</summary>
    private TemplateRow? SelectedRow => List.SelectedItem as TemplateRow;

    private TreeRow? SelectedTreeRow => List.SelectedItem as TreeRow;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Alt との組み合わせは Key.System として届く。変換中・変換確定の Enter は Key.ImeProcessed として届くため、ここでは確定しない。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Enter when modifiers == ModifierKeys.None:
                e.Handled = true;
                if (SelectedTreeRow is CategoryRow category) Toggle(category.CategoryId);
                else Confirm(byEnter: true);
                break;
            // 検索欄が空のときだけ ←→ で開閉する（文字があるときは文字カーソルの移動に使う）。
            // ← はテンプレートを選んでいればそのカテゴリを閉じる、→ は閉じた見出しを開く。
            case Key.Left or Key.Right when modifiers == ModifierKeys.None && SearchBox.Text.Length == 0 && SelectedTreeRow is { } row:
                e.Handled = true;
                var expand = key == Key.Right;
                if (row is CategoryRow c ? c.Expanded != expand : !expand) Toggle(row.CategoryId);
                break;
            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;
            case Key.Up or Key.Down:
                var offset = key == Key.Down ? 1 : -1;
                if (modifiers == ModifierKeys.None) MoveSelection(offset);
                else if (modifiers == ModifierKeys.Alt) MoveSelectedTemplate(offset);
                else if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift)) MoveSelectedCategory(offset);
                else break;
                e.Handled = true;
                break;
            // 件数が多いときのため、PageUp/PageDown で 10 行ずつ動かす（検索欄にフォーカスがあっても一覧を動かす）。
            case Key.PageUp or Key.PageDown when modifiers == ModifierKeys.None:
                e.Handled = true;
                MoveSelection(key == Key.PageDown ? PageRows : -PageRows);
                break;
            case Key.P when modifiers == ModifierKeys.Control:
                e.Handled = true;
                if (SelectedRow is { } pinRow) TogglePin(pinRow);
                break;
            case Key.Space when modifiers == ModifierKeys.Control:
                e.Handled = true;
                SetPreviewVisible(PreviewPane.Visibility != Visibility.Visible, save: true);
                break;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        // Alt だけを押して離すと、Windows のウィンドウメニューの操作に入り、続く ↓ や Esc がメニューに取られる。
        // Alt+↑↓ で並べ替えるため、押しかけて離しても選択画面の操作を続けられるようにする。
        if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) e.Handled = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // 利用者が別のアプリへ移った場合は奪い返さずに中止する。
        if (!_finished) Dispatcher.BeginInvoke(Cancel);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_finished)
        {
            _finished = true;
            Canceled?.Invoke();
        }
    }

    // ---- 一覧 ----

    /// <summary>
    /// 保存済みのデータから一覧を作り直す。<paramref name="selectTemplate"/> のテンプレート、次に <paramref name="selectCategory"/> の見出しを選ぶ
    /// （無ければ先頭のテンプレート、それも無ければ先頭の見出し）。
    /// </summary>
    private void Refresh(Guid? selectTemplate, Guid? selectCategory = null)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = TreeRows.From(TemplateListView.ForPicker(_host.Library, Query, _collapsed), _host.Fixed.Template?.Id, collapsible: true);
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r is TemplateRow t && t.Template.Id == selectTemplate)
            ?? rows.FirstOrDefault(r => r is CategoryRow c && c.CategoryId == selectCategory)
            ?? rows.FirstOrDefault(r => r is TemplateRow)
            ?? rows.FirstOrDefault();
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        NoMatch.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFoldAllButton(rows);
        UpdatePreview();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ClearError();
        Refresh(null, null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void MoveSelection(int offset)
    {
        // 見出しも選べる（Enter・←→ で開閉するため）。1 行より大きく動かすときは端で止める。
        var i = List.SelectedIndex + offset;
        if (Math.Abs(offset) > 1) i = Math.Clamp(i, 0, List.Items.Count - 1);
        if (i < 0 || i >= List.Items.Count) return;
        List.SelectedIndex = i;
        List.ScrollIntoView(List.Items[i]);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInButton(e.OriginalSource)) return; // 📌の連打で実行しない
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: TemplateRow })
            Confirm(byEnter: false);
    }

    // ---- 開閉 ----

    /// <summary>
    /// カテゴリを開く・閉じる。検索中は使わない。閉じた ID を設定に保存する（保存できなくても、この起動中は覚えておく）。
    /// 開閉で画面が縮んでポインターの位置がずれないよう、表示中は高さを縮めない。
    /// </summary>
    private void Toggle(Guid categoryId)
    {
        if (Searching) return;
        if (!_collapsed.Remove(categoryId)) _collapsed.Add(categoryId);
        ApplyCollapsed(categoryId);
    }

    private void OnFoldAll(object sender, RoutedEventArgs e)
    {
        if (Searching) return;
        var categories = _host.Library.Categories.Select(c => c.Id).ToList();
        if (categories.Any(id => !_collapsed.Contains(id) && _host.Library.CountIn(id) > 0)) _collapsed.UnionWith(categories);
        else _collapsed.Clear();
        ApplyCollapsed(SelectedTreeRow?.CategoryId);
    }

    private void ApplyCollapsed(Guid? selectCategory)
    {
        if (IsLoaded) MinHeight = Math.Max(MinHeight, ActualHeight);
        var error = _host.SetCollapsedCategories(_collapsed);
        if (error.Length > 0 && !_collapseErrorShown)
        {
            _collapseErrorShown = true;
            ShowError(Strings.PickerCollapseNotSaved + Environment.NewLine + error);
        }
        // 開閉した見出しを選ぶ（閉じたカテゴリの中のテンプレートを選んだまま Enter で実行しない）。
        Refresh(null, selectCategory);
    }

    private void UpdateFoldAllButton(List<TreeRow> rows)
    {
        var anyExpanded = rows.OfType<CategoryRow>().Any(c => c.Expanded);
        FoldAllButton.IsEnabled = !Searching && rows.Count > 0;
        // E70E: 上向き（すべて閉じる）、E70D: 下向き（すべて開く）。
        FoldAllIcon.Text = anyExpanded ? "\uE70E" : "\uE70D";
        var label = anyExpanded ? Strings.PickerCollapseAll : Strings.PickerExpandAll;
        FoldAllButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(FoldAllButton, label);
    }

    // ---- 固定 ----

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TemplateRow row }) TogglePin(row);
    }

    /// <summary>固定する（固定中なら解除する）。実行はしない。</summary>
    private void TogglePin(TemplateRow row)
    {
        var selected = SelectedTreeRow;
        var error = _host.SetFixed(row.Pinned ? null : row.Template.Id);
        if (error.Length > 0) ShowError(error);
        else ClearError();
        Refresh((selected as TemplateRow)?.Template.Id, selected?.CategoryId);
    }

    // ---- 並べ替え ----

    /// <summary>Alt+↑↓。テンプレートならカテゴリ内で、見出しならカテゴリを動かす。</summary>
    private void MoveSelectedTemplate(int offset)
    {
        if (SelectedTreeRow is CategoryRow)
        {
            MoveSelectedCategory(offset);
            return;
        }
        if (SelectedRow is not { } row || !CanReorder()) return;
        ApplyMove(row, () => _host.MoveTemplate(row.Template.Id, offset));
    }

    /// <summary>Alt+Shift+↑↓。選んでいる行のカテゴリを動かす。</summary>
    private void MoveSelectedCategory(int offset)
    {
        if (SelectedTreeRow is not { } row || !CanReorder()) return;
        ApplyMove(row, () => _host.MoveCategory(row.CategoryId, offset));
    }

    /// <summary>検索で絞り込んでいる間は並べ替えない（見えていない項目との位置関係が分からないため）。</summary>
    private bool CanReorder()
    {
        if (Query.Length == 0) return true;
        ShowError(Strings.PickerNoSortWhileSearching);
        return false;
    }

    /// <summary>動かして保存し、動かした行を選び直す。</summary>
    private void ApplyMove(TreeRow row, Func<string> move)
    {
        var error = move();
        if (error.Length > 0) ShowError(error);
        else ClearError();
        Refresh((row as TemplateRow)?.Template.Id, row.CategoryId);
    }

    /// <summary>
    /// ドラッグした行を <paramref name="target"/> の位置へ動かす量。動かせない組み合わせ（別のカテゴリへのテンプレートの移動など）は null。
    /// </summary>
    private int? DropOffset(TreeRow source, TreeRow target)
    {
        var library = _host.Library;
        switch (source)
        {
            case TemplateRow s when target.CategoryId == s.CategoryId:
                var siblings = library.TemplatesIn(s.CategoryId).Select(t => t.Id).ToList();
                var from = siblings.IndexOf(s.Template.Id);
                // 見出しへ落としたらカテゴリの先頭へ。
                var to = target is TemplateRow t ? siblings.IndexOf(t.Template.Id) : 0;
                return from < 0 || to < 0 || from == to ? null : to - from;
            case CategoryRow c:
                var categories = library.Categories.Select(x => x.Id).ToList();
                var fromCategory = categories.IndexOf(c.CategoryId);
                var toCategory = categories.IndexOf(target.CategoryId);
                return fromCategory < 0 || toCategory < 0 || fromCategory == toCategory ? null : toCategory - fromCategory;
            default:
                return null;
        }
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragRow = null;
        _pendingToggle = null;
        if (IsInButton(e.OriginalSource) || e.OriginalSource is not DependencyObject d) return;
        if (ItemsControl.ContainerFromElement(List, d) is not ListBoxItem { DataContext: TreeRow row }) return;
        _dragRow = row;
        _dragStart = e.GetPosition(List);
        // 見出しは離したときに開閉する（ドラッグで動かす場合・ダブルクリックの 2 回目では開閉しない）。
        if (row is CategoryRow category && e.ClickCount == 1 && !Searching) _pendingToggle = category;
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var toggle = _pendingToggle;
        _pendingToggle = null;
        if (toggle is null || e.OriginalSource is not DependencyObject d) return;
        if (ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: CategoryRow row } && row.CategoryId == toggle.CategoryId)
            Toggle(row.CategoryId);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(List) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        _pendingToggle = null;
        if (!CanReorder()) return;
        try
        {
            DragDrop.DoDragDrop(List, new DataObject(DragFormat, row), DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var (source, container, offset) = DropInfo(e);
        e.Effects = source is not null && offset is not null ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(offset is not null ? container : null);
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTarget(null);
        var (source, _, offset) = DropInfo(e);
        if (source is null || offset is not { } n) return;
        ApplyMove(source,
            () => source is CategoryRow c ? _host.MoveCategory(c.CategoryId, n) : _host.MoveTemplate(((TemplateRow)source).Template.Id, n));
    }

    private (TreeRow? Source, ListBoxItem? Container, int? Offset) DropInfo(DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not TreeRow source || e.OriginalSource is not DependencyObject d) return (null, null, null);
        if (ItemsControl.ContainerFromElement(List, d) is not ListBoxItem { DataContext: TreeRow target } container) return (source, null, null);
        return (source, container, DropOffset(source, target));
    }

    private void SetDropTarget(ListBoxItem? container)
    {
        if (_dropTarget == container) return;
        _dropTarget?.ClearValue(BackgroundProperty);
        _dropTarget = container;
        _dropTarget?.SetValue(BackgroundProperty, DropHighlight);
    }

    // ---- 本文パネル ----

    private void OnTogglePreview(object sender, RoutedEventArgs e) => SetPreviewVisible(PreviewPane.Visibility != Visibility.Visible, save: true);

    private void SetPreviewVisible(bool visible, bool save)
    {
        PreviewPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.FontWeight = visible ? FontWeights.SemiBold : FontWeights.Normal;
        UpdatePreview();
        if (save) _host.SetPickerPreview(visible);
    }

    private void UpdatePreview()
    {
        if (PreviewPane.Visibility != Visibility.Visible) return;
        // 見出しを選んでいるときは本文を出さない（カテゴリ名だけ）。
        var template = SelectedRow?.Template;
        PreviewTitle.Text = template?.Name ?? (SelectedTreeRow as CategoryRow)?.Name ?? "";
        PreviewBox.Text = template?.Body ?? "";
        PreviewBox.ScrollToHome();
    }

    // ---- 確定・中止 ----

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_finished) return;
        Cancel();
        SettingsRequested?.Invoke();
    }

    private void Confirm(bool byEnter)
    {
        if (_finished || SelectedRow is not { } row) return;
        _finished = true;
        Close();
        Confirmed?.Invoke(row.Template, byEnter);
    }

    private void Cancel()
    {
        if (_finished) return;
        _finished = true;
        Close();
        Canceled?.Invoke();
    }

    // ---- 補助 ----

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError() => ErrorText.Visibility = Visibility.Collapsed;

    /// <summary>本文パネルを開くなどで大きさが変わったとき、作業領域からはみ出さないように寄せる。</summary>
    private void KeepInWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (Left + ActualWidth > work.Right) Left = Math.Max(work.Left, work.Right - ActualWidth);
        if (Top + ActualHeight > work.Bottom) Top = Math.Max(work.Top, work.Bottom - ActualHeight);
    }

    private static bool IsInButton(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Button) return true;
            if (d is ListBoxItem) return false;
        }
        return false;
    }
}
