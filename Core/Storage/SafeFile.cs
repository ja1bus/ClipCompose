namespace ClipCompose.Core.Storage;

/// <summary>ファイルが上限より大きい。</summary>
public sealed class FileTooLargeException(long length) : IOException($"The file is larger than the limit ({length} bytes).")
{
    public long Length { get; } = length;
}

/// <summary>
/// 保存ファイルの読み書き（仕様書 §11）。
/// - 読み込み・保存・インポートとも UTF-8 で 20 MiB まで。
/// - 保存は同じフォルダーの一時ファイルへ書いてディスクへフラッシュし、置き換える。直前のファイルは .bak として 1 世代残す。
/// </summary>
public static class SafeFile
{
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>上限までを読む。上限を超えるなら <see cref="FileTooLargeException"/>。先頭の BOM は除く。</summary>
    public static byte[] ReadLimited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new FileTooLargeException(stream.Length);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        // 読んでいる間に伸びた場合も、上限を超える分は読まない。
        if (stream.ReadByte() >= 0) throw new FileTooLargeException(stream.Length);
        return bytes.AsSpan().StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;
    }

    /// <summary>
    /// 一時ファイル経由で置き換える。<paramref name="backupPath"/> があれば、置き換え前のファイルをそこへ残す。
    /// 失敗した場合は元のファイルを変えない（一時ファイルは消す）。
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes, string? backupPath)
    {
        if (bytes.LongLength > MaxBytes) throw new FileTooLargeException(bytes.LongLength);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>壊れたファイルなどを、同じフォルダーへ日時付きの名前で退避する（削除しない）。退避先のパス。</summary>
    public static string MoveAside(string path, string label, DateTime now)
    {
        var target = SidePath(path, label, now);
        File.Move(path, target);
        return target;
    }

    /// <summary>同じフォルダーへ日時付きの名前で写しを作る。写しのパス。</summary>
    public static string CopyAside(string path, string label, DateTime now)
    {
        var target = SidePath(path, label, now);
        File.Copy(path, target);
        return target;
    }

    /// <summary>"templates.json" → "templates.corrupt-20261025-120000.json"（同名があれば "-2" 等を付ける）。</summary>
    private static string SidePath(string path, string label, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var target = Path.Combine(directory, $"{name}.{label}-{stamp}{extension}");
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(directory, $"{name}.{label}-{stamp}-{i}{extension}");
        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残った一時ファイルは次の保存で上書きされる。
        }
    }
}
