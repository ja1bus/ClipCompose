namespace ClipCompose.Core.Text;

/// <summary>
/// 文字数の数え方と、保存・出力できない文字の判定（仕様書 §4・§7.2）。
/// 上限は Unicode スカラー値の数で数える（絵文字のサロゲートペアは 1 文字、結合文字は個別に 1 文字）。
/// </summary>
public static class TextMetrics
{
    /// <summary>Unicode スカラー値の数。対になっていないサロゲートは 1 つとして数える。</summary>
    public static int ScalarCount(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            count++;
        }
        return count;
    }

    /// <summary>対になっていないサロゲートを含むか（UTF-8 の JSON で表現できない）。</summary>
    public static bool HasLoneSurrogate(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>NUL を含むか（Windows のテキスト形式では NUL で終わるため、正確に渡せない）。</summary>
    public static bool ContainsNul(ReadOnlySpan<char> text) => text.Contains('\0');

    /// <summary>空白（改行を含む）だけ、または空か。</summary>
    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);
}
