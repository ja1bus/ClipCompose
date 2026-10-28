using ClipCompose.Core.Settings;
#if MSIX
using Windows.ApplicationModel;
#endif

namespace ClipCompose.Services;

/// <summary>
/// Windows へのサインイン時の自動起動（仕様書 §3）。MSIX のマニフェスト（installer/msix/AppxManifest.xml）の
/// windows.startupTask "ClipComposeStartup" を StartupTask API で有効・無効にする。状態の正本は Windows で、settings.json には保存しない。
/// WinRT の API を使うため、MSIX 用のビルド（ClipComposeMsix=true。TargetFramework に Windows の版を付け、定数 MSIX を定義）だけで実装する
/// （PlainClip と同じ方式）。通常のビルド・パッケージでない実行では <see cref="StartupState.Unsupported"/> を返し、Windows に触れない。
/// </summary>
public static class StartupService
{
    /// <summary>マニフェストの uap5:StartupTask の TaskId と同じ値。</summary>
    public const string StartupTaskId = "ClipComposeStartup";

    /// <summary>Windows の［スタートアップ アプリ］の設定ページ。</summary>
    public const string WindowsSettingsUri = "ms-settings:startupapps";

#if MSIX
    public static Task<StartupState> GetStateAsync() => Run("Failed to read the startup task state", task => Task.FromResult(task.State));

    /// <summary>
    /// 有効化を求める・無効にする。Windows 側で無効にされている（DisabledByUser）・ポリシーで決まっている場合は変えられず、その状態を返す。
    /// </summary>
    public static Task<StartupState> SetEnabledAsync(bool enabled) => Run("Failed to change the startup task", async task =>
    {
        if (enabled) return await task.RequestEnableAsync();
        if (task.State is not StartupTaskState.Enabled) return task.State;
        task.Disable();
        // 結果は Windows から読み直す（手元のオブジェクトの状態に頼らない）。
        return (await StartupTask.GetAsync(StartupTaskId)).State;
    });

    // StartupTask の非同期 API を UI スレッドで待つと止まることがあるため（PlainClip で確認）、スレッドプールで実行する。
    private static Task<StartupState> Run(string failure, Func<StartupTask, Task<StartupTaskState>> action)
    {
        if (!PackageInfo.IsPackaged) return Task.FromResult(StartupState.Unsupported);
        return Task.Run(async () =>
        {
            try
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                return Map(await action(task));
            }
            catch (Exception ex)
            {
                ErrorLog.Write(failure, ex);
                return StartupState.Unknown;
            }
        });
    }

    private static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => StartupState.Enabled,
        StartupTaskState.Disabled => StartupState.Disabled,
        StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupState.EnabledByPolicy,
        _ => StartupState.Unknown,
    };
#else
    public static Task<StartupState> GetStateAsync() => Task.FromResult(StartupState.Unsupported);

    public static Task<StartupState> SetEnabledAsync(bool enabled) => Task.FromResult(StartupState.Unsupported);
#endif
}
