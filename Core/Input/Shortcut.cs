using ClipCompose.Core.Localization;

namespace ClipCompose.Core.Input;

/// <summary>修飾キー。値は Win32 RegisterHotKey の MOD_* と同じ。</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

public enum ShortcutValidation
{
    Ok,
    Empty,
    ModifierOnly,
    RequiresModifier,
}

/// <summary>
/// グローバルショートカット（修飾キー＋主キーの仮想キーコード）。文字列表記は "Ctrl+Alt+S"。Key = 0 は未割当。
/// </summary>
public readonly record struct Shortcut(KeyModifiers Modifiers, int Key)
{
    public static readonly Shortcut None = default;

    public bool IsEmpty => Key == 0;

    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = ParseModifier(parts[i]);
            if (m == KeyModifiers.None || modifiers.HasFlag(m)) return false;
            modifiers |= m;
        }
        if (ParseModifier(parts[^1]) != KeyModifiers.None) return false;
        if (!KeyNames.TryParse(parts[^1], out var vk) || KeyNames.IsModifierKey(vk)) return false;
        shortcut = new Shortcut(modifiers, vk);
        return true;
    }

    private static KeyModifiers ParseModifier(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => KeyModifiers.Control,
        "ALT" => KeyModifiers.Alt,
        "SHIFT" => KeyModifiers.Shift,
        "WIN" or "WINDOWS" => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// 規則: 主キーが必要。修飾キー（Ctrl・Alt・Win。Shift だけは不可）を含むこと。
    /// ただし通常の入力に使わないキー（F1～F24・PrintScreen・Pause・ScrollLock）は単独でもよい。
    /// </summary>
    public ShortcutValidation Validate()
    {
        if (IsEmpty) return ShortcutValidation.Empty;
        if (KeyNames.IsModifierKey(Key)) return ShortcutValidation.ModifierOnly;
        var hasCommand = (Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        if (!hasCommand && !KeyNames.IsStandaloneKey(Key)) return ShortcutValidation.RequiresModifier;
        return ShortcutValidation.Ok;
    }

    public static string Describe(ShortcutValidation result) => result switch
    {
        ShortcutValidation.Ok => "",
        ShortcutValidation.Empty => Strings.ShortcutEmpty,
        ShortcutValidation.ModifierOnly => Strings.ShortcutModifierOnly,
        ShortcutValidation.RequiresModifier => Strings.ShortcutRequiresModifier,
        _ => result.ToString(),
    };

    /// <summary>"Ctrl+Alt+Shift+Win+Key" の順で表記する。未割当は空文字。</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.GetName(Key));
        return string.Join("+", parts);
    }
}
