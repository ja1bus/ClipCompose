using ClipCompose.Core.Localization;

namespace ClipCompose.Core.Text;

/// <summary>ツールチップに載せる固定スロット（参照先のあるものだけ）。<see cref="Key"/> は登録済みのショートカットの表示（未登録なら null）。</summary>
public readonly record struct TrayTooltipSlot(int Slot, string Name, string? Key);

/// <summary>
/// タスクトレイのアイコンのツールチップ（仕様書 §3・§17.5）。NotifyIcon.Text は UTF-16 で 127 文字まで。
/// 全部の行が収まらなければ固定を件数だけにした短い形にし、それでも超えれば 127 文字で切る（サロゲートペアの途中では切らない）。
/// </summary>
public static class TrayTooltip
{
    public const int MaxLength = 127;

    public static string Build(string? normalKey, IReadOnlyList<TrayTooltipSlot> slots)
    {
        var head = new List<string> { "ClipCompose" };
        if (normalKey is not null) head.Add(string.Format(Strings.TrayTooltipNormalFormat, normalKey));

        List<string> full = [.. head];
        if (slots.Count == 0) full.Add(Strings.TrayTooltipNoFixed);
        else
            full.AddRange(slots.Select(s => s.Key is null
                ? string.Format(Strings.TrayTooltipFixedFormat, s.Slot, s.Name)
                : string.Format(Strings.TrayTooltipFixedHotkeyFormat, s.Slot, s.Name, s.Key)));
        var text = string.Join("\n", full);
        if (text.Length <= MaxLength) return text;

        List<string> compact = [.. head, string.Format(Strings.TrayTooltipFixedCountFormat, slots.Count)];
        return Cut(string.Join("\n", compact));
    }

    private static string Cut(string text)
    {
        if (text.Length <= MaxLength) return text;
        var length = MaxLength;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}
