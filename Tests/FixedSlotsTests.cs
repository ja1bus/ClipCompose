using System.Globalization;
using System.Text;
using System.Text.Json;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;
using ClipCompose.Core.Storage;
using ClipCompose.Core.Text;

namespace ClipCompose.Tests;

internal static class TestSettings
{
    /// <summary>スロット 1 に <paramref name="id"/> を入れた設定（1.2.1 までの「固定 1 件」の置き換え）。</summary>
    public static AppSettings PinnedInSlot1(Guid id)
    {
        var s = new AppSettings();
        s.Slot(1).TemplateId = id;
        return s;
    }

    public static AppSettings Slots(Guid? one, Guid? two, Guid? three)
    {
        var s = new AppSettings();
        s.Slot(1).TemplateId = one;
        s.Slot(2).TemplateId = two;
        s.Slot(3).TemplateId = three;
        for (var n = 1; n <= 3; n++) s.Slot(n).Hotkey = $"Ctrl+Alt+Shift+F{13 + n}";
        s.Slot(2).AutoPaste = false;
        return s;
    }
}

/// <summary>settings.json の schemaVersion 2 と、1.2.1（schemaVersion 1）からの移行・補正（仕様書 §11・§17.2。試験 T-01〜T-11）。</summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid T5 = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid S = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly TempDirectory _dir = new();
    private readonly SettingsStore _store;

    public SettingsMigrationTests() => _store = new SettingsStore(_dir.Path);

    public void Dispose() => _dir.Dispose();

    /// <summary>1.2.1 が保存する settings.json（試験 P7）。</summary>
    private static string Settings121(string extra = "") =>
        $$"""{"schemaVersion":1,"normalHotkey":"Ctrl+Alt+Shift+F13","normalAutoPaste":true,"fixedTemplateId":"{{T1}}","fixedHotkey":"Ctrl+Alt+Shift+F14","fixedAutoPaste":false,"language":"ja","trayNoticeShown":true,"pickerPreview":true,"collapsedCategoryIds":["{{S}}"]{{extra}}}""";

    private SettingsLoad LoadText(string json)
    {
        File.WriteAllText(_store.FilePath, json);
        return new SettingsStore(_dir.Path).Load();
    }

    [Fact]
    public void Settings_of_1_2_1_move_the_pin_to_slot_1_without_rewriting_the_file()
    {
        var json = Settings121();
        var load = LoadText(json);
        Assert.Equal(LoadKind.Loaded, load.Kind);
        var s = load.Settings;
        Assert.Equal(3, s.FixedSlots.Count);
        Assert.Equal(T1, s.Slot(1).TemplateId);
        Assert.Equal("Ctrl+Alt+Shift+F14", s.Slot(1).Hotkey);
        Assert.False(s.Slot(1).AutoPaste);
        foreach (var n in new[] { 2, 3 })
        {
            Assert.Null(s.Slot(n).TemplateId);
            Assert.Equal("", s.Slot(n).Hotkey);
            Assert.True(s.Slot(n).AutoPaste);
        }
        Assert.Equal("Ctrl+Alt+Shift+F13", s.NormalHotkey);
        Assert.True(s.NormalAutoPaste);
        Assert.Equal("ja", s.Language);
        Assert.True(s.TrayNoticeShown);
        Assert.True(s.PickerPreview);
        Assert.Equal([S], s.CollapsedCategoryIds);
        Assert.Equal(PickerPlacements.Center, s.PickerPlacement);
        Assert.Null(s.PickerLeft);
        Assert.Null(s.PickerTop);
        Assert.True(s.StartupNotice);
        var fix = Assert.Single(load.Fixes);
        Assert.StartsWith("Migrated from schemaVersion 1", fix);
        Assert.Equal(json, File.ReadAllText(_store.FilePath)); // 読み込んだだけでは書かない
    }

    [Fact]
    public void Migrated_settings_are_saved_as_version_2_and_read_back_the_same()
    {
        var first = LoadText(Settings121()).Settings;
        var store = new SettingsStore(_dir.Path);
        store.Load();
        Assert.True(store.Save(first).Succeeded);

        using (var doc = JsonDocument.Parse(File.ReadAllBytes(_store.FilePath)))
        {
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            var slots = root.GetProperty("fixedSlots");
            Assert.Equal(3, slots.GetArrayLength());
            Assert.Equal(T1, slots[0].GetProperty("templateId").GetGuid());
            Assert.Equal("Ctrl+Alt+Shift+F14", slots[0].GetProperty("hotkey").GetString());
            Assert.False(slots[0].GetProperty("autoPaste").GetBoolean());
            Assert.False(root.TryGetProperty("fixedTemplateId", out _));
            Assert.False(root.TryGetProperty("fixedHotkey", out _));
            Assert.False(root.TryGetProperty("fixedAutoPaste", out _));
        }
        var again = new SettingsStore(_dir.Path).Load();
        Assert.Empty(again.Fixes);
        Assert.Equal(JsonSerializer.Serialize(Comparable(first)), JsonSerializer.Serialize(Comparable(again.Settings)));
    }

    private static object Comparable(AppSettings s) => new
    {
        s.NormalHotkey, s.NormalAutoPaste, s.Language, s.TrayNoticeShown, s.PickerPreview, s.CollapsedCategoryIds,
        s.PickerPlacement, s.PickerLeft, s.PickerTop, s.StartupNotice,
        Slots = s.FixedSlots.Select(x => new { x.TemplateId, x.Hotkey, x.AutoPaste }).ToList(),
    };

    [Fact]
    public void Version_2_round_trip_keeps_every_value_and_clone_is_deep()
    {
        var s = TestSettings.Slots(T1, T3, null);
        s.PickerPlacement = PickerPlacements.Remembered;
        s.PickerLeft = -1200;
        s.PickerTop = 300;
        s.StartupNotice = false;
        Assert.True(_store.Save(s).Succeeded);
        var load = new SettingsStore(_dir.Path).Load();
        Assert.Empty(load.Fixes);
        Assert.Equal(JsonSerializer.Serialize(Comparable(s)), JsonSerializer.Serialize(Comparable(load.Settings)));

        var copy = s.Clone();
        copy.Slot(1).TemplateId = T5;
        copy.FixedSlots.RemoveAt(2);
        Assert.Equal(3, s.FixedSlots.Count);
        Assert.Equal(T1, s.Slot(1).TemplateId);
    }

    [Fact]
    public void Version_1_without_pin_items_migrates_to_defaults()
    {
        var load = LoadText("""{"schemaVersion":1,"normalHotkey":"Ctrl+Alt+Shift+F13"}""");
        Assert.Equal(LoadKind.Loaded, load.Kind);
        Assert.All(load.Settings.FixedSlots, x => Assert.Null(x.TemplateId));
        Assert.Contains(load.Fixes, f => f.StartsWith("Migrated"));
    }

    [Fact]
    public void Version_1_ignores_fixed_slots_and_uses_the_old_items()
    {
        var load = LoadText(Settings121($$""","fixedSlots":[{"templateId":"{{T3}}","hotkey":"Ctrl+Alt+Shift+F15","autoPaste":false},null,null]"""));
        Assert.Equal(LoadKind.Loaded, load.Kind);
        Assert.Equal(T1, load.Settings.Slot(1).TemplateId);
        Assert.Equal("Ctrl+Alt+Shift+F14", load.Settings.Slot(1).Hotkey);
        Assert.Null(load.Settings.Slot(2).TemplateId);
        Assert.Null(load.Settings.Slot(3).TemplateId);
        Assert.Null(FixedSlotRules.SlotOf(load.Settings, T3));
    }

    [Fact]
    public void Version_1_empty_guid_is_cleared_without_a_record()
    {
        var load = LoadText("""{"schemaVersion":1,"fixedTemplateId":"00000000-0000-0000-0000-000000000000"}""");
        Assert.Null(load.Settings.Slot(1).TemplateId);
        Assert.Single(load.Fixes);
    }

    /// <summary>レビュー R3: 1.2.1 が知らない項目（版 2 の項目）の型が違っても、版 1 として移行できる。</summary>
    [Theory]
    [InlineData(""","fixedSlots":true""")]
    [InlineData(""","fixedSlots":[{"templateId":"not-a-guid"}]""")]
    [InlineData(""","startupNotice":"no","pickerPlacement":5,"pickerLeft":"x","pickerTop":[]""")]
    public void Version_1_ignores_version_2_items_of_any_type(string extra)
    {
        var load = LoadText(Settings121(extra));
        Assert.Equal(LoadKind.Loaded, load.Kind);
        var s = load.Settings;
        Assert.Equal(T1, s.Slot(1).TemplateId);
        Assert.Equal("Ctrl+Alt+Shift+F14", s.Slot(1).Hotkey);
        Assert.False(s.Slot(1).AutoPaste);
        Assert.Null(s.Slot(2).TemplateId);
        Assert.Null(s.Slot(3).TemplateId);
        Assert.Equal("Ctrl+Alt+Shift+F13", s.NormalHotkey);
        Assert.True(s.NormalAutoPaste);
        Assert.Equal("ja", s.Language);
        Assert.True(s.TrayNoticeShown);
        Assert.True(s.PickerPreview);
        Assert.Equal([S], s.CollapsedCategoryIds);
        Assert.Equal(PickerPlacements.Center, s.PickerPlacement);
        Assert.Null(s.PickerLeft);
        Assert.True(s.StartupNotice);
        Assert.StartsWith("Migrated from schemaVersion 1", Assert.Single(load.Fixes));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"fixedTemplateId":"not-a-guid"}""")]
    [InlineData("""{"schemaVersion":1,"fixedAutoPaste":"yes"}""")]
    [InlineData("""{"schemaVersion":1,"normalHotkey":5}""")]
    [InlineData("""{"schemaVersion":1,"collapsedCategoryIds":"x"}""")]
    public void Version_1_with_wrong_types_is_not_read(string json)
    {
        File.WriteAllText(_store.FilePath, json);
        var load = _store.Load();
        Assert.Equal(LoadKind.Failed, load.Kind);
        Assert.Contains(_store.FilePath, load.Problem);
        Assert.False(_store.Save(new AppSettings()).Succeeded);
        Assert.Equal(json, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void Version_1_invalid_or_duplicate_pinned_shortcut_is_cleared()
    {
        using (new UiCultureScope("ja"))
        {
            var load = LoadText("""{"schemaVersion":1,"fixedHotkey":"nonsense"}""");
            Assert.Equal("", load.Settings.Slot(1).Hotkey);
            Assert.Contains(load.Fixes, f => f.StartsWith("FixedSlots[0].Hotkey"));
            Assert.All(load.Fixes, f => Assert.True(f.All(c => c < 0x3000), f));
        }
        var dup = LoadText("""{"schemaVersion":1,"normalHotkey":"Ctrl+Alt+Shift+F13","fixedHotkey":"ctrl+alt+shift+f13"}""");
        Assert.Equal("Ctrl+Alt+Shift+F13", dup.Settings.NormalHotkey);
        Assert.Equal("", dup.Settings.Slot(1).Hotkey);
        Assert.Contains(dup.Fixes, f => f.Contains("same as another shortcut"));
    }

    private static string Slot(Guid id, string key = "", bool auto = true) =>
        $$"""{"templateId":"{{id}}","hotkey":"{{key}}","autoPaste":{{(auto ? "true" : "false")}}}""";

    [Theory]
    [InlineData("null", true)]
    [InlineData("[]", true)]
    [InlineData("TWO", true)]
    [InlineData("FOUR", true)]
    [InlineData("NULLFIRST", true)]
    [InlineData(null, false)]
    public void Slot_array_shape_is_corrected(string? slots, bool recorded)
    {
        var value = slots switch
        {
            "TWO" => $"[{Slot(T1)},{Slot(T3)}]",
            "FOUR" => $"[{Slot(T1)},{Slot(T3)},null,{Slot(T5, "Ctrl+Alt+Shift+F16")}]",
            "NULLFIRST" => $"[null,{Slot(T3)},null]",
            _ => slots,
        };
        var json = value is null ? """{"schemaVersion":2}""" : $$"""{"schemaVersion":2,"fixedSlots":{{value}}}""";
        var load = LoadText(json);
        Assert.Equal(LoadKind.Loaded, load.Kind);
        var s = load.Settings;
        Assert.Equal(3, s.FixedSlots.Count);
        Assert.All(s.FixedSlots, x => Assert.NotNull(x));
        switch (slots)
        {
            case "TWO":
                Assert.Equal(T1, s.Slot(1).TemplateId);
                Assert.Equal(T3, s.Slot(2).TemplateId);
                Assert.Null(s.Slot(3).TemplateId);
                break;
            case "FOUR":
                Assert.Null(FixedSlotRules.SlotOf(s, T5));
                Assert.DoesNotContain(s.FixedSlots, x => x.Hotkey == "Ctrl+Alt+Shift+F16");
                break;
            case "NULLFIRST":
                Assert.Equal(T3, s.Slot(2).TemplateId);
                Assert.Null(s.Slot(1).TemplateId);
                Assert.Equal("", s.Slot(1).Hotkey);
                Assert.True(s.Slot(1).AutoPaste);
                break;
        }
        Assert.Equal(recorded, load.Fixes.Count > 0);
    }

    [Fact]
    public void The_same_template_stays_only_in_the_first_slot()
    {
        var a = LoadText($$"""{"schemaVersion":2,"fixedSlots":[{{Slot(T1)}},null,{{Slot(T1)}}]}""");
        Assert.Equal(T1, a.Settings.Slot(1).TemplateId);
        Assert.Null(a.Settings.Slot(3).TemplateId);
        Assert.Contains(a.Fixes, f => f.StartsWith("FixedSlots[2].TemplateId"));

        var b = LoadText($$"""{"schemaVersion":2,"fixedSlots":[{"templateId":null},{{Slot(T3)}},{{Slot(T3, "Ctrl+Alt+Shift+F16", false)}}]}""");
        Assert.Equal(T3, b.Settings.Slot(2).TemplateId);
        Assert.Null(b.Settings.Slot(3).TemplateId);
        Assert.Equal("Ctrl+Alt+Shift+F16", b.Settings.Slot(3).Hotkey);
        Assert.False(b.Settings.Slot(3).AutoPaste);
    }

    [Theory]
    [InlineData("P", "ctrl+alt+shift+f13", "P", "P", "P", "", "", "", 3)]
    [InlineData("", "Ctrl+Alt+Shift+F14", "", "ctrl+alt+shift+F14", "", "Ctrl+Alt+Shift+F14", "", "", 1)]
    [InlineData("", "", "Ctrl+Alt+Shift+F15", "Ctrl+Alt+Shift+F15", "", "", "Ctrl+Alt+Shift+F15", "", 1)]
    [InlineData("P", "Ctrl+Alt+Shift+F14", "Ctrl+Alt+Shift+F15", "Ctrl+Alt+Shift+F16", "P", "Ctrl+Alt+Shift+F14", "Ctrl+Alt+Shift+F15", "Ctrl+Alt+Shift+F16", 0)]
    public void Four_shortcuts_keep_the_first_in_normal_then_slot_order(string normal, string one, string two, string three,
        string normalAfter, string oneAfter, string twoAfter, string threeAfter, int records)
    {
        static string K(string v) => v == "P" ? "Ctrl+Alt+Shift+F13" : v;
        var s = new AppSettings { NormalHotkey = K(normal) };
        s.Slot(1).Hotkey = K(one);
        s.Slot(2).Hotkey = K(two);
        s.Slot(3).Hotkey = K(three);
        var fixes = SettingsNormalizer.Normalize(s);
        Assert.Equal(K(normalAfter), s.NormalHotkey);
        Assert.Equal(K(oneAfter), s.Slot(1).Hotkey);
        Assert.Equal(K(twoAfter), s.Slot(2).Hotkey);
        Assert.Equal(K(threeAfter), s.Slot(3).Hotkey);
        Assert.Equal(records, fixes.Count);
    }

    [Theory]
    [InlineData("\"center\"", "center", false)]
    [InlineData("\"cursor\"", "cursor", false)]
    [InlineData("\"remembered\"", "remembered", false)]
    [InlineData("\" Cursor \"", "cursor", false)]
    [InlineData("\"top\"", "center", true)]
    [InlineData("\"\"", "center", false)]
    [InlineData("null", "center", false)]
    public void Picker_placement_is_corrected(string value, string expected, bool recorded)
    {
        var load = LoadText($$"""{"schemaVersion":2,"pickerPlacement":{{value}}}""");
        Assert.Equal(expected, load.Settings.PickerPlacement);
        Assert.Equal(recorded, load.Fixes.Any(f => f.StartsWith("PickerPlacement")));
    }

    [Fact]
    public void Half_a_position_is_dropped_and_startup_notice_defaults_to_on()
    {
        var load = LoadText("""{"schemaVersion":2,"pickerLeft":100}""");
        Assert.Null(load.Settings.PickerLeft);
        Assert.Null(load.Settings.PickerTop);
        Assert.Empty(load.Fixes);
        Assert.True(load.Settings.StartupNotice);
    }
}

/// <summary>固定スロットの規則とスロット単位の解除（仕様書 §6・§17.3。試験 T-12〜T-19）。</summary>
public sealed class FixedSlotRulesTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly Guid T1 = Guid.NewGuid();
    private static readonly Guid T3 = Guid.NewGuid();
    private static readonly Guid T5 = Guid.NewGuid();

    private static void AssertKeysUnchanged(AppSettings expected, AppSettings actual)
    {
        for (var n = 1; n <= 3; n++)
        {
            Assert.Equal(expected.Slot(n).Hotkey, actual.Slot(n).Hotkey);
            Assert.Equal(expected.Slot(n).AutoPaste, actual.Slot(n).AutoPaste);
        }
    }

    [Fact]
    public void Pinning_moves_unpins_and_never_changes_the_original()
    {
        var s = TestSettings.Slots(T1, T3, null);

        var a = FixedSlotRules.WithPin(s, 3, T5);
        Assert.Equal([T1, T3, T5], a.FixedSlots.Select(x => x.TemplateId));

        var b = FixedSlotRules.WithPin(s, 2, T1);
        Assert.Equal([null, T1, null], b.FixedSlots.Select(x => x.TemplateId));

        var c = FixedSlotRules.WithPin(s, 1, null);
        Assert.Null(c.Slot(1).TemplateId);

        Assert.Equal(2, FixedSlotRules.SlotOf(s, T3));
        Assert.Null(FixedSlotRules.SlotOf(s, T5));

        var (e, changedE) = FixedSlotRules.WithoutTemplates(s, new HashSet<Guid> { T1, Guid.NewGuid() });
        Assert.True(changedE);
        Assert.Equal([null, T3, null], e.FixedSlots.Select(x => x.TemplateId));
        var (_, changedF) = FixedSlotRules.WithoutTemplates(s, new HashSet<Guid> { Guid.NewGuid() });
        Assert.False(changedF);

        foreach (var x in new[] { a, b, c, e }) AssertKeysUnchanged(s, x);
        Assert.Equal([T1, T3, null], s.FixedSlots.Select(x => x.TemplateId));
    }

    [Fact]
    public void Resolve_by_slot_reports_each_state_with_the_slot_number()
    {
        var c = TemplateLibrary.Empty.AddCategory("c");
        var t = c.Library!.AddTemplate(c.Id, "t1", "body");
        var library = t.Library!;
        var s = TestSettings.Slots(t.Id, null, Guid.NewGuid());
        var all = FixedTemplate.ResolveAll(s, library);
        Assert.Equal([1, 2, 3], all.Select(x => x.Slot));
        Assert.Equal([FixedTemplateState.Found, FixedTemplateState.NotSet, FixedTemplateState.Missing], all.Select(x => x.State));
        library = library.UpdateTemplate(t.Id, c.Id, "t1", "new body").Library!;
        Assert.Equal("new body", FixedTemplate.Resolve(s, 1, library).Template!.Body);
    }

    /// <summary>試験の基本データ（3 カテゴリ 5 件）。</summary>
    private sealed class Data
    {
        public TemplateLibrary Library = TemplateLibrary.Empty;
        public Guid M, S, R, T1, T2, T3, T4, T5;

        public Data()
        {
            var m = Library.AddCategory("メール"); Library = m.Library!; M = m.Id;
            var s = Library.AddCategory("要約"); Library = s.Library!; S = s.Id;
            var r = Library.AddCategory("翻訳"); Library = r.Library!; R = r.Id;
            T1 = Add(M, "お礼", "ありがとうございました。");
            T2 = Add(M, "返信", "次の文に返信してください。\n{{clipboard}}");
            T3 = Add(S, "短く", "短くしてください。");
            T4 = Add(S, "箇条書き", "{{clipboard}}\nを箇条書きに");
            T5 = Add(R, "英訳", "英訳してください。");
        }

        private Guid Add(Guid category, string name, string body)
        {
            var e = Library.AddTemplate(category, name, body);
            Library = e.Library!;
            return e.Id;
        }
    }

    private (Data Data, TemplateStore Templates, SettingsStore Settings) Setup(AppSettings settings)
    {
        var data = new Data();
        var templates = new TemplateStore(_dir.Path);
        var store = new SettingsStore(_dir.Path);
        Assert.True(templates.Save(data.Library).Succeeded);
        Assert.True(store.Save(settings).Succeeded);
        return (data, templates, store);
    }

    private static TemplateLibrary FileWith(Guid id)
    {
        var c = TemplateLibrary.Empty.AddCategory("Imported");
        return c.Library!.AddTemplate(c.Id, "same id", "imported", id).Library!;
    }

    [Fact]
    public void Replacing_everything_unpins_every_slot_first_and_keeps_the_rest()
    {
        var probe = new Data();
        var settings = TestSettings.Slots(null, null, null);
        var (data, templates, store) = Setup(settings);
        // Data は毎回新しい ID なので、保存した方の ID でスロットを入れ直す。
        settings = TestSettings.Slots(data.T1, data.T3, null);
        settings.CollapsedCategoryIds = [data.S];
        Assert.True(store.Save(settings).Succeeded);
        var before = File.ReadAllBytes(templates.FilePath);

        var outcome = ImportProcedure.Replace(templates, store, settings, FileWith(data.T1));
        Assert.True(outcome.Succeeded);
        var saved = new SettingsStore(_dir.Path).Load().Settings;
        Assert.All(saved.FixedSlots, x => Assert.Null(x.TemplateId));
        AssertKeysUnchanged(settings, saved);
        Assert.Equal([data.S], saved.CollapsedCategoryIds);
        var copy = Assert.Single(Directory.GetFiles(_dir.Path, "templates.before-import-*.json"));
        Assert.Equal(before, File.ReadAllBytes(copy));
        Assert.NotEqual(probe.T1, data.T1);
    }

    [Fact]
    public void Replace_and_append_without_pins_do_not_write_settings()
    {
        var (data, templates, store) = Setup(new AppSettings());
        File.Delete(store.FilePath);
        Assert.True(ImportProcedure.Replace(templates, store, new AppSettings(), data.Library).Succeeded);
        Assert.True(ImportProcedure.SaveRemoving(templates, store, new AppSettings(), data.Library, new HashSet<Guid> { data.T1 }, "before-delete").Succeeded);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Failure_to_save_the_unpin_changes_nothing()
    {
        var (data, templates, store) = Setup(new AppSettings());
        var settings = TestSettings.Slots(data.T1, data.T3, null);
        Assert.True(store.Save(settings).Succeeded);
        var templatesBefore = File.ReadAllBytes(templates.FilePath);
        var settingsBefore = File.ReadAllBytes(store.FilePath);
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = ImportProcedure.Replace(templates, store, settings, FileWith(data.T1));
            Assert.False(outcome.Succeeded);
            Assert.Equal(data.T1, outcome.Settings.Slot(1).TemplateId);
        }
        Assert.Equal(templatesBefore, File.ReadAllBytes(templates.FilePath));
        Assert.Equal(settingsBefore, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void Failure_to_save_the_templates_puts_every_slot_back()
    {
        var (data, templates, store) = Setup(new AppSettings());
        var settings = TestSettings.Slots(data.T1, data.T3, null);
        Assert.True(store.Save(settings).Succeeded);
        var templatesBefore = File.ReadAllBytes(templates.FilePath);
        using (new FileStream(templates.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var outcome = ImportProcedure.Replace(templates, store, settings, FileWith(data.T1));
            Assert.False(outcome.Succeeded);
        }
        var saved = new SettingsStore(_dir.Path).Load().Settings;
        Assert.Equal([data.T1, data.T3, null], saved.FixedSlots.Select(x => x.TemplateId));
        Assert.Equal(templatesBefore, File.ReadAllBytes(templates.FilePath));
    }

    [Fact]
    public void Replacing_a_category_contents_unpins_only_the_slot_whose_template_disappears()
    {
        var (data, templates, store) = Setup(new AppSettings());
        var settings = TestSettings.Slots(data.T1, data.T2, data.T5);
        Assert.True(store.Save(settings).Succeeded);
        var file = TemplateLibrary.Empty.AddCategory("メール");
        var fileLib = file.Library!.AddTemplate(file.Id, "返信", "別の本文").Library!;
        fileLib = fileLib.AddTemplate(file.Id, "新規", "new").Library!;

        var plan = LibraryMerge.Plan(data.Library, fileLib, new Dictionary<Guid, MergeMode> { [file.Id] = MergeMode.ReplaceContents });
        var merged = LibraryMerge.Apply(data.Library, fileLib, plan);
        var outcome = ImportProcedure.SaveRemoving(templates, store, settings, merged.Library, merged.RemovedTemplateIds, "before-import");
        Assert.True(outcome.Succeeded);
        Assert.Equal([null, data.T2, data.T5], outcome.Settings.FixedSlots.Select(x => x.TemplateId));
        Assert.Equal([null, data.T2, data.T5], new SettingsStore(_dir.Path).Load().Settings.FixedSlots.Select(x => x.TemplateId));
        AssertKeysUnchanged(settings, outcome.Settings);
        Assert.Single(Directory.GetFiles(_dir.Path, "templates.before-import-*.json"));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("unpin-fails")]
    [InlineData("save-fails")]
    public void Deleting_a_category_unpins_only_its_slot(string mode)
    {
        var (data, templates, store) = Setup(new AppSettings());
        var settings = TestSettings.Slots(data.T5, data.T3, null);
        Assert.True(store.Save(settings).Succeeded);
        var edit = data.Library.DeleteCategoryWithTemplates(data.S);
        var removed = data.Library.TemplatesIn(data.S).Select(t => t.Id).ToHashSet();
        var templatesBefore = File.ReadAllBytes(templates.FilePath);
        ImportOutcome outcome;
        switch (mode)
        {
            case "unpin-fails":
                using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    outcome = ImportProcedure.SaveRemoving(templates, store, settings, edit.Library!, removed, "before-delete");
                break;
            case "save-fails":
                using (new FileStream(templates.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    outcome = ImportProcedure.SaveRemoving(templates, store, settings, edit.Library!, removed, "before-delete");
                break;
            default:
                outcome = ImportProcedure.SaveRemoving(templates, store, settings, edit.Library!, removed, "before-delete");
                break;
        }
        var saved = new SettingsStore(_dir.Path).Load().Settings;
        if (mode == "ok")
        {
            Assert.True(outcome.Succeeded);
            Assert.Equal([data.T5, null, null], saved.FixedSlots.Select(x => x.TemplateId));
            Assert.Single(Directory.GetFiles(_dir.Path, "templates.before-delete-*.json"));
        }
        else
        {
            Assert.False(outcome.Succeeded);
            Assert.Equal(templatesBefore, File.ReadAllBytes(templates.FilePath));
            Assert.Equal([data.T5, data.T3, null], saved.FixedSlots.Select(x => x.TemplateId));
        }
    }
}

/// <summary>退避ファイルの一覧・保持上限・戻す（仕様書 §11・§17.9。試験 T-20〜T-26）。</summary>
public sealed class SideCopyTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly List<string> _log = [];
    private DateTime _now = new(2026, 11, 18, 9, 0, 0);
    private readonly TemplateStore _store;
    private readonly SettingsStore _settings;

    public SideCopyTests()
    {
        _store = new TemplateStore(_dir.Path, () => _now, (message, _) => _log.Add(message));
        _settings = new SettingsStore(_dir.Path);
        Assert.True(_store.Save(Library("current")).Succeeded);
    }

    public void Dispose() => _dir.Dispose();

    private static TemplateLibrary Library(string name, int templates = 1)
    {
        var c = TemplateLibrary.Empty.AddCategory(name);
        var library = c.Library!;
        for (var i = 0; i < templates; i++) library = library.AddTemplate(c.Id, $"t{i}", "secret body").Library!;
        return library;
    }

    private string Put(string name, TemplateLibrary? library = null)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllBytes(path, TemplateFileFormat.Serialize(library ?? Library(name)));
        return path;
    }

    private static readonly string[] NotMatching =
    [
        "templates.corrupt-20261104-090000.json", "templates.json.bak", "templates.before-import-20261104-090000.json.tmp",
        "templates.before-export-20261104-090000.json", "Templates.before-import-20261104-090000.json",
        "templates.before-import-20261104-090000.JSON", "templates.before-import-20261332-090000.json",
        "templates.before-import-20261104-250000.json", "templates.before-import-20261104-090000-1.json",
        "templates.before-import-20261104-090000-x.json", "templates.before-import-2026110-090000.json",
        "templates.before-import-20261104-090000 (2).json", "templates.before-import-20261104-090000-02.json",
    ];

    [Fact]
    public void Only_exactly_named_copies_are_listed_newest_first()
    {
        Put("templates.before-import-20261101-090000.json");
        Put("templates.before-delete-20261102-090000.json");
        Put("templates.before-restore-20261103-090000.json");
        Put("templates.before-import-20261103-090000-2.json");
        foreach (var name in NotMatching)
        {
            // 大小文字だけ違う名前は Windows では同じファイルになるため、別の名前の物だけ置く。
            if (File.Exists(Path.Combine(_dir.Path, name))) continue;
            Put(name);
        }
        var list = _store.ListSideCopies();
        Assert.Equal(
            ["templates.before-import-20261103-090000-2.json", "templates.before-restore-20261103-090000.json",
             "templates.before-delete-20261102-090000.json", "templates.before-import-20261101-090000.json"],
            list.Select(c => Path.GetFileName(c.Path)));
        Assert.Equal([SideCopyKind.Import, SideCopyKind.Restore, SideCopyKind.Delete, SideCopyKind.Import], list.Select(c => c.Kind));
        Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0), list[0].Stamp);
        Assert.Equal(2, list[0].Sequence);
        Assert.Equal(1, list[1].Sequence);
    }

    private void PutMany(int count, Func<int, string>? kind = null)
    {
        for (var i = 0; i < count; i++)
        {
            var path = Put($"templates.before-{(kind?.Invoke(i) ?? "import")}-2025{1 + i / 28:00}{1 + i % 28:00}-000000.json");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(i == 0 ? 1 : 0)); // 最古の名前のファイルを一番新しい更新日時にする
        }
    }

    private int Copies() => _store.ListSideCopies().Count;

    [Theory]
    [InlineData(19, 20, 0)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    public void Old_copies_beyond_twenty_are_deleted_after_saving(int existing, int after, int deleted)
    {
        PutMany(existing);
        var names = _store.ListSideCopies().Select(c => c.Path).ToList();
        Assert.True(_store.SaveAfterCopy(Library("new"), "before-import").Succeeded);
        Assert.Equal(after, Copies());
        // 名前の日時が最も古いものから消える（更新日時ではない）。
        foreach (var gone in names.TakeLast(deleted)) Assert.False(File.Exists(gone));
        Assert.All(names.SkipLast(deleted), p => Assert.True(File.Exists(p)));
    }

    /// <summary>レビュー R2: 既存の 20 件より名前の日時が古い写し（時計を戻した・別の PC のファイル）でも、今回の写しは消さない。</summary>
    [Fact]
    public void The_copy_just_made_is_kept_even_if_older_copies_are_newer_by_name()
    {
        for (var i = 1; i <= 20; i++) Put($"templates.before-import-20261118-1300{i:00}.json");
        _now = new DateTime(2026, 11, 18, 13, 0, 0);
        var before = File.ReadAllBytes(_store.FilePath);
        Assert.True(_store.SaveAfterCopy(Library("new"), "before-restore").Succeeded);

        var list = _store.ListSideCopies();
        Assert.Equal(20, list.Count);
        var restore = Assert.Single(list, c => c.Kind == SideCopyKind.Restore);
        Assert.Equal("templates.before-restore-20261118-130000.json", Path.GetFileName(restore.Path));
        Assert.Equal(before, File.ReadAllBytes(restore.Path));
        // 残りは古い（名前の日時の）ものから消える。
        Assert.False(File.Exists(Path.Combine(_dir.Path, "templates.before-import-20261118-130001.json")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "templates.before-import-20261118-130002.json")));
        // 一覧から元に戻せる。
        Assert.Equal("current", _store.ReadSideCopy(restore.Path).Library!.Categories[0].Name);
    }

    [Fact]
    public void The_limit_counts_all_kinds_together()
    {
        // 最古（i = 0）は delete。
        PutMany(20, i => i == 0 ? "delete" : i < 8 ? "import" : i < 14 ? "delete" : "restore");
        var oldest = _store.ListSideCopies()[^1];
        Assert.Equal(SideCopyKind.Delete, oldest.Kind);
        Assert.True(_store.SaveAfterCopy(Library("new"), "before-restore").Succeeded);
        Assert.Equal(20, Copies());
        Assert.False(File.Exists(oldest.Path));
    }

    [Fact]
    public void Other_files_are_never_deleted()
    {
        foreach (var name in NotMatching)
            if (!File.Exists(Path.Combine(_dir.Path, name))) Put(name);
        var others = NotMatching.Where(n => File.Exists(Path.Combine(_dir.Path, n)))
            .ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(_dir.Path, n)));
        PutMany(20);
        Assert.True(_store.SaveAfterCopy(Library("new"), "before-import").Succeeded);
        Assert.Equal(20, Copies());
        foreach (var (name, bytes) in others)
        {
            var path = Path.Combine(_dir.Path, name);
            Assert.True(File.Exists(path), name);
            // templates.json.bak は通常の保存で 1 世代のバックアップとして書き直される（消えないことだけを見る）。
            if (name != "templates.json.bak") Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Nothing_is_deleted_when_saving_fails()
    {
        PutMany(20);
        var names = _store.ListSideCopies().Select(c => c.Path).ToList();
        var before = File.ReadAllBytes(_store.FilePath);
        using (new FileStream(_store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(_store.SaveAfterCopy(Library("new"), "before-import").Succeeded);
        Assert.All(names, p => Assert.True(File.Exists(p)));
        Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
    }

    [Fact]
    public void A_copy_that_cannot_be_deleted_is_logged_and_saving_still_succeeds()
    {
        PutMany(20);
        var oldest = _store.ListSideCopies()[^1].Path;
        using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(_store.SaveAfterCopy(Library("new"), "before-import").Succeeded);
            Assert.Equal("new", _store.Load().Library.Categories[0].Name);
            Assert.True(File.Exists(oldest));
            Assert.Equal(21, Copies());
            Assert.NotEmpty(_log);
            Assert.All(_log, m => Assert.DoesNotContain("secret body", m));
        }
        _now = _now.AddSeconds(1);
        Assert.True(_store.SaveAfterCopy(Library("newer"), "before-import").Succeeded);
        Assert.Equal(20, Copies());
    }

    [Fact]
    public void Copies_are_validated_on_demand()
    {
        var good = Put("templates.before-import-20261101-090000.json", Library("good", 3));
        var broken = Path.Combine(_dir.Path, "templates.before-delete-20261102-090000.json");
        File.WriteAllText(broken, "{ broken");
        var newer = Path.Combine(_dir.Path, "templates.before-delete-20261103-090000.json");
        File.WriteAllText(newer, """{"schemaVersion":99,"categories":[],"templates":[]}""");

        var ok = _store.ReadSideCopy(good);
        Assert.True(ok.Succeeded);
        Assert.Equal(3, ok.Library!.Templates.Count);
        foreach (var bad in new[] { broken, newer })
        {
            var r = _store.ReadSideCopy(bad);
            Assert.False(r.Succeeded);
            Assert.Contains(bad, r.Problem);
        }
    }

    [Fact]
    public void Restoring_the_oldest_copy_replaces_everything_and_keeps_the_current_data()
    {
        PutMany(19);
        var oldest = Put("templates.before-import-20240101-000000.json", Library("oldest", 3));
        Assert.Equal(20, Copies());
        var current = File.ReadAllBytes(_store.FilePath);
        var parsed = _store.ReadSideCopy(oldest);
        var outcome = ImportProcedure.Replace(_store, _settings, new AppSettings(), parsed.Library!, "before-restore");
        Assert.True(outcome.Succeeded);
        Assert.Equal("oldest", _store.Load().Library.Categories[0].Name);
        var restoreCopy = Assert.Single(Directory.GetFiles(_dir.Path, "templates.before-restore-*.json"));
        Assert.Equal(current, File.ReadAllBytes(restoreCopy));
        Assert.False(File.Exists(oldest));
        Assert.Equal(20, Copies());
    }

    [Theory]
    [InlineData("ja-JP-japanese")]
    [InlineData("th-TH")]
    public void Copy_names_use_the_gregorian_year_in_any_region(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = cultureName == "ja-JP-japanese" ? new CultureInfo("ja-JP") : new CultureInfo(cultureName);
            if (cultureName == "ja-JP-japanese") culture.DateTimeFormat.Calendar = new JapaneseCalendar();
            CultureInfo.CurrentCulture = culture;
            Assert.True(_store.SaveAfterCopy(Library("new"), "before-import").Succeeded);
            var copy = Assert.Single(_store.ListSideCopies());
            Assert.StartsWith("templates.before-import-2026", Path.GetFileName(copy.Path));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

/// <summary>選択画面を開く位置の計算（仕様書 §5・§17.7。試験 T-29）。</summary>
public sealed class PickerPlacementTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Center_is_the_middle_of_the_work_area()
    {
        Assert.Equal((802, 320), PickerPlacements.Place(PickerPlacements.Center, Work, 100, 100, 1.0, null, null, 316, 400));
    }

    [Theory]
    [InlineData(100, 100, 1.0, 116, 116)]
    [InlineData(100, 100, 1.5, 124, 124)]
    [InlineData(1587, 623, 1.0, 1603, 639)]
    [InlineData(1588, 624, 1.0, 1604, 640)]
    [InlineData(1589, 625, 1.0, 1604, 640)]
    [InlineData(1900, 1000, 1.0, 1604, 640)]
    public void Near_the_cursor_fits_in_the_work_area(int cx, int cy, double scale, int x, int y)
    {
        Assert.Equal((x, y), PickerPlacements.Place(PickerPlacements.Cursor, Work, cx, cy, scale, null, null, 316, 400));
    }

    [Fact]
    public void A_window_taller_than_the_work_area_starts_at_its_top()
    {
        Assert.Equal(0, PickerPlacements.Place(PickerPlacements.Cursor, Work, 100, 100, 1.0, null, null, 316, 1100).Y);
        var lower = new PixelRect(0, 40, 1920, 1080);
        Assert.True(PickerPlacements.Place(PickerPlacements.Cursor, lower, 100, 0, 1.0, null, null, 316, 400).Y >= 40);
    }

    [Fact]
    public void Monitors_left_of_the_primary_use_negative_coordinates()
    {
        var left = new PixelRect(-1920, 0, 0, 1040);
        Assert.Equal((-1118, 320), PickerPlacements.Place(PickerPlacements.Center, left, -100, 100, 1.0, null, null, 316, 400));
        Assert.Equal((-316, 116), PickerPlacements.Place(PickerPlacements.Cursor, left, -100, 100, 1.0, null, null, 316, 400));
    }

    [Fact]
    public void Remembered_position_is_kept_inside_or_falls_back_to_center()
    {
        Assert.Equal((500, 300), PickerPlacements.Place(PickerPlacements.Remembered, Work, 100, 100, 1.0, (500, 300), Work, 316, 400));
        Assert.Equal((1604, 640), PickerPlacements.Place(PickerPlacements.Remembered, Work, 100, 100, 1.0, (1800, 900), Work, 316, 400));
        Assert.Equal((802, 320), PickerPlacements.Place(PickerPlacements.Remembered, Work, 100, 100, 1.0, (-30000, -30000), null, 316, 400));
        Assert.Equal((802, 320), PickerPlacements.Place(PickerPlacements.Remembered, Work, 100, 100, 1.0, null, null, 316, 400));
    }
}

/// <summary>トレイのツールチップ（127 文字。仕様書 §3・§17.5。試験 T-30）。</summary>
public sealed class TrayTooltipTests
{
    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Without_pins_it_says_none(string language)
    {
        using var _ = new UiCultureScope(language);
        Assert.Contains(Strings.TrayTooltipNoFixed, TrayTooltip.Build("Ctrl+Alt+Space", []));
    }

    [Fact]
    public void Only_slots_with_templates_are_listed()
    {
        using var _ = new UiCultureScope("ja");
        var tip = TrayTooltip.Build(null, [new(1, "お礼", "Ctrl+Alt+1"), new(3, "英訳", null)]);
        Assert.Contains("固定 1: お礼（Ctrl+Alt+1）", tip);
        Assert.Contains("固定 3: 英訳", tip);
        Assert.DoesNotContain("固定 2", tip);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Long_tooltips_switch_to_the_count(string language)
    {
        using var _ = new UiCultureScope(language);
        string Build(int nameLength) => TrayTooltip.Build("Ctrl+Alt+Space",
            [new(1, new string('あ', nameLength), "F13"), new(2, "x", "F14")]);
        // 全行の形がちょうど 126・127・128 文字になる名前の長さを探す。
        var baseLength = Build(1).Length - 1;
        Assert.Equal(126, Build(126 - baseLength).Length);
        Assert.Equal(127, Build(127 - baseLength).Length);
        var compact = Build(128 - baseLength);
        Assert.True(compact.Length < 127);
        Assert.Contains(string.Format(Strings.TrayTooltipFixedCountFormat, 2), compact);
        Assert.DoesNotContain("あ", compact);
    }

    [Fact]
    public void Even_the_short_form_is_cut_at_127()
    {
        var tip = TrayTooltip.Build(new string('K', 200), [new(1, "x", null)]);
        Assert.Equal(127, tip.Length);
    }

    [Fact]
    public void Cutting_never_splits_a_surrogate_pair()
    {
        var key = new string('K', 124 - "ClipCompose\n".Length) + "😀😀😀";
        var tip = TrayTooltip.Build(key, [new(1, "x", null)]);
        Assert.True(tip.Length <= 127);
        Assert.False(char.IsHighSurrogate(tip[^1]));
    }
}

/// <summary>端での並べ替えは同じライブラリ（保存しない。整備 2。試験 T-28）。</summary>
public sealed class NoOpMoveTests
{
    [Fact]
    public void Moves_at_the_ends_return_the_same_instance()
    {
        var a = TemplateLibrary.Empty.AddCategory("A");
        var b = a.Library!.AddCategory("B");
        var c = b.Library!.AddCategory("C");
        var t1 = c.Library!.AddTemplate(b.Id, "1", "x");
        var t2 = t1.Library!.AddTemplate(b.Id, "2", "x");
        var library = t2.Library!;

        Assert.Same(library, library.MoveCategory(a.Id, -1).Library);
        Assert.Same(library, library.MoveCategory(c.Id, 1).Library);
        Assert.Same(library, library.MoveTemplate(t1.Id, -1).Library);
        Assert.Same(library, library.MoveTemplate(t2.Id, 1).Library);
        var moved = library.MoveCategory(b.Id, -1).Library!;
        Assert.NotSame(library, moved);
        Assert.Equal(["B", "A", "C"], moved.Categories.Select(x => x.Name));
    }
}

/// <summary>templates.json・エクスポートの形式は 1.3.0 でも変わらない（試験 T-31）。</summary>
public sealed class FileFormatUnchangedTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Template_files_keep_schema_1_and_no_pc_settings()
    {
        var c = TemplateLibrary.Empty.AddCategory("C");
        var library = c.Library!.AddTemplate(c.Id, "t", "b").Library!;
        var store = new TemplateStore(_dir.Path);
        Assert.True(store.Save(library).Succeeded);
        var export = Path.Combine(_dir.Path, "..", Path.GetFileName(_dir.Path) + "-export.json");
        try
        {
            Assert.True(store.Export(export, library).Succeeded);
            foreach (var path in new[] { store.FilePath, export })
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(text);
                Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(["schemaVersion", "categories", "templates"], doc.RootElement.EnumerateObject().Select(p => p.Name));
                foreach (var key in new[] { "fixedSlots", "pickerPlacement", "pickerLeft", "startupNotice", "hotkey" })
                    Assert.DoesNotContain(key, text);
            }
        }
        finally
        {
            File.Delete(export);
        }
    }
}
