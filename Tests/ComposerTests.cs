using ClipCompose.Core.Composition;
using ClipCompose.Core.Text;

namespace ClipCompose.Tests;

/// <summary>差し込み・文字数・入力の拒否（仕様書 §7、受入条件 2〜4）。</summary>
public sealed class ComposerTests
{
    [Fact]
    public void All_variables_are_replaced_with_the_same_input()
    {
        var r = Composer.Compose("A:{{clipboard}}\nB:{{clipboard}}", "x");
        Assert.True(r.Succeeded);
        Assert.Equal("A:x\nB:x", r.Text);
    }

    [Fact]
    public void Variables_inside_the_input_are_not_expanded_again()
    {
        var r = Composer.Compose("[{{clipboard}}]", "{{clipboard}}{{clipboard}}");
        Assert.Equal("[{{clipboard}}{{clipboard}}]", r.Text);
    }

    [Theory]
    [InlineData("{{Clipboard}}")]
    [InlineData("{{ clipboard }}")]
    [InlineData("{clipboard}")]
    [InlineData("{{clipboard}")]
    [InlineData("{{date}}")]
    public void Only_the_exact_variable_is_recognized(string body)
    {
        Assert.False(Composer.UsesClipboard(body));
        Assert.Equal(body, Composer.ComposeWithoutInput(body).Text); // 未知の変数らしい文字列はそのまま
    }

    [Fact]
    public void Adjacent_braces_around_the_variable_are_kept()
    {
        Assert.Equal("{x}", Composer.Compose("{{{clipboard}}}", "x").Text);
    }

    [Fact]
    public void Line_breaks_spaces_japanese_symbols_and_emoji_are_preserved()
    {
        const string input = "  先頭の空白\r\n\t日本語・記号 <&>\"' 😀👍🏽 é\r\n\r\n";
        const string body = "\r\n前置き {{clipboard}} 後書き  \n";
        var r = Composer.Compose(body, input);
        Assert.Equal("\r\n前置き " + input + " 後書き  \n", r.Text);
    }

    [Fact]
    public void Whitespace_only_input_is_used_as_is()
    {
        Assert.Equal("< \r\n >", Composer.Compose("<{{clipboard}}>", " \r\n ").Text);
    }

    [Theory]
    [InlineData(null, ComposeError.InputMissing)]
    [InlineData("", ComposeError.InputEmpty)]
    [InlineData("a\0b", ComposeError.InputContainsNul)]
    public void Unusable_input_is_rejected(string? input, ComposeError expected)
    {
        var r = Composer.Compose("{{clipboard}}", input);
        Assert.False(r.Succeeded);
        Assert.Equal(expected, r.Error);
        Assert.Null(r.Text);
    }

    [Fact]
    public void Input_over_the_limit_is_rejected()
    {
        var input = new string('a', Composer.MaxScalars + 1);
        Assert.Equal(ComposeError.InputTooLong, Composer.Compose("{{clipboard}}", input).Error);
        Assert.True(Composer.Compose("{{clipboard}}", new string('a', Composer.MaxScalars)).Succeeded);
    }

    [Fact]
    public void Result_over_the_limit_is_rejected_before_building_it()
    {
        // 入力は上限内でも、2 箇所に差し込むと超える。
        var input = new string('a', Composer.MaxScalars / 2 + 1);
        Assert.Equal(ComposeError.ResultTooLong, Composer.Compose("{{clipboard}}{{clipboard}}", input).Error);
        // 固定部分を含めてちょうど上限なら成功する。
        var exact = Composer.Compose("ab{{clipboard}}", new string('a', Composer.MaxScalars - 2));
        Assert.True(exact.Succeeded);
    }

    [Fact]
    public void Surrogate_pairs_count_as_one_and_combining_marks_separately()
    {
        Assert.Equal(1, TextMetrics.ScalarCount("😀"));
        Assert.Equal(2, TextMetrics.ScalarCount("é"));
        Assert.Equal(4, TextMetrics.ScalarCount("👍🏽ab"));
        Assert.Equal(1, TextMetrics.ScalarCount("\uD800")); // 対になっていないサロゲート

        // 絵文字だけの入力は、UTF-16 の長さが上限の 2 倍でもスカラー値の数で判定する。
        var emoji = string.Concat(Enumerable.Repeat("😀", Composer.MaxScalars));
        Assert.True(Composer.Compose("{{clipboard}}", emoji).Succeeded);
    }

    [Fact]
    public void Lone_surrogates_in_the_input_are_passed_through_unchanged()
    {
        Assert.Equal("[\uD800]", Composer.Compose("[{{clipboard}}]", "\uD800").Text);
    }

    [Fact]
    public void Template_without_variable_is_output_as_is()
    {
        const string body = "  定型文\r\n  ";
        Assert.Equal(body, Composer.ComposeWithoutInput(body).Text);
        Assert.Equal(ComposeError.TemplateInvalid, Composer.ComposeWithoutInput("a\0").Error);
    }

    [Fact]
    public void Template_containing_nul_is_rejected()
    {
        Assert.Equal(ComposeError.TemplateInvalid, Composer.Compose("{{clipboard}}\0", "x").Error);
    }

    [Fact]
    public void Variables_are_counted()
    {
        Assert.Equal(0, Composer.CountVariables("text"));
        Assert.Equal(3, Composer.CountVariables("{{clipboard}}{{clipboard}} {{clipboard}}"));
    }
}
