using System.Globalization;
using System.Text.RegularExpressions;
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

/// <summary>退避ファイルの種類（ファイル名の before-&lt;種類&gt;）。</summary>
public enum SideCopyKind
{
    /// <summary>インポート（全件置換・追加インポート）の前。</summary>
    Import,

    /// <summary>中身のあるカテゴリの削除の前。</summary>
    Delete,

    /// <summary>［以前の状態に戻す］の前。</summary>
    Restore,
}

/// <summary>退避ファイル 1 つ。<see cref="Stamp"/> はファイル名の日時、<see cref="Sequence"/> は同じ日時の番号（無ければ 1）。</summary>
public sealed record SideCopy(string Path, SideCopyKind Kind, DateTime Stamp, int Sequence);

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

    /// <summary>退避ファイル（before-import・before-delete・before-restore）を残す件数（合計。仕様書 §11）。</summary>
    public const int BackupKeep = 20;

    /// <summary>
    /// 退避ファイルの名前（厳密に一致するものだけを一覧・削除の対象にする。corrupt・.bak・似た名前は対象外）。
    /// 番号は先頭が 0 でない 2 以上の整数。
    /// </summary>
    private static readonly Regex SideCopyName = new(
        @"^templates\.before-(import|delete|restore)-(\d{8}-\d{6})(?:-([1-9]\d{0,8}))?\.json$", RegexOptions.CultureInvariant);

    private readonly Func<DateTime> _now;
    private readonly Action<string, Exception?>? _log;

    /// <param name="log">保存は続けられるが記録しておく問題（古い退避ファイルを消せない等）。本文を含めない。</param>
    public TemplateStore(string directory, Func<DateTime>? now = null, Action<string, Exception?>? log = null)
    {
        Directory = directory;
        _now = now ?? (() => DateTime.Now);
        _log = log;
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
    /// （インポート "before-import"・中身のあるカテゴリの削除 "before-delete"・以前の状態に戻す "before-restore"）。
    /// 保存する内容が上限を超える場合は写しも作らずに中止する。写し・保存のどちらかに失敗したら現在のデータを変えない。
    /// 保存に成功したら、<see cref="BackupKeep"/> 件を超えた古い退避ファイルを消す。
    /// </summary>
    public StoreResult SaveAfterCopy(TemplateLibrary library, string label)
    {
        if (SaveBlocked) return new(false, string.Format(Strings.SaveBlockedFormat, FilePath));
        var bytes = TemplateFileFormat.Serialize(library);
        if (bytes.LongLength > SafeFile.MaxBytes) return TooLarge(FilePath);
        string? copied = null;
        if (File.Exists(FilePath))
        {
            try
            {
                copied = SafeFile.CopyAside(FilePath, label, _now());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(false, string.Format(Strings.SaveFailedFormat, FilePath, ex.Message));
            }
        }
        var result = Write(FilePath, bytes, BackupPath);
        // 保存に成功した後だけ古い退避ファイルを消す（失敗を繰り返して古い写しが押し出されないように）。
        if (result.Succeeded) PruneSideCopies(copied);
        return result;
    }

    /// <summary>
    /// データのフォルダーの退避ファイルを、ファイル名の日時の新しい順（同じ日時なら番号の大きい順）に返す。読めなければ空（記録する）。
    /// ファイルの中身は読まない。
    /// </summary>
    public IReadOnlyList<SideCopy> ListSideCopies()
    {
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return [];
            files = System.IO.Directory.GetFiles(Directory, "templates.before-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not list the backup copies in {Directory}", ex);
            return [];
        }
        var copies = new List<SideCopy>();
        foreach (var file in files)
        {
            var match = SideCopyName.Match(Path.GetFileName(file));
            if (!match.Success) continue;
            if (!DateTime.TryParseExact(match.Groups[2].Value, SafeFile.StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) continue;
            var sequence = 1;
            if (match.Groups[3].Success && (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 2)) continue;
            var kind = match.Groups[1].Value switch
            {
                "import" => SideCopyKind.Import,
                "delete" => SideCopyKind.Delete,
                _ => SideCopyKind.Restore,
            };
            copies.Add(new SideCopy(file, kind, stamp, sequence));
        }
        return [.. copies.OrderByDescending(c => c.Stamp).ThenByDescending(c => c.Sequence)];
    }

    /// <summary>退避ファイルの内容を全件検証して返す（まだ置き換えない）。</summary>
    public ParsedLibrary ReadSideCopy(string path) => ReadFile(path);

    /// <summary>
    /// <see cref="BackupKeep"/> 件を超えた古い退避ファイルを消す。消せなくても保存の結果は変えない（記録する）。
    /// 今回作った写し（<paramref name="keep"/>）は、ファイル名の日時が既存のものより古くても（時計を戻した・別の PC のファイル）消さず、
    /// これを含めて上限に収まるよう、残りの古いものから消す。
    /// </summary>
    private void PruneSideCopies(string? keep)
    {
        var keepName = keep is null ? null : Path.GetFileName(keep);
        var others = ListSideCopies().Where(c => keepName is null || !string.Equals(Path.GetFileName(c.Path), keepName, StringComparison.OrdinalIgnoreCase));
        foreach (var old in others.Skip(keep is null ? BackupKeep : BackupKeep - 1))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not delete an old backup copy: {old.Path}", ex);
            }
        }
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
