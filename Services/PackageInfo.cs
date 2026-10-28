using ClipCompose.Core.Storage;
using static ClipCompose.Interop.NativeMethods;

namespace ClipCompose.Services;

/// <summary>
/// MSIX パッケージ（Microsoft Store 版）として動いているか（PlainClip と同じ方式）。
/// パッケージ版は %LOCALAPPDATA% への書き込みが振り替えられるため、データの保存先を実際の場所にする。
/// </summary>
public static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>パッケージのファミリー名。パッケージ版でなければ null。</summary>
    public static string? FamilyName { get; } = IsPackaged ? ReadFamilyName() : null;

    /// <summary>設定・テンプレート・ログの保存先（エクスプローラーで開ける実際の場所）。</summary>
    public static string DataDirectory { get; } = DebugDataDirectory() ?? SettingsStore.DataDirectory(FamilyName);

    /// <summary>
    /// Debug ビルドだけ: 環境変数 CLIPCOMPOSE_DATA_DIR で保存先を変える（試験で利用者のテンプレート・設定を上書きしないため）。
    /// Release ビルド（配布物）では使わない。
    /// </summary>
    private static string? DebugDataDirectory()
    {
#if DEBUG
        var directory = Environment.GetEnvironmentVariable("CLIPCOMPOSE_DATA_DIR");
        return string.IsNullOrWhiteSpace(directory) ? null : System.IO.Path.GetFullPath(directory);
#else
        return null;
#endif
    }

    private static unsafe bool DetectPackaged()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static unsafe string? ReadFamilyName()
    {
        try
        {
            var length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer || length <= 0) return null;
            var buffer = new char[length];
            fixed (char* p = buffer)
            {
                // length は終端の NUL を含む文字数。
                return GetCurrentPackageFamilyName(ref length, p) == 0 ? new string(buffer, 0, length - 1) : null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

}
