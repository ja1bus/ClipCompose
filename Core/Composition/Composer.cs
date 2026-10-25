using System.Text;
using ClipCompose.Core.Text;

namespace ClipCompose.Core.Composition;

public enum ComposeError
{
    None,

    /// <summary>変数ありなのに、クリップボードにテキストが無い。</summary>
    InputMissing,

    /// <summary>テキストの長さが 0。</summary>
    InputEmpty,

    /// <summary>NUL を含む（Windows のテキスト形式で正確に渡せない）。</summary>
    InputContainsNul,

    /// <summary>入力が上限（<see cref="Composer.MaxScalars"/>）を超える。</summary>
    InputTooLong,

    /// <summary>合成結果が上限を超える。</summary>
    ResultTooLong,

    /// <summary>テンプレート本文が出力できない（NUL を含む等。保存時の検証をすり抜けた場合）。</summary>
    TemplateInvalid,
}

public readonly record struct ComposeResult(ComposeError Error, string? Text)
{
    public bool Succeeded => Error == ComposeError.None;

    public static ComposeResult Fail(ComposeError error) => new(error, null);
}

/// <summary>
/// テンプレートへのクリップボードの差し込み（仕様書 §7）。
/// 変数は <see cref="Variable"/> だけで、大文字・小文字と空白を含めて完全一致で判定する。
/// テンプレート側だけを走査して置き換えるため、入力の中にある変数の文字列は再展開されない。
/// </summary>
public static class Composer
{
    public const string Variable = "{{clipboard}}";

    /// <summary>入力と合成結果それぞれの安全上限（Unicode スカラー値の数）。貼り付け先が受け付ける量ではない。</summary>
    public const int MaxScalars = 1_000_000;

    public static bool UsesClipboard(string body) => body.Contains(Variable, StringComparison.Ordinal);

    /// <summary>テンプレート内の変数の数。</summary>
    public static int CountVariables(string body)
    {
        var count = 0;
        for (var i = body.IndexOf(Variable, StringComparison.Ordinal); i >= 0; i = body.IndexOf(Variable, i + Variable.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// 変数なしのテンプレートの出力。本文をそのまま返す（クリップボードは使わない）。
    /// </summary>
    public static ComposeResult ComposeWithoutInput(string body)
    {
        if (UsesClipboard(body)) throw new ArgumentException("The template uses the clipboard.", nameof(body));
        if (body.Length == 0 || TextMetrics.ContainsNul(body)) return ComposeResult.Fail(ComposeError.TemplateInvalid);
        return TextMetrics.ScalarCount(body) > MaxScalars ? ComposeResult.Fail(ComposeError.ResultTooLong) : new(ComposeError.None, body);
    }

    /// <summary>
    /// 変数ありのテンプレートへ、一度取得した入力を全箇所に差し込む。
    /// 結果の大きさを先に計算し、上限を超えるなら文字列を作らずに中止する。本文・入力の改行・空白は変えない。
    /// </summary>
    public static ComposeResult Compose(string body, string? input)
    {
        var variables = CountVariables(body);
        if (variables == 0) throw new ArgumentException("The template does not use the clipboard.", nameof(body));
        if (TextMetrics.ContainsNul(body)) return ComposeResult.Fail(ComposeError.TemplateInvalid);
        if (input is null) return ComposeResult.Fail(ComposeError.InputMissing);
        if (input.Length == 0) return ComposeResult.Fail(ComposeError.InputEmpty);
        if (TextMetrics.ContainsNul(input)) return ComposeResult.Fail(ComposeError.InputContainsNul);

        var inputScalars = TextMetrics.ScalarCount(input);
        if (inputScalars > MaxScalars) return ComposeResult.Fail(ComposeError.InputTooLong);
        // 変数の文字列は ASCII なので、1 文字 = 1 スカラー値。
        long fixedScalars = TextMetrics.ScalarCount(body) - (long)variables * Variable.Length;
        if (fixedScalars + (long)variables * inputScalars > MaxScalars) return ComposeResult.Fail(ComposeError.ResultTooLong);

        var capacity = body.Length - variables * Variable.Length + variables * input.Length;
        var sb = new StringBuilder(capacity);
        var start = 0;
        for (var i = body.IndexOf(Variable, StringComparison.Ordinal); i >= 0; i = body.IndexOf(Variable, start, StringComparison.Ordinal))
        {
            sb.Append(body, start, i - start).Append(input);
            start = i + Variable.Length;
        }
        sb.Append(body, start, body.Length - start);
        return new(ComposeError.None, sb.ToString());
    }
}
