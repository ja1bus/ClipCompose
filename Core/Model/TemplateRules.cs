using ClipCompose.Core.Localization;
using ClipCompose.Core.Text;

namespace ClipCompose.Core.Model;

public enum LibraryError
{
    None,
    NameEmpty,
    NameTooLong,
    /// <summary>NUL・改行・対になっていないサロゲートを含む。</summary>
    NameInvalidCharacters,
    NameDuplicate,
    BodyEmpty,
    BodyTooLong,
    /// <summary>NUL・対になっていないサロゲートを含む。</summary>
    BodyInvalidCharacters,
    CategoryNotFound,
    TemplateNotFound,
    /// <summary>テンプレートが所属しているカテゴリは削除できない。</summary>
    CategoryNotEmpty,
}

/// <summary>名前・本文の入力規則（仕様書 §4・§7.2）。上限は Unicode スカラー値の数。</summary>
public static class TemplateRules
{
    public const int CategoryNameMax = 50;
    public const int TemplateNameMax = 100;
    public const int BodyMax = 20_000;

    /// <summary>名前の前後の空白を除いた値と、規則に合うか。</summary>
    public static LibraryError CheckName(string? raw, int max, out string name)
    {
        name = (raw ?? "").Trim();
        if (name.Length == 0) return LibraryError.NameEmpty;
        // 一覧・メニューに 1 行で表示するため、名前には改行を入れない。
        if (TextMetrics.ContainsNul(name) || name.AsSpan().IndexOfAny('\r', '\n') >= 0 || TextMetrics.HasLoneSurrogate(name))
            return LibraryError.NameInvalidCharacters;
        return TextMetrics.ScalarCount(name) > max ? LibraryError.NameTooLong : LibraryError.None;
    }

    /// <summary>本文の規則。本文は Trim しない（空白だけの本文は登録不可）。</summary>
    public static LibraryError CheckBody(string? body)
    {
        if (TextMetrics.IsBlank(body)) return LibraryError.BodyEmpty;
        if (TextMetrics.ContainsNul(body) || TextMetrics.HasLoneSurrogate(body)) return LibraryError.BodyInvalidCharacters;
        return TextMetrics.ScalarCount(body) > BodyMax ? LibraryError.BodyTooLong : LibraryError.None;
    }

    /// <summary>名前の重複判定（前後の空白を除いた後、大文字・小文字を区別しない Ordinal 比較）。</summary>
    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Describe(LibraryError error, bool category = false) => error switch
    {
        LibraryError.None => "",
        LibraryError.NameEmpty => Strings.ErrorNameEmpty,
        LibraryError.NameTooLong => string.Format(Strings.ErrorNameTooLongFormat, category ? CategoryNameMax : TemplateNameMax),
        LibraryError.NameInvalidCharacters => Strings.ErrorNameInvalidCharacters,
        LibraryError.NameDuplicate => category ? Strings.ErrorCategoryNameDuplicate : Strings.ErrorTemplateNameDuplicate,
        LibraryError.BodyEmpty => Strings.ErrorBodyEmpty,
        LibraryError.BodyTooLong => string.Format(Strings.ErrorBodyTooLongFormat, BodyMax.ToString("N0")),
        LibraryError.BodyInvalidCharacters => Strings.ErrorBodyInvalidCharacters,
        LibraryError.CategoryNotFound => Strings.ErrorCategoryNotFound,
        LibraryError.TemplateNotFound => Strings.ErrorTemplateNotFound,
        LibraryError.CategoryNotEmpty => Strings.ErrorCategoryNotEmpty,
        _ => error.ToString(),
    };
}
