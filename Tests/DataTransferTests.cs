using System.Text;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;
using ClipCompose.Core.Storage;

namespace ClipCompose.Tests;

/// <summary>
/// カテゴリ単位のエクスポート・追加インポート・中身のあるカテゴリの削除の保存手順と、開閉状態の設定（仕様書 §11）。
/// 個人のデータではなく一時フォルダーで行う。
/// </summary>
public sealed class DataTransferTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly TemplateStore _templates;
    private readonly SettingsStore _settings;
    private readonly TemplateLibrary _current;
    private readonly Guid _a;
    private readonly Guid _b;
    private readonly Guid _pinned;

    public DataTransferTests()
    {
        _templates = new TemplateStore(_dir.Path, () => new DateTime(2026, 10, 28, 9, 0, 0));
        _settings = new SettingsStore(_dir.Path);
        var a = TemplateLibrary.Empty.AddCategory("A");
        var b = a.Library!.AddCategory("B");
        var pinned = b.Library!.AddTemplate(a.Id, "pinned", "pinned body");
        _current = pinned.Library!.AddTemplate(a.Id, "other", "other body").Library!.AddTemplate(b.Id, "b1", "secret b1").Library!;
        (_a, _b, _pinned) = (a.Id, b.Id, pinned.Id);
        Assert.True(_templates.Save(_current).Succeeded);
        Assert.True(_settings.Save(new AppSettings { FixedTemplateId = _pinned }).Succeeded);
    }

    public void Dispose() => _dir.Dispose();

    private AppSettings Pinned() => new() { FixedTemplateId = _pinned };

    private string[] Copies(string label) => Directory.GetFiles(_dir.Path, $"templates.{label}-*.json");

    [Fact]
    public void Partial_export_is_a_normal_library_file_with_only_the_chosen_categories()
    {
        var path = _dir.File("out.json");
        Assert.True(_templates.Export(path, _current.Subset([_a])).Succeeded);
        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("secret b1", text); // 選ばなかったカテゴリの本文は含まない
        Assert.DoesNotContain("fixedTemplateId", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schemaVersion\": 1", text);
        var read = _templates.ReadImport(path);
        Assert.True(read.Succeeded, read.Problem);
        Assert.Equal(_current.Subset([_a]).Categories, read.Library!.Categories);
        Assert.Equal(_current.TemplatesIn(_a), read.Library.TemplatesInDisplayOrder());
    }

    [Fact]
    public void Append_keeps_existing_data_and_pin_and_backs_up_first()
    {
        var file = TemplateLibrary.Empty.AddCategory("A");
        var imported = file.Library!.AddTemplate(file.Id, "pinned", "file body", _pinned).Library!; // 同じ ID を含むファイル
        var merged = LibraryMerge.Apply(_current, imported, LibraryMerge.Plan(_current, imported, new Dictionary<Guid, MergeMode> { [file.Id] = MergeMode.AddAsNew }));
        var outcome = ImportProcedure.SaveRemoving(_templates, _settings, Pinned(), merged.Library, merged.RemovedTemplateIds, "before-import");
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Equal(_pinned, outcome.Settings.FixedTemplateId); // 固定はそのまま
        var saved = _templates.Load().Library;
        Assert.Equal("pinned body", saved.FindTemplate(_pinned)!.Body); // 既存は上書きされない
        Assert.Equal(3, saved.Categories.Count);
        Assert.Single(Copies("before-import"));
    }

    [Fact]
    public void Replacing_contents_unpins_only_when_the_pinned_template_disappears()
    {
        var file = TemplateLibrary.Empty.AddCategory("A");
        var imported = file.Library!.AddTemplate(file.Id, "new only", "x").Library!;
        var merged = LibraryMerge.Apply(_current, imported, LibraryMerge.Plan(_current, imported, new Dictionary<Guid, MergeMode> { [file.Id] = MergeMode.ReplaceContents }));
        Assert.Contains(_pinned, merged.RemovedTemplateIds);
        var outcome = ImportProcedure.SaveRemoving(_templates, _settings, Pinned(), merged.Library, merged.RemovedTemplateIds, "before-import");
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Null(outcome.Settings.FixedTemplateId);
        Assert.Null(new SettingsStore(_dir.Path).Load().Settings.FixedTemplateId);
        Assert.Equal(["new only"], _templates.Load().Library.TemplatesIn(_a).Select(t => t.Name));
    }

    [Fact]
    public void Failed_append_changes_nothing()
    {
        var file = TemplateLibrary.Empty.AddCategory("New");
        var imported = file.Library!.AddTemplate(file.Id, "n", "x").Library!;
        var merged = LibraryMerge.Apply(_current, imported, LibraryMerge.Plan(_current, imported, new Dictionary<Guid, MergeMode> { [file.Id] = MergeMode.AddAsNew }));
        var before = File.ReadAllBytes(_templates.FilePath);
        using (new FileStream(_templates.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var outcome = ImportProcedure.SaveRemoving(_templates, _settings, Pinned(), merged.Library, merged.RemovedTemplateIds, "before-import");
            Assert.False(outcome.Succeeded);
            Assert.Contains(_templates.FilePath, outcome.Problem);
        }
        Assert.Equal(before, File.ReadAllBytes(_templates.FilePath)); // 途中まで追加されない
    }

    [Fact]
    public void Merged_result_over_the_size_limit_is_stopped_before_making_a_copy()
    {
        // 1 件 2 万文字（3 バイト文字）× 400 件 ≒ 24 MB。入力ファイルが上限内でも、結合後が上限を超えれば保存しない。
        var big = TemplateLibrary.Empty.AddCategory("Big");
        var library = big.Library!;
        var body = new string('あ', TemplateRules.BodyMax);
        var templates = Enumerable.Range(0, 400).Select(i => new Template(Guid.NewGuid(), big.Id, $"t{i}", body)).ToList();
        var merged = new TemplateLibrary([.. _current.Categories, .. library.Categories], [.. _current.Templates, .. templates]);
        var before = File.ReadAllBytes(_templates.FilePath);
        var result = _templates.SaveAfterCopy(merged, "before-import");
        Assert.False(result.Succeeded);
        Assert.Contains(_templates.FilePath, result.Problem);
        Assert.Empty(Copies("before-import"));
        Assert.Equal(before, File.ReadAllBytes(_templates.FilePath));
    }

    [Fact]
    public void Deleting_a_category_with_the_pinned_template_unpins_first_and_keeps_a_copy()
    {
        var edit = _current.DeleteCategoryWithTemplates(_a);
        var removed = _current.TemplatesIn(_a).Select(t => t.Id).ToHashSet();
        var outcome = ImportProcedure.SaveRemoving(_templates, _settings, Pinned(), edit.Library!, removed, "before-delete");
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Null(outcome.Settings.FixedTemplateId);
        var copy = Assert.Single(Copies("before-delete"));
        // 残した写しは［全件置換で復元］でそのまま読める。
        var restored = _templates.ReadImport(copy);
        Assert.True(restored.Succeeded, restored.Problem);
        Assert.Equal(_current.TemplatesInDisplayOrder(), restored.Library!.TemplatesInDisplayOrder());
    }

    [Fact]
    public void Failure_to_unpin_cancels_the_deletion()
    {
        var edit = _current.DeleteCategoryWithTemplates(_a);
        var removed = _current.TemplatesIn(_a).Select(t => t.Id).ToHashSet();
        using (new FileStream(_settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = ImportProcedure.SaveRemoving(_templates, _settings, Pinned(), edit.Library!, removed, "before-delete");
            Assert.False(outcome.Succeeded);
            Assert.Equal(_pinned, outcome.Settings.FixedTemplateId);
        }
        Assert.NotNull(_templates.Load().Library.FindCategory(_a));
        Assert.Empty(Copies("before-delete"));
    }

    [Fact]
    public void Deleting_without_the_pin_does_not_touch_the_settings()
    {
        var settingsBefore = File.ReadAllBytes(_settings.FilePath);
        var edit = _current.DeleteCategoryWithTemplates(_b);
        var outcome = ImportProcedure.SaveRemoving(_templates, _settings, Pinned(), edit.Library!, _current.TemplatesIn(_b).Select(t => t.Id).ToHashSet(), "before-delete");
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Equal(_pinned, outcome.Settings.FixedTemplateId);
        Assert.Equal(settingsBefore, File.ReadAllBytes(_settings.FilePath));
    }

    // ---- エクスポートの保存先 ----

    [Theory]
    [InlineData(new[] { "翻訳" }, "ClipCompose-翻訳.json")]
    [InlineData(new[] { "a/b:c*?" }, "ClipCompose-a_b_c__.json")]
    [InlineData(new[] { "CON" }, "ClipCompose-_CON.json")]
    [InlineData(new[] { "end. " }, "ClipCompose-end.json")]
    [InlineData(new[] { "a", "b" }, "ClipCompose-templates-20261028.json")]
    public void Export_file_name_is_safe(string[] names, string expected)
    {
        Assert.Equal(expected, ExportFileName.Suggest(names, new DateTime(2026, 10, 28)));
    }

    [Fact]
    public void Export_file_name_is_shortened()
    {
        var name = ExportFileName.Suggest([new string('x', 50)], DateTime.Now);
        Assert.True(name.Length <= "ClipCompose-.json".Length + 60);
    }

    [Fact]
    public void Exports_into_the_data_folder_are_refused()
    {
        Assert.True(ExportFileName.IsInDataDirectory(_templates.FilePath, _dir.Path));
        Assert.True(ExportFileName.IsInDataDirectory(Path.Combine(_dir.Path.ToUpperInvariant(), "x.json"), _dir.Path + Path.DirectorySeparatorChar));
        Assert.False(ExportFileName.IsInDataDirectory(Path.Combine(_dir.Path, "sub", "x.json"), _dir.Path));
        Assert.False(ExportFileName.IsInDataDirectory(Path.Combine(Path.GetTempPath(), "x.json"), _dir.Path));
    }

    // ---- 開閉状態の設定 ----

    [Fact]
    public void Collapsed_categories_round_trip_and_are_normalized()
    {
        var id = Guid.NewGuid();
        var s = new AppSettings { CollapsedCategoryIds = [id] };
        Assert.True(_settings.Save(s).Succeeded);
        Assert.Equal([id], new SettingsStore(_dir.Path).Load().Settings.CollapsedCategoryIds);

        File.WriteAllText(_settings.FilePath, $$"""{"schemaVersion":1,"collapsedCategoryIds":["{{id}}","{{id}}","{{Guid.Empty}}"]}""");
        Assert.Equal([id], new SettingsStore(_dir.Path).Load().Settings.CollapsedCategoryIds);
        File.WriteAllText(_settings.FilePath, """{"schemaVersion":1,"collapsedCategoryIds":null}""");
        var load = new SettingsStore(_dir.Path).Load();
        Assert.Equal(LoadKind.Loaded, load.Kind);
        Assert.Empty(load.Settings.CollapsedCategoryIds);
    }

    [Fact]
    public void Settings_of_1_0_0_still_load_with_everything_expanded()
    {
        File.WriteAllText(_settings.FilePath, $$"""
            {"schemaVersion":1,"fixedTemplateId":"{{_pinned}}","normalHotkey":"Ctrl+Alt+Space","fixedHotkey":"","normalAutoPaste":false,
             "fixedAutoPaste":true,"language":"ja","trayNoticeShown":true,"pickerPreview":true}
            """);
        var load = new SettingsStore(_dir.Path).Load();
        Assert.Equal(LoadKind.Loaded, load.Kind);
        Assert.Empty(load.Settings.CollapsedCategoryIds);
        Assert.Equal(_pinned, load.Settings.FixedTemplateId);
        Assert.Empty(load.Fixes);
    }

    [Fact]
    public void Clone_copies_the_collapsed_list()
    {
        var s = new AppSettings { CollapsedCategoryIds = [_a] };
        var copy = s.Clone();
        copy.CollapsedCategoryIds.Add(_b);
        Assert.Equal([_a], s.CollapsedCategoryIds);
    }
}
