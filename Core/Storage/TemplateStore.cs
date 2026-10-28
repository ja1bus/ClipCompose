using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;

namespace ClipCompose.Core.Storage;

public enum LoadKind
{
    /// <summary>ファイルが無い（初回）。</summary>
    Missing,
    Loaded,
    /// <summary>ファイルはあるが読めない・内容が不正。</summary>
    Failed,
}

public sealed record TemplateLoad(LoadKind Kind, TemplateLibrary Library, string Problem);

/// <summary>保存の結果。失敗の理由はフルパスを含む表示用の文字列。</summary>
public readonly record struct StoreResult(bool Succeeded, string Problem)
{
    public static readonly StoreResult Ok = new(true, "");
}

/// <summary>
/// templates.json の読み込み・保存・復旧・インポート（仕様書 §11）。
/// 読み込みに失敗したら元のファイルを残したまま通常の保存を止める（<see cref="SaveBlocked"/>）。
/// 復旧は利用者が選んだ場合だけ行い、壊れたファイルは退避して残す。
/// </summary>
public sealed class TemplateStore
{
    public const string FileName = "templates.json";

    private readonly Func<DateTime> _now;

    public TemplateStore(string directory, Func<DateTime>? now = null)
    {
        Directory = directory;
        _now = now ?? (() => DateTime.Now);
    }

    public string Directory { get; }

    public string FilePath => Path.Combine(Directory, FileName);

    public string BackupPath => FilePath + ".bak";

    /// <summary>読み込みに失敗したため、通常の保存を止めているか。</summary>
    public bool SaveBlocked { get; private set; }

    public TemplateLoad Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            SaveBlocked = false;
            return new(LoadKind.Missing, TemplateLibrary.Empty, "");
        }
        var parsed = ReadFile(path);
        SaveBlocked = !parsed.Succeeded;
        return parsed.Succeeded
            ? new(LoadKind.Loaded, parsed.Library!, "")
            : new(LoadKind.Failed, TemplateLibrary.Empty, parsed.Problem);
    }

    public StoreResult Save(TemplateLibrary library)
    {
        if (SaveBlocked) return new(false, string.Format(Strings.SaveBlockedFormat, FilePath));
        return Write(FilePath, library, BackupPath);
    }

    /// <summary>バックアップの内容を全件検証して返す（まだ置き換えない）。</summary>
    public ParsedLibrary ReadBackup() =>
        File.Exists(BackupPath) ? ReadFile(BackupPath) : new(null, string.Format(Strings.BackupMissingFormat, BackupPath));

    /// <summary>
    /// 検証済みのバックアップの内容で復旧する（利用者が選んだ場合だけ）。壊れたファイルは退避して残す。
    /// </summary>
    public StoreResult RestoreFrom(TemplateLibrary validated) => Recover(validated);

    /// <summary>壊れたファイルを退避し、空の状態で始める（利用者が選んだ場合だけ）。</summary>
    public StoreResult StartEmpty() => Recover(TemplateLibrary.Empty);

    /// <summary>エクスポート（カテゴリ・テンプレートだけ。PC ごとの設定は含めない）。</summary>
    public StoreResult Export(string path, TemplateLibrary library) => Write(path, library, null);

    /// <summary>インポートするファイルを全件検証する（まだ置き換えない）。</summary>
    public ParsedLibrary ReadImport(string path) => ReadFile(path);

    /// <summary>
    /// 現在のデータを日時付きの名前で写してから、全件置換する。写し・保存のどちらかに失敗したら現在のデータを変えない。
    /// </summary>
    public StoreResult ReplaceWithImport(TemplateLibrary validated) => SaveAfterCopy(validated, "before-import");

    /// <summary>
    /// 現在のデータを日時付きの名前（templates.&lt;label&gt;-yyyyMMdd-HHmmss.json）で写してから保存する
    /// （インポート "before-import"・中身のあるカテゴリの削除 "before-delete"）。保存する内容が上限を超える場合は写しも作らずに中止する。
    /// 写し・保存のどちらかに失敗したら現在のデータを変えない。
    /// </summary>
    public StoreResult SaveAfterCopy(TemplateLibrary library, string label)
    {
        if (SaveBlocked) return new(false, string.Format(Strings.SaveBlockedFormat, FilePath));
        var bytes = TemplateFileFormat.Serialize(library);
        if (bytes.LongLength > SafeFile.MaxBytes) return TooLarge(FilePath);
        if (File.Exists(FilePath))
        {
            try
            {
                SafeFile.CopyAside(FilePath, label, _now());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(false, string.Format(Strings.SaveFailedFormat, FilePath, ex.Message));
            }
        }
        return Write(FilePath, bytes, BackupPath);
    }

    private StoreResult Recover(TemplateLibrary library)
    {
        try
        {
            if (File.Exists(FilePath)) SafeFile.MoveAside(FilePath, "corrupt", _now());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, string.Format(Strings.SaveFailedFormat, FilePath, ex.Message));
        }
        // 壊れたファイルを退避した後は、通常の保存（.bak は壊れたファイルの前の正常なもののまま）で書く。
        var result = Write(FilePath, library, null);
        if (result.Succeeded) SaveBlocked = false;
        return result;
    }

    private static ParsedLibrary ReadFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = SafeFile.ReadLimited(path);
        }
        catch (FileTooLargeException)
        {
            return new(null, string.Format(Strings.LoadFailedFormat, path, string.Format(Strings.ProblemTooLargeFormat, SafeFile.MaxBytes / 1024 / 1024)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, string.Format(Strings.LoadFailedFormat, path, ex.Message));
        }
        var parsed = TemplateFileFormat.Parse(bytes);
        return parsed.Succeeded ? parsed : parsed with { Problem = string.Format(Strings.LoadFailedFormat, path, parsed.Problem) };
    }

    private static StoreResult Write(string path, TemplateLibrary library, string? backupPath) =>
        Write(path, TemplateFileFormat.Serialize(library), backupPath);

    private static StoreResult Write(string path, byte[] bytes, string? backupPath)
    {
        try
        {
            SafeFile.WriteAtomic(path, bytes, backupPath);
            return StoreResult.Ok;
        }
        catch (FileTooLargeException)
        {
            return TooLarge(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, string.Format(Strings.SaveFailedFormat, path, ex.Message));
        }
    }

    private static StoreResult TooLarge(string path) =>
        new(false, string.Format(Strings.SaveFailedFormat, path, string.Format(Strings.ProblemTooLargeFormat, SafeFile.MaxBytes / 1024 / 1024)));
}
