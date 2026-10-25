using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Tests;

/// <summary>［バージョン情報］から開く URL（Web は表示言語に合わせる）。</summary>
public sealed class AppLinksTests
{
    [Fact]
    public void Web_pages_follow_the_display_language()
    {
        Assert.Equal("https://www.ja1bus.com/clipcompose/manual/", AppLinks.Manual("ja"));
        Assert.Equal("https://www.ja1bus.com/clipcompose/en/manual/", AppLinks.Manual("en"));
        Assert.Equal("https://www.ja1bus.com/clipcompose/privacy/", AppLinks.Privacy("ja"));
        Assert.Equal("https://www.ja1bus.com/clipcompose/en/privacy/", AppLinks.Privacy("en"));
        Assert.Equal("https://github.com/ja1bus/ClipCompose", AppLinks.Source);
    }
}

/// <summary>テストの間だけ表示言語（CurrentUICulture）を切り替える。</summary>
public sealed class UiCultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public UiCultureScope(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}

/// <summary>表示言語（英語・日本語）の文字列と、言語の決め方（C# 共通の実装標準）。</summary>
public sealed partial class LocalizationTests
{
    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"{culture.Name} の文字列がありません（ja\\ClipCompose.Core.resources.dll）");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static readonly Dictionary<string, string> English = Load(CultureInfo.InvariantCulture);
    private static readonly Dictionary<string, string> Japanese = Load(CultureInfo.GetCultureInfo("ja"));

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]")]
    private static partial Regex JapaneseChars();

    private static int[] Placeholders(string s) =>
        Placeholder().Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();

    [Fact]
    public void Japanese_has_exactly_the_same_keys_as_English()
    {
        Assert.NotEmpty(English);
        Assert.Empty(English.Keys.Except(Japanese.Keys));
        Assert.Empty(Japanese.Keys.Except(English.Keys));
    }

    [Fact]
    public void No_string_is_empty()
    {
        Assert.Empty(English.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
        Assert.Empty(Japanese.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        foreach (var (key, en) in English)
            Assert.True(Placeholders(en).SequenceEqual(Placeholders(Japanese[key])), $"{key}: 埋め込み位置 {{n}} が英語と日本語で違う");
    }

    [Fact]
    public void Format_strings_can_be_formatted()
    {
        foreach (var (key, en) in English.Where(p => p.Key.EndsWith("Format", StringComparison.Ordinal)))
        {
            var count = Placeholders(en).Length;
            Assert.True(count > 0, $"{key}: 名前が Format で終わるのに {{0}} が無い");
            var args = Enumerable.Range(0, count).Select(i => (object)$"<{i}>").ToArray();
            Assert.Contains("<0>", string.Format(CultureInfo.InvariantCulture, en, args));
            Assert.Contains("<0>", string.Format(CultureInfo.InvariantCulture, Japanese[key], args));
        }
    }

    [Fact]
    public void Strings_that_are_not_formats_have_no_placeholders()
    {
        // {{clipboard}} を含む文字列は string.Format に渡さないため、Format で終わる名前にしない。
        foreach (var (key, en) in English.Where(p => !p.Key.EndsWith("Format", StringComparison.Ordinal)))
            Assert.Empty(Placeholders(en));
    }

    [Fact]
    public void Japanese_text_is_not_left_in_English_strings()
    {
        Assert.Empty(English.Where(p => JapaneseChars().IsMatch(p.Value)).Select(p => p.Key));
    }

    [Fact]
    public void Variable_name_is_not_translated()
    {
        Assert.Contains("{{clipboard}}", English[nameof(Strings.EditorInsertVariable)]);
        Assert.Contains("{{clipboard}}", Japanese[nameof(Strings.EditorInsertVariable)]);
        Assert.Contains("{{clipboard}}", Japanese[nameof(Strings.GuideText)]);
    }

    [Theory]
    [InlineData("", "ja-JP", "ja")]
    [InlineData("", "en-US", "en")]
    [InlineData("", "de-DE", "en")]
    [InlineData("en", "ja-JP", "en")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("JA", "en-US", "ja")]
    [InlineData("fr", "ja-JP", "ja")]
    [InlineData(null, "fr-FR", "en")]
    public void Resolve_uses_setting_then_windows_language(string? setting, string windows, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(setting, CultureInfo.GetCultureInfo(windows)).Name);
    }

    [Fact]
    public void Language_names_are_written_in_their_own_language()
    {
        using (new UiCultureScope("en"))
        {
            Assert.Equal("日本語", UiLanguage.DisplayName(UiLanguage.Japanese));
            Assert.Equal(English[nameof(Strings.LanguageSystem)], UiLanguage.DisplayName(UiLanguage.System));
        }
        using (new UiCultureScope("ja"))
        {
            Assert.Equal("English", UiLanguage.DisplayName(UiLanguage.English));
            Assert.Equal(Japanese[nameof(Strings.LanguageSystem)], UiLanguage.DisplayName(UiLanguage.System));
        }
    }

    [Fact]
    public void Get_returns_text_in_the_requested_language()
    {
        using var _ = new UiCultureScope("ja");
        Assert.Equal(English[nameof(Strings.RestartPrompt)], UiLanguage.Get(nameof(Strings.RestartPrompt), CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void Core_messages_follow_the_ui_language()
    {
        using (new UiCultureScope("en"))
        {
            Assert.Equal("Enter a name.", TemplateRules.Describe(LibraryError.NameEmpty));
            Assert.StartsWith("Combine the key with", Shortcut.Describe(ShortcutValidation.RequiresModifier));
        }
        using (new UiCultureScope("ja"))
        {
            Assert.Equal("名前を入力してください。", TemplateRules.Describe(LibraryError.NameEmpty));
        }
    }

    [Fact]
    public void Setting_fix_records_are_English_regardless_of_ui_language()
    {
        using var _ = new UiCultureScope("ja");
        var s = new AppSettings { Language = "fr", NormalHotkey = "bad", FixedHotkey = "Shift+A" };
        var fixes = SettingsNormalizer.Normalize(s);
        Assert.Equal(3, fixes.Count);
        Assert.DoesNotContain(fixes, f => JapaneseChars().IsMatch(f));
    }
}

/// <summary>ホットキーの表記と規則（仕様書 §10。AreaShot と同じ規則）。</summary>
public sealed class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData("alt+ctrl+p", "Ctrl+Alt+P")]
    [InlineData("Win+Shift+F1", "Shift+Win+F1")]
    public void Parse_and_format(string text, string expected)
    {
        Assert.True(Shortcut.TryParse(text, out var s));
        Assert.Equal(expected, s.ToString());
    }

    [Theory]
    [InlineData("Shift+A", ShortcutValidation.RequiresModifier)] // 通常の文字入力を妨げる
    [InlineData("A", ShortcutValidation.RequiresModifier)]
    [InlineData("F9", ShortcutValidation.Ok)]
    [InlineData("Ctrl+Alt+V", ShortcutValidation.Ok)]
    public void Validation(string text, ShortcutValidation expected)
    {
        Assert.True(Shortcut.TryParse(text, out var s));
        Assert.Equal(expected, s.Validate());
    }

    [Fact]
    public void Empty_is_unassigned()
    {
        Assert.False(Shortcut.TryParse("", out var s));
        Assert.True(s.IsEmpty);
        Assert.Equal("", s.ToString());
    }
}
