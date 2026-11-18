using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Core.Execution;

public enum FixedTemplateState
{
    NotSet,

    /// <summary>固定 ID のテンプレートが見つからない（削除済み・インポートで置換された等）。</summary>
    Missing,

    Found,
}

/// <summary>
/// 固定モードの参照先（仕様書 §6）。毎回、保存済みの最新のライブラリから ID で引き直す。
/// <see cref="Slot"/> はスロット番号（1〜3。スロットによらない解決では 0）。
/// </summary>
public readonly record struct FixedTemplate(FixedTemplateState State, Template? Template, int Slot = 0)
{
    public static FixedTemplate Resolve(Guid? fixedId, TemplateLibrary library)
    {
        if (fixedId is not { } id || id == Guid.Empty) return new(FixedTemplateState.NotSet, null);
        return library.FindTemplate(id) is { } t ? new(FixedTemplateState.Found, t) : new(FixedTemplateState.Missing, null);
    }

    /// <summary>スロット番号（1〜3）のテンプレートを引く。</summary>
    public static FixedTemplate Resolve(AppSettings settings, int slot, TemplateLibrary library) =>
        Resolve(settings.Slot(slot).TemplateId, library) with { Slot = slot };

    /// <summary>スロット 1〜3 をまとめて引く。</summary>
    public static IReadOnlyList<FixedTemplate> ResolveAll(AppSettings settings, TemplateLibrary library) =>
        [.. FixedSlotRules.Numbers.Select(n => Resolve(settings, n, library))];
}
