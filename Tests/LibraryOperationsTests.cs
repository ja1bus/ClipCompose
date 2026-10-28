using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Tests;

/// <summary>複製の名前・カテゴリの抽出と削除・追加インポートの組み立て（仕様書 §4・§11）。</summary>
public sealed class LibraryOperationsTests
{
    /// <summary>A（a1, a2）・B（b1）・空の Empty。</summary>
    private static (TemplateLibrary Library, Guid A, Guid B, Guid Empty) Sample()
    {
        var a = TemplateLibrary.Empty.AddCategory("A");
        var b = a.Library!.AddCategory("B");
        var e = b.Library!.AddCategory("Empty");
        var library = e.Library!
            .AddTemplate(a.Id, "a1", "body a1 {{clipboard}}\r\n  ").Library!
            .AddTemplate(b.Id, "b1", "body b1").Library!
            .AddTemplate(a.Id, "a2", "body a2").Library!;
        return (library, a.Id, b.Id, e.Id);
    }

    // ---- 複製の名前 ----

    [Fact]
    public void Copy_name_adds_a_suffix_then_numbers()
    {
        Assert.Equal("x (copy)", UniqueName.Make("x", 100, " (copy)", " (copy {0})", _ => false));
        var taken = new[] { "x (copy)", "X (COPY 2)" };
        Assert.Equal("x (copy 3)", UniqueName.Make("x", 100, " (copy)", " (copy {0})", n => taken.Any(t => TemplateRules.SameName(t, n))));
    }

    [Fact]
    public void Copy_name_keeps_the_suffix_and_shortens_only_the_original_within_the_limit()
    {
        var longName = new string('あ', 98) + "😀😀";
        var name = UniqueName.Make(longName, TemplateRules.TemplateNameMax, "（コピー）", "（コピー {0}）", _ => false);
        Assert.Equal(LibraryError.None, TemplateRules.CheckName(name, TemplateRules.TemplateNameMax, out _));
        Assert.EndsWith("（コピー）", name);
        Assert.Equal(100, Core.Text.TextMetrics.ScalarCount(name));
        // 絵文字（サロゲートペア）を途中で切らない。
        var emoji = UniqueName.Make(string.Concat(Enumerable.Repeat("😀", 100)), TemplateRules.TemplateNameMax, "（コピー）", "（コピー {0}）", _ => false);
        Assert.False(Core.Text.TextMetrics.HasLoneSurrogate(emoji));
        Assert.Equal(100, Core.Text.TextMetrics.ScalarCount(emoji));
    }

    [Fact]
    public void Suggested_copy_name_is_unique_in_the_category_and_the_copy_saves_with_a_new_id()
    {
        var (library, a, _, _) = Sample();
        var source = library.TemplatesIn(a).First();
        var name = library.SuggestCopyName(a, source.Name);
        Assert.Equal("a1" + Strings.CopySuffix, name);
        var copy = library.AddTemplate(a, name, source.Body);
        Assert.True(copy.Succeeded);
        Assert.NotEqual(source.Id, copy.Id);
        var saved = copy.Library!.FindTemplate(copy.Id)!;
        Assert.Equal(source.Body, saved.Body); // 本文・空白・改行はそのまま
        Assert.Equal(source, copy.Library.FindTemplate(source.Id)); // 元は変わらない
        Assert.Equal(copy.Id, copy.Library.TemplatesIn(a).Last().Id); // カテゴリの末尾
        // もう一度複製すると番号付き。
        Assert.Equal("a1" + string.Format(Strings.CopySuffixNumberedFormat, 2), copy.Library.SuggestCopyName(a, source.Name));
    }

    // ---- 抽出・削除 ----

    [Fact]
    public void Subset_keeps_only_the_chosen_categories_with_ids_and_order()
    {
        var (library, a, b, empty) = Sample();
        var subset = library.Subset([empty, a]);
        Assert.Equal([a, empty], subset.Categories.Select(c => c.Id));
        Assert.Equal(["a1", "a2"], subset.TemplatesInDisplayOrder().Select(t => t.Name));
        Assert.DoesNotContain(subset.Templates, t => t.CategoryId == b);
        Assert.Equal(library.TemplatesIn(a), subset.TemplatesIn(a));
    }

    [Fact]
    public void Deleting_a_category_with_templates_removes_only_that_category()
    {
        var (library, a, b, _) = Sample();
        Assert.Equal(LibraryError.CategoryNotEmpty, library.DeleteCategory(a).Error); // 通常の削除は従来どおり
        var edit = library.DeleteCategoryWithTemplates(a);
        Assert.True(edit.Succeeded);
        Assert.Null(edit.Library!.FindCategory(a));
        Assert.Empty(edit.Library.TemplatesIn(a));
        Assert.Equal(library.TemplatesIn(b), edit.Library.TemplatesIn(b));
        Assert.Equal(LibraryError.CategoryNotFound, library.DeleteCategoryWithTemplates(Guid.NewGuid()).Error);
    }

    [Fact]
    public void Index_follows_the_list_order_and_counts()
    {
        var (library, a, b, empty) = Sample();
        Assert.Equal(["a1", "a2"], library.TemplatesIn(a).Select(t => t.Name));
        Assert.Equal(2, library.CountIn(a));
        Assert.Equal(1, library.CountIn(b));
        Assert.Equal(0, library.CountIn(empty));
        var moved = library.MoveTemplate(library.TemplatesIn(a).Last().Id, -1).Library!;
        Assert.Equal(["a2", "a1"], moved.TemplatesIn(a).Select(t => t.Name)); // 新しいライブラリは新しい索引
    }

    // ---- 追加インポート ----

    private static TemplateLibrary File(params (string Category, string[] Templates)[] categories)
    {
        var library = TemplateLibrary.Empty;
        foreach (var (category, templates) in categories)
        {
            var c = library.AddCategory(category);
            library = c.Library!;
            foreach (var t in templates) library = library.AddTemplate(c.Id, t, "file " + t).Library!;
        }
        return library;
    }

    private static Dictionary<Guid, MergeMode> All(TemplateLibrary file, MergeMode mode = MergeMode.AddAsNew) =>
        file.Categories.ToDictionary(c => c.Id, _ => mode);

    [Fact]
    public void Append_adds_new_categories_after_the_existing_ones_with_new_ids()
    {
        var (current, a, b, empty) = Sample();
        var file = File(("New", ["n1", "n2"]), ("Other", ["o1"]));
        var plan = LibraryMerge.Plan(current, file, All(file));
        var merged = LibraryMerge.Apply(current, file, plan).Library;
        Assert.Equal(["A", "B", "Empty", "New", "Other"], merged.Categories.Select(c => c.Name));
        Assert.Equal(current.Categories, merged.Categories.Take(3)); // 既存はそのまま
        Assert.Equal(current.TemplatesInDisplayOrder(), merged.TemplatesInDisplayOrder().Take(3));
        var fileIds = file.Categories.Select(c => c.Id).Concat(file.Templates.Select(t => t.Id)).ToHashSet();
        Assert.DoesNotContain(merged.Categories, c => fileIds.Contains(c.Id));
        Assert.DoesNotContain(merged.Templates, t => fileIds.Contains(t.Id));
        var added = merged.Categories[3];
        Assert.Equal(["n1", "n2"], merged.TemplatesIn(added.Id).Select(t => t.Name)); // 所属の付け替えと順序
        Assert.Equal("file n1", merged.TemplatesIn(added.Id).First().Body);
    }

    [Fact]
    public void Only_the_chosen_categories_are_imported()
    {
        var (current, _, _, _) = Sample();
        var file = File(("New", ["n1"]), ("Skip", ["s1"]));
        var plan = LibraryMerge.Plan(current, file, new Dictionary<Guid, MergeMode> { [file.Categories[0].Id] = MergeMode.AddAsNew });
        var merged = LibraryMerge.Apply(current, file, plan).Library;
        Assert.DoesNotContain(merged.Categories, c => c.Name == "Skip");
        Assert.DoesNotContain(merged.Templates, t => t.Name == "s1");
    }

    [Fact]
    public void Same_names_are_renamed_against_existing_and_other_imported_categories()
    {
        var (current, _, _, _) = Sample();
        var withImported = current.AddCategory("A" + Strings.ImportSuffix).Library!;
        var file = File(("a", ["x"]), ("B", ["y"]));
        var plan = LibraryMerge.Plan(withImported, file, All(file));
        Assert.Equal("a" + string.Format(Strings.ImportSuffixNumberedFormat, 2), plan[0].ResultName); // 「A（取り込み）」は既にある
        Assert.Equal("B" + Strings.ImportSuffix, plan[1].ResultName);
        var merged = LibraryMerge.Apply(withImported, file, plan).Library;
        Assert.Equal(merged.Categories.Count, merged.Categories.Select(c => c.Name.ToUpperInvariant()).Distinct().Count());
        foreach (var c in merged.Categories) Assert.Equal(LibraryError.None, TemplateRules.CheckName(c.Name, TemplateRules.CategoryNameMax, out _));
    }

    [Fact]
    public void Renamed_category_stays_within_the_name_limit()
    {
        var name = new string('長', TemplateRules.CategoryNameMax);
        var current = TemplateLibrary.Empty.AddCategory(name).Library!;
        var file = File((name, ["t"]));
        var step = Assert.Single(LibraryMerge.Plan(current, file, All(file)));
        Assert.Equal(LibraryError.None, TemplateRules.CheckName(step.ResultName, TemplateRules.CategoryNameMax, out _));
        Assert.EndsWith(Strings.ImportSuffix, step.ResultName);
    }

    [Fact]
    public void Importing_the_same_file_twice_makes_another_category()
    {
        var (current, _, _, _) = Sample();
        var file = File(("New", ["n1"]));
        var once = LibraryMerge.Apply(current, file, LibraryMerge.Plan(current, file, All(file))).Library;
        var twice = LibraryMerge.Apply(once, file, LibraryMerge.Plan(once, file, All(file))).Library;
        Assert.Equal(["A", "B", "Empty", "New", "New" + Strings.ImportSuffix], twice.Categories.Select(c => c.Name));
    }

    [Fact]
    public void Replace_keeps_the_category_and_reuses_ids_of_templates_with_the_same_name()
    {
        var (current, a, b, _) = Sample();
        var a1 = current.TemplatesIn(a).First(t => t.Name == "a1");
        var a2 = current.TemplatesIn(a).First(t => t.Name == "a2");
        var file = File(("a", ["A1", "new"]));
        var plan = LibraryMerge.Plan(current, file, All(file, MergeMode.ReplaceContents));
        var step = Assert.Single(plan);
        Assert.Equal(MergeMode.ReplaceContents, step.Mode);
        Assert.Equal("A", step.ResultName); // 既存の名前のまま
        var result = LibraryMerge.Apply(current, file, plan);
        var merged = result.Library;
        Assert.Equal(current.Categories, merged.Categories); // ID・位置・名前は変わらない
        Assert.Equal(["A1", "new"], merged.TemplatesIn(a).Select(t => t.Name)); // ファイルの名前・順序
        Assert.Equal(a1.Id, merged.TemplatesIn(a).First().Id); // 同じ名前は同じ ID（固定を保つ）
        Assert.Equal("file A1", merged.TemplatesIn(a).First().Body);
        Assert.Equal([a2.Id], result.RemovedTemplateIds); // ファイルに無いものは消える
        Assert.Equal(current.TemplatesIn(b), merged.TemplatesIn(b));
    }

    [Fact]
    public void Replace_without_a_same_named_category_adds_it()
    {
        var (current, _, _, _) = Sample();
        var file = File(("New", ["n1"]));
        var step = Assert.Single(LibraryMerge.Plan(current, file, All(file, MergeMode.ReplaceContents)));
        Assert.Equal(MergeMode.AddAsNew, step.Mode);
        Assert.Equal("New", step.ResultName);
    }

    // ---- 一覧の行・検索 ----

    [Fact]
    public void Picker_rows_follow_collapse_state_and_hide_empty_categories()
    {
        var (library, a, b, empty) = Sample();
        var rows = TemplateListView.ForPicker(library, "", new HashSet<Guid> { a });
        Assert.Collection(rows,
            r => Assert.Equal(new CategoryEntry(library.FindCategory(a)!, 2, false), r),
            r => Assert.Equal(new CategoryEntry(library.FindCategory(b)!, 1, true), r),
            r => Assert.Equal("b1", Assert.IsType<TemplateEntry>(r).Template.Name));
        Assert.DoesNotContain(rows, r => r.CategoryId == empty);
        // 不在の ID は無視して、すべて開く。
        Assert.Equal(5, TemplateListView.ForPicker(library, "", new HashSet<Guid> { Guid.NewGuid() }).Count);
    }

    [Fact]
    public void Search_ignores_collapse_state_and_lists_templates_with_their_category()
    {
        var (library, a, b, _) = Sample();
        var rows = TemplateListView.ForPicker(library, "A2", new HashSet<Guid> { a, b });
        var row = Assert.IsType<TemplateEntry>(Assert.Single(rows));
        Assert.Equal("a2", row.Template.Name);
        Assert.Equal("A", row.Category!.Name);
        // カテゴリ名で一致すれば、そのカテゴリの全テンプレート。
        Assert.Equal(["b1"], TemplateListView.ForPicker(library, "b", new HashSet<Guid>()).OfType<TemplateEntry>()
            .Where(t => t.Category!.Id == b).Select(t => t.Template.Name));
        // 空白で区切った語の AND（カテゴリ名とテンプレート名にまたがってよい）。全角の空白も区切り。
        Assert.Equal(["a1"], TemplateListView.ForPicker(library, "a　1", new HashSet<Guid>()).OfType<TemplateEntry>().Select(t => t.Template.Name));
        Assert.Empty(TemplateListView.ForPicker(library, "a b1", new HashSet<Guid>()));
        // 本文は検索しない。
        Assert.Empty(TemplateListView.ForPicker(library, "body", new HashSet<Guid>()));
    }

    [Fact]
    public void Editor_rows_show_all_categories_and_matching_headings_while_searching()
    {
        var (library, a, b, empty) = Sample();
        Assert.Equal(6, TemplateListView.ForEditor(library, "").Count);
        var rows = TemplateListView.ForEditor(library, "empty");
        Assert.Equal(empty, Assert.IsType<CategoryEntry>(Assert.Single(rows)).Category.Id);
        rows = TemplateListView.ForEditor(library, "a1");
        Assert.Equal([a, a], rows.Select(r => r.CategoryId));
        Assert.Equal(2, Assert.IsType<CategoryEntry>(rows[0]).Count); // 件数は絞る前の数
    }

    [Fact]
    public void Picker_rows_scale_to_large_libraries()
    {
        var library = TemplateLibrary.Empty;
        var categories = Enumerable.Range(0, 100).Select(i => new Category(Guid.NewGuid(), $"Category {i}")).ToList();
        var templates = categories.SelectMany(c => Enumerable.Range(0, 100).Select(i => new Template(Guid.NewGuid(), c.Id, $"Template {i}", "x"))).ToList();
        library = new TemplateLibrary(categories, templates);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var all = TemplateListView.ForPicker(library, "", new HashSet<Guid>());
        var search = TemplateListView.ForPicker(library, "template 99", new HashSet<Guid>());
        var collapsed = TemplateListView.ForPicker(library, "", categories.Select(c => c.Id).ToHashSet());
        watch.Stop();
        Assert.Equal(10_100, all.Count);
        Assert.Equal(199, search.Count); // 各カテゴリの「Template 99」と、カテゴリ名が一致する「Category 99」の全件（重なる 1 件を除く）
        Assert.Equal(100, collapsed.Count); // 閉じたカテゴリの子の行は作らない
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }

    // ---- 自動起動の表示 ----

    [Fact]
    public void Startup_states_other_than_enabled_or_disabled_are_not_shown_as_plain_off()
    {
        Assert.Equal((true, true, false), Parts(StartupStates.View(StartupState.Enabled)));
        Assert.Equal((false, true, false), Parts(StartupStates.View(StartupState.Disabled)));
        Assert.Equal((false, false, true), Parts(StartupStates.View(StartupState.DisabledByUser)));
        Assert.Equal((true, false, false), Parts(StartupStates.View(StartupState.EnabledByPolicy)));
        Assert.Equal((false, false, false), Parts(StartupStates.View(StartupState.DisabledByPolicy)));
        Assert.Equal((false, false, false), Parts(StartupStates.View(StartupState.Unsupported)));
        Assert.Equal((false, false, true), Parts(StartupStates.View(StartupState.Unknown)));
        foreach (var state in Enum.GetValues<StartupState>().Where(s => s != StartupState.Disabled))
            Assert.NotEmpty(StartupStates.View(state).Status);

        static (bool, bool, bool) Parts(StartupView v) => (v.Checked, v.CanChange, v.ShowWindowsSettings);
    }
}
