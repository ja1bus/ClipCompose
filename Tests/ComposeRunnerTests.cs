using ClipCompose.Core.Composition;
using ClipCompose.Core.Execution;
using ClipCompose.Core.Input;
using ClipCompose.Core.Model;
using ClipCompose.Core.Text;

namespace ClipCompose.Tests;

/// <summary>
/// クリップボードの代わり。読み取り・更新番号の参照・書き込みの回数を数える。
/// </summary>
internal sealed class FakeClipboard : IClipboardPort
{
    public ClipboardReadStatus Status = ClipboardReadStatus.Ok;
    public string? Text;
    public ClipProtection Protection;
    public uint Sequence = 100;
    public ClipboardWriteStatus WriteStatus = ClipboardWriteStatus.Written;

    /// <summary>読み取りの後、書き込みの前に他のアプリが書き換えたことにする。</summary>
    public bool ChangeAfterRead;

    /// <summary>読み取りを止めておく（二重実行の確認用）。</summary>
    public TaskCompletionSource? ReadGate;

    public int ReadCount;
    public int SequenceQueries;
    public readonly List<(string Text, ClipProtection Protection, uint? Expected)> Writes = [];

    public uint CurrentSequence
    {
        get
        {
            SequenceQueries++;
            return Sequence;
        }
    }

    public async Task<ClipboardRead> ReadTextAsync(int maxScalars)
    {
        ReadCount++;
        if (ReadGate is not null) await ReadGate.Task;
        var read = new ClipboardRead(Status, Status == ClipboardReadStatus.Ok ? Text : null, Protection, Sequence);
        if (ChangeAfterRead) Sequence++;
        return read;
    }

    public Task<ClipboardWrite> WriteTextAsync(string text, ClipProtection protection, uint? expectedSequence)
    {
        if (expectedSequence is { } e && e != Sequence) return Task.FromResult(new ClipboardWrite(ClipboardWriteStatus.ChangedSinceRead));
        if (WriteStatus != ClipboardWriteStatus.Written) return Task.FromResult(new ClipboardWrite(WriteStatus));
        Writes.Add((text, protection, expectedSequence));
        Text = text;
        Protection = protection;
        Sequence++;
        return Task.FromResult(new ClipboardWrite(ClipboardWriteStatus.Written, Sequence));
    }
}

internal sealed class FakePaste : IPastePort
{
    public PasteOutcome Outcome = new(PasteStatus.Sent);
    public readonly List<(PasteTarget Target, uint Sequence)> Calls = [];

    public Task<PasteOutcome> PasteAsync(PasteTarget target, uint writtenSequence)
    {
        Calls.Add((target, writtenSequence));
        return Task.FromResult(Outcome);
    }
}

/// <summary>実行の流れ（仕様書 §7〜§9、受入条件 1・3・5・8）。</summary>
public sealed class ComposeRunnerTests
{
    private static readonly Guid CategoryId = Guid.NewGuid();
    private static readonly PasteTarget Target = new(123, KeyModifiers.Control | KeyModifiers.Alt, 'P');

    private static Template T(string body) => new(Guid.NewGuid(), CategoryId, "t", body);

    private readonly FakeClipboard _clipboard = new();
    private readonly FakePaste _paste = new();
    private readonly ComposeRunner _runner;

    public ComposeRunnerTests()
    {
        _runner = new ComposeRunner(_clipboard, _paste);
    }

    [Theory]
    [InlineData(ClipboardReadStatus.Ok, "existing text")]
    [InlineData(ClipboardReadStatus.NoText, null)] // 空・画像・ファイル
    [InlineData(ClipboardReadStatus.Unavailable, null)]
    public async Task Template_without_variable_never_reads_the_clipboard(ClipboardReadStatus status, string? text)
    {
        _clipboard.Status = status;
        _clipboard.Text = text;
        _clipboard.Protection = ClipProtection.ExcludeFromHistory | ClipProtection.NoCloudUpload;

        var outcome = await _runner.RunAsync(T("定型文"), null);

        Assert.Equal(RunStatus.Copied, outcome.Status);
        Assert.Equal(0, _clipboard.ReadCount);
        Assert.Equal(0, _clipboard.SequenceQueries); // 形式・保護指定・更新番号の確認もしない
        var write = Assert.Single(_clipboard.Writes);
        Assert.Equal("定型文", write.Text);
        Assert.Equal(ClipProtection.None, write.Protection); // 入力の保護指定を読まず、標準の出力
        Assert.Null(write.Expected);
    }

    [Fact]
    public async Task Template_without_variable_can_run_repeatedly()
    {
        var t = T("same");
        Assert.Equal(RunStatus.Copied, (await _runner.RunAsync(t, null)).Status);
        Assert.Equal(RunStatus.Copied, (await _runner.RunAsync(t, null)).Status);
        Assert.Equal(2, _clipboard.Writes.Count);
        Assert.Equal(0, _clipboard.ReadCount);
    }

    [Fact]
    public async Task Template_with_variable_reads_once_and_inserts_everywhere()
    {
        _clipboard.Text = "本文";
        var outcome = await _runner.RunAsync(T("{{clipboard}}/{{clipboard}}"), null);
        Assert.Equal(RunStatus.Copied, outcome.Status);
        Assert.Equal(1, _clipboard.ReadCount);
        var write = Assert.Single(_clipboard.Writes);
        Assert.Equal("本文/本文", write.Text);
        Assert.Equal(100u, write.Expected); // 読んだ時点の更新番号から変わっていない場合だけ書く
    }

    [Theory]
    [InlineData(ClipProtection.NoCloudUpload)]
    [InlineData(ClipProtection.ExcludeFromHistory | ClipProtection.NoCloudUpload)]
    public async Task Protection_of_the_input_is_kept_in_the_output(ClipProtection protection)
    {
        _clipboard.Text = "secret";
        _clipboard.Protection = protection;
        await _runner.RunAsync(T("pw: {{clipboard}}"), null);
        Assert.Equal(protection, Assert.Single(_clipboard.Writes).Protection);
    }

    [Fact]
    public async Task Failure_to_apply_protection_is_not_success()
    {
        _clipboard.Text = "secret";
        _clipboard.Protection = ClipProtection.ExcludeFromHistory | ClipProtection.NoCloudUpload;
        _clipboard.WriteStatus = ClipboardWriteStatus.ProtectionUnavailable;
        var outcome = await _runner.RunAsync(T("{{clipboard}}"), Target);
        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Equal(RunFailure.ProtectionUnavailable, outcome.Failure);
        Assert.Empty(_clipboard.Writes);
        Assert.Empty(_paste.Calls);
    }

    [Theory]
    [InlineData(ClipboardReadStatus.NoText, null, RunStatus.InputRejected, ComposeError.InputMissing)]
    [InlineData(ClipboardReadStatus.Ok, "", RunStatus.InputRejected, ComposeError.InputEmpty)]
    [InlineData(ClipboardReadStatus.Ok, "a\0b", RunStatus.InputRejected, ComposeError.InputContainsNul)]
    [InlineData(ClipboardReadStatus.TooLong, null, RunStatus.InputRejected, ComposeError.InputTooLong)]
    [InlineData(ClipboardReadStatus.Unavailable, null, RunStatus.Failed, ComposeError.None)]
    public async Task Unusable_input_does_not_write(ClipboardReadStatus status, string? text, RunStatus expected, ComposeError error)
    {
        _clipboard.Status = status;
        _clipboard.Text = text;
        var outcome = await _runner.RunAsync(T("{{clipboard}}"), Target);
        Assert.Equal(expected, outcome.Status);
        Assert.Equal(error, outcome.ComposeError);
        Assert.Empty(_clipboard.Writes);
        Assert.Empty(_paste.Calls);
    }

    [Fact]
    public async Task Change_after_reading_is_not_overwritten()
    {
        _clipboard.Text = "x";
        _clipboard.ChangeAfterRead = true;
        var outcome = await _runner.RunAsync(T("{{clipboard}}"), Target);
        Assert.Equal(RunStatus.ClipboardChangedBeforeCopy, outcome.Status);
        Assert.Empty(_clipboard.Writes);
        Assert.Empty(_paste.Calls);
    }

    [Fact]
    public async Task Recomposing_the_previous_output_is_stopped()
    {
        _clipboard.Text = "source";
        var t = T("Q: {{clipboard}}");
        Assert.Equal(RunStatus.Copied, (await _runner.RunAsync(t, null)).Status);

        var again = await _runner.RunAsync(t, null);
        Assert.Equal(RunStatus.AlreadyComposed, again.Status);
        Assert.Equal(1, _clipboard.ReadCount); // 2 回目は読まない
        Assert.Single(_clipboard.Writes);

        // 利用者がコピーし直せば実行できる。
        _clipboard.Text = "new source";
        _clipboard.Sequence++;
        Assert.Equal(RunStatus.Copied, (await _runner.RunAsync(t, null)).Status);
        Assert.Equal("Q: new source", _clipboard.Writes[^1].Text);
    }

    [Fact]
    public async Task Concurrent_request_is_dropped_not_queued()
    {
        _clipboard.Text = "x";
        _clipboard.ReadGate = new TaskCompletionSource();
        var first = _runner.RunAsync(T("{{clipboard}}"), null);
        Assert.True(_runner.IsBusy);

        var second = await _runner.RunAsync(T("other"), null);
        Assert.Equal(RunStatus.Busy, second.Status);

        _clipboard.ReadGate.SetResult();
        Assert.Equal(RunStatus.Copied, (await first).Status);
        Assert.False(_runner.IsBusy);
        Assert.Single(_clipboard.Writes); // 捨てた要求は後から実行しない
    }

    [Fact]
    public async Task Paste_is_requested_only_with_a_target_and_after_a_successful_write()
    {
        await _runner.RunAsync(T("text"), Target);
        var call = Assert.Single(_paste.Calls);
        Assert.Equal(Target, call.Target);
        Assert.Equal(_clipboard.Sequence, call.Sequence); // 書き込み後の番号で直前確認する
    }

    [Theory]
    [InlineData(PasteStatus.Sent, PasteSkipReason.None, RunStatus.PasteSent)]
    [InlineData(PasteStatus.CopiedOnly, PasteSkipReason.TargetElevated, RunStatus.CopiedPasteSkipped)]
    [InlineData(PasteStatus.CopiedOnly, PasteSkipReason.KeysHeld, RunStatus.CopiedPasteSkipped)]
    [InlineData(PasteStatus.ClipboardChanged, PasteSkipReason.None, RunStatus.ClipboardChangedAfterCopy)]
    public async Task Paste_outcomes_are_reported_separately(PasteStatus paste, PasteSkipReason reason, RunStatus expected)
    {
        _paste.Outcome = new(paste, reason);
        var outcome = await _runner.RunAsync(T("text"), Target);
        Assert.Equal(expected, outcome.Status);
        Assert.Equal(reason, outcome.PasteSkip);
    }

    [Fact]
    public async Task Begin_and_cancel_hold_the_single_slot_for_the_chooser()
    {
        Assert.True(_runner.TryBegin());
        Assert.Equal(RunStatus.Busy, (await _runner.RunAsync(T("x"), null)).Status);
        _runner.Cancel();
        Assert.False(_runner.IsBusy);
        Assert.Empty(_clipboard.Writes);
        Assert.Equal(0, _clipboard.ReadCount); // 中止ではクリップボードを読み書きしない
    }

    [Fact]
    public async Task Write_failure_is_reported_and_clears_the_previous_output_record()
    {
        _clipboard.Text = "x";
        var t = T("{{clipboard}}!");
        await _runner.RunAsync(t, null);
        _clipboard.WriteStatus = ClipboardWriteStatus.Failed;
        _clipboard.Sequence++; // 利用者がコピーし直した
        Assert.Equal(RunFailure.WriteFailed, (await _runner.RunAsync(t, null)).Failure);
    }

    /// <summary>
    /// レビュー指摘 5: 書き込んだ直後に他のアプリが書き換えた場合、その番号を自分の出力として記録しない。
    /// 記録すると、貼り付け直前の変更検出をすり抜けて他のアプリの内容を貼り付けるおそれがある。
    /// </summary>
    [Fact]
    public async Task Output_replaced_right_after_writing_is_not_pasted_or_recorded()
    {
        _clipboard.Text = "source";
        _clipboard.WriteStatus = ClipboardWriteStatus.WrittenButReplaced;
        var outcome = await _runner.RunAsync(T("{{clipboard}}!"), Target);
        Assert.Equal(RunStatus.ClipboardChangedAfterCopy, outcome.Status);
        Assert.Empty(_paste.Calls);

        // 前回の出力として記録していないので、次の実行は「コピーし直して」で止まらずに読む。
        _clipboard.WriteStatus = ClipboardWriteStatus.Written;
        Assert.Equal(RunStatus.Copied, (await _runner.RunAsync(T("{{clipboard}}?"), null)).Status);
        Assert.Equal(2, _clipboard.ReadCount);
    }

    /// <summary>レビュー指摘 4: 利用者が別のアプリへ移った場合は前面を奪い返さない。</summary>
    [Theory]
    [InlineData(true, false, false, FocusAction.Proceed)]  // 貼り付け先が前面のまま
    [InlineData(true, true, true, FocusAction.Proceed)]
    [InlineData(false, true, true, FocusAction.Restore)]   // 通常モード: 選択画面を閉じて前面が ClipCompose 側・無し
    [InlineData(false, false, true, FocusAction.Abandon)]  // 通常モード: 利用者が別のアプリへ移った
    [InlineData(false, true, false, FocusAction.Abandon)]  // 固定モード: 前面を奪っていないので戻さない
    [InlineData(false, false, false, FocusAction.Abandon)] // 固定モード: 別のアプリへ移った
    public void Focus_is_restored_only_after_closing_the_chooser(bool isTarget, bool isOwnOrNone, bool restoreAllowed, FocusAction expected)
    {
        Assert.Equal(expected, PasteFocusRule.Decide(isTarget, isOwnOrNone, restoreAllowed));
    }

    [Fact]
    public void Pinned_mode_targets_do_not_restore_focus_by_default()
    {
        Assert.False(new PasteTarget(1, KeyModifiers.Control, 'P').RestoreFocus);
    }

    [Fact]
    public void Fixed_template_states()
    {
        var library = TemplateLibrary.Empty.AddCategory("c").Library!;
        var category = library.Categories[0].Id;
        var edit = library.AddTemplate(category, "fixed", "body");
        library = edit.Library!;

        Assert.Equal(FixedTemplateState.NotSet, FixedTemplate.Resolve(null, library).State);
        Assert.Equal(FixedTemplateState.Missing, FixedTemplate.Resolve(Guid.NewGuid(), library).State);
        var found = FixedTemplate.Resolve(edit.Id, library);
        Assert.Equal(FixedTemplateState.Found, found.State);

        // 同じ ID を編集したら、保存済みの最新版を使う。
        library = library.UpdateTemplate(edit.Id, category, "fixed", "new body").Library!;
        Assert.Equal("new body", FixedTemplate.Resolve(edit.Id, library).Template!.Body);

        // 削除後は参照先不在（別のテンプレートを自動で固定しない）。
        library = library.AddTemplate(category, "other", "x").Library!.DeleteTemplate(edit.Id).Library!;
        Assert.Equal(FixedTemplateState.Missing, FixedTemplate.Resolve(edit.Id, library).State);
    }
}
