using System.Globalization;
using System.Text;
using ClipCompose.Core.Text;

namespace ClipCompose.Core.Model;

/// <summary>
/// 重ならない名前の候補（複製の「（コピー）」、取り込みの「（取り込み）」）。上限は Unicode スカラー値の数で、
/// 接尾辞の分を先に確保して元の名前の側だけを短くする（接尾辞は削らない）。
/// </summary>
public static class UniqueName
{
    /// <param name="name">元の名前。</param>
    /// <param name="max">名前の上限（スカラー値の数）。</param>
    /// <param name="suffix">1 つ目の接尾辞（例: "（コピー）"）。</param>
    /// <param name="numberedFormat">2 つ目以降の接尾辞（{0} が番号。例: "（コピー {0}）"）。</param>
    /// <param name="isTaken">その名前が既に使われているか。</param>
    public static string Make(string name, int max, string suffix, string numberedFormat, Func<string, bool> isTaken)
    {
        var baseName = name.Trim();
        for (var n = 1; ; n++)
        {
            var tail = n == 1 ? suffix : string.Format(CultureInfo.InvariantCulture, numberedFormat, n);
            var candidate = Truncate(baseName, max - TextMetrics.ScalarCount(tail)).TrimEnd() + tail;
            if (!isTaken(candidate)) return candidate;
        }
    }

    /// <summary>先頭から <paramref name="max"/> 個のスカラー値まで（サロゲートペアを分けない）。</summary>
    private static string Truncate(string text, int max)
    {
        if (max <= 0) return "";
        if (TextMetrics.ScalarCount(text) <= max) return text;
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ == max) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}
