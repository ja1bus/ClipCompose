using ClipCompose.Core.Composition;
using ClipCompose.Core.Model;
using ClipCompose.Core.Text;

namespace ClipCompose.Core.Execution;

/// <summary>実行結果の分類（仕様書 §9 の通知の区分）。</summary>
public enum RunStatus
{
    /// <summary>コピー成功（自動貼り付けなし）。</summary>
    Copied,

    /// <summary>コピーして貼り付け操作を送信した。</summary>
    PasteSent,

    /// <summary>コピーのみ・自動貼り付けは中止。</summary>
    CopiedPasteSkipped,

    /// <summary>コピー後、貼り付け前に他のアプリがクリップボードを変えた（新しい内容は上書きしない）。</summary>
    ClipboardChangedAfterCopy,

    /// <summary>入力を読んだ後に他のアプリがクリップボードを変えたため、書き込まずに中止した。</summary>
    ClipboardChangedBeforeCopy,

    /// <summary>直前の自分の出力がそのまま残っている（変数ありの再合成を中止）。</summary>
    AlreadyComposed,

    /// <summary>別の実行の途中だった（要求は蓄積しない）。</summary>
    Busy,

    /// <summary>入力または合成結果が条件を満たさない（<see cref="RunOutcome.ComposeError"/>）。</summary>
    InputRejected,

    /// <summary>クリップボードを開けない・書き込みに失敗した等。</summary>
    Failed,
}

public enum RunFailure
{
    None,
    ClipboardBusy,
    ProtectionUnavailable,
    WriteFailed,
}

public readonly record struct RunOutcome(
    RunStatus Status,
    ComposeError ComposeError = ComposeError.None,
    RunFailure Failure = RunFailure.None,
    PasteSkipReason PasteSkip = PasteSkipReason.None);

/// <summary>
/// テンプレートの実行（仕様書 §5〜§9）。
/// - 変数なし: クリップボードの本文・形式・保護指定を一切読まず、本文をそのまま標準の出力で書き込む。
/// - 変数あり: 一度の取得で本文と保護指定を読み、全箇所へ差し込む。保護指定は弱めずに付け直す。
///   書き込みは読んだ時点から変わっていない場合だけ行う。
/// - 同時に実行するのは 1 件だけ。実行中の要求は捨てる（後から続けて実行しない）。
/// 呼び出しは UI スレッドから行う前提（状態をロックで守らない）。
/// </summary>
public sealed class ComposeRunner
{
    private readonly IClipboardPort _clipboard;
    private readonly IPastePort _paste;

    /// <summary>直前に自分が書き込んだ後の更新番号（再合成の判定だけに使う。本文は持たない）。</summary>
    private uint? _lastOutputSequence;

    public ComposeRunner(IClipboardPort clipboard, IPastePort paste)
    {
        _clipboard = clipboard;
        _paste = paste;
    }

    public bool IsBusy { get; private set; }

    /// <summary>実行を始められるか確かめて、始められるなら実行中にする。選択画面を開く前の確認に使う。</summary>
    public bool TryBegin()
    {
        if (IsBusy) return false;
        IsBusy = true;
        return true;
    }

    /// <summary><see cref="TryBegin"/> の後、実行せずに終える（選択画面を中止した場合）。</summary>
    public void Cancel() => IsBusy = false;

    /// <summary>実行中でなければ実行する。</summary>
    /// <param name="template">保存済みのテンプレート。</param>
    /// <param name="target">自動貼り付けの貼り付け先。null ならコピーのみ。</param>
    public async Task<RunOutcome> RunAsync(Template template, PasteTarget? target)
    {
        if (!TryBegin()) return new(RunStatus.Busy);
        return await RunBegunAsync(template, target);
    }

    /// <summary><see cref="TryBegin"/> で実行中にした後に呼ぶ。終わったら実行中を解除する。</summary>
    public async Task<RunOutcome> RunBegunAsync(Template template, PasteTarget? target)
    {
        if (!IsBusy) throw new InvalidOperationException("TryBegin was not called.");
        try
        {
            return await RunCoreAsync(template, target);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<RunOutcome> RunCoreAsync(Template template, PasteTarget? target)
    {
        string text;
        ClipProtection protection;
        uint? expected;
        if (template.UsesClipboard)
        {
            if (_lastOutputSequence is { } last && _clipboard.CurrentSequence == last) return new(RunStatus.AlreadyComposed);
            var read = await _clipboard.ReadTextAsync(Composer.MaxScalars);
            switch (read.Status)
            {
                case ClipboardReadStatus.NoText:
                    return new(RunStatus.InputRejected, ComposeError.InputMissing);
                case ClipboardReadStatus.TooLong:
                    return new(RunStatus.InputRejected, ComposeError.InputTooLong);
                case ClipboardReadStatus.Unavailable:
                    return new(RunStatus.Failed, Failure: RunFailure.ClipboardBusy);
            }
            var composed = Composer.Compose(template.Body, read.Text);
            if (!composed.Succeeded) return new(RunStatus.InputRejected, composed.Error);
            text = composed.Text!;
            protection = read.Protection;
            expected = read.Sequence;
        }
        else
        {
            var composed = Composer.ComposeWithoutInput(template.Body);
            if (!composed.Succeeded) return new(RunStatus.InputRejected, composed.Error);
            text = composed.Text!;
            protection = ClipProtection.None;
            expected = null;
        }

        var write = await _clipboard.WriteTextAsync(text, protection, expected);
        switch (write.Status)
        {
            case ClipboardWriteStatus.Written:
                break;
            case ClipboardWriteStatus.WrittenButReplaced:
                // 書き込んだ直後に他のアプリが書き換えた。その番号を自分の出力として記録せず、貼り付けもしない。
                _lastOutputSequence = null;
                return new(RunStatus.ClipboardChangedAfterCopy);
            case ClipboardWriteStatus.ChangedSinceRead:
                return new(RunStatus.ClipboardChangedBeforeCopy);
            case ClipboardWriteStatus.Busy:
                return new(RunStatus.Failed, Failure: RunFailure.ClipboardBusy);
            case ClipboardWriteStatus.ProtectionUnavailable:
                return new(RunStatus.Failed, Failure: RunFailure.ProtectionUnavailable);
            default:
                // 途中で失敗した場合は元の内容が残っているとは限らないため、前回の出力の記録も消す。
                _lastOutputSequence = null;
                return new(RunStatus.Failed, Failure: RunFailure.WriteFailed);
        }
        _lastOutputSequence = write.Sequence;

        if (target is not { } t) return new(RunStatus.Copied);
        var paste = await _paste.PasteAsync(t, write.Sequence);
        return paste.Status switch
        {
            PasteStatus.Sent => new(RunStatus.PasteSent),
            PasteStatus.ClipboardChanged => new(RunStatus.ClipboardChangedAfterCopy),
            _ => new(RunStatus.CopiedPasteSkipped, PasteSkip: paste.Reason),
        };
    }
}
