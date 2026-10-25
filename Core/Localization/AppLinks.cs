namespace ClipCompose.Core.Localization;

/// <summary>
/// 設定画面の［バージョン情報］から開く Web・公開リポジトリの URL。Web は表示言語に合わせる（日本語以外は英語のページ）。
/// URL は Web サイト（www.ja1bus.com/&lt;app&gt;/）と公開リポジトリの規約に従う。
/// </summary>
public static class AppLinks
{
    private const string Site = "https://www.ja1bus.com/clipcompose/";

    public const string Source = "https://github.com/ja1bus/ClipCompose";

    public static string Manual(string language) => Page(language, "manual/");

    public static string Privacy(string language) => Page(language, "privacy/");

    private static string Page(string language, string path) =>
        language == UiLanguage.Japanese ? Site + path : Site + "en/" + path;
}
