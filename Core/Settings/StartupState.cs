using ClipCompose.Core.Localization;

namespace ClipCompose.Core.Settings;

/// <summary>
/// サインイン時の自動起動の状態（仕様書 §3）。正本は Windows（MSIX の StartupTask）で、settings.json には保存しない。
/// Windows の StartupTaskState に、取得できない場合（未対応・失敗）を加えたもの。
/// </summary>
public enum StartupState
{
    /// <summary>パッケージ版（Microsoft Store 版）でないため使えない（開発中の実行など）。</summary>
    Unsupported,
    /// <summary>状態を取得できなかった。</summary>
    Unknown,
    Disabled,
    Enabled,
    /// <summary>Windows の設定（スタートアップ アプリ・タスク マネージャー）で利用者が無効にした。アプリからは有効にできない。</summary>
    DisabledByUser,
    DisabledByPolicy,
    EnabledByPolicy,
}

/// <summary>設定画面での表し方。</summary>
/// <param name="Checked">チェックを付けて表示するか。</param>
/// <param name="CanChange">チェックを操作できるか。</param>
/// <param name="ShowWindowsSettings">Windows の［スタートアップ アプリ］を開く操作を出すか。</param>
public readonly record struct StartupView(bool Checked, bool CanChange, bool ShowWindowsSettings, string Status);

public static class StartupStates
{
    public static bool IsOn(StartupState state) => state is StartupState.Enabled or StartupState.EnabledByPolicy;

    /// <summary>状態ごとの表示。取得失敗・ポリシー・未対応を「無効」と同じに見せない。</summary>
    public static StartupView View(StartupState state) => state switch
    {
        StartupState.Enabled => new(true, true, false, Strings.StartupEnabled),
        StartupState.Disabled => new(false, true, false, ""),
        StartupState.DisabledByUser => new(false, false, true, Strings.StartupDisabledByUser),
        StartupState.DisabledByPolicy => new(false, false, false, Strings.StartupDisabledByPolicy),
        StartupState.EnabledByPolicy => new(true, false, false, Strings.StartupEnabledByPolicy),
        StartupState.Unsupported => new(false, false, false, Strings.StartupUnsupported),
        _ => new(false, false, true, Strings.StartupUnknown),
    };
}
