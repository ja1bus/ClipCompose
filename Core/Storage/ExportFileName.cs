using System.Text;
using ClipCompose.Core.Text;

namespace ClipCompose.Core.Storage;

/// <summary>エクスポートの保存先（仕様書 §11）。初期のファイル名と、アプリが管理するフォルダーへの保存の拒否。</summary>
public static class ExportFileName
{
    private const int MaxNameScalars = 60;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 1 カテゴリなら「ClipCompose-カテゴリ名.json」、複数なら「ClipCompose-templates-yyyyMMdd.json」。
    /// ファイル名に使えない文字は「_」にし、長い名前は短くする。
    /// </summary>
    public static string Suggest(IReadOnlyList<string> categoryNames, DateTime now)
    {
        if (categoryNames.Count == 1 && Clean(categoryNames[0]) is { Length: > 0 } name) return $"ClipCompose-{name}.json";
        return $"ClipCompose-templates-{now:yyyyMMdd}.json";
    }

    private static string Clean(string name)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in name.Trim().EnumerateRunes())
        {
            if (count++ == MaxNameScalars) break;
            builder.Append(rune.Value < 0x20 || (rune.IsBmp && @"<>:""/\|?*".Contains((char)rune.Value)) ? "_" : rune.ToString());
        }
        // 末尾の空白・ピリオドは Windows で消えるため除く。
        var cleaned = builder.ToString().TrimEnd(' ', '.');
        if (TextMetrics.HasLoneSurrogate(cleaned)) return "";
        return ReservedNames.Contains(cleaned) ? "_" + cleaned : cleaned;
    }

    /// <summary>
    /// アプリのデータのフォルダー（templates.json・settings.json・バックアップ・ログの置き場所）の中か。
    /// エクスポートで管理中のファイルを上書きしないよう、このフォルダーへの保存は断る。
    /// </summary>
    public static bool IsInDataDirectory(string path, string dataDirectory)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null) return false;
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        return string.Equals(Path.TrimEndingDirectorySeparator(folder), data, StringComparison.OrdinalIgnoreCase);
    }
}
