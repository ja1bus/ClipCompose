namespace ClipCompose.Core.Settings;

/// <summary>
/// 固定スロットの規則（仕様書 §6・§17.3）。スロット番号は 1〜<see cref="Count"/>。
/// 同じテンプレートは 1 つのスロットにだけ入る。どの操作も元の設定を変えず、複製を返す。
/// </summary>
public static class FixedSlotRules
{
    public const int Count = 3;

    /// <summary>すべて未設定の 3 スロット。</summary>
    public static List<FixedSlot> Defaults() => [.. Enumerable.Range(0, Count).Select(_ => new FixedSlot())];

    public static IEnumerable<int> Numbers => Enumerable.Range(1, Count);

    /// <summary><paramref name="templateId"/> が入っているスロット番号（無ければ null）。</summary>
    public static int? SlotOf(AppSettings settings, Guid templateId)
    {
        for (var i = 0; i < Count; i++)
            if (settings.FixedSlots[i].TemplateId == templateId) return i + 1;
        return null;
    }

    /// <summary>
    /// <paramref name="slot"/> に <paramref name="templateId"/> を入れた複製。別のスロットにあればそこを未設定にする（移る）。
    /// null ならそのスロットを未設定にする。ショートカット・自動貼り付けは変えない。
    /// </summary>
    public static AppSettings WithPin(AppSettings settings, int slot, Guid? templateId)
    {
        if (slot is < 1 or > Count) throw new ArgumentOutOfRangeException(nameof(slot));
        var updated = settings.Clone();
        if (templateId is { } id)
            foreach (var s in updated.FixedSlots.Where(s => s.TemplateId == id)) s.TemplateId = null;
        updated.Slot(slot).TemplateId = templateId;
        return updated;
    }

    /// <summary><paramref name="templateIds"/> のテンプレートが入っているスロットだけを未設定にした複製と、変わったか。</summary>
    public static (AppSettings Settings, bool Changed) WithoutTemplates(AppSettings settings, IReadOnlySet<Guid> templateIds)
    {
        var updated = settings.Clone();
        var changed = false;
        foreach (var s in updated.FixedSlots.Where(s => s.TemplateId is { } id && templateIds.Contains(id)))
        {
            s.TemplateId = null;
            changed = true;
        }
        return (updated, changed);
    }

    /// <summary>すべてのスロットのテンプレートを未設定にした複製と、変わったか（全件置換・以前の状態に戻す）。</summary>
    public static (AppSettings Settings, bool Changed) WithoutAll(AppSettings settings)
    {
        var updated = settings.Clone();
        var changed = false;
        foreach (var s in updated.FixedSlots.Where(s => s.TemplateId is not null))
        {
            s.TemplateId = null;
            changed = true;
        }
        return (updated, changed);
    }
}
