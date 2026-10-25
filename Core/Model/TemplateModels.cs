using ClipCompose.Core.Composition;

namespace ClipCompose.Core.Model;

/// <summary>カテゴリ（仕様書 §4）。ID は作成時に決め、名前・順序を変えても変えない。</summary>
public sealed record Category(Guid Id, string Name)
{
    /// <summary>一覧の項目の名前として UI Automation（読み上げ）に渡る文字列。既定の record の表記（ID を含む）にしない。</summary>
    public override string ToString() => Name;
}

/// <summary>テンプレート（仕様書 §4）。ID は作成時に決め、名前・本文・順序・カテゴリを変えても変えない。</summary>
public sealed record Template(Guid Id, Guid CategoryId, string Name, string Body)
{
    /// <summary>本文に {{clipboard}} があり、実行時にクリップボードを読むか。</summary>
    public bool UsesClipboard => Composer.UsesClipboard(Body);

    /// <summary>読み上げ用の名前。本文は含めない。</summary>
    public override string ToString() => Name;
}
