using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Settings;

namespace ClipCompose.Core.Storage;

public sealed record SettingsLoad(LoadKind Kind, AppSettings Settings, string Problem, IReadOnlyList<string> Fixes);

/// <summary>
/// settings.json（PC ごとの設定。仕様書 §11）。テンプレートとは別のファイルなので、テンプレートが読めなくても使える。
/// 不正な JSON・新しい schemaVersion なら既定値で動き、元のファイルを残したまま保存を止める。個々の値の問題は補正する。
/// </summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = SettingsJsonContext.Default,
    };

    private static JsonTypeInfo<AppSettings> TypeInfo => (JsonTypeInfo<AppSettings>)Options.GetTypeInfo(typeof(AppSettings));

    public SettingsStore(string directory)
    {
        Directory = directory;
    }

    /// <summary>%LOCALAPPDATA%\ClipCompose（設定・テンプレート・ログ）。パッケージ版でないとき。</summary>
    public static string DefaultDirectory => DataDirectory(null);

    /// <summary>
    /// 設定・テンプレート・ログの保存先。MSIX パッケージ（Microsoft Store 版）として動くときは
    /// %LOCALAPPDATA% への書き込みがパッケージ専用の場所へ振り替えられ、エクスプローラーからは元のパスで見えないため、
    /// 実際の場所（%LOCALAPPDATA%\Packages\&lt;ファミリー名&gt;\LocalCache\Local\ClipCompose）を直接使う（PlainClip と同じ方式）。
    /// </summary>
    public static string DataDirectory(string? packageFamilyName, string? localAppData = null)
    {
        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(packageFamilyName)
            ? Path.Combine(local, "ClipCompose")
            : Path.Combine(local, "Packages", packageFamilyName, "LocalCache", "Local", "ClipCompose");
    }

    public string Directory { get; }

    public string FilePath => Path.Combine(Directory, FileName);

    public bool SaveBlocked { get; private set; }

    public SettingsLoad Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            SaveBlocked = false;
            return new(LoadKind.Missing, new AppSettings(), "", []);
        }
        string problem;
        try
        {
            var bytes = SafeFile.ReadLimited(path);
            using (var document = JsonDocument.Parse(bytes))
            {
                if (!TemplateFileFormat.TryGetSchemaVersion(document, out var version)) return Fail(Strings.ProblemNoSchemaVersion);
                if (version > AppSettings.CurrentSchemaVersion) return Fail(string.Format(Strings.ProblemNewerSchemaFormat, version));
            }
            var settings = JsonSerializer.Deserialize(bytes, TypeInfo) ?? new AppSettings();
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            var fixes = SettingsNormalizer.Normalize(settings);
            SaveBlocked = false;
            return new(LoadKind.Loaded, settings, "", fixes);
        }
        catch (JsonException ex)
        {
            problem = TemplateFileFormat.InvalidJson(ex);
        }
        catch (FileTooLargeException)
        {
            problem = string.Format(Strings.ProblemTooLargeFormat, SafeFile.MaxBytes / 1024 / 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }
        return Fail(problem);

        SettingsLoad Fail(string reason)
        {
            SaveBlocked = true;
            return new(LoadKind.Failed, new AppSettings(), string.Format(Strings.LoadFailedFormat, path, reason), []);
        }
    }

    /// <summary>読めない設定ファイルを退避して、既定値で保存し直す（利用者が選んだ場合だけ）。</summary>
    public StoreResult ResetMovingAside(AppSettings defaults)
    {
        try
        {
            if (File.Exists(FilePath)) SafeFile.MoveAside(FilePath, "corrupt", DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, string.Format(Strings.SaveFailedFormat, FilePath, ex.Message));
        }
        SaveBlocked = false;
        return Save(defaults);
    }

    public StoreResult Save(AppSettings settings)
    {
        if (SaveBlocked) return new(false, string.Format(Strings.SaveBlockedFormat, FilePath));
        try
        {
            SafeFile.WriteAtomic(FilePath, JsonSerializer.SerializeToUtf8Bytes(settings, TypeInfo), FilePath + ".bak");
            return StoreResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, string.Format(Strings.SaveFailedFormat, FilePath, ex.Message));
        }
    }
}
