namespace ClipCompose.Core.Model;

/// <summary>編集の結果。成功なら新しいライブラリ、失敗なら理由（元のライブラリは変わらない）。</summary>
public readonly record struct LibraryEdit(TemplateLibrary? Library, LibraryError Error, Guid Id = default)
{
    public bool Succeeded => Error == LibraryError.None;

    public static LibraryEdit Fail(LibraryError error) => new(null, error);
}

/// <summary>
/// カテゴリとテンプレートの一覧（仕様書 §4）。変更できない値で、編集は新しいライブラリを返す。
/// 表示順は一覧の並び（保存時に order として書く）。保存に成功したものだけを実行対象にするため、
/// 呼び出し側は保存に成功してから差し替える。
/// </summary>
public sealed class TemplateLibrary
{
    public static readonly TemplateLibrary Empty = new([], []);

    public TemplateLibrary(IReadOnlyList<Category> categories, IReadOnlyList<Template> templates)
    {
        Categories = categories;
        Templates = templates;
    }

    public IReadOnlyList<Category> Categories { get; }

    /// <summary>全テンプレート。カテゴリ内の表示順は、この並びでそのカテゴリのものを取り出した順。</summary>
    public IReadOnlyList<Template> Templates { get; }

    public Category? FindCategory(Guid id) => Categories.FirstOrDefault(c => c.Id == id);

    public Template? FindTemplate(Guid id) => Templates.FirstOrDefault(t => t.Id == id);

    public IEnumerable<Template> TemplatesIn(Guid categoryId) => Templates.Where(t => t.CategoryId == categoryId);

    /// <summary>カテゴリの表示順にテンプレートを並べた一覧（選択画面・トレイ用）。</summary>
    public IEnumerable<Template> TemplatesInDisplayOrder() => Categories.SelectMany(c => TemplatesIn(c.Id));

    public LibraryEdit AddCategory(string name, Guid? id = null)
    {
        var error = CheckCategoryName(name, null, out var trimmed);
        if (error != LibraryError.None) return LibraryEdit.Fail(error);
        var category = new Category(id ?? Guid.NewGuid(), trimmed);
        return new(new TemplateLibrary([.. Categories, category], Templates), LibraryError.None, category.Id);
    }

    public LibraryEdit RenameCategory(Guid id, string name)
    {
        if (FindCategory(id) is null) return LibraryEdit.Fail(LibraryError.CategoryNotFound);
        var error = CheckCategoryName(name, id, out var trimmed);
        if (error != LibraryError.None) return LibraryEdit.Fail(error);
        return new(new TemplateLibrary([.. Categories.Select(c => c.Id == id ? c with { Name = trimmed } : c)], Templates), LibraryError.None, id);
    }

    /// <summary>テンプレートが所属するカテゴリは削除しない（移動または削除してから）。</summary>
    public LibraryEdit DeleteCategory(Guid id)
    {
        if (FindCategory(id) is null) return LibraryEdit.Fail(LibraryError.CategoryNotFound);
        if (TemplatesIn(id).Any()) return LibraryEdit.Fail(LibraryError.CategoryNotEmpty);
        return new(new TemplateLibrary([.. Categories.Where(c => c.Id != id)], Templates), LibraryError.None, id);
    }

    /// <summary>カテゴリを <paramref name="offset"/> だけ動かす（-1 = 上へ、+1 = 下へ）。端を越える場合は変えない。</summary>
    public LibraryEdit MoveCategory(Guid id, int offset)
    {
        var list = Categories.ToList();
        var index = list.FindIndex(c => c.Id == id);
        if (index < 0) return LibraryEdit.Fail(LibraryError.CategoryNotFound);
        var target = Math.Clamp(index + offset, 0, list.Count - 1);
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(target, item);
        return new(new TemplateLibrary(list, Templates), LibraryError.None, id);
    }

    public LibraryEdit AddTemplate(Guid categoryId, string name, string body, Guid? id = null)
    {
        if (FindCategory(categoryId) is null) return LibraryEdit.Fail(LibraryError.CategoryNotFound);
        var error = CheckTemplate(categoryId, name, body, null, out var trimmed);
        if (error != LibraryError.None) return LibraryEdit.Fail(error);
        var template = new Template(id ?? Guid.NewGuid(), categoryId, trimmed, body);
        return new(new TemplateLibrary(Categories, [.. Templates, template]), LibraryError.None, template.Id);
    }

    /// <summary>
    /// 名前・本文・カテゴリを更新する（ID は変えない）。カテゴリを変えた場合は移動先の末尾に置く。
    /// </summary>
    public LibraryEdit UpdateTemplate(Guid id, Guid categoryId, string name, string body)
    {
        var current = FindTemplate(id);
        if (current is null) return LibraryEdit.Fail(LibraryError.TemplateNotFound);
        if (FindCategory(categoryId) is null) return LibraryEdit.Fail(LibraryError.CategoryNotFound);
        var error = CheckTemplate(categoryId, name, body, id, out var trimmed);
        if (error != LibraryError.None) return LibraryEdit.Fail(error);
        var updated = current with { CategoryId = categoryId, Name = trimmed, Body = body };
        var templates = current.CategoryId == categoryId
            ? Templates.Select(t => t.Id == id ? updated : t).ToList()
            : [.. Templates.Where(t => t.Id != id), updated];
        return new(new TemplateLibrary(Categories, templates), LibraryError.None, id);
    }

    public LibraryEdit DeleteTemplate(Guid id)
    {
        if (FindTemplate(id) is null) return LibraryEdit.Fail(LibraryError.TemplateNotFound);
        return new(new TemplateLibrary(Categories, [.. Templates.Where(t => t.Id != id)]), LibraryError.None, id);
    }

    /// <summary>同じカテゴリの中で <paramref name="offset"/> だけ動かす。端を越える場合は変えない。</summary>
    public LibraryEdit MoveTemplate(Guid id, int offset)
    {
        var current = FindTemplate(id);
        if (current is null) return LibraryEdit.Fail(LibraryError.TemplateNotFound);
        var siblings = TemplatesIn(current.CategoryId).ToList();
        var index = siblings.FindIndex(t => t.Id == id);
        var target = Math.Clamp(index + offset, 0, siblings.Count - 1);
        if (target == index) return new(this, LibraryError.None, id);
        siblings.RemoveAt(index);
        siblings.Insert(target, current);
        // 他のカテゴリの並びは変えず、このカテゴリの位置に並べ替えたものを順に入れ直す。
        var queue = new Queue<Template>(siblings);
        var templates = Templates.Select(t => t.CategoryId == current.CategoryId ? queue.Dequeue() : t).ToList();
        return new(new TemplateLibrary(Categories, templates), LibraryError.None, id);
    }

    public LibraryError CheckCategoryName(string name, Guid? self, out string trimmed)
    {
        var error = TemplateRules.CheckName(name, TemplateRules.CategoryNameMax, out trimmed);
        if (error != LibraryError.None) return error;
        var n = trimmed;
        return Categories.Any(c => c.Id != self && TemplateRules.SameName(c.Name, n)) ? LibraryError.NameDuplicate : LibraryError.None;
    }

    public LibraryError CheckTemplate(Guid categoryId, string name, string body, Guid? self, out string trimmed)
    {
        var error = TemplateRules.CheckName(name, TemplateRules.TemplateNameMax, out trimmed);
        if (error != LibraryError.None) return error;
        var n = trimmed;
        if (TemplatesIn(categoryId).Any(t => t.Id != self && TemplateRules.SameName(t.Name, n))) return LibraryError.NameDuplicate;
        return TemplateRules.CheckBody(body);
    }
}
