using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;

namespace ClipCompose.Views;

/// <summary>カテゴリ選択画面の 1 行。チェック・取り込み方はカテゴリ ID ごとに持つ（行の表示部品には持たせない）。</summary>
public sealed class CategoryChoice : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private int _modeIndex;
    private string _resultText = "";

    public CategoryChoice(Category category, int count, bool hasSameName, bool forImport)
    {
        Category = category;
        Count = count;
        HasSameName = hasSameName;
        ForImport = forImport;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Category Category { get; }

    public string Name => Category.Name;

    public int Count { get; }

    public string CountText => string.Format(Strings.RowCountFormat, Count.ToString("N0"));

    /// <summary>同名の既存カテゴリがある（取り込み方を選べる）。</summary>
    public bool HasSameName { get; }

    public bool ForImport { get; }

    public bool ShowDetail => ForImport && IsChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            Changed();
            Changed(nameof(ShowDetail));
        }
    }

    /// <summary>0 = 別カテゴリで追加、1 = 中身を置き換え。</summary>
    public int ModeIndex
    {
        get => _modeIndex;
        set
        {
            if (_modeIndex == value) return;
            _modeIndex = value;
            Changed();
        }
    }

    public MergeMode Mode => HasSameName && ModeIndex == 1 ? MergeMode.ReplaceContents : MergeMode.AddAsNew;

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (_resultText == value) return;
            _resultText = value;
            Changed();
        }
    }

    /// <summary>読み上げ用の名前。</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, CountText, IsChecked ? Strings.SelectChecked : Strings.SelectUnchecked, ShowDetail ? ResultText : null }
            .Where(s => !string.IsNullOrEmpty(s)));

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// エクスポート・追加インポートで、カテゴリをチェックで選ぶ画面（仕様書 §11）。初期状態はすべて選択。
/// - 検索は一覧を絞るだけで、隠れた行のチェックは変えない。一括の選択・解除は表示中の行だけに働く。
/// - 選んだカテゴリの中のテンプレートはすべて対象（個別には選ばない）。0 カテゴリでは先へ進めない。
/// </summary>
public partial class CategorySelectWindow : Window
{
    private readonly List<CategoryChoice> _choices;
    private readonly ICollectionView _view;
    private readonly Action<IReadOnlyList<CategoryChoice>>? _changed;

    private CategorySelectWindow(Window owner, string prompt, string note, string okText, List<CategoryChoice> choices,
        Action<IReadOnlyList<CategoryChoice>>? changed)
    {
        InitializeComponent();
        Owner = owner;
        PromptText.Text = prompt;
        NoteText.Text = note;
        OkButton.Content = okText;
        _choices = choices;
        _changed = changed;
        foreach (var choice in choices) choice.PropertyChanged += OnChoiceChanged;
        _view = CollectionViewSource.GetDefaultView(choices);
        _view.Filter = o => o is CategoryChoice c && Visible(c);
        List.ItemsSource = _view;
        if (choices.Count > 0) List.SelectedIndex = 0;
        _changed?.Invoke(_choices);
        UpdateSummary();
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>OK なら選んだ行（チェックの付いたもの）、キャンセルなら null。</summary>
    public static IReadOnlyList<CategoryChoice>? Ask(Window owner, string prompt, string note, string okText, List<CategoryChoice> choices,
        Action<IReadOnlyList<CategoryChoice>>? changed = null)
    {
        var window = new CategorySelectWindow(owner, prompt, note, okText, choices, changed);
        return window.ShowDialog() == true ? [.. choices.Where(c => c.IsChecked)] : null;
    }

    private string[] Terms => TemplateListView.Terms(SearchBox.Text);

    private bool Visible(CategoryChoice choice) => Terms.All(t => choice.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(CategoryChoice.IsChecked) or nameof(CategoryChoice.ModeIndex))) return;
        _changed?.Invoke(_choices);
        UpdateSummary();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _view is null) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        if (List.SelectedItem is null && !_view.IsEmpty) List.SelectedIndex = 0;
        UpdateSummary();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetVisible(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetVisible(false);

    private void SetVisible(bool value)
    {
        foreach (var choice in _choices.Where(Visible)) choice.IsChecked = value;
    }

    /// <summary>行を選んでいるときの Space でチェックを切り替える（取り込み方の欄の操作中は除く）。</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is ComboBox or ComboBoxItem) return;
        if (List.SelectedItem is not CategoryChoice choice) return;
        choice.IsChecked = !choice.IsChecked;
        e.Handled = true;
    }

    private void UpdateSummary()
    {
        var chosen = _choices.Where(c => c.IsChecked).ToList();
        var hidden = chosen.Count(c => !Visible(c));
        var text = string.Format(Strings.SelectSummaryFormat, chosen.Count.ToString("N0"), chosen.Sum(c => c.Count).ToString("N0"));
        if (hidden > 0) text += " " + string.Format(Strings.SelectHiddenFormat, hidden.ToString("N0"));
        SummaryText.Text = text;
        OkButton.IsEnabled = chosen.Count > 0;
        NoMatch.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_choices.Any(c => c.IsChecked)) return;
        DialogResult = true;
    }
}
