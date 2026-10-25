using ClipCompose.Core.Model;

namespace ClipCompose.Tests;

/// <summary>カテゴリ・テンプレートの管理と入力規則（仕様書 §4）。</summary>
public sealed class TemplateLibraryTests
{
    private static (TemplateLibrary Library, Guid A, Guid B) TwoCategories()
    {
        var a = TemplateLibrary.Empty.AddCategory("A");
        var b = a.Library!.AddCategory("B");
        return (b.Library!, a.Id, b.Id);
    }

    [Fact]
    public void Names_are_trimmed_but_bodies_are_not()
    {
        var (library, a, _) = TwoCategories();
        var edit = library.AddTemplate(a, "  name  ", "  body\r\n");
        var t = edit.Library!.FindTemplate(edit.Id)!;
        Assert.Equal("name", t.Name);
        Assert.Equal("  body\r\n", t.Body);
    }

    [Theory]
    [InlineData("", LibraryError.NameEmpty)]
    [InlineData("   ", LibraryError.NameEmpty)]
    [InlineData("a\nb", LibraryError.NameInvalidCharacters)]
    [InlineData("a\0", LibraryError.NameInvalidCharacters)]
    public void Invalid_names_are_rejected(string name, LibraryError expected)
    {
        Assert.Equal(expected, TemplateLibrary.Empty.AddCategory(name).Error);
    }

    [Theory]
    [InlineData(" \r\n\t", LibraryError.BodyEmpty)]
    [InlineData("a\0b", LibraryError.BodyInvalidCharacters)]
    public void Invalid_bodies_are_rejected(string body, LibraryError expected)
    {
        var (library, a, _) = TwoCategories();
        Assert.Equal(expected, library.AddTemplate(a, "n", body).Error);
    }

    [Fact]
    public void Lone_surrogates_are_rejected()
    {
        // 属性の引数（InlineData）では対になっていないサロゲートが U+FFFD に置き換わるため、コードで渡す。
        Assert.Equal(LibraryError.NameInvalidCharacters, TemplateLibrary.Empty.AddCategory("a\uD800").Error);
        var (library, a, _) = TwoCategories();
        Assert.Equal(LibraryError.BodyInvalidCharacters, library.AddTemplate(a, "n", "\uDC00").Error);
        Assert.Equal(LibraryError.BodyInvalidCharacters, library.AddTemplate(a, "n", "x\uD83D").Error);
    }

    [Fact]
    public void Limits_count_unicode_scalars()
    {
        Assert.True(TemplateLibrary.Empty.AddCategory(new string('a', TemplateRules.CategoryNameMax)).Succeeded);
        Assert.Equal(LibraryError.NameTooLong, TemplateLibrary.Empty.AddCategory(new string('a', TemplateRules.CategoryNameMax + 1)).Error);
        // 絵文字 50 個（UTF-16 では 100）は 50 文字。
        Assert.True(TemplateLibrary.Empty.AddCategory(string.Concat(Enumerable.Repeat("😀", TemplateRules.CategoryNameMax))).Succeeded);

        var (library, a, _) = TwoCategories();
        Assert.True(library.AddTemplate(a, new string('n', TemplateRules.TemplateNameMax), "b").Succeeded);
        Assert.Equal(LibraryError.NameTooLong, library.AddTemplate(a, new string('n', TemplateRules.TemplateNameMax + 1), "b").Error);
        Assert.True(library.AddTemplate(a, "n", string.Concat(Enumerable.Repeat("😀", TemplateRules.BodyMax))).Succeeded);
        Assert.Equal(LibraryError.BodyTooLong, library.AddTemplate(a, "n", new string('b', TemplateRules.BodyMax + 1)).Error);
        // 結合文字は個別に数える。
        Assert.Equal(LibraryError.BodyTooLong, library.AddTemplate(a, "n", string.Concat(Enumerable.Repeat("é", TemplateRules.BodyMax / 2 + 1))).Error);
    }

    [Fact]
    public void Category_names_are_unique_ignoring_case_and_surrounding_spaces()
    {
        var (library, a, _) = TwoCategories();
        Assert.Equal(LibraryError.NameDuplicate, library.AddCategory(" a ").Error);
        Assert.Equal(LibraryError.NameDuplicate, library.RenameCategory(a, "b").Error);
        Assert.True(library.RenameCategory(a, "a").Succeeded); // 自分自身との重複は問題にしない
    }

    [Fact]
    public void Template_names_are_unique_only_within_a_category()
    {
        var (library, a, b) = TwoCategories();
        library = library.AddTemplate(a, "Same", "x").Library!;
        Assert.Equal(LibraryError.NameDuplicate, library.AddTemplate(a, "SAME", "y").Error);
        Assert.True(library.AddTemplate(b, "same", "y").Succeeded);
    }

    [Fact]
    public void Category_with_templates_cannot_be_deleted()
    {
        var (library, a, _) = TwoCategories();
        var edit = library.AddTemplate(a, "t", "x");
        Assert.Equal(LibraryError.CategoryNotEmpty, edit.Library!.DeleteCategory(a).Error);
        var emptied = edit.Library.DeleteTemplate(edit.Id).Library!;
        Assert.True(emptied.DeleteCategory(a).Succeeded);
    }

    [Fact]
    public void Ids_do_not_change_on_rename_move_or_edit()
    {
        var (library, a, b) = TwoCategories();
        var edit = library.AddTemplate(a, "t", "x");
        var id = edit.Id;
        library = edit.Library!
            .RenameCategory(a, "A2").Library!
            .MoveCategory(a, 1).Library!
            .UpdateTemplate(id, b, "t2", "y").Library!;
        var t = library.FindTemplate(id)!;
        Assert.Equal(b, t.CategoryId);
        Assert.Equal("t2", t.Name);
        Assert.Equal(a, library.Categories[1].Id);
    }

    [Fact]
    public void Moving_to_another_category_puts_the_template_last()
    {
        var (library, a, b) = TwoCategories();
        var x = library.AddTemplate(b, "x", "1");
        var y = x.Library!.AddTemplate(a, "y", "2");
        library = y.Library!.UpdateTemplate(y.Id, b, "y", "2").Library!;
        Assert.Equal(["x", "y"], library.TemplatesIn(b).Select(t => t.Name));
    }

    [Fact]
    public void Templates_are_reordered_within_their_category()
    {
        var (library, a, b) = TwoCategories();
        library = library.AddTemplate(a, "a1", "x").Library!.AddTemplate(b, "b1", "x").Library!;
        var a2 = library.AddTemplate(a, "a2", "x");
        library = a2.Library!.MoveTemplate(a2.Id, -1).Library!;
        Assert.Equal(["a2", "a1"], library.TemplatesIn(a).Select(t => t.Name));
        Assert.Equal(["b1"], library.TemplatesIn(b).Select(t => t.Name));
        // 端を越える移動は変えない。
        Assert.Equal(["a2", "a1"], library.MoveTemplate(a2.Id, -1).Library!.TemplatesIn(a).Select(t => t.Name));
    }

    [Fact]
    public void Failed_edits_leave_the_library_unchanged()
    {
        var (library, a, _) = TwoCategories();
        var before = library.Categories.Count;
        Assert.False(library.AddCategory("").Succeeded);
        Assert.Null(library.AddCategory("").Library);
        Assert.Equal(before, library.Categories.Count);
        Assert.Equal(LibraryError.CategoryNotFound, library.AddTemplate(Guid.NewGuid(), "t", "x").Error);
        Assert.Equal(LibraryError.TemplateNotFound, library.UpdateTemplate(Guid.NewGuid(), a, "t", "x").Error);
    }

    [Fact]
    public void Text_given_to_screen_readers_is_the_name_only()
    {
        // 一覧の項目は ToString が UI Automation の名前になる。ID・本文を読み上げない。
        var c = new Category(Guid.NewGuid(), "カテゴリ");
        Assert.Equal("カテゴリ", c.ToString());
        Assert.Equal("名前", new Template(Guid.NewGuid(), c.Id, "名前", "secret body").ToString());
    }

    [Fact]
    public void Display_order_follows_categories()
    {
        var (library, a, b) = TwoCategories();
        library = library.AddTemplate(b, "b1", "x").Library!.AddTemplate(a, "a1", "x").Library!;
        Assert.Equal(["a1", "b1"], library.TemplatesInDisplayOrder().Select(t => t.Name));
    }
}
