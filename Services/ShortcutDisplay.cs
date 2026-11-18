using ClipCompose.Core.Input;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Services;

/// <summary>
/// ショートカットの画面上の表記（仕様書 §10・§17.10）。保存形式（<see cref="Shortcut.ToString"/>）は変えず、
/// 表に名前が無いキー（記号キーなど。保存上は "VK_BA"）だけ、キーボードの配列に合わせた Windows のキー名にする。
/// キー名を取れなければ従来の表記のまま。
/// </summary>
public static class ShortcutDisplay
{
    public static string Format(Shortcut shortcut)
    {
        if (shortcut.IsEmpty) return "";
        var text = shortcut.ToString();
        if (KeyNames.HasName(shortcut.Key) || KeyName(shortcut.Key) is not { } name) return text;
        // 修飾キーの表記はそのまま、最後の主キーの部分だけを差し替える。
        var plus = text.LastIndexOf('+');
        return plus < 0 ? name : text[..(plus + 1)] + name;
    }

    /// <summary>Windows のキー名（取れなければ null）。</summary>
    private static unsafe string? KeyName(int vk)
    {
        var scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
        if (scan == 0) return null;
        var lParam = (int)((scan & 0xFF) << 16);
        if ((scan & 0xFF00) is 0xE000 or 0xE100) lParam |= 1 << 24; // 拡張キー
        var buffer = stackalloc char[64];
        var length = GetKeyNameText(lParam, buffer, 64);
        if (length <= 0) return null;
        var name = new string(buffer, 0, length).Trim();
        // "+" が入るとショートカットの区切りと紛らわしいが、表示だけなのでそのまま出す（空白だけなら使わない）。
        return name.Length == 0 ? null : name;
    }
}
