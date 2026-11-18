using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Views;

/// <summary>
/// 固定スロットを選ぶメニュー（仕様書 §5・§17.5。選択画面と設定画面の［テンプレート］タブで共通）。
/// 項目はスロット 1〜3。アクセスキーは番号（Ctrl+P → 1／2／3）。選んでいるテンプレートが入っているスロットは「解除」。
/// </summary>
public static class PinMenu
{
    /// <summary>メニューの項目に添えるテンプレート名の長さ（UTF-16。超えれば 29 文字＋「…」）。</summary>
    public const int NameLength = 30;

    /// <summary>
    /// メニューを作る。項目を選ぶと <paramref name="pin"/>（スロット番号、入れるテンプレートの ID。解除なら null）を呼ぶ。
    /// </summary>
    public static ContextMenu Create(Template template, IReadOnlyList<FixedTemplate> slots, Action<int, Guid?> pin)
    {
        var menu = new ContextMenu();
        foreach (var slot in slots)
        {
            var number = slot.Slot;
            var current = slot.Template;
            var isThis = current?.Id == template.Id;
            // 名前の "_" がアクセスキーにならないよう、名前だけを書式へ渡す前に "__" にする（番号の "_" はそのまま）。
            var header = isThis ? string.Format(Strings.PinMenuUnpinFormat, number)
                : current is not null ? string.Format(Strings.PinMenuReplaceFormat, number, EscapeName(Shorten(current.Name)))
                : string.Format(Strings.PinMenuPinFormat, number);
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => pin(number, isThis ? null : template.Id);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>ボタンの上にメニューを開き、最初の項目にキーボードのフォーカスを置く。</summary>
    public static void Open(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Top;
        menu.Opened += (_, _) =>
        {
            if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first) first.Focus();
        };
        menu.IsOpen = true;
    }

    /// <summary>メニューに出す名前（30 文字を超えれば 29 文字＋「…」。サロゲートペアの途中で切らない）。</summary>
    public static string Shorten(string name)
    {
        if (name.Length <= NameLength) return name;
        var length = NameLength - 1;
        if (char.IsHighSurrogate(name[length - 1])) length--;
        return name[..length] + "…";
    }

    /// <summary>テンプレート名の中の "_" をアクセスキーの指定と取り違えないようにする。</summary>
    private static string EscapeName(string name) => name.Replace("_", "__");
}
