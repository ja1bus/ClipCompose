using ClipCompose.Core.Model;

namespace ClipCompose.Core.Execution;

public enum FixedTemplateState
{
    NotSet,

    /// <summary>固定 ID のテンプレートが見つからない（削除済み・インポートで置換された等）。</summary>
    Missing,

    Found,
}

/// <summary>固定モードの参照先（仕様書 §6）。毎回、保存済みの最新のライブラリから ID で引き直す。</summary>
public readonly record struct FixedTemplate(FixedTemplateState State, Template? Template)
{
    public static FixedTemplate Resolve(Guid? fixedId, TemplateLibrary library)
    {
        if (fixedId is not { } id || id == Guid.Empty) return new(FixedTemplateState.NotSet, null);
        return library.FindTemplate(id) is { } t ? new(FixedTemplateState.Found, t) : new(FixedTemplateState.Missing, null);
    }
}
