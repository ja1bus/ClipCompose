using System.IO;
using System.Text;
using ClipCompose.Core.Storage;

namespace ClipCompose.Services;

/// <summary>
/// データの保存先（<see cref="PackageInfo.DataDirectory"/>）の error.log への記録。記録の文言は表示言語によらず英語。
/// コピーした文章・合成結果・テンプレートの本文は書かない（仕様書 §7.2）。
/// </summary>
public static class ErrorLog
{
    private static readonly Lock Gate = new();

    public static string FilePath => Path.Combine(PackageInfo.DataDirectory, "error.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(PackageInfo.DataDirectory);
                var text = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').AppendLine(message);
                if (exception is not null) text.AppendLine(exception.ToString());
                File.AppendAllText(FilePath, text.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくても本体の動作は続ける。
        }
    }
}
