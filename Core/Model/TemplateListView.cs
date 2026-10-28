namespace ClipCompose.Core.Model;

/// <summary>一覧の 1 行（カテゴリの見出し、またはテンプレート）。表示用の部品は Views 側で作る。</summary>
public abstract record ListEntry(Guid CategoryId);

/// <summary>カテゴリの見出し。<see cref="Count"/> はカテゴリの全テンプレートの件数（検索で絞った件数ではない）。</summary>
public sealed record CategoryEntry(Category Category, int Count, bool Expanded) : ListEntry(Category.Id);

/// <summary>テンプレート。<see cref="Category"/> は検索結果の平らな一覧で所属を添えるときだけ（見出しの下なら null）。</summary>
public sealed record TemplateEntry(Template Template, Category? Category) : ListEntry(Template.CategoryId);

/// <summary>
/// 選択画面・設定画面の一覧に出す行と、検索の判定（仕様書 §5・§10）。
/// - 検索の対象はテンプレート名とカテゴリ名。空白で区切った語をすべて含むもの（AND。大小文字を区別しない）。
///   カテゴリ名だけが一致した語も一致として数えるため、カテゴリ名で検索するとそのカテゴリの全テンプレートが出る。本文は検索しない。
/// - 選択画面の検索中は、開閉状態によらず一致したテンプレートだけを平らに並べ、所属カテゴリを添える。
/// - 閉じたカテゴリの子の行は作らない（件数が多くても行を作るのは見えるものだけ）。
/// </summary>
public static class TemplateListView
{
    public static string[] Terms(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(Template template, Category category, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (!template.Name.Contains(term, StringComparison.OrdinalIgnoreCase) && !category.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>選択画面の行。</summary>
    /// <param name="collapsed">閉じているカテゴリの ID（検索中は使わない）。</param>
    public static List<ListEntry> ForPicker(TemplateLibrary library, string query, IReadOnlySet<Guid> collapsed)
    {
        var terms = Terms(query);
        var rows = new List<ListEntry>();
        foreach (var category in library.Categories)
        {
            var templates = library.TemplatesIn(category.Id);
            if (terms.Length > 0)
            {
                rows.AddRange(templates.Where(t => Matches(t, category, terms)).Select(t => new TemplateEntry(t, category)));
                continue;
            }
            var count = library.CountIn(category.Id);
            if (count == 0) continue; // 空のカテゴリは選ぶものが無いので出さない
            var expanded = !collapsed.Contains(category.Id);
            rows.Add(new CategoryEntry(category, count, expanded));
            if (expanded) rows.AddRange(templates.Select(t => new TemplateEntry(t, null)));
        }
        return rows;
    }

    /// <summary>
    /// 設定画面の行。カテゴリはいつも開き、空のカテゴリも出す。検索中は一致したテンプレートと、その見出しだけにする
    /// （カテゴリ名が一致した場合は、テンプレートが無くても見出しを出す。改名・削除をできるように）。
    /// </summary>
    public static List<ListEntry> ForEditor(TemplateLibrary library, string query)
    {
        var terms = Terms(query);
        var rows = new List<ListEntry>();
        foreach (var category in library.Categories)
        {
            var count = library.CountIn(category.Id);
            var templates = library.TemplatesIn(category.Id);
            if (terms.Length > 0)
            {
                var matched = templates.Where(t => Matches(t, category, terms)).ToList();
                var categoryMatched = terms.All(term => category.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (matched.Count == 0 && !categoryMatched) continue;
                rows.Add(new CategoryEntry(category, count, Expanded: true));
                rows.AddRange(matched.Select(t => new TemplateEntry(t, null)));
                continue;
            }
            rows.Add(new CategoryEntry(category, count, Expanded: true));
            rows.AddRange(templates.Select(t => new TemplateEntry(t, null)));
        }
        return rows;
    }
}
