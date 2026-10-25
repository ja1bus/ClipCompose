using System.Windows;
using System.Windows.Controls;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;

namespace ClipCompose.Views;

/// <summary>
/// カテゴリ見出し付きの 1 つの一覧の行（選択画面・設定画面の［テンプレート］タブ。仕様書 §5・§10）。
/// カテゴリの見出し行の下に、そのカテゴリのテンプレートを表示順に並べる。
/// </summary>
public abstract record TreeRow
{
    public abstract Guid CategoryId { get; }
}

/// <summary>カテゴリの見出し行。</summary>
public sealed record CategoryRow(Category Category) : TreeRow
{
    public override Guid CategoryId => Category.Id;

    public string Name => Category.Name;

    /// <summary>一覧の項目の名前として UI Automation（読み上げ）に渡る文字列。</summary>
    public override string ToString() => string.Format(Strings.RowCategoryFormat, Name);
}

/// <summary>テンプレートの行。</summary>
public sealed record TemplateRow(Template Template, bool Pinned) : TreeRow
{
    public override Guid CategoryId => Template.CategoryId;

    public string Name => Template.Name;

    public bool UsesClipboard => Template.UsesClipboard;

    public string PinTip => Pinned ? Strings.RowUnpinTip : Strings.RowPinTip;

    /// <summary>一覧の項目の名前として UI Automation（読み上げ）に渡る文字列。ID・本文は含めない。</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, UsesClipboard ? Strings.UsesClipboard : Strings.TextOnly, Pinned ? Strings.PinnedMark : null }.OfType<string>());
}

public static class TreeRows
{
    /// <summary>
    /// 一覧の行を作る。<paramref name="query"/> が空でなければ、名前に含む（大小文字を区別しない）テンプレートと、
    /// それを含むカテゴリの見出しだけにする。<paramref name="includeEmptyCategories"/> が false なら、テンプレートの無いカテゴリを出さない。
    /// </summary>
    public static List<TreeRow> Build(TemplateLibrary library, Guid? fixedId, string query, bool includeEmptyCategories)
    {
        var rows = new List<TreeRow>();
        foreach (var category in library.Categories)
        {
            var templates = library.TemplatesIn(category.Id)
                .Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (templates.Count == 0 && (query.Length > 0 || !includeEmptyCategories)) continue;
            rows.Add(new CategoryRow(category));
            rows.AddRange(templates.Select(t => new TemplateRow(t, t.Id == fixedId)));
        }
        return rows;
    }
}

/// <summary>選択画面では見出しを選択・フォーカスの対象にしない（↑↓とクリックで選ばれない）。</summary>
public sealed class HeaderNotSelectableStyle : StyleSelector
{
    public Style? Header { get; set; }

    public Style? Item { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) => item is CategoryRow ? Header : Item;
}
