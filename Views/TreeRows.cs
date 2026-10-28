using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;

namespace ClipCompose.Views;

/// <summary>
/// カテゴリ見出し付きの 1 つの一覧の行（選択画面・設定画面の［テンプレート］タブ。仕様書 §5・§10）。
/// どの行を出すか（開閉・検索）は Core の <see cref="TemplateListView"/> が決め、ここでは表示用の値だけを持つ。
/// </summary>
public abstract record TreeRow
{
    public abstract Guid CategoryId { get; }
}

/// <summary>カテゴリの見出し行。<paramref name="Collapsible"/> は開閉できる一覧（選択画面）か。</summary>
public sealed record CategoryRow(Category Category, int Count, bool Expanded, bool Collapsible) : TreeRow
{
    public override Guid CategoryId => Category.Id;

    public string Name => Category.Name;

    public string CountText => Count.ToString("N0");

    public string CountTip => string.Format(Strings.RowCountFormat, CountText);

    /// <summary>一覧の項目の名前として UI Automation（読み上げ）に渡る文字列。</summary>
    public override string ToString() =>
        string.Join(", ", new[]
        {
            string.Format(Strings.RowCategoryFormat, Name),
            CountTip,
            Collapsible ? (Expanded ? Strings.RowExpanded : Strings.RowCollapsed) : null,
        }.OfType<string>());
}

/// <summary>テンプレートの行。<paramref name="CategoryName"/> は検索結果の平らな一覧で所属を添えるときだけ。</summary>
public sealed record TemplateRow(Template Template, bool Pinned, string? CategoryName = null) : TreeRow
{
    public override Guid CategoryId => Template.CategoryId;

    public string Name => Template.Name;

    public bool UsesClipboard => Template.UsesClipboard;

    public bool ShowCategory => CategoryName is not null;

    public string PinTip => Pinned ? Strings.RowUnpinTip : Strings.RowPinTip;

    /// <summary>一覧の項目の名前として UI Automation（読み上げ）に渡る文字列。ID・本文は含めない。</summary>
    public override string ToString() =>
        string.Join(", ", new[]
        {
            Name,
            CategoryName is null ? null : string.Format(Strings.RowCategoryFormat, CategoryName),
            UsesClipboard ? Strings.UsesClipboard : Strings.TextOnly,
            Pinned ? Strings.PinnedMark : null,
        }.OfType<string>());
}

public static class TreeRows
{
    /// <summary>Core が決めた行を、表示用の行にする。</summary>
    public static List<TreeRow> From(IEnumerable<ListEntry> entries, Guid? fixedId, bool collapsible) =>
        [.. entries.Select(e => e switch
        {
            CategoryEntry c => (TreeRow)new CategoryRow(c.Category, c.Count, c.Expanded, collapsible),
            TemplateEntry t => new TemplateRow(t.Template, t.Template.Id == fixedId, t.Category?.Name),
            _ => throw new InvalidOperationException(),
        })];
}
