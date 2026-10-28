using ClipCompose.Core.Localization;

namespace ClipCompose.Core.Model;

/// <summary>追加インポートでの、カテゴリの取り込み方。</summary>
public enum MergeMode
{
    /// <summary>新しいカテゴリとして既存の後ろに追加する（同名があれば「（取り込み）」を付ける）。</summary>
    AddAsNew,

    /// <summary>同名の既存カテゴリの中身を、ファイルの内容に置き換える（カテゴリの ID・位置・名前は変えない）。</summary>
    ReplaceContents,
}

/// <summary>取り込む 1 カテゴリの結果。<see cref="Target"/> は置き換える既存カテゴリ（追加なら null）。</summary>
public sealed record MergeStep(Category Source, int TemplateCount, MergeMode Mode, Category? Target, string ResultName);

/// <summary>結合後のライブラリと、消えたテンプレートの ID（固定の解除の判断に使う）。</summary>
public sealed record MergeResult(TemplateLibrary Library, IReadOnlyList<MergeStep> Steps, IReadOnlySet<Guid> RemovedTemplateIds);

/// <summary>
/// 追加インポート（仕様書 §11）。既存のカテゴリ・テンプレートは残し、ファイルから選んだカテゴリだけを取り込む。
/// - 追加するカテゴリとテンプレートには、すべて新しい ID を付ける（ファイルの ID で既存のものや固定の参照先を上書きしない）。
/// - 置き換えは、同名の既存カテゴリの中身をファイルの内容にする。同じ名前のテンプレートは同じものとして既存の ID を引き継ぐ
///   （固定を保つため）。ファイルに無いテンプレートは消える。
/// - 同名カテゴリへテンプレートを混ぜる方式は取らない（古い内容と新しい内容が混ざるため）。
/// </summary>
public static class LibraryMerge
{
    /// <summary>
    /// 選んだカテゴリ（ファイルのカテゴリ ID → 取り込み方）ごとの結果名。ファイルのカテゴリの順に並べる。
    /// 置き換えを選んでも同名の既存カテゴリが無ければ追加にする。
    /// </summary>
    public static IReadOnlyList<MergeStep> Plan(TemplateLibrary current, TemplateLibrary file, IReadOnlyDictionary<Guid, MergeMode> selection)
    {
        // 追加するカテゴリ同士でも重ならないよう、決めた名前を順に加える。
        var taken = current.Categories.Select(c => c.Name).ToList();
        var steps = new List<MergeStep>();
        foreach (var source in file.Categories)
        {
            if (!selection.TryGetValue(source.Id, out var mode)) continue;
            var same = SameName(current, source.Name);
            var count = file.CountIn(source.Id);
            if (mode == MergeMode.ReplaceContents && same is not null)
            {
                steps.Add(new(source, count, MergeMode.ReplaceContents, same, same.Name));
                continue;
            }
            var name = taken.Any(t => TemplateRules.SameName(t, source.Name))
                ? UniqueName.Make(source.Name, TemplateRules.CategoryNameMax, Strings.ImportSuffix, Strings.ImportSuffixNumberedFormat,
                    candidate => taken.Any(t => TemplateRules.SameName(t, candidate)))
                : source.Name;
            taken.Add(name);
            steps.Add(new(source, count, MergeMode.AddAsNew, null, name));
        }
        return steps;
    }

    /// <summary>同じ名前（大小文字を区別しない）の既存カテゴリ。</summary>
    public static Category? SameName(TemplateLibrary current, string name) =>
        current.Categories.FirstOrDefault(c => TemplateRules.SameName(c.Name, name));

    /// <summary>結合後のライブラリを作る（保存はしない）。<paramref name="newId"/> はテスト用。</summary>
    public static MergeResult Apply(TemplateLibrary current, TemplateLibrary file, IReadOnlyList<MergeStep> steps, Func<Guid>? newId = null)
    {
        newId ??= Guid.NewGuid;
        var categories = current.Categories.ToList();
        var templates = current.Templates.ToList();
        var removed = new HashSet<Guid>();
        foreach (var step in steps)
        {
            var incoming = file.TemplatesIn(step.Source.Id).ToList();
            if (step.Target is { } target)
            {
                var old = current.TemplatesIn(target.Id).ToList();
                var kept = new HashSet<Guid>();
                templates.RemoveAll(t => t.CategoryId == target.Id);
                foreach (var t in incoming)
                {
                    var reuse = old.FirstOrDefault(o => TemplateRules.SameName(o.Name, t.Name));
                    var id = reuse?.Id ?? newId();
                    if (reuse is not null) kept.Add(reuse.Id);
                    templates.Add(t with { Id = id, CategoryId = target.Id });
                }
                removed.UnionWith(old.Select(o => o.Id).Where(id => !kept.Contains(id)));
            }
            else
            {
                var category = new Category(newId(), step.ResultName);
                categories.Add(category);
                templates.AddRange(incoming.Select(t => t with { Id = newId(), CategoryId = category.Id }));
            }
        }
        return new(new TemplateLibrary(categories, templates), steps, removed);
    }
}
