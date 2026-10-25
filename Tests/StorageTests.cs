using System.Text;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;
using ClipCompose.Core.Storage;

namespace ClipCompose.Tests;

/// <summary>テストごとの一時フォルダー（個人のデータを使わない）。</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipCompose.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>保存・読込障害・復旧・インポート（仕様書 §11、受入条件 6・7）。</summary>
public sealed class StorageTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly TemplateStore _store;

    public StorageTests()
    {
        _store = new TemplateStore(_dir.Path, () => new DateTime(2026, 10, 25, 12, 0, 0));
    }

    public void Dispose() => _dir.Dispose();

    private static TemplateLibrary Sample(string body = "本文 {{clipboard}}\r\n  ")
    {
        var c = TemplateLibrary.Empty.AddCategory("カテゴリ");
        var c2 = c.Library!.AddCategory("Second");
        return c2.Library!
            .AddTemplate(c.Id, "テンプレート", body).Library!
            .AddTemplate(c.Id, "😀 two", "定型文").Library!
            .AddTemplate(c2.Id, "x", "y").Library!;
    }

    private static void AssertSame(TemplateLibrary expected, TemplateLibrary actual)
    {
        Assert.Equal(expected.Categories, actual.Categories);
        Assert.Equal(expected.TemplatesInDisplayOrder(), actual.TemplatesInDisplayOrder());
    }

    [Fact]
    public void First_run_is_distinguished_from_a_read_failure()
    {
        var load = _store.Load();
        Assert.Equal(LoadKind.Missing, load.Kind);
        Assert.False(_store.SaveBlocked);
        Assert.Empty(load.Library.Categories);
    }

    [Fact]
    public void Saved_data_is_read_back_exactly()
    {
        var library = Sample();
        Assert.True(_store.Save(library).Succeeded);
        var load = new TemplateStore(_dir.Path).Load();
        Assert.Equal(LoadKind.Loaded, load.Kind);
        AssertSame(library, load.Library);
        // 日本語はエスケープせず UTF-8 のまま書く（BOM なし）。
        var bytes = File.ReadAllBytes(_store.FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("テンプレート", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Previous_good_file_is_kept_as_backup()
    {
        var first = Sample("first");
        _store.Save(first);
        _store.Save(Sample("second"));
        Assert.True(File.Exists(_store.BackupPath));
        AssertSame(first, _store.ReadBackup().Library!);
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion": 1, "categories": [], "templates": [], "extra": 1}""")]
    [InlineData("""{"schemaVersion": 1, "categories": []}""")]
    [InlineData("""{"schemaVersion": 1, "schemaVersion": 1, "categories": [], "templates": []}""")]
    [InlineData("""{"schemaVersion": 99, "categories": [], "templates": [], "newField": true}""")]
    [InlineData("""{"schemaVersion": 0, "categories": [], "templates": []}""")]
    [InlineData("""{"categories": [], "templates": []}""")]
    [InlineData("""{"schemaVersion": 1, "categories": [{"id": "11111111-1111-1111-1111-111111111111", "name": null, "order": 0}], "templates": []}""")]
    // レビュー指摘 1: 以前は例外になり、復旧の案内まで進めずに起動に失敗していた。
    [InlineData("""{"schemaVersion": "1", "categories": [], "templates": []}""")]
    [InlineData("""{"schemaVersion": 1.5, "categories": [], "templates": []}""")]
    [InlineData("""{"schemaVersion": null, "categories": [], "templates": []}""")]
    [InlineData("""{"schemaVersion": 1, "categories": [null], "templates": []}""")]
    [InlineData("""{"schemaVersion": 1, "categories": [], "templates": [null]}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("""1""")]
    public void Invalid_files_are_not_repaired_or_overwritten(string content)
    {
        File.WriteAllText(_store.FilePath, content);
        var load = _store.Load();
        Assert.Equal(LoadKind.Failed, load.Kind);
        Assert.Contains(_store.FilePath, load.Problem); // 利用者が確認できるようにフルパスを示す
        Assert.True(_store.SaveBlocked);

        Assert.False(_store.Save(Sample()).Succeeded);
        Assert.False(_store.ReplaceWithImport(Sample()).Succeeded);
        Assert.Equal(content, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void Newer_schema_is_reported_as_such()
    {
        File.WriteAllText(_store.FilePath, """{"schemaVersion": 2, "categories": [], "templates": [], "tags": []}""");
        Assert.Contains("2", _store.Load().Problem);
    }

    [Fact]
    public void Referential_and_uniqueness_problems_are_rejected_without_body_text()
    {
        const string secret = "SECRET-BODY";
        var a = Guid.NewGuid();
        var t = Guid.NewGuid();
        var json = $$"""
        {
          "schemaVersion": 1,
          "categories": [{"id": "{{a}}", "name": "A", "order": 0}, {"id": "{{a}}", "name": "B", "order": 1}],
          "templates": [
            {"id": "{{t}}", "categoryId": "{{Guid.NewGuid()}}", "name": "t", "body": "{{secret}}", "order": 0},
            {"id": "{{t}}", "categoryId": "{{a}}", "name": "u", "body": "   ", "order": 1}
          ]
        }
        """;
        File.WriteAllText(_store.FilePath, json);
        var load = _store.Load();
        Assert.Equal(LoadKind.Failed, load.Kind);
        Assert.DoesNotContain(secret, load.Problem);
        Assert.True(_store.SaveBlocked);
    }

    [Fact]
    public void Oversized_file_is_rejected()
    {
        using (var f = new FileStream(_store.FilePath, FileMode.Create))
            f.SetLength(SafeFile.MaxBytes + 1);
        Assert.Equal(LoadKind.Failed, _store.Load().Kind);
    }

    [Fact]
    public void Save_failure_keeps_the_existing_file_and_reports_the_path()
    {
        var original = Sample("original");
        _store.Save(original);
        var before = File.ReadAllBytes(_store.FilePath);

        // 他のプロセスが開いたままにしている状況（置き換えに失敗する）。
        using (new FileStream(_store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = _store.Save(Sample("changed"));
            Assert.False(result.Succeeded);
            Assert.Contains(_store.FilePath, result.Problem);
        }
        Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
        AssertSame(original, _store.Load().Library);
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
    }

    [Fact]
    public void Restore_from_backup_only_when_chosen_and_keeps_the_broken_file()
    {
        var good = Sample("good");
        _store.Save(good);
        _store.Save(Sample("newer"));
        File.WriteAllText(_store.FilePath, "{ broken");

        Assert.Equal(LoadKind.Failed, _store.Load().Kind);
        var backup = _store.ReadBackup();
        Assert.True(backup.Succeeded);

        Assert.True(_store.RestoreFrom(backup.Library!).Succeeded);
        Assert.False(_store.SaveBlocked);
        AssertSame(good, _store.Load().Library);
        var corrupt = Assert.Single(Directory.GetFiles(_dir.Path, "templates.corrupt-*.json"));
        Assert.Equal("{ broken", File.ReadAllText(corrupt));
    }

    [Fact]
    public void Starting_empty_keeps_the_broken_file()
    {
        File.WriteAllText(_store.FilePath, "{ broken");
        _store.Load();
        Assert.True(_store.StartEmpty().Succeeded);
        Assert.Empty(_store.Load().Library.Categories);
        Assert.Single(Directory.GetFiles(_dir.Path, "templates.corrupt-*.json"));
    }

    [Fact]
    public void Missing_backup_is_reported()
    {
        Assert.False(_store.ReadBackup().Succeeded);
    }

    [Fact]
    public void Export_and_import_round_trip_and_back_up_the_current_data()
    {
        var current = Sample("current");
        _store.Save(current);
        var other = new TemplateStore(Path.Combine(_dir.Path, "other"));
        var exported = Sample("exported");
        var exportPath = _dir.File("export.json");
        Assert.True(other.Export(exportPath, exported).Succeeded);

        var import = _store.ReadImport(exportPath);
        Assert.True(import.Succeeded);
        Assert.True(_store.ReplaceWithImport(import.Library!).Succeeded);

        AssertSame(exported, _store.Load().Library);
        var before = Assert.Single(Directory.GetFiles(_dir.Path, "templates.before-import-*.json"));
        Assert.True(TemplateFileFormat.Parse(File.ReadAllBytes(before)).Succeeded);
        AssertSame(current, TemplateFileFormat.Parse(File.ReadAllBytes(before)).Library!);
    }

    [Fact]
    public void Export_does_not_contain_pc_settings()
    {
        var path = _dir.File("export.json");
        _store.Export(path, Sample());
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("hotkey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("language", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_import_is_rejected_before_changing_anything()
    {
        var current = Sample("current");
        _store.Save(current);
        var path = _dir.File("bad.json");
        File.WriteAllText(path, """{"schemaVersion": 1, "categories": [], "templates": [{"id": "11111111-1111-1111-1111-111111111111", "categoryId": "22222222-2222-2222-2222-222222222222", "name": "t", "body": "b", "order": 0}]}""");
        var import = _store.ReadImport(path);
        Assert.False(import.Succeeded);
        Assert.Contains(path, import.Problem);
        AssertSame(current, _store.Load().Library);
        Assert.Empty(Directory.GetFiles(_dir.Path, "templates.before-import-*.json"));
    }

    [Fact]
    public void Import_save_failure_keeps_the_current_data()
    {
        var current = Sample("current");
        _store.Save(current);
        var before = File.ReadAllBytes(_store.FilePath);
        using (new FileStream(_store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(_store.ReplaceWithImport(Sample("imported")).Succeeded);
        }
        Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
    }

    [Fact]
    public void Order_fields_decide_the_display_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = $$"""
        {
          "schemaVersion": 1,
          "categories": [{"id": "{{a}}", "name": "A", "order": 1}, {"id": "{{b}}", "name": "B", "order": 0}],
          "templates": [
            {"id": "{{Guid.NewGuid()}}", "categoryId": "{{a}}", "name": "a2", "body": "x", "order": 5},
            {"id": "{{Guid.NewGuid()}}", "categoryId": "{{a}}", "name": "a1", "body": "x", "order": 2},
            {"id": "{{Guid.NewGuid()}}", "categoryId": "{{b}}", "name": "b1", "body": "x", "order": 0}
          ]
        }
        """;
        var parsed = TemplateFileFormat.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Succeeded, parsed.Problem);
        Assert.Equal(["B", "A"], parsed.Library!.Categories.Select(c => c.Name));
        Assert.Equal(["b1", "a1", "a2"], parsed.Library.TemplatesInDisplayOrder().Select(t => t.Name));
    }

    [Fact]
    public void Utf8_bom_is_accepted()
    {
        var bytes = TemplateFileFormat.Serialize(Sample());
        File.WriteAllBytes(_store.FilePath, [0xEF, 0xBB, 0xBF, .. bytes]);
        Assert.Equal(LoadKind.Loaded, _store.Load().Kind);
    }
}

/// <summary>
/// インポートと固定の解除の順序（仕様書 §11、レビュー指摘 6）。置き換わったのに固定が残る状態を作らない。
/// </summary>
public sealed class ImportProcedureTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly TemplateStore _templates;
    private readonly SettingsStore _settings;
    private readonly TemplateLibrary _current;
    private readonly Guid _pinned;

    public ImportProcedureTests()
    {
        _templates = new TemplateStore(_dir.Path);
        _settings = new SettingsStore(_dir.Path);
        var c = TemplateLibrary.Empty.AddCategory("C");
        var t = c.Library!.AddTemplate(c.Id, "pinned", "current body");
        _current = t.Library!;
        _pinned = t.Id;
        _templates.Save(_current);
        _settings.Save(new AppSettings { FixedTemplateId = _pinned });
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>インポートするデータに、固定中と同じ ID のテンプレートが含まれる（同じ PC のエクスポートを戻す場合など）。</summary>
    private TemplateLibrary ImportedWithSameId()
    {
        var c = TemplateLibrary.Empty.AddCategory("Imported");
        return c.Library!.AddTemplate(c.Id, "same id", "imported body", _pinned).Library!;
    }

    private AppSettings Pinned() => new() { FixedTemplateId = _pinned };

    [Fact]
    public void Successful_import_unpins_even_when_the_same_id_is_imported()
    {
        var outcome = ImportProcedure.Replace(_templates, _settings, Pinned(), ImportedWithSameId());
        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Settings.FixedTemplateId);
        Assert.Null(new SettingsStore(_dir.Path).Load().Settings.FixedTemplateId);
        Assert.Equal("imported body", _templates.Load().Library.FindTemplate(_pinned)!.Body);
    }

    [Fact]
    public void Failure_to_save_the_unpin_cancels_the_import()
    {
        using (new FileStream(_settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = ImportProcedure.Replace(_templates, _settings, Pinned(), ImportedWithSameId());
            Assert.False(outcome.Succeeded);
            Assert.Equal(_pinned, outcome.Settings.FixedTemplateId); // 実行上も元のまま（テンプレートも元のまま）
            Assert.Contains(_settings.FilePath, outcome.Problem);
        }
        Assert.Equal("current body", _templates.Load().Library.FindTemplate(_pinned)!.Body);
        Assert.Equal(_pinned, new SettingsStore(_dir.Path).Load().Settings.FixedTemplateId);
        Assert.Empty(Directory.GetFiles(_dir.Path, "templates.before-import-*.json"));
    }

    [Fact]
    public void Failure_to_replace_the_templates_restores_the_pin()
    {
        using (new FileStream(_templates.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var outcome = ImportProcedure.Replace(_templates, _settings, Pinned(), ImportedWithSameId());
            Assert.False(outcome.Succeeded);
            Assert.Equal(_pinned, outcome.Settings.FixedTemplateId);
        }
        Assert.Equal("current body", _templates.Load().Library.FindTemplate(_pinned)!.Body);
        Assert.Equal(_pinned, new SettingsStore(_dir.Path).Load().Settings.FixedTemplateId);
    }

    [Fact]
    public void Import_without_a_pin_does_not_touch_the_settings_file()
    {
        File.Delete(_settings.FilePath);
        var outcome = ImportProcedure.Replace(_templates, _settings, new AppSettings(), ImportedWithSameId());
        Assert.True(outcome.Succeeded);
        Assert.False(File.Exists(_settings.FilePath));
    }
}

/// <summary>PC ごとの設定（settings.json。仕様書 §10・§11）。</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Defaults_have_no_hotkeys_and_paste_only_in_pinned_mode()
    {
        var s = new AppSettings();
        Assert.Equal("", s.NormalHotkey);
        Assert.Equal("", s.FixedHotkey);
        Assert.False(s.NormalAutoPaste);
        Assert.True(s.FixedAutoPaste);
        Assert.Null(s.FixedTemplateId);
        Assert.Equal("", s.Language);
    }

    [Fact]
    public void First_run_and_round_trip()
    {
        var store = new SettingsStore(_dir.Path);
        Assert.Equal(LoadKind.Missing, store.Load().Kind);
        var id = Guid.NewGuid();
        var s = new AppSettings { NormalHotkey = "Ctrl+Alt+Space", FixedTemplateId = id, NormalAutoPaste = true, FixedAutoPaste = false, Language = "ja", TrayNoticeShown = true, PickerPreview = true };
        Assert.True(store.Save(s).Succeeded);
        var load = new SettingsStore(_dir.Path).Load();
        Assert.Equal(LoadKind.Loaded, load.Kind);
        Assert.Equal("Ctrl+Alt+Space", load.Settings.NormalHotkey);
        Assert.Equal(id, load.Settings.FixedTemplateId);
        Assert.True(load.Settings.NormalAutoPaste);
        Assert.False(load.Settings.FixedAutoPaste);
        Assert.Equal("ja", load.Settings.Language);
        Assert.True(load.Settings.TrayNoticeShown);
        Assert.True(load.Settings.PickerPreview);
        Assert.Empty(load.Fixes);
        Assert.DoesNotContain("normalShortcut", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Store_package_uses_the_real_local_cache_folder()
    {
        // MSIX 版は %LOCALAPPDATA% への書き込みが振り替えられるため、エクスプローラーで開ける実際の場所を使う。
        var local = Path.Combine("C:", "Users", "u", "AppData", "Local");
        Assert.Equal(Path.Combine(local, "ClipCompose"), SettingsStore.DataDirectory(null, local));
        Assert.Equal(Path.Combine(local, "Packages", "ja1bus.ClipCompose_n5ceq43v5gfza", "LocalCache", "Local", "ClipCompose"),
            SettingsStore.DataDirectory("ja1bus.ClipCompose_n5ceq43v5gfza", local));
    }

    [Fact]
    public void Invalid_values_are_cleared_and_recorded()
    {
        var store = new SettingsStore(_dir.Path);
        File.WriteAllText(store.FilePath, """{"schemaVersion": 1, "normalHotkey": "Shift+A", "fixedHotkey": "nonsense", "language": "fr", "fixedTemplateId": "00000000-0000-0000-0000-000000000000"}""");
        var load = store.Load();
        Assert.Equal(LoadKind.Loaded, load.Kind);
        Assert.Equal("", load.Settings.NormalHotkey);
        Assert.Equal("", load.Settings.FixedHotkey);
        Assert.Equal("", load.Settings.Language);
        Assert.Null(load.Settings.FixedTemplateId);
        Assert.Equal(3, load.Fixes.Count);
    }

    [Fact]
    public void Duplicate_hotkeys_keep_only_the_normal_one()
    {
        var s = new AppSettings { NormalHotkey = "Ctrl+Alt+P", FixedHotkey = "ctrl+alt+p" };
        SettingsNormalizer.Normalize(s);
        Assert.Equal("Ctrl+Alt+P", s.NormalHotkey);
        Assert.Equal("", s.FixedHotkey);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{"schemaVersion": 2, "newSetting": 1}""")]
    [InlineData("""{"schemaVersion": "1"}""")] // レビュー指摘 1
    [InlineData("""{"normalHotkey": "Ctrl+Alt+P"}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void Unreadable_settings_are_kept_and_not_overwritten(string content)
    {
        var store = new SettingsStore(_dir.Path);
        File.WriteAllText(store.FilePath, content);
        var load = store.Load();
        Assert.Equal(LoadKind.Failed, load.Kind);
        Assert.Contains(store.FilePath, load.Problem);
        Assert.False(store.Save(new AppSettings()).Succeeded);
        Assert.Equal(content, File.ReadAllText(store.FilePath));

        // 利用者が選んだ場合だけ、退避して既定値で始める。
        Assert.True(store.ResetMovingAside(new AppSettings()).Succeeded);
        Assert.Single(Directory.GetFiles(_dir.Path, "settings.corrupt-*.json"));
        Assert.Equal(LoadKind.Loaded, store.Load().Kind);
    }
}
