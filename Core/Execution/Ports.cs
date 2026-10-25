using ClipCompose.Core.Input;
using ClipCompose.Core.Text;

namespace ClipCompose.Core.Execution;

public enum ClipboardReadStatus
{
    /// <summary>テキストを読めた（長さ 0 の場合も含む）。</summary>
    Ok,

    /// <summary>テキストの形式が無い（空・画像・ファイル等）。</summary>
    NoText,

    /// <summary>テキストの大きさが上限を明らかに超える（全体を読まずに打ち切った）。</summary>
    TooLong,

    /// <summary>他のアプリが使用中で開けない、またはデータを取得できない。</summary>
    Unavailable,
}

/// <summary>入力の取得結果。本文・保護指定・更新番号は、クリップボードを開いている間の同じ時点のもの。</summary>
public sealed record ClipboardRead(ClipboardReadStatus Status, string? Text, ClipProtection Protection, uint Sequence);

public enum ClipboardWriteStatus
{
    Written,

    /// <summary>
    /// 書き込んだ直後（閉じた後、自分の書き込み後の更新番号を確かめる前）に他のアプリが書き換えた。
    /// 自分の出力の番号が分からないため、貼り付け・二重合成の判定には使わない。
    /// </summary>
    WrittenButReplaced,

    /// <summary>入力を読んだ後に他のアプリが書き換えたため、上書きしなかった。</summary>
    ChangedSinceRead,

    /// <summary>他のアプリが使用中で開けなかった。</summary>
    Busy,

    /// <summary>必要な保護指定を付けられないため、本文を書き込まなかった。</summary>
    ProtectionUnavailable,

    /// <summary>書き込みの途中で失敗した（元の内容が残っているとは限らない）。</summary>
    Failed,
}

/// <summary>書き込みの結果。<see cref="Sequence"/> は書き込み後の更新番号（自動貼り付けの直前確認・二重合成の判定に使う）。</summary>
public sealed record ClipboardWrite(ClipboardWriteStatus Status, uint Sequence = 0);

/// <summary>
/// クリップボードの読み書き（Win32 の実装はアプリ側）。読み取りの回数をテストで数えられるよう、ここを通して呼ぶ。
/// </summary>
public interface IClipboardPort
{
    /// <summary>現在の更新番号（GetClipboardSequenceNumber）。内容・形式・保護指定は読まない。</summary>
    uint CurrentSequence { get; }

    /// <summary>Unicode テキストと保護指定を一度に取得する。<paramref name="maxScalars"/> を明らかに超える分は読まない。</summary>
    Task<ClipboardRead> ReadTextAsync(int maxScalars);

    /// <summary>
    /// プレーンテキストを書き込む。<paramref name="expectedSequence"/> が指定されていれば、開いた後・空にする前に
    /// 更新番号を比べ、違えば書き込まずに <see cref="ClipboardWriteStatus.ChangedSinceRead"/> を返す。
    /// </summary>
    Task<ClipboardWrite> WriteTextAsync(string text, ClipProtection protection, uint? expectedSequence);
}

/// <summary>
/// ホットキー押下時の貼り付け先と、押されていたキー（仕様書 §9）。Window はトップレベルウィンドウのハンドル。
/// <paramref name="RestoreFocus"/> は、ClipCompose の選択画面を閉じた後に貼り付け先を前面へ戻してよいか（通常モード）。
/// 固定モードは前面を奪っていないため戻さない。
/// </summary>
public readonly record struct PasteTarget(nint Window, KeyModifiers Modifiers, int TriggerKey, bool RestoreFocus = false);

public enum FocusAction
{
    /// <summary>貼り付け先が前面。そのまま進める。</summary>
    Proceed,

    /// <summary>前面が ClipCompose 自身か無い（選択画面を閉じた直後）。貼り付け先を前面へ戻す。</summary>
    Restore,

    /// <summary>利用者が別のアプリへ移った。奪い返さずにコピーのみにする。</summary>
    Abandon,
}

/// <summary>
/// 自動貼り付けの前に、前面のウィンドウをどう扱うか（仕様書 §9「別アプリへ移動した場合は強制的に奪い返さない」）。
/// </summary>
public static class PasteFocusRule
{
    /// <param name="foregroundIsTarget">前面のトップレベルウィンドウが貼り付け先か。</param>
    /// <param name="foregroundIsOwnOrNone">前面が ClipCompose 自身のウィンドウか、前面のウィンドウが無いか。</param>
    /// <param name="restoreAllowed">選択画面を閉じた後で、前面へ戻してよいか（<see cref="PasteTarget.RestoreFocus"/>）。</param>
    public static FocusAction Decide(bool foregroundIsTarget, bool foregroundIsOwnOrNone, bool restoreAllowed)
    {
        if (foregroundIsTarget) return FocusAction.Proceed;
        return restoreAllowed && foregroundIsOwnOrNone ? FocusAction.Restore : FocusAction.Abandon;
    }
}

public enum PasteStatus
{
    /// <summary>Ctrl+V 相当の入力を送った（貼り付けの完了ではない）。</summary>
    Sent,

    /// <summary>コピーのみ（貼り付け先が閉じた・前面にできない・管理者権限・キーが離されない等）。</summary>
    CopiedOnly,

    /// <summary>待っている間にクリップボードが変わったため送らなかった。</summary>
    ClipboardChanged,
}

public enum PasteSkipReason
{
    None,
    TargetGone,
    TargetElevated,
    FocusNotRestored,
    KeysHeld,
    SendFailed,
}

public readonly record struct PasteOutcome(PasteStatus Status, PasteSkipReason Reason = PasteSkipReason.None);

/// <summary>自動貼り付け（Win32 の実装はアプリ側）。</summary>
public interface IPastePort
{
    /// <param name="writtenSequence">合成結果を書き込んだ後の更新番号。送る直前に変わっていたら送らない。</param>
    Task<PasteOutcome> PasteAsync(PasteTarget target, uint writtenSequence);
}
